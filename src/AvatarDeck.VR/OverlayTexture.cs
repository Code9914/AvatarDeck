using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AvatarDeck.VR;

/// <summary>
/// A shared D3D11 device plus one GPU texture per overlay. CPU pixels (BGRA) are uploaded only when a new frame
/// exists, then handed to SteamVR with <c>SetOverlayTexture</c>. Used from the VR thread only.
/// </summary>
public sealed class D3D11Device : IDisposable
{
    private D3D11Device(ID3D11Device device, ID3D11DeviceContext context)
    {
        Device = device;
        Context = context;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    /// <summary>Creates the device on the GPU driving the headset (index reported by SteamVR), or the default GPU.</summary>
    public static D3D11Device Create(int adapterIndex)
    {
        IDXGIAdapter1? adapter = null;
        if (adapterIndex >= 0)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)adapterIndex, out adapter).CheckError();
        }

        try
        {
            D3D11.D3D11CreateDevice(
                adapter,
                adapter is null ? DriverType.Hardware : DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0],
                out ID3D11Device? device,
                out ID3D11DeviceContext? context).CheckError();
            return new D3D11Device(device!, context!);
        }
        finally
        {
            adapter?.Dispose();
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>How overlay pixels reach SteamVR.</summary>
public enum OverlayUploadMode
{
    /// <summary>A new shared D3D11 texture per update (default: no flicker, GPU upload).</summary>
    FreshTexture,

    /// <summary><c>SetOverlayRaw</c>: simplest and always fresh, but SteamVR recreates its texture on every call (brief flicker).</summary>
    Raw,
}

/// <summary>Pixel upload for one overlay.</summary>
/// <remarks>
/// SteamVR caches what it reads from a shared texture: re-using the same texture objects (single or double buffered)
/// left the headset showing an older copy until another overlay of the process changed, even though reading the
/// compositor's texture back showed the new content. Handing over a texture SteamVR has never seen forces it to read
/// the new pixels, so each update uses a fresh texture; the last few are kept alive while SteamVR may still use them.
/// </remarks>
public sealed class OverlayTexture(D3D11Device d3d, OverlayUploadMode mode = OverlayUploadMode.FreshTexture) : IDisposable
{
    private const int KeepAlive = 4;
    private static readonly TimeSpan GpuWaitTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Queue<ID3D11Texture2D> _recent = new();
    private ID3D11Query? _fence;
    private byte[] _rgba = [];

    /// <summary>Duration of the last upload (conversion included), for diagnostics.</summary>
    public double LastUploadMs { get; private set; }

    /// <summary>Uploads pixels to an overlay: premultiplied BGRA, or straight RGBA when <paramref name="isRgba"/>.</summary>
    public void Submit(ulong overlay, byte[] pixels, int width, int height, int stride, bool isRgba = false)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            SubmitCore(overlay, pixels, width, height, stride, isRgba);
        }
        finally
        {
            LastUploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private void SubmitCore(ulong overlay, byte[] pixels, int width, int height, int stride, bool isRgba)
    {
        if (mode == OverlayUploadMode.Raw || isRgba)
        {
            SubmitRaw(overlay, pixels, width, height, stride, isRgba);
            return;
        }

        var texture = d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,

            // Required: the compositor (another process) opens the texture through a shared handle.
            MiscFlags = ResourceOptionFlags.Shared,
        });

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            d3d.Context.UpdateSubresource(texture, 0, null, handle.AddrOfPinnedObject(), (uint)stride, 0);
        }
        finally
        {
            handle.Free();
        }

        WaitForGpu();

        var vrTexture = new Texture_t
        {
            handle = texture.NativePointer,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Auto,
        };
        var error = OpenVR.Overlay.SetOverlayTexture(overlay, ref vrTexture);
        _recent.Enqueue(texture);
        while (_recent.Count > KeepAlive)
        {
            _recent.Dequeue().Dispose();
        }

        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"SetOverlayTexture failed: {error}");
        }
    }

    /// <summary>Blocks until the GPU has executed the upload (typically well under 1 ms).</summary>
    private unsafe void WaitForGpu()
    {
        _fence ??= d3d.Device.CreateQuery(new QueryDescription(QueryType.Event));
        d3d.Context.End(_fence);
        d3d.Context.Flush();
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(GpuWaitTimeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        int done = 0;
        while (d3d.Context.GetData(_fence, (IntPtr)(&done), sizeof(int), AsyncGetDataFlags.None).Code != 0 || done == 0)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
            {
                break;
            }

            Thread.Yield();
        }
    }

    private void SubmitRaw(ulong overlay, byte[] pixels, int width, int height, int stride, bool isRgba)
    {
        if (isRgba && stride == width * 4)
        {
            Upload(overlay, pixels, width, height); // already in SteamVR's format: no conversion
            return;
        }

        var length = width * height * 4;
        if (_rgba.Length != length)
        {
            _rgba = new byte[length];
        }

        // BGRA → RGBA. Alpha is kept premultiplied: the panel is opaque except its anti-aliased rounded corners,
        // where the difference is invisible, and skipping the division keeps this a pure byte shuffle.
        if (stride == width * 4)
        {
            SwapRedBlue(pixels, _rgba);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                SwapRedBlue(pixels.AsSpan(y * stride, width * 4), _rgba.AsSpan(y * width * 4, width * 4));
            }
        }

        Upload(overlay, _rgba, width, height);
    }

    /// <summary>Swaps bytes 0 and 2 of every pixel (BGRA ⇄ RGBA), 16 bytes per SIMD shuffle, split across cores for large images.</summary>
    internal static void SwapRedBlue(byte[] source, byte[] destination)
    {
        const int ParallelThreshold = 1 << 20;
        if (source.Length < ParallelThreshold)
        {
            SwapRedBlueSerial(source, destination);
            return;
        }

        var chunk = ((source.Length / Environment.ProcessorCount) + 15) & ~15;
        Parallel.For(0, (source.Length + chunk - 1) / chunk, i =>
        {
            var start = i * chunk;
            var length = Math.Min(chunk, source.Length - start);
            SwapRedBlueSerial(source.AsSpan(start, length), destination.AsSpan(start, length));
        });
    }

    /// <inheritdoc cref="SwapRedBlue(byte[], byte[])"/>
    internal static void SwapRedBlue(ReadOnlySpan<byte> source, Span<byte> destination) => SwapRedBlueSerial(source, destination);

    private static void SwapRedBlueSerial(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var mask = System.Runtime.Intrinsics.Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        var i = 0;
        ref var src = ref MemoryMarshal.GetReference(source);
        ref var dst = ref MemoryMarshal.GetReference(destination);
        for (; i <= source.Length - 16; i += 16)
        {
            var v = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref src, (nuint)i);
            System.Runtime.Intrinsics.Vector128.Shuffle(v, mask).StoreUnsafe(ref dst, (nuint)i);
        }

        for (; i <= source.Length - 4; i += 4)
        {
            destination[i] = source[i + 2];
            destination[i + 1] = source[i + 1];
            destination[i + 2] = source[i];
            destination[i + 3] = source[i + 3];
        }
    }

    private static void Upload(ulong overlay, byte[] rgba, int width, int height)
    {
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var error = OpenVR.Overlay.SetOverlayRaw(overlay, handle.AddrOfPinnedObject(), (uint)width, (uint)height, 4);
            if (error != EVROverlayError.None)
            {
                throw new InvalidOperationException($"SetOverlayRaw failed: {error}");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        while (_recent.Count > 0)
        {
            _recent.Dequeue().Dispose();
        }

        _fence?.Dispose();
        _fence = null;
        _rgba = [];
    }
}
