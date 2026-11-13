using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>Pick the two PDFs to compare. Returns (left, right) full paths, or null on cancel.</summary>
public sealed class ComparePickDialog : Window
{
    private string? _left;
    private string? _right;
    private readonly TextBlock _leftLabel;
    private readonly TextBlock _rightLabel;
    private readonly Button _compare;

    public ComparePickDialog(string? initialLeft = null)
    {
        Title = "Compare PDFs";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        CanResize = false;

        if (initialLeft is { Length: > 0 } && File.Exists(initialLeft))
            _left = Path.GetFullPath(initialLeft);

        _leftLabel = PathLabel();
        _rightLabel = PathLabel();
        _compare = new Button { Content = "Compare", IsDefault = true, MinWidth = 88, Classes = { "primary" } };
        _compare.Click += (_, _) =>
        {
            if (_left is null || _right is null)
                return;
            Close((_left, _right));
        };

        var pickLeft = new Button { Content = "Choose left...", Classes = { "ghost" }, MinWidth = 120 };
        pickLeft.Click += async (_, _) =>
        {
            if (await PickPdfAsync("Choose the left PDF") is { } path)
            {
                _left = path;
                UpdateLabels();
            }
        };

        var pickRight = new Button { Content = "Choose right...", Classes = { "ghost" }, MinWidth = 120 };
        pickRight.Click += async (_, _) =>
        {
            if (await PickPdfAsync("Choose the right PDF") is { } path)
            {
                _right = path;
                UpdateLabels();
            }
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        cancel.Click += (_, _) => Close(null);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { cancel, _compare }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Compare PDFs",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "Choose two PDF files to view side by side. Changed text is highlighted on the pages.",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                },
                Row("Left", _leftLabel, pickLeft),
                Row("Right", _rightLabel, pickRight),
                footer
            }
        };

        UpdateLabels();
    }

    private static TextBlock PathLabel() => new()
    {
        Opacity = 0.85,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static DockPanel Row(string title, TextBlock label, Button pick)
    {
        var header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Width = 48, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pick, Dock.Right);
        DockPanel.SetDock(header, Dock.Left);
        return new DockPanel
        {
            LastChildFill = true,
            Children = { pick, header, label }
        };
    }

    private void UpdateLabels()
    {
        _leftLabel.Text = _left is null ? "No file chosen" : Path.GetFileName(_left);
        _rightLabel.Text = _right is null ? "No file chosen" : Path.GetFileName(_right);
        _compare.IsEnabled = _left is not null && _right is not null
                             && !string.Equals(_left, _right, System.StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> PickPdfAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF files") { Patterns = ["*.pdf"] },
                FilePickerFileTypes.All
            ]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath() is { } path ? Path.GetFullPath(path) : null;
    }
}
