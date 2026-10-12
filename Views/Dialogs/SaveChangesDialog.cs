using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>Save / Don't Save / Cancel prompt shown before closing a modified file. Closing the window means Cancel.</summary>
public sealed class SaveChangesDialog : Window
{
    public SaveChangesDialog(string fileName)
    {
        Title = "Save changes?";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 88, Classes = { "primary" } };
        var dontSave = new Button { Content = "Don't Save", MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        save.Click += (_, _) => Close(SaveChangesChoice.Save);
        dontSave.Click += (_, _) => Close(SaveChangesChoice.DontSave);
        cancel.Click += (_, _) => Close(SaveChangesChoice.Cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Save changes?", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"\"{fileName}\" has changes that have not been saved. Save them before closing?",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { dontSave, cancel, save }
                }
            }
        };
    }
}
