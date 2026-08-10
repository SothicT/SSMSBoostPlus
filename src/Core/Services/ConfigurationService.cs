using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.Services;

/// <summary>
/// Configuration service implementation using JSON files for complex configuration
/// and supporting VS Settings for simple configuration.
/// </summary>
public class ConfigurationService : IConfigurationService
{
    private readonly ILoggerService _logger;
    private readonly string _configDirectory;
    private readonly string _aiConfigPath;
    private readonly string _versionGuardConfigPath;
    private readonly string _loggingConfigPath;
    private readonly string _localHistoryConfigPath;
    private readonly string _completionConfigPath;

    private AIConfiguration? _cachedAIConfig;
    private VersionGuardConfiguration? _cachedVersionGuardConfig;
    private LoggingConfiguration? _cachedLoggingConfig;
    private LocalHistoryConfig? _cachedLocalHistoryConfig;
    private CompletionConfiguration? _cachedCompletionConfig;

    public ConfigurationService(ILoggerService logger, string configDirectory)
    {
        _logger = logger;
        _configDirectory = configDirectory;
        _aiConfigPath = Path.Combine(configDirectory, "ai-config.json");
        _versionGuardConfigPath = Path.Combine(configDirectory, "versionguard-config.json");
        _loggingConfigPath = Path.Combine(configDirectory, "logging-config.json");
        _localHistoryConfigPath = Path.Combine(configDirectory, "localhistory-config.json");
        _completionConfigPath = Path.Combine(configDirectory, "completion-config.json");

        EnsureConfigDirectoryExists();
    }

    /// <inheritdoc />
    public async Task<AIConfiguration> GetAIConfigurationAsync()
    {
        if (_cachedAIConfig != null)
            return _cachedAIConfig;

        _cachedAIConfig = await LoadConfigAsync<AIConfiguration>(_aiConfigPath) ?? new AIConfiguration();
        return _cachedAIConfig;
    }

    /// <inheritdoc />
    public async Task SaveAIConfigurationAsync(AIConfiguration config)
    {
        await SaveConfigAsync(_aiConfigPath, config);
        _cachedAIConfig = config;
        _logger.Information("AI 配置已保存。");
    }

    /// <inheritdoc />
    public async Task<VersionGuardConfiguration> GetVersionGuardConfigurationAsync()
    {
        if (_cachedVersionGuardConfig != null)
            return _cachedVersionGuardConfig;

        _cachedVersionGuardConfig = await LoadConfigAsync<VersionGuardConfiguration>(_versionGuardConfigPath) ?? new VersionGuardConfiguration();
        return _cachedVersionGuardConfig;
    }

    /// <inheritdoc />
    public async Task SaveVersionGuardConfigurationAsync(VersionGuardConfiguration config)
    {
        await SaveConfigAsync(_versionGuardConfigPath, config);
        _cachedVersionGuardConfig = config;
        _logger.Information("版本守卫配置已保存。");
    }

    /// <inheritdoc />
    public async Task<LoggingConfiguration> GetLoggingConfigurationAsync()
    {
        if (_cachedLoggingConfig != null)
            return _cachedLoggingConfig;

        _cachedLoggingConfig = await LoadConfigAsync<LoggingConfiguration>(_loggingConfigPath) ?? new LoggingConfiguration();
        return _cachedLoggingConfig;
    }

    /// <inheritdoc />
    public async Task SaveLoggingConfigurationAsync(LoggingConfiguration config)
    {
        await SaveConfigAsync(_loggingConfigPath, config);
        _cachedLoggingConfig = config;
        _logger.Information("日志配置已保存。");
    }

    /// <inheritdoc />
    public async Task<CompletionConfiguration> GetCompletionConfigurationAsync()
    {
        if (_cachedCompletionConfig != null)
            return _cachedCompletionConfig;

        _cachedCompletionConfig = await LoadConfigAsync<CompletionConfiguration>(_completionConfigPath) ?? new CompletionConfiguration();
        return _cachedCompletionConfig;
    }

    /// <inheritdoc />
    public async Task SaveCompletionConfigurationAsync(CompletionConfiguration config)
    {
        await SaveConfigAsync(_completionConfigPath, config);
        _cachedCompletionConfig = config;
        _logger.Information("代码补全配置已保存。");
    }

    /// <inheritdoc />
    public async Task<LocalHistoryConfig> GetLocalHistoryConfigAsync()
    {
        if (_cachedLocalHistoryConfig != null)
            return _cachedLocalHistoryConfig;

        _cachedLocalHistoryConfig = await LoadConfigAsync<LocalHistoryConfig>(_localHistoryConfigPath)
            ?? new LocalHistoryConfig();
        return _cachedLocalHistoryConfig;
    }

    /// <inheritdoc />
    public async Task SaveLocalHistoryConfigAsync(LocalHistoryConfig config)
    {
        await SaveConfigAsync(_localHistoryConfigPath, config);
        _cachedLocalHistoryConfig = config;
        _logger.Information("Local History 配置已保存。");
    }

    /// <inheritdoc />
    public async Task<bool> IsAIConfiguredAsync()
    {
        // ① 如果用户显式配置了 CodeBuddy CLI 路径，检测 CLI 是否可用
        var config = await GetAIConfigurationAsync();
        if (!string.IsNullOrWhiteSpace(config.CodebuddyCliPath))
        {
            var cliAvailable = await IsCodebuddyCliAvailableAsync();
            if (cliAvailable)
            {
                _logger.Information("[AIConfig] ✅ 用户配置的 CodeBuddy CLI 可用，AI 已就绪。");
                return true;
            }
            _logger.Debug("[AIConfig] 用户配置的 CLI 路径不可用，回退检查 HTTP API 配置...");
        }
        else
        {
            _logger.Debug("[AIConfig] 用户未配置 CLI 路径，跳过 CLI 检测，直接检查 HTTP API...");
        }

        // ② 检测用户是否已配置自定义 HTTP API
        var apiConfigured = !string.IsNullOrWhiteSpace(config.ApiKey)
            && !string.IsNullOrWhiteSpace(config.ApiEndpoint)
            && !string.IsNullOrWhiteSpace(config.ModelName);

        if (apiConfigured)
        {
            _logger.Information($"[AIConfig] ✅ 用户已配置 HTTP API (Endpoint={config.ApiEndpoint}, Model={config.ModelName})。");
            return true;
        }

        // ③ 都没有配置时，使用内置默认 Chat API（两步 REST 调用）
        _logger.Information($"[AIConfig] ✅ 使用内置默认 Chat API (BaseUrl={AIDefaults.DefaultChatApiBaseUrl})。");
        return true;
    }

    /// <summary>
    /// 检测 CodeBuddy CLI 是否可用。
    /// 仅检查 ai-config.json 中配置的 CodebuddyCliPath，不自动搜索系统 PATH。
    /// </summary>
    private async Task<bool> IsCodebuddyCliAvailableAsync()
    {
        var config = await GetAIConfigurationAsync();

        if (!string.IsNullOrWhiteSpace(config.CodebuddyCliPath))
        {
            _logger.Debug($"[AIConfig] 使用配置的 CLI 路径: {config.CodebuddyCliPath}");
            return await TryRunCliVersionAsync(config.CodebuddyCliPath);
        }

        _logger.Debug("[AIConfig] 未配置 CLI 路径，跳过 CLI 检测。");
        return false;
    }

    /// <summary>
    /// 尝试运行指定 CLI 可执行文件的 --version 命令。
    /// 自动处理 .cmd / 无扩展名的情况（通过 cmd.exe /c 回退）。
    /// </summary>
    /// <param name="cliPath">CLI 可执行文件路径（可以是完整路径或仅文件名）</param>
    /// <returns>CLI 是否可用（exitCode=0 且未超时）</returns>
    private async Task<bool> TryRunCliVersionAsync(string cliPath)
    {
        // ① 先尝试直接 Process.Start（适用于 .exe 文件）
        var directResult = await TryRunCliVersionCoreAsync(cliPath, useCmdFallback: false);
        if (directResult.HasValue)
            return directResult.Value;

        // ② 若失败（如 .cmd 文件无法直接启动），回退到 cmd.exe /c
        _logger.Debug($"[AIConfig] 直接启动失败，尝试 cmd.exe /c 方式: {cliPath}");
        var cmdResult = await TryRunCliVersionCoreAsync(cliPath, useCmdFallback: true);
        return cmdResult ?? false;
    }

    /// <summary>
    /// 核心 CLI 版本检测逻辑。
    /// </summary>
    /// <param name="cliPath">CLI 路径</param>
    /// <param name="useCmdFallback">是否通过 cmd.exe /c 包装执行</param>
    /// <returns>可用时返回 true，不可用时返回 false，启动失败时返回 null</returns>
    private async Task<bool?> TryRunCliVersionCoreAsync(string cliPath, bool useCmdFallback)
    {
        try
        {
            ProcessStartInfo startInfo;
            if (useCmdFallback)
            {
                // cmd.exe /c "codebuddy --version" — 支持 .cmd/.bat 等脚本文件
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{cliPath}\" --version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // 异步等待退出，最多 10 秒超时
            var completed = await Task.Run(() => process.WaitForExit(10_000));
            var available = completed && process.ExitCode == 0;

            if (available)
            {
                var version = process.StandardOutput.ReadToEnd().Trim();
                _logger.Debug($"[AIConfig] CodeBuddy CLI 检测成功: path={cliPath}, version={version}, mode={(useCmdFallback ? "cmd" : "direct")}");
            }
            else
            {
                _logger.Debug($"[AIConfig] CodeBuddy CLI 检测失败: path={cliPath}, completed={completed}, exitCode={process.ExitCode}, mode={(useCmdFallback ? "cmd" : "direct")}");
            }

            return available;
        }
        catch (Exception ex)
        {
            var level = useCmdFallback ? "异常(最终)" : "异常";
            _logger.Debug($"[AIConfig] CodeBuddy CLI 检测{level}: path={cliPath}, {ex.GetType().Name} - {ex.Message}, mode={(useCmdFallback ? "cmd" : "direct")}");
            return null; // null 表示需要回退
        }
    }

    /// <summary>
    /// Invalidate cached configuration, forcing reload on next access.
    /// </summary>
    public void InvalidateCache()
    {
        _cachedAIConfig = null;
        _cachedVersionGuardConfig = null;
        _cachedLoggingConfig = null;
        _cachedLocalHistoryConfig = null;
        _cachedCompletionConfig = null;
    }

    private void EnsureConfigDirectoryExists()
    {
        if (!Directory.Exists(_configDirectory))
        {
            Directory.CreateDirectory(_configDirectory);
            _logger.Debug($"Configuration directory created: {_configDirectory}");
        }
    }

    private async Task<T?> LoadConfigAsync<T>(string filePath) where T : class
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.Debug($"配置文件未找到: {filePath}，使用默认值。");
                return null;
            }

            var json = File.ReadAllText(filePath);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
        }
        catch (Exception ex)
        {
            _logger.Warning($"从 {filePath} 加载配置失败: {ex.Message}");
            return null;
        }
    }

    private async Task SaveConfigAsync<T>(string filePath, T config)
    {
        try
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error($"保存配置到 {filePath} 失败: {ex.Message}");
            throw;
        }
    }
}
