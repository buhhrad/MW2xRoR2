using System;
using System.Collections.Generic;
using HarmonyLib;
using RoR2;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MW2RoR2
{
    /// MW2's front-end menus (Create-a-Class and its popups) run by mw2sim from the user's
    /// localized_ui_mp.ff: their scripts, expressions, tables and the saved loadouts all live on the
    /// native side. This hosts them: feeds the cursor and keys, draws the commands with the HUD
    /// drawer, plays the sounds the scripts ask for, and keeps RoR2's UI from reacting underneath.
    static unsafe class Mw2Menus
    {
        public static bool Ready { get; private set; }

        public const uint KeyClick = 1, KeyEsc = 2, KeyUp = 4, KeyDown = 8, KeyEnter = 16, KeyF1 = 32;

        /// Press menu keys from code (the playtest pilot), applied on the next drawn frame.
        public static void Inject(uint keys) => pendingKeys |= keys;

        static readonly Mw2HudCmd[] cmds = new Mw2HudCmd[2048];
        static readonly byte[] buf = new byte[4096];
        static uint pendingKeys;
        static bool wasOpen;
        static readonly List<BaseInputModule> disabled = new List<BaseInputModule>();

        /// Load the menus and the loadouts saved at `pdataPath`.
        public static void Init(string pdataPath, Harmony harmony)
        {
            try
            {
                var b = System.Text.Encoding.UTF8.GetBytes(pdataPath ?? "");
                uint n;
                fixed (byte* p = b) n = Native.mw2_menu_init(p, (UIntPtr)b.Length);
                Ready = n > 0;
                Plugin.Log.LogInfo($"MW2 menus: {n} (loadouts: {pdataPath})");
                // Esc belongs to the MW2 menu while one is open, not RoR2's pause screen.
                var toggle = AccessTools.Method("RoR2.PauseManager:TogglePauseScreen");
                if (toggle != null) harmony.Patch(toggle, prefix: new HarmonyMethod(typeof(Mw2Menus), nameof(BlockPause)));
                else Plugin.Log.LogWarning("PauseManager.TogglePauseScreen not found; Esc in MW2 menus also opens RoR2's pause screen.");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 menus unavailable: {e.Message}"); }
        }

        static bool BlockPause() => !IsOpen;

        public static bool IsOpen => Ready && Native.mw2_menu_depth() > 0;

        /// The menu the player opened (cac_popup for Create-a-Class, changeclass in a match).
        static string opened;

        public static bool Open(string name)
        {
            if (!Ready) return false;
            if (Native.mw2_menu_depth() == 0) opened = name;
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return Native.mw2_menu_open(p, (UIntPtr)b.Length) == 1;
        }

        /// scriptMenuResponse from the menus (changeclass: "class0".."class4", "custom1".."custom10", "back").
        public static event Action<string> Response;
        /// A menu answer without clicking (the pilot).
        internal static void SimulateResponse(string r) => Response?.Invoke(r);

        static string Text(uint n) => n == 0 || n > buf.Length ? "" : System.Text.Encoding.UTF8.GetString(buf, 0, (int)n);

        /// MW2's localized text for a key (MP_MATCH_STARTING_IN), the key itself if unknown.
        public static string Localize(string key)
        {
            if (!Ready) return key;
            var b = System.Text.Encoding.UTF8.GetBytes(key);
            uint n;
            fixed (byte* k = b) fixed (byte* p = buf) n = Native.mw2_menu_localize(k, (UIntPtr)b.Length, p, (uint)buf.Length);
            return Text(n);
        }

        /// tableLookup over the zones' string tables ("" if not found).
        public static string TableLookup(string table, int col0, string key, int col)
        {
            if (!Ready) return "";
            var t = System.Text.Encoding.UTF8.GetBytes(table);
            var k = System.Text.Encoding.UTF8.GetBytes(key);
            uint n;
            fixed (byte* pt = t) fixed (byte* pk = k) fixed (byte* p = buf) n = Native.mw2_menu_table_lookup(pt, (UIntPtr)t.Length, (uint)col0, pk, (UIntPtr)k.Length, (uint)col, p, (uint)buf.Length);
            return Text(n);
        }

        /// Every row's cell in one column of a string table (perkTable names ...).
        public static string[] TableColumn(string table, int col)
        {
            if (!Ready) return new string[0];
            var t = System.Text.Encoding.UTF8.GetBytes(table);
            var big = new byte[64 * 1024];
            uint n;
            fixed (byte* pt = t) fixed (byte* p = big) n = Native.mw2_menu_table_column(pt, (UIntPtr)t.Length, (uint)col, p, (uint)big.Length);
            if (n == 0 || n > big.Length) return new string[0];
            return System.Text.Encoding.UTF8.GetString(big, 0, (int)n).Split((char)10);
        }

        /// MW2's isItemUnlocked (unlockTable: rank and challenge).
        public static bool ItemUnlocked(string item)
        {
            if (!Ready || string.IsNullOrEmpty(item)) return true;
            var b = System.Text.Encoding.UTF8.GetBytes(item);
            fixed (byte* p = b) return Native.mw2_menu_item_unlocked(p, (UIntPtr)b.Length) != 0;
        }

        public static string PlayerData(string path)
        {
            if (!Ready) return "";
            var b = System.Text.Encoding.UTF8.GetBytes(path);
            uint n;
            fixed (byte* pp = b) fixed (byte* o = buf) n = Native.mw2_menu_player_data(pp, (UIntPtr)b.Length, o, (uint)buf.Length);
            return Text(n);
        }

        /// Switch to another player data file (Mw2Progress profiles); the current one is saved.
        public static bool SwitchPlayerData(string pdataPath)
        {
            if (!Ready || string.IsNullOrEmpty(pdataPath)) return false;
            var b = System.Text.Encoding.UTF8.GetBytes(pdataPath);
            fixed (byte* p = b) return Native.mw2_menu_switch_pdata(p, (UIntPtr)b.Length) == 1;
        }

        public static void SetPlayerData(string path, string value)
        {
            if (!Ready) return;
            var p = System.Text.Encoding.UTF8.GetBytes(path);
            var v = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            fixed (byte* pp = p) fixed (byte* pv = v) Native.mw2_menu_set_player_data(pp, (UIntPtr)p.Length, pv, (UIntPtr)v.Length);
        }

        public static void SetFaction(string faction)
        {
            if (!Ready) return;
            var b = System.Text.Encoding.UTF8.GetBytes(faction ?? "");
            fixed (byte* p = b) Native.mw2_menu_set_faction(p, (UIntPtr)b.Length);
        }

        public static void CloseAll()
        {
            if (Ready) Native.mw2_menu_close_all();
        }

        public static void SetLocal(string name, int value)
        {
            if (!Ready) return;
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) Native.mw2_menu_set_local_int(p, (UIntPtr)b.Length, value);
        }

        public static int Local(string name)
        {
            if (!Ready) return -1;
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return Native.mw2_menu_local_int(p, (UIntPtr)b.Length);
        }

        /// Custom class `index` (0-based): primary, secondary, lethal, tactical, tactical count,
        /// killstreak 1-3, perk 1-3, death streak. Null if the menus aren't loaded.
        public static string[] ClassLoadout(int index)
        {
            if (!Ready || index < 0) return null;
            uint n;
            fixed (byte* p = buf) n = Native.mw2_menu_class_loadout((uint)index, p, (uint)buf.Length);
            if (n == 0 || n > buf.Length) return null;
            return System.Text.Encoding.UTF8.GetString(buf, 0, (int)n).Split((char)10);
        }

        /// Call once per frame (Update).
        public static void Update()
        {
            bool open = IsOpen;
            if (open != wasOpen)
            {
                wasOpen = open;
                if (open) Grab();
                else Release();
            }
            // What the last frame's scripts queued (a menu closing itself still answers).
            if (Ready) { PlaySounds(); Responses(); }
            if (!open) return;
            if (Input.GetMouseButtonDown(0)) pendingKeys |= KeyClick;
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) pendingKeys |= KeyEsc;
            if (Input.GetKeyDown(KeyCode.UpArrow)) pendingKeys |= KeyUp;
            if (Input.GetKeyDown(KeyCode.DownArrow)) pendingKeys |= KeyDown;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) pendingKeys |= KeyEnter;
            if (Input.GetKeyDown(KeyCode.F1)) pendingKeys |= KeyF1; // menu execKey (Create-a-Streak: clear)
            // The class rename field (pc_rename): what's typed goes there (Unity's backspace is U+0008;
            // Enter is the menus' own key and confirms).
            Editing = Native.mw2_menu_editing() == 1;
            if (Editing && !string.IsNullOrEmpty(Input.inputString)) Type(Input.inputString);
        }

        /// An MW2 edit field has the keyboard: the mod's hotkeys stay quiet (In.Typing).
        public static bool Editing { get; private set; }

        /// Text into the open edit field (also the pilot's way in).
        public static bool Type(string text)
        {
            if (!Ready || string.IsNullOrEmpty(text)) return false;
            var b = System.Text.Encoding.UTF8.GetBytes(text);
            fixed (byte* p = b) return Native.mw2_menu_text(p, (UIntPtr)b.Length) == 1;
        }

        static void Responses()
        {
            uint n;
            fixed (byte* p = buf) n = Native.mw2_menu_drain(1, p, (uint)buf.Length);
            if (n == 0 || n > buf.Length) return;
            foreach (var r in Text(n).Split((char)10))
                if (r.Length > 0)
                {
                    Plugin.Log.LogInfo($"MW2 menu response: {r}");
                    try { Response?.Invoke(r); } catch (Exception e) { Plugin.Log.LogWarning($"MW2 menu response {r}: {e.Message}"); }
                }
        }

        /// After RoR2 has set the cursor for the frame: free and hidden (MW2 draws its own).
        public static void LateUpdate()
        {
            if (!wasOpen) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = Mw2Icons.Get("ui_cursor") == null;
        }

        /// OnGUI, after everything else (the menus cover the screen).
        public static void Draw()
        {
            if (!Ready || Event.current == null || Event.current.type != EventType.Repaint || Native.mw2_menu_depth() == 0) return;
            // In the lobby MW2 draws its menus over the main menu's backdrop (main_text's first 9 items:
            // background, drifting clouds, mp image, glow, gradient); in a match over the game.
            SetBackdrop(Plugin.CurrentBody() == null ? "main_text" : "", 9);
            var m = Event.current.mousePosition;
            uint n;
            fixed (Mw2HudCmd* c = cmds) n = Native.mw2_menu_frame(Screen.width, Screen.height, (int)(Time.unscaledTime * 1000f), m.x, m.y, pendingKeys, c, (uint)cmds.Length);
            pendingKeys = 0;
            Mw2MenuHud.DrawCmds(cmds, n);
            DrawCursor(m);
        }

        static string backdrop;

        static void SetBackdrop(string menu, uint count)
        {
            if (backdrop == menu) return;
            backdrop = menu;
            var b = System.Text.Encoding.UTF8.GetBytes(menu);
            fixed (byte* p = b) Native.mw2_menu_set_backdrop(p, (UIntPtr)b.Length, count);
        }

        static void DrawCursor(Vector2 at)
        {
            var tex = Mw2Icons.Get("ui_cursor");
            if (tex == null) return;
            // IW4 draws the cursor 32 units square in the 640x480 space, centred on the mouse point
            // (cursorx - 16, cursory - 16): ui_cursor's arrow tip is the image centre (30,30 of 64).
            float size = 32f * Screen.height / 480f;
            GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), tex);
        }

        static void PlaySounds()
        {
            uint n;
            fixed (byte* p = buf) n = Native.mw2_menu_drain(0, p, (uint)buf.Length);
            if (n == 0 || n > buf.Length) return;
            foreach (var alias in System.Text.Encoding.UTF8.GetString(buf, 0, (int)n).Split((char)10))
                if (alias.Length > 0) Mw2Audio.PlayUi(alias);
        }

        /// RoR2's uGUI would also take the clicks meant for the MW2 menu: its event systems sleep
        /// while one is open.
        static RoR2.UI.MPEventSystem focused;

        /// What RoR2's CursorOpener does (it needs a HUD's event-system locator to work): count one
        /// more cursor opener on the local user's event system, which makes RoR2 treat the UI as
        /// focused and stop the camera and the survivor's input.
        static void OpenCursor(int delta)
        {
            var es = delta > 0 ? LocalUserManager.GetFirstLocalUser()?.eventSystem : focused;
            if (es == null) return;
            var prop = AccessTools.Property(typeof(RoR2.UI.MPEventSystem), "cursorOpenerCount");
            if (prop?.GetSetMethod(true) == null) return;
            int n = (int)prop.GetValue(es);
            prop.GetSetMethod(true).Invoke(es, new object[] { Mathf.Max(0, n + delta) });
            focused = delta > 0 ? es : null;
            var user = LocalUserManager.GetFirstLocalUser();
            Plugin.Log.LogInfo($"MW2 menu cursor: openers {n} -> {prop.GetValue(es)}, cursor visible {es.isCursorVisible}, UI focused {user?.isUIFocused}");
        }

        static void Grab()
        {
            disabled.Clear();
            // In a run: RoR2's own UI-focus (what its pause / inventory screens use) stops the camera
            // and the survivor's input. In the lobby the cursor is already RoR2's, so its UI sleeps instead.
            if (Plugin.CurrentBody() != null)
            {
                OpenCursor(1);
                return;
            }
            // Only its input modules: with the EventSystems themselves off, every survivor icon's
            // GetLocalUser threw each frame (220k errors in one lobby, playtest 10-03-26).
            foreach (var es in UnityEngine.Object.FindObjectsOfType<EventSystem>())
                foreach (var m in es.GetComponents<BaseInputModule>())
                {
                    if (!m.enabled) continue;
                    m.enabled = false;
                    disabled.Add(m);
                }
        }

        static void Release()
        {
            if (focused != null) OpenCursor(-1);
            Mw2LoadoutRows.Refresh(); // the Loadout tab's Class / Killstreaks rows
            foreach (var m in disabled)
                if (m != null) m.enabled = true;
            disabled.Clear();
            if (opened != "cac_popup") return;
            // The class last opened in Create-a-Class is the one you play.
            int cls = Local("classIndex");
            if (cls >= 0 && cls < 10 && Plugin.Instance.Class.Value != cls + 1)
            {
                Plugin.Instance.Class.Value = cls + 1;
                Plugin.Log.LogInfo($"MW2 class: Custom Class {cls + 1}");
            }
        }
    }
}
