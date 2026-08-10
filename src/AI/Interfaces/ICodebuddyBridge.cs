using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SSMSBoostPlus.AI.Models;

namespace SSMSBoostPlus.AI.Bridge;

/// <summary>
/// Codebuddy CLI 桥接接口，提供与 AI 服务的交互能力。
/// 支持非流式和流式两种调用模式。
/// </summary>
public interface ICodebuddyBridge
{
    /// <summary>
    /// 执行非流式 AI 请求，等待完整响应后返回。
    /// </summary>
    /// <param name="request">AI 请求对象</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>AI 完整响应</returns>
    Task<AIResponse> ExecuteAsync(AIRequest request, CancellationToken ct = default);

    /// <summary>
    /// 执行流式 AI 请求，通过异步枚举逐块返回响应。
    /// </summary>
    /// <param name="request">AI 请求对象</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>流式响应块枚举（支持 await foreach）</returns>
    IAsyncEnumerable<AIStreamChunk> ExecuteStreamAsync(AIRequest request, CancellationToken ct = default);

    /// <summary>
    /// 检测 Codebuddy CLI 是否在系统 PATH 中可用。
    /// </summary>
    /// <returns>CLI 是否可用</returns>
    Task<bool> IsCliAvailableAsync();
}
