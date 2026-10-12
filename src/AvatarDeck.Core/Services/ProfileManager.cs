using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.Core.Services;

/// <summary>
/// Owns the active avatar profile. Follows VRChat avatar changes, loads the matching profile,
/// and generates one automatically (from VRChat's OSC config) the first time an avatar is seen.
/// </summary>
public sealed class ProfileManager : IDisposable
{
    private readonly IProfileStore _store;
    private readonly IVrcStateFeed _feed;
    private readonly IEnumerable<IAvatarDefinitionSource> _sources;
    private readonly INotificationService _notifications;
    private readonly ILogger<ProfileManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileManager(
        IProfileStore store,
        IVrcStateFeed feed,
        IEnumerable<IAvatarDefinitionSource> sources,
        INotificationService notifications,
        ILogger<ProfileManager> logger)
    {
        _store = store;
        _feed = feed;
        _sources = sources;
        _notifications = notifications;
        _logger = logger;
        _feed.AvatarChanged += OnAvatarChanged;
    }

    /// <summary>The profile currently displayed. Null until an avatar is known.</summary>
    public AvatarProfile? ActiveProfile { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="ActiveProfile"/> is replaced.</summary>
    public event EventHandler<AvatarProfile?>? ActiveProfileChanged;

    /// <summary>
    /// Restores a profile at startup: the avatar VRChat already reported, otherwise the most recently used one.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_feed.CurrentAvatarId is { } current)
        {
            await ActivateAsync(current, cancellationToken).ConfigureAwait(false);
            return;
        }

        var recent = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefault();
        if (recent is not null)
        {
            await ActivateAsync(recent.AvatarId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Loads (or creates) the profile for an avatar and makes it active. Also used by the quick avatar picker.</summary>
    public async Task ActivateAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveProfile?.AvatarId == avatarId)
            {
                return;
            }

            var profile = await _store.LoadAsync(avatarId, cancellationToken).ConfigureAwait(false);
            var definition = await FindDefinitionAsync(avatarId, cancellationToken).ConfigureAwait(false);

            if (profile is null)
            {
                if (definition is not null)
                {
                    profile = ProfileBuilder.Build(definition, ProfileSource.VrcOscConfig);
                    _notifications.Notify($"New profile generated for {profile.Name}", NotificationLevel.Success);
                }
                else
                {
                    profile = new AvatarProfile { AvatarId = avatarId, Name = avatarId };
                    _notifications.Notify("Unknown avatar: pick a profile or import a Unity project", NotificationLevel.Warning);
                }

                await _store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            else if (definition is not null && profile.Source != ProfileSource.Manual && !profile.IsCustomized
                     && profile.GeneratorVersion < ProfileBuilder.GeneratorVersion)
            {
                // Generated layout never touched by the user: upgrade it to the improved organisation rules.
                profile = ProfileBuilder.Reorganize(profile, definition);
                await _store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Re-organised profile {AvatarId} with generator v{Version}", avatarId, ProfileBuilder.GeneratorVersion);
            }
            else if (definition is not null)
            {
                var added = ProfileBuilder.MergeMissing(profile, definition);
                var renamed = profile.Name == profile.AvatarId && !string.IsNullOrWhiteSpace(definition.Name) && definition.Name != avatarId;
                if (renamed)
                {
                    profile.Name = definition.Name; // placeholder profile created before the real name was known
                }

                if (added > 0 || renamed)
                {
                    _logger.LogInformation("Added {Count} new parameters to profile {AvatarId}", added, avatarId);
                    await _store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
                }
            }

            ActiveProfile = profile;
            _logger.LogInformation("Active profile: {Name} ({AvatarId})", profile.Name, profile.AvatarId);
        }
        finally
        {
            _gate.Release();
        }

        ActiveProfileChanged?.Invoke(this, ActiveProfile);
    }

    /// <summary>
    /// Rebuilds the active profile's layout with the automatic rules (keeps presets and favorites).
    /// </summary>
    /// <returns>False when no parameter list is available (VRChat not reachable and no OSC config).</returns>
    public async Task<bool> ReorganizeActiveAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveProfile is not { } current)
        {
            return false;
        }

        var definition = await FindDefinitionAsync(current.AvatarId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveProfile = ProfileBuilder.Reorganize(current, definition);
            await _store.SaveAsync(ActiveProfile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        ActiveProfileChanged?.Invoke(this, ActiveProfile);
        return true;
    }

    /// <summary>Persists the active profile (call after user edits; callers should debounce).</summary>
    public async Task SaveActiveAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveProfile is not { } profile)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AvatarDefinition?> FindDefinitionAsync(string avatarId, CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            try
            {
                if (await source.TryGetAsync(avatarId, cancellationToken).ConfigureAwait(false) is { } definition)
                {
                    return definition;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Avatar definition source {Source} failed for {AvatarId}", source.GetType().Name, avatarId);
            }
        }

        return null;
    }

    private async void OnAvatarChanged(object? sender, AvatarChangedEventArgs e)
    {
        // async void is confined to this event handler; every exception is caught and logged.
        try
        {
            await ActivateAsync(e.AvatarId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to activate profile for avatar {AvatarId}", e.AvatarId);
            _notifications.Notify("Could not load avatar profile", NotificationLevel.Error);
        }
    }

    public void Dispose()
    {
        _feed.AvatarChanged -= OnAvatarChanged;
        _gate.Dispose();
    }
}
