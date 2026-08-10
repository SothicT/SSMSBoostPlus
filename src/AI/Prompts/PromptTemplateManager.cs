using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.Shared;
using SSMSBoostPlus.Shared.Extensions;

namespace SSMSBoostPlus.AI.Prompts;

/// <summary>
/// Prompt 模板管理实现，负责根据模板类型组装完整的 AI 请求提示文本。
/// 
/// 工作流程：
/// 1. 从嵌入的 Markdown 资源文件加载 system prompt（定义 AI 角色和任务规范）
/// 2. 根据模板类型构建 user prompt（包含用户代码和上下文信息）
/// 
/// 模板文件位置：Prompts/Templates/ 下
/// - suggest.md  → SQL 代码建议模板
/// - comment.md  → SQL 注释生成模板
/// - format.md   → SQL 格式化模板
/// - analyze.md  → SQL 性能分析模板
/// </summary>
public class PromptTemplateManager : IPromptTemplateManager
{
    /// <summary>
    /// 模板文件名映射表（枚举 → 嵌入资源名）。
    /// 资源文件通过 csproj 的 EmbeddedResource 配置嵌入 DLL。
    /// </summary>
    private static readonly Dictionary<PromptTemplate, string> TemplateResourceMap = new()
    {
        { PromptTemplate.Suggest, "PromptTemplates.suggest.md" },
        { PromptTemplate.Comment, "PromptTemplates.comment.md" },
        { PromptTemplate.Format, "PromptTemplates.format.md" },
        { PromptTemplate.Analyze, "PromptTemplates.analyze.md" },
        { PromptTemplate.Chat, "PromptTemplates.chat.md" }
    };

    // ── 缓存：避免重复加载资源文件 ─────────────────────────────

    /// <summary>System prompt 内容缓存</summary>
    private readonly Dictionary<PromptTemplate, string> _systemPromptCache = new();

    /// <summary>资源加载失败时的降级 System prompt</summary>
    private static readonly Dictionary<PromptTemplate, string> FallbackSystemPrompts = new()
    {
        {
            PromptTemplate.Suggest,
            @"你是一位 SQL 代码审查专家，擅长分析 T-SQL 代码并给出改进建议。
你的任务是对用户提供的 SQL 代码进行全面审查，按优先级从以下四个方面给出建议：
1. **安全风险**：SQL 注入、敏感数据泄露、权限过度、死锁风险、事务范围过大
2. **语法错误**：关键字拼写错误、括号不匹配、数据类型不匹配
3. **代码精简**：冗余子查询可改为 JOIN、重复条件合并、CTE 替代嵌套子查询
4. **性能优化**：全表扫描、缺失索引、隐式类型转换、SELECT * 警告

输出格式要求：
- 使用 Markdown 格式
- 按照安全风险、语法错误、代码精简、性能优化四个分类组织内容
- 每个建议附带代码示例
- 每个分类标注优先级（P0-P3）
- 无问题则标注 ✅
- 输出语言：中文"
        },
        {
            PromptTemplate.Comment,
            @"你是一位 SQL 注释生成专家，擅长为 T-SQL 代码添加清晰准确的注释。
你的任务是为用户提供的 SQL 代码添加注释，要求：
1. 根据代码复杂度控制注释密度（简单代码少注释，复杂代码详细注释）
2. 存储过程/函数头部生成块注释（功能、参数、返回值）
3. 语句级别注释：为每个主要 SQL 语句添加功能说明
4. JOIN 注释：解释每个 JOIN 的关联目的和条件
5. CTE 注释：解释每个 CTE 的用途和逻辑
6. WHERE 条件注释：解释复杂筛选条件的业务含义
7. 变量声明注释：说明变量用途
8. 异常处理注释：TRY...CATCH 说明错误处理策略

注释原则：解释""为什么""而非""是什么""，不过度注释。

输出格式：
- 输出带注释的完整 SQL 代码
- 保持 SQL 的可执行性
- 对象头部使用 /* */ 块注释，行内使用 -- 单行注释
- 不改变原始 SQL 的逻辑和格式"
        },
        {
            PromptTemplate.Format,
            @"你是一位 SQL 格式化专家，擅长将 T-SQL 代码格式化为规范、易读的形式。
你需要根据用户指定的风格对 SQL 代码进行格式化：

**Microsoft 风格**：
- SQL 关键字大写（SELECT, FROM, WHERE 等）
- 4 空格缩进
- 逗号放在行尾

**Google 风格**：
- SQL 关键字小写（select, from, where 等）
- 2 空格缩进
- 逗号放在行首

特殊结构格式化规则：
- CASE 表达式：WHEN...THEN 对齐缩进，ELSE 与 END 对齐
- CTE：WITH 后换行，每个 CTE 独占一段
- 窗口函数：OVER 中的 PARTITION BY 和 ORDER BY 换行对齐
- UNION / UNION ALL：前后空行
- 长 IN 列表：每个值独占一行
- Schema 前缀保留（不省略 dbo.）
- AS 关键字保留（不省略）

输出格式：
- 输出格式化后的完整 SQL 代码
- 保持 SQL 的可执行性
- 不改变 SQL 的逻辑语义"
        },
        {
            PromptTemplate.Analyze,
            @"你是一位 SQL 性能分析专家，擅长分析 T-SQL 的执行计划和性能瓶颈。
你需要结合用户提供的 Schema 信息和执行计划，对 SQL 代码进行深度性能分析。

分析内容：
1. **执行计划分析**：解读执行计划中的关键操作（含 Spool 操作分析）
2. **统计信息评估**：检查估算行数与实际行数差异，识别统计信息过时
3. **缺失索引**：识别可能缺失的索引并给出创建建议
4. **性能瓶颈**：定位查询中的性能热点（全表扫描、隐式类型转换、参数嗅探、TempDB 压力、锁/阻塞等）
5. **优化方案**：给出具体的 SQL 改写建议和索引优化方案

输出格式：
- 使用 Markdown 格式
- 按照执行计划分析、统计信息评估、缺失索引、性能瓶颈、优化方案五个部分组织
- 提供具体的索引创建 T-SQL 语句
- 无问题则标注 ✅
- 输出语言：中文"
        },
        {
            PromptTemplate.Chat,
            @"你是一位数据库技术专家，精通 SQL Server / T-SQL，同时对 MySQL、PostgreSQL 等主流数据库有深入了解。

你的角色定位：
- 擅长 T-SQL / SQL 查询编写与优化
- 精通 SQL Server 性能优化（执行计划、索引、锁等）
- 能够进行跨数据库兼容性分析

约束：
- 当用户提供 SQL 代码时，优先分析代码中的问题
- 当用户问题不明确时，主动追问澄清
- 回答使用 Markdown 格式
- 输出语言：中文
- 保持简洁，重点突出"
        }
    };

    /// <inheritdoc />
    public string BuildPrompt(PromptTemplate template, string code, AIContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("代码不能为空", nameof(code));

        return template switch
        {
            PromptTemplate.Suggest => BuildSuggestPrompt(code, context),
            PromptTemplate.Comment => BuildCommentPrompt(code, context),
            PromptTemplate.Format => BuildFormatPrompt(code, context),
            PromptTemplate.Analyze => BuildAnalyzePrompt(code, context),
            PromptTemplate.Chat => BuildChatPrompt(code, context),
            _ => throw new ArgumentOutOfRangeException(nameof(template), $"不支持的模板类型: {template}")
        };
    }

    /// <inheritdoc />
    public string GetSystemPrompt(PromptTemplate template)
    {
        // 优先从缓存返回
        if (_systemPromptCache.TryGetValue(template, out var cached))
            return cached;

        // 尝试从嵌入资源加载
        var content = LoadEmbeddedResource(template);

        if (!string.IsNullOrEmpty(content))
        {
            _systemPromptCache[template] = content!;
            return content!;
        }

        // 资源加载失败时使用降级 prompt
        var fallback = FallbackSystemPrompts.TryGetValue(template, out var fb) ? fb! : "你是 SQL Server 助手。";
        _systemPromptCache[template] = fallback;
        return fallback;
    }

    #region User Prompt 构建

    /// <summary>构建「代码建议」user prompt</summary>
    private static string BuildSuggestPrompt(string code, AIContext? context)
    {
        var prompt = $"请对以下 SQL 代码进行审查并给出改进建议：\n\n```sql\n{code}\n```";
        AppendDatabaseContext(ref prompt, context);
        return prompt;
    }

    /// <summary>构建「注释生成」user prompt</summary>
    private static string BuildCommentPrompt(string code, AIContext? context)
    {
        var prompt = $"请为以下 SQL 代码添加注释：\n\n```sql\n{code}\n```";
        AppendDatabaseContext(ref prompt, context);
        return prompt;
    }

    /// <summary>构建「格式化」user prompt</summary>
    private static string BuildFormatPrompt(string code, AIContext? context)
    {
        var style = context?.FormatStyle ?? SSMSBoostPlus.Shared.FormatStyle.Microsoft;
        var styleName = style == SSMSBoostPlus.Shared.FormatStyle.Microsoft ? "Microsoft" : "Google";
        var prompt = $"请使用 {styleName} 风格对以下 SQL 代码进行格式化：\n\n```sql\n{code}\n```";
        AppendDatabaseContext(ref prompt, context);
        return prompt;
    }

    /// <summary>构建「自由对话」user prompt</summary>
    private static string BuildChatPrompt(string code, AIContext? context)
    {
        var prompt = code;
        AppendDatabaseContext(ref prompt, context);
        return prompt;
    }

    /// <summary>构建「性能分析」user prompt</summary>
    private static string BuildAnalyzePrompt(string code, AIContext? context)
    {
        var prompt = $"请对以下 SQL 代码进行性能分析：\n\n```sql\n{code}\n```";

        if (context != null && !string.IsNullOrWhiteSpace(context.SchemaInfo))
            prompt += $"\n\n---\n\n**Schema 信息：**\n\n{context.SchemaInfo}";

        if (context != null && !string.IsNullOrWhiteSpace(context.IndexInfo))
            prompt += $"\n\n---\n\n**索引信息：**\n\n{context.IndexInfo}";

        if (context != null && !string.IsNullOrWhiteSpace(context.ExecutionPlan))
            prompt += $"\n\n---\n\n**执行计划：**\n\n{context.ExecutionPlan!.Truncate(8000)}";


        AppendDatabaseContext(ref prompt, context);
        return prompt;
    }

    #endregion

    #region 私有辅助方法

    /// <summary>
    /// 追加数据库名称上下文到 prompt（如果可用）。
    /// </summary>
    private static void AppendDatabaseContext(ref string prompt, AIContext? context)
    {
        if (context?.DatabaseName != null)
            prompt = $"[数据库: {context.DatabaseName}]\n\n{prompt}";
    }

    /// <summary>
    /// 从嵌入的 Markdown 资源文件加载 system prompt 文本。
    /// </summary>
    private static string? LoadEmbeddedResource(PromptTemplate template)
    {
        try
        {
            var resourceName = TemplateResourceMap.TryGetValue(template, out var rn) ? rn : null;
            if (resourceName == null) return null;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            // 资源加载失败时不抛异常，由调用方使用降级 prompt
            return null;
        }
    }

    #endregion
}
