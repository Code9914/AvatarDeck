using AvatarDeck.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AvatarDeck.VR;

/// <summary>DI registration for the SteamVR overlay. Requires an <see cref="IOverlaySurface"/> (provided by the UI).</summary>
public static class VrServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    /// <param name="connectToSteamVr">False to register the controller without running the overlay loop (tests).</param>
    public static IServiceCollection AddAvatarDeckVr(this IServiceCollection services, bool connectToSteamVr = true)
    {
        services.AddSingleton<VrOverlayService>();
        services.AddSingleton<IVrPanelController>(sp => sp.GetRequiredService<VrOverlayService>());
        if (connectToSteamVr)
        {
            services.AddHostedService(sp => sp.GetRequiredService<VrOverlayService>());
        }

        return services;
    }
}
