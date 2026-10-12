using System.Collections.ObjectModel;
using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;
using AvatarDeck.UI.ViewModels.Widgets;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AvatarDeck.UI.ViewModels;

/// <summary>Kind of entry in the left navigation rail.</summary>
public enum NavKind
{
    Favorites,
    Presets,
    Folder,
}

/// <summary>An entry of the navigation rail (a tab).</summary>
public sealed class NavItemViewModel(NavKind kind, string title, string glyph, FolderViewModel? folder = null)
{
    public NavKind Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; } = glyph;

    public FolderViewModel? Folder { get; } = folder;
}

/// <summary>A folder (page) and its widgets; sub-folders are shown as chips.</summary>
public sealed class FolderViewModel(Folder model, FolderViewModel? parent)
{
    public Folder Model { get; } = model;

    public FolderViewModel? Parent { get; } = parent;

    public string Name => Model.Name;

    public List<WidgetViewModel> Widgets { get; } = [];

    public List<FolderViewModel> Children { get; } = [];

    /// <summary>"Clothes › Shoes".</summary>
    public string Path => Parent is null ? Name : $"{Parent.Path} › {Name}";
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
