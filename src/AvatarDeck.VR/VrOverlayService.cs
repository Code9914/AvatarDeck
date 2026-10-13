using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Valve.VR;

namespace AvatarDeck.VR;

/// <summary>
/// SteamVR overlay host. Waits for SteamVR (never launches it), then shows four overlays: the panel (the tablet UI),
/// a move handle under it, a laser cursor and a wrist launcher button.
/// </summary>
/// <remarks>
/// <para>Interaction is trigger-only, so the grip stays free for the game:</para>
/// <list type="bullet">
/// <item>Point + trigger on the panel = click (the aim is frozen as soon as the trigger starts moving, to cancel the
/// controller dip of the pull). Trigger + drag up/down scrolls (handled by the UI).</item>
/// <item>Trigger held on the handle = move the panel; trigger with the other hand meanwhile = resize.</item>
/// <item>Wrist button: trigger tap = show/hide; trigger held + move = reposition it on the hand.</item>
/// </list>
/// <para>AvatarDeck uses its own laser (ray/panel intersection) instead of SteamVR's laser mouse, which would block
/// all game input while the overlay is visible. The loop runs on a dedicated thread, synchronised with the
/// compositor (<c>WaitFrameSync</c>, 90/120 Hz) while the panel is visible, ~20 Hz otherwise.</para>
/// </remarks>
public sealed class VrOverlayService : BackgroundService, IVrPanelController
{
    /// <summary>Set by <c>--vr-diag &lt;dir&gt;</c>: compositor readback after each panel update (see <see cref="CompositorReadback"/>).</summary>
    public static string? DiagnosticsDirectory { get; set; }

    private const string PanelKey = "avatardeck.panel";
    private const string HandleKey = "avatardeck.handle";
    private const string CursorKey = "avatardeck.cursor";
    private const string WristKey = "avatardeck.wrist";
    private const float CursorWidth = 0.016f;
    private const float WristWidth = 0.055f;
    private const float HandleWidth = 0.2f;
    private const int HandleTexWidth = 256;
    private const int HandleTexHeight = 36;
    private const float HandleGap = 0.03f;
    private const float MinWidth = 0.2f;
    private const float MaxWidth = 2f;

    /// <summary>Trigger travel at which the click point is frozen (well before the click threshold).</summary>
    private const float StabilizeThreshold = 0.12f;

    /// <summary>Pointer movement (meters) that turns a wrist-button press into a drag.</summary>
    private const float WristDragDistance = 0.025f;

    private static readonly TimeSpan WaitForSteamVrInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();

    private readonly IOverlaySurface _surface;
    private readonly ISettingsStore _settings;
    private readonly ILogger<VrOverlayService> _logger;
    private readonly ConcurrentQueue<Action> _commands = new();

    private volatile VrStatus _status = VrStatus.WaitingForSteamVr;
    private volatile string _statusMessage = "Waiting for SteamVR…";
    private volatile bool _wantVisible;
    private bool _connected;
    private bool _visible;

    private D3D11Device? _d3d;
    private OverlayTexture? _panelTexture;
    private OverlayTexture? _handleTexture;
    private OverlayTexture? _cursorTexture;
    private OverlayTexture? _kickTexture;
    private OverlayTexture? _wristTexture;
    private VrInput? _input;
    /// <summary>
    /// Two panel overlays used alternately: a new frame is uploaded (SetOverlayRaw) to the hidden one, which is shown
    /// a couple of VR frames later, once SteamVR has fully received it. The visible panel is never half-updated,
    /// so there is no flicker. (Shared D3D11 textures do not refresh reliably in the headset; see OverlayTexture.)
    /// </summary>
    private readonly ulong[] _panels = new ulong[2];
    private int _front;
    private int _swapCountdown;

    /// <summary>
    /// VR frames between a raw upload and showing it: SetOverlayRaw returns before SteamVR has rebuilt its texture,
    /// so showing the buffer too early can display it empty for a frame (flicker).
    /// </summary>
    private const int SwapDelayFrames = 3;

    /// <summary>
    /// The back panel stays *visible* but nearly transparent: SteamVR defers texture updates of hidden overlays until
    /// they are shown, which made the content jump back to an old frame at each swap.
    /// </summary>
    private const float BackPanelAlpha = 0.02f;
    private long _lastPanelUpload;

    /// <summary>Panel uploads are capped at ~60 per second.</summary>
    private static readonly TimeSpan PanelUploadInterval = TimeSpan.FromMilliseconds(16);

    private ulong FrontPanel => _panels[_front];

    /// <summary>Frame refused by SteamVR, re-uploaded on a later VR frame unless a newer one arrives.</summary>
    private OverlayFrame? _retryFrame;
    private DateTime? _uploadFailureSince;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(30);

    /// <summary>Uploads a frame to the back panel and schedules the swap; a refused upload is retried later.</summary>
    private void UploadPanelFrame(OverlayFrame frame)
    {
        _lastPanelUpload = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_panelTexture!.Submit(BackPanel, frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.IsRgba))
        {
            // SteamVR refuses raw uploads (RequestFailed) after a couple of hundred uploads to the same overlay:
            // resources it only frees when the overlay is destroyed. The failing overlay is always the back one
            // (2 % opacity), so it is recreated invisibly while the front panel stays on screen, then retried.
            ReportUploadFailure(_panelTexture.LastError);
            RecreateBackPanel();
            if (!_panelTexture.Submit(BackPanel, frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.IsRgba))
            {
                _retryFrame = frame;
                _lastPanelUpload += (long)(RetryBackoff.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                return;
            }
        }

        _retryFrame = null;
        _uploadFailureSince = null;
        _swapCountdown = SwapDelayFrames;
        _uploadCount++;
        _uploadTotalMs += _panelTexture.LastUploadMs;
        if (DateTime.UtcNow - _uploadStatsSince > TimeSpan.FromSeconds(2))
        {
            _logger.LogDebug("VR upload: {Count} panel uploads, avg {Avg:0.0} ms", _uploadCount, _uploadTotalMs / _uploadCount);
            _uploadCount = 0;
            _uploadTotalMs = 0;
            _uploadStatsSince = DateTime.UtcNow;
        }

        if (_readback is not null)
        {
            _readback.RecordSubmitted(frame.Pixels, frame.Width, frame.Height, frame.IsRgba);
            _diagChecks.Clear();
            _diagChecks.Enqueue(DateTime.UtcNow.AddMilliseconds(100));
            _diagChecks.Enqueue(DateTime.UtcNow.AddMilliseconds(600));
            _diagChecks.Enqueue(DateTime.UtcNow.AddMilliseconds(2000));
        }
    }

    private void KickCompositor()
    {
        if (PanelWorld() is { } panel)
        {
            // Hidden behind the panel centre; positioned so it is "visible" (hidden overlays are not processed).
            var transform = VrMath.ToHmd(Matrix4x4.CreateTranslation(0, 0, -0.01f) * panel);
            OpenVR.Overlay.SetOverlayTransformAbsolute(_kick, ETrackingUniverseOrigin.TrackingUniverseStanding, ref transform);
        }

        OpenVR.Overlay.ShowOverlay(_kick);
        _kickTexture?.Submit(_kick, KickPixels, 4, 4, 16);
    }

    private int _panelGeneration;

    /// <summary>Destroys and recreates the back panel overlay (frees SteamVR's per-overlay upload resources).</summary>
    private void RecreateBackPanel()
    {
        var back = 1 - _front;
        OpenVR.Overlay.DestroyOverlay(_panels[back]);
        _panelGeneration++;
        _panels[back] = CreateOverlay($"{PanelKey}.{_panelGeneration}", "AvatarDeck (buffer)");

        var overlay = OpenVR.Overlay;
        overlay.SetOverlaySortOrder(_panels[back], 1);
        overlay.SetOverlayAlpha(_panels[back], BackPanelAlpha);
        overlay.SetOverlayWidthInMeters(_panels[back], Settings.WidthMeters);
        ApplyPanelTransform();
        overlay.ShowOverlay(_panels[back]);
        _logger.LogInformation("Recreated the back panel overlay after an upload refusal (generation {Generation})", _panelGeneration);
    }

    /// <summary>Upload refusals are expected now and then; only persistent ones (over a second) are worth a warning.</summary>
    private void ReportUploadFailure(EVROverlayError error)
    {
        _uploadFailureSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _uploadFailureSince.Value > TimeSpan.FromSeconds(1))
        {
            _logger.LogWarning("Panel upload refused by SteamVR for over a second ({Error}); still retrying", error);
            _uploadFailureSince = DateTime.UtcNow;
        }
        else
        {
            _logger.LogDebug("Panel upload refused ({Error}); retrying", error);
        }
    }

    private int _uploadCount;
    private double _uploadTotalMs;
    private DateTime _uploadStatsSince = DateTime.UtcNow;

    private ulong BackPanel => _panels[1 - _front];
    private ulong _handle;
    private ulong _cursor;

    /// <summary>
    /// Tiny invisible overlay whose texture is re-submitted after each panel update. Observed in the headset:
    /// SteamVR only redraws a GPU-textured overlay once *another* overlay of the process changes (the panel updated
    /// as soon as the laser left it or the wrist/handle highlight changed). This does that change, invisibly.
    /// </summary>
    private ulong _kick;
    private int _kickFrames;
    private static readonly byte[] KickPixels = new byte[4 * 4 * 4];
    private ulong _wrist;

    // Panel pose
    private Matrix4x4 _panelOffset = Matrix4x4.Identity;
    private PanelAnchor _appliedAnchor;
    private bool _hasPanelPose;

    // Laser pointer on the panel
    private int _pointerHand; // 0 none, 1 left, 2 right
    private bool _pointerDown;
    private bool _hovering;
    private readonly Vector2?[] _frozenUv = new Vector2?[3];

    // Handle drag / resize
    private bool _handleHover;
    private int _dragHand;
    private Matrix4x4 _dragOffset;
    private int _resizeHand;
    private float _resizeStartDistance;
    private float _resizeStartWidth;

    // Wrist button
    private bool _wristHover;
    private uint _wristDevice = uint.MaxValue;
    private Matrix4x4? _wristOffset;
    private bool _wristPressed;
    private bool _wristDragging;
    private Vector3 _wristPressStart;
    private Matrix4x4 _wristGrabOffset;

    private int _saveVersion;
    private CompositorReadback? _readback;
    private readonly Queue<DateTime> _diagChecks = new();

    private DateTime _lastDiagnostic;

    public VrOverlayService(IOverlaySurface surface, ISettingsStore settings, ILogger<VrOverlayService> logger)
    {
        _surface = surface;
        _settings = settings;
        _logger = logger;
        _appliedAnchor = Settings.Anchor;
        if (VrMath.FromArray(Settings.Transform) is { } saved)
        {
            _panelOffset = saved;
            _hasPanelPose = true;
        }
    }

    public event EventHandler? StateChanged;

    public VrStatus Status => _status;

    public string StatusMessage => _statusMessage;

    public bool IsPanelVisible => _wantVisible;

    public VrPanelSettings Settings => _settings.Current.Vr;

    // ───────────────────────────── IVrPanelController (any thread) ─────────────────────────────

    public void ShowPanel()
    {
        _logger.LogInformation("Show panel");
        _wantVisible = true;
        _commands.Enqueue(ShowInternal);
        RaiseStateChanged();
    }

    public void HidePanel()
    {
        _logger.LogInformation("Hide panel");
        _wantVisible = false;
        _commands.Enqueue(HideInternal);
        RaiseStateChanged();
    }

    public void TogglePanel()
    {
        if (_wantVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    public void Recenter() => _commands.Enqueue(() =>
    {
        ResetPanelPose();
        ApplyPanelTransform();
        Persist();
    });

    public void ApplySettings()
    {
        Settings.Opacity = Math.Clamp(Settings.Opacity, 0.2f, 1f);
        Settings.WidthMeters = Math.Clamp(Settings.WidthMeters, MinWidth, MaxWidth);
        _commands.Enqueue(() =>
        {
            if (Settings.Anchor != _appliedAnchor)
            {
                _appliedAnchor = Settings.Anchor;
                ResetPanelPose();
            }

            ApplyOverlaySettings();
            ApplyPanelTransform();
            _wristDevice = uint.MaxValue; // re-attach the wrist button (hand may have changed)
            _wristOffset = null;
            Persist();
        });
        RaiseStateChanged();
    }

    // ───────────────────────────── Main loop ─────────────────────────────

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken stoppingToken)
    {
        OpenVrRuntime.InstallResolver();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_connected)
            {
                if (!(_surface.IsReady && OpenVrRuntime.IsSteamVrRunning() && TryConnect()))
                {
                    stoppingToken.WaitHandle.WaitOne(WaitForSteamVrInterval);
                }

                continue;
            }

            try
            {
                Frame();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "VR frame failed; reconnecting");
                Disconnect();
                SetStatus(VrStatus.Error, "VR error, retrying…");
                stoppingToken.WaitHandle.WaitOne(WaitForSteamVrInterval);
                continue;
            }

            if (!_connected)
            {
                continue;
            }

            if (_visible)
            {
                OpenVR.Overlay.WaitFrameSync(100); // paces the loop to the headset refresh rate (90/120 Hz)
            }
            else
            {
                stoppingToken.WaitHandle.WaitOne(50);
            }
        }

        Disconnect();
    }

    private bool TryConnect()
    {
        var error = EVRInitError.None;
        try
        {
            OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
        }
        catch (DllNotFoundException)
        {
            SetStatus(VrStatus.Error, "SteamVR runtime not found (openvr_api.dll)");
            return false;
        }

        if (error != EVRInitError.None)
        {
            _logger.LogWarning("OpenVR init failed: {Error}", error);
            SetStatus(VrStatus.WaitingForSteamVr, $"Waiting for SteamVR… ({error})");
            return false;
        }

        try
        {
            var adapter = -1;
            OpenVR.System.GetDXGIOutputInfo(ref adapter);
            _d3d = D3D11Device.Create(adapter);
            // GPU upload, a fresh shared texture per frame, into the two stacked always-visible panels.
            // Raw upload (SetOverlayRaw) is not sustainable: SteamVR refuses every raw upload of the process
            // (RequestFailed) after ~190 full-panel uploads, until the OpenVR connection is re-created.
            _panelTexture = new OverlayTexture(_d3d, OverlayUploadMode.FreshTexture);
            _handleTexture = new OverlayTexture(_d3d);
            _cursorTexture = new OverlayTexture(_d3d);
            _kickTexture = new OverlayTexture(_d3d);
            _wristTexture = new OverlayTexture(_d3d);

            _panels[0] = CreateOverlay(PanelKey, "AvatarDeck");
            _panels[1] = CreateOverlay(PanelKey + ".b", "AvatarDeck (buffer)");
            _front = 0;
            _swapCountdown = 0;
            _handle = CreateOverlay(HandleKey, "AvatarDeck move handle");
            _cursor = CreateOverlay(CursorKey, "AvatarDeck cursor");
            _kick = CreateOverlay("avatardeck.kick", "AvatarDeck refresh");
            OpenVR.Overlay.SetOverlayWidthInMeters(_kick, 0.001f);
            OpenVR.Overlay.SetOverlayAlpha(_kick, 0.01f);
            OpenVR.Overlay.SetOverlaySortOrder(_kick, 0);
            _wrist = CreateOverlay(WristKey, "AvatarDeck launcher");

            var overlay = OpenVR.Overlay;
            overlay.SetOverlaySortOrder(_panels[0], 1);
            overlay.SetOverlaySortOrder(_panels[1], 1);
            overlay.SetOverlaySortOrder(_handle, 2);
            overlay.SetOverlaySortOrder(_wrist, 5);
            overlay.SetOverlaySortOrder(_cursor, 10);
            overlay.SetOverlayWidthInMeters(_cursor, CursorWidth);
            overlay.SetOverlayWidthInMeters(_wrist, WristWidth);
            overlay.SetOverlayWidthInMeters(_handle, HandleWidth);
            _cursorTexture.Submit(_cursor, ProceduralTextures.Cursor(64), 64, 64, 64 * 4);
            SubmitHandleTexture(highlighted: false);
            SubmitWristTexture(highlighted: false);

            _input = new VrInput(_logger);
            _input.Initialize();
            _readback = DiagnosticsDirectory is null ? null : new CompositorReadback(_d3d, _logger) { OutputDirectory = DiagnosticsDirectory };

            _connected = true;
            _wristDevice = uint.MaxValue;
            ApplyOverlaySettings();
            SetStatus(VrStatus.Connected, _input.IsReady ? "Connected to SteamVR" : "Connected (controller input unavailable)");
            _logger.LogInformation("Connected to SteamVR");

            if (_wantVisible)
            {
                ShowInternal();
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not create the VR overlays");
            Disconnect();
            SetStatus(VrStatus.Error, "Could not create the VR overlay (see logs)");
            return false;
        }
    }

    private static ulong CreateOverlay(string key, string name)
    {
        ulong handle = 0;
        var error = OpenVR.Overlay.CreateOverlay(key, name, ref handle);
        if (error == EVROverlayError.KeyInUse)
        {
            // Left over by a previous instance that did not shut down cleanly.
            error = OpenVR.Overlay.FindOverlay(key, ref handle);
        }

        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"CreateOverlay({key}) failed: {error}");
        }

        return handle;
    }

    private void Disconnect()
    {
        if (!_connected && _d3d is null)
        {
            return;
        }

        if (_pointerDown)
        {
            _surface.PointerUp();
        }

        _surface.PointerLeave();
        _surface.SetActive(false);
        _pointerDown = _hovering = _visible = false;
        _dragHand = _resizeHand = 0;
        _wristPressed = _wristDragging = false;

        _panelTexture?.Dispose();
        _kickTexture?.Dispose();
        _kickTexture = null;
        _handleTexture?.Dispose();
        _cursorTexture?.Dispose();
        _wristTexture?.Dispose();
        _d3d?.Dispose();
        _panelTexture = _handleTexture = _cursorTexture = _wristTexture = null;
        _d3d = null;
        _input = null;
        _readback = null;

        if (_connected)
        {
            OpenVR.Shutdown();
            _connected = false;
            _logger.LogInformation("Disconnected from SteamVR");
        }

        SetStatus(VrStatus.WaitingForSteamVr, "Waiting for SteamVR…");
    }

    private void Frame()
    {
        var vrEvent = default(VREvent_t);
        while (OpenVR.System.PollNextEvent(ref vrEvent, EventSize))
        {
            if (vrEvent.eventType == (uint)EVREventType.VREvent_Quit)
            {
                OpenVR.System.AcknowledgeQuit_Exiting();
                Disconnect();
                return;
            }
        }

        RunCommands();

        var input = _input!;
        input.Update();
        LogDiagnostics(input);

        if (input.Left.TogglePressed || input.Right.TogglePressed)
        {
            _logger.LogInformation("Toggle binding pressed");
            TogglePanel();
            RunCommands();
        }

        var cursorUsed = UpdateWristButton(input);
        if (_visible)
        {
            if (Settings.Anchor != PanelAnchor.World)
            {
                ApplyPanelTransform(); // follow controller re-connections
            }

            UpdateHandle(input);
            cursorUsed |= UpdatePointer(input);

            // Show the back buffer once SteamVR has had a couple of frames to receive it, then hide the old one.
            if (_swapCountdown > 0 && --_swapCountdown == 0)
            {
                // Bring the freshly uploaded panel to the front, then fade the old one to the back.
                // (Raise first: at worst one frame shows both, never neither.)
                OpenVR.Overlay.SetOverlayAlpha(BackPanel, Settings.Opacity);
                OpenVR.Overlay.SetOverlaySortOrder(BackPanel, 2);
                OpenVR.Overlay.SetOverlaySortOrder(FrontPanel, 1);
                OpenVR.Overlay.SetOverlayAlpha(FrontPanel, BackPanelAlpha);
                _front = 1 - _front;
                _kickFrames = 3;
            }

            // Nudge SteamVR into redrawing the panel (see _kick) for a few frames after each swap.
            if (_kickFrames > 0)
            {
                _kickFrames--;
                KickCompositor();
            }

            if (_swapCountdown == 0
                && System.Diagnostics.Stopwatch.GetElapsedTime(_lastPanelUpload) >= PanelUploadInterval
                && (_surface.TakeFrame() ?? _retryFrame) is { } frame)
            {
                UploadPanelFrame(frame);
            }

            if (_readback is not null && _diagChecks.TryPeek(out var due) && DateTime.UtcNow >= due)
            {
                _diagChecks.Dequeue();
                _readback.Check(FrontPanel, $"{_diagChecks.Count switch { 2 => "+100 ms", 1 => "+600 ms", _ => "+2 s" }} after last submit");
            }
        }

        if (!cursorUsed)
        {
            OpenVR.Overlay.HideOverlay(_cursor);
        }
    }

    private void RunCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            command();
        }
    }

    // ───────────────────────────── Panel placement ─────────────────────────────

    private float PanelWidth => Settings.WidthMeters;

    private float PanelHeight
    {
        get
        {
            var (w, h) = _surface.PixelSize;
            return w == 0 ? PanelWidth * 0.65f : PanelWidth * h / w;
        }
    }

    private ref HandState AnchorHand => ref Settings.Anchor == PanelAnchor.LeftHand ? ref _input!.Left : ref _input!.Right;

    /// <summary>Panel pose in the standing play space, or null if its anchor hand is not tracked.</summary>
    private Matrix4x4? PanelWorld()
    {
        if (Settings.Anchor == PanelAnchor.World)
        {
            return _panelOffset;
        }

        ref var hand = ref AnchorHand;
        return hand.HasPose ? _panelOffset * hand.Device : null;
    }

    /// <summary>Handle pose: centred under the panel, same orientation.</summary>
    private Matrix4x4 HandleWorld(in Matrix4x4 panel) =>
        Matrix4x4.CreateTranslation(0, -((PanelHeight / 2) + HandleGap), 0.002f) * panel;

    private float HandleHeight => HandleWidth * HandleTexHeight / HandleTexWidth;

    private void ResetPanelPose()
    {
        if (Settings.Anchor == PanelAnchor.World)
        {
            if (_input is { HasHead: true })
            {
                _panelOffset = VrMath.PlaceInFront(_input.Head);
                _hasPanelPose = true;
            }
        }
        else
        {
            _panelOffset = VrMath.DefaultHandOffset(Settings.Anchor == PanelAnchor.LeftHand);
            _hasPanelPose = true;
        }
    }

    private void ApplyPanelTransform()
    {
        if (!_connected)
        {
            return;
        }

        var transform = VrMath.ToHmd(_panelOffset);
        if (Settings.Anchor == PanelAnchor.World)
        {
            foreach (var panel in _panels)
            {
                OpenVR.Overlay.SetOverlayTransformAbsolute(panel, ETrackingUniverseOrigin.TrackingUniverseStanding, ref transform);
            }
        }
        else if (_input is not null)
        {
            var device = AnchorHand.DeviceIndex;
            if (device != OpenVR.k_unTrackedDeviceIndexInvalid)
            {
                foreach (var panel in _panels)
                {
                    OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(panel, device, ref transform);
                }
            }
        }
    }

    private void ApplyOverlaySettings()
    {
        if (!_connected)
        {
            return;
        }

        foreach (var panel in _panels)
        {
            OpenVR.Overlay.SetOverlayAlpha(panel, panel == FrontPanel ? Settings.Opacity : BackPanelAlpha);
            OpenVR.Overlay.SetOverlaySortOrder(panel, panel == FrontPanel ? 2u : 1u);
            OpenVR.Overlay.SetOverlayWidthInMeters(panel, Settings.WidthMeters);
        }

        if (!Settings.WristButton)
        {
            OpenVR.Overlay.HideOverlay(_wrist);
        }

        if (_visible && Settings.Locked)
        {
            OpenVR.Overlay.HideOverlay(_handle);
        }
        else if (_visible)
        {
            OpenVR.Overlay.ShowOverlay(_handle);
        }
    }

    private void ShowInternal()
    {
        if (!_connected || _visible)
        {
            return;
        }

        // Summon: a world panel opens in front of the user, unless it was locked in place (pinned).
        _input?.Update();
        if (!_hasPanelPose || (Settings.Anchor == PanelAnchor.World && (!Settings.Locked || !IsInView(_panelOffset))))
        {
            ResetPanelPose();
            _logger.LogInformation("Panel placed in front of the user");
        }

        _visible = true;
        ApplyOverlaySettings();
        ApplyPanelTransform();
        _surface.SetActive(true);
        if (_surface.TakeFrame() is { } frame)
        {
            foreach (var panel in _panels)
            {
                _panelTexture!.Submit(panel, frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.IsRgba);
            }
        }

        _swapCountdown = 0;
        foreach (var panel in _panels)
        {
            OpenVR.Overlay.ShowOverlay(panel);
        }
    }

    private void HideInternal()
    {
        if (!_connected || !_visible)
        {
            return;
        }

        if (_pointerDown)
        {
            _surface.PointerUp();
            _pointerDown = false;
        }

        _surface.PointerLeave();
        _surface.SetActive(false);
        _swapCountdown = 0;
        foreach (var panel in _panels)
        {
            OpenVR.Overlay.HideOverlay(panel);
        }

        OpenVR.Overlay.HideOverlay(_handle);
        _hovering = _visible = false;
        _dragHand = _resizeHand = 0;
    }

    /// <summary>World panel reasonably in front of the head (within 1.5 m and ~60° of the view direction).</summary>
    private bool IsInView(in Matrix4x4 panel)
    {
        if (_input is not { HasHead: true } input)
        {
            return true;
        }

        var toPanel = panel.Translation - input.Head.Translation;
        var distance = toPanel.Length();
        return distance is > 0.15f and < 1.5f && Vector3.Dot(toPanel / distance, VrMath.Forward(input.Head)) > 0.5f;
    }

    // ───────────────────────────── Handle: move & resize ─────────────────────────────

    private void UpdateHandle(VrInput input)
    {
        if (PanelWorld() is not { } panel)
        {
            return;
        }

        var handle = HandleWorld(panel);
        if (!Settings.Locked)
        {
            var transform = VrMath.ToHmd(handle);
            OpenVR.Overlay.SetOverlayTransformAbsolute(_handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref transform);
        }

        // Start dragging: trigger pressed while the laser is on the handle.
        var hovered = false;
        foreach (var index in new[] { 1, 2 })
        {
            ref var hand = ref Hand(input, index);
            if (Settings.Locked || !hand.HasPose || _dragHand == index || _resizeHand == index)
            {
                continue;
            }

            var onHandle = VrMath.Intersect(handle, HandleWidth, HandleHeight * 2.5f, hand.Aim.Translation, VrMath.Forward(hand.Aim)) is not null;
            hovered |= onHandle;
            if (!hand.ClickPressed)
            {
                continue;
            }

            if (_dragHand == 0 && onHandle)
            {
                ReleasePointer();
                _dragHand = index;
                _dragOffset = VrMath.RelativeTo(panel, hand.Device);
                _logger.LogInformation("Panel drag started ({Hand} hand)", HandName(index));
            }
            else if (_dragHand != 0 && _resizeHand == 0)
            {
                // Second hand pulls the trigger anywhere while dragging: resize by hand distance.
                _resizeHand = index;
                _resizeStartDistance = Math.Max(0.05f, VrMath.Distance(input.Left.Device, input.Right.Device));
                _resizeStartWidth = Settings.WidthMeters;
                _logger.LogInformation("Panel resize started");
            }
        }

        var highlight = hovered || _dragHand != 0;
        if (highlight != _handleHover)
        {
            _handleHover = highlight;
            SubmitHandleTexture(highlight);
        }

        if (_dragHand == 0)
        {
            return;
        }

        ref var dragging = ref Hand(input, _dragHand);
        if (_resizeHand != 0 && !Hand(input, _resizeHand).Click)
        {
            _resizeHand = 0;
            Persist();
        }

        if (!dragging.Click || !dragging.HasPose)
        {
            _dragHand = _resizeHand = 0;
            Persist();
            return;
        }

        var world = _dragOffset * dragging.Device;
        if (Settings.Anchor == PanelAnchor.World)
        {
            // Position follows the hand; orientation always faces the head (upright, no roll), like XSOverlay.
            _panelOffset = input.HasHead ? VrMath.FaceTowards(world.Translation, input.Head.Translation) : world;
        }
        else
        {
            ref var anchor = ref AnchorHand;
            if (anchor.HasPose)
            {
                _panelOffset = VrMath.RelativeTo(world, anchor.Device);
            }
        }

        _hasPanelPose = true;
        ApplyPanelTransform();

        if (_resizeHand != 0 && Hand(input, _resizeHand).HasPose)
        {
            var factor = VrMath.Distance(input.Left.Device, input.Right.Device) / _resizeStartDistance;
            Settings.WidthMeters = Math.Clamp(_resizeStartWidth * factor, MinWidth, MaxWidth);
            foreach (var overlay in _panels)
            {
                OpenVR.Overlay.SetOverlayWidthInMeters(overlay, Settings.WidthMeters);
            }
        }
    }

    // ───────────────────────────── Laser pointer on the panel ─────────────────────────────

    /// <returns>True when the cursor is shown on the panel.</returns>
    private bool UpdatePointer(VrInput input)
    {
        if (PanelWorld() is not { } panel)
        {
            LeavePanel();
            return false;
        }

        // Keep the hand that is clicking; otherwise take the first free hand whose laser hits the panel.
        (Vector2 Uv, float Distance, Vector3 LocalHit)? hit = null;
        var handIndex = 0;
        if (_pointerHand != 0 && IsFreeHand(_pointerHand))
        {
            hit = HitTest(Hand(input, _pointerHand), panel);
            handIndex = hit is null ? 0 : _pointerHand;
        }

        if (hit is null)
        {
            foreach (var candidate in new[] { 2, 1 })
            {
                if (IsFreeHand(candidate) && HitTest(Hand(input, candidate), panel) is { } h)
                {
                    hit = h;
                    handIndex = candidate;
                    break;
                }
            }
        }

        if (hit is not { } result)
        {
            if (_pointerDown && !Hand(input, _pointerHand).Click)
            {
                _surface.PointerUp();
                _pointerDown = false;
            }

            LeavePanel();
            return false;
        }

        if (_pointerHand != handIndex)
        {
            ReleasePointer();
            _pointerHand = handIndex;
        }

        ref var hand = ref Hand(input, handIndex);
        _hovering = true;

        // Click stabilisation: freeze the aim as soon as the trigger starts moving; the click lands there.
        if (!_pointerDown)
        {
            if (hand.Trigger >= StabilizeThreshold)
            {
                _frozenUv[handIndex] ??= result.Uv;
            }
            else
            {
                _frozenUv[handIndex] = null;
            }
        }

        if (hand.ClickPressed && !_pointerDown)
        {
            var uv = _frozenUv[handIndex] ?? result.Uv;
            _surface.PointerMove(uv.X, uv.Y);
            _surface.PointerDown();
            _pointerDown = true;
            _pressUv = uv;
            _pressDragging = false;
            _frozenUv[handIndex] = null;
        }
        else if (!hand.Click && _pointerDown)
        {
            if (_pressDragging)
            {
                _surface.PointerMove(result.Uv.X, result.Uv.Y);
            }

            _surface.PointerUp(); // released where it was pressed unless it became a drag: the click lands
            _pointerDown = false;
        }
        else if (_pointerDown)
        {
            // Dead zone: the hand always moves a little while the trigger is held. Keep the pointer still until the
            // laser clearly moves away, so a click is never turned into a drag/scroll by accident.
            if (!_pressDragging && Vector2.Distance(PanelPixels(result.Uv), PanelPixels(_pressUv)) > ClickDeadZonePixels)
            {
                _pressDragging = true;
            }

            if (_pressDragging)
            {
                _surface.PointerMove(result.Uv.X, result.Uv.Y);
            }
        }
        else if (_frozenUv[handIndex] is null)
        {
            _surface.PointerMove(result.Uv.X, result.Uv.Y);
        }

        // Cursor dot slightly in front of the panel surface.
        var cursor = VrMath.ToHmd(Matrix4x4.CreateTranslation(result.LocalHit.X, result.LocalHit.Y, 0.004f) * panel);
        OpenVR.Overlay.SetOverlayTransformAbsolute(_cursor, ETrackingUniverseOrigin.TrackingUniverseStanding, ref cursor);
        OpenVR.Overlay.ShowOverlay(_cursor);
        return true;
    }

    /// <summary>Laser movement (panel UI pixels) tolerated during a click before it becomes a drag.</summary>
    private const float ClickDeadZonePixels = 28f;

    private Vector2 _pressUv;
    private bool _pressDragging;

    /// <summary>UV → panel UI pixels (1100 × 720 logical layout, aspect from the rendered frame).</summary>
    private Vector2 PanelPixels(Vector2 uv) => new(uv.X * 1100f, uv.Y * 1100f * PanelHeight / PanelWidth);

    private bool IsFreeHand(int index) => index != _dragHand && index != _resizeHand && !(_wristPressed && index == WristPointerHand);

    private void ReleasePointer()
    {
        if (_pointerDown)
        {
            _surface.PointerUp();
            _pointerDown = false;
        }
    }

    private void LeavePanel()
    {
        if (!_hovering)
        {
            return;
        }

        if (!_pointerDown)
        {
            _surface.PointerLeave();
            _pointerHand = 0;
        }

        _hovering = false;
    }

    private (Vector2 Uv, float Distance, Vector3 LocalHit)? HitTest(in HandState hand, in Matrix4x4 panel) =>
        hand.HasPose ? VrMath.Intersect(panel, PanelWidth, PanelHeight, hand.Aim.Translation, VrMath.Forward(hand.Aim)) : null;

    private static ref HandState Hand(VrInput input, int index) => ref index == 1 ? ref input.Left : ref input.Right;

    private static string HandName(int index) => index == 1 ? "left" : "right";

    // ───────────────────────────── Wrist launcher ─────────────────────────────

    private int WristPointerHand => Settings.WristHand == PanelAnchor.RightHand ? 1 : 2;

    /// <returns>True when the cursor is shown on the wrist button.</returns>
    private bool UpdateWristButton(VrInput input)
    {
        if (!Settings.WristButton)
        {
            return false;
        }

        var left = Settings.WristHand != PanelAnchor.RightHand;
        ref var carrier = ref left ? ref input.Left : ref input.Right;
        if (!carrier.HasPose)
        {
            return false;
        }

        var offset = _wristOffset ??= VrMath.FromArray(Settings.WristTransform) ?? VrMath.DefaultWristButtonOffset(left);
        if (_wristDevice != carrier.DeviceIndex)
        {
            ApplyWristTransform(carrier.DeviceIndex, offset);
            OpenVR.Overlay.ShowOverlay(_wrist);
        }

        // Pointed at (laser) or touched with the other hand.
        ref var pointer = ref Hand(input, WristPointerHand);
        var world = offset * carrier.Device;
        var hit = pointer.HasPose && WristPointerHand != _dragHand
            ? VrMath.Intersect(world, WristWidth, WristWidth, pointer.Aim.Translation, VrMath.Forward(pointer.Aim))
            : null;
        var touching = pointer.HasPose && Vector3.Distance(pointer.Device.Translation, world.Translation) < 0.06f;

        // Trigger on the button: a tap toggles the panel, a press-and-move repositions the button on the hand.
        if (!_wristPressed && pointer.ClickPressed && (hit is not null || touching) && !(_hovering && _pointerHand == WristPointerHand))
        {
            _wristPressed = true;
            _wristDragging = false;
            _wristPressStart = pointer.Device.Translation;
            _wristGrabOffset = VrMath.RelativeTo(world, pointer.Device);
        }

        if (_wristPressed)
        {
            if (!_wristDragging && pointer.HasPose && Vector3.Distance(pointer.Device.Translation, _wristPressStart) > WristDragDistance)
            {
                _wristDragging = true;
                _logger.LogInformation("Wrist button drag started");
            }

            if (_wristDragging && pointer.HasPose)
            {
                offset = VrMath.RelativeTo(_wristGrabOffset * pointer.Device, carrier.Device);
                _wristOffset = offset;
                ApplyWristTransform(carrier.DeviceIndex, offset);
            }

            if (!pointer.Click)
            {
                _wristPressed = false;
                if (_wristDragging)
                {
                    Settings.WristTransform = VrMath.ToArray(offset);
                    Persist();
                }
                else
                {
                    _logger.LogInformation("Wrist button clicked");
                    TogglePanel();
                    RunCommands();
                }
            }
        }

        var hover = hit is not null || touching || _wristPressed;
        if (hover != _wristHover)
        {
            _wristHover = hover;
            SubmitWristTexture(hover);
        }

        // Cursor dot on the button.
        if (hit is { } h && !_wristDragging)
        {
            var cursor = VrMath.ToHmd(Matrix4x4.CreateTranslation(h.LocalHit.X, h.LocalHit.Y, 0.003f) * world);
            OpenVR.Overlay.SetOverlayTransformAbsolute(_cursor, ETrackingUniverseOrigin.TrackingUniverseStanding, ref cursor);
            OpenVR.Overlay.ShowOverlay(_cursor);
            return true;
        }

        return false;
    }

    private void ApplyWristTransform(uint device, in Matrix4x4 offset)
    {
        var transform = VrMath.ToHmd(offset);
        OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_wrist, device, ref transform);
        _wristDevice = device;
    }

    private void SubmitWristTexture(bool highlighted) =>
        _wristTexture?.Submit(_wrist, ProceduralTextures.WristButton(128, highlighted), 128, 128, 128 * 4);

    private void SubmitHandleTexture(bool highlighted) =>
        _handleTexture?.Submit(_handle, ProceduralTextures.Handle(HandleTexWidth, HandleTexHeight, highlighted), HandleTexWidth, HandleTexHeight, HandleTexWidth * 4);

    // ───────────────────────────── Diagnostics ─────────────────────────────

    /// <summary>Trigger presses at Information level, a one-line summary per second at Debug level.</summary>
    private void LogDiagnostics(VrInput input)
    {
        foreach (var index in new[] { 1, 2 })
        {
            ref var hand = ref Hand(input, index);
            if (hand.ClickPressed)
            {
                var hit = _visible && PanelWorld() is { } p ? HitTest(hand, p) : null;
                _logger.LogInformation("{Hand} trigger pressed: hitsPanel={Hit}", HandName(index), hit is { } h ? h.Uv.ToString("F2", null) : "no");
            }
        }

        if (DateTime.UtcNow - _lastDiagnostic < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastDiagnostic = DateTime.UtcNow;
        static string Describe(in HandState hand) => hand.HasPose
            ? $"pose tip={hand.HasTipPose} trigger={hand.Trigger:F2}"
            : "NO POSE";
        _logger.LogDebug("VR input ready={Ready} | L: {Left} | R: {Right}", input.IsReady, Describe(input.Left), Describe(input.Right));
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private void Persist()
    {
        Settings.Transform = _hasPanelPose ? VrMath.ToArray(_panelOffset) : null;
        _ = SaveDebouncedAsync();
        RaiseStateChanged();
    }

    /// <summary>Drags and slider moves produce many changes: write the file once they settle.</summary>
    private async Task SaveDebouncedAsync()
    {
        var version = Interlocked.Increment(ref _saveVersion);
        try
        {
            await Task.Delay(SaveDelay).ConfigureAwait(false);
            if (version == Volatile.Read(ref _saveVersion))
            {
                await _settings.SaveAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save VR settings");
        }
    }

    private void SetStatus(VrStatus status, string message)
    {
        if (_status == status && _statusMessage == message)
        {
            return;
        }

        _status = status;
        _statusMessage = message;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
