using System;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Common;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS连接服务提供者
/// 使用ISqlConnectionService官方API获取连接
/// </summary>
public class SsmsConnectionServiceProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;
    private readonly SsmsConnectionServiceHelper _connectionHelper;

    public string ProviderName => "SSMS Connection Service Provider";
    public int Priority => -10; // 最高优先级

    public bool IsAvailable => SmartAssemblyLoader.GetDetectedVersion() >= SSMSVersion.V18;

    public SsmsConnectionServiceProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
        _connectionHelper = new SsmsConnectionServiceHelper(logger);
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!IsAvailable)
        {
            _logger?.Debug("[SsmsConnectionServiceProvider] Not available (SSMS version < 18)");
            return null;
        }

        try
        {
            _logger?.Debug("[SsmsConnectionServiceProvider] Attempting to get connection using ISqlConnectionService...");

            // 使用官方API获取连接
            var connectionInfo = _connectionHelper.GetCurrentActiveConnection(_serviceProvider);

            if (connectionInfo == null)
            {
                _logger?.Debug("[SsmsConnectionServiceProvider] No active connection found");
                return null;
            }

            // 转换为ConnectionInfo
            //var connectionInfo = ConvertToConnectionInfo(sqlConnectionInfo);

            _logger?.Information($"[SsmsConnectionServiceProvider] ✅ Got connection: Server={connectionInfo.ServerInstance}, DB={connectionInfo.DatabaseName}");

            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SsmsConnectionServiceProvider] Exception occurred");
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
