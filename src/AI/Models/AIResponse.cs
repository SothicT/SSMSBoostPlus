using System;

namespace SSMSBoostPlus.AI.Models;

/// <summary>
/// AI 响应模型，封装从 Bridge 返回的完整响应结果。
/// </summary>
public class AIResponse
{
    /// <summary>AI 生成的文本内容</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>输入 Token 消耗数</summary>
    public int InputTokens { get; set; }

    /// <summary>输出 Token 消耗数</summary>
    public int OutputTokens { get; set; }

    /// <summary>本次请求耗时</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>是否成功获取响应</summary>
    public bool IsSuccess { get; set; }

    /// <summary>错误描述信息（IsSuccess=false 时有值）</summary>
    public string? ErrorMessage { get; set; }
}
