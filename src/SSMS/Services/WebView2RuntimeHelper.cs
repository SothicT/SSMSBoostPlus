using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services;

/// <summary>
/// WebView2 Runtime 路径查找共享工具类。
/// 当 EdgeUpdate 注册表缺失时，从文件系统直接定位 Runtime 安装路径作为兜底方案。
/// </summary>
internal static class WebView2RuntimeHelper
{
    /// <summary>
    /// WebView2 Runtime 版本目录名正则，匹配格式如 131.0.2903.86 / 150.0.4078.48。
    /// 用于排除 SetupMetrics 等非版本目录。
    /// </summary>
    private static readonly Regex VersionDirPattern = new(
        @"^\d+\.\d+\.\d+\.\d+$",
        RegexOptions.Compiled);

    /// <summary>
    /// 尝试从文件系统查找 WebView2 Runtime 路径。
    /// 查找优先级：WEBVIEW2_RUNTIME_PATH 环境变量 → 常见安装路径下最新版本目录。
    /// </summary>
    /// <param name="logger">可选的日志记录器，用于输出诊断信息</param>
    /// <returns>Runtime 路径，未找到则返回 null（由 SDK 使用默认查找逻辑）</returns>
    internal static string? TryFindWebView2RuntimePath(ILoggerService? logger = null)
    {
        try
        {
            // 1. 优先检查环境变量
            var envPath = Environment.GetEnvironmentVariable("WEBVIEW2_RUNTIME_PATH", EnvironmentVariableTarget.Machine)
                       ?? Environment.GetEnvironmentVariable("WEBVIEW2_RUNTIME_PATH", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
            {
                logger?.Information($"[WebView2RuntimeHelper] 使用环境变量路径: {envPath}");
                return envPath;
            }

            // 2. 扫描常见安装路径
            var searchPaths = new[]
            {
                @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application",
                @"C:\Program Files\Microsoft\EdgeWebView\Application"
            };

            foreach (var basePath in searchPaths)
            {
                if (!Directory.Exists(basePath))
                {
                    logger?.Debug($"[WebView2RuntimeHelper] 路径不存在: {basePath}");
                    continue;
                }

                // 获取所有子目录列表（用于日志）
                var allDirs = Directory.GetDirectories(basePath)
                    .Select(Path.GetFileName)
                    .ToArray();
                logger?.Debug($"[WebView2RuntimeHelper] 扫描 {basePath}，发现子目录: [{string.Join(", ", allDirs)}]");

                // 只匹配版本号格式的目录（如 150.0.4078.48），排除 SetupMetrics 等非版本目录
                var versionDirs = allDirs
                    .Where(name => VersionDirPattern.IsMatch(name))
                    .Select(name => new
                    {
                        Name = name,
                        FullPath = Path.Combine(basePath, name),
                        Version = Version.TryParse(name, out var v) ? v : new Version(0, 0, 0, 0)
                    })
                    .OrderByDescending(x => x.Version)  // 语义版本排序（非字母排序）
                    .ToArray();

                if (versionDirs.Length == 0)
                {
                    logger?.Warning($"[WebView2RuntimeHelper] 路径 {basePath} 下无有效版本目录（非版本目录已排除）");
                    continue;
                }

                // 从最高版本开始，校验是否包含运行时二进制文件
                foreach (var dir in versionDirs)
                {
                    var runtimeExe = Path.Combine(dir.FullPath, "msedgewebview2.exe");
                    if (File.Exists(runtimeExe))
                    {
                        logger?.Information($"[WebView2RuntimeHelper] 找到有效 Runtime 路径: {dir.FullPath} (版本 {dir.Version})");
                        return dir.FullPath;
                    }

                    logger?.Warning($"[WebView2RuntimeHelper] 版本目录 {dir.FullPath} 缺少 msedgewebview2.exe，跳过");
                }

                logger?.Warning($"[WebView2RuntimeHelper] 路径 {basePath} 下所有版本目录均缺少运行时二进制文件");
            }

            logger?.Warning("[WebView2RuntimeHelper] 未找到 WebView2 Runtime 路径");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[WebView2RuntimeHelper] 查找 Runtime 路径异常");
        }

        return null;
    }
}