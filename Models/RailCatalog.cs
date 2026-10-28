using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenPdfStudio.Models;

/// <summary>A command the left rail can show. Order in <see cref="RailCatalog.All"/> is the rail order.</summary>
/// <param name="AboveDivider">Navigation pins sit above the hairline; tool pins sit below it.</param>
public sealed record RailCommand(string Id, string Label, string Group, bool AboveDivider, bool RequiresDocument);

/// <summary>
/// Commands that can be pinned to the left rail.
/// A new edit command needs an entry here and a button in the Edit group of the ribbon.
/// </summary>
public static class RailCatalog
{
    public const string Navigation = "Navigation";
    public const string Home = "Home";
    public const string Edit = "Edit";
    public const string Comment = "Comment";
    public const string View = "View";

    public static readonly string[] GroupOrder = [Navigation, Home, Edit, Comment, View];

    public static readonly RailCommand[] All =
    [
        new("Home", "Home", Navigation, true, false),
        new("Pages", "Pages", View, true, true),
        new("Comment", "Comment", Comment, true, true),
        new("Recent", "Recent", Navigation, true, false),
        new("Starred", "Starred", Navigation, true, false),
        new("Settings", "Settings", Navigation, true, false),
        new("Hand", "Hand", Home, false, true),
        new("Select", "Select", Home, false, true),
        new("Snapshot", "SnapShot", Home, false, true),
        new("Clipboard", "Clipboard", Home, false, true),
        new("Zoom", "Zoom", Home, false, true),
        new("PageFit", "Page Fit", Home, false, true),
        new("Find", "Find", Home, false, true),
        new("Undo", "Undo", Edit, false, true),
        new("Redo", "Redo", Edit, false, true),
        new("Highlight", "Highlight", Comment, false, true),
        new("Underline", "Underline", Comment, false, true),
        new("StrikeOut", "Strikeout", Comment, false, true),
        new("Squiggly", "Squiggly", Comment, false, true),
        new("Note", "Note", Comment, false, true),
        new("Typewriter", "Typewriter", Comment, false, true),
        new("Textbox", "Text box", Comment, false, true),
        new("Callout", "Callout", Comment, false, true),
        new("KeepTool", "Keep tool", Comment, false, true),
        new("Format", "Format", Comment, false, true),
        new("Ruler", "Ruler", View, false, true),
        new("ReadMode", "Read mode", View, false, true),
        new("FullScreen", "Full screen", View, false, true),
        new("Rotate", "Rotate", View, false, true),
    ];

    public static readonly string[] DefaultPins =
    [
        "Home", "Pages", "Comment", "Recent", "Starred", "Settings",
        "Hand", "Select", "Snapshot", "Clipboard", "Zoom", "PageFit", "Ruler"
    ];

    private static readonly Dictionary<string, RailCommand> ById =
        All.ToDictionary(command => command.Id, StringComparer.Ordinal);

    public static bool TryGet(string id, out RailCommand command) => ById.TryGetValue(id, out command!);
}
