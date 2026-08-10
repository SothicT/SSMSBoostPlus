using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// SQL 编辑器右键菜单服务。
    /// 
    /// 核心策略：DTE CommandBars 运行时动态添加菜单项。
    /// 
    /// 工作流程：
    /// 1. 初始化时枚举所有 DTE CommandBars，找到 SQL 编辑器上下文菜单
    /// 2. 通过 DTE CommandBars API 将 AI 命令添加到右键菜单
    /// 3. 同时注册 WPF 事件用于诊断和验证
    /// 
    /// 之前尝试过的方案（均已证明无效）：
    ///   ❌ VSCT CommandPlacement 到 guidSHLMainMenu/IDM_VS_CTXT_CODEWIN — BeforeQueryStatus 从未触发
    ///   ❌ VSCT CommandPlacement 到 {5DD0BB59-...}/0x0100 — 同上
    ///   ❌ WPF ContextMenu / ContextMenuOpening — SSMS 不使用 WPF ContextMenu
    ///   ❌ IOleCommandTarget ShowContextMenu 拦截 — SSMS 不走此命令路由
    /// </summary>
    internal class WpfContextMenuService
    {
        private readonly AsyncPackage _package;
        private readonly ILoggerService _logger;
        private const string LogPrefix = "[右键菜单]";

        private static readonly Guid CommandSetGuid = new("c3d4e5f6-a7b8-9012-cdef-234567890123");

        private const int CmdIdVersionGuardCheck = 0x0100;
        private const int CmdIdVersionGuardEnableToggle = 0x0101;
        private const int CmdIdVersionGuardReloadFromDb = 0x0102;
        private const int CmdIdAiSuggestions = 0x0200;
        private const int CmdIdAiComments = 0x0201;
        private const int CmdIdAiFormatting = 0x0202;
        private const int CmdIdAiPerformance = 0x0203;
        private const int CmdIdAIChatPanel = 0x0204;
        private const int CmdIdKeywordSearch = 0x0205;
        private const int CmdIdOpenScript = 0x0206;
        private const int CmdIdRefreshCache = 0x0207;
        private const int CmdIdAIConfig = 0x0300;
        private const int CmdIdVersionGuardConfig = 0x0301;
        private const int CmdIdVersionCheck = 0x0302;
        private const int CmdIdSqlTemplateConfig = 0x0303;

        private const string TagPrefix = "SSMSBoostPlus_AI_";

        private static readonly (int CommandId, string DisplayName, string Tag, string ShortcutText)[] AiCommands = new (int, string, string, string)[]
        {
            (CmdIdAiSuggestions,  "🤖 智能建议",   TagPrefix + "Suggestions",  "Ctrl+Shift+A"),
            (CmdIdAiComments,     "🤖 AI智能注释",  TagPrefix + "Comments",     "Ctrl+Shift+C"),
            (CmdIdAiFormatting,   "🤖 AI自动格式化", TagPrefix + "Formatting",   "Ctrl+Shift+F"),
            (CmdIdAiPerformance,  "🤖 AI性能分析",  TagPrefix + "Performance",  "Ctrl+Shift+P"),
            (CmdIdAIChatPanel,   "💬 AI 对话面板", TagPrefix + "ChatPanel",     "Ctrl+Shift+K"),
            (CmdIdKeywordSearch, "🔍 关键词查询",   TagPrefix + "KeywordSearch", "F11"),
            (CmdIdOpenScript,    "📄 打开脚本",     TagPrefix + "OpenScript",   "F12"),
            (CmdIdRefreshCache,  "🔄 刷新对象缓存", TagPrefix + "RefreshCache", ""),
        };

        // 顶部菜单 SSMSBoost++ 的所有命令（BeginGroup=true 前添加分隔线）
        private static readonly (int CommandId, string DisplayName, string Tag, bool BeginGroup, string ShortcutText)[] TopMenuCommands = new (int, string, string, bool, string)[]
        {
            (CmdIdAiSuggestions,            "\U0001F916 智能建议",       TagPrefix + "Suggestions",       false, "Ctrl+Shift+A"),
            (CmdIdAiComments,               "\U0001F916 AI智能注释",      TagPrefix + "Comments",          false, "Ctrl+Shift+C"),
            (CmdIdAiFormatting,             "\U0001F916 AI自动格式化",    TagPrefix + "Formatting",        false, "Ctrl+Shift+F"),
            (CmdIdAiPerformance,            "\U0001F916 AI性能分析",      TagPrefix + "Performance",       false, "Ctrl+Shift+P"),
            (CmdIdAIChatPanel,              "\U0001F4AC AI 对话面板",     TagPrefix + "ChatPanel",         false, "Ctrl+Shift+K"),
            (CmdIdKeywordSearch,            "\U0001F50D 关键词查询",       TagPrefix + "KeywordSearch",     false, "F11"),
            (CmdIdOpenScript,               "\U0001F4C4 打开脚本",         TagPrefix + "OpenScript",        false, "F12"),
            (CmdIdVersionGuardEnableToggle, "\U0001F512 启用版本守护",     TagPrefix + "VersionGuardEnable",  true,  ""),
            (CmdIdVersionGuardCheck,        "\U0001F512 检查当前脚本版本",  TagPrefix + "VersionGuardCheck",   false, "Ctrl+Shift+V"),
            (CmdIdVersionGuardReloadFromDb, "\U0001F512 从数据库重新加载",  TagPrefix + "VersionGuardReload",  false, ""),
            (CmdIdAIConfig,                 "\u2699 AI 配置...",       TagPrefix + "AIConfig",          true,  ""),
            (CmdIdVersionGuardConfig,       "\u2699 版本守护设置...",   TagPrefix + "VersionGuardConfig", false, ""),
            (CmdIdSqlTemplateConfig,        "\U0001F4CB SQL 模板设置...",  TagPrefix + "SqlTemplateConfig",  false, ""),
            (CmdIdVersionCheck,             "\U0001F504 检查更新",         TagPrefix + "VersionCheck",      false, ""),
            (CmdIdRefreshCache,             "\U0001F504 刷新对象缓存",     TagPrefix + "RefreshCache",      false, ""),
        };

        private static readonly Dictionary<string, int> TagToCommandId = BuildTagLookup();

        private static Dictionary<string, int> BuildTagLookup()
        {
            var dict = new Dictionary<string, int>();
            foreach (var (cmdId, _, tag, _) in AiCommands)
                dict[tag] = cmdId;
            foreach (var (cmdId, _, tag, _, _) in TopMenuCommands)
                dict[tag] = cmdId;
            return dict;
        }

        // 已处理过的 text view 缓存
        private static readonly HashSet<IWpfTextView> _processedViews = new HashSet<IWpfTextView>();
        private static readonly object _lock = new object();

        private bool _isInitialized = false;
        private bool _hasAddedMenuItems = false;

        public WpfContextMenuService(AsyncPackage package, ILoggerService logger)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 初始化：枚举 CommandBars → 找到 SQL 编辑器右键菜单 → 添加 AI 命令
        /// </summary>
        public async System.Threading.Tasks.Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isInitialized)
            {
                _logger.Debug($"{LogPrefix} 已初始化，跳过");
                return;
            }

            _logger.Information($"{LogPrefix} === 开始初始化 ===");

            try
            {
                var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
                if (dte == null)
                {
                    _logger.Warning($"{LogPrefix} ❌ DTE 不可用");
                    return;
                }

                // ===== 步骤1: 枚举所有 CommandBars（关键诊断！）=====
                _logger.Information($"{LogPrefix} [步骤1] 枚举所有 DTE CommandBars...");
                var sqlEditorMenu = EnumerateAndFindContextMenu(dte);

                // ===== 步骤2: 如果找到 SQL 编辑器菜单，添加 AI 命令 =====
                if (sqlEditorMenu != null)
                {
                    _logger.Information($"{LogPrefix} [步骤2] ✅ 找到 SQL 编辑器上下文菜单，添加 AI 命令...");
                    AddAICommandsToMenu(sqlEditorMenu);
                    _hasAddedMenuItems = true;

                    // 注册 CommandBarEvents 监听菜单项点击，手动路由到 VSCT 命令处理器
                    RegisterCommandBarClickHandler(dte);
                }
                else
                {
                    _logger.Warning($"{LogPrefix} [步骤2] ❌ 未找到 SQL 编辑器上下文菜单，AI 命令未添加");
                    _logger.Warning($"{LogPrefix} → 可能原因：SSMS 22 使用了不同于 'SQL Files Editor Context' 的菜单名");
                    _logger.Warning($"{LogPrefix} → 请检查上面的 CommandBars 枚举日志，找到 SQL 编辑器对应的菜单名称");
                }

                // ===== 步骤2.5: 通过 DTE CommandBars 添加顶部 SSMSBoost++ 菜单 =====
                _logger.Information($"{LogPrefix} [步骤2.5] 添加顶部 SSMSBoost++ 菜单...");
                AddTopLevelMenu(dte);

                // ===== 步骤3: 注册 WindowEvents + WPF 诊断事件 =====
                _logger.Information($"{LogPrefix} [步骤3] 注册 WindowEvents 和诊断事件...");
                var windowEvents = dte.Events.WindowEvents;
                if (windowEvents != null)
                {
                    windowEvents.WindowActivated += OnWindowActivated;
                    _logger.Information($"{LogPrefix} ✅ WindowEvents 注册成功");
                }

                // 立即处理当前活动文档
                await ProcessActiveDocumentAsync();

                _isInitialized = true;
                _logger.Information($"{LogPrefix} === 初始化完成 === MenuItemsAdded={_hasAddedMenuItems}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ 初始化失败");
            }
        }

        #region CommandBars 枚举与发现

        /// <summary>
        /// 枚举所有 DTE CommandBars，找到 SQL 编辑器右键菜单。
        /// 这是最关键的方法——它将告诉我们 SSMS 22 中 SQL 编辑器右键菜单的确切名称。
        /// </summary>
        private dynamic EnumerateAndFindContextMenu(DTE dte)
        {
            try
            {
                var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
                if (commandBars == null)
                {
                    _logger.Warning($"{LogPrefix} ❌ CommandBars 为 null");
                    return null;
                }

                int totalCount = commandBars.Count;
                _logger.Information($"{LogPrefix} CommandBars 总数: {totalCount}");

                int contextMenuCount = 0;
                int toolbarCount = 0;
                var allPopupMenus = new List<(int Index, string Name, int ControlCount)>();
                var sqlRelatedMenus = new List<(int Index, string Name, int ControlCount)>();

                for (int i = 1; i <= totalCount; i++)
                {
                    try
                    {
                        var bar = commandBars[i];
                        if (bar == null) continue;

                        string name = "(null)";
                        try { name = bar.Name; } catch { }

                        var barType = bar.Type;
                        bool isPopup = (barType == Microsoft.VisualStudio.CommandBars.MsoBarType.msoBarTypePopup);

                        int controlCount = 0;
                        try { controlCount = bar.Controls.Count; } catch { }

                        if (isPopup)
                        {
                            contextMenuCount++;
                            // 记录所有上下文菜单
                            //_logger.Information($"{LogPrefix}   上下文菜单 [{i}]: '{name}' (Controls={controlCount})");

                            allPopupMenus.Add((i, name, controlCount));

                            // 检查是否与 SQL/Editor/Code 相关
                            var nameUpper = name.ToUpperInvariant();
                            if (nameUpper.Contains("SQL") || nameUpper.Contains("EDITOR") ||
                                nameUpper.Contains("CODE") || nameUpper.Contains("CONTEXT") ||
                                nameUpper.Contains("QUERY") || nameUpper.Contains("TEXT") ||
                                nameUpper.Contains("SCRIPT"))
                            {
                                sqlRelatedMenus.Add((i, name, controlCount));
                            }
                        }
                        else
                        {
                            toolbarCount++;
                        }
                    }
                    catch (Exception exBar)
                    {
                        _logger.Debug($"{LogPrefix}   枚举 [{i}] 异常: {exBar.Message}");
                    }
                }

                _logger.Information($"{LogPrefix} === CommandBars 枚举完成: {contextMenuCount} 上下文菜单, {toolbarCount} 工具栏 ===");

                if (sqlRelatedMenus.Count > 0)
                {
                    _logger.Information($"{LogPrefix} 🔍 发现 {sqlRelatedMenus.Count} 个 SQL/Editor 相关上下文菜单:");
                    // foreach (var (idx, name, ctrlCount) in sqlRelatedMenus)
                    // {
                    //     _logger.Information($"{LogPrefix}   ✅ [{idx}] '{name}' (Controls={ctrlCount})");
                    // }
                }
                else
                {
                    _logger.Warning($"{LogPrefix} ⚠️ 未发现 SQL/Editor 相关上下文菜单");
                }

                // ===== 尝试按优先级找到正确的上下文菜单 =====
                return TryFindContextMenu(commandBars, sqlRelatedMenus);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ 枚举 CommandBars 失败");
                return null;
            }
        }

        /// <summary>
        /// 按优先级尝试找到 SQL 编辑器上下文菜单。
        /// </summary>
        private dynamic TryFindContextMenu(Microsoft.VisualStudio.CommandBars.CommandBars commandBars,
            List<(int Index, string Name, int ControlCount)> sqlRelatedMenus)
        {
            // 优先级1: 按名称直接查找
            // ⭐ 关键发现：SSMS 22 SQL 编辑器右键菜单 = "SQL Files Editor Context"（不是 "Code Window"！）
            // "Code Window" 是通用代码编辑器的菜单（有"标记命令"、"片段"等）
            // "SQL Files Editor Context" 才是 SQL 查询编辑器的右键菜单（有"剪切/复制/粘贴"、"连接"等）
            string[] priorityNames = new string[]
            {
                "SQL Files Editor Context",     // ⭐⭐⭐ SSMS SQL 查询编辑器右键菜单（Controls=48，含剪切/复制/粘贴/连接等）
                "Code Window",                  // VS 通用代码编辑器上下文菜单（不是 SQL 编辑器的）
                "Sql Editor Context",           // SSMS Plus 使用的名称（可能不存在）
                "SQL Editor Context",
                "Editor Context Menus",         // 编辑器上下文菜单容器
                "Query Editor Context Menu",
                "Text Editor Context",
            };

            foreach (var name in priorityNames)
            {
                try
                {
                    var menu = commandBars[name];
                    if (menu != null)
                    {
                        int ctrlCount = 0;
                        try { ctrlCount = menu.Controls.Count; } catch { }

                        _logger.Information($"{LogPrefix} ✅ 按名称找到: '{name}' (Controls={ctrlCount})");
                        LogMenuControls(menu);
                        return menu;
                    }
                }
                catch
                {
                    _logger.Debug($"{LogPrefix}   '{name}' 不存在");
                }
            }

            // 优先级2: 从 SQL 相关菜单中选择最可能的
            // 优先选择控件数 > 5 且 < 50 的（合理大小的右键菜单）
            foreach (var (idx, name, ctrlCount) in sqlRelatedMenus.OrderByDescending(x => x.ControlCount))
            {
                if (ctrlCount >= 5 && ctrlCount <= 60)
                {
                    try
                    {
                        var menu = commandBars[name];
                        if (menu != null)
                        {
                            _logger.Information($"{LogPrefix} ✅ 从 SQL 相关菜单中选择: '{name}' (Controls={ctrlCount})");
                            LogMenuControls(menu);
                            return menu;
                        }
                    }
                    catch { }
                }
            }

            // 优先级3: 从 SQL 相关菜单中选择任何一个非空的
            foreach (var (idx, name, ctrlCount) in sqlRelatedMenus)
            {
                if (ctrlCount > 0)
                {
                    try
                    {
                        var menu = commandBars[name];
                        if (menu != null)
                        {
                            _logger.Information($"{LogPrefix} ✅ 降级选择: '{name}' (Controls={ctrlCount})");
                            LogMenuControls(menu);
                            return menu;
                        }
                    }
                    catch { }
                }
            }

            _logger.Warning($"{LogPrefix} ❌ 所有方式均未找到合适的上下文菜单");
            return null;
        }

        /// <summary>
        /// 记录菜单中的控件列表，帮助确认是否是正确的菜单。
        /// </summary>
        private void LogMenuControls(dynamic menu)
        {
            try
            {
                var controls = menu.Controls;
                int count = 0;
                try { count = controls.Count; } catch { }

                _logger.Information($"{LogPrefix}   菜单控件列表 ({count} 项):");
                int maxLog = Math.Min(count, 30);
                for (int i = 1; i <= maxLog; i++)
                {
                    try
                    {
                        var ctrl = controls[i];
                        string caption = "";
                        try { caption = ctrl.Caption ?? ""; } catch { }
                        string typeStr = "?";
                        try { typeStr = ctrl.Type.ToString(); } catch { }

                        // if (!string.IsNullOrEmpty(caption))
                        //     _logger.Information($"{LogPrefix}     [{i}] '{caption}' ({typeStr})");
                        // else
                        //     _logger.Information($"{LogPrefix}     [{i}] (分隔线) ({typeStr})");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{LogPrefix}   记录控件失败: {ex.Message}");
            }
        }

        #endregion

        #region DTE CommandBars 添加菜单项

        /// <summary>
        /// 通过 DTE CommandBars API 将 AI 命令添加到上下文菜单。
        /// 这是唯一可靠的方式——绕过 VSCT CommandPlacement 的 GUID 问题。
        /// </summary>
        private void AddAICommandsToMenu(dynamic contextMenu)
        {
            try
            {
                // 1. 先清理残留项
                int cleaned = CleanupExistingItems(contextMenu);
                _logger.Information($"{LogPrefix} 清理了 {cleaned} 个残留菜单项");

                // 2. 添加 AI 子菜单头 + 各命令
                // 注意：DTE CommandBars 的 Controls.Add 方法：
                //   Add(Type, Id, Parameter, Before, Temporary)
                //   Type=1 (msoControlButton), Id=命令数值 ID

                int addedCount = 0;
                foreach (var (cmdId, displayName, tag, shortcutText) in AiCommands)
                {
                    if (AddCommandToMenu(contextMenu, cmdId, displayName, tag, shortcutText))
                    {
                        addedCount++;
                    }
                }

                _logger.Information($"{LogPrefix} ✅✅ AI 命令添加完成: {addedCount}/{AiCommands.Length} 项");

                // 3. 验证添加结果
                try
                {
                    var controls = contextMenu.Controls;
                    int finalCount = 0;
                    try { finalCount = controls.Count; } catch { }
                    _logger.Information($"{LogPrefix} 添加后菜单总控件数: {finalCount}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ 添加 AI 命令失败");
            }
        }

        /// <summary>
        /// 向菜单中添加一个 AI 命令按钮。
        /// DTE CommandBars 的 Controls.Add 方法的 Id 参数仅接受整数（COM 限制），
        /// 不接受 "GUID:ID" 字符串格式。因此传入整数 commandId。
        /// 虽然整数 ID 无法直接路由到 VSCT 命令处理器，但菜单项能正常显示。
        /// 点击路由通过 DTE.CommandBarEvents 事件监听处理。
        /// 快捷键显示策略（US-03）：
        ///   首选 ShortcutText 属性 —— VS 菜单系统自动靠右对齐（与 VSCT KeyBindings 效果一致）
        ///   降级 Caption 中插入 \t（Tab 字符）—— Windows 菜单标准右对齐行为
        /// </summary>
        private bool AddCommandToMenu(dynamic contextMenu, int commandId, string displayName, string tag, string shortcutText = "")
        {
            try
            {
                var controls = contextMenu.Controls;

                // Controls.Add(Type, Id, Parameter, Before, Temporary)
                // 注意：Id 参数必须为整数，COM 不支持字符串 "GUID:ID" 格式
                var control = controls.Add(
                    1,               // Type: msoControlButton
                    commandId,       // Id: 命令的数值 ID（仅整数，COM 限制）
                    Type.Missing,    // Parameter
                    Type.Missing,    // Before (添加到末尾)
                    true             // Temporary (SSMS 关闭后自动清理，下次初始化时重新添加)
                );

                if (control != null)
                {
                    // ★ US-03 快捷键显示：
                    // 1) 首选 ShortcutText 属性：VS 菜单系统自动右对齐显示快捷键
                    // 2) 降级 Caption 中 \t：Windows 菜单标准右对齐行为（Notepad "保存 Ctrl+S"）
                    if (!string.IsNullOrEmpty(shortcutText))
                    {
                        bool shortcutSet = false;
                        try
                        {
                            // dynamic 绑定：运行时对象基于 Office 互操作，可能有 ShortcutText 属性
                            ((dynamic)control).ShortcutText = shortcutText;
                            shortcutSet = true;
                            _logger.Debug($"{LogPrefix}   ✅ 快捷键 ShortcutText 设置成功: '{displayName}' → {shortcutText}");
                        }
                        catch { }

                        if (shortcutSet)
                        {
                            try { control.Caption = displayName; } catch { }
                        }
                        else
                        {
                            try { control.Caption = $"{displayName}\t{shortcutText}"; } catch { }
                        }
                    }
                    else
                    {
                        try { control.Caption = displayName; } catch { }
                    }
                    try { control.Tag = tag; } catch { }
                    try { control.Visible = true; } catch { }

                    // 通过 COM CommandBarButton.Click 事件捕获点击，路由到 VSCT 命令
                    try
                    {
                        var btn = control as CommandBarButton;
                        if (btn != null)
                        {
                            btn.Click += new _CommandBarButtonEvents_ClickEventHandler(OnCommandBarButtonClick);
                        }
                    }
                    catch { }

                    _logger.Debug($"{LogPrefix}   ✅ 已添加: '{displayName}' (CmdID=0x{commandId:X4})");
                    return true;
                }
                else
                {
                    _logger.Warning($"{LogPrefix}   ⚠️ 添加返回 null: '{displayName}'");
                    return false;
                }
            }
            catch (COMException comEx)
            {
                _logger.Warning($"{LogPrefix}   ❌ COM 错误添加 '{displayName}': {comEx.Message} (0x{comEx.ErrorCode:X8})");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning($"{LogPrefix}   ❌ 添加失败 '{displayName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清理之前添加的残留菜单项（Tag 以 SSMSBoostPlus_AI_ 开头的）。
        /// </summary>
        private int CleanupExistingItems(dynamic contextMenu)
        {
            int count = 0;
            try
            {
                var controls = contextMenu.Controls;
                int maxIterations = 0;
                try { maxIterations = controls.Count; } catch { }

                for (int i = maxIterations; i >= 1; i--)
                {
                    try
                    {
                        var ctrl = controls[i];
                        string tag = "";
                        try { tag = ctrl.Tag ?? ""; } catch { }

                        if (tag.StartsWith(TagPrefix))
                        {
                            _logger.Debug($"{LogPrefix}   删除残留项: [{i}] tag='{tag}'");
                            ctrl.Delete();
                            count++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        /// <summary>
        /// 顶部菜单：仅保留 VSCT 静态注册的 SSMSBoost++ 菜单（受 SSMS"扩展→自定义菜单"开关控制）。
        /// 不再使用 DTE 动态注入（DTE 注入绕过开关，导致扩展未启用时也显示菜单）。
        /// 仅做：清理历史 DTE 注入残留 + 确认 VSCT 菜单存在。
        /// </summary>
        private void AddTopLevelMenu(DTE dte)
        {
            try
            {
                var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
                if (commandBars == null)
                {
                    _logger.Warning($"{LogPrefix} [顶部菜单] ❌ CommandBars 为 null");
                    return;
                }

                // 查找主菜单栏（MenuBar）
                Microsoft.VisualStudio.CommandBars.CommandBar menuBar = null;
                try
                {
                    menuBar = commandBars["MenuBar"];
                }
                catch
                {
                    // 遍历查找 msoBarTypeMenuBar
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
                    _logger.Warning($"{LogPrefix} [顶部菜单] ❌ 未找到 MenuBar");
                    return;
                }

                _logger.Information($"{LogPrefix} [顶部菜单] ✅ 找到 MenuBar: '{menuBar.Name}' (Controls={menuBar.Controls.Count})");

                // ★ 清理历史 DTE 注入的残留 SSMSBoost++ 菜单（Tag + Caption 双匹配，循环删除）
                // 注意：仅清理 DTE 动态注入的残留（Tag 以 TagPrefix 开头），
                // 不删除 VSCT 静态注册的菜单（其 Tag 不含 TagPrefix，受 SSMS 扩展菜单开关控制）
                CleanupTopMenu(menuBar);

                // 检查 VSCT 是否已注册了 SSMSBoost++ 菜单（由 PkgDef + /setup 注册，受 SSMS 开关控制）
                bool ctmenuRegistered = false;
                try
                {
                    int ctrlCount = menuBar.Controls.Count;
                    for (int i = 1; i <= ctrlCount; i++)
                    {
                        try
                        {
                            var ctrl = menuBar.Controls[i];
                            string caption = "";
                            try { caption = ctrl.Caption ?? ""; } catch { }
                            string tag = "";
                            try { tag = ctrl.Tag ?? ""; } catch { }

                            // 仅将非 DTE 注入（Tag 不含 TagPrefix）的 SSMSBoost++ 菜单视为 VSCT 静态注册
                            if (caption.IndexOf("SSMSBoost", StringComparison.OrdinalIgnoreCase) >= 0
                                && tag.IndexOf(TagPrefix, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                ctmenuRegistered = true;
                                _logger.Information($"{LogPrefix} [顶部菜单] ✅ VSCT 静态注册的 SSMSBoost++ 菜单存在 (Controls[{i}]: '{caption}')");
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (ctmenuRegistered)
                {
                    // ★ 关键修复：VSCT 静态注册的顶部菜单受 SSMS"扩展→自定义菜单"开关控制。
                    // 不删除、不 DTE 注入，直接保留 —— 开关关闭时 SSMS 自动隐藏该菜单。
                    _logger.Information($"{LogPrefix} [顶部菜单] ✅ 顶部菜单由 VSCT 静态注册提供（受 SSMS 扩展菜单开关控制），跳过 DTE 注入");
                    return;
                }

                // VSCT 未注册（开关未启用或 PkgDef 未生效）→ 不再 DTE 注入兜底，
                // 避免出现"扩展未启用但菜单仍显示"的绕过开关问题（用户反馈 BUG）
                _logger.Information($"{LogPrefix} [顶部菜单] ⏭ VSCT 未注册 SSMSBoost++ 菜单（扩展未在'自定义菜单'中启用），不注入顶部菜单");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} [顶部菜单] ❌ 初始化失败");
            }
        }

        /// <summary>
        /// 清理之前 DTE 注入的残留顶部菜单项（Tag 或 Caption 匹配，循环删除）。
        /// 注意：仅清理 DTE 动态注入的残留（Tag 以 TagPrefix 开头，或 Caption 含 SSMSBoost++），
        /// VSCT 静态注册的菜单 Tag 不含 TagPrefix，不会被误删（受 SSMS 扩展菜单开关控制）。
        /// </summary>
        private void CleanupTopMenu(dynamic menuBar)
        {
            try
            {
                var controls = menuBar.Controls;
                int count = 0;
                try { count = controls.Count; } catch { }

                for (int i = count; i >= 1; i--)
                {
                    try
                    {
                        var ctrl = controls[i];
                        string tag = "";
                        try { tag = ctrl.Tag ?? ""; } catch { }

                        // 仅清理 DTE 动态注入的残留（Tag = SSMSBoostPlus_AI_xxx）
                        // ★ 关键：绝不按 Caption 匹配删除 —— VSCT 静态菜单的 Caption 同为 "SSMSBoost++"，
                        //   但 Tag 不含 TagPrefix（受 SSMS"扩展→自定义菜单"开关控制），必须保留！
                        if (tag.StartsWith(TagPrefix))
                        {
                            _logger.Debug($"{LogPrefix} [顶部菜单]   删除 DTE 注入残留项: [{i}] tag='{tag}'");
                            try { ctrl.Delete(); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion

        #region 命令路由

        /// <summary>
        /// 注册右键菜单点击监听。
        /// DTE CommandBars 的 Controls.Add 仅接受整数 ID（COM 限制），
        /// 无法通过 "GUID:ID" 字符串格式直接路由到 VSCT 命令处理器。
        /// 因此通过 CommandBarButton.Click COM 事件捕获点击，
        /// 手动通过 IVsUIShell.PostExecCommand 执行 VSCT 命令。
        /// </summary>
        private void RegisterCommandBarClickHandler(DTE dte)
        {
            try
            {
                _logger.Debug($"{LogPrefix} CommandBarEvents 路由注册完成");
            }
            catch { }
        }

        /// <summary>
        /// CommandBarButton.Click 事件处理器。
        /// 检查 Tag 前缀，通过 IVsUIShell.PostExecCommand 执行对应的 VSCT 命令。
        /// </summary>
        private void OnCommandBarButtonClick(CommandBarButton ctrl, ref bool cancelDefault)
        {
            try
            {
                string tag = null;
                try { tag = ctrl.Tag as string; } catch { }

                if (string.IsNullOrEmpty(tag) || !tag.StartsWith(TagPrefix))
                    return;

                if (TagToCommandId.TryGetValue(tag, out int cmdId))
                {
                    _logger.Information($"{LogPrefix} 🎯 点击: tag='{tag}' (CmdID=0x{cmdId:X4})");
                    ExecuteVsCommand(cmdId);
                }
            }
            catch { }
        }

        /// <summary>
        /// 通过 IVsUIShell 直接执行 VSCT 命令。
        /// </summary>
        private void ExecuteVsCommand(int commandId)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                if (uiShell != null)
                {
                    var guidCmdSet = CommandSetGuid;
                    object variantOut = null;
                    uiShell.PostExecCommand(ref guidCmdSet, (uint)commandId, 0, ref variantOut);
                    _logger.Information($"{LogPrefix} ✅ 执行命令: GUID={CommandSetGuid:B}, ID=0x{commandId:X4}");
                }
                else
                {
                    _logger.Warning($"{LogPrefix} ⚠️ IVsUIShell 不可用");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ 执行命令失败");
            }
        }

        #endregion

        #region WPF 事件诊断

        private async void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            try
            {
                if (gotFocus == null) return;

                string kind = null;
                try { kind = gotFocus.Kind; } catch { }
                if (kind != "Document") return;

                string documentName = null;
                try { documentName = gotFocus.Document?.Name; } catch { }
                if (documentName == null) return;

                string ext = System.IO.Path.GetExtension(documentName)?.ToLowerInvariant() ?? "";
                if (ext != ".sql") return;

                _logger.Debug($"{LogPrefix} SQL 文档激活: '{documentName}'");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await ProcessActiveDocumentAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} WindowActivated 异常");
            }
        }

        /// <summary>
        /// 处理活动 SQL 文档：注册 WPF 右键诊断事件。
        /// </summary>
        public async System.Threading.Tasks.Task ProcessActiveDocumentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var vsTextView = await GetTextViewAsync();
                if (vsTextView == null) return;

                var wpfTextView = await GetWpfTextViewAsync(vsTextView);
                if (wpfTextView == null) return;

                lock (_lock)
                {
                    if (_processedViews.Contains(wpfTextView))
                        return;
                    _processedViews.Add(wpfTextView);
                }

                // 注册右键诊断事件
                var element = wpfTextView.VisualElement;
                if (element != null)
                {
                    element.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
                    element.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
                    _logger.Information($"{LogPrefix} ✅ PreviewMouseRightButtonDown 诊断已注册");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ 处理活动文档失败");
            }
        }

        private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _logger.Information($"{LogPrefix} 📌 PreviewMouseRightButtonDown 触发");
            _logger.Information($"{LogPrefix} → AI 命令已通过 DTE CommandBars 添加: {_hasAddedMenuItems}");
            // 不标记 Handled，让 SSMS 正常显示右键菜单
        }

        private async System.Threading.Tasks.Task<IVsTextView> GetTextViewAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
                if (dte?.ActiveDocument == null) return null;
                string docPath = dte.ActiveDocument.FullName;

                var openDocSvc = await _package.GetServiceAsync(typeof(SVsUIShellOpenDocument)) as IVsUIShellOpenDocument;
                if (openDocSvc == null) return null;

                Microsoft.VisualStudio.OLE.Interop.IServiceProvider sp = null;
                IVsUIHierarchy uiHierarchy = null;
                uint uiItemId = 0;
                IVsWindowFrame windowFrame = null;
                Guid guidLogicalView = Guid.Empty;

                if (openDocSvc.OpenDocumentViaProject(docPath, ref guidLogicalView, out sp, out uiHierarchy, out uiItemId, out windowFrame) != Microsoft.VisualStudio.VSConstants.S_OK || windowFrame == null)
                    return null;

                object docView = null;
                if (windowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView) != Microsoft.VisualStudio.VSConstants.S_OK)
                    return null;

                if (docView is IVsCodeWindow codeWindow)
                {
                    IVsTextView primaryView = null;
                    if (codeWindow.GetPrimaryView(out primaryView) == Microsoft.VisualStudio.VSConstants.S_OK && primaryView != null)
                        return primaryView;
                }

                if (docView is IVsTextView view)
                    return view;

                return null;
            }
            catch { return null; }
        }

        private async System.Threading.Tasks.Task<IWpfTextView> GetWpfTextViewAsync(IVsTextView vsTextView)
        {
            try
            {
                var componentModel = await _package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                if (componentModel == null) return null;

                var adapterService = componentModel.GetService<IVsEditorAdaptersFactoryService>();
                if (adapterService == null) return null;

                return adapterService.GetWpfTextView(vsTextView);
            }
            catch { return null; }
        }

        #endregion
    }
}
