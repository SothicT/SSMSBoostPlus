using System;
using System.Collections.Generic;
using System.Linq;

namespace SSMSBoostPlus.VersionGuard.Models;

/// <summary>
/// Represents a reference to a SQL database object extracted from code.
/// </summary>
public class ObjectReference
{
    /// <summary>
    /// Object name (e.g., usp_GetData).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Schema name (e.g., dbo). Defaults to "dbo" if not specified.
    /// </summary>
    public string Schema { get; set; } = "dbo";

    /// <summary>
    /// Database name. Empty if not specified (uses current database).
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// Object type (PROCEDURE, FUNCTION, VIEW, TRIGGER).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Whether this object is defined by a CREATE/ALTER statement in the script (true),
    /// or merely referenced by an EXEC statement (false).
    /// </summary>
    public bool IsDefinition { get; set; } = true;

    /// <summary>
    /// Full qualified name: [Database].[Schema].[Name].
    /// </summary>
    public string FullName => string.IsNullOrEmpty(Database)
        ? $"{Schema}.{Name}"
        : $"{Database}.{Schema}.{Name}";

    /// <summary>
    /// Line number where the object reference was found (start line).
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// End line number of the object definition in the script.
    /// For CREATE/ALTER statements, this is the last line of the statement body.
    /// </summary>
    public int EndLineNumber { get; set; }
}

/// <summary>
/// 本地脚本与数据库脚本的快速比对结果
/// </summary>
public enum DiffComparisonType
{
    /// <summary>尚未比对</summary>
    Unknown,
    /// <summary>完全一致</summary>
    Identical,
    /// <summary>只有新增（本地有、DB 无）</summary>
    OnlyAdded,
    /// <summary>只有删除（DB 有、本地无）</summary>
    OnlyDeleted,
    /// <summary>有冲突（既有新增又有删除）</summary>
    Conflict,
    /// <summary>对象在数据库中不存在</summary>
    NotFound
}

/// <summary>
/// Version information for a database object.
/// </summary>
public class VersionInfo
{
    /// <summary>
    /// Object reference.
    /// </summary>
    public ObjectReference ObjectRef { get; set; } = new();

    /// <summary>
    /// Last modification time from sys.objects.modify_date.
    /// </summary>
    public DateTime? DbModifyDate { get; set; }

    /// <summary>
    /// Time when the script was opened.
    /// </summary>
    public DateTime ScriptOpenTime { get; set; }

    /// <summary>
    /// Whether the object exists in the database.
    /// </summary>
    public bool ObjectExists { get; set; }

    /// <summary>
    /// Whether the DB version is newer than the script open time.
    /// </summary>
    public bool IsOutdated => ObjectExists && DbModifyDate.HasValue && DbModifyDate.Value > ScriptOpenTime;

    /// <summary>
    /// Time difference between DB modify time and script open time.
    /// </summary>
    public TimeSpan? TimeDifference => DbModifyDate.HasValue ? DbModifyDate.Value - ScriptOpenTime : null;

    /// <summary>
    /// 时间差的中文描述（精确到秒，不含小数）。
    /// </summary>
    public string TimeDifferenceText
    {
        get
        {
            if (TimeDifference == null) return "未知";

            var ts = TimeDifference.Value;
            // 使用绝对值，取整到秒
            var totalSeconds = (int)Math.Abs(ts.TotalSeconds);

            if (totalSeconds < 60)
                return $"{totalSeconds}秒前";

            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;

            if (totalSeconds < 3600) // < 1小时
                return seconds > 0 ? $"{minutes}分{seconds}秒前" : $"{minutes}分钟前";

            var hours = minutes / 60;
            minutes = minutes % 60;

            if (totalSeconds < 86400) // < 1天
                return minutes > 0 ? $"{hours}小时{minutes}分前" : $"{hours}小时前";

            var days = hours / 24;
            hours = hours % 24;

            return hours > 0 ? $"{days}天{hours}小时前" : $"{days}天前";
        }
    }

    /// <summary>
    /// 本地脚本与 DB 定义的快速比对结果
    /// </summary>
    public DiffComparisonType ComparisonType { get; set; } = DiffComparisonType.Unknown;

    /// <summary>
    /// 比对结果的中文显示文字
    /// </summary>
    public string ComparisonText => ComparisonType switch
    {
        DiffComparisonType.Identical => "一致",
        DiffComparisonType.OnlyAdded => "新增",
        DiffComparisonType.OnlyDeleted => "删减",
        DiffComparisonType.Conflict => "有冲突",
        DiffComparisonType.NotFound => "对象不存在",
        _ => "未比对"
    };

    /// <summary>
    /// 用户是否已审查该对象的差异比对。
    /// 在 VersionWarningDialog 中，查看差异并选择 AcceptLeft/AcceptRight 后标记为 true。
    /// </summary>
    public bool IsReviewed { get; set; } = false;

    /// <summary>
    /// 是否需要强制审查差异。
    /// 当比对结果为 Conflict、OnlyAdded、OnlyDeleted 时返回 true，
    /// "继续执行"按钮在存在未审查的此类对象时将被禁用。
    /// </summary>
    public bool RequiresReview => ComparisonType switch
    {
        DiffComparisonType.Conflict => true,
        DiffComparisonType.OnlyAdded => true,
        DiffComparisonType.OnlyDeleted => true,
        _ => false
    };

    /// <summary>
    /// 审查状态的中文显示文字
    /// </summary>
    public string ReviewStatusText
    {
        get
        {
            if (!RequiresReview) return "无需审查";
            return IsReviewed ? "已审查" : "待审查";
        }
    }
}

/// <summary>
/// Result of a version check operation.
/// </summary>
public class VersionCheckResult
{
    /// <summary>
    /// All checked objects and their version info.
    /// </summary>
    public List<VersionInfo> VersionInfos { get; set; } = new();

    /// <summary>
    /// Objects that are outdated (DB version newer than script open time).
    /// </summary>
    public List<VersionInfo> OutdatedObjects => VersionInfos.Where(v => v.IsOutdated).ToList();

    /// <summary>
    /// Whether any object is outdated.
    /// </summary>
    public bool HasOutdated => OutdatedObjects.Any();

    /// <summary>
    /// Whether the check was skipped (due to skip token).
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// Whether an error occurred during the check.
    /// </summary>
    public bool HasError { get; set; }

    /// <summary>
    /// Error message if an error occurred.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Time taken for the check in milliseconds.
    /// </summary>
    public long ElapsedMs { get; set; }
}
