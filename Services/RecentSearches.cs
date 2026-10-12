using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenPdfStudio.Services;

/// <summary>The last few search queries, newest first, saved between sessions.</summary>
public static class RecentSearches
{
    private const int MaxItems = 10;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPdfStudio",
        "recent-searches.txt");

    private static readonly List<string> Items = Load();

    public static event Action? Changed;

    public static IReadOnlyList<string> All => Items;

    public static void Add(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return;

        Items.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        Items.Insert(0, query);
        if (Items.Count > MaxItems)
            Items.RemoveRange(MaxItems, Items.Count - MaxItems);

        Save();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Items.Clear();
        Save();
        Changed?.Invoke();
    }

    private static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return File.ReadAllLines(FilePath)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Take(MaxItems)
                    .ToList();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new List<string>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, Items);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
