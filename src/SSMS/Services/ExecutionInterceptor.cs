using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services.Connection;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.SSMS.Services;

/// <summary>
/// Intercepts SSMS SQL execution commands (F5, Ctrl+F5, ALT+X) via DTE.CommandEvents.BeforeExecute.
///
/// CRITICAL DESIGN: Async-first with "Cancel-then-ReExecute" pattern
/// ================================================================
/// The BeforeExecute callback is synchronous and must set CancelDefault before returning.
/// However, version checking requires async database queries. Doing .Wait() or .GetResult()
/// on the UI thread causes DEADLOCK (Task needs UI thread to SwitchToMainThread, UI thread
/// is blocked waiting for the Task → mutual wait → timeout).
///
/// Solution: "Cancel-then-ReExecute" pattern
/// 1. BeforeExecute: Always CancelDefault=true (cancel the original execution)
/// 2. Fire-and-forget an async version check via ThreadHelper.JoinableTaskFactory.RunAsync
/// 3. If version check passes → re-invoke the same command via DTE.Commands.Item().Execute()
/// 4. If version check finds outdated objects → show warning dialog, user decides
/// 5. _isChecking flag prevents re-interception when we re-invoke the command
///
/// Previous failed approaches:
/// - .GetAwaiter().GetResult() / .Wait() on UI thread → deadlock (5s timeout)
/// - Task.Run(() => ...) → can't access DTE/VS services from background thread
///
/// Key design decisions based on lessons learned:
/// 1. DTE.CommandEvents is the ONLY reliable way to intercept SSMS Query.Execute in SSMS 22
/// 2. Must find the correct GUID/ID for SSMS's "Query.Execute" command at runtime
/// 3. Must store Events/CommandEvents/DTE as class fields to prevent GC
/// 4. Must NOT block the UI thread with .Wait() or .GetResult()
/// </summary>
public sealed class ExecutionInterceptor : IDisposable
{
    // SSMS command names to intercept
    private static readonly string[] SqlExecuteCommandNames = new[]
    {
        "Query.Execute",                    // F5 - Execute query
        "Query.ExecuteWithResultsToGrid",   // Ctrl+F5 variant
        "Query.ExecuteCurrentStatement",    // ALT+F5 variant
    };

    private readonly AsyncPackage _package;
    private readonly ILoggerService _logger;
    private readonly IConfigurationService _configService;
    private readonly Func<IDatabaseService> _databaseServiceFactory;
    private readonly Func<SsmsDialogService> _dialogServiceFactory;
    private readonly ConnectionCache? _connectionCache;
    private readonly SSMS.Services.Connection.ConnectionServiceFacade? _connectionFacade;

    // ⚠️ CRITICAL: These MUST be stored as class fields to prevent .NET GC from collecting them!
    private DTE? _dte;
    private Events? _dteEvents;
    private CommandEvents? _commandEvents;
    private CommandEvents? _wildcardCommandEvents;

    // 自增令牌，用于区分"用户主动执行"和"版本守卫重执行"
    // 每次版本守卫重执行时递增令牌，BeforeExecute 检查令牌是否匹配
    // 如果不匹配（用户主动执行）→ 拦截检查
    // 如果匹配（守卫重执行）→ 放行
    private int _reEntryToken;
    private int _expectedReEntryToken;

    private bool _disposed;
    private bool _subscribed;

    // Discovered command info
    private string? _discoveredGuid;
    private int _discoveredId;

    public ExecutionInterceptor(
        AsyncPackage package,
        ILoggerService logger,
        IConfigurationService configService,
        ConnectionCache? connectionCache = null,
        Func<IDatabaseService>? databaseServiceFactory = null,
        Func<SsmsDialogService>? dialogServiceFactory = null,
        SSMS.Services.Connection.ConnectionServiceFacade? connectionFacade = null)
    {
        _package = package;
        _logger = logger;
        _configService = configService;
        _connectionCache = connectionCache;
        _connectionFacade = connectionFacade;
        _databaseServiceFactory = databaseServiceFactory ?? (() => new DatabaseService(_logger));
        _dialogServiceFactory = dialogServiceFactory ?? (() => new SsmsDialogService(_package, _configService,
            _connectionFacade ?? throw new InvalidOperationException("ConnectionServiceFacade is required for VersionWarningDialog")));
    }

    public void Subscribe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_subscribed) return;

        try
        {
            _dte = (DTE)((System.IServiceProvider)_package).GetService(typeof(DTE));
            if (_dte == null)
            {
                _logger.Error("[ExecInterceptor] Failed to get DTE service.");
                return;
            }

            _dteEvents = _dte.Events;
            if (_dteEvents == null)
            {
                _logger.Error("[ExecInterceptor] Failed to get DTE.Events.");
                return;
            }

            DiscoverExecuteCommandGuid();

            if (_discoveredGuid != null)
            {
                _commandEvents = _dteEvents.CommandEvents[_discoveredGuid, _discoveredId];
                _commandEvents.BeforeExecute += OnBeforeExecute;
                _logger.Information($"[ExecInterceptor] ✅ Subscribed to CommandEvents for GUID={_discoveredGuid}, ID={_discoveredId}");
            }
            else
            {
                _wildcardCommandEvents = _dteEvents.CommandEvents[null, 0];
                _wildcardCommandEvents.BeforeExecute += OnBeforeExecuteWildcard;
                _logger.Information("[ExecInterceptor] ⚠️ Could not find Query.Execute GUID, using wildcard listener.");
            }

            _subscribed = true;
            _logger.Information("[ExecInterceptor] ✅ F5 interception active via DTE.CommandEvents.BeforeExecute.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ExecInterceptor] Subscribe failed.");
        }
    }

    private void DiscoverExecuteCommandGuid()
    {
        try
        {
            _logger.Information("[ExecInterceptor] Discovering Query.Execute command GUID...");

            foreach (Command cmd in _dte!.Commands)
            {
                try
                {
                    if (SqlExecuteCommandNames.Contains(cmd.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        _discoveredGuid = cmd.Guid;
                        _discoveredId = cmd.ID;
                        _logger.Information($"[ExecInterceptor] ✅ Found command: Name={cmd.Name}, GUID={cmd.Guid}, ID={cmd.ID}");
                        return;
                    }
                }
                catch { }
            }

            _logger.Warning("[ExecInterceptor] Could not find Query.Execute command GUID. Will use wildcard listener.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ExecInterceptor] Command GUID discovery failed.");
        }
    }

    // Map GUID+ID to command name for re-execution
    private string? _lastCommandName;

    /// <summary>
    /// BeforeExecute handler (specific GUID).
    /// Uses "Cancel-then-ReExecute" pattern to avoid UI thread deadlock.
    /// </summary>
    private void OnBeforeExecute(
        string Guid,
        int ID,
        object CustomIn,
        object CustomOut,
        ref bool CancelDefault)
    {
        // 令牌匹配 → 这是守卫重执行的命令，放行
        // 同时递增令牌供下一次用户 F5 使用
        if (Interlocked.CompareExchange(ref _reEntryToken, 0, 0) == _expectedReEntryToken
            && _expectedReEntryToken > 0)
        {
            Interlocked.Increment(ref _reEntryToken); // 为下一次用户 F5 准备新令牌
            return;
        }

        // 否则，递增令牌准备拦截
        Interlocked.Increment(ref _reEntryToken);

        _logger.Information($"[ExecInterceptor] ✅ SQL Execute detected: GUID={Guid}, ID={ID}");

        // Save command name for re-execution
        try
        {
            var cmd = _dte?.Commands.Item(Guid, ID);
            _lastCommandName = cmd?.Name;
        }
        catch { }

        // ALWAYS cancel the default execution first — we'll re-invoke it if check passes
        CancelDefault = true;

        // Launch async version check (fire-and-forget with JTF)
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await CheckAndExecuteAsync();
        });
    }

    /// <summary>
    /// BeforeExecute handler (wildcard fallback).
    /// </summary>
    private void OnBeforeExecuteWildcard(
        string Guid,
        int ID,
        object CustomIn,
        object CustomOut,
        ref bool CancelDefault)
    {
        // 令牌匹配 → 守卫重执行，放行
        // 同时递增令牌供下一次用户 F5 使用
        if (Interlocked.CompareExchange(ref _reEntryToken, 0, 0) == _expectedReEntryToken
            && _expectedReEntryToken > 0)
        {
            Interlocked.Increment(ref _reEntryToken);
            return;
        }

        Interlocked.Increment(ref _reEntryToken);

        bool isSqlExecute = false;

        if (_discoveredGuid != null && Guid.Equals(_discoveredGuid, StringComparison.OrdinalIgnoreCase) && ID == _discoveredId)
        {
            isSqlExecute = true;
        }
        else
        {
            try
            {
                var cmd = _dte?.Commands.Item(Guid, ID);
                if (cmd?.Name != null && SqlExecuteCommandNames.Contains(cmd.Name, StringComparer.OrdinalIgnoreCase))
                {
                    isSqlExecute = true;
                    _discoveredGuid = Guid;
                    _discoveredId = ID;
                    _lastCommandName = cmd.Name;
                }
            }
            catch { }
        }

        if (!isSqlExecute) return;

        _logger.Information($"[ExecInterceptor] ✅ SQL Execute detected (wildcard): GUID={Guid}, ID={ID}");

        CancelDefault = true;

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await CheckAndExecuteAsync();
        });
    }

    /// <summary>
    /// Core async logic: run version check, then either re-execute the command or block.
    /// This runs on the UI thread via JTF, so we can use await properly (no deadlock).
    /// </summary>
    private async Task CheckAndExecuteAsync()
    {
        var progressDialog = new UI.Dialogs.ProcessingDialog(
            "SSMSBoost++ - Version Check",
            "正在检查脚本版本...");
        progressDialog.Show();
        progressDialog.UpdateDetail("Analyzing SQL script...");

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var editorService = new SsmsEditorService(_package);

            if (!editorService.IsSqlDocument())
            {
                ReExecuteCommandSync();
                return;
            }

            var (code, _) = await editorService.GetCodeAsync();
            if (string.IsNullOrWhiteSpace(code))
            {
                ReExecuteCommandSync();
                return;
            }

            // Use AxialStyleConnectionProvider with shared ConnectionCache
            var connectionProvider = new AxialStyleConnectionProvider(_package, _logger, _connectionCache);
            var connectionInfo = await connectionProvider.GetConnectionInfoAsync();

            var connectionString = connectionInfo?.ConnectionString;
            var database = connectionInfo?.DatabaseName;

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.Debug("[ExecInterceptor] No active database connection, allowing execution.");
                ReExecuteCommandSync();
                return;
            }

            _logger.Information($"[ExecInterceptor] ✅ Got connection via AxialStyleProvider: Server={connectionInfo?.ServerInstance}, DB={database}, Source={connectionInfo?.SourceProvider}");

            var config = await _configService.GetVersionGuardConfigurationAsync();
            if (!config.Enabled)
            {
                ReExecuteCommandSync();
                return;
            }

            var objectExtractor = new ObjectExtractor();
            var tokenResult = objectExtractor.DetectTokens(code, config.AskToken, config.SkipToken);
            if (tokenResult.HasSkipToken)
            {
                _logger.Information("[ExecInterceptor] Skip token detected, allowing execution.");
                ReExecuteCommandSync();
                return;
            }

            var docPath = editorService.GetActiveDocumentPath();
            var scriptOpenTime = editorService.GetScriptOpenTime(docPath);

            var databaseService = _databaseServiceFactory();
            var versionChecker = new VersionChecker(objectExtractor, databaseService, _configService, _logger);

            progressDialog.UpdateStatus("正在比对数据库版本...");
            progressDialog.UpdateDetail("Querying database object modify dates...");

            VersionCheckResult? checkResult = null;
            try
            {
                checkResult = await versionChecker.CheckVersionAsync(code, scriptOpenTime, connectionString, database ?? "dbo");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[ExecInterceptor] Version check failed, allowing execution.");
                ReExecuteCommandSync();
                return;
            }

            if (checkResult == null || checkResult.Skipped || checkResult.HasError || !checkResult.HasOutdated)
            {
                _logger.Information("[ExecInterceptor] Version check passed, allowing execution.");
                progressDialog.CloseSafely();
                // ⚠️ 清除 ModifyDate 缓存，确保下一次 F5 获取数据库的最新 modify_date
                databaseService.ClearCache();
                ReExecuteCommandSync();
                // F3：轮询 DB modify_date 变化，设 OpenTime 为精确的 modify_date（防下次 F5 误弹）
                await UpdateOpenTimeAfterExecutionAsync(
                    docPath, databaseService, connectionString!,
                    checkResult?.VersionInfos ?? new List<VersionInfo>());
                return;
            }

            // Version check found outdated objects — close progress BEFORE showing warning dialog
            // (same pattern as VersionGuardCommand: progress bar must not overlap with warning dialog)
            _logger.Information("[ExecInterceptor] ⚠️ Outdated objects found, showing warning dialog.");
            progressDialog.CloseSafely();

            var dialogService = _dialogServiceFactory();
            var userChoice = await dialogService.ShowVersionWarningDialog(
                checkResult, code,
                vi => versionChecker.GetLatestDefinitionAsync(
                    connectionString!, vi.ObjectRef.Database ?? database ?? "dbo", vi.ObjectRef.Schema, vi.ObjectRef.Name),
                vi => versionChecker.GetLatestModifyDateAsync(
                    connectionString!, vi.ObjectRef.Database ?? database ?? "dbo", vi.ObjectRef.Schema, vi.ObjectRef.Name),
                vi => versionChecker.RefreshComparisonAsync(
                    connectionString!, code, vi));

            switch (userChoice)
            {
                case VersionWarningResult.ReloadFromDb:
                    await HandleReloadFromDbAsync(versionChecker, editorService, connectionString, database, checkResult);
                    // After reload, re-execute the command
                    databaseService.ClearCache();
                    ReExecuteCommandSync();
                    // F3：轮询 DB modify_date 变化，设 OpenTime 为精确的 modify_date（防下次 F5 误弹）
                    await UpdateOpenTimeAfterExecutionAsync(
                        docPath, databaseService, connectionString!, checkResult.VersionInfos);
                    break;

                case VersionWarningResult.ContinueExecution:
                    // User chose to continue — re-execute the command
                    databaseService.ClearCache();
                    ReExecuteCommandSync();
                    // F3：轮询 DB modify_date 变化，设 OpenTime 为精确的 modify_date（防下次 F5 误弹）
                    await UpdateOpenTimeAfterExecutionAsync(
                        docPath, databaseService, connectionString!, checkResult.VersionInfos);
                    break;

                case VersionWarningResult.Cancel:
                    _logger.Information("[ExecInterceptor] User cancelled execution.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ExecInterceptor] Interception error, re-executing command as fallback.");
            try { ReExecuteCommandSync(); } catch { }
        }
        finally
        {
            progressDialog?.CloseSafely();
        }
    }

    /// <summary>
/// Re-invoke the original SQL execute command after version check passes.
/// Uses DTE.ExecuteCommand(commandName) to trigger the same command.
///
/// CRITICAL: Uses token-based re-entry detection to prevent infinite loop.
/// Before executing, sets _expectedReEntryToken to current _reEntryToken value.
/// BeforeExecute checks if _reEntryToken == _expectedReEntryToken → if so, lets it through.
/// This works because DTE.ExecuteCommand is asynchronous (returns immediately, executes later),
/// so _reEntryToken is already incremented to match _expectedReEntryToken when the command
/// actually triggers BeforeExecute.
/// </summary>
private void ReExecuteCommandSync()
{
    ThreadHelper.ThrowIfNotOnUIThread();
    try
    {
        // Set expected token BEFORE executing the command
        // _reEntryToken was already incremented in BeforeExecute
        _expectedReEntryToken = Interlocked.CompareExchange(ref _reEntryToken, 0, 0);

        var commandName = _lastCommandName ?? "Query.Execute";
        _logger.Information($"[ExecInterceptor] ✅ Re-executing command: {commandName} (token={_expectedReEntryToken})");
        _dte?.ExecuteCommand(commandName);
    }
    catch (Exception ex)
    {
        _logger.Error(ex, "[ExecInterceptor] Failed to re-execute command.");
    }
}

/// <summary>
/// F3：脚本执行后轮询 DB modify_date 变化，将 OpenTime 设为精确的 modify_date。
/// 下次 F5 时 modify_date == OpenTime → IsOutdated(>) 为 false → 不误弹窗。
/// 只有其他人再次修改 DB 对象后 modify_date > OpenTime 才会触发弹窗。
///
/// 原理：DTE.ExecuteCommand 是异步的（立即返回，稍后执行），
/// Task.Delay 期间 UI 线程空闲，会处理排队的 DTE 命令 → SQL 执行完成 → modify_date 变化。
/// 轮询到变化后设 OpenTime = modify_date，完全相等 → 下次 F5 不会误判过时。
/// </summary>
private async Task UpdateOpenTimeAfterExecutionAsync(
    string? docPath,
    IDatabaseService databaseService,
    string connectionString,
    List<VersionInfo> versionInfos)
{
    if (string.IsNullOrEmpty(docPath) || string.IsNullOrEmpty(connectionString) || versionInfos.Count == 0)
    {
        SsmsEditorService.UpdateScriptOpenTime(docPath);
        return;
    }

    // 获取执行前的 modify_date 最大值，用于判断执行何时完成
    var preExecMax = versionInfos
        .Where(v => v.DbModifyDate.HasValue)
        .Select(v => v.DbModifyDate!.Value)
        .DefaultIfEmpty(DateTime.MinValue)
        .Max();

    var objectRefs = versionInfos.Select(v => v.ObjectRef).ToList();

    // 轮询 modify_date 变化（Task.Delay 让 UI 线程处理排队的 DTE 命令 → SQL 执行完成）
    for (int i = 0; i < 10; i++) // 10 × 200ms = 最大 2 秒
    {
        await Task.Delay(200).ConfigureAwait(false);

        databaseService.ClearCache();
        DateTime currentMax = DateTime.MinValue;
        foreach (var objRef in objectRefs)
        {
            var md = await databaseService.GetObjectModifyDateAsync(connectionString, objRef)
                .ConfigureAwait(false);
            if (md.HasValue && md.Value > currentMax)
                currentMax = md.Value;
        }

        if (currentMax > preExecMax)
        {
            // modify_date 已变化 → SQL 执行完成 → 将 OpenTime 设为精确的 modify_date
            // 下次 F5：modify_date == OpenTime → > 为 false → 不弹窗
            SsmsEditorService.SetScriptOpenTime(docPath, currentMax);
            _logger.Information($"[ExecInterceptor] ✅ OpenTime = exact modify_date {currentMax:HH:mm:ss.fff}");
            return;
        }
    }

    // 超时：modify_date 未变化（脚本不修改 DB 对象，或执行极慢）
    SsmsEditorService.UpdateScriptOpenTime(docPath);
    _logger.Warning("[ExecInterceptor] ⚠️ Post-execution modify_date unchanged, fallback to DateTime.Now");
}

    private async Task HandleReloadFromDbAsync(VersionChecker vc, SsmsEditorService es, string? cs, string? db, VersionCheckResult cr)
    {
        try
        {
            var obj = cr.OutdatedObjects.FirstOrDefault();
            if (obj == null) return;
            var def = await vc.GetLatestDefinitionAsync(cs!, obj.ObjectRef.Database ?? db ?? "dbo", obj.ObjectRef.Schema, obj.ObjectRef.Name);
            if (def != null)
            {
                await es.ReplaceAllAsync(def);
                _logger.Information($"[ExecInterceptor] Reloaded {obj.ObjectRef.FullName}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ExecInterceptor] Reload failed.");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                if (_commandEvents != null)
                    _commandEvents.BeforeExecute -= OnBeforeExecute;
                if (_wildcardCommandEvents != null)
                    _wildcardCommandEvents.BeforeExecute -= OnBeforeExecuteWildcard;
            }
            catch { }
            _disposed = true;
        }
    }
}
