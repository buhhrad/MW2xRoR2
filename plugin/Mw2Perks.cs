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
    /// MW2's perks as RoR2 items (playtest 10-02-26): one item per perkTable "specialty_*" row, with
    /// MW2's icon, name and description. A class hands its perks out as items; the items are only
    /// the carrier (shown in RoR2's item bar, synced by RoR2 in multiplayer) - what a perk does
    /// comes from MW2's own data. They never drop and can't be removed, copied, printed or stolen.
    static class Mw2Perks
    {
        /// perkTable name (specialty_fastreload) -> item.
        public static readonly Dictionary<string, ItemDef> Items = new Dictionary<string, ItemDef>();
        static readonly Dictionary<string, string> strings = new Dictionary<string, string>();

        /// Class equipment that perkTable also lists: not perks.
        static readonly HashSet<string> notPerks = new HashSet<string> { "specialty_null", "specialty_tacticalinsertion", "specialty_blastshield" };

        public static void Init(Harmony harmony)
        {
            if (!Mw2Menus.Ready) return;
            foreach (var perk in Mw2Menus.TableColumn("mp/perkTable.csv", 1).Distinct())
            {
                if (!perk.StartsWith("specialty_") || notPerks.Contains(perk)) continue;
                string up = perk.ToUpperInvariant();
                string nameKey = Mw2Menus.TableLookup("mp/perkTable.csv", 1, perk, 2);
                string descKey = Mw2Menus.TableLookup("mp/perkTable.csv", 1, perk, 4);
                string icon = Mw2Menus.TableLookup("mp/perkTable.csv", 1, perk, 3);
                var def = ScriptableObject.CreateInstance<ItemDef>();
                def.name = "MW2_" + perk;
                def.nameToken = "MW2_PERK_" + up + "_NAME";
                def.pickupToken = def.descriptionToken = "MW2_PERK_" + up + "_DESC";
                def.loreToken = "";
                def.tier = ItemTier.NoTier;
                def.hidden = false;
                def.canRemove = false;
                def.tags = new[]
                {
                    ItemTag.CannotSteal, ItemTag.CannotCopy, ItemTag.CannotDuplicate, ItemTag.BrotherBlacklist,
                    ItemTag.AIBlacklist, ItemTag.WorldUnique, ItemTag.IgnoreForDropList, ItemTag.SacrificeBlacklist,
                    ItemTag.DevotionBlacklist, ItemTag.RebirthBlacklist,
                };
                var tex = string.IsNullOrEmpty(icon) ? null : Mw2Icons.Get(icon);
                if (tex != null) def.pickupIconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                strings[def.nameToken] = Mw2Menus.Localize(nameKey);
                strings[def.descriptionToken] = Mw2Menus.Localize(descKey);
                Items[perk] = def;
            }
            ContentManager.collectContentPackProviders += add => add(new Provider());
            var getString = AccessTools.Method(typeof(Language), "GetLocalizedStringByToken", new[] { typeof(string) });
            if (getString != null) harmony.Patch(getString, postfix: new HarmonyMethod(typeof(Mw2Perks), nameof(LocalizedString)));
            Plugin.Log.LogInfo($"MW2 perks as items: {Items.Count} ({string.Join(", ", Items.Keys.Take(6))} ...)");
        }

        static void LocalizedString(string token, ref string __result)
        {
            if (token != null && token.StartsWith("MW2_PERK_") && strings.TryGetValue(token, out var s)) __result = s;
        }

        class Provider : IContentPackProvider
        {
            readonly ContentPack pack = new ContentPack();
            public string identifier => Plugin.Guid + ".perks";

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

        /// Swap the body's perk items for `perks` (perkTable names; empty / specialty_null skipped).
        /// Inventories belong to the server: a client works out its set (its unlocks, its deathstreak)
        /// and the host gives the items (Mw2Net KClass).
        public static void Apply(CharacterBody body, IEnumerable<string> perks)
        {
            if (body == null || body.inventory == null || Items.Count == 0) return;
            var want = Resolve(perks);
            if (NetworkServer.active) Give(body, want);
            else Mw2Net.SendClass(body, want);
            Plugin.Log.LogInfo($"MW2 perks: {string.Join(", ", want)}{(NetworkServer.active ? "" : " (sent to the host)")}");
        }

        /// The perk items a class carries: its perks, each one's Pro once unlocked, the deathstreak.
        static HashSet<string> Resolve(IEnumerable<string> perks)
        {
            var want = new HashSet<string>(perks.Where(p => p != null && Items.ContainsKey(p)));
            // The deathstreak earned this life shows as an item too (Painkiller only while it lasts).
            if (Mw2Deathstreaks.ItemShown && Items.ContainsKey(Mw2Deathstreaks.Active)) want.Add(Mw2Deathstreaks.Active);
            // _class.gsc loadoutAllPerks: each perk's Pro (perkTable column 8) comes with it once unlocked.
            foreach (var p in want.ToArray())
            {
                string pro = Mw2Menus.TableLookup("mp/perkTable.csv", 1, p, 8);
                if (pro.Length > 0 && pro != p && Items.ContainsKey(pro) && Mw2Menus.ItemUnlocked(pro)) want.Add(pro);
            }
            return want;
        }

        /// Server: set the body's perk items to exactly `want` (names that aren't MW2 perks ignored).
        public static void Give(CharacterBody body, ICollection<string> want)
        {
            var inv = body != null ? body.inventory : null;
            if (inv == null || !NetworkServer.active) return;
            foreach (var kv in Items)
            {
                var idx = kv.Value.itemIndex;
                if (idx == ItemIndex.None) continue;
                int have = inv.GetItemCount(idx);
                bool keep = want.Contains(kv.Key);
                if (!keep && have > 0) inv.RemoveItem(idx, have);
                else if (keep && have == 0) inv.GiveItem(idx, 1);
            }
        }

        /// IW4L's engine perk bits (ps.perks[0]) for what the body carries. Values from IW4L:
        /// weapon_iw4 spread.rs / tick.rs / view_bob.rs, movement_iw4 sprint.rs / mantle.rs.
        static readonly (string perk, uint bit)[] engineBits =
        {
            ("specialty_bulletaccuracy", 0x2u),          // hip spread x0.65
            ("specialty_fastreload", 0x4u),              // reload states x2
            ("specialty_lightweight", 0x0100_0000u),     // view bob x0.75
            ("specialty_marathon", 0x0200_0000u),        // unlimited sprint
            ("specialty_fastmantle", 1u << 19),          // Marathon Pro: fast mantle
        };

        public const uint MarathonBit = 0x0200_0000u; // unlimited sprint

        public static uint EngineBits(CharacterBody body)
        {
            uint bits = 0;
            foreach (var (perk, bit) in engineBits)
                if (Has(body, perk)) bits |= bit;
            return bits;
        }

        /// _perks.gsc cac_modified_damage: Stopping Power +perk_bulletDamage (40%) on bullets.
        public static float BulletDamage(CharacterBody body) => Has(body, "specialty_bulletdamage") ? 1.4f : 1f;

        /// _perks.gsc cac_modified_damage: Danger Close +perk_explosiveDamage (40%) on explosives.
        public static float ExplosiveDamage(CharacterBody body) => Has(body, "specialty_explosivedamage") ? 1.4f : 1f;

        /// _perks.gsc: Lightweight sets moveSpeedScaler 1.07. Marathon's MW2 effect (unlimited
        /// sprint) is everyone's here (config UnlimitedSprint), so with that on it sprints 10%
        /// faster instead (playtest 10-04-26) - not MW2.
        public static float MoveSpeed(CharacterBody body, bool sprinting = false) =>
            (Has(body, "specialty_lightweight") ? 1.07f : 1f)
            * (sprinting && Plugin.Instance.UnlimitedSprint.Value && Has(body, "specialty_marathon") ? 1.1f : 1f);

        /// _perks.gsc cac_modified_damage: Danger Close Pro +perk_dangerClose (100%) on killstreak explosives.
        public static float KillstreakExplosiveDamage(CharacterBody body) => Has(body, "specialty_dangerclose") ? 2f : 1f;

        /// Does this body carry the perk (its item)?
        public static bool Has(CharacterBody body, string perk)
        {
            var inv = body != null ? body.inventory : null;
            return inv != null && Items.TryGetValue(perk, out var def) && def.itemIndex != ItemIndex.None && inv.GetItemCount(def.itemIndex) > 0;
        }
    }
}
