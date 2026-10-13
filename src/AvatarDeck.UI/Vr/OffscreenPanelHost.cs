using System.ComponentModel;
using System.Runtime.InteropServices;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.UI.Vr;

/// <summary>
/// Hosts a second <see cref="MainView"/> (same view model as the desktop window) in a hidden, off-screen window,
/// renders it to pixels for the VR overlay and receives the VR laser as regular Win32 mouse messages,
/// so hover, click, drag and wheel behave exactly like with a mouse.
/// </summary>
/// <remarks>
/// Rendering is on demand: ~60 fps for a short time after any input or state change, a cheap safety check
/// twice per second otherwise, and nothing at all while the panel is hidden. Unchanged frames are not re-uploaded.
/// Pixel buffers are recycled (render → last → published → consumer), so steady-state rendering allocates nothing.
/// </remarks>
public sealed class OffscreenPanelHost : IOverlaySurface, IDisposable
{
    /// <summary>Logical (DIP) size of the VR panel.</summary>
    public const double LogicalWidth = 1100;

    public const double LogicalHeight = 720;

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseLeave = 0x02A3;
    private const int MkLButton = 0x0001;
    private static readonly TimeSpan AwakeDuration = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan IdleRenderInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ActiveTick = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(100);

    private readonly ISettingsStore _settings;
    private readonly IVrcStateFeed _feed;
    private readonly INotificationService _notifications;
    private readonly ILogger<OffscreenPanelHost> _logger;
    private readonly object _gate = new();

    private Window? _window;
    private Control? _view;

    /// <summary>The off-screen view (diagnostics and self-test).</summary>
    public Control? View => _view;
    private RenderTargetBitmap? _bitmap;

    private DispatcherTimer? _timer;
    private INotifyPropertyChanged? _viewModel;
    private object? _dataContext;
    private byte[] _render = [];
    private byte[] _last = [];
    private byte[] _published = [];
    private byte[] _consumer = [];
    private bool _hasNewFrame;
    private volatile bool _isReady;
    private volatile bool _active;
    private DateTime _awakeUntil;
    private DateTime _lastRender;
    private bool _buttonDown;
    private (int X, int Y) _lastPoint;

    public OffscreenPanelHost(ISettingsStore settings, IVrcStateFeed feed, INotificationService notifications, ILogger<OffscreenPanelHost> logger)
    {
        _settings = settings;
        _feed = feed;
        _notifications = notifications;
        _logger = logger;
    }

    public bool IsReady => _isReady;

    public (int Width, int Height) PixelSize { get; private set; }

    /// <summary>
    /// Prepares the surface. Must be called on the UI thread once the application is running. The hidden window and
    /// pixel buffers (~35 MB) are only created the first time the panel is shown in VR, so desktop-only use costs nothing.
    /// </summary>
    public void Initialize(object dataContext)
    {
        var scale = Math.Clamp(_settings.Current.Vr.RenderScale, 1f, 2f);
        PixelSize = ((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
        _dataContext = dataContext;
        _isReady = true;
    }

    private void EnsureCreated()
    {
        if (_window is not null || _dataContext is null)
        {
            return;
        }

        var dataContext = _dataContext;
        var scale = Math.Clamp(_settings.Current.Vr.RenderScale, 1f, 2f);
        var (width, height) = PixelSize;

        _view = new MainView { DataContext = dataContext };
        _window = new Window
        {
            Title = "AvatarDeck VR surface",
            Width = LogicalWidth,
            Height = LogicalHeight,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000), // off every monitor; still laid out and alive
            Background = Brushes.Transparent,
            Content = _view,
        };
        _window.Show();
        InstallLeaveFilter();

        _bitmap = new RenderTargetBitmap(new Avalonia.PixelSize(width, height), new Vector(96 * scale, 96 * scale));

        var length = width * height * 4;
        _render = new byte[length];
        _last = new byte[length];
        _published = new byte[length];
        _consumer = new byte[length];

        _timer = new DispatcherTimer(ActiveTick, DispatcherPriority.Render, (_, _) => OnTick());

        _viewModel = dataContext as INotifyPropertyChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnStateChanged;
        }

        // Not woken by VRChat parameter traffic (dozens of updates per second, mostly invisible here): remote changes
        // show up through the 500 ms safety render, while user input and notifications render immediately.
        _notifications.Raised += OnStateChanged;
        _logger.LogInformation("VR surface ready ({Width}×{Height} px)", width, height);
    }

    // ───────────────────────────── IOverlaySurface ─────────────────────────────

    public void SetActive(bool active) => Dispatcher.UIThread.Post(() =>
    {
        _active = active;
        if (active)
        {
            EnsureCreated();
            Wake();
            RenderFrame(); // first frame right away
        }
        else
        {
            _timer?.Stop();
        }
    });

    public OverlayFrame? TakeFrame()
    {
        lock (_gate)
        {
            if (!_hasNewFrame)
            {
                return null;
            }

            (_published, _consumer) = (_consumer, _published);
            _hasNewFrame = false;
            return new OverlayFrame(_consumer, PixelSize.Width, PixelSize.Height);
        }
    }

    /// <summary>
    /// The laser moves at the headset rate (90–120 Hz). Moves are coalesced: only the latest position is delivered,
    /// at Input priority (below rendering), so a fast drag never starves the renderer.
    /// </summary>
    public void PointerMove(double x, double y)
    {
        bool schedule;
        lock (_moveGate)
        {
            schedule = _pendingMove is null;
            _pendingMove = (x, y);
        }

        if (schedule)
        {
            Dispatcher.UIThread.Post(ApplyPendingMove, DispatcherPriority.Input);
        }
    }

    private readonly object _moveGate = new();
    private (double X, double Y)? _pendingMove;

    /// <summary>Delivers the latest coalesced move (also called before button events so they land at the right place).</summary>
    private void ApplyPendingMove()
    {
        (double X, double Y)? move;
        lock (_moveGate)
        {
            move = _pendingMove;
            _pendingMove = null;
        }

        if (move is not { } m || _window is null)
        {
            return;
        }

        var scaling = _window.RenderScaling;
        _lastPoint = ((int)(m.X * LogicalWidth * scaling), (int)(m.Y * LogicalHeight * scaling));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Send(WmMouseMove, _buttonDown ? MkLButton : 0, MakeLParam(_lastPoint.X, _lastPoint.Y));
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _moveCount++;
        _moveTotalMs += ms;
        _moveMaxMs = Math.Max(_moveMaxMs, ms);
        Wake();
    }

    private int _moveCount;
    private double _moveTotalMs;
    private double _moveMaxMs;

    public void PointerDown() => Dispatcher.UIThread.Post(() =>
    {
        ApplyPendingMove();
        if (_view is not null && _window is not null)
        {
            var logical = new Point(_lastPoint.X / _window.RenderScaling, _lastPoint.Y / _window.RenderScaling);
            var target = _view.InputHitTest(logical) as Control;
            _logger.LogInformation(
                "VR click at {X:0},{Y:0} (view {W:0}x{H:0}, scaling {S}) on {Target} [{Context}]",
                logical.X, logical.Y, _view.Bounds.Width, _view.Bounds.Height, _window.RenderScaling,
                target?.GetType().Name ?? "nothing", (target?.DataContext as ViewModels.Widgets.WidgetViewModel)?.Label ?? target?.DataContext?.GetType().Name);
        }

        _buttonDown = true;
        Send(WmLButtonDown, MkLButton, MakeLParam(_lastPoint.X, _lastPoint.Y));
        Wake();
    });

    public void PointerUp() => Dispatcher.UIThread.Post(() =>
    {
        ApplyPendingMove();
        _logger.LogInformation("VR release at {X},{Y}", _lastPoint.X, _lastPoint.Y);
        _buttonDown = false;
        Send(WmLButtonUp, 0, MakeLParam(_lastPoint.X, _lastPoint.Y));
        Wake();
    });

    public void PointerLeave() => Dispatcher.UIThread.Post(() =>
    {
        ApplyPendingMove();
        Send(WmMouseLeave, OurLeaveMarker, 0);
        Wake();
    });

    public void Scroll(double notches) => Dispatcher.UIThread.Post(() =>
    {
        ApplyPendingMove();
        if (_window is null)
        {
            return;
        }

        // WM_MOUSEWHEEL carries screen coordinates.
        var screen = _window.PointToScreen(new Point(_lastPoint.X / _window.RenderScaling, _lastPoint.Y / _window.RenderScaling));
        Send(WmMouseWheel, (int)(notches * 120) << 16, MakeLParam(screen.X, screen.Y));
        Wake();
    });

    // ───────────────────────────── Rendering ─────────────────────────────

    private void OnStateChanged(object? sender, object e) => Dispatcher.UIThread.Post(Wake);

    /// <summary>Renders at full rate for a short while (covers transitions and animations).</summary>
    private void Wake()
    {
        if (!_active || _timer is null)
        {
            return;
        }

        _awakeUntil = DateTime.UtcNow + AwakeDuration;
        SetTimerInterval(ActiveTick);
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>
    /// Changes the render timer period only when it differs: assigning <see cref="DispatcherTimer.Interval"/> restarts
    /// the timer, so doing it on every laser move (90 Hz) kept postponing the next render during drags.
    /// </summary>
    private void SetTimerInterval(TimeSpan interval)
    {
        if (_timer is not null && _timer.Interval != interval)
        {
            _timer.Interval = interval;
        }
    }

    private void OnTick()
    {
        if (!_active)
        {
            _timer!.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var awake = now < _awakeUntil;
        if (awake || now - _lastRender >= IdleRenderInterval)
        {
            RenderFrame();
        }

        SetTimerInterval(awake ? ActiveTick : IdleTick);
    }

    private void RenderFrame()
    {
        if (_bitmap is null || _view is null)
        {
            return;
        }

        _lastRender = DateTime.UtcNow;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Force a synchronous layout pass. Avalonia normally lays out on its render tick, but the tick of an
        // off-screen window can be paused, leaving a new page measured-but-not-arranged until the next mouse event.
        var layoutWasStale = !_view.IsMeasureValid || !_view.IsArrangeValid
            || _view.GetVisualDescendants().OfType<Layoutable>().Any(l => !l.IsArrangeValid);
        _window!.UpdateLayout();
        if (layoutWasStale)
        {
            _staleLayouts++;
        }
        using (var context = _bitmap.CreateDrawingContext(true))
        {
            // Clear: the panel background is translucent, so the previous frame must not show through.
        }

        _bitmap.Render(_view);
        var handle = GCHandle.Alloc(_render, GCHandleType.Pinned);
        try
        {
            _bitmap.CopyPixels(new PixelRect(0, 0, PixelSize.Width, PixelSize.Height), handle.AddrOfPinnedObject(), _render.Length, PixelSize.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        var rendered = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (_render.AsSpan().SequenceEqual(_last))
        {
            RecordTiming(rendered, published: false);
            return; // nothing changed: no upload
        }

        lock (_gate)
        {
            Buffer.BlockCopy(_render, 0, _published, 0, _render.Length);
            _hasNewFrame = true;
        }

        (_render, _last) = (_last, _render);
        RecordTiming(rendered, published: true);
    }

    private int _staleLayouts;
    private int _timingFrames;
    private double _timingTotalMs;
    private double _timingMaxMs;
    private int _timingPublished;
    private DateTime _timingSince = DateTime.UtcNow;

    /// <summary>Logs render cost every 2 s (Debug) to keep latency visible.</summary>
    private void RecordTiming(TimeSpan rendered, bool published)
    {
        _timingFrames++;
        _timingTotalMs += rendered.TotalMilliseconds;
        _timingMaxMs = Math.Max(_timingMaxMs, rendered.TotalMilliseconds);
        _timingPublished += published ? 1 : 0;
        if (DateTime.UtcNow - _timingSince < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _logger.LogDebug(
            "VR render: {Frames} frames ({Published} new, {Stale} with stale layout fixed) avg {Avg:0.0} ms max {Max:0.0} ms",
            _timingFrames, _timingPublished, _staleLayouts, _timingTotalMs / _timingFrames, _timingMaxMs);
        _staleLayouts = 0;
        if (_moveCount > 0)
        {
            _logger.LogDebug("VR moves: {Count} delivered, avg {Avg:0.0} ms max {Max:0.0} ms", _moveCount, _moveTotalMs / _moveCount, _moveMaxMs);
            _moveCount = 0;
            _moveTotalMs = _moveMaxMs = 0;
        }
        _timingFrames = _timingPublished = 0;
        _timingTotalMs = _timingMaxMs = 0;
        _timingSince = DateTime.UtcNow;
    }

    // ───────────────────────────── Win32 input injection ─────────────────────────────

    /// <summary>wParam tag identifying the WM_MOUSELEAVE messages we send ourselves.</summary>
    private const nint OurLeaveMarker = 0xADEC;
    private const int GwlpWndProc = -4;

    private WndProcDelegate? _filterProc;
    private nint _originalProc;

    /// <summary>
    /// Subclasses the hidden window to drop the WM_MOUSELEAVE messages Windows generates on its own. Avalonia asks
    /// Windows to report when the mouse leaves (TrackMouseEvent); Windows checks the *real* desktop cursor, which is
    /// never over this off-screen window, and immediately reports a leave — cancelling every hover state the laser
    /// creates. Only our own leave messages (tagged with <see cref="OurLeaveMarker"/>) are let through.
    /// </summary>
    private void InstallLeaveFilter()
    {
        if (_window?.TryGetPlatformHandle() is not { } handle || _filterProc is not null)
        {
            return;
        }

        _filterProc = FilterProc; // keep the delegate alive for the lifetime of the window
        _originalProc = SetWindowLongPtrW(handle.Handle, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_filterProc));
    }

    private nint FilterProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmMouseLeave)
        {
            if (wParam != OurLeaveMarker)
            {
                return 0; // generated by Windows from the real cursor: ignore
            }

            wParam = 0;
        }

        return CallWindowProcW(_originalProc, hWnd, message, wParam, lParam);
    }

    private delegate nint WndProcDelegate(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hWnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint hWnd, uint message, nint wParam, nint lParam);

    private void Send(uint message, nint wParam, nint lParam)
    {
        if (_window?.TryGetPlatformHandle() is { } handle)
        {
            _ = SendMessageW(handle.Handle, message, wParam, lParam);
        }
    }

    private static nint MakeLParam(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    public void Dispose()
    {
        _timer?.Stop();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnStateChanged;
        }

        _notifications.Raised -= OnStateChanged;
        _bitmap?.Dispose();

        _window?.Close();
    }
}
