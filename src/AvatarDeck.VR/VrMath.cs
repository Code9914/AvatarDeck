using System.Numerics;
using Valve.VR;

namespace AvatarDeck.VR;

/// <summary>
/// Pure geometry used by the overlay: conversions between OpenVR and System.Numerics, panel placement,
/// laser/panel intersection and grab maths. No OpenVR calls here, so everything is unit-testable.
/// </summary>
/// <remarks>
/// OpenVR matrices are row-major 3×4 with column vectors (<c>p' = M·p</c>); System.Numerics uses row vectors
/// (<c>p' = p·M</c>), so conversion is a transpose. Composition reads left-to-right: <c>local * parent</c>.
/// OpenVR is right-handed, -Z forward, +Y up; an overlay's visible face points toward its +Z axis.
/// </remarks>
public static class VrMath
{
    /// <summary>Maximum laser length in meters.</summary>
    public const float MaxRayLength = 6f;

    public static Matrix4x4 ToMatrix(in HmdMatrix34_t m) => new(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    public static HmdMatrix34_t ToHmd(in Matrix4x4 m) => new()
    {
        m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = m.M41,
        m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43,
    };

    /// <summary>Serialises a pose as 12 floats in OpenVR row-major order (for settings).</summary>
    public static float[] ToArray(in Matrix4x4 m) =>
        [m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43];

    public static Matrix4x4? FromArray(float[]? a) => a is { Length: 12 }
        ? new Matrix4x4(a[0], a[4], a[8], 0, a[1], a[5], a[9], 0, a[2], a[6], a[10], 0, a[3], a[7], a[11], 1)
        : null;

    /// <summary>Forward direction (-Z axis) of a pose.</summary>
    public static Vector3 Forward(in Matrix4x4 pose) => -new Vector3(pose.M31, pose.M32, pose.M33);

    /// <summary>
    /// Places a panel in front of the head: <paramref name="distance"/> meters ahead (horizontally),
    /// <paramref name="drop"/> meters below eye level, upright, tilted to face the eyes.
    /// </summary>
    public static Matrix4x4 PlaceInFront(in Matrix4x4 head, float distance = 0.55f, float drop = 0.12f)
    {
        var forward = Forward(head);
        forward.Y = 0;
        forward = forward.LengthSquared() < 1e-4f ? -Vector3.UnitZ : Vector3.Normalize(forward);

        var center = head.Translation + (forward * distance) - new Vector3(0, drop, 0);

        // CreateWorld sets Z = -forward, i.e. the visible face points back at the user.
        var upright = Matrix4x4.CreateWorld(center, forward, Vector3.UnitY);

        // Tilt so the panel normal points at the eyes (rotation about the panel's own X axis).
        var tilt = MathF.Atan2(drop, distance);
        return Matrix4x4.CreateRotationX(-tilt) * upright;
    }

    /// <summary>
    /// Pose at <paramref name="position"/> whose visible face (+Z) points at <paramref name="eye"/>, kept upright
    /// (its X axis stays horizontal, so the panel never rolls).
    /// </summary>
    public static Matrix4x4 FaceTowards(Vector3 position, Vector3 eye)
    {
        var away = position - eye; // CreateWorld: Z = -forward, so forward = away from the eye
        if (away.LengthSquared() < 1e-6f)
        {
            return Matrix4x4.CreateTranslation(position);
        }

        away = Vector3.Normalize(away);
        if (MathF.Abs(away.Y) > 0.98f)
        {
            away = Vector3.Normalize(new Vector3(away.X, MathF.Sign(away.Y) * 0.98f, away.Z + 1e-3f)); // avoid gimbal at the pole
        }

        return Matrix4x4.CreateWorld(position, away, Vector3.UnitY);
    }

    /// <summary>
    /// Intersects a ray with a panel (a <paramref name="width"/>×<paramref name="height"/> rectangle centred
    /// on the pose origin, in its XY plane). Only the visible (+Z) face counts.
    /// </summary>
    /// <returns>Normalised coordinates (0,0 = top-left … 1,1 = bottom-right) and distance, or null.</returns>
    public static (Vector2 Uv, float Distance, Vector3 LocalHit)? Intersect(
        in Matrix4x4 panel, float width, float height, Vector3 origin, Vector3 direction)
    {
        if (!Matrix4x4.Invert(panel, out var inverse))
        {
            return null;
        }

        var o = Vector3.Transform(origin, inverse);
        var d = Vector3.TransformNormal(direction, inverse);
        if (d.Z >= -1e-6f || o.Z <= 0)
        {
            return null; // parallel, pointing away, or behind the panel
        }

        var t = -o.Z / d.Z;
        var hit = o + (d * t);
        var u = (hit.X / width) + 0.5f;
        var v = 0.5f - (hit.Y / height);
        if (u is < 0 or > 1 || v is < 0 or > 1)
        {
            return null;
        }

        var worldDistance = Vector3.Distance(origin, Vector3.Transform(hit, panel));
        return worldDistance > MaxRayLength ? null : (new Vector2(u, v), worldDistance, hit);
    }

    /// <summary>Offset that keeps an object rigidly attached to a moving parent: <c>object = offset * parent</c>.</summary>
    public static Matrix4x4 RelativeTo(in Matrix4x4 obj, in Matrix4x4 parent) =>
        Matrix4x4.Invert(parent, out var inverse) ? obj * inverse : Matrix4x4.Identity;

    /// <summary>Distance between two poses' origins.</summary>
    public static float Distance(in Matrix4x4 a, in Matrix4x4 b) => Vector3.Distance(a.Translation, b.Translation);

    /// <summary>
    /// Tip (aim) pose relative to the raw controller pose, used when SteamVR does not deliver the tip pose action
    /// (it is intermittent for overlay apps). Measured on Valve Index controllers: the tip points 40° below the raw
    /// -Z axis, ~2 cm forward. Close enough for Touch/Vive, whose tips are tilted similarly.
    /// </summary>
    public static Matrix4x4 TipOffset { get; set; } =
        Matrix4x4.CreateRotationX(-40f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0, -0.015f, 0.02f);

    /// <summary>Aim pose derived from the raw controller pose.</summary>
    public static Matrix4x4 TipFromDevice(in Matrix4x4 device) => TipOffset * device;

    /// <summary>Default pose of a panel held in a hand, like a tablet (relative to the controller's raw pose).</summary>
    public static Matrix4x4 DefaultHandOffset(bool leftHand)
    {
        // Lay the panel along the forearm-facing side, tilted toward the face, slightly in front of the hand.
        var side = leftHand ? 1f : -1f;
        return Matrix4x4.CreateRotationX(-MathF.PI / 3f)
            * Matrix4x4.CreateRotationY(side * -MathF.PI / 10f)
            * Matrix4x4.CreateTranslation(side * 0.02f, 0.12f, 0.02f);
    }

    /// <summary>
    /// Default pose of the wrist launcher button relative to the controller's raw pose: lying on the back of the hand,
    /// facing up and slightly outward. Expressed in the tip frame (natural hand orientation), then mapped to raw.
    /// Users can grab it to place it exactly where they want.
    /// </summary>
    public static Matrix4x4 DefaultWristButtonOffset(bool leftHand)
    {
        var outward = leftHand ? -1f : 1f; // the back of the hand is on the outer side
        return Matrix4x4.CreateRotationX(-MathF.PI / 2)                    // face up
            * Matrix4x4.CreateRotationZ(-outward * 40f * MathF.PI / 180f)  // tilt toward the outer side
            * Matrix4x4.CreateTranslation(outward * 0.035f, 0.02f, 0.10f)  // over the hand, 10 cm behind the tip
            * TipOffset;
    }
}
