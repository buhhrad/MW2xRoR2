using System.Collections;
using HarmonyLib;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// equipment.rs Mw2Equipment (44 bytes): a weapon's projectile / equipment facts.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Mw2Equipment
    {
        public int offhand_class;
        public byte kind, explosion_type, stickiness, flags;
        public int fuse_ms, speed, activate_dist, impact_damage;
        public float radius, radius_min, inner_damage, outer_damage, cone_angle; // 44 bytes, as mw2sim's (was 40: the native write ran 4 bytes past it)
    }

    /// MW2's deathstreaks with the mod's rule for RoR2 (10-02-26): one death is enough - you go down, a
    /// teammate carries the stage, you come back with your class's deathstreak (MW2 needs 3-4 deaths
    /// without a kill, which a RoR2 run never reaches). A kill resets the count, as in MW2. What each
    /// one does is MW2's (_perkfunctions.gsc, _damage.gsc, _class.gsc):
    ///   Painkiller (specialty_combathigh): damage / 3 for 10 s (falls exempt), overlay + timer.
    ///   Final Stand: the next lethal hit leaves 1 health and puts you prone with a pistol for 20 s,
    ///     then you get back up at full health (lastStandRespawnPlayer).
    ///   Martyrdom (specialty_grenadepulldeath): your frag goes off where you die, after its fuse.
    ///   Copycat: copies a teammate's class (MW2: the killer's) - needs multiplayer.
    static class Mw2Deathstreaks
    {
        static Run run;
        static int deaths;
        static string active = "";
        static float painkillerFrom = -1f, lastStandFrom = -1f;
        static bool finalStandArmed;

        public const float PainkillerTime = 10f, FinalStandTime = 20f, LastStandTime = 10f;

        /// The current last stand: Final Stand (20 s, then up) or the Last Stand perk (10 s, then
        /// you bleed out - _damage.gsc lastStandTimer(10, false)).
        static bool lastStandGetsUp;
        static float LastStandLength => lastStandGetsUp ? FinalStandTime : LastStandTime;

        public static string Active => active;
        /// Seconds of Painkiller left (0: none) and an unused Final Stand, for the host (Mw2Net KClass).
        public static float PainkillerLeft => PainkillerOn ? PainkillerTime - (Time.time - painkillerFrom) : 0f;
        public static bool FinalStandArmed => finalStandArmed;
        public static bool InLastStand => lastStandFrom >= 0f && Time.time - lastStandFrom < LastStandLength;
        /// In the Last Stand perk's last stand (not Final Stand's): ch_laststand_pro counts these kills.
        public static bool InLastStandPerk => InLastStand && !lastStandGetsUp;

        public static void Init(Harmony harmony)
        {
            var take = AccessTools.Method(typeof(HealthComponent), "TakeDamage", new[] { typeof(DamageInfo) });
            if (take != null) harmony.Patch(take, prefix: new HarmonyMethod(typeof(Mw2Deathstreaks), nameof(TakeDamage)));
            else Plugin.Log.LogWarning("HealthComponent.TakeDamage not found; Painkiller / Final Stand won't change damage.");
        }

        static void NewRunCheck()
        {
            if (run == Run.instance) return;
            run = Run.instance;
            deaths = 0;
            active = "";
            painkillerFrom = lastStandFrom = -1f;
            finalStandArmed = false;
        }

        /// The player killed something: MW2 resets cur_death_streak.
        public static void OnKill()
        {
            NewRunCheck();
            deaths = 0;
        }

        /// The player's MW2 body died.
        public static void OnDeath(CharacterBody body, Mw2Bridge bridge)
        {
            NewRunCheck();
            deaths++;
            // A client's Martyrdom frag is the host's to throw (it knows the deathstreak from KClass).
            if (active == "specialty_grenadepulldeath" && body != null && NetworkServer.active) Martyrdom(body);
            active = "";
            painkillerFrom = lastStandFrom = -1f;
            finalStandArmed = false;
        }

        /// A new life with this class (its deathstreak in `deathstreak`): give it if earned.
        public static void OnSpawn(CharacterBody body, string deathstreak)
        {
            NewRunCheck();
            active = "";
            // A stand cut short by a stage change mustn't end (heal / bleed out) on the new body.
            lastStandFrom = -1f;
            lastStandGetsUp = false;
            finalStandArmed = false;
            if (deaths < 1 || string.IsNullOrEmpty(deathstreak) || deathstreak == "specialty_null") return;
            active = deathstreak;
            // splashNotify(loadoutDeathStreak): splashTable row named after the perk, mp_last_stand.
            string title = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/splashTable.csv", 0, deathstreak, 1));
            string desc = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/splashTable.csv", 0, deathstreak, 2));
            Plugin.Instance.Bridge.Streaks.Splash(title, desc, 2.5f, Mw2Menus.TableLookup("mp/splashTable.csv", 0, deathstreak, 3));
            Mw2Audio.PlayUi(Mw2Menus.TableLookup("mp/splashTable.csv", 0, deathstreak, 9));
            Plugin.Log.LogInfo($"MW2 deathstreak: {deathstreak} ({deaths} death(s) without a kill)");
            switch (deathstreak)
            {
                case "specialty_combathigh": painkillerFrom = Time.time; break;
                case "specialty_finalstand": finalStandArmed = true; break;
                case "specialty_copycat": Plugin.Log.LogInfo("MW2 Copycat: copies a teammate's class - multiplayer only, nothing to copy"); break;
            }
        }

        /// Host: is this MW2 player down in Last Stand / Final Stand (the host's own, or a client's)?
        public static bool InStand(CharacterBody body)
        {
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            if (bridge != null && bridge.Active && body == bridge.LocalBody) return InLastStand;
            var st = Mw2Net.StatusOf(body);
            return st != null && Time.time < st.standUntil;
        }

        static bool PainkillerOn => painkillerFrom >= 0f && Time.time - painkillerFrom < PainkillerTime;

        /// Painkiller's item lasts its 10 s; the others the whole life.
        public static bool ItemShown => active.Length > 0 && (active != "specialty_combathigh" || PainkillerOn);

        // Server side: damage to an MW2 player - the local one, or a client's (rules from its KClass).
        static void TakeDamage(HealthComponent __instance, DamageInfo damageInfo)
        {
            if (damageInfo == null || __instance == null || !NetworkServer.active) return;
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            if (bridge == null) return;
            if (bridge.Active && __instance.body == bridge.LocalBody)
            {
                if (Modify(__instance, damageInfo, PainkillerOn, finalStandArmed, InLastStand, out bool getsUp))
                {
                    lastStandGetsUp = getsUp;
                    finalStandArmed = false;
                    lastStandFrom = Time.time;
                    bridge.EnterLastStand();
                    Plugin.Log.LogInfo(lastStandGetsUp ? "MW2 Final Stand: last stand for 20 s" : "MW2 Last Stand: 10 s with a pistol");
                }
                return;
            }
            var st = Mw2Net.StatusOf(__instance.body);
            if (st == null) return;
            bool inStand = Time.time < st.standUntil;
            if (Modify(__instance, damageInfo, Time.time < st.painkillerUntil, st.finalStandArmed, inStand, out bool up))
            {
                st.finalStandArmed = false;
                // The client runs the timer; until it reports back, no second last stand.
                st.standUntil = Time.time + (up ? FinalStandTime : LastStandTime) + 5f;
                Mw2Net.ServerStand(__instance.body, up);
                Plugin.Log.LogInfo($"MW2 {(up ? "Final Stand" : "Last Stand")} for a client");
            }
        }

        /// _perks.gsc / _damage.gsc on one hit. True if it put the player in last stand (`getsUp`:
        /// Final Stand's, else the Last Stand perk's).
        static bool Modify(HealthComponent hc, DamageInfo damageInfo, bool painkiller, bool finalStand, bool inStand, out bool getsUp)
        {
            getsUp = false;
            bool fall = (damageInfo.damageType & DamageType.FallDamage) != 0;
            // _perks.gsc cac_modified_damage: combathigh divides by 3 (MOD_FALLING exempt).
            if (painkiller && !fall) damageInfo.damage /= 3f;
            // Final Stand: a lethal hit puts you in last stand instead (_damage.gsc).
            // RoR2 applies armor after this point (damage x 100 / (100 + armor)), so lethality and
            // the 1 health left over are worked out after it.
            float armor = hc.body != null ? hc.body.armor : 0f;
            float factor = armor >= 0f ? 100f / (100f + armor) : 2f - 100f / (100f - armor);
            // Last Stand (specialty_pistoldeath): _damage.gsc mayDoLastStand takes bullets and falls,
            // never MOD_TRIGGER_HURT; RoR2's hits carry no MW2 damage kinds, so any lethal hit but the
            // out-of-bounds / void kills.
            bool lastStandPerk = !finalStand && Mw2Perks.Has(hc.body, "specialty_pistoldeath")
                && (damageInfo.damageType & (DamageType.VoidDeath | DamageType.OutOfBounds)) == 0;
            if (!(finalStand || lastStandPerk) || inStand || damageInfo.damage * factor < hc.combinedHealth) return false;
            getsUp = finalStand;
            damageInfo.damage = Mathf.Max(0f, (hc.combinedHealth - 1f) / Mathf.Max(factor, 0.01f));
            // MW2 leaves exactly 1 health; RoR2's one-shot protection would leave 10%.
            damageInfo.damageType |= DamageType.BypassOneShotProtection;
            return true;
        }

        /// Client: the host put this player in last stand.
        public static void StandFromHost(Mw2Bridge bridge, bool getsUp)
        {
            lastStandGetsUp = getsUp;
            finalStandArmed = false;
            lastStandFrom = Time.time;
            bridge.EnterLastStand();
            Plugin.Log.LogInfo(getsUp ? "MW2 Final Stand: last stand for 20 s" : "MW2 Last Stand: 10 s with a pistol");
        }

        /// Called each frame by the bridge.
        public static void Update(Mw2Bridge bridge)
        {
            if (lastStandFrom >= 0f && !InLastStand)
            {
                lastStandFrom = -1f;
                var hc = bridge.LocalBody != null ? bridge.LocalBody.healthComponent : null;
                if (lastStandGetsUp)
                {
                    active = "";
                    bridge.LeaveLastStand();
                    if (hc != null && NetworkServer.active) hc.Networkhealth = hc.fullHealth;
                    else Mw2Net.SendStandEnd(bridge.LocalBody, true);
                    Plugin.Log.LogInfo("MW2 Final Stand: back up");
                }
                else
                {
                    // lastStandBleedOut: the timer ran out.
                    Plugin.Log.LogInfo("MW2 Last Stand: bled out");
                    if (hc != null && NetworkServer.active) hc.Suicide();
                    else Mw2Net.SendStandEnd(bridge.LocalBody, false);
                }
            }
            if (active == "specialty_combathigh" && painkillerFrom >= 0f && !PainkillerOn)
            {
                // _unsetPerk( "specialty_combathigh" ) after 10 s.
                active = "";
                painkillerFrom = -1f;
                bridge.RefreshPerks();
            }
        }

        public static void Martyrdom(CharacterBody body)
        {
            // _missions.gsc ch_martyr: Martyrdom drops frag_grenade_short (2.5 s fuse).
            uint frag = Native.WeaponIndex("frag_grenade_short_mp");
            if (frag == 0) frag = Native.WeaponIndex("frag_grenade_mp");
            if (frag == 0 || Native.mw2_weapon_equipment(frag, out var e) != 1) return;
            float dmg = body.damage, at = Time.time;
            var pos = body.footPosition + Vector3.up * 0.2f;
            var team = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.Player;
            var attacker = body.master != null ? body.master.gameObject : null;
            Plugin.Instance.StartCoroutine(Detonate(pos, e, dmg, team, attacker));
            Plugin.Log.LogInfo($"MW2 Martyrdom: frag dropped, {e.fuse_ms} ms fuse");
        }

        static IEnumerator Detonate(Vector3 pos, Mw2Equipment e, float bodyDamage, TeamIndex team, GameObject attacker)
        {
            yield return new WaitForSeconds(Mathf.Max(e.fuse_ms, 0) / 1000f);
            float reference = Mathf.Max(Plugin.Instance.DamageReference.Value, 1f);
            if (NetworkServer.active)
            {
                new BlastAttack
                {
                    attacker = attacker,
                    inflictor = attacker,
                    teamIndex = team,
                    baseDamage = bodyDamage * e.inner_damage / reference,
                    baseForce = 1500f,
                    position = pos,
                    radius = e.radius * Space.Scale,
                    falloffModel = BlastAttack.FalloffModel.Linear,
                    procCoefficient = 1f,
                    damageColorIndex = DamageColorIndex.Default,
                    damageType = DamageType.Generic,
                }.Fire();
            }
            Mw2Projectiles.Explosion(Native.WeaponIndex("frag_grenade_short_mp"), pos);
        }

        /// OnGUI: Painkiller's overlay, timer and icon (_perkfunctions.gsc setCombatHigh), the last
        /// stand timer.
        public static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            float sy = Screen.height / 480f;
            if (PainkillerOn)
            {
                float t = Time.time - painkillerFrom;
                // Fades in over 1 s, holds, fades out over the last 2 s.
                float a = t < 1f ? t : (t > 8f ? Mathf.Clamp01((PainkillerTime - t) / 2f) : 1f);
                Mw2Icons.Draw(new Rect(0, 0, Screen.width, Screen.height), "combathigh_overlay", new Color(1f, 1f, 1f, a));
                Timer(PainkillerTime - t, 112f * sy, new Color(0.8f, 0.8f, 0f, a), "specialty_painkiller", 32f * sy, a * 0.85f);
            }
            if (InLastStand)
                Timer(LastStandLength - (Time.time - lastStandFrom), 112f * sy, new Color(0.8f, 0.8f, 0f, 1f), lastStandGetsUp ? "specialty_finalstand" : "specialty_pistoldeath", 32f * sy, 0.85f);
        }

        /// createTimer( "hudsmall", 1.0 ) at CENTER, CENTER, 0, y with an icon above it.
        static void Timer(float secondsLeft, float y, Color col, string icon, float iconPx, float iconAlpha)
        {
            int s = Mathf.CeilToInt(Mathf.Max(secondsLeft, 0f));
            string text = $"{s / 60}:{s % 60:00}";
            float px = Native.mw2_hudelem_em_px(7, 1f, Screen.height / 480f);
            float cy = Screen.height * 0.5f + y;
            Mw2Font.Label(new Rect(0, cy - px, Screen.width, px * 2f), text, px, col, TextAnchor.MiddleCenter, Mw2Font.Small);
            Mw2Icons.Draw(new Rect(Screen.width * 0.5f - iconPx * 0.5f, cy - px - iconPx, iconPx, iconPx), icon, new Color(1f, 1f, 1f, iconAlpha));
        }
    }
}
