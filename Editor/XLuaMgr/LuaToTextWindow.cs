using System.IO;
using UnityEditor;
using UnityEngine;

namespace XLuaTools
{
    /// <summary>
    /// Lua → Text 批量转换窗口。
    /// 菜单：Tools/XLua/Lua 转 Text 工具
    /// </summary>
    public class LuaToTextWindow : EditorWindow
    {
        private const string PrefSource = "XLuaTools.LuaToText.SourceFolder";
        private const string PrefOutput = "XLuaTools.LuaToText.OutputFolder";
        private const string PrefRecursive = "XLuaTools.LuaToText.Recursive";
        private const string PrefKeepStructure = "XLuaTools.LuaToText.KeepStructure";
        private const string PrefOverwrite = "XLuaTools.LuaToText.Overwrite";
        private const string PrefDeleteStale = "XLuaTools.LuaToText.DeleteStale";
        private const string PrefBundleName = "XLuaTools.LuaToText.BundleName";
        private const string PrefSelectOutput = "XLuaTools.LuaToText.SelectOutput";

        private LuaToTextSettings _settings;
        private LuaToTextResult _lastResult;
        private Vector2 _resultScroll;

        private int _sourceFileCount;
        private string _countedFolder;

        [MenuItem("Tools/XLua/Lua 转 Text 工具", false, 100)]
        public static void Open()
        {
            LuaToTextWindow window = GetWindow<LuaToTextWindow>(false, "Lua → Text", true);
            window.minSize = new Vector2(580, 480);
            window.Show();
        }

        [MenuItem("Tools/XLua/快速转换 Lua → Text（使用上次配置）", false, 101)]
        public static void QuickConvert()
        {
            LuaToTextSettings settings = LoadSettings();
            LuaToTextResult result = LuaToTextConverter.Convert(settings);
            result.LogToConsole();
            SaveSettings(settings);
        }

        private void OnEnable()
        {
            _settings = LoadSettings();
            RefreshFileCount();
        }

        private void OnGUI()
        {
            if (_settings == null) _settings = LoadSettings();

            EditorGUILayout.LabelField("Lua → Text 批量转换", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "把 .lua 脚本复制成 .lua.txt（Unity 识别为 TextAsset），用于打包 AssetBundle / StreamingAssets。\n" +
                "支持保持目录结构，并可清理输出目录中已没有源文件的 .lua.txt。",
                MessageType.Info);

            EditorGUILayout.Space();
            DrawFolderRow("源目录", ref _settings.sourceFolder, "选择 Lua 脚本目录");
            DrawFolderRow("输出目录", ref _settings.outputFolder, "选择输出目录（建议放在 Assets 下）");

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(114);
            if (GUILayout.Button("恢复默认路径", GUILayout.Width(110)))
            {
                _settings.sourceFolder = LuaToTextConverter.GetDefaultSourceFolder();
                _settings.outputFolder = LuaToTextConverter.GetDefaultOutputFolder();
                OnSettingsChanged();
            }
            if (GUILayout.Button("打开输出目录", GUILayout.Width(110)))
            {
                OpenFolder(_settings.outputFolder);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(114);
            if (GUILayout.Button("一键创建示例脚本 Lua/Main.lua", GUILayout.Width(220)))
            {
                LuaSampleScriptCreator.CreateWithDialog(_settings.sourceFolder);
                RefreshFileCount();
                Repaint();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("找到 " + _sourceFileCount + " 个 .lua 文件" +
                                    (string.IsNullOrEmpty(_countedFolder) ? string.Empty : "\n" + _countedFolder),
                _sourceFileCount > 0 ? MessageType.None : MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("选项", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _settings.recursive = EditorGUILayout.ToggleLeft("包含子目录", _settings.recursive);
            _settings.keepDirectoryStructure = EditorGUILayout.ToggleLeft("保持目录结构（关闭则全部平铺到输出目录）", _settings.keepDirectoryStructure);
            _settings.overwrite = EditorGUILayout.ToggleLeft("覆盖已存在的 .lua.txt", _settings.overwrite);
            _settings.deleteStale = EditorGUILayout.ToggleLeft("清理输出目录中多余的 .lua.txt（避免打包已删除的脚本）", _settings.deleteStale);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("AssetBundle 名称（可选）", GUILayout.Width(160));
            _settings.assetBundleName = EditorGUILayout.TextField(_settings.assetBundleName);
            EditorGUILayout.EndHorizontal();

            _settings.selectOutputAfterConvert = EditorGUILayout.ToggleLeft("转换完成后在 Project 窗口选中输出目录", _settings.selectOutputAfterConvert);

            if (EditorGUI.EndChangeCheck())
            {
                OnSettingsChanged();
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("开始转换", GUILayout.Height(32)))
            {
                RunConvert();
            }
            if (GUILayout.Button("刷新文件数", GUILayout.Height(32), GUILayout.Width(100)))
            {
                RefreshFileCount();
            }
            EditorGUILayout.EndHorizontal();

            if (_lastResult != null)
            {
                DrawResult();
            }
        }

        private void DrawFolderRow(string label, ref string value, string browseTitle)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(110));

            EditorGUI.BeginChangeCheck();
            value = EditorGUILayout.TextField(value);
            if (EditorGUI.EndChangeCheck())
            {
                OnSettingsChanged();
            }

            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                string start = (!string.IsNullOrEmpty(value) && Directory.Exists(value))
                    ? value
                    : LuaToTextConverter.GetProjectRoot();

                string picked = EditorUtility.OpenFolderPanel(browseTitle, start, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    value = picked;
                    OnSettingsChanged();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawResult()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("上次结果", EditorStyles.boldLabel);

            string summary = string.Format("转换 {0} 个，跳过 {1} 个，清理 {2} 个{3}",
                _lastResult.converted, _lastResult.skipped, _lastResult.deleted,
                _lastResult.canceled ? "（已取消）" : string.Empty);

            EditorGUILayout.HelpBox(summary + "\n输出目录: " + _lastResult.outputFolder,
                _lastResult.errors.Count > 0 ? MessageType.Warning : MessageType.None);

            if (_lastResult.errors.Count == 0) return;

            _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.Height(110));
            for (int i = 0; i < _lastResult.errors.Count; i++)
            {
                EditorGUILayout.LabelField(_lastResult.errors[i], EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void RunConvert()
        {
            SaveSettings(_settings);

            _lastResult = LuaToTextConverter.Convert(_settings);
            _lastResult.LogToConsole();

            RefreshFileCount();
            Repaint();
        }

        private void OnSettingsChanged()
        {
            RefreshFileCount();
            SaveSettings(_settings);
        }

        private void RefreshFileCount()
        {
            if (_settings == null) return;

            _countedFolder = _settings.sourceFolder;
            _sourceFileCount = string.IsNullOrEmpty(_countedFolder)
                ? 0
                : LuaToTextConverter.CollectLuaFiles(_countedFolder, _settings.recursive).Count;
        }

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        private static LuaToTextSettings LoadSettings()
        {
            return new LuaToTextSettings
            {
                sourceFolder = EditorPrefs.GetString(PrefSource, LuaToTextConverter.GetDefaultSourceFolder()),
                outputFolder = EditorPrefs.GetString(PrefOutput, LuaToTextConverter.GetDefaultOutputFolder()),
                recursive = EditorPrefs.GetBool(PrefRecursive, true),
                keepDirectoryStructure = EditorPrefs.GetBool(PrefKeepStructure, true),
                overwrite = EditorPrefs.GetBool(PrefOverwrite, true),
                deleteStale = EditorPrefs.GetBool(PrefDeleteStale, false),
                assetBundleName = EditorPrefs.GetString(PrefBundleName, string.Empty),
                selectOutputAfterConvert = EditorPrefs.GetBool(PrefSelectOutput, true)
            };
        }

        private static void SaveSettings(LuaToTextSettings settings)
        {
            if (settings == null) return;

            EditorPrefs.SetString(PrefSource, settings.sourceFolder ?? string.Empty);
            EditorPrefs.SetString(PrefOutput, settings.outputFolder ?? string.Empty);
            EditorPrefs.SetBool(PrefRecursive, settings.recursive);
            EditorPrefs.SetBool(PrefKeepStructure, settings.keepDirectoryStructure);
            EditorPrefs.SetBool(PrefOverwrite, settings.overwrite);
            EditorPrefs.SetBool(PrefDeleteStale, settings.deleteStale);
            EditorPrefs.SetString(PrefBundleName, settings.assetBundleName ?? string.Empty);
            EditorPrefs.SetBool(PrefSelectOutput, settings.selectOutputAfterConvert);
        }
    }
}
