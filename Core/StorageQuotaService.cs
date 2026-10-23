using System;
using System.IO;
using UnityEngine;

namespace HotFix.HotUpdate
{
    /// <summary>
    /// 磁盘空间与旧版本清理。
    ///
    /// 策略很保守：永远保留"当前 + 上一版"，只清理更老的目录。
    /// 宁可多占一点空间，也不能把可回滚的版本删掉。
    /// </summary>
    public sealed class StorageQuotaService
    {
        /// <summary>预留给系统与 Addressables 缓存的余量。</summary>
        private const long ReserveBytes = 32L * 1024 * 1024;

        private readonly StorageService _storage;

        public StorageQuotaService(StorageService storage)
        {
            _storage = storage;
        }

        /// <summary>目录占用（递归，失败时返回已统计到的部分）。</summary>
        public long GetUsageBytes(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

            long total = 0;
            try
            {
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception)
                    {
                        // 单个文件读不到就跳过，不影响整体判断
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 统计目录大小失败: " + e.Message);
            }

            return total;
        }

        /// <summary>
        /// 是否有足够空间放下本次更新。可用空间未知（部分平台拿不到）时返回 true，交给下载失败兜底。
        /// </summary>
        public bool HasRoomFor(long requiredBytes, out string detail)
        {
            detail = null;
            if (requiredBytes <= 0) return true;

            long available = _storage.GetAvailableBytes();
            if (available <= 0) return true;

            long usable = available - ReserveBytes;
            if (usable >= requiredBytes) return true;

            detail = "空间不足：需要 " + FormatBytes(requiredBytes + ReserveBytes) +
                     "，可用 " + FormatBytes(available);
            return false;
        }

        public long GetHotfixUsage()
        {
            return GetUsageBytes(HotfixPaths.Root);
        }

        /// <summary>只保留指定版本，其余 v* 目录删掉。</summary>
        public void PruneLuaVersions(string keepVersion, string previousVersion)
        {
            if (!Directory.Exists(HotfixPaths.LuaRoot)) return;

            string keepCurrent = HotfixPaths.LuaVersionDirectory(keepVersion);
            string keepPrevious = string.IsNullOrEmpty(previousVersion)
                ? null
                : HotfixPaths.LuaVersionDirectory(previousVersion);

            try
            {
                string[] directories = Directory.GetDirectories(HotfixPaths.LuaRoot);
                for (int i = 0; i < directories.Length; i++)
                {
                    if (SamePath(directories[i], keepCurrent)) continue;
                    if (keepPrevious != null && SamePath(directories[i], keepPrevious)) continue;

                    _storage.DeleteDirectorySafe(directories[i]);
                    Debug.Log("[Hotfix] 已清理旧 Lua 版本: " + directories[i]);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Hotfix] 清理旧版本失败: " + e.Message);
            }
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            string a = left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
        }
    }
}
