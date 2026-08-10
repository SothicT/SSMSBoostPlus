---
name: ssms-menu-deploy
description: >
  SSMSBoostPlus 项目新增主菜单命令并完整部署的标准化流程。涵盖 VSCT 定义、Command 类创建、
  Package 注册、两次编译嵌入 ctmenu、全量文件部署、SSMS 缓存清除（privateregistry.bin）全链路。
  触发：新增菜单、添加菜单项、菜单不显示、部署菜单、ssms menu deploy、新增命令、菜单部署
---

# SSMSBoostPlus 新增菜单 & 部署 SKILL

## 适用范围

在 SSMSBoost++ 顶部主菜单中新增命令按钮（如"SQL 模板设置..."），并完整部署到 SSMS 22。

> **注意**：如需添加 SQL 编辑器**右键上下文菜单**，请使用 `ssms-context-menu` SKILL。

## 关键教训（踩坑总结）

| 问题 | 根因 | 解决方案 |
|------|------|----------|
| 菜单编译后不显示 | DLL 未嵌入 `Menus.ctmenu` 资源 | 两次编译：第一次生成 .cto，第二次嵌入 DLL |
| 菜单部署后不显示 | `privateregistry.bin` 缓存了旧菜单定义 | 删除 `privateregistry.bin` + 清除 ComponentModelCache |
| 菜单部署后仍不显示 | 部署脚本未复制 `Menus.ctmenu` 文件 | 部署脚本必须从 obj 目录复制 .cto 为 Menus.ctmenu |
| 点击菜单无反应 | Command 未在 Package 中注册 | 在 `InitializeAsync()` 中调用 `XxxCommand.InitializeAsync()` |

## 标准流程（6 步）

### Step 1: VSCT 定义命令

在 `src/SSMS/SSMSBoostPlus.vsct` 中添加三处：

#### 1a. IDSymbol（Symbols → guidSSMSBoostPlusCmdSet）

```xml
<!-- 在对应分组中添加，ID 必须全局唯一 -->
<IDSymbol name="cmdidMyNewCommand" value="0x0304"/>
```

**ID 分配规则**：

| 分组 | ID 范围 | 说明 |
|------|---------|------|
| Version Guard | 0x0100-0x010F | 版本守护 |
| CODE WITH AI | 0x0200-0x020F | AI 代码助手 |
| Configuration | 0x0300-0x030F | 配置类（设置、检查更新等） |
| Menu IDs | 0x1000+ | 菜单容器 |
| Group IDs | 0x2000+ | 分组容器 |

#### 1b. Button（Commands → Buttons）

```xml
<!-- 放在 <Buttons> 节点内，与其他 Button 同级 -->
<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidMyNewCommand" priority="0x0260" type="Button">
  <Parent guid="guidSSMSBoostPlusCmdSet" id="Group_Config"/>
  <Strings>
    <ButtonText>📋 我的命令...</ButtonText>
    <CommandName>MyNewCommand</CommandName>
  </Strings>
</Button>
```

**关键属性**：
- `priority`：控制菜单内排序，数值越小越靠前
- `Parent`：决定命令出现在哪个菜单分组
  - `Group_CodeWithAI`：AI 代码助手分组
  - `Group_VersionGuard`：版本守护分组
  - `Group_Config`：配置分组（设置类命令）
- `ButtonText`：中文显示文本，建议加 emoji 前缀
- 如需动态显示/隐藏，添加 `<CommandFlag>DynamicVisibility</CommandFlag>`
- 如需快捷键，在 `<KeyBindings>` 中添加绑定

### Step 2: 创建 Command 类

在 `src/SSMS/Commands/` 下创建 `MyNewCommand.cs`：

```csharp
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 我的命令。
/// VSCT: cmdidMyNewCommand = 0x0304
/// </summary>
public sealed class MyNewCommand : BaseCommand<MyNewCommand>
{
    private ILoggerService _logger = new ConsoleLoggerService();
    // 按需添加服务依赖字段

    private MyNewCommand(AsyncPackage package, OleMenuCommandService commandService)
        : base(package, commandService, 0x0304)  // ⚠️ 必须与 VSCT IDSymbol value 一致
    {
    }

    public static async Task InitializeAsync(
        AsyncPackage package,
        ILoggerService logger
        /* 其他依赖服务参数 */)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new MyNewCommand(package, commandService!)
        {
            _logger = logger,
            // 初始化其他依赖
        };
    }

    protected override void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command == null) return;
        command.Visible = true;
        command.Enabled = true;
    }

    protected override void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // 实现命令逻辑
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "执行命令失败");
        }
    }
}
```

**关键要点**：
- 继承 `BaseCommand<T>`（CRTP 模式），自动处理 CommandID 注册
- 构造函数的 `commandId` 参数**必须与 VSCT IDSymbol value 完全一致**
- `InitializeAsync` 是静态方法，在 Package 中调用
- `Instance` 单例由基类管理

### Step 3: Package 注册

在 `src/SSMS/SSMSBoostPlusPackage.cs` 的 `InitializeAsync()` 中添加：

```csharp
// 在 "Register configuration commands" 区域附近添加
await Commands.MyNewCommand.InitializeAsync(this, logger /*, 其他依赖 */);
```

**位置参考**（按现有代码结构）：
- Version Guard 命令区域（约 L134-138）
- CODE WITH AI 命令区域（约 L141-144）
- SQL Tools 命令区域（约 L155-156）
- Configuration 命令区域（约 L159-164）

### Step 4: 编译（两次编译法）

> **关键**：由于 .NET SDK 10 + VSSDK 17.14 的 `MergeCtoResource` target 不被调用，
> .cto 文件在编译阶段才生成，无法在同一次编译中嵌入 DLL。
> 必须两次编译：第一次生成 .cto，第二次将 .cto 嵌入 DLL。

```powershell
# 第一次编译：生成 .cto（DLL 此时不含 Menus.ctmenu 资源）
dotnet build SSMSBoostPlus.slnx -c Release

# 第二次编译：.cto 已存在，静态 ItemGroup 将其嵌入 DLL
dotnet build SSMSBoostPlus.slnx -c Release
```

**验证 DLL 是否包含嵌入资源**：

```powershell
[System.Reflection.Assembly]::LoadFile('src\SSMS\bin\Release\net472\SSMSBoostPlus.SSMS.dll').GetManifestResourceNames()
```

输出必须包含 `Menus.ctmenu`。如果不包含，检查 csproj 中的静态 `ItemGroup`：

```xml
<!-- 此 ItemGroup 在 MSBuild 评估阶段读取 .cto，因此需要两次编译 -->
<ItemGroup Condition="Exists('$(IntermediateOutputPath)SSMSBoostPlus.cto')">
  <EmbeddedResource Include="$(IntermediateOutputPath)SSMSBoostPlus.cto">
    <LogicalName>Menus.ctmenu</LogicalName>
    <Visible>false</Visible>
  </EmbeddedResource>
</ItemGroup>
```

**同时验证 .cto 文件大小**：

```powershell
Get-Item 'src\SSMS\obj\Release\net472\SSMSBoostPlus.cto' | Select-Object Name, Length, LastWriteTime
```

### Step 5: 部署（全量 + 缓存清除）

#### 5a. 检查 SSMS 是否关闭

```powershell
tasklist /FI "IMAGENAME eq ssms.exe" 2>nul | find /I "ssms.exe"
```

如有输出，必须先关闭 SSMS。

#### 5b. 部署所有文件

使用 `deploy-current.bat`（管理员权限运行），或手动部署以下文件：

**核心文件**（从 `bin\Release\net472`）：

| 文件 | 说明 |
|------|------|
| SSMSBoostPlus.SSMS.dll | 主程序 DLL（含嵌入的 Menus.ctmenu） |
| SSMSBoostPlus.SSMS.pdb | 调试符号 |
| Core.dll | 核心功能层 |
| Core.pdb | 调试符号 |
| AI.dll | AI 服务层 |
| AI.pdb | 调试符号 |
| Shared.dll | 共享库 |
| Shared.pdb | 调试符号 |

**配置文件**（从 `obj\Release\net472`）：

| 文件 | 源路径 | 目标文件名 |
|------|--------|-----------|
| SSMSBoostPlus.SSMS.pkgdef | obj 目录 | SSMSBoostPlus.SSMS.pkgdef |
| SSMSBoostPlus.cto | obj 目录 | **Menus.ctmenu**（重命名！） |

**第三方依赖**（从 `bin\Release\net472`）：

```
DiffPlex.dll, Serilog.dll, Serilog.Sinks.File.dll, Serilog.Enrichers.Environment.dll,
Microsoft.Data.SqlClient.dll, Newtonsoft.Json.dll, Microsoft.Bcl.AsyncInterfaces.dll,
Microsoft.Extensions.Configuration.dll, Microsoft.Extensions.Configuration.Json.dll,
Microsoft.Extensions.Configuration.Binder.dll, Microsoft.SqlServer.TransactSql.ScriptDom.dll,
System.Net.Http.dll, Markdig.dll, Markdown.Xaml.dll,
Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.Wpf.dll, WebView2Loader.dll
```

**部署目标目录**：
```
D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus
```

#### 5c. 清除 SSMS 缓存（关键！）

```powershell
# 1. 删除私有注册表缓存（SSMS 从此文件读取合并后的 pkgdef，包括菜单定义）
$vsBase = "C:\Users\$env:USERNAME\AppData\Local\Microsoft\VisualStudio\18.0_f46ef89a"
Copy-Item "$vsBase\privateregistry.bin" "$vsBase\privateregistry.bin.bak" -Force
Remove-Item "$vsBase\privateregistry.bin" -Force

# 2. 清除 ComponentModelCache
Remove-Item "$vsBase\ComponentModelCache\*" -Recurse -Force -ErrorAction SilentlyContinue
```

> **注意**：`18.0_f46ef89a` 是 SSMS 22 的缓存目录名（基于 VS 18.0 Shell）。
> `_f46ef89a` 后缀可能因环境不同而变化，使用 `Get-ChildItem` 查找实际目录名。

#### 5d. 刷新配置标记

```powershell
$configFlag = "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\extensions.configurationchanged"
Set-Content -Path $configFlag -Value "$(Get-Date)"
```

### Step 6: 验证

1. **启动 SSMS 22**
2. **检查菜单**：顶部菜单栏 → SSMSBoost++ → 确认新菜单项出现
3. **检查日志**：

```powershell
# 查看最新日志
Get-ChildItem "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus\Logs" -Filter "ssmsboostplus-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

日志中应看到：
- `SSMSBoostPlus 包初始化成功。` — 包加载成功
- 新命令的 `InitializeAsync` 被调用（如有日志）

4. **点击菜单项**：确认功能正常执行

## 快速部署脚本模板

```powershell
# deploy-menu.ps1 — 新增菜单后的完整部署脚本
$ErrorActionPreference = "Stop"

$SRC = "d:\172.31.1.44\AI\SSMSBoostPlus\src\SSMS\bin\Release\net472"
$OBJ = "d:\172.31.1.44\AI\SSMSBoostPlus\src\SSMS\obj\Release\net472"
$DST = "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus"
$VS_CACHE = "C:\Users\$env:USERNAME\AppData\Local\Microsoft\VisualStudio"

# 1. 检查 SSMS
$ssms = Get-Process ssms -ErrorAction SilentlyContinue
if ($ssms) { Write-Error "SSMS 正在运行，请先关闭！"; exit 1 }

# 2. 复制核心 DLL + PDB
@("SSMSBoostPlus.SSMS","SSMSBoostPlus.Core","SSMSBoostPlus.AI","SSMSBoostPlus.Shared") | ForEach-Object {
    Copy-Item "$SRC\$_.dll" "$DST\" -Force
    Copy-Item "$SRC\$_.pdb" "$DST\" -Force -ErrorAction SilentlyContinue
}

# 3. 复制 pkgdef + ctmenu（关键！）
Copy-Item "$OBJ\SSMSBoostPlus.SSMS.pkgdef" "$DST\" -Force
Copy-Item "$OBJ\SSMSBoostPlus.cto" "$DST\Menus.ctmenu" -Force  # ⚠️ 重命名！

# 4. 复制第三方依赖
@("DiffPlex","Serilog","Serilog.Sinks.File","Serilog.Enrichers.Environment",
  "Microsoft.Data.SqlClient","Newtonsoft.Json","Microsoft.Bcl.AsyncInterfaces",
  "Microsoft.Extensions.Configuration","Microsoft.Extensions.Configuration.Json",
  "Microsoft.Extensions.Configuration.Binder","Microsoft.SqlServer.TransactSql.ScriptDom",
  "System.Net.Http","Markdig","Markdown.Xaml") | ForEach-Object {
    Copy-Item "$SRC\$_.dll" "$DST\" -Force -ErrorAction SilentlyContinue
}

# 5. 清除 SSMS 缓存（关键！）
$cacheDir = Get-ChildItem $VS_CACHE -Directory | Where-Object { $_.Name -like "18.0_*" } | Select-Object -First 1
if ($cacheDir) {
    $cachePath = $cacheDir.FullName
    Copy-Item "$cachePath\privateregistry.bin" "$cachePath\privateregistry.bin.bak" -Force -ErrorAction SilentlyContinue
    Remove-Item "$cachePath\privateregistry.bin" -Force -ErrorAction SilentlyContinue
    Remove-Item "$cachePath\ComponentModelCache\*" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "✅ SSMS 缓存已清除: $cachePath"
}

# 6. 刷新配置标记
$configFlag = "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\extensions.configurationchanged"
Set-Content -Path $configFlag -Value "$(Get-Date)"

# 7. 验证
Write-Host "`n=== 部署验证 ==="
$asm = [System.Reflection.Assembly]::LoadFile("$DST\SSMSBoostPlus.SSMS.dll")
$resNames = $asm.GetManifestResourceNames()
if ($resNames -contains "Menus.ctmenu") {
    Write-Host "✅ DLL 包含 Menus.ctmenu 嵌入资源"
} else {
    Write-Host "⚠️  DLL 未包含 Menus.ctmenu 嵌入资源（文件模式仍可工作）"
}
Write-Host "✅ Menus.ctmenu 文件: $(Get-Item "$DST\Menus.ctmenu" | Select-Object -ExpandProperty Length) bytes"
Write-Host "✅ pkgdef 文件: $(Test-Path "$DST\SSMSBoostPlus.SSMS.pkgdef")"
Write-Host "`n请启动 SSMS 22 验证菜单。"
```

## 故障排查

| 症状 | 检查项 | 解决方案 |
|------|--------|----------|
| 菜单完全不显示 | DLL 是否含 Menus.ctmenu 资源 | 两次编译法重新编译 |
| 菜单完全不显示 | Menus.ctmenu 文件是否部署 | 从 obj 目录复制 .cto 并重命名 |
| 菜单完全不显示 | privateregistry.bin 是否清除 | 删除私有注册表缓存 |
| 菜单完全不显示 | VSCT 编译是否有错误 | 检查编译日志中 VSCT 相关输出 |
| 点击菜单无反应 | Command 是否注册 | 检查 Package.InitializeAsync() 中调用 |
| 点击菜单无反应 | CommandId 是否匹配 | 对比 VSCT IDSymbol value 和构造函数参数 |
| 菜单显示旧版本 | 部署目录 DLL 时间戳 | 确认 DLL 是最新编译的版本 |
| 日志无命令记录 | InitializeAsync 是否执行 | 检查 Package 初始化日志和异常 |
