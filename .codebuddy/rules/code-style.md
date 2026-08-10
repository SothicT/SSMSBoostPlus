---
description: 
alwaysApply: true
enabled: true
updatedAt: 2026-06-09T09:36:55.820Z
provider: 
---

# 代码规范

## 编译配置

| 设置 | 值 |
|------|---|
| LangVersion | 10.0 |
| Nullable | enable |
| ImplicitUsings | 禁用（显式 using） |

## XAML 行为库

**必须使用**：`Microsoft.Xaml.Behaviors.Wpf`

**禁止使用**：`System.Windows.Interactivity`（已过时）

```xml
<!-- ✅ 正确 -->
<Window xmlns:i="http://schemas.microsoft.com/xaml/behaviors">
    <i:Interaction.Triggers>
        ...
    </i:Interaction.Triggers>
</Window>

<!-- ❌ 错误 -->
<Window xmlns:i="clr-namespace:System.Windows.Interactivity;assembly=System.Windows.Interactivity">
```

## 配置与日志路径

**必须使用**：程序集所在目录

```csharp
// ✅ 正确
var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

// ❌ 禁止
var appData = Environment.GetFolderPath(ApplicationData);
```

优势：
- 部署后配置跟随扩展目录
- 便于管理、备份、迁移
- 日志子目录：`{部署目录}\Logs\`

## 命名规范

### C# 代码

| 类型 | 规范 | 示例 |
|------|------|------|
| 类/接口 | PascalCase | `CodeAnalysisService` |
| 方法 | PascalCase | `GetTablesAsync` |
| 私有字段 | _camelCase | `_configService` |
| 常量 | PascalCase | `MaxRetryCount` |
| 参数 | camelCase | `tableName` |

### XAML 资源

| 类型 | 规范 | 示例 |
|------|------|------|
| MenuText | 中文 | `重新生成SQL` |
| ButtonText | 中文 | `提交反馈` |
| ToolTip | 中文 | `将当前查询发送到AI分析` |

## 异常处理

```csharp
try
{
    // 业务逻辑
}
catch (SqlException ex) when (ex.Number == 4060)  // 特定异常
{
    // 数据库特定错误处理
    throw new InvalidOperationException("数据库不存在", ex);
}
catch (Exception ex)
{
    // 通用异常处理
    Logger.Error(ex, "操作失败");
    throw;
}
```

## async/await 规范

- 方法名必须以 `Async` 结尾
- 返回 `Task` 或 `Task<T>`
- 使用 `ConfigureAwait(false)` 避免死锁

```csharp
public async Task<IEnumerable<TableInfo>> GetTablesAsync(
    string connectionString,
    CancellationToken cancellationToken = default)
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    
    return await GetTablesInternalAsync(connection, cancellationToken)
        .ConfigureAwait(false);
}
```