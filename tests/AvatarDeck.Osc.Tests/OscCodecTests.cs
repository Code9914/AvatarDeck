using System.Buffers.Binary;
using System.Text;

namespace AvatarDeck.Osc.Tests;

public class OscCodecTests
{
    [Fact]
    public void Encodes_bool_true_exactly_like_the_spec()
    {
        var buffer = new byte[64];
        var length = OscCodec.Encode("/avatar/parameters/Hoodie", [OscArgument.Bool(true)], buffer);

        // "/avatar/parameters/Hoodie" = 25 chars + NUL → padded to 28; ",T" + NUL → padded to 4; no payload
        length.Should().Be(32);
        Encoding.ASCII.GetString(buffer, 0, 25).Should().Be("/avatar/parameters/Hoodie");
        buffer.AsSpan(25, 3).ToArray().Should().AllBeEquivalentTo((byte)0);
        Encoding.ASCII.GetString(buffer, 28, 2).Should().Be(",T");
    }

    [Fact]
    public void Encodes_float_big_endian()
    {
        var buffer = new byte[64];
        var length = OscCodec.Encode("/a", [OscArgument.Float(0.75f)], buffer);

        length.Should().Be(4 + 4 + 4);
        BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(8)).Should().Be(0.75f);
    }

    [Theory]
    [InlineData("/avatar/parameters/HairHue")]
    [InlineData("/x")]
    [InlineData("/avatar/parameters/Clothes/Hoodie")]
    public void Round_trips_every_argument_kind(string address)
    {
        OscArgument[] arguments =
        [
            OscArgument.Int(-42), OscArgument.Float(0.125f), OscArgument.String("avtr_ünïcødé"),
            OscArgument.Bool(true), OscArgument.Bool(false), OscArgument.Nil,
        ];
        var buffer = new byte[OscCodec.GetEncodedSize(address, arguments)];
        var length = OscCodec.Encode(address, arguments, buffer);

        length.Should().Be(buffer.Length);
        length.Should().Be(length / 4 * 4, "OSC packets are 4-byte aligned");
        OscCodec.TryDecodeMessage(buffer, out var message).Should().BeTrue();
        message.Address.Should().Be(address);
        message.Arguments.Should().Equal(arguments);
    }

    [Fact]
    public void Decodes_nested_bundles()
    {
        var first = Encode("/avatar/parameters/A", OscArgument.Int(1));
        var second = Encode("/avatar/parameters/B", OscArgument.Float(0.5f));
        var inner = Bundle(second);
        var outer = Bundle(first, inner);

        var messages = new List<OscMessage>();
        OscCodec.TryDecode(outer, messages).Should().BeTrue();

        messages.Select(m => m.Address).Should().Equal("/avatar/parameters/A", "/avatar/parameters/B");
        messages[1].Arguments[0].FloatValue.Should().Be(0.5f);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { (byte)'/', (byte)'a', 0 })]                  // not aligned
    [InlineData(new byte[] { (byte)'/', (byte)'a', (byte)'b', (byte)'c' })] // unterminated
    [InlineData(new byte[] { (byte)'/', 0, 0, 0, (byte)',', (byte)'i', 0, 0 })] // int tag without payload
    public void Rejects_malformed_packets(byte[] packet)
    {
        OscCodec.TryDecode(packet, new List<OscMessage>()).Should().BeFalse();
    }

    [Fact]
    public void Rejects_bundle_with_oversized_element()
    {
        var bundle = Bundle(Encode("/a", OscArgument.Int(1)));
        BinaryPrimitives.WriteInt32BigEndian(bundle.AsSpan(16), 9999);

        OscCodec.TryDecode(bundle, new List<OscMessage>()).Should().BeFalse();
    }

    [Fact]
    public void Encode_throws_when_destination_too_small()
    {
        var act = () => OscCodec.Encode("/avatar/parameters/Hoodie", [OscArgument.Int(1)], new byte[8]);
        act.Should().Throw<ArgumentException>();
    }

    private static byte[] Encode(string address, OscArgument argument)
    {
        var buffer = new byte[OscCodec.GetEncodedSize(address, [argument])];
        OscCodec.Encode(address, [argument], buffer);
        return buffer;
    }

    private static byte[] Bundle(params byte[][] elements)
    {
        var stream = new MemoryStream();
        stream.Write("#bundle\0"u8);
        stream.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }); // "immediately"
        Span<byte> size = stackalloc byte[4];
        foreach (var element in elements)
        {
            BinaryPrimitives.WriteInt32BigEndian(size, element.Length);
            stream.Write(size);
            stream.Write(element);
        }

        return stream.ToArray();
    }
}
