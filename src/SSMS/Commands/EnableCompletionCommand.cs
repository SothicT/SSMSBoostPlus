using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 启用/禁用代码补全弹窗切换命令。
/// VSCT: cmdidEnableCompletion = 0x0305
/// 勾选时输入时弹出代码补全提示弹窗，不勾选则不弹出。
/// </summary>
public sealed class EnableCompletionCommand : BaseCommand<EnableCompletionCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private bool _isEnabled;

    private EnableCompletionCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0305)
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        IConfigurationService configService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));

        // 读取初始配置状态
        var initialConfig = await configService.GetCompletionConfigurationAsync().ConfigureAwait(false);

        Instance = new EnableCompletionCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService,
            _isEnabled = initialConfig.Enabled
        };

        // 初始化时应用代码补全状态
        SqlTemplateCompletionService.IsEnabled = initialConfig.Enabled;
    }

    protected override void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command == null) return;

        command.Checked = _isEnabled;
        command.Visible = true;
        command.Enabled = true;
    }

    protected override void Execute(object sender, EventArgs e)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var config = await _configService.GetCompletionConfigurationAsync().ConfigureAwait(false);
            config.Enabled = !config.Enabled;
            await _configService.SaveCompletionConfigurationAsync(config).ConfigureAwait(false);
            _isEnabled = config.Enabled;

            // 动态切换代码补全开关
            SqlTemplateCompletionService.IsEnabled = config.Enabled;

            _logger.Information($"代码补全已{(config.Enabled ? "启用" : "禁用")}。");
        });
    }
}
