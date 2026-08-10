using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SSMSBoostPlus.Deployment.Targets;

/// <summary>
/// 直接部署到 SSMS 扩展目录。
/// 将编译产物复制到 SSMS 的 Extensions\SSMSBoostPlus 目录。
/// 对应现有 deploy-to-ssms.bat 的逻辑。
/// </summary>
public class SsmsDirectDeploy : IDeploymentTarget
{
    private readonly IDeploymentLogger _logger;
    private readonly ResourceChecker _resourceChecker;

    public string DisplayName => "直接部署到 SSMS";
    public string Id => "ssms-direct";
    public string Description => "将编译产物直接复制到 SSMS 的 Extensions 目录，适用于开发调试";

    public SsmsDirectDeploy(IDeploymentLogger logger, ResourceChecker resourceChecker)
    {
        _logger = logger;
        _resourceChecker = resourceChecker;
    }

    public async Task<ResourceCheckResult> CheckAvailabilityAsync()
    {
        var result = new ResourceCheckResult();

        // 检查 SSMS 安装目录是否存在
        var ssmsDirs = new[]
        {
            @"D:\Program Files\Microsoft SQL Server Management Studio 22",
            @"C:\Program Files\Microsoft SQL Server Management Studio 22",
            @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 22"
        };

        var ssmsDir = ssmsDirs.FirstOrDefault(Directory.Exists);
        result.Items.Add(new ResourceCheckResult.CheckItem
        {
            Name = "SSMS 安装目录",
            Passed = ssmsDir != null,
            Message = ssmsDir != null ? $"✅ {ssmsDir}" : "❌ SSMS 22 未安装",
            Severity = ResourceCheckResult.CheckSeverity.Error
        });

        // 检查管理员权限（通过检测能否在 Program Files 下创建测试文件）
        bool isAdmin = false;
        try
        {
            var testPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), ".deploy_test");
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            isAdmin = true;
        }
        catch
        {
            isAdmin = false;
        }

        result.Items.Add(new ResourceCheckResult.CheckItem
        {
            Name = "管理员权限",
            Passed = isAdmin,
            Message = isAdmin ? "✅ 已以管理员身份运行" : "❌ 需要管理员权限才能写入 SSMS 目录",
            Severity = ResourceCheckResult.CheckSeverity.Error
        });

        result.IsReady = result.Items.All(i => i.Passed);
        result.Summary = $"SSMS 直接部署: {(result.IsReady ? "可用" : "不可用")}";
        return result;
    }

    public async Task<DeploymentResult> DeployAsync(
        DeploymentContext context,
        IProgress<DeploymentProgress> progress,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new DeploymentResult();

        try
        {
            // 先执行资源检查
            progress.Report(new DeploymentProgress
            { StepName = "资源检查", PercentComplete = 0, StatusMessage = "检查编译产物和 SSMS 目录..." });

            var resourceCheck = await _resourceChecker.CheckAsync(context);
            if (!resourceCheck.IsReady)
            {
                var errors = resourceCheck.Items
                    .Where(i => !i.Passed && i.Severity == ResourceCheckResult.CheckSeverity.Error)
                    .Select(i => i.Message);
                result.Success = false;
                result.ErrorMessage = $"资源检查未通过:\n{string.Join("\n", errors)}";
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // === 步骤1: 清理冲突目录 ===
            progress.Report(new DeploymentProgress
            { StepName = "清理冲突目录", PercentComplete = 10, StatusMessage = "检查并清理 commonextensions 冲突目录..." });

            var ssmsBaseDir = GetSsmsBaseDir();
            var extDir = Path.Combine(ssmsBaseDir, "Release", "Common7", "IDE", "Extensions", "SSMSBoostPlus");
            var commonExtDir = Path.Combine(ssmsBaseDir, "Release", "Common7", "IDE", "commonextensions", "SSMSBoostPlus");

            if (Directory.Exists(commonExtDir))
            {
                Directory.Delete(commonExtDir, true);
                _logger.LogInfo($"[部署] 已清理冲突目录: {commonExtDir}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // === 步骤2: 创建目标目录 ===
            progress.Report(new DeploymentProgress
            { StepName = "创建目标目录", PercentComplete = 25, StatusMessage = $"创建: {extDir}" });

            Directory.CreateDirectory(extDir);

            // === 步骤3: 复制核心文件 ===
            progress.Report(new DeploymentProgress
            { StepName = "复制核心文件", PercentComplete = 40, StatusMessage = "复制 SSMSBoostPlus.SSMS.dll 等核心文件..." });

            var filesToCopy = new[]
            {
                "SSMSBoostPlus.SSMS.dll",
                "SSMSBoostPlus.Core.dll",
            };

            foreach (var file in filesToCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(context, file, extDir, result);
            }

            // PDB 文件（可选）
            if (context.IncludePdbFiles)
            {
                var pdbFiles = new[] { "SSMSBoostPlus.SSMS.pdb", "SSMSBoostPlus.Core.pdb" };
                foreach (var pdb in pdbFiles)
                {
                    CopyFile(context, pdb, extDir, result, optional: true);
                }
            }

            // === 步骤4: 复制配置文件 ===
            progress.Report(new DeploymentProgress
            { StepName = "复制配置文件", PercentComplete = 60, StatusMessage = "复制 pkgdef、cto、manifest 等配置..." });

            CopyConfigFile(context, extDir, "SSMSBoostPlus.SSMS.pkgdef", result);
            CopyConfigFile(context, extDir, "SSMSBoostPlus.cto", result, targetName: "Menus.ctmenu");
            CopyVsixManifest(context, extDir, result);

            cancellationToken.ThrowIfCancellationRequested();

            // === 步骤5: 复制依赖项 ===
            progress.Report(new DeploymentProgress
            { StepName = "复制依赖项", PercentComplete = 80, StatusMessage = "复制 Serilog、DiffPlex 等依赖 DLL..." });

            var dependencies = new[]
            {
                "DiffPlex.dll", "Serilog.dll", "Serilog.Sinks.File.dll",
                "Microsoft.Data.SqlClient.dll", "Newtonsoft.Json.dll",
                "Microsoft.Extensions.Configuration.dll",
                "Microsoft.Extensions.Configuration.Abstractions.dll",
                "Microsoft.Extensions.Configuration.Binder.dll",
                "Microsoft.Extensions.Configuration.FileExtensions.dll",
                "Microsoft.Extensions.Configuration.Json.dll",
                "Microsoft.Extensions.FileProviders.Abstractions.dll",
                "Microsoft.Extensions.FileProviders.Physical.dll",
                "Microsoft.Extensions.FileSystemGlobbing.dll",
                "Microsoft.Extensions.Primitives.dll",
                "Microsoft.Bcl.AsyncInterfaces.dll",
                "Microsoft.IO.Redist.dll",
                "System.Memory.dll", "System.Runtime.CompilerServices.Unsafe.dll",
                "System.Text.Json.dll", "System.Threading.Tasks.Extensions.dll",
                "System.ValueTuple.dll",
                "Azure.Core.dll", "Azure.Identity.dll",
                "Microsoft.Identity.Client.dll",
                "Microsoft.Identity.Client.Extensions.Msal.dll",
                "Microsoft.IdentityModel.Abstractions.dll",
                "Microsoft.IdentityModel.JsonWebTokens.dll",
                "Microsoft.IdentityModel.Logging.dll",
                "Microsoft.IdentityModel.Protocols.dll",
                "Microsoft.IdentityModel.Protocols.OpenIdConnect.dll",
                "Microsoft.IdentityModel.Tokens.dll"
            };

            foreach (var dep in dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(context, dep, extDir, result, optional: true);
            }

            // === 步骤6: 刷新配置 ===
            progress.Report(new DeploymentProgress
            { StepName = "刷新配置", PercentComplete = 95, StatusMessage = "创建 extensions.configurationchanged 标记..." });

            var configFlag = Path.Combine(
                Path.GetDirectoryName(extDir) ?? "", "extensions.configurationchanged");
            File.WriteAllText(configFlag, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            sw.Stop();
            result.Success = true;
            result.TargetPath = extDir;
            result.ElapsedMs = sw.ElapsedMilliseconds;

            _logger.LogInfo($"[部署] ✅ 直接部署到 SSMS 完成! (耗时: {result.ElapsedMs}ms)");
            _logger.LogInfo($"[部署]   目标目录: {extDir}");
            _logger.LogInfo($"[部署]   已部署 {result.DeployedFiles.Count} 个文件");

            progress.Report(new DeploymentProgress
            { StepName = "部署完成", PercentComplete = 100, StatusMessage = $"✅ 部署成功! 已部署 {result.DeployedFiles.Count} 个文件到 {extDir}" });
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            result.Success = false;
            result.ErrorMessage = "部署已被用户取消";
            _logger.LogWarning("[部署] ⚠️ 部署被取消");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.ErrorMessage = $"部署异常: {ex.Message}";
            _logger.LogError($"[部署] ❌ {result.ErrorMessage}");
        }

        return result;
    }

    private string GetSsmsBaseDir()
    {
        var candidates = new[]
        {
            @"D:\Program Files\Microsoft SQL Server Management Studio 22",
            @"C:\Program Files\Microsoft SQL Server Management Studio 22",
            @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 22"
        };

        return candidates.First(Directory.Exists);
    }

    private void CopyFile(DeploymentContext context, string fileName, string targetDir,
        DeploymentResult result, bool optional = false)
    {
        var src = Path.Combine(context.OutputDir, fileName);
        if (!File.Exists(src))
        {
            if (!optional)
                _logger.LogError($"[部署] ❌ 源文件不存在: {fileName}");
            return;
        }

        var dest = Path.Combine(targetDir, fileName);
        File.Copy(src, dest, overwrite: true);
        result.DeployedFiles.Add(fileName);
        _logger.LogDebug($"[部署]   ✅ {fileName} → {dest}");
    }

    private void CopyConfigFile(DeploymentContext context, string targetDir,
        string fileName, DeploymentResult result, string? targetName = null)
    {
        var solutionDir = Path.GetDirectoryName(context.SolutionDir) ?? "";
        var objDir = Path.Combine(solutionDir, "SSMS", "obj", context.Configuration, context.TargetFramework);
        var src = Path.Combine(objDir, fileName);

        if (!File.Exists(src))
        {
            // 尝试不带 TFM 的路径
            src = Path.Combine(solutionDir, "SSMS", "obj", context.Configuration, fileName);
        }

        if (!File.Exists(src))
        {
            _logger.LogWarning($"[部署] ⚠️ 配置文件未找到: {fileName} (跳过)");
            return;
        }

        var destName = targetName ?? fileName;
        var dest = Path.Combine(targetDir, destName);
        File.Copy(src, dest, overwrite: true);
        result.DeployedFiles.Add(destName);
        _logger.LogDebug($"[部署]   ✅ {destName} → {dest}");
    }

    private void CopyVsixManifest(DeploymentContext context, string targetDir,
        DeploymentResult result)
    {
        var solutionDir = Path.GetDirectoryName(context.SolutionDir) ?? "";
        var src = Path.Combine(solutionDir, "SSMS", "source.extension.vsixmanifest");

        if (!File.Exists(src))
        {
            _logger.LogWarning("[部署] ⚠️ extension.vsixmanifest 未找到 (跳过)");
            return;
        }

        var dest = Path.Combine(targetDir, "extension.vsixmanifest");
        File.Copy(src, dest, overwrite: true);
        result.DeployedFiles.Add("extension.vsixmanifest");
        _logger.LogDebug($"[部署]   ✅ extension.vsixmanifest → {dest}");
    }
}