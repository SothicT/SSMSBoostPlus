# 窗口标题正则解析 — 服务器名含括号导致数据库名丢失

## 匹配特征

- 异常类型: 无异常（逻辑 Bug，非运行时异常）
- 关键方法: `AxialStyleConnectionProvider.TryGetFromWindowCaption`
- 消息关键词: `Parsed server only`、`Database=master`（窗口标题中明确包含其他数据库名时）

## 常见根因

SSMS 窗口标题格式 `SQLQuery1.sql - ServerName(IP/Port).DatabaseName (UserName (SPID))` 中，服务器名部分可能包含括号（如 `NC-Rainbow-MBD3MD2-P(10.32.136.89/91)`）。`TryGetFromWindowCaption()` 中的正则字符类 `[\w\.\\\-]+` 不包含 `(` 和 `)`，导致正则无法完整匹配服务器名，逐级降级到"仅匹配服务器名"兜底分支，数据库名被硬编码为 `"master"`。

## 修复方向

将 4 处正则的服务器名捕获组字符类从 `[\w\.\\\-]+` 扩展为 `[\w\.\\\-\(\)]+`。

## 关联问题

- 2026-07-13-010-窗口切换跟随与反馈（上游触发，本次是该问题的下游影响）

## 诊断记录

- 首次发现: 2026-07-13
- 触发条件: 服务器名含括号 + 窗口切换
- 影响范围: 所有使用 `AxialStyleConnectionProvider` 策略A 解析窗口标题的场景