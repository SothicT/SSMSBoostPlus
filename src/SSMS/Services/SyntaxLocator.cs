using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// 语法节点枚举（完整版，对应 PRD 2.2.4 映射表）。
    /// 每个枚举值对应一个 T-SQL 语法节点位置。
    /// </summary>
    public enum SyntaxNode
    {
        // ===== 通用 =====
        /// <summary>语句起始 / 语句后新语句</summary>
        StatementStart,
        /// <summary>表名位置（FROM/JOIN/INSERT INTO/UPDATE/DELETE）</summary>
        TableNamePosition,
        /// <summary>WHERE/HAVING/ON 条件区</summary>
        SearchConditionPosition,
        /// <summary>通用表达式位置</summary>
        ExpressionPosition,
        /// <summary>点号后（别名. 或 表名.）</summary>
        AfterDotNotation,
        /// <summary>跨库逐层引导</summary>
        CrossDbContextPrompt,
        /// <summary>注释/字符串内 → 不提示</summary>
        InCommentOrString,
        /// <summary>WITH(表提示) 括号内</summary>
        TableHintPosition,
        /// <summary>OPTION(查询提示) 括号内</summary>
        QueryOptionPosition,

        // ===== SELECT 特有 =====
        /// <summary>SELECT 后、列区前</summary>
        AfterSelectKeyword,
        /// <summary>SELECT 列区域（FROM 之前）</summary>
        SelectColumnArea,
        /// <summary>别名.* 点号后</summary>
        SelectAliasColumnAfterDot,
        /// <summary>INTO 后</summary>
        IntoNewTablePosition,
        /// <summary>FROM 后</summary>
        AfterFromKeyword,
        /// <summary>JOIN 后</summary>
        AfterJoinKeyword,
        /// <summary>ON 后（JOIN 条件）</summary>
        AfterJoinOnKeyword,
        /// <summary>WHERE 后</summary>
        AfterWhereKeyword,
        /// <summary>AND/OR 后（条件链）</summary>
        AfterAndOrKeyword,
        /// <summary>GROUP BY 后</summary>
        AfterGroupByKeyword,
        /// <summary>HAVING 后</summary>
        AfterHavingKeyword,
        /// <summary>ORDER BY 后</summary>
        AfterOrderByKeyword,
        /// <summary>OFFSET/FETCH 位置</summary>
        AfterOffsetFetchKeyword,
        /// <summary>FOR XML/JSON 位置</summary>
        ForOutputFormatPosition,
        /// <summary>UNION/EXCEPT/INTERSECT 后</summary>
        AfterSetOperationKeyword,
        /// <summary>TOP(n) 括号内</summary>
        AfterTopKeyword,

        // ===== INSERT 特有 =====
        /// <summary>INSERT INTO 后（表名）</summary>
        InsertIntoTablePosition,
        /// <summary>INSERT(列名列表) 括号内</summary>
        InsertColumnListPosition,
        /// <summary>VALUES 括号内</summary>
        InsertValuesPosition,
        /// <summary>表名确认后（触发展开）</summary>
        InsertTableAfterInsertInto,

        // ===== UPDATE 特有 =====
        /// <summary>UPDATE 后（表名）</summary>
        UpdateTablePosition,
        /// <summary>SET 后</summary>
        UpdateSetPosition,
        /// <summary>SET 列=后</summary>
        UpdateSetColumnAssignment,
        /// <summary>UPDATE 附加 FROM</summary>
        UpdateFromTablePosition,

        // ===== DELETE 特有 =====
        /// <summary>DELETE 后（表名）</summary>
        DeleteTablePosition,
        /// <summary>DELETE 附加 FROM</summary>
        DeleteFromTablePosition,

        // ===== EXEC 特有 =====
        /// <summary>EXEC 后</summary>
        ExecPosition,
        /// <summary>EXEC 参数位置</summary>
        ExecParameterPosition,

        // ===== DDL: CREATE 系列 =====
        /// <summary>CREATE 后（提示对象类型）</summary>
        AfterCreateKeyword,
        /// <summary>CREATE TABLE 后（提示表名）</summary>
        AfterCreateTableKeyword,
        /// <summary>CREATE TABLE 括号内列定义位置（提示数据类型）</summary>
        ColumnDefinitionPosition,
        /// <summary>列定义后（提示列级约束）</summary>
        ColumnConstraintPosition,
        /// <summary>逗号后（提示新列或表级约束）</summary>
        TableConstraintPosition,
        /// <summary>CONSTRAINT 后（提示约束名，自由文本）</summary>
        ConstraintNamePosition,
        /// <summary>约束名后（提示约束类型）</summary>
        ConstraintTypePosition,
        /// <summary>约束括号内（提示列名）</summary>
        ConstraintColumnsPosition,
        /// <summary>REFERENCES 后（提示被引用表名）</summary>
        AfterForeignKeyReferences,
        /// <summary>REFERENCES 表名( 后（提示被引用表列名）</summary>
        AfterForeignKeyRefColumns,
        /// <summary>外键末尾（提示 ON DELETE/UPDATE 行为）</summary>
        OnDeleteUpdatePosition,
        /// <summary>CREATE INDEX 后（提示索引名）</summary>
        AfterCreateIndexKeyword,
        /// <summary>CREATE INDEX ON 后（提示表名）</summary>
        AfterCreateIndexOnKeyword,
        /// <summary>索引列位置（提示列名+ASC/DESC）</summary>
        IndexColumnPosition,
        /// <summary>索引选项位置（提示 INCLUDE/WHERE/WITH）</summary>
        IndexOptionsPosition,
        /// <summary>CREATE VIEW 后（提示视图名）</summary>
        AfterCreateViewKeyword,
        /// <summary>VIEW AS 后（提示 SELECT）</summary>
        AfterViewAsKeyword,
        /// <summary>CREATE PROCEDURE 后（提示过程名）</summary>
        AfterCreateProcKeyword,
        /// <summary>过程参数名后（提示数据类型）</summary>
        AfterProcParamNamePosition,
        /// <summary>AS 后（提示过程体起始关键词）</summary>
        AfterProcAsKeyword,
        /// <summary>CREATE FUNCTION 后（提示函数名）</summary>
        AfterCreateFuncKeyword,
        /// <summary>CREATE TRIGGER 后（提示触发器名）</summary>
        AfterCreateTriggerKeyword,
        /// <summary>TRIGGER ON 后（提示表名）</summary>
        AfterTriggerOnKeyword,

        // ===== DDL: ALTER 系列 =====
        /// <summary>ALTER 后（提示对象类型）</summary>
        AfterAlterKeyword,
        /// <summary>ALTER TABLE 后（提示表名）</summary>
        AfterAlterTableKeyword,
        /// <summary>ALTER TABLE 表名后（提示操作类型）</summary>
        AfterAlterTableAction,
        /// <summary>ALTER TABLE ADD 后（提示列名或CONSTRAINT）</summary>
        AfterAlterTableAddKeyword,
        /// <summary>ALTER TABLE ADD 列名后（提示数据类型）</summary>
        AfterAlterTableAddColumnType,
        /// <summary>DROP COLUMN 后（提示列名）</summary>
        AfterDropColumnKeyword,
        /// <summary>DROP CONSTRAINT 后（提示约束名）</summary>
        AfterDropConstraintKeyword,
        /// <summary>ALTER COLUMN 后（提示列名）</summary>
        AfterAlterTableAlterColumnKeyword,
        /// <summary>ALTER COLUMN 列名后（提示数据类型）</summary>
        AfterAlterColumnTypePosition,
        /// <summary>ALTER INDEX 后（提示索引名）</summary>
        AfterAlterIndexKeyword,
        /// <summary>ALTER INDEX ON 后（提示表名）</summary>
        AfterAlterIndexOnKeyword,
        /// <summary>ALTER INDEX 表名后（提示操作）</summary>
        AfterAlterIndexAction,

        // ===== DDL: DROP 系列 =====
        /// <summary>DROP 后（提示对象类型）</summary>
        AfterDropKeyword,
        /// <summary>DROP TABLE/VIEW 等后（提示对象名）</summary>
        AfterDropObjectKeyword,
        /// <summary>DROP INDEX 后（提示索引名）</summary>
        AfterDropIndexKeyword,

        // ===== DDL: TRUNCATE 系列 =====
        /// <summary>TRUNCATE 后（提示 TABLE）</summary>
        AfterTruncateKeyword,
        /// <summary>TRUNCATE TABLE 后（提示表名）</summary>
        AfterTruncateTableKeyword,

        // ===== 数据类型 =====
        /// <summary>需要数据类型提示的位置（DECLARE @var 后等）</summary>
        DataTypePosition,
    }

    /// <summary>
    /// 语法位置定位结果。
    /// </summary>
    public class SyntaxLocationResult
    {
        /// <summary>语法节点</summary>
        public SyntaxNode Node { get; set; }

        /// <summary>嵌套深度（0=主语句，1+=子查询/函数参数）</summary>
        public int Depth { get; set; }

        /// <summary>点号前的词（如别名/表名/库名），仅当 Node=AfterDotNotation 或 CrossDbContextPrompt 时有效</summary>
        public string? WordBeforeDot { get; set; }

        /// <summary>跨库引导层级：0=提示DB，1=提示Schema，2=提示Table</summary>
        public int CrossDbLevel { get; set; }

        /// <summary>跨库名称（已输入的 DB 名）</summary>
        public string? CrossDbName { get; set; }

        /// <summary>跨库 Schema 名称（已输入的 Schema 名）</summary>
        public string? CrossSchemaName { get; set; }
    }

    /// <summary>
    /// 语法定位器。在语句文本内，根据光标位置定位到具体的语法节点。
    /// 纯逻辑模块，不依赖 VS SDK / WPF，可直接单元测试。
    /// </summary>
    public class SyntaxLocator
    {
        /// <summary>
        /// SQL 关键词模式（按优先级排序，多词关键词优先）。
        /// </summary>
        private static readonly Regex KeywordPattern = new Regex(
            @"\b(GROUP\s+BY|ORDER\s+BY|INSERT\s+INTO|INNER\s+JOIN|LEFT\s+(?:OUTER\s+)?JOIN|RIGHT\s+(?:OUTER\s+)?JOIN|" +
            @"FULL\s+(?:OUTER\s+)?JOIN|CROSS\s+JOIN|CROSS\s+APPLY|OUTER\s+APPLY|" +
            @"UNION\s+ALL|SELECT|FROM|WHERE|AND|OR|NOT|IN|BETWEEN|LIKE|IS|AS|ON|" +
            @"INSERT|INTO|VALUES|UPDATE|SET|DELETE|MERGE|" +
            @"JOIN|INNER|LEFT|RIGHT|OUTER|CROSS|FULL|" +
            @"GROUP|ORDER|HAVING|TOP|DISTINCT|" +
            @"UNION|INTERSECT|EXCEPT|ALL|" +
            @"OFFSET|FETCH|NEXT|ONLY|ROWS|" +
            @"FOR|XML|JSON|PATH|AUTO|RAW|ROOT|" +
            @"OPTION|RECOMPILE|" +
            @"EXEC|EXECUTE|" +
            @"WITH|NOLOCK|ROWLOCK|TABLOCK|TABLOCKX|HOLDLOCK|UPDLOCK|XLOCK|READPAST|" +
            @"CASE|WHEN|THEN|ELSE|END|" +
            // ===== DDL 关键词（US-04）=====
            @"CREATE|ALTER|DROP|TRUNCATE|" +
            @"TABLE|INDEX|VIEW|PROCEDURE|FUNCTION|TRIGGER|" +
            @"CONSTRAINT|PRIMARY|FOREIGN|REFERENCES|CHECK|" +
            @"ADD|COLUMN|UNIQUE|CLUSTERED|NONCLUSTERED|CASCADE|" +
            // ===== 数据类型触发词（US-05）=====
            @"DECLARE|CAST|CONVERT|RETURNS|" +
            // ===== DDL 辅助词 ==========
            @"AFTER|INSTEAD|REBUILD|REORGANIZE|DISABLE|IDENTITY|" +
            @"NULL)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 定位光标在语句中的语法节点。
        /// </summary>
        /// <param name="statementText">完整语句文本</param>
        /// <param name="cursorOffset">光标在语句文本中的字符偏移（0-based）</param>
        /// <param name="statementType">语句类型</param>
        /// <returns>语法位置定位结果</returns>
        public SyntaxLocationResult Locate(string statementText, int cursorOffset, StatementType statementType)
        {
            var result = new SyntaxLocationResult { Node = SyntaxNode.ExpressionPosition, Depth = 0 };

            if (string.IsNullOrEmpty(statementText))
            {
                result.Node = SyntaxNode.StatementStart;
                return result;
            }

            // 确保光标在有效范围内
            cursorOffset = Math.Max(0, Math.Min(cursorOffset, statementText.Length));

            // Step 0: 检查是否在注释/字符串内
            if (IsInCommentOrString(statementText, cursorOffset))
            {
                result.Node = SyntaxNode.InCommentOrString;
                return result;
            }

            // Step 1: 检查点号触发（优先级最高）
            var dotResult = CheckDotNotation(statementText, cursorOffset);
            if (dotResult != null)
            {
                return dotResult;
            }

            // Step 2: 提取光标前的关键词序列
            var keywords = ExtractKeywordsOrdered(statementText);

            // Step 3: 找到光标前最近的关键词
            KeywordInfo? prevKeyword = null;
            foreach (var kw in keywords)
            {
                if (kw.EndPos <= cursorOffset)
                {
                    // 如果光标紧贴关键词之后（如 "SELECT " 中的空格位置紧跟 SELECT）
                    prevKeyword = kw;
                }
                else if (kw.StartPos <= cursorOffset)
                {
                    // 光标在关键词内部 → 不提示（用户正在输入关键词）
                    result.Node = SyntaxNode.ExpressionPosition;
                    return result;
                }
                else
                {
                    break;
                }
            }

            // Step 4: 计算嵌套深度（光标位置的括号栈深度）
            result.Depth = CalculateParenDepth(statementText, cursorOffset);

            // Step 5: 根据前一个关键词判定语法节点
            if (prevKeyword == null)
            {
                result.Node = SyntaxNode.StatementStart;
                return result;
            }

            result.Node = ResolveNode(prevKeyword.Word, statementText, cursorOffset, prevKeyword, statementType, result.Depth);
            return result;
        }

        /// <summary>
        /// 检查光标是否在注释或字符串内。
        /// </summary>
        private bool IsInCommentOrString(string text, int offset)
        {
            bool inString = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < offset && i < text.Length; i++)
            {
                char c = text[i];

                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++; // skip /
                    }
                    continue;
                }

                if (inString)
                {
                    if (c == '\'') inString = false;
                    continue;
                }

                if (c == '\'') { inString = true; continue; }
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
            }

            return inString || inLineComment || inBlockComment;
        }

        /// <summary>
        /// 检查点号触发（别名. 或 表名. 或 DB.Schema.Table. 模式）。
        /// </summary>
        private SyntaxLocationResult? CheckDotNotation(string text, int offset)
        {
            if (offset <= 0) return null;

            // 从光标向前找最近的点号
            int dotPos = -1;
            for (int i = offset - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '.')
                {
                    dotPos = i;
                    break;
                }
                // 遇到空格、换行、分号、逗号、括号、方括号等停止
                if (c == ' ' || c == '\n' || c == '\r' || c == ';' || c == '(' || c == ')' || c == ','
                    || c == '[' || c == ']')
                    break;
            }

            if (dotPos < 0) return null;

            // ★ 验证点号与光标之间只有空白字符
            // 如果存在非空白字符（如完整的方括号标识符 [TableName]），说明点号不构成点号触发
            for (int i = dotPos + 1; i < offset; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                    return null; // 点号与光标之间有非空白字符 → 不是有效的点号触发
            }

            // 提取点号前的词
            int wordEnd = dotPos - 1;
            // 跳过方括号
            if (wordEnd >= 0 && text[wordEnd] == ']')
            {
                int bracketStart = wordEnd - 1;
                while (bracketStart >= 0 && text[bracketStart] != '[')
                    bracketStart--;
                if (bracketStart >= 0)
                {
                    string wordInBrackets = text.Substring(bracketStart + 1, wordEnd - bracketStart - 1);
                    return CheckCrossDbOrDot(text, dotPos, wordInBrackets, offset, bracketStart, true);
                }
            }

            // 提取普通单词
            int wordStart = wordEnd;
            while (wordStart >= 0 && IsWordChar(text[wordStart]))
                wordStart--;
            wordStart++;

            if (wordStart > wordEnd) return null;

            string wordBeforeDot = text.Substring(wordStart, wordEnd - wordStart + 1);
            return CheckCrossDbOrDot(text, dotPos, wordBeforeDot, offset, wordStart, false);
        }

        /// <summary>
        /// 判断点号前是别名/表名（→ AfterDotNotation）还是数据库名（→ CrossDbContextPrompt）。
        /// </summary>
        /// <param name="wasBracketed">点号前的词是否被方括号包裹（如 [MESDATA]）</param>
        private SyntaxLocationResult CheckCrossDbOrDot(string text, int dotPos, string wordBeforeDot,
            int cursorOffset, int wordStart, bool wasBracketed)
        {
            if (string.IsNullOrEmpty(wordBeforeDot))
                return null;

            // 检查点号前是否还有更多点号（跨库模式：DB.Schema.Table.）
            int dotCount = 0;
            int checkPos = wordStart - 1;

            // 向前数连续的点号段
            while (checkPos >= 0)
            {
                // 跳过空白
                while (checkPos >= 0 && (text[checkPos] == ' ' || text[checkPos] == '\t'))
                    checkPos--;
                if (checkPos < 0) break;

                // 检查是否是点号分隔符（段与段之间的点）
                if (text[checkPos] == '.')
                {
                    dotCount++;
                    checkPos--;
                    continue;
                }

                // 检查是否是方括号
                if (text[checkPos] == ']')
                {
                    int bs = checkPos - 1;
                    while (bs >= 0 && text[bs] != '[')
                        bs--;
                    if (bs < 0) break;
                    checkPos = bs - 1;
                    // 检查前面是否有点号
                    while (checkPos >= 0 && (text[checkPos] == ' ' || text[checkPos] == '\t'))
                        checkPos--;
                    if (checkPos >= 0 && text[checkPos] == '.')
                    {
                        dotCount++;
                        checkPos--;
                        continue;
                    }
                    break;
                }

                // 检查是否是普通字符
                if (IsWordChar(text[checkPos]))
                {
                    int ws = checkPos;
                    while (ws >= 0 && IsWordChar(text[ws]))
                        ws--;
                    checkPos = ws;
                    // 检查前面是否有点号
                    while (checkPos >= 0 && (text[checkPos] == ' ' || text[checkPos] == '\t'))
                        checkPos--;
                    if (checkPos >= 0 && text[checkPos] == '.')
                    {
                        dotCount++;
                        checkPos--;
                        continue;
                    }
                    break;
                }

                break;
            }

            // dotCount 表示当前词之前的点号数量：
            //   0 → DB. (level 1) 或 alias. (AfterDotNotation)
            //   1 → DB.Schema. (level 2)
            //   2 → DB.Schema.Table. (level 3 = AfterDotNotation, field candidates)

            if (dotCount >= 2)
            {
                // DB.Schema.Table. → 字段候选
                return new SyntaxLocationResult
                {
                    Node = SyntaxNode.AfterDotNotation,
                    WordBeforeDot = wordBeforeDot,
                    Depth = 0
                };
            }

            if (dotCount >= 1)
            {
                // 跨库模式：DB.Schema. → 提示 Table（level 2）
                string? dbName = null;
                string? schemaName = null;
                var parts = ExtractCrossDbParts(text, dotPos);

                if (parts.Count >= 1) dbName = parts[0];
                if (parts.Count >= 2) schemaName = parts[1];

                return new SyntaxLocationResult
                {
                    Node = SyntaxNode.CrossDbContextPrompt,
                    CrossDbLevel = dotCount + 1,
                    CrossDbName = dbName,
                    CrossSchemaName = schemaName,
                    WordBeforeDot = wordBeforeDot,
                    Depth = 0
                };
            }

            // dotCount == 0：可能是 DB. 或 alias.
            if (wasBracketed)
            {
                // 方括号包裹的词在点号前 → 视为数据库名（如 [MESDATA].）
                return new SyntaxLocationResult
                {
                    Node = SyntaxNode.CrossDbContextPrompt,
                    CrossDbLevel = 1,
                    CrossDbName = wordBeforeDot,
                    WordBeforeDot = wordBeforeDot,
                    Depth = 0
                };
            }

            // 普通点号触发（别名. 或 表名.）
            return new SyntaxLocationResult
            {
                Node = SyntaxNode.AfterDotNotation,
                WordBeforeDot = wordBeforeDot,
                Depth = 0
            };
        }

        /// <summary>
        /// 从点号位置向前提取跨库各段名称。
        /// </summary>
        private List<string> ExtractCrossDbParts(string text, int dotPos)
        {
            var parts = new List<string>();
            int pos = dotPos - 1;

            while (pos >= 0)
            {
                // 跳过空白
                while (pos >= 0 && (text[pos] == ' ' || text[pos] == '\t'))
                    pos--;
                if (pos < 0) break;

                string segment;
                if (text[pos] == ']')
                {
                    int bs = pos - 1;
                    while (bs >= 0 && text[bs] != '[')
                        bs--;
                    if (bs < 0) break;
                    segment = text.Substring(bs + 1, pos - bs - 1);
                    pos = bs - 1;
                }
                else if (IsWordChar(text[pos]))
                {
                    int ws = pos;
                    while (ws >= 0 && IsWordChar(text[ws]))
                        ws--;
                    segment = text.Substring(ws + 1, pos - ws);
                    pos = ws;
                }
                else
                {
                    break;
                }

                parts.Insert(0, segment);

                // 检查前面是否有点号
                while (pos >= 0 && (text[pos] == ' ' || text[pos] == '\t'))
                    pos--;
                if (pos >= 0 && text[pos] == '.')
                {
                    pos--;
                    continue;
                }
                break;
            }

            return parts;
        }

        /// <summary>
        /// 提取语句中的关键词序列（按位置排序）。
        /// </summary>
        private List<KeywordInfo> ExtractKeywordsOrdered(string text)
        {
            var result = new List<KeywordInfo>();

            // 跳过字符串和注释内的关键词
            bool inString = false;
            bool inLineComment = false;
            bool inBlockComment = false;
            int parenDepth = 0;

            foreach (Match m in KeywordPattern.Matches(text))
            {
                // 检查是否在字符串/注释内
                int pos = m.Index;
                if (IsPositionInCommentOrString(text, pos))
                    continue;

                result.Add(new KeywordInfo
                {
                    Word = Regex.Replace(m.Value, @"\s+", " ").ToUpperInvariant(),
                    StartPos = m.Index,
                    EndPos = m.Index + m.Length
                });
            }

            return result;
        }

        /// <summary>
        /// 检查指定位置是否在注释或字符串内。
        /// </summary>
        private bool IsPositionInCommentOrString(string text, int offset)
        {
            bool inString = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < offset; i++)
            {
                char c = text[i];

                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }
                if (inString)
                {
                    if (c == '\'') inString = false;
                    continue;
                }

                if (c == '\'') { inString = true; continue; }
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
            }

            return inString || inLineComment || inBlockComment;
        }

        /// <summary>
        /// 计算指定位置的括号深度。
        /// </summary>
        private int CalculateParenDepth(string text, int offset)
        {
            int depth = 0;
            bool inString = false;

            for (int i = 0; i < offset && i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\'') inString = !inString;
                if (inString) continue;

                if (c == '(') depth++;
                else if (c == ')') depth--;
            }

            return Math.Max(0, depth);
        }

        /// <summary>
        /// 根据前一个关键词判定语法节点。
        /// </summary>
        private SyntaxNode ResolveNode(string prevKeyword, string text, int cursorOffset,
            KeywordInfo prevKw, StatementType stmtType, int depth)
        {
            // 检查光标是否紧跟在关键词后面（即关键词 + 空格 + 光标）
            bool immediatelyAfter = IsImmediatelyAfterKeyword(text, cursorOffset, prevKw);

            switch (prevKeyword)
            {
                // ===== SELECT 系列 =====
                case "SELECT":
                    if (immediatelyAfter)
                        return SyntaxNode.AfterSelectKeyword;
                    // 检查是否有 TOP ( → 在 TOP 括号内
                    return SyntaxNode.SelectColumnArea;

                case "TOP":
                    return SyntaxNode.AfterTopKeyword;

                case "DISTINCT":
                case "ALL":
                    // DISTINCT/ALL 后面还是列区
                    return SyntaxNode.SelectColumnArea;

                case "FROM":
                    if (immediatelyAfter)
                        return SyntaxNode.AfterFromKeyword;
                    // 检查是否在 WITH( 表提示内
                    if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                        return SyntaxNode.TableHintPosition;
                    return SyntaxNode.TableNamePosition;

                case "JOIN":
                case "INNER JOIN":
                case "LEFT JOIN":
                case "LEFT OUTER JOIN":
                case "RIGHT JOIN":
                case "RIGHT OUTER JOIN":
                case "FULL JOIN":
                case "FULL OUTER JOIN":
                case "CROSS JOIN":
                    if (immediatelyAfter)
                        return SyntaxNode.AfterJoinKeyword;
                    // 检查是否在 ON 之后
                    if (HasKeywordBetween(text, "ON", prevKw.EndPos, cursorOffset))
                        return SyntaxNode.AfterJoinOnKeyword;
                    if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                        return SyntaxNode.TableHintPosition;
                    return SyntaxNode.TableNamePosition;

                // 单独的 INNER/LEFT/RIGHT/CROSS/FULL 关键词（后面未跟 JOIN）
                case "INNER":
                case "LEFT":
                case "RIGHT":
                case "CROSS":
                case "FULL":
                case "OUTER":
                    return SyntaxNode.ExpressionPosition;

                case "CROSS APPLY":
                case "OUTER APPLY":
                    if (immediatelyAfter)
                        return SyntaxNode.AfterJoinKeyword;
                    return SyntaxNode.TableNamePosition;

                case "ON":
                    return SyntaxNode.AfterJoinOnKeyword;

                case "WHERE":
                    return SyntaxNode.AfterWhereKeyword;

                case "AND":
                case "OR":
                    return SyntaxNode.AfterAndOrKeyword;

                case "HAVING":
                    return SyntaxNode.AfterHavingKeyword;

                case "GROUP BY":
                    return SyntaxNode.AfterGroupByKeyword;

                case "ORDER":
                case "ORDER BY":
                    return SyntaxNode.AfterOrderByKeyword;

                case "OFFSET":
                case "FETCH":
                case "NEXT":
                case "ONLY":
                    return SyntaxNode.AfterOffsetFetchKeyword;

                case "FOR":
                    return SyntaxNode.ForOutputFormatPosition;

                case "UNION":
                case "UNION ALL":
                case "INTERSECT":
                case "EXCEPT":
                    return SyntaxNode.AfterSetOperationKeyword;

                // ===== INSERT 系列 =====
                case "INSERT":
                    if (immediatelyAfter)
                        return SyntaxNode.InsertIntoTablePosition;
                    // 检查是否后面跟了 INTO
                    if (HasKeywordBetween(text, "INTO", prevKw.EndPos, cursorOffset))
                    {
                        // 找到 INTO 的位置
                        var intoKw = FindKeyword(text, "INTO", prevKw.EndPos);
                        if (intoKw != null)
                        {
                            // 表名确认后（INTO 表名 后面没有 ( 或 VALUES）
                            if (IsImmediatelyAfterKeyword(text, cursorOffset, intoKw))
                                return SyntaxNode.InsertIntoTablePosition;
                            // 检查是否已有表名 → 触发展开
                            return SyntaxNode.InsertTableAfterInsertInto;
                        }
                    }
                    return SyntaxNode.InsertIntoTablePosition;

                case "INSERT INTO":
                    if (immediatelyAfter)
                        return SyntaxNode.InsertIntoTablePosition;
                    // 检查光标是否在 ( 列名列表 ) 内
                    if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                        return SyntaxNode.InsertColumnListPosition;
                    // 表名已输入，检查是否需要展开
                    return SyntaxNode.InsertTableAfterInsertInto;

                case "INTO":
                    if (immediatelyAfter)
                        return SyntaxNode.InsertIntoTablePosition;
                    // 检查光标是否在 ( 列名列表 ) 内
                    if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                        return SyntaxNode.InsertColumnListPosition;
                    // 表名已输入，检查是否需要展开
                    return SyntaxNode.InsertTableAfterInsertInto;

                case "VALUES":
                    return SyntaxNode.InsertValuesPosition;

                // ===== UPDATE 系列 =====
                case "UPDATE":
                    if (immediatelyAfter)
                        return SyntaxNode.UpdateTablePosition;
                    // 检查是否已有表名 → 应该提示 SET
                    return SyntaxNode.TableNamePosition;

                case "SET":
                    // 检查是否在 = 赋值之后
                    if (HasCharBetween(text, '=', prevKw.EndPos, cursorOffset))
                        return SyntaxNode.UpdateSetColumnAssignment;
                    // 检查是否在 UPDATE 的 FROM 之后
                    if (HasKeywordBetween(text, "FROM", prevKw.EndPos, cursorOffset))
                        return SyntaxNode.UpdateFromTablePosition;
                    return SyntaxNode.UpdateSetPosition;

                // ===== DELETE 系列 =====
                case "DELETE":
                    if (immediatelyAfter)
                        return SyntaxNode.DeleteTablePosition;
                    return SyntaxNode.TableNamePosition;

                // ===== EXEC 系列 =====
                case "EXEC":
                case "EXECUTE":
                    if (immediatelyAfter)
                        return SyntaxNode.ExecPosition;
                    return SyntaxNode.ExecParameterPosition;

                // ===== OPTION =====
                case "OPTION":
                    return SyntaxNode.QueryOptionPosition;

                // ===== WITH (表提示或 CTE) =====
                case "WITH":
                    // 检查是否在括号内（表提示）
                    if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                        return SyntaxNode.TableHintPosition;
                    return SyntaxNode.ExpressionPosition;

                // ===== 条件表达式关键词 =====
                case "BETWEEN":
                case "LIKE":
                case "IN":
                case "IS":
                case "NOT":
                    return SyntaxNode.ExpressionPosition;

                // ===== CASE 表达式 =====
                case "CASE":
                case "WHEN":
                case "THEN":
                case "ELSE":
                case "END":
                    return SyntaxNode.ExpressionPosition;

                // ===== DDL: CREATE/ALTER/DROP/TRUNCATE 系列（US-04）=====
                case "CREATE":
                    return SyntaxNode.AfterCreateKeyword;

                case "ALTER":
                    return SyntaxNode.AfterAlterKeyword;

                case "DROP":
                    // ALTER TABLE ... DROP COLUMN/CONSTRAINT
                    if (HasKeywordBetween(text, "ALTER", 0, prevKw.StartPos))
                    {
                        if (HasKeywordBetween(text, "COLUMN", prevKw.EndPos, cursorOffset))
                            return SyntaxNode.AfterDropColumnKeyword;
                        return SyntaxNode.AfterDropConstraintKeyword;
                    }
                    return SyntaxNode.AfterDropKeyword;

                case "TRUNCATE":
                    if (immediatelyAfter)
                        return SyntaxNode.AfterTruncateKeyword;
                    // TRUNCATE TABLE → table name
                    if (HasKeywordBetween(text, "TABLE", prevKw.EndPos, cursorOffset))
                        return SyntaxNode.AfterTruncateTableKeyword;
                    return SyntaxNode.AfterTruncateKeyword;

                case "TABLE":
                    if (stmtType == StatementType.DDL)
                    {
                        if (HasKeywordBetween(text, "CREATE", 0, prevKw.StartPos))
                        {
                            if (immediatelyAfter)
                                return SyntaxNode.AfterCreateTableKeyword;
                            // CREATE TABLE 内括号中 → 列定义
                            if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                                return ResolveColumnDefinitionPosition(text, cursorOffset);
                            return SyntaxNode.AfterCreateTableKeyword;
                        }
                        if (HasKeywordBetween(text, "ALTER", 0, prevKw.StartPos))
                        {
                            if (immediatelyAfter)
                                return SyntaxNode.AfterAlterTableKeyword;
                            return SyntaxNode.AfterAlterTableAction;
                        }
                        if (HasKeywordBetween(text, "DROP", 0, prevKw.StartPos))
                            return SyntaxNode.AfterDropObjectKeyword;
                        if (HasKeywordBetween(text, "TRUNCATE", 0, prevKw.StartPos))
                            return SyntaxNode.AfterTruncateTableKeyword;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "INDEX":
                    if (stmtType == StatementType.DDL)
                    {
                        if (HasKeywordBetween(text, "CREATE", 0, prevKw.StartPos))
                        {
                            if (immediatelyAfter)
                                return SyntaxNode.AfterCreateIndexKeyword;
                            if (HasKeywordBetween(text, "ON", prevKw.EndPos, cursorOffset))
                                return SyntaxNode.AfterCreateIndexOnKeyword;
                            return SyntaxNode.AfterCreateIndexKeyword;
                        }
                        if (HasKeywordBetween(text, "ALTER", 0, prevKw.StartPos))
                        {
                            if (immediatelyAfter)
                                return SyntaxNode.AfterAlterIndexKeyword;
                            if (HasKeywordBetween(text, "ON", prevKw.EndPos, cursorOffset))
                                return SyntaxNode.AfterAlterIndexOnKeyword;
                            return SyntaxNode.AfterAlterIndexAction;
                        }
                        if (HasKeywordBetween(text, "DROP", 0, prevKw.StartPos))
                            return SyntaxNode.AfterDropIndexKeyword;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "VIEW":
                    if (stmtType == StatementType.DDL && HasKeywordBetween(text, "CREATE", 0, prevKw.StartPos))
                        return SyntaxNode.AfterCreateViewKeyword;
                    return SyntaxNode.ExpressionPosition;

                case "PROCEDURE":
                    if (stmtType == StatementType.DDL && HasKeywordBetween(text, "CREATE", 0, prevKw.StartPos))
                        return SyntaxNode.AfterCreateProcKeyword;
                    return SyntaxNode.ExpressionPosition;

                case "FUNCTION":
                    if (stmtType == StatementType.DDL && HasKeywordBetween(text, "CREATE", 0, prevKw.StartPos))
                        return SyntaxNode.AfterCreateFuncKeyword;
                    return SyntaxNode.ExpressionPosition;

                case "TRIGGER":
                    if (stmtType == StatementType.DDL && HasKeywordBetween(text, "CREATE", 0, prevKw.StartPos))
                    {
                        if (immediatelyAfter)
                            return SyntaxNode.AfterCreateTriggerKeyword;
                        if (HasKeywordBetween(text, "ON", prevKw.EndPos, cursorOffset))
                            return SyntaxNode.AfterTriggerOnKeyword;
                        return SyntaxNode.AfterCreateTriggerKeyword;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "ADD":
                    if (stmtType == StatementType.DDL && HasKeywordBetween(text, "ALTER", 0, prevKw.StartPos))
                    {
                        if (immediatelyAfter)
                            return SyntaxNode.AfterAlterTableAddKeyword;
                        // ADD 列名 → 数据类型位置
                        return SyntaxNode.AfterAlterTableAddColumnType;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "CONSTRAINT":
                    if (stmtType == StatementType.DDL)
                    {
                        if (immediatelyAfter)
                            return SyntaxNode.ConstraintNamePosition;
                        return SyntaxNode.ConstraintTypePosition;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "REFERENCES":
                    if (stmtType == StatementType.DDL)
                    {
                        if (immediatelyAfter)
                            return SyntaxNode.AfterForeignKeyReferences;
                        // REFERENCES 表名( → 列名
                        if (IsInsideParen(text, cursorOffset, prevKw.EndPos))
                            return SyntaxNode.AfterForeignKeyRefColumns;
                        return SyntaxNode.AfterForeignKeyReferences;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "COLUMN":
                    if (stmtType == StatementType.DDL)
                    {
                        if (HasKeywordBetween(text, "ALTER", 0, prevKw.StartPos))
                            return SyntaxNode.AfterAlterTableAlterColumnKeyword;
                        if (HasKeywordBetween(text, "DROP", 0, prevKw.StartPos))
                            return SyntaxNode.AfterDropColumnKeyword;
                    }
                    return SyntaxNode.ExpressionPosition;

                case "DECLARE":
                    // DECLARE @var → 数据类型位置（US-05）
                    if (!immediatelyAfter)
                        return SyntaxNode.DataTypePosition;
                    return SyntaxNode.ExpressionPosition;

                case "RETURNS":
                    // CREATE FUNCTION RETURNS → 数据类型位置（US-05）
                    if (stmtType == StatementType.DDL && !immediatelyAfter)
                        return SyntaxNode.DataTypePosition;
                    return SyntaxNode.ExpressionPosition;

                case "REBUILD":
                case "REORGANIZE":
                case "DISABLE":
                    if (stmtType == StatementType.DDL)
                        return SyntaxNode.ExpressionPosition;
                    return SyntaxNode.ExpressionPosition;

                case "UNIQUE":
                case "CLUSTERED":
                case "NONCLUSTERED":
                    if (stmtType == StatementType.DDL)
                        return SyntaxNode.ExpressionPosition;
                    return SyntaxNode.ExpressionPosition;

                case "CHECK":
                    if (stmtType == StatementType.DDL)
                        return SyntaxNode.ExpressionPosition;
                    return SyntaxNode.ExpressionPosition;

                // ===== 默认 =====
                default:
                    // 如果是 UPDATE 的附加 FROM
                    if (stmtType == StatementType.Update && prevKeyword == "FROM")
                        return SyntaxNode.UpdateFromTablePosition;
                    if (stmtType == StatementType.Delete && prevKeyword == "FROM")
                        return SyntaxNode.DeleteFromTablePosition;
                    return SyntaxNode.ExpressionPosition;
            }
        }

        /// <summary>
        /// 解析 CREATE TABLE 括号内的列定义位置（US-04）。
        /// 根据光标前的文本判断是列名位置、数据类型位置还是约束位置。
        /// </summary>
        private SyntaxNode ResolveColumnDefinitionPosition(string text, int cursorOffset)
        {
            // 从光标向前找最近的逗号或开括号（depth=0 层级）
            int lastDelim = -1;
            int parenDepth = 0;
            for (int i = cursorOffset - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == ')')
                    parenDepth++;
                else if (c == '(')
                {
                    if (parenDepth == 0)
                    {
                        lastDelim = i;
                        break;
                    }
                    parenDepth--;
                }
                else if (c == ',' && parenDepth == 0)
                {
                    lastDelim = i;
                    break;
                }
            }

            int startPos = (lastDelim >= 0) ? lastDelim + 1 : 0;
            string segment = text.Substring(startPos, cursorOffset - startPos).Trim();

            // 空段 → 新列定义开始，提示数据类型
            if (string.IsNullOrEmpty(segment))
                return SyntaxNode.ColumnDefinitionPosition;

            // 检查是否以 CONSTRAINT 开头
            if (segment.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                var parts = segment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 1)
                    return SyntaxNode.ConstraintNamePosition;
                return SyntaxNode.ConstraintTypePosition;
            }

            // 检查是否以 PRIMARY/UNIQUE/FOREIGN/CHECK 开头（表级约束）
            if (segment.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                // 如果后面有括号 → 列名位置
                if (segment.Contains("("))
                    return SyntaxNode.ConstraintColumnsPosition;
                return SyntaxNode.TableConstraintPosition;
            }

            // 计算单词数（排除方括号内的内容作为单词）
            var words = segment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 1)
                return SyntaxNode.ColumnDefinitionPosition; // 仅列名 → 数据类型位置

            return SyntaxNode.ColumnConstraintPosition; // 列名+类型 → 约束位置
        }

        /// <summary>
        /// 检查光标是否紧跟在关键词之后（关键词 + 空格 + 光标）。
        /// </summary>
        private bool IsImmediatelyAfterKeyword(string text, int cursorOffset, KeywordInfo kw)
        {
            // 从关键词结束位置到光标，应该全是空白
            for (int i = kw.EndPos; i < cursorOffset; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                    return false;
            }
            return cursorOffset > kw.EndPos || cursorOffset == kw.EndPos;
        }

        /// <summary>
        /// 检查在 [start, offset) 范围内是否存在指定关键词。
        /// </summary>
        private bool HasKeywordBetween(string text, string keyword, int start, int offset)
        {
            return FindKeyword(text, keyword, start, offset) != null;
        }

        /// <summary>
        /// 在指定范围内查找关键词。
        /// </summary>
        private KeywordInfo? FindKeyword(string text, string keyword, int start, int end = -1)
        {
            if (end < 0) end = text.Length;

            var pattern = new Regex($@"\b{keyword}\b", RegexOptions.IgnoreCase);
            foreach (Match m in pattern.Matches(text))
            {
                if (m.Index >= start && m.Index < end)
                {
                    if (!IsPositionInCommentOrString(text, m.Index))
                    {
                        return new KeywordInfo
                        {
                            Word = m.Value.ToUpperInvariant(),
                            StartPos = m.Index,
                            EndPos = m.Index + m.Length
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 检查光标是否在指定起始位置后的括号内。
        /// </summary>
        private bool IsInsideParen(string text, int cursorOffset, int startPos)
        {
            int depth = 0;
            bool inString = false;

            for (int i = startPos; i < cursorOffset && i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\'') inString = !inString;
                if (inString) continue;

                if (c == '(') depth++;
                else if (c == ')') depth--;
            }

            return depth > 0;
        }

        /// <summary>
        /// 检查在 [start, offset) 范围内是否存在指定字符。
        /// </summary>
        private bool HasCharBetween(string text, char c, int start, int offset)
        {
            bool inString = false;
            for (int i = start; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\'') inString = !inString;
                if (inString) continue;
                if (text[i] == c) return true;
            }
            return false;
        }

        /// <summary>
        /// 判断字符是否是单词字符（字母、数字、下划线、#、@）。
        /// </summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@';
        }

        /// <summary>
        /// 关键词信息。
        /// </summary>
        private class KeywordInfo
        {
            public string Word { get; set; } = "";
            public int StartPos { get; set; }
            public int EndPos { get; set; }
        }
    }
}
