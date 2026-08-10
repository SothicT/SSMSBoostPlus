using System.Threading.Tasks;

namespace SSMSBoostPlus.Interfaces;

/// <summary>
/// Output window abstraction for platform-independent message display.
/// </summary>
public interface IOutputWindow
{
    /// <summary>
    /// Clear the output window.
    /// </summary>
    Task ClearAsync();

    /// <summary>
    /// Append a line of text to the output window.
    /// </summary>
    Task AppendLineAsync(string text);

    /// <summary>
    /// Append text without a newline.
    /// </summary>
    Task AppendAsync(string text);

    /// <summary>
    /// Show an error message.
    /// </summary>
    Task ShowErrorAsync(string message);

    /// <summary>
    /// Show a warning message.
    /// </summary>
    Task ShowWarningAsync(string message);
}
