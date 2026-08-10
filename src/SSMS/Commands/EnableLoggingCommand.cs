using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 启用/禁用日志记录切换命令。
/// VSCT: cmdidEnableLogging = 0x0304
/// 勾选时表示启用日志文件记录，不勾选则不写任何日志。
/// </summary>
public sealed class EnableLoggingCommand : BaseCommand<EnableLoggingCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private ILoggerService _appLogger; // 应用日志实例（运行时动态开关）
    private bool _isEnabled;

    private EnableLoggingCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0304)
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        IConfigurationService configService,
        ILoggerService appLogger)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));

        // 读取初始配置状态
        var initialConfig = await configService.GetLoggingConfigurationAsync().ConfigureAwait(false);

        Instance = new EnableLoggingCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService,
            _appLogger = appLogger,
            _isEnabled = initialConfig.Enabled
        };

        // 初始化时应用日志状态
        appLogger.SetEnabled(initialConfig.Enabled);
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
            var config = await _configService.GetLoggingConfigurationAsync().ConfigureAwait(false);
            config.Enabled = !config.Enabled;
            await _configService.SaveLoggingConfigurationAsync(config).ConfigureAwait(false);
            _isEnabled = config.Enabled;

            // 动态切换应用日志开关
            _appLogger.SetEnabled(config.Enabled);

            _logger.Information($"日志记录已{(config.Enabled ? "启用" : "禁用")}。");
        });
    }
}
