using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// SQL 模板展开服务。
    /// 拦截 SQL 编辑器中的 TAB 键，如果光标前的单词匹配模板缩写，
    /// 则将缩写替换为模板展开内容，并将光标定位到 $cursor 标记位置。
    ///
    /// 工作原理：
    /// 1. 当 SQL 文档激活时，获取 IWpfTextView 并订阅 PreviewKeyDown
    /// 2. 检测到 TAB 键时，提取光标前的单词
    /// 3. 如果单词匹配模板缩写，替换并定位光标，标记事件为 Handled
    /// 4. 如果不匹配，不拦截（让默认 TAB 行为生效）
    /// </summary>
    public class SqlTemplateExpansionService
    {
        private readonly AsyncPackage _package;
        private readonly ILoggerService _logger;
        private readonly SqlTemplateService _templateService;
        private static readonly HashSet<IWpfTextView> _processedViews = new();
        private static readonly object _lock = new();

        private const string CursorMarker = "$cursor";

        public SqlTemplateExpansionService(
            AsyncPackage package,
            ILoggerService logger,
            SqlTemplateService templateService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
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

                // 监听窗口激活事件，为新打开的 SQL 文档注册 TAB 拦截
                var windowEvents = dte.Events.WindowEvents;
                if (windowEvents != null)
                {
                    windowEvents.WindowActivated += OnWindowActivated;
                    _logger.Information("[SqlTemplate] 已注册 WindowEvents 监听");
                }

                // 立即处理当前活动文档
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
                if (kind != "Document") return;

                string documentName = null;
                try { documentName = gotFocus.Document?.Name; } catch { }
                if (documentName == null) return;

                string ext = System.IO.Path.GetExtension(documentName)?.ToLowerInvariant() ?? "";
                if (ext != ".sql") return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await ProcessActiveDocumentAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] WindowActivated 异常");
            }
        }

        /// <summary>
        /// 处理活动文档：获取 IWpfTextView 并注册 PreviewKeyDown 事件。
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

                lock (_lock)
                {
                    if (_processedViews.Contains(wpfTextView))
                        return;
                    _processedViews.Add(wpfTextView);
                }

                var element = wpfTextView.VisualElement;
                if (element != null)
                {
                    element.PreviewKeyDown -= OnPreviewKeyDown;
                    element.PreviewKeyDown += OnPreviewKeyDown;
                    _logger.Information("[SqlTemplate] ✅ TAB 展开已注册");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 处理活动文档失败");
            }
        }

        /// <summary>
        /// TAB 键拦截处理。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key != Key.Tab)
                    return;

                // 获取 WPF text view
                var element = sender as System.Windows.IInputElement;
                if (element == null) return;

                // 通过 Package 获取组件模型，再获取 text view
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await TryExpandTemplateAsync(e);
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] PreviewKeyDown 异常");
            }
        }

        /// <summary>
        /// 尝试展开模板。如果成功展开，标记 KeyEventArgs 为 Handled。
        /// </summary>
        private async Task TryExpandTemplateAsync(KeyEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var wpfTextView = await GetActiveWpfTextViewAsync();
                if (wpfTextView == null) return;

                var caret = wpfTextView.Caret;
                var position = caret.Position.BufferPosition;

                // 获取当前行光标前的文本
                var line = position.GetContainingLine();
                var lineText = line.Extent.GetText();
                var caretColumn = position.Position - line.Start.Position;

                if (caretColumn <= 0) return;

                // 提取光标前的单词（从光标位置往前找，直到遇到空格/制表符/换行）
                var wordStart = caretColumn;
                while (wordStart > 0 && IsWordChar(lineText[wordStart - 1]))
                {
                    wordStart--;
                }

                if (wordStart == caretColumn) return; // 光标前没有单词

                var word = lineText.Substring(wordStart, caretColumn - wordStart);

                // 尝试匹配模板
                var expansion = _templateService.TryExpand(word);
                if (string.IsNullOrEmpty(expansion))
                    return; // 没有匹配的模板，不拦截

                // 执行替换
                using (var edit = wpfTextView.TextBuffer.CreateEdit())
                {
                    var span = new Span(line.Start.Position + wordStart, word.Length);
                    edit.Replace(span, expansion);
                    edit.Apply();
                }

                // 定位光标
                var newStart = line.Start.Position + wordStart;
                var expansionLength = expansion.Length;

                // 查找 $cursor 标记
                var cursorIndex = expansion.IndexOf(CursorMarker, StringComparison.Ordinal);
                if (cursorIndex >= 0)
                {
                    // 删除 $cursor 标记，将光标定位到该位置
                    var cursorPosition = newStart + cursorIndex;
                    var markerEnd = cursorIndex + CursorMarker.Length;

                    using (var edit = wpfTextView.TextBuffer.CreateEdit())
                    {
                        var markerSpan = new Span(cursorPosition, CursorMarker.Length);
                        edit.Delete(markerSpan);
                        edit.Apply();
                    }

                    // 移动光标
                    wpfTextView.Caret.MoveTo(new SnapshotPoint(wpfTextView.TextBuffer.CurrentSnapshot, cursorPosition));
                }
                else
                {
                    // 没有 $cursor 标记，将光标移到展开内容末尾
                    var endPosition = newStart + expansionLength;
                    wpfTextView.Caret.MoveTo(new SnapshotPoint(wpfTextView.TextBuffer.CurrentSnapshot, endPosition));
                }

                // 标记事件已处理，阻止默认 TAB 行为
                e.Handled = true;

                _logger.Information($"[SqlTemplate] ✅ 展开模板: '{word}' → ({expansion.Length} 字符)");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 展开模板失败");
            }
        }

        /// <summary>
        /// 判断字符是否为单词字符（字母、数字、下划线）。
        /// </summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// 获取当前活动文档的 IWpfTextView。
        /// </summary>
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
    }
}
