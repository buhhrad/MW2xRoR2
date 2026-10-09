using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2 XP and rank: score values from _rank.gsc (team-based: kill 100), killstreak XP from
    /// killstreakTable.csv, ranks from mp/ranktable.csv. XP is kept in the plugin config so it
    /// carries across runs, like MW2's.
    unsafe class Mw2Progress
    {
        struct Popup { public int amount; public float at; }
        readonly List<Popup> popups = new List<Popup>();
        float promotedUntil;
        Mw2Rank rank;
        string rankIcon = "";
        bool haveRank;
        public Mw2Audio Audio;

        // Playtest pilot runs keep XP in memory only, so test kills never touch the player's saved rank.
        static int? pilotXp;
        static int Xp
        {
            get => Mw2Pilot.Active ? (pilotXp ?? (pilotXp = XpEntry.Value)).Value : XpEntry.Value;
            set { if (Mw2Pilot.Active) pilotXp = value; else XpEntry.Value = value; }
        }

        /// The XP MW2's menus rank against (unlocks).
        public static int CurrentXp => Xp;

        public static uint Score(string name)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return Native.mw2_score(p, (UIntPtr)b.Length);
        }

        void Refresh()
        {
            haveRank = RankFor(Xp, out rank, out rankIcon);
            dirty = false;
        }

        /// The rank for `xp` and its icon: the rank's own, or its prestige icon
        /// (mp/rankIconTable.csv: column 1 + prestige).
        public static bool RankFor(int xp, out Mw2Rank r, out string icon)
        {
            var buf = stackalloc byte[64];
            uint len = 0;
            bool ok = Native.mw2_rank_for_xp((uint)Mathf.Max(xp, 0), out r, buf, 64, out len) == 1;
            var bytes = new byte[Math.Min(len, 64u)];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = buf[i];
            icon = System.Text.Encoding.UTF8.GetString(bytes);
            int prestige = Prestige;
            if (ok && prestige > 0)
            {
                string p = Mw2Menus.TableLookup("mp/rankIconTable.csv", 0, r.id.ToString(), 1 + prestige);
                if (p.Length > 0) icon = p;
            }
            return ok;
        }

        /// The rank changed outside play (Unlock everything): read it again.
        public static void MarkDirty() => dirty = true;
        static bool dirty;

        // Two profiles (playtest 10-04-26): your own progress, and a separate one with everything
        // unlocked, so unlocking it all for a test doesn't end unlocking things the regular way.
        public static bool Unlocked => Plugin.Instance.UnlockedProfile.Value;

        /// MW2: Create-a-Class (your custom classes) unlocks at level 4; before that you play MW2's
        /// default classes (playtest 10-04-26).
        public const int CreateAClassLevel = 4, CreateAStreakLevel = 10;
        /// Your custom class slots (MW2: none before level 4, then 5, more with prestige).
        public static int CustomClassSlots => Mw2Menus.Ready ? (int)Native.mw2_menu_custom_class_slots() : 0;
        public static bool CreateAClassUnlocked => CustomClassSlots > 0;
        /// MW2: picking your own killstreaks (Create-a-Streak, unlockTable "cas") opens at level 10;
        /// before it you play UAV, Care Package, Predator Missile.
        public static bool CreateAStreakUnlocked => !Mw2Menus.Ready || Mw2Menus.ItemUnlocked("cas");
        public static readonly string[] DefaultStreaks = { "uav", "airdrop", "predator_missile" };
        /// MW2's default classes 4 and 5 (Scout Sniper, Riot Control: classes 14 / 15 here) open at
        /// level 3 (unlockTable "sniper"; the changeclass menu checks it for both).
        public static bool DefaultClassOpen(int cls) => cls < 14 || !Mw2Menus.Ready || Mw2Menus.ItemUnlocked("sniper");
        public static BepInEx.Configuration.ConfigEntry<int> XpEntry => Unlocked ? Plugin.Instance.UnlockedXp : Plugin.Instance.Xp;
        public static int Prestige => Unlocked ? 10 : Mathf.Clamp(Plugin.Instance.Prestige.Value, 0, 10);

        /// Switch profiles (the menus' player data and the XP). The unlocked one is filled the first
        /// time: every MW2 challenge done (attachments, camos, titles, emblems), the top rank's XP,
        /// prestige 10.
        public static bool SetProfile(bool unlocked)
        {
            if (unlocked == Unlocked) return true;
            string path = Plugin.Instance.PdataFor(unlocked);
            bool fresh = unlocked && !System.IO.File.Exists(path);
            if (!Mw2Menus.SwitchPlayerData(path)) return false;
            Plugin.Instance.UnlockedProfile.Value = unlocked;
            pilotXp = null;
            if (fresh || (unlocked && Plugin.Instance.UnlockedXp.Value == 0)) UnlockAll();
            Mw2Menus.SetPlayerData("experience", XpEntry.Value.ToString());
            MarkDirty();
            Plugin.Log.LogInfo($"MW2 profile: {(unlocked ? "unlocked" : "your own")} ({path}), {XpEntry.Value} XP, prestige {Prestige}");
            return true;
        }

        /// Every MW2 challenge done, the top rank's XP: the unlocked profile's contents.
        static bool UnlockAll()
        {
            uint challenges = 0;
            uint xp = Native.mw2_menu_unlock_all(&challenges);
            if (xp == 0) return false;
            Plugin.Instance.UnlockedXp.Value = (int)xp;
            Plugin.Log.LogInfo($"MW2 unlock everything: {challenges} challenges, {xp} XP, prestige 10");
            return true;
        }

        /// Kind of kill for XP: 0 a monster, 1 an elite, 2 a boss (it travels with the host's kill relay).
        public static byte KillKind(RoR2.CharacterBody victim) =>
            victim == null ? (byte)0 : victim.isChampion || victim.isBoss ? (byte)2 : victim.isElite ? (byte)1 : (byte)0;

        /// A kill's XP: MW2's (100) scaled down - RoR2 sends hundreds of monsters a run and ranks flew
        /// by (playtest 10-06-26: "its a bit tooo much because we kill soo many things") - elites x2,
        /// bosses x10 (Progress.KillXpScale).
        public static int KillXp(byte kind)
        {
            float xp = Score("kill") * Mathf.Max(Plugin.Instance.KillXpScale.Value, 0f) * (kind == 2 ? 10f : kind == 1 ? 2f : 1f);
            return Mathf.Max(1, Mathf.RoundToInt(xp));
        }

        public void Add(int amount)
        {
            if (amount <= 0) return;
            if (!haveRank || dirty) Refresh();
            uint before = rank.id;
            Xp = Xp + amount;
            Mw2Menus.SetPlayerData("experience", Xp.ToString());
            popups.Add(new Popup { amount = amount, at = Time.unscaledTime });
            Refresh();
            if (haveRank && rank.id > before)
            {
                promotedUntil = Time.unscaledTime + 3f;
                Audio?.PlayAlias("mp_level_up");
            }
        }

        static GUIStyle popupStyle, rankStyle, promoStyle;

        /// For MW2's own xpbar_hd: progress through the current rank and the rank id.
        public float XpFrac => haveRank && rank.xpToNext > 0 ? Mathf.Clamp01((Xp - (int)rank.minXp) / (float)rank.xpToNext) : 0f;
        public int RankId => haveRank ? (int)rank.id : 0;
        public string RankIcon => haveRank ? rankIcon : "";
        public string RankDisplay => haveRank ? rank.display.ToString() : "";

        /// MW2's own HUD has no rank in play; a small one beside the minimap (playtest 10-06-26: "a little
        /// icon for what rank you are would be helpful"), the rank's number under it.
        public void DrawRankBadge(Rect minimap)
        {
            if (!haveRank || string.IsNullOrEmpty(rankIcon)) return;
            float s = Mathf.Round(minimap.height * 0.26f);
            var r = new Rect(minimap.xMax + 6f, minimap.y + 4f, s, s);
            Mw2Icons.Draw(r, rankIcon, Color.white);
            Mw2Font.Label(new Rect(r.x - 20f, r.yMax, s + 40f, Mw2Hud.S(20f)), RankDisplay, Mw2Hud.S(20f), Color.white, TextAnchor.MiddleCenter, Mw2Font.Small);
        }
        /// MW2's menus draw the XP bar; ours is only the fallback.
        public bool MenuBar;

        public void Draw()
        {
            if (!haveRank || dirty) Refresh();
            if (popupStyle == null)
            {
                popupStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                popupStyle.normal.textColor = new Color(1f, 0.85f, 0.25f);
                rankStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                promoStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            float w = Screen.width, h = Screen.height;

            // Score popups: "+100" near the crosshair, rising and fading (MW2's in-game XP feedback).
            popups.RemoveAll(p => Time.unscaledTime - p.at > 1.2f);
            for (int i = 0; i < popups.Count; i++)
            {
                float t = (Time.unscaledTime - popups[i].at) / 1.2f;
                Mw2Font.Label(new Rect(w / 2 - 100, h * 0.43f - t * 30f - (popups.Count - 1 - i) * Mw2Hud.S(30f), 200, 30), $"+{popups[i].amount}",
                    Mw2Hud.S(30f), new Color(1f, 0.85f, 0.25f, 1f - t * t), TextAnchor.MiddleCenter);
            }

            if (!haveRank) return;
            if (MenuBar) goto promotion;
            // XP bar along the bottom edge with the rank icon (MW2's 720_xpbar art).
            float barH = Mathf.Round(h * 0.011f);
            var bar = new Rect(0, h - barH, w, barH);
            Mw2Icons.Draw(bar, "720_xpbar_empty", Color.white);
            float frac = rank.xpToNext > 0 ? Mathf.Clamp01((Xp - (int)rank.minXp) / (float)rank.xpToNext) : 1f;
            var t2 = Mw2Icons.Get("720_xpbar_solid");
            if (t2 != null) GUI.DrawTextureWithTexCoords(new Rect(0, bar.y, w * frac, barH), t2, new Rect(0, 0, frac, 1f), true);
            float ri = Mathf.Round(h * 0.03f);
            Mw2Icons.Draw(new Rect(6, bar.y - ri - 2, ri, ri), rankIcon, Color.white);
            Mw2Font.Label(new Rect(10 + ri, bar.y - ri - 2, 300, ri), $"{rank.display}   {Xp - (int)rank.minXp} / {rank.xpToNext}", Mw2Hud.S(20f), Color.white, TextAnchor.MiddleLeft, Mw2Font.Small);

            promotion:
            if (Time.unscaledTime < promotedUntil)
            {
                float big = Mathf.Round(h * 0.09f);
                Mw2Icons.Draw(new Rect(w / 2 - big / 2, h * 0.24f, big, big), rankIcon, Color.white);
                Mw2Font.Label(new Rect(0, h * 0.24f + big + 4, w, 36), $"Promoted to rank {rank.display}", Mw2Hud.S(38f), Color.white, TextAnchor.MiddleCenter, Mw2Font.Objective);
            }
        }
    }
}
