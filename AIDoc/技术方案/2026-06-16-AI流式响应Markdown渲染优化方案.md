---
title: AI流式响应Markdown渲染优化方案
date: 2026-06-16
author: AI Assistant
tags: [Markdown, highlight.js, 流式渲染, 代码块缓冲, GFM]
---

# AI流式响应 Markdown 渲染优化方案

## 一、背景与问题

### 1.1 当前问题

AI 流式结果弹窗（`AIStreamResultDialog`）的响应区使用 WebBrowser + Markdig 渲染 Markdown，存在以下问题：

| # | 问题 | 根因 | 影响 |
|---|------|------|------|
| 1 | ` ```sql ` 标记原样显示 | `StreamingPipeline` 缺少围栏代码块扩展 | 代码块无样式，阅读极差 |
| 2 | 代码块无语法高亮 | HTML 模板仅有基础 CSS，无语法着色 | 一片灰底，无法区分关键字 |
| 3 | 列表/表格格式错乱 | `StreamingPipeline` 缺少表格/列表扩展 | 结构化信息无法识别 |
| 4 | 内容挤在一行 | 流式 chunk 截断代码块标记，Markdig 无法解析不完整内容 | 排版混乱 |
| 5 | 不支持 GitHub 风格 | 缺乏 GFM 完整扩展 + 现代化样式 | 与主流 Markdown 体验脱节 |

### 1.2 涉及范围

| 文件 | 角色 |
|------|------|
| `src/SSMS/UI/Dialogs/AIStreamResultDialog.xaml.cs` | 流式弹窗主逻辑（核心修改） |
| `src/SSMS/UI/Chat/Controls/MarkdownViewer.xaml.cs` | 聊天面板 Markdown 渲染（同步修改） |

> **注意**：`MarkdownViewer`（聊天面板）已在 2026-06-15 从 `Markdown.Xaml` 迁移为 `Markdig + WebBrowser`，本次也将对其进行升级，保持一致。

---

## 二、技术方案

### 2.1 总体架构

```
┌─────────────────────────────────────────────────────────┐
│                      AIStreamResultDialog                │
│  ┌─────────────────────────────────────────────────────┐│
│  │  MarkdownBuffer（新增）                               ││
│  │  • 段落级缓冲队列                                     ││
│  │  • 代码块状态跟踪（open/close）                        ││
│  │  • 只推送完整段落/闭合代码块到渲染层                     ││
│  └─────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────┐│
│  │  Markdig Pipeline（升级）                             ││
│  │  • 完整 GFM 扩展（UseAdvancedExtensions）              ││
│  │  • 统一管线（不分流式/最终）                            ││
│  └─────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────┐│
│  │  HTML + highlight.js（新增）                          ││
│  │  • 嵌入式 highlight.js（SQL 语法高亮）                 ││
│  │  • GitHub 风格 CSS                                   ││
│  │  • 暗色主题支持                                      ││
│  └─────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────┘
```

### 2.2 代码块缓冲队列（MarkdownBuffer）

**核心思路**：流式 chunk 可能截断 Markdown 语法标记，需按段落粒度缓冲，只推送完整内容到渲染层。

```
chunk1: "优化建议：\n```sql\nSELECT * FROM "
chunk2: "Users WHERE Status = 'Active'\n```\n以上是优化后的代码"
                              ↓
MarkdownBuffer 工作流程：
  1. 收到 chunk1 → 检测到 ```sql 开始 → 标记 inCodeBlock=true
  2. 收到 chunk2 → 检测到 ``` 结束 → 标记 inCodeBlock=false
  3. 将完整代码块 "```sql\nSELECT * FROM Users...\n```" 推送到渲染层
  4. 渲染层调用 Markdig → HTML → highlight.js 着色
```

**关键设计**：

```csharp
/// <summary>
/// Markdown 流式缓冲器。
/// 负责缓冲流式 chunk 中的不完整段落（尤其是代码块），
/// 只在获得完整内容后推送到渲染管线。
/// </summary>
internal class MarkdownBuffer
{
    private readonly StringBuilder _buffer = new();
    private bool _inFencedCodeBlock;
    private int _backtickCount;

    /// <summary>
    /// 追加一个 chunk，返回需要渲染的完整段落列表。
    /// 不完整的代码块会暂留在缓冲中，等待后续 chunk 补全。
    /// </summary>
    public IReadOnlyList<string> Append(string chunk) { ... }

    /// <summary>
    /// 流式结束时调用，强制 flush 剩余缓冲内容。
    /// </summary>
    public string Flush() { ... }
}
```

### 2.3 Markdig 管线升级

将现有的双管线（`StreamingPipeline` + `FinalPipeline`）统一为**单一全功能管线**：

```csharp
/// <summary>统一 Markdig 管线：完整 GFM 扩展</summary>
private static readonly MarkdownPipeline GfmPipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()      // 围栏代码块、表格、列表、删除线、自动链接等
    .UseEmojiAndSmiley()          // Emoji 支持
    .UseAutoLinks()               // URL 自动链接
    .UseGenericAttributes()       // 通用属性（为代码块添加 language-* class）
    .Build();
```

**管线支持语法清单**：

| 语法 | 当前（StreamingPipeline） | 升级后（GfmPipeline） |
|------|--------------------------|----------------------|
| 围栏代码块 ` ```sql ` | ❌ | ✅ |
| 表格 `\| col1 \| col2 \|` | ❌ | ✅ |
| 有序/无序列表 | ❌ | ✅ |
| 删除线 `~~text~~` | ❌ | ✅ |
| 任务列表 `- [x]` | ❌ | ✅ |
| 脚注 | ❌ | ✅ |
| 围栏区块 `:::info` | ❌ | ✅ |
| 加粗/斜体 | ✅ | ✅ |
| 自动链接 | ✅ | ✅ |
| Emoji `:smile:` | ❌ | ✅ |

### 2.4 语法高亮（highlight.js）

#### 2.4.1 方案选择

| 方案 | 优点 | 缺点 | 选择 |
|------|------|------|------|
| CDN 加载 | 代码体积小 | 需要网络，SSMS 可能离线 | ❌ |
| 嵌入完整 highlight.js | 离线可用 | 约 300KB | ❌ |
| **嵌入精简版（仅 SQL）** | **离线可用，约 25KB** | **需构建精简包** | **✅ 推荐** |

#### 2.4.2 集成方式

将 highlight.js（仅 SQL 语言支持 + GitHub 主题 CSS）作为**嵌入资源**（Embedded Resource）添加到 SSMS 项目中：

```
src/SSMS/
├── Resources/
│   ├── highlight/
│   │   ├── highlight.min.js      # 精简版（仅 SQL）
│   │   ├── github.min.css         # GitHub 风格主题
│   │   └── github-dark.min.css    # 暗色主题（可选）
```

在 `BuildHtmlDocument()` 中内联加载：

```csharp
private static string BuildHtmlDocument(string bodyContent)
{
    var highlightJs = LoadEmbeddedResource("highlight.min.js");
    var highlightCss = LoadEmbeddedResource("github.min.css");

    return $@"<!DOCTYPE html>
<html>
<head>
<style>{highlightCss}</style>
<script>{highlightJs}</script>
</head>
<body>
{bodyContent}
<script>hljs.highlightAll();</script>
</body>
</html>";
}
```

#### 2.4.3 流式渲染时的高亮触发

```csharp
private void RenderStreamingContent(string html)
{
    UpdateDomById("content", html);
    // 调用 highlight.js 对新插入的代码块进行着色
    ExecuteJavaScript("hljs.highlightAll();");
}
```

> `ExecuteJavaScript()` 通过 WebBrowser 的 `Document.InvokeScript()` 实现。

### 2.5 GitHub 风格 CSS

完全重写 HTML 模板中的 CSS 样式，采用 GitHub Markdown 风格：

| 元素 | GitHub 风格 | 当前风格 |
|------|-------------|----------|
| 代码块背景 | `#F6F8FA` | `#F1F5F9` |
| 代码块边框 | `1px solid #D0D7DE` | `1px solid #E2E8F0` |
| 代码块圆角 | `6px` | `4px` |
| 行高 | `1.6` | `1.6` |
| 标题字体 | `600` + `-apple-system` | `600` + `-apple-system` |
| 引用边框色 | `#D0D7DE`（灰蓝） | `#1A73E8`（蓝） |
| 引用背景 | `#F6F8FA` | `#F0F7FF` |
| 表格边框 | `1px solid #D0D7DE` | `1px solid #E2E8F0` |
| 表格头部 | `#F6F8FA` | `#F8FAFC` |

### 2.6 暗色主题支持（可选）

通过 CSS `@media (prefers-color-scheme: dark)` 自动适配：

```css
@media (prefers-color-scheme: dark) {
    body { background: #0D1117; color: #C9D1D9; }
    pre { background: #161B22; border-color: #30363D; }
    code { background: #161B22; }
    /* ... */
}
```

---

## 三、涉及文件与修改清单

### 3.1 核心修改（2 个文件）

| 文件 | 修改内容 | 改动量 |
|------|----------|--------|
| `AIStreamResultDialog.xaml.cs` | 1) 新增 `MarkdownBuffer` 类<br>2) 升级管线为 GfmPipeline<br>3) HTML 模板增加 highlight.js + GitHub 风格 CSS<br>4) 流式渲染逻辑改为缓冲队列驱动<br>5) 最终渲染调用 highlight.js | ~150 行 |
| `AIStreamResultDialog.xaml` | HTML 模板中的 CSS 更新（仅影响 BuildHtmlDocument） | ~50 行 |

### 3.2 同步修改（1 个文件）

| 文件 | 修改内容 | 改动量 |
|------|----------|--------|
| `MarkdownViewer.xaml.cs` | 1) 同步升级管线为 GfmPipeline<br>2) HTML 模板增加 GitHub 风格 CSS + highlight.js<br>3) 保持一致体验 | ~30 行 |

### 3.3 新增资源（3 个文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/SSMS/Resources/highlight/github.min.css` | 嵌入资源 | GitHub 风格代码高亮主题 |
| `src/SSMS/Resources/highlight/github-dark.min.css` | 嵌入资源 | 暗色主题代码高亮 |
| `src/SSMS/Resources/highlight/highlight.min.js` | 嵌入资源 | 精简版 highlight.js（SQL 语言） |

### 3.4 不涉及修改

| 文件 | 原因 |
|------|------|
| `.csproj` | 不需要新增 NuGet 包 |
| `AIStreamChunk` 相关 | 不改变数据模型 |
| `CodebuddyCliBridge` / `AIApiClientBridge` | 不改变桥接层 |
| 测试项目 | 纯 UI 渲染改动，无需新增单元测试 |

---

## 四、流式渲染工作流（完整流程）

```
流式 chunk 到达
      │
      ▼
OnChunkReceived(chunk)
      │
      ▼
MarkdownBuffer.Append(chunk)
      │
      ├── 如果 chunk 在代码块内部 → 缓冲，暂不渲染
      │
      ├── 如果 chunk 闭合了代码块 → 输出闭合后的完整段落
      │
      └── 如果 chunk 是普通文本 → 按段落切分，输出完整段落
              │
              ▼
      100ms 渲染定时器（DispatcherTimer）
              │
              ▼
      调用 Markdig.ToHtml(paragraph, GfmPipeline)
              │
              ▼
      UpdateDomById("content", html)
              │
              ▼
      ExecuteJavaScript("hljs.highlightAll()")
              │
              ▼
      滚动到底部
              │
              ▼
      （等待下一个 chunk...）
              │
              ▼
      流式完成 → MarkdownBuffer.Flush() → 最终渲染
```

---

## 五、边缘情况处理

| 场景 | 处理方式 |
|------|----------|
| **代码块未闭合就结束** | `Flush()` 时自动追加 `\n```\n` 强制闭合 |
| **嵌套代码块**（罕见） | 不处理嵌套，按最外层 ``` 匹配 |
| **空响应** | 显示「AI 未返回有效内容」占位 |
| **超长代码块（>5000 字符）** | 拆分渲染，每 2000 字符输出一次中间结果 |
| **highlight.js 加载失败** | 降级为无高亮的 pre/code CSS 样式 |
| **WebBrowser DOM 操作异常** | catch 后降级为纯文本显示（当前已有机制） |
| **非 SQL 语言标记**（如 ` ```json `） | highlight.js 自动检测语言，不支持时按纯文本显示 |
| **流式过程中用户关闭弹窗** | `CancelButton` → 取消 `_cts` 并关闭弹窗 |

---

## 六、验证标准

| 检查项 | 预期结果 |
|--------|----------|
| ` ```sql ... ``` ` 代码块 | 显示为灰色背景代码块，SQL 关键字高亮着色 |
| ` ``` ` （纯代码块，无语言标记） | 显示为代码块，无高亮 |
| 表格 `\| A \| B \|` | 正确渲染为表格样式 |
| 无序/有序列表 | 正确显示列表符号/编号 |
| Emoji `:smile:` | 渲染为 😄 |
| 流式期间代码块显示 | 不会出现截断的 ` ``` ` 标记 |
| 流式期间普通文本 | 随到随显，无延迟 |
| 最终完成渲染 | 调用 highlight.js 完整着色 |
| 暗色主题 | 系统为暗色时自动切换 |
| 复制响应 | 复制原始 Markdown 文本（含 ` ``` ` 标记），不影响功能 |