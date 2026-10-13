using AvatarDeck.Core.Models;

namespace AvatarDeck.Core.Abstractions;

/// <summary>A rendered panel frame, top row first: straight-alpha RGBA (<see cref="IsRgba"/>) or premultiplied BGRA.</summary>
public sealed class OverlayFrame(byte[] pixels, int width, int height, bool isRgba = false)
{
    /// <summary>True for straight-alpha RGBA (ready for SteamVR's raw upload), false for premultiplied BGRA.</summary>
    public bool IsRgba { get; } = isRgba;

    public byte[] Pixels { get; } = pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride => Width * 4;
}

/// <summary>
/// The UI rendered off-screen for the VR overlay. Implemented by the UI layer (Avalonia), consumed by the VR layer.
/// All methods are thread-safe: they marshal to the UI thread.
/// </summary>
public interface IOverlaySurface
{
    /// <summary>True once the UI is ready to render.</summary>
    bool IsReady { get; }

    /// <summary>Size of rendered frames in pixels.</summary>
    (int Width, int Height) PixelSize { get; }

    /// <summary>
    /// Starts or stops rendering. While active, frames are produced only when the content changes
    /// (input, VRChat values, animations), so an idle visible panel costs almost nothing.
    /// </summary>
    void SetActive(bool active);

    /// <summary>Returns the latest frame if a new one was rendered since the last call, otherwise null.</summary>
    OverlayFrame? TakeFrame();

    /// <summary>Pointer position in normalised panel coordinates (0,0 = top-left, 1,1 = bottom-right).</summary>
    void PointerMove(double x, double y);

    void PointerDown();

    void PointerUp();

    /// <summary>The laser left the panel.</summary>
    void PointerLeave();

    /// <summary>Vertical scroll, in "wheel notches" (positive = up).</summary>
    void Scroll(double notches);
}

/// <summary>Connection state of the VR runtime.</summary>
public enum VrStatus
{
    /// <summary>SteamVR is not running; the service waits for it.</summary>
    WaitingForSteamVr,

    Connected,

    /// <summary>OpenVR is not usable (runtime missing, init failure). See <see cref="IVrPanelController.StatusMessage"/>.</summary>
    Error,
}

/// <summary>Controls the VR panel. Implemented by the VR layer; used by the UI (settings sheet, desktop button).</summary>
public interface IVrPanelController
{
    VrStatus Status { get; }

    string StatusMessage { get; }

    bool IsPanelVisible { get; }

    /// <summary>Current panel settings (live object; call <see cref="ApplySettings"/> after changing it).</summary>
    VrPanelSettings Settings { get; }

    /// <summary>Raised (any thread) when status, visibility or settings change.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised (VR thread) when SteamVR is shutting down.</summary>
    event EventHandler? SteamVrExiting;

    void ShowPanel();

    void HidePanel();

    void TogglePanel();

    /// <summary>Places the panel in front of the user's head (world anchor) or resets the hand offset.</summary>
    void Recenter();

    /// <summary>Applies and persists <see cref="Settings"/> (opacity, size, anchor, lock…).</summary>
    void ApplySettings();
}
