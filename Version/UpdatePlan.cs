namespace HotFix.HotUpdate
{
    /// <summary>
    /// 版本决策结果。只描述"要做什么"，不执行任何动作。
    /// </summary>
    public sealed class UpdatePlan
    {
        /// <summary>当前生效清单（本地或内置），首次启动可能为 null。</summary>
        public HotfixManifest Local;

        /// <summary>远端清单，拉取失败或未变化时为 null。</summary>
        public HotfixManifest Remote;

        /// <summary>本次启动最终应使用的清单。无更新时等于 Local。</summary>
        public HotfixManifest Target;

        public bool NeedUpdate;

        /// <summary>是否需要更新 Lua 包。</summary>
        public bool NeedScriptUpdate;

        /// <summary>是否需要更新资源（远端 catalog / 资源组）。</summary>
        public bool NeedResourceUpdate;

        /// <summary>本次需要处理的资源组 label。</summary>
        public string[] ResourceLabels = new string[0];

        /// <summary>App 版本不在清单兼容区间内，需要去商店更新。</summary>
        public bool RequiresStoreUpdate;

        /// <summary>预计下载字节数。</summary>
        public long TotalBytes;

        /// <summary>决策原因，用于日志与上报。</summary>
        public string Reason = string.Empty;
    }
}
