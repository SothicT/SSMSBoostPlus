using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services;

namespace SSMSBoostPlus.SSMS.Commands
{
    /// <summary>
    /// 刷新数据库对象缓存命令。
    /// 获取当前 SQL 编辑器的数据库连接，拉取表、SP、函数等对象名称，覆盖旧缓存。
    ///
    /// VSCT: cmdidRefreshCache = 0x0207
    /// </summary>
    public sealed class RefreshCacheCommand : BaseCommand<RefreshCacheCommand>
    {
        private ILoggerService _logger = new ConsoleLoggerService();
        private DatabaseObjectCacheService? _cacheService;

        private RefreshCacheCommand(AsyncPackage package, OleMenuCommandService commandService)
            : base(package, commandService, 0x0207)
        {
        }

        public static async Task InitializeAsync(
            AsyncPackage package,
            ILoggerService logger,
            DatabaseObjectCacheService cacheService)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
            Instance = new RefreshCacheCommand(package, commandService!)
            {
                _logger = logger,
                _cacheService = cacheService
            };
        }

        protected override void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            var command = sender as OleMenuCommand;
            if (command == null) return;

            var editorService = new SsmsEditorService(Package, _logger);
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
                // 获取状态栏
                var statusBar = await Package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
                statusBar?.SetText("SSMSBoostPlus: 正在刷新数据库对象缓存...");

                // 订阅缓存服务的状态通知
                if (_cacheService != null)
                {
                    _cacheService.StatusChanged += OnStatusChanged;
                    try
                    {
                        var success = await _cacheService.RefreshCacheForCurrentConnectionAsync();

                        if (success)
                        {
                            // 2秒后清除状态栏
                            await Task.Delay(2000);
                        }
                    }
                    finally
                    {
                        _cacheService.StatusChanged -= OnStatusChanged;
                    }
                }

                statusBar?.Clear();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[RefreshCache] 刷新缓存命令执行失败");
            }
        }

        private void OnStatusChanged(string status)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var statusBar = await Package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
                statusBar?.SetText($"SSMSBoostPlus: {status}");
            });
        }
    }
}
