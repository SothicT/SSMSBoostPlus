using System;

namespace SSMSBoostPlus.Shared.Extensions;

/// <summary>
/// 字符串扩展方法集合，提供 CLI 转义、截断、Token 检测等工具方法。
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// 转义字符串用于 CLI 参数传递（处理换行符、反斜杠、双引号）。
    /// </summary>
    /// <remarks>
    /// 转义顺序很重要：
    /// 1. 反斜杠必须最先转义（避免二次转义）
    /// 2. 换行符（CRLF/LF/CR）统一转换为 \n
    /// 3. 制表符转换为 \t
    /// 4. 双引号最后转义
    /// </remarks>
    public static string EscapeForCli(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return input
            .Replace("\\", "\\\\")      // 反斜杠转义（必须最先）
            .Replace("\r\n", "\\n")    // Windows CRLF → \n
            .Replace("\n", "\\n")       // Linux LF → \n
            .Replace("\r", "\\n")      // 旧版 Mac CR → \n
            .Replace("\t", "\\t")      // 制表符
            .Replace("\"", "\\\"");    // 双引号
    }

    /// <summary>
    /// 截断字符串到指定最大长度，超出时追加省略号后缀。
    /// </summary>
    /// <param name="input">原始字符串</param>
    /// <param name="maxLength">最大允许长度（含后缀）</param>
    /// <param name="suffix">超出时的追加后缀，默认为 "..."</param>
    public static string Truncate(this string input, int maxLength, string suffix = "...")
    {
        if (string.IsNullOrEmpty(input) || input.Length <= maxLength)
            return input;

        return input.Substring(0, maxLength - suffix.Length) + suffix;
    }

    /// <summary>
    /// 判断字符串是否包含指定 token（简单包含匹配）。
    /// </summary>
    public static bool ContainsToken(this string input, string token)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(token))
            return false;

        return input.IndexOf(token, StringComparison.Ordinal) >= 0;
    }
}
