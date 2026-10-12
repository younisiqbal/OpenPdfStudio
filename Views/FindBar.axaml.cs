using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class FindBar : UserControl
{
    private DocumentSearchViewModel? _vm;

    public FindBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when focus should go back to the page view.</summary>
    public event Action? ReturnFocusRequested;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.FocusRequested -= FocusQuery;
            _vm.Closed -= OnClosed;
        }

        _vm = DataContext as DocumentSearchViewModel;
        if (_vm is null)
            return;

        _vm.FocusRequested += FocusQuery;
        _vm.Closed += OnClosed;
    }

    private void FocusQuery() =>
        Dispatcher.UIThread.Post(() =>
        {
            QueryBox.Focus(NavigationMethod.Tab);
            QueryBox.SelectAll();
        }, DispatcherPriority.Input);

    private void OnClosed() => ReturnFocusRequested?.Invoke();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;

        if (e.Key == Key.Escape)
        {
            _vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
