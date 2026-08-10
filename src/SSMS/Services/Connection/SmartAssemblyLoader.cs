using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// 智能程序集加载器 - 自动适配不同SSMS版本的程序集名称变更
/// SSMS 22进行了重大重构，程序集名称发生变化
/// </summary>
public static class SmartAssemblyLoader
{
    private static readonly Dictionary<string, Assembly> _loadedAssemblies = new();
    private static readonly object _lock = new();
    private static ILoggerService? _logger;
    private static string? _ssmsPath;
    private static SSMSVersion _detectedVersion;

    /// <summary>
    /// 程序集逻辑名称到实际文件名的映射（按优先级排序）
    /// SSMS 22使用新名称，SSMS 18/19/20使用旧名称
    /// </summary>
    private static readonly Dictionary<string, string[]> AssemblyMappings = new()
    {
        ["VSIntegration"] = new[]
        {
            "Microsoft.SqlServer.SqlTools.VSIntegration.dll",        // SSMS 22+ 新名称
            "Microsoft.SqlServer.Management.UI.VSIntegration.dll"   // SSMS 18/19/20 旧名称
        },
        
        ["SqlStudioExplorer"] = new[]
        {
            "Microsoft.SqlServer.Management.Sdk.SqlStudio.dll",     // SSMS 22+ 新名称（合并）
            "Microsoft.SqlServer.Management.SqlStudio.Explorer.dll" // SSMS 18/19/20 旧名称
        },
        
        ["ObjectExplorer"] = new[]
        {
            "ObjectExplorer.dll",  // SSMS 22+ 独立程序集
            "Microsoft.SqlServer.Management.SqlStudio.Explorer.dll" // SSMS 旧版本备用
        },
        
        ["ConnectionDlg"] = new[]
        {
            "ConnectionDlg.dll",  // SSMS 22+
            "Microsoft.SqlServer.ConnectionDlg.UI.dll" // 旧版本
        },
        
        ["ConnectionInfo"] = new[]
        {
            "Microsoft.SqlServer.ConnectionInfo.dll"  // 所有版本通用
        },
        
        ["SdkSfc"] = new[]
        {
            "Microsoft.SqlServer.Management.Sdk.Sfc.dll"  // 所有版本通用
        },
        
        ["ConnectionUI"] = new[]
        {
            "Microsoft.SqlServer.Management.ConnectionUI.dll",
            "Microsoft.SqlServer.Management.ConnectionUI.Dialog.dll"
        }
    };

    /// <summary>
    /// 确保所有必需的程序集已加载
    /// </summary>
    public static void EnsureAssembliesLoaded(ILoggerService? logger = null)
    {
        if (_loadedAssemblies.Count > 0) return;

        lock (_lock)
        {
            if (_loadedAssemblies.Count > 0) return;

            _logger = logger;
            _logger?.Information("[SmartLoader] Starting intelligent assembly loading...");

            try
            {
                // 检测SSMS版本和路径
                _ssmsPath = DetectSsmsPath();
                _detectedVersion = DetectSsmsVersion(_ssmsPath);
                
                _logger?.Information($"[SmartLoader] Detected SSMS {_detectedVersion} at: {_ssmsPath}");

                // 按优先级加载关键程序集
                LoadAssemblyByKey("ConnectionInfo");      // 基础连接信息
                LoadAssemblyByKey("SdkSfc");              // SDK基础
                LoadAssemblyByKey("VSIntegration");       // VS集成（名称已变更）
                LoadAssemblyByKey("SqlStudioExplorer");   // SqlStudio Explorer（名称已变更）
                LoadAssemblyByKey("ObjectExplorer");      // 对象浏览器（SSMS 22独立）
                LoadAssemblyByKey("ConnectionDlg");       // 连接对话框
                LoadAssemblyByKey("ConnectionUI");        // 连接UI

                _logger?.Information($"[SmartLoader] ✅ Successfully loaded {_loadedAssemblies.Count} assemblies");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[SmartLoader] Failed to load assemblies");
            }
        }
    }

    /// <summary>
    /// 按逻辑名称加载程序集（自动尝试多个候选名称）
    /// </summary>
    private static Assembly? LoadAssemblyByKey(string logicalName)
    {
        if (!AssemblyMappings.TryGetValue(logicalName, out var candidates))
        {
            _logger?.Warning($"[SmartLoader] No mapping found for: {logicalName}");
            return null;
        }

        foreach (var fileName in candidates)
        {
            var assemblyPath = Path.Combine(_ssmsPath!, fileName);
            
            if (File.Exists(assemblyPath))
            {
                var assembly = LoadAssemblyFromFile(assemblyPath);
                if (assembly != null)
                {
                    _loadedAssemblies[logicalName] = assembly;
                    _logger?.Debug($"[SmartLoader] ✅ Loaded {logicalName}: {fileName}");
                    return assembly;
                }
            }
            else
            {
                _logger?.Debug($"[SmartLoader] Not found: {fileName}");
            }
        }

        _logger?.Warning($"[SmartLoader] ❌ Failed to load any candidate for: {logicalName}");
        return null;
    }

    /// <summary>
    /// 从文件加载程序集
    /// 优先使用 Assembly.Load(AssemblyName) 确保加载到正确的 CLR 绑定上下文，
    /// 回退到 Assembly.LoadFrom 作为兜底。
    ///
    /// 为什么这很重要：
    /// Assembly.LoadFrom() 将程序集加载到 LoadFrom Context，与主进程的 Load Context 隔离。
    /// 当 ServiceCache.ScriptFactory 内部尝试解析同一上下文的类型时，会抛出 NullReferenceException。
    /// Assembly.Load(AssemblyName) 先在 Load Context 中查找（SSMS 已加载的程序集），
    /// 确保类型在同一绑定上下文中，从而解决 NullReferenceException。
    /// </summary>
    private static Assembly? LoadAssemblyFromFile(string assemblyPath)
    {
        try
        {
            // 策略1：尝试 Assembly.Load(AssemblyName) — 使用 Load Context
            // 先获取程序集名称，然后在 Load Context 中查找
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            try
            {
                var assembly = Assembly.Load(assemblyName);
                _logger?.Debug($"[SmartLoader] Loaded via Assembly.Load: {assembly.FullName} (Load Context)");
                return assembly;
            }
            catch (FileNotFoundException)
            {
                // Load Context 中找不到，回退到 LoadFrom
                _logger?.Debug($"[SmartLoader] Assembly.Load failed for {assemblyName.FullName}, trying LoadFrom...");
            }

            // 策略2：回退到 Assembly.LoadFrom（LoadFrom Context）
            var assembly2 = Assembly.LoadFrom(assemblyPath);
            _logger?.Debug($"[SmartLoader] Loaded via LoadFrom: {assembly2.FullName} (LoadFrom Context — fallback)");
            return assembly2;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SmartLoader] Failed to load {assemblyPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取类型（从已加载的程序集中搜索）
    /// </summary>
    public static Type? GetType(string typeName, string? logicalAssemblyName = null)
    {
        try
        {
            // 1. 尝试直接获取
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // 2. 如果指定了逻辑程序集名称，从该程序集中获取
            if (!string.IsNullOrEmpty(logicalAssemblyName) && 
                _loadedAssemblies.TryGetValue(logicalAssemblyName, out var assembly))
            {
                type = assembly.GetType(typeName);
                if (type != null)
                {
                    _logger?.Debug($"[SmartLoader] Found type {typeName} in {assembly.GetName().Name}");
                    return type;
                }
            }

            // 3. 从所有已加载的程序集中搜索
            foreach (var asm in _loadedAssemblies.Values)
            {
                type = asm.GetType(typeName);
                if (type != null)
                {
                    _logger?.Debug($"[SmartLoader] Found type {typeName} in {asm.GetName().Name}");
                    return type;
                }
            }

            // 4. 从AppDomain的所有程序集中搜索
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(typeName);
                if (type != null)
                {
                    _logger?.Debug($"[SmartLoader] Found type {typeName} in AppDomain assembly {asm.GetName().Name}");
                    return type;
                }
            }

            _logger?.Debug($"[SmartLoader] Type not found: {typeName}");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[SmartLoader] Error getting type {typeName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取已加载的程序集
    /// </summary>
    public static Assembly? GetAssembly(string logicalName)
    {
        return _loadedAssemblies.TryGetValue(logicalName, out var assembly) ? assembly : null;
    }

    /// <summary>
    /// 获取检测到的SSMS版本
    /// </summary>
    public static SSMSVersion GetDetectedVersion()
    {
        if (_detectedVersion == SSMSVersion.Unknown)
        {
            _logger?.Debug("[SmartLoader] GetDetectedVersion: cache miss, re-detecting...");
            _ssmsPath ??= DetectSsmsPath();
            _detectedVersion = DetectSsmsVersion(_ssmsPath);
            _logger?.Debug($"[SmartLoader] GetDetectedVersion: detected={_detectedVersion}, path={_ssmsPath ?? "(empty)"}");
        }
        else
        {
            _logger?.Debug($"[SmartLoader] GetDetectedVersion: cache hit, version={_detectedVersion}");
        }
        return _detectedVersion;
    }

    /// <summary>
    /// 检测SSMS安装路径（IDE根目录，如 ...\Common7\IDE）
    /// 多策略回退：自身位置 → 默认安装路径 → 自定义路径 → 环境变量
    /// </summary>
    private static string DetectSsmsPath()
    {
        _logger?.Debug("[SmartLoader] DetectSsmsPath: starting path detection...");

        // 策略0: 从当前程序集部署位置反推（最可靠，不依赖安装路径）
        // 插件路径: {SSMS_IDE}\Extensions\SSMSBoostPlus\SSMSBoostPlus.SSMS.dll
        // 往上走 3 级到 {SSMS_IDE}
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[0] assembly location: {assemblyLocation}");

        var extensionsDir = Path.GetDirectoryName(assemblyLocation);           // ...\Extensions\SSMSBoostPlus
        var ideDir = Path.GetDirectoryName(Path.GetDirectoryName(extensionsDir)); // ...\IDE
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[0] derived IDE dir: {ideDir}");

        if (!string.IsNullOrEmpty(ideDir)
            && Directory.Exists(ideDir)
            && ideDir.EndsWith("IDE", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[0] SUCCESS: {ideDir}");
            return ideDir;
        }
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[0] FAILED: ideDir empty/invalid/not ending with IDE");

        // 策略1: SSMS 22（默认安装路径 — Program Files）
        var path22 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Microsoft SQL Server Management Studio 22\Release\Common7\IDE");
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[1] SSMS22 (ProgramFiles): {path22}");
        if (Directory.Exists(path22))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[1] SUCCESS: {path22}");
            return path22;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[1] FAILED: directory not found");

        // 策略2: SSMS 20（x86）
        var path20 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft SQL Server Management Studio 20\Common7\IDE");
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[2] SSMS20 (ProgramFilesX86): {path20}");
        if (Directory.Exists(path20))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[2] SUCCESS: {path20}");
            return path20;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[2] FAILED: directory not found");

        // 策略3: SSMS 19（x86）
        var path19 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft SQL Server Management Studio 19\Common7\IDE");
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[3] SSMS19 (ProgramFilesX86): {path19}");
        if (Directory.Exists(path19))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[3] SUCCESS: {path19}");
            return path19;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[3] FAILED: directory not found");

        // 策略4: SSMS 18（x86）
        var path18 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft SQL Server Management Studio 18\Common7\IDE");
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[4] SSMS18 (ProgramFilesX86): {path18}");
        if (Directory.Exists(path18))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[4] SUCCESS: {path18}");
            return path18;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[4] FAILED: directory not found");

        // 策略5: D 盘自定义安装（SSMS 22 常见）
        var path22D = @"D:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE";
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[5] SSMS22 D-drive: {path22D}");
        if (Directory.Exists(path22D))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[5] SUCCESS: {path22D}");
            return path22D;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[5] FAILED: directory not found");

        // 策略6: D 盘自定义安装（SSMS 20/19/18）
        var pathOtherD = @"D:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE";
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[6] SSMS20 D-drive (x86): {pathOtherD}");
        if (Directory.Exists(pathOtherD))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[6] SUCCESS: {pathOtherD}");
            return pathOtherD;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[6] FAILED: directory not found");

        // 策略7: 环境变量
        var envPath = Environment.GetEnvironmentVariable("SSMS_INSTALL_PATH");
        _logger?.Debug($"[SmartLoader] DetectSsmsPath[7] env SSMS_INSTALL_PATH: {envPath ?? "(not set)"}");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
        {
            _logger?.Information($"[SmartLoader] ✅ DetectSsmsPath[7] SUCCESS: {envPath}");
            return envPath;
        }
        _logger?.Debug("[SmartLoader] DetectSsmsPath[7] FAILED: env var not set or directory not found");

        _logger?.Warning("[SmartLoader] ❌ DetectSsmsPath: ALL 8 strategies FAILED");
        return string.Empty;
    }

    /// <summary>
    /// 检测SSMS版本（通过路径中的 "Management Studio XX" 关键词识别）
    /// </summary>
    private static SSMSVersion DetectSsmsVersion(string? path)
    {
        _logger?.Debug($"[SmartLoader] DetectSsmsVersion: input path='{path ?? "(null)"}'");

        if (string.IsNullOrEmpty(path))
        {
            _logger?.Warning("[SmartLoader] DetectSsmsVersion: SKIPPED (path is empty or null) → Unknown");
            return SSMSVersion.Unknown;
        }

        // 使用 OrdinalIgnoreCase，因 Assembly.GetExecutingAssembly().Location 可能返回全小写路径
        // net472 不支持 Contains(string, StringComparison)，改用 IndexOf
        if (path.IndexOf("Management Studio 22", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _logger?.Information($"[SmartLoader] DetectSsmsVersion: ✅ V22 (matched 'Management Studio 22' in path)");
            return SSMSVersion.V22;
        }
        if (path.IndexOf("Management Studio 20", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _logger?.Information($"[SmartLoader] DetectSsmsVersion: ✅ V20 (matched 'Management Studio 20' in path)");
            return SSMSVersion.V20;
        }
        if (path.IndexOf("Management Studio 19", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _logger?.Information($"[SmartLoader] DetectSsmsVersion: ✅ V19 (matched 'Management Studio 19' in path)");
            return SSMSVersion.V19;
        }
        if (path.IndexOf("Management Studio 18", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _logger?.Information($"[SmartLoader] DetectSsmsVersion: ✅ V18 (matched 'Management Studio 18' in path)");
            return SSMSVersion.V18;
        }

        _logger?.Warning($"[SmartLoader] DetectSsmsVersion: ❌ Unknown (no 'Management Studio XX' found in path)");
        return SSMSVersion.Unknown;
    }
}

/// <summary>
/// SSMS版本枚举
/// </summary>
public enum SSMSVersion
{
    Unknown,
    V18,
    V19,
    V20,
    V22
}
