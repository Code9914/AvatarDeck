using System.Runtime.InteropServices;
using AvatarDeck.Core.Parameters;

namespace AvatarDeck.Osc;

/// <summary>OSC argument type tags supported by AvatarDeck (the subset VRChat uses).</summary>
public enum OscArgumentKind : byte
{
    Int,
    Float,
    String,
    True,
    False,
    Nil,
}

/// <summary>A single decoded OSC argument.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct OscArgument : IEquatable<OscArgument>
{
    private readonly int _int;
    private readonly float _float;
    private readonly string? _string;

    private OscArgument(OscArgumentKind kind, int i = 0, float f = 0, string? s = null)
    {
        Kind = kind;
        _int = i;
        _float = f;
        _string = s;
    }

    public OscArgumentKind Kind { get; }

    public static OscArgument Int(int value) => new(OscArgumentKind.Int, i: value);

    public static OscArgument Float(float value) => new(OscArgumentKind.Float, f: value);

    public static OscArgument String(string value) => new(OscArgumentKind.String, s: value);

    public static OscArgument Bool(bool value) => new(value ? OscArgumentKind.True : OscArgumentKind.False);

    public static OscArgument Nil { get; } = new(OscArgumentKind.Nil);

    public static OscArgument From(OscValue value) => value.Type switch
    {
        ParameterType.Bool => Bool(value.AsBool()),
        ParameterType.Int => Int(value.AsInt()),
        _ => Float(value.AsFloat()),
    };

    public int IntValue => _int;

    public float FloatValue => _float;

    public string? StringValue => _string;

    /// <summary>Converts to an avatar parameter value. Strings and nil are not parameter values.</summary>
    public bool TryGetValue(out OscValue value)
    {
        value = Kind switch
        {
            OscArgumentKind.Int => OscValue.FromInt(_int),
            OscArgumentKind.Float => OscValue.FromFloat(_float),
            OscArgumentKind.True => OscValue.FromBool(true),
            OscArgumentKind.False => OscValue.FromBool(false),
            _ => default,
        };
        return Kind is not (OscArgumentKind.String or OscArgumentKind.Nil);
    }

    public bool Equals(OscArgument other) =>
        Kind == other.Kind && _int == other._int && _float.Equals(other._float) && string.Equals(_string, other._string, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is OscArgument other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _int, _float, _string);

    public static bool operator ==(OscArgument left, OscArgument right) => left.Equals(right);

    public static bool operator !=(OscArgument left, OscArgument right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        OscArgumentKind.Int => _int.ToString(System.Globalization.CultureInfo.InvariantCulture),
        OscArgumentKind.Float => _float.ToString(System.Globalization.CultureInfo.InvariantCulture),
        OscArgumentKind.String => $"\"{_string}\"",
        OscArgumentKind.True => "true",
        OscArgumentKind.False => "false",
        _ => "nil",
    };
}

/// <summary>A decoded OSC message.</summary>
public sealed record OscMessage(string Address, OscArgument[] Arguments)
{
    public override string ToString() => $"{Address} {string.Join(' ', Arguments)}";
}
