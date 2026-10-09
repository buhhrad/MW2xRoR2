using System.Collections.Generic;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's riot shield (riotshield_mp). MW2 gives the shield a collision model: held on the left
    /// arm (tag_weapon_left) it stops what comes at your front, stowed on your back
    /// (tag_shield_back, `_class.gsc` trackRiotShield) it stops what comes at your back. RoR2 has no
    /// such model to hit, so the host rejects damage arriving inside the shield's arc instead.
    static class Mw2Shield
    {
        static readonly Dictionary<uint, bool> isShield = new Dictionary<uint, bool>();
        static uint index;
        static float lastLog;

        public static bool Is(uint weapon)
        {
            if (weapon == 0) return false;
            if (!isShield.TryGetValue(weapon, out bool s)) isShield[weapon] = s = Native.mw2_weapon_is_shield(weapon) == 1;
            return s;
        }

        /// riotshield_mp's weapon index (the stowed model on a teammate's back).
        public static uint Index => index != 0 ? index : (index = Native.WeaponIndex("riotshield_mp"));

        public static void Init(Harmony harmony)
        {
            var take = AccessTools.Method(typeof(HealthComponent), "TakeDamage");
            if (take != null) harmony.Patch(take, prefix: new HarmonyMethod(typeof(Mw2Shield), nameof(Block)) { priority = Priority.First });
        }

        /// Host: false (skip the damage) when it hits a shield.
        static bool Block(HealthComponent __instance, DamageInfo damageInfo)
        {
            if (damageInfo == null || __instance == null || __instance.body == null || damageInfo.rejected) return true;
            var body = __instance.body;
            if (!Mw2Net.ShieldOf(body, out bool held, out bool back) || (!held && !back)) return true;
            // Bleeds / burns aren't coming from anywhere; his own and the world's (falls, void) neither.
            if (damageInfo.dotIndex != DotController.DotIndex.None || damageInfo.attacker == null || damageInfo.attacker == body.gameObject) return true;
            var core = body.corePosition;
            var attacker = damageInfo.attacker.GetComponent<CharacterBody>();
            // Where it came from: the impact point when it's off his middle, else the attacker.
            Vector3 from;
            if (damageInfo.position != Vector3.zero && (damageInfo.position - core).sqrMagnitude > 0.01f) from = damageInfo.position;
            else if (attacker != null) from = attacker.corePosition;
            else return true;
            var to = from - core; to.y = 0f;
            var face = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward; face.y = 0f;
            if (to.sqrMagnitude < 1e-4f || face.sqrMagnitude < 1e-4f) return true;
            float cone = Mathf.Clamp(Plugin.Instance.ShieldBlockAngle.Value, 0f, 180f);
            float angle = Vector3.Angle(face, to);
            if (!(held && angle <= cone) && !(back && angle >= 180f - cone)) return true;
            damageInfo.rejected = true;
            // MW2's shield impacts: knives clang, explosions thud, bullets ping.
            bool close = attacker != null && (attacker.corePosition - core).magnitude < 4f + attacker.radius;
            bool blast = (damageInfo.damageType & DamageType.AOE) != 0;
            string alias = blast ? "grenade_explode_riotshield" : close ? "melee_knife_hit_shield" : "bullet_small_riotshield";
            Mw2Gunfire.ImpactSound?.Invoke(alias, core + to.normalized * body.radius);
            if (Time.unscaledTime - lastLog > 0.5f)
            {
                lastLog = Time.unscaledTime;
                Plugin.Log.LogInfo($"[shield] {(held ? "front" : "back")} blocked {damageInfo.damage:F1} from {(attacker != null ? attacker.name : damageInfo.attacker.name)} at {angle:F0} deg ({alias})");
            }
            return false;
        }
    }
}
