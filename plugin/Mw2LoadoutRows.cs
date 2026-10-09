using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.Events;

namespace MW2RoR2
{
    /// MW2's Create-a-Class and Create-a-Streak inside the MW2 Soldier's Loadout tab (playtest 10-03-26),
    /// as RoR2 rows under Skin: "Class" (your custom classes and MW2's defaults by their primary's
    /// icon, picked like a skill variant, plus Create-a-Class) and "Killstreaks" (the class's three
    /// streaks; click to open Create-a-Streak). Built through RoR2's own LoadoutPanelController.Row
    /// so they look and behave like the rows above them.
    static class Mw2LoadoutRows
    {
        static readonly string[] defaultClasses = { "Grenadier", "First Recon", "Overwatch", "Scout Sniper", "Riot Control" };
        static Type rowType;
        static ConstructorInfo rowCtor;
        static MethodInfo addButton, finishSetup, dispose;
        static FieldInfo rowsField, findChoice, displayData, ddBody, ddProfile, highlightRect;

        public static void Init(Harmony harmony)
        {
            rowType = AccessTools.Inner(typeof(LoadoutPanelController), "Row");
            rowCtor = rowType != null ? AccessTools.Constructor(rowType, new[] { typeof(LoadoutPanelController), typeof(BodyIndex), typeof(string) }) : null;
            addButton = rowType != null ? AccessTools.Method(rowType, "AddButton") : null;
            finishSetup = rowType != null ? AccessTools.Method(rowType, "FinishSetup") : null;
            dispose = rowType != null ? AccessTools.Method(rowType, "Dispose") : null;
            highlightRect = rowType != null ? AccessTools.Field(rowType, "choiceHighlightRect") : null;
            findChoice = rowType != null ? AccessTools.Field(rowType, "findCurrentChoice") : null;
            rowsField = AccessTools.Field(typeof(LoadoutPanelController), "rows");
            displayData = AccessTools.Field(typeof(LoadoutPanelController), "currentDisplayData");
            ddBody = displayData != null ? AccessTools.Field(displayData.FieldType, "bodyIndex") : null;
            ddProfile = displayData != null ? AccessTools.Field(displayData.FieldType, "userProfile") : null;
            if (rowCtor == null || addButton == null || finishSetup == null || rowsField == null || ddBody == null)
            {
                Plugin.Log.LogWarning("MW2 loadout rows: RoR2's loadout panel changed; the rows are off (F3 / F5 still open the menus)");
                return;
            }
            harmony.Patch(AccessTools.Method(typeof(LoadoutPanelController), "Rebuild"), postfix: new HarmonyMethod(typeof(Mw2LoadoutRows), nameof(AfterRebuild)));
        }

        /// The MW2 Soldier's panel is showing (the bottom-of-screen key hints then aren't needed).
        public static bool Showing;

        static BodyIndex BodyOf(LoadoutPanelController p) => (BodyIndex)ddBody.GetValue(displayData.GetValue(p));

        static void AfterRebuild(LoadoutPanelController __instance)
        {
            try
            {
                var bi = BodyOf(__instance);
                Showing = bi != BodyIndex.None && bi == BodyCatalog.FindBodyIndex(Mw2Survivor.BodyName);
                if (!Showing || !Mw2Menus.Ready) return;
                var rows = (IList)rowsField.GetValue(__instance);
                // RoR2's skin row would be ~90 buttons wide: a selector instead (SkinRow).
                if (rows.Count > 0 && dispose != null)
                {
                    var last = rows[rows.Count - 1];
                    dispose.Invoke(last, null);
                    rows.RemoveAt(rows.Count - 1);
                }
                rows.Add(SkinRow(__instance, bi));
                rows.Add(ClassRow(__instance, bi));
                rows.Add(StreakRow(__instance, bi));
                rows.Add(RankRow(__instance, bi));
                // Field of view lives in RoR2's settings (Gameplay: Mw2FovSetting), changeable mid-game.
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 loadout rows: {e}"); }
        }

        /// Rebuild open loadout panels (the class / streak picks changed in MW2's menus) next frame
        /// (a click handler must not destroy its own button).
        public static void Refresh() => refreshPending = true;
        static bool refreshPending;

        public static void Update()
        {
            if (!refreshPending) return;
            refreshPending = false;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<LoadoutPanelController>())
                if (p.isActiveAndEnabled) AccessTools.Method(typeof(LoadoutPanelController), "Rebuild").Invoke(p, null);
        }

        static object NewRow(LoadoutPanelController owner, BodyIndex bi, string token, string title)
        {
            Mw2Survivor.SetString(token, title);
            return rowCtor.Invoke(new object[] { owner, bi, token });
        }

        static void Button(object row, LoadoutPanelController owner, Sprite icon, string key, string title, string body, UnityAction click, int index)
        {
            string t = "MW2_SOLDIER_LOADOUT_" + key + "_NAME", b = "MW2_SOLDIER_LOADOUT_" + key + "_DESC";
            Mw2Survivor.SetString(t, title);
            Mw2Survivor.SetString(b, body);
            addButton.Invoke(row, new object[] { owner, icon, t, b, new Color(0.55f, 0.6f, 0.45f), click, "", null, false, index });
        }

        // ------------------------------------------------------------------ Class

        static object ClassRow(LoadoutPanelController owner, BodyIndex bi)
        {
            var classes = new System.Collections.Generic.List<(int n, string[] cls)>();
            // Your custom classes (1-10) only once Create-a-Class is unlocked (level 4, as MW2).
            bool cac = Mw2Progress.CreateAClassUnlocked;
            int slots = Mw2Progress.CustomClassSlots;
            for (int n = 1; n <= 15; n++)
            {
                if ((n <= 10 && n > slots) || !Mw2Progress.DefaultClassOpen(n)) continue;
                var cls = Mw2Menus.ClassLoadout(n - 1);
                if (cls != null && cls.Length >= 11 && cls[0].Length > 0) classes.Add((n, cls));
            }
            return Carousel(owner, bi, "CLASS", "Class", classes.Count,
                i => (WeaponIcon(classes[i].cls[0]), classes[i].n <= 10 ? CustomName(classes[i].n) : defaultClasses[classes[i].n - 11], Describe(classes[i].cls)),
                i => { Plugin.Instance.UseCustomClass.Value = true; Plugin.Instance.Class.Value = classes[i].n; },
                () => classes.FindIndex(c => c.n == Plugin.Instance.PlayClass),
                cac ? (Mw2BindIcons.Plus(), "Create-a-Class", $"Open MW2's Create-a-Class to edit your classes ({Plugin.Instance.CreateAClassKey.Value}).", (UnityAction)(() => Mw2Menus.Open("cac_popup")))
                    : ((Sprite, string, string, UnityAction)?)null);
        }

        // ------------------------------------------------------------------ Skin

        /// The MW2 Soldier's ~90 skins (Mw2Skins) on a selector; a pick sets RoR2's own loadout skin
        /// (character select's model follows it, and it's networked like any skin).
        static object SkinRow(LoadoutPanelController owner, BodyIndex bi)
        {
            var profile = ddProfile?.GetValue(displayData.GetValue(owner)) as UserProfile;
            int count = Mw2Skins.Catalog.Count + 1;
            return Carousel(owner, bi, "SKIN", "Skin", count,
                i => (Mw2Skins.Emblem(i == 0 ? Plugin.Instance.BodyFaction.Value : Mw2Skins.Catalog[i - 1].faction),
                      i == 0 ? $"Default ({Mw2Skins.FactionName(Plugin.Instance.BodyFaction.Value)})" : Mw2Skins.Name(Mw2Skins.Catalog[i - 1]),
                      $"MW2 skin {i} of {count - 1}. The head is MW2's pick each spawn."),
                i =>
                {
                    if (profile == null) return;
                    var lo = new Loadout();
                    profile.CopyLoadout(lo);
                    lo.bodyLoadoutManager.SetSkinIndex(bi, (uint)i);
                    profile.SetLoadout(lo);
                },
                () => profile != null ? (int)profile.loadout.bodyLoadoutManager.GetSkinIndex(bi) : 0,
                null);
        }

        // ------------------------------------------------------------------ selector

        const int Window = 8;
        static readonly System.Collections.Generic.Dictionary<string, int> pageStart = new System.Collections.Generic.Dictionary<string, int>();

        /// A row that shows `count` items `Window` at a time between a left and right arrow (wrapping
        /// around), like a selector wheel; `extra` is an always-shown last button (Create-a-Class).
        static object Carousel(LoadoutPanelController owner, BodyIndex bi, string key, string title, int count,
            Func<int, (Sprite icon, string name, string desc)> item, Action<int> pick, Func<int> current,
            (Sprite icon, string name, string desc, UnityAction click)? extra)
        {
            var row = NewRow(owner, bi, "MW2_SOLDIER_LOADOUT_" + key, title);
            // 10 buttons fit beside the label (11 ran just past the panel): 8 per page between the
            // arrows, 7 with an extra button.
            int window = extra.HasValue ? Window - 1 : Window;
            bool paged = count > window;
            int cur = current();
            if (!pageStart.TryGetValue(key, out int start)) start = paged && cur >= 0 ? cur / window * window : 0;
            if (start >= count) start = 0;
            pageStart[key] = start;
            int shown = paged ? window : count, b = 0;
            if (paged)
                Button(row, owner, Mw2BindIcons.Arrow(false), key + "_PREV", "Previous", $"{title}: {start + 1}-{Math.Min(start + window, count)} of {count}", () =>
                {
                    int s0 = pageStart[key] - window;
                    pageStart[key] = s0 < 0 ? (count - 1) / window * window : s0; // wraps to the last page
                    Refresh();
                }, b++);
            for (int k = 0; k < shown && start + k < count; k++)
            {
                int idx = start + k;
                var it = item(idx);
                Button(row, owner, it.icon, key + idx, it.name, it.desc, () => { pick(idx); Refresh(); }, b++);
            }
            if (paged)
                Button(row, owner, Mw2BindIcons.Arrow(true), key + "_NEXT", "Next", $"{title}: {start + 1}-{Math.Min(start + window, count)} of {count}", () =>
                {
                    int s0 = pageStart[key] + window;
                    pageStart[key] = s0 >= count ? 0 : s0; // wraps to the first page
                    Refresh();
                }, b++);
            if (extra.HasValue)
            {
                var x = extra.Value;
                Button(row, owner, x.icon, key + "_EXTRA", x.name, x.desc, x.click, b++);
            }
            int lead = paged ? 1 : 0;
            findChoice.SetValue(row, (Func<Loadout, int>)(_ =>
            {
                int c = current() - pageStart[key];
                return c >= 0 && c < shown ? c + lead : -1;
            }));
            finishSetup.Invoke(row, new object[] { false });
            HideHighlightIfNone(row, current() - start, shown);
            return row;
        }

        /// RoR2 parks the choice highlight on a slot past the buttons when nothing in the row is
        /// picked (this page of a selector, the Killstreaks row): hide it then.
        static void HideHighlightIfNone(object row, int c, int shown)
        {
            var rt = highlightRect?.GetValue(row) as RectTransform;
            if (rt != null) rt.gameObject.SetActive(c >= 0 && c < shown);
        }

        static string CustomName(int n)
        {
            string s = Mw2Menus.PlayerData($"customclasses.{n - 1}.name");
            return string.IsNullOrEmpty(s) ? $"Custom Class {n}" : s;
        }

        /// MW2's name for a weapon (mp/statsTable.csv name column, by base weapon name).
        static string Pretty(string weapon) => weapon.Length == 0 ? "" : Mw2Menus.Localize(Mw2Menus.TableLookup("mp/statsTable.csv", 4, weapon.Split('_')[0], 3));

        static string Describe(string[] c)
        {
            string Perk(string p) => p.Length == 0 ? "" : Mw2Menus.Localize(Mw2Menus.TableLookup("mp/perkTable.csv", 1, p, 2));
            string Streak(string s) => s.Length == 0 ? "" : Mw2Menus.Localize(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, s, 2));
            return $"{Pretty(c[0])} / {Pretty(c[1])}\n{Perk(c[8])}, {Perk(c[9])}, {Perk(c[10])}\nKillstreaks: {Streak(c[5])}, {Streak(c[6])}, {Streak(c[7])}";
        }

        static readonly System.Collections.Generic.Dictionary<string, Sprite> sprites = new System.Collections.Generic.Dictionary<string, Sprite>();

        static Sprite Sprite(string material)
        {
            if (string.IsNullOrEmpty(material)) return null;
            if (sprites.TryGetValue(material, out var s)) return s;
            var tex = Mw2Icons.Get(material);
            return sprites[material] = tex != null ? UnityEngine.Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)) : null;
        }

        /// The primary's MW2 icon (mp/statsTable.csv image column, by base weapon name).
        static Sprite WeaponIcon(string weapon)
        {
            string b = weapon.Split('_')[0];
            return Sprite(Mw2Menus.TableLookup("mp/statsTable.csv", 4, b, 6)) ?? Sprite("hud_icon_" + b);
        }

        // ------------------------------------------------------------------ Rank

        /// Your MW2 rank (prestige icon included) and Unlock everything (max rank, prestige 10, every
        /// challenge: all weapons, attachments, camos, perks, killstreaks, titles and emblems).
        static object RankRow(LoadoutPanelController owner, BodyIndex bi)
        {
            var row = NewRow(owner, bi, "MW2_SOLDIER_LOADOUT_RANK", "Rank");
            int xp = Mw2Progress.CurrentXp;
            Mw2Progress.RankFor(xp, out var r, out var icon);
            int prestige = Mw2Progress.Prestige;
            string rankName = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/rankTable.csv", 0, r.id.ToString(), 5));
            Button(row, owner, Sprite(icon), "RANK", $"Level {r.display}" + (prestige > 0 ? $", Prestige {prestige}" : ""),
                $"{rankName}\n{xp:N0} XP", () => { }, 0);
            // A switch between two profiles: your own progress stays as it is.
            if (!Mw2Progress.Unlocked)
                Button(row, owner, Sprite("cardicon_prestige_10"), "UNLOCK", "Unlocked profile",
                    "Switch to a separate profile with everything unlocked: max rank, Prestige 10, every weapon, attachment, camo, perk, killstreak, title and emblem. Your own progress is kept; switch back any time.",
                    () => { if (Mw2Progress.SetProfile(true)) Refresh(); }, 1);
            else
                Button(row, owner, Sprite(icon), "OWN", "Your own profile",
                    "On the unlocked profile. Switch back to your own progress and unlock things the regular way (the unlocked profile is kept).",
                    () => { if (Mw2Progress.SetProfile(false)) Refresh(); }, 1);
            findChoice.SetValue(row, (Func<Loadout, int>)(_ => -1));
            finishSetup.Invoke(row, new object[] { false });
            HideHighlightIfNone(row, -1, 0);
            return row;
        }

        // ------------------------------------------------------------------ Killstreaks

        static object StreakRow(LoadoutPanelController owner, BodyIndex bi)
        {
            var row = NewRow(owner, bi, "MW2_SOLDIER_LOADOUT_STREAKS", "Killstreaks");
            var cls = Mw2Menus.ClassLoadout(Plugin.Instance.PlayClass - 1);
            // Before Create-a-Streak (level 10) everyone plays MW2's default three.
            bool cas = Mw2Progress.CreateAStreakUnlocked;
            if (!cas) cls = new[] { "", "", "", "", "", Mw2Progress.DefaultStreaks[0], Mw2Progress.DefaultStreaks[1], Mw2Progress.DefaultStreaks[2] };
            UnityAction openCas = () =>
            {
                if (Mw2Progress.CreateAStreakUnlocked) Mw2Menus.Open("menu_cas_popup");
                else Chat.AddMessage($"<color=#c8c8c8>Create-a-Streak unlocks at level {Mw2Progress.CreateAStreakLevel}.</color>");
            };
            int i = 0;
            if (cls != null && cls.Length > 7)
                for (int k = 5; k <= 7; k++)
                {
                    string s = cls[k];
                    if (s.Length == 0) continue;
                    string name = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, s, 2));
                    uint id = Native.StreakId(s);
                    string kills = id != 0 ? $"{Native.mw2_streak_kills(id)} kills. " : "";
                    Button(row, owner, Sprite(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, s, 14)), "STREAK" + k, name,
                        kills + Mw2Menus.Localize(Mw2Menus.TableLookup("mp/killstreakTable.csv", 1, s, 3)) +
                        (cas ? $"\nClick to change your killstreaks ({Plugin.Instance.CreateAStreakKey.Value})." : $"\nCreate-a-Streak unlocks at level {Mw2Progress.CreateAStreakLevel}."),
                        openCas, i++);
                }
            if (i == 0)
                Button(row, owner, Mw2BindIcons.Plus(), "CAS", "Create-a-Streak", $"Pick your killstreaks ({Plugin.Instance.CreateAStreakKey.Value}).", openCas, i);
            findChoice.SetValue(row, (Func<Loadout, int>)(_ => -1));
            finishSetup.Invoke(row, new object[] { false });
            HideHighlightIfNone(row, -1, 0);
            return row;
        }
    }
}
