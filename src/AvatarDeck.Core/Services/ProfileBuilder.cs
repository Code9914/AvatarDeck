using System.Text;
using System.Text.RegularExpressions;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;

namespace AvatarDeck.Core.Services;

/// <summary>
/// Turns the flat parameter list VRChat exposes into a tidy, human-friendly profile.
/// </summary>
/// <remarks>
/// Rules, in order:
/// <list type="number">
/// <item>Drop what is never user-facing: read-only/built-in parameters, VRCFury version markers (<c>VF_1.2.3</c>),
/// VRCFury parameter-compressor internals (<c>VF57_SyncDataNum0</c>) and local copies of another parameter
/// (<c>VF57_Hair_hue</c> when <c>Hair_hue</c> exists).</item>
/// <item>Send system features (GoGo Loco, tracking control, smoothing, tracking flags) to a hidden "System" folder.</item>
/// <item>Build folders from <c>/</c>-separated paths, cleaning VRCFury prefixes (<c>VF132_Shockwave</c> → "Shockwave").</item>
/// <item>Group flat VRCFury-prefixed parameters (<c>VF134_Flashlight</c>) into an "Extras" folder.</item>
/// <item>Hide animator internals (<c>isIdle</c>, <c>resetLoco</c>, <c>…Synced</c>) in "System".</item>
/// <item>Everything else goes to the "Avatar" folder. Inside a page, the UI groups widgets into sections by
/// their first word (<see cref="SectionKey"/>): Hair hue / Hair swap → "Hair".</item>
/// </list>
/// </remarks>
public static partial class ProfileBuilder
{
    /// <summary>Bump when the rules change: untouched generated profiles are then re-organised automatically.</summary>
    public const int GeneratorVersion = 3;

    /// <summary>Folder receiving parameters that have no path prefix.</summary>
    public const string DefaultFolderName = "Avatar";

    /// <summary>Hidden folder receiving system/locomotion/tracking parameters.</summary>
    public const string SystemFolderName = "System";

    /// <summary>Folder receiving flat VRCFury prefab parameters.</summary>
    public const string ExtrasFolderName = "Extras";

    /// <summary>Accent palette assigned to generated folders, in order.</summary>
    public static readonly IReadOnlyList<string> Palette =
        ["#8B7CFF", "#4CC9F0", "#F72585", "#3DDC97", "#FFC857", "#FF8C42", "#A0E426", "#FF5D73", "#5B8CFF", "#C77DFF"];

    /// <summary>VRChat built-ins that are never shown.</summary>
    private static readonly HashSet<string> IgnoredParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
        "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated",
        "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion",
        "IsAnimatorEnabled", "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters",
        "EyeHeightAsPercent", "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV",
    };

    /// <summary>Feature flags of tracking/face-tracking systems: real parameters, but not avatar customisation.</summary>
    private static readonly HashSet<string> SystemParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "EyeTrackingActive", "LipTrackingActive", "FaceTrackingEmulation", "FaceTrackingLimits",
        "FacialExpressionsDisabled", "VisemesEnable", "RemoteModeActive", "TrackingActive",
    };

    /// <summary>First path segments of well-known system prefabs, with a friendly sub-folder name.</summary>
    private static readonly Dictionary<string, string> SystemRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Go"] = "GoGo Loco",
        ["GoLoco"] = "GoGo Loco",
        ["Smoothing"] = "Smoothing",
        ["State"] = "Tracking",
        ["FT"] = "Face Tracking",
        ["v2"] = "Face Tracking",
        ["OSCm"] = "OSC",
        ["SPS"] = "SPS",
    };

    /// <summary>Keyword → icon name, used for generated folders.</summary>
    private static readonly (string Keyword, string Icon)[] IconKeywords =
    [
        ("toggle", "toggle"), ("cloth", "shirt"), ("outfit", "shirt"), ("wear", "shirt"), ("hair", "palette"),
        ("color", "palette"), ("colour", "palette"), ("hue", "palette"), ("avatar", "person"), ("body", "person"),
        ("face", "emoji"), ("shock", "bolt"), ("effect", "sparkle"), ("particle", "sparkle"), ("light", "light"),
        ("cam", "camera"), ("extra", "sparkle"), ("system", "settings"), ("prop", "cube"), ("weapon", "cube"),
    ];

    /// <summary>What happens to a parameter.</summary>
    private enum Placement
    {
        Drop,
        System,
        User,
    }

    // ───────────────────────────── Public API ─────────────────────────────

    /// <summary>Returns true when the parameter can become a widget at all (writable, not a VRChat built-in).</summary>
    public static bool IsUserParameter(ParameterDefinition parameter) =>
        parameter.IsWritable
        && !IgnoredParameters.Contains(parameter.Name)
        && !parameter.Name.EndsWith("_IsGrabbed", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_Angle", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_Stretch", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_Squish", StringComparison.Ordinal)
        && !parameter.Name.EndsWith("_IsPosed", StringComparison.Ordinal);

    /// <summary>Builds a new, organised profile from an avatar definition.</summary>
    public static AvatarProfile Build(AvatarDefinition definition, ProfileSource source)
    {
        var profile = new AvatarProfile
        {
            AvatarId = definition.AvatarId,
            Name = string.IsNullOrWhiteSpace(definition.Name) ? definition.AvatarId : definition.Name,
            Source = source,
            GeneratorVersion = GeneratorVersion,
        };

        Place(profile, definition, definition.Parameters);
        Finish(profile, restructure: true);
        return profile;
    }

    /// <summary>
    /// Adds widgets for parameters missing from an existing profile, placed with the same rules,
    /// without touching what the user customised.
    /// </summary>
    /// <returns>Number of widgets added.</returns>
    public static int MergeMissing(AvatarProfile profile, AvatarDefinition definition)
    {
        var known = profile.AllWidgets().SelectMany(w => w.Parameters).ToHashSet(StringComparer.Ordinal);
        var missing = definition.Parameters.Where(p => !known.Contains(p.Name)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        var before = profile.AllWidgets().Count();
        Place(profile, definition, missing);
        var added = profile.AllWidgets().Count() - before;
        if (added > 0)
        {
            // Never restructure a layout the user arranged by hand: only style the new folders.
            Finish(profile, restructure: !profile.IsCustomized);
        }

        return added;
    }

    /// <summary>
    /// Rebuilds the layout from scratch with the current rules, keeping presets and favorites (matched by parameter).
    /// </summary>
    public static AvatarProfile Reorganize(AvatarProfile existing, AvatarDefinition definition)
    {
        var rebuilt = Build(definition with { Name = existing.Name }, existing.Source == ProfileSource.Manual ? ProfileSource.VrcOscConfig : existing.Source);
        rebuilt.Presets = existing.Presets;

        var favoriteParameters = existing.Favorites
            .Select(existing.FindWidget)
            .Where(w => w is not null)
            .Select(w => w!.Parameter)
            .ToList();
        var byParameter = rebuilt.AllWidgets().GroupBy(w => w.Parameter).ToDictionary(g => g.Key, g => g.First());
        rebuilt.Favorites = favoriteParameters.Where(byParameter.ContainsKey).Select(p => byParameter[p].Id).ToList();
        return rebuilt;
    }

    /// <summary>Creates the default widget for a parameter.</summary>
    public static WidgetBase CreateWidget(ParameterDefinition parameter, string? label = null)
    {
        label ??= Humanize(StripVrcFuryPrefix(parameter.Name.Split('/')[^1]));
        return parameter.Type switch
        {
            ParameterType.Bool => new ToggleWidget { Label = label, Parameter = parameter.Name, ParameterType = ParameterType.Bool },
            ParameterType.Int => new SliderWidget { Label = label, Parameter = parameter.Name, ParameterType = ParameterType.Int, Minimum = 0, Maximum = 255, Step = 1 },
            _ => new SliderWidget { Label = label, Parameter = parameter.Name, ParameterType = ParameterType.Float },
        };
    }

    /// <summary>
    /// Section key used by the UI to group widgets of one page: the first word of the label, lower-case.
    /// "Hair Hue" and "Hair swap" share the section "hair".
    /// </summary>
    public static string SectionKey(string label)
    {
        var trimmed = label.TrimStart();
        var end = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var word = end < 0 ? trimmed : trimmed[..end];
        word = word.TrimEnd('s', 'S').Length >= 3 ? word.TrimEnd('s', 'S') : word; // "Tops" ≈ "Top"
        return word.ToLowerInvariant();
    }

    /// <summary>Removes a VRCFury instance prefix: <c>VF134_Flashlight</c> → <c>Flashlight</c>.</summary>
    public static string StripVrcFuryPrefix(string name)
    {
        var match = VrcFuryPrefixRegex().Match(name);
        return match.Success && match.Length < name.Length ? name[match.Length..] : name;
    }

    /// <summary>Turns <c>HairHue</c> / <c>hair_hue</c> into <c>Hair Hue</c> / <c>Hair hue</c>.</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-' || (c == '.' && i > 0 && i < name.Length - 1 && char.IsLetter(name[i + 1])))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (i > 0 && builder.Length > 0 && builder[^1] != ' '
                && ((char.IsUpper(c) || char.IsDigit(c)) && char.IsLower(name[i - 1])))
            {
                builder.Append(' ');
            }

            builder.Append(builder.Length == 0 ? char.ToUpperInvariant(c) : c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>Picks an icon name for a folder from keywords in its name.</summary>
    public static string GuessIcon(string folderName)
    {
        foreach (var (keyword, icon) in IconKeywords)
        {
            if (folderName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return icon;
            }
        }

        return "folder";
    }

    // ───────────────────────────── Placement ─────────────────────────────

    private static void Place(AvatarProfile profile, AvatarDefinition definition, IEnumerable<ParameterDefinition> parameters)
    {
        var allNames = definition.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var parameter in parameters.Where(IsUserParameter).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (placement, systemFolder) = Classify(parameter.Name, allNames);
            if (placement == Placement.Drop)
            {
                continue;
            }

            var segments = parameter.Name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var label = Humanize(StripVrcFuryPrefix(segments[^1]));
            string[] path;

            if (placement == Placement.System)
            {
                path = [SystemFolderName, systemFolder!];
            }
            else if (segments.Length > 1)
            {
                path = segments[..^1].Select(s => Humanize(StripVrcFuryPrefix(s))).ToArray();
            }
            else if (VrcFuryPrefixRegex().IsMatch(parameter.Name))
            {
                // Flat parameters of VRCFury prefabs: one "Extras" folder; sections group them inside the page.
                path = [ExtrasFolderName];
            }
            else
            {
                path = [DefaultFolderName];
            }

            var folder = GetOrCreateFolder(profile.Folders, path);
            if (placement == Placement.System)
            {
                profile.Folders.First(f => f.Name == SystemFolderName).Hidden = true;
            }

            folder.Widgets.Add(CreateWidget(parameter, label));
        }
    }

    /// <summary>Decides whether a parameter is dropped, hidden in System (and in which sub-folder) or shown.</summary>
    private static (Placement Placement, string? SystemFolder) Classify(string name, HashSet<string> allNames)
    {
        if (VrcFuryMarkerRegex().IsMatch(name) || VrcFuryCompressorRegex().IsMatch(name))
        {
            return (Placement.Drop, null);
        }

        // Local copy made by VRCFury of a parameter that also exists unprefixed.
        var stripped = StripVrcFuryPrefix(name);
        if (stripped != name && allNames.Contains(stripped))
        {
            return (Placement.Drop, null);
        }

        var segments = stripped.Split('/');
        if (segments.Length > 1 && SystemRoots.TryGetValue(segments[0], out var friendly))
        {
            return (Placement.System, friendly);
        }

        if (SystemParameters.Contains(name) || VrcFuryTrackingRegex().IsMatch(name)
            || name.Contains("FullControllerBuilder", StringComparison.Ordinal))
        {
            return (Placement.System, "Tracking");
        }

        if (segments.Length == 1)
        {
            // Internal state of custom locomotion / animator logic: isIdle, isMoving, resetLoco, LocoGroundFlap…
            if (StateFlagRegex().IsMatch(stripped) || stripped.Contains("loco", StringComparison.OrdinalIgnoreCase))
            {
                return (Placement.System, "Locomotion");
            }

            // Network sync helpers: BlinkTriggerSynced, BonkSync…
            if (stripped.EndsWith("Sync", StringComparison.OrdinalIgnoreCase) || stripped.EndsWith("Synced", StringComparison.OrdinalIgnoreCase))
            {
                return (Placement.System, "Sync");
            }
        }

        return (Placement.User, null);
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

    /// <summary>Flattens wrapper folders, orders folders and assigns icons/colors to new ones.</summary>
    private static void Finish(AvatarProfile profile, bool restructure)
    {
        if (restructure)
        {
            Flatten(profile.Folders);

            // Path folders first (alphabetical), then Avatar, Extras, and System last. List.Sort is unstable: use OrderBy.
            var ordered = profile.Folders
                .OrderBy(Rank)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            profile.Folders.Clear();
            profile.Folders.AddRange(ordered);
        }

        var colorIndex = 0;
        foreach (var folder in profile.Folders)
        {
            folder.Icon ??= GuessIcon(folder.Name);
            folder.Color ??= folder.Name == SystemFolderName ? "#9AA3B5" : Palette[colorIndex++ % Palette.Count];
            foreach (var child in folder.Children.SelectMany(c => c.SelfAndDescendants()))
            {
                child.Icon ??= GuessIcon(child.Name);
                child.Color ??= folder.Color;
            }
        }

        static int Rank(Folder f) => f.Name switch
        {
            SystemFolderName => 3,
            DefaultFolderName => 1,
            _ when f.Name.StartsWith(ExtrasFolderName, StringComparison.Ordinal) => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// "A3C" containing only "Toggle" becomes a single "A3C Toggle" folder. If a folder with the merged name already
    /// exists (parameters added later), the contents are merged into it.
    /// </summary>
    private static void Flatten(List<Folder> folders)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            while (folder.Widgets.Count == 0 && folder.Children.Count == 1 && folder.Name != SystemFolderName)
            {
                var child = folder.Children[0];
                child.Name = $"{folder.Name} {child.Name}";
                child.Hidden |= folder.Hidden;
                folders[i] = folder = child;
            }

            var twin = folders.FindIndex(f => f != folder && string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase));
            if (twin >= 0 && twin < i)
            {
                folders[twin].Widgets.AddRange(folder.Widgets);
                folders[twin].Children.AddRange(folder.Children);
                folders.RemoveAt(i--);
                continue;
            }

            Flatten(folder.Children);
        }
    }

    /// <summary>VRCFury instance prefix: <c>VF134_</c>.</summary>
    [GeneratedRegex(@"^VF(\d+)_", RegexOptions.CultureInvariant)]
    private static partial Regex VrcFuryPrefixRegex();

    /// <summary>VRCFury version/build markers and flags: <c>VF_1.1426.0</c>, <c>VF 2022.3.22f1</c>, <c>VF_N</c>, <c>VF/BrokenWd</c>.</summary>
    [GeneratedRegex(@"^VF[_ /]", RegexOptions.CultureInvariant)]
    private static partial Regex VrcFuryMarkerRegex();

    /// <summary>VRCFury parameter compressor internals: <c>VF57_SyncDataNum0</c>, <c>VF54_SyncPointer</c>, <c>VF54_Hairhue/LastSynced</c>.</summary>
    [GeneratedRegex(@"^VF\d+_(Sync(DataNum|Index|Pointer|Data)\w*|.+/(LastSynced|Current))$", RegexOptions.CultureInvariant)]
    private static partial Regex VrcFuryCompressorRegex();

    /// <summary>Animator state flags: <c>isIdle</c>, <c>isMoving</c>.</summary>
    [GeneratedRegex(@"^is[A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex StateFlagRegex();

    /// <summary>VRCFury tracking-control internals: <c>VF34_TC_current_trackingEyes</c>.</summary>
    [GeneratedRegex(@"^VF\d+_TC_", RegexOptions.CultureInvariant)]
    private static partial Regex VrcFuryTrackingRegex();
}
