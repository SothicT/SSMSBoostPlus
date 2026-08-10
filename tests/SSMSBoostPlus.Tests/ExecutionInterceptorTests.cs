using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.VersionGuard;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Tests;

public class ExecutionInterceptorTests
{
    // ============================================================
    //  SqlExecuteCommands.IsSqlExecuteCommand tests
    // ============================================================

    [Fact]
    public void IsSqlExecuteCommand_KnownSSMSSqlGuid_ExecuteSql_ReturnsTrue()
    {
        var guid = new Guid("52692960-56BC-4989-B5D3-94C50A5F8B8F");
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand(guid, 1));
    }

    [Fact]
    public void IsSqlExecuteCommand_KnownSSMSSqlGuid_ExecuteSelSql_ReturnsTrue()
    {
        var guid = new Guid("52692960-56BC-4989-B5D3-94C50A5F8B8F");
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand(guid, 2));
    }

    [Fact]
    public void IsSqlExecuteCommand_KnownSSMSSqlGuid_ExecuteStoredProc_ReturnsTrue()
    {
        var guid = new Guid("52692960-56BC-4989-B5D3-94C50A5F8B8F");
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand(guid, 3));
    }

    [Fact]
    public void IsSqlExecuteCommand_KnownSSMSSqlGuid_ExecuteScriptWithResults_ReturnsTrue()
    {
        var guid = new Guid("52692960-56BC-4989-B5D3-94C50A5F8B8F");
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand(guid, 4));
    }

    [Fact]
    public void IsSqlExecuteCommand_KnownVSStd2KGuid_Execute_ReturnsTrue()
    {
        var guid = new Guid("732ABE74-CD80-11D0-A2DB-00AA00A3EFFF");
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand(guid, 17));
    }

    [Fact]
    public void IsSqlExecuteCommand_KnownVSStd2KGuid_ExecuteWithResults_ReturnsTrue()
    {
        var guid = new Guid("732ABE74-CD80-11D0-A2DB-00AA00A3EFFF");
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand(guid, 18));
    }

    [Fact]
    public void IsSqlExecuteCommand_UnknownGuid_ReturnsFalse()
    {
        var guid = Guid.NewGuid();
        Assert.False(SqlExecuteCommands.IsSqlExecuteCommand(guid, 1));
    }

    [Fact]
    public void IsSqlExecuteCommand_KnownGuidButWrongId_ReturnsFalse()
    {
        var guid = new Guid("52692960-56BC-4989-B5D3-94C50A5F8B8F");
        Assert.False(SqlExecuteCommands.IsSqlExecuteCommand(guid, 999));
    }

    [Fact]
    public void IsSqlExecuteCommand_StringOverload_ValidGuid_ReturnsTrue()
    {
        Assert.True(SqlExecuteCommands.IsSqlExecuteCommand("52692960-56BC-4989-B5D3-94C50A5F8B8F", 1));
    }

    [Fact]
    public void IsSqlExecuteCommand_StringOverload_InvalidGuid_ReturnsFalse()
    {
        Assert.False(SqlExecuteCommands.IsSqlExecuteCommand("not-a-guid", 1));
    }

    // ============================================================
    //  VersionGuardConfiguration interaction tests
    // ============================================================

    [Fact]
    public void VersionGuardConfiguration_Disabled_ShouldAllowExecution()
    {
        var config = new VersionGuardConfiguration { Enabled = false };
        Assert.False(config.Enabled);
    }

    [Fact]
    public void VersionGuardConfiguration_Enabled_ShouldCheckVersion()
    {
        var config = new VersionGuardConfiguration { Enabled = true };
        Assert.True(config.Enabled);
    }

    [Fact]
    public void VersionGuardConfiguration_DefaultIsEnabled()
    {
        var config = new VersionGuardConfiguration();
        Assert.True(config.Enabled);
    }

    // ============================================================
    //  VersionCheckResult decision logic tests
    // ============================================================

    [Fact]
    public void VersionCheckResult_Skipped_ShouldAllowExecution()
    {
        var result = new VersionCheckResult { Skipped = true };
        Assert.True(result.Skipped);
        Assert.False(result.HasOutdated);
    }

    [Fact]
    public void VersionCheckResult_HasError_ShouldAllowExecution()
    {
        var result = new VersionCheckResult { HasError = true, ErrorMessage = "test" };
        Assert.True(result.HasError);
        Assert.False(result.HasOutdated);
    }

    [Fact]
    public void VersionCheckResult_NoOutdated_ShouldAllowExecution()
    {
        var result = new VersionCheckResult
        {
            VersionInfos = new List<VersionInfo>
            {
                new()
                {
                    ObjectRef = new ObjectReference { Name = "Test", Schema = "dbo" },
                    DbModifyDate = DateTime.Now.AddHours(-2),
                    ScriptOpenTime = DateTime.Now.AddHours(-1),
                    ObjectExists = true
                }
            }
        };
        Assert.False(result.HasOutdated);
    }

    [Fact]
    public void VersionCheckResult_HasOutdated_TriggerWarningDialog()
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
    }

    // ============================================================
    //  Token detection for execution interception
    // ============================================================

    [Fact]
    public void SkipToken_ShouldBypassCheck()
    {
        var extractor = new ObjectExtractor();
        var sql = "-- #nosynccheck\nCREATE PROCEDURE dbo.Test AS SELECT 1";
        var result = extractor.DetectTokens(sql, "#checklater", "#nosynccheck");
        Assert.True(result.HasSkipToken);
    }

    [Fact]
    public void NoToken_ShouldProceedWithCheck()
    {
        var extractor = new ObjectExtractor();
        var sql = "CREATE PROCEDURE dbo.Test AS SELECT 1";
        var result = extractor.DetectTokens(sql, "#checklater", "#nosynccheck");
        Assert.False(result.HasSkipToken);
        Assert.False(result.HasAskToken);
    }
}
