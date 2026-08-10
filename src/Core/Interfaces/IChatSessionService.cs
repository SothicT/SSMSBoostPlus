using System;
using System.Collections.Generic;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// AI 聊天会话服务抽象。管理会话期间的消息列表，支持事件驱动的 UI 刷新。
/// 实现应为内存级别（会话关闭后清空），不涉及持久化。
/// </summary>
public interface IChatSessionService
{
    /// <summary>当前会话中所有消息的只读列表</summary>
    IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>向会话追加一条消息</summary>
    void AddMessage(ChatMessage message);

    /// <summary>更新会话中指定消息的内容（用于流式场景实时更新）</summary>
    void UpdateMessage(string messageId, string content, string? thinkingContent = null);

    /// <summary>设置指定消息的流式完成状态（完成时调用，触发最终 UI 更新）</summary>
    void CompleteMessage(string messageId);

    /// <summary>清空当前会话的所有消息</summary>
    void Clear();

    /// <summary>会话中有新消息添加时触发（UI 可订阅以刷新）</summary>
    event EventHandler<ChatMessage>? MessageAdded;

    /// <summary>会话中已有消息内容更新时触发（流式场景）</summary>
    event EventHandler<ChatMessage>? MessageUpdated;

    /// <summary>会话被清空时触发</summary>
    event EventHandler? SessionCleared;
}

#region 聊天消息模型（Core 层基类，AI 层可扩展）

/// <summary>
/// 聊天消息角色枚举。
/// </summary>
public enum ChatRole
{
    /// <summary>用户消息</summary>
    User,
    /// <summary>AI 助手消息</summary>
    Assistant,
    /// <summary>系统提示消息（通常不可见）</summary>
    System
}

/// <summary>
/// 聊天消息操作类型枚举。
/// </summary>
public enum ChatActionType
{
    /// <summary>应用到编辑器</summary>
    ApplyCode,
    /// <summary>打开 Diff 对比</summary>
    OpenDiff,
    /// <summary>复制代码到剪贴板</summary>
    CopyCode
}

/// <summary>
/// 聊天消息中可附带的操作按钮模型。
/// </summary>
public class ChatAction
{
    /// <summary>操作类型</summary>
    public ChatActionType Type { get; set; }

    /// <summary>按钮显示标签（如"📋 应用此修改"）</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>原始代码（用户选中的 SQL）</summary>
    public string? OriginalCode { get; set; }

    /// <summary>AI 建议的代码</summary>
    public string? SuggestedCode { get; set; }
}

/// <summary>
/// 聊天消息模型。放置在 Core 层以避免循环依赖，
/// AI 层可通过扩展方法或子类添加更多字段。
/// </summary>
public class ChatMessage
{
    /// <summary>消息唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>消息角色</summary>
    public ChatRole Role { get; set; }

    /// <summary>消息文本内容（支持 Markdown）</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>AI 思考过程内容（流式模式下实时累积，渲染在可折叠区域）</summary>
    public string? ThinkingContent { get; set; }

    /// <summary>是否正在流式接收中（标记消息内容仍在实时更新）</summary>
    public bool IsStreaming { get; set; }

    /// <summary>消息时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>用户消息附带的可折叠 SQL 代码（AI 消息为 null）</summary>
    public string? AttachedCode { get; set; }

    /// <summary>AI 回复中提取出的操作列表（用户消息为 null）</summary>
    public List<ChatAction> Actions { get; set; } = new();
}

#endregion