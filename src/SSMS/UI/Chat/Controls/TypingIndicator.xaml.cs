using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SSMSBoostPlus.SSMS.UI.Chat.Controls;

/// <summary>
/// 打字指示器控件。
/// 显示三个跳动圆点动画，表示 AI 正在生成回复。
/// 通过 Start() / Stop() 控制动画启停。
/// </summary>
public partial class TypingIndicator : UserControl
{
    private Storyboard? _animation;
    private bool _isAnimating;

    public TypingIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 控件加载完成时获取动画引用。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _animation = FindResource("DotAnimation") as Storyboard;

        if (Visibility == Visibility.Visible)
        {
            StartAnimation();
        }
    }

    /// <summary>
    /// 控件卸载时停止动画，避免内存泄漏。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    /// <summary>
    /// 开始跳动动画。
    /// </summary>
    public void StartAnimation()
    {
        if (_animation != null && !_isAnimating)
        {
            _animation.Begin(this, true);
            _isAnimating = true;
        }
    }

    /// <summary>
    /// 停止跳动动画。
    /// </summary>
    public void StopAnimation()
    {
        if (_animation != null)
        {
            _animation.Stop(this);
            _isAnimating = false;
        }
    }

    /// <summary>
    /// 当 Visibility 改变时自动控制动画。
    /// </summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == VisibilityProperty)
        {
            if (Visibility == Visibility.Visible)
            {
                StartAnimation();
            }
            else
            {
                StopAnimation();
            }
        }
    }
}