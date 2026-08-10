using SSMSBoostPlus.Shared;
using System.Collections.Generic;

namespace SSMSBoostPlus.AI.Models;

/// <summary>
/// AI 请求模型，封装发送给 Bridge 的完整请求信息。
/// </summary>
public class AIRequest
{
    /// <summary>使用的 Prompt 模板类型</summary>
    public PromptTemplate Template { get; set; }

    /// <summary>待处理的 SQL 代码文本</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>数据库 Schema 信息（Analyze 模板使用）</summary>
    public string? SchemaInfo { get; set; }

    /// <summary>执行计划 XML 文本（Analyze 模板使用）</summary>
    public string? ExecutionPlan { get; set; }

    /// <summary>格式化风格（Format 模板使用）</summary>
    public Shared.FormatStyle? FormatStyle { get; set; }

    /// <summary>数据库索引信息（Analyze 模板使用）</summary>
    public string? IndexInfo { get; set; }

    /// <summary>数据库名称上下文（所有模板均可用）</summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// 只读模式：为 true 时，AI 仅输出文本建议，禁止修改任何本地文件。
    /// CLI 桥接将通过 System Prompt 约束 AI 行为，确保不执行文件写入操作。
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// 规范上下文：从需求规格文档中提取的结构化规范信息，
    /// 让 AI 在生成代码时感知验收条件、字段定义和接口约束。
    /// </summary>
    public SpecContext? SpecContext { get; set; }
}

/// <summary>
/// 规范上下文：承载从需求规格文档中提取的结构化规范信息。
/// 用于 SDD 场景，让 AI 在生成代码时感知规范约束。
/// </summary>
public class SpecContext
{
    /// <summary>规范 ID（如 SPEC-SSMSBP-001）</summary>
    public string? SpecId { get; set; }

    /// <summary>规范版本</summary>
    public string? SpecVersion { get; set; }

    /// <summary>所属模块名</summary>
    public string? ModuleName { get; set; }

    /// <summary>当前功能的验收条件列表</summary>
    public List<SpecAcceptanceCriterion>? AcceptanceCriteria { get; set; }

    /// <summary>相关字段定义列表</summary>
    public List<SpecFieldDefinition>? FieldDefinitions { get; set; }

    /// <summary>相关接口定义列表</summary>
    public List<SpecInterfaceDefinition>? InterfaceDefinitions { get; set; }

    /// <summary>非功能需求（性能、安全等约束）</summary>
    public List<string>? NonFunctionalRequirements { get; set; }

    /// <summary>权限约束</summary>
    public List<string>? PermissionRequirements { get; set; }
}

/// <summary>
/// 结构化验收条件，对应需求规格中的 AC。
/// </summary>
public class SpecAcceptanceCriterion
{
    public string Id { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Priority { get; set; } = "P3";
    public string Scenario { get; set; } = string.Empty;
    public string Given { get; set; } = string.Empty;
    public string When { get; set; } = string.Empty;
    public string Then { get; set; } = string.Empty;
    public List<string>? Tags { get; set; }
}

/// <summary>
/// 结构化字段定义，对应需求规格中的字段定义。
/// </summary>
public class SpecFieldDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool PrimaryKey { get; set; }
    public string? Default { get; set; }
    public List<string>? Enum { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 结构化接口定义，对应需求规格中的接口定义。
/// </summary>
public class SpecInterfaceDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<SpecInterfaceParameter>? Inputs { get; set; }
    public SpecInterfaceOutput? Outputs { get; set; }
    public List<SpecInterfaceError>? Errors { get; set; }
}

public class SpecInterfaceParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class SpecInterfaceOutput
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<SpecFieldDefinition>? Fields { get; set; }
}

public class SpecInterfaceError
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
