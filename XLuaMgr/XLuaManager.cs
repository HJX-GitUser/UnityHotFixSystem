using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using XLua;

/// <summary>
/// xLua 运行时管理器。
///
/// 职责：
/// 1. 单例 + DontDestroyOnLoad，LuaEnv 生命周期与 Unity 进程对齐；
/// 2. 自定义 Loader：Debug 期读工程根目录的 Lua 文件夹，发布期读 persistentDataPath / StreamingAssets / AssetBundle；
/// 3. 封装 SafeDoString / DoFile / Require / GetGlobal / CallLuaFunction；
/// 4. 驱动 Lua 侧注册的 Update / LateUpdate / FixedUpdate（未注册就不调用，零额外开销）；
/// 5. Reload(module) 支持热重载：清 package.loaded + 丢弃回调 + 重新 require，不重启 LuaEnv；
/// 6. 所有 Lua 调用统一 try/catch，完整打印堆栈（含 Lua 行号），并抛出 OnLuaError 给上层。
///
/// 注意：热重载只重跑 Lua 脚本；已经通过 <see cref="GetGlobal{T}"/>/反射持有旧闭包的 C# 代码不会自动更新，
/// 业务逻辑应通过模块 + 生命周期回调的方式接入，才能被 Reload 正确替换。
/// </summary>
[LuaCallCSharp]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-10000)]
public class XLuaManager : MonoBehaviour
{
    private const string DefaultModuleName = "__global__";

    /// <summary>Update 等无参回调复用同一个空数组，避免每帧 GC。</summary>
    private static readonly object[] NoArgs = new object[0];

    #region 单例

    /// <summary>全局唯一实例；场景里没有时会自动创建。</summary>
    public static XLuaManager Instance { get; private set; }

    /// <summary>
    /// 由外部引导流程（BootLoader）接管创建时置 true，
    /// 避免场景加载后自动创建并立刻执行 Lua 入口。
    /// </summary>
    public static bool SuppressAutoCreate { get; set; }

    /// <summary>下次创建时使用的 Lua 根目录（热更版本目录），由引导流程在创建前设置。</summary>
    public static string PendingLuaRoot { get; set; }

    /// <summary>下次创建时不自动执行 mainModule（由引导流程自己决定时机）。</summary>
    public static bool PendingSuppressAutoRunMain { get; set; }

    /// <summary>LuaEnv 是否可用。</summary>
    public static bool IsReady
    {
        get { return Instance != null && Instance._luaEnv != null; }
    }

    /// <summary>
    /// 关闭 Domain Reload 时静态字段不会被重置，这里手动清理，避免残留野指针。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        OnLuaError = null;
        SuppressAutoCreate = false;
        PendingLuaRoot = null;
        PendingSuppressAutoRunMain = false;
    }

    /// <summary>
    /// 场景里没有手动挂载时，在首个场景加载完成后自动创建，保证任何脚本都能直接访问。
    /// 如果场景中已放置带 XLuaManager 的对象，则使用场景里的那份（可在 Inspector 配置）。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (SuppressAutoCreate) return;
        if (Instance != null || !Application.isPlaying) return;

        var go = new GameObject("[XLuaManager]");
        go.AddComponent<XLuaManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[XLua] 已存在 XLuaManager，销毁重复实例: " + name);
            Destroy(gameObject);
            return;
        }

        Instance = this;
        transform.SetParent(null, false);
        DontDestroyOnLoad(gameObject);

        ConsumePendingBootstrap();
        RebuildScriptLoader();
        InitLuaEnv();

        if (autoRunMainModule && !_suppressAutoRunMain)
        {
            RunMainModule();
        }
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        Instance = null;
        DisposeLuaEnv();

        // LuaEnv 已经释放，此时才能安全卸载脚本包
        if (_hotfixBundle != null)
        {
            _hotfixBundle.Unload(true);
            _hotfixBundle = null;
        }
    }

    private void OnApplicationQuit()
    {
        DisposeLuaEnv();
    }

    #endregion

    #region Inspector 配置

    [Header("运行模式")]
    [Tooltip("Debug：优先加载工程根目录 Lua 文件夹（支持直接改脚本热重载）；Release：优先 AssetBundle / StreamingAssets")]
    [SerializeField]
    private bool debugMode =
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        true;
#else
        false;
#endif

    [Header("启动配置")]
    [Tooltip("启动后自动 require 的入口模块，留空则不执行")]
    [SerializeField] private string mainModule = "Main";

    [SerializeField] private bool autoRunMainModule = true;

    [Header("脚本路径")]
    [Tooltip("Lua 目录名。开发期为 工程根目录/<该名字>，发布期为 streamingAssetsPath/<该名字> 与 persistentDataPath/<该名字>")]
    [SerializeField] private string luaFolderName = "Lua";

    [Tooltip("是否允许从 persistentDataPath 读取（热更脚本落地目录）")]
    [SerializeField] private bool loadFromPersistentData = true;

    #endregion

    #region 状态

    private LuaEnv _luaEnv;
    private bool _tickFailed;

    private readonly LuaScriptLoader _loader = new LuaScriptLoader();
    private ILuaScriptProvider _bundleProvider;

    /// <summary>热更 Lua 目录（hotfix/lua/v&lt;version&gt;）；null 表示使用默认来源。</summary>
    private string _luaRootOverride;

    /// <summary>从热更包加载的 AssetBundle，进程内常驻（Lua chunk 会一直引用它）。</summary>
    private AssetBundle _hotfixBundle;

    /// <summary>本次创建是否跳过自动执行入口模块。</summary>
    private bool _suppressAutoRunMain;

    private readonly Dictionary<string, ModuleCallbacks> _moduleCallbacks =
        new Dictionary<string, ModuleCallbacks>();

    /// <summary>按注册顺序保存模块回调，逐帧遍历时零分配。</summary>
    private readonly List<ModuleCallbacks> _callbackList = new List<ModuleCallbacks>();

    private readonly Dictionary<string, LuaTable> _moduleCache = new Dictionary<string, LuaTable>();

    private int _updateCount;
    private int _lateUpdateCount;
    private int _fixedUpdateCount;

    /// <summary>底层 LuaEnv，慎用；常规调用请走封装接口。</summary>
    public LuaEnv LuaEnv
    {
        get { return _luaEnv; }
    }

    public LuaScriptLoader ScriptLoader
    {
        get { return _loader; }
    }

    /// <summary>最近一次 Lua 错误。</summary>
    public LuaErrorInfo LastError { get; private set; }

    /// <summary>Debug / Release 模式，切换后会重建脚本搜索顺序。</summary>
    public bool DebugMode
    {
        get { return debugMode; }
        set
        {
            if (debugMode == value) return;
            debugMode = value;
            RebuildScriptLoader();
            Debug.Log("[XLua] 已切换到 " + (debugMode ? "Debug" : "Release") + " 模式");
        }
    }

    public string MainModule
    {
        get { return mainModule; }
        set { mainModule = value; }
    }

    /// <summary>已注册的 Update 回调数量（诊断用）。</summary>
    public int UpdateCallbackCount
    {
        get { return _updateCount; }
    }

    #endregion

    #region 错误处理

    /// <summary>
    /// Lua 错误回调。上层可订阅它做上报 / 弹窗 / 降级处理。
    /// 注意：回调里不要再抛异常，内部已经做了保护。
    /// </summary>
    public static event Action<LuaErrorInfo> OnLuaError;

    public static void RegisterLuaErrorHandler(Action<LuaErrorInfo> handler)
    {
        if (handler != null) OnLuaError += handler;
    }

    public static void UnregisterLuaErrorHandler(Action<LuaErrorInfo> handler)
    {
        if (handler != null) OnLuaError -= handler;
    }

    public static void ClearLuaErrorHandlers()
    {
        OnLuaError = null;
    }

    private void HandleLuaError(Exception exception, string context)
    {
        RaiseLuaError(new LuaErrorInfo(context, exception));
    }

    private void RaiseLuaError(LuaErrorInfo error)
    {
        if (error == null) return;

        LastError = error;
        // Message 中已包含 Lua 的 "stack traceback:"，ToDisplayString 追加了 C# 调用栈
        Debug.LogError(error.ToDisplayString());

        Action<LuaErrorInfo> handlers = OnLuaError;
        if (handlers == null) return;

        Delegate[] invocationList = handlers.GetInvocationList();
        for (int i = 0; i < invocationList.Length; i++)
        {
            try
            {
                ((Action<LuaErrorInfo>)invocationList[i])(error);
            }
            catch (Exception e)
            {
                Debug.LogError("[XLua] OnLuaError 回调自身抛出异常: " + e);
            }
        }
    }

    #endregion

    #region 初始化 / 释放

    /// <summary>
    /// 创建 LuaEnv 并安装自定义 Loader。重复调用安全。
    /// </summary>
    public LuaEnv InitLuaEnv()
    {
        if (_luaEnv != null) return _luaEnv;

        try
        {
            _luaEnv = new LuaEnv();
            // 自定义 loader 优先于 xLua 内置的 Resources / StreamingAssets searcher；
            // 返回 null 时 xLua 会继续尝试内置 loader，因此不会破坏 require('xlua.util') 之类的内置模块。
            _luaEnv.AddLoader(LuaCustomLoader);
            _tickFailed = false;

            Debug.Log("[XLua] LuaEnv 创建完成（" + (debugMode ? "Debug" : "Release") + " 模式）");
        }
        catch (Exception e)
        {
            HandleLuaError(e, "创建 LuaEnv");
            _luaEnv = null;
        }

        return _luaEnv;
    }

    /// <summary>
    /// 释放 LuaEnv。会先清理所有 Lua 回调与模块缓存，避免释放带 C# 回调的 LuaEnv 触发 xLua 异常。
    /// </summary>
    public void DisposeLuaEnv()
    {
        ClearAllCallbacks();
        ClearModuleCache();

        LuaEnv env = _luaEnv;
        _luaEnv = null;
        _tickFailed = false;

        if (env == null) return;

        try
        {
            // 先让 Lua 侧跑完 GC，再关闭状态机
            env.Tick();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[XLua] 释放前 Tick 失败（可忽略）: " + e.Message);
        }

        try
        {
            env.Dispose();
            if (debugMode) Debug.Log("[XLua] LuaEnv 已释放");
        }
        catch (Exception e)
        {
            Debug.LogError("[XLua] 释放 LuaEnv 失败: " + e);
        }
    }

    /// <summary>
    /// 重建脚本搜索顺序：
    /// Debug ：工程目录 → persistentDataPath → AssetBundle → StreamingAssets
    /// Release：AssetBundle → persistentDataPath → StreamingAssets →（Editor 下兜底）工程目录
    /// </summary>
    private void RebuildScriptLoader()
    {
        _loader.ClearProviders();
        _loader.Verbose = debugMode;

        string devRoot = GetDevLuaRoot();
        string persistentRoot = !string.IsNullOrEmpty(_luaRootOverride)
            ? _luaRootOverride
            : (loadFromPersistentData ? Path.Combine(Application.persistentDataPath, luaFolderName) : null);
        string streamingRoot = string.IsNullOrEmpty(Application.streamingAssetsPath)
            ? null
            : Path.Combine(Application.streamingAssetsPath, luaFolderName);

        if (debugMode)
        {
            AddFileProvider(devRoot, "工程目录");
            AddFileProvider(persistentRoot, "persistentDataPath");
            AddBundleProvider();
            AddFileProvider(streamingRoot, "StreamingAssets");
        }
        else
        {
            AddBundleProvider();
            AddFileProvider(persistentRoot, "persistentDataPath");
            AddFileProvider(streamingRoot, "StreamingAssets");
            if (Application.isEditor) AddFileProvider(devRoot, "工程目录(Editor 兜底)");
        }
    }

    private void AddFileProvider(string root, string label)
    {
        if (string.IsNullOrEmpty(root)) return;

        _loader.AddProvider(new FileSystemLuaProvider(root));
        if (debugMode) Debug.Log("[XLua] 脚本来源: " + label + " -> " + root);
    }

    private void AddBundleProvider()
    {
        if (_bundleProvider == null) return;

        _loader.AddProvider(_bundleProvider);
        if (debugMode) Debug.Log("[XLua] 脚本来源: AssetBundle");
    }

    /// <summary>
    /// Application.dataPath 指向 &lt;工程根&gt;/Assets，向上一级就是工程根目录。
    /// </summary>
    private string GetDevLuaRoot()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // 设备上 dataPath 指向 apk，不存在可写的工程目录
        return null;
#else
        if (string.IsNullOrEmpty(luaFolderName)) return null;
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, luaFolderName);
#endif
    }

    /// <summary>
    /// 交给 xLua 的自定义 Loader。filepath 会被 xLua 用作 chunk name，
    /// 命中后替换成真实路径，这样 Lua 报错时能直接看到磁盘位置与行号。
    /// </summary>
    private byte[] LuaCustomLoader(ref string filepath)
    {
        try
        {
            string resolvedPath;
            byte[] bytes = _loader.Load(filepath, out resolvedPath);
            if (bytes == null) return null;

            filepath = resolvedPath;
            return bytes;
        }
        catch (Exception e)
        {
            Debug.LogError("[XLua] 自定义 Loader 异常: " + e);
            return null;
        }
    }

    /// <summary>
    /// 指定 AssetBundle 作为脚本来源（如果已有 Provider 会被替换）。
    /// Bundle 内脚本需要是 TextAsset，例如 "assets/lua/main.lua.txt"。
    /// </summary>
    public void AssignAssetBundle(AssetBundle bundle)
    {
        _bundleProvider = bundle != null ? new AssetBundleLuaProvider(bundle) : null;
        RebuildScriptLoader();
    }

    /// <summary>
    /// 使用自定义 Provider（例如 Addressables 加载完再喂进来）。
    /// </summary>
    public void AssignLuaProvider(ILuaScriptProvider provider)
    {
        _bundleProvider = provider;
        RebuildScriptLoader();
    }

    /// <summary>
    /// 运行时切换 Lua 根目录（热更版本目录）。
    /// 目录里存在 lua.bundle 时按 AssetBundle 读取，否则按普通文件读取。
    /// 切换后已 require 的模块不会自动重跑，需要的话再调用 <see cref="Reload"/>。
    /// </summary>
    public void SetLuaRootOverride(string directory)
    {
        _luaRootOverride = string.IsNullOrEmpty(directory) ? null : directory;
        if (!string.IsNullOrEmpty(_luaRootOverride)) EnsureHotfixBundleLoaded();
        RebuildScriptLoader();
    }

    /// <summary>消费引导流程在创建前排队的配置。</summary>
    private void ConsumePendingBootstrap()
    {
        if (!string.IsNullOrEmpty(PendingLuaRoot))
        {
            _luaRootOverride = PendingLuaRoot;
            PendingLuaRoot = null;
        }

        _suppressAutoRunMain = PendingSuppressAutoRunMain;
        PendingSuppressAutoRunMain = false;

        if (!string.IsNullOrEmpty(_luaRootOverride))
        {
            EnsureHotfixBundleLoaded();
        }
    }

    /// <summary>热更目录里有 lua.bundle 就挂载它；没有则该目录按普通文件读取。</summary>
    private void EnsureHotfixBundleLoaded()
    {
        if (_hotfixBundle != null) return;

        string bundlePath = Path.Combine(_luaRootOverride, "lua.bundle");
        if (!File.Exists(bundlePath)) return;

        try
        {
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogWarning("[XLua] 加载 Lua AssetBundle 失败: " + bundlePath);
                return;
            }

            _hotfixBundle = bundle;
            _bundleProvider = new AssetBundleLuaProvider(bundle);
            Debug.Log("[XLua] Lua 热更包已挂载: " + bundlePath);
        }
        catch (Exception e)
        {
            Debug.LogError("[XLua] 加载 Lua AssetBundle 异常: " + e);
        }
    }

    #endregion

    #region 对外接口

    /// <summary>
    /// 执行一段 Lua 代码，异常不会向外抛。
    /// </summary>
    /// <param name="code">Lua 源码。</param>
    /// <param name="chunkName">用于错误堆栈的名字，留空时使用 "chunk"。</param>
    /// <returns>Lua 返回值数组；失败返回 null。</returns>
    public object[] SafeDoString(string code, string chunkName = "")
    {
        if (string.IsNullOrEmpty(code)) return null;

        if (_luaEnv == null)
        {
            Debug.LogWarning("[XLua] LuaEnv 未初始化，忽略 SafeDoString");
            return null;
        }

        string chunk = string.IsNullOrEmpty(chunkName) ? "chunk" : chunkName;
        try
        {
            return _luaEnv.DoString(code, chunk);
        }
        catch (Exception e)
        {
            HandleLuaError(e, "DoString(" + chunk + ")");
            return null;
        }
    }

    /// <summary>
    /// 执行一个 Lua 文件。filePath 可以是相对路径（"Main.lua" / "sub/Util"），
    /// 会按当前 Provider 顺序查找，支持 .lua 与 .lua.txt。
    /// </summary>
    public bool DoFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;

        if (_luaEnv == null)
        {
            Debug.LogWarning("[XLua] LuaEnv 未初始化，忽略 DoFile: " + filePath);
            return false;
        }

        try
        {
            string resolvedPath;
            byte[] bytes = _loader.Load(filePath, out resolvedPath);
            if (bytes == null)
            {
                RaiseLuaError(new LuaErrorInfo("DoFile(" + filePath + ")",
                    new FileNotFoundException("找不到 Lua 脚本: " + filePath)));
                return false;
            }

            _luaEnv.DoString(bytes, "@" + resolvedPath);
            return true;
        }
        catch (Exception e)
        {
            HandleLuaError(e, "DoFile(" + filePath + ")");
            return false;
        }
    }

    /// <summary>
    /// require 一个模块（内部 return require "xxx"），结果原样返回。
    /// </summary>
    public object[] Require(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName)) return null;

        if (_luaEnv == null)
        {
            Debug.LogWarning("[XLua] LuaEnv 未初始化，忽略 Require: " + moduleName);
            return null;
        }

        string code = "return require('" + EscapeLuaString(moduleName) + "')";
        try
        {
            return _luaEnv.DoString(code, "@require:" + moduleName);
        }
        catch (Exception e)
        {
            HandleLuaError(e, "Require(" + moduleName + ")");
            return null;
        }
    }

    /// <summary>
    /// 读取 Lua 全局变量。T 为委托类型时需要在 xLua 生成列表里注册（CSharpCallLua）。
    /// </summary>
    public T GetGlobal<T>(string name)
    {
        if (_luaEnv == null || string.IsNullOrEmpty(name)) return default(T);

        try
        {
            return _luaEnv.Global.Get<T>(name);
        }
        catch (Exception e)
        {
            HandleLuaError(e, "GetGlobal<" + typeof(T).Name + ">(" + name + ")");
            return default(T);
        }
    }

    /// <summary>
    /// 设置 Lua 全局变量（常用于把 C# 对象注入 Lua）。
    /// </summary>
    public bool SetGlobal<T>(string name, T value)
    {
        if (_luaEnv == null || string.IsNullOrEmpty(name)) return false;

        try
        {
            _luaEnv.Global.Set(name, value);
            return true;
        }
        catch (Exception e)
        {
            HandleLuaError(e, "SetGlobal(" + name + ")");
            return false;
        }
    }

    /// <summary>
    /// 调用模块中的函数。
    /// </summary>
    /// <param name="module">require 的模块名，如 "Main"。</param>
    /// <param name="func">函数名，支持点号路径，如 "Sub.Update"。</param>
    /// <param name="args">参数，可空。</param>
    /// <returns>Lua 返回值数组；失败返回 null。</returns>
    public object[] CallLuaFunction(string module, string func, params object[] args)
    {
        if (_luaEnv == null)
        {
            Debug.LogWarning("[XLua] LuaEnv 未初始化，忽略 CallLuaFunction: " + module + "." + func);
            return null;
        }

        if (string.IsNullOrEmpty(module) || string.IsNullOrEmpty(func))
        {
            Debug.LogWarning("[XLua] CallLuaFunction 需要模块名与函数名");
            return null;
        }

        LuaFunction function = null;
        try
        {
            LuaTable table = GetModuleTable(module);
            if (table == null)
            {
                RaiseLuaError(new LuaErrorInfo("CallLuaFunction(" + module + "." + func + ")",
                    new LuaException("模块 '" + module + "' 没有返回 table")));
                return null;
            }

            function = table.GetInPath<LuaFunction>(func);
            if (function == null)
            {
                RaiseLuaError(new LuaErrorInfo("CallLuaFunction(" + module + "." + func + ")",
                    new LuaException("模块 '" + module + "' 中找不到函数 '" + func + "'")));
                return null;
            }

            return function.Call(args);
        }
        catch (Exception e)
        {
            HandleLuaError(e, "CallLuaFunction(" + module + "." + func + ")");
            return null;
        }
        finally
        {
            SafeDispose(function);
        }
    }

    /// <summary>自动执行入口模块，返回模块返回值。</summary>
    public object[] RunMainModule()
    {
        if (string.IsNullOrEmpty(mainModule)) return null;
        if (_luaEnv == null && InitLuaEnv() == null) return null;

        return Require(mainModule);
    }

    /// <summary>
    /// 热重载某个模块：不重启 LuaEnv，只重新加载该模块。
    /// 步骤：移除该模块的生命周期回调 → 丢弃模块 table → package.loaded[name] = nil → 重新 require。
    /// </summary>
    /// <param name="moduleName">模块名（与 require 使用的名字一致）。</param>
    /// <param name="reExecute">是否立即重新执行脚本。</param>
    public bool Reload(string moduleName, bool reExecute = true)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            Debug.LogWarning("[XLua] Reload 需要模块名");
            return false;
        }

        if (_luaEnv == null)
        {
            Debug.LogWarning("[XLua] LuaEnv 未初始化，忽略 Reload: " + moduleName);
            return false;
        }

        try
        {
            // 1. 旧函数闭包不能继续被驱动，否则会操作已经失效的 upvalue
            UnregisterModuleCallbacks(moduleName);

            // 2. 丢掉模块 table 缓存
            LuaTable cached;
            if (_moduleCache.TryGetValue(moduleName, out cached))
            {
                _moduleCache.Remove(moduleName);
                SafeDispose(cached);
            }

            // 3. 清掉 package.loaded，下次 require 才会真正重新执行文件
            SafeDoString("package.loaded['" + EscapeLuaString(moduleName) + "'] = nil",
                "@reload:" + moduleName);

            if (debugMode) Debug.Log("[XLua] 模块已卸载: " + moduleName);

            // 4. 立即重新执行
            if (!reExecute) return true;

            object[] results = Require(moduleName);
            return results != null;
        }
        catch (Exception e)
        {
            HandleLuaError(e, "Reload(" + moduleName + ")");
            return false;
        }
    }

    /// <summary>创建一个新的 Lua table（调用方负责 Dispose）。</summary>
    public LuaTable NewTable()
    {
        return _luaEnv != null ? _luaEnv.NewTable() : null;
    }

    /// <summary>C# 侧写日志的快捷入口，同时作为 Lua 调用 C# 静态方法的示例。</summary>
    public static void Log(string message)
    {
        Debug.Log("[XLua] " + message);
    }

    /// <summary>切换 Debug / Release 模式（会重建脚本搜索顺序）。</summary>
    public static void SetDebugMode(bool debug)
    {
        if (Instance == null) return;
        Instance.DebugMode = debug;
    }

    #endregion

    #region Lua 生命周期注册

    /// <summary>
    /// 注册 Update 回调（Lua: CS.XLuaManager.RegisterUpdate("Main", fn)）。
    /// module 用于 Reload 时清理，同一模块可注册多个回调。
    /// </summary>
    public static void RegisterUpdate(string module, LuaFunction function)
    {
        RegisterInternal(module, function, CallbackKind.Update);
    }

    /// <summary>注册 LateUpdate 回调。</summary>
    public static void RegisterLateUpdate(string module, LuaFunction function)
    {
        RegisterInternal(module, function, CallbackKind.LateUpdate);
    }

    /// <summary>注册 FixedUpdate 回调。</summary>
    public static void RegisterFixedUpdate(string module, LuaFunction function)
    {
        RegisterInternal(module, function, CallbackKind.FixedUpdate);
    }

    /// <summary>移除某个模块注册的所有生命周期回调（Reload 会自动调用）。</summary>
    public static void UnregisterModuleCallbacks(string module)
    {
        if (Instance == null) return;
        Instance.RemoveModuleCallbacks(module);
    }

    /// <summary>清空所有模块的生命周期回调。</summary>
    public static void UnregisterAllCallbacks()
    {
        if (Instance == null) return;
        Instance.ClearAllCallbacks();
    }

    private static void RegisterInternal(string module, LuaFunction function, CallbackKind kind)
    {
        if (function == null) return;

        XLuaManager manager = Instance;
        if (manager == null)
        {
            Debug.LogWarning("[XLua] XLuaManager 不存在，忽略回调注册: " + module);
            return;
        }

        manager.RegisterCallback(module, function, kind);
    }

    private void RegisterCallback(string module, LuaFunction function, CallbackKind kind)
    {
        if (string.IsNullOrEmpty(module)) module = DefaultModuleName;

        ModuleCallbacks callbacks;
        if (!_moduleCallbacks.TryGetValue(module, out callbacks) || callbacks == null)
        {
            callbacks = new ModuleCallbacks();
            _moduleCallbacks[module] = callbacks;
            _callbackList.Add(callbacks);
        }

        switch (kind)
        {
            case CallbackKind.Update:
                callbacks.Update.Add(function);
                _updateCount++;
                break;
            case CallbackKind.LateUpdate:
                callbacks.LateUpdate.Add(function);
                _lateUpdateCount++;
                break;
            case CallbackKind.FixedUpdate:
                callbacks.FixedUpdate.Add(function);
                _fixedUpdateCount++;
                break;
        }

        if (debugMode) Debug.Log("[XLua] 注册 " + kind + " 回调: " + module);
    }

    private void RemoveModuleCallbacks(string module)
    {
        if (string.IsNullOrEmpty(module)) module = DefaultModuleName;

        ModuleCallbacks callbacks;
        if (!_moduleCallbacks.TryGetValue(module, out callbacks) || callbacks == null) return;

        _updateCount -= callbacks.Update.Count;
        _lateUpdateCount -= callbacks.LateUpdate.Count;
        _fixedUpdateCount -= callbacks.FixedUpdate.Count;

        DisposeAll(callbacks.Update);
        DisposeAll(callbacks.LateUpdate);
        DisposeAll(callbacks.FixedUpdate);

        _moduleCallbacks.Remove(module);
        _callbackList.Remove(callbacks);

        if (debugMode) Debug.Log("[XLua] 已移除 " + module + " 的生命周期回调");
    }

    private void ClearAllCallbacks()
    {
        for (int i = 0; i < _callbackList.Count; i++)
        {
            ModuleCallbacks callbacks = _callbackList[i];
            DisposeAll(callbacks.Update);
            DisposeAll(callbacks.LateUpdate);
            DisposeAll(callbacks.FixedUpdate);
        }

        _callbackList.Clear();
        _moduleCallbacks.Clear();
        _updateCount = 0;
        _lateUpdateCount = 0;
        _fixedUpdateCount = 0;
    }

    #endregion

    #region Unity 生命周期驱动

    private void Update()
    {
        TickLuaEnv();

        if (_updateCount <= 0) return;

        for (int i = 0; i < _callbackList.Count; i++)
        {
            List<LuaFunction> functions = _callbackList[i].Update;
            for (int j = 0; j < functions.Count; j++)
            {
                InvokeNoArg(functions[j], "Update");
            }
        }
    }

    private void LateUpdate()
    {
        if (_lateUpdateCount <= 0) return;

        for (int i = 0; i < _callbackList.Count; i++)
        {
            List<LuaFunction> functions = _callbackList[i].LateUpdate;
            for (int j = 0; j < functions.Count; j++)
            {
                InvokeNoArg(functions[j], "LateUpdate");
            }
        }
    }

    private void FixedUpdate()
    {
        if (_fixedUpdateCount <= 0) return;

        for (int i = 0; i < _callbackList.Count; i++)
        {
            List<LuaFunction> functions = _callbackList[i].FixedUpdate;
            for (int j = 0; j < functions.Count; j++)
            {
                InvokeNoArg(functions[j], "FixedUpdate");
            }
        }
    }

    private void TickLuaEnv()
    {
        if (_luaEnv == null || _tickFailed) return;

        try
        {
            // xLua 的 GC 需要每帧驱动
            _luaEnv.Tick();
        }
        catch (Exception e)
        {
            // LuaEnv 已损坏时停止 Tick，避免每帧刷屏
            _tickFailed = true;
            HandleLuaError(e, "LuaEnv.Tick");
        }
    }

    private void InvokeNoArg(LuaFunction function, string callbackName)
    {
        if (function == null) return;

        try
        {
            function.Call(NoArgs);
        }
        catch (Exception e)
        {
            HandleLuaError(e, "Lua." + callbackName);
        }
    }

    #endregion

    #region 内部工具

    private LuaTable GetModuleTable(string moduleName)
    {
        LuaTable cached;
        if (_moduleCache.TryGetValue(moduleName, out cached) && cached != null) return cached;

        object[] results = Require(moduleName);
        if (results == null || results.Length == 0) return null;

        var table = results[0] as LuaTable;
        if (table == null) return null;

        _moduleCache[moduleName] = table;
        return table;
    }

    private void ClearModuleCache()
    {
        foreach (KeyValuePair<string, LuaTable> pair in _moduleCache)
        {
            SafeDispose(pair.Value);
        }
        _moduleCache.Clear();
    }

    private static void DisposeAll(List<LuaFunction> functions)
    {
        for (int i = 0; i < functions.Count; i++)
        {
            SafeDispose(functions[i]);
        }
        functions.Clear();
    }

    private static void SafeDispose(LuaBase luaObject)
    {
        if (luaObject == null) return;

        try
        {
            luaObject.Dispose();
        }
        catch
        {
            // LuaEnv 可能已经释放，此时忽略即可
        }
    }

    private static string EscapeLuaString(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private enum CallbackKind
    {
        Update,
        LateUpdate,
        FixedUpdate
    }

    private sealed class ModuleCallbacks
    {
        public readonly List<LuaFunction> Update = new List<LuaFunction>();
        public readonly List<LuaFunction> LateUpdate = new List<LuaFunction>();
        public readonly List<LuaFunction> FixedUpdate = new List<LuaFunction>();
    }

    #endregion
}
