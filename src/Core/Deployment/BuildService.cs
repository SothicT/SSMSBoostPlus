using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SSMSBoostPlus.Deployment;

/// <summary>
/// 编译服务 — 负责语法检查和编译验证。
/// 封装 MSBuild 调用，提供编译结果的详细报告。
/// </summary>
public class BuildService
{
    private readonly IDeploymentLogger _logger;

    /// <summary>MSBuild 可执行文件路径</summary>
    public string MsBuildPath { get; set; }

    public BuildService(IDeploymentLogger logger, string? msBuildPath = null)
    {
        _logger = logger;
        MsBuildPath = msBuildPath ?? GetDefaultMsBuildPath();
    }

    /// <summary>
    /// 执行编译并返回结果。
    /// </summary>
    public async Task<BuildResult> BuildAsync(string solutionPath, string configuration = "Release")
    {
        var result = new BuildResult
        {
            Configuration = configuration,
            StartTime = DateTime.Now
        };

        _logger.LogInfo($"[编译] 开始编译: {Path.GetFileName(solutionPath)} ({configuration})");
        _logger.LogInfo($"[编译] MSBuild: {MsBuildPath}");

        // 检查解决方案文件
        if (!File.Exists(solutionPath))
        {
            result.Success = false;
            result.ErrorMessage = $"解决方案文件不存在: {solutionPath}";
            _logger.LogError($"[编译] {result.ErrorMessage}");
            return result;
        }

        // 检查 MSBuild
        if (!File.Exists(MsBuildPath))
        {
            result.Success = false;
            result.ErrorMessage = $"MSBuild 未找到: {MsBuildPath}";
            _logger.LogError($"[编译] {result.ErrorMessage}");
            return result;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = MsBuildPath,
                    Arguments = $"\"{solutionPath}\" -p:Configuration={configuration} -t:Build -m -nologo -v:minimal",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    _logger.LogDebug($"[MSBuild] {e.Data}");
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                    _logger.LogWarning($"[MSBuild-ERR] {e.Data}");
                }
            };

            var sw = Stopwatch.StartNew();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.RawOutput = outputBuilder.ToString();
            result.RawErrors = errorBuilder.ToString();

            result.Success = process.ExitCode == 0;
            result.ExitCode = process.ExitCode;

            if (result.Success)
            {
                _logger.LogInfo($"[编译] ✅ 编译成功 (耗时: {result.ElapsedMs}ms)");
            }
            else
            {
                result.ErrorMessage = $"编译失败，退出码: {process.ExitCode}";
                _logger.LogError($"[编译] ❌ {result.ErrorMessage}");
                ParseBuildErrors(result);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"编译过程异常: {ex.Message}";
            _logger.LogError($"[编译] ❌ {result.ErrorMessage}");
        }

        result.EndTime = DateTime.Now;
        return result;
    }

    /// <summary>
    /// 验证输出目录的关键 DLL 是否已生成。
    /// </summary>
    public bool VerifyOutputFiles(string outputDir, params string[] requiredDlls)
    {
        if (!Directory.Exists(outputDir))
        {
            _logger.LogError($"[验证] 输出目录不存在: {outputDir}");
            return false;
        }

        bool allFound = true;
        foreach (var dll in requiredDlls)
        {
            var path = Path.Combine(outputDir, dll);
            if (!File.Exists(path))
            {
                _logger.LogError($"[验证] ❌ 缺少必要 DLL: {dll}");
                allFound = false;
            }
            else
            {
                var size = new FileInfo(path).Length;
                _logger.LogInfo($"[验证] ✅ {dll} ({size:N0} bytes)");
            }
        }

        return allFound;
    }

    private void ParseBuildErrors(BuildResult result)
    {
        // 从 MSBuild 输出中提取错误信息
        var rawErrors = result.RawErrors ?? "";
        if (rawErrors.Length > 0)
        {
            foreach (var line in rawErrors.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.ErrorDetails.Add(line.Trim());
                }
            }
        }
    }

    private static string GetDefaultMsBuildPath()
    {
        // 按优先级尝试多个可能路径
        var candidates = new[]
        {
            @"D:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return candidates[0]; // 返回默认值，调用方会检查
    }
}

/// <summary>
/// 编译结果。
/// </summary>
public class BuildResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string Configuration { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long ElapsedMs { get; set; }
    public string? RawOutput { get; set; }
    public string? RawErrors { get; set; }
    public List<string> ErrorDetails { get; set; } = new();

    public bool HasErrors => !Success || ErrorDetails.Count > 0;
}