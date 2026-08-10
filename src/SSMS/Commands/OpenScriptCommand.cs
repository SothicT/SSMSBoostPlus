using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Data;
using System.IO;
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

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 打开脚本命令。
/// 在 SQL 编辑器中选择对象名称（表/存储过程/函数/视图等），
/// 右键点击"打开脚本"，从数据库获取完整定义脚本并在新编辑器中打开。
///
/// 性能要求：1-2 秒内完成（含 5K 行脚本）。
/// 通过 sys.sql_modules.definition 直接获取定义，毫秒级返回。
///
/// VSCT: cmdidOpenScript = 0x0206
/// </summary>
public sealed class OpenScriptCommand : BaseCommand<OpenScriptCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    private ConnectionServiceFacade _connectionService = null!;

    private OpenScriptCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0206)
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger,
        ConnectionServiceFacade? connectionService = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new OpenScriptCommand(package, commandService!)
        {
            _logger = logger,
            _connectionService = connectionService ?? CreateDefaultConnectionService(package, logger)
        };

        // F11 keyboard hook is registered later via RegisterF11Interceptor()
        // after DTE/Shell is ready (called from SSMSBoostPlusPackage).
    }

    #region Win32 F12 Keyboard Hook (WH_KEYBOARD_LL)

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
    private const int VK_F12 = 0x7B;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    private static IntPtr _f12HookID = IntPtr.Zero;
    private static LowLevelKeyboardProc? _f12HookProc;

    /// <summary>
    /// Register F12 keyboard hook via Win32 WH_KEYBOARD_LL.
    ///
    /// This is the ONLY reliable way to intercept function keys in SSMS 22:
    /// - VSCT &lt;KeyBindings&gt;: SSMS Shell doesn't load third-party keybinding tables
    /// - DTE Command.Bindings: throws E_FAIL in SSMS
    /// - WPF PreviewKeyDown: SSMS is Win32+WPF interop, SQL editor keystrokes don't tunnel to MainWindow
    /// - DTE CommandEvents: only fires if F12 is bound to a DTE command (it isn't in SSMS by default)
    ///
    /// WH_KEYBOARD_LL installs an OS-level keyboard hook that captures ALL keystrokes
    /// before SSMS processes them. Must be installed on the UI thread.
    /// </summary>
    public static void RegisterF12Interceptor()
    {
        if (Instance == null)
        {
            // Instance not ready yet, will be called again later
            return;
        }
        if (_f12HookID != IntPtr.Zero) return; // Already registered

        try
        {
            _f12HookProc = HookCallback;
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _f12HookID = SetWindowsHookEx(
                WH_KEYBOARD_LL, _f12HookProc,
                GetModuleHandle(curModule.ModuleName!), 0);

            if (_f12HookID != IntPtr.Zero)
            {
                Instance._logger.Information("[OpenScript] ✅ F12 keyboard hook registered via Win32 WH_KEYBOARD_LL");
            }
            else
            {
                Instance._logger.Warning($"[OpenScript] ❌ Failed to register WH_KEYBOARD_LL hook (Win32 Error: {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            Instance._logger.Warning($"[OpenScript] F12 hook registration error: {ex.Message}");
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
            if (vkCode == VK_F12)
            {
                // Don't intercept if modifier keys are pressed (Shift+F12, Ctrl+F12, Alt+F12)
                if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                {
                    return CallNextHookEx(_f12HookID, nCode, wParam, lParam);
                }

                if (Instance == null) return CallNextHookEx(_f12HookID, nCode, wParam, lParam);

                try
                {
                    // Quick check: is a SQL document active?
                    var editorService = new SsmsEditorService(Instance.Package, Instance._logger);
                    if (editorService.IsSqlDocument())
                    {
                        Instance._logger.Information("[OpenScript] F12 intercepted via WH_KEYBOARD_LL, triggering OpenScript...");

                        // Trigger OpenScript asynchronously (don't block the hook callback)
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
                    Instance._logger.Warning($"[OpenScript] F12 hook handler error: {ex.Message}");
                }
            }
        }

        return CallNextHookEx(_f12HookID, nCode, wParam, lParam);
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
                "打开脚本功能需要一个活动的 SQL 文档。\n请打开 .sql 文件后重试。",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 2: Get selected text (object name)
        var (code, isSelection) = await editorService.GetCodeAsync();
        if (!isSelection || string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show(
                "请在 SQL 编辑器中选择一个对象名称（如表名、存储过程名等）后重试。",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 3: Parse object name
        var objectName = code.Trim().Trim('[', ']', '"', '\'', '(', ')', ';', ' ', '\t', '\r', '\n');
        if (string.IsNullOrWhiteSpace(objectName))
        {
            MessageBox.Show(
                "选择的对象名称无效，请重新选择。",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // ★ Bug 修复：如果选中文本不包含 DB 前缀，从当前行文本中提取完整 3 段名
        if (objectName.IndexOf('.') < 0)
        {
            try
            {
                var lineText = await editorService.GetCurrentLineTextAsync();
                if (!string.IsNullOrEmpty(lineText))
                {
                    // 查找选中对象名在行中的位置，向前查找 [DB].Schema. 或 DB.Schema. 模式
                    var idx = lineText.IndexOf(objectName, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        // 向前查找 3 段名模式：[DB].Schema.Object 或 DB.Schema.Object
                        var beforeText = lineText.Substring(0, idx);
                        // 匹配 [DBName].Schema. 或 DBName.Schema. 模式
                        var match = System.Text.RegularExpressions.Regex.Match(
                            beforeText + objectName,
                            @"(?:\[?(\w+)\]?\.)?(?:\[?(\w+)\]?\.)?\[?" + System.Text.RegularExpressions.Regex.Escape(objectName) + @"\]?",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success && match.Groups[1].Success)
                        {
                            var extractedDb = match.Groups[1].Value;
                            var extractedSchema = match.Groups[2].Success ? match.Groups[2].Value : "";
                            if (!string.IsNullOrEmpty(extractedSchema))
                                objectName = $"[{extractedDb}].[{extractedSchema}].{objectName}";
                            else
                                objectName = $"[{extractedDb}].{objectName}";
                            _logger.Information($"[OpenScript] 从行文本提取到3段名: '{objectName}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[OpenScript] 提取行文本失败: {ex.Message}");
            }
        }

        _logger.Information($"[OpenScript] 选中的对象名: '{objectName}'");

        // Step 4: Get connection info
        var connectionInfo = await _connectionService.GetConnectionInfoAsync();
        if (connectionInfo == null || string.IsNullOrEmpty(connectionInfo.ConnectionString))
        {
            MessageBox.Show(
                "未检测到活动的数据库连接。\n请确保已连接到数据库服务器。",
                "SSMSBoostPlus - 无连接",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var connectionString = connectionInfo.ConnectionString;
        _logger.Information($"[OpenScript] 连接: Server={connectionInfo.ServerInstance}, Database={connectionInfo.DatabaseName}");

        // Step 5: Show status
        SetStatusText($"SSMSBoostPlus: 正在获取 '{objectName}' 的脚本...");

        try
        {
            // Step 6: Query and get the object script
            var script = await GetObjectScriptAsync(connectionString, objectName);

            if (string.IsNullOrEmpty(script))
            {
                _logger.Warning($"[OpenScript] 未找到对象脚本: '{objectName}'");
                MessageBox.Show(
                    $"在数据库中未找到对象 '{objectName}'，或该对象没有可生成的脚本。\n\n" +
                    "请检查：\n" +
                    "1. 对象名称是否正确\n" +
                    "2. 当前连接的数据库是否正确\n" +
                    "3. 对象类型是否支持（TABLE, VIEW, PROCEDURE, FUNCTION, TRIGGER）",
                    "SSMSBoostPlus",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Step 7: 自动注释 ALTER/CREATE 行（F10）
            var commentedScript = AutoCommentAlterCreate(script);
            if (commentedScript != script)
            {
                _logger.Information($"[OpenScript] 已自动注释 ALTER/CREATE 行");
            }

            // Step 8: Open new query editor with the script
            OpenNewQueryWindow(commentedScript, objectName);

            _logger.Information($"✅ 打开脚本成功: '{objectName}' ({commentedScript.Length} 字符)");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"打开脚本失败: '{objectName}'");
            MessageBox.Show(
                $"获取对象脚本时出错：\n{ex.Message}",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetStatusText(null);
        }
    }

    /// <summary>
    /// 从数据库获取对象的完整定义脚本。
    /// 优先使用 sys.sql_modules.definition（最快，毫秒级返回），
    /// 对于表对象则动态生成 CREATE TABLE 脚本。
    /// </summary>
    private async Task<string?> GetObjectScriptAsync(string connectionString, string objectName)
    {
        // ★ Bug 修复：支持3段名解析 [MESDATA].dbo.P_ReceiveMesTableInfo
        var parts = objectName.Split('.');
        string schema, name, targetDb;

        if (parts.Length >= 3)
        {
            // 3段名: [DBName].Schema.Object 或 DBName.Schema.Object
            targetDb = parts[0].Trim('[', ']');
            schema = parts[1].Trim('[', ']');
            name = parts[2].Trim('[', ']');
        }
        else if (parts.Length >= 2)
        {
            // 2段名: Schema.Object
            targetDb = "";
            schema = parts[0].Trim('[', ']');
            name = parts[1].Trim('[', ']');
        }
        else
        {
            // 1段名: Object
            targetDb = "";
            schema = "";
            name = parts[0].Trim('[', ']');
        }

        _logger.Information($"[OpenScript] 解析结果: db='{targetDb}', schema='{schema}', name='{name}'");

        try
        {
            // ★ Bug 修复：如果指定了目标数据库，修改连接字符串的 Initial Catalog
            string effectiveConnStr = connectionString;
            if (!string.IsNullOrEmpty(targetDb))
            {
                var connBuilder = new SqlConnectionStringBuilder(connectionString)
                {
                    InitialCatalog = targetDb
                };
                effectiveConnStr = connBuilder.ConnectionString;
                _logger.Information($"[OpenScript] 跨库查询: 切换到数据库 '{targetDb}'");
            }

            using var conn = new SqlConnection(effectiveConnStr);
            await conn.OpenAsync();

            // 获取数据库信息（用于日志）
            var dbName = conn.Database;
            _logger.Information($"[OpenScript] 已连接到数据库: {dbName}");

            // 如果没有指定 schema，先查找对象在哪个 schema 下
            if (string.IsNullOrEmpty(schema))
            {
                var findSchemaSql = @"
SELECT TOP 1 SCHEMA_NAME(schema_id) AS SchemaName, type, type_desc
FROM sys.objects 
WHERE name = @name
ORDER BY CASE WHEN type = 'U' THEN 0 ELSE 1 END";

                using var findCmd = new SqlCommand(findSchemaSql, conn);
                findCmd.Parameters.AddWithValue("@name", name);
                findCmd.CommandTimeout = 5;

                using var findReader = await findCmd.ExecuteReaderAsync();
                if (findReader.HasRows)
                {
                    await findReader.ReadAsync();
                    schema = findReader["SchemaName"]?.ToString() ?? "dbo";
                    var objType = findReader["type"]?.ToString();
                    var objTypeDesc = findReader["type_desc"]?.ToString();
                    _logger.Information($"[OpenScript] 找到对象: schema='{schema}', type='{objType}', type_desc='{objTypeDesc}'");
                    findReader.Close();
                }
                else
                {
                    // ★ Bug 修复：先关闭 findReader，再执行模糊搜索，避免 DataReader 冲突
                    findReader.Close();

                    _logger.Warning($"[OpenScript] 在数据库 '{dbName}' 中未找到名为 '{name}' 的对象");
                    
                    // 尝试模糊搜索，看是否有类似的对象
                    var fuzzySql = @"
SELECT TOP 10 SCHEMA_NAME(schema_id) AS SchemaName, name, type, type_desc
FROM sys.objects 
WHERE name LIKE @namePattern
ORDER BY name";

                    using var fuzzyCmd = new SqlCommand(fuzzySql, conn);
                    fuzzyCmd.Parameters.AddWithValue("@namePattern", $"%{name}%");
                    fuzzyCmd.CommandTimeout = 5;

                    using var fuzzyReader = await fuzzyCmd.ExecuteReaderAsync();
                    if (fuzzyReader.HasRows)
                    {
                        var suggestions = new List<string>();
                        while (await fuzzyReader.ReadAsync())
                        {
                            suggestions.Add($"  {fuzzyReader["SchemaName"]}.{fuzzyReader["name"]} ({fuzzyReader["type_desc"]})");
                        }
                        _logger.Information($"[OpenScript] 模糊搜索找到类似对象:\n{string.Join("\n", suggestions)}");
                    }
                    
                    return null;
                }
                // findReader 已在 if/else 分支中关闭
            }

            var fullName = $"[{schema}].[{name}]";
            _logger.Information($"[OpenScript] 完整对象名: {fullName}");

            // 1️⃣ 尝试从 sys.sql_modules 获取定义（PROCEDURE, FUNCTION, VIEW, TRIGGER）
            _logger.Debug($"[OpenScript] 尝试 sys.sql_modules: {fullName}");
            var definition = await GetSqlModuleDefinitionAsync(conn, fullName);
            if (!string.IsNullOrEmpty(definition))
            {
                // 将 CREATE 转为 ALTER，方便用户直接修改并执行
                var alterScript = ConvertCreateToAlter(definition);
                _logger.Information($"[OpenScript] ✅ 从 sys.sql_modules 获取到定义: '{fullName}' ({definition.Length} 字符, ALTER模式)");
                return alterScript;
            }

            // 2️⃣ 尝试动态生成表结构脚本
            // 注意：表没有有意义的 ALTER TABLE 全量脚本，CREATE TABLE 是展示表结构的标准方式
            _logger.Information($"[OpenScript] sys.sql_modules 无结果，尝试生成表结构脚本: {fullName}");
            var tableScript = await GenerateCreateTableScriptAsync(conn, schema, name);
            if (!string.IsNullOrEmpty(tableScript))
            {
                _logger.Information($"[OpenScript] ✅ 动态生成表结构脚本成功: '{fullName}' ({tableScript.Length} 字符)");
                return tableScript;
            }

            _logger.Warning($"[OpenScript] 对象 '{fullName}' 不是表类型或生成脚本失败");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"[OpenScript] 获取对象脚本时出错: '{objectName}' - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 sys.sql_modules.definition 获取代码对象定义。
    /// </summary>
    private static async Task<string?> GetSqlModuleDefinitionAsync(SqlConnection conn, string fullName)
    {
        var sql = @"
SELECT definition 
FROM sys.sql_modules 
WHERE object_id = OBJECT_ID(@fullName)";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fullName", fullName);
        cmd.CommandTimeout = 5;

        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return null;

        return (string)result;
    }

    /// <summary>
    /// 将定义中的首个 CREATE 关键词替换为 ALTER，
    /// 使脚本可以直接修改并执行（ALTER PROCEDURE / ALTER VIEW / ALTER FUNCTION 等）。
    /// </summary>
    private static string ConvertCreateToAlter(string definition)
    {
        if (string.IsNullOrEmpty(definition))
            return definition;

        // 查找第一个 CREATE 关键词（忽略大小写、前后空白）
        // 匹配 CREATE 后面跟着 PROCEDURE / VIEW / FUNCTION / TRIGGER 等
        var pattern = System.Text.RegularExpressions.Regex.Escape("CREATE");
        var regex = new System.Text.RegularExpressions.Regex(
            @"(?i)^\s*CREATE\s+",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var match = regex.Match(definition);
        if (match.Success)
        {
            // 替换第一个匹配项
            var replaced = regex.Replace(definition, match.Value.ToUpperInvariant().Replace("CREATE", "ALTER"), 1);
            return replaced;
        }

        // 如果开头不是 CREATE（可能是注释），尝试找第一个 CREATE 关键词
        var createIndex = definition.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase);
        if (createIndex >= 0)
        {
            var sb = new StringBuilder(definition);
            // 找到 CREATE 的 C 位置，替换为 ALTER
            sb.Remove(createIndex, 6); // "CREATE" = 6 chars
            sb.Insert(createIndex, "ALTER");
            return sb.ToString();
        }

        return definition;
    }

    /// <summary>
    /// 动态生成 CREATE TABLE 脚本。
    /// </summary>
    private async Task<string?> GenerateCreateTableScriptAsync(SqlConnection conn, string schema, string name)
    {
        var fullName = $"[{schema}].[{name}]";

        // 先检查对象是否存在且是表（U）
        var checkSql = @"
SELECT type, type_desc 
FROM sys.objects 
WHERE name = @name 
  AND schema_id = SCHEMA_ID(@schema)";

        using var checkCmd = new SqlCommand(checkSql, conn);
        checkCmd.Parameters.AddWithValue("@name", name);
        checkCmd.Parameters.AddWithValue("@schema", schema);
        checkCmd.CommandTimeout = 5;

        using var checkReader = await checkCmd.ExecuteReaderAsync();
        if (!checkReader.HasRows)
        {
            _logger.Warning($"[OpenScript] sys.objects 中未找到: schema='{schema}', name='{name}'");
            return null;
        }
        await checkReader.ReadAsync();

        var type = (checkReader["type"] as string)?.Trim();
        var typeDesc = checkReader["type_desc"] as string;
        checkReader.Close();

        _logger.Information($"[OpenScript] 对象类型: type='{type}', type_desc='{typeDesc}'");

        // sys.objects.type 是 char(2)，返回值可能带空格（如 'U '），已 Trim
        if (type != "U")
        {
            _logger.Warning($"[OpenScript] 对象类型不是表(U)，而是: {type} ({typeDesc})");
            return null;
        }

        // 获取列信息
        var columnsSql = @"
SELECT 
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    c.is_computed,
    cc.definition AS ComputedDefinition,
    c.default_object_id,
    dc.definition AS DefaultDefinition,
    c.collation_name
FROM sys.columns c
LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
WHERE c.object_id = OBJECT_ID(@fullName)
ORDER BY c.column_id";

        var columns = new List<ColumnInfo>();

        using (var colCmd = new SqlCommand(columnsSql, conn))
        {
            colCmd.Parameters.AddWithValue("@fullName", fullName);
            colCmd.CommandTimeout = 5;

            using var colReader = await colCmd.ExecuteReaderAsync();
            _logger.Information($"[OpenScript] 查询列信息: {fullName}");

            while (await colReader.ReadAsync())
            {
                var col = new ColumnInfo
                {
                    Name = colReader["ColumnName"]?.ToString() ?? "",
                    DataType = colReader["DataType"]?.ToString() ?? "",
                    MaxLength = colReader["max_length"] != DBNull.Value ? Convert.ToInt16(colReader["max_length"]) : (short)-1,
                    Precision = colReader["precision"] != DBNull.Value ? Convert.ToByte(colReader["precision"]) : (byte)0,
                    Scale = colReader["scale"] != DBNull.Value ? Convert.ToByte(colReader["scale"]) : (byte)0,
                    IsNullable = Convert.ToBoolean(colReader["is_nullable"]),
                    IsIdentity = Convert.ToBoolean(colReader["is_identity"]),
                    IsComputed = Convert.ToBoolean(colReader["is_computed"]),
                    ComputedDefinition = colReader["ComputedDefinition"]?.ToString(),
                    HasDefault = Convert.ToInt32(colReader["default_object_id"]) != 0,
                    DefaultDefinition = colReader["DefaultDefinition"]?.ToString(),
                    CollationName = colReader["collation_name"]?.ToString()
                };
                columns.Add(col);
            }
        }

        _logger.Information($"[OpenScript] 获取到 {columns.Count} 列");

        if (columns.Count == 0)
        {
            _logger.Warning($"[OpenScript] 未获取到列信息，OBJECT_ID 可能返回 NULL: {fullName}");
            return null;
        }

        // 获取索引/约束信息（使用 FOR XML PATH 兼容所有 SQL Server 版本）
        var indexesSql = @"
SELECT 
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.is_primary_key AS IsPrimaryKey,
    i.is_unique_constraint AS IsUniqueConstraint,
    STUFF((
        SELECT ', ' + c2.name + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
        FROM sys.index_columns ic2
        JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id
          AND ic2.is_included_column = 0
        ORDER BY ic2.key_ordinal
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS KeyColumns,
    STUFF((
        SELECT ', ' + c2.name
        FROM sys.index_columns ic2
        JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id
          AND ic2.is_included_column = 1
        ORDER BY ic2.index_column_id
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS IncludedColumns,
    i.has_filter,
    i.filter_definition
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(@fullName)
  AND i.name IS NOT NULL
  AND i.type NOT IN (0)
ORDER BY i.is_primary_key DESC, i.is_unique DESC, i.name";

        var indexes = new List<IndexInfo>();

        using (var idxCmd = new SqlCommand(indexesSql, conn))
        {
            idxCmd.Parameters.AddWithValue("@fullName", fullName);
            idxCmd.CommandTimeout = 5;

            using var idxReader = await idxCmd.ExecuteReaderAsync();
            while (await idxReader.ReadAsync())
            {
                var idx = new IndexInfo
                {
                    Name = idxReader["IndexName"]?.ToString() ?? "",
                    TypeDesc = idxReader["IndexType"]?.ToString() ?? "",
                    IsUnique = Convert.ToBoolean(idxReader["IsUnique"]),
                    IsPrimaryKey = Convert.ToBoolean(idxReader["IsPrimaryKey"]),
                    IsUniqueConstraint = Convert.ToBoolean(idxReader["IsUniqueConstraint"]),
                    KeyColumns = idxReader["KeyColumns"]?.ToString() ?? "",
                    IncludedColumns = idxReader["IncludedColumns"]?.ToString(),
                    HasFilter = Convert.ToBoolean(idxReader["has_filter"]),
                    FilterDefinition = idxReader["filter_definition"]?.ToString()
                };
                indexes.Add(idx);
            }
        }

        _logger.Information($"[OpenScript] 获取到 {indexes.Count} 个索引/约束");

        // 构建 CREATE TABLE 脚本
        return BuildCreateTableScript(schema, name, columns, indexes);
    }

    /// <summary>
    /// 从列信息和索引信息构建表结构脚本。
    /// 注意：表使用 CREATE TABLE 语法展示完整结构（与 SSMS "Script Table as > CREATE To" 一致），
    /// 因为 ALTER TABLE 语法是增量修改（ADD/DROP COLUMN），无法展示完整表定义。
    /// </summary>
    private static string BuildCreateTableScript(
        string schema, string name,
        List<ColumnInfo> columns, List<IndexInfo> indexes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{schema}].[{name}]");
        sb.AppendLine("(");

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            sb.Append("    ");

            if (col.IsComputed)
            {
                sb.Append($"[{col.Name}] AS {col.ComputedDefinition}");
            }
            else
            {
                sb.Append($"[{col.Name}] {GetDataTypeString(col)}");

                if (col.IsIdentity)
                {
                    sb.Append(" IDENTITY(1,1)");
                }

                if (!col.IsNullable)
                {
                    sb.Append(" NOT NULL");
                }
                else
                {
                    sb.Append(" NULL");
                }

                if (col.HasDefault && !string.IsNullOrEmpty(col.DefaultDefinition))
                {
                    sb.Append($" DEFAULT {col.DefaultDefinition}");
                }
            }

            if (i < columns.Count - 1)
                sb.AppendLine(",");
            else
                sb.AppendLine();
        }

        // 添加主键约束
        var pk = indexes.Find(i => i.IsPrimaryKey);
        if (pk != null)
        {
            sb.AppendLine($"    CONSTRAINT [{pk.Name}] PRIMARY KEY ({pk.KeyColumns})");
        }

        sb.AppendLine(");");

        // 添加唯一约束
        foreach (var idx in indexes)
        {
            if (idx.IsPrimaryKey || idx.IsUniqueConstraint)
                continue;

            if (idx.IsUnique)
            {
                sb.AppendLine();
                sb.AppendLine($"CREATE UNIQUE {idx.TypeDesc} [{idx.Name}]");
                sb.AppendLine($"    ON [{schema}].[{name}] ({idx.KeyColumns})");
                if (!string.IsNullOrEmpty(idx.IncludedColumns))
                {
                    sb.AppendLine($"    INCLUDE ({idx.IncludedColumns})");
                }
                if (idx.HasFilter && !string.IsNullOrEmpty(idx.FilterDefinition))
                {
                    sb.AppendLine($"    WHERE ({idx.FilterDefinition})");
                }
                sb.AppendLine(";");
            }
        }

        // 添加非唯一索引
        foreach (var idx in indexes)
        {
            if (idx.IsPrimaryKey || idx.IsUnique)
                continue;

            sb.AppendLine();
            sb.AppendLine($"CREATE {idx.TypeDesc} [{idx.Name}]");
            sb.AppendLine($"    ON [{schema}].[{name}] ({idx.KeyColumns})");
            if (!string.IsNullOrEmpty(idx.IncludedColumns))
            {
                sb.AppendLine($"    INCLUDE ({idx.IncludedColumns})");
            }
            if (idx.HasFilter && !string.IsNullOrEmpty(idx.FilterDefinition))
            {
                sb.AppendLine($"    WHERE ({idx.FilterDefinition})");
            }
            sb.AppendLine(";");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 根据列信息生成数据类型字符串。
    /// </summary>
    private static string GetDataTypeString(ColumnInfo col)
    {
        var typeName = col.DataType.ToUpperInvariant();

        switch (typeName)
        {
            case "BIGINT":
            case "INT":
            case "SMALLINT":
            case "TINYINT":
            case "BIT":
            case "DATE":
            case "DATETIME":
            case "DATETIME2":
            case "SMALLDATETIME":
            case "DATETIMEOFFSET":
            case "TIME":
            case "FLOAT":
            case "REAL":
            case "MONEY":
            case "SMALLMONEY":
            case "UNIQUEIDENTIFIER":
            case "TEXT":
            case "NTEXT":
            case "IMAGE":
            case "SQL_VARIANT":
            case "XML":
            case "HIERARCHYID":
            case "GEOGRAPHY":
            case "GEOMETRY":
                return typeName;

            case "DECIMAL":
            case "NUMERIC":
                return col.Precision > 0
                    ? $"{typeName}({col.Precision}, {col.Scale})"
                    : typeName;

            case "CHAR":
            case "VARCHAR":
            case "NCHAR":
                return col.MaxLength == -1 || col.MaxLength > 8000
                    ? $"{typeName}(MAX)"
                    : $"{typeName}({col.MaxLength})";

            case "NVARCHAR":
                return col.MaxLength == -1 || col.MaxLength > 4000
                    ? $"{typeName}(MAX)"
                    : $"{typeName}({col.MaxLength / 2})";

            case "BINARY":
            case "VARBINARY":
                return col.MaxLength == -1
                    ? $"{typeName}(MAX)"
                    : $"{typeName}({col.MaxLength})";

            default:
                return typeName;
        }
    }

    /// <summary>
    /// 自动注释脚本中的首个 ALTER/CREATE 语句行。
    /// 仅注释第一个未注释的以 CREATE 或 ALTER 开头的行，避免注释嵌套定义中的内部语句。
    /// </summary>
    private static string AutoCommentAlterCreate(string script)
    {
        if (string.IsNullOrEmpty(script))
            return script;

        var lines = script.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        bool commented = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if ((trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)) &&
                !trimmed.StartsWith("--"))
            {
                // 仅注释首个未注释的 ALTER/CREATE 行
                lines[i] = "-- " + lines[i];
                commented = true;
                break;
            }
        }

        if (commented)
            return string.Join(Environment.NewLine, lines);
        return script;
    }

    /// <summary>
    /// 在 SSMS 中打开一个新的查询编辑器窗口并填充脚本内容。
    /// 使用临时 .sql 文件方式，兼容 SSMS 各版本。
    /// </summary>
    private void OpenNewQueryWindow(string sqlContent, string objectName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte == null)
            {
                _logger.Warning("DTE 不可用，无法打开新查询窗口");
                return;
            }

            // 创建临时 SQL 文件并打开
            var safeName = objectName.Replace("\\", "_").Replace("/", "_").Replace(".", "_");
            var tempFile = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SSMSBoost_{safeName}_{Guid.NewGuid():N}.sql");

            System.IO.File.WriteAllText(tempFile, sqlContent, Encoding.UTF8);

            dte.ItemOperations.OpenFile(tempFile);

            _logger.Information($"✅ 新查询窗口已打开: '{objectName}' ({sqlContent.Length} 字符)");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "打开新查询窗口失败");
            MessageBox.Show(
                $"打开新查询窗口失败。\n{ex.Message}",
                "SSMSBoostPlus",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Set the SSMS status bar text, or clear it if text is null.
    /// </summary>
    private void SetStatusText(string? text)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var statusBar = (IVsStatusbar?)((System.IServiceProvider)Package).GetService(typeof(SVsStatusbar));
            if (statusBar != null)
            {
                if (text != null)
                    statusBar.SetText(text);
                else
                    statusBar.Clear();
            }
        }
        catch
        {
            // Status bar is best-effort
        }
    }

    #region Data Models

    private class ColumnInfo
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public short MaxLength { get; set; }
        public byte Precision { get; set; }
        public byte Scale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public string? ComputedDefinition { get; set; }
        public bool HasDefault { get; set; }
        public string? DefaultDefinition { get; set; }
        public string? CollationName { get; set; }
    }

    private class IndexInfo
    {
        public string Name { get; set; } = "";
        public string TypeDesc { get; set; } = "";
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsUniqueConstraint { get; set; }
        public string KeyColumns { get; set; } = "";
        public string? IncludedColumns { get; set; }
        public bool HasFilter { get; set; }
        public string? FilterDefinition { get; set; }
    }

    #endregion
}
