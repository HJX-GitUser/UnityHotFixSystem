using System;

namespace HotFix.HotUpdate
{
    /// <summary>事务状态。只用于"上次是否正常收尾"的判断，不承载业务语义。</summary>
    [Serializable]
    public sealed class HotfixState
    {
        public const string PhaseIdle = "idle";
        public const string PhaseDownloading = "downloading";
        public const string PhaseVerifying = "verifying";
        public const string PhaseCommitted = "committed";

        public string phase = PhaseIdle;

        /// <summary>本次事务的目标 contentVersion。</summary>
        public int targetContentVersion;

        /// <summary>已提交但还没标记 healthy 的启动次数。</summary>
        public int bootAttempts;

        /// <summary>被判定为坏版本、不再尝试下载的 contentVersion；0 表示无。</summary>
        public int badContentVersion;

        public string lastError = string.Empty;

        public bool IsInterrupted
        {
            get { return phase == PhaseDownloading || phase == PhaseVerifying; }
        }

        /// <summary>清空事务字段，但保留坏版本标记（否则会反复下载同一个坏版本）。</summary>
        public void ClearTransaction()
        {
            phase = PhaseIdle;
            targetContentVersion = 0;
            bootAttempts = 0;
            lastError = string.Empty;
        }
    }
}
