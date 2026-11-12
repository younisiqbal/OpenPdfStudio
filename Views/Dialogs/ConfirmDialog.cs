using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>A yes/no question. <c>ShowDialog&lt;bool&gt;</c> returns true for yes.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string yesText, string noText = "Not now")
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var yes = new Button { Content = yesText, IsDefault = true, MinWidth = 88, Classes = { "primary" } };
        var no = new Button { Content = noText, IsCancel = true, MinWidth = 88 };
        yes.Click += (_, _) => Close(true);
        no.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes }
                }
            }
        };
    }
}
