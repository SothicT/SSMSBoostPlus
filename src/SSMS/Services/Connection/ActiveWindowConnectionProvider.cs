using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Instrumentation;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// 从活动代码窗口获取连接信息
/// 这是最可靠的方式，因为用户打开查询窗口时，连接信息必然存在
/// </summary>
public class ActiveWindowConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "Active Window Connection Provider";
    public int Priority => -1; // 最高优先级

    public bool IsAvailable => true;

    public ActiveWindowConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger?.Debug("[ActiveWindowProvider] Attempting to get connection from active window...");

            // 确保程序集已加载
            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // 策略1: 从 DTE.ActiveDocument 获取
            var connection = TryGetFromActiveDocument();
            if (connection != null)
            {
                _logger?.Information("[ActiveWindowProvider] ✅ Got connection from active document");
                return connection;
            }

            // 策略2: 从 ServiceCache.GlobalConnectionInfo 获取（延迟获取）
            connection = await TryGetFromGlobalConnectionInfoAsync();
            if (connection != null)
            {
                _logger?.Information("[ActiveWindowProvider] ✅ Got connection from GlobalConnectionInfo");
                return connection;
            }

            // 策略3: 从 ScriptFactory 获取（带异常保护）
            connection = TryGetFromScriptFactory();
            if (connection != null)
            {
                _logger?.Information("[ActiveWindowProvider] ✅ Got connection from ScriptFactory");
                return connection;
            }

            // 策略4: 终极获取方案（带异常保护）
            connection = GetCurrentConnectionFromSSMS();
            if (connection != null)
            {
                _logger?.Information("[ActiveWindowProvider] ✅ Got connection from SSMSService");
                return connection;
            }

            _logger?.Warning("[ActiveWindowProvider] ❌ Failed to get connection from active window");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ActiveWindowProvider] Exception occurred");
            return null;
        }
    }

    /// <summary>
    /// 从活动文档获取连接信息
    /// </summary>
    private ConnectionInfo? TryGetFromActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ActiveWindowProvider] Trying DTE.ActiveDocument...");

            // 获取 DTE
            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            if (dteType == null)
            {
                _logger?.Debug("[ActiveWindowProvider] DTE type not found");
                return null;
            }

            var dte = _serviceProvider.GetService(dteType);
            if (dte == null)
            {
                _logger?.Debug("[ActiveWindowProvider] DTE service not available");
                return null;
            }

            // 使用dynamic关键字访问COM对象（ActiveDocument是COM对象，不能用反射）
            dynamic dteDynamic = dte;

            // 预获取 ActiveWindow 以供后续 TextDocument 路径使用
            dynamic activeWindow = null;
            try { activeWindow = dteDynamic.ActiveWindow; } catch { }
            
            dynamic activeDoc = null;
            try
            {
                activeDoc = dteDynamic.ActiveDocument;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] Failed to get ActiveDocument: {ex.Message}");
                return null;
            }
            
            if (activeDoc == null)
            {
                _logger?.Debug("[ActiveWindowProvider] No active document");
                return null;
            }

            _logger?.Debug($"[ActiveWindowProvider] Active document type: {activeDoc.GetType().FullName}");

            // 尝试获取 ProjectItem（使用dynamic访问COM对象属性）
            try
            {
                dynamic projectItem = activeDoc.ProjectItem;
                
                if (projectItem != null)
                {
                    _logger?.Debug($"[ActiveWindowProvider] ProjectItem type: {projectItem.GetType().FullName}");
                    
                    // 尝试获取 Object 属性
                    try
                    {
                        dynamic documentObject = projectItem.Object;
                        if (documentObject != null)
                        {
                            _logger?.Debug($"[ActiveWindowProvider] Document object: {documentObject.GetType().FullName}");
                            
                            // 检查是否实现了 IConnectionInfo 接口
                            var connectionInfo = TryExtractConnectionFromObject(documentObject);
                            if (connectionInfo != null)
                            {
                                return connectionInfo;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[ActiveWindowProvider] Failed to get ProjectItem.Object: {ex.Message}");
                    }
                }
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] ProjectItem property not found: {ex.Message}");
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] COM error accessing ProjectItem: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] Error accessing ProjectItem: {ex.Message}");
            }

            // 尝试从文档的 Properties 中获取连接信息
            try
            {
                dynamic properties = activeDoc.Properties;
                
                if (properties != null)
                {
                    _logger?.Debug("[ActiveWindowProvider] Exploring document properties...");
                    
                    try
                    {
                        // 尝试获取常见的连接相关属性
                        dynamic connectionProperty = properties.Item("Connection");
                        if (connectionProperty != null)
                        {
                            _logger?.Debug($"[ActiveWindowProvider] Found Connection property: {connectionProperty.GetType().FullName}");
                            return TryExtractConnectionFromObject(connectionProperty);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[ActiveWindowProvider] Connection property not found: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] Failed to access Properties: {ex.Message}");
            }

            // ✅ SSMS 查询窗口：通过 DTE Document.Object 拿编辑器 → 连接
            // 🎯 核心修复：COM 对象必须用 dynamic 全程访问，不能用反射（Type.GetProperty 在 COM RCW 上返回空）
            try
            {
                // 1. 通过 Document.Object 获取编辑器的 COM 对象（SSMS 查询窗口的编辑器对象）
                dynamic documentObject = null;
                try { documentObject = activeDoc.Object; } catch { }
                // 若 Object 属性不可用，尝试 COM 索引器 get_Object("")
                if (documentObject == null)
                {
                    try { documentObject = activeDoc.get_Object(""); } catch { }
                }

                if (documentObject != null)
                {
                    _logger?.Debug($"[ActiveWindowProvider] Document.Object type: {documentObject.GetType().FullName}");

                    // 2. 尝试从编辑器对象中获取连接属性（全部用 dynamic）
                    dynamic connection = null;
                    try { connection = documentObject.Connection; } catch { }
                    if (connection == null) try { connection = documentObject.CurrentConnection; } catch { }
                    if (connection == null) try { connection = documentObject.SqlConnection; } catch { }

                    if (connection != null)
                    {
                        _logger?.Debug($"[ActiveWindowProvider] Got connection object: {connection.GetType().FullName}");

                        // 3. 尝试提取 SqlConnection
                        try
                        {
                            dynamic sqlConn = connection.SqlConnection;
                            if (sqlConn != null)
                            {
                                _logger?.Debug($"[ActiveWindowProvider] ✅ SqlConnection: {sqlConn.DataSource}/{sqlConn.Database}");
                                return TryExtractConnectionFromObject(sqlConn);
                            }
                        }
                        catch { }

                        // 如果 SqlConnection 不行，直接尝试从 connection 提取连接信息
                        return TryExtractConnectionFromObject(connection);
                    }
                }

                // 如果直接 Object 拿不到，尝试通过 ActiveWindow 走 TextDocument 路线
                _logger?.Debug("[ActiveWindowProvider] Document.Object returned null, trying ActiveWindow.TextDocument...");
                try
                {
                    dynamic textDoc = activeWindow.TextDocument;
                    if (textDoc != null)
                    {
                        _logger?.Debug($"[ActiveWindowProvider] TextDocument type: {textDoc.GetType().FullName}");
                        dynamic lang = null;
                        try { lang = textDoc.Language; } catch { }
                        if (lang != null)
                        {
                            _logger?.Debug($"[ActiveWindowProvider] TextDocument Language: {lang}");
                        }
                        // TextDocument 本身可能携带 Connection
                        dynamic conn = null;
                        try { conn = textDoc.Connection; } catch { }
                        if (conn != null)
                        {
                            return TryExtractConnectionFromObject(conn);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[ActiveWindowProvider] TextDocument access failed: {ex.Message}");
                }

                _logger?.Debug("[ActiveWindowProvider] Could not get connection from COM document object");
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] COM document access failed: {ex}");
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ActiveWindowProvider] ActiveDocument failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 GlobalConnectionInfo 获取（延迟获取，带重试）
    /// </summary>
    private async Task<ConnectionInfo?> TryGetFromGlobalConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger?.Debug("[ActiveWindowProvider] Trying ServiceCache.GlobalConnectionInfo...");

            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[ActiveWindowProvider] ServiceCache type not found");
                return null;
            }

            var globalConnInfoProp = serviceCacheType.GetProperty("GlobalConnectionInfo",
                BindingFlags.Public | BindingFlags.Static);

            if (globalConnInfoProp == null)
            {
                _logger?.Debug("[ActiveWindowProvider] GlobalConnectionInfo property not found");
                return null;
            }

            var globalConnInfo = globalConnInfoProp.GetValue(null);
            
            if (globalConnInfo == null)
            {
                _logger?.Debug("[ActiveWindowProvider] GlobalConnectionInfo is null");
                return null;
            }

            _logger?.Debug($"[ActiveWindowProvider] GlobalConnectionInfo type: {globalConnInfo.GetType().FullName}");
            
            // 探索其属性
            var type = globalConnInfo.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            
            foreach (var prop in props)
            {
                _logger?.Debug($"[ActiveWindowProvider]   {prop.Name}: {prop.PropertyType.Name}");
                
                try
                {
                    var value = prop.GetValue(globalConnInfo);
                    if (value != null)
                    {
                        var connInfo = TryExtractConnectionFromObject(value);
                        if (connInfo != null)
                        {
                            return connInfo;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[ActiveWindowProvider]     Error: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ActiveWindowProvider] GlobalConnectionInfo failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 ScriptFactory 获取（带异常保护）
    /// </summary>
    private ConnectionInfo? TryGetFromScriptFactory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ActiveWindowProvider] Trying ServiceCache.ScriptFactory...");

            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[ActiveWindowProvider] ServiceCache type not found");
                return null;
            }

            var scriptFactoryProp = serviceCacheType.GetProperty("ScriptFactory",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (scriptFactoryProp == null)
            {
                _logger?.Debug("[ActiveWindowProvider] ScriptFactory property not found");
                return null;
            }

            // 带异常保护的获取
            object? scriptFactory = null;
            try
            {
                scriptFactory = scriptFactoryProp.GetValue(null);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[ActiveWindowProvider] ScriptFactory access failed: {ex.Message}");
                _logger.Debug($"完整异常堆栈：{ex}");
                return null;
            }

            if (scriptFactory == null)
            {
                _logger?.Debug("[ActiveWindowProvider] ScriptFactory is null");
                return null;
            }

            _logger?.Debug($"[ActiveWindowProvider] ScriptFactory type: {scriptFactory.GetType().FullName}");

            // 查找获取当前活动窗口连接信息的方法
            var type = scriptFactory.GetType();
            
            // 常见的方法名：
            // - GetCurrentlyActiveWndConnectionInfo
            // - GetCurrentConnection
            // - ActiveConnection
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            
            foreach (var method in methods)
            {
                if (method.Name.Contains("Connection") && method.GetParameters().Length == 0)
                {
                    _logger?.Debug($"[ActiveWindowProvider]   Found method: {method.Name}");
                    
                    try
                    {
                        var connection = method.Invoke(scriptFactory, null);
                        if (connection != null)
                        {
                            _logger?.Debug($"[ActiveWindowProvider]     Connection type: {connection.GetType().FullName}");
                            
                            var connInfo = TryExtractConnectionFromObject(connection);
                            if (connInfo != null)
                            {
                                return connInfo;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[ActiveWindowProvider]     Method call failed: {ex.Message}");
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ActiveWindowProvider] ScriptFactory failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从对象中尝试提取连接信息
    /// </summary>
    private ConnectionInfo? TryExtractConnectionFromObject(object obj)
    {
        try
        {
            var type = obj.GetType();
            
            // 检查是否实现了 IConnectionInfo 接口
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.Name.Contains("IConnectionInfo"))
                {
                    _logger?.Debug($"[ActiveWindowProvider] Object implements {iface.Name}");
                    
                    // 尝试获取连接属性
                    var serverInstanceProp = type.GetProperty("ServerInstance");
                    var databaseNameProp = type.GetProperty("DatabaseName");
                    var connectionStringProp = type.GetProperty("ConnectionString");
                    
                    if (serverInstanceProp != null || databaseNameProp != null || connectionStringProp != null)
                    {
                        var connInfo = new ConnectionInfo
                        {
                            SourceProvider = ProviderName,
                            ServerInstance = serverInstanceProp?.GetValue(obj) as string,
                            DatabaseName = databaseNameProp?.GetValue(obj) as string,
                            ConnectionString = connectionStringProp?.GetValue(obj) as string
                        };
                        
                        _logger?.Information($"[ActiveWindowProvider] Extracted connection: Server={connInfo.ServerInstance}, DB={connInfo.DatabaseName}");
                        return connInfo;
                    }
                }
            }

            // 如果不是接口实现，尝试直接提取属性
            var serverProp = type.GetProperty("ServerInstance");
            var dbProp = type.GetProperty("DatabaseName");
            var connStrProp = type.GetProperty("ConnectionString");
            
            if (serverProp != null || dbProp != null || connStrProp != null)
            {
                var connInfo = new ConnectionInfo
                {
                    SourceProvider = ProviderName,
                    ServerInstance = serverProp?.GetValue(obj) as string,
                    DatabaseName = dbProp?.GetValue(obj) as string,
                    ConnectionString = connStrProp?.GetValue(obj) as string
                };
                
                _logger?.Information($"[ActiveWindowProvider] Extracted connection from properties: Server={connInfo.ServerInstance}, DB={connInfo.DatabaseName}");
                return connInfo;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ActiveWindowProvider] Failed to extract connection: {ex.Message}");
            return null;
        }
    }

    // 终极稳定方案：从SSMS内部服务拿当前连接，不需要处理COM文档
    public ConnectionInfo? TryGetCurrentConnectionFromSSMSService()
    {
        // 第一步还是找ServiceCache
        Assembly smssAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a =>
                a.GetName().Name.Equals("Microsoft.SqlServer.SqlTools.VSIntegration") ||
                a.GetName().Name.StartsWith("Microsoft.SqlServer.Management.UI.VSIntegration"));

        if (smssAssembly == null)
        {
            _logger?.Debug("[GetCurrentConnection] Can't find SSMS core assembly");
            return null;
        }

        Type serviceCacheType = smssAssembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
        if (serviceCacheType == null)
        {
            serviceCacheType = smssAssembly.GetType("Microsoft.SqlServer.SqlTools.VSIntegration.ServiceCache");
            if (serviceCacheType == null)
            {
                _logger?.Debug("[GetCurrentConnection] ServiceCache type not found");
                return null;
            }
        }

        // ✅ 你版本的ServiceCache有VSMonitorSelection，用它拿当前选中的节点，一定能拿到
        PropertyInfo monitorSelectionProp = serviceCacheType.GetProperties(
        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
        .FirstOrDefault(p => p.Name.Equals("VSMonitorSelection"));

        if (monitorSelectionProp == null)
        {
            _logger?.Debug("[GetCurrentConnection] VSMonitorSelection not found");
            return null;
        }

        object monitorSelection = monitorSelectionProp.GetValue(null);
        if (monitorSelection == null)
        {
            _logger?.Debug("[GetCurrentConnection] VSMonitorSelection is null");
            return null;
        }

        // 👉 核心修正：IVsMonitorSelection的GetSelectedItems是接口方法，直接转接口调用
        Type vsSelectionInterface = monitorSelection.GetType().GetInterface("IVsMonitorSelection");
        if (vsSelectionInterface == null)
        {
            vsSelectionInterface = monitorSelection.GetType().GetInterfaces().FirstOrDefault(i => i.Name.Contains("IVsMonitorSelection"));
            if (vsSelectionInterface == null)
            {
                var allInterfaces = string.Join(", ", monitorSelection.GetType().GetInterfaces().Select(i => i.Name));
                _logger?.Debug($"All implemented interfaces: {allInterfaces}");
                _logger?.Debug("[GetCurrentConnection] IVsMonitorSelection interface not found");
                return null;
            }
        }

        // 从接口找GetSelectedItems方法
        MethodInfo getSelectedMethod = vsSelectionInterface.GetMethod(
            "GetSelectedItems",
            BindingFlags.Public | BindingFlags.Instance);

        if (getSelectedMethod == null)
        {
            _logger?.Debug("[GetCurrentConnection] GetSelectedItems method not found on interface");
            return null;
        }

        // IVsMonitorSelection.GetSelectedItems 签名: (uint grfGSI, out uint pITemID, out IntPtr ppSite, out object ppSelected)
        object[] parameters = new object[] { 0, 0, IntPtr.Zero, null };
        // COM方法会返回HRESULT，失败的话我们也能打日志
        int result = (int)getSelectedMethod.Invoke(monitorSelection, parameters);
        if (result != 0) // S_OK是0
        {
            _logger?.Debug($"[GetCurrentConnection] GetSelectedItems failed, HRESULT: 0x{result:X8}");
            return null;
        }
        object ppMISel = parameters[3]; // 输出参数存在第四位

        if (ppMISel is not IEnumerable<object> items || !items.Any())
        {
            _logger?.Debug("[GetCurrentConnection] No selected items");
            return null;
        }

        // 遍历选中项找带连接的数据库节点
        foreach (object item in items)
        {
            if (item == null) continue;

            // 从节点拿连接信息，这个是SSMS所有节点的统一接口
            Type itemType = item.GetType();
            PropertyInfo connectionProperty = itemType.GetProperty(
                "Connection",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (connectionProperty == null)
            {
                // 试另一个常用属性名ServerConnection
                connectionProperty = itemType.GetProperty(
                    "ServerConnection",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (connectionProperty == null) continue;
            object connectionInfo = connectionProperty.GetValue(item);
            if (connectionInfo == null) continue;

            // 拿实际的SqlConnection
            Type connType = connectionInfo.GetType();
            PropertyInfo sqlConnProp = connType.GetProperty(
                "SqlConnection",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (sqlConnProp == null) continue;

#if NETCOREAPP
        Microsoft.Data.SqlClient.SqlConnection sqlConn = 
            (Microsoft.Data.SqlClient.SqlConnection)sqlConnProp.GetValue(connectionInfo);
#else
            System.Data.SqlClient.SqlConnection sqlConn =
                (System.Data.SqlClient.SqlConnection)sqlConnProp.GetValue(connectionInfo);
#endif

            // 拿当前数据库，如果节点就是数据库直接拿名字
            PropertyInfo dbProp = itemType.GetProperty(
                "Database",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            string databaseName = dbProp != null
                ? (string)dbProp.GetValue(item)
                : sqlConn.Database;

            _logger?.Debug($"[GetCurrentConnection] Success! {sqlConn.DataSource}/{databaseName}");
            return TryExtractConnectionFromObject(sqlConn);
        }

        // 如果选中节点没找到，再兜底试之前的GlobalConnectionInfo方式，兼容打开查询窗口的情况
        _logger?.Debug("[GetCurrentConnection] Fallback to GlobalConnectionInfo...");
        PropertyInfo globalConnProp = serviceCacheType.GetProperty(
            "GlobalConnectionInfo",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (globalConnProp != null)
        {
            object globalConnectionInfo = globalConnProp.GetValue(null);
            if (globalConnectionInfo != null)
            {
                Type globalConnType = globalConnectionInfo.GetType();
                PropertyInfo currentConnProp = globalConnType.GetProperty(
                    "ActiveConnection",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (currentConnProp == null)
                {
                    currentConnProp = globalConnType.GetProperty(
                        "CurrentConnection",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (currentConnProp != null)
                {
                    object currentConnection = currentConnProp.GetValue(globalConnectionInfo);
                    if (currentConnection != null)
                    {
                        Type connType = currentConnection.GetType();
                        PropertyInfo sqlConnProp = connType.GetProperty(
                            "SqlConnection",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (sqlConnProp != null)
                        {
#if NETCOREAPP
                        Microsoft.Data.SqlClient.SqlConnection sqlConn = 
                            (Microsoft.Data.SqlClient.SqlConnection)sqlConnProp.GetValue(currentConnection);
#else
                            System.Data.SqlClient.SqlConnection sqlConn =
                                (System.Data.SqlClient.SqlConnection)sqlConnProp.GetValue(currentConnection);
#endif
                            _logger?.Debug($"[GetCurrentConnection] Success! {sqlConn.DataSource}/{sqlConn.Database}");
                            return TryExtractConnectionFromObject(sqlConn);
                        }
                    }
                }
            }
        }

        _logger?.Debug("[GetCurrentConnection] No available connection found");
        return null;
    }

    public ConnectionInfo? GetCurrentConnectionFromSSMS()
    {
        // Step 0: 先查找 SSMS 核心程序集，优先 SmartAssemblyLoader（已加载的），其次 AppDomain
        Assembly smssAssembly = SmartAssemblyLoader.GetAssembly("VSIntegration")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a =>
                    a.GetName().Name.Equals("Microsoft.SqlServer.SqlTools.VSIntegration") ||
                    a.GetName().Name.StartsWith("Microsoft.SqlServer.Management.UI.VSIntegration"));

        if (smssAssembly == null)
        {
            _logger?.Debug("[GetCurrentConnection] Can't find SSMS core assembly (VSIntegration)");
            return null;
        }

        Type serviceCacheType = smssAssembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache")
            ?? smssAssembly.GetType("Microsoft.SqlServer.SqlTools.VSIntegration.ServiceCache");
        if (serviceCacheType == null)
        {
            _logger?.Debug("[GetCurrentConnection] ServiceCache type not found in assembly");
            return null;
        }

        // ============================================================
        // 路径1: ExtensibilityModel → DTE → ActiveDocument → Object
        // 使用 dynamic 访问 COM IDispatch 属性（反射无法获取 COM 属性）
        // ============================================================
        {
            PropertyInfo dteProp = serviceCacheType.GetProperty("ExtensibilityModel",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (dteProp == null)
            {
                _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] Property not found on ServiceCache");
            }
            else
            {
                object dte = dteProp.GetValue(null);
                if (dte == null)
                {
                    _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] Value is null (ServiceCache not initialized)");
                }
                else
                {
                    try
                    {
                        // 🎯 核心修复：使用 dynamic 访问 COM 对象的 IDispatch 属性
                        dynamic dteDynamic = dte;

                        // 获取 ActiveDocument（COM 调用，dynamic 正确路由到 IDispatch）
                        dynamic activeDoc = null;
                        try { activeDoc = dteDynamic.ActiveDocument; } catch (Exception ex) { _logger?.Debug($"[GetCurrentConnection][ExtensibilityModel] ActiveDocument failed: {ex.Message}"); }

                        if (activeDoc == null)
                        {
                            _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] No active document");
                        }
                        else
                        {
                            _logger?.Debug($"[GetCurrentConnection][ExtensibilityModel] Got ActiveDocument: {activeDoc.GetType()}");

                            // 通过 dynamic 调用 Document.Object（COM 索引器 get_Object）
                            dynamic documentObject = null;
                            try { documentObject = activeDoc.Object; } catch { }
                            // 如果属性方式不行，尝试 get_Object("") 方式
                            if (documentObject == null)
                            {
                                try { documentObject = activeDoc.get_Object(""); } catch { }
                            }

                            if (documentObject == null)
                            {
                                _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] Document.Object is null (not a query window?)");
                            }
                            else
                            {
                                _logger?.Debug($"[GetCurrentConnection][ExtensibilityModel] Document.Object type: {documentObject.GetType().FullName}");

                                // 从 editor window 对象中拿连接属性
                                dynamic connection = null;
                                try { connection = documentObject.Connection; } catch { }

                                if (connection == null)
                                {
                                    _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] .Connection not found, trying .CurrentConnection...");
                                    try { connection = documentObject.CurrentConnection; } catch { }
                                }
                                if (connection == null)
                                {
                                    _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] .CurrentConnection not found, trying .ServerConnection...");
                                    try { connection = documentObject.ServerConnection; } catch { }
                                }

                                if (connection != null)
                                {
                                    _logger?.Debug($"[GetCurrentConnection][ExtensibilityModel] Got connection object: {connection.GetType().FullName}");
                                    // 尝试提取 SqlConnection
                                    try { dynamic sqlConn = connection.SqlConnection; if (sqlConn != null) { _logger?.Debug($"[GetCurrentConnection][ExtensibilityModel] ✅ SqlConnection: {sqlConn.DataSource}/{sqlConn.Database}"); return TryExtractConnectionFromObject(sqlConn); } } catch { }
                                    // 如果 SqlConnection 不行，直接提取连接信息
                                    var result = TryExtractConnectionFromObject(connection);
                                    if (result != null) return result;
                                }
                                else
                                {
                                    _logger?.Debug("[GetCurrentConnection][ExtensibilityModel] No connection property found on document object");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[GetCurrentConnection][ExtensibilityModel] Dynamic COM access failed: {ex.Message}");
                    }
                }
            }
        }

        // ============================================================
        // 路径2: VSMonitorSelection → 通过 IVsMonitorSelection 接口调用 GetSelectedItems
        // ============================================================
        _logger?.Debug("[GetCurrentConnection][VSMonitorSelection] Trying VSMonitorSelection...");
        {
            PropertyInfo monitorSelectionProp = serviceCacheType.GetProperty("VSMonitorSelection",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (monitorSelectionProp == null)
            {
                _logger?.Debug("[GetCurrentConnection][VSMonitorSelection] Property not found on ServiceCache");
            }
            else
            {
                object monitorSelection = monitorSelectionProp.GetValue(null);
                if (monitorSelection == null)
                {
                    _logger?.Debug("[GetCurrentConnection][VSMonitorSelection] Value is null");
                }
                else
                {
                    // 通过接口查找 GetSelectedItems，确保 COM 方法正确解析
                    Type vsSelectionInterface = monitorSelection.GetType().GetInterface("IVsMonitorSelection");
                    if (vsSelectionInterface == null)
                    {
                        vsSelectionInterface = monitorSelection.GetType()
                            .GetInterfaces().FirstOrDefault(i => i.Name.Contains("IVsMonitorSelection"));
                    }

                    if (vsSelectionInterface == null)
                    {
                        var allInterfaces = string.Join(", ", monitorSelection.GetType().GetInterfaces().Select(i => i.Name));
                        _logger?.Debug($"[GetCurrentConnection][VSMonitorSelection] IVsMonitorSelection interface not found. Available: {allInterfaces}");
                    }
                    else
                    {
                        MethodInfo getSelectedMethod = vsSelectionInterface.GetMethod("GetSelectedItems",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (getSelectedMethod == null)
                        {
                            _logger?.Debug("[GetCurrentConnection][VSMonitorSelection] GetSelectedItems method not found on interface");
                        }
                        else
                        {
                            // IVsMonitorSelection.GetSelectedItems 签名:
                            //   int GetSelectedItems(uint grfGSI, out uint pITemID, out IVsMultiItemSelect ppMISel, out object ppSelected)
                            object[] parameters = new object[] { 0U, 0U, null, null };
                            try
                            {
                                object invokeResult = getSelectedMethod.Invoke(monitorSelection, parameters);
                                int hResult = (int)invokeResult;

                                if (hResult != 0)
                                {
                                    _logger?.Debug($"[GetCurrentConnection][VSMonitorSelection] GetSelectedItems failed, HRESULT: 0x{hResult:X8}");
                                }
                                else
                                {
                                    object ppSelected = parameters[3];
                                    if (ppSelected == null)
                                    {
                                        _logger?.Debug("[GetCurrentConnection][VSMonitorSelection] ppSelected is null (no selection)");
                                    }
                                    else
                                    {
                                        // ppSelected 可能是 Array 或 IEnumerable，尝试转换为列表
                                        List<object> items = new List<object>();
                                        if (ppSelected is Array arr)
                                        {
                                            foreach (var item in arr) items.Add(item);
                                        }
                                        else if (ppSelected is IEnumerable<object> enumItems)
                                        {
                                            items.AddRange(enumItems);
                                        }
                                        else
                                        {
                                            _logger?.Debug($"[GetCurrentConnection][VSMonitorSelection] ppSelected type: {ppSelected.GetType().FullName}, not iterable as objects");
                                        }

                                        if (items.Count == 0)
                                        {
                                            _logger?.Debug("[GetCurrentConnection][VSMonitorSelection] No selected items found");
                                        }
                                        else
                                        {
                                            _logger?.Debug($"[GetCurrentConnection][VSMonitorSelection] Got {items.Count} selected items");
                                            foreach (object item in items)
                                            {
                                                if (item == null) continue;
                                                PropertyInfo connProp = item.GetType()
                                                    .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                                    .FirstOrDefault(p => new[] { "Connection", "ServerConnection", "ConnectionInfo" }.Contains(p.Name));
                                                if (connProp != null)
                                                {
                                                    var connResult = ExtractSqlConnection(connProp.GetValue(item));
                                                    if (connResult != null) return connResult;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"[GetCurrentConnection][VSMonitorSelection] Invoke failed: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        // ============================================================
        // 路径3: GlobalConnectionInfo（静默备选）
        // ============================================================
        _logger?.Debug("[GetCurrentConnection][GlobalConnectionInfo] Trying GlobalConnectionInfo...");
        {
            PropertyInfo globalConnProp = serviceCacheType.GetProperty("GlobalConnectionInfo",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (globalConnProp == null)
            {
                _logger?.Debug("[GetCurrentConnection][GlobalConnectionInfo] Property not found");
            }
            else
            {
                object globalConnInfo = globalConnProp.GetValue(null);
                if (globalConnInfo == null)
                {
                    _logger?.Debug("[GetCurrentConnection][GlobalConnectionInfo] GlobalConnectionInfo is null (SSMS 22 可能已废弃此属性)");
                }
                else
                {
                    PropertyInfo connectionsProp = globalConnInfo.GetType().GetProperty("Connections",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (connectionsProp == null)
                    {
                        _logger?.Debug("[GetCurrentConnection][GlobalConnectionInfo] Connections property not found");
                    }
                    else
                    {
                        object connListObj = connectionsProp.GetValue(globalConnInfo);
                        if (connListObj == null)
                        {
                            _logger?.Debug("[GetCurrentConnection][GlobalConnectionInfo] Connections is null");
                        }
                        else if (connListObj is IEnumerable<object> connList && connList.Any())
                        {
                            _logger?.Debug($"[GetCurrentConnection][GlobalConnectionInfo] Found {connList.Count()} connections");
                            foreach (object conn in connList)
                            {
                                var result = ExtractSqlConnection(conn);
                                if (result != null) return result;
                            }
                        }
                        else
                        {
                            _logger?.Debug("[GetCurrentConnection][GlobalConnectionInfo] Connections is empty");
                        }
                    }
                }
            }
        }

        // ============================================================
        // 全部路径失败 → 输出诊断汇总
        // ============================================================
        _logger?.Warning("[GetCurrentConnection] ❌ 所有回退路径均未能获取到连接。可能原因：");
        _logger?.Warning("[GetCurrentConnection]   1. 确保已打开一个已连接数据库的查询窗口");
        _logger?.Warning("[GetCurrentConnection]   2. ExtensibilityModel/DTE 路径失败（COM 对象未暴露所需属性）");
        _logger?.Warning("[GetCurrentConnection]   3. VSMonitorSelection 路径失败（无选中项或 COM 接口调用异常）");
        _logger?.Warning("[GetCurrentConnection]   4. GlobalConnectionInfo 为 null（SSMS 22 可能已废弃此缓存）");
        return null;
    }

    // 统一提取SqlConnection的方法，不用重复写
    private ConnectionInfo? ExtractSqlConnection(object connectionObject)
    {
        if (connectionObject == null) return null;

        // 如果本身就是SqlConnection直接返回
        if (connectionObject is System.Data.SqlClient.SqlConnection sysConn
            && sysConn.State == System.Data.ConnectionState.Open)
        {
            _logger?.Debug($"[GetCurrentConnection] Success! {sysConn.DataSource}/{sysConn.Database}");
#if NETCOREAPP
        return new YourConnectionInfo
        {
            DataSource = sysConn.DataSource,
            Database = sysConn.Database,
            ConnectionString = sysConn.ConnectionString,
            SqlConnection = new Microsoft.Data.SqlClient.SqlConnection(sysConn.ConnectionString)
        };
#else
            return TryExtractConnectionFromObject(sysConn);
#endif
        }
        if (connectionObject is Microsoft.Data.SqlClient.SqlConnection mdConn
            && mdConn.State == System.Data.ConnectionState.Open)
        {
            _logger?.Debug($"[GetCurrentConnection] Success! {mdConn.DataSource}/{mdConn.Database}");
            return TryExtractConnectionFromObject(mdConn);
        }

        // 否则找里面的SqlConnection属性
        Type connType = connectionObject.GetType();
        PropertyInfo sqlConnProp = connType.GetProperty("SqlConnection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (sqlConnProp != null)
        {
            var sqlConn = sqlConnProp.GetValue(connectionObject);
            if (sqlConn == null) return null;

            if (sqlConn is System.Data.SqlClient.SqlConnection sysConn2
                && sysConn2.State == System.Data.ConnectionState.Open)
            {
                _logger?.Debug($"[GetCurrentConnection] Success! {sysConn2.DataSource}/{sysConn2.Database}");
#if NETCOREAPP
            return new YourConnectionInfo
            {
                DataSource = sysConn.DataSource,
                Database = sysConn.Database,
                ConnectionString = sysConn.ConnectionString,
                SqlConnection = new Microsoft.Data.SqlClient.SqlConnection(sysConn.ConnectionString)
            };
#else
                return TryExtractConnectionFromObject(sysConn2);
#endif
            }
            if (sqlConn is Microsoft.Data.SqlClient.SqlConnection mdConn2
                && mdConn2.State == System.Data.ConnectionState.Open)
            {
                _logger?.Debug($"[GetCurrentConnection] Success! {mdConn2.DataSource}/{mdConn2.Database}");
                return TryExtractConnectionFromObject(mdConn2);
            }
        }

        // 有些版本叫ServerConnection，本身就是连接对象
        PropertyInfo serverConnProp = connType.GetProperty("ServerConnection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (serverConnProp != null)
        {
            var serverConn = serverConnProp.GetValue(connectionObject);
            if (serverConn == null) return null;
            return ExtractSqlConnection(serverConn);
        }

        return null;
    }
}
