namespace AvatarDeck.VR;

/// <summary>Small textures drawn in code (no image assets): the wrist launcher button and the laser cursor.</summary>
public static class ProceduralTextures
{
    private static readonly (float R, float G, float B) AccentA = (0x8B / 255f, 0x7C / 255f, 0xFF / 255f);
    private static readonly (float R, float G, float B) AccentB = (0x4C / 255f, 0xC9 / 255f, 0xF0 / 255f);

    /// <summary>Round launcher button: accent gradient disc with a 2×2 "deck" glyph. Premultiplied BGRA.</summary>
    public static byte[] WristButton(int size, bool highlighted)
    {
        var pixels = new byte[size * size * 4];
        var c = (size - 1) / 2f;
        var radius = size * 0.46f;
        var tile = size * 0.13f;
        var gap = size * 0.045f;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - c;
                var dy = y - c;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                var alpha = Math.Clamp(radius - distance + 0.5f, 0, 1); // anti-aliased edge
                if (alpha <= 0)
                {
                    continue;
                }

                var t = Math.Clamp((x + y) / (2f * size), 0, 1);
                var r = Lerp(AccentA.R, AccentB.R, t);
                var g = Lerp(AccentA.G, AccentB.G, t);
                var b = Lerp(AccentA.B, AccentB.B, t);
                if (highlighted)
                {
                    (r, g, b) = (Lerp(r, 1, 0.25f), Lerp(g, 1, 0.25f), Lerp(b, 1, 0.25f));
                }

                // White ring
                if (distance > radius - (size * 0.035f))
                {
                    (r, g, b) = (1, 1, 1);
                }

                // 2×2 rounded tiles in the middle
                var ax = MathF.Abs(dx) - (gap + (tile / 2));
                var ay = MathF.Abs(dy) - (gap + (tile / 2));
                if (MathF.Abs(ax) < tile / 2 && MathF.Abs(ay) < tile / 2)
                {
                    (r, g, b) = (1, 1, 1);
                }

                var opacity = alpha * 0.95f;
                Write(pixels, ((y * size) + x) * 4, r, g, b, opacity);
            }
        }

        return pixels;
    }

    /// <summary>Move handle under the panel: a rounded pill, white when idle, accent when hovered/dragged. Premultiplied BGRA.</summary>
    public static byte[] Handle(int width, int height, bool highlighted)
    {
        var pixels = new byte[width * height * 4];
        var radius = height / 2f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Distance to the pill's centre segment.
                var cx = Math.Clamp(x + 0.5f, radius, width - radius);
                var dx = x + 0.5f - cx;
                var dy = y + 0.5f - radius;
                var alpha = Math.Clamp(radius - MathF.Sqrt((dx * dx) + (dy * dy)), 0, 1);
                if (alpha <= 0)
                {
                    continue;
                }

                var t = (float)x / width;
                var (r, g, b, a) = highlighted
                    ? (Lerp(AccentA.R, AccentB.R, t), Lerp(AccentA.G, AccentB.G, t), Lerp(AccentA.B, AccentB.B, t), 0.95f)
                    : (1f, 1f, 1f, 0.55f);
                Write(pixels, ((y * width) + x) * 4, r, g, b, alpha * a);
            }
        }

        return pixels;
    }

    /// <summary>Laser cursor: white dot with a soft accent halo. Premultiplied BGRA.</summary>
    public static byte[] Cursor(int size)
    {
        var pixels = new byte[size * size * 4];
        var c = (size - 1) / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = MathF.Sqrt(((x - c) * (x - c)) + ((y - c) * (y - c))) / (size / 2f);
                if (d >= 1)
                {
                    continue;
                }

                if (d < 0.42f)
                {
                    Write(pixels, ((y * size) + x) * 4, 1, 1, 1, Math.Clamp((0.42f - d) * 30, 0, 1));
                }
                else
                {
                    var halo = (1 - d) / 0.58f * 0.55f;
                    Write(pixels, ((y * size) + x) * 4, AccentA.R, AccentA.G, AccentA.B, halo);
                }
            }
        }

        return pixels;
    }

    private static void Write(byte[] pixels, int index, float r, float g, float b, float a)
    {
        pixels[index] = (byte)(b * a * 255);
        pixels[index + 1] = (byte)(g * a * 255);
        pixels[index + 2] = (byte)(r * a * 255);
        pixels[index + 3] = (byte)(a * 255);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
