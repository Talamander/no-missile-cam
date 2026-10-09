using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using NuclearOption.MissionEditorScripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Talamander.MissileCam
{
    /// <summary>
    /// Client-only spectator camera for the local player's own in-flight missiles and their selected target.
    ///
    /// It reuses the game's own spectator pathway: CameraStateManager.SetFollowingUnit(unit), the same call the
    /// map uses when you click a unit icon (UnitMapIcon.Select). The game's orbit state already supports a Missile
    /// as the followed unit. The plugin sends nothing over the network and patches no game method.
    ///
    /// Visibility rules copied from the game:
    /// - Missiles: only missiles whose ownerID is the local aircraft (same faction, always tracked).
    /// - Target: only when the local HQ owns it or has an accurate track (FactionHQ.IsTargetPositionAccurate(t, 100)),
    ///   the same test as UnitMapIcon.Select and CameraStateManager.FollowCheck. FollowCheck still drops the view
    ///   once the track goes stale.
    ///
    /// Joystick buttons (default MOZA AB6 buttons 12 and 14) are read straight from Rewired, so they work without a
    /// game action. Both buttons and their keys are toggles: pressing again cycles to the next missile or returns.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "talamander.no.missilecam";
        public const string Name = "Missile Cam";
        public const string Version = "0.2.1";

        internal static ManualLogSource Log;

        ConfigEntry<bool> pluginEnabled;
        ConfigEntry<KeyboardShortcut> cycleKey;
        ConfigEntry<KeyboardShortcut> targetKey;
        ConfigEntry<KeyboardShortcut> returnKey;
        ConfigEntry<float> lingerSeconds;
        ConfigEntry<bool> autoNextMissile;
        ConfigEntry<bool> allowTargetView;
        ConfigEntry<bool> allowInVR;
        ConfigEntry<bool> showLabel;
        ConfigEntry<string> joystickName;
        ConfigEntry<int> missileButton;
        ConfigEntry<int> targetButton;
        ConfigEntry<bool> releaseGameBindings;

        Rewired.Joystick joystick;
        float nextJoystickSearch;

        // Session state. Null savedState means the plugin is not driving the camera.
        CameraBaseState savedState;
        Aircraft savedAircraft;
        Unit modTarget;          // the unit the plugin asked the camera to follow
        float lostAt = -1f;      // Time.unscaledTime when modTarget was lost (impact, despawn, track loss)
        string label = "";
        bool vrDetected;

        void Awake()
        {
            Log = Logger;
            pluginEnabled = Config.Bind("General", "Enabled", true, "Master switch.");
            cycleKey = Config.Bind("Keys", "CycleMissile", new KeyboardShortcut(KeyCode.RightBracket),
                "Toggle: follow your first missile in flight, then the next one on each press, then back to your aircraft.");
            targetKey = Config.Bind("Keys", "ViewTarget", new KeyboardShortcut(KeyCode.LeftBracket),
                "Toggle: follow your primary target (only with an accurate track); press again to return to your aircraft.");
            returnKey = Config.Bind("Keys", "Return", new KeyboardShortcut(KeyCode.Backslash),
                "Return to your aircraft and the camera view you had before.");
            lingerSeconds = Config.Bind("Behaviour", "LingerSeconds", 2.5f,
                "After the followed missile hits or disappears, hold the camera where it is for this long.");
            autoNextMissile = Config.Bind("Behaviour", "AutoNextMissile", true,
                "After the linger, follow your next missile in flight instead of returning, if there is one.");
            allowTargetView = Config.Bind("Behaviour", "AllowTargetView", true,
                "Allow the ViewTarget key at all.");
            allowInVR = Config.Bind("Behaviour", "AllowInVR", false,
                "Allow the missile cam when the NOVR plugin is loaded. Off by default: untested in VR.");
            showLabel = Config.Bind("Display", "ShowLabel", true, "Small text label while the missile cam is active.");
            joystickName = Config.Bind("Joystick", "DeviceName", "AB6",
                "Part of the joystick name as Rewired reports it (the AB6 is \"Gudsen MOZA AB6 FFB Base\"). Empty disables joystick buttons.");
            missileButton = Config.Bind("Joystick", "MissileButton", 12,
                "Joystick button for the CycleMissile toggle, numbered as in MOZA Cockpit / MOZA Button Viewer (1-based). 0 disables.");
            targetButton = Config.Bind("Joystick", "TargetButton", 14,
                "Joystick button for the ViewTarget toggle, MOZA numbering (1-based). 0 disables.");
            releaseGameBindings = Config.Bind("Joystick", "ReleaseGameBindings", false,
                "Opt-in, once: remove the game's own bindings on MissileButton and TargetButton for this joystick, so a press does " +
                "not also trigger a game action. Saves a backup XML to BepInEx/config first, then sets itself to false.");

            vrDetected = HasPluginLike("novr");
            SceneManager.activeSceneChanged += (_, __) => ResetState("scene changed");
            Log.LogInfo($"{Name} {Version} loaded. VR plugin detected: {vrDetected}.");
        }

        static bool HasPluginLike(string fragment)
        {
            foreach (var key in BepInEx.Bootstrap.Chainloader.PluginInfos.Keys)
                if (key.ToLowerInvariant().Contains(fragment)) return true;
            return false;
        }

        bool Active => savedState != null;

        void Update()
        {
            if (!pluginEnabled.Value) return;
            bool missilePress = KeyPressed(cycleKey.Value) || JoystickButtonDown(missileButton.Value);
            bool targetPress = KeyPressed(targetKey.Value) || JoystickButtonDown(targetButton.Value);
            bool returnPress = KeyPressed(returnKey.Value);
            bool anyPress = missilePress || targetPress || returnPress;

            if (vrDetected && !allowInVR.Value) { if (anyPress) Flash("off in VR (set AllowInVR)"); return; }
            if (GameManager.gameState != GameState.SinglePlayer && GameManager.gameState != GameState.Multiplayer)
            {
                if (Active) ResetState("left gameplay");
                if (anyPress) Log.LogInfo($"Press ignored: game state {GameManager.gameState}.");
                return;
            }
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null)
            {
                if (Active) ResetState("no camera");
                if (anyPress) Log.LogInfo("Press ignored: no CameraStateManager.");
                return;
            }

            if (Active) Supervise(cam);

            if (InputFieldChecker.InsideInputField) { if (anyPress) Log.LogInfo("Press ignored: text field focused."); return; }
            if (missilePress) CycleMissile(cam);
            else if (targetPress && allowTargetView.Value) ViewTarget(cam);
            else if (returnPress && Active) Restore(cam, "return key");
        }

        /// <summary>
        /// Main key down this frame with its own modifiers held. Unlike KeyboardShortcut.IsDown, this does not
        /// reject the press while any other key or joystick button is held (throttle keys, MOZA switches that
        /// report as held buttons).
        /// </summary>
        static bool KeyPressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !UnityInput.Current.GetKeyDown(shortcut.MainKey)) return false;
            foreach (var modifier in shortcut.Modifiers)
                if (!UnityInput.Current.GetKey(modifier)) return false;
            return true;
        }

        // ---- joystick ----

        bool JoystickButtonDown(int mozaNumber)
        {
            if (mozaNumber <= 0 || !FindJoystick()) return false;
            int index = mozaNumber - 1; // MOZA numbering is 1-based; Rewired button indices are 0-based.
            return index < joystick.buttonCount && joystick.GetButtonDown(index);
        }

        bool FindJoystick()
        {
            if (joystick != null && joystick.isConnected) return true;
            joystick = null;
            if (string.IsNullOrEmpty(joystickName.Value) || !Rewired.ReInput.isReady) return false;
            if (Time.unscaledTime < nextJoystickSearch) return false;
            nextJoystickSearch = Time.unscaledTime + 3f;
            joystick = Rewired.ReInput.controllers.Joysticks.FirstOrDefault(j => j.name != null && j.name.Contains(joystickName.Value));
            if (joystick == null) return false;
            Log.LogInfo($"Joystick buttons on \"{joystick.name}\": missile {missileButton.Value}, target {targetButton.Value}.");
            if (releaseGameBindings.Value) ReleaseGameBindings();
            return true;
        }

        void ReleaseGameBindings()
        {
            try
            {
                var map = Rewired.ReInput.players.GetPlayer(0).controllers.maps.GetMap(Rewired.ControllerType.Joystick, joystick.id, 0, 0);
                if (map == null) { Log.LogWarning("No game controller map for the joystick yet; will retry next start."); return; }
                var ids = new HashSet<int>();
                foreach (int n in new[] { missileButton.Value, targetButton.Value })
                    if (n > 0 && n <= joystick.buttonCount) ids.Add(joystick.Buttons[n - 1].elementIdentifier.id);
                var doomed = map.AllMaps.Where(m => m.elementType == Rewired.ControllerElementType.Button && ids.Contains(m.elementIdentifierId)).ToArray();
                if (doomed.Length > 0)
                {
                    File.WriteAllText(Path.Combine(Paths.ConfigPath, "missilecam-joystick-before-release.xml"), map.ToXmlString());
                    foreach (var m in doomed)
                    {
                        var action = Rewired.ReInput.mapping.GetAction(m.actionId);
                        Log.LogInfo($"Removing game binding {m.elementIdentifierName} -> {(action != null ? action.name : m.actionId.ToString())}.");
                        map.DeleteElementMap(m.id);
                    }
                    Rewired.ReInput.userDataStore.SavePlayerData(0);
                    PlayerPrefs.Save();
                }
                else Log.LogInfo("No game bindings on the missile cam buttons.");
                releaseGameBindings.Value = false;
                Config.Save();
            }
            catch (System.Exception e) { Log.LogError(e); }
        }

        // ---- actions ----

        void CycleMissile(CameraStateManager cam)
        {
            if (!TryGetFlyingAircraft(out var aircraft)) { Flash("no local aircraft in flight"); return; }
            var missiles = OwnMissiles(aircraft);
            int current = Active && modTarget is Missile m ? missiles.IndexOf(m) : -1;
            bool onMissile = Active && modTarget is Missile;
            // Toggle: on a missile, step to the next one; after the last (or with none left), return to the aircraft.
            if (onMissile && current + 1 >= missiles.Count && (current >= 0 || missiles.Count == 0))
            {
                Restore(cam, "missile toggle");
                return;
            }
            if (missiles.Count == 0) { Flash("no missiles in flight"); return; }
            var next = missiles[current + 1];
            Follow(cam, aircraft, next, $"missile {missiles.IndexOf(next) + 1}/{missiles.Count}: {UnitLabel(next)}");
        }

        void ViewTarget(CameraStateManager cam)
        {
            if (!TryGetFlyingAircraft(out var aircraft)) { Flash("no local aircraft in flight"); return; }
            Unit target = null;
            var list = aircraft.weaponManager != null ? aircraft.weaponManager.GetTargetList() : null;
            if (list != null)
                foreach (var u in list)
                    if (u != null && !u.disabled) { target = u; break; }
            // Toggle: pressing again while on the target goes back to the aircraft.
            if (Active && modTarget != null && !(modTarget is Missile)) { Restore(cam, "target toggle"); return; }
            if (target == null) { Flash("no target selected"); return; }
            if (!MayView(target)) { Flash("target not tracked"); return; }
            Follow(cam, aircraft, target, $"target: {UnitLabel(target)}");
        }

        void Follow(CameraStateManager cam, Aircraft aircraft, Unit unit, string text)
        {
            if (!Active)
            {
                savedState = cam.currentState;
                savedAircraft = aircraft;
            }
            modTarget = unit;
            lostAt = -1f;
            label = text;
            // Same call the map uses. With a Rigidbody it switches to the orbit state.
            cam.SetFollowingUnit(unit);
            Log.LogInfo($"Following {text}");
        }

        void Restore(CameraStateManager cam, string why)
        {
            var aircraft = savedAircraft;
            var state = savedState;
            ResetState(why);
            if (aircraft == null || !GameManager.IsLocalAircraft(aircraft)) return;
            cam.SetFollowingUnit(aircraft);
            if (aircraft.disabled)
            {
                // The game only plays its death handling if the camera was on the aircraft when it died
                // (CameraStateManager.Cam_OnFollowingUnitDisabled). Repeat that here.
                if (aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0].dead)
                {
                    SoundManager.PlayInterfaceOneShot(GameAssets.i.deathSound);
                    if (aircraft.Player != null) aircraft.Player.ShowMap(5f);
                }
                return;
            }
            // CameraCockpitState.EnterState casts followingUnit to Aircraft, so it is safe only now that the
            // aircraft is followed again.
            if (state != null && state != cam.currentState) cam.SwitchState(state);
        }

        void ResetState(string why)
        {
            if (Active) Log.LogInfo($"Missile cam off ({why}).");
            savedState = null;
            savedAircraft = null;
            modTarget = null;
            lostAt = -1f;
            label = "";
        }

        // ---- per-frame supervision ----

        void Supervise(CameraStateManager cam)
        {
            // Local aircraft lost or replaced: go back at once so the game's own death flow runs.
            if (savedAircraft == null || !GameManager.IsLocalAircraft(savedAircraft) || savedAircraft.disabled)
            {
                Restore(cam, "local aircraft destroyed or replaced");
                return;
            }

            var followed = cam.followingUnit;
            bool ours = followed != null && followed == modTarget;
            bool targetLost = modTarget == null || modTarget.disabled;

            if (followed != null && !ours)
            {
                // The player or the game moved the camera somewhere else (map click, Change Spectate Faction,
                // back to own aircraft). Step aside; do not fight the game.
                ResetState(followed == savedAircraft ? "camera returned to aircraft" : "camera taken over");
                return;
            }

            if (followed == null && !targetLost)
            {
                // The unit still exists but the game dropped it: FollowCheck after a stale track, or move input in
                // orbit. Do not leave a flying player in a detached free camera; go back to the aircraft.
                Restore(cam, "game dropped the view (track lost or move input)");
                return;
            }

            // The followed unit hit, detonated or despawned. The game's Cam_OnFollowingUnitDisabled has already
            // switched to the free state at the last position. Linger there, then move on.
            if (followed == null || targetLost)
            {
                if (lostAt < 0f) { lostAt = Time.unscaledTime; label += " (lost)"; }
                if (Time.unscaledTime - lostAt < lingerSeconds.Value) return;
                if (autoNextMissile.Value)
                {
                    var missiles = OwnMissiles(savedAircraft);
                    if (missiles.Count > 0)
                    {
                        Follow(cam, savedAircraft, missiles[0], $"missile 1/{missiles.Count}: {UnitLabel(missiles[0])}");
                        return;
                    }
                }
                Restore(cam, "followed unit gone");
            }
        }

        // ---- helpers ----

        static bool TryGetFlyingAircraft(out Aircraft aircraft)
        {
            return GameManager.GetLocalAircraft(out aircraft) && aircraft != null && !aircraft.disabled;
        }

        static List<Missile> OwnMissiles(Aircraft aircraft)
        {
            var result = new List<Missile>();
            var owner = aircraft.persistentID;
            // UnitRegistry.allUnits drops units when they are disabled (Registry_OnUnitDisable).
            foreach (var unit in UnitRegistry.allUnits)
                if (unit is Missile m && m != null && !m.disabled && m.ownerID == owner)
                    result.Add(m);
            result.Sort((a, b) => a.persistentID.Id.CompareTo(b.persistentID.Id));
            return result;
        }

        /// <summary>The game's own rule for following a unit you do not own.</summary>
        static bool MayView(Unit unit)
        {
            if (!GameManager.GetLocalHQ(out var hq)) return true; // no faction: vanilla spectator sees everything
            if (unit.NetworkHQ == null || unit.NetworkHQ == hq) return true;
            return hq.IsTargetPositionAccurate(unit, 100f);
        }

        static string UnitLabel(Unit unit)
        {
            if (unit == null) return "?";
            if (unit.definition != null && !string.IsNullOrEmpty(unit.definition.unitName)) return unit.definition.unitName;
            return string.IsNullOrEmpty(unit.unitName) ? unit.name : unit.unitName;
        }

        string flash = "";
        float flashUntil;
        void Flash(string text) { flash = text; flashUntil = Time.unscaledTime + 1.5f; Log.LogInfo(text); }

        GUIStyle style;
        void OnGUI()
        {
            if (!showLabel.Value) return;
            string text = Active ? "MISSILE CAM  " + label : (Time.unscaledTime < flashUntil ? "MISSILE CAM  " + flash : null);
            if (text == null) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            var rect = new Rect(24, Screen.height - 64, Screen.width - 48, 28);
            style.normal.textColor = new Color(0, 0, 0, 0.7f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
            style.normal.textColor = new Color(1f, 0.85f, 0.3f, 1f);
            GUI.Label(rect, text, style);
        }
    }
}
