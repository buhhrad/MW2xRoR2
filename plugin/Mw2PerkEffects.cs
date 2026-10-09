using System.Collections.Generic;
using HarmonyLib;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// What MW2's perks do here beyond MW2's own numbers (Mw2Perks), for the perks that only make
    /// sense against other players (playtest 10-04-26: "do all the perks really work in this
    /// environment"). These are RoR2 stand-ins, not MW2 behaviour:
    ///   Cold-Blooded: monsters can't pick you up as a target beyond 30 m (Spy Game: 20 m).
    ///   Ninja: crouched or prone, not beyond 12 m. Dead Silence: walking (not sprinting), 20 m.
    ///   Scrambler: monsters within 15 m attack 20% slower (Pro: within 25 m).
    ///   SitRep: elites and bosses within 60 m are outlined red (Pro: every monster within 30 m).
    ///   Stopping Power Pro (Armor Piercing): bullets +40% against bosses (MW2: against vehicles).
    /// MW2 behaviour also kept here: Commando Pro takes no fall damage (_perks.gsc MOD_FALLING).
    static class Mw2PerkEffects
    {
        static bool Has(CharacterBody b, string perk) => Mw2Perks.Has(b, perk);

        public static void Init(Harmony harmony)
        {
            var find = AccessTools.Method(typeof(BaseAI), "FindEnemyHurtBox");
            if (find != null) harmony.Patch(find, postfix: new HarmonyMethod(typeof(Mw2PerkEffects), nameof(Spotted)));
            else Plugin.Log.LogWarning("MW2 perks: BaseAI.FindEnemyHurtBox not found; the stealth perks do nothing");
            var stats = AccessTools.Method(typeof(CharacterBody), "RecalculateStats");
            if (stats != null) harmony.Patch(stats, postfix: new HarmonyMethod(typeof(Mw2PerkEffects), nameof(Stats)));
        }

        // ---------------------------------------------------------------- stealth (host: monster AI)

        /// The menus' and perk items' text for the perks above, saying what they do here instead of
        /// what they do in MW2 (playtest 10-04-26). MW2's own wording style: short, "+" on the Pro line.
        /// Keys are perkTable's description (col 4) and Pro (col 9 of the upgrade row) strings.
        public static unsafe void Describe()
        {
            var text = new Dictionary<string, string>
            {
                // Cold-Blooded / Pro (specialty_spygame)
                ["PERKS_DESC_COLDBLOODED"] = "Monsters can't spot you beyond 30 m.",
                ["PERKS_DESC_SPYGAME"] = "+Monsters can't spot you beyond 20 m.",
                ["PERKS_UPGRADE_SPYGAME2"] = "+Hidden Beyond 20 m",
                // Scrambler / Pro (specialty_delaymine)
                ["PERKS_DESC_LOCALJAMMER"] = "Monsters within 15 m attack 20% slower.",
                ["PERKS_DESC_NINJA"] = "+Jams monsters within 25 m.",
                ["PERKS_UPGRADE_NINJA"] = "+Jam Range 25 m",
                // Ninja / Pro (specialty_quieter)
                ["PERKS_DESC_HEARTBREAKER"] = "Crouched or prone, monsters can't spot you beyond 12 m.",
                ["PERKS_MAKE_LESS_SOUND_WHEN"] = "+Not sprinting, monsters can't spot you beyond 20 m.",
                ["PERKS_UPGRADE_DEADSILENCE"] = "+Hidden Beyond 20 m",
                // SitRep / Pro (specialty_selectivehearing)
                ["PERKS_ABILITY_TO_SEEK_OUT_ENEMY"] = "Elites and bosses within 60 m are outlined.",
                ["PERKS_DESC_SELECTIVEHEARING"] = "+Every monster within 30 m is outlined.",
                ["PERKS_UPGRADE_AMPLIFY"] = "+Outline All Monsters",
                // Stopping Power Pro (specialty_armorpiercing)
                ["PERKS_DESC_ARMOR_DAMAGE"] = "+40% bullet damage vs. bosses.",
                ["PERKS_UPGRADE_ARMOR_DAMAGE"] = "+Extra Damage Vs. Bosses",
            };
            // Marathon: everyone sprints without limit here (config UnlimitedSprint), so it's speed.
            if (Plugin.Instance.UnlimitedSprint.Value) text["PERKS_DESC_MARATHON"] = "Sprint 10% faster.";
            int set = 0;
            foreach (var kv in text)
            {
                var k = System.Text.Encoding.UTF8.GetBytes(kv.Key);
                var v = System.Text.Encoding.UTF8.GetBytes(kv.Value);
                fixed (byte* pk = k) fixed (byte* pv = v) set += Native.mw2_localize_set(pk, (System.UIntPtr)k.Length, pv, (System.UIntPtr)v.Length);
            }
            Plugin.Log.LogInfo($"MW2 perks: {set}/{text.Count} descriptions say what they do in RoR2");
        }

        /// How far away a monster can first spot this player (0 = no limit).
        public static float SpotRange(CharacterBody b)
        {
            float r = 0f;
            void Limit(float m) { r = r == 0f ? m : Mathf.Min(r, m); }
            if (Has(b, "specialty_coldblooded")) Limit(30f);
            if (Has(b, "specialty_spygame")) Limit(20f);
            byte stance = Mw2Net.StanceOf(b);
            if (Has(b, "specialty_heartbreaker") && (stance == 1 || stance == 2)) Limit(12f);
            if (Has(b, "specialty_quieter") && !b.isSprinting) Limit(20f);
            return r;
        }

        static void Spotted(BaseAI __instance, ref HurtBox __result)
        {
            if (__result == null || __instance == null || __instance.body == null) return;
            var target = __result.healthComponent != null ? __result.healthComponent.body : null;
            if (target == null || !target.isPlayerControlled) return;
            float limit = SpotRange(target);
            if (limit > 0f && (target.corePosition - __instance.body.corePosition).sqrMagnitude > limit * limit) __result = null;
        }

        // ---------------------------------------------------------------- stats (every machine)

        static readonly HashSet<CharacterBody> noFall = new HashSet<CharacterBody>();
        static readonly HashSet<CharacterBody> jammed = new HashSet<CharacterBody>();
        static System.Reflection.MethodInfo attackSpeedSetter;

        static void Stats(CharacterBody __instance)
        {
            if (__instance == null) return;
            if (__instance.isPlayerControlled)
            {
                bool want = Has(__instance, "specialty_falldamage");
                bool had = (__instance.bodyFlags & CharacterBody.BodyFlags.IgnoreFallDamage) != 0;
                if (want && !had) { __instance.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage; noFall.Add(__instance); }
                else if (!want && noFall.Remove(__instance)) __instance.bodyFlags &= ~CharacterBody.BodyFlags.IgnoreFallDamage;
            }
            if (jammed.Contains(__instance))
            {
                attackSpeedSetter = attackSpeedSetter ?? AccessTools.PropertySetter(typeof(CharacterBody), nameof(CharacterBody.attackSpeed));
                attackSpeedSetter?.Invoke(__instance, new object[] { __instance.attackSpeed * 0.8f });
            }
        }

        // ---------------------------------------------------------------- per frame

        static float nextJam, nextSitRep;
        static readonly List<CharacterBody> players = new List<CharacterBody>();

        public static void Update()
        {
            if (Time.time >= nextJam) { nextJam = Time.time + 0.5f; if (NetworkServer.active) Jam(); }
            if (Time.time >= nextSitRep) { nextSitRep = Time.time + 0.5f; SitRep(); }
        }

        /// Host: Scrambler players jam the monsters near them (their attack speed, through Stats).
        static void Jam()
        {
            players.Clear();
            foreach (var b in CharacterBody.readOnlyInstancesList)
                if (b != null && b.isPlayerControlled && Has(b, "specialty_localjammer")) players.Add(b);
            jammed.RemoveWhere(b => b == null);
            foreach (var m in CharacterBody.readOnlyInstancesList)
            {
                if (m == null || m.teamComponent == null || m.teamComponent.teamIndex != TeamIndex.Monster) continue;
                bool j = false;
                foreach (var p in players)
                {
                    float r = Has(p, "specialty_delaymine") ? 25f : 15f;
                    if ((p.corePosition - m.corePosition).sqrMagnitude <= r * r) { j = true; break; }
                }
                if (j == jammed.Contains(m)) continue;
                if (j) jammed.Add(m); else jammed.Remove(m);
                m.MarkAllStatsDirty();
            }
        }

        static readonly Dictionary<CharacterBody, Highlight> outlined = new Dictionary<CharacterBody, Highlight>();

        /// This machine's player with SitRep: the monsters worth knowing about get a red outline.
        static void SitRep()
        {
            var me = Plugin.Instance != null ? Plugin.Instance.Bridge.LocalBody2 : null;
            bool sit = me != null && Has(me, "specialty_detectexplosive");
            bool hearing = sit && Has(me, "specialty_selectivehearing");
            var keep = new HashSet<CharacterBody>();
            if (sit)
                foreach (var m in CharacterBody.readOnlyInstancesList)
                {
                    if (m == null || m.teamComponent == null || m.teamComponent.teamIndex != TeamIndex.Monster || m.healthComponent == null || !m.healthComponent.alive) continue;
                    float d = (m.corePosition - me.corePosition).sqrMagnitude;
                    if (((m.isElite || m.isChampion) && d <= 60f * 60f) || (hearing && d <= 30f * 30f)) keep.Add(m);
                }
            foreach (var m in keep)
            {
                if (outlined.ContainsKey(m)) continue;
                var model = m.modelLocator != null && m.modelLocator.modelTransform != null ? m.modelLocator.modelTransform.GetComponent<CharacterModel>() : null;
                var r = model != null && model.baseRendererInfos != null && model.baseRendererInfos.Length > 0 ? model.baseRendererInfos[0].renderer : null;
                if (r == null) continue;
                var h = r.gameObject.AddComponent<Highlight>();
                h.targetRenderer = r;
                h.highlightColor = Highlight.HighlightColor.custom;
                h.CustomColor = new Color(1f, 0.15f, 0.1f);
                h.strength = 1f;
                h.isOn = true;
                outlined[m] = h;
            }
            var gone = new List<CharacterBody>();
            foreach (var kv in outlined) if (kv.Key == null || !keep.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var m in gone)
            {
                if (outlined.TryGetValue(m, out var h) && h != null) Object.Destroy(h);
                outlined.Remove(m);
            }
        }

        // ---------------------------------------------------------------- bullets

        /// Armor Piercing (Stopping Power Pro): bullets +40% against bosses.
        public static void ArmorPiercing(BulletAttack attack, ref BulletAttack.BulletHit hit, DamageInfo info)
        {
            var victim = hit.hitHurtBox != null && hit.hitHurtBox.healthComponent != null ? hit.hitHurtBox.healthComponent.body : null;
            if (victim != null && victim.isChampion) info.damage *= 1.4f;
        }
    }
}
