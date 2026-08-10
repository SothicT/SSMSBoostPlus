---
name: ai-comment
description: "SSMSBoostPlus AI 模板 — SQL 注释生成。自动为 T-SQL 代码添加清晰、分层的中文注释。触发：添加注释、生成注释、comment、注释SQL、加注释"
---

# 02 — SQL 注释生成 Skill（comment.md）

## 概述

对应 Prompt 模板 `src/AI/Prompts/Templates/comment.md`，功能是为用户提供的 T-SQL 代码按层级和密度规则自动生成中文注释。**不改变原始 SQL 的逻辑和格式**，仅添加注释。

## 触发条件

当用户提出以下意图时，CodeBuddy 应识别并引导用户使用此功能：

| 场景 | 示例表述 |
|------|---------|
| 添加注释 | "给这段 SQL 加注释"、"添加注释"、"annotate this SQL" |
| 生成注释 | "生成中文注释"、"帮我注释一下"、"comment this code" |
| 文档化 | "给存储过程写注释"、"文档化这段代码" |

> **实际使用方式**：用户在 SSMS 编辑器中选中 SQL 代码后，点击 AI 对话面板的「📝 注释」快捷按钮触发。

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

> **说明**：Comment 模板不需要 Schema/Index/执行计划等信息，注释规则完全基于代码本身的结构分析。

## 核心角色指令（内嵌）

AI 被赋予"SQL 注释生成专家"角色，按以下层级和密度规则生成注释：

### 注释密度控制

| 代码复杂度 | 注释密度 | 示例 |
|-----------|---------|------|
| **简单**（单表 SELECT、简单 WHERE） | 少量关键注释 | `-- 查询活跃客户` |
| **中等**（多表 JOIN、子查询） | 每个主要块加注释 | 每个 JOIN / 子查询说明 |
| **复杂**（多层 CTE、CASE、窗口函数） | 详细注释 | 每段逻辑说明业务含义 |

### 注释层级

1. **对象级注释**（存储过程/函数/视图头部）— 功能、参数、返回值
2. **语句级注释** — SELECT/INSERT/UPDATE/DELETE/MERGE 的业务目的
3. **结构注释** — JOIN 关联目的、CTE 用途、WHERE 条件业务含义
4. **异常处理注释** — TRY...CATCH 策略说明

### 注释原则

- 解释**"为什么"**而非**"是什么"**
- 不过度注释：简单代码少量注释，复杂代码详细注释
- 全部使用中文
- 块注释 `/* */` 用于对象头部，行内注释 `--` 用于语句/条件
- 注释放在被注释代码的上方

> 完整的 Prompt 规则细节见 `src/AI/Prompts/Templates/comment.md`

## 输出规范

- 输出带注释的**完整 SQL 代码**（保持可执行性）
- 不改变原始 SQL 的逻辑和格式
- 仅输出代码，无需额外解释文字

## 调用链路

```
用户选中代码 → 点击「📝 注释」按钮
  → AIChatControl.QuickComment_Click()
    → ExecuteQuickAction("注释", PromptTemplate.Comment, ...)
      → CodeAnalysisService.AddCommentsAsync(code, databaseName)
        → BuildRequest(PromptTemplate.Comment, code)
        → bridge.ExecuteAsync(request)
        → AIApiClientBridge / CodebuddyCliBridge
        → 从响应中提取 SQL 代码块（去除 Markdown 包裹）
```

## 代码位置

| 组件 | 文件 | 关键方法 |
|------|------|---------|
| 快捷按钮 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `QuickComment_Click()` (L480) |
| 执行模板 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `ExecuteQuickAction()` (L514) |
| 分析服务 | `src/AI/Services/CodeAnalysisService.cs` | `AddCommentsAsync()` (L82) |
| Prompt 构建 | `src/AI/Prompts/PromptTemplateManager.cs` | `BuildCommentPrompt()` (L201) |
| SQL 提取 | `src/AI/Services/CodeAnalysisService.cs` | `ExtractSqlFromResponse()` (L252) |

## 注意事项

1. **可执行性保证**：注释绝对不能影响 SQL 运行，`--` 单行注释和 `/* */` 块注释不会改变执行逻辑
2. **不改变原始代码**：仅添加注释，不能修改关键字、格式、逻辑
3. **差异过大时**：如果 AI 响应中提取的代码与原始代码差异显著，应通过 Diff 对话框让用户确认后再应用
4. **注释密度**：AI 可能过度注释简单代码，用户可手动删减多余的注释
5. **存储过程头部**：对于 `CREATE PROCEDURE` / `FUNCTION` / `VIEW`，应确保头部注释块包含完整的参数说明