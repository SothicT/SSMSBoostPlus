using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// Command handler for reloading code from database.
/// Runs a version check on the current script and reloads the latest
/// definition for any outdated objects found.
///
/// VSCT: cmdidVersionGuardReloadFromDb = 0x0102
/// </summary>
public sealed class VersionGuardReloadCommand : BaseCommand<VersionGuardReloadCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private IDatabaseService _databaseService = null!;

    private VersionGuardReloadCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0102)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService configService, IDatabaseService databaseService)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new VersionGuardReloadCommand(package, commandService!)
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

        try
        {
            var editorService = new Services.SsmsEditorService(Package);

            if (!editorService.IsSqlDocument())
            {
                MessageBox.Show("此命令需要一个活动的 SQL 文档。", "SSMSBoostPlus",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (code, _) = await editorService.GetCodeAsync();
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show("当前活动文档中没有 SQL 代码。", "SSMSBoostPlus",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var connectionString = editorService.GetActiveConnectionString();
            if (string.IsNullOrEmpty(connectionString))
            {
                MessageBox.Show("没有活动的数据库连接。\n请先连接到数据库。",
                    "SSMSBoostPlus", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var database = editorService.GetActiveDatabaseName() ?? "dbo";

            // Run version check
            var objectExtractor = new ObjectExtractor();
            var versionChecker = new VersionChecker(objectExtractor, _databaseService, _configService, _logger);

            var docPath = editorService.GetActiveDocumentPath();
            var scriptOpenTime = docPath != null && File.Exists(docPath)
                ? File.GetLastWriteTime(docPath)
                : DateTime.Now;

            var result = await versionChecker.CheckVersionAsync(code, scriptOpenTime, connectionString, database);

            if (result.Skipped)
            {
                MessageBox.Show("Version check was skipped (disabled or skip token found).", "Reload from DB",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (result.HasError)
            {
                MessageBox.Show($"版本检查失败：\n{result.ErrorMessage}", "从数据库重新加载",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!result.HasOutdated)
            {
                MessageBox.Show("未发现过时对象，您的脚本已是最新版本。", "从数据库重新加载",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Reload the most outdated object (primary use case)
            var outdatedObj = result.OutdatedObjects[0];
            var latestDef = await versionChecker.GetLatestDefinitionAsync(
                connectionString,
                outdatedObj.ObjectRef.Database ?? database,
                outdatedObj.ObjectRef.Schema,
                outdatedObj.ObjectRef.Name);

            if (latestDef != null)
            {
                await editorService.ReplaceAllAsync(latestDef);
                _logger.Information($"已从数据库重新加载 {outdatedObj.ObjectRef.FullName} 的定义。");

                var msg = result.OutdatedObjects.Count > 1
                    ? $"已重新加载: {outdatedObj.ObjectRef.FullName}\n\n" +
                      $"注意: 另外还有 {result.OutdatedObjects.Count - 1} 个过时对象。\n" +
                      "请再次运行检查以查看其余对象。"
                    : $"已重新加载: {outdatedObj.ObjectRef.FullName}";

                MessageBox.Show(msg, "从数据库重新加载", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"无法获取 {outdatedObj.ObjectRef.FullName} 的定义。\n" +
                    "该对象可能没有 SQL 模块定义（如表）。",
                    "从数据库重新加载", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "从数据库重新加载命令失败");
            MessageBox.Show($"发生错误：\n{ex.Message}", "从数据库重新加载",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
