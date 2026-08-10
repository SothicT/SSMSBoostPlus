---
description: 
alwaysApply: true
enabled: true
updatedAt: 2026-06-09T09:37:37.623Z
provider: 
---

# 测试规则

## 测试门禁（Gate）

**提交前必须通过**：

```powershell
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --no-build
```

如测试未通过，**禁止提交**。

## 测试技术栈

| 组件 | 版本 | 用途 |
|------|------|------|
| xUnit | 2.4.2 | 测试框架 |
| Moq | 4.18.4 | Mock 框架 |
| FluentAssertions | 6.11.0 | 断言库 |

## 当前测试状态

| 指标 | 数值 |
|------|------|
| 用例总数 | 103 |
| 当前状态 | 全部通过 ✅ |
| 覆盖率报告 | `tests/coverage/html/index.html` |

## 测试项目结构

```
tests/
├── SSMSBoostPlus.Tests/
│   ├── SSMSBoostPlus.Tests.csproj
│   ├── Services/
│   ├── Bridges/
│   └── ...
└── coverage/
    └── html/
        └── index.html  # 覆盖率报告
```

## 运行测试

```powershell
# 运行所有测试
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release

# 带覆盖率
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --collect:"XPlat Code Coverage"

# 仅运行特定测试
dotnet test tests/SSMSBoostPlus.Tests/ -c Release --filter "FullyQualifiedName~ServiceTests"
```

## 新增测试要求

| 场景 | 要求 |
|------|------|
| 新增 public 方法 | 必须有对应的单元测试 |
| 修改现有逻辑 | 必须更新/新增测试用例 |
| Bug 修复 | 必须添加 Regression Test |

## 测试命名规范

```csharp
[Fact]
public void GetTablesAsync_WhenDatabaseExists_ReturnsTableList()
{
    // Arrange: 设置测试数据和 Mock
    // Act: 执行被测方法
    // Assert: 验证结果
}
```

格式：`[方法名]_[场景]_[预期结果]`