using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SSMSBoostPlus.AI.Bridge;
using SSMSBoostPlus.AI.Models;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.CodeAnalysis;
using SSMSBoostPlus.DiffEngine;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.Shared;
using SSMSBoostPlus.SSMS.Services;

namespace SSMSBoostPlus.SSMS.UI.Chat;

/// <summary>
/// AI 聊天面板 WPF UserControl。内嵌于 AIChatToolWindow。
/// 负责：消息渲染、快捷指令、AI 调用编排、操作按钮响应。
/// </summary>
public partial class AIChatControl : UserControl
{
    #region 依赖服务

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationService _configService;
    private readonly IChatSessionService _chatSession;
    private readonly ILoggerService _logger;

    // 仅用于一次性检查状态
    private bool _isInitialized;

    /// <summary>当前 AI 请求的取消令牌源（null 表示无进行中的请求）</summary>
    private CancellationTokenSource? _cts;

    /// <summary>是否正在处理 AI 请求</summary>
    private bool _isBusy;

    #endregion

    #region 构造函数

    public AIChatControl(
        IServiceProvider serviceProvider,
        IConfigurationService configService,
        IChatSessionService chatSession,
        ILoggerService logger)
    {
        InitializeComponent();

        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _chatSession = chatSession ?? throw new ArgumentNullException(nameof(chatSession));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 订阅会话事件
        _chatSession.MessageAdded += OnMessageAdded;
        _chatSession.MessageUpdated += OnMessageUpdated;
        _chatSession.SessionCleared += OnSessionCleared;

        // 绑定空格占位提示
        InputBox.GotFocus += (s, e) =>
        {
            if (InputBox.Text == "输入你的问题...")
                InputBox.Text = string.Empty;
        };
        InputBox.LostFocus += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(InputBox.Text))
                InputBox.Text = "输入你的问题...";
        };
        InputBox.Text = "输入你的问题...";

        // Ctrl+Enter 发送
        InputBox.PreviewKeyDown += InputBox_PreviewKeyDown;

        // 初始化消息列表绑定
        MessagesItemsControl.ItemsSource = _chatSession.Messages;

        Loaded += OnLoaded;
    }

    #endregion

    #region 初始化

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        Loaded -= OnLoaded;
        await RefreshStatusAsync();
    }

    /// <summary>
    /// 刷新状态栏：检测编辑器选中代码和数据库连接。
    /// </summary>
    private async Task RefreshStatusAsync()
    {
        try
        {
            var editorService = new Services.SsmsEditorService(_serviceProvider, _logger);
            var (code, isSelection) = await editorService.GetCodeAsync();

            if (isSelection && !string.IsNullOrWhiteSpace(code))
            {
                var lines = code.Split('\n').Length;
                SelectionInfoText.Text = $"📝 已选中 {lines} 行代码";
            }
            else
            {
                SelectionInfoText.Text = string.Empty;
            }

            // 尝试获取数据库名称
            var dbName = editorService.GetActiveDatabaseName();
            if (!string.IsNullOrEmpty(dbName))
            {
                StatusText.Text = $"就绪 · 数据库: {dbName}";
            }
        }
        catch
        {
            // 状态栏刷新失败不影响主流程
        }
    }

    #endregion

    #region 会话事件

    private void OnMessageAdded(object? sender, ChatMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                // 隐藏空提示
                EmptyHint.Visibility = _chatSession.Messages.Count > 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                // 强制刷新 ItemsControl
                MessagesItemsControl.ItemsSource = null;
                MessagesItemsControl.ItemsSource = _chatSession.Messages;

                // 滚动到底部
                MessageScrollViewer.ScrollToEnd();

                _logger.Debug($"[AIChat] OnMessageAdded 刷新完成 | MsgCount={_chatSession.Messages.Count}, EmptyHint={EmptyHint.Visibility}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[AIChat] OnMessageAdded UI 刷新异常");
                // 确保至少 EmptyHint 被隐藏
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        });
    }

    /// <summary>
    /// 上次 UI 刷新时间（用于流式节流，避免高频刷新导致卡顿）。
    /// </summary>
    private DateTime _lastUiRefresh = DateTime.MinValue;

    /// <summary>
    /// 流式消息内容更新事件处理（由 ChatSessionService.MessageUpdated 触发）。
    /// 实时更新消息气泡的显示内容（文本 + 思考区域）。
    /// </summary>
    private void OnMessageUpdated(object? sender, ChatMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                // 节流：至少间隔 60ms 才刷新一次 UI
                var now = DateTime.Now;
                if ((now - _lastUiRefresh).TotalMilliseconds < 60 && message.IsStreaming)
                    return;

                _lastUiRefresh = now;

                // 强制刷新 ItemsControl 以反映 Content / ThinkingContent 的变更
                MessagesItemsControl.ItemsSource = null;
                MessagesItemsControl.ItemsSource = _chatSession.Messages;

                // 仅在消息是流式更新的最后部分时滚动到底部（避免频繁滚动）
                MessageScrollViewer.ScrollToEnd();

                _logger.Debug($"[AIChat] OnMessageUpdated 刷新 | MsgId={message.Id.Substring(0, 8)}..., Streaming={message.IsStreaming}, ContentLen={message.Content?.Length ?? 0}, ThinkLen={message.ThinkingContent?.Length ?? 0}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[AIChat] OnMessageUpdated UI 刷新异常");
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnSessionCleared(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            EmptyHint.Visibility = Visibility.Visible;
            MessagesItemsControl.ItemsSource = null;
            MessagesItemsControl.ItemsSource = _chatSession.Messages;
        });
    }

    #endregion

    #region 消息发送

    /// <summary>
    /// 取消按钮点击：中断当前 AI 请求。
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AddSystemMessage("⏹ 已取消 AI 请求。");
        SetStatus("已取消");
    }

    /// <summary>
    /// 切换 UI 繁忙/空闲模式。
    /// 繁忙时：显示取消按钮、打字指示器、锁定输入框、禁用快捷按钮。
    /// 空闲时：恢复发送按钮、隐藏打字指示器、启用输入框。
    /// </summary>
    private void SetBusyMode(bool busy)
    {
        _isBusy = busy;

        // 发送/取消按钮切换
        SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // 输入框锁定
        InputBox.IsReadOnly = busy;
        InputBox.Background = busy
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9))
            : System.Windows.Media.Brushes.White;
        InputBox.Opacity = busy ? 0.7 : 1.0;

        // 打字指示器
        TypingIndicatorControl.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // 滚动到底部以显示打字指示器
        if (busy)
        {
            Dispatcher.BeginInvoke(new Action(() => MessageScrollViewer.ScrollToEnd()));
        }
    }

    /// <summary>
    /// 发送按钮点击。
    /// </summary>
    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendMessage();
    }

    /// <summary>
    /// 键盘事件：Ctrl+Enter 发送消息。
    /// </summary>
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SendMessage();
        }
    }

    /// <summary>
    /// 发送消息主流程（流式版本）。
    /// 使用 ExecuteStreamAsync 实时逐块接收 AI 返回内容，
    /// 区分思考过程（Thinking）和正式回答（Text），分别更新 ThinkingContent 和 Content。
    /// </summary>
    private async void SendMessage()
    {
        if (_isBusy) return; // 防止重复发送

        var userText = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(userText) || userText == "输入你的问题...")
            return;

        // 清空输入框
        InputBox.Text = string.Empty;

        // 获取编辑器选中代码
        var editorService = new Services.SsmsEditorService(_serviceProvider, _logger);
        var (code, isSelection) = await editorService.GetCodeAsync();
        var attachedCode = isSelection ? code : null;

        // 添加用户消息
        var userMessage = new ChatMessage
        {
            Role = ChatRole.User,
            Content = userText,
            AttachedCode = attachedCode
        };
        _chatSession.AddMessage(userMessage);

        // 检查 AI 配置
        if (!await _configService.IsAIConfiguredAsync())
        {
            AddSystemMessage("⚠️ AI 尚未配置。请通过菜单 SSMSBoost++ → ⚙ AI 配置... 设置 AI 后端。");
            return;
        }

        // 进入繁忙模式：显示打字指示器 + 取消按钮 + 锁定输入
        SetBusyMode(true);
        SetStatus("🤖 正在连接 AI 服务...");

        // 创建取消令牌
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 创建占位 AI 消息（流式过程中实时更新）
        var aiMessage = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = string.Empty,
            ThinkingContent = null,
            IsStreaming = true
        };
        _chatSession.AddMessage(aiMessage);
        var aiMessageId = aiMessage.Id;

        // 累积缓冲
        var thinkingBuffer = new StringBuilder();
        var contentBuffer = new StringBuilder();
        var isInThinking = false;

        try
        {
            // 创建 AI 桥接
            token.ThrowIfCancellationRequested();
            var aiConfig = await _configService.GetAIConfigurationAsync();
            SetStatus("📝 正在分析...");

            var bridge = await BridgeFactory.CreateBestAvailableBridgeAsync(aiConfig, _logger);

            token.ThrowIfCancellationRequested();
            SetStatus("💡 正在生成回复...");

            // 构建 prompt
            var userPrompt = userText;
            if (!string.IsNullOrWhiteSpace(code))
            {
                userPrompt = $"当前编辑器中的 SQL 代码:\n```sql\n{code}\n```\n\n用户问题: {userText}";
            }

            // 构建 AIRequest（只读模式：仅输出建议，不修改文件）
            var request = new AIRequest
            {
                Template = PromptTemplate.Chat,
                Code = userPrompt,
                ReadOnly = true
            };

            // 流式调用 AI ─────────────────────────────────────────────
            await foreach (var chunk in bridge.ExecuteStreamAsync(request, token))
            {
                token.ThrowIfCancellationRequested();

                switch (chunk.ChunkType)
                {
                    case AIStreamChunkType.ThinkingStart:
                        isInThinking = true;
                        break;

                    case AIStreamChunkType.Thinking:
                        thinkingBuffer.Append(chunk.Content);
                        // 实时更新思考内容
                        _chatSession.UpdateMessage(aiMessageId, contentBuffer.ToString(), thinkingBuffer.ToString());
                        break;

                    case AIStreamChunkType.ThinkingEnd:
                        isInThinking = false;
                        break;

                    case AIStreamChunkType.Text:
                        contentBuffer.Append(chunk.Content);
                        // 实时更新正式回答内容
                        _chatSession.UpdateMessage(aiMessageId, contentBuffer.ToString(), isInThinking ? thinkingBuffer.ToString() : null);
                        break;

                    case AIStreamChunkType.Error:
                        _logger.Error($"[AIChat] 流式错误: {chunk.Content}");
                        AddSystemMessage($"❌ AI 流式错误: {chunk.Content}");
                        break;

                    case AIStreamChunkType.Complete:
                        // 流式完成标记，退出循环
                        break;
                }

                if (chunk.IsComplete || chunk.ChunkType == AIStreamChunkType.Complete)
                    break;
            }

            // ── 流式完成后的处理 ────────────────────────────────────
            token.ThrowIfCancellationRequested();

            var finalContent = contentBuffer.ToString();
            var finalThinking = thinkingBuffer.Length > 0 ? thinkingBuffer.ToString() : null;

            if (string.IsNullOrWhiteSpace(finalContent) && string.IsNullOrWhiteSpace(finalThinking))
            {
                AddSystemMessage("❌ AI 返回内容为空。");
                return;
            }

            // 提取 SQL 代码块生成操作按钮（在完整内容上操作）
            var actions = ExtractCodeActions(code ?? string.Empty, finalContent);

            // 最终更新：设置完整内容和操作按钮
            var finalMessage = _chatSession.Messages.FirstOrDefault(m => m.Id == aiMessageId);
            if (finalMessage != null)
            {
                finalMessage.Content = finalContent;
                finalMessage.ThinkingContent = finalThinking;
                finalMessage.Actions = actions;
                finalMessage.IsStreaming = false;

                // 通过 CompleteMessage 通知 UI 最后一次刷新
                _chatSession.CompleteMessage(aiMessageId);
            }

            SetStatus($"✅ 完成 · 耗时 {DateTime.Now.Subtract(userMessage.Timestamp).TotalSeconds:F1}s");
        }
        catch (OperationCanceledException)
        {
            // 用户取消，已在上层添加提示消息
            _logger.Information("[AIChat] AI 请求被用户取消");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[AIChat] 发送消息失败");
            AddSystemMessage($"❌ 发生异常: {ex.Message}");
        }
        finally
        {
            // 释放取消令牌源
            _cts?.Dispose();
            _cts = null;

            // 退出繁忙模式
            SetBusyMode(false);

            if (StatusText.Text == "已取消")
            {
                // 保持取消状态文本
            }
            else if (StatusText.Text.StartsWith("✅") || StatusText.Text.StartsWith("❌"))
            {
                // 保持完成/失败状态文本
            }
            else
            {
                SetStatus("就绪");
            }
        }
    }

    /// <summary>
    /// 从 AI 回答的 Markdown 中提取 SQL 代码块，生成操作按钮。
    /// 匹配 ```sql ... ``` 或 ``` ... ``` 模式。
    /// </summary>
    private static List<ChatAction> ExtractCodeActions(string originalCode, string aiContent)
    {
        var actions = new List<ChatAction>();

        if (string.IsNullOrWhiteSpace(aiContent))
            return actions;

        // 匹配 Markdown 代码块: ```sql ... ``` 或 ``` ... ```
        var matches = Regex.Matches(aiContent,
            @"```(?:sql)?\s*\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var code = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(code))
                continue;

            // 判断是否是完整 SQL 语句（非 EXPLAIN/查询计划文本）
            if (IsLikelyExecutableSql(code))
            {
                actions.Add(new ChatAction
                {
                    Type = ChatActionType.ApplyCode,
                    Label = "📋 应用此修改",
                    OriginalCode = originalCode,
                    SuggestedCode = code
                });
            }
            else
            {
                actions.Add(new ChatAction
                {
                    Type = ChatActionType.CopyCode,
                    Label = "📝 复制代码",
                    SuggestedCode = code
                });
            }

            // 最多生成 3 个按钮避免 UI 溢出
            if (actions.Count >= 3)
                break;
        }

        return actions;
    }

    /// <summary>
    /// 启发式判断代码块是否是可能可执行的 SQL。
    /// </summary>
    private static bool IsLikelyExecutableSql(string code)
    {
        var upper = code.ToUpperInvariant().Trim();
        return upper.StartsWith("SELECT") || upper.StartsWith("WITH")
            || upper.StartsWith("CREATE") || upper.StartsWith("ALTER")
            || upper.StartsWith("DROP") || upper.StartsWith("INSERT")
            || upper.StartsWith("UPDATE") || upper.StartsWith("DELETE")
            || upper.StartsWith("EXEC") || upper.StartsWith("MERGE");
    }

    /// <summary>
    /// 添加系统提示消息（错误/警告）。
    /// </summary>
    private void AddSystemMessage(string text)
    {
        _chatSession.AddMessage(new ChatMessage
        {
            Role = ChatRole.System,
            Content = text
        });
    }

    /// <summary>
    /// 设置状态栏文本，null 恢复默认。
    /// </summary>
    private void SetStatus(string? text)
    {
        Dispatcher.Invoke(() =>
        {
            if (text != null)
            {
                StatusText.Text = text;
            }
            else
            {
                StatusText.Text = "就绪";
            }
        });
    }

    #endregion

    #region 快捷按钮

    /// <summary>
    /// 快捷按钮：代码建议。
    /// </summary>
    private async void QuickSuggest_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteQuickAction("建议", PromptTemplate.Suggest, "请审查以下 SQL 代码并给出改进建议（语法错误、代码精简、性能优化）：");
    }

    /// <summary>
    /// 快捷按钮：注释生成。
    /// </summary>
    private async void QuickComment_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteQuickAction("注释", PromptTemplate.Comment, "请为以下 SQL 代码添加中文注释：");
    }

    /// <summary>
    /// 快捷按钮：格式化。
    /// </summary>
    private async void QuickFormat_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteQuickAction("格式化", PromptTemplate.Format, "请使用 Microsoft 风格格式化以下 SQL 代码：");
    }

    /// <summary>
    /// 快捷按钮：性能分析。
    /// </summary>
    private async void QuickAnalyze_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteQuickAction("性能分析", PromptTemplate.Analyze, "请对以下 SQL 代码进行性能分析（执行计划、缺失索引、瓶颈、优化方案）：");
    }

    /// <summary>
    /// 快捷按钮：清空对话。
    /// </summary>
    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _chatSession.Clear();
        RefreshStatusAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 快捷指令的执行模板：获取代码 → 检查配置 → 构建 Prompt → 调用 AI → 解析结果。
    /// 支持取消令牌、打字指示器、输入锁定。
    /// </summary>
    private async Task ExecuteQuickAction(string actionName, PromptTemplate template, string promptPrefix)
    {
        if (_isBusy) return; // 防止重复执行

        var editorService = new Services.SsmsEditorService(_serviceProvider, _logger);
        var (code, isSelection) = await editorService.GetCodeAsync();

        if (string.IsNullOrWhiteSpace(code))
        {
            AddSystemMessage("⚠️ 请先在编辑器中选中 SQL 代码。");
            return;
        }

        if (!await _configService.IsAIConfiguredAsync())
        {
            AddSystemMessage("⚠️ AI 尚未配置。请通过菜单 SSMSBoost++ → ⚙ AI 配置... 设置。");
            return;
        }

        // 添加用户消息（以快捷指令形式）
        var userMessage = new ChatMessage
        {
            Role = ChatRole.User,
            Content = $"{promptPrefix}",
            AttachedCode = code
        };
        _chatSession.AddMessage(userMessage);

        // 进入繁忙模式
        SetBusyMode(true);
        SetStatus($"🤖 正在执行「{actionName}」...");

        // 创建取消令牌
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            token.ThrowIfCancellationRequested();
            var aiConfig = await _configService.GetAIConfigurationAsync();
            var bridge = await BridgeFactory.CreateBestAvailableBridgeAsync(aiConfig, _logger);
            var diffCalculator = new DiffCalculator();
            var promptManager = new PromptTemplateManager();

            var analysisService = new CodeAnalysisService(
                bridge, promptManager, diffCalculator, _configService, _logger);

            // 获取数据库上下文
            var dbName = editorService.GetActiveDatabaseName();

            token.ThrowIfCancellationRequested();

            // 根据模板类型调用对应的分析方法
            CodeAnalysis.Models.AnalysisResult result = template switch
            {
                PromptTemplate.Suggest => await analysisService.GetSuggestionsAsync(code, databaseName: dbName),
                PromptTemplate.Comment => await analysisService.AddCommentsAsync(code, databaseName: dbName),
                PromptTemplate.Format => await analysisService.FormatCodeAsync(code, databaseName: dbName),
                PromptTemplate.Analyze => await analysisService.AnalyzePerformanceAsync(code, databaseName: dbName),
                _ => throw new ArgumentOutOfRangeException(nameof(template))
            };

            token.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                AddSystemMessage($"❌ 「{actionName}」失败: {result.ErrorMessage}");
                return;
            }

            // 构建 AI 回答内容和操作按钮
            var aiContent = result.Content ?? string.Empty;
            var suggestedCode = result.SuggestedCode;
            var actions = new List<ChatAction>();

            if (!string.IsNullOrWhiteSpace(suggestedCode))
            {
                actions.Add(new ChatAction
                {
                    Type = ChatActionType.ApplyCode,
                    Label = "📋 应用此修改",
                    OriginalCode = code,
                    SuggestedCode = suggestedCode
                });
            }

            var aiMessage = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = aiContent,
                Actions = actions
            };
            _chatSession.AddMessage(aiMessage);

            // 🔧 双重保险：强制刷新 UI（防止 OnMessageAdded 事件因 WPF 布局/ActiveX 时序问题未生效）
            Dispatcher.Invoke(() =>
            {
                EmptyHint.Visibility = Visibility.Collapsed;
                MessagesItemsControl.ItemsSource = null;
                MessagesItemsControl.ItemsSource = _chatSession.Messages;
                MessageScrollViewer.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);

            SetStatus($"✅ 「{actionName}」完成 · 耗时 {result.ElapsedMs}ms");
        }
        catch (OperationCanceledException)
        {
            _logger.Information($"[AIChat] 快捷指令「{actionName}」被用户取消");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"[AIChat] 快捷指令「{actionName}」失败");
            AddSystemMessage($"❌ 「{actionName}」异常: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusyMode(false);

            if (StatusText.Text.Contains("取消"))
            {
                // 保持取消状态文本
            }
            else if (!StatusText.Text.StartsWith("✅"))
            {
                SetStatus("就绪");
            }
        }
    }

    #endregion

    #region 操作按钮

    /// <summary>
    /// AI 消息中的操作按钮点击事件（如"应用此修改"）。
    /// </summary>
    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ChatAction action)
            return;

        switch (action.Type)
        {
            case ChatActionType.ApplyCode:
                _ = ApplyCodeAsync(action);
                break;

            case ChatActionType.CopyCode:
                if (!string.IsNullOrWhiteSpace(action.SuggestedCode))
                {
                    Clipboard.SetText(action.SuggestedCode);
                    AddSystemMessage("📋 代码已复制到剪贴板。");
                }
                break;
        }
    }

    /// <summary>
    /// 弹出 Diff 对话框，用户确认后替换编辑器选中文本。
    /// </summary>
    private async Task ApplyCodeAsync(ChatAction action)
    {
        if (string.IsNullOrWhiteSpace(action.SuggestedCode))
            return;

        var originalCode = action.OriginalCode ?? string.Empty;

        // 弹出 Diff 对比对话框
        var dialogService = new SsmsDialogService(_serviceProvider);
        var diffResult = await dialogService.ShowDiffDialog(
            originalCode,
            action.SuggestedCode,
            "AI 建议修改 — 对比确认");

        if (diffResult == DiffDialogResult.AcceptRight || diffResult == DiffDialogResult.AcceptLeft)
        {
            var editorService = new Services.SsmsEditorService(_serviceProvider, _logger);

            if (diffResult == DiffDialogResult.AcceptRight)
            {
                await editorService.ReplaceSelectionAsync(action.SuggestedCode);
                AddSystemMessage("✅ AI 修改建议已应用到编辑器。");
            }
            else
            {
                // AcceptLeft: 用户选择保留原始版本（不做修改）
                AddSystemMessage("已保留原始代码。");
            }

            await RefreshStatusAsync();
        }
    }

    #endregion

    #region 资源清理

    /// <summary>
    /// 清理资源：取消进行中的请求、释放令牌、清空会话。
    /// 窗口关闭时由 AIChatToolWindow 调用。
    /// </summary>
    public void Cleanup()
    {
        // 取消进行中的 AI 请求
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _isBusy = false;

        // 清空会话消息
        _chatSession.Clear();

        // 取消会话事件订阅
        _chatSession.MessageAdded -= OnMessageAdded;
        _chatSession.MessageUpdated -= OnMessageUpdated;
        _chatSession.SessionCleared -= OnSessionCleared;

        // 取消键盘事件订阅
        InputBox.PreviewKeyDown -= InputBox_PreviewKeyDown;
    }

    #endregion
}