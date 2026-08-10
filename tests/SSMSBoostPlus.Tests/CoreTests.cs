using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Shared;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Tests;

public class ObjectExtractorTests
{
    [Fact]
    public void ExtractObjects_CreateProcedure_ExtractsCorrectly()
    {
        var extractor = new ObjectExtractor();
        var sql = @"
CREATE PROCEDURE dbo.GetUserById
    @UserId INT
AS
    SELECT * FROM Users WHERE Id = @UserId";

        var refs = extractor.ExtractObjects(sql);

        Assert.Single(refs);
        Assert.Equal("dbo.GetUserById", refs[0].FullName);
        Assert.Equal("GetUserById", refs[0].Name);
        Assert.Equal("dbo", refs[0].Schema);
    }

    [Fact]
    public void ExtractObjects_AlterFunction_ExtractsCorrectly()
    {
        var extractor = new ObjectExtractor();
        var sql = @"
ALTER FUNCTION dbo.CalculateTotal(@OrderId INT)
RETURNS DECIMAL
AS
BEGIN
    RETURN 0
END";

        var refs = extractor.ExtractObjects(sql);

        Assert.Single(refs);
        Assert.Equal("dbo.CalculateTotal", refs[0].FullName);
    }

    [Fact]
    public void ExtractObjects_MultipleObjects_ExtractsAll()
    {
        var extractor = new ObjectExtractor();
        var sql = @"
EXEC dbo.GetUserById @UserId = 1
GO
CREATE VIEW dbo.ActiveUsers AS SELECT * FROM Users WHERE Active = 1";

        var refs = extractor.ExtractObjects(sql);

        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void DetectTokens_SkipToken_ReturnsSkip()
    {
        var extractor = new ObjectExtractor();
        var sql = "-- #nosynccheck\nCREATE PROCEDURE dbo.Test AS SELECT 1";

        var result = extractor.DetectTokens(sql, "#checklater", "#nosynccheck");

        Assert.True(result.HasSkipToken);
        Assert.False(result.HasAskToken);
    }

    [Fact]
    public void DetectTokens_AskToken_ReturnsAsk()
    {
        var extractor = new ObjectExtractor();
        var sql = "-- #checklater\nCREATE PROCEDURE dbo.Test AS SELECT 1";

        var result = extractor.DetectTokens(sql, "#checklater", "#nosynccheck");

        Assert.False(result.HasSkipToken);
        Assert.True(result.HasAskToken);
    }

    [Fact]
    public void DetectTokens_NoTokens_ReturnsNone()
    {
        var extractor = new ObjectExtractor();
        var sql = "CREATE PROCEDURE dbo.Test AS SELECT 1";

        var result = extractor.DetectTokens(sql, "#checklater", "#nosynccheck");

        Assert.False(result.HasSkipToken);
        Assert.False(result.HasAskToken);
    }
}

public class DiffCalculatorTests
{
    [Fact]
    public void CalculateDiff_IdenticalTexts_NoHunks()
    {
        var calculator = new DiffCalculator();
        var result = calculator.CalculateDiff("SELECT 1", "SELECT 1");

        Assert.True(result.IsIdentical);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void CalculateDiff_DifferentTexts_HasHunks()
    {
        var calculator = new DiffCalculator();
        var left = "SELECT * FROM Users";
        var right = "SELECT Id, Name FROM Users";

        var result = calculator.CalculateDiff(left, right);

        Assert.False(result.IsIdentical);
        Assert.NotEmpty(result.Hunks);
    }

    [Fact]
    public void AreIdentical_SameStrings_ReturnsTrue()
    {
        var calculator = new DiffCalculator();
        Assert.True(calculator.AreIdentical("hello", "hello"));
    }

    [Fact]
    public void AreIdentical_DifferentStrings_ReturnsFalse()
    {
        var calculator = new DiffCalculator();
        Assert.False(calculator.AreIdentical("hello", "world"));
    }

    [Fact]
    public void CalculateDiff_AddedLines_CountsCorrectly()
    {
        var calculator = new DiffCalculator();
        var left = "line1\nline3";
        var right = "line1\nline2\nline3";

        var result = calculator.CalculateDiff(left, right);

        Assert.True(result.AddedLines > 0);
    }

    [Fact]
    public void CalculateDiff_DeletedLines_CountsCorrectly()
    {
        var calculator = new DiffCalculator();
        var left = "line1\nline2\nline3";
        var right = "line1\nline3";

        var result = calculator.CalculateDiff(left, right);

        Assert.True(result.DeletedLines > 0);
    }

    [Fact]
    public void CalculateDiff_SqlStoredProcedure_DetectsChanges()
    {
        var calculator = new DiffCalculator();
        var left = @"CREATE PROCEDURE dbo.GetUserById
    @UserId INT
AS
    SELECT * FROM Users WHERE Id = @UserId";

        var right = @"CREATE PROCEDURE dbo.GetUserById
    @UserId INT
AS
    SELECT Id, Name, Email FROM Users WHERE Id = @UserId";

        var result = calculator.CalculateDiff(left, right);

        Assert.False(result.IsIdentical);
        Assert.NotEmpty(result.Hunks);
        Assert.True(result.AddedLines > 0 || result.DeletedLines > 0);
    }

    [Fact]
    public void CalculateDiff_MultipleChanges_ProducesMultipleHunks()
    {
        var calculator = new DiffCalculator();
        var left = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
        var right = "line1\nCHANGED2\nline3\nline4\nline5\nCHANGED6\nline7\nline8\nline9\nline10";

        var result = calculator.CalculateDiff(left, right, contextLines: 1);

        Assert.False(result.IsIdentical);
        // Changes at lines 2 and 6 are far apart, should produce 2 hunks with context=1
        Assert.True(result.Hunks.Count >= 2);
    }

    [Fact]
    public void GetUnifiedDiff_ProducesOutput()
    {
        var calculator = new DiffCalculator();
        var left = "SELECT * FROM Users";
        var right = "SELECT Id FROM Users";

        var unified = calculator.GetUnifiedDiff(left, right);

        Assert.Contains("--- Original", unified);
        Assert.Contains("+++ Modified", unified);
        Assert.Contains("@@", unified);
    }

    [Fact]
    public void CalculateDiff_EmptyToLeft_ProducesAddedLines()
    {
        var calculator = new DiffCalculator();
        var result = calculator.CalculateDiff("", "line1\nline2");

        Assert.False(result.IsIdentical);
        Assert.True(result.AddedLines > 0);
        Assert.Equal(0, result.DeletedLines);
    }

    [Fact]
    public void CalculateDiff_LeftToEmpty_ProducesDeletedLines()
    {
        var calculator = new DiffCalculator();
        var result = calculator.CalculateDiff("line1\nline2", "");

        Assert.False(result.IsIdentical);
        Assert.Equal(0, result.AddedLines);
        Assert.True(result.DeletedLines > 0);
    }

    [Fact]
    public void GetSideBySideDiff_ReturnsNonNull()
    {
        var calculator = new DiffCalculator();
        var result = calculator.GetSideBySideDiff("hello", "world");

        Assert.NotNull(result);
    }
}

public class AIModelsTests
{
    [Fact]
    public void AIResponse_Defaults_AreCorrect()
    {
        var response = new AIResponse
        {
            IsSuccess = true,
            Content = "result",
            InputTokens = 10,
            OutputTokens = 20
        };

        Assert.True(response.IsSuccess);
        Assert.Equal("result", response.Content);
    }

    [Fact]
    public void AIResponse_Failure_SetsError()
    {
        var response = new AIResponse
        {
            IsSuccess = false,
            ErrorMessage = "error message"
        };

        Assert.False(response.IsSuccess);
        Assert.Equal("error message", response.ErrorMessage);
    }
}

public class VersionModelsTests
{
    [Fact]
    public void VersionInfo_IsOutdated_DbModifiedAfterOpen_ReturnsTrue()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            DbModifyDate = DateTime.Now,
            ScriptOpenTime = DateTime.Now.AddHours(-1),
            ObjectExists = true
        };

        Assert.True(info.IsOutdated);
    }

    [Fact]
    public void VersionInfo_IsOutdated_DbModifiedBeforeOpen_ReturnsFalse()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            DbModifyDate = DateTime.Now.AddHours(-2),
            ScriptOpenTime = DateTime.Now.AddHours(-1),
            ObjectExists = true
        };

        Assert.False(info.IsOutdated);
    }

    [Fact]
    public void VersionCheckResult_HasOutdated_WithOutdatedObjects_ReturnsTrue()
    {
        var result = new VersionCheckResult
        {
            VersionInfos = new List<VersionInfo>
            {
                new()
                {
                    ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
                    DbModifyDate = DateTime.Now,
                    ScriptOpenTime = DateTime.Now.AddHours(-1),
                    ObjectExists = true
                }
            }
        };

        Assert.True(result.HasOutdated);
        Assert.Single(result.OutdatedObjects);
    }

    [Fact]
    public void RequiresReview_Conflict_ReturnsTrue()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.Conflict
        };

        Assert.True(info.RequiresReview);
    }

    [Fact]
    public void RequiresReview_OnlyAdded_ReturnsTrue()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.OnlyAdded
        };

        Assert.True(info.RequiresReview);
    }

    [Fact]
    public void RequiresReview_OnlyDeleted_ReturnsTrue()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.OnlyDeleted
        };

        Assert.True(info.RequiresReview);
    }

    [Fact]
    public void RequiresReview_Identical_ReturnsFalse()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.Identical
        };

        Assert.False(info.RequiresReview);
    }

    [Fact]
    public void RequiresReview_NotFound_ReturnsFalse()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.NotFound
        };

        Assert.False(info.RequiresReview);
    }

    [Fact]
    public void ReviewStatusText_Reviewed_ReturnsCorrectText()
    {
        var info = new VersionInfo
        {
            ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
            ComparisonType = DiffComparisonType.Conflict,
            IsReviewed = true
        };

        Assert.Equal("已审查", info.ReviewStatusText);
    }
}

public class ConfigurationModelsTests
{
    [Fact]
    public void AIConfiguration_Defaults_AreCorrect()
    {
        var config = new AIConfiguration();

        Assert.Equal(0.3, config.DefaultTemperature);
        Assert.Equal(4000, config.DefaultMaxTokens);
        Assert.Equal(120, config.TimeoutSeconds);
        Assert.Equal(FormatStyle.Microsoft, config.DefaultFormattingStyle);
    }

    [Fact]
    public void VersionGuardConfiguration_Defaults_AreCorrect()
    {
        var config = new VersionGuardConfiguration();

        Assert.True(config.Enabled);
        Assert.Equal(GuardAction.AskUser, config.DefaultAction);
        Assert.Equal("#nosynccheck", config.SkipToken);
        Assert.Equal("#checklater", config.AskToken);
        Assert.True(config.TokenCaseSensitive);
        Assert.Contains("tempdb", config.ExcludedDatabases);
    }
}
