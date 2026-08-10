using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Commands;
using SSMSBoostPlus.SSMS.Services;
using SSMSBoostPlus.SSMS.Services.Connection;

namespace SSMSBoostPlus.SSMS;

/// <summary>
/// Main VSIX package entry point for SSMSBoostPlus.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad("{e80ef1cb-6d64-4609-8faa-feacfd3bc89f}", PackageAutoLoadFlags.BackgroundLoad)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(UI.Chat.AIChatToolWindow))]
[ProvideToolWindow(typeof(UI.VersionHistory.LocalHistoryToolWindow))]
    public sealed class SSMSBoostPlusPackage : AsyncPackage
{
    public const string PackageGuidString = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    private IConfigurationService? _configService;
    private ConnectionCache? _connectionCache;
    private IDatabaseService? _databaseService;
    private SSMS.Services.SqlTemplateService? _sqlTemplateService;
    private SSMS.Services.DatabaseObjectCacheService? _dbObjectCacheService;
    private ConnectionServiceFacade? _connectionService;
    private ILoggerService? _packageLogger; // 全局日志实例（用于动态开关）

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            // Initialize services
            var logger = CreateLogger();
            _configService = new ConfigurationService(logger, GetConfigDirectory());
            var configService = _configService;

            // Load SSMS assemblies with smart loader (auto-detects version)
            SmartAssemblyLoader.EnsureAssembliesLoaded(logger);

            // Detect SSMS version and register appropriate providers
            var ssmsVersion = SmartAssemblyLoader.GetDetectedVersion();
            logger.Information($"检测到 SSMS 版本: {ssmsVersion}");

            // 创建连接凭据缓存（全局共享，所有提供者共用，文件持久化）
            // 缓存已成功登入过的凭据，避免每次从窗口标题解析时密码不可用导致的回退开销
            // 缓存文件保存在 AppData\SSMSBoostPlus\connection_cache.json，重启 SSMS 后自动加载
            var connectionCache = new ConnectionCache(logger);
            connectionCache.SetCacheDirectory(GetConfigDirectory());
            _connectionCache = connectionCache;  // Store as field for ExecutionInterceptor

            // ★ 扩展启用检测：连接缓存加载/状态栏提示延迟到 Shell 就绪 + 检测扩展启用后执行
            // （见 RunDiagnosticAfterShellInitialized → LoadCachesAfterExtensionEnabled）
            // 原因：SSMS"扩展→自定义菜单"开关只控制菜单显示，不控制 Package 加载；
            // 若扩展未启用，不应在后台加载连接缓存（避免"连接缓存已加载"状态栏提示等后台进程）
            
            // Register connection providers with fallback strategy
            var connectionProviders = new List<IConnectionInfoProvider>();
            
            // ⭐ 最高优先级：Axial-Style Provider（编译时强类型引用 SSMS 22 内部 DLL）
            // 仅在 SSMS 22+ 激活，借鉴 github.com/Axial-SQL/AxialSqlTools 已验证方案
            // 强类型访问 ServiceCache.ScriptFactory 和 IObjectExplorerService，解决纯反射方式的 NullReferenceException 问题
            if (ssmsVersion >= SSMSVersion.V22)
            {
                connectionProviders.Add(new AxialStyleConnectionProvider(this, logger, connectionCache));  // Priority -20: 最高优先级（SSMS 22+）
            }
            
            // 次优先级：ScriptFactory（纯反射方案，从当前活动查询窗口获取连接）
            connectionProviders.Add(new ScriptFactoryConnectionProvider(this, logger));  // Priority -15: ScriptFactory
            
            // 次高优先级：使用SSMS官方API（ISqlConnectionService）
            connectionProviders.Add(new SsmsConnectionServiceProvider(this, logger));  // Priority -10: Official SSMS APIs
            
            // 备选方案：从活动窗口获取
            connectionProviders.Add(new ActiveWindowConnectionProvider(this, logger));  // Priority -1: Active window

            // SSMS 22 深度探索 Provider（P-5：在 ActiveWindow 和 SSMS22 之间探索）
            if (ssmsVersion >= SSMSVersion.V22)
            {
                connectionProviders.Add(new SSMS22DiscoveryProvider(this, logger));  // Priority -5: SSMS 22 discovery
            }

            // Add SSMS 22 provider if applicable
            if (ssmsVersion >= SSMSVersion.V22)
            {
                connectionProviders.Add(new SSMS22ConnectionProvider(this, logger));  // Priority 0: SSMS 22 optimized
            }
            
            // Add standard providers
            connectionProviders.Add(new OfficialConnectionProvider(this, logger));      // Priority 1: Official SDK interfaces
            connectionProviders.Add(new ReflectionConnectionProvider(this, logger));   // Priority 10: Reflection fallback
            
            var connectionService = new ConnectionServiceFacade(connectionProviders, logger);
            _connectionService = connectionService;

            // 创建共享的 DatabaseService 实例（通过 IDatabaseService 接口传递给需要的 Command）
            _databaseService = new DatabaseService(logger);
            IDatabaseService databaseService = _databaseService;

            // 创建数据库对象缓存服务（用于智能补全）
            // ★ 延迟文件加载：等检测扩展启用后再 LoadFromFileAsync（见 LoadCachesAfterExtensionEnabled）
            _dbObjectCacheService = new SSMS.Services.DatabaseObjectCacheService(
                logger, connectionService, GetConfigDirectory());

            // Register Version Guard commands
            await Commands.VersionGuardCommand.InitializeAsync(this, logger, configService, databaseService, connectionService);
            await Commands.VersionGuardEnableToggleCommand.InitializeAsync(this, logger, configService);
            await Commands.VersionGuardReloadCommand.InitializeAsync(this, logger, configService, databaseService);
            await Commands.VersionGuardConfigCommand.InitializeAsync(this, logger, configService);

            // Register CODE WITH AI commands
            await Commands.AISuggestionsCommand.InitializeAsync(this, logger, configService);
            await Commands.AICommentsCommand.InitializeAsync(this, logger, configService);
            await Commands.AIFormattingCommand.InitializeAsync(this, logger, configService);
            await Commands.AIPerformanceCommand.InitializeAsync(this, logger, configService, databaseService);

            // 设置 AIChatToolWindow 静态服务引用（确保 VS SDK 布局恢复时也能正确初始化）
            UI.Chat.AIChatToolWindow.StaticServiceProvider = this;
            UI.Chat.AIChatToolWindow.StaticConfigService = configService;
            UI.Chat.AIChatToolWindow.StaticLogger = logger;

            // 设置 LocalHistoryToolWindow 静态服务引用
            UI.VersionHistory.LocalHistoryToolWindow.StaticServiceProvider = this;
            UI.VersionHistory.LocalHistoryToolWindow.StaticConfigService = configService;
            UI.VersionHistory.LocalHistoryToolWindow.StaticLogger = logger;

            // Register AI Chat Panel command
            await Commands.AIChatCommand.InitializeAsync(this, logger, configService);

            // Register Local History commands
            var localHistoryEditorService = new SsmsEditorService(this, logger);
            await Commands.LocalHistoryCommand.InitializeAsync(
                this, logger, configService, connectionService, localHistoryEditorService);

            // Register SQL Tools commands (share the same connection service as version guard)
            await Commands.KeywordSearchCommand.InitializeAsync(this, logger, connectionService);
            await Commands.OpenScriptCommand.InitializeAsync(this, logger, connectionService);

            // Register Refresh Cache command (for database object completion cache)
            await Commands.RefreshCacheCommand.InitializeAsync(this, logger, _dbObjectCacheService);

            // Register configuration commands
            await Commands.AIConfigCommand.InitializeAsync(this, logger, configService);

            // 根据日志配置动态开关日志（在初始化流程中尽早应用）
            var logConfig = await configService.GetLoggingConfigurationAsync().ConfigureAwait(false);
            logger.SetEnabled(logConfig.Enabled);

            // Register feature toggle commands
            await Commands.EnableLoggingCommand.InitializeAsync(this, logger, configService, logger);
            await Commands.EnableCompletionCommand.InitializeAsync(this, logger, configService);

            // Initialize SQL Template service
            _sqlTemplateService = new SSMS.Services.SqlTemplateService(logger, GetConfigDirectory());
            _sqlTemplateService.Load();
            await Commands.SqlTemplateConfigCommand.InitializeAsync(this, logger, _sqlTemplateService);

            // Register version check command
            var versionCheckService = new VersionCheckService(GetConfigDirectory(),
                msg => logger.Information(msg));
            await Commands.VersionCheckCommand.InitializeAsync(this, versionCheckService,
                msg => logger.Information(msg));

            logger.Information("SSMSBoostPlus 包初始化成功。");
            
            // 自动运行一次诊断探索（仅SSMS 22）
            // 使用 ShellInitialized 事件替代原先的 Task.Delay(5000) hack
            if (ssmsVersion >= SSMSVersion.V22)
            {
                await RunDiagnosticAfterShellInitialized(logger);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SSMSBoostPlus 初始化错误: {ex}");
        }
    }

    private ILoggerService CreateLogger()
    {
        try
        {
            var logDir = Path.Combine(GetConfigDirectory(), "Logs");
            return new SerilogLoggerService(logDir, "Debug", 30, 10);
        }
        catch
        {
            return new ConsoleLoggerService();
        }
    }

    private string GetConfigDirectory()
    {
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var configDir = Path.GetDirectoryName(assemblyLocation)
            ?? throw new InvalidOperationException("无法确定插件程序集所在目录。");

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        return configDir;
    }
    
    private async Task RunDiagnosticExploration(ILoggerService logger)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            
            logger.Information("=== 自动诊断: 正在探索 SSMS 22 连接 API ===");
            
            // 1. 探索 ServiceCache.GlobalConnectionInfo
            //await ExploreGlobalConnectionInfo(logger);
            
            logger.Information("=== 自动诊断完成 ===");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "自动诊断失败");
        }
    }
    
    private async Task ExploreGlobalConnectionInfo(ILoggerService logger)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            var serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (serviceCacheType == null)
            {
                logger.Error("[自动诊断] 未找到 ServiceCache 类型");
                return;
            }

            // 探索 GlobalConnectionInfo
            var globalConnInfoProp = serviceCacheType.GetProperty("GlobalConnectionInfo",
                BindingFlags.Public | BindingFlags.Static);

            if (globalConnInfoProp != null)
            {
                var globalConnInfo = globalConnInfoProp.GetValue(null);
                logger.Information($"[自动诊断] GlobalConnectionInfo: {globalConnInfo?.GetType().FullName ?? "null"}");

                if (globalConnInfo != null)
                {
                    ExploreObjectProperties(globalConnInfo, logger);
                }
            }

            // 探索 ServiceProvider
            var serviceProviderProp = serviceCacheType.GetProperty("ServiceProvider",
                BindingFlags.Public | BindingFlags.Static);
            
            if (serviceProviderProp != null)
            {
                var serviceProvider = serviceProviderProp.GetValue(null);
                logger.Information($"[自动诊断] ServiceProvider: {serviceProvider?.GetType().FullName ?? "null"}");
            }

            // 探索 ScriptFactory
            var scriptFactoryProp = serviceCacheType.GetProperty("ScriptFactory",
                BindingFlags.Public | BindingFlags.Static);
            
            if (scriptFactoryProp != null)
            {
                var scriptFactory = scriptFactoryProp.GetValue(null);
                logger.Information($"[自动诊断] ScriptFactory: {scriptFactory?.GetType().FullName ?? "null"}");

                if (scriptFactory != null)
                {
                    logger.Information("[自动诊断] 正在探索 ScriptFactory 属性:");
                    var props = scriptFactory.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in props.Take(10))
                    {
                        logger.Information($"[自动诊断]   {prop.Name}: {prop.PropertyType.Name}");
                    }
                }
            }

            // 探索 ExtensibilityModel
            var extensibilityModelProp = serviceCacheType.GetProperty("ExtensibilityModel",
                BindingFlags.Public | BindingFlags.Static);
            
            if (extensibilityModelProp != null)
            {
                var extensibilityModel = extensibilityModelProp.GetValue(null);
                logger.Information($"[自动诊断] ExtensibilityModel: {extensibilityModel?.GetType().FullName ?? "null"}");
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[自动诊断] 探索 ServiceCache 属性失败");
        }
    }

    /// <summary>
    /// 注册执行拦截器，订阅 DTE BeforeExecute 事件以拦截 F5/Ctrl+F5 等 SQL 执行命令。
    /// 必须在 DTE 可用后（Shell 初始化完成）调用。
    /// </summary>
    private void RegisterExecutionInterceptor(ILoggerService logger, IDatabaseService databaseService)
    {
        try
        {
            var interceptor = new ExecutionInterceptor(this, logger, _configService!, _connectionCache,
                () => databaseService,
                connectionFacade: _connectionService!);
            interceptor.Subscribe();
            logger.Information("[ExecutionInterceptor] ✅ 执行拦截器已注册，将拦截 F5/Ctrl+F5 等 SQL 执行命令");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[ExecutionInterceptor] ❌ 注册执行拦截器失败");
        }
    }

    private void ExploreObjectProperties(object obj, ILoggerService logger)
    {
        try
        {
            var type = obj.GetType();
            logger.Information($"[自动诊断] 正在探索: {type.FullName}");

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                logger.Information($"[自动诊断]   {prop.Name}: {prop.PropertyType.Name}");
                
                try
                {
                    var value = prop.GetValue(obj);
                    if (value != null)
                    {
                            logger.Information($"[自动诊断]     值类型: {value.GetType().FullName}");

                        // 尝试提取连接信息
                        var serverInstanceProp = value.GetType().GetProperty("ServerInstance");
                        var databaseNameProp = value.GetType().GetProperty("DatabaseName");

                        if (serverInstanceProp != null || databaseNameProp != null)
                        {
                            var server = serverInstanceProp?.GetValue(value);
                            var database = databaseNameProp?.GetValue(value);

                            logger.Information($"[自动诊断]     ✅ 发现连接: Server={server}, Database={database}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug($"[自动诊断]     错误: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[自动诊断] 探索对象属性失败");
        }
    }

    /// <summary>
    /// 等待 Shell 完全初始化后再运行诊断探索和注册执行拦截器。
    /// 使用 EnvDTE.DTE 轮询方式替代不可靠的 Task.Delay 方式。
    /// DTE 只有 Shell 完全初始化后才可获取，因此可作为初始化完成的代理检测。
    /// </summary>
    private async Task RunDiagnosticAfterShellInitialized(ILoggerService logger)
    {
        try
        {
            // 轮询 DTE 服务，确认 Shell 已初始化（最长等待15秒）
            for (int i = 0; i < 15; i++)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = await GetServiceAsync(typeof(DTE)) as DTE;
                if (dte != null)
                {
                    logger.Information($"[Shell初始化] DTE 可用，Shell 已初始化（等待约 {i}s）");

                    // ★ 扩展启用检测：SSMS"扩展→自定义菜单"开关只控制菜单显示，不控制 Package 加载。
                    // 若扩展未启用，跳过所有后台服务（缓存加载/右键菜单/补全/键盘钩子/版本检查），
                    // 避免出现"扩展关闭但后台进程仍在运行"的问题（用户反馈）。
                    // 采用"确认式"检测：带重试等待 VSCT 菜单渲染（SSMS 22 菜单延迟出现），
                    // 多次检测仍无菜单 → 判定未启用（用户在自定义菜单中关闭了扩展）。
                    if (!await IsExtensionEnabledWithRetryAsync(dte, logger))
                    {
                        logger.Warning("[Shell初始化] SSMSBoost++ 未在'扩展→自定义菜单'中启用，跳过所有后台服务初始化");
                        return;
                    }
                    logger.Information("[Shell初始化] SSMSBoost++ 扩展已启用，开始后台服务初始化...");

                    // 扩展启用后，加载连接缓存 + DB 缓存（含状态栏进度提示）
                    await LoadCachesAfterExtensionEnabled(logger);

                    await RunDiagnosticExploration(logger);

                    // DTE 就绪后，注册执行拦截器（F5 拦截）
                    RegisterExecutionInterceptor(logger, _databaseService!);

                    // DTE 就绪后，注册 F12 键盘钩子（打开脚本功能）
                    Commands.OpenScriptCommand.RegisterF12Interceptor();

                    // DTE 就绪后，注册 F11 键盘钩子（关键词查询功能）
                    Commands.KeywordSearchCommand.RegisterF11Interceptor();

                    // DTE 就绪后，通过 DTE CommandBars 动态添加 AI 右键菜单项
                    await AddWpfContextMenuAsync(logger);

                    // DTE 就绪后，初始化 SQL 模板智能提示
                    var templateCompletionService = new SSMS.Services.SqlTemplateCompletionService(
                        this, logger, _sqlTemplateService!, _dbObjectCacheService);
                    await templateCompletionService.InitializeAsync();

                    // 初始化完成后，注册 ESC 键盘钩子（兜底关闭弹窗）
                    templateCompletionService.RegisterEscKeyboardHook();

                    // DTE 就绪后，启动版本自动检查
                    StartAutoVersionCheck(logger);

                    return;
                }

                await Task.Delay(1000);
            }

            logger.Warning("[Shell初始化] Shell 在15秒内未完成初始化，仍然运行诊断");

            // ★ 15 秒超时兜底：尝试检测扩展启用，未启用则跳过后台服务
            if (!IsExtensionEnabledViaAnyDte(logger))
            {
                logger.Warning("[Shell初始化] SSMSBoost++ 未在'扩展→自定义菜单'中启用，跳过后台服务初始化");
                return;
            }

            await LoadCachesAfterExtensionEnabled(logger);
            await RunDiagnosticExploration(logger);
            RegisterExecutionInterceptor(logger, _databaseService!);
            Commands.OpenScriptCommand.RegisterF12Interceptor();
            Commands.KeywordSearchCommand.RegisterF11Interceptor();
            await AddWpfContextMenuAsync(logger);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[Shell初始化] 检查 Shell 初始化状态失败，使用回退方案运行诊断");
            await Task.Delay(3000);
            if (IsExtensionEnabledViaAnyDte(logger))
            {
                await LoadCachesAfterExtensionEnabled(logger);
                await RunDiagnosticExploration(logger);
            }
        }
    }

    /// <summary>
    /// 带重试的扩展启用检测（"确认式"）。
    /// SSMS 22 中 VSCT 菜单可能延迟渲染（启动初期 MenuBar 中尚未出现），
    /// 因此"未找到菜单"需重试等待；多次仍无菜单 → 判定未启用（用户在自定义菜单中关闭了扩展）。
    /// 返回 true = 扩展已启用（开始后台服务）；false = 扩展未启用（跳过后台服务）。
    /// </summary>
    private async Task<bool> IsExtensionEnabledWithRetryAsync(DTE dte, ILoggerService logger)
    {
        const int MaxRetries = 5;
        const int RetryDelayMs = 1500;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            var result = TryDetectExtensionEnabled(dte, logger);
            if (result.HasValue)
            {
                logger.Information($"[扩展启用检测] 第 {attempt + 1} 次检测: 扩展{(result.Value ? "已启用" : "未启用")}");
                return result.Value;
            }

            // result == null → 菜单未找到（可能延迟渲染），等待后重试
            logger.Information($"[扩展启用检测] 第 {attempt + 1} 次检测未找到 VSCT 菜单，等待 {RetryDelayMs}ms 后重试...");
            await Task.Delay(RetryDelayMs);
        }

        // 多次重试仍未找到 VSCT 静态菜单 → 判定未启用
        // （用户已在"扩展→自定义菜单"中关闭 SSMSBoost++，菜单被 SSMS 隐藏/移除）
        logger.Warning("[扩展启用检测] 多次重试未找到 VSCT 静态注册的 SSMSBoost++ 菜单，判定为未启用");
        return false;
    }

    /// <summary>
    /// 单次扩展启用检测（三态返回）。
    /// 判断依据：MenuBar 中 VSCT 静态注册的 SSMSBoost++ 菜单的可见性。
    ///   - true  ：找到菜单且 Visible=true → 扩展启用
    ///   - false ：找到菜单但 Visible=false → 扩展未启用（SSMS 开关已关闭）
    ///   - null  ：未找到菜单（无法区分"延迟渲染"与"已关闭隐藏"，需重试确认）
    /// 注意：仅识别 VSCT 静态注册的菜单（Tag 不含 DTE 注入前缀 "SSMSBoostPlus_AI_"），
    /// 避免把历史 DTE 注入的残留菜单误判为 VSCT 菜单。
    /// </summary>
    private bool? TryDetectExtensionEnabled(DTE dte, ILoggerService logger)
    {
        try
        {
            var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
            if (commandBars == null)
            {
                logger.Warning("[扩展启用检测] CommandBars 为 null，按启用处理（保守）");
                return true;
            }

            Microsoft.VisualStudio.CommandBars.CommandBar menuBar = null;
            try { menuBar = commandBars["MenuBar"]; }
            catch
            {
                for (int i = 1; i <= commandBars.Count; i++)
                {
                    try
                    {
                        var bar = commandBars[i];
                        if (bar != null && bar.Type == Microsoft.VisualStudio.CommandBars.MsoBarType.msoBarTypeMenuBar)
                        {
                            menuBar = bar;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (menuBar == null)
            {
                logger.Warning("[扩展启用检测] 未找到 MenuBar，按启用处理（保守）");
                return true;
            }

            int ctrlCount = 0;
            try { ctrlCount = menuBar.Controls.Count; } catch { }

            for (int i = 1; i <= ctrlCount; i++)
            {
                try
                {
                    var ctrl = menuBar.Controls[i];
                    string caption = "";
                    try { caption = ctrl.Caption ?? ""; } catch { }
                    string tag = "";
                    try { tag = ctrl.Tag ?? ""; } catch { }

                    // 仅识别 VSCT 静态注册的菜单（Tag 不含 DTE 注入前缀 "SSMSBoostPlus_AI_"）
                    if (caption.IndexOf("SSMSBoost", StringComparison.OrdinalIgnoreCase) >= 0
                        && tag.IndexOf("SSMSBoostPlus_AI_", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        bool visible = true;
                        try { visible = ctrl.Visible; } catch { }
                        logger.Information($"[扩展启用检测] 找到 VSCT 静态注册的 SSMSBoost++ 菜单 (Controls[{i}]: '{caption}', Visible={visible})");
                        return visible;
                    }
                }
                catch { }
            }

            // 未找到 VSCT 静态菜单 → 返回 null（不确定，需重试确认是否延迟渲染）
            return null;
        }
        catch (Exception ex)
        {
            logger.Warning($"[扩展启用检测] 检测异常: {ex.Message}，按启用处理（保守）");
            return true;
        }
    }

    /// <summary>
    /// 无 DTE 参数时的扩展启用检测兜底（尝试获取 DTE；获取失败时按启用处理）。
    /// </summary>
    private bool IsExtensionEnabledViaAnyDte(ILoggerService logger)
    {
        try
        {
            var dte = GetService(typeof(DTE)) as DTE;
            if (dte == null)
            {
                logger.Warning("[扩展启用检测] DTE 不可用，按启用处理（保守）");
                return true;
            }
            var result = TryDetectExtensionEnabled(dte, logger);
            return result ?? true; // null（未找到）→ 保守按启用处理
        }
        catch (Exception ex)
        {
            logger.Warning($"[扩展启用检测] IsExtensionEnabledViaAnyDte 异常: {ex.Message}，按启用处理（保守）");
            return true;
        }
    }

    /// <summary>
    /// 扩展启用后加载连接缓存 + DB 缓存（含状态栏进度提示）。
    /// 该逻辑从 InitializeAsync 延迟到 Shell 就绪 + 扩展启用检测之后执行，
    /// 确保扩展未启用时不产生"连接缓存已加载"等后台进程提示。
    /// </summary>
    private async Task LoadCachesAfterExtensionEnabled(ILoggerService logger)
    {
        try
        {
            // 连接缓存：注册状态栏进度事件并异步加载
            if (_connectionCache != null)
            {
                var statusBar = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
                _connectionCache.OnLoadProgress += (s, e) =>
                {
                    if (statusBar != null)
                    {
                        statusBar.SetText($"SSMSBoostPlus: 正在加载连接缓存 ({e.PercentComplete}%)...");
                        if (e.PercentComplete >= 100)
                        {
                            statusBar.Clear();
                            statusBar.SetText($"SSMSBoostPlus: 连接缓存已加载 ({e.ServerName ?? "完成"})");
                            // 2秒后清除状态栏
                            _ = Task.Delay(2000).ContinueWith(_ =>
                            {
                                ThreadHelper.JoinableTaskFactory.Run(async () =>
                                {
                                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                    statusBar.Clear();
                                });
                            });
                        }
                    }
                };

                _ = _connectionCache.LoadFromFileAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.Warning($"[ConnectionCache] 后台加载缓存异常: {t.Exception?.Message}");
                });
            }

            // DB 对象缓存：异步加载缓存文件
            if (_dbObjectCacheService != null)
            {
                _ = _dbObjectCacheService.LoadFromFileAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.Warning($"[DbCache] 加载缓存文件异常: {t.Exception?.Message}");
                });
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[扩展启用检测] LoadCachesAfterExtensionEnabled 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 在 DTE 就绪后，通过 DTE CommandBars 运行时动态添加 AI 右键菜单项。
    /// 核心：枚举 CommandBars 找到 "SQL Files Editor Context"，然后 Controls.Add() 添加命令。
    /// 这是在 SSMS 22 中添加右键菜单的唯一可行方案（VSCT CommandPlacement 不生效）。
    /// </summary>
    private async Task AddWpfContextMenuAsync(ILoggerService logger)
    {
        try
        {
            var wpfService = new WpfContextMenuService(this, logger);
            await wpfService.InitializeAsync();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[右键菜单] 添加 AI 右键菜单项失败");
        }
    }

    /// <summary>
    /// 启动版本自动检查机制（每周 12:00 检查 Release 分支）。
    /// 使用 System.Threading.Timer 每小时检查一次是否需要检测更新。
    /// </summary>
    private void StartAutoVersionCheck(ILoggerService logger)
    {
        try
        {
            // 首次检查：在 Shell 初始化后 30 秒开始
            _ = Task.Run(async () =>
            {
                await Task.Delay(30000);
                await CheckAndNotifyUpdateAsync(logger);
            });

            // 定期检查：每小时检查一次是否需要更新（判断是否超过 7 天且 >= 12:00）
            var hourlyTimer = new System.Threading.Timer(
                async _ =>
                {
                    try
                    {
                        await CheckAndNotifyUpdateAsync(logger);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"[VersionCheck] 定时检查异常: {ex.Message}");
                    }
                },
                null,
                TimeSpan.FromHours(1),  // 首次触发：1小时后
                TimeSpan.FromHours(1)); // 后续触发：每小时一次

            logger.Information("[VersionCheck] 自动更新检查已启动（每小时检查一次）");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[VersionCheck] 启动自动更新检查失败");
        }
    }

    /// <summary>
    /// 检查是否需要检测更新，需要时执行版本检查。
    /// </summary>
    private async Task CheckAndNotifyUpdateAsync(ILoggerService logger)
    {
        try
        {
            var versionService = new VersionCheckService(GetConfigDirectory(),
                msg => logger.Information(msg));

            if (!versionService.ShouldCheckUpdate())
                return;

            logger.Information("[VersionCheck] 触发自动版本检查");

            if (Commands.VersionCheckCommand.Instance is VersionCheckCommand cmd)
            {
                await cmd.CheckForUpdateAsync(isManual: false);
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[VersionCheck] 自动检查执行失败: {ex.Message}");
        }
    }
}
