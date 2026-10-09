using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MW2RoR2
{
    /// The player's own MW2 PC key binds (players/config_mp.cfg, or IW4x's iw4x_config.cfg), so
    /// MW2 mode uses the keys their hands already know: "+actionslot 4" = killstreak (MW2's
    /// _killstreaks.gsc gives streaks action slot 4), "+frag" = lethal, "+smoke" = tactical,
    /// "+breath_sprint" / "+holdbreath" = hold breath. Mouse binds are skipped where RoR2 owns the
    /// button (MOUSE3 = ping).
    static class Mw2Binds
    {
        public static readonly Dictionary<string, KeyCode> Keys = new Dictionary<string, KeyCode>();
        public static string Source;

        public static void Load(string commonMpPath)
        {
            try
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(commonMpPath)));
                var players = Path.Combine(root ?? "", "players");
                if (!Directory.Exists(players)) return;
                foreach (var name in new[] { "config_mp.cfg", "iw4x_config.cfg", "config.cfg" })
                {
                    var f = Path.Combine(players, name);
                    if (!File.Exists(f)) continue;
                    Parse(File.ReadAllLines(f));
                    Source = f;
                    break;
                }
                if (Source != null) Plugin.Log.LogInfo($"MW2 binds from {Source}: " + string.Join(", ", Describe()));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 binds: {e.Message}"); }
        }

        static readonly Regex Bind = new Regex("^\\s*bind\\s+(\\S+)\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);

        static void Parse(string[] lines)
        {
            foreach (var line in lines)
            {
                var m = Bind.Match(line);
                if (!m.Success) continue;
                var key = ToKey(m.Groups[1].Value);
                if (key == KeyCode.None || key == KeyCode.Mouse2) continue; // MOUSE3 = RoR2 ping
                string cmd = m.Groups[2].Value.Trim().ToLowerInvariant();
                // First keyboard bind wins over mouse binds for the same command.
                if (Keys.TryGetValue(cmd, out var have) && have < KeyCode.Mouse0 && key >= KeyCode.Mouse0) continue;
                if (!Keys.ContainsKey(cmd) || key < KeyCode.Mouse0) Keys[cmd] = key;
            }
        }

        // Movement keys from MW2's binds (playtest 10-06-26: RoR2's Ctrl sprint fought a friend's MW2 Ctrl
        // stance - crouch and sprint at once, and sprint stands you up). Keyboard binds only.
        static KeyCode? Keyboard(KeyCode? k) => k.HasValue && k.Value < KeyCode.Mouse0 ? k : null;
        public static KeyCode? Sprint => Keyboard(For("+breath_sprint", "+sprint"));
        public static KeyCode? Crouch => Keyboard(For("togglecrouch", "+movedown", "lowerstance"));
        public static KeyCode? Prone => Keyboard(For("toggleprone", "+prone", "goprone"));
        /// MW2's use key (+activate: F on PC) - the killstreak rides' thermal toggle.
        public static KeyCode? Use => Keyboard(For("+activate", "+usereload"));

        public static KeyCode? For(params string[] commands)
        {
            foreach (var c in commands) if (Keys.TryGetValue(c, out var k)) return k;
            return null;
        }

        /// MW2's key name for HUD hints ("6", "SHIFT", "G").
        public static string Label(KeyCode k)
        {
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            switch (k)
            {
                case KeyCode.LeftShift: case KeyCode.RightShift: return "SHIFT";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "CTRL";
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "ALT";
                case KeyCode.Mouse0: return "MOUSE1";
                case KeyCode.Mouse1: return "MOUSE2";
                case KeyCode.Mouse3: return "MOUSE4";
                case KeyCode.Mouse4: return "MOUSE5";
                default: return k.ToString().ToUpperInvariant();
            }
        }

        static IEnumerable<string> Describe()
        {
            foreach (var c in new[] { "+actionslot 4", "+frag", "+smoke", "+breath_sprint", "+holdbreath", "togglecrouch", "toggleprone", "+movedown", "+prone" })
                if (Keys.TryGetValue(c, out var k)) yield return $"{c}={Label(k)}";
        }

        static KeyCode ToKey(string name)
        {
            string n = name.Trim().ToUpperInvariant();
            if (n.Length == 1 && n[0] >= '0' && n[0] <= '9') return KeyCode.Alpha0 + (n[0] - '0');
            if (n.Length == 1 && n[0] >= 'A' && n[0] <= 'Z') return KeyCode.A + (n[0] - 'A');
            if (n.StartsWith("F") && int.TryParse(n.Substring(1), out var fk) && fk >= 1 && fk <= 15) return KeyCode.F1 + (fk - 1);
            if (n.StartsWith("MOUSE") && int.TryParse(n.Substring(5), out var mb) && mb >= 1 && mb <= 7) return KeyCode.Mouse0 + (mb - 1);
            switch (n)
            {
                case "SHIFT": return KeyCode.LeftShift;
                case "CTRL": return KeyCode.LeftControl;
                case "ALT": return KeyCode.LeftAlt;
                case "SPACE": return KeyCode.Space;
                case "TAB": return KeyCode.Tab;
                case "ENTER": return KeyCode.Return;
                case "BACKSPACE": return KeyCode.Backspace;
                case "CAPSLOCK": return KeyCode.CapsLock;
                case "UPARROW": return KeyCode.UpArrow;
                case "DOWNARROW": return KeyCode.DownArrow;
                case "LEFTARROW": return KeyCode.LeftArrow;
                case "RIGHTARROW": return KeyCode.RightArrow;
                default: return KeyCode.None;
            }
        }
    }
}
