using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's challenges (allChallengesTable, kept in the menus' playerdata): kills with a gun advance
    /// its Marksman and kills-with-attachment challenges; a completed tier pays its XP and shows
    /// MW2's challenge splash (splashTable: 3.5 s, mp_challenge_complete). Unlocks follow from them.
    static unsafe class Mw2Challenges
    {
        /// _missions.gsc sorts kills by means of death: bullets, explosives, melee. Killstreak
        /// kills (None) don't feed the perk challenges.
        public enum Cause { None, Bullet, Explosive, Melee }
        public static Cause Current = Cause.None;
        static Cause lastUsed = Cause.None;
        static float lastUsedAt = -999f;

        /// Run `fire` (a BulletAttack / BlastAttack / melee hit) with its kills attributed to `cause`.
        public static void With(Cause cause, Action fire)
        {
            var before = Current;
            Current = cause;
            if (cause != Cause.None) { lastUsed = cause; lastUsedAt = Time.unscaledTime; }
            try { fire(); } finally { Current = before; }
        }

        /// A client's kills reach it from the host after the hit (RoR2 raises deaths on the host only),
        /// so the kind of kill is the last attack this player made, if recent (approximate: a grenade
        /// and a burst landing together can swap). Older than 2 s: a bullet, the common case.
        public static Cause Recent() => Time.unscaledTime - lastUsedAt < 2f ? lastUsed : Cause.Bullet;

        /// MW2's per-kill perk challenges (_missions.gsc onPlayerKill branches), Pro unlocks.
        public static List<Done> PerkKill(RoR2.CharacterBody body, Vector3? victimPos, string weapon, bool ads)
        {
            var done = new List<Done>();
            void Add(string ch) => done.AddRange(Progress(ch, 1));
            bool Has(string p) => Mw2Perks.Has(body, p);
            switch (Current)
            {
                case Cause.Bullet:
                    if (Has("specialty_bulletaccuracy") && !ads) Add("ch_bulletaccuracy_pro");
                    // distanceSquared < 65536: 256 units.
                    if (victimPos.HasValue && Vector3.Distance(body.corePosition, victimPos.Value) < 256f * Space.Scale)
                    {
                        if (Has("specialty_heartbreaker")) Add("ch_deadsilence_pro");
                        if (Has("specialty_localjammer")) Add("ch_scrambler_pro");
                    }
                    if (Has("specialty_fastreload")) Add("ch_sleightofhand_pro");
                    string name = weapon ?? "";
                    int atts = name.EndsWith("_mp") ? name.Substring(0, name.Length - 3).Split('_').Length - 1 : 0;
                    if (Has("specialty_bling") && atts == 2) Add("ch_bling_pro");
                    if (Has("specialty_bulletdamage")) Add("ch_stoppingpower_pro");
                    if (Has("specialty_pistoldeath") && Mw2Deathstreaks.InLastStandPerk) Add("ch_laststand_pro");
                    break;
                case Cause.Explosive:
                    if (Has("specialty_explosivedamage")) Add("ch_dangerclose_pro");
                    break;
                case Cause.Melee:
                    if (Has("specialty_extendedmelee")) Add("ch_extendedmelee_pro");
                    if (Has("specialty_heartbreaker")) Add("ch_deadsilence_pro");
                    break;
            }
            return done;
        }

        public struct Done
        {
            public string Name;
            public int Tier, Xp;
        }

        static readonly byte[] buf = new byte[4096];

        static List<Done> Parse(uint n)
        {
            var list = new List<Done>();
            if (n == 0 || n > buf.Length) return list;
            foreach (var line in System.Text.Encoding.UTF8.GetString(buf, 0, (int)n).Split((char)10))
            {
                var f = line.Split('|');
                if (f.Length == 3 && int.TryParse(f[1], out int tier) && int.TryParse(f[2], out int xp))
                    list.Add(new Done { Name = f[0], Tier = tier, Xp = xp });
            }
            return list;
        }

        /// A kill with this MW2 weapon (full name, ak47_gl_mp).
        public static List<Done> Kill(string weapon, bool headshot = false)
        {
            if (!Mw2Menus.Ready || string.IsNullOrEmpty(weapon)) return new List<Done>();
            var b = System.Text.Encoding.UTF8.GetBytes(weapon);
            uint n;
            fixed (byte* p = b) fixed (byte* o = buf) n = Native.mw2_challenge_kill(p, (UIntPtr)b.Length, headshot ? 1 : 0, o, (uint)buf.Length);
            return Parse(n);
        }

        /// Progress on a challenge by name (Pro perk challenges: miles run, kills with a perk ...).
        public static List<Done> Progress(string name, long amount)
        {
            if (!Mw2Menus.Ready || amount <= 0) return new List<Done>();
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            uint n;
            fixed (byte* p = b) fixed (byte* o = buf) n = Native.mw2_challenge_progress(p, (UIntPtr)b.Length, amount, o, (uint)buf.Length);
            return Parse(n);
        }

        static readonly string[] roman = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

        struct Pending
        {
            public string Title, Desc;
        }

        static readonly Queue<Pending> queue = new Queue<Pending>();
        static float nextSplashAt;

        /// Show queued splashes one at a time (_hud_message.gsc actionNotify: each waits for the
        /// last; splashTable duration 3.5 s for challenges).
        public static void Update(Mw2Killstreaks streaks)
        {
            if (queue.Count == 0 || Time.unscaledTime < nextSplashAt || streaks == null) return;
            var p = queue.Dequeue();
            streaks.Splash(p.Title, p.Desc, 3.5f);
            Mw2Audio.PlayUi("mp_challenge_complete");
            nextSplashAt = Time.unscaledTime + 3.5f;
        }

        /// Pay out and announce completed tiers.
        public static void Report(List<Done> done, Mw2Progress progress, Mw2Killstreaks streaks)
        {
            foreach (var d in done)
            {
                progress?.Add(d.Xp);
                // allChallengesTable column 1: the challenge's name; splashTable: its description
                // (CHALLENGE_GET_N_KILLS, &&1 = the tier's target).
                string name = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/allChallengesTable.csv", 0, d.Name, 1));
                string target = Mw2Menus.TableLookup("mp/allChallengesTable.csv", 0, d.Name, 6 + (d.Tier - 1) * 2);
                if (d.Name.Contains("marathon") || d.Name.Contains("lightweight")) target = (long.TryParse(target, out var ft) ? ft / 5280 : 0).ToString();
                string desc = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/splashTable.csv", 0, d.Name, 2)).Replace("&&1", target);
                string title = name + (d.Tier < roman.Length ? " " + roman[d.Tier] : "");
                queue.Enqueue(new Pending { Title = title, Desc = desc });
                Plugin.Log.LogInfo($"MW2 challenge: {d.Name} tier {d.Tier} (+{d.Xp} XP) - {title}: {desc}");
            }
        }
    }
}
