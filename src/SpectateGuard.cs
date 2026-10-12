using System;
using HarmonyLib;

namespace Talamander.MissileCam
{
    /// <summary>
    /// Blocks the game's spectator hop while Missile Cam owns the camera.
    ///
    /// CameraOrbitState.Inputs lets the player jump the camera to the nearest tracked aircraft of another faction
    /// ("Change Spectate Faction") whenever the orbit camera is not on the local aircraft:
    ///   if (!IsLocalAircraft(cam.followingUnit) &amp;&amp; GetButtonDown("Change Spectate Faction") &amp;&amp; TryGetNearestEnemyAircraft(out t))
    ///       SetFollowingUnit(t);
    /// While the mod follows a missile or the target, that condition is true, so without this guard a flying player
    /// could hop to units they cannot reach in the unmodded game (orbiting your own aircraft keeps the hop off).
    ///
    /// The guard marks the span of Inputs and makes TryGetNearestEnemyAircraft report "none" inside it while the mod
    /// owns the camera. Inside Inputs that call is only reached after the hop key went down, so nothing else changes.
    /// Outside Inputs (CameraMotion's "Spectate Next Aircraft" look-at, which only turns the camera) it is left alone.
    /// Plugin.Supervise is the second layer: any other switch to a unit that is not yours sends you back to your aircraft.
    /// </summary>
    internal static class SpectateGuard
    {
        [ThreadStatic] static bool insideOrbitInputs;
        static Func<bool> modOwnsCamera;
        static Action onBlocked;

        public static bool Installed { get; private set; }

        public static void Install(Harmony harmony, Func<bool> ownsCamera, Action blocked)
        {
            modOwnsCamera = ownsCamera;
            onBlocked = blocked;
            var inputs = AccessTools.Method(typeof(CameraOrbitState), "Inputs");
            var nearest = AccessTools.Method(typeof(CameraOrbitState), "TryGetNearestEnemyAircraft");
            if (inputs == null || nearest == null)
            {
                Plugin.Log.LogWarning("Spectator hop guard not installed (game methods not found). " +
                                      "The per-frame check still returns the camera to your aircraft.");
                return;
            }
            harmony.Patch(inputs,
                prefix: new HarmonyMethod(typeof(SpectateGuard), nameof(InputsPrefix)),
                finalizer: new HarmonyMethod(typeof(SpectateGuard), nameof(InputsFinalizer)));
            harmony.Patch(nearest, prefix: new HarmonyMethod(typeof(SpectateGuard), nameof(NearestEnemyPrefix)));
            Installed = true;
        }

        static void InputsPrefix() => insideOrbitInputs = true;

        static Exception InputsFinalizer(Exception __exception)
        {
            insideOrbitInputs = false;
            return __exception;
        }

        static bool NearestEnemyPrefix(ref Unit __0, ref bool __result)
        {
            if (!insideOrbitInputs || modOwnsCamera == null || !modOwnsCamera()) return true;
            __0 = null;
            __result = false;
            onBlocked?.Invoke();
            return false;
        }
    }
}
