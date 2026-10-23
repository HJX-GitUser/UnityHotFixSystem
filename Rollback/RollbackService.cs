using System;
using UnityEngine;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 事务与回滚。
    ///
    /// 权威版本只有一份：manifest.json。
    /// manifest.prev.json 与 state.json 的存在意义是让"更新到一半"和"更新后打不开"都能恢复。
    ///
    /// 兜底顺序：current → prev → 包内内置基线。
    /// </summary>
    public sealed class RollbackService
    {
        private readonly StorageService _storage;

        public RollbackService(StorageService storage)
        {
            _storage = storage;
        }

        public HotfixState ReadState()
        {
            string text;
            if (!_storage.TryReadText(HotfixPaths.StateFile, out text)) return new HotfixState();

            try
            {
                HotfixState state = JsonUtility.FromJson<HotfixState>(text);
                return state != null ? state : new HotfixState();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] state.json 解析失败，按 idle 处理: " + e.Message);
                return new HotfixState();
            }
        }

        public void WriteState(HotfixState state)
        {
            if (state == null) return;
            _storage.WriteTextAtomic(HotfixPaths.StateFile, JsonUtility.ToJson(state, true));
        }

        /// <summary>进入中间态（downloading / verifying）。启动时看到中间态即视为异常退出。</summary>
        public void BeginTransaction(string phase, int targetContentVersion)
        {
            HotfixState state = ReadState();
            state.phase = phase;
            state.targetContentVersion = targetContentVersion;
            state.lastError = string.Empty;
            WriteState(state);
        }

        /// <summary>本轮更新放弃：回到 idle，保留坏版本标记与部分文件（部分文件用于本次会话内的续传）。</summary>
        public void Abort(string error)
        {
            HotfixState state = ReadState();
            state.ClearTransaction();
            state.lastError = error ?? string.Empty;
            WriteState(state);
        }

        /// <summary>
        /// 提交：备份旧清单 → 写新清单 → 标记 committed。
        /// 写出新清单之前崩溃，state 仍是中间态，下次启动会自动回退；
        /// 写出之后崩溃，则靠 bootAttempts 兜住。
        /// </summary>
        public void Commit(HotfixManifest target)
        {
            if (target == null) throw new ArgumentNullException("target");

            if (_storage.Exists(HotfixPaths.ManifestFile))
            {
                _storage.CopyFileReplace(HotfixPaths.ManifestFile, HotfixPaths.ManifestPrevFile);
            }

            _storage.WriteTextAtomic(HotfixPaths.ManifestFile, JsonUtility.ToJson(target, true));

            HotfixState state = ReadState();
            state.phase = HotfixState.PhaseCommitted;
            state.targetContentVersion = target.contentVersion;
            state.bootAttempts = 0;
            state.lastError = string.Empty;
            WriteState(state);
        }

        /// <summary>
        /// 回退到上一版清单；没有可用 prev 时退回内置基线。
        /// </summary>
        /// <param name="previous">回退后的清单；null 表示已退到内置基线。</param>
        /// <returns>是否执行了回退动作。</returns>
        public bool TryRollbackToPrevious(out HotfixManifest previous)
        {
            previous = null;

            string prevText;
            if (_storage.TryReadText(HotfixPaths.ManifestPrevFile, out prevText))
            {
                HotfixManifest parsed = TryParse(prevText);
                if (parsed != null)
                {
                    _storage.WriteTextAtomic(HotfixPaths.ManifestFile, prevText);
                    _storage.DeleteFileSafe(HotfixPaths.ManifestPrevFile);
                    _storage.DeleteFileSafe(HotfixPaths.HealthyFile);

                    HotfixState state = ReadState();
                    int failedVersion = state.targetContentVersion;
                    state.ClearTransaction();
                    // 记录坏版本，避免下次又把同一版下回来
                    if (failedVersion > 0) state.badContentVersion = failedVersion;
                    WriteState(state);

                    previous = parsed;
                    Debug.LogWarning("[Hotfix] 已回退到 previous contentVersion=" + parsed.contentVersion);
                    return true;
                }
            }

            Debug.LogWarning("[Hotfix] 没有可用的 previous 清单，回退到内置基线");
            _storage.DeleteFileSafe(HotfixPaths.ManifestFile);
            _storage.DeleteFileSafe(HotfixPaths.ManifestPrevFile);
            _storage.DeleteFileSafe(HotfixPaths.HealthyFile);

            HotfixState fallbackState = ReadState();
            int interruptedVersion = fallbackState.targetContentVersion;
            fallbackState.ClearTransaction();
            if (interruptedVersion > 0) fallbackState.badContentVersion = interruptedVersion;
            WriteState(fallbackState);
            return true;
        }

        /// <summary>把某个版本标记为坏版本，后续不再尝试下载。</summary>
        public void MarkBadVersion(int contentVersion)
        {
            if (contentVersion <= 0) return;

            HotfixState state = ReadState();
            state.badContentVersion = contentVersion;
            WriteState(state);
            Debug.LogWarning("[Hotfix] 版本 " + contentVersion + " 已标记为坏版本");
        }

        /// <summary>启动成功哨兵：写入生效版本并结束事务。</summary>
        public void MarkHealthy(HotfixManifest manifest)
        {
            if (manifest == null) return;

            _storage.WriteTextAtomic(HotfixPaths.HealthyFile, manifest.contentVersion.ToString());

            HotfixState state = ReadState();
            state.phase = HotfixState.PhaseIdle;
            state.targetContentVersion = 0;
            state.bootAttempts = 0;
            WriteState(state);
        }

        /// <summary>上次成功启动的 contentVersion；没有记录返回 0。</summary>
        public int ReadHealthyVersion()
        {
            string text;
            if (!_storage.TryReadText(HotfixPaths.HealthyFile, out text)) return 0;

            int version;
            return int.TryParse(text.Trim(), out version) ? version : 0;
        }

        /// <summary>本次启动是"已提交但尚未健康"的第几次尝试。</summary>
        public int IncreaseBootAttempts()
        {
            HotfixState state = ReadState();
            state.bootAttempts++;
            WriteState(state);
            return state.bootAttempts;
        }

        private static HotfixManifest TryParse(string json)
        {
            json = HotfixManifest.SanitizeJsonText(json);
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                HotfixManifest manifest = JsonUtility.FromJson<HotfixManifest>(json);
                if (manifest == null) return null;

                string error;
                return manifest.TryValidate(out error) ? manifest : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 备份清单解析失败: " + e.Message);
                return null;
            }
        }
    }
}
