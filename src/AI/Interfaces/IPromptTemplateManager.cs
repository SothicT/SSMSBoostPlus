using SSMSBoostPlus.AI.Models;

namespace SSMSBoostPlus.AI.Prompts;

/// <summary>
/// Prompt 模板管理接口，负责根据模板类型组装完整的 AI 请求提示文本。
/// 将用户代码 + 系统提示 + 上下文信息组合为最终发送给 AI 的 prompt。
/// </summary>
public interface IPromptTemplateManager
{
    /// <summary>
    /// 根据模板类型、SQL 代码和可选上下文，组装完整的 user prompt 文本。
    /// </summary>
    /// <param name="template">Prompt 模板类型</param>
    /// <param name="code">待处理的 SQL 代码</param>
    /// <param name="context">AI 上下文（可选，包含 Schema/执行计划/格式风格等）</param>
    /// <returns>组装后的完整 prompt 字符串</returns>
    string BuildPrompt(PromptTemplate template, string code, AIContext? context = null);

    /// <summary>
    /// 获取指定模板类型的 system prompt 内容。
    /// system prompt 定义了 AI 的角色、任务和行为规范。
    /// </summary>
    /// <param name="template">Prompt 模板类型</param>
    /// <returns>system prompt 文本</returns>
    string GetSystemPrompt(PromptTemplate template);
}
