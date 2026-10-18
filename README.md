# SE2VR: OpenXR VR for Space Engineers 2

An experimental plugin that renders Space Engineers 2 in a PC VR headset through OpenXR (SteamVR, Oculus/Meta, WMR,
Virtual Desktop, and others).

> Status: **early prototype**. The first goal is a stereo 3D, head-tracked view with the game's menus and HUD on a
> floating panel. Motion controllers are not supported yet. Keep playing with mouse and keyboard, or a gamepad.

## How it works

Space Engineers 2 has a built-in plugin hook. Starting the game with `-plugins:<path to dll>` loads any assembly that
contains a class implementing `Keen.VRage.Core.Plugins.IPlugin`. The engine (VRage 3) is written in C# on .NET 9 and
renders with DirectX 12, so a plugin can work directly with the renderer:

1. **Start-up.** Before the engine is created, the plugin connects to the active OpenXR runtime. It then forces the game
   window to the headset's per-eye resolution, with v-sync off.
2. **Session.** On the first rendered frame, an OpenXR session is created on the game's own D3D12 device and direct
   command queue (`XR_KHR_D3D12_enable`).
3. **Frames.** Each frame replaces `Render12EngineComponent.Draw`, using a Harmony prefix:
   - `xrWaitFrame` paces the game to the headset, and `xrLocateViews` provides the head pose.
   - The scene is drawn **twice, once per eye, in the same frame**. Each eye gets the game camera with the tracked eye
     pose applied on top, its own asymmetric projection, and its own temporal history.
   - Each eye's result is copied into an OpenXR swapchain image. Those images are wrapped in the engine's own
     `BackBuffer` type, so the renderer's resource-state tracking handles the barriers.
   - The game UI (menus, HUD, cursor) is rendered once into a separate image and shown as a **floating panel** (an
     OpenXR quad layer). It is not burned into both eyes.
   - GPU particles are simulated once per frame. The right eye reuses the left eye's simulation.
4. **Desktop window.** The window keeps showing the right eye as a mirror.

The game's built-in temporal upscaler (FSR) is switched to FXAA while the headset is active, because one temporal
history cannot serve two viewpoints.

## Requirements

- Space Engineers 2 (tested against game version 2.4.0.95).
- A PC VR headset with an OpenXR runtime set as active. For SteamVR: SteamVR Settings > OpenXR > "Set SteamVR as
  OpenXR runtime".
- .NET SDK 9 or newer, to build.
- A strong GPU. Rendering SE2 twice per frame at headset resolution is heavy. Lower `RenderScale` (see below) and the
  game's graphics preset until it is smooth.

## Build and install

```powershell
.\install.ps1
```

This builds the plugin and copies it to `%LOCALAPPDATA%\SE2VR`. It then prints the Steam launch option to use:

```
-plugins:C:\Users\<you>\AppData\Local\SE2VR\SE2VR.dll
```

Paste that line into Steam > Space Engineers 2 > Properties > General > Launch Options. Then:

1. Start SteamVR with the headset connected.
2. Start the game from Steam.

`.\install.ps1 -Launch` does the install and launches the game in one step. If the game is not in the default Steam
library, pass `-GameDir "D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2"`.

To play without VR, remove the launch option, or set `"Enabled": false` in `SE2VR.json`.

## Settings (`%LOCALAPPDATA%\SE2VR\SE2VR.json`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `RenderScale` | `0.75` | Per-eye resolution relative to the runtime's recommendation. SteamVR's per-app resolution slider applies on top of this. |
| `WorldScale` | `1.0` | Scales head movement and eye separation. Values above 1 make the world feel smaller. |
| `UiDistance` | `1.6` | Distance of the menu/HUD panel, in meters. |
| `UiWidth` | `1.6` | Width of the menu/HUD panel, in meters. |
| `ForceWindowedResolution` | `true` | Runs the game in a window sized to one eye, so every rendered pixel reaches the headset. |
| `LockResolution` | `true` | Ignores display-mode changes from the game's options menu while VR is active. They are still saved for non-VR play. |
| `RecenterKey` | `Home` | Makes your current head position and heading the new "forward". Accepts names like `Home`, `F8` or `Ctrl+R`. Empty disables it. |
| `CaptureKey` | `Ctrl+Shift+F10` | Saves the images sent to the headset (both eyes and the UI panel) as PNGs in the `captures` folder. |
| `DisableFsr` | `true` | Uses FXAA instead of FSR while in VR. |
| `SystemWaitSeconds` | `15` | How long to wait at start-up for the headset to appear. |
| `VerboseLogging` | `false` | Writes more detail to the log. |

Settings are read once, at game start.

## Using it

- **Recenter:** the view recenters automatically when the session starts and when a world loads. Press `Home` (the
  `RecenterKey` setting), or use SteamVR's "Reset seated position", to recenter at any time.
- **Debug capture:** press `Ctrl+Shift+F10`, or create an empty file named `capture` in the plugin folder, to save
  what the headset receives into `captures\`.
- **Steering:** the mouse or gamepad still turns the character and aims tools. The headset adds free head movement on
  top. The crosshair stays in the center of the floating HUD panel.
- **Log:** everything the plugin does is written to `%LOCALAPPDATA%\SE2VR\SE2VR.log`, and mirrored into the game log
  with an `[SE2VR]` prefix.

## Known limitations

- No motion controllers or VR interaction yet.
- Screen-space effects (SSR, ambient occlusion, clouds) keep one history shared by both eyes. They may shimmer
  slightly.
- Water splash particles still simulate once per eye.
- Minimizing the game window stops scene rendering.
- Game updates can rename or change the renderer internals this plugin patches. When that happens, the plugin logs the
  failed patch and the game runs normally without VR.

## Prior art

[JakariaMods/SE2VR](https://github.com/JakariaMods/SE2VR) is an unofficial OpenVR plugin by a Keen developer. It
alternates eyes between frames by default. This project is an independent OpenXR implementation and shares no code
with it.

## Development notes

- `Directory.Build.props` points at the game's `Game2` folder. Override it with `SE2_GAME_DIR` or
  `/p:SE2GameDir=...`.
- Renderer internals are accessed through [Krafs.Publicizer](https://github.com/krafs/Publicizer). Patching uses
  [Harmony](https://github.com/pardeike/Harmony).
- The OpenXR bindings in `src/SE2VR/OpenXR` are hand-written. The plugin locates the active runtime the same way the
  Khronos loader does. To use the official loader instead (for example, to enable OpenXR API layers), drop
  `openxr_loader.dll` next to `SE2VR.dll`.
- Never commit decompiled game code.
