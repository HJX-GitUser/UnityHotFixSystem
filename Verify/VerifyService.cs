using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 完整性校验：大小 + SHA256。
    ///
    /// 资源包的 CRC 由 Addressables 内建能力负责，这里只处理自带 Hash 的文件（Lua 包）。
    /// 大文件按块读取并定期让出帧，避免卡住主线程。
    /// 注意：C# 不允许在带 catch 的 try 里 yield，因此错误先记录再统一处理。
    /// </summary>
    public sealed class VerifyService
    {
        private const int ChunkSize = 1024 * 1024;
        private const int ChunksPerFrame = 4;

        public IEnumerator VerifyFile(string path, long expectedSize, string expectedHash, Action<bool, string> onDone)
        {
            if (!File.Exists(path))
            {
                Complete(onDone, false, "文件不存在: " + path);
                yield break;
            }

            long actualSize;
            try
            {
                actualSize = new FileInfo(path).Length;
            }
            catch (Exception e)
            {
                Complete(onDone, false, "读取文件大小失败: " + e.Message);
                yield break;
            }

            if (expectedSize > 0 && actualSize != expectedSize)
            {
                Complete(onDone, false, "大小不符：期望 " + expectedSize + "，实际 " + actualSize);
                yield break;
            }

            if (string.IsNullOrEmpty(expectedHash))
            {
                // 没有 Hash 时只能依赖大小，属于降级校验
                Complete(onDone, true, null);
                yield break;
            }

            string actualHash = null;
            string error = null;
            yield return ComputeSha256(path, h => actualHash = h, e => error = e);

            if (!string.IsNullOrEmpty(error))
            {
                Complete(onDone, false, "计算 Hash 失败: " + error);
                yield break;
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Complete(onDone, false, "Hash 不符：期望 " + expectedHash + "，实际 " + actualHash);
                yield break;
            }

            Complete(onDone, true, null);
        }

        public IEnumerator ComputeSha256(string path, Action<string> onSuccess, Action<string> onError)
        {
            SHA256 sha = null;
            FileStream stream = null;
            string openError = null;

            try
            {
                sha = SHA256.Create();
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize);
            }
            catch (Exception e)
            {
                openError = e.Message;
            }

            if (openError != null)
            {
                if (stream != null) stream.Dispose();
                if (sha != null) sha.Dispose();
                if (onError != null) onError(openError);
                yield break;
            }

            var buffer = new byte[ChunkSize];
            int chunksSinceYield = 0;
            string readError = null;

            // 外层 try 只有 finally，允许在里面 yield
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception e)
                    {
                        readError = e.Message;
                        break;
                    }

                    if (read <= 0) break;

                    sha.TransformBlock(buffer, 0, read, null, 0);

                    if (++chunksSinceYield >= ChunksPerFrame)
                    {
                        chunksSinceYield = 0;
                        yield return null;
                    }
                }
            }
            finally
            {
                stream.Dispose();
            }

            if (!string.IsNullOrEmpty(readError))
            {
                sha.Dispose();
                if (onError != null) onError(readError);
                yield break;
            }

            string hex = null;
            string finalError = null;
            try
            {
                sha.TransformFinalBlock(buffer, 0, 0);
                hex = ToHex(sha.Hash);
            }
            catch (Exception e)
            {
                finalError = e.Message;
            }
            finally
            {
                sha.Dispose();
            }

            if (finalError != null)
            {
                if (onError != null) onError(finalError);
                yield break;
            }

            if (onSuccess != null) onSuccess(hex);
        }

        /// <summary>同步版本，供编辑器工具使用。</summary>
        public static string ComputeSha256Hex(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;

            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }
            return builder.ToString();
        }

        private static void Complete(Action<bool, string> onDone, bool success, string error)
        {
            if (onDone != null) onDone(success, error);
        }
    }
}
