using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.UI.VersionHistory;

/// <summary>
/// Local History 版本差异对比弹窗。
/// 两种模式：
/// - 模式 A：服务器当前定义 vs 历史版本（单击选中）
/// - 模式 B：两个历史版本间对比（Ctrl 多选）
///
/// 渲染层使用虚拟化的 ListView（UI Virtualization），仅布局可见行，
/// 从根本上解决 FlowDocument 无虚拟化导致的大脚本卡顿问题。
/// </summary>
public partial class LocalHistoryDiffDialog : Window
{
    private readonly string _historicalScript;
    private readonly ILoggerService _logger;
    private readonly Func<Task<(string? leftScript, string? rightScript)>>? _retryCallback;
    private bool _scrollSyncing;
    private bool _scrollSyncEnabled = true;
    private bool _wordWrap = true;

    // 差异标记配色
    private static readonly SolidColorBrush AddedBg = new(Color.FromRgb(230, 255, 230));    // 浅绿
    private static readonly SolidColorBrush DeletedBg = new(Color.FromRgb(255, 230, 230));   // 浅红
    private static readonly SolidColorBrush ModifiedBg = new(Color.FromRgb(255, 255, 220));  // 浅黄
    private static readonly SolidColorBrush LineNumFg = new(Color.FromRgb(140, 140, 140));
    private static readonly SolidColorBrush LineNumBgDefault = new(Color.FromRgb(248, 248, 248));
    private static readonly SolidColorBrush ImaginaryRightBg = new(Color.FromRgb(255, 235, 238));
    // 差异点当前高亮底色（与 增/删/改 底色区分）
    private static readonly SolidColorBrush DiffHighlightBg = new(Color.FromRgb(0xBB, 0xDE, 0xFB));

    /// <summary>左右两侧差异行索引集合（按行序，排除 Unchanged / Imaginary 占位）。</summary>
    private readonly List<int> _diffLineIndexes = new();
    private int _currentDiffIndex = -1;

    private ObservableCollection<DiffLineItem>? _leftItems;
    private ObservableCollection<DiffLineItem>? _rightItems;

    private ScrollViewer? _leftSv;
    private ScrollViewer? _rightSv;

    public LocalHistoryDiffDialog(
        string leftTitle,
        string leftScript,
        string rightTitle,
        string rightScript,
        string restoreScript,
        ILoggerService logger,
        Func<Task<(string? leftScript, string? rightScript)>>? retryCallback = null)
    {
        InitializeComponent();
        WordWrapButton.IsChecked = true;   // 默认自动换行；放在 InitializeComponent 之后，避免构造期间触发事件访问未就绪字段
        Loaded += OnDialogLoaded;

        _historicalScript = rightScript ?? "";
        _logger = logger;
        _retryCallback = retryCallback;

        LeftHeader.Text = leftTitle;
        RightHeader.Text = rightTitle;
        Title = $"版本差异对比 — {rightTitle}";

        if (string.IsNullOrEmpty(leftScript) && retryCallback != null)
            LeftOverlay.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(rightScript) && retryCallback != null)
            RightOverlay.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(leftScript) || !string.IsNullOrEmpty(rightScript))
            RenderDiff(leftScript ?? "", rightScript ?? "");

        UpdateNavStatus();
    }

    // ============================================================
    // 差异计算与渲染（ListView 虚拟化）
    // ============================================================

    private void RenderDiff(string leftText, string rightText)
    {
        var diffModel = SideBySideDiffBuilder.Diff(leftText, rightText, false, false);
        var leftLines = diffModel.OldText.Lines;
        var rightLines = diffModel.NewText.Lines;

        int maxPos = Math.Max(
            leftLines.Where(l => l.Position.HasValue).Select(l => l.Position!.Value).DefaultIfEmpty(0).Max(),
            rightLines.Where(l => l.Position.HasValue).Select(l => l.Position!.Value).DefaultIfEmpty(0).Max());
        int numWidth = Math.Max(2, maxPos.ToString().Length);

        _leftItems = BuildItems(leftLines, numWidth, isLeft: true);
        _rightItems = BuildItems(rightLines, numWidth, isLeft: false);
        LeftList.ItemsSource = _leftItems;
        RightList.ItemsSource = _rightItems;

        CollectDiffIndexes(leftLines, rightLines);
    }

    private static ObservableCollection<DiffLineItem> BuildItems(IList<DiffPiece> lines, int numWidth, bool isLeft)
    {
        var items = new ObservableCollection<DiffLineItem>();
        foreach (var line in lines)
        {
            var item = new DiffLineItem();
            var marker = line.Type == ChangeType.Inserted ? "+"
                       : line.Type == ChangeType.Deleted ? "-"
                       : line.Type == ChangeType.Modified ? "~" : null;
            var num = line.Position.HasValue ? line.Position.Value.ToString().PadLeft(numWidth) : new string(' ', numWidth);
            item.LineNumberText = (marker != null ? marker + " " : "") + num + " | ";
            item.LineNumberBg = line.Position.HasValue
                ? LineNumBgDefault
                : (isLeft ? DeletedBg : ImaginaryRightBg);

            switch (line.Type)
            {
                case ChangeType.Unchanged:
                    item.BaseRowBg = Brushes.White;
                    item.SubPieces = new List<DiffPiece> { new DiffPiece(line.Text ?? "", ChangeType.Unchanged) };
                    break;
                case ChangeType.Inserted:
                    item.BaseRowBg = AddedBg;
                    item.SubPieces = NormalizeSubPieces(line, ChangeType.Inserted);
                    break;
                case ChangeType.Deleted:
                    item.BaseRowBg = DeletedBg;
                    item.SubPieces = NormalizeSubPieces(line, ChangeType.Deleted);
                    break;
                case ChangeType.Modified:
                    item.BaseRowBg = ModifiedBg;
                    item.SubPieces = NormalizeSubPieces(line, ChangeType.Modified);
                    break;
                case ChangeType.Imaginary:
                    item.BaseRowBg = isLeft ? DeletedBg : ImaginaryRightBg;
                    item.SubPieces = null; // 占位行无文本
                    break;
            }

            item.RowBg = item.BaseRowBg;
            items.Add(item);
        }
        return items;
    }

    private static IList<DiffPiece> NormalizeSubPieces(DiffPiece line, ChangeType forced)
    {
        if (line.SubPieces != null && line.SubPieces.Count > 1)
            return line.SubPieces.Where(s => s.Type != ChangeType.Imaginary).ToList();
        return new List<DiffPiece> { new DiffPiece(line.Text ?? "", forced) };
    }

    // ============================================================
    // 差异点导航（基于行索引）
    // ============================================================

    private void CollectDiffIndexes(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
    {
        _diffLineIndexes.Clear();
        _currentDiffIndex = -1;

        int n = Math.Min(leftLines.Count, rightLines.Count);
        for (int i = 0; i < n; i++)
        {
            var lt = leftLines[i].Type;
            var rt = rightLines[i].Type;
            bool isDiff = lt == ChangeType.Inserted || lt == ChangeType.Deleted || lt == ChangeType.Modified
                       || rt == ChangeType.Inserted || rt == ChangeType.Deleted || rt == ChangeType.Modified;
            if (!isDiff) continue;
            _diffLineIndexes.Add(i);
        }

        if (_diffLineIndexes.Count > 0)
            GoToDiff(0);
        else
            UpdateNavStatus();
    }

    private void GoToDiff(int idx)
    {
        if (_diffLineIndexes.Count == 0 || idx < 0 || idx >= _diffLineIndexes.Count) return;

        if (_currentDiffIndex >= 0 && _currentDiffIndex < _diffLineIndexes.Count)
        {
            var prev = _diffLineIndexes[_currentDiffIndex];
            if (_leftItems != null && prev < _leftItems.Count) _leftItems[prev].RowBg = _leftItems[prev].BaseRowBg;
            if (_rightItems != null && prev < _rightItems.Count) _rightItems[prev].RowBg = _rightItems[prev].BaseRowBg;
        }

        _currentDiffIndex = idx;
        var cur = _diffLineIndexes[idx];
        if (_leftItems != null && cur < _leftItems.Count) _leftItems[cur].RowBg = DiffHighlightBg;
        if (_rightItems != null && cur < _rightItems.Count) _rightItems[cur].RowBg = DiffHighlightBg;

        if (_leftItems != null && cur < _leftItems.Count) LeftList.ScrollIntoView(_leftItems[cur]);
        if (_rightItems != null && cur < _rightItems.Count) RightList.ScrollIntoView(_rightItems[cur]);

        UpdateNavStatus();
    }

    private void GoPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDiffIndex > 0) GoToDiff(_currentDiffIndex - 1);
    }

    private void GoNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDiffIndex >= 0 && _currentDiffIndex < _diffLineIndexes.Count - 1) GoToDiff(_currentDiffIndex + 1);
    }

    private void UpdateNavStatus()
    {
        int total = _diffLineIndexes.Count;
        int cur = _currentDiffIndex + 1;
        if (DiffStatusText != null)
            DiffStatusText.Text = total == 0 ? "差异 0 / 0" : $"差异 {cur} / {total}";
        if (PrevDiffButton != null) PrevDiffButton.IsEnabled = _currentDiffIndex > 0;
        if (NextDiffButton != null) NextDiffButton.IsEnabled = _currentDiffIndex >= 0 && _currentDiffIndex < total - 1;
    }

    // ============================================================
    // 自动换行 / 横向滚动 切换
    // ============================================================

    private void WordWrap_Checked(object sender, RoutedEventArgs e)
    {
        _wordWrap = true;
        ApplyWrap(LeftList);
        ApplyWrap(RightList);
    }

    private void WordWrap_Unchecked(object sender, RoutedEventArgs e)
    {
        _wordWrap = false;
        ApplyWrap(LeftList);
        ApplyWrap(RightList);
    }

    /// <summary>
    /// 控制 ListView 内容的横向约束与横向滚动条：
    /// 自动换行模式 => 内容宽度约束为视口宽（文本折行），隐藏横滚条；
    /// 不换行模式 => 内容宽度不限（单行超宽），显示横滚条。
    /// 仅修改 ItemsPresenter.MaxWidth 与 HorizontalScrollBarVisibility，不重建元素。
    /// </summary>
    private void ApplyWrap(ListView listView)
    {
        var sv = GetScrollViewer(listView);
        if (sv == null) return;
        var presenter = FindVisualChild<ItemsPresenter>(sv) ?? FindVisualChild<ItemsPresenter>(listView);
        if (presenter != null)
            presenter.MaxWidth = _wordWrap ? listView.ActualWidth : double.PositiveInfinity;
        ScrollViewer.SetHorizontalScrollBarVisibility(listView,
            _wordWrap ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
    }

    // ============================================================
    // 同步滚动（监听 ListView 内部 ScrollViewer）
    // ============================================================

    private void SyncScroll_Checked(object sender, RoutedEventArgs e)
    {
        _scrollSyncEnabled = true;
        SyncScrollButton.Content = "🔗 同步滚动";
    }

    private void SyncScroll_Unchecked(object sender, RoutedEventArgs e)
    {
        _scrollSyncEnabled = false;
        SyncScrollButton.Content = "🔓 独立滚动";
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrollSyncing) return;
        if (!_scrollSyncEnabled) return;
        _scrollSyncing = true;

        try
        {
            if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0) return;

            var sourceSv = sender as ScrollViewer;
            var targetSv = sourceSv == _leftSv ? _rightSv : _leftSv;
            if (sourceSv == null || targetSv == null) return;

            double sourceVMax = Math.Max(1, sourceSv.ScrollableHeight);
            double vRatio = sourceSv.VerticalOffset / sourceVMax;
            targetSv.ScrollToVerticalOffset(vRatio * Math.Max(1, targetSv.ScrollableHeight));

            double sourceHMax = Math.Max(1, sourceSv.ScrollableWidth);
            double hRatio = sourceSv.HorizontalOffset / sourceHMax;
            targetSv.ScrollToHorizontalOffset(hRatio * Math.Max(1, targetSv.ScrollableWidth));
        }
        finally
        {
            _scrollSyncing = false;
        }
    }

    // ============================================================
    // 布局完成后的初始化（订阅滚动 / 应用换行 / 补一次跳转）
    // ============================================================

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        EnsureScrollViewers();
        ApplyWrap(LeftList);
        ApplyWrap(RightList);

        if (_currentDiffIndex >= 0 && _currentDiffIndex < _diffLineIndexes.Count)
        {
            var cur = _diffLineIndexes[_currentDiffIndex];
            if (_leftItems != null && cur < _leftItems.Count) LeftList.ScrollIntoView(_leftItems[cur]);
            if (_rightItems != null && cur < _rightItems.Count) RightList.ScrollIntoView(_rightItems[cur]);
        }
    }

    private void EnsureScrollViewers()
    {
        _leftSv = GetScrollViewer(LeftList);
        _rightSv = GetScrollViewer(RightList);
        if (_leftSv != null) _leftSv.ScrollChanged += OnScrollChanged;
        if (_rightSv != null) _rightSv.ScrollChanged += OnScrollChanged;

        // 窗口尺寸变化时，自动换行模式需重新约束内容宽度
        LeftList.SizeChanged += (_, _) => { if (_wordWrap) ApplyWrap(LeftList); };
        RightList.SizeChanged += (_, _) => { if (_wordWrap) ApplyWrap(RightList); };
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject dep)
    {
        if (dep is null) return null;
        if (dep is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
        {
            var child = VisualTreeHelper.GetChild(dep, i);
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject dep) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
        {
            var child = VisualTreeHelper.GetChild(dep, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // ============================================================
    // 重试加载
    // ============================================================

    private async void LeftRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_retryCallback == null) return;
        LeftRetryButton.IsEnabled = false;
        LeftRetryButton.Content = "重试中...";
        try
        {
            var (leftScript, rightScript) = await _retryCallback();
            if (!string.IsNullOrEmpty(leftScript))
            {
                LeftOverlay.Visibility = Visibility.Collapsed;
                RenderDiff(leftScript, rightScript ?? _historicalScript);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[LocalHistory] 左侧脚本重试加载失败");
        }
        finally
        {
            LeftRetryButton.IsEnabled = true;
            LeftRetryButton.Content = "重试";
        }
    }

    private async void RightRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_retryCallback == null) return;
        RightRetryButton.IsEnabled = false;
        RightRetryButton.Content = "重试中...";
        try
        {
            var (leftScript, rightScript) = await _retryCallback();
            if (!string.IsNullOrEmpty(rightScript))
            {
                RightOverlay.Visibility = Visibility.Collapsed;
                RenderDiff(leftScript ?? "", rightScript);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[LocalHistory] 右侧脚本重试加载失败");
        }
        finally
        {
            RightRetryButton.IsEnabled = true;
            RightRetryButton.Content = "重试";
        }
    }

    // ============================================================
    // Action Buttons
    // ============================================================

    /// <summary>
    /// 用户点击"在编辑器中打开"时携带的历史脚本；为 null 表示未请求打开。
    /// 真正的 DTE 新建查询 + 写脚本动作放到对话框关闭后（调用方 ShowDialog 返回的 Dispatcher 上下文），
    /// 以避免在模态对话框内部直接调用 DTE.ExecuteCommand 的嵌套消息泵阻塞/死锁风险。
    /// </summary>
    public string? ScriptToOpenInEditor { get; private set; }

    private void OpenInEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_historicalScript))
        {
            _logger.Warning("[LocalHistory] 在编辑器中打开失败：历史脚本为空");
            return;
        }

        // 不在模态对话框内部直接调用 DTE：SSMS 是 VS 隔离外壳，其消息泵由 WPF 嵌套 Dispatcher 驱动，
        // 在 ShowDialog 内部调用 dte.ExecuteCommand 存在命令路由阻塞/死锁风险。
        // 这里仅把待打开脚本交给调用方，在 ShowDialog 返回后的 UI 线程 Dispatcher 上执行真正的
        // "新建查询 + 写入脚本"（与现有 dte?.ActiveDocument?.Activate() 同源、已验证可用）。
        ScriptToOpenInEditor = _historicalScript;
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// 差异行视图模型：ListView 的 ItemsSource 元素。
/// RowBg 实现 INotifyPropertyChanged，差异高亮通过修改 RowBg 即时反映到 UI（绑定 ListViewItem.Background）。
/// </summary>
public sealed class DiffLineItem : INotifyPropertyChanged
{
    private Brush _rowBg = Brushes.White;

    public int? LineNumber { get; set; }
    public string LineNumberText { get; set; } = "";
    public Brush LineNumberBg { get; set; } = Brushes.Transparent;
    public Brush BaseRowBg { get; set; } = Brushes.White;
    public IList<DiffPiece>? SubPieces { get; set; }

    public Brush RowBg
    {
        get => _rowBg;
        set
        {
            if (!Equals(_rowBg, value))
            {
                _rowBg = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// TextBlock 附加属性：将 DiffPlex 的逐字符 diff 子片段渲染为带颜色的 Run 集合。
/// 仅对当前可见的 ListViewItem 触发（UI 虚拟化），大脚本下不会一次性构建全部 Run。
/// </summary>
public static class DiffTextBlock
{
    private static readonly SolidColorBrush AddedWordBg = new(Color.FromRgb(76, 175, 80));
    private static readonly SolidColorBrush DeletedWordBg = new(Color.FromRgb(229, 57, 53));
    private static readonly SolidColorBrush ModifiedWordBg = new(Color.FromRgb(245, 127, 23));

    public static readonly DependencyProperty DiffPiecesProperty =
        DependencyProperty.RegisterAttached(
            "DiffPieces", typeof(IList<DiffPiece>), typeof(DiffTextBlock),
            new PropertyMetadata(null, OnDiffPiecesChanged));

    public static void SetDiffPieces(TextBlock element, IList<DiffPiece>? value) => element.SetValue(DiffPiecesProperty, value);
    public static IList<DiffPiece>? GetDiffPieces(TextBlock element) => (IList<DiffPiece>?)element.GetValue(DiffPiecesProperty);

    private static void OnDiffPiecesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var pieces = e.NewValue as IList<DiffPiece>;
        if (pieces == null) return;

        foreach (var sp in pieces)
        {
            if (sp.Type == ChangeType.Imaginary) continue;
            bool isChange = sp.Type != ChangeType.Unchanged;
            var run = new Run(sp.Text ?? "")
            {
                Foreground = isChange ? Brushes.White : Brushes.Black,
                Background = sp.Type switch
                {
                    ChangeType.Inserted => AddedWordBg,
                    ChangeType.Deleted => DeletedWordBg,
                    ChangeType.Modified => ModifiedWordBg,
                    _ => Brushes.Transparent
                }
            };
            tb.Inlines.Add(run);
        }
    }
}
