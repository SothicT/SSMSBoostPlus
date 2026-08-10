---
title: SSMSBoostPlus 功能增强与 BUG 修复 PRD
date: 2026-07-31
author: PM Agent
tags: [SSMSBoostPlus, 快捷键, ESC, 检查更新, 连接解析, PRD]
---

# 产品需求文档（PRD）：SSMSBoostPlus 功能增强与 BUG 修复

## 文档信息

| 字段 | 值 |
|------|------|
| 版本号 | v1.2 |
| 创建日期 | 2026-07-31 |
| 作者 | PM Agent |
| 变更记录 | v1.0 初始创建；v1.1（2026-08-01）US-06 暂缓，实施顺序调整；v1.2（2026-08-04）补充 US-03 菜单快捷键显示的完整实现说明与验收细化 |

---

## 一、产品概述

### 1.1 业务背景与问题陈述

SSMSBoostPlus 是 SSMS 22 的功能增强扩展插件，当前版本在以下 5 个方面存在功能缺失或缺陷：

1. **快捷键布局不合理**：F11 绑定"打开脚本"，用户期望 F12 绑定"打开脚本"、F11 绑定"关键字查找"，更符合使用直觉
2. **菜单快捷键无显示**：顶部菜单和右键菜单中，绑定了快捷键的菜单项未显示快捷键提示，用户无法发现快捷键
3. **ESC 关闭候选弹窗失效**：代码补全弹窗无法通过 ESC 键可靠关闭，经过多次修复仍未解决
4. **检查更新响应慢**：点击"检查更新"按钮后无即时反馈，用户体验差
5. **连接服务器名解析错误**：当用户在 SSMS 连接对话框中填写了别名（如 "SRS"），代码从窗口标题解析到的是别名而非实际服务器 IP，导致无法建立数据库连接获取对象

### 1.2 产品目标与成功标准

| 编号 | 目标 | 成功标准（可量化） |
|------|------|-------------------|
| G1 | F12/F11 快捷键互换 | F12 触发打开脚本、F11 触发关键字查询，成功率 100% |
| G2 | 菜单显示快捷键 | 所有绑定了快捷键的菜单项右侧显示快捷键文字，覆盖率 100% |
| G3 | ESC 可靠关闭弹窗 | 弹窗可见时按 ESC，弹窗关闭成功率 100%（连续测试 20 次无失败） |
| G4 | 检查更新即时反馈 | 点击按钮后 ≤200ms 内出现 UI 反馈（进度提示/状态栏文字） |
| G5 | 服务器名正确解析 | 无论用户在连接对话框填写别名还是 IP，都能获取到实际可连接的服务器地址 |

### 1.3 目标用户画像

| 维度 | 描述 |
|------|------|
| 角色 | SQL Server 数据库开发人员 / DBA |
| 工具 | SSMS 22 + SSMSBoostPlus 扩展 |
| 场景 | 日常 SQL 开发、存储过程管理、数据库对象浏览 |
| 痛点 | 快捷键不符合习惯、ESC 无法关闭弹窗影响编码效率、连接解析失败导致功能不可用 |

### 1.4 核心使用场景

- **场景 1**：用户在 SQL 编辑器中选中表名，按 F12 快速打开该表的 CREATE 脚本
- **场景 2**：用户在 SQL 编辑器中选中关键字，按 F11 快速跳转到关键字查询
- **场景 3**：用户通过菜单操作时，能直观看到各功能的快捷键提示
- **场景 4**：代码补全弹窗出现后，用户按 ESC 关闭弹窗继续手动输入
- **场景 5**：用户点击"检查更新"后立即看到"正在检查..."提示
- **场景 6**：用户使用别名连接数据库服务器后，打开脚本/代码补全等功能正常工作

---

## 二、用户故事与验收条件

### US-01：F12 绑定打开脚本（P0）

> 作为 SQL 开发人员，我希望按 F12 能打开选中对象的脚本，以便快速查看表结构或存储过程定义

**验收条件：**
- Given 用户在 SQL 编辑器中选中一个对象名（如表名 `Users`）
- When 用户按 F12 键
- Then 系统获取该对象的定义脚本并在新窗口中打开
- And 弹窗可见时 F12 不触发打开脚本（先关闭弹窗）

### US-02：F11 绑定关键字查找（P0）

> 作为 SQL 开发人员，我希望按 F11 能触发关键字查询，以便快速搜索 SQL 关键字

**验收条件：**
- Given 用户在 SQL 编辑器中选中一段文本
- When 用户按 F11 键
- Then 系统弹出关键字查询窗口，显示选中文本的搜索结果
- And F11 + 修饰键（Shift/Ctrl/Alt）不触发关键字查询

### US-03：菜单显示快捷键提示（P1）

> 作为用户，我希望菜单项右侧显示对应的快捷键，以便我了解并使用快捷键操作

**验收条件：**
- Given 用户打开 SSMSBoost++ 顶部菜单或右键菜单
- When 菜单渲染完成
- Then 绑定了快捷键的菜单项右侧靠右对齐显示快捷键文字（如 `Ctrl+Shift+A`、`F12`）
- And 未绑定快捷键的菜单项不显示快捷键区域

**实现说明（v1.2 补充，2026-08-04）：**

背景：SSMS Shell 不加载第三方 VSCT KeyBindings，因此 VS 不会自动在菜单项右侧显示快捷键，必须手动注入快捷键文本。

| 菜单 | 实现路径 | 快捷键显示方案 |
|------|----------|----------------|
| 顶部菜单（SSMSBoost++） | VSCT 静态注册（Menus.ctmenu） | ① VSCT ButtonText 中加入 Tab 字符（`&#x9;`）+ 快捷键文本；② WpfContextMenuService 检测到 ctmenu 已注册时，仍通过 DTE `ShortcutText` 属性强制设置（双保险） |
| 右键菜单（SQL 编辑器） | DTE CommandBars 动态注入 | ① 首选 DTE `ShortcutText` 属性（VS 菜单系统自动右对齐）；② 降级 Caption 中 `\t` 制表符 |

快捷键映射：

| 菜单项 | 快捷键 | 顶部菜单 | 右键菜单 |
|--------|--------|----------|----------|
| 智能建议 | Ctrl+Shift+A | ✅ | ✅ |
| AI智能注释 | Ctrl+Shift+C | ✅ | ✅ |
| AI自动格式化 | Ctrl+Shift+F | ✅ | ✅ |
| AI性能分析 | Ctrl+Shift+P | ✅ | ✅ |
| AI 对话面板 | Ctrl+Shift+K | ✅ | ✅ |
| 检查当前脚本版本 | Ctrl+Shift+V | ✅ | ✅ |
| 关键词查询 | F11 | -（仅右键） | ✅ |
| 打开脚本 | F12 | -（仅右键） | ✅ |

**已知风险：**
- DTE `ShortcutText` 属性在 `Microsoft.VisualStudio.CommandBars.CommandBarControl` 静态类型上不存在，通过 dynamic 绑定运行时调用；若底层对象不支持则自动降级为 `\t` Caption 方案
- VSCT ButtonText 中的 Tab 字符（`&#x9;`）依赖 VS 菜单系统对 `\t` 的标准右对齐渲染，需部署后实测确认

### US-04：ESC 可靠关闭候选弹窗（P0）

> 作为 SQL 开发人员，我希望按 ESC 键能可靠关闭代码补全弹窗，以便我手动输入而不被弹窗干扰

**验收条件：**
- Given 代码补全弹窗正在显示
- When 用户按 ESC 键
- Then 弹窗立即关闭
- And ESC 事件被消费，不传播到 SSMS Shell
- And 弹窗关闭后继续输入字符时，弹窗可正常重新出现
- And 连续测试 20 次，成功率 100%

### US-05：检查更新即时反馈（P1）

> 作为用户，我希望点击"检查更新"后立即看到反馈，以便我知道系统正在响应我的操作

**验收条件：**
- Given 用户点击"检查更新"菜单项
- When 点击事件触发
- Then ≤200ms 内状态栏显示"正在检查更新..."或弹出进度提示
- And 检查完成后状态栏更新为结果（"已是最新版本"或"发现新版本"）
- And 检查过程中 UI 不卡顿

### US-06：服务器名正确解析（P0）

> 作为用户，我希望无论我在连接对话框中填写别名还是 IP 地址，代码补全和打开脚本等功能都能正常连接到数据库

**验收条件：**
- Given 用户在 SSMS 连接对话框中填写了别名（如 "SRS"）作为服务器名称
- And SSMS 成功连接到实际服务器（如 10.76.1.229）
- When 用户触发打开脚本或代码补全功能
- Then 系统使用 SSMS 内部的实际连接信息建立数据库连接
- And 不依赖窗口标题解析的服务器名作为 DataSource
- And 日志中记录实际服务器地址

---

## 三、功能规格说明

### 3.1 模块一：快捷键绑定调整（US-01, US-02）

#### 3.1.1 功能概述
将 F11（当前绑定"打开脚本"）改为 F12 绑定"打开脚本"，F11 绑定"关键字查询"。

#### 3.1.2 涉及文件与修改点

| 文件 | 修改内容 |
|------|----------|
| `SSMSBoostPlus.vsct` (行 498-504) | F11→F12（cmdidOpenScript），新增 F11→cmdidKeywordSearch |
| `OpenScriptCommand.cs` (行 58-183) | Win32 键盘钩子 `VK_F11`(0x7A) → `VK_F12`(0x7B)，方法名 `RegisterF11Interceptor` → `RegisterF12Interceptor` |
| `SSMSBoostPlusPackage.cs` (行 431, 453) | 调用 `RegisterF12Interceptor()` |
| `KeywordSearchCommand.cs` | 新增 Win32 键盘钩子 `RegisterF11Interceptor()`，拦截 F11 触发关键字查询 |
| `SSMSBoostPlusPackage.cs` | 在 DTE 就绪后调用 `KeywordSearchCommand.RegisterF11Interceptor()` |

#### 3.1.3 正常流程
1. SSMS 启动 → Package 初始化 → DTE 就绪
2. 注册 F12 Win32 键盘钩子 → 拦截 F12 → 检查是否在 SQL 文档 → 触发 OpenScriptCommand
3. 注册 F11 Win32 键盘钩子 → 拦截 F11 → 检查是否在 SQL 文档 → 触发 KeywordSearchCommand
4. 修饰键组合（Shift+F11/F12、Ctrl+F11/F12、Alt+F11/F12）不被拦截，放行给 SSMS

#### 3.1.4 异常流程
- 键盘钩子注册失败 → 日志记录，菜单命令仍可通过右键菜单使用
- SQL 文档未激活 → 不拦截按键，放行给 SSMS

#### 3.1.5 数据规则
- `VK_F11 = 0x7A`，`VK_F12 = 0x7B`
- 修饰键检测：`GetAsyncKeyState(VK_SHIFT/VK_CONTROL/VK_MENU) & 0x8000`

---

### 3.2 模块二：菜单快捷键显示（US-03）

#### 3.2.1 功能概述
在顶部菜单和右键菜单中，为绑定了快捷键的菜单项添加快捷键文字显示，靠右对齐。

#### 3.2.2 快捷键映射表

| 命令 ID | 命令名称 | 快捷键 | 显示文字 |
|---------|----------|--------|----------|
| cmdidAISuggestions | 智能建议 | Ctrl+Shift+A | `Ctrl+Shift+A` |
| cmdidAIComments | AI智能注释 | Ctrl+Shift+C | `Ctrl+Shift+C` |
| cmdidAIFormatting | AI自动格式化 | Ctrl+Shift+F | `Ctrl+Shift+F` |
| cmdidAIPerformance | AI性能分析 | Ctrl+Shift+P | `Ctrl+Shift+P` |
| cmdidAIChatPanel | AI 对话面板 | Ctrl+Shift+K | `Ctrl+Shift+K` |
| cmdidVersionGuardCheck | 检查当前脚本版本 | Ctrl+Shift+V | `Ctrl+Shift+V` |
| cmdidOpenScript | 打开脚本 | F12 | `F12` |
| cmdidKeywordSearch | 关键词查询 | F11 | `F11` |

#### 3.2.3 涉及文件与修改点

| 文件 | 修改内容 |
|------|----------|
| `WpfContextMenuService.cs` (行 64-94) | 在 `AiCommands` 和 `TopMenuCommands` 数组中新增 `ShortcutText` 字段 |
| `WpfContextMenuService.cs` AddAICommandsToMenu 方法 | 设置 CommandBarControl.Caption 时追加 `\t{快捷键}` 格式（利用 Tab 字符实现靠右对齐，这是 Windows 菜单的标准做法） |

#### 3.2.4 正常流程
1. 菜单项创建时，Caption 设置为 `显示文字\t快捷键`（如 `🤖 智能建议\tCtrl+Shift+A`）
2. Windows 菜单渲染引擎自动将 Tab 后的文字靠右对齐显示

#### 3.2.5 异常流程
- 如果 Tab 分隔方式不被 SSMS CommandBars 支持 → 降级为在 Caption 末尾用空格填充后追加快捷键文字

#### 3.2.6 交互说明
- 顶部菜单：SSMSBoost++ 下拉菜单中所有菜单项
- 右键菜单：SQL 编辑器右键菜单中 SSMSBoostPlus 子菜单项
- 快捷键文字使用浅灰色（如果 CommandBars API 支持）或跟随系统默认样式

---

### 3.3 模块三：ESC 关闭候选弹窗（US-04）

#### 3.3.1 功能概述
修复 ESC 键无法可靠关闭代码补全弹窗的问题。

#### 3.3.2 当前实现分析

当前 `CompletionCommandFilter.Exec()` 中有两处 ESC 拦截逻辑：

1. **行 3109-3116**：VSStd2K 命令组，`CMDID_CANCEL`(id=2) + `_isPopupVisible` → 关闭弹窗
2. **行 3176-3184**：VSStd2K 命令组，`CMDID_CANCEL`(id=2) → 关闭弹窗（在 `switch(nCmdID)` 块内）

**已知失败原因分析**：
- SSMS 22 中 ESC 可能通过**多个命令组**路由（VSStd2K、VSStd97 等），当前仅拦截 VSStd2K 组
- SSMS 22 中 `CMDID_CANCEL`(id=2) 可能同时被映射为 BACKSPACE，导致逻辑冲突
- 弹窗可见性判断 `_isPopupVisible` 可能存在时序问题（弹窗刚出现但标志未设置）
- SSMS 原生 IntelliSense 可能抢先消费 ESC 事件

#### 3.3.3 修改方案

| 修改点 | 内容 |
|--------|------|
| 新增 VSStd97 命令组 ESC 拦截 | 在 `Exec()` 中新增对 `guidVSStd97` 命令组的 `CMDID_CANCEL_97`(id=154) 的拦截 |
| 新增 Win32 键盘钩子兜底 | 在 `SqlTemplateCompletionService` 中注册 WH_KEYBOARD_LL 钩子，监听 `VK_ESCAPE`(0x1B)，当弹窗可见时拦截 ESC 并关闭弹窗 |
| 修复 _isPopupVisible 时序 | 在 `ShowOrUpdatePopup` 方法的**第一行**设置 `_isPopupVisible = true`，在 `HidePopup` 方法的**最后一行**设置 `_isPopupVisible = false` |
| 增强 DismissSsmsIntelliSense | 确保在关闭我们的弹窗前，先关闭 SSMS 原生 IntelliSense 弹窗 |

#### 3.3.4 正常流程
1. 弹窗显示 → `_isPopupVisible = true`
2. 用户按 ESC
3. Win32 键盘钩子优先捕获 → 检查 `_isPopupVisible` → 关闭 SSMS IntelliSense → 关闭我们的弹窗 → 拦截按键（返回 1）
4. `_isPopupVisible = false`，`_lastUserHideTime = now`，`_suppressAutoReopen = true`

#### 3.3.5 异常流程
- Win32 钩子未注册 → 回退到 IOleCommandTarget 拦截路径
- 弹窗不可见时按 ESC → 不拦截，放行给 SSMS
- 弹窗关闭后继续输入 → `_suppressAutoReopen` 在输入字符时清除，弹窗正常重新出现

#### 3.3.6 边界条件
- 弹窗正在动画过渡中 → 立即设 `_isPopupVisible = false` 并关闭
- SSMS 原生 IntelliSense 同时可见 → 先关闭 SSMS IntelliSense，再关闭我们的弹窗
- 连续快速按 ESC 多次 → 第一次关闭弹窗，后续不响应

---

### 3.4 模块四：检查更新响应优化（US-05）

#### 3.4.1 功能概述
优化"检查更新"按钮的响应速度，添加即时 UI 反馈。

#### 3.4.2 当前问题分析

当前 `VersionCheckCommand.Execute()` → `CheckForUpdateAsync(isManual: true)` 流程：
1. `_versionService.GetLocalVersionHash()` — 读取本地文件，快（<10ms）
2. `_versionService.GetLatestReleaseHashAsync()` — Git ls-remote 网络操作，慢（2-10秒）
3. 检查完成后才显示 MessageBox

**问题**：步骤 1-2 期间 UI 无任何反馈，用户感觉按钮无响应。

#### 3.4.3 修改方案

| 修改点 | 内容 |
|--------|------|
| 即时状态栏反馈 | `Execute()` 方法第一行立即设置状态栏文字 `"SSMSBoostPlus: 正在检查更新..."` |
| 进度对话框（可选） | 弹出非模态进度提示窗口（如"正在检查更新，请稍候..."），检查完成后自动关闭 |
| 异步不阻塞 | 确保 `GetLatestReleaseHashAsync` 在后台线程执行，不阻塞 UI 线程 |
| 超时处理 | 添加 15 秒超时机制，超时后显示"检查更新超时，请检查网络连接" |
| 取消支持 | 进度窗口支持"取消"按钮，取消后中止检查 |

#### 3.4.4 正常流程
1. 用户点击"检查更新"
2. ≤200ms 内状态栏显示 `"SSMSBoostPlus: 正在检查更新..."`
3. 后台执行 Git ls-remote 获取远程版本
4. 检查完成 → 状态栏更新 → 显示结果对话框
5. 清除状态栏文字

#### 3.4.5 异常流程
- 网络超时（>15秒） → 显示"检查更新超时"
- Git 不可用 → 显示"未找到 Git"
- 远程仓库不可达 → 显示"无法连接到代码仓库"

---

### 3.5 模块五：服务器名解析 BUG 修复（US-06）

#### 3.5.1 功能概述
修复当用户在 SSMS 连接对话框中使用别名连接时，代码从窗口标题解析到别名而非实际服务器地址，导致无法建立数据库连接的问题。

#### 3.5.2 BUG 根因分析

**日志证据**（ssmsboostplus-2026073117.log 行 208-588）：

```
Window caption: SQLQuery1.sql - SRS.SR (SRSUser (103))
Parsed: Server=SRS, DB=SR, User=SRSUser, SPID=103
→ ConnectionCache 缓存未命中，服务器: SRS
→ No valid cached credentials, falling back to strategies B~E
→ 策略B: 找到 UIConnectionInfo，但未能提取连接信息
→ 策略C: VsServiceProvider is null
→ 策略D: ConnectionStore 找到但无可用实例
→ 策略E: ScriptFactory 抛 NullReferenceException
→ 全部 8 个 Provider 均失败
```

**根因**：
1. **策略A（窗口标题解析）**：从标题 `SRS.SR (SRSUser (103))` 解析到 Server=SRS，但 SRS 是用户填写的别名/友好名称，不是可解析的服务器地址。且 SQL Server Auth 模式下没有缓存密码，无法建立连接。
2. **策略B（ObjectExplorer 深度探索）**：成功获取到 `UIConnectionInfo` 对象，但 `ExtractFromObject` 方法不识别 `UIConnectionInfo` 类型，无法从中提取 ServerName/Password/DatabaseName。`UIConnectionInfo` 的数据库名存储在 `AdvancedOptions` 中，当前代码未处理此结构。
3. **策略C-E**：SSMS 22 内部 API 变更导致全部失败。

#### 3.5.3 修改方案

| 修改点 | 内容 |
|--------|------|
| **策略B 增强：UIConnectionInfo 提取** | 在 `ExtractFromObject` 方法中新增对 `UIConnectionInfo` 类型的专门处理：提取 `ServerName`、`UserName`、`Password`、`AdvancedOptions.Get("DATABASE")` |
| **策略A 增强：服务器名解析验证** | 从窗口标题解析到服务器名后，检查该名称是否可解析（DNS/别名），如不可解析则不返回，继续尝试策略 B~E |
| **策略B 增强：UIConnectionInfo 优先** | 策略B 获取到 `UIConnectionInfo` 后，直接调用 `BuildConnectionInfo(serverName, databaseName, userName, password, encryptConn, trustCert)` 构建连接 |
| **新增策略：从 OE 获取已连接的 SqlConnection** | 策略B 中尝试从 ObjectExplorer 的导航树中获取已打开的 `ServerConnection`，其 `ConnectionContext.SqlConnection` 包含实际服务器地址 |

#### 3.5.4 正常流程
1. 策略A 解析窗口标题 → Server=SRS, User=SRSUser → 检查缓存 → 未命中 → 返回 null
2. 策略B ObjectExplorer → 获取 `UIConnectionInfo` → **新增：提取 ServerName/实际IP或别名, UserName/SRSUser, Password/*****, Database/SR** → 构建连接字符串 → 返回 ConnectionInfo
3. 后续功能（打开脚本、代码补全）使用正确的连接信息

#### 3.5.5 异常流程
- 策略B UIConnectionInfo 的 Password 为空 → 缓存 ServerName 和 UserName，仍返回 null（无密码无法连接 SQL Auth）
- 所有策略均失败 → 日志记录详细信息，提示用户"未检测到活动连接"

#### 3.5.6 边界条件
- Windows 认证模式 → UIConnectionInfo 无 Password，使用 `Integrated Security=True`
- 多个连接窗口 → 优先使用活动窗口的连接
- 用户切换窗口 → 重新获取连接信息

---

## 四、数据埋点与指标

### 4.1 关键行为事件

| 事件名 | 触发时机 | 采集数据 |
|--------|----------|----------|
| `shortcut_key_used` | 用户使用快捷键触发功能 | 快捷键（F11/F12/Ctrl+Shift+*）、命令名称 |
| `esc_close_popup` | 用户按 ESC 关闭弹窗 | 成功/失败、弹窗类型 |
| `check_update_clicked` | 用户点击检查更新 | 响应时间(ms)、结果 |
| `connection_parsed` | 连接信息解析完成 | 策略名称、服务器名、成功/失败 |

### 4.2 核心指标

| 指标 | 计算方式 | 目标值 |
|------|----------|--------|
| ESC 关闭弹窗成功率 | `esc_close_popup(成功) / esc_close_popup(总数)` | ≥99% |
| 检查更新响应时间 | `check_update_clicked` 的 `响应时间` 中位数 | ≤200ms |
| 连接解析成功率 | `connection_parsed(成功) / connection_parsed(总数)` | ≥95% |
| 快捷键使用率 | `shortcut_key_used / 命令触发总数` | ≥30% |

---

## 五、非功能需求

| 维度 | 要求 |
|------|------|
| 性能 | ESC 拦截延迟 ≤16ms（一帧内完成）；检查更新即时反馈 ≤200ms |
| 兼容性 | SSMS 22（V22.x），Windows 10/11 |
| 稳定性 | Win32 键盘钩子不阻塞 SSMS 主线程（回调 ≤300ms 内完成） |
| 安全 | 日志中不记录完整连接串（仅记录 Server 和 Database 名） |
| 日志 | 所有快捷键触发、ESC 拦截、连接解析过程记录到日志文件 |

---

## 六、验收标准

### 6.1 核心功能验收清单

| 编号 | 验收项 | 验收方法 |
|------|--------|----------|
| AC-01 | F12 在 SQL 文档中触发打开脚本 | 选中表名 → 按 F12 → 新窗口打开表结构脚本 |
| AC-02 | F11 在 SQL 文档中触发关键字查询 | 选中文本 → 按 F11 → 弹出关键字查询窗口 |
| AC-03 | F12/F11 + 修饰键不触发 | Shift+F12/Ctrl+F11 → 不触发各自功能 |
| AC-04 | 顶部菜单显示快捷键 | 打开 SSMSBoost++ 菜单 → 菜单项右侧显示快捷键 |
| AC-05 | 右键菜单显示快捷键 | 右键 SQL 编辑器 → SSMSBoostPlus 子菜单 → 快捷键显示 |
| AC-06 | ESC 关闭弹窗（20次连续） | 弹窗显示 → ESC → 弹窗关闭，连续 20 次无失败 |
| AC-07 | ESC 后继续输入弹窗可重现 | ESC 关闭弹窗 → 输入字符 → 弹窗重新出现 |
| AC-08 | 检查更新即时反馈 | 点击按钮 → ≤200ms 状态栏显示"正在检查..." |
| AC-09 | 别名连接下打开脚本可用 | 用别名连接 → 选中表名 → F12 → 成功打开脚本 |
| AC-10 | 别名连接下代码补全可用 | 用别名连接 → 输入 SQL → 代码补全弹窗正常显示对象列表 |

### 6.2 异常场景验收清单

| 编号 | 验收项 |
|------|--------|
| EX-01 | 键盘钩子注册失败 → 菜单命令仍可用 |
| EX-02 | 弹窗不可见时按 ESC → 不影响编辑器 |
| EX-03 | 检查更新网络超时 → 显示超时提示 |
| EX-04 | 无活动连接时触发功能 → 友好提示 |

---

## 七、上线计划建议

### 7.1 灰度策略

| 阶段 | 范围 | 观察指标 |
|------|------|----------|
| 阶段1 | 开发者自测 | 全部 AC 通过 |
| 阶段2 | 1-2 名用户试用 | ESC 成功率、连接解析成功率 |
| 阶段3 | 全量发布 | 所有指标达标 |

### 7.2 回滚条件

- ESC 拦截导致 SSMS 编辑器按键异常（退格/删除失效）
- Win32 键盘钩子导致 SSMS 卡顿或崩溃
- 连接解析修改导致原有正常连接场景退化

### 7.3 上线后观察指标

- 日志中 `connection_parsed` 成功率 ≥95%
- 日志中无 `ESC 拦截异常` 错误
- 用户反馈 ESC 关闭弹窗功能正常

---

## 八、产品风险标注与建议

| 风险类型 | 风险描述 | 等级 | 建议 |
|----------|----------|------|------|
| 技术可行性 | Win32 键盘钩子（F11/F12/ESC）可能与 SSMS 内部快捷键冲突 | 中 | 仅在 SQL 文档激活时拦截，修饰键组合放行 |
| 技术可行性 | DTE CommandBars 的 Caption `\t` 分隔符在 SSMS 22 中可能不支持靠右对齐 | 中 | 需实际验证，如不支持则降级为空格填充方案 |
| 用户接受度 | F11/F12 快捷键变更可能影响已有使用习惯 | 低 | 在菜单中显示快捷键提示，帮助用户适应 |
| 技术可行性 | UIConnectionInfo 的 Password 属性在 SSMS 22 中可能被保护无法读取 | 高 | 需验证 Password 属性可访问性，如不可读则依赖 ConnectionCache 缓存密码机制 |
| 竞品威胁 | 无直接竞品威胁 | 低 | — |

---

## 九、优先级与实施建议

> **变更记录（2026-08-01）**：根据用户指示，US-06（服务器名解析 BUG）暂时搁置，本轮不处理。实施顺序相应调整。

| 优先级 | 需求 | 建议实施顺序 | 理由 |
|--------|------|-------------|------|
| P0 | US-04 ESC 关闭弹窗 | 第1个 | 高频使用场景，用户多次反馈，影响编码效率 |
| P0 | US-01/US-02 F11/F12 快捷键 | 第2个 | 与 ESC 修复共享 Win32 键盘钩子基础设施，合并实施成本更低 |
| P1 | US-05 检查更新优化 | 第3个 | 体验优化，不影响核心功能 |
| P1 | US-03 菜单快捷键显示 | 第4个 | 体验优化，依赖快捷键调整完成后实施 |
| ~~P0~~ | ~~US-06 服务器名解析 BUG~~ | ~~暂缓~~ | 用户决定本轮不处理，后续单独排期 |
