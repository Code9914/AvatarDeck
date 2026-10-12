using AvatarDeck.Core.Abstractions;
using AvatarDeck.Osc.OscQuery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AvatarDeck.Osc;

/// <summary>DI registration for the OSC layer.</summary>
public static class OscServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="VrcOscClient"/> (OSC send/receive) and <see cref="VrcStateSynchronizer"/> (OSCQuery full-state sync).
    /// Call before registering other <see cref="IAvatarDefinitionSource"/>s: the live OSCQuery source is the most accurate
    /// and sources are consulted in registration order.
    /// </summary>
    public static IServiceCollection AddVrcOsc(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VrcOscOptions>(configuration.GetSection(VrcOscOptions.SectionName));
        services.AddSingleton<VrcOscClient>();
        services.AddSingleton<IParameterSender>(sp => sp.GetRequiredService<VrcOscClient>());
        services.AddSingleton<IVrcStateFeed>(sp => sp.GetRequiredService<VrcOscClient>());
        services.AddHostedService(sp => sp.GetRequiredService<VrcOscClient>());

        services.AddSingleton<VrcOscQueryClient>();
        services.AddSingleton<VrcStateSynchronizer>();
        services.AddSingleton<IVrcStateSync>(sp => sp.GetRequiredService<VrcStateSynchronizer>());
        services.AddSingleton<IAvatarDefinitionSource>(sp => sp.GetRequiredService<VrcStateSynchronizer>());
        services.AddHostedService(sp => sp.GetRequiredService<VrcStateSynchronizer>());
        return services;
    }
}
