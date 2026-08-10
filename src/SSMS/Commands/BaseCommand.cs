using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// Base class for VS command handlers.
/// Provides common infrastructure for registering and executing menu commands.
/// </summary>
/// <typeparam name="T">The concrete command type (CRTP pattern).</typeparam>
public abstract class BaseCommand<T> where T : BaseCommand<T>
{
    /// <summary>
    /// Gets the singleton command instance.
    /// </summary>
    public static T? Instance { get; protected set; }

    /// <summary>
    /// Gets the async package that owns this command.
    /// </summary>
    protected AsyncPackage Package { get; }

    /// <summary>
    /// Gets the menu command for this handler.
    /// </summary>
    protected OleMenuCommand? MenuCommand { get; private set; }

    /// <summary>
    /// The command set GUID - must match guidSSMSBoostPlusCmdSet in the VSCT file.
    /// </summary>
    protected static readonly Guid CommandSetGuid = new("c3d4e5f6-a7b8-9012-cdef-234567890123");

    protected BaseCommand(AsyncPackage package, OleMenuCommandService commandService, int commandId)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));

        var menuCommandId = new CommandID(CommandSetGuid, commandId);
        var menuItem = new OleMenuCommand(Execute, menuCommandId);
        menuItem.BeforeQueryStatus += OnBeforeQueryStatus;

        commandService.AddCommand(menuItem);
        MenuCommand = menuItem;
    }

    /// <summary>
    /// Override to control command visibility/enablement.
    /// Default: command is always visible and enabled.
    /// </summary>
    protected virtual void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var command = sender as OleMenuCommand;
        if (command != null)
        {
            command.Visible = true;
            command.Enabled = true;
        }
    }

    /// <summary>
    /// Execute the command. Override in derived classes.
    /// </summary>
    protected abstract void Execute(object sender, EventArgs e);
}
