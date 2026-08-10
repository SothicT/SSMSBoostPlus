using System;
using Microsoft.SqlServer.Management.Common;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS连接服务辅助类
/// 使用反射方式获取ISqlConnectionService，适配SSMS多版本
/// 支持SSMS 18/19/22等不同版本的命名空间变化
/// </summary>
public class SsmsConnectionServiceHelper
{
    private readonly ILoggerService? _logger;

    public SsmsConnectionServiceHelper(ILoggerService? logger = null)
    {
        _logger = logger;
        SsmsReflectionHelper.Initialize(logger);
    }

    /// <summary>
    /// 获取SSMS当前活动的数据库连接
    /// 使用反射方式获取ISqlConnectionService服务
    /// </summary>
    /// <param name="serviceProvider">服务提供者（通常是Package实例）</param>
    /// <returns>当前活动连接信息，如果无活动连接则返回null</returns>
    public ConnectionInfo? GetCurrentActiveConnection(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[SsmsConnectionServiceHelper] Getting current active connection...");

            // 使用反射获取ISqlConnectionService服务类型
            var sqlConnectionServiceType = SsmsReflectionHelper.GetSqlConnectionServiceType();
            if (sqlConnectionServiceType == null)
            {
                _logger?.Warning("[SsmsConnectionServiceHelper] ISqlConnectionService type not found in any assembly");
                return null;
            }

            // 从SSMS服务容器获取连接服务
            var sqlConnectionService = serviceProvider.GetService(sqlConnectionServiceType);
            
            if (sqlConnectionService == null)
            {
                _logger?.Warning("[SsmsConnectionServiceHelper] ISqlConnectionService not available");
                return null;
            }

            _logger?.Debug($"[SsmsConnectionServiceHelper] Got ISqlConnectionService: {sqlConnectionService.GetType().FullName}");

            // 使用反射获取ActiveConnection属性
            var activeConnection = SsmsReflectionHelper.GetPropertyValue(sqlConnectionService, "ActiveConnection");
            
            if (activeConnection == null)
            {
                _logger?.Debug("[SsmsConnectionServiceHelper] No active connection");
                return null;
            }

            _logger?.Debug($"[SsmsConnectionServiceHelper] ActiveConnection type: {activeConnection.GetType().FullName}");

            // 使用反射提取ConnectionInfo
            var connectionInfo = SsmsReflectionHelper.ExtractConnectionInfo(activeConnection);

            if (connectionInfo != null)
            {
                var serverInstance = SsmsReflectionHelper.GetServerInstance(connectionInfo);
                _logger?.Information($"[SsmsConnectionServiceHelper] ✅ Got connection: Server={serverInstance}, DB={connectionInfo.DatabaseName}");
            }

            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SsmsConnectionServiceHelper] Failed to get current active connection");
            return null;
        }
    }
}
