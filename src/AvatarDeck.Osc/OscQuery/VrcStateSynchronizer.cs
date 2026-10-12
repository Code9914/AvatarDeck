using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Parameters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AvatarDeck.Osc.OscQuery;

/// <summary>
/// Keeps AvatarDeck's view of VRChat complete: reads the full state through OSCQuery at startup,
/// after every avatar change, and periodically while VRChat is not reachable yet.
/// Also acts as the most accurate <see cref="IAvatarDefinitionSource"/> (live parameter list + avatar name).
/// </summary>
public sealed class VrcStateSynchronizer : BackgroundService, IVrcStateSync, IAvatarDefinitionSource
{
    /// <summary>VRChat needs a moment after an avatar switch before every parameter has its value.</summary>
    private static readonly TimeSpan AvatarSettleDelay = TimeSpan.FromSeconds(1.5);

    private readonly VrcOscQueryClient _query;
    private readonly VrcOscClient _client;
    private readonly ILogger<VrcStateSynchronizer> _logger;
    private readonly TimeSpan _retryInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private VrcStateSnapshot? _lastSnapshot;
    private volatile bool _synced;
    private bool _applying;
    private int _disposed;

    public VrcStateSynchronizer(VrcOscQueryClient query, VrcOscClient client, IOptions<VrcOscOptions> options, ILogger<VrcStateSynchronizer> logger)
    {
        _query = query;
        _client = client;
        _logger = logger;
        _retryInterval = TimeSpan.FromSeconds(Math.Max(2, options.Value.SyncRetrySeconds));
        _client.AvatarChanged += OnAvatarChanged;
    }

    public string? CurrentAvatarName => _lastSnapshot?.AvatarName;

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _query.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                _synced = false;
                return false;
            }

            _lastSnapshot = snapshot;
            var values = new List<KeyValuePair<string, OscValue>>(snapshot.Parameters.Count);
            foreach (var (definition, value) in snapshot.Parameters)
            {
                if (value is { } v)
                {
                    values.Add(new(definition.Name, v));
                }
            }

            _applying = true;
            try
            {
                _client.ApplySnapshot(snapshot.AvatarId, values);
            }
            finally
            {
                _applying = false;
            }

            _synced = true;
            _logger.LogInformation("Synced {Count} parameter values from VRChat (avatar {Name})", values.Count, snapshot.AvatarName);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AvatarDefinition?> TryGetAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        var snapshot = _lastSnapshot;
        return Task.FromResult(snapshot is not null && snapshot.AvatarId == avatarId
            ? new AvatarDefinition(avatarId, snapshot.AvatarName ?? avatarId, snapshot.Parameters.Select(p => p.Definition).ToList())
            : null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_synced)
                {
                    await SyncAsync(stoppingToken).ConfigureAwait(false);
                }

                // Sleep until the retry interval elapses or an avatar change wakes us up. Once synced,
                // this only wakes on avatar changes, so it costs nothing while idle.
                await _wake.WaitAsync(_synced ? Timeout.InfiniteTimeSpan : _retryInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VRChat state sync failed");
                _synced = false;
            }
        }
    }

    private async void OnAvatarChanged(object? sender, AvatarChangedEventArgs e)
    {
        if (_applying)
        {
            return; // raised synchronously by our own ApplySnapshot
        }

        try
        {
            await Task.Delay(AvatarSettleDelay).ConfigureAwait(false);
            _synced = false;
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // already scheduled
        }
    }

    public override void Dispose()
    {
        // Registered under several interfaces: the container may call this more than once.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.AvatarChanged -= OnAvatarChanged;
        _gate.Dispose();
        _wake.Dispose();
        base.Dispose();
    }
}
