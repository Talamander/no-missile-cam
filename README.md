# Missile Cam (Nuclear Option, BepInEx 5)

Client-only spectator camera for your own in-flight missiles and your selected target.

**Status: test build.** It compiles against Nuclear Option 0.34.1 (build `cb745d6c44f1`), but it has not been confirmed working in a real game yet. Please report what happens.

## Install (players)

1. Install BepInEx 5 (Windows x64, version 5.4.23 or later 5.4.x) from the official releases page: https://github.com/BepInEx/BepInEx/releases. Extract it into the Nuclear Option game folder (the folder with `NuclearOption.exe`). Start the game once and quit, so BepInEx creates its folders.
2. Download `NoMissileCam-<version>.zip` from this repository's Releases page.
3. Extract the zip into the same game folder. You should get `BepInEx/plugins/NoMissileCam/NoMissileCam.dll`.
4. Start the game. `BepInEx/LogOutput.log` should contain `Missile Cam <version> loaded.`

To remove it, delete the `BepInEx/plugins/NoMissileCam` folder.

Only you need it. Other players and the host do not need the mod.

## Keyboard controls

- `]` (right square bracket): follow your first missile in flight. Each further press follows your next missile. After the last one, the next press returns to your aircraft.
- `[` (left square bracket): follow your primary target, only if your side has an accurate track on it. Press again to return to your aircraft.
- `\` (backslash): return to your aircraft and the view you had before.

After the followed missile hits or disappears, the camera holds for 2.5 s. Then it moves to your next missile in flight, or returns to your aircraft.

## Change the keys in game

Open the game's **Controls** menu and click **Missile Cam Keys** (added next to Edit Bindings). You can also press `Ctrl+F8` anywhere. Click a binding, then press the new key or joystick button; Escape cancels and **clear** removes a binding. Each action can have one keyboard key (with Shift/Ctrl/Alt if you hold them) and one joystick button. Changes save at once.

Everything is also in `BepInEx/config/talamander.no.missilecam.cfg` (created on first start), including timings. BepInEx ConfigurationManager also shows these settings if you have it installed.

## What to report when testing

- Did `]` follow a missile, and did pressing again cycle and then return?
- Did `[` follow a selected target?
- What happened when the missile hit, when the target died, and when you died while watching?
- Your `BepInEx/LogOutput.log`. Lines from `Missile Cam` explain ignored presses (for example `Press ignored: game state Editor.` or `no local aircraft in flight`).

## Controls (full list)

Both missile and target controls are toggles.

- `]` or MOZA AB6 button 12 (`CycleMissile`, `CycleMissileDevice` / `CycleMissileButton`): the first press follows your first missile in flight. Each further press follows the next missile. After the last missile, the next press returns to your aircraft.
- `[` or MOZA AB6 button 14 (`ViewTarget`, `ViewTargetDevice` / `ViewTargetButton`): follows your primary target, only if your side has an accurate track on it. Pressing again returns to your aircraft.
- `\` (`Return`, `ReturnDevice` / `ReturnButton`, no joystick button by default): back to your aircraft and the view you had before.
- `Ctrl+F8` (`OpenSettings`): the key binding window.

Joystick buttons are optional and default to the MOZA AB6. With no matching joystick they do nothing. Joystick buttons use the MOZA Cockpit / MOZA Button Viewer numbering (1-based). The plugin reads them straight from Rewired, matching part of the joystick name (`...Device`, default `AB6`), so they need no game action. Capturing a button in the binding window stores that joystick's full name.

The game's own bindings on those buttons still fire. To free a button, clear it in the game's Controls menu, or set `ReleaseGameBindings = true` once. That option writes `BepInEx/config/missilecam-joystick-<id>-before-release.xml`, removes the game bindings on the two buttons, saves, and sets itself back to false.

After the followed missile hits or disappears, the camera holds for `LingerSeconds` (2.5 s), then moves to your next missile in flight (`AutoNextMissile`) or returns to your aircraft.

## How it works (code evidence, decompiled 0.34.1 client, build `cb745d6c44f1`)

- It calls `CameraStateManager.SetFollowingUnit(unit)`. The map uses the same call when you click a unit icon while dead or spectating (`UnitMapIcon.Select`). The orbit camera already handles a `Missile` as the followed unit, and even looks at the missile's target (`CameraOrbitState.LookingAtTarget`).
- Own missiles come from `UnitRegistry.allUnits`, filtered by `Missile.ownerID == localAircraft.persistentID`. Missiles register there on every client (`Missile.StartMissile` → `RegisterUnit`) and leave it when disabled.
- Target view uses the game's own rule (`FactionHQ.IsTargetPositionAccurate(target, 100f)`), the same check as the map click and `CameraStateManager.FollowCheck`. FollowCheck still drops the view if the track goes stale; the plugin then returns you to your aircraft.
- Restore: `SetFollowingUnit(aircraft)`, then `SwitchState(savedState)`. Never switches to the cockpit state while following a missile, because `CameraCockpitState.EnterState` casts the followed unit to `Aircraft`.
- Death while watching a missile: the game plays its death sound and opens the map only if the camera was on your aircraft when it died. The plugin returns to the aircraft at once and repeats that handling.
- Any other camera change (map click, Change Spectate Faction, back to own aircraft) is treated as you taking over: the plugin stops driving.

## Client/server requirements

- Only the player who wants the camera installs it. No network messages. Its only Harmony patch is a postfix on `ControlsMenu.Start` that adds the Missile Cam Keys button. No camera code in the game sends a Cmd, Rpc or SyncVar (`CameraStateManager`, `Camera*State`, `UnitMapIcon`).
- Mirage visibility: Assembly-CSharp has no `NetworkVisibility` subclass, so Mirage uses its default `AlwaysVisible` (every scene-ready player observes every object). Missiles therefore exist on every client. This is from code; no packet capture was done.
- No server setting controls spectating or the free camera (checked `MissionSettings`, `DedicatedServerConfig`, `HostOptions`). There is no anti-cheat in the assembly. `ModdedServer` is only a lobby label the host sets.
- Unknown until a real test: whether a host or other players can notice the mod in any way. Nothing in the code suggests it.

## Limits and risks

- While you watch a missile, your aircraft keeps flying on your inputs. The game does the same in its own orbit view.
- Move input (Move Longitudinal/Lateral) in the orbit view drops the followed unit in vanilla code. The plugin then returns you to your aircraft.
- `AllowInVR` is off: the plugin does nothing if a NOVR plugin is loaded, until someone tests it in the headset.
- The camera renders whatever is near the missile. That is the same as the vanilla free camera, but it is a view of your own weapon you do not get in vanilla while alive.

## Build

```
dotnet build -c Release -p:GameManagedDir="<game folder>/NuclearOption_Data/Managed"
```

The game's DLLs are compile references only and are not copied or redistributed. BepInEx comes from the official NuGet feed (`BepInEx.BaseLib` 5.4.21) as a compile reference only. The dedicated server's `NuclearOptionServer_Data/Managed` folder also works for a type check (same build hash).
