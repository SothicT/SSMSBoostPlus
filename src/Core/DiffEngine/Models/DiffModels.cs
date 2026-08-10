using System;
using System.Collections.Generic;
using System.Linq;

namespace SSMSBoostPlus.DiffEngine.Models;

/// <summary>
/// Represents a complete diff result between two texts.
/// </summary>
public class DiffResult
{
    /// <summary>
    /// The original (left) text.
    /// </summary>
    public string LeftText { get; set; } = string.Empty;

    /// <summary>
    /// The modified (right) text.
    /// </summary>
    public string RightText { get; set; } = string.Empty;

    /// <summary>
    /// List of diff hunks (change blocks).
    /// </summary>
    public List<DiffHunk> Hunks { get; set; } = new();

    /// <summary>
    /// Whether the two texts are identical.
    /// </summary>
    public bool IsIdentical => Hunks.Count == 0;

    /// <summary>
    /// Total number of added lines.
    /// </summary>
    public int AddedLines => Hunks.Sum(h => h.AddedCount);

    /// <summary>
    /// Total number of deleted lines.
    /// </summary>
    public int DeletedLines => Hunks.Sum(h => h.DeletedCount);

    /// <summary>
    /// Total number of modified lines (approximate).
    /// </summary>
    public int ModifiedLines => Hunks.Sum(h => h.ModifiedCount);
}

/// <summary>
/// Represents a block of changes in a diff.
/// </summary>
public class DiffHunk
{
    /// <summary>
    /// Starting line number in the left (original) text.
    /// </summary>
    public int LeftStartLine { get; set; }

    /// <summary>
    /// Number of lines in the left (original) text.
    /// </summary>
    public int LeftLineCount { get; set; }

    /// <summary>
    /// Starting line number in the right (modified) text.
    /// </summary>
    public int RightStartLine { get; set; }

    /// <summary>
    /// Number of lines in the right (modified) text.
    /// </summary>
    public int RightLineCount { get; set; }

    /// <summary>
    /// Individual diff lines within this hunk.
    /// </summary>
    public List<DiffLine> Lines { get; set; } = new();

    /// <summary>
    /// Number of added lines in this hunk.
    /// </summary>
    public int AddedCount => Lines.Count(l => l.Type == DiffLineType.Added);

    /// <summary>
    /// Number of deleted lines in this hunk.
    /// </summary>
    public int DeletedCount => Lines.Count(l => l.Type == DiffLineType.Deleted);

    /// <summary>
    /// Number of modified lines (min of added/deleted as approximation).
    /// </summary>
    public int ModifiedCount => Math.Min(AddedCount, DeletedCount);
}

/// <summary>
/// Represents a single line in a diff.
/// </summary>
public class DiffLine
{
    /// <summary>
    /// The type of change for this line.
    /// </summary>
    public DiffLineType Type { get; set; }

    /// <summary>
    /// The text content of the line.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Line number in the left (original) text, or -1 if not applicable.
    /// </summary>
    public int LeftLineNumber { get; set; } = -1;

    /// <summary>
    /// Line number in the right (modified) text, or -1 if not applicable.
    /// </summary>
    public int RightLineNumber { get; set; } = -1;
}

/// <summary>
/// Type of change for a diff line.
/// </summary>
public enum DiffLineType
{
    /// <summary>Line is unchanged (context).</summary>
    Unchanged,

    /// <summary>Line was added.</summary>
    Added,

    /// <summary>Line was deleted.</summary>
    Deleted,

    /// <summary>Line was modified (for display purposes).</summary>
    Modified
}
