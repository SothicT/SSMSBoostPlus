using System;

namespace SSMSBoostPlus.VersionGuard;

/// <summary>
/// Defines known SSMS SQL execution command GUIDs and IDs.
/// Used by ExecutionInterceptor to detect when a SQL Execute command is triggered.
/// </summary>
public static class SqlExecuteCommands
{
    /// <summary>
    /// SQL Editor command set GUID (SSMS-specific).
    /// {52692960-56BC-4989-B5D3-94C50A5F8B8F}
    /// </summary>
    public static readonly Guid SqlEditorCommandSet = new Guid("52692960-56BC-4989-B5D3-94C50A5F8B8F");

    /// <summary>
    /// VS Standard 2K command set GUID.
    /// {732ABE74-CD80-11D0-A2DB-00AA00A3EFFF}
    /// </summary>
    public static readonly Guid VSStd2KCommandSet = new Guid("732ABE74-CD80-11D0-A2DB-00AA00A3EFFF");

    /// <summary>
    /// Known SQL Execute command definitions (GUID, ID).
    /// </summary>
    public static readonly (Guid Guid, int Id, string Name)[] KnownCommands = new[]
    {
        (SqlEditorCommandSet, 1, "ECMD_EXECUTESQL"),
        (SqlEditorCommandSet, 2, "ECMD_EXECUTESELSQL"),
        (SqlEditorCommandSet, 3, "ECMD_EXECUTESTOREDPROC"),
        (SqlEditorCommandSet, 4, "ECMD_EXECUTESCRIPTWITHRESULTS"),
        (VSStd2KCommandSet, 17, "VSStd2K_Execute"),
        (VSStd2KCommandSet, 18, "VSStd2K_ExecuteWithResults"),
    };

    /// <summary>
    /// Check if a command GUID+ID matches any known SQL Execute command.
    /// </summary>
    public static bool IsSqlExecuteCommand(Guid guid, int id)
    {
        foreach (var (knownGuid, knownId, _) in KnownCommands)
        {
            if (guid == knownGuid && id == knownId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if a command GUID string+ID matches any known SQL Execute command.
    /// </summary>
    public static bool IsSqlExecuteCommand(string guidString, int id)
    {
        if (!Guid.TryParse(guidString, out var guid)) return false;
        return IsSqlExecuteCommand(guid, id);
    }
}
