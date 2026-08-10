# 诊断报告: VersionGuard "没有可供检查的 SQL 代码" + 连接失败

| 属性 | 值 |
|------|-----|
| 时间 | 2026-06-07 16:56-16:57 |
| 来源 | SSMS 日志 |
| 分类 | 运行时异常 |
| 严重度 | 🟡 警告（功能限制，非代码 bug） |
| 知识库 | 🆕 新错误："没有可供检查的 SQL 代码" / ✅ 已知："连接提供者失败" |

---

## 原始日志

### 错误 1（×3）: 没有可供检查的 SQL 代码

```
2026-06-07 16:56:24.612 [WRN] [NCN100019624-01] 没有可供检查的 SQL 代码。
2026-06-07 16:56:28.842 [WRN] [NCN100019624-01] 没有可供检查的 SQL 代码。
2026-06-07 16:56:31.273 [WRN] [NCN100019624-01] 没有可供检查的 SQL 代码。
```

### 错误 2（×2）: 连接提供者全部失败

```
2026-06-07 16:56:51.266 [ERR] [NCN100019624-01] [ConnectionFacade] ❌ All connection providers failed.
2026-06-07 16:57:34.836 [ERR] [NCN100019624-01] [ConnectionFacade] ❌ All connection providers failed.
```

---

## 根因分析

### 问题 1: "没有可供检查的 SQL 代码"

用户反复点击 VersionGuard（版本检查）功能，但：
- 打开的 `.sql` 文档是**空文件**（无内容）
- `SsmsEditorService.GetCodeAsync()` → `editPoint.GetText(EndPoint)` 返回空字符串
- `VersionGuardCommand` 正确地检测到空代码并输出警告

**流程图**：
```
用户点击 VersionGuard 
→ IsSqlDocument() ✅ .sql 文件
→ GetCodeAsync()  ❌ 空文件（或 DTE 无法读取）
→ 警告"没有可供检查的 SQL 代码"
→ 提前返回（不崩溃）
```

### 问题 2: 连接提供者全部失败

与上一份日志（15时）完全相同的原因：SSMS 22 无活动数据库连接，所有 5 个连接提供者依次失败。

---

## 修复建议

### 方案一（推荐）— 增强 "没有可供检查的 SQL 代码" 提示

当前用户看到 3 次相同的警告但可能不理解含义。建议改为更明确的提示：

```csharp
// 文件: src/SSMS/Commands/VersionGuardCommand.cs, 行 100-104
// 修改前:
if (string.IsNullOrWhiteSpace(code))
{
    progressDialog.CloseSafely();
    _logger.Warning("没有可供检查的 SQL 代码。");
    return;
}

// 修改后:
if (string.IsNullOrWhiteSpace(code))
{
    progressDialog.CloseSafely();
    _logger.Warning("当前 SQL 文档没有可供检查的代码（文件为空或无法读取内容）。");
    System.Windows.MessageBox.Show(
        "当前查询窗口中没有 SQL 代码。\n\n" +
        "可能原因：\n" +
        "• 查询窗口是空白的（未输入 SQL 语句）\n" +
        "• 文件尚未完全加载\n\n" +
        "请在查询窗口中输入 SQL 代码后重试。",
        "SSMSBoostPlus - 无代码",
        System.Windows.MessageBoxButton.OK,
        System.Windows.MessageBoxImage.Information);
    return;
}
```

### 方案二（备选）— 防重复提示

用户 8 秒内触发了 3 次 VersionGuard（16:56:24 → 16:56:28 → 16:56:31），每次都输出相同警告。可加防抖：

```csharp
// 在 VersionGuardCommand 类中添加
private static DateTime _lastEmptyCodeWarning = DateTime.MinValue;
private static readonly TimeSpan EmptyCodeWarningCooldown = TimeSpan.FromSeconds(5);

// 在警告前检查
if (DateTime.Now - _lastEmptyCodeWarning > EmptyCodeWarningCooldown)
{
    _logger.Warning("没有可供检查的 SQL 代码。");
    _lastEmptyCodeWarning = DateTime.Now;
}
```

---

## 预防措施

- [ ] VersionGuard 增加更明确的用户提示（弹窗说明原因）
- [ ] 考虑在 VersionGuard 按钮禁用态：当无 SQL 代码时灰显按钮
- [ ] 添加用户文档说明：VersionGuard 需要「有内容的 SQL 查询窗口 + 已连接数据库」

---

## 诊断信息

- 诊断耗时: ~30s
- 代码搜索次数: 3（VersionGuardCommand + SsmsEditorService.GetCodeAsync + IsSqlDocument）
- 知识库: 🆕 新错误归档 | ✅ 已知错误跳过