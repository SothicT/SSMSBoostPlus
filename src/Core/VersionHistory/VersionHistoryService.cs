using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.VersionHistory.Models;

namespace SSMSBoostPlus.VersionHistory;

public class VersionHistoryService : IVersionHistoryService
{
    private readonly ILoggerService _logger;
    private readonly string _databaseName;
    private readonly string _tableName;

    public VersionHistoryService(ILoggerService logger, string databaseName = "MESBASE", string tableName = "DBTrigger_Object_Version_Table")
    {
        _logger = logger;
        _databaseName = databaseName;
        _tableName = tableName;
    }

    public async Task<List<VersionRecord>> GetObjectHistoryAsync(
        string connectionString,
        string objectName,
        string? databaseName = null,
        int top = 100,
        CancellationToken ct = default)
    {
        // 兼容两种存储格式：dbo.XX 和 XX
        // 如果传入 dbo.XX 格式，生成两个匹配条件
        var hasSchema = objectName.Contains('.');
        var bareName = hasSchema ? objectName.Split('.').Last() : objectName;

        var sql = $@"
            SELECT TOP (@top)
                [ID],
                [databasename],
                [eventtype],
                [objectname],
                [objecttype],
                [loginname],
                [hostname],
                [PostTime],
                [Version]
            FROM [{_databaseName}].[dbo].[{_tableName}]
            WHERE ([objectname] = @objectName {(hasSchema ? "OR [objectname] = @bareName" : "")})
            {(databaseName != null ? "AND [databasename] = @databaseName" : "")}
            ORDER BY [PostTime] DESC, [ID] DESC";

        var records = new List<VersionRecord>();

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@top", top);
            cmd.Parameters.AddWithValue("@objectName", objectName);
            if (hasSchema)
                cmd.Parameters.AddWithValue("@bareName", bareName);
            if (databaseName != null)
                cmd.Parameters.AddWithValue("@databaseName", databaseName);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                records.Add(MapListRecord(reader));
            }
        }
        catch (SqlException ex)
        {
            _logger.Error(ex, $"[VersionHistory] SQL 查询失败，对象: {objectName}");
            throw new InvalidOperationException($"无法查询版本历史: {ex.Message}", ex);
        }

        return records;
    }

    public async Task<List<VersionRecord>> GetRecentChangesAsync(
        string connectionString,
        int top = 50,
        string? databaseFilter = null,
        CancellationToken ct = default)
    {
        var sql = $@"
            SELECT TOP (@top)
                [ID],
                [databasename],
                [eventtype],
                [objectname],
                [objecttype],
                [sqlcommand],
                [loginname],
                [hostname],
                [PostTime],
                [Version]
            FROM [{_databaseName}].[dbo].[{_tableName}]
            {(databaseFilter != null ? "WHERE [databasename] = @databaseFilter" : "")}
            ORDER BY [PostTime] DESC";

        var records = new List<VersionRecord>();

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@top", top);
            if (databaseFilter != null)
                cmd.Parameters.AddWithValue("@databaseFilter", databaseFilter);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                records.Add(MapRecord(reader));
            }
        }
        catch (SqlException ex)
        {
            _logger.Error(ex, "[VersionHistory] 获取最近变更失败");
            throw;
        }

        return records;
    }

    public async Task<List<ObjectSummary>> GetObjectListAsync(
        string connectionString,
        string? databaseFilter = null,
        int? top = null,
        string? keyword = null,
        CancellationToken ct = default)
    {
        var conditions = new List<string>();
        if (databaseFilter != null)
            conditions.Add("[databasename] = @databaseFilter");
        if (!string.IsNullOrWhiteSpace(keyword))
            conditions.Add("[objectname] LIKE @keyword");

        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        var topClause = top.HasValue ? $"TOP {top.Value}" : "";

        var sql = $@"
            SELECT {topClause}
                t.[objectname],
                t.[objecttype],
                t.[databasename],
                t.[VersionCount],
                t.[LastModified],
                t2.[loginname] AS LastModifiedBy,
                t.[TotalCount]
            FROM (
                SELECT [objectname],
                       MAX([objecttype]) AS objecttype,
                       MAX([databasename]) AS databasename,
                       COUNT(*) AS VersionCount,
                       MAX([PostTime]) AS LastModified,
                       COUNT(*) OVER() AS TotalCount
                FROM [{_databaseName}].[dbo].[{_tableName}]
                {whereClause}
                GROUP BY [objectname]
            ) t
            OUTER APPLY (
                SELECT TOP 1 [loginname]
                FROM [{_databaseName}].[dbo].[{_tableName}]
                WHERE [objectname] = t.[objectname]
                ORDER BY [PostTime] DESC
            ) t2
            ORDER BY t.LastModified DESC";

        var objects = new List<ObjectSummary>();

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            if (databaseFilter != null)
                cmd.Parameters.AddWithValue("@databaseFilter", databaseFilter);
            if (!string.IsNullOrWhiteSpace(keyword))
                cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                objects.Add(new ObjectSummary
                {
                    ObjectName = reader["objectname"]?.ToString() ?? "",
                    ObjectType = reader["objecttype"]?.ToString() ?? "",
                    DatabaseName = reader["databasename"]?.ToString() ?? "",
                    VersionCount = reader["VersionCount"] != DBNull.Value ? Convert.ToInt32(reader["VersionCount"]) : 0,
                    LastModified = reader["LastModified"] != DBNull.Value ? Convert.ToDateTime(reader["LastModified"]) : DateTime.MinValue,
                    LastModifiedBy = reader["LastModifiedBy"]?.ToString() ?? "",
                    TotalCount = reader["TotalCount"] != DBNull.Value ? Convert.ToInt32(reader["TotalCount"]) : 0
                });
            }
        }
        catch (SqlException ex)
        {
            _logger.Error(ex, "[VersionHistory] 获取对象列表失败");
            throw;
        }

        return objects;
    }

    public async Task<string?> GetVersionSqlAsync(
        string connectionString,
        long versionId,
        CancellationToken ct = default)
    {
        var sql = $@"
            SELECT [sqlcommand]
            FROM [{_databaseName}].[dbo].[{_tableName}]
            WHERE [ID] = @id";

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", versionId);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result?.ToString();
        }
        catch (SqlException ex)
        {
            _logger.Error(ex, $"[VersionHistory] 获取版本 SQL 失败，ID: {versionId}");
            return null;
        }
    }

    public async Task<RestoreScriptResult> GenerateRestoreScriptAsync(
        string connectionString,
        long versionId,
        CancellationToken ct = default)
    {
        var record = await GetRecordByIdAsync(connectionString, versionId, ct).ConfigureAwait(false);
        if (record == null)
            return new RestoreScriptResult { CanRestore = false, ErrorMessage = "版本记录不存在" };

        return record.ChangeType switch
        {
            "ALTER" => new RestoreScriptResult
            {
                CanRestore = true,
                Script = record.SqlCommand,
                Type = RestoreType.AlterToHistory
            },
            "CREATE" => new RestoreScriptResult
            {
                CanRestore = true,
                Script = ConvertCreateToAlter(record.SqlCommand, record.ObjectType),
                Type = RestoreType.AlterToHistory
            },
            "DROP" => await BuildRestoreScriptForDropAsync(connectionString, record, ct).ConfigureAwait(false),
            _ => new RestoreScriptResult { CanRestore = false, ErrorMessage = $"不支持的变更类型: {record.ChangeType}" }
        };
    }

    private async Task<RestoreScriptResult> BuildRestoreScriptForDropAsync(
        string connectionString, VersionRecord dropRecord, CancellationToken ct)
    {
        // 取上一条非 DROP 版本的 sqlcommand
        var prevRecord = await GetPreviousNonDropRecordAsync(connectionString, dropRecord.ObjectName, dropRecord.PostTime, ct).ConfigureAwait(false);
        if (prevRecord == null)
            return new RestoreScriptResult { CanRestore = false, ErrorMessage = "未找到 DROP 之前的版本记录" };

        var script = prevRecord.ChangeType == "ALTER"
            ? ConvertAlterToCreate(prevRecord.SqlCommand, prevRecord.ObjectType)
            : prevRecord.SqlCommand;

        return new RestoreScriptResult
        {
            CanRestore = true,
            Script = script,
            Type = RestoreType.RecreateDropped
        };
    }

    /// <summary>
    /// 解析对象名为 (Schema, Name) 元组。
    /// 处理以下格式：
    ///   - "Proc"                          → ("dbo", "Proc")     无 schema 前缀
    ///   - "dbo.Proc"                      → ("dbo", "Proc")     标准二级名称
    ///   - "[dbo].[Proc]"                  → ("dbo", "Proc")     方括号包裹
    ///   - "dbo.MySchema.Proc"             → ("dbo", "Proc")     三级名称（首段 schema，末段 name）
    /// </summary>
    public static (string Schema, string Name) ParseObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return ("dbo", objectName ?? string.Empty);

        // 1. 去除所有方括号字符
        var cleaned = Regex.Replace(objectName, @"[\[\]]", string.Empty);

        // 2. 按 . 分割
        var parts = cleaned.Split('.');

        if (parts.Length == 1)
        {
            // 无 schema 前缀
            return ("dbo", parts[0].Trim());
        }
        else
        {
            // 2 段：首段为 schema、末段为 name
            // 3+ 段：首段为 schema、末段为 name（中间段视为命名空间/子 schema）
            // 注：Core 项目目标 netstandard2.0，不支持索引访问器（parts[^1]），用 parts[parts.Length - 1]
            return (parts[0].Trim(), parts[parts.Length - 1].Trim());
        }
    }

    public async Task<string?> GetServerObjectDefinitionAsync(
        string connectionString,
        string schema,
        string name,
        string objectType,
        CancellationToken ct = default)
    {
        // 按 objectType 分支处理不同对象类型
        var normalizedType = objectType?.ToUpperInvariant() ?? string.Empty;

        if (normalizedType == "TABLE")
        {
            return await BuildCreateTableScriptAsync(connectionString, schema, name, ct).ConfigureAwait(false);
        }

        // 模块类型：PROCEDURE / FUNCTION / VIEW / TRIGGER 等走 sys.sql_modules
        var moduleTypeFilter = new[]
        {
            "PROCEDURE", "FUNCTION", "VIEW", "TRIGGER",
            "SQL_STORED_PROCEDURE", "SQL_SCALAR_FUNCTION", "SQL_TABLE_VALUED_FUNCTION",
            "SQL_INLINE_TABLE_VALUED_FUNCTION"
        };

        if (!moduleTypeFilter.Contains(normalizedType))
        {
            _logger.Warning($"[VersionHistory] 不支持的对象类型: {objectType}");
            return null;
        }

        var sql = @"
            SELECT m.definition
            FROM sys.sql_modules m
            INNER JOIN sys.objects o ON m.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @name";

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@name", name);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result?.ToString();
        }
        catch (SqlException ex)
        {
            _logger.Error(ex, $"[VersionHistory] 获取服务器对象定义失败: {schema}.{name}");
            return null;
        }
    }

    /// <summary>
    /// 从系统表拼接 CREATE TABLE 脚本（覆盖字段定义、主键约束、默认值约束、索引）。
    /// 解决 P2：GetServerObjectDefinitionAsync 原本只支持 sys.sql_modules，无法处理 TABLE 类型。
    /// </summary>
    private async Task<string?> BuildCreateTableScriptAsync(
        string connectionString,
        string schema,
        string name,
        CancellationToken ct)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // 1. 查 object_id（用于后续关联查询）
            int objectId;
            using (var cmdId = new SqlCommand(
                "SELECT o.object_id FROM sys.tables o INNER JOIN sys.schemas s ON o.schema_id = s.schema_id " +
                "WHERE s.name = @schema AND o.name = @name", conn))
            {
                cmdId.Parameters.AddWithValue("@schema", schema);
                cmdId.Parameters.AddWithValue("@name", name);
                var idResult = await cmdId.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (idResult == null)
                {
                    _logger.Warning($"[VersionHistory] 表不存在: {schema}.{name}");
                    return null;
                }
                objectId = (int)idResult;
            }

            // 2. 查询字段定义 + 默认值约束（一次性合并查询，避免多次往返）
            var columnDefs = new List<string>();
            var primaryKeyColumns = new List<string>();
            using (var cmd = new SqlCommand(@"
                SELECT
                    c.column_id,
                    c.name,
                    t.name,
                    c.max_length,
                    c.precision,
                    c.scale,
                    c.is_nullable,
                    c.is_identity,
                    ISNULL(c.collation_name, ''),
                    ISNULL((SELECT TOP 1 dc.definition
                            FROM sys.default_constraints dc
                            WHERE dc.parent_object_id = c.object_id
                              AND dc.parent_column_id = c.column_id), '') AS default_def
                FROM sys.columns c
                INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
                WHERE c.object_id = @objectId
                ORDER BY c.column_id", conn))
            {
                cmd.Parameters.AddWithValue("@objectId", objectId);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var colName = reader.GetString(1);
                    var typeName = reader.GetString(2);
                    var maxLength = reader.GetInt16(3);
                    var precision = reader.GetByte(4);
                    var scale = reader.GetByte(5);
                    var isNullable = reader.GetBoolean(6);
                    var isIdentity = reader.GetBoolean(7);
                    var collation = reader.GetString(8);
                    var defaultDef = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);

                    var def = FormatColumnDefinition(
                        colName, typeName, maxLength, precision, scale,
                        isNullable, isIdentity, collation, defaultDef);
                    columnDefs.Add(def);
                }
            }

            // 3. 查询主键字段（is_primary_key = 1）
            using (var cmd = new SqlCommand(@"
                SELECT c.name
                FROM sys.index_columns ic
                INNER JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE i.object_id = @objectId AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal", conn))
            {
                cmd.Parameters.AddWithValue("@objectId", objectId);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    primaryKeyColumns.Add(reader.GetString(0));
                }
            }

            // 4. 查询普通索引（排除主键和聚集堆）
            var indexScripts = new List<string>();
            using (var cmd = new SqlCommand(@"
                SELECT i.name AS IndexName, i.type_desc, i.is_unique,
                       STUFF((SELECT ', [' + c.name + ']' + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                              FROM sys.index_columns ic
                              INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                AND ic.is_included_column = 0
                              ORDER BY ic.key_ordinal
                              FOR XML PATH('')), 1, 2, '') AS KeyCols,
                       ISNULL(STUFF((SELECT ', [' + c.name + ']'
                                     FROM sys.index_columns ic
                                     INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                       AND ic.is_included_column = 1
                                     ORDER BY ic.index_column_id
                                     FOR XML PATH('')), 1, 2, ''), '') AS IncludeCols
                FROM sys.indexes i
                WHERE i.object_id = @objectId
                  AND i.is_primary_key = 0
                  AND i.is_hypothetical = 0
                  AND i.name IS NOT NULL
                ORDER BY i.index_id", conn))
            {
                cmd.Parameters.AddWithValue("@objectId", objectId);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var indexName = reader.GetString(0);
                    var typeDesc = reader.GetString(1);
                    var isUnique = reader.GetBoolean(2);
                    var keyCols = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var includeCols = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

                    if (string.IsNullOrEmpty(keyCols))
                        continue;  // 跳过纯包含列索引（HEAP 等）

                    var sb = new System.Text.StringBuilder();
                    sb.Append("CREATE ");
                    if (isUnique) sb.Append("UNIQUE ");
                    sb.Append(typeDesc.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase) ? "CLUSTERED " : "NONCLUSTERED ");
                    sb.Append("INDEX [").Append(indexName).Append("] ON [")
                      .Append(schema).Append("].[").Append(name).Append("] (")
                      .Append(keyCols).Append(")");
                    if (!string.IsNullOrEmpty(includeCols))
                        sb.Append(" INCLUDE (").Append(includeCols).Append(")");

                    indexScripts.Add(sb.ToString());
                }
            }

            // 5. 拼接最终脚本
            var sb2 = new System.Text.StringBuilder();
            sb2.Append("CREATE TABLE [").Append(schema).Append("].[").Append(name).Append("]\n(\n");

            for (int i = 0; i < columnDefs.Count; i++)
            {
                if (i > 0) sb2.Append(",\n");
                sb2.Append("    ").Append(columnDefs[i]);
            }

            // 主键约束
            if (primaryKeyColumns.Count > 0)
            {
                sb2.Append(",\n    CONSTRAINT [PK_").Append(name).Append("] PRIMARY KEY CLUSTERED (");
                sb2.Append(string.Join(", ", primaryKeyColumns.Select(c => "[" + c + "]")));
                sb2.Append(")");
            }

            sb2.Append("\n)");
            sb2.Append(";\nGO\n");

            foreach (var idx in indexScripts)
            {
                sb2.Append(idx).Append(";\nGO\n");
            }

            return sb2.ToString();
        }
        catch (SqlException ex)
        {
            _logger.Error(ex, $"[VersionHistory] 拼接 CREATE TABLE 脚本失败: {schema}.{name}");
            return null;
        }
    }

    /// <summary>
    /// 格式化单个字段定义（含类型、长度、IDENTITY、DEFAULT、NULL 等）。
    /// </summary>
    private static string FormatColumnDefinition(
        string colName,
        string typeName,
        short maxLength,
        byte precision,
        byte scale,
        bool isNullable,
        bool isIdentity,
        string collation,
        string defaultDef = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[").Append(colName).Append("] [").Append(typeName).Append("]");

        // 长度/精度/小数位
        if (typeName == "nvarchar" || typeName == "nchar" || typeName == "varchar" || typeName == "char"
            || typeName == "binary" || typeName == "varbinary")
        {
            if (maxLength == -1)
                sb.Append("(MAX)");
            else if (typeName.StartsWith("n", StringComparison.Ordinal))
                sb.Append("(").Append(maxLength / 2).Append(")");
            else
                sb.Append("(").Append(maxLength).Append(")");
        }
        else if (typeName == "decimal" || typeName == "numeric")
        {
            sb.Append("(").Append(precision).Append(",").Append(scale).Append(")");
        }

        // COLLATE
        if (!string.IsNullOrEmpty(collation) &&
            (typeName == "nvarchar" || typeName == "nchar" ||
             typeName == "varchar" || typeName == "char" ||
             typeName == "text" || typeName == "ntext"))
        {
            sb.Append(" COLLATE ").Append(collation);
        }

        // IDENTITY
        if (isIdentity)
            sb.Append(" IDENTITY(1,1)");

        // DEFAULT
        if (!string.IsNullOrEmpty(defaultDef))
            sb.Append(" DEFAULT ").Append(defaultDef);

        // NULL / NOT NULL
        sb.Append(isNullable ? " NULL" : " NOT NULL");

        return sb.ToString();
    }

    #region Private Helpers

    private async Task<VersionRecord?> GetRecordByIdAsync(string connectionString, long id, CancellationToken ct)
    {
        var sql = $@"
            SELECT TOP 1
                [ID], [databasename], [eventtype], [objectname], [objecttype],
                [sqlcommand], [loginname], [hostname], [PostTime], [Version]
            FROM [{_databaseName}].[dbo].[{_tableName}]
            WHERE [ID] = @id";

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                return MapRecord(reader);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<VersionRecord?> GetPreviousNonDropRecordAsync(
        string connectionString, string objectName, DateTime beforeTime, CancellationToken ct)
    {
        // 兼容两种存储格式：dbo.XX 和 XX
        var hasSchema = objectName.Contains('.');
        var bareName = hasSchema ? objectName.Split('.').Last() : objectName;

        var sql = $@"
            SELECT TOP 1
                [ID], [databasename], [eventtype], [objectname], [objecttype],
                [sqlcommand], [loginname], [hostname], [PostTime], [Version]
            FROM [{_databaseName}].[dbo].[{_tableName}]
            WHERE ([objectname] = @objectName {(hasSchema ? "OR [objectname] = @bareName" : "")})
              AND [PostTime] < @beforeTime
              AND [eventtype] NOT LIKE 'DROP_%'
            ORDER BY [PostTime] DESC";

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@objectName", objectName);
            if (hasSchema)
                cmd.Parameters.AddWithValue("@bareName", bareName);
            cmd.Parameters.AddWithValue("@beforeTime", beforeTime);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                return MapRecord(reader);

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 列表场景映射：不含 sqlcommand 大字段。列表 UI 仅展示元数据，脚本正文不在此返回，
    /// 需要时在调用方通过 GetVersionSqlAsync(Id) 按需懒加载，避免一次性搬运数百 KB 大字段。
    /// </summary>
    private static VersionRecord MapListRecord(SqlDataReader reader)
    {
        return new VersionRecord
        {
            Id = Convert.ToInt64(reader["ID"]),
            DatabaseName = reader["databasename"]?.ToString() ?? "",
            EventType = reader["eventtype"]?.ToString() ?? "",
            ObjectName = reader["objectname"]?.ToString() ?? "",
            ObjectType = reader["objecttype"]?.ToString() ?? "",
            SqlCommand = string.Empty,
            LoginName = reader["loginname"]?.ToString() ?? "",
            HostName = reader["hostname"]?.ToString() ?? "",
            PostTime = reader["PostTime"] != DBNull.Value ? Convert.ToDateTime(reader["PostTime"]) : DateTime.MinValue,
            Version = reader["Version"] != DBNull.Value ? Convert.ToInt32(reader["Version"]) : 0
        };
    }

    private static VersionRecord MapRecord(SqlDataReader reader)
    {
        return new VersionRecord
        {
            Id = Convert.ToInt64(reader["ID"]),
            DatabaseName = reader["databasename"]?.ToString() ?? "",
            EventType = reader["eventtype"]?.ToString() ?? "",
            ObjectName = reader["objectname"]?.ToString() ?? "",
            ObjectType = reader["objecttype"]?.ToString() ?? "",
            SqlCommand = reader["sqlcommand"]?.ToString() ?? "",
            LoginName = reader["loginname"]?.ToString() ?? "",
            HostName = reader["hostname"]?.ToString() ?? "",
            PostTime = reader["PostTime"] != DBNull.Value ? Convert.ToDateTime(reader["PostTime"]) : DateTime.MinValue,
            Version = reader["Version"] != DBNull.Value ? Convert.ToInt32(reader["Version"]) : 0
        };
    }

    private static string ConvertCreateToAlter(string sqlCommand, string objectType)
    {
        var typePattern = GetObjectTypePattern(objectType);
        if (string.IsNullOrEmpty(typePattern)) return sqlCommand;

        return Regex.Replace(
            sqlCommand,
            $@"^\s*CREATE\s+({typePattern})",
            @"ALTER $1",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private static string ConvertAlterToCreate(string sqlCommand, string objectType)
    {
        var typePattern = GetObjectTypePattern(objectType);
        if (string.IsNullOrEmpty(typePattern)) return sqlCommand;

        return Regex.Replace(
            sqlCommand,
            $@"^\s*ALTER\s+({typePattern})",
            @"CREATE $1",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private static string GetObjectTypePattern(string objectType)
    {
        return objectType?.ToUpperInvariant() switch
        {
            "PROCEDURE" => "PROCEDURE|PROC",
            "FUNCTION" => "FUNCTION",
            "VIEW" => "VIEW",
            "TRIGGER" => "TRIGGER",
            _ => objectType ?? ""
        };
    }

    #endregion
}