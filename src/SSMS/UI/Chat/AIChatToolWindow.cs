using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.UI.Chat;

/// <summary>
/// SSMS 可停靠工具窗口：AI 聊天面板。
/// 通过 ProvideToolWindow 注册后自动获得停靠/固定/自动隐藏能力。
/// 
/// 生命周期策略（关闭即销毁，下次打开为全新窗口）：
/// - 用户点击 X → 窗口被隐藏
/// - 每次打开（Ctrl+Shift+K）时检测窗口是否曾被隐藏：
///   * 是 → 清理旧资源，创建全新的 AIChatControl + ChatSessionService
///   * 否（窗口当前可见）→ 保持现有会话
/// 
/// 初始化策略：
/// - 静态服务引用在 Package.InitializeAsync 中设置
/// - 构造函数中直接尝试初始化（覆盖 VS SDK 布局恢复等场景）
/// - InitializeContent 作为主要入口，处理窗口关闭后的重建
/// </summary>
[Guid("F8E9D8C7-B6A5-4321-0987-ABCDEF123456")]
public class AIChatToolWindow : ToolWindowPane
{
    private AIChatControl? _chatControl;

    /// <summary>静态服务引用，由 SSMSBoostPlusPackage.InitializeAsync 设置</summary>
    public static IServiceProvider? StaticServiceProvider { get; set; }
    public static IConfigurationService? StaticConfigService { get; set; }
    public static ILoggerService? StaticLogger { get; set; }

    /// <summary>
    /// VS SDK 在创建窗口框架后回调此方法。
    /// 用于布局恢复场景：当 VS 在包初始化完成前恢复工具窗口时，确保内容被正确初始化，
    /// 避免占位文本"正在加载 AI 助手..."永久卡住。
    /// </summary>
    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();

        // 布局恢复场景：如果静态引用已设置，直接初始化内容
        if (StaticServiceProvider != null && StaticConfigService != null && StaticLogger != null)
        {
            try
            {
                if (_chatControl == null)
                {
                    StaticLogger.Information("[AIChatToolWindow] OnToolWindowCreated: 使用静态引用初始化...");
                    var chatSession = new ChatSessionService();
                    _chatControl = new AIChatControl(StaticServiceProvider, StaticConfigService, chatSession, StaticLogger);
                    Content = _chatControl;
                    StaticLogger.Information("[AIChatToolWindow] ✅ OnToolWindowCreated 初始化成功");
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Error(ex, "[AIChatToolWindow] ❌ OnToolWindowCreated 初始化失败");
                // 不设置占位文本，由 InitializeContent 兜底
            }
        }
    }

    /// <summary>
    /// 无参构造函数（VS SDK 要求）。
    /// 优先使用静态服务引用直接初始化，避免占位文本"正在加载"卡住。
    /// 若服务未就绪（理论上不应发生），显示占位文本等待 InitializeContent 兜底。
    /// </summary>
    public AIChatToolWindow()
        : base(null)
    {
        Caption = "🤖 SSMSBoost++ AI 助手";

        StaticLogger?.Information("[AIChatToolWindow] 构造函数被调用，尝试初始化...");

        try
        {
            if (TryInitializeContent())
            {
                StaticLogger?.Information("[AIChatToolWindow] ✅ 构造函数中直接初始化成功");
                return;
            }

            StaticLogger?.Warning("[AIChatToolWindow] ⚠️ 静态服务未就绪，显示占位文本（等待 InitializeContent 兜底）");
        }
        catch (Exception ex)
        {
            StaticLogger?.Error(ex, "[AIChatToolWindow] ❌ 构造函数初始化异常");
        }

        // 兜底：服务未就绪时显示占位文本
        Content = new System.Windows.Controls.TextBlock
        {
            Text = "正在加载 AI 助手...",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.Gray
        };
    }

    /// <summary>
    /// 初始化窗口内容。由 AIChatCommand 在每次打开窗口时调用。
    /// 
    /// 行为逻辑：
    /// 1. 如果 _chatControl 存在且窗口当前可见 → 保持现有会话（用户只是重新激活）
    /// 2. 如果 _chatControl 存在但窗口不可见（曾被关闭）→
    ///    清理旧资源，创建全新的 AIChatControl + ChatSessionService
    /// 3. 如果 _chatControl 为 null（首次打开）→ 创建新内容
    /// </summary>
    public void InitializeContent(IServiceProvider serviceProvider, IConfigurationService configService, ILoggerService logger)
    {
        // 情况1：已有内容且窗口可见 → 保持现有会话
        if (_chatControl != null && IsFrameVisible())
        {
            logger.Debug("[AIChatToolWindow] InitializeContent: 窗口可见，保持现有会话");
            return;
        }

        // 情况2：已有内容但窗口不可见（曾被关闭）→ 清理旧资源
        if (_chatControl != null)
        {
            logger.Information("[AIChatToolWindow] 窗口已被关闭，销毁旧实例并创建全新会话...");
            CleanupInternal();
        }

        // 情况3：创建全新内容
        logger.Information("[AIChatToolWindow] InitializeContent: 正在创建全新的 AIChatControl...");

        try
        {
            var chatSession = new ChatSessionService();
            _chatControl = new AIChatControl(serviceProvider, configService, chatSession, logger);
            Content = _chatControl;
            logger.Information("[AIChatToolWindow] ✅ 全新 AIChatControl 创建成功");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[AIChatToolWindow] ❌ AIChatControl 创建失败");
            // 显示错误信息而非卡在"正在加载"
            Content = new System.Windows.Controls.TextBlock
            {
                Text = $"⚠️ AI 助手加载失败：{ex.Message}",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.Red,
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
        }
    }

    /// <summary>
    /// 检测窗口 Frame 当前是否可见。
    /// 通过 VSFPROPID_IsVisible 属性检查（属性 ID: -7008）。
    /// 用于判断用户是否关闭了窗口（隐藏）还是窗口正在使用中。
    /// </summary>
    private bool IsFrameVisible()
    {
        if (Frame == null) return false;

        try
        {
            var vsFrame = (IVsWindowFrame)Frame;
            object visibility = false;
            // VSFPROPID_IsVisible = -7008（标准 VS SDK 属性 ID）
            vsFrame.GetProperty(-7008, out visibility);
            return visibility is bool isVisible && isVisible;
        }
        catch
        {
            // GetProperty 失败时返回 false，允许 InitializeContent 走重建路径
            return false;
        }
    }

    /// <summary>
    /// 清理内部资源：释放 AIChatControl、取消 AI 请求、清空会话。
    /// </summary>
    private void CleanupInternal()
    {
        if (_chatControl != null)
        {
            try
            {
                _chatControl.Cleanup();
            }
            catch (Exception ex)
            {
                StaticLogger?.Error(ex, "[AIChatToolWindow] Cleanup 异常");
            }
            _chatControl = null;
        }
        Content = null;
    }

    /// <summary>
    /// 尝试使用静态服务引用初始化内容。
    /// </summary>
    private bool TryInitializeContent()
    {
        if (StaticServiceProvider == null || StaticConfigService == null || StaticLogger == null)
            return false;

        var chatSession = new ChatSessionService();
        _chatControl = new AIChatControl(StaticServiceProvider, StaticConfigService, chatSession, StaticLogger);
        Content = _chatControl;
        return true;
    }
}