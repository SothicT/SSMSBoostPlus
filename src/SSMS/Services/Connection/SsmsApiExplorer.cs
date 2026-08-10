using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS API 探索工具 - 运行时动态探索可用的服务类型和属性
/// 用于诊断和发现SSMS内部API结构
/// </summary>
public static class SsmsApiExplorer
{
    private static ILoggerService? _logger;

    /// <summary>
    /// 探索所有已加载程序集中的服务类型
    /// </summary>
    public static void ExploreServices(ILoggerService? logger = null)
    {
        _logger = logger;
        _logger?.Information("[ApiExplorer] Starting SSMS API exploration...");

        try
        {
            // 确保程序集已加载
            SmartAssemblyLoader.EnsureAssembliesLoaded(logger);

            var version = SmartAssemblyLoader.GetDetectedVersion();
            _logger?.Information($"[ApiExplorer] SSMS Version: {version}");

            // 探索关键服务类型
            ExploreServiceType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", "VSIntegration");
            ExploreServiceType("Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService", "ObjectExplorer");
            ExploreServiceType("Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService", "ConnectionDlg");
            
            // 探索SSMS 22新程序集中的类型
            if (version >= SSMSVersion.V22)
            {
                _logger?.Information("[ApiExplorer] Exploring SSMS 22 specific types...");
                ExploreAssemblyTypes("ObjectExplorer");
                ExploreAssemblyTypes("ConnectionDlg");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ApiExplorer] Exploration failed");
        }
    }

    /// <summary>
    /// 探索特定服务类型的结构和可用属性/方法
    /// </summary>
    private static void ExploreServiceType(string typeName, string logicalAssemblyName)
    {
        try
        {
            var type = SmartAssemblyLoader.GetType(typeName, logicalAssemblyName);
            if (type == null)
            {
                _logger?.Debug($"[ApiExplorer] Type not found: {typeName}");
                return;
            }

            _logger?.Information($"[ApiExplorer] === Exploring: {typeName} ===");

            // 探索静态属性（通常是单例入口）
            var staticProps = type.GetProperties(BindingFlags.Public | BindingFlags.Static);
            _logger?.Debug($"[ApiExplorer] Static properties ({staticProps.Length}):");
            foreach (var prop in staticProps)
            {
                _logger?.Debug($"  {prop.Name}: {prop.PropertyType.Name}");
                
                // 尝试获取值
                try
                {
                    var value = prop.GetValue(null);
                    _logger?.Debug($"    Current value: {(value?.GetType().Name ?? "null")}");
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"    Cannot get value: {ex.Message}");
                }
            }

            // 探索实例属性
            var instanceProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            _logger?.Debug($"[ApiExplorer] Instance properties ({instanceProps.Length}):");
            foreach (var prop in instanceProps.Take(20)) // 限制输出数量
            {
                if (prop.Name.Contains("Connection") || 
                    prop.Name.Contains("Current") ||
                    prop.Name.Contains("Selected"))
                {
                    _logger?.Debug($"  {prop.Name}: {prop.PropertyType.Name}");
                }
            }

            // 探索方法
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            _logger?.Debug($"[ApiExplorer] Methods ({methods.Length}):");
            foreach (var method in methods.Where(m => m.Name.Contains("Get") || m.Name.Contains("Connection")).Take(15))
            {
                var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                _logger?.Debug($"  {method.Name}({parameters}): {method.ReturnType.Name}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ApiExplorer] Error exploring {typeName}: {ex.Message}");
        }
    }

    /// <summary>
    /// 探索程序集中的所有类型
    /// </summary>
    private static void ExploreAssemblyTypes(string logicalAssemblyName)
    {
        try
        {
            var assembly = SmartAssemblyLoader.GetAssembly(logicalAssemblyName);
            if (assembly == null)
            {
                _logger?.Debug($"[ApiExplorer] Assembly not loaded: {logicalAssemblyName}");
                return;
            }

            _logger?.Information($"[ApiExplorer] === Exploring Assembly: {assembly.GetName().Name} ===");

            var types = assembly.GetTypes()
                .Where(t => t.IsPublic && (
                    t.Name.Contains("Service") ||
                    t.Name.Contains("Provider") ||
                    t.Name.Contains("Manager") ||
                    t.Name.Contains("Connection") ||
                    t.Name.Contains("Explorer")
                ))
                .OrderBy(t => t.Name);

            foreach (var type in types)
            {
                _logger?.Debug($"  {type.FullName}");
                
                // 显示接口
                var interfaces = type.GetInterfaces();
                if (interfaces.Length > 0)
                {
                    _logger?.Debug($"    Implements: {string.Join(", ", interfaces.Select(i => i.Name))}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ApiExplorer] Error exploring assembly {logicalAssemblyName}: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试获取服务实例并探索其属性
    /// </summary>
    public static Dictionary<string, object?> ExploreServiceInstance(IServiceProvider serviceProvider, string typeName, string logicalAssemblyName)
    {
        var result = new Dictionary<string, object?>();

        try
        {
            var type = SmartAssemblyLoader.GetType(typeName, logicalAssemblyName);
            if (type == null)
            {
                _logger?.Debug($"[ApiExplorer] Cannot explore - type not found: {typeName}");
                return result;
            }

            var service = serviceProvider.GetService(type);
            if (service == null)
            {
                _logger?.Debug($"[ApiExplorer] Service not available: {typeName}");
                return result;
            }

            _logger?.Information($"[ApiExplorer] Exploring service instance: {service.GetType().FullName}");

            var props = service.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                try
                {
                    var value = prop.GetValue(service);
                    result[prop.Name] = value;
                    
                    if (value != null)
                    {
                        _logger?.Debug($"  {prop.Name}: {value.GetType().Name}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"  {prop.Name}: [Error: {ex.Message}]");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, $"[ApiExplorer] Error exploring service instance {typeName}");
        }

        return result;
    }

    /// <summary>
    /// 探索所有可能的连接获取路径
    /// </summary>
    public static void ExploreConnectionPaths(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _logger = logger;
        _logger?.Information("[ApiExplorer] Exploring all possible connection retrieval paths...");

        // 路径1: ServiceCache.ConnectionDialog
        ExplorePath1(serviceProvider);

        // 路径2: ConnectionDialogService.Instance
        ExplorePath2();

        // 路径3: ObjectExplorerService
        ExplorePath3(serviceProvider);

        // 路径4: 新的SSMS 22 API
        ExplorePath4(serviceProvider);
    }

    private static void ExplorePath1(IServiceProvider serviceProvider)
    {
        _logger?.Information("[ApiExplorer] Path 1: ServiceCache.ConnectionDialog");
        
        try
        {
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", 
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[ApiExplorer] ServiceCache type not found");
                return;
            }

            var connectionDialogProp = serviceCacheType.GetProperty("ConnectionDialog",
                BindingFlags.Public | BindingFlags.Static);

            if (connectionDialogProp == null)
            {
                // 尝试查找其他属性
                var props = serviceCacheType.GetProperties(BindingFlags.Public | BindingFlags.Static);
                _logger?.Debug($"[ApiExplorer] Available static properties: {string.Join(", ", props.Select(p => p.Name))}");
                return;
            }

            var connectionDialog = connectionDialogProp.GetValue(null);
            _logger?.Debug($"[ApiExplorer] ConnectionDialog instance: {connectionDialog?.GetType().FullName ?? "null"}");
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ApiExplorer] Path 1 failed: {ex.Message}");
        }
    }

    private static void ExplorePath2()
    {
        _logger?.Information("[ApiExplorer] Path 2: ConnectionDialogService.Instance");
        
        try
        {
            var serviceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService",
                "ConnectionDlg");

            if (serviceType == null)
            {
                _logger?.Debug("[ApiExplorer] ConnectionDialogService type not found");
                return;
            }

            var instanceProp = serviceType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);

            if (instanceProp == null)
            {
                _logger?.Debug("[ApiExplorer] Instance property not found");
                return;
            }

            var instance = instanceProp.GetValue(null);
            _logger?.Debug($"[ApiExplorer] Service instance: {instance?.GetType().FullName ?? "null"}");

            if (instance != null)
            {
                var props = instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    if (prop.Name.Contains("Connection") || prop.Name.Contains("Current"))
                    {
                        _logger?.Debug($"  {prop.Name}: {prop.PropertyType.Name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ApiExplorer] Path 2 failed: {ex.Message}");
        }
    }

    private static void ExplorePath3(IServiceProvider serviceProvider)
    {
        _logger?.Information("[ApiExplorer] Path 3: ObjectExplorerService");
        
        try
        {
            var serviceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService",
                "ObjectExplorer");

            if (serviceType == null)
            {
                _logger?.Debug("[ApiExplorer] ObjectExplorerService type not found");
                return;
            }

            var service = serviceProvider.GetService(serviceType);
            _logger?.Debug($"[ApiExplorer] Service instance: {service?.GetType().FullName ?? "null"}");
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ApiExplorer] Path 3 failed: {ex.Message}");
        }
    }

    private static void ExplorePath4(IServiceProvider serviceProvider)
    {
        _logger?.Information("[ApiExplorer] Path 4: SSMS 22 New APIs");
        
        try
        {
            var version = SmartAssemblyLoader.GetDetectedVersion();
            if (version < SSMSVersion.V22)
            {
                _logger?.Debug("[ApiExplorer] Path 4 skipped (not SSMS 22)");
                return;
            }

            // 探索 ObjectExplorer.dll 中的类型
            var objectExplorerAsm = SmartAssemblyLoader.GetAssembly("ObjectExplorer");
            if (objectExplorerAsm != null)
            {
                var types = objectExplorerAsm.GetTypes()
                    .Where(t => t.IsPublic && t.Name.Contains("Service"))
                    .ToList();

                _logger?.Debug($"[ApiExplorer] Found {types.Count} service types in ObjectExplorer.dll");
                
                foreach (var type in types)
                {
                    _logger?.Debug($"  {type.FullName}");
                    
                    var service = serviceProvider.GetService(type);
                    if (service != null)
                    {
                        _logger?.Information($"  ✅ Service available: {type.Name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[ApiExplorer] Path 4 failed: {ex.Message}");
        }
    }
}
