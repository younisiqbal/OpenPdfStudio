using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.Themes;

namespace OpenPdfStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const int MaxPasswordAttempts = 3;

    private static readonly string ThemeFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPdfStudio",
        "theme.txt");

    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _version = "v0.1.0-dev";
    [ObservableProperty] private ViewModelBase _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeActive), nameof(HasActiveDocument))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(FitWidthCommand),
        nameof(FitPageCommand), nameof(CloseActiveTabCommand), nameof(CopySelectionCommand))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme), nameof(IsLightTheme), nameof(IsSystemTheme),
        nameof(IsMidnightTheme), nameof(IsHighContrastTheme))]
    private AppTheme _selectedTheme = AppTheme.Dark;

    public bool IsDarkTheme => SelectedTheme == AppTheme.Dark;
    public bool IsLightTheme => SelectedTheme == AppTheme.Light;
    public bool IsSystemTheme => SelectedTheme == AppTheme.System;
    public bool IsMidnightTheme => SelectedTheme == AppTheme.Midnight;
    public bool IsHighContrastTheme => SelectedTheme == AppTheme.HighContrast;

    public bool IsHomeActive => ActiveDocument is null;
    public bool HasActiveDocument => ActiveDocument is not null;

    public HomeViewModel Home { get; }
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    public Func<Task<string?>>? PickPdfAsync { get; set; }
    public Action? RequestClose { get; set; }

    /// <summary>Asks for a password. Arguments: file name, error from the previous attempt. Returns null on cancel.</summary>
    public Func<string, string?, Task<string?>>? RequestPasswordAsync { get; set; }

    /// <summary>Shows a blocking error message. Arguments: title, message.</summary>
    public Func<string, string, Task>? ShowErrorAsync { get; set; }

    public MainViewModel()
    {
        Home = new HomeViewModel(s => Status = s, OpenPdfAsync, OpenPathAsync);
        _currentPage = Home;
        ApplyTheme(LoadSavedTheme());
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsActive = false;
        if (newValue is not null)
            newValue.IsActive = true;

        CurrentPage = (ViewModelBase?)newValue ?? Home;
    }

    [RelayCommand]
    private Task OpenFile() => OpenPdfAsync();

    [RelayCommand]
    private void ShowHome() => ActiveDocument = null;

    [RelayCommand]
    private void Exit() => RequestClose?.Invoke();

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void CloseActiveTab()
    {
        if (ActiveDocument is not null)
            CloseDocument(ActiveDocument);
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ZoomIn() => ActiveDocument?.ZoomInCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ZoomOut() => ActiveDocument?.ZoomOutCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void FitWidth() => ActiveDocument?.FitWidthCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void FitPage() => ActiveDocument?.FitPageCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void CopySelection()
    {
        if (ActiveDocument?.CopySelectionCommand.CanExecute(null) == true)
            ActiveDocument.CopySelectionCommand.Execute(null);
    }

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        ApplyTheme(theme);
        Status = $"Theme: {AppThemes.DisplayName(theme)}";
        SaveTheme(theme);
    }

    public async Task OpenPathAsync(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            Status = "That file path is not valid";
            return;
        }

        var name = Path.GetFileName(fullPath);
        var existing = Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDocument(existing);
            Status = $"Switched to {name}";
            return;
        }

        if (!File.Exists(fullPath))
        {
            Home.RemoveRecent(fullPath);
            Status = $"File not found: {name}";
            await ShowError("File not found", $"\"{name}\" could not be found. It has been removed from Recent files.");
            return;
        }

        Status = $"Opening {name}...";
        string? password = null;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var session = await PdfDocumentSession.OpenAsync(fullPath, password);
                var document = new DocumentViewModel(session, s => Status = s, ActivateDocument, CloseDocument);
                Documents.Add(document);
                ActivateDocument(document);
                Home.AddOpenedFile(fullPath);
                Status = $"Opened: {name} ({session.PageCount} pages)";
                return;
            }
            catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired
                                              && attempt < MaxPasswordAttempts
                                              && RequestPasswordAsync is not null)
            {
                password = await RequestPasswordAsync(name, password is null ? null : "Incorrect password. Try again.");
                if (password is null)
                {
                    Status = "Open cancelled";
                    return;
                }
            }
            catch (PdfOpenException ex)
            {
                var message = ex.Error == PdfOpenError.PasswordRequired ? "The password was incorrect." : ex.Message;
                Status = $"Could not open {name}";
                await ShowError("Could not open PDF", $"{name}\n\n{message}");
                return;
            }
            catch (Exception ex)
            {
                Status = $"Could not open {name}";
                await ShowError("Could not open PDF", $"{name}\n\n{ex.Message}");
                return;
            }
        }
    }

    public void DisposeAll()
    {
        foreach (var document in Documents)
            document.Dispose();

        Documents.Clear();
        ActiveDocument = null;
    }

    private void ActivateDocument(DocumentViewModel document) => ActiveDocument = document;

    private void CloseDocument(DocumentViewModel document)
    {
        var index = Documents.IndexOf(document);
        if (index < 0)
            return;

        Documents.RemoveAt(index);
        if (ActiveDocument == document)
            ActiveDocument = Documents.Count > 0 ? Documents[Math.Min(index, Documents.Count - 1)] : null;

        document.Dispose();
        Status = $"Closed {document.FileName}";
    }

    private async Task OpenPdfAsync()
    {
        if (PickPdfAsync is null)
        {
            Status = "Open file: picker is not available";
            return;
        }

        var path = await PickPdfAsync();
        if (string.IsNullOrEmpty(path))
        {
            Status = "Open cancelled";
            return;
        }

        await OpenPathAsync(path);
    }

    private Task ShowError(string title, string message) =>
        ShowErrorAsync?.Invoke(title, message) ?? Task.CompletedTask;

    private void ApplyTheme(AppTheme theme)
    {
        SelectedTheme = theme;
        if (Application.Current is { } app)
            app.RequestedThemeVariant = AppThemes.ToVariant(theme);
    }

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (File.Exists(ThemeFilePath) &&
                Enum.TryParse(File.ReadAllText(ThemeFilePath).Trim(), out AppTheme saved))
                return saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return AppTheme.Dark;
    }

    private void SaveTheme(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFilePath)!);
            File.WriteAllText(ThemeFilePath, theme.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Theme applied, but could not be saved";
        }
    }
}
