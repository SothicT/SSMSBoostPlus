using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// 别名映射信息。
    /// </summary>
    public class AliasInfo
    {
        /// <summary>别名</summary>
        public string Alias { get; set; } = "";

        /// <summary>物理表名（含 schema，如 dbo.Table 或 DB.dbo.Table）</summary>
        public string PhysicalTable { get; set; } = "";

        /// <summary>数据库名（跨库时有效，否则 null）</summary>
        public string? DatabaseName { get; set; }

        /// <summary>是否是临时表（#T1 或 @tableVariable）</summary>
        public bool IsTempTable { get; set; }
    }

    /// <summary>
    /// 裸表信息（无别名的 FROM/JOIN 表）。
    /// </summary>
    public class BareTableInfo
    {
        /// <summary>显示名（如表名本身）</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>物理表名（含 schema）</summary>
        public string PhysicalName { get; set; } = "";

        /// <summary>完整路径（含跨库前缀）</summary>
        public string FullPath { get; set; } = "";

        /// <summary>是否是临时表</summary>
        public bool IsTempTable { get; set; }
    }

    /// <summary>
    /// 子查询别名信息。
    /// </summary>
    public class SubqueryAliasInfo
    {
        /// <summary>子查询别名</summary>
        public string Alias { get; set; } = "";

        /// <summary>输出列名列表</summary>
        public List<string> Columns { get; set; } = new();

        /// <summary>是否 SELECT *</summary>
        public bool HasStarSelect => Columns.Contains("*");

        /// <summary>子查询内层 FROM 的表名（用于展开 SELECT *）</summary>
        public string? InnerFromTable { get; set; }
    }

    /// <summary>
    /// 语句解析结果。
    /// </summary>
    public class StatementParseResult
    {
        /// <summary>别名映射（别名 → 物理表信息）</summary>
        public Dictionary<string, AliasInfo> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>裸表集合（无别名的 FROM/JOIN 表）</summary>
        public List<BareTableInfo> BareTables { get; set; } = new();

        /// <summary>子查询别名集合</summary>
        public List<SubqueryAliasInfo> SubqueryAliases { get; set; } = new();
    }

    /// <summary>
    /// 语句解析器。从语句文本中解析别名映射、裸表集合、子查询别名等信息。
    /// 纯逻辑模块，不依赖 VS SDK / WPF，可直接单元测试。
    /// </summary>
    public class StatementParser
    {
        // ================================================================
        // 别名解析正则（强化版）
        // 覆盖格式：
        //   FROM table [AS] alias
        //   FROM [schema].[table] alias
        //   FROM [db].[schema].[table] alias
        //   FROM db.schema.table alias
        //   JOIN table alias ON ...
        //   INNER/LEFT/RIGHT/CROSS/FULL JOIN table alias
        //   FROM #TempTable alias
        //   FROM @TableVar alias
        // ================================================================
        private static readonly Regex AliasPattern = new Regex(
            @"\b(?:FROM|JOIN)\s+"                           // FROM 或 JOIN
            + @"((?:\[?\w+\]?\s*\.\s*)*)"                  // 可选的 [DB].[Schema]. 前缀（捕获组1）
            + @"\[?(#[\w]+|@\w+|\w+)\]?"                   // 表名（支持 #临时表、@表变量、普通表名）（捕获组2）
            + @"(?:\s+WITH\s*\([^)]*\))?"                  // 可选的 WITH(表提示)
            + @"(?:\s+AS)?\s+(?!(?:LEFT|RIGHT|INNER|OUTER|CROSS|FULL|JOIN)\b)(\w+)"  // 可选的 AS + 别名（排除 JOIN 修饰词）
            + @"(?:\s+WITH\s*\([^)]*\))?"                  // 可选的 WITH(表提示)在别名后
            + @"(?=\s+(?:ON|INNER|LEFT|RIGHT|CROSS|FULL|JOIN|WHERE|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT|,|\)|;)|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        // 裸表（无别名）检测正则
        private static readonly Regex BareTablePattern = new Regex(
            @"\b(?:FROM|JOIN)\s+"                           // FROM 或 JOIN
            + @"((?:\[?\w+\]?\s*\.\s*)*)"                  // 可选的跨库前缀（捕获组1）
            + @"\[?(#[\w]+|@\w+|\w+)\]?"                   // 表名（捕获组2）
            + @"(?:\s+WITH\s*\([^)]*\))?"                  // 可选的 WITH()
            + @"(?!\s+(?:AS\s+)?(?!(?:LEFT|RIGHT|INNER|OUTER|CROSS|FULL|JOIN)\b)\w+\s+(?:ON|INNER|LEFT|RIGHT|CROSS|FULL|JOIN|WHERE|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT))"  // 后面不是别名（排除 JOIN 修饰词）
            + @"(?=\s+(?:ON|WHERE|INNER|LEFT|RIGHT|CROSS|FULL|JOIN|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT|,|\)|;)|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        // 子查询别名检测：匹配 (SELECT ... ) alias 模式
        // 注意：Singleline 模式下 . 匹配换行
        private static readonly Regex SubqueryAliasPattern = new Regex(
            @"\(\s*SELECT\s+(.+?)\s+FROM\s+(.+?)\s*\)\s*(?:AS\s+)?(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        // 子查询输出列提取
        private static readonly Regex SelectColumnPattern = new Regex(
            @"(?:\[?(\w+)\]?\s*\.\s*)?\[?(\w+)\]?(?:\s+(?:AS\s+)?\[?(\w+)\]?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // UPDATE 目标表
        private static readonly Regex UpdateTargetPattern = new Regex(
            @"^\s*UPDATE\s+(?:TOP\s*\(\s*\d+\s*\)\s+)?((?:\[?\w+\]?\s*\.\s*)*)\[?(\w+)\]?(?:\s+(?:AS\s+)?(\w+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        // INSERT 目标表
        private static readonly Regex InsertTargetPattern = new Regex(
            @"^\s*INSERT\s+INTO\s+((?:\[?\w+\]?\s*\.\s*)*)\[?(\w+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        // ALTER TABLE 目标表
        private static readonly Regex AlterTableTargetPattern = new Regex(
            @"^\s*ALTER\s+TABLE\s+((?:\[?\w+\]?\s*\.\s*)*)\[?(\w+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// 解析语句中的所有别名映射、裸表集合和子查询别名。
        /// </summary>
        public StatementParseResult Parse(string statementText)
        {
            var result = new StatementParseResult();

            if (string.IsNullOrWhiteSpace(statementText))
                return result;

            // 1. 解析子查询别名 (SELECT ... ) L
            ParseSubqueryAliases(statementText, result);

            // 2. 解析显式别名（FROM/JOIN Table alias）
            ParseAliases(statementText, result);

            // 3. 解析裸表（无别名的 FROM/JOIN 表）
            ParseBareTables(statementText, result);

            // 4. 解析逗号分隔的表（FROM T1 A, T2 B, T3 C）
            ParseCommaSeparatedTables(statementText, result);

            return result;
        }

        /// <summary>
        /// 获取 UPDATE 语句的更新目标表。
        /// </summary>
        public (string tableName, string? alias)? GetUpdateTargetTable(string statementText)
        {
            if (string.IsNullOrWhiteSpace(statementText))
                return null;

            var match = UpdateTargetPattern.Match(statementText);
            if (!match.Success)
                return null;

            string prefix = match.Groups[1].Value.Trim().Replace("[", "").Replace("]", "").Replace(" ", "");
            string tableName = match.Groups[2].Value;
            string? alias = match.Groups[3].Success ? match.Groups[3].Value : null;

            string fullTable = string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;
            return (fullTable, alias);
        }

        /// <summary>
        /// 获取 INSERT 语句的目标表。
        /// </summary>
        public string? GetInsertTargetTable(string statementText)
        {
            if (string.IsNullOrWhiteSpace(statementText))
                return null;

            var match = InsertTargetPattern.Match(statementText);
            if (!match.Success)
                return null;

            string prefix = match.Groups[1].Value.Trim().Replace("[", "").Replace("]", "").Replace(" ", "");
            string tableName = match.Groups[2].Value;

            return string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;
        }

        /// <summary>
        /// 获取 ALTER TABLE 语句的目标表名。
        /// 支持 ALTER TABLE [schema].[table] / ALTER TABLE table 等格式。
        /// </summary>
        public string? GetAlterTableTargetTable(string statementText)
        {
            if (string.IsNullOrWhiteSpace(statementText))
                return null;

            var match = AlterTableTargetPattern.Match(statementText);
            if (!match.Success)
                return null;

            string prefix = match.Groups[1].Value.Trim().Replace("[", "").Replace("]", "").Replace(" ", "");
            string tableName = match.Groups[2].Value;

            return string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;
        }

        /// <summary>
        /// 获取 SELECT 语句中的所有列别名。
        /// </summary>
        public List<string> GetSelectColumnAliases(string statementText)
        {
            var aliases = new List<string>();
            if (string.IsNullOrWhiteSpace(statementText))
                return aliases;

            // 提取 SELECT 和 FROM 之间的列清单
            // US-02: 修复 TOP n（不带括号）被误识别为列别名的问题
            // 原: TOP\s*\([^)]*\)\s*  仅匹配 TOP (100)
            // 新: TOP\s*(?:\([^)]*\)|\d+)\s+  同时匹配 TOP (100) 和 TOP 100
            var selectMatch = Regex.Match(statementText, @"\bSELECT\b\s+(?:TOP\s*(?:\([^)]*\)|\d+)\s+)?(?:DISTINCT\s+)?(.*?)\s+FROM\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!selectMatch.Success)
                return aliases;

            string columnList = selectMatch.Groups[1].Value;

            // 按顶层逗号分割
            foreach (var part in SplitByTopLevelComma(columnList))
            {
                var trimmed = part.Trim();

                // 跳过 *
                if (trimmed == "*" || trimmed.EndsWith(".*"))
                    continue;

                // 匹配 [别名.]列名 [AS] 列别名
                var m = SelectColumnPattern.Match(trimmed);
                if (m.Success && m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value))
                {
                    // 有 AS 别名
                    aliases.Add(m.Groups[3].Value);
                }
                else if (m.Success && m.Groups[2].Success)
                {
                    // 无 AS，使用列名
                    // 如果是 表.列 格式，使用列名
                    string colName = m.Groups[2].Value;
                    // US-02: 跳过纯数字（如 TOP 100 中的 100 不应被当作列别名）
                    if (!string.IsNullOrEmpty(colName) && colName != "*" && !colName.All(char.IsDigit))
                        aliases.Add(colName);
                }
            }

            return aliases;
        }

        // ================================================================
        // 私有方法
        // ================================================================

        /// <summary>
        /// 解析显式别名。
        /// </summary>
        private void ParseAliases(string text, StatementParseResult result)
        {
            foreach (Match m in AliasPattern.Matches(text))
            {
                string prefix = m.Groups[1].Value;
                string tableName = m.Groups[2].Value;
                string alias = m.Groups[3].Value;

                // 清理 prefix
                prefix = prefix.Replace("[", "").Replace("]", "").Replace(" ", "");

                // 组合完整表名
                string fullTableName = string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;

                // 确保 schema.table 格式（如果没有点号，加 dbo.）
                if (!fullTableName.Contains('.') && !fullTableName.StartsWith("#") && !fullTableName.StartsWith("@"))
                    fullTableName = "dbo." + fullTableName;

                // 解析数据库名（如果有3段）
                string? dbName = null;
                var parts = fullTableName.Split('.');
                if (parts.Length >= 3)
                    dbName = parts[0];

                bool isTemp = tableName.StartsWith("#") || tableName.StartsWith("@");

                // 跳过已经被子查询别名捕获的（避免子查询的别名被误识别为表别名）
                if (result.SubqueryAliases.Any(sq => sq.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    continue;

                result.Aliases[alias] = new AliasInfo
                {
                    Alias = alias,
                    PhysicalTable = fullTableName,
                    DatabaseName = dbName,
                    IsTempTable = isTemp
                };
            }
        }

        /// <summary>
        /// 解析逗号分隔的表列表（FROM T1 A, T2 B, T3 C）。
        /// 仅处理 FROM 子句中逗号分隔的表，跳过包含 JOIN 的部分。
        /// </summary>
        private void ParseCommaSeparatedTables(string text, StatementParseResult result)
        {
            // 提取 FROM 子句：FROM ... (直到 WHERE/GROUP/ORDER/HAVING/UNION 或行尾)
            var fromMatch = Regex.Match(text,
                @"\bFROM\b\s+(.*?)(?=\bWHERE\b|\bGROUP\s+BY\b|\bORDER\s+BY\b|\bHAVING\b|\bUNION\b|\bEXCEPT\b|\bINTERSECT\b|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!fromMatch.Success) return;

            string fromClause = fromMatch.Groups[1].Value;

            // 按顶层逗号分割
            var parts = SplitByTopLevelComma(fromClause);

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // 跳过包含 JOIN 的部分（由 AliasPattern 处理）
                if (Regex.IsMatch(trimmed, @"\bJOIN\b", RegexOptions.IgnoreCase)) continue;

                // 跳过子查询
                if (trimmed.StartsWith("(")) continue;

                // 尝试匹配 表名 + 别名
                var aliasMatch = Regex.Match(trimmed,
                    @"^((?:\[?\w+\]?\s*\.\s*)*)\[?(#[\w]+|@\w+|\w+)\]?(?:\s+WITH\s*\([^)]*\))?(?:\s+AS)?\s+(\w+)$",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (aliasMatch.Success)
                {
                    string prefix = aliasMatch.Groups[1].Value.Replace("[", "").Replace("]", "").Replace(" ", "");
                    string tableName = aliasMatch.Groups[2].Value;
                    string alias = aliasMatch.Groups[3].Value;

                    // 跳过已捕获的别名
                    if (result.Aliases.ContainsKey(alias)) continue;
                    if (result.SubqueryAliases.Any(sq => sq.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string fullTableName = string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;
                    if (!fullTableName.Contains('.') && !fullTableName.StartsWith("#") && !fullTableName.StartsWith("@"))
                        fullTableName = "dbo." + fullTableName;

                    string? dbName = null;
                    var segParts = fullTableName.Split('.');
                    if (segParts.Length >= 3) dbName = segParts[0];

                    bool isTemp = tableName.StartsWith("#") || tableName.StartsWith("@");

                    result.Aliases[alias] = new AliasInfo
                    {
                        Alias = alias,
                        PhysicalTable = fullTableName,
                        DatabaseName = dbName,
                        IsTempTable = isTemp
                    };
                }
                else
                {
                    // 尝试匹配裸表（无别名）
                    var bareMatch = Regex.Match(trimmed,
                        @"^((?:\[?\w+\]?\s*\.\s*)*)\[?(#[\w]+|@\w+|\w+)\]?(?:\s+WITH\s*\([^)]*\))?$",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);

                    if (bareMatch.Success)
                    {
                        string prefix = bareMatch.Groups[1].Value.Replace("[", "").Replace("]", "").Replace(" ", "");
                        string tableName = bareMatch.Groups[2].Value;

                        string fullTableName = string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;
                        if (!fullTableName.Contains('.') && !fullTableName.StartsWith("#") && !fullTableName.StartsWith("@"))
                            fullTableName = "dbo." + fullTableName;

                        bool isTemp = tableName.StartsWith("#") || tableName.StartsWith("@");

                        if (!result.Aliases.Values.Any(a => a.PhysicalTable.Equals(fullTableName, StringComparison.OrdinalIgnoreCase)))
                        {
                            result.BareTables.Add(new BareTableInfo
                            {
                                DisplayName = tableName,
                                PhysicalName = fullTableName,
                                FullPath = fullTableName,
                                IsTempTable = isTemp
                            });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 解析裸表（无别名的表）。
        /// </summary>
        private void ParseBareTables(string text, StatementParseResult result)
        {
            foreach (Match m in BareTablePattern.Matches(text))
            {
                string prefix = m.Groups[1].Value;
                string tableName = m.Groups[2].Value;

                // 清理
                prefix = prefix.Replace("[", "").Replace("]", "").Replace(" ", "");
                string fullTableName = string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;

                if (!fullTableName.Contains('.') && !fullTableName.StartsWith("#") && !fullTableName.StartsWith("@"))
                    fullTableName = "dbo." + fullTableName;

                bool isTemp = tableName.StartsWith("#") || tableName.StartsWith("@");

                // 检查是否已经被别名捕获
                if (result.Aliases.Values.Any(a => a.PhysicalTable.Equals(fullTableName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // 检查是否是子查询的内部表（不在外层裸表中添加）
                if (result.SubqueryAliases.Any(sq => sq.InnerFromTable?.Equals(fullTableName, StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                result.BareTables.Add(new BareTableInfo
                {
                    DisplayName = tableName,
                    PhysicalName = fullTableName,
                    FullPath = fullTableName,
                    IsTempTable = isTemp
                });
            }
        }

        /// <summary>
        /// 解析子查询别名。
        /// </summary>
        private void ParseSubqueryAliases(string text, StatementParseResult result)
        {
            foreach (Match m in SubqueryAliasPattern.Matches(text))
            {
                string selectBody = m.Groups[1].Value.Trim();
                string fromBody = m.Groups[2].Value.Trim();
                string alias = m.Groups[3].Value;

                var columns = ExtractSelectColumns(selectBody);
                string? innerTable = ExtractInnerFromTable(fromBody);

                result.SubqueryAliases.Add(new SubqueryAliasInfo
                {
                    Alias = alias,
                    Columns = columns,
                    InnerFromTable = innerTable
                });
            }
        }

        /// <summary>
        /// 从子查询 SELECT 列表中提取输出列。
        /// </summary>
        private List<string> ExtractSelectColumns(string selectBody)
        {
            var columns = new List<string>();

            foreach (var part in SplitByTopLevelComma(selectBody))
            {
                var trimmed = part.Trim();

                if (string.IsNullOrEmpty(trimmed))
                    continue;

                if (trimmed == "*")
                {
                    columns.Add("*");
                    continue;
                }

                if (trimmed.EndsWith(".*"))
                {
                    // 别名.* → 暂时跳过
                    continue;
                }

                // 匹配 [别名.]列名 [AS] 别名
                var m = SelectColumnPattern.Match(trimmed);
                if (m.Success)
                {
                    // 优先使用 AS 别名（组3），否则使用列名（组2）
                    string colName = m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value)
                        ? m.Groups[3].Value
                        : m.Groups[2].Value;

                    if (!string.IsNullOrEmpty(colName) && colName != "*")
                        columns.Add(colName);
                }
            }

            return columns;
        }

        /// <summary>
        /// 从子查询的 FROM 子句中提取内层表名。
        /// </summary>
        private string? ExtractInnerFromTable(string fromBody)
        {
            // 简化处理：取 FROM 后第一个表名
            // 跳过 JOIN、ON、WHERE 等
            var match = Regex.Match(fromBody,
                @"^((?:\[?\w+\]?\s*\.\s*)*)\[?(#[\w]+|@\w+|\w+)\]?(?:\s+(?:WITH|AS|INNER|LEFT|RIGHT|CROSS|FULL|JOIN|ON|WHERE))?$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
                return null;

            string prefix = match.Groups[1].Value.Replace("[", "").Replace("]", "").Replace(" ", "");
            string tableName = match.Groups[2].Value;

            string full = string.IsNullOrEmpty(prefix) ? tableName : prefix + tableName;
            if (!full.Contains('.') && !full.StartsWith("#") && !full.StartsWith("@"))
                full = "dbo." + full;

            return full;
        }

        /// <summary>
        /// 按顶层逗号分割（跳过括号内和字符串内的逗号）。
        /// </summary>
        private List<string> SplitByTopLevelComma(string text)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            int parenDepth = 0;
            bool inString = false;

            foreach (char c in text)
            {
                if (c == '\'') inString = !inString;

                if (!inString)
                {
                    if (c == '(') parenDepth++;
                    else if (c == ')') parenDepth--;

                    if (c == ',' && parenDepth == 0)
                    {
                        parts.Add(sb.ToString());
                        sb.Clear();
                        continue;
                    }
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
                parts.Add(sb.ToString());

            return parts;
        }
    }
}
