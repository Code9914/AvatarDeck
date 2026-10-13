using System.Security.Cryptography;
using System.Text;

namespace AvatarDeck.App;

/// <summary>
/// One AvatarDeck per data folder: the VR overlays, the OSC port and the profile files cannot be shared. A second
/// launch (Start menu while already running, or SteamVR auto-start) only asks the first instance to come forward.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Returns the instance guard, or null when AvatarDeck is already running for <paramref name="dataRoot"/>.
    /// In that case the running instance is signalled when <paramref name="activateExisting"/> is true.
    /// </summary>
    public static SingleInstance? TryAcquire(string dataRoot, bool activateExisting, Action onActivate)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToUpperInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: true, $@"Local\AvatarDeck.{id}", out var created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\AvatarDeck.{id}.activate");
        if (!created)
        {
            if (activateExisting)
            {
                activate.Set();
            }

            activate.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
