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
/// Command handler for AI Configuration dialog.
/// VSCT: cmdidAIConfig = 0x0300
/// </summary>
public sealed class AIConfigCommand : BaseCommand<AIConfigCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;

    private AIConfigCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0300)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService configService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new AIConfigCommand(package, commandService!)
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
            var dialog = new AIConfigDialog(_configService, _logger);
            dialog.ShowDialog();
        });
    }
}
