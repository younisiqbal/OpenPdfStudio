using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.Services;

/// <summary>App-wide viewer settings shared by every open document tab.</summary>
public static class ViewerPreferences
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPdfStudio");

    private static readonly string HandCursorFilePath = Path.Combine(SettingsDirectory, "hand-cursor.txt");
    private static readonly string RulerFilePath = Path.Combine(SettingsDirectory, "ruler.txt");
    private static readonly string CommentToolbarFilePath = Path.Combine(SettingsDirectory, "comment-toolbar.txt");
    private static readonly string KeepToolFilePath = Path.Combine(SettingsDirectory, "keep-tool-selected.txt");
    private static readonly string AnnotationStylesFilePath = Path.Combine(SettingsDirectory, "annotation-styles.json");
    private static readonly string RailPinsFilePath = Path.Combine(SettingsDirectory, "rail-pins.json");
    private static readonly string RibbonCollapsedFilePath = Path.Combine(SettingsDirectory, "ribbon-collapsed.txt");

    private static HandCursorStyle? _handCursorStyle;
    private static (bool Show, RulerUnit Unit)? _ruler;
    private static bool? _showCommentToolbar;
    private static bool? _keepToolSelected;
    private static Dictionary<string, AnnotationStyleData>? _annotationStyles;
    private static HashSet<string>? _railPins;
    private static bool? _ribbonCollapsed;

    public static event Action? Changed;

    public static HandCursorStyle HandCursorStyle
    {
        get => _handCursorStyle ??= Load(HandCursorFilePath, HandCursorStyle.PalmAndFist);
        set
        {
            if (_handCursorStyle == value)
                return;

            _handCursorStyle = value;
            Save(HandCursorFilePath, value.ToString());
            Changed?.Invoke();
        }
    }

    public static bool ShowRuler
    {
        get => Ruler.Show;
        set => SetRuler(value, Ruler.Unit);
    }

    public static RulerUnit RulerUnit
    {
        get => Ruler.Unit;
        set => SetRuler(Ruler.Show, value);
    }

    private static (bool Show, RulerUnit Unit) Ruler => _ruler ??= LoadRuler();

    private static void SetRuler(bool show, RulerUnit unit)
    {
        if (Ruler == (show, unit))
            return;

        _ruler = (show, unit);
        Save(RulerFilePath, $"{show}\n{unit}");
        Changed?.Invoke();
    }

    public static bool ShowCommentToolbar
    {
        get => _showCommentToolbar ??= LoadBool(CommentToolbarFilePath, false);
        set
        {
            if (_showCommentToolbar == value)
                return;

            _showCommentToolbar = value;
            Save(CommentToolbarFilePath, value.ToString());
            Changed?.Invoke();
        }
    }

    public static bool RibbonCollapsed
    {
        get => _ribbonCollapsed ??= LoadBool(RibbonCollapsedFilePath, false);
        set
        {
            if (_ribbonCollapsed == value)
                return;

            _ribbonCollapsed = value;
            Save(RibbonCollapsedFilePath, value.ToString());
            Changed?.Invoke();
        }
    }

    public static bool IsPinned(string id) => RailPins.Contains(id);

    public static void SetPinned(string id, bool pinned)
    {
        if (!RailCatalog.TryGet(id, out _))
            return;

        var next = new HashSet<string>(RailPins, StringComparer.Ordinal);
        if (!(pinned ? next.Add(id) : next.Remove(id)))
            return;

        _railPins = next;
        Save(RailPinsFilePath, JsonSerializer.Serialize(next.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        Changed?.Invoke();
    }

    private static HashSet<string> RailPins => _railPins ??= LoadRailPins();

    private static HashSet<string> LoadRailPins()
    {
        var json = string.Join('\n', ReadLines(RailPinsFilePath));
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<string>(RailCatalog.DefaultPins, StringComparer.Ordinal);

        try
        {
            var saved = JsonSerializer.Deserialize<string[]>(json);
            if (saved is null)
                return new HashSet<string>(RailCatalog.DefaultPins, StringComparer.Ordinal);

            return new HashSet<string>(saved.Where(id => RailCatalog.TryGet(id, out _)), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(RailCatalog.DefaultPins, StringComparer.Ordinal);
        }
    }

    /// <summary>When false, a comment tool goes back to the hand tool after one use.</summary>
    public static bool KeepToolSelected
    {
        get => _keepToolSelected ??= LoadBool(KeepToolFilePath, false);
        set
        {
            if (_keepToolSelected == value)
                return;

            _keepToolSelected = value;
            Save(KeepToolFilePath, value.ToString());
            Changed?.Invoke();
        }
    }

    /// <summary>The style new annotations of this kind start with, as last set in the Format panel.</summary>
    public static AnnotationStyle GetAnnotationStyle(AnnotationKind kind) =>
        AnnotationStyles.TryGetValue(kind.ToString(), out var data) ? data.ToStyle() : AnnotationStyle.DefaultFor(kind);

    public static void SetAnnotationStyle(AnnotationKind kind, AnnotationStyle style)
    {
        AnnotationStyles[kind.ToString()] = AnnotationStyleData.From(style);
        Save(AnnotationStylesFilePath, JsonSerializer.Serialize(AnnotationStyles));
        Changed?.Invoke();
    }

    private static Dictionary<string, AnnotationStyleData> AnnotationStyles => _annotationStyles ??= LoadAnnotationStyles();

    private static Dictionary<string, AnnotationStyleData> LoadAnnotationStyles()
    {
        var json = string.Join('\n', ReadLines(AnnotationStylesFilePath));
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, AnnotationStyleData>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, AnnotationStyleData>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new Dictionary<string, AnnotationStyleData>();
        }
    }

    private static bool LoadBool(string path, bool fallback)
    {
        var lines = ReadLines(path);
        return lines.Length > 0 && bool.TryParse(lines[0].Trim(), out var saved) ? saved : fallback;
    }

    private static (bool Show, RulerUnit Unit) LoadRuler()
    {
        var lines = ReadLines(RulerFilePath);
        var show = lines.Length > 0 && bool.TryParse(lines[0].Trim(), out var saved) && saved;
        var unit = lines.Length > 1 && Enum.TryParse(lines[1].Trim(), out RulerUnit savedUnit) ? savedUnit : RulerUnit.Inches;
        return (show, unit);
    }

    private static T Load<T>(string path, T fallback) where T : struct, Enum
    {
        var lines = ReadLines(path);
        return lines.Length > 0 && Enum.TryParse(lines[0].Trim(), out T saved) ? saved : fallback;
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            if (File.Exists(path))
                return File.ReadAllLines(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return [];
    }

    private static void Save(string path, string contents)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(path, contents);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
