using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// Killstreak aircraft that monsters can shoot down (playtest 10-03-26). Each one carries an invisible
    /// RoR2 body on the owner's team (a destructible's body: no AI, motor or sounds), sized to the hull
    /// and moved with it, so monsters with a ranged attack aim and fire at it like at a drone. Its
    /// health is MW2's (_helicopter.gsc heli_maxhealth 1500, Pave Low x2, _harrier.gsc 3000, _ac130.gsc
    /// 1000, _airdrop.gsc little bird 500) in RoR2 terms: times the owner's max health over MW2's 100,
    /// so it keeps up with leveling. Damage drives MW2's stages on the vehicle (Mw2Vehicle.Damaged);
    /// the killing hit is turned away and the vehicle crashes its MW2 way instead of dying as a RoR2
    /// body. Melee-only monsters never pick one (playtest: ground melee mobs still go for the player).
    /// Monsters live on the host, so the hitboxes do too: the host's own aircraft (Mw2Vehicle), and
    /// a teammate's as mirrored here (Mw2Net: the props message says which are shootable; the hits
    /// go back to the owner, whose aircraft takes them).
    interface IShootable
    {
        bool Crashing { get; }
        bool Dead { get; }
        void Kill();
        void Damaged(float healthFraction);
    }

    static class Mw2VehicleHealth
    {
        static readonly Dictionary<HealthComponent, IShootable> proxies = new Dictionary<HealthComponent, IShootable>();
        static readonly string[] prefabs = { "ExplosivePotDestructibleBody", "FusionCellDestructibleBody", "Drone1Body" };
        static GameObject prefab;
        static bool searched;

        public static void Init(Harmony harmony)
        {
            var take = AccessTools.Method(typeof(HealthComponent), "TakeDamage", new[] { typeof(DamageInfo) });
            if (take != null) harmony.Patch(take, prefix: new HarmonyMethod(typeof(Mw2VehicleHealth), nameof(TakeDamage)), postfix: new HarmonyMethod(typeof(Mw2VehicleHealth), nameof(AfterDamage)));
            var find = AccessTools.Method(typeof(BaseAI), "FindEnemyHurtBox", new[] { typeof(float), typeof(bool), typeof(bool) });
            if (find != null) harmony.Patch(find, postfix: new HarmonyMethod(typeof(Mw2VehicleHealth), nameof(FindEnemy)));
            else Plugin.Log.LogWarning("BaseAI.FindEnemyHurtBox not found: melee monsters may chase killstreak aircraft.");
            var eval = AccessTools.Method(typeof(BaseAI), "EvaluateSkillDrivers");
            if (eval != null) harmony.Patch(eval, prefix: new HarmonyMethod(typeof(Mw2VehicleHealth), nameof(StretchRanges)), postfix: new HarmonyMethod(typeof(Mw2VehicleHealth), nameof(RestoreRanges)));
        }

        /// Pilot / debug: the live hitboxes.
        public static IEnumerable<HealthComponent> Hitboxes => proxies.Keys.Where(h => h != null);

        public static bool IsVehicle(CharacterBody b) => b != null && b.healthComponent != null && proxies.ContainsKey(b.healthComponent);

        /// Server: give `v` a body monsters can shoot. `mw2Health` in MW2 points, `radius` in metres.
        public static GameObject Create(IShootable v, Vector3 at, CharacterBody owner, float mw2Health, float radius)
        {
            if (!NetworkServer.active || owner == null || owner.teamComponent == null) return null;
            if (!searched)
            {
                searched = true;
                foreach (var n in prefabs)
                {
                    prefab = BodyCatalog.FindBodyPrefab(n);
                    if (prefab != null) { Plugin.Log.LogInfo($"MW2 vehicle health: proxy body {n}"); break; }
                }
                if (prefab == null) Plugin.Log.LogWarning("MW2 vehicle health: no proxy body prefab; aircraft can't be shot down.");
            }
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            go.name = "MW2 vehicle hitbox";
            var body = go.GetComponent<CharacterBody>();
            var hc = go.GetComponent<HealthComponent>();
            if (body == null || hc == null) { UnityEngine.Object.Destroy(go); return null; }
            // Nothing of its own: no state machines (death / idle), motors, physics, AI, stun.
            foreach (var esm in go.GetComponents<EntityStateMachine>()) esm.enabled = false;
            foreach (var cdb in go.GetComponents<CharacterDeathBehavior>()) cdb.enabled = false;
            foreach (var ssh in go.GetComponents<SetStateOnHurt>()) UnityEngine.Object.Destroy(ssh);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>()) { rb.isKinematic = true; rb.useGravity = false; }
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
            var model = body.modelLocator != null && body.modelLocator.modelTransform != null ? body.modelLocator.modelTransform.GetComponent<CharacterModel>() : null;
            if (model != null) model.invisibilityCount++;
            // Its hurt boxes about the hull's size. They live on the model, which RoR2's ModelLocator
            // usually detaches from the body: the model is scaled and moved too (Follow).
            var modelT = body.modelLocator != null ? body.modelLocator.modelTransform : null;
            if (body.modelLocator != null) body.modelLocator.autoUpdateModelTransform = false;
            var hurt = (modelT != null ? modelT.gameObject : go).GetComponentInChildren<HurtBox>();
            float size = hurt != null && hurt.collider != null ? Mathf.Max(hurt.collider.bounds.extents.magnitude, 0.1f) : 1f;
            float k = Mathf.Clamp(radius / size, 0.2f, 40f);
            go.transform.localScale = Vector3.one * k;
            if (modelT != null && !modelT.IsChildOf(go.transform)) modelT.localScale = modelT.localScale * k;
            models[go] = modelT;
            if (body.teamComponent != null) body.teamComponent.teamIndex = owner.teamComponent.teamIndex;
            body.baseMaxHealth = mw2Health * Mathf.Max(owner.maxHealth, 1f) / 100f;
            body.levelMaxHealth = 0f;
            body.baseArmor = 0f;
            body.baseRegen = 0f;
            body.levelRegen = 0f;
            NetworkServer.Spawn(go);
            body.RecalculateStats();
            hc.health = hc.fullHealth;
            proxies[hc] = v;
            return go;
        }

        static readonly Dictionary<GameObject, Transform> models = new Dictionary<GameObject, Transform>();

        /// Keep the hitbox (body and its detached model with the hurt boxes) on the hull.
        public static void Follow(GameObject proxy, Vector3 at)
        {
            if (proxy == null) return;
            proxy.transform.position = at;
            if (models.TryGetValue(proxy, out var m) && m != null && !m.IsChildOf(proxy.transform)) m.position = at;
        }

        public static void Remove(GameObject proxy)
        {
            if (proxy == null) return;
            if (models.TryGetValue(proxy, out var m) && m != null && !m.IsChildOf(proxy.transform)) UnityEngine.Object.Destroy(m.gameObject);
            models.Remove(proxy);
            var hc = proxy.GetComponent<HealthComponent>();
            if (hc != null) proxies.Remove(hc);
            if (NetworkServer.active) NetworkServer.Destroy(proxy); else UnityEngine.Object.Destroy(proxy);
        }

        // The killing hit is turned away: the vehicle crashes MW2's way (Mw2Vehicle.Kill).
        static void TakeDamage(HealthComponent __instance, DamageInfo damageInfo)
        {
            if (damageInfo == null || __instance == null || !proxies.TryGetValue(__instance, out var v)) return;
            if (v.Crashing || v.Dead) { damageInfo.rejected = true; return; }
            float armor = __instance.body != null ? __instance.body.armor : 0f;
            float factor = armor >= 0f ? 100f / (100f + armor) : 2f - 100f / (100f - armor);
            if (damageInfo.damage * factor >= __instance.combinedHealth)
            {
                damageInfo.rejected = true;
                v.Kill();
            }
        }

        static void AfterDamage(HealthComponent __instance)
        {
            if (__instance != null && proxies.TryGetValue(__instance, out var v) && !v.Crashing) v.Damaged(__instance.combinedHealthFraction);
        }

        // ------------------------------------------------------------------ who targets them

        static readonly Dictionary<BaseAI, bool> ranged = new Dictionary<BaseAI, bool>();

        /// A monster with any skill driver reaching 30 m or more: it can shoot something in the air.
        static bool Ranged(BaseAI ai)
        {
            if (ranged.TryGetValue(ai, out bool r)) return r;
            r = ai.GetComponents<AISkillDriver>().Any(d => d.skillSlot != SkillSlot.None && d.maxDistance >= 30f);
            ranged[ai] = r;
            return r;
        }

        // RoR2 tests a skill's range as straight-line distance; an aircraft 40 m up is out of reach of
        // a monster standing under it. While a monster weighs its skills against one, its ranged
        // skills reach the height difference further (the shot itself still flies the whole way).
        static readonly List<(AISkillDriver d, float max)> stretched = new List<(AISkillDriver, float)>();

        static void StretchRanges(BaseAI __instance)
        {
            stretched.Clear();
            var en = __instance != null ? __instance.currentEnemy : null;
            var go = en != null ? en.gameObject : null;
            var hc = go != null ? go.GetComponent<HealthComponent>() : null;
            if (hc == null || !proxies.ContainsKey(hc) || __instance.body == null || !Ranged(__instance)) return;
            float dy = Mathf.Abs(go.transform.position.y - __instance.body.corePosition.y);
            foreach (var d in __instance.skillDrivers)
            {
                if (d == null || d.skillSlot == SkillSlot.None || d.maxDistance < 30f) continue;
                stretched.Add((d, d.maxDistance));
                d.maxDistance += dy;
            }
        }

        static void RestoreRanges()
        {
            foreach (var (d, max) in stretched) if (d != null) d.maxDistance = max;
            stretched.Clear();
        }

        static void FindEnemy(BaseAI __instance, float maxDistance, bool full360Vision, bool filterByLoS, ref HurtBox __result)
        {
            if (__result == null || __result.healthComponent == null || !proxies.ContainsKey(__result.healthComponent)) return;
            if (Ranged(__instance)) return;
            // Melee: the nearest target that isn't a killstreak aircraft.
            var body = __instance.body;
            if (body == null || body.teamComponent == null) { __result = null; return; }
            var search = new BullseyeSearch
            {
                viewer = body,
                teamMaskFilter = TeamMask.GetEnemyTeams(body.teamComponent.teamIndex),
                sortMode = BullseyeSearch.SortMode.Distance,
                minDistanceFilter = 0f,
                maxDistanceFilter = maxDistance,
                searchOrigin = body.corePosition,
                searchDirection = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward,
                maxAngleFilter = full360Vision ? 180f : 90f,
                filterByLoS = filterByLoS,
            };
            search.RefreshCandidates();
            __result = search.GetResults().FirstOrDefault(h => h != null && h.healthComponent != null && !proxies.ContainsKey(h.healthComponent));
        }
    }
}
