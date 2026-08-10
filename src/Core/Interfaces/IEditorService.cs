using System.Threading.Tasks;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// Editor service abstraction for platform-independent text editing operations.
/// </summary>
public interface IEditorService
{
    /// <summary>
    /// Get the selected text from the active document. Returns full content if no selection.
    /// </summary>
    Task<(string Code, bool IsSelection)> GetCodeAsync();

    /// <summary>
    /// Replace the selected text in the active document.
    /// </summary>
    Task ReplaceSelectionAsync(string newText);

    /// <summary>
    /// Replace all content in the active document.
    /// </summary>
    Task ReplaceAllAsync(string newText);

    /// <summary>
    /// Get the file path of the active document.
    /// </summary>
    string GetActiveDocumentPath();

    /// <summary>
    /// Check if the active document is a SQL document.
    /// </summary>
    bool IsSqlDocument();
}
