using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvatarDeck.Core.Parameters;

/// <summary>
/// Serializes <see cref="OscValue"/> as a plain JSON primitive so profiles stay hand-editable:
/// <c>true</c> (bool), <c>3</c> (int) or <c>0.75</c> / <c>1.0</c> (float — always written with a decimal point).
/// </summary>
public sealed class OscValueJsonConverter : JsonConverter<OscValue>
{
    public override OscValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return OscValue.FromBool(true);
            case JsonTokenType.False:
                return OscValue.FromBool(false);
            case JsonTokenType.Number:
                var raw = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
                var isFloat = Array.IndexOf(raw, (byte)'.') >= 0 || Array.IndexOf(raw, (byte)'e') >= 0 || Array.IndexOf(raw, (byte)'E') >= 0;
                return isFloat ? OscValue.FromFloat(reader.GetSingle()) : OscValue.FromInt(reader.GetInt32());
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for an OSC value.");
        }
    }

    public override void Write(Utf8JsonWriter writer, OscValue value, JsonSerializerOptions options)
    {
        switch (value.Type)
        {
            case ParameterType.Bool:
                writer.WriteBooleanValue(value.AsBool());
                break;
            case ParameterType.Int:
                writer.WriteNumberValue(value.AsInt());
                break;
            default:
                var text = value.AsFloat().ToString("R", CultureInfo.InvariantCulture);
                if (!text.Contains('.', StringComparison.Ordinal) && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
                {
                    text += ".0";
                }

                writer.WriteRawValue(text, skipInputValidation: true);
                break;
        }
    }
}
