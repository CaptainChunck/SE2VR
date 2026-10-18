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
     pose applied on top, its own asymmetric projection, and its own temporal history: the engine's temporal effects
     (clouds, water, stochastic transparency, raytraced GI) reproject last frame's result with the current
     projection, so the right eye gets a second set of history buffers, swapped in around its draw.
   - Each eye's result is copied into an OpenXR swapchain image. Those images are wrapped in the engine's own
     `BackBuffer` type, so the renderer's resource-state tracking handles the barriers.
   - The game UI (menus and HUD) is rendered once into a separate image and shown as a **floating panel** in front
     of you (an OpenXR quad layer, or a curved cylinder layer where the runtime supports it). It is not burned into
     both eyes.
   - Eye-independent work is done once per frame: GPU particle simulation, sun and character shadow refreshes,
     reflection probe updates and planet cloud shadows. The engine schedules some of these per scene draw, so drawing
     two eyes would otherwise run them twice as often as on the monitor.
4. **UI layout.** The VR window is nearly square, like the eye images, which would stretch the game's UI over a tall
   1920x1920 canvas. The plugin tells the UI the screen is 16:9 instead (the top part of the window), so menus and the
   HUD keep the layout they were designed for and the panel can be made wide enough to read. Mouse input needs no
   change, because the canvas keeps the window's origin and pixel scale.
5. **Mouse pointer.** The game's pointer is the Windows cursor, which only exists on the monitor. Whenever Windows
   shows it over the game window, the plugin draws its own pointer on the panel, at the position the game's UI uses.
6. **World markers.** GPS and antenna markers are projected with the panel's own frustum, so they sit over their
   objects in the headset.
7. **Desktop window.** The window keeps showing the right eye as a mirror. The plugin keeps the player's saved display
   settings unchanged, so the VR window size never ends up in the game's options.

The game's temporal anti-aliasing and upscaler (FSR; version 3.1 on NVIDIA and older AMD cards) keeps working in VR:
the right eye gets its own FSR context, so each eye's history only ever sees that eye. Without temporal anti-aliasing
the game's dithering and fine detail shimmer badly in a headset. Ray tracing is switched off while the headset is
active (see `RaytracingInVR`), because per eye it costs about as much as the rest of the frame and its buffers would
be needed twice.

### VR rendering optimizations

All of these are runtime-only changes to the renderer (no game files are touched), and each has a setting:

- **Dynamic resolution** (`DynamicResolution`). The frame's GPU time is measured by the engine itself. When a frame
  would miss the headset's budget (90% of one refresh at full rate, of two at half rate), the resolution FSR upscales
  from goes down a little; when there is room it goes back up to the graphics preset's own resolution. FSR is made for
  a changing input size, so this shows as a slightly softer picture instead of the judder of dropping from 45 to 30
  fps. On an RTX 3050 with the Extreme preset it doubled the frame rate (18 to 38 fps).
- **Foveated rendering** (`Foveation`). Variable rate shading (D3D12 tier 2: NVIDIA RTX / GTX 16, AMD RX 6000 or
  newer) shades the lighting passes, transparents and particles at one color per 2x2 pixels towards the edges of
  the view, around each eye's own lens center, and at 4x4 outside the area the lenses show (from the runtime's
  visibility mask). Geometry, textures and edges stay per pixel. The engine's deferred texturing writes triangle IDs
  in its G-buffer pass, which must stay exact, so the G-buffer is left alone.
- **Shared eye adaptation**. The right eye uses the exposure the left eye's eye adaptation just computed. Run per
  eye, it adapted twice as fast and left the eyes a step apart in brightness.
- **VR post-processing** (`VrPostEffects`, `LensFlaresInVR`). Lens dirt is off (it is glued to the screen, so it moves
  with your head), bloom runs at half resolution without its anti-flicker pass (FSR keeps it steady), and lens flares
  are off: their ghosts are flat screen effects, and the second eye overwrote the first eye's flare occlusion results.

### VR settings in the game

The game's **Settings** screen gets a **VR** tab with every setting above, applied immediately and saved to
`SE2VR.json`, plus **MODIFY UI**, **RECENTER VIEW** and **RESTORE** (back to the defaults). MODIFY UI lets you take
parts of the HUD off the menu screen - suit status, toolbar, ship status, objectives, notifications, warnings - and
place each one around your view, where it follows your head: left/right and up/down in degrees, distance and size.
A part is a piece of the UI image the plugin already renders, shown by the VR compositor as its own head-locked
panel, so it costs no extra rendering. Parts follow your head only while you play (the game hides the mouse pointer);
when a menu is open they go back on the menu screen. While MODIFY UI is open (from the pause menu or the main menu)
the headset shows every part that follows your head in a see-through frame, the chosen part in blue, with a picture of
the HUD from the last moment you played (kept every few frames during play, as menus have no HUD), so every change
shows right away. Moving a part switches "Follows your head" on.

## Requirements

- Space Engineers 2 (tested against game version 2.4.0.95).
- A PC VR headset with an OpenXR runtime set as active. For SteamVR: SteamVR Settings > OpenXR > "Set SteamVR as
  OpenXR runtime".
- To build from source only: .NET SDK 9 or newer.
- A strong GPU. Rendering SE2 twice per frame at headset resolution is heavy. Lower `RenderScale` (see below) and the
  game's graphics preset until it is smooth. With 8 GB of video memory use the Low or Medium preset in VR: High and
  Extreme run out of video memory with two eyes and can crash the graphics driver ("device removed").

## Install (players)

1. Download `SE2VR-v<version>.zip` from the repository's Releases page and extract it anywhere.
2. Double-click `Install.cmd`. It copies the mod to `%LOCALAPPDATA%\SE2VR`, creates a **Space Engineers 2 VR** shortcut
   on the desktop and copies the Steam launch option to the clipboard. Nothing in the game folder is changed.
3. Connect the headset and double-click **Space Engineers 2 VR**. Steam must be running; the shortcut starts SteamVR
   first if it is your OpenXR runtime and not running yet.

The shortcut starts the game directly with the plugin (it tells Steamworks the app id through the `SteamAppId`
environment variable), so Steam's own Play button keeps starting the normal game. If you prefer starting from Steam,
paste the launch option from the clipboard into Steam > Space Engineers 2 > Properties > General > Launch Options.
`Uninstall.cmd` removes the mod again. No .NET SDK is needed; the game brings the .NET runtime.

The plugin never saves the VR window size into your game settings: full screen, resolution and v-sync stay as they
were for playing without VR.

## Build from source

```powershell
.\install.ps1            # build and install to %LOCALAPPDATA%\SE2VR
.\tools\Package.ps1      # build the player zip in dist\
```

`install.ps1` builds the plugin and copies it to `%LOCALAPPDATA%\SE2VR`. It then prints the Steam launch option to use:

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
| `UiDistance` | `1.6` | Distance of the menu/HUD panel from your seated head position, in meters. |
| `UiWidthDegrees` | `70` | How much of your view the panel covers horizontally. Raise it if text is too small to read; lower it if you have to turn your head to see the edges. |
| `UiCurved` | `true` | Curves the panel around you so every part is the same distance away. Needs an OpenXR runtime with cylinder layers (`XR_KHR_composition_layer_cylinder`). SteamVR 2.x does not offer them, so it shows a flat panel; the log says which one is used. |
| `UiAspectRatio` | `1.7778` | Shape of the UI canvas. The game's UI is designed for 16:9. |
| `ShowCursor` | `true` | Shows the mouse pointer on the panel. |
| `ForceWindowedResolution` | `true` | Runs the game in a window sized to one eye, so every rendered pixel reaches the headset. |
| `LockResolution` | `true` | Ignores display-mode changes from the game's options menu while VR is active. They are still saved for non-VR play. |
| `UiBiggerKey` / `UiSmallerKey` | `Ctrl+PageUp` / `Ctrl+PageDown` | Make the panel 5 degrees wider or narrower while playing. The new size is saved to `UiWidthDegrees`. |
| `RecenterKey` | `Home` | Makes your current head position and heading the new "forward". Accepts names like `Home`, `F8` or `Ctrl+R`. Empty disables it. |
| `CaptureKey` | `Ctrl+Shift+F10` | Saves the images sent to the headset (both eyes and the UI panel) as PNGs in the `captures` folder. |
| `ShareWorkBetweenEyes` | `true` | Does eye-independent work (shadow refreshes, reflection probes, cloud shadows) once per frame instead of once per eye. Saves about 1.5 ms per frame in large worlds. Turn off if you ever see a shadow or reflection glitch in one eye. |
| `FsrInVR` | `true` | Uses the game's FSR anti-aliasing and upscaling in VR (when it is selected in the game's graphics options), with a separate history per eye. `false` uses FXAA, which is a little cheaper but leaves edges and fine detail shimmering. |
| `RaytracingInVR` | `false` | Allows ray tracing while the headset is active. It roughly doubles the GPU time and needs much more video memory. The game's own setting, used on the monitor, is not changed. |
| `Foveation` | `Quality` | Foveated rendering: `Off`, `Quality` (coarse shading beyond 48 degrees from the lens center), `Balanced` (beyond 40) or `Performance` (beyond 32, and 4x4 beyond 50). |
| `VrPostEffects` | `true` | No lens dirt, bloom at half resolution without its anti-flicker pass. |
| `LensFlaresInVR` | `false` | Lens flares and lamp glare. |
| `DynamicResolution` | `true` | Lowers the render resolution a little when a frame would be late, and raises it again when there is room. |
| `DynamicResolutionMin` | `0.5` | Lowest render resolution dynamic resolution may use, as a fraction of the eye image. |
| `DynamicResolutionTarget` | `Auto` | `Auto` aims for the headset's full refresh rate when the GPU nearly makes it at the preset's resolution, otherwise half (45 fps on 90 Hz, with SteamVR reprojection). `Full` or `Half` forces one. |
| `HudPieces` | | Placement of the HUD parts that can follow your head; set it with MODIFY UI in the game's VR settings. |
| `SystemWaitSeconds` | `15` | How long to wait at start-up for the headset to appear. |
| `VerboseLogging` | `false` | Writes more detail to the log. |

Settings are read at game start; the panel size keys change `UiWidthDegrees` while playing.

## Using it

- **Recenter:** the view recenters automatically when the session starts and when a world loads. Press `Home` (the
  `RecenterKey` setting), or use SteamVR's "Reset seated position", to recenter at any time.
- **Debug capture:** press `Ctrl+Shift+F10`, or create an empty file named `capture` in the plugin folder, to save
  what the headset receives into `captures\`.
- **Steering:** the mouse or gamepad still turns the character and aims tools. The headset adds free head movement on
  top. The crosshair stays in the center of the floating HUD panel.
- **Menus:** use the mouse as usual; its pointer is drawn on the panel. Keep the game window focused on the desktop.
  The game window is taller than most monitors (it has the headset's per-eye resolution). If the top part of it, where
  the UI is, hangs off the screen, the plugin moves the window once so the mouse can reach the whole UI.
- **Log:** everything the plugin does is written to `%LOCALAPPDATA%\SE2VR\SE2VR.log`, and mirrored into the game log
  with an `[SE2VR]` prefix.

## Performance

The scene is drawn twice per frame, so expect roughly half the frame rate you get on the monitor at the same
resolution. Measured on an RTX 3050 with a VIVE Pro 2 (90 Hz), RenderScale 0.75 (1668x1668 per eye), Medium preset:

| World | Frame rate | GPU time per frame (both eyes) |
|---|---|---|
| Empty World | ~89 fps | ~9 ms |
| Creative, Concordia Research Facility (large grids) | ~45 fps (SteamVR reprojects to 90 Hz) | ~21 ms |

Doing eye-independent work once per frame (`ShareWorkBetweenEyes`) cut the GPU time in the Creative world by about
1.5 ms per frame (to about 11 ms of GPU work), measured A/B in one session.

To make it smoother, lower `RenderScale` (0.6 draws about a third fewer pixels than 0.75), the game's graphics preset,
or SteamVR's per-application resolution. The log writes a `Perf:` line every 10 seconds with the frame rate and
timings. For a per-pass GPU breakdown, start the game with the environment variable `SE2VR_GPU_PROFILE=1` (add
`SE2VR_GPU_PROFILE_AB=1` to flip `ShareWorkBetweenEyes` every 10 seconds for a same-session comparison).

## Known limitations

- No motion controllers or VR interaction yet.
- The HUD panel floats at a fixed distance. When you focus on something much nearer or farther, the crosshair can
  look doubled.
- World markers (GPS, antennas) are placed on the panel where their objects appear from the seated head position, so
  they drift a little when you lean far to the side. On a curved panel they are exact only near its center. The
  colonization map screen has not been checked in VR yet.
- Cockpits and ship flight have not been checked in VR yet.
- Screen-space reflections keep one history shared by both eyes and may shimmer slightly.
- Water splash particles still simulate once per eye.
- Minimizing the game window stops scene rendering.
- Game updates can rename or change the renderer internals this plugin patches. When that happens, the plugin logs the
  failed patch and the game runs normally without VR.

## Prior art

[JakariaMods/SE2VR](https://github.com/JakariaMods/SE2VR) is an unofficial OpenVR plugin by a Keen developer. It
alternates eyes between frames by default. This project is an independent OpenXR implementation and shares no code
with it.

## Automated tests

The scripts in `tools\` drive a real game session and save what the headset receives, so changes can be checked
without wearing the headset:

```powershell
.\tools\Run-UiTour.ps1             # loads the built-in Empty World and opens every main in-game screen
.\tools\Run-UiTour.ps1 -World CRF  # the same in the Creative world (Concordia Research Facility)
.\tools\Run-MenuTour.ps1           # main menu: settings tabs, new game, load, workshop, credits
.\tools\Run-ViewCheck.ps1          # loads a world (default: Quick Start Survival on a planet) and captures the 3D view
.\tools\Run-StabilityCheck.ps1     # captures a still view several times and measures how much it flickers
.\tools\Run-ParticleCheck.ps1      # uses the welder, grinder and drill and flies the jetpack, capturing both eyes
.\tools\Run-HudEditorCheck.ps1     # MODIFY UI from the pause menu and the main menu: framed HUD parts, highlight, auto-follow
.\tools\Run-StabilityCheck.ps1 -Label fxaa -Settings @{ FsrInVR = $false }   # the same with other mod settings
```

Each run writes the eye images, the UI panel and a simulated headset view per eye (with the panel and pointer
composited using the real eye poses and fields of view), plus a report with plugin errors, game assertions and frame
timing, to `%LOCALAPPDATA%\SE2VR\testruns\<timestamp>`.

They never touch your own saves or the game installation. The game runs with `-appData` pointing at an isolated
profile (`%LOCALAPPDATA%\SE2VR\testprofile`), and it is started directly with the `SteamAppId` environment variable
instead of a `steam_appid.txt` file. SteamVR must be running. The headset can stay on the desk, because test mode
renders anyway and simulates a seated, straight-ahead head pose.

## Development notes

- `Directory.Build.props` points at the game's `Game2` folder. Override it with `SE2_GAME_DIR` or
  `/p:SE2GameDir=...`.
- Renderer internals are accessed through [Krafs.Publicizer](https://github.com/krafs/Publicizer). Patching uses
  [Harmony](https://github.com/pardeike/Harmony).
- The OpenXR bindings in `src/SE2VR/OpenXR` are hand-written. The plugin locates the active runtime the same way the
  Khronos loader does. To use the official loader instead (for example, to enable OpenXR API layers), drop
  `openxr_loader.dll` next to `SE2VR.dll`.
- Never commit decompiled game code.
