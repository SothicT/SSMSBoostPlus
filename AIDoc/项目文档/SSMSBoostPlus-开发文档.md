# SSMSBoost++ 内部开发文档

**版本**：1.1
**更新日期**：2026-07-02
**目标读者**：接手维护的开发人员

---

> **v1.1 变更摘要**（2026-07-02）
>
> 本次小幅迭代基于最近 30 次提交，对开发文档做以下补全与勘误：
>
> 1. **架构补充**：新增 `Installer` 子项目（net472 命令行安装器）；修正模块依赖图为 **Shared → Core → AI → SSMS** 单向链。
> 2. **命令清单扩容**：从 9 个扩展为 11+（新增 `cmdidAIChatPanel=0x0204` AI 对话面板、`cmdidVersionCheck=0x0302` 版本检查与自动更新）。
> 3. **AI 子项目完善**：补 `BridgeFactoryService` 拆分（3 级优先级 + 日志）、`CodeAnalysisService` 新增 Chat 场景、Prompt 模板扩为 5 套（suggest / comment / format / analyze / chat）。
> 4. **SSMS 集成新增组件**：`AIChatToolWindow`、`MarkdownViewer`、`TypingIndicator`、`VersionUpdateDialog`、`CommitDetailDialog`；SQL 编辑器右键菜单改为运行时动态注册方案。
> 5. **构建/部署修正**：`.sln` → `.slnx`、`SSMSBoostPlus.slnx` 命令；4 核心 DLL + 12 第三方依赖部署清单；保留 `ManualPkgDef` 永久方案。
> 6. **配置模型**：`AIConfiguration` 增 `UserPromptPrefix=@SSMSBOOST` 常量；`AIDefaults` 公开 `DefaultEndpoint` / `DefaultApiKey` / `DefaultModelName`。
>
> > 注：本次不包含 Bug 修复记录，相关修复请查阅 `AIDoc/诊断报告/`。

---

## 目录

1. [项目概览](#1-项目概览)
2. [架构设计](#2-架构设计)
3. [核心模块详解](#3-核心模块详解)
4. [版本守护核心算法](#4-版本守护核心算法)
5. [AI 桥接层原理](#5-ai-桥接层原理)
6. [SSMS 集成细节](#6-ssms-集成细节)
7. [构建与部署](#7-构建与部署)
8. [附录](#8-附录)

---

## 1. 项目概览

### 1.1 项目结构

```
SSMSBoostPlus/
├── src/
│   ├── Shared/            # 共享常量和枚举（netstandard2.0，纯共享库）
│   ├── Core/              # 核心业务逻辑库（netstandard2.0）
│   ├── AI/                # AI 桥接与提示工程库（netstandard2.0）
│   ├── SSMS/              # SSMS 扩展宿主（net472, WPF）
│   └── Installer/         # 独立命令行安装器（net472，独立可执行）
├── tests/                 # 单元测试（net8.0, xUnit）
├── AIDoc/                 # 文档输出目录
└── deploy-to-ssms.bat     # 部署脚本（需管理员权限）
```

> **v1.1 新增**：`Installer` 子项目（`src/Installer/`）为独立命令行 EXE（`InstallSSMSBoostPlus.exe`），不依赖 SSMS 宿主，可双击直接在线下载并安装最新版本（详见 7.x 节）。

**模块职责边界**（v1.1 明确）：

| 模块 | 职责 | 不应包含 |
|------|------|----------|
| `Shared` | 公共类型、工具、接口定义 | 业务逻辑、SSMS 特定代码 |
| `Core` | 核心业务逻辑、数据库操作 | AI 调用、UI 代码 |
| `AI` | AI 服务封装、Prompt 管理 | UI 代码、SSMS 特定逻辑 |
| `SSMS` | SSMS 集成、菜单命令、UI | 核心业务逻辑 |
| `Installer` | SSMS 路径自动检测、文件复制、PkgDef 注册 | SSMS 宿主、UI |

### 1.2 模块依赖关系图

> **v1.1 勘误**：原版依赖图存在「AI → Core / Core → AI」隐含双向引用嫌疑，现明确为**单向链**。

```
Shared (netstandard2.0)
    ↓
Core (netstandard2.0)
    ↓
AI (netstandard2.0)
    ↓
SSMS (net472)
    ↑
Installer (net472, 仅依赖 Microsoft.Win32.Registry)
```

**依赖规则**：

- ✅ 允许：`Shared → Core → AI → SSMS` 单向下行
- ❌ 禁止：Core 引用 AI、Core 引用 SSMS
- ❌ 禁止：AI 引用 SSMS
- ❌ 禁止：任意循环依赖

**Installer 与 SSMS 解耦**：`Installer` 不引用 `SSMS.dll` 等业务程序集，仅通过文件系统操作完成部署；这一设计允许 Installer 独立分发、单独版本迭代。

### 1.3 项目引用链

SSMS 项目（`SSMSBoostPlus.SSMS.csproj`）中的核心引用：

| 引用项目 | 目标框架 | 说明 |
|---------|---------|------|
| `SSMSBoostPlus.Core.csproj` | netstandard2.0 | 核心业务逻辑 |
| `SSMSBoostPlus.AI.csproj` | netstandard2.0 | AI 桥接与提示 |
| `Microsoft.VisualStudio.Sdk` 17.14.40265 | - | VS SDK |
| `Microsoft.VSSDK.BuildTools` 17.14.2142 | - | VSCT 编译 |
| `Microsoft.SqlServer.SqlManagementObjects` 161.44091.41 | - | SQL Server 数据模型 |
| `Microsoft.Xaml.Behaviors.Wpf` 1.1.77 | - | WPF 行为 |
| `Markdig` 0.34.0 / `marked.js` 嵌入 | - | **v1.1 新增** Markdown 渲染（CS / JS 双引擎） |
| `DiffPlex` | - | 差异计算（行/字符级） |

> **v1.1 补充**：Installer 子项目（`SSMSBoostPlus.Installer.csproj`）**不引用任何业务项目**，仅依赖 `Microsoft.Win32.Registry` 5.0.0（用于读取 SSMS 安装路径）。

### 1.4 命名空间对应

| 命名空间 | 所在模块 | 职责 |
|---------|---------|------|
| `SSMSBoostPlus.SSMS` | SSMS | 扩展入口、命令处理、UI |
| `SSMSBoostPlus.SSMS.Commands` | SSMS | **14 个**命令处理器（含 VersionCheck / Diagnostic 等 v1.1 新增） |
| `SSMSBoostPlus.SSMS.Services` | SSMS | SSMS API 封装（编辑器/对话框/连接/右键菜单） |
| `SSMSBoostPlus.SSMS.Services.Connection` | SSMS | 连接信息多提供者获取 |
| `SSMSBoostPlus.SSMS.UI.Dialogs` | SSMS | XAML 对话框（Diff / Config / Warning / StreamResult / CommitDetail / VersionUpdate 等） |
| `SSMSBoostPlus.SSMS.UI.Chat` | SSMS | **v1.1 新增** AI 对话面板（`AIChatToolWindow` / `AIChatControl`） |
| `SSMSBoostPlus.SSMS.UI.Chat.Controls` | SSMS | **v1.1 新增** Markdown 渲染控件（`MarkdownViewer` / `TypingIndicator`） |
| `SSMSBoostPlus.Models` | Core | 配置数据模型 |
| `SSMSBoostPlus.Services` | Core | 通用服务（配置/数据库/日志/连接缓存） |
| `SSMSBoostPlus.VersionGuard` | Core | 版本守护核心 |
| `SSMSBoostPlus.SQLParser` | Core | SQL 解析（对象提取） |
| `SSMSBoostPlus.DiffEngine` | Core | Diff 计算 |
| `SSMSBoostPlus.Interfaces` | Core | 服务抽象接口 |
| `SSMSBoostPlus.AI.Bridge` | AI | API 桥接（CLI / HTTP / `BridgeFactoryService` 工厂） |
| `SSMSBoostPlus.AI.Context` | AI | **v1.1 新增** 数据库上下文构建器（Schema、表/列元数据） |
| `SSMSBoostPlus.AI.Interfaces` | AI | **v1.1 新增** 桥接/服务接口（`IBridgeFactory` / `IPromptTemplateManager`） |
| `SSMSBoostPlus.AI.Services` | AI | **v1.1 新增** `CodeAnalysisService` 所在命名空间 |
| `SSMSBoostPlus.AI.Prompts` | AI | 提示模板管理（5 套模板） |
| `SSMSBoostPlus.AI.Parsing` | AI | 响应解析 |
| `SSMSBoostPlus.AI.Models` | AI | 请求/响应模型 |
| `SSMSBoostPlus.Shared` | Shared | 共享枚举常量 |
| `SSMSBoostPlus.Installer` | Installer | **v1.1 新增** 独立命令行安装器 |

---

## 2. 架构设计

### 2.1 插件入口（SSMSBoostPlusPackage）

`src/SSMS/SSMSBoostPlusPackage.cs` 是插件的入口类，继承自 `AsyncPackage`。关键职责：

- **初始化时机**：SSMS 启动时自动加载
- **菜单注册**：通过 `VSCT` 定义和 `OleMenuCommandService` 注册命令
- **命令初始化**：在 `InitializeAsync` 中调用各命令的 `InitializeAsync` 方法注册 11 个命令处理器
- **PkgDef 注册**：GUID `a1b2c3d4-e5f6-7890-abcd-ef1234567890`，类名 `SSMSBoostPlus.SSMS.SSMSBoostPlusPackage`

### 2.2 命令注册机制（VSCT → CommandHandler）

#### VSCT 定义层

`SSMSBoostPlus.vsct` 使用 XML 定义菜单结构和命令。编译时 `VSCT.exe` 将 `.vsct` 编译为二进制 `.cto` 文件，嵌入 DLL 的 `VSTMANY"type="CTMenu"` 资源段。

**GUID 结构**：

| GUID 名称 | 值 | 用途 |
|-----------|---|------|
| `guidSSMSBoostPlusPkg` | `{a1b2c3d4-e5f6-7890-abcd-ef1234567890}` | 包标识 |
| `guidSSMSBoostPlusCmdSet` | `{c3d4e5f6-a7b8-9012-cdef-234567890123}` | 命令集 |
| `guidSHLMainMenu` | `{d309f791-903f-11d0-9efc-00a0c911004f}` | SSMS 主菜单 |
| `guidVSStd97` | `{5efc7975-14bc-11cf-9b2b-aa0057381919}` | 标准编辑器快捷键 |

**命令 ID**：> **v1.1 勘误**：原 1.0 文档中 `VersionGuardCheck` 与 `VersionGuardEnableToggle` 的 ID 写反，已与 `SSMSBoostPlus.vsct` 校对修正；并新增 AI 对话面板 / 版本检查 2 个命令。

| 命令 ID | 值 | 功能 | 快捷键 |
|--------|---|------|-------|
| `cmdidVersionGuardCheck` | 0x0100 | 检查脚本版本（手动触发 VersionGuard） | Ctrl+Shift+V |
| `cmdidVersionGuardEnableToggle` | 0x0101 | 启用/禁用版本守护 | — |
| `cmdidVersionGuardReloadFromDb` | 0x0102 | 从数据库重新加载 | — |
| `cmdidAISuggestions` | 0x0200 | AI 智能建议 | Ctrl+Shift+A |
| `cmdidAIComments` | 0x0201 | AI 添加注释 | Ctrl+Shift+C |
| `cmdidAIFormatting` | 0x0202 | AI 格式化代码 | Ctrl+Shift+F |
| `cmdidAIPerformance` | 0x0203 | AI 性能分析 | Ctrl+Shift+P |
| `cmdidAIChatPanel` | 0x0204 | **v1.1 新增** AI 对话面板（停靠工具窗口） | Ctrl+Shift+K |
| `cmdidAIConfig` | 0x0300 | AI 配置对话框 | — |
| `cmdidVersionGuardConfig` | 0x0301 | 版本守护设置 | — |
| `cmdidVersionCheck` | 0x0302 | **v1.1 新增** 插件自身版本检查与自动更新 | — |
| `cmdidDiagnostic` | 0x0200（内部）| **v1.1 新增** 错误诊断（菜单项） | — |

> 另：SQL 编辑器右键菜单上的 5 个 AI 命令由运行时 `WpfContextMenuService` 动态注册，与 `.vsct` 完全解耦（详见 2.6 节）。

#### 命令处理器层

每个命令对应一个继承自 `BaseCommand<T>` 的命令类（如 `AISuggestionsCommand`）。基类 `BaseCommand` 提供：

- `OnBeforeQueryStatus`：动态控制命令可见性（仅 SQL 文档时启用）
- `Execute`：入口方法，内部调用 `ExecuteAsync` 异步执行

典型执行流程（以 `AISuggestionsCommand` 为例）：

```
Execute(sender, e)
  → ExecuteAsync()
      1. 验证 SQL 文档
      2. 获取代码（选区或全文）
      3. 验证 AI 配置
      4. 获取数据库上下文
      5. 设置状态栏文本
      6. 创建 AI 桥接 → 调用 CodeAnalysisService.GetSuggestionsAsync
      7. 显示 Diff 对话框
      8. 用户接受 → 替换编辑器内容
      9. 清除状态栏
```

### 2.3 AI 桥接层（BridgeFactory → ICodebuddyBridge）

见 [第 5 章 AI 桥接层原理](#5-ai-桥接层原理)。

### 2.4 代码分析服务（CodeAnalysisService）

`src/AI/Services/CodeAnalysisService.cs` 是 AI 功能的中央协调器，封装了 **5 个** AI 分析场景（v1.1 新增 Chat）：

| 方法 | 功能 | Prompt 模板 | 渲染方式 |
|------|------|----------|---------|
| `GetSuggestionsAsync` | 智能建议 | `suggest.md` | Diff 对话框（行级） |
| `AddCommentsAsync` | 添加注释 | `comment.md` | Diff 对话框（行级） |
| `FormatCodeAsync` | 格式化代码 | `format.md` | Diff 对话框（行级） |
| `AnalyzePerformanceAsync` | 性能分析 | `analyze.md` | 流式弹窗（`AIStreamResultDialog`） |
| `ChatAsync` | **v1.1 新增** 对话式问答 | `chat.md` | 流式面板（`AIChatToolWindow`） |

> **v1.1 勘误**：原文档模板名为 `suggestions_prompt` 等，v1.1 已重命名为精简的 `suggest.md` / `comment.md` / `format.md` / `analyze.md` / `chat.md`（资源文件名 = 模板 ID）。

每个方法流程一致：

```
接收代码 → 构建 Prompt（含上下文/配置） → 调用 bridge.SendAsync
  → 解析响应 → 提取 SuggestedCode / Content
  → 返回 AnalysisResult
```

### 2.5 Diff 引擎（DiffCalculator）

`src/Core/DiffEngine/DiffCalculator.cs` 使用 `DiffPlex` 库计算两段文本的差异，返回 `DiffResult`（包含变更类型：Unchanged / Inserted / Deleted / Modified）。

SSMS 中通过 `AIDiffDialog`（WPF XAML）呈现 Diff 结果，左侧原始代码，右侧 AI 结果，支持行级颜色区分。

### 2.6 AI 对话面板与 Markdown 渲染（v1.1 新增）

#### 2.6.1 可停靠工具窗口 `AIChatToolWindow`

`src/SSMS/UI/Chat/AIChatToolWindow.cs` 是 SSMS 可停靠工具窗格（继承自 `ToolWindowPane`），通过 `[Guid("F8E9D8C7-...")]` + `ProvideToolWindow` 特性注册（详见 `SSMSBoostPlusPackage.cs`）。

**生命周期策略**（"关闭即销毁，下次打开为全新窗口"）：

| 场景 | 行为 |
|------|------|
| 用户点 X | 窗口被隐藏，不释放资源 |
| Ctrl+Shift+K 触发 | 检测窗口是否曾隐藏：隐藏→销毁旧 `AIChatControl` + `ChatSessionService` 重建；可见→保持会话 |
| VS 布局恢复 | `OnToolWindowCreated` 回调兜底初始化，避免占位文本"正在加载 AI 助手..."卡死 |

**静态服务引用**：`_package.InitializeAsync` 中将 `IServiceProvider` / `IConfigurationService` / `ILoggerService` 注入 `AIChatToolWindow.Static*` 属性，绕开 `ToolWindowPane` 构造时机过早导致的 DI 失败。

#### 2.6.2 聊天控件 `AIChatControl`

`src/SSMS/UI/Chat/AIChatControl.xaml(.cs)` 是面板主体：

- 顶部操作条：清空对话 / 重新生成 / 复制全部 / 导出 Markdown
- 中部对话流：`ScrollViewer` + `ItemsControl` 渲染消息（用户/AI/系统三类气泡）
- 底部输入区：多行 `TextBox` + 发送按钮 + 「停止生成」动态按钮
- 通过 `MarkdownViewer`（见 2.6.3）渲染 AI 回复
- 通过 `TypingIndicator`（见 2.6.4）展示流式输出中的"打字中"动画

#### 2.6.3 Markdown 渲染 `MarkdownViewer`（双引擎）

`src/SSMS/UI/Chat/Controls/MarkdownViewer.xaml.cs` 用 `WebView2` 承载 `marked.js`（嵌入资源）实现流式 Markdown 渲染：

1. AI 每推一段文本（含 `<think>` 思考过程）→ 通过 `InvokeScriptAsync` 调用 JS 函数 `appendStreamChunk(text)`
2. JS 端用 `marked.parse` 增量渲染 + 滚动到底部
3. 渲染完成通知通过 `WebMessageReceived` 回调回 C#

**为什么放弃手写解析器**：手写 Markdown 解析对代码块、表格、列表嵌套的处理在 SSE 流式追加场景下极易出 bug。`marked.js` 已是行业标准，体积小（≈ 30 KB），WebView2 缓存命中后开销可忽略。

#### 2.6.4 "打字中"指示器 `TypingIndicator`

`src/SSMS/UI/Chat/Controls/TypingIndicator.xaml.cs` 是一个轻量 XAML 控件（3 个跳动的小圆点动画），通过 `Storyboard` + `BeginAnimation` 循环播放。在 SSE 推流未到达首字节 / 推流间隔 > 200 ms 时显示。

#### 2.6.5 SQL 编辑器右键菜单：`WpfContextMenuService`（运行时动态方案）

`src/SSMS/Services/WpfContextMenuService.cs` 解决了 SSMS 22+ 中 **VSCT `CommandPlacement` 对 SQL 编辑器右键菜单完全无效** 的问题（`BeforeQueryStatus` 从不触发）。采用 **DTE `CommandBars` 运行时动态添加** 方案：

```csharp
// 核心代码节选
private const int CmdIdAiSuggestions  = 0x0200;
private const int CmdIdAiComments     = 0x0201;
private const int CmdIdAiFormatting   = 0x0202;
private const int CmdIdAiPerformance  = 0x0203;
private const int CmdIdAIChatPanel    = 0x0204;
private const int CmdIdAIConfig       = 0x0300;

private static readonly (int CommandId, string DisplayName, string Tag)[] AiCommands = {
    (CmdIdAiSuggestions,  "🤖 智能建议",     "SSMSBoostPlus_AI_Suggestions"),
    (CmdIdAiComments,     "🤖 AI智能注释",    "SSMSBoostPlus_AI_Comments"),
    (CmdIdAiFormatting,   "🤖 AI自动格式化",  "SSMSBoostPlus_AI_Formatting"),
    (CmdIdAiPerformance,  "🤖 AI性能分析",    "SSMSBoostPlus_AI_Performance"),
    (CmdIdAIChatPanel,    "💬 AI 对话面板",   "SSMSBoostPlus_AI_ChatPanel"),
    (CmdIdAIConfig,       "⚙ AI 配置...",     "SSMSBoostPlus_AI_Config"),
};
```

**初始化流程**：

1. 枚举所有 DTE `CommandBars`，匹配 SQL 编辑器上下文菜单
2. 通过 `DTE.CommandBars` API 调 `Add()` 注册 6 个 AI 命令项
3. 同一 `IWpfTextView` 用 `_processedViews` 哈希去重，避免重复注入
4. 关联 `BeforeQueryStatus` 控制仅在 SQL 文档启用

**已验证无效的方案**（见 `WpfContextMenuService.cs` 头注释）：

| 方案 | 失败原因 |
|------|----------|
| VSCT `CommandPlacement → guidSHLMainMenu/IDM_VS_CTXT_CODEWIN` | `BeforeQueryStatus` 从未触发 |
| VSCT `CommandPlacement → {5DD0BB59-...}/0x0100` | 同上 |
| WPF `ContextMenu` / `ContextMenuOpening` | SSMS 不走 WPF ContextMenu |
| `IOleCommandTarget ShowContextMenu` 拦截 | SSMS 不走此命令路由 |

> **演进建议**：未来若 SSMS 修复右键菜单事件链，优先回退到 VSCT 方案以减少运行时反射。

---

## 3. 核心模块详解

### 3.1 AI 配置数据模型（AIConfiguration）

`src/Core/Models/Configurations.cs` 中的 `AIConfiguration` 类：

```csharp
public class AIConfiguration
{
    public string ApiKey { get; set; }                      // API 密钥
    public string ApiEndpoint { get; set; }                 // OpenAI 兼容端点
    public string ModelName { get; set; }                   // 模型名称
    public double DefaultTemperature { get; set; } = 0.3;   // 创造性参数
    public int DefaultMaxTokens { get; set; } = 4000;      // 最大输出 token
    public int TimeoutSeconds { get; set; } = 120;         // 请求超时（秒）
    public FormatStyle DefaultFormattingStyle { get; set; } // 格式化风格
    public string CodebuddyCliPath { get; set; } = "";     // CLI 可执行文件路径（可选）
    public bool UseBearerPrefix { get; set; } = true;      // Authorization 头是否使用 "Bearer " 前缀
}
```

#### 内置默认 AP（AIDefaults）

```csharp
public static class AIDefaults
{
    public const string DefaultEndpoint = "http://10.32.2.50:7500/feishu/stream/event";
    public const string DefaultApiKey = "";
    public const string DefaultModelName = "";
    public const string UserPromptPrefix = "@SSMSBOOST @SSMSBOOST_SQL_性能分析SKILLS ";
}
```

> **v1.1 勘误**：原文档将前缀记为 `"@SSMSBOOST "`，实际值包含 **两个 token**（`@SSMSBOOST` 主标识 + `@SSMSBOOST_SQL_性能分析SKILLS` 性能分析场景提示），后端据此路由到对应 Skills。该前缀由 4 个 AI 命令（建议/注释/格式化/性能分析）统一在 `BuildRequest` 时前置拼装，**Chat 命令不使用**该前缀（由 `chat.md` 模板自带系统指令接管）。

当用户未配置 CLI 和 HTTP API 时，自动使用内置默认接口（零配置启动）。`IsAIConfigured()` 在 v1.1 起始终返回 `true`，避免命令在未配置环境下不可用。

### 3.2 版本守护配置（VersionGuardConfiguration）

```csharp
public class VersionGuardConfiguration
{
    public bool Enabled { get; set; } = true;                       // 总开关
    public GuardAction DefaultAction { get; set; } = GuardAction.AskUser;  // 默认行为
    public List<string> ExcludedObjects { get; set; } = new();      // 排除对象（Glob）
    public List<string> ExcludedDatabases { get; set; } = new() {   // 排除数据库
        "tempdb", "master", "model", "msdb"
    };
    public string AskToken { get; set; } = "#checklater";           // 询问令牌
    public string SkipToken { get; set; } = "#nosynccheck";         // 跳过令牌
    public bool TokenCaseSensitive { get; set; } = true;            // 令牌大小写敏感
    public bool AutoRefreshOnMismatch { get; set; } = false;        // 不匹配时自动刷新
}
```

### 3.3 日志配置（LoggingConfiguration）

```csharp
public class LoggingConfiguration
{
    public string LogLevel { get; set; } = "Debug";         // 日志级别
    public string LogDirectory { get; set; } = "%INSTALL_DIR%\\Logs"; // 日志目录
    public int RetentionDays { get; set; } = 30;           // 保留天数
    public int MaxFileSizeMB { get; set; } = 10;           // 单文件最大 MB
}
```

日志实现使用 `Serilog`，通过 `SerilogLoggerService` 提供 `Debug / Information / Warning / Error` 四个日志方法。

### 3.4 GuardAction 枚举含义

`GuardAction` 定义发现版本过时对象时的默认行为：

| 枚举值 | 含义 |
|--------|------|
| `StopExecution` | 停止执行（阻止脚本运行） |
| `AskUser`（默认） | 弹出对话框，询问用户选择 |
| `LogOnly` | 仅记录日志，不阻塞执行 |

### 3.5 FormatStyle 枚举含义

`SSMSBoostPlus.Shared.FormatStyle` 定义格式化风格：

| 枚举值 | 含义 |
|--------|------|
| `Microsoft`（默认） | SQL 关键字小写，Google SQL 风格 |
| `Google` | SQL 关键字大写 |

---

## 4. 版本守护核心算法

### 4.1 对象引用提取（ObjectExtractor）

`src/Core/SQLParser/ObjectExtractor.cs` 使用 Microsoft 的 `TSql150Parser`（`Microsoft.SqlServer.TransactSql.ScriptDom`）解析 T-SQL 代码。

解析流程：

```
SQL 文本
  → TSql150Parser.Parse()
  → TSqlFragment（语法树）
  → ObjectReferenceVisitor（自定义 Visitor）
  → List<ObjectReference>
```

`ObjectReferenceVisitor` 继承自 `TSqlFragmentVisitor`，访问语法树中的 `NamedTableReference`（表引用）、`ScalarFunctionReference`（标量函数）等节点，提取数据库对象引用。

### 4.2 数据库对象版本比对（VersionChecker）

`src/Core/VersionGuard/VersionChecker.cs` 的 `CheckVersionAsync` 方法是核心比对逻辑：

```
输入：SQL 脚本文本、脚本打开时间、连接字符串、当前数据库
输出：VersionCheckResult（包含过时对象列表或跳过原因）

算法步骤：
1. 检查版本守护是否启用 → 禁用则 Skipped
2. 检测特殊标记（#nosynccheck / #checklater）
3. ObjectExtractor.ExtractObjects() → 提取对象引用列表
4. 过滤排除的数据库和对象（ExcludedDatabases / ExcludedObjects）
5. 对每个对象执行数据库元数据查询：
   SELECT modify_date FROM sys.objects WHERE name = @objName AND schema_id = SCHEMA_ID(@schema)
6. 对比 modify_date 与 scriptOpenTime
7. 如有对象 modify_date > scriptOpenTime → HasOutdated = true
```

### 4.3 SQL Server 元数据查询

版本比对依赖 `sys.objects` 系统表中的 `modify_date` 和 `create_date` 字段。`src/Core/Services/DatabaseService.cs` 提供 `GetObjectModifyDateAsync` 方法查询单个对象：

```sql
SELECT modify_date FROM sys.objects
WHERE name = @name
  AND schema_id = SCHEMA_ID(@schema)
  AND type IN ('P','V','FN','IF','TF','U','TF')
```

### 4.4 特殊标记解析（#nosynccheck / #checklater）

`ObjectExtractor.DetectTokens` 在解析阶段扫描注释文本，支持：

| 标记 | 行为 |
|------|------|
| `#nosynccheck` | `SkipToken` → 完全跳过版本检查 |
| `#checklater` | `AskToken` → 弹出询问窗口（即使 `DefaultAction` 为其他值） |

标记支持大小写敏感配置（`TokenCaseSensitive`）。

---

## 5. AI 桥接层原理

### 5.1 BridgeFactory 自动选择逻辑

`src/AI/Bridge/BridgeFactoryService.cs` 中的 `CreateBestAvailableBridgeAsync` 方法实现 **3 级优先级**自动选择策略：

```
CreateBestAvailableBridgeAsync(config)

① CodebuddyCliPath 已配置？
   ├─是 → 检查 CLI 是否可用（IsCliAvailableAsync）
   │   ├─可用 → ✅ CodebuddyCliBridge（本地优先）
   │   └─不可用 → ⚠️ 日志降级，到②
   └─否 → 到②

② ApiEndpoint 已配置（用户自定义）？
   ├─是 → ✅ AIApiClientBridge（用户配置的 API）
   └─否 → 到③

③ 兜底 → ✅ AIApiClientBridge（内置默认 API）
           （AIDefaults.DefaultEndpoint / DefaultApiKey / DefaultModelName）
```

**决策日志输出**（搜索 `[BridgeFactory]`）：

| 日志 | 含义 |
|------|------|
| `✅ 使用用户配置的 CodeBuddy CLI` | ① 生效 |
| `⚠️ 配置的 CLI 路径不可用，回退到 HTTP API` | ① 失败 → ② |
| `使用用户配置的 HTTP API (Endpoint=...)` | ② 生效 |
| `使用内置默认 AI API (Endpoint=...)` | ③ 生效（零配置兜底） |

### 5.2 桥接实现

**CodebuddyCliBridge**（`src/AI/Bridge/CodebuddyCliBridge.cs`）：

通过本地命令行工具 `codebuddy` 发送请求，支持：
- 流式输出（Streaming）：SSE 逐 token 推送
- 非流式输出：一次性获取完整响应
- ReadOnly 模式：通过 System Prompt 追加只读指令
- 响应通过 `ResponseParser` 解析

**AIApiClientBridge**（`src/AI/Bridge/AIApiClientBridge.cs`）：

通过 HTTP POST 调用 OpenAI 兼容 API，支持：
- 标准 REST 调用（`ExecuteAsync`）
- SSE 流式输出（`ExecuteStreamAsync`）
- ReadOnly 模式：System Prompt 追加只读指令
- 使用 `System.Net.Http.HttpClient` 实现

### 5.3 配置方式

| 方式 | 配置项 | 数据模型 |
|------|--------|---------|
| **配置对话框** | 菜单 → AI 配置... | 保存到 `config.json` |
| **配置文件** | `appsettings.json` | `AIConfiguration` 节点 |
| **API 调用** | `BridgeFactory.CreateBestAvailableBridgeAsync(config)` | `AIConfiguration` 参数 |

配置持久化路径：`%LOCALAPPDATA%\SSMSBoostPlus\config.json`

### 5.4 支持的 API 类型

| 桥接类型 | 支持模型/后端 | 特性 |
|---------|-------------|------|
| `CodebuddyCliBridge` | CodeBuddy CLI | 流式输出、本地执行、无需 API Key |
| `AIApiClientBridge` | OpenAI 兼容接口 | DeepSeek / Claude / OpenAI 等 |

### 5.5 Prompt 模板管理（PromptTemplateManager）

`src/AI/Prompts/PromptTemplateManager.cs` 管理所有 AI 提示模板。模板以占位符形式嵌入，在调用前通过 `FillTemplate` 方法替换变量：

| 模板 ID | 文件 | 用途 | 关键占位符 | 渲染场景 |
|--------|------|------|-----------|---------|
| `suggest` | `src/AI/Prompts/Templates/suggest.md` | 智能建议 | `{code}`, `{context}` | Diff 对话框 |
| `comment` | `src/AI/Prompts/Templates/comment.md` | 添加注释 | `{code}` | Diff 对话框 |
| `format` | `src/AI/Prompts/Templates/format.md` | 格式化代码 | `{code}`, `{style}` | Diff 对话框 |
| `analyze` | `src/AI/Prompts/Templates/analyze.md` | 性能分析 | `{code}`, `{execution_plan}` | 流式弹窗 |
| `chat` | `src/AI/Prompts/Templates/chat.md` | **v1.1 新增** 对话问答 | `{history}`, `{user_message}`, `{schema}` | 流式面板 |

模板以 **嵌入 Markdown 资源** 为首选（`Prompts.analyze.md` 等），**硬编码降级** 为兜底（防止资源缺失导致命令完全不可用）。`FillTemplate` 方法统一处理 `{{ }}` 双花括号占位符替换。`IPromptTemplateManager` 接口在 v1.1 起对外暴露，便于测试和替换实现。

---

## 6. SSMS 集成细节

### 6.1 连接信息获取（ConnectionServiceFacade）

`src/SSMS/Services/Connection/ConnectionServiceFacade.cs` 是连接信息的多提供者聚合器，优先使用官方 API，失败时自动降级到反射方式。

**提供者列表**（按优先级）：

| 提供者 | 说明 |
|--------|------|
| `OfficialConnectionProvider` | 使用 SSMS 官方公开 API |
| `ReflectionConnectionProvider` | 通过反射访问内部服务 |

`GetConnectionInfoAsync()` 返回 `ConnectionInfo`（包含 ConnectionString、ServerInstance、DatabaseName、SourceProvider）。

### 6.2 编辑器服务（SsmsEditorService）

`src/SSMS/Services/SsmsEditorService.cs` 封装 SSMS 编辑器的 DTE API，提供：

| 方法 | 说明 |
|------|------|
| `IsSqlDocument()` | 当前活动文档是否为 SQL 文件 |
| `GetCodeAsync()` | 获取选中文本或全文，返回 `(code, isSelection)` |
| `GetActiveDocumentPath()` | 获取当前文档的磁盘路径 |
| `GetActiveDatabaseName()` | 获取当前连接的数据库名 |
| `GetActiveConnectionString()` | 获取当前连接字符串 |
| `ReplaceSelectionAsync(text)` | 替换选中文本 |
| `ReplaceAllAsync(text)` | 替换全文 |

### 6.3 对话框服务（SsmsDialogService）

`src/SSMS/Services/SsmsDialogService.cs` 提供对话框调用：

| 方法 | 说明 |
|------|------|
| `ShowDiffDialog(original, modified, title)` | 显示 Diff 对比对话框，返回 `DiffDialogResult` |
| `ShowVersionWarningDialog(result, code, getLatestFn)` | 显示版本警告对话框，返回用户选择 |

### 6.4 状态栏操作

通过 `IVsStatusbar`（SSMS 公开 API）操作状态栏：

```csharp
var statusBar = (IVsStatusbar)serviceProvider.GetService(typeof(SVsStatusbar));
statusBar.SetText("正在使用 AI 分析 SQL 代码...");  // 设置文本
statusBar.Clear();                                   // 清除文本
```

---

## 7. 构建与部署

### 7.0 构建命令（.slnx 方案，v1.1 勘误）

> **v1.1 勘误**：原文档的构建命令使用 `SSMSBoostPlus.sln`，**该文件已被企业加密软件加壳，`dotnet` 无法解析**。请改用 **`SSMSBoostPlus.slnx`**（XML 格式解决方案）。

```powershell
# ✅ 正确：使用 .slnx
dotnet build SSMSBoostPlus.slnx -c Release

# ❌ 禁止：.sln 会被加壳破坏
dotnet build SSMSBoostPlus.sln -c Release
```

**`.slnx` vs `.sln`**：`.slnx` 是 .NET SDK 9+ 引入的简化 XML 格式，无 GUID、无 ProjectSection、不需要 `Format Version 12.00` 头，更易于版本控制 diff 与工具链解析。

### 7.1 VSSDK 17.14 + .NET SDK 10 兼容性说明

当前环境使用：
- **VSSDK BuildTools**：17.14.2142
- **VisualStudio SDK Package**：17.14.40265
- **MSBuild / .NET SDK**：10.0（.NET 10）

存在已知兼容性问题，见 [7.4 节](#74-已知-bug-与-workaround)。

### 7.2 ManualPkgDef 机制（绕过 SDK bug）

**.NET SDK 10 的 `PrepareForRunDependsOn` 覆盖 bug**：`Microsoft.Common.CurrentVersion.targets` 行 4870 直接赋值覆盖 `PrepareForRunDependsOn`，擦除 VSSDK 设置的 `GeneratePkgDef;CopyPkgDef` 依赖链。

**解决方案**（`SSMSBoostPlus.SSMS.csproj`）：

```xml
<!-- 禁用 VSSDK 的 GeneratePkgDef 目标（任务程序集与 .NET SDK 10 不兼容） -->
<Target Name="GeneratePkgDef" />

<!-- Build 完成后调用 CreatePkgDef.exe CLI 生成 .pkgdef -->
<Target Name="ManualPkgDef" AfterTargets="Build">
  <Exec Command="&quot;$(CreatePkgDefExe)&quot; /out=&quot;$(IntermediateOutputPath)$(TargetName).pkgdef&quot; /codebase &quot;$(TargetPath)&quot;" />
  <Copy SourceFiles="$(IntermediateOutputPath)$(TargetName).pkgdef" DestinationFiles="$(OutDir)$(TargetName).pkgdef" />
</Target>
```

### 7.3 CTO 文件嵌入流程

VSCT 编译分两阶段：

```
第 1 次 Build：
  VSCT.exe 编译 SSMSBoostPlus.vsct
    → SSMSBoostPlus.cto（obj\Release\net472）

第 2 次 Build：
  Csc.exe 编译时，.cto 作为 EmbeddedResource 嵌入 SSMSBoostPlus.SSMS.dll
    → DLL 包含 VSTMANY"type="CTMenu" 资源段
```

**注意**：`csproj` 中 CTO 文件的 `EmbeddedResource` 必须在**静态 ItemGroup** 中声明（Target 内添加无效）。

### 7.4 已知 bug 与 workaround

| # | 问题 | workaround |
|---|------|-----------|
| 1 | .NET SDK 10 覆盖 `PrepareForRunDependsOn` | 使用 `ManualPkgDef`（见 7.2） |
| 2 | VSSDK 17.14 `CreatePkgDef` 任务报 VSSDK1032 | 改用 `CreatePkgDef.exe` CLI |
| 3 | VSSDK 17.14 `CreateMenuPkgDef` 因空路径崩溃 | 禁用该目标（空 `Target` 覆盖） |
| 4 | VSCT.exe 在 .NET SDK 10 下路径为空 | 预先生成 CTO，两阶段构建 |

### 7.5 PkgDef 注册机制

`SSMSBoostPlus.SSMS.pkgdef` 注册三项核心内容：

```reg
; 包注册
[$RootKey$\Packages\{a1b2c3d4-e5f6-7890-abcd-ef1234567890}]
@="SSMSBoostPlusPackage"
"Class"="SSMSBoostPlus.SSMS.SSMSBoostPlusPackage"
"CodeBase"="$PackageFolder$\SSMSBoostPlus.SSMS.dll"

; 自动加载包（SQL 编辑器激活时）
[$RootKey$\AutoLoadPackages\{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}]
"{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"=dword:00000002

; 菜单资源注册（文件模式 + DLL 内嵌双保险）
[$RootKey$\Menus]
"{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"=", Menus.ctmenu, 1"
```

部署后必须运行 `ssms.exe /setup` 强制 PkgDef 重新扫描合并到 `privateregistry.bin`。

### 7.6 部署清单（4 核心 DLL + 12 第三方依赖）

`deploy-to-ssms.bat`（需**管理员权限**）将 `bin\Release\net472\*` 全部复制到 SSMS 22 扩展目录。

**目标目录**（`deploy-to-ssms.bat` 内部路径已 v1.1 动态化，见 7.8 节）：

```
D:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus
```

**4 个核心 DLL**（含对应 `.pdb`）：

| 文件 | 说明 |
|------|------|
| `SSMSBoostPlus.SSMS.dll` | SSMS 扩展主程序（含 CTMenu 嵌入资源） |
| `SSMSBoostPlus.SSMS.pdb` | 主程序调试符号 |
| `Core.dll` | 核心业务逻辑层 |
| `Core.pdb` | Core 调试符号 |
| `AI.dll` | AI 服务封装层 |
| `AI.pdb` | AI 调试符号 |
| `Shared.dll` | 共享库 |
| `Shared.pdb` | Shared 调试符号 |

**12 个第三方依赖**（按文件名排序）：

| 文件 | 用途 |
|------|------|
| `DiffPlex.dll` | 行/字符级差异计算 |
| `Serilog.dll` | 结构化日志核心 |
| `Serilog.Sinks.File.dll` | 日志文件落盘 |
| `Serilog.Enrichers.Environment.dll` | 环境变量富化 |
| `Microsoft.Data.SqlClient.dll` | SQL Server 客户端 |
| `Microsoft.SqlServer.TransactSql.ScriptDom.dll` | T-SQL 解析 |
| `Microsoft.SqlServer.ScriptDom.dll` | ScriptDom 配套 |
| `Microsoft.SqlServer.SqlManagementObjects.dll` | SMO（版本守护用） |
| `Newtonsoft.Json.dll` | JSON 序列化 |
| `Microsoft.Bcl.AsyncInterfaces.dll` | IAsyncDisposable 兼容垫片 |
| `Microsoft.Extensions.Configuration*.dll` | 配置（Binder / Json / 根） |
| `Microsoft.Xaml.Behaviors.Wpf.dll` | WPF 行为（v1.1 起替代过时的 System.Windows.Interactivity） |
| `System.Net.Http.dll` | HTTP 客户端（net472 polyfill） |

**部署流程**（脚本内部分步骤）：

1. **Step 1**：检查 SSMS 是否运行，若运行则退出
2. **Step 2**：清空目标目录
3. **Step 3**：复制 `SSMSBoostPlus.SSMS.pkgdef`
4. **Step 4**：复制 4 核心 DLL + PDB
5. **Step 5**：注册 PkgDef（`regsvr32` 或 `VSReg.exe`）
6. **Step 6**：复制 12 第三方依赖
7. **Step 7**：验证部署完整性（文件存在性 + 版本号一致性）

### 7.7 独立安装器 `InstallSSMSBoostPlus.exe`（v1.1 新增）

`src/Installer/Program.cs` 是 net472 控制台 EXE，**不引用任何业务项目**，仅依赖 `Microsoft.Win32.Registry 5.0.0`。可直接双击运行或与 `deploy-to-ssms.bat` 互换使用。

**支持参数**：

| 参数 | 行为 |
|------|------|
| （无） | 交互式：检测 SSMS → 用户选择安装目录 → 复制文件 + 注册 PkgDef |
| `--download` | **v1.1 新增** 双击默认行为：从内网 Git 仓库 `Release` 分支拉取最新构建产物（git clone --depth 1 --branch Release）→ 自动安装 |
| `--install <path>` | 指定 SSMS 安装根目录直接安装（跳过检测） |
| `--uninstall` | 卸载（从注册表 + 扩展目录移除） |
| `--version` | 打印安装器版本 |

**SSMS 多版本自动检测**（按优先级枚举注册表）：

```
HKLM\SOFTWARE\Microsoft\SQL Server Management Studio\22.0
HKLM\SOFTWARE\Microsoft\SQL Server Management Studio\20.0
HKLM\SOFTWARE\Microsoft\SQL Server Management Studio\19.0
HKLM\SOFTWARE\Microsoft\SQL Server Management Studio\18.0
HKLM\SOFTWARE\Microsoft\SQL Server Management Studio\17.0
```

**双击在线下载安装流程**（`--download` 模式，由 `VersionCheckService` 在检测到新版本时调用）：

1. 解析 `--download` 参数 → 从内网 Git 仓库 `http://172.16.10.168/.../SSMSBoostPlus.git` 的 `Release` 分支 `git ls-remote` 拿到最新 commit
2. `git clone --depth 1 --branch Release <url> %TEMP%\sbp_install` 拉到临时目录
3. 复制产物到检测到的 SSMS 扩展目录
4. 写 `version_info.json`（包含新 commit hash）
5. 调用方（`VersionCheckCommand`）后续触发 `Environment.Exit(0)` 关闭 SSMS，让用户重新打开

> **v1.1 变更**：Installer 与 `SSMSBoostPlus.SSMS.dll` 完全解耦，可独立版本迭代；`version_info.json` 由 Installer 写、由 `VersionCheckService` 读，路径固定为 `%INSTALL_DIR%\version_info.json`。

### 7.8 SSMS 22 路径动态化（v1.1 新增）

`deploy-to-ssms.bat` 与 `InstallSSMSBoostPlus.exe` 在 v1.1 之前硬编码 `D:\Microsoft SQL Server Management Studio 22\Release\...`，用户机器若安装到其他盘符（如 `E:\`）会部署失败。v1.1 起统一改为 **注册表 + 默认值回退** 双策略：

```csharp
// 优先级：命令行参数 → 注册表检测 → 硬编码默认
string? installRoot =
    args.Length > 0 ? args[0] :
    ReadInstallRootFromRegistry("22.0") ??      // 优先 SSMS 22
    ReadInstallRootFromRegistry("20.0") ??      // 降级到 20
    @"D:\Microsoft SQL Server Management Studio 22\Release";  // 最终兜底
```

**注册表读取键**：

```
HKLM\SOFTWARE\Microsoft\SQL Server Management Studio\XX.0\InstallRoot
```

**典型场景**：

| 场景 | 行为 |
|------|------|
| SSMS 22 装在 `E:\` | 注册表读到 `E:\...` → 部署到 `E:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus` |
| SSMS 22 未装、只有 20 | 降级检测 20.0 |
| 都没有 | 用兜底路径并提示用户确认 |
| 用户传参 `--install E:\...` | 直接跳过检测 |

详细诊断与决策表见 `AIDoc/架构变更/2026-07-02-小H动态路径检测改造.md`。

---

---

## 8. 附录

### A. 关键 GUID 一览

| GUID | 用途 | 代码定义位置 |
|------|------|------------|
| `a1b2c3d4-e5f6-7890-abcd-ef1234567890` | 包 GUID（Package） | `SSMSBoostPlusPackage.PackageGuidString` |
| `c3d4e5f6-a7b8-9012-cdef-234567890123` | 命令集 GUID | `SSMSBoostPlus.vsct` / `SSMSBoostPlusPackage` |
| `e80ef1cb-6d64-4609-8faa-feacfd3bc89f` | 自动加载 Flag GUID | `SSMSBoostPlusPackage.AutoLoadFlag` |
| `C7875030-1F2E-4A78-BFEE-2789260B8E6B` | 右键上下文菜单 GUID | `SSMSBoostPlus.vsct` |

### B. 部署目录

扩展安装目录：`D:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus\`

部署内容：

| 文件 | 源路径 | 说明 |
|------|--------|------|
| `SSMSBoostPlus.SSMS.dll` | bin\Release\net472 | 主程序集 |
| `SSMSBoostPlus.SSMS.pdb` | bin\Release\net472 | 调试符号 |
| `SSMSBoostPlus.SSMS.pkgdef` | bin\Release\net472 | 包注册文件 |
| `SSMSBoostPlus.cto` | obj\Release\net472 | 菜单二进制（复制为 Menus.ctmenu） |
| 第三方 DLL | NuGet 缓存 | DiffPlex、Serilog 等 |

### C. 调试技巧

**日志调试**：

日志默认保存在扩展目录 `Logs\` 下，默认级别 `Debug`。通过配置 `LoggingConfiguration.LogLevel` 可调整为 `Information`（减少日志量）。

**ActivityLog**（SSMS 启动日志）：

SSMS 的 ActivityLog 位于 `%APPDATA%\Microsoft\AppEnv\ActivityLog.xml`，所有 VS 包加载事件均记录在此。时间戳为 UTC（本地时间需 +8h）。

**VS 调试附加**：

在 VS 中 Attach to Process → 选择 `Ssms.exe` → 设置断点 → 在 SSMS 中触发命令。

### D. 配置持久化路径

用户配置默认保存在：`%LOCALAPPDATA%\SSMSBoostPlus\config.json`

首次运行后自动创建。配置以 JSON 格式存储 `AIConfiguration` 和 `VersionGuardConfiguration`。
