# SSMS 22 连接获取修复指南

## 问题分析

### 根本原因

SSMS 22 进行了重大架构重构，程序集名称发生变化：

| 功能 | SSMS 18/19/20 | SSMS 22 |
|------|---------------|---------|
| VS集成 | `Microsoft.SqlServer.Management.UI.VSIntegration.dll` | `Microsoft.SqlServer.SqlTools.VSIntegration.dll` |
| SqlStudio Explorer | `Microsoft.SqlServer.Management.SqlStudio.Explorer.dll` | `Microsoft.SqlServer.Management.Sdk.SqlStudio.dll` + `ObjectExplorer.dll` |
| 连接对话框 | `Microsoft.SqlServer.ConnectionDlg.UI.dll` | `ConnectionDlg.dll` |

### 日志证据

```
[AssemblyLoader] Assembly not found: Microsoft.SqlServer.Management.SqlStudio.Explorer.dll
[AssemblyLoader] Assembly not found: Microsoft.SqlServer.Management.UI.VSIntegration.dll
[OfficialProvider] IScriptEditorService type not found
[OfficialProvider] IObjectExplorerService type not found
[ReflectionProvider] ConnectionDialog property not found
[ReflectionProvider] ObjectExplorerService not available
```

## 解决方案

### 1. 智能程序集加载器 (SmartAssemblyLoader)

**文件**: `src/SSMS/Services/Connection/SmartAssemblyLoader.cs`

**核心功能**:
- 自动检测SSMS版本
- 支持多候选程序集名称（新名称优先，旧名称备选）
- 按优先级加载程序集
- 缓存已加载的程序集

**关键代码**:
```csharp
private static readonly Dictionary<string, string[]> AssemblyMappings = new()
{
    ["VSIntegration"] = new[]
    {
        "Microsoft.SqlServer.SqlTools.VSIntegration.dll",        // SSMS 22+
        "Microsoft.SqlServer.Management.UI.VSIntegration.dll"   // SSMS 18/19/20
    },
    // ... 更多映射
};
```

### 2. SSMS 22 专用连接提供者

**文件**: `src/SSMS/Services/Connection/SSMS22ConnectionProvider.cs`

**多重获取策略**:
1. 从 `ObjectExplorer.dll` 获取（SSMS 22新程序集）
2. 从 `ConnectionDlg.dll` 获取（新连接对话框）
3. 从活动文档获取
4. 探索式获取（运行时探索API）

**优先级**: 0（最高，仅在SSMS 22中激活）

### 3. 增强的反射提供者

**改进点**:
- 完善 `TryGetFromConnectionDialog` 实现（之前直接返回null）
- 探索所有属性寻找连接信息
- 更详细的日志记录

### 4. API探索工具

**文件**: `src/SSMS/Services/Connection/SsmsApiExplorer.cs`

**功能**:
- 运行时探索可用的服务类型
- 自动发现连接获取路径
- 生成详细的诊断日志

## 测试步骤

### 1. 编译项目

```bash
cd d:/Code/SSMSBoostPlus
dotnet build
```

### 2. 安装VSIX

1. 找到生成的 `.vsix` 文件（通常在 `bin/Debug/` 或 `bin/Release/`）
2. 在SSMS 22中安装扩展
3. 重启SSMS 22

### 3. 查看诊断日志

**日志路径**: `C:\Users\{用户名}\AppData\Roaming\SSMSBoostPlus\Logs\ssmsboostplus-{日期}.log`

**关键日志检查**:

#### 成功的日志应包含:
```
[SmartLoader] Detected SSMS V22 at: D:\Program Files\Microsoft SQL Server Management Studio 22\...
[SmartLoader] ✅ Loaded VSIntegration: Microsoft.SqlServer.SqlTools.VSIntegration.dll
[SmartLoader] ✅ Loaded ObjectExplorer: ObjectExplorer.dll
[ConnectionFacade] Initialized with 3 providers: SSMS 22 Optimized Provider(P0), Official SSMS SDK Interfaces(P1), SSMS Internal Reflection (Fallback)(P10)
[SSMS22Provider] ✅ Got connection from ObjectExplorer.dll
```

#### 失败的诊断:
```
[SmartLoader] ❌ Failed to load any candidate for: VSIntegration
[SSMS22Provider] ❌ All SSMS 22 strategies failed
[ConnectionFacade] ❌ All connection providers failed
```

### 4. 触发连接获取

1. 在SSMS 22中打开一个数据库连接
2. 打开一个查询编辑器窗口
3. 执行任何扩展命令（如 Version Guard）
4. 查看日志输出

### 5. 运行API探索（可选）

如果想深入了解SSMS 22的API结构，可以手动调用：

```csharp
SsmsApiExplorer.ExploreServices(logger);
SsmsApiExplorer.ExploreConnectionPaths(serviceProvider, logger);
```

这将在日志中输出所有可用的服务类型和属性。

## 兼容性验证

### SSMS 18/19/20 测试

1. 在SSMS 18/19/20环境中安装VSIX
2. 查看日志应显示：
```
[SmartLoader] Detected SSMS V18/V19/V20
[SmartLoader] ✅ Loaded VSIntegration: Microsoft.SqlServer.Management.UI.VSIntegration.dll
```

3. SSMS22ConnectionProvider 应被跳过（IsAvailable = false）
4. OfficialConnectionProvider 或 ReflectionConnectionProvider 应成功获取连接

### SSMS 22 测试

1. SSMS22ConnectionProvider 应被优先尝试
2. 如果失败，自动降级到 OfficialConnectionProvider
3. 最后尝试 ReflectionConnectionProvider

## 故障排查

### 问题: 所有程序集加载失败

**检查**:
1. SSMS安装路径是否正确
2. IDE目录下是否有这些DLL文件：
   ```
   dir "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\*.dll" | findstr "SqlStudio\|ObjectExplorer\|Connection"
   ```

**解决**: 更新 `SmartAssemblyLoader.DetectSsmsPath()` 中的路径

### 问题: 服务实例获取失败

**原因**: SSMS 22的服务注册机制可能改变

**诊断**:
1. 查看日志中的服务类型探索结果
2. 使用 `SsmsApiExplorer` 详细探索

**解决**: 
1. 根据探索结果更新类型名称
2. 尝试新的服务获取路径

### 问题: 连接属性不存在

**原因**: ServerConnection对象的属性在不同SSMS版本中可能不同

**诊断**: 查看日志中 `ExtractConnectionInfo` 的输出

**解决**: 添加更多属性的探测逻辑

## 关键改进总结

1. ✅ **程序集名称适配**: 自动处理新旧程序集名称
2. ✅ **版本感知**: 根据SSMS版本选择最优Provider
3. ✅ **完善逻辑**: 修复 TryGetFromConnectionDialog 直接返回null的问题
4. ✅ **多重策略**: SSMS 22专用Provider实现多种获取路径
5. ✅ **探索工具**: 提供运行时API探索能力
6. ✅ **详细日志**: 增强诊断信息，便于排查问题

## 下一步

如果仍有问题：

1. **提供完整日志文件**（包含从启动到失败的所有日志）
2. **运行诊断命令**：
   ```csharp
   SsmsApiExplorer.ExploreServices(logger);
   ```
3. **手动检查程序集**：
   ```powershell
   dir "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\*.dll" | findstr /i "SqlStudio Explorer Connection"
   ```

这将帮助我们进一步分析SSMS 22的实际API结构。
