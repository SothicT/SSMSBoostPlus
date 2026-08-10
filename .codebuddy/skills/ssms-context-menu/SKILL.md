---
name: ssms-context-menu
description: >
  This skill should be used when developing VSIX extensions for SSMS (SQL Server Management Studio)
  that need to add custom menu items to the SQL editor right-click context menu. It provides the
  proven DTE CommandBars runtime approach for SSMS 22+, covers all known pitfalls (VSCT
  CommandPlacement does NOT work for SQL editor context menu), and includes a complete implementation
  template. Trigger when: adding right-click menu items in SSMS SQL editor, SSMS context menu extension,
  SSMS右键菜单, SQL editor context menu, SSMS extension context menu, CommandBars dynamic menu.
---

# SSMS SQL Editor Context Menu Skill

## Purpose

Add custom menu items to the SSMS SQL query editor right-click context menu. This skill provides the
only proven working approach for SSMS 22+ (based on Visual Studio 2022 Shell), along with a complete
implementation template and diagnostic methods.

## Critical Knowledge: What Does NOT Work

These approaches have been **empirically proven to fail** for SSMS SQL editor context menu:

| Approach | Why It Fails |
|----------|-------------|
| VSCT `CommandPlacement` to `guidSHLMainMenu / IDM_VS_CTXT_CODEWIN` | `BeforeQueryStatus` is never called — VS Shell never queries commands for this GUID in the SQL editor |
| VSCT `CommandPlacement` to `{5DD0BB59-7076-4C59-88D3-DE36931F63F0}/0x0100` | Same issue — `BeforeQueryStatus` never fires |
| `CommandBars["Code Window"]` | This is the **generic code editor** context menu (contains "Bookmark", "Snippets", "Outline") — NOT the SQL editor context menu |
| WPF `ContextMenu` / `ContextMenuOpening` event | SSMS does not use WPF ContextMenu for its right-click menus |
| `IOleCommandTarget.ShowContextMenu` interception | SSMS does not use this command routing for context menus |

## The Only Working Approach: DTE CommandBars Runtime Dynamic Addition

### Core Principle

Use `EnvDTE.DTE.CommandBars` automation model to find the SQL editor context menu by name at runtime,
then call `Controls.Add()` to add menu items dynamically. This bypasses VSCT's declarative mechanism
entirely.

### SSMS 22 SQL Editor Context Menu Identity

| Property | Value |
|----------|-------|
| DTE CommandBars Name | `SQL Files Editor Context` |
| Index | 119 |
| Control Count | 48 (includes Cut/Copy/Paste/Connect/etc.) |
| Menu Type | `msoBarTypePopup` |

**Important distinction**: `Code Window` (index 12, 25 controls) is the generic VS code editor context
menu, NOT the SQL query editor context menu. Always use `SQL Files Editor Context` first.

## Implementation Workflow

### Step 1: Define Commands in VSCT

Commands must be defined in VSCT for OleMenuCommandService registration and keyboard shortcuts.
VSCT CommandPlacement to context menus will NOT work, but the command definitions are still required.

```xml
<GuidSymbol name="guidMyCmdSet" value="{YOUR-GUID-HERE}">
  <IDSymbol name="cmdidMyCommand1" value="0x0200"/>
  <IDSymbol name="cmdidMyCommand2" value="0x0201"/>
</GuidSymbol>
```

Also define Button entries with `DynamicVisibility` flag and KeyBindings as needed.

### Step 2: Register Commands via OleMenuCommandService

Each command must be registered in Package.InitializeAsync(), or clicking the menu item will do nothing:

```csharp
var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
var cmdId = new CommandID(CommandSetGuid, 0x0200);
var cmd = new OleMenuCommand(Execute, cmdId);
cmd.BeforeQueryStatus += OnBeforeQueryStatus;
commandService.AddCommand(cmd);
```

### Step 3: Create ContextMenuService

Create a service class that handles the DTE CommandBars enumeration and dynamic menu addition.
See `references/context-menu-service-template.cs` for the complete implementation template.

Key aspects of the service:

1. **Wait for DTE availability**: DTE is only available after Shell initialization. Poll for up to
   15 seconds in `RunDiagnosticAfterShellInitialized()`.

2. **Enumerate and find the correct CommandBar**: Use a priority list of known names:

   ```csharp
   string[] priorityNames = new string[]
   {
       "SQL Files Editor Context",     // SSMS 22 SQL query editor (highest priority)
       "Code Window",                  // Generic code editor (fallback, not SQL editor)
       "Sql Editor Context",           // SSMS Plus project name (may not exist)
       "SQL Editor Context",
       "Editor Context Menus",
       "Query Editor Context Menu",
       "Text Editor Context",
   };
   ```

3. **Add menu items via Controls.Add()**:

   ```csharp
   var control = controls.Add(
       1,               // Type: msoControlButton
       commandId,       // Id: MUST match VSCT IDSymbol value
       Type.Missing,    // Parameter
       Type.Missing,    // Before (add to end)
       true             // Temporary: auto-cleanup on SSMS close
   );
   control.Caption = displayName;
   control.Tag = tag;   // Tag prefix for cleanup of residual items
   control.Visible = true;
   ```

4. **Clean up residual items** before adding new ones (iterate backwards to avoid index shift):

   ```csharp
   for (int i = controls.Count; i >= 1; i--)
   {
       var ctrl = controls[i];
       string tag = ctrl.Tag ?? "";
       if (tag.StartsWith(TagPrefix))
           ctrl.Delete();
   }
   ```

### Step 4: Call from Package Initialization

The context menu service must be called after DTE becomes available:

```csharp
// In Package.InitializeAsync():
if (ssmsVersion >= SSMSVersion.V22)
{
    await RunDiagnosticAfterShellInitialized(logger);
}

private async Task RunDiagnosticAfterShellInitialized(ILoggerService logger)
{
    for (int i = 0; i < 15; i++)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await GetServiceAsync(typeof(DTE)) as DTE;
        if (dte != null)
        {
            // Other DTE-dependent initialization...
            RegisterExecutionInterceptor(logger);

            // Add context menu items (CRITICAL: must be after DTE available)
            await AddContextMenuAsync(logger);
            return;
        }
        await Task.Delay(1000);
    }
    // Fallback...
}

private async Task AddContextMenuAsync(ILoggerService logger)
{
    try
    {
        var service = new ContextMenuService(this, logger);
        await service.InitializeAsync();
    }
    catch (Exception ex)
    {
        logger.Error(ex, "Failed to add context menu items");
    }
}
```

## Diagnostic Method: Discovering the Correct CommandBar Name

If SSMS version changes or menu names change, use this diagnostic code to enumerate all context
menus and find the correct one:

```csharp
var commandBars = dte.CommandBars as Microsoft.VisualStudio.CommandBars.CommandBars;
for (int i = 1; i <= commandBars.Count; i++)
{
    var bar = commandBars[i];
    if (bar.Type == MsoBarType.msoBarTypePopup)
    {
        logger.Information($"Context menu [{i}]: '{bar.Name}' (Controls={bar.Controls.Count})");
        // Log first few control captions to identify the menu
        for (int j = 1; j <= Math.Min(bar.Controls.Count, 5); j++)
        {
            logger.Information($"  [{j}] '{bar.Controls[j].Caption}'");
        }
    }
}
```

Key identification: If a context menu contains "Cut", "Copy", "Paste", "Connect" — it is the SQL
editor context menu. If it contains "Bookmark", "Snippets", "Outline" — it is "Code Window" (wrong).

## Verification Checklist

After deployment, verify in the log file:

- [ ] `[右键菜单] === 开始初始化 ===` — Service started
- [ ] `CommandBars 总数: 334` — DTE available
- [ ] `✅ 按名称找到: 'SQL Files Editor Context'` — Found correct menu
- [ ] `✅✅ AI 命令添加完成: N/N 项` — All items added successfully
- [ ] In SSMS: Open SQL script, right-click editor → custom menu items appear

## Common Issues

| Symptom | Likely Cause | Fix |
|----------|-------------|-----|
| No menu items appear | DTE not ready when adding | Ensure DTE polling before calling service |
| No menu items appear | Wrong CommandBar name | Check log enumeration results, update priority list |
| Clicking menu item does nothing | CommandId mismatch with VSCT | Verify Controls.Add Id matches IDSymbol value |
| Duplicate menu items | Residual items not cleaned | Check Tag prefix matching and cleanup logic |
| Old code after deploy | Build output not copied | Check DLL timestamp matches build time |

## Adding New Commands: 6-Step Process

1. **VSCT**: Add `<IDSymbol>` with unique value (e.g., `0x0205`)
2. **VSCT**: Add `<Button>` element with `DynamicVisibility` flag
3. **Command class**: Create with `OleMenuCommandService.AddCommand()`, using matching CommandID
4. **ContextMenuService**: Add entry to commands array with matching ID and display name
5. **Package**: Call `NewCommand.InitializeAsync()` in `InitializeAsync()`
6. **Build → Deploy → Verify**

## Reference Files

- `references/context-menu-service-template.cs` — Complete ContextMenuService implementation template
- `references/package-integration-template.cs` — Package integration code template
