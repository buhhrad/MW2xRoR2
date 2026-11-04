using System.Linq;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// Inputs the mod reads, with an override the playtest pilot can drive (dev builds; DevOff.cs in a
    /// release). Real input still works.
    static class In
    {
        /// The mod's keyboard keys, except while RoR2's chat or console has the keyboard (typing "gg"
        /// isn't a grenade).
        public static bool Key(KeyCode k) => !Typing && Input.GetKey(k);
        public static bool KeyDown(KeyCode k) => !Typing && Input.GetKeyDown(k);

        static int typingFrame = -1;
        static bool typing;
        static float nextChatScan;
        static RoR2.UI.ChatBox[] chats;
        static readonly System.Reflection.FieldInfo showInput = HarmonyLib.AccessTools.Field(typeof(RoR2.UI.ChatBox), "_showInput");
        public static bool Typing
        {
            get
            {
                if (Time.frameCount == typingFrame) return typing;
                typingFrame = Time.frameCount;
                typing = false;
                if (RoR2.UI.ConsoleWindow.instance != null || Mw2Menus.Editing || Mw2Cinema.InputBlocked) return typing = true;
                if (Time.unscaledTime >= nextChatScan) { nextChatScan = Time.unscaledTime + 1f; chats = UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>(); }
                if (chats != null && showInput != null) foreach (var c in chats) if (c != null && (bool)showInput.GetValue(c)) return typing = true;
                return typing;
            }
        }

        public static bool Attack(InputBankTest b) => (b != null && b.skill1.down) || Mw2Pilot.Attack;
        public static bool AttackPressed(InputBankTest b) => (b != null && b.skill1.justPressed) || Mw2Pilot.Consume(ref Mw2Pilot.AttackPulse);
        public static bool Ads(InputBankTest b) => (b != null && b.skill2.down) || Mw2Pilot.Ads;
        /// MW2's own sprint key when their MW2 binds name one; else RoR2's sprint - but never while the
        /// crouch or prone key is down (RoR2's default Ctrl sprint against an MW2 Ctrl stance).
        public static bool Sprint(InputBankTest b)
        {
            if (Mw2Pilot.Sprint) return true;
            if (Mw2Binds.Sprint is KeyCode s) return Key(s);
            return b != null && b.sprint.down && !AnyKey(CrouchKeys) && !AnyKey(ProneKeys);
        }

        /// Crouch / prone: every key their MW2 binds give the stance, plus the config's key when it was
        /// changed from the default or MW2 has none (GitHub #1: keys set in the .cfg did nothing while
        /// MW2's binds were read). A key on both is crouch.
        public static KeyCode[] CrouchKeys { get { Stances(); return crouchKeys; } }
        public static KeyCode[] ProneKeys { get { Stances(); return proneKeys; } }
        static KeyCode[] crouchKeys = new KeyCode[0], proneKeys = new KeyCode[0];
        static KeyCode stanceCfgCrouch = KeyCode.None, stanceCfgProne = KeyCode.None;
        /// MW2's binds were (re)read: work the stance keys out again.
        internal static void ResetStances() => stancesBuilt = false;
        static bool stancesBuilt;

        static void Stances()
        {
            var p = Plugin.Instance;
            if (p.CrouchKey.Value == stanceCfgCrouch && p.ProneKey.Value == stanceCfgProne && stancesBuilt) return;
            stancesBuilt = true;
            stanceCfgCrouch = p.CrouchKey.Value; stanceCfgProne = p.ProneKey.Value;
            var crouch = new System.Collections.Generic.List<KeyCode>(Mw2Binds.CrouchKeys);
            if (crouch.Count == 0 || !Equals(p.CrouchKey.Value, p.CrouchKey.DefaultValue)) crouch.Add(p.CrouchKey.Value);
            var prone = new System.Collections.Generic.List<KeyCode>(Mw2Binds.ProneKeys);
            if (prone.Count == 0 || !Equals(p.ProneKey.Value, p.ProneKey.DefaultValue)) prone.Add(p.ProneKey.Value);
            prone.RemoveAll(k => k == KeyCode.None || crouch.Contains(k));
            crouch.RemoveAll(k => k == KeyCode.None);
            crouchKeys = crouch.Distinct().ToArray();
            proneKeys = prone.Distinct().ToArray();
            if (proneKeys.Length == 0) Plugin.Log.LogWarning($"MW2 stance keys: no prone key ({p.ProneKey.Value} is crouch). Set Keys.ProneKey in com.buhhrad.mw2ror2.cfg or bind toggleprone in MW2.");
            Plugin.Log.LogInfo($"MW2 stance keys: crouch {string.Join("/", crouchKeys.Select(Mw2Binds.Label))}, prone {string.Join("/", proneKeys.Select(Mw2Binds.Label))}");
        }

        public static bool AnyKey(KeyCode[] keys) { foreach (var k in keys) if (Key(k)) return true; return false; }
        public static bool AnyKeyDown(KeyCode[] keys) { foreach (var k in keys) if (KeyDown(k)) return true; return false; }
        public static bool Jump(InputBankTest b) => (b != null && b.jump.down) || Mw2Pilot.Jump;
        public static bool Interact(InputBankTest b) => (b != null && b.interact.down) || Mw2Pilot.Interact;
        public static bool InteractPressed(InputBankTest b) => (b != null && b.interact.justPressed) || Mw2Pilot.Consume(ref Mw2Pilot.InteractPulse);
        public static bool Reload => In.Key(KeyCode.R) || Mw2Pilot.Reload;
        public static bool StreakKeyDown => In.KeyDown(Plugin.Instance.StreakKey.Value) || Mw2Pilot.Consume(ref Mw2Pilot.StreakPulse);
        /// AC-130 gun pick: 0/1/2 or -1.
        public static int GunPick()
        {
            if (In.KeyDown(KeyCode.Alpha1)) return 0;
            if (In.KeyDown(KeyCode.Alpha2)) return 1;
            if (In.KeyDown(KeyCode.Alpha3)) return 2;
            int p = Mw2Pilot.GunPulse; Mw2Pilot.GunPulse = -1;
            return p;
        }

        /// World move vector: the pilot's forward/right relative to the aim, else RoR2's.
        public static Vector3 Move(InputBankTest b)
        {
            if (!Mw2Pilot.Moving || b == null) return b != null ? b.moveVector : Vector3.zero;
            // Relative to the aim the sim steers by (the pilot's own view could be off by 90 deg:
            // forward became a strafe, so it never sprinted).
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            var f = Mw2Pilot.MoveAlongView && bridge != null && bridge.ViewFirstPerson ? Mw2Pilot.ViewForward : b.aimDirection;
            f.y = 0f; f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            var r = new Vector3(f.z, 0f, -f.x);
            return Vector3.ClampMagnitude(f * Mw2Pilot.Fwd + r * Mw2Pilot.Right, 1f);
        }
    }
}
