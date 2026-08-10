using System.Collections.Generic;
using System.Threading.Tasks;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// Connection information provider interface.
/// Implementations can use different strategies to obtain SSMS connection info.
/// </summary>
public interface IConnectionInfoProvider
{
    /// <summary>
    /// Provider name for logging and diagnostics.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Provider priority (lower value = higher priority).
    /// Used for fallback strategy: try providers in priority order.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Check if the provider is available in current environment.
    /// Should return false if required services/types are not available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Get connection information from current SSMS context.
    /// </summary>
    /// <returns>Connection information, or null if not available</returns>
    Task<ConnectionInfo?> GetConnectionInfoAsync();
}

/// <summary>
/// Represents SSMS connection information.
/// Platform-independent data structure.
/// </summary>
public class ConnectionInfo
{
    /// <summary>
    /// Server instance name (e.g., "localhost\SQLEXPRESS", "myserver.database.windows.net").
    /// </summary>
    public string? ServerInstance { get; set; }

    /// <summary>
    /// Database name (initial catalog).
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Authentication type.
    /// </summary>
    public AuthenticationType AuthenticationType { get; set; }

    /// <summary>
    /// User name (for SQL authentication).
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// SQL Server version string.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>
    /// Full connection string (if available).
    /// May be null if provider only has partial information.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Source provider name (for logging).
    /// </summary>
    public string? SourceProvider { get; set; }

    /// <summary>
    /// Additional metadata from the connection context.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Authentication type enumeration.
/// </summary>
public enum AuthenticationType
{
    /// <summary>
    /// Windows Integrated Authentication.
    /// </summary>
    Windows,

    /// <summary>
    /// SQL Server Authentication.
    /// </summary>
    SqlServer,

    /// <summary>
    /// Azure Active Directory Authentication.
    /// </summary>
    AzureActiveDirectory,

    /// <summary>
    /// Unknown or other authentication type.
    /// </summary>
    Unknown
}
