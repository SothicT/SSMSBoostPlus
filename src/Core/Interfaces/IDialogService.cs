using System;
using System.Threading.Tasks;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// Dialog service abstraction for platform-independent dialog display.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Show the version warning dialog.
    /// </summary>
    /// <param name="versionInfo">The version check result with outdated objects.</param>
    /// <param name="currentScriptCode">The current SQL script code in the editor.</param>
    /// <param name="getDbDefinition">Callback to fetch the latest DB definition for a given outdated object.</param>
    /// <param name="getLatestModifyDate">Callback to fetch the latest modify_date for a given outdated object (bypasses cache).</param>
    /// <param name="refreshComparison">Callback to refresh the ComparisonType for a given outdated object after changes detected.</param>
    /// <returns>User's choice: Reload, Continue, or Cancel.</returns>
    Task<VersionWarningResult> ShowVersionWarningDialog(
        VersionCheckResult versionInfo,
        string currentScriptCode,
        Func<VersionInfo, Task<string?>> getDbDefinition,
        Func<VersionInfo, Task<DateTime?>> getLatestModifyDate,
        Func<VersionInfo, Task> refreshComparison);

    /// <summary>
    /// Show the AI diff dialog for code comparison.
    /// </summary>
    /// <param name="leftCode">Local code (left side).</param>
    /// <param name="rightCode">AI generated code (right side).</param>
    /// <param name="title">Dialog title.</param>
    /// <returns>User's choice: AcceptRight, AcceptLeft, or Cancel.</returns>
    Task<DiffDialogResult> ShowDiffDialog(string leftCode, string rightCode, string title);
}

/// <summary>
/// Result of the version warning dialog.
/// </summary>
public enum VersionWarningResult
{
    ReloadFromDb,
    ContinueExecution,
    Cancel
}

/// <summary>
/// Result of the AI diff dialog.
/// </summary>
public enum DiffDialogResult
{
    AcceptRight,
    AcceptLeft,
    Cancel
}
