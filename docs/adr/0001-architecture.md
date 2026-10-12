# ADR 0001 — Overall architecture

- Status: accepted
- Date: 2026-10-12

## Context

AvatarDeck replaces VRChat's radial (R) menu for users whose controller sticks drift. It must render a
floating tablet inside SteamVR, be fully usable with a laser pointer + trigger, drive avatar parameters
over OSC, and stay very light (< 100 MB RAM, < 1 % CPU idle, 90/120 Hz while interacting).

## Decisions

### Overlay technology: OpenVR overlays (SteamVR), not an OpenXR overlay
OpenXR has no cross-application overlay API that SteamVR implements (`XR_EXTX_overlay` is unsupported).
VRChat — whether launched in OpenVR or OpenXR mode on PC — runs on the SteamVR runtime, and `IVROverlay`
overlays are composited on top of it. This covers Index, Vive, WMR and Quest via Link/Air Link/Virtual
Desktop→SteamVR. Standalone Quest is out of scope (no overlay tool can target it).

### UI: Avalonia 11 + built-in Fluent theme, rendered offscreen for VR
One `MainView` is used by both the desktop window and the VR overlay. For VR the view is hosted in an
offscreen `TopLevel`, rendered by Skia to a bitmap **only when invalidated**, copied to a D3D11 texture and
submitted with `SetOverlayTexture`. Idle = zero frames = ~0 % CPU. `tools/UiSnapshot` already exercises this
offscreen path to produce screenshots.

*Deviation from the initial plan:* Avalonia's own `FluentTheme` is used instead of FluentAvalonia — it
provides the same Fluent controls with fewer dependencies and less memory. FluentAvalonia can be added later
if its extra controls (NavigationView, InfoBar) become necessary.

Glassmorphism: real backdrop blur is impossible inside an overlay texture, so "glass" is emulated with
translucent gradients and light borders (`Themes/Glass.axaml`), which looks identical on desktop and in VR.

### OSC: in-house codec
`OscCodec` implements the OSC 1.0 subset VRChat uses (i, f, s, T, F, N, nested bundles) with span-based,
allocation-free encoding. Existing libraries are either Unity-centric (OscCore) or allocate per message.
`VrcOscClient` separates **discrete** sends (`SendAsync`, ordered — toggles, buttons, presets) from
**continuous** sends (`Post`, latest-wins coalescing — sliders), so dragging a slider never floods VRChat
and a button's press/release pair is never merged.

Two sockets are used (send / receive) and `SIO_UDP_CONNRESET` is disabled, otherwise Windows reports ICMP
"port unreachable" (VRChat closed) as receive errors.

### Automatic profiles without Unity
VRChat writes `%LocalAppData%Low\VRChat\VRChat\OSC\usr_*\Avatars\avtr_*.json` with the typed list of every
parameter. On `/avatar/change`, `ProfileManager` loads `avatars/{id}.json` or generates it from that file
(`ProfileBuilder`: bools → toggles, floats → sliders, ints → stepped sliders, `A/B` names → folders,
built-ins filtered). Existing profiles are merged non-destructively when new parameters appear.
The Unity importer (phase 7) refines this with menu structure, labels and dropdown values.

### Layering and dependency rule

```
Core  ←  Osc, Infrastructure, UnityImport, VR, Integrations, UI  ←  App (composition root)
```

- **Core**: models, value types, interfaces, pure services. No I/O, no UI, trimmable/AOT-safe.
- **UI** knows only Core interfaces — never sockets or OpenVR — so it is testable and renderable headless.
- **App** is the only project that wires concrete implementations (Microsoft.Extensions.Hosting).

### Persistence
`System.Text.Json` with source generation (`AvatarDeckJsonContext`): no reflection, trim/AOT safe.
Widgets are polymorphic (`"kind": "toggle"`). `OscValue` is written as a plain JSON primitive
(`true`, `3`, `0.75`, floats always with a decimal point so `1.0` is never read back as an int).
Writes are atomic (temp file + replace); corrupt files are backed up, never overwritten. `schemaVersion`
drives migrations.

### Threading
OSC receive and the coalescing flush run on the thread pool; VRChat events are marshalled to the UI thread
with `Dispatcher.UIThread.Post`. Remote values are applied with `ApplyRemoteValue`, which suppresses
re-sending (no feedback loops). `async void` is restricted to event handlers that catch everything.

## Consequences

- Windows + SteamVR only (acceptable: VRChat PC VR is Windows-only in practice).
- Debug builds measure ~150 MB working set; reaching < 100 MB requires Release, trimming and disabling the
  desktop window's acrylic when in VR-only mode (phase 9).
