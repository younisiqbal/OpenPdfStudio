using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenPdfStudio.Models;

public partial class RecentFile(string name, string folder, string lastOpened, string size, string fullPath)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Folder { get; } = folder;
    public string LastOpened { get; } = lastOpened;
    public string Size { get; } = size;
    public string FullPath { get; } = fullPath;

    [ObservableProperty] private bool _isStarred;
}
