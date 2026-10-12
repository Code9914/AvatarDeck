using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.Osc;

/// <summary>
/// UDP transport for OSC. Uses two sockets (send / receive) so that ICMP "port unreachable" replies
/// caused by sending while VRChat is closed never break the receive loop.
/// </summary>
public sealed class OscUdpTransport : IAsyncDisposable
{
    private const int MaxPacketSize = 65_507;
    private const int SioUdpConnReset = -1744830452; // Windows: stop reporting ICMP port unreachable as socket errors

    private readonly ILogger _logger;
    private readonly Socket _sendSocket;
    private Socket? _receiveSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public OscUdpTransport(IPEndPoint remote, ILogger logger)
    {
        Remote = remote;
        _logger = logger;
        _sendSocket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        DisableConnReset(_sendSocket);
    }

    /// <summary>Destination of outgoing packets (VRChat's input port).</summary>
    public IPEndPoint Remote { get; set; }

    /// <summary>Port actually bound for receiving, or null when not listening.</summary>
    public int? ListeningPort => (_receiveSocket?.LocalEndPoint as IPEndPoint)?.Port;

    /// <summary>
    /// Starts receiving on <paramref name="local"/>. Each decoded message is passed to <paramref name="onMessage"/>
    /// on the receive thread — handlers must be fast and must not block.
    /// </summary>
    /// <returns>False if the port is already in use (another OSC app is bound to it).</returns>
    public bool StartListening(IPEndPoint local, Action<OscMessage> onMessage)
    {
        if (_receiveSocket is not null)
        {
            throw new InvalidOperationException("Already listening.");
        }

        var socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        DisableConnReset(socket);
        try
        {
            socket.Bind(local);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket.Dispose();
            _logger.LogWarning("OSC port {Port} is already in use; parameter feedback disabled until OSCQuery is available", local.Port);
            return false;
        }

        _receiveSocket = socket;
        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, onMessage, _receiveCts.Token));
        _logger.LogInformation("Listening for OSC on {EndPoint}", socket.LocalEndPoint);
        return true;
    }

    /// <summary>Encodes and sends one message.</summary>
    public ValueTask SendAsync(string address, OscArgument argument, CancellationToken cancellationToken = default)
    {
        ReadOnlySpan<OscArgument> arguments = [argument];
        var buffer = ArrayPool<byte>.Shared.Rent(OscCodec.GetEncodedSize(address, arguments));
        int length;
        try
        {
            length = OscCodec.Encode(address, arguments, buffer);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return SendAndReturnAsync(buffer, length, cancellationToken);
    }

    private async ValueTask SendAndReturnAsync(byte[] buffer, int length, CancellationToken cancellationToken)
    {
        try
        {
            await _sendSocket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, Remote, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, Action<OscMessage> onMessage, CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(MaxPacketSize, pinned: true);
        var messages = new List<OscMessage>(16);
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                received = result.ReceivedBytes;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "OSC receive error");
                continue;
            }

            messages.Clear();
            if (!OscCodec.TryDecode(buffer.AsSpan(0, received), messages))
            {
                _logger.LogDebug("Dropped malformed OSC packet ({Length} bytes)", received);
            }

            foreach (var message in messages)
            {
                try
                {
                    onMessage(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OSC message handler failed for {Address}", message.Address);
                }
            }
        }
    }

    private static void DisableConnReset(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
    }

    /// <summary>Stops listening (sending remains available).</summary>
    public async Task StopListeningAsync()
    {
        if (_receiveSocket is null)
        {
            return;
        }

        await _receiveCts!.CancelAsync().ConfigureAwait(false);
        _receiveSocket.Dispose();
        if (_receiveTask is not null)
        {
            await _receiveTask.ConfigureAwait(false);
        }

        _receiveCts.Dispose();
        _receiveSocket = null;
        _receiveCts = null;
        _receiveTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopListeningAsync().ConfigureAwait(false);
        _sendSocket.Dispose();
    }
}
