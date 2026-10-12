using AvatarDeck.UI.ViewModels;
using AvatarDeck.UI.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace AvatarDeck.UI;

/// <summary>Avalonia application. Receives the DI container from the host (composition root in AvatarDeck.App).</summary>
public sealed partial class App : Application
{
    private readonly IServiceProvider? _services;

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
            var viewModel = _services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
