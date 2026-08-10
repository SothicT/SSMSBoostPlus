using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 版本检查服务 — 查询 Release 分支最新 commit，与本地版本对比。
/// 使用 git ls-remote 获取远程最新 hash，git log 获取 commit 差异。
/// 版本信息存储在扩展目录的 version_info.json 中。
/// </summary>
public class VersionCheckService
{
    private readonly string _configDir;
    private readonly string _versionFilePath;
    private readonly Action<string>? _logger;

    // 仓库 URL（内部使用，不向用户展示）
    private const string RepoUrl = "http://172.16.10.168/AI项目集合/SSMSBoostPlus/_git/SSMSBoostPlus";

    public VersionCheckService(string configDir, Action<string>? logger = null)
    {
        _configDir = configDir;
        _versionFilePath = Path.Combine(configDir, "version_info.json");
        _logger = logger;
    }

    /// <summary>
    /// 检查是否需要自动检测更新（距上次检查超过 7 天且当前时间 >= 12:00）。
    /// </summary>
    public bool ShouldCheckUpdate()
    {
        try
        {
            var info = LoadVersionInfo();
            if (info == null) return true;

            if (DateTime.Now - info.LastCheckTime >= TimeSpan.FromDays(7))
            {
                if (DateTime.Now.Hour >= 12)
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 获取 Release 分支最新的 commit hash。
    /// </summary>
    public async Task<string?> GetLatestReleaseHashAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"ls-remote \"{RepoUrl}\" Release",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                Log("[VersionCheck] git 进程启动失败");
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            process.WaitForExit(15000);

            if (process.ExitCode != 0)
            {
                Log($"[VersionCheck] git ls-remote 失败: {error.Trim()}");
                return null;
            }

            var parts = output.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                return parts[0].Trim();

            Log("[VersionCheck] 无法解析 git ls-remote 输出");
            return null;
        }
        catch (Exception ex)
        {
            Log($"[VersionCheck] 获取远程版本失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取从本地版本到远程版本之间的 commit 列表。
    /// 在临时 git 仓库中执行，避免依赖 SSMS 扩展目录是否有 .git。
    /// </summary>
    public async Task<List<string>> GetCommitsSinceAsync(string localHash, string remoteHash)
    {
        var commits = new List<string>();
        var tempDir = Path.Combine(Path.GetTempPath(), "SSMSBoostPlus_Git_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);

            // 1. 初始化临时 git 仓库
            var (initCode, _) = await RunGitProcessAsync(tempDir, "init", 5000);
            if (initCode != 0)
            {
                Log("[VersionCheck] 无法创建临时 git 仓库");
                return commits;
            }

            // 2. 从远程仓库浅层获取 Release 分支（最近 50 个 commit）
            var (fetchCode, fetchErr) = await RunGitProcessAsync(tempDir,
                $"fetch --depth 50 \"{RepoUrl}\" Release", 60000);

            if (fetchCode != 0)
            {
                Log($"[VersionCheck] git fetch 失败: {fetchErr.Trim()}");
                // 兜底：尝试用 ls-remote 获取最新 commit 信息
                commits.Add($"远程版本: {remoteHash.Substring(0, 8)}");
                return commits;
            }

            // 3. 使用 git log 对比本地版本和远程 FETCH_HEAD
            var (logCode, logOutput) = await RunGitProcessAsync(tempDir,
                $"log \"{localHash}..FETCH_HEAD\" --oneline --no-color", 15000);

            if (logCode != 0 || string.IsNullOrWhiteSpace(logOutput))
            {
                // git log 失败（例如 localHash 不在历史中），改用 git log FETCH_HEAD 显示最近提交
                Log("[VersionCheck] git log 对比失败，使用最近提交列表");
                var (recentCode, recentOutput) = await RunGitProcessAsync(tempDir,
                    "log FETCH_HEAD --oneline --no-color -20", 15000);

                if (recentCode == 0 && !string.IsNullOrWhiteSpace(recentOutput))
                {
                    foreach (var line in recentOutput.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                            commits.Add(trimmed);
                    }
                }
                else
                {
                    commits.Add($"远程版本: {remoteHash.Substring(0, 8)}");
                }
                return commits;
            }

            // 4. 解析输出
            foreach (var line in logOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    commits.Add(trimmed);
            }
        }
        catch (Exception ex)
        {
            Log($"[VersionCheck] 获取 commit 列表失败: {ex.Message}");
            if (!string.IsNullOrEmpty(remoteHash))
                commits.Add($"远程版本: {remoteHash.Substring(0, 8)}");
        }
        finally
        {
            // 清理临时目录
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* 忽略清理错误 */ }
        }

        return commits;
    }

    /// <summary>
    /// 在指定工作目录中运行 git 命令，返回 (退出码, 输出文本)。
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunGitProcessAsync(
        string workingDir, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, "进程启动失败");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            process.WaitForExit(timeoutMs);

            return (process.ExitCode, output + error);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public string? GetLocalVersionHash()
    {
        var info = LoadVersionInfo();
        return info?.LocalHash;
    }

    public void StoreVersion(string hash)
    {
        try
        {
            var info = new VersionInfo { LocalHash = hash, LastCheckTime = DateTime.Now };
            var json = JsonConvert.SerializeObject(info, Formatting.Indented);
            File.WriteAllText(_versionFilePath, json);
            Log($"[VersionCheck] 版本已保存: {hash?.Substring(0, 8)}");
        }
        catch (Exception ex)
        {
            Log($"[VersionCheck] 保存版本信息失败: {ex.Message}");
        }
    }

    public void UpdateLastCheckTime()
    {
        try
        {
            var info = LoadVersionInfo() ?? new VersionInfo();
            info.LastCheckTime = DateTime.Now;
            var json = JsonConvert.SerializeObject(info, Formatting.Indented);
            File.WriteAllText(_versionFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"[VersionCheck] 更新检查时间失败: {ex.Message}");
        }
    }

    internal static string GetRepoUrl() => RepoUrl;

    private VersionInfo? LoadVersionInfo()
    {
        try
        {
            if (!File.Exists(_versionFilePath)) return null;
            var json = File.ReadAllText(_versionFilePath);
            return JsonConvert.DeserializeObject<VersionInfo>(json);
        }
        catch { return null; }
    }

    private void Log(string message)
    {
        Debug.WriteLine(message);
        _logger?.Invoke(message);
    }

    private class VersionInfo
    {
        [JsonProperty("localHash")]
        public string? LocalHash { get; set; }

        [JsonProperty("lastCheckTime")]
        public DateTime LastCheckTime { get; set; }
    }
}