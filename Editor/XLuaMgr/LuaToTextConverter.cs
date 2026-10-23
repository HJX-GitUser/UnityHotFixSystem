using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace XLuaTools
{
    /// <summary>
    /// Lua → Text 批量转换配置。
    /// </summary>
    public sealed class LuaToTextSettings
    {
        /// <summary>源目录（绝对路径），递归查找 .lua 文件。</summary>
        public string sourceFolder = string.Empty;

        /// <summary>输出目录（绝对路径），通常放在 Assets 下，方便作为 TextAsset 打包。</summary>
        public string outputFolder = string.Empty;

        /// <summary>是否包含子目录。</summary>
        public bool recursive = true;

        /// <summary>是否保持源目录的层级结构；关闭后所有文件平铺到输出目录。</summary>
        public bool keepDirectoryStructure = true;

        /// <summary>是否覆盖输出目录中已存在的同名文件。</summary>
        public bool overwrite = true;

        /// <summary>是否清理输出目录中没有对应源文件的 .lua.txt（避免打包进已删除的脚本）。</summary>
        public bool deleteStale;

        /// <summary>转换后为生成的 TextAsset 设置的 AssetBundle 名称，留空则不设置。</summary>
        public string assetBundleName = string.Empty;

        /// <summary>转换完成后在 Project 窗口选中输出目录。</summary>
        public bool selectOutputAfterConvert = true;
    }

    /// <summary>
    /// 一次批量转换的结果统计。
    /// </summary>
    public sealed class LuaToTextResult
    {
        public int converted;
        public int skipped;
        public int deleted;
        public bool canceled;
        public string sourceFolder;
        public string outputFolder;

        public readonly List<string> assets = new List<string>();
        public readonly List<string> errors = new List<string>();

        /// <summary>把结果写进控制台，供菜单快速转换与窗口共用。</summary>
        public void LogToConsole()
        {
            string summary = string.Format("[XLua] Lua → Text 完成：转换 {0}，跳过 {1}，清理 {2}{3}\n源目录: {4}\n输出目录: {5}",
                converted, skipped, deleted, canceled ? "（已取消）" : string.Empty, sourceFolder, outputFolder);

            if (errors.Count == 0)
            {
                Debug.Log(summary);
                return;
            }

            Debug.LogWarning(summary + "\n错误 " + errors.Count + " 条");
            for (int i = 0; i < errors.Count; i++)
            {
                Debug.LogWarning("[XLua] " + errors[i]);
            }
        }
    }

    /// <summary>
    /// 把 .lua 脚本批量复制成 .lua.txt（Unity 可识别的 TextAsset），
    /// 支持保持目录结构、清理过期文件、设置 AssetBundle 名称。
    /// 不依赖任何窗口，可以从菜单、EditorWindow 或 -executeMethod 调用。
    /// </summary>
    public static class LuaToTextConverter
    {
        public const string LuaExtension = ".lua";
        public const string TextSuffix = ".txt";

        /// <summary>默认源目录：工程根目录下的 Lua 文件夹（与 XLuaManager 的开发期路径一致）。</summary>
        public static string GetDefaultSourceFolder()
        {
            return Path.Combine(GetProjectRoot(), "Lua");
        }

        /// <summary>默认输出目录：Assets/StreamingAssets/Lua（可直接打进包内的脚本目录）。</summary>
        public static string GetDefaultOutputFolder()
        {
            return Path.Combine(GetProjectRoot(), "Assets", "StreamingAssets", "Lua");
        }

        /// <summary>工程根目录（Application.dataPath 的上一级）。</summary>
        public static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        /// <summary>绝对路径转 "Assets/..." 形式的工程相对路径；不在 Assets 下返回 null。</summary>
        public static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;

            string projectRoot = GetProjectRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(absolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, projectRoot, StringComparison.OrdinalIgnoreCase)) return string.Empty;

            string prefix = projectRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            return fullPath.Substring(prefix.Length).Replace('\\', '/');
        }

        /// <summary>
        /// 执行批量转换。
        /// </summary>
        public static LuaToTextResult Convert(LuaToTextSettings settings)
        {
            var result = new LuaToTextResult();
            if (settings == null)
            {
                result.errors.Add("配置为空");
                return result;
            }

            string source = NormalizeDirectory(settings.sourceFolder);
            string output = NormalizeDirectory(settings.outputFolder);
            result.sourceFolder = source;
            result.outputFolder = output;

            if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
            {
                result.errors.Add("源目录不存在: " + (string.IsNullOrEmpty(source) ? "<空>" : source));
                return result;
            }

            if (string.IsNullOrEmpty(output))
            {
                result.errors.Add("输出目录为空");
                return result;
            }

            if (IsSameOrInside(output, source))
            {
                result.errors.Add("输出目录不能位于源目录内部: " + output);
                return result;
            }

            if (IsSameOrInside(source, output))
            {
                result.errors.Add("源目录不能位于输出目录内部: " + source);
                return result;
            }

            List<string> sourceFiles = CollectLuaFiles(source, settings.recursive);
            if (sourceFiles.Count == 0)
            {
                result.errors.Add("源目录下没有找到 .lua 文件: " + source);
                return result;
            }

            try
            {
                Directory.CreateDirectory(output);
            }
            catch (Exception e)
            {
                result.errors.Add("创建输出目录失败: " + e.Message);
                return result;
            }

            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool outputUnderAssets = ToProjectRelativePath(output) != null;
            int total = sourceFiles.Count;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Lua → Text",
                            string.Format("({0}/{1}) {2}", i + 1, total, Path.GetFileName(sourceFiles[i])),
                            (float)i / total))
                    {
                        result.canceled = true;
                        break;
                    }

                    string sourceFile = sourceFiles[i];
                    string relative = GetRelativePath(source, sourceFile);
                    string destRelative = settings.keepDirectoryStructure
                        ? relative + TextSuffix
                        : Path.GetFileName(relative) + TextSuffix;

                    string destFile = Path.GetFullPath(Path.Combine(output, destRelative.Replace('/', Path.DirectorySeparatorChar)));
                    expectedFiles.Add(destFile);

                    if (!settings.overwrite && File.Exists(destFile))
                    {
                        result.skipped++;
                        continue;
                    }

                    try
                    {
                        string directory = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                        // 直接按字节复制，避免改变编码 / 换行
                        File.WriteAllBytes(destFile, File.ReadAllBytes(sourceFile));
                        result.converted++;

                        string assetPath = ToProjectRelativePath(destFile);
                        if (!string.IsNullOrEmpty(assetPath)) result.assets.Add(assetPath);
                    }
                    catch (Exception e)
                    {
                        result.errors.Add(sourceFile + " -> " + e.Message);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (settings.deleteStale)
            {
                result.deleted = DeleteStaleFiles(output, expectedFiles, result.errors);
            }

            AssetDatabase.Refresh();

            if (!string.IsNullOrEmpty(settings.assetBundleName) && outputUnderAssets)
            {
                ApplyAssetBundleName(result.assets, settings.assetBundleName);
            }

            if (settings.selectOutputAfterConvert && outputUnderAssets)
            {
                SelectFolder(ToProjectRelativePath(output));
            }

            return result;
        }

        /// <summary>
        /// 使用默认路径转换（工程根 Lua → Assets/StreamingAssets/Lua），并清理过期文件。
        /// 可用于批处理 / CI：Unity.exe -batchmode -quit -projectPath &lt;工程&gt; -executeMethod XLuaTools.LuaToTextConverter.ConvertDefaultFolders
        /// </summary>
        public static void ConvertDefaultFolders()
        {
            var settings = new LuaToTextSettings
            {
                sourceFolder = GetDefaultSourceFolder(),
                outputFolder = GetDefaultOutputFolder(),
                recursive = true,
                keepDirectoryStructure = true,
                overwrite = true,
                deleteStale = true,
                selectOutputAfterConvert = false
            };

            LuaToTextResult result = Convert(settings);
            result.LogToConsole();

            if (result.errors.Count > 0 && Application.isBatchMode)
            {
                EditorApplication.Exit(1);
            }
        }

        /// <summary>收集目录下的 .lua 文件（不含已经是 .lua.txt 的文件）。</summary>
        public static List<string> CollectLuaFiles(string sourceFolder, bool recursive)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder)) return files;

            string[] all;
            try
            {
                all = Directory.GetFiles(sourceFolder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[XLua] 遍历源目录失败: " + e.Message);
                return files;
            }

            for (int i = 0; i < all.Length; i++)
            {
                // 用扩展名精确判断，避免 Windows 通配符把 Main.lua.txt 也当成 .lua
                if (string.Equals(Path.GetExtension(all[i]), LuaExtension, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(all[i]);
                }
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static int DeleteStaleFiles(string output, HashSet<string> expectedFiles, List<string> errors)
        {
            int deleted = 0;

            string[] existing;
            try
            {
                existing = Directory.GetFiles(output, "*" + TextSuffix, SearchOption.AllDirectories);
            }
            catch (Exception e)
            {
                errors.Add("清理过期文件失败: " + e.Message);
                return 0;
            }

            for (int i = 0; i < existing.Length; i++)
            {
                string file = Path.GetFullPath(existing[i]);
                // 只清理本工具生成的 .lua.txt，不动用户自己的 txt
                if (!file.EndsWith(LuaExtension + TextSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (expectedFiles.Contains(file)) continue;

                try
                {
                    string assetPath = ToProjectRelativePath(file);
                    if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.DeleteAsset(assetPath))
                    {
                        deleted++;
                        continue;
                    }

                    File.Delete(file);
                    string metaFile = file + ".meta";
                    if (File.Exists(metaFile)) File.Delete(metaFile);
                    deleted++;
                }
                catch (Exception e)
                {
                    errors.Add("删除失败 " + file + " -> " + e.Message);
                }
            }

            DeleteEmptyDirectories(output);
            return deleted;
        }

        private static void DeleteEmptyDirectories(string root)
        {
            try
            {
                string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
                // 从最深层开始删，父目录才可能变空
                Array.Sort(directories, (a, b) => b.Length.CompareTo(a.Length));

                for (int i = 0; i < directories.Length; i++)
                {
                    if (Directory.GetFiles(directories[i]).Length != 0) continue;
                    if (Directory.GetDirectories(directories[i]).Length != 0) continue;

                    string assetPath = ToProjectRelativePath(directories[i]);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                    else
                    {
                        Directory.Delete(directories[i]);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[XLua] 清理空目录失败: " + e.Message);
            }
        }

        private static void ApplyAssetBundleName(List<string> assetPaths, string bundleName)
        {
            for (int i = 0; i < assetPaths.Count; i++)
            {
                try
                {
                    AssetImporter importer = AssetImporter.GetAtPath(assetPaths[i]);
                    if (importer == null)
                    {
                        Debug.LogWarning("[XLua] 找不到资源导入器: " + assetPaths[i]);
                        continue;
                    }

                    // Unity 会强制把 AssetBundle 名转成小写
                    importer.assetBundleName = bundleName;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[XLua] 设置 AssetBundle 名称失败 " + assetPaths[i] + " -> " + e.Message);
                }
            }
        }

        private static void SelectFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;

            UnityEngine.Object folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (folder == null) return;

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = folder;
        }

        private static string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return string.Empty;

            return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string GetRelativePath(string root, string file)
        {
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? root
                : root + Path.DirectorySeparatorChar;

            if (file.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return file.Substring(rootWithSeparator.Length).Replace('\\', '/');
            }

            return Path.GetFileName(file);
        }

        private static bool IsSameOrInside(string path, string parent)
        {
            string childFull = NormalizeDirectory(path);
            string parentFull = NormalizeDirectory(parent);
            if (string.IsNullOrEmpty(childFull) || string.IsNullOrEmpty(parentFull)) return false;

            if (string.Equals(childFull, parentFull, StringComparison.OrdinalIgnoreCase)) return true;

            return childFull.StartsWith(parentFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
