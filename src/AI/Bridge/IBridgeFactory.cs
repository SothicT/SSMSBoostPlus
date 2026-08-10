using System.Threading.Tasks;
using SSMSBoostPlus.Models;

namespace SSMSBoostPlus.AI.Bridge;

/// <summary>
/// Bridge factory abstraction for creating AI bridge instances.
/// Allows mocking bridge creation logic in unit tests.
/// </summary>
public interface IBridgeFactory
{
    /// <summary>
    /// Create the best available AI bridge instance.
    /// Prefer CLI if available, fall back to HTTP API.
    /// </summary>
    Task<ICodebuddyBridge> CreateBestAvailableBridgeAsync(AIConfiguration config);

    /// <summary>
    /// Directly create an HTTP API bridge (does not attempt CLI).
    /// </summary>
    ICodebuddyBridge CreateHttpApiBridge(AIConfiguration config);

    /// <summary>
    /// Directly create a CLI bridge (does not check availability).
    /// </summary>
    /// <param name="cliPath">Optional full path to codebuddy CLI executable. Uses PATH if null/empty.</param>
    ICodebuddyBridge CreateCliBridge(string? cliPath = null);
}