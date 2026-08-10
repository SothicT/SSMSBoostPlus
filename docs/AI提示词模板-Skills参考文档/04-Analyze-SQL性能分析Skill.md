---
name: ai-analyze
description: "SSMSBoostPlus AI 模板 — SQL 性能分析。结合执行计划、Schema 和索引信息对 SQL 进行深度性能分析。触发：性能分析、执行计划、analyze、优化SQL、查询优化、瓶颈分析"
---

# 04 — SQL 性能分析 Skill（analyze.md）

## 概述

对应 Prompt 模板 `src/AI/Prompts/Templates/analyze.md`，功能是结合用户提供的 Schema 信息、索引信息和执行计划，对 T-SQL 代码进行深度性能分析。这是所有模板中**上下文最丰富**的一个。

## 触发条件

当用户提出以下意图时，CodeBuddy 应识别并引导用户使用此功能：

| 场景 | 示例表述 |
|------|---------|
| 性能分析 | "分析性能"、"性能分析"、"analyze performance" |
| 执行计划解读 | "解读执行计划"、"看看执行计划"、"explain query plan" |
| 优化 SQL | "优化查询速度"、"SQL 跑得慢"、"慢查询分析" |
| 索引建议 | "建议加什么索引"、"missing index"、"索引优化" |

> **实际使用方式**：用户可通过两种方式触发：
> 1. AI 对话面板的「📊 分析」快捷按钮（Ctrl+Shift+K 打开面板）
> 2. 菜单命令 **SSMSBoost++ → AI 性能分析** 或快捷键 `Ctrl+Shift+P`（弹出独立流式结果弹窗）

## 前置条件

| 条件 | 说明 |
|------|------|
| 选中 SQL 代码 | 编辑器中有活动的 SQL 文档，且选中了代码片段 |
| AI 已配置 | 已通过 SSMSBoost++ → ⚙ AI 配置 完成设置 |
| 数据库连接（推荐） | 有活动连接时自动采集执行计划、Schema、索引信息；无连接时仅基于代码文本分析 |

## 上下文采集（最重要的 Skill）

Analyze 是**唯一**完整采集所有上下文信息的模板：

| 字段 | 采集方式 | 说明 | 最大长度 |
|------|---------|------|---------|
| **Code** | `editorService.GetCodeAsync()` | 用户选中的 SQL 代码 | — |
| **DatabaseName** | `editorService.GetActiveDatabaseName()` | 当前数据库名 | — |
| **ExecutionPlan** | `_databaseService.GetExecutionPlanAsync(connStr, code)` | SQL Server 实际执行计划（XML 格式） | 截断 8000 字符 |
| **SchemaInfo** | `_databaseService.GetTableSchemaAsync(connStr, tableName)` | 从 SQL 提取表名后逐个查询表结构（列名、类型、是否为 NULL、默认值等） | 最多 10 张表 |
| **IndexInfo** | `_databaseService.GetTableIndexesAsync(connStr, tableName)` | 从 SQL 提取表名后逐个查询索引信息（索引类型、包含列等） | 最多 10 张表 |

### 表名提取逻辑

使用正则表达式从 SQL 中提取 `FROM`、`JOIN`、`UPDATE`、`INSERT INTO`、`DELETE FROM` 后的表名：

```csharp
var pattern = @"(?:FROM|JOIN|UPDATE|INTO|DELETE\s+FROM)\s+"
            + @"(?:\[?(?<schema>\w+)\]?\.)?\[?(?<table>\w+)\]?";
```

- 支持 `[schema].[table]`、`schema.table`、`[table]`、`table` 格式
- 过滤 SQL 关键字（SELECT、WHERE、SET 等）
- 最多处理前 10 张表（通过 `tableNames.Take(10)`）
- 单表查询失败时跳过（不影响其他表）

## 核心角色指令（内嵌）

AI 被赋予"SQL 性能分析专家"角色，从以下五个方面进行深度分析：

### 1. 📊 执行计划分析

解读执行计划中的关键操作：

| 操作类型 | 分析要点 |
|---------|---------|
| 扫描操作 | Table Scan、Clustered Index Scan、Index Scan |
| 查找操作 | Index Seek、Clustered Index Seek |
| 连接类型 | Nested Loop、Hash Match、Merge Join |
| 排序操作 | Sort、Top Sort |
| 聚合操作 | Stream Aggregate、Hash Aggregate |
| 并行操作 | Parallelism |
| Spool 操作 | Eager Spool（假脱机缓存中间结果）、Lazy Spool |

识别高代价操作符（Cost > 50%）和异常执行路径。

### 2. 📈 统计信息评估

- 检查统计信息最后更新时间（隐式判断）
- 识别统计信息缺失或过时导致的估算偏差
- 当**"估算行数 vs 实际行数"差异 > 10 倍**时标记警告
- 建议统计信息更新策略

### 3. 🔍 缺失索引

根据查询条件、JOIN 列、ORDER BY 列识别缺失索引：

- 建议索引列（WHERE 条件列、JOIN 列）
- 提供 INCLUDE 列建议（覆盖索引）
- 给出具体的 `CREATE INDEX` T-SQL 语句
- 评估索引对写入性能的影响

### 4. ⚠️ 性能瓶颈

| 瓶颈类型 | 说明 |
|---------|------|
| 全表扫描 | 缺少合适的索引导致 |
| 隐式类型转换 | 列类型与查询参数类型不匹配 |
| 参数嗅探 | 缓存计划不适合当前参数 |
| 非 SARGable 表达式 | WHERE 条件中的函数包裹列 |
| 估算与实际行数差异过大 | 统计信息过时 |
| Key Lookup 过多 | 索引未覆盖所有查询列 |
| 不合理的 JOIN 类型选择 | 小表与大表 Nested Loop 等 |
| Spool 操作 | 中间结果集需要缓存 |
| TempDB 压力 | Sort/Hash 溢出到 TempDB、临时表滥用 |
| 锁/阻塞 | 锁升级、事务隔离级别导致阻塞 |

### 5. ✅ 优化方案

- SQL 改写建议（附带改写前后对比）
- 索引优化方案（附带创建语句）
- 查询提示建议（必要时）
- 分区或归档建议（大表场景）
- 统计信息维护建议（`UPDATE STATISTICS` 语句）

> 完整的 Prompt 规则细节见 `src/AI/Prompts/Templates/analyze.md`

## 输出规范

- 格式：Markdown，按以下 5 个章节组织
  - `## 📊 执行计划分析`
  - `## 📈 统计信息评估`
  - `## 🔍 缺失索引`
  - `## ⚠️ 性能瓶颈`
  - `## ✅ 优化方案`
- 无问题章节标注 ✅
- 代码块使用 ```sql 标记
- 输出语言：中文

## 调用链路（两种路径）

### 路径 A：AI 对话面板快捷按钮

```
用户选中代码 → 点击「📊 分析」按钮
  → AIChatControl.QuickAnalyze_Click()
    → ExecuteQuickAction("性能分析", PromptTemplate.Analyze, ...)
      → CodeAnalysisService.AnalyzePerformanceAsync(code, databaseName: dbName)
        → （注意：此处未传递 SchemaInfo/IndexInfo/ExecutionPlan）
        → bridge.ExecuteAsync(request)
```

### 路径 B：独立命令（Ctrl+Shift+P）— 更完整

```
用户选中代码 → Ctrl+Shift+P
  → AIPerformanceCommand.ExecuteAsync()
    → 从编辑器获取 connectionString + databaseName
    → 获取 executionPlanXml（GetExecutionPlanAsync）
    → 提取表名（ExtractTableNames）
    → 逐个获取 schemaInfo（GetTableSchemaAsync）
    → 逐个获取 indexInfo（GetTableIndexesAsync）
    → 构建 AIRequest（含 SchemaInfo + IndexInfo + ExecutionPlan）
    → 弹出 AIStreamResultDialog（流式结果弹窗）
```

## 代码位置

| 组件 | 文件 | 关键方法 |
|------|------|---------|
| 快捷按钮（面板） | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `QuickAnalyze_Click()` (L496) |
| 独立命令 | `src/SSMS/Commands/AIPerformanceCommand.cs` | `ExecuteAsync()` (L81) |
| 分析服务 | `src/AI/Services/CodeAnalysisService.cs` | `AnalyzePerformanceAsync()` (L117) |
| Prompt 构建 | `src/AI/Prompts/PromptTemplateManager.cs` | `BuildAnalyzePrompt()` (L227) |
| 表名提取 | `src/SSMS/Commands/AIPerformanceCommand.cs` | `ExtractTableNames()` (L281) |
| 数据库服务 | `src/Core/Services/DatabaseService.cs` | `GetExecutionPlanAsync()` / `GetTableSchemaAsync()` / `GetTableIndexesAsync()` |

## 注意事项

1. **路径 A vs 路径 B 差异**：`AIChatControl.ExecuteQuickAction` 中的 Analyze 调用 **未传递** SchemaInfo/IndexInfo/ExecutionPlan，上下文仅包含 DatabaseName。如需完整分析，应**优先使用路径 B**（Ctrl+Shift+P 独立命令）或增强 ExecuteQuickAction 的上下文采集逻辑

2. **执行计划获取可能失败**：当 SQL 包含临时表、表变量等导致无法生成实际执行计划时，`GetExecutionPlanAsync` 会抛出异常，此时应降级为仅基于代码文本的分析

3. **执行计划截断**：执行计划 XML 截断到 8000 字符（`Truncate(8000)`），超长执行计划会被截断——若执行计划过长，AI 可能丢失后半部分信息

4. **表名提取局限性**：正则表达式方法无法识别动态 SQL 中的表名、CTE 别名后的真实表名、同义词等

5. **Schema/Index 获取可能失败**：单表 Schema/Index 获取异常时跳过该表，不中断整体流程

6. **只读模式**：ReadOnly=true，AI 仅输出分析建议，不修改文件

7. **输出格式**：流式弹窗（AIStreamResultDialog）实时渲染 Markdown 结果，支持 highlight.js 高亮和暗色主题