---
name: build-and-deploy
description: "Use when compiling or deploying the SSMSBoostPlus SSMS 22 extension project. Triggers: 编译项目、构建、build、部署、deploy、发布、打包、重新编译、编译并部署。仅适用于 SSMSBoostPlus 项目。"
---

# SSMSBoostPlus 编译与部署 (v6 — VSSDK 自动管道 + 手动备用)

将 SSMSBoostPlus 项目从源码编译并部署到 SSMS 22 的完整流水线。

**当前版本核心变更**:
- csproj 使用 VSSDK 自动管道（GeneratePkgDefFile=true），pkgdef 由 CreatePkgDef.exe 生成
- 手动构建管线 Target 已注释保留在 csproj 中，作为备用方案
- 新增 AI 项目引用（SSMS → AI → Core + Shared），四项目结构
- 部署文件清单已更新：4 个项目 DLL + 14 个第三方依赖
- SSMS 安装路径: `D:\Microsoft SQL Server Management Studio 22\Release\`（无 Program Files 前缀；注意含 `Release\` 子目录，SSMS.exe 位于 `Release\Common7\IDE\`）
- 工作区路径: `F:\2.C#_Core\SSMSBoostPlus`

## 项目关键信息

| 项 | 值 |
|---|---|
| 解决方案 | `SSMSBoostPlus.slnx`（优先，.sln 被企业加密软件加壳） |
| 目标框架 | net472 (SSMS 主项目), netstandard2.0 (子项目) |
| 依赖链 | Shared ← Core/AI ← SSMS（Shared 最底层；Core 和 AI 引用 Shared；SSMS 直接引用 Core 和 AI） |
| 项目数 | 4 个: SSMS / Core / AI / Shared |
| 编译配置 | Release (部署用) / Debug (调试用) |
| 编译命令 | `dotnet restore SSMSBoostPlus.slnx && dotnet build SSMSBoostPlus.slnx -c Release` |
| 部署目标 | `D:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus` |
| 部署脚本 | `deploy-to-ssms.bat` (需管理员权限) |
| NuGet 配置 | 项目级 `nuget.config`，内部 HTTP 源需 `allowInsecureConnections="true"` |
| Package GUID | `a1b2c3d4-e5f6-7890-abcd-ef1234567890`（pkgdef 与代码必须一致） |
| AutoLoad GUID | `e80ef1cb-6d64-4609-8faa-feacfd3bc89f` |

## 架构：VSSDK 自动管道 + 手动备用

### 当前状态：使用 VSSDK 自动管道

csproj 当前采用 **VSSDK 自动管道** 进行编译，`<GeneratePkgDefFile>` 设为 `true`。

```
dotnet build 执行流程（当前激活）：
┌─────────────────────────────────────────────────────┐
│  VSCTCompile (自动管道)                              │
│  ├── VSCT.exe 编译 .vsct → .cto                      │
│  └── 输出到 obj/Release/net472/                       │
├─────────────────────────────────────────────────────┤
│  CoreCompile: C# 编译                                │
│  ├── VSSDK 自动合并 CTO 到 DLL                        │
│  └── 生成 SSMSBoostPlus.SSMS.dll + Core/AI/Shared   │
├─────────────────────────────────────────────────────┤
│  GeneratePkgDef (自动管道, 已启用):                   │
│  └── CreatePkgDef.exe → .pkgdef                      │
├─────────────────────────────────────────────────────┤
│  输出物:                                             │
│  ├── bin/Release/net472/*.dll (4个项目DLL + 依赖)    │
│  ├── obj/Release/net472/*.pkgdef                     │
│  └── obj/Release/net472/*.cto (需部署时复制为ctmenu)  │
└─────────────────────────────────────────────────────┘
```

> ⚠️ **VSSDK 自动管道的已知限制**: CreatePkgDef.exe 生成的 pkgdef 可能存在格式 bug。
> 如遇扩展无法加载，用 `diagnosis/fix-pkgdef.ps1` 后处理修复，或切换回手动管线。

### VSSDK 自动管道已知 Bug 备查

以下 Bug 出现于 VSSDK BuildTools 的 CreatePkgDef.exe，如扩展加载失败可对照排查：

| # | Bug | 自动管道输出 | 正确值 |
|---|-----|-------------|--------|
| 1 | 包名截断 | `"SSMSBoostPlus"` | `"SSMSBoostPlusPackage"` |
| 2 | 路径变量错误 | `$WinDir*` | `$WinDir$` |
| 3 | 多余 Assembly 行 + 缺少 AllowsBackgroundLoad | 有 Assembly 行，无 AllowsBackgroundLoad | 无 Assembly 行，有 `AllowsBackgroundLoad=dword:00000001` |
| 4 | 多余空 ToolWindows 节 | 存在空 `[...ToolWindows]` 节 | 应删除 |
| 5 | 全局子键引用符错误 | `$RootKey%`（所有子键行） | `$RootKey$` |
| 6 | Menus 注册模式错误 | `",1000,1"`（嵌入 ID 模式） | `", Menus.ctmenu, 1"`（文件模式） |
| 7 | AutoLoad 值错误 | `dword:00000001` | `dword:00000002`（BackgroundLoad） |

### 手动管线（已注释保留，备用方案）

csproj 第 91-109 行保留了手动管线的 Target 定义（注释状态）。切换步骤：

1. 将 `<GeneratePkgDefFile>true</GeneratePkgDefFile>` 改为 `false`
2. 取消注释 ManualVSCTCompile / ManualPkgDef / CopyCtoAsMenuFile Target
3. 取消注释 CTO 嵌入静态 ItemGroup（第 72-77 行）
4. 执行两阶段构建（第 1 次 build 生成 cto → 第 2 次 build 嵌入 DLL）

手动管线使用 `diagnosis/generate-pkgdef.ps1` here-string 模板生成正确格式的 pkgdef，绕过所有 bug。

### 关键组件说明

**1. VSCTCompile（自动管道驱动）**
- 由 VSSDK targets 内置逻辑调用 VSCT.exe 编译 `.vsct` → `.cto`
- 输出路径: `obj/Release/net472/SSMSBoostPlus.cto`
- VSSDK BuildTools PackageReference 版本: **17.8.2273**（csproj 第 30 行）
- 解压后的 tools 内部版本: **17.8.2365**（Import 路径中使用，第 81 行）

**2. CTO 部署（双保险机制）**
- 自动管道通过 MergeCtoResource 步骤将 CTO 嵌入 DLL
- 部署脚本同时将 `.cto` 复制为 `Menus.ctmenu` 到 Extensions 目录
- 双保险: DLL 内嵌资源 + 外部 ctmenu 文件

**3. PkgDef 生成**
- 当前由 CreatePkgDef.exe 自动生成（GeneratePkgDefFile=true）
- 输出: `obj/Release/net472/SSMSBoostPlus.SSMS.pkgdef`
- 如遇崩溃或格式 bug，切换到手动的 generate-pkgdef.ps1

**4. 项目结构（四项目）**
```
SSMS (net472) ──→ Core (netstandard2.0) ──→ Shared (netstandard2.0)
  └─────────────→ AI (netstandard2.0) ──→ Core + Shared
```

## 流水线流程

```
Step 1: 环境预检
  ├── 确认 .slnx 文件存在
  ├── 确认 dotnet CLI 可用
  └── 确认项目结构完整 (src/Shared, src/Core, src/AI, src/SSMS)
          │
Step 2: dotnet restore
  └── dotnet restore SSMSBoostPlus.slnx
          │
Step 3: dotnet build（编译 + VSCT编译 + pkgdef生成）
  └── dotnet build SSMSBoostPlus.slnx -c Release
      ├── VSCTCompile: .vsct → .cto ✅ (自动管道)
      ├── CoreCompile: C# 编译 → 4个DLL ✅
      └── GeneratePkgDef: CreatePkgDef.exe → .pkgdef ✅ (自动管道)

     ┌────┴────┐
     │ 编译失败 │ ──→ 分析错误 → 修复代码 → 回到 Step 3
     └────┬────┘
          │ 编译成功
Step 4: 产物验证
  ├── SSMSBoostPlus.SSMS.dll ✓ (必须)
  ├── SSMSBoostPlus.Core.dll ✓ (必须)
  ├── SSMSBoostPlus.AI.dll ✓ (必须，已添加 ProjectReference)
  ├── SSMSBoostPlus.Shared.dll ✓ (必须)
  ├── SSMSBoostPlus.SSMS.pkgdef ✓ (必须，自动管道生成)
  └── obj/.../SSMSBoostPlus.cto ✓ (必须，部署时复制为 Menus.ctmenu)
  产物路径: src\SSMS\bin\Release\net472\ (DLL)
            src\SSMS\obj\Release\net472\ (pkgdef, cto)
          │
Step 5: 部署到 SSMS 22
  ├── 命令: 以管理员身份运行 deploy-to-ssms.bat
  ├── 脚本自动完成: 检查产物(4DLL) → 清理冲突目录 → 复制4DLL+4pdb → 复制配置(pkgdef/ctmenu/vsixmanifest) → 复制依赖(14项) → 刷新配置
  └── 若终端无管理员权限 → 提示用户以管理员身份运行
          │
Step 6: 强制刷新 PkgDef 缓存 + 重启 SSMS
  ├── ssms.exe /setup (强制重新扫描合并 pkgdef 到私有注册表)
  └── 提醒用户重启 SSMS 22 验证扩展加载
```

## 参数化控制

用户可通过自然语言指定不同模式：

| 用户意图 | 执行范围 |
|---|---|
| "编译项目" / "编译并部署" | 完整流水线 Step 1-6 |
| "仅编译" / "只编译不部署" | Step 1-4，跳过部署 |
| "Debug编译" | Step 1-4，配置改为 Debug |
| "部署" / "重新部署" | 跳过编译，直接执行 Step 5-6 (前提: 产物已存在) |
| "干净重建" / "rebuild" | Step 1 前 clean，然后正常流程 |

## 编译命令参考

```powershell
# 标准编译 (Release) — 必须使用 .slnx（.sln 被加密加壳）
dotnet restore SSMSBoostPlus.slnx
dotnet build SSMSBoostPlus.slnx -c Release

# Debug 编译
dotnet build SSMSBoostPlus.slnx -c Debug

# 干净重建
dotnet clean SSMSBoostPlus.slnx -c Release
dotnet restore SSMSBoostPlus.slnx
dotnet build SSMSBoostPlus.slnx -c Release

# 仅编译指定子项目
dotnet build src/SSMS/SSMSBoostPlus.SSMS.csproj -c Release

# 两阶段构建（仅手动管线模式需要，切换后首次使用）
# 第 1 次: 生成 CTO
dotnet build SSMSBoostPlus.slnx -c Release
# 第 2 次: 将 CTO 嵌入 DLL（静态 ItemGroup 生效）
dotnet build SSMSBoostPlus.slnx -c Release
```

## 部署命令参考

```powershell
# 部署 (需管理员权限)
# 方式1: 直接运行脚本
.\deploy-to-ssms.bat

# 方式2: 通过 PowerShell 以管理员身份启动
Start-Process powershell -Verb RunAs -ArgumentList "-File `"$PWD\deploy-to-ssms.bat`""

# 部署后强制刷新 PkgDef 缓存（必须执行）
& "D:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe" /setup
```

## 关键脚本文件

| 脚本 | 路径 | 用途 |
|------|------|------|
| generate-pkgdef.ps1 | `diagnosis/generate-pkgdef.ps1` | 用 here-string 模板生成正确格式的 pkgdef（手动管线模式） |
| fix-pkgdef.ps1 | `diagnosis/fix-pkgdef.ps1` | pkgdef 后处理修复脚本（自动管道输出有 bug 时备用） |
| compare_files.ps1 | `diagnosis/compare_files.ps1` | 对比生成的 pkgdef/ctmenu 与参考文件 |
| deploy-to-ssms.bat | 项目根目录 | 部署到 SSMS 22 Extensions 目录（含4项目DLL+14依赖） |

## 部署文件完整清单

### 核心 DLL（4 个，deploy-to-ssms.bat Step 4）
| 文件 | 来源项目 | 必须 |
|------|---------|------|
| SSMSBoostPlus.SSMS.dll | src/SSMS | 是 |
| SSMSBoostPlus.Core.dll | src/Core | 是 |
| SSMSBoostPlus.AI.dll | src/AI | 是 |
| SSMSBoostPlus.Shared.dll | src/Shared | 是 |

### 第三方依赖（14 个，deploy-to-ssms.bat Step 6）
| 文件 | 来源 | 说明 |
|------|------|------|
| DiffPlex.dll | Core NuGet | SQL 差异对比引擎 |
| Serilog.dll | Core NuGet | 日志框架 |
| Serilog.Sinks.File.dll | Core NuGet | 文件日志输出 |
| Serilog.Enrichers.Environment.dll | Core NuGet | 环境信息日志增强 |
| Microsoft.Data.SqlClient.dll | Core NuGet | SQL Server 数据驱动 |
| Newtonsoft.Json.dll | Core/AI NuGet | JSON 序列化 |
| Microsoft.Bcl.AsyncInterfaces.dll | Core/AI NuGet | 异步基础库 |
| Microsoft.Extensions.Configuration.dll | Core NuGet | 配置框架 |
| Microsoft.Extensions.Configuration.Json.dll | Core NuGet | JSON 配置源 |
| Microsoft.Extensions.Configuration.Binder.dll | Core NuGet | 配置绑定 |
| Microsoft.SqlServer.TransactSql.ScriptDom.dll | Core NuGet | T-SQL 解析器 |
| System.Net.Http.dll | Core NuGet | HTTP 基础库 |

### 配置文件（deploy-to-ssms.bat Step 5）
| 文件 | 来源位置 | 说明 |
|------|---------|------|
| SSMSBoostPlus.SSMS.pkgdef | obj/Release/net472/ 或 obj/Release/ | 注册表合并脚本 |
| Menus.ctmenu | 从 obj/.../.cto 复制而来 | 命令表二进制（文件模式） |
| extension.vsixmanifest | src/SSMS/源码 | VSIX 清单 |

## pkgdef 正确格式参考

```
[$RootKey$\Packages\{a1b2c3d4-e5f6-7890-abcd-ef1234567890}]
@="SSMSBoostPlusPackage"
"InprocServer32"="$WinDir$\SYSTEM32\MSCOREE.DLL"
"Class"="SSMSBoostPlus.SSMS.SSMSBoostPlusPackage"
"CodeBase"="$PackageFolder$\SSMSBoostPlus.SSMS.dll"
"AllowsBackgroundLoad"=dword:00000001
[$RootKey$\AutoLoadPackages\{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}]
"{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"=dword:00000002
[$RootKey$\Menus]
"{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"=", Menus.ctmenu, 1"
```

**格式要点**:
- `$RootKey$` — 子键前缀（不是 `%` 或 `*`）
- `$WinDir$` — Windows 路径变量（不是 `%` 或 `*`）
- `\{guid\}` — 花括号需转义（pkgdef 格式要求）
- `, Menus.ctmenu, 1` — 文件模式菜单资源引用（不是 ID 模式）
- `dword:00000002` — BackgroundLoad 标志值（不是 01）

## 常见错误处理

| 错误类型 | 典型信息 | 处理方式 |
|---|---|---|
| NU1104 | 找不到包/源 | 检查 NuGet 源配置，确认 Microsoft.VSSDK 包可用 |
| CS0246 | 缺少类型/命名空间 | 检查 ProjectReference 是否完整（应有 Core + AI），子项目是否已编译 |
| CS1705 | 版本不匹配 | 检查 TargetFramework 一致性 (netstandard2.0 vs net472) |
| XML 解析错误 | csproj 第 N 行无效字符 | MSBuild Exec 内联 PowerShell 引号冲突 → 改用外部 .ps1 脚本文件 |
| CreatePkgDef.exe 崩溃 | ArgumentNullException | GeneratePkgDefFile=false 时会崩溃 → 当前设为 true 可避免；或改用手动的 generate-pkgdef.ps1 |
| VSCT 编译失败 | .vsct 语法错误 | 检查 vsct 源码和 VSCT.exe 路径是否正确 |
| 脚本找不到 | diagnosis/ 路径 404 | csproj 从 src/SSMS 引用需 `..\..\diagnosis`（上两级） |
| 部署权限不足 | 拒绝访问 | 以管理员身份运行 deploy-to-ssms.bat |
| 扩展未加载 | SSMS 启动后无扩展 | ① 检查 GUID 一致性 ② 运行 ssms.exe /setup ③ 查 ActivityLog ④ 对比 pkgdef 格式与上方参考 |

## 踩坑经验库（持续积累）

### MSBuild + PowerShell 集成
- **MSBuild Exec 内联 PowerShell 必须避免**: PowerShell 的 `$` 变量、引号嵌套会与 XML/cmd 解析多层冲突，几乎无法正确转义。**解决方案: 统一使用外部 .ps1 脚本文件 + `-File` 参数调用**
- **PowerShell here-string `@'...'@` 是生成包含特殊字符文本的最佳方式**: 完全避免 `$`、`\`、`"` 等字符的转义问题

### CreatePkgDef.exe 限制
- 在 `GeneratePkgDefFile=false` 时会因内部 `ArgumentNullException`（source 为 null，Where() 调用处）崩溃
- 在 `GeneratePkgDefFile=true` 时能运行但输出可能存在 7 处格式 bug
- **建议**: 当前保持 `GeneratePkgDefFile=true` 使用自动管道；如发现 bug 则用 `fix-pkgdef.ps1` 后处理或切换手动管线

### CTO 嵌入 DLL（手动管线模式相关）
- .NET SDK 风格项目中，Target **内**的动态 ItemGroup 对 EmbeddedResource **不生效**（MSBuild 评估顺序问题）
- 必须使用**静态 ItemGroup** + `Condition="Exists(...)"`
- 结果: 手动管线需要**两阶段构建**（第 1 次生成 cto，第 2 次才嵌入）

### ctmenu 二进制差异
- 同一 vsct 源码在不同 VSCT 编译器版本下产生的二进制产物**不同**（即使文件大小相同）
- 属于**正常现象**，不影响功能
- SSMS 通过 CFCT 魔术字节头识别格式，不校验具体内容哈希

### 部署后验证
- **必须**运行 `ssms.exe /setup` 强制 PkgDef 重新扫描合并到私有注册表
- 私有注册表实际路径: `HKU\<用户SID>\Software\Microsoft\SSMS\22.0_79c71968_Config\`
  - 注意: 是 `_Config` 后缀，**不是** `_Configuration`
- ActivityLog.xml 路径: `%APPDATA%\Microsoft\Ssms\22.0_79c71968\Configration\ActivityLog.xml`
  - ActivityLog 时间为 **UTC**，本地时间需 +8h

### csproj 路径技巧
- 从 `src/SSMS/` 引用项目根的 `diagnosis/` 目录: `$(MSBuildProjectDirectory)\..\..\diagnosis`
- 从 `src/SSMS/` 引用项目根的 `diagnosis/` 目录下的文件时，Exec Command 中用相对路径同样需要上两级

### .sln 被加密加壳 → 必须用 .slnx
- `.sln` 文件被企业加密软件加壳后，`dotnet restore` 和 `dotnet build` 均会失败（exit code 1，无详细错误）
- **解决方案**: 始终使用 `.slnx` 格式：`dotnet build SSMSBoostPlus.slnx -c Release`（build 自动触发 restore）
- `.slnx` 是 .NET SDK 原生支持的解决方案格式，与 .sln 功能等价

## 关键注意事项

- **当前使用 VSSDK 自动管道**: `dotnet build` 一条命令完成编译 + pkgdef + ctmenu 生成
- **手动管线已注释备用**: 切换方法见上方"手动管线"章节
- **四项目结构**: SSMS → Core + AI，AI/Core → Shared。部署需全部 4 个 DLL
- **pkgdef GUID 一致性**: 部署前验证 pkgdef 中的 Package GUID 与代码 `[Guid]` 一致（`a1b2c3d4-e5f6-7890-abcd-ef1234567890`）
- **CTO 双保险部署**: DLL 内嵌入 + 外部 Menus.ctmenu 文件两种模式同时存在
- **extensions.configurationchanged**: 部署脚本自动更新此时间戳触发 SSMS 重扫扩展
- **Package.cs 属性要求**:
  ```csharp
  [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
  [ProvideAutoLoad("{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}", PackageAutoLoadFlags.BackgroundLoad)]
  [ProvideMenuResource("Menus.ctmenu", 1)]
  ```
- **VSSDK BuildTools 版本号说明**: PackageReference 为 **17.8.2273**（NuGet 包版本），解压后 tools 内部为 **17.8.2365**（Import 路径版本）。文档历史中出现的 18.5.40034 是旧版工具的版本号，已不再使用。
