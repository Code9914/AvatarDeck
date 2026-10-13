namespace AvatarDeck.UI;

/// <summary>How the application was started (from the command line).</summary>
/// <param name="StartHidden">Start in the notification area without opening the desktop window.</param>
/// <param name="ExitWithSteamVr">Quit when SteamVR shuts down (set when SteamVR itself started AvatarDeck).</param>
public sealed record AppLaunchOptions(bool StartHidden = false, bool ExitWithSteamVr = false);
