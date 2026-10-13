using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;

namespace AvatarDeck.VR;

/// <summary>
/// Locates SteamVR and its <c>openvr_api.dll</c>. AvatarDeck does not ship the native DLL: it loads the one
/// installed with SteamVR, which always matches the running runtime.
/// </summary>
public static class OpenVrRuntime
{
    private static int _resolverInstalled;

    /// <summary>Registers the DllImport resolver for the OpenVR bindings assembly (idempotent).</summary>
    public static void InstallResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(OpenVR).Assembly, Resolve);
    }

    /// <summary>True when SteamVR's server is running (checked without loading OpenVR, so SteamVR is never launched by us).</summary>
    public static bool IsSteamVrRunning()
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>SteamVR runtime folder, read from <c>%LocalAppData%\openvr\openvrpaths.vrpath</c>.</summary>
    public static string? FindRuntimeDirectory()
    {
        var vrpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        try
        {
            using var stream = File.OpenRead(vrpath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("runtime", out var runtimes))
            {
                foreach (var runtime in runtimes.EnumerateArray())
                {
                    if (runtime.GetString() is { } path && Directory.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through
        }

        return null;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "openvr_api", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        // 1. Next to the executable (lets advanced users pin a version). 2. SteamVR's own copy.
        var local = Path.Combine(AppContext.BaseDirectory, "openvr_api.dll");
        if (File.Exists(local) && NativeLibrary.TryLoad(local, out var handle))
        {
            return handle;
        }

        if (FindRuntimeDirectory() is { } runtime)
        {
            var candidate = Path.Combine(runtime, "bin", Environment.Is64BitProcess ? "win64" : "win32", "openvr_api.dll");
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero; // default probing → DllNotFoundException, reported as "SteamVR not installed"
    }
}
