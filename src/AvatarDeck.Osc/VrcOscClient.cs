using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Parameters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AvatarDeck.Osc;

/// <summary>
/// VRChat OSC endpoint: sends avatar parameters, tracks values and avatar changes reported by VRChat.
/// </summary>
/// <remarks>
/// Continuous input (<see cref="Post"/>) is coalesced per parameter: while a slider is dragged only the latest
/// value is sent, at most once per <see cref="VrcOscOptions.CoalesceIntervalMilliseconds"/>. The flush loop
/// waits on a channel, so it costs nothing while idle.
/// </remarks>
public sealed class VrcOscClient : IParameterSender, IVrcStateFeed, IHostedService, IAsyncDisposable
{
    private readonly VrcOscOptions _options;
    private readonly ILogger<VrcOscClient> _logger;
    private readonly TimeProvider _time;
    private readonly OscUdpTransport _transport;
    private readonly ConcurrentDictionary<string, OscValue> _parameters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OscValue> _pending = new(StringComparer.Ordinal);
    private readonly Channel<bool> _flushSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _lastPacketTimestamp;
    private volatile string? _currentAvatarId;
    private int _disposed;

    public VrcOscClient(IOptions<VrcOscOptions> options, ILogger<VrcOscClient> logger, TimeProvider time)
    {
        _options = options.Value;
        _logger = logger;
        _time = time;
        _transport = new OscUdpTransport(new IPEndPoint(IPAddress.Parse(_options.Host), _options.SendPort), logger);
    }

    public event EventHandler<AvatarChangedEventArgs>? AvatarChanged;

    public event EventHandler<ParameterChangedEventArgs>? ParameterChanged;

    /// <summary>Raised for every received message (used by the OSC monitor / history views).</summary>
    public event EventHandler<OscMessage>? MessageReceived;

    public string? CurrentAvatarId => _currentAvatarId;

    public bool IsVrChatResponding
    {
        get
        {
            var last = Interlocked.Read(ref _lastPacketTimestamp);
            return last != 0 && _time.GetElapsedTime(last) < TimeSpan.FromSeconds(_options.RespondingTimeoutSeconds);
        }
    }

    /// <summary>True when the feedback port could be bound.</summary>
    public bool IsListening => _transport.ListeningPort is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _transport.StartListening(new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.ReceivePort), OnMessage);
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("VRChat OSC client sending to {Host}:{Port}", _options.Host, _options.SendPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent: called by the host, then again from DisposeAsync.
        if (Interlocked.Exchange(ref _cts, null) is not { } cts)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (_flushTask is not null)
        {
            await _flushTask.ConfigureAwait(false);
        }

        await _transport.StopListeningAsync().ConfigureAwait(false);
        cts.Dispose();
    }

    public ValueTask SendAsync(string parameter, OscValue value, CancellationToken cancellationToken = default)
    {
        _pending.TryRemove(parameter, out _); // an explicit send supersedes a queued coalesced value
        _parameters[parameter] = value;
        return SendCoreAsync(parameter, value, cancellationToken);
    }

    public void Post(string parameter, OscValue value)
    {
        _pending[parameter] = value;
        _parameters[parameter] = value;
        _flushSignal.Writer.TryWrite(true);
    }

    public bool TryGetParameter(string parameter, out OscValue value) => _parameters.TryGetValue(parameter, out value);

    /// <summary>
    /// Applies a full state read from VRChat (OSCQuery): switches avatar if needed, then reports every value
    /// that differs from what is known. Events are raised synchronously on the calling thread.
    /// </summary>
    public void ApplySnapshot(string? avatarId, IEnumerable<KeyValuePair<string, OscValue>> values)
    {
        Interlocked.Exchange(ref _lastPacketTimestamp, _time.GetTimestamp());

        if (avatarId is { Length: > 0 } && avatarId != _currentAvatarId)
        {
            _currentAvatarId = avatarId;
            _parameters.Clear();
            _logger.LogInformation("Active avatar (from OSCQuery): {AvatarId}", avatarId);
            AvatarChanged?.Invoke(this, new AvatarChangedEventArgs(avatarId));
        }

        foreach (var (name, value) in values)
        {
            if (_pending.ContainsKey(name))
            {
                continue; // a user change is about to be sent: it wins
            }

            if (!_parameters.TryGetValue(name, out var previous) || previous != value)
            {
                _parameters[name] = value;
                ParameterChanged?.Invoke(this, new ParameterChangedEventArgs(name, value));
            }
        }
    }

    private async ValueTask SendCoreAsync(string parameter, OscValue value, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.SendAsync(ParameterAddress.For(parameter), OscArgument.From(value), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Failed to send {Parameter}={Value}", parameter, value);
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.CoalesceIntervalMilliseconds);
        try
        {
            while (await _flushSignal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _flushSignal.Reader.TryRead(out _);
                foreach (var key in _pending.Keys)
                {
                    if (_pending.TryRemove(key, out var value))
                    {
                        await SendCoreAsync(key, value, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Rate limit: values posted during this delay are merged into the next flush.
                await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void OnMessage(OscMessage message)
    {
        Interlocked.Exchange(ref _lastPacketTimestamp, _time.GetTimestamp());
        MessageReceived?.Invoke(this, message);

        if (message.Arguments.Length == 0)
        {
            return;
        }

        if (message.Address == ParameterAddress.AvatarChange)
        {
            if (message.Arguments[0].StringValue is { Length: > 0 } avatarId && avatarId != _currentAvatarId)
            {
                _currentAvatarId = avatarId;
                _parameters.Clear();
                _logger.LogInformation("Avatar changed to {AvatarId}", avatarId);
                AvatarChanged?.Invoke(this, new AvatarChangedEventArgs(avatarId));
            }

            return;
        }

        if (ParameterAddress.TryGetName(message.Address, out var name) && message.Arguments[0].TryGetValue(out var value))
        {
            // Ignore echoes of a value we are still about to send, so the UI does not jump back while dragging.
            if (_pending.ContainsKey(name))
            {
                return;
            }

            if (!_parameters.TryGetValue(name, out var previous) || previous != value)
            {
                _parameters[name] = value;
                ParameterChanged?.Invoke(this, new ParameterChangedEventArgs(name, value));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The container may dispose this instance once per interface it is registered under.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
