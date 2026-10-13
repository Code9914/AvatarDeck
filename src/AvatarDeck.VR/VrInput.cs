using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Valve.VR;

namespace AvatarDeck.VR;

/// <summary>State of one hand for the current frame.</summary>
public struct HandState
{
    /// <summary>Aim pose (laser origin and direction), valid when <see cref="HasPose"/>.</summary>
    public Matrix4x4 Aim;

    /// <summary>Raw controller pose (used to attach overlays to the hand and for dragging).</summary>
    public Matrix4x4 Device;

    public uint DeviceIndex;

    public bool HasPose;

    /// <summary>True when the aim comes from SteamVR's "tip" pose (otherwise derived from the raw pose).</summary>
    public bool HasTipPose;

    /// <summary>Analog trigger travel, 0–1.</summary>
    public float Trigger;

    /// <summary>Trigger held past the click threshold.</summary>
    public bool Click;

    public bool ClickPressed;

    public bool ClickReleased;

    public bool TogglePressed;
}

/// <summary>
/// SteamVR Input (action manifest in <c>input/actions.json</c>): trigger (analog), show/hide toggle and the aim pose
/// per hand. Everything is driven by the trigger — the grip is left entirely to the game, so grabbing things in
/// VRChat never moves the panel. Overlay apps share controller input with the game.
/// </summary>
public sealed class VrInput(ILogger logger)
{
    /// <summary>Trigger travel that counts as a click, and the release point (hysteresis avoids chatter).</summary>
    private const float ClickPressThreshold = 0.6f;
    private const float ClickReleaseThreshold = 0.4f;

    private static readonly uint DigitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
    private static readonly uint AnalogSize = (uint)Marshal.SizeOf<InputAnalogActionData_t>();
    private static readonly uint PoseSize = (uint)Marshal.SizeOf<InputPoseActionData_t>();
    private static readonly uint ActiveSetSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();

    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private readonly VRActiveActionSet_t[] _activeSets = new VRActiveActionSet_t[1];
    private ulong _set;
    private ulong _pointer;
    private ulong _click;
    private ulong _toggle;
    private ulong _leftSource;
    private ulong _rightSource;
    private EVRInputError _lastError;

    public HandState Left;

    public HandState Right;

    /// <summary>Head pose, valid when <see cref="HasHead"/>.</summary>
    public Matrix4x4 Head;

    public bool HasHead;

    /// <summary>True when the action manifest was accepted by SteamVR.</summary>
    public bool IsReady { get; private set; }

    public void Initialize()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "input", "actions.json");
        var input = OpenVR.Input;
        var error = input.SetActionManifestPath(manifest);
        error = error == EVRInputError.None ? input.GetActionSetHandle("/actions/avatardeck", ref _set) : error;
        error = error == EVRInputError.None ? input.GetActionHandle("/actions/avatardeck/in/pointer", ref _pointer) : error;
        error = error == EVRInputError.None ? input.GetActionHandle("/actions/avatardeck/in/click", ref _click) : error;
        error = error == EVRInputError.None ? input.GetActionHandle("/actions/avatardeck/in/toggle", ref _toggle) : error;
        error = error == EVRInputError.None ? input.GetInputSourceHandle("/user/hand/left", ref _leftSource) : error;
        error = error == EVRInputError.None ? input.GetInputSourceHandle("/user/hand/right", ref _rightSource) : error;

        IsReady = error == EVRInputError.None;
        if (!IsReady)
        {
            logger.LogError("SteamVR input initialisation failed: {Error} (manifest {Manifest})", error, manifest);
        }

        _activeSets[0] = new VRActiveActionSet_t { ulActionSet = _set };
    }

    /// <summary>Reads every action and pose for this frame.</summary>
    public void Update()
    {
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);
        HasHead = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd].bPoseIsValid;
        if (HasHead)
        {
            Head = VrMath.ToMatrix(_poses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking);
        }

        if (!IsReady)
        {
            return;
        }

        OpenVR.Input.UpdateActionState(_activeSets, ActiveSetSize);
        ReadHand(ref Left, _leftSource, ETrackedControllerRole.LeftHand);
        ReadHand(ref Right, _rightSource, ETrackedControllerRole.RightHand);
    }

    private void ReadHand(ref HandState hand, ulong source, ETrackedControllerRole role)
    {
        var input = OpenVR.Input;
        hand.DeviceIndex = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
        hand.HasPose = hand.DeviceIndex < _poses.Length && _poses[hand.DeviceIndex].bPoseIsValid;
        if (hand.HasPose)
        {
            // The raw controller pose is always available; the "tip" pose action is not reliably delivered to overlay
            // apps. Aim from the tip when SteamVR provides it, otherwise from the raw pose with the measured tip offset.
            hand.Device = VrMath.ToMatrix(_poses[hand.DeviceIndex].mDeviceToAbsoluteTracking);
            var pose = default(InputPoseActionData_t);
            Check(input.GetPoseActionDataForNextFrame(_pointer, ETrackingUniverseOrigin.TrackingUniverseStanding, ref pose, PoseSize, source));
            hand.HasTipPose = pose.bActive && pose.pose.bPoseIsValid;
            hand.Aim = hand.HasTipPose ? VrMath.ToMatrix(pose.pose.mDeviceToAbsoluteTracking) : VrMath.TipFromDevice(hand.Device);
        }

        // Trigger as an analog value: more reliable than the "click" input, whose end-of-travel detent does not fire
        // on every controller/configuration.
        var analog = default(InputAnalogActionData_t);
        hand.Trigger = Check(input.GetAnalogActionData(_click, ref analog, AnalogSize, source)) && analog.bActive ? analog.x : 0f;
        var wasDown = hand.Click;
        hand.Click = wasDown ? hand.Trigger > ClickReleaseThreshold : hand.Trigger > ClickPressThreshold;
        hand.ClickPressed = hand.Click && !wasDown;
        hand.ClickReleased = !hand.Click && wasDown;

        var digital = default(InputDigitalActionData_t);
        hand.TogglePressed = Check(input.GetDigitalActionData(_toggle, ref digital, DigitalSize, source))
            && digital.bActive && digital.bChanged && digital.bState;
    }

    /// <summary>Logs each distinct input error once; returns true on success.</summary>
    private bool Check(EVRInputError error)
    {
        if (error != EVRInputError.None && error != _lastError)
        {
            logger.LogWarning("SteamVR input error: {Error}", error);
        }

        _lastError = error;
        return error == EVRInputError.None;
    }
}
