using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services.Connection;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// SQL 模板智能提示补全服务。
    ///
    /// 架构参考：
    /// - Microsoft VSSDK-Extensibility-Samples (Ook_Language_Integration)
    /// - https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-displaying-statement-completion
    ///
    /// 核心设计（与 Microsoft 官方模式一致）：
    /// 1. 所有按键处理在 IOleCommandTarget.Exec 中完成（同步，无竞态）
    /// 2. TYPECHAR → 同步触发/过滤弹窗（不用 TextBuffer.Changed）
    /// 3. RETURN/TAB → 提交选中项
    /// 4. UP/DOWN → 导航选中项
    /// 5. CANCEL → 关闭弹窗
    /// 6. BACKSPACE/DELETE → 重新评估弹窗
    /// </summary>
    public class SqlTemplateCompletionService
    {
        private readonly AsyncPackage _package;
        private readonly ILoggerService _logger;
        private readonly SqlTemplateService _templateService;
        private readonly DatabaseObjectCacheService? _cacheService;
        private static readonly HashSet<IWpfTextView> _processedViews = new();
        private static readonly object _lock = new();
        private static readonly HashSet<IVsTextView> _filteredTextViews = new();

        // ★ 上下文感知补全引擎（新增模块）
        private readonly SqlBreaker _sqlBreaker = new();
        private readonly SyntaxLocator _syntaxLocator = new();
        private readonly CompletionDispatcher? _completionDispatcher;

        /// <summary>
        /// 代码补全功能全局开关。由 EnableCompletionCommand 动态控制。
        /// </summary>
        public static bool IsEnabled { get; set; } = true;

        private const string CursorMarker = "$cursor";

        // SQL 关键词列表（用于智能提示补全）
        private static readonly string[] SqlKeywords =
        {
            // DML - 数据操作
            "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS",
            "BETWEEN", "LIKE", "IS", "NULL", "AS", "ON",
            "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE",
            // JOIN
            "JOIN", "INNER", "LEFT", "RIGHT", "OUTER", "CROSS", "FULL",
            // 排序/分组
            "GROUP", "BY", "ORDER", "ASC", "DESC", "HAVING",
            "TOP", "DISTINCT",
            // 集合
            "UNION", "ALL", "INTERSECT", "EXCEPT",
            // 条件表达式
            "CASE", "WHEN", "THEN", "ELSE", "END",
            "COALESCE", "NULLIF", "ISNULL",
            // 事务
            "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION",
            // 流程控制
            "IF", "ELSE", "WHILE", "BREAK", "CONTINUE",
            "RETURN", "TRY", "CATCH", "THROW",
            // 游标
            "CURSOR", "OPEN", "CLOSE", "DEALLOCATE", "FETCH", "NEXT",
            // 执行
            "EXEC", "EXECUTE", "SP_EXECUTESQL",
            "PRINT", "RAISERROR", "WAITFOR", "DELAY",
            // DDL - 数据定义
            "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "INDEX",
            "PROCEDURE", "FUNCTION", "TRIGGER", "SCHEMA", "DATABASE",
            "CONSTRAINT", "PRIMARY", "KEY", "FOREIGN", "REFERENCES",
            "DEFAULT", "CHECK", "NOCHECK", "WITH", "GRANT", "REVOKE",
            // 聚合函数
            "COUNT", "SUM", "AVG", "MIN", "MAX",
            // 窗口函数
            "OVER", "PARTITION", "ROWS", "RANGE",
            "RANK", "ROW_NUMBER", "DENSE_RANK", "NTILE",
            "LEAD", "LAG", "FIRST_VALUE", "LAST_VALUE",
            // 字符串函数
            "LEN", "LEFT", "RIGHT", "SUBSTRING", "CHARINDEX",
            "PATINDEX", "REPLACE", "STUFF", "REVERSE",
            "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM",
            "CONCAT", "CONCAT_WS", "STRING_AGG", "STRING_SPLIT",
            "REPLICATE", "SPACE", "FORMAT",
            // 日期函数
            "GETDATE", "GETUTCDATE", "SYSDATETIME", "DATEADD",
            "DATEDIFF", "DATEPART", "YEAR", "MONTH", "DAY",
            "EOMONTH", "DATEFROMPARTS",
            // 数学函数
            "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT",
            // 转换函数
            "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT",
            "PARSE", "TRY_PARSE",
            // 系统函数
            "IDENTITY", "SCOPE_IDENTITY", "NEWID", "NEWSEQUENTIALID",
            "OBJECT_ID", "COL_LENGTH", "DB_NAME",
            // 其他常用
            "GO", "USE", "OPTION", "RECOMPILE",
            "OUTPUT", "INTO", "FOR", "XML", "PATH", "JSON",
            "OFFSET", "FETCH", "NEXT", "ONLY", "ROWS",
            "PIVOT", "UNPIVOT",
            "APPLY", "OUTER",
            "BULK", "INSERT",
            "TRUNCATE", "IDENTITY",
            "STATISTICS", "REBUILD", "REORGANIZE",
            "SCHEMA", "DATABASE", "LOGIN", "USER",
            "BACKUP", "RESTORE", "LOG",
            "OPENJSON", "OPENXML",
            "IIF", "CHOOSE",
            // ★ 表提示（Table Hints）
            "WITH", "NOLOCK", "ROWLOCK", "PAGLOCK", "TABLOCK",
            "TABLOCKX", "HOLDLOCK", "UPDLOCK", "XLOCK", "READPAST",
            "NOEXPAND", "INDEX", "FORCE", "KEEPFIXED",
        };

        // 常用组合 SQL 片段（旧系统兜底时展示，作为模板关键词的补充）
        private static readonly (string display, string insert)[] CompoundSqlSnippets =
        {
            ("OFFSET  ROWS FETCH NEXT  ROWS ONLY", "OFFSET  ROWS FETCH NEXT  ROWS ONLY "),
            ("LEFT JOIN  ON ",                      "LEFT JOIN  ON "),
            ("RIGHT JOIN  ON ",                     "RIGHT JOIN  ON "),
            ("INNER JOIN  ON ",                     "INNER JOIN  ON "),
            ("ORDER BY ",                           "ORDER BY "),
            ("GROUP BY ",                           "GROUP BY "),
            ("INSERT INTO ",                        "INSERT INTO "),
            ("WITH (NOLOCK)",                       "WITH (NOLOCK) "),
        };

        // ================================================================
        // 补全分类：决定当前弹窗展示哪些类型的候选项
        // ================================================================
        private enum CompletionCategory
        {
            /// <summary>仅关键词 + 模板（一般输入上下文，不查 DB 对象）</summary>
            KeywordsAndTemplates,
            /// <summary>仅表 + 视图（FROM/SELECT/JOIN 等之后）</summary>
            TablesAndViews,
            /// <summary>仅存储过程 + 函数（EXEC 之后）</summary>
            ProceduresAndFunctions,
        }

        // ================================================================
        // SQL 关键词前缀索引（静态初始化一次，避免每次按键全量遍历）
        // ================================================================
        private static readonly Dictionary<string, List<string>> _keywordPrefixIndex;

        static SqlTemplateCompletionService()
        {
            _keywordPrefixIndex = new Dictionary<string, List<string>>();
            foreach (var keyword in SqlKeywords)
            {
                var lower = keyword.ToLowerInvariant();
                int maxLen = Math.Min(3, lower.Length);
                for (int len = 1; len <= maxLen; len++)
                {
                    var prefix = lower.Substring(0, len);
                    if (!_keywordPrefixIndex.TryGetValue(prefix, out var list))
                    {
                        list = new List<string>();
                        _keywordPrefixIndex[prefix] = list;
                    }
                    list.Add(keyword);
                }
            }
        }

        // Popup UI components
        private Popup? _popup;
        private ListBox? _listBox;
        private IWpfTextView? _activeTextView;
        private bool _isPopupVisible;
        private bool _isExpanding;

        // 当前匹配的候选项列表（与 ListBox.Items 同步）
        // 统一支持 SQL 模板 + 数据库对象
        private List<CompletionCandidate> _currentMatches = new();

        // 节流：SSMS 对每次按键发送多次命令事件，需要去重
        private DateTime _lastMoveTime = DateTime.MinValue;
        private const int MoveThrottleMs = 150;

        // 展开节流：防止 SSMS 重复发送 TAB 事件导致展开后插入 TAB 字符
        private DateTime _lastExpandTime = DateTime.MinValue;
        private const int ExpandThrottleMs = 300;

        // 布局评估节流：防止 SSMS 重复布局变化事件导致多次重评估
        private DateTime _lastLayoutEvalTime = DateTime.MinValue;
        private const int LayoutEvalThrottleMs = 100;

        // 服务器切换检测节流：避免每次文本变化都调用 GetConnectionInfoAsync（COM 调用）
        private DateTime _lastServerSwitchCheckTime = DateTime.MinValue;
        private const int ServerSwitchCheckThrottleMs = 2000;

        // 文本变化时间：用于焦点丢失时判断是否因编辑导致
        private DateTime _lastTextChangeTime = DateTime.MinValue;

        // 用户主动关闭弹窗的时间（如 ESC），用于防止 OnLayoutChanged 立即重开弹窗
        private DateTime _lastUserHideTime = DateTime.MinValue;

        // US-08: 抑制弹窗自动重开标志
        // TAB/ENTER/ESC 设置为 true，用户主动输入字符时清除为 false
        // 防止 ExpandSelectedTemplate 插入文本触发的 OnLayoutChanged → OnCharTyped 重开弹窗
        private bool _suppressAutoReopen = false;

        // US-06: 输入防抖定时器（120ms 窗口内连续输入仅最后一次触发补全计算）
        private System.Windows.Threading.DispatcherTimer? _debounceTimer;
        private int _debounceVersion; // 防抖版本号，用于取消过期的回调

        // 前台窗口监控定时器（检测跨应用切换）
        private System.Windows.Threading.DispatcherTimer? _focusCheckTimer;

        // 命令过滤器引用（用于关闭 SSMS IntelliSense 弹窗）
        private CompletionCommandFilter? _commandFilter;

        // 表结构预览弹窗
        private Popup? _structurePopup;
        private TextBox? _structureTextBox;
        private DateTime _structurePopupDismissTime = DateTime.MinValue;

        // 内部编辑标志：防止文本缓冲区变化事件关闭弹窗
        private bool _isInternalEdit;

        // ================================================================
        // 分类优化相关字段
        // ================================================================

        // 空格触发标志：用户按空格呼出弹窗后，后续输入字母保持分类过滤
        private bool _hasSpaceTrigger;
        private CompletionCategory _lastSpaceCategory = CompletionCategory.KeywordsAndTemplates;

        // 最近一次补全上下文分类（用于在 ExpandSelectedTemplate 中确定 DB 选中后的弹窗分类）
        private CompletionCategory _lastCompletionCategory = CompletionCategory.KeywordsAndTemplates;

        // 模板缓存：避免每次按键调用 GetAllTemplates() 重新排序
        private List<SqlTemplate> _cachedTemplates = new();
        private bool _templatesDirty = true;

        public SqlTemplateCompletionService(
            AsyncPackage package,
            ILoggerService logger,
            SqlTemplateService templateService,
            DatabaseObjectCacheService? cacheService = null)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
            _cacheService = cacheService;

            // ★ 初始化上下文感知补全引擎
            if (_cacheService != null)
            {
                _completionDispatcher = new CompletionDispatcher(_cacheService);
            }
        }

        /// <summary>
        /// 初始化服务。
        /// </summary>
        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
                if (dte == null) return;

                var windowEvents = dte.Events.WindowEvents;
                if (windowEvents != null)
                {
                    windowEvents.WindowActivated += OnWindowActivated;
                    _logger.Information("[SqlTemplate] 已注册 WindowEvents 监听");
                }

                await ProcessActiveDocumentAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 初始化失败");
            }
        }

        private async void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            try
            {
                if (gotFocus == null) return;

                string kind = null;
                try { kind = gotFocus.Kind; } catch { }
                if (kind != "Document")
                {
                    HidePopup();
                    return;
                }

                string documentName = null;
                try { documentName = gotFocus.Document?.Name; } catch { }
                if (documentName == null)
                {
                    HidePopup();
                    return;
                }

                string ext = System.IO.Path.GetExtension(documentName)?.ToLowerInvariant() ?? "";
                if (ext != ".sql")
                {
                    HidePopup();
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                HidePopup();
                await ProcessActiveDocumentAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] WindowActivated 异常");
            }
        }

        /// <summary>
        /// 处理活动文档：获取 IWpfTextView 并注册事件。
        /// </summary>
        public async Task ProcessActiveDocumentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var vsTextView = await GetTextViewAsync();
                if (vsTextView == null) return;

                var wpfTextView = await GetWpfTextViewAsync(vsTextView);
                if (wpfTextView == null) return;

                _activeTextView = wpfTextView;

                // ★ 同步当前服务器和数据库名称（用于 Schema 检测）
                if (_cacheService != null)
                {
                    _currentServer = _cacheService.GetCurrentServer();
                    _currentDatabase = _cacheService.GetCurrentDatabase();
                }

                // 注册事件（仅首次）
                bool isNewView;
                lock (_lock)
                {
                    isNewView = !_processedViews.Contains(wpfTextView);
                    if (isNewView)
                    {
                        _processedViews.Add(wpfTextView);
                    }
                }

                if (isNewView)
                {
                    var element = wpfTextView.VisualElement;
                    if (element != null)
                    {
                        // 鼠标点击关闭弹窗
                        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
                        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                    }

                    // 订阅布局变化（滚动时隐藏提示）
                    wpfTextView.LayoutChanged -= OnLayoutChanged;
                    wpfTextView.LayoutChanged += OnLayoutChanged;
                }

                // 添加 IOleCommandTarget 命令过滤器
                if (!_filteredTextViews.Contains(vsTextView))
                {
                    try
                    {
                        var filter = new CompletionCommandFilter(this);
                        IOleCommandTarget next;
                        int hr = vsTextView.AddCommandFilter(filter, out next);
                        filter.Next = next;
                        _filteredTextViews.Add(vsTextView);
                        _commandFilter = filter;
                        _logger.Information($"[SqlTemplate] ✅ 命令过滤器已添加 (HR=0x{hr:X8})");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[SqlTemplate] 添加命令过滤器失败");
                    }
                }

                _logger.Information("[SqlTemplate] ✅ 智能提示已注册");

                // 自动加载数据库对象缓存（后台异步，不阻塞 UI）
                if (_cacheService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _cacheService.TryAutoLoadAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "[SqlTemplate] 自动加载缓存失败");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 处理活动文档失败");
            }
        }

        // ================================================================
        // 前台窗口监控（检测跨应用切换，关闭弹窗）
        // ================================================================

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // ================================================================
        // Win32 ESC 键盘钩子（兜底机制，确保 ESC 可靠关闭弹窗）
        // ================================================================

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_ESCAPE = 0x1B;

        private static IntPtr _escHookID = IntPtr.Zero;
        private static LowLevelKeyboardProc? _escHookProc;

        /// <summary>
        /// 注册 Win32 低级键盘钩子，作为 ESC 关闭弹窗的兜底机制。
        /// 当 IOleCommandTarget 的 ESC 拦截因 SSMS 22 内部命令路由问题失效时，
        /// 此钩子在 OS 层面捕获 ESC 按键，确保弹窗可靠关闭。
        /// </summary>
        public void RegisterEscKeyboardHook()
        {
            if (_escHookID != IntPtr.Zero) return;

            try
            {
                _escHookProc = EscHookCallback;
                using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule!;
                _escHookID = SetWindowsHookEx(
                    WH_KEYBOARD_LL, _escHookProc,
                    GetModuleHandle(curModule.ModuleName!), 0);

                if (_escHookID != IntPtr.Zero)
                {
                    _logger.Information("[SqlTemplate] ✅ ESC 键盘钩子注册成功 (WH_KEYBOARD_LL)");
                }
                else
                {
                    _logger.Warning($"[SqlTemplate] ❌ ESC 键盘钩子注册失败 (Win32 Error: {Marshal.GetLastWin32Error()})");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[SqlTemplate] ESC 键盘钩子注册异常: {ex.Message}");
            }
        }

        /// <summary>
        /// ESC 键盘钩子回调。运行在 UI 线程（安装钩子的线程，通过消息循环）。
        /// 必须快速返回（≤300ms），否则 OS 会自动卸载钩子。
        /// </summary>
        private IntPtr EscHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (vkCode == VK_ESCAPE && _isPopupVisible)
                {
                    try
                    {
                        _logger.Debug("[SqlTemplate] ESC 钩子拦截: 弹窗可见，关闭弹窗");
                        _suppressAutoReopen = true;
                        CancelDebounce();
                        DismissSsmsIntelliSense();
                        // 直接在 UI 线程关闭弹窗（钩子回调运行在 UI 线程）
                        ThreadHelper.ThrowIfNotOnUIThread();
                        HidePopup("EscHookCallback");
                        _lastUserHideTime = DateTime.UtcNow;

                        // 拦截按键，不传递给 SSMS
                        return (IntPtr)1;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"[SqlTemplate] ESC 钩子处理异常: {ex.Message}");
                    }
                }
            }

            return CallNextHookEx(_escHookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// 启动前台窗口监控。弹窗显示时调用，检测用户切换到其他应用。
        /// </summary>
        private void StartFocusMonitor()
        {
            if (_focusCheckTimer == null)
            {
                _focusCheckTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                _focusCheckTimer.Tick += OnFocusCheckTick;
            }
            _focusCheckTimer.Start();
        }

        private void OnFocusCheckTick(object? sender, EventArgs e)
        {
            if (!_isPopupVisible)
            {
                _focusCheckTimer?.Stop();
                return;
            }

            try
            {
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return;

                GetWindowThreadProcessId(foreground, out uint pid);
                var currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

                if (pid != currentPid)
                {
                    _logger.Debug("[SqlTemplate] 前台窗口进程变化，关闭弹窗");
                    HidePopup();
                }
            }
            catch
            {
                // 忽略监控异常
            }
        }

        /// <summary>
        /// 布局变化时处理。
        /// 1. 滚动时隐藏弹窗
        /// 2. 文本变化（如 BACKSPACE 绕过命令过滤器）时重新评估弹窗
        /// 内部编辑和模板展开时跳过。
        /// </summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_isInternalEdit || _isExpanding) return;

            // ★ Fire-and-forget: 检测服务器切换（必须在 _isPopupVisible 检查之前）
            // 否则弹窗不可见时切换连接（如新开查询窗口连接到不同服务器），
            // OnLayoutChanged 会直接 return，DetectServerSwitchAsync 永远不会被调用
            // 节流：避免每次文本变化都调用 GetConnectionInfoAsync（COM 调用），2 秒检查一次
            if (_cacheService != null)
            {
                var nowSwitch = DateTime.UtcNow;
                if ((nowSwitch - _lastServerSwitchCheckTime).TotalMilliseconds >= ServerSwitchCheckThrottleMs)
                {
                    _lastServerSwitchCheckTime = nowSwitch;
                    _ = _cacheService.DetectServerSwitchAsync();
                }
            }

            if (!_isPopupVisible) return;
            
            if (e.VerticalTranslation || e.HorizontalTranslation)
            {
                // 滚动 — 隐藏弹窗
                HidePopup();
            }
            else
            {
                // 文本变化但非滚动 — 重新评估弹窗
                // 节流：SSMS 对单次编辑发送多次布局变化事件
                var now = DateTime.UtcNow;
                if ((now - _lastLayoutEvalTime).TotalMilliseconds < LayoutEvalThrottleMs)
                    return;
                _lastLayoutEvalTime = now;
                
                //_logger.Debug("[SqlTemplate] OnLayoutChanged: 文本变化，重新评估弹窗");
                OnCharTyped();
            }
        }

        // ================================================================
        // 弹窗显示/更新逻辑（全部同步，在 UI 线程上由命令过滤器调用）
        // ================================================================

        /// <summary>
        /// 用户输入字符后调用（US-06: 带 120ms 防抖窗口）。
        /// 
        /// 优化版核心逻辑：
        /// 1. 根据上下文关键词确定补全分类（关键词&模板 / 表&视图 / 函数&存储过程）
        /// 2. 仅搜索当前分类下的候选项（不搜索无关分类）
        /// 3. 使用前缀索引快速定位候选集（关键词 + 数据库对象双索引）
        /// 4. 短输入（≤2 字符）跳过复杂子序列匹配
        /// 5. 排序使用预计算优先级别表（避免重复判断 contextKeyword）
        /// </summary>
        internal void OnCharTyped()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // US-06: 输入防抖 —— 120ms 窗口内连续输入仅最后一次触发实际计算
            _debounceVersion++;
            var version = _debounceVersion;

            _debounceTimer?.Stop();
            _debounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer?.Stop();
                if (version == _debounceVersion)
                {
                    // US-08: TAB/ENTER/ESC 后抑制弹窗自动重开
                    // 仅拦截防抖触发的间接调用（OnLayoutChanged → OnCharTyped → debounce）
                    // 不影响 ExpandSelectedTemplate 的直接 OnCharTypedCore 调用
                    if (_suppressAutoReopen && !_isPopupVisible)
                        return;
                    OnCharTypedCore();
                }
            };
            _debounceTimer.Start();
        }

        /// <summary>
        /// 取消待执行的防抖回调（在弹窗关闭、ESC、ENTER 等需要立即响应的场景调用）。
        /// </summary>
        private void CancelDebounce()
        {
            _debounceVersion++;
            _debounceTimer?.Stop();
        }

        /// <summary>
        /// 补全计算核心逻辑（防抖后实际执行）。
        /// </summary>
        private void OnCharTypedCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 记录文本变化时间（用于焦点丢失判断）
            _lastTextChangeTime = DateTime.UtcNow;

            // ★ Fire-and-forget: 检测服务器切换（用户可能在不切换文档的情况下更改了连接）
            if (_cacheService != null)
                _ = _cacheService.DetectServerSwitchAsync();

            try
            {
                if (_activeTextView == null) return;

                // ★★★ F3: 优先检测跨库上下文（如 "DBName." 或 "DBName.dbo."）★★★
                var (crossDbName, crossSchemaName, crossObjPrefix) = DetectCrossDbContextV2(_activeTextView);
                if (crossDbName != null)
                {
                    HandleCrossDbCompletion(crossDbName, crossSchemaName, crossObjPrefix);
                    return;
                }

                // ★★★ F8: 【Bug修复-顺序提前】检测字段/别名补全必须在 GetWordBeforeCaret 之前
                // 因为 '.' 不是单词字符，GetWordBeforeCaret 返回空导致 HidePopup+return，
                // 字段补全检测永远无法执行。
                var (isFieldCompletion, fieldTableName, fieldPrefix) = DetectFieldCompletionContext(_activeTextView);
                if (isFieldCompletion && fieldTableName != null)
                {
                    HandleFieldOrAliasCompletion(_activeTextView, fieldTableName, fieldPrefix ?? "");
                    return;
                }

                // ★★★ 新增：上下文感知补全引擎 ★★★
                // 当旧的点号检测不命中时，尝试使用新的语法级上下文感知引擎
                if (TryContextAwareCompletion(_activeTextView))
                {
                    return;
                }

                var (word, wordStart, _) = GetWordBeforeCaret(_activeTextView);

                if (string.IsNullOrEmpty(word))
                {
                    // ★ 空格触发的弹窗：空单词不关闭弹窗
                    if (_isPopupVisible && _hasSpaceTrigger)
                        return;

                    // ★ 空单词时，检查光标前是否有 SQL 上下文关键词（如 ON/WHERE/AND/OR 后空格）
                    // 如果有，展示该上下文对应的关键词/模板，作为兜底
                    var caretPos = _activeTextView.Caret.Position.BufferPosition.Position;
                    var contextKeywordEmpty = GetSqlContextBeforeWord(_activeTextView, caretPos);
                    if (!string.IsNullOrEmpty(contextKeywordEmpty))
                    {
                        var emptyCategory = GetCompletionCategory(contextKeywordEmpty);
                        ShowCategoryCandidates(emptyCategory, contextKeywordEmpty);
                        return;
                    }

                    // ★ 实在无法确定上下文时，展示全部关键词+模板作为兜底
                    ShowCategoryCandidates(CompletionCategory.KeywordsAndTemplates, "");
                    return;
                }

                // 获取 SQL 上下文关键词
                var contextKeyword = GetSqlContextBeforeWord(_activeTextView, wordStart);

                // ★ 如果弹窗由空格触发，保持上次的分类，不重新计算
                CompletionCategory category;
                if (_isPopupVisible && _hasSpaceTrigger)
                {
                    category = _lastSpaceCategory;
                }
                else
                {
                    category = GetCompletionCategory(contextKeyword);
                    _hasSpaceTrigger = false;
                }

                // ★ 记录当前分类，供 ExpandSelectedTemplate 中 DB 选中后使用
                _lastCompletionCategory = category;

                // 统一候选项列表
                var allMatches = new List<CompletionCandidate>();

                // ★ 关键词 + 模板：所有场景都需要（搜索量小）
                // 使用前缀索引快速定位候选关键词
                var lowerWord = word.ToLowerInvariant();
                var candidateKeywords = lowerWord.Length <= 3
                    ? (_keywordPrefixIndex.TryGetValue(lowerWord, out var kwList) ? kwList : new List<string>())
                    : SqlKeywords.Where(k => k.StartsWith(lowerWord, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var keyword in candidateKeywords)
                {
                    var score = CompletionCandidate.FuzzyMatch(word, keyword);
                    if (score >= 0)
                    {
                        allMatches.Add(new CompletionCandidate
                        {
                            DisplayText = keyword,
                            FilterText = keyword,
                            TypeTag = "关键字",
                            InsertText = keyword,
                            ItemType = CompletionItemType.Keyword,
                            MatchScore = score
                        });
                    }
                }

                // ★ 模板搜索（使用缓存，避免重复排序）
                if (_templatesDirty)
                {
                    _cachedTemplates = _templateService.GetAllTemplates();
                    _templatesDirty = false;
                }
                foreach (var t in _cachedTemplates)
                {
                    if (!string.IsNullOrEmpty(t.Abbreviation))
                    {
                        var score = CompletionCandidate.FuzzyMatch(word, t.Abbreviation!);
                        if (score >= 0)
                        {
                            allMatches.Add(new CompletionCandidate
                            {
                                DisplayText = t.Abbreviation ?? "",
                                FilterText = t.Abbreviation ?? "",
                                TypeTag = "模板",
                                InsertText = t.Expansion,
                                PreviewText = t.Expansion,
                                ItemType = CompletionItemType.Template,
                                Source = t,
                                MatchScore = score
                            });
                        }
                    }
                }

                // ★ 常用组合 SQL 片段（prefix 匹配，不通过 FuzzyMatch 避免 sf→OFFSET 误匹配）
                foreach (var (display, insert) in CompoundSqlSnippets)
                {
                    if (string.IsNullOrEmpty(word) || 
                        display.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    {
                        allMatches.Add(new CompletionCandidate
                        {
                            DisplayText = display,
                            FilterText = display,
                            InsertText = insert,
                            TypeTag = "模板",
                            ItemType = CompletionItemType.Template,
                            MatchScore = word.Equals(display, StringComparison.OrdinalIgnoreCase) ? 90 : 50,
                        });
                    }
                }

                // ★ 数据库对象：根据分类选择性搜索 + 跨库支持
                if (_cacheService != null)
                {
                    // 注意：跨库上下文已在 OnCharTyped 开头通过 DetectCrossDbContextV2 处理
                    // 此处仅处理普通模式（非跨库）

                    if (category == CompletionCategory.TablesAndViews)
                    {
                        var dbMatches = _cacheService.GetMatchingObjects(word);
                        var filtered = dbMatches.Where(m =>
                            m.TypeCode == "U" || m.TypeCode == "V").ToList();
                        allMatches.AddRange(filtered);
                    }
                    else if (category == CompletionCategory.ProceduresAndFunctions)
                    {
                        var dbMatches = _cacheService.GetMatchingObjects(word);
                        var filtered = dbMatches.Where(m =>
                            m.TypeCode == "P" || m.TypeCode == "FN" ||
                            m.TypeCode == "IF" || m.TypeCode == "TF").ToList();
                        allMatches.AddRange(filtered);
                    }

                    // ★ F4: 添加其他数据库的名称作为候选（去重：完整匹配DB名时不显示）
                    AddDbNameCandidates(word, allMatches);
                }

                if (allMatches.Count == 0)
                {
                    HidePopup();
                    return;
                }

                // ★ 排序优化：预计算优先级别表（避免重复判断 contextKeyword）
                Func<CompletionCandidate, int> priorityFunc = category switch
                {
                    CompletionCategory.ProceduresAndFunctions => m =>
                        m.TypeCode is "P" or "FN" or "IF" or "TF" ? 0
                        : m.ItemType == CompletionItemType.Template ? 1
                        : m.ItemType == CompletionItemType.Keyword ? 2 : 3,
                    CompletionCategory.TablesAndViews => m =>
                        m.TypeCode is "U" or "V" ? 0
                        : m.ItemType == CompletionItemType.Template ? 1
                        : m.ItemType == CompletionItemType.Keyword ? 2 : 3,
                    _ => m =>
                        m.ItemType == CompletionItemType.Template ? 0
                        : m.ItemType == CompletionItemType.Keyword ? 1 : 2,
                };

                allMatches = allMatches
                    .OrderByDescending(m => m.MatchScore)
                    .ThenBy(priorityFunc)
                    .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ShowOrUpdatePopup(allMatches, word);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] OnCharTyped 失败");
            }
        }

        /// <summary>
        /// 获取光标前的单词及其位置。
        /// </summary>
        private (string word, int start, int end) GetWordBeforeCaret(IWpfTextView textView)
        {
            var caret = textView.Caret;
            var position = caret.Position.BufferPosition;
            var line = position.GetContainingLine();
            var lineText = line.Extent.GetText();
            var caretColumn = position.Position - line.Start.Position;

            if (caretColumn <= 0) return ("", 0, 0);

            var wordStart = caretColumn;
            while (wordStart > 0 && IsWordChar(lineText[wordStart - 1]))
            {
                wordStart--;
            }

            if (wordStart == caretColumn) return ("", 0, 0);

            var word = lineText.Substring(wordStart, caretColumn - wordStart);
            return (word, line.Start.Position + wordStart, line.Start.Position + caretColumn);
        }

        /// <summary>
        /// 获取当前单词前的 SQL 上下文关键词（如 EXEC、SELECT 等）。
        /// 用于智能过滤补全对象类型。
        /// </summary>
        private string GetSqlContextBeforeWord(IWpfTextView textView, int wordStart)
        {
            try
            {
                var snapshot = textView.TextBuffer.CurrentSnapshot;
                var line = snapshot.GetLineFromPosition(wordStart);
                var lineText = line.Extent.GetText();
                var wordStartColumn = wordStart - line.Start.Position;

                // 从当前单词开始位置向前跳过空白
                int pos = wordStartColumn;
                while (pos > 0 && char.IsWhiteSpace(lineText[pos - 1]))
                    pos--;

                if (pos <= 0) return "";

                // 向前读取上一个单词
                int wordEnd = pos;
                while (pos > 0 && IsWordChar(lineText[pos - 1]))
                    pos--;

                if (pos == wordEnd) return "";

                return lineText.Substring(pos, wordEnd - pos).ToUpperInvariant();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 根据上下文关键词确定补全分类。
        /// 决定弹窗是否只显示特定类型的候选项。
        /// </summary>
        private static CompletionCategory GetCompletionCategory(string contextKeyword)
        {
            return contextKeyword switch
            {
                "EXEC" or "EXECUTE" or "SP_EXECUTESQL" => CompletionCategory.ProceduresAndFunctions,
                "SELECT" or "FROM" or "JOIN" or "INNER" or "LEFT" or "RIGHT" or "OUTER" or "CROSS" or "FULL"
                    or "INSERT" or "UPDATE" or "DELETE" or "INTO" or "MERGE"
                    or "TRUNCATE" or "ALTER" or "DROP" or "TABLE" => CompletionCategory.TablesAndViews,
                _ => CompletionCategory.KeywordsAndTemplates,
            };
        }

        // ================================================================
        // 跨库检测与 DB 名称候选
        // ================================================================

        /// <summary>
        /// 检测光标前是否有跨库上下文（如 "EXEC OtherDB." 或 "FROM [OtherDB]."）。
        /// 通过检查光标前是否为 "DBName." 模式，且 DBName 在已知数据库列表中。
        /// </summary>
        private string? DetectCrossDbContext(IWpfTextView textView, int wordStart)
        {
            try
            {
                var snapshot = textView.TextBuffer.CurrentSnapshot;
                var line = snapshot.GetLineFromPosition(wordStart);
                var lineText = line.Extent.GetText();
                var startColumn = wordStart - line.Start.Position;

                if (startColumn <= 0) return null;

                // 从 wordStart（当前单词开始位置）向前搜索
                // 寻找 "[DBName]." 或 "DBName." 模式
                // 跳过可能的 "Schema." 部分（如 "dbo."）

                // 第一步：找到 wordStart 前最后一个 '.' 或 ']'
                int pos = startColumn - 1;

                // 跳过当前单词前的点号和 schema 名（如 ".dbo."）
                // 允许的模式：DBName.dbo.xxx 或 [DBName].dbo.xxx
                // 我们只需要提取 DBName 部分

                // 从 wordStart 向前查找第一个 '.'
                int dotPos = -1;
                for (int i = startColumn - 1; i >= 0; i--)
                {
                    if (lineText[i] == '.')
                    {
                        dotPos = i;
                        break;
                    }
                }

                if (dotPos < 0) return null;

                // 在 dotPos 前查找数据库名称
                int nameEnd = dotPos;

                // 处理 [DBName] 模式
                if (nameEnd > 0 && lineText[nameEnd - 1] == ']')
                {
                    // 查找匹配的 '['
                    int bracketStart = nameEnd - 2; // 跳过 ']'
                    while (bracketStart >= 0 && lineText[bracketStart] != '[')
                        bracketStart--;

                    if (bracketStart >= 0 && bracketStart < nameEnd - 1)
                    {
                        var dbName = lineText.Substring(bracketStart + 1, nameEnd - bracketStart - 2);
                        if (IsKnownDatabase(dbName))
                            return dbName;
                    }
                    return null;
                }

                // 处理普通 DBName 模式（无方括号）
                int nameStart = nameEnd;
                while (nameStart > 0 && IsWordChar(lineText[nameStart - 1]))
                    nameStart--;

                if (nameStart < nameEnd)
                {
                    var dbName = lineText.Substring(nameStart, nameEnd - nameStart);
                    if (IsKnownDatabase(dbName))
                        return dbName;
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 检查名称是否为已知的数据库。
        /// 1. 优先检查 _allDatabaseNames（从 sys.databases 获取的完整列表）
        /// 2. 降级检查缓存 keys（即使 prefetch 的 DB 名称查询尚未完成）
        /// 3. 如果缓存未就绪，短暂轮询等待（300ms 内）
        /// </summary>
        private bool IsKnownDatabase(string name)
        {
            if (_cacheService == null) return false;

            // 快速检查（第一次）
            if (QuickCheckDatabase(name))
                return true;

            // ★ 轮询等待：缓存可能正在后台异步加载中
            // SSMS 启动后立即输入时，_allDatabaseNames 可能尚未就绪
            for (int i = 0; i < 6; i++)
            {
                System.Threading.Thread.Sleep(50);
                if (QuickCheckDatabase(name))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 快速检查数据库名称是否已知（不等待）。
        /// </summary>
        private bool QuickCheckDatabase(string name)
        {
            if (_cacheService == null) return false;

            var knownDbs = _cacheService.GetAllDatabaseNames();
            if (knownDbs.Any(db => string.Equals(db, name, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 降级：检查缓存 keys（prefetch 可能已加载对象但 _allDatabaseNames 未就绪）
            foreach (var key in _cacheService.GetCachedDatabaseKeys())
            {
                var parts = key.Split('.');
                if (parts.Length >= 2 &&
                    string.Equals(parts[parts.Length - 1], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 跨库模式下，从 DB 名称之前查找上下文关键词。
        /// 支持两种格式：
        /// - "EXEC [MESDATA].dbo." → 找到 '[' 前的 "EXEC"
        /// - "EXEC MESDATA.dbo."   → 找到 'MESDATA' 前的 "EXEC"
        /// </summary>
        private string GetContextBeforeDbName(IWpfTextView textView, int caretPos)
        {
            try
            {
                var snapshot = textView.TextBuffer.CurrentSnapshot;
                var line = snapshot.GetLineFromPosition(caretPos);
                var lineText = line.Extent.GetText();
                var column = caretPos - line.Start.Position;

                if (column <= 0) return "";

                int searchStartPos = -1;

                // 策略1：查找方括号 DB 名称 [DBName].
                for (int i = column - 1; i >= 0; i--)
                {
                    if (lineText[i] == '[')
                    {
                        searchStartPos = i;
                        break;
                    }
                    // 遇到空格就停止向前搜索
                    if (char.IsWhiteSpace(lineText[i]))
                        break;
                }

                // 策略2：非方括号 DB 名称 DBName.Schema.
                if (searchStartPos < 0)
                {
                    // 从光标向前查找 "DBName." 模式
                    // 跳过当前正在输入的 Schema 名称（如 "dbo"）
                    int dotPos = -1;
                    for (int i = column - 1; i >= 0; i--)
                    {
                        if (lineText[i] == '.')
                        {
                            dotPos = i;
                            break;
                        }
                        if (char.IsWhiteSpace(lineText[i]))
                            break;
                    }

                    if (dotPos > 0)
                    {
                        // dotPos 之前应该是 DBName
                        int dbNameEnd = dotPos;
                        int dbNameStart = dbNameEnd - 1;
                        while (dbNameStart >= 0 && IsWordChar(lineText[dbNameStart]))
                            dbNameStart--;
                        dbNameStart++; // 回到 DBName 的第一个字符

                        if (dbNameStart < dbNameEnd)
                        {
                            searchStartPos = dbNameStart;
                        }
                    }
                }

                if (searchStartPos < 0) return "";

                // 从 DB 名称之前查找上下文关键词
                int kwPos = searchStartPos - 1;
                // 跳过空白
                while (kwPos >= 0 && char.IsWhiteSpace(lineText[kwPos]))
                    kwPos--;

                if (kwPos < 0) return "";

                // 读取前一个单词
                int kwEnd = kwPos + 1;
                while (kwPos >= 0 && IsWordChar(lineText[kwPos]))
                    kwPos--;

                if (kwPos + 1 >= kwEnd) return "";

                return lineText.Substring(kwPos + 1, kwEnd - kwPos - 1).ToUpperInvariant();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 从光标位置向前搜索，找到最近的 SQL 上下文关键词（EXEC/FROM/JOIN 等）。
        /// 支持多段跳过：EXEC [MESDATA].dbo. 或 EXEC MESDATA.dbo. 都能正确找到 EXEC。
        /// </summary>
        private string GetSqlKeywordBeforePosition(IWpfTextView textView, int caretPos)
        {
            try
            {
                var snapshot = textView.TextBuffer.CurrentSnapshot;
                var line = snapshot.GetLineFromPosition(caretPos);
                var lineText = line.Extent.GetText();
                var column = caretPos - line.Start.Position;

                if (column <= 0) return "";

                int pos = column - 1;

                // 跳过末尾的点号和空白
                while (pos >= 0 && (char.IsWhiteSpace(lineText[pos]) || lineText[pos] == '.'))
                    pos--;

                // ★ 循环跳过所有 "word." 段（如 dbo. MESDATA. [MESDATA]. 等）
                while (pos >= 0)
                {
                    // 跳过 word 字符（如 dbo, MESDATA）
                    while (pos >= 0 && IsWordChar(lineText[pos])) pos--;

                    // 处理方括号 [DBName]
                    if (pos >= 0 && lineText[pos] == ']')
                    {
                        while (pos >= 0 && lineText[pos] != '[') pos--;
                        if (pos > 0) pos--;
                    }

                    // 跳过 word 和 '.' 之间的空白
                    while (pos >= 0 && char.IsWhiteSpace(lineText[pos])) pos--;

                    // 如果遇到 '.'，说明前面还有一个段，跳过 '.' 继续循环
                    if (pos >= 0 && lineText[pos] == '.')
                    {
                        pos--;
                        // 跳过 '.' 前的空白
                        while (pos >= 0 && char.IsWhiteSpace(lineText[pos])) pos--;
                        continue;
                    }

                    // 不是 '.'，说明已经到了关键词前的空白
                    break;
                }

                // 跳过关键词前的空白
                while (pos >= 0 && char.IsWhiteSpace(lineText[pos])) pos--;

                if (pos < 0) return "";

                // 读取关键词
                int kwEnd = pos + 1;
                while (pos >= 0 && IsWordChar(lineText[pos])) pos--;

                if (kwEnd <= pos + 1) return "";

                return lineText.Substring(pos + 1, kwEnd - pos - 1).ToUpperInvariant();
            }
            catch { return ""; }
        }

        // ================================================================
        // F3: 增强版跨库上下文检测（支持 DBName.Schema.Object 三级模式）
        // ================================================================

        /// <summary>
        /// 增强版跨库上下文检测。
        /// 支持三种模式：
        ///   1. DBName.ObjPrefix → dbName="MESDATA", schemaName=null, objectPrefix="Use"
        ///   2. DBName.Schema.ObjPrefix → dbName="MESDATA", schemaName="dbo", objectPrefix="Use"
        ///   3. Schema.ObjPrefix → dbName=当前DB, schemaName="dbo", objectPrefix="Use"
        /// </summary>
        private (string? dbName, string? schemaName, string objectPrefix) DetectCrossDbContextV2(IWpfTextView textView)
        {
            try
            {
                var caret = textView.Caret.Position.BufferPosition;
                var line = caret.GetContainingLine();
                var lineText = line.Extent.GetText();
                var column = caret.Position - line.Start.Position;

                if (column <= 0) return (null, null, "");

                int pos = column - 1;

                // Step 1: 跳过当前单词（光标前的部分）
                int wordEnd = pos;
                while (pos >= 0 && IsWordChar(lineText[pos])) pos--;
                string currentWord = lineText.Substring(pos + 1, wordEnd - pos);

                // Step 2: 检查点号前是否有内容
                if (pos >= 0 && lineText[pos] == '.')
                {
                    int firstDot = pos;

                    // Step 3: 读取点号前的单词
                    pos--;
                    int nameEnd = pos;

                    // ★ Bug 修复：检查点号前是否是 ']'（如 [MESDATA].dbo. 中的 dbo 前的 ']'）
                    if (pos >= 0 && lineText[pos] == ']')
                    {
                        // 点号前是 ']'，说明是 [DBName]. 模式
                        return ExtractDbNameFromBracket(lineText, pos, firstDot, currentWord);
                    }

                    while (pos >= 0 && IsWordChar(lineText[pos])) pos--;
                    string nameBeforeDot = lineText.Substring(pos + 1, nameEnd - pos);

                    // 检查 [DBName] 模式（点号前直接是 ']'）
                    if (pos >= 0 && lineText[pos] == ']')
                    {
                        return ExtractDbNameFromBracket(lineText, pos, firstDot, currentWord);
                    }

                    // Step 4: 检查是否是 DBName.Schema 模式（有两个点号）
                    if (pos >= 0 && lineText[pos] == '.')
                    {
                        int secondDot = pos;
                        pos--;

                        // ★ Bug 修复：检查 DBName 是否被方括号包裹（如 [MESDATA].dbo. ）
                        if (pos >= 0 && lineText[pos] == ']')
                        {
                            // 从 ']' 向前找到 '['
                            int bracketStart = pos - 1;
                            while (bracketStart >= 0 && lineText[bracketStart] != '[')
                                bracketStart--;

                            if (bracketStart >= 0)
                            {
                                var dbName = lineText.Substring(bracketStart + 1, pos - bracketStart - 1);
                                if (IsKnownDatabase(dbName))
                                {
                                    // 模式: [DBName].Schema.ObjectPrefix
                                    return (dbName, nameBeforeDot, currentWord);
                                }
                            }
                        }

                        // 普通模式：DBName.Schema.ObjectPrefix（无方括号）
                        int dbNameEnd = pos;
                        while (pos >= 0 && IsWordChar(lineText[pos])) pos--;
                        string dbNamePlain = lineText.Substring(pos + 1, dbNameEnd - pos);

                        if (IsKnownDatabase(dbNamePlain))
                        {
                            // 模式: DBName.Schema.ObjectPrefix
                            return (dbNamePlain, nameBeforeDot, currentWord);
                        }
                    }

                    // Step 5: 检查是否是 DBName.Object 模式（一个点号）
                    if (IsKnownDatabase(nameBeforeDot))
                    {
                        // 模式: DBName.ObjectPrefix
                        return (nameBeforeDot, null, currentWord);
                    }

                    // Step 6: 检查是否是 Schema.Object 模式（无跨库）
                    if (IsKnownSchema(nameBeforeDot))
                    {
                        // 模式: Schema.ObjectPrefix，在当前 DB 中搜索
                        return (_currentDatabase, nameBeforeDot, currentWord);
                    }
                }

                return (null, null, "");
            }
            catch { return (null, null, ""); }
        }

        /// <summary>
        /// 从 [DBName]. 模式中提取数据库名。
        /// </summary>
        private (string? dbName, string? schemaName, string objectPrefix) ExtractDbNameFromBracket(
            string lineText, int bracketPos, int dotPos, string currentWord)
        {
            // bracketPos 指向 ']' 的位置
            int start = bracketPos - 1;
            while (start >= 0 && lineText[start] != '[')
                start--;

            if (start < 0) return (null, null, currentWord);

            var dbName = lineText.Substring(start + 1, bracketPos - start - 1);
            if (IsKnownDatabase(dbName))
            {
                return (dbName, null, currentWord);
            }
            return (null, null, currentWord);
        }

        /// <summary>
        /// 检查名称是否为当前缓存中的已知 Schema。
        /// </summary>
        private bool IsKnownSchema(string name)
        {
            // 简化判断：常用 Schema 名直接匹配
            return name.Equals("dbo", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("guest", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 当前数据库名称（用于 Schema 模式检测）。
        /// </summary>
        private string? _currentDatabase;

        /// <summary>
        /// 当前服务器名称（用于 Schema 模式检测）。
        /// </summary>
        private string? _currentServer;

        // ================================================================
        // F5: 跨库补全分支处理（Schema/对象）
        // ================================================================

        /// <summary>
        /// 处理跨库补全：用户已输入 "DBName." 或 "DBName.Schema."，后续输入在此 DB 中搜索。
        /// </summary>
        private void HandleCrossDbCompletion(string dbName, string? schemaName, string objectPrefix)
        {
            if (_cacheService == null) return;

            // 懒惰加载目标 DB 的缓存
            _ = _cacheService.EnsureDatabaseLoadedAsync(dbName);

            if (schemaName != null)
            {
                // 已指定 Schema → 搜索该 Schema 下的对象
                var caretPos = _activeTextView!.Caret.Position.BufferPosition.Position;
                var contextKeyword = GetContextBeforeDbName(_activeTextView, caretPos);
                // ★ Bug 修复：如果 GetContextBeforeDbName 返回空（非方括号格式），尝试备用方法
                if (string.IsNullOrEmpty(contextKeyword))
                {
                    contextKeyword = GetSqlKeywordBeforePosition(_activeTextView, caretPos);
                }
                var category = GetCompletionCategory(contextKeyword);
                _logger.Information($"[SqlTemplate] HandleCrossDbCompletion: db='{dbName}', schema='{schemaName}', prefix='{objectPrefix}', contextKeyword='{contextKeyword}', category={category}");

                var matches = _cacheService.GetMatchingObjectsForDbAndSchema(dbName, schemaName, objectPrefix, category.ToString());
                if (matches.Count > 0)
                    ShowOrUpdatePopup(matches, objectPrefix);
                else
                    HidePopup();
            }
            else
            {
                // ★ Bug 修复：未指定 Schema 时，始终显示 Schema 候选列表
                // 无论 EXEC/FROM 上下文，都先让用户选择 Schema，再展示对象
                var schemaMatches = _cacheService.GetSchemaCandidatesForDb(dbName, objectPrefix);
                if (schemaMatches.Count > 0)
                    ShowOrUpdatePopup(schemaMatches, objectPrefix);
                else
                    HidePopup();
            }
        }

        // ================================================================
        // F8: 字段/别名补全检测与处理
        // ================================================================

        /// <summary>
        /// ★ 新增：上下文感知补全引擎入口。
        /// 使用 SqlBreaker + SyntaxLocator + CompletionDispatcher 实现语法级上下文感知。
        /// 当旧的点号检测不命中时调用，作为新的补全路径。
        /// 返回 true 表示已处理（弹窗已展示或已隐藏），false 表示未命中（回退到旧逻辑）。
        /// </summary>
        private bool TryContextAwareCompletion(IWpfTextView textView)
        {
            if (_completionDispatcher == null || _cacheService == null)
                return false;

            try
            {
                var snapshot = textView.TextBuffer.CurrentSnapshot;
                var caretPos = textView.Caret.Position.BufferPosition.Position;
                var caretLine = snapshot.GetLineFromPosition(caretPos);
                int caretLineNumber = caretLine.LineNumber;

                // 获取所有行文本
                int lineCount = snapshot.LineCount;
                var lines = new string[lineCount];
                for (int i = 0; i < lineCount; i++)
                {
                    lines[i] = snapshot.GetLineFromLineNumber(i).GetText();
                }

                // Step 1: 断句
                var (startLine, endLine, statementText, stmtType) = _sqlBreaker.LocateCurrentStatement(lines, caretLineNumber);
                if (string.IsNullOrWhiteSpace(statementText))
                    return false;

                // 计算光标在语句文本中的偏移
                int lineStartOffset = snapshot.GetLineFromLineNumber(startLine).Start.Position;
                int cursorOffset = caretPos - lineStartOffset;

                // Step 2: 语法定位
                var locationResult = _syntaxLocator.Locate(statementText, cursorOffset, stmtType);

                // 注释/字符串内 → 不提示，让旧逻辑处理（返回 false 会让旧逻辑 HidePopup）
                if (locationResult.Node == SyntaxNode.InCommentOrString)
                    return false;

                // 获取用户已输入的过滤文本
                string filterText = GetFilterTextAtCursor(textView, locationResult);

                // Step 3: 分发获取候选
                string connectedDb = _cacheService.GetCurrentDatabase() ?? "";
                var candidates = _completionDispatcher.GetCandidates(
                    locationResult.Node, statementText, filterText, locationResult, connectedDb);

                // ★ US-03: 合并展示组合 SQL 片段（除点号/跨库/注释上下文外）
                // 让上下文感知补全的结果中也包含 OFFSET/ORDER BY 等组合片段
                if (locationResult.Node != SyntaxNode.AfterDotNotation &&
                    locationResult.Node != SyntaxNode.CrossDbContextPrompt &&
                    locationResult.Node != SyntaxNode.InCommentOrString)
                {
                    foreach (var (display, insert) in CompoundSqlSnippets)
                    {
                        if (string.IsNullOrEmpty(filterText) ||
                            display.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(new CompletionCandidate
                            {
                                DisplayText = display,
                                FilterText = display,
                                InsertText = insert,
                                TypeTag = "模板",
                                ItemType = CompletionItemType.Template,
                                MatchScore = string.IsNullOrEmpty(filterText) ? -2 : 50,
                            });
                        }
                    }

                    // ★ 问题修复：字段上下文（如 SelectColumnArea）中合并匹配的模板缩写
                    // 否则输入 "sf" 时只显示字段候选（SQ 前缀字段），模板 sf→SELECT TOP 100 * FROM 不会出现
                    // 模板 Abbreviation 采用前缀匹配（sf 匹配 sf/ssf/sfu，避免 FuzzyMatch 的误匹配）
                    if (_templatesDirty)
                    {
                        _cachedTemplates = _templateService.GetAllTemplates();
                        _templatesDirty = false;
                    }
                    foreach (var t in _cachedTemplates)
                    {
                        if (string.IsNullOrEmpty(t.Abbreviation)) continue;
                        if (string.IsNullOrEmpty(filterText) ||
                            t.Abbreviation!.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(new CompletionCandidate
                            {
                                DisplayText = t.Abbreviation ?? "",
                                FilterText = t.Abbreviation ?? "",
                                TypeTag = "模板",
                                InsertText = t.Expansion,
                                PreviewText = t.Expansion,
                                ItemType = CompletionItemType.Template,
                                Source = t,
                                MatchScore = string.Equals(t.Abbreviation, filterText, StringComparison.OrdinalIgnoreCase)
                                    ? 80   // 精确匹配缩写 → 最高优先
                                    : (string.IsNullOrEmpty(filterText) ? -3 : 60),
                            });
                        }
                    }
                }

                if (candidates.Count > 0)
                {
                    ShowOrUpdatePopup(candidates, filterText);
                    return true;
                }

                // 候选为空：让旧系统兜底（展示关键词+模板）
                // 不再拦截 HidePopup+return true，否则旧系统永远无法展示兜底内容
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] TryContextAwareCompletion 异常");
                return false;
            }
        }

        /// <summary>
        /// 获取光标位置当前输入的过滤文本。
        /// </summary>
        private string GetFilterTextAtCursor(IWpfTextView textView, SyntaxLocationResult location)
        {
            if (location.Node == SyntaxNode.AfterDotNotation ||
                location.Node == SyntaxNode.CrossDbContextPrompt)
            {
                // 点号后：取点号后到光标的文本
                return "";
            }

            // 普通：取当前单词
            var (word, _, _) = GetWordBeforeCaret(textView);
            return word ?? "";
        }

        /// <summary>
        /// 判断语法节点是否是"字段上下文"（命中后不回退到关键词模板）。
        /// </summary>
        private bool IsFieldContextNode(SyntaxNode node)
        {
            switch (node)
            {
                case SyntaxNode.SelectColumnArea:
                case SyntaxNode.AfterWhereKeyword:
                case SyntaxNode.AfterAndOrKeyword:
                case SyntaxNode.SearchConditionPosition:
                case SyntaxNode.AfterJoinOnKeyword:
                case SyntaxNode.AfterHavingKeyword:
                case SyntaxNode.UpdateSetPosition:
                case SyntaxNode.InsertColumnListPosition:
                case SyntaxNode.AfterDotNotation:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 检测光标前是否是 "TableName.<prefix>" 或 "Alias.<prefix>" 模式（字段补全上下文）。
        /// prefix 为 '.' 之后到光标位置的字符（可能为空）。
        /// </summary>
        private (bool isFieldCompletion, string? tableName, string? prefix) DetectFieldCompletionContext(IWpfTextView textView)
        {
            try
            {
                var caret = textView.Caret.Position.BufferPosition;
                var line = caret.GetContainingLine();
                var lineText = line.Extent.GetText();
                var column = caret.Position - line.Start.Position;

                if (column <= 1) return (false, null, null);

                // 从光标位置向前找最近的 '.'
                int dotPos = -1;
                for (int i = column - 1; i >= 0; i--)
                {
                    if (lineText[i] == '.')
                    {
                        dotPos = i;
                        break;
                    }
                }

                if (dotPos < 0) return (false, null, null);

                // '.' 之后到光标位置的内容作为 prefix
                string prefix = lineText.Substring(dotPos + 1, column - dotPos - 1);

                // ★ 关键校验：prefix 中如果包含空格、=、<> 等非单词字符，
                //   说明用户已经输入完字段名正在写条件，不是字段补全触发
                if (!string.IsNullOrEmpty(prefix) && !Regex.IsMatch(prefix, @"^[\w\[\].]*$"))
                {
                    _logger.Debug($"[SqlTemplate] DetectField: prefix 含非单词字符 '{prefix}'，跳过字段补全触发");
                    return (false, null, null);
                }

                // 找到 '.' 前的单词（支持 [TableName] 或 [Schema].[TableName] 模式）
                int pos = dotPos - 1;

                // ★ 处理括号模式: [TableName].prefix 或 [Schema].[TableName].prefix
                if (pos >= 0 && lineText[pos] == ']')
                {
                    _logger.Debug($"[SqlTemplate] DetectField: 检测到括号模式, prefix='{prefix}'");
                    return ExtractTableNameFromBracket(lineText, pos, prefix);
                }

                int wordEnd = pos;
                while (pos >= 0 && IsWordChar(lineText[pos])) pos--;

                if (pos == wordEnd) return (false, null, null);

                var wordBeforeDot = lineText.Substring(pos + 1, wordEnd - pos);

                // 检查1：是否是表名
                if (_cacheService != null)
                {
                    var tableName = _cacheService.ResolveTableName(wordBeforeDot);
                    if (tableName != null)
                    {
                        _logger.Debug($"[SqlTemplate] DetectField: '{wordBeforeDot}' 解析为表名 '{tableName}', prefix='{prefix}'");
                        return (true, tableName, prefix);
                    }
                }

                // 检查2：是否是别名
                var aliasMap = ResolveAliasMap(textView);
                _logger.Debug($"[SqlTemplate] DetectField: wordBeforeDot='{wordBeforeDot}', aliasMap.Count={aliasMap.Count}, keys=[{string.Join(", ", aliasMap.Keys)}]");
                if (aliasMap.TryGetValue(wordBeforeDot, out var resolvedTable))
                {
                    _logger.Debug($"[SqlTemplate] DetectField: 别名 '{wordBeforeDot}' → '{resolvedTable}', prefix='{prefix}'");
                    return (true, resolvedTable, prefix);
                }

                _logger.Debug($"[SqlTemplate] DetectField: '{wordBeforeDot}' 未匹配到表名或别名");
                return (false, null, null);
            }
            catch (Exception ex) { _logger.Error(ex, "[SqlTemplate] DetectFieldCompletionContext 异常"); return (false, null, null); }
        }

        /// <summary>
        /// 从方括号模式中提取表名（支持 [TableName] 或 [Schema].[TableName] 模式）。
        /// bracketEnd 指向 ']' 的位置。
        /// </summary>
        private (bool isFieldCompletion, string? tableName, string? prefix) ExtractTableNameFromBracket(
            string lineText, int bracketEnd, string prefix)
        {
            // 从 ']' 向前找到 '['
            int start = bracketEnd - 1;
            while (start >= 0 && lineText[start] != '[')
                start--;

            if (start < 0) return (false, null, null);

            var nameInBrackets = lineText.Substring(start + 1, bracketEnd - start - 1);

            // 检查是否是已知表名
            if (_cacheService != null)
            {
                var tableName = _cacheService.ResolveTableName(nameInBrackets);
                if (tableName != null)
                    return (true, tableName, prefix);
            }

            return (false, null, null);
        }

        /// <summary>
        /// 处理字段/别名补全。
        /// </summary>
        private void HandleFieldOrAliasCompletion(IWpfTextView textView, string tableName, string prefix)
        {
            if (_cacheService == null) return;

            _logger.Debug($"[SqlTemplate] HandleField: tableName='{tableName}', prefix='{prefix}'");
            var columns = _cacheService.GetTableColumns(tableName, prefix);
            if (columns == null || columns.Count == 0)
            {
                _logger.Debug($"[SqlTemplate] HandleField: GetTableColumns 返回 null 或空, tableName='{tableName}'");
                // ★ Bug 修复：字段补全失败时隐藏弹窗，避免残留上一次的关键词弹窗
                HidePopup();
                return;
            }

            _logger.Debug($"[SqlTemplate] HandleField: 找到 {columns.Count} 列");

            var allMatches = columns.Select(c => new CompletionCandidate
            {
                DisplayText = c.Name,
                FilterText = c.Name,
                TypeTag = "字段",
                InsertText = c.Name,
                ItemType = CompletionItemType.Field,
                MatchScore = string.IsNullOrEmpty(prefix) ? 0
                    : CompletionCandidate.FuzzyMatch(prefix, c.Name),
            }).ToList();

            // 排序：精确匹配 > 前缀匹配 > 子序列匹配
            allMatches = allMatches
                .OrderByDescending(m => m.MatchScore)
                .ThenBy(m => m.FilterText, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ShowOrUpdatePopup(allMatches, prefix);
        }

        /// <summary>
        /// 解析 SQL 中的别名映射表。
        /// 从当前语句中找出 "FROM TableName Alias" 或 "JOIN TableName Alias" 模式。
        /// 返回 Dictionary&lt;别名, 表名&gt;
        /// </summary>
        private Dictionary<string, string> ResolveAliasMap(IWpfTextView textView)
        {
            var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var snapshot = textView.TextBuffer.CurrentSnapshot;
                var caretLine = snapshot.GetLineFromPosition(textView.Caret.Position.BufferPosition.Position);

                for (int i = 0; i <= caretLine.LineNumber; i++)
                {
                    var lineText = snapshot.GetLineFromLineNumber(i).GetText();

                    // ★ Bug 修复：支持以下表名格式：
                    //   1. [DB].[Schema].[Table] alias
                    //   2. [Schema].[Table] alias
                    //   3. [Table] alias
                    //   4. DB.Schema.Table alias
                    //   5. Schema.Table alias
                    //   6. Table alias
                    var matches = System.Text.RegularExpressions.Regex.Matches(
                        lineText,
                        @"\b(?:FROM|JOIN)\s+((?:\[?\w+\]?\.)*)\[?(\w+)\]?(?:\s+AS)?\s+(\w+)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string prefix = match.Groups[1].Value; // e.g. "[MESDATA].[dbo]." or "dbo." or ""
                        string tableName = match.Groups[2].Value; // e.g. "HONEY_832_MM_HEADER"
                        string alias = match.Groups[3].Value;

                        // 组合完整表名
                        string fullTableName = prefix + tableName;
                        // 去除方括号
                        fullTableName = fullTableName.Replace("[", "").Replace("]", "");

                        // 确保至少有 schema.table 格式
                        if (!fullTableName.Contains('.'))
                            fullTableName = "dbo." + fullTableName;

                        aliasMap[alias] = fullTableName;
                    }
                }
            }
            catch { }
            return aliasMap;
        }

        /// <summary>
        /// 添加其他数据库的名称作为补全候选。
        /// 仅当输入字符匹配 DB 名称前缀时添加，避免过多无关候选。
        /// ★ F4: 如果输入的 word 与某个 DB 名称完全一致，跳过该 DB 名称候选（去重）。
        /// </summary>
        private void AddDbNameCandidates(string word, List<CompletionCandidate> allMatches)
        {
            if (_cacheService == null || string.IsNullOrEmpty(word)) return;

            var dbCandidates = _cacheService.GetDatabaseCandidates();
            foreach (var dbCandidate in dbCandidates)
            {
                var score = CompletionCandidate.FuzzyMatch(word, dbCandidate.FilterText);
                if (score >= 0)
                {
                    dbCandidate.MatchScore = score;
                    dbCandidate.ItemType = CompletionItemType.Database;
                    allMatches.Add(dbCandidate);
                }
            }
        }

        /// <summary>
        /// 显示或更新提示框。
        /// 
        /// 优化版：
        /// - 匹配列表没有变化时，跳过 WPF UI 重建（仅更新选中项）
        /// - 只有在列表变化时才重建 ListBox 项
        /// 弹窗已可见时，如果匹配列表相同，保留当前选中项（用户上下键选择不被覆盖）。
        /// </summary>
        private void ShowOrUpdatePopup(List<CompletionCandidate> matches, string filterWord)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_activeTextView == null) return;

                if (_popup == null)
                {
                    CreatePopup();
                }

                // 检查匹配列表是否变化（用于跳过 UI 重建 + 保留选中项）
                // 比较 DisplayText 列表，而非引用相等（因为每次创建新对象）
                bool matchesChanged = !matches.Select(m => m.DisplayText)
                    .SequenceEqual(_currentMatches.Select(m => m.DisplayText));
                int previousSelection = _listBox!.SelectedIndex;

                if (matchesChanged)
                {
                    // ★ 列表变化时才重建 UI
                    _currentMatches = matches;
                    _listBox.Items.Clear();
                    foreach (var candidate in matches)
                    {
                        var item = CreateCompletionItem(candidate);
                        _listBox.Items.Add(item);
                    }
                }
                else
                {
                    // ★ 列表未变：跳过 UI 重建，仅更新 _currentMatches 引用
                    _currentMatches = matches;
                }

                // 选择匹配项
                if (_isPopupVisible && !matchesChanged && previousSelection >= 0 && previousSelection < matches.Count)
                {
                    // 弹窗已可见且列表未变：保留当前选中项（用户正在导航）
                    _listBox.SelectedIndex = previousSelection;
                }
                else
                {
                    // 新弹窗或列表变化：选择最佳匹配
                    int selectedIndex = 0;
                    for (int i = 0; i < matches.Count; i++)
                    {
                        if (string.Equals(matches[i].FilterText, filterWord, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                    _listBox.SelectedIndex = selectedIndex;
                }
                _listBox.ScrollIntoView(_listBox.SelectedItem);

                // 定位到光标下方
                PositionPopup();

                if (!_isPopupVisible)
                {
                    // ★ 时序修复：先设 _isPopupVisible = true，再打开 Popup
                    // 确保 ESC 钩子在 Popup 打开动画期间也能拦截
                    _isPopupVisible = true;
                    _popup!.IsOpen = true;
                    _activeTextView.VisualElement.Focus();
                    StartFocusMonitor();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 显示提示框失败");
            }
        }

        // ================================================================
        // 空格触发的提示弹窗
        // ================================================================

        /// <summary>
        /// 空格键按下后调用。
        /// 检查空格前的上下文关键词，决定弹出相应分类的提示弹窗。
        /// 同时检测跨库上下文（[DBName].），若检测到则显示该 DB 的对象。
        /// EXEC 空格 → 显示所有 SP + 函数
        /// FROM 空格 → 显示所有表 + 视图
        /// [MESDATA]. 空格 → 显示 MESDATA 的对应对象
        /// 其他     → 显示关键词 + 模板
        /// </summary>
        internal void OnSpaceTyped()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // US-08: 用户主动输入空格，清除弹窗抑制标志
            _suppressAutoReopen = false;

            // ★ Fire-and-forget: 检测服务器切换
            if (_cacheService != null)
                _ = _cacheService.DetectServerSwitchAsync();

            try
            {
                if (_activeTextView == null) return;

                var caretPos = _activeTextView.Caret.Position.BufferPosition.Position;

                // ★ 首先检测跨库上下文（支持 DBName. 和 DBName.Schema. 两种模式）
                var (crossDbName, crossSchemaName, crossObjPrefix) = DetectCrossDbContextV2(_activeTextView);
                if (crossDbName != null)
                {
                    _logger.Information($"[SqlTemplate] OnSpaceTyped 检测到跨库: db='{crossDbName}', schema='{crossSchemaName}'");

                    if (crossSchemaName != null)
                    {
                        // DBName.Schema. 空格 → 直接显示该 Schema 下对象
                        HandleCrossDbCompletion(crossDbName, crossSchemaName, crossObjPrefix);
                        return;
                    }

                    // DBName. 空格 → 从 DB 名称之前查找上下文关键词
                    var contextKeyword = GetContextBeforeDbName(_activeTextView, caretPos);
                    if (string.IsNullOrEmpty(contextKeyword))
                    {
                        contextKeyword = GetSqlKeywordBeforePosition(_activeTextView, caretPos);
                    }
                    _logger.Information($"[SqlTemplate]   → 跨库上下文关键词: '{contextKeyword}'");
                    var category = GetCompletionCategory(contextKeyword);

                    // 记录空格触发状态
                    _hasSpaceTrigger = true;
                    _lastSpaceCategory = category;

                    ShowCategoryCandidatesForDb(crossDbName, category);
                    return;
                }

                // 获取光标前的上下文关键词（即空格前的单词）
                var contextKeyword2 = GetSqlContextBeforeWord(_activeTextView, caretPos);
                var category2 = GetCompletionCategory(contextKeyword2);

                // 记录空格触发状态，后续 OnCharTyped 保持此分类
                _hasSpaceTrigger = true;
                _lastSpaceCategory = category2;

                ShowCategoryCandidates(category2, contextKeyword2);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] OnSpaceTyped 失败");
            }
        }

        /// <summary>
        /// 显示指定分类下的所有候选项（无前缀过滤，用于空格触发）。
        /// </summary>
        private void ShowCategoryCandidates(CompletionCategory category, string contextKeyword)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var allMatches = new List<CompletionCandidate>();

                switch (category)
                {
                    case CompletionCategory.KeywordsAndTemplates:
                    {
                        // 显示所有关键词
                        foreach (var keyword in SqlKeywords)
                        {
                            allMatches.Add(new CompletionCandidate
                            {
                                DisplayText = keyword,
                                FilterText = keyword,
                                TypeTag = "关键字",
                                InsertText = keyword,
                                ItemType = CompletionItemType.Keyword,
                                MatchScore = 0,
                            });
                        }
                        // 显示所有模板
                        if (_templatesDirty)
                        {
                            _cachedTemplates = _templateService.GetAllTemplates();
                            _templatesDirty = false;
                        }
                        foreach (var t in _cachedTemplates)
                        {
                            if (!string.IsNullOrEmpty(t.Abbreviation))
                            {
                                allMatches.Add(new CompletionCandidate
                                {
                                    DisplayText = t.Abbreviation ?? "",
                                    FilterText = t.Abbreviation ?? "",
                                    TypeTag = "模板",
                                    InsertText = t.Expansion,
                                    PreviewText = t.Expansion,
                                    ItemType = CompletionItemType.Template,
                                    Source = t,
                                    MatchScore = 0,
                                });
                            }
                        }
                        // ★ 常用组合 SQL 片段（旧系统管理，不受新系统干扰）
                        foreach (var (display, insert) in CompoundSqlSnippets)
                        {
                            allMatches.Add(new CompletionCandidate
                            {
                                DisplayText = display,
                                FilterText = display,
                                InsertText = insert,
                                TypeTag = "模板",
                                ItemType = CompletionItemType.Template,
                                MatchScore = -1, // 排在普通模板后面
                            });
                        }
                        break;
                    }

                    case CompletionCategory.TablesAndViews:
                    {
                        // 显示所有表 + 视图
                        if (_cacheService != null)
                        {
                            var dbMatches = _cacheService.GetAllObjectsOfTypes("U", "V");
                            allMatches.AddRange(dbMatches);
                        }
                        break;
                    }

                    case CompletionCategory.ProceduresAndFunctions:
                    {
                        // 显示所有 SP + 函数
                        if (_cacheService != null)
                        {
                            var dbMatches = _cacheService.GetAllObjectsOfTypes("P", "FN", "IF", "TF");
                            allMatches.AddRange(dbMatches);
                        }
                        break;
                    }
                }

                // ★ 所有分类下都添加 DB 名称候选
                if (_cacheService != null)
                {
                    var dbCandidates = _cacheService.GetDatabaseCandidates();
                    allMatches.AddRange(dbCandidates);
                }

                if (allMatches.Count > 0)
                {
                    ShowOrUpdatePopup(allMatches, "");
                }
                else
                {
                    HidePopup();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] ShowCategoryCandidates 失败");
            }
        }

        /// <summary>
        /// 选中 DB 名称后，自动弹出该数据库指定分类的对象列表。
        /// </summary>
        private void ShowCategoryCandidatesForDb(string dbName, CompletionCategory category)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_cacheService == null)
                {
                    _logger.Warning("[SqlTemplate] ShowCategoryCandidatesForDb: _cacheService 为 null");
                    return;
                }

                _logger.Information($"[SqlTemplate] ShowCategoryCandidatesForDb: db='{dbName}', category={category}, _currentServer={_cacheService.GetCurrentServer()}");

                var allMatches = new List<CompletionCandidate>();

                switch (category)
                {
                    case CompletionCategory.KeywordsAndTemplates:
                    {
                        // 一般输入上下文：显示该 DB 的所有对象（表+视图+SP+函数）
                        var dbMatches = _cacheService.GetAllObjectsOfTypesForDb(dbName, "U", "V", "P", "FN", "IF", "TF");
                        _logger.Information($"[SqlTemplate]   → GetAllObjectsOfTypesForDb('{dbName}', ALL) 返回 {dbMatches.Count} 项");
                        allMatches.AddRange(dbMatches);
                        break;
                    }
                    case CompletionCategory.TablesAndViews:
                    {
                        var dbMatches = _cacheService.GetAllObjectsOfTypesForDb(dbName, "U", "V");
                        _logger.Information($"[SqlTemplate]   → GetAllObjectsOfTypesForDb('{dbName}', U,V) 返回 {dbMatches.Count} 项");
                        allMatches.AddRange(dbMatches);
                        break;
                    }
                    case CompletionCategory.ProceduresAndFunctions:
                    {
                        var dbMatches = _cacheService.GetAllObjectsOfTypesForDb(dbName, "P", "FN", "IF", "TF");
                        _logger.Information($"[SqlTemplate]   → GetAllObjectsOfTypesForDb('{dbName}', P,FN,IF,TF) 返回 {dbMatches.Count} 项");
                        allMatches.AddRange(dbMatches);
                        break;
                    }
                }

                if (allMatches.Count > 0)
                {
                    _logger.Information($"[SqlTemplate] ✅ 显示 {dbName} 的 {allMatches.Count} 个候选项");
                    ShowOrUpdatePopup(allMatches, "");
                }
                else
                {
                    _logger.Warning($"[SqlTemplate] ⚠️ {dbName} 无匹配项，不显示弹窗");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] ShowCategoryCandidatesForDb 失败");
            }
        }

        /// <summary>
        /// 创建 WPF Popup 控件（模仿 SSMS IntelliSense 样式）。
        /// </summary>
        private void CreatePopup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _listBox = new ListBox
            {
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9)),
                MaxWidth = 500,
                MaxHeight = 300,
                Focusable = false,
            };
            // 恢复虚拟化：配合 Grid 固定列宽布局，虚拟化不会导致标签错位
            VirtualizingStackPanel.SetIsVirtualizing(_listBox, true);
            VirtualizingStackPanel.SetVirtualizationMode(_listBox, VirtualizationMode.Recycling);
            ScrollViewer.SetHorizontalScrollBarVisibility(_listBox, ScrollBarVisibility.Hidden);

            // 选中项样式（蓝色高亮），拉伸填满弹窗宽度
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 3, 8, 3)));
            itemStyle.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))));
            itemStyle.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var trigger = new Trigger
            {
                Property = ListBoxItem.IsSelectedProperty,
                Value = true,
            };
            trigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xC9, 0xDE, 0xF5))));
            itemStyle.Triggers.Add(trigger);
            _listBox.ItemContainerStyle = itemStyle;

            // 鼠标单击预览表结构，双击展开候选项
            _listBox.PreviewMouseLeftButtonUp += OnCompletionItemPreviewMouseLeftButtonUp;


            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 2,
                    Opacity = 0.3,
                    Color = Colors.Gray,
                },
                Child = _listBox,
            };

            _popup = new Popup
            {
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Child = border,
                Focusable = false,
                StaysOpen = true,
            };
        }

        /// <summary>
        /// 创建列表项 UI（主文本 + 预览 + 类型标签）。
        /// 统一支持 SQL 模板和数据库对象。
        /// 使用 Grid 布局确保类型标签在弹窗右侧对齐。
        /// </summary>
        private ListBoxItem CreateCompletionItem(CompletionCandidate candidate)
        {
            // 使用 Grid 布局：内容区 Star（填充），类型标签 Auto（固定在右侧）
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 内容区
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 标签区

            // 类型标签（右列，Auto）
            var tagText = new TextBlock
            {
                Text = candidate.TypeTag,
                FontSize = 10,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(GetTypeTagForegroundColor(candidate.TypeTag)),
                Background = new SolidColorBrush(GetTypeTagBackgroundColor(candidate.TypeTag)),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
            };
            Grid.SetColumn(tagText, 1);
            grid.Children.Add(tagText);

            // 主文本 + 预览（左列，Star）
            var contentPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var mainText = new TextBlock
            {
                Text = candidate.DisplayText,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x4A, 0xB0)),
                MinWidth = 70,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            };
            contentPanel.Children.Add(mainText);

            // 预览文本（仅模板类型有）
            if (candidate.ItemType == CompletionItemType.Template && !string.IsNullOrEmpty(candidate.PreviewText))
            {
                var preview = candidate.PreviewText!;
                preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", "");
                preview = preview.Replace("$cursor", "|");
                if (preview.Length > 55) preview = preview.Substring(0, 52) + "...";

                var expText = new TextBlock
                {
                    Text = preview,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                };
                contentPanel.Children.Add(expText);
            }

            Grid.SetColumn(contentPanel, 0);
            grid.Children.Add(contentPanel);

            return new ListBoxItem
            {
                Content = grid,
                Tag = candidate,
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        /// <summary>
        /// 鼠标单击预览表结构，双击展开候选项。
        /// </summary>
        private void OnCompletionItemPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_listBox == null) return;

            var lb = (ListBox)sender;
            var point = e.GetPosition(lb);
            var hitResult = lb.InputHitTest(point) as DependencyObject;

            while (hitResult != null && !(hitResult is ListBoxItem))
                hitResult = VisualTreeHelper.GetParent(hitResult);

            if (hitResult is not ListBoxItem item || item.Tag is not CompletionCandidate candidate)
                return;

            lb.SelectedItem = item;

            if (e.ClickCount == 2)
            {
                _logger.Debug("[SqlTemplate] 鼠标双击选中: 展开");
                HideStructurePopup();
                // ★ Bug 修复：重置节流时间戳，避免双击展开被之前的 TAB/ENTER 节流跳过
                _lastExpandTime = DateTime.MinValue;
                ExpandSelectedTemplate();
                e.Handled = true;
                return;
            }

            // 单击：表/视图显示结构预览
            if (candidate.ItemType == CompletionItemType.DatabaseObject &&
                candidate.Source is DatabaseObjectInfo objInfo &&
                (objInfo.TypeCode == "U" || objInfo.TypeCode == "V") &&
                objInfo.Columns != null && objInfo.Columns.Count > 0)
            {
                ShowStructurePreview(objInfo);
                e.Handled = true;
            }
        }


        /// <summary>
        /// 获取类型标签的前景色。
        /// </summary>
        private static Color GetTypeTagForegroundColor(string typeTag)
        {
            return typeTag switch
            {
                "模板" => Color.FromRgb(0x6A, 0x1B, 0x9A),    // 紫色
                "关键字" => Color.FromRgb(0xE6, 0x51, 0x00),   // 橙色
                "表" => Color.FromRgb(0x0D, 0x47, 0xA1),       // 深蓝
                "视图" => Color.FromRgb(0x1B, 0x5E, 0x20),     // 深绿
                "SP" => Color.FromRgb(0xE6, 0x51, 0x00),       // 橙色
                "函数" => Color.FromRgb(0x00, 0x69, 0x7A),     // 青色
                "DB" => Color.FromRgb(0x7B, 0x1F, 0xA2),       // 紫罗兰
                "模式" => Color.FromRgb(0x00, 0x69, 0x5C),     // 深青绿
                "字段" => Color.FromRgb(0x1A, 0x23, 0x7E),     // 深蓝灰
                _ => Color.FromRgb(0x42, 0x42, 0x42)           // 默认灰色
            };
        }

        /// <summary>
        /// 获取类型标签的背景色。
        /// </summary>
        private static Color GetTypeTagBackgroundColor(string typeTag)
        {
            return typeTag switch
            {
                "模板" => Color.FromRgb(0xF3, 0xE5, 0xF5),    // 浅紫
                "关键字" => Color.FromRgb(0xFF, 0xF3, 0xE0),   // 浅橙
                "表" => Color.FromRgb(0xE3, 0xF2, 0xFD),       // 浅蓝
                "视图" => Color.FromRgb(0xE8, 0xF5, 0xE9),     // 浅绿
                "SP" => Color.FromRgb(0xFF, 0xF3, 0xE0),      // 浅橙
                "函数" => Color.FromRgb(0xE0, 0xF7, 0xFA),     // 浅青
                "DB" => Color.FromRgb(0xF3, 0xE5, 0xF5),      // 浅紫
                "模式" => Color.FromRgb(0xE0, 0xF2, 0xF1),     // 浅青绿
                "字段" => Color.FromRgb(0xE8, 0xEA, 0xF6),     // 浅蓝灰
                _ => Color.FromRgb(0xF5, 0xF5, 0xF5)           // 默认浅灰
            };
        }

        /// <summary>
        /// 将提示框定位到当前光标下方。
        /// </summary>
        private void PositionPopup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_activeTextView == null || _popup == null) return;

            try
            {
                var caretPosition = _activeTextView.Caret.Position.BufferPosition;
                var textViewLine = _activeTextView.GetTextViewLineContainingBufferPosition(caretPosition);
                var textBounds = textViewLine.GetCharacterBounds(caretPosition);

                _popup.PlacementTarget = _activeTextView.VisualElement;
                _popup.Placement = PlacementMode.Relative;
                _popup.HorizontalOffset = textBounds.Left - _activeTextView.ViewportLeft;
                _popup.VerticalOffset = textBounds.Bottom - _activeTextView.ViewportTop + 1;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 定位提示框失败");
            }
        }

        /// <summary>
        /// 显示指定数据库对象的表结构预览弹窗。
        /// </summary>
        private void ShowStructurePreview(DatabaseObjectInfo objInfo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeTextView == null) return;

            try
            {
                if (_structurePopup == null)
                {
                    _structureTextBox = new TextBox
                    {
                        IsReadOnly = true,
                        IsReadOnlyCaretVisible = false,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Background = new SolidColorBrush(Color.FromRgb(0xFC, 0xFC, 0xFC)),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                        Padding = new Thickness(8),
                        MaxWidth = 600,
                        MaxHeight = 400,
                        TextWrapping = TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    };

                    var border = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xFC, 0xFC, 0xFC)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2),
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            BlurRadius = 6,
                            ShadowDepth = 2,
                            Opacity = 0.3,
                            Color = Colors.Gray,
                        },
                        Child = _structureTextBox,
                    };

                    _structurePopup = new Popup
                    {
                        AllowsTransparency = true,
                        PopupAnimation = PopupAnimation.None,
                        Child = border,
                        Focusable = false,
                        StaysOpen = true,
                    };
                }

                _structureTextBox!.Text = GenerateCreateTableScript(objInfo);
                _structurePopup!.PlacementTarget = _activeTextView.VisualElement;
                _structurePopup.Placement = PlacementMode.Relative;

                var caretPosition = _activeTextView.Caret.Position.BufferPosition;
                var textViewLine = _activeTextView.GetTextViewLineContainingBufferPosition(caretPosition);
                var textBounds = textViewLine.GetCharacterBounds(caretPosition);

                _structurePopup.HorizontalOffset = textBounds.Left - _activeTextView.ViewportLeft + 320;
                _structurePopup.VerticalOffset = textBounds.Bottom - _activeTextView.ViewportTop + 1;
                _structurePopup.IsOpen = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 显示表结构预览失败");
            }
        }

        private void HideStructurePopup()
        {
            if (_structurePopup != null && _structurePopup.IsOpen)
            {
                _structurePopup.IsOpen = false;
                _structurePopupDismissTime = DateTime.UtcNow;
            }
        }

        private string GenerateCreateTableScript(DatabaseObjectInfo objInfo)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE [{objInfo.Schema}].[{objInfo.Name}]");
            sb.AppendLine("(");

            for (int i = 0; i < objInfo.Columns!.Count; i++)
            {
                var c = objInfo.Columns[i];
                sb.Append("    ");

                if (c.IsComputed && !string.IsNullOrEmpty(c.ComputedDefinition))
                {
                    sb.Append($"[{c.Name}] AS {c.ComputedDefinition}");
                }
                else
                {
                    sb.Append($"[{c.Name}] {GetColumnTypeString(c)}");

                    if (c.IsIdentity)
                        sb.Append(" IDENTITY(1,1)");

                    sb.Append(c.IsNullable ? " NULL" : " NOT NULL");

                    // ★ 添加 DEFAULT 值
                    if (c.HasDefault && !string.IsNullOrEmpty(c.DefaultDefinition))
                        sb.Append($" DEFAULT {c.DefaultDefinition}");
                }

                if (i < objInfo.Columns.Count - 1)
                    sb.AppendLine(",");
                else
                    sb.AppendLine();
            }

            // ★ 添加主键约束
            if (objInfo.Indexes != null)
            {
                var pk = objInfo.Indexes.Find(idx => idx.IsPrimaryKey);
                if (pk != null)
                {
                    sb.AppendLine($"    CONSTRAINT [{pk.Name}] PRIMARY KEY ({pk.KeyColumns})");
                }
            }

            sb.AppendLine(");");

            // ★ 添加索引脚本
            if (objInfo.Indexes != null)
            {
                foreach (var idx in objInfo.Indexes)
                {
                    if (idx.IsPrimaryKey)
                        continue;

                    sb.AppendLine();
                    var unique = idx.IsUnique ? "UNIQUE " : "";
                    sb.AppendLine($"CREATE {unique}{idx.TypeDesc} [{idx.Name}]");
                    sb.AppendLine($"    ON [{objInfo.Schema}].[{objInfo.Name}] ({idx.KeyColumns})");

                    if (!string.IsNullOrEmpty(idx.IncludedColumns))
                        sb.AppendLine($"    INCLUDE ({idx.IncludedColumns})");

                    if (idx.HasFilter && !string.IsNullOrEmpty(idx.FilterDefinition))
                        sb.AppendLine($"    WHERE ({idx.FilterDefinition})");

                    sb.AppendLine(";");
                }
            }

            return sb.ToString();
        }

        private static string GetColumnTypeString(ColumnInfo c)
        {
            var type = c.DataType.ToUpperInvariant();
            if (type is "NVARCHAR" or "VARCHAR" or "NCHAR" or "CHAR" or "VARBINARY" or "BINARY")
            {
                var len = c.MaxLength == -1 ? "MAX" : c.MaxLength.ToString();
                if (type is "NVARCHAR" or "NCHAR")
                    len = c.MaxLength == -1 ? "MAX" : (c.MaxLength / 2).ToString();
                return $"{type}({len})";
            }
            if (type is "DECIMAL" or "NUMERIC")
                return $"{type}({c.Precision},{c.Scale})";
            return type;
        }

        /// <summary>
        /// 关闭 SSMS 自带的 IntelliSense 弹窗，避免与我们的弹窗重叠。
        /// 同步向编辑器的命令链发送 CANCEL 命令。
        /// </summary>
        private void DismissSsmsIntelliSense()
        {
            try
            {
                if (_commandFilter?.Next == null) return;

                // 同步发送 VSStd97 Cancel (154) 和 VSStd2K Cancel (2)
                // 直接调用 Next.Exec，绕过我们自己的过滤器
                var guid97 = new Guid("5EFC7975-14BC-11CF-9B2B-00AA00573819"); // VSStd97
                _commandFilter.Next.Exec(ref guid97, 154, 0, IntPtr.Zero, IntPtr.Zero);

                var guid2k = new Guid("1496A755-94DE-11D0-8C3F-00C04FC2AAE2"); // VSStd2K
                _commandFilter.Next.Exec(ref guid2k, 2, 0, IntPtr.Zero, IntPtr.Zero);

                _logger.Information("[SqlTemplate] 已尝试关闭 SSMS IntelliSense 弹窗");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[SqlTemplate] DismissSsmsIntelliSense 异常: {ex.Message}");
            }
        }


        /// <summary>
        /// 弹窗可见时自己插入字符（不传递 TYPECHAR 给 SSMS，避免触发 IntelliSense）。
        /// 如果存在文本选区，先删除选区内容再插入字符。
        /// </summary>
        internal void InsertCharAndRefresh(char ch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeTextView == null) return;

            // US-08: 用户主动输入字符，清除弹窗抑制标志
            _suppressAutoReopen = false;

            try
            {
                _isInternalEdit = true;
                var caret = _activeTextView.Caret.Position.BufferPosition;

                // ★ F1: 检测文本选区，删除选区内容
                var selection = _activeTextView.Selection;
                if (selection != null && !selection.IsEmpty)
                {
                    var selectionSpan = selection.SelectedSpans[0];
                    using (var edit = _activeTextView.TextBuffer.CreateEdit())
                    {
                        edit.Delete(selectionSpan);
                        edit.Apply();
                    }
                    // 删除选区后光标在选区起始位置
                    caret = _activeTextView.Caret.Position.BufferPosition;
                }

                using (var edit = _activeTextView.TextBuffer.CreateEdit())
                {
                    edit.Insert(caret.Position, ch.ToString());
                    edit.Apply();
                }
                _activeTextView.Caret.MoveTo(new SnapshotPoint(
                    _activeTextView.TextBuffer.CurrentSnapshot, caret.Position + 1));

                OnCharTyped();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] InsertCharAndRefresh 失败");
            }
            finally
            {
                _isInternalEdit = false;
            }
        }

        /// <summary>
        /// 弹窗可见时自己处理退格（不传递 BACKSPACE 给 SSMS，避免 CANCEL 干扰）。
        /// </summary>
        internal void HandleBackspace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeTextView == null) return;

            // US-08: 用户主动退格，清除弹窗抑制标志
            _suppressAutoReopen = false;

            try
            {
                _isInternalEdit = true;
                var caret = _activeTextView.Caret.Position.BufferPosition;
                if (caret.Position > 0)
                {
                    using (var edit = _activeTextView.TextBuffer.CreateEdit())
                    {
                        edit.Delete(new Span(caret.Position - 1, 1));
                        edit.Apply();
                    }
                    _activeTextView.Caret.MoveTo(new SnapshotPoint(
                        _activeTextView.TextBuffer.CurrentSnapshot, caret.Position - 1));
                }

                _logger.Information($"[SqlTemplate] HandleBackspace: 退格后刷新弹窗");
                OnCharTyped();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] HandleBackspace 失败");
            }
            finally
            {
                _isInternalEdit = false;
            }
        }

        /// <summary>
        /// 弹窗可见时自己处理 Delete 键。
        /// </summary>
        internal void HandleDelete()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeTextView == null) return;

            // US-08: 用户主动删除，清除弹窗抑制标志
            _suppressAutoReopen = false;

            try
            {
                _isInternalEdit = true;
                var caret = _activeTextView.Caret.Position.BufferPosition;
                var snapshot = _activeTextView.TextBuffer.CurrentSnapshot;
                if (caret.Position < snapshot.Length)
                {
                    using (var edit = _activeTextView.TextBuffer.CreateEdit())
                    {
                        edit.Delete(new Span(caret.Position, 1));
                        edit.Apply();
                    }
                }

                OnCharTyped();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] HandleDelete 失败");
            }
            finally
            {
                _isInternalEdit = false;
            }
        }

        /// <summary>
        /// 隐藏提示框。
        /// </summary>
        internal void HidePopup([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            HideStructurePopup();

            // ★ 时序修复：先设 _isPopupVisible = false，再关闭 Popup
            // 确保钩子回调和 Exec 回调看到的弹窗状态一致
            _isPopupVisible = false;

            if (_popup != null && _popup.IsOpen)
            {
                _popup.IsOpen = false;
                _focusCheckTimer?.Stop();
            }

            // US-06: 弹窗关闭时取消待执行的防抖回调
            // 但 ExpandSelectedTemplate / ShowOrUpdatePopup 内部调用时不清除（它们需要后续重新评估）
            if (caller != "ExpandSelectedTemplate" && caller != "ShowOrUpdatePopup")
            {
                CancelDebounce();
            }

            // ★ 用户主动关闭（非内部 HidePopup）时记录时间 + 重置空格触发状态
            // ExpandSelectedTemplate 和 ShowOrUpdatePopup 的内部 HidePopup 调用不重置
            if (caller != "ExpandSelectedTemplate" && caller != "ShowOrUpdatePopup")
            {
                _lastUserHideTime = DateTime.UtcNow;
                _hasSpaceTrigger = false;
            }
        }

        /// <summary>
        /// 鼠标点击时关闭提示框。
        /// </summary>
        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isPopupVisible)
            {
                HidePopup();
            }
        }

        // ================================================================
        // 模板展开逻辑
        // ================================================================

        /// <summary>
        /// 展开当前选中的候选项。
        /// 模板：展开为完整 SQL 代码片段，处理 $cursor 标记。
        /// 数据库对象：插入 [schema].[name] 格式的对象名。
        /// </summary>
        internal void ExpandSelectedTemplate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 节流：防止 SSMS 重复发送 TAB/ENTER 事件
            var now = DateTime.UtcNow;
            if ((now - _lastExpandTime).TotalMilliseconds < ExpandThrottleMs)
            {
                _logger.Debug("[SqlTemplate] ExpandSelectedTemplate: 节流跳过（重复事件）");
                return;
            }
            _lastExpandTime = now;

            try
            {
                if (_listBox == null || _listBox.SelectedItem == null) return;
                if (_activeTextView == null) return;

                var selectedItem = _listBox.SelectedItem as ListBoxItem;
                if (selectedItem == null) return;

                var candidate = selectedItem.Tag as CompletionCandidate;
                if (candidate == null) return;

                // ★ 运行时检测跨库上下文：如果光标前有 [DBName].Schema.，去除 InsertText 中已存在的前缀
                if (candidate.ItemType == CompletionItemType.DatabaseObject && _activeTextView != null)
                {
                    var (crossDb, crossSchema, _) = DetectCrossDbContextV2(_activeTextView);
                    if (crossDb != null && candidate.InsertText != null)
                    {
                        var insertText = candidate.InsertText;

                        // 去除 [Schema]. 前缀（如果用户已输入 Schema）
                        if (crossSchema != null)
                        {
                            var schemaPrefix = $"[{crossSchema}].";
                            if (insertText.StartsWith(schemaPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                insertText = insertText.Substring(schemaPrefix.Length);
                                _logger.Information($"[SqlTemplate] ⚡ 已去除 InsertText 中的 Schema 前缀 '{crossSchema}'");
                            }
                            // 也处理无方括号的 Schema. 前缀
                            else if (insertText.StartsWith(crossSchema + ".", StringComparison.OrdinalIgnoreCase))
                            {
                                insertText = insertText.Substring(crossSchema.Length + 1);
                                _logger.Information($"[SqlTemplate] ⚡ 已去除 InsertText 中的 Schema 前缀 '{crossSchema}'（无方括号）");
                            }
                        }

                        // 去除 [DBName]. 前缀（如果存在）
                        var dbPrefix = $"[{crossDb}].";
                        if (insertText.StartsWith(dbPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            insertText = insertText.Substring(dbPrefix.Length);
                            _logger.Information($"[SqlTemplate] ⚡ 已去除 InsertText 中的 DB 前缀 '{crossDb}'");
                        }

                        candidate.InsertText = insertText;
                    }
                }

                // ★ F1: 检测文本选区，删除选区内容
                int startPosition;
                int length;

                var selection = _activeTextView.Selection;
                if (selection != null && !selection.IsEmpty)
                {
                    var selectionSpan = selection.SelectedSpans[0];
                    using (var edit = _activeTextView.TextBuffer.CreateEdit())
                    {
                        edit.Delete(selectionSpan);
                        edit.Apply();
                    }
                    startPosition = selectionSpan.Start.Position;
                    length = 0;
                }
                else
                {
                    var (word, wordStart, _) = GetWordBeforeCaret(_activeTextView);
                    if (string.IsNullOrEmpty(word))
                    {
                        startPosition = _activeTextView.Caret.Position.BufferPosition.Position;
                        length = 0;
                    }
                    else
                    {
                        startPosition = wordStart;
                        length = word.Length;
                    }
                }

                string? selectedDbName = null;
                bool triggerCrossDbAfterSchema = false;
                HidePopup();

                _logger.Information($"[SqlTemplate] ✅ ExpandSelectedTemplate 开始执行: Type={candidate.ItemType}, Display='{candidate.DisplayText}', Filter='{candidate.FilterText}'");

                _isExpanding = true;
                try
                {
                    string insertText;

                    if (candidate.ItemType == CompletionItemType.DatabaseObject)
                    {
                        // 数据库对象：插入 [schema].[name] + 空格
                        insertText = candidate.InsertText ?? candidate.DisplayText;

                        // ★ F8: SP/函数参数直接补全到编辑器
                        if (candidate.Source is DatabaseObjectInfo objInfo &&
                            (objInfo.TypeCode is "P" or "FN" or "IF" or "TF"))
                        {
                            _logger.Debug($"[SqlTemplate] SP展开: Name={objInfo.Name}, TypeCode={objInfo.TypeCode}, Parameters={(objInfo.Parameters == null ? "null" : $"{objInfo.Parameters.Count}个")}");
                            
                            if (objInfo.Parameters != null && objInfo.Parameters.Count > 0)
                            {
                                var nonOutputParams = objInfo.Parameters
                                    .Where(p => !p.IsOutput)
                                    .Select(p => p.Name);

                                if (objInfo.TypeCode is "FN" or "TF")
                                {
                                    // 函数：用圆括号包裹参数
                                    insertText = $"{objInfo.Name}({string.Join(", ", nonOutputParams)})";
                                }
                                else
                                {
                                    // SP：空格分隔参数
                                    insertText = $"{objInfo.Name} {string.Join(", ", nonOutputParams)}";
                                }
                                _logger.Information($"[SqlTemplate] SP参数展开: {objInfo.Name} → {insertText}");
                            }
                            else
                            {
                                // ★ Bug 修复：Parameters 为 null（旧缓存），触发后台刷新
                                var dbToRefresh = candidate.DatabaseName ?? _cacheService?.GetCurrentDatabaseName();
                                if (dbToRefresh != null && _cacheService != null)
                                {
                                    _logger.Warning($"[SqlTemplate] SP '{objInfo.Name}' 的 Parameters 为 null（缓存过旧），触发后台刷新: {dbToRefresh}");
                                    _ = Task.Run(() => _cacheService.RefreshSingleDatabaseAsync(dbToRefresh));
                                }
                                else
                                {
                                    _logger.Debug($"[SqlTemplate] SP无参数，使用默认展开");
                                }
                            }
                        }
                        else
                        {
                            // 普通对象：不追加末尾空格（问题4）
                        }
                    }
                    else if (candidate.ItemType == CompletionItemType.Database)
                    {
                        // 数据库名称：插入 [DBName]. 并触发 OnCharTyped 来显示 Schema 候选
                        // ★ Bug 修复：不再直接调用 ShowCategoryCandidatesForDb，而是插入 [DBName]. 
                        // 后触发 OnCharTyped，让 DetectCrossDbContextV2 → HandleCrossDbCompletion 统一处理
                        // 这样用户可以先选 Schema，再选对象
                        // ★ Bug 修复：candidate.InsertText 已是 "[MESDATA]." 格式，直接使用
                        insertText = candidate.InsertText ?? $"[{candidate.DisplayText}].";
                        selectedDbName = candidate.FilterText;
                        _logger.Information($"[SqlTemplate] 选中 DB 名称: '{selectedDbName}', 插入文本='{insertText}', 当前分类={_lastCompletionCategory}");
                    }
                    else if (candidate.ItemType == CompletionItemType.Schema)
                    {
                        // Schema 名称：插入 "Schema."，然后触发跨库对象补全
                        insertText = candidate.InsertText ?? candidate.DisplayText;
                        if (!insertText.EndsWith(".", StringComparison.Ordinal))
                            insertText += ".";
                        triggerCrossDbAfterSchema = true;
                    }
                    else if (candidate.ItemType == CompletionItemType.Keyword)
                    {
                        // SQL 关键词：插入关键词（不再追加空格，问题4）
                        insertText = candidate.InsertText ?? candidate.DisplayText;
                    }
                    else
                    {
                        // SQL 模板：获取展开内容
                        insertText = candidate.InsertText ?? "";
                    }

                    if (!string.IsNullOrEmpty(insertText))
                    {
                        DoExpand(_activeTextView, startPosition, length, insertText);
                    }
                }
                finally
                {
                    _isExpanding = false;
                }

                // ★ 选中 DB 名称后：懒惰加载 + 触发 OnCharTyped 显示 Schema 候选
                if (selectedDbName != null && _cacheService != null && _activeTextView != null)
                {
                    // 懒惰加载（可能尚未缓存），然后触发弹窗
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _cacheService.EnsureDatabaseLoadedAsync(selectedDbName);
                        }
                        catch { }
                    });

                    // ★ Bug 修复：不再直接调用 ShowCategoryCandidatesForDb，
                    // 而是触发 OnCharTyped，让 DetectCrossDbContextV2 检测 [DBName]. 上下文
                    // HandleCrossDbCompletion 会显示 Schema 候选列表
                    _logger.Information($"[SqlTemplate] DB '{selectedDbName}' 选中，触发 OnCharTyped 显示 Schema 候选");
                    // 用户主动选择 DB/Schema 后需要立即响应，跳过防抖
                    OnCharTypedCore();
                }

                // ★ 选中 Schema 后：触发跨库对象补全（DBName.Schema. → 对象列表）
                if (triggerCrossDbAfterSchema && _activeTextView != null)
                {
                    // 用户主动选择 DB/Schema 后需要立即响应，跳过防抖
                    OnCharTypedCore();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 选择展开失败");
            }
        }

        /// <summary>
        /// 弹窗不可见时，TAB 键精确匹配展开（供命令过滤器调用）。
        /// </summary>
        internal bool TryExpandExactMatch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_activeTextView == null) return false;

                var (word, wordStart, _) = GetWordBeforeCaret(_activeTextView);
                if (string.IsNullOrEmpty(word)) return false;

                var expansion = _templateService.TryExpand(word);
                if (string.IsNullOrEmpty(expansion)) return false;

                _isExpanding = true;
                try
                {
                    DoExpand(_activeTextView, wordStart, word.Length, expansion);
                    //_logger.Information($"[SqlTemplate] ✅ TAB展开: '{word}' → ({expansion.Length} 字符)");
                    return true;
                }
                finally
                {
                    _isExpanding = false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] TAB展开失败");
                return false;
            }
        }

        /// <summary>
        /// 执行模板展开：替换缩写为展开内容，处理 $cursor 标记。
        /// </summary>
        private void DoExpand(IWpfTextView wpfTextView, int startPosition, int length, string expansion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            using (var edit = wpfTextView.TextBuffer.CreateEdit())
            {
                var span = new Span(startPosition, length);
                edit.Replace(span, expansion);
                edit.Apply();
            }

            var cursorIndex = expansion.IndexOf(CursorMarker, StringComparison.Ordinal);
            if (cursorIndex >= 0)
            {
                var cursorPosition = startPosition + cursorIndex;

                using (var edit = wpfTextView.TextBuffer.CreateEdit())
                {
                    var markerSpan = new Span(cursorPosition, CursorMarker.Length);
                    edit.Delete(markerSpan);
                    edit.Apply();
                }

                wpfTextView.Caret.MoveTo(new SnapshotPoint(wpfTextView.TextBuffer.CurrentSnapshot, cursorPosition));
            }
            else
            {
                var endPosition = startPosition + expansion.Length;
                wpfTextView.Caret.MoveTo(new SnapshotPoint(wpfTextView.TextBuffer.CurrentSnapshot, endPosition));
            }
        }

        /// <summary>
        /// 判断字符是否为单词字符。
        /// </summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// 移动选中项（供命令过滤器调用）。
        /// 支持循环导航：到达底部后按 DOWN 回到第一项，到达顶部后按 UP 回到最后一项。
        /// </summary>
        internal void MoveSelection(int delta)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_listBox == null || _listBox.Items.Count == 0) return;

            // 节流：SSMS 对每次按键发送多次命令事件（日志确认 4 次），
            // 150ms 内只处理第一次，否则选项会在两个项之间来回弹跳
            var now = DateTime.UtcNow;
            if ((now - _lastMoveTime).TotalMilliseconds < MoveThrottleMs)
            {
                return;
            }
            _lastMoveTime = now;

            int count = _listBox.Items.Count;
            int oldIndex = _listBox.SelectedIndex;
            int newIndex = oldIndex + delta;

            // 循环导航
            if (newIndex >= count) newIndex = 0;
            if (newIndex < 0) newIndex = count - 1;

            _listBox.SelectedIndex = newIndex;
            _listBox.ScrollIntoView(_listBox.SelectedItem);
            _logger.Information($"[SqlTemplate] MoveSelection: delta={delta}, {oldIndex}→{newIndex} (count={count})");
        }

        // ================================================================
        // IOleCommandTarget 命令过滤器
        //
        // 参考 Microsoft VSSDK-Extensibility-Samples (Ook_Language_Integration)
        // 和官方 Walkthrough: Displaying Statement Completion
        //
        // 核心模式：
        // 1. 预处理：拦截 UP/DOWN/RETURN/TAB/CANCEL
        // 2. 传递给 Next
        // 3. 后处理：TYPECHAR → 同步触发/过滤弹窗
        // ================================================================

        private class CompletionCommandFilter : IOleCommandTarget
        {
            private readonly SqlTemplateCompletionService _owner;
            public IOleCommandTarget? Next { get; set; }

            // VS 标准命令组 GUID
            private static readonly Guid VSStd97Guid = new Guid("5EFC7975-14BC-11CF-9B2B-00AA00573819");
            private static readonly Guid VSStd2KGuid = new Guid("1496A755-94DE-11D0-8C3F-00C04FC2AAE2");

            // VSStd2KCmdID 命令 ID
            // 注意：SSMS 22 中 UP/DOWN 的命令 ID 与标准 VSStd2KCmdID 枚举相反！
            // 标准：UP=11, DOWN=13，但 SSMS 22 实际发送：UP=13, DOWN=11
            // （通过用户反馈确认：id=11 对应物理 DOWN 键，id=13 对应物理 UP 键）
            private const uint CMDID_TYPECHAR = 1;   // 输入字符
            private const uint CMDID_CANCEL    = 2;   // ESC
            private const uint CMDID_RETURN    = 3;   // Enter
            private const uint CMDID_TAB       = 4;   // Tab
            private const uint CMDID_BACKSPACE = 14;  // Backspace (VSStd2KCmdID.BACKSPACE)
            private const uint CMDID_LEFT      = 7;   // ←
            private const uint CMDID_DOWN      = 11;  // ↓ (SSMS 22: id=11 是 DOWN)
            private const uint CMDID_RIGHT     = 9;   // →
            private const uint CMDID_UP        = 13;  // ↑ (SSMS 22: id=13 是 UP)
            private const uint CMDID_DELETE    = 16;  // Delete (VSStd2KCmdID.DELETE)

            // VSStd97CmdID: Up=11, Down=12, Left=13, Right=14, Cancel=154, Backspace=43, Delete=17
            private const uint CMDID_UP_97    = 11;
            private const uint CMDID_DOWN_97  = 12;
            private const uint CMDID_LEFT_97  = 13;
            private const uint CMDID_RIGHT_97 = 14;
            private const uint CMDID_CANCEL_97 = 154;

            private const int OLECMDERR_E_CANCELED = unchecked((int)0x80040104);

            public CompletionCommandFilter(SqlTemplateCompletionService owner)
            {
                _owner = owner;
            }

            /// <summary>
            /// 从 pvaIn 中提取 TYPECHAR 命令的输入字符。
            /// 参考 Microsoft VSSDK 示例。
            /// </summary>
            private static char GetTypeChar(IntPtr pvaIn)
            {
                if (pvaIn == IntPtr.Zero) return '\0';
                try
                {
                    return (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                }
                catch
                {
                    return '\0';
                }
            }

            public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
            {
                // 代码补全被禁用时，直接放行所有命令，不拦截
                if (!SqlTemplateCompletionService.IsEnabled)
                {
                    return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? -1;
                }

                try
                {
                    // ================================================================
                    // 阶段 1：预处理（在传递给 Next 之前）
                    // 拦截需要消费的命令（返回 CANCELED，不传递给 Next）
                    // ================================================================

                    if (pguidCmdGroup == VSStd2KGuid)
                    {
                        // ★ Bug 修复：仅在弹窗可见时拦截 CANCEL
                        // SSMS 22 会将 BACKSPACE 映射为 CMDID_CANCEL (ECMD_CANCEL, id=2)，
                        // 无条件拦截会导致 BACKSPACE 被消费（return 0），退格失效。
                        // DELETE (id=16) 不受影响，所以 DELETE 正常。
                        if (nCmdID == CMDID_CANCEL && _owner._isPopupVisible)
                        {
                            _owner._logger.Debug($"[SqlTemplate] ESC 拦截: _isPopupVisible={_owner._isPopupVisible}");
                            _owner._suppressAutoReopen = true; // US-08: 先设标志
                            _owner.DismissSsmsIntelliSense();
                            _owner.HidePopup();
                            _owner._lastUserHideTime = DateTime.UtcNow;
                            return 0;
                        }

                        // === 始终拦截 TYPECHAR（单词字符）===
                        // 自己插入字符 + 触发弹窗，SSMS 永远看不到 TYPECHAR
                        // 这样 SSMS 不会触发 IntelliSense，避免弹窗重叠
                        if (nCmdID == CMDID_TYPECHAR)
                        {
                            char ch = GetTypeChar(pvaIn);
                            if (char.IsLetterOrDigit(ch) || ch == '_')
                            {
                                _owner.InsertCharAndRefresh(ch);
                                return OLECMDERR_E_CANCELED;
                            }
                            else if (ch == ' ')
                            {
                                // ★ 空格键：先放行让空格写入缓冲区，再触发弹窗
                                int spaceHr = Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? -1;
                                if (spaceHr >= 0)
                                {
                                    _owner.OnSpaceTyped();
                                }
                                return spaceHr;
                            }
                            else
                            {
                                // 非单词字符（含退格）：放行给 SSMS 处理
                                // 不关闭弹窗，让 OnLayoutChanged → OnCharTyped 自然评估
                                // 退格：文本变短 → OnCharTyped 重新匹配 → 弹窗更新（不关闭）
                            }
                        }

                        if (_owner._isPopupVisible)
                        {
                            // === 弹窗可见：拦截导航和编辑键 ===
                            switch (nCmdID)
                            {
                                case CMDID_UP:
                                    // SSMS 22: CMDID_UP(id=13) 对应物理 DOWN 键 → 向下移动
                                    _owner.MoveSelection(1);
                                    return OLECMDERR_E_CANCELED;

                                case CMDID_DOWN:
                                    // SSMS 22: CMDID_DOWN(id=11) 对应物理 UP 键 → 向上移动
                                    _owner.MoveSelection(-1);
                                    return OLECMDERR_E_CANCELED;

                                case CMDID_RETURN:
                                    // ★ ENTER 键：不应选中补全项，只关闭弹窗，让 SSMS 插入换行
                                    _owner.CancelDebounce();
                                    _owner._suppressAutoReopen = true; // US-08: 先设标志，防止 HidePopup 内部触发 OnLayoutChanged → debounce 重开弹窗
                                    _owner.HidePopup();
                                    break; // 放行给 Next.Exec（插入换行）

                                case CMDID_TAB:
                                    _owner.CancelDebounce();
                                    _owner._suppressAutoReopen = true; // US-08: 先设标志，防止 ExpandSelectedTemplate 内部 HidePopup 触发 OnLayoutChanged → debounce 重开弹窗
                                    _owner.ExpandSelectedTemplate();
                                    return OLECMDERR_E_CANCELED;

                                case CMDID_CANCEL:
                                    // ★ F2: 先关闭 SSMS IntelliSense（此时 _isPopupVisible 仍为 true），再隐藏我们的弹窗
                                    _owner.DismissSsmsIntelliSense();
                                    _owner.HidePopup();
                                    _owner.CancelDebounce();
                                    // ★ 返回 0 (S_OK) 表示已处理，阻止命令继续传播
                                    // OLECMDERR_E_CANCELED 会让 VS Shell 继续路由该 ESC，
                                    // 可能导致 SSMS 重新打开 IntelliSense 造成弹窗未关闭的错觉
                                    return 0;

                                case CMDID_LEFT:
                                case CMDID_RIGHT:
                                    _owner.HidePopup();
                                    // 放行，让光标移动
                                    break;

                                case CMDID_BACKSPACE:
                                    // ★ Bug 修复：隐藏弹窗，让 SSMS 原生处理退格
                                    // 避免 _activeTextView 不匹配当前文档导致退格失效
                                    _owner._logger.Debug("[SqlTemplate] BACKSPACE (popup visible): 隐藏弹窗，放行给 SSMS");
                                    _owner.HidePopup();
                                    break; // 放行给 Next.Exec

                                case CMDID_DELETE:
                                    // ★ Bug 修复：同上，隐藏弹窗后放行给 SSMS
                                    _owner._logger.Debug("[SqlTemplate] DELETE (popup visible): 隐藏弹窗，放行给 SSMS");
                                    _owner.HidePopup();
                                    break; // 放行给 Next.Exec
                            }
                        }
                        else
                        {
                            // === 弹窗不可见：TAB 精确匹配展开 ===
                            if (nCmdID == CMDID_TAB)
                            {
                                // 节流：刚展开过，消费重复 TAB 事件（不插入 TAB 字符）
                                if ((DateTime.UtcNow - _owner._lastExpandTime).TotalMilliseconds < ExpandThrottleMs)
                                {
                                    return OLECMDERR_E_CANCELED;
                                }
                                if (_owner.TryExpandExactMatch())
                                {
                                    return OLECMDERR_E_CANCELED;
                                }
                            }
                        }
                    }
                    else if (pguidCmdGroup == VSStd97Guid)
                    {
                        // ★ Bug 修复：仅在弹窗可见时拦截 VSStd97 CANCEL
                        // 同 VSStd2K CANCEL 修复理由
                        if (nCmdID == CMDID_CANCEL_97 && _owner._isPopupVisible)
                        {
                            _owner._logger.Debug($"[SqlTemplate] ESC(VSStd97) 拦截: _isPopupVisible={_owner._isPopupVisible}");
                            _owner._suppressAutoReopen = true; // US-08: 先设标志
                            _owner.DismissSsmsIntelliSense();
                            _owner.HidePopup();
                            _owner._lastUserHideTime = DateTime.UtcNow;
                            return 0;
                        }

                        // VSStd97 组兜底（部分编辑器可能使用此组）
                        if (_owner._isPopupVisible)
                        {
                            switch (nCmdID)
                            {
                                case CMDID_UP_97:
                                    _owner.MoveSelection(-1);
                                    return OLECMDERR_E_CANCELED;

                                case CMDID_DOWN_97:
                                    _owner.MoveSelection(1);
                                    return OLECMDERR_E_CANCELED;

                                case CMDID_LEFT_97:
                                case CMDID_RIGHT_97:
                                    _owner.HidePopup();
                                    // 放行
                                    break;

                                case CMDID_CANCEL_97:
                                    _owner.DismissSsmsIntelliSense();
                                    _owner.HidePopup();
                                    return 0;

                                case 43: // VSStd97CmdID.Backspace
                                    // ★ Bug 修复：隐藏弹窗，让 SSMS 原生处理退格
                                    _owner._logger.Debug("[SqlTemplate] VSStd97 BACKSPACE (popup visible): 隐藏弹窗，放行给 SSMS");
                                    _owner.HidePopup();
                                    break; // 放行给 Next.Exec

                                case 17: // VSStd97CmdID.Delete
                                    _owner._logger.Debug("[SqlTemplate] VSStd97 DELETE (popup visible): 隐藏弹窗，放行给 SSMS");
                                    _owner.HidePopup();
                                    break; // 放行给 Next.Exec
                            }
                        }
                    }

                    // ================================================================
                    // 阶段 2：传递给 Next（让命令执行，如字符插入缓冲区）
                    // ================================================================

                    // 记录弹窗状态和文本快照（用于后处理兜底）
                    bool popupWasVisible = _owner._isPopupVisible;
                    int snapshotLengthBefore = _owner._activeTextView?.TextBuffer?.CurrentSnapshot?.Length ?? -1;

                    int hr = Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? -1;

                    // ================================================================
                    // 阶段 3：后处理（Next 执行成功后）
                    // 参考 Microsoft 模式：TYPECHAR 后触发/过滤弹窗
                    // ================================================================
                    bool postProcessed = false;

                    if (hr >= 0 && pguidCmdGroup == VSStd2KGuid)
                    {
                        switch (nCmdID)
                        {
                            case CMDID_TYPECHAR:
                                {
                                    char ch = GetTypeChar(pvaIn);
                                    if (char.IsLetterOrDigit(ch) || ch == '_')
                                    {
                                        // 输入字母/数字/下划线：触发或更新弹窗（同步）
                                        _owner.OnCharTyped();
                                        postProcessed = true;
                                    }
                                    else if (ch == '.')
                                    {
                                        // ★★★ F3/F5/F8: 点号是跨库检测和字段补全的关键触发字符
                                        _owner.OnCharTyped();
                                        postProcessed = true;
                                    }
                                    else if (ch == ' ')
                                    {
                                        _owner.OnSpaceTyped();
                                        postProcessed = true;
                                    }
                                    else
                                    {
                                        // 其他非单词字符：放行给 SSMS 处理
                                        // 不关闭弹窗，让 OnLayoutChanged → OnCharTyped 自然评估
                                    }
                                }
                                break;

                            case CMDID_BACKSPACE:
                            case CMDID_DELETE:
                                // 删除键：重新评估弹窗（模糊匹配，退格后仍可匹配）
                                _owner._logger.Debug("[SqlTemplate] BACKSPACE/DELETE 后处理: 重新评估弹窗");
                                _owner.OnCharTyped();
                                postProcessed = true;
                                break;
                        }
                    }
                    else if (hr >= 0 && pguidCmdGroup == VSStd97Guid)
                    {
                        switch (nCmdID)
                        {
                            case 43: // VSStd97CmdID.Backspace
                                _owner._logger.Debug("[SqlTemplate] VSStd97 BACKSPACE 后处理: 重新评估弹窗");
                                // ★ Bug 修复：Next.Exec 已处理删除，此处只需重新评估弹窗，不要再次删除
                                _owner.OnCharTyped();
                                postProcessed = true;
                                break;

                            case 17: // VSStd97CmdID.Delete
                                _owner._logger.Debug("[SqlTemplate] VSStd97 DELETE 后处理: 重新评估弹窗");
                                _owner.OnCharTyped();
                                postProcessed = true;
                                break;
                        }
                    }

                    // ================================================================
                    // 阶段 3.5：兜底 — 任何导致文本长度变化的命令都重新评估弹窗
                    // 解决 '.' / BACKSPACE / DELETE 走未知命令组导致弹窗未触发的问题
                    // ★ Bug 修复：跳过 CANCEL(ESC) 命令，避免 ESC 后弹窗被重新打开
                    // ================================================================
                    bool isCancelCommand = (pguidCmdGroup == VSStd2KGuid && nCmdID == CMDID_CANCEL) ||
                                           (pguidCmdGroup == VSStd97Guid && nCmdID == CMDID_CANCEL_97);
                    if (hr >= 0 && !postProcessed && !_owner._isInternalEdit && !isCancelCommand)
                    {
                        int snapshotLengthAfter = _owner._activeTextView?.TextBuffer?.CurrentSnapshot?.Length ?? -1;
                        if (snapshotLengthAfter != snapshotLengthBefore)
                        {
                            _owner._logger.Debug($"[SqlTemplate] 兜底后处理: 文本长度变化 {snapshotLengthBefore}→{snapshotLengthAfter}, 命令组={pguidCmdGroup}, nCmdID={nCmdID}");
                            _owner.OnCharTyped();
                        }
                    }

                    // ★ ESC 命令兜底：即使 ESC 改变了文本（某些编辑器行为），也确保弹窗关闭
                    if (hr >= 0 && isCancelCommand)
                    {
                        if (_owner._isPopupVisible)
                        {
                            _owner.HidePopup();
                        }
                        _owner._lastUserHideTime = DateTime.UtcNow;
                    }

                    // ================================================================
                    // 阶段 3.6：兜底 — 弹窗可见但被未知命令关闭时重新评估
                    // 解决 BACKSPACE 走未知命令组导致弹窗消失的问题
                    // ================================================================
                    if (hr >= 0 && popupWasVisible && !_owner._isPopupVisible && !postProcessed)
                    {
                        int snapshotLengthAfter = _owner._activeTextView?.TextBuffer?.CurrentSnapshot?.Length ?? -1;
                        if (snapshotLengthAfter != snapshotLengthBefore)
                        {
                            // 文本发生了变化（如退格、删除），重新评估弹窗
                            _owner.OnCharTyped();
                        }
                        // ★ F2: 文本长度未变化 + 弹窗从可见变为不可见
                        // 很可能是 ESC 或其他关闭命令，不重新打开弹窗
                        else
                        {
                            _owner._lastUserHideTime = DateTime.UtcNow;
                        }
                    }

                    return hr;

                }
                catch (Exception ex)
                {
                    _owner._logger.Error(ex, "[SqlTemplate] CommandFilter.Exec 异常");
                }

                return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? -1;
            }

            public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
            {
                return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? -1;
            }
        }

        #region 获取 IWpfTextView 的辅助方法

        private async Task<IWpfTextView?> GetActiveWpfTextViewAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var vsTextView = await GetTextViewAsync();
                if (vsTextView == null) return null;

                return await GetWpfTextViewAsync(vsTextView);
            }
            catch { return null; }
        }

        private async Task<IVsTextView?> GetTextViewAsync()
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

        private async Task<IWpfTextView?> GetWpfTextViewAsync(IVsTextView vsTextView)
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
