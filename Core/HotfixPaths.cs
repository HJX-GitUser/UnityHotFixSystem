using System;
using System.IO;
using UnityEngine;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 运行时目录约定的唯一出处。
    /// 其他模块不允许自己拼 Application.persistentDataPath。
    /// </summary>
    public static class HotfixPaths
    {
        public const string RootFolderName = "hotfix";
        public const string ManifestFileName = "manifest.json";
        public const string ManifestPrevFileName = "manifest.prev.json";
        public const string ManifestEtagFileName = "manifest.etag";
        public const string StateFileName = "state.json";
        public const string HealthyFileName = "healthy.flag";
        public const string LuaFolderName = "lua";
        public const string LuaBundleFileName = "lua.bundle";
        public const string LuaPointerFileName = "current.txt";
        public const string TempFolderName = "tmp";

        /// <summary>可写根目录：persistentDataPath/hotfix</summary>
        public static string Root
        {
            get { return Path.Combine(Application.persistentDataPath, RootFolderName); }
        }

        public static string ManifestFile
        {
            get { return Path.Combine(Root, ManifestFileName); }
        }

        public static string ManifestPrevFile
        {
            get { return Path.Combine(Root, ManifestPrevFileName); }
        }

        public static string ManifestEtagFile
        {
            get { return Path.Combine(Root, ManifestEtagFileName); }
        }

        public static string StateFile
        {
            get { return Path.Combine(Root, StateFileName); }
        }

        public static string HealthyFile
        {
            get { return Path.Combine(Root, HealthyFileName); }
        }

        public static string LuaRoot
        {
            get { return Path.Combine(Root, LuaFolderName); }
        }

        public static string LuaPointerFile
        {
            get { return Path.Combine(LuaRoot, LuaPointerFileName); }
        }

        public static string LuaVersionDirectory(string version)
        {
            return Path.Combine(LuaRoot, "v" + (version ?? string.Empty));
        }

        public static string LuaBundleInVersionDirectory(string version)
        {
            return Path.Combine(LuaVersionDirectory(version), LuaBundleFileName);
        }

        public static string TempRoot
        {
            get { return Path.Combine(Root, TempFolderName); }
        }

        /// <summary>包内的出厂基线目录（只读，Android 上需通过 UnityWebRequest 访问）。</summary>
        public static string BundledRoot
        {
            get { return Path.Combine(Application.streamingAssetsPath, RootFolderName); }
        }

        public static string BundledManifestFile
        {
            get { return Path.Combine(BundledRoot, ManifestFileName); }
        }

        public static string BundledLuaBundleFile
        {
            get { return Path.Combine(BundledRoot, LuaBundleFileName); }
        }

        /// <summary>
        /// 包内路径是否必须走 UnityWebRequest。
        /// Android 的 StreamingAssets 位于 apk 内部，File API 读不到。
        /// </summary>
        public static bool RequiresWebRequest(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            return path.StartsWith(Application.streamingAssetsPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
