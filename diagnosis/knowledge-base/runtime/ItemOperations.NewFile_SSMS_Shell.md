# ItemOperations.NewFile 在 SSMS 隔离外壳抛 0x80004005

## 匹配特征

- 异常类型: `System.Runtime.InteropServices.COMException (0x80004005)`
- 关键方法: `EnvDTE.ItemOperations.NewFile`
- 消息关键词: `模板"SQL Files\SQL Query"对选定的项目无效`

## 常见根因

1. SSMS 是 VS 隔离外壳（Isolated Shell），没有项目/项模板系统；`ItemOperations.NewFile` 本质是"向当前选中项目添加新项"，找不到合法宿主项目/模板即抛此异常。

## 修复方案

1. （推荐）改用 SSMS 原生命令 `dte.ExecuteCommand("Query.NewQuery")` 新建查询编辑器窗口，再由 `dte.ActiveDocument` 取得 `TextDocument` 写入内容。该命令族（`Query.Execute` 等）在本仓库 SSMS 环境下已验证可用。
2. 不要在模态 `ShowDialog` 对话框内部直接调用 `dte.ExecuteCommand`；把 DTE 操作放到 `ShowDialog()` 返回后的 `Dispatcher.BeginInvoke(..., DispatcherPriority.Background)` 上下文中执行，避免嵌套 WPF 消息泵导致命令路由阻塞/死锁。

## 预防措施

- SSMS 隔离外壳中禁止使用 `ItemOperations.NewFile` 等依赖项目系统的 API 新建文档；统一用 `Query.NewQuery`。
- 对 `dte`、`ActiveDocument`、`TextDocument` 做 null 检查并记录日志，避免异常被静默吞掉导致"无响应"。

## 关联源码

- `src/SSMS/UI/VersionHistory/LocalHistoryDiffDialog.xaml.cs`（原 `OpenInEditorButton_Click` 中的 `NewFile` 调用）
- `src/SSMS/UI/VersionHistory/LocalHistoryToolWindowControl.xaml.cs`（`ShowDialog` 返回后用 `Query.NewQuery` 打开并写入）

## 历史案例

- 2026-07-20: 版本差异对比页"在编辑器中打开"按钮点击后主编辑器未打开新窗口，日志复现 `COMException 0x80004005`，按方案一修复并验证（build 通过、136 测试通过）。
