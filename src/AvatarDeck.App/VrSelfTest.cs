using System.Runtime.InteropServices;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.UI.ViewModels;
using AvatarDeck.UI.ViewModels.Widgets;
using AvatarDeck.UI.Vr;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.App;

/// <summary>
/// <c>--vr-selftest &lt;output-dir&gt;</c>: exercises the VR panel pipeline without a headset — off-screen rendering,
/// frame hand-off and laser input injection (click and drag-scroll) — then exits with 0 (pass) or 1 (fail).
/// Run it with an isolated <c>--data-dir</c> and a dummy OSC port so nothing reaches VRChat.
/// </summary>
internal static class VrSelfTest
{
    public static async Task<int> RunAsync(IServiceProvider services, string outputDirectory)
    {
        var logger = services.GetRequiredService<ILogger<HostMarkerForSelfTest>>();
        var surface = services.GetRequiredService<OffscreenPanelHost>();
        var vm = services.GetRequiredService<MainViewModel>();
        Directory.CreateDirectory(outputDirectory);
        var failures = new List<string>();

        void Check(bool condition, string message)
        {
            logger.LogInformation("[{Result}] {Message}", condition ? "PASS" : "FAIL", message);
            if (!condition)
            {
                failures.Add(message);
            }
        }

        await WaitUntil(() => vm.NavItems.Count > 0 && surface.IsReady, TimeSpan.FromSeconds(10));
        Check(surface.IsReady, "surface initialised");
        Check(vm.NavItems.Count > 0, "profile loaded");

        // Like in real VR use: the desktop window is minimised (or behind the game) while the panel is used.
        if (Environment.GetCommandLineArgs().Contains("--minimized"))
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                main.Hide();
                logger.LogInformation("[INFO] desktop window hidden");
            }
        }

        // 1. Activation renders a first frame.
        surface.SetActive(true);
        var frame = await WaitForFrame(surface);
        Check(frame is not null, "first frame rendered");
        if (frame is not null)
        {
            Check(frame.Width == surface.PixelSize.Width && frame.Height == surface.PixelSize.Height, $"frame size {frame.Width}×{frame.Height}");
            Check(frame.Pixels.Any(b => b != 0), "frame has content");
            Save(frame, Path.Combine(outputDirectory, "vr-01-first-frame.png"));
        }

        // 2. Idle: no new frame when nothing changes.
        await Task.Delay(1200);
        _ = surface.TakeFrame();
        await Task.Delay(700);
        Check(surface.TakeFrame() is null, "idle panel produces no new frame");

        // 3. Laser click on a toggle tile.
        vm.SelectedNav = vm.NavItems.First(n => n.Folder?.Widgets.OfType<ToggleWidgetViewModel>().Any() == true);
        await Task.Delay(300);
        var toggle = vm.Sections.SelectMany(s => s.Widgets).OfType<ToggleWidgetViewModel>().First();
        var before = toggle.IsOn;
        var target = FindCenter(surface.View!, c => c is ToggleButton && c.DataContext == toggle);
        Check(target is not null, $"found tile \"{toggle.Label}\" in the VR view");
        if (target is { } point)
        {
            // Hover only (the laser stays on the panel, no leave): the hover highlight must reach the overlay quickly.
            await Task.Delay(1500);
            _ = surface.TakeFrame();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            surface.PointerMove(point.X, point.Y);
            await Task.Delay(100);
            var tile = surface.View!.GetVisualDescendants().OfType<ToggleButton>().First(c => c.DataContext == toggle);
            var presenter = tile.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
            logger.LogInformation("[INFO] after hover: IsPointerOver={Over} presenter background={Background}", tile.IsPointerOver, presenter?.Background);
            frame = await WaitForFrame(surface);
            Check(frame is not null && watch.ElapsedMilliseconds < 300, $"hover produced a new frame in {watch.ElapsedMilliseconds} ms");
            if (frame is not null)
            {
                Save(frame, Path.Combine(outputDirectory, "vr-02-hover.png"));
            }

            // Click while still on the panel.
            await Task.Delay(1500);
            _ = surface.TakeFrame();
            watch.Restart();
            surface.PointerDown();
            surface.PointerUp();
            frame = await WaitForFrame(surface);
            Check(frame is not null && watch.ElapsedMilliseconds < 200, $"click produced a new frame in {watch.ElapsedMilliseconds} ms");
            Check(toggle.IsOn != before, $"laser click toggled \"{toggle.Label}\" ({before} → {toggle.IsOn})");
            if (frame is not null)
            {
                Save(frame, Path.Combine(outputDirectory, "vr-03-after-click.png"));
            }

            // Small moves on the panel keep producing frames while something changes (e.g. hover moving to the next tile).
            var next = FindCenter(surface.View!, c => c is ToggleButton && c.DataContext != toggle);
            if (next is { } other)
            {
                await Task.Delay(1500);
                _ = surface.TakeFrame();
                watch.Restart();
                surface.PointerMove(other.X, other.Y);
                frame = await WaitForFrame(surface);
                Check(frame is not null && watch.ElapsedMilliseconds < 200, $"moving to another tile produced a new frame in {watch.ElapsedMilliseconds} ms");
            }
        }

        // 3b. Laser click on another tab of the rail: the very next frame must show the new page (laid out).
        var tab = vm.NavItems.FirstOrDefault(n => n.Folder is not null && n != vm.SelectedNav && n.Folder.Widgets.Count > 0);
        if (tab is not null)
        {
            var tabPoint = FindCenter(surface.View!, c => c is ListBoxItem && c.DataContext == tab);
            Check(tabPoint is not null, $"found tab \"{tab.Title}\"");
            if (tabPoint is { } tp)
            {
                await Task.Delay(1500);
                _ = surface.TakeFrame();
                // Realistic trigger: ~300 ms between press and release, laser still moving slightly.
                surface.PointerMove(tp.X, tp.Y);
                await Task.Delay(50);
                surface.PointerDown();
                await Task.Delay(150);
                await Task.Delay(150);
                surface.PointerUp();
                await Task.Delay(150);
                Check(vm.SelectedNav == tab, $"tab \"{tab.Title}\" selected by laser click");
                var firstWidget = tab.Folder!.Widgets.First();
                var shown = surface.View!.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(c => c.DataContext == firstWidget && c.Bounds.Width > 0 && c.IsArrangeValid);
                Check(shown is not null, $"new page laid out right after the click (\"{firstWidget.Label}\" arranged)");
                frame = await WaitForFrame(surface);
                if (frame is not null)
                {
                    Save(frame, Path.Combine(outputDirectory, "vr-04-after-tab.png"));
                }
            }
        }

        // 4. Drag-to-scroll on a page taller than the viewport (fill one with search results if needed).
        vm.SearchText = " ";
        vm.SearchText = string.Empty;
        vm.SelectedNav = vm.NavItems.OrderByDescending(n => n.Folder?.Widgets.Count ?? 0).First();
        await Task.Delay(300);
        var scroll = surface.View!.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Extent.Height > s.Viewport.Height + 20);
        if (scroll is null)
        {
            logger.LogInformation("[SKIP] drag-scroll: no page taller than the panel in this profile");
        }
        else
        {
            var start = FindCenter(surface.View!, c => c == scroll)!.Value;
            await Task.Delay(1500);
            _ = surface.TakeFrame();
            var beforeFrame = await WaitForFrame(surface); // forces a fresh baseline after hover
            surface.PointerMove(start.X, start.Y + 0.2);
            await Task.Delay(800);
            beforeFrame = surface.TakeFrame() ?? beforeFrame;
            var beforePixels = beforeFrame?.Pixels.ToArray();
            surface.PointerDown();
            for (var i = 1; i <= 10; i++)
            {
                surface.PointerMove(start.X, start.Y + 0.2 - (i * 0.03));
                await Task.Delay(16);
            }

            surface.PointerUp();
            await Task.Delay(200);
            Check(scroll.Offset.Y > 50, $"drag scrolled the page (offset {scroll.Offset.Y:0})");
            var afterFrame = await WaitForFrame(surface);
            Check(afterFrame is not null && beforePixels is not null && !afterFrame.Pixels.AsSpan().SequenceEqual(beforePixels),
                "the scrolled content reached the overlay frame");
            if (afterFrame is not null)
            {
                Save(afterFrame, Path.Combine(outputDirectory, "vr-05-after-scroll.png"));
            }
            Check(toggle.IsOn != before, "drag did not click the tile under the laser");
        }

        // 5. VR-realistic drag: 90 Hz pointer moves from a background thread (like the VR loop), frames counted meanwhile.
        if (scroll is not null)
        {
            scroll.Offset = default;
            await Task.Delay(500);
            _ = surface.TakeFrame();
            var origin = FindCenter(surface.View!, c => c == scroll)!.Value;
            var framesDuringDrag = 0;
            var dragTask = Task.Run(async () =>
            {
                surface.PointerMove(origin.X, origin.Y + 0.25);
                surface.PointerDown();
                await Task.Delay(120); // dead zone: no moves while the trigger settles
                for (var i = 0; i < 90; i++) // 1 s of dragging up
                {
                    surface.PointerMove(origin.X, origin.Y + 0.25 - (i * 0.005));
                    await Task.Delay(11);
                }

                surface.PointerUp();
            });
            var offsets = new HashSet<double>();
            var watchDrag = System.Diagnostics.Stopwatch.StartNew();
            var ticks = 0;
            while (!dragTask.IsCompleted)
            {
                if (surface.TakeFrame() is not null)
                {
                    framesDuringDrag++;
                }

                offsets.Add(scroll.Offset.Y);
                ticks++;
                await Task.Delay(11);
            }

            logger.LogInformation("[INFO] drag: {Ticks} UI ticks in {Ms} ms, {Offsets} distinct scroll offsets", ticks, watchDrag.ElapsedMilliseconds, offsets.Count);

            Check(scroll.Offset.Y > 100, $"90 Hz drag scrolled the page (offset {scroll.Offset.Y:0})");
            Check(framesDuringDrag >= 10, $"frames produced during a 1 s drag: {framesDuringDrag}");
        }

        // 6. Diagnostic: scroll by code (no input at all) and count the frames that reflect it.
        if (scroll is not null)
        {
            scroll.Offset = default;
            await Task.Delay(800);
            _ = surface.TakeFrame();
            var reflected = 0;
            for (var k = 1; k <= 30; k++)
            {
                scroll.Offset = new Vector(0, k * 10);
                surface.PointerMove(0.99, 0.99); // wake the renderer like input would
                await Task.Delay(17);
                if (surface.TakeFrame() is not null)
                {
                    reflected++;
                }
            }

            logger.LogInformation("[INFO] code-driven scroll every 17 ms: {Reflected}/30 offset changes produced a new frame", reflected);
        }

        surface.SetActive(false);
        logger.LogInformation("VR self-test finished: {Result}", failures.Count == 0 ? "PASS" : $"FAIL ({string.Join("; ", failures)})");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task<OverlayFrame?> WaitForFrame(IOverlaySurface surface)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(50);
            if (surface.TakeFrame() is { } frame)
            {
                return frame;
            }
        }

        return null;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>Normalised (0..1) center of the first visual matching <paramref name="predicate"/>.</summary>
    private static Point? FindCenter(Control root, Func<Control, bool> predicate)
    {
        var control = root.GetVisualDescendants().OfType<Control>().FirstOrDefault(predicate);
        if (control?.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root) is not { } center)
        {
            return null;
        }

        return new Point(center.X / root.Bounds.Width, center.Y / root.Bounds.Height);
    }

    private static void Save(OverlayFrame frame, string path)
    {
        using var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
            frame.IsRgba ? PixelFormat.Rgba8888 : PixelFormat.Bgra8888, frame.IsRgba ? AlphaFormat.Unpremul : AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Pixels, y * frame.Stride, buffer.Address + (y * buffer.RowBytes), frame.Stride);
            }
        }

        bitmap.Save(path);
    }

    /// <summary>Logger category.</summary>
    private sealed class HostMarkerForSelfTest;
}
