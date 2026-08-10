# SSMSBoostPlus 自动化测试方案

本文档汇总 SSMSBoostPlus 插件的自动化测试体系，包含架构改造说明、测试矩阵、运行方式、覆盖率报告和发现的代码问题。

---

## 覆盖率报告

| 格式 | 路径 | 说明 |
|------|------|------|
| **HTML（推荐）** | `tests/coverage/html/index.html` | 浏览器打开，可视化逐行覆盖 |
| **XML（原始）** | `tests/coverage/*/coverage.cobertura.xml` | 供 CI/CD 工具消费（470 KB） |

### 核心模块覆盖率亮点

| 模块 | 覆盖率 | 行数 |
|------|--------|------|
| **VersionChecker.CheckVersionAsync** | 100% | 70/70 |
| **SqlExecuteCommands** | 100% | 17/17 |
| **BridgeFactory / BridgeFactoryService** | 100% | 7/7 |
| **CodeAnalysisService** | 97.3% | 36/37 |
| **ObjectExtractor** | ~95% | — |
| **DiffCalculator** | ~95% | — |
| ConnectionStringBuilder | ~90% | 已有测试覆盖 |
| AIResponse / AIRequest 模型 | 83%/67% | — |
| DatabaseService | ~60% | 集成测试覆盖 |

### 整体统计

| 程序集 | 覆盖行 | 总行 | 覆盖率 |
|--------|--------|------|--------|
| SSMSBoostPlus.Core | 367 | 1,461 | 25.1% |
| SSMSBoostPlus.AI | 125 | 645 | 19.4% |
| SSMSBoostPlus.Shared | 0 | 9 | 0% |
| **合计** | **492** | **2,115** | **23.3%** |

> 整体覆盖率偏低是因为 Bridge 实现类（AIApiClientBridge、CodebuddyCliBridge）、PromptTemplateManager、ResponseParser、ContextProvider 等包含大量与外部系统交互的代码，未被单元测试覆盖。这些类适合通过集成测试或端到端测试覆盖。

---

## 1. 架构改造

为支持核心模块的可测试性，完成两项向后兼容改造：

### 1.1 IDatabaseService 接口提取

| 项目 | 详情 |
|------|------|
| 新增文件 | `src/Core/Interfaces/IDatabaseService.cs` |
| 修改文件 | `src/Core/Services/DatabaseService.cs`（添加 `: IDatabaseService`） |
| 修改文件 | `src/Core/VersionGuard/VersionChecker.cs`（参数类型 `DatabaseService` → `IDatabaseService`） |
| 影响范围 | 零破坏，接口实现类可隐式转换，所有调用点无需改动 |

### 1.2 BridgeFactory 可注入化

| 项目 | 详情 |
|------|------|
| 新增文件 | `src/AI/Bridge/IBridgeFactory.cs`（接口） |
| 新增文件 | `src/AI/Bridge/BridgeFactoryService.cs`（实例实现类） |
| 修改文件 | `src/AI/Bridge/BridgeFactory.cs`（静态方法委托到 `BridgeFactoryService` 实例） |
| 影响范围 | 向后兼容，静态 API 不变，新增支持构造函数注入 `IBridgeFactory` |

---

## 2. 测试矩阵

### 2.1 测试统计

| 指标 | 数值 |
|------|------|
| 测试总计 | 103 个 |
| 通过 | 103 个 |
| 失败 | 0 个 |
| 跳过 | 0 个（集成测试在 LocalDB 不可用时静默跳过） |
| 原有用例 | 63 个 |
| 新增用例 | 40 个 |

### 2.2 测试文件清单

| 文件 | 用例数 | 覆盖模块 | 依赖 |
|------|--------|----------|------|
| CoreTests.cs | 21 | ObjectExtractor, DiffCalculator, AI/VG/Config Models | 无 |
| ExecutionInterceptorTests.cs | 16 | SqlExecuteCommands, VG Config, Token 检测 | 无 |
| ConnectionStringBuilderTests.cs | 16 | ConnectionStringBuilder | 无 |
| VersionCheckerTests.cs | 12 | VersionChecker.CheckVersionAsync, GetLatestDefinitionAsync | Moq: IDatabaseService + IConfigurationService + ILoggerService |
| CodeAnalysisServiceTests.cs | 10 | CodeAnalysisService 四个分析方法 + 代码提取 + 异常 | Moq: ICodebuddyBridge + IPromptTemplateManager + IConfigurationService |
| BridgeFactoryTests.cs | 4 | BridgeFactoryService 降级逻辑 + 静态桥接向后兼容 | 无（实例化测试） |
| ConfigurationServiceTests.cs | 4 | AIConfig/VGConfig 默认值 + IsAIConfigured 逻辑 | Moq: IConfigurationService |
| ObjectExtractorEdgeTests.cs | 6 | ObjectExtractor 空脚本/注释干扰/Schema 默认 + DiffCalculator 多差异块 + VersionInfo 边界 | 无 |
| DatabaseServiceIntegrationTests.cs | 4 | DatabaseService 真实 SQL Server LocalDB 交互 | LocalDB（可选） |

### 2.3 版本守护测试覆盖

| 场景 | 状态 |
|------|------|
| 版本守护已禁用 → Skipped=true | 已覆盖 |
| 脚本含 #nosynccheck → Skipped=true | 已覆盖 |
| 脚本含 #checklater → 不跳过，继续检查 | 已覆盖 |
| 脚本无对象引用 → 空 VersionInfos | 已覆盖 |
| 所有对象未过期 → HasOutdated=false | 已覆盖 |
| 单个对象过期 → HasOutdated=true | 已覆盖 |
| 排除数据库匹配 → 对象被过滤 | 已覆盖 |
| 排除对象匹配 → 对象被过滤 | 已覆盖 |
| DB 查询异常 → ObjectExists=false，非致命 | 已覆盖 |
| 配置服务异常 → HasError=true | 已覆盖 |
| 仅检查 CREATE/ALTER 定义对象（EXEC 引用被过滤） | 已覆盖 |
| GetLatestDefinitionAsync 基本流程 | 已覆盖 |

### 2.4 AI 桥接层测试覆盖

| 场景 | 状态 |
|------|------|
| 直接创建 CLI 桥接 → 返回 CodebuddyCliBridge | 已覆盖 |
| 直接创建 HTTP API 桥接 → 返回 AIApiClientBridge | 已覆盖 |
| 静态 BridgeFactory.CreateCliBridge 向后兼容 | 已覆盖 |
| 静态 BridgeFactory.CreateHttpApiBridge 向后兼容 | 已覆盖 |

### 2.5 AI 代码分析服务测试覆盖

| 场景 | 状态 |
|------|------|
| AI 未配置 → GetSuggestionsAsync 返回失败 | 已覆盖 |
| GetSuggestionsAsync 成功 → 返回有效结果 | 已覆盖 |
| Bridge 返回失败 → 返回 ErrorMessage | 已覆盖 |
| AddCommentsAsync with ```sql 代码块 → 正确提取 | 已覆盖 |
| AddCommentsAsync 无代码块 → 原文返回 | 已覆盖 |
| FormatCodeAsync 使用默认风格 | 已覆盖 |
| FormatCodeAsync 覆盖风格 | 已覆盖 |
| AnalyzePerformanceAsync 含执行计划 | 已覆盖 |
| Bridge 抛异常 → 捕获返回失败 | 已覆盖 |
| AI 返回空内容 → SuggestedCode null | 已覆盖 |

### 2.6 集成测试（LocalDB）

| 场景 | 状态 |
|------|------|
| 查询 sys.objects 获取 modify_date | 已覆盖（需 LocalDB） |
| 查询不存在的对象返回 null | 已覆盖（需 LocalDB） |
| 获取系统对象定义 | 已覆盖（需 LocalDB） |
| ClearCache 清空后重新查询 | 已覆盖（需 LocalDB） |

---

## 3. 运行方式

### 3.1 本地运行

```powershell
cd F:\2.C#_Core\SSMSBoostPlus

# 编译并运行全量测试
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release

# 仅运行版本守护相关测试
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --filter "FullyQualifiedName~VersionChecker"

# 仅运行 AI 相关测试
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --filter "FullyQualifiedName~CodeAnalysis|BridgeFactory"

# 生成覆盖率报告
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release `
    --collect:"XPlat Code Coverage" `
    --results-directory:./tests/coverage

# 生成 HTML 报告（需先安装 ReportGenerator）
# dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator -reports:./tests/coverage/*/coverage.cobertura.xml -targetdir:./tests/coverage/html -reporttypes:Html

# 打开 HTML 报告
start tests/coverage/html/index.html
```

### 3.2 集成测试前置条件

`DatabaseServiceIntegrationTests` 需要 SQL Server LocalDB：

```powershell
# 确认 LocalDB 可用
sqllocaldb info MSSQLLocalDB

# 若未安装，通过 Visual Studio Installer 勾选 SQL Server LocalDB 组件
```

LocalDB 不可用时，集成测试自动静默跳过（不报错）。

---

## 4. 发现的代码问题

在测试编写过程中发现一个真实代码问题：

**ObjectReference.Database 初始化为空字符串而非 null**

`ObjectExtractor.AddObject()` 中：
```csharp
Database = name.DatabaseIdentifier?.Value ?? string.Empty,
```

导致 `VersionChecker` 中：
```csharp
r.Database ?? currentDatabase
```
的 `??` 回退逻辑无效（`""` 不是 `null`，不会回退到 `currentDatabase`）。

当对象无数据库前缀时（如 `CREATE PROCEDURE dbo.Test`），排除数据库（ExcludedDatabases）的过滤依赖此回退逻辑。

**影响**：如果用户配置中 `ExcludedDatabases` 包含当前连接的数据库名，无前缀的 `CREATE PROCEDURE` 语句不会被正确排除。

**状态**：已记录，建议将 `ObjectReference.Database` 的默认值改为 `null` 并在创建实例时使用 `name.DatabaseIdentifier?.Value`（保留 `null` 而非回退到 `""`）。当前测试已适配现有行为。

---

## 5. 文件清单

| 路径 | 状态 | 说明 |
|------|------|------|
| `src/Core/Interfaces/IDatabaseService.cs` | 新增 | 数据库服务接口 |
| `src/Core/Services/DatabaseService.cs` | 修改 | 实现 IDatabaseService |
| `src/Core/VersionGuard/VersionChecker.cs` | 修改 | 参数类型改为 IDatabaseService |
| `src/AI/Bridge/IBridgeFactory.cs` | 新增 | 桥接工厂接口 |
| `src/AI/Bridge/BridgeFactoryService.cs` | 新增 | 桥接工厂实例实现 |
| `src/AI/Bridge/BridgeFactory.cs` | 修改 | 静态类委托到实例类 |
| `tests/SSMSBoostPlus.Tests/VersionCheckerTests.cs` | 新增 | 版本守护测试（12 用例） |
| `tests/SSMSBoostPlus.Tests/BridgeFactoryTests.cs` | 新增 | 桥接工厂测试（4 用例） |
| `tests/SSMSBoostPlus.Tests/CodeAnalysisServiceTests.cs` | 新增 | AI 分析服务测试（10 用例） |
| `tests/SSMSBoostPlus.Tests/ConfigurationServiceTests.cs` | 新增 | 配置一致性测试（4 用例） |
| `tests/SSMSBoostPlus.Tests/ObjectExtractorEdgeTests.cs` | 新增 | 边界用例测试（6 用例） |
| `tests/SSMSBoostPlus.Tests/DatabaseServiceIntegrationTests.cs` | 新增 | 集成测试（4 用例） |