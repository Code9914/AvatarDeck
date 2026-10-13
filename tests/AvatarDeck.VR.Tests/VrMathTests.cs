using System.Numerics;

namespace AvatarDeck.VR.Tests;

public class VrMathTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Converts_openvr_matrices_both_ways()
    {
        var pose = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(1, 2, 3);

        var roundTrip = VrMath.ToMatrix(VrMath.ToHmd(pose));
        var hmd = VrMath.ToHmd(pose);

        roundTrip.Should().Be(pose);
        (hmd.m3, hmd.m7, hmd.m11).Should().Be((1f, 2f, 3f), "OpenVR keeps the translation in the 4th column");
        VrMath.FromArray(VrMath.ToArray(pose)).Should().Be(pose);
        VrMath.FromArray([1, 2]).Should().BeNull();
    }

    [Fact]
    public void Places_panel_in_front_of_the_head_facing_the_user()
    {
        // Head at 1.7 m looking down -Z (OpenVR forward), turned 90° to the left.
        var head = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 1.7f, 0);

        var panel = VrMath.PlaceInFront(head, distance: 0.5f, drop: 0.1f);

        panel.Translation.X.Should().BeApproximately(-0.5f, Eps, "turned left → forward is -X");
        panel.Translation.Y.Should().BeApproximately(1.6f, Eps);
        panel.Translation.Z.Should().BeApproximately(0, Eps);

        // The visible face (+Z) points back toward the head.
        var normal = Vector3.Normalize(new Vector3(panel.M31, panel.M32, panel.M33));
        Vector3.Dot(normal, Vector3.Normalize(head.Translation - panel.Translation)).Should().BeGreaterThan(0.99f);
    }

    [Theory]
    [InlineData(0f, 0f, 0.5f, 0.5f)]       // center
    [InlineData(-0.25f, 0.15f, 0f, 0f)]    // top-left corner
    [InlineData(0.24f, -0.14f, 0.98f, 0.9667f)]
    public void Intersects_laser_with_panel_in_top_left_uv_space(float x, float y, float u, float v)
    {
        var panel = Matrix4x4.CreateTranslation(0, 1.5f, -1); // 0.5 × 0.3 m, facing +Z
        var origin = new Vector3(x, 1.5f + y, 0);

        var hit = VrMath.Intersect(panel, 0.5f, 0.3f, origin, -Vector3.UnitZ);

        hit.Should().NotBeNull();
        hit!.Value.Uv.X.Should().BeApproximately(u, 1e-3f);
        hit.Value.Uv.Y.Should().BeApproximately(v, 1e-3f);
        hit.Value.Distance.Should().BeApproximately(1, Eps);
    }

    [Fact]
    public void Ignores_misses_back_face_and_far_hits()
    {
        var panel = Matrix4x4.CreateTranslation(0, 0, -1);

        VrMath.Intersect(panel, 0.5f, 0.3f, new Vector3(1, 0, 0), -Vector3.UnitZ).Should().BeNull("outside the rectangle");
        VrMath.Intersect(panel, 0.5f, 0.3f, Vector3.Zero, Vector3.UnitZ).Should().BeNull("pointing away");
        VrMath.Intersect(panel, 0.5f, 0.3f, new Vector3(0, 0, -2), Vector3.UnitZ).Should().BeNull("behind the panel");
        VrMath.Intersect(Matrix4x4.CreateTranslation(0, 0, -10), 0.5f, 0.3f, Vector3.Zero, -Vector3.UnitZ).Should().BeNull("too far");
    }

    [Fact]
    public void Grab_offset_keeps_panel_rigidly_attached_to_the_hand()
    {
        var panel = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(0.2f, 1.4f, -0.6f);
        var handStart = Matrix4x4.CreateRotationX(0.5f) * Matrix4x4.CreateTranslation(0.1f, 1.2f, -0.3f);
        var offset = VrMath.RelativeTo(panel, handStart);

        // Move and rotate the hand: the panel follows exactly.
        var motion = Matrix4x4.CreateRotationY(1f) * Matrix4x4.CreateTranslation(0.5f, 0.1f, 0);
        var handNow = handStart * motion;
        var expected = panel * motion;

        var actual = offset * handNow;
        Vector3.Distance(actual.Translation, expected.Translation).Should().BeLessThan(Eps);
        (offset * handStart).Translation.Should().Match<Vector3>(t => Vector3.Distance(t, panel.Translation) < Eps);
    }

    [Fact]
    public void Procedural_textures_have_transparent_corners_and_opaque_center()
    {
        var button = ProceduralTextures.WristButton(64, highlighted: false);
        var cursor = ProceduralTextures.Cursor(32);

        button.Should().HaveCount(64 * 64 * 4);
        button[3].Should().Be(0, "corner alpha");
        button[((32 * 64) + 32) * 4 + 3].Should().BeGreaterThan(200, "center alpha");
        cursor[((16 * 32) + 16) * 4 + 3].Should().Be(255);
    }
}

public class TipOffsetTests
{
    [Fact]
    public void Derived_tip_points_like_the_measured_index_tip()
    {
        // Measured on Valve Index: tip forward in the raw controller frame ≈ (0.067, -0.643, -0.763).
        var forward = VrMath.Forward(VrMath.TipFromDevice(System.Numerics.Matrix4x4.Identity));

        System.Numerics.Vector3.Dot(forward, System.Numerics.Vector3.Normalize(new(0.067f, -0.643f, -0.763f))).Should().BeGreaterThan(0.99f);
    }
}

public class PixelConversionTests
{
    [Theory]
    [InlineData(8)]               // scalar tail only
    [InlineData(1650 * 1080 * 4)] // full panel: SIMD + parallel path
    public void Swaps_red_and_blue_exactly(int length)
    {
        var source = new byte[length];
        new Random(42).NextBytes(source);
        var destination = new byte[length];

        OverlayTexture.SwapRedBlue(source, destination); // warm-up (JIT, thread pool)
        var watch = System.Diagnostics.Stopwatch.StartNew();
        OverlayTexture.SwapRedBlue(source, destination);
        watch.Stop();

        for (var i = 0; i < length; i += 4)
        {
            if (destination[i] != source[i + 2] || destination[i + 1] != source[i + 1] || destination[i + 2] != source[i] || destination[i + 3] != source[i + 3])
            {
                Assert.Fail($"pixel {i / 4} wrong");
            }
        }

        if (length > 1_000_000)
        {
            // Generous bound: test assemblies run in parallel and compete for the cores.
            watch.Elapsed.TotalMilliseconds.Should().BeLessThan(25, "a full panel must convert in a few ms");
        }
    }
}

public class FaceTowardsTests
{
    [Theory]
    [InlineData(0.5f, 1.2f, -0.6f)]
    [InlineData(-0.8f, 2.0f, 0.3f)]
    [InlineData(0.0f, 0.5f, 1.0f)]
    public void Panel_faces_the_eye_and_stays_upright(float x, float y, float z)
    {
        var eye = new System.Numerics.Vector3(0, 1.6f, 0);
        var panel = VrMath.FaceTowards(new(x, y, z), eye);

        var normal = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(panel.M31, panel.M32, panel.M33));
        var toEye = System.Numerics.Vector3.Normalize(eye - panel.Translation);
        System.Numerics.Vector3.Dot(normal, toEye).Should().BeGreaterThan(0.999f, "visible face points at the eye");
        panel.M12.Should().BeApproximately(0, 1e-4f, "X axis horizontal: no roll");
    }
}
