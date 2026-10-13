using AvatarDeck.Core.Models;
using AvatarDeck.Core.Parameters;
using AvatarDeck.Core.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvatarDeck.UI.ViewModels;

/// <summary>Layout operations the editors need; implemented by <see cref="MainViewModel"/>.</summary>
public interface IEditorHost
{
    /// <summary>All folders, for the "move to" list.</summary>
    IReadOnlyList<FolderChoiceViewModel> FolderChoices { get; }

    /// <summary>Marks the profile as customised and saves. <paramref name="rebuild"/> refreshes the panel immediately.</summary>
    void OnLayoutEdited(bool rebuild);

    WidgetBase ChangeWidgetKind(WidgetBase widget, string kind);

    void MoveWidget(WidgetBase widget, int delta);

    void MoveWidgetTo(WidgetBase widget, Folder folder);

    void MoveFolder(Folder folder, int delta);

    Folder AddFolder(Folder? parent);

    void DeleteFolder(Folder folder);

    void CloseEditors();
}

/// <summary>A destination in the "move to" list.</summary>
public sealed class FolderChoiceViewModel(Folder folder, string path)
{
    public Folder Folder { get; } = folder;

    public string Path { get; } = path;

    public string Glyph => Icons.Glyph(Folder.Icon);

    public IBrush AccentBrush => Icons.Brush(Folder.Color);
}

/// <summary>A selectable option (icon, color, widget type) in an editor.</summary>
public sealed partial class ChoiceViewModel(string value, string display) : ObservableObject
{
    public string Value { get; } = value;

    /// <summary>Glyph, color or text to display.</summary>
    public string Display { get; } = display;

    public IBrush Brush => Icons.Brush(Value.StartsWith('#') ? Value : null);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Editor sheet for one control: label, type, visibility, order, folder.</summary>
public sealed partial class WidgetEditorViewModel : ObservableObject
{
    private readonly IEditorHost _host;

    public WidgetEditorViewModel(WidgetBase model, IEditorHost host)
    {
        _model = model;
        _host = host;
        Kinds =
        [
            new ChoiceViewModel("toggle", "Toggle"),
            new ChoiceViewModel("button", "Button"),
            new ChoiceViewModel("slider", "Slider"),
        ];
        RefreshKind();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsShown), nameof(ParameterInfo), nameof(HasRange), nameof(RangeMinimum), nameof(RangeMaximum))]
    private WidgetBase _model;

    public IReadOnlyList<ChoiceViewModel> Kinds { get; }

    public IReadOnlyList<FolderChoiceViewModel> Folders => _host.FolderChoices;

    public string ParameterInfo => $"/avatar/parameters/{Model.Parameter} · {Model.ParameterType}";

    /// <summary>Range editing applies to sliders and int steppers.</summary>
    public bool HasRange => Model is SliderWidget;

    public double RangeMinimum
    {
        get => (Model as SliderWidget)?.Minimum ?? 0;
        set => SetRange(value, null);
    }

    public double RangeMaximum
    {
        get => (Model as SliderWidget)?.Maximum ?? 1;
        set => SetRange(null, value);
    }

    private void SetRange(double? minimum, double? maximum)
    {
        if (Model is not SliderWidget slider)
        {
            return;
        }

        slider.Minimum = (float)(minimum ?? slider.Minimum);
        slider.Maximum = (float)Math.Max(maximum ?? slider.Maximum, slider.Minimum + (slider.ParameterType == ParameterType.Int ? 1 : 0.01));
        OnPropertyChanged(nameof(RangeMinimum));
        OnPropertyChanged(nameof(RangeMaximum));
        _host.OnLayoutEdited(rebuild: true);
    }

    public string Label
    {
        get => Model.Label;
        set
        {
            if (value != Model.Label)
            {
                Model.Label = value;
                OnPropertyChanged();
                _host.OnLayoutEdited(rebuild: false);
            }
        }
    }

    public bool IsShown
    {
        get => !Model.Hidden;
        set
        {
            if (value == Model.Hidden)
            {
                Model.Hidden = !value;
                OnPropertyChanged();
                _host.OnLayoutEdited(rebuild: true);
            }
        }
    }

    [RelayCommand]
    private void SetKind(ChoiceViewModel kind)
    {
        Model = _host.ChangeWidgetKind(Model, kind.Value);
        RefreshKind();
    }

    [RelayCommand]
    private void MoveUp() => _host.MoveWidget(Model, -1);

    [RelayCommand]
    private void MoveDown() => _host.MoveWidget(Model, +1);

    [RelayCommand]
    private void MoveTo(FolderChoiceViewModel target) => _host.MoveWidgetTo(Model, target.Folder);

    [RelayCommand]
    private void Close() => _host.CloseEditors();

    private void RefreshKind()
    {
        var current = Model switch
        {
            ToggleWidget => "toggle",
            ButtonWidget => "button",
            _ => "slider",
        };
        foreach (var kind in Kinds)
        {
            kind.IsSelected = kind.Value == current;
        }
    }
}

/// <summary>Editor sheet for one folder: name, icon, color, visibility, order, sub-folders.</summary>
public sealed partial class FolderEditorViewModel : ObservableObject
{
    private readonly IEditorHost _host;

    public FolderEditorViewModel(Folder model, IEditorHost host)
    {
        Model = model;
        _host = host;
        IconChoices = Icons.Glyphs.Select(kv => new ChoiceViewModel(kv.Key, kv.Value) { IsSelected = kv.Key == model.Icon }).ToList();
        ColorChoices = ProfileBuilder.Palette.Append("#9AA3B5")
            .Select(c => new ChoiceViewModel(c, c) { IsSelected = string.Equals(c, model.Color, StringComparison.OrdinalIgnoreCase) })
            .ToList();
    }

    public Folder Model { get; }

    public IReadOnlyList<ChoiceViewModel> IconChoices { get; }

    public IReadOnlyList<ChoiceViewModel> ColorChoices { get; }

    public bool CanDelete => Model.Widgets.Count == 0 && Model.Children.Count == 0;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (value != Model.Name)
            {
                Model.Name = value;
                OnPropertyChanged();
                _host.OnLayoutEdited(rebuild: false);
            }
        }
    }

    public bool IsShown
    {
        get => !Model.Hidden;
        set
        {
            if (value == Model.Hidden)
            {
                Model.Hidden = !value;
                OnPropertyChanged();
                _host.OnLayoutEdited(rebuild: true);
            }
        }
    }

    [RelayCommand]
    private void SetIcon(ChoiceViewModel icon)
    {
        Model.Icon = icon.Value;
        foreach (var choice in IconChoices)
        {
            choice.IsSelected = choice == icon;
        }

        _host.OnLayoutEdited(rebuild: true);
    }

    [RelayCommand]
    private void SetColor(ChoiceViewModel color)
    {
        Model.Color = color.Value;
        foreach (var choice in ColorChoices)
        {
            choice.IsSelected = choice == color;
        }

        _host.OnLayoutEdited(rebuild: true);
    }

    [RelayCommand]
    private void MoveUp() => _host.MoveFolder(Model, -1);

    [RelayCommand]
    private void MoveDown() => _host.MoveFolder(Model, +1);

    [RelayCommand]
    private void AddSubfolder() => _host.AddFolder(Model);

    [RelayCommand]
    private void Delete() => _host.DeleteFolder(Model);

    [RelayCommand]
    private void Close() => _host.CloseEditors();
}

/// <summary>Widget model conversions used when the user changes a control's type.</summary>
internal static class WidgetConversions
{
    public static WidgetBase Convert(WidgetBase source, string kind)
    {
        WidgetBase result = kind switch
        {
            "toggle" => new ToggleWidget
            {
                OnValue = source.ParameterType == ParameterType.Bool ? OscValue.FromBool(true) : OscValue.FromInt(1).ConvertTo(source.ParameterType),
                OffValue = OscValue.FromInt(0).ConvertTo(source.ParameterType),
            },
            "button" => new ButtonWidget
            {
                PressValue = source.ParameterType == ParameterType.Bool ? OscValue.FromBool(true) : OscValue.FromInt(1).ConvertTo(source.ParameterType),
                ReleaseValue = OscValue.FromInt(0).ConvertTo(source.ParameterType),
            },
            _ => new SliderWidget
            {
                Minimum = 0,
                Maximum = source.ParameterType == ParameterType.Int ? 255 : 1,
                Step = source.ParameterType == ParameterType.Int ? 1 : 0,
            },
        };

        result.Id = source.Id;
        result.Label = source.Label;
        result.Icon = source.Icon;
        result.Parameter = source.Parameter;
        result.ParameterType = source.ParameterType;
        result.Hidden = source.Hidden;
        return result;
    }
}
