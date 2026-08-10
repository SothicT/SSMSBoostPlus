using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Services;

/// <summary>
/// Database service for querying SQL Server metadata.
/// Supports both connection string and pre-opened SqlConnection for maximum flexibility.
/// When using a pre-opened SqlConnection, the caller manages the connection lifecycle
/// (no new/open/dispose inside the service).
/// </summary>
public class DatabaseService : IDatabaseService
{
    private readonly ILoggerService _logger;

    // Cache: ObjectKey -> (modifyDate, cachedTime)
    private readonly Dictionary<string, Tuple<DateTime, DateTime>> _cache = new Dictionary<string, Tuple<DateTime, DateTime>>();
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    public DatabaseService(ILoggerService logger)
    {
        _logger = logger;
    }

    // ============================================================
    // 原有 string 重载（保持向后兼容）
    // ============================================================

    /// <summary>
    /// Get the modify_date of a database object from sys.objects.
    /// </summary>
    public async Task<DateTime?> GetObjectModifyDateAsync(string connectionString, ObjectReference objectRef)
    {
        var cacheKey = $"{connectionString}:{objectRef.FullName}";

        if (TryGetCache(cacheKey, out var cached)) return cached;

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return await QueryModifyDateAsync(conn, objectRef, cacheKey);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"查询 {objectRef.FullName} 的 modify_date 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the modify_date using a pre-opened SqlConnection.
    /// Connection must already be open; caller manages lifecycle.
    /// </summary>
    public async Task<DateTime?> GetObjectModifyDateAsync(SqlConnection connection, ObjectReference objectRef)
    {
        if (connection.State != ConnectionState.Open)
        {
            _logger.Error($"SqlConnection 未打开（对象: {objectRef.FullName}），当前状态: {connection.State}");
            return null;
        }

        var cacheKey = $"{connection.DataSource}/{connection.Database}:{objectRef.FullName}";

        if (TryGetCache(cacheKey, out var cached)) return cached;

        try
        {
            return await QueryModifyDateAsync(connection, objectRef, cacheKey);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"查询 {objectRef.FullName} 的 modify_date 失败: {ex.Message}");
            return null;
        }
    }

    private async Task<DateTime?> QueryModifyDateAsync(SqlConnection conn, ObjectReference objectRef, string cacheKey)
    {
        var sql = @"
SELECT modify_date 
FROM sys.objects 
WHERE name = @name 
  AND schema_id = SCHEMA_ID(@schema)";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", objectRef.Name);
        cmd.Parameters.AddWithValue("@schema", objectRef.Schema);

        var result = await cmd.ExecuteScalarAsync();

        if (result != null && result != DBNull.Value)
        {
            var modifyDate = (DateTime)result;
            _cache[cacheKey] = Tuple.Create(modifyDate, DateTime.Now);
            return modifyDate;
        }

        return null;
    }

    // ============================================================
    // GetObjectDefinitionAsync
    // ============================================================

    public async Task<string?> GetObjectDefinitionAsync(string connectionString, ObjectReference objectRef)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return await QueryDefinitionAsync(conn, objectRef);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"获取 {objectRef.FullName} 的定义失败: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> GetObjectDefinitionAsync(SqlConnection connection, ObjectReference objectRef)
    {
        if (connection.State != ConnectionState.Open)
        {
            _logger.Error($"SqlConnection 未打开（对象: {objectRef.FullName}）");
            return null;
        }

        try
        {
            return await QueryDefinitionAsync(connection, objectRef);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"获取 {objectRef.FullName} 的定义失败: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> QueryDefinitionAsync(SqlConnection conn, ObjectReference objectRef)
    {
        var sql = @"
SELECT definition 
FROM sys.sql_modules 
WHERE object_id = OBJECT_ID(@fullObjectName)";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fullObjectName", objectRef.FullName);

        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return null;

        var definition = (string)result;

        // sys.sql_modules.definition always returns CREATE statement.
        // For version comparison (updating existing objects), convert to ALTER.
        definition = ConvertCreateToAlter(definition);

        return definition;
    }

    /// <summary>
    /// Convert CREATE PROCEDURE/FUNCTION/VIEW/TRIGGER to ALTER.
    /// sys.sql_modules.definition always stores CREATE statements,
    /// but for version comparison we need ALTER (updating existing objects).
    /// </summary>
    private static string ConvertCreateToAlter(string definition)
    {
        if (string.IsNullOrEmpty(definition)) return definition;

        // Replace the first occurrence of CREATE with ALTER (case-insensitive, word boundary)
        // Pattern matches: CREATE PROCEDURE, CREATE FUNCTION, CREATE VIEW, CREATE TRIGGER
        var pattern = @"(?i)\bCREATE\b(?=\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b)";
        return System.Text.RegularExpressions.Regex.Replace(definition, pattern, "ALTER", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    // ============================================================
    // GetExecutionPlanAsync
    // ============================================================

    public async Task<string?> GetExecutionPlanAsync(string connectionString, string sqlCode)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return await QueryPlanAsync(conn, sqlCode);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to get execution plan: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> GetExecutionPlanAsync(SqlConnection connection, string sqlCode)
    {
        if (connection.State != ConnectionState.Open) return null;
        try { return await QueryPlanAsync(connection, sqlCode); }
        catch (Exception ex) { _logger.Warning($"Failed to get execution plan: {ex.Message}"); return null; }
    }

    private async Task<string?> QueryPlanAsync(SqlConnection conn, string sqlCode)
    {
        using var setPlanCmd = new SqlCommand("SET SHOWPLAN_XML ON", conn);
        await setPlanCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        try
        {
            using var cmd = new SqlCommand(sqlCode, conn);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var planBuilder = new System.Text.StringBuilder();
            while (await reader.ReadAsync().ConfigureAwait(false))
                planBuilder.Append(reader[0].ToString());
            reader.Close();
            return planBuilder.Length > 0 ? planBuilder.ToString() : null;
        }
        finally
        {
            // 无论是否异常，始终重置 SHOWPLAN_XML 状态，避免连接状态残留
            using var resetPlanCmd = new SqlCommand("SET SHOWPLAN_XML OFF", conn);
            await resetPlanCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    // ============================================================
    // GetTableSchemaAsync
    // ============================================================

    /// <summary>
    /// 获取指定表的 Schema 信息（列名、数据类型、可空、键信息）。
    /// 通过 INFORMATION_SCHEMA.COLUMNS 查询，返回格式化文本。
    /// </summary>
    public async Task<string?> GetTableSchemaAsync(string connectionString, string tableName)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return await QueryTableSchemaAsync(conn, tableName);
        }
        catch (Exception ex)
        {
            _logger.Warning($"获取表 {tableName} 的 Schema 信息失败: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> QueryTableSchemaAsync(SqlConnection conn, string tableName)
    {
        // 解析 Schema 和表名
        var parts = tableName.Split('.');
        string schemaPart, tablePart;
        if (parts.Length >= 2)
        {
            schemaPart = parts[parts.Length - 2].Trim('[', ']');
            tablePart = parts[parts.Length - 1].Trim('[', ']');
        }
        else
        {
            schemaPart = "dbo";
            tablePart = parts[0].Trim('[', ']');
        }

        var sql = @"
SELECT 
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.IS_NULLABLE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.NUMERIC_PRECISION,
    c.NUMERIC_SCALE,
    CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 'YES' ELSE 'NO' END AS IS_PRIMARY_KEY
FROM INFORMATION_SCHEMA.COLUMNS c
LEFT JOIN (
    SELECT ku.COLUMN_NAME
    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku 
        ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
        AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA
    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
        AND tc.TABLE_SCHEMA = @schema
        AND tc.TABLE_NAME = @table
) pk ON c.COLUMN_NAME = pk.COLUMN_NAME
WHERE c.TABLE_SCHEMA = @schema
    AND c.TABLE_NAME = @table
ORDER BY c.ORDINAL_POSITION";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schemaPart);
        cmd.Parameters.AddWithValue("@table", tablePart);
        cmd.CommandTimeout = 5;

        using var reader = await cmd.ExecuteReaderAsync();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"表 [{schemaPart}].[{tablePart}] 结构信息：");
        sb.AppendLine("列名 | 数据类型 | 可空 | 主键");
        sb.AppendLine("--- | --- | --- | ---");

        var hasRows = false;
        while (await reader.ReadAsync())
        {
            hasRows = true;
            var colName = reader["COLUMN_NAME"]?.ToString() ?? "";
            var dataType = reader["DATA_TYPE"]?.ToString() ?? "";
            var isNullable = reader["IS_NULLABLE"]?.ToString() == "YES" ? "✓" : "✗";
            var isPk = reader["IS_PRIMARY_KEY"]?.ToString() == "YES" ? "🔑" : "";

            // 附加长度/精度信息
            var maxLength = reader["CHARACTER_MAXIMUM_LENGTH"];
            var precision = reader["NUMERIC_PRECISION"];
            var scale = reader["NUMERIC_SCALE"];
            if (maxLength != null && maxLength != DBNull.Value)
                dataType += $"({(Convert.ToInt64(maxLength) == -1 ? "MAX" : maxLength)})";
            else if (precision != null && precision != DBNull.Value && Convert.ToInt16(precision) > 0)
            {
                dataType += $"({precision}";
                if (scale != null && scale != DBNull.Value && Convert.ToInt16(scale) > 0)
                    dataType += $",{scale}";
                dataType += ")";
            }

            sb.AppendLine($"{colName} | {dataType} | {isNullable} | {isPk}");
        }

        return hasRows ? sb.ToString() : null;
    }

    // ============================================================
    // GetTableIndexesAsync
    // ============================================================

    /// <summary>
    /// 获取指定表的索引信息（索引名、列、唯一性、类型）。
    /// 通过 sys.indexes + sys.index_columns 查询，返回格式化文本。
    /// </summary>
    public async Task<string?> GetTableIndexesAsync(string connectionString, string tableName)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return await QueryTableIndexesAsync(conn, tableName);
        }
        catch (Exception ex)
        {
            _logger.Warning($"获取表 {tableName} 的索引信息失败: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> QueryTableIndexesAsync(SqlConnection conn, string tableName)
    {
        // 解析 Schema 和表名
        var parts = tableName.Split('.');
        string schemaPart, tablePart;
        if (parts.Length >= 2)
        {
            schemaPart = parts[parts.Length - 2].Trim('[', ']');
            tablePart = parts[parts.Length - 1].Trim('[', ']');
        }
        else
        {
            schemaPart = "dbo";
            tablePart = parts[0].Trim('[', ']');
        }

        var sql = @"
SELECT 
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.is_primary_key AS IsPrimaryKey,
    STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS Columns,
    STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name ELSE NULL END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS IncludedColumns
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE s.name = @schema
    AND t.name = @table
    AND i.name IS NOT NULL
GROUP BY i.name, i.type_desc, i.is_unique, i.is_primary_key
ORDER BY i.is_primary_key DESC, i.is_unique DESC, i.name
";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schemaPart);
        cmd.Parameters.AddWithValue("@table", tablePart);
        cmd.CommandTimeout = 5;

        using var reader = await cmd.ExecuteReaderAsync();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"表 [{schemaPart}].[{tablePart}] 索引信息：");
        sb.AppendLine("索引名 | 类型 | 唯一 | 主键 | 列 | 包含列");
        sb.AppendLine("--- | --- | --- | --- | --- | ---");

        var hasRows = false;
        while (await reader.ReadAsync())
        {
            hasRows = true;
            var indexName = reader["IndexName"]?.ToString() ?? "";
            var indexType = reader["IndexType"]?.ToString() ?? "";
            var isUnique = (bool)reader["IsUnique"] ? "✓" : "✗";
            var isPk = (bool)reader["IsPrimaryKey"] ? "🔑" : "";
            var columns = reader["Columns"]?.ToString() ?? "";
            var included = reader["IncludedColumns"]?.ToString() ?? "";

            sb.AppendLine($"{indexName} | {indexType} | {isUnique} | {isPk} | {columns} | {included}");
        }

        return hasRows ? sb.ToString() : null;
    }

    // ============================================================
    // 辅助
    // ============================================================

    private bool TryGetCache(string cacheKey, out DateTime? value)
    {
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.Now - cached.Item2 < _cacheTtl)
        {
            _logger.Debug($"缓存命中: {cacheKey}");
            value = cached.Item1;
            return true;
        }
        _cache.Remove(cacheKey);
        value = null;
        return false;
    }

    public void ClearCache()
    {
        _cache.Clear();
    }
}
