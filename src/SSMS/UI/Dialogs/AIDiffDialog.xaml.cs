using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DiffPlex.DiffBuilder.Model;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.DiffEngine.Models;
using SSMSBoostPlus.Interfaces;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// Side-by-side diff dialog for version comparison.
/// Left: DB version (read-only). Right: Your script (editable).
/// Each diff line on the right side has an inline ▶ button to sync that line from DB.
/// Prev/Next navigation moves the caret in the right (Local) panel and highlights current diff yellow.
/// Key design: DiffPlex SideBySideDiffBuilder produces OldText.Lines and NewText.Lines with the SAME count
/// and matching indices — so both FlowDocuments have the same number of Paragraph blocks, and block index N
/// on the left corresponds to block index N on the right.
/// </summary>
public partial class AIDiffDialog : Window
{
    public DiffDialogResult Result { get; private set; } = DiffDialogResult.Cancel;

    // ═══════════════════════════════════════════════════
    // 专业配色方案（柔和不刺眼，与 Notepad-- 风格对齐）
    // ═══════════════════════════════════════════════════

    // 行号区域背景色（形成左侧彩色竖条效果）
    private static readonly SolidColorBrush AddedLineNumberBg = new(Color.FromRgb(200, 230, 201));  // 浅绿
    private static readonly SolidColorBrush DeletedLineNumberBg = new(Color.FromRgb(255, 205, 210)); // 浅红
    private static readonly SolidColorBrush ModifiedLineNumberBg = new(Color.FromRgb(255, 236, 179)); // 浅黄

    // 代码文本区域背景（统一为白色，干净整洁）
    private static readonly SolidColorBrush CodeTextBackground = new(Color.FromRgb(255, 255, 255));

    // 词级高亮（用于 SubPieces）
    private static readonly SolidColorBrush AddedWordBackground = new(Color.FromRgb(76, 175, 80));
    private static readonly SolidColorBrush AddedWordForeground = Brushes.White;
    private static readonly SolidColorBrush DeletedWordBackground = new(Color.FromRgb(229, 57, 53));
    private static readonly SolidColorBrush DeletedWordForeground = Brushes.White;
    private static readonly SolidColorBrush ModifiedWordBackground = new(Color.FromRgb(245, 127, 23));
    private static readonly SolidColorBrush ModifiedWordForeground = Brushes.White;

    // 行号文字
    private static readonly SolidColorBrush LineNumberBrush = new(Color.FromRgb(120, 120, 120));
    private static readonly SolidColorBrush LineNumberBgDefault = new(Color.FromRgb(248, 248, 248)); // 默认行号背景

    // 当前选中差异高亮：淡蓝（更专业）
    private static readonly SolidColorBrush CurrentDiffBackground = new(Color.FromRgb(227, 242, 253)); // #E3F2FD

    // 已同步行指示：淡青
    private static readonly SolidColorBrush SyncedBackground = new(Color.FromRgb(224, 247, 250)); // #E0F7FA

    // 同步按钮颜色
    private static readonly SolidColorBrush SyncButtonBackground = new(Color.FromRgb(0, 120, 212)); // #0078D4
    private static readonly SolidColorBrush SyncButtonBorder = new(Color.FromRgb(0, 90, 158));

    private bool _scrollSyncing;

    // Diff navigation: grouped hunks — unified block indices (same for both sides)
    private readonly List<DiffHunkInfo> _diffHunks = new();
    private int _currentHunkIndex = -1;

    // Track ALL paragraph backgrounds (not just diff lines) so we can reset after highlighting
    private readonly Dictionary<int, Brush> _leftParagraphBackgrounds = new();
    private readonly Dictionary<int, Brush> _rightParagraphBackgrounds = new();

    // Track indicator (line number area) background for diff detection
    // Unlike paragraph background (always white), indicator background is colored for diffs
    private readonly Dictionary<int, Brush> _rightIndicatorBackgrounds = new();

    // Track which blocks have been synced via ▶ button
    private readonly HashSet<int> _syncedBlocks = new();

    // 追踪"同步删除"的 block：左侧为 Imaginary（DB 中无此行）时同步，
    // 表示该行应从合并脚本中删除。
    private readonly HashSet<int> _syncedDeletedBlocks = new();

    // 存储每行同步前的原始右侧文本（用于撤销）
    private readonly Dictionary<int, string> _originalRightTexts = new();

    // 操作条中的按钮映射（blockIndex → Button），用于中间条中的行级同步/撤销
    private readonly Dictionary<int, Button> _actionButtons = new();

    // 追踪哪些 block 是占位行（Hunk-Only 模式下高度为24px，非18px）
    private readonly HashSet<int> _placeholderBlocks = new();

    // 记录每行的 ChangeType（用于 RebuildDiffHunksFromState 按类型分裂 hunk）
    private readonly Dictionary<int, ChangeType> _lineChangeTypes = new();

    // 标记用户最终选择
    private bool _hasAcceptedLeft;
    private bool _hasAcceptedRight;

    // Original source texts
    private readonly string _leftCode;
    private readonly string _rightCode;

    // ═══════════════════════════════════════════════════
    // 需求新增：冲突淡红色 + 块级同步 + 可编辑
    // ═══════════════════════════════════════════════════

    // 冲突行淡红色背景（行号区域）
    private static readonly SolidColorBrush ConflictBackground = new(Color.FromRgb(255, 235, 238)); // #FFEBEE

    // 已解决冲突背景（同步后原始冲突行标记为"已解决"）
    private static readonly SolidColorBrush ResolvedBackground = new(Color.FromRgb(255, 243, 224)); // #FFF3E0

    // 追踪已同步的 hunk → (插入的 block 索引列表, 原始冲突块起始, 原始冲突块结束)
    // key 是唯一递增的 syncId，不是 hunkIndex（避免 RebuildDiffHunksFromState 后索引重编号导致碰撞）
    private readonly Dictionary<int, (List<int> Inserted, int OrigStart, int OrigEnd)> _hunkInsertedBlocks = new();

    // 唯一同步 ID 计数器，用于 _hunkInsertedBlocks 的 key
    // 避免 hunkIndex 在 RebuildDiffHunksFromState 后重新编号导致的碰撞
    private int _nextSyncId = 0;

    // 行号重编号防抖计时器
    private System.Windows.Threading.DispatcherTimer? _renumberTimer;

    // 标记是否正在程序化修改文档（避免 TextChanged 触发重编号）
    private bool _isProgrammaticChange;

    /// <summary>
    /// 完整的数据库版本代码（左侧原始输入），供外部获取。
    /// 用于"采用数据库版本"时替换编辑器内容。
    /// </summary>
    public string LeftCode => _leftCode;

    // Hunk-Only rendering
    private const int HunkOnlyThreshold = 500;        // 超过此行数自动启用 Hunk-Only
    private const int HunkContextLines = 5;            // 差异块前后上下文行数
    private SideBySideDiffModel? _sideBySideModel;     // 保留用于展开全部
    private bool _isHunkOnlyMode;                      // 当前是否为 Hunk-Only 模式
    private bool _isLoaded;                            // 窗口已加载完成（防止异步回调访问已释放对象）
    private int _totalLineCount;                       // 原始总行数（OldText.Lines.Count）

    /// <summary>
    /// 统一行号宽度（左右面板共享），确保行号列对齐。
    /// 在 BuildFullDocuments / BuildHunkFilteredDocuments 中计算，
    /// 取左右两侧最大 Position 值的字符串宽度。
    /// </summary>
    private int _unifiedNumWidth = 2;

    

    /// <summary>
    /// A contiguous range of diff block indices with a specific change type.
    /// Since both sides have the same block count, a single range applies to both FlowDocuments.
    /// Hunks are now split by ChangeType: Inserted (新增), Deleted (删除), Modified (冲突).
    /// </summary>
    private class DiffHunkInfo
    {
        public int BlockStartIndex { get; set; }
        public int BlockEndIndex { get; set; }

        /// <summary>
        /// 差异类型：Inserted=新增, Deleted=删除, Modified=冲突
        /// </summary>
        public ChangeType HunkChangeType { get; set; } = ChangeType.Modified;
    }

    /// <summary>
    /// 提取当前右侧面板合并后的完整脚本内容，
    /// 包含所有同步/撤销操作后的最终文本。
    /// 在 Hunk-Only 模式下，基于 _sideBySideModel.NewText.Lines 重建完整脚本，
    /// 避免占位段落（"—— N 行未变更 ——"）产生垃圾文本且折叠行丢失。
    /// </summary>
    public string MergedScript
    {
        get
        {
            if (_isHunkOnlyMode && _sideBySideModel != null)
            {
                return BuildMergedScriptHunkMode();
            }

            var lines = new List<string>();
            int blockIdx = 0;
            foreach (var block in RightViewer.Document.Blocks)
            {
                if (block is Paragraph para)
                {
                    // 仅跳过"同步删除"的行（左侧 Imaginary + 已同步 → 该行应从脚本中删除）
                    // 双重检查：_syncedDeletedBlocks 索引 + 段落删除线装饰
                    if (_syncedDeletedBlocks.Contains(blockIdx) || IsParagraphSyncedDelete(para))
                    {
                        blockIdx++;
                        continue;
                    }
                    var code = ExtractCodeFromParagraph(para);
                    lines.Add(code);
                }
                blockIdx++;
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Hunk-Only 模式下重建完整脚本。
    /// 基于 _sideBySideModel.NewText.Lines（完整右代码行）重建，
    /// 已渲染的 Hunk 行从 RightViewer 段落提取文本（保留同步修改），
    /// 折叠的非差异行从模型原始文本恢复。
    /// Imaginary 行（DiffPlex 填充行）先检查段落是否有同步内容，有则包含，无则跳过。
    /// </summary>
    private string BuildMergedScriptHunkMode()
    {
        var newLines = _sideBySideModel!.NewText.Lines;
        if (newLines == null || newLines.Count == 0)
            return _rightCode;

        // 构建反向映射：原始模型行索引 → Hunk 块索引
        var origToHunk = new Dictionary<int, int>(_hunkBlockToOriginalIndex.Count);
        for (int hunkIdx = 0; hunkIdx < _hunkBlockToOriginalIndex.Count; hunkIdx++)
        {
            int origIdx = _hunkBlockToOriginalIndex[hunkIdx];
            if (origIdx >= 0)
                origToHunk[origIdx] = hunkIdx;
        }

        // 构建已同步行映射：原始行索引 → 同步的DB代码行列表
        // 同步的段落插入在 hunk.BlockStartIndex 之前，对应的原始行在 hunk.BlockStartIndex + insertedCount
        var syncedLinesBeforeOrig = new Dictionary<int, List<string>>();
        foreach (var kvp in _hunkInsertedBlocks)
        {
            var syncState = kvp.Value;
            int firstInserted = syncState.Inserted.FirstOrDefault();
            int afterSyncIdx = firstInserted + syncState.Inserted.Count;
            if (afterSyncIdx < _hunkBlockToOriginalIndex.Count)
            {
                int origIdx = _hunkBlockToOriginalIndex[afterSyncIdx];
                if (origIdx >= 0)
                {
                    var lines = new List<string>();
                    foreach (var idx in syncState.Inserted)
                    {
                        var para = GetParagraphAtIndex(RightViewer.Document, idx);
                        if (para != null)
                            lines.Add(ExtractCodeFromParagraph(para));
                    }
                    syncedLinesBeforeOrig[origIdx] = lines;
                }
            }
        }

        var result = new List<string>(newLines.Count);

        for (int origIdx = 0; origIdx < newLines.Count; origIdx++)
        {
            // 在此原始行之前插入已同步的DB代码行
            if (syncedLinesBeforeOrig.TryGetValue(origIdx, out var syncedLines))
            {
                result.AddRange(syncedLines);
            }

            var line = newLines[origIdx];

            // Imaginary 行：DiffPlex 为对齐左侧额外行插入的填充行，原始右代码中不存在
            if (line.Type == ChangeType.Imaginary)
            {
                // 检查是否被同步（段落被修改后有文本内容）
                if (origToHunk.TryGetValue(origIdx, out int hunkIdx))
                {
                    // 跳过已解决的 Imaginary 行（同步后不应出现在合并脚本中）
                    if (_rightIndicatorBackgrounds.TryGetValue(hunkIdx, out var imagBg)
                        && ReferenceEquals(imagBg, ResolvedBackground))
                        continue;

                    var para = GetParagraphAtIndex(RightViewer.Document, hunkIdx);
                    if (para != null)
                    {
                        var text = ExtractCodeFromParagraph(para);
                        if (!string.IsNullOrEmpty(text))
                            result.Add(text); // 已同步的 Imaginary 行 → 包含
                    }
                }
                // 未同步的 Imaginary 行 → 跳过（原始右代码中不存在）
                continue;
            }

            // 已渲染的 Hunk 行（在 Hunk 视图中）→ 从段落提取文本（保留同步修改）
            if (origToHunk.TryGetValue(origIdx, out int hunkIdx2))
            {
                var para = GetParagraphAtIndex(RightViewer.Document, hunkIdx2);

                // 跳过"同步删除"的行（左侧 Imaginary + 已同步 → 该行应从脚本中删除）
                // 双重检查：_syncedDeletedBlocks 索引 + 段落删除线装饰
                if (_syncedDeletedBlocks.Contains(hunkIdx2) || IsParagraphSyncedDelete(para))
                    continue;

                if (para != null)
                {
                    result.Add(ExtractCodeFromParagraph(para));
                }
                else
                {
                    result.Add(line.Text ?? string.Empty);
                }
            }
            else
            {
                // 折叠的非差异行 → 使用模型原始文本
                result.Add(line.Text ?? string.Empty);
            }
        }

        return string.Join(Environment.NewLine, result);
    }

    private class MinimapRange
    {
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public Brush Color { get; set; } = Brushes.Gray;
    }

    private readonly List<MinimapRange> _minimapRanges = new();

    // Minimap constants
    private const int MinimapWidth = 14;
    private const int MinimapMinBlockHeight = 2;

    /// <summary>
    /// Lightweight constructor: stores codes, shows loading overlay, defers diff to Loaded event.
    /// </summary>
    public AIDiffDialog(string leftCode, string rightCode, string title)
    {
        InitializeComponent();
        Title = title;

        _leftCode = leftCode;
        _rightCode = rightCode;

        // 永置顶：确保比对弹窗始终在其父窗口（过时弹窗）之上
        // 配合 VersionWarningDialog 中设置的 Owner = this，双重保障层级正确
        Topmost = true;

        // Show loading state immediately
        LoadingOverlay.Visibility = Visibility.Visible;
        DiffContentGrid.Visibility = Visibility.Collapsed;
        PrevDiffButton.IsEnabled = false;
        NextDiffButton.IsEnabled = false;

        // Defer heavy work to Loaded event (fires after window is displayed)
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Cleanup: prevent async callbacks from accessing disposed window.
    /// </summary>
    private void OnClosed(object sender, EventArgs e)
    {
        _isLoaded = false;
        _renumberTimer?.Stop();
        _renumberTimer = null;
        // 不清空 _hunkInsertedBlocks 等状态 — VersionWarningDialog 的 Closed handler
        // 在本方法之后执行，需要读取 MergedScript（依赖这些状态）。
        // 对象将被 GC 回收，无需手动清空。
    }

    /// <summary>
    /// Window.Loaded handler: runs diff calculation on background thread,
    /// then builds FlowDocuments on UI thread, hides loading overlay.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        Loaded -= OnLoaded; // 仅执行一次

        try
        {
            // Phase 1: DiffPlex on background thread (Myers O(N×D), can be slow for large texts)
            LoadingDetailText.Text = "DiffPlex 引擎正在分析代码变更...";
            _sideBySideModel = await System.Threading.Tasks.Task.Run(() =>
            {
                var calculator = new DiffCalculator();
                return calculator.GetSideBySideDiff(_leftCode, _rightCode);
            });

            if (!_isLoaded) return; // 窗口已在加载期间关闭

            // Phase 2: Determine rendering mode
            _totalLineCount = Math.Max(
                _sideBySideModel.OldText.Lines?.Count ?? 0,
                _sideBySideModel.NewText.Lines?.Count ?? 0);

            bool hasChanges = _sideBySideModel.NewText.Lines?.Any(
                l => l.Type != ChangeType.Unchanged) ?? false;

            // 阻止 TextChanged 在文档构建期间触发 RenumberTimer（防止事件风暴导致 UI 冻结）
            _isProgrammaticChange = true;
            try
            {
                if (!hasChanges)
                {
                    // No differences → full render (trivially fast even for large files)
                    _isHunkOnlyMode = false;
                    BuildFullDocuments(_sideBySideModel);
                }
                else if (_totalLineCount > HunkOnlyThreshold)
                {
                    // Large file with changes → Hunk-Only mode
                    _isHunkOnlyMode = true;
                    BuildHunkFilteredDocuments(_sideBySideModel);
                }
                else
                {
                    // Small file → full render
                    _isHunkOnlyMode = false;
                    BuildFullDocuments(_sideBySideModel);
                }
            }
            finally
            {
                _isProgrammaticChange = false;
            }

            if (!_isLoaded) return;

            // Phase 2c: 初始行号编号（不依赖布局，可在折叠状态下执行）
            _isProgrammaticChange = true;
            try
            {
                RenumberRightParagraphs();
            }
            finally
            {
                _isProgrammaticChange = false;
            }

            // Phase 3: Setup navigation and scrolling
            LeftViewer.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnScrollChanged));
            RightViewer.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnScrollChanged));

            UpdateDiffNavigation();

            // Phase 4: Show content FIRST, then populate buttons
            LoadingOverlay.Visibility = Visibility.Collapsed;
            DiffContentGrid.Visibility = Visibility.Visible;

            // Phase 4b: 等 WPF 完成布局后再填充差异按钮
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isLoaded) return;
                PopulateDiffButtons();
            }), System.Windows.Threading.DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            if (_isLoaded)
            {
                LoadingDetailText.Text = $"❌ 差异计算失败: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Build full FlowDocuments (existing logic, extracted from constructor).
    /// </summary>
    private void BuildFullDocuments(SideBySideDiffModel model)
    {
        // Stats
        var addedCount = model.NewText.Lines?.Count(l => l.Type == ChangeType.Inserted) ?? 0;
        var deletedCount = model.OldText.Lines?.Count(l => l.Type == ChangeType.Deleted) ?? 0;
        var modifiedLeftCount = model.OldText.Lines?.Count(l => l.Type == ChangeType.Modified) ?? 0;
        var modifiedRightCount = model.NewText.Lines?.Count(l => l.Type == ChangeType.Modified) ?? 0;
        var modifiedCount = Math.Max(modifiedLeftCount, modifiedRightCount);
        var totalChanges = addedCount + deletedCount + modifiedCount;

        string stats;
        if (totalChanges == 0)
        {
            stats = "✓ 两个版本完全一致，无差异";
        }
        else
        {
            var parts = new List<string>();
            if (addedCount > 0) parts.Add($"新增 {addedCount} 行");
            if (deletedCount > 0) parts.Add($"删除 {deletedCount} 行");
            if (modifiedCount > 0) parts.Add($"修改 {modifiedCount} 行");
            stats = $"📊 共检测到 {totalChanges} 处差异：{string.Join("，", parts)}";
        }
        StatsText.Text = stats;

        _leftParagraphBackgrounds.Clear();
        _rightParagraphBackgrounds.Clear();
        _rightIndicatorBackgrounds.Clear();
        _lineChangeTypes.Clear();
        _placeholderBlocks.Clear();
        _syncedBlocks.Clear();
        _syncedDeletedBlocks.Clear();
        _originalRightTexts.Clear();
        _actionButtons.Clear();

        // 计算统一行号宽度：取左右两侧最大 Position 值
        int leftMaxPos = model.OldText.Lines?.Where(l => l.Position.HasValue).Select(l => l.Position.Value).DefaultIfEmpty(0).Max() ?? 0;
        int rightMaxPos = model.NewText.Lines?.Where(l => l.Position.HasValue).Select(l => l.Position.Value).DefaultIfEmpty(0).Max() ?? 0;
        _unifiedNumWidth = Math.Max(2, Math.Max(leftMaxPos, rightMaxPos).ToString().Length);

        LeftViewer.Document = BuildSideDocument(model.OldText, isLeft: true, _leftParagraphBackgrounds, _unifiedNumWidth);
        RightViewer.Document = BuildSideDocument(model.NewText, isLeft: false, _rightParagraphBackgrounds, _unifiedNumWidth);

        BuildDiffHunks(model);

        // Hunk-Only UI elements
        LineCountText.Visibility = Visibility.Collapsed;
        ExpandAllButton.Visibility = Visibility.Collapsed;

        // Show SyncAllButton and Minimap when there are diffs
        SyncAllButton.Visibility = totalChanges > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildMinimap(model);
    }

    // ============================================================
    // Hunk-Only Rendering (Plan A)
    // ============================================================

    /// <summary>
    /// Map: hunk-only block index → original SideBySideDiffModel line index.
    /// Used for navigation remapping and SyncLine.
    /// </summary>
    private readonly List<int> _hunkBlockToOriginalIndex = new();

    /// <summary>
    /// Build hunk-filtered FlowDocuments for both sides.
    /// Only renders diff regions + context (±5 lines) + placeholder gaps.
    /// </summary>
    private void BuildHunkFilteredDocuments(SideBySideDiffModel model)
    {
        var leftLines = model.OldText.Lines;
        var rightLines = model.NewText.Lines;
        if (leftLines == null || rightLines == null) return;

        // Step 1: Find diff ranges (original indices where either side is not Unchanged)
        var diffRanges = FindDiffRanges(leftLines, rightLines);

        // Step 2: Expand with context and merge overlapping
        var mergedRanges = ExpandAndMergeRanges(diffRanges, leftLines.Count);

        // Step 3: Build filtered documents
        _hunkBlockToOriginalIndex.Clear();
        _leftParagraphBackgrounds.Clear();
        _rightParagraphBackgrounds.Clear();
        _rightIndicatorBackgrounds.Clear();
        _lineChangeTypes.Clear();
        _placeholderBlocks.Clear();

        // 计算统一行号宽度：取左右两侧最大 Position 值
        int leftMaxPos = leftLines.Where(l => l.Position.HasValue).Select(l => l.Position!.Value).DefaultIfEmpty(0).Max();
        int rightMaxPos = rightLines.Where(l => l.Position.HasValue).Select(l => l.Position!.Value).DefaultIfEmpty(0).Max();
        _unifiedNumWidth = Math.Max(2, Math.Max(leftMaxPos, rightMaxPos).ToString().Length);

        LeftViewer.Document = BuildHunkFilteredDocument(
            leftLines, mergedRanges, isLeft: true,
            _leftParagraphBackgrounds, _hunkBlockToOriginalIndex, _unifiedNumWidth);
        RightViewer.Document = BuildHunkFilteredDocument(
            rightLines, mergedRanges, isLeft: false,
            _rightParagraphBackgrounds, _hunkBlockToOriginalIndex, _unifiedNumWidth);

        // Step 4: Build diff hunks for the filtered document
        BuildHunkDiffHunks(model, mergedRanges);

        // Step 5: Stats and UI
        var addedCount = rightLines.Count(l => l.Type == ChangeType.Inserted);
        var deletedCount = leftLines.Count(l => l.Type == ChangeType.Deleted);
        var modifiedCount = Math.Max(
            leftLines.Count(l => l.Type == ChangeType.Modified),
            rightLines.Count(l => l.Type == ChangeType.Modified));
        var totalChanges = addedCount + deletedCount + modifiedCount;

        var parts = new List<string>();
        if (addedCount > 0) parts.Add($"新增 {addedCount} 行");
        if (deletedCount > 0) parts.Add($"删除 {deletedCount} 行");
        if (modifiedCount > 0) parts.Add($"修改 {modifiedCount} 行");

        var renderedLines = _hunkBlockToOriginalIndex
            .Count(i => i >= 0 && i < leftLines.Count
                && (leftLines[i].Type != ChangeType.Unchanged || rightLines[i].Type != ChangeType.Unchanged));

        StatsText.Text = $"📊 共检测到 {totalChanges} 处差异：{string.Join("，", parts)}  " +
                         $"[差异聚焦模式 — 已折叠 {_totalLineCount - renderedLines} 行]";
        LineCountText.Text = $"总行数：{_totalLineCount} | 当前渲染：{renderedLines}";
        LineCountText.Visibility = Visibility.Visible;
        ExpandAllButton.Visibility = Visibility.Visible;
        ExpandAllButton.IsChecked = false;

        SyncAllButton.Visibility = totalChanges > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildMinimap(model);
    }

    /// <summary>
    /// Find all contiguous diff ranges (original line indices) where either side differs.
    /// </summary>
    private static List<(int Start, int End)> FindDiffRanges(
        IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
    {
        var ranges = new List<(int Start, int End)>();
        int? rangeStart = null;

        for (int i = 0; i < leftLines.Count; i++)
        {
            var leftType = i < leftLines.Count ? leftLines[i].Type : ChangeType.Unchanged;
            var rightType = i < rightLines.Count ? rightLines[i].Type : ChangeType.Unchanged;
            bool isDiff = leftType != ChangeType.Unchanged || rightType != ChangeType.Unchanged;

            if (isDiff)
            {
                if (rangeStart == null) rangeStart = i;
            }
            else if (rangeStart != null)
            {
                ranges.Add((rangeStart.Value, i - 1));
                rangeStart = null;
            }
        }

        if (rangeStart != null)
            ranges.Add((rangeStart.Value, leftLines.Count - 1));

        return ranges;
    }

    /// <summary>
    /// Expand each range with context lines and merge overlapping ranges.
    /// </summary>
    private List<(int Start, int End)> ExpandAndMergeRanges(
        List<(int Start, int End)> ranges, int totalLines)
    {
        // Expand
        var expanded = new List<(int Start, int End)>();
        foreach (var (start, end) in ranges)
        {
            int newStart = Math.Max(0, start - HunkContextLines);
            int newEnd = Math.Min(totalLines - 1, end + HunkContextLines);
            expanded.Add((newStart, newEnd));
        }

        // Merge overlapping
        if (expanded.Count <= 1) return expanded;

        var merged = new List<(int Start, int End)>();
        var current = expanded[0];

        for (int i = 1; i < expanded.Count; i++)
        {
            if (expanded[i].Start <= current.End + 1) // +1 for adjacent ranges
            {
                current = (current.Start, Math.Max(current.End, expanded[i].End));
            }
            else
            {
                merged.Add(current);
                current = expanded[i];
            }
        }
        merged.Add(current);

        return merged;
    }

    /// <summary>
    /// Build a hunk-filtered FlowDocument: only render lines in merged ranges,
    /// with placeholder paragraphs between ranges.
    /// </summary>
    private FlowDocument BuildHunkFilteredDocument(
        IList<DiffPiece> lines,
        List<(int Start, int End)> mergedRanges,
        bool isLeft,
        Dictionary<int, Brush> paragraphBackgrounds,
        List<int> blockToOriginalMap,
        int numWidth)
    {
        var doc = new FlowDocument
        {
            PageWidth = double.NaN,
            ColumnWidth = double.PositiveInfinity,
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        if (lines.Count == 0)
        {
            var emptyPara = new Paragraph(new Run("(empty)")) { Foreground = LineNumberBrush, LineHeight = 18 };
            paragraphBackgrounds[0] = Brushes.Transparent;
            doc.Blocks.Add(emptyPara);
            return doc;
        }


        int hunkBlockIndex = 0;
        int lastEnd = -1;

        foreach (var (start, end) in mergedRanges)
        {
            // Placeholder for gap before this range
            int gapLines = start - lastEnd - 1;
            if (gapLines > 0)
            {
                AddPlaceholderParagraph(doc, gapLines, paragraphBackgrounds, ref hunkBlockIndex,
                    blockToOriginalMap, isLeft);
            }

            // Render lines in this range
            for (int i = start; i <= end && i < lines.Count; i++)
            {
                var line = lines[i];
                var para = BuildParagraph(line, isLeft, numWidth, hunkBlockIndex);
                doc.Blocks.Add(para);
                paragraphBackgrounds[hunkBlockIndex] = para.Background;
                if (!isLeft)
                {
                    _rightIndicatorBackgrounds[hunkBlockIndex] = GetIndicatorBackground(line, isLeft);
                    // 右侧渲染时，line.Type 直接对应差异类型
                    _lineChangeTypes[hunkBlockIndex] = line.Type;
                }
                if (isLeft || blockToOriginalMap.Count == hunkBlockIndex)
                    blockToOriginalMap.Add(i);
                hunkBlockIndex++;
            }

            lastEnd = end;
        }

        // Placeholder for trailing gap
        int trailingGap = lines.Count - 1 - lastEnd;
        if (trailingGap > 0)
        {
            AddPlaceholderParagraph(doc, trailingGap, paragraphBackgrounds, ref hunkBlockIndex,
                blockToOriginalMap, isLeft);
        }

        return doc;
    }

    /// <summary>
    /// Add a placeholder paragraph indicating N unchanged lines were collapsed.
    /// Uses a visual separator line "—— N 行未变更 ——" instead of arrow text.
    /// </summary>
    private void AddPlaceholderParagraph(FlowDocument doc, int skippedLines,
        Dictionary<int, Brush> paragraphBackgrounds, ref int blockIndex,
        List<int> blockToOriginalMap, bool isLeft)
    {
        // 使用视觉分隔线 + 居中文字，替代原始的 "↑ N unchanged lines ↑"
        var separatorBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));

        var para = new Paragraph
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(248, 248, 248)),
            TextAlignment = TextAlignment.Center,
            LineHeight = 24,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        // 固定宽度占位（保持与普通段落一致的对齐）
        AddButtonSpacer(para);

        // 左侧横线
        var leftRun = new Run(new string('—', 6))
        {
            Foreground = separatorBrush,
            FontSize = 10
        };
        para.Inlines.Add(leftRun);

        // 中央文字
        var textRun = new Run($"  {skippedLines} 行未变更  ")
        {
            Foreground = separatorBrush,
            FontSize = 10,
            FontStyle = FontStyles.Italic
        };
        para.Inlines.Add(textRun);

        // 右侧横线
        var rightRun = new Run(new string('—', 6))
        {
            Foreground = separatorBrush,
            FontSize = 10
        };
        para.Inlines.Add(rightRun);

        doc.Blocks.Add(para);
        paragraphBackgrounds[blockIndex] = para.Background;
        if (!isLeft)
            _rightIndicatorBackgrounds[blockIndex] = LineNumberBgDefault;
        if (isLeft || blockToOriginalMap.Count == blockIndex)
            blockToOriginalMap.Add(-1); // placeholder: no original mapping
        _placeholderBlocks.Add(blockIndex); // 追踪占位行（高度24px）
        blockIndex++;
    }

    /// <summary>
    /// Build a single Paragraph from a DiffPiece (shared by full and hunk-filtered builds).
    /// Extracted from BuildSideDocument to avoid code duplication.
    /// </summary>
    private Paragraph BuildParagraph(DiffPiece line, bool isLeft, int numWidth, int blockIndex)
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 18, // 必须显式设置，否则 WPF 使用默认行高(~20-22px)导致操作条按钮错位
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, // 强制行高 = 18px
            Background = Brushes.White // 统一点击白色背景，差异通过行号区域着色标识
        };

        switch (line.Type)
        {
            case ChangeType.Unchanged:
                para.Inlines.Add(CreateLineNumber(line.Position, numWidth));
                AddSqlHighlightedRuns(para, line.Text ?? string.Empty, Brushes.Black);
                break;

            case ChangeType.Imaginary:
                var imaginaryBg = isLeft ? DeletedLineNumberBg : ConflictBackground;
                para.Inlines.Add(CreateLineNumber(null, numWidth, imaginaryBg));
                para.Inlines.Add(CreateTextRun(string.Empty, LineNumberBrush));
                break;

            case ChangeType.Inserted:
                para.Inlines.Add(CreateLineNumber(line.Position, numWidth, ConflictBackground));
                if (line.SubPieces != null && line.SubPieces.Count > 1)
                    AddWordLevelHighlights(para, line.SubPieces, AddedWordBackground, AddedWordForeground);
                else
                    AddSqlHighlightedRuns(para, line.Text ?? string.Empty, Brushes.Black);
                break;

            case ChangeType.Deleted:
                para.Inlines.Add(CreateLineNumber(line.Position, numWidth, ConflictBackground));
                if (line.SubPieces != null && line.SubPieces.Count > 1)
                    AddWordLevelHighlights(para, line.SubPieces, DeletedWordBackground, DeletedWordForeground);
                else
                    AddSqlHighlightedRuns(para, line.Text ?? string.Empty, Brushes.Black);
                break;

            case ChangeType.Modified:
                para.Inlines.Add(CreateLineNumber(line.Position, numWidth, ConflictBackground));
                if (line.SubPieces != null && line.SubPieces.Count > 1)
                    AddWordLevelHighlights(para, line.SubPieces, ModifiedWordBackground, ModifiedWordForeground);
                else
                    AddSqlHighlightedRuns(para, line.Text ?? string.Empty, Brushes.Black);
                break;

            default:
                para.Inlines.Add(CreateLineNumber(line.Position, numWidth));
                AddSqlHighlightedRuns(para, line.Text ?? string.Empty, Brushes.Black);
                break;
        }

        // 在所有行号和代码之后，在最前面插入固定宽度占位
        AddButtonSpacer(para);

        return para;
    }

    /// <summary>
    /// Build diff hunks for the hunk-filtered document, splitting by ChangeType.
    /// Each hunk's block indices correspond to the filtered block index space.
    /// </summary>
    private void BuildHunkDiffHunks(SideBySideDiffModel model,
        List<(int Start, int End)> mergedRanges)
    {
        _diffHunks.Clear();

        // Build mapping: original index → hunk block index
        var originalToHunk = new Dictionary<int, int>();
        for (int hunkIdx = 0; hunkIdx < _hunkBlockToOriginalIndex.Count; hunkIdx++)
        {
            int origIdx = _hunkBlockToOriginalIndex[hunkIdx];
            if (origIdx >= 0)
                originalToHunk[origIdx] = hunkIdx;
        }

        // For each merged range, find the diff sub-ranges split by ChangeType
        var leftLines = model.OldText.Lines;
        var rightLines = model.NewText.Lines;
        if (leftLines == null || rightLines == null) return;

        foreach (var (rangeStart, rangeEnd) in mergedRanges)
        {
            int? subHunkStart = null;
            int subHunkEnd = -1;
            ChangeType currentType = ChangeType.Unchanged;

            for (int i = rangeStart; i <= rangeEnd; i++)
            {
                var leftType = i < leftLines.Count ? leftLines[i].Type : ChangeType.Unchanged;
                var rightType = i < rightLines.Count ? rightLines[i].Type : ChangeType.Unchanged;
                bool isDiff = leftType != ChangeType.Unchanged || rightType != ChangeType.Unchanged;

                if (isDiff && originalToHunk.TryGetValue(i, out int hunkIdx))
                {
                    var lineType = ClassifyDiffLine(leftType, rightType);

                    if (subHunkStart == null)
                    {
                        subHunkStart = hunkIdx;
                        subHunkEnd = hunkIdx;
                        currentType = lineType;
                    }
                    else if (lineType != currentType)
                    {
                        // Type changed → close current sub-hunk
                        _diffHunks.Add(new DiffHunkInfo
                        {
                            BlockStartIndex = subHunkStart.Value,
                            BlockEndIndex = subHunkEnd,
                            HunkChangeType = currentType
                        });
                        subHunkStart = hunkIdx;
                        subHunkEnd = hunkIdx;
                        currentType = lineType;
                    }
                    else
                    {
                        subHunkEnd = hunkIdx;
                    }
                }
                else if (subHunkStart != null)
                {
                    _diffHunks.Add(new DiffHunkInfo
                    {
                        BlockStartIndex = subHunkStart.Value,
                        BlockEndIndex = subHunkEnd,
                        HunkChangeType = currentType
                    });
                    subHunkStart = null;
                }
            }

            if (subHunkStart != null)
            {
                _diffHunks.Add(new DiffHunkInfo
                {
                    BlockStartIndex = subHunkStart.Value,
                    BlockEndIndex = subHunkEnd,
                    HunkChangeType = currentType
                });
            }
        }
    }

    // ============================================================
    // Expand All (toggle from Hunk-Only → Full render)
    // ============================================================

    /// <summary>
    /// Toggle ON: rebuild full FlowDocuments from the cached model.
    /// </summary>
    private void ExpandAll_Checked(object sender, RoutedEventArgs e)
    {
        if (_sideBySideModel == null) return;

        _isHunkOnlyMode = false;
        _hunkBlockToOriginalIndex.Clear();
        BuildFullDocuments(_sideBySideModel);
        _currentHunkIndex = -1;
        UpdateDiffNavigation();
        ExpandAllButton.Content = "⛶ 展开全部";
    }

    /// <summary>
    /// Toggle OFF: switch back to Hunk-Only mode.
    /// </summary>
    private void ExpandAll_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_sideBySideModel == null) return;

        _isHunkOnlyMode = true;
        BuildHunkFilteredDocuments(_sideBySideModel);
        _currentHunkIndex = -1;
        UpdateDiffNavigation();
    }

    // ============================================================
    // Build FlowDocument (full — existing)
    // ============================================================

    private FlowDocument BuildSideDocument(DiffPaneModel side, bool isLeft,
        Dictionary<int, Brush> paragraphBackgrounds, int numWidth)
    {
        var doc = new FlowDocument
        {
            PageWidth = double.NaN,
            ColumnWidth = double.PositiveInfinity,
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        var lines = side.Lines;
        if (lines == null || lines.Count == 0)
        {
            var emptyPara = new Paragraph(new Run("(empty)")) { Foreground = LineNumberBrush, LineHeight = 18 };
            paragraphBackgrounds[0] = Brushes.Transparent;
            doc.Blocks.Add(emptyPara);
            return doc;
        }

        int blockIndex = 0;
        foreach (var line in lines)
        {
            var para = BuildParagraph(line, isLeft, numWidth, blockIndex);
            doc.Blocks.Add(para);
            paragraphBackgrounds[blockIndex] = para.Background;
            if (!isLeft)
                {
                    _rightIndicatorBackgrounds[blockIndex] = GetIndicatorBackground(line, isLeft);
                    _lineChangeTypes[blockIndex] = line.Type;
                }
            blockIndex++;
        }

        return doc;
    }

    /// <summary>
    /// Build the Minimap overview panel: color-coded diff positions across the file.
    /// </summary>
    private void BuildMinimap(SideBySideDiffModel model)
    {
        _minimapRanges.Clear();
        MinimapCanvas.Children.Clear();

        var leftLines = model.OldText.Lines;
        var rightLines = model.NewText.Lines;
        if (leftLines == null || rightLines == null) return;

        int totalLines = leftLines.Count;
        if (totalLines == 0) return;

        // Show minimap only when there are enough lines (>= 20) and diffs exist
        bool hasDiff = _diffHunks.Count > 0;
        if (totalLines < 20 || !hasDiff)
        {
            var parent = MinimapCanvas.Parent as Border;
            if (parent?.Parent is Border outerBorder)
                outerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var parentBorder = MinimapCanvas.Parent as Border;
        if (parentBorder?.Parent is Border outerBorder2)
            outerBorder2.Visibility = Visibility.Visible;

        double canvasHeight = MinimapCanvas.ActualHeight > 0
            ? MinimapCanvas.ActualHeight
            : 400; // fallback before layout
        double lineHeight = Math.Max(2, canvasHeight / totalLines);

        // Walk through all lines, group contiguous diff types
        int i = 0;
        while (i < totalLines)
        {
            var leftType = i < leftLines.Count ? leftLines[i].Type : ChangeType.Unchanged;
            var rightType = i < rightLines.Count ? rightLines[i].Type : ChangeType.Unchanged;
            bool isDiff = leftType != ChangeType.Unchanged || rightType != ChangeType.Unchanged;

            if (!isDiff)
            {
                i++;
                continue;
            }

            // Determine the primary color for this diff block
            Brush blockColor;
            if (rightType == ChangeType.Inserted || leftType == ChangeType.Imaginary && !true)
                blockColor = AddedLineNumberBg;
            else if (leftType == ChangeType.Deleted)
                blockColor = DeletedLineNumberBg;
            else
                blockColor = ModifiedLineNumberBg;

            // Group contiguous diff lines
            int start = i;
            while (i < totalLines)
            {
                var lt = i < leftLines.Count ? leftLines[i].Type : ChangeType.Unchanged;
                var rt = i < rightLines.Count ? rightLines[i].Type : ChangeType.Unchanged;
                if (lt == ChangeType.Unchanged && rt == ChangeType.Unchanged)
                    break;
                i++;
            }
            int end = i - 1;

            _minimapRanges.Add(new MinimapRange
            {
                StartLine = start,
                EndLine = end,
                Color = blockColor
            });

            // Draw rectangle on canvas
            double y = start * lineHeight;
            double height = Math.Max(MinimapMinBlockHeight, (end - start + 1) * lineHeight);

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = MinimapWidth,
                Height = height,
                Fill = blockColor,
                Opacity = 0.85,
                ToolTip = $"行 {start + 1} - {end + 1}"
            };
            Canvas.SetTop(rect, y);
            Canvas.SetLeft(rect, 0);
            MinimapCanvas.Children.Add(rect);
        }

        // Also draw current viewport indicator (will be updated on scroll)
        UpdateMinimapViewport(0, canvasHeight);
    }

    /// <summary>
    /// Update the minimap viewport indicator (called on scroll).
    /// </summary>
    private void UpdateMinimapViewport(double visibleStart, double visibleEnd)
    {
        // Remove old viewport indicator
        var toRemove = MinimapCanvas.Children
            .OfType<System.Windows.Shapes.Rectangle>()
            .FirstOrDefault(r => r.Stroke == Brushes.Gray);
        if (toRemove != null)
            MinimapCanvas.Children.Remove(toRemove);

        if (MinimapCanvas.ActualHeight <= 0) return;

        var totalLines = _totalLineCount;
        if (totalLines <= 0) return;

        double lineHeight = MinimapCanvas.ActualHeight / totalLines;
        double y = visibleStart / (MinimapCanvas.ActualHeight > 0 ? MinimapCanvas.ActualHeight : 1) * MinimapCanvas.ActualHeight;
        double h = Math.Max(4, (visibleEnd - visibleStart) / (MinimapCanvas.ActualHeight > 0 ? MinimapCanvas.ActualHeight : 1) * MinimapCanvas.ActualHeight);

        var viewport = new System.Windows.Shapes.Rectangle
        {
            Width = MinimapWidth,
            Height = h,
            Fill = Brushes.Transparent,
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            Opacity = 0.6
        };
        Canvas.SetTop(viewport, y);
        Canvas.SetLeft(viewport, 0);
        MinimapCanvas.Children.Add(viewport);
    }

    /// <summary>
    /// 创建块级同步按钮，根据 ChangeType 显示不同图标和颜色：
    ///   Inserted（新增）→ 绿色 + 
    ///   Deleted（删除）→ 红色 ✕
    ///   Modified（冲突/修改）→ 蓝色 ▶
    /// </summary>
    private Button CreateSyncButton(int hunkIndex, ChangeType changeType)
    {
        string content, tooltip;
        Brush bg, borderBg;

        switch (changeType)
        {
            case ChangeType.Inserted:
                content = "+";
                bg = new SolidColorBrush(Color.FromRgb(76, 175, 80));     // 绿色：新增
                borderBg = new SolidColorBrush(Color.FromRgb(56, 142, 60));
                tooltip = "从数据库新增此行（DB有，你无）";
                break;
            case ChangeType.Deleted:
                content = "−";
                bg = new SolidColorBrush(Color.FromRgb(244, 67, 54));     // 红色：删除
                borderBg = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                tooltip = "从数据库删除此行（DB无，你有）";
                break;
            default: // Modified
                content = "▶";
                bg = SyncButtonBackground;                                 // 蓝色：替换
                borderBg = SyncButtonBorder;
                tooltip = "采用数据库版本替换（双方不同）";
                break;
        }

        var button = new Button
        {
            Content = content,
            Width = 20,
            Height = 18,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = bg,
            BorderBrush = borderBg,
            BorderThickness = new Thickness(1),
            Tag = hunkIndex,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tooltip,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        button.Click += SyncBlock_Click;
        return button;
    }

    /// <summary>
    /// 创建块级撤销按钮（↺，橙色圆角），不操作面板。
    /// </summary>
    private Button CreateUndoButton(int hunkIndex)
    {
        var button = new Button
        {
            Content = "↺",
            Width = 20,
            Height = 18,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 126, 0)),
            BorderThickness = new Thickness(1),
            Tag = hunkIndex,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = "撤销同步，移除插入的数据库代码块",
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        button.Click += UndoBlock_Click;
        return button;
    }

    /// <summary>
    /// 在段落开头插入固定宽度（24px）的 InlineUIContainer 占位。
    /// 使用 InsertBefore 确保占位始终在行号和代码之前。
    /// </summary>
    private void AddButtonSpacer(Paragraph para)
    {
        var spacer = new Grid
        {
            Width = 24,
            Height = para.LineHeight,
            Background = Brushes.Transparent
        };
        var container = new InlineUIContainer(spacer)
        {
            BaselineAlignment = BaselineAlignment.Center
        };
        // 插入到最前面（行号和代码之前）
        if (para.Inlines.FirstInline != null)
            para.Inlines.InsertBefore(para.Inlines.FirstInline, container);
        else
            para.Inlines.Add(container);
    }

    /// <summary>
    /// 将差异段落的占位替换为同步/撤销按钮。
    /// 非差异段落的占位保持透明。
    /// </summary>
    private void PopulateDiffButtons()
    {
        ClearDiffButtons();

        var paras = RightViewer.Document.Blocks.OfType<Paragraph>().ToList();
        int blockCount = paras.Count;

        // 为 _diffHunks 中未同步的 hunk 替换占位为同步按钮
        for (int h = 0; h < _diffHunks.Count; h++)
        {
            var hunk = _diffHunks[h];
            int blockIndex = hunk.BlockStartIndex;
            if (blockIndex < 0 || blockIndex >= blockCount) continue;

            var button = CreateSyncButton(h, hunk.HunkChangeType);
            _actionButtons[blockIndex] = button;
            ReplaceSpacerWithButton(paras[blockIndex], button);
        }

        // 为 _hunkInsertedBlocks 中已同步的 hunk 替换占位为撤销按钮
        foreach (var kvp in _hunkInsertedBlocks)
        {
            int syncId = kvp.Key;
            var syncState = kvp.Value;

            int blockIndex;
            if (syncState.Inserted.Count > 0)
                blockIndex = syncState.Inserted.First();
            else
                blockIndex = syncState.OrigStart;

            if (blockIndex < 0 || blockIndex >= blockCount) continue;
            if (_actionButtons.ContainsKey(blockIndex)) continue;

            var button = CreateUndoButton(syncId);
            _actionButtons[blockIndex] = button;
            ReplaceSpacerWithButton(paras[blockIndex], button);
        }
    }

    /// <summary>
    /// 将段落首位的 InlineUIContainer 占位替换为按钮。
    /// 如果首位不是占位（新创建的段落），先添加占位再替换。
    /// </summary>
    private void ReplaceSpacerWithButton(Paragraph para, Button button)
    {
        // 确保段落有占位
        EnsureButtonSpacer(para);

        var firstInline = para.Inlines.FirstInline;
        if (firstInline is InlineUIContainer container)
        {
            para.Inlines.Remove(container);
        }
        var btnContainer = new InlineUIContainer(button)
        {
            BaselineAlignment = BaselineAlignment.Center
        };
        para.Inlines.InsertBefore(para.Inlines.FirstInline, btnContainer);
    }

    /// <summary>
    /// 确保段落第一个 inline 是固定宽度的占位。
    /// 如果没有（例如 SyncHunk 新创建的段落），补充插入。
    /// </summary>
    private static void EnsureButtonSpacer(Paragraph para)
    {
        var first = para.Inlines.FirstInline;
        if (first is InlineUIContainer)
            return; // 已有占位

        // 没有占位→插入一个
        var spacer = new Grid
        {
            Width = 24,
            Height = 18,
            Background = Brushes.Transparent
        };
        var container = new InlineUIContainer(spacer)
        {
            BaselineAlignment = BaselineAlignment.Center
        };
        para.Inlines.InsertBefore(first, container);
    }

    /// <summary>
    /// 从段落中移除按钮，恢复为透明占位。
    /// 只处理第一个 inline 是 InlineUIContainer 的段落。
    /// </summary>
    private void ClearDiffButtons()
    {
        var paras = RightViewer.Document.Blocks.OfType<Paragraph>().ToList();
        foreach (var para in paras)
        {
            var firstInline = para.Inlines.FirstInline;
            if (firstInline is InlineUIContainer container)
            {
                // 只替换占位中的内容为透明 Grid，不移除占位本身
                if (container.Child is Button)
                {
                    container.Child = new Grid
                    {
                        Width = 24,
                        Height = 18,
                        Background = Brushes.Transparent
                    };
                }
            }
        }
        _actionButtons.Clear();
    }

    // ═══════════════════════════════════════════════════
    // SQL 语法高亮 — 关键词着色
    // ═══════════════════════════════════════════════════

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL",
        "AS", "ON", "SET", "TOP", "DISTINCT", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ORDER", "BY", "GROUP", "HAVING", "INNER", "LEFT", "RIGHT", "JOIN",
        "INSERT", "INTO", "VALUES", "UPDATE", "DELETE",
        "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "INDEX",
        "PROCEDURE", "FUNCTION", "TRIGGER", "IF", "ELSE",
        "BEGIN", "WHILE", "RETURN", "EXEC", "EXECUTE", "DECLARE",
        "WITH", "NOLOCK", "GO", "ASC", "DESC", "FOR",
        "OUTPUT", "INTO", "EXISTS", "ANY", "ALL", "SOME",
        "UNION", "ALL", "INTERSECT", "EXCEPT",
        "CAST", "CONVERT", "COALESCE", "NULLIF",
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "OVER", "PARTITION", "ROW_NUMBER", "RANK", "DENSE_RANK",
        "CROSS", "APPLY", "OUTER", "UNION", "ALL",
    };

    private static readonly SolidColorBrush SqlKeywordBrush = new(Color.FromRgb(0, 0, 255)); // 蓝色关键词

    /// <summary>
    /// 获取差异行的指示器背景色（行号区域显示的颜色，仅右侧面板用）。
    /// 需求2：所有冲突类型统一显示为淡红色。
    /// </summary>
    private static Brush GetIndicatorBackground(DiffPiece line, bool isLeft)
    {
        return line.Type switch
        {
            ChangeType.Inserted => ConflictBackground,
            ChangeType.Deleted => ConflictBackground,
            ChangeType.Modified => ConflictBackground,
            ChangeType.Imaginary => isLeft ? DeletedLineNumberBg : ConflictBackground,
            _ => LineNumberBgDefault
        };
    }

    private static Run CreateLineNumber(int? position, int numWidth, Brush? background = null)
    {
        var num = position.HasValue ? position.Value.ToString().PadLeft(numWidth) : new string(' ', numWidth);
        return new Run(num + " | ")
        {
            Foreground = LineNumberBrush,
            Background = background ?? LineNumberBgDefault
        };
    }

    /// <summary>
    /// Create a Run or multiple Runs with SQL keyword highlighting.
    /// Simple word-boundary tokenization to colorize SQL keywords.
    /// Falls back to plain Run for non-SQL text or very short fragments.
    /// </summary>
    private static void AddSqlHighlightedRuns(Paragraph para, string text, Brush defaultForeground)
    {
        if (string.IsNullOrEmpty(text))
        {
            para.Inlines.Add(CreateTextRun(string.Empty, defaultForeground));
            return;
        }

        // Use simple tokenization: split on word boundaries
        int pos = 0;
        while (pos < text.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(text[pos]))
            {
                int wsStart = pos;
                while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
                para.Inlines.Add(CreateTextRun(text.Substring(wsStart, pos - wsStart), defaultForeground));
                continue;
            }

            // Check for SQL comment (-- or /*)
            if (pos + 1 < text.Length && text[pos] == '-' && text[pos + 1] == '-')
            {
                // Line comment: rest of the line is a comment
                var comment = text.Substring(pos);
                para.Inlines.Add(new Run(comment) { Foreground = new SolidColorBrush(Color.FromRgb(0, 128, 0)) });
                return; // rest of line is comment
            }

            // Regular word
            int wordStart = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])
                   && text[pos] != '(' && text[pos] != ')' 
                   && text[pos] != ',' && text[pos] != ';'
                   && text[pos] != '.' && text[pos] != '='
                   && text[pos] != '<' && text[pos] != '>'
                   && text[pos] != '+' && text[pos] != '-'
                   && text[pos] != '*' && text[pos] != '/')
                pos++;

            if (pos == wordStart)
            {
                // Single non-word character
                para.Inlines.Add(new Run(text[pos].ToString()) { Foreground = defaultForeground });
                pos++;
                continue;
            }

            var word = text.Substring(wordStart, pos - wordStart);
            if (SqlKeywords.Contains(word))
            {
                // SQL keyword - blue and bold
                para.Inlines.Add(new Run(word)
                {
                    Foreground = SqlKeywordBrush,
                    FontWeight = FontWeights.Bold
                });
            }
            else
            {
                para.Inlines.Add(new Run(word) { Foreground = defaultForeground });
            }

            // Handle the delimiter after the word
            if (pos < text.Length && (text[pos] == '(' || text[pos] == ')' || text[pos] == ','
                || text[pos] == ';' || text[pos] == '.' || text[pos] == '='
                || text[pos] == '<' || text[pos] == '>' || text[pos] == '+'
                || text[pos] == '-' || text[pos] == '*' || text[pos] == '/'))
            {
                para.Inlines.Add(new Run(text[pos].ToString()) { Foreground = defaultForeground });
                pos++;
            }
        }
    }

    private static Run CreateTextRun(string text, Brush foreground)
    {
        return new Run(text ?? string.Empty) { Foreground = foreground };
    }

    private static void AddWordLevelHighlights(Paragraph para, IList<DiffPiece> subPieces,
        Brush highlightBackground, Brush highlightForeground)
    {
        foreach (var piece in subPieces)
        {
            var text = piece.Text ?? string.Empty;
            switch (piece.Type)
            {
                case ChangeType.Inserted:
                case ChangeType.Deleted:
                case ChangeType.Modified:
                    para.Inlines.Add(new Run(text)
                    {
                        Foreground = highlightForeground,
                        Background = highlightBackground,
                        FontWeight = FontWeights.Bold
                    });
                    break;
                default:
                    para.Inlines.Add(new Run(text) { Foreground = Brushes.Black });
                    break;
            }
        }
    }

    // ============================================================
    // Per-block Sync (▶ button click) and Undo (↺ button click)
    // 需求1：按块处理同步
    // 需求2：同步时向上插入，DB代码新增到冲突块首行之上
    // ============================================================

    /// <summary>
    /// 检查指定 blockIndex 对应的左侧（DB）行是否为 Imaginary（空填充行），
    /// 或右侧（本地）行是否为 Inserted（仅存在于本地脚本）。
    /// </summary>
    private bool IsLeftImaginary(int blockIndex)
    {
        if (_sideBySideModel?.OldText.Lines == null) return false;

        int origIdx;
        if (_isHunkOnlyMode)
        {
            if (blockIndex < 0 || blockIndex >= _hunkBlockToOriginalIndex.Count) return false;
            origIdx = _hunkBlockToOriginalIndex[blockIndex];
            if (origIdx < 0) return false;
        }
        else
        {
            origIdx = blockIndex;
        }

        if (origIdx < 0 || origIdx >= _sideBySideModel.OldText.Lines.Count) return false;

        if (_sideBySideModel.OldText.Lines[origIdx].Type == ChangeType.Imaginary)
            return true;

        if (_sideBySideModel.NewText.Lines != null
            && origIdx < _sideBySideModel.NewText.Lines.Count
            && _sideBySideModel.NewText.Lines[origIdx].Type == ChangeType.Inserted)
            return true;

        return false;
    }

    /// <summary>
    /// 检查左侧段落是否无实际内容（Imaginary 填充行或空文本行）。
    /// </summary>
    /// <summary>
    /// 从数据库同步到我的脚本的视角，判断此行对我的脚本来说是新增/删除/替换。
    ///   数据库有，我没有 → 同步后我会新增这行 → 显示 +
    ///   数据库没有，我有 → 同步后我会删除这行 → 显示 -
    ///   双方都有但不同 → 同步后替换 → 显示 ▶
    /// </summary>
    private static ChangeType ClassifyDiffLine(ChangeType leftType, ChangeType rightType)
    {
        bool leftHasContent = leftType != ChangeType.Imaginary && leftType != ChangeType.Unchanged;
        bool rightHasContent = rightType != ChangeType.Imaginary && rightType != ChangeType.Unchanged;

        // DB有，我没有 → 同步后我会新增这行
        if (leftHasContent && !rightHasContent)
            return ChangeType.Inserted; // 显示 +

        // DB没有，我有 → 同步后我会删除这行
        if (!leftHasContent && rightHasContent)
            return ChangeType.Deleted; // 显示 -

        // 双方都有 → 替换
        return ChangeType.Modified; // 显示 ▶
    }

    private bool IsLeftEmpty(int blockIndex)
    {
        if (IsLeftImaginary(blockIndex))
            return true;

        var leftPara = GetParagraphAtIndex(LeftViewer.Document, blockIndex);
        if (leftPara == null) return false;

        var leftText = ExtractCodeFromParagraph(leftPara);
        return string.IsNullOrWhiteSpace(leftText);
    }

    /// <summary>
    /// 需求1+2：块级同步 — 将 DB 代码块整体插入到冲突块首行之上。
    /// 同步后：插入的 DB 代码行标记为 SyncedBackground（淡青色），
    /// 原始冲突行标记为 ResolvedBackground（淡橙色，表示已处理但可编辑/删除）。
    /// </summary>
    private void SyncBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int hunkIndex) return;

        try
        {
            SyncHunk(hunkIndex);

            // 所有文档操作和遍历都在 _isProgrammaticChange 保护下进行，
            // 防止 TextChanged 触发 RenumberTimer 导致集合修改冲突
            _isProgrammaticChange = true;
            try
            {
                RenumberRightParagraphs();
                RebuildDiffHunksFromState();
                PopulateDiffButtons();
            }
            finally
            {
                _isProgrammaticChange = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncBlock_Click failed: {ex}");
            MessageBox.Show($"同步操作失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 同步指定 hunk：收集 DB 代码行，在冲突块首行之上插入新段落。
    /// 同时在左侧文档插入对齐用的占位段落，保持左右行对齐。
    /// </summary>
    private void SyncHunk(int hunkIndex)
    {
        if (hunkIndex < 0 || hunkIndex >= _diffHunks.Count) return;
        var hunk = _diffHunks[hunkIndex];

        // Hunk-Only 模式下直接同步，不切换到 Full 模式（避免重建全文档导致 UI 冻结/崩溃）

        // 已同步则跳过（block 级别检查，避免 hunkIndex 碰撞）
        if (_rightIndicatorBackgrounds.TryGetValue(hunk.BlockStartIndex, out var startBg)
            && (ReferenceEquals(startBg, SyncedBackground) || ReferenceEquals(startBg, ResolvedBackground)))
            return;

        // 1. 收集 DB 代码行（跳过空行/Imaginary）
        var dbLines = new List<string>();
        for (int bi = hunk.BlockStartIndex; bi <= hunk.BlockEndIndex; bi++)
        {
            if (IsLeftEmpty(bi)) continue;
            var leftPara = GetParagraphAtIndex(LeftViewer.Document, bi);
            if (leftPara == null) continue;
            var dbText = ExtractCodeFromParagraph(leftPara);
            if (!string.IsNullOrWhiteSpace(dbText))
                dbLines.Add(dbText);
        }

        if (dbLines.Count == 0)
        {
            // DB 侧无代码行（全部为 Imaginary）→ 本地独有的行应标记为"同步删除"
            _isProgrammaticChange = true;
            try
            {
                for (int bi = hunk.BlockStartIndex; bi <= hunk.BlockEndIndex; bi++)
                {
                    // 标记为已解决 + 同步删除
                    _rightIndicatorBackgrounds[bi] = ResolvedBackground;
                    SetLineNumberBackground(RightViewer.Document, bi, ResolvedBackground);
                    _syncedDeletedBlocks.Add(bi);

                    // 使用 RichTextBox 的 TextRange API 应用删除线 + 灰色前景
                    // 直接修改 Run.TextDecorations 在 RichTextBox 中可能不渲染
                    var rightPara = GetParagraphAtIndex(RightViewer.Document, bi);
                    if (rightPara != null)
                    {
                        // 为整个段落添加删除线（跳过行号区域）
                        foreach (var inline in rightPara.Inlines.ToList())
                        {
                            if (inline is Run run && run.Text.Contains(" | "))
                            {
                                // 跳过行号 Run
                                continue;
                            }
                            if (inline is Run codeRun && !string.IsNullOrWhiteSpace(codeRun.Text))
                            {
                                try
                                {
                                    var textRange = new TextRange(codeRun.ContentStart, codeRun.ContentEnd);
                                    textRange.ApplyPropertyValue(
                                        Run.TextDecorationsProperty,
                                        new TextDecorationCollection
                                        {
                                            new TextDecoration(
                                                TextDecorationLocation.Strikethrough,
                                                new Pen(Brushes.Gray, 1.5),
                                                0,
                                                TextDecorationUnit.FontRecommended,
                                                TextDecorationUnit.FontRecommended)
                                        });
                                    textRange.ApplyPropertyValue(Run.ForegroundProperty, Brushes.Gray);
                                }
                                catch
                                {
                                    // fallback: 直接修改 Run 属性
                                    codeRun.TextDecorations = new TextDecorationCollection
                                    {
                                        new TextDecoration(
                                            TextDecorationLocation.Strikethrough,
                                            new Pen(Brushes.Gray, 1.5),
                                            0,
                                            TextDecorationUnit.FontRecommended,
                                            TextDecorationUnit.FontRecommended)
                                    };
                                    codeRun.Foreground = Brushes.Gray;
                                }
                            }
                        }
                    }
                }

                // 记录已同步的 hunk（空插入列表，仅含原始范围用于撤销）
                int syncIdEmpty = _nextSyncId++;
                _hunkInsertedBlocks[syncIdEmpty] = (new List<int>(), hunk.BlockStartIndex, hunk.BlockEndIndex);
            }
            finally
            {
                _isProgrammaticChange = false;
            }
            return;
        }

        int insertAt = hunk.BlockStartIndex;
        int insertedCount = dbLines.Count;

        // 2. 移动所有 tracking 中 >= insertAt 的索引
        _isProgrammaticChange = true;
        try
        {
            ShiftDictionaryKeys(_leftParagraphBackgrounds, insertAt, insertedCount);
            ShiftDictionaryKeys(_rightParagraphBackgrounds, insertAt, insertedCount);
            ShiftDictionaryKeys(_rightIndicatorBackgrounds, insertAt, insertedCount);
            ShiftHashSetValues(_syncedBlocks, insertAt, insertedCount);
            ShiftHashSetValues(_syncedDeletedBlocks, insertAt, insertedCount);
            ShiftHashSetValues(_placeholderBlocks, insertAt, insertedCount);
            ShiftDictionaryKeys(_originalRightTexts, insertAt, insertedCount);
            ShiftDictionaryKeys(_actionButtons, insertAt, insertedCount);
            ShiftDictionaryKeys(_lineChangeTypes, insertAt, insertedCount);

            // 3. 在左右文档中插入新段落
            var numWidth = GetMaxLineNumberWidth();
            var insertedIndices = new List<int>();

            for (int i = 0; i < insertedCount; i++)
            {
                int idx = insertAt + i;

                // 右侧：DB 代码行（已同步标记）
                var newRightPara = new Paragraph
                {
                    Margin = new Thickness(0),
                    LineHeight = 18, // 与操作条 spacer 高度一致
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Background = SyncedBackground
                };
                newRightPara.Inlines.Add(new Run(new string(' ', numWidth) + " | ")
                {
                    Foreground = LineNumberBrush,
                    Background = SyncedBackground
                });
                AddSqlHighlightedRuns(newRightPara, dbLines[i], Brushes.Black);
                // 同步插入的段落也需要有按钮占位
                AddButtonSpacer(newRightPara);

                // 左侧：占位段落（对齐用）
                var newLeftPara = new Paragraph
                {
                    Margin = new Thickness(0),
                    LineHeight = 18, // 与右侧段落高度一致
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Background = Brushes.White
                };
                newLeftPara.Inlines.Add(new Run(new string(' ', numWidth) + " | ")
                {
                    Foreground = LineNumberBrush,
                    Background = LineNumberBgDefault
                });
                newLeftPara.Inlines.Add(new Run("← 已同步到右侧") { Foreground = Brushes.LightGray, FontSize = 10 });

                InsertParagraphAtIndex(RightViewer.Document, newRightPara, idx);
                InsertParagraphAtIndex(LeftViewer.Document, newLeftPara, idx);

                _rightParagraphBackgrounds[idx] = SyncedBackground;
                _rightIndicatorBackgrounds[idx] = SyncedBackground;
                _leftParagraphBackgrounds[idx] = Brushes.White;

                insertedIndices.Add(idx);
            }

            // 4. 将原始冲突行标记为"已解决"（不再是 diff，但保留内容供编辑/删除）
            for (int bi = hunk.BlockStartIndex + insertedCount; bi <= hunk.BlockEndIndex + insertedCount; bi++)
            {
                if (_rightIndicatorBackgrounds.ContainsKey(bi)
                    && !ReferenceEquals(_rightIndicatorBackgrounds[bi], LineNumberBgDefault)
                    && !ReferenceEquals(_rightIndicatorBackgrounds[bi], SyncedBackground))
                {
                    _rightIndicatorBackgrounds[bi] = ResolvedBackground;
                    SetLineNumberBackground(RightViewer.Document, bi, ResolvedBackground);
                }
            }

            // 5. 记录已同步的 hunk（含原始冲突块范围，用于撤销时恢复）
            int syncId = _nextSyncId++;
            _hunkInsertedBlocks[syncId] = (insertedIndices, hunk.BlockStartIndex, hunk.BlockEndIndex);

            // 5b. 更新 _hunkBlockToOriginalIndex（仅在 Hunk-Only 模式下使用）
            if (_isHunkOnlyMode)
            {
                for (int i = 0; i < insertedCount; i++)
                {
                    int insertIdx = Math.Min(insertAt + i, _hunkBlockToOriginalIndex.Count);
                    _hunkBlockToOriginalIndex.Insert(insertIdx, -1);
                }
            }

            // 6. 同步其他 hunk 的索引（在 insertAt 之后的 hunk 需要偏移）
            foreach (var other in _hunkInsertedBlocks.ToList())
            {
                if (other.Key == syncId) continue;
                var shifted = other.Value.Inserted
                    .Select(v => v >= insertAt ? v + insertedCount : v)
                    .ToList();
                var origStart = other.Value.OrigStart >= insertAt ? other.Value.OrigStart + insertedCount : other.Value.OrigStart;
                var origEnd = other.Value.OrigEnd >= insertAt ? other.Value.OrigEnd + insertedCount : other.Value.OrigEnd;
                if (!shifted.SequenceEqual(other.Value.Inserted))
                    _hunkInsertedBlocks[other.Key] = (shifted, origStart, origEnd);
            }
        }
        finally
        {
            _isProgrammaticChange = false;
        }
    }

    /// <summary>
    /// 需求1+2：块级撤销 — 移除插入的 DB 代码段落，恢复原始冲突状态。
    /// </summary>
    private void UndoBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int syncId) return;

        try
        {
            UndoHunk(syncId);

            // 所有文档操作和遍历都在 _isProgrammaticChange 保护下进行
            _isProgrammaticChange = true;
            try
            {
                RenumberRightParagraphs();
                RebuildDiffHunksFromState();
                PopulateDiffButtons();
            }
            finally
            {
                _isProgrammaticChange = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UndoBlock_Click failed: {ex}");
            MessageBox.Show($"撤销操作失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 撤销指定同步操作：移除插入的段落，恢复冲突行指示器。
    /// 参数 syncId 是 _hunkInsertedBlocks 的 key（由 SyncHunk 生成的唯一 ID）。
    /// </summary>
    private void UndoHunk(int syncId)
    {
        if (!_hunkInsertedBlocks.TryGetValue(syncId, out var syncState)) return;

        var insertedIndices = syncState.Inserted;

        // 同步删除的撤销（无插入段落，仅恢复冲突行指示器 + 移除删除线）
        if (insertedIndices.Count == 0)
        {
            _isProgrammaticChange = true;
            try
            {
                for (int bi = syncState.OrigStart; bi <= syncState.OrigEnd; bi++)
                {
                    _rightIndicatorBackgrounds[bi] = ConflictBackground;
                    SetLineNumberBackground(RightViewer.Document, bi, ConflictBackground);
                    _syncedDeletedBlocks.Remove(bi);

                    // 移除删除线装饰
                    var rightPara = GetParagraphAtIndex(RightViewer.Document, bi);
                    if (rightPara != null)
                    {
                        foreach (var inline in rightPara.Inlines.ToList())
                        {
                            if (inline is Run run && run.TextDecorations != null)
                            {
                                var decorations = run.TextDecorations
                                    .Where(d => d.Location != TextDecorationLocation.Strikethrough)
                                    .ToList();
                                run.TextDecorations = decorations.Count > 0
                                    ? new TextDecorationCollection(decorations)
                                    : null;
                            }
                        }
                    }
                }

                _hunkInsertedBlocks.Remove(syncId);
            }
            finally
            {
                _isProgrammaticChange = false;
            }
            return;
        }

        int insertAt = insertedIndices.Min();
        int removedCount = insertedIndices.Count;

        _isProgrammaticChange = true;
        try
        {
            // 1. 从左右文档中移除插入的段落（从后往前删，避免索引移位）
            foreach (var idx in insertedIndices.OrderByDescending(x => x))
            {
                var rightPara = GetParagraphAtIndex(RightViewer.Document, idx);
                var leftPara = GetParagraphAtIndex(LeftViewer.Document, idx);
                if (rightPara != null) RightViewer.Document.Blocks.Remove(rightPara);
                if (leftPara != null) LeftViewer.Document.Blocks.Remove(leftPara);

                _rightParagraphBackgrounds.Remove(idx);
                _rightIndicatorBackgrounds.Remove(idx);
                _leftParagraphBackgrounds.Remove(idx);
                _actionButtons.Remove(idx);
            }

            // 2. 反向移动 tracking 中 > insertAt 的索引
            UnshiftDictionaryKeys(_leftParagraphBackgrounds, insertAt, removedCount);
            UnshiftDictionaryKeys(_rightParagraphBackgrounds, insertAt, removedCount);
            UnshiftDictionaryKeys(_rightIndicatorBackgrounds, insertAt, removedCount);
            ShiftHashSetValues(_syncedBlocks, insertAt + removedCount, -removedCount);
            ShiftHashSetValues(_syncedDeletedBlocks, insertAt + removedCount, -removedCount);
            ShiftHashSetValues(_placeholderBlocks, insertAt + removedCount, -removedCount);
            UnshiftDictionaryKeys(_originalRightTexts, insertAt, removedCount);
            UnshiftDictionaryKeys(_actionButtons, insertAt, removedCount);
            UnshiftDictionaryKeys(_lineChangeTypes, insertAt, removedCount);

            // 3. 恢复原始冲突行的指示器为 ConflictBackground
            int origStart = syncState.OrigStart;
            int origEnd = syncState.OrigEnd;
            for (int bi = origStart; bi <= origEnd; bi++)
            {
                if (_rightIndicatorBackgrounds.ContainsKey(bi)
                    && ReferenceEquals(_rightIndicatorBackgrounds[bi], ResolvedBackground))
                {
                    _rightIndicatorBackgrounds[bi] = ConflictBackground;
                    SetLineNumberBackground(RightViewer.Document, bi, ConflictBackground);
                }
            }

            // 4. 更新其他 hunk 的插入索引
            _hunkInsertedBlocks.Remove(syncId);

            // 4b. 从 _hunkBlockToOriginalIndex 中移除已撤销的条目（仅 Hunk-Only 模式）
            if (_isHunkOnlyMode)
            {
                for (int i = 0; i < removedCount; i++)
                {
                    if (insertAt < _hunkBlockToOriginalIndex.Count)
                        _hunkBlockToOriginalIndex.RemoveAt(insertAt);
                }
            }

            foreach (var other in _hunkInsertedBlocks.ToList())
            {
                var shifted = other.Value.Inserted
                    .Select(v => v > insertAt ? v - removedCount : v)
                    .ToList();
                var origStart2 = other.Value.OrigStart > insertAt ? other.Value.OrigStart - removedCount : other.Value.OrigStart;
                var origEnd2 = other.Value.OrigEnd > insertAt ? other.Value.OrigEnd - removedCount : other.Value.OrigEnd;
                if (!shifted.SequenceEqual(other.Value.Inserted))
                    _hunkInsertedBlocks[other.Key] = (shifted, origStart2, origEnd2);
            }
        }
        finally
        {
            _isProgrammaticChange = false;
        }
    }

    // ═══════════════════════════════════════════════════
    // 辅助方法：索引移位、段落插入、行号重编号
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 将字典中 key >= threshold 的所有键向后移动 shift 位。
    /// </summary>
    private static void ShiftDictionaryKeys<T>(Dictionary<int, T> dict, int threshold, int shift)
    {
        if (shift <= 0) return;
        var keysToShift = dict.Keys.Where(k => k >= threshold).OrderByDescending(k => k).ToList();
        foreach (var key in keysToShift)
        {
            var value = dict[key];
            dict.Remove(key);
            dict[key + shift] = value;
        }
    }

    /// <summary>
    /// 将字典中 key > threshold 的所有键向前移动 shift 位（撤销移位）。
    /// </summary>
    private static void UnshiftDictionaryKeys<T>(Dictionary<int, T> dict, int threshold, int shift)
    {
        if (shift <= 0) return;
        var keysToShift = dict.Keys.Where(k => k > threshold).OrderBy(k => k).ToList();
        foreach (var key in keysToShift)
        {
            var value = dict[key];
            dict.Remove(key);
            dict[key - shift] = value;
        }
    }

    /// <summary>
    /// 将 HashSet 中 >= threshold 的所有值向后移动 shift 位。
    /// shift 为负数时向前移动。
    /// </summary>
    private static void ShiftHashSetValues(HashSet<int> set, int threshold, int shift)
    {
        if (shift == 0) return;
        var valuesToShift = set.Where(v => v >= threshold).ToList();
        foreach (var v in valuesToShift)
        {
            set.Remove(v);
            set.Add(v + shift);
        }
    }

    /// <summary>
    /// 在 FlowDocument 的指定索引处插入段落。
    /// </summary>
    private static void InsertParagraphAtIndex(FlowDocument doc, Paragraph newPara, int index)
    {
        if (index <= 0)
        {
            doc.Blocks.InsertBefore(doc.Blocks.FirstBlock, newPara);
            return;
        }

        int i = 0;
        Block? target = null;
        foreach (var block in doc.Blocks)
        {
            if (i == index) { target = block; break; }
            i++;
        }

        if (target != null)
            doc.Blocks.InsertBefore(target, newPara);
        else
            doc.Blocks.Add(newPara);
    }

    /// <summary>
    /// 获取行号宽度（左右面板统一）。
    /// 使用 _unifiedNumWidth 确保两侧行号列宽度一致。
    /// 同步操作后如果右侧行数增加，动态扩展宽度。
    /// </summary>
    private int GetMaxLineNumberWidth()
    {
        int rightCount = RightViewer.Document.Blocks.OfType<Paragraph>().Count();
        return Math.Max(_unifiedNumWidth, Math.Max(2, rightCount.ToString().Length));
    }

    /// <summary>
    /// 需求3：重新编号右侧面板所有段落的行号。
    /// 跳过占位段落（—— N 行未变更 ——），其余段落按顺序从 1 开始编号。
    /// </summary>
    private void RenumberRightParagraphs()
    {
        int lineNumber = 1;
        int numWidth = GetMaxLineNumberWidth();
        int blockIdx = 0;

        // 使用 ToList 快照遍历，防止修改 Run.Text 时 WPF 内部操作使枚举器失效
        foreach (var block in RightViewer.Document.Blocks.OfType<Paragraph>().ToList())
        {
            var para = block;

            // 跳过开头的 InlineUIContainer 按钮占位，找到首个 Run 元素
            Inline? targetInline = null;
            foreach (var inline in para.Inlines)
            {
                if (inline is not InlineUIContainer)
                {
                    targetInline = inline;
                    break;
                }
            }

            // 检测占位段落（横线分隔符 "——"）
            if (targetInline is Run r && r.Text.Contains("—"))
            {
                blockIdx++;
                continue;
            }

            var lineNumStr = lineNumber.ToString().PadLeft(numWidth) + " | ";
            var indicatorBg = _rightIndicatorBackgrounds.TryGetValue(blockIdx, out var bg)
                ? bg : LineNumberBgDefault;

            // 查找或创建行号 Run
            if (targetInline is Run firstRun && firstRun.Text.Contains(" | "))
            {
                firstRun.Text = lineNumStr;
                firstRun.Background = indicatorBg;
            }
            else if (targetInline != null)
            {
                // 段落没有行号前缀，在目标 inline 之前插入一个（按钮占位之后）
                var newRun = new Run(lineNumStr)
                {
                    Foreground = LineNumberBrush,
                    Background = indicatorBg
                };
                para.Inlines.InsertBefore(targetInline, newRun);
            }
            else
            {
                // 空段落，添加行号
                var newRun = new Run(lineNumStr)
                {
                    Foreground = LineNumberBrush,
                    Background = indicatorBg
                };
                para.Inlines.Add(newRun);
            }

            lineNumber++;
            blockIdx++;
        }
    }

    // ═══════════════════════════════════════════════════
    // 需求3：可编辑面板 — 行号保护和自动重编号
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 右侧面板文本变化时，防抖触发行号重编号。
    /// </summary>
    private void RightViewer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isProgrammaticChange || !_isLoaded) return;

        _renumberTimer?.Stop();
        _renumberTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _renumberTimer.Tick += (s, args) =>
        {
            _renumberTimer!.Stop();
            if (!_isLoaded) return; // 窗口已关闭，放弃执行
            _isProgrammaticChange = true;
            RenumberRightParagraphs();
            _isProgrammaticChange = false;
        };
        _renumberTimer.Start();
    }

    /// <summary>
    /// 保护行号前缀：防止光标进入行号区域（" | "之前）。
    /// Backspace 在行号边界时阻止删除，Home 键跳到行号之后。
    /// </summary>
    private void RightViewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isProgrammaticChange) return;

        var caret = RightViewer.CaretPosition;
        var currentRun = caret.Parent as Run;

        // 检查光标是否在行号区域（第一个 Run 且包含 " | "）
        if (currentRun != null && currentRun.Text.Contains(" | "))
        {
            // 判断光标在 " | " 之前还是之后
            var textBeforeCaret = currentRun.Text;
            var caretOffset = textBeforeCaret.Length;

            // 如果在行号区域内（" | " 之前），阻止 Backspace 和 Left
            var pipeIndex = currentRun.Text.IndexOf(" | ");
            if (pipeIndex >= 0)
            {
                // 简化处理：如果光标在段落的第一个 Run 中且在 " | " 之前
                var para = currentRun.Parent as Paragraph;
                if (para != null && para.Inlines.FirstInline == currentRun)
                {
                    if (e.Key == Key.Back || e.Key == Key.Left)
                    {
                        // 获取光标在 Run 中的位置
                        var runText = currentRun.Text;
                        var caretPos = caret.GetTextRunLength(LogicalDirection.Backward);
                        // 如果光标在 " | " 之前，阻止
                        if (caretPos <= pipeIndex + 3)
                        {
                            e.Handled = true;
                        }
                    }
                }
            }
        }

        // 阻止 Home 键进入行号区域
        if (e.Key == Key.Home)
        {
            // 让 Home 跳到行号之后（代码起始位置）
            var para = caret.Paragraph;
            if (para != null)
            {
                var firstRun = para.Inlines.FirstInline as Run;
                if (firstRun != null && firstRun.Text.Contains(" | "))
                {
                    // 移动光标到行号之后
                    var afterLineNumber = firstRun.ContentEnd;
                    RightViewer.CaretPosition = afterLineNumber;
                    e.Handled = true;
                }
            }
        }
    }

    /// <summary>
    /// 提取代码文本，跳过行号前缀和 InlineUIContainer（按钮）。
    /// </summary>
    private static string ExtractCodeFromParagraph(Paragraph? para)
    {
        if (para == null) return string.Empty;

        var parts = new List<string>();
        bool pastLineNumber = false;

        foreach (var inline in para.Inlines)
        {
            if (inline is Run run)
            {
                if (!pastLineNumber)
                {
                    // The first Run is always the line number (ends with " | ")
                    if (run.Text.Contains(" | "))
                    {
                        pastLineNumber = true;
                        continue;
                    }
                }
                parts.Add(run.Text);
            }
            // Skip InlineUIContainer (buttons)
        }

        return string.Join(string.Empty, parts);
    }

    /// <summary>
    /// 检查段落是否为"同步删除"行（含删除线装饰的 Run）。
    /// 用于 MergedScript 中作为 _syncedDeletedBlocks 的补充检查，
    /// 确保同步删除的行不会出现在合并脚本中。
    /// </summary>
    private static bool IsParagraphSyncedDelete(Paragraph? para)
    {
        if (para == null) return false;
        foreach (var inline in para.Inlines)
        {
            if (inline is Run run && run.TextDecorations != null)
            {
                foreach (var deco in run.TextDecorations)
                {
                    if (deco.Location == TextDecorationLocation.Strikethrough)
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Extract the line number prefix string from a paragraph (e.g., "  5 | " or "    | ").
    /// </summary>
    private static string ExtractLineNumberPrefix(Paragraph? para)
    {
        if (para == null) return "  | ";

        foreach (var inline in para.Inlines)
        {
            if (inline is Run run)
            {
                var pipeIndex = run.Text.IndexOf(" | ");
                if (pipeIndex >= 0)
                {
                    return run.Text.Substring(0, pipeIndex + 3);
                }
            }
        }

        return "  | ";
    }

    /// <summary>
    /// Rebuild diff hunks based on current indicator background state, splitting by ChangeType.
    /// Lines that have been synced (SyncedBackground) or are unchanged (LineNumberBgDefault) are not diffs.
    /// </summary>
    private void RebuildDiffHunksFromState()
    {
        _diffHunks.Clear();

        // Count total blocks in right document (使用快照防止枚举器失效)
        int blockCount = RightViewer.Document.Blocks.OfType<Paragraph>().Count();

        int subHunkStart = -1;
        int subHunkEnd = -1;
        ChangeType currentType = ChangeType.Unchanged;

        for (int i = 0; i < blockCount; i++)
        {
            bool isDiff = _rightIndicatorBackgrounds.TryGetValue(i, out var bg)
                          && !ReferenceEquals(bg, LineNumberBgDefault)
                          && !ReferenceEquals(bg, SyncedBackground)
                          && !ReferenceEquals(bg, ResolvedBackground);

            if (isDiff)
            {
                // Get the classified ChangeType for this line
                // Map raw DiffPlex ChangeType to our split classification:
                //   Inserted → 新增, Modified → 冲突, Imaginary → 删除
                ChangeType lineType = ChangeType.Modified;
                if (_lineChangeTypes.TryGetValue(i, out var storedType))
                {
                    lineType = storedType switch
                    {
                        ChangeType.Inserted => ChangeType.Inserted,
                        ChangeType.Modified => ChangeType.Modified,
                        ChangeType.Imaginary => ChangeType.Deleted,
                        ChangeType.Deleted => ChangeType.Deleted,
                        _ => ChangeType.Modified
                    };
                }

                if (subHunkStart < 0)
                {
                    subHunkStart = i;
                    subHunkEnd = i;
                    currentType = lineType;
                }
                else if (lineType != currentType)
                {
                    // Type changed → close current sub-hunk
                    _diffHunks.Add(new DiffHunkInfo
                    {
                        BlockStartIndex = subHunkStart,
                        BlockEndIndex = subHunkEnd,
                        HunkChangeType = currentType
                    });
                    subHunkStart = i;
                    subHunkEnd = i;
                    currentType = lineType;
                }
                else
                {
                    subHunkEnd = i;
                }
            }
            else
            {
                if (subHunkStart >= 0)
                {
                    _diffHunks.Add(new DiffHunkInfo
                    {
                        BlockStartIndex = subHunkStart,
                        BlockEndIndex = subHunkEnd,
                        HunkChangeType = currentType
                    });
                    subHunkStart = -1;
                }
            }
        }

        if (subHunkStart >= 0)
        {
            _diffHunks.Add(new DiffHunkInfo
            {
                BlockStartIndex = subHunkStart,
                BlockEndIndex = subHunkEnd,
                HunkChangeType = currentType
            });
        }

        // Reset current hunk index
        _currentHunkIndex = -1;

        // Update stats text
        var remainingDiffs = _rightIndicatorBackgrounds
            .Count(kvp => !ReferenceEquals(kvp.Value, LineNumberBgDefault)
                       && !ReferenceEquals(kvp.Value, SyncedBackground)
                       && !ReferenceEquals(kvp.Value, ResolvedBackground));
        var syncedCount = _hunkInsertedBlocks.Count;

        if (remainingDiffs == 0 && syncedCount == 0)
        {
            StatsText.Text = "✓ 两个版本完全一致，无差异";
        }
        else if (remainingDiffs == 0)
        {
            StatsText.Text = $"✓ 已全部同步（{syncedCount} 处差异已处理）";
            Result = DiffDialogResult.AcceptLeft;
        }
        else
        {
            StatsText.Text = $"📊 剩余 {remainingDiffs} 处差异" +
                             (syncedCount > 0 ? $"（已同步 {syncedCount} 处）" : "");
        }

        UpdateDiffNavigation();
        SyncAllButton.Visibility = remainingDiffs > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    // Diff Hunks — unified block indices
    // ============================================================

    /// <summary>
    /// Build diff hunks from the SideBySideModel directly, splitting by ChangeType.
    /// Within a contiguous diff range, each change type (Inserted/Deleted/Modified)
    /// gets its own sub-hunk with an independent sync button.
    /// </summary>
    private void BuildDiffHunks(SideBySideDiffModel sideBySide)
    {
        _diffHunks.Clear();

        var leftLines = sideBySide.OldText.Lines;
        var rightLines = sideBySide.NewText.Lines;
        if (leftLines == null || rightLines == null) return;

        var lineCount = Math.Max(leftLines.Count, rightLines.Count);
        int subHunkStart = -1;
        ChangeType currentType = ChangeType.Unchanged;

        for (int i = 0; i < lineCount; i++)
        {
            var leftType = i < leftLines.Count ? leftLines[i].Type : ChangeType.Unchanged;
            var rightType = i < rightLines.Count ? rightLines[i].Type : ChangeType.Unchanged;

            bool isDiff = leftType != ChangeType.Unchanged || rightType != ChangeType.Unchanged;

            if (isDiff)
            {
                var lineType = ClassifyDiffLine(leftType, rightType);

                if (subHunkStart < 0)
                {
                    // First diff line of this sub-hunk
                    subHunkStart = i;
                    currentType = lineType;
                }
                else if (lineType != currentType)
                {
                    // Type changed → close current sub-hunk, start new one
                    _diffHunks.Add(new DiffHunkInfo
                    {
                        BlockStartIndex = subHunkStart,
                        BlockEndIndex = i - 1,
                        HunkChangeType = currentType
                    });
                    subHunkStart = i;
                    currentType = lineType;
                }
            }
            else
            {
                if (subHunkStart >= 0)
                {
                    _diffHunks.Add(new DiffHunkInfo
                    {
                        BlockStartIndex = subHunkStart,
                        BlockEndIndex = i - 1,
                        HunkChangeType = currentType
                    });
                    subHunkStart = -1;
                }
            }
        }

        // Final sub-hunk
        if (subHunkStart >= 0)
        {
            _diffHunks.Add(new DiffHunkInfo
            {
                BlockStartIndex = subHunkStart,
                BlockEndIndex = lineCount - 1,
                HunkChangeType = currentType
            });
        }
    }

    // ============================================================
    // Minimap Click Navigation
    // ============================================================

    /// <summary>
    /// Click on minimap to jump to the nearest diff at that proportional position.
    /// </summary>
    private void Minimap_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_minimapRanges.Count == 0 || _diffHunks.Count == 0) return;

        var pos = e.GetPosition(MinimapCanvas);
        double ratio = pos.Y / Math.Max(1, MinimapCanvas.ActualHeight);
        int targetLine = (int)(ratio * _totalLineCount);

        // Find the nearest diff hunk
        int bestHunk = -1;
        int bestDist = int.MaxValue;
        for (int h = 0; h < _diffHunks.Count; h++)
        {
            var hunk = _diffHunks[h];
            int hunkMid = (hunk.BlockStartIndex + hunk.BlockEndIndex) / 2;
            int dist = Math.Abs(hunkMid - targetLine);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestHunk = h;
            }
        }

        if (bestHunk >= 0 && bestHunk != _currentHunkIndex)
        {
            _currentHunkIndex = bestHunk;
            NavigateToDiff(_currentHunkIndex);
        }
    }

    // ============================================================
    // Keyboard Shortcuts
    // ============================================================

    /// <summary>
    /// Handle keyboard shortcuts:
    /// Alt+↑ / Alt+↓ → Prev/Next diff
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            PrevDiff_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            NextDiff_Click(sender, e);
            e.Handled = true;
        }
    }

    // ============================================================
    // Diff Navigation
    // ============================================================

    private void PrevDiff_Click(object sender, RoutedEventArgs e)
    {
        if (_diffHunks.Count == 0) return;
        _currentHunkIndex = (_currentHunkIndex - 1 + _diffHunks.Count) % _diffHunks.Count;
        NavigateToDiff(_currentHunkIndex);
    }

    private void NextDiff_Click(object sender, RoutedEventArgs e)
    {
        if (_diffHunks.Count == 0) return;
        _currentHunkIndex = (_currentHunkIndex + 1) % _diffHunks.Count;
        NavigateToDiff(_currentHunkIndex);
    }

    /// <summary>
    /// Navigate to a diff hunk:
    /// 1. Reset ALL right-side indicator backgrounds to their original colors
    /// 2. Set current hunk's indicator backgrounds to highlight color on the RIGHT side only
    /// 3. Move caret in the right viewer to the current diff line
    /// 4. Scroll both viewers to show the diff
    /// </summary>
    private void NavigateToDiff(int hunkIndex)
    {
        if (hunkIndex < 0 || hunkIndex >= _diffHunks.Count) return;
        var hunk = _diffHunks[hunkIndex];

        // Step 1: Reset ALL right-side indicator backgrounds to stored values
        ResetIndicatorBackgrounds();

        // Step 2: Set current hunk indicator backgrounds to highlight color on the RIGHT side
        for (int bi = hunk.BlockStartIndex; bi <= hunk.BlockEndIndex; bi++)
        {
            SetLineNumberBackground(RightViewer.Document, bi, CurrentDiffBackground);
        }

        // Step 3: Move caret + scroll (delayed to ensure layout is complete)
        var rightPara = GetParagraphAtIndex(RightViewer.Document, hunk.BlockStartIndex);
        if (rightPara != null)
        {
            RightViewer.Focus();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                RightViewer.CaretPosition = rightPara.ContentStart;
                rightPara.BringIntoView();
                ScrollToParagraph(RightViewer, rightPara);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Scroll left viewer to the corresponding diff
        var leftPara = GetParagraphAtIndex(LeftViewer.Document, hunk.BlockStartIndex);
        if (leftPara != null)
        {
            leftPara.BringIntoView();
            ScrollToParagraph(LeftViewer, leftPara);
        }

        UpdateDiffNavigation();
    }

    /// <summary>
    /// Reset all right-side indicator backgrounds to their stored values.
    /// </summary>
    private void ResetIndicatorBackgrounds()
    {
        // 使用 ToList 快照防止 SetLineNumberBackground 触发布局使字典迭代失效
        foreach (var kvp in _rightIndicatorBackgrounds.ToList())
        {
            SetLineNumberBackground(RightViewer.Document, kvp.Key, kvp.Value);
        }
    }

    /// <summary>
    /// Set the line number area background for a specific block index in a FlowDocument.
    /// Finds the first Run (which is the line number prefix) and sets its Background.
    /// </summary>
    private static void SetLineNumberBackground(FlowDocument doc, int blockIndex, Brush background)
    {
        var para = GetParagraphAtIndex(doc, blockIndex);
        if (para == null) return;

        foreach (var inline in para.Inlines)
        {
            if (inline is Run run)
            {
                // First Run is always the line number (contains " | ")
                if (run.Text.Contains(" | ") || run.Text.Contains("—"))
                {
                    run.Background = background;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Reset ALL tracked paragraphs to their original background colors.
    /// </summary>
    private static void ResetParagraphBackgrounds(FlowDocument doc, Dictionary<int, Brush> originalBackgrounds)
    {
        foreach (var kvp in originalBackgrounds)
        {
            var para = GetParagraphAtIndex(doc, kvp.Key);
            if (para != null)
            {
                para.Background = kvp.Value;
            }
        }
    }

    private static void ScrollToParagraph(RichTextBox viewer, Paragraph targetPara)
    {
        if (viewer == null || targetPara == null) return;

        var scrollViewer = FindScrollViewer(viewer);
        if (scrollViewer != null)
        {
            viewer.Dispatcher.BeginInvoke(new Action(() =>
            {
                var contextOffset = 18 * 3;
                var newOffset = Math.Max(0, scrollViewer.VerticalOffset - contextOffset);
                scrollViewer.ScrollToVerticalOffset(newOffset);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private static Paragraph? GetParagraphAtIndex(FlowDocument doc, int blockIndex)
    {
        if (blockIndex < 0) return null;
        int i = 0;
        foreach (var block in doc.Blocks)
        {
            if (i == blockIndex && block is Paragraph para) return para;
            i++;
        }
        return null;
    }

    private void UpdateDiffNavigation()
    {
        var totalHunks = _diffHunks.Count;
        if (totalHunks == 0)
        {
            DiffPositionText.Text = "无差异";
            PrevDiffButton.IsEnabled = false;
            NextDiffButton.IsEnabled = false;
        }
        else
        {
            var displayIndex = _currentHunkIndex >= 0 ? _currentHunkIndex + 1 : 0;
            DiffPositionText.Text = $"差异 {displayIndex} / {totalHunks}";
            PrevDiffButton.IsEnabled = true;
            NextDiffButton.IsEnabled = true;
        }
    }

    // ============================================================
    // Synchronized Scrolling — 比例同步算法
    // ============================================================

    /// <summary>
    /// Proportionally synchronized scrolling between left and right panels.
    /// Instead of copying absolute offset (which fails when panels have different line counts),
    /// calculates the scroll ratio and applies it to the target panel.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrollSyncing || !_isLoaded) return;
        _scrollSyncing = true;

        try
        {
            var sourceSv = FindScrollViewer(sender as RichTextBox);
            var targetSv = FindScrollViewer(sender == LeftViewer ? RightViewer : LeftViewer);
            if (sourceSv == null || targetSv == null) return;

            // Only do proportional sync for user scroll (not content changes)
            if (e.ExtentHeightChange == 0 && e.ExtentWidthChange == 0)
            {
                // Proportional vertical sync
                double sourceVMax = Math.Max(1, sourceSv.ScrollableHeight);
                double vRatio = sourceSv.VerticalOffset / sourceVMax;
                double targetVOffset = vRatio * Math.Max(1, targetSv.ScrollableHeight);
                targetSv.ScrollToVerticalOffset(targetVOffset);

                // Proportional horizontal sync
                double sourceHMax = Math.Max(1, sourceSv.ScrollableWidth);
                double hRatio = sourceSv.HorizontalOffset / sourceHMax;
                double targetHOffset = hRatio * Math.Max(1, targetSv.ScrollableWidth);
                targetSv.ScrollToHorizontalOffset(targetHOffset);
            }

            // Update minimap viewport
            UpdateMinimapViewport(
                sourceSv.VerticalOffset,
                sourceSv.VerticalOffset + sourceSv.ViewportHeight);
        }
        finally
        {
            _scrollSyncing = false;
        }
    }

    // ============================================================
    // Button Handlers
    // ============================================================

    /// <summary>
    /// 需求1：全部同步 — 将所有未同步的代码块从 DB 版本同步到本地脚本。
    /// 从后往前同步，避免索引移位影响前面的 hunk。
    /// </summary>
    private void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 收集所有未同步的 hunk 索引
            var unsyncedHunks = new List<int>();
            for (int h = 0; h < _diffHunks.Count; h++)
            {
                var hunk = _diffHunks[h];

                // block 级别检查：hunk 首行已被同步/解决则跳过
                if (_rightIndicatorBackgrounds.TryGetValue(hunk.BlockStartIndex, out var startBg)
                    && (ReferenceEquals(startBg, SyncedBackground) || ReferenceEquals(startBg, ResolvedBackground)))
                    continue;

                bool hasConflict = false;
                for (int bi = hunk.BlockStartIndex; bi <= hunk.BlockEndIndex; bi++)
                {
                    if (_rightIndicatorBackgrounds.TryGetValue(bi, out var bg)
                        && !ReferenceEquals(bg, LineNumberBgDefault)
                        && !ReferenceEquals(bg, SyncedBackground)
                        && !ReferenceEquals(bg, ResolvedBackground))
                    {
                        hasConflict = true;
                        break;
                    }
                }
                if (hasConflict)
                    unsyncedHunks.Add(h);
            }

            if (unsyncedHunks.Count == 0) return;

            // 从后往前同步，避免索引移位影响前面的 hunk
            foreach (var hunkIndex in unsyncedHunks.OrderByDescending(x => x))
            {
                SyncHunk(hunkIndex);
            }


            _isProgrammaticChange = true;
            try
            {
                RenumberRightParagraphs();
                RebuildDiffHunksFromState();
                PopulateDiffButtons();
            }
            finally
            {
                _isProgrammaticChange = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncAll_Click failed: {ex}");
            MessageBox.Show($"全部同步失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AcceptLeft_Click(object sender, RoutedEventArgs e)
    {
        _hasAcceptedLeft = true;
        Result = DiffDialogResult.AcceptLeft;
        Close();
    }

    private void AcceptRight_Click(object sender, RoutedEventArgs e)
    {
        _hasAcceptedRight = true;
        Result = DiffDialogResult.AcceptRight;
        // 不清空任何状态 — OnClosed 也不清空。
        // VersionWarningDialog 的 Closed handler 在 OnClosed 之后执行，
        // 需要读取 MergedScript（依赖 _hunkInsertedBlocks 等状态）。
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = DiffDialogResult.Cancel;
        Close();
    }
}
