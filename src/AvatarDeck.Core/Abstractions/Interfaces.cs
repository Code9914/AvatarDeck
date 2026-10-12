using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;

namespace AvatarDeck.Core.Abstractions;

/// <summary>Sends avatar parameter values to VRChat.</summary>
public interface IParameterSender
{
    /// <summary>
    /// Sends a value immediately, preserving order. Use for discrete actions (toggles, button press/release, presets).
    /// </summary>
    ValueTask SendAsync(string parameter, OscValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a value with latest-wins coalescing per parameter. Use for continuous input (sliders) to avoid flooding VRChat.
    /// </summary>
    void Post(string parameter, OscValue value);
}

/// <summary>Raised when VRChat reports a parameter value (from any source, including the in-game menu).</summary>
public sealed class ParameterChangedEventArgs(string parameter, OscValue value) : EventArgs
{
    public string Parameter { get; } = parameter;

    public OscValue Value { get; } = value;
}

/// <summary>Raised when the local user switches avatar.</summary>
public sealed class AvatarChangedEventArgs(string avatarId) : EventArgs
{
    public string AvatarId { get; } = avatarId;
}

/// <summary>Live state coming back from VRChat. Events are raised on a background thread.</summary>
public interface IVrcStateFeed
{
    /// <summary>Last avatar id reported by VRChat, if any.</summary>
    string? CurrentAvatarId { get; }

    /// <summary>True when a packet was received from VRChat recently.</summary>
    bool IsVrChatResponding { get; }

    event EventHandler<AvatarChangedEventArgs>? AvatarChanged;

    event EventHandler<ParameterChangedEventArgs>? ParameterChanged;

    /// <summary>Gets the last known value of a parameter.</summary>
    bool TryGetParameter(string parameter, out OscValue value);
}

/// <summary>
/// Pulls the complete current state (active avatar + every parameter value) from VRChat.
/// Plain OSC only reports changes, so without this the panel would not know what is already enabled.
/// </summary>
public interface IVrcStateSync
{
    /// <summary>Name of the active avatar as reported by VRChat, if known.</summary>
    string? CurrentAvatarName { get; }

    /// <summary>Queries VRChat and applies the result to <see cref="IVrcStateFeed"/>.</summary>
    /// <returns>False when VRChat is not reachable.</returns>
    Task<bool> SyncAsync(CancellationToken cancellationToken = default);
}

/// <summary>Persists avatar profiles.</summary>
public interface IProfileStore
{
    Task<AvatarProfile?> LoadAsync(string avatarId, CancellationToken cancellationToken = default);

    Task SaveAsync(AvatarProfile profile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string avatarId, CancellationToken cancellationToken = default);
}

/// <summary>Lightweight profile listing entry (for the quick avatar picker).</summary>
public sealed record ProfileSummary(string AvatarId, string Name, DateTimeOffset UpdatedAt);

/// <summary>Provides the parameter list of an avatar from an external source (VRChat OSC config, Unity project…).</summary>
public interface IAvatarDefinitionSource
{
    Task<AvatarDefinition?> TryGetAsync(string avatarId, CancellationToken cancellationToken = default);
}

/// <summary>Raw avatar description used to generate a profile.</summary>
public sealed record AvatarDefinition(string AvatarId, string Name, IReadOnlyList<ParameterDefinition> Parameters);

/// <summary>Severity of an overlay/desktop notification.</summary>
public enum NotificationLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Shows discreet notifications (toast in the panel, both desktop and VR).</summary>
public interface INotificationService
{
    event EventHandler<Notification>? Raised;

    void Notify(string message, NotificationLevel level = NotificationLevel.Info);
}

/// <summary>A notification message.</summary>
public sealed record Notification(string Message, NotificationLevel Level, DateTimeOffset Timestamp);
