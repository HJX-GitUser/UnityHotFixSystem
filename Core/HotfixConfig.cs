using System;
using UnityEngine;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 热更配置。挂在 BootLoader 的 Inspector 上，避免把地址/阈值写死在代码里。
    /// </summary>
    [Serializable]
    public sealed class HotfixConfig
    {
        [Tooltip("清单地址，例如 https://cdn.example.com/hotfix/manifest.json")]
        public string manifestUrl = string.Empty;

        [Tooltip("渠道标记，与清单里的 channel 不一致时本次不更新")]
        public string channel = "default";

        [Tooltip("留空则使用 Application.version")]
        public string appVersionOverride = string.Empty;

        [Tooltip("单次请求超时（秒）")]
        public int timeoutSeconds = 30;

        [Tooltip("下载失败重试次数（不含首次）")]
        public int maxRetries = 3;

        [Tooltip("Editor / DevelopmentBuild 下跳过远端检查，只走本地与内置版本")]
        public bool skipRemoteCheckInDevBuild = false;

        [Tooltip("打印每个阶段的详细日志")]
        public bool verboseLog = true;

        [Header("网络策略")]
        [Tooltip("是否允许在移动网络下下载")]
        public bool allowCellularDownload = false;

        [Tooltip("超过该大小且当前是移动网络时，本次跳过更新（继续用旧内容）")]
        public long wifiOnlyThresholdBytes = 200L * 1024 * 1024;

        [Tooltip("超过该大小时需要玩家确认；设为 0 表示不确认。UI 未提供确认回调时自动通过")]
        public long confirmThresholdBytes = 50L * 1024 * 1024;

        [Header("资源通道")]
        [Tooltip("是否启用 Addressables 远端 catalog（清单里 resources.catalogUrl 为空时自动跳过）")]
        public bool enableAddressablesChannel = true;

        public string ResolveAppVersion()
        {
            return string.IsNullOrEmpty(appVersionOverride) ? Application.version : appVersionOverride;
        }
    }
}
