using SSMSBoostPlus.AI.Models;

namespace SSMSBoostPlus.AI.Parsing;

/// <summary>
/// AI 响应解析接口，负责解析 CLI 输出的 JSON/stream-json 格式数据。
/// 非流式模式解析完整 JSON 响应；流式模式解析单行 JSON 数据块。
/// </summary>
public interface IResponseParser
{
    /// <summary>
    /// 解析 CLI 非流式输出的 JSON 响应字符串。
    /// </summary>
    /// <param name="rawResponse">CLI 标准输出的原始 JSON 字符串</param>
    /// <param name="template">当前使用的 Prompt 模板类型（用于选择解析策略）</param>
    /// <returns>结构化的 AI 响应对象</returns>
    AIResponse ParseResponse(string rawResponse, PromptTemplate template);

    /// <summary>
    /// 解析 stream-json 单行数据为一个流式响应块。
    /// </summary>
    /// <param name="jsonLine">单行 JSON 数据字符串</param>
    /// <returns>流式响应块（含内容、类型、完成标记）</returns>
    AIStreamChunk ParseStreamChunk(string jsonLine);
}
