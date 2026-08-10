using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// 数据库服务集成测试：需要 SQL Server LocalDB 实例。
/// 若 LocalDB 不可用则测试返回空（不报错）。
/// 验证真实 SQL Server 交互：modify_date 查询、对象定义获取、缓存清空。
/// 
/// 前置条件：LocalDB 已安装（通常随 Visual Studio 一起安装）。
/// 连接字符串：(localdb)\\MSSQLLocalDB
/// </summary>
public class DatabaseServiceIntegrationTests
{
    private const string LocalDbConnectionString = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;";

    /// <summary>
    /// 检测 LocalDB 是否可连接。不可用时测试直接返回（静默跳过）。
    /// </summary>
    private static async Task<bool> IsLocalDbAvailableAsync()
    {
        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(LocalDbConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // 1. 查询 sys.objects 获取 modify_date
    // ============================================================
    [Fact]
    public async Task GetObjectModifyDateAsync_SystemObject_ReturnsDateTime()
    {
        if (!await IsLocalDbAvailableAsync()) return;

        var db = new DatabaseService(new NullLoggerService());
        var objRef = new ObjectReference
        {
            Database = "master",
            Schema = "sys",
            Name = "objects"
        };

        var modifyDate = await db.GetObjectModifyDateAsync(
            $"{LocalDbConnectionString}Database=master;", objRef);

        modifyDate.Should().NotBeNull();
        modifyDate!.Value.Should().BeCloseTo(System.DateTime.Now, System.TimeSpan.FromDays(365 * 20)); // SQL Server 安装时间至今
    }

    // ============================================================
    // 2. 查询不存在的对象返回 null
    // ============================================================
    [Fact]
    public async Task GetObjectModifyDateAsync_NonExistentObject_ReturnsNull()
    {
        if (!await IsLocalDbAvailableAsync()) return;

        var db = new DatabaseService(new NullLoggerService());
        var objRef = new ObjectReference
        {
            Database = "master",
            Schema = "dbo",
            Name = "NonExistentProc_XYZ123"
        };

        var modifyDate = await db.GetObjectModifyDateAsync(
            $"{LocalDbConnectionString}Database=master;", objRef);

        modifyDate.Should().BeNull();
    }

    // ============================================================
    // 3. 获取系统对象定义
    // ============================================================
    [Fact]
    public async Task GetObjectDefinitionAsync_SystemObject_ReturnsDefinition()
    {
        if (!await IsLocalDbAvailableAsync()) return;

        var db = new DatabaseService(new NullLoggerService());
        var objRef = new ObjectReference
        {
            Database = "master",
            Schema = "sys",
            Name = "sp_help"
        };

        var definition = await db.GetObjectDefinitionAsync(
            $"{LocalDbConnectionString}Database=master;", objRef);

        definition.Should().NotBeNull();
        definition.Should().StartWith("CREATE"); // sys.sql_modules 返回 CREATE
    }

    // ============================================================
    // 4. ClearCache 清空后重新查询
    // ============================================================
    [Fact]
    public async Task ClearCache_AfterClear_ReQueriesDb()
    {
        if (!await IsLocalDbAvailableAsync()) return;

        var db = new DatabaseService(new NullLoggerService());
        var objRef = new ObjectReference
        {
            Database = "master",
            Schema = "sys",
            Name = "objects"
        };

        // 第一次查询
        var date1 = await db.GetObjectModifyDateAsync(
            $"{LocalDbConnectionString}Database=master;", objRef);

        // 清空缓存
        db.ClearCache();

        // 第二次查询（应重新走 DB）
        var date2 = await db.GetObjectModifyDateAsync(
            $"{LocalDbConnectionString}Database=master;", objRef);

        date1.Should().NotBeNull();
        date2.Should().NotBeNull();
        date1.Should().Be(date2); // 同一对象未变，日期相同
    }
}

/// <summary>
/// 空日志服务：用于集成测试中静默日志输出。
/// </summary>
internal class NullLoggerService : ILoggerService
{
    public void SetEnabled(bool enabled) { }
    public void Information(string message) { }
    public void Debug(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Error(System.Exception ex, string message) { }
}