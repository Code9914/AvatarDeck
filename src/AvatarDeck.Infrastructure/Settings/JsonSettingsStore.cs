using System.Text.Json;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Infrastructure.Serialization;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.Infrastructure.Settings;

/// <summary>Stores <see cref="AppSettings"/> in <c>preferences.json</c> (atomic writes, tolerant reads).</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _path = Path.Combine(paths.Root, "preferences.json");
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, Current, AvatarDeckJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize(stream, AvatarDeckJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; using defaults", _path);
        }

        return new AppSettings();
    }
}
