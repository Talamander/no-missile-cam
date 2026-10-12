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

To build the player zip (`dist/NoMissileCam-<version>.zip`, which unzips into the game folder):

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
| Behaviour | `LingerSeconds` | 2.5 | How long the camera holds after the followed missile is gone. |
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
- Any other camera change (map click, Change Spectate Faction, back to own aircraft) is treated as
  you taking over: the plugin stops driving.
- The game's first scene load destroys the BepInEx manager object, and every plugin on it, unless the
  object is hidden. BepInEx's `HideManagerGameObject` option does that; since 0.3.2 the plugin also
  sets `HideFlags.HideAndDontSave` on its own object in `Awake`, as Nuclear Replay does. Before
  0.3.2, the mod only kept working on installs where another mod or that option hid the object.

## Client and server

- Only the player who wants the camera installs it. It sends no network messages. Its only Harmony
  patch is a postfix on `ControlsMenu.Start` that adds the **Missile Cam Keys** button. No camera
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

What to include in a bug report:

- Did `]` follow a missile, and did pressing again cycle and then return?
- Did `[` follow a selected target?
- What happened when the missile hit, when the target died, and when you died while watching?
- Your `BepInEx/LogOutput.log`. Lines from `Missile Cam` explain ignored presses (for example
  `Press ignored: game state Editor.` or `no local aircraft in flight`).
