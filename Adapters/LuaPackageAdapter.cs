using System;
using System.Collections;
using System.IO;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// Lua 包通道。
    ///
    /// 只负责"Lua 包这件东西"的落盘与激活，不做版本决策：
    /// 下载 / 校验 / 安装三步刻意拆开，让编排层能在正确的时机推进事务状态。
    /// </summary>
    public sealed class LuaPackageAdapter
    {
        private readonly StorageService _storage;
        private readonly DownloadService _downloader;
        private readonly VerifyService _verifier;

        public LuaPackageAdapter(StorageService storage, DownloadService downloader, VerifyService verifier)
        {
            _storage = storage;
            _downloader = downloader;
            _verifier = verifier;
        }

        public string TempPackagePath
        {
            get { return Path.Combine(HotfixPaths.TempRoot, HotfixPaths.LuaBundleFileName); }
        }

        public IEnumerator Download(ScriptSection script, Action<long, long> onProgress, Action<bool, string> onDone)
        {
            var request = new DownloadService.DownloadRequest
            {
                Url = script.packageUrl,
                TargetPath = TempPackagePath,
                ExpectedSize = script.size
            };

            DownloadService.DownloadOutcome outcome = null;
            yield return _downloader.Download(request, onProgress, o => outcome = o);

            if (outcome == null || !outcome.Success)
            {
                Complete(onDone, false, outcome != null ? outcome.Error : "下载未返回结果");
                yield break;
            }

            Complete(onDone, true, null);
        }

        public IEnumerator Verify(ScriptSection script, Action<bool, string> onDone)
        {
            string path = TempPackagePath;
            yield return _verifier.VerifyFile(path, script.size, script.packageHash, (success, error) =>
            {
                // 校验失败的文件没有任何价值，删掉避免下次误用
                if (!success) _storage.DeleteFileSafe(path);
                Complete(onDone, success, error);
            });
        }

        /// <summary>把校验通过的临时文件移动到版本目录。同盘移动，等价于改名。</summary>
        public bool Place(ScriptSection script, out string error)
        {
            error = null;
            string target = HotfixPaths.LuaBundleInVersionDirectory(script.version);

            try
            {
                _storage.MoveFileReplace(TempPackagePath, target);
                return true;
            }
            catch (Exception e)
            {
                error = "移动 Lua 包到版本目录失败: " + e.Message;
                return false;
            }
        }

        /// <summary>提交步骤：写指针。指针只是诊断与兜底，权威版本仍来自清单。</summary>
        public void Activate(string version)
        {
            if (string.IsNullOrEmpty(version)) return;
            _storage.WriteTextAtomic(HotfixPaths.LuaPointerFile, version);
        }

        /// <summary>
        /// 解析本次启动应使用的 Lua 目录。
        /// 返回 null 表示没有可用的热更包（首次启动 / 已回退到内置），调用方沿用默认来源。
        /// </summary>
        public string ResolveActiveRoot(HotfixManifest manifest)
        {
            if (manifest != null && manifest.scripts != null && !string.IsNullOrEmpty(manifest.scripts.version))
            {
                string versionRoot = HotfixPaths.LuaVersionDirectory(manifest.scripts.version);
                if (_storage.Exists(Path.Combine(versionRoot, HotfixPaths.LuaBundleFileName))) return versionRoot;
            }

            string pointerVersion;
            if (_storage.TryReadText(HotfixPaths.LuaPointerFile, out pointerVersion))
            {
                pointerVersion = pointerVersion.Trim();
                if (!string.IsNullOrEmpty(pointerVersion))
                {
                    string pointerRoot = HotfixPaths.LuaVersionDirectory(pointerVersion);
                    if (_storage.Exists(Path.Combine(pointerRoot, HotfixPaths.LuaBundleFileName))) return pointerRoot;
                }
            }

            return null;
        }

        /// <summary>
        /// 首次启动把包内出厂 Lua 包释放到可写目录。
        /// 这样"出厂基线"与"热更版本"共用同一条加载路径（版本目录 + bundle），回滚逻辑也只有一套。
        /// </summary>
        public IEnumerator EnsureBundledBaseline(HotfixManifest bundledManifest, Action<string> onDone)
        {
            string existing = ResolveActiveRoot(bundledManifest);
            if (existing != null)
            {
                CompleteRoot(onDone, existing);
                yield break;
            }

            if (bundledManifest == null || bundledManifest.scripts == null ||
                string.IsNullOrEmpty(bundledManifest.scripts.version))
            {
                CompleteRoot(onDone, null);
                yield break;
            }

            byte[] bytes = null;
            string error = null;
            yield return _storage.ReadBundledBytes(HotfixPaths.BundledLuaBundleFile, b => bytes = b, e => error = e);

            if (bytes == null || bytes.Length == 0)
            {
                UnityEngine.Debug.LogWarning("[Hotfix] 出厂 Lua 包不可用: " + error);
                CompleteRoot(onDone, null);
                yield break;
            }

            string targetPath = HotfixPaths.LuaBundleInVersionDirectory(bundledManifest.scripts.version);
            try
            {
                _storage.WriteBytesAtomic(targetPath, bytes);
                _storage.WriteTextAtomic(HotfixPaths.LuaPointerFile, bundledManifest.scripts.version);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Hotfix] 释放出厂 Lua 包失败: " + e.Message);
                CompleteRoot(onDone, null);
                yield break;
            }

            CompleteRoot(onDone, HotfixPaths.LuaVersionDirectory(bundledManifest.scripts.version));
        }

        private static void Complete(Action<bool, string> onDone, bool success, string error)
        {
            if (onDone != null) onDone(success, error);
        }

        private static void CompleteRoot(Action<string> onDone, string root)
        {
            if (onDone != null) onDone(root);
        }
    }
}
