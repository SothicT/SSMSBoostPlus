using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using SSMSBoostPlus.SSMS.Services;
using Xunit;

namespace SSMSBoostPlus.Tests.Services
{
    public class CompletionDispatcherTests
    {
        private readonly Mock<ICompletionCacheProvider> _mockCache = new();
        private readonly CompletionDispatcher _dispatcher;

        public CompletionDispatcherTests()
        {
            _dispatcher = new CompletionDispatcher(_mockCache.Object);
        }

        private void SetupTableColumns(string tableName, params string[] columns)
        {
            _mockCache
                .Setup(c => c.GetTableColumns(It.Is<string>(s => s.IndexOf(tableName, StringComparison.OrdinalIgnoreCase) >= 0), It.IsAny<string>()))
                .Returns(columns.Select(col => new ColumnInfo { Name = col }).ToList());
        }

        [Fact]
        public void GetCandidates_InCommentOrString_ReturnsEmpty()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.InCommentOrString, "", "", new SyntaxLocationResult(), "");

            result.Should().BeEmpty();
        }

        [Fact]
        public void GetCandidates_StatementStart_ReturnsKeywords()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.StatementStart, "", "SE", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "SELECT");
        }

        [Fact]
        public void GetCandidates_AfterWhere_ReturnsFieldsAndKeywords()
        {
            var sql = "SELECT * FROM [dbo].[T] A WHERE ";
            SetupTableColumns("T", "Id", "Name", "Status");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "Id");
            result.Should().Contain(c => c.FilterText == "AND");
        }

        [Fact]
        public void GetCandidates_MultiTable_FieldCandidatesHaveAliasPrefix()
        {
            var sql = "SELECT * FROM [dbo].[T1] A JOIN [dbo].[T2] B ON A.id = B.id WHERE ";
            SetupTableColumns("T1", "Id", "Name");
            SetupTableColumns("T2", "Id", "Code");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            // 多表上下文：字段应带别名前缀
            result.Should().Contain(c => c.DisplayText == "A.[Id]");
            result.Should().Contain(c => c.DisplayText == "A.[Name]");
            result.Should().Contain(c => c.DisplayText == "B.[Id]");
            result.Should().Contain(c => c.DisplayText == "B.[Code]");
        }

        [Fact]
        public void GetCandidates_SingleTable_ExplicitAlias_ShowsPrefix()
        {
            var sql = "SELECT * FROM [dbo].[T] A WHERE ";
            SetupTableColumns("T", "Id", "Name");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            // 单表且显式别名 A ≠ T → 只显示带前缀版本
            result.Should().Contain(c => c.DisplayText == "A.[Id]");
            result.Should().Contain(c => c.DisplayText == "A.[Name]");
            // 不显示不带前缀的版本（通过 WHERE a. 获取无前缀）
            result.Should().NotContain(c => c.DisplayText == "Id" && c.ItemType == CompletionItemType.Field);
            result.Should().NotContain(c => c.DisplayText == "Name" && c.ItemType == CompletionItemType.Field);
        }

        [Fact]
        public void GetCandidates_DotNotation_ReturnsFieldsForAlias()
        {
            var sql = "SELECT * FROM [dbo].[T] A WHERE A.";
            SetupTableColumns("T", "Id", "Name");

            var location = new SyntaxLocationResult
            {
                Node = SyntaxNode.AfterDotNotation,
                WordBeforeDot = "A"
            };

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterDotNotation, sql, "", location, "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "Id");
            result.Should().Contain(c => c.FilterText == "Name");
            // 点号触发：不标注来源
            result.All(c => !c.DisplayText.Contains('.')).Should().BeTrue();
        }

        [Fact]
        public void GetCandidates_TempTableAlias_ShowsPlaceholder()
        {
            var sql = "SELECT * FROM #T1 T WHERE ";
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.DisplayText.Contains("#临时表#"));
        }

        [Fact]
        public void GetCandidates_SubqueryAlias_ReturnsSubqueryColumns()
        {
            var sql = "SELECT * FROM #T1 T JOIN (SELECT WO, VersionNo FROM T2) L ON T.WO = L.WO WHERE ";
            SetupTableColumns("T2", "WO", "VersionNo");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            // 子查询别名 L 的字段应该出现
            result.Should().Contain(c => c.FilterText == "WO");
            result.Should().Contain(c => c.FilterText == "VersionNo");
        }

        [Fact]
        public void GetCandidates_InsertExpand_ReturnsExpandCandidate()
        {
            var sql = "INSERT INTO [dbo].[T]";
            SetupTableColumns("T", "Id", "Name", "Status");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.InsertTableAfterInsertInto, sql, "", new SyntaxLocationResult(), "");

            result.Should().HaveCount(1);
            result[0].InsertText.Should().Contain("[Id]");
            result[0].InsertText.Should().Contain("[Name]");
            result[0].InsertText.Should().Contain("[Status]");
            result[0].InsertText.Should().Contain("VALUES");
        }

        [Fact]
        public void GetCandidates_InsertExpand_AlreadyHasColumns_ReturnsEmpty()
        {
            var sql = "INSERT INTO [dbo].[T] (Id) VALUES (1)";
            var result = _dispatcher.GetCandidates(
                SyntaxNode.InsertTableAfterInsertInto, sql, "", new SyntaxLocationResult(), "");

            result.Should().BeEmpty();
        }

        [Fact]
        public void GetCandidates_AfterFrom_ReturnsTableCandidates()
        {
            _mockCache
                .Setup(c => c.GetMatchingObjects(It.IsAny<string>()))
                .Returns(new List<CompletionCandidate>
                {
                    new CompletionCandidate { DisplayText = "dbo.TableA", FilterText = "TableA", ItemType = CompletionItemType.DatabaseObject, TypeCode = "U" }
                });

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterFromKeyword, "SELECT * FROM ", "Tab", new SyntaxLocationResult(), "TestDB");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "TableA");
        }

        [Fact]
        public void GetCandidates_AfterOrderBy_ReturnsColumnAliasesAndKeywords()
        {
            var sql = "SELECT col1 AS Name, col2 AS Code FROM T ORDER BY ";
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterOrderByKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "Name");
            result.Should().Contain(c => c.FilterText == "Code");
            result.Should().Contain(c => c.FilterText == "ASC");
            result.Should().Contain(c => c.FilterText == "DESC");
        }

        [Fact]
        public void GetCandidates_UserComplexSql_AllThreeSourcesVisible()
        {
            // 用户的复杂SQL场景：FROM #T1 T JOIN (子查询) L JOIN [dbo].[BD_Castles_OrderSoftwareInfo] I
            var sql = @"SELECT L.WO AS WO, I.HQ_Work_Order AS HQ_Work_Order
FROM #T1 T
INNER JOIN (SELECT WO, HQ_Work_Order FROM MESDATA.[dbo].[BD_WOCastlesOrderLink] WITH(NOLOCK)) L ON T.WO = L.WO
INNER JOIN [dbo].[BD_Castles_OrderSoftwareInfo] I WITH(NOLOCK)
    ON L.HQ_Work_Order = I.HQ_Work_Order AND L.VersionNo = I.VersionNo
WHERE ";

            SetupTableColumns("BD_Castles_OrderSoftwareInfo", "HQ_Work_Order", "VersionNo", "HQ_Item_Number");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            // T = 临时表占位
            result.Should().Contain(c => c.DisplayText.Contains("T.") && c.DisplayText.Contains("#临时表#"));

            // L = 子查询字段
            result.Should().Contain(c => c.DisplayText == "L.[WO]");
            result.Should().Contain(c => c.DisplayText == "L.[HQ_Work_Order]");

            // I = 物理表字段
            result.Should().Contain(c => c.DisplayText == "I.[HQ_Work_Order]");
            result.Should().Contain(c => c.DisplayText == "I.[VersionNo]");
            result.Should().Contain(c => c.DisplayText == "I.[HQ_Item_Number]");
        }

        // ===== US-01: WHERE 后子句关键词放开 =====

        [Fact]
        public void GetCandidates_AfterWhere_ContainsSubsequentClauseKeywords()
        {
            var sql = "SELECT * FROM [dbo].[T] A WHERE ";
            SetupTableColumns("T", "Id", "Name");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "", new SyntaxLocationResult(), "");

            // 应包含条件运算符
            result.Should().Contain(c => c.FilterText == "AND");
            result.Should().Contain(c => c.FilterText == "OR");
            result.Should().Contain(c => c.FilterText == "IN");
            // 应包含后续子句关键词
            result.Should().Contain(c => c.FilterText == "GROUP");
            result.Should().Contain(c => c.FilterText == "ORDER");
            result.Should().Contain(c => c.FilterText == "HAVING");
            result.Should().Contain(c => c.FilterText == "OFFSET");
            result.Should().Contain(c => c.FilterText == "UNION");
        }

        [Fact]
        public void GetCandidates_AfterWhere_FilterByGroup_ReturnsGroupKeyword()
        {
            var sql = "SELECT * FROM [dbo].[T] A WHERE ";
            SetupTableColumns("T", "Id");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterWhereKeyword, sql, "GRO", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "GROUP");
        }

        [Fact]
        public void GetCandidates_AfterHaving_ContainsSubsequentClauseKeywords()
        {
            var sql = "SELECT * FROM [dbo].[T] A GROUP BY Id HAVING ";
            SetupTableColumns("T", "Id");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterHavingKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "ORDER");
            result.Should().Contain(c => c.FilterText == "OFFSET");
        }

        [Fact]
        public void GetCandidates_SelectColumnArea_DoesNotContainGroupBy()
        {
            var sql = "SELECT ";
            var result = _dispatcher.GetCandidates(
                SyntaxNode.SelectColumnArea, sql, "", new SyntaxLocationResult(), "");

            // SELECT 列区不应包含 GROUP/ORDER/HAVING（这些是 WHERE 后的子句）
            result.Should().NotContain(c => c.FilterText == "GROUP");
            result.Should().NotContain(c => c.FilterText == "HAVING");
            // 但应包含 AS, TOP 等
            result.Should().Contain(c => c.FilterText == "AS");
        }

        // ===== US-02: TOP 常量不被误识别为列别名 =====

        [Fact]
        public void GetSelectColumnAliases_TopWithoutParens_DoesNotIncludeNumber()
        {
            var parser = new StatementParser();
            var sql = "SELECT TOP 100 * FROM [dbo].[T] ORDER BY ";

            var aliases = parser.GetSelectColumnAliases(sql);

            aliases.Should().NotContain("100");
            aliases.Should().NotContain("TOP");
        }

        [Fact]
        public void GetSelectColumnAliases_TopWithParens_DoesNotIncludeNumber()
        {
            var parser = new StatementParser();
            var sql = "SELECT TOP (50) Col1, Col2 FROM [dbo].[T]";

            var aliases = parser.GetSelectColumnAliases(sql);

            aliases.Should().NotContain("50");
            aliases.Should().Contain("Col1");
            aliases.Should().Contain("Col2");
        }

        [Fact]
        public void GetSelectColumnAliases_TopWithoutParens_WithColumns()
        {
            var parser = new StatementParser();
            var sql = "SELECT TOP 100 Col1, Col2 FROM [dbo].[T]";

            var aliases = parser.GetSelectColumnAliases(sql);

            aliases.Should().NotContain("100");
            aliases.Should().Contain("Col1");
            aliases.Should().Contain("Col2");
        }

        // ===== US-04: DDL 语法树 =====

        [Fact]
        public void GetCandidates_AfterCreateKeyword_ReturnsDdlObjectTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterCreateKeyword, "CREATE ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "TABLE");
            result.Should().Contain(c => c.FilterText == "INDEX");
            result.Should().Contain(c => c.FilterText == "VIEW");
            result.Should().Contain(c => c.FilterText == "PROCEDURE");
            result.Should().Contain(c => c.FilterText == "FUNCTION");
            result.Should().Contain(c => c.FilterText == "TRIGGER");
        }

        [Fact]
        public void GetCandidates_AfterCreateTable_ReturnsTableCandidates()
        {
            _mockCache
                .Setup(c => c.GetAllObjectsOfTypes(It.IsAny<string[]>()))
                .Returns(new List<CompletionCandidate>
                {
                    new CompletionCandidate { DisplayText = "dbo.MyTable", FilterText = "MyTable" }
                });

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterCreateTableKeyword, "CREATE TABLE ", "", new SyntaxLocationResult(), "TestDB");

            result.Should().NotBeEmpty();
        }

        [Fact]
        public void GetCandidates_ColumnDefinitionPosition_ReturnsDataTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.ColumnDefinitionPosition, "CREATE TABLE T (Col1 ", "", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "INT");
            result.Should().Contain(c => c.FilterText == "VARCHAR");
            result.Should().Contain(c => c.FilterText == "DATETIME");
        }

        [Fact]
        public void GetCandidates_ColumnConstraintPosition_ReturnsConstraints()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.ColumnConstraintPosition, "CREATE TABLE T (Col1 INT ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "NOT NULL");
            result.Should().Contain(c => c.FilterText == "PRIMARY KEY");
            result.Should().Contain(c => c.FilterText == "UNIQUE");
            result.Should().Contain(c => c.FilterText == "DEFAULT");
            result.Should().Contain(c => c.FilterText == "IDENTITY");
        }

        [Fact]
        public void GetCandidates_AfterDropKeyword_ReturnsDropObjectTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterDropKeyword, "DROP ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "TABLE");
            result.Should().Contain(c => c.FilterText == "VIEW");
            result.Should().Contain(c => c.FilterText == "INDEX");
            result.Should().Contain(c => c.FilterText == "PROCEDURE");
        }

        [Fact]
        public void GetCandidates_AfterAlterKeyword_ReturnsDdlObjectTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterAlterKeyword, "ALTER ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "TABLE");
            result.Should().Contain(c => c.FilterText == "INDEX");
            result.Should().Contain(c => c.FilterText == "VIEW");
        }

        [Fact]
        public void GetCandidates_AfterAlterTableAction_ReturnsActions()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterAlterTableAction, "ALTER TABLE T ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "ADD");
            result.Should().Contain(c => c.FilterText == "DROP");
        }

        [Fact]
        public void GetCandidates_AfterTruncateKeyword_ReturnsTable()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterTruncateKeyword, "TRUNCATE ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "TABLE");
        }

        [Fact]
        public void GetCandidates_ConstraintTypePosition_ReturnsConstraintTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.ConstraintTypePosition, "CREATE TABLE T (Col1 INT, CONSTRAINT PK_T ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "PRIMARY KEY");
            result.Should().Contain(c => c.FilterText == "UNIQUE");
            result.Should().Contain(c => c.FilterText == "FOREIGN KEY");
            result.Should().Contain(c => c.FilterText == "CHECK");
        }

        // ===== US-05: 数据类型提示 =====

        [Fact]
        public void GetCandidates_DataTypePosition_ReturnsAllDataTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.DataTypePosition, "DECLARE @var ", "", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            // 精确数值
            result.Should().Contain(c => c.FilterText == "INT");
            result.Should().Contain(c => c.FilterText == "BIGINT");
            result.Should().Contain(c => c.FilterText == "DECIMAL");
            result.Should().Contain(c => c.FilterText == "BIT");
            // 字符串
            result.Should().Contain(c => c.FilterText == "VARCHAR");
            result.Should().Contain(c => c.FilterText == "NVARCHAR");
            // 日期
            result.Should().Contain(c => c.FilterText == "DATETIME");
            result.Should().Contain(c => c.FilterText == "DATE");
            // 其他
            result.Should().Contain(c => c.FilterText == "UNIQUEIDENTIFIER");
            result.Should().Contain(c => c.FilterText == "XML");
        }

        [Fact]
        public void GetCandidates_DataTypePosition_FilterByInt_ReturnsIntTypes()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.DataTypePosition, "DECLARE @var ", "INT", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "INT");
        }

        [Fact]
        public void GetCandidates_DataTypePosition_InsertTextHasParentheses()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.DataTypePosition, "DECLARE @var ", "VARCHAR", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.FilterText == "VARCHAR");
            var varcharCandidate = result.First(c => c.FilterText == "VARCHAR");
            varcharCandidate.InsertText.Should().StartWith("VARCHAR(");
        }

        [Fact]
        public void GetCandidates_DataTypePosition_DisplayTextHasCategory()
        {
            var result = _dispatcher.GetCandidates(
                SyntaxNode.DataTypePosition, "DECLARE @var ", "", new SyntaxLocationResult(), "");

            result.Should().Contain(c => c.DisplayText.Contains("[数值]"));
            result.Should().Contain(c => c.DisplayText.Contains("[字符串]"));
            result.Should().Contain(c => c.DisplayText.Contains("[日期]"));
        }

        // ===== Issue 1: ALTER TABLE ... ALTER COLUMN 后提示字段名 =====

        [Fact]
        public void GetCandidates_AfterAlterTableAlterColumn_ReturnsTableColumns()
        {
            var sql = "ALTER TABLE [dbo].[_BD_MD_Project] ALTER COLUMN ";
            SetupTableColumns("_BD_MD_Project", "ProjectId", "ProjectName", "Status");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterAlterTableAlterColumnKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "ProjectId");
            result.Should().Contain(c => c.FilterText == "ProjectName");
            result.Should().Contain(c => c.FilterText == "Status");
        }

        [Fact]
        public void GetCandidates_AfterAlterTableAlterColumn_WithFilter_ReturnsFilteredColumns()
        {
            var sql = "ALTER TABLE [dbo].[_BD_MD_Project] ALTER COLUMN Pro";
            SetupTableColumns("_BD_MD_Project", "ProjectId", "ProjectName", "Status");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterAlterTableAlterColumnKeyword, sql, "Pro", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            // With filter "Pro", matching columns should be present and ranked first
            result.Should().Contain(c => c.FilterText == "ProjectId");
            result.Should().Contain(c => c.FilterText == "ProjectName");
            // ProjectId and ProjectName should have higher match score than Status
            var projectCandidates = result.Where(c => c.FilterText.StartsWith("Pro"));
            var statusCandidate = result.FirstOrDefault(c => c.FilterText == "Status");
            if (statusCandidate != null)
            {
                projectCandidates.First().MatchScore.Should().BeGreaterThan(statusCandidate.MatchScore);
            }
        }

        [Fact]
        public void GetCandidates_AfterDropColumn_ReturnsTableColumns()
        {
            var sql = "ALTER TABLE [dbo].[T] DROP COLUMN ";
            SetupTableColumns("T", "Col1", "Col2", "Col3");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterDropColumnKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "Col1");
            result.Should().Contain(c => c.FilterText == "Col2");
            result.Should().Contain(c => c.FilterText == "Col3");
        }

        [Fact]
        public void GetCandidates_AfterAlterTableAlterColumn_SimpleTableName_ReturnsColumns()
        {
            var sql = "ALTER TABLE MyTable ALTER COLUMN ";
            SetupTableColumns("MyTable", "Id", "Name");

            var result = _dispatcher.GetCandidates(
                SyntaxNode.AfterAlterTableAlterColumnKeyword, sql, "", new SyntaxLocationResult(), "");

            result.Should().NotBeEmpty();
            result.Should().Contain(c => c.FilterText == "Id");
            result.Should().Contain(c => c.FilterText == "Name");
        }
    }
}
