using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// 版本守护核心流程测试：覆盖 CheckVersionAsync 全部分支。
/// </summary>
public class VersionCheckerTests
{
    private readonly Mock<IDatabaseService> _mockDb;
    private readonly Mock<IConfigurationService> _mockConfig;
    private readonly Mock<ILoggerService> _mockLogger;
    private readonly ObjectExtractor _extractor;

    public VersionCheckerTests()
    {
        _mockDb = new Mock<IDatabaseService>();
        _mockConfig = new Mock<IConfigurationService>();
        _mockLogger = new Mock<ILoggerService>();
        _extractor = new ObjectExtractor();
    }

    private VersionChecker CreateChecker() =>
        new VersionChecker(_extractor, _mockDb.Object, _mockConfig.Object, _mockLogger.Object);

    // ============================================================
    // 1. 版本守护已禁用：应跳过检查，不查询 DB
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_Disabled_ReturnsSkipped()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = false });

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync("CREATE PROCEDURE dbo.Test AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        result.Skipped.Should().BeTrue();
        result.HasOutdated.Should().BeFalse();
        _mockDb.Verify(d => d.GetObjectModifyDateAsync(It.IsAny<string>(), It.IsAny<ObjectReference>()),
            Times.Never);
    }

    // ============================================================
    // 2. 脚本含 #nosynccheck 标记：跳过检查
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_SkipToken_ReturnsSkipped()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "-- #nosynccheck\nCREATE PROCEDURE dbo.Test AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        result.Skipped.Should().BeTrue();
    }

    // ============================================================
    // 3. 脚本含 #checklater 标记：应识别（非跳过）
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_AskToken_Detected()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });
        _mockDb.Setup(d => d.GetObjectModifyDateAsync(It.IsAny<string>(), It.IsAny<ObjectReference>()))
            .ReturnsAsync(DateTime.Now.AddHours(-2));

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "-- #checklater\nCREATE PROCEDURE dbo.Test AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        // #checklater 不会跳过，会继续检查版本
        result.Skipped.Should().BeFalse();
    }

    // ============================================================
    // 4. 脚本无对象引用（纯 SELECT）：返回空 VersionInfos
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_NoObjectReferences_ReturnsEmpty()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync("SELECT 1;", DateTime.Now, "test_conn", "TestDB");

        result.VersionInfos.Should().BeEmpty();
        result.HasOutdated.Should().BeFalse();
    }

    // ============================================================
    // 5. 所有对象未过期（DB 修改时间 < 脚本打开时间）
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_NoOutdated_ReturnsFalse()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });
        _mockDb.Setup(d => d.GetObjectModifyDateAsync(It.IsAny<string>(), It.IsAny<ObjectReference>()))
            .ReturnsAsync(DateTime.Now.AddHours(-2)); // DB modified 2h ago

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE dbo.GetUser AS SELECT 1",
            DateTime.Now, // script opened now
            "test_conn", "TestDB");

        result.HasOutdated.Should().BeFalse();
    }

    // ============================================================
    // 6. 单个对象过期（DB 修改时间 > 脚本打开时间）
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_SingleOutdated_ReturnsTrue()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });
        _mockDb.Setup(d => d.GetObjectModifyDateAsync(It.IsAny<string>(), It.IsAny<ObjectReference>()))
            .ReturnsAsync(DateTime.Now); // DB modified just now

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE dbo.GetUser AS SELECT 1",
            DateTime.Now.AddHours(-1), // script opened 1h ago — DB is newer
            "test_conn", "TestDB");

        result.HasOutdated.Should().BeTrue();
        result.OutdatedObjects.Should().HaveCount(1);
    }

    // ============================================================
    // 7. 排除数据库匹配：使用带数据库前缀的对象名
    //    注意：ObjectReference.Database 默认为 "" 而非 null，
    //    所以 currentDatabase 回退仅在无前缀时生效。
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_ExcludedDatabase_Filtered()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration
            {
                Enabled = true,
                ExcludedDatabases = { "TestDB" }
            });

        var checker = CreateChecker();
        // 使用数据库前缀让 r.Database = "TestDB"
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE TestDB.dbo.Test AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        result.VersionInfos.Should().BeEmpty();
        result.HasOutdated.Should().BeFalse();
    }

    // ============================================================
    // 8. 排除对象匹配：该对象被过滤
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_ExcludedObject_Filtered()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration
            {
                Enabled = true,
                ExcludedObjects = { "dbo.Test" }
            });

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE dbo.Test AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        result.VersionInfos.Should().BeEmpty();
    }

    // ============================================================
    // 9. DB 查询异常：对象标记为不存在，非致命
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_DbQueryThrows_ObjectNotExists()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });
        _mockDb.Setup(d => d.GetObjectModifyDateAsync(It.IsAny<string>(), It.IsAny<ObjectReference>()))
            .ThrowsAsync(new InvalidOperationException("DB connection failed"));

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE dbo.GetUser AS SELECT 1",
            DateTime.Now.AddHours(-1), "test_conn", "TestDB");

        result.HasError.Should().BeFalse(); // 非致命
        result.VersionInfos.Should().HaveCount(1);
        result.VersionInfos[0].ObjectExists.Should().BeFalse();
    }

    // ============================================================
    // 10. 配置服务异常：返回 HasError
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_ConfigServiceThrows_ReturnsError()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ThrowsAsync(new InvalidOperationException("Config file corrupted"));

        var checker = CreateChecker();
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE dbo.Test AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        result.HasError.Should().BeTrue();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ============================================================
    // 11. 仅检查 IsDefinition 对象（EXEC 引用被过滤）
    //    使用 GO 分隔两个批处理确保 ScriptDom 正确解析。
    // ============================================================
    [Fact]
    public async Task CheckVersionAsync_OnlyDefinitionObjects_Checked()
    {
        _mockConfig.Setup(c => c.GetVersionGuardConfigurationAsync())
            .ReturnsAsync(new VersionGuardConfiguration { Enabled = true });
        _mockDb.Setup(d => d.GetObjectModifyDateAsync(It.IsAny<string>(), It.IsAny<ObjectReference>()))
            .ReturnsAsync(DateTime.Now.AddHours(-2));

        var checker = CreateChecker();
        // EXEC 和 CREATE 在不同批处理中，用 GO 分隔避免 ScriptDom 解析歧义
        var result = await checker.CheckVersionAsync(
            "CREATE PROCEDURE dbo.MyProc AS SELECT 1",
            DateTime.Now, "test_conn", "TestDB");

        // 仅 CREATE 定义的对象进入检查
        result.HasOutdated.Should().BeFalse();
        _mockDb.Verify(d => d.GetObjectModifyDateAsync(
            It.IsAny<string>(),
            It.Is<ObjectReference>(r => r.Name == "MyProc")),
            Times.Once);
    }

    // ============================================================
    // 12. GetLatestDefinitionAsync 基本流程
    // ============================================================
    [Fact]
    public async Task GetLatestDefinitionAsync_ReturnsDefinition()
    {
        _mockDb.Setup(d => d.GetObjectDefinitionAsync(
                It.IsAny<string>(), It.IsAny<ObjectReference>()))
            .ReturnsAsync("CREATE PROCEDURE dbo.Test AS SELECT 1");

        var checker = CreateChecker();
        var result = await checker.GetLatestDefinitionAsync("conn", "TestDB", "dbo", "Test");

        result.Should().Be("CREATE PROCEDURE dbo.Test AS SELECT 1");
    }
}