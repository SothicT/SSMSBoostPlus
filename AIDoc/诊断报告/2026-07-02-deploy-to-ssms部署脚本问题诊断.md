---
title: deploy-to-ssms.bat 部署脚本问题诊断
date: 2026-07-02
author: AI Assistant
tags: [部署脚本, 诊断报告, SSMS22, 动态检测, 硬编码]
---

# deploy-to-ssms.bat 部署脚本问题诊断报告

## 文档信息

| 项 | 值 |
|---|---|
| 诊断对象 | `deploy-to-ssms.bat`（227 行） |
| 对比脚本 | `deploy-current.bat`（224 行，中文版） |
| 关联 Agent | `.codebuddy/agents/小H发布部署.md` |
| 诊断方法 | 静态分析 + 部署流程反推 + 路径影响评估 |
| 严重度统计 | 🔴 P0: 5 个 / 🟡 P1: 7 个 / 🟢 P2: 6 个 |

---

## 一、问题分类总览

| 严重度 | 数量 | 典型影响 |
|--------|------|----------|
| 🔴 P0 致命（不可移植/必然失败） | 5 | 跨机器部署必失败 |
| 🟡 P1 严重（健壮性差） | 7 | 偶发失败、用户体验差 |
| 🟢 P2 改进（可优化） | 6 | 维护性、安全性 |

---

## 二、🔴 P0 致命问题（5 个）

### P0-1：SSMS 22 路径硬编码
**位置**：第 22-24 行

```bat
set SSMS_EXT_DIR=D:\Microsoft SQL Server Management Studio 22\Release\...
set SSMS_COMMON_DIR=D:\Microsoft SQL Server Management Studio 22\Release\...
set CONFIG_FLAG=D:\Microsoft SQL Server Management Studio 22\Release\...
```

**问题**：
- 仅匹配 `D:\Microsoft SQL Server Management Studio 22\` 这**唯一一条**路径
- 不支持 `C:\Program Files\`、`E:\`、自定义安装路径
- 用户机器路径稍有变化即失败

**影响**：**100% 跨机器失败** — 任何与作者机器不同的安装位置都无法部署

**修复方案**：调用 `diagnosis/Find-SSMS22Path.ps1` 动态检测

---

### P0-2：项目路径硬编码为 F 盘
**位置**：第 20 行

```bat
set PROJECT_DIR=F:\2.C#_Core\SSMSBoostPlus
```

**问题**：
- 写死为作者本机 `F:\2.C#_Core\SSMSBoostPlus`
- 其他用户工作区不可能是这个路径

**影响**：**100% 跨用户失败**

**修复方案**：

```bat
:: 方案 A：自动推导（脚本所在目录）
set PROJECT_DIR=%~dp0..
:: 方案 B：要求用户输入
set /p PROJECT_DIR="请输入项目根目录: "
```

---

### P0-3：WebView2 NuGet 版本硬编码
**位置**：第 191-195 行

```bat
set WEBVIEW2_NUGET=%USERPROFILE%\.nuget\packages\microsoft.web.webview2\1.0.2903.40
```

**问题**：
- 版本号 `1.0.2903.40` 写死
- 用户 `dotnet restore` 后，本地 NuGet 缓存目录可能是 `1.0.2277.42` 或其他版本
- 静默失败：找不到 DLL 时仅 `if exist` 跳过，**无任何提示**

**影响**：WebView2 相关功能（AI 对话面板）部署后无法运行，**用户毫无感知**

**修复方案**：

```bat
:: 动态查找最新版本
for /d %%D in ("%USERPROFILE%\.nuget\packages\microsoft.web.webview2\*") do (
    set WEBVIEW2_NUGET=%%D
)
if not defined WEBVIEW2_NUGET echo [WARNING] WebView2 NuGet 包未找到
```

---

### P0-4：未调用 `ssms.exe /setup`
**位置**：第 200-206 行（缺失）

**问题**：
- 部署后**只刷新了 `extensions.configurationchanged` 标记**
- **未执行 `SSMS.exe /setup` 强制 PkgDef 重新合并到私有注册表**
- 私有注册表：`HKU\<SID>\Software\Microsoft\SSMS\22.0_79c71968_Config\`

**影响**：
- 偶发"扩展不加载"问题
- 用户需手动以管理员身份执行 `SSMS.exe /setup` 才能生效

**修复方案**：在 Step 7 末尾追加：

```bat
echo [Step 8/8] 强制刷新 PkgDef 缓存...
"%SSMS_EXE%" /setup
echo [OK] PkgDef 已重新合并到私有注册表
```

---

### P0-5：未关闭 SSMS 进程检测
**位置**：整个脚本（缺失）

**问题**：
- 部署前未检查 SSMS 是否运行
- SSMS 正在运行时，4 个核心 DLL 被锁定，copy 静默失败（不报错但文件未更新）

**影响**：
- 部署"看起来成功"但实际是旧版本 DLL
- 用户重启 SSMS 后扩展加载的仍是旧代码，**排查极其困难**

**修复方案**：在 Step 1 之前增加：

```bat
:: 检查 SSMS 进程
tasklist /FI "IMAGENAME eq SSMS.exe" 2>NUL | find /I /N "SSMS.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo [ERROR] SSMS 正在运行！请先关闭所有 SSMS 窗口。
    echo.
    pause
    exit /b 1
)
```

---

## 三、🟡 P1 严重问题（7 个）

### P1-1：copy 命令无错误检查
**位置**：第 92-195 行（所有 copy 语句）

```bat
copy /Y "%SOURCE_DIR%\SSMSBoostPlus.SSMS.dll" "%SSMS_EXT_DIR%\"
echo [OK] SSMSBoostPlus.SSMS.dll
```

**问题**：
- `copy` 失败时 `ERRORLEVEL` 被设置但未检查
- 即使 DLL 被锁定 copy 失败，仍会打印 `[OK]`
- **误导用户认为部署成功**

**修复方案**：

```bat
copy /Y "..." "%SSMS_EXT_DIR%\" >nul
if errorlevel 1 (
    echo [ERROR] 复制失败: SSMSBoostPlus.SSMS.dll
    echo 可能原因: 文件被 SSMS 占用 / 权限不足 / 磁盘空间不足
    exit /b 1
)
echo [OK] SSMSBoostPlus.SSMS.dll
```

---

### P1-2：未清空旧部署残留
**位置**：Step 3 之后（缺失）

**问题**：
- 只删除了 `commonextensions\SSMSBoostPlus`
- **未清空 `Extensions\SSMSBoostPlus\` 旧文件**
- 如果新版删除了某个 DLL（如 `Markdig.dll`），旧版 DLL 仍残留

**影响**：旧依赖堆积，可能导致版本冲突

**修复方案**：

```bat
echo [Step 2.5/7] 清空旧部署残留...
if exist "%SSMS_EXT_DIR%" (
    del /q "%SSMS_EXT_DIR%\*.dll" 2>nul
    del /q "%SSMS_EXT_DIR%\*.pdb" 2>nul
    del /q "%SSMS_EXT_DIR%\*.pkgdef" 2>nul
    del /q "%SSMS_EXT_DIR%\*.ctmenu" 2>nul
    del /q "%SSMS_EXT_DIR%\*.vsixmanifest" 2>nul
)
```

---

### P1-3：配置文件源路径散落重复
**位置**：第 129-156 行

```bat
if exist "%PROJECT_DIR%\src\SSMS\obj\Release\net472\SSMSBoostPlus.SSMS.pkgdef" (...
) else (
    if exist "%PROJECT_DIR%\src\SSMS\obj\Release\SSMSBoostPlus.SSMS.pkgdef" (...
```

**问题**：
- pkgdef / cto 路径每个文件**重复写了两遍**（带 / 不带 `net472` 兜底）
- 维护性差，新增文件需改 2 处

**修复方案**：抽取变量

```bat
:: 兜底多版本输出目录
set OBJ_DIR_1=%PROJECT_DIR%\src\SSMS\obj\Release\net472
set OBJ_DIR_2=%PROJECT_DIR%\src\SSMS\obj\Release
for %%P in ("%OBJ_DIR_1%" "%OBJ_DIR_2%") do (
    if exist "%%~P\SSMSBoostPlus.SSMS.pkgdef" set PKGDEF_PATH=%%~P
)
```

---

### P1-4：依赖项静默失败无累计报告
**位置**：第 164-195 行

```bat
if exist "%SOURCE_DIR%\DiffPlex.dll" copy /Y ... >nul
```

**问题**：
- 所有 `if exist` 失败时**不打印任何信息**
- 部署完成后用户不知道**实际复制了几个文件 / 缺了几个**
- `Step 6` 末尾仅打印 `Dependencies copied`，无法定位缺失项

**修复方案**：

```bat
set DEP_COPIED=0
set DEP_MISSING=
if exist "%SOURCE_DIR%\DiffPlex.dll" (copy /Y ... >nul & set /a DEP_COPIED+=1) else (set "DEP_MISSING=!DEP_MISSING! DiffPlex.dll")
:: ... 其余 12 个依赖类似
echo [INFO] 复制 %DEP_COPIED% 个依赖项
if defined DEP_MISSING (
    echo [WARNING] 缺失: %DEP_MISSING%
)
```

---

### P1-5：未验证部署完整性
**位置**：Step 7 之后（缺失）

**问题**：
- 部署完成**无任何验证步骤**
- 不对比目标文件 `LastWriteTime` 与源文件
- 不检查 `SSMS.exe /setup` 是否真的执行成功

**修复方案**：Step 8 增加：

```bat
:: 验证关键文件时间戳
for %%F in (SSMSBoostPlus.SSMS.dll SSMSBoostPlus.Core.dll SSMSBoostPlus.AI.dll SSMSBoostPlus.Shared.dll) do (
    if not exist "%SSMS_EXT_DIR%\%%F" (
        echo [ERROR] 部署失败: %%F 不存在
        exit /b 1
    )
)
echo [OK] 部署验证通过
```

---

### P1-6：硬编码 `extensions.configurationchanged` 格式不规范
**位置**：第 204 行

```bat
echo %DATE% %TIME% > "%CONFIG_FLAG%"
```

**问题**：
- `extensions.configurationchanged` 标记**无换行终止**可能导致内容不规范
- `%DATE% %TIME%` 在不同区域设置下格式不同（中文系统显示 `2026/07/02`）

**修复方案**：

```bat
:: 使用 ISO 8601 格式
powershell -NoProfile -Command "(Get-Date -Format 'yyyy-MM-ddTHH:mm:ss') | Set-Content -Path '%CONFIG_FLAG%'"
```

---

### P1-7：管理员权限检查后无 `setlocal` 隔离
**位置**：第 1-18 行

**问题**：
- 脚本未使用 `setlocal enabledelayedexpansion`，导致复杂条件中变量无法正确更新
- 全局环境变量污染
- 中文显示乱码风险（未设置 `chcp 65001`）

**修复方案**：脚本头部

```bat
@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
```

---

## 四、🟢 P2 改进项（6 个）

| # | 位置 | 问题 | 建议 |
|---|------|------|------|
| P2-1 | 第 2-3 行 | 无 `chcp 65001`，中文显示乱码 | 加 `chcp 65001 >nul` |
| P2-2 | 第 220-222 行 | 验证用 `dir /B` 仅文件名，缺大小/时间信息 | 改用 PowerShell `Get-ChildItem \| Format-Table` |
| P2-3 | 整个脚本 | 无 `--dry-run` / `--no-act` 模式 | 增加参数解析 |
| P2-4 | 整个脚本 | 无日志记录（仅控制台输出） | 加 `> deploy-%DATE%.log` |
| P2-5 | 第 191 行 | `set WEBVIEW2_NUGET` 全局变量命名空间污染 | 改为 `setlocal` 局部 |
| P2-6 | 整个脚本 | 无版本号/构建哈希校验 | 部署前对比 SSMSBoostPlus.csproj 版本 |

---

## 五、🆚 deploy-to-ssms.bat vs deploy-current.bat 对比

| 维度 | deploy-to-ssms.bat | deploy-current.bat |
|------|--------------------|--------------------|
| **项目路径** | `F:\2.C#_Core\SSMSBoostPlus` ❌ | `d:\Code\SSMSBoostPlusPub` ❌ |
| **SSMS 路径** | 正确路径 ✅（但硬编码） | 错误路径 ❌（`D:\Program Files\...`） |
| **字符编码** | 无 ❌ | `chcp 65001` ✅ |
| **延迟扩展** | 无 ❌ | `setlocal enabledelayedexpansion` ✅ |
| **进度报告** | 无 | `[40%%]` 等百分比 ✅ |
| **多语言** | 英文 | 中文 |
| **P0 问题数** | 5 | 5（路径全错，更严重） |

**结论**：
- `deploy-to-ssms.bat`：**路径正确**（D 盘无 Program Files），但同样硬编码
- `deploy-current.bat`：**路径错误**（多了 Program Files）
- 两个脚本**都有 P0 问题**，需统一改造

---

## 六、改造方案（推荐）

### 方案对比

| 方案 | 改动量 | 收益 | 推荐度 |
|------|--------|------|--------|
| **A. 抽取动态检测** — 脚本内嵌 PowerShell 调用 `Find-SSMS22InstallPath` | 🟡 中 | 彻底解决路径硬编码 | ⭐⭐⭐⭐⭐ |
| **B. 配置文件化** — `deploy.config.json` 存路径，脚本启动时读取 | 🟢 小 | 解决路径问题，但需用户配置 | ⭐⭐⭐ |
| **C. 完全 PowerShell 重写** — 抛弃 .bat，改用 .ps1 | 🔴 大 | 最佳实践，但改动量大 | ⭐⭐⭐⭐ |

### 最终推荐：**方案 4**（**抽取 `diagnosis/Find-SSMS22Path.ps1` 独立脚本 + 改造 deploy-to-ssms.bat 调用**）

#### 推荐理由

1. **单一职责原则**：检测逻辑与部署逻辑分离，一份代码多处复用
2. **可测试性**：PS1 可独立 Pester 测试，.bat 调用 .ps1 是 Win32 最佳实践
3. **可复用性**：CI/CD、其他扩展、小H Agent 都可调用同一份 PS1
4. **改动量适中**：脚本增加 ~30 行，PS1 落地为 ~150 行
5. **回退容易**：保留原硬编码作为兜底
6. **与已有工作协同**：小H Agent 文档**已经实现了同名函数**，抽取 PS1 = 文档代码落地为可执行文件

#### 关键设计

- `Find-SSMS22Path.ps1` 支持参数：`-AsJson` / `-NoPersist` / `-Source` / `-Verbose`
- `.bat` 调用方式：`for /f "delims=" %%P in ('powershell ... -File Find-SSMS22Path.ps1 -AsJson')`
- 项目目录自动推导：`set PROJECT_DIR=%~dp0..`
- SSMS 进程前置检查
- 部署后强制 `SSMS.exe /setup`

---

## 七、立即可执行 vs 待决策

### 立即可执行（无需决策）
- 加 `chcp 65001` 解决中文乱码
- 加 `setlocal enabledelayedexpansion`
- 增加 `extensions.configurationchanged` 标记格式规范化

### 待用户决策
- 抽取独立 `diagnosis/Find-SSMS22Path.ps1` 脚本 ✅（已确认执行方案 4）
- 统一改造两个 .bat（保留一个即可） ✅（已确认执行方案 4）
- 改造后同步更新小H Agent 中的"部署后验证"逻辑 ✅（已确认执行方案 4）
- 部署脚本纳入版本控制（目前未在 git 中跟踪）— 后续评估

---

## 八、相关文档

- `AIDoc/架构变更/2026-07-02-小H动态路径检测改造.md` - 关联的 Agent 改造
- `AIDoc/部署方案/2026-07-02-部署脚本动态路径改造方案.md` - 即将生成的方案
- `.codebuddy/skills/build-and-deploy/SKILL.md` - 编译部署 SOP
- `.codebuddy/agents/小H发布部署.md` - 编译部署 Agent

---

## 九、诊断元数据

```yaml
诊断ID: DIAG-2026-07-02-001
诊断对象: deploy-to-ssms.bat (227行), deploy-current.bat (224行)
P0问题数: 5
P1问题数: 7
P2问题数: 6
建议方案: 方案4 (抽取PS1 + 改造bat)
决策者: 用户
实施者: AI Assistant
完成时间: 2026-07-02 14:17
```
