using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using System;
using System.Threading.Tasks;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS官方API连接提供者
/// 使用IConnectionManager官方API获取连接
/// </summary>
public class SsmsOfficialApiProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;
    private readonly DatabaseConnectionHelper _connectionHelper;

    public string ProviderName => "SSMS Official API Provider";
    public int Priority => -10; // 最高优先级

    public bool IsAvailable => SmartAssemblyLoader.GetDetectedVersion() >= SSMSVersion.V18;

    public SsmsOfficialApiProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
        _connectionHelper = new DatabaseConnectionHelper(logger);
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!IsAvailable)
        {
            _logger?.Debug("[SsmsOfficialApiProvider] Not available (SSMS version < 18)");
            return null;
        }

        try
        {
            _logger?.Debug("[SsmsOfficialApiProvider] Attempting to get connection using SSMS official APIs...");

            // 使用官方API获取连接
            var connectionInfo = _connectionHelper.GetCurrentActiveConnection(_serviceProvider);

            if (connectionInfo == null)
            {
                _logger?.Debug("[SsmsOfficialApiProvider] No active connection found");
                return null;
            }

            // 转换为ConnectionInfo
            //var connectionInfo = ConvertToConnectionInfo(sqlConnectionInfo);
            
            _logger?.Information($"[SsmsOfficialApiProvider] ✅ Got connection: Server={connectionInfo.ServerInstance}, DB={connectionInfo.DatabaseName}");
            
            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SsmsOfficialApiProvider] Exception occurred");
            return null;
        }
    }

    /// <summary>
    /// 将SqlConnectionInfo转换为ConnectionInfo
    /// </summary>
    private ConnectionInfo ConvertToConnectionInfo(SqlConnectionInfo sqlConnectionInfo)
    {
        var connectionInfo = new ConnectionInfo
        {
            SourceProvider = ProviderName,
            ServerInstance = SsmsReflectionHelper.GetServerInstance(sqlConnectionInfo),
            DatabaseName = sqlConnectionInfo.DatabaseName,
            ConnectionString = sqlConnectionInfo.ConnectionString,
            UserName = sqlConnectionInfo.UserName,
            ServerVersion = sqlConnectionInfo.ServerVersion?.ToString(),
            AuthenticationType = sqlConnectionInfo.UseIntegratedSecurity 
                ? AuthenticationType.Windows 
                : AuthenticationType.SqlServer
        };

        return connectionInfo;
    }
}
