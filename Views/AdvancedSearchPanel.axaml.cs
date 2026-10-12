using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class AdvancedSearchPanel : UserControl
{
    private AdvancedSearchViewModel? _vm;

    public AdvancedSearchPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnViewModelPropertyChanged;

            _vm = DataContext as AdvancedSearchViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdvancedSearchViewModel.IsOpen) && _vm is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() =>
            {
                QueryBox.Focus(NavigationMethod.Tab);
                QueryBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }
}
