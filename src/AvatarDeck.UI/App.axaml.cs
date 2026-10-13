using AvatarDeck.Core.Abstractions;
using AvatarDeck.UI.ViewModels;
using AvatarDeck.UI.Vr;
using AvatarDeck.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AvatarDeck.UI;

/// <summary>Avalonia application. Receives the DI container from the host (composition root in AvatarDeck.App).</summary>
/// <remarks>
/// AvatarDeck lives in the notification area: the desktop window is created on demand and destroyed when closed
/// (its visual tree is a large part of the memory footprint), while the VR panel keeps running in the background.
/// </remarks>
public sealed partial class App : Application
{
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider? _services;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _viewModel;
    private IVrPanelController? _vr;
    private MainWindow? _window;
    private NativeMenuItem? _panelItem;
    private NativeMenuItem? _autoStartItem;
    private DispatcherTimer? _trimTimer;

    /// <summary>Parameterless constructor for the XAML previewer.</summary>
    public App()
    {
    }

    public App(IServiceProvider services) => _services = services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (_services is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _viewModel = _services.GetRequiredService<MainViewModel>();
            _vr = _services.GetRequiredService<IVrPanelController>();
            var options = _services.GetService<AppLaunchOptions>() ?? new AppLaunchOptions();

            // Closing the window keeps AvatarDeck running in the notification area; "Quit" exits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _services.GetService<OffscreenPanelHost>()?.Initialize(_viewModel);
            _ = _viewModel.InitializeAsync();

            CreateTrayIcon();
            _vr.StateChanged += (_, _) => Dispatcher.UIThread.Post(OnVrStateChanged);
            if (options.ExitWithSteamVr)
            {
                _vr.SteamVrExiting += (_, _) => Dispatcher.UIThread.Post(Quit);
            }

            if (options.StartHidden)
            {
                ScheduleIdleTrim();
            }
            else
            {
                ShowMainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens (or brings to front) the desktop window. UI thread.</summary>
    public void ShowMainWindow()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closed += (_, _) =>
            {
                _window = null;
                ScheduleIdleTrim();
            };
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Quit()
    {
        _window?.Close();
        _desktop?.Shutdown();
    }

    private void CreateTrayIcon()
    {
        var open = new NativeMenuItem("Open AvatarDeck");
        open.Click += (_, _) => ShowMainWindow();

        _panelItem = new NativeMenuItem("VR panel") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _panelItem.Click += (_, _) => _vr?.TogglePanel();

        _autoStartItem = new NativeMenuItem("Start with SteamVR") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _autoStartItem.Click += (_, _) =>
        {
            if (_vr is not null)
            {
                _vr.Settings.StartWithSteamVr = !_vr.Settings.StartWithSteamVr;
                _vr.ApplySettings();
            }
        };

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var tray = new TrayIcon
        {
            ToolTipText = "AvatarDeck",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AvatarDeck.UI/Assets/avatardeck.ico"))),
            Menu = [open, _panelItem, _autoStartItem, new NativeMenuItemSeparator(), quit],
        };
        tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, [tray]);
        OnVrStateChanged();
    }

    private void OnVrStateChanged()
    {
        if (_vr is null)
        {
            return;
        }

        if (_panelItem is not null)
        {
            _panelItem.IsChecked = _vr.IsPanelVisible;
            _panelItem.IsEnabled = _vr.Status == VrStatus.Connected;
        }

        if (_autoStartItem is not null)
        {
            _autoStartItem.IsChecked = _vr.Settings.StartWithSteamVr;
        }

        if (!_vr.IsPanelVisible)
        {
            ScheduleIdleTrim();
        }
    }

    /// <summary>Trims memory once nothing is on screen for a few seconds (window closed, VR panel hidden).</summary>
    private void ScheduleIdleTrim()
    {
        if (_window is not null || _vr?.IsPanelVisible == true)
        {
            return;
        }

        if (_trimTimer is null)
        {
            _trimTimer = new DispatcherTimer { Interval = IdleTrimDelay };
            _trimTimer.Tick += (_, _) =>
            {
                _trimTimer.Stop();
                if (_window is null && _vr?.IsPanelVisible != true)
                {
                    MemoryTrim.Run();
                }
            };
        }

        _trimTimer.Stop();
        _trimTimer.Start();
    }
}
