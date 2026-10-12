using System.Windows.Input;

namespace OpenPdfStudio.ViewModels;

/// <summary>Window-level commands a document's context menu needs to reach.</summary>
public sealed record DocumentShellCommands(ICommand ReadMode, ICommand FullScreen, ICommand Print);

public enum SaveChangesChoice
{
    Save,
    DontSave,
    Cancel
}
