using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// Test tools for the autonomous playtests (Mw2Pilot): god mode, infinite ammo, spawns. Nothing in
    /// normal play opens or turns them on. Everything goes through RoR2's own systems, so kills from
    /// "Kill all enemies" count as yours (streaks, on-kill items).
    class Mw2Admin
    {
        public bool Open;
        public bool God, InfiniteAmmo;
        float timeScale = 1f;
        string weaponField = "ak47_mp";
        Vector2 scroll;
        Rect window = new Rect(60, 60, 430, 640);
        bool cursorHeld;

        public void Toggle()
        {
            Open = !Open;
            Cursor(Open);
        }

        /// RoR2 frees the mouse (and stops turning the camera) while its event system has a cursor opener.
        void Cursor(bool open)
        {
            if (open == cursorHeld) return;
            var user = LocalUserManager.GetFirstLocalUser();
            object es = user != null ? Traverse.Create(user).Property("eventSystem").GetValue() : null;
            if (es == null) return;
            var t = Traverse.Create(es);
            var f = t.Field("cursorOpenerCount");
            var p = t.Property("cursorOpenerCount");
            int delta = open ? 1 : -1;
            if (f.FieldExists()) f.SetValue(f.GetValue<int>() + delta);
            else if (p.PropertyExists()) p.SetValue(p.GetValue<int>() + delta);
            else { Plugin.Log.LogWarning("RoR2 cursor opener not found; the admin panel can't free the mouse."); return; }
            cursorHeld = open;
        }

        /// Per physics tick while MW2 mode is on.
        public void Apply(CharacterBody body)
        {
            if (body == null || body.healthComponent == null || !NetworkServer.active) return;
            body.healthComponent.godMode = God;
        }

        /// A ring of ordinary monsters 14 m out (RoR2's MasterSummon, monster team), for testing
        /// close-quarters fights and killstreak targets.
        public static int SpawnRing(CharacterBody body, string[] masters = null, float radius = 14f, bool passive = false)
        {
            if (body == null || !NetworkServer.active) return 0;
            masters = masters ?? new[] { "LemurianMaster", "LemurianMaster", "LemurianMaster", "LemurianMaster", "BeetleMaster", "BeetleMaster" };
            int n = 0;
            for (int i = 0; i < masters.Length; i++)
            {
                var prefab = MasterCatalog.FindMasterPrefab(masters[i]);
                if (prefab == null) { Plugin.Log.LogWarning($"no master '{masters[i]}'"); continue; }
                var dir = Quaternion.Euler(0f, i * 360f / masters.Length, 0f) * Vector3.forward;
                var at = body.footPosition + dir * radius + Vector3.up * 2f;
                if (Physics.Raycast(at + Vector3.up * 20f, Vector3.down, out var hit, 60f, LayerIndex.world.mask)) at = hit.point + Vector3.up * 0.5f;
                var summoned = new MasterSummon
                {
                    masterPrefab = prefab,
                    position = at,
                    rotation = Quaternion.LookRotation(-dir),
                    teamIndexOverride = TeamIndex.Monster,
                    ignoreTeamMemberLimit = true,
                }.Perform();
                if (summoned != null) n++;
                // Passive targets (reels): no AI, so they stand there instead of attacking.
                if (summoned != null && passive)
                    foreach (var ai in summoned.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
            }
            Plugin.Log.LogInfo($"[admin] spawned {n} enemies");
            return n;
        }

        /// One monster `metres` in front of the player, facing them (melee tests).
        public static bool SpawnAhead(CharacterBody body, string master, float metres)
        {
            if (body == null || !NetworkServer.active) return false;
            var prefab = MasterCatalog.FindMasterPrefab(master);
            if (prefab == null) return false;
            var fwd = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
            fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var at = body.footPosition + fwd * metres + Vector3.up * 2f;
            if (Physics.Raycast(body.footPosition + fwd * metres + Vector3.up * 1.5f, Vector3.down, out var hit, 6f, LayerIndex.world.mask)) at = hit.point + Vector3.up * 0.5f;
            var m = new MasterSummon { masterPrefab = prefab, position = at, rotation = Quaternion.LookRotation(-fwd), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
            Plugin.Log.LogInfo($"[admin] spawned {master} {metres} m ahead: {(m != null)}");
            return m != null;
        }

        /// Every monster on the map dies (reel recordings start clean).
        public static void KillMonsters()
        {
            if (!NetworkServer.active) return;
            foreach (var cb in new List<CharacterBody>(CharacterBody.readOnlyInstancesList))
                if (cb != null && cb.teamComponent != null && cb.teamComponent.teamIndex == TeamIndex.Monster && cb.healthComponent != null)
                    cb.healthComponent.Suicide();
        }

        public void Draw(Mw2Bridge bridge)
        {
            if (!Open) return;
            window = GUILayout.Window(0x4D5732, window, id => Body(bridge), "MW2 x RoR2 admin");
        }

        void Body(Mw2Bridge bridge)
        {
            var body = bridge.Body;
            var streaks = bridge.Streaks;
            var cfg = Plugin.Instance;
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("<b>Player</b>");
            God = GUILayout.Toggle(God, " God mode");
            InfiniteAmmo = GUILayout.Toggle(InfiniteAmmo, " Infinite reserve ammo");
            Slider("Move speed", cfg.MoveSpeedScale, 0f, 3f, $"x{bridge.MoveSpeedScale():F2}");
            Slider("Enemy speed", cfg.EnemySpeedScale, 0f, 2f, $"x{bridge.EnemySpeedScale():F2}");
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Time scale {timeScale:F2}", GUILayout.Width(150));
            float ts = GUILayout.HorizontalSlider(timeScale, 0.1f, 2f);
            if (GUILayout.Button("1x", GUILayout.Width(36))) ts = 1f;
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(ts, timeScale)) { timeScale = ts; Time.timeScale = ts; }

            GUILayout.Space(8);
            GUILayout.Label("<b>Killstreaks</b>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+1 kill")) streaks.AdminKills(1);
            if (GUILayout.Button("+5 kills")) streaks.AdminKills(5);
            if (GUILayout.Button("Reset streak")) streaks.AdminResetStreak();
            GUILayout.EndHorizontal();
            int col = 0;
            GUILayout.BeginHorizontal();
            foreach (uint id in streaks.AllStreaks())
            {
                // airdrop_sentry_minigun is what a care package holds, not a streak of its own
                // (MW2's Sentry Gun streak is "sentry"): listing both showed two Sentry Guns.
                if (Native.StreakString(id, 0) == "airdrop_sentry_minigun") continue;
                string name = Mw2Killstreaks.Pretty(Native.StreakString(id, 0));
                bool built = streaks.IsBuilt(id);
                GUI.enabled = built;
                if (GUILayout.Button(built ? name : name + " (soon)", GUILayout.Width(190))) streaks.AdminGive(id);
                GUI.enabled = true;
                if (++col % 2 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("<b>World</b>");
            if (GUILayout.Button("Kill all enemies (counts as your kills)")) KillAll(body);
            if (GUILayout.Button("Spawn enemies around me (4 Lemurians + 2 Beetles)")) SpawnRing(body);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+$1000") && body != null && body.master != null && NetworkServer.active) body.master.GiveMoney(1000);
            if (GUILayout.Button("White item")) GiveItem(body, 1);
            if (GUILayout.Button("Green item")) GiveItem(body, 2);
            if (GUILayout.Button("Red item")) GiveItem(body, 3);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("<b>Weapons</b>");
            col = 0;
            GUILayout.BeginHorizontal();
            foreach (var w in bridge.Loadout)
            {
                if (GUILayout.Button(Mw2Hud.Pretty(w), GUILayout.Width(126))) bridge.GiveWeapon(w);
                if (++col % 3 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            weaponField = GUILayout.TextField(weaponField, GUILayout.Width(250));
            if (GUILayout.Button("Give MW2 weapon")) bridge.GiveWeapon(weaponField.Trim());
            GUILayout.EndHorizontal();
            GUILayout.Label("Any name from MW2, e.g. m4_mp, cheytac_mp, spas12_mp, rpg_mp.");

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        static void Slider(string label, BepInEx.Configuration.ConfigEntry<float> entry, float min, float max, string now)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label} {(entry.Value > 0f ? $"x{entry.Value:F2}" : $"auto ({now})")}", GUILayout.Width(190));
            float v = GUILayout.HorizontalSlider(entry.Value, min, max);
            if (GUILayout.Button("auto", GUILayout.Width(44))) v = 0f;
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(v, entry.Value)) entry.Value = v < 0.05f ? 0f : v;
        }

        static void KillAll(CharacterBody body)
        {
            if (body == null || !NetworkServer.active) return;
            var team = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.Player;
            int n = 0;
            foreach (var cb in CharacterBody.readOnlyInstancesList.ToArray())
            {
                if (cb == null || cb == body || cb.teamComponent == null || cb.teamComponent.teamIndex == team) continue;
                if (cb.healthComponent == null || !cb.healthComponent.alive) continue;
                cb.healthComponent.Suicide(body.gameObject, body.gameObject);
                n++;
            }
            Plugin.Log.LogInfo($"[admin] killed {n} enemies");
        }

        static void GiveItem(CharacterBody body, int tier)
        {
            if (body == null || body.inventory == null || Run.instance == null || !NetworkServer.active) return;
            var list = tier == 1 ? Run.instance.availableTier1DropList : tier == 2 ? Run.instance.availableTier2DropList : Run.instance.availableTier3DropList;
            if (list == null || list.Count == 0) return;
            var pickup = list[UnityEngine.Random.Range(0, list.Count)];
            var def = PickupCatalog.GetPickupDef(pickup);
            if (def == null || def.itemIndex == ItemIndex.None) return;
            body.inventory.GiveItem(def.itemIndex, 1);
            Plugin.Log.LogInfo($"[admin] gave {def.internalName}");
        }
    }
}
