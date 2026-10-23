using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 通用下载器：断点续传 + 超时 + 指数退避重试。
    ///
    /// 它不知道内容语义（Lua 包还是资源），只管"把 URL 变成磁盘上的完整文件"。
    /// 完整性校验由 VerifyService 负责，两者刻意分开。
    /// </summary>
    public sealed class DownloadService
    {
        private readonly StorageService _storage;
        private readonly HotfixConfig _config;

        public DownloadService(StorageService storage, HotfixConfig config)
        {
            _storage = storage;
            _config = config;
        }

        public sealed class DownloadRequest
        {
            public string Url;
            public string TargetPath;

            /// <summary>期望大小；&lt;= 0 表示未知，此时只能校验 Hash。</summary>
            public long ExpectedSize;
        }

        public sealed class DownloadOutcome
        {
            public bool Success;
            public string Error;
            public long BytesWritten;
            public int Attempts;
        }

        /// <summary>
        /// 下载单个文件。文件可能已存在（上次中断），此时用 Range 续传。
        /// </summary>
        /// <param name="onProgress">(已下载字节, 总字节)，总字节未知时为 0。</param>
        public IEnumerator Download(DownloadRequest request, Action<long, long> onProgress, Action<DownloadOutcome> onDone)
        {
            if (request == null || string.IsNullOrEmpty(request.Url) || string.IsNullOrEmpty(request.TargetPath))
            {
                Complete(onDone, Fail("下载请求参数不完整", 0));
                yield break;
            }

            try
            {
                _storage.EnsureParentDirectory(request.TargetPath);
            }
            catch (Exception e)
            {
                Complete(onDone, Fail("创建下载目录失败: " + e.Message, 0));
                yield break;
            }

            int maxAttempts = Mathf.Max(1, _config.maxRetries + 1);
            long expected = request.ExpectedSize;
            string lastError = null;
            bool restartFromScratch = false;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                long existing = _storage.GetFileSize(request.TargetPath);
                if (existing < 0) existing = 0;

                // 已经和期望大小一致：直接进入校验环节，不浪费流量
                if (expected > 0 && existing == expected)
                {
                    Complete(onDone, Success(existing, attempt));
                    yield break;
                }

                if (existing > 0 && (restartFromScratch || (expected > 0 && existing > expected)))
                {
                    _storage.DeleteFileSafe(request.TargetPath);
                    existing = 0;
                    restartFromScratch = false;
                }

                bool resumed = existing > 0;
                int responseCode = 0;
                string error = null;

                using (var webRequest = UnityWebRequest.Get(request.Url))
                {
                    webRequest.timeout = Mathf.Max(10, _config.timeoutSeconds);

                    // 失败时保留已下载内容，下次才能续传
                    var handler = new DownloadHandlerFile(request.TargetPath, resumed);
                    handler.removeFileOnAbort = false;
                    webRequest.downloadHandler = handler;

                    if (resumed)
                    {
                        webRequest.SetRequestHeader("Range", "bytes=" + existing + "-");
                    }

                    UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (onProgress != null)
                        {
                            onProgress(existing + (long)webRequest.downloadedBytes, expected);
                        }
                        yield return null;
                    }

                    responseCode = (int)webRequest.responseCode;
                    if (webRequest.result != UnityWebRequest.Result.Success)
                    {
                        error = webRequest.error ?? ("HTTP " + responseCode);
                    }
                }

                // 服务器忽略 Range 回了 200：追加写会把整包拼在半截文件后面，必须作废重下
                if (resumed && responseCode == 200)
                {
                    lastError = "服务器未返回 206（忽略 Range），改为完整下载";
                    restartFromScratch = true;
                    continue;
                }

                if (resumed && responseCode == 416)
                {
                    lastError = "Range 请求无效（416），改为完整下载";
                    restartFromScratch = true;
                    continue;
                }

                long finalSize = _storage.GetFileSize(request.TargetPath);
                if (finalSize < 0) finalSize = 0;

                if (error == null && expected > 0 && finalSize != expected)
                {
                    error = "文件大小不符：期望 " + expected + "，实际 " + finalSize;
                }

                if (error == null)
                {
                    if (onProgress != null) onProgress(finalSize, expected);
                    Complete(onDone, Success(finalSize, attempt));
                    yield break;
                }

                lastError = error;
                Debug.LogWarning("[Hotfix] 下载失败（第 " + attempt + "/" + maxAttempts + " 次）: " + error + " <- " + request.Url);

                if (attempt < maxAttempts)
                {
                    float delay = Mathf.Min(8f, Mathf.Pow(2f, attempt - 1));
                    yield return new WaitForSecondsRealtime(delay);
                }
            }

            Complete(onDone, Fail(lastError, maxAttempts));
        }

        private static DownloadOutcome Success(long bytes, int attempts)
        {
            return new DownloadOutcome { Success = true, BytesWritten = bytes, Attempts = attempts };
        }

        private static DownloadOutcome Fail(string error, int attempts)
        {
            return new DownloadOutcome { Success = false, Error = error ?? "下载失败", Attempts = attempts };
        }

        private static void Complete(Action<DownloadOutcome> onDone, DownloadOutcome outcome)
        {
            if (onDone != null) onDone(outcome);
        }
    }
}
