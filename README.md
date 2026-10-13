# AvatarDeck

A floating VR tablet for SteamVR that controls VRChat avatar parameters over OSC —
a full replacement for the radial (R) menu, built for people whose controller sticks drift.
Point, click, done: no joystick needed.

![Favorites](docs/screenshots/01-favorites.png)

## Status

| Phase | Content | State |
|---|---|---|
| 1 | Solution skeleton, DI host, logging, dark glass theme | ✅ |
| 2 | OSC codec, UDP transport, VRChat client, avatar change detection, full-state sync (OSCQuery) | ✅ |
| 3 | Domain model, JSON profiles, auto-generation from VRChat OSC config | ✅ |
| 4 | Tablet UI: tabs, folders/pages, search, favorites, presets, toasts | ✅ first version |
| 5 | SteamVR overlay (offscreen render → D3D11 → `IVROverlay`) | ✅ needs in-headset validation |
| 6 | Laser pointer, trigger click, drag-scroll, grab/move/resize, lock, opacity, wrist button | ✅ needs in-headset validation |
| 7 | Unity project importer (Expression Parameters / Menus) | ⏳ |
| 8 | Macros, hotkeys, local API, MIDI, Stream Deck, plugins, themes, import/export | ⏳ |
| 9 | Footprint tuning (< 100 MB, < 1 % CPU idle), installer, SteamVR auto-start | ⏳ |

## Build & run

Requirements: Windows 10/11, .NET 8 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/AvatarDeck.App
```

In VRChat, enable OSC (Action menu → Options → OSC → Enabled). AvatarDeck sends to `127.0.0.1:9000`
and listens on `127.0.0.1:9001` (configurable in `appsettings.json` or `%AppData%\AvatarDeck\settings.json`).

### Developing without VRChat

```bash
# once: create an isolated data folder with the demo profile
mkdir -p .devdata/avatars && cp samples/avatars/avtr_demo.json .devdata/avatars/
# terminal 1 — the app
dotnet run --project src/AvatarDeck.App -- --data-dir ./.devdata
# terminal 2 — a fake VRChat: prints what AvatarDeck sends, echoes it back, switches avatar
dotnet run --project tools/OscSniffer -- --avatar avtr_demo
```

`tools/UiSnapshot` renders the UI offscreen to PNG (`docs/screenshots`).

## VR panel (SteamVR)

Start SteamVR, then AvatarDeck (any order — it waits for SteamVR and never launches it). The panel works with
VRChat in OpenVR or OpenXR mode on PC (both run on the SteamVR runtime).

| Action | Default (Index) — trigger only, the grip stays free for the game |
|---|---|
| Show / hide | Trigger tap on the wrist button (left hand), double-press left **B**, or "VR panel" on the desktop |
| Click | Point with either controller, **trigger** (the aim is frozen as the trigger starts moving, plus a small dead zone) |
| Scroll | Trigger held + drag up/down (no thumbstick) |
| Move | Trigger held on the **handle** under the panel — the panel keeps facing you |
| Resize | While moving, pull the other hand's trigger and spread / bring hands together |
| Move the wrist button | Trigger held on it + move (position remembered) |

"VR panel" settings: attach to the world or a hand, opacity, size, lock, wrist button, recenter.
Bindings can be changed in SteamVR › Settings › Controllers › Manage controller bindings › AvatarDeck.

How it works: the same `MainView` is hosted in a hidden window, rendered to pixels only when something changes,
and uploaded with `IVROverlay::SetOverlayRaw` into two stacked overlays used alternately (the back one stays
visible at 2 % opacity, because SteamVR defers updates of hidden overlays) — shared D3D11 textures were tried and
did not refresh reliably in the headset. The laser is AvatarDeck's own (ray/panel intersection, aim from the
controller pose with the measured Index tip offset), injected as Win32 mouse messages — so the game keeps all
controller input. Int avatar parameters are shown as − value + steppers (range editable).
OpenVR bindings: Valve's official `openvr_api.cs` (vendored, BSD-3); `openvr_api.dll` is loaded from SteamVR.

Diagnostics:
- `--vr-selftest <out>` (with `--data-dir <dir> --Osc:SendPort=59999`): renders the VR panel without SteamVR, injects
  laser hover / clicks / tab changes / 90 Hz drags, checks frame production and saves frames.
- `--vr-diag <out>`: with SteamVR running, simulates laser clicks every 4 s and reads back the compositor's copy of the
  panel (`GetOverlayTexture`) to check what the headset is given.
- `--show-vr` opens the panel at start, `--verbose` logs input, render and upload timings.

## Profiles

One JSON file per avatar in `%AppData%\AvatarDeck\avatars\{avatarId}.json`
(see [samples/avatars/avtr_demo.json](samples/avatars/avtr_demo.json)):

```jsonc
{
  "avatarId": "avtr_…",
  "folders": [
    { "name": "Clothes", "widgets": [
        { "kind": "toggle", "label": "Hoodie", "parameter": "Hoodie", "parameterType": "Bool", "onValue": true, "offValue": false },
        { "kind": "slider", "label": "Hair Hue", "parameter": "HairHue", "parameterType": "Float", "minimum": 0, "maximum": 1 }
    ], "children": [] }
  ],
  "presets": [ { "name": "Casual", "values": [ { "parameter": "HairHue", "value": 0.75 } ] } ],
  "favorites": [ "…widget id…" ]
}
```

When VRChat reports a new avatar, AvatarDeck loads its profile, or generates one automatically from the
live parameter list (OSCQuery) or the OSC config VRChat writes in `%LocalAppData%Low\VRChat\VRChat\OSC`.

Auto-organisation: `Folder/Name` paths become folders (VRCFury `VF123_` prefixes removed), flat VRCFury prefab
parameters go to "Extras", VRCFury internals (local copies, compressor, version markers) are dropped, and
GoGo Loco / tracking / smoothing parameters go to a hidden "System" folder. Inside a page, controls are grouped
into sections by their first word (Hair → Hue, Swap).

**Edit mode** (pencil, top right): click any control to rename it, change its type (toggle / button / slider),
hide it, reorder it or move it to another folder; edit folders (name, icon, color, visibility, order, pages);
"Auto-organize" rebuilds the layout from scratch (favorites and presets are kept). Everything is laser-friendly.

## Architecture

```
src/
  AvatarDeck.Core            domain: models, OscValue, interfaces, ProfileBuilder/Manager, presets
  AvatarDeck.Osc             OSC 1.0 codec, UDP transport, VrcOscClient (coalescing, feedback, avatar change)
  AvatarDeck.Infrastructure  JSON profile store (atomic, source-generated), VRChat OSC config reader, paths
  AvatarDeck.UI              Avalonia views + MVVM view models, Glass theme, off-screen VR surface
  AvatarDeck.VR              SteamVR overlay service: overlays, laser, grab/resize, input actions, D3D11 upload
  AvatarDeck.VR.OpenVR       Valve's official OpenVR C# bindings (vendored, unmodified)
  AvatarDeck.App             composition root: generic host, DI, Serilog, Avalonia startup
tests/                       xUnit: codec, loopback UDP client, persistence, profile generation
tools/OscSniffer             fake VRChat for development
tools/UiSnapshot             headless offscreen renderer → PNG
docs/adr/                    architecture decision records
```

Design decisions are recorded in [docs/adr](docs/adr/0001-architecture.md).
