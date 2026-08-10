using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// Command handler for Version Guard enable/disable toggle.
/// VSCT: cmdidVersionGuardEnableToggle = 0x0101
/// </summary>
public sealed class VersionGuardEnableToggleCommand : BaseCommand<VersionGuardEnableToggleCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private bool _isEnabled; // 缓存配置状态，避免 OnBeforeQueryStatus 中同步阻塞

    private VersionGuardEnableToggleCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0101)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService configService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));

        // 读取初始配置状态
        var initialConfig = await configService.GetVersionGuardConfigurationAsync().ConfigureAwait(false);

        Instance = new VersionGuardEnableToggleCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService,
            _isEnabled = initialConfig.Enabled
        };
    }

    protected override void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command == null) return;

        // 直接读缓存字段，避免同步阻塞 UI 线程
        command.Checked = _isEnabled;
        command.Visible = true;
        command.Enabled = true;
    }

    protected override void Execute(object sender, EventArgs e)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var config = await _configService.GetVersionGuardConfigurationAsync().ConfigureAwait(false);
            config.Enabled = !config.Enabled;
            await _configService.SaveVersionGuardConfigurationAsync(config).ConfigureAwait(false);
            _isEnabled = config.Enabled; // 更新缓存
            _logger.Information($"Version Guard {(config.Enabled ? "enabled" : "disabled")}.");
        });
    }
}
