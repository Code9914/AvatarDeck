using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvatarDeck.UI.ViewModels;

/// <summary>"VR panel" sheet: show/hide in the headset, opacity, size, lock, anchor, wrist button, recenter.</summary>
public sealed partial class VrPanelViewModel : ObservableObject, IDisposable
{
    private readonly IVrPanelController _vr;

    public VrPanelViewModel(IVrPanelController vr)
    {
        _vr = vr;
        Anchors =
        [
            new ChoiceViewModel(nameof(PanelAnchor.World), "In the world"),
            new ChoiceViewModel(nameof(PanelAnchor.LeftHand), "Left hand"),
            new ChoiceViewModel(nameof(PanelAnchor.RightHand), "Right hand"),
        ];
        _vr.StateChanged += OnStateChanged;
        Refresh();
    }

    /// <summary>Sheet open.</summary>
    [ObservableProperty]
    private bool _isOpen;

    public IReadOnlyList<ChoiceViewModel> Anchors { get; }

    public string StatusText => _vr.StatusMessage;

    public bool IsConnected => _vr.Status == VrStatus.Connected;

    public bool IsPanelVisible
    {
        get => _vr.IsPanelVisible;
        set
        {
            if (value != _vr.IsPanelVisible)
            {
                if (value)
                {
                    _vr.ShowPanel();
                }
                else
                {
                    _vr.HidePanel();
                }
            }
        }
    }

    /// <summary>Opacity in percent (20–100).</summary>
    public double OpacityPercent
    {
        get => Math.Round(Settings.Opacity * 100);
        set => Update(() => Settings.Opacity = (float)(value / 100));
    }

    /// <summary>Panel width in centimeters.</summary>
    public double WidthCentimeters
    {
        get => Math.Round(Settings.WidthMeters * 100);
        set => Update(() => Settings.WidthMeters = (float)(value / 100));
    }

    public bool Locked
    {
        get => Settings.Locked;
        set => Update(() => Settings.Locked = value);
    }

    public bool WristButton
    {
        get => Settings.WristButton;
        set => Update(() => Settings.WristButton = value);
    }

    private VrPanelSettings Settings => _vr.Settings;

    [RelayCommand]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void TogglePanel() => _vr.TogglePanel();

    [RelayCommand]
    private void Recenter() => _vr.Recenter();

    [RelayCommand]
    private void SetAnchor(ChoiceViewModel anchor) =>
        Update(() => Settings.Anchor = Enum.Parse<PanelAnchor>(anchor.Value));

    private void Update(Action change)
    {
        change();
        _vr.ApplySettings();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        foreach (var anchor in Anchors)
        {
            anchor.IsSelected = anchor.Value == Settings.Anchor.ToString();
        }

        OnPropertyChanged(string.Empty); // every computed property
    }

    public void Dispose() => _vr.StateChanged -= OnStateChanged;
}

/// <summary>Used when VR support is not available (tests, headless rendering).</summary>
public sealed class DisabledVrPanelController : IVrPanelController
{
    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public VrStatus Status => VrStatus.Error;

    public string StatusMessage => "VR disabled";

    public bool IsPanelVisible => false;

    public VrPanelSettings Settings { get; } = new();

    public void ShowPanel()
    {
    }

    public void HidePanel()
    {
    }

    public void TogglePanel()
    {
    }

    public void Recenter()
    {
    }

    public void ApplySettings()
    {
    }
}
