using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.Shared;
using SSMSBoostPlus.Shared.Extensions;

namespace SSMSBoostPlus.AI.Bridge;

/// <summary>
/// HTTP API 桥接实现，支持两种模式：
/// 1. OpenAI 兼容 API（用户配置的自定义 API）
/// 2. SSE 流式 API（内置默认的飞书流式接口）
/// </summary>
public class AIApiClientBridge : ICodebuddyBridge
{
    /// <summary>响应内容截断阈值</summary>
    private const int ResponseTruncateLength = 2000;

    private readonly HttpClient _httpClient;
    private readonly AIConfiguration _config;
    private readonly ILoggerService _logger;
    private readonly bool _isBuiltInDefault;

    /// <summary>
    /// 初始化 HTTP API 桥接实例。
    /// </summary>
    public AIApiClientBridge(AIConfiguration config, ILoggerService? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? new ConsoleLoggerService();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        // 检测是否为内置默认 SSE 流式接口
        _isBuiltInDefault = string.Equals(config.ApiEndpoint, AIDefaults.DefaultEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<AIResponse> ExecuteAsync(AIRequest request, CancellationToken ct = default)
    {
        try
        {
            if (_isBuiltInDefault)
            {
                return await ExecuteSseAsync(request, ct);
            }

            return await ExecuteOpenAiAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            return CreateErrorResponse($"网络请求失败: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return CreateErrorResponse($"AI API 请求超时（{_config.TimeoutSeconds}s）");
        }
        catch (OperationCanceledException)
        {
            return CreateErrorResponse("请求已取消");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"[AI Bridge] HTTP API 异常 | 类型: {request.Template}");
            return CreateErrorResponse($"未知错误: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AIStreamChunk> ExecuteStreamAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_isBuiltInDefault)
        {
            // 内置默认 API 实现真正的 SSE 流式输出
            await foreach (var chunk in ExecuteSseStreamAsync(request, ct))
            {
                yield return chunk;
            }
        }
        else
        {
            // 非流式 API 回退为完整请求后一次性返回
            var response = await ExecuteAsync(request, ct);
            yield return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Text,
                Content = response.IsSuccess ? response.Content : response.ErrorMessage ?? string.Empty,
                IsComplete = false
            };
            yield return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Complete,
                Content = string.Empty,
                IsComplete = true
            };
        }
    }

    /// <inheritdoc />
    public Task<bool> IsCliAvailableAsync()
    {
        // 内置默认 API 始终可用；用户自定义 API 需配置 Endpoint
        if (_isBuiltInDefault)
            return Task.FromResult(true);

        return Task.FromResult(
            !string.IsNullOrEmpty(_config.ApiEndpoint) &&
            !string.IsNullOrEmpty(_config.ModelName));
    }

    #region OpenAI 兼容模式

    /// <summary>
    /// 执行 OpenAI 兼容 API 请求。
    /// </summary>
    private async Task<AIResponse> ExecuteOpenAiAsync(AIRequest request, CancellationToken ct)
    {
        var systemPrompt = GetSystemPromptForTemplate(request.Template);
        var userPrompt = GetUserPromptForTemplate(request);

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

        LogPrompt(request.Template, effectiveSystemPrompt, userPrompt);

        var requestBody = new
        {
            model = _config.ModelName,
            messages = new[]
            {
                new { role = "system", content = effectiveSystemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = _config.DefaultTemperature,
            max_tokens = _config.DefaultMaxTokens,
            stream = false
        };

        using var requestMessage = BuildOpenAiRequest(requestBody);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var httpResponse = await _httpClient.SendAsync(requestMessage, ct).ConfigureAwait(false);
        sw.Stop();

        httpResponse.EnsureSuccessStatusCode();
        var responseContent = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        var response = ParseOpenAiResponse(responseContent, sw.Elapsed);
        LogResponse(request.Template, response);
        return response;
    }

    /// <summary>
    /// 构建 OpenAI 兼容格式的 HTTP 请求。
    /// </summary>
    private HttpRequestMessage BuildOpenAiRequest(object requestBody)
    {
        var jsonContent = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint)
        {
            Content = content
        };

        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            var authValue = _config.UseBearerPrefix
                ? $"Bearer {_config.ApiKey}"
                : _config.ApiKey;
            requestMessage.Headers.Add("Authorization", authValue);
        }

        return requestMessage;
    }

    #endregion

    #region SSE 流式模式（内置默认 API）

    /// <summary>
    /// 执行 SSE 流式 API 请求，收集完整响应后返回。
    /// </summary>
    private async Task<AIResponse> ExecuteSseAsync(AIRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var collectedContent = new StringBuilder();

        // 记录请求 Prompt
        var userPrompt = BuildBuiltInUserPrompt(request);
        LogSsePrompt(request.Template, userPrompt);

        await foreach (var chunk in ExecuteSseStreamAsync(request, ct))
        {
            if (chunk.ChunkType == AIStreamChunkType.Text && chunk.Content != null)
            {
                collectedContent.Append(chunk.Content);
            }
        }
        sw.Stop();

        var response = new AIResponse
        {
            Content = collectedContent.ToString(),
            Duration = sw.Elapsed,
            IsSuccess = true
        };

        // 记录完整响应内容（截断到 2000 字符）
        LogSseResponse(request.Template, response);
        return response;
    }

    /// <summary>
    /// 执行 SSE 流式 API 请求，逐块返回流式内容。
    /// 请求体格式：{ q: "@SSMSBOOST ...", chat_id: "default", user_id: "default" }
    /// 响应格式：text/event-stream，每行 data: {text}\n\n
    /// 注意：yield return 不能放在 try-catch 块内，因此错误处理在单独的异步方法中完成。
    /// 注意：流式模式下完整响应由调用方（如 AIStreamResultDialog）记录。
    /// </summary>
    private async IAsyncEnumerable<AIStreamChunk> ExecuteSseStreamAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var userPrompt = BuildBuiltInUserPrompt(request);

        _logger.Information($"[AI Bridge] SSE 流式请求 | 类型: {request.Template} | Prompt({userPrompt.Length} chars):\n{TruncateContent(userPrompt)}");

        // 发送 HTTP 请求
        var httpStreamResult = await SendSseRequestAsync(userPrompt, ct);
        if (!httpStreamResult.Success)
        {
            yield return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Text,
                Content = httpStreamResult.ErrorMessage,
                IsComplete = true
            };
            yield break;
        }

        // 流式读取 SSE 响应（无 try-catch，所以 yield 是安全的）
        var reader = httpStreamResult.Reader!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ttfbSw = System.Diagnostics.Stopwatch.StartNew(); // 首token耗时（非空data）
        var firstChunkLogged = false;
        var inThinkBlock = false;
        var chunkIndex = 0;

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;

            // 非 data: 行跳过（SSE 注释行、空行分隔符等）
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            // 提取 data: 后的内容。注意：不移除前导空格，否则 SQL 缩进丢失
            // SSE 标准 "data: text" 格式：data: 后有一个可选空格，仅移除这一个空格
            var rawContent = line.Substring(5);
            var data = rawContent.Length > 0 && rawContent[0] == ' ' ? rawContent.Substring(1) : rawContent;
            chunkIndex++;

            // 空 data 行处理
            if (string.IsNullOrEmpty(data))
            {
                if (!firstChunkLogged)
                {
                    // 首个空 data → SSE 连接已建立，记录日志
                    _logger.Information($"[AI Bridge] SSE 连接已建立 (耗时: {ttfbSw.ElapsedMilliseconds}ms)，等待首个内容 token...");
                    continue;
                }
                // 已有内容后的空行 → 作为 Markdown 空行保留（段落分隔）
                yield return new AIStreamChunk
                {
                    ChunkType = AIStreamChunkType.Text,
                    Content = "\n",
                    IsComplete = false
                };
                continue;
            }

            // 记录首个非空 data 的 TTFB
            if (!firstChunkLogged)
            {
                ttfbSw.Stop();
                _logger.Information($"[AI Bridge] SSE 首 token (非空) 耗时: {ttfbSw.ElapsedMilliseconds}ms (TTFB), data=[{data}]");
                firstChunkLogged = true;
            }

            // 详细日志：前 10 个非空 chunk
            if (chunkIndex <= 15)
            {
                _logger.Debug($"[AI Bridge] SSE chunk #{chunkIndex}: [{data}]");
            }

            // 检测思考标签 <think...> ... </think...>
            // Qwen3 模型在思考阶段输出 <think\\>content</think\\>
            // 也可能输出 <think\\n> 或 <think > 等变体，用 Contains 宽松匹配
            if (data.Contains("<think"))
            {
                inThinkBlock = true;
                // 推送思考开始标记
                yield return new AIStreamChunk
                {
                    ChunkType = AIStreamChunkType.ThinkingStart,
                    Content = string.Empty,
                    IsComplete = false
                };
                // 如果 <think...> 后面还有内容，也推送
                var gtIdx = data.IndexOf(">");
                if (gtIdx >= 0 && gtIdx < data.Length - 1)
                {
                    var afterThink = data.Substring(gtIdx + 1);
                    if (!string.IsNullOrEmpty(afterThink))
                    {
                        yield return new AIStreamChunk
                        {
                            ChunkType = AIStreamChunkType.Thinking,
                            Content = afterThink,
                            IsComplete = false
                        };
                    }
                }
                continue;
            }

            if (data.Contains("</think"))
            {
                inThinkBlock = false;
                // 推送思考结束标记
                yield return new AIStreamChunk
                {
                    ChunkType = AIStreamChunkType.ThinkingEnd,
                    Content = string.Empty,
                    IsComplete = false
                };
                // 如果 </think...> 后面还有内容，作为正式文本推送
                var gtIdx = data.IndexOf(">");
                if (gtIdx >= 0 && gtIdx < data.Length - 1)
                {
                    var afterThink = data.Substring(gtIdx + 1);
                    if (!string.IsNullOrEmpty(afterThink))
                    {
                        yield return new AIStreamChunk
                        {
                            ChunkType = AIStreamChunkType.Text,
                            Content = afterThink,
                            IsComplete = false
                        };
                    }
                }
                continue;
            }

            // 思考阶段的内容作为 Thinking 类型推送
            if (inThinkBlock)
            {
                yield return new AIStreamChunk
                {
                    ChunkType = AIStreamChunkType.Thinking,
                    Content = data,
                    IsComplete = false
                };
                continue;
            }

            // 正式回答内容
            yield return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Text,
                Content = data,
                IsComplete = false
            };
        }

        sw.Stop();
        _logger.Information($"[AI Bridge] SSE 流式完成 | 类型: {request.Template} | 耗时: {sw.Elapsed.TotalMilliseconds:F0}ms");

        reader.Dispose();
        httpStreamResult.ResponseStream?.Dispose();
        httpStreamResult.HttpResponse?.Dispose();

        yield return new AIStreamChunk
        {
            ChunkType = AIStreamChunkType.Complete,
            Content = string.Empty,
            IsComplete = true
        };
    }

    /// <summary>
    /// 发送 SSE 流式 HTTP 请求，返回可读取的流。错误在此方法中处理。
    /// </summary>
    private async Task<SseStreamResult> SendSseRequestAsync(string userPrompt, CancellationToken ct)
    {
        var requestBody = new
        {
            q = userPrompt,
            chat_id = "default",
            user_id = "default"
        };

        var jsonContent = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint)
        {
            Content = content
        };

        try
        {
            var httpResponse = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            httpResponse.EnsureSuccessStatusCode();

            var responseStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            // bufferSize: 128 — 小缓冲区确保 SSE 数据不堆积，尽可能快地 yield 每个 chunk
            // 默认 1024 字节缓冲会攒满才返回，造成延迟
            var reader = new StreamReader(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 128);

            return new SseStreamResult
            {
                Success = true,
                HttpResponse = httpResponse,
                ResponseStream = responseStream,
                Reader = reader
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"[AI Bridge] SSE 流式请求失败");
            return new SseStreamResult
            {
                Success = false,
                ErrorMessage = $"请求失败: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// SSE 流式请求结果。
    /// </summary>
    private class SseStreamResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public HttpResponseMessage? HttpResponse { get; set; }
        public Stream? ResponseStream { get; set; }
        public StreamReader? Reader { get; set; }
    }

    /// <summary>
    /// 为内置默认 API 构建用户问题内容（添加 @SSMSBOOST 前缀 + 角色指令）。
    /// </summary>
    private string BuildBuiltInUserPrompt(AIRequest request)
    {
        var code = request.Code ?? string.Empty;
        var basePrompt = request.Template switch
        {
            PromptTemplate.Suggest => $"【角色】你是一位 SQL 代码审查专家。\n\n请优化如下语句：\n```sql\n{code}\n```",
            PromptTemplate.Comment => $"【角色】你是一位 SQL 注释专家。\n\n请为如下 SQL 代码添加中文注释：\n```sql\n{code}\n```",
            PromptTemplate.Format => $"【角色】你是一位 SQL 格式化专家。\n\n请格式化如下 SQL 代码（Microsoft 风格）：\n```sql\n{code}\n```",
            PromptTemplate.Analyze => $"【角色】你是一位 SQL 性能分析专家。\n\n请对如下 SQL 代码进行性能分析：\n```sql\n{code}\n```",
            PromptTemplate.Chat => $"【角色】你是一位数据库技术专家，精通 SQL Server / T-SQL，同时了解 MySQL、PostgreSQL。\n\n{code}",
            _ => code
        };

        // 追加上下文信息
        if (!string.IsNullOrWhiteSpace(request.SchemaInfo))
            basePrompt += $"\n\nSchema 信息：\n{request.SchemaInfo}";

        if (!string.IsNullOrWhiteSpace(request.IndexInfo))
            basePrompt += $"\n\n索引信息：\n{request.IndexInfo}";

        if (!string.IsNullOrWhiteSpace(request.ExecutionPlan))
            basePrompt += $"\n\n执行计划：\n{request.ExecutionPlan!.Truncate(8000)}";

        // 添加 @SSMSBOOST 前缀
        return $"{AIDefaults.UserPromptPrefix}{basePrompt}";
    }

    #endregion

    #region Prompt 构建（OpenAI 模式）

    /// <summary>
    /// 根据模板类型获取对应的 System Prompt。
    /// </summary>
    private static string GetSystemPromptForTemplate(PromptTemplate template)
    {
        return template switch
        {
            PromptTemplate.Suggest => @"你是一位 SQL Server 专家，专注于 T-SQL 代码优化和最佳实践。

你的任务：
1. 语法错误检测：识别 T-SQL 语法错误和常见错误模式
2. 代码精简：识别冗余子查询、可合并的 CTE、过度嵌套
3. 性能优化：从分页、索引、事务等角度分析
4. 安全审查：检查 SQL 注入、死锁风险、事务范围过大

输出格式：
- 使用 Markdown 格式
- 每个建议单独列出，附带原始代码和建议代码
- 明确标注建议分类（安全风险 / 语法错误 / 代码精简 / 性能优化）
- 提供具体的优化理由",

            PromptTemplate.Comment => @"你是一位 SQL Server 专家，专注于为 T-SQL 代码生成清晰有用的注释。

你的任务：
1. 为存储过程/函数添加头部块注释（用途、参数、返回值）
2. 为每个主要 SQL 语句添加功能说明注释
3. 为 JOIN 关系添加注释（关联条件、目的）
4. 为 WHERE 条件添加注释（过滤逻辑）
5. 为 CTE 或子查询添加注释（用途）
6. 为变量声明添加注释

注释原则：
- 简洁清晰，避免过度注释
- 简单代码少注释，复杂代码详细注释
- 注释解释""为什么""，而非""是什么""
- 使用中文注释
- 头部块注释使用 /* */，行内注释使用 --
- 注释放在对应语句的上方

仅输出带有注释的完整 SQL 代码，无需额外解释文字。",

            PromptTemplate.Format => @"你是一位 SQL Server 专家，专注于 SQL 代码格式化。

格式化规则：
关键字：大写（SELECT, FROM, WHERE）
缩进：4 空格
换行：每个主要关键字前换行
逗号位置：列名后，行尾
对齐：SELECT 列表左对齐
特殊结构：CASE 对齐、CTE 独立行、窗口函数换行
保留 Schema 前缀（dbo.），保留 AS 关键字

仅输出格式化后的完整 SQL 代码，无需额外解释文字。",

            PromptTemplate.Analyze => @"你是一位 SQL Server 性能优化专家，擅长分析查询性能并给出优化方案。

你的任务：
1. 分析执行计划，识别高代价操作符（含 Spool 操作）
2. 评估统计信息：检查估算行数与实际行数差异
3. 检测缺失索引（分析谓词列、JOIN 列、ORDER BY 列）
4. 识别性能瓶颈（全表扫描、隐式类型转换、参数嗅探、TempDB 压力、锁/阻塞）
5. 给出具体的优化方案（包含 CREATE INDEX DDL、统计信息维护建议）

输出格式（使用 Markdown）：
## 📊 执行计划分析
[执行计划关键操作解读]

## 📈 统计信息评估
[统计信息状态及建议]

## 🔍 缺失索引
[索引建议及创建语句]

## ⚠️ 性能瓶颈
[识别的性能问题]

## ✅ 优化方案
[具体的优化建议和代码]

输出语言：中文",

            PromptTemplate.Chat => @"你是一位数据库技术专家，精通 SQL Server / T-SQL，同时对 MySQL、PostgreSQL 等主流数据库有深入了解。

你的角色定位：
- 擅长 T-SQL / SQL 查询编写与优化
- 精通 SQL Server 性能优化（执行计划、索引、锁等）
- 能够进行跨数据库兼容性分析

约束：
- 当用户提供 SQL 代码时，优先分析代码中的问题
- 当用户问题不明确时，主动追问澄清
- 回答使用 Markdown 格式
- 输出语言：中文
- 保持简洁，重点突出",

            _ => "你是一位 SQL Server 助手。"
        };
    }

    /// <summary>
    /// 根据模板类型构建 User Prompt 内容（OpenAI 模式）。
    /// </summary>
    private static string GetUserPromptForTemplate(AIRequest request)
    {
        var code = request.Code ?? string.Empty;
        var basePrompt = request.Template switch
        {
            PromptTemplate.Suggest => $"请分析以下 SQL 代码并提供改进建议：\n\n```sql\n{code}\n```",
            PromptTemplate.Comment => $"请为以下 SQL 代码生成注释：\n\n```sql\n{code}\n```",
            PromptTemplate.Format => $"请格式化以下 SQL 代码：\n\n```sql\n{code}\n```",
            PromptTemplate.Analyze => $"请对以下 SQL 代码进行性能分析：\n\n```sql\n{code}\n```",
            PromptTemplate.Chat => code,
            _ => code
        };

        // 追加上下文信息
        if (!string.IsNullOrWhiteSpace(request.SchemaInfo))
            basePrompt += $"\n\n**Schema 信息：**\n\n{request.SchemaInfo}";

        if (!string.IsNullOrWhiteSpace(request.IndexInfo))
            basePrompt += $"\n\n**索引信息：**\n\n{request.IndexInfo}";

        if (!string.IsNullOrWhiteSpace(request.ExecutionPlan))
            basePrompt += $"\n\n**执行计划：**\n\n{request.ExecutionPlan!.Truncate(8000)}";

        return basePrompt;
    }

    #endregion

    #region 响应解析

    /// <summary>
    /// 解析 OpenAI 兼容格式的 API 响应。
    /// </summary>
    private static AIResponse ParseOpenAiResponse(string responseContent, TimeSpan duration)
    {
        try
        {
            var json = JObject.Parse(responseContent);
            var text = json["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
            var usage = json["usage"];

            return new AIResponse
            {
                Content = text,
                InputTokens = usage?["prompt_tokens"]?.Value<int>() ?? 0,
                OutputTokens = usage?["completion_tokens"]?.Value<int>() ?? 0,
                Duration = duration,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            return new AIResponse
            {
                Content = responseContent,
                Duration = duration,
                IsSuccess = true,
                ErrorMessage = $"响应解析警告: {ex.Message}"
            };
        }
    }

    #endregion

    #region 辅助方法

    private AIResponse CreateErrorResponse(string message)
    {
        var response = new AIResponse
        {
            IsSuccess = false,
            ErrorMessage = message,
            Duration = TimeSpan.Zero
        };
        return response;
    }

    /// <summary>
    /// 记录发送给 AI 的 Prompt（OpenAI 模式）。
    /// </summary>
    private void LogPrompt(PromptTemplate template, string systemPrompt, string userPrompt)
    {
        try
        {
            var combinedPrompt = $"[System]\n{systemPrompt}\n\n[User]\n{userPrompt}";
            _logger.Information(
                $"[AI Bridge] 发送请求 | 类型: {template} | Prompt({combinedPrompt.Length} chars):\n{combinedPrompt}");
        }
        catch { }
    }

    /// <summary>
    /// 记录 SSE 内置 API 的请求 Prompt。
    /// </summary>
    private void LogSsePrompt(PromptTemplate template, string userPrompt)
    {
        try
        {
            _logger.Information(
                $"[AI Bridge] SSE 发送请求 | 类型: {template} | Prompt({userPrompt.Length} chars):\n{TruncateContent(userPrompt)}");
        }
        catch { }
    }

    /// <summary>
    /// 记录 AI 返回的响应内容（OpenAI 模式）。
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
        catch { }
    }

    /// <summary>
    /// 记录 SSE 内置 API 的响应内容。
    /// </summary>
    private void LogSseResponse(PromptTemplate template, AIResponse response)
    {
        try
        {
            if (response.IsSuccess)
            {
                var content = response.Content ?? string.Empty;
                _logger.Information(
                    $"[AI Bridge] SSE 收到响应 | 类型: {template} | 耗时: {response.Duration.TotalMilliseconds:F0}ms | 内容({content.Length} chars):\n{TruncateContent(content)}");
            }
            else
            {
                _logger.Information(
                    $"[AI Bridge] SSE 请求失败 | 类型: {template} | 耗时: {response.Duration.TotalMilliseconds:F0}ms | 错误: {response.ErrorMessage}");
            }
        }
        catch { }
    }

    /// <summary>
    /// 截断过长内容。
    /// </summary>
    private static string TruncateContent(string content)
    {
        if (content.Length <= ResponseTruncateLength)
            return content;

        return content.Substring(0, ResponseTruncateLength)
               + $"\n...[已截断, 共 {content.Length} 字符]";
    }

    #endregion

    /// <summary>
    /// 释放 HTTP 客户端资源。
    /// </summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}