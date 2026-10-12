using AvatarDeck.Core.Parameters;

namespace AvatarDeck.Core.Models;

/// <summary>
/// Everything AvatarDeck knows about one avatar: its folders, widgets, presets and favorites.
/// Persisted as <c>avatars/{AvatarId}.json</c>.
/// </summary>
public sealed class AvatarProfile
{
    /// <summary>Current on-disk schema version. Bump it and add a migration when the format changes.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>VRChat avatar id (<c>avtr_…</c>), or a local id for manually created profiles.</summary>
    public string AvatarId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Top-level folders (tabs). Folders can be nested.</summary>
    public List<Folder> Folders { get; set; } = [];

    public List<Preset> Presets { get; set; } = [];

    /// <summary>Ids of favorite widgets, in display order.</summary>
    public List<string> Favorites { get; set; } = [];

    public PanelLayout Layout { get; set; } = new();

    /// <summary>Where this profile came from; used to decide whether regeneration may overwrite it.</summary>
    public ProfileSource Source { get; set; } = ProfileSource.Manual;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Enumerates every widget in every folder, depth-first.</summary>
    public IEnumerable<WidgetBase> AllWidgets() => Folders.SelectMany(f => f.AllWidgets());

    /// <summary>Finds a widget by id.</summary>
    public WidgetBase? FindWidget(string id) => AllWidgets().FirstOrDefault(w => w.Id == id);
}

/// <summary>Origin of a profile.</summary>
public enum ProfileSource
{
    Manual,
    VrcOscConfig,
    UnityProject,
}

/// <summary>A named group of widgets, optionally containing sub-folders (e.g. "Clothes" → Hoodie, Jacket…).</summary>
public sealed class Folder
{
    public string Id { get; set; } = WidgetBase.NewId();

    public string Name { get; set; } = string.Empty;

    public string? Icon { get; set; }

    public List<WidgetBase> Widgets { get; set; } = [];

    public List<Folder> Children { get; set; } = [];

    public IEnumerable<WidgetBase> AllWidgets() => Widgets.Concat(Children.SelectMany(c => c.AllWidgets()));
}

/// <summary>A one-click set of parameter values (Outfit 1, Casual, Public…).</summary>
public sealed class Preset
{
    public string Id { get; set; } = WidgetBase.NewId();

    public string Name { get; set; } = string.Empty;

    public string? Icon { get; set; }

    public List<PresetEntry> Values { get; set; } = [];
}

/// <summary>One parameter assignment inside a <see cref="Preset"/>.</summary>
public sealed class PresetEntry
{
    public string Parameter { get; set; } = string.Empty;

    public OscValue Value { get; set; }
}

/// <summary>Per-avatar visual layout of the tablet content.</summary>
public sealed class PanelLayout
{
    /// <summary>Widget grid columns.</summary>
    public int Columns { get; set; } = 3;

    /// <summary>Id of the folder opened last, restored on load.</summary>
    public string? LastFolderId { get; set; }
}
