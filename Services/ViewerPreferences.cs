using System;
using System.IO;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.Services;

/// <summary>App-wide viewer settings shared by every open document tab.</summary>
public static class ViewerPreferences
{
    private static readonly string HandCursorFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPdfStudio",
        "hand-cursor.txt");

    private static HandCursorStyle? _handCursorStyle;

    public static event Action? Changed;

    public static HandCursorStyle HandCursorStyle
    {
        get => _handCursorStyle ??= Load();
        set
        {
            if (_handCursorStyle == value)
                return;

            _handCursorStyle = value;
            Save(value);
            Changed?.Invoke();
        }
    }

    private static HandCursorStyle Load()
    {
        try
        {
            if (File.Exists(HandCursorFilePath) &&
                Enum.TryParse(File.ReadAllText(HandCursorFilePath).Trim(), out HandCursorStyle saved))
                return saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return HandCursorStyle.PalmAndFist;
    }

    private static void Save(HandCursorStyle style)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HandCursorFilePath)!);
            File.WriteAllText(HandCursorFilePath, style.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
