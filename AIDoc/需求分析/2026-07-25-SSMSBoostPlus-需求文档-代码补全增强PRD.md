---
title: SQL代码智能补全增强 - 产品需求文档
date: 2026-07-25
author: PM Agent
tags: [代码补全, 表结构缓存, 字段补全, schema补全, 自动注释]
---

# SQL代码智能补全增强 - 产品需求文档

## 1. 文档信息

| 项目 | 内容 |
|------|------|
| 版本号 | v1.1 |
| 创建日期 | 2026-07-25 |
| 作者 | PM Agent |
| 状态 | 初稿 |
| 涉及模块 | SqlTemplateCompletionService, DatabaseObjectCacheService, 文件打开事件 |

### 变更记录

| 版本 | 日期 | 变更内容 | 变更人 |
|------|------|----------|--------|
| v1.0 | 2026-07-25 | 初始版本 | PM Agent |
| v1.1 | 2026-07-26 | 合并US-04/US-05：根据上下文智能决定Schema层级；扩展US-08：新增SP/函数参数补全 | PM Agent |

---

## 2. 产品概述

### 2.1 业务背景与问题陈述

SSMSBoostPlus 已实现基础的 SQL 代码智能补全功能，包括 SQL 关键词提示、模板展开、当前数据库对象补全和跨库对象补全。但在实际使用中发现以下核心问题：

**Bug 类：**
1. 补全展开时未处理编辑器中的文本选区，选中文本后触发补全会丢失选中内容
2. 输入 DB 名称后按小数点，弹窗被错误关闭，无法自动切换到该 DB 的对象补全
3. ESC 键无法关闭代码补全弹窗
4. 已输入的 DB 名称仍然在补全候选中重复出现

**功能缺失类：**
5. 缺少 `dbo` 等 Schema 名称的补全提示
6. 缺少 Schema 级别的智能补全引导（DBNAME → Schema → Object）
7. 缺少表结构的本地缓存和展示
8. 缺少表字段的上下文感知补全
9. 缺少 SP/函数参数的补全提示
10. 打开已存在的脚本文件时，缺少对 ALTER/CREATE 语句的保护性注释

### 2.2 产品目标与成功标准

| 目标 | 成功标准（可量化指标） |
|------|----------------------|
| 解决现有 bug | ESC 关闭弹窗成功率 100%、无冗余 DB 名称候选 |
| Schema 补全 | 输入 DBNAME. 后根据上下文智能展示 schema 或直接展示对象列表 |
| 表结构缓存 | 缓存包含表结构的列信息，与对象缓存同时加载 |
| 表结构展示 | 单击补全项弹出表结构（CREATE TABLE 脚本），双击自动补全 |
| 表字段补全 | 在 `TableA.` 后正确弹出 A 表的字段列表（含别名识别） |
| SP参数补全 | 选中SP后自动弹出参数列表并支持逐参补全 |
| 自动注释 | 通过菜单打开脚本时自动注释 ALTER/CREATE 首行 |

### 2.3 目标用户画像

| 属性 | 描述 |
|------|------|
| 角色 | SQL Server 开发人员 / DBA |
| 典型场景 | 在 SSMS 中编写 SQL 查询和存储过程 |
| 痛点 | 需要频繁跨库查询对象名和表结构，手动输入 schema+对象名效率低 |
| 使用频率 | 每天 4-8 小时 |

### 2.4 核心使用场景

1. **跨库查询编写**：`SELECT * FROM MESDATA.dbo.Users WHERE ...` — 需要逐级提示 DB→Schema→Object
2. **表字段查询**：`SELECT A.Name, A.Age FROM Users A WHERE A.` — 需要提示 A 表的字段
3. **SP 调用编写**：`EXEC MESDATA.dbo.GetUserBy @Id, @Name` — 需要跳过 Schema 层直接补全 SP 及参数
4. **脚本保护**：打开旧脚本查看时，自动注释 ALTER/CREATE 防止误执行
5. **表结构查看**：在补全列表中单击表名即可查看其结构，不需要另外查询

---

## 3. 用户故事与验收条件

### 3.1 Bug 修复类

#### US-01：补全展开时删除选中文本

> **作为** SQL 开发者
> **我希望** 在编辑器中选中文本后触发补全（输入或选择候选项），选中的文本被自动删除
> **以便** 不需要手动删除选中内容，提高编码效率

**验收条件：**
```
Given 编辑器中存在选中文本 "EXEC"
When 用户输入新的字符（如 "S"）触发补全弹窗
Then 选中文本 "EXEC" 被自动删除
And 输入字符 "S" 被正确插入

Given 编辑器中存在选中文本 "EXEC"
And 补全弹窗已显示
When 用户按 Enter/Tab 选择补全项
Then 选中文本 "EXEC" 被自动删除
And 补全内容被正确展开
```

- **优先级**：P0

#### US-02：ESC 关闭弹窗

> **作为** SQL 开发者
> **我希望** 按下 ESC 键能够关闭代码补全弹窗
> **以便** 在不需要补全时快速退出

**验收条件：**
```
Given 代码补全弹窗已显示
When 用户按下 ESC 键
Then 弹窗关闭
And 光标位置不变
And 输入缓冲区不受影响
```

- **优先级**：P0

#### US-03：已输入 DB 名称后不再重复显示

> **作为** SQL 开发者
> **我希望** 在输入了 `MESDATA` 作为数据库名称前缀后，补全弹窗中不再出现 `MESDATA.` 作为候选
> **以便** 减少冗余选项，聚焦真正需要的对象补全

**验收条件：**
```
Given 用户已输入 "MESDATA"
When 补全弹窗显示
Then 弹窗中不包含 "MESDATA." 候选
But 弹窗中包含 MESDATA 数据库下的对象候选

Given 用户已输入 "MESDATA."
When 补全弹窗显示
Then 弹窗仅显示 MESDATA 数据库下的对象/模式
And 不显示任何数据库名称候选
```

- **优先级**：P1

### 3.2 Schema 补全类（合并 US-04 + US-05）

#### US-04/05：DB 名称输入后按上下文智能补全（Schema / 对象）

> **作为** SQL 开发者
> **我希望** 输入 `DBName.` 后，补全弹窗根据上下文关键词智能决定下一步：
> - `FROM DBName.` → 弹出 Schema 列表（如 dbo, sys, guest...），选中后再弹出对象列表
> - `EXEC DBName.` → **跳过 Schema 层级**，直接弹出该 DB 下的存储过程和函数列表
> - `JOIN DBName.` → 弹出 Schema 列表
> **以便** 按三段式引导（DB→Schema→Object）逐步补全，同时在 EXEC 场景中跳过冗余的 Schema 选择

**为什么要跳过 Schema（仅 EXEC 场景）：**
- SQL Server 中存储过程和函数绝大部分位于 `dbo` Schema 下
- EXEC 调用的核心需求是快速找到 SP/函数名，不需要先选 Schema
- FROM/JOIN/SELECT 场景中，表和视图可能分布在多个 Schema 下，需要先选 Schema

**验收条件：**
```
Given 用户输入 "FROM MESDATA."
When 补全弹窗显示
Then 弹窗显示 Schema 列表（dbo, guest, sys, INFORMATION_SCHEMA 等该库的所有 schema）
And Schema 候选的 TypeTag 为 "模式"
And 选中 Schema 后插入 "schema."（带尾随点号）

Given 用户输入 "EXEC MESDATA."
When 补全弹窗显示
Then 弹窗跳过 Schema 层，直接显示 MESDATA 下的所有存储过程和函数
And 弹窗中不包含 Schema 候选

Given 用户输入 "FROM MESDATA.dbo."
When 补全弹窗显示
Then 弹窗显示 MESDATA.dbo 下的所有表/视图

Given 用户输入 "FROM MESDATA.dbo.Use"
When 补全弹窗显示
Then 弹窗过滤显示 MESDATA.dbo 下以 "Use" 开头的表/视图

Given 当前 DB 有自定义的 schema（如 "sales", "hr"）
When 用户输入 "FROM MyDB."
Then 弹窗中也包含这些自定义 schema

Given 用户输入 "JOIN MESDATA."
When 补全弹窗显示
Then 弹窗显示 Schema 列表（与 FROM 行为一致）
```

- **优先级**：P1

### 3.3 表结构缓存与展示类

#### US-06：表结构缓存

> **作为** SQL 开发者
> **我希望** 在加载数据库对象缓存时，同时加载表的结构信息（列名、数据类型等）
> **以便** 单击补全项时能查看表结构

**验收条件：**
```
Given DatabaseObjectCacheService 已加载 "MESDATA" 的缓存
Then 缓存中不仅包含表名，还包含每张表的列信息
And 列信息包括：列名、数据类型、是否可为空、长度/精度、默认值
And 缓存持久化到本地文件

Given 缓存已就绪
When 用户单击补全弹窗中的表名
Then 弹出一个新的信息弹窗，显示该表的 CREATE TABLE 脚本
```

- **优先级**：P1

#### US-07：单击查看表结构，双击补全

> **作为** SQL 开发者
> **我希望** 在补全弹窗中单击表名时弹出表结构信息，双击时自动补全表名
> **以便** 在需要查结构时查看，不需要时直接补全

**验收条件：**
```
Given 补全弹窗显示表名 "Users"
When 用户单击 "Users"
Then 弹出一个浮动提示框（Tooltip/Popup），显示 Users 表的 CREATE TABLE 脚本
And 补全弹窗本身不关闭

Given 补全弹窗显示表名 "Users"
When 用户双击 "Users"
Then 不弹出结构提示
And 直接补全 "Users" 到编辑器中
And 结构提示弹窗（如有）被关闭
```

- **优先级**：P2

### 3.4 表字段补全类

#### US-08：表字段补全 + SP/函数参数补全

> **作为** SQL 开发者
> **我希望** 在以下两种场景中能获得精准补全提示：
> 1. `TableA.` 后提示该表的所有字段名
> 2. 选中 SP/函数后自动提示其参数列表
> **以便** 快速编写查询和存储过程调用，无需手动查阅表结构或参数定义

**场景 A：表字段补全（原逻辑）**

**验收条件：**
```
Given 编辑器中存在 "SELECT * FROM Users U WHERE U."
When 补全弹窗显示
Then 弹窗显示 Users 表的所有字段（Id, Name, Email, Age...）
And 字段候选的 TypeTag 为 "字段"
And 选中字段后插入字段名 + 空格

Given 编辑器中存在 "SELECT * FROM Users WHERE Users."
When 补全弹窗显示
Then 同样显示 Users 表的所有字段

Given 编辑器中不存在表名上下文（如 "SELECT A."）
When 无匹配表名
Then 不显示字段补全，回退到关键词+模板补全
```

**场景 B：SP/函数参数补全（新增）**

> 当用户选中一个存储过程或函数后，**直接将其参数列表补全到编辑器中**，不需要额外的弹窗交互。
> 模式：选中 SP 名 → 展开为完整调用语句（含参数名）

**设计思路：**
- 不弹参数选择弹窗，减少交互层级
- 选中即补全完整参数列表，用户直接在参数名后填写值
- SP 使用空格分隔参数，函数使用圆括号 + 逗号分隔

**验收条件：**
```
Given 用户选中 SP "dbo.GetUserBy"（通过 Enter/Tab/双击）
And SP 有参数 @Id INT, @Name NVARCHAR(50)
When 补全展开后
Then 编辑器插入 "dbo.GetUserBy @Id, @Name"
And 光标定位在第一个参数值的位置（@Id 之后）
And 不弹出参数选择弹窗

Given 用户选中函数 "dbo.CalcTotal"
And 函数有参数 @amount DECIMAL, @rate DECIMAL
When 补全展开后
Then 编辑器插入 "dbo.CalcTotal(@amount, @rate)"
And 光标定位在第一个参数值的位置（@amount 之后）

Given SP 无参数
When 用户选中该 SP
Then 编辑器插入 "dbo.GetUserBy "（仅 SP 名 + 空格）
And 光标定位在 SP 名后

Given SP 有一个参数 @Id INT
When 用户选中该 SP
Then 编辑器插入 "dbo.GetUserBy @Id"
And 光标定位在 @Id 之后
```

- **优先级**：P1

#### US-09：表别名关联字段提示

> **作为** SQL 开发者
> **我希望** 在 `FROM TableA A WHERE A.` 场景中，A 作为 TableA 的别名，输入 `A.` 后提示 TableA 的字段
> **以便** 不需要记住字段名的手动输入

**验收条件：**
```
Given 编辑器中存在 "FROM Orders O WHERE O."
When 补全弹窗显示
Then 弹窗显示 Orders 表的所有字段

Given 编辑器中存在 "FROM Users U JOIN Orders O ON U.Id = O.UserId WHERE U."
When 补全弹窗显示
Then 弹窗显示 Users 表的字段

Given 编辑器中存在 "FROM Users U JOIN Orders O ON U.Id = O.UserId WHERE O."
When 补全弹窗显示
Then 弹窗显示 Orders 表的字段
```

- **优先级**：P2

### 3.5 自动注释类

#### US-10：打开脚本自动注释 ALTER/CREATE

> **作为** SQL 开发者
> **我希望** 通过 SSMSBoostPlus 菜单打开 .sql 脚本文件时，自动注释掉文件中的 ALTER / CREATE 语句行
> **以便** 防止误执行修改数据库结构的语句

**验收条件：**
```
Given 用户通过 SSMSBoostPlus "打开脚本" 菜单打开脚本文件
And 文件中包含 "CREATE PROCEDURE dbo.Test AS..."
When 脚本打开后
Then 该行被自动注释为 "-- CREATE PROCEDURE dbo.Test AS..."

Given 文件中包含 "ALTER TABLE dbo.Users ADD Column1 INT"
When 脚本打开后
Then 该行被自动注释为 "-- ALTER TABLE dbo.Users ADD Column1 INT"

Given 语句已有注释 "-- CREATE PROCEDURE dbo.Test"
When 脚本打开
Then 不重复注释，保持原有状态

Given 文件不包含 ALTER/CREATE 语句
When 脚本打开
Then 文件内容保持不变
```

- **优先级**：P1

---

## 4. 功能规格说明

### 4.1 功能模块总览

| 模块编号 | 功能名称 | 涉及组件 | 优先级 |
|----------|----------|---------|--------|
| F1 | 补全展开时处理文本选区 | SqlTemplateCompletionService | P0 |
| F2 | ESC 关闭弹窗 | SqlTemplateCompletionService.CompletionCommandFilter | P0 |
| F3 | 小数点后跨库上下文切换 | SqlTemplateCompletionService | P1 |
| F4 | DB 名称候选去重 | SqlTemplateCompletionService | P1 |
| F5 | Schema 补全（动态读取+上下文智能） | DatabaseObjectCacheService + SqlTemplateCompletionService | P1 |
| F6 | 表结构缓存（含列信息） | DatabaseObjectCacheService | P1 |
| F7 | 表结构信息弹窗（单击/双击） | SqlTemplateCompletionService (UI) | P2 |
| F8 | 表字段补全 + SP参数补全 | SqlTemplateCompletionService + DatabaseObjectCacheService | P1 |
| F9 | 表别名关联字段 | SqlTemplateCompletionService | P2 |
| F10 | 打开脚本自动注释 | 新增文件打开事件处理 | P1 |

---

### 4.2 F1：补全展开时处理文本选区

#### 触发条件
- 用户选中编辑器中的文本后：
  - 输入任意字符触发补全弹窗
  - 按 Enter/Tab 选择弹窗中的候选项

#### 正常流程
1. 用户选中文本（如 "EXEC"）
2. 用户输入新字符（如 "S"）
3. 命令过滤器拦截 TYPECHAR
4. 调用 `InsertCharAndRefresh` 前检测选区
5. 存在选区 → 先用 `edit.Delete(selectionSpan)` 删除选中文本
6. 在删除后的位置插入新字符
7. 触发弹窗刷新

#### 验收条件
- 选中文本 + 键入字符 → 选中内容被删除，字符被插入
- 选中文本 + Enter/Tab 补全 → 选中内容被删除，补全内容被插入
- 无选中文本 → 行为不变

---

### 4.3 F2：ESC 关闭弹窗

#### 问题根因分析
当前 `CompletionCommandFilter` 中 ESC 处理（第 1971-1973 行）拦截了 VSStd2K 组的 `CMDID_CANCEL`（id=2）。ESC 可能通过其他命令组（如 VSStd97）或不同路径发送。

#### 正常流程
1. 弹窗可见
2. 用户按 ESC
3. 命令过滤器拦截到 `CMDID_CANCEL`
4. 调用 `HidePopup()` 并返回 `OLECMDERR_E_CANCELED`
5. 弹窗关闭

#### 兜底方案
若 ESC 以未知命令组通过，在阶段 3.5（行 2098-2107）的兜底逻辑中，检测弹窗状态变化后不自动重开。

#### 验收条件
- 弹窗可见时按 ESC → 弹窗关闭
- 弹窗关闭后按 ESC → 不影响编辑器
- 弹窗可见时按 ESC 后继续输入 → 弹窗重新出现（正常触发）

---

### 4.4 F3：小数点后跨库上下文切换

#### 问题根因分析
当前 `OnCharTyped`（第 480-669 行）在调用 `GetWordBeforeCaret` 后，如果 word 为空（原因是 '.' 不是 `IsWordChar` 返回 true 的字符），会走向第 493-505 行的空单词分支，直接 `HidePopup()`，导致后面第 585 行的 `DetectCrossDbContext` 检测逻辑永远不会执行。

#### 正常流程（修改后）
1. 用户输入 `MESDATA.`，光标在点号后
2. `OnCharTyped` 被调用
3. **步骤调整**：先检测跨库上下文（`DetectCrossDbContext`）
4. 若检测到跨库模式 → 进入 `HandleCrossDbCompletion` 分支，显示该 DB 的 Schema 列表
5. 若未检测到 → 执行原 `GetWordBeforeCaret` 逻辑

#### 代码修改建议
将 `DetectCrossDbContext` 调用提前到 `GetWordBeforeCaret` 之前：

```csharp
internal void OnCharTyped()
{
    // 1. 优先检测跨库上下文（关键修改：提前检测）
    var (crossDbName, objPrefix) = DetectCrossDbContextV2(_activeTextView);
    if (crossDbName != null)
    {
        HandleCrossDbCompletion(crossDbName, objPrefix);
        return;
    }

    // 2. 原有逻辑
    var (word, wordStart, _) = GetWordBeforeCaret(_activeTextView);
    // ...
}
```

---

### 4.5 F4：DB 名称候选去重

#### 正常流程
1. 用户在 `OnCharTyped`（或 `AddDbNameCandidates`）中添加 DB 名称候选时
2. 检测当前已输入的 word 是否匹配某个 DB 名称的前缀
3. 如果 `word` 与某个 DB 名称**完全一致**（忽略大小写），则从候选列表中排除该 DB 名称
4. 如果 `word` 是 DB 名称的**部分前缀**，则 DB 名称保留在候选中

#### 触发条件
```csharp
// 场景：用户输入 "MESDATA"（完全匹配 DB 名称）
// → 不添加 "MESDATA." 到候选
// → 但仍可添加 MESDATA 数据库下的对象候选

// 场景：用户输入 "MES"（部分匹配）
// → 添加 "MESDATA." 到候选（得分按模糊匹配）
```

---

### 4.6 F5：Schema 补全（上下文智能）

#### 数据来源
- 启动时加载缓存时，通过 SQL 查询获取当前服务器上每个数据库的 Schema 列表
- SQL：`SELECT name FROM [DBName].sys.schemas ORDER BY name`
- 筛除系统 schema（`db_*`, `INFORMATION_SCHEMA` 保留展示，`sys`、`dbo` 保留）

#### 缓存结构扩展

在 `DatabaseObjectCache` 中新增字段：

```csharp
public class DatabaseObjectCache
{
    // ... 现有字段 ...

    /// <summary>
    /// 数据库中的所有 Schema 名称列表（新增）
    /// </summary>
    [JsonProperty("schemas")]
    public List<string> Schemas { get; set; } = new();
}
```

#### 补全流程（智能分段式引导）

根据上下文关键词智能决定是否显示 Schema 层级：

**场景 A：FROM/JOIN/SELECT 等 → 需要 Schema 层级**
```
用户输入: "FROM "
    | 空格/字符触发弹窗
弹窗显示: DB名称候选（MESDATA., OtherDB. ...）
    | 用户选中或输入 "MESDATA"
用户输入: "FROM MESDATA."
    | 自动检测跨库上下文，上下文关键词="FROM"
弹窗显示: Schema 列表（dbo, sys, guest, INFORMATION_SCHEMA ...）
    | 用户选中或输入 "dbo"
用户输入: "FROM MESDATA.dbo."
    | 自动检测跨库+Schema上下文
弹窗显示: 对象列表（根据上下文关键词分类：表/视图）
    | 用户选中
完成: "FROM MESDATA.dbo.Users"
```

**场景 B：EXEC → 跳过 Schema 层级**
```
用户输入: "EXEC "
    | 空格/字符触发弹窗
弹窗显示: DB名称候选（MESDATA., OtherDB. ...）
    | 用户选中或输入 "MESDATA"
用户输入: "EXEC MESDATA."
    | 自动检测跨库上下文，上下文关键词="EXEC"
弹窗显示: 跳过 Schema，直接显示该 DB 的 SP/函数列表
    | 用户选中
完成: "EXEC MESDATA.dbo.GetUserBy"
    | 选中 SP 后 → 自动弹出 SP 参数提示（见 F8 扩展）
```

#### Schema 候选的展示

| 属性 | 值 |
|------|-----|
| DisplayText | schema 名称（如 "dbo"） |
| TypeTag | "模式" |
| InsertText | schema名称 + "."（如 "dbo."） |
| ItemType | 新增枚举 `CompletionItemType.Schema` |

---

### 4.7 F6：表结构缓存

#### 数据来源
在 FetchAndCacheAsync 中，拉取表对象后，额外查询表的列信息：

```sql
-- 对于每张表，获取其列信息
SELECT
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    COLUMN_DEFAULT(c.column_id) AS DefaultValue,
    ep.value AS Description
FROM [DBName].sys.columns c
LEFT JOIN [DBName].sys.extended_properties ep
    ON ep.major_id = c.object_id
    AND ep.minor_id = c.column_id
    AND ep.name = 'MS_Description'
WHERE c.object_id = OBJECT_ID('[schema].[tableName]')
ORDER BY c.column_id
```

#### 缓存结构扩展

```csharp
public class DatabaseObjectInfo
{
    // ... 现有字段 ...

    /// <summary>
    /// 表的列信息列表（仅表/视图有此字段）
    /// </summary>
    [JsonProperty("columns")]
    public List<ColumnInfo>? Columns { get; set; }
}

public class ColumnInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("dataType")]
    public string DataType { get; set; } = "";

    [JsonProperty("maxLength")]
    public short MaxLength { get; set; }

    [JsonProperty("precision")]
    public byte Precision { get; set; }

    [JsonProperty("scale")]
    public byte Scale { get; set; }

    [JsonProperty("isNullable")]
    public bool IsNullable { get; set; }

    [JsonProperty("isIdentity")]
    public bool IsIdentity { get; set; }

    [JsonProperty("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }
}
```

#### 缓存生成逻辑

```csharp
// 在 FetchAndCacheAsync 中，拉取对象列表后
foreach (var obj in cache.Objects)
{
    if (obj.TypeCode is "U" or "V") // 仅表和视图需要列信息
    {
        obj.Columns = await FetchColumnsAsync(connectionString, obj.Schema, obj.Name);
    }
}
```

#### 表结构脚本生成
根据缓存的列信息，生成 CREATE TABLE 格式脚本：

```sql
CREATE TABLE [dbo].[Users] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [Name] NVARCHAR(100) NOT NULL,
    [Email] NVARCHAR(255) NULL,
    [Age] INT NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
)
```

---

### 4.8 F7：表结构信息弹窗

#### 交互行为

| 操作 | 行为 |
|------|------|
| 单击表/视图名 | 在补全弹窗**旁边**（或原弹窗内）弹出结构信息弹窗，显示 CREATE TABLE 脚本 |
| 双击表/视图名 | 直接展开补全（插入表名），不弹出结构信息 |

#### UI 设计方案

**方案**：在现有补全 Popup 中嵌入展开/收缩机制
- 选中表名后，按某个快捷键（如 Ctrl+Space）或单击，在 ListBox 中展开该表的结构预览
- 或新增一个独立的 Popup，定位在原弹窗右侧

**推荐**：鼠标悬浮或单击时，在原弹窗下方或右侧显示一个 Tooltip 风格的浮层，显示 CREATE TABLE 脚本（等宽字体，语法高亮可选）。

---

### 4.9 F8：表字段补全 + SP/函数参数补全

#### 核心逻辑（场景 A：表字段补全）

**步骤 1：检测表名/别名上下文**

在 `OnCharTyped` 中新增检测分支：当输入 `Tablename.` 或 `Alias.` 模式时，检查光标前是否为已知表名或已声明的别名。

**检测方法：**
1. 获取光标前的单词（`.` 之前的文本）
2. 检查该单词是否为：
   - 当前 DB 中的表名 → 使用该表的列信息
   - FROM/JOIN 子句中声明的别名 → 解析别名到真实表名

**步骤 2：解析别名到真实表名**

```csharp
/// <summary>
/// 解析 SQL 中的别名映射表。
/// 从当前语句中找出 "FROM TableName Alias" 或 "JOIN TableName Alias" 模式。
/// 返回 Dictionary<别名, 表名>
/// </summary>
private Dictionary<string, string> ResolveAliasMap(IWpfTextView textView)
{
    // 获取当前可见行的文本
    // 使用正则匹配 FROM/JOIN pattern:
    // FROM\s+(\w+)\s+(\w+)  -> 表名, 别名
    // JOIN\s+(\w+)\s+(\w+)  -> 表名, 别名
    // 返回 Dictionary { "U" -> "Users", "O" -> "Orders" }
}
```

**步骤 3：字段补全弹窗**

```csharp
// 检测到表名或别名后的处理
private void HandleFieldCompletion(string tableName, string fieldPrefix)
{
    var columns = _cacheService.GetTableColumns(tableName, fieldPrefix);
    if (columns.Count == 0) return;

    var allMatches = columns.Select(c => new CompletionCandidate
    {
        DisplayText = c.Name,
        FilterText = c.Name,
        TypeTag = "字段",
        InsertText = c.Name + " ",
        ItemType = CompletionItemType.Field,
        MatchScore = CompletionCandidate.FuzzyMatch(fieldPrefix, c.Name)
    }).ToList();

    ShowOrUpdatePopup(allMatches, fieldPrefix);
}
```

#### 核心逻辑（场景 B：SP/函数参数补全 — 新增）

**数据来源：SP 参数缓存**

在 `FetchAndCacheAsync` 中，拉取存储过程和函数时，额外查询其参数信息：

```sql
-- 对于每个 SP/函数，获取其参数信息
SELECT
    p.name AS ParameterName,
    TYPE_NAME(p.user_type_id) AS DataType,
    p.max_length,
    p.precision,
    p.scale,
    p.is_output,
    p.has_default_value,
    p.default_value
FROM [DBName].sys.parameters p
WHERE p.object_id = OBJECT_ID('[schema].[procedureName]')
ORDER BY p.parameter_id
```

**缓存结构扩展**

```csharp
public class DatabaseObjectInfo
{
    // ... 现有字段 ...

    /// <summary>
    /// 表的列信息列表（仅表/视图有此字段）
    /// </summary>
    [JsonProperty("columns")]
    public List<ColumnInfo>? Columns { get; set; }

    /// <summary>
    /// SP/函数的参数列表（仅SP/函数有此字段，新增）
    /// </summary>
    [JsonProperty("parameters")]
    public List<ParameterInfo>? Parameters { get; set; }
}

public class ParameterInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("dataType")]
    public string DataType { get; set; } = "";

    [JsonProperty("maxLength")]
    public short MaxLength { get; set; }

    [JsonProperty("isOutput")]
    public bool IsOutput { get; set; }

    [JsonProperty("hasDefault")]
    public bool HasDefault { get; set; }
}
```

**展开时直接补全参数列表（不弹窗）**

当用户在 `ExpandSelectedTemplate` 中选中一个 SP/函数（ItemType = DatabaseObject，TypeCode = "P"/"FN"/"IF"/"TF"）后：

1. 从缓存中查找该 SP/函数的参数列表
2. 若无参数 → 正常展开 `SP名 + 空格`，不做额外操作
3. 若有参数：
   - **SP**：展开为 `SP名 @参数1, @参数2, ...`（带空格，逗号分隔）
   - **函数**：展开为 `函数名(@参数1, @参数2, ...)`（带圆括号，逗号分隔）
4. 光标定位在第一个参数名之后，方便用户直接输入参数值

**展开格式示例**

| SP/函数 | 参数 | 展开结果 |
|---------|------|---------|
| `dbo.GetUserBy` | @Id INT, @Name NVARCHAR(50) | `dbo.GetUserBy @Id, @Name` |
| `dbo.CalcTotal` | @amount DECIMAL(18,2) | `dbo.CalcTotal(@amount)` |
| `dbo.UpdateUser` | @Id INT, @Name NVARCHAR(50), @Status INT = NULL | `dbo.UpdateUser @Id, @Name, @Status` |
| `dbo.GetVersion` | 无参数 | `dbo.GetVersion `（仅SP名+空格） |

**参数补全流程示意（无弹窗）**
```
EXEC dbo.GetUserBy
    | 选中 SP，从缓存读取参数列表
    | 直接展开为 "dbo.GetUserBy @Id, @Name, @Status"
    | 光标定位在 @Id 之后
用户直接在光标处输入参数值: "dbo.GetUserBy 1, @Name, @Status"
    | 用户输入 1 后，手动移动到 @Name 后继续输入
正常编辑
```

#### 字段/参数候选的展示

#### 字段/参数候选的展示

| 类型 | DisplayText | TypeTag | InsertText | 排序 |
|------|-------------|---------|------------|------|
| 字段 | 字段名 | "字段" | 字段名 + 空格 | 精确匹配 > 前缀匹配 > 子序列匹配 |
| SP参数 | @参数名 数据类型 | "参数" | @参数名 + 空格 | 按 parameter_id 顺序，必填优先 |

---

### 4.10 F9：表别名关联字段提示

#### 别名解析规则

```
输入样例                              检测到的别名映射
"FROM Users U WHERE U."               U -> Users
"FROM Orders O WHERE O."              O -> Orders
"FROM Users U JOIN Orders O ON ..."   U -> Users, O -> Orders
"FROM Users AS U WHERE U."            U -> Users（支持 AS 语法）
"FROM [dbo].[Users] U WHERE U."       U -> Users（支持方括号语法）
```

#### 解析方法

使用正则表达式在当前可见行范围内搜索：

```regex
\b(?:FROM|JOIN)\s+\[?(\w+(?:\.\w+)?)\]?(?:\s+AS)?\s+(\w+)\b
```

匹配结果：
- Group 1: 表名（如 "Users"、"dbo.Users"）
- Group 2: 别名（如 "U"）

---

### 4.11 F10：打开脚本自动注释

#### 触发事件
通过 SSMSBoostPlus "打开脚本" 菜单（或工具栏按钮）打开 .sql 文件时触发。

**注意**：不拦截 SSMS 原生的文件打开事件，仅对通过 SSMSBoostPlus 自定义菜单打开的文件生效。

#### 处理流程
1. 用户点击 SSMSBoostPlus "打开脚本" 菜单
2. 弹出文件选择对话框
3. 用户选择 .sql 文件
4. 文件在 SSMS 编辑器中打开
5. **自动执行**：扫描文件内容
6. 对于每一行，如果以 `ALTER` 或 `CREATE` 开头（忽略前导空白符），且该行**未被注释**，则在行首添加 `-- `
7. 更新编辑器内容

#### 注释规则

| 输入行 | 输出行 |
|--------|--------|
| `CREATE PROCEDURE dbo.Test AS` | `-- CREATE PROCEDURE dbo.Test AS` |
| `  ALTER TABLE dbo.Users ADD Col1 INT` | `--   ALTER TABLE dbo.Users ADD Col1 INT` |
| `-- CREATE PROCEDURE dbo.Test AS` | `-- CREATE PROCEDURE dbo.Test AS`（不变） |
| `  -- ALTER TABLE dbo.Users` | `  -- ALTER TABLE dbo.Users`（不变） |
| `SELECT * FROM Users` | `SELECT * FROM Users`（不变） |
| `GO` | `GO`（不变） |

#### 正则匹配规则
```
^\s*(CREATE|ALTER)\b  ->  行首（忽略空白）以 CREATE 或 ALTER 开头
且该行未被注释（非 -- 开头）
```

---

## 5. 数据埋点与指标

| 事件 | 埋点时机 | 数据内容 |
|------|----------|---------|
| schema_completion_shown | Schema 补全弹窗显示 | dbName, schemaCount |
| field_completion_shown | 字段补全弹窗显示 | tableName, aliasUsed(是/否) |
| sp_param_expanded | SP参数直接补全到编辑器 | spName, paramCount |
| table_structure_clicked | 单击查看表结构 | tableName |
| auto_comment_applied | 自动注释执行 | lineCount, scriptPath |
| esc_closed_popup | ESC 关闭弹窗 | — |

---

## 6. 非功能需求

| 类别 | 要求 |
|------|------|
| 性能 | 表结构缓存加载不阻塞 UI（后台异步），单个 DB 的列信息查询 <= 500ms |
| 性能 | 字段补全弹窗响应时间 <= 100ms（从输入字符到弹窗显示） |
| 性能 | SP参数查询 <= 200ms（后台异步缓存） |
| 存储 | 表结构缓存持久化到 `db_object_cache.json`，单个 DB 的列信息增量 <= 100KB |
| 兼容性 | 支持 SSMS 18/19/20/22 |
| 兼容性 | 支持 Azure SQL Database（sys.schemas/sys.columns/sys.parameters 查询兼容） |
| 安全性 | 不修改用户脚本文件，仅在编辑器中插入注释（回退可撤销） |

---

## 7. 验收标准

### 7.1 核心功能验收清单

| ID | 验收项 | 通过条件 |
|----|--------|---------|
| AC-01 | 选中文本 + 输入字符 | 选中内容被删除，字符被插入 |
| AC-02 | 选中文本 + Enter 补全 | 选中内容被删除，补全内容插入 |
| AC-03 | ESC 关闭弹窗 | 按下 ESC 后弹窗关闭，编辑器无异常 |
| AC-04 | 小数点触发跨库补全 | 输入 `DBName.` 后弹出该 DB 的 Schema 列表 |
| AC-05 | DB 名称去重 | 输入完整 DB 名后，候选不重复显示该 DB 名称 |
| AC-06 | Schema 补全（FROM场景） | 输入 `DBName.` 后显示所有 Schema，选中后继续补全对象 |
| AC-07 | Schema 跳过（EXEC场景） | 输入 `EXEC DBName.` 后直接显示 SP/函数，跳过 Schema |
| AC-08 | 表结构缓存 | 缓存文件包含列信息，启动加载后缓存就绪 |
| AC-09 | 结构信息弹窗 | 单击表名弹出 CREATE TABLE 脚本 |
| AC-10 | 表字段补全 | 输入 `TableA.` 后弹出字段列表 |
| AC-11 | 别名字段补全 | 别名 `A.` 关联到真实表 `TableA` 的字段 |
| AC-12 | SP参数补全 | 选中SP后直接补全参数列表到编辑器中，不弹窗，光标定位在首参后 |
| AC-13 | 自动注释 | 通过自定义菜单打开脚本，ALTER/CREATE 行被自动注释 |

### 7.2 异常场景验收清单

| ID | 验收项 | 通过条件 |
|----|--------|---------|
| EC-01 | 表结构缓存加载失败 | 不阻塞 UI，字段补全回退到仅关键词/模板补全 |
| EC-02 | sys.schemas 查询失败 | Schema 补全使用默认列表（dbo, sys, guest, INFORMATION_SCHEMA） |
| EC-03 | 别名解析失败（复杂 SQL） | 不显示字段补全，回退到正常补全逻辑 |
| EC-04 | 打开非 SQL 文件 | 不执行自动注释 |
| EC-05 | 文件已有注释的 ALTER/CREATE | 不重复注释 |
| EC-06 | SP参数缓存加载失败 | SP正常补全但不弹出参数提示 |

### 7.3 性能指标验收

| 指标 | 目标值 |
|------|--------|
| 字段补全弹窗响应时间 | <= 100ms |
| 表结构缓存加载（10 个 DB） | <= 30s（后台异步） |
| 缓存文件大小 | <= 200MB（50 个 DB） |

---

## 8. 上线计划建议

### 8.1 灰度策略

| 阶段 | 范围 | 观察期 | 回滚条件 |
|------|------|--------|---------|
| Phase 1 | Bug 修复（F1-F4，影响面小） | 1 天 | 无严重回归 bug |
| Phase 2 | Schema 补全 + DB 去重（F5） | 2 天 | 补全命中率下降 > 10% |
| Phase 3 | 表结构缓存 + 字段补全 + SP参数（F6-F9） | 3 天 | 内存占用 > 500MB 或卡顿 |
| Phase 4 | 自动注释（F10） | 2 天 | 用户反馈误注释 > 5% |

### 8.2 上线后观察指标

| 指标 | 目标 | 告警阈值 |
|------|------|---------|
| Schema 补全触发次数/天 | > 用户数的 10% | < 5% |
| 字段补全触发次数/天 | > 用户数的 20% | < 10% |
| SP参数直接补全次数/天 | > 用户数的 10% | < 5% |
| 自动注释覆盖率 | 100% 的打开脚本 | < 90% |
| 新增异常日志 | < 10 条/天 | > 50 条/天 |

---

## 9. 产品风险标注与建议

| 风险 | 等级 | 说明 | 缓解措施 |
|------|------|------|---------|
| 表结构缓存体积过大 | 中 | 50 个 DB x 100 张表 x 20 列可能导致缓存文件过大 | 压缩持久化；按需懒加载（仅加载当前 DB 的完整列信息） |
| 别名解析不够准确 | 中 | 复杂 SQL（子查询、CTE）中别名解析可能失败 | 使用正则匹配简化版解析，复杂 SQL 降级为不显示字段补全 |
| 自动注释误伤 | 低 | 用户可能希望保留某些 ALTER/CREATE 语句不注释 | 提供撤销（Ctrl+Z）恢复；后续可加白名单配置 |
| Schema 查询权限 | 低 | 某些用户可能无权访问 sys.schemas | 查询失败时使用默认 Schema 列表（dbo, sys, guest）兜底 |
| SP参数直接补全可能参数过多 | 低 | SP参数过多（>5个）时一次性补全可能占满编辑行 | 后续可考虑参数过多时只补必填参数，或加配置开关 |
