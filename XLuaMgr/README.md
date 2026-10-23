# XLuaManager 使用说明

基于 xLua 的 Lua 运行时管理器：负责 `LuaEnv` 生命周期、脚本加载、C# ↔ Lua 调用封装、Unity 生命周期驱动、模块热重载与统一错误处理。

---

## 1. 文件结构

运行时脚本与 Hotfix 框架同属一个根目录，编辑器脚本统一收在 `Assets/Scripts/Hotfix/Editor/XLuaMgr/`：

```
Assets/Scripts/Hotfix/
├── XLuaMgr/                        # 运行时（随包）
│   ├── XLuaManager.cs              # 单例管理器：初始化、接口封装、生命周期驱动、热重载、错误处理
│   ├── LuaScriptLoader.cs          # 脚本定位：ILuaScriptProvider + 文件系统 / AssetBundle 实现
│   ├── LuaErrorInfo.cs             # 错误快照：Lua traceback + C# 堆栈
│   └── README.md                   # 本文档
└── Editor/
    └── XLuaMgr/                    # 仅编辑器
        ├── LuaToTextConverter.cs   # 批量把 .lua 转成 .lua.txt 的核心逻辑（可命令行调用）
        ├── LuaToTextWindow.cs      # 转换工具窗口与菜单项
        └── LuaSampleScriptCreator.cs  # 一键创建示例脚本（模板内置在代码中）

Lua/                      # 开发期脚本目录（= Application.dataPath/../Lua）
└── Main.lua              # 示例入口（也支持 Main.lua.txt）
```

---

## 2. 快速开始

1. **挂载管理器**（二选一）
   - 场景中新建空物体，添加 `XLuaManager` 组件（推荐，可在 Inspector 配置）；
   - 什么都不做：运行时会在首个场景加载完成后自动创建 `[XLuaManager]`，同样走 `DontDestroyOnLoad`。
2. **写入口脚本** `Lua/Main.lua`，返回一个 table 即可（示例见第 9 节）。
3. **运行**：`Awake` 中创建 `LuaEnv` 并 `require("Main")`，Lua 里注册的回调随之开始被驱动。

C# 侧调用示例：

```csharp
// 直接调用 Lua 函数
object[] ret = XLuaManager.Instance.CallLuaFunction("Main", "SayHello", "World");
Debug.Log(ret[0]); // Hello, World

// 读全局变量
int value = XLuaManager.Instance.GetGlobal<int>("SomeGlobal");

// 运行时热重载一个模块
XLuaManager.Instance.Reload("Main");
```

---

## 3. Inspector 配置

| 字段 | 默认值 | 说明 |
|---|---|---|
| `debugMode` | Editor/Development Build 为 `true` | Debug 优先读工程目录脚本；Release 优先 AssetBundle / StreamingAssets |
| `mainModule` | `Main` | 启动时自动 `require` 的入口模块，留空则不执行 |
| `autoRunMainModule` | `true` | 是否在 `Awake` 里自动执行入口模块 |
| `luaFolderName` | `Lua` | 开发期 = `工程根/<名字>`；发布期 = `streamingAssetsPath/<名字>` 与 `persistentDataPath/<名字>` |
| `loadFromPersistentData` | `true` | 是否允许从 `persistentDataPath` 读取（热更脚本落地目录） |

运行时也可切换：`XLuaManager.SetDebugMode(true/false)` 或 `XLuaManager.Instance.DebugMode = ...`，切换后会重建脚本搜索顺序。

---

## 4. 脚本加载规则

### 4.1 搜索顺序

| 模式 | 顺序 |
|---|---|
| Debug | 工程目录 `Lua/` → `persistentDataPath/Lua/` → AssetBundle → `StreamingAssets/Lua/` |
| Release | AssetBundle → `persistentDataPath/Lua/` → `StreamingAssets/Lua/` →（仅 Editor 兜底）工程目录 |

- 工程目录由 `Application.dataPath/../<luaFolderName>` 计算，即 `<工程根>/Lua`。
- 自定义 Loader 返回 `null` 时，自动回落到 xLua 内置 searcher（`Resources`、`StreamingAssets` 等），因此 `require("xlua.util")` 之类的内置模块不受影响。

### 4.2 文件名解析

`require` / `DoFile` 传入的路径会依次尝试（顺序即优先级）：

| 传入 | 候选文件 |
|---|---|
| `"Main"` | `Main.lua` → `Main.lua.txt` → `Main` |
| `"Main.lua"` | `Main.lua` → `Main.lua.txt` |
| `"a.b"` | `a/b.lua` → `a/b.lua.txt` → `a/b` |
| `"sub/Util.lua.txt"` | 原样读取 |

- `.lua.txt` 后缀便于把脚本当 `TextAsset` 打进 AssetBundle。
- 命中后，chunk name 会被替换成真实路径，Lua 报错时可直接看到文件与行号。

### 4.3 打包 / 热更

```csharp
// 方式一：AssetBundle（推荐，Android / iOS 均可）
AssetBundle bundle = AssetBundle.LoadFromFile(path);
XLuaManager.Instance.AssignAssetBundle(bundle);

// 方式二：自定义来源（例如 Addressables 下载完成后喂给管理器）
XLuaManager.Instance.AssignLuaProvider(myProvider);
```

> ⚠️ Android / iOS 的 `StreamingAssets` 位于包内（`jar://` / app bundle），`File` API 读不到。
> 发布版本请使用 **AssetBundle**，或先把脚本解压/下载到 `persistentDataPath`（管理器已默认支持该目录，且 Release 下优先级高于 `StreamingAssets`）。
>
> 打包所需的 `.lua.txt` 可以用第 10 节的编辑器工具批量生成。

---

## 5. 核心 API

| API | 返回 | 说明 |
|---|---|---|
| `SafeDoString(string code, string chunkName = "")` | `object[]` | 执行一段 Lua 代码，异常不外抛；失败返回 `null` |
| `DoFile(string filePath)` | `bool` | 按加载器规则查找并执行脚本文件 |
| `Require(string moduleName)` | `object[]` | 等价于 `return require('模块名')` |
| `GetGlobal<T>(string name)` | `T` | 读取 Lua 全局变量 |
| `SetGlobal<T>(string name, T value)` | `bool` | 写入 Lua 全局变量（注入 C# 对象） |
| `CallLuaFunction(string module, string func, params object[] args)` | `object[]` | 调用模块中的函数，`func` 支持 `"Sub.Func"` 路径 |
| `Reload(string moduleName, bool reExecute = true)` | `bool` | 热重载单个模块 |
| `RunMainModule()` | `object[]` | 重新执行入口模块 |
| `NewTable()` | `LuaTable` | 新建 Lua table（调用方负责 `Dispose`） |
| `DisposeLuaEnv()` | `void` | 主动释放 `LuaEnv`（重复调用安全） |
| `AssignAssetBundle(AssetBundle bundle)` | `void` | 指定 AssetBundle 脚本来源 |
| `AssignLuaProvider(ILuaScriptProvider provider)` | `void` | 指定自定义脚本来源 |
| `XLuaManager.Log(string message)` | `void` | 静态日志方法，同时作为「Lua 调 C# 静态方法」的示例 |

属性：`Instance`、`IsReady`、`LuaEnv`、`ScriptLoader`、`DebugMode`、`MainModule`、`LastError`、`UpdateCallbackCount`。
事件：`OnLuaError`（配合 `RegisterLuaErrorHandler` / `UnregisterLuaErrorHandler` / `ClearLuaErrorHandlers`）。

---

## 6. 生命周期驱动

Lua 侧显式注册，**不注册就不会被调用**；未注册任何回调时，每帧只执行 `LuaEnv.Tick()`（xLua GC 需要），不产生额外调用与 GC。

```lua
local XLuaManager = CS.XLuaManager

local function onUpdate()
    -- 每帧逻辑
end

XLuaManager.RegisterUpdate("Main", onUpdate)          -- Update
XLuaManager.RegisterLateUpdate("Main", onLateUpdate)  -- LateUpdate
XLuaManager.RegisterFixedUpdate("Main", onFixedUpdate)-- FixedUpdate
```

- 第一个参数是 **模块名**，用于 `Reload` 时精确清理；同一模块可注册多个回调。
- 卸载：`XLuaManager.UnregisterModuleCallbacks("Main")` / `XLuaManager.UnregisterAllCallbacks()`。
- 内部按模块分桶并维护计数，计数为 0 时直接 return；无参调用复用静态空数组，避免每帧 GC。

---

## 7. 热重载

```csharp
XLuaManager.Instance.Reload("Main");        // C#
```

```lua
CS.XLuaManager.Instance:Reload("Main")      -- Lua
```

执行步骤：

1. 移除该模块已注册的 `Update / LateUpdate / FixedUpdate` 回调（旧闭包不能继续被驱动）；
2. 丢弃模块 table 缓存并释放引用；
3. 执行 `package.loaded["Main"] = nil`；
4. 重新 `require`，脚本顶层的注册代码会重新执行。

**限制与注意**

- 只重跑 Lua 脚本，**不重启 `LuaEnv`**，因此 C# 侧持有的 `LuaTable` / `LuaFunction` 包装器会失效（已被管理器清理）。
- 已经由 C# 通过 `GetGlobal<Action>` 等方式拿走的旧委托不会自动更新；请把逻辑做成「模块 + 生命周期回调」，才能被 Reload 正确替换。
- 脚本中 `local` 定义的模块内部状态会随重载重置，需要保留的数据请放到 C# 侧或 Lua 全局表中。

---

## 8. 错误处理

所有入口（`SafeDoString` / `DoFile` / `Require` / `GetGlobal` / `CallLuaFunction` / 生命周期回调 / `LuaEnv.Tick`）都包了 `try/catch`：

- 打印完整错误（`LuaException.Message` 已包含 `stack traceback:` 与行号，`LuaErrorInfo.ToDisplayString()` 再追加 C# 调用栈）；
- 触发 `OnLuaError` 事件，回调内部抛出的异常也会被隔离，不会影响其它订阅者；
- 记录 `LastError` 供上层查询；
- `Tick` 失败会置位并停止后续 Tick，避免每帧刷屏。

```csharp
XLuaManager.RegisterLuaErrorHandler(info =>
{
    Debug.LogError($"Lua 出错：{info.Context}\n{info.Message}");
    // 上报、弹窗、降级……
});
```

示例输出：

```
[XLua] Lua 错误 @ DoString(@TestError.lua)
TestError.lua:2: attempt to index a nil value (local 't')
stack traceback:
    TestError.lua:2: in main chunk
--- C# 调用栈 ---
...
```

---

## 9. 示例脚本 `Lua/Main.lua`

`Main.lua` 演示了：

1. 调用 `UnityEngine.Debug.Log`（C# 静态方法）；
2. 调用自定义 C# 静态方法 `CS.XLuaManager.Log`；
3. `RegisterUpdate("Main", ...)` 注册每帧回调；
4. `GameObject.Find("Cube")` 访问场景对象并旋转；
5. 暴露 `M.SayHello(name)` / `M.GetElapsed()` 给 C# 通过 `CallLuaFunction` 调用；
6. 注释里给出运行时热重载写法。

运行后控制台可见：

```
[XLua] 脚本来源: 工程目录 -> D:\UnityProject\HotFixProject\Lua
[XLua] LuaEnv 创建完成（Debug 模式）
[XLua] 脚本命中: Main -> D:\UnityProject\HotFixProject\Lua\Main.lua
[Main.lua] 脚本已加载
[XLua] 这条日志来自 C# 静态方法 XLuaManager.Log
[Main.lua] 找到 GameObject: Cube
[XLua] 注册 Update 回调: Main
[Main.lua] Update 累计运行 2.0 秒
```

---

## 10. 编辑器工具：Lua → Text 批量转换

菜单：**Tools → XLua → Lua 转 Text 工具**（或用 **Tools → XLua → 快速转换 Lua → Text（使用上次配置）** 一键执行）。

窗口选项：

| 选项 | 说明 |
|---|---|
| 源目录 | 默认 `<工程根>/Lua`，查找 `.lua` 文件 |
| 输出目录 | 默认 `Assets/StreamingAssets/Lua`；也可指向任意目录（在 Assets 之外时只写文件，不生成 meta、不设置 AssetBundle） |
| 包含子目录 | 是否递归遍历源目录 |
| 保持目录结构 | 关闭后所有脚本平铺到输出目录 |
| 覆盖已存在的 .lua.txt | 关闭时同名文件会被跳过 |
| 清理输出目录中多余的 .lua.txt | 删除没有对应源文件的 `.lua.txt`，只处理 `.lua.txt`，不会误删其它 txt |
| AssetBundle 名称 | 可选，为生成的 TextAsset 设置 `assetBundleName`（Unity 会转成小写） |

转换规则：

- `Main.lua` → `Main.lua.txt`，按字节复制，不改变编码与换行；
- 输出结构与 `XLuaManager` 的路径解析一致，`require("Main")` / `DoFile("Main.lua")` 都能直接命中生成的 `.lua.txt`；
- 输出位于 `Assets/` 下时自动 `AssetDatabase.Refresh()`，并可自动在 Project 窗口选中输出目录；
- 配置保存在 `EditorPrefs`，下次打开窗口自动恢复。

> Debug 模式下工程目录的优先级最高，所以开发时改源 `.lua` 立即生效；发布构建读取的则是生成的 `.lua.txt`（默认落在 `StreamingAssets/Lua`，正好是管理器的搜索路径之一）。

### 10.1 一键创建示例脚本

菜单：**Tools → XLua → 创建示例脚本 Lua/Main.lua**，或点击窗口里的 **一键创建示例脚本 Lua/Main.lua** 按钮。

- 目标路径 = 当前「源目录」+ `Main.lua`（源目录默认为 `<工程根>/Lua`）；
- 已存在时弹窗询问是否覆盖，取消则不改动文件；
- 模板内置在 `LuaSampleScriptCreator.cs` 的 `SampleTemplate` 常量中，写入时统一为 **UTF-8（无 BOM）+ LF 换行**；
- 模板内容包含：`Debug.Log`、`XLuaManager.Log`、`RegisterUpdate`、`GameObject.Find("Cube")` 与热重载注释；
- 也可直接调用 C#：

```csharp
var result = XLuaTools.LuaSampleScriptCreator.Create(
    XLuaTools.LuaToTextConverter.GetDefaultSourceFolder(),
    overwrite: false);

Debug.Log(result.path);                 // <工程根>/Lua/Main.lua
string template = XLuaTools.LuaSampleScriptCreator.GetSampleTemplate(); // 内置模板内容
```

创建成功后点击窗口的 **开始转换** 即可生成对应的 `Main.lua.txt`。

批处理 / CI：

```bash
Unity.exe -batchmode -quit -projectPath <工程路径> ^
  -executeMethod XLuaTools.LuaToTextConverter.ConvertDefaultFolders
```

使用默认路径（工程根 `Lua` → `Assets/StreamingAssets/Lua`，并清理过期文件）；有错误时退出码为 1。

也可以直接在 C# 中调用：

```csharp
var settings = new XLuaTools.LuaToTextSettings
{
    sourceFolder = XLuaTools.LuaToTextConverter.GetDefaultSourceFolder(),
    outputFolder = XLuaTools.LuaToTextConverter.GetDefaultOutputFolder(),
    deleteStale = true,
};

LuaToTextResult result = XLuaTools.LuaToTextConverter.Convert(settings);
result.LogToConsole();
```

---

## 11. 代码生成（可选但建议）

`XLuaManager` 已标记 `[LuaCallCSharp]`。没有生成代码时 xLua 会走反射调用（可用，但更慢，且委托类型不受支持）。发布前建议：

1. 打开 Unity 菜单 **XLua → Generate Code**；
2. 如需在 Lua 中接收 C# 委托，把对应委托类型加入 `CSharpCallLua` 生成列表。

---

## 12. 常见问题

| 现象 | 原因与处理 |
|---|---|
| 控制台没有 `[XLua]` 日志 | 场景没有挂 `XLuaManager` 且未进入 Play Mode（自动创建只在运行时生效），或 `autoRunMainModule` 被关闭 |
| `找不到 Lua 脚本: Main` | 确认脚本在 `<工程根>/Lua/Main.lua`（不是 `Assets/Lua`），或检查 `luaFolderName` |
| 改了 Lua 没生效 | `require` 有缓存，需要 `Reload("Main")` 或重新进入 Play Mode |
| Android 读不到 StreamingAssets | 平台限制，改用 `AssignAssetBundle` 或 `persistentDataPath` |
| 报错没有行号 | 检查 chunk name 是否被 `SafeDoString` 传成了 `""`（默认 `chunk`）；`DoFile` 会自动使用真实路径 |
| 关闭 Domain Reload 后出现野实例 | 已通过 `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` 重置静态字段；自定义静态事件请在初始化处自行清理 |
| 转换工具提示“没有找到 .lua 文件” | 源目录默认是 `<工程根>/Lua`；脚本放在 `Assets/Lua` 时请在窗口里改源目录 |
