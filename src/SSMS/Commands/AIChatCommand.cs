using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 打开 AI 聊天面板工具窗口的命令处理器。
/// 
/// VSCT: cmdidAIChatPanel = 0x0204
/// Key binding: Ctrl+Shift+K
/// </summary>
public sealed class AIChatCommand : BaseCommand<AIChatCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;

    private AIChatCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0204)
    {
    }

    /// <summary>
    /// 初始化命令（由 Package.InitializeAsync 调用）。
    /// </summary>
    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        IConfigurationService configService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new AIChatCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService
        };
        logger.Information("[AIChat] ✅ 命令已注册 (cmdid=0x0204)");
    }

    /// <summary>
    /// 命令执行：查找或创建 AIChatToolWindow 并显示。
    /// </summary>
    protected override void Execute(object sender, EventArgs e)
    {
        _logger.Information("[AIChat] 命令触发");

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ExecuteAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[AIChat] ExecuteAsync 未捕获异常");
            }
        });
    }

    private async Task ExecuteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger.Information("[AIChat] 正在查找/创建 ToolWindow...");

            // 通过 Package 查找或创建工具窗口
            var window = Package.FindToolWindow(
                typeof(UI.Chat.AIChatToolWindow),
                0,     // 单实例
                true); // 不存在则创建

            if (window?.Frame == null)
            {
                _logger.Error("[AIChat] ❌ 无法创建 AIChatToolWindow (window or frame is null)");
                return;
            }

            _logger.Information("[AIChat] ToolWindow 已获取，正在初始化内容...");

            // 确保窗口内容已初始化（首次创建时或布局恢复时）
            if (window is UI.Chat.AIChatToolWindow chatWindow)
            {
                chatWindow.InitializeContent(Package, _configService, _logger);
                _logger.Information("[AIChat] InitializeContent 调用完成");
            }
            else
            {
                _logger.Warning($"[AIChat] window 不是 AIChatToolWindow 类型: {window?.GetType().FullName}");
            }

            var frame = (IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            _logger.Information("[AIChat] ✅ AI 对话面板已显示");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[AIChat] ❌ 打开 AI 聊天面板失败");
        }
    }
}
