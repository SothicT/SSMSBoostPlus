# SSMSBoostPlus

基于 AI 大模型的 SQL Server Management Studio 22 智能增强扩展，为 SSMS 提供 SQL 安全审查、注释生成、代码格式化、性能分析、AI 对话及版本守卫六大核心功能。

---

## 目录

1. [项目简介](#1-项目简介)
2. [功能特性](#2-功能特性)
3. [系统架构](#3-系统架构)
4. [技术栈](#4-技术栈)
5. [快速开始](#5-快速开始)
6. [开发指南](#6-开发指南)
7. [构建与部署](#7-构建与部署)
8. [测试](#8-测试)
9. [配置说明](#9-配置说明)
10. [许可证](#10-许可证)

---

## 1. 项目简介

SSMSBoostPlus 是一款面向 SQL Server 开发人员的 SSMS 22 扩展插件。它将数据库元数据（Schema、索引、执行计划）深度注入 AI 上下文，实现精准的 SQL 分析，将传统人工 SQL 审查与性能诊断从数十分钟缩短至秒级响应。

| 项目 | 说明 |
|------|------|
| **全称** | SSMSBoostPlus |
| **类型** | SSMS 22 扩展插件（VSIX） |
| **目标用户** | SQL Server 开发人员、DBA |
| **开发方式** | 基于 CodeBuddy AI Agent 全程驱动 |
| **支持版本** | SSMS 18 / 19 / 20 / 22 |

---

## 2. 功能特性

SSMSBoostPlus 提供 5 大核心 AI 功能及版本守卫功能，可通过 SQL 编辑器右键菜单或快捷键快速调用：

| 功能 | 快捷键 | 说明 |
|------|--------|------|
| **AI 智能建议** | Ctrl+Shift+A | 基于数据库上下文，对选中 SQL 提出优化建议 |
| **AI 添加注释** | Ctrl+Shift+C | 自动为 SQL 脚本生成中文注释 |
| **AI 格式化代码** | Ctrl+Shift+F | 智能格式化 SQL 语句 |
| **AI 性能分析** | Ctrl+Shift+P | 结合执行计划分析 SQL 性能瓶颈 |
| **AI 对话面板** | Ctrl+Shift+K | 基于数据库 Schema 的 AI 问答停靠面板 |
| **版本守卫** | Ctrl+Shift+V | 检测脚本引用的数据库对象自编辑后是否已被他人修改，防止版本冲突 |

所有 AI 功能均支持：
- 选中代码分析 / 全文分析
- Diff 差异对比预览（前 4 项）
- 流式 Markdown 渲染输出（性能分析 / 对话面板）
- 数据库上下文（Schema、索引、执行计划）自动注入

### 版本守卫（非 AI 功能）

**版本守卫**（Version Guard）专为多人协作场景设计，在执行 SQL 前自动检测脚本引用的数据库对象是否已被他人修改，防止版本冲突。

| 快捷键 | 说明 |
|--------|------|
| **Ctrl+Shift+V** | 手动触发版本检查 |
| **F5（自动拦截）** | 执行前自动拦截并触发版本检查 |

核心能力：
- **差异比对**：基于 DiffPlex 进行行级差异对比，分类为一致/新增/删减/有冲突/对象不存在
- **强制审查**：对有冲突/新增/删减的对象，必须逐项审查差异后才能继续执行
- **二次校验**：点击"继续执行"前再次查询数据库，确保审查期间无人修改
- **一键重载**：从数据库重新加载最新定义替换编辑器内容
- **跳过令牌**：在脚本中添加 `--version-guard-skip` 可跳过检查

---

## 3. 系统架构

### 3.1 模块依赖关系

```
Shared (netstandard2.0)
    ↓
Core  (netstandard2.0)
    ↓
AI    (netstandard2.0)
    ↓
SSMS  (net472)
    ↑
Installer (net472, 独立命令行)
```

**依赖规则**（单向，禁止逆流）：
- ✅ 允许：`Shared → Core → AI → SSMS` 单向下行
- ❌ 禁止：Core 引用 AI、Core 引用 SSMS
- ❌ 禁止：AI 引用 SSMS
- ❌ 禁止：任意循环依赖

### 3.2 模块职责

| 模块 | 目标框架 | 职责 | 不应包含 |
|------|----------|------|----------|
| `Shared` | netstandard2.0 | 公共类型、工具、接口定义 | 业务逻辑、SSMS 特定代码 |
| `Core` | netstandard2.0 | 核心业务逻辑、数据库操作 | AI 调用、UI 代码 |
| `AI` | netstandard2.0 | AI 服务封装、Prompt 管理 | UI 代码、SSMS 特定逻辑 |
| `SSMS` | net472 | SSMS 集成、菜单命令、WPF UI | 核心业务逻辑 |
| `Installer` | net472 | 独立命令行安装器 | SSMS 宿主、UI |

### 3.3 主要命名空间

| 命名空间 | 模块 | 职责 |
|----------|------|------|
| `SSMSBoostPlus.SSMS.Commands` | SSMS | 14 个命令处理器 |
| `SSMSBoostPlus.SSMS.Services` | SSMS | SSMS API 封装（编辑器/对话框/连接/右键菜单） |
| `SSMSBoostPlus.SSMS.UI.Dialogs` | SSMS | XAML 对话框 |
| `SSMSBoostPlus.SSMS.UI.Chat` | SSMS | AI 对话面板 |
| `SSMSBoostPlus.Services` | Core | 通用服务（配置/数据库/日志/连接缓存） |
| `SSMSBoostPlus.VersionGuard` | Core | 前后端版本校验 |
| `SSMSBoostPlus.SQLParser` | Core | SQL 解析（对象提取） |
| `SSMSBoostPlus.DiffEngine` | Core | Diff 差异计算 |
| `SSMSBoostPlus.AI.Bridge` | AI | AI API 桥接（CLI / HTTP） |
| `SSMSBoostPlus.AI.Services` | AI | CodeAnalysisService（5 套 Prompt 模板） |
| `SSMSBoostPlus.Shared` | Shared | 共享枚举常量 |

---

## 4. 技术栈

| 技术 | 版本/说明 |
|------|-----------|
| **C# 语言** | 10.0 |
| **.NET Standard** | 2.0（Shared / Core / AI） |
| **.NET Framework** | 4.7.2（SSMS / Installer） |
| **WPF** | 桌面 UI 框架 |
| **VS SDK** | 17.14.40265 |
| **VSSDK BuildTools** | 17.14.2142 |

### 核心依赖库

| 依赖 | 用途 |
|------|------|
| `Microsoft.Data.SqlClient` 5.2.0 | SQL Server 数据库连接 |
| `Microsoft.SqlServer.TransactSql.ScriptDom` 150.4573.2 | SQL 语法解析（AST） |
| `Microsoft.SqlServer.SqlManagementObjects` 161.44091.41 | SQL Server 数据模型 |
| `Newtonsoft.Json` 13.0.3 | JSON 序列化 |
| `DiffPlex` 1.7.2 | 差异计算（行/字符级） |
| `Serilog` 3.0.1 | 结构化日志 |
| `Microsoft.Xaml.Behaviors.Wpf` 1.1.77 | WPF 行为绑定 |
| `Microsoft.Web.WebView2` 1.0.2903.40 | Chromium 内核浏览器控件 |
| `Markdig` 0.37.0 | Markdown → XAML 渲染 |
| `Markdown.Xaml` 1.0.0 | WPF Markdown 控件 |

---

## 5. 快速开始

### 环境要求

- **SSMS 22**（支持 18/19/20）
- **.NET SDK**（仅开发需要，推荐 .NET SDK 10.0+）

### 安装方式

**方式一：使用 Installer（推荐）**

从 Release 分支下载 `InstallSSMSBoostPlus.exe`，双击运行即可自动完成在线下载与安装。

**方式二：手动部署**

```powershell
# 1. 关闭 SSMS
# 2. 以管理员身份运行部署脚本
.\deploy-to-ssms.bat
```

**方式三：源码编译部署**

```powershell
# 1. 编译项目
dotnet build SSMSBoostPlus.slnx -c Release

# 2. 部署（管理员权限）
.\deploy-to-ssms.bat
```

---

## 6. 开发指南

### 项目结构

```
SSMSBoostPlus/
├── src/
│   ├── Shared/              # 共享常量和枚举（netstandard2.0）
│   ├── Core/                # 核心业务逻辑（netstandard2.0）
│   ├── AI/                  # AI 桥接与提示工程（netstandard2.0）
│   │   └── Prompts/Templates/  # 5 套 Prompt Markdown 模板
│   ├── SSMS/                # SSMS 扩展宿主（net472, WPF）
│   │   ├── Commands/        # 14 个命令处理器
│   │   ├── UI/Dialogs/      # XAML 对话框
│   │   ├── UI/Chat/         # AI 对话面板
│   │   └── SSMSBoostPlus.vsct  # 命令表定义
│   └── Installer/           # 独立命令行安装器（net472）
├── tests/                   # 单元测试（net8.0, xUnit）
├── AIDoc/                   # 项目文档目录
├── deploy-to-ssms.bat       # 部署脚本（需管理员权限）
├── SSMSBoostPlus.slnx       # 解决方案文件（注意：使用 .slnx 而非 .sln）
└── nuget.config             # NuGet 包源配置
```

### 编译

```powershell
# 编译解决方案（务必使用 .slnx，.sln 被企业加密软件加壳无法解析）
dotnet build SSMSBoostPlus.slnx -c Release

# 编译特定项目
dotnet build src/SSMS/SSMSBoostPlus.SSMS.csproj -c Release
```

### 编码规范要点

| 规范 | 要求 |
|------|------|
| C# 版本 | 10.0 |
| 可空引用类型 | `enable`（所有项目） |
| 隐式 using | 禁用（需显式 using 声明） |
| 私有字段 | `_camelCase` |
| async 方法 | 以 `Async` 后缀结尾，使用 `ConfigureAwait(false)` |
| XAML 行为库 | 使用 `Microsoft.Xaml.Behaviors.Wpf`，禁止 `System.Windows.Interactivity` |
| 日志与配置路径 | 使用 `Assembly.GetExecutingAssembly().Location` 所在目录 |

---

## 7. 构建与部署

### 编译命令

```powershell
dotnet build SSMSBoostPlus.slnx -c Release
```

> ⚠️ **重要**：必须使用 `SSMSBoostPlus.slnx`，不要使用 `SSMSBoostPlus.sln`（企业加密软件会加壳 `.sln` 文件，导致 `dotnet` 无法解析）。

### 部署脚本

使用 `deploy-to-ssms.bat` 将编译产物部署到 SSMS 扩展目录。

**部署前准备**：
1. 确保已执行 `dotnet build SSMSBoostPlus.slnx -c Release`
2. **关闭所有 SSMS 窗口**（否则 DLL 被锁定）
3. **以管理员身份运行** `deploy-to-ssms.bat`

### 部署清单

**核心 DLL（4 个 + 4 个 PDB）**：

| 文件 | 说明 |
|------|------|
| `SSMSBoostPlus.SSMS.dll` | SSMS 扩展主程序 |
| `SSMSBoostPlus.Core.dll` | 核心功能层 |
| `SSMSBoostPlus.AI.dll` | AI 服务封装 |
| `SSMSBoostPlus.Shared.dll` | 共享库 |

**第三方依赖（12 个）**：

`DiffPlex.dll`、`Serilog.dll`、`Serilog.Sinks.File.dll`、`Serilog.Enrichers.Environment.dll`、`Microsoft.Data.SqlClient.dll`、`Newtonsoft.Json.dll`、`Microsoft.Bcl.AsyncInterfaces.dll`、`Microsoft.Extensions.Configuration.dll`、`Microsoft.Extensions.Configuration.Json.dll`、`Microsoft.Extensions.Configuration.Binder.dll`、`Microsoft.SqlServer.TransactSql.ScriptDom.dll`、`System.Net.Http.dll`

**配置文件（3 个）**：

| 文件 | 说明 |
|------|------|
| `SSMSBoostPlus.SSMS.pkgdef` | VSIX 包注册信息 |
| `Menus.ctmenu` | 编译后的命令表 |
| `extension.vsixmanifest` | 扩展清单 |

### 部署流程

部署脚本自动按以下步骤执行：

1. 检查管理员权限
2. 检测 SSMS 安装路径
3. 验证编译产物完整性
4. 清除旧部署文件
5. 复制核心 DLL + PDB
6. 复制配置文件（pkgdef / ctmenu / vsixmanifest）
7. 复制第三方依赖
8. 刷新扩展配置缓存（`ssms.exe /setup`）
9. 验证部署完整性

---

## 8. 测试

### 测试技术栈

| 组件 | 版本 | 用途 |
|------|------|------|
| xUnit | 2.4.2 | 测试框架 |
| Moq | 4.18.4 | Mock 框架 |
| FluentAssertions | 6.11.0 | 断言库 |

### 运行测试

```powershell
# 运行所有测试
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release

# 带覆盖率
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --collect:"XPlat Code Coverage"

# 运行特定测试
dotnet test tests/SSMSBoostPlus.Tests/ -c Release --filter "FullyQualifiedName~ServiceTests"
```

### 测试门禁

提交前必须通过全部测试：

```powershell
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --no-build
```

当前测试用例总数：**103 个**，全部通过 ✅。

---

## 9. 配置说明

### NuGet 包源

`nuget.config` 配置了两个包源：

```xml
<packageSources>
  <clear />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  <add key="MES.huaqin" value="http://172.16.50.189:8765/nuget" />
</packageSources>
```

> 内部 HTTP NuGet 源已设置 `allowInsecureConnections="true"`。

### PkgDef 手动生成机制

> ⚠️ **此机制为永久方案**，不要尝试"修复"回 VSSDK 原生方式。

原因：.NET SDK 10 与 VSSDK 17.14 的 `PrepareForRunDependsOn` 存在覆盖 Bug，导致 `GeneratePkgDef` 目标不被调度。解决方案：

1. **VSCT 编译**：`ManualVSCTCompile` Target（`BeforeTargets="CoreCompile"`）调用 `VSCT.exe` 将 `.vsct` 编译为 `.cto`
2. **PkgDef 生成**：`ManualPkgDef` Target（`AfterTargets="Build"`）调用 `CreatePkgDef.exe` CLI 生成 `.pkgdef`

两个"禁用"目标确保 VSSDK 原生 Pipeline 不会干扰：

```xml
<Target Name="GeneratePkgDef" />
<Target Name="CreateMenuPkgDef" />
```

### SSMS 内部 DLL 引用

SSMS 项目通过编译时引用 + 运行时动态加载策略引用 SSMS 内部 DLL：

| DLL | 编译时 | 运行时 |
|-----|--------|--------|
| `envdte.dll` | 引用（Private=False） | SSMS 加载 |
| `Microsoft.VisualStudio.Editor.dll` | 引用（Private=False） | SSMS 加载 |
| `Microsoft.VisualStudio.Text.UI.Wpf.dll` | 引用（Private=False） | SSMS 加载 |
| `Microsoft.VisualStudio.ComponentModelHost.dll` | 引用（Private=False） | SSMS 加载 |
| `VSIntegration*.dll` | 不引用 | 动态加载（SmartAssemblyLoader） |
| `ObjectExplorer*.dll` | 不引用 | 动态加载（SmartAssemblyLoader） |

### 日志路径

日志文件输出到扩展部署目录下的 `Logs/` 子目录：

```
{部署目录}\Logs\SSMSBoostPlus-{日期}.log
```

### 版本守卫配置

版本守卫支持通过扩展部署目录下的 `appsettings.json` 自定义配置：

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `VersionGuard.Enabled` | bool | true | 是否启用版本守卫 |
| `VersionGuard.SkipTokens` | string[] | ["--version-guard-skip"] | 脚本中可跳过检查的标记列表 |
| `VersionGuard.ExcludedDatabases` | string[] | [] | 排除检查的数据库列表 |
| `VersionGuard.ExcludedObjects` | string[] | [] | 排除检查的对象列表 |
| `VersionGuard.BypassOnNoDiff` | bool | true | 无差异时是否自动跳过 |

---

## 10. 许可证

本项目为内部开发项目，版权归原作者所有。