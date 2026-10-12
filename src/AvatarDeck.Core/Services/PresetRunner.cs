using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Models;

namespace AvatarDeck.Core.Services;

/// <summary>Applies presets: sends every value of the preset, in order, in a single action.</summary>
public sealed class PresetRunner(IParameterSender sender, INotificationService notifications)
{
    public async Task ApplyAsync(Preset preset, CancellationToken cancellationToken = default)
    {
        foreach (var entry in preset.Values)
        {
            await sender.SendAsync(entry.Parameter, entry.Value, cancellationToken).ConfigureAwait(false);
        }

        notifications.Notify($"Preset \"{preset.Name}\" applied", NotificationLevel.Success);
    }

    /// <summary>Creates a preset capturing the current value of the given parameters.</summary>
    public static Preset Capture(string name, IEnumerable<string> parameters, IVrcStateFeed feed)
    {
        var preset = new Preset { Name = name };
        foreach (var parameter in parameters.Distinct(StringComparer.Ordinal))
        {
            if (feed.TryGetParameter(parameter, out var value))
            {
                preset.Values.Add(new PresetEntry { Parameter = parameter, Value = value });
            }
        }

        return preset;
    }
}
