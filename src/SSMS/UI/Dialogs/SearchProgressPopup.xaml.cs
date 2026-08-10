using System;
using System.Windows;
using System.Windows.Interop;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// 右下角小型进度提示弹窗。
/// 非模态、无边框、自动定位在 SSMS 窗口右下角。
/// </summary>
public partial class SearchProgressPopup : Window
{
    private readonly System.Timers.Timer _autoCloseTimer;

    public SearchProgressPopup()
    {
        InitializeComponent();

        // 10 秒后自动关闭（防遗忘）
        _autoCloseTimer = new System.Timers.Timer(10000);
        _autoCloseTimer.Elapsed += (_, _) =>
        {
            _autoCloseTimer.Stop();
            CloseSafely();
        };
        _autoCloseTimer.Start();
    }

    /// <summary>
    /// 定位到父窗口右下角并显示。
    /// </summary>
    public void ShowAtBottomRight(Window owner)
    {
        Owner = owner;

        // 计算右下角位置
        var left = owner.Left + owner.Width - Width - 20;
        var top = owner.Top + owner.Height - Height - 50;

        // 确保不超出屏幕边界
        left = Math.Max(10, left);
        top = Math.Max(10, top);

        Left = left;
        Top = top;

        Show();
    }

    /// <summary>
    /// 更新状态文字（线程安全）。
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
    /// 安全关闭弹窗（线程安全）。
    /// </summary>
    public void CloseSafely()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() =>
            {
                _autoCloseTimer?.Stop();
                Close();
            });
            return;
        }
        _autoCloseTimer?.Stop();
        Close();
    }
}