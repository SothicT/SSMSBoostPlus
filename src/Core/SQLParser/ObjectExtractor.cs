using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.SQLParser;

/// <summary>
/// Extracts database object references from T-SQL code using ScriptDom.
/// </summary>
public class ObjectExtractor
{
    /// <summary>
    /// Extract all database object references from the given SQL code.
    /// </summary>
    /// <param name="sqlCode">T-SQL code to parse.</param>
    /// <returns>List of object references found in the code.</returns>
    public List<ObjectReference> ExtractObjects(string sqlCode)
    {
        if (string.IsNullOrWhiteSpace(sqlCode))
            return new List<ObjectReference>();

        try
        {
            var parser = new TSql150Parser(true);
            IList<ParseError> errors;
            var fragment = parser.Parse(new StringReader(sqlCode), out errors);

            if (errors.Count > 0)
            {
                // Try to extract objects even with parse errors
                // by visiting the partially parsed fragment
            }

            var visitor = new ObjectReferenceVisitor();
            fragment?.Accept(visitor);

            return visitor.Objects
                .GroupBy(o => o.FullName)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception)
        {
            return new List<ObjectReference>();
        }
    }

    /// <summary>
    /// Detect tokens in the SQL code that control version guard behavior.
    /// </summary>
    /// <param name="sqlCode">T-SQL code to scan.</param>
    /// <param name="askToken">Ask token pattern (e.g., #checklater).</param>
    /// <param name="skipToken">Skip token pattern (e.g., #nosynccheck).</param>
    /// <returns>Token detection result.</returns>
    public TokenDetectionResult DetectTokens(string sqlCode, string askToken, string skipToken)
    {
        var result = new TokenDetectionResult();

        if (string.IsNullOrWhiteSpace(sqlCode))
            return result;

        var lines = sqlCode.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // Check for skip token (as a comment)
            if (!string.IsNullOrEmpty(skipToken) && line.IndexOf(skipToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.HasSkipToken = true;
                result.SkipTokenLine = i + 1;
            }

            // Check for ask token (as a comment)
            if (!string.IsNullOrEmpty(askToken) && line.IndexOf(askToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.HasAskToken = true;
                result.AskTokenLine = i + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Extract the script text for a specific object from the full SQL code.
    /// Uses the object reference's LineNumber and EndLineNumber to extract
    /// the CREATE/ALTER statement body from the original script.
    /// Extends backward to include header comments before the CREATE/ALTER keyword.
    /// Uses a state machine to correctly track multi-line /* */ comment blocks.
    /// </summary>
    /// <param name="sqlCode">Full T-SQL script.</param>
    /// <param name="objectRef">Object reference with line number info.</param>
    /// <returns>The script text for the specified object, or null if not found.</returns>
    public string? ExtractObjectScript(string sqlCode, ObjectReference objectRef)
    {
        if (string.IsNullOrWhiteSpace(sqlCode) || objectRef.LineNumber <= 0)
            return null;

        var lines = sqlCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // LineNumber is 1-based, array is 0-based
        var createLine = objectRef.LineNumber - 1;

        // 向前扩展到头部注释（含空行、-- 注释、/* */ 多行注释块）
        // 反向遍历，用状态机追踪 /* */ 块边界
        var startLine = createLine;
        var insideBlockComment = false; // 反向遍历：看到 */ 时置 true，看到 /* 时置 false

        for (int i = createLine - 1; i >= 0; i--)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // 检查 /* */ 边界（反向：*/ 表示块结束，/* 表示块开始）
            var hasCloseComment = trimmed.IndexOf("*/", StringComparison.Ordinal) >= 0;
            var hasOpenComment = trimmed.IndexOf("/*", StringComparison.Ordinal) >= 0;

            // 当前行是否完全是注释行？
            var isEmptyOrWhitespace = string.IsNullOrWhiteSpace(line);
            var isSingleLineComment = trimmed.StartsWith("--");
            var isBlockOnly = hasOpenComment || hasCloseComment;

            // 如果有 /* 或 */ 标记，更新块追踪状态
            if (hasOpenComment || hasCloseComment)
            {
                if (hasCloseComment && !hasOpenComment)
                {
                    // 反向：看到 */ 表示进入块
                    insideBlockComment = true;
                }
                else if (hasOpenComment && !hasCloseComment)
                {
                    // 反向：看到 /* 表示离开块
                    insideBlockComment = false;
                }
                // 同一行同时有 /* 和 */，不改变状态
            }

            if (isEmptyOrWhitespace || isSingleLineComment || isBlockOnly || insideBlockComment)
            {
                startLine = i;
            }
            else
            {
                break; // 遇到非注释代码行，停止扩展
            }
        }

        // EndLineNumber: 如果为 0 或等于 StartLine（回退值），扩展到文件末尾
        var endLine = objectRef.EndLineNumber > objectRef.LineNumber
            ? Math.Min(objectRef.EndLineNumber - 1, lines.Length - 1)
            : lines.Length - 1; // 回退：扩展到文件末尾

        if (startLine < 0 || startLine >= lines.Length)
            return null;

        // Extract lines from start to end
        var scriptLines = new List<string>();
        for (int i = startLine; i <= endLine && i < lines.Length; i++)
        {
            scriptLines.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, scriptLines);
    }

    /// <summary>
    /// 在完整 SQL 代码中替换指定对象的脚本部分。
    /// 使用与 ExtractObjectScript 相同的向后扩展逻辑定位对象起始行，
    /// 然后将对象行替换为 newScript，保留对象前后的其他内容。
    /// </summary>
    /// <param name="sqlCode">完整 T-SQL 脚本。</param>
    /// <param name="objectRef">对象引用（含行号信息）。</param>
    /// <param name="newScript">替换后的对象脚本。</param>
    /// <returns>替换后的完整 SQL 脚本。</returns>
    public string ReplaceObjectScript(string sqlCode, ObjectReference objectRef, string newScript)
    {
        if (string.IsNullOrWhiteSpace(sqlCode))
            return newScript;

        if (objectRef.LineNumber <= 0)
            return newScript;

        var lines = sqlCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var createLine = objectRef.LineNumber - 1;

        // 向前扩展到头部注释（与 ExtractObjectScript 相同的逻辑）
        var startLine = createLine;
        var insideBlockComment = false;

        for (int i = createLine - 1; i >= 0; i--)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            var hasCloseComment = trimmed.IndexOf("*/", StringComparison.Ordinal) >= 0;
            var hasOpenComment = trimmed.IndexOf("/*", StringComparison.Ordinal) >= 0;

            if (hasOpenComment || hasCloseComment)
            {
                if (hasCloseComment && !hasOpenComment)
                    insideBlockComment = true;
                else if (hasOpenComment && !hasCloseComment)
                    insideBlockComment = false;
            }

            var isEmptyOrWhitespace = string.IsNullOrWhiteSpace(line);
            var isSingleLineComment = trimmed.StartsWith("--");
            var isBlockOnly = hasOpenComment || hasCloseComment;

            if (isEmptyOrWhitespace || isSingleLineComment || isBlockOnly || insideBlockComment)
            {
                startLine = i;
            }
            else
            {
                break;
            }
        }

        var endLine = objectRef.EndLineNumber > objectRef.LineNumber
            ? Math.Min(objectRef.EndLineNumber - 1, lines.Length - 1)
            : lines.Length - 1;

        if (startLine < 0 || startLine >= lines.Length)
            return newScript;

        // 拼接：前部 + newScript + 后部
        var before = startLine > 0
            ? string.Join(Environment.NewLine, lines, 0, startLine)
            : string.Empty;

        var afterStart = endLine + 1;
        var after = afterStart < lines.Length
            ? string.Join(Environment.NewLine, lines, afterStart, lines.Length - afterStart)
            : string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(before)) parts.Add(before);
        parts.Add(newScript);
        if (!string.IsNullOrEmpty(after)) parts.Add(after);

        return string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// Result of token detection in SQL code.
/// </summary>
public class TokenDetectionResult
{
    public bool HasSkipToken { get; set; }
    public int SkipTokenLine { get; set; }
    public bool HasAskToken { get; set; }
    public int AskTokenLine { get; set; }
}

/// <summary>
/// TSqlFragmentVisitor that extracts object references from T-SQL syntax tree.
/// </summary>
internal class ObjectReferenceVisitor : TSqlFragmentVisitor
{
    public List<ObjectReference> Objects { get; } = new List<ObjectReference>();

    public override void Visit(CreateProcedureStatement node)
    {
        AddObject(node.ProcedureReference?.Name, "PROCEDURE", node);
        base.Visit(node);
    }

    public override void Visit(AlterProcedureStatement node)
    {
        AddObject(node.ProcedureReference?.Name, "PROCEDURE", node);
        base.Visit(node);
    }

    public override void Visit(CreateFunctionStatement node)
    {
        AddObject(node.Name, "FUNCTION", node);
        base.Visit(node);
    }

    public override void Visit(AlterFunctionStatement node)
    {
        AddObject(node.Name, "FUNCTION", node);
        base.Visit(node);
    }

    public override void Visit(CreateViewStatement node)
    {
        AddObject(node.SchemaObjectName, "VIEW", node);
        base.Visit(node);
    }

    public override void Visit(AlterViewStatement node)
    {
        AddObject(node.SchemaObjectName, "VIEW", node);
        base.Visit(node);
    }

    public override void Visit(CreateTriggerStatement node)
    {
        AddObject(node.Name, "TRIGGER", node);
        base.Visit(node);
    }

    public override void Visit(AlterTriggerStatement node)
    {
        AddObject(node.Name, "TRIGGER", node);
        base.Visit(node);
    }

    public override void Visit(ExecuteStatement node)
    {
        // Extract procedure name from EXEC statements — but mark as reference (not definition)
        var execSpec = node.ExecuteSpecification?.ExecutableEntity as ExecutableProcedureReference;
        if (execSpec?.ProcedureReference?.ProcedureReference?.Name != null)
        {
            AddObject(execSpec.ProcedureReference.ProcedureReference.Name, "PROCEDURE", node, isDefinition: false);
        }
        base.Visit(node);
    }

    private void AddObject(SchemaObjectName? name, string type, TSqlFragment node, bool isDefinition = true)
    {
        if (name is null) return;

        // Calculate end line from the last token of the statement
        var endLine = node.StartLine;
        if (node.ScriptTokenStream != null)
        {
            // Find the last token within this fragment's range
            for (int i = node.LastTokenIndex; i >= node.FirstTokenIndex && i < node.ScriptTokenStream.Count; i--)
            {
                if (node.ScriptTokenStream[i] != null)
                {
                    endLine = node.ScriptTokenStream[i].Line;
                    break;
                }
            }
        }

        Objects.Add(new ObjectReference
        {
            Name = name.BaseIdentifier?.Value ?? string.Empty,
            Schema = name.SchemaIdentifier?.Value ?? "dbo",
            Database = name.DatabaseIdentifier?.Value ?? string.Empty,
            Type = type,
            LineNumber = node.StartLine,
            EndLineNumber = endLine,
            IsDefinition = isDefinition
        });
    }
}
