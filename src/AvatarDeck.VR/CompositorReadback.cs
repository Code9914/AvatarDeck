using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Valve.VR;
using Vortice.Direct3D11;

namespace AvatarDeck.VR;

/// <summary>
/// Diagnostics (<c>--vr-diag</c>): reads back what the SteamVR compositor actually holds for an overlay
/// (<c>IVROverlay::GetOverlayTexture</c>) and tells which recently submitted frame it matches.
/// Answers "is the headset showing the latest frame?" without anyone wearing the headset.
/// </summary>
public sealed class CompositorReadback(D3D11Device d3d, ILogger logger)
{
    private const int SampleStep = 23; // compare a sparse grid of pixels: fast and sufficient
    private readonly List<(int Sequence, byte[] Samples)> _submitted = [];
    private int _sequence;

    /// <summary>Directory where mismatching frames are saved (raw .bgra dumps), or null.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Remembers a submitted frame (sampled).</summary>
    public void RecordSubmitted(byte[] pixels, int width, int height, bool isRgba)
    {
        _sequence++;
        _submitted.Add((_sequence, Sample(pixels, width, height, width * 4, bgra: !isRgba)));
        if (_submitted.Count > 6)
        {
            _submitted.RemoveAt(0);
        }
    }

    /// <summary>Reads the compositor's copy of the overlay and logs which submission it matches.</summary>
    public void Check(ulong overlay, string reason)
    {
        if (_submitted.Count == 0)
        {
            return;
        }

        var native = IntPtr.Zero;
        uint width = 0, height = 0, format = 0;
        var api = ETextureType.DirectX;
        var colorSpace = EColorSpace.Auto;
        var bounds = default(VRTextureBounds_t);
        var error = OpenVR.Overlay.GetOverlayTexture(overlay, ref native, d3d.Device.NativePointer, ref width, ref height, ref format, ref api, ref colorSpace, ref bounds);
        if (error != EVROverlayError.None || native == IntPtr.Zero)
        {
            logger.LogWarning("[DIAG] GetOverlayTexture failed: {Error}", error);
            return;
        }

        try
        {
            using var view = new ID3D11ShaderResourceView(native);
            using var resource = view.Resource;
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var description = texture.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            using var staging = d3d.Device.CreateTexture2D(description);
            d3d.Context.CopyResource(staging, texture);
            var mapped = d3d.Context.Map(staging, 0, MapMode.Read);
            byte[] pixels;
            try
            {
                pixels = new byte[(int)(mapped.RowPitch * description.Height)];
                Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
            }
            finally
            {
                d3d.Context.Unmap(staging, 0);
            }

            var bgra = description.Format.ToString().StartsWith("B8G8R8A8", StringComparison.Ordinal);
            var samples = Sample(pixels, (int)description.Width, (int)description.Height, (int)mapped.RowPitch, bgra);
            var ranking = _submitted
                .Select(s => (s.Sequence, Diff: Difference(s.Samples, samples)))
                .OrderBy(r => r.Diff)
                .ToList();
            var best = ranking[0];
            var latest = _submitted[^1].Sequence;
            var verdict = best.Diff > 8 ? "MATCHES NOTHING" : best.Sequence == latest ? "latest frame" : $"{latest - best.Sequence} frame(s) behind";
            logger.LogInformation(
                "[DIAG] {Reason}: compositor texture {W}x{H} {Format} shows {Verdict} (diff latest={DiffLatest:0.0}, best={DiffBest:0.0})",
                reason, description.Width, description.Height, description.Format, verdict,
                ranking.First(r => r.Sequence == latest).Diff, best.Diff);

            if (OutputDirectory is not null && reason.Contains("600", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(OutputDirectory);
                SavePng(Path.Combine(OutputDirectory, $"compositor-{DateTime.Now:HHmmss-fff}.png"), pixels, (int)description.Width, (int)description.Height, (int)mapped.RowPitch, bgra);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "[DIAG] compositor readback failed");
        }
        finally
        {
            OpenVR.Overlay.ReleaseNativeOverlayHandle(overlay, native);
        }
    }

    /// <summary>Minimal PNG writer (RGBA, no filtering) so diagnostics need no imaging library.</summary>
    private static void SavePng(string path, byte[] pixels, int width, int height, int pitch, bool bgra)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (var x = 0; x < width; x++)
            {
                var i = (y * pitch) + (x * 4);
                raw.WriteByte(bgra ? pixels[i + 2] : pixels[i]);
                raw.WriteByte(pixels[i + 1]);
                raw.WriteByte(bgra ? pixels[i] : pixels[i + 2]);
                raw.WriteByte(255);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        stream.Write(length);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>Samples a grid of pixels as RGB bytes.</summary>
    private static byte[] Sample(byte[] pixels, int width, int height, int pitch, bool bgra)
    {
        var samples = new List<byte>();
        for (var y = SampleStep / 2; y < height; y += SampleStep)
        {
            for (var x = SampleStep / 2; x < width; x += SampleStep)
            {
                var i = (y * pitch) + (x * 4);
                samples.Add(bgra ? pixels[i + 2] : pixels[i]);
                samples.Add(pixels[i + 1]);
                samples.Add(bgra ? pixels[i] : pixels[i + 2]);
            }
        }

        return [.. samples];
    }

    /// <summary>Mean absolute difference (0–255).</summary>
    private static double Difference(byte[] a, byte[] b)
    {
        var count = Math.Min(a.Length, b.Length);
        if (count == 0)
        {
            return 255;
        }

        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (double)sum / count;
    }
}
