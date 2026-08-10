using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services.Connection;
using SSMSBoostPlus.VersionHistory;
using SSMSBoostPlus.VersionHistory.Models;

namespace SSMSBoostPlus.SSMS.UI.VersionHistory;

/// <summary>
/// Local History ToolWindow 主界面控件。
/// 搜索下拉框 + DataGrid 版本列表 + 状态栏 + DTE 窗口切换监听。
/// </summary>
public partial class LocalHistoryToolWindowControl : UserControl
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationService _configService;
    private readonly ILoggerService _logger;
    private VersionHistoryService? _historyService;
    private string? _connectionString;
    private string? _databaseFilter;
    private List<ObjectSummary> _allObjects = new();
    private bool _isLoading;
    private bool _isUpdatingText;
    private DTE? _dte;
    private string? _targetConnectionString;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _activationCts;
    private CancellationTokenSource? _versionLoadCts;
    private List<string> _availableDatabases = new();
    private bool _isDatabaseResolved;
    private string? _currentServerName;
    private bool _isDatabaseComboBoxUpdating;
    private bool _isPinned;
    private int _totalObjectCount;

    public LocalHistoryToolWindowControl(
        IServiceProvider serviceProvider,
        IConfigurationService configService,
        ILoggerService logger)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;
        _configService = configService;
        _logger = logger;

        Loaded += OnLoaded;

        // 订阅编辑框文本变化事件，实现实时模糊过滤
        ObjectSearchBox.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(OnSearchBoxTextChanged));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 获取 DTE 实例用于窗口切换监听
        try
        {
            _dte = _serviceProvider.GetService(typeof(DTE)) as DTE
                ?? Package.GetGlobalService(typeof(DTE)) as DTE;

            if (_dte != null)
            {
                _dte.Events.WindowEvents.WindowActivated += OnWindowActivated;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[LocalHistory] 无法获取 DTE 实例: {ex.Message}");
        }
    }

    /// <summary>
    /// 为指定对象加载版本历史。
    /// </summary>
    /// <param name="connectionString">MESBASE 连接串</param>
    /// <param name="initialObjectName">初始对象名（可为 null）</param>
    /// <param name="databaseFilter">原始数据库名，用于按库过滤版本记录（可为 null）</param>
    public async void LoadForObject(string connectionString, string? initialObjectName, string? databaseFilter = null)
    {
        _connectionString = connectionString;
        // 空字符串等同于 null：表示数据库名未解析，不按数据库过滤
        _databaseFilter = string.IsNullOrEmpty(databaseFilter) ? null : databaseFilter;

        // 权限隔离：检查白名单
        var config = await _configService.GetLocalHistoryConfigAsync().ConfigureAwait(true);
        if (config.AllowedDatabases.Count > 0 && _databaseFilter != null)
        {
            if (!config.AllowedDatabases.Contains(_databaseFilter, StringComparer.OrdinalIgnoreCase))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                UpdateStatus($"当前数据库 \"{_databaseFilter}\" 不在允许列表中，请联系管理员");
                ObjectCountLabel.Text = "";
                ConnectionStatusLabel.Text = "受限";
                ConnectionStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE7, 0x4C, 0x3C));
                return;
            }
        }

        // 推导目标库连接串（用于比对服务器当前定义）
        if (!string.IsNullOrEmpty(databaseFilter))
        {
            try
            {
                var targetBuilder = new SqlConnectionStringBuilder(connectionString);
                targetBuilder.InitialCatalog = databaseFilter;
                _targetConnectionString = targetBuilder.ConnectionString;
            }
            catch
            {
                _targetConnectionString = null;
            }
        }
        else
        {
            _targetConnectionString = null;
        }

        _historyService = new VersionHistoryService(
            _logger,
            (await _configService.GetLocalHistoryConfigAsync().ConfigureAwait(true)).DatabaseName);

        // 解析连接字符串，显示服务器和数据库信息
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            _currentServerName = builder.DataSource;
            var displayName = !string.IsNullOrEmpty(databaseFilter) ? databaseFilter : builder.InitialCatalog;
            _isDatabaseResolved = !string.IsNullOrEmpty(displayName);

            ServerLabel.Text = _currentServerName ?? "";

            // 构建悬浮提示信息（显式 ToolTip 对象确保多行渲染）
            var userName = !string.IsNullOrEmpty(builder.UserID) ? builder.UserID : "Windows 身份验证";
            ServerLabel.ToolTip = new System.Windows.Controls.ToolTip
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"服务器地址: {_currentServerName}\n连接用户: {userName}",
                    TextWrapping = System.Windows.TextWrapping.Wrap
                }
            };

            // 更新数据库下拉框
            _isDatabaseComboBoxUpdating = true;
            if (_isDatabaseResolved)
            {
                DatabaseComboBox.Text = displayName;
                DatabaseComboBox.Foreground = System.Windows.Media.Brushes.Black;
            }
            else
            {
                DatabaseComboBox.Text = "未识别";
                DatabaseComboBox.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
                DatabaseComboBox.ToolTip = "无法从窗口标题识别数据库名，当前显示该服务器全部数据库的版本记录。你可以点击下拉选择目标数据库。";
            }
            _isDatabaseComboBoxUpdating = false;

            // 异步加载可用数据库列表
            _ = LoadAvailableDatabasesAsync(connectionString);
        }
        catch
        {
            ServerLabel.Text = "";
            ServerLabel.ToolTip = null;
            DatabaseComboBox.Text = "";
            _isDatabaseResolved = false;
        }

        try
        {
            // 加载对象列表（搜索下拉框用），按当前数据库过滤，使用配置的 Top 限制
            var topLimit = config.ObjectListTopLimit > 0 ? config.ObjectListTopLimit : 200;
            _allObjects = await _historyService.GetObjectListAsync(connectionString, databaseFilter, top: topLimit).ConfigureAwait(true);
            // 按类型优先级排序（SP > VW > TBL > FN > TR > TYP > OBJ），再按名称排序
            _allObjects = _allObjects
                .OrderBy(o => o.TypeSortOrder)
                .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 记录总数（用于截断提示）
            _totalObjectCount = _allObjects.Count > 0 ? _allObjects[0].TotalCount : 0;
        }
        catch
        {
            _allObjects = new List<ObjectSummary>();
            _totalObjectCount = 0;
        }

        // 初始化搜索框
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _isUpdatingText = true;
        ObjectSearchBox.ItemsSource = _allObjects;
        ObjectSearchBox.IsEditable = true;
        _isUpdatingText = false;

        // 更新对象计数标签
        if (_totalObjectCount > 0 && _totalObjectCount > _allObjects.Count)
        {
            ObjectCountLabel.Text = $"已加载 {_allObjects.Count}/{_totalObjectCount} 个对象，输入关键字搜索更多";
        }
        else
        {
            ObjectCountLabel.Text = _allObjects.Count > 0 ? $"共 {_allObjects.Count} 个对象" : "";
        }

        // 更新连接状态标签
        UpdateConnectionStatusLabel();

        if (!string.IsNullOrEmpty(initialObjectName))
        {
            SetSearchBoxText(initialObjectName, false);
            await LoadVersionsAsync(initialObjectName);
        }
        else
        {
            SetSearchBoxText("未检测到数据库对象定义语句", true);
            VersionGrid.ItemsSource = null;
            StatusText.Text = "";
        }
    }

    /// <summary>
    /// 异步加载服务器上所有在线数据库列表，填充 DatabaseComboBox 下拉选项。
    /// </summary>
    private async Task LoadAvailableDatabasesAsync(string connectionString)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        DatabaseComboBox.Text = "加载中...";
        DatabaseComboBox.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource;
            var authType = string.IsNullOrEmpty(builder.UserID) ? "Windows" : "SQL";

            // 读取缓存配置
            var config = await _configService.GetLocalHistoryConfigAsync().ConfigureAwait(true);
            var cacheMinutes = config.DatabaseListCacheMinutes;

            // 尝试从缓存获取
            var cached = DatabaseListCache.Get(dataSource, authType, cacheMinutes);
            if (cached != null)
            {
                _availableDatabases = cached;
                _logger.Debug($"[LocalHistory] 数据库列表缓存命中: {dataSource}, {cached.Count} 个数据库");
            }
            else
            {
                builder.InitialCatalog = "";
                builder.ConnectTimeout = 10;

                using var conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name";

                var databases = new List<string>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    databases.Add(reader.GetString(0));
                }

                _availableDatabases = databases;

                // 写入缓存
                DatabaseListCache.Set(dataSource, authType, databases);
                _logger.Debug($"[LocalHistory] 数据库列表已缓存: {dataSource}, {databases.Count} 个数据库");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _isDatabaseComboBoxUpdating = true;

            // 构建下拉列表：头部插入 "(全部数据库)" 选项
            var items = new List<string> { "(全部数据库)" };
            items.AddRange(_availableDatabases);
            DatabaseComboBox.ItemsSource = items;

            // 如果数据库已解析，选中当前项；否则选中"(全部数据库)"
            if (_isDatabaseResolved && !string.IsNullOrEmpty(_databaseFilter))
            {
                var matched = _availableDatabases.FirstOrDefault(
                    d => string.Equals(d, _databaseFilter, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    DatabaseComboBox.SelectedItem = matched;
                }
                else
                {
                    DatabaseComboBox.SelectedItem = "(全部数据库)";
                }
            }
            else
            {
                DatabaseComboBox.SelectedItem = "(全部数据库)";
                DatabaseComboBox.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
            }
            _isDatabaseComboBoxUpdating = false;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[LocalHistory] 加载数据库列表失败: {ex.Message}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DatabaseComboBox.Text = "加载失败";
            DatabaseComboBox.ToolTip = $"加载失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 数据库下拉框选择变更事件：用户手动切换数据库时，重新加载该库的对象列表和版本历史。
    /// </summary>
    private async void DatabaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDatabaseComboBoxUpdating || _isLoading) return;

        if (DatabaseComboBox.SelectedItem is string selectedDb && !string.IsNullOrEmpty(selectedDb))
        {
            if (selectedDb == "(全部数据库)")
            {
                // 切换到不过滤状态
                if (_databaseFilter == null) return;
                _logger.Information("[LocalHistory] 用户选择全部数据库，清除过滤");

                _databaseFilter = null;
                _isDatabaseResolved = false;
                _targetConnectionString = null;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DatabaseComboBox.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
                UpdateConnectionStatusLabel();

                // 重新加载全部对象
                if (_historyService != null && _connectionString != null)
                {
                    try
                    {
                        var config = await _configService.GetLocalHistoryConfigAsync().ConfigureAwait(true);
                        var topLimit = config.ObjectListTopLimit > 0 ? config.ObjectListTopLimit : 200;
                        _allObjects = await _historyService.GetObjectListAsync(_connectionString, null, top: topLimit).ConfigureAwait(true);
                        _allObjects = _allObjects
                            .OrderBy(o => o.TypeSortOrder)
                            .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        _totalObjectCount = _allObjects.Count > 0 ? _allObjects[0].TotalCount : 0;
                    }
                    catch
                    {
                        _allObjects = new List<ObjectSummary>();
                        _totalObjectCount = 0;
                    }
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _isUpdatingText = true;
                ObjectSearchBox.ItemsSource = _allObjects;
                _isUpdatingText = false;
                SetSearchBoxText("", false);
                VersionGrid.ItemsSource = null;
                UpdateStatus("已显示全部数据库的对象，请选择对象查看版本历史");
                return;
            }

            if (string.Equals(selectedDb, _databaseFilter, StringComparison.OrdinalIgnoreCase)) return;

            _logger.Information($"[LocalHistory] 用户手动切换数据库: {_databaseFilter} → {selectedDb}");
            await ReloadForDatabaseAsync(selectedDb);
        }
    }

    /// <summary>
    /// 以指定数据库为目标重新加载对象列表和版本历史。
    /// </summary>
    private async Task ReloadForDatabaseAsync(string databaseName)
    {
        if (_connectionString == null) return;

        _databaseFilter = databaseName;
        _isDatabaseResolved = true;

        // 更新目标连接串
        try
        {
            var targetBuilder = new SqlConnectionStringBuilder(_connectionString);
            targetBuilder.InitialCatalog = databaseName;
            _targetConnectionString = targetBuilder.ConnectionString;
        }
        catch { }

        // 更新 UI
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _isDatabaseComboBoxUpdating = true;
        DatabaseComboBox.Text = databaseName;
        DatabaseComboBox.Foreground = System.Windows.Media.Brushes.Black;
        DatabaseComboBox.ToolTip = $"当前数据库: {databaseName}";
        _isDatabaseComboBoxUpdating = false;

        // 重新加载对象列表
        try
        {
            if (_historyService != null)
            {
                _allObjects = await _historyService.GetObjectListAsync(_connectionString, databaseName, top: 100)
                    .ConfigureAwait(true);
                _allObjects = _allObjects
                    .OrderBy(o => o.TypeSortOrder)
                    .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch
        {
            _allObjects = new List<ObjectSummary>();
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _isUpdatingText = true;
        ObjectSearchBox.ItemsSource = _allObjects;
        _isUpdatingText = false;
        SetSearchBoxText("", false);
        VersionGrid.ItemsSource = null;
        UpdateStatus($"已切换到 {databaseName}，请选择对象查看版本历史");
    }

    private async Task LoadVersionsAsync(string objectName)
    {
        if (_historyService == null || string.IsNullOrEmpty(_connectionString))
            return;

        // 取消上一次未完成的加载：确保「最后选中的对象」才更新网格，
        // 避免快速切换对象/窗口时并发跑多次重查询（全表扫描）并造成结果错乱。
        _versionLoadCts?.Cancel();
        _versionLoadCts?.Dispose();
        _versionLoadCts = new CancellationTokenSource();
        var ct = _versionLoadCts.Token;

        _isLoading = true;
        // 加载前清空旧数据，避免残留旧对象行造成歧义
        VersionGrid.ItemsSource = null;
        // 显示加载遮罩 + 全局等待光标，提供可见反馈，消除「只能干等」观感
        LoadingOverlay.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.Wait;
        UpdateStatus("加载中...");

        try
        {
            var records = await _historyService.GetObjectHistoryAsync(
                _connectionString, objectName, _databaseFilter, ct: ct).ConfigureAwait(true);

            // 若本次加载已被更新的选择取消，则不覆盖网格
            if (ct.IsCancellationRequested)
                return;

            // 序号倒序赋值
            var count = records.Count;
            for (int i = 0; i < count; i++)
                records[i].RowNumber = count - i;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 切回 UI 线程期间可能已有新选择，再次确认未被取消
            if (ct.IsCancellationRequested)
                return;

            VersionGrid.ItemsSource = records;
            UpdateStatus($"共 {count} 个版本");

            // 更新搜索框文本
            SetSearchBoxText(objectName, false);
        }
        catch (OperationCanceledException)
        {
            // 被新选择取消，静默忽略（不覆盖网格）
        }
        catch (SqlException ex)
        {
            UpdateStatus("连接失败");
            _logger.Error(ex, $"[LocalHistory] 加载版本列表失败: {objectName}");
            MessageBox.Show(
                $"无法连接到版本历史数据库。\n\n{ex.Message}",
                "SSMSBoostPlus - 版本历史",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            UpdateStatus("加载失败");
            _logger.Error(ex, $"[LocalHistory] 加载版本列表异常: {objectName}");
        }
        finally
        {
            _isLoading = false;
            // 仅当本次加载未被更新的选择取消时复位遮罩与光标；
            // 若已被取消，说明有更新的加载在进行，由它负责复位，避免提前/重复复位。
            if (!ct.IsCancellationRequested)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                Mouse.OverrideCursor = null;
            }
        }
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    #region Search ComboBox Events

    private void ObjectSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = ObjectSearchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text) && text != "未检测到数据库对象定义语句")
            {
                _ = LoadVersionsAsync(text);
            }
        }
    }

    private void ObjectSearchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObjectSearchBox.SelectedItem is ObjectSummary selected)
        {
            _ = LoadVersionsAsync(selected.ObjectName);
        }
    }

    /// <summary>
    /// 设置搜索框文本（程序化设置时使用，避免触发实时过滤）。
    /// </summary>
    /// <param name="text">显示文本</param>
    /// <param name="isPlaceholder">是否为占位符（灰色斜体）</param>
    private void SetSearchBoxText(string text, bool isPlaceholder)
    {
        _isUpdatingText = true;
        ObjectSearchBox.Text = text;
        ObjectSearchBox.Foreground = isPlaceholder
            ? System.Windows.Media.Brushes.Gray
            : System.Windows.Media.Brushes.Black;
        ObjectSearchBox.FontStyle = isPlaceholder
            ? System.Windows.FontStyles.Italic
            : System.Windows.FontStyles.Normal;
        _isUpdatingText = false;
    }

    /// <summary>
    /// 搜索框文本变化时实时模糊过滤下拉列表（服务端搜索 + 300ms 防抖）。
    /// </summary>
    private async void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingText) return;

        var keyword = ObjectSearchBox.Text?.Trim();

        // 空文本或占位符 → 显示初始 Top 100 列表
        if (string.IsNullOrEmpty(keyword) || keyword == "未检测到数据库对象定义语句")
        {
            _isUpdatingText = true;
            ObjectSearchBox.ItemsSource = _allObjects;
            _isUpdatingText = false;
            return;
        }

        // 取消上一次搜索
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // 300ms 防抖
        try
        {
            await Task.Delay(300, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;
        if (_historyService == null || string.IsNullOrEmpty(_connectionString)) return;

        try
        {
            // 服务端搜索（按当前库 + 关键字过滤，Top 100）
            var results = await _historyService.GetObjectListAsync(
                _connectionString, _databaseFilter, top: 100, keyword: keyword, ct: ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested) return;

            // 按类型优先级排序
            results = results
                .OrderBy(o => o.TypeSortOrder)
                .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ct.IsCancellationRequested) return;

            _isUpdatingText = true;
            ObjectSearchBox.ItemsSource = results;
            ObjectSearchBox.IsDropDownOpen = true;
            ObjectSearchBox.Text = keyword;
            _isUpdatingText = false;

            // 将光标移到末尾
            if (ObjectSearchBox.Template.FindName("PART_EditableTextBox", ObjectSearchBox) is TextBox editBox)
            {
                editBox.CaretIndex = keyword.Length;
            }
        }
        catch (OperationCanceledException)
        {
            // 搜索被取消，忽略
        }
        catch (Exception ex)
        {
            _logger.Warning($"[LocalHistory] 下拉框搜索失败: {ex.Message}");
        }
    }

    #endregion

    #region DataGrid Events

    private void VersionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 加载阶段跳过，避免检索成功后自动弹出差异比对对话框
        if (_isLoading) return;

        // 单行选中 → 弹出 Diff（与服务器当前版本对比）
        if (VersionGrid.SelectedItems.Count == 1 && VersionGrid.SelectedItem is VersionRecord record)
        {
            OpenDiffDialog(record);
        }
    }

    private void VersionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ctrl 多选两行 → 对比两个历史版本
        if (VersionGrid.SelectedItems.Count == 2)
        {
            var selected = VersionGrid.SelectedItems.Cast<VersionRecord>().ToList();
            OpenDiffDialog(leftRecord: selected[0], rightRecord: selected[1]);
        }
    }

    private void OpenDiffDialog(VersionRecord? serverRecord = null,
                                VersionRecord? leftRecord = null,
                                VersionRecord? rightRecord = null)
    {
        if (_historyService == null || string.IsNullOrEmpty(_connectionString))
            return;

        _ = OpenDiffDialogAsync(serverRecord, leftRecord, rightRecord);
    }

    /// <summary>
    /// 确保版本记录的 SqlCommand 已加载。列表查询出于性能考虑不再返回 sqlcommand 大字段，
    /// 在需要展示/复制时按 ID 通过 GetVersionSqlAsync 懒加载回填到记录上（顺带缓存，供后续复用）。
    /// </summary>
    private async Task EnsureSqlCommandLoadedAsync(VersionRecord? record, CancellationToken ct)
    {
        if (record == null || !string.IsNullOrEmpty(record.SqlCommand))
            return;
        if (_historyService == null || string.IsNullOrEmpty(_connectionString))
            return;

        try
        {
            var sql = await _historyService.GetVersionSqlAsync(_connectionString, record.Id, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(sql))
                record.SqlCommand = sql;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[LocalHistory] 懒加载版本 SQL 失败，ID={record.Id}: {ex.Message}");
        }
    }

    private async Task OpenDiffDialogAsync(
        VersionRecord? serverRecord,
        VersionRecord? leftRecord,
        VersionRecord? rightRecord)
    {
        try
        {
            string? leftScript = null;
            string? rightScript = null;
            string leftTitle = "";
            string rightTitle = "";
            string? statusMessage = null;

            if (serverRecord != null)
            {
                // 模式 A：服务器当前 vs 选中历史版本
                // 使用缓存的 _targetConnectionString（LoadForObject 时推导）
                if (!string.IsNullOrEmpty(_targetConnectionString))
                {
                    // P3 修复：使用 ParseObjectName 处理方括号、多种 schema 格式
                    var (schema, name) = VersionHistoryService.ParseObjectName(serverRecord.ObjectName);

                    leftScript = await _historyService.GetServerObjectDefinitionAsync(
                        _targetConnectionString, schema, name, serverRecord.ObjectType).ConfigureAwait(true);

                    if (leftScript == null)
                    {
                        // 区分错误类型：TABLE 类型不支持 vs 一般加载失败
                        if (serverRecord.ObjectType?.ToUpperInvariant() == "TABLE")
                            statusMessage = "不支持的对象类型: TABLE";
                        else
                            statusMessage = "服务器定义加载失败";
                    }
                }
                else
                {
                    statusMessage = "无法获取目标数据库连接";
                }

                await EnsureSqlCommandLoadedAsync(serverRecord, CancellationToken.None).ConfigureAwait(true);
                leftTitle = "服务器当前定义";
                rightTitle = $"V{serverRecord.Version} - {serverRecord.FormattedTime} ({serverRecord.LoginName})";
                rightScript = serverRecord.SqlCommand;
            }
            else if (leftRecord != null && rightRecord != null)
            {
                // 模式 B：两个历史版本对比
                var olderRecord = leftRecord.PostTime <= rightRecord.PostTime ? leftRecord : rightRecord;
                var newerRecord = leftRecord.PostTime > rightRecord.PostTime ? leftRecord : rightRecord;

                await EnsureSqlCommandLoadedAsync(olderRecord, CancellationToken.None).ConfigureAwait(true);
                await EnsureSqlCommandLoadedAsync(newerRecord, CancellationToken.None).ConfigureAwait(true);
                leftTitle = $"V{olderRecord.Version} - {olderRecord.FormattedTime} ({olderRecord.LoginName})";
                leftScript = olderRecord.SqlCommand;
                rightTitle = $"V{newerRecord.Version} - {newerRecord.FormattedTime} ({newerRecord.LoginName})";
                rightScript = newerRecord.SqlCommand;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (statusMessage != null)
                UpdateStatus(statusMessage);

            // 构建重试回调（仅模式 A 且服务器定义加载失败时有效）
            Func<Task<(string? leftScript, string? rightScript)>>? retryCallback = null;
            if (serverRecord != null && !string.IsNullOrEmpty(_targetConnectionString))
            {
                var capturedConnection = _targetConnectionString;
                var capturedObjectName = serverRecord.ObjectName;
                var capturedObjectType = serverRecord.ObjectType;
                var capturedHistoryService = _historyService;
                var capturedRightScript = rightScript;

                retryCallback = async () =>
                {
                    var (schema, name) = VersionHistoryService.ParseObjectName(capturedObjectName);
                    var reloadedLeft = await capturedHistoryService.GetServerObjectDefinitionAsync(
                        capturedConnection, schema, name, capturedObjectType).ConfigureAwait(false);
                    return (reloadedLeft, capturedRightScript);
                };
            }

            var diffDialog = new LocalHistoryDiffDialog(
                leftTitle, leftScript ?? "",
                rightTitle, rightScript ?? "",
                rightRecord?.SqlCommand ?? "",
                _logger,
                retryCallback);
            diffDialog.Owner = System.Windows.Window.GetWindow(this);
            diffDialog.ShowDialog();

            // 修复：SSMS Win32/WPF 混搭环境下，ToolWindow 不是标准 WPF Window，
            // Window.GetWindow 返回 null，ShowDialog 关闭后键盘焦点可能错误回到
            // LocalHistory ToolWindow 而非 SQL 编辑器。
            // DispatcherPriority.Background 确保在 WPF 所有焦点恢复操作完成后才执行激活。
            if (diffDialog.DialogResult == true)
            {
                var scriptToOpen = diffDialog.ScriptToOpenInEditor;
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                        if (dte == null)
                        {
                            _logger.Warning("[LocalHistory] 在编辑器中打开失败：DTE 不可用");
                            return;
                        }

                        if (!string.IsNullOrEmpty(scriptToOpen))
                        {
                            // SSMS 是 VS 隔离外壳，没有项目/项模板系统，
                            // ItemOperations.NewFile("SQL Files\\SQL Query", ...) 会抛
                            // "模板"SQL Files\SQL Query"对选定的项目无效"。
                            // 改用 SSMS 原生命令 Query.NewQuery 新建查询编辑器窗口。
                            dte.ExecuteCommand("Query.NewQuery");

                            var doc = dte.ActiveDocument;
                            if (doc == null)
                            {
                                _logger.Warning("[LocalHistory] 在编辑器中打开失败：Query.NewQuery 后 ActiveDocument 为 null");
                                return;
                            }

                            var textDoc = (EnvDTE.TextDocument)doc.Object("TextDocument");
                            var editPoint = textDoc.CreateEditPoint();
                            editPoint.StartOfDocument();
                            editPoint.ReplaceText(
                                textDoc.EndPoint,
                                scriptToOpen,
                                (int)EnvDTE.vsEPReplaceTextOptions.vsEPReplaceTextAutoformat);
                            _logger.Information("[LocalHistory] 历史版本脚本已在编辑器中打开");
                        }

                        dte.ActiveDocument?.Activate();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[LocalHistory] 在编辑器中打开脚本失败");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[LocalHistory] 打开 Diff 对话框失败");
        }
    }

    #endregion

    #region Refresh

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var currentObject = ObjectSearchBox.Text?.Trim();

        // 无对象时提示用户
        if (string.IsNullOrEmpty(currentObject) || currentObject == "未检测到数据库对象定义语句")
        {
            UpdateStatus("请先选择对象");
            return;
        }

        // 防止重复点击
        if (_isLoading)
            return;

        // 禁用按钮并显示刷新中状态
        RefreshButton.IsEnabled = false;
        UpdateStatus("刷新中...");

        try
        {
            await LoadVersionsAsync(currentObject);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void SyncConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        // 防止加载中重复触发
        if (_isLoading) return;

        await RefreshForActiveWindowAsync();
    }

    #endregion

    #region Window Activation (DTE)

    private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
    {
        var gotKind = gotFocus?.Kind ?? "null";
        var lostKind = lostFocus?.Kind ?? "null";
        _logger.Debug($"[LocalHistory] WindowActivated: gotFocus.Kind={gotKind}, lostFocus.Kind={lostKind}");

        // 钉住检查：已钉住时跳过窗口切换跟随
        if (_isPinned)
        {
            _logger.Debug("[LocalHistory] WindowActivated: 已钉住，跳过窗口切换跟随");
            return;
        }

        if (gotFocus?.Kind == "Document")
        {
            _logger.Debug("[LocalHistory] WindowActivated: 文档窗口激活，触发防抖刷新");

            // 防抖：取消上一次待处理的刷新
            _activationCts?.Cancel();
            _activationCts = new CancellationTokenSource();
            var ct = _activationCts.Token;

            _ = DebouncedRefreshAsync(ct);
        }
        else
        {
            _logger.Debug($"[LocalHistory] WindowActivated: 非文档窗口 (Kind={gotKind})，跳过刷新");
        }
    }

    private async Task DebouncedRefreshAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configService.GetLocalHistoryConfigAsync().ConfigureAwait(true);
            var delayMs = config.DebounceMs > 0 ? config.DebounceMs : 300;

            await Task.Delay(delayMs, ct);
            if (ct.IsCancellationRequested) return;

            await RefreshForActiveWindowAsync();
        }
        catch (TaskCanceledException)
        {
            // 防抖取消，忽略
        }
    }

    private async Task RefreshForActiveWindowAsync()
    {
        // 防止加载中重复触发切换
        if (_isLoading)
        {
            _logger.Debug("[LocalHistory] RefreshForActiveWindow: 跳过（_isLoading=true）");
            return;
        }

        try
        {
            if (_connectionString == null)
            {
                _logger.Debug("[LocalHistory] RefreshForActiveWindow: 跳过（_connectionString=null）");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            UpdateStatus("切换中...");
            UpdateSsmsStatusBar("SSMSBoostPlus: 正在切换版本历史...");

            // 方案A：优先使用 ConnectionServiceFacade（窗口标题解析，更可靠），兜底 SsmsEditorService
            string? activeConnStr = null;
            var facade = LocalHistoryToolWindow.StaticConnectionFacade;
            if (facade != null)
            {
                try
                {
                    var connInfo = await facade.GetConnectionInfoAsync().ConfigureAwait(true);
                    if (connInfo != null && !string.IsNullOrEmpty(connInfo.ConnectionString))
                    {
                        activeConnStr = connInfo.ConnectionString;
                        _logger.Debug($"[LocalHistory] RefreshForActiveWindow: ConnectionServiceFacade 获取到连接: Server={connInfo.ServerInstance}, DB={connInfo.DatabaseName}");
                    }
                    else
                    {
                        _logger.Debug("[LocalHistory] RefreshForActiveWindow: ConnectionServiceFacade 返回 null，回退到 SsmsEditorService");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[LocalHistory] RefreshForActiveWindow: ConnectionServiceFacade 异常: {ex.Message}，回退到 SsmsEditorService");
                }
            }
            else
            {
                _logger.Debug("[LocalHistory] RefreshForActiveWindow: StaticConnectionFacade 为 null，使用 SsmsEditorService 兜底");
            }

            // 兜底：使用 SsmsEditorService.GetActiveConnectionString()
            if (string.IsNullOrEmpty(activeConnStr))
            {
                var editorService = new SSMS.Services.SsmsEditorService(_serviceProvider, _logger);
                activeConnStr = editorService.GetActiveConnectionString();
                if (string.IsNullOrEmpty(activeConnStr))
                {
                    _logger.Debug("[LocalHistory] RefreshForActiveWindow: SsmsEditorService 也返回 null，跳过刷新");
                }
            }

            if (!string.IsNullOrEmpty(activeConnStr))
            {
                try
                {
                    var activeBuilder = new SqlConnectionStringBuilder(activeConnStr);
                    var currentBuilder = new SqlConnectionStringBuilder(_connectionString);

                    var activeDataSource = activeBuilder.DataSource;
                    var activeDatabase = activeBuilder.InitialCatalog;

                    var currentDataSource = currentBuilder.DataSource;
                    var currentDatabase = _databaseFilter;

                    // 将空字符串归一化为 null 以便比较（空字符串表示数据库名未解析）
                    var activeDbNormalized = string.IsNullOrEmpty(activeDatabase) ? null : activeDatabase;
                    var currentDbNormalized = string.IsNullOrEmpty(currentDatabase) ? null : currentDatabase;

                    _logger.Debug($"[LocalHistory] RefreshForActiveWindow: 比较连接 active={activeDataSource}/{activeDbNormalized ?? "(未识别)"} vs current={currentDataSource}/{currentDbNormalized ?? "(未识别)"}");

                    if (!string.Equals(activeDataSource, currentDataSource, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(activeDbNormalized, currentDbNormalized, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Information($"[LocalHistory] 检测到连接变更: {currentDataSource}/{currentDbNormalized ?? "(未识别)"} → {activeDataSource}/{activeDbNormalized ?? "(未识别)"}，全量刷新");

                        // 权限隔离：检查白名单
                        var config = await _configService.GetLocalHistoryConfigAsync().ConfigureAwait(true);
                        if (config.AllowedDatabases.Count > 0 && activeDbNormalized != null)
                        {
                            if (!config.AllowedDatabases.Contains(activeDbNormalized, StringComparer.OrdinalIgnoreCase))
                            {
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                UpdateStatus($"当前数据库 \"{activeDbNormalized}\" 不在允许列表中，请联系管理员");
                                UpdateSsmsStatusBar($"SSMSBoostPlus: 数据库 \"{activeDbNormalized}\" 不在允许列表中");
                                return;
                            }
                        }

                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var dbDisplay = activeDbNormalized ?? "(未识别)";
                        UpdateStatus($"已切换到 {activeDataSource}/{dbDisplay}，加载中...");
                        UpdateSsmsStatusBar($"SSMSBoostPlus: 已切换到 {activeDataSource}/{dbDisplay}");

                        // 转换为 MESBASE 连接串并重新初始化
                        var originalDbName = activeDbNormalized;
                        var mesbaseBuilder = new SqlConnectionStringBuilder(activeConnStr)
                        {
                            InitialCatalog = config.DatabaseName
                        };

                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        LoadForObject(mesbaseBuilder.ConnectionString, null, originalDbName);
                        return;
                    }

                    _logger.Debug("[LocalHistory] RefreshForActiveWindow: 连接未变更，仅检查对象切换");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[LocalHistory] 连接变更检测失败: {ex.Message}");
                }
            }

            // 连接未变更，检查当前编辑窗口对象是否变化
            var editorServiceForCode = new SSMS.Services.SsmsEditorService(_serviceProvider, _logger);
            var (code, _) = await editorServiceForCode.GetCodeAsync().ConfigureAwait(true);

            string? objectName = null;
            if (!string.IsNullOrWhiteSpace(code))
            {
                var extractor = new SSMSBoostPlus.SQLParser.ObjectExtractor();
                var objects = extractor.ExtractObjects(code);
                var firstDef = objects.Find(o => o.IsDefinition);
                if (firstDef != null)
                    objectName = $"{firstDef.Schema}.{firstDef.Name}";

                // P3: 数据库名未解析时，尝试从 SQL 中解析 USE 语句作为兜底建议
                if (_databaseFilter == null)
                {
                    var useDb = TryParseUseDatabase(code);
                    if (useDb != null)
                    {
                        _logger.Information($"[LocalHistory] USE 解析兜底: 检测到数据库 {useDb}，更新下拉选择器建议");
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        _isDatabaseComboBoxUpdating = true;
                        _databaseFilter = useDb;
                        _isDatabaseResolved = true;
                        DatabaseComboBox.Text = useDb;
                        DatabaseComboBox.Foreground = System.Windows.Media.Brushes.Black;
                        DatabaseComboBox.ToolTip = $"从 SQL USE 语句解析: {useDb}";
                        _isDatabaseComboBoxUpdating = false;
                    }
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!string.IsNullOrEmpty(objectName))
            {
                SetSearchBoxText(objectName, false);
                await LoadVersionsAsync(objectName);
                UpdateSsmsStatusBar("SSMSBoostPlus: 版本历史就绪");
            }
            else
            {
                SetSearchBoxText("未检测到数据库对象定义语句", true);
                VersionGrid.ItemsSource = null;
                UpdateStatus("未检测到对象定义");
                UpdateSsmsStatusBar("SSMSBoostPlus: 未检测到对象定义");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[LocalHistory] 窗口切换刷新失败: {ex.Message}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            UpdateStatus("切换失败");
            UpdateSsmsStatusBar("SSMSBoostPlus: 切换失败");
        }
    }

    /// <summary>
    /// 同步更新 SSMS 主窗口状态栏文本。
    /// </summary>
    private void UpdateSsmsStatusBar(string message)
    {
        try
        {
            if (_dte?.StatusBar != null)
            {
                _dte.StatusBar.Text = message;
            }
        }
        catch
        {
            // 静默忽略，SSMS 状态栏更新失败不影响核心功能
        }
    }

    /// <summary>
    /// 从 SQL 文本中解析 USE [database] 语句，作为数据库名兜底提示。
    /// 仅在正则解析和 DTE COM 查询均失败时调用。
    /// </summary>
    private static string? TryParseUseDatabase(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            sql,
            @"^\s*USE\s+\[?(\w+)\]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Multiline);

        return match.Success ? match.Groups[1].Value : null;
    }

    #endregion

    #region Pin & Copy

    private void UpdateConnectionStatusLabel()
    {
        if (_isPinned)
        {
            ConnectionStatusLabel.Text = "已钉住";
            ConnectionStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE6, 0x7E, 0x22));
        }
        else if (_isDatabaseResolved)
        {
            ConnectionStatusLabel.Text = "已跟随";
            ConnectionStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x27, 0xAE, 0x60));
        }
        else
        {
            ConnectionStatusLabel.Text = "未识别";
            ConnectionStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE7, 0x4C, 0x3C));
        }
    }

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;

        if (_isPinned)
        {
            PinButton.Content = "📌 已钉住";
            PinButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE6, 0x7E, 0x22));
            ConnectionStatusLabel.Text = "已钉住";
            ConnectionStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE6, 0x7E, 0x22));
        }
        else
        {
            PinButton.Content = "📌";
            PinButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5));
            ConnectionStatusLabel.Text = "已跟随";
            ConnectionStatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x27, 0xAE, 0x60));

            // 取消钉住后自动重新跟随当前窗口
            await RefreshForActiveWindowAsync();
        }
    }

    private async void CopySql_Click(object sender, RoutedEventArgs e)
    {
        if (VersionGrid.SelectedItem is not VersionRecord record)
            return;

        try
        {
            // 列表查询不再返回 sqlcommand，复制前按需懒加载
            if (string.IsNullOrEmpty(record.SqlCommand))
                await EnsureSqlCommandLoadedAsync(record, CancellationToken.None).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(record.SqlCommand))
            {
                System.Windows.Clipboard.SetText(record.SqlCommand);
                UpdateStatus("SQL 已复制到剪贴板");
            }
            else
            {
                UpdateStatus("未找到该版本的 SQL 内容");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[LocalHistory] 复制 SQL 失败: {ex.Message}");
            UpdateStatus("复制失败");
        }
    }

    #endregion
}