using System.Net;
using System.Net.Sockets;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Parameters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AvatarDeck.Osc.Tests;

/// <summary>End-to-end tests over real loopback UDP sockets, with a fake "VRChat" on the other side.</summary>
public sealed class VrcOscClientTests : IAsyncLifetime
{
    private readonly UdpClient _fakeVrChat = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly int _clientReceivePort = GetFreePort();
    private VrcOscClient _client = null!;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new VrcOscOptions
        {
            SendPort = ((IPEndPoint)_fakeVrChat.Client.LocalEndPoint!).Port,
            ReceivePort = _clientReceivePort,
            CoalesceIntervalMilliseconds = 50,
        });
        _client = new VrcOscClient(options, NullLogger<VrcOscClient>.Instance, TimeProvider.System);
        await _client.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        _fakeVrChat.Dispose();
    }

    [Fact]
    public async Task SendAsync_sends_typed_parameter()
    {
        await _client.SendAsync("Hoodie", OscValue.FromBool(true));

        var message = await ReceiveAsync();
        message.Address.Should().Be("/avatar/parameters/Hoodie");
        message.Arguments.Should().Equal(OscArgument.Bool(true));
    }

    [Fact]
    public async Task Post_coalesces_rapid_updates_to_latest_value()
    {
        for (var i = 1; i <= 100; i++)
        {
            _client.Post("HairHue", OscValue.FromFloat(i / 100f));
        }

        var messages = new List<OscMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            while (true)
            {
                messages.Add(await ReceiveAsync(cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
        }

        messages.Should().NotBeEmpty().And.HaveCountLessThan(10, "100 posts must be merged");
        messages[^1].Arguments[0].FloatValue.Should().Be(1f);
    }

    [Fact]
    public async Task Raises_avatar_and_parameter_events_from_vrchat_packets()
    {
        var avatar = new TaskCompletionSource<string>();
        var parameter = new TaskCompletionSource<ParameterChangedEventArgs>();
        _client.AvatarChanged += (_, e) => avatar.TrySetResult(e.AvatarId);
        _client.ParameterChanged += (_, e) => parameter.TrySetResult(e);

        await SendFromVrChatAsync("/avatar/change", OscArgument.String("avtr_test"));
        (await avatar.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("avtr_test");

        await SendFromVrChatAsync("/avatar/parameters/Tail", OscArgument.Int(3));
        var e = await parameter.Task.WaitAsync(TimeSpan.FromSeconds(2));
        e.Parameter.Should().Be("Tail");
        e.Value.Should().Be(OscValue.FromInt(3));

        _client.CurrentAvatarId.Should().Be("avtr_test");
        _client.TryGetParameter("Tail", out var stored).Should().BeTrue();
        stored.AsInt().Should().Be(3);
        _client.IsVrChatResponding.Should().BeTrue();
    }

    private async Task<OscMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var result = await _fakeVrChat.ReceiveAsync(timeout.Token);
        OscCodec.TryDecodeMessage(result.Buffer, out var message).Should().BeTrue();
        return message;
    }

    private async Task SendFromVrChatAsync(string address, OscArgument argument)
    {
        var buffer = new byte[OscCodec.GetEncodedSize(address, [argument])];
        OscCodec.Encode(address, [argument], buffer);
        await _fakeVrChat.SendAsync(buffer, new IPEndPoint(IPAddress.Loopback, _clientReceivePort));
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
