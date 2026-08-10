namespace SSMSBoostPlus.Shared;

/// <summary>
/// SQL 代码格式化风格枚举。
/// 用于 AI 格式化命令和配置。
/// </summary>
public enum FormatStyle
{
    /// <summary>
    /// Microsoft 风格：
    /// - 关键字大写（SELECT, FROM, WHERE）
    /// - 4 空格缩进
    /// - 逗号放在行尾
    /// </summary>
    Microsoft,

    /// <summary>
    /// Google 风格：
    /// - 关键字小写（select, from, where）
    /// - 2 空格缩进
    /// - 逗号放在行首
    /// </summary>
    Google
}
