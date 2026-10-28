using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.ViewModels;

/// <summary>One row in the rail customize checklist.</summary>
public partial class RailPinItem : ObservableObject
{
    private bool _syncing;

    public RailPinItem(string id, string label)
    {
        Id = id;
        Label = label;
        _isPinned = ViewerPreferences.IsPinned(id);
    }

    public string Id { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isPinned;

    partial void OnIsPinnedChanged(bool value)
    {
        if (!_syncing)
            ViewerPreferences.SetPinned(Id, value);
    }

    public void Sync(bool pinned)
    {
        if (IsPinned == pinned)
            return;

        _syncing = true;
        IsPinned = pinned;
        _syncing = false;
    }
}

public sealed class RailPinGroup(string title, IReadOnlyList<RailPinItem> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<RailPinItem> Items { get; } = items;
}
