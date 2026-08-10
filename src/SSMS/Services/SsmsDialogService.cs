using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.SSMS.UI.Dialogs;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.SSMS.Services;

/// <summary>
/// SSMS-specific implementation of IDialogService using non-modal WPF dialogs.
/// Non-modal dialogs (Show()) do not block the SSMS UI thread, allowing users
/// to interact with SSMS while version guard dialogs are open.
/// ⚠️ Static references prevent GC from collecting the dialogs while shown.
/// </summary>
public class SsmsDialogService : IDialogService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationService? _configService;
    private readonly SSMS.Services.Connection.ConnectionServiceFacade? _connectionFacade;

    /// <summary>
    /// 默认构造（兼容旧调用方，不含版本守卫所需服务）
    /// </summary>
    public SsmsDialogService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 完整构造（含版本守卫所需服务）
    /// </summary>
    public SsmsDialogService(
        IServiceProvider serviceProvider,
        IConfigurationService configService,
        SSMS.Services.Connection.ConnectionServiceFacade connectionFacade)
    {
        _serviceProvider = serviceProvider;
        _configService = configService;
        _connectionFacade = connectionFacade;
    }

    // ═══════════════════════════════════════════════════
    // 强引用保护：非模态弹窗必须持有静态引用，防止 GC 回收
    // ═══════════════════════════════════════════════════
    private static VersionWarningDialog? _activeVersionWarning;
    private static AIDiffDialog? _activeDiffDialog;

    /// <inheritdoc />
    public Task<VersionWarningResult> ShowVersionWarningDialog(
        VersionCheckResult versionInfo,
        string currentScriptCode,
        Func<VersionInfo, Task<string?>> getDbDefinition,
        Func<VersionInfo, Task<DateTime?>> getLatestModifyDate,
        Func<VersionInfo, Task> refreshComparison)
    {
        var tcs = new TaskCompletionSource<VersionWarningResult>();

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var editorService = new SsmsEditorService(_serviceProvider);
            _activeVersionWarning = new VersionWarningDialog(
                versionInfo, currentScriptCode, getDbDefinition, getLatestModifyDate, refreshComparison, editorService,
                _configService ?? throw new InvalidOperationException("IConfigurationService is required for VersionWarningDialog"),
                _connectionFacade ?? throw new InvalidOperationException("ConnectionServiceFacade is required for VersionWarningDialog"));

            _activeVersionWarning.Closed += (_, _) =>
            {
                tcs.TrySetResult(_activeVersionWarning.Result);
                _activeVersionWarning = null;
            };

            _activeVersionWarning.Show(); // 非模态，不阻塞 SSMS
        });

        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<DiffDialogResult> ShowDiffDialog(string leftCode, string rightCode, string title)
    {
        var tcs = new TaskCompletionSource<DiffDialogResult>();

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _activeDiffDialog = new AIDiffDialog(leftCode, rightCode, title);

            _activeDiffDialog.Closed += (_, _) =>
            {
                tcs.TrySetResult(_activeDiffDialog.Result);
                _activeDiffDialog = null;
            };

            _activeDiffDialog.Show(); // 非模态，不阻塞 SSMS
        });

        return tcs.Task;
    }
}