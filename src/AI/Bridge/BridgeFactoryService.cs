using System.Threading.Tasks;
using SSMSBoostPlus.AI.Parsing;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.AI.Bridge;

/// <summary>
/// Instance-based bridge factory implementation.
/// Encapsulates the bridge selection logic (HTTP API first, CLI only when explicitly configured).
/// This is the injectable version for unit testing.
/// </summary>
public class BridgeFactoryService : IBridgeFactory
{
    private readonly ILoggerService _logger;

    /// <summary>
    /// 初始化工厂服务实例。
    /// </summary>
    /// <param name="logger">日志服务（可选，为 null 时使用 ConsoleLogger 回退）</param>
    public BridgeFactoryService(ILoggerService? logger = null)
    {
        _logger = logger ?? new ConsoleLoggerService();
    }

    /// <inheritdoc/>
    public async Task<ICodebuddyBridge> CreateBestAvailableBridgeAsync(AIConfiguration config)
    {
        // ① 如果用户显式配置了 CodeBuddy CLI 路径，优先尝试 CLI
        if (!string.IsNullOrWhiteSpace(config.CodebuddyCliPath))
        {
            var cliBridge = CreateCliBridge(config.CodebuddyCliPath);
            if (await cliBridge.IsCliAvailableAsync())
            {
                _logger.Information("[BridgeFactory] ✅ 使用用户配置的 CodeBuddy CLI。");
                return cliBridge;
            }
            _logger.Warning($"[BridgeFactory] 用户配置的 CLI 路径不可用: {config.CodebuddyCliPath}，回退到 HTTP API...");
        }

        // ② 如果用户已配置自定义 HTTP API，使用用户的配置
        if (HasUserConfiguredHttpApi(config))
        {
            _logger.Information($"[BridgeFactory] 使用用户配置的 HTTP API (Endpoint={config.ApiEndpoint})。");
            return new AIApiClientBridge(config, _logger);
        }

        // ③ 兜底：使用内置默认 AI API（无需用户配置）
        _logger.Information($"[BridgeFactory] 使用内置默认 AI API (Endpoint={AIDefaults.DefaultEndpoint})。");

        var defaultConfig = new AIConfiguration
        {
            ApiEndpoint = AIDefaults.DefaultEndpoint,
            ApiKey = AIDefaults.DefaultApiKey,
            ModelName = AIDefaults.DefaultModelName,
            TimeoutSeconds = config.TimeoutSeconds,
            DefaultTemperature = config.DefaultTemperature,
            DefaultMaxTokens = config.DefaultMaxTokens,
            DefaultFormattingStyle = config.DefaultFormattingStyle
        };
        return new AIApiClientBridge(defaultConfig, _logger);
    }

    /// <summary>
    /// 检测用户是否已自定义配置 HTTP API（设置了 Endpoint 即视为用户配置）。
    /// </summary>
    private static bool HasUserConfiguredHttpApi(AIConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(config.ApiEndpoint);
    }

    /// <inheritdoc/>
    public ICodebuddyBridge CreateHttpApiBridge(AIConfiguration config)
    {
        return new AIApiClientBridge(config, _logger);
    }

    /// <inheritdoc/>
    public ICodebuddyBridge CreateCliBridge(string? cliPath = null)
    {
        var promptManager = new PromptTemplateManager();
        var responseParser = new ResponseParser();
        return new CodebuddyCliBridge(promptManager, responseParser, _logger, cliPath);
    }
}