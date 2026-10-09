using Avalonia.Controls;
using Avalonia.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnRecentFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RecentFile file } &&
            DataContext is HomeViewModel vm)
        {
            vm.OpenRecentCommand.Execute(file);
        }
    }
}
