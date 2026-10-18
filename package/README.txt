SE2VR - VR for Space Engineers 2 (experimental)
================================================

Plays Space Engineers 2 in a PC VR headset: stereo 3D with head tracking, and the game's menus and HUD on a
floating panel in front of you. Keep playing with mouse and keyboard (or a gamepad); motion controllers are not
supported yet.

Requirements
- Space Engineers 2 on Steam.
- A PC VR headset with SteamVR (or another OpenXR runtime set as the active one).
- A strong graphics card: the game is drawn twice per frame, once per eye.

Graphics settings for VR
- Use the Low or Medium graphics preset. High and Extreme are far too heavy with two eyes: on an 8 GB card they run
  out of video memory, which makes the game stutter and can crash the graphics driver.
- Keep Anti-aliasing on FSR. In VR each eye gets its own FSR history; without it edges and fine detail shimmer.
- Ray tracing is switched off while you play in VR (set RaytracingInVR to true in SE2VR.json to allow it). Your
  normal setting is kept for playing on the monitor.

Install
1. Extract this zip anywhere.
2. Double-click Install.cmd.
   It copies the mod to %LOCALAPPDATA%\SE2VR, creates a "Space Engineers 2 VR" shortcut on your desktop and
   copies the Steam launch option to your clipboard. Nothing in the game's folder is changed, and your saves are
   not touched.

Play
1. Connect the headset. Steam must be running and signed in.
2. Double-click "Space Engineers 2 VR" on your desktop. It starts SteamVR first if needed.
   Alternative: paste the launch option into Steam > Space Engineers 2 > Properties > General > Launch Options and
   start the game from Steam. Then every start from Steam is in VR; remove the launch option to play normally.

In the game
- The view recenters when a world loads. Press Home (or use SteamVR's "Reset seated position") to recenter.
- Ctrl+PageUp / Ctrl+PageDown make the floating panel bigger / smaller (the size is remembered).
- The mouse turns your character and aims tools; the headset adds free head movement on top.
- Menus work with the mouse; its pointer is shown on the floating panel. Keep the game window focused.
- The game window on your monitor mirrors one eye.

VR settings in the game
- Settings has a VR tab: render resolution, dynamic resolution, foveated rendering, FSR, ray tracing in VR, post
  effects, world scale, menu screen size and distance. Changes apply right away and are saved.
- MODIFY UI (top of the VR tab) takes parts of the HUD off the menu screen and places them around your view, where
  they follow your head: suit status, toolbar, ship status, objectives, notifications, warnings. While it is open the
  headset shows every part that follows your head in a frame (the chosen part in blue) with a picture of your HUD,
  so you see each change right away. Open it from the in-game menu (Esc) to see your HUD in the parts; from the main
  menu the parts show as empty frames. Moving a part switches "Follows your head" on.
- RESTORE puts the VR settings and the HUD layout back to their defaults.

Settings file (%LOCALAPPDATA%\SE2VR\SE2VR.json, edit with Notepad while the game is closed)
- RenderScale (0.75): resolution per eye. Lower it if the game stutters, raise it for a sharper image.
- UiWidthDegrees (70): size of the floating panel (also Ctrl+PageUp / Ctrl+PageDown in the game).
- UiDistance (1.6): distance of the panel in meters.
- RecenterKey (Home), ShowCursor (true), Enabled (true = VR on).

Troubleshooting
- Everything the mod does is written to %LOCALAPPDATA%\SE2VR\SE2VR.log. Every 10 seconds it notes the frame rate and
  video memory use, and it warns when video memory is full.
- The desktop shortcut writes what it did to %LOCALAPPDATA%\SE2VR\launcher.log, and shows a message if the game
  could not be started.
- If the headset is not found within 15 seconds, the game starts normally on the monitor.
- Your normal display settings (full screen, resolution, v-sync) are kept for playing without VR.

Uninstall
Double-click Uninstall.cmd. If you added the Steam launch option, remove it in Steam as well.
