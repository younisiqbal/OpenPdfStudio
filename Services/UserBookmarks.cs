using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenPdfStudio.Services;

/// <summary>A bookmark the user added to a page. It is kept by OpenPdfStudio, not written into the PDF.</summary>
public sealed record UserBookmark(string Title, int PageIndex);

/// <summary>Per-file user bookmarks, stored in one JSON file keyed by full path.</summary>
public static class UserBookmarks
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPdfStudio",
        "bookmarks.json");

    private static Dictionary<string, List<UserBookmark>>? _all;

    public static IReadOnlyList<UserBookmark> For(string path) =>
        All.TryGetValue(Key(path), out var list) ? list : [];

    public static void Set(string path, IEnumerable<UserBookmark> bookmarks)
    {
        var list = bookmarks.OrderBy(b => b.PageIndex).ToList();
        if (list.Count == 0)
            All.Remove(Key(path));
        else
            All[Key(path)] = list;
        Save();
    }

    /// <summary>Carries bookmarks over when a file is saved under a new name.</summary>
    public static void Copy(string fromPath, string toPath)
    {
        if (string.Equals(Key(fromPath), Key(toPath), StringComparison.Ordinal))
            return;

        Set(toPath, For(fromPath));
    }

    private static string Key(string path) =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? Path.GetFullPath(path).ToUpperInvariant()
            : Path.GetFullPath(path);

    private static Dictionary<string, List<UserBookmark>> All => _all ??= Load();

    private static Dictionary<string, List<UserBookmark>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, List<UserBookmark>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new Dictionary<string, List<UserBookmark>>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
