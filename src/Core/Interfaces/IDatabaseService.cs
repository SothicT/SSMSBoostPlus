using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// Database service abstraction for querying SQL Server metadata.
/// Supports both connection string and pre-opened SqlConnection overloads.
/// </summary>
public interface IDatabaseService
{
    /// <summary>
    /// Get the modify_date of a database object from sys.objects by connection string.
    /// </summary>
    Task<DateTime?> GetObjectModifyDateAsync(string connectionString, ObjectReference objectRef);

    /// <summary>
    /// Get the modify_date using a pre-opened SqlConnection.
    /// </summary>
    Task<DateTime?> GetObjectModifyDateAsync(SqlConnection connection, ObjectReference objectRef);

    /// <summary>
    /// Get the full definition (SQL text) of a database object by connection string.
    /// </summary>
    Task<string?> GetObjectDefinitionAsync(string connectionString, ObjectReference objectRef);

    /// <summary>
    /// Get the full definition using a pre-opened SqlConnection.
    /// </summary>
    Task<string?> GetObjectDefinitionAsync(SqlConnection connection, ObjectReference objectRef);

    /// <summary>
    /// Get the estimated execution plan XML for a SQL statement by connection string.
    /// </summary>
    Task<string?> GetExecutionPlanAsync(string connectionString, string sqlCode);

    /// <summary>
    /// Get the estimated execution plan XML using a pre-opened SqlConnection.
    /// </summary>
    Task<string?> GetExecutionPlanAsync(SqlConnection connection, string sqlCode);

    /// <summary>
    /// 获取指定表的 Schema 信息（列名、数据类型、可空、键信息）。
    /// 返回格式化的文本描述，供 AI 性能分析使用。
    /// </summary>
    /// <param name="connectionString">数据库连接字符串</param>
    /// <param name="tableName">表名（可含 Schema 前缀，如 dbo.MyTable）</param>
    /// <returns>Schema 信息文本；查询失败返回 null</returns>
    Task<string?> GetTableSchemaAsync(string connectionString, string tableName);

    /// <summary>
    /// 获取指定表的索引信息（索引名、列、唯一性、类型）。
    /// 返回格式化的文本描述，供 AI 性能分析使用。
    /// </summary>
    /// <param name="connectionString">数据库连接字符串</param>
    /// <param name="tableName">表名（可含 Schema 前缀，如 dbo.MyTable）</param>
    /// <returns>索引信息文本；查询失败返回 null</returns>
    Task<string?> GetTableIndexesAsync(string connectionString, string tableName);

    /// <summary>
    /// Clear the internal modify_date cache.
    /// </summary>
    void ClearCache();
}