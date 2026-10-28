using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class MarkupMiniBar : UserControl
{
    public MarkupMiniBar()
    {
        InitializeComponent();
        RemoveButton.Click += (_, _) => RemoveClicked?.Invoke();
        CopyButton.Click += (_, _) => CopyClicked?.Invoke();
        CommentButton.Click += (_, _) => CommentClicked?.Invoke();
        FillColors();
        if (ColorButton.Flyout is Flyout flyout)
        {
            flyout.Opened += (_, _) =>
            {
                IsColorFlyoutOpen = true;
                ColorFlyoutOpened?.Invoke();
            };
            flyout.Closed += (_, _) =>
            {
                IsColorFlyoutOpen = false;
                ColorFlyoutClosed?.Invoke();
            };
        }
    }

    public bool IsColorFlyoutOpen { get; private set; }

    public event Action? RemoveClicked;
    public event Action? CopyClicked;
    public event Action? CommentClicked;
    public event Action<Color>? ColorPicked;
    public event Action? ColorFlyoutOpened;
    public event Action? ColorFlyoutClosed;

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
