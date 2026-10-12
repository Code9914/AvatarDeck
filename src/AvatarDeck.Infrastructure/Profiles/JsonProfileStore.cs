using System.Text.Json;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Infrastructure.Serialization;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.Infrastructure.Profiles;

/// <summary>
/// Stores each profile as <c>avatars/{AvatarId}.json</c>. Writes are atomic (temp file + replace),
/// so a crash or power loss can never leave a half-written profile.
/// </summary>
public sealed class JsonProfileStore(AppPaths paths, ILogger<JsonProfileStore> logger) : IProfileStore
{
    public async Task<AvatarProfile?> LoadAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(avatarId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync(stream, AvatarDeckJsonContext.Default.AvatarProfile, cancellationToken).ConfigureAwait(false);
            return profile is null ? null : Migrate(profile);
        }
        catch (JsonException ex)
        {
            // Keep the broken file for manual recovery instead of silently overwriting it.
            var backup = path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(path, backup, overwrite: true);
            logger.LogError(ex, "Profile {Path} is corrupt; a copy was saved to {Backup}", path, backup);
            return null;
        }
    }

    public async Task SaveAsync(AvatarProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.Avatars);
        var path = GetPath(profile.AvatarId);
        var temp = path + ".tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, profile, AvatarDeckJsonContext.Default.AvatarProfile, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
        logger.LogDebug("Saved profile {AvatarId}", profile.AvatarId);
    }

    public async Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Avatars))
        {
            return [];
        }

        var result = new List<ProfileSummary>();
        foreach (var file in Directory.EnumerateFiles(paths.Avatars, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = OpenRead(file);
                var profile = await JsonSerializer.DeserializeAsync(stream, AvatarDeckJsonContext.Default.AvatarProfile, cancellationToken).ConfigureAwait(false);
                if (profile is not null)
                {
                    result.Add(new ProfileSummary(profile.AvatarId, profile.Name, profile.UpdatedAt));
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping unreadable profile {File}", file);
            }
        }

        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task DeleteAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(avatarId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Upgrades older on-disk formats. Add a step here for every schema bump.</summary>
    private static AvatarProfile Migrate(AvatarProfile profile)
    {
        // v1 is the first format: nothing to migrate yet.
        profile.SchemaVersion = AvatarProfile.CurrentSchemaVersion;
        return profile;
    }

    private string GetPath(string avatarId) => Path.Combine(paths.Avatars, ToFileName(avatarId) + ".json");

    /// <summary>Avatar ids are <c>avtr_&lt;guid&gt;</c>, but never trust them as file names.</summary>
    internal static string ToFileName(string avatarId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avatarId);
        var invalid = Path.GetInvalidFileNameChars();
        var chars = avatarId.Select(c => Array.IndexOf(invalid, c) >= 0 || c == '.' ? '_' : c).ToArray();
        return new string(chars);
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
