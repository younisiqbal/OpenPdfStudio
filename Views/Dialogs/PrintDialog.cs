using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenPdfStudio.Services.Printing;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>Printer, copies, page range and orientation. <c>ShowDialog&lt;PrintJob?&gt;</c> returns null on cancel.</summary>
public sealed class PrintDialog : Window
{
    private static readonly string[] OrientationNames = ["Auto (match each page)", "Portrait", "Landscape"];

    private readonly int _pageCount;
    private readonly int _currentPageIndex;
    private readonly ComboBox _printer;
    private readonly NumericUpDown _copies;
    private readonly RadioButton _allPages;
    private readonly RadioButton _currentPage;
    private readonly RadioButton _customPages;
    private readonly TextBox _range;
    private readonly ComboBox _orientation;
    private readonly TextBlock _error;

    public PrintDialog(string documentName, IReadOnlyList<string> printers, string? defaultPrinter, int pageCount, int currentPageIndex)
    {
        _pageCount = pageCount;
        _currentPageIndex = currentPageIndex;

        Title = "Print";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _printer = new ComboBox { ItemsSource = printers, HorizontalAlignment = HorizontalAlignment.Stretch };
        _printer.SelectedItem = printers.Contains(defaultPrinter) ? defaultPrinter : printers.FirstOrDefault();

        _copies = new NumericUpDown { Minimum = 1, Maximum = 999, Value = 1, Increment = 1, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };

        _allPages = new RadioButton { Content = $"All pages ({pageCount})", GroupName = "range", IsChecked = true };
        _currentPage = new RadioButton { Content = $"Current page ({currentPageIndex + 1})", GroupName = "range" };
        _customPages = new RadioButton { Content = "Pages", GroupName = "range", VerticalAlignment = VerticalAlignment.Center };
        _range = new TextBox { PlaceholderText = "e.g. 1-3, 5", Width = 200 };
        _range.GotFocus += (_, _) => _customPages.IsChecked = true;

        _orientation = new ComboBox { ItemsSource = OrientationNames, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        _error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        var print = new Button { Content = "Print", IsDefault = true, MinWidth = 88, Classes = { "primary" }, IsEnabled = printers.Count > 0 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        print.Click += (_, _) => TryPrint();
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Print \"{documentName}\"", FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                Labeled("Printer", _printer),
                Labeled("Copies", _copies),
                Labeled("Pages", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        _allPages,
                        _currentPage,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _customPages, _range } }
                    }
                }),
                Labeled("Orientation", _orientation),
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, print }
                }
            }
        };
    }

    private static Control Labeled(string label, Control content) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 }, content }
    };

    private void TryPrint()
    {
        if (_printer.SelectedItem is not string printer)
        {
            ShowError("Choose a printer.");
            return;
        }

        IReadOnlyList<int>? pages;
        if (_currentPage.IsChecked == true)
            pages = [_currentPageIndex];
        else if (_customPages.IsChecked == true)
            pages = PageRange.Parse(_range.Text ?? string.Empty, _pageCount);
        else
            pages = Enumerable.Range(0, _pageCount).ToList();

        if (pages is null)
        {
            ShowError($"Enter pages between 1 and {_pageCount}, for example 1-3, 5.");
            return;
        }

        var orientation = (PrintOrientation)System.Math.Max(0, _orientation.SelectedIndex);
        Close(new PrintJob(printer, (int)(_copies.Value ?? 1), pages, orientation));
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }
}
