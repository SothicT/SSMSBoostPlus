# 日志格式参考

## 1. Serilog 日志（运行时异常）

### 配置格式

```
outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{MachineName}] {Message:lj}{NewLine}{Exception}"
```

### 解析规则

| 日志片段 | 提取 |
|----------|------|
| `2026-06-07 16:30:00.123` | 时间戳 |
| `[ERR]` / `[FTL]` / `[WRN]` / `[INF]` / `[DBG]` | 日志级别 |
| `[MACHINE-NAME]` | 机器名 |
| `{Message}` | 日志消息（单行） |
| `{Exception}` | 异常详情（多行，包含类型/消息/堆栈） |

### 示例

```
2026-06-07 16:30:00.123 [ERR] [DEV-PC] Failed to connect to database
System.NullReferenceException: Object reference not set to an instance of an object.
   at SSMSBoostPlus.SSMS.Services.Connection.DatabaseConnectionHelper.GetConnectionString() in F:\2.C#_Core\SSMSBoostPlus\src\SSMS\Services\Connection\DatabaseConnectionHelper.cs:line 42
   at SSMSBoostPlus.SSMS.Services.Connection.ConnectionServiceFacade.OpenConnection() in F:\2.C#_Core\SSMSBoostPlus\src\SSMS\Services\Connection\ConnectionServiceFacade.cs:line 128
```

### 提取结果

| 字段 | 值 |
|------|-----|
| 异常类型 | `System.NullReferenceException` |
| 异常消息 | `Object reference not set to an instance of an object.` |
| 堆栈帧1 | `DatabaseConnectionHelper.GetConnectionString()` (line 42) |
| 堆栈帧2 | `ConnectionServiceFacade.OpenConnection()` (line 128) |
| 上下文消息 | `Failed to connect to database` |

---

## 2. MSBuild 编译错误

### 格式

```
{file}({line},{col}): error CS{code}: {message} [{project}]
```

### 示例

```
src\SSMS\Services\Connection\DatabaseConnectionHelper.cs(42,13): error CS0246: The type or namespace name 'SqlConnection' could not be found (are you missing a using directive or an assembly reference?) [F:\2.C#_Core\SSMSBoostPlus\src\SSMS\SSMSBoostPlus.SSMS.csproj]
```

### 提取结果

| 字段 | 值 |
|------|-----|
| 错误码 | `CS0246` |
| 文件 | `src\SSMS\Services\Connection\DatabaseConnectionHelper.cs` |
| 行号 | `42` |
| 列号 | `13` |
| 消息 | `The type or namespace name 'SqlConnection' could not be found` |

### 常见 CS 错误码速查

| 错误码 | 含义 | 常见原因 |
|--------|------|----------|
| CS0246 | 找不到类型或命名空间 | 缺少 using 或项目引用 |
| CS1061 | 类型不包含定义 | 方法/属性拼写错误或缺少扩展方法 |
| CS0103 | 名称不存在 | 变量未声明或作用域不对 |
| CS0117 | 类型不包含成员 | 静态方法通过实例调用或反之 |
| CS1503 | 参数类型不匹配 | 方法签名变化后未同步调用方 |
| CS0234 | 命名空间中不存在类型 | 缺少 NuGet 包引用 |

---

## 3. xUnit 测试失败

### 格式

```
Failed {TestName} [{elapsed}]
  Error Message:
   {message}
  Stack Trace:
   {stackTrace}
```

### 示例

```
Failed SSMSBoostPlus.Tests.Services.DatabaseServiceTests.GetConnectionString_ShouldReturnValidString [15 ms]
  Error Message:
   Assert.Equal() Failure
   Expected: "Server=localhost;Database=test"
   Actual:   (null)
  Stack Trace:
     at SSMSBoostPlus.Tests.Services.DatabaseServiceTests.GetConnectionString_ShouldReturnValidString() in F:\2.C#_Core\SSMSBoostPlus\tests\SSMSBoostPlus.Tests\Services\DatabaseServiceTests.cs:line 25
```

### 提取结果

| 字段 | 值 |
|------|-----|
| 测试名 | `GetConnectionString_ShouldReturnValidString` |
| 失败类型 | `Assert.Equal() Failure` |
| 期望值 | `Server=localhost;Database=test` |
| 实际值 | `(null)` |
| 堆栈帧 | `DatabaseServiceTests.GetConnectionString_ShouldReturnValidString()` (line 25) |