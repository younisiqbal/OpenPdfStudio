using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>Choose and order the PDFs to merge. Returns the full paths in order, or null on cancel.</summary>
public sealed class MergeDialog : Window
{
    private readonly ObservableCollection<string> _files;
    private readonly ListBox _list;
    private readonly Button _up, _down, _remove, _merge;
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    public MergeDialog(IReadOnlyList<string> initialFiles)
    {
        Title = "Merge PDFs";
        Width = 560;
        Height = 440;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _files = new ObservableCollection<string>(initialFiles.Where(File.Exists));
        _list = new ListBox
        {
            ItemsSource = _files,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((path, _) => new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = Path.GetDirectoryName(path), FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            })
        };
        _list.SelectionChanged += (_, _) => Refresh();

        var add = new Button { Content = "Add files...", Classes = { "primary" } };
        add.Click += async (_, _) => await AddFilesAsync();
        _up = Tool("Move up", () => Move(-1));
        _down = Tool("Move down", () => Move(1));
        _remove = Tool("Remove", Remove);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        cancel.Click += (_, _) => Close(null);
        _merge = new Button { Content = "Merge", IsDefault = true, MinWidth = 88, Classes = { "primary" } };
        _merge.Click += (_, _) => Close(_files.ToList());

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, _up, _down, _remove } };
        var footer = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, _merge } };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_hint);

        var root = new DockPanel { Margin = new Thickness(20) };
        var title = new TextBlock { Text = "Merge PDFs", FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        tools.Margin = new Thickness(0, 0, 0, 12);
        footer.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(title);
        root.Children.Add(tools);
        root.Children.Add(footer);
        root.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            CornerRadius = new CornerRadius(6),
            Child = _list
        });
        Content = root;

        Refresh();
    }

    private static Button Tool(string text, Action action)
    {
        var button = new Button { Content = text, Classes = { "ghost" } };
        button.Click += (_, _) => action();
        return button;
    }

    private async System.Threading.Tasks.Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add PDFs to merge",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDF files") { Patterns = ["*.pdf"] }]
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
                _files.Add(Path.GetFullPath(path));
        }

        Refresh();
    }

    private void Move(int direction)
    {
        var index = _list.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _files.Count)
            return;

        _files.Move(index, target);
        _list.SelectedIndex = target;
    }

    private void Remove()
    {
        var index = _list.SelectedIndex;
        if (index < 0)
            return;

        _files.RemoveAt(index);
        if (_files.Count > 0)
            _list.SelectedIndex = Math.Min(index, _files.Count - 1);
        Refresh();
    }

    private void Refresh()
    {
        var index = _list.SelectedIndex;
        _up.IsEnabled = index > 0;
        _down.IsEnabled = index >= 0 && index < _files.Count - 1;
        _remove.IsEnabled = index >= 0;
        _merge.IsEnabled = _files.Count >= 2;
        _hint.Text = _files.Count < 2
            ? "Add at least two PDFs. Pages are joined in the order listed."
            : $"{_files.Count} files. Pages are joined in the order listed.";
    }
}
