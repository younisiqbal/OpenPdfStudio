using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using OpenPdfStudio.Models;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class FormatPanel : UserControl
{
    public FormatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        FontCombo.SelectionChanged += (_, _) =>
        {
            if (DataContext is FormatPanelViewModel vm && FontCombo.SelectedItem is AnnotationFont font)
                vm.SetFontCommand.Execute(font);
        };
        SizeCombo.SelectionChanged += (_, _) =>
        {
            if (DataContext is FormatPanelViewModel vm && SizeCombo.SelectedItem is double size)
                vm.SetFontSizeCommand.Execute(size);
        };
        SpacingCombo.SelectionChanged += (_, _) =>
        {
            if (DataContext is FormatPanelViewModel vm && SpacingCombo.SelectedItem is double spacing)
                vm.SetLineSpacingCommand.Execute(spacing);
        };
    }

    private void Bind()
    {
        if (DataContext is not FormatPanelViewModel vm)
            return;

        Fill(NoteColorGrid, vm.Swatches, vm.SetNoteColorCommand);
        Fill(TextColorGrid, vm.Swatches, vm.SetTextColorCommand);
        Fill(MarkupColorGrid, vm.Swatches, vm.SetNoteColorCommand);
        Fill(BorderColorGrid, vm.Swatches, vm.SetBorderColorCommand);
        Fill(FillColorGrid, vm.Swatches, vm.SetFillColorCommand);
        FillIcons(vm);
    }

    private static void Fill(UniformGrid grid, System.Collections.Generic.IReadOnlyList<Color> colors, ICommand command)
    {
        grid.Children.Clear();
        foreach (var color in colors)
        {
            var button = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Avalonia.Thickness(0),
                Margin = new Avalonia.Thickness(1),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(2),
                Command = command,
                CommandParameter = color
            };
            grid.Children.Add(button);
        }
    }

    private void FillIcons(FormatPanelViewModel vm)
    {
        IconGrid.Children.Clear();
        foreach (var icon in vm.Icons)
        {
            var (body, _) = NoteIcons.Get(icon);
            var button = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Avalonia.Thickness(3),
                Margin = new Avalonia.Thickness(1),
                Command = vm.SetIconCommand,
                CommandParameter = icon,
                Content = new Avalonia.Controls.Shapes.Path
                {
                    Data = StreamGeometry.Parse(body),
                    Stretch = Stretch.Uniform,
                    Fill = Brushes.Goldenrod,
                    Stroke = Brushes.SaddleBrown,
                    StrokeThickness = 0.6
                }
            };
            IconGrid.Children.Add(button);
        }
    }
}
