using System;
using System.Collections;
using System.Collections.Generic;
using EntityStates;
using HarmonyLib;
using RoR2;
using RoR2.ContentManagement;
using RoR2.Skills;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// The MW2 soldier as his own survivor on character select (playtest 10-03-26), instead of Commando
    /// switching into MW2 mode. His body is a networked clone of Commando's (RoR2's movement,
    /// collision and items) with his own name, skills and display; MW2 mode takes it on spawn and
    /// only it (other survivors stay themselves; F6 still toggles MW2 mode by hand). Skills, on the
    /// RoR2 binds MW2 doesn't already use:
    ///   Primary / Secondary: MW2's fire and aim down sights (shown; the MW2 sim does them).
    ///   Utility: One Man Army (specialty_onemanarmy) - choose a class mid-fight, MW2's 6 s change.
    ///   Special: Tactical Insertion (specialty_tacticalinsertion) - plant MW2's flare, use it again to
    ///   get back to it. Its own key (R stays MW2's reload).
    static class Mw2Survivor
    {
        public const string BodyName = "MW2SoldierBody";
        public static GameObject Body, Display;
        public static SurvivorDef Survivor;
        public static SkillDef Fire, Ads, OneManArmy, TacticalInsertion;
        static readonly List<SkillFamily> families = new List<SkillFamily>();
        static readonly Dictionary<string, string> strings = new Dictionary<string, string>();
        static GameObject holder;

        /// The special is pressed from our key, not RoR2's (R reloads).
        public static bool FiringSpecial;

        public static bool IsMw2(CharacterBody b) => b != null && b.baseNameToken == "MW2_SOLDIER_BODY_NAME";

        /// The local player has him picked on character select.
        public static bool IsSelected()
        {
            var nu = LocalUserManager.GetFirstLocalUser()?.currentNetworkUser;
            var bi = BodyCatalog.FindBodyIndex(BodyName);
            return nu != null && bi != BodyIndex.None && nu.bodyIndexPreference == bi;
        }

        public static void Init(Harmony harmony)
        {
            ContentManager.collectContentPackProviders += add => add(new Provider());
            // Damage the MW2 Soldier takes (playtest 10-03-26: enemies hit too hard).
            var take = AccessTools.Method(typeof(HealthComponent), "TakeDamage");
            if (take != null) harmony.Patch(take, prefix: new HarmonyMethod(typeof(Mw2Survivor), nameof(ScaleDamageTaken)));
            // Commando's model (his body is a clone of Commando's) never shows, even for the frames
            // before MW2 mode builds the soldier (playtest: a flash of Commando at spawn).
            var start = AccessTools.Method(typeof(CharacterBody), "Start");
            if (start != null) harmony.Patch(start, postfix: new HarmonyMethod(typeof(Mw2Survivor), nameof(HideCommando)));
            var getString = AccessTools.Method(typeof(Language), "GetLocalizedStringByToken", new[] { typeof(string) });
            if (getString != null) harmony.Patch(getString, postfix: new HarmonyMethod(typeof(Mw2Survivor), nameof(LocalizedString)));
        }

        public static void SetString(string token, string value) => strings[token] = value;

        static void ScaleDamageTaken(HealthComponent __instance, DamageInfo damageInfo)
        {
            if (damageInfo == null || __instance == null || !IsMw2(__instance.body)) return;
            if (Mw2Pilot.Active && damageInfo.attacker == null)
                Plugin.Log.LogInfo($"[pilot] world damage {damageInfo.damage:F1} type {damageInfo.damageType.damageType} (motor vy {(__instance.body.characterMotor != null ? __instance.body.characterMotor.velocity.y : 0f):F1})");
            // Not his own (Tactical Insertion's test death, fall damage stays RoR2's) or the void.
            if (damageInfo.attacker == null || damageInfo.attacker == __instance.gameObject) return;
            damageInfo.damage *= Mathf.Max(Plugin.Instance.DamageTakenScale.Value, 0f);
        }

        static readonly List<CharacterBody> players = new List<CharacterBody>();
        static int playersFrame = -1;

        /// Living player bodies that are MW2 Soldiers (the host's and the clients'), once per frame.
        public static List<CharacterBody> Mw2Players()
        {
            if (playersFrame == Time.frameCount) return players;
            playersFrame = Time.frameCount;
            players.Clear();
            foreach (var pc in PlayerCharacterMasterController.instances)
            {
                var b = pc != null && pc.master != null ? pc.master.GetBody() : null;
                if (b != null && IsMw2(b) && b.healthComponent != null && b.healthComponent.alive) players.Add(b);
            }
            return players;
        }

        /// Host, every frame: MW2's health regen for MW2 Soldiers - after RegenDelay seconds without a
        /// hit, back to full fast (MW2's "breathing better"). RoR2's own regen keeps working.
        public static void Regen()
        {
            if (!NetworkServer.active) return;
            float delay = Plugin.Instance.RegenDelay.Value, rate = Plugin.Instance.RegenPerSecond.Value;
            if (delay <= 0f || rate <= 0f) return;
            foreach (var b in CharacterBody.readOnlyInstancesList)
            {
                if (b == null || !IsMw2(b)) continue;
                var hc = b.healthComponent;
                if (hc == null || !hc.alive || hc.health >= hc.fullHealth || hc.timeSinceLastHit < delay) continue;
                if (Mw2Deathstreaks.InStand(b)) continue; // MW2: no regen while down in last stand
                hc.Heal(hc.fullHealth * rate * Time.deltaTime, default(ProcChainMask), false);
            }
        }

        static void HideCommando(CharacterBody __instance)
        {
            if (!IsMw2(__instance)) return;
            var cm = __instance.modelLocator != null && __instance.modelLocator.modelTransform != null ? __instance.modelLocator.modelTransform.GetComponent<CharacterModel>() : null;
            if (cm != null) cm.invisibilityCount++;
        }

        static void LocalizedString(string token, ref string __result)
        {
            if (token != null && token.StartsWith("MW2_SOLDIER_") && strings.TryGetValue(token, out var s)) __result = s;
        }

        /// A copy of a networked prefab under a new name and network asset id (kept inactive, so
        /// nothing on it wakes up until RoR2 instantiates it).
        static GameObject Clone(GameObject original, string name)
        {
            if (holder == null)
            {
                holder = new GameObject("MW2 prefabs");
                holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holder);
            }
            var clone = UnityEngine.Object.Instantiate(original, holder.transform);
            clone.name = name;
            var ni = clone.GetComponent<NetworkIdentity>();
            if (ni != null)
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mw2-ror2/" + name));
                    var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    AccessTools.Field(typeof(NetworkIdentity), "m_AssetId")?.SetValue(ni, NetworkHash128.Parse(hex));
                }
            }
            return clone;
        }

        static Sprite Icon(string material)
        {
            var tex = string.IsNullOrEmpty(material) ? null : Mw2Icons.Get(material);
            return tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)) : null;
        }

        static SkillDef Skill(string key, string name, string desc, Type state, float cooldown, string icon, SkillDef d = null)
        {
            d = d ?? ScriptableObject.CreateInstance<SkillDef>();
            ((ScriptableObject)d).name = "MW2" + key;
            d.skillName = "MW2" + key;
            d.skillNameToken = "MW2_SOLDIER_" + key + "_NAME";
            d.skillDescriptionToken = "MW2_SOLDIER_" + key + "_DESC";
            strings[d.skillNameToken] = name;
            strings[d.skillDescriptionToken] = desc;
            d.icon = Icon(icon);
            d.activationState = new SerializableEntityStateType(state);
            d.activationStateMachineName = "Weapon";
            d.baseRechargeInterval = cooldown;
            d.baseMaxStock = 1;
            d.rechargeStock = cooldown > 0f ? 1 : 0; // no cooldown: once until the next body (stage)
            d.requiredStock = 1;
            d.stockToConsume = 1;
            d.interruptPriority = InterruptPriority.Any;
            d.isCombatSkill = false;
            d.mustKeyPress = true;
            d.cancelSprintingOnActivation = false;
            d.canceledFromSprinting = false;
            d.fullRestockOnAssign = true;
            d.beginSkillCooldownOnSkillEnd = false;
            return d;
        }

        static void SetFamily(GenericSkill slot, SkillDef def)
        {
            if (slot == null || def == null) return;
            var fam = ScriptableObject.CreateInstance<SkillFamily>();
            ((ScriptableObject)fam).name = "MW2SoldierFamily" + def.skillName;
            fam.variants = new[] { new SkillFamily.Variant { skillDef = def, viewableNode = new ViewablesCatalog.Node(def.skillNameToken, false, null) } };
            AccessTools.Field(typeof(GenericSkill), "_skillFamily")?.SetValue(slot, fam);
            families.Add(fam);
        }

        /// His portrait (playtest 10-03-26: MW2's, not the Rangers'): MW2's own desktop icon from the
        /// player's iw4mp.exe (<mw2>/zone/english/common_mp.ff -> <mw2>/iw4mp.exe).
        static Texture2D Portrait()
        {
            var zone = System.IO.Path.GetDirectoryName(Plugin.Instance.CommonMpPath.Value ?? "");
            var root = zone != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(zone)) : null;
            var tex = root != null ? Mw2ExeIcon.Load(System.IO.Path.Combine(root, "iw4mp.exe"), feather: true) : null;
            if (tex != null) tex.name = "mw2_soldier_portrait";
            return tex;
        }

        static bool Build()
        {
            var commando = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterBodies/CommandoBody");
            if (commando == null) { Plugin.Log.LogWarning("MW2 survivor: CommandoBody not found"); return false; }
            Body = Clone(commando, BodyName);
            var display = LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterDisplays/CommandoDisplay");
            Display = display != null ? Clone(display, "MW2SoldierDisplay") : null;
            if (Display != null) Display.AddComponent<Mw2SoldierDisplay>();

            var cb = Body.GetComponent<CharacterBody>();
            cb.baseNameToken = "MW2_SOLDIER_BODY_NAME";
            cb.subtitleNameToken = "MW2_SOLDIER_BODY_SUBTITLE";
            cb.bodyColor = new Color(0.55f, 0.6f, 0.45f);
            var portrait = Portrait() ?? Mw2Icons.Get(Mw2Skins.Table(Plugin.Instance.BodyFaction.Value, 5));
            if (portrait != null) cb.portraitIcon = portrait;
            strings["MW2_SOLDIER_BODY_NAME"] = "MW2 Soldier";
            strings["MW2_SOLDIER_BODY_SUBTITLE"] = "Modern Warfare 2";

            string Perk(string perk, int col) => Mw2Menus.Localize(Mw2Menus.TableLookup("mp/perkTable.csv", 1, perk, col));
            string PerkIcon(string perk) => Mw2Menus.TableLookup("mp/perkTable.csv", 1, perk, 3);
            // Fire / ADS show the input they're on (Mw2BindIcons), mouse or gamepad.
            var fireDef = ScriptableObject.CreateInstance<Mw2BindSkillDef>();
            var adsDef = ScriptableObject.CreateInstance<Mw2BindSkillDef>();
            adsDef.right = true;
            Fire = Skill("FIRE", "Fire", "Your class's MW2 weapon. Reload with R; switch weapons with 1.", typeof(Mw2NoopState), 0f, null, fireDef);
            Ads = Skill("ADS", "Aim Down Sights", "Aim down the sights of your MW2 weapon.", typeof(Mw2NoopState), 0f, null, adsDef);
            Fire.icon = Mw2BindIcons.Get(false, false);
            Ads.icon = Mw2BindIcons.Get(true, false);
            OneManArmy = Skill("OMA", Perk("specialty_onemanarmy", 2), "Choose a different class mid-fight. MW2's One Man Army: the change takes 6 s (3 s with One Man Army Pro).", typeof(OneManArmyState), 30f, PerkIcon("specialty_onemanarmy"));
            TacticalInsertion = Skill("TI", Perk("specialty_tacticalinsertion", 2), $"Plant MW2's Tactical Insertion flare where you stand. Die while it's down and you come back on it. Once per stage. Your special key, or {Plugin.Instance.TacticalInsertionKeyInPlay}.", typeof(TacticalInsertionState), 0f, PerkIcon("specialty_tacticalinsertion"));

            var loc = Body.GetComponent<SkillLocator>();
            SetFamily(loc.primary, Fire);
            SetFamily(loc.secondary, Ads);
            SetFamily(loc.utility, OneManArmy);
            SetFamily(loc.special, TacticalInsertion);

            Survivor = ScriptableObject.CreateInstance<SurvivorDef>();
            ((ScriptableObject)Survivor).name = "MW2Soldier";
            Survivor.cachedName = "MW2Soldier";
            Survivor.bodyPrefab = Body;
            Survivor.displayPrefab = Display;
            Survivor.primaryColor = cb.bodyColor;
            Survivor.displayNameToken = "MW2_SOLDIER_BODY_NAME";
            Survivor.descriptionToken = "MW2_SOLDIER_DESCRIPTION";
            Survivor.outroFlavorToken = "MW2_SOLDIER_OUTRO";
            Survivor.mainEndingEscapeFailureFlavorToken = "MW2_SOLDIER_FAIL";
            Survivor.desiredSortPosition = 100f;
            strings["MW2_SOLDIER_DESCRIPTION"] = "A soldier straight out of Modern Warfare 2: your Create-a-Class loadouts, perks, killstreaks and deathstreaks. F3 Create-a-Class, F5 Create-a-Streak.";
            strings["MW2_SOLDIER_OUTRO"] = "..and so he left, mission accomplished.";
            strings["MW2_SOLDIER_FAIL"] = "..and so he vanished, MIA.";
            // Skins: every MW2 MP body (Mw2Skins).
            Mw2Skins.LoadCatalog();
            Mw2Skins.Build(Body, Display);
            Plugin.Log.LogInfo($"MW2 survivor: MW2 Soldier built, {Mw2Skins.Defs.Count} skins");
            return true;
        }

        class Provider : IContentPackProvider
        {
            readonly ContentPack pack = new ContentPack();
            public string identifier => Plugin.Guid + ".survivor";

            public IEnumerator LoadStaticContentAsync(LoadStaticContentAsyncArgs args)
            {
                try
                {
                    if (Build())
                    {
                        pack.bodyPrefabs.Add(new[] { Body });
                        pack.survivorDefs.Add(new[] { Survivor });
                        pack.skillDefs.Add(new[] { Fire, Ads, OneManArmy, TacticalInsertion });
                        pack.skillFamilies.Add(families.ToArray());
                        pack.entityStateTypes.Add(new[] { typeof(Mw2NoopState), typeof(OneManArmyState), typeof(TacticalInsertionState) });
                    }
                }
                catch (Exception e) { Plugin.Log.LogError($"MW2 survivor: {e}"); }
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
                if (Body != null) ClientScene.RegisterPrefab(Body);
                args.ReportProgress(1f);
                yield break;
            }
        }
    }

    /// Fire / ADS: shown on the skill bar, done by the MW2 sim.
    public class Mw2NoopState : BaseState
    {
        public override void OnEnter()
        {
            base.OnEnter();
            outer.SetNextStateToMain();
        }
    }

    public class OneManArmyState : BaseState
    {
        public override void OnEnter()
        {
            base.OnEnter();
            if (isAuthority) Plugin.Instance.Bridge.OneManArmy();
            outer.SetNextStateToMain();
        }
    }

    public class TacticalInsertionState : BaseState
    {
        public override void OnEnter()
        {
            base.OnEnter();
            if (isAuthority) Mw2TacticalInsertion.Use(characterBody, skillLocator != null ? skillLocator.special : null);
            outer.SetNextStateToMain();
        }
    }

    /// Character select: the MW2 soldier in place of Commando's model, in the picked skin, with a
    /// head rolled from the faction's pool (as each spawn rolls one) and the class's primary. A
    /// teammate's soldier wears their head and holds their class's gun (Mw2Net lobby message).
    class Mw2SoldierDisplay : MonoBehaviour
    {
        // The last few skin / head pairs stay built (hidden): swapping back and forth is instant.
        const int Kept = 4;
        readonly Dictionary<(int, int), Mw2Character> built = new Dictionary<(int, int), Mw2Character>();
        readonly List<(int, int)> recent = new List<(int, int)>();
        Mw2Character ch = new Mw2Character();
        Renderer[] commando;
        ModelSkinController skins;
        CharacterSelectSurvivorPreviewDisplayController preview;
        (int skin, int head) shown = (-1, -1);
        int rolledSkin = -1, rolledHead;

        NetworkUser Owner => preview != null ? preview.networkUser : null;
        bool Local => Owner == null || (Owner.isLocalPlayer && !Mw2Net.LobbyEcho);

        void Start()
        {
            commando = GetComponentsInChildren<Renderer>(true);
            skins = GetComponentInChildren<ModelSkinController>(true);
            preview = GetComponent<CharacterSelectSurvivorPreviewDisplayController>() ?? GetComponentInParent<CharacterSelectSurvivorPreviewDisplayController>();
            // Commando's model stays hidden behind ours (RoR2's async skin apply turns renderers back on).
            var cm = GetComponentInChildren<CharacterModel>(true);
            if (cm != null) cm.invisibilityCount++;
        }

        void Update()
        {
            int skin = skins != null ? Mathf.Max(skins.currentSkinIndex, 0) : 0;
            int head;
            if (Local)
            {
                // A new head each time a skin is picked (MW2 rolls one each spawn).
                if (skin != rolledSkin) { rolledSkin = skin; rolledHead = UnityEngine.Random.Range(0, 8); }
                head = rolledHead;
                if (Owner != null && Owner.isLocalPlayer) Mw2Net.LocalLobbyHead = head; // only the player's own pad
            }
            else head = Mw2Net.LobbyOf(Owner, out _, out int theirs) && theirs >= 0 ? theirs : 0;
            if ((skin, head) != shown)
            {
                shown = (skin, head);
                ch.Hide();
                bool ok;
                var key = (skin, head);
                if (built.TryGetValue(key, out var keep) && keep.Exists) { ch = keep; ok = true; }
                else
                {
                    ch = new Mw2Character();
                    var owner = Owner;
                    System.Func<uint> weapon = Local ? (System.Func<uint>)null : () => Mw2Net.LobbyOf(owner, out uint w, out _) ? w : 0u;
                    ok = ch.BuildDisplay(transform, Mw2Skins.Key(skin, head), weapon);
                    if (ok) built[key] = ch;
                }
                recent.Remove(key);
                recent.Add(key);
                while (recent.Count > Kept)
                {
                    var old = recent[0];
                    recent.RemoveAt(0);
                    if (built.TryGetValue(old, out var gone)) { gone.Destroy(); built.Remove(old); }
                }
                // RoR2 re-skins Commando's model on a skin change: keep it hidden behind ours.
                foreach (var r in commando) if (r != null) r.enabled = !ok;
            }
            ch.StepDisplay(transform, Time.deltaTime);
        }

        void OnDestroy()
        {
            // Also runs while the game quits, when the native side and Unity objects may be gone already.
            try
            {
                foreach (var c in built.Values) c?.Destroy();
                built.Clear();
                ch?.Destroy();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 soldier cleanup: {e.Message}"); }
        }
    }

    /// MW2's Tactical Insertion (_perkfunctions.gsc): mil_emergency_flare_mp with flare_ambient_green
    /// marks where you spawn next. Here (playtest 10-03-26): one per stage; die with it down and you come
    /// back on it once (the host revives you, as Dio's Best Friend does), no cooldown to farm.
    static class Mw2TacticalInsertion
    {
        static GameObject flare;
        static Vector3 at;
        static uint fx;
        static float nextFx;
        static Stage plantedStage, usedStage;

        static bool thrown;
        static float thrownAt;
        static CharacterBody thrower;

        public static void Use(CharacterBody body, GenericSkill skill)
        {
            if (body == null || flare != null || usedStage == Stage.instance) return;
            // MW2: the flare is ignited and tossed down (flare_mp's animations); it plants where it lands.
            if (Plugin.Instance.Bridge.LocalBody2 == body && Plugin.Instance.Bridge.ThrowTacticalInsertion())
            {
                thrown = true;
                thrownAt = Time.time;
                thrower = body;
                usedStage = Stage.instance;
                return;
            }
            // Dropped at your feet: on the ground below you (RoR2's isGrounded isn't kept under MW2
            // movement). Nothing under you within 6 m (mid-jump off a ledge): not planted, no cost.
            if (!Physics.Raycast(body.footPosition + Vector3.up * 0.5f, Vector3.down, out var hit, 6.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                skill?.AddOneStock();
                return;
            }
            Plant(body, hit.point);
        }

        /// The thrown flare came to rest: plant it there (on the ground below, if it stopped on a wall).
        public static void Landed(CharacterBody body, Vector3 pos)
        {
            if (!thrown || flare != null) return;
            thrown = false;
            Plugin.Log.LogInfo($"MW2 tactical insertion: flare down at {pos}");
            if (Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out var hit, 6.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) pos = hit.point;
            Plant(body, pos);
        }

        static void Plant(CharacterBody body, Vector3 point)
        {
            at = point;
            plantedStage = usedStage = Stage.instance;
            flare = Mw2Prop.Build("mil_emergency_flare_mp", body, Space.Scale);
            flare?.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, body.transform.eulerAngles.y, 0f));
            Mw2Audio.PlayUi("weap_c4_plant"); // the plant (approximate: MW2 uses the equipment's raise)
            Mw2Net.TacticalInsertion(body, at);
        }

        public static void Update()
        {
            if (flare != null && plantedStage != Stage.instance) Clear();
            // MW2 drops the flare at your feet after its ignite / toss animation (flare_mp makes no
            // projectile here): plant it there once the animation's had its time.
            if (thrown && Time.time - thrownAt > 1.5f)
            {
                if (thrower != null && thrower.healthComponent != null && thrower.healthComponent.alive) Landed(thrower, thrower.footPosition);
                else thrown = false;
            }
            // Used this stage: a respawned body (full stock again) doesn't get another.
            var me = LocalUserManager.GetFirstLocalUser()?.cachedBody; // dead bodies too (MW2 mode lets go of them)
            var sp = me != null && Mw2Survivor.IsMw2(me) ? me.skillLocator?.special : null;
            if (sp != null && sp.stock > 0 && usedStage != null && usedStage == Stage.instance) sp.RemoveAllStocks();
            // The flare is spent once you've come back on it (or you're up again some other way).
            if (flare != null && me != null && me.healthComponent != null && !me.healthComponent.alive) dying = true;
            else if (flare != null && dying && me != null && me.healthComponent != null && me.healthComponent.alive) { dying = false; Clear(); }
            if (flare == null) return;
            if (Time.time >= nextFx) { nextFx = Time.time + 0.5f; if (fx == 0 || !Mw2Fx.Move(fx, at + Vector3.up * 0.1f, Vector3.up)) fx = Mw2Fx.Play("misc/flare_ambient_green", at + Vector3.up * 0.1f, Vector3.up); }
        }
        static bool dying;

        public static void Clear()
        {
            if (flare != null) UnityEngine.Object.Destroy(flare);
            flare = null;
            dying = false;
            if (fx != 0) Mw2Fx.Stop(fx);
            fx = 0;
        }

        // ---------------------------------------------------------------- host

        static readonly Dictionary<CharacterMaster, (Vector3 at, Stage stage)> planted = new Dictionary<CharacterMaster, (Vector3, Stage)>();
        static readonly Dictionary<CharacterMaster, Stage> spent = new Dictionary<CharacterMaster, Stage>();

        /// Host: `body`'s player planted at `at` (once per stage per player).
        public static void ServerPlant(CharacterBody body, Vector3 at)
        {
            var m = body != null ? body.master : null;
            if (m == null || Stage.instance == null || (spent.TryGetValue(m, out var s) && s == Stage.instance)) return;
            spent[m] = Stage.instance;
            planted[m] = (at, Stage.instance);
            Plugin.Log.LogInfo($"MW2 tactical insertion planted at {at} for {m.name}");
        }

        public static void Init(Harmony harmony)
        {
            var revive = AccessTools.Method(typeof(CharacterMaster), "TryReviveOnBodyDeath");
            if (revive != null) harmony.Patch(revive, postfix: new HarmonyMethod(typeof(Mw2TacticalInsertion), nameof(Revive)));
            else Plugin.Log.LogWarning("MW2 tactical insertion: CharacterMaster.TryReviveOnBodyDeath not found");
        }

        /// Host, after RoR2's own revives (Dio's) said no: back up on the flare 2 s later.
        static void Revive(CharacterMaster __instance, CharacterBody body, ref bool __result)
        {
            if (__result || !NetworkServer.active || __instance == null) return;
            if (!planted.TryGetValue(__instance, out var p) || p.stage != Stage.instance) return;
            planted.Remove(__instance);
            __result = true;
            __instance.preventGameOver = true;
            __instance.StartCoroutine(RespawnAt(__instance, p.at));
            Plugin.Log.LogInfo($"MW2 tactical insertion: {__instance.name} comes back on the flare");
        }

        static IEnumerator RespawnAt(CharacterMaster m, Vector3 at)
        {
            yield return new WaitForSeconds(2f);
            if (m != null)
            {
                // As Dio's Best Friend does (CharacterMaster.RespawnExtraLife): a safe spot near the
                // flare, straight into the main state (RoR2's spawn rise left him half in the ground),
                // a few seconds untouchable.
                var prefab = m.bodyPrefab != null ? m.bodyPrefab.GetComponent<CharacterBody>() : null;
                var safe = prefab != null ? TeleportHelper.FindSafeTeleportDestination(at, prefab, RoR2Application.rng) : null;
                var body = m.Respawn(safe ?? at + Vector3.up * 0.3f, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f), true);
                if (body != null)
                {
                    foreach (var esm in body.GetComponents<EntityStateMachine>()) esm.initialStateType = esm.mainStateType;
                    body.AddTimedBuff(RoR2Content.Buffs.Immune, 3f);
                }
                m.preventGameOver = false;
            }
        }
    }
}

namespace MW2RoR2
{
    /// MW2's multiplayer menu music (music_mainmenu_mp) on character select while the MW2 Soldier is
    /// picked (playtest 10-03-26); RoR2's own music is muted meanwhile and comes back when he isn't.
    static class Mw2MenuMusic
    {
        static uint loop;
        static float retryAt;

        public static void Update()
        {
            var nu = RoR2.LocalUserManager.GetFirstLocalUser()?.currentNetworkUser;
            var bi = RoR2.BodyCatalog.FindBodyIndex(Mw2Survivor.BodyName);
            bool want = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "lobby"
                && nu != null && bi != RoR2.BodyIndex.None && nu.bodyIndexPreference == bi;
            if (want)
            {
                if (loop == 0 && UnityEngine.Time.unscaledTime >= retryAt)
                {
                    retryAt = UnityEngine.Time.unscaledTime + 1f; // the streamed music needs the archive index
                    loop = Mw2Audio.LoopStart("music_mainmenu_mp");
                    if (loop != 0) { Mw2Audio.Ror2Music(false); Plugin.Log.LogInfo("MW2 menu music on (character select, MW2 Soldier)"); }
                }
                Mw2Audio.MusicVolume(loop);
            }
            else if (loop != 0)
            {
                Mw2Audio.LoopStop(loop);
                loop = 0;
                Mw2Audio.Ror2Music(true);
            }
        }
    }
}
