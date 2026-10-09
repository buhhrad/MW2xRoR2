using System;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's knife. The sim runs the weapon side (melee_time / melee_delay from the weapon file,
    /// the lunge through pmove); this side does what IW4's client and server do around it:
    /// pick the lunge target before the press, then trace for the stab at the hit moment.
    /// A knife kill is one hit in MW2 (135 damage vs 100 health); here it kills any ordinary
    /// monster outright and hits bosses for the knife's damage at the guns' scale.
    static class Mw2Melee
    {
        // IW4 player_meleeRange / Width / Height (weapon_iw4 defaults).
        const float RangeUnits = 64f, WidthUnits = 10f;
        // Commando: perk_extendedMeleeRange replaces player_meleeRange. 176 is IW4's dvar default
        // as recalled (docs/mw2-reference.md); it is not in any file read from the install - unverified.
        const float ExtendedRangeUnits = 176f;
        static float Range(CharacterBody body) => Mw2Perks.Has(body, "specialty_extendedmelee") ? ExtendedRangeUnits : RangeUnits;
        // How far ahead MW2 lunges from (config Balance.MeleeLungeRange, MW2's 128 units).
        static float Lunge(CharacterBody body) => Mathf.Max(Plugin.Instance.MeleeLungeRange.Value, Range(body));
        const float ConeDeg = 30f;
        const float PlayerRadiusUnits = 15f;

        /// Tell the sim who it would lunge at if melee were pressed now (nearest enemy in front,
        /// in sight, within lunge range); 0 distance when nobody.
        public static void UpdateTarget(IntPtr sim, CharacterBody body, Vector3 eye, Vector3 aim)
        {
            if (sim == IntPtr.Zero || body == null) return;
            var flat = new Vector3(aim.x, 0f, aim.z);
            if (flat.sqrMagnitude < 1e-4f) { Native.mw2_set_melee_target(sim, 0f, 0f); return; }
            flat.Normalize();
            float best = float.MaxValue;
            Vector3 bestDir = Vector3.zero;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (!Mw2Strike.IsEnemy(body, cb)) continue;
                var to = cb.corePosition - eye;
                if (Mathf.Abs(to.y) > Lunge(body) * Space.Scale * 0.6f) continue;
                var toFlat = new Vector3(to.x, 0f, to.z);
                float edge = toFlat.magnitude - cb.radius - PlayerRadiusUnits * Space.Scale;
                if (edge > Lunge(body) * Space.Scale || edge >= best) continue;
                if (Vector3.Angle(flat, toFlat) > ConeDeg) continue;
                if (Physics.Linecast(eye, cb.corePosition, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) continue;
                best = edge;
                bestDir = toFlat;
            }
            if (best == float.MaxValue) { Native.mw2_set_melee_target(sim, 0f, 0f); return; }
            var iw = Space.DirToIw(bestDir.normalized);
            Native.mw2_set_melee_target(sim, Mathf.Atan2(iw.y, iw.x) * Mathf.Rad2Deg, Mathf.Max(best, 0f) / Space.Scale + 1f);
        }

        /// A throwing knife struck at `at`: like the knife in hand, it kills anything short of a boss
        /// (MW2: 135 against 100 health), a boss takes its impact damage (playtest 10-05-26: the throwing
        /// knife should one-hit). False if no enemy is there.
        public static bool ThrownKnife(CharacterBody body, uint weapon, Vector3 at, Vector3 dir)
        {
            if (body == null) return false;
            CharacterBody target = null;
            float bestD = float.MaxValue;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (!Mw2Strike.IsEnemy(body, cb) || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                float d = Vector3.Distance(cb.corePosition, at) - cb.radius;
                if (d < 1.2f && d < bestD) { bestD = d; target = cb; }
            }
            if (target == null) return false;
            if (!body.hasEffectiveAuthority) return true;
            bool boss = target.isBoss || target.isChampion;
            int impact = Native.mw2_weapon_equipment(weapon, out var eq) == 1 && eq.impact_damage > 0 ? eq.impact_damage : 135;
            float damage = boss
                ? body.damage * (impact / Mathf.Max(Plugin.Instance.DamageReference.Value, 1f)) * Mathf.Max(Plugin.Instance.DamageMultiplier.Value, 0f)
                : target.healthComponent.fullCombinedHealth * 4f + body.damage * 10f;
            if (dir.sqrMagnitude < 1e-4f) dir = target.corePosition - at;
            var from = at - dir.normalized * 0.8f;
            new BulletAttack
            {
                owner = body.gameObject,
                weapon = body.gameObject,
                origin = from,
                aimVector = (target.corePosition - from).normalized,
                minSpread = 0f,
                maxSpread = 0f,
                bulletCount = 1,
                damage = damage,
                damageType = boss ? DamageType.Generic : DamageType.BypassArmor | DamageType.BypassOneShotProtection,
                force = 400f,
                falloffModel = BulletAttack.FalloffModel.None,
                maxDistance = Vector3.Distance(from, target.corePosition) + target.radius + 0.5f,
                procCoefficient = 1f,
                isCrit = body.RollCrit(),
                radius = 0.4f,
                smartCollision = true,
                stopperMask = LayerIndex.world.mask,
                filterCallback = BulletAttack.ignoreAlliesFilterCallback,
            }.Fire();
            Plugin.Log.LogInfo($"[knife] throwing knife struck {target.name}{(boss ? " (boss: impact damage)" : "")}");
            return true;
        }

        /// The blade's hit moment: the nearest enemy within MW2's melee reach in front of the eye.
        /// Returns true on a hit.
        public static bool Hit(CharacterBody body, uint weapon, Vector3 eye, Vector3 aim, Action onHit)
        {
            if (body == null) return false;
            float reach = (Range(body) + WidthUnits) * Space.Scale;
            CharacterBody target = null;
            float bestD = float.MaxValue;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (!Mw2Strike.IsEnemy(body, cb)) continue;
                var to = cb.corePosition - eye;
                float d = to.magnitude - cb.radius;
                if (d > reach || d >= bestD) continue;
                // The cone is level (RoR2's monsters are short: a Lemurian at your feet is 70 deg below
                // the view); the reach is still a 3D distance.
                if (d > 0.2f && Vector3.Angle(new Vector3(aim.x, 0f, aim.z), new Vector3(to.x, 0f, to.z)) > 45f) continue;
                if (Physics.Linecast(eye, cb.corePosition, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) continue;
                target = cb;
                bestD = d;
            }
            if (target == null)
            {
                CharacterBody near = null; float nd = float.MaxValue;
                foreach (var cb in CharacterBody.readOnlyInstancesList)
                    if (Mw2Strike.IsEnemy(body, cb) && (cb.corePosition - eye).magnitude < nd) { nd = (cb.corePosition - eye).magnitude; near = cb; }
                Plugin.Log.LogInfo($"[melee] swing missed (no enemy within {reach:F2} m in front){(near != null ? $"; nearest {near.name} {nd - near.radius:F2} m at {Vector3.Angle(aim, near.corePosition - eye):F0} deg, wall {Physics.Linecast(eye, near.corePosition, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)}" : "")}");
                return false;
            }
            var at = target.corePosition;
            bool shield = Mw2Shield.Is(weapon);
            Mw2Gunfire.ImpactSound?.Invoke(shield ? "melee_riotshield_impact_plr" : "melee_knife_hit_body", at);
            onHit?.Invoke();
            if (!body.hasEffectiveAuthority) return true;
            bool boss = target.isBoss || target.isChampion;
            float damage;
            if (boss)
            {
                float reference = Mathf.Max(Plugin.Instance.DamageReference.Value, 1f);
                damage = body.damage * (Mathf.Max(Native.mw2_weapon_melee_damage(weapon), 1) / reference) * Mathf.Max(Plugin.Instance.DamageMultiplier.Value, 0f);
            }
            // MW2: a knife (meleeDamage 135 vs 100 health) kills in one; the shield's bash (50) in two.
            else if (shield) damage = target.healthComponent.fullCombinedHealth * 0.51f;
            else damage = target.healthComponent.fullCombinedHealth * 4f + body.damage * 10f;
            // A point-blank bullet straight at it: networks like the gunfire does, procs on-hit items.
            var melee = new BulletAttack
            {
                owner = body.gameObject,
                weapon = body.gameObject,
                origin = eye,
                aimVector = (at - eye).normalized,
                minSpread = 0f,
                maxSpread = 0f,
                bulletCount = 1,
                damage = damage,
                damageType = boss ? DamageType.Generic : DamageType.BypassArmor | DamageType.BypassOneShotProtection,
                force = 600f,
                falloffModel = BulletAttack.FalloffModel.None,
                maxDistance = Vector3.Distance(eye, at) + target.radius + 0.5f,
                procCoefficient = 1f,
                isCrit = body.RollCrit(),
                radius = 0.4f,
                smartCollision = true,
                stopperMask = LayerIndex.world.mask,
            };
            Mw2Challenges.With(Mw2Challenges.Cause.Melee, melee.Fire);
            Plugin.Log.LogInfo($"[melee] {(shield ? "bashed" : "stabbed")} {target.name} at {bestD:F2} m{(boss ? " (boss: melee damage)" : "")}");
            return true;
        }
    }
}
