using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// The MW2 Soldier's skins (playtest 10-03-26): every MP body MW2's scripts give a faction and class
    /// (mw2_character_skins, about 88 in the retail install), on RoR2's own skin row in the loadout
    /// panel. Skin 0 is the config faction (Character.Faction). A skin is a character key,
    /// `faction|class|variant|head`; the head is rolled each spawn from the faction's pool, as MW2's
    /// mptype scripts do. Teammates get the key with the player state (Mw2Net), so they see it too.
    unsafe static class Mw2Skins
    {
        public struct Entry
        {
            public string faction, cls, body;
            public int variant;
        }

        public static readonly List<Entry> Catalog = new List<Entry>();
        public static readonly List<SkinDef> Defs = new List<SkinDef>();

        public static void LoadCatalog()
        {
            Catalog.Clear();
            uint n = Native.mw2_character_skins(null, 0);
            if (n == 0) { Plugin.Log.LogWarning("MW2 skins: no catalog from the install"); return; }
            var buf = new byte[n];
            fixed (byte* p = buf) n = Native.mw2_character_skins(p, (uint)buf.Length);
            foreach (var line in System.Text.Encoding.UTF8.GetString(buf, 0, (int)Math.Min(n, (uint)buf.Length)).Split('\n'))
            {
                var c = line.Split('\t');
                if (c.Length < 4 || !int.TryParse(c[2], out int v)) continue;
                Catalog.Add(new Entry { faction = c[0], cls = c[1], variant = v, body = c[3] });
            }
            Plugin.Log.LogInfo($"MW2 skins: {Catalog.Count} bodies");
        }

        // ------------------------------------------------------------------ names

        static readonly Dictionary<string, string> classNames = new Dictionary<string, string>
        {
            { "assault", "Assault" }, { "smg", "SMG" }, { "lmg", "LMG" }, { "shotgun", "Shotgun" }, { "sniper", "Sniper" }, { "riot", "Riot Shield" },
        };

        /// mp/factionTable.csv row for a faction ref; the TF141 camo sets share socom_141's row if they have none.
        public static string Table(string faction, int col)
        {
            string s = Mw2Menus.TableLookup("mp/factionTable.csv", 0, faction, col);
            if (s.Length == 0 && faction.StartsWith("socom_141")) s = Mw2Menus.TableLookup("mp/factionTable.csv", 0, "socom_141", col);
            return s;
        }

        /// The team name MW2 shows (factionTable col 1), plus the camo for factions that share one.
        public static string FactionName(string faction)
        {
            string key = Table(faction, 1);
            string name = key.Length > 0 ? Mw2Menus.Localize(key) : "";
            if (name.Length == 0 || name == key) name = faction;
            foreach (var camo in new[] { "desert", "forest", "arctic" })
                if (faction.EndsWith("_" + camo) && name.IndexOf(camo, StringComparison.OrdinalIgnoreCase) < 0)
                    name += $" ({char.ToUpper(camo[0])}{camo.Substring(1)})";
            return name;
        }

        public static string Name(Entry e)
        {
            string cls = classNames.TryGetValue(e.cls, out var c) ? c : e.cls;
            bool more = Catalog.Exists(x => x.faction == e.faction && x.cls == e.cls && x.variant != e.variant);
            return $"{FactionName(e.faction)}: {cls}{(more ? " " + (char)('A' + e.variant) : "")}";
        }

        // ------------------------------------------------------------------ keys

        /// Skin `index` on the MW2 Soldier (0 = the config faction) as a character key.
        public static string Key(int index, int head)
        {
            if (index <= 0 || index > Catalog.Count) return Plugin.Instance.BodyFaction.Value ?? "us_army";
            var e = Catalog[index - 1];
            return $"{e.faction}|{e.cls}|{e.variant}|{head}";
        }

        /// The key for a body this spawn: its skin with a rolled head, or the config faction for
        /// other survivors in MW2 mode (F6).
        public static string KeyFor(CharacterBody body)
        {
            if (!Mw2Survivor.IsMw2(body)) return Plugin.Instance.BodyFaction.Value ?? "us_army";
            return Key((int)body.skinIndex, UnityEngine.Random.Range(0, 8));
        }

        public static void Parse(string key, out string faction, out string cls, out int variant, out int head)
        {
            var p = (key ?? "").Split('|');
            faction = p.Length > 0 && p[0].Length > 0 ? p[0] : "us_army";
            cls = p.Length > 1 ? p[1] : "";
            variant = p.Length > 2 && int.TryParse(p[2], out int v) ? v : 0;
            head = p.Length > 3 && int.TryParse(p[3], out int h) ? h : 0;
        }

        public static string FactionOf(string key) { Parse(key, out var f, out _, out _, out _); return f; }

        /// The local player's faction: their MW2 Soldier skin's (from their loadout, so it's right
        /// before the body spawns), else the config faction.
        public static string LocalFaction()
        {
            var user = LocalUserManager.GetFirstLocalUser();
            var bi = BodyCatalog.FindBodyIndex(Mw2Survivor.BodyName);
            var nu = user?.currentNetworkUser;
            if (user?.userProfile?.loadout != null && bi != BodyIndex.None && (nu == null || nu.bodyIndexPreference == bi))
            {
                int skin = (int)user.userProfile.loadout.bodyLoadoutManager.GetSkinIndex(bi);
                if (skin > 0 && skin <= Catalog.Count) return Catalog[skin - 1].faction;
            }
            return Plugin.Instance.BodyFaction.Value ?? "us_army";
        }

        /// The announcer prefix (US, UK, RU ... factionTable col 7) for the local skin's faction;
        /// the Killstreaks.Faction config on the default skin.
        public static string Voice()
        {
            string cfg = Plugin.Instance.Faction.Value;
            string f = LocalFaction();
            if (f == Plugin.Instance.BodyFaction.Value) return cfg;
            string p = Table(f, 7).TrimEnd('_');
            return p.Length > 0 ? p : cfg;
        }

        // ------------------------------------------------------------------ skin defs

        /// RoR2 skins for the MW2 Soldier: copies of Commando's default skin (the hidden Commando model
        /// keeps its materials) with MW2's names and faction emblems. Same list on the body's and the
        /// display's ModelSkinController so the loadout panel and character select agree.
        public static void Build(GameObject body, GameObject display)
        {
            Defs.Clear();
            var msc = body.GetComponent<ModelLocator>()?.modelTransform?.GetComponent<ModelSkinController>();
            if (msc == null || msc.skins == null || msc.skins.Length == 0) { Plugin.Log.LogWarning("MW2 skins: no ModelSkinController on the body"); return; }
            var template = msc.skins[0];
            Defs.Add(Def(template, 0, "MW2_SOLDIER_SKIN_DEFAULT", $"Default ({FactionName(Plugin.Instance.BodyFaction.Value)})", Plugin.Instance.BodyFaction.Value));
            for (int i = 0; i < Catalog.Count; i++)
                Defs.Add(Def(template, i + 1, "MW2_SOLDIER_SKIN_" + (i + 1), Name(Catalog[i]), Catalog[i].faction));
            var arr = Defs.ToArray();
            msc.skins = arr;
            var dmsc = display != null ? display.GetComponentInChildren<ModelSkinController>(true) : null;
            if (dmsc != null) dmsc.skins = arr;
        }

        static readonly Dictionary<string, Sprite> emblems = new Dictionary<string, Sprite>();

        static SkinDef Def(SkinDef template, int i, string token, string name, string faction)
        {
            var d = UnityEngine.Object.Instantiate(template);
            ((ScriptableObject)d).name = "MW2SoldierSkin" + i;
            d.nameToken = token;
            d.unlockableDef = null;
            d.unlockableName = "";
            Mw2Survivor.SetString(token, name);
            var icon = Emblem(faction);
            if (icon != null) d.icon = icon;
            return d;
        }

        /// The faction's MW2 emblem (factionTable col 5, faction_128_*); null until MW2's tables are loaded (the
        /// skins are made at content load: their icons are filled in later, see Emblem callers).
        public static Sprite Emblem(string faction)
        {
            if (emblems.TryGetValue(faction, out var icon) && icon != null) return icon;
            var tex = Mw2Icons.Get(Table(faction, 5));
            icon = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)) : null;
            if (icon != null) emblems[faction] = icon;
            if (icon != null)
                for (int i = 0; i < Defs.Count; i++)
                    if (Defs[i] != null && (i == 0 ? Plugin.Instance.BodyFaction.Value : Catalog[i - 1].faction) == faction) Defs[i].icon = icon;
            return icon;
        }
    }
}
