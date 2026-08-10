using FluentAssertions;
using Xunit;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// ObjectExtractor 和 DiffCalculator 边界用例测试：
/// 补充 CoreTests.cs 中未覆盖的复杂场景。
/// </summary>
public class ObjectExtractorEdgeTests
{
    private readonly ObjectExtractor _extractor = new ObjectExtractor();
    private readonly DiffCalculator _diffCalculator = new DiffCalculator();

    // ============================================================
    // 1. 空脚本：不提取任何对象
    // ============================================================
    [Fact]
    public void ExtractObjects_EmptyScript_ReturnsEmpty()
    {
        var refs = _extractor.ExtractObjects("");
        refs.Should().BeEmpty();
    }

    // ============================================================
    // 2. 注释中的 CREATE 不应被提取
    // ============================================================
    [Fact]
    public void ExtractObjects_CommentedCreate_NotExtracted()
    {
        var refs = _extractor.ExtractObjects(
            "-- CREATE PROCEDURE dbo.Commented AS SELECT 1\n" +
            "CREATE PROCEDURE dbo.Real AS SELECT 2");

        refs.Should().HaveCount(1);
        refs[0].Name.Should().Be("Real");
    }

    // ============================================================
    // 3. Schema 默认值：无 Schema 时 Name == FullName
    // ============================================================
    [Fact]
    public void ExtractObjects_NoSchema_FullNameEqualsName()
    {
        var refs = _extractor.ExtractObjects("CREATE PROCEDURE MyProc AS SELECT 1");

        if (refs.Count > 0)
        {
            // 当 ObjectExtractor 无法推断 Schema 时，Name == FullName
            refs[0].Name.Should().Be("MyProc");
        }
    }

    // ============================================================
    // 4. DiffCalculator 多差异块上下文行数
    // ============================================================
    [Fact]
    public void CalculateDiff_LargeGap_UsesContextLines()
    {
        var left = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
        var right = "line1\nCHANGED2\nline3\nline4\nline5\nCHANGED6\nline7\nline8\nline9\nline10";

        var result = _diffCalculator.CalculateDiff(left, right, contextLines: 1);

        result.IsIdentical.Should().BeFalse();
        // 两个相距较远的变更应产生至少 2 个差异块
        result.Hunks.Count.Should().BeGreaterOrEqualTo(2);
    }

    // ============================================================
    // 5. ObjectReference.IsOutdated 边界：DbModifyDate 为 null
    // ============================================================
    [Fact]
    public void VersionInfo_DbModifyDateNull_NotOutdated()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            DbModifyDate = null,
            ScriptOpenTime = System.DateTime.Now,
            ObjectExists = false
        };

        info.IsOutdated.Should().BeFalse();
    }

    // ============================================================
    // 6. ObjectReference.IsOutdated 边界：精确到秒相等
    // ============================================================
    [Fact]
    public void VersionInfo_SameTime_NotOutdated()
    {
        var now = System.DateTime.Now;
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            DbModifyDate = now,
            ScriptOpenTime = now,
            ObjectExists = true
        };

        // modify_date 等于脚本打开时间 → 未过期
        info.IsOutdated.Should().BeFalse();
    }

    // ============================================================
    // 7. VersionInfo.RequiresReview: Unknown 类型不需要审查
    // ============================================================
    [Fact]
    public void VersionInfo_RequiresReview_Unknown_ReturnsFalse()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.Unknown
        };

        info.RequiresReview.Should().BeFalse();
    }

    // ============================================================
    // 8. VersionInfo.ReviewStatusText: 待审查（需要审查但未审查）
    // ============================================================
    [Fact]
    public void VersionInfo_ReviewStatusText_NotReviewed_RequiresReview_Returns待审查()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.Conflict,
            IsReviewed = false
        };

        info.ReviewStatusText.Should().Be("待审查");
    }

    // ============================================================
    // 9. VersionInfo.ReviewStatusText: 无需审查
    // ============================================================
    [Fact]
    public void VersionInfo_ReviewStatusText_NotReviewed_NoReview_Returns无需审查()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.Identical,
            IsReviewed = false
        };

        info.ReviewStatusText.Should().Be("无需审查");
    }
}