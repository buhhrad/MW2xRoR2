using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RoR2;
using RoR2.ContentManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// MW2 killstreaks as chest loot (playtest 10-03-26): a chest that would drop a white / green / red
    /// item sometimes (Killstreaks/ChestChance) drops a killstreak pickup instead. Picking one up
    /// works like swapping RoR2 equipment: it takes the place of the loadout streak with the
    /// nearest kill count for the rest of the run, and the streak it replaced drops at your feet.
    /// Picking up one you already carry gives it to you on the spot. The item is only a carrier:
    /// the host takes it straight back out of the inventory and tells the owner (Mw2Net).
    static unsafe class Mw2StreakItems
    {
        /// Streak id -> its pickup item.
        public static readonly Dictionary<uint, ItemDef> Items = new Dictionary<uint, ItemDef>();
        static readonly Dictionary<string, string> strings = new Dictionary<string, string>();

        public static void Init(Harmony harmony)
        {
            if (!Mw2Menus.Ready || Native.mw2_streak_table_count() == 0) return;
            uint n = Native.mw2_streak_table_count();
            for (uint id = 1; id <= n + 1; id++)
            {
                if (Native.mw2_streak_kills(id) == 0) continue;
                string name = Native.StreakString(id, 0);
                if (!Mw2Killstreaks.Built(name)) continue;
                string up = name.ToUpperInvariant();
                var def = ScriptableObject.CreateInstance<ItemDef>();
                def.name = "MW2_KS_" + name;
                def.nameToken = "MW2_KS_" + up + "_NAME";
                def.pickupToken = def.descriptionToken = "MW2_KS_" + up + "_DESC";
                def.loreToken = "";
                def.tier = ItemTier.NoTier;
                def.hidden = false;
                def.canRemove = true;
                def.tags = new[]
                {
                    ItemTag.CannotSteal, ItemTag.CannotCopy, ItemTag.CannotDuplicate, ItemTag.BrotherBlacklist,
                    ItemTag.AIBlacklist, ItemTag.IgnoreForDropList, ItemTag.SacrificeBlacklist,
                    ItemTag.DevotionBlacklist, ItemTag.RebirthBlacklist,
                };
                def.pickupModelPrefab = PickupPrefab(name);
                // killstreakTable: 2 name, 3 description, 14 icon.
                var tex = Mw2Icons.Get(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, name, 14));
                if (tex != null) def.pickupIconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                strings[def.nameToken] = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, name, 2));
                strings[def.descriptionToken] = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, name, 3));
                Items[id] = def;
            }
            ContentManager.collectContentPackProviders += add => add(new Provider());
            var getString = AccessTools.Method(typeof(Language), "GetLocalizedStringByToken", new[] { typeof(string) });
            if (getString != null) harmony.Patch(getString, postfix: new HarmonyMethod(typeof(Mw2StreakItems), nameof(LocalizedString)));
            var drop = AccessTools.Method(typeof(ChestBehavior), "ItemDrop");
            if (drop != null) harmony.Patch(drop, prefix: new HarmonyMethod(typeof(Mw2StreakItems), nameof(ChestDrop)));
            else Plugin.Log.LogWarning("ChestBehavior.ItemDrop not found: no killstreak chest drops.");
            foreach (var give in new[] { "GiveItem", "GiveItemPermanent" })
            {
                var m = AccessTools.Method(typeof(Inventory), give, new[] { typeof(ItemIndex), typeof(int) });
                if (m != null) harmony.Patch(m, postfix: new HarmonyMethod(typeof(Mw2StreakItems), nameof(Given)));
            }
            Plugin.Log.LogInfo($"MW2 killstreak pickups: {Items.Count}");
        }

        // ---- the pickup on the ground: the streak's own MW2 model at item size (playtest 10-06-26) ----
        static GameObject holder;
        static GameObject mystery;

        /// A prefab RoR2 spawns for the pickup (spinning and bobbing it like any item): a loader that
        /// builds the streak's MW2 model there - the mesh needs MW2's files, so it is made on the spot.
        static GameObject PickupPrefab(string streak)
        {
            if (mystery == null) mystery = LegacyResourcesAPI.Load<GameObject>("Prefabs/PickupModels/PickupMystery");
            if (holder == null) { holder = new GameObject("MW2 streak pickup prefabs"); holder.SetActive(false); Object.DontDestroyOnLoad(holder); }
            var go = new GameObject("MW2 pickup " + streak);
            go.transform.SetParent(holder.transform, false);
            go.AddComponent<Mw2StreakPickupModel>().streak = streak;
            return go;
        }

        /// The MW2 model for a streak's pickup (null: the mystery box).
        internal static string ModelFor(string streak)
        {
            switch (streak)
            {
                case "uav": case "counter_uav": return "vehicle_uav_static_mp";
                case "airdrop": case "airdrop_mega": return "com_plasticcase_friendly";
                case "sentry": case "airdrop_sentry_minigun": return "sentry_minigun";
                case "precision_airstrike": return "vehicle_mig29_desert";
                case "harrier_airstrike": return "vehicle_av8b_harrier_jet_mp";
                case "helicopter": return "vehicle_cobra_helicopter_fly_low";
                case "helicopter_flares": return "vehicle_pavelow";
                case "helicopter_minigun": return "vehicle_little_bird_armed";
                case "stealth_airstrike": return "vehicle_b2_bomber";
                case "ac130": return "vehicle_ac130_low_mp";
                // Nuke / EMP use the hand-held trigger, which has no world model: stand-ins.
                case "nuke": return "projectile_stealth_bomb_mk84";
                case "emp": return "viewmodel_briefcase_bomb_mp";
                case "predator_missile":
                    Mw2Projectiles.ProjectileOf(Native.WeaponIndex("remotemissile_projectile_mp"), out var pm, out _);
                    return string.IsNullOrEmpty(pm) ? null : pm;
            }
            // EMP, Nuke...: the killstreak's own item (its world model), if it has one.
            var nb = new byte[96];
            int len;
            unsafe { fixed (byte* p = nb) len = Native.mw2_weapon_world_model(Native.WeaponIndex($"killstreak_{streak}_mp"), p, 96); }
            return len > 0 ? System.Text.Encoding.UTF8.GetString(nb, 0, System.Math.Min(len, 96)) : null;
        }

        internal static GameObject Mystery => mystery;

        static void LocalizedString(string token, ref string __result)
        {
            if (token != null && token.StartsWith("MW2_KS_") && strings.TryGetValue(token, out var s)) __result = s;
        }

        static uint StreakOf(ItemIndex index)
        {
            if (index == ItemIndex.None) return 0;
            foreach (var kv in Items) if (kv.Value.itemIndex == index) return kv.Key;
            return 0;
        }

        public static PickupIndex PickupOf(uint streak) =>
            Items.TryGetValue(streak, out var def) && def.itemIndex != ItemIndex.None ? PickupCatalog.FindPickupIndex(def.itemIndex) : PickupIndex.none;

        static readonly System.Reflection.PropertyInfo currentPickup = AccessTools.Property(typeof(ChestBehavior), "currentPickup");

        /// Server: a chest is about to drop an ordinary item; sometimes make it a killstreak.
        static void ChestDrop(ChestBehavior __instance)
        {
            if (!NetworkServer.active || Items.Count == 0 || currentPickup == null || __instance == null) return;
            if (Random.value >= Plugin.Instance.StreakChestChance.Value) return;
            var cur = (UniquePickup)currentPickup.GetValue(__instance);
            var def = PickupCatalog.GetPickupDef(cur.pickupIndex);
            var item = def != null && def.itemIndex != ItemIndex.None ? ItemCatalog.GetItemDef(def.itemIndex) : null;
            if (item == null || (item.tier != ItemTier.Tier1 && item.tier != ItemTier.Tier2 && item.tier != ItemTier.Tier3)) return;
            var streak = Items.Keys.ElementAt(Random.Range(0, Items.Count));
            var pick = PickupOf(streak);
            if (pick == PickupIndex.none) return;
            currentPickup.SetValue(__instance, cur.WithPickupIndex(pick));
            Plugin.Log.LogInfo($"MW2 chest: {item.name} -> killstreak {Native.StreakString(streak, 0)}");
        }

        /// Server: a killstreak pickup reached an inventory - take it back out and hand the streak
        /// to whoever plays that body.
        static void Given(Inventory __instance, ItemIndex itemIndex)
        {
            if (!NetworkServer.active || __instance == null) return;
            uint streak = StreakOf(itemIndex);
            if (streak == 0) return;
            int n = __instance.GetItemCountPermanent(itemIndex);
            if (n <= 0) return;
            __instance.RemoveItemPermanent(itemIndex, n);
            var master = __instance.GetComponent<CharacterMaster>();
            var body = master != null ? master.GetBody() : null;
            if (body != null) Mw2Net.ServerStreakPickup(body, streak);
        }

        /// Server: drop a killstreak pickup out of `body` (the streak a pickup replaced).
        public static void ServerDrop(CharacterBody body, uint streak)
        {
            var pick = PickupOf(streak);
            if (!NetworkServer.active || body == null || pick == PickupIndex.none) return;
            var fwd = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
            fwd = Vector3.ProjectOnPlane(fwd, Vector3.up).normalized;
            PickupDropletController.CreatePickupDroplet(pick, body.corePosition + Vector3.up, fwd * 6f + Vector3.up * 15f);
        }

        class Provider : IContentPackProvider
        {
            readonly ContentPack pack = new ContentPack();
            public string identifier => Plugin.Guid + ".killstreaks";

            public IEnumerator LoadStaticContentAsync(LoadStaticContentAsyncArgs args)
            {
                pack.itemDefs.Add(Items.Values.ToArray());
                args.ReportProgress(1f);
                yield break;
            }

            public IEnumerator GenerateContentPackAsync(GetContentPackAsyncArgs args)
            {
                ContentPack.Copy(pack, args.output);
                args.ReportProgress(1f);
                yield break;
            }

            public IEnumerator FinalizeAsync(FinalizeAsyncArgs args)
            {
                args.ReportProgress(1f);
                yield break;
            }
        }
    }

    /// Builds a killstreak pickup's MW2 model inside RoR2's pickup display (which spins and bobs it),
    /// fitted to an item's size; the mystery box if the model can't be built.
    class Mw2StreakPickupModel : MonoBehaviour
    {
        public string streak;
        const float ItemSize = 1.1f; // metres across, about a RoR2 item on the ground

        // Awake (inside RoR2's Instantiate), so the pickup display finds a renderer for its highlight.
        void Awake()
        {
            if (string.IsNullOrEmpty(streak)) { var n = gameObject.name; int i = n.IndexOf("MW2 pickup "); if (i >= 0) streak = n.Substring(i + 11).Replace("(Clone)", "").Trim(); }
            GameObject model = null;
            string name = Mw2StreakItems.ModelFor(streak);
            if (!string.IsNullOrEmpty(name))
            {
                Mw2Prop.Mirroring++; // everyone builds their own: not a prop for multiplayer to mirror
                try { model = Mw2Prop.Build(name, null, Space.Scale > 0f ? Space.Scale : 0.0254f); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"MW2 pickup model {name}: {e.Message}"); }
                finally { Mw2Prop.Mirroring--; }
            }
            if (model == null)
            {
                if (Mw2StreakItems.Mystery != null) Instantiate(Mw2StreakItems.Mystery, transform, false);
                return;
            }
            model.transform.SetParent(transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            var rs = model.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (size > 1e-4f) model.transform.localScale *= ItemSize / size;
            // Centred on the display's pivot.
            model.transform.position += transform.position - (transform.position + (b.center - transform.position) * (ItemSize / Mathf.Max(size, 1e-4f)));
            foreach (var r in rs) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
