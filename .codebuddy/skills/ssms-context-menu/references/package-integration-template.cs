// =============================================================================
// Package Integration Template — How to integrate ContextMenuService into your Package
// For SSMS 22+ SQL Editor Right-Click Context Menu
// =============================================================================

// In your Package class (e.g., MyExtensionPackage.cs):

/*

Step 1: Add the context menu service initialization call AFTER DTE becomes available.

The ContextMenuService.InitializeAsync() MUST be called after DTE is available,
because it needs DTE.CommandBars to find the SQL editor context menu.

DTE only becomes available after the VS Shell is fully initialized.
Use a polling approach to wait for DTE.

*/

protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
{
    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

    try
    {
        var logger = CreateLogger();
        
        // ... your existing initialization (register commands, services, etc.) ...

        // Register commands via OleMenuCommandService (REQUIRED for context menu items to work!)
        await Commands.MyCommand1.InitializeAsync(this, logger, configService);
        await Commands.MyCommand2.InitializeAsync(this, logger, configService);
        // ... other commands ...

        logger.Information("Package initialization successful.");

        // After Shell initialization: add context menu items
        if (ssmsVersion >= SSMSVersion.V22)
        {
            await RunDiagnosticAfterShellInitialized(logger);
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Package initialization error: {ex}");
    }
}

/// <summary>
/// Wait for Shell to fully initialize before running DTE-dependent operations.
/// DTE is only available after Shell initialization — use it as a proxy detector.
/// </summary>
private async Task RunDiagnosticAfterShellInitialized(ILoggerService logger)
{
    try
    {
        // Poll DTE service, confirm Shell is initialized (max 15 seconds)
        for (int i = 0; i < 15; i++)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetServiceAsync(typeof(DTE)) as DTE;
            if (dte != null)
            {
                logger.Information($"[ShellInit] DTE available, Shell initialized (waited ~{i}s)");

                // Other DTE-dependent initialization...
                // RegisterExecutionInterceptor(logger);
                // ... etc ...

                // CRITICAL: Add context menu items AFTER DTE is available!
                await AddContextMenuAsync(logger);

                return;
            }

            await Task.Delay(1000);
        }

        // Fallback: try anyway after timeout
        logger.Warning("[ShellInit] Shell not initialized within 15s, running with fallback");
        await AddContextMenuAsync(logger);
    }
    catch (Exception ex)
    {
        logger.Error(ex, "[ShellInit] Shell initialization check failed");
    }
}

/// <summary>
/// Add custom menu items to the SQL editor right-click context menu.
/// Core: enumerate CommandBars, find "SQL Files Editor Context", then Controls.Add() to add commands.
/// This is the ONLY working approach in SSMS 22 (VSCT CommandPlacement does NOT work for SQL editor).
/// </summary>
private async Task AddContextMenuAsync(ILoggerService logger)
{
    try
    {
        var service = new Services.ContextMenuService(this, logger);
        await service.InitializeAsync();
    }
    catch (Exception ex)
    {
        logger.Error(ex, "[ContextMenu] Failed to add context menu items");
    }
}

// =============================================================================
// Integration Checklist
// =============================================================================
//
// 1. ContextMenuService.cs exists in your project
// 2. CommandSetGuid in ContextMenuService matches VSCT guidSSMSBoostPlusCmdSet
// 3. Command IDs (0x0200, 0x0201, etc.) match VSCT IDSymbol values
// 4. TagPrefix is unique to your extension
// 5. Commands are registered via OleMenuCommandService in Package.InitializeAsync()
// 6. AddContextMenuAsync() is called AFTER DTE becomes available
// 7. VSCT still defines commands for main menu and keyboard shortcuts
// 8. Deploy and verify via log file checklist
