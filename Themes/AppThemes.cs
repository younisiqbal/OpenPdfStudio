using Avalonia.Styling;

namespace OpenPdfStudio.Themes;

public enum AppTheme
{
    Dark,
    Light,
    System,
    Midnight,
    HighContrast
}

public static class AppThemes
{
    public static ThemeVariant Midnight { get; } = new("Midnight", ThemeVariant.Dark);
    public static ThemeVariant HighContrast { get; } = new("HighContrast", ThemeVariant.Dark);

    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.System => ThemeVariant.Default,
        AppTheme.Midnight => Midnight,
        AppTheme.HighContrast => HighContrast,
        _ => ThemeVariant.Dark
    };

    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.HighContrast => "High Contrast",
        _ => theme.ToString()
    };
}
