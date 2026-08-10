---
name: release-publish
description: "将 SSMSBoostPlus 项目最新的本地代码重新编译、打包成安装包并发布到 Release 分支。触发: 发布RELEASE、发布Release、发布release、release发布、上线发布、应用发布、发布新版本、publish release、release publish、打包发布"
---

# SSMSBoostPlus 发布 RELEASE 流程

将当前 `feat-ai` 分支的最新代码编译 → 打包安装包 → 推送到 `Release` 分支的完整流水线。

## 项目关键信息

| 项 | 值 |
|---|---|
| 解决方案 | `SSMSBoostPlus.slnx`（.sln 被企业加密软件加壳，不可用） |
| 编译配置 | Release |
| 工作路径 | `d:\172.31.1.44\AI\SSMSBoostPlus` |
| 当前开发分支 | `feat-ai` |
| 发布目标分支 | `Release` |
| 远程仓库 | `http://172.16.10.168/AI项目集合/SSMSBoostPlus/_git/SSMSBoostPlus` |
| 分支依赖链 | feat-ai → (编译) → Release |

## 流水线流程（共 7 步）

### Step 1: 确认当前在 feat-ai 分支且工作区干净

```powershell
git branch          # 确认当前分支为 feat-ai
git status          # 确认无未提交的修改
```

- 如有未提交修改 → 视情况 `git add + git commit` 或 `git stash`
- 工作区最好干净，避免切换分支时冲突

### Step 2: 编译项目（Release 配置）

```powershell
dotnet build SSMSBoostPlus.slnx -c Release --nologo
```

编译产出：
- VSCTCompile: `.vsct` → `.cto` ✅
- CoreCompile: C# 编译 → 4个 DLL ✅
- GeneratePkgDef: CreatePkgDef.exe → `.pkgdef` ✅

编译失败 → 分析错误 → 修复 → 回到 Step 2

### Step 3: 编译安装程序（InstallSSMSBoostPlus.exe）

```powershell
dotnet build src/Installer/SSMSBoostPlus.Installer.csproj -c Release --nologo
```

### Step 4: 验证编译产物

```powershell
# 核心 DLL
Test-Path "src/SSMS/bin/Release/net472/SSMSBoostPlus.SSMS.dll"
Test-Path "src/SSMS/bin/Release/net472/SSMSBoostPlus.Core.dll"
Test-Path "src/SSMS/bin/Release/net472/SSMSBoostPlus.AI.dll"
Test-Path "src/SSMS/bin/Release/net472/SSMSBoostPlus.Shared.dll"

# 配置
Test-Path "src/SSMS/obj/Release/net472/SSMSBoostPlus.SSMS.pkgdef"
Test-Path "src/SSMS/obj/Release/net472/SSMSBoostPlus.cto"
Test-Path "src/SSMS/Resources/marked.min.js"
Test-Path "src/SSMS/Resources/stream-engine.js"
Test-Path "src/SSMS/source.extension.vsixmanifest"

# 安装程序
Test-Path "src/Installer/bin/Release/net472/InstallSSMSBoostPlus.exe"

# WebView2 NuGet 缓存
Test-Path "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2\1.0.2903.40\"
```

### Step 5: 打包安装包到临时目录（在 feat-ai 分支上执行）

> ⚠ **关键**：必须在切到 Release 分支之前完成打包，因为切换分支会删除 feat-ai 的编译产物！

将编译产物复制到工作区外的临时目录 `d:\172.31.1.44\_release_temp`：

```powershell
$srcBase = "d:\172.31.1.44\AI\SSMSBoostPlus"
$tmp = "d:\172.31.1.44\_release_temp"
$fd = "$tmp\files"

# 清空重建
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item $fd -ItemType Directory -Force | Out-Null

# ========== 核心 DLL（4个+4个PDB）==========
$buildDir = "$srcBase\src\SSMS\bin\Release\net472"
Copy-Item "$buildDir\SSMSBoostPlus.SSMS.dll" $fd\
Copy-Item "$buildDir\SSMSBoostPlus.Core.dll" $fd\
Copy-Item "$buildDir\SSMSBoostPlus.AI.dll" $fd\
Copy-Item "$buildDir\SSMSBoostPlus.Shared.dll" $fd\
@("SSMSBoostPlus.SSMS.pdb","SSMSBoostPlus.Core.pdb","SSMSBoostPlus.AI.pdb","SSMSBoostPlus.Shared.pdb") | % {
    $p = "$buildDir\$_"; if (Test-Path $p) { Copy-Item $p $fd\ }
}

# ========== 配置文件（3个）==========
$objDir = "$srcBase\src\SSMS\obj\Release\net472"
if (Test-Path "$objDir\SSMSBoostPlus.SSMS.pkgdef") { Copy-Item "$objDir\SSMSBoostPlus.SSMS.pkgdef" $fd\ }
$cto = "$objDir\SSMSBoostPlus.cto"
if (Test-Path $cto) { Copy-Item $cto "$fd\Menus.ctmenu" }
$mn = "$srcBase\src\SSMS\source.extension.vsixmanifest"
if (Test-Path $mn) { Copy-Item $mn "$fd\extension.vsixmanifest" }

# ========== JS 资源（2个）==========
$resDir = "$srcBase\src\SSMS\Resources"
@("marked.min.js","stream-engine.js") | % {
    $p = "$resDir\$_"; if (Test-Path $p) { Copy-Item $p $fd\ }
}

# ========== 第三方依赖（30个）==========
@(
    "DiffPlex.dll","Serilog.dll","Serilog.Sinks.File.dll","Serilog.Enrichers.Environment.dll",
    "Microsoft.Data.SqlClient.dll","Newtonsoft.Json.dll","Microsoft.Bcl.AsyncInterfaces.dll",
    "Microsoft.Extensions.Configuration.dll","Microsoft.Extensions.Configuration.Abstractions.dll",
    "Microsoft.Extensions.Configuration.Binder.dll","Microsoft.Extensions.Configuration.FileExtensions.dll",
    "Microsoft.Extensions.Configuration.Json.dll","Microsoft.Extensions.FileProviders.Abstractions.dll",
    "Microsoft.Extensions.FileProviders.Physical.dll","Microsoft.Extensions.FileSystemGlobbing.dll",
    "Microsoft.Extensions.Primitives.dll","Microsoft.SqlServer.TransactSql.ScriptDom.dll",
    "Markdig.dll","Markdown.Xaml.dll","Microsoft.Xaml.Behaviors.dll",
    "System.Memory.dll","System.Runtime.CompilerServices.Unsafe.dll","System.Text.Json.dll",
    "System.Threading.Tasks.Extensions.dll","System.ValueTuple.dll","System.Buffers.dll",
    "System.Numerics.Vectors.dll"
) | % { $p = "$buildDir\$_"; if (Test-Path $p) { Copy-Item $p $fd\ } }

# ========== WebView2（3个，从 NuGet 缓存）==========
$wv = "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2\1.0.2903.40"
@(
    "lib\net462\Microsoft.Web.WebView2.Core.dll",
    "lib\net462\Microsoft.Web.WebView2.Wpf.dll",
    "runtimes\win-x64\native\WebView2Loader.dll"
) | % { $p = "$wv\$_"; if (Test-Path $p) { Copy-Item $p $fd\ } }

# ========== ⚠ 安装程序不重新打包 ==========
# 安装程序 (InstallSSMSBoostPlus.exe) 不随每次发布更新
# 只在安装程序自身逻辑发生变化时才重新编译和发布
Write-Output "跳过安装程序打包（保留 Release 分支上现有的 InstallSSMSBoostPlus.exe）"

# ========== 验证文件数 ==========
$count = (Get-ChildItem $fd -File).Count
Write-Output "打包完成: files目录=$count 个文件"
```

### Step 6: 切换到 Release 分支并更新内容

```powershell
# 切到 Release 分支
git checkout Release

# 清理 Release 分支上除了 .git 之外的所有内容（保留 InstallSSMSBoostPlus.exe）
Remove-Item -Path "src" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "tests" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "Installer" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ".codebuddy" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ".workbuddy" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "logs" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "files" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "build-installer.bat" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "install-online.bat" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "README.txt" -Force -ErrorAction SilentlyContinue
Remove-Item -Path ".gitignore" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "*.docx" -Force -ErrorAction SilentlyContinue
# ⚠ 不再删除 InstallSSMSBoostPlus.exe 和 .config，保留安装程序不变

# 从临时目录复制新的 files 目录
$tmp = "d:\172.31.1.44\_release_temp"
Copy-Item "$tmp\files" ".\" -Recurse -Force
# ⚠ 不再复制 InstallSSMSBoostPlus.exe 和 .config
Remove-Item $tmp -Recurse -Force

# 提交并推送
git add -A
git commit -m "Release v{版本号}: {更新说明}"
git push origin Release --force
```

### Step 7: 切回 feat-ai 分支并验证

```powershell
git checkout feat-ai
git fetch origin Release
git log --oneline origin/Release -3
```

验证远程 Release 分支的最后提交是否为刚才推送的版本。

## 参数化控制

| 用户意图 | 执行范围 |
|---|---|
| "发布RELEASE" / "发布Release" | 完整流水线 Step 1-7 |
| "仅编译不发布" | Step 1-4（不打包推送） |
| "仅更新安装包" | Step 5-7（不重新编译，前提是已有编译产物） |
| "紧急修复发布" | Step 1-7（带 git stash apply 流程） |
| "查看最新Release版本" | Step 7 的验证步骤 |

## 安装包文件完整清单（43个文件）

### 核心 DLL（4个+4个PDB）

| 文件 | 来源 |
|------|------|
| SSMSBoostPlus.SSMS.dll + .pdb | src/SSMS |
| SSMSBoostPlus.Core.dll + .pdb | src/Core |
| SSMSBoostPlus.AI.dll + .pdb | src/AI |
| SSMSBoostPlus.Shared.dll + .pdb | src/Shared |

### 配置文件（3个）

| 文件 | 来源 | 说明 |
|------|------|------|
| SSMSBoostPlus.SSMS.pkgdef | obj/Release/net472/  | 扩展注册信息 |
| Menus.ctmenu | 从 .cto 复制 | 编译后的菜单资源 |
| extension.vsixmanifest | src/SSMS/ | VSIX 清单 |

### JS 资源（2个）

| 文件 | 说明 |
|------|------|
| marked.min.js | Markdown 解析库（v12.0.2） |
| stream-engine.js | SSE 客户端 + 流式渲染引擎 |

### 第三方依赖（30个）

| 类别 | 文件 |
|------|------|
| 差异对比 | DiffPlex.dll |
| 日志 | Serilog.dll, Serilog.Sinks.File.dll, Serilog.Enrichers.Environment.dll |
| 数据库 | Microsoft.Data.SqlClient.dll |
| JSON | Newtonsoft.Json.dll |
| 异步 | Microsoft.Bcl.AsyncInterfaces.dll |
| 配置框架 | Microsoft.Extensions.Configuration.dll, *.Abstractions.dll, *.Binder.dll, *.FileExtensions.dll, *.Json.dll, *.FileProviders.Abstractions.dll, *.FileProviders.Physical.dll, *.FileSystemGlobbing.dll, *.Primitives.dll |
| T-SQL 解析 | Microsoft.SqlServer.TransactSql.ScriptDom.dll |
| Markdown | Markdig.dll, Markdown.Xaml.dll |
| WPF | Microsoft.Xaml.Behaviors.dll |
| WebView2 | Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.Wpf.dll, WebView2Loader.dll |
| 系统库 | System.Memory.dll, System.Runtime.CompilerServices.Unsafe.dll, System.Text.Json.dll, System.Threading.Tasks.Extensions.dll, System.ValueTuple.dll, System.Buffers.dll, System.Numerics.Vectors.dll |

### 安装程序（2个）- 不随每次发布更新

| 文件 | 说明 |
|------|------|
| InstallSSMSBoostPlus.exe | C# 安装程序，双击自动在线下载安装，支持 --download/--uninstall/--all/--ssms22 等参数。**仅当安装程序自身逻辑变更时才需重新编译和替换** |
| InstallSSMSBoostPlus.exe.config | 安装程序配置文件（.NET 运行时版本） |

## Release 分支结构

```
Release 分支根目录/
├── InstallSSMSBoostPlus.exe        ← C# 安装程序（双击即可在线安装，不随每次发布更新）
├── InstallSSMSBoostPlus.exe.config ← 安装程序配置（不随每次发布更新）
└── files/                          ← 部署文件（仅此目录随每次发布更新）
    ├── SSMSBoostPlus.SSMS.dll
    ├── SSMSBoostPlus.Core.dll
    ├── SSMSBoostPlus.AI.dll
    ├── SSMSBoostPlus.Shared.dll
    ├── SSMSBoostPlus.SSMS.pkgdef
    ├── Menus.ctmenu
    ├── extension.vsixmanifest
    ├── marked.min.js
    ├── stream-engine.js
    ├── DiffPlex.dll
    ├── Serilog.dll
    ├── ...（其余依赖）
    └── WebView2Loader.dll
```

> ⚠ Release 分支不再包含 `src/`、`Installer/InstallerPackage/` 等源码目录，仅保留安装包本身。
> ⚠ 安装程序 (`InstallSSMSBoostPlus.exe`) 不随每次发布更新。仅当安装程序自身逻辑变更时才需要重新编译和替换。

## 关键注意事项

### 1. 必须使用 .slnx 而非 .sln
`.sln` 文件被企业加密软件加壳，`dotnet build` 会失败。始终使用 `dotnet build SSMSBoostPlus.slnx -c Release`。

### 2. 编译后必须同时编译安装程序
```powershell
dotnet build src/Installer/SSMSBoostPlus.Installer.csproj -c Release
```
安装程序和主项目是独立的 csproj，需要分别编译。

### 3. 打包必须在切 Release 分支之前完成
切换分支会删除 feat-ai 的工作目录，导致编译产物丢失。**必须先打包到临时目录（如 `d:\172.31.1.44\_release_temp`），再切换分支。**

### 4. WebView2 DLL 需从 NuGet 缓存复制
WebView2 NuGet 包的 DLL 不在 bin 输出目录，需要从 NuGet 缓存手动复制：
```
%USERPROFILE%\.nuget\packages\microsoft.web.webview2\1.0.2903.40\
├── lib\net462\Microsoft.Web.WebView2.Core.dll
├── lib\net462\Microsoft.Web.WebView2.Wpf.dll
└── runtimes\win-x64\native\WebView2Loader.dll
```

### 5. Force Push 到 Release 分支
Release 分支使用 `--force` 推送，因为每次发布都是完全替换安装包内容，不需要保留历史。

### 6. 版本号管理
每次发布应更新版本号：
- 如果修改了代码：提交信息标注版本号（如 `Release v1.2.3: xxx`）
- 如果只更新了安装包：提交信息标注包版本

### 7. 记忆文件注意事项
- `.workbuddy/memory/` 目录在切换分支时会被删除
- 发布完成后可能需要重建记忆文件（MEMORY.md 和 当天日期文件）

## 常见问题处理

| 问题 | 原因 | 处理方式 |
|------|------|----------|
| dotnet build 失败 | .sln 被加密 | 改用 .slnx |
| 找不到 WebView2 DLL | NuGet 缓存不存在或版本不对 | 检查 %USERPROFILE%\.nuget\packages\microsoft.web.webview2\ 目录 |
| Release 分支推送失败 | 权限不足或网络问题 | 检查 git 凭据和网络连接 |
| 切换分支时文件冲突 | Release 分支上存在未提交的 untracked 文件 | 先 `Remove-Item` 清理冲突文件再切换 |
| 编译产物不完整 | 缺少 NuGet 包 | `dotnet restore` 后再 build |
| git checkout Release 报错 files/ 冲突 | Release 分支的 old files/ 与本地 untracked files/ 冲突 | 先 `Remove-Item files -Recurse -Force` |
| 打包后文件数不足43个 | 某些依赖 DLL 未复制 | 检查 Step 5 脚本中是否遗漏了文件名 |

## Release 发布检查清单

- [ ] 1. 当前在 feat-ai 分支
- [ ] 2. 工作区干净（无未提交修改）
- [ ] 3. 编译 Release 成功（0 error）
- [ ] 4. 编译 Installer 成功（0 error）
- [ ] 5. 验证编译产物（4 DLL + pkgdef + cto + exe）
- [ ] 6. **打包到临时目录**（43个文件，切分支前完成）
- [ ] 7. 推送到 Release 分支成功
- [ ] 8. 切回 feat-ai 分支
- [ ] 9. 验证远程 Release 分支已更新
- [ ] 10. 重建记忆文件（如有需要）