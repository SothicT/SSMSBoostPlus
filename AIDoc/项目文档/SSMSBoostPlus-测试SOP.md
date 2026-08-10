# SSMSBoostPlus 测试 SOP

本文档描述 SSMSBoostPlus 插件的完整测试流程，覆盖版本守护（Version Guard）和 AI 代码助手（AI Assistant）两大功能。测试分为三个层次：L1 单元测试、L2 集成测试、L3 SSMS 内手动交互测试。

---

## 1. 测试环境准备

### 1.1 编译与部署

每次代码变更后，必须重新编译部署再进行测试：

```powershell
cd F:\2.C#_Core\SSMSBoostPlus

# 清理并重新编译
dotnet clean src/SSMS/SSMSBoostPlus.SSMS.csproj -c Release
dotnet build src/SSMS/SSMSBoostPlus.SSMS.csproj -c Release

# 执行部署脚本（需交互确认）
.\deploy-to-ssms.bat
```

**部署产物验证**（部署完成后确认）：

| 文件 | 预期路径 | 说明 |
|------|----------|------|
| SSMSBoostPlus.SSMS.dll | bin\Release\net472\ | 主程序集 |
| SSMSBoostPlus.cto | obj\Release\net472\ | 嵌入的命令表 |
| SSMSBoostPlus.pkgdef | obj\Release\net472\ | 包注册文件 |
| SSMSBoostPlus.SSMS.dll | SSMS 扩展目录 | 实际加载位置 |

**部署后强制刷新**（每次部署后必须执行）：

```powershell
# 方法一：运行 SSMS 安装命令
& "D:\Microsoft SQL Server Management Studio 22\Common7\IDE\Ssms.exe" /setup

# 方法二：直接重启 SSMS（推荐）
# 关闭所有 SSMS 实例后重新打开
```

### 1.2 日志开启与查看

插件日志路径：`%LOCALAPPDATA%\Microsoft\SSMS\22.0_79c71968\Extensions\SSMSBoostPlus\logs\`

每次测试前清空日志，测试后检查异常：

```powershell
# 查看最新日志
Get-Content "$env:LOCALAPPDATA\Microsoft\SSMS\22.0_79c71968\Extensions\SSMSBoostPlus\logs\*.log" -Tail 50 -Wait

# 格式化查看异常
Get-Content "$env:LOCALAPPDATA\Microsoft\SSMS\22.0_79c71968\Extensions\SSMSBoostPlus\logs\*.log" | Select-String "Error|Exception|WARN"
```

**ActivityLog 路径**（SSMS 启动异常时必查）：
`%APPDATA%\Microsoft\SQL Server Management Studio\22.0_ActivityLog.xml`

> 注意：ActivityLog.xml 中的时间为 UTC（世界协调时间），北京时间需 +8 小时。

### 1.3 测试数据库准备

建议使用 SQL Server LocalDB 或一个独立的测试数据库实例，避免影响生产数据。

**预置测试对象**（在测试库中创建以下对象，命名统一前缀 `SSMSBoostTest_`）：

```sql
-- 存储过程
CREATE OR ALTER PROCEDURE SSMSBoostTest_GetUserById
    @UserId INT
AS
BEGIN
    SELECT 'Test User' AS Name, @UserId AS UserId;
END
GO

-- 视图
CREATE OR ALTER VIEW SSMSBoostTest_ActiveUsers
AS
SELECT 1 AS Id, 'Active User' AS Name
GO

-- 函数
CREATE OR ALTER FUNCTION SSMSBoostTest_CalculateTotal(@OrderId INT)
RETURNS INT
AS
BEGIN
    RETURN @OrderId * 100;
END
GO

-- 多对象引用脚本（用于版本守护引用测试）
-- SSMSBoostTest_GetUserById.sql 内容：
/*
EXEC SSMSBoostTest_GetUserById @UserId = 1;
GO
SELECT * FROM SSMSBoostTest_ActiveUsers;
GO
*/
```

---

## 2. 版本守护（Version Guard）测试

版本守护通过比对脚本中引用的数据库对象修改时间（`modify_date`）与脚本打开时间，判断脚本是否过期，防止在不感知的情况下执行了旧版本存储过程。

### 2.1 启用与禁用

**前置条件**：SSMS 已加载插件，连接到测试数据库。

**测试步骤**：

1. 打开 SSMS，新建查询窗口，连接到测试数据库。
2. 在查询窗口中输入：`SELECT 1;`（任意 SQL，只要不是 DDL）。
3. 点击工具栏上的 **SSMSBoost++** 主菜单，确认下拉菜单显示正常。
4. 点击 **版本守护** 子菜单，确认子菜单项全部可见。
5. 点击 **启用版本守护**（带勾选标记表示已启用），切换为禁用状态。
6. 再次点击 **启用版本守护**，恢复启用状态。

**预期结果**：每次切换后，状态栏或弹窗显示当前版本守护状态（启用/禁用），日志中记录状态变更。

{{Screenshot: 版本守护启用状态切换}}

### 2.2 版本检查 — 脚本过期场景

**目的**：验证当脚本中引用的数据库对象在脚本打开后被修改时，插件能够正确检测并弹窗警告。

**测试步骤**：

1. 确认版本守护处于启用状态。
2. 打开 **SSMSBoostTest_GetUserById.sql** 文件（使用 SSMS 的「文件 → 打开 → 文件」）。
3. 记录当前时间（精确到秒，后续用）。
4. **不关闭脚本窗口**，在另一个查询窗口中执行修改：
   ```sql
   ALTER PROCEDURE SSMSBoostTest_GetUserById
       @UserId INT
   AS
   BEGIN
       SELECT 'Modified User' AS Name, @UserId AS UserId, GETDATE() AS ModifiedAt;
   END
   GO
   ```
5. 回到脚本窗口，点击 **执行**（F5）。
6. 观察弹窗内容。

**预期结果**：弹窗标题为「版本冲突」或类似，提示 `SSMSBoostTest_GetUserById` 对象的数据库版本已更新，并显示过期对象列表。

{{Screenshot: 版本过期弹窗}}

**变体测试 — 无过期**：

- 修改脚本内容后执行（不改数据库），应不触发版本守护弹窗。
- 修改数据库对象后执行（不在版本守护启用状态下），应不拦截。

### 2.3 版本检查 — 从数据库重新加载

**目的**：验证「从数据库重新加载」功能能正确刷新对象版本快照。

**测试步骤**：

1. 打开 **SSMSBoostTest_GetUserById.sql**，执行一次（确保快照已记录）。
2. 在另一个查询窗口中修改存储过程（加一列）。
3. 在脚本窗口中点击 **版本守护 → 从数据库重新加载**。
4. 再次执行脚本（本次修改）。

**预期结果**：重新加载后，插件重新获取当前数据库中的对象定义，后续版本检查基于新的快照进行。

{{Screenshot: 从数据库重新加载菜单}}

### 2.4 特殊标记 — #nosynccheck（跳过检查）

**目的**：`#nosynccheck` 标记应使版本守护完全跳过该脚本的检查。

**测试步骤**：

1. 新建查询窗口，输入以下内容：
   ```sql
   -- #nosynccheck
   ALTER PROCEDURE SSMSBoostTest_GetUserById
       @UserId INT
   AS
   BEGIN
       SELECT 'Skipped' AS Name;
   END
   GO
   ```
2. 执行脚本（F5）。

**预期结果**：版本守护检测到 `#nosynccheck` 注释，跳过版本检查，脚本正常执行，无弹窗。日志中有对应记录。

### 2.5 特殊标记 — #checklater（稍后检查）

**目的**：`#checklater` 标记应使版本守护提示用户是否立即检查。

**测试步骤**：

1. 新建查询窗口，输入以下内容：
   ```sql
   -- #checklater
   CREATE OR ALTER PROCEDURE SSMSBoostTest_TempProc
   AS
   BEGIN
       SELECT 'Temp';
   END
   GO
   ```
2. 执行脚本（F5）。

**预期结果**：插件识别 `#checklater` 标记，弹窗询问用户是立即检查版本还是继续执行。

### 2.6 版本守护配置对话框

**目的**：验证版本守护设置能正确保存和生效。

**测试步骤**：

1. 点击 **版本守护 → 版本守护设置...**。
2. 打开设置对话框后，尝试以下操作：
   - **排除数据库**：添加 `tempdb`，确认保存后 `tempdb` 中的对象不触发版本检查。
   - **排除对象**：添加 `SSMSBoostTest_*`，确认带此前缀的对象全部被排除。
   - **默认操作**：切换「询问用户」/「阻止执行」/「仅警告」，验证每次执行后行为符合选择。
   - **标记符号**：修改 `#nosynccheck` 和 `#checklater` 的默认符号（可测试非默认符号不触发）。
3. 关闭对话框，重复 2.2 测试，确认配置已生效。

**预期结果**：所有设置项保存后重启 SSMS 仍然生效（配置持久化到 JSON 文件）。

{{Screenshot: 版本守护设置对话框}}

### 2.7 版本守护单元测试（L1）

```powershell
cd F:\2.C#_Core\SSMSBoostPlus

# 运行所有单元测试
dotnet test tests/SSMSBoostPlus.Tests -c Release --verbosity normal
```

**版本守护相关测试用例**：

| 测试类 | 测试方法 | 验证内容 |
|--------|----------|----------|
| `ObjectExtractorTests` | `ExtractObjects_CreateProcedure_ExtractsCorrectly` | 从 CREATE PROCEDURE 提取对象引用 |
| `ObjectExtractorTests` | `ExtractObjects_AlterFunction_ExtractsCorrectly` | 从 ALTER FUNCTION 提取对象引用 |
| `ObjectExtractorTests` | `ExtractObjects_MultipleObjects_ExtractsAll` | 从多对象脚本提取全部引用 |
| `ObjectExtractorTests` | `DetectTokens_SkipToken_ReturnsSkip` | `#nosynccheck` 标记检测 |
| `ObjectExtractorTests` | `DetectTokens_AskToken_ReturnsAsk` | `#checklater` 标记检测 |
| `ObjectExtractorTests` | `DetectTokens_NoTokens_ReturnsNone` | 无标记时正常检查 |
| `VersionModelsTests` | `VersionInfo_IsOutdated_DbModifiedAfterOpen_ReturnsTrue` | DB 修改时间 > 打开时间 → 过期 |
| `VersionModelsTests` | `VersionInfo_IsOutdated_DbModifiedBeforeOpen_ReturnsFalse` | DB 修改时间 < 打开时间 → 未过期 |
| `VersionModelsTests` | `VersionCheckResult_HasOutdated_WithOutdatedObjects_ReturnsTrue` | 过时对象列表汇总 |
| `ExecutionInterceptorTests` | `VersionGuardConfiguration_DefaultIsEnabled` | 默认启用 |
| `ExecutionInterceptorTests` | `VersionGuardConfiguration_Disabled_ShouldAllowExecution` | 禁用时不拦截 |
| `ExecutionInterceptorTests` | `SkipToken_ShouldBypassCheck` | 跳过标记绕过检查 |
| `ExecutionInterceptorTests` | `NoToken_ShouldProceedWithCheck` | 无标记时正常检查 |
| `ExecutionInterceptorTests` | `IsSqlExecuteCommand_KnownSSMSSqlGuid_ExecuteSql_ReturnsTrue` | SQL 执行命令识别 |

**全部通过的预期输出**：

```
Passed! - Failed: 0, Passed: XX, Skipped: 0, Total: XX
```

---

## 3. AI 代码助手测试

AI 代码助手通过调用 AI API（优先 Codebuddy CLI，备选 HTTP API）为 SQL 脚本提供智能建议、注释生成、格式化代码和性能分析功能。

### 3.1 AI 配置 — 首次配置

**前置条件**：SSMS 已加载插件，未配置过 AI（首次使用）。

**测试步骤**：

1. 点击 **SSMSBoost++ → AI 配置...**。
2. 打开 AI 配置对话框，包含以下字段：
   - **API 类型**：Codebuddy CLI / OpenAI 兼容 API
   - **API Key**：（仅 HTTP API 时填写）
   - **端点 URL**：（仅 HTTP API 时填写）
   - **模型选择**：下拉列表（若为空则需网络连接后刷新）
   - **Temperature**：滑块 0.0 ~ 1.0，默认 0.3
   - **最大 Token 数**：输入框，默认 4000
   - **超时秒数**：输入框，默认 120
   - **格式化风格**：Microsoft / Allman，默认 Microsoft
   - **连接测试** 按钮
3. 点击 **连接测试**，观察反馈。
4. 填写有效配置后点击 **确定**，观察保存反馈。
5. 重启 SSMS 后重新打开配置，确认配置已持久化。

**预期结果**：配置保存后，AI 功能菜单项变为可用状态；配置错误时，状态栏或日志中有明确错误信息。

{{Screenshot: AI 配置对话框}}

### 3.2 智能建议（Suggestions）

**目的**：验证 AI 能为当前 SQL 脚本提供智能建议。

**测试步骤**：

1. 在查询窗口中输入以下 SQL（故意包含可优化的写法）：
   ```sql
   SELECT * FROM Users WHERE IsActive = 1 AND Name <> ''
   ```
2. 将光标放在查询窗口内。
3. 点击 **SSMSBoost++ → AI 代码助手 → 智能建议**（或使用快捷键 Ctrl+Shift+A）。
4. 等待 AI 返回结果（通常 5~30 秒，视网络和模型而定）。
5. 观察 Diff 对比窗口。

**预期结果**：
- Diff 对比窗口打开，显示原 SQL 与建议版本的差异。
- 差异以统一格式（unified diff）展示，新增行绿色，删除行红色。
- 窗口底部有「应用建议」和「忽略」按钮。

{{Screenshot: 智能建议 Diff 对比窗口}}

**无建议场景**：

- 输入 `SELECT 1;`，应提示「未发现问题，无需建议」或类似信息。

### 3.3 添加注释（Add Comments）

**目的**：验证 AI 能为 SQL 脚本自动添加注释。

**测试步骤**：

1. 输入以下 SQL（无注释）：
   ```sql
   CREATE PROCEDURE dbo.GetOrderSummary
       @OrderId INT
   AS
   BEGIN
       SELECT OrderId, SUM(Amount) AS TotalAmount
       FROM Orders
       WHERE OrderId = @OrderId
       GROUP BY OrderId;
   END
   ```
2. 点击 **AI 代码助手 → 添加注释**（或 Ctrl+Shift+C）。
3. 等待 AI 返回。

**预期结果**：Diff 窗口显示原脚本与带注释版本的对比，注释应包含：
- 存储过程名称和用途说明
- 参数说明
- 主要逻辑行注释

{{Screenshot: 添加注释 Diff 对比}}

### 3.4 格式化代码（Format Code）

**目的**：验证 AI 能按指定风格格式化 SQL 代码。

**前置条件**：在 AI 配置中确认「格式化风格」设置正确（Microsoft 或 Allman）。

**测试步骤**：

1. 输入以下未格式化的 SQL（全部挤在一行）：
   ```sql
   CREATE PROCEDURE dbo.GetUserById @UserId INT AS SELECT Id,Name,Email FROM Users WHERE Id=@UserId;
   ```
2. 点击 **AI 代码助手 → 格式化代码**（或 Ctrl+Shift+F）。
3. 等待 AI 返回。

**预期结果**：Diff 窗口显示格式化后的结果，关键改动包括：关键字大写、缩进统一、逗号前置、关键字换行（Allman 风格）。

{{Screenshot: 格式化代码 Diff 对比}}

**格式化风格验证**：
- 配置为 Microsoft 风格：关键字小写 / 大写混用，简洁单行格式
- 配置为 Allman 风格：BEGIN / END / SELECT / FROM 各自占一行

### 3.5 性能分析（Performance Analysis）

**目的**：验证 AI 能分析 SQL 性能并提供优化建议（含执行计划分析）。

**测试步骤**：

1. 输入以下低效 SQL：
   ```sql
   SELECT * FROM Orders WHERE OrderDate >= '2024-01-01' AND OrderDate <= '2024-12-31'
   ```
2. 点击 **AI 代码助手 → 性能分析**（或 Ctrl+Shift+P）。
3. 等待 AI 返回分析结果。

**预期结果**：Diff 窗口显示分析报告，包含：
- 性能问题描述（全文，非截图）
- 优化建议（具体 SQL 写法或索引建议）
- 预期性能提升（如适用）

> 注意：性能分析需要 SQL 执行计划（XML），部分场景下 AI 会请求 SSMS 提供执行计划数据。

{{Screenshot: 性能分析结果}}

### 3.6 AI 代码助手单元测试（L1）

```powershell
cd F:\2.C#_Core\SSMSBoostPlus
dotnet test tests/SSMSBoostPlus.Tests -c Release --verbosity normal
```

**AI 相关测试用例**：

| 测试类 | 测试方法 | 验证内容 |
|--------|----------|----------|
| `AIModelsTests` | `AIResponse_Defaults_AreCorrect` | AIResponse 默认值正确 |
| `AIModelsTests` | `AIResponse_Failure_SetsError` | 失败响应设置错误信息 |
| `DiffCalculatorTests` | `CalculateDiff_IdenticalTexts_NoHunks` | 相同文本无差异块 |
| `DiffCalculatorTests` | `CalculateDiff_DifferentTexts_HasHunks` | 不同文本产生差异块 |
| `DiffCalculatorTests` | `CalculateDiff_SqlStoredProcedure_DetectsChanges` | 存储过程变更检测 |
| `DiffCalculatorTests` | `GetUnifiedDiff_ProducesOutput` | 统一格式差异输出格式正确 |
| `DiffCalculatorTests` | `GetSideBySideDiff_ReturnsNonNull` | 并排差异生成非空 |
| `ConfigurationModelsTests` | `AIConfiguration_Defaults_AreCorrect` | AIConfiguration 默认参数正确 |

### 3.7 AI 桥接层集成测试（L2）

**目的**：验证 BridgeFactory 自动选择逻辑（CLI 优先 → HTTP API 备选）。

**测试场景与预期**：

| 场景 | BridgeFactory 行为 | 验证方式 |
|------|--------------------|----------|
| Codebuddy CLI 已安装且可用 | 返回 `CodebuddyCliBridge` | 检查日志输出包含 `Using CLI Bridge` |
| CLI 不可用，HTTP API 已配置 | 返回 `AIApiClientBridge` | 检查日志输出包含 `Using HTTP API Bridge` |
| CLI 不可用，HTTP API 未配置 | 返回 `AIApiClientBridge`，调用时报错 | 触发错误弹窗或日志异常 |
| HTTP API 配置错误（无效 Key） | AI 返回 `IsSuccess = false` | Diff 窗口显示错误信息 |

**测试步骤**（手动切换验证）：

1. 确认 Codebuddy CLI 已安装（运行 `codebuddy --version`）。
2. 执行 AI 功能，确认日志显示使用 CLI 桥接。
3. 临时重命名 Codebuddy CLI 可执行文件使其不可用（模拟 CLI 不存在）。
4. 执行 AI 功能，确认日志切换为 HTTP API 桥接。
5. 恢复 CLI 名称，验证恢复。

---

## 4. 异常场景测试

### 4.1 无网络连接

**测试步骤**：

1. 断开主机网络（禁用网卡或拔网线）。
2. 启动 SSMS。
3. 尝试执行 AI 功能（智能建议）。
4. 恢复网络。

**预期结果**：弹窗提示「无法连接到 AI 服务，请检查网络连接」，插件不崩溃，其他功能（版本守护）正常可用。

### 4.2 无数据库连接

**测试步骤**：

1. SSMS 连接到测试数据库。
2. 执行脚本过程中，**断开数据库连接**（或停止 SQL Server 服务）。
3. 执行包含数据库引用的 SQL。

**预期结果**：版本守护检测到无连接，弹窗提示「无法获取数据库对象信息」，脚本不执行或弹窗后用户可选择继续。

### 4.3 AI API Key 无效

**测试步骤**：

1. 在 AI 配置中填写一个明显无效的 API Key（如 `invalid_key_12345`）。
2. 保存配置。
3. 执行 AI 功能（智能建议）。

**预期结果**：弹窗或日志显示「API 认证失败，请检查 API Key」，不输出无意义的随机内容。

### 4.4 AI 请求超时

**前置条件**：将 AI 配置中的「超时秒数」设为极小值（如 1 秒）。

**测试步骤**：

1. 配置超时为 1 秒。
2. 执行 AI 功能（性能分析，内容较长，通常耗时较长）。
3. 观察超时行为。

**预期结果**：超过 1 秒后中断请求，弹窗提示「AI 请求超时」，插件不卡死。

### 4.5 AI API 限流（429 Too Many Requests）

**测试步骤**：

1. 在短时间内（< 1 分钟）连续执行多个 AI 功能（智能建议 → 添加注释 → 格式化 → 性能分析）。
2. 观察 API 限流时的行为。

**预期结果**：限流时弹窗提示「请求过于频繁，请稍后再试」，等待一段时间后自动恢复。

### 4.6 快捷键冲突

**测试步骤**：

1. 在 SSMS 中尝试按下各 AI 功能的快捷键：
   - `Ctrl+Shift+A`：智能建议
   - `Ctrl+Shift+C`：添加注释
   - `Ctrl+Shift+F`：格式化代码
   - `Ctrl+Shift+P`：性能分析
   - `Ctrl+Shift+V`：版本守护启用/禁用切换
2. 若快捷键被其他插件或 SSMS 本身占用，观察行为。

**预期结果**：SSMSBoostPlus 定义的快捷键优先响应，不被其他功能覆盖。若发生冲突，在日志中有警告记录。

---

## 5. 部署回归检查清单

每次执行 `deploy-to-ssms.bat` 部署后，必须完成以下全部检查项：

### 5.1 编译产物完整性

| 检查项 | 验证命令 |
|--------|----------|
| 编译无错误 | `dotnet build -c Release` 输出 `0 Error(s)` |
| CTO 文件生成 | `Get-ChildItem obj -Recurse -Filter *.cto \| Select FullName, Length` |
| PkgDef 文件生成 | `Get-ChildItem obj -Recurse -Filter *.pkgdef \| Select FullName, Length` |
| Menus.ctmenu 生成 | `Get-ChildItem obj -Recurse -Filter Menus.ctmenu \| Select FullName, Length` |
| bin 目录产物齐全 | `Get-ChildItem bin/Release/net472 -Filter *.dll \| Select Name` |

### 5.2 SSMS 内注册验证

| 检查项 | 预期结果 |
|--------|----------|
| SSMSBoost++ 主菜单可见 | 工具栏或菜单栏出现 SSMSBoost++ 菜单 |
| 版本守护子菜单可见 | 子菜单包含 4 个子项 |
| AI 代码助手子菜单可见 | 子菜单包含 4 个子项 |
| 所有菜单项有中文文本 | 版本守护、AI 代码助手、启用版本守护... 等 |
| 版本守护快捷键 Ctrl+Shift+V 响应 | 在查询窗口按下后切换启用状态 |
| AI 配置快捷键 Ctrl+Shift+, 响应 | 打开 AI 配置对话框 |
| ActivityLog 无 Package 相关错误 | `Select-String "SSMSBoostPlus" ActivityLog.xml` 无 Error |

### 5.3 配置持久化验证

| 检查项 | 验证方法 |
|--------|----------|
| AI 配置保存后重启 SSMS 仍生效 | 修改配置 → 重启 SSMS → 重新打开配置，对话框显示已保存的值 |
| 版本守护配置保存后重启 SSMS 仍生效 | 同上，检查排除数据库、标记符号等 |
| 首次启动无配置文件时使用默认值 | 删除配置 JSON，重启 SSMS，AI 和版本守护使用默认值运行 |

**配置文件路径**：
`%LOCALAPPDATA%\Microsoft\SSMS\22.0_79c71968\Extensions\SSMSBoostPlus\settings.json`

---

## 6. 已知限制与测试注意事项

1. **SQL Server LocalDB 连接字符串**：测试时若使用 LocalDB，连接字符串需指定 `(localdb)\MSSQLLocalDB`，不可留空。
2. **跨数据库引用**：版本守护仅检查当前连接的数据库中的对象，跨服务器引用（linked server）暂不支持。
3. **AI 响应质量**：AI 功能的输出质量取决于所选模型，测试以功能流程正确为关注点，不以 AI 输出内容的文学质量为验收标准。
4. **Diff 窗口中文显示**：Diff 对比窗口依赖 SSMS 的文本渲染，部分字体配置下中文字符可能显示为方框，这是 SSMS 自身问题，与插件无关。
5. **执行计划获取**：性能分析功能需要 SSMS 执行查询并获取 XML 执行计划，若查询无结果集（如 DDL 语句），性能分析功能自动降级为静态 SQL 分析。

---

## 附录 A：测试数据文件

建议在 `tests/testdata/` 目录下创建以下测试 SQL 脚本文件，便于重复使用：

```
tests/testdata/
  vg_expired.sql          # 版本过期测试脚本
  vg_multiple_objs.sql     # 多对象引用脚本
  vg_nosynccheck.sql       # 含 #nosynccheck 标记脚本
  vg_checklater.sql        # 含 #checklater 标记脚本
  ai_suggestions.sql       # AI 智能建议测试脚本
  ai_comments.sql          # AI 添加注释测试脚本
  ai_format.sql            # AI 格式化测试脚本（单行格式）
  ai_performance.sql        # AI 性能分析测试脚本
  ai_empty.sql             # 空脚本（无建议场景）
```

## 附录 B：快捷键速查

| 功能 | 快捷键 | 菜单路径 |
|------|--------|----------|
| 启用版本守护（切换） | Ctrl+Shift+V | SSMSBoost++ → 版本守护 |
| 智能建议 | Ctrl+Shift+A | SSMSBoost++ → AI 代码助手 |
| 添加注释 | Ctrl+Shift+C | SSMSBoost++ → AI 代码助手 |
| 格式化代码 | Ctrl+Shift+F | SSMSBoost++ → AI 代码助手 |
| 性能分析 | Ctrl+Shift+P | SSMSBoost++ → AI 代码助手 |
| AI 配置 | Ctrl+Shift+, | SSMSBoost++ → AI 配置 |
| 检查脚本版本 | 无默认 | SSMSBoost++ → 版本守护 → 检查当前脚本版本 |
| 重新加载 | 无默认 | SSMSBoost++ → 版本守护 → 从数据库重新加载 |
| 版本守护设置 | 无默认 | SSMSBoost++ → 版本守护 → 版本守护设置 |

## 附录 C：日志关键字

测试过程中搜索以下关键字快速定位问题：

| 关键字 | 含义 |
|--------|------|
| `VG:` | 版本守护模块日志前缀 |
| `AI:` | AI 代码助手模块日志前缀 |
| `Bridge:` | AI 桥接层日志前缀 |
| `Diff:` | 差异引擎日志前缀 |
| `ERROR` | 任何错误 |
| `VSSDK` | SSMS 包加载问题（部署失败） |
| `PkgDef` | 包注册问题 |
| `ArgumentNullException` | 空参数异常（通常是 PkgDef 模板变量未替换） |
