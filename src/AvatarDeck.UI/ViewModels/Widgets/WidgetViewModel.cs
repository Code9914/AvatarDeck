using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvatarDeck.UI.ViewModels.Widgets;

/// <summary>Services a widget needs; implemented by <see cref="MainViewModel"/>.</summary>
public interface IWidgetHost
{
    IParameterSender Sender { get; }

    void ToggleFavorite(WidgetViewModel widget);

    /// <summary>Records a user action (history) and refreshes counters.</summary>
    void OnUserAction(WidgetViewModel widget, OscValue value);

    /// <summary>Opens the editor sheet for a widget (edit mode).</summary>
    void EditWidget(WidgetViewModel widget);

    /// <summary>Shows or hides a widget (edit mode).</summary>
    void ToggleHidden(WidgetViewModel widget);
}

/// <summary>
/// Base view model of a panel control. Values coming back from VRChat are applied with
/// <see cref="ApplyRemoteValue"/> without echoing them back over OSC.
/// </summary>
public abstract partial class WidgetViewModel : ObservableObject
{
    private int _suppressSend;

    protected WidgetViewModel(WidgetBase model, IWidgetHost host)
    {
        Model = model;
        Host = host;
        _displayLabel = model.Label;
    }

    public WidgetBase Model { get; }

    protected IWidgetHost Host { get; }

    public string Id => Model.Id;

    public string Label => Model.Label;

    public string Parameter => Model.Parameter;

    public bool IsHidden => Model.Hidden;

    /// <summary>Folder path shown in search results ("Clothes › Hoodie").</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Label as shown on the tile (without the section word: "Hair Hue" in section "Hair" → "Hue").</summary>
    [ObservableProperty]
    private string _displayLabel;

    /// <summary>Show the folder path under the label (search results, favorites).</summary>
    [ObservableProperty]
    private bool _showPath;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>In edit mode a click opens the editor instead of changing the value.</summary>
    [ObservableProperty]
    private bool _isEditMode;

    [RelayCommand]
    private void ToggleFavorite() => Host.ToggleFavorite(this);

    [RelayCommand]
    private void Edit() => Host.EditWidget(this);

    [RelayCommand]
    private void ToggleHidden() => Host.ToggleHidden(this);

    /// <summary>Updates the UI from a value reported by VRChat.</summary>
    public void ApplyRemoteValue(string parameter, OscValue value)
    {
        _suppressSend++;
        try
        {
            OnRemoteValue(parameter, value);
        }
        finally
        {
            _suppressSend--;
        }
    }

    protected abstract void OnRemoteValue(string parameter, OscValue value);

    /// <summary>True while applying a remote value: property-changed handlers must not send.</summary>
    protected bool IsApplyingRemote => _suppressSend > 0;

    protected async void SendDiscrete(string parameter, OscValue value)
    {
        // async void is limited to UI-triggered fire-and-forget sends; failures are logged by the transport.
        Host.OnUserAction(this, value);
        await Host.Sender.SendAsync(parameter, value.ConvertTo(Model.ParameterType)).ConfigureAwait(false);
    }

    protected void SendContinuous(string parameter, OscValue value)
    {
        Host.Sender.Post(parameter, value.ConvertTo(Model.ParameterType));
    }

    /// <summary>Creates the view model matching a widget model.</summary>
    public static WidgetViewModel Create(WidgetBase model, IWidgetHost host, string path) => model switch
    {
        ToggleWidget t => new ToggleWidgetViewModel(t, host) { Path = path },
        ButtonWidget b => new ButtonWidgetViewModel(b, host) { Path = path },
        SliderWidget s => new SliderWidgetViewModel(s, host) { Path = path },
        DropdownWidget d => new DropdownWidgetViewModel(d, host) { Path = path },
        TwoAxisWidget a => new TwoAxisWidgetViewModel(a, host) { Path = path },
        _ => throw new NotSupportedException($"Unknown widget type {model.GetType().Name}"),
    };
}
