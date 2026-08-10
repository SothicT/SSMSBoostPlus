using Microsoft.SqlServer.Management.Common;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNet.SignalR.Infrastructure;


namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS连接服务反射工具类
/// 使用反射方式获取连接服务，避免对特定命名空间的编译时依赖
/// 支持SSMS 18/19/22等多个版本的命名空间变化
/// 
/// 重要：此类不直接引用任何 SSMS 内部类型（如 SqlConnectionInfo），
/// 所有操作都通过反射完成，避免程序集加载失败。
/// </summary>
public static class SsmsReflectionHelper
{
    private static ILoggerService? _logger;
    
    // 缓存已找到的类型，避免重复查找
    private static readonly Dictionary<string, Type> _typeCache = new();
    
    // SSMS不同版本的可能命名空间
    private static readonly string[] ConnectionManagerTypeNames = new[]
    {
        // SSMS 22+
        "Microsoft.SqlServer.Tools.Connection.IConnectionManager",
        // SSMS 19
        "Microsoft.SqlTools.Connection.IConnectionManager",
        // SSMS 18及更早版本
        "Microsoft.SqlServer.SqlTools.Interfaces.Connection.IConnectionManager"
    };
    
    private static readonly string[] SqlConnectionServiceTypeNames = new[]
    {
        // SSMS 22+
        "Microsoft.SqlServer.Tools.Connection.ISqlConnectionService",
        // SSMS 19
        "Microsoft.SqlTools.Connection.ISqlConnectionService",
        // SSMS 18及更早版本
        "Microsoft.SqlServer.SqlTools.Interfaces.Connection.ISqlConnectionService"
    };
    
    private static readonly string[] ConnectedDatabaseTypeNames = new[]
    {
        // SSMS 22+
        "Microsoft.SqlServer.Tools.Connection.IConnectedDatabase",
        // SSMS 19
        "Microsoft.SqlTools.Connection.IConnectedDatabase",
        // SSMS 18及更早版本
        "Microsoft.SqlServer.SqlTools.Interfaces.Connection.IConnectedDatabase"
    };
    
    // SqlConnectionInfo 类型名称（用于反射查找）
    private static readonly string[] SqlConnectionInfoTypeNames = new[]
    {
        "Microsoft.SqlServer.Management.Common.SqlConnectionInfo",
        "Microsoft.SqlServer.ConnectionInfo.SqlConnectionInfo"
    };
    
    /// <summary>
    /// 初始化日志服务
    /// </summary>
    public static void Initialize(ILoggerService? logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// 在已加载的程序集中查找类型
    /// </summary>
    /// <param name="possibleTypeNames">可能的类型全名列表</param>
    /// <returns>找到的类型，如果未找到则返回null</returns>
    public static Type? FindType(params string[] possibleTypeNames)
    {
        foreach (var typeName in possibleTypeNames)
        {
            // 先从缓存查找
            if (_typeCache.TryGetValue(typeName, out var cachedType))
            {
                return cachedType;
            }
            
            // 在已加载的程序集中查找
            var type = Type.GetType(typeName, false);
            if (type != null)
            {
                _typeCache[typeName] = type;
                _logger?.Debug($"[SsmsReflectionHelper] Found type in Type.GetType: {typeName}");
                return type;
            }
            
            // 遍历已加载的程序集查找
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(typeName, false);
                    if (type != null)
                    {
                        _typeCache[typeName] = type;
                        _logger?.Debug($"[SsmsReflectionHelper] Found type in assembly {assembly.GetName().Name}: {typeName}");
                        return type;
                    }
                }
                catch
                {
                    // 忽略无法访问的程序集
                }
            }
        }
        
        _logger?.Warning($"[SsmsReflectionHelper] Type not found: {string.Join(", ", possibleTypeNames)}");
        return null;
    }
    
    /// <summary>
    /// 获取IConnectionManager服务类型
    /// </summary>
    public static Type? GetConnectionManagerType()
    {
        return FindType(ConnectionManagerTypeNames);
    }
    
    /// <summary>
    /// 获取ISqlConnectionService服务类型
    /// </summary>
    public static Type? GetSqlConnectionServiceType()
    {
        return FindType(SqlConnectionServiceTypeNames);
    }
    
    /// <summary>
    /// 获取IConnectedDatabase类型
    /// </summary>
    public static Type? GetConnectedDatabaseType()
    {
        return FindType(ConnectedDatabaseTypeNames);
    }
    
    /// <summary>
    /// 通过反射获取服务
    /// </summary>
    /// <param name="serviceProvider">服务提供者</param>
    /// <param name="possibleTypeNames">可能的服务类型全名列表</param>
    /// <returns>服务实例，如果未找到则返回null</returns>
    public static object? GetService(IServiceProvider serviceProvider, params string[] possibleTypeNames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        
        var serviceType = FindType(possibleTypeNames);
        if (serviceType == null)
        {
            return null;
        }
        
        try
        {
            var service = serviceProvider.GetService(serviceType);
            var connectionManager = serviceProvider.GetService(typeof(IConnectionManager)) as IConnectionManager;

            _logger?.Debug($"[SsmsReflectionHelper] Got service: {serviceType.FullName}, instance: {service?.GetType().FullName ?? "null"}");
            return service;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, $"[SsmsReflectionHelper] Failed to get service: {serviceType.FullName}");
            return null;
        }
    }
    
    /// <summary>
    /// 通过反射获取属性值
    /// </summary>
    /// <typeparam name="T">属性值类型</typeparam>
    /// <param name="obj">对象实例</param>
    /// <param name="propertyName">属性名</param>
    /// <returns>属性值，如果未找到则返回default</returns>
    public static T? GetPropertyValue<T>(object? obj, string propertyName)
    {
        if (obj == null)
        {
            return default;
        }
        
        try
        {
            var type = obj.GetType();
            var property = type.GetProperty(propertyName);
            
            if (property == null)
            {
                _logger?.Debug($"[SsmsReflectionHelper] Property not found: {propertyName} on type {type.FullName}");
                return default;
            }
            
            var value = property.GetValue(obj);
            return value is T tValue ? tValue : default;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, $"[SsmsReflectionHelper] Failed to get property: {propertyName}");
            return default;
        }
    }
    
    /// <summary>
    /// 通过反射获取属性值（对象类型）
    /// </summary>
    /// <param name="obj">对象实例</param>
    /// <param name="propertyName">属性名</param>
    /// <returns>属性值，如果未找到则返回null</returns>
    public static object? GetPropertyValue(object? obj, string propertyName)
    {
        return GetPropertyValue<object?>(obj, propertyName);
    }
    
    /// <summary>
    /// 通过反射调用方法
    /// </summary>
    /// <param name="obj">对象实例</param>
    /// <param name="methodName">方法名</param>
    /// <param name="args">方法参数</param>
    /// <returns>方法返回值，如果调用失败则返回null</returns>
    public static object? InvokeMethod(object? obj, string methodName, params object[] args)
    {
        if (obj == null)
        {
            return null;
        }
        
        try
        {
            var type = obj.GetType();
            var argTypes = args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
            var method = type.GetMethod(methodName, argTypes);
            
            if (method == null)
            {
                _logger?.Debug($"[SsmsReflectionHelper] Method not found: {methodName} on type {type.FullName}");
                return null;
            }
            
            return method.Invoke(obj, args);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, $"[SsmsReflectionHelper] Failed to invoke method: {methodName}");
            return null;
        }
    }
    
    /// <summary>
    /// 从连接对象中提取服务器实例名（反射方式）
    /// 尝试 ServerInstance、ServerName 属性或从 ConnectionString 解析
    /// </summary>
    /// <param name="connectionObj">连接信息对象（任意类型）</param>
    /// <returns>服务器实例名</returns>
    public static string? GetServerInstance(object? connectionObj)
    {
        if (connectionObj == null)
        {
            return null;
        }
        
        // 尝试ServerInstance属性
        var serverInstance = GetPropertyValue<string>(connectionObj, "ServerInstance");
        if (!string.IsNullOrEmpty(serverInstance))
        {
            return serverInstance;
        }
        
        // 尝试ServerName属性
        var serverName = GetPropertyValue<string>(connectionObj, "ServerName");
        if (!string.IsNullOrEmpty(serverName))
        {
            return serverName;
        }
        
        // 尝试从ConnectionString提取
        var connectionString = GetPropertyValue<string>(connectionObj, "ConnectionString");
        if (!string.IsNullOrEmpty(connectionString))
        {
            // 简单解析Data Source或Server
            var parts = connectionString.Split(';');
            foreach (var part in parts)
            {
                var kv = part.Split('=');
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim().ToLower();
                    if (key == "data source" || key == "server" || key == "address" || key == "addr" || key == "network address")
                    {
                        return kv[1].Trim();
                    }
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// 从连接对象中提取 ConnectionInfo（完全使用反射）
    /// </summary>
    /// <param name="connectionObj">连接对象</param>
    /// <param name="providerName">提供者名称</param>
    /// <returns>ConnectionInfo，如果转换失败则返回null</returns>
    public static ConnectionInfo? ExtractConnectionInfo(object? connectionObj, string? providerName = null)
    {
        if (connectionObj == null)
        {
            return null;
        }
        
        try
        {
            // 尝试获取ConnectionInfo属性
            var innerConnectionInfo = GetPropertyValue(connectionObj, "ConnectionInfo");
            var targetObj = innerConnectionInfo ?? connectionObj;
            
            var type = targetObj.GetType();
            _logger?.Debug($"[SsmsReflectionHelper] Extracting connection info from: {type.FullName}");
            
            // 使用反射构建 ConnectionInfo
            var connectionInfo = new ConnectionInfo
            {
                SourceProvider = providerName,
                ServerInstance = GetServerInstance(targetObj),
                DatabaseName = GetPropertyValue<string>(targetObj, "DatabaseName"),
                ConnectionString = GetPropertyValue<string>(targetObj, "ConnectionString"),
                UserName = GetPropertyValue<string>(targetObj, "Login") ?? GetPropertyValue<string>(targetObj, "UserName"),
                ServerVersion = GetPropertyValue<string>(targetObj, "ServerVersion"),
            };
            
            // 认证类型
            var integratedSecurity = GetPropertyValue<bool?>(targetObj, "IntegratedSecurity") 
                ?? GetPropertyValue<bool?>(targetObj, "UseIntegratedSecurity");
            if (integratedSecurity == true)
            {
                connectionInfo.AuthenticationType = AuthenticationType.Windows;
            }
            else
            {
                // 检查是否是 AAD 认证
                var authType = GetPropertyValue<string>(targetObj, "Authentication");
                if (!string.IsNullOrEmpty(authType) && authType.IndexOf("Active", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    connectionInfo.AuthenticationType = AuthenticationType.AzureActiveDirectory;
                }
                else
                {
                    connectionInfo.AuthenticationType = AuthenticationType.SqlServer;
                }
            }
            
            return connectionInfo;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, $"[SsmsReflectionHelper] Failed to extract connection info");
            return null;
        }
    }
}
