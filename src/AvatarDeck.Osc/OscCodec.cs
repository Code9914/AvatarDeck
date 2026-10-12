using System.Buffers.Binary;
using System.Text;

namespace AvatarDeck.Osc;

/// <summary>
/// OSC 1.0 encoder/decoder. Encoding writes into a caller-supplied span (no allocations);
/// decoding understands messages and (nested) bundles, which VRChat uses when sending many parameters at once.
/// </summary>
/// <remarks>Spec: https://opensoundcontrol.stanford.edu/spec-1_0.html. All numbers are big-endian, all fields 4-byte aligned.</remarks>
public static class OscCodec
{
    private static readonly byte[] BundleHeader = "#bundle\0"u8.ToArray();
    private const int MaxBundleDepth = 8;

    /// <summary>Exact number of bytes <see cref="Encode"/> will write.</summary>
    public static int GetEncodedSize(string address, ReadOnlySpan<OscArgument> arguments)
    {
        var size = Padded(Encoding.UTF8.GetByteCount(address) + 1) + Padded(arguments.Length + 2);
        foreach (var argument in arguments)
        {
            size += argument.Kind switch
            {
                OscArgumentKind.Int or OscArgumentKind.Float => 4,
                OscArgumentKind.String => Padded(Encoding.UTF8.GetByteCount(argument.StringValue!) + 1),
                _ => 0,
            };
        }

        return size;
    }

    /// <summary>Encodes a message into <paramref name="destination"/>.</summary>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="ArgumentException">The destination is too small or the address is invalid.</exception>
    public static int Encode(string address, ReadOnlySpan<OscArgument> arguments, Span<byte> destination)
    {
        if (string.IsNullOrEmpty(address) || address[0] != '/')
        {
            throw new ArgumentException("OSC address must start with '/'.", nameof(address));
        }

        var required = GetEncodedSize(address, arguments);
        if (destination.Length < required)
        {
            throw new ArgumentException($"Destination too small ({destination.Length} < {required}).", nameof(destination));
        }

        destination[..required].Clear(); // padding bytes must be zero
        var offset = Encoding.UTF8.GetBytes(address, destination);
        offset = Padded(offset + 1);

        destination[offset] = (byte)',';
        for (var i = 0; i < arguments.Length; i++)
        {
            destination[offset + 1 + i] = arguments[i].Kind switch
            {
                OscArgumentKind.Int => (byte)'i',
                OscArgumentKind.Float => (byte)'f',
                OscArgumentKind.String => (byte)'s',
                OscArgumentKind.True => (byte)'T',
                OscArgumentKind.False => (byte)'F',
                _ => (byte)'N',
            };
        }

        offset += Padded(arguments.Length + 2);

        foreach (var argument in arguments)
        {
            switch (argument.Kind)
            {
                case OscArgumentKind.Int:
                    BinaryPrimitives.WriteInt32BigEndian(destination[offset..], argument.IntValue);
                    offset += 4;
                    break;
                case OscArgumentKind.Float:
                    BinaryPrimitives.WriteSingleBigEndian(destination[offset..], argument.FloatValue);
                    offset += 4;
                    break;
                case OscArgumentKind.String:
                    var written = Encoding.UTF8.GetBytes(argument.StringValue!, destination[offset..]);
                    offset += Padded(written + 1);
                    break;
            }
        }

        return offset;
    }

    /// <summary>Decodes a UDP packet (message or bundle) and appends every message found to <paramref name="output"/>.</summary>
    /// <returns>False if the packet is malformed; messages decoded before the error are kept.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> packet, ICollection<OscMessage> output) => TryDecode(packet, output, 0);

    private static bool TryDecode(ReadOnlySpan<byte> packet, ICollection<OscMessage> output, int depth)
    {
        if (packet.Length < 4 || packet.Length % 4 != 0)
        {
            return false;
        }

        if (packet.StartsWith(BundleHeader))
        {
            if (depth >= MaxBundleDepth || packet.Length < 16)
            {
                return false;
            }

            var offset = 16; // header (8) + time tag (8); time tags are ignored — VRChat always sends "immediately"
            while (offset < packet.Length)
            {
                if (offset + 4 > packet.Length)
                {
                    return false;
                }

                var size = BinaryPrimitives.ReadInt32BigEndian(packet[offset..]);
                offset += 4;
                if (size <= 0 || offset + size > packet.Length || !TryDecode(packet.Slice(offset, size), output, depth + 1))
                {
                    return false;
                }

                offset += size;
            }

            return true;
        }

        if (TryDecodeMessage(packet, out var message))
        {
            output.Add(message);
            return true;
        }

        return false;
    }

    /// <summary>Decodes a single OSC message.</summary>
    public static bool TryDecodeMessage(ReadOnlySpan<byte> data, out OscMessage message)
    {
        message = null!;
        var offset = 0;
        if (!TryReadString(data, ref offset, out var address) || address.Length == 0 || address[0] != '/')
        {
            return false;
        }

        // Type tag string is optional in very old OSC implementations; treat absence as "no arguments".
        if (offset >= data.Length)
        {
            message = new OscMessage(address, []);
            return true;
        }

        if (data[offset] != (byte)',')
        {
            return false;
        }

        var tagStart = offset + 1;
        var tagEnd = data[tagStart..].IndexOf((byte)0);
        if (tagEnd < 0)
        {
            return false;
        }

        var tags = data.Slice(tagStart, tagEnd);
        offset = Padded(tagStart + tagEnd + 1);

        var arguments = tags.Length == 0 ? [] : new OscArgument[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            switch (tags[i])
            {
                case (byte)'i':
                    if (offset + 4 > data.Length) return false;
                    arguments[i] = OscArgument.Int(BinaryPrimitives.ReadInt32BigEndian(data[offset..]));
                    offset += 4;
                    break;
                case (byte)'f':
                    if (offset + 4 > data.Length) return false;
                    arguments[i] = OscArgument.Float(BinaryPrimitives.ReadSingleBigEndian(data[offset..]));
                    offset += 4;
                    break;
                case (byte)'s':
                    if (!TryReadString(data, ref offset, out var text)) return false;
                    arguments[i] = OscArgument.String(text);
                    break;
                case (byte)'T':
                    arguments[i] = OscArgument.Bool(true);
                    break;
                case (byte)'F':
                    arguments[i] = OscArgument.Bool(false);
                    break;
                case (byte)'N':
                    arguments[i] = OscArgument.Nil;
                    break;
                default:
                    return false; // unsupported type (blob, double, …): VRChat never sends them
            }
        }

        message = new OscMessage(address, arguments);
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset >= data.Length)
        {
            return false;
        }

        var end = data[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(data.Slice(offset, end));
        offset = Padded(offset + end + 1);
        return offset <= data.Length;
    }

    private static int Padded(int length) => (length + 3) & ~3;
}
