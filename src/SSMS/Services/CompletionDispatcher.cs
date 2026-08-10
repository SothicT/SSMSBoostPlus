using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// 补全缓存提供者接口。由 DatabaseObjectCacheService 实现。
    /// 用于解耦 CompletionDispatcher 与具体缓存实现，支持单元测试 Mock。
    /// </summary>
    public interface ICompletionCacheProvider
    {
        /// <summary>获取表的列信息</summary>
        List<ColumnInfo>? GetTableColumns(string tableName, string prefix);

        /// <summary>获取数据库候选列表</summary>
        List<CompletionCandidate> GetDatabaseCandidates();

        /// <summary>获取指定数据库的 Schema 候选</summary>
        List<CompletionCandidate> GetSchemaCandidatesForDb(string dbName, string prefix);

        /// <summary>获取当前库中匹配前缀的对象（表/视图/SP/函数）</summary>
        List<CompletionCandidate> GetMatchingObjects(string prefix);

        /// <summary>获取指定数据库中匹配前缀的对象</summary>
        List<CompletionCandidate> GetMatchingObjectsForDb(string databaseName, string prefix);

        /// <summary>获取当前库中指定类型的所有对象（无前缀过滤）</summary>
        List<CompletionCandidate> GetAllObjectsOfTypes(params string[] typeCodes);

        /// <summary>获取指定数据库中指定类型的所有对象</summary>
        List<CompletionCandidate> GetAllObjectsOfTypesForDb(string databaseName, params string[] typeCodes);

        /// <summary>解析表名（检查是否为缓存中的已知表）</summary>
        string? ResolveTableName(string name);
    }

    /// <summary>
    /// 字段来源信息（统一抽象别名/裸表/子查询）。
    /// </summary>
    public class FieldSource
    {
        /// <summary>别名或表名（用于显示前缀）</summary>
        public string Alias { get; set; } = "";

        /// <summary>物理表名</summary>
        public string? PhysicalName { get; set; }

        /// <summary>是否临时表</summary>
        public bool IsTempTable { get; set; }

        /// <summary>来源类型</summary>
        public FieldSourceType SourceType { get; set; }

        /// <summary>子查询输出列（仅 SourceType=Subquery 有效）</summary>
        public List<string>? SubqueryColumns { get; set; }

        /// <summary>子查询是否 SELECT *</summary>
        public bool HasStarSelect { get; set; }

        /// <summary>子查询内层 FROM 表名（用于展开 *）</summary>
        public string? InnerFromTable { get; set; }
    }

    /// <summary>
    /// 字段来源类型。
    /// </summary>
    public enum FieldSourceType
    {
        /// <summary>物理表（含跨库）</summary>
        Table,
        /// <summary>临时表</summary>
        TempTable,
        /// <summary>子查询别名</summary>
        Subquery
    }

    /// <summary>
    /// 分发引擎。根据语法节点决定展示哪种补全候选。
    /// 依赖 ICompletionCacheProvider 和 StatementParser。
    /// </summary>
    public class CompletionDispatcher
    {
        private readonly ICompletionCacheProvider _cache;
        private readonly StatementParser _parser;

        // SQL 关键词列表（与 SqlTemplateCompletionService 保持一致）
        private static readonly string[] ContextKeywords =
        {
            "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE", "IS", "NULL",
            "ASC", "DESC", "AS", "ON",
            "CASE", "WHEN", "THEN", "ELSE", "END",
            "TOP", "DISTINCT", "ALL",
            "UNION", "INTERSECT", "EXCEPT",
            "DEFAULT", "VALUES",
        };

        // 语句起始关键词（StatementStart 上下文使用）
        // 包含所有可作为 SQL 语句第一个词的关键词
        private static readonly string[] StatementStartKeywords =
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE",
            "EXEC", "EXECUTE", "SP_EXECUTESQL",
            "CREATE", "ALTER", "DROP",
            "BEGIN", "COMMIT", "ROLLBACK",
            "IF", "WHILE", "RETURN", "DECLARE", "SET", "PRINT",
            "TRUNCATE", "GRANT", "REVOKE",
            "WITH",
        };

        // WHERE/HAVING/ON 条件区关键词（条件运算符 + 后续子句关键词）
        // US-01: WHERE 后不仅要提示条件运算符，还要提示 GROUP BY/ORDER BY/HAVING/OFFSET/UNION 等后续子句
        private static readonly string[] WhereConditionKeywords =
        {
            // 条件运算符
            "AND", "OR", "IN", "BETWEEN", "LIKE", "IS", "NOT", "NULL",
            // 后续子句关键词（WHERE 后可跟的合法子句）
            "GROUP", "ORDER", "HAVING", "OFFSET", "UNION", "INTERSECT", "EXCEPT",
            // CASE 表达式（WHERE 中常用）
            "CASE", "WHEN", "THEN", "ELSE", "END",
        };

        // SELECT 列区关键词
        private static readonly string[] SelectColumnKeywords =
        {
            "AS", "TOP", "DISTINCT", "ALL",
            "CASE", "WHEN", "THEN", "ELSE", "END",
            "FROM",
        };

        // DDL 对象类型关键词
        private static readonly string[] DdlObjectKeywords =
        {
            "TABLE", "INDEX", "VIEW", "PROCEDURE", "FUNCTION", "TRIGGER",
        };

        // DDL DROP 对象类型关键词
        private static readonly string[] DropObjectKeywords =
        {
            "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX", "TRIGGER",
        };

        // 列级约束关键词
        private static readonly string[] ColumnConstraintKeywords =
        {
            "NOT NULL", "NULL", "PRIMARY KEY", "UNIQUE", "DEFAULT", "CHECK", "REFERENCES", "IDENTITY",
        };

        // 表级约束关键词
        private static readonly string[] TableConstraintKeywords =
        {
            "CONSTRAINT", "PRIMARY KEY", "UNIQUE", "FOREIGN KEY", "CHECK",
        };

        // 约束类型关键词
        private static readonly string[] ConstraintTypeKeywords =
        {
            "PRIMARY KEY", "UNIQUE", "FOREIGN KEY", "CHECK", "DEFAULT",
        };

        // ALTER TABLE 操作关键词
        private static readonly string[] AlterTableActionKeywords =
        {
            "ADD", "DROP", "ALTER COLUMN", "WITH CHECK", "WITH NOCHECK",
        };

        // 索引选项关键词
        private static readonly string[] IndexOptionKeywords =
        {
            "INCLUDE", "WHERE", "WITH",
        };

        // 索引操作关键词
        private static readonly string[] IndexActionKeywords =
        {
            "REBUILD", "REORGANIZE", "DISABLE",
        };

        // ON DELETE/UPDATE 行为关键词
        private static readonly string[] OnDeleteUpdateKeywords =
        {
            "CASCADE", "SET NULL", "SET DEFAULT", "NO ACTION",
        };

        // 过程体起始关键词
        private static readonly string[] BodyStartKeywords =
        {
            "SELECT", "BEGIN", "DECLARE", "SET", "INSERT", "UPDATE", "DELETE",
            "IF", "WHILE", "RETURN", "EXEC",
        };

        // SQL Server 数据类型列表（US-05）
        private static readonly (string category, string typeName, string insertText)[] SqlDataTypes =
        {
            // 精确数值
            ("数值", "INT", "INT"),
            ("数值", "BIGINT", "BIGINT"),
            ("数值", "SMALLINT", "SMALLINT"),
            ("数值", "TINYINT", "TINYINT"),
            ("数值", "DECIMAL", "DECIMAL(18,2)"),
            ("数值", "NUMERIC", "NUMERIC(18,2)"),
            ("数值", "MONEY", "MONEY"),
            ("数值", "SMALLMONEY", "SMALLMONEY"),
            ("数值", "BIT", "BIT"),
            // 近似数值
            ("数值", "FLOAT", "FLOAT"),
            ("数值", "REAL", "REAL"),
            // 字符串
            ("字符串", "VARCHAR", "VARCHAR(50)"),
            ("字符串", "NVARCHAR", "NVARCHAR(50)"),
            ("字符串", "CHAR", "CHAR(10)"),
            ("字符串", "NCHAR", "NCHAR(10)"),
            ("字符串", "TEXT", "TEXT"),
            ("字符串", "NTEXT", "NTEXT"),
            // 日期时间
            ("日期", "DATETIME", "DATETIME"),
            ("日期", "DATETIME2", "DATETIME2"),
            ("日期", "DATE", "DATE"),
            ("日期", "TIME", "TIME"),
            ("日期", "SMALLDATETIME", "SMALLDATETIME"),
            ("日期", "DATETIMEOFFSET", "DATETIMEOFFSET"),
            // 二进制
            ("二进制", "BINARY", "BINARY(50)"),
            ("二进制", "VARBINARY", "VARBINARY(50)"),
            ("二进制", "IMAGE", "IMAGE"),
            // 其他
            ("其他", "UNIQUEIDENTIFIER", "UNIQUEIDENTIFIER"),
            ("其他", "XML", "XML"),
            ("其他", "SQL_VARIANT", "SQL_VARIANT"),
            ("其他", "TABLE", "TABLE"),
            ("其他", "CURSOR", "CURSOR"),
        };

        public CompletionDispatcher(ICompletionCacheProvider cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _parser = new StatementParser();
        }

        /// <summary>
        /// 根据语法节点获取补全候选列表。
        /// </summary>
        /// <param name="node">语法节点</param>
        /// <param name="currentStatement">当前完整语句文本</param>
        /// <param name="filterText">已输入的字符片段</param>
        /// <param name="locationResult">语法位置定位结果（含点号前词、跨库信息等）</param>
        /// <param name="connectedDb">当前连接数据库名</param>
        public List<CompletionCandidate> GetCandidates(
            SyntaxNode node,
            string currentStatement,
            string filterText,
            SyntaxLocationResult locationResult,
            string connectedDb)
        {
            try
            {
                switch (node)
                {
                    // ===== 不提示 =====
                    case SyntaxNode.InCommentOrString:
                        return new List<CompletionCandidate>();

                    // ===== 关键词 + 模板 =====
                    case SyntaxNode.StatementStart:
                        return GetKeywordCandidates(filterText, StatementStartKeywords);

                    case SyntaxNode.AfterSelectKeyword:
                        return GetKeywordCandidates(filterText, new[] { "TOP", "DISTINCT", "ALL" })
                            .Concat(GetFieldCandidates(currentStatement, filterText, false))
                            .ToList();

                    case SyntaxNode.AfterTopKeyword:
                        // SELECT TOP n 之后 → 提示字段 + 关键词
                        return GetFieldCandidates(currentStatement, filterText, false)
                            .Concat(GetKeywordCandidates(filterText, new[] { "DISTINCT", "ALL" }))
                            .ToList();

                    // ===== SELECT 列区 =====
                    case SyntaxNode.SelectColumnArea:
                        return GetFieldCandidates(currentStatement, filterText, false)
                            .Concat(GetKeywordCandidates(filterText, SelectColumnKeywords))
                            .ToList();

                    // ===== 条件区（WHERE / HAVING / ON / AND / OR）=====
                    // US-01: 放开关键词限制，不再只返回 8 个条件运算符
                    case SyntaxNode.AfterWhereKeyword:
                    case SyntaxNode.AfterAndOrKeyword:
                    case SyntaxNode.SearchConditionPosition:
                    case SyntaxNode.AfterHavingKeyword:
                    case SyntaxNode.AfterJoinOnKeyword:
                        return GetFieldCandidates(currentStatement, filterText, false)
                            .Concat(GetKeywordCandidates(filterText, WhereConditionKeywords))
                            .ToList();

                    case SyntaxNode.AfterDotNotation:
                        // 点号触发：用点号前的词查找对应表的字段
                        return GetDotFieldCandidates(currentStatement, filterText, locationResult);

                    // ===== 表名提示 =====
                    case SyntaxNode.AfterFromKeyword:
                    case SyntaxNode.AfterJoinKeyword:
                    case SyntaxNode.TableNamePosition:
                    case SyntaxNode.InsertIntoTablePosition:
                    case SyntaxNode.UpdateTablePosition:
                    case SyntaxNode.DeleteTablePosition:
                    case SyntaxNode.UpdateFromTablePosition:
                    case SyntaxNode.DeleteFromTablePosition:
                        return GetTableCandidates(filterText, connectedDb);

                    // ===== 跨库引导 =====
                    case SyntaxNode.CrossDbContextPrompt:
                        return GetCrossDbCandidates(filterText, connectedDb, locationResult);

                    // ===== ORDER BY =====
                    case SyntaxNode.AfterOrderByKeyword:
                        return GetColumnAliasCandidates(currentStatement, filterText)
                            .Concat(GetFieldCandidates(currentStatement, filterText, false))
                            .Concat(GetKeywordCandidates(filterText, new[] { "BY", "ASC", "DESC" }))
                            .OrderByDescending(c => c.MatchScore)
                            .ThenBy(c => c.FilterText, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                    // ===== GROUP BY =====
                    case SyntaxNode.AfterGroupByKeyword:
                        return GetColumnAliasCandidates(currentStatement, filterText)
                            .Concat(GetFieldCandidates(currentStatement, filterText, false))
                            .OrderByDescending(c => c.MatchScore)
                            .ThenBy(c => c.FilterText, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                    // ===== INSERT 相关 =====
                    case SyntaxNode.InsertTableAfterInsertInto:
                        return GetInsertExpandCandidates(currentStatement);

                    case SyntaxNode.InsertColumnListPosition:
                        var insertTable = _parser.GetInsertTargetTable(currentStatement);
                        if (insertTable == null) return new List<CompletionCandidate>();
                        return GetFieldCandidatesForTable(insertTable, filterText, true);

                    case SyntaxNode.InsertValuesPosition:
                        return GetKeywordCandidates(filterText, new[] { "DEFAULT", "NULL" });

                    // ===== UPDATE 相关 =====
                    case SyntaxNode.UpdateSetPosition:
                        var updateTarget = _parser.GetUpdateTargetTable(currentStatement);
                        if (updateTarget == null) return new List<CompletionCandidate>();
                        return GetFieldCandidatesForTable(updateTarget.Value.tableName, filterText, false);

                    case SyntaxNode.UpdateSetColumnAssignment:
                        return GetKeywordCandidates(filterText, new[] { "DEFAULT", "NULL" });

                    // ===== EXEC =====
                    case SyntaxNode.ExecPosition:
                        // SP(P) + 函数(FN/IF/TF)
                        // 有 filter 时也按类型过滤，避免返回表/视图等非 SP/函数对象
                        return string.IsNullOrEmpty(filterText)
                            ? _cache.GetAllObjectsOfTypes("P", "FN", "IF", "TF")
                            : _cache.GetMatchingObjects(filterText)
                                .Where(m => m.TypeCode == "P" || m.TypeCode == "FN" ||
                                            m.TypeCode == "IF" || m.TypeCode == "TF")
                                .ToList();

                    case SyntaxNode.ExecParameterPosition:
                        // SP 参数提示（简化：不提示，SSMS 自带参数提示）
                        return new List<CompletionCandidate>();

                    // ===== 集合运算 =====
                    case SyntaxNode.AfterSetOperationKeyword:
                        return GetKeywordCandidates(filterText, new[] { "SELECT", "INSERT", "UPDATE", "DELETE" });

                    // ===== 表提示 =====
                    case SyntaxNode.TableHintPosition:
                        return GetKeywordCandidates(filterText, new[]
                        {
                            "NOLOCK", "ROWLOCK", "PAGLOCK", "TABLOCK", "TABLOCKX",
                            "HOLDLOCK", "UPDLOCK", "XLOCK", "READPAST", "NOEXPAND",
                            "READUNCOMMITTED", "READCOMMITTED", "REPEATABLEREAD", "SERIALIZABLE"
                        });

                    // ===== DDL: CREATE 系列（US-04）=====
                    case SyntaxNode.AfterCreateKeyword:
                        return GetKeywordCandidates(filterText, DdlObjectKeywords);

                    case SyntaxNode.AfterCreateTableKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    case SyntaxNode.ColumnDefinitionPosition:
                        return GetDataTypeCandidates(filterText);

                    case SyntaxNode.ColumnConstraintPosition:
                        return GetKeywordCandidates(filterText, ColumnConstraintKeywords);

                    case SyntaxNode.TableConstraintPosition:
                        return GetKeywordCandidates(filterText, TableConstraintKeywords);

                    case SyntaxNode.ConstraintNamePosition:
                        // 约束名是自由文本，不需要提示
                        return new List<CompletionCandidate>();

                    case SyntaxNode.ConstraintTypePosition:
                        return GetKeywordCandidates(filterText, ConstraintTypeKeywords);

                    case SyntaxNode.ConstraintColumnsPosition:
                        // 列名在语句中已定义，这里简化为空（未来可从语句解析已定义列名）
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterForeignKeyReferences:
                        return GetTableCandidates(filterText, connectedDb);

                    case SyntaxNode.AfterForeignKeyRefColumns:
                        // 被引用表的列名（简化：返回空，SSMS 自带）
                        return new List<CompletionCandidate>();

                    case SyntaxNode.OnDeleteUpdatePosition:
                        return GetKeywordCandidates(filterText, OnDeleteUpdateKeywords);

                    case SyntaxNode.AfterCreateIndexKeyword:
                        // 索引名是自由文本
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterCreateIndexOnKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    case SyntaxNode.IndexColumnPosition:
                        return GetKeywordCandidates(filterText, new[] { "ASC", "DESC" });

                    case SyntaxNode.IndexOptionsPosition:
                        return GetKeywordCandidates(filterText, IndexOptionKeywords);

                    case SyntaxNode.AfterCreateViewKeyword:
                        // 视图名是自由文本
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterViewAsKeyword:
                        return GetKeywordCandidates(filterText, new[] { "SELECT" });

                    case SyntaxNode.AfterCreateProcKeyword:
                        // 过程名是自由文本
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterProcParamNamePosition:
                        return GetDataTypeCandidates(filterText);

                    case SyntaxNode.AfterProcAsKeyword:
                        return GetKeywordCandidates(filterText, BodyStartKeywords);

                    case SyntaxNode.AfterCreateFuncKeyword:
                        // 函数名是自由文本
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterCreateTriggerKeyword:
                        // 触发器名是自由文本
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterTriggerOnKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    // ===== DDL: ALTER 系列 =====
                    case SyntaxNode.AfterAlterKeyword:
                        return GetKeywordCandidates(filterText, DdlObjectKeywords);

                    case SyntaxNode.AfterAlterTableKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    case SyntaxNode.AfterAlterTableAction:
                        return GetKeywordCandidates(filterText, AlterTableActionKeywords);

                    case SyntaxNode.AfterAlterTableAddKeyword:
                        return GetKeywordCandidates(filterText, new[] { "CONSTRAINT" });

                    case SyntaxNode.AfterAlterTableAddColumnType:
                        return GetDataTypeCandidates(filterText);

                    case SyntaxNode.AfterDropColumnKeyword:
                        // DROP COLUMN 后提示当前表的列名
                        {
                            var alterTableTarget = _parser.GetAlterTableTargetTable(currentStatement);
                            if (alterTableTarget == null) return new List<CompletionCandidate>();
                            return GetFieldCandidatesForTable(alterTableTarget, filterText, false);
                        }

                    case SyntaxNode.AfterDropConstraintKeyword:
                        // 当前表约束名（简化：返回空）
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterAlterTableAlterColumnKeyword:
                        // ALTER COLUMN 后提示当前表的列名
                        {
                            var alterTableTarget = _parser.GetAlterTableTargetTable(currentStatement);
                            if (alterTableTarget == null) return new List<CompletionCandidate>();
                            return GetFieldCandidatesForTable(alterTableTarget, filterText, false);
                        }

                    case SyntaxNode.AfterAlterColumnTypePosition:
                        return GetDataTypeCandidates(filterText);

                    case SyntaxNode.AfterAlterIndexKeyword:
                        // 索引名是自由文本
                        return new List<CompletionCandidate>();

                    case SyntaxNode.AfterAlterIndexOnKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    case SyntaxNode.AfterAlterIndexAction:
                        return GetKeywordCandidates(filterText, IndexActionKeywords);

                    // ===== DDL: DROP 系列 =====
                    case SyntaxNode.AfterDropKeyword:
                        return GetKeywordCandidates(filterText, DropObjectKeywords);

                    case SyntaxNode.AfterDropObjectKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    case SyntaxNode.AfterDropIndexKeyword:
                        // 索引名是自由文本
                        return new List<CompletionCandidate>();

                    // ===== DDL: TRUNCATE 系列 =====
                    case SyntaxNode.AfterTruncateKeyword:
                        return GetKeywordCandidates(filterText, new[] { "TABLE" });

                    case SyntaxNode.AfterTruncateTableKeyword:
                        return GetTableCandidates(filterText, connectedDb);

                    // ===== 数据类型提示（US-05）=====
                    case SyntaxNode.DataTypePosition:
                        return GetDataTypeCandidates(filterText);

                    // ===== 默认 =====
                    default:
                        return GetKeywordCandidates(filterText);
                }
            }
            catch (Exception)
            {
                return new List<CompletionCandidate>();
            }
        }

        // ================================================================
        // 字段候选生成
        // ================================================================

        /// <summary>
        /// 获取字段候选列表（无点号触发）。
        /// </summary>
        private List<CompletionCandidate> GetFieldCandidates(string statement, string filter, bool hasDotPrefix)
        {
            var parseResult = _parser.Parse(statement);
            var allSources = BuildFieldSources(parseResult);

            if (allSources.Count == 0)
                return new List<CompletionCandidate>();

            return BuildFieldCandidateList(allSources, filter, false);
        }

        /// <summary>
        /// 获取点号触发的字段候选列表。
        /// </summary>
        private List<CompletionCandidate> GetDotFieldCandidates(
            string statement, string filter, SyntaxLocationResult location)
        {
            if (string.IsNullOrEmpty(location.WordBeforeDot))
                return new List<CompletionCandidate>();

            var parseResult = _parser.Parse(statement);

            // 检查是否是别名
            if (parseResult.Aliases.TryGetValue(location.WordBeforeDot, out var aliasInfo))
            {
                if (aliasInfo.IsTempTable)
                {
                    return new List<CompletionCandidate>
                    {
                        new CompletionCandidate
                        {
                            DisplayText = $"{aliasInfo.Alias}.[#临时表#]",
                            FilterText = aliasInfo.Alias,
                            InsertText = $"{aliasInfo.Alias}.",
                            TypeTag = "临时表",
                            ItemType = CompletionItemType.Field,
                            MatchScore = -1
                        }
                    };
                }

                var columns = _cache.GetTableColumns(aliasInfo.PhysicalTable, filter);
                if (columns == null) return new List<CompletionCandidate>();

                // 点号触发：仅显示字段名，不标注来源
                return columns.Select(c => new CompletionCandidate
                {
                    DisplayText = c.Name,
                    FilterText = c.Name,
                    InsertText = c.Name,
                    TypeTag = "字段",
                    ItemType = CompletionItemType.Field,
                    MatchScore = string.IsNullOrEmpty(filter) ? 0
                        : CompletionCandidate.FuzzyMatch(filter, c.Name),
                })
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
            }

            // 检查是否是子查询别名
            var subquery = parseResult.SubqueryAliases.FirstOrDefault(
                sq => sq.Alias.Equals(location.WordBeforeDot, StringComparison.OrdinalIgnoreCase));
            if (subquery != null)
            {
                List<string> columns;
                if (subquery.HasStarSelect && !string.IsNullOrEmpty(subquery.InnerFromTable))
                {
                    var dbCols = _cache.GetTableColumns(subquery.InnerFromTable, filter);
                    columns = dbCols?.Select(c => c.Name).ToList() ?? new List<string>();
                }
                else
                {
                    columns = subquery.Columns.Where(c => c != "*").ToList();
                }

                return columns.Select(c => new CompletionCandidate
                {
                    DisplayText = c,
                    FilterText = c,
                    InsertText = c,
                    TypeTag = "字段",
                    ItemType = CompletionItemType.Field,
                    MatchScore = string.IsNullOrEmpty(filter) ? 0
                        : CompletionCandidate.FuzzyMatch(filter, c),
                })
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
            }

            // 检查是否是表名
            var resolved = _cache.ResolveTableName(location.WordBeforeDot);
            if (resolved != null)
            {
                var columns = _cache.GetTableColumns(resolved, filter);
                if (columns == null) return new List<CompletionCandidate>();

                return columns.Select(c => new CompletionCandidate
                {
                    DisplayText = c.Name,
                    FilterText = c.Name,
                    InsertText = c.Name,
                    TypeTag = "字段",
                    ItemType = CompletionItemType.Field,
                    MatchScore = string.IsNullOrEmpty(filter) ? 0
                        : CompletionCandidate.FuzzyMatch(filter, c.Name),
                })
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
            }

            return new List<CompletionCandidate>();
        }

        /// <summary>
        /// 获取指定表的字段候选。
        /// </summary>
        private List<CompletionCandidate> GetFieldCandidatesForTable(string tableName, string filter, bool hasDotPrefix)
        {
            var columns = _cache.GetTableColumns(tableName, filter);
            if (columns == null) return new List<CompletionCandidate>();

            return columns.Select(c => new CompletionCandidate
            {
                DisplayText = c.Name,
                FilterText = c.Name,
                InsertText = c.Name,
                TypeTag = "字段",
                ItemType = CompletionItemType.Field,
                MatchScore = string.IsNullOrEmpty(filter) ? 0
                    : CompletionCandidate.FuzzyMatch(filter, c.Name),
            })
            .OrderByDescending(m => m.MatchScore)
            .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
            .ToList();
        }

        /// <summary>
        /// 从解析结果构建字段来源列表。
        /// </summary>
        private List<FieldSource> BuildFieldSources(StatementParseResult parseResult)
        {
            var sources = new List<FieldSource>();

            // 物理表别名
            foreach (var kvp in parseResult.Aliases)
            {
                var aliasInfo = kvp.Value;
                sources.Add(new FieldSource
                {
                    Alias = aliasInfo.Alias,
                    PhysicalName = aliasInfo.PhysicalTable,
                    IsTempTable = aliasInfo.IsTempTable,
                    SourceType = aliasInfo.IsTempTable ? FieldSourceType.TempTable : FieldSourceType.Table
                });
            }

            // 裸表
            foreach (var bare in parseResult.BareTables)
            {
                // 避免重复（如果裸表名与别名映射中的表名相同）
                if (sources.Any(s => s.PhysicalName?.Equals(bare.PhysicalName, StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                sources.Add(new FieldSource
                {
                    Alias = bare.DisplayName,
                    PhysicalName = bare.PhysicalName,
                    IsTempTable = bare.IsTempTable,
                    SourceType = bare.IsTempTable ? FieldSourceType.TempTable : FieldSourceType.Table
                });
            }

            // 子查询别名
            foreach (var sq in parseResult.SubqueryAliases)
            {
                sources.Add(new FieldSource
                {
                    Alias = sq.Alias,
                    SourceType = FieldSourceType.Subquery,
                    SubqueryColumns = sq.Columns,
                    HasStarSelect = sq.HasStarSelect,
                    InnerFromTable = sq.InnerFromTable
                });
            }

            return sources;
        }

        /// <summary>
        /// 构建字段候选列表（统一处理多表/单表/子查询/临时表）。
        /// </summary>
        private List<CompletionCandidate> BuildFieldCandidateList(
            List<FieldSource> sources, string filter, bool isDotTrigger)
        {
            var candidates = new List<CompletionCandidate>();
            bool isMultiTable = sources.Count > 1;

            foreach (var source in sources)
            {
                List<string>? columns = null;
                bool hasExplicitAlias = false;

                switch (source.SourceType)
                {
                    case FieldSourceType.TempTable:
                        // 临时表：显示占位
                        candidates.Add(new CompletionCandidate
                        {
                            DisplayText = $"{source.Alias}.[#临时表#]",
                            FilterText = source.Alias,
                            InsertText = $"{source.Alias}.",
                            TypeTag = "临时表",
                            ItemType = CompletionItemType.Field,
                            MatchScore = -1
                        });
                        continue;

                    case FieldSourceType.Subquery:
                        hasExplicitAlias = true; // 子查询别名一定是显式的
                        if (source.HasStarSelect && !string.IsNullOrEmpty(source.InnerFromTable))
                        {
                            // SELECT * → 展开为对应物理表
                            var dbCols = _cache.GetTableColumns(source.InnerFromTable!, filter);
                            columns = dbCols?.Select(c => c.Name).ToList();
                        }
                        else
                        {
                            columns = source.SubqueryColumns?.Where(c => c != "*").ToList();
                        }
                        break;

                    default:
                        // 物理表
                        if (!string.IsNullOrEmpty(source.PhysicalName))
                        {
                            var dbCols = _cache.GetTableColumns(source.PhysicalName!, filter);
                            columns = dbCols?.Select(c => c.Name).ToList();
                        }
                        // 别名与物理表名不同 → 用户显式定义了别名
                        hasExplicitAlias = !string.IsNullOrEmpty(source.PhysicalName)
                            && !source.Alias.Equals(source.PhysicalName, StringComparison.OrdinalIgnoreCase);
                        break;
                }

                if (columns == null || columns.Count == 0)
                    continue;

                bool forcePrefix = isMultiTable || (hasExplicitAlias && !isDotTrigger);

                foreach (var colName in columns)
                {
                    // 点号触发 → 仅字段名（通过 a. 方式已指定来源）
                    // 多表或显式别名 → 带别名前缀（帮助区分来源）
                    bool showPrefix = forcePrefix;

                    string display = showPrefix
                        ? $"{source.Alias}.[{colName}]"
                        : colName;

                    string insert = showPrefix
                        ? $"{source.Alias}.{colName} "
                        : $"{colName} ";

                    candidates.Add(new CompletionCandidate
                    {
                        DisplayText = display,
                        FilterText = colName,
                        InsertText = insert,
                        TypeTag = "字段",
                        ItemType = CompletionItemType.Field,
                        MatchScore = string.IsNullOrEmpty(filter) ? 0
                            : CompletionCandidate.FuzzyMatch(filter, colName),
                    });

                    // 注：单表显式别名 → 只显示带前缀版本（如 a.TransDateTime）
                    //     用户可通过点号 a. 触发获取无前缀字段名
                    //     裸表（无别名）→ 只显示无前缀版本（如 TransDateTime）
                }
            }

            // 排序：匹配分 > 字母序
            return candidates
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ================================================================
        // 表名候选
        // ================================================================

        private List<CompletionCandidate> GetTableCandidates(string filter, string connectedDb)
        {
            // 获取当前库的表/视图（TypeCode: U=表, V=视图）
            // 有 filter 时也按类型过滤，避免返回 SP/函数等非表对象
            var candidates = string.IsNullOrEmpty(filter)
                ? _cache.GetAllObjectsOfTypes("U", "V")
                : _cache.GetMatchingObjects(filter)
                    .Where(m => m.TypeCode == "U" || m.TypeCode == "V")
                    .ToList();

            // 如果有缓存服务且输入不为空，也添加数据库候选
            if (!string.IsNullOrEmpty(filter))
            {
                var dbCandidates = _cache.GetDatabaseCandidates() ?? new List<CompletionCandidate>();
                foreach (var db in dbCandidates)
                {
                    var score = CompletionCandidate.FuzzyMatch(filter, db.FilterText);
                    if (score >= 0)
                    {
                        db.MatchScore = score;
                        candidates.Add(db);
                    }
                }
            }

            return candidates
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ================================================================
        // 跨库候选
        // ================================================================

        private List<CompletionCandidate> GetCrossDbCandidates(
            string filter, string connectedDb, SyntaxLocationResult location)
        {
            int level = location.CrossDbLevel;

            switch (level)
            {
                case 1:
                    // [DBName]. → 提示 Schema
                    if (!string.IsNullOrEmpty(location.CrossDbName))
                        return _cache.GetSchemaCandidatesForDb(location.CrossDbName, filter);
                    return new List<CompletionCandidate>();

                case 2:
                    // [DBName].[Schema]. → 提示 Table
                    if (!string.IsNullOrEmpty(location.CrossDbName))
                        return _cache.GetMatchingObjectsForDb(location.CrossDbName, filter);
                    return new List<CompletionCandidate>();

                default:
                    // 空 → 提示 DB + 当前库表
                    var dbCandidates = _cache.GetDatabaseCandidates() ?? new List<CompletionCandidate>();
                    var localTables = (string.IsNullOrEmpty(filter)
                        ? _cache.GetAllObjectsOfTypes("U", "V")
                        : _cache.GetMatchingObjects(filter)
                            .Where(m => m.TypeCode == "U" || m.TypeCode == "V")
                            .ToList()) ?? new List<CompletionCandidate>();
                    return dbCandidates.Concat(localTables).ToList();
            }
        }

        // ================================================================
        // 关键词候选
        // ================================================================

        private List<CompletionCandidate> GetKeywordCandidates(string filter, string[]? specificKeywords = null)
        {
            var keywords = specificKeywords ?? ContextKeywords;
            var candidates = new List<CompletionCandidate>();

            foreach (var kw in keywords)
            {
                int score = string.IsNullOrEmpty(filter) ? 0
                    : CompletionCandidate.FuzzyMatch(filter, kw);

                if (string.IsNullOrEmpty(filter) || score >= 0)
                {
                    candidates.Add(new CompletionCandidate
                    {
                        DisplayText = kw,
                        FilterText = kw,
                        InsertText = kw,
                        TypeTag = "关键词",
                        ItemType = CompletionItemType.Keyword,
                        MatchScore = score
                    });
                }
            }

            return candidates
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ================================================================
        // 数据类型候选（US-05）
        // ================================================================

        /// <summary>
        /// 获取 SQL Server 数据类型候选列表。
        /// </summary>
        private List<CompletionCandidate> GetDataTypeCandidates(string filter)
        {
            var candidates = new List<CompletionCandidate>();

            foreach (var (category, typeName, insertText) in SqlDataTypes)
            {
                int score = string.IsNullOrEmpty(filter) ? 0
                    : CompletionCandidate.FuzzyMatch(filter, typeName);

                if (string.IsNullOrEmpty(filter) || score >= 0)
                {
                    candidates.Add(new CompletionCandidate
                    {
                        DisplayText = $"[{category}] {typeName}",
                        FilterText = typeName,
                        InsertText = insertText,
                        TypeTag = "类型",
                        ItemType = CompletionItemType.Keyword,
                        MatchScore = score
                    });
                }
            }

            return candidates
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ================================================================
        // SELECT 列别名候选
        // ================================================================

        private List<CompletionCandidate> GetColumnAliasCandidates(string statement, string filter = "")
        {
            var aliases = _parser.GetSelectColumnAliases(statement);
            var result = new List<CompletionCandidate>();

            foreach (var a in aliases)
            {
                int score = string.IsNullOrEmpty(filter) ? 0
                    : CompletionCandidate.FuzzyMatch(filter, a);

                if (string.IsNullOrEmpty(filter) || score >= 0)
                {
                    result.Add(new CompletionCandidate
                    {
                        DisplayText = a,
                        FilterText = a,
                        InsertText = a,
                        TypeTag = "列别名",
                        ItemType = CompletionItemType.Field,
                        MatchScore = score
                    });
                }
            }

            return result;
        }

        // ================================================================
        // INSERT 字段展开
        // ================================================================

        private List<CompletionCandidate> GetInsertExpandCandidates(string statement)
        {
            // 幂等检测：表名后已有 ( 或 VALUES → 不展开
            var insertMatch = System.Text.RegularExpressions.Regex.Match(
                statement,
                @"INSERT\s+INTO\s+(?:\[?\w+\]?\s*\.\s*)*\[?(\w+)\]?\s*(\(|VALUES)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (insertMatch.Success && insertMatch.Groups[2].Success)
            {
                // 已有 ( 或 VALUES → 不展开
                return new List<CompletionCandidate>();
            }

            var tableName = _parser.GetInsertTargetTable(statement);
            if (string.IsNullOrEmpty(tableName))
                return new List<CompletionCandidate>();

            var columns = _cache.GetTableColumns(tableName, "");
            if (columns == null || columns.Count == 0)
                return new List<CompletionCandidate>();

            var colList = string.Join(",", columns.Select(c => $"[{c.Name}]"));
            var defaultValues = string.Join(",", columns.Select(c => $"''"));
            var expandedText = $"({colList}){Environment.NewLine}VALUES ({defaultValues})";

            return new List<CompletionCandidate>
            {
                new CompletionCandidate
                {
                    DisplayText = $"展开字段 ({columns.Count} 列)",
                    FilterText = "",
                    InsertText = expandedText,
                    TypeTag = "INSERT",
                    ItemType = CompletionItemType.Template,
                    MatchScore = 0
                }
            };
        }
    }
}
