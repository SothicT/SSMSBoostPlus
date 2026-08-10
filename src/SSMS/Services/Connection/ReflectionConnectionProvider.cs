using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// Connection provider using reflection to access SSMS internal types.
/// This is a fallback strategy when official SDK interfaces are not available.
/// Preserves existing connection retrieval logic from SsmsEditorService.
/// </summary>
public class ReflectionConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "SSMS Internal Reflection (Fallback)";
    public int Priority => 10; // Lower priority, used as fallback

    public bool IsAvailable => true; // Always available as fallback

    public ReflectionConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger?.Debug("[ReflectionProvider] Attempting to get connection via reflection...");

            // Ensure assemblies are loaded first!
            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // Multi-layer fallback strategy (from original SsmsEditorService)
            var connectionInfo = TryGetFromServiceCache()
                ?? TryGetFromConnectionDialog()
                ?? TryGetFromObjectExplorer();

            if (connectionInfo != null)
            {
                _logger?.Information($"[ReflectionProvider] ✅ Successfully retrieved connection via reflection");
            }
            else
            {
                _logger?.Warning("[ReflectionProvider] ❌ Failed to get connection via reflection");
            }

            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ReflectionProvider] Exception occurred");
            return null;
        }
    }

    /// <summary>
    /// Try to get connection from ServiceCache.ConnectionDialog.CurrentConnection.
    /// This is the most reliable method for query editor windows.
    /// </summary>
    private ConnectionInfo? TryGetFromServiceCache()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ReflectionProvider] Trying ServiceCache.ConnectionDialog...");

            // Try to get ConnectionDialog from ServiceCache
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[ReflectionProvider] ServiceCache type not found");
                return null;
            }

            // Get ConnectionDialog property
            var connectionDialogProp = serviceCacheType.GetProperty("ConnectionDialog",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (connectionDialogProp == null)
            {
                _logger?.Debug("[ReflectionProvider] ConnectionDialog property not found");
                return null;
            }

            var connectionDialog = connectionDialogProp.GetValue(null);
            if (connectionDialog == null)
            {
                _logger?.Debug("[ReflectionProvider] ConnectionDialog instance is null");
                return null;
            }

            // Get CurrentConnection property
            var currentConnectionProp = connectionDialog.GetType().GetProperty("CurrentConnection");
            if (currentConnectionProp == null)
            {
                _logger?.Debug("[ReflectionProvider] CurrentConnection property not found");
                return null;
            }

            var currentConnection = currentConnectionProp.GetValue(connectionDialog);
            if (currentConnection == null)
            {
                _logger?.Debug("[ReflectionProvider] CurrentConnection is null");
                return null;
            }

            return ExtractConnectionInfo(currentConnection);
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ReflectionProvider] ServiceCache failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Try to get connection from ConnectionDialogService.Instance.
    /// </summary>
    private ConnectionInfo? TryGetFromConnectionDialog()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ReflectionProvider] Trying ConnectionDialogService...");

            var connectionDialogServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService",
                "ConnectionDlg");

            if (connectionDialogServiceType == null)
            {
                _logger?.Debug("[ReflectionProvider] ConnectionDialogService type not found");
                return null;
            }

            // Try to get Instance property (singleton pattern)
            var instanceProp = connectionDialogServiceType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (instanceProp == null)
            {
                _logger?.Debug("[ReflectionProvider] Instance property not found");
                return null;
            }

            var serviceInstance = instanceProp.GetValue(null);
            if (serviceInstance == null)
            {
                _logger?.Debug("[ReflectionProvider] Service instance is null");
                return null;
            }

            // Explore the service for connection info
            // This may require further investigation based on actual SSMS version
            _logger?.Debug($"[ReflectionProvider] Service instance type: {serviceInstance.GetType().FullName}");
            
            // 探索服务实例的所有属性，寻找连接信息
            var props = serviceInstance.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var prop in props)
            {
                _logger?.Debug($"[ReflectionProvider]   Property: {prop.Name} ({prop.PropertyType.Name})");
                
                // 尝试获取包含 "Connection" 或 "Current" 的属性
                if (prop.Name.Contains("Connection") || prop.Name.Contains("Current"))
                {
                    try
                    {
                        var value = prop.GetValue(serviceInstance);
                        if (value != null)
                        {
                            _logger?.Information($"[ReflectionProvider] Found connection property: {prop.Name}");
                            return ExtractConnectionInfo(value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[ReflectionProvider] Failed to get {prop.Name}: {ex.Message}");
                    }
                }
            }
            
            // For now, return null and let other methods handle it
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ReflectionProvider] ConnectionDialogService failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Try to get connection from Object Explorer selected node.
    /// </summary>
    private ConnectionInfo? TryGetFromObjectExplorer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[ReflectionProvider] Trying ObjectExplorer...");

            var objectExplorerServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService",
                "ObjectExplorer");

            if (objectExplorerServiceType == null)
            {
                _logger?.Debug("[ReflectionProvider] ObjectExplorerService type not found");
                return null;
            }

            var objectExplorerService = _serviceProvider.GetService(objectExplorerServiceType);
            if (objectExplorerService == null)
            {
                _logger?.Debug("[ReflectionProvider] ObjectExplorerService not available");
                return null;
            }

            // Try different methods to get selected node or current connection
            // This is based on the diagnostic logs from TROUBLESHOOTING.md
            var type = objectExplorerService.GetType();

            // Method 1: Try GetSelectedNodes
            var getSelectedNodesMethod = type.GetMethod("GetSelectedNodes");
            if (getSelectedNodesMethod != null)
            {
                var parameters = new object[] { 0, null };
                getSelectedNodesMethod.Invoke(objectExplorerService, parameters);

                var count = (int)parameters[0];
                var nodes = parameters[1] as Array;

                if (count > 0 && nodes != null && nodes.Length > 0)
                {
                    var selectedNode = nodes.GetValue(0);
                    var connectionProp = selectedNode.GetType().GetProperty("Connection");
                    if (connectionProp != null)
                    {
                        var connection = connectionProp.GetValue(selectedNode);
                        if (connection != null)
                        {
                            return ExtractConnectionInfo(connection);
                        }
                    }
                }
            }

            // Method 2: Try other properties
            // Based on diagnostic logs, explore available properties
            var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop.Name.Contains("Connection") || prop.Name.Contains("Selected"))
                {
                    try
                    {
                        var value = prop.GetValue(objectExplorerService);
                        if (value != null)
                        {
                            _logger?.Debug($"[ReflectionProvider] Found property: {prop.Name} = {value.GetType().FullName}");
                        }
                    }
                    catch { }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ReflectionProvider] ObjectExplorer failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract connection information from a connection object (reflection version).
    /// Handles ServerConnection and similar types.
    /// </summary>
    private ConnectionInfo? ExtractConnectionInfo(object connection)
    {
        try
        {
            var connType = connection.GetType();
            _logger?.Debug($"[ReflectionProvider] Extracting connection info from: {connType.FullName}");

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

            // ConnectionTimeout
            var timeoutProp = connType.GetProperty("ConnectionTimeout");
            if (timeoutProp != null)
            {
                connectionInfo.ConnectionTimeout = (int)timeoutProp.GetValue(connection);
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

            // ConnectionString
            var connectionStringProp = connType.GetProperty("ConnectionString");
            if (connectionStringProp != null)
            {
                connectionInfo.ConnectionString = connectionStringProp.GetValue(connection) as string;
            }

            _logger?.Debug($"[ReflectionProvider] Extracted: Server={connectionInfo.ServerInstance}, DB={connectionInfo.DatabaseName}");

            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ReflectionProvider] Failed to extract connection info");
            return null;
        }
    }
}
