using System;
using System.Threading.Tasks;
using SSMSBoostPlus.AI.Bridge;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.CodeAnalysis.Models;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.DiffEngine.Models;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.Shared;

namespace SSMSBoostPlus.CodeAnalysis;

/// <summary>
/// 代码分析服务编排层，协调 Bridge + PromptManager + DiffEngine 完成完整的 AI 分析流程。
/// 
/// 职责：
/// 1. 接收上层命令的分析请求（建议/注释/格式化/性能分析）
/// 2. 通过 IPromptTemplateManager 构建 prompt
/// 3. 通过 ICodebuddyBridge 调用 AI 服务
/// 4. 解析响应并提取 SQL 代码
/// 5. 提供差异计算能力
/// 
/// 注意：此服务位于 AI 子项目中，避免 Core ↔ AI 的循环依赖。
/// </summary>
public class CodeAnalysisService
{
    private readonly ICodebuddyBridge _bridge;
    private readonly IPromptTemplateManager _promptManager;
    private readonly DiffCalculator _diffCalculator;
    private readonly IConfigurationService _configurationService;
    private readonly ILoggerService _logger;

    /// <summary>
    /// 初始化代码分析服务。
    /// </summary>
    /// <param name="bridge">AI 桥接实例（CLI 或 HTTP API）</param>
    /// <param name="promptManager">Prompt 模板管理器</param>
    /// <param name="diffCalculator">差异计算引擎</param>
    /// <param name="configurationService">配置服务</param>
    /// <param name="logger">日志服务</param>
    public CodeAnalysisService(
        ICodebuddyBridge bridge,
        IPromptTemplateManager promptManager,
        DiffCalculator diffCalculator,
        IConfigurationService configurationService,
        ILoggerService logger)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _diffCalculator = diffCalculator ?? throw new ArgumentNullException(nameof(diffCalculator));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region 公开分析方法

    /// <summary>
    /// 获取 AI 代码建议（语法错误 + 代码精简 + 性能优化）。
    /// </summary>
    /// <param name="sqlCode">待分析的 SQL 代码</param>
    /// <param name="databaseContext">数据库上下文（已弃用，请使用 databaseName）</param>
    /// <param name="databaseName">数据库名称，用于增强 AI 上下文</param>
    /// <param name="readOnly">只读模式：为 true 时 AI 仅输出建议不修改文件</param>
    public async Task<AnalysisResult> GetSuggestionsAsync(string sqlCode, string? databaseContext = null, string? databaseName = null, bool readOnly = false)
    {
        var request = BuildRequest(PromptTemplate.Suggest, sqlCode, readOnly);
        request.Code = sqlCode;
        request.DatabaseName = databaseName;

        return await ExecuteAnalysisAsync(AnalysisType.Suggestions, sqlCode, request);
    }

    /// <summary>
    /// 为 SQL 代码添加 AI 生成的注释。
    /// </summary>
    /// <param name="sqlCode">待注释的 SQL 代码</param>
    /// <param name="databaseName">数据库名称，用于增强 AI 上下文</param>
    /// <param name="readOnly">只读模式：为 true 时 AI 仅输出建议不修改文件</param>
    public async Task<AnalysisResult> AddCommentsAsync(string sqlCode, string? databaseName = null, bool readOnly = false)
    {
        var request = BuildRequest(PromptTemplate.Comment, sqlCode, readOnly);
        request.DatabaseName = databaseName;
        return await ExecuteAnalysisAsync(AnalysisType.Comments, sqlCode, request);
    }

    /// <summary>
    /// 使用 AI 格式化 SQL 代码。
    /// </summary>
    /// <param name="sqlCode">待格式化的 SQL 代码</param>
    /// <param name="style">格式化风格</param>
    /// <param name="databaseName">数据库名称，用于增强 AI 上下文</param>
    /// <param name="readOnly">只读模式：为 true 时 AI 仅输出建议不修改文件</param>
    public async Task<AnalysisResult> FormatCodeAsync(string sqlCode, Shared.FormatStyle? style = null, string? databaseName = null, bool readOnly = false)
    {
        var config = await _configurationService.GetAIConfigurationAsync();
        var effectiveStyle = style ?? config.DefaultFormattingStyle;

        var request = BuildRequest(PromptTemplate.Format, sqlCode, readOnly);
        request.FormatStyle = effectiveStyle;
        request.DatabaseName = databaseName;

        return await ExecuteAnalysisAsync(AnalysisType.Formatting, sqlCode, request);
    }

    /// <summary>
    /// 分析 SQL 性能（执行计划解读 + 缺失索引 + 瓶颈定位）。
    /// </summary>
    /// <param name="sqlCode">待分析的 SQL 代码</param>
    /// <param name="executionPlanXml">执行计划 XML（可选）</param>
    /// <param name="schemaInfo">数据库表 Schema 信息（可选，增强分析准确性）</param>
    /// <param name="indexInfo">数据库表索引信息（可选，增强分析准确性）</param>
    /// <param name="databaseName">数据库名称（可选，增强 AI 上下文）</param>
    /// <param name="readOnly">只读模式：为 true 时 AI 仅输出建议不修改文件</param>
    public async Task<AnalysisResult> AnalyzePerformanceAsync(
        string sqlCode, string? executionPlanXml = null,
        string? schemaInfo = null, string? indexInfo = null,
        string? databaseName = null, bool readOnly = false)
    {
        var request = BuildRequest(PromptTemplate.Analyze, sqlCode, readOnly);
        request.ExecutionPlan = executionPlanXml;
        request.SchemaInfo = schemaInfo;
        request.IndexInfo = indexInfo;
        request.DatabaseName = databaseName;

        return await ExecuteAnalysisAsync(AnalysisType.Performance, sqlCode, request);
    }

    #endregion

    #region 差异计算

    /// <summary>
    /// 计算原始代码与建议代码之间的文本差异。
    /// </summary>
    public DiffResult CalculateDiff(string originalCode, string suggestedCode)
    {
        return _diffCalculator.CalculateDiff(originalCode, suggestedCode);
    }

    #endregion

    #region 私有方法

    /// <summary>
    /// 构建通用的 AI 请求对象。
    /// </summary>
    private static AIRequest BuildRequest(PromptTemplate template, string code, bool readOnly = false)
    {
        return new AIRequest
        {
            Template = template,
            Code = code,
            ReadOnly = readOnly
        };
    }

    /// <summary>
    /// 执行 AI 分析的核心流程：检查配置 → 调用 Bridge → 解析响应 → 提取代码。
    /// </summary>
    private async Task<AnalysisResult> ExecuteAnalysisAsync(
        AnalysisType type,
        string originalCode,
        AIRequest request)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 前置检查：确认 AI 已配置
            if (!await _configurationService.IsAIConfiguredAsync())
            {
                return CreateFailureResult(type, originalCode,
                    "AI 未配置。请在设置中配置 API Key 和 Endpoint。");
            }

            _logger.Information($"[CodeAnalysis] 发送 {type} 分析请求...");

            // 通过 Bridge 执行 AI 请求
            var response = await _bridge.ExecuteAsync(request);

            stopwatch.Stop();

            // 处理失败响应
            if (!response.IsSuccess)
            {
                _logger.Error($"[CodeAnalysis] AI {type} 分析失败: {response.ErrorMessage}");
                return CreateFailureResult(type, originalCode,
                    response.ErrorMessage ?? "未知错误",
                    stopwatch.ElapsedMilliseconds);
            }

            // 构建成功结果
            var result = new AnalysisResult
            {
                Type = type,
                Success = true,
                OriginalCode = originalCode,
                Content = response.Content ?? string.Empty,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Usage = response.InputTokens > 0 || response.OutputTokens > 0
                    ? new TokenUsage
                    {
                        PromptTokens = response.InputTokens,
                        CompletionTokens = response.OutputTokens,
                        TotalTokens = response.InputTokens + response.OutputTokens
                    }
                    : null
            };

            // 对于建议/注释/格式化类型：从 Markdown 响应中提取 SQL 代码
            if (type is AnalysisType.Suggestions or AnalysisType.Comments or AnalysisType.Formatting)
            {
                result.SuggestedCode = ExtractSqlFromResponse(response.Content ?? string.Empty);
            }

            _logger.Information(
                $"[CodeAnalysis] AI {type} 分析完成，耗时 {stopwatch.ElapsedMilliseconds}ms，"
                + $"Token 用量: {response.InputTokens + response.OutputTokens}");

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(ex, $"[CodeAnalysis] AI {type} 分析异常");
            return CreateFailureResult(type, originalCode, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 创建失败的分析结果对象。
    /// </summary>
    private static AnalysisResult CreateFailureResult(
        AnalysisType type, string originalCode, string errorMessage, long elapsedMs = 0)
    {
        return new AnalysisResult
        {
            Type = type,
            Success = false,
            OriginalCode = originalCode,
            ErrorMessage = errorMessage,
            ElapsedMs = elapsedMs
        };
    }

    /// <summary>
    /// 从 AI 响应文本中提取 SQL 代码（去除 ```sql ... ``` Markdown 代码块包裹）。
    /// </summary>
    private static string ExtractSqlFromResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        // 尝试从 ```sql 或 ``` 代码块中提取内容
        var codeBlockStart = content.IndexOf("```sql", StringComparison.OrdinalIgnoreCase);
        if (codeBlockStart < 0)
            codeBlockStart = content.IndexOf("```", StringComparison.OrdinalIgnoreCase);

        if (codeBlockStart >= 0)
        {
            var contentStart = content.IndexOf('\n', codeBlockStart) + 1;
            var codeBlockEnd = content.IndexOf("```", contentStart, StringComparison.OrdinalIgnoreCase);

            if (codeBlockEnd > contentStart)
            {
                return content.Substring(contentStart, codeBlockEnd - contentStart).Trim();
            }
        }

        // 无代码块标记，返回原文
        return content.Trim();
    }

    #endregion
}
