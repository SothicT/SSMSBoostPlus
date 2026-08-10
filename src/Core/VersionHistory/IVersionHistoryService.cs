using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SSMSBoostPlus.VersionHistory.Models;

namespace SSMSBoostPlus.VersionHistory;

public interface IVersionHistoryService
{
    Task<List<VersionRecord>> GetObjectHistoryAsync(
        string connectionString,
        string objectName,
        string? databaseName = null,
        int top = 100,
        CancellationToken ct = default);

    Task<List<VersionRecord>> GetRecentChangesAsync(
        string connectionString,
        int top = 50,
        string? databaseFilter = null,
        CancellationToken ct = default);

    Task<List<ObjectSummary>> GetObjectListAsync(
        string connectionString,
        string? databaseFilter = null,
        int? top = null,
        string? keyword = null,
        CancellationToken ct = default);

    Task<string?> GetVersionSqlAsync(
        string connectionString,
        long versionId,
        CancellationToken ct = default);

    Task<RestoreScriptResult> GenerateRestoreScriptAsync(
        string connectionString,
        long versionId,
        CancellationToken ct = default);

    Task<string?> GetServerObjectDefinitionAsync(
        string connectionString,
        string schema,
        string name,
        string objectType,
        CancellationToken ct = default);
}