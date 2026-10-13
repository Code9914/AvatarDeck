using AvatarDeck.Infrastructure;
using AvatarDeck.Osc;
using AvatarDeck.UI;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.UI.ViewModels;
using AvatarDeck.UI.Vr;
using AvatarDeck.VR;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack;

namespace AvatarDeck.App;

/// <summary>
/// Composition root. The generic host owns DI, configuration, logging and background services (OSC, later OpenVR);
/// Avalonia owns the UI thread. The host is started before the UI and stopped after the last window closes.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Installer hooks (install / update / uninstall): must run first, may exit the process.
        VelopackApp.Build().Run();

        // --data-dir <path>: portable mode / testing with an isolated profile folder.
        var diagIndex = Array.IndexOf(args, "--vr-diag");
        if (diagIndex >= 0)
        {
            VrOverlayService.DiagnosticsDirectory = Path.GetFullPath(diagIndex + 1 < args.Length ? args[diagIndex + 1] : "vr-diag");
        }

        var dataDirIndex = Array.IndexOf(args, "--data-dir");
        var paths = dataDirIndex >= 0 && dataDirIndex + 1 < args.Length
            ? new AppPaths(Path.GetFullPath(args[dataDirIndex + 1]))
            : new AppPaths();

        // --steamvr: started by SteamVR (avatardeck.vrmanifest) → background, quit with SteamVR.
        // --minimized: start in the notification area (e.g. Windows start-up).
        var startedBySteamVr = args.Contains("--steamvr");
        var options = new AppLaunchOptions(
            StartHidden: startedBySteamVr || args.Contains("--minimized"),
            ExitWithSteamVr: startedBySteamVr);

        using var instance = SingleInstance.TryAcquire(
            paths.Root,
            activateExisting: !startedBySteamVr,
            onActivate: () => Dispatcher.UIThread.Post(() => (Application.Current as AvatarDeck.UI.App)?.ShowMainWindow()));
        if (instance is null)
        {
            return 0; // already running: it has been asked to show its window
        }

        using var host = CreateHost(args, paths, options);
        var logger = host.Services.GetRequiredService<ILogger<HostMarker>>();

        try
        {
            host.StartAsync().GetAwaiter().GetResult();
            logger.LogInformation("AvatarDeck {Version} started", typeof(Program).Assembly.GetName().Version);

            // --show-vr: open the panel in the headset as soon as SteamVR is connected.
            if (args.Contains("--show-vr"))
            {
                host.Services.GetRequiredService<IVrPanelController>().ShowPanel();
            }

            var builder = BuildAvaloniaApp(host.Services);

            // --vr-diag <dir>: compositor readback + automatic tab changes every 4 s (no headset interaction needed).
            var diag = Array.IndexOf(args, "--vr-diag");
            if (diag >= 0)
            {
                builder = builder.AfterSetup(_ => Dispatcher.UIThread.Post(
                    async () =>
                    {
                        var vm = host.Services.GetRequiredService<MainViewModel>();
                        for (var i = 0; i < 1000; i++)
                        {
                            await Task.Delay(4000);
                            var folders = vm.NavItems.Where(n => n.Folder is not null).ToList();
                            if (folders.Count == 0)
                            {
                                continue;
                            }

                            // Simulate the laser exactly like the VR service does (move, press, hold, release).
                            var target = folders[i % folders.Count];
                            var surface = host.Services.GetRequiredService<OffscreenPanelHost>();
                            var item = surface.View?.GetVisualDescendants().OfType<Avalonia.Controls.ListBoxItem>().FirstOrDefault(c => c.DataContext == target);
                            if (item?.TranslatePoint(new Avalonia.Point(item.Bounds.Width / 2, item.Bounds.Height / 2), surface.View!) is { } p)
                            {
                                surface.PointerMove(p.X / surface.View!.Bounds.Width, p.Y / surface.View.Bounds.Height);
                                await Task.Delay(30);
                                surface.PointerDown();
                                await Task.Delay(150);
                                surface.PointerUp();
                                logger.LogInformation("[DIAG] laser-clicked tab {Tab}", target.Title);
                            }
                        }
                    },
                    DispatcherPriority.Background));
            }
            var selfTest = Array.IndexOf(args, "--vr-selftest");
            if (selfTest >= 0)
            {
                var output = selfTest + 1 < args.Length ? args[selfTest + 1] : "vr-selftest";
                builder = builder.AfterSetup(_ => Dispatcher.UIThread.Post(
                    async () =>
                    {
                        var code = await VrSelfTest.RunAsync(host.Services, output);
                        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(code);
                    },
                    DispatcherPriority.Background));
            }

            return builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error");
            return 1;
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Log.CloseAndFlush();
        }
    }

    private static IHost CreateHost(string[] args, AppPaths paths, AppLaunchOptions options)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // User overrides live next to the profiles so they survive updates.
        builder.Configuration.AddJsonFile(Path.Combine(paths.Root, "settings.json"), optional: true, reloadOnChange: false);

        builder.Services.AddSerilog((_, configuration) => configuration
            .MinimumLevel.Is(args.Contains("--verbose") ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(paths.Logs, "avatardeck-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        builder.Services
            .AddVrcOsc(builder.Configuration) // first: OSCQuery is the preferred avatar definition source
            .AddAvatarDeckCore(paths)
            .AddAvatarDeckVr(connectToSteamVr: !args.Contains("--vr-selftest")) // self-test must not react to a real headset
            .AddSingleton<OffscreenPanelHost>()
            .AddSingleton<IOverlaySurface>(sp => sp.GetRequiredService<OffscreenPanelHost>())
            .AddSingleton(options)
            .AddHostedService<UpdateService>()
            .AddSingleton<VrPanelViewModel>()
            .AddSingleton<MainViewModel>();

        return builder.Build();
    }

    /// <summary>Also used by the Avalonia XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new AvatarDeck.UI.App(services))
            .UsePlatformDetect()
            // CPU rendering: the VR panel is rasterised on the CPU anyway (RenderTargetBitmap), and skipping the GPU
            // backend (ANGLE + graphics driver) cuts ~50 MB of memory. The desktop window is small and mostly static.
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .WithInterFont()
            .LogToTrace();

    /// <summary>Marker type for the startup logger category.</summary>
    private sealed class HostMarker;
}
