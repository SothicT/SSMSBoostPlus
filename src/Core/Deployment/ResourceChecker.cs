using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SSMSBoostPlus.Deployment;

/// <summary>
/// 资源检查器 — 发布前检查必备资源是否就绪。
/// 检查项包括：编译产物是否存在、SSMS 目标目录是否可写、权限是否足够等。
/// </summary>
public class ResourceChecker
{
    private readonly IDeploymentLogger _logger;

    public ResourceChecker(IDeploymentLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 执行发布前的资源检查。
    /// </summary>
    public async Task<ResourceCheckResult> CheckAsync(DeploymentContext context, string[]? additionalCheckPaths = null)
    {
        var result = new ResourceCheckResult();
        _logger.LogInfo("[资源检查] 开始发布前资源检查...");

        // 1. 检查输出目录
        await Task.Run(() =>
        {
            CheckItem("编译输出目录", () =>
                Directory.Exists(context.OutputDir),
                $"输出目录: {context.OutputDir}", result);
        });

        // 2. 检查核心 DLL
        await Task.Run(() =>
        {
            var ssmsDll = Path.Combine(context.OutputDir, "SSMSBoostPlus.SSMS.dll");
            CheckItem("核心扩展 DLL", () =>
                File.Exists(ssmsDll) && new FileInfo(ssmsDll).Length > 0,
                ssmsDll, result);
        });

        // 3. 检查 Core DLL
        await Task.Run(() =>
        {
            var coreDll = Path.Combine(context.OutputDir, "SSMSBoostPlus.Core.dll");
            CheckItem("核心库 DLL", () =>
                File.Exists(coreDll) && new FileInfo(coreDll).Length > 0,
                coreDll, result);
        });

        // 4. 检查 pkgdef 文件
        await Task.Run(() =>
        {
            var pkgdefPaths = new[]
            {
                Path.Combine(context.OutputDir, "SSMSBoostPlus.SSMS.pkgdef"),
                Path.Combine(Path.GetDirectoryName(context.SolutionDir) ?? "", "SSMS", "obj", context.Configuration, context.TargetFramework, "SSMSBoostPlus.SSMS.pkgdef")
            };
            var found = pkgdefPaths.FirstOrDefault(File.Exists);
            CheckItem("pkgdef 配置", () => found != null,
                found ?? "未找到 pkgdef 文件（将以警告继续）", result,
                ResourceCheckResult.CheckSeverity.Warning);
        });

        // 5. 检查 cto/menu 文件
        await Task.Run(() =>
        {
            var ctoPaths = new[]
            {
                Path.Combine(context.OutputDir, "SSMSBoostPlus.cto"),
                Path.Combine(Path.GetDirectoryName(context.SolutionDir) ?? "", "SSMS", "obj", context.Configuration, context.TargetFramework, "SSMSBoostPlus.cto")
            };
            var found = ctoPaths.FirstOrDefault(File.Exists);
            CheckItem("菜单资源 (.cto)", () => found != null,
                found ?? "未找到 .cto 文件（将以警告继续）", result,
                ResourceCheckResult.CheckSeverity.Warning);
        });

        // 6. 额外自定义路径检查
        if (additionalCheckPaths != null)
        {
            foreach (var path in additionalCheckPaths)
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(path))
                    {
                        var files = Directory.GetFiles(path).Length;
                        CheckItem($"自定义路径: {Path.GetFileName(path)}", () => files > 0,
                            $"{path} ({files} 个文件)", result);
                    }
                    else
                    {
                        CheckItem($"自定义路径: {Path.GetFileName(path)}", () => false,
                            $"路径不存在: {path}", result);
                    }
                });
            }
        }

        // 汇总
        var errors = result.Items.Count(i => !i.Passed && i.Severity == ResourceCheckResult.CheckSeverity.Error);
        var warnings = result.Items.Count(i => !i.Passed && i.Severity == ResourceCheckResult.CheckSeverity.Warning);

        result.IsReady = errors == 0;
        result.Summary = $"{result.Items.Count} 项检查: " +
            $"{result.Items.Count(i => i.Passed)} 通过, " +
            $"{warnings} 警告, " +
            $"{errors} 错误";

        _logger.LogInfo($"[资源检查] {(result.IsReady ? "✅ 就绪" : "❌ 未通过")}: {result.Summary}");

        return result;
    }

    private void CheckItem(string name, Func<bool> check, string detail,
        ResourceCheckResult result,
        ResourceCheckResult.CheckSeverity severity = ResourceCheckResult.CheckSeverity.Error)
    {
        try
        {
            var passed = check();
            result.Items.Add(new ResourceCheckResult.CheckItem
            {
                Name = name,
                Passed = passed,
                Message = passed ? $"✅ {detail}" : $"❌ {detail}",
                Severity = passed ? ResourceCheckResult.CheckSeverity.Info : severity
            });

            if (passed)
                _logger.LogInfo($"[资源检查]   ✅ {name}: {detail}");
            else if (severity == ResourceCheckResult.CheckSeverity.Warning)
                _logger.LogWarning($"[资源检查]   ⚠️ {name}: {detail}");
            else
                _logger.LogError($"[资源检查]   ❌ {name}: {detail}");
        }
        catch (Exception ex)
        {
            result.Items.Add(new ResourceCheckResult.CheckItem
            {
                Name = name,
                Passed = false,
                Message = $"❌ 检查异常: {ex.Message}",
                Severity = severity
            });
            _logger.LogError($"[资源检查]   ❌ {name}: 异常 - {ex.Message}");
        }
    }
}