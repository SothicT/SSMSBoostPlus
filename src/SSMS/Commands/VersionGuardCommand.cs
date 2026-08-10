using System.IO;
using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard;
using SSMSBoostPlus.VersionGuard.Models;
using SSMSBoostPlus.SSMS.Services;
using SSMSBoostPlus.SSMS.Services.Connection;
using System.Collections.Generic;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// Command handler for Version Guard check.
/// Checks if the current SQL script references objects that have been
/// modified in the database since the script was opened.
/// VSCT: cmdidVersionGuardCheck = 0x0100
/// </summary>
public sealed class VersionGuardCommand : BaseCommand<VersionGuardCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private IConfigurationService _configService = null!;
    private IDatabaseService _databaseService = null!;
    private ConnectionServiceFacade _connectionService = null!;

    private VersionGuardCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0100)
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package, 
        ILoggerService logger, 
        IConfigurationService configService,
        IDatabaseService databaseService,
        ConnectionServiceFacade? connectionService = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new VersionGuardCommand(package, commandService!)
        {
            _logger = logger,
            _configService = configService,
            _databaseService = databaseService,
            _connectionService = connectionService ?? CreateDefaultConnectionService(package, logger)
        };
    }

    private static ConnectionServiceFacade CreateDefaultConnectionService(AsyncPackage package, ILoggerService logger)
    {
        var providers = new List<IConnectionInfoProvider>
        {
            new OfficialConnectionProvider(package, logger),
            new ReflectionConnectionProvider(package, logger)
        };
        return new ConnectionServiceFacade(providers, logger);
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
        // Show processing progress dialog
        var progressDialog = new UI.Dialogs.ProcessingDialog(
            "SSMSBoost++ - Version Check",
            "正在解析脚本中的数据库对象引用...");
        progressDialog.Show();
        progressDialog.UpdateDetail("正在分析 SQL 脚本...");

        try
        {
            var editorService = new Services.SsmsEditorService(Package, _logger);
            if (!editorService.IsSqlDocument())
            {
                progressDialog.CloseSafely();
                _logger.Warning("当前活动文档不是 SQL 文件。");
                return;
            }

            var (code, _) = await editorService.GetCodeAsync();
            if (string.IsNullOrWhiteSpace(code))
            {
                progressDialog.CloseSafely();
                _logger.Warning("没有可供检查的 SQL 代码。");
                return;
            }

            // Use new ConnectionServiceFacade with automatic fallback
            progressDialog.UpdateStatus("正在获取数据库连接信息...");
            progressDialog.UpdateDetail("正在获取连接凭据...");
            var connectionInfo = await _connectionService.GetConnectionInfoAsync();
            if (connectionInfo == null || string.IsNullOrEmpty(connectionInfo.ConnectionString))
            {
                _logger.Warning("没有活动的数据库连接。");
                System.Windows.MessageBox.Show(
                    "未检测到活动的数据库连接。\n\n" +
                    "请确保:\n" +
                    "1. 已连接到数据库服务器\n" +
                    "2. 查询窗口已关联连接\n\n" +
                    "请先连接数据库，然后打开新的查询窗口。",
                    "SSMSBoostPlus - 无连接",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var connectionString = connectionInfo.ConnectionString;
            var database = connectionInfo.DatabaseName ?? "dbo";

            _logger.Information($"Using connection from: {connectionInfo.SourceProvider}");
            _logger.Information($"Server: {connectionInfo.ServerInstance}, Database: {database}");

            progressDialog.UpdateStatus("正在执行版本比对...");
            progressDialog.UpdateDetail("正在与数据库对象进行比对...");

            var objectExtractor = new ObjectExtractor();
            var versionChecker = new VersionChecker(objectExtractor, _databaseService, _configService, _logger);

            // 使用编辑器打开时间缓存（比 File.GetLastWriteTime 更准确）
            var docPath = editorService.GetActiveDocumentPath();
            var scriptOpenTime = editorService.GetScriptOpenTime(docPath);

            var result = await versionChecker.CheckVersionAsync(code, scriptOpenTime, connectionString, database);

            progressDialog.CloseSafely();

            if (result.Skipped)
            {
                _logger.Information("版本检查已跳过。");
                return;
            }

            if (result.HasError)
            {
                _logger.Error($"版本检查出错: {result.ErrorMessage}");
                return;
            }

            if (result.HasOutdated)
            {
                var dialogService = new SsmsDialogService(Package, _configService, _connectionService);
                var userChoice = await dialogService.ShowVersionWarningDialog(
                    result,
                    code,
                    vi => versionChecker.GetLatestDefinitionAsync(
                        connectionString,
                        vi.ObjectRef.Database ?? database,
                        vi.ObjectRef.Schema,
                        vi.ObjectRef.Name),
                    vi => versionChecker.GetLatestModifyDateAsync(
                        connectionString,
                        vi.ObjectRef.Database ?? database,
                        vi.ObjectRef.Schema,
                        vi.ObjectRef.Name),
                    vi => versionChecker.RefreshComparisonAsync(
                        connectionString, code, vi));

                switch (userChoice)
                {
                    case VersionWarningResult.ReloadFromDb:
                        var outdatedObj = result.OutdatedObjects.First();
                        var latestDef = await versionChecker.GetLatestDefinitionAsync(
                            connectionString,
                            outdatedObj.ObjectRef.Database ?? database,
                            outdatedObj.ObjectRef.Schema,
                            outdatedObj.ObjectRef.Name);

                        if (latestDef != null)
                        {
                            await editorService.ReplaceAllAsync(latestDef);
                            _logger.Information($"已从数据库重新加载 {outdatedObj.ObjectRef.FullName} 的定义。");
                        }
                        else
                        {
                            _logger.Warning($"无法获取 {outdatedObj.ObjectRef.FullName} 的定义。");
                        }
                        break;

                    case VersionWarningResult.ContinueExecution:
                        _logger.Information("用户选择继续使用当前脚本执行。");
                        break;

                    case VersionWarningResult.Cancel:
                        _logger.Information("用户取消了执行。");
                        break;
                }
            }
            else
            {
                _logger.Information("版本检查通过 — 未发现过时对象。");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "版本守卫命令失败");
        }
        finally
        {
            progressDialog?.CloseSafely();
        }
    }
}
