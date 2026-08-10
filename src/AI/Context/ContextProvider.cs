using System;
using SSMSBoostPlus.AI.Models;

namespace SSMSBoostPlus.AI.Context;

/// <summary>
/// AI 上下文提供者实现，根据模板类型构建相应的 AI 上下文对象。
/// 不同模板需要的上下文字段不同：
/// - Suggest/Comment/Format/Chat：仅需代码 + 格式风格
/// - Analyze：需要代码 + Schema + 执行计划
/// </summary>
public class ContextProvider : IContextProvider
{
    /// <inheritdoc />
    public AIContext BuildContext(PromptTemplate template, string code, string? schemaInfo = null, string? executionPlan = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("代码不能为空", nameof(code));

        return template switch
        {
            // 性能分析模板：携带完整的 Schema 和执行计划信息
            PromptTemplate.Analyze => new AIContext
            {
                Code = code,
                SchemaInfo = schemaInfo,
                IndexInfo = null,   // 由外部按需赋值
                ExecutionPlan = executionPlan
            },

            // 建议 / 注释 / 格式化 / 对话模板：基础上下文 + 格式风格
            PromptTemplate.Suggest or
            PromptTemplate.Comment or
            PromptTemplate.Format or
            PromptTemplate.Chat => new AIContext
            {
                Code = code,
                FormatStyle = Shared.FormatStyle.Microsoft  // 默认 Microsoft 风格
            },

            // 未知的模板类型
            _ => throw new ArgumentOutOfRangeException(nameof(template),
                $"不支持的模板类型: {template}")
        };
    }
}
