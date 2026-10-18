SE2VR - VR for Space Engineers 2 (experimental)
================================================

Plays Space Engineers 2 in a PC VR headset: stereo 3D with head tracking, and the game's menus and HUD on a
floating panel in front of you. Keep playing with mouse and keyboard (or a gamepad); motion controllers are not
supported yet.

Requirements
- Space Engineers 2 on Steam.
- A PC VR headset with SteamVR (or another OpenXR runtime set as the active one).
- A strong graphics card: the game is drawn twice per frame, once per eye.

Install
1. Extract this zip anywhere.
2. Double-click Install.cmd.
   It copies the mod to %LOCALAPPDATA%\SE2VR, creates a "Space Engineers 2 VR" shortcut on your desktop and
   copies the Steam launch option to your clipboard. Nothing in the game's folder is changed, and your saves are
   not touched.

Play
1. Start SteamVR with the headset connected. Steam must be running and signed in.
2. Double-click "Space Engineers 2 VR" on your desktop.
   Alternative: paste the launch option into Steam > Space Engineers 2 > Properties > General > Launch Options and
   start the game from Steam. Then every start from Steam is in VR; remove the launch option to play normally.

In the game
- The view recenters when a world loads. Press Home (or use SteamVR's "Reset seated position") to recenter.
- Ctrl+PageUp / Ctrl+PageDown make the floating panel bigger / smaller (the size is remembered).
- The mouse turns your character and aims tools; the headset adds free head movement on top.
- Menus work with the mouse; its pointer is shown on the floating panel. Keep the game window focused.
- The game window on your monitor mirrors one eye.

Settings (edit %LOCALAPPDATA%\SE2VR\SE2VR.json with Notepad while the game is closed)
- RenderScale (0.75): resolution per eye. Lower it if the game stutters, raise it for a sharper image.
- UiWidthDegrees (70): size of the floating panel (also Ctrl+PageUp / Ctrl+PageDown in the game).
- UiDistance (1.6): distance of the panel in meters.
- RecenterKey (Home), ShowCursor (true), Enabled (true = VR on).

Troubleshooting
- Everything the mod does is written to %LOCALAPPDATA%\SE2VR\SE2VR.log.
- If the headset is not found within 15 seconds, the game starts normally on the monitor.
- Your normal display settings (full screen, resolution, v-sync) are kept for playing without VR.

Uninstall
Double-click Uninstall.cmd. If you added the Steam launch option, remove it in Steam as well.
