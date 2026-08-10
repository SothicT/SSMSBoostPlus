using FluentAssertions;
using Xunit;
using SSMSBoostPlus.Models;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// LocalHistoryConfig 新增属性默认值测试。
/// 覆盖 P0 钉住、防抖、权限隔离、下拉框优化等重构引入的配置项。
/// </summary>
public class LocalHistoryConfigTests
{
    [Fact]
    public void LocalHistoryConfig_Defaults_AreCorrect()
    {
        var config = new LocalHistoryConfig();

        // 既有默认值保持不变（向后兼容）
        config.Enabled.Should().BeTrue();
        config.TableName.Should().Be("DBTrigger_Object_Version_Table");
        config.DatabaseName.Should().Be("MESBASE");
        config.MaxVersionsToLoad.Should().Be(100);
        config.AutoOpenOnExternalChange.Should().BeFalse();

        // 新增属性默认值
        config.AutoFollowEnabled.Should().BeTrue("默认应启用窗口切换跟随");
        config.DebounceMs.Should().Be(300, "默认防抖间隔 300ms");
        config.ShowConnectionStatus.Should().BeTrue("默认显示连接状态");
        config.AllowedDatabases.Should().NotBeNull().And.BeEmpty("默认无白名单限制");
        config.ObjectListTopLimit.Should().Be(200, "默认对象列表 Top 限制 200，替代硬编码 100");
        config.DatabaseListCacheMinutes.Should().Be(5, "默认数据库列表缓存 5 分钟");
    }

    [Fact]
    public void LocalHistoryConfig_AllowedDatabases_CanBePopulated()
    {
        var config = new LocalHistoryConfig
        {
            AllowedDatabases = new System.Collections.Generic.List<string> { "MESDB_Prod", "MESDB_Test" }
        };

        config.AllowedDatabases.Should().HaveCount(2);
        config.AllowedDatabases.Should().Contain("MESDB_Prod");
        config.AllowedDatabases.Should().Contain("MESDB_Test");
    }

    [Fact]
    public void LocalHistoryConfig_DatabaseName_CanBeChanged()
    {
        var config = new LocalHistoryConfig
        {
            DatabaseName = "MESBASE_V2"
        };

        config.DatabaseName.Should().Be("MESBASE_V2");
    }
}

/// <summary>
/// ObjectSummary.TotalCount 属性测试。
/// 覆盖 P0 下拉框优化中用于显示截断提示的 TotalCount 字段。
/// </summary>
public class ObjectSummaryTests
{
    [Fact]
    public void ObjectSummary_Defaults_AreCorrect()
    {
        var summary = new SSMSBoostPlus.VersionHistory.Models.ObjectSummary();

        summary.ObjectName.Should().Be("");
        summary.ObjectType.Should().Be("");
        summary.DatabaseName.Should().Be("");
        summary.VersionCount.Should().Be(0);
        summary.LastModified.Should().Be(default);
        summary.LastModifiedBy.Should().Be("");
        summary.TotalCount.Should().Be(0, "TotalCount 默认应为 0");
    }

    [Fact]
    public void ObjectSummary_TotalCount_CanBeSet()
    {
        var summary = new SSMSBoostPlus.VersionHistory.Models.ObjectSummary
        {
            ObjectName = "usp_GetData",
            ObjectType = "PROCEDURE",
            DatabaseName = "MESDB_Prod",
            VersionCount = 12,
            TotalCount = 523
        };

        summary.TotalCount.Should().Be(523);
        summary.ObjectName.Should().Be("usp_GetData");
    }
}
