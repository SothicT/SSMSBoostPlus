using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;
using SSMSBoostPlus.AI.Bridge;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.CodeAnalysis;
using SSMSBoostPlus.CodeAnalysis.Models;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.Shared;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// AI 代码分析服务测试：覆盖四个分析方法 + 边界场景 + 代码提取逻辑。
/// 所有外部依赖通过 Moq 注入。
/// </summary>
public class CodeAnalysisServiceTests
{
    private readonly Mock<ICodebuddyBridge> _mockBridge;
    private readonly Mock<IPromptTemplateManager> _mockPrompt;
    private readonly Mock<IConfigurationService> _mockConfig;
    private readonly Mock<ILoggerService> _mockLogger;
    private readonly DiffCalculator _diffCalculator;

    public CodeAnalysisServiceTests()
    {
        _mockBridge = new Mock<ICodebuddyBridge>();
        _mockPrompt = new Mock<IPromptTemplateManager>();
        _mockConfig = new Mock<IConfigurationService>();
        _mockLogger = new Mock<ILoggerService>();
        _diffCalculator = new DiffCalculator();
    }

    private CodeAnalysisService CreateService() =>
        new CodeAnalysisService(_mockBridge.Object, _mockPrompt.Object, _diffCalculator,
            _mockConfig.Object, _mockLogger.Object);

    /// <summary>
    /// 构建成功的 AI 响应（不含代码块）。
    /// </summary>
    private static AIResponse SuccessResponse(string content = "SELECT 1") => new AIResponse
    {
        IsSuccess = true,
        Content = content,
        InputTokens = 10,
        OutputTokens = 20
    };

    // ============================================================
    // 1. AI 未配置：GetSuggestionsAsync 返回失败
    // ============================================================
    [Fact]
    public async Task GetSuggestionsAsync_AINotConfigured_ReturnsFailure()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(false);

        var service = CreateService();
        var result = await service.GetSuggestionsAsync("SELECT * FROM Users");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AI 未配置");
        _mockBridge.Verify(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 2. GetSuggestionsAsync 成功：返回有效结果
    // ============================================================
    [Fact]
    public async Task GetSuggestionsAsync_Success_ReturnsValidResult()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse());

        var service = CreateService();
        var result = await service.GetSuggestionsAsync("SELECT * FROM Users");

        result.Success.Should().BeTrue();
        result.Type.Should().Be(AnalysisType.Suggestions);
    }

    // ============================================================
    // 3. GetSuggestionsAsync 失败（AI 返回错误）
    // ============================================================
    [Fact]
    public async Task GetSuggestionsAsync_BridgeFails_ReturnsFailure()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIResponse { IsSuccess = false, ErrorMessage = "API rate limit" });

        var service = CreateService();
        var result = await service.GetSuggestionsAsync("SELECT * FROM Users");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("API rate limit");
    }

    // ============================================================
    // 4. AddCommentsAsync 成功 + 代码提取（```sql 代码块）
    // ============================================================
    [Fact]
    public async Task AddCommentsAsync_WithCodeBlock_ExtractsContent()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse("```sql\n-- comment\nSELECT Id FROM Users;\n```"));

        var service = CreateService();
        var result = await service.AddCommentsAsync("SELECT Id FROM Users");

        result.Success.Should().BeTrue();
        result.SuggestedCode.Should().Be("-- comment\nSELECT Id FROM Users;");
    }

    // ============================================================
    // 5. AddCommentsAsync 无代码块标记：原文返回
    // ============================================================
    [Fact]
    public async Task AddCommentsAsync_NoCodeBlock_ReturnsRawContent()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse("SELECT Id FROM Users; -- comment"));

        var service = CreateService();
        var result = await service.AddCommentsAsync("SELECT Id FROM Users");

        result.Success.Should().BeTrue();
        result.SuggestedCode.Should().Be("SELECT Id FROM Users; -- comment");
    }

    // ============================================================
    // 6. FormatCodeAsync 使用默认格式风格
    // ============================================================
    [Fact]
    public async Task FormatCodeAsync_UsesDefaultFormatStyle()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockConfig.Setup(c => c.GetAIConfigurationAsync())
            .ReturnsAsync(new AIConfiguration { DefaultFormattingStyle = FormatStyle.Microsoft });
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse());

        var service = CreateService();
        var result = await service.FormatCodeAsync("select 1");

        result.Success.Should().BeTrue();
        _mockBridge.Verify(b => b.ExecuteAsync(
            It.Is<AIRequest>(r => r.FormatStyle == FormatStyle.Microsoft),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 7. FormatCodeAsync 覆盖风格
    // ============================================================
    [Fact]
    public async Task FormatCodeAsync_OverrideStyle_UsesGivenStyle()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockConfig.Setup(c => c.GetAIConfigurationAsync())
            .ReturnsAsync(new AIConfiguration { DefaultFormattingStyle = FormatStyle.Microsoft });
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse());

        var service = CreateService();
        var result = await service.FormatCodeAsync("select 1", FormatStyle.Google);

        result.Success.Should().BeTrue();
        _mockBridge.Verify(b => b.ExecuteAsync(
            It.Is<AIRequest>(r => r.FormatStyle == FormatStyle.Google),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 8. AnalyzePerformanceAsync 含执行计划
    // ============================================================
    [Fact]
    public async Task AnalyzePerformanceAsync_WithExecutionPlan_SetsPlan()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse());

        var planXml = "<ShowPlanXML>...</ShowPlanXML>";
        var service = CreateService();
        var result = await service.AnalyzePerformanceAsync("SELECT * FROM Users", planXml);

        result.Success.Should().BeTrue();
        _mockBridge.Verify(b => b.ExecuteAsync(
            It.Is<AIRequest>(r => r.ExecutionPlan == planXml),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 9. Bridge 抛异常：捕获返回失败
    // ============================================================
    [Fact]
    public async Task GetSuggestionsAsync_BridgeThrows_ReturnsFailure()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request timed out"));

        var service = CreateService();
        var result = await service.GetSuggestionsAsync("SELECT * FROM Users");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
    }

    // ============================================================
    // 10. AI 返回空内容：SuggestedCode 为 null
    // ============================================================
    [Fact]
    public async Task AddCommentsAsync_EmptyContent_ReturnsNullSuggestedCode()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse(null!));

        var service = CreateService();
        var result = await service.AddCommentsAsync("SELECT 1");

        result.Success.Should().BeTrue();
        result.SuggestedCode.Should().BeNullOrEmpty();
    }

    // ============================================================
    // 11. AnalyzePerformanceAsync 含完整上下文（Schema + 执行计划 + 索引）
    // ============================================================
    [Fact]
    public async Task AnalyzePerformanceAsync_WithFullContext_SetsAllFields()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse());

        var planXml = "<ShowPlanXML>...</ShowPlanXML>";
        var schema = "CREATE TABLE Users (Id INT, Name NVARCHAR(100))";
        var indexes = "IX_Users_Id CLUSTERED INDEX ON Users(Id)";

        var service = CreateService();
        var result = await service.AnalyzePerformanceAsync("SELECT * FROM Users", planXml, schema, indexes);

        result.Success.Should().BeTrue();
        _mockBridge.Verify(b => b.ExecuteAsync(
            It.Is<AIRequest>(r =>
                r.ExecutionPlan == planXml &&
                r.SchemaInfo == schema &&
                r.IndexInfo == indexes),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 12. AnalyzePerformanceAsync 仅 Schema + 索引，无执行计划
    // ============================================================
    [Fact]
    public async Task AnalyzePerformanceAsync_WithSchemaAndIndexButNoPlan_SetsFieldsCorrectly()
    {
        _mockConfig.Setup(c => c.IsAIConfiguredAsync()).ReturnsAsync(true);
        _mockBridge.Setup(b => b.ExecuteAsync(It.IsAny<AIRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResponse());

        var schema = "CREATE TABLE Orders (Id INT)";
        var indexes = "IX_Orders_Id ON Orders(Id)";

        var service = CreateService();
        var result = await service.AnalyzePerformanceAsync("SELECT * FROM Orders", null, schema, indexes);

        result.Success.Should().BeTrue();
        _mockBridge.Verify(b => b.ExecuteAsync(
            It.Is<AIRequest>(r =>
                r.ExecutionPlan == null &&
                r.SchemaInfo == schema &&
                r.IndexInfo == indexes),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}