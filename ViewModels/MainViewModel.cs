using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;
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
    [ObservableProperty] private string _version = "v0.2.0";
    [ObservableProperty] private ViewModelBase _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeActive), nameof(HasActiveDocument), nameof(ShowDocumentToolbar), nameof(ShowRailDivider),
        nameof(ShowAppMenu), nameof(ShowTabStrip))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(FitWidthCommand),
        nameof(FitPageCommand), nameof(CloseActiveTabCommand), nameof(CopySelectionCommand),
        nameof(FindCommand), nameof(FindNextCommand), nameof(FindPreviousCommand), nameof(ToggleRulerCommand),
        nameof(ActualSizeCommand), nameof(RotateClockwiseCommand), nameof(PasteCommand), nameof(PasteInPlaceCommand),
        nameof(SaveCommand), nameof(SaveAsCommand), nameof(PrintCommand), nameof(ToggleReadModeCommand),
        nameof(ToggleFullScreenCommand), nameof(SelectRibbonTabCommand))]
    private DocumentViewModel? _activeDocument;

    /// <summary>Read Mode: the document fills the window with menus, tabs, rail and status bar hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresenting), nameof(IsChromeVisible), nameof(ShowDocumentToolbar),
        nameof(ShowAppMenu), nameof(ShowTabStrip))]
    private bool _isReadMode;

    /// <summary>Full Screen: like Read Mode, and the window covers the whole screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresenting), nameof(IsChromeVisible), nameof(ShowDocumentToolbar),
        nameof(ShowAppMenu), nameof(ShowTabStrip))]
    private bool _isFullScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeTab), nameof(IsEditTab), nameof(IsCommentTab), nameof(IsViewTab),
        nameof(IsHomeRibbon), nameof(IsEditRibbon), nameof(IsCommentRibbon), nameof(IsViewRibbon))]
    private RibbonTab _selectedRibbonTab = RibbonTab.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRibbonExpanded), nameof(IsHomeRibbon), nameof(IsEditRibbon),
        nameof(IsCommentRibbon), nameof(IsViewRibbon))]
    private bool _isRibbonCollapsed;

    [ObservableProperty] private int _pinEpoch;

    private readonly List<RailPinItem> _railItems = [];

    public bool IsPresenting => IsReadMode || IsFullScreen;
    public bool IsChromeVisible => !IsPresenting;
    public string PresentationExitText => IsFullScreen ? "Exit Full Screen (Esc)" : "Exit Read Mode (Esc)";

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
    public bool ShowDocumentToolbar => HasActiveDocument && IsChromeVisible;

    // The home screen has its own sidebar menu, so the app menu only shows with a document.
    public bool ShowAppMenu => IsChromeVisible && HasActiveDocument;
    public bool ShowTabStrip => IsChromeVisible && (HasActiveDocument || Documents.Count > 0);
    public bool IsRibbonExpanded => !IsRibbonCollapsed;
    public bool IsHomeTab => SelectedRibbonTab == RibbonTab.Home;
    public bool IsEditTab => SelectedRibbonTab == RibbonTab.Edit;
    public bool IsCommentTab => SelectedRibbonTab == RibbonTab.Comment;
    public bool IsViewTab => SelectedRibbonTab == RibbonTab.View;
    public bool IsHomeRibbon => IsHomeTab && IsRibbonExpanded;
    public bool IsEditRibbon => IsEditTab && IsRibbonExpanded;
    public bool IsCommentRibbon => IsCommentTab && IsRibbonExpanded;
    public bool IsViewRibbon => IsViewTab && IsRibbonExpanded;

    public IReadOnlyList<RailPinGroup> RailGroups { get; }

    public bool ShowRailDivider =>
        RailCatalog.All.Any(entry => entry.AboveDivider && IsRailEntryVisible(entry)) &&
        RailCatalog.All.Any(entry => !entry.AboveDivider && IsRailEntryVisible(entry));

    public HomeViewModel Home { get; }
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();
    public AdvancedSearchViewModel AdvancedSearch { get; }
    public TopSearchViewModel TopSearch { get; }

    public Func<Task<string?>>? PickPdfAsync { get; set; }
    public Action? RequestClose { get; set; }

    /// <summary>Asks for a password. Arguments: file name, error from the previous attempt. Returns null on cancel.</summary>
    public Func<string, string?, Task<string?>>? RequestPasswordAsync { get; set; }

    /// <summary>Shows a blocking error message. Arguments: title, message.</summary>
    public Func<string, string, Task>? ShowErrorAsync { get; set; }

    /// <summary>Asks where to save. Argument: the current path. Returns null on cancel.</summary>
    public Func<string, Task<string?>>? PickSavePathAsync { get; set; }

    /// <summary>Asks whether to save changes to a file before closing it. Argument: file name.</summary>
    public Func<string, Task<SaveChangesChoice>>? ConfirmSaveChangesAsync { get; set; }

    /// <summary>Shows the print dialog for a document and prints it.</summary>
    public Func<DocumentViewModel, Task>? PrintDocumentAsync { get; set; }

    /// <summary>Brings the main window to the front, used when a second launch opens a PDF.</summary>
    public Action? RequestActivate { get; set; }

    public Func<Task>? ShowSettingsAsync { get; set; }

    /// <summary>Shows the split dialog. Returns the files to write, or null on cancel.</summary>
    public Func<DocumentViewModel, Task<IReadOnlyList<SplitOutput>?>>? ShowSplitAsync { get; set; }

    /// <summary>Shows the merge dialog, starting with the given files. Returns the files in order, or null on cancel.</summary>
    public Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>? ShowMergeAsync { get; set; }

    /// <summary>Asks a yes/no question. Arguments: title, message, label for yes.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public Func<Task>? ShowAboutAsync { get; set; }

    public MainViewModel()
    {
        _isRibbonCollapsed = ViewerPreferences.RibbonCollapsed;
        RailGroups = CreateRailGroups();
        ViewerPreferences.Changed += OnViewerPreferencesChanged;
        Home = new HomeViewModel(s => Status = s, OpenPdfAsync, OpenPathAsync);
        AdvancedSearch = new AdvancedSearchViewModel(() => ActiveDocument, () => Documents.ToList(),
            OpenAndGetDocumentAsync, s => Status = s);
        TopSearch = new TopSearchViewModel(() => ActiveDocument, AdvancedSearch, s => Status = s);
        _currentPage = Home;
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowTabStrip));
        ApplyTheme(LoadSavedTheme());
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.CommentRibbonRequested -= ShowCommentRibbon;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.CommentRibbonRequested += ShowCommentRibbon;
        }

        CurrentPage = (ViewModelBase?)newValue ?? Home;
        TopSearch.OnActiveDocumentChanged();
        if (newValue is null)
            ExitPresentation();
    }

    partial void OnIsRibbonCollapsedChanged(bool value)
    {
        if (ViewerPreferences.RibbonCollapsed != value)
            ViewerPreferences.RibbonCollapsed = value;
    }

    private IReadOnlyList<RailPinGroup> CreateRailGroups()
    {
        var groups = new List<RailPinGroup>();
        foreach (var name in RailCatalog.GroupOrder)
        {
            var items = new List<RailPinItem>();
            foreach (var command in RailCatalog.All.Where(entry => entry.Group == name))
            {
                var item = new RailPinItem(command.Id, command.Label);
                _railItems.Add(item);
                items.Add(item);
            }

            groups.Add(new RailPinGroup(name, items));
        }

        return groups;
    }

    private bool IsRailEntryVisible(RailCommand entry) =>
        ViewerPreferences.IsPinned(entry.Id) && (!entry.RequiresDocument || HasActiveDocument);

    private void OnViewerPreferencesChanged()
    {
        foreach (var item in _railItems)
            item.Sync(ViewerPreferences.IsPinned(item.Id));

        PinEpoch++;
        OnPropertyChanged(nameof(ShowRailDivider));
        if (IsRibbonCollapsed != ViewerPreferences.RibbonCollapsed)
            IsRibbonCollapsed = ViewerPreferences.RibbonCollapsed;
    }

    private void ShowCommentRibbon() => SelectRibbonTab(RibbonTab.Comment.ToString());

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void SelectRibbonTab(string? tab)
    {
        if (!Enum.TryParse(tab, out RibbonTab value))
            return;

        SelectedRibbonTab = value;
        if (IsRibbonCollapsed)
            IsRibbonCollapsed = false;
    }

    [RelayCommand]
    private void ToggleRibbonCollapsed() => IsRibbonCollapsed = !IsRibbonCollapsed;

    partial void OnIsReadModeChanged(bool value) => OnPresentationChanged();
    partial void OnIsFullScreenChanged(bool value) => OnPresentationChanged();

    private void OnPresentationChanged()
    {
        OnPropertyChanged(nameof(PresentationExitText));
        foreach (var document in Documents)
            document.IsPresenting = IsPresenting;
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ToggleReadMode()
    {
        if (IsFullScreen)
        {
            IsFullScreen = false;
            IsReadMode = true;
        }
        else
        {
            IsReadMode = !IsReadMode;
        }

        Status = IsReadMode ? "Read Mode. Press Esc or Ctrl+H to exit." : "Read Mode off";
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
        if (IsFullScreen)
            IsReadMode = false;
        Status = IsFullScreen ? "Full Screen. Press Esc or F11 to exit." : "Full Screen off";
    }

    [RelayCommand]
    private void ExitPresentationMode() => ExitPresentation();

    /// <summary>Leaves Read Mode or Full Screen. Returns false when neither was on.</summary>
    public bool ExitPresentation()
    {
        if (!IsPresenting)
            return false;

        IsReadMode = false;
        IsFullScreen = false;
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private Task Print() =>
        ActiveDocument is { } document && PrintDocumentAsync is not null
            ? PrintDocumentAsync(document)
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ActualSize() => ActiveDocument?.ActualSizeCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void RotateClockwise() => ActiveDocument?.RotateClockwiseCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private Task Paste() => ActiveDocument?.PasteCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private Task PasteInPlace() => ActiveDocument?.PasteInPlaceCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task Save()
    {
        if (ActiveDocument is not { } document)
            return;

        if (!document.IsModified)
        {
            Status = "No changes to save";
            return;
        }

        await SaveDocumentAsync(document, saveAs: false);
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task SaveAs()
    {
        if (ActiveDocument is { } document)
            await SaveDocumentAsync(document, saveAs: true);
    }

    private async Task<bool> SaveDocumentAsync(DocumentViewModel document, bool saveAs)
    {
        var path = document.FilePath;
        if (saveAs)
        {
            if (PickSavePathAsync is null)
                return false;

            var picked = await PickSavePathAsync(document.FilePath);
            if (string.IsNullOrEmpty(picked))
            {
                Status = "Save cancelled";
                return false;
            }

            path = Path.GetFullPath(picked);
            var openElsewhere = Documents.Any(d => d != document &&
                string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (openElsewhere)
            {
                await ShowError("Could not save", $"\"{Path.GetFileName(path)}\" is open in another tab. Close it first, or pick a different name.");
                return false;
            }
        }

        try
        {
            Status = $"Saving {Path.GetFileName(path)}...";
            await document.SaveToAsync(path);
            Home.AddOpenedFile(document.FilePath);
            Status = $"Saved {document.FileName}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Could not save {Path.GetFileName(path)}";
            await ShowError("Could not save", $"{Path.GetFileName(path)}\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>Asks about unsaved changes. Returns false when the user cancels or the save fails.</summary>
    private async Task<bool> ConfirmDiscardOrSaveAsync(DocumentViewModel document)
    {
        if (!document.IsModified || ConfirmSaveChangesAsync is null)
            return true;

        ActivateDocument(document);
        return await ConfirmSaveChangesAsync(document.FileName) switch
        {
            SaveChangesChoice.Save => await SaveDocumentAsync(document, saveAs: false),
            SaveChangesChoice.DontSave => true,
            _ => false
        };
    }

    /// <summary>Called before the window closes. Returns false to keep it open.</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var document in Documents.Where(d => d.IsModified).ToList())
        {
            if (!await ConfirmDiscardOrSaveAsync(document))
                return false;
        }

        return true;
    }

    [RelayCommand]
    private Task OpenFile() => OpenPdfAsync();

    [RelayCommand]
    private void ShowHome() => ActiveDocument = null;

    [RelayCommand]
    private void Exit() => RequestClose?.Invoke();

    [RelayCommand]
    private async Task MakeDefaultPdfApp() => Status = await PdfAssociation.MakeDefaultAsync();

    [RelayCommand]
    private Task OpenSettings() => ShowSettingsAsync?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task StartCommenting()
    {
        if (await EnsureActiveDocumentAsync() is not null)
            SelectRibbonTab(RibbonTab.Comment.ToString());
    }

    /// <summary>Returns the active document, or asks for a PDF and opens it when the home screen is showing.</summary>
    private async Task<DocumentViewModel?> EnsureActiveDocumentAsync()
    {
        if (ActiveDocument is { } active)
            return active;

        if (PickPdfAsync is null || await PickPdfAsync() is not { Length: > 0 } path)
        {
            Status = "Open cancelled";
            return null;
        }

        return await OpenAndGetDocumentAsync(path);
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task CloseActiveTab()
    {
        if (ActiveDocument is not null)
            await RequestCloseDocumentAsync(ActiveDocument);
    }

    private async Task RequestCloseDocumentAsync(DocumentViewModel document)
    {
        if (await ConfirmDiscardOrSaveAsync(document))
            CloseDocument(document);
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

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task Find()
    {
        if (ActiveDocument is not { } document)
            return;

        var selected = await document.GetSelectedTextAsync();
        var initial = selected is { Length: > 0 and <= 100 } && !selected.Contains('\n') ? selected.Trim() : null;
        document.Search.Open(initial);
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void FindNext() => ActiveDocument?.Search.NextCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void FindPrevious() => ActiveDocument?.Search.PreviousCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ToggleRuler() => ActiveDocument?.ToggleRulerCommand.Execute(null);

    [RelayCommand]
    private void OpenAdvancedSearch()
    {
        var query = ActiveDocument?.Search.Query;
        AdvancedSearch.Open(string.IsNullOrWhiteSpace(query) ? null : ActiveDocument!.Search.Options);
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
        if (await OpenSessionAsync(fullPath) is not { } session)
            return;

        var document = CreateDocument(session);
        Documents.Add(document);
        ActivateDocument(document);
        Home.AddOpenedFile(fullPath);
        Status = $"Opened: {name} ({session.PageCount} pages)";
    }

    /// <summary>Opens a PDF, asking for its password if needed. Shows the error and returns null on failure or cancel.</summary>
    private async Task<PdfDocumentSession?> OpenSessionAsync(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        string? password = null;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PdfDocumentSession.OpenAsync(fullPath, password);
            }
            catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired
                                              && attempt < MaxPasswordAttempts
                                              && RequestPasswordAsync is not null)
            {
                password = await RequestPasswordAsync(name, password is null ? null : "Incorrect password. Try again.");
                if (password is null)
                {
                    Status = "Open cancelled";
                    return null;
                }
            }
            catch (PdfOpenException ex)
            {
                var message = ex.Error == PdfOpenError.PasswordRequired ? "The password was incorrect." : ex.Message;
                Status = $"Could not open {name}";
                await ShowError("Could not open PDF", $"{name}\n\n{message}");
                return null;
            }
            catch (Exception ex)
            {
                Status = $"Could not open {name}";
                await ShowError("Could not open PDF", $"{name}\n\n{ex.Message}");
                return null;
            }
        }
    }

    private DocumentViewModel CreateDocument(PdfDocumentSession session)
    {
        var document = new DocumentViewModel(session, s => Status = s, ActivateDocument,
            d => _ = RequestCloseDocumentAsync(d));
        document.Search.OpenAdvancedSearch = options => AdvancedSearch.Open(options);
        document.Shell = new DocumentShellCommands(ToggleReadModeCommand, ToggleFullScreenCommand, PrintCommand);
        document.IsPresenting = IsPresenting;
        document.ApplyArrangementAsync = pages => ApplyArrangementAsync(document, pages);
        return document;
    }

    /// <summary>Opens the file (or switches to its tab) and returns its document, or null if it could not be opened.</summary>
    private async Task<DocumentViewModel?> OpenAndGetDocumentAsync(string path)
    {
        await OpenPathAsync(path);
        var document = Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (document is not null && ActiveDocument != document)
            ActivateDocument(document);
        return document;
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
