using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// 使用SSMS官方API获取连接信息
/// 参考：IConnectionManager, ISqlConnectionService
/// </summary>
public class SsmsOfficialConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "SSMS Official API Provider";
    public int Priority => -10; // 最高优先级

    public bool IsAvailable => SmartAssemblyLoader.GetDetectedVersion() >= SSMSVersion.V18;

    public SsmsOfficialConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!IsAvailable)
        {
            _logger?.Debug("[SsmsOfficialProvider] Not available (SSMS version < 18)");
            return null;
        }

        try
        {
            _logger?.Debug("[SsmsOfficialProvider] Attempting to get connection using SSMS official APIs...");

            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // 策略1: 使用 IConnectionManager 服务
            var connection = TryGetFromConnectionManager();
            if (connection != null)
            {
                _logger?.Information("[SsmsOfficialProvider] ✅ Got connection from IConnectionManager");
                return connection;
            }

            // 策略2: 使用 ISqlConnectionService 服务
            connection = TryGetFromSqlConnectionService();
            if (connection != null)
            {
                _logger?.Information("[SsmsOfficialProvider] ✅ Got connection from ISqlConnectionService");
                return connection;
            }

            // 策略3: 从 ServiceCache 获取连接服务
            connection = TryGetFromServiceCache();
            if (connection != null)
            {
                _logger?.Information("[SsmsOfficialProvider] ✅ Got connection from ServiceCache");
                return connection;
            }

            _logger?.Warning("[SsmsOfficialProvider] ❌ All official API strategies failed");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SsmsOfficialProvider] Exception occurred");
            return null;
        }
    }

    /// <summary>
    /// 从 IConnectionManager 获取连接
    /// 这是SSMS官方推荐的获取当前活动连接的方式
    /// </summary>
    private ConnectionInfo? TryGetFromConnectionManager()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SsmsOfficialProvider] Trying IConnectionManager...");

            // 尝试多种可能的IConnectionManager类型定义位置
            var connectionManagerTypes = new[]
            {
                "Microsoft.SqlServer.Tools.Connection.IConnectionManager, Microsoft.SqlServer.Tools.Connection",
                "Microsoft.SqlServer.Management.Connection.IConnectionManager, Microsoft.SqlServer.Management.Connection",
                "Microsoft.SqlServer.ConnectionDlg.IConnectionManager, Microsoft.SqlServer.ConnectionDlg",
                "Microsoft.SqlServer.Management.UI.Connection.IConnectionManager, Microsoft.SqlServer.Management.UI.Connection"
            };

            foreach (var typeName in connectionManagerTypes)
            {
                try
                {
                    // 使用SmartAssemblyLoader搜索类型（支持从已加载程序集查找）
                    var managerType = SmartAssemblyLoader.GetType(typeName);

                    if (managerType == null) continue;

                    _logger?.Debug($"[SsmsOfficialProvider] Found type: {managerType.FullName}");

                    var service = _serviceProvider.GetService(managerType);
                    if (service == null)
                    {
                        _logger?.Debug($"[SsmsOfficialProvider] Service not available for {managerType.Name}");
                        continue;
                    }

                    _logger?.Debug($"[SsmsOfficialProvider] Got service: {service.GetType().FullName}");

                    // 探索服务的属性和方法
                    var serviceType = service.GetType();
                    
                    // 尝试获取 ActiveConnection 属性
                    var activeConnProp = serviceType.GetProperty("ActiveConnection");
                    if (activeConnProp != null)
                    {
                        var activeConn = activeConnProp.GetValue(service);
                        if (activeConn != null)
                        {
                            _logger?.Debug($"[SsmsOfficialProvider] ActiveConnection: {activeConn.GetType().FullName}");
                            return ExtractConnectionFromObject(activeConn);
                        }
                    }

                    // 尝试获取 OpenedConnections 属性（所有已打开的连接）
                    var openedConnsProp = serviceType.GetProperty("OpenedConnections");
                    if (openedConnsProp != null)
                    {
                        var openedConns = openedConnsProp.GetValue(service) as System.Collections.IEnumerable;
                        if (openedConns != null)
                        {
                            foreach (var conn in openedConns)
                            {
                                var connInfo = ExtractConnectionFromObject(conn);
                                if (connInfo != null)
                                {
                                    _logger?.Information("[SsmsOfficialProvider] Got connection from OpenedConnections");
                                    return connInfo;
                                }
                            }
                        }
                    }

                    // 尝试其他可能的属性
                    var props = serviceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in props)
                    {
                        if (prop.Name.Contains("Connection") || prop.Name.Contains("Current"))
                        {
                            try
                            {
                                var value = prop.GetValue(service);
                                if (value != null)
                                {
                                    _logger?.Debug($"[SsmsOfficialProvider] Found property {prop.Name}: {value.GetType().Name}");
                                    var connInfo = ExtractConnectionFromObject(value);
                                    if (connInfo != null) return connInfo;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"[SsmsOfficialProvider] Property {prop.Name} failed: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[SsmsOfficialProvider] Type {typeName} failed: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SsmsOfficialProvider] IConnectionManager failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 ISqlConnectionService 获取连接
    /// </summary>
    private ConnectionInfo? TryGetFromSqlConnectionService()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SsmsOfficialProvider] Trying ISqlConnectionService...");

            var serviceTypes = new[]
            {
                "Microsoft.SqlServer.Tools.Connection.ISqlConnectionService, Microsoft.SqlServer.Tools.Connection",
                "Microsoft.SqlServer.Management.Connection.ISqlConnectionService, Microsoft.SqlServer.Management.Connection"
            };

            foreach (var typeName in serviceTypes)
            {
                try
                {
                    var serviceType = Type.GetType(typeName);
                    if (serviceType == null) continue;

                    var service = _serviceProvider.GetService(serviceType);
                    if (service == null) continue;

                    _logger?.Debug($"[SsmsOfficialProvider] Got ISqlConnectionService: {service.GetType().FullName}");

                    // 探索方法
                    var methods = service.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var method in methods)
                    {
                        if (method.Name.Contains("Connection") && method.GetParameters().Length == 0)
                        {
                            try
                            {
                                var result = method.Invoke(service, null);
                                if (result != null)
                                {
                                    return ExtractConnectionFromObject(result);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"[SsmsOfficialProvider] Method {method.Name} failed: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[SsmsOfficialProvider] {typeName} failed: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SsmsOfficialProvider] ISqlConnectionService failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 ServiceCache 获取连接服务
    /// </summary>
    private ConnectionInfo? TryGetFromServiceCache()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SsmsOfficialProvider] Trying ServiceCache...");

            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[SsmsOfficialProvider] ServiceCache type not found");
                return null;
            }

            // 尝试获取连接相关服务
            var serviceProps = new[] { "ConnectionManager", "SqlConnectionService", "ConnectionService" };

            foreach (var propName in serviceProps)
            {
                try
                {
                    var prop = serviceCacheType.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
                    if (prop == null) continue;

                    var service = prop.GetValue(null);
                    if (service == null) continue;

                    _logger?.Debug($"[SsmsOfficialProvider] Got {propName}: {service.GetType().FullName}");

                    // 探索获取连接的方法
                    var methods = service.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var method in methods)
                    {
                        if ((method.Name.Contains("Active") || method.Name.Contains("Current")) &&
                            method.Name.Contains("Connection") &&
                            method.GetParameters().Length == 0)
                        {
                            try
                            {
                                var connection = method.Invoke(service, null);
                                if (connection != null)
                                {
                                    return ExtractConnectionFromObject(connection);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"[SsmsOfficialProvider] Method {method.Name} failed: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[SsmsOfficialProvider] Property {propName} failed: {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SsmsOfficialProvider] ServiceCache failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从对象中提取连接信息
    /// </summary>
    private ConnectionInfo? ExtractConnectionFromObject(object obj)
    {
        try
        {
            var type = obj.GetType();
            _logger?.Debug($"[SsmsOfficialProvider] Extracting from: {type.FullName}");

            // 检查是否是连接包装类型
            var connectionInfoProp = type.GetProperty("ConnectionInfo");
            if (connectionInfoProp != null)
            {
                var innerConnection = connectionInfoProp.GetValue(obj);
                if (innerConnection != null)
                {
                    return ExtractConnectionFromObject(innerConnection);
                }
            }

            var connectionInfo = new ConnectionInfo
            {
                SourceProvider = ProviderName
            };

            // 提取标准属性
            var serverProp = type.GetProperty("ServerInstance") ?? type.GetProperty("ServerName");
            if (serverProp != null)
            {
                connectionInfo.ServerInstance = serverProp.GetValue(obj) as string;
            }

            var dbProp = type.GetProperty("DatabaseName") ?? type.GetProperty("Database");
            if (dbProp != null)
            {
                connectionInfo.DatabaseName = dbProp.GetValue(obj) as string;
            }

            var connStrProp = type.GetProperty("ConnectionString");
            if (connStrProp != null)
            {
                connectionInfo.ConnectionString = connStrProp.GetValue(obj) as string;
            }

            var loginProp = type.GetProperty("Login") ?? type.GetProperty("UserName");
            if (loginProp != null)
            {
                connectionInfo.UserName = loginProp.GetValue(obj) as string;
            }

            _logger?.Debug($"[SsmsOfficialProvider] Extracted: Server={connectionInfo.ServerInstance}, DB={connectionInfo.DatabaseName}");

            if (!string.IsNullOrEmpty(connectionInfo.ServerInstance) ||
                !string.IsNullOrEmpty(connectionInfo.ConnectionString))
            {
                return connectionInfo;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SsmsOfficialProvider] Failed to extract connection info");
            return null;
        }
    }
}
