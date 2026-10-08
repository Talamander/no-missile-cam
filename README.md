# Missile Cam (Nuclear Option, BepInEx 5)

Client-only spectator camera for your own in-flight missiles and your selected target. Status: **code written and type-checked only. Not installed, not run in the game, not tested.**

## Keys (BepInEx config `talamander.no.missilecam.cfg`)

- `]` CycleMissile: follow your next missile in flight (starts the missile cam).
- `[` ViewTarget: follow your primary target, only if your side has an accurate track on it.
- `\` Return: back to your aircraft and the view you had before.

After the followed missile hits or disappears, the camera holds for `LingerSeconds` (2.5 s), then moves to your next missile in flight (`AutoNextMissile`) or returns to your aircraft.

## How it works (code evidence, decompiled 0.34.1 client, build `cb745d6c44f1`)

- It calls `CameraStateManager.SetFollowingUnit(unit)`. The map uses the same call when you click a unit icon while dead or spectating (`UnitMapIcon.Select`). The orbit camera already handles a `Missile` as the followed unit, and even looks at the missile's target (`CameraOrbitState.LookingAtTarget`).
- Own missiles come from `UnitRegistry.allUnits`, filtered by `Missile.ownerID == localAircraft.persistentID`. Missiles register there on every client (`Missile.StartMissile` → `RegisterUnit`) and leave it when disabled.
- Target view uses the game's own rule (`FactionHQ.IsTargetPositionAccurate(target, 100f)`), the same check as the map click and `CameraStateManager.FollowCheck`. FollowCheck still drops the view if the track goes stale; the plugin then returns you to your aircraft.
- Restore: `SetFollowingUnit(aircraft)`, then `SwitchState(savedState)`. Never switches to the cockpit state while following a missile, because `CameraCockpitState.EnterState` casts the followed unit to `Aircraft`.
- Death while watching a missile: the game plays its death sound and opens the map only if the camera was on your aircraft when it died. The plugin returns to the aircraft at once and repeats that handling.
- Any other camera change (map click, Change Spectate Faction, back to own aircraft) is treated as you taking over: the plugin stops driving.

## Client/server requirements

- Only the player who wants the camera installs it. No Harmony patches, no network messages. No camera code in the game sends a Cmd, Rpc or SyncVar (`CameraStateManager`, `Camera*State`, `UnitMapIcon`).
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
dotnet build -c Release                     # against the client install (needs /mnt/games)
dotnet build -c Release -p:GameManagedDir=$HOME/Games/nuclear-option-server/NuclearOptionServer_Data/Managed
```

The second form type-checks against the dedicated server DLLs (same build hash). It is not the shipping build. BepInEx comes from the official NuGet feed (`BepInEx.BaseLib` 5.4.21) as a compile reference only.

Do not install into the Steam game, `/mnt/games/no-maptest` or `/mnt/games/no-friendtest` without coordinating first.
