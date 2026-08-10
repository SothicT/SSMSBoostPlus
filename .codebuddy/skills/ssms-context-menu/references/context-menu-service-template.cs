// =============================================================================
// ContextMenuService — Complete Implementation Template
// For SSMS 22+ SQL Editor Right-Click Context Menu
// =============================================================================
//
// Usage:
//   1. Copy this file to your project: src/Services/ContextMenuService.cs
//   2. Update CommandSetGuid to match your VSCT guidSSMSBoostPlusCmdSet
//   3. Update command IDs and display names
//   4. Update TagPrefix to your extension name
//   5. Call from Package after DTE is available
//
// CRITICAL: This service MUST be called AFTER DTE becomes available!
// See references/package-integration-template.cs for how to integrate.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace YourExtension.Services
{
    /// <summary>
    /// SQL editor right-click context menu service.
    ///
    /// Core strategy: DTE CommandBars runtime dynamic addition.
    ///
    /// Workflow:
    /// 1. Enumerate all DTE CommandBars, find SQL editor context menu
    /// 2. Add custom commands via DTE CommandBars API
    /// 3. Register WPF diagnostic events for verification
    ///
    /// Proven non-working approaches (DO NOT USE):
    ///   ❌ VSCT CommandPlacement to guidSHLMainMenu/IDM_VS_CTXT_CODEWIN
    ///   ❌ VSCT CommandPlacement to {5DD0BB59-...}/0x0100
    ///   ❌ WPF ContextMenu / ContextMenuOpening
    ///   ❌ IOleCommandTarget.ShowContextMenu interception
    /// </summary>
    internal class ContextMenuService
    {
        private readonly AsyncPackage _package;
        private readonly ILoggerService _logger;
        private const string LogPrefix = "[ContextMenu]";

        // ⚠️ MUST match your VSCT guidSSMSBoostPlusCmdSet value!
        private static readonly Guid CommandSetGuid = new("YOUR-COMMAND-SET-GUID-HERE");

        // ⚠️ MUST match your VSCT IDSymbol values!
        private const int CmdIdCommand1 = 0x0200;
        private const int CmdIdCommand2 = 0x0201;
        private const int CmdIdCommand3 = 0x0202;
        // Add more command IDs as needed...

        // Tag prefix for cleanup of residual menu items
        // Change "YourExt_" to your extension's unique prefix
        private const string TagPrefix = "YourExt_Command_";

        // Define all commands to add to the context menu
        private static readonly (int CommandId, string DisplayName, string Tag)[] Commands = new[]
        {
            (CmdIdCommand1, "🤖 Command 1", TagPrefix + "Cmd1"),
            (CmdIdCommand2, "🤖 Command 2", TagPrefix + "Cmd2"),
            (CmdIdCommand3, "🤖 Command 3", TagPrefix + "Cmd3"),
            // Add more commands as needed...
        };

        // Processed text view cache
        private static readonly HashSet<IWpfTextView> _processedViews = new HashSet<IWpfTextView>();
        private static readonly object _lock = new object();

        private bool _isInitialized = false;
        private bool _hasAddedMenuItems = false;

        public ContextMenuService(AsyncPackage package, ILoggerService logger)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Initialize: enumerate CommandBars → find SQL editor menu → add commands
        /// </summary>
        public async System.Threading.Tasks.Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isInitialized)
            {
                _logger.Debug($"{LogPrefix} Already initialized, skipping");
                return;
            }

            _logger.Information($"{LogPrefix} === Starting initialization ===");

            try
            {
                // Step 1: Get DTE
                var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
                if (dte == null)
                {
                    _logger.Warning($"{LogPrefix} ❌ DTE not available");
                    return;
                }

                // Step 2: Enumerate CommandBars and find SQL editor context menu
                _logger.Information($"{LogPrefix} [Step 1] Enumerating DTE CommandBars...");
                var sqlEditorMenu = EnumerateAndFindContextMenu(dte);

                // Step 3: Add commands if found
                if (sqlEditorMenu != null)
                {
                    _logger.Information($"{LogPrefix} [Step 2] ✅ Found SQL editor context menu, adding commands...");
                    AddCommandsToMenu(sqlEditorMenu);
                    _hasAddedMenuItems = true;
                }
                else
                {
                    _logger.Warning($"{LogPrefix} [Step 2] ❌ SQL editor context menu not found");
                }

                // Step 4: Register WindowEvents for document switching
                var windowEvents = dte.Events.WindowEvents;
                if (windowEvents != null)
                {
                    windowEvents.WindowActivated += OnWindowActivated;
                    _logger.Information($"{LogPrefix} ✅ WindowEvents registered");
                }

                // Process currently active document
                await ProcessActiveDocumentAsync();

                _isInitialized = true;
                _logger.Information($"{LogPrefix} === Initialization complete === MenuItemsAdded={_hasAddedMenuItems}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ Initialization failed");
            }
        }

        #region CommandBars Enumeration and Discovery

        /// <summary>
        /// Enumerate all DTE CommandBars and find the SQL editor right-click context menu.
        /// </summary>
        private dynamic EnumerateAndFindContextMenu(DTE dte)
        {
            try
            {
                var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
                if (commandBars == null)
                {
                    _logger.Warning($"{LogPrefix} ❌ CommandBars is null");
                    return null;
                }

                int totalCount = commandBars.Count;
                _logger.Information($"{LogPrefix} CommandBars total count: {totalCount}");

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
                            // Log all popup menus for diagnostics
                            _logger.Information($"{LogPrefix}   Popup menu [{i}]: '{name}' (Controls={controlCount})");

                            var nameUpper = name.ToUpperInvariant();
                            if (nameUpper.Contains("SQL") || nameUpper.Contains("EDITOR") ||
                                nameUpper.Contains("CODE") || nameUpper.Contains("CONTEXT") ||
                                nameUpper.Contains("QUERY") || nameUpper.Contains("TEXT"))
                            {
                                sqlRelatedMenus.Add((i, name, controlCount));
                            }
                        }
                    }
                    catch (Exception exBar)
                    {
                        _logger.Debug($"{LogPrefix}   Enumerate [{i}] error: {exBar.Message}");
                    }
                }

                _logger.Information($"{LogPrefix} === Enumeration complete: {sqlRelatedMenus.Count} SQL/Editor related menus found ===");

                return TryFindContextMenu(commandBars, sqlRelatedMenus);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ CommandBars enumeration failed");
                return null;
            }
        }

        /// <summary>
        /// Try to find the SQL editor context menu by priority.
        /// </summary>
        private dynamic TryFindContextMenu(
            Microsoft.VisualStudio.CommandBars.CommandBars commandBars,
            List<(int Index, string Name, int ControlCount)> sqlRelatedMenus)
        {
            // ⭐⭐⭐ CRITICAL PRIORITY LIST ⭐⭐⭐
            // "SQL Files Editor Context" = SSMS 22 SQL query editor right-click menu
            // "Code Window" = Generic VS code editor (NOT SQL editor!)
            string[] priorityNames = new string[]
            {
                "SQL Files Editor Context",     // ⭐⭐⭐ SSMS 22 SQL query editor right-click menu
                "Code Window",                  // Generic VS code editor (NOT SQL editor)
                "Sql Editor Context",           // SSMS Plus project name (may not exist in SSMS 22)
                "SQL Editor Context",
                "Editor Context Menus",
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

                        _logger.Information($"{LogPrefix} ✅ Found by name: '{name}' (Controls={ctrlCount})");
                        LogMenuControls(menu);
                        return menu;
                    }
                }
                catch
                {
                    _logger.Debug($"{LogPrefix}   '{name}' not found");
                }
            }

            // Fallback: pick from SQL-related menus by control count
            foreach (var (idx, name, ctrlCount) in sqlRelatedMenus.OrderByDescending(x => x.ControlCount))
            {
                if (ctrlCount >= 5 && ctrlCount <= 60)
                {
                    try
                    {
                        var menu = commandBars[name];
                        if (menu != null)
                        {
                            _logger.Information($"{LogPrefix} ✅ Fallback selection: '{name}' (Controls={ctrlCount})");
                            LogMenuControls(menu);
                            return menu;
                        }
                    }
                    catch { }
                }
            }

            _logger.Warning($"{LogPrefix} ❌ No suitable context menu found");
            return null;
        }

        /// <summary>
        /// Log menu controls to help verify the correct menu was found.
        /// </summary>
        private void LogMenuControls(dynamic menu)
        {
            try
            {
                var controls = menu.Controls;
                int count = 0;
                try { count = controls.Count; } catch { }

                _logger.Information($"{LogPrefix}   Menu controls ({count} items):");
                int maxLog = Math.Min(count, 30);
                for (int i = 1; i <= maxLog; i++)
                {
                    try
                    {
                        var ctrl = controls[i];
                        string caption = "";
                        try { caption = ctrl.Caption ?? ""; } catch { }
                        if (!string.IsNullOrEmpty(caption))
                            _logger.Information($"{LogPrefix}     [{i}] '{caption}'");
                        else
                            _logger.Information($"{LogPrefix}     [{i}] (separator)");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"{LogPrefix}   Log controls failed: {ex.Message}");
            }
        }

        #endregion

        #region DTE CommandBars Add Menu Items

        /// <summary>
        /// Add custom commands to the context menu via DTE CommandBars API.
        /// </summary>
        private void AddCommandsToMenu(dynamic contextMenu)
        {
            try
            {
                // 1. Clean up residual items first
                int cleaned = CleanupExistingItems(contextMenu);
                _logger.Information($"{LogPrefix} Cleaned up {cleaned} residual items");

                // 2. Add each command
                int addedCount = 0;
                foreach (var (cmdId, displayName, tag) in Commands)
                {
                    if (AddCommandToMenu(contextMenu, cmdId, displayName, tag))
                    {
                        addedCount++;
                    }
                }

                _logger.Information($"{LogPrefix} ✅✅ Commands added: {addedCount}/{Commands.Length}");

                // 3. Verify result
                try
                {
                    var controls = contextMenu.Controls;
                    int finalCount = 0;
                    try { finalCount = controls.Count; } catch { }
                    _logger.Information($"{LogPrefix} Total menu controls after adding: {finalCount}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ Add commands failed");
            }
        }

        /// <summary>
        /// Add a single command button to the menu.
        /// </summary>
        private bool AddCommandToMenu(dynamic contextMenu, int commandId, string displayName, string tag)
        {
            try
            {
                var controls = contextMenu.Controls;

                // Controls.Add(Type, Id, Parameter, Before, Temporary)
                var control = controls.Add(
                    1,               // Type: msoControlButton
                    commandId,       // Id: MUST match VSCT IDSymbol value!
                    Type.Missing,    // Parameter
                    Type.Missing,    // Before (add to end)
                    true             // Temporary: auto-cleanup on SSMS close
                );

                if (control != null)
                {
                    try { control.Caption = displayName; } catch { }
                    try { control.Tag = tag; } catch { }
                    try { control.Visible = true; } catch { }

                    _logger.Information($"{LogPrefix}   ✅ Added: '{displayName}' (CmdID=0x{commandId:X4})");
                    return true;
                }
                else
                {
                    _logger.Warning($"{LogPrefix}   ⚠️ Add returned null: '{displayName}'");
                    return false;
                }
            }
            catch (COMException comEx)
            {
                _logger.Warning($"{LogPrefix}   ❌ COM error adding '{displayName}': {comEx.Message} (0x{comEx.ErrorCode:X8})");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning($"{LogPrefix}   ❌ Add failed '{displayName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clean up residual menu items (Tag starts with TagPrefix).
        /// Must iterate backwards to avoid index shift!
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
                            _logger.Debug($"{LogPrefix}   Deleting residual: [{i}] tag='{tag}'");
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

        #endregion

        #region WindowEvents and WPF Diagnostics

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

                _logger.Debug($"{LogPrefix} SQL document activated: '{documentName}'");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await ProcessActiveDocumentAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} WindowActivated error");
            }
        }

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

                var element = wpfTextView.VisualElement;
                if (element != null)
                {
                    element.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
                    element.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
                    _logger.Information($"{LogPrefix} ✅ PreviewMouseRightButtonDown diagnostic registered");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"{LogPrefix} ❌ Process active document failed");
            }
        }

        private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _logger.Information($"{LogPrefix} 📌 PreviewMouseRightButtonDown triggered");
            _logger.Information($"{LogPrefix} → Commands added via DTE CommandBars: {_hasAddedMenuItems}");
            // Do NOT mark Handled — let SSMS show its context menu normally
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
