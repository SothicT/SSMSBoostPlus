using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services;
using SSMSBoostPlus.SSMS.UI.Dialogs;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// SQL 模板配置命令。
/// 打开 SQL 模板设置对话框，用户可以管理自定义模板缩写。
///
/// VSCT: cmdidSqlTemplateConfig = 0x0303
/// </summary>
public sealed class SqlTemplateConfigCommand : BaseCommand<SqlTemplateConfigCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private SqlTemplateService _templateService = null!;

    private SqlTemplateConfigCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0303)
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        SqlTemplateService templateService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new SqlTemplateConfigCommand(package, commandService!)
        {
            _logger = logger,
            _templateService = templateService
        };
    }

    protected override void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command == null) return;
        command.Visible = true;
        command.Enabled = true;
    }

    protected override void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dialog = new SqlTemplateConfigDialog(_templateService);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开 SQL 模板设置对话框失败");
            MessageBox.Show(
                $"打开 SQL 模板设置失败：\n{ex.Message}",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
