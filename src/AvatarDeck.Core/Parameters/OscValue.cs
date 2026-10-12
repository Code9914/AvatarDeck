using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace AvatarDeck.Core.Parameters;

/// <summary>The three value types VRChat accepts for avatar parameters.</summary>
public enum ParameterType : byte
{
    Bool = 0,
    Int = 1,
    Float = 2,
}

/// <summary>
/// Allocation-free tagged union holding a VRChat avatar parameter value (bool, int or float).
/// </summary>
[StructLayout(LayoutKind.Auto)]
[JsonConverter(typeof(OscValueJsonConverter))]
public readonly struct OscValue : IEquatable<OscValue>
{
    private readonly int _int;
    private readonly float _float;

    private OscValue(ParameterType type, int i, float f)
    {
        Type = type;
        _int = i;
        _float = f;
    }

    public ParameterType Type { get; }

    public static OscValue FromBool(bool value) => new(ParameterType.Bool, value ? 1 : 0, 0f);

    public static OscValue FromInt(int value) => new(ParameterType.Int, value, 0f);

    public static OscValue FromFloat(float value) => new(ParameterType.Float, 0, value);

    public static implicit operator OscValue(bool value) => FromBool(value);

    public static implicit operator OscValue(int value) => FromInt(value);

    public static implicit operator OscValue(float value) => FromFloat(value);

    /// <summary>Returns the value as bool. Numeric values are truthy when non-zero, like VRChat does.</summary>
    public bool AsBool() => Type switch
    {
        ParameterType.Float => _float != 0f,
        _ => _int != 0,
    };

    /// <summary>Returns the value as int. Floats are rounded to the nearest integer.</summary>
    public int AsInt() => Type == ParameterType.Float ? (int)MathF.Round(_float) : _int;

    /// <summary>Returns the value as float.</summary>
    public float AsFloat() => Type == ParameterType.Float ? _float : _int;

    /// <summary>Converts this value to the requested type (used when a widget targets a parameter of another type).</summary>
    public OscValue ConvertTo(ParameterType type) => type switch
    {
        ParameterType.Bool => FromBool(AsBool()),
        ParameterType.Int => FromInt(AsInt()),
        _ => FromFloat(AsFloat()),
    };

    public bool Equals(OscValue other) => Type == other.Type && _int == other._int && _float.Equals(other._float);

    public override bool Equals(object? obj) => obj is OscValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Type, _int, _float);

    public static bool operator ==(OscValue left, OscValue right) => left.Equals(right);

    public static bool operator !=(OscValue left, OscValue right) => !left.Equals(right);

    public override string ToString() => Type switch
    {
        ParameterType.Bool => AsBool() ? "true" : "false",
        ParameterType.Int => _int.ToString(CultureInfo.InvariantCulture),
        _ => _float.ToString("0.0###", CultureInfo.InvariantCulture),
    };
}
