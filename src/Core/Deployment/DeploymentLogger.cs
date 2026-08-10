using System;
using System.IO;

namespace SSMSBoostPlus.Deployment;

/// <summary>
/// 发布日志接口 — 支持日志记录和事件通知。
/// </summary>
public interface IDeploymentLogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message);
    void LogDebug(string message);

    /// <summary>日志条目事件（用于实时监控）</summary>
    event EventHandler<LogEntryEventArgs>? OnLogEntry;
}

/// <summary>
/// 日志条目事件参数。
/// </summary>
public class LogEntryEventArgs : EventArgs
{
    public string Message { get; set; } = "";
    public LogLevel Level { get; set; } = LogLevel.Info;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// 发布日志记录器 — 同时记录到文件和控制台事件。
/// </summary>
public class DeploymentLogger : IDeploymentLogger, IDisposable
{
    private readonly string _logFilePath;
    private readonly StreamWriter? _writer;
    private readonly object _lock = new();

    public event EventHandler<LogEntryEventArgs>? OnLogEntry;

    public DeploymentLogger(string? logDir = null)
    {
        logDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SSMSBoostPlus", "DeploymentLogs");

        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir,
            $"deploy_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        try
        {
            _writer = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
            LogInfo($"[日志] 发布日志已创建: {_logFilePath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"创建日志文件失败: {ex.Message}");
        }
    }

    public void LogInfo(string message) => WriteEntry(LogLevel.Info, message);
    public void LogWarning(string message) => WriteEntry(LogLevel.Warning, message);
    public void LogError(string message) => WriteEntry(LogLevel.Error, message);
    public void LogDebug(string message) => WriteEntry(LogLevel.Debug, message);

    private void WriteEntry(LogLevel level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var formatted = $"[{timestamp}] [{level,-7}] {message}";

        // 写入文件
        lock (_lock)
        {
            _writer?.WriteLine(formatted);
        }

        // 触发事件（用于实时监控 UI）
        OnLogEntry?.Invoke(this, new LogEntryEventArgs
        {
            Message = message,
            Level = level,
            Timestamp = DateTime.Now
        });

        // Debug 输出
        System.Diagnostics.Debug.WriteLine(formatted);
    }

    public string GetLogFilePath() => _logFilePath;

    public void Dispose()
    {
        _writer?.Dispose();
    }
}