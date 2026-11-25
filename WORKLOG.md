# CoopPlus / TwoScreens - work log

Local two-player co-op for GTA V Legacy (story mode). Player 1 = keyboard/mouse on the main monitor,
Player 2 = controller, own camera, own screen (second monitor/TV). Everything here is original code;
it does not ship or modify the original "Offline Coop - SplitScreen" mod (Nexus 864, AlexDevilish),
whose terms forbid redistribution.

## Parts
- `src/TwoScreens.cpp` -> `TwoScreens.asi` (C++, Script Hook V present callback, D3D11)
  - Game alternates cameras every frame (P1 gameplay cam / P2 scripted cam).
  - CoopPlus stamps a 6x6 colour tag in the top-left of each frame (green = P1, magenta = P2).
  - A pixel shader reads the tag on the GPU and routes: P1 frames stay on the game window,
    P2 frames are copied to `g_lastP2` and shown on a borderless NOACTIVATE window on the 2nd screen
    (TV) every frame; the main screen re-shows the last P1 frame on P2 frames. Tag is painted over.
  - Shared memory `GTASplitScreenCoop` {int enabled, int cam, int heartbeat=FrameCount}; TV hides when
    heartbeat is stale (>250 ms) or enabled=0.
  - Tag probe logs "tag samples P2/P1/none" every 3 s to `A:\GTAV\TwoScreens.log`.
- `src/CoopPlus.cs` -> `scripts\CoopPlus.dll` (C#, SHVDN v3 API, build with BuildTools Roslyn csc)
  - Reads P2's pad through a private copy of xinput1_4 (`<GTA>\CoopPlus\xinput_p2.dll`).
  - Writes `GTAXInputBlock` {magic 0x58424C4B, ver 2, slot, heartbeat, seqlock} every tick so the
    game ignores P2's pad (currently consumed by the original mod's XInputBlock.asi - to be replaced).
  - Settings/log/test commands in `<GTA>\CoopPlus\` (CoopPlus.ini, CoopPlus.log, cmd.txt, debug.on).

## Facts learned the hard way
- FrameLag/HudFrameLag/MinimapFrameLag = 0 is correct (tag drawn with the same tick's view).
- P2 model hash 0x9B22DBAF = Franklin (player_one), 0x0D7114C9 = Michael (player_zero).
- `World.GetNearbyVehicles(ped, r)` returns nothing for script peds -> use the position overload.
- SHVDN reload key ignores injected key presses; restart GTA instead (PauseOnFocusLoss=0 set).
- Present the TV every frame from a persistent last-P2 texture, or it flashes black.
- GTA culls/deletes entities based on whichever camera rendered; hold entities near both players.
- Desktop-duplication capture: `test\rec.ps1 <name> mon|tv <frames> <fps>` (ffmpeg ddagrab).

## Test harness (`<GTA>\CoopPlus\cmd.txt`, one command per line)
spawn, despawn, split on|off, wait <ms>, pad <ms> lx ly rx ry lt rt <hexButtons>, car [model],
p1car, p1enter [seat], p1exit, p2face [yaw], camyaw <deg>, tp, drivemode direct|tasks, lag <hud> <map>,
state, mark <text>, pickup health|armour|pistol, hurt, kill, ped <dx> <dy>, held.
XInput button bits: DUp 1, DDown 2, DLeft 4, DRight 8, Start 10, Back 20, LS 40, RS 80, LB 100,
RB 200, A 1000, B 2000, X 4000, Y 8000.

## 11-24 late: workflow
- Co-op logic = `src\CoopPlus.cs` (class CoopCore) -> `<GTA>\CoopPlus\core\CoopPlusCore.dll`, hot-reloaded by
  `src\CoopPlusLoader.cs` (`scripts\CoopPlus.dll`). Build + install live: `src\build_cs.ps1 -Install` (no restart;
  Player 2 handed over via `<GTA>\CoopPlus\handoff.txt`).
- ASI changes need a restart: `test\restart_gta.ps1 -Marker <tag>`. It never force-kills the Rockstar launcher
  (overlapping launches -> "unable to install prerequisites" + admin prompt) and auto-clicks STORY MODE.
- Controller blocker now in TwoScreens.asi: GTA imports XINPUT1_4 by ordinal (2 GetState, 3 SetState); it
  also bypasses the IAT, so exports of both xinput modules are patched with jmp -> handlers that call a
  private copy (`<GTA>\CoopPlus\xinput_p2.dll`). XInputBlock.asi / OfflineCoopMod.asi moved to `<GTA>\_disabled`.
- HUD must look like GTA's own: P2 health/armour drawn into GTA's minimap bar (x 28-164 / 166-231, rows
  1049-1057 at 1080p); WASTED = MP_BIG_MESSAGE_FREEMODE SHOW_SHARD_WASTED_MP_MESSAGE with label RESPAWN_W.
- Test without a pad: `spawn 0` (explicit slot) then drive with `pad` commands.
