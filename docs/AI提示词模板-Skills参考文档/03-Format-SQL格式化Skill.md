---
name: ai-format
description: "SSMSBoostPlus AI 模板 — SQL 格式化。按 Microsoft 或 Google 风格格式化 T-SQL 代码。触发：格式化、format、整理SQL、美化SQL、排版SQL"
---

# 03 — SQL 格式化 Skill（format.md）

## 概述

对应 Prompt 模板 `src/AI/Prompts/Templates/format.md`，功能是将用户提供的 T-SQL 代码按指定风格（Microsoft / Google）格式化为规范、易读的形式。**不改变 SQL 的逻辑语义**，仅调整缩进、换行、大小写和排版。

## 触发条件

当用户提出以下意图时，CodeBuddy 应识别并引导用户使用此功能：

| 场景 | 示例表述 |
|------|---------|
| 格式化 | "格式化这段 SQL"、"整理 SQL 格式"、"format this SQL" |
| 美化排版 | "美化 SQL"、"排版一下"、"让 SQL 更好看" |
| 风格切换 | "改成 Google 风格"、"用 Microsoft 风格格式化" |

> **实际使用方式**：用户在 SSMS 编辑器中选中 SQL 代码后，点击 AI 对话面板的「🎨 格式化」快捷按钮触发。默认使用 Microsoft 风格，用户可在 AI 配置中修改默认风格。

## 前置条件

| 条件 | 说明 |
|------|------|
| 选中 SQL 代码 | 编辑器中有活动的 SQL 文档，且选中了代码片段 |
| AI 已配置 | 已通过 SSMSBoost++ → ⚙ AI 配置 完成设置 |

## 上下文采集

| 字段 | 采集方式 | 说明 |
|------|---------|------|
| **Code** | `editorService.GetCodeAsync()` | 用户选中的 SQL 代码 |
| **DatabaseName** | `editorService.GetActiveDatabaseName()` | 当前数据库名，追加到 Prompt 前缀 |
| **FormatStyle** | `config.DefaultFormattingStyle` | 格式化风格（Microsoft / Google），默认 Microsoft |

> **说明**：Format 模板不需要 Schema/Index/执行计划，格式化规则完全基于语法结构。

## 核心角色指令（内嵌）

AI 被赋予"SQL 格式化专家"角色，支持两种风格：

### Microsoft 风格（默认）

```sql
SELECT
    t1.Column1,
    t1.Column2,
    t2.Column3
FROM Table1 AS t1
    INNER JOIN Table2 AS t2
        ON t1.Id = t2.Table1Id
WHERE t1.Status = 1
    AND t2.Type = 'A'
ORDER BY t1.Column1;
```

| 规则 | 值 |
|------|-----|
| 关键字大小写 | 大写（SELECT, FROM, WHERE） |
| 缩进 | 4 空格 |
| 逗号位置 | 行尾 |
| 子查询对齐 | 缩进对齐 |

### Google 风格

```sql
select
  t1.column1
  , t1.column2
  , t2.column3
from table1 as t1
  inner join table2 as t2
    on t1.id = t2.table1_id
where t1.status = 1
  and t2.type = 'a'
order by t1.column1;
```

| 规则 | 值 |
|------|-----|
| 关键字大小写 | 小写（select, from, where） |
| 缩进 | 2 空格 |
| 逗号位置 | 行首 |
| 子查询对齐 | 缩进对齐 |

### 通用格式化规则

1. 每个 SQL 子句独占一行
2. 列名、表名适当换行
3. 条件组合使用缩进
4. 保持 SQL 的可执行性
5. 不改变 SQL 的逻辑语义
6. 字符串内容和注释保持原样
7. Schema 前缀始终保留（不省略 `dbo.`）
8. `AS` 关键字统一保留

### 特殊结构处理

| 结构 | 规则 |
|------|------|
| CASE 表达式 | WHEN...THEN 对齐缩进，ELSE 与 END 对齐 |
| CTE 表达式 | WITH 后换行，每个 CTE 独占一段 |
| UNION / UNION ALL | 前后各空一行 |
| 窗口函数 | OVER 中的 PARTITION BY 和 ORDER BY 换行对齐 |
| 长 IN 列表 | 每个值独占一行，逗号放在行尾 |

> 完整的 Prompt 规则细节见 `src/AI/Prompts/Templates/format.md`

## 输出规范

- 输出格式化后的**完整 SQL 代码**
- 仅输出代码，无需额外解释文字
- 保持 SQL 的可执行性

## 调用链路

```
用户选中代码 → 点击「🎨 格式化」按钮
  → AIChatControl.QuickFormat_Click()
    → ExecuteQuickAction("格式化", PromptTemplate.Format, ...)
      → CodeAnalysisService.FormatCodeAsync(code, style, databaseName)
        → 从配置获取 effectiveStyle（默认 Microsoft）
        → BuildRequest(PromptTemplate.Format, code)
        → request.FormatStyle = effectiveStyle
        → bridge.ExecuteAsync(request)
        → AIApiClientBridge / CodebuddyCliBridge
        → 从响应中提取 SQL 代码块（去除 Markdown 包裹）
```

## 代码位置

| 组件 | 文件 | 关键方法 |
|------|------|---------|
| 快捷按钮 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `QuickFormat_Click()` (L488) |
| 执行模板 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `ExecuteQuickAction()` (L514) |
| 分析服务 | `src/AI/Services/CodeAnalysisService.cs` | `FormatCodeAsync()` (L96) |
| Prompt 构建 | `src/AI/Prompts/PromptTemplateManager.cs` | `BuildFormatPrompt()` (L209) |
| SQL 提取 | `src/AI/Services/CodeAnalysisService.cs` | `ExtractSqlFromResponse()` (L252) |

## 注意事项

1. **格式可执行性**：格式化后的 SQL 必须能直接在 SQL Server 中运行
2. **逻辑零改动**：不能改变 SQL 的逻辑语义——包括关键字、表名、列名、字符串内容等
3. **风格一致性**：同一段代码内不允许混用两种风格
4. **注释保护**：SQL 中的注释内容保持原样，不能修改
5. **Diff 确认**：格式化后代码与原始代码差异较明显时，建议通过 Diff 对话框让用户确认
6. **Template 传递风格**：当前 PromptTemplateManager 通过 `AppendDatabaseContext` 和 `AIContext.FormatStyle` 传递风格信息，无需单独指定
7. **Token 效率**：Format 是所有模板中 Token 消耗最低的，适合频繁使用