using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Lua 脚本数据来源。实现类只需要回答"这个相对路径能否读到内容"，不关心 Lua 运行时。
/// </summary>
public interface ILuaScriptProvider
{
    /// <summary>
    /// 按候选路径读取脚本。
    /// </summary>
    /// <param name="candidatePath">候选相对路径，例如 "Main.lua"、"common/util.lua.txt"。</param>
    /// <param name="resolvedPath">命中后用于 Lua 堆栈显示的路径（尽量给绝对路径）。</param>
    /// <returns>脚本字节；没找到返回 null，交给下一个 Provider。</returns>
    byte[] Read(string candidatePath, out string resolvedPath);
}

/// <summary>
/// 脚本定位器：按注册顺序遍历所有 Provider，并负责生成候选文件名。
/// 支持 xLua 常见的 require("a.b") 写法（点号换算成目录分隔符），
/// 也支持 ".lua" 与 ".lua.txt" 两种后缀（后者便于放进 AssetBundle / TextAsset）。
/// </summary>
public sealed class LuaScriptLoader
{
    private readonly List<ILuaScriptProvider> _providers = new List<ILuaScriptProvider>(4);
    private readonly List<string> _candidates = new List<string>(3);

    /// <summary>是否打印命中日志（跟随 Debug 模式）。</summary>
    public bool Verbose { get; set; }

    public int ProviderCount
    {
        get { return _providers.Count; }
    }

    public void AddProvider(ILuaScriptProvider provider)
    {
        if (provider == null) return;
        _providers.Add(provider);
    }

    public void ClearProviders()
    {
        _providers.Clear();
    }

    /// <summary>
    /// 读取脚本。
    /// </summary>
    /// <param name="moduleOrPath">require 的模块名（"Main"、"a.b"）或文件路径（"Main.lua"）。</param>
    /// <param name="resolvedPath">命中后的实际路径。</param>
    /// <returns>找不到时返回 null。</returns>
    public byte[] Load(string moduleOrPath, out string resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrEmpty(moduleOrPath) || _providers.Count == 0) return null;

        BuildCandidates(moduleOrPath, _candidates);

        for (int c = 0; c < _candidates.Count; c++)
        {
            string candidate = _candidates[c];
            for (int p = 0; p < _providers.Count; p++)
            {
                string resolved;
                byte[] bytes = _providers[p].Read(candidate, out resolved);
                if (bytes == null || bytes.Length == 0) continue;

                resolvedPath = string.IsNullOrEmpty(resolved) ? candidate : resolved;
                if (Verbose) Debug.Log("[XLua] 脚本命中: " + moduleOrPath + " -> " + resolvedPath);
                return bytes;
            }
        }

        return null;
    }

    /// <summary>
    /// 根据模块名/文件名生成候选路径（顺序即优先级）。
    /// </summary>
    public static void BuildCandidates(string moduleOrPath, List<string> candidates)
    {
        candidates.Clear();
        if (string.IsNullOrEmpty(moduleOrPath)) return;

        string path = moduleOrPath.Trim();
        if (path.Length == 0) return;

        if (HasLuaExtension(path))
        {
            AddUnique(candidates, path);
            // 允许传 "Main.lua" 时命中实际的 "Main.lua.txt"
            if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(candidates, path + ".txt");
            }
            return;
        }

        // require("a.b") 约定：点号是目录分隔符
        string normalized = path.Replace('.', '/');
        AddUnique(candidates, normalized + ".lua");
        AddUnique(candidates, normalized + ".lua.txt");
        AddUnique(candidates, normalized);
    }

    private static bool HasLuaExtension(string path)
    {
        return path.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUnique(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return;
        }
        list.Add(value);
    }
}

/// <summary>
/// 从磁盘目录读取脚本，用于开发期的工程目录、persistentDataPath 热更目录、StreamingAssets 目录。
/// </summary>
public sealed class FileSystemLuaProvider : ILuaScriptProvider
{
    private readonly string _rootDirectory;

    public FileSystemLuaProvider(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    public string RootDirectory
    {
        get { return _rootDirectory; }
    }

    public byte[] Read(string candidatePath, out string resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrEmpty(_rootDirectory) || string.IsNullOrEmpty(candidatePath)) return null;

#if UNITY_ANDROID && !UNITY_EDITOR
        // Android 的 StreamingAssets 在 apk 内部（jar://），File API 读不到，
        // 请改用 AssetBundle 或先把脚本解压到 persistentDataPath。
        if (_rootDirectory.IndexOf("://", StringComparison.Ordinal) >= 0) return null;
#endif

        try
        {
            char separator = Path.DirectorySeparatorChar;
            string relative = candidatePath.Replace('/', separator).Replace('\\', separator);
            string fullPath = Path.Combine(_rootDirectory, relative);

            if (!File.Exists(fullPath)) return null;

            byte[] bytes = File.ReadAllBytes(fullPath);
            resolvedPath = fullPath;
            return bytes;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[XLua] 读取 Lua 脚本失败: " + candidatePath + " -> " + e.Message);
            return null;
        }
    }
}

/// <summary>
/// 从已加载的 AssetBundle 读取脚本（打包后 / 热更时的推荐方式）。
/// Bundle 中的 Lua 脚本应为 TextAsset，后缀为 .lua 或 .lua.txt。
/// </summary>
public sealed class AssetBundleLuaProvider : ILuaScriptProvider
{
    private readonly Dictionary<string, TextAsset> _assets =
        new Dictionary<string, TextAsset>(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _assetNames = new List<string>();

    public AssetBundleLuaProvider(AssetBundle bundle)
    {
        if (bundle == null) throw new ArgumentNullException("bundle");

        string[] names = bundle.GetAllAssetNames();
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (!IsLuaAsset(name)) continue;

            TextAsset asset = bundle.LoadAsset<TextAsset>(name);
            if (asset == null) continue;

            _assets[name] = asset;
            _assetNames.Add(name);
        }
    }

    public int Count
    {
        get { return _assetNames.Count; }
    }

    public byte[] Read(string candidatePath, out string resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrEmpty(candidatePath) || _assetNames.Count == 0) return null;

        for (int i = 0; i < _assetNames.Count; i++)
        {
            string assetName = _assetNames[i];
            // Bundle 内路径通常是全小写的 "assets/lua/main.lua.txt"，这里做后缀匹配
            if (assetName.Equals(candidatePath, StringComparison.OrdinalIgnoreCase)
                || assetName.EndsWith("/" + candidatePath, StringComparison.OrdinalIgnoreCase))
            {
                TextAsset asset;
                if (!_assets.TryGetValue(assetName, out asset) || asset == null) return null;

                resolvedPath = assetName;
                return asset.bytes;
            }
        }

        return null;
    }

    private static bool IsLuaAsset(string assetName)
    {
        return assetName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            || assetName.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase);
    }
}
