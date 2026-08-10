using System;

namespace SSMSBoostPlus.VersionHistory.Models;

/// <summary>
/// 对应 DBTrigger_Object_Version_Table 的一条记录
/// </summary>
public class VersionRecord
{
    public long Id { get; set; }
    public string DatabaseName { get; set; } = "";
    public string EventType { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string SqlCommand { get; set; } = "";
    public string LoginName { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTime PostTime { get; set; }
    public int Version { get; set; }

    /// <summary>简化的变更类型（CREATE / ALTER / DROP）</summary>
    public string ChangeType => EventType switch
    {
        var e when e.StartsWith("CREATE_") => "CREATE",
        var e when e.StartsWith("ALTER_") => "ALTER",
        var e when e.StartsWith("DROP_") => "DROP",
        _ => EventType
    };

    /// <summary>变更类型颜色（UI 绑定用）</summary>
    public string ChangeTypeColor => ChangeType switch
    {
        "CREATE" => "#27AE60",
        "ALTER" => "#F39C12",
        "DROP" => "#E74C3C",
        _ => "#95A5A6"
    };

    /// <summary>格式化的时间显示（yyyy-MM-dd HH:mm:ss）</summary>
    public string FormattedTime => PostTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>序号（UI 绑定用，由加载时赋值）</summary>
    public int RowNumber { get; set; }

    /// <summary>Display text for combo box: objectname (versionCount)</summary>
    public string DisplayText => $"{ObjectName}（{Version} 版本）";
}

/// <summary>
/// 对象摘要（搜索下拉框用）
/// </summary>
public class ObjectSummary
{
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public int VersionCount { get; set; }
    public DateTime LastModified { get; set; }
    public string LastModifiedBy { get; set; } = "";
    /// <summary>总对象数（用于截断提示，由 COUNT(*) OVER() 窗口函数填充）</summary>
    public int TotalCount { get; set; }

    /// <summary>下拉框显示文本：dbo.usp_GetData（12 版本）</summary>
    public string DisplayText => $"{ObjectName}（{VersionCount} 版本）";

    /// <summary>对象类型缩写标签（SP/FN/VW/TR/TBL/TYP/OBJ）</summary>
    public string TypeAbbreviation => ObjectType?.ToUpperInvariant() switch
    {
        "PROCEDURE" => "SP",
        "PROC" => "SP",
        "FUNCTION" => "FN",
        "VIEW" => "VW",
        "TRIGGER" => "TR",
        "TABLE" => "TBL",
        "TYPE" => "TYP",
        _ => "OBJ"
    };

    /// <summary>类型标签颜色（hex 字符串，供 WPF 绑定）</summary>
    public string TypeColor => ObjectType?.ToUpperInvariant() switch
    {
        "PROCEDURE" => "#0078D4",
        "PROC" => "#0078D4",
        "FUNCTION" => "#884EA0",
        "VIEW" => "#27AE60",
        "TRIGGER" => "#E67E22",
        "TABLE" => "#34495E",
        "TYPE" => "#7F8C8D",
        _ => "#95A5A6"
    };

    public override string ToString() => DisplayText;

    /// <summary>类型排序优先级（SP > FN > TBL > VW > TR > TYP > OBJ）</summary>
    public int TypeSortOrder => ObjectType?.ToUpperInvariant() switch
    {
        "PROCEDURE" => 0,
        "PROC" => 0,
        "FUNCTION" => 1,
        "TABLE" => 2,
        "VIEW" => 3,
        "TRIGGER" => 4,
        "TYPE" => 5,
        _ => 6
    };
}

/// <summary>
/// 恢复脚本结果
/// </summary>
public class RestoreScriptResult
{
    public bool CanRestore { get; set; }
    public string? Script { get; set; }
    public string? ErrorMessage { get; set; }
    public RestoreType Type { get; set; }
}

public enum RestoreType
{
    /// <summary>ALTER 恢复：将历史 CREATE 语句转为 ALTER</summary>
    AlterToHistory,
    /// <summary>CREATE 恢复：对象已被 DROP，用历史定义重建</summary>
    RecreateDropped,
    /// <summary>DROP 恢复：撤销 CREATE 操作，生成 DROP 脚本</summary>
    DropCreated
}