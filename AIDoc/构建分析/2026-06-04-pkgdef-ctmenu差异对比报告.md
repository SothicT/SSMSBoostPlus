# PKGDEF / CTMENU 文件差异对比分析报告

> 分析日期：2026-06-03  
> 当前生成文件：`src/SSMS/bin/Release/net472/`  
> 正确参考文件：`E:\SSMSBoostPlus\`（已确认可用的版本）

---

## 一、pkgdef 差异总览（7 处差异）

### 逐行差异表

| # | 位置 | 当前生成（错误） | 正确参考 | 严重程度 | 根因 |
|---|---|---|---|---|---|
| 1 | Line 2 `@=` 值 | `"SSMSBoostPlus"` | `"SSMSBoostPlusPackage"` | **高** | 包名应为 Package 类全名，影响 SSMS 扩展识别 |
| 2 | Line 3 路径变量 | `$WinDir*`（带星号） | `$WinDir$` | **致命** | `*` 是无效变量格式，导致 MSCOREE.DLL 路径解析失败 |
| 3 | Line 6 内容 | 有冗余的 `"Assembly"=...` 行 | 有 `"AllowsBackgroundLoad"=dword:00000001` | **高** | 多了不需要的 Assembly 行；少了后台加载标志 |
| 4 | Line 7-8 | 空 `[...\ToolWindows]` 节 | 直接跳到 AutoLoadPackages | 低 | 多余空节，不影响功能但说明生成逻辑有误 |
| 5 | 全局子键路径 | 使用 `$RootKey%` | 使用 `$RootKey$` | **致命** | `%` 是值引用符号，所有子键 section header 都写错了 |
| 6 | Menus 注册值 | `",1000,1"` （嵌入资源 ID 模式） | `", Menus.ctmenu, 1"` （文件名模式） | **核心** | 正确版引用外部 ctmenu 文件，当前版从 DLL 内嵌资源按 ID 加载 |
| 7 | AutoLoad 值 | `dword:00000001` | `dword:00000002` | 中 | 1=无条件加载，2=需要 UI 上下文；代码声明的是 BackgroundLoad(=2) 但生成为了 1 |

---

### 差异详情

#### 差异 1 — 包名称（Line 2）

```
当前: @="SSMSBoostPlus"
正确: @="SSMSBoostPlusPackage"
```

包名称应该是 Package 类的全限定名 `SSMSBoostPlusPackage`，SSMS 用它来标识和显示扩展。当前生成的版本截断了 "Package" 后缀。

#### 差异 2 — InprocServer32 路径变量（Line 3）

```
当前: "InprocServer32"="$WinDir*\SYSTEM32\MSCOREE.DLL"
正确: "InprocServer32"="$WinDir$\SYSTEM32\MSCOREE.DLL"
```

`$WinDir*` 中的星号是无效格式。pkgdef 规范中路径变量使用 `$` 结尾（如 `$WinDir$`、`$PackageFolder$`）。星号会导致 SSMS 无法解析 MSCOREE.DLL 的正确路径，这是**致命错误**。

#### 差异 3 — Assembly 行 vs AllowsBackgroundLoad（Line 6）

```
当前: "Assembly"="SSMSBoostPlus.SSMS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
正确: "AllowsBackgroundLoad"=dword:00000001
```

两个问题：
1. 当前生成了不需要的 `Assembly` 行（当使用 `UseCodebase=true` 时不需要显式指定 Assembly）
2. 缺少 `AllowsBackgroundLoad=dword:00000001`，该标志允许包在后台加载，对 SSMS 启动性能很重要

#### 差异 4 — 多余的空 ToolWindows 节（Line 7-8）

```
当前: [$RootKey%\Packages\{guid}\ToolWindows]
      (空节)
正确: (不存在此节)
```

纯多余内容，不影响运行但不规范。

#### 差异 5 — 子键引用符全局错误（最关键的格式问题）

**这是影响范围最广的错误。** pkgdef 格式规范明确定义了两种根键引用方式：

| 符号 | 用途 | 适用位置 |
|------|------|----------|
| `$RootKey$` | 注册表子键/节路径 | Section header（方括号内的路径） |
| `$RootKey%` | 值数据中的根键引用 | 值数据中（极少使用） |

当前生成的文件把**所有**子键路径都错误地写成了 `%`：

```
错误 - 当前生成:
[$RootKey%\Packages\{a1b2c3d4-...}\ToolWindows]   ← 错！
[$RootKey%\Menus]                                    ← 错！
[$RootKey%\AutoLoadPackages\{e80ef1cb-...}]          ← 错！

正确 - 参考文件:
[$RootKey$\AutoLoadPackages\{e80ef1cb-...}]          ← 对
[$RootKey$\Menus]                                     ← 对
```

这会导致注册表合并时路径解析失败，SSMS 无法正确识别扩展的注册信息。

#### 差异 6 — Menus 注册方式（核心问题）

```
当前: "{guid}"=",1000,1"
正确: "{guid}"=", Menus.ctmenu, 1"
```

两种模式有本质区别：

| 模式 | 格式 | 含义 |
|------|------|------|
| 嵌入资源 ID 模式 | `",1000,1"` | 从 DLL 内嵌的二进制资源中按 ID=1000 加载菜单数据 |
| 文件名模式 | `", Menus.ctmenu, 1"` | 从扩展目录下的外部文件 `Menus.ctmenu` 加载菜单数据 |

正确参考使用的是**文件部署模式**。这与之前积累的经验一致——需要同时部署 Menus.ctmenu 文件作为"双保险"策略的一部分。

#### 差异 7 — AutoLoadPackages 加载模式值

```
当前: dword:00000001
正确: dword:00000002
```

| 值 | 含义 |
|----|------|
| 1 | NoSolution_Load（无 UI 上下文即可加载） |
| 2 | UILoad（需要 UI 解决方案上下文才加载） |

Package 代码中使用的是 `[ProvideAutoLoad(..., PackageAutoLoadFlags.BackgroundLoad)]`，对应值应为 2，但自动管道生成了错误的值 1。

---

## 二、ctmenu 二进制文件比较

### 基本信息

| 属性 | 当前生成 | 正确参考 |
|------|---------|---------|
| 文件大小 | **1439 bytes** | **1439 bytes** |
| 字节数差异 | — | **1119 处不同**（占 77.8%） |

两个文件大小完全相同，但内容几乎完全不同——说明是不同编译条件/版本的产物。

### 差异分布

字节差异从 Offset 0x0014 开始（前 20 字节的 header 相同），主要差异集中在：

```
Offset 0x0014: Cur=0x2B  Ref=0xF4
Offset 0x0015: Cur=0x33  Ref=0x81
Offset 0x0016: Cur=0x3C  Ref=0x71
Offset 0x0017: Cur=0x4D  Ref=0xE8
...（共 1119 处）
```

### 可能原因

1. **VSCT 编译器版本不同** — 正确参考可能由旧版本 VS SDK 编译；当前使用 `Microsoft.VSSDK.BuildTools 18.5.40034`
2. **编译选项/标志差异** — 不同版本 VSCT.exe 的默认参数或输出格式可能有变化
3. **二进制头部元数据编码变化** — GUID 顺序、版本号、校验和等编译器生成元数据的编码方式不同
4. **命令排序或哈希算法差异** — CTO 格式内部包含命令数据的哈希/校验信息

---

## 三、完整文件对照

### 当前生成的 pkgdef 全文

```ini
[$RootKey$\Packages\{a1b2c3d4-e5f6-7890-abcd-ef1234567890}]
@="SSMSBoostPlus"
"InprocServer32"="$WinDir*\SYSTEM32\MSCOREE.DLL"
"Class"="SSMSBoostPlus.SSMS.SSMSBoostPlusPackage"
"CodeBase"="$PackageFolder$\SSMSBoostPlus.SSMS.dll"
"Assembly"="SSMSBoostPlus.SSMS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"

[$RootKey%\Packages\{a1b2c3d4-e5f6-7890-abcd-ef1234567890}\ToolWindows]

[$RootKey%\Menus]
"{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"=",1000,1"

[$RootKey%\AutoLoadPackages\{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}]
"{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"=dword:00000001
```

### 正确参考的 pkgdef 全文

```ini
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

---

## 四、根因分析

### 构建环境

当前 csproj 配置：
- SDK 风格项目 (`Microsoft.NET.Sdk.WindowsDesktop`)
- `GeneratePkgDefFile=true` — 由 VSSDK BuildTools 自动生成 pkgdef
- `<VSCTCompile>` ItemGroup — 自动调用 VSCT 编译器
- VSSDK BuildTools 版本：**18.5.40034**
- 已注释掉手动管线（ManualVSCTCompile / ManualPkgDef）

### 根本原因

VSSDK BuildTools 的自动构建管道在 .NET SDK 风格项目中行为异常：

1. **CreatePkgDef.exe 生成的 pkgdef 存在多个格式 bug** — 子键引用符用 `%` 代替 `$`、路径变量出现 `*`、缺少 AllowsBackgroundLoad、包名截断、AutoLoad 值错误
2. **VSCT 编译器产物不稳定** — 同一 vsct 源码在不同条件下产生不同的二进制输出

这些问题与之前经验记录中的结论一致：".NET SDK 下 ItemGroup 在 Target 内添加的 EmbeddedResource 不生效"，说明整个 VSSDK 自动管道在当前构建环境中不可靠。

---

## 五、修复方案建议

### 方案 A：恢复手动构建管线（推荐）

取消注释 csproj 中的手动 Target，完全控制生成过程：
1. 取消注释 `ManualVSCTCompile` Target — 用 VSCT.exe 手动编译 vsct → cto
2. 取消注释 `ManualPkgDef` Target — 用 CreatePkgDef.exe 手动生成 pkgdef（可修正参数）
3. 将 CTO 同时嵌入 DLL（静态 EmbeddedResource）并复制为 Menus.ctmenu（文件模式）
4. 必要时以正确参考文件为模板，通过脚本后处理修正 pkgdef 内容

优点：完全可控，可精确匹配正确的输出格式  
缺点：维护成本稍高，需要确保 VSCT.exe / CreatePkgDef.exe 路径正确

### 方案 B：基于正确参考文件的模板化构建

将确认可用的正确文件作为固定模板：
1. 将正确 pkgdef 作为源文件纳入项目
2. 构建时仅替换动态部分（如 CodeBase 路径）
3. 直接复制正确的 ctmenu 作为构建产物
4. 可选：将正确 vsct 的编译产物(cto)直接签入源码管理

优点：简单可靠，100% 保证输出正确  
缺点：vsct 源码修改后需重新从参考环境编译 ctmeun

### 方案 C：构建后修复脚本

保持现有自动管道，增加一个 PostBuild 脚本修正已知问题：
1. 正则替换 `$RootKey%` → `$RootKey$`（子键行）
2. 替换 `$WinDir*` → `$WinDir$`
3. 修正包名为 SSMSBoostPlusPackage
4. 删除多余的 Assembly 行，添加 AllowsBackgroundLoad
5. 修正 AutoLoad 值为 dword:00000002
6. 修正 Menus 值为 `, Menus.ctmenu, 1`
7. 删除空的 ToolWindows 节

优点：不改变现有构建流程  
缺点：治标不治本，如果自动管道的其他行为变化可能出现新问题

---

## 六、相关文件索引

| 文件 | 路径 | 说明 |
|------|------|------|
| 当前 pkgdef | `src/SSMS/bin/Release/net472/SSMSBoostPlus.SSMS.pkgdef` | 自动构建产物（有问题） |
| 当前 ctmenu | `src/SSMS/bin/Release/netet472/Menus.ctmenu` | 自动构建产物（有问题） |
| 参考 pkgdef | `E:\SSMSBoostPlus\SSMSBoostPlus.SSMS.pkgdef` | 确认可用的正确版本 |
| 参考 ctmenu | `E:\SSMSBoostPlus\Menus.ctmenu` | 确认可用的正确版本 |
| 参考 vsct | `E:\SSMSBoostPlus\SSMSBoostPlus.vsct` | 命令表定义源码 |
| 项目文件 | `src/SSMS/SSMSBoostPlus.SSMS.csproj` | 构建配置 |
| 包类定义 | `src/SSMS/SSMSBoostPlusPackage.cs` | PackageRegistration 等特性定义 |
