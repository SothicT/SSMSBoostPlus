---
name: ai-suggest
description: "SSMSBoostPlus AI 模板 — SQL 代码建议。对用户选中的 SQL 代码进行安全审查、语法检查、代码精简、性能优化建议。触发：代码审查、优化建议、审查SQL、suggest、检查代码"
---

# 01 — SQL 代码建议 Skill（suggest.md）

## 概述

对应 Prompt 模板 `src/AI/Prompts/Templates/suggest.md`，功能是对用户选中的 T-SQL 代码进行全面审查，按四个优先级维度给出改进建议。

## 触发条件

当用户提出以下意图时，CodeBuddy 应识别并引导用户使用此功能：

| 场景 | 示例表述 |
|------|---------|
| 代码审查 | "审查这段 SQL"、"帮我检查 SQL 代码"、"review this SQL" |
| 优化建议 | "优化这段 SQL"、"有没有更好的写法"、"suggest improvements" |
| 安全检查 | "检查 SQL 注入风险"、"安全检查这段代码" |
| 通用审查 | "分析这段代码有什么问题"、"帮我看看这段 SQL" |

> **实际使用方式**：用户在 SSMS 编辑器中选中 SQL 代码后，点击 AI 对话面板的「💡 建议」快捷按钮，或通过快捷键 `Ctrl+Shift+S` 触发。

## 前置条件

| 条件 | 说明 |
|------|------|
| 选中 SQL 代码 | 编辑器中有活动的 SQL 文档，且选中了代码片段 |
| AI 已配置 | 已通过 SSMSBoost++ → ⚙ AI 配置 完成设置 |
| 数据库连接（可选） | 有活动连接可增强上下文准确性 |

## 上下文采集

| 字段 | 采集方式 | 说明 |
|------|---------|------|
| **Code** | `editorService.GetCodeAsync()` | 用户选中的 SQL 代码 |
| **DatabaseName** | `editorService.GetActiveDatabaseName()` | 当前数据库名，追加到 Prompt 前缀 `[数据库: xxx]` |
| **SchemaInfo** | `_databaseService.GetTableSchemaAsync()` | 从 SQL 中提取表名后查询表结构（列名、类型、约束） |
| **IndexInfo** | `_databaseService.GetTableIndexesAsync()` | 从 SQL 中提取表名后查询索引信息 |

> **注意**：SchemaInfo 和 IndexInfo 是此 Skill **建议扩展**的上下文。当前实现中（2026-06-18）Suggest 模板**尚未**采集 Schema/Index，仅传递 DatabaseName。如需增强审查准确性，应在调用方（如 `AIChatControl.ExecuteQuickAction`）中补充采集逻辑。

## 核心角色指令（内嵌）

AI 被赋予"SQL 代码审查专家"角色，按以下四个优先级维度进行审查：

```
P0 — 安全风险（最紧急）
  - SQL 注入漏洞（动态拼接 SQL、未参数化查询）
  - 敏感数据泄露（未脱敏的密码、身份证、手机号等）
  - 权限过度（GRANT 权限范围过大）
  - 死锁风险（事务中表访问顺序不一致）
  - 事务范围过大（BEGIN TRAN 后未及时 COMMIT）

P1 — 语法错误
  - 关键字拼写错误、括号不匹配、数据类型不匹配
  - 对象名解析歧义（缺少 Schema 前缀）

P2 — 代码精简
  - 冗余子查询可改为 JOIN
  - 重复条件合并、CTE 替代嵌套子查询
  - 可简化的 CASE 表达式、未使用的变量

P3 — 性能优化
  - 全表扫描风险、缺失索引建议
  - 隐式类型转换、参数嗅探
  - SELECT * 使用警告
```

> 完整的 Prompt 规则细节见 `src/AI/Prompts/Templates/suggest.md`

## 输出规范

- 格式：Markdown
- 按 P0→P1→P2→P3 优先级降序排列
- 每个问题附带**问题描述 | 代码位置 → 修正建议**和示例代码
- 无问题标注 ✅
- 输出语言：中文

## 调用链路

```
用户选中代码 → 点击「💡 建议」按钮
  → AIChatControl.QuickSuggest_Click()
    → ExecuteQuickAction("建议", PromptTemplate.Suggest, ...)
      → CodeAnalysisService.GetSuggestionsAsync(code, databaseName)
        → BuildRequest(PromptTemplate.Suggest, code)
        → bridge.ExecuteAsync(request)
        → AIApiClientBridge / CodebuddyCliBridge
```

## 代码位置

| 组件 | 文件 | 关键方法 |
|------|------|---------|
| 快捷按钮 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `QuickSuggest_Click()` (L472) |
| 执行模板 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `ExecuteQuickAction()` (L514) |
| 分析服务 | `src/AI/Services/CodeAnalysisService.cs` | `GetSuggestionsAsync()` (L67) |
| Prompt 构建 | `src/AI/Prompts/PromptTemplateManager.cs` | `BuildSuggestPrompt()` (L193) |
| Bridge 路由 | `src/AI/Bridge/BridgeFactory.cs` | `CreateBestAvailableBridgeAsync()` |

## 注意事项

1. **ReadOnly 模式**：AI 仅输出文本建议，不修改文件
2. **Schema/Index 增强**：当前 Suggest 模板未采集 SchemaInfo/IndexInfo，如需增强需修改 `AIChatControl.ExecuteQuickAction` 中的调用逻辑
3. **表名提取限制**：最多处理 SQL 中前 10 张表，避免 Prompt 过长
4. **无问题场景**：必须标注 ✅ 防止用户误解为 AI 未响应
5. **Token 消耗**：Suggest 是 4 维度审查，Prompt 较长，注意 API Token 配额