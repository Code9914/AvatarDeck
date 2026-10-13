using AvatarDeck.Core.Abstractions;
using AvatarDeck.Core.Services;
using AvatarDeck.Infrastructure.Profiles;
using AvatarDeck.Infrastructure.VrChat;
using Microsoft.Extensions.DependencyInjection;

namespace AvatarDeck.Infrastructure;

/// <summary>DI registration for persistence and domain services.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAvatarDeckCore(this IServiceCollection services, AppPaths paths)
    {
        paths.EnsureCreated();
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IProfileStore, JsonProfileStore>();
        services.AddSingleton<ISettingsStore, Settings.JsonSettingsStore>();
        services.AddSingleton<IAvatarDefinitionSource, VrcOscConfigReader>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<PresetRunner>();
        return services;
    }
}
