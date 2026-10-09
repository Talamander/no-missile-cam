using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Talamander.MissileCam
{
    /// <summary>
    /// One missile cam control: a keyboard key (with optional modifiers) and an optional joystick button.
    /// Joystick buttons are read straight from Rewired, so they need no game action.
    /// </summary>
    internal class Binding
    {
        public readonly string Label;
        public readonly ConfigEntry<KeyboardShortcut> Key;
        public readonly ConfigEntry<string> Device;
        public readonly ConfigEntry<int> Button;

        Rewired.Joystick joystick;
        string joystickFor;
        float nextSearch;

        public Binding(ConfigFile config, string label, string keyName, KeyCode defaultKey, string keyHelp,
            string device, int button)
        {
            Label = label;
            Key = config.Bind("Keys", keyName, new KeyboardShortcut(defaultKey), keyHelp);
            Device = config.Bind("Joystick", keyName + "Device", device,
                "Part of the joystick name as Rewired reports it (the MOZA AB6 is \"Gudsen MOZA AB6 FFB Base\"). Empty disables.");
            Button = config.Bind("Joystick", keyName + "Button", button,
                "Joystick button, 1-based (MOZA Cockpit / MOZA Button Viewer numbering). 0 disables.");
        }

        public bool Pressed() => KeyPressed(Key.Value) || JoystickPressed();

        /// <summary>
        /// Main key down this frame with its own modifiers held. Unlike KeyboardShortcut.IsDown, this does not
        /// reject the press while any other key or joystick button is held (throttle keys, MOZA switches that
        /// report as held buttons).
        /// </summary>
        public static bool KeyPressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !BepInEx.UnityInput.Current.GetKeyDown(shortcut.MainKey)) return false;
            foreach (var modifier in shortcut.Modifiers)
                if (!BepInEx.UnityInput.Current.GetKey(modifier)) return false;
            return true;
        }

        bool JoystickPressed()
        {
            int index = Button.Value - 1;
            var stick = FindJoystick();
            return stick != null && index >= 0 && index < stick.buttonCount && stick.GetButtonDown(index);
        }

        public Rewired.Joystick FindJoystick()
        {
            string want = Device.Value;
            if (string.IsNullOrEmpty(want) || Button.Value <= 0) return null;
            if (joystick != null && joystick.isConnected && joystickFor == want) return joystick;
            joystick = null;
            if (!Rewired.ReInput.isReady || Time.unscaledTime < nextSearch) return null;
            nextSearch = Time.unscaledTime + 3f;
            joystick = Rewired.ReInput.controllers.Joysticks.FirstOrDefault(j => j.name != null && j.name.Contains(want));
            joystickFor = want;
            if (joystick != null) Plugin.Log.LogInfo($"{Label}: button {Button.Value} on \"{joystick.name}\".");
            return joystick;
        }

        public string KeyText => Key.Value.MainKey == KeyCode.None ? "none" : Key.Value.ToString();

        public string JoystickText
        {
            get
            {
                if (string.IsNullOrEmpty(Device.Value) || Button.Value <= 0) return "none";
                return $"{Device.Value} button {Button.Value}";
            }
        }
    }
}
