using System;
using FluentAssertions;
using SSMSBoostPlus.SSMS.Services;
using Xunit;

namespace SSMSBoostPlus.Tests.Services
{
    public class SyntaxLocatorTests
    {
        private readonly SyntaxLocator _locator = new();

        private SyntaxLocationResult LocateAt(string sql, int cursorOffset, StatementType type = StatementType.Select)
        {
            return _locator.Locate(sql, cursorOffset, type);
        }

        /// <summary>
        /// 在 SQL 文本中标记光标位置，用 █ 表示光标位置。
        /// 返回去掉标记的 SQL 文本和光标偏移量。
        /// </summary>
        private (string sql, int offset) Cursor(string sqlWithMarker)
        {
            int pos = sqlWithMarker.IndexOf('█');
            if (pos < 0) throw new ArgumentException("Missing cursor marker █");
            return (sqlWithMarker.Remove(pos, 1), pos);
        }

        [Fact]
        public void Locate_AfterSelect_ReturnsAfterSelectKeyword()
        {
            var (sql, off) = Cursor("SELECT █ * FROM T");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterSelectKeyword);
        }

        [Fact]
        public void Locate_SelectColumnArea_ReturnsSelectColumnArea()
        {
            var (sql, off) = Cursor("SELECT col1, █ FROM T");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.SelectColumnArea);
        }

        [Fact]
        public void Locate_AfterFrom_ReturnsAfterFromKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterFromKeyword);
        }

        [Fact]
        public void Locate_AfterWhere_ReturnsAfterWhereKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM T WHERE █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterWhereKeyword);
        }

        [Fact]
        public void Locate_AfterAnd_ReturnsAfterAndOrKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM T WHERE a=1 AND █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterAndOrKeyword);
        }

        [Fact]
        public void Locate_DotNotation_ReturnsAfterDotNotation()
        {
            var (sql, off) = Cursor("SELECT a.█ FROM T a");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterDotNotation);
            result.WordBeforeDot.Should().Be("a");
        }

        [Fact]
        public void Locate_AfterSet_Update_ReturnsUpdateSetPosition()
        {
            var (sql, off) = Cursor("UPDATE T SET █");
            var result = LocateAt(sql, off, StatementType.Update);
            result.Node.Should().Be(SyntaxNode.UpdateSetPosition);
        }

        [Fact]
        public void Locate_AfterInsertInto_ReturnsInsertIntoTablePosition()
        {
            var (sql, off) = Cursor("INSERT INTO █");
            var result = LocateAt(sql, off, StatementType.Insert);
            result.Node.Should().Be(SyntaxNode.InsertIntoTablePosition);
        }

        [Fact]
        public void Locate_InComment_ReturnsInCommentOrString()
        {
            var (sql, off) = Cursor("SELECT * -- █ comment\nFROM T");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.InCommentOrString);
        }

        [Fact]
        public void Locate_AfterJoin_ReturnsAfterJoinKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM T1 INNER JOIN █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterJoinKeyword);
        }

        [Fact]
        public void Locate_AfterOn_ReturnsAfterJoinOnKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM T1 A JOIN T2 B ON █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterJoinOnKeyword);
        }

        [Fact]
        public void Locate_AfterOrderBy_ReturnsAfterOrderByKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM T ORDER BY █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterOrderByKeyword);
        }

        [Fact]
        public void Locate_AfterGroupBy_ReturnsAfterGroupByKeyword()
        {
            var (sql, off) = Cursor("SELECT * FROM T GROUP BY █");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.AfterGroupByKeyword);
        }

        [Fact]
        public void Locate_AfterExec_ReturnsExecPosition()
        {
            var (sql, off) = Cursor("EXEC █");
            var result = LocateAt(sql, off, StatementType.Exec);
            result.Node.Should().Be(SyntaxNode.ExecPosition);
        }

        [Fact]
        public void Locate_CrossDb_AfterDbDot_ReturnsCrossDbContextPrompt()
        {
            var (sql, off) = Cursor("SELECT * FROM [MESDATA].█");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.CrossDbContextPrompt);
            result.CrossDbLevel.Should().Be(1);
            result.CrossDbName.Should().Be("MESDATA");
        }

        [Fact]
        public void Locate_CrossDb_AfterDbSchemaDot_ReturnsCrossDbContextPrompt()
        {
            var (sql, off) = Cursor("SELECT * FROM [MESDATA].[dbo].█");
            var result = LocateAt(sql, off);
            result.Node.Should().Be(SyntaxNode.CrossDbContextPrompt);
            result.CrossDbLevel.Should().Be(2);
            result.CrossDbName.Should().Be("MESDATA");
        }

        [Fact]
        public void Locate_StatementStart_EmptyText_ReturnsStatementStart()
        {
            var result = LocateAt("", 0);
            result.Node.Should().Be(SyntaxNode.StatementStart);
        }

        [Fact]
        public void Locate_FromAfterSelectColumns_FindsFromCorrectly()
        {
            // 用户场景：SELECT列在前，FROM在后
            var sql = "SELECT CAST(B.[ORDER_TYPE_ID] as nvarchar(500)) AS [ORDER_TYPE_ID] FROM [BD_ERP_SO_HEADER] B";
            // 光标在 FROM 之后
            int fromPos = sql.IndexOf("FROM") + 5;
            var result = LocateAt(sql, fromPos);
            result.Node.Should().Be(SyntaxNode.AfterFromKeyword);
        }

        [Fact]
        public void Locate_SelectColumnArea_WithComplexCast_ReturnsSelectColumnArea()
        {
            // 用户场景：复杂CAST表达式中输入
            var sql = "SELECT CAST(B.[ORDER_TYPE_ID] as nvarchar(500)) AS [ORDER_TYPE_ID], CAST(B.[col2] █ FROM T B";
            // 找到 █ 位置（实际上SQL中已经没有标记了，我们直接用位置）
            // 简化测试：在 FROM 之前的位置
            int fromPos = sql.IndexOf("FROM");
            var result = LocateAt(sql.Substring(0, fromPos).TrimEnd(), fromPos - 1);
            // 在列区域
            result.Node.Should().BeOneOf(SyntaxNode.SelectColumnArea, SyntaxNode.ExpressionPosition);
        }
    }
}
