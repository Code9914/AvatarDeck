namespace AvatarDeck.Core.Models;

/// <summary>User preferences persisted in <c>%AppData%\AvatarDeck\preferences.json</c>.</summary>
public sealed class AppSettings
{
    public VrPanelSettings Vr { get; set; } = new();
}

/// <summary>Where the VR panel is attached.</summary>
public enum PanelAnchor
{
    /// <summary>Pinned in the play space.</summary>
    World,

    LeftHand,

    RightHand,
}

/// <summary>VR panel preferences.</summary>
public sealed class VrPanelSettings
{
    /// <summary>Panel width in meters.</summary>
    public float WidthMeters { get; set; } = 0.55f;

    /// <summary>0.2 – 1.</summary>
    public float Opacity { get; set; } = 0.97f;

    /// <summary>A locked panel cannot be grabbed or resized.</summary>
    public bool Locked { get; set; }

    public PanelAnchor Anchor { get; set; } = PanelAnchor.World;

    /// <summary>
    /// Panel pose relative to its anchor (row-major 3×4, OpenVR convention). Null = default placement.
    /// For <see cref="PanelAnchor.World"/> it is relative to the standing play space.
    /// </summary>
    public float[]? Transform { get; set; }

    /// <summary>Show the small launcher button above the wrist.</summary>
    public bool WristButton { get; set; } = true;

    /// <summary>Wrist button pose relative to the carrying controller (row-major 3×4). Null = default (on the back of the hand).</summary>
    public float[]? WristTransform { get; set; }

    /// <summary>Hand carrying the wrist button.</summary>
    public PanelAnchor WristHand { get; set; } = PanelAnchor.LeftHand;

    /// <summary>Supersampling of the panel texture (1 = 1 texel per UI pixel). Higher is sharper but heavier.</summary>
    public float RenderScale { get; set; } = 1.5f;
}
