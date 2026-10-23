using System;
using System.Globalization;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 版本清单。系统唯一的版本真相。
    /// 字段刻意做少：清单是"决策输入"，不是"内容目录"。
    /// </summary>
    [Serializable]
    public sealed class HotfixManifest
    {
        /// <summary>当前客户端能理解的清单格式版本。</summary>
        public const int CurrentManifestVersion = 1;

        public int manifestVersion = CurrentManifestVersion;

        /// <summary>单调递增整数，"是否有更新"的唯一比较依据。</summary>
        public int contentVersion;

        public VersionRange appVersionRange = new VersionRange();
        public string channel = "default";
        public ResourceSection resources = new ResourceSection();
        public ScriptSection scripts = new ScriptSection();
        public long createdAtUnix;

        public bool TryValidate(out string error)        {
            error = null;

            if (manifestVersion <= 0 || manifestVersion > CurrentManifestVersion)
            {
                error = "清单格式版本不支持: " + manifestVersion;
                return false;
            }

            if (scripts == null)
            {
                scripts = new ScriptSection();
            }

            if (string.IsNullOrEmpty(scripts.version))
            {
                error = "清单缺少 scripts.version";
                return false;
            }

            if (string.IsNullOrEmpty(scripts.packageUrl))
            {
                error = "清单缺少 scripts.packageUrl";
                return false;
            }

            if (scripts.size <= 0)
            {
                error = "清单缺少 scripts.size";
                return false;
            }

            if (string.IsNullOrEmpty(scripts.packageHash))
            {
                error = "清单缺少 scripts.packageHash";
                return false;
            }

            if (string.IsNullOrEmpty(scripts.entryModule))
            {
                scripts.entryModule = "Main";
            }

            return true;
        }

        /// <summary>
        /// 清洗清单文本：部分 CDN / 编辑器工具会输出带 BOM 的 UTF-8，
        /// JsonUtility 遇到 BOM 会直接报 "Invalid value"，这里统一兜住。
        /// </summary>
        public static string SanitizeJsonText(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;

            if (json[0] == '\uFEFF') json = json.Substring(1);
            return json.Trim();
        }
    }

    [Serializable]
    public sealed class VersionRange
    {
        public string min = string.Empty;
        public string max = string.Empty;

        public bool IsCompatible(string appVersion)
        {
            if (!string.IsNullOrEmpty(min) && Compare(appVersion, min) < 0) return false;
            if (!string.IsNullOrEmpty(max) && Compare(appVersion, max) > 0) return false;
            return true;
        }

        /// <summary>
        /// 点分数字比较（"1.2.10" &gt; "1.2.9"）。缺省段按 0 处理。
        /// 非数字段按 0 处理，因此 AppVersion 应保持纯数字点分格式。
        /// </summary>
        public static int Compare(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return 0;

            string[] a = Split(left);
            string[] b = Split(right);
            int length = a.Length > b.Length ? a.Length : b.Length;

            for (int i = 0; i < length; i++)
            {
                int va = i < a.Length ? ParsePart(a[i]) : 0;
                int vb = i < b.Length ? ParsePart(b[i]) : 0;
                if (va != vb) return va < vb ? -1 : 1;
            }

            return 0;
        }

        private static string[] Split(string version)
        {
            if (string.IsNullOrEmpty(version)) return new string[0];
            return version.Trim().Split('.');
        }

        private static int ParsePart(string part)
        {
            int value;
            return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }

    [Serializable]
    public sealed class ResourceSection
    {
        /// <summary>远端 catalog 地址（P1 资源通道使用）。</summary>
        public string catalogUrl = string.Empty;
        public string catalogHash = string.Empty;
        public ResourceGroupInfo[] groups = new ResourceGroupInfo[0];
    }

    [Serializable]
    public sealed class ResourceGroupInfo
    {
        public string name = string.Empty;
        public string version = string.Empty;
        public long size;
        public string hash = string.Empty;
    }

    [Serializable]
    public sealed class ScriptSection
    {
        /// <summary>Lua 包版本，例如 "1.0.3"。</summary>
        public string version = string.Empty;
        public string packageUrl = string.Empty;

        /// <summary>包级 SHA256，小写十六进制。</summary>
        public string packageHash = string.Empty;
        public long size;

        /// <summary>入口模块名，默认 "Main"。</summary>
        public string entryModule = "Main";
    }
}
