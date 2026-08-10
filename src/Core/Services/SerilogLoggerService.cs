using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace SSMSBoostPlus.Services;

/// <summary>
/// Serilog-based logging service implementation.
/// Supports file logging with hourly rotation and configurable log levels.
/// </summary>
public class SerilogLoggerService : ILoggerService, IDisposable
{
    private readonly Serilog.ILogger _logger;
    private bool _disposed;
    private volatile bool _enabled = true;

    public SerilogLoggerService(string logDirectory, string logLevel = "Debug", int retentionDays = 30, int maxFileSizeMB = 10)
    {
        var level = ParseLogLevel(logLevel);

        _logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "ssmsboostplus-.log"),
                restrictedToMinimumLevel: level,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{MachineName}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Hour,
                retainedFileCountLimit: retentionDays * 24, // hourly files * 24 hours per day
                fileSizeLimitBytes: maxFileSizeMB * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true
            )
            .CreateLogger();
    }

    /// <summary>
    /// Constructor for testing with a pre-configured logger.
    /// </summary>
    public SerilogLoggerService(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    /// <inheritdoc />
    public void Debug(string message)
    {
        if (!_enabled) return;
        _logger.Debug(message);
    }

    /// <inheritdoc />
    public void Information(string message)
    {
        if (!_enabled) return;
        _logger.Information(message);
    }

    /// <inheritdoc />
    public void Warning(string message)
    {
        if (!_enabled) return;
        _logger.Warning(message);
    }

    /// <inheritdoc />
    public void Error(string message)
    {
        if (!_enabled) return;
        _logger.Error(message);
    }

    /// <inheritdoc />
    public void Error(Exception ex, string message)
    {
        if (!_enabled) return;
        _logger.Error(ex, message);
    }

    private static LogEventLevel ParseLogLevel(string level)
    {
        return level?.ToLowerInvariant() switch
        {
            "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" => LogEventLevel.Fatal,
            _ => LogEventLevel.Debug
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            (_logger as IDisposable)?.Dispose();
            _disposed = true;
        }
    }
}
