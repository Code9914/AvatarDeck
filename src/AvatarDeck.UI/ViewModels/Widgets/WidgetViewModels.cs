using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvatarDeck.UI.ViewModels.Widgets;

/// <summary>On/off tile.</summary>
public sealed partial class ToggleWidgetViewModel(ToggleWidget model, IWidgetHost host) : WidgetViewModel(model, host)
{
    [ObservableProperty]
    private bool _isOn;

    /// <summary>Raised whenever the on/off state changes (local or remote), for folder counters.</summary>
    public event EventHandler? StateChanged;

    partial void OnIsOnChanged(bool value)
    {
        if (!IsApplyingRemote)
        {
            SendDiscrete(Parameter, value ? model.OnValue : model.OffValue);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRemoteValue(string parameter, OscValue value) =>
        IsOn = model.ParameterType == ParameterType.Bool
            ? value.AsBool() == model.OnValue.AsBool()
            : value.ConvertTo(model.ParameterType) == model.OnValue.ConvertTo(model.ParameterType);
}

/// <summary>Momentary button (press → hold → release).</summary>
public sealed partial class ButtonWidgetViewModel(ButtonWidget model, IWidgetHost host) : WidgetViewModel(model, host)
{
    [ObservableProperty]
    private bool _isPressed;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task PressAsync()
    {
        IsPressed = true;
        Host.OnUserAction(this, model.PressValue);
        await Host.Sender.SendAsync(Parameter, model.PressValue.ConvertTo(model.ParameterType));
        await Task.Delay(Math.Max(50, model.HoldMilliseconds));
        await Host.Sender.SendAsync(Parameter, model.ReleaseValue.ConvertTo(model.ParameterType));
        IsPressed = false;
    }

    protected override void OnRemoteValue(string parameter, OscValue value)
    {
        // Buttons are momentary: remote values are not displayed.
    }
}

/// <summary>Continuous value; ints snap to whole steps.</summary>
public sealed partial class SliderWidgetViewModel : WidgetViewModel
{
    private readonly SliderWidget _model;

    public SliderWidgetViewModel(SliderWidget model, IWidgetHost host)
        : base(model, host)
    {
        _model = model;
        _value = model.Minimum;
    }

    public double Minimum => _model.Minimum;

    public double Maximum => _model.Maximum;

    /// <summary>Int parameters are shown as a − value + stepper (discrete states) instead of a slider.</summary>
    public bool IsStepper => _model.ParameterType == ParameterType.Int;

    public bool IsSlider => !IsStepper;

    [RelayCommand]
    private void Decrement() => StepBy(-1);

    [RelayCommand]
    private void Increment() => StepBy(+1);

    private void StepBy(int delta)
    {
        var next = Math.Clamp(Math.Round(Value) + delta, _model.Minimum, _model.Maximum);
        if (next != Value)
        {
            _stepping = true;
            Value = next;
            _stepping = false;
            SendDiscrete(Parameter, OscValue.FromInt((int)next));
        }
    }

    private bool _stepping;

    public double Step => _model.ParameterType == ParameterType.Int ? Math.Max(1, _model.Step) : _model.Step;

    public bool SnapToStep => Step > 0;

    public string DisplayValue => _model.ParameterType == ParameterType.Int
        ? ((int)Math.Round(Value)).ToString(System.Globalization.CultureInfo.CurrentCulture)
        : _model.Maximum <= 1f && _model.Minimum >= -1f
            ? $"{Value * 100:0}%"
            : Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue))]
    private double _value;

    partial void OnValueChanged(double value)
    {
        if (_stepping)
        {
            return; // stepper sends a discrete, ordered value itself
        }

        if (!IsApplyingRemote)
        {
            SendContinuous(Parameter, OscValue.FromFloat((float)value));
        }
    }

    protected override void OnRemoteValue(string parameter, OscValue value) => Value = value.AsFloat();
}

/// <summary>Picks one value from a list.</summary>
public sealed partial class DropdownWidgetViewModel(DropdownWidget model, IWidgetHost host) : WidgetViewModel(model, host)
{
    public IReadOnlyList<DropdownOption> Options => model.Options;

    [ObservableProperty]
    private DropdownOption? _selectedOption;

    partial void OnSelectedOptionChanged(DropdownOption? value)
    {
        if (value is not null && !IsApplyingRemote)
        {
            SendDiscrete(Parameter, value.Value);
        }
    }

    protected override void OnRemoteValue(string parameter, OscValue value) =>
        SelectedOption = model.Options.FirstOrDefault(o => o.Value.ConvertTo(model.ParameterType) == value.ConvertTo(model.ParameterType));
}

/// <summary>Two-axis puppet replacement.</summary>
public sealed partial class TwoAxisWidgetViewModel(TwoAxisWidget model, IWidgetHost host) : WidgetViewModel(model, host)
{
    public double Minimum => model.Minimum;

    public double Maximum => model.Maximum;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    partial void OnXChanged(double value)
    {
        if (!IsApplyingRemote)
        {
            SendContinuous(model.Parameter, OscValue.FromFloat((float)value));
        }
    }

    partial void OnYChanged(double value)
    {
        if (!IsApplyingRemote)
        {
            SendContinuous(model.ParameterY, OscValue.FromFloat((float)value));
        }
    }

    protected override void OnRemoteValue(string parameter, OscValue value)
    {
        if (parameter == model.ParameterY)
        {
            Y = value.AsFloat();
        }
        else
        {
            X = value.AsFloat();
        }
    }
}
