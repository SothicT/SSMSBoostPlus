---
description: 
alwaysApply: true
enabled: true
updatedAt: 2026-06-09T09:37:03.858Z
provider: 
---

# 架构约束规则

## 依赖链（单向，禁止逆流）

```
Shared (netstandard2.0)
    ↓
Core (netstandard2.0)
    ↓
AI (netstandard2.0)
    ↓
SSMS (net472)
```

**规则**：
- 严禁 Core 引用 AI
- 严禁 AI 引用 SSMS
- 严禁 SSMS 引用 Core/AI 的具体实现
- 避免循环依赖，保持单向依赖

## 模块职责边界

| 模块 | 职责 | 不应包含 |
|------|------|----------|
| Shared | 公共类型、工具、接口定义 | 业务逻辑、SSMS 特定代码 |
| Core | 核心业务逻辑、数据库操作 | AI 调用、UI 代码 |
| AI | AI 服务封装、Prompt 管理 | UI 代码、SSMS 特定逻辑 |
| SSMS | SSMS 集成、菜单命令、UI | 核心业务逻辑 |

## CodeAnalysisService 位置

`CodeAnalysisService` 必须位于 **AI 子项目**，而非 Core。

原因：避免 Core ↔ AI 循环依赖。AI 层依赖 Core，Core 若再依赖 AI 则形成循环。

## BridgeFactory 路由策略

自动选择通信桥接：

```
BridgeFactory.Create()
    ├── CLI 可用 → CodebuddyCliBridge（本地优先）
    └── CLI 不可用 → AIApiClientBridge（API 兜底）
```

## 接口化原则

新增功能时，若需要可测试性，**先提取接口**：

```csharp
// ✅ 正确：先定义接口
public interface IDatabaseService
{
    Task<IEnumerable<string>> GetTablesAsync();
}

public class DatabaseService : IDatabaseService { ... }

// ❌ 错误：直接使用具体实现
public class DatabaseService
{
    public async Task<IEnumerable<string>> GetTablesAsync() { ... }
}
```

**适用场景**：
- 需要单元测试的功能
- 未来可能替换实现的模块
- 需要依赖注入的服务

## Prompt 模板策略

采用双策略保证 Prompt 模板可用性：

1. **嵌入 Markdown 资源**：作为主策略，便于维护
2. **硬编码降级**：作为兜底，防止资源缺失

```csharp
// Primary: Load from embedded resource
var template = LoadFromResource("Prompts.analyze.md");

// Fallback: Hard-coded default
if (string.IsNullOrEmpty(template))
    template = "You are a SQL analysis assistant...";
```