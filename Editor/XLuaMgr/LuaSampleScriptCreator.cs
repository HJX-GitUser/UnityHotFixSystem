using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace XLuaTools
{
    /// <summary>示例脚本创建结果。</summary>
    public sealed class SampleScriptResult
    {
        public bool success;
        public bool alreadyExists;
        public string path;
        public string error;
    }

    /// <summary>
    /// 一键在指定目录生成示例脚本 Main.lua。
    /// 模板内置在本脚本中，写入时统一为 UTF-8（无 BOM）+ LF 换行。
    /// 菜单：Tools/XLua/创建示例脚本 Lua/Main.lua
    /// </summary>
    public static class LuaSampleScriptCreator
    {
        public const string SampleFileName = "Main.lua";

        /// <summary>
        /// 示例模板。逐字字符串中 "" 表示一个双引号。
        /// 与 XLuaManager 的接口保持同步：Debug.Log / XLuaManager.Log / RegisterUpdate / GameObject.Find / 热重载注释。
        /// </summary>
        private const string SampleTemplate = @"--[[
    Main.lua —— XLuaManager 示例入口

    加载位置（开发期）：<工程根>/Lua/Main.lua
    require 名字：      ""Main""（对应文件 Main.lua 或 Main.lua.txt）

    演示内容：
      1. 调用 UnityEngine.Debug.Log（C# 静态方法）
      2. 调用自定义 C# 静态方法 CS.XLuaManager.Log
      3. 向 XLuaManager 注册 Update 回调（不注册就不会被调用）
      4. 访问场景中的 GameObject（Cube）并旋转它
      5. 暴露一个函数给 C# 调用：XLuaManager.CallLuaFunction(""Main"", ""SayHello"", ""World"")
]]

local UnityEngine = CS.UnityEngine
local Debug = UnityEngine.Debug
local XLuaManager = CS.XLuaManager

local M = {}            -- 模块表，作为 require(""Main"") 的返回值

local cube = nil        -- 场景里的 Cube
local elapsed = 0       -- Update 累计运行时间
local logTimer = 0      -- 日志节流计时
local LOG_INTERVAL = 2

-- ============================================================
-- 1. 调用 C# 静态方法：UnityEngine.Debug.Log
-- ============================================================
Debug.Log(""[Main.lua] 脚本已加载"")

-- ============================================================
-- 2. 调用自定义 C# 静态方法：XLuaManager.Log
-- ============================================================
XLuaManager.Log(""这条日志来自 C# 静态方法 XLuaManager.Log"")

-- ============================================================
-- 3. 访问 GameObject：按名字查找场景中的 Cube
-- ============================================================
cube = UnityEngine.GameObject.Find(""Cube"")
if cube ~= nil then
    Debug.Log(""[Main.lua] 找到 GameObject: "" .. cube.name)
else
    Debug.LogWarning(""[Main.lua] 场景中没有名为 Cube 的对象，跳过旋转演示"")
end

-- ============================================================
-- 4. Update 回调：由 XLuaManager 每帧驱动
-- ============================================================
local function onUpdate()
    local deltaTime = UnityEngine.Time.deltaTime
    elapsed = elapsed + deltaTime
    logTimer = logTimer + deltaTime

    -- 通过 Transform 组件旋转 Cube
    if cube ~= nil then
        cube.transform:Rotate(0, 30 * deltaTime, 0)
    end

    -- 每 2 秒输出一次，避免刷屏
    if logTimer >= LOG_INTERVAL then
        logTimer = 0
        Debug.Log(string.format(""[Main.lua] Update 累计运行 %.1f 秒"", elapsed))
    end
end

-- 注册到名为 ""Main"" 的模块；Reload(""Main"") 时会自动移除这些回调
XLuaManager.RegisterUpdate(""Main"", onUpdate)

-- 运行时热重载（在其它 Lua 代码里执行，会重新加载本文件并重新注册回调）：
-- CS.XLuaManager.Instance:Reload(""Main"")
-- 等价的 C# 写法：XLuaManager.Instance.Reload(""Main"");

-- 需要时同样可以注册其它生命周期（未注册则 XLuaManager 不做任何调用）
-- XLuaManager.RegisterLateUpdate(""Main"", function() end)
-- XLuaManager.RegisterFixedUpdate(""Main"", function() end)

-- ============================================================
-- 5. 暴露给 C# 的接口
-- ============================================================
function M.SayHello(name)
    name = name or ""XLua""
    Debug.Log(""[Main.lua] SayHello: "" .. name)
    return ""Hello, "" .. name
end

function M.GetElapsed()
    return elapsed
end

return M
";

        [MenuItem("Tools/XLua/创建示例脚本 Lua/Main.lua", false, 102)]
        public static void CreateDefaultSample()
        {
            CreateWithDialog(LuaToTextConverter.GetDefaultSourceFolder());
        }

        /// <summary>
        /// 目标文件已存在时弹窗询问是否覆盖，然后把结果写进控制台。
        /// </summary>
        public static SampleScriptResult CreateWithDialog(string targetFolder)
        {
            SampleScriptResult result = Create(targetFolder, false);
            if (result.alreadyExists)
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "示例脚本已存在",
                    "是否覆盖？\n" + result.path,
                    "覆盖",
                    "取消");

                if (overwrite) result = Create(targetFolder, true);
            }

            Log(result);
            return result;
        }

        /// <summary>
        /// 把内置模板写成 Main.lua。
        /// </summary>
        /// <param name="targetFolder">输出目录（绝对路径）；为空时使用工程根目录下的 Lua。</param>
        /// <param name="overwrite">已存在时是否覆盖。</param>
        public static SampleScriptResult Create(string targetFolder, bool overwrite)
        {
            var result = new SampleScriptResult();

            string folder = string.IsNullOrEmpty(targetFolder)
                ? LuaToTextConverter.GetDefaultSourceFolder()
                : targetFolder;

            result.path = Path.Combine(Path.GetFullPath(folder), SampleFileName);

            if (File.Exists(result.path) && !overwrite)
            {
                result.success = true;
                result.alreadyExists = true;
                return result;
            }

            try
            {
                string directory = Path.GetDirectoryName(result.path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 统一成 LF；UTF-8 不带 BOM，避免 Lua 解析到 BOM 字符
                string content = GetSampleTemplate();
                File.WriteAllText(result.path, content, new UTF8Encoding(false));
                result.success = true;

                // 目标在 Assets 下时让 Unity 立即感知新文件
                if (!string.IsNullOrEmpty(LuaToTextConverter.ToProjectRelativePath(result.path)))
                {
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception e)
            {
                result.error = e.Message;
            }

            return result;
        }

        /// <summary>获取内置模板内容（LF 换行），供预览或测试使用。</summary>
        public static string GetSampleTemplate()
        {
            return SampleTemplate.Replace("\r\n", "\n");
        }

        private static void Log(SampleScriptResult result)
        {
            if (result == null) return;

            if (!result.success)
            {
                Debug.LogError("[XLua] 创建示例脚本失败: " + result.error);
                return;
            }

            if (result.alreadyExists)
            {
                Debug.Log("[XLua] 示例脚本已存在，未覆盖: " + result.path);
                return;
            }

            Debug.Log("[XLua] 已创建示例脚本: " + result.path);
        }
    }
}
