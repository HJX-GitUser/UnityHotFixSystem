using System;

namespace HotFix.HotUpdate
{
    public enum HotfixStage
    {
        Idle,
        CheckingLocal,
        FetchingManifest,
        Deciding,
        Preparing,
        Downloading,
        DownloadingResources,
        WaitingForConfirmation,
        Verifying,
        Installing,
        Committing,
        Loading,
        Completed,
        Failed,
        StoreUpdateRequired
    }

    public enum HotfixErrorCode
    {
        None,
        Network,
        ManifestInvalid,
        StoreUpdateRequired,
        InsufficientSpace,
        DownloadFailed,
        VerifyFailed,
        InstallFailed,
        LuaLoadFailed,
        Unknown
    }

    /// <summary>进度快照。UI 只读它，不反向驱动流程。</summary>
    public struct UpdateProgress
    {
        public HotfixStage Stage;
        public string Message;
        public float Progress01;
        public long DownloadedBytes;
        public long TotalBytes;
    }

    public sealed class UpdateResult
    {
        public bool Success;
        public HotfixErrorCode Error = HotfixErrorCode.None;
        public string Message = string.Empty;

        /// <summary>本次启动实际使用的清单（可能来自内置、本地或远端）。</summary>
        public HotfixManifest Manifest;

        /// <summary>需要 XLuaManager 使用的 Lua 目录；null 表示沿用默认来源。</summary>
        public string LuaRootOverride;

        public bool AppliedUpdate;

        public static UpdateResult Ok(HotfixManifest manifest, string luaRoot, bool appliedUpdate)
        {
            return new UpdateResult
            {
                Success = true,
                Manifest = manifest,
                LuaRootOverride = luaRoot,
                AppliedUpdate = appliedUpdate
            };
        }

        public static UpdateResult Fail(HotfixErrorCode code, string message)
        {
            return new UpdateResult
            {
                Success = false,
                Error = code,
                Message = message
            };
        }
    }
}
