namespace SSMSBoostPlus.CodeAnalysis.Models;

/// <summary>
/// Result of AI code analysis.
/// </summary>
public class AnalysisResult
{
    /// <summary>
    /// The type of analysis performed.
    /// </summary>
    public AnalysisType Type { get; set; }

    /// <summary>
    /// Whether the analysis was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The AI-generated analysis content (formatted for display).
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Original code that was analyzed.
    /// </summary>
    public string OriginalCode { get; set; } = string.Empty;

    /// <summary>
    /// Suggested/replacement code (for Suggestions and Formatting).
    /// </summary>
    public string? SuggestedCode { get; set; }

    /// <summary>
    /// Execution plan XML (if available, for Performance analysis).
    /// </summary>
    public string? ExecutionPlanXml { get; set; }

    /// <summary>
    /// Error message if analysis failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Token usage information.
    /// </summary>
    public TokenUsage? Usage { get; set; }

    /// <summary>
    /// Elapsed time in milliseconds.
    /// </summary>
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Type of code analysis.
/// </summary>
public enum AnalysisType
{
    /// <summary>AI code suggestions.</summary>
    Suggestions,

    /// <summary>Add comments to code.</summary>
    Comments,

    /// <summary>Format code according to style.</summary>
    Formatting,

    /// <summary>Performance analysis.</summary>
    Performance
}

/// <summary>
/// Token usage information for AI API calls.
/// </summary>
public class TokenUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}
