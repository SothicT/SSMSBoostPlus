namespace SSMSBoostPlus.Shared;

/// <summary>
/// 全局共享常量定义，包含扩展名、版本号、Token 常量等。
/// </summary>
public static class Constants
{
    /// <summary>SSMSBoost++ 扩展显示名称</summary>
    public const string ExtensionName = "SSMSBoost++";

    /// <summary>扩展版本号（语义化版本）</summary>
    public const string ExtensionVersion = "0.2.0";

    /// <summary>VSCT 命令集 GUID</summary>
    public const string CommandSetGuid = "B5E3C8D1-4A2F-4D7B-9E6A-1C3D5F7A9B2E";

    // ── Token 常量（版本守卫 SQL 注释指令）─────────────────

    /// <summary>跳过版本检查的 Token（写在 SQL 注释中）</summary>
    public const string SkipCheckToken = "#nosynccheck";

    /// <summary>延迟确认的 Token（跳过本次检查但下次仍提示）</summary>
    public const string ForceConfirmToken = "#checklater";

    // ── Codebuddy CLI 常量 ─────────────────────────────────

    /// <summary>Codebuddy CLI 可执行文件名称</summary>
    public const string CodebuddyCliName = "codebuddy";

    /// <summary>CLI 默认超时时间（毫秒），默认 2 分钟</summary>
    public const int DefaultCliTimeoutMs = 120_000;

    // ── 版本守卫 ───────────────────────────────────────────

    /// <summary>版本守卫单次检查最大耗时（毫秒）</summary>
    public const int MaxVersionCheckTimeMs = 100;
}
