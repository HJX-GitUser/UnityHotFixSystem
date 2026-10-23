using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 文件读写与目录约定的唯一实现。
    ///
    /// 约定：
    /// 1. 所有写入都是"先写临时文件再改名"，避免进程被杀后留下半截文件；
    /// 2. 已生效的内容永远不被就地覆盖（版本化目录 + 指针）；
    /// 3. StreamingAssets 一律按只读处理，必要时走 UnityWebRequest。
    /// </summary>
    public sealed class StorageService
    {
        public void EnsureDirectories()
        {
            Directory.CreateDirectory(HotfixPaths.Root);
            Directory.CreateDirectory(HotfixPaths.LuaRoot);
            Directory.CreateDirectory(HotfixPaths.TempRoot);
        }

        /// <summary>启动时清空临时区：上次残留的半截下载没有任何价值。</summary>
        public void CleanTemp()
        {
            DeleteDirectorySafe(HotfixPaths.TempRoot);
            Directory.CreateDirectory(HotfixPaths.TempRoot);
        }

        public bool Exists(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public long GetFileSize(string path)
        {
            try
            {
                if (!Exists(path)) return -1;
                return new FileInfo(path).Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public bool TryReadText(string path, out string text)
        {
            text = null;
            try
            {
                if (!Exists(path)) return false;
                text = File.ReadAllText(path, Encoding.UTF8);
                return !string.IsNullOrEmpty(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 读取失败: " + path + " -> " + e.Message);
                return false;
            }
        }

        /// <summary>写入文本：临时文件 → flush 到磁盘 → 替换目标。</summary>
        public void WriteTextAtomic(string path, string content)
        {
            EnsureParentDirectory(path);

            string tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }

            // 目标已存在时 File.Move 会失败；窗口期极短，且 state.json 会兜住这个中间态。
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }

        /// <summary>写入二进制：与文本写入同样走"临时文件 + 改名"。</summary>
        public void WriteBytesAtomic(string path, byte[] content)
        {
            if (content == null) throw new ArgumentNullException("content");

            EnsureParentDirectory(path);

            string tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }

        public void MoveFileReplace(string sourcePath, string targetPath)        {
            if (!Exists(sourcePath)) throw new FileNotFoundException("源文件不存在: " + sourcePath);

            EnsureParentDirectory(targetPath);
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(sourcePath, targetPath);
        }

        public void CopyFileReplace(string sourcePath, string targetPath)
        {
            if (!Exists(sourcePath)) throw new FileNotFoundException("源文件不存在: " + sourcePath);

            EnsureParentDirectory(targetPath);
            string tempPath = targetPath + ".tmp";
            File.Copy(sourcePath, tempPath, true);
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempPath, targetPath);
        }

        public void DeleteFileSafe(string path)
        {
            try
            {
                if (Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 删除文件失败: " + path + " -> " + e.Message);
            }
        }

        public void DeleteDirectorySafe(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                Directory.Delete(path, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 删除目录失败: " + path + " -> " + e.Message);
            }
        }

        /// <summary>可用空间（字节）。无法判断时返回 -1，调用方应跳过空间预检。</summary>
        public long GetAvailableBytes()
        {
            try
            {
                string fullPath = Path.GetFullPath(Application.persistentDataPath);
                string driveRoot = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(driveRoot)) return -1;
                return new DriveInfo(driveRoot).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// 读取包内文件（Android 上 StreamingAssets 在 apk 内，必须走 UnityWebRequest）。
        /// </summary>
        public IEnumerator ReadBundledText(string absolutePath, Action<string> onSuccess, Action<string> onError)
        {
            string url = ToFileUrl(absolutePath);
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 15;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (onError != null) onError(request.error + " (" + url + ")");
                    yield break;
                }

                if (onSuccess != null) onSuccess(request.downloadHandler.text);
            }
        }

        /// <summary>读取包内二进制文件（用于释放出厂基线）。</summary>
        public IEnumerator ReadBundledBytes(string absolutePath, Action<byte[]> onSuccess, Action<string> onError)
        {
            string url = ToFileUrl(absolutePath);
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 120;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (onError != null) onError(request.error + " (" + url + ")");
                    yield break;
                }

                if (onSuccess != null) onSuccess(request.downloadHandler.data);
            }
        }

        public static string ToFileUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.IndexOf("://", StringComparison.Ordinal) >= 0) return path;

            string normalized = path.Replace('\\', '/');
            // Unix 绝对路径 "/a/b" -> file:///a/b；Windows "C:/a/b" -> file:///C:/a/b
            return normalized.StartsWith("/", StringComparison.Ordinal)
                ? "file://" + normalized
                : "file:///" + normalized;
        }

        public void EnsureParentDirectory(string path)
        {
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        }
    }
}
