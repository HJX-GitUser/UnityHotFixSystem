using UnityEngine;

namespace HotFix.HotUpdate.EditorTools
{
    /// <summary>
    /// 构建期配置（编辑器专用资产）。不放运行时程序集，避免把发布参数打进包体。
    /// </summary>
    public sealed class HotfixBuildSettings : ScriptableObject
    {
        [Header("源")]
        [Tooltip("Lua 文本资产目录（.lua.txt），由 LuaToTextConverter 生成")]
        public string luaAssetFolder = "Assets/ArtAssets/Lua";

        [Header("发布")]
        [Tooltip("CDN 上的热更根目录，末尾不要带 /")]
        public string cdnBaseUrl = "https://cdn.example.com/hotfix";

        public string channel = "default";

        [Tooltip("App 版本兼容区间；留空表示不限制（首次接入建议留空，避免误加强更）")]
        public string appVersionMin = string.Empty;
        public string appVersionMax = string.Empty;

        public string entryModule = "Main";

        [Tooltip("每次构建自动 +1；客户端只比较这个数字")]
        public int contentVersion = 0;

        [Header("产物")]
        [Tooltip("构建产物目录（上传 CDN 的源）")]
        public string outputFolder = "ServerData/hotfix";

        [Header("资源通道（Addressables）")]
        [Tooltip("开启后构建会把指定目录下的资产打包成远端资源组，并写入清单")]
        public bool includeAddressables = true;

        [Tooltip("远端资源组名，同时作为运行期下载用的 label")]
        public string resourceGroupName = "remote_config";

        [Tooltip("这些目录下的资产会被标记为 addressable")]
        public string[] resourceAssetFolders = new[] { "Assets/ArtAssets/Config" };
    }
}
