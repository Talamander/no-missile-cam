using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
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
    /// as the followed unit. The plugin sends nothing over the network. Its Harmony patches: a postfix on
    /// ControlsMenu.Start that adds the Missile Cam Keys button to the game's Controls menu, and the spectator hop
    /// guard on CameraOrbitState (see SpectateGuard).
    ///
    /// Visibility rules copied from the game:
    /// - Missiles: only missiles whose ownerID is the local aircraft (same faction, always tracked).
    /// - Target: only when the local HQ owns it or has an accurate track (FactionHQ.IsTargetPositionAccurate(t, 100)),
    ///   the same test as UnitMapIcon.Select and CameraStateManager.FollowCheck. FollowCheck still drops the view
    ///   once the track goes stale.
    /// - No spectating while you fly: while the plugin owns the camera, the game's spectator hop ("Change Spectate
    ///   Faction") is blocked, and any other switch to a unit that is not yours sends the camera back to your aircraft.
    /// - The hold after an impact is capped at MaxLingerSeconds, camera move input is off during it, and the hold ends
    ///   early if the camera moves away from the impact point anyway.
    ///
    /// Joystick buttons (default MOZA AB6 buttons 12 and 14) are read straight from Rewired, so they work without a
    /// game action. Both buttons and their keys are toggles: pressing again cycles to the next missile or returns.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "talamander.no.missilecam";
        public const string Name = "Missile Cam";
        public const string Version = "0.3.5";

        internal static ManualLogSource Log;

        /// <summary>Upper bound for LingerSeconds.</summary>
        public const float MaxLingerSeconds = 5f;
        /// <summary>The hold after an impact ends early if the camera moves further than this from where it started.</summary>
        const float MaxLingerDrift = 50f;

        ConfigEntry<bool> pluginEnabled;
        Binding missileBinding;
        Binding targetBinding;
        Binding returnBinding;
        Binding[] bindings;
        ConfigEntry<KeyboardShortcut> settingsKey;
        SettingsWindow settings;
        ConfigEntry<float> lingerSeconds;
        ConfigEntry<bool> autoNextMissile;
        ConfigEntry<bool> allowTargetView;
        ConfigEntry<bool> allowInVR;
        ConfigEntry<bool> showLabel;
        ConfigEntry<bool> releaseGameBindings;

        // Session state. Null savedState means the plugin is not driving the camera.
        CameraBaseState savedState;
        Aircraft savedAircraft;
        Unit modTarget;          // the unit the plugin asked the camera to follow
        float lostAt = -1f;      // Time.unscaledTime when modTarget was lost (impact, despawn, track loss)
        GlobalPosition lingerFrom; // camera position when the hold started
        bool inputsLocked;       // the plugin set CameraStateManager.allowInputs = false for the hold
        bool savedAllowInputs;
        string label = "";
        bool vrDetected;

        void Awake()
        {
            Log = Logger;
            // Nuclear Option's first scene load destroys the BepInEx manager object (and this plugin with it) unless
            // the object is hidden, as BepInEx's HideManagerGameObject option does. Without this, a default BepInEx
            // install loads the plugin and then loses it before the main menu, so no key does anything.
            if ((gameObject.hideFlags & HideFlags.DontSave) == 0) gameObject.hideFlags |= HideFlags.HideAndDontSave;
            pluginEnabled = Config.Bind("General", "Enabled", true, "Master switch.");
            missileBinding = new Binding(Config, "Follow / cycle missile", "CycleMissile", KeyCode.RightBracket,
                "Toggle: follow your first missile in flight, then the next one on each press, then back to your aircraft.", "AB6", 12);
            targetBinding = new Binding(Config, "Follow target", "ViewTarget", KeyCode.LeftBracket,
                "Toggle: follow your primary target (only with an accurate track); press again to return to your aircraft.", "AB6", 14);
            returnBinding = new Binding(Config, "Return to aircraft", "Return", KeyCode.Backslash,
                "Return to your aircraft and the camera view you had before.", "", 0);
            bindings = new[] { missileBinding, targetBinding, returnBinding };
            settingsKey = Config.Bind("Keys", "OpenSettings", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Open the Missile Cam key binding window. The window also opens from Missile Cam Keys in the game's Controls menu.");
            lingerSeconds = Config.Bind("Behaviour", "LingerSeconds", 2.5f, new ConfigDescription(
                "After the followed missile hits or disappears, hold the camera where it is for this long (0 to 5 seconds). " +
                "Camera move input is off during the hold.", new AcceptableValueRange<float>(0f, MaxLingerSeconds)));
            autoNextMissile = Config.Bind("Behaviour", "AutoNextMissile", true,
                "After the linger, follow your next missile in flight instead of returning, if there is one.");
            allowTargetView = Config.Bind("Behaviour", "AllowTargetView", true,
                "Allow the ViewTarget key at all.");
            allowInVR = Config.Bind("Behaviour", "AllowInVR", true,
                "Allow the missile cam when the NOVR plugin is loaded. On by default; VR use is still untested.");
            showLabel = Config.Bind("Display", "ShowLabel", true, "Small text label while the missile cam is active.");
            releaseGameBindings = Config.Bind("Joystick", "ReleaseGameBindings", false,
                "Opt-in, once: remove the game's own bindings on the missile cam joystick buttons, so a press does " +
                "not also trigger a game action. Saves a backup XML to BepInEx/config first, then sets itself to false.");

            settings = new SettingsWindow(Config, bindings);
            try
            {
                SpectateGuard.Install(new Harmony(Guid + ".spectate"), () => Active, OnSpectateHopBlocked);
            }
            catch (System.Exception e) { Log.LogError($"Spectator hop guard failed to install: {e}"); }
            vrDetected = HasPluginLike("novr");
            SceneManager.activeSceneChanged += (_, __) => ResetState("scene changed");
            Log.LogInfo($"{Name} {Version} loaded. VR plugin detected: {vrDetected}. Spectator hop guard: {(SpectateGuard.Installed ? "on" : "OFF")}.");
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
            if (Binding.KeyPressed(settingsKey.Value) && !InputFieldChecker.InsideInputField) settings.Toggle();
            settings.Update();
            if (releaseGameBindings.Value && Rewired.ReInput.isReady && GameManager.gameState.IsSingleOrMultiplayer()) ReleaseGameBindings();
            bool listen = !settings.IsOpen;
            bool missilePress = listen && missileBinding.Pressed();
            bool targetPress = listen && targetBinding.Pressed();
            bool returnPress = listen && returnBinding.Pressed();
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

        // ---- joystick ----

        void ReleaseGameBindings()
        {
            releaseGameBindings.Value = false;
            Config.Save();
            try
            {
                bool changed = false;
                foreach (var b in bindings)
                {
                    var stick = b.FindJoystick();
                    if (stick == null || b.Button.Value > stick.buttonCount) continue;
                    var map = Rewired.ReInput.players.GetPlayer(0).controllers.maps.GetMap(Rewired.ControllerType.Joystick, stick.id, 0, 0);
                    if (map == null) continue;
                    int element = stick.Buttons[b.Button.Value - 1].elementIdentifier.id;
                    var doomed = map.AllMaps.Where(m => m.elementType == Rewired.ControllerElementType.Button && m.elementIdentifierId == element).ToArray();
                    if (doomed.Length == 0) continue;
                    File.WriteAllText(Path.Combine(Paths.ConfigPath, $"missilecam-joystick-{stick.id}-before-release.xml"), map.ToXmlString());
                    foreach (var m in doomed)
                    {
                        var action = Rewired.ReInput.mapping.GetAction(m.actionId);
                        Log.LogInfo($"Removing game binding {m.elementIdentifierName} -> {(action != null ? action.name : m.actionId.ToString())}.");
                        map.DeleteElementMap(m.id);
                        changed = true;
                    }
                }
                if (changed) { Rewired.ReInput.userDataStore.SavePlayerData(0); PlayerPrefs.Save(); }
                else Log.LogInfo("No game bindings on the missile cam joystick buttons.");
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
            UnlockMoveInputs();
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
            UnlockMoveInputs();
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
                if (followed == savedAircraft)
                {
                    // Back on your own aircraft (the game's own controls can do that): step aside.
                    ResetState("camera returned to aircraft");
                    return;
                }
                if (followed is Missile own && !own.disabled && own.ownerID == savedAircraft.persistentID)
                {
                    // Another of your own missiles: keep driving.
                    UnlockMoveInputs();
                    modTarget = own;
                    lostAt = -1f;
                    label = $"missile: {UnitLabel(own)}";
                    Log.LogInfo($"Camera moved to your own missile {UnitLabel(own)}; still following.");
                    return;
                }
                // Anything else while your aircraft flies (the game's spectator hop, or any other path) is spectating
                // a unit you could not follow in the unmodded game. Send the camera home instead of letting go.
                Log.LogWarning($"Camera moved to {UnitLabel(followed)} while you are flying. Missile Cam does not allow " +
                               "spectating other units; back to your aircraft.");
                Restore(cam, "spectator hop blocked");
                Flash("spectating is off while you fly", log: false);
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
            // switched to the free state at the last position. Hold there, then move on.
            if (followed == null || targetLost)
            {
                var here = cam.transform.GlobalPosition();
                if (lostAt < 0f) { lostAt = Time.unscaledTime; lingerFrom = here; label += " (lost)"; }
                // The free camera must not fly away from the impact point: move input off for the hold.
                LockMoveInputs(cam);
                float hold = Mathf.Clamp(lingerSeconds.Value, 0f, MaxLingerSeconds);
                bool drifted = FastMath.Distance(here, lingerFrom) > MaxLingerDrift;
                if (drifted) Log.LogInfo("Camera moved away from the impact point during the hold; ending the hold.");
                else if (Time.unscaledTime - lostAt < hold) return;
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

        void LockMoveInputs(CameraStateManager cam)
        {
            if (!inputsLocked)
            {
                savedAllowInputs = cam.allowInputs;
                inputsLocked = true;
            }
            cam.allowInputs = false;
        }

        void UnlockMoveInputs()
        {
            if (!inputsLocked) return;
            inputsLocked = false;
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam != null) cam.allowInputs = savedAllowInputs;
        }

        void OnSpectateHopBlocked()
        {
            Log.LogInfo("Change Spectate Faction ignored: Missile Cam has the camera and your aircraft is flying.");
            Flash("spectating is off while you fly", log: false);
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
        void Flash(string text, bool log = true) { flash = text; flashUntil = Time.unscaledTime + 1.5f; if (log) Log.LogInfo(text); }

        GUIStyle style;
        void OnGUI()
        {
            settings.OnGUI();
            if (!showLabel.Value) return;
            string text = Time.unscaledTime < flashUntil ? "MISSILE CAM  " + flash : (Active ? "MISSILE CAM  " + label : null);
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
