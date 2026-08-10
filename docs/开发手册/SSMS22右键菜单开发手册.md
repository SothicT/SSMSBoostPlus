# SSMS 22 SQL 编辑器右键菜单开发手册

> **适用版本**：SSMS 22（基于 Visual Studio 2022 Shell）  
> **最后更新**：2026-06-12  
> **状态**：✅ 已验证通过

---

## 一、核心结论

### 1.1 SSMS 22 SQL 编辑器右键菜单的正确名称

| 属性 | 值 |
|------|------|
| **DTE CommandBars 名称** | `SQL Files Editor Context` |
| **索引位置** | 119（在所有 CommandBars 中的序号） |
| **控件数量** | 48 个（含剪切/复制/粘贴/连接等标准项） |
| **菜单类型** | `msoBarTypePopup`（弹出式上下文菜单） |

### 1.2 必须避开的陷阱

| ❌ 错误方案 | 原因 |
|-------------|------|
| `CommandBars["Code Window"]` | **这是通用代码编辑器的菜单**（含"标记命令"、"片段"、"大纲显示"等），不是 SQL 编辑器右键菜单！ |
| VSCT `CommandPlacement` 到 `guidSHLMainMenu / IDM_VS_CTXT_CODEWIN` | BeforeQueryStatus 从未被调用——VS Shell 没有在 SQL 编辑器右键菜单中查询此 GUID 的命令 |
| VSCT `CommandPlacement` 到 `{5DD0BB59-...}/0x0100` | 同上，BeforeQueryStatus 从未触发 |
| WPF `ContextMenu` / `ContextMenuOpening` 事件 | SSMS 不使用 WPF ContextMenu 来显示右键菜单 |
| `IOleCommandTarget.ShowContextMenu` 拦截 | SSMS 不走此命令路由 |

---

## 二、技术架构

### 2.1 整体方案：DTE CommandBars 运行时动态添加

**唯一可行的方案**：通过 `EnvDTE.DTE` 的 `CommandBars` 自动化模型，在运行时找到 `SQL Files Editor Context` 并动态添加菜单项。

```
┌──────────────────────────────────────────────────────────┐
│                    SSMSBoostPlusPackage                   │
│                     (AsyncPackage)                       │
│                                                          │
│  InitializeAsync()                                      │
│    ├─ 注册命令（VSCT 方式 → 主菜单 + 快捷键）            │
│    └─ RunDiagnosticAfterShellInitialized()               │
│         ├─ 轮询 DTE 可用性（最长 15 秒）                  │
│         ├─ RegisterExecutionInterceptor()                 │
│         └─ AddWpfContextMenuAsync()                       │
│              └─ WpfContextMenuService.InitializeAsync()   │
│                   ├─ 枚举所有 CommandBars                 │
│                   ├─ 找到 SQL 编辑器上下文菜单             │
│                   └─ Controls.Add() 添加 AI 命令         │
└──────────────────────────────────────────────────────────┘
```

### 2.2 关键流程图

```
Package 初始化
    │
    ▼
等待 DTE 可用（轮询，最长15秒）
    │
    ▼
获取 DTE 服务 ──────── 失败 → 放弃（日志记录）
    │
    ▼
获取 CommandBars 集合 ── 失败 → 放弃
    │
    ▼
遍历所有 CommandBars (约334个)
    │
    ├─ 筛选 isPopup = msoBarTypePopup
    │
    ├─ 按名称匹配优先级列表：
    │   1. "SQL Files Editor Context"  ← ⭐ 最高优先级
    │   2. "Code Window"               ← 备选
    │   3. "Sql Editor Context"        ← 备选
    │   ...
    │
    ▼ 找到目标菜单
    │
    ├─ 清理残留项（Tag 前缀匹配，从后往前删）
    │
    ├─ Controls.Add(Type=1, Id=cmdId, Temporary=true)
    │   对每个 AI 命令：
    │   ├─ 0x0200 → 🤖 智能建议
    │   ├─ 0x0201 → 🤖 AI智能注释
    │   ├─ 0x0202 → 🤖 AI自动格式化
    │   ├─ 0x0203 → 🤖 AI性能分析
    │   ├─ 0x0204 → 💬 AI 对话面板
    │   └─ 0x0300 → ⚙ AI 配置...
    │
    ▼
注册 WindowEvents 监听 SQL 文档切换
    │
    ▼
注册 WPF PreviewMouseRightButtonDown 诊断事件
    │
    ▼
完成！右键菜单已可用
```

---

## 三、代码实现详解

### 3.1 包入口：初始化调用链

**文件**：`src/SSMS/SSMSBoostPlusPackage.cs`

```csharp
protected override async Task InitializeAsync(...)
{
    // 1. 注册命令（VSCT 方式 → 主菜单栏 + 快捷键）
    await Commands.AISuggestionsCommand.InitializeAsync(this, logger, configService);
    await Commands.AICommentsCommand.InitializeAsync(this, logger, configService);
    // ... 其他命令 ...

    // 2. SSMS 22 专属：等待 Shell 初始化后添加右键菜单
    if (ssmsVersion >= SSMSVersion.V22)
    {
        await RunDiagnosticAfterShellInitialized(logger);
    }
}

private async Task RunDiagnosticAfterShellInitialized(ILoggerService logger)
{
    // 轮询 DTE 服务，确认 Shell 已初始化（最长等待15秒）
    for (int i = 0; i < 15; i++)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await GetServiceAsync(typeof(DTE)) as DTE;
        if (dte != null)
        {
            // DTE 就绪后注册右键菜单
            await AddWpfContextMenuAsync(logger);
            return;
        }
        await Task.Delay(1000);
    }
}

private async Task AddWpfContextMenuAsync(ILoggerService logger)
{
    var wpfService = new Services.WpfContextMenuService(this, logger);
    await wpfService.InitializeAsync();
}
```

**关键点**：
- `WpfContextMenuService.InitializeAsync()` 必须在 DTE 可用之后调用
- DTE 只有在 Shell 完全初始化后才可获取
- 所有操作必须在 UI 线程执行（`SwitchToMainThreadAsync()`）

### 3.2 核心服务：WpfContextMenuService

**文件**：`src/SSMS/Services/WpfContextMenuService.cs`

#### 3.2.1 命令定义

```csharp
private static readonly Guid CommandSetGuid = new("c3d4e5f6-a7b8-9012-cdef-234567890123");

// 命令 ID 必须与 VSCT 中定义的完全一致！
private const int CmdIdAiSuggestions = 0x0200;
private const int CmdIdAiComments = 0x0201;
private const int CmdIdAiFormatting = 0x0202;
private const int CmdIdAiPerformance = 0x0203;
private const int CmdIdAIChatPanel = 0x0204;
private const int CmdIdAIConfig = 0x0300;

// Tag 前缀用于清理残留项
private const string TagPrefix = "SSMSBoostPlus_AI_";

private static readonly (int CommandId, string DisplayName, string Tag)[] AiCommands = new[]
{
    (CmdIdAiSuggestions,  "🤖 智能建议",    TagPrefix + "Suggestions"),
    (CmdIdAiComments,     "🤖 AI智能注释",   TagPrefix + "Comments"),
    (CmdIdAiFormatting,   "🤖 AI自动格式化", TagPrefix + "Formatting"),
    (CmdIdAiPerformance,  "🤖 AI性能分析",   TagPrefix + "Performance"),
    (CmdIdAIChatPanel,   "💬 AI 对话面板",  TagPrefix + "ChatPanel"),
    (CmdIdAIConfig,      "⚙ AI 配置...",    TagPrefix + "Config"),
};
```

**⚠️ 关键约束**：`CommandId` 必须与 VSCT 中的 `IDSymbol` 值一致，否则点击菜单项不会触发命令处理器！

#### 3.2.2 初始化流程

```csharp
public async System.Threading.Tasks.Task InitializeAsync()
{
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    // 防止重复初始化
    if (_isInitialized) return;

    // 步骤1: 获取 DTE
    var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
    if (dte == null) return;

    // 步骤2: 枚举 CommandBars，找到 SQL 编辑器上下文菜单
    var sqlEditorMenu = EnumerateAndFindContextMenu(dte);

    // 步骤3: 添加 AI 命令到找到的菜单
    if (sqlEditorMenu != null)
    {
        AddAICommandsToMenu(sqlEditorMenu);
    }

    // 步骤4: 注册 WindowEvents 监听文档切换
    dte.Events.WindowEvents.WindowActivated += OnWindowActivated;

    _isInitialized = true;
}
```

#### 3.2.3 枚举与发现

```csharp
private dynamic EnumerateAndFindContextMenu(DTE dte)
{
    var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
    
    // 遍历所有 CommandBars，筛选 Popup 类型
    for (int i = 1; i <= commandBars.Count; i++)
    {
        var bar = commandBars[i];
        bool isPopup = (bar.Type == MsoBarType.msoBarTypePopup);
        if (isPopup)
        {
            // 记录日志，帮助诊断
            _logger.Information($"上下文菜单 [{i}]: '{bar.Name}' (Controls={bar.Controls.Count})");
        }
    }
    
    // 按优先级匹配
    return TryFindContextMenu(commandBars, sqlRelatedMenus);
}
```

#### 3.2.4 优先级匹配（⭐ 核心逻辑）

```csharp
private dynamic TryFindContextMenu(CommandBars commandBars, List<...> sqlRelatedMenus)
{
    // ⭐⭐⭐ 关键优先级列表！
    string[] priorityNames = new string[]
    {
        "SQL Files Editor Context",     // ⭐⭐⭐ SSMS 22 SQL 查询编辑器右键菜单
        "Code Window",                  // VS 通用代码编辑器上下文菜单（不是 SQL 编辑器的）
        "Sql Editor Context",           // SSMS Plus 使用的名称（可能不存在）
        "SQL Editor Context",
        "Editor Context Menus",         // 编辑器上下文菜单容器
        "Query Editor Context Menu",
        "Text Editor Context",
    };

    // 按优先级逐个尝试
    foreach (var name in priorityNames)
    {
        var menu = commandBars[name];
        if (menu != null) return menu;
    }
    
    // 降级：从 SQL 相关菜单中选择
    // ...
    return null;
}
```

#### 3.2.5 添加菜单项

```csharp
private bool AddCommandToMenu(dynamic contextMenu, int commandId, string displayName, string tag)
{
    var controls = contextMenu.Controls;
    
    // Controls.Add(Type, Id, Parameter, Before, Temporary)
    var control = controls.Add(
        1,               // Type: msoControlButton
        commandId,       // Id: 命令的数值 ID（对应 VSCT 中定义的命令）
        Type.Missing,    // Parameter
        Type.Missing,    // Before（添加到末尾）
        true             // Temporary：SSMS 关闭后自动清理，下次初始化时重新添加
    );
    
    control.Caption = displayName;
    control.Tag = tag;       // 用于清理残留项
    control.Visible = true;
    
    return true;
}
```

**Controls.Add 参数详解**：

| 参数 | 类型 | 说明 |
|------|------|------|
| Type | int | `1` = `msoControlButton`（按钮），`10` = `msoControlPopup`（子菜单） |
| Id | int | 命令的数值 ID，**必须与 VSCT 中的 IDSymbol 值一致** |
| Parameter | object | 通常传 `Type.Missing` |
| Before | object | 插入位置索引，`Type.Missing` = 添加到末尾 |
| Temporary | bool | `true` = 临时项，SSMS 关闭后自动清理 |

#### 3.2.6 清理残留项

```csharp
private int CleanupExistingItems(dynamic contextMenu)
{
    var controls = contextMenu.Controls;
    // 从后往前删除（避免索引变化问题）
    for (int i = controls.Count; i >= 1; i--)
    {
        var ctrl = controls[i];
        string tag = ctrl.Tag ?? "";
        if (tag.StartsWith(TagPrefix))  // 匹配 "SSMSBoostPlus_AI_"
        {
            ctrl.Delete();
        }
    }
}
```

---

## 四、VSCT 配置

### 4.1 命令定义（VSCT 中必须有对应的 IDSymbol）

**文件**：`src/SSMS/SSMSBoostPlus.vsct`

```xml
<GuidSymbol name="guidSSMSBoostPlusCmdSet" value="{c3d4e5f6-a7b8-9012-cdef-234567890123}">
  <!-- CODE WITH AI group (0x0200-0x020F) -->
  <IDSymbol name="cmdidAISuggestions"    value="0x0200"/>
  <IDSymbol name="cmdidAIComments"       value="0x0201"/>
  <IDSymbol name="cmdidAIFormatting"     value="0x0202"/>
  <IDSymbol name="cmdidAIPerformance"    value="0x0203"/>
  <IDSymbol name="cmdidAIChatPanel"      value="0x0204"/>
  
  <!-- Configuration group (0x0300-0x030F) -->
  <IDSymbol name="cmdidAIConfig"         value="0x0300"/>
</GuidSymbol>
```

### 4.2 命令注册（OleMenuCommandService）

每个命令必须通过 `OleMenuCommandService` 注册，否则 `Controls.Add(Type=1, Id=commandId)` 创建的菜单项点击后不会触发任何操作。

```csharp
// 在 Package.InitializeAsync() 中注册
await Commands.AISuggestionsCommand.InitializeAsync(this, logger, configService);
await Commands.AICommentsCommand.InitializeAsync(this, logger, configService);
// ... 其他命令 ...
```

每个 Command 类内部使用 `OleMenuCommandService.AddCommand()` 注册，包含：
- `CommandID`：GUID + 数值 ID（与 VSCT 一致）
- `MenuCommand`：`OleMenuCommand` 实例，绑定 `BeforeQueryStatus` 和 `Invoke` 回调

### 4.3 CommandPlacement（VSCT 方式仍然需要保留）

虽然 `IDM_VS_CTXT_CODEWIN` 的 `CommandPlacement` 在 SSMS 22 SQL 编辑器右键菜单中不生效（BeforeQueryStatus 从不触发），但仍需保留：
1. **主菜单栏**的命令显示依赖 VSCT 定义
2. **快捷键绑定**依赖 VSCT 的 `KeyBindings` 段
3. DTE 方式添加的菜单项需要对应的 Command ID 才能触发命令

---

## 五、SSMS 22 CommandBars 枚举参考

### 5.1 完整上下文菜单列表（SSMS 22 实测）

以下是 SSMS 22 中所有 `msoBarTypePopup` 类型的 CommandBars，按索引排序（部分列举）：

| 索引 | 名称 | 控件数 | 用途 |
|------|------|--------|------|
| 12 | Code Window | 25 | 通用代码编辑器右键菜单（标记命令、片段、大纲显示等） |
| 119 | **SQL Files Editor Context** | **48** | **⭐ SQL 查询编辑器右键菜单**（剪切/复制/粘贴/连接等） |
| ... | ... | ... | 其他上下文菜单 |

### 5.2 "Code Window" vs "SQL Files Editor Context" 对比

| 属性 | Code Window | SQL Files Editor Context |
|------|-------------|--------------------------|
| **索引** | 12 | 119 |
| **控件数** | 25 | 48 |
| **内容** | 标记命令、打开文件、片段、大纲显示 | 剪切、复制、粘贴、连接、Open Prompt AI |
| **触发场景** | 通用代码编辑器右键 | **SQL 查询编辑器右键** |
| **是否目标** | ❌ 否 | ✅ **是** |

### 5.3 如何自行发现正确的 CommandBar 名称

如果 SSMS 版本变化或菜单名称变化，可以使用以下诊断方法：

```csharp
// 在 WpfContextMenuService.InitializeAsync() 中添加诊断日志
var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
for (int i = 1; i <= commandBars.Count; i++)
{
    var bar = commandBars[i];
    if (bar.Type == MsoBarType.msoBarTypePopup)
    {
        _logger.Information($"上下文菜单 [{i}]: '{bar.Name}' (Controls={bar.Controls.Count})");
        
        // 记录前几个控件标题，帮助确认菜单用途
        for (int j = 1; j <= Math.Min(bar.Controls.Count, 5); j++)
        {
            _logger.Information($"  [{j}] '{bar.Controls[j].Caption}'");
        }
    }
}
```

---

## 六、部署与调试

### 6.1 编译

```powershell
cd d:\Code\SSMSBoostPlusPub
dotnet build src\SSMS\SSMSBoostPlus.SSMS.csproj -c Release
```

### 6.2 部署步骤

```powershell
# 1. 关闭 SSMS
Stop-Process -Name 'SSMS' -Force -ErrorAction SilentlyContinue

# 2. 复制编译产物
Copy-Item 'src\SSMS\bin\Release\net472\SSMSBoostPlus.SSMS.dll' '<SSMS扩展目录>\SSMSBoostPlus.SSMS.dll' -Force

# 3. 刷新 PkgDef 缓存
echo 2026-06-12 > "<SSMS扩展目录>\..\extensions.configurationchanged"
& "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe" /setup

# 4. 启动 SSMS 验证
& "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe"
```

### 6.3 验证检查清单

- [ ] 日志中出现 `[右键菜单] === 开始初始化 ===` → 服务已启动
- [ ] 日志中出现 `CommandBars 总数: 334` → DTE 可用
- [ ] 日志中出现 `✅ 按名称找到: 'SQL Files Editor Context'` → 找到正确菜单
- [ ] 日志中出现 `✅✅ AI 命令添加完成: 6/6 项` → 菜单项添加成功
- [ ] SSMS 中打开 SQL 脚本，右键点击编辑器空白处 → 出现 AI 菜单项

### 6.4 常见问题排查

| 问题 | 可能原因 | 解决方法 |
|------|---------|---------|
| 右键菜单没有 AI 项 | DTE 未就绪就尝试添加 | 检查 DTE 轮询是否超时 |
| 右键菜单没有 AI 项 | CommandBar 名称不匹配 | 查看日志中枚举结果，更新优先级列表 |
| 点击 AI 菜单项无反应 | CommandId 与 VSCT 不一致 | 检查 `Controls.Add` 的 Id 参数值 |
| 右键菜单出现重复项 | 残留项未清理 | 检查 Tag 前缀匹配和清理逻辑 |
| 部署后仍是旧代码 | 编译产物未正确复制 | 检查 DLL 时间戳是否为最新 |

---

## 七、扩展新命令步骤

### 7.1 添加新的 AI 命令到右键菜单

**Step 1：在 VSCT 中定义命令 ID**

```xml
<!-- SSMSBoostPlus.vsct -->
<IDSymbol name="cmdidAINewFeature" value="0x0205"/>
```

**Step 2：在 VSCT 中添加 Button 定义**

```xml
<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidAINewFeature" priority="0x0260" type="Button">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_CodeWithAI"/>
  <CommandFlag>DynamicVisibility</CommandFlag>
  <Strings>
    <ButtonText>🤖 AI新功能</ButtonText>
    <CommandName>AINewFeature</CommandName>
  </Strings>
</Button>
```

**Step 3：创建命令类**

```csharp
// src/SSMS/Commands/AINewFeatureCommand.cs
internal sealed class AINewFeatureCommand : BaseCommand
{
    public static async Task InitializeAsync(AsyncPackage package, ILoggerService logger, IConfigurationService config)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        var cmdId = new CommandID(CommandSet, 0x0205);  // ← 对应 VSCT 中的 ID
        var cmd = new OleMenuCommand(Execute, cmdId);
        cmd.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService.AddCommand(cmd);
    }
    // ...
}
```

**Step 4：在 WpfContextMenuService 中添加菜单项**

```csharp
// 更新命令数组
private const int CmdIdAINewFeature = 0x0205;

private static readonly (int CommandId, string DisplayName, string Tag)[] AiCommands = new[]
{
    // ... 现有命令 ...
    (CmdIdAINewFeature, "🤖 AI新功能", TagPrefix + "NewFeature"),  // ← 新增
};
```

**Step 5：在 Package 中注册命令**

```csharp
await Commands.AINewFeatureCommand.InitializeAsync(this, logger, configService);
```

**Step 6：编译 → 部署 → 验证**

---

## 八、技术原理深度解析

### 8.1 为什么 VSCT CommandPlacement 不生效？

VS Shell 的命令路由机制如下：

```
用户右键点击 → VS Shell 查找对应上下文菜单 → 显示菜单项
                                        ↑
                    通过 CommandPlacement 的 Parent GUID/ID 定位
```

对于 SQL 编辑器，SSMS 使用了自定义的上下文菜单（`SQL Files Editor Context`），而不是 VS 标准的 `IDM_VS_CTXT_CODEWIN`。因此，VSCT 中声明的 `CommandPlacement` 到 `IDM_VS_CTXT_CODEWIN` 不会被查询。

### 8.2 为什么 DTE CommandBars 方案可以生效？

DTE CommandBars API 直接操作 VS Shell 内部的命令栏对象模型，**绕过了 VSCT 的声明式机制**：

```
DTE.CommandBars["SQL Files Editor Context"]
    → 获取实际的 CommandBar 对象
    → Controls.Add() 直接向此 CommandBar 添加控件
    → SSMS 显示此菜单时，新添加的项自然出现
```

### 8.3 Temporary=true 的意义

- `Temporary=true` 意味着菜单项不会持久化到注册表
- SSMS 关闭后菜单项消失，下次启动时由代码重新添加
- 这避免了"菜单项越积越多"的问题
- 但也意味着必须在每次 Package 初始化时都调用添加逻辑

### 8.4 Controls.Add 的 Id 参数如何触发命令？

当用户点击菜单项时：
1. VS Shell 获取菜单项的 `Id` 值（如 `0x0200`）
2. 结合 CommandSet GUID（`{c3d4e5f6-...}`），构造 `CommandID`
3. 在 `OleMenuCommandService` 中查找匹配的已注册命令
4. 调用该命令的 `Invoke` 处理器

**因此，`Controls.Add` 的 `Id` 参数必须与 `OleMenuCommandService.AddCommand` 注册的 CommandID 一致！**

---

## 九、参考资源

| 资源 | 说明 |
|------|------|
| [DTE CommandBars API](https://learn.microsoft.com/en-us/dotnet/api/envdte.commandbars) | 微软官方文档 |
| [VSCT CommandPlacement](https://learn.microsoft.com/en-us/visualstudio/extensibility/commandplacement-element) | 声明式菜单放置（SSMS SQL 编辑器中不生效） |
| [SSMS Plus 开源项目](https://github.com/akarzazi/SSMSPlus) | 使用 `CommandBars["Sql Editor Context"]`（名称与 SSMS 22 不同） |
| [vsix.guide Context Menus](https://vsix.guide/context-menus/) | VS 扩展上下文菜单指南 |
| [EnableVSIPLogging](https://learn.microsoft.com/en-us/visualstudio/extensibility/internals/enabling-diagnostic-logging) | 通过注册表启用 VSIP 日志，发现上下文菜单 GUID |

---

## 十、变更历史

| 日期 | 变更 | 作者 |
|------|------|------|
| 2026-06-12 | 初始版本，基于 SSMS 22 实测验证 | 小Q |
| 2026-06-12 | 确认 `SQL Files Editor Context` 为正确菜单名称 | 小Q |
