# 知识库索引

> 自动维护，记录 `diagnosis/knowledge-base/` 下所有条目。新增条目时同步更新此文件。

## Runtime 运行时异常

| 条目 | 异常类型 | 关键方法 | 消息关键词 |
|------|----------|----------|------------|
| [null-reference](diagnosis/knowledge-base/runtime/null-reference.md) | `NullReferenceException` | 任意 | `Object reference not set` |
| [empty-sql-code](diagnosis/knowledge-base/runtime/empty-sql-code.md) | 功能性警告 | `GetCodeAsync` | `没有可供检查的 SQL 代码` |
| [webview2-runtime-not-found](diagnosis/knowledge-base/runtime/webview2-runtime-not-found.md) | `WebView2RuntimeNotFoundException` | `WebView2RuntimeHelper.TryFindWebView2RuntimePath` | `Couldn't find a compatible Webview2 Runtime installation` |
| [toolwindow-null-content](diagnosis/knowledge-base/runtime/toolwindow-null-content.md) | 功能性警告 | `AIChatToolWindow` | `窗口已被关闭，销毁旧实例并创建全新会话` |
| [regex-parentheses-server-name](diagnosis/knowledge-base/runtime/regex-parentheses-server-name.md) | 逻辑 Bug（无异常） | `TryGetFromWindowCaption` | `Parsed server only`、`Database=master`（标题含其他库名时） |
| (待添加) | | | |

## Build 编译错误

| 条目 | 错误码 | 典型触发 | 消息关键词 |
|------|--------|----------|------------|
| [cs0246-missing-using](diagnosis/knowledge-base/build/cs0246-missing-using.md) | CS0246 | 缺少 using 或引用 | `could not be found` |
| (待添加) | | | |

## Test 测试失败

| 条目 | 失败模式 | 典型触发 | 消息关键词 |
|------|----------|----------|------------|
| [assertion-failure](diagnosis/knowledge-base/test/assertion-failure.md) | `Assert.Equal()` / `Assert.True()` | 断言值不匹配 | `Assert.*() Failure` |
| (待添加) | | | |