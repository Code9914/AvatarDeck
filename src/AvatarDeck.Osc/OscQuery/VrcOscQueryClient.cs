using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AvatarDeck.Core.Parameters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AvatarDeck.Osc.OscQuery;

/// <summary>Full state read from VRChat's OSCQuery server.</summary>
/// <param name="AvatarId">Active avatar id (<c>/avatar/change</c>).</param>
/// <param name="AvatarName">Active avatar name (<c>/avatar/name</c>).</param>
/// <param name="Parameters">Every avatar parameter with its current value (null when VRChat does not expose it).</param>
public sealed record VrcStateSnapshot(
    string? AvatarId,
    string? AvatarName,
    IReadOnlyList<(ParameterDefinition Definition, OscValue? Value)> Parameters);

/// <summary>
/// Client for VRChat's OSCQuery HTTP server (https://github.com/Vidvox/OSCQueryProposal).
/// VRChat listens on a random port; it is discovered from VRChat's log line
/// <c>Advertising Service VRChat-Client-XXXXXX of type OSCQuery on PORT</c> (or configured explicitly).
/// </summary>
public sealed partial class VrcOscQueryClient : IDisposable
{
    private const int MaxLogLinesScanned = 20_000;

    private readonly VrcOscOptions _options;
    private readonly ILogger<VrcOscQueryClient> _logger;
    private readonly HttpClient _http;
    private int? _cachedPort;

    public VrcOscQueryClient(IOptions<VrcOscOptions> options, ILogger<VrcOscQueryClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>Default VRChat data folder: <c>%LocalAppData%Low\VRChat\VRChat</c>.</summary>
    public static string DefaultVrChatDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.GetDirectoryName(local)!, "LocalLow", "VRChat", "VRChat");
        }
    }

    /// <summary>Reads the current avatar state, or null if VRChat's OSCQuery server is not reachable.</summary>
    public async Task<VrcStateSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // Try the cached port first; if it fails (VRChat restarted → new port) rediscover once.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var port = _options.OscQueryPort > 0 ? _options.OscQueryPort : _cachedPort ??= DiscoverPortFromLog();
            if (port is null)
            {
                return null;
            }

            try
            {
                var url = new Uri($"http://{_options.Host}:{port.Value.ToString(CultureInfo.InvariantCulture)}/avatar");
                await using var stream = await _http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Parse(document.RootElement);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("OSCQuery request on port {Port} failed: {Error}", port, ex.Message); // expected while VRChat is closed
                _cachedPort = null;
                if (_options.OscQueryPort > 0)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>Finds VRChat's OSCQuery port in the most recent VRChat log.</summary>
    public int? DiscoverPortFromLog()
    {
        var directory = string.IsNullOrWhiteSpace(_options.VrChatDataDirectory) ? DefaultVrChatDirectory : _options.VrChatDataDirectory;
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var log = new DirectoryInfo(directory).EnumerateFiles("output_log_*.txt")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (log is null)
        {
            return null;
        }

        try
        {
            // VRChat keeps the log open for writing: share read/write.
            using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var port = FindPortInLog(reader);
            if (port is not null)
            {
                _logger.LogInformation("VRChat OSCQuery port {Port} found in {Log}", port, log.Name);
            }

            return port;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read VRChat log {Log}", log.FullName);
            return null;
        }
    }

    /// <summary>Returns the last OSCQuery port advertised in a VRChat log.</summary>
    public static int? FindPortInLog(TextReader reader)
    {
        int? port = null;
        var lines = 0;
        while (reader.ReadLine() is { } line && lines++ < MaxLogLinesScanned)
        {
            var match = AdvertiseRegex().Match(line);
            if (match.Success)
            {
                port = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            }
        }

        return port;
    }

    /// <summary>Parses the JSON tree returned for <c>/avatar</c>.</summary>
    public static VrcStateSnapshot Parse(JsonElement root)
    {
        string? avatarId = null;
        string? avatarName = null;
        var parameters = new List<(ParameterDefinition, OscValue?)>();
        Walk(root);
        return new VrcStateSnapshot(avatarId, avatarName, parameters);

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("FULL_PATH", out var pathElement) && pathElement.GetString() is { } path
                && node.TryGetProperty("TYPE", out var typeElement) && typeElement.GetString() is { Length: > 0 } type)
            {
                var value = node.TryGetProperty("VALUE", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0
                    ? values[0]
                    : default;

                if (path == "/avatar/change" && value.ValueKind == JsonValueKind.String)
                {
                    avatarId = value.GetString();
                }
                else if (path == "/avatar/name" && value.ValueKind == JsonValueKind.String)
                {
                    avatarName = value.GetString();
                }
                else if (ParameterAddress.TryGetName(path, out var name) && TryMapType(type, out var parameterType))
                {
                    // ACCESS: 1 = read, 2 = write, 3 = read/write
                    var access = node.TryGetProperty("ACCESS", out var accessElement) && accessElement.TryGetInt32(out var a) ? a : 3;
                    parameters.Add((new ParameterDefinition(name, parameterType, IsWritable: (access & 2) != 0), ReadValue(value, parameterType)));
                }
            }

            if (node.TryGetProperty("CONTENTS", out var contents) && contents.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in contents.EnumerateObject())
                {
                    Walk(child.Value);
                }
            }
        }
    }

    private static bool TryMapType(string oscType, out ParameterType type)
    {
        switch (oscType)
        {
            case "T" or "F":
                type = ParameterType.Bool;
                return true;
            case "i":
                type = ParameterType.Int;
                return true;
            case "f":
                type = ParameterType.Float;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static OscValue? ReadValue(JsonElement value, ParameterType type) => value.ValueKind switch
    {
        JsonValueKind.True => OscValue.FromBool(true),
        JsonValueKind.False => OscValue.FromBool(false),
        JsonValueKind.Number when type == ParameterType.Int && value.TryGetInt32(out var i) => OscValue.FromInt(i),
        JsonValueKind.Number => OscValue.FromFloat(value.GetSingle()).ConvertTo(type),
        _ => null,
    };

    [GeneratedRegex(@"Advertising Service VRChat-Client-\S+ of type OSCQuery on (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AdvertiseRegex();

    public void Dispose() => _http.Dispose();
}
