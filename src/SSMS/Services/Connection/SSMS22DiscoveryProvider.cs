using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS 22 深度探索 Provider - 通过7条独立策略深入探索 SSMS 22 内部连接获取方式。
/// 优先级 P-5，高于 ActiveWindowProvider(P-1) 和 SSMS22Provider(P0) 之间。
/// 
/// 设计原则：
/// 1. 每条策略都是独立的，互不影响
/// 2. 每条策略都有前置条件检查和详细失败原因日志
/// 3. 探索式策略（Path 5-7）只输出诊断日志，不返回值（用于后续人工分析）
/// 4. 高成功率策略（Path 1-3）优先尝试并返回值
/// </summary>
public class SSMS22DiscoveryProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;

    public string ProviderName => "SSMS 22 Discovery Provider";
    public int Priority => -5; // 高于 ActiveWindow(-1) 和 SSMS22(0)

    public bool IsAvailable => SmartAssemblyLoader.GetDetectedVersion() >= SSMSVersion.V22;

    public SSMS22DiscoveryProvider(IServiceProvider serviceProvider, ILoggerService? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!IsAvailable)
        {
            _logger?.Debug("[SSMS22Discovery] Not available (SSMS version < 22)");
            return null;
        }

        _logger?.Information("╔══════════════════════════════════════════════════╗");
        _logger?.Information("║   SSMS 22 Discovery Provider - 7 条探索策略启动  ║");
        _logger?.Information("╚══════════════════════════════════════════════════╝");

        try
        {
            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // ============================================================
            // 高成功率策略（同时执行探索，任一成功即返回）
            // ============================================================

            // 策略1: DependencyManager DI 容器 → 解析 IConnectionManager
            var result = await Strategy1_DependencyManagerAsync();
            if (result != null) return result;

            // 策略2: 直接通过 VS Package 的 IServiceProvider 获取 SSMS 服务
            result = await Strategy2_VsServiceProviderAsync();
            if (result != null) return result;

            // 策略3: ObjectExplorer.dll 深度服务扫描
            result = await Strategy3_ObjectExplorerDeepScanAsync();
            if (result != null) return result;

            // 策略4: SVsMonitorSelection + WindowFrame 组合探索
            result = await Strategy4_MonitorSelectionDeepAsync();
            if (result != null) return result;

            // ============================================================
            // 探索式策略（只输出日志，不返回值 - 供诊断分析）
            // ============================================================
            await Strategy5_RunningDocumentTableAsync();
            await Strategy6_DteWindowsEnumerationAsync();
            await Strategy7_AppDomainConnectionScanAsync();

            _logger?.Warning("[SSMS22Discovery] ❌ 全部7条策略均未获取到连接");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[SSMS22Discovery] Provider exception");
            return null;
        }
    }

    // ============================================================
    // 策略1: DependencyManager DI 容器
    // 前提条件:
    //   - ConnectionDialogService 存在 Instance 属性（单例入口）
    //   - Instance 有 DependencyManager 属性（类型 IDependencyManager）
    //   - DependencyManager 能解析 IConnectionManager 服务
    // ============================================================
    private async Task<ConnectionInfo?> Strategy1_DependencyManagerAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略1] DependencyManager DI 容器探索...");

        // 步骤1: 获取 ConnectionDialogService 类型
        var serviceType = SmartAssemblyLoader.GetType(
            "Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService",
            "ConnectionDlg");

        if (serviceType == null)
        {
            _logger?.Debug("[SSMS22Discovery][策略1] ❌ ConnectionDialogService 类型未找到（ConnectionDlg.dll 未加载）");
            return null;
        }
        _logger?.Debug($"[SSMS22Discovery][策略1] ✅ ConnectionDialogService: {serviceType.FullName}");

        // 步骤2: 获取 Instance 属性
        var instanceProp = serviceType.GetProperty("Instance",
            BindingFlags.Public | BindingFlags.Static);

        if (instanceProp == null)
        {
            _logger?.Debug("[SSMS22Discovery][策略1] ❌ Instance 静态属性不存在");
            return null;
        }
        _logger?.Debug("[SSMS22Discovery][策略1] ✅ Instance 属性存在");

        var instance = instanceProp.GetValue(null);
        if (instance == null)
        {
            _logger?.Debug("[SSMS22Discovery][策略1] ❌ Instance 值为 null（ConnectionDialogService 未被初始化）");
            return null;
        }
        _logger?.Debug($"[SSMS22Discovery][策略1] ✅ Instance 类型: {instance.GetType().FullName}");

        // 步骤3: 列出所有公共属性（诊断）
        var allProps = instance.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
            .ToList();
        _logger?.Debug($"[SSMS22Discovery][策略1] 实例共有 {allProps.Count} 个属性");
        foreach (var p in allProps)
        {
            _logger?.Debug($"[SSMS22Discovery][策略1]   属性: {p.Name} ({p.PropertyType.Name})");
        }

        // 步骤4: 获取 DependencyManager
        // IDependencyManager 是 SSMS 22 DI 容器的入口
        // 可能的名字: DependencyManager, Dependencies, ServiceProvider, Container
        string[] possibleContainerNames = { "DependencyManager", "Dependencies", "ServiceProvider", "Container" };
        PropertyInfo? containerProp = null;

        foreach (var name in possibleContainerNames)
        {
            containerProp = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (containerProp != null)
            {
                _logger?.Debug($"[SSMS22Discovery][策略1] ✅ 找到容器属性: {name}");
                break;
            }
        }

        if (containerProp == null)
        {
            _logger?.Debug("[SSMS22Discovery][策略1] ❌ 未找到任何容器属性（DependencyManager/Dependencies/ServiceProvider/Container）");
            return null;
        }

        var container = containerProp.GetValue(instance);
        if (container == null)
        {
            _logger?.Debug("[SSMS22Discovery][策略1] ❌ 容器属性值为 null");
            return null;
        }
        _logger?.Debug($"[SSMS22Discovery][策略1] ✅ 容器类型: {container.GetType().FullName}");

        // 步骤5: 探索容器的接口和属性
        var containerType = container.GetType();
        var containerInterfaces = containerType.GetInterfaces();
        _logger?.Debug($"[SSMS22Discovery][策略1] 容器实现的接口: {string.Join(", ", containerInterfaces.Select(i => i.Name))}");

        // 尝试从容器中解析连接服务
        // 方法1: 如果容器有 GetService/Resolve 方法
        var getServiceMethod = containerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name is "GetService" or "Resolve" or "TryResolve");

        if (getServiceMethod != null)
        {
            _logger?.Debug($"[SSMS22Discovery][策略1] ✅ 找到解析方法: {getServiceMethod.Name}");

            // 尝试解析已知的连接服务类型
            var knownServiceTypes = new[]
            {
                "Microsoft.SqlServer.Tools.Connection.IConnectionManager",
                "Microsoft.SqlServer.Tools.Connection.ISqlConnectionService",
                "Microsoft.SqlServer.Tools.Connection.IActiveConnectionManager",
                "Microsoft.SqlTools.Connection.IConnectionManager",
                "Microsoft.SqlTools.Connection.ISqlConnectionService",
                "Microsoft.SqlServer.SqlTools.Interfaces.Connection.IConnectionManager",
                "Microsoft.SqlServer.Management.Common.IServerConnection"
            };

            foreach (var serviceTypeName in knownServiceTypes)
            {
                try
                {
                    var svcType = Type.GetType(serviceTypeName, false)
                        ?? AppDomain.CurrentDomain.GetAssemblies()
                            .Select(a => a.GetType(serviceTypeName, false))
                            .FirstOrDefault(t => t != null);

                    if (svcType == null)
                    {
                        _logger?.Debug($"[SSMS22Discovery][策略1]   跳过 {serviceTypeName}（类型未加载）");
                        continue;
                    }

                    _logger?.Debug($"[SSMS22Discovery][策略1]   尝试解析: {svcType.Name}");

                    // 尝试调用 GetService(Type)
                    object? svcInstance = null;
                    try
                    {
                        var parameters = getServiceMethod.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Type))
                        {
                            svcInstance = getServiceMethod.Invoke(container, new object[] { svcType });
                        }
                        else if (parameters.Length == 0)
                        {
                            svcInstance = getServiceMethod.Invoke(container, null);
                        }
                    }
                    catch { }

                    if (svcInstance == null)
                    {
                        _logger?.Debug($"[SSMS22Discovery][策略1]   ❌ {svcType.Name} 解析失败（返回 null 或异常）");
                        continue;
                    }

                    _logger?.Debug($"[SSMS22Discovery][策略1]   ✅ {svcType.Name} 解析成功: {svcInstance.GetType().FullName}");

                    // 探索该服务实例的连接相关属性
                    var connInfo = ExtractFromServiceInstance(svcInstance, "[策略1]");
                    if (connInfo != null) return connInfo;
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[SSMS22Discovery][策略1]   解析 {serviceTypeName} 异常: {ex.Message}");
                }
            }
        }

        // 方法2: 如果容器本身有 Connection 相关属性
        var containerConnInfo = ExtractConnectionFromObject(container, "[策略1-容器]");
        if (containerConnInfo != null) return containerConnInfo;

        // 方法3: 探索容器的所有属性和方法（完整诊断）
        _logger?.Debug("[SSMS22Discovery][策略1] 探索容器所有公共属性:");
        foreach (var prop in containerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                var val = prop.GetValue(container);
                _logger?.Debug($"  {prop.Name}: {prop.PropertyType.Name} = {(val?.GetType().Name ?? "null")}");
            }
            catch (Exception ex)
            {
                _logger?.Debug($"  {prop.Name}: [获取失败: {ex.Message}]");
            }
        }

        _logger?.Debug("[SSMS22Discovery][策略1] ❌ 未能从 DI 容器获取连接");
        return null;
    }

    // ============================================================
    // 策略2: VS Package IServiceProvider 直接获取 SSMS 服务
    // 前提条件:
    //   - VS Package 的 IServiceProvider 能解析特定 SSMS 服务
    //   - 这些服务本身具有连接相关属性
    // ============================================================
    private async Task<ConnectionInfo?> Strategy2_VsServiceProviderAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略2] VS ServiceProvider 服务解析...");

        // 尝试通过 VS Package 获取各种 SSMS 服务
        // SSMS 22 可能通过 MEF/VS MEF 容器注册了新的服务类型
        Type[] serviceTypesToTry =
        {
            TryGetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache"),
            TryGetType("Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService"),
            TryGetType("Microsoft.SqlServer.Management.SqlStudio.Explorer.IObjectExplorerService"),
            TryGetType("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.IScriptEditorService"),
            TryGetType("Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService"),
        };

        foreach (var svcType in serviceTypesToTry)
        {
            if (svcType == null) continue;

            _logger?.Debug($"[SSMS22Discovery][策略2] 尝试获取服务: {svcType.FullName}");

            try
            {
                var svc = _serviceProvider.GetService(svcType);
                if (svc == null)
                {
                    _logger?.Debug($"[SSMS22Discovery][策略2] ❌ {svcType.Name} 服务不可用（返回 null）");
                    continue;
                }

                _logger?.Debug($"[SSMS22Discovery][策略2] ✅ {svcType.Name} 服务可用: {svc.GetType().FullName}");
                var connInfo = ExtractFromServiceInstance(svc, "[策略2]");
                if (connInfo != null) return connInfo;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SSMS22Discovery][策略2] ❌ {svcType.Name} 获取异常: {ex.Message}");
            }
        }

        // 尝试 SVsServiceProvider 获取标准 VS 服务
        _logger?.Debug("[SSMS22Discovery][策略2] 尝试 SVsServiceProvider...");
        try
        {
            var shell = _serviceProvider.GetService(typeof(SVsShell)) as IVsShell;
            if (shell != null)
            {
                _logger?.Debug("[SSMS22Discovery][策略2] ✅ IVsShell 可用，探索其属性...");
                // 通过 IVsShell 获取全局服务
                // IVsShell 本身没有连接信息，但可以尝试获取其他服务
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略2] SVsShell 获取失败: {ex.Message}");
        }

        _logger?.Debug("[SSMS22Discovery][策略2] ❌ 未能从 VS ServiceProvider 获取连接");
        return null;
    }

    // ============================================================
    // 策略3: ObjectExplorer.dll 深度服务扫描
    // 前提条件:
    //   - ObjectExplorer.dll 已加载
    //   - 其中有公共服务类型可通过 VS 容器获取
    //   - 服务实例包含当前选中数据库节点及其连接
    // ============================================================
    private async Task<ConnectionInfo?> Strategy3_ObjectExplorerDeepScanAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略3] ObjectExplorer.dll 深度扫描...");

        var assembly = SmartAssemblyLoader.GetAssembly("ObjectExplorer");
        if (assembly == null)
        {
            _logger?.Debug("[SSMS22Discovery][策略3] ❌ ObjectExplorer.dll 未加载");
            return null;
        }

        // 获取所有公共类型
        var allTypes = assembly.GetTypes().Where(t => t.IsPublic).ToList();
        _logger?.Debug($"[SSMS22Discovery][策略3] ObjectExplorer.dll 共有 {allTypes.Count} 个公共类型");

        // 筛选与服务/管理器/连接相关的类型
        var relevantTypes = allTypes
            .Where(t => t.Name.Contains("Service") || t.Name.Contains("Manager") ||
                        t.Name.Contains("Connection") || t.Name.Contains("Explorer") ||
                        t.Name.Contains("Provider") || t.Name.Contains("Store"))
            .ToList();

        _logger?.Debug($"[SSMS22Discovery][策略3] 相关类型 ({relevantTypes.Count} 个):");
        foreach (var t in relevantTypes)
        {
            var interfaces = t.GetInterfaces().Select(i => i.Name);
            _logger?.Debug($"  {t.FullName}");
            if (interfaces.Any())
                _logger?.Debug($"    接口: {string.Join(", ", interfaces)}");
        }

        // 尝试从 VS 服务容器获取每个服务类型
        foreach (var svcType in relevantTypes.Where(t => t.Name.Contains("Service") || t.Name.Contains("Manager")))
        {
            try
            {
                var svc = _serviceProvider.GetService(svcType);
                if (svc == null) continue;

                _logger?.Debug($"[SSMS22Discovery][策略3] ✅ 服务可用: {svcType.Name}");

                // 探索该服务的所有属性
                var svcProps = svc.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                _logger?.Debug($"  [{svcType.Name}] 属性 ({svcProps.Length}):");
                foreach (var prop in svcProps)
                {
                    var isConnectionRelated = prop.Name.Contains("Connection") ||
                                              prop.Name.Contains("Selected") ||
                                              prop.Name.Contains("Current") ||
                                              prop.Name.Contains("Active");
                    if (!isConnectionRelated && svcProps.Length > 5) continue; // 只打连接相关属性

                    try
                    {
                        var val = prop.GetValue(svc);
                        _logger?.Debug($"    {prop.Name}({prop.PropertyType.Name}) = {(val?.GetType().Name ?? "null")}");

                        if (isConnectionRelated && val != null)
                        {
                            var connInfo = ExtractConnectionFromObject(val, $"[策略3-{svcType.Name}]");
                            if (connInfo != null) return connInfo;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"    {prop.Name}: [获取失败: {ex.Message}]");
                    }
                }

                // 探索方法（特别是 Get/Find/Query 相关方法）
                var methods = svcType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var method in methods.Where(m =>
                    m.Name.StartsWith("Get") || m.Name.StartsWith("Find") ||
                    m.Name.StartsWith("Query") || m.Name.StartsWith("Try")))
                {
                    if (method.GetParameters().Length == 0)
                    {
                        try
                        {
                            _logger?.Debug($"  [{svcType.Name}] 调用方法: {method.Name}()");
                            var result = method.Invoke(svc, null);
                            if (result != null)
                            {
                                _logger?.Debug($"    返回: {result.GetType().FullName}");
                                var connInfo = ExtractConnectionFromObject(result, $"[策略3-{svcType.Name}.{method.Name}]");
                                if (connInfo != null) return connInfo;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug($"    {method.Name}: [调用失败: {ex.Message}]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SSMS22Discovery][策略3] {svcType.Name} 获取失败: {ex.Message}");
            }
        }

        _logger?.Debug("[SSMS22Discovery][策略3] ❌ 未能从 ObjectExplorer.dll 获取连接");
        return null;
    }

    // ============================================================
    // 策略4: SVsMonitorSelection + WindowFrame 组合探索
    // 前提条件:
    //   - ServiceCache.VSMonitorSelection 可用
    //   - 通过 IVsMonitorSelection 接口获取当前选中的层次结构
    // ============================================================
    private async Task<ConnectionInfo?> Strategy4_MonitorSelectionDeepAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略4] SVsMonitorSelection + WindowFrame 探索...");

        // 方法一: 通过 ServiceCache.VSMonitorSelection（已在 ActiveWindowConnectionProvider 中验证可用）
        _logger?.Debug("[SSMS22Discovery][策略4] 方法1: ServiceCache.VSMonitorSelection...");
        try
        {
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger?.Debug("[SSMS22Discovery][策略4] ❌ ServiceCache 类型未找到");
            }
            else
            {
                var monitorSelectionProp = serviceCacheType.GetProperty("VSMonitorSelection",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (monitorSelectionProp == null)
                {
                    _logger?.Debug("[SSMS22Discovery][策略4] ❌ VSMonitorSelection 属性未找到");
                }
                else
                {
                    var monitorSelection = monitorSelectionProp.GetValue(null);
                    if (monitorSelection == null)
                    {
                        _logger?.Debug("[SSMS22Discovery][策略4] ❌ VSMonitorSelection 值为 null");
                    }
                    else
                    {
                        _logger?.Debug($"[SSMS22Discovery][策略4] ✅ VSMonitorSelection 类型: {monitorSelection.GetType().FullName}");

                        // 通过 IVsMonitorSelection 接口调用 GetSelection
                        var vsInterface = monitorSelection.GetType().GetInterface("IVsMonitorSelection");
                        if (vsInterface == null)
                        {
                            vsInterface = monitorSelection.GetType().GetInterfaces()
                                .FirstOrDefault(i => i.Name.Contains("IVsMonitorSelection"));
                        }

                        if (vsInterface == null)
                        {
                            _logger?.Debug("[SSMS22Discovery][策略4] ❌ IVsMonitorSelection 接口未找到");
                        }
                        else
                        {
                            var getSelectionMethod = vsInterface.GetMethod("GetSelection",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (getSelectionMethod == null)
                            {
                                _logger?.Debug("[SSMS22Discovery][策略4] ❌ GetSelection 方法未找到");
                            }
                            else
                            {
                                // IVsMonitorSelection.GetSelection(out IntPtr ppHier, out uint pitemid, out IVsMultiItemSelect ppMISel, out IntPtr ppSelContainer)
                                object[] selParams = new object[] { IntPtr.Zero, 0U, null, IntPtr.Zero };
                                try
                                {
                                    var hr = (int)getSelectionMethod.Invoke(monitorSelection, selParams);
                                    if (hr == 0)
                                    {
                                        var ppHier = (IntPtr)selParams[0];
                                        var pitemid = (uint)selParams[1];
                                        _logger?.Debug($"[SSMS22Discovery][策略4] GetSelection 成功: ppHier={(ppHier != IntPtr.Zero ? "非空" : "空")}, pitemid={pitemid}");

                                        if (ppHier != IntPtr.Zero)
                                        {
                                            var hierarchy = Marshal.GetObjectForIUnknown(ppHier) as IVsHierarchy;
                                            if (hierarchy != null)
                                            {
                                                _logger?.Debug($"[SSMS22Discovery][策略4] ✅ Hierarchy 类型: {hierarchy.GetType().FullName}");

                                                // 尝试获取 ExtObject
                                                object propertyValue;
                                                var propHr = hierarchy.GetProperty(pitemid, (int)__VSHPROPID.VSHPROPID_ExtObject, out propertyValue);
                                                if (propHr == 0 && propertyValue != null)
                                                {
                                                    _logger?.Debug($"[SSMS22Discovery][策略4] ✅ Hierarchy ExtObject: {propertyValue.GetType().FullName}");
                                                    var connInfo = ExtractConnectionFromObject(propertyValue, "[策略4-Hierarchy]");
                                                    if (connInfo != null) return connInfo;
                                                }

                                                // 尝试获取 Caption
                                                propHr = hierarchy.GetProperty(pitemid, (int)__VSHPROPID.VSHPROPID_Caption, out propertyValue);
                                                if (propHr == 0) _logger?.Debug($"[SSMS22Discovery][策略4] Hierarchy Caption: {propertyValue}");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _logger?.Debug($"[SSMS22Discovery][策略4] GetSelection 失败, HRESULT=0x{hr:X8}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger?.Debug($"[SSMS22Discovery][策略4] GetSelection 调用异常: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略4] ServiceCache 路径异常: {ex.Message}");
        }

        // 方法二: 尝试通过 IVsUIShell 获取窗口框架
        _logger?.Debug("[SSMS22Discovery][策略4] 方法2: IVsUIShell 窗口枚举...");
        try
        {
            var uiShell = _serviceProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (uiShell != null)
            {
                // 查找 Object Explorer 工具窗口
                Guid toolWindowGuid = new Guid("C50070C6-6311-4A88-B295-A6D2D5C9D62C");
                IVsWindowFrame oeFrame;
                var hr = uiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref toolWindowGuid, out oeFrame);
                if (hr == 0 && oeFrame != null)
                {
                    _logger?.Debug("[SSMS22Discovery][策略4] ✅ 找到 Object Explorer 窗口");

                    object docView;
                    hr = oeFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView);
                    if (hr == 0 && docView != null)
                    {
                        _logger?.Debug($"[SSMS22Discovery][策略4] OE DocView: {docView.GetType().FullName}");
                        var connInfo = ExtractConnectionFromObject(docView, "[策略4-OE]");
                        if (connInfo != null) return connInfo;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略4] IVsUIShell 路径失败: {ex.Message}");
        }

        // 方法三: 尝试通过 Object Explorer 工具窗口获取选中项
        _logger?.Debug("[SSMS22Discovery][策略4] 方法3: Object Explorer 窗口...");
        try
        {
            var oeServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService",
                "ObjectExplorer");

            if (oeServiceType != null)
            {
                var oeService = _serviceProvider.GetService(oeServiceType);
                if (oeService != null)
                {
                    _logger?.Debug($"[SSMS22Discovery][策略4] ✅ ObjectExplorerService 可用: {oeService.GetType().FullName}");
                    var connInfo = ExtractFromServiceInstance(oeService, "[策略4-OE]");
                    if (connInfo != null) return connInfo;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略4] ObjectExplorer 路径失败: {ex.Message}");
        }

        _logger?.Debug("[SSMS22Discovery][策略4] ❌ 未能从 MonitorSelection 获取连接");
        return null;
    }

    // ============================================================
    // 策略5: Running Document Table (RDT) - 探索式（只输出日志）
    // ============================================================
    private async Task Strategy5_RunningDocumentTableAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略5] Running Document Table 探索...");

        try
        {
            var rdt = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt == null)
            {
                _logger?.Debug("[SSMS22Discovery][策略5] ❌ IVsRunningDocumentTable 不可用");
                return;
            }

            _logger?.Debug("[SSMS22Discovery][策略5] ✅ IVsRunningDocumentTable 可用");

            // 枚举所有打开的文档
            IEnumRunningDocuments enumRdt;
            int hr = rdt.GetRunningDocumentsEnum(out enumRdt);
            if (hr != 0 || enumRdt == null)
            {
                _logger?.Debug($"[SSMS22Discovery][策略5] ❌ 无法获取文档枚举, HRESULT=0x{hr:X8}");
                return;
            }

            uint[] cookies = new uint[1];
            uint fetched = 0;
            int docIndex = 0;

            while (enumRdt.Next(1, cookies, out fetched) == 0 && fetched > 0)
            {
                uint cookie = cookies[0];
                docIndex++;

                // 获取文档信息 - IVsRunningDocumentTable.GetDocumentInfo 有 8 个参数
                uint grfRDTFlags;
                string? docMoniker;
                uint dwFlags;
                uint readLocks;
                uint editLocks;
                IVsHierarchy hierarchy;
                uint itemId;
                IntPtr docData = IntPtr.Zero;

                hr = rdt.GetDocumentInfo(
                    cookie,
                    out grfRDTFlags,
                    out readLocks,
                    out editLocks,
                    out docMoniker,
                    out hierarchy,
                    out itemId,
                    out docData);

                if (hr == 0 && docMoniker != null)
                {
                    var shortName = docMoniker.Split('\\', '/').LastOrDefault() ?? docMoniker;
                    _logger?.Debug($"[SSMS22Discovery][策略5] 文档 #{docIndex}: {shortName}");

                    if (shortName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Debug($"  ⭐ 这是 SQL 查询窗口!");

                        // 尝试从 DocData 提取连接信息
                        if (docData != IntPtr.Zero)
                        {
                            try
                            {
                                var docDataObj = Marshal.GetObjectForIUnknown(docData);
                                if (docDataObj != null)
                                {
                                    _logger?.Debug($"  DocData 类型: {docDataObj.GetType().FullName}");
                                    _logger?.Debug($"  DocData 接口: {string.Join(", ", docDataObj.GetType().GetInterfaces().Select(i => i.Name))}");

                                    // 探索 DocData 的所有属性
                                    var docProps = docDataObj.GetType()
                                        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                                    foreach (var prop in docProps)
                                    {
                                        var isConnRelated = prop.Name.Contains("Connection") ||
                                                            prop.Name.Contains("Editor") ||
                                                            prop.Name.Contains("Language") ||
                                                            prop.Name.Contains("Text");
                                        if (!isConnRelated) continue;

                                        try
                                        {
                                            var val = prop.GetValue(docDataObj);
                                            _logger?.Debug($"    {prop.Name}({prop.PropertyType.Name}) = {(val?.GetType().Name ?? "null")}");
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger?.Debug($"    {prop.Name}: [获取失败: {ex.Message}]");
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"  DocData 探索失败: {ex.Message}");
                            }
                            finally
                            {
                                Marshal.Release(docData);
                            }
                        }
                    }
                }
            }

            _logger?.Debug($"[SSMS22Discovery][策略5] 共发现 {docIndex} 个打开文档");
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略5] ❌ RDT 枚举失败: {ex.Message}");
        }
    }

    // ============================================================
    // 策略6: DTE Windows 枚举 - 探索式（只输出日志）
    // ============================================================
    private async Task Strategy6_DteWindowsEnumerationAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略6] DTE Windows 枚举探索...");

        try
        {
            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            if (dteType == null)
            {
                _logger?.Debug("[SSMS22Discovery][策略6] ❌ DTE 类型未找到");
                return;
            }

            var dte = _serviceProvider.GetService(dteType);
            if (dte == null)
            {
                _logger?.Debug("[SSMS22Discovery][策略6] ❌ DTE 服务不可用");
                return;
            }

            dynamic dteDynamic = dte;

            // 枚举所有窗口
            try
            {
                dynamic windows = dteDynamic.Windows;
                _logger?.Debug("[SSMS22Discovery][策略6] ✅ 获取 Windows 集合成功");

                int count = 0;
                foreach (var window in windows)
                {
                    count++;
                    if (count > 20) break; // 限制输出

                    try
                    {
                        string caption = "";
                        string kind = "";
                        try { caption = window.Caption ?? ""; } catch { }
                        try { kind = window.Kind ?? ""; } catch { }

                        _logger?.Debug($"[SSMS22Discovery][策略6]   窗口 #{count}: Kind={kind}, Caption={caption.Substring(0, Math.Min(60, caption.Length))}");

                        // 如果是文档窗口，尝试获取 Document
                        if (kind == "Document" || kind == "DocumentWell" || caption.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                dynamic doc = window.Document;
                                if (doc != null)
                                {
                                    _logger?.Debug($"      Document 类型: {doc.GetType().FullName}");

                                    // 尝试 Object
                                    try
                                    {
                                        dynamic obj = doc.Object;
                                        if (obj != null)
                                        {
                                            _logger?.Debug($"      Object 类型: {obj.GetType().FullName}");
                                            dynamic conn = null;
                                            try { conn = obj.Connection; if (conn != null) _logger?.Debug($"      ✅ 找到 Connection: {conn.GetType().FullName}"); } catch { }
                                            try { conn = obj.CurrentConnection; if (conn != null) _logger?.Debug($"      ✅ 找到 CurrentConnection: {conn.GetType().FullName}"); } catch { }
                                        }
                                        else
                                        {
                                            _logger?.Debug($"      Object 为 null（web-based 编辑器不暴露 COM 对象）");
                                        }
                                    }
                                    catch { }

                                    // 尝试 get_Object
                                    try
                                    {
                                        dynamic obj2 = doc.get_Object("");
                                        if (obj2 != null)
                                        {
                                            _logger?.Debug($"      get_Object(\"\") 类型: {obj2.GetType().FullName}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger?.Debug($"      get_Object(\"\") 失败: {ex.Message}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"      Document 获取失败: {ex.Message}");
                            }
                        }

                        // 如果是工具窗口（如 Object Explorer）
                        if (caption.Contains("Object Explorer") || caption.Contains("对象资源管理器"))
                        {
                            _logger?.Debug($"      ⭐ 这是 Object Explorer 窗口!");
                            try
                            {
                                dynamic obj = window.Object;
                                if (obj != null)
                                {
                                    _logger?.Debug($"      Object Explorer 对象类型: {obj.GetType().FullName}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"      Object Explorer 对象获取失败: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"      [窗口 {count} 访问失败: {ex.Message}]");
                    }
                }
                _logger?.Debug($"[SSMS22Discovery][策略6] 共列举了 {count} 个窗口");
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SSMS22Discovery][策略6] Windows 枚举失败: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略6] ❌ DTE 访问失败: {ex.Message}");
        }
    }

    // ============================================================
    // 策略7: AppDomain 程序集扫描 - 探索式（只输出日志）
    // ============================================================
    private async Task Strategy7_AppDomainConnectionScanAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[SSMS22Discovery][策略7] AppDomain 连接扫描...");

        try
        {
            // 获取所有已加载的 SSMS 相关程序集
            var ssmsAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic &&
                            (a.GetName().Name.Contains("SqlServer") ||
                             a.GetName().Name.Contains("SqlTools") ||
                             a.GetName().Name.Contains("Connection")))
                .ToList();

            _logger?.Debug($"[SSMS22Discovery][策略7] 找到 {ssmsAssemblies.Count} 个 SSMS 相关程序集");
            foreach (var asm in ssmsAssemblies)
            {
                _logger?.Debug($"  {asm.GetName().Name} v{asm.GetName().Version}");
            }

            // 在每个程序集中搜索连接相关类型
            foreach (var asm in ssmsAssemblies)
            {
                try
                {
                    var types = asm.GetTypes()
                        .Where(t => t.IsPublic && !t.IsAbstract &&
                                    (t.Name.Contains("ConnectionManager") ||
                                     t.Name.Contains("ConnectionStore") ||
                                     t.Name.Contains("ActiveConnection") ||
                                     t.Name.Contains("CurrentConnection")))
                        .ToList();

                    if (types.Count > 0)
                    {
                        _logger?.Debug($"[SSMS22Discovery][策略7] {asm.GetName().Name} 中找到 {types.Count} 个相关类型:");
                        foreach (var t in types)
                        {
                            _logger?.Debug($"  {t.FullName}");

                            // 检查是否有静态 Instance/Default/Current 属性
                            var staticProps = t.GetProperties(BindingFlags.Public | BindingFlags.Static);
                            foreach (var sp in staticProps)
                            {
                                if (sp.Name is "Instance" or "Default" or "Current")
                                {
                                    _logger?.Debug($"    静态属性: {sp.Name}({sp.PropertyType.Name})");
                                    try
                                    {
                                        var val = sp.GetValue(null);
                                        if (val != null)
                                        {
                                            _logger?.Debug($"      值: {val.GetType().FullName}");
                                            var connInfo = ExtractConnectionFromObject(val, "[策略7]");
                                            if (connInfo != null)
                                            {
                                                _logger?.Information($"[SSMS22Discovery][策略7] ✅ 找到连接! Server={connInfo.ServerInstance}, DB={connInfo.DatabaseName}");
                                                // 注意：探索式策略不返回，只记录
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger?.Debug($"      [获取失败: {ex.Message}]");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[SSMS22Discovery][策略7] {asm.GetName().Name} 类型扫描失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SSMS22Discovery][策略7] ❌ AppDomain 扫描失败: {ex.Message}");
        }
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>
    /// 尝试获取类型（不抛异常）
    /// </summary>
    private static Type? TryGetType(string typeName)
    {
        try
        {
            return Type.GetType(typeName, false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从服务实例中提取连接信息
    /// 探索该实例的所有属性，寻找 Connection/ServerConnection/CurrentConnection 等
    /// </summary>
    private ConnectionInfo? ExtractFromServiceInstance(object serviceInstance, string tag)
    {
        var type = serviceInstance.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

        foreach (var prop in props)
        {
            bool isConnectionRelated = prop.Name.Contains("Connection") ||
                                       prop.Name.Contains("SelectedNode") ||
                                       prop.Name.Contains("Active") ||
                                       prop.Name.Contains("Current");

            if (!isConnectionRelated) continue;

            try
            {
                var val = prop.GetValue(serviceInstance);
                if (val == null)
                {
                    _logger?.Debug($"{tag} {type.Name}.{prop.Name} = null");
                    continue;
                }

                _logger?.Debug($"{tag} ✅ {type.Name}.{prop.Name} = {val.GetType().FullName}");

                var connInfo = ExtractConnectionFromObject(val, $"{tag}-{prop.Name}");
                if (connInfo != null) return connInfo;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"{tag} {type.Name}.{prop.Name} [访问失败: {ex.Message}]");
            }
        }

        // 也检查方法
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var method in methods.Where(m =>
            m.Name.StartsWith("Get") && m.GetParameters().Length == 0 &&
            (m.Name.Contains("Connection") || m.Name.Contains("Selected") || m.Name.Contains("Active"))))
        {
            try
            {
                var result = method.Invoke(serviceInstance, null);
                if (result != null)
                {
                    _logger?.Debug($"{tag} {type.Name}.{method.Name}() 返回: {result.GetType().FullName}");
                    var connInfo = ExtractConnectionFromObject(result, $"{tag}-{method.Name}");
                    if (connInfo != null) return connInfo;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"{tag} {type.Name}.{method.Name}() [调用失败: {ex.Message}]");
            }
        }

        return null;
    }

    /// <summary>
    /// 从任意对象中提取连接信息
    /// 尝试多种连接对象类型（SqlConnection、ServerConnection、SqlConnectionInfo 等）
    /// </summary>
    private ConnectionInfo? ExtractConnectionFromObject(object obj, string tag)
    {
        if (obj == null) return null;

        try
        {
            var type = obj.GetType();
            var typeName = type.FullName ?? "";

            // 如果是直接的 SqlConnection（两种版本都支持）
            if (typeName.Contains("SqlConnection"))
            {
                _logger?.Debug($"{tag} 这是 SqlConnection 类型!");
                try
                {
                    var dataSource = type.GetProperty("DataSource")?.GetValue(obj) as string;
                    var database = type.GetProperty("Database")?.GetValue(obj) as string;
                    var connStr = type.GetProperty("ConnectionString")?.GetValue(obj) as string;

                    if (!string.IsNullOrEmpty(dataSource))
                    {
                        _logger?.Information($"{tag} ✅ SqlConnection: {dataSource}/{database}");
                        return new ConnectionInfo
                        {
                            SourceProvider = ProviderName,
                            ServerInstance = dataSource,
                            DatabaseName = database,
                            ConnectionString = connStr
                        };
                    }
                }
                catch { }
            }

            // 使用 SsmsReflectionHelper 提取（支持 ServerConnection, SqlConnectionInfo 等）
            var connInfo = SsmsReflectionHelper.ExtractConnectionInfo(obj, ProviderName);
            if (connInfo != null && (!string.IsNullOrEmpty(connInfo.ServerInstance) || !string.IsNullOrEmpty(connInfo.ConnectionString)))
            {
                _logger?.Information($"{tag} ✅ 提取到连接: Server={connInfo.ServerInstance}, DB={connInfo.DatabaseName}");
                return connInfo;
            }

            // 如果反射没找到，尝试深度探索该对象的所有属性
            _logger?.Debug($"{tag} 深度探索 {typeName} 的所有属性...");
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var prop in props)
            {
                if (!prop.Name.Contains("Connection") && !prop.Name.Contains("Server") &&
                    !prop.Name.Contains("Database") && !prop.Name.Contains("Sql"))
                    continue;

                try
                {
                    var val = prop.GetValue(obj);
                    if (val == null) continue;

                    _logger?.Debug($"{tag}   嵌套属性: {prop.Name}({prop.PropertyType.Name}) = {val.GetType().FullName}");

                    // 递归提取
                    var nestedResult = ExtractConnectionFromObject(val, $"{tag}.{prop.Name}");
                    if (nestedResult != null) return nestedResult;
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"{tag}   {prop.Name}: [获取失败: {ex.Message}]");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"{tag} 提取异常: {ex.Message}");
            return null;
        }
    }
}