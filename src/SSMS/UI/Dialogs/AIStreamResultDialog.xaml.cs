using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SSMSBoostPlus.SSMS.Services;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// AI 响应结果弹窗（第十五版 — WebView2 + 两步 REST JSON API）。
///
/// v15 关键变化：
/// - 从 SSE 流式改为两步 REST JSON 调用
/// - Step 1: GET /chat/api/open → 获取 chat_id
/// - Step 2: POST /chat/api/chat_message/{chat_id} → 获取 data.content
/// - JS 引擎直接完成两步调用，C# 只需传 baseUrl/authToken/promptMessage
/// </summary>
public partial class AIStreamResultDialog : Window
{
    private static readonly Lazy<string> CachedStreamEngine = new(LoadStreamEngine);
    private static readonly Lazy<string> CachedMarkedLib = new(LoadMarkedLib);

    private static string LoadStreamEngine()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("SSMSBoostPlus.Resources.stream-engine.js");
        if (stream == null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LoadMarkedLib()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("SSMSBoostPlus.Resources.marked.min.js");
        if (stream == null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly string _baseUrl;
    private readonly string _authToken;
    private readonly string _promptMessage;
    private readonly StringBuilder _collectedResponse = new();
    private readonly object _syncLock = new();
    private Stopwatch _stopwatch = new();
    private DispatcherTimer? _statusTimer;
    private string? _extractedCode;
    private readonly string _requestContent;
    private readonly ILoggerService? _logger;
    private bool _webViewInitialized;

    public string? CollectedResponse
    {
        get { lock (_syncLock) { return _collectedResponse.Length > 0 ? _collectedResponse.ToString() : null; } }
    }

    public bool IsStreamCompleted { get; private set; }
    public bool HasError { get; private set; }

    public AIStreamResultDialog(string title, string requestContent,
        string baseUrl, string authToken, string promptMessage,
        ILoggerService? logger = null)
    {
        InitializeComponent();

        Title = $"SSMSBoost++ - {title}";
        TitleText.Text = title;
        _baseUrl = baseUrl;
        _authToken = authToken;
        _promptMessage = promptMessage;
        _requestContent = requestContent;
        _logger = logger;
        RequestSizeText.Text = $"{requestContent.Length} 字符";
        Loaded += OnLoaded;
    }



    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // WebView2 异步初始化
            // 禁用 Web 安全策略（等同于 IE11 的宽松跨域模式）
            // 原因：NavigateToString 的页面 origin 为 null，fetch 跨域请求会被 Chromium CORS 拦截
            // 这是内部 SSMS 扩展工具，不是公网浏览器，禁用合理
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-web-security"
            };

            // 尝试查找 WebView2 Runtime 路径（当 EdgeUpdate 注册表缺失时的兜底方案）
            var runtimePath = WebView2RuntimeHelper.TryFindWebView2RuntimePath(_logger);
            _logger?.Information($"WebView2 Runtime 路径: {runtimePath ?? "(SDK 默认查找)"}");
            var env = await CoreWebView2Environment.CreateAsync(runtimePath, null, options);

            // 初始化请求区 WebView2（用 marked 渲染 Markdown）
            await RequestWebView.EnsureCoreWebView2Async(env);
            RequestWebView.CoreWebView2.NavigateToString(BuildRequestHtml());

            // 初始化响应区 WebView2
            await ResponseWebView.EnsureCoreWebView2Async(env);

            // 注册 JS→C# 消息通道
            ResponseWebView.CoreWebView2.WebMessageReceived += OnWebMessage;

            _webViewInitialized = true;
            InitResponseBrowser();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "初始化 WebView2 失败");
            StatusText.Text = $"初始化 WebView2 失败";
            ProgressBar.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
            HasError = true;
        }
    }

    private void InitResponseBrowser()
    {
        try
        {
            var html = BuildResponseHtml();
            ResponseWebView.CoreWebView2.NavigateToString(html);

            _stopwatch.Start();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _statusTimer.Tick += OnStatusTimerTick;
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "初始化响应浏览器失败");
            StatusText.Text = $"初始化失败";
            ProgressBar.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
            HasError = true;
        }
    }

    private string BuildResponseHtml()
    {
        var engineJs = CachedStreamEngine.Value;
        var baseUrl = _baseUrl;
        var authToken = _authToken;
        var promptMessage = _promptMessage;

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<style>
{SharedCss}
</style>
<script>var BASE_URL={Newtonsoft.Json.JsonConvert.SerializeObject(baseUrl)};var AUTH_TOKEN={Newtonsoft.Json.JsonConvert.SerializeObject(authToken)};var PROMPT_MESSAGE={Newtonsoft.Json.JsonConvert.SerializeObject(promptMessage)};</script>
{(string.IsNullOrEmpty(CachedMarkedLib.Value) ? "" : $"<script>{CachedMarkedLib.Value}</script>")}
{(string.IsNullOrEmpty(engineJs) ? "" : $"<script>{engineJs}</script>")}
</head>
<body>
<div id=""thinking-area"">
<div id=""thinking-header"" onclick=""document.getElementById('thinking-area').classList.toggle('collapsed')"">💭 AI 思考过程</div>
<div id=""thinking-body""></div>
</div>
<div id=""content""></div>
</body>
</html>";
    }

    private string BuildRequestHtml()
    {
        var markedLib = CachedMarkedLib.Value;
        // 转义请求内容中的特殊字符，用于 JSON 注入
        var requestJson = Newtonsoft.Json.JsonConvert.SerializeObject(_requestContent);

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<style>
{SharedCss}
body{{background:#F8F9FA;}}
</style>
{(string.IsNullOrEmpty(markedLib) ? "" : $"<script>{markedLib}</script>")}
</head>
<body>
<div id=""content""></div>
<script>
(function() {{
    var text = {requestJson};
    if (typeof marked === 'undefined') {{
        document.getElementById('content').innerHTML = text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\n/g,'<br>');
    }} else {{
        marked.setOptions({{ gfm: true, breaks: true, silent: true }});
        // 流式渲染时未闭合代码块补全
        var renderText = text;
        var fenceCount = (renderText.match(/```/g) || []).length;
        if (fenceCount % 2 !== 0) renderText += '\n```';
        document.getElementById('content').innerHTML = marked.parse(renderText);
    }}
}})();
</script>
</body>
</html>";
    }

    private const string SharedCss = @"
body{font-family:-apple-system,Segoe UI,Noto Sans,Microsoft YaHei,sans-serif;font-size:14px;line-height:1.6;color:#1F2328;padding:12px 16px;margin:0;word-wrap:break-word;overflow-wrap:break-word;}
#content{word-wrap:break-word;overflow-wrap:break-word;}
#thinking-area{margin-bottom:12px;border:1px solid #E2E8F0;border-radius:6px;overflow:hidden;display:none;}
#thinking-header{background:#F8FAFC;padding:8px 12px;font-size:12px;font-weight:600;color:#64748B;cursor:pointer;user-select:none;border-bottom:1px solid #E2E8F0;}
#thinking-header:hover{background:#F1F5F9;}
#thinking-body{padding:8px 12px;font-size:12px;color:#94A3B8;font-family:Consolas,monospace;line-height:1.5;max-height:200px;overflow-y:auto;background:#FAFBFC;}
#thinking-area.collapsed #thinking-body{display:none;}
pre{background:#F6F8FA;border:1px solid #D0D7DE;border-radius:6px;padding:12px;overflow-x:auto;font-family:Consolas,Courier New,monospace;font-size:12px;line-height:1.5;white-space:pre;margin:8px 0;}
code{background:#F6F8FA;padding:2px 5px;border-radius:4px;font-family:Consolas,Courier New,monospace;font-size:12px;color:#1F2328;}
pre code{background:none;padding:0;white-space:pre;}
table{border-collapse:collapse;width:100%;margin:8px 0;display:block;overflow-x:auto;}
th,td{border:1px solid #D0D7DE;padding:6px 10px;text-align:left;font-size:13px;}
th{background:#F6F8FA;font-weight:600;}
h1,h2,h3,h4,h5,h6{margin-top:16px;margin-bottom:8px;font-weight:600;line-height:1.25;}
h1{font-size:20px;border-bottom:1px solid #D0D7DE;padding-bottom:6px;}
h2{font-size:17px;border-bottom:1px solid #D0D7DE;padding-bottom:4px;}
h3{font-size:15px;}h4{font-size:14px;}
blockquote{border-left:4px solid #D0D7DE;margin:8px 0;padding:4px 14px;background:#F6F8FA;color:#57606A;}
ul,ol{padding-left:24px;margin:6px 0;}
li{margin:3px 0;}
hr{border:none;border-top:1px solid #D0D7DE;margin:16px 0;}
strong{font-weight:600;}
a{color:#0969DA;text-decoration:none;}
a:hover{text-decoration:underline;}
p{margin:6px 0;}
@media (prefers-color-scheme:dark){
body{background:#0D1117;color:#C9D1D9;}
#thinking-area{border-color:#30363D;}
#thinking-header{background:#161B22;color:#8B949E;border-bottom-color:#30363D;}
#thinking-body{background:#0D1117;color:#6E7681;}
pre{background:#161B22;border-color:#30363D;}
code{background:#161B22;color:#C9D1D9;}
th{background:#161B22;}
th,td{border-color:#30363D;}
h1,h2{border-bottom-color:#21262D;}
blockquote{border-left-color:#30363D;background:#161B22;color:#8B949E;}
hr{border-top-color:#21262D;}
a{color:#58A6FF;}
}
";

    /// <summary>
    /// 处理 JS 通过 window.chrome.webview.postMessage 发送的消息。
    /// 消息格式：{ type: "complete", text: "..." } 或 { type: "error", message: "..." }
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;

            var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Dynamic.ExpandoObject>(json);
            var dict = (System.Collections.Generic.IDictionary<string, object>)msg;

            if (!dict.TryGetValue("type", out var typeObj)) return;
            var type = typeObj?.ToString();

            if (type == "complete" && dict.TryGetValue("text", out var textObj))
            {
                OnJsStreamComplete(textObj?.ToString() ?? string.Empty);
            }
            else if (type == "error" && dict.TryGetValue("message", out var msgObj))
            {
                OnJsStreamError(msgObj?.ToString() ?? "未知错误");
            }
            else if (type == "status" && dict.TryGetValue("message", out var statusMsgObj))
            {
                // JS 发来的状态更新（如"AI 正在思考..."、"AI 正在回答..."）
                StatusText.Text = statusMsgObj?.ToString() ?? "处理中...";
            }
        }
        catch { }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_stopwatch.IsRunning)
        {
            var elapsed = _stopwatch.Elapsed.TotalSeconds;
            ElapsedText.Text = $"{elapsed:F1}s";
        }
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_extractedCode))
            {
                Clipboard.SetText(_extractedCode);
                CopyCodeButton.Content = "✅ 已复制";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { CopyCodeButton.Content = "📝 复制代码"; timer.Stop(); };
                timer.Start();
            }
        }
        catch { }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = CollectedResponse;
            if (!string.IsNullOrWhiteSpace(text))
            {
                Clipboard.SetText(text);
                CopyButton.Content = "✅ 已复制";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { CopyButton.Content = "📋 复制响应"; timer.Stop(); };
                timer.Start();
            }
        }
        catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// JS 流式传输完成回调（由 window.chrome.webview.postMessage 触发）。
    /// 在 UI 线程上停止计时器、更新状态。
    /// </summary>
    private void OnJsStreamComplete(string collectedText)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _stopwatch.Stop();
            _statusTimer?.Stop();

            IsStreamCompleted = true;
            lock (_syncLock) { _collectedResponse.Append(collectedText); }

            ElapsedText.Text = $"{_stopwatch.Elapsed.TotalSeconds:F1}s";
            StatusText.Text = "✅ 响应完成";
            ProgressBar.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
        }));
    }

    /// <summary>
    /// JS 流式传输出错回调（由 window.chrome.webview.postMessage 触发）。
    /// </summary>
    private void OnJsStreamError(string errorMessage)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _stopwatch.Stop();
            _statusTimer?.Stop();

            HasError = true;
            lock (_syncLock) { _collectedResponse.Append(errorMessage); }

            StatusText.Text = "❌ 连接失败";
            ProgressBar.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
        }));
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer?.Stop();
        base.OnClosed(e);
    }
}
