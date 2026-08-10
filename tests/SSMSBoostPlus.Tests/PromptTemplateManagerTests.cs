using System;
using FluentAssertions;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.Shared;
using Xunit;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// PromptTemplateManager 单元测试：验证各模板的 Prompt 构建，
/// 特别是 Analyze 模板在传入 AIContext 后是否包含 Schema、执行计划、索引信息。
/// </summary>
public class PromptTemplateManagerTests
{
    private readonly PromptTemplateManager _manager = new();

    // ============================================================
    // 1. Analyze 模板 + 完整上下文：Prompt 包含所有信息
    // ============================================================
    [Fact]
    public void BuildPrompt_AnalyzeWithFullContext_ContainsAllContextInfo()
    {
        var context = new AIContext
        {
            SchemaInfo = "CREATE TABLE Users (Id INT, Name NVARCHAR(100))",
            IndexInfo = "IX_Users_Id CLUSTERED INDEX ON Users(Id)",
            ExecutionPlan = "<ShowPlanXML><StmtSimple/></ShowPlanXML>",
            DatabaseName = "TestDB"
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Analyze, "SELECT * FROM Users", context);

        prompt.Should().Contain("Schema 信息");
        prompt.Should().Contain("CREATE TABLE Users");
        prompt.Should().Contain("索引信息");
        prompt.Should().Contain("IX_Users_Id");
        prompt.Should().Contain("执行计划");
        prompt.Should().Contain("ShowPlanXML");
        prompt.Should().Contain("TestDB");
    }

    // ============================================================
    // 2. Analyze 模板 + 无上下文：Prompt 仅包含 SQL 代码
    // ============================================================
    [Fact]
    public void BuildPrompt_AnalyzeWithoutContext_ContainsOnlyCode()
    {
        var prompt = _manager.BuildPrompt(PromptTemplate.Analyze, "SELECT * FROM Users", context: null);

        prompt.Should().Contain("SELECT * FROM Users");
        prompt.Should().NotContain("Schema 信息");
        prompt.Should().NotContain("索引信息");
        prompt.Should().NotContain("执行计划");
    }

    // ============================================================
    // 3. Analyze 模板 + 仅 SchemaInfo：Prompt 包含 Schema 但无其他
    // ============================================================
    [Fact]
    public void BuildPrompt_AnalyzeWithSchemaOnly_ContainsSchemaButNotOthers()
    {
        var context = new AIContext
        {
            SchemaInfo = "CREATE TABLE Orders (Id INT, Amount DECIMAL)"
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Analyze, "SELECT * FROM Orders", context);

        prompt.Should().Contain("Schema 信息");
        prompt.Should().Contain("CREATE TABLE Orders");
        prompt.Should().NotContain("索引信息");
        prompt.Should().NotContain("执行计划");
    }

    // ============================================================
    // 4. Analyze 模板 + 执行计划超长截断：Truncate(8000)
    // ============================================================
    [Fact]
    public void BuildPrompt_AnalyzeWithLongExecutionPlan_TruncatesPlan()
    {
        var longPlan = new string('X', 10000);
        var context = new AIContext
        {
            ExecutionPlan = longPlan
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Analyze, "SELECT 1", context);

        prompt.Should().Contain("执行计划");
        // 截断后长度应不超过 8000 + 标记文字
        prompt.Should().NotContain(new string('X', 10000));
    }

    // ============================================================
    // 5. Suggest 模板 + 数据库名称：Prompt 包含数据库名
    // ============================================================
    [Fact]
    public void BuildPrompt_SuggestWithDatabaseName_ContainsDatabaseName()
    {
        var context = new AIContext
        {
            DatabaseName = "MyDatabase"
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Suggest, "SELECT 1", context);

        prompt.Should().Contain("MyDatabase");
    }

    // ============================================================
    // 6. Comment 模板 + 数据库名称：Prompt 包含数据库名
    // ============================================================
    [Fact]
    public void BuildPrompt_CommentWithDatabaseName_ContainsDatabaseName()
    {
        var context = new AIContext
        {
            DatabaseName = "ProductionDB"
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Comment, "SELECT 1", context);

        prompt.Should().Contain("ProductionDB");
    }

    // ============================================================
    // 7. Format 模板 + Microsoft 风格：Prompt 包含风格名称
    // ============================================================
    [Fact]
    public void BuildPrompt_FormatMicrosoft_ContainsMicrosoftStyle()
    {
        var context = new AIContext
        {
            FormatStyle = FormatStyle.Microsoft
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Format, "select 1", context);

        prompt.Should().Contain("Microsoft");
    }

    // ============================================================
    // 8. Format 模板 + Google 风格：Prompt 包含风格名称
    // ============================================================
    [Fact]
    public void BuildPrompt_FormatGoogle_ContainsGoogleStyle()
    {
        var context = new AIContext
        {
            FormatStyle = FormatStyle.Google
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Format, "select 1", context);

        prompt.Should().Contain("Google");
    }

    // ============================================================
    // 9. 空代码抛异常
    // ============================================================
    [Fact]
    public void BuildPrompt_EmptyCode_ThrowsArgumentException()
    {
        var act = () => _manager.BuildPrompt(PromptTemplate.Suggest, "");

        act.Should().Throw<ArgumentException>().WithMessage("*代码不能为空*");
    }

    // ============================================================
    // 9b. Format 模板 + 数据库名称：Prompt 包含数据库名（新增）
    // ============================================================
    [Fact]
    public void BuildPrompt_FormatWithDatabaseName_ContainsDatabaseName()
    {
        var context = new AIContext
        {
            DatabaseName = "FormatDB"
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Format, "SELECT 1", context);

        prompt.Should().Contain("FormatDB");
    }

    // ============================================================
    // 9c. Chat 模板构建：直接返回代码原文
    // ============================================================
    [Fact]
    public void BuildPrompt_Chat_ReturnsCodeDirectly()
    {
        var prompt = _manager.BuildPrompt(PromptTemplate.Chat, "用户的问题是什么？", context: null);

        prompt.Should().Contain("用户的问题是什么？");
    }

    // ============================================================
    // 9d. Chat 模板 + 数据库名称：Prompt 包含数据库名
    // ============================================================
    [Fact]
    public void BuildPrompt_ChatWithDatabaseName_ContainsDatabaseName()
    {
        var context = new AIContext
        {
            DatabaseName = "ChatDB"
        };

        var prompt = _manager.BuildPrompt(PromptTemplate.Chat, "如何优化这个查询？", context);

        prompt.Should().Contain("ChatDB");
        prompt.Should().Contain("如何优化这个查询？");
    }

    // ============================================================
    // 10. GetSystemPrompt 各模板返回非空内容
    // ============================================================
    [Theory]
    [InlineData(PromptTemplate.Suggest)]
    [InlineData(PromptTemplate.Comment)]
    [InlineData(PromptTemplate.Format)]
    [InlineData(PromptTemplate.Analyze)]
    [InlineData(PromptTemplate.Chat)]
    public void GetSystemPrompt_AllTemplates_ReturnsNonEmptyString(PromptTemplate template)
    {
        var systemPrompt = _manager.GetSystemPrompt(template);

        systemPrompt.Should().NotBeNullOrEmpty();
    }
}
