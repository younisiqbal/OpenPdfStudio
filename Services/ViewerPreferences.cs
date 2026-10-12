using System;
using System.IO;
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

    private static HandCursorStyle? _handCursorStyle;
    private static (bool Show, RulerUnit Unit)? _ruler;

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
