using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// 通过 SSMS22 内部 API 获取当前数据库连接信息。
/// 
/// 策略说明：
/// 1. ServiceCache.ScriptFactory 在 SSMS22 中 getter 内部抛 NullReferenceException（框架bug），已弃用
/// 2. ServiceCache.ServiceProvider 可能可用，用于 IObjectExplorerService 路径
/// 3. 直接枚举 AppDomain 中已加载的 SSMS 程序集，寻找连接信息
/// 
/// 参考 AxialSqlTools 的做法：只在用户交互时尝试获取，异常静默处理。
/// </summary>
public class ScriptFactoryConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "ScriptFactory Connection Provider";
    public int Priority => -15; // 最高优先级
    public bool IsAvailable => true;

    public ScriptFactoryConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger?.Debug("[ScriptFactoryProvider] Attempting to get connection...");

            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // 策略A：从 ObjectExplorer 获取（最可靠，用户需在对象资源管理器中选中节点）
            // 通过 ServiceCache.ServiceProvider 获取 IObjectExplorerService
            var connInfo = GetFromObjectExplorerViaServiceCache();
            if (connInfo != null)
            {
                _logger?.Information("[ScriptFactoryProvider] ✅ Got connection from ObjectExplorer (ServiceCache)");
                return connInfo;
            }

            // 策略B：尝试从 DTE 活动文档获取连接信息
            connInfo = GetFromDTEActiveDocument();
            if (connInfo != null)
            {
                _logger?.Information("[ScriptFactoryProvider] ✅ Got connection from DTE ActiveDocument");
                return connInfo;
            }

            // 策略C：从 AppDomain 中寻找已加载的 ScriptFactory 实例
            connInfo = GetFromAppDomainScriptFactory();
            if (connInfo != null)
            {
                _logger?.Information("[ScriptFactoryProvider] ✅ Got connection from AppDomain ScriptFactory");
                return connInfo;
            }

            _logger?.Warning("[ScriptFactoryProvider] ❌ All strategies failed");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ScriptFactoryProvider] Exception occurred");
            return null;
        }
    }

    /// <summary>
    /// 通过 ServiceCache.ServiceProvider → IObjectExplorerService 获取连接。
    /// 用户需要在对象资源管理器中选中一个连接的节点。
    /// </summary>
    private ConnectionInfo? GetFromObjectExplorerViaServiceCache()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ScriptFactoryProvider] Strategy A: ObjectExplorer via ServiceCache...");

            // 获取 ServiceCache 类型
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] ServiceCache type not found");
                return null;
            }

            // 获取 ServiceProvider 静态属性
            var spProp = serviceCacheType.GetProperty("ServiceProvider",
                BindingFlags.Public | BindingFlags.Static);

            if (spProp == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] ServiceCache.ServiceProvider property not found");
                return null;
            }

            object? serviceProvider = null;
            try
            {
                serviceProvider = spProp.GetValue(null);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[ScriptFactoryProvider] ServiceCache.ServiceProvider access failed: {ex.Message}");
                return null;
            }

            if (serviceProvider == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] ServiceCache.ServiceProvider is null");
                return null;
            }

            _logger?.Debug($"[ScriptFactoryProvider] ServiceProvider type: {serviceProvider.GetType().FullName}");

            // 获取 IObjectExplorerService 类型
            var oeServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService",
                "VSIntegration");

            if (oeServiceType == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] IObjectExplorerService type not found");
                return null;
            }

            // 调用 serviceProvider.GetService(oeServiceType)
            var getServiceMethod = serviceProvider.GetType().GetMethod("GetService", new[] { typeof(Type) });
            if (getServiceMethod == null) return null;

            var oeService = getServiceMethod.Invoke(serviceProvider, new object[] { oeServiceType });
            if (oeService == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] IObjectExplorerService service not available");
                return null;
            }

            _logger?.Debug($"[ScriptFactoryProvider] ObjectExplorerService type: {oeService.GetType().FullName}");

            // 调用 GetSelectedNodes
            var oeType = oeService.GetType();
            var getSelectedNodesMethod = oeType.GetMethod("GetSelectedNodes");
            if (getSelectedNodesMethod == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] GetSelectedNodes method not found");
                return null;
            }

            var parameters = new object[] { 0, null! };
            getSelectedNodesMethod.Invoke(oeService, parameters);

            var nodeCount = (int)parameters[0];
            var nodes = (Array?)parameters[1];

            if (nodeCount <= 0 || nodes == null || nodes.Length == 0)
            {
                _logger?.Debug("[ScriptFactoryProvider] No selected node in ObjectExplorer");
                return null;
            }

            var selectedNode = nodes.GetValue(0);
            if (selectedNode == null) return null;

            var nodeType = selectedNode.GetType();

            // 获取 Connection 属性
            var connProp = nodeType.GetProperty("Connection",
                BindingFlags.Public | BindingFlags.Instance);
            if (connProp == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] SelectedNode.Connection property not found");
                return null;
            }

            var connection = connProp.GetValue(selectedNode);
            if (connection == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] SelectedNode.Connection is null");
                return null;
            }

            // 获取 Context 属性提取数据库名
            string databaseName = "master";
            var contextProp = nodeType.GetProperty("Context");
            if (contextProp != null)
            {
                var context = contextProp.GetValue(selectedNode) as string;
                if (!string.IsNullOrWhiteSpace(context))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(context, @"Database\[@Name='(.*?)'\]");
                    if (match.Success) databaseName = match.Groups[1].Value;
                }
            }

            // 从 Connection 对象提取信息
            var connObjType = connection.GetType();
            var serverProp = connObjType.GetProperty("ServerName");
            var userProp = connObjType.GetProperty("UserName");
            var passProp = connObjType.GetProperty("Password");

            var serverName = serverProp?.GetValue(connection) as string ?? "";
            var userName = userProp?.GetValue(connection) as string ?? "";
            var password = passProp?.GetValue(connection) as string ?? "";

            _logger?.Information($"[ScriptFactoryProvider] ✅ ObjectExplorer connection: Server={serverName}, DB={databaseName}");

            return new ConnectionInfo
            {
                SourceProvider = ProviderName,
                ServerInstance = serverName,
                DatabaseName = databaseName,
                UserName = userName,
                AuthenticationType = string.IsNullOrWhiteSpace(password)
                    ? AuthenticationType.Windows
                    : AuthenticationType.SqlServer,
                ConnectionString = BuildConnectionString(serverName, databaseName, userName, password, "", "")
            };
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ScriptFactoryProvider] Strategy A failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 DTE 的活动文档中获取连接信息。
    /// 利用 DTE COM 对象枚举打开的 SQL 查询窗口。
    /// </summary>
    private ConnectionInfo? GetFromDTEActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ScriptFactoryProvider] Strategy B: DTE ActiveDocument...");

            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            if (dteType == null) return null;

            var dte = _serviceProvider.GetService(dteType);
            if (dte == null) return null;

            // 枚举所有打开的文档窗口
            dynamic dteDynamic = dte;
            dynamic documents = null;
            try { documents = dteDynamic.Documents; } catch { }

            if (documents == null)
            {
                _logger?.Debug("[ScriptFactoryProvider] No documents available");
                return null;
            }

            int docCount = 0;
            try { docCount = documents.Count; } catch { }

            if (docCount == 0)
            {
                _logger?.Debug("[ScriptFactoryProvider] No open documents");
                return null;
            }

            _logger?.Debug($"[ScriptFactoryProvider] Found {docCount} open documents");

            // 遍历所有文档，找 SQL 查询窗口
            foreach (dynamic doc in documents)
            {
                try
                {
                    string docName = "";
                    try { docName = doc.Name ?? ""; } catch { }

                    if (!docName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                        !docName.Contains(".sql"))
                        continue;

                    _logger?.Debug($"[ScriptFactoryProvider] Found SQL document: {docName}");

                    // 尝试获取 ProjectItem.Object 获取连接信息
                    try
                    {
                        dynamic projectItem = doc.ProjectItem;
                        if (projectItem != null)
                        {
                            dynamic docObject = projectItem.Object;
                            if (docObject != null)
                            {
                                var conn = TryExtractConnectionFromDTEObject(docObject);
                                if (conn != null) return conn;
                            }
                        }
                    }
                    catch { }
                }
                catch { }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ScriptFactoryProvider] Strategy B failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 AppDomain 中已加载的程序集查找 ScriptFactory 并尝试调用。
    /// 这种方式的区别：不是通过 SmartAssemblyLoader 主动加载，而是查找 SSMS22 已经加载的实例。
    /// </summary>
    private ConnectionInfo? GetFromAppDomainScriptFactory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ScriptFactoryProvider] Strategy C: AppDomain ScriptFactory...");

            // 查找所有已加载的 SSMS 相关程序集
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName == null) continue;

                // 只搜索 SSMS 相关的程序集
                if (!asmName.Contains("SqlServer") && !asmName.Contains("SqlTools") &&
                    !asmName.Contains("VSIntegration") && !asmName.Contains("SQLEditors"))
                    continue;

                _logger?.Debug($"[ScriptFactoryProvider] Checking assembly: {asmName}");

                // 查找 ServiceCache 类型
                var serviceCacheType = asm.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                if (serviceCacheType == null) continue;

                _logger?.Debug($"[ScriptFactoryProvider] Found ServiceCache in {asmName}");

                // 尝试获取 ScriptFactory
                var sfProp = serviceCacheType.GetProperty("ScriptFactory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (sfProp == null) continue;

                try
                {
                    var scriptFactory = sfProp.GetValue(null);
                    if (scriptFactory == null)
                    {
                        _logger?.Debug("[ScriptFactoryProvider] ScriptFactory is null even from AppDomain");
                        continue;
                    }

                    // 查找 CurrentlyActiveWndConnectionInfo 属性
                    var sfType = scriptFactory.GetType();
                    var connInfoProp = sfType.GetProperty("CurrentlyActiveWndConnectionInfo",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (connInfoProp == null)
                    {
                        _logger?.Debug("[ScriptFactoryProvider] CurrentlyActiveWndConnectionInfo not found on ScriptFactory");
                        continue;
                    }

                    var activeConn = connInfoProp.GetValue(scriptFactory);
                    if (activeConn == null)
                    {
                        _logger?.Debug("[ScriptFactoryProvider] CurrentlyActiveWndConnectionInfo is null");
                        continue;
                    }

                    // 提取 UIConnectionInfo
                    var activeConnType = activeConn.GetType();
                    var uiConnProp = activeConnType.GetProperty("UIConnectionInfo",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (uiConnProp == null) continue;

                    var uiConnInfo = uiConnProp.GetValue(activeConn);
                    if (uiConnInfo == null) continue;

                    return ExtractFromUIConnectionInfo(uiConnInfo);
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[ScriptFactoryProvider] AppDomain ScriptFactory access failed: {ex.Message}");
                    continue;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ScriptFactoryProvider] Strategy C failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 DTE COM 对象中提取连接信息。
    /// </summary>
    private ConnectionInfo? TryExtractConnectionFromDTEObject(object docObject)
    {
        try
        {
            var type = docObject.GetType();
            _logger?.Debug($"[ScriptFactoryProvider] DTE Object type: {type.FullName}");

            // 尝试获取 ConnectionString 或类似属性
            var connStrProp = type.GetProperty("ConnectionString");
            if (connStrProp != null)
            {
                var connStr = connStrProp.GetValue(docObject) as string;
                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    return new ConnectionInfo
                    {
                        SourceProvider = ProviderName,
                        ConnectionString = connStr
                    };
                }
            }

            // 尝试各种可能的连接属性
            foreach (var propName in new[] { "Server", "ServerName", "DataSource", "SqlConnection" })
            {
                var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(docObject);
                    _logger?.Debug($"[ScriptFactoryProvider]   {propName} = {val}");
                    
                    if (val != null && propName == "ServerName")
                    {
                        return new ConnectionInfo
                        {
                            SourceProvider = ProviderName,
                            ServerInstance = val.ToString()
                        };
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 UIConnectionInfo 对象中提取连接信息。
    /// </summary>
    private ConnectionInfo? ExtractFromUIConnectionInfo(object uiConnInfo)
    {
        try
        {
            var type = uiConnInfo.GetType();

            var serverProp = type.GetProperty("ServerName");
            var serverName = serverProp?.GetValue(uiConnInfo) as string ?? "";

            var userProp = type.GetProperty("UserName");
            var userName = userProp?.GetValue(uiConnInfo) as string ?? "";

            var passProp = type.GetProperty("Password");
            var password = passProp?.GetValue(uiConnInfo) as string ?? "";

            string databaseName = "master";
            var advOptionsProp = type.GetProperty("AdvancedOptions");
            if (advOptionsProp != null)
            {
                var advOptions = advOptionsProp.GetValue(uiConnInfo);
                if (advOptions != null)
                {
                    var advType = advOptions.GetType();
                    var getMethod = advType.GetMethod("Get", new[] { typeof(string) });
                    if (getMethod != null)
                    {
                        var db = getMethod.Invoke(advOptions, new object[] { "DATABASE" }) as string;
                        if (!string.IsNullOrWhiteSpace(db)) databaseName = db;
                    }
                }
            }

            _logger?.Information($"[ScriptFactoryProvider] Connection extracted: Server={serverName}, DB={databaseName}");

            return new ConnectionInfo
            {
                SourceProvider = ProviderName,
                ServerInstance = serverName,
                DatabaseName = databaseName,
                UserName = userName,
                AuthenticationType = string.IsNullOrWhiteSpace(password)
                    ? AuthenticationType.Windows
                    : AuthenticationType.SqlServer,
                ConnectionString = BuildConnectionString(serverName, databaseName, userName, password, "", "")
            };
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ScriptFactoryProvider] ExtractFromUIConnectionInfo failed: {ex.Message}");
            return null;
        }
    }

    private static string BuildConnectionString(string server, string database, string userName, string password,
        string encryptConnection, string trustServerCert)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            ApplicationName = "SSMSBoostPlus"
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            builder.IntegratedSecurity = false;
            builder.UserID = userName;
            builder.Password = password;
        }
        else if (!string.IsNullOrWhiteSpace(userName))
        {
            builder.UserID = userName;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        if (encryptConnection.Equals("true", StringComparison.OrdinalIgnoreCase))
            builder.Encrypt = true;
        if (trustServerCert.Equals("true", StringComparison.OrdinalIgnoreCase))
            builder.TrustServerCertificate = true;

        return builder.ToString();
    }
}