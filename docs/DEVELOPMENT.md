# Missile Cam for developers

How the mod works, what it touches in the game, and how to build it. Players do not need any of
this; see the [README](../README.md).

## Build

```
dotnet build -c Release -p:GameManagedDir="<game folder>/NuclearOption_Data/Managed"
```

The game's DLLs are compile references only and are not copied or redistributed. BepInEx comes from
the official NuGet feed (`BepInEx.BaseLib` 5.4.21) as a compile reference only. The dedicated
server's `NuclearOptionServer_Data/Managed` folder also works for a type check (same build hash).

To build the release zips:

- `dist/NoMissileCam-<version>-plugins.zip`: the DLL, README and LICENSE at the zip root, for mod
  managers. NOMM unpacks a mod's zip into `BepInEx/plugins/<mod id>/` (the mod id is `NoMissileCam`),
  and NOMNOM's auto-update takes the **first** asset of each new GitHub release, so this zip must be
  uploaded first.
- `dist/NoMissileCam-<version>.zip`: the same files under `BepInEx/plugins/NoMissileCam/`, for players
  who unzip it into the game folder by hand. Upload it second.

The script also writes `dist/SHA256SUMS` and stops if the csproj and `Plugin.cs` versions differ.
NOMNOM needs a plain numeric tag such as `v0.3.4`.

```
python3 scripts/package.py -p:GameManagedDir="<game folder>/NuclearOption_Data/Managed"
```

## Settings

Everything is in `BepInEx/config/talamander.no.missilecam.cfg`, created on the first start. The
in-game key window (Ctrl+F8, or **Missile Cam Keys** in the Controls menu) writes the same file.

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | true | Master switch. |
| Keys | `CycleMissile` | `]` | Follow your first missile, then the next; after the last, back to your aircraft. |
| Keys | `ViewTarget` | `[` | Follow your primary target, if your side has an accurate track on it. |
| Keys | `Return` | `\` | Back to your aircraft and the view you had before. |
| Keys | `OpenSettings` | Ctrl+F8 | Open the key window. |
| Joystick | `CycleMissileDevice` / `CycleMissileButton` | `AB6` / 12 | Joystick button for `CycleMissile`. |
| Joystick | `ViewTargetDevice` / `ViewTargetButton` | `AB6` / 14 | Joystick button for `ViewTarget`. |
| Joystick | `ReturnDevice` / `ReturnButton` | none | Joystick button for `Return`. |
| Joystick | `ReleaseGameBindings` | false | One-shot: removes the game's own bindings on the mod's buttons, then sets itself back to false. |
| Behaviour | `LingerSeconds` | 2.5 | How long the camera holds after the followed missile is gone, 0 to 5 seconds. Camera move input is off during the hold. |
| Behaviour | `AutoNextMissile` | true | After the hold, move to your next missile in flight. |
| Behaviour | `AllowTargetView` | true | Allow the target view. |
| Behaviour | `AllowInVR` | true | Allow the missile cam while a VR plugin is loaded. |
| Display | `ShowLabel` | true | Small text label while the missile cam is active. |

Joystick buttons use the MOZA Cockpit / MOZA Button Viewer numbering (1-based). The plugin reads them
straight from Rewired, matching part of the joystick name (the `...Device` setting), so they need no
game action. With no matching joystick they do nothing. Capturing a button in the key window stores
that joystick's full name.

The game's own bindings on those buttons still fire. To free a button, clear it in the game's
Controls menu, or set `ReleaseGameBindings = true` once. That writes
`BepInEx/config/missilecam-joystick-<id>-before-release.xml` as a backup, removes the game bindings
on the two buttons, saves, and sets the option back to false.

## How it works

Code evidence is from the decompiled 0.34.1 client, build `cb745d6c44f1`. The game's main menu in
the test copy reports Early Access 0.34.2.

- It calls `CameraStateManager.SetFollowingUnit(unit)`. The map uses the same call when you click a
  unit icon while dead or spectating (`UnitMapIcon.Select`). The orbit camera already handles a
  `Missile` as the followed unit, and even looks at the missile's target
  (`CameraOrbitState.LookingAtTarget`).
- Own missiles come from `UnitRegistry.allUnits`, filtered by
  `Missile.ownerID == localAircraft.persistentID`. Missiles register there on every client
  (`Missile.StartMissile` calls `RegisterUnit`) and leave it when disabled.
- Target view uses the game's own rule (`FactionHQ.IsTargetPositionAccurate(target, 100f)`), the
  same check as the map click and `CameraStateManager.FollowCheck`. `FollowCheck` still drops the
  view if the track goes stale; the plugin then returns you to your aircraft.
- Restore: `SetFollowingUnit(aircraft)`, then `SwitchState(savedState)`. It never switches to the
  cockpit state while following a missile, because `CameraCockpitState.EnterState` casts the
  followed unit to `Aircraft`.
- Death while watching a missile: the game plays its death sound and opens the map only if the
  camera was on your aircraft when it died. The plugin returns to the aircraft at once and repeats
  that handling.
- No spectating while you fly. In the game's orbit camera, `CameraOrbitState.Inputs` lets the
  **Change Spectate Faction** key jump to the nearest tracked aircraft of another faction whenever the
  followed unit is not your own aircraft (`!GameManager.IsLocalAircraft(cam.followingUnit)`). Orbiting
  your own aircraft keeps that off, but following a missile or a target would turn it on. Since
  0.3.4, while the plugin has the camera:
  - a Harmony prefix makes `CameraOrbitState.TryGetNearestEnemyAircraft` return nothing when it is
    called from `Inputs` (the only call there is the hop, after the key went down), so the key does
    nothing. The "Spectate Next Aircraft" look-at in `CameraMotion`, which only turns the camera, is
    left alone;
  - every frame, if the camera follows anything other than the plugin's unit, your own missiles or
    your own aircraft, the plugin sends it back to your aircraft (the same path as the Return key).
  A flying player cannot follow other units in the unmodded game: `UnitMapIcon.Select` only selects
  a target while your aircraft is alive, and `GameplayUI.SwitchSpectatedAircraft` only runs without
  one.
- Back on your own aircraft through the game's controls: the plugin stops driving. Death and
  respawn go through the aircraft check first, so the game's own death flow runs as before.
- The hold after an impact runs in the game's free camera. The plugin sets
  `CameraStateManager.allowInputs = false` for the hold (the free and controlled states only move on
  input when it is true) and restores the old value afterwards. If the camera still moves more than
  50 m from where the hold started, the hold ends early. `LingerSeconds` is limited to 0 to 5.
- The game's first scene load destroys the BepInEx manager object, and every plugin on it, unless the
  object is hidden. BepInEx's `HideManagerGameObject` option does that; since 0.3.2 the plugin also
  sets `HideFlags.HideAndDontSave` on its own object in `Awake`, as Nuclear Replay does. Before
  0.3.2, the mod only kept working on installs where another mod or that option hid the object.

## Client and server

- Only the player who wants the camera installs it. It sends no network messages. Its Harmony
  patches are a postfix on `ControlsMenu.Start` that adds the **Missile Cam Keys** button, and the
  spectator hop guard on `CameraOrbitState.Inputs` and `CameraOrbitState.TryGetNearestEnemyAircraft`
  (active only while the plugin has the camera). No camera
  code in the game sends a Cmd, Rpc or SyncVar (`CameraStateManager`, `Camera*State`,
  `UnitMapIcon`).
- Mirage visibility: Assembly-CSharp has no `NetworkVisibility` subclass, so Mirage uses its default
  `AlwaysVisible` (every scene-ready player observes every object). Missiles therefore exist on
  every client. This is from the code; no packet capture was done.
- No server setting controls spectating or the free camera (checked `MissionSettings`,
  `DedicatedServerConfig`, `HostOptions`). There is no anti-cheat in the assembly. `ModdedServer` is
  only a lobby label the host sets.
- Unknown until a real multiplayer test: whether a host or other players can notice the mod in any
  way. Nothing in the code suggests it.

## Testing

The 0.3.2 test ran in an isolated copy of the game on Linux (Proton), headless, with a default
BepInEx install (`HideManagerGameObject = false`) and Missile Cam as the only plugin, in the
single-player mission *05. Furball*. A separate test-only plugin flew the aircraft, fired the
missiles, pressed the keys and saved frames; it is not part of this repository. The log showed the
mod alive at mission start, `[` following the target, `]` following a missile and then the next
one, and Ctrl+F8 opening the key window.

The 0.3.4 test used the same setup. The test plugin also pressed the game's Change Spectate Faction
action (injected through Rewired) while the mod followed the target and while it followed a missile,
called `CameraStateManager.SetFollowingUnit` on another aircraft directly, and held the free camera's
move input during the hold after a hit. Results, against 0.3.3 in the same mission:

- Change Spectate Faction while following the target or a missile: 0.3.3 moved the camera to another
  faction's aircraft and let go; 0.3.4 kept the camera on the same unit and logged
  `Change Spectate Faction ignored`.
- Direct switch to another aircraft: 0.3.3 stayed on that aircraft; 0.3.4 returned the camera to
  the player's aircraft.
- Hold after a hit with move input held: the 0.3.3 camera flew about 470 m away; the 0.3.4 camera
  moved 0.0 m, and a configured `LingerSeconds = 30` was clamped to 5.

What to include in a bug report:

- Did `]` follow a missile, and did pressing again cycle and then return?
- Did `[` follow a selected target?
- What happened when the missile hit, when the target died, and when you died while watching?
- Your `BepInEx/LogOutput.log`. Lines from `Missile Cam` explain ignored presses (for example
  `Press ignored: game state Editor.` or `no local aircraft in flight`).
