using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// Connection service facade that implements automatic fallback strategy.
/// Tries providers in priority order: Official SDK interfaces → Reflection fallback.
/// 
/// Strategy pattern implementation:
/// 1. Try OfficialConnectionProvider (Priority 1) - Uses IScriptEditorService/IObjectExplorerService
/// 2. Fallback to ReflectionConnectionProvider (Priority 10) - Uses SSMS internal reflection
/// 
/// This ensures maximum compatibility across different SSMS versions and configurations.
/// </summary>
public class ConnectionServiceFacade
{
    private readonly IEnumerable<IConnectionInfoProvider> _providers;
    private readonly ILoggerService? _logger;

    /// <summary>
    /// Event raised when a fallback occurs (for monitoring and diagnostics).
    /// </summary>
    public event EventHandler<FallbackEventArgs>? FallbackOccurred;

    public ConnectionServiceFacade(
        IEnumerable<IConnectionInfoProvider> providers,
        ILoggerService? logger = null)
    {
        _providers = providers?.OrderBy(p => p.Priority).ToList() 
            ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger;

        _logger?.Information($"[ConnectionFacade] Initialized with {_providers.Count()} providers: " +
            string.Join(", ", _providers.Select(p => $"{p.ProviderName}(P{p.Priority})")));
    }

    /// <summary>
    /// Get connection information using automatic fallback strategy.
    /// </summary>
    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _logger?.Information("[ConnectionFacade] Starting connection retrieval with fallback strategy...");

        var exceptions = new List<Exception>();
        var attemptedProviders = new List<string>();

        foreach (var provider in _providers)
        {
            attemptedProviders.Add(provider.ProviderName);

            try
            {
                // Check if provider is available
                if (!provider.IsAvailable)
                {
                    _logger?.Debug($"[ConnectionFacade] Provider '{provider.ProviderName}' is not available, skipping...");
                    continue;
                }

                _logger?.Debug($"[ConnectionFacade] Trying provider: {provider.ProviderName} (Priority {provider.Priority})");

                var connectionInfo = await provider.GetConnectionInfoAsync();

                if (connectionInfo != null)
                {
                    // ★ 服务器一致性校验：所有 provider 返回的连接必须与窗口标题解析的服务器一致
                    // 场景：用户切换到新服务器（如 172.16.50.167），但 Object Explorer 仍停留在旧服务器
                    // （如 172.16.183.25）→ AxialStyle 已拒绝，但其他 provider 可能返回旧服务器连接
                    // 必须统一拒绝，否则补全/缓存使用错误服务器且无任何提示
                    var expectedServer = AxialStyleConnectionProvider.LastWindowCaptionServer;
                    if (!string.IsNullOrEmpty(expectedServer) &&
                        !string.Equals(connectionInfo.ServerInstance, expectedServer, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Warning($"[ConnectionFacade] ⚠️ 服务器不匹配: 窗口标题='{expectedServer}', " +
                            $"provider '{provider.ProviderName}' 返回='{connectionInfo.ServerInstance}'，" +
                            $"拒绝使用该连接（Object Explorer 节点可能未同步切换到新服务器）");
                        continue; // 跳过该 provider，尝试下一个
                    }

                    _logger?.Information($"[ConnectionFacade] ✅ Successfully retrieved connection from: {provider.ProviderName}");
                    
                    // Log connection details (without sensitive information)
                    _logger?.Information($"[ConnectionFacade] Connection details: Server={connectionInfo.ServerInstance}, " +
                        $"Database={connectionInfo.DatabaseName}, Auth={connectionInfo.AuthenticationType}");

                    return connectionInfo;
                }
                else
                {
                    _logger?.Debug($"[ConnectionFacade] Provider '{provider.ProviderName}' returned null, trying next...");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[ConnectionFacade] Provider '{provider.ProviderName}' failed: {ex.Message}");
                exceptions.Add(ex);

                // Raise fallback event
                OnFallbackOccurred(provider.ProviderName, ex, attemptedProviders);
            }
        }

        // All providers failed
        var errorMessage = $"All connection providers failed. Attempted: {string.Join(", ", attemptedProviders)}";
        _logger?.Error($"[ConnectionFacade] ❌ {errorMessage}");

        if (exceptions.Any())
        {
            throw new AggregateException(errorMessage, exceptions);
        }

        return null;
    }

    /// <summary>
    /// Get connection information with detailed diagnostics.
    /// Returns both the connection info and diagnostics about which provider was used.
    /// </summary>
    public async Task<(ConnectionInfo? Connection, ConnectionDiagnostics Diagnostics)> GetConnectionWithDiagnosticsAsync()
    {
        var diagnostics = new ConnectionDiagnostics
        {
            StartTime = DateTime.UtcNow
        };

        try
        {
            var connection = await GetConnectionInfoAsync();
            diagnostics.Success = connection != null;
            diagnostics.EndTime = DateTime.UtcNow;
            diagnostics.AttemptedProviders = _providers.Select(p => p.ProviderName).ToList();

            return (connection, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Success = false;
            diagnostics.ErrorMessage = ex.Message;
            diagnostics.EndTime = DateTime.UtcNow;
            diagnostics.AttemptedProviders = _providers.Select(p => p.ProviderName).ToList();

            return (null, diagnostics);
        }
    }

    /// <summary>
    /// Check if any provider is available.
    /// </summary>
    public bool IsAnyProviderAvailable()
    {
        return _providers.Any(p => p.IsAvailable);
    }

    /// <summary>
    /// Get list of all registered providers and their status.
    /// </summary>
    public List<ProviderStatus> GetProviderStatuses()
    {
        return _providers.Select(p => new ProviderStatus
        {
            Name = p.ProviderName,
            Priority = p.Priority,
            IsAvailable = p.IsAvailable
        }).ToList();
    }

    protected virtual void OnFallbackOccurred(string providerName, Exception exception, List<string> attemptedProviders)
    {
        FallbackOccurred?.Invoke(this, new FallbackEventArgs
        {
            FailedProvider = providerName,
            Exception = exception,
            AttemptedProviders = attemptedProviders,
            Timestamp = DateTime.UtcNow
        });
    }
}

/// <summary>
/// Fallback event arguments.
/// </summary>
public class FallbackEventArgs : EventArgs
{
    public string FailedProvider { get; set; } = "";
    public Exception Exception { get; set; } = null!;
    public List<string> AttemptedProviders { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Provider status information.
/// </summary>
public class ProviderStatus
{
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public bool IsAvailable { get; set; }
}

/// <summary>
/// Connection retrieval diagnostics.
/// </summary>
public class ConnectionDiagnostics
{
    public bool Success { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
    public List<string> AttemptedProviders { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
