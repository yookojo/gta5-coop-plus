# CoopPlus: local two-player co-op for GTA V

Play GTA V story mode with a friend on the same PC. Player 1 uses keyboard and mouse. Player 2 uses a controller: Xbox, PlayStation (DualShock 4 / DualSense) or Switch Pro, with no DS4Windows needed. Each player has their own camera, aim, guns, cars, money, wanted level and minimap.

**Two screens:** Player 1's view stays on the main monitor at its full refresh rate, and Player 2's view goes to a second monitor or TV at that screen's own resolution. **One screen:** the view splits side by side or top and bottom.

> Supports **GTA V Legacy** (DirectX 11) with **Script Hook V**. GTA V Enhanced isn't supported. Story mode only. Script Hook V closes the game if you go online.

## Features
- **Two separate players in one world.** Player 2 walks, sprints, crouches, jumps and climbs. They aim (with controller aim assist), shoot while moving, punch, reload, and switch guns with a weapon wheel. They get into any car as driver or passenger, drive with analogue controls, do drive-bys, and fly helicopters and planes.
- **Screens.** Choose two screens, side by side, top and bottom, or side by side with nothing cut off. A swap key fixes reversed screens, and an "identify screens" check shows who's where. Frame share lets one player get more frames.
- **GTA's own HUD.** Player 2's health and armour sit in GTA's minimap bar, and their cash and wanted stars use the game's own font and star icon. Player 2's minimap is centred on Player 2. The WASTED screen and the menus use the game's own style.
- **Economy.** Player 2 has their own money, earned from kills, cash pickups and a share of mission payouts. A shop sells guns, armour, health and cars. Player 2 picks up weapons, health and armour from the ground.
- **Together or against each other.** Friendly fire, revive (press E next to a downed Player 2), deathmatch, survival waves and races to your waypoint. A "ride together" key and a "bring Player 2 here" option keep you together.
- **Story-mode safe.** Player 2 hides during cutscenes, character switches and fades, follows you when a mission moves you, and is protected from mission clean-ups. When you switch character, Player 2 becomes the protagonist you aren't playing.
- **Rumble** on the Xbox controller when Player 2 gets hit, a saved loadout, outfits, and "deliver my car".

## Install
1. Install **Script Hook V** for your game version from <http://www.dev-c.com/gtav/scripthookv/>: copy everything in its `bin` folder next to `GTA5.exe`.
2. Download the latest `CoopPlus-*.zip` from **Releases**, unzip it, and run:
   ```
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```
   The installer:
   - finds the game (Epic, Steam or Rockstar);
   - installs Script Hook V .NET (nightly) if it's missing;
   - copies CoopPlus;
   - sets GTA to borderless window and to keep running when unfocused;
   - on Epic, stops the admin prompt GTA shows on every launch (see below).

   To remove it again: `install.ps1 -Uninstall`.

## Controls
| | Player 1 (keyboard) | Player 2 (controller) |
|---|---|---|
| Join / leave | **N** | **Back + Start** (PlayStation: Share + Options) |
| Co-op menu | **F7** (shown on your screen) | **Back** (shown on Player 2's screen) |
| Split on/off, swap screens | **K**, **F9** | in the menu |
| Ride together | **J** | **D-pad down**: come to Player 1 |
| Revive Player 2 | **E** next to them | |
| Move / camera | normal GTA | left stick / right stick (click: first person) |
| Aim / shoot | | LT / RT |
| Sprint / jump / crouch | | hold A / X / click left stick |
| Enter car | | Y: tap = drive, hold = passenger |
| Weapons | | hold LB = weapon wheel, D-pad left/right = previous/next |
| Driving | | RT gas, LT brake/reverse, A handbrake, X change seat, click right stick = look back |
| Flying | | left stick moves, RT up (heli) / throttle (plane), LT down |

Settings are kept in `<GTA>\CoopPlus\CoopPlus.ini` and change from the in-game menu.

## How it works
- `TwoScreens.asi` (C++, D3D11, Script Hook V present callback). The game draws Player 1's and Player 2's cameras on alternate frames, and every frame carries a tiny colour tag. A pixel shader reads that tag on the GPU and sends each frame to the right place: the main window, a borderless window on the second screen, or one half of a single screen. Nothing waits on the GPU, so a stutter can never swap the screens. The same add-on hides Player 2's Xbox controller from the game so it only controls Player 2.
- `CoopPlus.dll` (C#, Script Hook V .NET v3) is a small loader. It hot-reloads `CoopPlus\core\CoopPlusCore.dll`, which holds all the gameplay.
- `PlayGTAV.exe` (Epic, optional) replaces the start-up stub. The original stub re-runs the Rockstar launcher installer **as administrator** on every launch just to check its version. This replacement starts the installed Rockstar launcher directly with the same arguments. The original is kept as `PlayGTAV.original.exe`.

## Limits
- Both players share one game, so each view gets about half of the game's frame rate. Frame share can tilt that.
- GTA only fully simulates the world around Player 1, so the area around a far-away Player 2 thins out. CoopPlus keeps cars and people near both players.
- Story missions are made for one player. Player 2 tags along but isn't part of a mission's logic.
- Two players only.

## Building from source
- Needs Visual Studio 2022 Build Tools (C++ and the Windows SDK) and a GTA folder with `ScriptHookVDotNet3.dll`.
- `src\build.bat` builds the ASI and the stub. `src\build_cs.ps1 -Install` builds the scripts and live-reloads the core into a running game.
- `test\run.ps1 <case>` runs the scripted in-game tests in `test\cases`. `package.ps1` builds the release zip.

## Credits
Inspired by "Offline Coop - SplitScreen" by AlexDevilish. No code or files from it are used or included. Uses Script Hook V by Alexander Blade and Script Hook V .NET (both installed separately).

## License
MIT. See `LICENSE`.
