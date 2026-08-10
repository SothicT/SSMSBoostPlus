using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.VersionGuard;

/// <summary>
/// Orchestrates version checking: extracts SQL object references from script,
/// queries database for modify dates, and compares with script open time.
/// </summary>
public class VersionChecker
{
    private readonly ObjectExtractor _objectExtractor;
    private readonly IDatabaseService _databaseService;
    private readonly IConfigurationService _configurationService;
    private readonly ILoggerService _logger;

    public VersionChecker(
        ObjectExtractor objectExtractor,
        IDatabaseService databaseService,
        IConfigurationService configurationService,
        ILoggerService logger)
    {
        _objectExtractor = objectExtractor;
        _databaseService = databaseService;
        _configurationService = configurationService;
        _logger = logger;
    }

    /// <summary>
    /// Check if the script references any database objects that have been modified
    /// since the script was opened.
    /// </summary>
    /// <param name="sqlScript">The SQL script content to check.</param>
    /// <param name="scriptOpenTime">When the script was opened/last synced.</param>
    /// <param name="connectionString">Database connection string.</param>
    /// <param name="currentDatabase">The current database context.</param>
    /// <returns>A VersionCheckResult with details about any outdated objects.</returns>
    public async Task<VersionCheckResult> CheckVersionAsync(
        string sqlScript,
        DateTime scriptOpenTime,
        string connectionString,
        string currentDatabase)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var config = await _configurationService.GetVersionGuardConfigurationAsync();

            // Check if version guard is enabled
            if (!config.Enabled)
            {
                _logger.Information("版本守卫已禁用，跳过检查。");
                return new VersionCheckResult
                {
                    Skipped = true,
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                };
            }

            // Check for skip/ask tokens
            var tokenResult = _objectExtractor.DetectTokens(sqlScript, config.AskToken, config.SkipToken);
            if (tokenResult.HasSkipToken)
            {
                _logger.Information("检测到跳过令牌，跳过版本检查。");
                return new VersionCheckResult
                {
                    Skipped = true,
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                };
            }

            // Extract object references from script
            var objectRefs = _objectExtractor.ExtractObjects(sqlScript);
            if (objectRefs.Count == 0)
            {
                _logger.Debug("脚本中未找到数据库对象引用。");
                return new VersionCheckResult
                {
                    VersionInfos = new List<VersionInfo>(),
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                };
            }

            // Filter excluded objects and databases
            // Only check objects that are DEFINED by CREATE/ALTER statements in the script
            // (skip EXEC references — we only care about the object the user is editing)
            var filteredRefs = objectRefs
                .Where(r => r.IsDefinition)
                .Where(r => !config.ExcludedObjects.Any(e => string.Equals(e, r.FullName, StringComparison.OrdinalIgnoreCase)))
                .Where(r => !config.ExcludedDatabases.Any(e => string.Equals(e, r.Database ?? currentDatabase, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Query database for modify dates
            var versionInfos = new List<VersionInfo>();
            foreach (var objRef in filteredRefs)
            {
                try
                {
                    var modifyDate = await _databaseService.GetObjectModifyDateAsync(connectionString, objRef);

                    var info = new VersionInfo
                    {
                        ObjectRef = objRef,
                        DbModifyDate = modifyDate,
                        ScriptOpenTime = scriptOpenTime,
                        ObjectExists = modifyDate.HasValue
                    };

                    versionInfos.Add(info);
                    _logger.Debug($"Object {objRef.FullName}: ModifyDate={modifyDate}, OpenTime={scriptOpenTime}, Outdated={info.IsOutdated}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"查询 {objRef.FullName} 的修改时间失败: {ex.Message}");
                    versionInfos.Add(new VersionInfo
                    {
                        ObjectRef = objRef,
                        ScriptOpenTime = scriptOpenTime,
                        ObjectExists = false
                    });
                }
            }

            stopwatch.Stop();

            var result = new VersionCheckResult
            {
                VersionInfos = versionInfos,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };

            if (result.HasOutdated)
            {
                _logger.Warning($"版本检查发现 {result.OutdatedObjects.Count} 个过时对象。");
                // 异步加载 DB 定义进行快速文本比对
                await PerformQuickComparisonAsync(connectionString, sqlScript, result);
            }
            else
            {
                _logger.Information("版本检查通过 — 无过时对象。");
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(ex, $"版本检查失败: {ex.Message}");
            return new VersionCheckResult
            {
                HasError = true,
                ErrorMessage = ex.Message,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// 对过时对象执行快速文本比对（本地脚本 vs DB 定义），填充 ComparisonType。
    /// 使用 DiffPlex 判断差异类型：只有新增 / 有冲突 / 只有删减。
    /// </summary>
    private async Task PerformQuickComparisonAsync(string connectionString, string sqlScript, VersionCheckResult result)
    {
        foreach (var vi in result.OutdatedObjects)
        {
            vi.ComparisonType = await CalculateComparisonTypeAsync(connectionString, sqlScript, vi);
            _logger.Debug($"快速比对 {vi.ObjectRef.FullName}: {vi.ComparisonText}");
        }
    }

    /// <summary>
    /// 计算单个对象的比对结果类型（本地脚本 vs DB 定义）。
    /// 提取出的复用方法，供 PerformQuickComparisonAsync 和 RefreshComparisonAsync 共用。
    /// </summary>
    private async Task<DiffComparisonType> CalculateComparisonTypeAsync(string connectionString, string sqlScript, VersionInfo vi)
    {
        if (!vi.ObjectExists)
            return DiffComparisonType.NotFound;

        string? dbDefinition;
        try
        {
            dbDefinition = await _databaseService.GetObjectDefinitionAsync(connectionString, vi.ObjectRef);
        }
        catch
        {
            return DiffComparisonType.NotFound;
        }

        if (string.IsNullOrEmpty(dbDefinition))
            return DiffComparisonType.NotFound;

        var extractor = new ObjectExtractor();
        var localScript = extractor.ExtractObjectScript(sqlScript, vi.ObjectRef);
        if (string.IsNullOrEmpty(localScript))
            return DiffComparisonType.OnlyDeleted;

        var calculator = new DiffEngine.DiffCalculator();
        var diff = calculator.GetSideBySideDiff(dbDefinition!, localScript!);
        var leftLines = diff.OldText.Lines;
        var rightLines = diff.NewText.Lines;
        if (leftLines == null || rightLines == null)
            return DiffComparisonType.Unknown;

        bool hasInserted = rightLines.Any(l => l.Type == DiffPlex.DiffBuilder.Model.ChangeType.Inserted);
        bool hasDeleted = leftLines.Any(l => l.Type == DiffPlex.DiffBuilder.Model.ChangeType.Deleted);
        bool hasModified = leftLines.Any(l => l.Type == DiffPlex.DiffBuilder.Model.ChangeType.Modified);

        if (!hasInserted && !hasDeleted && !hasModified)
            return DiffComparisonType.Identical;
        else if (hasInserted && !hasDeleted && !hasModified)
            return DiffComparisonType.OnlyAdded;
        else if (!hasInserted && hasDeleted && !hasModified)
            return DiffComparisonType.OnlyDeleted;
        else
            return DiffComparisonType.Conflict;
    }

    /// <summary>
    /// 刷新单个对象的比对结果（重新查询 DB 定义并执行 DiffPlex 比对）。
    /// 用于二次校验检测到变化时，确保 DataGrid 中"比对结果"列与实际差异一致。
    /// </summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="sqlScript">当前 SQL 脚本内容。</param>
    /// <param name="versionInfo">需要重新比对的过时对象信息。</param>
    public async Task RefreshComparisonAsync(string connectionString, string sqlScript, VersionInfo versionInfo)
    {
        try
        {
            versionInfo.ComparisonType = await CalculateComparisonTypeAsync(connectionString, sqlScript, versionInfo);
            versionInfo.ObjectExists = versionInfo.DbModifyDate.HasValue;
            _logger.Debug($"刷新比对结果 {versionInfo.ObjectRef.FullName}: {versionInfo.ComparisonText}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"刷新 {versionInfo.ObjectRef.FullName} 的比对结果失败");
            versionInfo.ComparisonType = DiffComparisonType.Unknown;
        }
    }

    /// <summary>
    /// 获取对象的最新 modify_date（绕过缓存）。
    /// 内部先调用 ClearCache() 清除 5 分钟 TTL 缓存，确保查询到的是数据库当前最新值。
    /// 用于"继续执行"按钮的二次校验：审查期间其他用户可能更新了服务器脚本。
    /// </summary>
    /// <param name="connectionString">数据库连接字符串。</param>
    /// <param name="database">数据库名。</param>
    /// <param name="schema">架构名。</param>
    /// <param name="name">对象名。</param>
    /// <returns>最新的 modify_date，查询失败或对象不存在时返回 null。</returns>
    public async Task<DateTime?> GetLatestModifyDateAsync(string connectionString, string database, string schema, string name)
    {
        try
        {
            // 清除缓存，确保获取数据库最新值而非缓存中的旧值
            _databaseService.ClearCache();
            var objectRef = new ObjectReference { Database = database, Schema = schema, Name = name };
            return await _databaseService.GetObjectModifyDateAsync(connectionString, objectRef);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"获取 {schema}.{name} 的最新修改时间失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the latest definition of a database object from the database.
    /// </summary>
    /// <param name="connectionString">Database connection string.</param>
    /// <param name="database">Database name.</param>
    /// <param name="schema">Schema name.</param>
    /// <param name="name">Object name.</param>
    /// <returns>The object definition SQL, or null if not found.</returns>
    public async Task<string?> GetLatestDefinitionAsync(string connectionString, string database, string schema, string name)
    {
        try
        {
            var objectRef = new ObjectReference { Database = database, Schema = schema, Name = name };
            return await _databaseService.GetObjectDefinitionAsync(connectionString, objectRef);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"获取 {schema}.{name} 的定义失败: {ex.Message}");
            return null;
        }
    }
}
