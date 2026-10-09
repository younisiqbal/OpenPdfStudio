using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private const int MaxRecent = 10;

    private static readonly string StarredFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPdfStudio",
        "starred.txt");

    private readonly Action<string> _setStatus;
    private readonly Func<Task> _openPdf;
    private readonly Func<string, Task> _openPath;
    private readonly HashSet<string> _starredPaths = new(StringComparer.OrdinalIgnoreCase);

    public HomeViewModel() : this(_ => { }, () => Task.CompletedTask, _ => Task.CompletedTask)
    {
    }

    public HomeViewModel(Action<string> setStatus, Func<Task> openPdf, Func<string, Task> openPath)
    {
        _setStatus = setStatus;
        _openPdf = openPdf;
        _openPath = openPath;
        RecentFiles.CollectionChanged += OnRecentFilesChanged;
        StarredFiles.CollectionChanged += OnStarredFilesChanged;
        LoadStarred();
    }

    public ObservableCollection<RecentFile> RecentFiles { get; } = new();
    public ObservableCollection<RecentFile> StarredFiles { get; } = new();

    [ObservableProperty] private bool _hasRecentFiles;
    [ObservableProperty] private bool _hasStarredFiles;

    [RelayCommand]
    private Task OpenFile() => _openPdf();

    [RelayCommand]
    private Task OpenRecent(RecentFile? file) =>
        file is null ? Task.CompletedTask : _openPath(file.FullPath);

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        _setStatus("Recent files cleared");
    }

    [RelayCommand]
    private void ToggleStar(RecentFile? file)
    {
        if (file is null)
            return;

        var starred = !_starredPaths.Contains(file.FullPath);
        if (starred)
        {
            _starredPaths.Add(file.FullPath);
            StarredFiles.Insert(0, CreateEntry(file.FullPath, file.LastOpened));
        }
        else
        {
            _starredPaths.Remove(file.FullPath);
            RemoveByPath(StarredFiles, file.FullPath);
        }

        foreach (var entry in RecentFiles.Concat(StarredFiles))
        {
            if (string.Equals(entry.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase))
                entry.IsStarred = starred;
        }

        SaveStarred();
        _setStatus(starred ? $"Starred: {file.Name}" : $"Removed from Starred: {file.Name}");
    }

    public void AddOpenedFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        RemoveRecent(fullPath);
        RecentFiles.Insert(0, CreateEntry(fullPath, "Just now"));

        while (RecentFiles.Count > MaxRecent)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    public void RemoveRecent(string fullPath) => RemoveByPath(RecentFiles, fullPath);

    private RecentFile CreateEntry(string fullPath, string lastOpened)
    {
        var info = new FileInfo(fullPath);
        return new RecentFile(
            info.Name,
            info.DirectoryName ?? string.Empty,
            lastOpened,
            FormatSize(info.Exists ? info.Length : 0),
            fullPath)
        {
            IsStarred = _starredPaths.Contains(fullPath)
        };
    }

    private static void RemoveByPath(ObservableCollection<RecentFile> files, string fullPath)
    {
        for (int i = files.Count - 1; i >= 0; i--)
        {
            if (string.Equals(files[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                files.RemoveAt(i);
        }
    }

    private void LoadStarred()
    {
        try
        {
            if (!File.Exists(StarredFilePath))
                return;

            foreach (var line in File.ReadAllLines(StarredFilePath))
            {
                var path = line.Trim();
                if (path.Length == 0 || !File.Exists(path) || !_starredPaths.Add(path))
                    continue;

                StarredFiles.Add(CreateEntry(path, string.Empty));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SaveStarred()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StarredFilePath)!);
            File.WriteAllLines(StarredFilePath, StarredFiles.Select(f => f.FullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _setStatus("Starred files could not be saved");
        }
    }

    private void OnRecentFilesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        HasRecentFiles = RecentFiles.Count > 0;

    private void OnStarredFilesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        HasStarredFiles = StarredFiles.Count > 0;

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }
}
