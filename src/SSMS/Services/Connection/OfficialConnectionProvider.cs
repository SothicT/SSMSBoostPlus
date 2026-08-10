using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// Connection provider using official SSMS SDK interfaces.
/// Based on SQL Prompt's approach: IScriptEditorService and IObjectExplorerService.
/// This is the preferred method as it uses documented, stable APIs.
/// </summary>
public class OfficialConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "Official SSMS SDK Interfaces";
    public int Priority => 1; // Highest priority

    public bool IsAvailable
    {
        get
        {
            try
            {
                // Check if SSMS SDK types are available in current environment
                // We'll check at runtime when GetConnectionInfoAsync is called
                // This is just a quick check for service availability
                return true; // Will be validated during actual call
            }
            catch
            {
                return false;
            }
        }
    }

    public OfficialConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger?.Debug("[OfficialProvider] Attempting to get connection via official SSMS SDK interfaces...");

            // Ensure assemblies are loaded first!
            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // Strategy 1: Try to get connection from active script editor
            var connection = TryGetFromScriptEditor();
            if (connection != null)
            {
                _logger?.Information("[OfficialProvider] ✅ Successfully retrieved connection from ScriptEditor");
                return connection;
            }

            // Strategy 2: Try to get connection from Object Explorer
            connection = TryGetFromObjectExplorer();
            if (connection != null)
            {
                _logger?.Information("[OfficialProvider] ✅ Successfully retrieved connection from ObjectExplorer");
                return connection;
            }

            _logger?.Warning("[OfficialProvider] ❌ Failed to get connection from both ScriptEditor and ObjectExplorer");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[OfficialProvider] Exception occurred while getting connection");
            return null;
        }
    }

    /// <summary>
    /// Try to get connection from the currently active script editor window.
    /// Uses IScriptEditorService (SQL Prompt's primary approach).
    /// </summary>
    private ConnectionInfo? TryGetFromScriptEditor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[OfficialProvider] Trying IScriptEditorService...");

            // Use SmartAssemblyLoader to get the type
            var scriptEditorServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.IScriptEditorService",
                "SqlStudioExplorer");

            if (scriptEditorServiceType == null)
            {
                _logger?.Debug("[OfficialProvider] IScriptEditorService type not found");
                return null;
            }

            var scriptEditorService = _serviceProvider.GetService(scriptEditorServiceType);
            if (scriptEditorService == null)
            {
                _logger?.Debug("[OfficialProvider] IScriptEditorService service not available");
                return null;
            }

            _logger?.Debug($"[OfficialProvider] Got IScriptEditorService: {scriptEditorService.GetType().FullName}");

            // Get active script editor
            // Method: GetActiveScriptEditor()
            var getActiveEditorMethod = scriptEditorServiceType.GetMethod("GetActiveScriptEditor");
            if (getActiveEditorMethod == null)
            {
                _logger?.Debug("[OfficialProvider] GetActiveScriptEditor method not found");
                return null;
            }

            var activeEditor = getActiveEditorMethod.Invoke(scriptEditorService, null);
            if (activeEditor == null)
            {
                _logger?.Debug("[OfficialProvider] No active script editor");
                return null;
            }

            _logger?.Debug($"[OfficialProvider] Active editor type: {activeEditor.GetType().FullName}");

            // Get ConnectionInfo property from active editor
            var connectionInfoProp = activeEditor.GetType().GetProperty("ConnectionInfo");
            if (connectionInfoProp == null)
            {
                _logger?.Debug("[OfficialProvider] ConnectionInfo property not found on editor");
                return null;
            }

            var connectionInfo = connectionInfoProp.GetValue(activeEditor);
            if (connectionInfo == null)
            {
                _logger?.Debug("[OfficialProvider] ConnectionInfo is null");
                return null;
            }

            // Extract connection details from ServerConnection object
            return ExtractConnectionInfo(connectionInfo);
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[OfficialProvider] ScriptEditor failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Try to get connection from Object Explorer selected node.
    /// Uses IObjectExplorerService (SQL Prompt's secondary approach).
    /// </summary>
    private ConnectionInfo? TryGetFromObjectExplorer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[OfficialProvider] Trying IObjectExplorerService...");

            // Get IObjectExplorerService from SSMS
            // Type: Microsoft.SqlServer.Management.SqlStudio.Explorer.IObjectExplorerService
            var objectExplorerServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.SqlStudio.Explorer.IObjectExplorerService",
                "ObjectExplorer");

            if (objectExplorerServiceType == null)
            {
                _logger?.Debug("[OfficialProvider] IObjectExplorerService type not found");
                return null;
            }

            var objectExplorerService = _serviceProvider.GetService(objectExplorerServiceType);
            if (objectExplorerService == null)
            {
                _logger?.Debug("[OfficialProvider] IObjectExplorerService service not available");
                return null;
            }

            _logger?.Debug($"[OfficialProvider] Got IObjectExplorerService: {objectExplorerService.GetType().FullName}");

            // Get selected nodes
            // Method: GetSelectedNodes(out int count, out INodeInformation[] nodes)
            var getSelectedNodesMethod = objectExplorerServiceType.GetMethod("GetSelectedNodes");
            if (getSelectedNodesMethod == null)
            {
                _logger?.Debug("[OfficialProvider] GetSelectedNodes method not found");
                return null;
            }

            var parameters = new object[] { 0, null };
            getSelectedNodesMethod.Invoke(objectExplorerService, parameters);

            var count = (int)parameters[0];
            var nodes = parameters[1] as Array;

            if (count == 0 || nodes == null || nodes.Length == 0)
            {
                _logger?.Debug("[OfficialProvider] No nodes selected in Object Explorer");
                return null;
            }

            var selectedNode = nodes.GetValue(0);
            _logger?.Debug($"[OfficialProvider] Selected node type: {selectedNode.GetType().FullName}");

            // Get Connection property from node
            var connectionProp = selectedNode.GetType().GetProperty("Connection");
            if (connectionProp == null)
            {
                _logger?.Debug("[OfficialProvider] Connection property not found on node");
                return null;
            }

            var connection = connectionProp.GetValue(selectedNode);
            if (connection == null)
            {
                _logger?.Debug("[OfficialProvider] Connection is null");
                return null;
            }

            // Extract connection details
            return ExtractConnectionInfo(connection);
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[OfficialProvider] ObjectExplorer failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract connection information from ServerConnection object.
    /// ServerConnection is from Microsoft.SqlServer.Management.Common namespace.
    /// </summary>
    private ConnectionInfo? ExtractConnectionInfo(object serverConnection)
    {
        try
        {
            var connType = serverConnection.GetType();
            _logger?.Debug($"[OfficialProvider] Extracting connection info from: {connType.FullName}");

            var connectionInfo = new ConnectionInfo
            {
                SourceProvider = ProviderName
            };

            // ServerInstance
            var serverInstanceProp = connType.GetProperty("ServerInstance");
            if (serverInstanceProp != null)
            {
                connectionInfo.ServerInstance = serverInstanceProp.GetValue(serverConnection) as string;
            }

            // DatabaseName
            var databaseNameProp = connType.GetProperty("DatabaseName");
            if (databaseNameProp != null)
            {
                connectionInfo.DatabaseName = databaseNameProp.GetValue(serverConnection) as string;
            }

            // ServerVersion
            var serverVersionProp = connType.GetProperty("ServerVersion");
            if (serverVersionProp != null)
            {
                var version = serverVersionProp.GetValue(serverConnection);
                connectionInfo.ServerVersion = version?.ToString();
            }

            // Login (username)
            var loginProp = connType.GetProperty("Login");
            if (loginProp != null)
            {
                connectionInfo.UserName = loginProp.GetValue(serverConnection) as string;
            }

            // ConnectionTimeout
            var timeoutProp = connType.GetProperty("ConnectionTimeout");
            if (timeoutProp != null)
            {
                connectionInfo.ConnectionTimeout = (int)timeoutProp.GetValue(serverConnection);
            }

            // Authentication type
            var integratedSecurityProp = connType.GetProperty("IntegratedSecurity");
            if (integratedSecurityProp != null)
            {
                var useIntegratedSecurity = (bool)integratedSecurityProp.GetValue(serverConnection);
                connectionInfo.AuthenticationType = useIntegratedSecurity 
                    ? AuthenticationType.Windows 
                    : AuthenticationType.SqlServer;
            }

            // ConnectionString (if available)
            var connectionStringProp = connType.GetProperty("ConnectionString");
            if (connectionStringProp != null)
            {
                connectionInfo.ConnectionString = connectionStringProp.GetValue(serverConnection) as string;
            }

            _logger?.Debug($"[OfficialProvider] Extracted: Server={connectionInfo.ServerInstance}, DB={connectionInfo.DatabaseName}, Auth={connectionInfo.AuthenticationType}");

            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[OfficialProvider] Failed to extract connection info");
            return null;
        }
    }
}
