using System.Text.Json.Serialization;
using AvatarDeck.Core.Parameters;

namespace AvatarDeck.Core.Models;

/// <summary>
/// Base class of every control shown on the panel. Persisted polymorphically using the <c>kind</c> discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ToggleWidget), "toggle")]
[JsonDerivedType(typeof(ButtonWidget), "button")]
[JsonDerivedType(typeof(SliderWidget), "slider")]
[JsonDerivedType(typeof(DropdownWidget), "dropdown")]
[JsonDerivedType(typeof(TwoAxisWidget), "twoAxis")]
public abstract class WidgetBase
{
    /// <summary>Stable identifier, used by favorites, history and external integrations.</summary>
    public string Id { get; set; } = NewId();

    public string Label { get; set; } = string.Empty;

    /// <summary>Optional icon name (resolved by the UI icon set).</summary>
    public string? Icon { get; set; }

    /// <summary>Target avatar parameter name (without the <c>/avatar/parameters/</c> prefix).</summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>Type of the target parameter. Values are converted to it before being sent.</summary>
    public ParameterType ParameterType { get; set; } = ParameterType.Bool;

    /// <summary>Every parameter this widget drives (multi-axis widgets drive several).</summary>
    [JsonIgnore]
    public virtual IEnumerable<string> Parameters => [Parameter];

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Two-state switch. For int parameters <see cref="OnValue"/> selects a specific value (e.g. outfit index).</summary>
public sealed class ToggleWidget : WidgetBase
{
    public OscValue OnValue { get; set; } = OscValue.FromBool(true);

    public OscValue OffValue { get; set; } = OscValue.FromBool(false);
}

/// <summary>Momentary button: sends <see cref="PressValue"/>, then <see cref="ReleaseValue"/> after <see cref="HoldMilliseconds"/>.</summary>
public sealed class ButtonWidget : WidgetBase
{
    public OscValue PressValue { get; set; } = OscValue.FromBool(true);

    public OscValue ReleaseValue { get; set; } = OscValue.FromBool(false);

    /// <summary>How long the press value is held. VRChat needs ~100 ms to reliably see a pulse.</summary>
    public int HoldMilliseconds { get; set; } = 200;
}

/// <summary>Continuous value (radial puppet replacement).</summary>
public sealed class SliderWidget : WidgetBase
{
    public SliderWidget() => ParameterType = ParameterType.Float;

    public float Minimum { get; set; }

    public float Maximum { get; set; } = 1f;

    /// <summary>Snap increment, 0 for continuous.</summary>
    public float Step { get; set; }
}

/// <summary>Selects one value from a list (typically an int parameter driving outfits or colors).</summary>
public sealed class DropdownWidget : WidgetBase
{
    public DropdownWidget() => ParameterType = ParameterType.Int;

    public List<DropdownOption> Options { get; set; } = [];
}

/// <summary>An entry of a <see cref="DropdownWidget"/>.</summary>
public sealed class DropdownOption
{
    public string Label { get; set; } = string.Empty;

    public OscValue Value { get; set; }
}

/// <summary>Two-axis puppet replacement (XY pad). <see cref="WidgetBase.Parameter"/> is the X axis.</summary>
public sealed class TwoAxisWidget : WidgetBase
{
    public TwoAxisWidget() => ParameterType = ParameterType.Float;

    public string ParameterY { get; set; } = string.Empty;

    public float Minimum { get; set; } = -1f;

    public float Maximum { get; set; } = 1f;

    [JsonIgnore]
    public override IEnumerable<string> Parameters => [Parameter, ParameterY];
}
