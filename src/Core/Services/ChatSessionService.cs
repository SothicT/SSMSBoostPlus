using System;
using System.Collections.Generic;
using System.Linq;
using SSMSBoostPlus.Interfaces;

namespace SSMSBoostPlus.Services;

/// <summary>
/// 内存聊天会话服务实现。会话期间保留消息列表，关闭 SSMS 后自动清空。
/// 通过事件通知 UI 层刷新消息显示。
/// </summary>
public class ChatSessionService : IChatSessionService
{
    private readonly List<ChatMessage> _messages = new();

    /// <inheritdoc />
    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();

    /// <inheritdoc />
    public event EventHandler<ChatMessage>? MessageAdded;

    /// <inheritdoc />
    public event EventHandler<ChatMessage>? MessageUpdated;

    /// <inheritdoc />
    public event EventHandler? SessionCleared;

    /// <inheritdoc />
    public void AddMessage(ChatMessage message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        _messages.Add(message);
        MessageAdded?.Invoke(this, message);
    }

    /// <inheritdoc />
    public void UpdateMessage(string messageId, string content, string? thinkingContent = null)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("消息 ID 不能为空", nameof(messageId));

        var message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message == null)
            throw new InvalidOperationException($"未找到 ID 为 '{messageId}' 的消息");

        // 更新内容（仅在非空时更新，避免覆盖完整内容为空）
        if (content != null)
            message.Content = content;

        // 更新思考内容
        if (thinkingContent != null)
            message.ThinkingContent = thinkingContent;

        message.IsStreaming = true;

        // 触发更新事件，UI 订阅以刷新
        MessageUpdated?.Invoke(this, message);
    }

    /// <inheritdoc />
    public void CompleteMessage(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("消息 ID 不能为空", nameof(messageId));

        var message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message == null)
            throw new InvalidOperationException($"未找到 ID 为 '{messageId}' 的消息");

        message.IsStreaming = false;

        // 最终状态更新，触发 UI 刷新
        MessageUpdated?.Invoke(this, message);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _messages.Clear();
        SessionCleared?.Invoke(this, EventArgs.Empty);
    }
}