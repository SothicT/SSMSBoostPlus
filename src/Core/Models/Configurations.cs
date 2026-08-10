using System;
using System.Collections.Generic;
using SSMSBoostPlus.Shared;

namespace SSMSBoostPlus.Models;

/// <summary>
/// AI API configuration model.
/// </summary>
public class AIConfiguration
{
    public string ApiKey { get; set; } = string.Empty;

    public string ApiEndpoint { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public double DefaultTemperature { get; set; } = 0.3;

    public int DefaultMaxTokens { get; set; } = 4000;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 默认格式化风格（使用 Shared 中的 FormatStyle 枚举）。
    /// </summary>
    public FormatStyle DefaultFormattingStyle { get; set; } = FormatStyle.Microsoft;

    /// <summary>
    /// CodeBuddy CLI 可执行文件的完整路径（可选）。
    /// 留空时不检测系统 PATH 中的 codebuddy。
    /// 示例: D:\node-v24.13.0\codebuddy.cmd
    /// </summary>
    public string CodebuddyCliPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否在 Authorization 头中使用 "Bearer " 前缀。
    /// 为 true 时发送 "Bearer {ApiKey}"，为 false 时直接发送 ApiKey 原始值。
    /// 默认 true（兼容 OpenAI 标准格式）。
    /// 内置默认 API 将此值设为 false。
    /// </summary>
    public bool UseBearerPrefix { get; set; } = true;
}

/// <summary>
/// 内置默认 AI API 常量。
/// 当用户未配置 CODEBUDDY CLI 和 HTTP API 时自动使用此默认接口。
/// 此接口为 SSE 流式 API，无需 API Key，请求体格式为 { q, chat_id, user_id }。
/// </summary>
public static class AIDefaults
{
    /// <summary>
    /// 默认 AI API 端点 URL（SSE 流式接口）。
    /// </summary>
    public const string DefaultEndpoint = "http://10.32.2.50:7500/feishu/stream/event";

    /// <summary>
    /// 默认 API 密钥值（SSE 流式接口无需认证）。
    /// </summary>
    public const string DefaultApiKey = "";

    /// <summary>
    /// 默认模型名称（SSE 流式接口不传模型参数）。
    /// </summary>
    public const string DefaultModelName = "";

    /// <summary>
    /// 用户问题前缀（所有 AI 请求内容前加上此前缀，用于触发服务端技能）。
    /// </summary>
    public const string UserPromptPrefix = "@SSMSBOOST @SSMSBOOST_SQL_性能分析SKILLS ";

    /// <summary>
    /// 新版 Chat API 基础 URL（两步调用：先 GET /chat/api/open 拿 chat_id，再 POST /chat/api/chat_message/{chat_id}）。
    /// </summary>
    public const string DefaultChatApiBaseUrl = "http://10.32.10.37:3210";

    /// <summary>
    /// 新版 Chat API 认证令牌（作为 Authorization Header 的值）。
    /// </summary>
    public const string DefaultAuthToken = "Bearer application-b1e60a63df9e30be7d2af45f5b20af77";
}

/// <summary>
/// Version guard configuration model.
/// </summary>
public class VersionGuardConfiguration
{
    public bool Enabled { get; set; } = true;

    public GuardAction DefaultAction { get; set; } = GuardAction.AskUser;

    public List<string> ExcludedObjects { get; set; } = new();

    public List<string> ExcludedDatabases { get; set; } = new() { "tempdb", "master", "model", "msdb" };

    public string AskToken { get; set; } = "#checklater";

    public string SkipToken { get; set; } = "#nosynccheck";

    public bool TokenCaseSensitive { get; set; } = true;

    public bool AutoRefreshOnMismatch { get; set; } = false;
}

/// <summary>
/// Logging configuration model.
/// </summary>
public class LoggingConfiguration
{
    /// <summary>是否启用日志文件记录（默认不勾选）</summary>
    public bool Enabled { get; set; } = false;

    public string LogLevel { get; set; } = "Debug";

    public string LogDirectory { get; set; } = "%INSTALL_DIR%\\Logs";

    public int RetentionDays { get; set; } = 30;

    public int MaxFileSizeMB { get; set; } = 10;
}

/// <summary>
/// 代码补全功能配置模型。
/// </summary>
public class CompletionConfiguration
{
    /// <summary>是否启用代码补全弹窗</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Local History configuration model.
/// </summary>
public class LocalHistoryConfig
{
    /// <summary>是否启用 Local History 功能</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>版本表名（默认 DBTrigger_Object_Version_Table）</summary>
    public string TableName { get; set; } = "DBTrigger_Object_Version_Table";

    /// <summary>版本表所在数据库名（默认 MESBASE），连接串由当前窗口自动推导时替换 InitialCatalog</summary>
    public string DatabaseName { get; set; } = "MESBASE";

    /// <summary>默认加载的版本数量上限</summary>
    public int MaxVersionsToLoad { get; set; } = 100;

    /// <summary>VersionGuard 检测到外部修改时是否自动弹出 Local History</summary>
    public bool AutoOpenOnExternalChange { get; set; } = false;

    /// <summary>是否默认启用窗口切换跟随（默认 true）</summary>
    public bool AutoFollowEnabled { get; set; } = true;

    /// <summary>WindowActivated 防抖间隔（毫秒，默认 300）</summary>
    public int DebounceMs { get; set; } = 300;

    /// <summary>是否在状态栏显示连接状态（默认 true）</summary>
    public bool ShowConnectionStatus { get; set; } = true;

    /// <summary>权限隔离：只允许查看这些数据库的版本历史，为空时不限制</summary>
    public List<string> AllowedDatabases { get; set; } = new();

    /// <summary>对象搜索下拉框初始加载数量上限（默认 200，替代硬编码 100）</summary>
    public int ObjectListTopLimit { get; set; } = 200;

    /// <summary>数据库列表缓存时间（分钟，默认 5，0 表示不缓存）</summary>
    public int DatabaseListCacheMinutes { get; set; } = 5;
}

/// <summary>
/// Guard action when version mismatch is detected.
/// </summary>
public enum GuardAction
{
    StopExecution,
    AskUser,
    LogOnly
}

/// <summary>
/// 向后兼容别名：FormattingStyle → Shared.FormatStyle。
/// 推荐新代码直接使用 SSMSBoostPlus.Shared.FormatStyle。
/// </summary>
#pragma warning disable CA1717 // 仅包含 Flags 的枚举应有复数命名
[Obsolete("请使用 SSMSBoostPlus.Shared.FormatStyle 替代。此别名仅为向后兼容保留。")]
public enum FormattingStyle
{
    Microsoft = (int)FormatStyle.Microsoft,
    Google = (int)FormatStyle.Google
}
#pragma warning restore CA1717
