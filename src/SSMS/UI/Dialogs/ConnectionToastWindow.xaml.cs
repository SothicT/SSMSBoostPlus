using System;
using System.Windows;
using System.Windows.Threading;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// 右下角 Toast 通知弹窗（数据库连接失败提示）。
/// 非模态、无边框、自动定位在 SSMS 窗口右下角。
/// 默认停留 10 秒后自动关闭，用户可点击 ✕ 手动关闭。
/// </summary>
public partial class ConnectionToastWindow : Window
{
    private readonly DispatcherTimer _autoCloseTimer;

    public ConnectionToastWindow()
    {
        InitializeComponent();

        // 10 秒后自动关闭（防遗忘）
        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Close();
        };
        _autoCloseTimer.Start();
    }

    /// <summary>
    /// 设置标题与正文（必须在 UI 线程调用）。
    /// </summary>
    public void SetMessage(string title, string message)
    {
        TitleText.Text = title;
        MessageText.Text = message;
    }

    /// <summary>
    /// 定位到屏幕右下角并显示（SSMS 主窗口右下角）。
    /// 使用屏幕工作区（排除任务栏）定位，不再依赖可能指向内部工具窗口的 Owner。
    /// </summary>
    public void ShowAtBottomRight(Window? owner)
    {
        // 尝试获取 SSMS 主窗口（优先使用传入的 owner，无效时尝试 Application.Current.MainWindow）
        var mainWindow = owner ?? Application.Current?.MainWindow;
        var workArea = SystemParameters.WorkArea;

        // 校验窗口是否有效（VS 扩展中 MainWindow 可能指向内部工具窗口，尺寸/位置异常）
        bool isValidWindow = mainWindow != null
            && mainWindow.IsVisible
            && mainWindow.Width >= 400 && mainWindow.Height >= 300
            && mainWindow.Left >= workArea.Left - 100
            && mainWindow.Top >= workArea.Top - 100
            && mainWindow.Left + mainWindow.Width <= workArea.Right + 100
            && mainWindow.Top + mainWindow.Height <= workArea.Bottom + 100;

        if (isValidWindow)
        {
            // 相对 SSMS 主窗口右下角（留出状态栏和边距空间）
            Left = mainWindow.Left + mainWindow.Width - Width - 24;
            Top = mainWindow.Top + mainWindow.Height - Height - 64;
        }
        else
        {
            // 兜底：屏幕工作区右下角（任务栏上方，留出边距）
            Left = workArea.Right - Width - 16;
            Top = workArea.Bottom - Height - 16;
        }

        // 确保不超出屏幕工作区边界
        Left = Math.Max(workArea.Left + 8, Math.Min(Left, workArea.Right - Width - 8));
        Top = Math.Max(workArea.Top + 8, Math.Min(Top, workArea.Bottom - Height - 8));

        // 设置 owner 为 SSMS 主窗口（保持窗口层级关系），无效时不设 owner
        if (isValidWindow)
            Owner = mainWindow;

        Show();
        Activate(); // 确保显示在最前
    }

    /// <summary>
    /// 用户点击关闭按钮。
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer.Stop();
        Close();
    }
}
