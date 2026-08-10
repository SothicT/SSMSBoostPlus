---
name: ai-chat
description: "SSMSBoostPlus AI 模板 — SQL 自由对话。AI 对话面板的自由聊天模式，通用数据库技术问答。触发：聊天、提问、对话、chat、咨询、数据库问题"
---

# 05 — SQL 自由对话 Skill（chat.md）

## 概述

对应 Prompt 模板 `src/AI/Prompts/Templates/chat.md`，是 AI 对话面板的默认功能——自由聊天模式。AI 以"数据库技术专家"角色回答用户关于 SQL Server / T-SQL / MySQL / PostgreSQL 的各种技术问题。

与其它模板不同，Chat **不限定输出格式**，完全根据用户问题灵活回答。

## 触发条件

当用户提出以下意图时，CodeBuddy 应识别并引导用户使用此功能：

| 场景 | 示例表述 |
|------|---------|
| 技术问答 | "什么是锁升级？"、"解释一下事务隔离级别" |
| SQL 编写 | "怎么写一个分页查询？"、"帮我写个存储过程" |
| 错误排查 | "这个错误什么意思？"、"ORA-xxxx 是什么" |
| 概念学习 | "讲一下索引的工作原理"、"B+树和哈希索引的区别" |
| 数据库对比 | "SQL Server 和 MySQL 的区别"、"迁移兼容性" |
| 代码分析 | "这段代码有什么问题？"、"帮我看看这个 SQL" |

> **实际使用方式**：通过快捷键 `Ctrl+Shift+K` 打开 AI 对话面板，直接在输入框中输入问题后按 `Ctrl+Enter` 发送。如果编辑器中选中了 SQL 代码，会自动附加上下文。

## 前置条件

| 条件 | 说明 |
|------|------|
| AI 已配置 | 已通过 SSMSBoost++ → ⚙ AI 配置 完成设置 |
| 编辑器代码（可选） | 如果选中了 SQL 代码，会自动附加到问题中作为上下文 |

## 上下文采集

| 字段 | 采集方式 | 说明 |
|------|---------|------|
| **User Text** | `InputBox.Text` | 用户在输入框中输入的问题文本 |
| **Code**（可选） | `editorService.GetCodeAsync()` | 如果编辑器中选中了 SQL 代码，自动附加到问题前 |
| **DatabaseName** | `editorService.GetActiveDatabaseName()` | 当前数据库名（通过 `AppendDatabaseContext` 追加到 Prompt 前缀） |

> **说明**：Chat 模板是自由对话模式，上下文仅用于增强 AI 对当前环境的感知，不约束回答方向。

### 代码附加逻辑

当编辑器中选中了 SQL 代码时，用户问题被构造成：

```
当前编辑器中的 SQL 代码:
```sql
{code}
```

用户问题: {userText}
```

这使得 AI 可以基于编辑器中的代码回答问题。

## 核心角色指令（内嵌）

AI 被赋予"数据库技术专家"角色：

### 角色定位

- 精通 **SQL Server / T-SQL**
- 同时深入了解 **MySQL、PostgreSQL** 等主流数据库
- 擅长：T-SQL/SQL 查询编写与优化、SQL Server 性能优化、数据库架构设计、跨数据库迁移和兼容性分析

### 能力范围

- 回答 SQL Server / T-SQL 相关技术问题
- 解释执行计划、锁机制、事务隔离级别等概念
- 提供 SQL 代码示例和最佳实践
- 当用户提到 MySQL、PostgreSQL 等问题时，给出兼容性建议
- 当问题明显超出数据库技术范围时，友好地提示用户

### 约束

- 当用户提供了 SQL 代码时，优先分析代码中的问题
- 当用户问题不明确时，主动追问澄清
- 回答保持简洁，重点突出
- 复杂的问题分点作答，便于阅读

> 完整的 Prompt 规则细节见 `src/AI/Prompts/Templates/chat.md`

## 输出规范

- 格式：Markdown（灵活自由，无固定模板结构）
- 代码块使用 ```sql 或 ``` 标记
- 禁止使用 HTML 标签
- 涉及数据库差异时标注版本信息（如 SQL Server 2019+）
- 输出语言：中文

## 操作按钮（自动生成）

AI 回答中的 SQL 代码块会自动生成操作按钮：

| 按钮 | 触发条件 | 行为 |
|------|---------|------|
| **📋 应用此修改** | 代码块以 SELECT/WITH/CREATE/ALTER/DROP/INSERT/UPDATE/DELETE/EXEC/MERGE 开头 | 弹出 Diff 对话框，用户确认后替换编辑器选中文本 |
| **📝 复制代码** | 其他非可执行 SQL（如 EXPLAIN 输出、配置示例等） | 复制代码到剪贴板 |

最多生成 3 个按钮。

## 调用链路

```
用户在输入框中输入问题 → Ctrl+Enter 发送
  → AIChatControl.SendMessage()
    → 获取编辑器选中代码（如有）
    → 构建 userPrompt（代码 + 用户问题）
    → 构建 AIRequest(Template=Chat, Code=userPrompt, ReadOnly=true)
    → bridge.ExecuteAsync(request)
    → AIApiClientBridge / CodebuddyCliBridge
    → 解析响应 → 提取代码块 → 生成操作按钮
    → 添加 AI 消息到会话列表
```

## 代码位置

| 组件 | 文件 | 关键方法 |
|------|------|---------|
| 发送消息 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `SendMessage()` (L243) |
| 操作按钮生成 | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` | `ExtractCodeActions()` (L375) |
| Prompt 构建（OpenAI） | `src/AI/Bridge/AIApiClientBridge.cs` | `GetUserPromptForTemplate()` (L618), `GetSystemPromptForTemplate()` (L519) |
| Prompt 构建（内置 API） | `src/AI/Bridge/AIApiClientBridge.cs` | `BuildBuiltInUserPrompt()` (L485) |
| Prompt 构建（Manager） | `src/AI/Prompts/PromptTemplateManager.cs` | `BuildChatPrompt()` (L219) |

## 注意事项

1. **上下文持久性**：Chat 模板支持多轮对话（通过 `IChatSessionService`），AI 可引用历史消息中的上下文。但注意**内置默认 SSE API 是无状态的**，每次请求独立发送全部上下文

2. **代码选中 vs 未选中**：
   - 选中代码时：AI 优先分析代码中的问题，回答更具针对性
   - 未选中代码时：AI 根据用户问题自由回答，可能不涉及具体代码

3. **多数据库兼容**：AI 默认以 SQL Server 为主，但用户可主动提及 MySQL/PostgreSQL 等，AI 会给出兼容性建议

4. **超出范围的问题**：当问题明显超出数据库技术范围时（如前端开发、操作系统配置），AI 应友好提示用户

5. **回答长度**：内置 SSE API 对长回答有截断风险，复杂问题建议分次提问

6. **取消机制**：用户可通过点击取消按钮中断正在生成的回答（通过 `CancellationToken`）

7. **会话管理**：点击「🗑 清空对话」按钮可清除当前会话历史，开始新的对话