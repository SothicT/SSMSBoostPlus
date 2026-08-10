using System;
using Microsoft.Data.SqlClient;

namespace SSMSBoostPlus.Services;

/// <summary>
/// Utility class for building and parsing SQL Server connection strings.
/// Shared between Core (testable) and SSMS layers.
/// </summary>
public static class ConnectionStringBuilder
{
    /// <summary>
    /// Build a SQL Server connection string from individual parameters.
    /// </summary>
    /// <param name="serverName">Server instance name (DataSource).</param>
    /// <param name="database">Initial catalog (database name). Optional.</param>
    /// <param name="integratedSecurity">Use Windows Authentication.</param>
    /// <param name="userId">SQL Auth username. Only used if integratedSecurity=false.</param>
    /// <param name="password">SQL Auth password. Only used if integratedSecurity=false.</param>
    /// <param name="connectTimeout">Connection timeout in seconds. Default: 15.</param>
    /// <returns>A valid SQL Server connection string.</returns>
    public static string BuildConnectionString(
        string serverName,
        string? database = null,
        bool integratedSecurity = true,
        string? userId = null,
        string? password = null,
        int connectTimeout = 15)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            throw new ArgumentException("Server name is required.", nameof(serverName));

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            IntegratedSecurity = integratedSecurity,
            ConnectTimeout = connectTimeout,
            TrustServerCertificate = true  // SSMS 22 默认启用加密，必须信任自签名证书
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.InitialCatalog = database;
        }

        if (!integratedSecurity)
        {
            builder.UserID = userId ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Parse the Initial Catalog (database name) from a connection string.
    /// </summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <returns>The database name, or null if not specified or parsing fails.</returns>
    public static string? ParseDatabaseName(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return string.IsNullOrEmpty(builder.InitialCatalog) ? null : builder.InitialCatalog;
        }
        catch
        {
            // Fallback: manual parsing for malformed connection strings
            foreach (var part in connectionString.Split(';'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim();
                    if (string.Equals(key, "Initial Catalog", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "Database", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = kv[1].Trim();
                        return string.IsNullOrEmpty(val) ? null : val;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Parse the DataSource (server name) from a connection string.
    /// </summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <returns>The server name, or null if parsing fails.</returns>
    public static string? ParseServerName(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return string.IsNullOrEmpty(builder.DataSource) ? null : builder.DataSource;
        }
        catch
        {
            foreach (var part in connectionString.Split(';'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim();
                    if (string.Equals(key, "Data Source", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "Server", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "Address", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = kv[1].Trim();
                        return string.IsNullOrEmpty(val) ? null : val;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Check if a connection string uses Windows Authentication.
    /// </summary>
    public static bool UsesIntegratedSecurity(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return builder.IntegratedSecurity;
        }
        catch
        {
            return false;
        }
    }
}
