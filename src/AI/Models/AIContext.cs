using SSMSBoostPlus.Shared;

namespace SSMSBoostPlus.AI.Models;

/// <summary>
/// AI 上下文模型，为 Prompt 构建提供丰富的环境信息。
/// 由 IContextProvider 根据模板类型和输入参数构建。
/// </summary>
public class AIContext
{
    /// <summary>待处理的 SQL 代码</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>数据库 Schema 信息（表结构、列定义、约束等）</summary>
    public string? SchemaInfo { get; set; }

    /// <summary>执行计划 XML 或文本</summary>
    public string? ExecutionPlan { get; set; }

    /// <summary>目标格式化风格（Format 模板使用）</summary>
    public Shared.FormatStyle? FormatStyle { get; set; }

    /// <summary>数据库索引信息（Analyze 模板使用）</summary>
    public string? IndexInfo { get; set; }

    /// <summary>当前数据库名称</summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// 规范上下文：承载从需求规格文档中提取的结构化规范信息。
    /// 由 SpecContextProvider 根据当前功能模块从 YAML 规范文件中加载。
    /// </summary>
    public SpecContext? SpecContext { get; set; }
}
