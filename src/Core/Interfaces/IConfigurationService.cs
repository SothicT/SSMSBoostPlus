using System.Threading.Tasks;
using SSMSBoostPlus.Models;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// Configuration service abstraction for platform-independent configuration management.
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Get the AI configuration.
    /// </summary>
    Task<AIConfiguration> GetAIConfigurationAsync();

    /// <summary>
    /// Save the AI configuration.
    /// </summary>
    Task SaveAIConfigurationAsync(AIConfiguration config);

    /// <summary>
    /// Get the version guard configuration.
    /// </summary>
    Task<VersionGuardConfiguration> GetVersionGuardConfigurationAsync();

    /// <summary>
    /// Save the version guard configuration.
    /// </summary>
    Task SaveVersionGuardConfigurationAsync(VersionGuardConfiguration config);

    /// <summary>
    /// Get the logging configuration.
    /// </summary>
    Task<LoggingConfiguration> GetLoggingConfigurationAsync();

    /// <summary>
    /// Save the logging configuration.
    /// </summary>
    Task SaveLoggingConfigurationAsync(LoggingConfiguration config);

    /// <summary>
    /// Get the code completion configuration.
    /// </summary>
    Task<CompletionConfiguration> GetCompletionConfigurationAsync();

    /// <summary>
    /// Save the code completion configuration.
    /// </summary>
    Task SaveCompletionConfigurationAsync(CompletionConfiguration config);

    /// <summary>
    /// Get the Local History configuration.
    /// </summary>
    Task<LocalHistoryConfig> GetLocalHistoryConfigAsync();

    /// <summary>
    /// Save the Local History configuration.
    /// </summary>
    Task SaveLocalHistoryConfigAsync(LocalHistoryConfig config);

    /// <summary>
    /// Check if AI is available: CodeBuddy CLI first, then HTTP API configuration.
    /// Returns true if CLI is in PATH or valid API key/endpoint/model are configured.
    /// </summary>
    Task<bool> IsAIConfiguredAsync();
}
