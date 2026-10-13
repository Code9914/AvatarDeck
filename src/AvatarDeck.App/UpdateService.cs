using AvatarDeck.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace AvatarDeck.App;

/// <summary>
/// Background updates for installed builds (Velopack): a while after start-up, checks the GitHub releases, downloads
/// a newer version and installs it silently when AvatarDeck exits. Does nothing in development builds.
/// </summary>
internal sealed class UpdateService(INotificationService notifications, ILogger<UpdateService> logger) : BackgroundService
{
    private const string ReleasesUrl = "https://github.com/Code9914/AvatarDeck";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            var manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                logger.LogDebug("Not an installed build: update check skipped");
                return;
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                logger.LogInformation("AvatarDeck is up to date ({Version})", manager.CurrentVersion);
                return;
            }

            logger.LogInformation("Downloading update {Version}", update.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(update, cancelToken: stoppingToken).ConfigureAwait(false);
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            notifications.Notify($"Update {update.TargetFullRelease.Version} ready: it will be installed when you quit AvatarDeck");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Offline, GitHub unreachable, no release published yet…: never a problem for the user.
            logger.LogInformation("Update check failed: {Message}", ex.Message);
        }
    }
}
