using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// SQL 语句类型枚举。
    /// </summary>
    public enum StatementType
    {
        Select,
        Insert,
        Update,
        Delete,
        Merge,
        Exec,
        With,       // CTE
        DDL,        // CREATE/ALTER/DROP
        Unknown
    }

    /// <summary>
    /// SQL 语句断句器。从文本行集合中定位光标所在的完整 SQL 语句。
    /// 纯逻辑模块，不依赖 VS SDK / WPF，可直接单元测试。
    /// </summary>
    public class SqlBreaker
    {
        /// <summary>
        /// 顶层 SQL 起始关键词（按优先级排序）。
        /// 用于向前/向后搜索语句边界。
        /// </summary>
        private static readonly Regex TopLevelKeywordPattern = new Regex(
            @"^\s*(SELECT|UPDATE|DELETE|INSERT|MERGE|EXEC|EXECUTE|CREATE|ALTER|DROP|TRUNCATE|WITH|USE|GO|DECLARE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// GO 分隔符（行首，大小写不敏感）。
        /// </summary>
        private static readonly Regex GoPattern = new Regex(
            @"^\s*GO\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// 从光标位置找到其所属的完整 SQL 语句。
        /// </summary>
        /// <param name="lines">文本缓冲区的所有行</param>
        /// <param name="caretLine">光标所在行号（0-based）</param>
        /// <returns>当前语句的起始行号、结束行号、语句文本、语句类型</returns>
        public (int startLine, int endLine, string statementText, StatementType type) LocateCurrentStatement(
            string[] lines, int caretLine)
        {
            if (lines == null || lines.Length == 0)
                return (0, 0, "", StatementType.Unknown);

            // Phase 1: 向前搜索语句起始
            int startLine = FindStatementStart(lines, caretLine);

            // Phase 2: 向后搜索语句结束
            int endLine = FindStatementEnd(lines, startLine, caretLine);

            // Phase 3: 提取语句文本
            var sb = new StringBuilder();
            for (int i = startLine; i <= endLine && i < lines.Length; i++)
            {
                if (i > startLine) sb.Append('\n');
                sb.Append(lines[i]);
            }
            string statementText = sb.ToString();

            // Phase 4: 检测语句类型
            var type = DetectStatementType(statementText);

            return (startLine, endLine, statementText, type);
        }

        /// <summary>
        /// 从字符串文本中判断一条 SQL 语句的起始关键词类型。
        /// </summary>
        public StatementType DetectStatementType(string statementText)
        {
            if (string.IsNullOrWhiteSpace(statementText))
                return StatementType.Unknown;

            var trimmed = statementText.TrimStart();

            // WITH (CTE)
            if (trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                return StatementType.With;

            // SELECT
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                return StatementType.Select;

            // INSERT
            if (trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                return StatementType.Insert;

            // UPDATE
            if (trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                return StatementType.Update;

            // DELETE
            if (trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
                return StatementType.Delete;

            // MERGE
            if (trimmed.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase))
                return StatementType.Merge;

            // EXEC / EXECUTE
            if (trimmed.StartsWith("EXEC", StringComparison.OrdinalIgnoreCase))
                return StatementType.Exec;

            // DDL
            if (trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("DROP", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("TRUNCATE", StringComparison.OrdinalIgnoreCase))
                return StatementType.DDL;

            // DECLARE（变量声明，US-05 数据类型提示）
            if (trimmed.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase))
                return StatementType.Unknown; // 保持 Unknown，SyntaxLocator 会识别 DECLARE 关键词

            return StatementType.Unknown;
        }

        /// <summary>
        /// 向前搜索语句起始行。
        /// 采用前向扫描策略：从最近 GO 之后开始，跟踪括号深度，
        /// 找到深度0处最后一个顶层关键词作为语句起始。
        /// </summary>
        private int FindStatementStart(string[] lines, int caretLine)
        {
            // 找到光标行之前最近的 GO 分隔符
            int lastGo = -1;
            for (int i = caretLine; i >= 0; i--)
            {
                if (GoPattern.IsMatch(lines[i]))
                {
                    lastGo = i;
                    break;
                }
            }

            int startLine = lastGo + 1;
            if (startLine > caretLine) startLine = caretLine;
            if (startLine < 0) startLine = 0;

            int parenDepth = 0;
            bool seenWith = false;

            for (int i = startLine; i <= caretLine && i < lines.Length; i++)
            {
                string lineText = lines[i];

                // 检查 GO
                if (GoPattern.IsMatch(lineText))
                {
                    parenDepth = 0;
                    seenWith = false;
                    startLine = i + 1;
                    continue;
                }

                string codePart = StripLineComment(lineText);
                if (string.IsNullOrWhiteSpace(codePart))
                    continue;

                // 在深度0处检查顶层关键词
                if (parenDepth <= 0)
                {
                    var match = TopLevelKeywordPattern.Match(lineText);
                    if (match.Success)
                    {
                        string keyword = match.Groups[1].Value.ToUpperInvariant();

                        if (keyword == "WITH")
                        {
                            startLine = i;
                            seenWith = true;
                        }
                        else if (keyword != "GO" && keyword != "USE")
                        {
                            // CTE 主体关键词（SELECT/INSERT/UPDATE/DELETE）不作为新语句起始
                            if (!seenWith)
                            {
                                startLine = i;
                            }
                            // 如果 seenWith=true，保持 WITH 行为起始
                        }
                    }
                }

                // 更新括号深度
                parenDepth += CountParenDepth(codePart);
            }

            return Math.Max(0, Math.Min(startLine, caretLine));
        }

        /// <summary>
        /// 向后搜索语句结束行。
        /// 从起始行向下扫描，遇到下一个顶层关键词（同层、非嵌套）或 GO 时结束。
        /// 支持 CTE：WITH...AS(...) 后的主查询（SELECT/INSERT/UPDATE/DELETE）属于同一条语句。
        /// </summary>
        private int FindStatementEnd(string[] lines, int startLine, int caretLine)
        {
            int endLine = startLine;
            int parenDepth = 0;
            bool inBlockComment = false;

            // 检测语句是否以 WITH 开头（CTE）
            bool isCTE = false;
            if (startLine < lines.Length)
            {
                var firstMatch = TopLevelKeywordPattern.Match(lines[startLine]);
                if (firstMatch.Success && firstMatch.Groups[1].Value.ToUpperInvariant() == "WITH")
                    isCTE = true;
            }
            bool cteMainQuerySeen = false;

            for (int i = startLine; i < lines.Length; i++)
            {
                string lineText = lines[i];

                // 处理多行注释
                if (inBlockComment)
                {
                    int endComment = lineText.IndexOf("*/", StringComparison.OrdinalIgnoreCase);
                    if (endComment >= 0)
                    {
                        inBlockComment = false;
                        lineText = lineText.Substring(endComment + 2);
                    }
                    else
                    {
                        continue;
                    }
                }

                // 检查多行注释开始
                int commentStart = lineText.IndexOf("/*", StringComparison.OrdinalIgnoreCase);
                while (commentStart >= 0)
                {
                    int commentEnd = lineText.IndexOf("*/", commentStart + 2, StringComparison.OrdinalIgnoreCase);
                    if (commentEnd >= 0)
                    {
                        lineText = lineText.Substring(0, commentStart) + 
                                   lineText.Substring(commentEnd + 2);
                        commentStart = lineText.IndexOf("/*", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        lineText = lineText.Substring(0, commentStart);
                        inBlockComment = true;
                        break;
                    }
                }

                // 移除行内注释
                string codePart = StripLineComment(lineText);

                // 统计括号深度（跳过字符串内的括号）
                parenDepth += CountParenDepth(codePart);

                // GO 分隔行
                if (GoPattern.IsMatch(lines[i]))
                {
                    endLine = i - 1;
                    break;
                }

                // 在第一个行之后，检查是否有新的顶层关键词（depth==0 时）
                if (i > startLine && parenDepth <= 0)
                {
                    var match = TopLevelKeywordPattern.Match(lines[i]);
                    if (match.Success)
                    {
                        string keyword = match.Groups[1].Value.ToUpperInvariant();

                        // CTE 主查询：WITH...AS(...) 后的第一个 SELECT/INSERT/UPDATE/DELETE/MERGE
                        if (isCTE && !cteMainQuerySeen &&
                            (keyword == "SELECT" || keyword == "INSERT" ||
                             keyword == "UPDATE" || keyword == "DELETE" || keyword == "MERGE"))
                        {
                            cteMainQuerySeen = true;
                            endLine = i;
                            continue;
                        }

                        // 遇到新的顶层关键词（非 GO）→ 前一条语句到此结束
                        endLine = i - 1;
                        break;
                    }
                }

                endLine = i;

                // 光标之后再多扫描一些行，但如果已经超出合理范围则停止
                if (i > caretLine + 200)
                    break;
            }

            return Math.Min(endLine, lines.Length - 1);
        }

        /// <summary>
        /// 移除行内注释（-- 开头的单行注释）。
        /// </summary>
        private string StripLineComment(string lineText)
        {
            // 简单实现：找到 -- 且不在字符串内
            bool inString = false;
            for (int i = 0; i < lineText.Length - 1; i++)
            {
                char c = lineText[i];
                if (c == '\'') inString = !inString;
                if (!inString && c == '-' && lineText[i + 1] == '-')
                {
                    return lineText.Substring(0, i);
                }
            }
            return lineText;
        }

        /// <summary>
        /// 统计一行文本中的括号深度变化（跳过字符串内的括号）。
        /// </summary>
        private int CountParenDepth(string text)
        {
            int depth = 0;
            bool inString = false;

            foreach (char c in text)
            {
                if (c == '\'') inString = !inString;
                if (inString) continue;

                if (c == '(') depth++;
                else if (c == ')') depth--;
            }

            return depth;
        }
    }
}
