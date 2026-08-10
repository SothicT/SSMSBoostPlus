using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS 22 专用连接提供者
/// 针对SSMS 22的新API结构进行优化
/// </summary>
public class SSMS22ConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "SSMS 22 Optimized Provider";
    public int Priority => 0; // 最高优先级，仅在SSMS 22中使用

    public bool IsAvailable => SmartAssemblyLoader.GetDetectedVersion() >= SSMSVersion.V22;

    public SSMS22ConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!IsAvailable)
        {
            _logger?.Debug("[SSMS22Provider] Not available (SSMS version < 22)");
            return null;
        }

        try
        {
            _logger?.Debug("[SSMS22Provider] Attempting to get connection using SSMS 22 APIs...");

            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // 策略1: 从新的ObjectExplorer.dll获取
            var connection = TryGetFromObjectExplorerDll();
            if (connection != null)
            {
                _logger?.Information("[SSMS22Provider] ✅ Got connection from ObjectExplorer.dll");
                return connection;
            }

            // 策略2: 从ConnectionDlg.dll获取
            connection = TryGetFromConnectionDlgDll();
            if (connection != null)
            {
                _logger?.Information("[SSMS22Provider] ✅ Got connection from ConnectionDlg.dll");
                return connection;
            }

            // 策略3: 从活动文档获取（新API）
            connection = TryGetFromActiveDocument();
            if (connection != null)
            {
                _logger?.Information("[SSMS22Provider] ✅ Got connection from ActiveDocument");
                return connection;
            }

            // 策略4: 探索式获取
            connection = TryExploreConnection();
            if (connection != null)
            {
                _logger?.Information("[SSMS22Provider] ✅ Got connection via exploration");
                return connection;
            }

            _logger?.Warning("[SSMS22Provider] ❌ All SSMS 22 strategies failed");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SSMS22Provider] Exception occurred");
            return null;
        }
    }

    /// <summary>
    /// 从新的ObjectExplorer.dll获取连接
    /// </summary>
    private ConnectionInfo? TryGetFromObjectExplorerDll()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SSMS22Provider] Trying ObjectExplorer.dll...");

            var assembly = SmartAssemblyLoader.GetAssembly("ObjectExplorer");
            if (assembly == null)
            {
                _logger?.Debug("[SSMS22Provider] ObjectExplorer assembly not loaded");
                return null;
            }

            // 查找所有服务类型
            var serviceTypes = assembly.GetTypes()
                .Where(t => t.IsPublic && t.Name.Contains("Service"))
                .ToList();

            foreach (var serviceType in serviceTypes)
            {
                try
                {
                    var service = _serviceProvider.GetService(serviceType);
                    if (service == null) continue;

                    _logger?.Debug($"[SSMS22Provider] Got service: {serviceType.Name}");

                    // 探索服务的属性
                    var props = service.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in props)
                    {
                        if (prop.Name.Contains("Connection") || prop.Name.Contains("SelectedNode"))
                        {
                            var value = prop.GetValue(service);
                            if (value != null)
                            {
                                _logger?.Debug($"[SSMS22Provider] Found {prop.Name}: {value.GetType().Name}");
                                
                                // 尝试提取连接信息
                                var connInfo = ExtractConnectionInfo(value);
                                if (connInfo != null) return connInfo;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[SSMS22Provider] Service {serviceType.Name} failed: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Provider] ObjectExplorer.dll failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从ConnectionDlg.dll获取连接
    /// </summary>
    private ConnectionInfo? TryGetFromConnectionDlgDll()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SSMS22Provider] Trying ConnectionDlg.dll...");

            var serviceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService",
                "ConnectionDlg");

            if (serviceType == null)
            {
                _logger?.Debug("[SSMS22Provider] ConnectionDialogService type not found");
                return null;
            }

            // 尝试获取Instance属性
            var instanceProp = serviceType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);

            if (instanceProp == null)
            {
                _logger?.Debug("[SSMS22Provider] Instance property not found");
                return null;
            }

            var instance = instanceProp.GetValue(null);
            if (instance == null)
            {
                _logger?.Debug("[SSMS22Provider] Instance is null");
                return null;
            }

            _logger?.Debug($"[SSMS22Provider] Got ConnectionDialogService instance: {instance.GetType().FullName}");

            // 探索所有属性
            var props = instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                _logger?.Debug($"[SSMS22Provider]   {prop.Name}: {prop.PropertyType.Name}");
                
                if (prop.Name.Contains("Connection") || prop.Name.Contains("Current"))
                {
                    try
                    {
                        var value = prop.GetValue(instance);
                        if (value != null)
                        {
                            _logger?.Information($"[SSMS22Provider] Found connection: {prop.Name}");
                            return ExtractConnectionInfo(value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[SSMS22Provider] Failed to get {prop.Name}: {ex.Message}");
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Provider] ConnectionDlg.dll failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从活动文档获取连接
    /// </summary>
    private ConnectionInfo? TryGetFromActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SSMS22Provider] Trying active document...");

            // 获取DTE
            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            if (dteType == null) return null;

            var dte = _serviceProvider.GetService(dteType);
            if (dte == null) return null;

            // 获取ActiveDocument
            var activeDocProp = dteType.GetProperty("ActiveDocument");
            var activeDoc = activeDocProp?.GetValue(dte);
            
            if (activeDoc == null)
            {
                _logger?.Debug("[SSMS22Provider] No active document");
                return null;
            }

            // 尝试获取文档的连接信息
            var docType = activeDoc.GetType();
            var props = docType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                if (prop.Name.Contains("Connection"))
                {
                    var value = prop.GetValue(activeDoc);
                    if (value != null)
                    {
                        return ExtractConnectionInfo(value);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Provider] ActiveDocument failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 探索式获取连接
    /// </summary>
    private ConnectionInfo? TryExploreConnection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SSMS22Provider] Exploring all possible connection sources...");

            // 使用ApiExplorer进行深入探索
            SsmsApiExplorer.ExploreConnectionPaths(_serviceProvider, _logger);

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Provider] Exploration failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 提取连接信息
    /// </summary>
    private ConnectionInfo? ExtractConnectionInfo(object connection)
    {
        try
        {
            var connType = connection.GetType();
            _logger?.Debug($"[SSMS22Provider] Extracting from: {connType.FullName}");

            var connectionInfo = new ConnectionInfo
            {
                SourceProvider = ProviderName
            };

            // ServerInstance
            var serverInstanceProp = connType.GetProperty("ServerInstance");
            if (serverInstanceProp != null)
            {
                connectionInfo.ServerInstance = serverInstanceProp.GetValue(connection) as string;
            }

            // DatabaseName
            var databaseNameProp = connType.GetProperty("DatabaseName");
            if (databaseNameProp != null)
            {
                connectionInfo.DatabaseName = databaseNameProp.GetValue(connection) as string;
            }

            // ServerVersion
            var serverVersionProp = connType.GetProperty("ServerVersion");
            if (serverVersionProp != null)
            {
                var version = serverVersionProp.GetValue(connection);
                connectionInfo.ServerVersion = version?.ToString();
            }

            // Login
            var loginProp = connType.GetProperty("Login");
            if (loginProp != null)
            {
                connectionInfo.UserName = loginProp.GetValue(connection) as string;
            }

            // ConnectionString
            var connectionStringProp = connType.GetProperty("ConnectionString");
            if (connectionStringProp != null)
            {
                connectionInfo.ConnectionString = connectionStringProp.GetValue(connection) as string;
            }

            // IntegratedSecurity
            var integratedSecurityProp = connType.GetProperty("IntegratedSecurity");
            if (integratedSecurityProp != null)
            {
                var useIntegratedSecurity = (bool)integratedSecurityProp.GetValue(connection);
                connectionInfo.AuthenticationType = useIntegratedSecurity
                    ? AuthenticationType.Windows
                    : AuthenticationType.SqlServer;
            }

            _logger?.Debug($"[SSMS22Provider] Extracted: Server={connectionInfo.ServerInstance}, DB={connectionInfo.DatabaseName}");

            // 验证是否有有效信息
            if (!string.IsNullOrEmpty(connectionInfo.ServerInstance) || 
                !string.IsNullOrEmpty(connectionInfo.ConnectionString))
            {
                return connectionInfo;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SSMS22Provider] Failed to extract connection info");
            return null;
        }
    }
}
