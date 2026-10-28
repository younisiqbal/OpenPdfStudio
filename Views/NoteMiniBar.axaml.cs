using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class NoteMiniBar : UserControl
{
    public NoteMiniBar()
    {
        InitializeComponent();
        CommentButton.Click += (_, _) => TogglePopup?.Invoke();
        SettingsButton.Click += (_, _) => SettingsClicked?.Invoke();
        FillColors();
    }

    public event Action? TogglePopup;
    public event Action<Color>? ColorPicked;
    public event Action? SettingsClicked;

    public void SetColor(Color color) =>
        Swatch.Fill = new SolidColorBrush(color);

    private void FillColors()
    {
        ColorGrid.Children.Clear();
        foreach (var color in FormatPanelViewModel.Palette)
        {
            var swatch = color;
            var button = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Avalonia.Thickness(0),
                Margin = new Avalonia.Thickness(1),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(2)
            };
            button.Click += (_, _) =>
            {
                SetColor(swatch);
                ColorPicked?.Invoke(swatch);
                if (ColorButton.Flyout is Flyout flyout)
                    flyout.Hide();
            };
            ColorGrid.Children.Add(button);
        }
    }
}
