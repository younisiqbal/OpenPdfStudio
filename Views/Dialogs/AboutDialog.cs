using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.Views.Dialogs;

public sealed class AboutDialog : Window
{
    public AboutDialog(string version)
    {
        Title = "About OpenPdfStudio";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 88, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "OpenPdfStudio", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Version {version.TrimStart('v')}", Opacity = 0.75 },
                new TextBlock
                {
                    Text = "A free, open-source PDF reader and editor for Windows, macOS, and Linux. Rendering by PDFium; interface by Avalonia.",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = "Licensed under the Apache License 2.0.", Opacity = 0.75 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(-8, 4, 0, 0),
                    Children =
                    {
                        Link("Website", ExternalLinks.Website),
                        Link("Source code", ExternalLinks.Repository),
                        Link("License", ExternalLinks.License)
                    }
                },
                close
            }
        };
    }

    private static Button Link(string text, string url)
    {
        var button = new Button { Content = text, Classes = { "ghost" } };
        ToolTip.SetTip(button, url);
        button.Click += (_, _) => ExternalLinks.Open(url);
        return button;
    }
}
