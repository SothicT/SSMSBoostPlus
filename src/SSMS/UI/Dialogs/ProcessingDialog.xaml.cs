using System;
using System.Windows;
using System.Windows.Threading;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// A simple modal dialog with an indeterminate progress bar.
/// Shows friendly processing status text while an operation is running.
/// </summary>
public partial class ProcessingDialog : Window
{
    public ProcessingDialog(string title = "SSMSBoost++ - Processing", string statusText = "Processing...")
    {
        InitializeComponent();
        Title = title;
        StatusText.Text = statusText;
    }

    /// <summary>
    /// Update the status text shown in the dialog.
    /// Thread-safe: can be called from any thread.
    /// </summary>
    public void UpdateStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => StatusText.Text = text);
            return;
        }
        StatusText.Text = text;
    }

    /// <summary>
    /// Update the detail text shown below the progress bar.
    /// Thread-safe: can be called from any thread.
    /// </summary>
    public void UpdateDetail(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => DetailText.Text = text);
            return;
        }
        DetailText.Text = text;
    }

    /// <summary>
    /// Close the dialog safely from any thread.
    /// </summary>
    public void CloseSafely()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Close());
            return;
        }
        Close();
    }
}