using System.Collections.ObjectModel;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Core.Services;
using AvatarDeck.UI.ViewModels.Widgets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.UI.ViewModels;

/// <summary>
/// Root view model of the tablet. Shared by the desktop window and the VR overlay so both always show the same state.
/// Layout editing lives in <c>MainViewModel.Editing.cs</c>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IWidgetHost, IDisposable
{
    private const int MaxHistory = 30;
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly ProfileManager _profiles;
    private readonly IProfileStore _store;
    private readonly IVrcStateFeed _feed;
    private readonly IVrcStateSync _sync;
    private readonly PresetRunner _presetRunner;
    private readonly INotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dictionary<string, List<WidgetViewModel>> _widgetsByParameter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NavItemViewModel> _navByWidgetId = new(StringComparer.Ordinal);
    private readonly List<WidgetViewModel> _allWidgets = [];
    private readonly List<FolderViewModel> _allFolders = [];
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource? _saveCts;
    private bool _switchingProfile;

    public MainViewModel(
        ProfileManager profiles,
        IProfileStore store,
        IParameterSender sender,
        IVrcStateFeed feed,
        IVrcStateSync sync,
        PresetRunner presetRunner,
        INotificationService notifications,
        ILogger<MainViewModel> logger)
    {
        _profiles = profiles;
        _store = store;
        Sender = sender;
        _feed = feed;
        _sync = sync;
        _presetRunner = presetRunner;
        _notifications = notifications;
        _logger = logger;

        _profiles.ActiveProfileChanged += OnActiveProfileChanged;
        _feed.ParameterChanged += OnParameterChanged;
        _notifications.Raised += OnNotification;

        // Cheap 5 s status refresh; everything else is event driven.
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => RefreshStatus());
        _statusTimer.Start();
    }

    public IParameterSender Sender { get; }

    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];

    /// <summary>The current page's widgets, grouped into sections.</summary>
    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    public ObservableCollection<FolderViewModel> SubFolders { get; } = [];

    public ObservableCollection<PresetViewModel> Presets { get; } = [];

    public ObservableCollection<ProfileSummary> Profiles { get; } = [];

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public ObservableCollection<HistoryEntry> History { get; } = [];

    [ObservableProperty]
    private string _avatarName = "No avatar";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "Waiting for VRChat…";

    [ObservableProperty]
    private NavItemViewModel? _selectedNav;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(PageTitle), nameof(PageGlyph), nameof(PageAccent), nameof(IsFolderPage))]
    private FolderViewModel? _currentFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(PageTitle), nameof(ShowPresets), nameof(IsFolderPage))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ProfileSummary? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string _newPresetName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPresets))]
    private bool _isPresetsPage;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>The presets page is replaced by search results while searching.</summary>
    public bool ShowPresets => IsPresetsPage && !IsSearching;

    public bool IsFolderPage => CurrentFolder is not null && !IsSearching;

    public bool CanGoUp => CurrentFolder?.Parent is not null && !IsSearching;

    public string PageTitle => IsSearching
        ? $"Results for “{SearchText.Trim()}”"
        : IsPresetsPage ? "Presets" : CurrentFolder?.Path ?? SelectedNav?.Title ?? string.Empty;

    public string PageGlyph => CurrentFolder?.Glyph ?? SelectedNav?.Glyph ?? string.Empty;

    public Avalonia.Media.IBrush PageAccent => CurrentFolder?.AccentBrush ?? SelectedNav?.AccentBrush ?? Icons.Brush(null);

    /// <summary>Loads the profile list and the initial profile.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogDebug("UI init: listing profiles");
            await RefreshProfilesAsync();

            // Read VRChat's full state first so the right avatar opens with its real values.
            _logger.LogDebug("UI init: syncing with VRChat");
            await _sync.SyncAsync();
            _logger.LogDebug("UI init: activating profile for {AvatarId}", _feed.CurrentAvatarId);
            await _profiles.InitializeAsync(); // raises ActiveProfileChanged → LoadProfile
            _logger.LogDebug("UI init: done");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialization failed");
            _notifications.Notify("Initialization failed, see logs", NotificationLevel.Error);
        }
    }

    // ───────────────────────────── Profile loading ─────────────────────────────

    private void OnActiveProfileChanged(object? sender, AvatarProfile? profile) =>
        Dispatcher.UIThread.Post(async () =>
        {
            CloseEditors();
            LoadProfile(profile, isRebuild: false);
            await RefreshProfilesAsync();
        });

    /// <summary>
    /// (Re)creates every view model from the profile model. Used on profile switch and after layout edits;
    /// <paramref name="isRebuild"/> (after an edit) restores the page and search that were open.
    /// </summary>
    private void LoadProfile(AvatarProfile? profile, bool isRebuild)
    {
        var keepNavKind = isRebuild ? SelectedNav?.Kind : null;
        var keepFolderId = isRebuild ? CurrentFolder?.Model.Id : null;
        var search = SearchText;
        foreach (var toggle in _allWidgets.OfType<ToggleWidgetViewModel>())
        {
            toggle.StateChanged -= OnToggleStateChanged;
        }

        _widgetsByParameter.Clear();
        _navByWidgetId.Clear();
        _allWidgets.Clear();
        _allFolders.Clear();
        NavItems.Clear();
        Presets.Clear();
        AvatarName = profile?.Name ?? "No avatar";

        if (profile is null)
        {
            SelectedNav = null;
            return;
        }

        var favorites = profile.Favorites.ToHashSet(StringComparer.Ordinal);
        NavItems.Add(new NavItemViewModel(NavKind.Favorites, "Favorites", "") { AccentBrush = Icons.Brush("#FFC857") });
        foreach (var folder in profile.Folders)
        {
            var folderVm = BuildFolder(folder, null, favorites);
            if (folder.Hidden && !IsEditMode)
            {
                continue;
            }

            var nav = new NavItemViewModel(NavKind.Folder, folder.Name, folderVm.Glyph, folderVm) { AccentBrush = folderVm.AccentBrush };
            foreach (var widget in folderVm.AllWidgets())
            {
                _navByWidgetId[widget.Id] = nav;
            }

            NavItems.Add(nav);
        }

        NavItems.Add(new NavItemViewModel(NavKind.Presets, "Presets", ""));
        Presets.ReplaceWith(profile.Presets.Select(p => new PresetViewModel(p)));
        RebuildFolderChoices();

        // Show current VRChat values right away.
        foreach (var (parameter, widgets) in _widgetsByParameter)
        {
            if (_feed.TryGetParameter(parameter, out var value))
            {
                widgets.ForEach(w => w.ApplyRemoteValue(parameter, value));
            }
        }

        foreach (var nav in NavItems)
        {
            nav.RefreshBadge();
        }

        foreach (var toggle in _allWidgets.OfType<ToggleWidgetViewModel>())
        {
            toggle.StateChanged += OnToggleStateChanged;
        }

        _switchingProfile = true;
        SelectedProfile = Profiles.FirstOrDefault(p => p.AvatarId == profile.AvatarId);
        _switchingProfile = false;

        // Restore the open page (after an edit), else the last folder of this avatar, else Favorites / first folder.
        var restoreId = keepFolderId ?? profile.Layout.LastFolderId;
        var restored = _allFolders.FirstOrDefault(f => f.Model.Id == restoreId);
        var restoredNav = restored is null ? null : NavItems.FirstOrDefault(n => n.Folder == restored.Root);
        if (restoredNav is not null)
        {
            SelectedNav = restoredNav;
            CurrentFolder = restored;
        }
        else if (keepNavKind is NavKind.Favorites or NavKind.Presets)
        {
            SelectedNav = NavItems.First(n => n.Kind == keepNavKind);
        }
        else
        {
            SelectedNav = favorites.Count > 0 ? NavItems[0] : NavItems.ElementAtOrDefault(1) ?? NavItems[0];
        }

        if (isRebuild)
        {
            SearchText = search; // an edit made from search results keeps the results open
        }

        RefreshPage();
    }

    private FolderViewModel BuildFolder(Folder folder, FolderViewModel? parent, HashSet<string> favorites)
    {
        var vm = new FolderViewModel(folder, parent);
        _allFolders.Add(vm);
        foreach (var widget in folder.Widgets)
        {
            var widgetVm = WidgetViewModel.Create(widget, this, vm.Path);
            widgetVm.IsFavorite = favorites.Contains(widget.Id);
            widgetVm.IsEditMode = IsEditMode;
            vm.Widgets.Add(widgetVm);
            _allWidgets.Add(widgetVm);
            foreach (var parameter in widget.Parameters)
            {
                if (!_widgetsByParameter.TryGetValue(parameter, out var list))
                {
                    _widgetsByParameter[parameter] = list = [];
                }

                list.Add(widgetVm);
            }
        }

        foreach (var child in folder.Children)
        {
            vm.Children.Add(BuildFolder(child, vm, favorites));
        }

        return vm;
    }

    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        if (value is not null && !_switchingProfile && value.AvatarId != _profiles.ActiveProfile?.AvatarId)
        {
            _ = SwitchProfileAsync(value.AvatarId);
        }
    }

    private async Task SwitchProfileAsync(string avatarId)
    {
        try
        {
            await _profiles.ActivateAsync(avatarId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not switch to profile {AvatarId}", avatarId);
        }
    }

    [RelayCommand]
    private async Task RefreshProfilesAsync()
    {
        var list = await _store.ListAsync();
        _switchingProfile = true;
        Profiles.ReplaceWith(list);
        SelectedProfile = Profiles.FirstOrDefault(p => p.AvatarId == _profiles.ActiveProfile?.AvatarId);
        _switchingProfile = false;
    }

    // ───────────────────────────── Navigation & search ─────────────────────────────

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        SearchText = string.Empty;
        IsPresetsPage = value?.Kind == NavKind.Presets;
        CurrentFolder = value?.Folder;
        if (value?.Folder is { } folder && _profiles.ActiveProfile is { } profile && profile.Layout.LastFolderId != folder.Model.Id)
        {
            profile.Layout.LastFolderId = folder.Model.Id;
            ScheduleSave();
        }

        RefreshPage();
    }

    partial void OnCurrentFolderChanged(FolderViewModel? value) => RefreshPage();

    partial void OnSearchTextChanged(string value) => RefreshPage();

    [RelayCommand]
    private void OpenFolder(FolderViewModel folder) => CurrentFolder = folder;

    [RelayCommand]
    private void GoUp()
    {
        if (CurrentFolder?.Parent is { } parent)
        {
            CurrentFolder = parent;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool IsVisible(WidgetViewModel widget) => IsEditMode || !widget.Model.Hidden;

    private void RefreshPage()
    {
        Sections.Clear();
        IEnumerable<FolderViewModel> subFolders = [];

        if (IsSearching)
        {
            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hiddenFolders = _allFolders.Where(f => f.IsHidden).SelectMany(f => f.AllWidgets()).ToHashSet();
            var results = _allWidgets
                .Where(w => IsVisible(w) && (IsEditMode || !hiddenFolders.Contains(w)))
                .Where(w => terms.All(t =>
                    w.Label.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                    || w.Parameter.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || w.Path.Contains(t, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();
            AddFlatSection(results);
        }
        else if (SelectedNav?.Kind == NavKind.Favorites)
        {
            var order = _profiles.ActiveProfile?.Favorites ?? [];
            AddFlatSection(_allWidgets.Where(w => w.IsFavorite && IsVisible(w)).OrderBy(w => order.IndexOf(w.Id)).ToList());
        }
        else if (!IsPresetsPage && CurrentFolder is { } folder)
        {
            foreach (var section in BuildSections(folder.Widgets.Where(IsVisible).ToList()))
            {
                Sections.Add(section);
            }

            subFolders = folder.Children.Where(c => IsEditMode || !c.IsHidden);
        }

        SubFolders.ReplaceWith(subFolders);
        IsEmpty = Sections.Count == 0 && SubFolders.Count == 0 && !ShowPresets;
        EmptyMessage = _profiles.ActiveProfile is { } active && !active.AllWidgets().Any()
            ? "This avatar has no custom toggles or sliders.\nOnly VRChat's built-in parameters were found."
            : IsSearching ? "No control matches this search."
            : SelectedNav?.Kind == NavKind.Favorites ? "No favorites yet.\nStar a control to pin it here."
            : "This folder is empty.";
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageGlyph));
        OnPropertyChanged(nameof(PageAccent));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(IsFolderPage));
    }

    /// <summary>Search results / favorites: one section, full labels, folder path shown.</summary>
    private void AddFlatSection(List<WidgetViewModel> widgets)
    {
        foreach (var widget in widgets)
        {
            widget.DisplayLabel = widget.Label;
            widget.ShowPath = true;
        }

        if (widgets.Count > 0)
        {
            Sections.Add(new SectionViewModel(null, widgets));
        }
    }

    /// <summary>
    /// Groups a page's widgets by first word ("Hair Hue", "Hair Swap" → section "Hair": Hue, Swap).
    /// Words shared by a single widget stay in an untitled first section. No headers when it would not help.
    /// </summary>
    private static List<SectionViewModel> BuildSections(List<WidgetViewModel> widgets)
    {
        foreach (var widget in widgets)
        {
            widget.DisplayLabel = widget.Label;
            widget.ShowPath = false;
        }

        var groups = widgets.GroupBy(w => ProfileBuilder.SectionKey(w.Label)).ToList();
        var titled = groups.Where(g => g.Count() >= 2).ToList();
        if (titled.Count == 0 || (titled.Count == 1 && titled[0].Count() == widgets.Count))
        {
            return widgets.Count == 0 ? [] : [new SectionViewModel(null, widgets)];
        }

        var sections = new List<SectionViewModel>();
        var loose = groups.Where(g => g.Count() < 2).SelectMany(g => g).ToList();
        if (loose.Count > 0)
        {
            sections.Add(new SectionViewModel(null, loose));
        }

        foreach (var group in titled)
        {
            var first = group.First().Label;
            var space = first.IndexOf(' ', StringComparison.Ordinal);
            var header = space < 0 ? first : first[..space];
            foreach (var widget in group)
            {
                var rest = widget.Label.Length > header.Length && widget.Label.StartsWith(header, StringComparison.OrdinalIgnoreCase)
                    ? widget.Label[header.Length..].Trim()
                    : widget.Label;
                widget.DisplayLabel = rest.Length == 0 ? widget.Label : char.ToUpperInvariant(rest[0]) + rest[1..];
            }

            sections.Add(new SectionViewModel(header, group.ToList()));
        }

        return sections;
    }

    // ───────────────────────────── Favorites, history, counters ─────────────────────────────

    public void ToggleFavorite(WidgetViewModel widget)
    {
        if (_profiles.ActiveProfile is not { } profile)
        {
            return;
        }

        widget.IsFavorite = !widget.IsFavorite;
        if (widget.IsFavorite)
        {
            profile.Favorites.Add(widget.Id);
        }
        else
        {
            profile.Favorites.Remove(widget.Id);
        }

        if (SelectedNav?.Kind == NavKind.Favorites)
        {
            RefreshPage();
        }

        ScheduleSave();
    }

    public void OnUserAction(WidgetViewModel widget, OscValue value) =>
        Dispatcher.UIThread.Post(() =>
        {
            History.Insert(0, new HistoryEntry(DateTime.Now, widget.Label, value.ToString()));
            while (History.Count > MaxHistory)
            {
                History.RemoveAt(History.Count - 1);
            }
        });

    private void OnToggleStateChanged(object? sender, EventArgs e)
    {
        if (sender is WidgetViewModel widget && _navByWidgetId.TryGetValue(widget.Id, out var nav))
        {
            nav.RefreshBadge();
        }
    }

    // ───────────────────────────── Presets ─────────────────────────────

    [RelayCommand]
    private async Task ApplyPresetAsync(PresetViewModel preset) => await _presetRunner.ApplyAsync(preset.Model);

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(NewPresetName) && _profiles.ActiveProfile is not null;

    /// <summary>Saves the current value of every parameter of this avatar as a new preset.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        if (_profiles.ActiveProfile is not { } profile)
        {
            return;
        }

        var preset = PresetRunner.Capture(NewPresetName.Trim(), _widgetsByParameter.Keys, _feed);
        if (preset.Values.Count == 0)
        {
            _notifications.Notify("No known values yet: change something in game first", NotificationLevel.Warning);
            return;
        }

        profile.Presets.Add(preset);
        Presets.Add(new PresetViewModel(preset));
        NewPresetName = string.Empty;
        ScheduleSave();
        _notifications.Notify($"Preset \"{preset.Name}\" saved", NotificationLevel.Success);
    }

    [RelayCommand]
    private void DeletePreset(PresetViewModel preset)
    {
        _profiles.ActiveProfile?.Presets.Remove(preset.Model);
        Presets.Remove(preset);
        ScheduleSave();
    }

    // ───────────────────────────── Live state ─────────────────────────────

    private void OnParameterChanged(object? sender, ParameterChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_widgetsByParameter.TryGetValue(e.Parameter, out var widgets))
            {
                foreach (var widget in widgets)
                {
                    widget.ApplyRemoteValue(e.Parameter, e.Value);
                }
            }

            if (!IsConnected)
            {
                RefreshStatus();
            }
        });

    private void RefreshStatus()
    {
        IsConnected = _feed.IsVrChatResponding;
        StatusText = IsConnected ? "Connected to VRChat" : "Waiting for VRChat…";
    }

    /// <summary>Re-reads every parameter value from VRChat (OSCQuery).</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SyncAsync()
    {
        var ok = await _sync.SyncAsync();
        RefreshStatus();
        _notifications.Notify(
            ok ? "State synchronized with VRChat" : "VRChat not found: is the game running with OSC enabled?",
            ok ? NotificationLevel.Success : NotificationLevel.Warning);
    }

    private void OnNotification(object? sender, Notification notification) =>
        Dispatcher.UIThread.Post(async () =>
        {
            var toast = new ToastViewModel(notification);
            Toasts.Add(toast);
            await Task.Delay(ToastDuration);
            Toasts.Remove(toast);
        });

    // ───────────────────────────── Persistence ─────────────────────────────

    /// <summary>Debounced save: many quick edits produce a single write.</summary>
    private async void ScheduleSave()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        try
        {
            await Task.Delay(SaveDelay, token);
            await _profiles.SaveActiveAsync(token);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer save
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving profile failed");
            _notifications.Notify("Could not save profile", NotificationLevel.Error);
        }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _profiles.ActiveProfileChanged -= OnActiveProfileChanged;
        _feed.ParameterChanged -= OnParameterChanged;
        _notifications.Raised -= OnNotification;
        _saveCts?.Dispose();
    }
}
