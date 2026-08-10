using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.Management.Common;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// 数据库连接服务 - 使用反射方式获取连接，适配SSMS多版本
/// 支持SSMS 18/19/22等不同版本的命名空间变化
/// </summary>
public class DatabaseConnectionHelper
{
    private readonly ILoggerService? _logger;

    public DatabaseConnectionHelper(ILoggerService? logger = null)
    {
        _logger = logger;
        SsmsReflectionHelper.Initialize(logger);
    }

    /// <summary>
    /// 获取SSMS当前活动的数据库连接
    /// 使用反射方式获取IConnectionManager服务
    /// </summary>
    public ConnectionInfo? GetCurrentActiveConnection(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[DatabaseConnectionHelper] Getting current active connection...");

            // 使用反射获取IConnectionManager服务类型
            var connectionManagerType = SsmsReflectionHelper.GetConnectionManagerType();
            if (connectionManagerType == null)
            {
                _logger?.Warning("[DatabaseConnectionHelper] IConnectionManager type not found in any assembly");
                return null;
            }

            // 获取服务实例
            var connectionManager = serviceProvider.GetService(connectionManagerType);
            if (connectionManager == null)
            {
                _logger?.Warning("[DatabaseConnectionHelper] IConnectionManager service not available");
                return null;
            }

            _logger?.Debug($"[DatabaseConnectionHelper] Got IConnectionManager: {connectionManager.GetType().FullName}");

            // 使用反射获取ActiveConnection属性
            var activeConnection = SsmsReflectionHelper.GetPropertyValue(connectionManager, "ActiveConnection");
            if (activeConnection == null)
            {
                _logger?.Debug("[DatabaseConnectionHelper] No active connection");
                return null;
            }

            _logger?.Debug($"[DatabaseConnectionHelper] ActiveConnection type: {activeConnection.GetType().FullName}");

            // 使用反射提取ConnectionInfo
            var connectionInfo = SsmsReflectionHelper.ExtractConnectionInfo(activeConnection);
            
            if (connectionInfo != null)
            {
                var serverInstance = SsmsReflectionHelper.GetServerInstance(connectionInfo);
                _logger?.Information($"[DatabaseConnectionHelper] ✅ Got connection: Server={serverInstance}, DB={connectionInfo.DatabaseName}");
            }
            
            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[DatabaseConnectionHelper] Failed to get current active connection");
            return null;
        }
    }

    /// <summary>
    /// 获取所有已打开的连接
    /// 使用反射方式遍历OpenedConnections
    /// </summary>
    public IEnumerable<ConnectionInfo> GetAllOpenedConnections(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[DatabaseConnectionHelper] Getting all opened connections...");

            // 使用反射获取IConnectionManager服务类型
            var connectionManagerType = SsmsReflectionHelper.GetConnectionManagerType();
            if (connectionManagerType == null)
            {
                _logger?.Warning("[DatabaseConnectionHelper] IConnectionManager type not found");
                return Enumerable.Empty<ConnectionInfo>();
            }

            // 获取服务实例
            var connectionManager = serviceProvider.GetService(connectionManagerType);
            if (connectionManager == null)
            {
                _logger?.Warning("[DatabaseConnectionHelper] IConnectionManager service not available");
                return Enumerable.Empty<ConnectionInfo>();
            }

            var connections = new List<ConnectionInfo>();
            
            // 使用反射获取OpenedConnections属性
            var openedConnections = SsmsReflectionHelper.GetPropertyValue<IEnumerable<object>>(connectionManager, "OpenedConnections");
            
            if (openedConnections != null)
            {
                foreach (var connection in openedConnections)
                {
                    var sqlInfo = SsmsReflectionHelper.ExtractConnectionInfo(connection);
                    if (sqlInfo != null)
                    {
                        connections.Add(sqlInfo);
                        var serverInstance = SsmsReflectionHelper.GetServerInstance(sqlInfo);
                        _logger?.Debug($"[DatabaseConnectionHelper] Found connection: Server={serverInstance}, DB={sqlInfo.DatabaseName}");
                    }
                }
            }

            _logger?.Information($"[DatabaseConnectionHelper] Found {connections.Count} opened connections");
            return connections;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[DatabaseConnectionHelper] Failed to get opened connections");
            return Enumerable.Empty<ConnectionInfo>();
        }
    }
}
