namespace SSMSBoostPlus.AI.Models;

/// <summary>
/// 流式响应块模型，表示流式模式下的单条返回数据。
/// </summary>
public class AIStreamChunk
{
    /// <summary>块内容类型（文本/工具调用/错误/完成等）</summary>
    public AIStreamChunkType ChunkType { get; set; }

    /// <summary>块的实际文本内容</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否为流式响应的最后一块（用于终止枚举）</summary>
    public bool IsComplete { get; set; }
}
