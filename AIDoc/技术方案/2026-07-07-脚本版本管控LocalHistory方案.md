---
title: 脚本版本管控 Local History 方案（Phase 1 重构）
date: 2026-07-07
author: AI Assistant
tags: [版本管控, LocalHistory, DDLTrigger, Phase1重构]
---

# 脚本版本管控 Local History 方案（Phase 1 重构）

## 一、背景与思路转变

### 1.1 原方案回顾

原 Phase 1 方案（2026-06-30）计划从零搭建完整的版本捕获基础设施：

- 在目标数据库创建 `__SSMSBoost_VersionHistory` 表
- 创建 DDL Trigger 自动捕获变更
- ExecutionInterceptor 双保留 + 去重机制
- 涉及 16 个文件，架构复杂

### 1.2 现状

DDL Trigger 捕获机制**已在生产环境运行**，数据持续写入 `MESBASE.dbo.DBTrigger_Object_Version_Table` 表。无需重新搭建捕获层。

### 1.3 新思路：Local History

转变核心：从"造基础设施"变为"消费已有数据"。

借鉴 IDE Local History（IntelliJ / VS Code）的用户体验，基于已有的 DDL Trigger 数据，构建版本浏览、差异对比、一键恢复的能力。用户无需任何额外操作，打开 Local History 面板即可看到所有数据库对象的完整变更历史。

### 1.4 新旧方案对比

| 维度 | 原方案 | 新方案 |
|------|--------|--------|
| DDL Trigger | 需要创建 | 已存在，直接使用 |
| 版本表 | 需要创建 `__SSMSBoost_VersionHistory` | 已存在 `DBTrigger_Object_Version_Table` |
| 捕获机制 | DDL Trigger + ExecutionInterceptor 双保留 | DDL Trigger 单源（已运行） |
| 去重 | Source 字段 + 时间窗口去重 | 不需要（单源无重复） |
| 基础设施搭建 | `EnsureTrackingInfrastructureAsync` | 不需要 |
| 数据模型 | PreviousDefinition + NewDefinition 双字段 | 单 `sqlcommand` 字段（完整 DDL 语句） |
| 涉及文件数 | 16 个 | 8 个 |
| 数据库侵入 | 需在目标库建表+建触发器 | 零侵入（只读查询 MESBASE） |

---

## 二、已有数据源

### 2.1 表结构

数据库：`MESBASE`
表名：`dbo.DBTrigger_Object_Version_Table`

```sql
CREATE TABLE [dbo].[DBTrigger_Object_Version_Table]
(
    [ID] [bigint] NOT NULL IDENTITY(1, 1),
    [databasename] [varchar](256) COLLATE Chinese_PRC_CI_AS NULL,      -- 数据库名
    [eventtype] [varchar](50) COLLATE Chinese_PRC_CI_AS NULL,           -- 事件类型 (CREATE_PROCEDURE / ALTER_PROCEDURE / DROP_PROCEDURE 等)
    [objectname] [varchar](500) COLLATE Chinese_PRC_CI_AS NOT NULL,     -- 对象名（含 Schema，如 dbo.usp_GetData）
    [objecttype] [varchar](25) COLLATE Chinese_PRC_CI_AS NULL,          -- 对象类型 (PROCEDURE / FUNCTION / VIEW / TRIGGER / SQL_TRIGGER)
    [sqlcommand] [nvarchar](max) COLLATE Chinese_PRC_CI_AS NULL,        -- 完整 DDL 语句
    [loginname] [varchar](256) COLLATE Chinese_PRC_CI_AS NULL,          -- 操作人
    [hostname] [varchar](256) COLLATE Chinese_PRC_CI_AS NULL,           -- 主机名
    [PostTime] [datetime] NOT NULL,                                     -- 操作时间
    [Version] [int] NOT NULL                                            -- 版本号（自增）
)
```

已有索引：
- `PK_DBTrigger_Object_Version_Table` — 聚集主键 `[ID]`
- `IX_DBTrigger_Object_Version_Table_01` — 非聚集索引 `[objectname]`

### 2.2 数据特征分析

`sqlcommand` 字段存储的是完整的 DDL 语句，例如：

- CREATE 事件：`CREATE PROCEDURE dbo.usp_GetData AS ...`
- ALTER 事件：`ALTER PROCEDURE dbo.usp_GetData AS ...`
- DROP 事件：`DROP PROCEDURE dbo.usp_GetData`

这意味着：
- 版本对比可以直接用相邻两条记录的 `sqlcommand` 做 Diff
- 恢复某版本时，需要将 `sqlcommand` 中的 `CREATE` 转为 `ALTER`（因为对象已存在）
- DROP 事件的 `sqlcommand` 为 `DROP ...`，恢复时需要取上一条非 DROP 记录的定义

### 2.3 补充索引建议

现有索引仅覆盖 `objectname` 单列查询。为支持 Local History 常用查询模式，建议补充：

```sql
-- 按对象名 + 时间倒序查询（Local History 时间线加载）
CREATE NONCLUSTERED INDEX IX_DBTrigger_Object_Version_Table_02
    ON [dbo].[DBTrigger_Object_Version_Table] ([objectname], [PostTime] DESC)
    WITH (FILLFACTOR = 80) ON [PRIMARY];

-- 按时间倒序查询最近变更（全局 Recent Changes）
CREATE NONCLUSTERED INDEX IX_DBTrigger_Object_Version_Table_03
    ON [dbo].[DBTrigger_Object_Version_Table] ([PostTime] DESC)
    INCLUDE ([objectname], [eventtype], [objecttype], [databasename], [loginname])
    WITH (FILLFACTOR = 80) ON [PRIMARY];
```

此操作为 DBA 在 MESBASE 上手动执行，SSMSBoostPlus 不自动创建。

---

## 三、架构设计

### 3.1 整体架构

```
┌─ SSMS Layer ──────────────────────────────────────────────────┐
│                                                               │
│  LocalHistoryCommand          VersionGuardWarningDialog       │
│       │                              │                        │
│       ▼                              │ "查看版本历史"           │
│  LocalHistoryToolWindow ◄────────────┘                        │
│       │                                                       │
│       │ 查询 / Diff / 恢复                                     │
│       ▼                                                       │
├─ Core Layer ──────────────────────────────────────────────────┤
│                                                               │
│  IVersionHistoryService                                       │
│       │                                                       │
│       ▼                                                       │
│  VersionHistoryService                                        │
│       │                                                       │
│       │ SQL查询   │ Diff计算        │ 恢复脚本生成              │
│       ▼          ▼                ▼                           │
│  SqlConnection  DiffCalculator  ScriptDom/正则                 │
│                                                               │
│       │ ConnectionString（由 SSMS 层注入）                       │
│       ▼                                                       │
│  MESBASE.dbo.DBTrigger_Object_Version_Table（已有数据源）       │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### 3.2 依赖链

遵循项目架构约束，新增组件的依赖方向：

```
Shared (netstandard2.0)
    ↓
Core (netstandard2.0)
    ↓  ← 新增: IVersionHistoryService / VersionHistoryService / Models
SSMS (net472)
       ← 新增: LocalHistoryCommand / LocalHistoryToolWindow
```

### 3.3 连接策略

Local History 需要连接 MESBASE 数据库读取版本数据。采用**当前窗口自动推断**策略，无需用户手动配置连接字符串，分两条线并行工作：

#### 3.3.1 连接串推导线

复用当前 SSMS 窗口的数据库连接，将 `InitialCatalog` 替换为 MESBASE（可配置）：

1. `LocalHistoryCommand` 调用 `ConnectionServiceFacade.GetConnectionInfoAsync()` 获取当前窗口的 `ConnectionInfo`
2. 取 `ConnectionInfo.ConnectionString`，用 `SqlConnectionStringBuilder` 修改 `InitialCatalog = LocalHistoryConfig.DatabaseName`（默认 `"MESBASE"`）
3. 连接串获取失败 → 提示用户检查 SSMS 连接状态

#### 3.3.2 对象推断线

从当前窗口脚本内容解析 DDL 语句，自动推断要查看历史记录的数据库对象：

1. `LocalHistoryCommand` 调用 `SsmsEditorService.GetCodeAsync()` 获取当前脚本（选中文本优先，无选中返回全文）
2. 用 `ObjectExtractor.ExtractObjects(code)` 解析，过滤 `IsDefinition == true` 的对象
3. 取第一个对象作为 `initialObjectName` 传给 `LocalHistoryToolWindow`，自动加载其版本历史
4. 无 DDL 语句 → 传 `null`，ToolWindow 显示空状态提示"未检测到数据库对象定义语句"

#### 3.3.3 编排流程

`LocalHistoryCommand` 负责 SSMS 层编排，两条线可通过 `Task.WhenAll` 并行执行：

```csharp
// LocalHistoryCommand.ExecuteAsync 核心编排逻辑
var connTask = _connectionService.GetConnectionInfoAsync();
var scriptTask = _editorService.GetCodeAsync();
await Task.WhenAll(connTask, scriptTask);

// 1. 推导连接串
var connInfo = connTask.Result;
if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
{
    MessageBox.Show("未检测到活动的数据库连接，请确保已连接到数据库服务器。");
    return;
}
var builder = new SqlConnectionStringBuilder(connInfo.ConnectionString)
{
    InitialCatalog = _config.DatabaseName  // 默认 "MESBASE"
};
var mesbaseConnStr = builder.ConnectionString;

// 2. 推断初始对象
var code = scriptTask.Result.Code;
var objectExtractor = new ObjectExtractor();
var objects = objectExtractor.ExtractObjects(code)
    .Where(o => o.IsDefinition)
    .ToList();
string? initialObjectName = objects.Count > 0
    ? $"{objects[0].Schema}.{objects[0].Name}"
    : null;

// 3. 打开 ToolWindow，传入连接串和初始对象
var window = Package.FindToolWindow(
    typeof(UI.VersionHistory.LocalHistoryToolWindow), 0, true);
if (window is UI.VersionHistory.LocalHistoryToolWindow historyWindow)
{
    historyWindow.InitializeContent(mesbaseConnStr, initialObjectName);
    var frame = (IVsWindowFrame)window.Frame;
    ErrorHandler.ThrowOnFailure(frame.Show());
}
```

#### 3.3.4 设计要点

| 要点 | 说明 |
|------|------|
| 为何完全移除 ConnectionString 配置 | 当前窗口连接已包含服务器、认证方式等完整信息，只需替换数据库名即可，无需用户额外配置 |
| 为何用 ConnectionServiceFacade | 已封装多 Provider 回退策略（OfficialConnectionProvider → ReflectionConnectionProvider），与 VersionGuardCommand 保持一致 |
| DatabaseName 配置项保留 | MESBASE 是约定名称，但保留可配置性以应对不同环境的命名差异 |
| 编排位置在 SSMS 层 | LocalHistoryCommand 负责编排，Core 层的 VersionHistoryService 只接收已构建好的连接串，不直接依赖 SSMS 服务（符合架构约束） |

---

## 四、数据模型

### 4.1 版本记录模型

```csharp
// src/Core/VersionHistory/Models/VersionHistoryModels.cs
namespace SSMSBoostPlus.VersionHistory.Models;

/// <summary>
/// 对应 DBTrigger_Object_Version_Table 的一条记录
/// </summary>
public class VersionRecord
{
    public long Id { get; set; }
    public string DatabaseName { get; set; } = "";
    public string EventType { get; set; } = "";       // CREATE_PROCEDURE / ALTER_PROCEDURE / DROP_PROCEDURE 等
    public string ObjectName { get; set; } = "";      // dbo.usp_GetData
    public string ObjectType { get; set; } = "";      // PROCEDURE / FUNCTION / VIEW / TRIGGER
    public string SqlCommand { get; set; } = "";      // 完整 DDL 语句
    public string LoginName { get; set; } = "";       // 操作人
    public string HostName { get; set; } = "";        // 主机名
    public DateTime PostTime { get; set; }            // 操作时间
    public int Version { get; set; }                  // 版本号

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
        "CREATE" => "#27AE60",  // 绿色
        "ALTER" => "#F39C12",   // 橙色
        "DROP" => "#E74C3C",    // 红色
        _ => "#95A5A6"          // 灰色
    };
}

/// <summary>
/// 对象摘要（左侧对象列表用）
/// </summary>
public class ObjectSummary
{
    public string ObjectName { get; set; } = "";      // dbo.usp_GetData
    public string ObjectType { get; set; } = "";      // PROCEDURE
    public string DatabaseName { get; set; } = "";
    public int VersionCount { get; set; }             // 版本数量
    public DateTime LastModified { get; set; }        // 最后修改时间
    public string LastModifiedBy { get; set; } = "";  // 最后修改人
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
```

### 4.2 配置模型

```csharp
// 扩展 Configurations.cs
namespace SSMSBoostPlus.Models;

public class LocalHistoryConfig
{
    /// <summary>是否启用 Local History 功能</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>版本表名（默认 DBTrigger_Object_Version_Table）</summary>
    public string TableName { get; set; } = "DBTrigger_Object_Version_Table";

    /// <summary>版本表所在数据库名（默认 MESBASE）
    /// 连接串由当前窗口自动推导，此字段用于替换 InitialCatalog</summary>
    public string DatabaseName { get; set; } = "MESBASE";

    /// <summary>默认加载的版本数量上限</summary>
    public int MaxVersionsToLoad { get; set; } = 100;

    /// <summary>VersionGuard 检测到外部修改时是否自动弹出 Local History</summary>
    public bool AutoOpenOnExternalChange { get; set; } = false;
}
```

---

## 五、服务层

### 5.1 接口定义

```csharp
// src/Core/VersionHistory/IVersionHistoryService.cs
namespace SSMSBoostPlus.VersionHistory;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using SSMSBoostPlus.VersionHistory.Models;

public interface IVersionHistoryService
{
    /// <summary>
    /// 获取指定对象的版本历史列表（按时间倒序）
    /// </summary>
    /// <param name="connectionString">MESBASE 连接串（由 SSMS 层自动推断注入）</param>
    Task<List<VersionRecord>> GetObjectHistoryAsync(
        string connectionString,
        string objectName,
        string? databaseName = null,
        int top = 100,
        CancellationToken ct = default);

    /// <summary>
    /// 获取最近的全局变更记录（按时间倒序）
    /// </summary>
    Task<List<VersionRecord>> GetRecentChangesAsync(
        string connectionString,
        int top = 50,
        string? databaseFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// 获取所有有版本记录的对象列表（左侧导航用）
    /// </summary>
    Task<List<ObjectSummary>> GetObjectListAsync(
        string connectionString,
        string? databaseFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// 获取指定版本的完整 SQL
    /// </summary>
    Task<string?> GetVersionSqlAsync(
        string connectionString,
        long versionId,
        CancellationToken ct = default);

    /// <summary>
    /// 生成恢复到指定版本的脚本
    /// </summary>
    Task<RestoreScriptResult> GenerateRestoreScriptAsync(
        string connectionString,
        long versionId,
        CancellationToken ct = default);
}
```

### 5.2 服务实现要点

```csharp
// src/Core/VersionHistory/VersionHistoryService.cs
namespace SSMSBoostPlus.VersionHistory;

public class VersionHistoryService : IVersionHistoryService
{
    private readonly ILoggerService _logger;
    private readonly LocalHistoryConfig _config;

    // 构造函数注入（不含连接串，连接串由调用方按方法注入）
    public VersionHistoryService(ILoggerService logger, LocalHistoryConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<List<VersionRecord>> GetObjectHistoryAsync(
        string connectionString,
        string objectName, string? databaseName = null,
        int top = 100, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT TOP (@top) *
            FROM [{_config.DatabaseName}].[dbo].[{_config.TableName}]
            WHERE [objectname] = @objectName
            {(databaseName != null ? "AND [databasename] = @databaseName" : "")}
            ORDER BY [PostTime] DESC";

        // 使用 connectionString 执行查询（Microsoft.Data.SqlClient）
        // 映射 SqlDataReader → VersionRecord
    }

    public async Task<List<ObjectSummary>> GetObjectListAsync(
        string connectionString,
        string? databaseFilter = null, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT
                [objectname],
                [objecttype],
                [databasename],
                COUNT(*) AS VersionCount,
                MAX([PostTime]) AS LastModified,
                (SELECT TOP 1 [loginname]
                 FROM [{_config.DatabaseName}].[dbo].[{_config.TableName}] t2
                 WHERE t2.[objectname] = t1.[objectname]
                 ORDER BY [PostTime] DESC) AS LastModifiedBy
            FROM [{_config.DatabaseName}].[dbo].[{_config.TableName}] t1
            {(databaseFilter != null ? "WHERE [databasename] = @databaseFilter" : "")}
            GROUP BY [objectname], [objecttype], [databasename]
            ORDER BY LastModified DESC";

        // 使用 connectionString 执行查询
        // 映射 → ObjectSummary
    }

    public async Task<RestoreScriptResult> GenerateRestoreScriptAsync(
        string connectionString,
        long versionId, CancellationToken ct = default)
    {
        // 1. 查询该版本的完整记录（使用 connectionString）
        var record = await GetRecordByIdAsync(connectionString, versionId, ct);
        if (record == null)
            return new RestoreScriptResult { CanRestore = false, ErrorMessage = "版本记录不存在" };

        // 2. 根据 ChangeType 生成恢复脚本
        //    - CREATE 事件 → 恢复 = DROP IF EXISTS（撤销创建）
        //    - ALTER 事件 → 恢复 = 将 sqlcommand 中的 ALTER 改为 ALTER（本身就是 ALTER）
        //                    但更准确的做法：取上一条版本的定义，将 CREATE 转 ALTER
        //    - DROP 事件 → 恢复 = 取上一条非 DROP 版本的 sqlcommand（即 CREATE 语句）

        // 3. 对于 ALTER 事件的恢复，直接用该版本的 sqlcommand 即可（它就是 ALTER 语句）
        //    对于 CREATE 事件的恢复（撤销），生成 DROP 脚本
        //    对于 DROP 事件的恢复（撤销），取上一条 CREATE/ALTER 的 sqlcommand

        // 4. 使用正则将 CREATE 替换为 ALTER（如果需要）
        //    正则：^\s*CREATE\s+(PROCEDURE|FUNCTION|VIEW|TRIGGER)
        //    替换为：ALTER $1
    }
}
```

### 5.3 恢复脚本生成逻辑

```
场景 A：用户选中一条 ALTER 记录，想"恢复到此版本"
  → 该记录的 sqlcommand 已是 ALTER 语句，直接返回
  → RestoreType.AlterToHistory

场景 B：用户选中一条 CREATE 记录，想"恢复到此版本"
  → 对象当前已存在（因为是先 CREATE 后 ALTER 过）
  → 将 sqlcommand 中的 CREATE 替换为 ALTER
  → RestoreType.AlterToHistory

场景 C：用户选中一条 DROP 记录，想"恢复到此版本"（撤销 DROP）
  → DROP 语句本身不能恢复对象，需要取上一条非 DROP 版本的 sqlcommand
  → 该 sqlcommand 是 CREATE 或 ALTER 语句
  → 将 ALTER 替换为 CREATE（如果原来是 ALTER）
  → RestoreType.RecreateDropped

场景 D：用户选中一条 CREATE 记录，想"撤销创建"（即恢复到创建前）
  → 生成 DROP IF EXISTS 脚本
  → RestoreType.DropCreated
```

---

## 六、UI 设计

### 6.1 ToolWindow 集成架构

Local History 作为可停靠 ToolWindow 嵌入 SSMS 窗口，参照 `AIChatToolWindow` 实现模式：

- `[ProvideToolWindow(typeof(LocalHistoryToolWindow))]` 在 Package 上注册
- `LocalHistoryToolWindow` 继承 `ToolWindowPane`，`[Guid]` 标识，`Caption` 动态显示当前对象名（如 `"版本历史 - dbo.MonitorMesCheckFlow"`）
- `Content` 设为 `LocalHistoryToolWindowControl`（WPF UserControl）
- 停靠位置默认在右侧，与 AI 聊天面板并列，用户可自由拖拽/固定/自动隐藏
- 生命周期参照 AIChatToolWindow：关闭即隐藏，下次打开复用；`static` 服务引用 + `InitializeContent` 模式

```
SSMS 主窗口
┌────────────────────────────────┬──────────────────────────┐
│                                │ 📋 版本历史               │
│                                │ ──────────────────────── │
│     SQL 编辑器区域              │ 🔍 [搜索对象...]         │
│                                │ ──────────────────────── │
│                                │ #12 2026-07-07 ALTER zs │
│                                │ #11 2026-07-06 ALTER ls │
│                                │ #10 2026-07-05 CREATE zs│
│                                │                          │
│                                │ 共 12 个版本  [🔄 刷新]   │
├────────────────────────────────┼──────────────────────────┤
│                                │ 🤖 SSMSBoost++ AI 助手   │
└────────────────────────────────┴──────────────────────────┘
```

### 6.2 主界面布局（单栏）

ToolWindow 内部为单栏垂直布局，无左右分栏：

**顶部 — 对象搜索/选择下拉框**：
- `ComboBox` + `IsEditable=True`，输入时实时过滤 MESBASE 中有版本记录的对象名
- 默认值：当前窗口脚本自动推断的对象名（`Schema.ObjectName`），以斜体灰色显示
- 用户可手动输入或从下拉列表选择任意对象，选中后立即加载其版本历史
- 下拉列表项格式：`dbo.usp_GetData`（12 版本）— 显示版本数作为辅助信息

**中部 — DataGrid 版本列表**：
- 列：`#`（序号倒序）、`时间`（短日期格式 `MM-dd HH:mm`）、`变更类型`（彩色标签）、`操作人`、`版本号`
- 支持单选（单击）和 Ctrl 多选两行（用于历史版本间对比）
- 蓝色高亮当前选中行
- 首次加载时自动选中最新版本行

**底部 — 状态栏**：
- 左侧："共 N 个版本"
- 右侧：`[🔄 刷新]` 按钮

```
┌──────────────────────────────────────┐
│ 📋 版本历史 - dbo.usp_GetData         │
├──────────────────────────────────────┤
│ 🔍 [dbo.usp_GetData         ▼]       │
├──────────────────────────────────────┤
│  #   时间       变更类型  操作人  版本 │
│ ──────────────────────────────────── │
│  12  07-07 09:30 ALTER   zhangsan V12│  ← 默认选中（蓝底高亮）
│  11  07-06 14:15 ALTER   lisi     V11│
│  10  07-05 10:00 CREATE  zhangsan V1 │
│  ...                                 │
├──────────────────────────────────────┤
│  共 12 个版本              [🔄 刷新]  │
└──────────────────────────────────────┘
```

> 变更类型列渲染为彩色标签：`CREATE` 绿色 (#27AE60)、`ALTER` 橙色 (#F39C12)、`DROP` 红色 (#E74C3C)

### 6.3 Diff 弹窗交互

选中版本行后弹出 `LocalHistoryDiffDialog` 对比弹窗，分两种模式：

**模式 A：与服务器当前版本对比（默认）**
- 触发：单击选中一行版本
- 左侧面板："服务器当前定义" — 通过当前连接实时查询 `sys.sql_modules` 获取对象定义
- 右侧面板：`V{n} - {时间} ({操作人})` — 选中版本的 `sqlcommand`
- 中间：Diff 高亮区域（复用 `DiffCalculator.GetSideBySideDiff()`）

**模式 B：两个历史版本间对比**
- 触发：Ctrl 多选两行版本
- 左侧面板：`V{m} - {时间} ({操作人})`（较旧版本）
- 右侧面板：`V{n} - {时间} ({操作人})`（较新版本）
- 中间：Diff 高亮区域

```
┌─ 版本差异对比 ──────────────────────────────────────────┐
│                                                         │
│  ┌─ 服务器当前定义 ───────┐ ┌─ V12 - 07-07 09:30 (zs) ─┐│
│  │                        │ │                          ││
│  │ ALTER PROCEDURE dbo... │ │ ALTER PROCEDURE dbo...   ││
│  │ AS                     │ │ AS                       ││
│  │   SELECT *             │ │   SELECT *               ││
│  │   FROM Orders        │ │   FROM Orders WITH(NOLOCK)││
│  │   WHERE 1=1          │ │   WHERE Status = 1       ││
│  │                        │ │                          ││
│  │  - WHERE 1=1           │ │  + WHERE Status = 1      ││
│  │  - SELECT *            │ │  + WITH(NOLOCK)          ││
│  └────────────────────────┘ └──────────────────────────┘│
│                                                         │
│                     [回退到此版本] [在编辑器中打开] [关闭]│
└─────────────────────────────────────────────────────────┘
```

按钮行为：
- **[回退到此版本]**（蓝色主按钮）：将右侧版本的 `sqlcommand` 作为恢复脚本，在新编辑器窗口中打开供用户审阅执行
- **[在编辑器中打开]**：将右侧版本的 `sqlcommand` 在新编辑器窗口中打开
- **[关闭]**：关闭差异弹窗

### 6.4 窗口切换自动刷新

在 `LocalHistoryToolWindowControl` 中监听 SSMS 编辑器窗口切换事件：

```csharp
// 监听 DTE 窗口激活事件
_dte.Events.WindowEvents.WindowActivated += OnWindowActivated;

private void OnWindowActivated(Window gotFocus, Window lostFocus)
{
    // 判断激活窗口是否为 SQL 编辑器（Kind == "Document" 且含有 SQL 内容）
    if (gotFocus.Kind == "Document" && IsSqlEditor(gotFocus))
    {
        // 触发 3.3 节自动推断逻辑：重新获取脚本 → 解析对象 → 更新搜索框和版本列表
        _ = RefreshForActiveWindowAsync();
    }
}
```

行为规则：
- 切换标签页后自动执行 3.3 节推断流程，更新搜索框文本和 DataGrid 数据
- 若新窗口无 DDL 语句 → 搜索框清空，DataGrid 显示空状态提示
- 若获取连接失败 → 搜索框保持原值，底部显示错误提示
- 刷新过程中显示加载动画，避免 UI 卡顿

### 6.5 Diff 渲染复用

差异高亮区域复用现有基础设施：

```csharp
// 版本对比核心调用
var calculator = new DiffCalculator();
var diff = calculator.GetSideBySideDiff(
    NormalizeSql(leftScript),
    NormalizeSql(rightScript));

// LocalHistoryDiffDialog 内嵌 AIDiffDialog 的 Diff 渲染控件
// 左右分栏展示，Diff 高亮语法着色
```

左侧定义获取（服务器当前版本）：

```csharp
// 通过当前连接的 sys.sql_modules 实时查询
var sql = @"
    SELECT definition
    FROM sys.sql_modules m
    INNER JOIN sys.objects o ON m.object_id = o.object_id
    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = @schema AND o.name = @name AND o.type_desc = @type";
```

---

## 七、VersionGuard 集成

### 7.1 场景

当 ExecutionInterceptor 通过 VersionChecker 检测到对象被外部修改时（`modify_date > scriptOpenTime`），在现有的 VersionWarningDialog 中增加"查看版本历史"入口。

### 7.2 集成方式

在 `VersionWarningDialog` 中新增按钮：

```
┌─ 版本守卫警告 ──────────────────────────┐
│                                        │
│  ⚠ 检测到以下对象已被外部修改：         │
│                                        │
│  dbo.usp_GetData — 2小时前被 lisi 修改  │
│                                        │
│  [查看差异] [查看版本历史] [继续执行]    │
│  [重新加载脚本] [取消]                  │
│                                        │
└────────────────────────────────────────┘
```

点击"查看版本历史"时：
1. 获取当前被修改对象的 `objectname` 和当前窗口的连接串
2. 用 `SqlConnectionStringBuilder` 将连接串的 `InitialCatalog` 替换为 MESBASE
3. 通过 `Package.FindToolWindow(typeof(LocalHistoryToolWindow), 0, true)` 查找或创建 ToolWindow
4. 调用 `LocalHistoryToolWindow.InitializeContent(connectionString, objectName)` 传入推导连接串和对象名
5. 调用 `(IVsWindowFrame)window.Frame.Show()` 显示 ToolWindow
6. 自动选中并高亮最新版本行（即外部修改对应的版本）

### 7.3 实现要点

在 `VersionWarningDialog.xaml.cs` 中：
- 新增 `LocalHistoryButton` 按钮和点击事件
- 点击事件中通过 `Package.FindToolWindow` 获取 `LocalHistoryToolWindow` 实例
- 调用 `InitializeContent(mesbaseConnStr, objectName)` 初始化并指定初始对象
- 通过 `IVsWindowFrame.Show()` 激活显示 ToolWindow

---

## 八、命令注册

### 8.1 新增命令

| 命令 | VSCT ID | 说明 |
|------|---------|------|
| `LocalHistoryCommand` | `0x0410` | 打开 Local History 工具窗口（ToolWindow） |
| `LocalHistoryConfigCommand` | `0x0420` | 打开 Local History 设置 |

### 8.2 VSCT 修改

在 `guidSSMSBoostPlusCmdSet` 中新增 IDSymbol：

```xml
<IDSymbol name="cmdidLocalHistory" value="0x0410"/>
<IDSymbol name="cmdidLocalHistoryConfig" value="0x0420"/>
```

新增按钮（放在 VersionGuard 分组中）：

```xml
<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidLocalHistory" priority="0x0140" type="Button">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_VersionGuard"/>
  <CommandFlag>DynamicVisibility</CommandFlag>
  <Strings>
    <ButtonText>📋 版本历史...</ButtonText>
    <CommandName>LocalHistory</CommandName>
  </Strings>
</Button>

<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidLocalHistoryConfig" priority="0x0150" type="Button">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_Config"/>
  <Strings>
    <ButtonText>⚙ 版本历史设置...</ButtonText>
    <CommandName>LocalHistorySettings</CommandName>
  </Strings>
</Button>
```

CommandPlacement 到右键上下文菜单：

```xml
<CommandPlacement guid="guidSSMSBoostPlusCmdSet" id="cmdidLocalHistory" priority="0x0140">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_ContextVersionGuard_Items"/>
</CommandPlacement>
```

### 8.3 Package 注册

**SSMSBoostPlusPackage.cs 增加 ProvideToolWindow 特性：**

```csharp
// 仿照 AIChatToolWindow 注册模式
[ProvideToolWindow(typeof(UI.VersionHistory.LocalHistoryToolWindow))]
```

在 `SSMSBoostPlusPackage.InitializeAsync()` 中：

```csharp
// 设置 LocalHistoryToolWindow 静态服务引用（确保 VS SDK 布局恢复时也能正确初始化）
UI.VersionHistory.LocalHistoryToolWindow.StaticServiceProvider = this;
UI.VersionHistory.LocalHistoryToolWindow.StaticConfigService = configService;
UI.VersionHistory.LocalHistoryToolWindow.StaticLogger = logger;

// 注册命令
await Commands.LocalHistoryCommand.InitializeAsync(
    this, logger, configService, connectionService, editorService);
await Commands.LocalHistoryConfigCommand.InitializeAsync(this, logger, configService);
```

### 8.4 LocalHistoryCommand 实现模式

仿照 `AIChatCommand`，使用 `Package.FindToolWindow` 查找或创建 ToolWindow：

```csharp
// LocalHistoryCommand.ExecuteAsync 核心逻辑
var window = Package.FindToolWindow(
    typeof(UI.VersionHistory.LocalHistoryToolWindow),
    0,     // 单实例
    true); // 不存在则创建

if (window?.Frame == null) return;

// 初始化内容（执行 3.3 节自动推断：连接串推导 + 对象推断）
if (window is UI.VersionHistory.LocalHistoryToolWindow historyWindow)
{
    historyWindow.InitializeContent(Package, _configService, _logger,
        _connectionService, _editorService);
}

var frame = (IVsWindowFrame)window.Frame;
ErrorHandler.ThrowOnFailure(frame.Show());
```

---

## 九、文件清单

| # | 文件路径 | 操作 | 说明 |
|---|---------|------|------|
| 1 | `src/Core/VersionHistory/IVersionHistoryService.cs` | **新建** | 服务接口 |
| 2 | `src/Core/VersionHistory/VersionHistoryService.cs` | **新建** | 服务实现 |
| 3 | `src/Core/VersionHistory/Models/VersionHistoryModels.cs` | **新建** | 数据模型 |
| 4 | `src/SSMS/Commands/LocalHistoryCommand.cs` | **新建** | 打开 Local History 工具窗口；注入 ConnectionServiceFacade + IEditorService，FindToolWindow 模式 |
| 5 | `src/SSMS/UI/VersionHistory/LocalHistoryToolWindow.cs` | **新建** | ToolWindow 宿主（继承 ToolWindowPane），Content = LocalHistoryToolWindowControl |
| 6 | `src/SSMS/UI/VersionHistory/LocalHistoryToolWindowControl.xaml` | **新建** | WPF UserControl：搜索下拉框 + DataGrid + 状态栏 |
| 7 | `src/SSMS/UI/VersionHistory/LocalHistoryToolWindowControl.xaml.cs` | **新建** | 代码后台：加载版本列表、搜索过滤、选中→弹出Diff、窗口切换监听 |
| 8 | `src/SSMS/UI/VersionHistory/LocalHistoryDiffDialog.xaml` | **新建** | 版本差异对比弹窗 XAML：左右分栏 + Diff 高亮区 + 底部按钮 |
| 9 | `src/SSMS/UI/VersionHistory/LocalHistoryDiffDialog.xaml.cs` | **新建** | 弹窗代码后台：构造对比内容、按钮事件 |
| 10 | `src/SSMS/SSMSBoostPlus.vsct` | **修改** | 新增命令定义 |
| 11 | `src/SSMS/SSMSBoostPlusPackage.cs` | **修改** | 新增 [ProvideToolWindow]、静态引用设置、注册新命令 |

可选修改（VersionGuard 集成）：

| # | 文件路径 | 操作 | 说明 |
|---|---------|------|------|
| 9 | `src/SSMS/UI/Dialogs/VersionWarningDialog.xaml` | **修改** | 新增"查看版本历史"按钮 |
| 10 | `src/SSMS/UI/Dialogs/VersionWarningDialog.xaml.cs` | **修改** | 按钮点击事件 |
| 11 | `src/Core/Models/Configurations.cs` | **修改** | 新增 LocalHistoryConfig |
| 12 | `src/Core/Interfaces/IConfigurationService.cs` | **修改** | 新增 Get/Save LocalHistoryConfig |
| 13 | `src/Core/Services/ConfigurationService.cs` | **修改** | 实现 Get/Save LocalHistoryConfig |
| 14 | `tests/SSMSBoostPlus.Tests/VersionHistoryServiceTests.cs` | **新建** | 单元测试 |

---

## 十、与原 Phase 1 的差异分析

### 10.1 不再需要的组件

| 原方案组件 | 不再需要的原因 |
|-----------|--------------|
| DDL Trigger 创建逻辑 | Trigger 已存在于 MESBASE |
| `__SSMSBoost_VersionHistory` 建表逻辑 | 表已存在 |
| `EnsureTrackingInfrastructureAsync` | 不需要搭建基础设施 |
| `RecordChangeAsync` + 去重机制 | DDL Trigger 单源捕获，无重复 |
| ExecutionInterceptor 中的 `RecordChangeAsync` 调用 | 不需要二次记录 |
| `Source` 字段 + 去重逻辑 | 单源无需去重 |
| `VersionHistoryConfig`（原方案独立配置类） | 替换为更简洁的 `LocalHistoryConfig` |
| `VersionHistoryConfigDialog` | 替换为 `LocalHistoryConfigCommand`（复用设置页面模式） |
| `CleanupHistoryAsync` | 表由 DBA 管理，插件不负责清理 |

### 10.2 新增能力

| 新方案能力 | 说明 |
|-----------|------|
| 对象列表浏览 | 按 `objectname` 分组，显示版本数和最后修改人 |
| 任意版本间 Diff | 选中两个版本直接对比（原方案只能与前一版本对比） |
| 简化的恢复逻辑 | 直接使用 `sqlcommand` 字段，不需要 PreviousDefinition/NewDefinition 双字段 |
| VersionGuard 联动 | 外部修改检测 → 一键查看版本历史 |
| 脚本感知自动推断 | 根据当前窗口脚本内容自动推断目标对象，复用当前连接推导 MESBASE 连接串，无需用户配置 |

### 10.3 数据模型映射

| 现有表字段 | C# 模型字段 | 说明 |
|-----------|------------|------|
| `ID` | `Id` | 直接映射 |
| `databasename` | `DatabaseName` | 直接映射 |
| `eventtype` | `EventType` | 直接映射，额外提供 `ChangeType` 计算属性 |
| `objectname` | `ObjectName` | 直接映射 |
| `objecttype` | `ObjectType` | 直接映射 |
| `sqlcommand` | `SqlCommand` | 直接映射，包含完整 DDL |
| `loginname` | `LoginName` | 直接映射 |
| `hostname` | `HostName` | 直接映射 |
| `PostTime` | `PostTime` | 直接映射 |
| `Version` | `Version` | 直接映射 |

---

## 十一、边缘情况与异常处理

| 场景 | 处理方式 |
|------|---------|
| 当前无活动 SQL 窗口 | 提示"请先打开一个 SQL 查询窗口" |
| 获取连接失败 | 提示"无法获取当前数据库连接，请确保已连接到数据库服务器" |
| 当前服务器无 MESBASE 数据库 | 连接 MESBASE 时 SqlException，提示"当前服务器上未找到 MESBASE 数据库" |
| MESBASE 不可连接 | 捕获 SqlException，对话框提示"无法连接到版本历史数据库，请检查网络" |
| 表 `DBTrigger_Object_Version_Table` 不存在 | 查询时捕获异常，提示"版本表不存在，请联系 DBA 确认 DDL Trigger 已部署" |
| 脚本解析失败（语法错误） | ObjectExtractor 已有 try-catch 返回空列表，走"无 DDL"分支 |
| 脚本含多个 DDL 语句 | 取第一个 `IsDefinition=true` 的对象，自动打开其历史记录 |
| 脚本无 DDL（纯 SELECT） | 打开空面板，提示"未检测到数据库对象定义语句" |
| 对象无任何版本记录 | 时间线区域显示"暂无版本记录" |
| DROP 事件后恢复 | 取上一条非 DROP 版本的 sqlcommand，将 ALTER 转 CREATE |
| 恢复时对象已被删除 | 恢复脚本使用 CREATE 语句（从历史 sqlcommand 提取） |
| 恢复时对象仍存在 | 恢复脚本使用 ALTER 语句（CREATE 转 ALTER 或直接用 ALTER） |
| 超大 sqlcommand（>1MB） | DiffCalculator 已有规范化逻辑，可正常处理 |
| 多人同时操作 Local History | 只读查询，无并发冲突 |
| 选中文本而非全文 | GetCodeAsync 已处理：有选中返回选中，无选中返回全文 |

---

## 十二、性能考虑

| 操作 | 预期性能 | 优化措施 |
|------|---------|---------|
| 连接串 + 对象推断 | <300ms | 连接获取走反射（100-300ms），ScriptDom 解析 <10ms，两线并行（Task.WhenAll） |
| 加载对象列表 | <500ms（千级对象） | GROUP BY 查询，建议补充索引 `IX_..._03` |
| 加载对象版本时间线 | <200ms（百级版本） | `objectname + PostTime DESC` 索引覆盖 |
| 版本间 Diff | <100ms | DiffPlex 已在项目中验证性能 |
| 全局最近变更 | <500ms | `PostTime DESC` 索引覆盖 |
| 恢复脚本生成 | <50ms | 纯文本处理，无数据库查询（除获取记录本身） |

---

## 十三、测试计划

| 测试用例 | 说明 |
|---------|------|
| `GetObjectHistory_ReturnsOrderedRecords` | 按时间倒序返回版本列表 |
| `GetObjectHistory_FiltersByDatabase` | 按数据库名筛选 |
| `GetObjectList_ReturnsGroupedSummary` | 对象列表正确分组，版本数准确 |
| `GetRecentChanges_ReturnsLatestFirst` | 全局最近变更按时间倒序 |
| `GenerateRestoreScript_Alter_ReturnsAlterScript` | ALTER 事件恢复：直接返回 sqlcommand |
| `GenerateRestoreScript_Create_ConvertsToAlter` | CREATE 事件恢复：CREATE 转 ALTER |
| `GenerateRestoreScript_Drop_GetsPreviousDefinition` | DROP 事件恢复：取上一条非 DROP 定义 |
| `GenerateRestoreScript_NotFound_ReturnsError` | 版本 ID 不存在返回错误 |
| `GetVersionSql_ReturnsFullSqlCommand` | 获取完整 DDL 语句 |
| `ConnectionFailure_ReturnsErrorMessage` | MESBASE 不可连接时返回友好错误 |
| `AutoInferConnection_ReplacesInitialCatalog` | 自动推断连接串：正确将 InitialCatalog 替换为 MESBASE |
| `AutoInferObject_ParsesFirstDdl` | 脚本含多个 DDL 时取第一个 IsDefinition 对象 |
| `AutoInferObject_NoDdl_ReturnsNull` | 脚本无 DDL 语句时返回 null |

---

## 十四、后续演进

本方案仅重构 Phase 1，不改变 Phase 2/3/4 的规划：

- **Phase 2（SQL 审核引擎）** — 不受影响，仍基于 ScriptDom
- **Phase 3（代码审批流程）** — 可基于 Local History 的版本数据扩展
- **Phase 4（Git 提交）** — 不受影响，仍计划使用 LibGit2Sharp

Local History 的版本数据天然可作为审批流程的上下文（"从 V12 恢复到 V11"），也为 Git 提交提供了变更追溯基础。
