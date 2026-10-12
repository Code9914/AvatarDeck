using System.Text.Json.Serialization;
using AvatarDeck.Core.Models;
using AvatarDeck.Infrastructure.VrChat;

namespace AvatarDeck.Infrastructure.Serialization;

/// <summary>
/// Compile-time generated JSON metadata: no reflection at runtime, trim/AOT safe and faster startup.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AvatarProfile))]
[JsonSerializable(typeof(VrcOscConfigFile))]
public sealed partial class AvatarDeckJsonContext : JsonSerializerContext;
