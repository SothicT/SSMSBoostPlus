# 断言失败

## 匹配特征

- 失败模式: `Assert.Equal()` / `Assert.True()` / `Assert.NotNull()` / `Assert.Throws<T>()`
- 消息关键词: `Assert.*() Failure`

## 常见根因

1. **Mock 设置遗漏**: Moq 的 `Setup()` 未覆盖测试路径中调用的方法，返回默认值（null/0/false）
2. **Mock 参数不匹配**: `It.Is<T>()` 条件过严，实际调用时参数不满足匹配条件
3. **测试数据错误**: 硬编码的期望值与实际业务逻辑不一致
4. **异步未等待**: 异步方法未 `await`，导致断言时结果尚未就绪
5. **状态污染**: 前一个测试修改了静态状态/单例，影响后续测试

## 修复方案

### 方案A：补充 Mock 设置

```csharp
// 添加遗漏的 Mock 设置
mockService.Setup(s => s.GetConnectionString())
           .Returns("Server=localhost;Database=test");
```

### 方案B：放宽匹配条件

```csharp
// 使用 It.IsAny<T>() 放宽参数匹配
mockRepo.Setup(r => r.Find(It.IsAny<int>()))
        .Returns(expectedEntity);
```

### 方案C：修正期望值

```csharp
// 检查实际业务逻辑，修正期望值
// 修改前: Assert.Equal("wrong value", result);
// 修改后: Assert.Equal("correct value", result);
```

### 方案D：使用 FluentAssertions（推荐）

```csharp
// 更清晰的断言语法
result.Should().NotBeNull()
      .And.Be("expected value");
```

## 预防措施

- [ ] 每个测试方法使用 Arrange/Act/Assert 三段式结构
- [ ] Mock 验证使用 `mock.Verify()` 确认调用次数
- [ ] 测试类实现 `IDisposable` 清理静态状态
- [ ] 使用 `FluentAssertions` 代替原生 Assert（项目已安装该包）

## 关联源码

- `tests/SSMSBoostPlus.Tests/` — 所有单元测试

## 历史案例

- （待积累）