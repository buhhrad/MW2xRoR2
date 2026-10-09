using System;
using System.Collections.Generic;
using System.Linq;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    /// MW2 killstreaks inside RoR2. The rules are MW2's own and run natively (kill counts and
    /// sounds from mp/killstreakTable.csv, earn/stack logic from _killstreaks.gsc, Care
    /// Package weights from _airdrop.gsc). What each streak does is built from RoR2 parts so
    /// items keep working: Predator damage is a RoR2 BlastAttack from the player's body.
    unsafe class Mw2Killstreaks
    {
        IntPtr ks;
        CharacterBody body;
        public Mw2Audio Audio;
        /// A streak was used (MW2 awards its killstreakTable XP).
        public Action<uint> OnUsed;
        public IntPtr Sim;

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uav"] = "UAV", ["counter_uav"] = "Counter-UAV", ["airdrop"] = "Care Package", ["sentry"] = "Sentry Gun",
            ["airdrop_sentry_minigun"] = "Sentry Gun", ["predator_missile"] = "Predator Missile",
            ["precision_airstrike"] = "Precision Airstrike", ["harrier_airstrike"] = "Harrier Strike",
            ["helicopter"] = "Attack Helicopter", ["airdrop_mega"] = "Emergency Airdrop", ["helicopter_flares"] = "Pave Low",
            ["stealth_airstrike"] = "Stealth Bomber", ["helicopter_minigun"] = "Chopper Gunner", ["ac130"] = "AC130",
            ["emp"] = "EMP", ["nuke"] = "Tactical Nuke",
        };
        static readonly HashSet<string> Implemented = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "uav", "airdrop", "predator_missile", "counter_uav", "sentry", "airdrop_sentry_minigun", "precision_airstrike",
            "harrier_airstrike", "helicopter", "airdrop_mega", "helicopter_flares", "stealth_airstrike", "helicopter_minigun",
            "ac130", "emp", "nuke",
        };

        static string KeyLabel
        {
            get
            {
                var k = Plugin.Instance.StreakKey.Value.ToString();
                return k.StartsWith("Alpha") && k.Length == 6 ? k.Substring(5) : k;
            }
        }

        public static string Pretty(string name) => Names.TryGetValue(name, out var n) ? n : name;

        string splash = "", splashSub = "";
        float splashUntil;

        public bool Ready => ks != IntPtr.Zero && Native.mw2_streak_table_count() > 0;

        /// Hardline (each streak one kill sooner) follows the perk.
        public void SetHardline(bool on)
        {
            if (ks != IntPtr.Zero) Native.mw2_streaks_set_hardline(ks, on ? 1 : 0);
        }

        void EnsureCreated()
        {
            if (ks != IntPtr.Zero || Native.mw2_streak_table_count() == 0) return;
            ks = Native.mw2_streaks_create();
            LoadLoadout();
        }

        /// The three streaks to earn (Create-a-Streak or the config list); re-read every life so
        /// picks made between runs apply.
        public void LoadLoadout()
        {
            if (ks == IntPtr.Zero) return;
            Native.mw2_streaks_set_kill_scale(Plugin.Instance.StreakKillScale.Value);
            Native.mw2_streaks_set_lap_growth(Plugin.Instance.StreakLapGrowth.Value);
            var ids = new List<uint>();
            // MW2's Create-a-Streak picks (playerdata killstreaks.0-2) when playing Create-a-Class
            // loadouts; the config list otherwise.
            var cls = Plugin.Instance.UseCustomClass.Value ? Mw2Menus.ClassLoadout(Plugin.Instance.PlayClass - 1) : null;
            var names = cls != null && cls.Length > 7 && cls[5].Length > 0
                ? new[] { cls[5], cls[6], cls[7] }
                : Plugin.Instance.Killstreaks.Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // MW2: your own picks only once Create-a-Streak opens (level 10); MW2's three before.
            if (!Mw2Progress.CreateAStreakUnlocked) names = Mw2Progress.DefaultStreaks;
            // Killstreak pickups this run replace the class's streaks until the run ends.
            if (runLoadout != null && runLoadoutRun != null && runLoadoutRun == Run.instance)
                names = runLoadout.Select(i => Native.StreakString(i, 0)).ToArray();
            foreach (var n in names)
            {
                uint id = Native.StreakId(n);
                if (id != 0) ids.Add(id); else Plugin.Log.LogWarning($"Unknown MW2 killstreak '{n}'");
            }
            var arr = ids.ToArray();
            uint set;
            fixed (uint* p = arr) set = Native.mw2_streak_set_loadout(ks, p, (uint)arr.Length);
            var desc = new List<string>();
            foreach (var id in arr) desc.Add($"{Pretty(Native.StreakString(id, 0))} ({Native.mw2_streak_kills(id)})");
            Plugin.Log.LogInfo($"MW2 killstreaks: {string.Join(", ", desc)}; use with {Plugin.Instance.StreakKey.Value}");
        }

        /// MW2 mode took a body. A new body (next stage, revive) is a new life: the kill count
        /// starts over, unused streaks are kept.
        public void Attach(CharacterBody b)
        {
            // A new run starts from nothing: no kills, no streaks held from the last game (playtest
            // 10-04-26: the streak carried over after game over).
            if (Run.instance != attachedRun)
            {
                attachedRun = Run.instance;
                if (ks != IntPtr.Zero) { Native.mw2_streaks_destroy(ks); ks = IntPtr.Zero; }
                body = null;
            }
            EnsureCreated();
            // The old body is usually destroyed by now (death, stage change), which Unity's == calls
            // null: compared by reference, or the new life never started.
            if (ks != IntPtr.Zero && !ReferenceEquals(body, null) && b != body) Native.mw2_streak_new_life(ks);
            body = b;
        }
        Run attachedRun;

        public void Detach()
        {
            HideMain(null);
            EndPredator(false);
            EndAll();
        }

        /// The player killed something (the bridge checks it was them).
        public void OnKill()
        {
            if (RidingJob is Ac130Job ac) ac.OnKill(); // the crew's kill confirmations
            if (ks == IntPtr.Zero || body == null) return;
            var earned = stackalloc uint[8];
            uint n = Native.mw2_streak_kill(ks, earned, 8);
            for (int i = 0; i < n && i < 8; i++)
            {
                Earned(earned[i], true);
                // ch_hardline_pro counts killstreaks earned with Hardline.
                if (Mw2Perks.Has(body, "specialty_hardline")) Mw2Challenges.Report(Mw2Challenges.Progress("ch_hardline_pro", 1), Plugin.Instance.Bridge.Progress, this);
            }
        }

        void Earned(uint id, bool dialog)
        {
            string name = Native.StreakString(id, 0);
            Splash($"{Pretty(name)}", $"Press {KeyLabel} for {Pretty(name)}");
            splashIcon = id;
            Play(Native.StreakString(id, 1));
            if (dialog) Play($"{Mw2Skins.Voice()}_1mc_{Native.StreakString(id, 2)}");
        }

        void Splash(string title, string sub) => Splash(title, sub, 2.5f); // splashtable.csv duration for killstreaks

        /// Every splash brings its own icon (splashTable column 3: a deathstreak's, none for a
        /// challenge); the last killstreak's stayed up under challenge splashes (playtest 10-04-26:
        /// Stopping Power Pro with an airstrike icon).
        public void Splash(string title, string sub, float seconds, string iconMaterial = null)
        {
            splashIcon = 0;
            splashMaterial = iconMaterial;
            splash = title;
            splashSub = sub;
            splashUntil = Time.unscaledTime + seconds;
        }

        void Play(string alias)
        {
            if (Audio != null && !string.IsNullOrEmpty(alias)) Audio.PlayAlias(alias);
        }

        public Mw2StreakState State()
        {
            Mw2StreakState s = default;
            if (ks != IntPtr.Zero) Native.mw2_streak_state(ks, out s);
            return s;
        }

        // ---------------------------------------------------------------- use

        /// Per frame, before the camera: input and streak updates.
        public void Update(CharacterBody current)
        {
            UpdateUavPlanes(); // they fly on whether or not he's alive
            if (ks == IntPtr.Zero || body == null || current != body) return;
            if (In.StreakKeyDown) TryUse();
            UpdateCallIn(Time.deltaTime);
            UpdateMarker();
            UpdatePredator(Time.deltaTime);
            UpdateJobs();
        }

        void TryUse()
        {
            foreach (var j in jobs)
                if (j is SentryJob carry && carry.BlocksWeapon) { carry.CancelRequested = true; return; }
            var s = State();
            if (s.stackLen == 0) return;
            uint id = s.stack[0];
            string name = Native.StreakString(id, 0);
            if (FreezesPlayer || callIn != null || body.healthComponent == null || !body.healthComponent.alive) return;
            if (name != "sentry" && name != "airdrop_sentry_minigun" && CallInGive != null && IsBuilt(id))
            {
                BeginCallIn(id, name);
                return;
            }
            Activate(id, name);
        }

        /// The streak itself (after its call-in animation, or at once for the sentry).
        bool Activate(uint id, string name)
        {
            bool started;
            switch (name)
            {
                case "uav": started = StartUav(); break;
                case "predator_missile": started = StartPredator(); break;
                default:
                    if (!IsBuilt(id)) { Splash(Pretty(name), "not built yet - it stays in your killstreaks"); return false; }
                    started = StartJob(name, id);
                    break;
            }
            if (!started) return false;
            Native.mw2_streak_take(ks);
            OnUsed?.Invoke(id);
            Play("weap_c4detpack_trigger_plr"); // usedKillstreak
            Play($"{Mw2Skins.Voice()}_1mc_use_{Native.StreakString(id, 3)}");
            return true;
        }

        // ---------------------------------------------------------------- call-in (MW2's killstreak weapons)

        /// Swap to the streak's own weapon (killstreakTable column 12) and play it the way MW2
        /// does: raise it (raise_time), then click the detonator / throw the smoke marker, or
        /// for laptop streaks just open the laptop; the streak goes off at that moment and the
        /// gun comes back with its ammo.
        public Action<string> CallInGive;
        public Action CallInRestore;
        class CallIn { public uint id; public string name; public float t, raise, fire, thrownAt = -1f; public bool laptop, marker, used, pressing, select, selecting; }
        /// The player's own fire button (the bridge sets it each tick): a smoke marker is thrown
        /// when they choose, as in MW2 (_airdrop.gsc waits on grenade_fire).
        public bool PlayerAttack;
        /// A thrown marker waiting to go off (_airdrop.gsc airDropMarkerActivate: the drop flies to
        /// where it explodes). Past the deadline (marker lost off the map) it drops at the aim point.
        uint markerId; string markerName; float markerDeadline;
        /// Where the location selector put the strike (consumed by StartJob).
        Vector3? pickedAt, pickedDir;
        CallIn callIn;
        public bool CallingIn => callIn != null;
        /// The sim presses fire for the detonator click / marker throw.
        public bool CallInFire => callIn != null && callIn.pressing;

        void BeginCallIn(uint id, string name)
        {
            string weapon = Native.StreakString(id, 4);
            uint w = Native.WeaponIndex(weapon);
            if (w == 0) { Activate(id, name); return; }
            bool select = name.EndsWith("_airstrike");
            bool laptop = select || name == "predator_missile" || name == "ac130" || name == "helicopter_minigun";
            if (laptop && !select && !RequireGround()) return; // MW2 refuses ride streaks in the air before the laptop comes out
            callIn = new CallIn { id = id, name = name, laptop = laptop, select = select, marker = weapon.EndsWith("_marker_mp"), raise = 1.2f, fire = 0.8f };
            if (Native.mw2_weapon_view(w, out _) == 1) { }
            CallInGive(weapon);
            Play("weap_c4detpack_trigger_plr");
        }

        void UpdateCallIn(float dt)
        {
            var c = callIn;
            if (c == null) return;
            c.t += dt;
            // Raise -> (laptop: use now) / (trigger, marker: press fire, use on the click/throw).
            float raiseEnd = c.marker ? 0.5f : c.raise;
            if (!c.used && c.t >= raiseEnd)
            {
                if (c.select)
                {
                    if (!c.selecting)
                    {
                        c.selecting = true;
                        Start(new LocationSelectJob(this, c.name != "harrier_airstrike",
                            (at, dir) => { pickedAt = at; pickedDir = dir; c.t = raiseEnd; Use(c); },
                            () => { CallInRestore?.Invoke(); callIn = null; }));
                    }
                }
                else if (c.laptop) Use(c);
                else if (c.marker)
                {
                    // Held until the player throws it: the bridge routes fire to the sim's offhand
                    // throw (hold pulls the pin, release throws, MW2's grenade physics) and reports
                    // the throw; the drop is called where it goes off.
                    c.pressing = false;
                    if (c.thrownAt >= 0f && c.t >= c.thrownAt + 0.6f)
                    {
                        c.used = true;
                        c.pressing = false;
                        markerId = c.id; markerName = c.name; markerDeadline = Time.time + 6f;
                    }
                }
                else
                {
                    // The click: held up to half a second so it lands once the trigger is ready (a tenth
                    // of a second at the raise's end could fall before - no clacker squeeze, 10-05-26).
                    c.pressing = c.t < raiseEnd + 0.5f;
                    if (c.t >= raiseEnd + 0.65f) Use(c);
                }
            }
            else c.pressing = false;
            float end = c.laptop ? raiseEnd + 1.0f : raiseEnd + c.fire;
            if (c.used && c.t >= end)
            {
                CallInRestore?.Invoke();
                callIn = null;
            }
        }

        /// The smoke marker is out and not yet thrown: its weapon (the bridge throws it), else null.
        public string MarkerInHand => callIn != null && callIn.marker && !callIn.used && callIn.thrownAt < 0f && callIn.t >= 0.5f
            ? Native.StreakString(callIn.id, 4) : null;

        /// The bridge saw the marker leave the hand.
        public void MarkerThrown()
        {
            if (callIn != null && callIn.marker && callIn.thrownAt < 0f) callIn.thrownAt = callIn.t;
        }

        /// The thrown marker went off at `at` (Mw2Projectiles): call the drop there.
        public void MarkerLanded(Vector3 at)
        {
            Plugin.Log.LogInfo($"MW2 marker went off at {at} (pending {(markerId != 0 ? Native.StreakString(markerId, 0) : "none")})");
            if (markerId == 0) return;
            uint id = markerId; string name = markerName;
            markerId = 0;
            pickedAt = at; pickedDir = null;
            if (!Activate(id, name)) Native.mw2_streak_give(ks, id);
        }

        void UpdateMarker()
        {
            if (markerId == 0 || Time.time < markerDeadline) return;
            uint id = markerId; string name = markerName;
            markerId = 0;
            if (!Activate(id, name)) Native.mw2_streak_give(ks, id);
        }

        void Use(CallIn c)
        {
            c.used = true;
            c.pressing = false;
            if (!Activate(c.id, c.name))
            {
                CallInRestore?.Invoke();
                callIn = null;
            }
        }

        // ---------------------------------------------------------------- admin

        public IEnumerable<uint> AllStreaks()
        {
            uint n = Native.mw2_streak_table_count();
            for (uint id = 1; id <= n + 1; id++)
                if (Native.mw2_streak_kills(id) > 0) yield return id;
        }

        public bool IsBuilt(uint id) => Implemented.Contains(Native.StreakString(id, 0));
        public static bool Built(string name) => Implemented.Contains(name);

        /// Pilot: a loadout and kill scale for a test (the next LoadLoadout puts the real ones back).
        public void SetLoadoutForTest(float scale, params string[] names)
        {
            if (ks == IntPtr.Zero) return;
            Native.mw2_streaks_set_kill_scale(scale);
            var arr = names.Select(n => Native.StreakId(n)).Where(i => i != 0).ToArray();
            fixed (uint* p = arr) Native.mw2_streak_set_loadout(ks, p, (uint)arr.Length);
        }

        /// Pilot / debug: each loadout streak with the kill count it comes at this lap.
        public string LapText()
        {
            var st = State();
            var parts = new List<string>();
            for (int i = 0; i < st.loadoutLen && i < 8; i++) parts.Add($"{Native.StreakString(st.loadout[i], 0)}@{Native.mw2_streak_next_kills(ks, st.loadout[i])}");
            return $"count {st.count}: {string.Join(" ", parts)}";
        }

        /// The loadout's streak names, ascending by kills.
        public List<string> LoadoutNames()
        {
            var st = State();
            var names = new List<string>();
            for (int i = 0; i < st.loadoutLen && i < 8; i++) names.Add(Native.StreakString(st.loadout[i], 0));
            return names;
        }

        /// This run's loadout after killstreak pickups (null: the class's own).
        static List<uint> runLoadout;
        static Run runLoadoutRun;

        /// A killstreak pickup reached this player (Mw2StreakItems): one already carried is given on
        /// the spot; otherwise it takes the place of the loadout streak with the nearest kill count
        /// for the rest of the run, and that one drops back out as a pickup.
        public bool PickUp(uint id)
        {
            if (ks == IntPtr.Zero || body == null) return false;
            string name = Native.StreakString(id, 0);
            var st = State();
            var cur = new List<uint>();
            for (int i = 0; i < st.loadoutLen && i < 8; i++) cur.Add(st.loadout[i]);
            if (cur.Contains(id) || cur.Count == 0)
            {
                Native.mw2_streak_give(ks, id);
                Earned(id, false);
                return true;
            }
            uint want = Native.mw2_streak_kills(id);
            uint replaced = cur.OrderBy(x => Math.Abs((int)Native.mw2_streak_kills(x) - (int)want)).ThenByDescending(x => Native.mw2_streak_kills(x)).First();
            var next = cur.Select(x => x == replaced ? id : x).ToList();
            runLoadout = next;
            runLoadoutRun = Run.instance;
            var arr = next.ToArray();
            fixed (uint* p = arr) Native.mw2_streak_set_loadout(ks, p, (uint)arr.Length);
            Splash(Pretty(name), $"Replaces {Pretty(Native.StreakString(replaced, 0))} ({Native.mw2_streak_kills(id)} kills)", 2.5f);
            splashIcon = id;
            Play(Native.StreakString(id, 1));
            Plugin.Log.LogInfo($"MW2 killstreak pickup: {name} replaces {Native.StreakString(replaced, 0)}");
            Mw2Net.DropStreak(body, replaced);
            return true;
        }

        /// Give a streak; `silent`: no splash or "killstreak acquired" (the showcase's nuke, playtest 10-06-26).
        public void AdminGive(uint id, bool silent = false)
        {
            if (ks == IntPtr.Zero || Native.mw2_streak_give(ks, id) != 1) return;
            if (!silent) Earned(id, true);
        }

        public void AdminKills(int n)
        {
            if (ks == IntPtr.Zero) return;
            var earned = stackalloc uint[8];
            for (int k = 0; k < n; k++)
            {
                uint got = Native.mw2_streak_kill(ks, earned, 8);
                for (int i = 0; i < got && i < 8; i++) Earned(earned[i], true);
            }
        }

        public void AdminResetStreak()
        {
            if (ks != IntPtr.Zero) Native.mw2_streak_new_life(ks);
        }

        /// While true the player stands still and can't act (MW2 ride killstreaks).
        public bool FreezesPlayer => predator.phase != PredPhase.None || RidingJob != null;

        /// The player can move but not shoot (carrying a sentry).
        public bool BlocksWeapon
        {
            get
            {
                foreach (var j in jobs) if (j.BlocksWeapon) return true;
                return false;
            }
        }

        // ---------------------------------------------------------------- UAV

        readonly List<float> uavEnds = new List<float>();
        const float UavSeconds = 30f; // level.radarViewTime (_uav.gsc)

        bool StartUav()
        {
            uavEnds.Add(Time.time + UavSeconds);
            LaunchUavPlane(UavSeconds);
            Mw2Net.SendTeam(Mw2Net.TeamUav, UavSeconds); // _uav.gsc: the radar is the team's
            return true;
        }

        /// A teammate's killstreak that works for us too (Mw2Net KTeam).
        public void TeamStreak(byte what, float seconds)
        {
            if (what == Mw2Net.TeamUav)
            {
                uavEnds.Add(Time.time + seconds); // his UAV's sweep on our radar (the plane comes as a prop)
                Plugin.Log.LogInfo($"MW2 UAV: a teammate's, {seconds:F0} s on our radar");
            }
            else if (what == Mw2Net.TeamNuke && body != null)
            {
                Start(new NukeJob(this, true)); // his countdown, flash, slow motion and aftermath; his nuke kills
                Plugin.Log.LogInfo("MW2 nuke: a teammate's");
            }
        }

        // The aircraft (_uav.gsc launchUAV): vehicle_uav_static_mp linked to a rig over the middle of
        // the map that turns -360 degrees every 60 s; the plane sits 6000-7000 units out from the rig
        // along a random bearing, raised by 3000-5000 against a 5000-7000 radius, facing along its
        // circle. 7 s before the end it unlinks and flies off ahead, 20000 units, with the AC-130 engine
        // effect. MW2 shows it only to the other team (so they can shoot it down); RoR2 has no other
        // team, so everyone sees it. The rig's centre here is the middle of the stage's ground nodes
        // and its height the highest of them (MW2 uses the minimap corners) - approximate.
        class UavPlane
        {
            public GameObject go;
            public Vector3 centre;   // Unity, the rig
            public Vec3f offset;     // MW2 units in the rig's frame
            public float angle, launched, duration;
            public bool leaving;
            public Vector3 from, fwd;
            public uint fx;
        }
        readonly List<UavPlane> uavPlanes = new List<UavPlane>();
        const string UavModel = "vehicle_uav_static_mp", UavEngineFx = "fire/jet_engine_ac130";

        /// A UAV aircraft in the sky, if one is up (the playtest pilot looks at it).
        public static Vector3? UavInSky;

        static float UavRigYaw => 115f - 360f / 60f * Time.time; // rotateyaw( -360, 60 ) from angles (0,115,0)

        void LaunchUavPlane(float duration)
        {
            if (body == null) return;
            var go = Mw2Prop.Build(UavModel, body, Space.Scale);
            if (go == null) { Plugin.Log.LogWarning("MW2 UAV: vehicle_uav_static_mp not built"); return; }
            float z = UnityEngine.Random.Range(3000, 5000), angle = UnityEngine.Random.Range(0, 360), r = UnityEngine.Random.Range(0, 2000) + 5000;
            var v = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad) * r, Mathf.Sin(angle * Mathf.Deg2Rad) * r, z).normalized * UnityEngine.Random.Range(6000, 7000);
            var p = new UavPlane { go = go, centre = MapCentre(), offset = new Vec3f(v.x, v.y, v.z), angle = angle, launched = Time.time, duration = duration };
            uavPlanes.Add(p);
            PlaceUav(p);
            Plugin.Log.LogInfo($"MW2 UAV: up {go.transform.position.y - body.footPosition.y:F0} m above him, {Vector3.Distance(go.transform.position, p.centre):F0} m from the stage centre");
        }

        Vector3 MapCentre()
        {
            var g = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
            int count = g != null ? g.GetNodeCount() : 0;
            Bounds b = default;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                if (!g.GetNodePosition(new RoR2.Navigation.NodeGraph.NodeIndex(i), out var at)) continue;
                if (!any) { b = new Bounds(at, Vector3.zero); any = true; } else b.Encapsulate(at);
            }
            return any ? new Vector3(b.center.x, b.max.y, b.center.z) : body.footPosition;
        }

        static void PlaceUav(UavPlane p)
        {
            float yaw = UavRigYaw * Mathf.Deg2Rad, c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            var o = new Vec3f(p.offset.x * c - p.offset.y * s, p.offset.x * s + p.offset.y * c, p.offset.z);
            float face = (UavRigYaw + p.angle - 90f) * Mathf.Deg2Rad;
            var fwd = Space.DirToUnity(new Vec3f(Mathf.Cos(face), Mathf.Sin(face), 0f));
            p.go.transform.SetPositionAndRotation(p.centre + Space.DirToUnity(o) * Space.Scale, Quaternion.LookRotation(fwd, Vector3.up));
        }

        void UpdateUavPlanes()
        {
            UavInSky = uavPlanes.Count > 0 && uavPlanes[0].go != null ? uavPlanes[0].go.transform.position : (Vector3?)null;
            for (int i = uavPlanes.Count - 1; i >= 0; i--)
            {
                var p = uavPlanes[i];
                float age = Time.time - p.launched;
                if (p.go == null || age >= p.duration) { RemoveUav(p); uavPlanes.RemoveAt(i); continue; }
                if (age < p.duration - 7f) { PlaceUav(p); continue; }
                if (!p.leaving)
                {
                    p.leaving = true;
                    p.from = p.go.transform.position;
                    p.fwd = p.go.transform.forward;
                    Mw2Prop.SetTrail(p.go, UavEngineFx);
                }
                // moveTo( dest, 60 ) for 3 s, then moveTo( dest, 4, 4, 0 ): the rest of the 20000
                // units in 4 s, accelerating the whole way.
                float t = age - (p.duration - 7f), first = 20000f * 3f / 60f;
                float d = t < 3f ? 20000f * t / 60f : first + (20000f - first) * Mathf.Pow(Mathf.Clamp01((t - 3f) / 4f), 2f);
                var at = p.from + p.fwd * d * Space.Scale;
                p.go.transform.position = at;
                if (p.fx == 0 || !Mw2Fx.Move(p.fx, at, p.fwd)) p.fx = Mw2Fx.Play(UavEngineFx, at, p.fwd);
            }
        }

        static void RemoveUav(UavPlane p)
        {
            if (p.fx != 0) Mw2Fx.Stop(p.fx);
            if (p.go != null) UnityEngine.Object.Destroy(p.go);
        }

        int UavsActive()
        {
            uavEnds.RemoveAll(t => t <= Time.time);
            return uavEnds.Count;
        }

        // ---------------------------------------------------------------- Predator Missile

        enum PredPhase { None, Laptop, Flying, Static }
        struct Predator
        {
            public PredPhase phase;
            public float timer;
            public Vector3 pos;
            public float yaw, pitch; // IW4 degrees: yaw + = left, pitch + = down
            public float speed;      // MW2 units/s
            public bool armed, boosted;
            public Vector3 lastAim;
            public float flown;
        }
        Predator predator;
        Camera predCam;

        // IW4L sim/remote_missile.rs (MW2 remote missile steering).
        const float PredPitchRate = 15f, PredYawRate = 20f, PredPitchMin = 1f, PredPitchMax = 87f;
        const float PredCruise = 3000f, PredBoost = 6000f, PredSpeedUp = 2000f, PredSpeedDown = 500f;
        // _remotemissile.gsc launch offsets (default maps) and remotemissile_projectile_mp.
        const float PredLaunchVert = 14000f, PredLaunchHorz = 7000f, PredTargetDist = 1500f;
        const float PredRadius = 450f, PredInner = 1000f, PredOuter = 10f, PredStartSpeed = 10f;
        const float PredLaptopSeconds = 1.0f; // initRideKillstreak: laptop out before the blackout

        bool StartPredator()
        {
            // MW2 refuses ride killstreaks in the air.
            if (!OnGround) { Splash("Predator Missile", "You must be on the ground"); return false; }
            predator = new Predator { phase = PredPhase.Laptop, timer = PredLaptopSeconds };
            return true;
        }

        void LaunchPredator()
        {
            var aim = V(Space.DirToIw(body.inputBank.aimDirection));
            float yaw = Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg;
            var fwd = new Vector3(Mathf.Cos(yaw * Mathf.Deg2Rad), Mathf.Sin(yaw * Mathf.Deg2Rad), 0f);
            var origin = V(Space.ToIw(body.footPosition));
            // MW2 launches from the sky. A start inside rock (an overhang above) climbs until it's clear;
            // only with nothing clear above does it fall back to under the roof (playtest 10-04-26: in a
            // tunnel it started right beside him - the tunnel roof read as the sky).
            float vert = PredLaunchVert, horz = PredLaunchHorz;
            var start = origin + new Vector3(0, 0, vert) - fwd * horz;
            bool clear = false;
            for (int i = 0; i < 20 && !(clear = !Physics.CheckSphere(Space.ToUnity(F(start)), 3f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)); i++)
                start.z += 10f / Space.Scale;
            if (!clear && Physics.Raycast(body.footPosition + Vector3.up * 2f, Vector3.up, out var roof, PredLaunchVert * Space.Scale, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                float k = Mathf.Max((roof.distance - 6f) / Space.Scale, 400f) / PredLaunchVert;
                start = origin + new Vector3(0, 0, vert * k) - fwd * (horz * k);
            }
            var target = origin + fwd * PredTargetDist;
            var dir = (target - start).normalized;
            predator.phase = PredPhase.Flying;
            // _remotemissile.gsc: black_bw, then the thermal vision for the missile camera.
            if (!predThermal) { predThermal = true; Mw2Thermal.Begin(); }
            predator.pos = Space.ToUnity(F(start));
            Plugin.Log.LogInfo($"MW2 predator: launched {predator.pos.y - body.footPosition.y:F0} m above him{(clear ? "" : " (under the roof: nothing clear above)")}");
            predator.yaw = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            predator.pitch = -Mathf.Asin(dir.z) * Mathf.Rad2Deg;
            predator.speed = PredStartSpeed;
            predator.armed = !In.Attack(body.inputBank);
            predator.lastAim = body.inputBank.aimDirection;
            predator.timer = 0f;
            predator.flown = 0f;
            // The missile itself (remotemissile_projectile_mp's model and trail): others see it fly
            // (Mw2Net mirrors it); its renderer is off here, the camera rides inside it.
            Mw2Projectiles.ProjectileOf(Native.WeaponIndex("remotemissile_projectile_mp"), out var pm, out var trail);
            if (!string.IsNullOrEmpty(pm))
            {
                predModel = Mw2Prop.Build(pm, body, Space.Scale);
                if (predModel != null)
                {
                    foreach (var r in predModel.GetComponentsInChildren<Renderer>()) r.enabled = false;
                    if (trail != null) Mw2Prop.SetTrail(predModel, trail);
                }
            }
        }

        GameObject predModel;

        void UpdatePredator(float dt)
        {
            switch (predator.phase)
            {
                case PredPhase.None: return;
                case PredPhase.Laptop:
                    predator.timer -= dt;
                    if (predator.timer <= 0f) LaunchPredator();
                    return;
                case PredPhase.Static:
                    predator.timer -= dt;
                    if (predator.timer <= 0f) EndPredator(false);
                    return;
            }
            var bank = body.inputBank;
            // Steering: the mouse still turns RoR2's aim; its per-frame turn is the stick,
            // capped at MW2's turn rates.
            var aimNow = bank.aimDirection;
            float dYaw = Vector3.SignedAngle(Flat(predator.lastAim), Flat(aimNow), Vector3.up); // + = right
            float dPitch = Mathf.Asin(Mathf.Clamp(-aimNow.y, -1f, 1f)) * Mathf.Rad2Deg - Mathf.Asin(Mathf.Clamp(-predator.lastAim.y, -1f, 1f)) * Mathf.Rad2Deg;
            predator.lastAim = aimNow;
            predator.yaw -= Mathf.Clamp(dYaw, -PredYawRate * dt, PredYawRate * dt);
            predator.pitch = Mathf.Clamp(predator.pitch + Mathf.Clamp(dPitch, -PredPitchRate * dt, PredPitchRate * dt), PredPitchMin, PredPitchMax);

            bool attack = In.Attack(bank);
            if (!attack) predator.armed = true;
            if (predator.armed && !predator.boosted && attack)
            {
                predator.speed = PredBoost;
                predator.boosted = true;
            }
            else if (predator.speed < PredCruise) predator.speed = Mathf.Min(predator.speed + PredSpeedUp * dt, PredCruise);
            else predator.speed = Mathf.Max(predator.speed - PredSpeedDown * dt, PredCruise);

            var dir = PredDir();
            float step = predator.speed * Space.Scale * dt;
            int mask = LayerIndex.world.mask | LayerIndex.entityPrecise.mask;
            if (Physics.Raycast(predator.pos, dir, out var hit, step + 0.05f, mask, QueryTriggerInteraction.Ignore))
            {
                Explode(hit.point);
                return;
            }
            predator.pos += dir * step;
            predator.flown += dt;
            predModel?.transform.SetPositionAndRotation(predator.pos, Quaternion.LookRotation(dir, Vector3.up));
            // Not MW2: a missile that never finds ground (off the map) gives control back.
            if (predator.flown > 20f) EndPredator(false);
        }

        static Vector3 V(Vec3f v) => new Vector3(v.x, v.y, v.z);
        static Vec3f F(Vector3 v) => new Vec3f(v.x, v.y, v.z);

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        Vector3 PredDir()
        {
            float y = predator.yaw * Mathf.Deg2Rad, p = predator.pitch * Mathf.Deg2Rad;
            var iw = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), -Mathf.Sin(p));
            return Space.DirToUnity(F(iw));
        }

        void Explode(Vector3 at)
        {
            float reference = Mathf.Max(Plugin.Instance.DamageReference.Value, 1f);
            if (body.hasEffectiveAuthority)
            {
                new BlastAttack
                {
                    attacker = body.gameObject,
                    inflictor = body.gameObject,
                    teamIndex = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.Player,
                    baseDamage = body.damage * PredInner / reference * Mw2Perks.KillstreakExplosiveDamage(body), // Danger Close Pro
                    baseForce = 2000f,
                    position = at,
                    radius = PredRadius * Space.Scale,
                    falloffModel = BlastAttack.FalloffModel.Linear,
                    procCoefficient = 1f,
                    crit = body.RollCrit(),
                    damageColorIndex = DamageColorIndex.Default,
                    damageType = DamageType.Generic,
                }.Fire();
            }
            // remotemissile_projectile_mp's own explosion (effect + sound), seen by everyone (Mw2Fx broadcasts).
            Mw2Projectiles.Explosion(Native.WeaponIndex("remotemissile_projectile_mp"), at);
            Play("exp_remote_missile");
            if (predModel != null) { UnityEngine.Object.Destroy(predModel); predModel = null; }
            // MW2 holds the missile view on static for a moment before giving control back.
            predator.phase = PredPhase.Static;
            predator.timer = 0.5f;
            predator.pos = at - PredDir() * 3f;
        }

        bool predThermal;

        void EndPredator(bool _)
        {
            HideMain(null);
            if (predThermal) { predThermal = false; Mw2Thermal.End(); }
            if (predModel != null) { UnityEngine.Object.Destroy(predModel); predModel = null; }
            predator = default;
            if (predCam != null && Mw2Fx.View == predCam) Mw2Fx.View = null;
            if (predCam != null) UnityEngine.Object.Destroy(predCam.gameObject);
            predCam = null;
        }

        /// After RoR2 (and MW2's view code) placed the main camera. The missile gets its own
        /// camera so RoR2's camera rig, which builds each frame from the last, isn't touched.
        public bool OnCamera(Camera main)
        {
            bool r = OnCameraInner(main);
            // While a killstreak camera has the screen, the main camera doesn't draw at all: its frame
            // (the player's view on the ground) showed through the Predator's post-processing as a
            // ghost (playtest 10-04-26), and drawing the world twice was wasted anyway.
            HideMain((RidingJob != null && rideCam.Live) || predCam != null ? main : null);
            return r;
        }

        readonly List<Camera> hiddenCams = new List<Camera>();

        /// The main camera and MW2's first-person viewmodel camera off while a killstreak camera has
        /// the screen (`main` null: back on). Every frame: RoR2 turns its scene camera back on itself.
        void HideMain(Camera main)
        {
            if (main == null)
            {
                foreach (var c in hiddenCams) if (c != null) c.enabled = true;
                hiddenCams.Clear();
                return;
            }
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.enabled || (c != main && c.name != "MW2 Viewmodel Camera")) continue;
                c.enabled = false;
                if (!hiddenCams.Contains(c)) hiddenCams.Add(c);
            }
            if (main.enabled) { main.enabled = false; if (!hiddenCams.Contains(main)) hiddenCams.Add(main); }
        }

        bool OnCameraInner(Camera main)
        {
            var ride = RidingJob;
            if (ride != null)
            {
                ride.OnCamera(main, rideCam);
                return true;
            }
            rideCam.Drop();
            if (predator.phase != PredPhase.Flying && predator.phase != PredPhase.Static) { if (predCam != null) EndPredator(false); return predator.phase == PredPhase.Laptop; }
            if (predCam == null)
            {
                var go = new GameObject("MW2 Predator Camera");
                go.SetActive(false);
                predCam = go.AddComponent<Camera>();
                Mw2Fx.View = predCam;
                predCam.CopyFrom(main);
                predCam.cullingMask = main.cullingMask & ~(1 << Mw2View.Layer);
                predCam.nearClipPlane = 0.1f;
                predCam.farClipPlane = Mathf.Max(main.farClipPlane, 2000f);
                predCam.useOcclusionCulling = false; // ground-level occlusion data culled monsters seen from the sky
                Mw2RideCam.PostProcessing(go, main); // the thermal vision (greyscale) shows here too
                go.SetActive(true);
                Mw2Vision.Reregister();
            }
            predCam.depth = main.depth + 5f;
            predCam.fieldOfView = 65f;
            predCam.transform.SetPositionAndRotation(predator.pos, Quaternion.LookRotation(PredDir(), Vector3.up));
            return true;
        }

        // ---------------------------------------------------------------- running streaks

        readonly List<StreakJob> jobs = new List<StreakJob>();
        readonly List<(StreakJob job, float at)> pending = new List<(StreakJob, float)>();
        readonly List<Mw2Crate> crates = new List<Mw2Crate>();
        readonly Mw2RideCam rideCam = new Mw2RideCam();

        public CharacterBody Body => body;

        /// Grounded as MW2's movement sees it. While MW2 mode drives the character RoR2's own
        /// motor never registers a landing (its velocity logic is replaced), so asking RoR2
        /// said "must be on the ground" until F6 was toggled off and on.
        public Func<bool> Grounded;
        public bool OnGround
        {
            get
            {
                bool mw2 = Grounded != null ? Grounded() : (body.characterMotor == null || body.characterMotor.isGrounded);
                if (mw2) { lastGrounded = Time.time; return true; }
                // MW2 drops ground on faces steeper than its walkable limit and for a frame
                // over every bump; RoR2's terrain is full of both. Standing within a short hop
                // of ground (or grounded a moment ago) counts, as it reads in MW2's own maps.
                if (Time.time - lastGrounded < 0.3f) return true;
                var foot = body.footPosition + Vector3.up * 0.4f;
                bool near = Physics.SphereCast(foot, 0.3f, Vector3.down, out var hit, 1.2f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
                if (!near) Plugin.Log.LogInfo($"[killstreaks] not on ground: foot {body.footPosition}, nothing within 1.2 m below");
                return near;
            }
        }
        float lastGrounded = -10f;

        /// Playtest: stop every running streak (and the Predator) between steps.
        public void EndAllForTest()
        {
            EndPredator(false);
            EndAll();
            uavEnds.Clear();
            foreach (var p in uavPlanes) RemoveUav(p);
            uavPlanes.Clear();
            if (callIn != null) { CallInRestore?.Invoke(); callIn = null; }
        }

        public Mw2Crate FirstCrate => crates.Count > 0 ? crates[0] : null;

        public void Start(StreakJob job, float delay = 0f)
        {
            job.K = this;
            if (delay > 0f) pending.Add((job, Time.time + delay));
            else jobs.Add(job);
        }

        public void AddCrate(Mw2Crate c) => crates.Add(c);

        /// A teammate's crate landed (Mw2Net KCrate): ours to take too.
        public void CrateFromTeam(uint id, Vector3 at, uint contents)
        {
            if (body == null || crates.Exists(c => c.Id == id)) return;
            crates.Add(new Mw2Crate(id, at, contents));
        }

        /// Someone took (or the owner lost) the crate: gone here too.
        public void CrateGone(uint id)
        {
            for (int i = crates.Count - 1; i >= 0; i--)
                if (crates[i].Id == id) { crates[i].Destroy(); crates.RemoveAt(i); }
        }

        /// A carried streak was cancelled: MW2 hands it back.
        public void GiveBack(uint id)
        {
            if (ks != IntPtr.Zero) Native.mw2_streak_give(ks, id);
        }

        /// Sound elems inside MW2 effects (explosion cracks, debris).
        /// Multiplayer: world sounds this client plays are sent to the others (Mw2Net).
        public static Action<string, Vector3> SoundBroadcast;
        /// The local player's rank badge, drawn beside the minimap (Mw2Progress.DrawRankBadge).
        public static Action<Rect> RankBadge;
        /// SoundBroadcast with the sound's own volume (0-1) for the other players.
        public static Action<string, Vector3, float> SoundBroadcastVol;
        public static int NoSoundBroadcast;

        public void FxSound(string alias, Vector3 at) => FxSound(alias, at, 1f);

        public void FxSound(string alias, Vector3 at, float scale)
        {
            if (NoSoundBroadcast == 0) SoundBroadcast?.Invoke(alias, at);
            // Heard from your body; dead and spectating there is none, so from the camera that's
            // following your teammate (playtest 10-06-26: no MW2 gun sounds while spectating).
            var cam = body == null ? Camera.main : null;
            if (body == null && cam == null) return;
            float d = Vector3.Distance(at, body != null ? body.corePosition : cam.transform.position);
            PlayAlias(alias, Mathf.Clamp01(1f - (d - 20f) / 230f) * scale);
        }

        /// Every sound this owner's streaks make, scaled (the animatic's background soldier is quieter).
        public float SoundScale = 1f;

        public void PlayAlias(string alias, float scale = 1f)
        {
            scale *= SoundScale;
            if (Audio != null && !string.IsNullOrEmpty(alias) && scale > 0.01f) Audio.PlayAlias(alias, scale);
        }

        public void PlayWeapon(uint weapon, float scale)
        {
            scale *= SoundScale;
            if (Audio != null && weapon != 0) Audio.PlayWeapon(weapon, scale);
        }

        StreakJob RidingJob
        {
            get
            {
                foreach (var j in jobs) if (j.Rides) return j;
                return null;
            }
        }

        bool AirspaceBusy(string slot)
        {
            foreach (var j in jobs) if (j.Airspace == slot) return true;
            foreach (var p in pending) if (p.job.Airspace == slot) return true;
            return false;
        }

        /// The jobs' own HUD only (the showcase shows the nuke's countdown and white-out, nothing else).
        public void DrawJobsHud(Camera cam) { foreach (var j in jobs) j.DrawHud(cam); }

        /// The animatic's MW2 soldier: his jobs run, nothing reads the player's keys.
        public void UpdateJobsOnly() => UpdateJobs();

        void UpdateJobs()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
                if (Time.time >= pending[i].at) { jobs.Add(pending[i].job); pending.RemoveAt(i); }
            for (int i = jobs.Count - 1; i >= 0; i--)
            {
                var j = jobs[i];
                // The nuke runs on real time (its own slow motion mustn't stretch it) - but while the
                // recorder runs the game frame by frame, real time raced ahead: the countdown went by in
                // ~2 s of video and its sound came out sped up (playtest 10-05-26).
                float dt = j is NukeJob ? (Time.captureFramerate > 0 ? 1f / Time.captureFramerate : Time.unscaledDeltaTime) : Time.deltaTime;
                j.Age += dt;
                bool alive;
                try { alive = j.Update(dt); }
                catch (Exception e) { Plugin.Log.LogWarning($"MW2 killstreak {j.GetType().Name} failed: {e}"); alive = false; }
                if (!alive) { j.Stop(); jobs.RemoveAt(i); }
            }
            for (int i = crates.Count - 1; i >= 0; i--)
                if (!crates[i].Update(Time.deltaTime, body, OpenCrate)) crates.RemoveAt(i);
        }

        void EndAll()
        {
            foreach (var j in jobs) j.Stop();
            jobs.Clear();
            pending.Clear();
            foreach (var c in crates) c.Destroy();
            crates.Clear();
            rideCam.Drop();
            if (Time.timeScale < 1f && Time.timeScale > 0f) Time.timeScale = 1f;
        }

        void OpenCrate(Mw2Crate c)
        {
            if (c.Contents == 0)
            {
                if (Sim != IntPtr.Zero) Native.mw2_add_reserve(Sim, 9999);
                Splash("Ammo", "Reserve ammo refilled");
                Play("ammo_crate_use");
            }
            else
            {
                Native.mw2_streak_give(ks, c.Contents);
                Earned(c.Contents, false);
            }
        }

        Vector3? TakePick(out Vector3? dir)
        {
            var at = pickedAt; dir = pickedDir;
            pickedAt = pickedDir = null;
            return at;
        }

        bool StartJob(string name, uint id)
        {
            string chopperBusy = "Air space too crowded";
            switch (name)
            {
                case "counter_uav": Start(new JamJob(this, 30f, "Counter-UAV")); LaunchUavPlane(30f); return true; // the same aircraft (launchUAV, isCounter)
                case "sentry":
                case "airdrop_sentry_minigun":
                    if (BlocksWeapon) return false;
                    Start(new SentryJob(this, id)); return true;
                case "airdrop": Start(new CarePackageJob(this, TakePick(out _) ?? Mw2Strike.AimPoint(body, 60f))); return true;
                case "airdrop_mega": Start(new EmergencyAirdropJob(this, TakePick(out _) ?? Mw2Strike.AimPoint(body, 60f))); return true;
                case "precision_airstrike": Start(new AirstrikeJob(this, false, TakePick(out var pd), pd)); return true;
                case "harrier_airstrike": Start(new AirstrikeJob(this, true, TakePick(out var hd), hd)); return true;
                case "stealth_airstrike": Start(new StealthBomberJob(this, TakePick(out var sd), sd)); return true;
                case "helicopter":
                case "helicopter_flares":
                    if (AirspaceBusy("chopper")) { Splash(Pretty(name), chopperBusy); return false; }
                    Start(new HeliJob(this, name == "helicopter_flares")); return true;
                case "helicopter_minigun":
                    if (!RequireGround()) return false;
                    if (AirspaceBusy("chopper")) { Splash(Pretty(name), chopperBusy); return false; }
                    Start(new ChopperGunnerJob(this)); return true;
                case "ac130":
                    if (!RequireGround()) return false;
                    if (AirspaceBusy("ac130")) { Splash(Pretty(name), chopperBusy); return false; }
                    Start(new Ac130Job(this)); return true;
                case "emp": Start(new EmpJob(this)); return true;
                case "nuke":
                    foreach (var j in jobs) if (j is NukeJob) return false;
                    Start(new NukeJob(this)); Mw2Net.SendTeam(Mw2Net.TeamNuke, 10f); return true; // _nuke.gsc: the whole match sees it
            }
            return false;
        }

        bool RequireGround()
        {
            // MW2 refuses ride killstreaks in the air.
            if (!OnGround) { Splash("Killstreak", "You must be on the ground"); return false; }
            return true;
        }

        // ---------------------------------------------------------------- HUD (MW2's own art)

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old;
        }

        static GUIStyle splashStyle, subStyle, listStyle;

        // Enemies the sweep caught: when, and where they were (MW2 shows that spot, not a live track).
        readonly Dictionary<CharacterBody, float> pinged = new Dictionary<CharacterBody, float>();
        readonly Dictionary<CharacterBody, Vector3> pingPos = new Dictionary<CharacterBody, Vector3>();
        float lastSweepX = -1f;
        uint splashIcon;
        string splashMaterial;

        /// MW2's thermal friend-or-foe overlay (ThermalVisionFOFOverlayOn in the Predator, AC-130 and
        /// chopper views): a mark on every player on your side - you included, so you don't fire on
        /// yourself (playtest 10-04-26) - and on every enemy. The AC-130 and chopper use the FOF boxes;
        /// the missile camera uses the engine's remotemissile_target_hostile / _friendly circles
        /// (registered beside the FOF boxes in iw4mp's HUD media). Sizes are approximate.
        void DrawFof(Camera view, bool missile)
        {
            if (view == null || body == null) return;
            float tanHalf = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                bool hostile = Mw2Strike.IsEnemy(body, cb);
                bool friend = !hostile && cb.isPlayerControlled;
                if (!hostile && !friend) continue;
                var sp = view.WorldToScreenPoint(cb.corePosition);
                if (sp.z <= 0.5f) continue;
                float px = Mathf.Clamp(cb.bestFitRadius * 2.6f * Screen.height / (2f * sp.z * tanHalf), 30f, 160f);
                var r = new Rect(sp.x - px * 0.5f, Screen.height - sp.y - px * 0.5f, px, px);
                if (missile) Mw2Icons.Draw(r, friend ? "remotemissile_target_friendly" : "remotemissile_target_hostile", Color.white);
                else Mw2Icons.DrawGlow(r, friend ? "hud_fofbox_self" : "hud_fofbox_hostile", Color.white);
            }
        }

        public void DrawHud(Camera cam)
        {
            if (ks == IntPtr.Zero || body == null) return;
            if (splashStyle == null)
            {
                splashStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                subStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
                listStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            }
            float w = Screen.width, h = Screen.height;

            var ride = RidingJob;
            if ((ride != null || predator.phase == PredPhase.Flying) && Mw2Fx.View != null) DrawFof(Mw2Fx.View, ride == null); // once the ride camera is up
            if (ride != null) { ride.DrawHud(cam); return; } // MW2 shows only the ride's view

            if (predator.phase == PredPhase.Laptop)
                Fill(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, 1f - Mathf.Clamp01(predator.timer / PredLaptopSeconds)));
            if (predator.phase == PredPhase.Static)
            {
                // _remotemissile.gsc staticEffect: a white screen with ac130_overlay_grain over it.
                Fill(new Rect(0, 0, w, h), Color.white);
                var grain = Mw2Icons.Get("ac130_overlay_grain");
                if (grain != null) GUI.DrawTextureWithTexCoords(new Rect(0, 0, w, h), grain, new Rect(UnityEngine.Random.value, UnityEngine.Random.value, w / grain.width, h / grain.height), true);
            }
            if (predator.phase == PredPhase.Flying)
            {
                // MW2's missilecam_hud: the missilecam_reticle and the Steer / Boost hints. (The
                // grain _remotemissile.gsc precaches is only the static when the missile hits.)
                Mw2MenuHud.DrawRide(Mw2MenuHud.RidePredator, "remotemissile_projectile_mp");
                return; // MW2 shows only the missile view
            }

            // Earned splash: the streak's icon over its name (MW2's killstreak splash).
            if (Time.unscaledTime < splashUntil)
            {
                float a = Mathf.Clamp01((splashUntil - Time.unscaledTime) / 0.4f);
                float iy = h * 0.12f;
                string splashArt = splashIcon != 0 ? Native.StreakString(splashIcon, 5) : splashMaterial;
                if (!string.IsNullOrEmpty(splashArt)) Mw2Icons.Draw(new Rect(w / 2 - 48, iy, 96, 96), splashArt, new Color(1f, 1f, 1f, a));
                Mw2Font.Label(new Rect(0, iy + 100, w, 40), splash, Mw2Hud.S(40f), new Color(1f, 1f, 1f, a), TextAnchor.MiddleCenter, Mw2Font.Objective);
                Mw2Font.Label(new Rect(0, iy + 140, w, 24), splashSub, Mw2Hud.S(24f), new Color(0.85f, 0.85f, 0.85f, a), TextAnchor.MiddleCenter, Mw2Font.Small);
            }

            // Streak icons, right side, cheapest at the bottom (MW2's HUD order): lit when
            // earned and unused, dim while still to earn.
            var st = State();
            float icon = Mathf.Round(h * 0.045f);
            float x = w - icon - 24f, y = h * 0.62f;
            for (int i = 0; i < st.loadoutLen; i++)
            {
                uint id = st.loadout[i];
                bool have = false;
                for (int k = 0; k < st.stackLen; k++) if (st.stack[k] == id) have = true;
                uint kills = Native.mw2_streak_next_kills(ks, id); // this lap's count
                var r = new Rect(x, y - i * (icon + 6f), icon, icon);
                Mw2Icons.Draw(r, Native.StreakString(id, 5), have ? Color.white : new Color(1f, 1f, 1f, 0.3f));
                Mw2Font.Label(new Rect(x - 60, r.y, 54, icon), $"{kills}", Mw2Hud.S(22f), have ? Color.white : new Color(1f, 1f, 1f, 0.5f), TextAnchor.MiddleRight, Mw2Font.Small);
            }
            Mw2Font.Label(new Rect(x - 260, y + icon + 4, 260 + icon, 22), $"Streak {st.count}", Mw2Hud.S(22f), Color.white, TextAnchor.MiddleRight, Mw2Font.Small);

            // Newest earned streak, ready to use: MW2's animated dpad icon + key. With MW2's own
            // menus running it goes in dpad_hd's action slot 4 (_killstreaks.gsc _setActionSlot(4)).
            if (Mw2MenuHud.Ready) Mw2MenuHud.SetDpad(4, st.stackLen > 0 ? Native.StreakString(st.stack[0], 7) : "", 8, 4);
            else if (st.stackLen > 0)
            {
                uint top = st.stack[0];
                float dw = Mathf.Round(h * 0.07f);
                var r = new Rect(x + icon - dw, y + icon + 30, dw, dw);
                Mw2Icons.DrawSheetFrame(r, Native.StreakString(top, 7), (int)(Time.unscaledTime * 15f), Color.white);
                Mw2Font.Label(new Rect(r.x - 260, r.y, 254, dw), $"[{KeyLabel}] {Pretty(Native.StreakString(top, 0))}", Mw2Hud.S(22f), Color.white, TextAnchor.MiddleRight, Mw2Font.Small);
            }

            foreach (var c in crates) c.Draw(cam, body);
            foreach (var j in jobs) j.DrawHud(cam);

            DrawRadar(cam);
        }

        /// MW2's minimap: square, top left, turns with the player - the stage itself from straight
        /// above (RoR2 has no map art: an orthographic camera draws it), friendlies always, an enemy
        /// as a red dot while it's fighting (MW2: firing unsuppressed), and everyone under a UAV, the
        /// sweep line passing them, then fading (two UAVs sweep twice as fast).
        // CharacterBody's seconds since it last dealt or took damage (private in RoR2).
        static readonly HarmonyLib.AccessTools.FieldRef<CharacterBody, float> combatAge =
            HarmonyLib.AccessTools.FieldRefAccess<CharacterBody, float>("outOfCombatStopwatch");
        static float CombatAge(CharacterBody cb) { try { return combatAge(cb); } catch { return 99f; } }

        static Rect RadarRect()
        {
            float size = Mathf.Round(Screen.height * 0.22f);
            return new Rect(20, Mathf.Round(Screen.height * 0.10f), size, size);
        }

        /// The minimap's visible frame (GUI pixels, y down): minimap_background's frame lines. RoR2's
        /// money panel lines up under it.
        public static Rect RadarFrame()
        {
            var r = RadarRect();
            float k = r.width / 256f; // the frame lines: x 27 / 238, y 11 / 222 from the top of the art
            return new Rect(r.x + 27f * k, r.y + 11f * k, 212f * k, 212f * k);
        }

        void DrawRadar(Camera cam)
        {
            var rect = RadarRect();
            float size = rect.width;
            var centre = rect.center;
            // minimap_background's window isn't centred in its 256 px art (measured from its alpha:
            // frame lines at x 27 / 238, y 11 / 222 from the top): x 28-238, y 12-222. A centred inset
            // put the map flush with the left line and under the bottom one (playtest 10-06-26).
            var inner = new Rect(rect.x + size * 28f / 256f, rect.y + size * 12f / 256f, size * 210f / 256f, size * 210f / 256f);
            centre = inner.center; // you, the dots and the sweep sit on the map's middle, not the art's
            // Dots and arrows stay inside the window too (as a fraction of the half-size, less a margin).
            float edge = inner.width / size - 0.05f;
            Fill(inner, new Color(0.03f, 0.05f, 0.04f, 0.55f));
            float range = Mathf.Max(Plugin.Instance.RadarRange.Value, 5f);
            if (cam != null && body != null)
            {
                var map = Mw2Minimap.Render(cam, body.footPosition, Flat(cam.transform.forward), range * (inner.width / size));
                if (map != null)
                {
                    var was = GUI.color;
                    GUI.color = new Color(0.62f, 0.8f, 0.66f, 0.9f);
                    GUI.DrawTexture(inner, map, ScaleMode.StretchToFill, false);
                    GUI.color = was;
                }
            }
            Mw2Icons.Draw(inner, "minimap_scanlines", new Color(1f, 1f, 1f, 0.25f));
            Mw2Icons.Draw(rect, "minimap_background", Color.white);
            RankBadge?.Invoke(rect);
            float arrow = Mathf.Round(size * 0.1f);
            float dot = Mathf.Round(size * 0.06f);
            if (cam != null && body != null)
            {
                var fwd0 = Flat(cam.transform.forward);
                var right0 = new Vector3(fwd0.z, 0f, -fwd0.x);
                var me0 = body.footPosition;
                var mine = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.Player;
                foreach (var cb in CharacterBody.readOnlyInstancesList)
                {
                    if (cb == null || cb == body || cb.teamComponent == null || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                    if (cb.master == null) continue; // pots / barrels: not on the radar
                    var d = cb.footPosition - me0;
                    var local = new Vector2(Vector3.Dot(d, right0), Vector3.Dot(d, fwd0)) / range;
                    if (Mathf.Abs(local.x) > edge || Mathf.Abs(local.y) > edge) continue;
                    var p = centre + new Vector2(local.x, -local.y) * (size / 2f);
                    if (cb.teamComponent.teamIndex == mine)
                    {
                        // Friendlies (players, drones, turrets): always, MW2's friendly arrow facing their way.
                        var f = Flat(cb.inputBank != null ? cb.inputBank.aimDirection : cb.transform.forward);
                        float ang = Vector3.SignedAngle(fwd0, f, Vector3.up);
                        var m = GUI.matrix;
                        GUIUtility.RotateAroundPivot(ang, p);
                        float fs = Mathf.Round(arrow * 0.8f);
                        Mw2Icons.Draw(new Rect(p.x - fs / 2, p.y - fs / 2, fs, fs), "compassping_player", new Color(0.45f, 0.75f, 1f, 1f));
                        GUI.matrix = m;
                    }
                    else if (cb.teamComponent.teamIndex != TeamIndex.Neutral && CombatAge(cb) < 1.5f && !pinged.ContainsKey(cb))
                    {
                        // Fighting: a red dot, fading as the fight goes quiet.
                        float a = 1f - CombatAge(cb) / 1.5f;
                        Mw2Icons.Draw(new Rect(p.x - dot / 2, p.y - dot / 2, dot, dot), "compassping_enemy", new Color(1f, 1f, 1f, a));
                    }
                }
            }
            Mw2Icons.Draw(new Rect(centre.x - arrow / 2, centre.y - arrow / 2, arrow, arrow), "compassping_player", Color.white);
            int uavs = UavsActive();
            if (uavs == 0 || cam == null) { pinged.Clear(); lastSweepX = -1f; return; }

            // Sweep period: approximate, not read from MW2 (the engine's radar dvars aren't in the scripts).
            float period = uavs >= 2 ? 2f : 4f;
            float sweep = (Time.time % period) / period; // 0..1 left to right
            float sweepX = inner.x + sweep * inner.width; // across the map window
            var line = Mw2Icons.Get("compass_radarline");
            if (line != null)
            {
                // The art is a horizontal bar; MW2 sweeps it as a vertical line.
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(90f, new Vector2(sweepX, centre.y));
                GUI.DrawTexture(new Rect(sweepX - inner.height / 2, centre.y - 3, inner.height, 6), line, ScaleMode.StretchToFill, true);
                GUI.matrix = m;
            }
            else Fill(new Rect(sweepX - 1, inner.y, 2, inner.height), new Color(0.6f, 1f, 0.6f, 0.35f));

            var fwd = Flat(cam.transform.forward);
            var right = new Vector3(fwd.z, 0f, -fwd.x);
            var me = body.footPosition;
            Vector2 OnMap(Vector3 world, out bool inside)
            {
                var d = world - me;
                var local = new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, fwd)) / range;
                inside = Mathf.Abs(local.x) <= edge && Mathf.Abs(local.y) <= edge;
                return centre + new Vector2(local.x, -local.y) * (size / 2f);
            }
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb == body || cb.teamComponent == null || cb.teamComponent.teamIndex == body.teamComponent.teamIndex) continue;
                if (cb.healthComponent == null || !cb.healthComponent.alive || cb.master == null) continue;
                var p = OnMap(cb.footPosition, out bool inside);
                if (!inside) continue;
                bool crossed = lastSweepX >= 0f && (lastSweepX <= sweepX ? (p.x >= lastSweepX && p.x < sweepX) : (p.x >= lastSweepX || p.x < sweepX));
                if (crossed) { pinged[cb] = Time.time; pingPos[cb] = cb.footPosition; }
            }
            lastSweepX = sweepX;
            var stale = new List<CharacterBody>();
            foreach (var kv in pinged)
            {
                float age = (Time.time - kv.Value) / period;
                if (age > 1f || kv.Key == null) { stale.Add(kv.Key); continue; }
                var p = OnMap(pingPos[kv.Key], out bool inside);
                if (inside) Mw2Icons.Draw(new Rect(p.x - dot / 2, p.y - dot / 2, dot, dot), "compassping_enemy", new Color(1f, 1f, 1f, 1f - age));
            }
            foreach (var k in stale) { pinged.Remove(k); pingPos.Remove(k); }
        }
    }
}
