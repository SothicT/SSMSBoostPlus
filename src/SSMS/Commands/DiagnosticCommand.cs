using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services.Connection;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 诊断命令 - 用于深入探索SSMS 22的连接API
/// 帮助发现新的连接获取路径
/// VSCT: cmdidDiagnostic = 0x0200
/// </summary>
public sealed class DiagnosticCommand : BaseCommand<DiagnosticCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();

    private DiagnosticCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0200)
    {
    }

    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new DiagnosticCommand(package, commandService!)
        {
            _logger = logger
        };
    }

    protected override void Execute(object sender, EventArgs e)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ExecuteAsync();
        });
    }

    private async Task ExecuteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            _logger.Information("=== Starting Diagnostic Exploration ===");
            
            // 1. 探索 ServiceCache.GlobalConnectionInfo
            await ExploreGlobalConnectionInfo();
            
            // 2. 探索 DependencyManager
            await ExploreDependencyManager();
            
            // 3. 探索所有可用的服务
            await ExploreAllServices();
            
            // 4. 探索活动窗口
            await ExploreActiveWindow();
            
            _logger.Information("=== Diagnostic Exploration Completed ===");
            
            ShowMessage("Diagnostic completed. Check log file for details.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Diagnostic failed");
            ShowMessage($"Diagnostic failed: {ex.Message}");
        }
    }

    private async Task ExploreGlobalConnectionInfo()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger.Information("[Diagnostic] === Exploring ServiceCache.GlobalConnectionInfo ===");

        try
        {
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                _logger.Error("[Diagnostic] ServiceCache type not found");
                return;
            }

            // 获取 GlobalConnectionInfo 属性
            var globalConnInfoProp = serviceCacheType.GetProperty("GlobalConnectionInfo",
                BindingFlags.Public | BindingFlags.Static);

            if (globalConnInfoProp == null)
            {
                _logger.Error("[Diagnostic] GlobalConnectionInfo property not found");
                return;
            }

            var globalConnInfo = globalConnInfoProp.GetValue(null);
            _logger.Information($"[Diagnostic] GlobalConnectionInfo value: {globalConnInfo?.GetType().FullName ?? "null"}");

            if (globalConnInfo != null)
            {
                // 探索这个对象的所有属性
                var type = globalConnInfo.GetType();
                _logger.Information($"[Diagnostic] Type: {type.FullName}");
                
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    _logger.Information($"[Diagnostic]   {prop.Name}: {prop.PropertyType.Name}");
                    
                    try
                    {
                        var value = prop.GetValue(globalConnInfo);
                        if (value != null)
                        {
                            _logger.Information($"[Diagnostic]     Value: {value}");
                            
                            // 如果是连接相关对象，继续探索
                            if (prop.Name.Contains("Connection") || prop.Name.Contains("Current"))
                            {
                                ExploreConnectionObject(value);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[Diagnostic]     Error: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Diagnostic] Failed to explore GlobalConnectionInfo");
        }
    }

    private void ExploreConnectionObject(object connectionObj)
    {
        try
        {
            var type = connectionObj.GetType();
            _logger.Information($"[Diagnostic]       === Connection Object: {type.FullName} ===");

            // ServerInstance
            var serverInstanceProp = type.GetProperty("ServerInstance");
            if (serverInstanceProp != null)
            {
                var serverInstance = serverInstanceProp.GetValue(connectionObj);
                _logger.Information($"[Diagnostic]       ServerInstance: {serverInstance}");
            }

            // DatabaseName
            var databaseNameProp = type.GetProperty("DatabaseName");
            if (databaseNameProp != null)
            {
                var databaseName = databaseNameProp.GetValue(connectionObj);
                _logger.Information($"[Diagnostic]       DatabaseName: {databaseName}");
            }

            // ConnectionString
            var connectionStringProp = type.GetProperty("ConnectionString");
            if (connectionStringProp != null)
            {
                var connectionString = connectionStringProp.GetValue(connectionObj);
                _logger.Information($"[Diagnostic]       ConnectionString: {connectionString}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Diagnostic] Failed to explore connection object");
        }
    }

    private async Task ExploreDependencyManager()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger.Information("[Diagnostic] === Exploring DependencyManager ===");

        try
        {
            var connectionDialogServiceType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.ConnectionDlg.UI.ConnectionDialogService",
                "ConnectionDlg");

            if (connectionDialogServiceType == null)
            {
                _logger.Error("[Diagnostic] ConnectionDialogService type not found");
                return;
            }

            var instanceProp = connectionDialogServiceType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);

            if (instanceProp == null)
            {
                _logger.Error("[Diagnostic] Instance property not found");
                return;
            }

            var serviceInstance = instanceProp.GetValue(null);
            if (serviceInstance == null)
            {
                _logger.Error("[Diagnostic] Service instance is null");
                return;
            }

            // 获取 DependencyManager
            var dependencyManagerProp = serviceInstance.GetType().GetProperty("DependencyManager");
            if (dependencyManagerProp == null)
            {
                _logger.Error("[Diagnostic] DependencyManager property not found");
                return;
            }

            var dependencyManager = dependencyManagerProp.GetValue(serviceInstance);
            _logger.Information($"[Diagnostic] DependencyManager: {dependencyManager?.GetType().FullName ?? "null"}");

            if (dependencyManager != null)
            {
                var type = dependencyManager.GetType();
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                
                foreach (var method in methods)
                {
                    if (method.Name.Contains("Get") || method.Name.Contains("Resolve"))
                    {
                        _logger.Information($"[Diagnostic]   Method: {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
                    }
                }

                // 尝试调用 GetService 或 Resolve 方法
                var getServiceMethod = type.GetMethod("GetService");
                if (getServiceMethod != null)
                {
                    _logger.Information("[Diagnostic] Trying to call GetService with known types...");
                    
                    // 尝试获取 ServerConnection 相关服务
                    var serverConnectionType = SmartAssemblyLoader.GetType(
                        "Microsoft.SqlServer.Management.Common.ServerConnection",
                        "ConnectionInfo");
                    
                    if (serverConnectionType != null)
                    {
                        try
                        {
                            var service = getServiceMethod.Invoke(dependencyManager, new object[] { serverConnectionType });
                            if (service != null)
                            {
                                _logger.Information($"[Diagnostic] ✅ Got service: {service.GetType().FullName}");
                                ExploreConnectionObject(service);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"[Diagnostic] GetService failed: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Diagnostic] Failed to explore DependencyManager");
        }
    }

    private async Task ExploreAllServices()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger.Information("[Diagnostic] === Exploring All Available Services ===");

        try
        {
            // 探索 ServiceCache 中所有静态属性
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType != null)
            {
                var staticProps = serviceCacheType.GetProperties(BindingFlags.Public | BindingFlags.Static);
                
                foreach (var prop in staticProps)
                {
                    if (prop.Name.Contains("Connection") || 
                        prop.Name.Contains("Script") ||
                        prop.Name.Contains("Editor"))
                    {
                        try
                        {
                            var value = prop.GetValue(null);
                            _logger.Information($"[Diagnostic] {prop.Name}: {value?.GetType().FullName ?? "null"}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"[Diagnostic] {prop.Name}: Error - {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Diagnostic] Failed to explore all services");
        }
    }

    private async Task ExploreActiveWindow()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger.Information("[Diagnostic] === Exploring Active Window ===");

        try
        {
            // 获取 DTE
            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            if (dteType == null)
            {
                _logger.Error("[Diagnostic] DTE type not found");
                return;
            }

            var dte = await Package.GetServiceAsync(dteType);
            if (dte == null)
            {
                _logger.Error("[Diagnostic] DTE service not available");
                return;
            }

            // 获取活动窗口
            var activeWindowProp = dteType.GetProperty("ActiveWindow");
            var activeWindow = activeWindowProp?.GetValue(dte);
            
            if (activeWindow != null)
            {
                _logger.Information($"[Diagnostic] Active window: {activeWindow.GetType().FullName}");
                
                // 探索窗口属性
                var windowType = activeWindow.GetType();
                var props = windowType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                
                foreach (var prop in props)
                {
                    if (prop.Name.Contains("Document") || prop.Name.Contains("Object"))
                    {
                        _logger.Information($"[Diagnostic]   {prop.Name}: {prop.PropertyType.Name}");
                    }
                }
            }

            // 获取活动文档
            var activeDocProp = dteType.GetProperty("ActiveDocument");
            var activeDoc = activeDocProp?.GetValue(dte);
            
            if (activeDoc != null)
            {
                _logger.Information($"[Diagnostic] Active document: {activeDoc.GetType().FullName}");
                
                var docType = activeDoc.GetType();
                var props = docType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                
                foreach (var prop in props)
                {
                    _logger.Information($"[Diagnostic]   {prop.Name}: {prop.PropertyType.Name}");
                    
                    // 尝试获取文档对象
                    if (prop.Name == "Object" || prop.Name == "Document")
                    {
                        try
                        {
                            var docObj = prop.GetValue(activeDoc);
                            if (docObj != null)
                            {
                                _logger.Information($"[Diagnostic]     Document object: {docObj.GetType().FullName}");
                                ExploreConnectionObject(docObj);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"[Diagnostic]     Error: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Diagnostic] Failed to explore active window");
        }
    }

    /// <summary>
    /// 扫描部署目录下最新日志文件，提取 [ERR]/[FTL] 条目并写入临时文件，
    /// 供 CodeBuddy error-diagnosis Skill 读取诊断。
    /// VSCT: cmdidDiagnoseErrors = 0x0201（需在 .vsct 中注册）
    /// </summary>
    public void ScanLatestErrors()
    {
        try
        {
            // 1. 定位日志目录 → {部署目录}\Logs\
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var deployDir = Path.GetDirectoryName(assemblyLocation);
            var logDir = Path.Combine(deployDir ?? ".", "Logs");

            _logger.Information($"[Diagnostic] Scanning logs in: {logDir}");

            if (!Directory.Exists(logDir))
            {
                _logger.Warning($"[Diagnostic] Logs directory not found: {logDir}");
                ShowMessage("日志目录不存在，请先触发一次错误或确认插件已正确部署。\n\n" +
                            $"期望路径: {logDir}");
                return;
            }

            // 2. 找最新 .log 文件
            var logFiles = Directory.GetFiles(logDir, "ssmsboostplus-*.log");
            if (logFiles.Length == 0)
            {
                _logger.Warning("[Diagnostic] No log files found");
                ShowMessage($"日志目录中没有找到日志文件。\n\n路径: {logDir}");
                return;
            }

            var latestLog = logFiles
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .First();

            _logger.Information($"[Diagnostic] Latest log: {latestLog.FullName} ({latestLog.LastWriteTime})");

            // 3. 提取所有 [ERR] / [FTL] 行
            var errorLines = new List<string>();
            var allLines = File.ReadAllLines(latestLog.FullName);
            var captureBlock = false;
            var currentBlock = new List<string>();

            foreach (var line in allLines)
            {
                // 检测错误/致命级别行开始
                if (line.Contains("[ERR]") || line.Contains("[FTL]"))
                {
                    // 如果有未完成的块，先保存
                    if (currentBlock.Count > 0)
                    {
                        errorLines.Add(string.Join(Environment.NewLine, currentBlock));
                        currentBlock.Clear();
                    }
                    captureBlock = true;
                    currentBlock.Add(line);
                }
                else if (captureBlock)
                {
                    // 继续捕获堆栈行（缩进或 at 开头）
                    if (line.StartsWith("   at ") || line.StartsWith("System.") ||
                        line.StartsWith("Microsoft.") || line.TrimStart().StartsWith("at "))
                    {
                        currentBlock.Add(line);
                    }
                    else
                    {
                        // 堆栈结束，保存当前块
                        errorLines.Add(string.Join(Environment.NewLine, currentBlock));
                        currentBlock.Clear();
                        captureBlock = false;
                    }
                }
            }

            // 保存最后一块
            if (currentBlock.Count > 0)
            {
                errorLines.Add(string.Join(Environment.NewLine, currentBlock));
            }

            if (errorLines.Count == 0)
            {
                _logger.Information("[Diagnostic] No [ERR]/[FTL] entries found in latest log");
                ShowMessage("最新日志中没有发现错误或致命级别条目。\n\n" +
                            $"日志文件: {latestLog.Name}\n" +
                            $"更新时间: {latestLog.LastWriteTime}");
                return;
            }

            // 4. 写入临时摘要文件
            var diagnosisDir = Path.Combine(deployDir ?? ".", "diagnosis");
            Directory.CreateDirectory(diagnosisDir);
            var tempFile = Path.Combine(diagnosisDir, "temp-latest-errors.txt");

            var content = $"# 最新日志错误摘要\n" +
                          $"# 来源: {latestLog.FullName}\n" +
                          $"# 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"# 错误数: {errorLines.Count}\n" +
                          $"{new string('-', 60)}\n\n" +
                          string.Join($"\n{new string('-', 60)}\n\n", errorLines);

            File.WriteAllText(tempFile, content);

            _logger.Information($"[Diagnostic] Written {errorLines.Count} error blocks to {tempFile}");

            // 5. 弹出提示引导 CodeBuddy
            ShowMessage($"已扫描最新日志，发现 {errorLines.Count} 个错误条目。\n\n" +
                        $"请在 CodeBuddy 中说「诊断最新日志」即可开始自动诊断。\n\n" +
                        $"临时文件: {tempFile}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Diagnostic] Failed to scan latest errors");
            ShowMessage($"扫描日志失败: {ex.Message}\n\n请检查日志目录权限。");
        }
    }

    private void ShowMessage(string message)
    {
        VsShellUtilities.ShowMessageBox(
            Package,
            message,
            "SSMSBoostPlus Diagnostic",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
