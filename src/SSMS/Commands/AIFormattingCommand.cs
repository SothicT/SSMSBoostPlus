using System;
using System.ComponentModel.Design;
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
/// Command handler for AI Formatting feature.
/// Formats the current SQL code (or selection) according to a configurable style.
///
/// Flow:
/// 1. Check if active document is SQL
/// 2. Verify AI is configured; prompt to configure if not
/// 3. Get code (selection or full document)
/// 4. Show "Formatting..." in status bar
/// 5. Call AI API for formatting
/// 6. Show side-by-side diff dialog (original vs formatted code)
/// 7. If user accepts AI code, replace in editor
///
/// VSCT: cmdidAIFormatting = 0x0202
/// Key binding: Ctrl+Shift+F
/// </summary>
public sealed class AIFormattingCommand : BaseCommand<AIFormattingCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private static AIStreamResultDialog? _activeDialog;

    private AIFormattingCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0202)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService configService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new AIFormattingCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService
        };
    }

    protected override void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command == null) return;

        var editorService = new Services.SsmsEditorService(Package, _logger);
        var isSql = editorService.IsSqlDocument();
        command.Visible = isSql;
        command.Enabled = isSql;

        _logger.Debug($"[BeforeQueryStatus] cmdidAIFormatting: Visible={command.Visible}, IsSqlDoc={isSql}");
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
                "AI 格式化功能需要一个活动的 SQL 文档。\n请打开 .sql 文件后重试。",
                "SSMSBoostPlus", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (code, isSelection) = await editorService.GetCodeAsync();
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show(
                "没有可供格式化的 SQL 代码。\n请选中部分代码或确保文档不为空。",
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

        var database = editorService.GetActiveDatabaseName();

        SetStatusText("SSMSBoostPlus: Formatting SQL code with AI...");

        try
        {
            // 构建实际发送给接口的 Prompt 文本
            var promptText = $"{AIDefaults.UserPromptPrefix}请格式化如下 SQL 代码（Microsoft 风格）：\n```sql\n{code}\n```";

            // 构建新 Chat API 参数（两步调用：GET /chat/api/open → POST /chat/api/chat_message/{chatId}）
            var baseUrl = AIDefaults.DefaultChatApiBaseUrl;
            var authToken = AIDefaults.DefaultAuthToken;

            // 立即弹出非模态结果弹窗（不锁死 SQL 编辑页面）
            _activeDialog = new AIStreamResultDialog("AI 格式化", promptText, baseUrl, authToken, promptText, _logger);
            _activeDialog.Closed += (_, _) => _activeDialog = null;
            _activeDialog.Show();

            _logger.Information("AI 格式化弹窗已打开（非模态）。");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI 格式化命令失败");
            MessageBox.Show(
                $"发生意外错误：\n{ex.Message}",
                "AI 格式化错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetStatusText(null);
        }
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
}
