using System.Runtime.InteropServices;

namespace AvatarDeck.UI;

/// <summary>
/// Gives memory back to Windows when AvatarDeck goes idle in the background (desktop window closed, VR panel hidden).
/// Most of the working set at that point is start-up and window code that will not run again for a while.
/// </summary>
internal static class MemoryTrim
{
    public static void Run()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        if (OperatingSystem.IsWindows())
        {
            // Pages are brought back on demand; this only stops idle pages from being counted against the app.
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}
