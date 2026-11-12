using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;
using OpenPdfStudio.Themes;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>App settings. Every change applies and saves immediately.</summary>
public sealed class SettingsDialog : Window
{
    private static readonly AppTheme[] Themes = Enum.GetValues<AppTheme>();
    private static readonly (PageLayoutMode Mode, string Name)[] Layouts =
    [
        (PageLayoutMode.Continuous, "Continuous scroll"),
        (PageLayoutMode.SinglePage, "Single page"),
        (PageLayoutMode.TwoPage, "Two pages")
    ];
    private static readonly (HandCursorStyle Style, string Name)[] HandCursors =
    [
        (HandCursorStyle.PalmAndFist, "Open palm, fist while dragging"),
        (HandCursorStyle.Palm, "Open palm"),
        (HandCursorStyle.MoveArrows, "Move arrows"),
        (HandCursorStyle.PointingFinger, "Pointing finger")
    ];
    private static readonly (RulerUnit Unit, string Name)[] RulerUnits =
    [
        (RulerUnit.Inches, "Inches"),
        (RulerUnit.Centimeters, "Centimeters")
    ];

    public SettingsDialog(MainViewModel main)
    {
        Title = "Settings";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var theme = Choice(Themes.Select(AppThemes.DisplayName), Array.IndexOf(Themes, main.SelectedTheme),
            index => main.SetThemeCommand.Execute(Themes[index]));
        var layout = Choice(Layouts.Select(l => l.Name), Array.FindIndex(Layouts, l => l.Mode == ViewerPreferences.PageLayout),
            index => ViewerPreferences.PageLayout = Layouts[index].Mode);
        var handCursor = Choice(HandCursors.Select(h => h.Name), Array.FindIndex(HandCursors, h => h.Style == ViewerPreferences.HandCursorStyle),
            index => ViewerPreferences.HandCursorStyle = HandCursors[index].Style);
        var rulerUnit = Choice(RulerUnits.Select(u => u.Name), Array.FindIndex(RulerUnits, u => u.Unit == ViewerPreferences.RulerUnit),
            index => ViewerPreferences.RulerUnit = RulerUnits[index].Unit);

        var showRuler = Toggle("Show the ruler", ViewerPreferences.ShowRuler, value => ViewerPreferences.ShowRuler = value);
        var keepTool = Toggle("Keep a comment tool selected after using it", ViewerPreferences.KeepToolSelected,
            value => ViewerPreferences.KeepToolSelected = value);
        var collapseRibbon = Toggle("Collapse the ribbon to its tab names", ViewerPreferences.RibbonCollapsed,
            value => ViewerPreferences.RibbonCollapsed = value);

        var makeDefault = new Button { Content = "Use as default PDF app...", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left };
        makeDefault.Click += (_, _) => main.MakeDefaultPdfAppCommand.Execute(null);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 88, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Settings", FontSize = 16, FontWeight = FontWeight.SemiBold },
                Labeled("Theme", theme),
                Labeled("Page layout for new documents", layout),
                Labeled("Hand cursor", handCursor),
                Labeled("Ruler unit", rulerUnit),
                showRuler,
                keepTool,
                collapseRibbon,
                makeDefault,
                close
            }
        };
    }

    private static ComboBox Choice(IEnumerable<string> names, int selected, Action<int> apply)
    {
        var box = new ComboBox { ItemsSource = names.ToList(), SelectedIndex = Math.Max(0, selected), HorizontalAlignment = HorizontalAlignment.Stretch };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
                apply(box.SelectedIndex);
        };
        return box;
    }

    private static CheckBox Toggle(string label, bool value, Action<bool> apply)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        box.IsCheckedChanged += (_, _) => apply(box.IsChecked == true);
        return box;
    }

    private static Control Labeled(string label, Control content) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 }, content }
    };
}
