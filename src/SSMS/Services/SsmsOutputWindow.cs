using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;

namespace SSMSBoostPlus.SSMS.Services;

/// <summary>
/// SSMS-specific implementation of IOutputWindow.
/// Writes to the SSMS output window pane.
/// </summary>
public class SsmsOutputWindow : IOutputWindow
{
    private readonly IServiceProvider _serviceProvider;
    private IVsOutputWindowPane? _pane;
    private readonly string _paneName = "SSMSBoostPlus";
    private static Guid _paneGuid = new("b2c3d4e5-f6a7-8901-bcde-f12345678901");

    public SsmsOutputWindow(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task ClearAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        GetPane()?.Clear();
    }

    /// <inheritdoc />
    public async Task AppendLineAsync(string text)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        GetPane()?.OutputStringThreadSafe(text + Environment.NewLine);
    }

    /// <inheritdoc />
    public async Task AppendAsync(string text)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        GetPane()?.OutputStringThreadSafe(text);
    }

    /// <inheritdoc />
    public async Task ShowErrorAsync(string message)
    {
        await AppendLineAsync($"[ERROR] {message}");
    }

    /// <inheritdoc />
    public async Task ShowWarningAsync(string message)
    {
        await AppendLineAsync($"[WARNING] {message}");
    }

    private IVsOutputWindowPane? GetPane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_pane != null) return _pane;

        var outputWindow = (IVsOutputWindow?)_serviceProvider.GetService(typeof(SVsOutputWindow));
        if (outputWindow == null) return null;

        outputWindow.CreatePane(ref _paneGuid, _paneName, 1, 1);
        outputWindow.GetPane(ref _paneGuid, out _pane);

        return _pane;
    }
}
