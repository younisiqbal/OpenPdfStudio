using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.Services.Printing;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>Split a PDF into one file per page, or one file per range. Returns the files to write, or null on cancel.</summary>
public sealed class SplitDialog : Window
{
    private readonly int _pageCount;
    private readonly RadioButton _eachPage;
    private readonly RadioButton _byRanges;
    private readonly TextBox _ranges;
    private readonly TextBox _folder;
    private readonly TextBox _baseName;
    private readonly TextBlock _error;

    public SplitDialog(DocumentViewModel document)
    {
        _pageCount = document.PageCount;

        Title = "Split PDF";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _eachPage = new RadioButton { Content = $"Every page as its own file ({_pageCount} files)", GroupName = "split", IsChecked = true };
        _byRanges = new RadioButton { Content = "By page ranges", GroupName = "split" };
        _ranges = new TextBox { PlaceholderText = "e.g. 1-3; 4-7; 8-" };
        _ranges.GotFocus += (_, _) => _byRanges.IsChecked = true;

        _folder = new TextBox { Text = Path.GetDirectoryName(document.FilePath) ?? string.Empty };
        var browse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var start = Directory.Exists(_folder.Text) ? await StorageProvider.TryGetFolderFromPathAsync(_folder.Text!) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose where to save the split files",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                _folder.Text = path;
        };
        var folderRow = new DockPanel { Children = { browse, _folder } };
        DockPanel.SetDock(browse, Dock.Right);

        _baseName = new TextBox { Text = Path.GetFileNameWithoutExtension(document.FilePath) };
        _error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        var split = new Button { Content = "Split", IsDefault = true, MinWidth = 88, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        split.Click += (_, _) => TrySplit();
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Split \"{document.FileName}\"", FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                Labeled("Split into", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        _eachPage,
                        _byRanges,
                        _ranges,
                        new TextBlock
                        {
                            Text = "Each range separated by ; becomes one file. A range can list pages with commas, like 1-2, 5.",
                            FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
                        }
                    }
                }),
                Labeled("Save to folder", folderRow),
                Labeled("File name prefix", _baseName),
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, split }
                }
            }
        };
    }

    private static Control Labeled(string label, Control content) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 }, content }
    };

    private void TrySplit()
    {
        var folder = _folder.Text?.Trim() ?? string.Empty;
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            ShowError("Choose a folder that exists.");
            return;
        }

        var baseName = _baseName.Text?.Trim() ?? string.Empty;
        if (baseName.Length == 0 || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("Enter a file name prefix without characters such as \\ / : * ? \" < > |.");
            return;
        }

        var groups = new List<(string Suffix, IReadOnlyList<int> Pages)>();
        var digits = _pageCount.ToString().Length;
        if (_eachPage.IsChecked == true)
        {
            for (var i = 0; i < _pageCount; i++)
                groups.Add(($"page {(i + 1).ToString().PadLeft(digits, '0')}", [i]));
        }
        else
        {
            var parts = (_ranges.Text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                ShowError("Enter at least one page range, for example 1-3; 4-.");
                return;
            }

            foreach (var part in parts)
            {
                if (PageRange.Parse(part, _pageCount) is not { Count: > 0 } pages)
                {
                    ShowError($"\"{part}\" is not a valid range. Use pages between 1 and {_pageCount}, for example 1-3; 4-.");
                    return;
                }

                groups.Add(($"pages {part.Replace(" ", "").Replace(',', '_')}", pages));
            }
        }

        var outputs = groups
            .Select(g => new SplitOutput(Path.Combine(folder, $"{baseName} - {g.Suffix}.pdf"), g.Pages))
            .ToList();

        if (outputs.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Count)
        {
            ShowError("Two ranges are the same. Give each file a different range.");
            return;
        }

        if (outputs.FirstOrDefault(o => File.Exists(o.Path)) is { } existing)
        {
            ShowError($"\"{Path.GetFileName(existing.Path)}\" already exists in that folder. Choose another folder or prefix.");
            return;
        }

        Close(outputs);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
