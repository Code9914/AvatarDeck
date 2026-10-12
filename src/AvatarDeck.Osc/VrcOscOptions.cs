namespace AvatarDeck.Osc;

/// <summary>VRChat OSC connection settings (section <c>Osc</c> in <c>appsettings.json</c>).</summary>
public sealed class VrcOscOptions
{
    public const string SectionName = "Osc";

    /// <summary>Host running VRChat.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>VRChat's input port (we send here).</summary>
    public int SendPort { get; set; } = 9000;

    /// <summary>VRChat's output port (we listen here).</summary>
    public int ReceivePort { get; set; } = 9001;

    /// <summary>Address the receive socket binds to.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>Minimum interval between two coalesced sends of the same parameter (≈ one 60 Hz frame).</summary>
    public int CoalesceIntervalMilliseconds { get; set; } = 16;

    /// <summary>VRChat is considered responsive if a packet arrived within this delay.</summary>
    public int RespondingTimeoutSeconds { get; set; } = 30;

    /// <summary>VRChat's OSCQuery HTTP port. 0 = discover it automatically from VRChat's log.</summary>
    public int OscQueryPort { get; set; }

    /// <summary>VRChat data folder (logs). Empty = <c>%LocalAppData%Low\VRChat\VRChat</c>.</summary>
    public string? VrChatDataDirectory { get; set; }

    /// <summary>Retry interval while VRChat is not reachable.</summary>
    public int SyncRetrySeconds { get; set; } = 10;
}
