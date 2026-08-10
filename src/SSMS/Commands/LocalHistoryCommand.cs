using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 打开 Local History 工具窗口的命令处理器。
/// 参照 AIChatCommand 的 FindToolWindow 模式。
/// 
/// VSCT: cmdidLocalHistory = 0x0410
/// </summary>
public sealed class LocalHistoryCommand : BaseCommand<LocalHistoryCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private SSMS.Services.Connection.ConnectionServiceFacade? _connectionService;
    private SsmsEditorService? _editorService;

    private LocalHistoryCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0410)
    {
    }

    /// <summary>
    /// 初始化命令（由 Package.InitializeAsync 调用）。
    /// </summary>
    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        IConfigurationService configService,
        SSMS.Services.Connection.ConnectionServiceFacade connectionService,
        SsmsEditorService editorService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new LocalHistoryCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService,
            _connectionService = connectionService,
            _editorService = editorService
        };
        logger.Information("[LocalHistory] ✅ 命令已注册 (cmdid=0x0410)");
    }

    /// <summary>
    /// 命令执行：查找或创建 LocalHistoryToolWindow 并显示。
    /// </summary>
    protected override void Execute(object sender, EventArgs e)
    {
        _logger.Information("[LocalHistory] 命令触发");

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ExecuteAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[LocalHistory] ExecuteAsync 未捕获异常");
            }
        });
    }

    private async Task ExecuteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger.Information("[LocalHistory] 正在查找/创建 ToolWindow...");

            // 通过 Package 查找或创建工具窗口
            var window = Package.FindToolWindow(
                typeof(UI.VersionHistory.LocalHistoryToolWindow),
                0,     // 单实例
                true); // 不存在则创建

            if (window?.Frame == null)
            {
                _logger.Error("[LocalHistory] ❌ 无法创建 LocalHistoryToolWindow (window or frame is null)");
                return;
            }

            _logger.Information("[LocalHistory] ToolWindow 已获取，正在初始化内容...");

            // 执行 3.3 节自动推断：连接串推导 + 对象推断
            if (window is UI.VersionHistory.LocalHistoryToolWindow historyWindow
                && _connectionService != null
                && _editorService != null)
            {
                historyWindow.InitializeContent(
                    Package, _configService, _logger,
                    _connectionService, _editorService);
                _logger.Information("[LocalHistory] InitializeContent 调用完成");
            }

            var frame = (IVsWindowFrame)window.Frame;
            ErrorHandler.ThrowOnFailure(frame.Show());
            _logger.Information("[LocalHistory] ✅ 版本历史面板已显示");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[LocalHistory] ❌ 打开版本历史面板失败");
        }
    }
}