using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.AI.Bridge;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.CodeAnalysis;
using SSMSBoostPlus.CodeAnalysis.Models;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.UI.Dialogs;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// Command handler for AI Performance Analysis feature.
/// Analyzes the current SQL code for performance issues and provides
/// optimization suggestions including missing index recommendations.
///
/// Flow:
/// 1. Check if active document is SQL
/// 2. Verify AI is configured; prompt to configure if not
/// 3. Get code + database context + optional execution plan
/// 4. Show "Analyzing performance..." in status bar
/// 5. Call AI API for performance analysis
/// 6. Display results in a dedicated performance report dialog
/// 7. If suggested code is returned, offer to apply via diff dialog
///
/// VSCT: cmdidAIPerformance = 0x0203
/// Key binding: Ctrl+Shift+P
/// </summary>
public sealed class AIPerformanceCommand : BaseCommand<AIPerformanceCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private IDatabaseService _databaseService = null!;
    private static AIStreamResultDialog? _activeDialog;

    private AIPerformanceCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0203)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService configService, IDatabaseService databaseService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new AIPerformanceCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService,
            _databaseService = databaseService
        };
    }

    protected override void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command == null) return;

        var editorService = new Services.SsmsEditorService(Package, _logger);
        command.Visible = editorService.IsSqlDocument();
        command.Enabled = command.Visible;
    }

    protected override void Execute(object sender, EventArgs e)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ExecuteAsync();
        });
    }

    private async Task ExecuteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var editorService = new Services.SsmsEditorService(Package);

        if (!editorService.IsSqlDocument())
        {
            MessageBox.Show(
                "AI 性能分析功能需要一个活动的 SQL 文档。\n请打开 .sql 文件后重试。",
                "SSMSBoostPlus", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (code, _) = await editorService.GetCodeAsync();
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show(
                "没有可供分析的 SQL 代码。\n请确保文档不为空。",
                "SSMSBoostPlus", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!await _configService.IsAIConfiguredAsync())
        {
            var configure = MessageBox.Show(
                "AI 尚未配置，是否打开 AI 配置对话框？",
                "AI 未配置", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (configure == MessageBoxResult.Yes)
            {
                var configDialog = new AIConfigDialog(_configService, _logger);
                configDialog.ShowDialog();
            }
            return;
        }

        // Get database context for better analysis
        var connectionString = editorService.GetActiveConnectionString();
        var database = editorService.GetActiveDatabaseName();

        // Try to get execution plan if connected
        string? executionPlanXml = null;
        string? schemaInfo = null;
        string? indexInfo = null;

        if (!string.IsNullOrEmpty(connectionString))
        {
            try
            {
                // 获取执行计划
                executionPlanXml = await _databaseService.GetExecutionPlanAsync(connectionString, code);

                // 从 SQL 中提取表名，获取 Schema 和索引信息
                var tableNames = ExtractTableNames(code);
                if (tableNames.Count > 0)
                {
                    SetStatusText("SSMSBoostPlus: 正在获取数据库 Schema 信息...");
                    var schemaBuilder = new List<string>();
                    var indexBuilder = new List<string>();

                    // 最多处理 10 张表，避免 Prompt 过长
                    foreach (var tableName in tableNames.Take(10))
                    {
                        try
                        {
                            var schema = await _databaseService.GetTableSchemaAsync(connectionString, tableName);
                            if (schema != null) schemaBuilder.Add(schema);

                            var indexes = await _databaseService.GetTableIndexesAsync(connectionString, tableName);
                            if (indexes != null) indexBuilder.Add(indexes);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"获取表 {tableName} 的 Schema/Index 失败: {ex.Message}");
                            // 跳过该表，继续处理其他表
                        }
                    }

                    if (schemaBuilder.Count > 0)
                        schemaInfo = string.Join("\n\n", schemaBuilder);

                    if (indexBuilder.Count > 0)
                        indexInfo = string.Join("\n\n", indexBuilder);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"无法获取数据库上下文信息: {ex.Message}");
                // Continue without schema/index info
            }
        }

        SetStatusText("SSMSBoostPlus: 正在使用 AI 分析性能...");

        try
        {
            // 构建实际发送给接口的 Prompt 文本
            var promptBase = $"请对如下 SQL 代码进行性能分析：\n```sql\n{code}\n```";
            if (!string.IsNullOrWhiteSpace(schemaInfo))
                promptBase += $"\n\nSchema 信息：\n{schemaInfo}";
            if (!string.IsNullOrWhiteSpace(indexInfo))
                promptBase += $"\n\n索引信息：\n{indexInfo}";
            if (!string.IsNullOrWhiteSpace(executionPlanXml))
                promptBase += $"\n\n执行计划：\n{executionPlanXml}";
            var promptText = $"{AIDefaults.UserPromptPrefix}{promptBase}";

            // 构建新 Chat API 参数（两步调用：GET /chat/api/open → POST /chat/api/chat_message/{chatId}）
            var baseUrl = AIDefaults.DefaultChatApiBaseUrl;
            var authToken = AIDefaults.DefaultAuthToken;

            // 立即弹出非模态结果弹窗（不锁死 SQL 编辑页面）
            _activeDialog = new AIStreamResultDialog("AI 性能分析", promptText, baseUrl, authToken, promptText, _logger);
            _activeDialog.Closed += (_, _) => _activeDialog = null;
            _activeDialog.Show();

            _logger.Information("AI 性能分析弹窗已打开（非模态）。");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 性能分析命令失败");
            MessageBox.Show(
                $"发生意外错误：\n{ex.Message}",
                "AI 性能分析错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetStatusText(null);
        }
    }

    /// <summary>
    /// 从 AI 响应文本中提取 SQL 代码（去除 ```sql ... ``` Markdown 代码块包裹）。
    /// </summary>
    private static string ExtractSqlFromResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var codeBlockStart = content.IndexOf("```sql", StringComparison.OrdinalIgnoreCase);
        if (codeBlockStart < 0)
            codeBlockStart = content.IndexOf("```", StringComparison.OrdinalIgnoreCase);

        if (codeBlockStart >= 0)
        {
            var contentStart = content.IndexOf('\n', codeBlockStart) + 1;
            if (contentStart > 0)
            {
                var codeBlockEnd = content.IndexOf("```", contentStart, StringComparison.OrdinalIgnoreCase);
                if (codeBlockEnd > contentStart)
                {
                    return content.Substring(contentStart, codeBlockEnd - contentStart).Trim();
                }
            }
        }

        return content.Trim();
    }

    private void SetStatusText(string? text)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var statusBar = (IVsStatusbar?)((System.IServiceProvider)Package).GetService(typeof(SVsStatusbar));
            if (statusBar != null)
            {
                if (text != null) statusBar.SetText(text);
                else statusBar.Clear();
            }
        }
        catch { }
    }

    /// <summary>
    /// 从 SQL 代码中提取引用的表名。
    /// 支持 FROM、JOIN、UPDATE、INSERT INTO、DELETE FROM 子句中的表名解析。
    /// </summary>
    private static List<string> ExtractTableNames(string sql)
    {
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 匹配 FROM / JOIN / UPDATE / INSERT INTO / DELETE FROM 后面的表名
        // 支持: [schema].[table]、schema.table、[table]、table
        var pattern = @"(?:FROM|JOIN|UPDATE|INTO|DELETE\s+FROM)\s+"
                    + @"(?:\[?(?<schema>\w+)\]?\.)?\[?(?<table>\w+)\]?";

        var matches = Regex.Matches(
            sql, pattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        foreach (Match match in matches)
        {
            var schema = match.Groups["schema"].Value;
            var table = match.Groups["table"].Value;

            // 过滤 SQL 关键字（子查询关键字不应被识别为表名）
            var sqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT", "WHERE", "SET", "VALUES", "ON", "AND", "OR", "NOT",
                "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL", "AS", "WITH"
            };

            if (sqlKeywords.Contains(table))
                continue;

            var fullName = string.IsNullOrEmpty(schema) || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)
                ? $"dbo.{table}"
                : $"{schema}.{table}";

            tableNames.Add(fullName);
        }

        return tableNames.ToList();
    }
}
