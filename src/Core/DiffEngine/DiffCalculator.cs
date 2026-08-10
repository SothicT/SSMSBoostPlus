using System;
using System.Collections.Generic;
using System.Linq;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using SSMSBoostPlus.DiffEngine.Models;

namespace SSMSBoostPlus.DiffEngine;

/// <summary>
/// Calculates diffs between two text strings using DiffPlex library.
/// Maintains the same public API (DiffResult/DiffHunk/DiffLine) while
/// leveraging DiffPlex's efficient diff algorithm internally.
/// </summary>
public class DiffCalculator
{
    private readonly SideBySideDiffBuilder _sideBySideBuilder;
    private readonly DiffPlex.IDiffer _differ;

    public DiffCalculator()
    {
        _differ = new DiffPlex.Differ();
        _sideBySideBuilder = new SideBySideDiffBuilder(_differ);
    }

    /// <summary>
    /// 规范化 SQL 文本：统一换行符、去除行尾空白、压缩连续空行。
    /// 消除 SSMS 编辑器与 sys.sql_modules 之间的格式差异，使 Diff 结果更精确。
    /// </summary>
    public static string NormalizeSql(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql ?? string.Empty;

        var lines = sql
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n');

        // 去除每行行尾空白，过滤纯空白行（保留有内容的行）
        var normalized = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            normalized.Add(trimmed);
        }

        // 去除首尾空行
        while (normalized.Count > 0 && string.IsNullOrWhiteSpace(normalized[0]))
            normalized.RemoveAt(0);
        while (normalized.Count > 0 && string.IsNullOrWhiteSpace(normalized[normalized.Count - 1]))
            normalized.RemoveAt(normalized.Count - 1);

        return string.Join("\n", normalized);
    }

    /// <summary>
    /// Calculate the diff between two text strings.
    /// Automatically normalizes both texts before comparison to eliminate
    /// whitespace/formatting noise between local editor and database definitions.
    /// </summary>
    /// <param name="leftText">The original (left) text.</param>
    /// <param name="rightText">The modified (right) text.</param>
    /// <param name="contextLines">Number of unchanged context lines around each hunk.</param>
    /// <returns>A DiffResult containing the hunks and line-level changes.</returns>
    public DiffResult CalculateDiff(string leftText, string rightText, int contextLines = 3)
    {
        // 规范化消除格式差异（DB定义 vs 编辑器文本）
        var leftNormalized = NormalizeSql(leftText);
        var rightNormalized = NormalizeSql(rightText);

        if (string.Equals(leftNormalized, rightNormalized, StringComparison.Ordinal))
        {
            return new DiffResult
            {
                LeftText = leftNormalized,
                RightText = rightNormalized,
                Hunks = new List<DiffHunk>()
            };
        }

        // Use DiffPlex's InlineDiffBuilder for line-level diff
        var inlineBuilder = new InlineDiffBuilder(_differ);
        var inlineResult = inlineBuilder.BuildDiffModel(leftNormalized, rightNormalized);

        // Convert DiffPlex result to our model
        var diffLines = ConvertInlineDiff(inlineResult);

        // Group into hunks with context
        var hunks = GroupIntoHunks(diffLines, contextLines);

        return new DiffResult
        {
            LeftText = leftNormalized,
            RightText = rightNormalized,
            Hunks = hunks
        };
    }

    /// <summary>
    /// Check if two texts are identical (quick comparison without full diff).
    /// Normalizes both texts before comparison.
    /// </summary>
    public bool AreIdentical(string leftText, string rightText)
    {
        return string.Equals(NormalizeSql(leftText), NormalizeSql(rightText), StringComparison.Ordinal);
    }

    /// <summary>
    /// Get a unified diff string representation.
    /// </summary>
    public string GetUnifiedDiff(string leftText, string rightText, string leftLabel = "Original", string rightLabel = "Modified")
    {
        var result = CalculateDiff(leftText, rightText);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"--- {leftLabel}");
        sb.AppendLine($"+++ {rightLabel}");

        foreach (var hunk in result.Hunks)
        {
            sb.AppendLine($"@@ -{hunk.LeftStartLine + 1},{hunk.LeftLineCount} +{hunk.RightStartLine + 1},{hunk.RightLineCount} @@");

            foreach (var line in hunk.Lines)
            {
                var prefix = line.Type switch
                {
                    DiffLineType.Unchanged => " ",
                    DiffLineType.Added => "+",
                    DiffLineType.Deleted => "-",
                    DiffLineType.Modified => "~",
                    _ => " "
                };
                sb.AppendLine($"{prefix}{line.Text}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get a side-by-side diff result from DiffPlex (for advanced UI scenarios).
    /// Normalizes both texts before comparison to eliminate format noise.
    /// </summary>
    public SideBySideDiffModel GetSideBySideDiff(string leftText, string rightText)
    {
        return _sideBySideBuilder.BuildDiffModel(
            NormalizeSql(leftText),
            NormalizeSql(rightText));
    }

    /// <summary>
    /// Convert DiffPlex DiffPaneModel to our DiffLine list.
    /// </summary>
    private static List<DiffLine> ConvertInlineDiff(DiffPaneModel diffResult)
    {
        var diffLines = new List<DiffLine>();
        var leftLineNum = 0;
        var rightLineNum = 0;

        foreach (var piece in diffResult.Lines)
        {
            switch (piece.Type)
            {
                case ChangeType.Unchanged:
                    leftLineNum++;
                    rightLineNum++;
                    diffLines.Add(new DiffLine
                    {
                        Type = DiffLineType.Unchanged,
                        Text = piece.Text ?? string.Empty,
                        LeftLineNumber = leftLineNum - 1,
                        RightLineNumber = rightLineNum - 1
                    });
                    break;

                case ChangeType.Inserted:
                    rightLineNum++;
                    diffLines.Add(new DiffLine
                    {
                        Type = DiffLineType.Added,
                        Text = piece.Text ?? string.Empty,
                        LeftLineNumber = -1,
                        RightLineNumber = rightLineNum - 1
                    });
                    break;

                case ChangeType.Deleted:
                    leftLineNum++;
                    diffLines.Add(new DiffLine
                    {
                        Type = DiffLineType.Deleted,
                        Text = piece.Text ?? string.Empty,
                        LeftLineNumber = leftLineNum - 1,
                        RightLineNumber = -1
                    });
                    break;

                case ChangeType.Modified:
                    leftLineNum++;
                    rightLineNum++;
                    diffLines.Add(new DiffLine
                    {
                        Type = DiffLineType.Modified,
                        Text = piece.Text ?? string.Empty,
                        LeftLineNumber = leftLineNum - 1,
                        RightLineNumber = rightLineNum - 1
                    });
                    break;

                default:
                    // ChangeType.Imaginary and others - skip alignment padding
                    break;
            }
        }

        return diffLines;
    }

    /// <summary>
    /// Group individual diff lines into hunks with context.
    /// </summary>
    private static List<DiffHunk> GroupIntoHunks(List<DiffLine> diffLines, int contextLines)
    {
        var hunks = new List<DiffHunk>();
        if (diffLines.Count == 0) return hunks;

        // Find indices of changed lines
        var changeIndices = new List<int>();
        for (int i = 0; i < diffLines.Count; i++)
        {
            if (diffLines[i].Type != DiffLineType.Unchanged)
                changeIndices.Add(i);
        }

        if (changeIndices.Count == 0) return hunks;

        // Group nearby changes into hunks
        var groups = new List<List<int>>();
        var currentGroup = new List<int> { changeIndices[0] };

        for (int i = 1; i < changeIndices.Count; i++)
        {
            if (changeIndices[i] - changeIndices[i - 1] <= contextLines * 2 + 1)
            {
                currentGroup.Add(changeIndices[i]);
            }
            else
            {
                groups.Add(currentGroup);
                currentGroup = new List<int> { changeIndices[i] };
            }
        }
        groups.Add(currentGroup);

        // Build hunks from groups
        foreach (var group in groups)
        {
            var startIndex = Math.Max(0, group[0] - contextLines);
            var endIndex = Math.Min(diffLines.Count - 1, group[group.Count - 1] + contextLines);

            var hunkLines = diffLines.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();

            var leftStart = hunkLines.FirstOrDefault(l => l.LeftLineNumber >= 0)?.LeftLineNumber ?? 0;
            var rightStart = hunkLines.FirstOrDefault(l => l.RightLineNumber >= 0)?.RightLineNumber ?? 0;
            var leftCount = hunkLines.Count(l => l.Type != DiffLineType.Added);
            var rightCount = hunkLines.Count(l => l.Type != DiffLineType.Deleted);

            hunks.Add(new DiffHunk
            {
                LeftStartLine = leftStart,
                LeftLineCount = leftCount,
                RightStartLine = rightStart,
                RightLineCount = rightCount,
                Lines = hunkLines
            });
        }

        return hunks;
    }
}
