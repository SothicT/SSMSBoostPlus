using System;
using Xunit;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.Tests;

public class ConnectionStringBuilderTests
{
    // ============================================================
    //  BuildConnectionString tests
    // ============================================================

    [Fact]
    public void BuildConnectionString_IntegratedSecurity_ProducesValidString()
    {
        var connStr = ConnectionStringBuilder.BuildConnectionString(
            "localhost\\SQLEXPRESS", "MyDatabase", integratedSecurity: true);

        Assert.Contains("Data Source=localhost\\SQLEXPRESS", connStr);
        Assert.Contains("Initial Catalog=MyDatabase", connStr);
        Assert.Contains("Integrated Security=True", connStr);
    }

    [Fact]
    public void BuildConnectionString_SqlAuth_ProducesValidString()
    {
        var connStr = ConnectionStringBuilder.BuildConnectionString(
            "myserver", "TestDb", integratedSecurity: false, userId: "sa", password: "pass123");

        Assert.Contains("Data Source=myserver", connStr);
        Assert.Contains("Initial Catalog=TestDb", connStr);
        Assert.Contains("User ID=sa", connStr);
        Assert.Contains("Password=pass123", connStr);
        Assert.Contains("Integrated Security=False", connStr);
    }

    [Fact]
    public void BuildConnectionString_NoDatabase_ProducesValidString()
    {
        var connStr = ConnectionStringBuilder.BuildConnectionString(
            "myserver", integratedSecurity: true);

        Assert.Contains("Data Source=myserver", connStr);
        Assert.Contains("Integrated Security=True", connStr);
        Assert.DoesNotContain("Initial Catalog", connStr);
    }

    [Fact]
    public void BuildConnectionString_EmptyServer_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ConnectionStringBuilder.BuildConnectionString(""));
    }

    [Fact]
    public void BuildConnectionString_NullServer_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ConnectionStringBuilder.BuildConnectionString(null!));
    }

    // ============================================================
    //  ParseDatabaseName tests
    // ============================================================

    [Fact]
    public void ParseDatabaseName_ValidConnectionString_ReturnsDatabaseName()
    {
        var connStr = "Data Source=localhost;Initial Catalog=MyDatabase;Integrated Security=True";
        Assert.Equal("MyDatabase", ConnectionStringBuilder.ParseDatabaseName(connStr));
    }

    [Fact]
    public void ParseDatabaseName_DatabaseKeyword_ReturnsDatabaseName()
    {
        var connStr = "Server=localhost;Database=TestDb;Integrated Security=True";
        Assert.Equal("TestDb", ConnectionStringBuilder.ParseDatabaseName(connStr));
    }

    [Fact]
    public void ParseDatabaseName_NoDatabase_ReturnsNull()
    {
        var connStr = "Data Source=localhost;Integrated Security=True";
        Assert.Null(ConnectionStringBuilder.ParseDatabaseName(connStr));
    }

    [Fact]
    public void ParseDatabaseName_EmptyString_ReturnsNull()
    {
        Assert.Null(ConnectionStringBuilder.ParseDatabaseName(""));
    }

    [Fact]
    public void ParseDatabaseName_Null_ReturnsNull()
    {
        Assert.Null(ConnectionStringBuilder.ParseDatabaseName(null!));
    }

    [Fact]
    public void ParseDatabaseName_MalformedString_FallbackParsing()
    {
        var connStr = "Server=myserver;Database=FallbackDb;SomeGarbage";
        Assert.Equal("FallbackDb", ConnectionStringBuilder.ParseDatabaseName(connStr));
    }

    // ============================================================
    //  ParseServerName tests
    // ============================================================

    [Fact]
    public void ParseServerName_ValidConnectionString_ReturnsServerName()
    {
        var connStr = "Data Source=MYPC\\SQLEXPRESS;Initial Catalog=Test;Integrated Security=True";
        Assert.Equal("MYPC\\SQLEXPRESS", ConnectionStringBuilder.ParseServerName(connStr));
    }

    [Fact]
    public void ParseServerName_ServerKeyword_ReturnsServerName()
    {
        var connStr = "Server=localhost;Database=Test";
        Assert.Equal("localhost", ConnectionStringBuilder.ParseServerName(connStr));
    }

    [Fact]
    public void ParseServerName_EmptyString_ReturnsNull()
    {
        Assert.Null(ConnectionStringBuilder.ParseServerName(""));
    }

    // ============================================================
    //  UsesIntegratedSecurity tests
    // ============================================================

    [Fact]
    public void UsesIntegratedSecurity_WindowsAuth_ReturnsTrue()
    {
        var connStr = "Data Source=localhost;Integrated Security=True";
        Assert.True(ConnectionStringBuilder.UsesIntegratedSecurity(connStr));
    }

    [Fact]
    public void UsesIntegratedSecurity_SqlAuth_ReturnsFalse()
    {
        var connStr = "Data Source=localhost;User ID=sa;Password=pass;Integrated Security=False";
        Assert.False(ConnectionStringBuilder.UsesIntegratedSecurity(connStr));
    }

    [Fact]
    public void UsesIntegratedSecurity_EmptyString_ReturnsFalse()
    {
        Assert.False(ConnectionStringBuilder.UsesIntegratedSecurity(""));
    }

    // ============================================================
    //  Round-trip tests
    // ============================================================

    [Fact]
    public void BuildThenParse_RoundTrip_IntegratedSecurity()
    {
        var connStr = ConnectionStringBuilder.BuildConnectionString(
            "localhost\\SQLEXPRESS", "MyDb", integratedSecurity: true);

        Assert.Equal("MyDb", ConnectionStringBuilder.ParseDatabaseName(connStr));
        Assert.Equal("localhost\\SQLEXPRESS", ConnectionStringBuilder.ParseServerName(connStr));
        Assert.True(ConnectionStringBuilder.UsesIntegratedSecurity(connStr));
    }

    [Fact]
    public void BuildThenParse_RoundTrip_SqlAuth()
    {
        var connStr = ConnectionStringBuilder.BuildConnectionString(
            "prodserver", "ProdDb", integratedSecurity: false, userId: "admin", password: "secret");

        Assert.Equal("ProdDb", ConnectionStringBuilder.ParseDatabaseName(connStr));
        Assert.Equal("prodserver", ConnectionStringBuilder.ParseServerName(connStr));
        Assert.False(ConnectionStringBuilder.UsesIntegratedSecurity(connStr));
    }
}
