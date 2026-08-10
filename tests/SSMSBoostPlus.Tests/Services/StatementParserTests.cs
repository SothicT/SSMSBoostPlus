using System;
using System.Linq;
using FluentAssertions;
using SSMSBoostPlus.SSMS.Services;
using Xunit;

namespace SSMSBoostPlus.Tests.Services
{
    public class StatementParserTests
    {
        private readonly StatementParser _parser = new();

        [Fact]
        public void Parse_SimpleAlias_ReturnsAlias()
        {
            var result = _parser.Parse("SELECT * FROM T A");
            result.Aliases.Should().ContainKey("A");
            result.Aliases["A"].PhysicalTable.Should().Contain("T");
        }

        [Fact]
        public void Parse_AsAlias_ReturnsAlias()
        {
            var result = _parser.Parse("SELECT * FROM T AS A");
            result.Aliases.Should().ContainKey("A");
        }

        [Fact]
        public void Parse_BracketedTableWithAlias_ReturnsAlias()
        {
            var result = _parser.Parse("SELECT * FROM [dbo].[T] A");
            result.Aliases.Should().ContainKey("A");
            result.Aliases["A"].PhysicalTable.Should().Contain("T");
        }

        [Fact]
        public void Parse_CrossDbTableWithAlias_ReturnsAlias()
        {
            var result = _parser.Parse("SELECT * FROM [DB].[dbo].[T] H");
            result.Aliases.Should().ContainKey("H");
            result.Aliases["H"].DatabaseName.Should().Be("DB");
        }

        [Fact]
        public void Parse_BareTable_ReturnsBareTable()
        {
            var result = _parser.Parse("SELECT * FROM [T] WITH(NOLOCK)");
            result.BareTables.Should().NotBeEmpty();
            result.BareTables[0].DisplayName.Should().Be("T");
        }

        [Fact]
        public void Parse_MultipleTablesWithAliases_ReturnsAll()
        {
            var result = _parser.Parse("SELECT * FROM T1 A, T2 B, T3 C");
            result.Aliases.Should().ContainKeys("A", "B", "C");
        }

        [Fact]
        public void Parse_JoinChain_ReturnsAllAliases()
        {
            var result = _parser.Parse("FROM T1 A JOIN T2 B ON A.id = B.id LEFT JOIN T3 C ON A.id = C.id");
            result.Aliases.Should().ContainKeys("A", "B", "C");
        }

        [Fact]
        public void Parse_TempTableAlias_MarksAsTemp()
        {
            var result = _parser.Parse("SELECT * FROM #T1 T");
            result.Aliases.Should().ContainKey("T");
            result.Aliases["T"].IsTempTable.Should().BeTrue();
        }

        [Fact]
        public void Parse_SubqueryAlias_ReturnsSubqueryWithColumns()
        {
            var result = _parser.Parse("SELECT * FROM (SELECT WO, VersionNo FROM T) L");
            result.SubqueryAliases.Should().HaveCount(1);
            result.SubqueryAliases[0].Alias.Should().Be("L");
            result.SubqueryAliases[0].Columns.Should().Contain(new[] { "WO", "VersionNo" });
        }

        [Fact]
        public void Parse_SubquerySelectStar_MarksAsStarSelect()
        {
            var result = _parser.Parse("SELECT * FROM (SELECT * FROM T) L");
            result.SubqueryAliases.Should().HaveCount(1);
            result.SubqueryAliases[0].HasStarSelect.Should().BeTrue();
            result.SubqueryAliases[0].InnerFromTable.Should().NotBeNull();
        }

        [Fact]
        public void Parse_ComplexMixed_AllSourcesParsed()
        {
            var sql = "SELECT * FROM #T1 T JOIN (SELECT WO FROM T2) L ON T.WO = L.WO JOIN T3 I ON L.WO = I.WO";
            var result = _parser.Parse(sql);

            result.Aliases.Should().ContainKey("T");
            result.Aliases["T"].IsTempTable.Should().BeTrue();

            result.Aliases.Should().ContainKey("I");

            result.SubqueryAliases.Should().HaveCount(1);
            result.SubqueryAliases[0].Alias.Should().Be("L");
            result.SubqueryAliases[0].Columns.Should().Contain("WO");
        }

        [Fact]
        public void Parse_UserComplexSql_AllAliasesParsed()
        {
            // 用户提供的复杂SQL
            var sql = @"SELECT TOP 20
    CAST(B.[ORDER_TYPE_ID] as nvarchar(500)) AS [ORDER_TYPE_ID],
    CAST(A.[HEADER_ID] AS nvarchar(500)) AS [HEADER_ID]
FROM [BD_ERP_SO_HEADER] B
INNER JOIN [dbo].[BD_ERP_SO_LINE] A ON A.[CUST_PO_NUMBER]=B.[CUST_PO_NUMBER]
WHERE A.CUST_PO_NUMBER IN(SELECT PrimaryKey FROM #T1);";

            var result = _parser.Parse(sql);

            result.Aliases.Should().ContainKey("A");
            result.Aliases["A"].PhysicalTable.Should().Contain("BD_ERP_SO_LINE");

            result.Aliases.Should().ContainKey("B");
            result.Aliases["B"].PhysicalTable.Should().Contain("BD_ERP_SO_HEADER");
        }

        [Fact]
        public void GetUpdateTargetTable_SimpleUpdate_ReturnsTableName()
        {
            var result = _parser.GetUpdateTargetTable("UPDATE T SET col = 1");
            result.Should().NotBeNull();
            result.Value.tableName.Should().Contain("T");
        }

        [Fact]
        public void GetUpdateTargetTable_UpdateWithAlias_ReturnsTableNameAndAlias()
        {
            var result = _parser.GetUpdateTargetTable("UPDATE [dbo].[T] A SET col = 1");
            result.Should().NotBeNull();
            result.Value.alias.Should().Be("A");
        }

        [Fact]
        public void GetInsertTargetTable_SimpleInsert_ReturnsTableName()
        {
            var result = _parser.GetInsertTargetTable("INSERT INTO [dbo].[BD_CF_EXTEND_Setting]");
            result.Should().NotBeNull();
            result.Should().Contain("BD_CF_EXTEND_Setting");
        }

        [Fact]
        public void GetSelectColumnAliases_WithAsAlias_ReturnsAliases()
        {
            var sql = "SELECT col1 AS Alias1, col2 AS Alias2 FROM T";
            var result = _parser.GetSelectColumnAliases(sql);
            result.Should().Contain(new[] { "Alias1", "Alias2" });
        }

        [Fact]
        public void GetSelectColumnAliases_WithoutAlias_ReturnsColumnNames()
        {
            var sql = "SELECT col1, col2 FROM T";
            var result = _parser.GetSelectColumnAliases(sql);
            result.Should().Contain(new[] { "col1", "col2" });
        }

        [Fact]
        public void Parse_CrossDbNoBracket_ReturnsAlias()
        {
            var result = _parser.Parse("SELECT * FROM MESDATA.dbo.BD_WOCastlesOrderLink L");
            result.Aliases.Should().ContainKey("L");
            result.Aliases["L"].DatabaseName.Should().Be("MESDATA");
        }
    }
}
