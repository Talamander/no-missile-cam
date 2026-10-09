using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Talamander.MissileCam
{
    /// <summary>
    /// In-game key binding window. Opened from a "Missile Cam Keys" button that is added next to the game's
    /// Edit Bindings button in the Controls menu, or with the OpenSettings shortcut. Changes are saved to the
    /// BepInEx config file at once.
    /// </summary>
    internal class SettingsWindow
    {
        const float W = 760f, H = 330f;

        readonly Binding[] bindings;
        readonly ConfigFile config;
        bool open;
        Binding capturing;
        bool capturingJoystick;
        EventSystem disabledEventSystem;
        bool savedCursorVisible;
        CursorLockMode savedCursorLock;
        GUIStyle title, cell, note;

        static SettingsWindow instance;

        public SettingsWindow(ConfigFile config, Binding[] bindings)
        {
            this.config = config;
            this.bindings = bindings;
            instance = this;
            var harmony = new Harmony(Plugin.Guid);
            harmony.Patch(AccessTools.Method(typeof(ControlsMenu), "Start"),
                postfix: new HarmonyMethod(typeof(SettingsWindow), nameof(ControlsMenuStartPostfix)));
        }

        public bool IsOpen => open;

        public void Toggle() { if (open) Close(); else Open(); }

        void Open()
        {
            open = true;
            capturing = null;
            // uGUI keeps receiving clicks under an IMGUI window; pause it while the window is open.
            var es = EventSystem.current;
            if (es != null && es.enabled) { es.enabled = false; disabledEventSystem = es; }
            savedCursorVisible = Cursor.visible;
            savedCursorLock = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        void Close()
        {
            open = false;
            capturing = null;
            if (disabledEventSystem != null) disabledEventSystem.enabled = true;
            disabledEventSystem = null;
            Cursor.visible = savedCursorVisible;
            Cursor.lockState = savedCursorLock;
            config.Save();
        }

        // ---- Controls menu button ----

        static void ControlsMenuStartPostfix(ControlsMenu __instance)
        {
            try { instance?.AddMenuButton(__instance); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Could not add the Missile Cam Keys button: {e.Message}"); }
        }

        void AddMenuButton(ControlsMenu menu)
        {
            Button source = null;
            foreach (var b in menu.GetComponentsInChildren<Button>(true))
            {
                for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                    if (b.onClick.GetPersistentMethodName(i) == "EditBindings") { source = b; break; }
                if (source != null) break;
            }
            if (source == null) { Plugin.Log.LogWarning("Controls menu: Edit Bindings button not found; use the OpenSettings shortcut."); return; }

            var go = Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = "MissileCamKeysButton";
            go.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            var button = go.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Open);
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = "Missile Cam Keys";
            foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = "Missile Cam Keys";

            // Without a layout group the clone would sit on top of the original; put it just below.
            if (source.transform.parent.GetComponent<LayoutGroup>() == null)
            {
                var rt = (RectTransform)go.transform;
                var src = (RectTransform)source.transform;
                rt.anchoredPosition = src.anchoredPosition - new Vector2(0f, src.rect.height + 8f);
            }
            Plugin.Log.LogInfo("Controls menu: added Missile Cam Keys button.");
        }

        // ---- capture ----

        static readonly KeyCode[] Modifiers =
        {
            KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt,
        };

        public void Update()
        {
            if (!open || capturing == null) return;
            var input = BepInEx.UnityInput.Current;
            if (input.GetKeyDown(KeyCode.Escape)) { capturing = null; return; }

            if (!capturingJoystick)
            {
                foreach (var key in input.SupportedKeyCodes)
                {
                    if (key == KeyCode.None || key >= KeyCode.Mouse0 || System.Array.IndexOf(Modifiers, key) >= 0) continue;
                    if (!input.GetKeyDown(key)) continue;
                    var held = new List<KeyCode>();
                    foreach (var m in Modifiers) if (input.GetKey(m)) held.Add(m);
                    capturing.Key.Value = new KeyboardShortcut(key, held.ToArray());
                    capturing = null;
                    config.Save();
                    return;
                }
                return;
            }

            if (!Rewired.ReInput.isReady) return;
            foreach (var stick in Rewired.ReInput.controllers.Joysticks)
                for (int i = 0; i < stick.buttonCount; i++)
                {
                    if (!stick.GetButtonDown(i)) continue;
                    capturing.Device.Value = stick.name;
                    capturing.Button.Value = i + 1;
                    capturing = null;
                    config.Save();
                    return;
                }
        }

        // ---- drawing ----

        public void OnGUI()
        {
            if (!open) return;
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            var saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale, sh = Screen.height / scale;
            if (title == null)
            {
                title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
                cell = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                note = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            }
            var r = new Rect((sw - W) / 2f, (sh - H) / 2f, W, H);
            GUI.Box(r, GUIContent.none);
            GUI.Box(r, GUIContent.none); // twice for a more opaque background
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 24));
            GUILayout.Label("Missile Cam controls", title);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Action", cell, GUILayout.Width(200));
            GUILayout.Label("Keyboard", cell, GUILayout.Width(250));
            GUILayout.Label("Joystick button", cell);
            GUILayout.EndHorizontal();
            foreach (var b in bindings)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(b.Label, cell, GUILayout.Width(200));
                string keyText = capturing == b && !capturingJoystick ? "press a key..." : b.KeyText;
                if (GUILayout.Button(keyText, GUILayout.Width(190), GUILayout.Height(28))) { capturing = b; capturingJoystick = false; }
                if (GUILayout.Button("clear", GUILayout.Width(52), GUILayout.Height(28)))
                { b.Key.Value = KeyboardShortcut.Empty; capturing = null; config.Save(); }
                GUILayout.Space(8);
                string joyText = capturing == b && capturingJoystick ? "press a button..." : b.JoystickText;
                if (GUILayout.Button(joyText, GUILayout.Width(170), GUILayout.Height(28))) { capturing = b; capturingJoystick = true; }
                if (GUILayout.Button("clear", GUILayout.Width(52), GUILayout.Height(28)))
                { b.Button.Value = 0; capturing = null; config.Save(); }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);
            GUILayout.Label("Click a binding, then press the key or joystick button. Escape cancels. A joystick button " +
                "still runs any game action bound to it; clear that in Edit Bindings.", note);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(120), GUILayout.Height(30))) Close();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.matrix = saved;
        }
    }
}
