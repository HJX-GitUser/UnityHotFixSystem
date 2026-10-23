using System;
using System.Text;

/// <summary>
/// 一次 Lua 调用的错误快照，交给 <see cref="XLuaManager.OnLuaError"/> 的上层处理逻辑使用。
/// xLua 抛出的 LuaException.Message 里已经带有 "stack traceback:"，其中包含 Lua 文件名与行号，
/// 这里再补上 C# 侧的调用堆栈，方便定位是哪个 C# 入口触发了 Lua 错误。
/// </summary>
public sealed class LuaErrorInfo
{
    /// <summary>出错上下文，例如 "Update"、"DoFile(Main.lua)"、"CallLuaFunction(Main.SayHello)"。</summary>
    public string Context { get; private set; }

    /// <summary>异常消息（Lua 错误时包含 Lua 堆栈与行号）。</summary>
    public string Message { get; private set; }

    /// <summary>C# 侧调用堆栈，Lua 层直接报错时可能为 null。</summary>
    public string StackTrace { get; private set; }

    /// <summary>原始异常对象，便于上层做类型判断（例如 LuaException）。</summary>
    public Exception Exception { get; private set; }

    /// <summary>发生时间。</summary>
    public DateTime Timestamp { get; private set; }

    public LuaErrorInfo(string context, Exception exception)
    {
        Context = string.IsNullOrEmpty(context) ? "Unknown" : context;
        Exception = exception;
        Timestamp = DateTime.Now;
        Message = exception != null ? exception.Message : "Unknown Lua error";
        StackTrace = exception != null ? exception.StackTrace : null;
    }

    /// <summary>格式化后的完整错误文本（写日志用）。</summary>
    public string ToDisplayString()
    {
        var sb = new StringBuilder(512);
        sb.Append("[XLua] Lua 错误 @ ").Append(Context).Append('\n');
        sb.Append(Message);

        if (!string.IsNullOrEmpty(StackTrace))
        {
            sb.Append("\n--- C# 调用栈 ---\n");
            sb.Append(StackTrace);
        }

        return sb.ToString();
    }

    public override string ToString()
    {
        return ToDisplayString();
    }
}
