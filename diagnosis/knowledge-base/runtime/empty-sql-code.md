# 没有可供检查的 SQL 代码

## 匹配特征

- 异常类型: 无（功能性警告，非异常）
- 关键方法: `SsmsEditorService.GetCodeAsync`、`VersionGuardCommand.ExecuteAsync`
- 消息关键词: `没有可供检查的 SQL 代码`

## 常见根因

1. 查询窗口为空（未输入任何 SQL 语句）
2. 打开的 .sql 文件内容为空
3. SSMS DTE API 的 `editPoint.GetText()` 返回空（COM 兼容问题）

## 修复方案

1. 在查询窗口中输入 SQL 代码后重试
2. 确保 .sql 文件已保存且有内容
3. 关闭并重新打开查询窗口（解决偶发的 DTE COM 问题）

## 预防措施

- [ ] VersionGuard 增加弹窗提示：「当前查询窗口没有 SQL 代码，请输入后重试」
- [ ] 考虑在无 SQL 代码时禁用 VersionGuard 按钮
- [ ] 添加防抖机制避免短时间内重复触发相同警告

## 关联源码

- `src/SSMS/Commands/VersionGuardCommand.cs:100-104` — 空代码检测和警告输出
- `src/SSMS/Services/SsmsEditorService.cs:34-51` — GetCodeAsync() 方法
- `src/SSMS/Services/SsmsEditorService.cs:89-101` — IsSqlDocument() 方法

## 历史案例

- 2026-06-07: 用户打开空 .sql 文件后反复点击 VersionGuard，3 次输出相同警告。代码处理正确，是功能性限制而非 bug。