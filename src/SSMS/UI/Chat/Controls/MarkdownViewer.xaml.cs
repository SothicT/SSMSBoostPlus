using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Markdig;
using Microsoft.Web.WebView2.Core;
using SSMSBoostPlus.SSMS.Services;

namespace SSMSBoostPlus.SSMS.UI.Chat.Controls;

/// <summary>
/// 自定义 Markdown 渲染控件。
/// 使用 Markdig 将 Markdown 文本转换为 HTML，
/// 通过 WebView2 + highlight.js 展示渲染后的内容。
/// 支持围栏代码块、表格、列表等完整 GFM 语法，带 GitHub 风格语法高亮。
/// </summary>
public partial class MarkdownViewer : UserControl
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownViewer),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Markdig 管线：完整 GFM 扩展</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseAutoLinks()
        .UseGenericAttributes()
        .Build();

    // ── 嵌入式 highlight.js 资源缓存 ──────────────────────────
    private static readonly Lazy<string> CachedHighlightJs = new(()
        => LoadEmbeddedResource("highlight.min.js"));

    private static readonly Lazy<string> CachedGithubCss = new(()
        => LoadEmbeddedResource("github.min.css"));

    private static readonly Lazy<string> CachedGithubDarkCss = new(()
        => LoadEmbeddedResource("github-dark.min.css"));

    /// <summary>暂存的 HTML 内容（WebView2 尚未就绪时使用）</summary>
    private string? _pendingHtml;

    /// <summary>WebView2 是否已初始化</summary>
    private bool _webViewInitialized;

    /// <summary>是否正在初始化 WebView2</summary>
    private bool _initializing;

    public MarkdownViewer()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 从嵌入资源中加载文本内容。
    /// </summary>
    private static string LoadEmbeddedResource(string fileName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"SSMSBoostPlus.Resources.highlight.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            viewer.RenderMarkdown(e.NewValue as string);
        }
    }

    /// <summary>
    /// 将 Markdown 文本渲染为 HTML 并通过 WebView2 展示。
    /// WebView2 需要异步初始化，如果尚未就绪则暂存 HTML。
    /// </summary>
    private void RenderMarkdown(string? markdownText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(markdownText))
            {
                NavigateOrDefer(BuildEmptyHtml());
                EmptyPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }

            // 🔧 Markdown 预处理：修复 SSE 流式拼接导致的格式丢失
            var normalized = NormalizeMarkdown(markdownText);

            // Markdig 转换：Markdown → HTML
            var html = Markdig.Markdown.ToHtml(normalized, Pipeline);

            // 构建完整 HTML 文档并加载到 WebView2
            var fullHtml = BuildHtmlDocument(html);
            NavigateOrDefer(fullHtml);
            EmptyPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // 渲染失败时降级：显示纯文本（HTML 编码后）
            System.Diagnostics.Debug.WriteLine($"[MarkdownViewer] 渲染异常: {ex.Message}");
            var escapedText = System.Security.SecurityElement.Escape(markdownText ?? string.Empty);
            NavigateOrDefer(BuildHtmlDocument(
                $"<pre style=\"white-space:pre-wrap;word-break:break-word;background:#F1F5F9;padding:10px;border-radius:4px;font-family:Consolas;font-size:12px;color:#1E293B;\">{escapedText}</pre>"));
            EmptyPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 预处理 Markdown 文本，修复 SSE 流式拼接导致的格式丢失：
    /// 1. 标题标记 # 后缺少空格 → 补上空格
    /// 2. 代码块标记 ``` 后缺少换行 → 补上换行
    /// </summary>
    private static string NormalizeMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        // 1. 修复标题：##text → ## text（支持 1-6 级标题）
        markdown = System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"^(#{1,6})(?!\s)(\S)",
            "$1 $2",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // 2. 修复代码块：```sqlText → ```sql\nText
        markdown = System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"(```\w*)(?!\n)(.)",
            "$1\n$2");

        return markdown;
    }

    /// <summary>
    /// 智能导航：如果 WebView2 已初始化则立即执行 NavigateToString，
    /// 否则暂存 HTML 并等待初始化完成。
    /// </summary>
    private void NavigateOrDefer(string fullHtml)
    {
        if (_webViewInitialized)
        {
            MarkdownWebView.CoreWebView2.NavigateToString(fullHtml);
            _pendingHtml = null;
        }
        else
        {
            _pendingHtml = fullHtml;
            if (!_initializing)
            {
                InitWebView2();
            }
        }
    }

    /// <summary>
    /// 异步初始化 WebView2，完成后渲染暂存的 HTML。
    /// 使用文件系统兜底查找 WebView2 Runtime 路径（当 EdgeUpdate 注册表缺失时）。
    /// </summary>
    private async void InitWebView2()
    {
        if (_initializing) return;
        _initializing = true;

        try
        {
            // 尝试查找 WebView2 Runtime 路径（当 EdgeUpdate 注册表缺失时的兜底方案）
            var runtimePath = WebView2RuntimeHelper.TryFindWebView2RuntimePath();
            if (!string.IsNullOrEmpty(runtimePath))
            {
                var env = await CoreWebView2Environment.CreateAsync(runtimePath);
                await MarkdownWebView.EnsureCoreWebView2Async(env);
            }
            else
            {
                await MarkdownWebView.EnsureCoreWebView2Async();
            }

            _webViewInitialized = true;

            // 渲染暂存的 HTML
            if (_pendingHtml != null)
            {
                MarkdownWebView.CoreWebView2.NavigateToString(_pendingHtml);
                _pendingHtml = null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarkdownViewer] WebView2 初始化失败: {ex.Message}");
            _initializing = false;
        }
    }

    /// <summary>
    /// 构建空内容的 HTML 骨架。
    /// </summary>
    private static string BuildEmptyHtml()
    {
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""></head>
<body style=""margin:0;padding:0;background:transparent;""></body></html>";
    }

    /// <summary>
    /// 构建完整 HTML 文档 —— GitHub 风格 CSS + highlight.js 语法高亮。
    /// </summary>
    private static string BuildHtmlDocument(string bodyContent)
    {
        var hljs = CachedHighlightJs.Value;
        var ghCss = CachedGithubCss.Value;
        var ghDarkCss = CachedGithubDarkCss.Value;

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<style>
    /* ── 基础样式（GitHub 风格） ──────────────────────── */
    body {{
        font-family: -apple-system, 'Segoe UI', 'Noto Sans', 'Microsoft YaHei', sans-serif;
        font-size: 14px;
        line-height: 1.6;
        color: #1F2328;
        padding: 0;
        margin: 0;
        word-wrap: break-word;
        overflow-wrap: break-word;
        max-width: 100%;
        overflow-x: hidden;
        box-sizing: border-box;
    }}
    *, *::before, *::after {{ box-sizing: border-box; max-width: 100%; }}

    /* ── 代码块 ──────────────────────────────────── */
    pre {{
        background: #F6F8FA;
        border: 1px solid #D0D7DE;
        border-radius: 6px;
        padding: 12px;
        overflow-x: auto;
        font-family: 'Consolas', 'Courier New', ui-monospace, monospace;
        font-size: 12px;
        line-height: 1.5;
        white-space: pre;
        margin: 8px 0;
        max-width: 100%;
        box-sizing: border-box;
    }}
    pre code.hljs {{
        display: block;
        overflow-x: auto;
        padding: 0;
        background: transparent;
        border: none;
    }}
    code {{
        background: #F6F8FA;
        padding: 2px 5px;
        border-radius: 4px;
        font-family: 'Consolas', 'Courier New', ui-monospace, monospace;
        font-size: 12px;
        color: #1F2328;
    }}
    pre code {{
        background: none;
        padding: 0;
        white-space: pre;
    }}

    /* ── 表格 ──────────────────────────────────── */
    table {{
        border-collapse: collapse;
        width: 100%;
        margin: 8px 0;
        display: block;
        overflow-x: auto;
    }}
    th, td {{
        border: 1px solid #D0D7DE;
        padding: 6px 10px;
        text-align: left;
        font-size: 13px;
    }}
    th {{
        background: #F6F8FA;
        font-weight: 600;
    }}

    /* ── 标题 ──────────────────────────────────── */
    h1, h2, h3, h4, h5, h6 {{
        margin-top: 16px;
        margin-bottom: 8px;
        font-weight: 600;
        line-height: 1.25;
    }}
    h1 {{ font-size: 20px; border-bottom: 1px solid #D0D7DE; padding-bottom: 6px; }}
    h2 {{ font-size: 17px; border-bottom: 1px solid #D0D7DE; padding-bottom: 4px; }}
    h3 {{ font-size: 15px; }}
    h4 {{ font-size: 14px; }}

    /* ── 引用 ──────────────────────────────────── */
    blockquote {{
        border-left: 4px solid #D0D7DE;
        margin: 8px 0;
        padding: 4px 14px;
        background: #F6F8FA;
        color: #57606A;
    }}

    /* ── 列表 ──────────────────────────────────── */
    ul, ol {{ padding-left: 24px; margin: 6px 0; }}
    li {{ margin: 3px 0; }}
    ul ul, ol ul {{ margin: 0; }}

    /* ── 分割线 ──────────────────────────────────── */
    hr {{ border: none; border-top: 1px solid #D0D7DE; margin: 16px 0; }}

    /* ── 其他 ──────────────────────────────────── */
    strong {{ font-weight: 600; }}
    a {{ color: #0969DA; text-decoration: none; }}
    a:hover {{ text-decoration: underline; }}
    p {{ margin: 6px 0; }}

    /* ── 暗色主题（自动适配系统主题） ──────────────── */
    @media (prefers-color-scheme: dark) {{
        body {{ background: #0D1117; color: #C9D1D9; }}
        pre {{ background: #161B22; border-color: #30363D; }}
        code {{ background: #161B22; color: #C9D1D9; }}
        th {{ background: #161B22; }}
        th, td {{ border-color: #30363D; }}
        h1, h2 {{ border-bottom-color: #21262D; }}
        blockquote {{ border-left-color: #30363D; background: #161B22; color: #8B949E; }}
        hr {{ border-top-color: #21262D; }}
        a {{ color: #58A6FF; }}
    }}
</style>
{(string.IsNullOrEmpty(ghCss) ? "" : $"<style>{ghCss}</style>")}
{(string.IsNullOrEmpty(ghDarkCss) ? "" : $@"<style>
@media (prefers-color-scheme: dark) {{
{ghDarkCss}
}}
</style>")}
{(string.IsNullOrEmpty(hljs) ? "" : $"<script>{hljs}</script>")}
</head>
<body>
{bodyContent}
{(string.IsNullOrEmpty(hljs) ? "" : "<script>hljs.highlightAll();</script>")}
</body>
</html>";
    }
}
