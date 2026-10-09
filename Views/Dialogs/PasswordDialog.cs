using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>Modal password prompt. <c>ShowDialog&lt;string?&gt;</c> returns null when cancelled.</summary>
public sealed class PasswordDialog : Window
{
    private readonly TextBox _password;

    public PasswordDialog(string fileName, string? error)
    {
        Title = "Password required";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _password = new TextBox { PasswordChar = '\u2022', PlaceholderText = "Password" };

        var ok = new Button { Content = "Open", IsDefault = true, Classes = { "primary" }, MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        ok.Click += (_, _) => Close(_password.Text ?? string.Empty);
        cancel.Click += (_, _) => Close(null);

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"\"{fileName}\" is protected. Enter the password to open it.",
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(error))
        {
            panel.Children.Add(new TextBlock
            {
                Text = error,
                Foreground = Brushes.IndianRed,
                TextWrapping = TextWrapping.Wrap
            });
        }

        panel.Children.Add(_password);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, ok }
        });

        Content = panel;
        Opened += (_, _) => _password.Focus(NavigationMethod.Pointer);
    }
}
