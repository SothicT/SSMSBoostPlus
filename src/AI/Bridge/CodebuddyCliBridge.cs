using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.AI.Parsing;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.Shared;
using SSMSBoostPlus.Shared.Extensions;

// ReSharper disable once CheckNamespace - 文件范围命名空间

namespace SSMSBoostPlus.AI.Bridge;

/// <summary>
/// Codebuddy CLI 子进程桥接实现。
/// 通过 System.Diagnostics.Process 启动 codebuddy CLI，
/// 支持非流式（完整 JSON 响应）和流式（stream-json）两种模式。
/// </summary>
public class CodebuddyCliBridge : ICodebuddyBridge
{
    /// <summary>CLI 可执行文件名称</summary>
    private const string CliName = Constants.CodebuddyCliName; // "codebuddy"

    /// <summary>响应内容截断阈值</summary>
    private const int ResponseTruncateLength = 2000;

    /// <summary>实际使用的 CLI 路径（可能为完整路径或仅文件名）</summary>
    private readonly string _cliPath;

    /// <summary>默认超时时间</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(Constants.DefaultCliTimeoutMs);

    private readonly IPromptTemplateManager _promptManager;
    private readonly IResponseParser _responseParser;
    private readonly TimeSpan _timeout;
    private readonly ILoggerService _logger;

    /// <summary>
    /// 初始化 CodebuddyCliBridge 实例。
    /// </summary>
    /// <param name="promptManager">Prompt 模板管理器</param>
    /// <param name="responseParser">响应解析器</param>
    /// <param name="logger">日志服务（可选，为 null 时静默跳过日志）</param>
    /// <param name="cliPath">CLI 可执行文件路径（可选，默认使用 PATH 中的 "codebuddy"）</param>
    /// <param name="timeout">请求超时时间，默认 2 分钟</param>
    public CodebuddyCliBridge(
        IPromptTemplateManager promptManager,
        IResponseParser responseParser,
        ILoggerService? logger = null,
        string? cliPath = null,
        TimeSpan? timeout = null)
    {
        _promptManager = promptManager ?? throw new ArgumentNullException(nameof(promptManager));
        _responseParser = responseParser ?? throw new ArgumentNullException(nameof(responseParser));
        _logger = logger ?? new ConsoleLoggerService();
        _cliPath = string.IsNullOrWhiteSpace(cliPath) ? CliName : cliPath!;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<AIResponse> ExecuteAsync(AIRequest request, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var context = BuildContextFromRequest(request);
        var prompt = _promptManager.BuildPrompt(request.Template, request.Code, context);
        var systemPrompt = _promptManager.GetSystemPrompt(request.Template);

        // 记录请求 Prompt（转义前）
        LogPrompt(request.Template, systemPrompt, prompt);

        // 转义 Prompt 并记录转义后的实际参数字符串（便于排查 CLI 传递问题）
        // 只读模式：在 System Prompt 尾部追加只读约束指令
        var effectiveSystemPrompt = systemPrompt;
        if (request.ReadOnly)
        {
            effectiveSystemPrompt += "\n\n## 只读约束\n"
                + "你处于**只读建议模式**，必须严格遵守以下规则：\n"
                + "- ✅ 你可以读取和分析用户提供的代码\n"
                + "- ✅ 你的回答应以文本建议形式输出（Markdown 格式）\n"
                + "- ❌ 禁止使用任何文件写入、编辑、创建、删除工具\n"
                + "- ❌ 禁止执行 Shell 命令\n"
                + "- ❌ 禁止修改任何本地文件\n\n"
                + "你的任务是提供分析和建议，而不是替用户修改代码。";
        }

        var escapedPrompt = prompt.EscapeForCli();
        var escapedSystemPrompt = effectiveSystemPrompt.EscapeForCli();
        var outputFormat = "json";
        var cliArgs = $"-p {escapedPrompt} --output-format {outputFormat} "
                    + $"--permission-mode bypassPermissions "
                    + $"--append-system-prompt {escapedSystemPrompt}";
        _logger.Information(
            $"[AI Bridge] CLI 转义后参数 ({cliArgs.Length} chars):\n{cliArgs}");

        var startInfo = CreateProcessStartInfo(cliArgs);

        try
        {
            var (output, error, elapsed) = await RunProcessAsync(startInfo, ct).ConfigureAwait(false);

            // CLI 标准输出为空但标准错误有内容 → 执行失败
            if (!string.IsNullOrEmpty(error) && string.IsNullOrEmpty(output))
            {
                var failResponse = new AIResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"CLI 执行失败: {error}",
                    Duration = elapsed
                };
                LogResponse(request.Template, failResponse);
                return failResponse;
            }

            var response = _responseParser.ParseResponse(output, request.Template);
            LogResponse(request.Template, response);
            return response;
        }
        catch (OperationCanceledException)
        {
            var cancelResponse = new AIResponse
            {
                IsSuccess = false,
                ErrorMessage = "请求已取消",
                Duration = TimeSpan.Zero
            };
            LogResponse(request.Template, cancelResponse);
            return cancelResponse;
        }
        catch (Exception ex)
        {
            var errorResponse = new AIResponse
            {
                IsSuccess = false,
                ErrorMessage = $"CLI 执行异常: {ex.Message}",
                Duration = TimeSpan.Zero
            };
            _logger.Error(ex, $"[AI Bridge] CLI 执行异常 | 类型: {request.Template}");
            LogResponse(request.Template, errorResponse);
            return errorResponse;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AIStreamChunk> ExecuteStreamAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var context = BuildContextFromRequest(request);
        var prompt = _promptManager.BuildPrompt(request.Template, request.Code, context);
        var systemPrompt = _promptManager.GetSystemPrompt(request.Template);

        // 转义 Prompt 并记录转义后的实际参数字符串
        // 只读模式：在 System Prompt 尾部追加只读约束指令
        var effectiveSystemPrompt = systemPrompt;
        if (request.ReadOnly)
        {
            effectiveSystemPrompt += "\n\n## 只读约束\n"
                + "你处于**只读建议模式**，必须严格遵守以下规则：\n"
                + "- ✅ 你可以读取和分析用户提供的代码\n"
                + "- ✅ 你的回答应以文本建议形式输出（Markdown 格式）\n"
                + "- ❌ 禁止使用任何文件写入、编辑、创建、删除工具\n"
                + "- ❌ 禁止执行 Shell 命令\n"
                + "- ❌ 禁止修改任何本地文件\n\n"
                + "你的任务是提供分析和建议，而不是替用户修改代码。";
        }

        var escapedPrompt = prompt.EscapeForCli();
        var escapedSystemPrompt = effectiveSystemPrompt.EscapeForCli();
        var outputFormat = "stream-json";
        var cliArgs = $"-p {escapedPrompt} --output-format {outputFormat} "
                    + $"--permission-mode bypassPermissions "
                    + $"--append-system-prompt {escapedSystemPrompt}";
        _logger.Information(
            $"[AI Bridge] CLI 转义后参数(流式) ({cliArgs.Length} chars):\n{cliArgs}");

        var startInfo = CreateProcessStartInfo(cliArgs);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            process.Start();

            // 逐行读取流式输出
            while (!process.StandardOutput.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;

                var chunk = _responseParser.ParseStreamChunk(line);
                yield return chunk;

                // 遇到完成或错误块，终止枚举
                if (chunk.IsComplete || chunk.ChunkType == AIStreamChunkType.Error)
                {
                    yield break;
                }
            }

            // 正常结束：发送 Complete 块
            yield return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Complete,
                Content = string.Empty,
                IsComplete = true
            };
        }
        finally
        {
            TryKillProcess(process);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsCliAvailableAsync()
    {
        // ① 直接执行（适用于 .exe 文件）
        if (await TryRunVersionDirectAsync())
            return true;

        // ② cmd.exe /c 回退（适用于 .cmd / 无扩展名脚本）
        return await TryRunVersionCmdFallbackAsync();
    }

    /// <summary>
    /// 直接 Process.Start 检测 CLI（适用于 .exe）。
    /// </summary>
    private async Task<bool> TryRunVersionDirectAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _cliPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var completed = await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return completed && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 cmd.exe /c 检测 CLI（适用于 .cmd / .bat 脚本）。
    /// </summary>
    private async Task<bool> TryRunVersionCmdFallbackAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{_cliPath}\" --version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var completed = await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return completed && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    #region 私有辅助方法

    /// <summary>
    /// 将 AIRequest 中的上下文字段映射为 AIContext 对象，
    /// 以便传入 PromptTemplateManager.BuildPrompt() 构建完整的 Prompt。
    /// </summary>
    private static AIContext BuildContextFromRequest(AIRequest request)
    {
        return new AIContext
        {
            SchemaInfo = request.SchemaInfo,
            IndexInfo = request.IndexInfo,
            ExecutionPlan = request.ExecutionPlan,
            FormatStyle = request.FormatStyle,
            DatabaseName = request.DatabaseName
        };
    }

    /// <summary>
    /// 创建进程启动信息（隐藏窗口、重定向输入输出、UTF-8 编码）。
    /// 自动检测 .cmd / 无 .exe 扩展名的情况，通过 cmd.exe /c 包装执行。
    /// </summary>
    private ProcessStartInfo CreateProcessStartInfo(string arguments)
    {
        // 判断是否需要用 cmd.exe 包装：非 .exe 文件（.cmd、无扩展名等）无法直接 Process.Start
        var needsCmdFallback = !_cliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        if (needsCmdFallback)
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{_cliPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
        }

        return new ProcessStartInfo
        {
            FileName = _cliPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
    }

    /// <summary>
    /// 启动进程并等待退出或超时。
    /// </summary>
    private async Task<(string output, string error, TimeSpan elapsed)> RunProcessAsync(
        ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var sw = Stopwatch.StartNew();

        // 并发读取 stdout 和 stderr（避免死锁）
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var completed = await WaitForExitAsync(process, _timeout).ConfigureAwait(false);

        sw.Stop();

        if (!completed)
        {
            TryKillProcess(process);
            throw new TimeoutException($"CLI 执行超时（{_timeout.TotalSeconds}秒）");
        }

        ct.ThrowIfCancellationRequested();

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        return (output, error, sw.Elapsed);
    }

    /// <summary>
    /// 等待进程退出，带超时控制。
    /// </summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>();

        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcs.TrySetResult(true);

        if (process.HasExited)
            return true;

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completedTask == tcs.Task;
    }

    /// <summary>
    /// 安全终止进程（忽略已退出的异常情况）。
    /// </summary>
    private static void TryKillProcess(Process? process)
    {
        if (process == null || process.HasExited) return;

        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // 进程已退出，忽略
        }
    }

    /// <summary>
    /// 记录发送给 AI 的 Prompt（System Prompt + User Prompt）。
    /// </summary>
    private void LogPrompt(PromptTemplate template, string systemPrompt, string userPrompt)
    {
        try
        {
            var combinedPrompt = $"[System]\n{systemPrompt}\n\n[User]\n{userPrompt}";
            _logger.Information(
                $"[AI Bridge] 发送请求 | 类型: {template} | Prompt({combinedPrompt.Length} chars):\n{combinedPrompt}");
        }
        catch
        {
            // 日志记录失败不影响主流程
        }
    }

    /// <summary>
    /// 记录 AI 返回的响应内容（超过 2000 字符自动截断）。
    /// </summary>
    private void LogResponse(PromptTemplate template, AIResponse response)
    {
        try
        {
            if (response.IsSuccess)
            {
                var content = response.Content ?? string.Empty;
                var displayContent = TruncateContent(content);
                _logger.Information(
                    $"[AI Bridge] 收到响应 | 类型: {template} | 耗时: {response.Duration.TotalMilliseconds:F0}ms | Token: I:{response.InputTokens}/O:{response.OutputTokens} | 内容({content.Length} chars):\n{displayContent}");
            }
            else
            {
                _logger.Information(
                    $"[AI Bridge] 请求失败 | 类型: {template} | 耗时: {response.Duration.TotalMilliseconds:F0}ms | 错误: {response.ErrorMessage}");
            }
        }
        catch
        {
            // 日志记录失败不影响主流程
        }
    }

    /// <summary>
    /// 截断过长内容，保留前 N 字符并追加截断标记。
    /// </summary>
    private static string TruncateContent(string content)
    {
        if (content.Length <= ResponseTruncateLength)
            return content;

        return content.Substring(0, ResponseTruncateLength)
               + $"\n...[已截断, 共 {content.Length} 字符]";
    }

    #endregion
}
