using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.UI.VersionHistory;

/// <summary>
/// SSMS 可停靠工具窗口：脚本版本历史。
/// 通过 ProvideToolWindow 注册后自动获得停靠/固定/自动隐藏能力。
/// 参照 AIChatToolWindow 实现模式。
/// </summary>
[Guid("D4E5F6A7-B8C9-0123-DEF4-567890ABCDEF")]
public class LocalHistoryToolWindow : ToolWindowPane
{
    private LocalHistoryToolWindowControl? _control;

    /// <summary>静态服务引用，由 SSMSBoostPlusPackage.InitializeAsync 设置</summary>
    public static IServiceProvider? StaticServiceProvider { get; set; }
    public static IConfigurationService? StaticConfigService { get; set; }
    public static ILoggerService? StaticLogger { get; set; }
    /// <summary>静态连接服务门面，由 InitializeContent 设置，供窗口切换时使用</summary>
    public static SSMS.Services.Connection.ConnectionServiceFacade? StaticConnectionFacade { get; set; }

    /// <summary>当前的 MESBASE 连接串（由 InitializeContent 设置）</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>当前初始对象名（可为 null）</summary>
    public string? InitialObjectName { get; private set; }

    /// <summary>
    /// VS SDK 回调：创建窗口框架后调用，用于布局恢复场景。
    /// </summary>
    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();

        if (StaticServiceProvider != null && StaticConfigService != null && StaticLogger != null)
        {
            try
            {
                if (_control == null)
                {
                    StaticLogger.Information("[LocalHistoryToolWindow] OnToolWindowCreated: 使用静态引用初始化...");
                    _control = new LocalHistoryToolWindowControl(StaticServiceProvider, StaticConfigService, StaticLogger);
                    Content = _control;
                    StaticLogger.Information("[LocalHistoryToolWindow] ✅ OnToolWindowCreated 初始化成功");
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Error(ex, "[LocalHistoryToolWindow] ❌ OnToolWindowCreated 初始化失败");
            }
        }
    }

    /// <summary>
    /// 无参构造函数（VS SDK 要求）。
    /// </summary>
    public LocalHistoryToolWindow()
        : base(null)
    {
        Caption = "📋 版本历史";

        StaticLogger?.Information("[LocalHistoryToolWindow] 构造函数被调用");

        try
        {
            if (TryInitializeContent())
            {
                StaticLogger?.Information("[LocalHistoryToolWindow] ✅ 构造函数中直接初始化成功");
                return;
            }

            StaticLogger?.Warning("[LocalHistoryToolWindow] ⚠️ 静态服务未就绪，显示占位文本");
        }
        catch (Exception ex)
        {
            StaticLogger?.Error(ex, "[LocalHistoryToolWindow] ❌ 构造函数初始化异常");
        }

        Content = new System.Windows.Controls.TextBlock
        {
            Text = "正在加载版本历史...",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize = 13,
            Foreground = System.Windows.Media.Brushes.Gray
        };
    }

    /// <summary>
    /// 初始化窗口内容。由 LocalHistoryCommand 在每次打开窗口时调用。
    /// 执行 3.3 节自动推断：连接串推导 + 对象推断。
    /// </summary>
    public void InitializeContent(
        IServiceProvider serviceProvider,
        IConfigurationService configService,
        ILoggerService logger,
        SSMS.Services.Connection.ConnectionServiceFacade connectionService,
        Services.SsmsEditorService editorService)
    {
        logger.Information("[LocalHistoryToolWindow] InitializeContent: 开始自动推断...");

        // 保存 ConnectionServiceFacade 供窗口切换时使用
        StaticConnectionFacade = connectionService;

        // 异步执行自动推断（避免阻塞 UI 线程）
        _ = InitializeContentAsync(serviceProvider, configService, logger, connectionService, editorService);
    }

    /// <summary>
    /// 带初始对象的快捷初始化（用于 VersionGuard 联动）。
    /// </summary>
    /// <param name="connectionString">MESBASE 连接串</param>
    /// <param name="initialObjectName">初始对象名（可为 null）</param>
    /// <param name="databaseFilter">原始数据库名，用于按库过滤（可为 null）</param>
    public void InitializeContent(string connectionString, string? initialObjectName, string? databaseFilter = null)
    {
        ConnectionString = connectionString;
        InitialObjectName = initialObjectName;
        UpdateCaption(initialObjectName);

        EnsureControl();

        if (_control != null && ConnectionString != null)
        {
            _control.LoadForObject(ConnectionString, initialObjectName, databaseFilter);
        }
    }

    private async System.Threading.Tasks.Task InitializeContentAsync(
        IServiceProvider serviceProvider,
        IConfigurationService configService,
        ILoggerService logger,
        SSMS.Services.Connection.ConnectionServiceFacade connectionService,
        Services.SsmsEditorService editorService)
    {
        try
        {
            // 3.3.1 连接串推导线 + 3.3.2 对象推断线并行执行
            var connTask = connectionService.GetConnectionInfoAsync();
            var scriptTask = editorService.GetCodeAsync();
            await System.Threading.Tasks.Task.WhenAll(connTask, scriptTask).ConfigureAwait(true);

            // 推导连接串
            var connInfo = connTask.Result;
            if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
            {
                logger.Warning("[LocalHistoryToolWindow] 无法获取当前数据库连接");
                System.Windows.MessageBox.Show(
                    "未检测到活动的数据库连接。\n请确保已连接到数据库服务器。",
                    "SSMSBoostPlus - 版本历史",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var config = await configService.GetLocalHistoryConfigAsync().ConfigureAwait(true);
            var originalDbName = connInfo.DatabaseName;
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connInfo.ConnectionString)
            {
                InitialCatalog = config.DatabaseName
            };
            ConnectionString = builder.ConnectionString;
            logger.Information($"[LocalHistoryToolWindow] MESBASE 连接串: Server={builder.DataSource}, DB={builder.InitialCatalog}, OriginalDB={originalDbName}");

            // 推断初始对象
            var (code, _) = scriptTask.Result;
            string? initialObjectName = null;

            if (!string.IsNullOrWhiteSpace(code))
            {
                var objectExtractor = new SSMSBoostPlus.SQLParser.ObjectExtractor();
                var objects = objectExtractor.ExtractObjects(code);
                var firstDef = objects.Find(o => o.IsDefinition);
                if (firstDef != null)
                {
                    initialObjectName = $"{firstDef.Schema}.{firstDef.Name}";
                    logger.Information($"[LocalHistoryToolWindow] 推断对象: {initialObjectName}");
                }
            }

            InitialObjectName = initialObjectName;
            UpdateCaption(initialObjectName);

            EnsureControl();

            if (_control != null && ConnectionString != null)
            {
                _control.LoadForObject(ConnectionString, initialObjectName, originalDbName);
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[LocalHistoryToolWindow] InitializeContentAsync 失败");
        }
    }

    private void UpdateCaption(string? objectName)
    {
        Caption = string.IsNullOrEmpty(objectName)
            ? "📋 版本历史"
            : $"📋 版本历史 - {objectName}";
    }

    private void EnsureControl()
    {
        if (_control == null)
        {
            _control = new LocalHistoryToolWindowControl(
                StaticServiceProvider ?? Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(System.IServiceProvider)) as IServiceProvider
                    ?? throw new InvalidOperationException("ServiceProvider is null"),
                StaticConfigService ?? throw new InvalidOperationException("ConfigService is null"),
                StaticLogger ?? new ConsoleLoggerService());
            Content = _control;
        }
    }

    /// <summary>
    /// 检测窗口 Frame 当前是否可见。
    /// </summary>
    private bool IsFrameVisible()
    {
        if (Frame == null) return false;
        try
        {
            var vsFrame = (Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame)Frame;
            object visibility = false;
            vsFrame.GetProperty(-7008, out visibility);
            return visibility is bool isVisible && isVisible;
        }
        catch
        {
            return false;
        }
    }

    private bool TryInitializeContent()
    {
        if (StaticServiceProvider == null || StaticConfigService == null || StaticLogger == null)
            return false;

        _control = new LocalHistoryToolWindowControl(StaticServiceProvider, StaticConfigService, StaticLogger);
        Content = _control;
        return true;
    }
}