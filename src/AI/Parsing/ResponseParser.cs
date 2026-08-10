using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.Shared;

namespace SSMSBoostPlus.AI.Parsing;

/// <summary>
/// AI 响应解析实现，负责解析 CLI 输出的 JSON/stream-json 格式数据。
/// 
/// 解析策略：
/// - 非流式 JSON：提取 content/text/message 字段作为文本，inputTokens/outputTokens 作为用量
/// - 流式 JSON 单行：根据 type 字段判断块类型，提取 content 字段作为内容
/// - JSON 解析失败时降级处理（非流式返回原文，流式作纯文本块）
/// </summary>
public class ResponseParser : IResponseParser
{
    /// <inheritdoc />
    public AIResponse ParseResponse(string rawResponse, PromptTemplate template)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return new AIResponse
            {
                IsSuccess = false,
                ErrorMessage = "CLI 返回空响应"
            };
        }

        try
        {
            var json = JToken.Parse(rawResponse);

            // 尝试多种可能的字段名（兼容不同 CLI 输出格式）
            var content = json.SelectToken("$.content")?.ToString()
                        ?? json.SelectToken("$.text")?.ToString()
                        ?? json.SelectToken("$.message")?.ToString()
                        ?? json.ToString();

            var inputTokens = json.SelectToken("$.inputTokens")?.Value<int>() ?? 0;
            var outputTokens = json.SelectToken("$.outputTokens")?.Value<int>() ?? 0;

            return new AIResponse
            {
                Content = content,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                IsSuccess = true
            };
        }
        catch (JsonException)
        {
            // JSON 解析失败，直接将原始文本作为内容返回
            return new AIResponse
            {
                Content = rawResponse,
                IsSuccess = true
            };
        }
    }

    /// <inheritdoc />
    public AIStreamChunk ParseStreamChunk(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Text,
                Content = string.Empty,
                IsComplete = false
            };
        }

        try
        {
            var json = JObject.Parse(jsonLine);
            var typeStr = json.SelectToken("$.type")?.ToString() ?? "text";

            var chunkType = ParseChunkType(typeStr);
            var content = json.SelectToken("$.content")?.ToString()
                         ?? json.SelectToken("$.text")?.ToString()
                         ?? json.ToString();

            return new AIStreamChunk
            {
                ChunkType = chunkType,
                Content = content,
                IsComplete = chunkType == AIStreamChunkType.Complete
            };
        }
        catch (JsonException)
        {
            // JSON 解析失败，将整行作为纯文本块处理
            return new AIStreamChunk
            {
                ChunkType = AIStreamChunkType.Text,
                Content = jsonLine,
                IsComplete = false
            };
        }
    }

    #region 私有方法

    /// <summary>
    /// 将字符串类型的块标识映射为枚举值。
    /// </summary>
    private static AIStreamChunkType ParseChunkType(string type)
    {
        return type?.ToLowerInvariant() switch
        {
            "text" => AIStreamChunkType.Text,
            "tool_use" or "tooluse" => AIStreamChunkType.ToolUse,
            "tool_result" or "toolresult" => AIStreamChunkType.ToolResult,
            "error" => AIStreamChunkType.Error,
            "complete" or "done" => AIStreamChunkType.Complete,
            _ => AIStreamChunkType.Text
        };
    }

    #endregion
}
