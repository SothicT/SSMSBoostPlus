using System;
using System.Linq;
using FluentAssertions;
using SSMSBoostPlus.SSMS.Services;
using Xunit;

namespace SSMSBoostPlus.Tests.Services
{
    public class SqlBreakerTests
    {
        private readonly SqlBreaker _breaker = new();

        [Fact]
        public void LocateCurrentStatement_SingleSelect_ReturnsWholeStatement()
        {
            var lines = new[] { "SELECT * FROM T" };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 0);

            start.Should().Be(0);
            end.Should().Be(0);
            type.Should().Be(StatementType.Select);
        }

        [Fact]
        public void LocateCurrentStatement_TwoSelects_LocatesSecond()
        {
            var lines = new[]
            {
                "SELECT * FROM T1",
                "SELECT * FROM T2"
            };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 1);

            start.Should().Be(1);
            end.Should().Be(1);
            type.Should().Be(StatementType.Select);
        }

        [Fact]
        public void LocateCurrentStatement_MultiLineSelect_ReturnsFullStatement()
        {
            var lines = new[]
            {
                "SELECT A.col1,",
                "       B.col2",
                "FROM T1 A",
                "JOIN T2 B ON A.id = B.id",
                "WHERE A.flag = 1"
            };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 2);

            start.Should().Be(0);
            end.Should().Be(4);
            type.Should().Be(StatementType.Select);
        }

        [Fact]
        public void LocateCurrentStatement_WithGoSeparator_SplitsCorrectly()
        {
            var lines = new[]
            {
                "SELECT 1",
                "GO",
                "SELECT 2"
            };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 2);

            start.Should().Be(2);
            end.Should().Be(2);
            type.Should().Be(StatementType.Select);
        }

        [Fact]
        public void LocateCurrentStatement_WithSubquery_TreatsAsOneStatement()
        {
            var lines = new[]
            {
                "SELECT * FROM (",
                "SELECT * FROM T2",
                ") sub"
            };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 1);

            start.Should().Be(0);
            end.Should().Be(2);
            type.Should().Be(StatementType.Select);
        }

        [Fact]
        public void LocateCurrentStatement_WithCTE_TreatsAsOneStatement()
        {
            var lines = new[]
            {
                "WITH cte AS (",
                "  SELECT * FROM T",
                ")",
                "SELECT * FROM cte"
            };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 3);

            start.Should().Be(0);
            end.Should().Be(3);
            type.Should().Be(StatementType.With);
        }

        [Fact]
        public void DetectStatementType_Select_ReturnsSelect()
        {
            _breaker.DetectStatementType("SELECT * FROM T").Should().Be(StatementType.Select);
        }

        [Fact]
        public void DetectStatementType_Insert_ReturnsInsert()
        {
            _breaker.DetectStatementType("INSERT INTO T VALUES (1)").Should().Be(StatementType.Insert);
        }

        [Fact]
        public void DetectStatementType_Update_ReturnsUpdate()
        {
            _breaker.DetectStatementType("UPDATE T SET col = 1").Should().Be(StatementType.Update);
        }

        [Fact]
        public void DetectStatementType_Delete_ReturnsDelete()
        {
            _breaker.DetectStatementType("DELETE FROM T").Should().Be(StatementType.Delete);
        }

        [Fact]
        public void DetectStatementType_Exec_ReturnsExec()
        {
            _breaker.DetectStatementType("EXEC sp_test").Should().Be(StatementType.Exec);
        }

        [Fact]
        public void DetectStatementType_With_ReturnsWith()
        {
            _breaker.DetectStatementType("WITH cte AS (...) SELECT * FROM cte").Should().Be(StatementType.With);
        }

        [Fact]
        public void LocateCurrentStatement_SelectColumnsBeforeFrom_LocatesFullStatement()
        {
            // 用户提供的复杂SQL：SELECT列表在前，FROM在后
            var lines = new[]
            {
                "SELECT TOP 20",
                "    CAST(B.[ORDER_TYPE_ID] as nvarchar(500)) AS [ORDER_TYPE_ID],",
                "    CAST(B.[ORDER_NUMBER] as nvarchar(500)) AS [ORDER_NUMBER]",
                "FROM [BD_ERP_SO_HEADER] B",
                "INNER JOIN [dbo].[BD_ERP_SO_LINE] A ON A.[CUST_PO_NUMBER]=B.[CUST_PO_NUMBER]",
                "WHERE A.CUST_PO_NUMBER IN(SELECT PrimaryKey FROM #T1);"
            };
            var (start, end, text, type) = _breaker.LocateCurrentStatement(lines, 1);

            start.Should().Be(0);
            end.Should().Be(5);
            type.Should().Be(StatementType.Select);
        }
    }
}
