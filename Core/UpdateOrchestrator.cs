using System;
using System.Collections;
using UnityEngine;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 流程编排：把"启动 → 检查 → 策略 → 下载 → 校验 → 提交 → 加载"串成一条状态机。
    ///
    /// 它不实现任何具体能力，只负责顺序、进度与失败处理。
    /// UI 通过 onProgress 观察进度；大包确认通过 <see cref="ConfirmDownloadRequest"/> 注入。
    /// </summary>
    public sealed class UpdateOrchestrator
    {
        private const float ProgressResourcesStart = 0.65f;
        private const float ProgressResourcesEnd = 0.90f;

        private readonly HotfixConfig _config;
        private readonly StorageService _storage;
        private readonly ManifestService _manifests;
        private readonly LuaPackageAdapter _lua;
        private readonly AddressablesAdapter _addressables;
        private readonly RollbackService _rollback;
        private readonly StorageQuotaService _quota;
        private readonly StageTimer _timer = new StageTimer();

        /// <summary>
        /// 可选：UI 实现的大包确认。参数为预计下载字节数与回调；不设置则自动通过。
        /// </summary>
        public Func<long, Action<bool>, IEnumerator> ConfirmDownloadRequest;

        /// <summary>可选：上报实现，默认只打日志。</summary>
        public IHotfixReporter Reporter = new ConsoleHotfixReporter();

        public UpdateOrchestrator(HotfixConfig config)
        {
            _config = config != null ? config : new HotfixConfig();
            _storage = new StorageService();
            _manifests = new ManifestService(_storage, _config);
            var downloader = new DownloadService(_storage, _config);
            _lua = new LuaPackageAdapter(_storage, downloader, new VerifyService());
            _addressables = new AddressablesAdapter();
            _rollback = new RollbackService(_storage);
            _quota = new StorageQuotaService(_storage);
        }

        public IEnumerator Run(Action<UpdateProgress> onProgress, Action<UpdateResult> onDone)
        {
            UpdateResult result = null;
            yield return RunCore(onProgress, r => result = r);

            if (result == null)
            {
                result = UpdateResult.Fail(HotfixErrorCode.Unknown, "流程未返回结果");
            }

            _timer.Close();

            if (Reporter != null)
            {
                try
                {
                    Reporter.OnFinished(result, _timer.Timings);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Hotfix] 上报实现抛异常（已忽略）: " + e.Message);
                }
            }

            if (onDone != null) onDone(result);
        }

        private IEnumerator RunCore(Action<UpdateProgress> onProgress, Action<UpdateResult> setResult)
        {
            Report(onProgress, HotfixStage.CheckingLocal, "初始化本地环境", 0.01f);

            _storage.EnsureDirectories();
            _storage.CleanTemp();

            HotfixState state = _rollback.ReadState();

            // ── 1. 上次更新被打断：先回退，保证这次启动是干净的 ─────────────────
            if (state.IsInterrupted)
            {
                Report(onProgress, HotfixStage.CheckingLocal, "检测到上次更新未完成，正在回退", 0.02f);

                HotfixManifest previous;
                _rollback.TryRollbackToPrevious(out previous);
                SyncLuaPointer(previous);

                state = _rollback.ReadState();
                Log("上次事务被打断，已回退");
            }

            // ── 2. 读取当前生效清单（本地 → 内置）────────────────────────────────
            HotfixManifest applied = null;
            yield return _manifests.LoadApplied(m => applied = m);

            // 出厂基线释放到可写目录，之后所有版本共用同一条加载路径
            if (_lua.ResolveActiveRoot(applied) == null)
            {
                string baselineRoot = null;
                yield return _lua.EnsureBundledBaseline(applied, r => baselineRoot = r);
                if (!string.IsNullOrEmpty(baselineRoot)) Log("出厂 Lua 基线已就绪: " + baselineRoot);
            }

            // ── 3. 已提交但还没证明"能启动"：计数，连续失败则回退 ────────────────
            if (state.phase == HotfixState.PhaseCommitted)
            {
                int healthyVersion = _rollback.ReadHealthyVersion();
                int committedVersion = applied != null ? applied.contentVersion : 0;

                if (applied == null || healthyVersion != committedVersion)
                {
                    int attempts = _rollback.IncreaseBootAttempts();
                    Log("新版本启动未确认（attempts=" + attempts + "）");

                    if (attempts >= 2)
                    {
                        Report(onProgress, HotfixStage.CheckingLocal, "连续启动失败，回退到上一版本", 0.05f);

                        HotfixManifest previous;
                        _rollback.TryRollbackToPrevious(out previous);
                        SyncLuaPointer(previous);

                        applied = previous;
                        state = _rollback.ReadState();
                    }
                }
                else
                {
                    _rollback.MarkHealthy(applied);
                    state = _rollback.ReadState();
                }
            }

            // ── 4. 拉远端清单 ───────────────────────────────────────────────────
            bool skipRemote = _config.skipRemoteCheckInDevBuild &&
                              (Application.isEditor || Debug.isDebugBuild);

            HotfixManifest remote = null;
            if (!skipRemote)
            {
                Report(onProgress, HotfixStage.FetchingManifest, "检查更新", 0.10f);
                yield return _manifests.FetchRemote(m => remote = m);
            }
            else
            {
                Report(onProgress, HotfixStage.FetchingManifest, "开发模式：跳过远端检查", 0.10f);
            }

            // ── 5. 版本决策 ─────────────────────────────────────────────────────
            UpdatePlan plan = _manifests.BuildPlan(remote);
            Log("决策: " + plan.Reason);

            if (plan.RequiresStoreUpdate)
            {
                Report(onProgress, HotfixStage.StoreUpdateRequired, plan.Reason, 1f);
                setResult(UpdateResult.Fail(HotfixErrorCode.StoreUpdateRequired, plan.Reason));
                yield break;
            }

            if (plan.NeedUpdate && plan.Remote != null && state.badContentVersion > 0 &&
                plan.Remote.contentVersion == state.badContentVersion)
            {
                plan.NeedUpdate = false;
                plan.NeedScriptUpdate = false;
                plan.NeedResourceUpdate = false;
                plan.Reason = "版本 " + state.badContentVersion + " 已标记为坏版本，本次跳过";
                Log(plan.Reason);
            }

            if (!plan.NeedUpdate)
            {
                Report(onProgress, HotfixStage.Completed, "已是最新版本", 1f);
                setResult(UpdateResult.Ok(applied, _lua.ResolveActiveRoot(applied), false));
                yield break;
            }

            // ── 6. 资源通道：先加载 catalog，才能知道要下多少 ────────────────────
            long resourceBytes = 0;
            bool updateResources = plan.NeedResourceUpdate && _config.enableAddressablesChannel;

            if (plan.NeedResourceUpdate && !_config.enableAddressablesChannel)
            {
                Log("资源通道已关闭，本次只更新 Lua");
            }

            if (updateResources)
            {
                Report(onProgress, HotfixStage.DownloadingResources, "加载资源 catalog", 0.12f);

                bool catalogOk = false;
                string catalogError = null;
                yield return _addressables.LoadRemoteCatalog(plan.Remote.resources.catalogUrl,
                    (ok, err) => { catalogOk = ok; catalogError = err; });

                if (!catalogOk)
                {
                    string message = "加载资源 catalog 失败：" + catalogError;
                    _rollback.Abort(message);
                    Report(onProgress, HotfixStage.Failed, message, 1f);
                    setResult(UpdateResult.Fail(HotfixErrorCode.DownloadFailed, message));
                    yield break;
                }

                resourceBytes = 0;
                for (int i = 0; i < plan.ResourceLabels.Length; i++)
                {
                    long size = 0;
                    string sizeError = null;
                    yield return _addressables.GetDownloadSize(plan.ResourceLabels[i], (s, e) => { size = s; sizeError = e; });

                    if (size > 0) resourceBytes += size;
                    if (!string.IsNullOrEmpty(sizeError))
                    {
                        Debug.LogWarning("[Hotfix] 查询资源大小失败(" + plan.ResourceLabels[i] + "): " + sizeError);
                    }
                }
            }

            long scriptBytes = plan.NeedScriptUpdate ? plan.TotalBytes : 0;
            long downloadBytes = scriptBytes + resourceBytes;

            // ── 7. 空间预检：新旧版本必须能共存 ─────────────────────────────────
            long keepBytes = applied != null && applied.scripts != null ? applied.scripts.size : 0;
            string quotaDetail;
            if (!_quota.HasRoomFor(downloadBytes + keepBytes, out quotaDetail))
            {
                _rollback.Abort(quotaDetail);
                Report(onProgress, HotfixStage.Failed, quotaDetail, 1f);
                setResult(UpdateResult.Fail(HotfixErrorCode.InsufficientSpace, quotaDetail));
                yield break;
            }

            // ── 8. 网络策略与玩家确认 ───────────────────────────────────────────
            if (IsCellular() && !_config.allowCellularDownload && downloadBytes >= _config.wifiOnlyThresholdBytes)
            {
                string reason = "当前为移动网络，跳过 " + FormatBytes(downloadBytes) + " 的更新";
                Log(reason);
                Report(onProgress, HotfixStage.Completed, reason, 1f);
                setResult(Skip(applied, _lua.ResolveActiveRoot(applied), reason));
                yield break;
            }

            if (_config.confirmThresholdBytes > 0 && downloadBytes >= _config.confirmThresholdBytes &&
                ConfirmDownloadRequest != null)
            {
                Report(onProgress, HotfixStage.WaitingForConfirmation,
                    "等待确认下载 " + FormatBytes(downloadBytes), 0.14f);

                bool confirmed = false;
                yield return ConfirmDownloadRequest(downloadBytes, ok => confirmed = ok);

                if (!confirmed)
                {
                    const string cancelled = "玩家取消了本次更新";
                    Log(cancelled);
                    Report(onProgress, HotfixStage.Completed, cancelled, 1f);
                    setResult(Skip(applied, _lua.ResolveActiveRoot(applied), cancelled));
                    yield break;
                }
            }

            // ── 9. Lua 包：下载 → 校验 → 落盘 ───────────────────────────────────
            ScriptSection script = plan.Remote.scripts;

            if (plan.NeedScriptUpdate)
            {
                _rollback.BeginTransaction(HotfixState.PhaseDownloading, plan.Remote.contentVersion);
                Report(onProgress, HotfixStage.Downloading, "开始下载 Lua 包 " + script.version, 0.15f);

                bool ok = false;
                string error = null;

                yield return _lua.Download(
                    script,
                    (received, total) => Report(onProgress, HotfixStage.Downloading,
                        "下载中 " + FormatBytes(received) + " / " + FormatBytes(total),
                        MapProgress(received, total, 0.15f, updateResources ? 0.50f : 0.65f)),
                    (success, err) => { ok = success; error = err; });

                if (!ok)
                {
                    _rollback.Abort(error);
                    Report(onProgress, HotfixStage.Failed, "下载失败：" + error, 1f);
                    setResult(UpdateResult.Fail(HotfixErrorCode.DownloadFailed, error));
                    yield break;
                }

                _rollback.BeginTransaction(HotfixState.PhaseVerifying, plan.Remote.contentVersion);
                Report(onProgress, HotfixStage.Verifying, "校验中", updateResources ? 0.55f : 0.70f);

                ok = false;
                error = null;
                yield return _lua.Verify(script, (success, err) => { ok = success; error = err; });

                if (!ok)
                {
                    _rollback.Abort(error);
                    Report(onProgress, HotfixStage.Failed, "校验失败：" + error, 1f);
                    setResult(UpdateResult.Fail(HotfixErrorCode.VerifyFailed, error));
                    yield break;
                }

                Report(onProgress, HotfixStage.Installing, "安装中", updateResources ? 0.60f : 0.85f);

                string installError;
                if (!_lua.Place(script, out installError))
                {
                    _rollback.Abort(installError);
                    Report(onProgress, HotfixStage.Failed, installError, 1f);
                    setResult(UpdateResult.Fail(HotfixErrorCode.InstallFailed, installError));
                    yield break;
                }
            }

            // ── 10. 资源：按 label 下载依赖（CRC 与缓存由 Addressables 负责）────
            if (updateResources && plan.ResourceLabels.Length > 0)
            {
                int labelCount = plan.ResourceLabels.Length;

                for (int i = 0; i < labelCount; i++)
                {
                    string label = plan.ResourceLabels[i];
                    float from = ProgressResourcesStart + (ProgressResourcesEnd - ProgressResourcesStart) * i / labelCount;
                    float to = ProgressResourcesStart + (ProgressResourcesEnd - ProgressResourcesStart) * (i + 1) / labelCount;

                    Report(onProgress, HotfixStage.DownloadingResources, "下载资源 " + label, from);

                    bool ok = false;
                    string error = null;
                    yield return _addressables.DownloadDependencies(label,
                        (done, total) => Report(onProgress, HotfixStage.DownloadingResources,
                            "资源 " + label + " " + FormatBytes(done) + " / " + FormatBytes(total),
                            MapProgress(done, total, from, to)),
                        (success, err) => { ok = success; error = err; });

                    if (!ok)
                    {
                        string message = "资源下载失败(" + label + ")：" + error;
                        _rollback.Abort(message);
                        Report(onProgress, HotfixStage.Failed, message, 1f);
                        setResult(UpdateResult.Fail(HotfixErrorCode.DownloadFailed, message));
                        yield break;
                    }
                }
            }

            // ── 11. 提交（事务边界）─────────────────────────────────────────────
            Report(onProgress, HotfixStage.Committing, "提交新版本", 0.92f);

            try
            {
                if (plan.NeedScriptUpdate) _lua.Activate(script.version);
                _rollback.Commit(plan.Remote);
            }
            catch (Exception e)
            {
                _rollback.Abort(e.Message);
                Report(onProgress, HotfixStage.Failed, "提交失败：" + e.Message, 1f);
                setResult(UpdateResult.Fail(HotfixErrorCode.InstallFailed, e.Message));
                yield break;
            }

            string previousVersion = applied != null && applied.scripts != null ? applied.scripts.version : null;
            _quota.PruneLuaVersions(script.version, previousVersion);

            Log("更新完成: contentVersion=" + plan.Remote.contentVersion + ", lua=" + script.version +
                ", resources=" + (updateResources ? "yes" : "no"));

            Report(onProgress, HotfixStage.Completed, "更新完成", 1f);
            setResult(UpdateResult.Ok(plan.Remote, _lua.ResolveActiveRoot(plan.Remote), true));
        }

        /// <summary>回退后让指针与回退后的清单保持一致，避免下次启动读到错位的版本目录。</summary>
        private void SyncLuaPointer(HotfixManifest manifest)
        {
            if (manifest == null || manifest.scripts == null || string.IsNullOrEmpty(manifest.scripts.version)) return;
            if (_lua.ResolveActiveRoot(manifest) == null) return;

            _lua.Activate(manifest.scripts.version);
        }

        private static UpdateResult Skip(HotfixManifest applied, string luaRoot, string reason)
        {
            UpdateResult result = UpdateResult.Ok(applied, luaRoot, false);
            result.Message = reason;
            return result;
        }

        private static bool IsCellular()
        {
            return Application.internetReachability == NetworkReachability.ReachableViaCarrierDataNetwork;
        }

        private static float MapProgress(long done, long total, float from, float to)
        {
            if (total <= 0) return from;
            float ratio = Mathf.Clamp01((float)done / total);
            return Mathf.Lerp(from, to, ratio);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "未知";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
        }

        private void Report(Action<UpdateProgress> onProgress, HotfixStage stage, string message, float progress)
        {
            _timer.Enter(stage, message);

            if (Reporter != null)
            {
                try
                {
                    Reporter.OnStage(stage, message);
                }
                catch (Exception)
                {
                    // 上报实现的问题不能影响主流程
                }
            }

            if (onProgress == null) return;

            onProgress(new UpdateProgress
            {
                Stage = stage,
                Message = message,
                Progress01 = Mathf.Clamp01(progress)
            });
        }

        private void Log(string message)
        {
            if (_config.verboseLog) Debug.Log("[Hotfix] " + message);
        }
    }
}
