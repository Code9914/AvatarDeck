using System.Collections.ObjectModel;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.UI.ViewModels.Widgets;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AvatarDeck.UI.ViewModels;

/// <summary>Kind of entry in the left navigation rail.</summary>
public enum NavKind
{
    Favorites,
    Presets,
    Folder,
}

/// <summary>Logical icon names → Segoe Fluent / MDL2 glyphs. Profiles store the logical name.</summary>
public static class Icons
{
    public static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = "",
        ["toggle"] = "",
        ["shirt"] = "",
        ["palette"] = "",
        ["person"] = "",
        ["emoji"] = "",
        ["bolt"] = "",
        ["sparkle"] = "",
        ["light"] = "",
        ["camera"] = "",
        ["settings"] = "",
        ["cube"] = "",
        ["heart"] = "",
        ["flag"] = "",
        ["world"] = "",
        ["music"] = "",
        ["eye"] = "",
        ["home"] = "",
    };

    public static string Glyph(string? icon) => icon is not null && Glyphs.TryGetValue(icon, out var g) ? g : Glyphs["folder"];

    public static IBrush Brush(string? color) =>
        color is not null && Color.TryParse(color, out var c) ? new SolidColorBrush(c) : new SolidColorBrush(Color.Parse("#8B7CFF"));
}

/// <summary>An entry of the navigation rail (a tab).</summary>
public sealed partial class NavItemViewModel(NavKind kind, string title, string glyph, FolderViewModel? folder = null) : ObservableObject
{
    public NavKind Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; } = glyph;

    public FolderViewModel? Folder { get; } = folder;

    public IBrush AccentBrush { get; init; } = Icons.Brush(null);

    /// <summary>Dimmed in the rail (hidden folder shown in edit mode).</summary>
    public bool IsHiddenFolder => Folder?.Model.Hidden == true;

    /// <summary>"3/8": active toggles / toggles in the folder.</summary>
    [ObservableProperty]
    private string? _badge;

    /// <summary>Recomputes <see cref="Badge"/> from the folder's toggles.</summary>
    public void RefreshBadge()
    {
        if (Folder is null)
        {
            return;
        }

        var toggles = Folder.AllWidgets().OfType<ToggleWidgetViewModel>().Where(t => !t.Model.Hidden).ToList();
        Badge = toggles.Count == 0 ? null : $"{toggles.Count(t => t.IsOn)}/{toggles.Count}";
    }
}

/// <summary>A folder (page) and its widgets; sub-folders are shown as chips.</summary>
public sealed class FolderViewModel(Folder model, FolderViewModel? parent)
{
    public Folder Model { get; } = model;

    public FolderViewModel? Parent { get; } = parent;

    public string Name => Model.Name;

    public string Glyph => Icons.Glyph(Model.Icon);

    public IBrush AccentBrush => Icons.Brush(Model.Color);

    public bool IsHidden => Model.Hidden;

    public List<WidgetViewModel> Widgets { get; } = [];

    public List<FolderViewModel> Children { get; } = [];

    /// <summary>"Clothes › Shoes".</summary>
    public string Path => Parent is null ? Name : $"{Parent.Path} › {Name}";

    public FolderViewModel Root => Parent?.Root ?? this;

    public IEnumerable<WidgetViewModel> AllWidgets() => Widgets.Concat(Children.SelectMany(c => c.AllWidgets()));

    public IEnumerable<FolderViewModel> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);
}

/// <summary>A titled group of widgets inside a page ("Hair": Hue, Swap).</summary>
public sealed class SectionViewModel(string? header, IReadOnlyList<WidgetViewModel> widgets)
{
    public string? Header { get; } = header;

    public bool HasHeader => Header is not null;

    public IReadOnlyList<WidgetViewModel> Widgets { get; } = widgets;
}

/// <summary>A preset button.</summary>
public sealed class PresetViewModel(Preset model)
{
    public Preset Model { get; } = model;

    public string Name => Model.Name;

    public string Summary => $"{Model.Values.Count} parameter{(Model.Values.Count == 1 ? string.Empty : "s")}";
}

/// <summary>A toast shown at the bottom of the panel.</summary>
public sealed class ToastViewModel(Notification notification)
{
    public string Message { get; } = notification.Message;

    public NotificationLevel Level { get; } = notification.Level;

    public string Glyph => Level switch
    {
        NotificationLevel.Success => "",
        NotificationLevel.Warning => "",
        NotificationLevel.Error => "",
        _ => "",
    };
}

/// <summary>One line of the action history.</summary>
public sealed record HistoryEntry(DateTime Time, string Label, string Value)
{
    public string TimeText => Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Observable collection helpers.</summary>
internal static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
