using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.SSMS.UI.Dialogs;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// Command handler for Version Guard Configuration dialog.
/// VSCT: cmdidVersionGuardConfig = 0x0301
/// </summary>
public sealed class VersionGuardConfigCommand : BaseCommand<VersionGuardConfigCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;

    private VersionGuardConfigCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0301)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService configService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new VersionGuardConfigCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService
        };
    }

    protected override void Execute(object sender, EventArgs e)
    {
        ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dialog = new VersionGuardConfigDialog(_configService, _logger);
            dialog.ShowDialog();
        });
    }
}
