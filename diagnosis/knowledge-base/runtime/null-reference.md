# NullReferenceException

## 匹配特征

- 异常类型: `System.NullReferenceException`
- 关键方法: 任意
- 消息关键词: `Object reference not set to an instance of an object`

## 常见根因

1. 依赖注入未配置：通过 `BridgeFactory` 获取的服务为 null，但调用方未检查
2. 反射返回 null：`GetProperty().GetValue()` 或 `GetMethod().Invoke()` 返回 null 后直接使用
3. 配置未加载：`ConfigurationService` 未初始化或配置文件不存在，导致配置值为 null
4. 初始化顺序问题：成员变量在使用前未赋值（如构造函数中异步初始化未完成）
5. 日志目录未创建：`SerilogLoggerService` 初始化时 Logs 目录不存在

## 修复方案

### 方案A：添加 Null 守卫（推荐）

```csharp
// 在方法入口添加
if (xxx == null)
{
    _logger.Warning("xxx 未初始化，跳过操作");
    return; // 或返回默认值
}
```

### 方案B：确保初始化顺序

```csharp
// 在构造函数或 InitializeAsync 中确保依赖就绪
if (service == null)
{
    service = BridgeFactory.GetService<ISomeService>();
    if (service == null)
        throw new InvalidOperationException("ISomeService 未注册");
}
```

### 方案C：启用 Nullable 静态分析

项目已使用 `LangVersion 10.0`，可在 csproj 中添加：
```xml
<Nullable>enable</Nullable>
```

## 预防措施

- [ ] 所有公共 API 入口添加参数 Null 检查
- [ ] `BridgeFactory.GetService<T>()` 返回值始终判空
- [ ] 反射操作（`GetValue`/`Invoke`）结果判空后再使用
- [ ] 关键服务在 `SSMSBoostPlusPackage.InitializeAsync()` 中完成初始化

## 关联源码

- `src/SSMS/Services/Connection/` — 大量反射操作
- `src/SSMS/SSMSBoostPlusPackage.cs` — 服务初始化入口
- `src/Core/Services/ConfigurationService.cs` — 配置加载

## 历史案例

- 2026-06-07: 部署后首次启动报 NullReferenceException，原因是 Logs 目录未自动创建（已修复：SerilogLoggerService 自动创建目录）
- 2026-06-07: SSMS 22 的 `ServiceCache.ScriptFactory` getter 内部抛 NRE，用户打开 .sql 文件但未连接数据库。已优雅处理（try-catch），非代码 bug。