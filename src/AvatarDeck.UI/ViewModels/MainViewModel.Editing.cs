using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Services;
using AvatarDeck.UI.ViewModels.Widgets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvatarDeck.UI.ViewModels;

/// <summary>
/// Layout editing: edit mode, widget/folder editor sheets, hide/show, move, reorder, type changes, auto-organise.
/// Every operation mutates the profile model, marks it customised, saves (debounced) and rebuilds the view models.
/// All controls are large click targets so the whole flow works with a VR laser pointer.
/// </summary>
public sealed partial class MainViewModel : IEditorHost
{
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(4);
    private List<FolderChoiceViewModel> _folderChoices = [];

    /// <summary>Edit mode: clicks open editors, hidden items are shown dimmed.</summary>
    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    private WidgetEditorViewModel? _widgetEditor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    private FolderEditorViewModel? _folderEditor;

    /// <summary>First click on "Auto-organize" arms it; a second click within a few seconds runs it.</summary>
    [ObservableProperty]
    private bool _isReorganizeArmed;

    public bool IsEditorOpen => WidgetEditor is not null || FolderEditor is not null;

    public IReadOnlyList<FolderChoiceViewModel> FolderChoices => _folderChoices;

    partial void OnIsEditModeChanged(bool value)
    {
        if (!value)
        {
            CloseEditors();
        }

        Rebuild();
    }

    [RelayCommand]
    private void ToggleEditMode() => IsEditMode = !IsEditMode;

    // ───────────────────────────── Opening editors ─────────────────────────────

    public void EditWidget(WidgetViewModel widget)
    {
        FolderEditor = null;
        WidgetEditor = new WidgetEditorViewModel(widget.Model, this);
    }

    public void ToggleHidden(WidgetViewModel widget)
    {
        widget.Model.Hidden = !widget.Model.Hidden;
        OnLayoutEdited(rebuild: true);
    }

    [RelayCommand]
    private void EditCurrentFolder()
    {
        if (CurrentFolder is { } folder)
        {
            WidgetEditor = null;
            FolderEditor = new FolderEditorViewModel(folder.Model, this);
        }
    }

    [RelayCommand]
    private void NewFolder()
    {
        var folder = AddFolder(null);
        WidgetEditor = null;
        FolderEditor = new FolderEditorViewModel(folder, this);
    }

    public void CloseEditors()
    {
        var hadEditor = IsEditorOpen;
        WidgetEditor = null;
        FolderEditor = null;
        if (hadEditor)
        {
            Rebuild(); // apply label/name edits typed in the sheet
        }
    }

    [RelayCommand]
    private void CloseEditor() => CloseEditors();

    // ───────────────────────────── IEditorHost ─────────────────────────────

    public void OnLayoutEdited(bool rebuild)
    {
        if (_profiles.ActiveProfile is { } profile)
        {
            profile.IsCustomized = true;
        }

        ScheduleSave();
        if (rebuild)
        {
            Rebuild();
        }
    }

    public WidgetBase ChangeWidgetKind(WidgetBase widget, string kind)
    {
        if (_profiles.ActiveProfile?.FindParent(widget) is not { } parent)
        {
            return widget;
        }

        var converted = WidgetConversions.Convert(widget, kind);
        parent.Widgets[parent.Widgets.IndexOf(widget)] = converted;
        OnLayoutEdited(rebuild: true);
        return converted;
    }

    public void MoveWidget(WidgetBase widget, int delta)
    {
        if (_profiles.ActiveProfile?.FindParent(widget) is not { } parent)
        {
            return;
        }

        if (Move(parent.Widgets, widget, delta))
        {
            OnLayoutEdited(rebuild: true);
        }
    }

    public void MoveWidgetTo(WidgetBase widget, Folder folder)
    {
        if (_profiles.ActiveProfile?.FindParent(widget) is not { } parent || parent == folder)
        {
            return;
        }

        parent.Widgets.Remove(widget);
        folder.Widgets.Add(widget);
        CloseEditorsWithoutRebuild();
        OnLayoutEdited(rebuild: true);
        _notifications.Notify($"Moved \"{widget.Label}\" to {folder.Name}", NotificationLevel.Success);
    }

    public void MoveFolder(Folder folder, int delta)
    {
        if (_profiles.ActiveProfile is not { } profile)
        {
            return;
        }

        if (Move(profile.FindSiblings(folder), folder, delta))
        {
            OnLayoutEdited(rebuild: true);
        }
    }

    public Folder AddFolder(Folder? parent)
    {
        var profile = _profiles.ActiveProfile ?? throw new InvalidOperationException("No active profile.");
        var siblings = parent?.Children ?? profile.Folders;
        var folder = new Folder
        {
            Name = parent is null ? "New folder" : "New page",
            Icon = "folder",
            Color = parent?.Color ?? ProfileBuilder.Palette[profile.Folders.Count % ProfileBuilder.Palette.Count],
        };

        // New top-level folders go before the hidden System folder.
        var systemIndex = siblings.FindIndex(f => f.Name == ProfileBuilder.SystemFolderName);
        siblings.Insert(systemIndex >= 0 ? systemIndex : siblings.Count, folder);
        OnLayoutEdited(rebuild: true);
        Navigate(folder);
        return folder;
    }

    public void DeleteFolder(Folder folder)
    {
        if (_profiles.ActiveProfile is not { } profile || folder.Widgets.Count > 0 || folder.Children.Count > 0)
        {
            return;
        }

        profile.FindSiblings(folder).Remove(folder);
        CloseEditorsWithoutRebuild();
        OnLayoutEdited(rebuild: true);
    }

    // ───────────────────────────── Auto-organize ─────────────────────────────

    /// <summary>Re-applies the automatic organisation (needs a second click to confirm: it resets the layout).</summary>
    [RelayCommand]
    private async Task ReorganizeAsync()
    {
        if (!IsReorganizeArmed)
        {
            IsReorganizeArmed = true;
            await Task.Delay(ConfirmWindow);
            IsReorganizeArmed = false;
            return;
        }

        IsReorganizeArmed = false;
        CloseEditorsWithoutRebuild();
        var ok = await _profiles.ReorganizeActiveAsync(); // raises ActiveProfileChanged → LoadProfile
        _notifications.Notify(
            ok ? "Layout re-organized automatically" : "VRChat not reachable: cannot read the parameter list",
            ok ? NotificationLevel.Success : NotificationLevel.Warning);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private void Rebuild() => LoadProfile(_profiles.ActiveProfile, isRebuild: true);

    private void CloseEditorsWithoutRebuild()
    {
        WidgetEditor = null;
        FolderEditor = null;
    }

    private void Navigate(Folder folder)
    {
        var target = _allFolders.FirstOrDefault(f => f.Model == folder);
        if (target is not null && NavItems.FirstOrDefault(n => n.Folder == target.Root) is { } nav)
        {
            SelectedNav = nav;
            CurrentFolder = target;
        }
    }

    private void RebuildFolderChoices()
    {
        _folderChoices = _allFolders.Select(f => new FolderChoiceViewModel(f.Model, f.Path)).ToList();
        OnPropertyChanged(nameof(FolderChoices));
    }

    private static bool Move<T>(List<T> list, T item, int delta)
    {
        var index = list.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return false;
        }

        (list[index], list[target]) = (list[target], list[index]);
        return true;
    }
}
