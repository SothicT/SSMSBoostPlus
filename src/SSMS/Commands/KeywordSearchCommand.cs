using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Data;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services;
using SSMSBoostPlus.SSMS.Services.Connection;
using SSMSBoostPlus.SSMS.UI.Dialogs;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 关键词查询命令。
/// 在 SQL 编辑器中选择关键词后，右键点击"关键词查询"，
/// 后台搜索数据库对象**脚本内容**中包含该关键词的对象。
/// 结果直接显示在 SSMS 结果网格中（不在编辑器中留下 SQL 语句）。
///
/// 实现方式：临时插入搜索 SQL → 选中执行 → 立即删除插入的 SQL → 结果显示在网格中。
///
/// VSCT: cmdidKeywordSearch = 0x0205
/// </summary>
public sealed class KeywordSearchCommand : BaseCommand<KeywordSearchCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private ConnectionServiceFacade _connectionService = null!;

    private KeywordSearchCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0205)
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        ConnectionServiceFacade? connectionService = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new KeywordSearchCommand(package, commandService!)
        {
            _logger = logger,
            _connectionService = connectionService ?? CreateDefaultConnectionService(package, logger)
        };

        // F11 keyboard hook is registered later via RegisterF11Interceptor()
        // after DTE/Shell is ready (called from SSMSBoostPlusPackage).
    }

    #region Win32 F11 Keyboard Hook (WH_KEYBOARD_LL)

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F11 = 0x7A;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    private static IntPtr _f11HookID = IntPtr.Zero;
    private static LowLevelKeyboardProc? _f11HookProc;

    /// <summary>
    /// Register F11 keyboard hook via Win32 WH_KEYBOARD_LL.
    /// F11 triggers keyword search when a SQL document is active.
    /// </summary>
    public static void RegisterF11Interceptor()
    {
        if (Instance == null)
        {
            // Instance not ready yet, will be called again later
            return;
        }
        if (_f11HookID != IntPtr.Zero) return; // Already registered

        try
        {
            _f11HookProc = HookCallback;
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _f11HookID = SetWindowsHookEx(
                WH_KEYBOARD_LL, _f11HookProc,
                GetModuleHandle(curModule.ModuleName!), 0);

            if (_f11HookID != IntPtr.Zero)
            {
                Instance._logger.Information("[KeywordSearch] ✅ F11 keyboard hook registered via Win32 WH_KEYBOARD_LL");
            }
            else
            {
                Instance._logger.Warning($"[KeywordSearch] ❌ Failed to register WH_KEYBOARD_LL hook (Win32 Error: {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            Instance._logger.Warning($"[KeywordSearch] F11 hook registration error: {ex.Message}");
        }
    }

    /// <summary>
    /// Low-level keyboard hook callback.
    /// Runs on the UI thread (the thread that installed the hook, via message loop).
    /// Must return quickly to avoid OS unhooking us (LowLevelHooksTimeout, default ~300ms).
    /// </summary>
    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_F11)
            {
                // Don't intercept if modifier keys are pressed (Shift+F11, Ctrl+F11, Alt+F11)
                if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                {
                    return CallNextHookEx(_f11HookID, nCode, wParam, lParam);
                }

                if (Instance == null) return CallNextHookEx(_f11HookID, nCode, wParam, lParam);

                try
                {
                    // Quick check: is a SQL document active?
                    var editorService = new SsmsEditorService(Instance.Package, Instance._logger);
                    if (editorService.IsSqlDocument())
                    {
                        Instance._logger.Information("[KeywordSearch] F11 intercepted via WH_KEYBOARD_LL, triggering KeywordSearch...");

                        // Trigger KeywordSearch asynchronously (don't block the hook callback)
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            Instance.Execute(Instance, EventArgs.Empty);
                        });

                        // Suppress the key — don't pass to SSMS
                        return (IntPtr)1;
                    }
                }
                catch (Exception ex)
                {
                    Instance._logger.Warning($"[KeywordSearch] F11 hook handler error: {ex.Message}");
                }
            }
        }

        return CallNextHookEx(_f11HookID, nCode, wParam, lParam);
    }

    #endregion

    private static ConnectionServiceFacade CreateDefaultConnectionService(AsyncPackage package, ILoggerService logger)
    {
        var providers = new List<IConnectionInfoProvider>
        {
            new AxialStyleConnectionProvider(package, logger),
            new ScriptFactoryConnectionProvider(package, logger),
            new ActiveWindowConnectionProvider(package, logger),
            new OfficialConnectionProvider(package, logger),
            new ReflectionConnectionProvider(package, logger)
        };
        return new ConnectionServiceFacade(providers, logger);
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

        var editorService = new SsmsEditorService(Package, _logger);

        // Step 1: Verify SQL document
        if (!editorService.IsSqlDocument())
        {
            MessageBox.Show(
                "关键词查询功能需要一个活动的 SQL 文档。\n请打开 .sql 文件后重试。",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 2: Get selected text (the keyword)
        var (code, isSelection) = await editorService.GetCodeAsync();
        if (!isSelection || string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show(
                "请在 SQL 编辑器中选择一个关键词后重试。",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 3: Trim and sanitize the keyword
        var keyword = code.Trim().Trim('[', ']', '"', '\'', '(', ')', ';', ' ', '\t', '\r', '\n');
        if (string.IsNullOrWhiteSpace(keyword))
        {
            MessageBox.Show(
                "选择的关键词无效，请重新选择。",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 4: Get connection info
        var connectionInfo = await _connectionService.GetConnectionInfoAsync();
        if (connectionInfo == null || string.IsNullOrEmpty(connectionInfo.ConnectionString))
        {
            MessageBox.Show(
                "未检测到活动的数据库连接。\n\n请确保已连接到数据库服务器。",
                "SSMSBoostPlus - 无连接",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var databaseName = connectionInfo.DatabaseName ?? "当前数据库";

        // Step 5: Show progress popup
        var progressPopup = new SearchProgressPopup();
        try
        {
            PositionPopupAtBottomRight(progressPopup);
            progressPopup.UpdateStatus($"正在搜索 '{keyword}'...");

            // Step 6: Build the search SQL
            var searchSql = BuildSearchSql(keyword);

            // Step 7: Execute in current editor (temporary insert → execute → cleanup)
            await ExecuteSearchInCurrentEditorAsync(searchSql, keyword, databaseName);

            _logger.Information($"关键词查询完成: keyword='{keyword}', database='{databaseName}'");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"关键词查询失败: '{keyword}'");
            MessageBox.Show(
                $"查询执行失败:\n{ex.Message}",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            await Task.Delay(300);
            progressPopup.CloseSafely();
        }
    }

    /// <summary>
    /// 构建搜索 SQL 语句。
    /// 搜索范围：
    /// 1. SP/函数/视图/触发器的定义（sys.sql_modules.definition）
    /// 2. 表的列名（sys.columns.name）
    /// </summary>
    private static string BuildSearchSql(string keyword)
    {
        // 转义 SQL LIKE 中的特殊字符
        var escapedKeyword = keyword.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        var keywordPattern = $"%{escapedKeyword}%";

        var sb = new StringBuilder();
        sb.AppendLine("-- =============================================");
        sb.AppendLine($"-- 关键词查询: '{keyword}'");
        sb.AppendLine("-- 搜索范围: 脚本内容、列名");
        sb.AppendLine("-- =============================================");
        sb.AppendLine();
        sb.AppendLine("-- 搜索脚本内容包含关键词的对象（SP/函数/视图/触发器）");
        sb.AppendLine("SELECT");
        sb.AppendLine("    SCHEMA_NAME(o.schema_id) AS [架构],");
        sb.AppendLine("    o.name AS [对象名称],");
        sb.AppendLine("    o.type_desc AS [对象类型],");
        sb.AppendLine("    '脚本内容' AS [匹配来源],");
        sb.AppendLine("    o.create_date AS [创建时间],");
        sb.AppendLine("    o.modify_date AS [修改时间]");
        sb.AppendLine("FROM sys.objects o");
        sb.AppendLine("INNER JOIN sys.sql_modules m ON o.object_id = m.object_id");
        sb.AppendLine("WHERE o.type IN ('P', 'FN', 'IF', 'TF', 'TR', 'V')");
        sb.AppendLine($"  AND m.definition LIKE '{keywordPattern.Replace("'", "''")}'");
        sb.AppendLine();
        sb.AppendLine("UNION ALL");
        sb.AppendLine();
        sb.AppendLine("-- 搜索表（列名包含关键词）");
        sb.AppendLine("SELECT");
        sb.AppendLine("    SCHEMA_NAME(t.schema_id) AS [架构],");
        sb.AppendLine("    t.name AS [对象名称],");
        sb.AppendLine("    'TABLE' AS [对象类型],");
        sb.AppendLine("    '列名' AS [匹配来源],");
        sb.AppendLine("    t.create_date AS [创建时间],");
        sb.AppendLine("    t.modify_date AS [修改时间]");
        sb.AppendLine("FROM sys.tables t");
        sb.AppendLine("WHERE EXISTS (");
        sb.AppendLine("    SELECT 1 FROM sys.columns c");
        sb.AppendLine("    WHERE c.object_id = t.object_id");
        sb.AppendLine($"      AND c.name LIKE '{keywordPattern.Replace("'", "''")}'");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("ORDER BY [对象类型], [对象名称];");

        return sb.ToString();
    }

    /// <summary>
    /// 在当前编辑器中临时插入搜索 SQL，选中并执行，然后立即删除插入的 SQL。
    /// SSMS 将选中的 SQL 发送到服务器后，删除编辑器中的文本不影响查询执行。
    /// 结果直接显示在 SSMS 结果网格中，编辑器保持干净。
    /// </summary>
    private async Task ExecuteSearchInCurrentEditorAsync(string searchSql, string keyword, string databaseName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.ActiveDocument?.Object("TextDocument") is not TextDocument textDoc)
        {
            _logger.Warning("无法获取当前编辑器的 TextDocument");
            return;
        }

        // 记录插入前的文档末尾位置
        var insertStartPoint = textDoc.EndPoint.CreateEditPoint();
        var startLine = insertStartPoint.Line;
        var startLineOffset = insertStartPoint.LineCharOffset;

        // 在文档末尾插入搜索 SQL
        insertStartPoint.Insert("\r\n" + searchSql);

        // 选中新插入的内容
        var newEndPoint = textDoc.EndPoint.CreateEditPoint();
        textDoc.Selection.MoveToLineAndOffset(startLine, startLineOffset);
        textDoc.Selection.MoveToLineAndOffset(newEndPoint.Line, newEndPoint.LineCharOffset, true);

        // 执行查询 — SSMS 将选中的 SQL 发送到服务器
        try
        {
            dte.ExecuteCommand("Query.Execute");
            _logger.Information($"✅ 关键词查询已执行: '{keyword}' (数据库: {databaseName})");
        }
        catch (Exception ex)
        {
            _logger.Warning($"自动执行查询失败（用户可手动按 F5 执行）: {ex.Message}");
        }

        // 等待 SSMS 将 SQL 发送到服务器
        await Task.Delay(500);

        // 立即删除插入的 SQL，保持编辑器干净
        try
        {
            var deleteStart = textDoc.CreateEditPoint();
            deleteStart.MoveToLineAndOffset(startLine, startLineOffset);
            var deleteEnd = textDoc.EndPoint.CreateEditPoint();
            deleteStart.Delete(deleteEnd);
            _logger.Information("已清理临时搜索 SQL，编辑器保持干净");
        }
        catch (Exception ex)
        {
            _logger.Warning($"清理临时 SQL 失败: {ex.Message}");
        }
    }

    private static void PositionPopupAtBottomRight(SearchProgressPopup popup)
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow != null)
            {
                popup.ShowAtBottomRight(mainWindow);
                return;
            }
        }
        catch { }

        popup.Left = SystemParameters.PrimaryScreenWidth - popup.Width - 20;
        popup.Top = SystemParameters.PrimaryScreenHeight - popup.Height - 80;
        popup.Show();
    }
}
