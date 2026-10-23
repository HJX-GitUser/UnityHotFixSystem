using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using XLuaTools;

namespace HotFix.HotUpdate.EditorTools
{
    /// <summary>
    /// 一键构建：Lua 包 → manifest.json → 释放内置基线。
    ///
    /// 产物：
    ///   ServerData/hotfix/lua-&lt;version&gt;.bundle   上传 CDN
    ///   ServerData/hotfix/manifest.json            上传 CDN
    ///   Assets/StreamingAssets/hotfix/lua.bundle   出厂基线（随包）
    ///   Assets/StreamingAssets/hotfix/manifest.json
    /// </summary>
    public static class HotfixBuildTools
    {
        private const string SettingsPath = "Assets/Settings/HotfixBuildSettings.asset";
        private const string BootScenePath = "Assets/Scenes/Boot.unity";

        [MenuItem("Tools/Hotfix/1. Create Build Settings")]
        public static void CreateSettings()
        {
            HotfixBuildSettings settings = LoadOrCreateSettings();
            if (settings != null) Selection.activeObject = settings;
        }

        [MenuItem("Tools/Hotfix/2. Build Lua Package + Manifest")]
        public static void BuildFromMenu()
        {
            if (TryBuildAll())
            {
                EditorUtility.DisplayDialog("热更新构建", "构建完成，详见 Console。", "好");
            }
        }

        /// <summary>供菜单、命令行与自动化流程共用的构建入口。</summary>
        public static bool TryBuildAll()
        {
            HotfixBuildSettings settings = LoadOrCreateSettings();
            return settings != null && Build(settings);
        }

        [MenuItem("Tools/Hotfix/3. Create Boot Scene")]
        public static void CreateBootScene()
        {
            if (File.Exists(BootScenePath))
            {
                Debug.Log("[Hotfix] Boot 场景已存在: " + BootScenePath);
            }
            else
            {
                // 用 Additive 创建，避免顶掉用户当前打开的场景
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var bootObject = new GameObject("Boot");
                SceneManager.MoveGameObjectToScene(bootObject, scene);
                bootObject.AddComponent<BootLoader>();

                Directory.CreateDirectory(Path.GetDirectoryName(BootScenePath));
                EditorSceneManager.SaveScene(scene, BootScenePath);
                EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.Refresh();
                Debug.Log("[Hotfix] 已创建 Boot 场景: " + BootScenePath);
            }

            EnsureBuildScenes();
        }

        /// <summary>
        /// 保证 Boot 排第一，并把 Assets/Scenes 下的其余场景补进 Build Settings。
        /// 没有这一步，更新完成后 BootLoader 无法加载主场景。
        /// </summary>
        private static void EnsureBuildScenes()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

            scenes.RemoveAll(s => string.Equals(s.path, BootScenePath, StringComparison.OrdinalIgnoreCase));
            scenes.Insert(0, new EditorBuildSettingsScene(BootScenePath, true));

            if (AssetDatabase.IsValidFolder("Assets/Scenes"))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (string.Equals(path, BootScenePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (scenes.Exists(s => string.Equals(s.path, path, StringComparison.OrdinalIgnoreCase))) continue;

                    scenes.Add(new EditorBuildSettingsScene(path, true));
                    Debug.Log("[Hotfix] 已加入 Build Settings: " + path);
                }
            }

            EditorBuildSettings.scenes = scenes.ToArray();

            // ProjectSettings 不归 AssetDatabase 管，显式保存一次，避免只存在内存里
            AssetDatabase.SaveAssets();
            try
            {
                EditorApplication.ExecuteMenuItem("File/Save Project");
            }
            catch (Exception)
            {
                // 批处理模式下菜单不可用时忽略：Unity 退出时仍会落盘
            }

            Debug.Log("[Hotfix] Build Settings 场景数: " + scenes.Count);
        }

        // ── 命令行入口（Unity -batchmode -executeMethod ...）────────────────────

        public static void BuildFromCommandLine()
        {
            if (!TryBuildAll())
            {
                Debug.LogError("[Hotfix] 命令行构建失败");
                EditorApplication.Exit(1);
            }
        }

        public static void SetupBootSceneFromCommandLine()
        {
            CreateBootScene();
        }

        // ── 实现 ───────────────────────────────────────────────────────────────

        private static bool Build(HotfixBuildSettings settings)
        {
            try
            {
                if (!ConvertLuaSources(settings)) return false;

                // Convert 内部会触发 AssetDatabase.Refresh，之前持有的配置实例会失效，必须重新取
                HotfixBuildSettings reloaded = AssetDatabase.LoadAssetAtPath<HotfixBuildSettings>(SettingsPath);
                if (reloaded != null) settings = reloaded;

                if (!Directory.Exists(settings.luaAssetFolder))
                {
                    Debug.LogError("[Hotfix] Lua 资产目录不存在: " + settings.luaAssetFolder);
                    return false;
                }

                string[] files = Directory.GetFiles(settings.luaAssetFolder, "*.lua.txt", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Debug.LogError("[Hotfix] 没有找到 .lua.txt，请先执行 Lua → Text 转换: " + settings.luaAssetFolder);
                    return false;
                }

                // 每次发布内容版本 +1，Lua 包版本与之绑定，避免两套版本号走偏
                settings.contentVersion++;
                int contentVersion = settings.contentVersion;   // 本地快照：后续 AssetDatabase 刷新会让引用失效
                string luaVersion = "1.0." + contentVersion;

                Directory.CreateDirectory(settings.outputFolder);

                string bundleFileName = "lua-" + luaVersion + ".bundle";
                string bundleOutputPath = Path.Combine(settings.outputFolder, bundleFileName);

                if (!BuildLuaBundle(files, luaVersion, bundleOutputPath)) return false;

                string hash = VerifyService.ComputeSha256Hex(bundleOutputPath);
                long size = new FileInfo(bundleOutputPath).Length;

                var resources = new ResourceSection();
                if (settings.includeAddressables)
                {
                    string addressablesError;
                    if (!BuildAddressables(settings, contentVersion, out resources, out addressablesError))
                    {
                        Debug.LogError("[Hotfix] Addressables 构建失败: " + addressablesError);
                        return false;
                    }
                }

                HotfixManifest manifest = ComposeManifest(settings, contentVersion, luaVersion, bundleFileName, hash, size, resources);
                string manifestJson = JsonUtility.ToJson(manifest, true);

                string serverManifestPath = Path.Combine(settings.outputFolder, "manifest.json");
                WriteAllText(serverManifestPath, manifestJson);

                WriteBundledBaseline(manifestJson, bundleOutputPath);

                SaveContentVersion(contentVersion);
                AssetDatabase.Refresh();

                Debug.Log("[Hotfix] 构建完成\n" +
                          "  contentVersion : " + contentVersion + "\n" +
                          "  luaVersion     : " + luaVersion + "\n" +
                          "  bundle         : " + bundleOutputPath + " (" + size + " bytes)\n" +
                          "  sha256         : " + hash + "\n" +
                          "  manifest       : " + serverManifestPath);

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Hotfix] 构建异常: " + e);
                return false;
            }
        }

        /// <summary>把工程根目录 Lua/ 下的源码转换成 .lua.txt，作为打包输入。</summary>
        private static bool ConvertLuaSources(HotfixBuildSettings buildSettings)
        {
            string projectRoot = LuaToTextConverter.GetProjectRoot();
            var convertSettings = new LuaToTextSettings
            {
                sourceFolder = LuaToTextConverter.GetDefaultSourceFolder(),
                outputFolder = Path.GetFullPath(Path.Combine(projectRoot, buildSettings.luaAssetFolder)),
                recursive = true,
                keepDirectoryStructure = true,
                overwrite = true,
                deleteStale = true,
                selectOutputAfterConvert = false
            };

            LuaToTextResult result = LuaToTextConverter.Convert(convertSettings);
            if (result == null || result.errors.Count > 0)
            {
                Debug.LogError("[Hotfix] Lua → Text 转换失败");
                return false;
            }

            Debug.Log("[Hotfix] Lua → Text：转换 " + result.converted + "，跳过 " + result.skipped +
                      "，清理 " + result.deleted);
            return true;
        }

        private static bool BuildLuaBundle(string[] files, string luaVersion, string outputPath)
        {
            var assetNames = new List<string>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                assetNames.Add(files[i].Replace('\\', '/'));
            }

            var build = new AssetBundleBuild
            {
                assetBundleName = "lua",
                assetNames = assetNames.ToArray()
            };

            string tempDir = Path.Combine(Path.GetTempPath(), "hotfix_lua_" + luaVersion);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            AssetBundleManifest built = BuildPipeline.BuildAssetBundles(
                tempDir,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);

            if (built == null)
            {
                Debug.LogError("[Hotfix] AssetBundle 构建失败（目标平台: " + EditorUserBuildSettings.activeBuildTarget + "）");
                return false;
            }

            string builtFile = Path.Combine(tempDir, "lua");
            if (!File.Exists(builtFile))
            {
                Debug.LogError("[Hotfix] 未生成 bundle 文件: " + builtFile);
                return false;
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(builtFile, outputPath);
            Directory.Delete(tempDir, true);

            Debug.Log("[Hotfix] Lua 包共 " + assetNames.Count + " 个脚本，平台 " +
                      EditorUserBuildSettings.activeBuildTarget);
            return true;
        }

        private static HotfixManifest ComposeManifest(
            HotfixBuildSettings settings, int contentVersion, string luaVersion, string bundleFileName, string hash, long size,
            ResourceSection resources)
        {
            var manifest = new HotfixManifest
            {
                manifestVersion = HotfixManifest.CurrentManifestVersion,
                contentVersion = contentVersion,
                channel = settings.channel,
                createdAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            manifest.appVersionRange.min = settings.appVersionMin;
            manifest.appVersionRange.max = settings.appVersionMax;

            manifest.scripts.version = luaVersion;
            manifest.scripts.packageUrl = settings.cdnBaseUrl.TrimEnd('/') + "/" + bundleFileName;
            manifest.scripts.packageHash = hash;
            manifest.scripts.size = size;
            manifest.scripts.entryModule = settings.entryModule;

            manifest.resources = resources != null ? resources : new ResourceSection();
            return manifest;
        }

        /// <summary>
        /// 构建 Addressables 远端内容，并把 catalog 地址与分组信息回填到清单。
        /// 远端产物落在 ServerData/hotfix/aa/[BuildTarget]，与 Lua 包一起上传 CDN。
        /// </summary>
        private static bool BuildAddressables(
            HotfixBuildSettings settings, int contentVersion, out ResourceSection section, out string error)
        {
            section = new ResourceSection();
            error = null;

            AddressableAssetSettings aaSettings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (aaSettings == null)
            {
                error = "无法获取 AddressableAssetSettings（Addressables 未初始化）";
                return false;
            }

            string target = EditorUserBuildSettings.activeBuildTarget.ToString();
            string cdn = settings.cdnBaseUrl.TrimEnd('/');

            // 构建到本地产物目录，按平台分目录；运行期从 CDN 同路径读取
            aaSettings.profileSettings.SetValue(aaSettings.activeProfileId,
                AddressableAssetSettings.kRemoteBuildPath, "ServerData/hotfix/aa/[BuildTarget]");
            aaSettings.profileSettings.SetValue(aaSettings.activeProfileId,
                AddressableAssetSettings.kRemoteLoadPath, cdn + "/aa/[BuildTarget]");

            AddressableAssetGroup group = aaSettings.FindGroup(settings.resourceGroupName);
            if (group == null)
            {
                group = aaSettings.CreateGroup(settings.resourceGroupName, false, false, true, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            }

            BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
            {
                error = "资源组缺少 BundledAssetGroupSchema: " + settings.resourceGroupName;
                return false;
            }

            schema.BuildPath.SetVariableByName(aaSettings, AddressableAssetSettings.kRemoteBuildPath);
            schema.LoadPath.SetVariableByName(aaSettings, AddressableAssetSettings.kRemoteLoadPath);

            int marked = 0;
            if (settings.resourceAssetFolders != null)
            {
                for (int f = 0; f < settings.resourceAssetFolders.Length; f++)
                {
                    string folder = settings.resourceAssetFolders[f];
                    if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) continue;

                    foreach (string guid in AssetDatabase.FindAssets("t:Object", new[] { folder }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;

                        AddressableAssetEntry entry = aaSettings.CreateOrMoveEntry(guid, group, false, true);
                        if (entry == null) continue;

                        // label 用组名：运行期按 label 查询下载量与下载依赖
                        entry.SetLabel(settings.resourceGroupName, true, true, true);
                        marked++;
                    }
                }
            }

            if (marked == 0)
            {
                Debug.LogWarning("[Hotfix] 资源组内没有任何资产，资源通道将只有空 catalog: " + settings.resourceGroupName);
            }

            EditorUtility.SetDirty(aaSettings);
            AssetDatabase.SaveAssets();

            AddressablesPlayerBuildResult result;
            AddressableAssetSettings.BuildPlayerContent(out result);

            if (result == null || !string.IsNullOrEmpty(result.Error))
            {
                error = result != null ? result.Error : "Addressables 构建无返回结果";
                return false;
            }

            // 注意：result.OutputPath 指向 Library 下的 settings.json，不是内容目录。
            // catalog 由 Addressables 写到 Library/com.unity.addressables/aa 下，需要我们自己搬到上传目录。
            string catalogSource = FindLatestCatalogRecursive(Path.Combine("Library", "com.unity.addressables", "aa"));
            if (catalogSource == null)
            {
                error = "未找到 catalog 文件（Library/com.unity.addressables/aa）";
                return false;
            }

            string destDir = Path.Combine("ServerData/hotfix/aa", target);
            Directory.CreateDirectory(destDir);

            // catalog.json 与 catalog.hash 要一起上传，运行期会去取同名 .hash 做校验
            string sourceDir = Path.GetDirectoryName(catalogSource);
            if (!string.IsNullOrEmpty(sourceDir))
            {
                foreach (string file in Directory.GetFiles(sourceDir, "catalog*"))
                {
                    File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
                }
            }

            string catalogName = Path.GetFileName(catalogSource);
            string destCatalog = Path.Combine(destDir, catalogName);

            string hashSource = catalogSource + ".hash";
            if (!File.Exists(hashSource))
            {
                string siblingHash = Path.Combine(sourceDir ?? string.Empty, "catalog.hash");
                if (File.Exists(siblingHash)) hashSource = siblingHash;
            }

            string catalogHash = File.Exists(hashSource)
                ? File.ReadAllText(hashSource).Trim()
                : VerifyService.ComputeSha256Hex(destCatalog);

            section.catalogUrl = cdn + "/aa/" + target + "/" + catalogName;
            section.catalogHash = catalogHash;
            section.groups = new[]
            {
                new ResourceGroupInfo
                {
                    name = settings.resourceGroupName,
                    version = contentVersion.ToString(),
                    // 真实大小在运行期用 Addressables.GetDownloadSizeAsync 现算，避免构建期统计偏差
                    size = 0,
                    hash = string.Empty
                }
            };

            Debug.Log("[Hotfix] Addressables 构建完成\n" +
                      "  平台      : " + target + "\n" +
                      "  资产数    : " + marked + "\n" +
                      "  catalog   : " + section.catalogUrl + "\n" +
                      "  本地产物  : " + destCatalog + "\n" +
                      "  hash      : " + catalogHash);

            return true;
        }

        private static string FindLatestCatalogRecursive(string root)
        {
            if (!Directory.Exists(root)) return null;

            string latest = null;
            DateTime latestTime = DateTime.MinValue;

            foreach (string file in Directory.GetFiles(root, "catalog*.json", SearchOption.AllDirectories))
            {
                DateTime time = File.GetLastWriteTimeUtc(file);
                if (latest == null || time > latestTime)
                {
                    latest = file;
                    latestTime = time;
                }
            }

            return latest;
        }

        private static void WriteBundledBaseline(string manifestJson, string bundlePath)
        {
            string streamingDir = Path.Combine(Application.streamingAssetsPath, "hotfix");
            Directory.CreateDirectory(streamingDir);

            WriteAllText(Path.Combine(streamingDir, "manifest.json"), manifestJson);
            File.Copy(bundlePath, Path.Combine(streamingDir, "lua.bundle"), true);

            Debug.Log("[Hotfix] 出厂基线已更新: " + streamingDir);
        }

        /// <summary>重新取配置资产再写入版本号：避免持有已被 AssetDatabase 刷新作废的引用。</summary>
        private static void SaveContentVersion(int contentVersion)
        {
            HotfixBuildSettings settings = AssetDatabase.LoadAssetAtPath<HotfixBuildSettings>(SettingsPath);
            if (settings == null)
            {
                Debug.LogWarning("[Hotfix] 保存 contentVersion 失败：配置资产不存在");
                return;
            }

            settings.contentVersion = contentVersion;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        private static void WriteAllText(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private static HotfixBuildSettings LoadOrCreateSettings()
        {
            HotfixBuildSettings settings = AssetDatabase.LoadAssetAtPath<HotfixBuildSettings>(SettingsPath);
            if (settings != null) return settings;

            string directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            settings = ScriptableObject.CreateInstance<HotfixBuildSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);
            AssetDatabase.SaveAssets();

            // CreateAsset 之后要重新取一次：Refresh/Save 会让创建时的托管引用变成 fake-null
            settings = AssetDatabase.LoadAssetAtPath<HotfixBuildSettings>(SettingsPath);
            if (settings == null)
            {
                Debug.LogError("[Hotfix] 构建配置创建失败: " + SettingsPath);
                return null;
            }

            Debug.Log("[Hotfix] 已创建构建配置: " + SettingsPath);
            return settings;
        }
    }
}
