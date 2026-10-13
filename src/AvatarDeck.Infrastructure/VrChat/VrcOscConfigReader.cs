using System.Text.Json;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Infrastructure.Serialization;
using Microsoft.Extensions.Logging;

namespace AvatarDeck.Infrastructure.VrChat;

/// <summary>
/// Reads the OSC config VRChat writes for every avatar it loads
/// (<c>%LocalAppData%Low\VRChat\VRChat\OSC\usr_*\Avatars\avtr_*.json</c>).
/// This gives a complete typed parameter list without needing the avatar's Unity project.
/// </summary>
public sealed class VrcOscConfigReader : IAvatarDefinitionSource
{
    private readonly string _oscRoot;
    private readonly ILogger<VrcOscConfigReader> _logger;

    public VrcOscConfigReader(ILogger<VrcOscConfigReader> logger)
        : this(AppPaths.VrChatOscRoot, logger)
    {
    }

    /// <summary>Uses a custom root (tests).</summary>
    public VrcOscConfigReader(string oscRoot, ILogger<VrcOscConfigReader> logger)
    {
        _oscRoot = oscRoot;
        _logger = logger;
    }

    public async Task<AvatarDefinition?> TryGetAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_oscRoot) || avatarId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        // Several VRChat accounts may have used the same avatar: take the most recent file.
        var file = Directory.EnumerateFiles(_oscRoot, avatarId + ".json", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        var config = await JsonSerializer.DeserializeAsync(stream, AvatarDeckJsonContext.Default.VrcOscConfigFile, cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            return null;
        }

        var parameters = new List<ParameterDefinition>(config.Parameters.Count);
        foreach (var p in config.Parameters)
        {
            var endpoint = p.Input ?? p.Output;
            if (endpoint is null || !Enum.TryParse<ParameterType>(endpoint.Type, ignoreCase: true, out var type))
            {
                continue;
            }

            // The address is authoritative: VRChat replaces spaces with '_' in addresses ("Hair hue" → /avatar/parameters/Hair_hue).
            var name = ParameterAddress.TryGetName(endpoint.Address, out var fromAddress) ? fromAddress : p.Name;
            parameters.Add(new ParameterDefinition(name, type, IsWritable: p.Input is not null));
        }

        _logger.LogInformation("Read {Count} parameters for {AvatarId} from VRChat OSC config", parameters.Count, avatarId);
        return new AvatarDefinition(config.Id ?? avatarId, config.Name ?? avatarId, parameters);
    }
}

/// <summary>Shape of VRChat's generated OSC avatar config.</summary>
public sealed class VrcOscConfigFile
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public List<VrcOscConfigParameter> Parameters { get; set; } = [];
}

/// <summary>One parameter of a <see cref="VrcOscConfigFile"/>. Read-only parameters have no <see cref="Input"/>.</summary>
public sealed class VrcOscConfigParameter
{
    public string Name { get; set; } = string.Empty;

    public VrcOscConfigEndpoint? Input { get; set; }

    public VrcOscConfigEndpoint? Output { get; set; }
}

/// <summary>Address/type pair of a <see cref="VrcOscConfigParameter"/>.</summary>
public sealed class VrcOscConfigEndpoint
{
    public string Address { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;
}
