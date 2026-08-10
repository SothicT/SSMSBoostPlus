using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// 版本更新通知弹窗。
/// 右下角弹出，类似 SSMS 更新通知风格，内嵌可滚动的 commit 差异列表。
/// </summary>
public partial class VersionUpdateDialog : Window
{
    private readonly string? _localHash;
    private readonly string _remoteHash;
    private readonly List<string> _commits;

    /// <summary>
    /// 用户点击"立即更新"时触发。
    /// </summary>
    public event EventHandler? UpdateClicked;

    public VersionUpdateDialog(string? localHash, string remoteHash, List<string> commits)
    {
        InitializeComponent();

        _localHash = localHash;
        _remoteHash = remoteHash;
        _commits = commits;

        // 更新摘要
        SummaryText.Text = $"{commits.Count} 个新提交可用";

        // 更新版本信息栏
        var info = $"本地版本: {localHash?.Substring(0, 8) ?? "未知"}\n" +
                   $"远程版本: {remoteHash.Substring(0, 8)}\n" +
                   $"更新后请重启 SSMS。";
        VersionInfoText.Text = info;

        // 填充 commit 列表
        foreach (var commit in commits)
        {
            // 格式美化：如包含制表符分隔的 hash+message，只显示 message
            var displayText = commit;
            if (commit.Contains("\t"))
            {
                var parts = commit.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    displayText = $"• {parts[1].Trim()}  ({parts[0].Substring(0, 7)})";
                else
                    displayText = $"• {commit}";
            }
            else
            {
                displayText = $"• {commit}";
            }
            CommitList.Items.Add(displayText);
        }

        // 10秒后自动关闭
        var closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        closeTimer.Tick += (s, e) =>
        {
            closeTimer.Stop();
            Close();
        };
        closeTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 将窗口定位到屏幕右下角
        var screenWidth = SystemParameters.WorkArea.Width;
        var screenHeight = SystemParameters.WorkArea.Height;

        Left = screenWidth - Width - 20;
        Top = screenHeight - Height - 20;

        // 添加阴影效果（运行时创建，避免 XAML 资源引用问题）
        var dropShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = 0.3,
            BlurRadius = 10,
            ShadowDepth = 3,
            Direction = 270
        };
        var border = Content as Border;
        if (border != null)
            border.Effect = dropShadow;
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 支持拖拽移动窗口。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            this.DragMove();
        }
    }
}