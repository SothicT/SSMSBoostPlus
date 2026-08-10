using SSMSBoostPlus.AI.Models;

namespace SSMSBoostPlus.AI.Context;

/// <summary>
/// AI 上下文提供者接口，负责根据模板类型构建相应的上下文信息。
/// 上下文包含 SQL 代码、Schema 信息、执行计划、格式化风格等。
/// </summary>
public interface IContextProvider
{
    /// <summary>
    /// 根据模板类型和输入参数构建完整的 AI 上下文。
    /// </summary>
    /// <param name="template">Prompt 模板类型（Suggest/Comment/Format/Analyze）</param>
    /// <param name="code">待分析的 SQL 代码</param>
    /// <param name="schemaInfo">数据库 Schema 信息（Analyze 模板使用）</param>
    /// <param name="executionPlan">执行计划 XML（Analyze 模板使用）</param>
    /// <returns>构建好的 AI 上下文对象</returns>
    AIContext BuildContext(PromptTemplate template, string code, string? schemaInfo = null, string? executionPlan = null);
}
