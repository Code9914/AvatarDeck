namespace AvatarDeck.Infrastructure;

/// <summary>Well-known file locations.</summary>
public sealed class AppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvatarDeck"))
    {
    }

    /// <summary>Creates paths under a custom root (portable mode, tests).</summary>
    public AppPaths(string root)
    {
        Root = root;
        Avatars = Path.Combine(root, "avatars");
        Logs = Path.Combine(root, "logs");
        Plugins = Path.Combine(root, "plugins");
        Themes = Path.Combine(root, "themes");
    }

    /// <summary><c>%AppData%\AvatarDeck</c> by default.</summary>
    public string Root { get; }

    /// <summary>One JSON file per avatar profile.</summary>
    public string Avatars { get; }

    public string Logs { get; }

    public string Plugins { get; }

    public string Themes { get; }

    /// <summary>
    /// VRChat's generated OSC configs: <c>%LocalAppData%Low\VRChat\VRChat\OSC</c>.
    /// There is no Environment.SpecialFolder for LocalLow, so it is derived from LocalApplicationData.
    /// </summary>
    public static string VrChatOscRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.GetDirectoryName(local)!, "LocalLow", "VRChat", "VRChat", "OSC");
        }
    }

    /// <summary>Creates the writable directories.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Avatars);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Plugins);
        Directory.CreateDirectory(Themes);
    }
}
