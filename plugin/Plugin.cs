using System;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    [BepInPlugin(Guid, "MW2 x Risk of Rain 2", "0.4.2")]
    public unsafe class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.buhhrad.mw2ror2";

        internal static Plugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;
        bool nativeOk;
        internal bool weaponsOk;

        // Kept alive for the process lifetime: native code holds the function pointers.
        static TraceFn worldTrace;
        static TraceFn floorTrace;

        internal ConfigEntry<string> CommonMpPath;
        internal ConfigEntry<string> Loadout;
        internal ConfigEntry<int> Class;
        internal ConfigEntry<bool> UseCustomClass;
        internal ConfigEntry<KeyCode> CreateAClassKey, ChangeClassKey, CreateAStreakKey;
        internal ConfigEntry<bool> AutoStart, Prematch;
        internal ConfigEntry<float> DamageReference;
        internal ConfigEntry<float> EnemySpeedScale;
        internal ConfigEntry<float> DamageMultiplier, SniperDamageScale, EquipmentDamageScale, HipSpreadScale;
        internal ConfigEntry<float> ExplosiveDamageScale;
        internal ConfigEntry<float> EnemyPersonalSpace;
        internal ConfigEntry<float> RangeScale, WorldScale;
        internal ConfigEntry<float> ShotgunRangeScale;
        internal ConfigEntry<bool> UnlimitedSprint, ThirdPersonStart, ThirdPersonOnly, SkipIntro, DamageFlinch;
        internal ConfigEntry<float> FieldOfView;
        internal ConfigEntry<float> JumpHeightScale, AirControl, FlinchScale;
        internal ConfigEntry<bool> NoLandingSlowdown;
        internal ConfigEntry<float> MoveSpeedScale, MoveSpeedBoost, SprintTimeScale, DamageTakenScale, ShieldBlockAngle, RegenDelay, RegenPerSecond;
        internal ConfigEntry<string> AmmoMode;
        internal ConfigEntry<bool> RoR2Ammo;
        internal ConfigEntry<float> LauncherCooldown, LethalCooldown, TacticalCooldown;
        internal ConfigEntry<float> Volume;
        internal ConfigEntry<string> DumpDir;
        internal ConfigEntry<string> Killstreaks;
        internal ConfigEntry<float> StreakKillScale, StreakChestChance, StreakLapGrowth, StreakFlightHeight, KillXpScale;
        internal ConfigEntry<KeyCode> TacticalInsertionKey;
        internal ConfigEntry<KeyCode> StreakKey;
        internal ConfigEntry<KeyCode> LethalKey, TacticalKey, MeleeKey, AltWeaponKey, NextWeaponKey, PrimaryKey, SecondaryKey, CrouchKey, ProneKey;
        internal ConfigEntry<float> MeleeLungeRange;
        internal ConfigEntry<bool> Mw2Body, StatusLine;
        internal ConfigEntry<string> BodyFaction;
        internal ConfigEntry<bool> UseMw2Binds;
        internal ConfigEntry<string> Lethal, Tactical;
        internal ConfigEntry<int> LethalCount, TacticalCount;
        internal ConfigEntry<string> Faction;
        internal ConfigEntry<float> RadarRange;
        internal ConfigEntry<float> VehicleScale;
        internal ConfigEntry<int> Prestige, UnlockedXp;
        /// The class you play: your pick, or MW2's first default class (Grenadier) while your custom
        /// classes are still locked (Create-a-Class at level 4).
        internal int PlayClass => (Class.Value <= 10 && Class.Value > Mw2Progress.CustomClassSlots) || !Mw2Progress.DefaultClassOpen(Class.Value) ? 11 : Class.Value;
        internal ConfigEntry<bool> UnlockedProfile;
        /// Player data (classes, challenges) of the normal profile; the unlocked one sits beside it.
        internal string PdataPath;
        internal string PdataFor(bool unlocked) => unlocked ? System.IO.Path.ChangeExtension(PdataPath, null) + "_unlocked.txt" : PdataPath;
        internal ConfigEntry<int> Xp;
        internal ConfigEntry<string> PlaytestDir;
        internal ConfigEntry<string> ViewHipOffset;
        internal ConfigEntry<string> ViewAdsOffset;

        readonly Mw2Bridge bridge = new Mw2Bridge();
        internal Mw2Bridge Bridge => bridge;

        /// BepInEx starts LogOutput.log over every launch: keep each session's in BepInEx\logs, so a
        /// play session's log survives the next launch - the last 10 played, the last 5 pilot runs
        /// apart (pilot runs had pushed every played session out, 10-04-26).
        void OnApplicationQuit()
        {
            Mw2Cinema.Unstage(); // a showcase run that didn't finish leaves the player's resolution / HUD as they were
            try
            {
                string src = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "logs");
                System.IO.Directory.CreateDirectory(dir);
                using (var from = new System.IO.FileStream(src, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var to = System.IO.File.Create(System.IO.Path.Combine(dir, $"LogOutput-{DateTime.Now:yyMMdd-HHmmss}{(Mw2Pilot.Ran ? "-pilot" : "")}.log")))
                    from.CopyTo(to);
                var all = new System.IO.DirectoryInfo(dir).GetFiles("LogOutput-*.log");
                foreach (bool pilot in new[] { false, true })
                {
                    var old = Array.FindAll(all, f => f.Name.EndsWith("-pilot.log") == pilot);
                    Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    for (int i = pilot ? 5 : 10; i < old.Length; i++) old[i].Delete();
                }
            }
            catch (Exception e) { Logger.LogWarning($"keeping the session log: {e.Message}"); }
        }

        void Awake()
        {
            Instance = this;
            Log = Logger;
            try
            {
                var asm = typeof(Plugin).Assembly;
                Log.LogInfo($"MW2 plugin loaded from {asm.Location} ({System.IO.File.GetLastWriteTime(asm.Location):MM-dd HH:mm:ss}), module {asm.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 6)}");
            }
            catch { }
            CommonMpPath = Config.Bind("MW2", "CommonMpPath",
                @"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff",
                "Your own MW2 (2009) common_mp.ff. Weapon stats are read from it at startup; nothing is copied.");
            Loadout = Config.Bind("MW2", "Loadout",
                "ak47_mp,cheytac_mp,spas12_mp,ump45_mp,m4_mp,aa12_mp,barrett_mp,deserteagle_mp,rpd_mp,model1887_mp",
                "Weapons F8 cycles through (MW2 internal names).");
            UseCustomClass = Config.Bind("MW2", "UseCustomClass", true, "Play the Create-a-Class loadout (Class) instead of the Loadout list and Equipment section.");
            Class = Config.Bind("MW2", "Class", 1, "Class you play: 1-10 your custom classes (MW2's Create-a-Class), 11-15 MW2's default classes (Grenadier .. Riot Control). Set by MW2's class menus.");
            CreateAClassKey = Config.Bind("MW2", "CreateAClassKey", KeyCode.F3, "Opens MW2's Create-a-Class (character select screen).");
            CreateAStreakKey = Config.Bind("MW2", "CreateAStreakKey", KeyCode.F5, "Opens MW2's killstreak picker (Create-a-Streak) on the character select screen.");
            ChangeClassKey = Config.Bind("MW2", "ChangeClassKey", KeyCode.F4, "Opens MW2's Choose Class menu mid-run. As in MW2: right after you spawn the class changes on the spot; once you've fought, it comes with your next spawn. One Man Army is the mid-fight change.");
            AutoStart = Config.Bind("MW2", "AutoStart", true, "MW2 mode takes your survivor at the start of every run (F6 still toggles).");
            Prematch = Config.Bind("MW2", "Prematch", true, "MW2's match start on a run's first stage: no drop pod, Choose Class menu, 5 s frozen countdown, intro vision, spawn music.");
            DamageReference = Config.Bind("Balance", "DamageReference", 40f,
                "MW2 damage that equals 100% RoR2 base damage per bullet (40 = one AK-47 round).");
            EnemyPersonalSpace = Config.Bind("Balance", "EnemyPersonalSpace", 1.6f, "Closest (metres, horizontal) an enemy's centre may get to yours in MW2 mode, at least its body radius + yours. RoR2 models stick out past their capsules and filled the first-person view.");
            ExplosiveDamageScale = Config.Bind("Balance", "ExplosiveDamageScale", 2.5f, "Extra on your launchers' and grenades' blasts and direct hits (MW2's numbers x this). MW2's rocket splash kills a 100-health player; RoR2 monsters are ~2.5x tougher against the gun scaling, so the MW2 numbers alone felt weak (launchers should do a lot of damage).");
            DamageMultiplier = Config.Bind("Balance", "DamageMultiplier", 1.25f, "Extra damage on every MW2 round, on top of DamageReference.");
            HipSpreadScale = Config.Bind("Balance", "HipSpreadScale", 0.6f, "Third person only (ThirdPersonOnly): MW2's spread scaled by this, bullets and crosshair together, hip and aimed - the crosshair is all you aim with there. First person keeps MW2's; shotgun pellet patterns are unchanged.");
            SniperDamageScale = Config.Bind("Balance", "SniperDamageScale", 7f, "Extra damage on sniper rifles' rounds (Intervention, Barrett, WA2000, M21), on top of DamageMultiplier: MW2's one-shot rifles against RoR2's health pools (one shot for small monsters, two or three for big ones).");
            if (Mathf.Approximately(SniperDamageScale.Value, 3.5f)) SniperDamageScale.Value = 7f; // the 10-04-26 default
            EquipmentDamageScale = Config.Bind("Balance", "EquipmentDamageScale", 1.8f, "Extra damage on frags, Semtex, C4 and claymores, on top of ExplosiveDamageScale.");
            WorldScale = Config.Bind("Character", "WorldScale", 1.5f, "Size of everything MW2 (soldier, sentry, aircraft, effects, movement) relative to MW2's own scale, to fit RoR2's bigger world. 1 = MW2 size next to the survivor's capsule.");
            RangeScale = Config.Bind("Balance", "RangeScale", 1f, "Multiplier on MW2's damage-falloff ranges (1 = MW2's own ranges). Was 2, where falloff almost never bit.");
            ShotgunRangeScale = Config.Bind("Balance", "ShotgunRangeScale", 1.5f, "Extra range multiplier for multi-pellet weapons (shotguns), on top of RangeScale.");
            MoveSpeedScale = Config.Bind("Balance", "MoveSpeedScale", 0f,
                "Multiplier on MW2 movement speed. 0 = automatic: MW2's own ground speed in metres (an MW2 unit is an inch).");
            // playtest 10-03-26: MW2's own speed felt slow on RoR2's maps, enemies hit too hard.
            MoveSpeedBoost = Config.Bind("Balance", "MoveSpeedBoost", 1.45f, "With MoveSpeedScale automatic: this much faster than MW2's own speed (RoR2's maps are big). Enemies don't speed up with it.");
            // playtest 10-04-26: still slow on RoR2's maps (1.2 -> 1.3), then "slightly faster" again
            // (1.3 -> 1.45); saved configs on an old default move up with it.
            if (Mathf.Approximately(MoveSpeedBoost.Value, 1.2f) || Mathf.Approximately(MoveSpeedBoost.Value, 1.3f)) MoveSpeedBoost.Value = 1.45f;
            FlinchScale = Config.Bind("General", "FlinchScale", 0.35f, "How hard the view jolts when you're hit, vs MW2's (1 = MW2: at least 5 degrees every hit). RoR2's own screen shake is its own slider in RoR2's settings.");
            AirControl = Config.Bind("Balance", "AirControl", 4f, "Extra steering in the air on top of MW2's (0 = MW2 only, whose air control is 1). RoR2-style: jumps out of a sprint keep their momentum and follow the stick.");
            NoLandingSlowdown = Config.Bind("Balance", "NoLandingSlowdown", true, "Skip MW2's landing drag (friction up to 2.5x after a jump, 2x after a hard landing).");
            JumpHeightScale = Config.Bind("Balance", "JumpHeightScale", 1.35f, "Jump height vs MW2's (39 units): RoR2's curbs and rocks a survivor jumps onto need a bit more.");
            UnlimitedSprint = Config.Bind("Balance", "UnlimitedSprint", true, "Sprint never runs out (MW2's Marathon), for RoR2's big maps.");
            FieldOfView = Config.Bind("View", "FieldOfView", 65f, new ConfigDescription(
                "MW2's cg_fov in first person: horizontal degrees on a 4:3 screen (MW2's default 65; 65-90). The gun and ADS zoom follow it, as in MW2. Also in RoR2's Settings > Gameplay (MW2 First-Person FOV).",
                new AcceptableValueRange<float>(65f, 90f)));
            DamageFlinch = Config.Bind("General", "DamageFlinch", true, "MW2's view kick when you're shot (approximate: IW4's bg_viewKick values as recalled, not read from the game).");
            SkipIntro = Config.Bind("General", "SkipIntro", true, "Skip RoR2's opening cutscene as soon as it can be skipped (what pressing through it does).");
            ThirdPersonOnly = Config.Bind("View", "ThirdPersonOnly", false, "Never first person: aiming down the sights zooms over the shoulder (scopes still show their overlay) and F7 does nothing.");
            ThirdPersonStart = Config.Bind("View", "ThirdPersonStart", true, "Spawn in third person (F7 switches; aiming down the sights is seen in first person either way).");
            SprintTimeScale = Config.Bind("Balance", "SprintTimeScale", 2f, "MW2 sprint length multiplier (1 = MW2's 4 s). Marathon still sprints forever.");
            ShieldBlockAngle = Config.Bind("Balance", "ShieldBlockAngle", 60f, "Riot shield: half-angle (degrees) of the arc it blocks, in front when held, behind when on your back.");
            DamageTakenScale = Config.Bind("Balance", "DamageTakenScale", 1f, "Damage the MW2 Soldier takes from enemies (1 = RoR2's: his health is Commando's).");
            RegenDelay = Config.Bind("Balance", "RegenDelay", 0f, "MW2 health regen: seconds without taking damage before you heal (0 = off - RoR2's regen and healing items as for any survivor; MW2's was 5).");
            // One-time move of saved configs to the main game's balance (playtest 10-06-26: "make the health
            // balanced to something like main game"): full damage taken, MW2's fast regen off.
            var balanceRev = Config.Bind("Internal", "BalanceRevision", 0, "Config migrations applied (don't edit).");
            if (balanceRev.Value < 2) { DamageTakenScale.Value = 1f; RegenDelay.Value = 0f; balanceRev.Value = 2; }
            if (balanceRev.Value < 3) { RangeScale.Value = 1f; balanceRev.Value = 3; }
            RegenPerSecond = Config.Bind("Balance", "RegenPerSecond", 0.25f, "MW2 health regen: fraction of full health healed per second once it starts.");
            EnemySpeedScale = Config.Bind("Balance", "EnemySpeedScale", 0f,
                "Enemy move-speed multiplier while MW2 movement is on. 0 = automatic: your MW2 walk speed / a RoR2 survivor's walk (about x0.7 at MW2's own speed).");
            Killstreaks = Config.Bind("Killstreaks", "Loadout", "uav,airdrop,predator_missile",
                "MW2 killstreak names from mp/killstreakTable.csv (MW2's default class: uav, airdrop, predator_missile). All 16 are built: uav, counter_uav, airdrop, sentry, predator_missile, precision_airstrike, harrier_airstrike, helicopter, airdrop_mega, helicopter_flares, stealth_airstrike, helicopter_minigun, ac130, emp, nuke.");
            StreakKey = Config.Bind("Killstreaks", "UseKey", KeyCode.Alpha5, "Key that uses your newest killstreak.");
            StreakKillScale = Config.Bind("Killstreaks", "KillScale", 4f,
                "Multiplier on every killstreak's kill count (RoR2 sends far more enemies than an MW2 lobby). 1 = MW2's own (UAV 3 ... Nuke 25).");
            StreakLapGrowth = Config.Bind("Killstreaks", "LapGrowth", 1.5f,
                "Past your top killstreak the loadout starts again from the bottom (RoR2's kill rate keeps climbing); each lap multiplies the kill gaps by this, counted from where the lap began. 1 = same gaps every lap.");
            StreakChestChance = Config.Bind("Killstreaks", "ChestChance", 0.05f,
                "Chance a chest drops an MW2 killstreak pickup instead of its item. Picking one up swaps it into your loadout for the run (the streak it replaces drops at your feet).");
            StreakFlightHeight = Config.Bind("Killstreaks", "FlightHeight", 2.5f,
                "Aircraft killstreaks fly this many times MW2's height over the ground (RoR2's stages are taller and rougher than MW2's maps). Jets, the Stealth Bomber and the C-130 take it whole (the Emergency Airdrop C-130 x1.5 more); helicopters and the Harrier a third of the raise. 1 = MW2's.");
            if (balanceRev.Value < 5) { StreakFlightHeight.Value = 2.5f; balanceRev.Value = 5; } // 2 -> 2.5 (playtest: "a little bit higher")
            // playtest 10-06-26: killstreak swaps came out of chests too often (was 0.15).
            if (balanceRev.Value < 4) { StreakChestChance.Value = 0.05f; balanceRev.Value = 4; }
            // MW2's default class: frag + flashbangs. G is MW2's PC lethal key; MW2's tactical is Q,
            // which is RoR2's equipment key, so F.
            UseMw2Binds = Config.Bind("Keys", "UseMw2Binds", true, "Use the key binds from your MW2 PC config (players/config_mp.cfg or iw4x_config.cfg) for killstreak / lethal / tactical instead of the keys below.");
            LethalKey = Config.Bind("Equipment", "LethalKey", KeyCode.G, "Hold to cook (frag), release to throw your lethal.");
            TacticalKey = Config.Bind("Equipment", "TacticalKey", KeyCode.F, "Throw your tactical.");
            MeleeKey = Config.Bind("Keys", "MeleeKey", KeyCode.V, "Knife (MW2 PC default V).");
            CrouchKey = Config.Bind("Keys", "CrouchKey", KeyCode.LeftControl, "Crouch on / off (MW2 togglecrouch). Jump or sprint stands you up.");
            ProneKey = Config.Bind("Keys", "ProneKey", KeyCode.Z, "Prone on / off (MW2 toggleprone).");
            TacticalInsertionKey = Config.Bind("Keys", "TacticalInsertionKey", KeyCode.X, "The MW2 Soldier's special, Tactical Insertion (RoR2's special key, R, stays MW2's reload). If one of the mod's other keys or your MW2 binds already uses it, the first free one of X, H, J, K, L is used instead.");
            AltWeaponKey = Config.Bind("Keys", "AltWeaponKey", KeyCode.Alpha3, "Underbarrel grenade launcher / shotgun on and off (MW2 +actionslot 3).");
            NextWeaponKey = Config.Bind("Keys", "NextWeaponKey", KeyCode.Alpha1, "Switch primary / secondary (MW2 weapnext). On the primary / secondary key it does nothing extra.");
            PrimaryKey = Config.Bind("Keys", "PrimaryKey", KeyCode.Alpha1, "Always the primary weapon.");
            SecondaryKey = Config.Bind("Keys", "SecondaryKey", KeyCode.Alpha2, "Always the secondary weapon.");
StatusLine = Config.Bind("Debug", "StatusLine", false, "Show the developer status line (MW2 mode / speed / weapon state) at the top left.");
            Mw2Body = Config.Bind("Character", "Mw2Body", true, "Show MW2's third-person soldier (MW2 anims, world gun) instead of the survivor's model.");
            BodyFaction = Config.Bind("Character", "Faction", "us_army", "MW2 faction body: us_army, socom_141, seals_udt, opforce_composite, militia (bodies come from your MW2 map zones).");
            MeleeLungeRange = Config.Bind("Balance", "MeleeLungeRange", 128f, "How far ahead (MW2 units) the knife lunges to an enemy; MW2 is 128 (176 with Commando).");
            Lethal = Config.Bind("Equipment", "Lethal", "frag_grenade_mp", "frag_grenade_mp, semtex_mp, throwingknife_mp, claymore_mp, c4_mp");
            Tactical = Config.Bind("Equipment", "Tactical", "flash_grenade_mp", "flash_grenade_mp, concussion_grenade_mp, smoke_grenade_mp");
            LethalCount = Config.Bind("Equipment", "LethalCount", 1, "Lethals per life (MW2: 1; Scavenger refills).");
            TacticalCount = Config.Bind("Equipment", "TacticalCount", 2, "Tacticals per life (MW2: 2 flash/stun, 1 smoke).");
            Faction = Config.Bind("Killstreaks", "Faction", "US", "Announcer voice: US, UK, NS, PG, RU or AB.");
            Xp = Config.Bind("Progress", "Xp", 0, "Your MW2 XP (ranks from mp/ranktable.csv). Kept across runs.");
            KillXpScale = Config.Bind("Progress", "KillXpScale", 0.1f, "XP for a kill as a share of MW2's 100 (RoR2 sends far more enemies than an MW2 match). Elites pay x2, bosses x10. Killstreak and challenge XP stay MW2's. 1 = MW2's.");
            // (empty for players: the pilot is a developer tool, off unless pointed at a folder)
            PlaytestDir = Config.Bind("Debug", "PlaytestDir", "", "Developers only. Autonomous playtest: put run.txt in this folder (contents: all, or step-name prefixes) and launch; results land in a timestamped folder. Empty = off.");
            UnlockedProfile = Config.Bind("Progress", "UnlockedProfile", false, "Playing on the separate profile with everything unlocked (the Loadout tab's Rank row switches). Your own progress is kept apart.");
            UnlockedXp = Config.Bind("Progress", "UnlockedXp", 0, "XP of the unlocked profile.");
            Prestige = Config.Bind("Progress", "Prestige", 0, "Your MW2 prestige (0-10): the rank icon (mp/rankIconTable.csv). Unlock everything (the Loadout tab's Rank row) sets 10.");
            VehicleScale = Config.Bind("Killstreaks", "VehicleScale", 2f, "Size of killstreak aircraft vs MW2 scale. RoR2's world is built much bigger than MW2's, so true-scale (1) jets look small.");
            RadarRange = Config.Bind("Killstreaks", "RadarRange", 80f, "Metres from the centre to the edge of the minimap.");
            DumpDir = Config.Bind("Debug", "DumpDir", "", @"Where F10 writes viewmodel captures. Empty = BepInEx\mw2-captures.");
            Volume = Config.Bind("MW2", "WeaponVolume", 0.35f, "Volume of MW2 weapon sounds (0-1), on top of the game's master and SFX sliders.");
            ViewHipOffset = Config.Bind("View", "HipOffset", "6,-7,-6",
                "First-person gun position at the hip, in MW2 units: forward, left, up (MW2's arms animation normally places it).");
            ViewAdsOffset = Config.Bind("View", "AdsOffset", "3,0,-3.4",
                "First-person gun position when fully aimed down sights, in MW2 units: forward, left, up.");
            RoR2Ammo = Config.Bind("Ammo", "RoR2Ammo", true, "RoR2-style ammo: guns never run out of reserve (magazines and reloads stay); launchers, grenades and tacticals come back one at a time on the cooldowns below. Off = AmmoMode.");
            LauncherCooldown = Config.Bind("Ammo", "LauncherCooldown", 8f, "Seconds for a launcher (AT4, RPG, Stinger, Javelin, Thumper, underbarrel grenade launcher) to get one round back, held or not.");
            LethalCooldown = Config.Bind("Ammo", "LethalCooldown", 10f, "Seconds for one lethal (frag, Semtex, throwing knife, C4, claymore) to come back.");
            TacticalCooldown = Config.Bind("Ammo", "TacticalCooldown", 12f, "Seconds for one tactical (flash, stun, smoke) to come back.");
            AmmoMode = Config.Bind("MW2", "AmmoMode", "Scavenger",
                "Scavenger = each kill refills one magazine of reserve (MW2's Scavenger perk). Infinite = reserve never runs out. Stock = MW2 reserve only.");

            nativeOk = LoadNative();
            if (!nativeOk) return;
            worldTrace = UnityWorld.Trace;
            floorTrace = FlatFloorTrace;
            SelfTest();

            // Not where the config says (MW2 in another Steam library, another language, a fresh
            // mod-manager config): look through Steam, and keep what's found.
            if (!System.IO.File.Exists(CommonMpPath.Value))
            {
                string found = Mw2Locate.Find(Mw2Locate.SteamRoots(), new[] { Mw2Locate.LibraryOf(BepInEx.Paths.GameRootPath) });
                if (found != null)
                {
                    Logger.LogInfo($"MW2 found through Steam: {found} (the configured {CommonMpPath.Value} doesn't exist)");
                    CommonMpPath.Value = found;
                }
                else Logger.LogWarning($"MW2 not found: {CommonMpPath.Value} doesn't exist and no Steam library has MW2 (2009). Set CommonMpPath in com.buhhrad.mw2ror2.cfg to your zone\\<language>\\common_mp.ff.");
            }
            float loadStart = Time.realtimeSinceStartup;
            int n = System.IO.File.Exists(CommonMpPath.Value) ? Native.LoadWeapons(CommonMpPath.Value) : -1;
            Logger.LogInfo($"MW2 data loaded in {Time.realtimeSinceStartup - loadStart:F1}s (at {Time.realtimeSinceStartup:F1}s)");
            weaponsOk = n > 0;
            if (weaponsOk) Logger.LogInfo($"Loaded {n} MW2 weapons from {CommonMpPath.Value}");
            if (weaponsOk) Mw2Fx.Init();
            if (weaponsOk) Mw2MenuHud.Init();
            Mw2Net.Init(bridge);
            if (UseMw2Binds.Value)
            {
                Mw2Binds.Load(CommonMpPath.Value);
                // In memory only: the .cfg keeps the user's fallback keys.
                bool save = Config.SaveOnConfigSet;
                Config.SaveOnConfigSet = false;
                if (Mw2Binds.For("+actionslot 4") is KeyCode ks) StreakKey.Value = ks;
                if (Mw2Binds.For("+frag") is KeyCode lk) LethalKey.Value = lk;
                if (Mw2Binds.For("+smoke") is KeyCode tk) TacticalKey.Value = tk;
                if (Mw2Binds.For("+melee", "+melee_breath") is KeyCode mk) MeleeKey.Value = mk;
                if (Mw2Binds.For("+actionslot 3") is KeyCode ak) AltWeaponKey.Value = ak;
                if (Mw2Binds.For("weapnext") is KeyCode nk) NextWeaponKey.Value = nk;
                Config.SaveOnConfigSet = save;
            }
            if (!weaponsOk) Logger.LogWarning($"No MW2 weapons loaded (path: {CommonMpPath.Value}). Movement only.");
            ResolveTacticalInsertionKey();

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(MotorPatch));
            harmony.PatchAll(typeof(JumpPadPatch));
            harmony.PatchAll(typeof(SkillPatch));
            harmony.PatchAll(typeof(EnemySpeedPatch));
            // Playtest runs work on a copy of the player's data (classes, challenges), like their XP.
            string pdata = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "mw2-ror2", "playerdata.txt");
            if (!string.IsNullOrWhiteSpace(PlaytestDir.Value) && System.IO.File.Exists(System.IO.Path.Combine(PlaytestDir.Value, "run.txt")))
            {
                string copy = System.IO.Path.Combine(PlaytestDir.Value, "pilot_playerdata.txt");
                try { if (System.IO.File.Exists(pdata)) System.IO.File.Copy(pdata, copy, true); else if (System.IO.File.Exists(copy)) System.IO.File.Delete(copy); } catch (Exception e) { Logger.LogWarning($"pilot playerdata copy: {e.Message}"); }
                pdata = copy;
            }
            PdataPath = pdata;
            if (weaponsOk) Mw2Menus.Init(PdataFor(UnlockedProfile.Value), harmony);
            if (weaponsOk) Mw2Menus.SetPlayerData("experience", Mw2Progress.XpEntry.Value.ToString());
            if (weaponsOk) Mw2Menus.SetFaction(BodyFaction.Value); // the skin's once a run starts (Mw2Skins.LocalFaction)
            if (weaponsOk) Mw2PerkEffects.Describe(); // before the perk items copy the texts
            if (weaponsOk) Mw2Perks.Init(harmony);
            if (weaponsOk) Mw2PerkEffects.Init(harmony);
            Mw2FovSetting.Init(harmony);
            Mw2ItemDisplays.Init(harmony);
            if (weaponsOk) Mw2Survivor.Init(harmony);
            if (weaponsOk) Mw2Shield.Init(harmony);
            // Every skin's models load in the background (character select's skin swaps, spawns).
            if (weaponsOk) Logger.LogInfo($"MW2 skin models: background load {(Native.mw2_character_prewarm() == 1 ? "started" : "not started")}");
            if (weaponsOk) Mw2TacticalInsertion.Init(harmony);
            if (weaponsOk) Mw2LoadoutRows.Init(harmony);
            Mw2DeathCam.Init(harmony);
            if (weaponsOk) Mw2VehicleHealth.Init(harmony);
            if (weaponsOk) Mw2StreakItems.Init(harmony);
            if (weaponsOk) Mw2Deathstreaks.Init(harmony);
            Mw2Menus.Response += OnMenuResponse;
            bridge.WantOn = AutoStart.Value;
            // MW2 runs start standing (the prematch countdown), not in a drop pod.
            var pod = AccessTools.PropertyGetter(typeof(Run), "spawnWithPod");
            if (pod != null) harmony.Patch(pod, postfix: new HarmonyMethod(typeof(Plugin), nameof(NoPod)));
            // Recoil goes on the camera after RoR2 has placed it.
            var camUpdate = AccessTools.Method(typeof(CameraRigController), "LateUpdate") ?? AccessTools.Method(typeof(CameraRigController), "Update");
            if (camUpdate != null) harmony.Patch(camUpdate, postfix: new HarmonyMethod(typeof(CameraKickPatch), nameof(CameraKickPatch.Postfix)));
            else Logger.LogWarning("CameraRigController update not found; MW2 recoil won't show on the camera.");
            GlobalEventManager.onCharacterDeathGlobal += report => { Mw2Perf.Kill(); long t0 = Mw2Perf.Begin(); bridge.OnKill(report); Mw2Perf.End("kill", t0); };
            GlobalEventManager.onClientDamageNotified += msg => { bridge.OnDamageNotified(msg); Mw2Net.OnDamage(msg); };
            GlobalEventManager.onServerDamageDealt += bridge.OnServerDamage;
            Mw2Pilot.TryStart(PlaytestDir.Value);
            Logger.LogInfo($"Ready. F6 MW2 mode, F7 first/third person, F8 cycle weapons, F9 debug; M1 fire, M2 ADS, R reload, {StreakKey.Value} killstreak. {Native.mw2_streak_table_count()} MW2 killstreaks loaded.");
        }

        bool LoadNative()
        {
            string dir = System.IO.Path.GetDirectoryName(Info.Location);
            string path = System.IO.Path.Combine(dir, "mw2sim.dll");
            // BepInEx doesn't search the plugin folder for native DLLs; load by full path
            // so DllImport("mw2sim") binds to this module.
            if (Native.LoadLibraryW(path) == IntPtr.Zero)
            {
                Logger.LogError($"Could not load {path} (Win32 error {Marshal.GetLastWin32Error()}).");
                return false;
            }
            uint abi = Native.mw2_abi_version();
            if (abi != Native.ExpectedAbi)
            {
                Logger.LogError($"mw2sim ABI {abi}, plugin expects {Native.ExpectedAbi}. Rebuild both.");
                return false;
            }
            int t = Marshal.SizeOf<Mw2Trace>(), i = Marshal.SizeOf<Mw2Input>(), s = Marshal.SizeOf<Mw2State>(), h = Marshal.SizeOf<Mw2Shot>();
            if (t != Native.TraceSize || i != Native.InputSize || s != Native.StateSize || h != Native.ShotSize || Marshal.SizeOf<Mw2ClipInfo>() != Native.ClipInfoSize || Marshal.SizeOf<Mw2StreakState>() != Native.StreakStateSize || Marshal.SizeOf<Mw2Rank>() != Native.RankSize || Marshal.SizeOf<Mw2WeaponView>() != Native.WeaponViewSize || Marshal.SizeOf<Mw2Glyph>() != Native.GlyphSize || Marshal.SizeOf<Mw2ViewSway>() != Native.ViewSwaySize)
            {
                Logger.LogError($"Struct size mismatch: trace {t}/{Native.TraceSize} input {i}/{Native.InputSize} state {s}/{Native.StateSize} shot {h}/{Native.ShotSize}.");
                return false;
            }
            return true;
        }

        // Drive the native sim through a C# trace callback inside the game process.
        void SelfTest()
        {
            var origin = new Vec3f(0, 0, 16);
            IntPtr sim = Native.mw2_create(ref origin);
            var idle = new Mw2Input { msec = 8, speedScale = 1f, fireRate = 1f };
            var walk = new Mw2Input { msec = 8, forwardmove = 127, speedScale = 1f, fireRate = 1f };
            Mw2State st = default;
            for (int n = 0; n < 125; n++) Native.mw2_step(sim, ref idle, floorTrace, IntPtr.Zero, out st);
            bool grounded = st.grounded != 0;
            for (int n = 0; n < 250; n++) Native.mw2_step(sim, ref walk, floorTrace, IntPtr.Zero, out st);
            float speed = Mathf.Sqrt(st.velocity.x * st.velocity.x + st.velocity.y * st.velocity.y);
            Native.mw2_destroy(sim);
            bool pass = grounded && Mathf.Abs(speed - 190f) < 0.01f;
            Logger.Log(pass ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Error,
                $"[gate2] native self-test {(pass ? "PASS" : "FAIL")}: grounded={grounded} walk={speed:F2} u/s (expect 190.00)");
        }

        static void FlatFloorTrace(IntPtr user, Vec3f* start, Vec3f* end, Vec3f* mins, Vec3f* maxs, uint mask, Mw2Trace* r)
        {
            float b0 = start->z + mins->z, b1 = end->z + mins->z;
            if (b0 < 0f)
            {
                *r = new Mw2Trace { fraction = 0f, normal = new Vec3f(0, 0, 1), endpos = *start, startsolid = 1, walkable = 1 };
            }
            else if (b1 < 0.125f && b1 < b0)
            {
                float f = Mathf.Clamp01((b0 - 0.125f) / (b0 - b1));
                var e = new Vec3f(start->x + (end->x - start->x) * f, start->y + (end->y - start->y) * f, start->z + (end->z - start->z) * f);
                *r = new Mw2Trace { fraction = f, normal = new Vec3f(0, 0, 1), endpos = e, walkable = 1 };
            }
        }

        bool hudRecovered;

        void Update()
        {
            Mw2Watchdog.Beat();
            Mw2Watchdog.Start(); // armed from the first frame on (the load before it may take long)
            // After RoR2 has exec'd its saved config (which would overwrite an earlier hud_enable).
            if (!hudRecovered && RoR2.Console.instance != null && Time.realtimeSinceStartup > 20f)
            { hudRecovered = true; Mw2Cinema.RecoverHud(); }
            if (!nativeOk) return;
            Mw2Perf.Frame();
            long perf0 = Mw2Perf.Begin();
            try { UpdateInner(); } finally { Mw2Perf.End("update", perf0); }
        }

        void UpdateInner()
        {
            if (weaponsOk) Mw2MenuMusic.Update();
            if (weaponsOk) Mw2LoadoutRows.Update();
            Mw2DeathCam.Update();
            if (weaponsOk) Mw2Survivor.Regen();
            // The MW2 Soldier's special from its own key (R is MW2's reload).
            var me = LocalBody();
            if (In.KeyDown(TacticalInsertionKeyInPlay) && Mw2Survivor.IsMw2(me) && me.skillLocator != null && me.skillLocator.special != null && !Mw2Menus.IsOpen)
            {
                Mw2Survivor.FiringSpecial = true;
                try { me.skillLocator.special.ExecuteIfReady(); } finally { Mw2Survivor.FiringSpecial = false; }
            }
            if (In.KeyDown(KeyCode.F6))
            {
                bridge.Manual = !bridge.Active && !Mw2Survivor.IsMw2(LocalBody());
                bridge.Toggle(LocalBody());
                Logger.LogInfo($"MW2 mode {(bridge.Active ? "ON" : "OFF")}");
            }
            if (In.KeyDown(PrimaryKey.Value)) bridge.SelectWeapon(0);
            else if (In.KeyDown(SecondaryKey.Value)) bridge.SelectWeapon(1);
            else if (In.KeyDown(KeyCode.F8) || (NextWeaponKey.Value != PrimaryKey.Value && NextWeaponKey.Value != SecondaryKey.Value && In.KeyDown(NextWeaponKey.Value))) bridge.CycleWeapon();
            if (In.KeyDown(AltWeaponKey.Value)) bridge.ToggleAlternate();
            if (In.KeyDown(KeyCode.F7)) bridge.ToggleFirstPerson();
            if (In.KeyDown(KeyCode.F9)) Logger.LogInfo("[debug] " + bridge.GunDebug());
            if (In.KeyDown(KeyCode.F10)) bridge.DumpViewmodel(string.IsNullOrWhiteSpace(DumpDir.Value) ? System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "mw2-captures") : DumpDir.Value);
            if (In.KeyDown(CreateAClassKey.Value) && !Mw2Menus.IsOpen && InCharacterSelect() && Mw2Survivor.IsSelected())
            {
                if (Mw2Progress.CreateAClassUnlocked) Mw2Menus.Open("cac_popup");
                else Chat.AddMessage($"<color=#c8c8c8>Create-a-Class unlocks at level {Mw2Progress.CreateAClassLevel}.</color>");
            }
            if (In.KeyDown(CreateAStreakKey.Value) && !Mw2Menus.IsOpen && InCharacterSelect() && Mw2Survivor.IsSelected())
            {
                if (Mw2Progress.CreateAStreakUnlocked) Mw2Menus.Open("menu_cas_popup");
                else Chat.AddMessage($"<color=#c8c8c8>Create-a-Streak unlocks at level {Mw2Progress.CreateAStreakLevel}.</color>");
            }
            if (In.KeyDown(ChangeClassKey.Value) && !Mw2Menus.IsOpen && bridge.Active) { Mw2Menus.SetFaction(Mw2Skins.LocalFaction()); Mw2Menus.Open("changeclass"); }
            long u0 = Mw2Perf.Begin();
            Mw2Menus.Update();
            Mw2Prematch.Update();
            Mw2Vision.Update();
            Mw2PerkEffects.Update();
            Mw2FovSetting.Update();
            Mw2IntroSkip.Update();
            Mw2Thermal.Update();
            Mw2Perf.End("u.menus", u0); u0 = Mw2Perf.Begin();
            Mw2Pilot.Update(bridge, LocalBody());
            Mw2Perf.End("u.pilot", u0); u0 = Mw2Perf.Begin();
            bridge.FrameUpdate(LocalBody());
            Mw2Perf.End("u.bridge", u0);
            bridge.SampleKeys();
            try { Mw2Net.Update(); } catch (Exception e) { Logger.LogWarning($"[net] {e.Message}"); }
        }

        void FixedUpdate()
        {
            if (!nativeOk) return;
            long perf0 = Mw2Perf.Begin();
            try { FixedInner(); } finally { Mw2Perf.End("fixed", perf0); }
        }

        void FixedInner()
        {
            bridge.Keep(LocalBody());
            bridge.FixedStep(LocalBody(), worldTrace, Time.fixedDeltaTime);
            // Monsters belong to the server: only the host may push them (clients would fight it) -
            // out of every MW2 player's space, the clients' too.
            if (UnityEngine.Networking.NetworkServer.active)
                foreach (var b in Mw2Survivor.Mw2Players()) Mw2Space.KeepEnemiesOut(b, EnemyPersonalSpace.Value);
        }

        void LateUpdate()
        {
            long perf0 = Mw2Perf.Begin();
            try { LateInner(); } finally { Mw2Perf.End("late", perf0); }
        }

        void LateInner()
        {
            if (nativeOk) bridge.LateFrame();
            if (nativeOk) Mw2Net.LateUpdate();
            if (nativeOk) Mw2Menus.LateUpdate();
        }

        float selectCheckAt;
        bool inSelect;

        /// RoR2's character select (lobby) is up: where MW2's Create-a-Class lives.
        bool InCharacterSelect()
        {
            if (Time.unscaledTime >= selectCheckAt)
            {
                selectCheckAt = Time.unscaledTime + 0.5f;
                inSelect = FindObjectOfType<RoR2.UI.CharacterSelectController>() != null;
            }
            return inSelect;
        }

        void OnGUI()
        {
            if (!nativeOk) return;
            long perf0 = Mw2Perf.Begin();
            try { OnGuiInner(); } finally { Mw2Perf.End("ongui", perf0); }
        }

        void OnGuiInner()
        {
            if (StatusLine.Value) GUI.Label(new Rect(12, 12, 900, 24), bridge.Hud());
            bridge.DrawHud();
            // (playtest 10-04-26: no Create-a-Class / killstreak hint over other survivors; the MW2 Soldier's
            // Loadout tab has the Class / Killstreaks rows.)
            Mw2Deathstreaks.Draw();
            Mw2Prematch.Draw();
            Mw2Menus.Draw();
            // Which zip this is, where everyone can compare before a lobby (the mismatch check uses it too).
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "title")
                Mw2Font.Label(new Rect(16, Screen.height - 40, 600, 28), "MW2xRoR2 " + Version, Mw2Hud.S(20f), new Color(1f, 1f, 1f, 0.7f), TextAnchor.MiddleLeft, Mw2Font.Small);
        }

        static string version;
        /// The packaged zip's stamp (tools/package.ps1 writes version.txt beside the mod), "dev" for a
        /// build deployed straight from the repo.
        public static string Version
        {
            get
            {
                if (version != null) return version;
                try
                {
                    var f = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "version.txt");
                    version = System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f).Trim() : "dev";
                }
                catch { version = "dev"; }
                if (version.Length == 0) version = "dev";
                return version;
            }
        }

        static void NoPod(ref bool __result)
        {
            if (Instance != null && Instance.Prematch.Value && Instance.bridge.WantOn) __result = false;
        }

        /// MW2's class menu answered: "class0".."class4" are MW2's default classes, "customN" yours.
        internal void OnMenuResponse(string r)
        {
            int n, pick;
            if (r.StartsWith("custom") && int.TryParse(r.Substring(6), out n) && n >= 1 && n <= 10)
            {
                if (n > Mw2Progress.CustomClassSlots) { if (bridge.OmaPending) bridge.StartOmaChange(); return; } // locked slot: class unchanged
                pick = n;
            }
            else if (r.StartsWith("class") && int.TryParse(r.Substring(5), out n) && n >= 0 && n <= 4) pick = 11 + n;
            else { if (r == "back") { Mw2Menus.CloseAll(); if (bridge.OmaPending) bridge.CancelOma(); } return; }
            if (bridge.OmaPending) { Class.Value = pick; bridge.StartOmaChange(); return; } // One Man Army: after MW2's delay
            // Mid-life (fought, or past the first seconds): the next spawn takes it, as in MW2.
            if (bridge.Active && !bridge.InClassGrace)
            {
                bridge.PendingClass = pick;
                Mw2Menus.CloseAll();
                bridge.Notice(ClassNextSpawnText());
                Logger.LogInfo($"MW2 class: {pick} picked mid-life, from the next spawn");
                return;
            }
            bridge.PendingClass = 0;
            Class.Value = pick;
            bridge.ApplyClass();
        }

        /// The Tactical Insertion key in play: the configured one, unless another key the mod reads already
        /// uses it. MW2's default crouch is C, the old default here: one press crouched and planted the flare.
        internal KeyCode TacticalInsertionKeyInPlay { get; private set; } = KeyCode.X;

        void ResolveTacticalInsertionKey()
        {
            var taken = new System.Collections.Generic.HashSet<KeyCode> { KeyCode.R, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F10, In.CrouchKey, In.ProneKey,
                PrimaryKey.Value, SecondaryKey.Value, NextWeaponKey.Value, AltWeaponKey.Value, StreakKey.Value, LethalKey.Value,
                TacticalKey.Value, MeleeKey.Value, CreateAClassKey.Value, ChangeClassKey.Value, CreateAStreakKey.Value };
            if (Mw2Binds.Sprint is KeyCode sprint) taken.Add(sprint);
            var want = TacticalInsertionKey.Value;
            TacticalInsertionKeyInPlay = want;
            if (!taken.Contains(want)) return;
            foreach (var k in new[] { KeyCode.X, KeyCode.H, KeyCode.J, KeyCode.K, KeyCode.L })
                if (!taken.Contains(k))
                {
                    TacticalInsertionKeyInPlay = k;
                    Logger.LogInfo($"Tactical Insertion key: {k} ({want} is already one of your keys)");
                    return;
                }
        }

        /// MW2's own line when available, else ours.
        static string ClassNextSpawnText()
        {
            const string key = "MP_CHANGE_CLASS_NEXT_SPAWN";
            var mw2 = Mw2Menus.Localize(key);
            return string.IsNullOrWhiteSpace(mw2) || mw2.Contains(key) ? "Your class will change when you respawn (next stage)." : mw2.TrimEnd('.', ' ') + " (next stage).";
        }

        internal static CharacterBody CurrentBody() => LocalBody();

        static CharacterBody LocalBody()
        {
            var user = LocalUserManager.GetFirstLocalUser();
            return user != null ? user.cachedBody : null;
        }
    }

    // While MW2 movement owns a motor, RoR2's own velocity logic is skipped and the
    // kinematic motor is steered onto the position the MW2 sim produced.
    [HarmonyPatch(typeof(CharacterMotor), "UpdateVelocity")]
    static class MotorPatch
    {
        static bool Prefix(CharacterMotor __instance, ref Vector3 __0, float __1)
        {
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            if (bridge == null || !bridge.Owns(__instance)) return true;
            __0 = bridge.MotorVelocity(__instance.Motor.TransientPosition, __1);
            __instance.velocity = __0;
            // RoR2's motor often doesn't count the MW2-driven body as grounded (it re-snaps only while
            // already grounded; the MW2 box and the capsule stand a few cm apart), and it only steps
            // up ledges when grounded: stuck on curbs and rocks MW2 walks up (playtest 10-04-26). The MW2
            // sim already decided he climbs them: step either way.
            __instance.Motor.AllowSteppingWithoutStableGrounding = true;
            return false;
        }
    }

    // RoR2 launch pads set the motor's velocity, which MW2 movement overwrites next tick: hand the
    // launch to the MW2 sim instead (RoR2's own pad logic still runs: sound, air-control lock).
    [HarmonyPatch(typeof(JumpVolume), "OnTriggerStay")]
    static class JumpPadPatch
    {
        static void Prefix(JumpVolume __instance, Collider other)
        {
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            var motor = other != null ? other.GetComponent<CharacterMotor>() : null;
            if (bridge == null || motor == null || motor.disableAirControlUntilCollision || !bridge.Owns(motor)) return;
            if (bridge.Launch(motor, __instance.jumpVelocity)) Plugin.Log.LogInfo($"MW2 launch pad: {__instance.jumpVelocity}");
        }
    }

    // MW2 owns M1 (fire) and M2 (ADS) while armed: the survivor's primary and secondary don't run.
    [HarmonyPatch(typeof(GenericSkill), "ExecuteIfReady")]
    static class SkillPatch
    {
        static bool Prefix(GenericSkill __instance, ref bool __result)
        {
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            if (bridge == null || !bridge.SuppressesSkill(__instance)) return true;
            __result = false;
            return false;
        }
    }

    static class CameraKickPatch
    {
        public static void Postfix(CameraRigController __instance)
        {
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            if (bridge == null || __instance == null || __instance.sceneCam == null) return;
            if (__instance.localUserViewer != null) bridge.RestCamera(__instance.sceneCam);
            if (Mw2DeathCam.Active && __instance.localUserViewer != null) { Mw2DeathCam.OnCamera(__instance.sceneCam); bridge.RenderFx(__instance.sceneCam); return; }
            if (__instance.targetBody == null || !bridge.IsLocalBody(__instance.targetBody))
            {
                // Spectating a teammate (or not the MW2 Soldier): everyone's MW2 effects still draw.
                if (__instance.localUserViewer != null) bridge.RenderFx(__instance.sceneCam);
                return;
            }
            bridge.OnCamera(__instance.sceneCam);
        }
    }

    // Keep MW2 movement authentic; slow the enemies instead so outrunning still works.
    [HarmonyPatch(typeof(CharacterBody), "RecalculateStats")]
    static class EnemySpeedPatch
    {
        static void Postfix(CharacterBody __instance)
        {
            var bridge = Plugin.Instance != null ? Plugin.Instance.Bridge : null;
            // Whenever an MW2 soldier is playing (the host's or a client's: monsters move on the host).
            if (bridge == null || (!bridge.Active && Mw2Survivor.Mw2Players().Count == 0)) return;
            if (__instance.teamComponent == null || __instance.teamComponent.teamIndex != TeamIndex.Monster) return;
            // moveSpeed has a private setter.
            var setter = MoveSpeedSetter ?? (MoveSpeedSetter = AccessTools.PropertySetter(typeof(CharacterBody), nameof(CharacterBody.moveSpeed)));
            setter?.Invoke(__instance, new object[] { __instance.moveSpeed * bridge.EnemySpeedScale() });
        }

        static System.Reflection.MethodInfo MoveSpeedSetter;
    }
}
