using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class TopSearchBox : UserControl
{
    private TopSearchViewModel? _vm;

    public TopSearchBox()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SearchBox.GotFocus += (_, _) => OpenDropDown();
        SearchBox.AddHandler(PointerPressedEvent, (_, _) => OpenDropDown(), RoutingStrategies.Tunnel, true);
        SearchBox.TextChanged += (_, _) =>
        {
            if (SearchBox.IsFocused)
                OpenDropDown();
        };
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.ActionCompleted -= OnActionCompleted;

        _vm = DataContext as TopSearchViewModel;
        if (_vm is not null)
            _vm.ActionCompleted += OnActionCompleted;
    }

    private void OpenDropDown()
    {
        if (_vm is not null)
            _vm.IsDropDownOpen = true;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                _vm.IsDropDownOpen = false;
                ReleaseFocus();
                e.Handled = true;
                break;
            case Key.Down when _vm.IsDropDownOpen:
                DropDown.Child?.Focus(NavigationMethod.Tab);
                e.Handled = true;
                break;
        }
    }

    private void OnActionCompleted() => ReleaseFocus();

    private void ReleaseFocus() => TopLevel.GetTopLevel(this)?.Focus();
}
