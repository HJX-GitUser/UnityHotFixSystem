using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 版本服务：回答"当前是什么版本"和"要不要更新"。
    ///
    /// 职责边界：
    /// - 只读清单、只做决策，不下载内容、不落盘业务文件；
    /// - 远端不可达时必须能降级到本地/内置清单，绝不能让玩家卡住。
    /// </summary>
    public sealed class ManifestService
    {
        private readonly StorageService _storage;
        private readonly HotfixConfig _config;

        public ManifestService(StorageService storage, HotfixConfig config)
        {
            _storage = storage;
            _config = config;
        }

        /// <summary>当前生效清单（本地优先，其次内置）。首次启动可能为 null。</summary>
        public HotfixManifest Applied { get; private set; }

        /// <summary>生效清单是否来自 StreamingAssets 内置基线。</summary>
        public bool AppliedFromBundled { get; private set; }

        public int LocalContentVersion
        {
            get { return Applied != null ? Applied.contentVersion : 0; }
        }

        /// <summary>
        /// 读取当前生效清单：持久化目录 → 内置基线。
        /// </summary>
        public IEnumerator LoadApplied(Action<HotfixManifest> onDone)
        {
            Applied = null;
            AppliedFromBundled = false;

            string localText;
            if (_storage.TryReadText(HotfixPaths.ManifestFile, out localText))
            {
                HotfixManifest local = Parse(localText, "本地清单");
                if (local != null)
                {
                    Applied = local;
                    Log("本地清单 contentVersion=" + local.contentVersion + ", lua=" + local.scripts.version);
                    if (onDone != null) onDone(Applied);
                    yield break;
                }
            }

            string bundledText = null;
            string bundledError = null;
            yield return _storage.ReadBundledText(
                HotfixPaths.BundledManifestFile,
                text => bundledText = text,
                error => bundledError = error);

            if (!string.IsNullOrEmpty(bundledText))
            {
                HotfixManifest bundled = Parse(bundledText, "内置清单");
                if (bundled != null)
                {
                    Applied = bundled;
                    AppliedFromBundled = true;
                    Log("内置清单 contentVersion=" + bundled.contentVersion + ", lua=" + bundled.scripts.version);
                }
            }
            else
            {
                Log("内置清单不可用: " + bundledError);
            }

            if (onDone != null) onDone(Applied);
        }

        /// <summary>
        /// 拉取远端清单。未配置、未变化（304）或失败时都回调 null —— 调用方按"不更新"处理。
        /// </summary>
        public IEnumerator FetchRemote(Action<HotfixManifest> onDone)
        {
            if (string.IsNullOrEmpty(_config.manifestUrl))
            {
                Log("未配置 manifestUrl，跳过远端检查");
                if (onDone != null) onDone(null);
                yield break;
            }

            string cachedEtag;
            if (!_storage.TryReadText(HotfixPaths.ManifestEtagFile, out cachedEtag)) cachedEtag = null;
            cachedEtag = cachedEtag != null ? cachedEtag.Trim() : null;

            // 清单是最关键的一次请求：失败一次就整轮放弃太脆，给足重试
            int maxAttempts = Mathf.Max(2, _config.maxRetries + 1);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                HotfixManifest manifest = null;
                bool notModified = false;
                string failure = null;

                yield return FetchOnce(
                    cachedEtag,
                    m => manifest = m,
                    () => notModified = true,
                    e => failure = e);

                if (notModified)
                {
                    Log("远端清单未变化（304）");
                    if (onDone != null) onDone(null);
                    yield break;
                }

                if (manifest != null)
                {
                    if (onDone != null) onDone(manifest);
                    yield break;
                }

                Debug.LogWarning("[Hotfix] 拉取远端清单失败（第 " + attempt + "/" + maxAttempts + " 次）: " + failure);
                if (attempt < maxAttempts)
                {
                    yield return new WaitForSecondsRealtime(0.5f * attempt);
                }
            }

            if (onDone != null) onDone(null);
        }

        private IEnumerator FetchOnce(
            string cachedEtag,
            Action<HotfixManifest> onParsed,
            Action onNotModified,
            Action<string> onFailed)
        {
            using (var request = UnityWebRequest.Get(_config.manifestUrl))
            {
                request.timeout = Mathf.Max(5, _config.timeoutSeconds);
                if (!string.IsNullOrEmpty(cachedEtag))
                {
                    request.SetRequestHeader("If-None-Match", cachedEtag);
                }

                yield return request.SendWebRequest();

                // 304 必须在判断 result 之前处理：不同 Unity 版本对 3xx 的归类不一致。
                if (request.responseCode == 304)
                {
                    if (onNotModified != null) onNotModified();
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string detail = "result=" + request.result +
                                    ", code=" + request.responseCode +
                                    ", error=" + (string.IsNullOrEmpty(request.error) ? "<empty>" : request.error);
                    if (onFailed != null) onFailed(detail);
                    yield break;
                }

                string etag = request.GetResponseHeader("ETag");
                if (!string.IsNullOrEmpty(etag))
                {
                    _storage.WriteTextAtomic(HotfixPaths.ManifestEtagFile, etag);
                }

                HotfixManifest manifest = Parse(request.downloadHandler.text, "远端清单");
                if (manifest == null)
                {
                    if (onFailed != null) onFailed("清单解析失败");
                    yield break;
                }

                if (onParsed != null) onParsed(manifest);
            }
        }

        /// <summary>
        /// 纯函数式决策：给定远端清单，算出这次该做什么。
        /// </summary>
        public UpdatePlan BuildPlan(HotfixManifest remote)
        {
            var plan = new UpdatePlan
            {
                Local = Applied,
                Remote = remote,
                Target = remote != null ? remote : Applied
            };

            if (remote == null)
            {
                plan.Reason = "无可用远端清单";
                return plan;
            }

            string appVersion = _config.ResolveAppVersion();
            if (!remote.appVersionRange.IsCompatible(appVersion))
            {
                plan.RequiresStoreUpdate = true;
                plan.Reason = "AppVersion " + appVersion + " 不在兼容区间 [" +
                              remote.appVersionRange.min + ", " + remote.appVersionRange.max + "]";
                return plan;
            }

            if (!string.IsNullOrEmpty(remote.channel) &&
                !string.Equals(remote.channel, _config.channel, StringComparison.OrdinalIgnoreCase))
            {
                plan.Reason = "渠道不匹配: 清单=" + remote.channel + " 客户端=" + _config.channel;
                return plan;
            }

            if (remote.contentVersion <= LocalContentVersion)
            {
                plan.Reason = "远端 contentVersion(" + remote.contentVersion +
                              ") 不高于本地(" + LocalContentVersion + ")";
                return plan;
            }

            // contentVersion 递增但两边内容一致（例如只改了以后才会用到的字段）：本次无事可做。
            bool scriptChanged = !SameScript(Applied, remote);
            bool resourceChanged = !SameResources(Applied, remote);

            if (!scriptChanged && !resourceChanged)
            {
                plan.Reason = "contentVersion 递增但内容一致，无需下载";
                return plan;
            }

            plan.NeedUpdate = true;
            plan.NeedScriptUpdate = scriptChanged;
            plan.NeedResourceUpdate = resourceChanged;
            plan.TotalBytes = scriptChanged ? remote.scripts.size : 0;
            plan.ResourceLabels = resourceChanged ? CollectResourceLabels(remote) : new string[0];

            plan.Reason = (scriptChanged
                              ? "Lua 包 " + (Applied != null ? Applied.scripts.version : "无") + " -> " + remote.scripts.version
                              : string.Empty)
                          + (resourceChanged
                              ? (scriptChanged ? "；" : string.Empty) + "资源 catalog -> " + remote.resources.catalogHash
                              : string.Empty);

            return plan;
        }

        private static bool SameScript(HotfixManifest local, HotfixManifest remote)
        {
            if (local == null || local.scripts == null || remote == null || remote.scripts == null) return false;

            return string.Equals(local.scripts.version, remote.scripts.version, StringComparison.Ordinal)
                   && string.Equals(local.scripts.packageHash, remote.scripts.packageHash, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameResources(HotfixManifest local, HotfixManifest remote)
        {
            if (remote == null || remote.resources == null) return true;
            if (string.IsNullOrEmpty(remote.resources.catalogHash)) return true;   // 未启用资源通道
            if (local == null || local.resources == null) return false;

            // catalog 的 hash 已经代表整份资源内容；组版本号只用于诊断，不参与比较
            if (!string.Equals(local.resources.catalogHash, remote.resources.catalogHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int localCount = local.resources.groups != null ? local.resources.groups.Length : 0;
            int remoteCount = remote.resources.groups != null ? remote.resources.groups.Length : 0;
            if (localCount != remoteCount) return false;

            for (int i = 0; i < remoteCount; i++)
            {
                if (!string.Equals(local.resources.groups[i].name, remote.resources.groups[i].name, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        private static string[] CollectResourceLabels(HotfixManifest remote)
        {
            if (remote == null || remote.resources == null || remote.resources.groups == null) return new string[0];

            var labels = new System.Collections.Generic.List<string>(remote.resources.groups.Length);
            for (int i = 0; i < remote.resources.groups.Length; i++)
            {
                string name = remote.resources.groups[i].name;
                if (string.IsNullOrEmpty(name) || labels.Contains(name)) continue;
                labels.Add(name);
            }
            return labels.ToArray();
        }

        private HotfixManifest Parse(string json, string label)
        {
            json = HotfixManifest.SanitizeJsonText(json);

            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("[Hotfix] " + label + " 为空");
                return null;
            }

            try
            {
                HotfixManifest manifest = JsonUtility.FromJson<HotfixManifest>(json);
                if (manifest == null)
                {
                    Debug.LogWarning("[Hotfix] " + label + " 解析结果为空");
                    return null;
                }

                string error;
                if (!manifest.TryValidate(out error))
                {
                    Debug.LogWarning("[Hotfix] " + label + " 校验失败: " + error);
                    return null;
                }

                return manifest;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] " + label + " 解析异常: " + e.Message);
                return null;
            }
        }

        private void Log(string message)
        {
            if (_config.verboseLog) Debug.Log("[Hotfix] " + message);
        }
    }
}
