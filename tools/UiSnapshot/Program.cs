// UiSnapshot — renders the tablet offscreen (Skia, no window, no GPU) to PNG files.
//
//   dotnet run --project tools/UiSnapshot -- <data-dir> <output-dir>
//
// Used for visual regression checks and documentation screenshots. It exercises the same
// offscreen rendering path the VR overlay uses (render a TopLevel → bitmap).
using AvatarDeck.Infrastructure;
using AvatarDeck.Osc;
using AvatarDeck.UI.ViewModels;
using AvatarDeck.UI.ViewModels.Widgets;
using AvatarDeck.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --live: read the real VRChat state (OSCQuery) instead of isolating from the game.
var live = args.Contains("--live");
args = args.Where(a => a != "--live").ToArray();
var dataDir = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "samples");
var outputDir = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/snapshots");
Directory.CreateDirectory(outputDir);

var services = new ServiceCollection()
    .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true))
    // Point VRChat discovery at an empty folder so snapshots never read the live game state.
    .AddVrcOsc(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Osc:VrChatDataDirectory"] = live ? null : Path.Combine(dataDir, "no-vrchat"),
    }).Build())
    .AddAvatarDeckCore(new AppPaths(dataDir))
    .AddSingleton<MainViewModel>()
    .BuildServiceProvider();

AppBuilder.Configure(() => new AvatarDeck.UI.App(services))
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var vm = services.GetRequiredService<MainViewModel>();
var window = new Window
{
    Width = 1100,
    Height = 720,
    Background = new SolidColorBrush(Color.Parse("#0B0D14")),
    Content = new MainView { DataContext = vm, Margin = new Thickness(12) },
};
window.Show();

var init = vm.InitializeAsync();
Pump(() => init.IsCompleted && vm.NavItems.Count > 0, TimeSpan.FromSeconds(15));
Console.WriteLine($"init completed={init.IsCompleted} nav={vm.NavItems.Count} avatar={vm.AvatarName}");

if (live)
{
    Snap("live-01");
    vm.SelectedNav = vm.NavItems.ElementAtOrDefault(1) ?? vm.SelectedNav;
    if (vm.CurrentFolder is { Widgets.Count: 0, Children.Count: > 0 } folder)
    {
        vm.CurrentFolder = folder.Children[0]; // e.g. "A3C › Toggle"
    }

    Snap("live-02");
    return;
}

void Snap(string name)
{
    Pump(() => false, TimeSpan.FromMilliseconds(400)); // let transitions settle
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
    var path = Path.Combine(outputDir, name + ".png");
    frame.Save(path);
    Console.WriteLine(path);
}

// Favorites with some live state.
foreach (var widget in vm.VisibleWidgets)
{
    switch (widget)
    {
        case ToggleWidgetViewModel t: t.ApplyRemoteValue(t.Parameter, true); break;
        case SliderWidgetViewModel s: s.ApplyRemoteValue(s.Parameter, 0.75f); break;
    }
}

Snap("01-favorites");

vm.SelectedNav = vm.NavItems.First(n => n.Title == "Clothes");
Snap("02-folder");

vm.SelectedNav = vm.NavItems.First(n => n.Kind == NavKind.Presets);
Snap("03-presets");

vm.SearchText = "hair";
Snap("04-search");

static void Pump(Func<bool> until, TimeSpan? max = null)
{
    var deadline = DateTime.UtcNow + (max ?? TimeSpan.FromSeconds(5));
    while (!until() && DateTime.UtcNow < deadline)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(10);
    }
}
