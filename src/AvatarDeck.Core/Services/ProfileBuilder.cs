using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;

namespace AvatarDeck.Core.Services;

/// <summary>
/// Turns a flat parameter list into an organised profile:
/// bools become toggles, floats become sliders, ints become sliders with integer steps.
/// Folders are derived from <c>/</c>-separated names (VRCFury / Modular Avatar style, e.g. <c>Clothes/Hoodie</c>).
/// </summary>
public static class ProfileBuilder
{
    /// <summary>Folder receiving parameters that have no path prefix.</summary>
    public const string DefaultFolderName = "Parameters";

    /// <summary>
    /// VRChat built-in or tracking parameters that should never become widgets.
    /// See https://creators.vrchat.com/avatars/animator-parameters/.
    /// </summary>
    private static readonly HashSet<string> IgnoredParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
        "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated",
        "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion",
        "IsAnimatorEnabled", "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters",
        "EyeHeightAsPercent", "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV",
    };

    /// <summary>Returns true when the parameter is a user-facing, writable parameter.</summary>
    public static bool IsUserParameter(ParameterDefinition parameter) =>
        parameter.IsWritable
        && !IgnoredParameters.Contains(parameter.Name)
        && !parameter.Name.StartsWith("OSCm/", StringComparison.Ordinal)        // OSC memory internals
        && !parameter.Name.StartsWith("FT/", StringComparison.Ordinal)          // face tracking
        && !parameter.Name.StartsWith("v2/", StringComparison.Ordinal)          // VRCFT v2
        && !parameter.Name.StartsWith("PhysBone", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_IsGrabbed", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_Angle", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_Stretch", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_Squish", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_IsPosed", StringComparison.Ordinal);

    /// <summary>Builds a new profile from an avatar definition.</summary>
    public static AvatarProfile Build(AvatarDefinition definition, ProfileSource source)
    {
        var profile = new AvatarProfile
        {
            AvatarId = definition.AvatarId,
            Name = string.IsNullOrWhiteSpace(definition.Name) ? definition.AvatarId : definition.Name,
            Source = source,
        };

        foreach (var parameter in definition.Parameters.Where(IsUserParameter).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (folderPath, label) = SplitPath(parameter.Name);
            var folder = GetOrCreateFolder(profile.Folders, folderPath);
            folder.Widgets.Add(CreateWidget(parameter, label));
        }

        return profile;
    }

    /// <summary>Creates the default widget for a parameter.</summary>
    public static WidgetBase CreateWidget(ParameterDefinition parameter, string? label = null)
    {
        label ??= SplitPath(parameter.Name).Label;
        return parameter.Type switch
        {
            ParameterType.Bool => new ToggleWidget { Label = label, Parameter = parameter.Name, ParameterType = ParameterType.Bool },
            ParameterType.Int => new SliderWidget { Label = label, Parameter = parameter.Name, ParameterType = ParameterType.Int, Minimum = 0, Maximum = 255, Step = 1 },
            _ => new SliderWidget { Label = label, Parameter = parameter.Name, ParameterType = ParameterType.Float },
        };
    }

    /// <summary>
    /// Adds widgets for parameters missing from an existing profile, without touching what the user customised.
    /// </summary>
    /// <returns>Number of widgets added.</returns>
    public static int MergeMissing(AvatarProfile profile, AvatarDefinition definition)
    {
        var known = profile.AllWidgets().SelectMany(w => w.Parameters).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var parameter in definition.Parameters.Where(IsUserParameter).Where(p => !known.Contains(p.Name)))
        {
            var (folderPath, label) = SplitPath(parameter.Name);
            GetOrCreateFolder(profile.Folders, folderPath).Widgets.Add(CreateWidget(parameter, label));
            added++;
        }

        return added;
    }

    private static (string[] FolderPath, string Label) SplitPath(string name)
    {
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= 1
            ? ([DefaultFolderName], Humanize(name))
            : (parts[..^1], Humanize(parts[^1]));
    }

    private static Folder GetOrCreateFolder(List<Folder> roots, string[] path)
    {
        var level = roots;
        Folder? folder = null;
        foreach (var segment in path)
        {
            folder = level.FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new Folder { Name = segment };
                level.Add(folder);
            }

            level = folder.Children;
        }

        return folder!;
    }

    /// <summary>Turns <c>HairHue</c> / <c>hair_hue</c> into <c>Hair Hue</c>.</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]) && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(builder.Length == 0 ? char.ToUpperInvariant(c) : c);
        }

        return builder.ToString().Trim();
    }
}
