# SSMSBoost++ 版本管控功能 — 细化实施计划

> **编写日期**: 2026-06-30
> **评审日期**: 2026-07-02
> **项目路径**: `F:\2.C#_Core\SSMSBoostPlus`
> **目标**: 在现有 SSMSBoost++ 插件基础上，将 VersionGuard（防呆拦截）扩展为完整的版本管控体系

---

## 〇、方案评审意见（2026-07-02）

### 评审结论

方案整体架构清晰，分阶段渐进式交付的策略合理。Phase 1 存在若干技术细节缺陷，已在下方各小节中以 `🔒 [评审修复]` 标注修改内容。

### 评审决策汇总

| 决策项 | 选择 | 说明 |
|--------|------|------|
| 实施范围 | 仅 Phase 1 | Phase 2/3/4 后续再做 |
| 变更记录方式 | DDL Trigger + ExecutionInterceptor 双保留 | 需去重机制，通过 `Source` 字段 + 时间窗口去重 |
| 配置拆分 | 拆分为独立 `VersionHistoryConfig` | 不再往 `VersionGuardConfiguration` 中添加属性 |
| Phase 3 | 暂不考虑 | 后续再定 |

### 严重问题修复清单

| # | 问题 | 修复方案 | 涉及小节 |
|---|------|----------|----------|
| S1 | DDL Trigger 对 DROP 事件会失败（`sys.sql_modules` 无记录，`NewDefinition NOT NULL` 导致 INSERT 失败） | `NewDefinition` 列改为 `NULL`；DROP 事件时写入 NULL | 2.3, 2.4 |
| S2 | `RecordChangeAsync` 方法在接口中未定义但集成代码中调用了 | 补充接口方法定义 | 2.5 |
| S3 | DDL Trigger 与 ExecutionInterceptor 双重记录导致数据重复 | 新增 `Source` 列，`RecordChangeAsync` 先查询时间窗口内 DDLTrigger 记录，命中则 UPDATE 否则 INSERT | 2.3, 2.4, 2.5, 2.9 |
| S4 | 回滚脚本拼接语法错误（`ALTER ... AS {PreviousDefinition}` 会产生两个 `AS`） | 将 `PreviousDefinition` 中的 `CREATE` 替换为 `ALTER`（正则 `^\s*CREATE` → `ALTER`） | 2.7 |
| S5 | 可配置表名/触发器名未在 SQL 中动态化 | Service 层使用配置值动态拼接 SQL | 2.4, 2.7 |
| S6 | 缺少 `ConfigureAwait(false)` 和异常处理 | 全部 async 方法加 `ConfigureAwait(false)` 和 try-catch | 2.5, 2.7 |
| S7 | 历史表数据清理机制缺失 | 新增 `CleanupHistoryAsync` 方法 + `MaxHistoryRows`/`RetentionDays` 配置 | 2.5, 2.7, 2.13 |
| S8 | `VersionGuardConfiguration` 配置膨胀 | 拆分为独立 `VersionHistoryConfig` 类 | 2.13 |
| S9 | VSCT 中 `Group_VersionHistory` 定义但未使用 | 移除未使用的 Group 定义，补充 `CommandPlacement` 到右键上下文菜单 | 2.11 |
| S10 | DDL Trigger 权限问题 | `EnsureTrackingInfrastructureAsync` 捕获 `SqlException`，返回 `EnsureResult` 而非抛出异常 | 2.5, 2.7 |

---

## 一、现状与目标

### 当前 VersionGuard 能力

用户按 F5 执行 SQL 时，自动拦截并比对 `sys.objects.modify_date` vs 脚本打开时间，发现有人改过则弹出警告对话框。能发现"有人改了"，但不知道"改了什么"、"谁改的"、"能否回滚"。

### 最终目标

将 VersionGuard 升级为完整的版本管控体系：

1. **版本历史 + 回滚**（Phase 1）— DDL Trigger 自动记录每次对象变更，支持历史追溯和回滚
2. **SQL 审核引擎**（Phase 2）— 基于 ScriptDom 的审核规则，执行前自动检查 SQL 质量
3. **代码审批流程**（Phase 3）— 变更前审核、变更中审批、变更后追溯的完整闭环
4. **Git 提交**（Phase 4）— SSMS 内一键提交到 Git（补充功能）

---

## 二、Phase 1 — 版本历史 + 回滚

### 2.1 概述

在 SQL Server 数据库中创建版本历史表，通过 DDL Trigger 自动捕获所有对象变更，配合 SSMS 内对话框实现历史追溯和回滚。

### 2.2 新增目录结构

```
src/Core/VersionControl/                     ← 新建
  ├── IVersionHistoryService.cs
  ├── VersionHistoryService.cs
  └── Models/
      └── VersionHistoryModels.cs
```

### 2.3 版本历史表结构

> 🔒 [评审修复 S1+S3] `NewDefinition` 改为 `NULL`（DROP 事件时对象已删除，无定义可取）；新增 `Source` 列用于区分变更来源（DDLTrigger / SSMSBoost++），支持去重机制。

```sql
CREATE TABLE [dbo].[__SSMSBoost_VersionHistory] (
    HistoryId       BIGINT IDENTITY(1,1) PRIMARY KEY,
    ObjectSchema    NVARCHAR(128)   NOT NULL DEFAULT 'dbo',
    ObjectName      NVARCHAR(256)   NOT NULL,
    ObjectType      NVARCHAR(50)    NOT NULL,  -- PROCEDURE/FUNCTION/VIEW/TRIGGER
    DatabaseName    NVARCHAR(128)   NOT NULL,
    PreviousDefinition  NVARCHAR(MAX),         -- 变更前的定义（null 表示首次创建）
    NewDefinition       NVARCHAR(MAX),         -- 🔒 改为 NULL：DROP 事件时对象已删除，无定义可取
    ChangeType      NVARCHAR(20)    NOT NULL,  -- CREATE/ALTER/DROP
    Source          NVARCHAR(20)    NOT NULL DEFAULT 'DDLTrigger',  -- 🔒 新增：DDLTrigger / SSMSBoost++
    ModifiedBy      NVARCHAR(128)   NOT NULL,  -- 操作人（LOGIN_NAME）
    ModifiedAt      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    SessionId       INT,                       -- SQL Server session ID
    HostName        NVARCHAR(128),             -- HOST_NAME()
    ApplicationName NVARCHAR(256),             -- APP_NAME()
    GitCommitSha    NVARCHAR(40),              -- 关联的 Git 提交 SHA（后续补充）
    Remark          NVARCHAR(500)              -- 备注/变更说明
);
CREATE INDEX IX_VersionHistory_Object
    ON [dbo].[__SSMSBoost_VersionHistory](ObjectSchema, ObjectName, DatabaseName);
CREATE INDEX IX_VersionHistory_Time
    ON [dbo].[__SSMSBoost_VersionHistory](ModifiedAt DESC);
-- 🔒 新增：去重查询索引（按对象 + 变更类型 + 时间范围查找 DDLTrigger 记录）
CREATE INDEX IX_VersionHistory_Dedup
    ON [dbo].[__SSMSBoost_VersionHistory](ObjectSchema, ObjectName, ChangeType, Source, ModifiedAt DESC);
```

### 2.4 DDL Trigger

> 🔒 [评审修复 S1+S3+S5] 修复 DROP 事件时 `@currentDef` 为 NULL 的问题；新增 `Source` 列写入 `DDLTrigger`；表名通过 Service 层动态拼接（下方 SQL 中 `{TableName}` 为占位符，实际由 `VersionHistoryService` 使用配置值替换）。

```sql
CREATE TRIGGER [{TriggerName}]
ON DATABASE
FOR CREATE_PROCEDURE, ALTER_PROCEDURE, DROP_PROCEDURE,
    CREATE_FUNCTION, ALTER_FUNCTION, DROP_FUNCTION,
    CREATE_VIEW, ALTER_VIEW, DROP_VIEW,
    CREATE_TRIGGER, ALTER_TRIGGER, DROP_TRIGGER
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @data XML = EVENTDATA();
    DECLARE @schema NVARCHAR(128) = @data.value('(/EVENT_INSTANCE/SchemaName)[1]', 'NVARCHAR(128)');
    DECLARE @name   NVARCHAR(256) = @data.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(256)');
    DECLARE @type   NVARCHAR(50)  = @data.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(50)');
    DECLARE @event  NVARCHAR(20)  = @data.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(20)');
    DECLARE @login  NVARCHAR(128) = @data.value('(/EVENT_INSTANCE/LoginName)[1]', 'NVARCHAR(128)');

    DECLARE @changeType NVARCHAR(20) =
        CASE WHEN @event LIKE 'CREATE_%' THEN 'CREATE'
             WHEN @event LIKE 'ALTER_%'  THEN 'ALTER'
             WHEN @event LIKE 'DROP_%'   THEN 'DROP'
             ELSE @event END;

    -- 🔒 修复：DROP 事件时对象已被删除，sys.sql_modules 无记录，@currentDef 为 NULL
    -- CREATE/ALTER 事件时从 sys.sql_modules 获取当前定义
    DECLARE @currentDef NVARCHAR(MAX);
    IF @changeType <> 'DROP'
    BEGIN
        SELECT @currentDef = [definition] FROM sys.sql_modules
        WHERE object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name));
    END
    -- DROP 事件时 @currentDef 保持 NULL

    -- PreviousDefinition 逻辑：
    --   CREATE → NULL（首次创建，无前版本）
    --   ALTER  → 上一条记录的 NewDefinition
    --   DROP   → 上一条记录的 NewDefinition（对象被删除前的最后定义）
    DECLARE @prevDef NVARCHAR(MAX);
    SELECT TOP 1 @prevDef = NewDefinition FROM [dbo].[{TableName}]
    WHERE ObjectSchema = @schema AND ObjectName = @name
    ORDER BY HistoryId DESC;

    INSERT INTO [dbo].[{TableName}]
        (ObjectSchema, ObjectName, ObjectType, DatabaseName,
         PreviousDefinition, NewDefinition, ChangeType, Source,
         ModifiedBy, SessionId, HostName, ApplicationName)
    VALUES
        (@schema, @name, @type, DB_NAME(),
         @prevDef, @currentDef,
         @changeType,
         'DDLTrigger',  -- 🔒 新增：标记来源为 DDL Trigger
         @login, @@SPID, HOST_NAME(), APP_NAME());
END;
```

### 2.5 接口定义

> 🔒 [评审修复 S2+S6+S7+S10] 补充 `RecordChangeAsync` 方法（含去重逻辑）；所有 async 方法新增 `CancellationToken` 参数；`EnsureTrackingInfrastructureAsync` 返回 `EnsureResult` 而非 void/抛出异常；新增 `CleanupHistoryAsync` 清理方法。

```csharp
// IVersionHistoryService.cs
namespace SSMSBoostPlus.VersionControl;

public interface IVersionHistoryService
{
    /// <summary>确保版本历史表和 DDL Trigger 存在（自动建表+创建 Trigger）</summary>
    /// <returns>成功返回 EnsureResult.Success；权限不足返回 EnsureResult.Failed(reason)</returns>
    Task<EnsureResult> EnsureTrackingInfrastructureAsync(
        string connectionString, CancellationToken ct = default);

    /// <summary>检查是否已启用版本跟踪（表和 Trigger 均存在）</summary>
    Task<bool> IsTrackingEnabledAsync(
        string connectionString, CancellationToken ct = default);

    /// <summary>获取对象的版本历史列表</summary>
    Task<List<VersionHistoryRecord>> GetHistoryAsync(
        string connectionString, string objectSchema, string objectName,
        string databaseName, int top = 20, CancellationToken ct = default);

    /// <summary>获取某个历史版本的完整定义</summary>
    Task<string?> GetVersionDefinitionAsync(
        string connectionString, long historyId, CancellationToken ct = default);

    /// <summary>生成回滚到指定版本的 SQL 脚本</summary>
    Task<RollbackScriptResult> GenerateRollbackScriptAsync(
        string connectionString, long historyId, CancellationToken ct = default);

    /// <summary>获取最近的全局变更记录</summary>
    Task<List<VersionHistoryRecord>> GetRecentChangesAsync(
        string connectionString, int top = 50,
        string? databaseFilter = null, CancellationToken ct = default);

    /// <summary>🔒 [新增] 记录通过 SSMSBoost++ 执行的变更（含去重逻辑）
    /// 先查询同一对象 + 同一 ChangeType + 时间窗口内的 DDLTrigger 记录，
    /// 命中则 UPDATE Source='SSMSBoost++' 并补充 Remark；未命中则 INSERT 新记录。</summary>
    Task RecordChangeAsync(
        string connectionString, VersionHistoryRecord record,
        CancellationToken ct = default);

    /// <summary>🔒 [新增] 清理过期历史数据（按 RetentionDays + MaxHistoryRows 策略）</summary>
    Task<int> CleanupHistoryAsync(
        string connectionString, CancellationToken ct = default);
}
```

```csharp
// EnsureResult — 基础设施创建结果
namespace SSMSBoostPlus.VersionControl;

public class EnsureResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public static EnsureResult Ok() => new() { Success = true };
    public static EnsureResult Failed(string message) => new() { Success = false, ErrorMessage = message };
}
```

### 2.6 数据模型

> 🔒 [评审修复 S1+S3] `NewDefinition` 改为 `string?`（DROP 事件时为 NULL）；新增 `Source` 字段；`RollbackScriptResult` 新增 `RollbackType` 枚举区分三种回滚场景。

```csharp
// VersionHistoryModels.cs
namespace SSMSBoostPlus.VersionControl.Models;

public class VersionHistoryRecord
{
    public long HistoryId { get; set; }
    public string ObjectSchema { get; set; } = "dbo";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string? PreviousDefinition { get; set; }
    public string? NewDefinition { get; set; } = "";  // 🔒 改为可空：DROP 事件时为 null
    public string ChangeType { get; set; } = "";      // CREATE/ALTER/DROP
    public string Source { get; set; } = "DDLTrigger"; // 🔒 新增：DDLTrigger / SSMSBoost++
    public string ModifiedBy { get; set; } = "";
    public DateTime ModifiedAt { get; set; }
    public int? SessionId { get; set; }
    public string? HostName { get; set; }
    public string? ApplicationName { get; set; }
    public string? GitCommitSha { get; set; }
    public string? Remark { get; set; }
}

public class RollbackScriptResult
{
    public bool CanRollback { get; set; }
    public RollbackType Type { get; set; }  // 🔒 新增：回滚类型枚举
    public string? RollbackScript { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>🔒 [新增] 回滚脚本类型</summary>
public enum RollbackType
{
    /// <summary>ALTER 回滚：将 PreviousDefinition 中的 CREATE 替换为 ALTER</summary>
    AlterToPrevious,

    /// <summary>DROP 已创建的对象（PreviousDefinition 为 null，表示首次创建的回滚）</summary>
    DropCreated,

    /// <summary>用 PreviousDefinition 重建已删除的对象（DROP 事件的回滚）</summary>
    RecreateDropped
}
```

### 2.7 服务实现

> 🔒 [评审修复 S4+S5+S6+S7+S10] 回滚脚本修正三种场景；DDL Trigger SQL 动态拼接表名/触发器名；所有 async 方法加 `ConfigureAwait(false)` 和异常处理；新增 `CleanupHistoryAsync` 清理机制；`EnsureTrackingInfrastructureAsync` 捕获权限异常返回 `EnsureResult`。

```csharp
// VersionHistoryService.cs 核心逻辑
public class VersionHistoryService : IVersionHistoryService
{
    private readonly ILoggerService _logger;
    private readonly VersionHistoryConfig _config;

    public VersionHistoryService(ILoggerService logger, VersionHistoryConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<EnsureResult> EnsureTrackingInfrastructureAsync(
        string connectionString, CancellationToken ct = default)
    {
        try
        {
            // 🔒 使用配置值动态拼接 SQL（修复 S5：表名/触发器名不再硬编码）
            // 1. 检查表是否存在：IF OBJECT_ID('[dbo].[{TableName}]') IS NULL → CREATE TABLE
            //    （CREATE TABLE SQL 中使用 _config.TableName 替换占位符）
            // 2. 检查 Trigger 是否存在：IF OBJECT_ID('[{TriggerName}]') IS NULL → CREATE TRIGGER
            //    （CREATE TRIGGER SQL 中使用 _config.TableName 和 _config.TriggerName 替换占位符）
            // 3. 🔒 检查并清理过期数据：await CleanupHistoryAsync(connectionString, ct)
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            // ... 建表/建 Trigger 逻辑 ...
            return EnsureResult.Ok();
        }
        catch (SqlException ex) when (ex.Number == 1088 || ex.Number == 229)
        {
            // 🔒 权限不足（CREATE TABLE / CREATE TRIGGER 需要 ALTER 权限）
            _logger.Warning($"[VersionHistory] 权限不足，无法创建版本跟踪基础设施: {ex.Message}");
            return EnsureResult.Failed($"权限不足，请联系 DBA 授予 ALTER 权限以启用版本跟踪。错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VersionHistory] EnsureTrackingInfrastructureAsync 失败");
            return EnsureResult.Failed($"创建版本跟踪基础设施失败: {ex.Message}");
        }
    }

    public async Task<RollbackScriptResult> GenerateRollbackScriptAsync(
        string connectionString, long historyId, CancellationToken ct = default)
    {
        try
        {
            // 1. SELECT PreviousDefinition, NewDefinition, ObjectSchema, ObjectName, ObjectType, ChangeType
            //     FROM {TableName} WHERE HistoryId = @historyId
            // 2. 🔒 修复 S4：回滚脚本生成逻辑（三种场景）
            //
            //   场景 A — ChangeType=ALTER, PreviousDefinition 不为 null:
            //     → 将 PreviousDefinition 中的首个 CREATE 替换为 ALTER（正则: ^\s*CREATE → ALTER）
            //     → RollbackType.AlterToPrevious
            //     → 示例: "CREATE PROCEDURE dbo.usp_GetData AS ..." → "ALTER PROCEDURE dbo.usp_GetData AS ..."
            //
            //   场景 B — ChangeType=CREATE, PreviousDefinition 为 null:
            //     → 生成 DROP 脚本: "DROP {type} IF EXISTS {schema}.{name}"
            //     → RollbackType.DropCreated
            //
            //   场景 C — ChangeType=DROP, PreviousDefinition 不为 null:
            //     → 直接使用 PreviousDefinition（已是完整的 CREATE 语句）
            //     → RollbackType.RecreateDropped
            //     → 示例: "CREATE PROCEDURE dbo.usp_GetData AS ..."
            //
            // 3. 使用 DiffCalculator 生成差异文本供 UI 展示
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VersionHistory] GenerateRollbackScriptAsync 失败");
            return new RollbackScriptResult { CanRollback = false, ErrorMessage = ex.Message };
        }
    }

    public async Task RecordChangeAsync(
        string connectionString, VersionHistoryRecord record, CancellationToken ct = default)
    {
        // 🔒 去重机制（修复 S3）：
        // 1. 查询同一对象 + 同一 ChangeType + 最近 {DeduplicationWindowSeconds} 秒内 Source='DDLTrigger' 的记录
        //    SELECT TOP 1 HistoryId FROM {TableName}
        //    WHERE ObjectSchema=@schema AND ObjectName=@name AND ChangeType=@changeType
        //      AND Source='DDLTrigger' AND ModifiedAt >= DATEADD(SECOND, -@window, SYSUTCDATETIME())
        //    ORDER BY HistoryId DESC
        // 2. 若找到：UPDATE 该记录 SET Source='SSMSBoost++', Remark=@remark WHERE HistoryId=@id
        //    （DDL Trigger 已记录了完整信息，仅标记来源为 SSMSBoost++）
        // 3. 若未找到（Trigger 未启用或非 DDL 操作）：INSERT 新记录 Source='SSMSBoost++'
    }

    public async Task<int> CleanupHistoryAsync(
        string connectionString, CancellationToken ct = default)
    {
        // 🔒 清理策略（修复 S7）：
        // 1. 按 RetentionDays 删除：DELETE FROM {TableName} WHERE ModifiedAt < DATEADD(DAY, -@retentionDays, SYSUTCDATETIME())
        // 2. 若仍超 MaxHistoryRows：DELETE FROM {TableName} WHERE HistoryId NOT IN
        //    (SELECT TOP @maxHistoryRows HistoryId FROM {TableName} ORDER BY HistoryId DESC)
        // 返回删除的行数
    }

    public async Task<List<VersionHistoryRecord>> GetHistoryAsync(
        string connectionString, string objectSchema, string objectName,
        string databaseName, int top = 20, CancellationToken ct = default)
    {
        // SELECT TOP @top * FROM {TableName}
        // WHERE ObjectSchema=@schema AND ObjectName=@name AND DatabaseName=@db
        // ORDER BY HistoryId DESC
    }

    public async Task<List<VersionHistoryRecord>> GetRecentChangesAsync(
        string connectionString, int top = 50,
        string? databaseFilter = null, CancellationToken ct = default)
    {
        // SELECT TOP @top * FROM {TableName}
        // [WHERE DatabaseName=@filter]
        // ORDER BY ModifiedAt DESC
    }
}
```

### 2.8 版本历史 UI

**VersionHistoryDialog.xaml** 布局:
```
┌──────────────────────────────────────────────────────────────┐
│  版本历史 — 数据库: [MyDB]                                    │
├──────────────────────────────────────────────────────────────┤
│  筛选: [对象名] [日期范围] [对象类型▾] [搜索]                 │
├──────────────────────────────────────────────────────────────┤
│  # | 时间                | 对象              | 类型   | 操作人    | 来源          │
│  ──+─────────────────────+───────────────────+────────+──────────+──────────────│
│  1 | 2026-06-30 14:23:10 | dbo.usp_GetData   | ALTER  | zhangsan | SSMSBoost++  │
│  2 | 2026-06-29 09:15:05 | dbo.usp_GetData   | ALTER  | lisi     | DDLTrigger   │
│  3 | 2026-06-28 16:00:00 | dbo.usp_GetData   | CREATE | zhangsan | SSMSBoost++  │
├──────────────────────────────────────────────────────────────┤
│  选中: dbo.usp_GetData (ALTER, zhangsan, 2026-06-30)         │
│  ┌─ 差异对比 ─────────────────────────────────────────────┐  │
│  │  左: 前一个版本 (HistoryId=2)                            │  │
│  │  右: 后一个版本 (HistoryId=1, 当前)                      │  │
│  │  - old line                                              │  │
│  │  + new line                                              │  │
│  └─────────────────────────────────────────────────────────┘  │
│  [回滚到此版本] [复制脚本到剪贴板] [在编辑器中打开] [关闭]      │
└──────────────────────────────────────────────────────────────┘
```

**关键设计**:
- 非模态窗口（`Show()`），可与其他 SSMS 窗口并行使用
- 筛选栏：对象名（支持模糊匹配）、日期范围（DateTimePicker）、对象类型（下拉多选）
- 列表按时间倒序展示，支持单选
- 🔒 新增"来源"列，区分 DDLTrigger 和 SSMSBoost++ 记录
- 选中某行后下方展示差异对比（复用 `DiffCalculator`）
- "回滚到此版本" → 调用 `GenerateRollbackScriptAsync` → 在确认对话框中展示生成的回滚脚本 → 用户确认后执行
- "在编辑器中打开" → 将选中版本的定义加载到 SSMS 编辑器中

### 2.9 与 VersionGuard 集成

> 🔒 [评审修复 S3+S6] 移除硬编码 `new VersionHistoryService(_logger)` 实例化，改为通过构造函数注入；`RecordChangeAsync` 延迟 3 秒调用避免与 DDL Trigger 事务冲突；去重逻辑由 `RecordChangeAsync` 内部处理。

在 `ExecutionInterceptor.CheckAndExecuteAsync()` 中，SQL 成功执行后（`ReExecuteCommandSync()` 之后）异步记录版本历史：

```csharp
// ExecutionInterceptor 构造函数新增 IVersionHistoryService 参数（依赖注入）
private readonly IVersionHistoryService? _historyService;
private readonly IConfigurationService _configService;

// 在 CheckAndExecuteAsync() 中，ReExecuteCommandSync() 之后：
// 🔒 延迟 3 秒调用 RecordChangeAsync，等待 DDL Trigger 事务提交后再去重
if (historyConfig.Enabled && historyConfig.AutoRecordOnExecute)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(3000).ConfigureAwait(false);  // 等待 DDL Trigger 完成
        try
        {
            var objectExtractor = new ObjectExtractor();
            var objectRefs = objectExtractor.ExtractObjects(code);
            foreach (var objRef in objectRefs.Where(r => r.IsDefinition))
            {
                var record = new VersionHistoryRecord
                {
                    ObjectSchema = objRef.Schema,
                    ObjectName = objRef.Name,
                    ObjectType = objRef.Type,
                    DatabaseName = database ?? "",
                    ChangeType = "ALTER",  // 通过解析可细分 CREATE/ALTER
                    NewDefinition = code,
                    ModifiedBy = Environment.UserName,
                    Source = "SSMSBoost++",  // 🔒 标记来源
                };
                // RecordChangeAsync 内部执行去重逻辑：
                // 先查询时间窗口内 DDLTrigger 记录 → 命中则 UPDATE Source；未命中则 INSERT
                await _historyService!.RecordChangeAsync(
                    connectionString!, record).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ExecInterceptor] 版本历史记录失败（不影响执行结果）");
        }
    });
}
```

**去重机制说明**：

```
用户按 F5 执行 ALTER PROCEDURE
    ↓
ExecutionInterceptor 拦截 → 版本检查通过 → ReExecuteCommandSync()
    ↓                                     ↓
    |                                     DDL Trigger 同步触发 → INSERT Source='DDLTrigger'
    |                                     （同一事务内，先于延迟 3 秒的 RecordChangeAsync）
    ↓
延迟 3 秒后 RecordChangeAsync 执行
    ↓
查询同一对象 + 同一 ChangeType + 最近 60 秒内 Source='DDLTrigger' 的记录
    ↓
找到 → UPDATE Source='SSMSBoost++', Remark='通过 SSMSBoost++ 执行'（去重成功，不产生重复记录）
未找到 → INSERT Source='SSMSBoost++'（Trigger 未启用或非 DDL 操作的补充记录）
```

### 2.10 新增命令

| 命令 | VSCT ID | 说明 |
|------|---------|------|
| `VersionHistoryCommand` | `0x0410` | 打开版本历史对话框，显示当前库的变更记录 |
| `VersionHistoryConfigCommand` | `0x0420` | 配置版本跟踪（启用/禁用、创建 DDL Trigger、查看跟踪状态）|

### 2.11 VSCT 修改

> 🔒 [评审修复 S9] 移除未使用的 `Group_VersionHistory` 定义（按钮放在现有 `Group_VersionGuard` 中）；补充 `CommandPlacement` 到右键上下文菜单分组 `Group_ContextVersionGuard_Items`。

在 `guidSSMSBoostPlusCmdSet` 中新增:
```xml
<!-- 🔒 移除 Group_VersionHistory 定义，复用现有 Group_VersionGuard -->
<IDSymbol name="cmdidVersionHistory"         value="0x0410"/>
<IDSymbol name="cmdidVersionHistoryConfig"   value="0x0420"/>
```

新增按钮（放在 VersionGuard 分组中）:
```xml
<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidVersionHistory" priority="0x0140" type="Button">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_VersionGuard"/>
  <CommandFlag>DynamicVisibility</CommandFlag>
  <Strings>
    <ButtonText>📋 版本历史...</ButtonText>
    <CommandName>VersionHistory</CommandName>
  </Strings>
</Button>

<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidVersionHistoryConfig" priority="0x0150" type="Button">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_Config"/>
  <Strings>
    <ButtonText>⚙ 版本历史设置...</ButtonText>
    <CommandName>VersionHistorySettings</CommandName>
  </Strings>
</Button>
```

🔒 新增 CommandPlacement（放入右键上下文菜单的版本守护子菜单）:
```xml
<!-- 版本历史命令也放入右键上下文菜单 -->
<CommandPlacement guid="guidSSMSBoostPlusCmdSet" id="cmdidVersionHistory" priority="0x0140">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_ContextVersionGuard_Items"/>
</CommandPlacement>
```

### 2.12 Package 注册

在 `SSMSBoostPlusPackage.InitializeAsync()` 中:
```csharp
await Commands.VersionHistoryCommand.InitializeAsync(this, logger, configService, databaseService);
await Commands.VersionHistoryConfigCommand.InitializeAsync(this, logger, configService);
```

### 2.13 配置项

> 🔒 [评审修复 S8] 不再往 `VersionGuardConfiguration` 中添加属性，拆分为独立的 `VersionHistoryConfig` 类。同时在 `IConfigurationService` 和 `ConfigurationService` 中新增对应的 Get/Save 方法。

```csharp
// Configurations.cs 中新增独立配置类（VersionGuardConfiguration 保持不变）
namespace SSMSBoostPlus.Models;

public class VersionHistoryConfig
{
    /// <summary>是否启用版本历史跟踪（默认关闭，用户按需开启）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>版本历史表名</summary>
    public string TableName { get; set; } = "__SSMSBoost_VersionHistory";

    /// <summary>DDL Trigger 名称</summary>
    public string TriggerName { get; set; } = "trg_SSMSBoost_TrackDDL";

    /// <summary>执行 SQL 后是否自动记录变更</summary>
    public bool AutoRecordOnExecute { get; set; } = true;

    /// <summary>历史记录最大保留行数（超过时自动清理最旧记录）</summary>
    public int MaxHistoryRows { get; set; } = 50000;

    /// <summary>历史记录保留天数</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>🔒 去重时间窗口（秒），RecordChangeAsync 在此窗口内查找 DDLTrigger 记录</summary>
    public int DeduplicationWindowSeconds { get; set; } = 60;
}
```

```csharp
// IConfigurationService.cs 新增方法签名
Task<VersionHistoryConfig> GetVersionHistoryConfigAsync();
Task SaveVersionHistoryConfigAsync(VersionHistoryConfig config);
```

```csharp
// ConfigurationService.cs 新增实现
// 新增字段：_versionHistoryConfigPath、_cachedVersionHistoryConfig
// InvalidateCache() 中清除 _cachedVersionHistoryConfig 缓存
// 配置文件路径：{程序集目录}\version_history_config.json
```

### 2.14 测试计划

> 🔒 [评审修复] 补充去重逻辑测试、回滚三种场景测试、清理机制测试、权限异常测试。

| 测试用例 | 说明 |
|---------|------|
| `EnsureTrackingInfrastructure_CreatesTable` | 表不存在时自动创建 |
| `EnsureTrackingInfrastructure_TableExists_NoOp` | 表已存在时不做操作 |
| `EnsureTrackingInfrastructure_CreatesTrigger` | Trigger 不存在时创建 |
| `EnsureTrackingInfrastructure_PermissionDenied_ReturnsFailed` | 🔒 权限不足返回 EnsureResult.Failed |
| `EnsureTrackingInfrastructure_RunsCleanup` | 🔒 创建后自动清理过期数据 |
| `IsTrackingEnabled_True_WhenBothExist` | 表和 Trigger 都存在返回 true |
| `IsTrackingEnabled_False_WhenMissing` | 任一缺失返回 false |
| `GetHistory_ReturnsOrderedRecords` | 按时间倒序返回 |
| `GetHistory_FiltersByObjectName` | 按对象名筛选 |
| `GetRecentChanges_ReturnsLatestFirst` | 获取全局最近的变更 |
| `GenerateRollbackScript_Alter_ReturnsAlterScript` | 🔒 ALTER 场景：将 CREATE 替换为 ALTER |
| `GenerateRollbackScript_Create_ReturnsDropScript` | 🔒 CREATE 场景：生成 DROP IF EXISTS |
| `GenerateRollbackScript_Drop_ReturnsRecreateScript` | 🔒 DROP 场景：用 PreviousDefinition 重建 |
| `GenerateRollbackScript_Alter_HandlesCreateKeyword` | 🔒 验证正则替换正确（不误替换注释中的 CREATE） |
| `GenerateRollbackScript_NotExists_ReturnsError` | 不存在的 HistoryId 返回错误 |
| `RecordChangeAsync_Dedup_UpdatesExistingDDLTriggerRecord` | 🔒 时间窗口内找到 DDLTrigger 记录 → UPDATE Source |
| `RecordChangeAsync_NoDup_InsertsNewRecord` | 🔒 时间窗口内未找到 → INSERT 新记录 |
| `CleanupHistory_DeletesByRetentionDays` | 🔒 按 RetentionDays 删除过期记录 |
| `CleanupHistory_DeletesByMaxRows` | 🔒 超过 MaxHistoryRows 时删除最旧记录 |

### 2.15 文件清单

> 🔒 [评审修复] 补充 `IConfigurationService` 和 `ConfigurationService` 的修改；新增 `VersionHistoryConfigDialog` 配置对话框。

| # | 文件路径 | 操作 |
|---|---------|------|
| 1 | `src/Core/VersionControl/IVersionHistoryService.cs` | **新建** |
| 2 | `src/Core/VersionControl/VersionHistoryService.cs` | **新建** |
| 3 | `src/Core/VersionControl/Models/VersionHistoryModels.cs` | **新建** |
| 4 | `src/SSMS/Commands/VersionHistoryCommand.cs` | **新建** |
| 5 | `src/SSMS/Commands/VersionHistoryConfigCommand.cs` | **新建** |
| 6 | `src/SSMS/UI/Dialogs/VersionHistoryDialog.xaml` | **新建** |
| 7 | `src/SSMS/UI/Dialogs/VersionHistoryDialog.xaml.cs` | **新建** |
| 8 | `src/SSMS/UI/Dialogs/VersionHistoryConfigDialog.xaml` | **新建** 🔒 配置对话框 |
| 9 | `src/SSMS/UI/Dialogs/VersionHistoryConfigDialog.xaml.cs` | **新建** 🔒 配置对话框代码后台 |
| 10 | `src/SSMS/SSMSBoostPlus.vsct` | 修改（新增历史命令 + CommandPlacement） |
| 11 | `src/SSMS/SSMSBoostPlusPackage.cs` | 修改（注册历史命令） |
| 12 | `src/Core/Models/Configurations.cs` | 修改（新增 VersionHistoryConfig 独立配置类） |
| 13 | `src/Core/Interfaces/IConfigurationService.cs` | 修改 🔒（新增 Get/Save VersionHistoryConfig 方法） |
| 14 | `src/Core/Services/ConfigurationService.cs` | 修改 🔒（实现 Get/Save VersionHistoryConfig） |
| 15 | `src/SSMS/Services/ExecutionInterceptor.cs` | 修改（执行后延迟调用 RecordChangeAsync） |
| 16 | `tests/SSMSBoostPlus.Tests/VersionHistoryServiceTests.cs` | **新建** |

---

## 三、Phase 2 — SQL 审核引擎

### 3.1 概述

在 Core 层新增基于 ScriptDom 的 SQL 审核规则引擎。用户在执行 SQL 前或手动触发审核时，自动解析 SQL 为 AST，逐条运行审核规则，将结果展示在 UI 中。审核结果可作为 Phase 3 审批流程的前置条件。

### 3.2 新增目录结构

```
src/Core/CodeReview/                         ← 新建
  ├── ICodeReviewService.cs
  ├── CodeReviewService.cs
  ├── Rules/
  │   ├── IReviewRule.cs
  │   ├── SelectStarRule.cs
  │   ├── NoWhereClauseRule.cs
  │   ├── DynamicSqlRule.cs
  │   ├── NamingConventionRule.cs
  │   └── LongProcedureRule.cs
  └── Models/
      ├── ReviewResult.cs
      └── ReviewRuleConfig.cs
```

### 3.3 审核规则接口

```csharp
// IReviewRule.cs
namespace SSMSBoostPlus.CodeReview.Rules;

public interface IReviewRule
{
    string RuleId { get; }
    string Name { get; }
    string Description { get; }
    ReviewSeverity Severity { get; }  // Error / Warning / Info
    bool IsEnabledByDefault { get; }

    /// <summary>对 SQL AST 执行审核，返回发现的问题列表</summary>
    List<ReviewIssue> Analyze(TSqlFragment fragment, string sqlScript);
}

public enum ReviewSeverity
{
    Error,    // 必须修复
    Warning,  // 建议修复
    Info      // 仅供参考
}

public class ReviewIssue
{
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public ReviewSeverity Severity { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string Message { get; set; } = "";
    public string? Suggestion { get; set; }  // 修复建议
}
```

### 3.4 内置规则实现

#### SelectStarRule — SELECT * 检测

```csharp
public class SelectStarRule : IReviewRule
{
    public string RuleId => "R001";
    public string Name => "SELECT * 检测";
    public string Description => "检测是否使用了 SELECT *，建议显式指定列名";
    public ReviewSeverity Severity => ReviewSeverity.Warning;
    public bool IsEnabledByDefault => true;

    public List<ReviewIssue> Analyze(TSqlFragment fragment, string sqlScript)
    {
        var issues = new List<ReviewIssue>();
        var visitor = new SelectStarVisitor();
        fragment.Accept(visitor);
        foreach (var line in visitor.StarLines)
        {
            issues.Add(new ReviewIssue
            {
                RuleId = RuleId, RuleName = Name, Severity = Severity,
                LineNumber = line,
                Message = "使用了 SELECT *，建议显式指定所需列名以避免不必要的数据传输和隐式依赖",
                Suggestion = "将 SELECT * 替换为 SELECT 列1, 列2, ..."
            });
        }
        return issues;
    }

    private class SelectStarVisitor : TSqlFragmentVisitor
    {
        public List<int> StarLines = new();
        public override void Visit(SelectStarExpression node)
        {
            StarLines.Add(node.StartLine);
        }
    }
}
```

#### NoWhereClauseRule — 无 WHERE 条件检测

```csharp
public class NoWhereClauseRule : IReviewRule
{
    public string RuleId => "R002";
    public string Name => "无 WHERE 条件";
    public string Description => "检测 UPDATE/DELETE 语句是否缺少 WHERE 子句";
    public ReviewSeverity Severity => ReviewSeverity.Error;
    public bool IsEnabledByDefault => true;

    public List<ReviewIssue> Analyze(TSqlFragment fragment, string sqlScript)
    {
        var issues = new List<ReviewIssue>();
        var visitor = new NoWhereVisitor();
        fragment.Accept(visitor);
        foreach (var line in visitor.StatementLines)
        {
            issues.Add(new ReviewIssue
            {
                RuleId = RuleId, RuleName = Name, Severity = Severity,
                LineNumber = line,
                Message = "UPDATE/DELETE 语句缺少 WHERE 子句，可能导致全表数据被修改或删除",
                Suggestion = "请添加 WHERE 条件限制操作范围；如确实需要操作全表数据，添加注释 --#suppress=R002 忽略此规则"
            });
        }
        return issues;
    }

    private class NoWhereVisitor : TSqlFragmentVisitor
    {
        public List<int> StatementLines = new();
        public override void Visit(UpdateStatement node)
        {
            if (node.WhereClause == null)
                StatementLines.Add(node.StartLine);
        }
        public override void Visit(DeleteStatement node)
        {
            if (node.WhereClause == null)
                StatementLines.Add(node.StartLine);
        }
    }
}
```

#### DynamicSqlRule — 动态 SQL 检测

```csharp
public class DynamicSqlRule : IReviewRule
{
    // RuleId: "R003"
    // 检测 sp_executesql 调用和 EXEC('...') 模式
    // 通过 ExecuteStatement 和 ExecuteFunctionStatement 节点检查
    // 建议：将动态 SQL 替换为参数化查询或存储过程
}
```

#### NamingConventionRule — 命名规范检测

```csharp
public class NamingConventionRule : IReviewRule
{
    // RuleId: "R004"
    // 检测存储过程是否以 usp_ 开头（非 sp_ 避免与系统过程冲突）
    // 检测函数是否以 fn_ 开头
    // 通过 CreateProcedureStatement / CreateFunctionStatement 的 ObjectName 检查
}
```

#### LongProcedureRule — 过长存储过程检测

```csharp
public class LongProcedureRule : IReviewRule
{
    // RuleId: "R005"
    // 检测 CREATE PROCEDURE 语句体的行数是否超过阈值（默认 50 行）
    // 通过 ScriptTokenStream 计算语句体结束行号
    // 阈值可在 ReviewRuleConfig 中配置
}
```

### 3.5 审核服务

```csharp
// ICodeReviewService.cs
namespace SSMSBoostPlus.CodeReview;

public interface ICodeReviewService
{
    /// <summary>执行 SQL 审核，返回审核结果</summary>
    Task<ReviewResult> ReviewAsync(string sqlScript, ReviewOptions? options = null);

    /// <summary>获取所有已注册的审核规则</summary>
    List<IReviewRule> GetRegisteredRules();

    /// <summary>启用/禁用指定规则</summary>
    void SetRuleEnabled(string ruleId, bool enabled);
}

public class ReviewOptions
{
    public List<string>? EnabledRuleIds { get; set; }  // null = 全部启用
    public Dictionary<string, object>? RuleConfig { get; set; }  // 规则特定配置
}
```

```csharp
// CodeReviewService.cs 核心逻辑
public class CodeReviewService : ICodeReviewService
{
    private readonly List<IReviewRule> _rules;
    private readonly HashSet<string> _disabledRules = new();

    public CodeReviewService()
    {
        _rules = new List<IReviewRule>
        {
            new SelectStarRule(),
            new NoWhereClauseRule(),
            new DynamicSqlRule(),
            new NamingConventionRule(),
            new LongProcedureRule(),
        };
    }

    public async Task<ReviewResult> ReviewAsync(string sqlScript, ReviewOptions? options = null)
    {
        var result = new ReviewResult();
        if (string.IsNullOrWhiteSpace(sqlScript))
        {
            result.IsEmpty = true;
            return result;
        }

        // 1. 使用 TSql150Parser 解析 SQL
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        IList<ParseError> errors;
        var fragment = parser.Parse(new StringReader(sqlScript), out errors);

        if (errors != null && errors.Count > 0)
        {
            result.ParseErrors = errors.Select(e => new ReviewIssue
            {
                RuleId = "PARSE", Severity = ReviewSeverity.Error,
                LineNumber = e.Line, ColumnNumber = e.Column,
                Message = $"SQL 解析错误: {e.Message}"
            }).ToList();
            result.HasParseError = true;
            return result;
        }

        // 2. 逐条规则审核
        foreach (var rule in _rules)
        {
            if (_disabledRules.Contains(rule.RuleId)) continue;
            if (options?.EnabledRuleIds != null && !options.EnabledRuleIds.Contains(rule.RuleId)) continue;

            var issues = rule.Analyze(fragment, sqlScript);
            result.Issues.AddRange(issues);
        }

        // 3. 汇总统计
        result.Errors = result.Issues.Count(i => i.Severity == ReviewSeverity.Error);
        result.Warnings = result.Issues.Count(i => i.Severity == ReviewSeverity.Warning);
        result.Infos = result.Issues.Count(i => i.Severity == ReviewSeverity.Info);
        result.Passed = result.Errors == 0;

        return result;
    }
}
```

### 3.6 审核结果模型

```csharp
// ReviewResult.cs
namespace SSMSBoostPlus.CodeReview.Models;

public class ReviewResult
{
    public bool IsEmpty { get; set; }
    public bool HasParseError { get; set; }
    public List<ReviewIssue> ParseErrors { get; set; } = new();
    public List<ReviewIssue> Issues { get; set; } = new();
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public int Infos { get; set; }
    public bool Passed { get; set; }  // Errors == 0

    public string Summary =>
        Passed
            ? $"审核通过（{Issues.Count} 条信息）"
            : $"审核未通过（{Errors} 个错误, {Warnings} 个警告, {Infos} 条信息）";
}
```

### 3.7 审核 UI

**CodeReviewDialog.xaml** 布局:
```
┌─────────────────────────────────────────────────────────────┐
│  SQL 代码审核结果                              ✕             │
├─────────────────────────────────────────────────────────────┤
│  ┌─ 概览 ────────────────────────────────────────────────┐  │
│  │  ✅ 审核未通过                                        │  │
│  │  ❌ 错误: 1    ⚠ 警告: 2    ℹ 提示: 1                │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ❌ 第 5 行  | 缺少 WHERE 条件                              │
│    规则: R002 · 必须修复                                    │
│    UPDATE dbo.Users SET Status = 0                          │
│    ↑ 建议添加 WHERE 条件                                     │
│                                                             │
│  ⚠  第 12 行 | 使用了 SELECT *                              │
│    规则: R001 · 建议修复                                    │
│    SELECT * FROM dbo.Orders                                 │
│    ↑ 建议显式指定列名                                        │
│                                                             │
│  ⚠  第 34 行 | 使用了动态 SQL                               │
│    规则: R003 · 建议修复                                    │
│    EXEC('SELECT * FROM ' + @TableName)                      │
│    ↑ 建议使用参数化查询                                      │
│                                                             │
│  ℹ  第 1 行  | 存储过程命名不规范                            │
│    规则: R004 · 仅供参考                                    │
│    CREATE PROCEDURE GetData                                 │
│    ↑ 建议以 usp_ 开头                                        │
│                                                             │
│  [复制到剪贴板] [在编辑器中定位] [关闭]                       │
└─────────────────────────────────────────────────────────────┘
```

**关键设计**:
- 模态窗口（`ShowDialog()`），因为审核结果需要用户确认后才能继续
- 概览区域使用颜色标识：绿色（通过）、红色（有错误）、黄色（仅警告）
- 问题列表按严重程度排序（Error > Warning > Info），每条包含行号、规则名、问题描述和建议
- 每条问题显示对应源代码行，方便用户定位
- "在编辑器中定位"按钮 → 跳转到 SSMS 编辑器的指定行
- 支持 `--#suppress=R001` 注释跳过特定规则（在扫描阶段预处理）

### 3.8 与 VersionGuard 集成

在 `ExecutionInterceptor.CheckAndExecuteAsync()` 中，版本检查前增加审核步骤：

```csharp
// 版本检查前先执行 SQL 审核
if (config.CodeReviewEnabled)
{
    var reviewService = new CodeReviewService();
    var reviewResult = await reviewService.ReviewAsync(code);

    if (!reviewResult.Passed && reviewResult.Errors > 0)
    {
        // 有 Error 级别问题 → 弹出审核结果对话框
        progressDialog.CloseSafely();
        var reviewDialog = new CodeReviewDialog(reviewResult, code);
        reviewDialog.ShowDialog();
        if (reviewDialog.UserChoice == ReviewUserChoice.Cancel)
        {
            _logger.Information("[ExecInterceptor] 用户因审核未通过取消执行");
            return;
        }
        // UserChoice == ContinueAnyway → 继续执行
    }
}

// 审核通过或用户选择继续 → 执行版本检查（现有逻辑）
```

### 3.9 新增命令

| 命令 | VSCT ID | 说明 |
|------|---------|------|
| `CodeReviewCommand` | `0x0500` | 手动触发 SQL 审核当前脚本 |
| `CodeReviewConfigCommand` | `0x0510` | 配置审核规则（启用/禁用、调整阈值）|

### 3.10 配置项

扩展 `VersionGuardConfiguration`:

```csharp
public class VersionGuardConfiguration
{
    // ... 现有 + Phase 1 新增 ...

    // Phase 2 新增
    public bool CodeReviewEnabled { get; set; } = true;       // 默认开启
    public bool BlockOnError { get; set; } = true;             // Error 级别阻止执行
    public List<string> DisabledRuleIds { get; set; } = new(); // 禁用的规则 ID
    public int LongProcedureThreshold { get; set; } = 50;      // 过长过程阈值
}
```

### 3.11 测试计划

| 测试用例 | 说明 |
|---------|------|
| `SelectStarRule_DetectsSelectStar` | SELECT * 被正确检测 |
| `SelectStarRule_IgnoresSpecificColumns` | SELECT col1, col2 不误报 |
| `NoWhereClauseRule_DetectsUpdateWithoutWhere` | UPDATE 无 WHERE 被检测 |
| `NoWhereClauseRule_DetectsDeleteWithoutWhere` | DELETE 无 WHERE 被检测 |
| `NoWhereClauseRule_AllowsUpdateWithWhere` | UPDATE 有 WHERE 不误报 |
| `DynamicSqlRule_DetectsSpExecuteSql` | sp_executesql 被检测 |
| `DynamicSqlRule_DetectsExecString` | EXEC('...') 被检测 |
| `NamingConventionRule_DetectsNonUspProc` | 存储过程不以 usp_ 开头被检测 |
| `NamingConventionRule_AllowsUspProc` | usp_ 开头的存储过程不误报 |
| `LongProcedureRule_DetectsLongProc` | 超过阈值的过程被检测 |
| `CodeReviewService_ReturnsSummary` | 审核结果汇总正确 |
| `CodeReviewService_ParseError_ReturnsError` | SQL 解析错误返回 PARSE 问题 |
| `CodeReviewService_EmptyScript_ReturnsIsEmpty` | 空脚本返回 IsEmpty |
| `SuppressComment_SkipRule` | `--#suppress=R001` 跳过对应规则 |

### 3.12 文件清单

| # | 文件路径 | 操作 |
|---|---------|------|
| 1 | `src/Core/CodeReview/ICodeReviewService.cs` | **新建** |
| 2 | `src/Core/CodeReview/CodeReviewService.cs` | **新建** |
| 3 | `src/Core/CodeReview/Rules/IReviewRule.cs` | **新建** |
| 4 | `src/Core/CodeReview/Rules/SelectStarRule.cs` | **新建** |
| 5 | `src/Core/CodeReview/Rules/NoWhereClauseRule.cs` | **新建** |
| 6 | `src/Core/CodeReview/Rules/DynamicSqlRule.cs` | **新建** |
| 7 | `src/Core/CodeReview/Rules/NamingConventionRule.cs` | **新建** |
| 8 | `src/Core/CodeReview/Rules/LongProcedureRule.cs` | **新建** |
| 9 | `src/Core/CodeReview/Models/ReviewResult.cs` | **新建** |
| 10 | `src/Core/CodeReview/Models/ReviewRuleConfig.cs` | **新建** |
| 11 | `src/SSMS/Commands/CodeReviewCommand.cs` | **新建** |
| 12 | `src/SSMS/Commands/CodeReviewConfigCommand.cs` | **新建** |
| 13 | `src/SSMS/UI/Dialogs/CodeReviewDialog.xaml` | **新建** |
| 14 | `src/SSMS/UI/Dialogs/CodeReviewDialog.xaml.cs` | **新建** |
| 15 | `src/SSMS/SSMSBoostPlus.vsct` | 修改（新增审核命令） |
| 16 | `src/SSMS/SSMSBoostPlusPackage.cs` | 修改（注册审核命令） |
| 17 | `src/Core/Models/Configurations.cs` | 修改（新增审核配置） |
| 18 | `src/SSMS/Services/ExecutionInterceptor.cs` | 修改（执行前插入审核步骤） |
| 19 | `tests/SSMSBoostPlus.Tests/CodeReviewServiceTests.cs` | **新建** |

---

## 四、Phase 3 — 代码审批流程

### 4.1 概述

新增审批模块，支持用户手动提交 SQL 变更审批请求，审批人在 SSMS 内查看变更详情并做出通过/拒绝决定。后续可扩展为强制审批（敏感操作自动触发）。

### 4.2 新增目录结构

```
src/Core/Approval/                           ← 新建
  ├── IApprovalService.cs
  ├── ApprovalService.cs
  └── Models/
      ├── ApprovalRequest.cs
      ├── ApprovalConfig.cs
      └── ApprovalNotification.cs
```

### 4.3 审批数据库表

```sql
CREATE TABLE [dbo].[__SSMSBoost_ApprovalRequests] (
    RequestId       UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    Requester       NVARCHAR(128)    NOT NULL,           -- 申请人
    Approver        NVARCHAR(128)    NOT NULL,           -- 审批人
    Status          NVARCHAR(20)     NOT NULL DEFAULT 'Pending',  -- Pending/Approved/Rejected/Cancelled
    Title           NVARCHAR(256)    NOT NULL,           -- 变更标题
    Description     NVARCHAR(MAX),                       -- 变更说明
    ChangeSql       NVARCHAR(MAX)    NOT NULL,           -- 变更 SQL
    ObjectSchema    NVARCHAR(128),                       -- 目标对象 Schema
    ObjectName      NVARCHAR(256),                       -- 目标对象名
    ObjectType      NVARCHAR(50),                        -- 对象类型
    DatabaseName    NVARCHAR(128)    NOT NULL,           -- 目标数据库
    ReviewResult    NVARCHAR(MAX),                       -- Phase 2 审核结果的 JSON
    Comments        NVARCHAR(MAX),                       -- 审批意见
    RequestedAt     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ReviewedAt      DATETIME2,
    GitCommitSha    NVARCHAR(40)                         -- 关联 Git 提交
);
CREATE INDEX IX_Approval_Status ON [dbo].[__SSMSBoost_ApprovalRequests](Status, Approver);
CREATE INDEX IX_Approval_Requester ON [dbo].[__SSMSBoost_ApprovalRequests](Requester);
```

### 4.4 审批服务接口

```csharp
// IApprovalService.cs
namespace SSMSBoostPlus.Approval;

public interface IApprovalService
{
    /// <summary>确保审批表存在</summary>
    Task EnsureTableExistsAsync(string connectionString);

    /// <summary>提交审批请求</summary>
    Task<ApprovalRequest> SubmitAsync(string connectionString, SubmitApprovalRequest request);

    /// <summary>审批通过</summary>
    Task<bool> ApproveAsync(string connectionString, Guid requestId, string? comments = null);

    /// <summary>审批拒绝</summary>
    Task<bool> RejectAsync(string connectionString, Guid requestId, string reason);

    /// <summary>获取待审批列表（按审批人筛选）</summary>
    Task<List<ApprovalRequest>> GetPendingAsync(string connectionString, string? approver = null);

    /// <summary>获取我的申请记录</summary>
    Task<List<ApprovalRequest>> GetMyRequestsAsync(string connectionString, string requester, int top = 20);

    /// <summary>获取审批详情</summary>
    Task<ApprovalRequest?> GetByIdAsync(string connectionString, Guid requestId);
}
```

### 4.5 数据模型

```csharp
public class ApprovalRequest
{
    public Guid RequestId { get; set; }
    public string Requester { get; set; } = "";
    public string Approver { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string ChangeSql { get; set; } = "";
    public string? ObjectSchema { get; set; }
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public string DatabaseName { get; set; } = "";
    public string? ReviewResultJson { get; set; }
    public string? Comments { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? GitCommitSha { get; set; }
}

public class SubmitApprovalRequest
{
    public string Approver { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string ChangeSql { get; set; } = "";
    public string? ObjectName { get; set; }
    public string? ObjectType { get; set; }
    public string DatabaseName { get; set; } = "";
    public string? ReviewResultJson { get; set; }
}
```

### 4.6 审批 UI

**SubmitApprovalDialog.xaml**:
```
┌──────────────────────────────────────────────┐
│  提交审批请求                              ✕   │
├──────────────────────────────────────────────┤
│  变更标题: [输入框]                            │
│  变更说明: [多行输入框]                        │
│  审批人:    [下拉选择]                         │
│  目标库:    [自动填充]                         │
│  目标对象:  [自动填充]                         │
│  ──────────────────────────────────────────   │
│  变更 SQL:                                    │
│  ┌──────────────────────────────────────────┐ │
│  │  ALTER PROCEDURE dbo.usp_GetData         │ │
│  │  AS                                      │ │
│  │  SELECT * FROM dbo.Orders  ← 审核结果  │ │
│  └──────────────────────────────────────────┘ │
│  ⚠ 审核发现 1 个警告                          │
│                                              │
│  [提交审批]  [取消]                          │
└──────────────────────────────────────────────┘
```

**ApprovalDialog.xaml**（审批人视角）:
```
┌──────────────────────────────────────────────┐
│  审批请求 — 来自 zhangsan                  ✕   │
├──────────────────────────────────────────────┤
│  标题: 修改 usp_GetData 查询逻辑              │
│  说明: 增加按日期筛选条件                      │
│  申请人: zhangsan    时间: 2026-06-30 14:23   │
│  目标: [MyDB].[dbo].[usp_GetData]             │
│  ──────────────────────────────────────────   │
│  ┌─ 变更 SQL ──────────────────────────────┐  │
│  │  ALTER PROCEDURE dbo.usp_GetData         │  │
│  │  @StartDate DATETIME2,                   │  │
│  │  @EndDate DATETIME2                      │  │
│  │  AS                                      │  │
│  │  SELECT * FROM dbo.Orders                │  │
│  │  WHERE OrderDate BETWEEN @StartDate       │  │
│  │    AND @EndDate                          │  │
│  └──────────────────────────────────────────┘  │
│  ⚠ 审核发现 1 个警告 (SELECT *)               │
│                                              │
│  审批意见:                                     │
│  ┌──────────────────────────────────────────┐ │
│  │  已确认 SELECT * 在此处是合理的             │ │
│  └──────────────────────────────────────────┘ │
│                                              │
│  [通过]  [拒绝]                              │
└──────────────────────────────────────────────┘
```

**ApprovalStatusToolWindow**（审批状态面板，ToolWindow 类型）:
```
┌─────────────────────────────────────────────────────┐
│  审批状态                         ↻ 刷新    ⚙ 设置   │
├─────────────────────────────────────────────────────┤
│  ┌─ 待我审批 (3) ─────────────────────────────────┐  │
│  │  📋 修改 usp_GetData      zhangsan  14:23     │  │
│  │  📋 新增 fn_CalcRevenue   lisi     13:15     │  │
│  │  📋 修改 vw_OrderSummary  wangwu   11:00     │  │
│  └───────────────────────────────────────────────┘  │
│  ┌─ 我的申请 (5) ─────────────────────────────────┐  │
│  │  ✅ 修改 proc1    已通过  wangwu  2026-06-29   │  │
│  │  ❌ 修改 proc2    已拒绝  lisi   2026-06-28   │  │
│  │  ⏳ 修改 proc3    待审批  wangwu  2026-06-28   │  │
│  └───────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

### 4.7 新增命令

| 命令 | VSCT ID | 说明 |
|------|---------|------|
| `SubmitApprovalCommand` | `0x0600` | 提交当前 SQL 变更审批 |
| `ApprovalStatusCommand` | `0x0610` | 打开审批状态面板 |

---

## 五、Phase 4 — Git 提交

### 5.1 概述

在 SSMS 右键菜单中增加"提交到 Git"按钮，用户无需离开 SSMS 即可将当前 SQL 脚本提交到 Git 仓库。

### 5.2 新增目录

```
src/Core/VersionControl/
  ├── IGitService.cs            ← 新建
  ├── GitService.cs             ← 新建
  └── Models/
      └── GitModels.cs          ← 新建
```

### 5.3 接口

```csharp
public interface IGitService
{
    bool IsGitRepository(string filePath);
    string? GetRepositoryRoot(string filePath);
    string? GetCurrentBranch(string repositoryPath);
    GitCommitResult Commit(string repositoryPath, string fileRelativePath, string message);
    GitFileStatus GetFileStatus(string repositoryPath, string fileRelativePath);
    GitCommitInfo? GetLastCommit(string repositoryPath, string fileRelativePath);
}
```

### 5.4 依赖

```xml
<PackageReference Include="LibGit2Sharp" Version="0.30.0" />
```

---

## 六、关键设计决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 版本历史存储 | SQL Server 表 | 与应用数据库同源，DDL Trigger 零延迟捕获，无需额外基础设施 |
| 变更捕获方式 | DDL Trigger + ExecutionInterceptor 双保留 | DDL Trigger 全局覆盖（含 SSMS 外部），ExecutionInterceptor 标记 SSMSBoost++ 来源 |
| 🔒 去重机制 | Source 字段 + 时间窗口 | RecordChangeAsync 延迟 3 秒后查询 DDLTrigger 记录，命中则 UPDATE 不重复 INSERT |
| 回滚实现 | 生成 ALTER/DROP/CREATE 脚本 | 不自动执行，让用户确认后再运行，安全可控 |
| 🔒 回滚脚本生成 | 正则替换 CREATE→ALTER | PreviousDefinition 来自 sys.sql_modules，已含 CREATE 语句头，需替换为 ALTER |
| 差异对比 | 复用 DiffCalculator | 项目已引用 DiffPlex，已有 SQL 规范化逻辑 |
| 对话框模式 | 非模态（Show） | 不阻塞 SSMS，可与其他窗口并行使用 |
| 配置默认 | 版本历史默认关闭 | 用户需主动开启，避免未经同意就在数据库中建表 |
| 🔒 配置拆分 | 独立 VersionHistoryConfig 类 | 避免 VersionGuardConfiguration 膨胀，各功能配置独立管理 |
| 🔒 历史表清理 | RetentionDays + MaxHistoryRows 双策略 | 防止表无限增长，每次 EnsureTrackingInfrastructureAsync 时自动清理 |
| 🔒 DDL Trigger 动态表名 | Service 层拼接 SQL | 配置中表名/触发器名可自定义，SQL 不再硬编码 |
| 🔒 权限处理 | EnsureResult 返回友好错误 | 创建表/Trigger 需 ALTER 权限，权限不足时不抛异常而返回错误信息 |
| 审核触发 | 执行前自动 + 手动 | 拦截器中默认开启，用户也可手动触发 |
| 审核阻止级别 | Error 级别阻止 | Warning 和 Info 仅提示，不阻止执行 |
| 审批流程 | 先手动后强制 | 第一阶段手动提交审批，后续扩展到自动拦截 |
| Phase 2 依赖 | 零新增 NuGet | ScriptDom 和 DiffPlex 已在项目中引用 |

## 七、依赖项

| 依赖 | 用途 | 版本 | 状态 |
|------|------|------|------|
| Microsoft.Data.SqlClient | 数据库操作 | 5.2.0 | ✅ 已引用 |
| DiffPlex | 差异对比 | 1.7.2 | ✅ 已引用 |
| Microsoft.SqlServer.TransactSql.ScriptDom | SQL 解析（Phase 2 用） | 150.4573.2 | ✅ 已引用 |
| Newtonsoft.Json | 配置序列化 | 13.0.3 | ✅ 已引用 |
| LibGit2Sharp | Git 操作（Phase 4 用） | 0.30.0 | ❌ 需新增 |

**Phase 1~3 零新增 NuGet 依赖** — 所有能力已通过现有依赖满足。