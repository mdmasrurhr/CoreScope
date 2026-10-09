using System;
using System.Collections.Generic;

namespace CoreScope.Core.Fixes;

/// <summary>How a fix is carried out.</summary>
public enum FixKind
{
    /// <summary>CoreScope does it for you (after confirmation when it changes or deletes something).</summary>
    Command,
    /// <summary>Opens the exact Windows settings page or tool where the problem is fixed.</summary>
    Settings,
    /// <summary>Takes you to a page inside CoreScope (for example Control Center).</summary>
    Page,
    /// <summary>Opens a web page (driver download, support page).</summary>
    Url,
    /// <summary>A step-by-step walkthrough for things only the BIOS/UEFI or the maker's tool can change.</summary>
    Guide,
}

/// <summary>One button on a finding.</summary>
/// <param name="Id">Stable id; for <see cref="FixKind.Command"/> it names the action the runner performs.</param>
/// <param name="Label">Button text.</param>
/// <param name="Target">Settings URI / tool, page title, URL, or a command argument.</param>
/// <param name="Confirm">If set, the user must agree to this message first (used whenever something is changed or deleted).</param>
/// <param name="NeedsAdmin">The action needs CoreScope unlocked (administrator).</param>
/// <param name="Primary">The main button (accent colour); others are quieter.</param>
public sealed record FixAction(string Id, string Label, FixKind Kind, string Target = "", string? Confirm = null,
                               bool NeedsAdmin = false, bool Primary = false, Guide? Guide = null)
{
    public bool IsPrimary => Primary;
    public bool IsSecondary => !Primary;
    public string Tooltip => Kind switch
    {
        FixKind.Command => NeedsAdmin ? "CoreScope does this for you (needs full access)" : "CoreScope does this for you",
        FixKind.Settings => "Opens the Windows setting where this is fixed",
        FixKind.Page => "Opens the page in CoreScope",
        FixKind.Url => "Opens a web page",
        FixKind.Guide => "Shows the steps",
        _ => "",
    };
}

/// <summary>A fix action together with the finding it belongs to (so progress and results show on that card).</summary>
public sealed record FixButton(FixAction Action, CoreScope.Core.Hardware.Insight Owner)
{
    public string Label => Action.Label;
    public bool IsPrimary => Action.Primary;
    public bool IsSecondary => !Action.Primary;
    public string Tooltip => Action.Tooltip;
    public CoreScope.Core.Hardware.FixState State => Owner.State;
}

/// <summary>A walkthrough: what to do, in order, with buttons for the steps CoreScope can help with.</summary>
public sealed record Guide(string Title, string Intro, IReadOnlyList<string> Steps, IReadOnlyList<FixAction> Actions);

public sealed record FixResult(bool Ok, string Message)
{
    public static FixResult Success(string message) => new(true, message);
    public static FixResult Failure(string message) => new(false, message);
    public static FixResult Cancelled { get; } = new(false, "Cancelled. Nothing was changed.");
}

/// <summary>What the fix runner needs from the application (dialogs, navigation, elevation).</summary>
public interface IFixHost
{
    /// <summary>Asks the user to confirm; returns true to go ahead.</summary>
    bool Confirm(string title, string message, string yes);
    void ShowGuide(Guide guide);
    /// <summary>Selects a CoreScope page by title; false when there is no such page.</summary>
    bool Navigate(string page);
    /// <summary>Restarts CoreScope with administrator rights (one Windows prompt).</summary>
    void UnlockFullAccess();
}
