---
title: AI 提示词模板 — Skills 参考文档索引
date: 2026-06-18
author: AI Assistant
tags: [AI, Prompt, Skills, SSMSBoostPlus]
---

# AI 提示词模板 — Skills 参考文档

## 概述

本文档集为 SSMSBoostPlus 的 5 个 AI 提示词模板分别生成了对应的 CodeBuddy Skill 参考文档，旨在帮助 **CodeBuddy AI Agent** 理解这些模板的用途、触发条件、上下文采集方式和调用链路，从而在用户提出相关需求时进行准确引导。

## 模板对照表

| # | 模板文件 | Skill 文档 | 触发场景 | 上下文信息 |
|---|---------|-----------|---------|-----------|
| 1 | `suggest.md` | [01-Suggest-SQL代码建议Skill](./01-Suggest-SQL代码建议Skill.md) | SQL 代码审查、优化建议 | DatabaseName + SchemaInfo + IndexInfo |
| 2 | `comment.md` | [02-Comment-SQL注释生成Skill](./02-Comment-SQL注释生成Skill.md) | 为 SQL 添加注释 | DatabaseName |
| 3 | `format.md` | [03-Format-SQL格式化Skill](./03-Format-SQL格式化Skill.md) | SQL 格式化 | DatabaseName + FormatStyle |
| 4 | `analyze.md` | [04-Analyze-SQL性能分析Skill](./04-Analyze-SQL性能分析Skill.md) | 性能分析、执行计划解读 | DatabaseName + SchemaInfo + IndexInfo + ExecutionPlan |
| 5 | `chat.md` | [05-Chat-SQL自由对话Skill](./05-Chat-SQL自由对话Skill.md) | 自由问答、数据库技术咨询 | DatabaseName |

## 架构依赖关系

```
Skill 文档（AIDoc/）
    ↓ 指导理解
CodeBuddy AI Agent
    ↓ 引导用户
SSMSBoostPlus UI 层（Command / ChatControl）
    ↓ 调用
CodeAnalysisService（src/AI/Services/CodeAnalysisService.cs）
    ↓ 委托
ICodebuddyBridge（CLI / HTTP API）
    ↓ 发送
PromptTemplate + 上下文 → AI 后端
```

## 上下文信息获取来源

| 上下文字段 | 获取方法 | 来源类 |
|-----------|---------|-------|
| DatabaseName | `editorService.GetActiveDatabaseName()` | `SsmsEditorService` |
| SchemaInfo | `_databaseService.GetTableSchemaAsync()` | `IDatabaseService` |
| IndexInfo | `_databaseService.GetTableIndexesAsync()` | `IDatabaseService` |
| ExecutionPlan | `_databaseService.GetExecutionPlanAsync()` | `IDatabaseService` |
| FormatStyle | `config.DefaultFormattingStyle` | `AIConfiguration` |

## 相关代码位置

| 组件 | 路径 |
|------|------|
| Prompt 模板文件 | `src/AI/Prompts/Templates/{suggest,comment,format,analyze,chat}.md` |
| PromptTemplateManager | `src/AI/Prompts/PromptTemplateManager.cs` |
| CodeAnalysisService | `src/AI/Services/CodeAnalysisService.cs` |
| AIApiClientBridge | `src/AI/Bridge/AIApiClientBridge.cs` |
| ContextProvider | `src/AI/Context/ContextProvider.cs` |
| AIRequest / AIContext | `src/AI/Models/AIRequest.cs` / `src/AI/Models/AIContext.cs` |
| AIChatControl（UI 端） | `src/SSMS/UI/Chat/AIChatControl.xaml.cs` |
| AIPerformanceCommand | `src/SSMS/Commands/AIPerformanceCommand.cs` |

## 构建与部署

- 编译命令：`dotnet build SSMSBoostPlus.slnx -c Release`
- 测试门禁：`dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --no-build`