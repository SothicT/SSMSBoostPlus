namespace SSMSBoostPlus.AI.Models;

/// <summary>
/// 流式响应块类型枚举，区分不同种类的流式数据。
/// </summary>
public enum AIStreamChunkType
{
    /// <summary>普通文本内容</summary>
    Text,

    /// <summary>AI 思考过程（如 Qwen3 的 &lt;think&gt; 标签内容）</summary>
    Thinking,

    /// <summary>思考阶段开始标记</summary>
    ThinkingStart,

    /// <summary>思考阶段结束标记</summary>
    ThinkingEnd,

    /// <summary>工具/函数调用指令</summary>
    ToolUse,

    /// <summary>工具调用结果</summary>
    ToolResult,

    /// <summary>错误信息</summary>
    Error,

    /// <summary>流式传输完成标记</summary>
    Complete
}
