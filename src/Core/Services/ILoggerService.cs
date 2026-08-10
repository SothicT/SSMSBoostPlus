using System;

namespace SSMSBoostPlus.Services;

/// <summary>
/// Logger service interface for platform-independent logging.
/// </summary>
public interface ILoggerService
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message);
    void Error(Exception ex, string message);

    /// <summary>
    /// 设置日志是否启用（动态开关，用于运行时控制日志文件写入）。
    /// </summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Default console logger for testing and non-SSMS environments.
/// </summary>
public class ConsoleLoggerService : ILoggerService
{
    public void SetEnabled(bool enabled) { } // ConsoleLogger 无需开关
    public void Debug(string message) => System.Diagnostics.Debug.WriteLine($"[调试] {message}");
    public void Information(string message) => Console.WriteLine($"[信息] {message}");
    public void Warning(string message) => Console.WriteLine($"[警告] {message}");
    public void Error(string message) => Console.WriteLine($"[错误] {message}");
    public void Error(Exception ex, string message) => Console.WriteLine($"[错误] {message} {ex.Message}");
}
