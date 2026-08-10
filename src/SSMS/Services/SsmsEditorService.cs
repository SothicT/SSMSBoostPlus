using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services;

/// <summary>
/// SSMS-specific implementation of IEditorService.
/// Accesses the active document's text buffer and selection via VS SDK APIs.
/// Also provides SSMS connection information via official SSMS APIs.
/// </summary>
public class SsmsEditorService : IEditorService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;
    private static bool _hasLoggedAssemblyInfo = false;
    private static readonly object _logLock = new object();

    // ═══════════════════════════════════════════════════
    // 文档打开时间缓存
    // 用于版本守卫的 OpenTime 判断，避免 File.GetLastWriteTime 不准确
    // 首次获取文档路径时记录当前时间，后续复用缓存
    // ═══════════════════════════════════════════════════
    private static readonly Dictionary<string, DateTime> _documentOpenTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _openTimeLock = new();

    public SsmsEditorService(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        HookDocumentActivation();
    }

    // ═══════════════════════════════════════════════════
    // 文档激活追踪（F1）
    // 版本守卫弹窗根据文档焦点显示/隐藏
    // ═══════════════════════════════════════════════════
    private static bool _activationHooked;
    private static readonly object _activationLock = new();

    /// <summary>
    /// 文档激活事件：参数为激活文档路径，null 表示非文档窗口激活。
    /// </summary>
    public static event Action<string?>? DocumentActivated;

    private void HookDocumentActivation()
    {
        if (_activationHooked) return;
        lock (_activationLock)
        {
            if (_activationHooked) return;

            try
            {
                var dte = _serviceProvider.GetService(typeof(DTE)) as DTE;
                if (dte?.Events?.WindowEvents != null)
                {
                    dte.Events.WindowEvents.WindowActivated += (Window gotFocus, Window _) =>
                    {
                        var docPath = gotFocus?.Document?.FullName;
                        DocumentActivated?.Invoke(docPath);
                    };
                    _activationHooked = true;
                }
            }
            catch { /* 忽略 DTE 事件挂载失败 */ }
        }
    }

    /// <summary>
    /// 设置指定文档的只读状态（F1）。
    /// 通过 IVsTextBuffer 设置/清除 BSF_USER_READONLY 标志。
    /// </summary>
    public void SetReadOnly(string? docPath, bool readOnly)
    {
        if (string.IsNullOrEmpty(docPath)) return;

        try
        {
            var rdt = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt == null) return;

            rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, docPath, out _, out _, out var docDataPtr, out _);
            if (docDataPtr != IntPtr.Zero)
            {
                var docDataObj = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(docDataPtr);
                if (docDataObj is IVsTextBuffer buffer)
                {
                    buffer.GetStateFlags(out uint flags);
                    if (readOnly)
                        flags |= (uint)BUFFERSTATEFLAGS.BSF_USER_READONLY;
                    else
                        flags &= ~(uint)BUFFERSTATEFLAGS.BSF_USER_READONLY;
                    buffer.SetStateFlags(flags);
                }
            }
        }
        catch { /* 忽略只读设置失败 */ }
    }

    /// <inheritdoc />
    public async Task<(string Code, bool IsSelection)> GetCodeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var textDocument = GetActiveTextDocument();
        if (textDocument == null) return (string.Empty, false);

        var selection = textDocument.Selection;
        if (selection != null && !selection.IsEmpty)
        {
            var selectedText = selection.Text;
            if (!string.IsNullOrEmpty(selectedText))
                return (selectedText, true);
        }

        var editPoint = textDocument.StartPoint.CreateEditPoint();
        return (editPoint.GetText(textDocument.EndPoint), false);
    }

    /// <inheritdoc />
    public async Task ReplaceSelectionAsync(string newText)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var textDocument = GetActiveTextDocument();
        if (textDocument == null) return;

        var selection = textDocument.Selection;
        selection.Delete();
        selection.Insert(newText);
    }

    /// <inheritdoc />
    public async Task ReplaceAllAsync(string newText)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var textDocument = GetActiveTextDocument();
        if (textDocument == null) return;

        var start = textDocument.StartPoint.CreateEditPoint();
        var end = textDocument.EndPoint.CreateEditPoint();
        start.ReplaceText(end, newText, 1); // vsEPReplaceTextOptions.vsEPReplaceTextAutoformat = 1
    }

    /// <summary>
    /// 获取活动文档的完整文本（忽略选择区域）（F2）。
    /// 用于版本守卫弹窗重新打开比对时获取最新编辑器内容，
    /// 避免使用过时的缓存代码导致差异行误判。
    /// </summary>
    public async Task<string> GetFullTextAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var textDocument = GetActiveTextDocument();
        if (textDocument == null) return string.Empty;

        var editPoint = textDocument.StartPoint.CreateEditPoint();
        return editPoint.GetText(textDocument.EndPoint);
    }

    /// <summary>
    /// 获取当前光标所在行的完整文本（包含选中区域所在行）。
    /// 用于 OpenScriptCommand 解析选中对象所在行的完整 3 段名（如 [MESDATA].dbo.SP）。
    /// </summary>
    public async Task<string> GetCurrentLineTextAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var textDocument = GetActiveTextDocument();
        if (textDocument == null) return string.Empty;

        var selection = textDocument.Selection;
        if (selection == null) return string.Empty;

        // 获取选中区域所在行号
        int line = selection.TopPoint.Line;

        // 从文档起始导航到目标行
        var startPoint = textDocument.StartPoint.CreateEditPoint();
        if (line > 1)
            startPoint.LineDown(line - 1);
        startPoint.StartOfLine();

        var endPoint = startPoint.CreateEditPoint();
        endPoint.EndOfLine();

        return startPoint.GetText(endPoint);
    }

    /// <inheritdoc />
    public string? GetActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dte = (DTE?)_serviceProvider.GetService(typeof(DTE));
        var path = dte?.ActiveDocument?.FullName;

        // 首次遇到此文档路径时，使用文件创建时间作为"打开时间"
        // 避免第一次按F5时才记录DateTime.Now，导致OpenTime晚于实际文档打开时间
        if (!string.IsNullOrEmpty(path))
        {
            lock (_openTimeLock)
            {
                if (!_documentOpenTimes.ContainsKey(path))
                {
                    _documentOpenTimes[path] = GetFileCreationTime(path);
                    _logger?.Debug($"[DocOpenTime] ✅ 文档首次打开: {Path.GetFileName(path)} at {_documentOpenTimes[path]:HH:mm:ss} (created)");
                }
            }
        }

        return path;
    }

    /// <summary>
    /// 获取文档的脚本打开时间（版本守卫用）。
    /// 优先使用编辑器首次打开时间的缓存，避免 File.GetLastWriteTime 的"保存即刷新"问题。
    /// 对于无路径的文档（新查询页），使用 DateTime.Now。
    /// </summary>
    public DateTime GetScriptOpenTime(string? docPath)
    {
        if (string.IsNullOrEmpty(docPath))
            return DateTime.Now;

        lock (_openTimeLock)
        {
            if (_documentOpenTimes.TryGetValue(docPath, out var cachedTime))
                return cachedTime;
        }

        // Fallback: 文件创建时间（比 LastWriteTime 更稳定）
        try
        {
            return File.GetCreationTime(docPath);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>
    /// 获取文件创建时间作为文档打开时间的近似值。
    /// SSMS 临时脚本文件（如 dbo.xxx1.sql）的创建时间 ≈ 文档打开时间。
    /// 相比 DateTime.Now，文件创建时间更准确反映用户实际打开文档的时刻。
    /// </summary>
    private static DateTime GetFileCreationTime(string path)
    {
        try
        {
            var creationTime = File.GetCreationTime(path);
            if (creationTime > DateTime.Now || creationTime == DateTime.MinValue)
                return DateTime.Now;
            return creationTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>
    /// 更新文档打开时间缓存。
    /// 在脚本执行成功后调用，使 OpenTime 与当前时间对齐，
    /// 避免因脚本自身执行导致的 ModifyDate=OpenTime 误判。
    /// </summary>
    public static void UpdateScriptOpenTime(string? docPath)
    {
        if (string.IsNullOrEmpty(docPath)) return;
        lock (_openTimeLock)
        {
            _documentOpenTimes[docPath] = DateTime.Now;
        }
    }

    /// <summary>
    /// 将文档打开时间缓存设置为指定时间（F3）。
    /// 用于脚本执行后：查询 DB 实际 modify_date 并设为 OpenTime，
    /// 这样下次 F5 时 modify_date == OpenTime → IsOutdated(>) 为 false → 不弹窗。
    /// 只有其他人再次修改 DB 对象后 modify_date > OpenTime 才会触发弹窗。
    /// </summary>
    public static void SetScriptOpenTime(string? docPath, DateTime time)
    {
        if (string.IsNullOrEmpty(docPath)) return;
        lock (_openTimeLock)
        {
            _documentOpenTimes[docPath] = time;
        }
    }

    /// <summary>
    /// 清除文档打开时间缓存（当前 SSMS 会话的所有缓存）。
    /// </summary>
    public static void ClearDocumentOpenTimes()
    {
        lock (_openTimeLock)
        {
            _documentOpenTimes.Clear();
        }
    }

    /// <inheritdoc />
    public bool IsSqlDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dte = (DTE?)_serviceProvider.GetService(typeof(DTE));
        if (dte?.ActiveDocument == null) return false;

        var document = dte.ActiveDocument;
        var textDocument = document.Object("TextDocument") as TextDocument;

        return textDocument?.Language?.Equals("SQL", StringComparison.OrdinalIgnoreCase) == true
            || (document.FullName?.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Get the current connection string from SSMS's active connection.
    /// 
    /// Multi-layer fallback strategy:
    /// Layer 1: SSMS Internal Services (reflection)
    ///   - Method 1: ServiceCache.ConnectionDialog.CurrentConnection
    ///   - Method 2: ScriptFactory.CurrentlyActiveWndConnectionInfo
    ///   - Method 3: ServiceCache.ObjectExplorer.SelectedNode.Connection
    ///   - Method 4: IObjectExplorerService.GetSelectedNodes
    /// Layer 2: VS SDK Standard Services
    ///   - Method 5: IVsDataConnectionsService
    /// 
    /// Note: These SSMS internal types are loaded at runtime via reflection.
    /// They are NOT available as compile-time references.
    /// </summary>
    public string? GetActiveConnectionString()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Log assembly information on first call
        LogLoadedAssemblies();

        try
        {
            // === Layer 1: SSMS Internal Services ===
            _logger?.Debug("=== 第1层: SSMS 内部服务 ===");

            // Method 1: ConnectionDialog
            var connStr = TryGetFromConnectionDialog();
            if (!string.IsNullOrEmpty(connStr))
            {
                _logger?.Information("✅ 从 ConnectionDialog 获取到连接");
                return connStr;
            }

            // Method 2: ScriptFactory
            connStr = TryGetFromScriptFactory();
            if (!string.IsNullOrEmpty(connStr))
            {
                _logger?.Information("✅ 从 ScriptFactory 获取到连接");
                return connStr;
            }

            // Method 3: ObjectExplorer via ServiceCache
            connStr = TryGetFromObjectExplorer();
            if (!string.IsNullOrEmpty(connStr))
            {
                _logger?.Information("✅ 从 ObjectExplorer 获取到连接");
                return connStr;
            }

            // Method 4: IObjectExplorerService via ServiceProvider
            connStr = TryGetFromObjectExplorerService();
            if (!string.IsNullOrEmpty(connStr))
            {
                _logger?.Information("✅ 从 IObjectExplorerService 获取到连接");
                return connStr;
            }

            // === Layer 2: VS SDK Standard Services ===
            _logger?.Debug("=== 第2层: VS SDK 标准服务 ===");

            // Method 5: VS Data Connections
            connStr = TryGetFromVsDataConnections();
            if (!string.IsNullOrEmpty(connStr))
            {
                _logger?.Information("✅ 从 VS Data Connections 获取到连接");
                return connStr;
            }
            
            // Method 6: VS Data Services via GUID
            connStr = TryGetFromVsDataServices();
            if (!string.IsNullOrEmpty(connStr))
            {
                _logger?.Information("✅ 从 VS Data Services via GUID 获取到连接");
                return connStr;
            }

            _logger?.Warning("❌ 所有方法均未能获取到连接");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "GetActiveConnectionString 中出错");
            return null;
        }
    }

    /// <summary>
    /// Get the current database name from SSMS's active connection.
    /// </summary>
    public string? GetActiveDatabaseName()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var connStr = GetActiveConnectionString();
            if (!string.IsNullOrEmpty(connStr))
            {
                var builder = new SqlConnectionStringBuilder(connStr);
                return builder.InitialCatalog;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #region SSMS Connection Resolution via Reflection

    /// <summary>
    /// SSMS internal service types (loaded via reflection at runtime)
    /// Based on SSMS 22 (Version 22.200.0.0) diagnostic logs
    /// </summary>
    private static class SsmsInternalTypes
    {
        // ObjectExplorerService - SSMS 22 specific
        public const string ObjectExplorerService = 
            "Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService, " +
            "Microsoft.SqlServer.Management.SqlStudio.Explorer, " +
            "Version=22.200.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91";
        
        // ConnectionDialogService - has static Instance property
        public const string ConnectionDialogService = 
            "Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService, " +
            "Microsoft.SqlServer.ConnectionDlg.UI, " +
            "Version=18.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
        
        // ConnectionDialog - has ConnectionInfo property
        public const string ConnectionDialog = 
            "Microsoft.SqlServer.Management.UI.ConnectionDlg.ConnectionDialog, " +
            "ConnectionDlg, " +
            "Version=22.200.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91";
    }

    /// <summary>
    /// Method 1: Get connection from ConnectionDialogService.Instance
    /// SSMS 22: Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService
    /// </summary>
    private string? TryGetFromConnectionDialog()
    {
        try
        {
            _logger?.Debug("方式1: 尝试 ConnectionDialogService");

            var serviceType = Type.GetType(SsmsInternalTypes.ConnectionDialogService);
            if (serviceType == null)
            {
                _logger?.Debug("  未找到 ConnectionDialogService 类型");
                return null;
            }

            // Get static Instance property
            var instanceProp = serviceType.GetProperty("Instance", 
                BindingFlags.Public | BindingFlags.Static);
            if (instanceProp == null)
            {
                _logger?.Debug("  未找到 Instance 属性");
                return null;
            }

            var serviceInstance = instanceProp.GetValue(null);
            if (serviceInstance == null)
            {
                _logger?.Debug("  服务实例为空");
                return null;
            }

            _logger?.Debug($"  找到服务: {serviceInstance.GetType().FullName}");

            // Try to find active connection from service
            // The service might have DependencyManager or other properties
            var depManagerProp = serviceInstance.GetType().GetProperty("DependencyManager");
            if (depManagerProp != null)
            {
                var depManager = depManagerProp.GetValue(serviceInstance);
                if (depManager != null)
                {
                    _logger?.Debug($"  找到 DependencyManager: {depManager.GetType().FullName}");

                    // Explore DependencyManager properties
                    var dmProps = depManager.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in dmProps)
                    {
                        _logger?.Debug($"    属性: {prop.Name} ({prop.PropertyType.Name})");
                        try
                        {
                            var value = prop.GetValue(depManager);
                            if (value != null)
                            {
                                _logger?.Debug($"      值类型: {value.GetType().FullName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug($"      获取值失败: {ex.Message}");
                        }
                    }
                    
                    // Explore DependencyManager methods
                    var dmMethods = depManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var method in dmMethods)
                    {
                        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        _logger?.Debug($"    方法: {method.Name}({parameters})");
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"  ConnectionDialogService 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Method 2: Get connection from ObjectExplorerService
    /// SSMS 22: Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService
    /// </summary>
    private string? TryGetFromScriptFactory()
    {
        try
        {
            _logger?.Debug("方式2: 尝试 ObjectExplorerService");

            var serviceType = Type.GetType(SsmsInternalTypes.ObjectExplorerService);
            if (serviceType == null)
            {
                _logger?.Debug("  未找到 ObjectExplorerService 类型");
                return null;
            }

            // Get service from ServiceProvider
            var service = _serviceProvider.GetService(serviceType);
            if (service == null)
            {
                _logger?.Debug("  ObjectExplorerService 无法从 ServiceProvider 获取");
                return null;
            }

            _logger?.Debug($"  找到服务: {service.GetType().FullName}");

            // ObjectExplorerService has Site and Container properties
            // We need to find a way to get the selected node or current connection
            // This requires exploring the object structure further

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"  ObjectExplorerService failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract connection string from connection object using reflection
    /// </summary>
    private string? ExtractConnectionString(object connection)
    {
        try
        {
            // Try ConnectionString property first
            var connStrProp = connection.GetType().GetProperty("ConnectionString");
            if (connStrProp != null)
            {
                var connStr = connStrProp.GetValue(connection) as string;
                if (!string.IsNullOrEmpty(connStr))
                {
                    _logger?.Information("从 ConnectionString 属性获取到连接字符串");
                    return connStr;
                }
            }

            // Fallback: build from connection properties
            return BuildConnectionStringFromConnectionInfo(connection);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "提取连接字符串失败");
            return null;
        }
    }

    /// <summary>
    /// Method 3: Get connection from DTE.ActiveDocument connection
    /// For query editor windows, get the connection from the document context
    /// </summary>
    private string? TryGetFromObjectExplorer()
    {
        try
        {
            _logger?.Debug("方式3: 尝试 DTE ActiveDocument 连接");

            var dte = (DTE?)_serviceProvider.GetService(typeof(DTE));
            if (dte?.ActiveDocument == null)
            {
                _logger?.Debug("  无活动文档");
                return null;
            }

            // For SQL documents, try to get connection from document properties
            var document = dte.ActiveDocument;
            _logger?.Debug($"  活动文档: {document.FullName}");

            // Check if this is a SQL document
            if (!document.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                !IsSqlDocument())
            {
                _logger?.Debug("  不是 SQL 文档");
                return null;
            }

            // Try to get connection from EnvDTE.ProjectItem or document context
            // This requires exploring SSMS-specific document properties
            // For now, return null and let other methods handle it
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"  DTE ActiveDocument 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Method 4: Get connection from ObjectExplorerService via ServiceProvider
    /// </summary>
    private string? TryGetFromObjectExplorerService()
    {
        try
        {
            _logger?.Debug("方式4: 通过 ServiceProvider 尝试 ObjectExplorerService");

            var serviceType = Type.GetType(SsmsInternalTypes.ObjectExplorerService);
            if (serviceType == null)
            {
                _logger?.Debug("  未找到 ObjectExplorerService 类型");
                return null;
            }

            var service = _serviceProvider.GetService(serviceType);
            if (service == null)
            {
                _logger?.Debug("  ObjectExplorerService 不可用");
                return null;
            }

            _logger?.Debug($"  找到服务实例: {service.GetType().AssemblyQualifiedName}");

            // Try to find GetSelectedNodes or similar method
            var type = service.GetType();
            
            // List all methods for debugging
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                _logger?.Debug($"  Method: {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
            }

            // List all properties for debugging
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                _logger?.Debug($"  属性: {prop.Name} ({prop.PropertyType.Name})");

                // Try to get property value for debugging
                try
                {
                    var value = prop.GetValue(service);
                    if (value != null)
                    {
                        _logger?.Debug($"    值: {value.GetType().FullName}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"    Failed to get value: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"  ObjectExplorerService failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Method 5: Get connection from VS Data Connections Service (Layer 2)
    /// </summary>
    private string? TryGetFromVsDataConnections()
    {
        try
        {
            _logger?.Debug("方式5: 尝试 VS Data Connections 服务");

            // Try to get IVsDataConnectionsService from VS SDK
            var vsDataSvcType = Type.GetType(
                "Microsoft.VisualStudio.Data.Services.IVsDataConnectionsService, " +
                "Microsoft.VisualStudio.Data.Services");

            if (vsDataSvcType == null)
            {
                _logger?.Debug("  未找到 IVsDataConnectionsService 类型");
                return null;
            }

            var vsDataSvc = _serviceProvider.GetService(vsDataSvcType);
            if (vsDataSvc == null)
            {
                _logger?.Debug("  VS Data Connections 服务不可用");
                return null;
            }

            // Try SelectedConnection property
            var selectedConnProp = vsDataSvcType.GetProperty("SelectedConnection");
            if (selectedConnProp == null)
            {
                _logger?.Debug("  SelectedConnection property not found");
                return null;
            }

            var selectedConnection = selectedConnProp.GetValue(vsDataSvc);
            if (selectedConnection == null)
            {
                _logger?.Debug("  无选中连接");
                return null;
            }

            // Try to get connection string
            var connStrProp = selectedConnection.GetType().GetProperty("DisplayConnectionString");
            if (connStrProp != null)
            {
                var connStr = connStrProp.GetValue(selectedConnection) as string;
                if (!string.IsNullOrEmpty(connStr))
                {
                    _logger?.Debug($"  从 VS Data Connections 获取到连接字符串");
                    return connStr;
                }
            }

            // Alternative: try EncryptedConnectionString
            var encryptedConnStrProp = selectedConnection.GetType().GetProperty("EncryptedConnectionString");
            if (encryptedConnStrProp != null)
            {
                var connStr = encryptedConnStrProp.GetValue(selectedConnection) as string;
                if (!string.IsNullOrEmpty(connStr))
                {
                    _logger?.Debug($"  从 VS Data Connections 获取到加密连接字符串");
                    return connStr;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"  VS Data Connections 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Method 6: Try VS Data Services by searching for types with "DataConnection" or "Data" keywords
    /// VS SDK standard services that might be available in SSMS
    /// </summary>
    private string? TryGetFromVsDataServices()
    {
        try
        {
            _logger?.Debug("方式6: 按类型搜索尝试 VS Data Services");
            
            // Search for potential VS data service types in loaded assemblies
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (!assembly.FullName.Contains("Microsoft.VisualStudio")) continue;
                if (!assembly.FullName.Contains("Data")) continue;
                
                try
                {
                    var types = assembly.GetTypes();
                    foreach (var type in types)
                    {
                        if (!type.IsInterface && !type.IsAbstract && 
                            (type.Name.Contains("DataConnection") || type.Name.Contains("DataService")))
                        {
                            _logger?.Debug($"  发现潜在服务类型: {type.FullName}");
                            
                            // Try to get service
                            try
                            {
                                var service = _serviceProvider.GetService(type);
                                if (service != null)
                                {
                                    _logger?.Debug($"    找到服务实例: {service.GetType().FullName}");

                                // Explore the service
                                var serviceProps = service.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                foreach (var prop in serviceProps)
                                {
                                    _logger?.Debug($"    属性: {prop.Name} ({prop.PropertyType.Name})");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"    获取服务失败: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"  Failed to get types from {assembly.FullName}: {ex.Message}");
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"  VS Data Services 搜索失败: {ex.Message}");
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Build connection string from connection object properties via reflection.
    /// This is a fallback method when ConnectionString property is not available.
    /// </summary>
    private string? BuildConnectionStringFromConnectionInfo(object connInfo)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                TrustServerCertificate = true  // SSMS 22 默认启用加密，必须信任自签名证书
            };
            var connType = connInfo.GetType();

            // Server instance
            var serverInstanceProp = connType.GetProperty("ServerInstance");
            if (serverInstanceProp != null)
            {
                var serverInstance = serverInstanceProp.GetValue(connInfo) as string;
                if (!string.IsNullOrEmpty(serverInstance))
                {
                    builder.DataSource = serverInstance;
                }
            }

            // Authentication
            var useIntegratedSecurityProp = connType.GetProperty("UseIntegratedSecurity");
            if (useIntegratedSecurityProp != null)
            {
                var useIntegratedSecurity = (bool)useIntegratedSecurityProp.GetValue(connInfo);
                if (useIntegratedSecurity)
                {
                    builder.IntegratedSecurity = true;
                }
                else
                {
                    builder.IntegratedSecurity = false;

                    var loginProp = connType.GetProperty("Login");
                    if (loginProp != null)
                    {
                        var login = loginProp.GetValue(connInfo) as string;
                        if (!string.IsNullOrEmpty(login))
                        {
                            builder.UserID = login;
                        }
                    }

                    var passwordProp = connType.GetProperty("Password");
                    if (passwordProp != null)
                    {
                        var password = passwordProp.GetValue(connInfo) as string;
                        if (!string.IsNullOrEmpty(password))
                        {
                            builder.Password = password;
                        }
                    }
                }
            }

            // Database
            var databaseNameProp = connType.GetProperty("DatabaseName");
            if (databaseNameProp != null)
            {
                var databaseName = databaseNameProp.GetValue(connInfo) as string;
                if (!string.IsNullOrEmpty(databaseName))
                {
                    builder.InitialCatalog = databaseName;
                }
            }

            // Connection timeout
            var connectionTimeoutProp = connType.GetProperty("ConnectionTimeout");
            if (connectionTimeoutProp != null)
            {
                var connectionTimeout = (int)connectionTimeoutProp.GetValue(connInfo);
                builder.ConnectTimeout = connectionTimeout;
            }

            return builder.ConnectionString;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "从连接信息构建连接字符串失败");
            return null;
        }
    }

    /// <summary>
    /// 激活指定路径的文档（使其成为 ActiveDocument）。
    /// 用于版本守卫弹窗绑定到触发编辑器：替换内容前先激活源文档。
    /// </summary>
    public void ActivateDocument(string? path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var dte = (DTE?)_serviceProvider.GetService(typeof(DTE));
            if (dte == null) return;

            foreach (Document doc in dte.Documents)
            {
                if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase))
                {
                    doc.Activate();
                    return;
                }
            }
        }
        catch
        {
            // 静默忽略：文档可能已关闭
        }
    }

    private TextDocument? GetActiveTextDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = (DTE?)_serviceProvider.GetService(typeof(DTE));
            if (dte?.ActiveDocument == null) return null;

            return dte.ActiveDocument.Object("TextDocument") as TextDocument;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Diagnostic method to log all loaded assemblies and relevant types.
    /// Helps identify correct assembly names and type names for SSMS internal services.
    /// </summary>
    private void LogLoadedAssemblies()
    {
        lock (_logLock)
        {
            if (_hasLoggedAssemblyInfo) return;
            _hasLoggedAssemblyInfo = true;
        }

        try
        {
            _logger?.Information("=== DIAGNOSTIC: Loaded Assemblies Analysis ===");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var relevantKeywords = new[] 
            { 
                "SqlServer", "Management", "Explorer", "Script", "Connection",
                "Database", "Data", "VSIntegration", "SqlStudio"
            };

            foreach (var assembly in assemblies)
            {
                var name = assembly.FullName;
                if (string.IsNullOrEmpty(name)) continue;

                // Check if assembly name contains relevant keywords
                var isRelevant = relevantKeywords.Any(k => 
                    name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isRelevant) continue;

                _logger?.Information($"程序集: {name}");

                try
                {
                    var types = assembly.GetTypes();
                    foreach (var type in types)
                    {
                        if (!type.IsPublic) continue;

                        var typeName = type.FullName;
                        if (string.IsNullOrEmpty(typeName)) continue;

                        // Look for specific service types
                        if (typeName.Contains("ServiceCache") ||
                            typeName.Contains("ScriptFactory") ||
                            typeName.Contains("ObjectExplorer") ||
                            typeName.Contains("ConnectionDialog") ||
                            typeName.Contains("IScriptEditor") ||
                            typeName.Contains("IObjectExplorer"))
                        {
                            _logger?.Information($"  Type: {typeName}");
                            _logger?.Information($"    AssemblyQualifiedName: {type.AssemblyQualifiedName}");

                            // Log properties
                            try
                            {
                                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Static);
                                if (props.Length > 0)
                                {
                                    _logger?.Information($"    Static Properties: {string.Join(", ", props.Select(p => p.Name))}");
                                }

                                var instanceProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                if (instanceProps.Length > 0)
                                {
                                    _logger?.Information($"    Instance Properties: {string.Join(", ", instanceProps.Select(p => p.Name))}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"    Failed to get properties: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"  Failed to get types from {name}: {ex.Message}");
                }
            }

            _logger?.Information("=== DIAGNOSTIC END ===");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "记录程序集信息失败");
        }
    }
}
