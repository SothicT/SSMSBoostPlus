using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.SQLParser;
using SSMSBoostPlus.SSMS.Services;
using SSMSBoostPlus.VersionGuard.Models;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// Dialog shown when version mismatch is detected.
/// Displays outdated objects and allows user to choose action.
/// Supports viewing side-by-side diff of current script vs DB definition.
///
/// CRITICAL: Non-modal dialog (Show()). All buttons must use Close() not DialogResult.
/// Diff sub-dialog (AIDiffDialog) tracking: when diff is open, the X button is blocked
/// to prevent premature closing before diff review is complete.
/// When diff dialog returns with AcceptLeft or AcceptRight, the editor content is
/// updated and the comparison is refreshed before closing.
/// </summary>
public partial class VersionWarningDialog : Window
{
    /// <summary>
    /// The user's choice from the dialog.
    /// </summary>
    public VersionWarningResult Result { get; private set; } = VersionWarningResult.Cancel;

    /// <summary>
    /// 触发版本守卫的源文档路径（公开，供外部按文档匹配弹窗）（F1）。
    /// </summary>
    public string? SourceDocumentPath => _sourceDocumentPath;

    private readonly VersionCheckResult _versionInfo;
    private string _currentScriptCode;
    private readonly Func<VersionInfo, Task<string?>> _getDbDefinition;
    private readonly Func<VersionInfo, Task<DateTime?>> _getLatestModifyDate;
    private readonly Func<VersionInfo, Task> _refreshComparison;
    private readonly SsmsEditorService _editorService;
    private readonly IConfigurationService _configService;
    private readonly SSMS.Services.Connection.ConnectionServiceFacade _connectionFacade;

    /// <summary>
    /// 本次审核开始时间（点"查看"时设置）。
    /// 同步后用 DB 最新 modify_date 与此时间对比：
    ///   - modify_date ≥ 此时间 → 审核期间 DB 被修改 → "NEW 新版本" + 待审查
    ///   - modify_date < 此时间 → 审核期间无修改 → 已审查
    /// </summary>
    private DateTime _reviewStartTime = DateTime.Now;

    /// <summary>
    /// 触发版本守卫的源文档路径。
    /// 替换编辑器内容前先激活此文档，避免覆盖用户切换到的其他编辑器。
    /// </summary>
    private readonly string? _sourceDocumentPath;

    // 非模态弹窗的强引用（防止 GC）
    private AIDiffDialog? _activeDiffDialog;

    // ═══════════════════════════════════════════════════
    // 文档焦点跟随（F1）
    // 弹出过时弹窗后，编辑器变只读
    // 切到其他编辑器时弹窗隐藏，切回时自动显示
    // ═══════════════════════════════════════════════════
    private bool _isActivationHooked;
    private bool _isHiddenOnDeactivate;  // 标记是否因切换文档而隐藏
    private bool _diffWasOpenOnHide;     // 隐藏时差异比对弹窗是否打开
    private bool _isReadOnlySet;         // 编辑器是否已被设为只读

    public VersionWarningDialog(
        VersionCheckResult versionInfo,
        string currentScriptCode,
        Func<VersionInfo, Task<string?>> getDbDefinition,
        Func<VersionInfo, Task<DateTime?>> getLatestModifyDate,
        Func<VersionInfo, Task> refreshComparison,
        SsmsEditorService editorService,
        IConfigurationService configService,
        SSMS.Services.Connection.ConnectionServiceFacade connectionFacade)
    {
        InitializeComponent();
        _versionInfo = versionInfo;
        _currentScriptCode = currentScriptCode;
        _getDbDefinition = getDbDefinition;
        _getLatestModifyDate = getLatestModifyDate;
        _refreshComparison = refreshComparison;
        _editorService = editorService;
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _connectionFacade = connectionFacade ?? throw new ArgumentNullException(nameof(connectionFacade));

        // 捕获触发版本守卫的源文档路径，用于替换内容前激活正确的编辑器
        _sourceDocumentPath = _editorService.GetActiveDocumentPath();

        ObjectsGrid.ItemsSource = versionInfo.OutdatedObjects;

        ShowDiffButton.IsEnabled = false;
        ObjectsGrid.SelectionChanged += (s, e) =>
        {
            ShowDiffButton.IsEnabled = ObjectsGrid.SelectedItem is VersionInfo
                && _activeDiffDialog == null;
        };

        // ⚠️ 比对弹窗打开时阻止 X 按钮关闭
        Closing += (s, e) =>
        {
            if (_activeDiffDialog != null)
            {
                e.Cancel = true;
                return;
            }
            // 关闭弹窗前恢复编辑器可编辑并取消激活注册（F1）
            RestoreEditingAndUnhook();
        };

        // 点击版本守卫窗口任意位置时，将差异比对弹窗抖动并突出到上层
        // 使用 AddHandler + handledEventsToo:true 确保即使 DataGrid 内部处理了事件也能捕获
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((s, e) =>
        {
            if (_activeDiffDialog != null && _activeDiffDialog.IsVisible)
            {
                _activeDiffDialog.Activate();
                ShakeWindow(_activeDiffDialog);
            }
        }), handledEventsToo: true);

        // 专门捕获 DataGrid 内部所有单元格点击（列头、模板列等深层控件）
        ObjectsGrid.AddHandler(UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((s, e) =>
            {
                if (_activeDiffDialog != null && _activeDiffDialog.IsVisible)
                {
                    _activeDiffDialog.Activate();
                    ShakeWindow(_activeDiffDialog);
                }
            }), handledEventsToo: true);

        // 初始化"继续执行"按钮状态
        UpdateContinueButtonState();

        // ═══════════════════════════════════════════════════
        // 文档焦点跟随（F1）：注册文档激活事件
        // 弹出过时弹窗 → 编辑器只读 → 切走隐藏 → 切回显示
        // ═══════════════════════════════════════════════════
        HookDocumentActivation();

        // 初始显示时将源文档设为只读
        SetReadOnly(true);

        // 永置顶：确保弹窗始终在其编辑器上方
        Topmost = true;
    }

    /// <summary>
    /// 注册文档激活事件，实现弹窗跟随文档焦点（F1）。
    /// </summary>
    private void HookDocumentActivation()
    {
        if (_isActivationHooked) return;
        _isActivationHooked = true;

        SsmsEditorService.DocumentActivated += OnDocumentActivated;
    }

    /// <summary>
    /// 文档激活事件处理：根据激活文档切换弹窗显示/隐藏（F1）。
    /// </summary>
    private void OnDocumentActivated(string? activeDocPath)
    {
        var isSourceDocActive = !string.IsNullOrEmpty(activeDocPath)
            && string.Equals(activeDocPath, _sourceDocumentPath, StringComparison.OrdinalIgnoreCase);

        if (isSourceDocActive)
        {
            // 切回源文档 → 显示弹窗
            if (_isHiddenOnDeactivate)
            {
                _isHiddenOnDeactivate = false;

                // 激活并显示版本守卫弹窗
                Show();
                Activate();
                // 永置顶：对话框会随文档切换隐藏/显示，不影响其他窗口
                Topmost = true;

                // 设置只读
                SetReadOnly(true);

                // 如果差异比对弹窗之前是打开的，恢复显示
                if (_diffWasOpenOnHide && _activeDiffDialog != null && !_activeDiffDialog.IsVisible)
                {
                    _diffWasOpenOnHide = false;
                    _activeDiffDialog.Show();
                    _activeDiffDialog.Activate();
                }
            }
        }
        else
        {
            // 切换到其他文档 → 隐藏弹窗
            if (IsVisible && !_isHiddenOnDeactivate)
            {
                _isHiddenOnDeactivate = true;

                // 记录差异比对弹窗状态
                _diffWasOpenOnHide = _activeDiffDialog != null && _activeDiffDialog.IsVisible;

                // 隐藏差异比对弹窗（不关闭，不触发 Closed 事件）
                if (_diffWasOpenOnHide)
                {
                    _activeDiffDialog!.Hide();
                }

                // 隐藏版本守卫弹窗
                Hide();
            }
        }
    }

    /// <summary>
    /// 关闭弹窗时恢复编辑器可编辑状态并取消激活注册（F1）。
    /// </summary>
    private void RestoreEditingAndUnhook()
    {
        // 恢复编辑器可编辑
        if (_isReadOnlySet)
        {
            SetReadOnly(false);
        }

        // 取消激活注册
        if (_isActivationHooked)
        {
            _isActivationHooked = false;
            SsmsEditorService.DocumentActivated -= OnDocumentActivated;
        }
    }

    /// <summary>
    /// 设置源文档只读/可编辑（F1）。
    /// </summary>
    private void SetReadOnly(bool readOnly)
    {
        _editorService.SetReadOnly(_sourceDocumentPath, readOnly);
        _isReadOnlySet = readOnly;
    }

    /// <summary>
    /// 抖动窗口两次（左右快速移动），提醒用户关注此窗口
    /// </summary>
    private static void ShakeWindow(Window window)
    {
        var originalLeft = window.Left;
        var shakeAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(300),
            RepeatBehavior = new RepeatBehavior(2) // 抖动2次
        };
        shakeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(originalLeft - 2, KeyTime.FromPercent(0.1)));
        shakeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(originalLeft + 2, KeyTime.FromPercent(0.3)));
        shakeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(originalLeft - 2, KeyTime.FromPercent(0.5)));
        shakeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(originalLeft + 2, KeyTime.FromPercent(0.7)));
        shakeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(originalLeft, KeyTime.FromPercent(1.0)));

        window.BeginAnimation(Window.LeftProperty, shakeAnim);
    }

    /// <summary>
    /// 差异比对关闭后，将检测过时对象页面静默置顶到所有窗口最前（含 SSMS 主窗口）并获取键盘焦点。
    /// 不抖动/闪烁。采用 Topmost 临时置顶后复位的方式，确保即便被 SSMS 主窗口遮挡也能抬到最前。
    /// </summary>
    private void BringSelfToFrontSilently()
    {
        this.Activate();
        this.Topmost = true;
        this.Topmost = false;
    }

    private async void ViewDiff_Click(object sender, RoutedEventArgs e)
    {
        // 差异比对弹窗已打开时阻止再开新的
        if (_activeDiffDialog != null) return;
        if (sender is Button btn && btn.Tag is VersionInfo vi)
        {
            await ShowDiffForAsync(vi);
        }
    }

    private async void ShowDiff_Click(object sender, RoutedEventArgs e)
    {
        // 差异比对弹窗已打开时阻止再开新的
        if (_activeDiffDialog != null) return;
        if (ObjectsGrid.SelectedItem is VersionInfo vi)
        {
            await ShowDiffForAsync(vi);
        }
    }

    private async void LocalHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectsGrid.SelectedItem is VersionInfo vi)
        {
            try
            {
                // 通过 ConnectionServiceFacade 统一获取连接
                var connInfo = await _connectionFacade.GetConnectionInfoAsync();
                if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
                {
                    System.Diagnostics.Debug.WriteLine("[VersionWarning] 无法获取当前数据库连接");
                    return;
                }

                // 捕获原始数据库名，用于按库过滤版本记录
                var originalDbName = connInfo.DatabaseName;

                // 读取配置中的版本历史数据库名（替代硬编码 MESBASE）
                var config = await _configService.GetLocalHistoryConfigAsync();
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connInfo.ConnectionString)
                {
                    InitialCatalog = config.DatabaseName
                };
                var mesbaseConnStr = builder.ConnectionString;

                // 通过 Package 查找或创建 LocalHistoryToolWindow
                var package = Microsoft.VisualStudio.Shell.Package.GetGlobalService(
                    typeof(Microsoft.VisualStudio.Shell.Interop.IVsShell)) as Microsoft.VisualStudio.Shell.AsyncPackage;
                if (package == null) return;

                var window = package.FindToolWindow(
                    typeof(SSMS.UI.VersionHistory.LocalHistoryToolWindow), 0, true);
                if (window?.Frame == null) return;

                if (window is SSMS.UI.VersionHistory.LocalHistoryToolWindow historyWindow)
                {
                    var objectName = $"{vi.ObjectRef.Schema}.{vi.ObjectRef.Name}";
                    historyWindow.InitializeContent(mesbaseConnStr, objectName, originalDbName);
                }

                var frame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)window.Frame;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VersionWarning] 打开 Local History 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 打开 AIDiffDialog 比对弹窗（非模态）。
    /// 每次打开时从编辑器获取最新代码，从数据库获取最新定义。
    /// 比对弹窗打开期间，VersionWarningDialog 的 X 按钮被阻止，
    /// 防止用户在未完成比对时误关闭。
    /// 比对弹窗返回后：
    ///   - AcceptLeft（采用数据库版本）：将编辑器内容替换为 DB 版本，重新比对，如有差异不标记已审查
    ///   - AcceptRight（保留我的脚本）：将编辑器内容替换为合并后的脚本，重新比对，如有差异不标记已审查
    ///   - Cancel：恢复按钮，弹窗保持打开
    /// </summary>
    private async Task ShowDiffForAsync(VersionInfo versionInfo)
    {
        var objName = versionInfo.ObjectRef.FullName;

        // 记录本次审核开始时间，用于同步后与 DB modify_date 对比
        _reviewStartTime = DateTime.Now;

        // F2：激活源文档编辑器（用户可能已切换到其他编辑器），
        // 并刷新 _currentScriptCode 为源编辑器最新内容，
        // 避免用户上次比对后修改的代码被误判为 DB 端差异行
        _editorService.ActivateDocument(_sourceDocumentPath);
        _currentScriptCode = await _editorService.GetFullTextAsync();

        var dbDefinition = await _getDbDefinition(versionInfo);

        if (dbDefinition == null)
        {
            MessageBox.Show(
                $"无法获取 {objName} 的数据库定义。\n" +
                "该对象可能没有 SQL 模块定义（如表、无模块的视图）。",
                "无法获取定义",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var extractor = new ObjectExtractor();
        var localScript = extractor.ExtractObjectScript(_currentScriptCode, versionInfo.ObjectRef) ?? _currentScriptCode;

        _activeDiffDialog = new AIDiffDialog(
            dbDefinition,
            localScript,
            $"Version Diff: {objName} (DB vs Your Script)");

        // 比对弹窗打开 → 禁用按钮（X 按钮已被 Closing 阻止）
        SetButtonsEnabled(false);

        _activeDiffDialog.Closed += async (_, _) =>
        {
            var activeDiff = _activeDiffDialog;
            _activeDiffDialog = null;

            if (activeDiff != null)
            {
                // ⚠️ F1：替换编辑器内容前必须暂时恢复可编辑（SetReadOnly(true) 设置后编辑器不可写）
                _editorService.SetReadOnly(_sourceDocumentPath, false);

                if (activeDiff.Result == DiffDialogResult.AcceptLeft)
                {
                    // 需求2：采用数据库版本 → 仅替换编辑器中该对象的脚本部分
                    var dbCode = activeDiff.LeftCode;
                    if (!string.IsNullOrEmpty(dbCode))
                    {
                        // F2：刷新 _currentScriptCode（用户可能在比对弹窗打开期间修改了编辑器）
                        _editorService.ActivateDocument(_sourceDocumentPath);
                        _currentScriptCode = await _editorService.GetFullTextAsync();

                        var extractor = new ObjectExtractor();
                        var newFullScript = extractor.ReplaceObjectScript(_currentScriptCode, versionInfo.ObjectRef, dbCode);
                        // 先激活源文档，确保替换到正确的编辑器
                        _editorService.ActivateDocument(_sourceDocumentPath);
                        await _editorService.ReplaceAllAsync(newFullScript);
                        _currentScriptCode = newFullScript;
                    }

                    // F1：重新设回只读（版本守卫弹窗仍打开）
                    _editorService.SetReadOnly(_sourceDocumentPath, true);

                    // 需求2+3：重新拉取 DB 代码比对，静默更新状态（不弹窗）
                    await ReCompareAndMarkAsync(versionInfo);
                    SetButtonsEnabled(true);
                    // 差异比对已关闭 → 将检测过时对象页面静默置顶到最前
                    BringSelfToFrontSilently();
                    return;
                }

                if (activeDiff.Result == DiffDialogResult.AcceptRight)
                {
                    // 需求2：保留我的脚本 → 仅替换编辑器中该对象的脚本部分（含同步修改）
                    var mergedScript = activeDiff.MergedScript;
                    if (!string.IsNullOrEmpty(mergedScript))
                    {
                        // F2：刷新 _currentScriptCode（用户可能在比对弹窗打开期间修改了编辑器）
                        _editorService.ActivateDocument(_sourceDocumentPath);
                        _currentScriptCode = await _editorService.GetFullTextAsync();

                        var extractor = new ObjectExtractor();
                        var newFullScript = extractor.ReplaceObjectScript(_currentScriptCode, versionInfo.ObjectRef, mergedScript);
                        // 先激活源文档，确保替换到正确的编辑器
                        _editorService.ActivateDocument(_sourceDocumentPath);
                        await _editorService.ReplaceAllAsync(newFullScript);
                        _currentScriptCode = newFullScript;
                    }

                    // F1：重新设回只读（版本守卫弹窗仍打开）
                    _editorService.SetReadOnly(_sourceDocumentPath, true);

                    // 需求2+3：重新拉取 DB 代码比对，静默更新状态（不弹窗）
                    await ReCompareAndMarkAsync(versionInfo);
                    SetButtonsEnabled(true);
                    // 差异比对已关闭 → 将检测过时对象页面静默置顶到最前
                    BringSelfToFrontSilently();
                    return;
                }

                // Cancel → F1：重新设回只读
                _editorService.SetReadOnly(_sourceDocumentPath, true);
            }

            // Cancel（含点击 X 关闭）→ 恢复按钮并将检测过时对象页面静默置顶到最前
            SetButtonsEnabled(true);
            BringSelfToFrontSilently();
        };

        _activeDiffDialog.Show();
    }

    /// <summary>
    /// 启用/禁用 VersionWarningDialog 的所有操作按钮
    /// </summary>
    private void SetButtonsEnabled(bool enabled)
    {
        ShowDiffButton.IsEnabled = enabled && ObjectsGrid.SelectedItem is VersionInfo;
        // 通过视觉反馈告知用户按钮状态
        var opacity = enabled ? 1.0 : 0.5;
        foreach (var child in ((StackPanel)ShowDiffButton.Parent).Children)
        {
            if (child is Button btn && btn != ShowDiffButton && btn != ContinueButton)
            {
                btn.IsEnabled = enabled;
                btn.Opacity = opacity;
            }
        }
        // "继续执行"按钮的状态由审查状态决定
        if (enabled)
        {
            UpdateContinueButtonState();
        }
        else
        {
            ContinueButton.IsEnabled = false;
            ContinueButton.Opacity = 0.5;
        }
    }

    private void ReloadFromDb_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "从数据库重新加载将丢失当前编辑器中的所有本地修改。\n\n确定要继续吗？",
            "确认重新加载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        Result = VersionWarningResult.ReloadFromDb;
        RestoreEditingAndUnhook();
        Close();
    }

    private async void ContinueExecution_Click(object sender, RoutedEventArgs e)
    {
        // 保存按钮原始文字和状态
        var originalContent = ContinueButton.Content;
        try
        {
            // UI 反馈：按钮文字改为"校验中..."，禁用按钮
            ContinueButton.Content = "校验中...";
            ContinueButton.IsEnabled = false;

            // 二次校验：逐个查询最新 modify_date，与弹窗打开时记录的 DbModifyDate 对比
            var changedObjects = new List<VersionInfo>();
            var latestDates = new Dictionary<VersionInfo, DateTime?>();

            foreach (var vi in _versionInfo.OutdatedObjects)
            {
                DateTime? latestModifyDate;
                try
                {
                    latestModifyDate = await _getLatestModifyDate(vi);
                }
                catch (Exception)
                {
                    // 查询失败：安全优先，弹窗报错，不允许继续执行
                    MessageBox.Show(
                        $"校验过程中查询 {vi.ObjectRef.FullName} 的修改时间失败。\n" +
                        "请检查数据库连接后重试。",
                        "校验失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                latestDates[vi] = latestModifyDate;

                // 对比 modify_date（null 也视为变化）
                if (latestModifyDate != vi.DbModifyDate)
                {
                    changedObjects.Add(vi);
                }
            }

            if (changedObjects.Count > 0)
            {
                // 检测到变化：弹窗警告，列出变化的对象名
                var objectList = string.Join("\n", changedObjects.Select(v => v.ObjectRef.FullName));
                MessageBox.Show(
                    $"以下对象在审查期间被修改：\n\n{objectList}\n\n" +
                    "请重新审查这些对象的差异。",
                    "检测到对象变更",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                // 显示"NEW 新版本"指示器
                NewVersionIndicator.Visibility = Visibility.Visible;

                // 重新计算发生变化对象的 ComparisonType，确保"比对结果"badge 准确
                foreach (var vi in changedObjects)
                {
                    await _refreshComparison(vi);
                }

                // 重置所有对象的 IsReviewed = false，更新 DbModifyDate 为最新值
                foreach (var vi in _versionInfo.OutdatedObjects)
                {
                    vi.IsReviewed = false;
                    if (latestDates.TryGetValue(vi, out var latestDate))
                    {
                        vi.DbModifyDate = latestDate;
                    }
                }

                // 刷新 DataGrid，更新按钮状态
                ObjectsGrid.Items.Refresh();
                UpdateContinueButtonState();
                return;
            }

            // 无变化：校验通过，设置结果并关闭
            Result = VersionWarningResult.ContinueExecution;
            RestoreEditingAndUnhook();
            Close();
        }
        finally
        {
            // 恢复按钮文字（如果弹窗未关闭）
            ContinueButton.Content = originalContent;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = VersionWarningResult.Cancel;
        RestoreEditingAndUnhook();
        Close();
    }

    /// <summary>
    /// 更新"继续执行"按钮的启用状态。
    /// 当所有需要审查的对象都已审查完毕时启用，否则禁用。
    /// </summary>
    private void UpdateContinueButtonState()
    {
        var unreviewed = GetUnreviewedObjects();
        var canContinue = !unreviewed.Any();
        ContinueButton.IsEnabled = canContinue;
        ContinueButton.Opacity = canContinue ? 1.0 : 0.5;
    }

    /// <summary>
    /// 同步后重新检测 DB 对象状态。
    /// 新逻辑：不再通过代码比对决定审查状态，而是查询 DB 最新 modify_date 与弹窗打开时间对比：
    ///   - modify_date ≥ 弹窗时间 → DB 有新版本，显示"NEW 新版本"，不标记已审查
    ///   - modify_date < 弹窗时间 → DB 无新版本，更新比对结果，标记已审查
    /// </summary>
    private async Task ReCompareAndMarkAsync(VersionInfo versionInfo)
    {
        // 使用 _currentScriptCode（触发版本守卫的编辑器代码），不从活动编辑器获取
        // 查询 DB 最新 modify_date
        DateTime? latestModifyDate = null;
        try
        {
            latestModifyDate = await _getLatestModifyDate(versionInfo);
        }
        catch
        {
            // 查询失败：静默忽略，不影响主流程
        }

        // 更新比对结果 badge
        await _refreshComparison(versionInfo);

        if (latestModifyDate.HasValue && latestModifyDate.Value >= _reviewStartTime)
        {
            // DB 有新版本（modify_date ≥ 本次审核开始时间）→ 显示"NEW 新版本"，不标记已审查
            NewVersionIndicator.Visibility = Visibility.Visible;
            versionInfo.DbModifyDate = latestModifyDate;
            versionInfo.IsReviewed = false;
        }
        else
        {
            // DB 无新版本（modify_date < 弹窗时间）→ 隐藏"NEW 新版本"，标记已审查
            NewVersionIndicator.Visibility = Visibility.Collapsed;
            versionInfo.IsReviewed = true;
        }

        ObjectsGrid.Items.Refresh();
        UpdateContinueButtonState();

        // 只有在已审查时才选中下一个未审查对象
        if (versionInfo.IsReviewed)
        {
            SelectNextUnreviewedObject();
        }
    }

    /// <summary>
    /// 在 DataGrid 中选中下一个未审查的对象。
    /// 如果没有未审查的对象，不做任何操作。
    /// </summary>
    private void SelectNextUnreviewedObject()
    {
        var next = GetUnreviewedObjects().FirstOrDefault();
        if (next != null)
        {
            ObjectsGrid.SelectedItem = next;
            ObjectsGrid.ScrollIntoView(next);
        }
    }

    /// <summary>
    /// 获取所有需要审查但尚未审查的对象列表。
    /// </summary>
    private IEnumerable<VersionInfo> GetUnreviewedObjects()
    {
        return _versionInfo.OutdatedObjects
            .Where(vi => vi.RequiresReview && !vi.IsReviewed);
    }
}