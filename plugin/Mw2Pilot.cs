using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoR2;
using UnityEngine;
using UnityEngine.SceneManagement;
using Path = System.IO.Path;

namespace MW2RoR2
{
    /// Inputs the mod reads, with an override the playtest pilot can drive. Real input still works.
    static class In
    {
        /// The mod's keyboard keys, except while RoR2's chat or console has the keyboard (typing "gg"
        /// isn't a grenade).
        public static bool Key(KeyCode k) => !Typing && Input.GetKey(k);
        public static bool KeyDown(KeyCode k) => !Typing && Input.GetKeyDown(k);

        static int typingFrame = -1;
        static bool typing;
        static float nextChatScan;
        static RoR2.UI.ChatBox[] chats;
        static readonly System.Reflection.FieldInfo showInput = HarmonyLib.AccessTools.Field(typeof(RoR2.UI.ChatBox), "_showInput");
        public static bool Typing
        {
            get
            {
                if (Time.frameCount == typingFrame) return typing;
                typingFrame = Time.frameCount;
                typing = false;
                if (RoR2.UI.ConsoleWindow.instance != null || Mw2Menus.Editing || Mw2Cinema.InputBlocked) return typing = true;
                if (Time.unscaledTime >= nextChatScan) { nextChatScan = Time.unscaledTime + 1f; chats = UnityEngine.Object.FindObjectsOfType<RoR2.UI.ChatBox>(); }
                if (chats != null && showInput != null) foreach (var c in chats) if (c != null && (bool)showInput.GetValue(c)) return typing = true;
                return typing;
            }
        }

        public static bool Attack(InputBankTest b) => (b != null && b.skill1.down) || Mw2Pilot.Attack;
        public static bool AttackPressed(InputBankTest b) => (b != null && b.skill1.justPressed) || Mw2Pilot.Consume(ref Mw2Pilot.AttackPulse);
        public static bool Ads(InputBankTest b) => (b != null && b.skill2.down) || Mw2Pilot.Ads;
        /// MW2's own sprint key when their MW2 binds name one; else RoR2's sprint - but never while the
        /// crouch or prone key is down (RoR2's default Ctrl sprint against an MW2 Ctrl stance).
        public static bool Sprint(InputBankTest b)
        {
            if (Mw2Pilot.Sprint) return true;
            if (Mw2Binds.Sprint is KeyCode s) return Key(s);
            return b != null && b.sprint.down && !Key(CrouchKey) && !Key(ProneKey);
        }
        public static KeyCode CrouchKey => Mw2Binds.Crouch ?? Plugin.Instance.CrouchKey.Value;
        public static KeyCode ProneKey => Mw2Binds.Prone ?? Plugin.Instance.ProneKey.Value;
        public static bool Jump(InputBankTest b) => (b != null && b.jump.down) || Mw2Pilot.Jump;
        public static bool Interact(InputBankTest b) => (b != null && b.interact.down) || Mw2Pilot.Interact;
        public static bool InteractPressed(InputBankTest b) => (b != null && b.interact.justPressed) || Mw2Pilot.Consume(ref Mw2Pilot.InteractPulse);
        public static bool Reload => In.Key(KeyCode.R) || Mw2Pilot.Reload;
        public static bool StreakKeyDown => In.KeyDown(Plugin.Instance.StreakKey.Value) || Mw2Pilot.Consume(ref Mw2Pilot.StreakPulse);
        /// AC-130 gun pick: 0/1/2 or -1.
        public static int GunPick()
        {
            if (In.KeyDown(KeyCode.Alpha1)) return 0;
            if (In.KeyDown(KeyCode.Alpha2)) return 1;
            if (In.KeyDown(KeyCode.Alpha3)) return 2;
            int p = Mw2Pilot.GunPulse; Mw2Pilot.GunPulse = -1;
            return p;
        }

        /// World move vector: the pilot's forward/right relative to the aim, else RoR2's.
        public static Vector3 Move(InputBankTest b)
        {
            if (!Mw2Pilot.Moving || b == null) return b != null ? b.moveVector : Vector3.zero;
            // Relative to the aim the sim steers by (the pilot's own view could be off by 90 deg:
            // forward became a strafe, so it never sprinted).
            var f = b.aimDirection; f.y = 0f; f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            var r = new Vector3(f.z, 0f, -f.x);
            return Vector3.ClampMagnitude(f * Mw2Pilot.Fwd + r * Mw2Pilot.Right, 1f);
        }
    }

    /// Autonomous playtest: started by a trigger file (`run.txt` in the playtest folder). Starts a
    /// singleplayer run, turns MW2 mode on, works through movement, guns, scopes and every
    /// killstreak, takes screenshots and writes metrics, then quits the game.
    static class Mw2Pilot
    {
        public static bool Active;
        /// This session was a pilot run (Active goes off when it finishes, before the game quits).
        public static bool Ran;
        public static bool Attack, Ads, Sprint, Jump, Interact, Reload, Moving, Frag, Smoke, Melee;
        public static bool FrontCam; // third person: put the camera in front of the soldier
        public static float Fwd, Right;
        public static bool AttackPulse, StreakPulse, CrouchPulse, PronePulse, InteractPulse;
        static readonly List<int> skinCycle = new List<int>();
        public static int GunPulse = -1;
        /// Accumulated view turn (IW4 degrees: x pitch + down, y yaw + left), applied like recoil.
        public static Vector3 Look;

        /// Where the pilot's camera looks (world): Look applied as world yaw + pitch on top of
        /// the camera rig, no roll, pitch clamped. Stacking it as a local rotation turned yaw
        /// about a tilted axis (horizon rolled, upside down), and RoR2's aim never sees it, so
        /// steering measures against this instead.
        public static Vector3 ViewForward = Vector3.forward;
        /// A run testing ThirdPersonOnly ("thirdonly=1"): the pilot stays in third person too.
        public static bool ThirdOnlyTest;
        /// A step testing the ThirdPersonOnly setting itself (the pilot otherwise ignores it).
        public static bool ThirdOnlyConfigTest;

        static Quaternion steerBase, steerOut;

        public static void Steer(Transform cam)
        {
            // Idempotent: the camera hook can run more than once before RoR2's rig places the
            // camera again; re-steering our own output stacked Look every call. MW2's kick / sway
            // land on top of our output afterwards (AfterCamera folds them into the base).
            if (Quaternion.Angle(cam.rotation, steerOut) > 0.01f) steerBase = cam.rotation;
            var f = steerBase * Vector3.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg - Look.y;
            float pitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg + Look.x;
            if (pitch > 85f) { Look.x -= pitch - 85f; pitch = 85f; }
            if (pitch < -85f) { Look.x += -85f - pitch; pitch = -85f; }
            Look.y = Mathf.Repeat(Look.y + 180f, 360f) - 180f;
            cam.rotation = steerOut = Quaternion.Euler(pitch, yaw, 0f);
            ViewForward = cam.rotation * Vector3.forward;
        }

        /// After MW2's view code added kick / sway to our output: keep that delta in the base and
        /// call the result ours, so a big kick (launchers) isn't mistaken for RoR2 re-placing the camera.
        public static void AfterCamera(Transform cam)
        {
            steerBase = steerBase * (Quaternion.Inverse(steerOut) * cam.rotation);
            steerOut = cam.rotation;
        }

        public static bool Consume(ref bool pulse) { bool p = pulse; pulse = false; return p; }

        // ------------------------------------------------------------ runner

        class Step
        {
            public string Name;
            public float Duration;
            public Action Begin;
            public Action<float> Tick;
            public float[] Shots = new float[0];
            public Action End;
            /// Look around / move / shoot like a player while the step runs (Roam).
            public bool Roam;
        }

        static readonly List<Step> steps = new List<Step>();
        static int stepIndex = -1;
        static float stepStart, phaseTimer;
        /// Step time: real time, except while the showcase records - then the video's 1/60 s a frame
        /// (each rendered frame is one video frame however long it takes; on real time the clips
        /// came out half length, pilot 10-05-26).
        static float clock;
        static int shotIndex;
        static string outDir;
        static StreamWriter metrics;
        enum Phase { Title, Lobby, WaitBody, Steps, Quit }
        static Phase phase;
        static readonly Dictionary<string, int> errors = new Dictionary<string, int>();
        static int frames;
        static float lateMaxMs, fpsSum, fpsMin = 999f, maxSpeed, sumSpeed, startZ, maxZ;
        static int speedSamples;
        static float swayMax, turnMax, pitchMax, enemyMin = 999f;
        static string enemyMinName = "";
        static Vector3 lastFwd;
        static string filter = "";
        static readonly System.Collections.Generic.Dictionary<string, string> settings = new System.Collections.Generic.Dictionary<string, string>();
        /// A run.txt setting (`skin=45`, `throwshots=1`), else the environment variable.
        internal static string Setting(string name, string env) => settings.TryGetValue(name, out var v) ? v : Environment.GetEnvironmentVariable(env);

        /// The playtest asked for this step by name (lets the real trigger run, e.g. the prematch at spawn).
        public static bool Wants(string step) => Active && filter.Split(',').Any(f => f.Trim() == step);
        static bool video, reel, cinema, animatic;
        public static string CurrentStep => stepIndex >= 0 && stepIndex < steps.Count ? steps[stepIndex].Name + (SubLabel != null ? "/" + SubLabel : "") : "";
        /// A step that schedules its own beats (the animatic relay) names the current one here.
        static string SubLabel;
        /// A step that ends on its own schedule sets this (Next runs at once).
        static bool stepDone;
        static Mw2Bridge bridge;

        public static void TryStart(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return; // players: no pilot
                var trigger = Path.Combine(dir, "run.txt");
                if (!File.Exists(trigger)) return;
                filter = File.ReadAllText(trigger).Trim();
                // "video" in the filter records the run (Mw2Recorder); the rest are step prefixes.
                var parts = filter.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                video = parts.Remove("video");
                // "reel": a clean recording for showing off - video on, no monster spawns, and the
                // gunfight / killstreak targets stand still (AI off) instead of attacking.
                reel = parts.Remove("reel");
                if (reel) video = true;
                // "cinema": the showcase reel (Mw2Cinema) - a clean run (as reel) on a chosen stage,
                // parked at a vista, every frame at 60 fps into ffmpeg instead of the review video.
                cinema = parts.Remove("cinema");
                if (cinema) { reel = true; video = false; if (!parts.Any(x => x.StartsWith("cine_"))) parts.Add("cine"); }
                // "animatic": the showcase as one take (playtest 10-05-26) - cinema's stage, framing and
                // recording, but the monsters live and roam the shot (the cameraman is on their team).
                animatic = parts.Remove("animatic");
                if (animatic) { cinema = true; video = false; if (!parts.Any(x => x.StartsWith("anim_"))) parts.Add("anim_go"); }
                // name=value tokens are settings (Steam launches the game, so env vars don't reach it).
                settings.Clear();
                foreach (var kv in parts.Where(x => x.Contains('=')).ToList()) { parts.Remove(kv); int i = kv.IndexOf('='); settings[kv.Substring(0, i).Trim()] = kv.Substring(i + 1).Trim(); }
                filter = string.Join(",", parts);
                outDir = Path.Combine(dir, DateTime.Now.ToString("yyMMdd-HHmmss"));
                Directory.CreateDirectory(outDir);
                File.Move(trigger, Path.Combine(outDir, "run.started"));
                metrics = new StreamWriter(Path.Combine(outDir, "metrics.jsonl")) { AutoFlush = true };
                Application.logMessageReceived += OnLog;
                Active = true; Ran = true;
                phase = Phase.Title;
                Plugin.Log.LogInfo($"[pilot] playtest started -> {outDir} (filter '{filter}') at {Time.realtimeSinceStartup:F1}s");
                try { Native.mw2_pdata_readonly(1); } catch (Exception e) { Plugin.Log.LogWarning($"[pilot] playerdata read-only: {e.Message}"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[pilot] could not start: {e.Message}"); }
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;
            string key = msg.Length > 160 ? msg.Substring(0, 160) : msg;
            errors[key] = errors.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        static void Write(string json) { try { metrics?.WriteLine(json); } catch { } }

        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "'");

        static void Cmd(string cmd)
        {
            Plugin.Log.LogInfo($"[pilot] console: {cmd}");
            RoR2.Console.instance.SubmitCmd(null, cmd);
        }

        static void Shot(string name)
        {
            string path = Path.Combine(outDir, $"{shotIndex++:000}_{name}.png");
            ScreenCapture.CaptureScreenshot(path);
        }

        static void ClearInput()
        {
            Attack = Ads = Sprint = Jump = Interact = Reload = Moving = Frag = Smoke = Melee = false;
            Fwd = Right = 0f;
            AttackPulse = StreakPulse = InteractPulse = false;
            GunPulse = -1;
        }

        /// Per frame from Plugin.Update.
        public static void Update(Mw2Bridge b, CharacterBody body)
        {
            if (!Active && phase != Phase.Quit) return;
            bridge = b;
            phaseTimer += Time.unscaledDeltaTime;
            clock += Mw2Cinema.Recording ? (Mw2Cinema.HoldingTime ? 0f : 1f / Mw2Cinema.Fps) : Time.unscaledDeltaTime;
            // Remote Desktop / focus changes can fire RoR2's pause (as Esc does); a paused game
            // never spawns the player. The pilot is driving, so it unpauses.
            if (phase != Phase.Quit && RoR2.PauseManager.isPaused && Time.unscaledTime >= nextUnpause)
            {
                nextUnpause = Time.unscaledTime + 1f;
                Plugin.Log.LogInfo("[pilot] game was paused; unpausing");
                Cmd("pause");
            }
            string scene = SceneManager.GetActiveScene().name;
            switch (phase)
            {
                case Phase.Title:
                    // No intro/splash skipping: with intro_skip the run never initialized RoR2's
                    // Rewired input, paused itself and never spawned the player (10-02-26).
                    if (RoR2.Console.instance != null && scene == "title" && phaseTimer > 4f)
                    {
                        Plugin.Log.LogInfo($"[pilot] title at {Time.realtimeSinceStartup:F1}s");
                        Shot("title");
                        Cmd("transition_command \"gamemode ClassicRun; host 0;\"");
                        phase = Phase.Lobby; phaseTimer = 0f;
                    }
                    break;
                case Phase.Lobby:
                    // lobbycac=1: Create-a-Class from character select, driven by keys, backed out with Esc
                    // (playtest 10-04-26: the game froze coming out of it there).
                    if (lobbyCacAt >= 0f)
                    {
                        float lt = phaseTimer - lobbyCacAt;
                        while (lobbyKeys.Count > 0 && lt >= lobbyKeys[0].at)
                        {
                            Plugin.Log.LogInfo($"[pilot] lobby cac key {lobbyKeys[0].keys} at {lt:F1}s, menu open {Mw2Menus.IsOpen}");
                            Mw2Menus.Inject(lobbyKeys[0].keys); lobbyKeys.RemoveAt(0);
                        }
                        if (lobbyKeys.Count == 0 && lt > 22f) { Plugin.Log.LogInfo($"[pilot] lobby cac done, menu open {Mw2Menus.IsOpen}"); Shot("lobby_cac_after"); lobbyCacAt = -2f; }
                        else if (lobbyKeys.Count > 0) { if (Mathf.Repeat(lt, 2f) < Time.unscaledDeltaTime) Shot($"lobby_cac_{lt:F0}"); break; }
                        else break;
                    }
                    // Character select takes a moment to accept a start (survivor still loading); try
                    // every 2.5 s until the run exists instead of one shot.
                    if (scene == "lobby" && phaseTimer >= nextStart && lobbyCacAt == -1f && settings.ContainsKey("lobbycac") && lobbyShot)
                    {
                        lobbyCacAt = phaseTimer;
                        lobbyKeys.Clear();
                        // Custom Class 1 -> Primary -> down to Riot Shield -> take it; back; Custom Class 2 ->
                        // Primary -> first category -> first gun; back out to character select.
                        foreach (var k in new (float, uint)[] {
                            (1.0f, Mw2Menus.KeyEnter), (2.0f, Mw2Menus.KeyEnter),
                            (3.0f, Mw2Menus.KeyDown), (3.3f, Mw2Menus.KeyDown), (3.6f, Mw2Menus.KeyDown), (3.9f, Mw2Menus.KeyDown),
                            (4.6f, Mw2Menus.KeyEnter), (5.6f, Mw2Menus.KeyEnter), (6.6f, Mw2Menus.KeyEnter), (7.6f, Mw2Menus.KeyEsc), (8.4f, Mw2Menus.KeyEsc),
                            (9.2f, Mw2Menus.KeyDown), (9.8f, Mw2Menus.KeyEnter), (10.8f, Mw2Menus.KeyEnter), (11.8f, Mw2Menus.KeyEnter), (12.8f, Mw2Menus.KeyEnter),
                            (13.8f, Mw2Menus.KeyEsc), (14.6f, Mw2Menus.KeyEsc), (15.4f, Mw2Menus.KeyEsc), (16.2f, Mw2Menus.KeyEsc), (17.0f, Mw2Menus.KeyEsc) })
                            lobbyKeys.Add(k);
                        Mw2Menus.Open("cac_popup");
                        Plugin.Log.LogInfo("[pilot] lobby cac opened");
                        break;
                    }
                    if (scene == "lobby" && phaseTimer >= nextStart)
                    {
                        nextStart = phaseTimer + 3f;
                        // Play the MW2 Soldier (MW2 mode is his); MW2_PILOT_SKIN picks his skin for the run.
                        var lu = LocalUserManager.GetFirstLocalUser();
                        var nu = lu?.currentNetworkUser;
                        var bi = BodyCatalog.FindBodyIndex(Mw2Survivor.BodyName);
                        if (nu != null && bi != BodyIndex.None) nu.CallCmdSetBodyPreference(bi);
                        if (!lobbyShot && lu?.userProfile != null && bi != BodyIndex.None)
                        {
                            lobbyShot = true;
                            if (uint.TryParse(Setting("skin", "MW2_PILOT_SKIN"), out uint want))
                            {
                                var lo = new Loadout();
                                lu.userProfile.CopyLoadout(lo);
                                restoreSkin = lo.bodyLoadoutManager.GetSkinIndex(bi);
                                lo.bodyLoadoutManager.SetSkinIndex(bi, want);
                                lu.userProfile.SetLoadout(lo);
                                Plugin.Log.LogInfo($"[pilot] MW2 Soldier skin {want} ({Mw2Skins.Key((int)want, 0)}), was {restoreSkin}");
                            }
                            if (int.TryParse(Setting("skincycle", ""), out int cycle) && cycle > 0)
                            {
                                skinCycle.Clear();
                                for (int k = 1; k <= cycle; k++) skinCycle.Add(k);
                                for (int k = 1; k <= Math.Min(3, cycle); k++) skinCycle.Add(k);
                                var lo0 = new Loadout();
                                lu.userProfile.CopyLoadout(lo0);
                                restoreSkin = lo0.bodyLoadoutManager.GetSkinIndex(bi);
                            }
                            if (settings.ContainsKey("loadouttab"))
                                foreach (var tab in UnityEngine.Object.FindObjectsOfType<RoR2.UI.HGButton>())
                                    if (tab.name.Contains("(Loadout)")) { tab.onClick.Invoke(); Plugin.Log.LogInfo("[pilot] Loadout tab opened"); }
                            // menutest=1: an MW2 menu over character select (RoR2's UI must keep working).
                            if (settings.ContainsKey("menutest")) Mw2Menus.Open("cac_popup");
                            nextStart = phaseTimer + 4f; // let character select show him
                            break;
                        }
                        // lobbyecho=1: our soldier drawn from our own lobby message (a teammate's path), head 5.
                        if (settings.ContainsKey("lobbyecho") && !Mw2Net.LobbyEcho) { Mw2Net.Echo = true; Mw2Net.LobbyEcho = true; Plugin.Log.LogInfo("[pilot] lobby echo on"); }
                        if (Mw2Net.LobbyEcho) Mw2Net.LocalLobbyHead = 5;
                        // classcycle=1: the character-select soldier holds each default class's primary.
                        if (settings.ContainsKey("classcycle") && classCycle == null)
                        {
                            classCycle = new List<int> { 11, 12, 13, 14, 15 };
                            classWas = (Plugin.Instance.Class.Value, Plugin.Instance.UseCustomClass.Value);
                        }
                        if (classCycle != null && classCycle.Count > 0)
                        {
                            if (classShot) { Shot($"class_{Plugin.Instance.PlayClass}"); classCycle.RemoveAt(0); classShot = false; nextStart = phaseTimer + 0.2f; break; }
                            Plugin.Instance.UseCustomClass.Value = true;
                            Plugin.Instance.Class.Value = classCycle[0];
                            Plugin.Log.LogInfo($"[pilot] class {Plugin.Instance.PlayClass}: {string.Join(" / ", Mw2Menus.ClassLoadout(Plugin.Instance.PlayClass - 1) ?? new string[0])}");
                            classShot = true;
                            nextStart = phaseTimer + 1.2f;
                            if (classCycle.Count == 1) { } // restored after the last shot
                            break;
                        }
                        if (classCycle != null && classWas.HasValue) { Plugin.Instance.Class.Value = classWas.Value.Item1; Plugin.Instance.UseCustomClass.Value = classWas.Value.Item2; classWas = null; }
                        if (skinCycle.Count > 0 && lu?.userProfile != null)
                        {
                            var lo = new Loadout();
                            lu.userProfile.CopyLoadout(lo);
                            lo.bodyLoadoutManager.SetSkinIndex(bi, (uint)skinCycle[0]);
                            lu.userProfile.SetLoadout(lo);
                            Plugin.Log.LogInfo($"[pilot] skin cycle -> {skinCycle[0]}");
                            skinCycle.RemoveAt(0);
                            nextStart = phaseTimer + 1.2f;
                            if (skinCycle.Count == 0) { lo.bodyLoadoutManager.SetSkinIndex(bi, restoreSkin ?? 0u); lu.userProfile.SetLoadout(lo); }
                            break;
                        }
                        Shot("lobby");
                        if (Mw2Menus.IsOpen) Mw2Menus.CloseAll();
                        if (settings.ContainsKey("dumpui")) DumpLoadoutUi();
                        Mw2Net.Echo = false; Mw2Net.LobbyEcho = false;
                        Cmd("pregame_start_run");
                    }
                    if (Run.instance != null) { Plugin.Log.LogInfo($"[pilot] run started at {Time.realtimeSinceStartup:F1}s"); phase = Phase.WaitBody; phaseTimer = 0f; }
                    if (phaseTimer > 60f) Finish("never left the lobby");
                    break;
                case Phase.WaitBody:
                    // The spawn, before MW2 mode takes the body (Commando's model must not show).
                    var spawned = LocalUserManager.GetFirstLocalUser()?.cachedBody;
                    if (spawned != null && spawnSeen < 0f) spawnSeen = Time.unscaledTime;
                    if (spawnSeen >= 0f && spawnShots < 3 && Time.unscaledTime - spawnSeen >= 0.6f + spawnShots * 0.7f) { spawnShots++; Shot("spawn"); }
                    // cinema: the stage asked for (stage=, default Distant Roost: its sky has the flying creatures).
                    // Any run with stage= (route checks on one stage, before / after) goes there too.
                    if ((cinema || !string.IsNullOrEmpty(Setting("stage", "MW2_PILOT_STAGE"))) && !stageForced && Run.instance != null && body != null && phaseTimer > 2f)
                    {
                        stageForced = true;
                        string want = Setting("stage", "MW2_PILOT_STAGE") ?? (animatic ? "golemplains" : "blackbeach");
                        var def = SceneCatalog.FindSceneDef(want);
                        if (def != null && SceneManager.GetActiveScene().name != want && UnityEngine.Networking.NetworkServer.active)
                        {
                            Plugin.Log.LogInfo($"[pilot] cinema: to stage {want}");
                            Run.instance.AdvanceStage(def);
                            spawnSeen = -1f; phaseTimer = 0f;
                            break;
                        }
                    }
                    if (body != null && body.healthComponent != null && body.healthComponent.alive && phaseTimer > 1f)
                    {
                        if (phaseTimer > 2.5f) { Plugin.Log.LogInfo($"[pilot] in game {Time.realtimeSinceStartup:F1}s after launch"); ForceResolution(); Build(); phase = Phase.Steps; stepIndex = -1; Next(); if (video) Mw2Recorder.Start(outDir); }
                    }
                    else if (phaseTimer > 90f) Finish("no player body");
                    break;
                case Phase.Steps:
                    RunStep(body);
                    break;
                case Phase.Quit:
                    if (phaseTimer > 2f) Application.Quit();
                    break;
            }
        }

        static bool skipSet, introSkipped, stageForced;
        static float nextStart = 3f, nextUnpause;
        static bool lobbyShot;
        static float lobbyCacAt = -1f;
        static bool renamed;
        static readonly List<(float at, uint keys)> lobbyKeys = new List<(float at, uint keys)>();
        static List<int> classCycle;
        static bool classShot;
        static (int, bool)? classWas;
        static float spawnSeen = -1f;
        static int spawnShots;
        static uint? restoreSkin;

        /// Set a RoR2 convar in memory (RoR2 saves it to config.cfg on exit, so it sticks).
        static bool SetConVar(string name, string value)
        {
            try
            {
                var cv = RoR2.Console.instance.FindConVar(name);
                if (cv == null) { Plugin.Log.LogWarning($"[pilot] no convar {name}"); return false; }
                cv.SetString(value);
                Plugin.Log.LogInfo($"[pilot] {name} = {cv.GetString()}");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[pilot] convar {name}: {e.Message}"); return false; }
        }

        /// End the opening cutscene now (what pressing through it does).
        static void SkipIntro()
        {
            if (introSkipped) return;
            var t = typeof(RoR2.Run).Assembly.GetType("RoR2.IntroCutsceneController");
            if (t == null) return;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(t))
            {
                var set = t.GetProperty("cutsceneIsFinished", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                try { set?.SetValue(set.GetSetMethod(true).IsStatic ? null : o, true); introSkipped = true; Plugin.Log.LogInfo("[pilot] skipped the intro cutscene"); }
                catch (Exception e) { Plugin.Log.LogWarning($"[pilot] intro skip: {e.Message}"); }
            }
        }

        static void Next()
        {
            if (stepIndex >= 0 && stepIndex < steps.Count)
            {
                var s = steps[stepIndex];
                try { s.End?.Invoke(); } catch (Exception e) { OnLog($"[pilot] step end {s.Name}: {e.Message}", "", LogType.Exception); }
                float secs = clock - stepStart;
                var st = bridge.State;
                Write($"{{\"step\":\"{Esc(s.Name)}\",\"secs\":{secs:F2},\"frames\":{frames},\"fps_avg\":{(frames > 0 ? fpsSum / frames : 0):F1},\"fps_min\":{fpsMin:F1},\"late_frame_max_ms\":{lateMaxMs:F1}," +
                      $"\"speed_max_ups\":{maxSpeed:F1},\"speed_avg_ups\":{(speedSamples > 0 ? sumSpeed / speedSamples : 0):F1},\"speed_max_mps\":{maxSpeed * Space.Scale:F2}," +
                      $"\"jump_ups\":{maxZ - startZ:F1},\"sway_max_deg\":{swayMax:F3},\"clip\":{st.clip},\"stock\":{st.stock},\"ads\":{st.adsFrac:F2},\"turn_max_deg_frame\":{turnMax:F1},\"enemy_min_m\":{enemyMin:F2},\"enemy_min\":\"{Esc(enemyMinName)}\",\"pushes\":{Mw2Space.Pushes},\"pitch_max_deg\":{pitchMax:F0},\"errors_total\":{errors.Values.Sum()}}}");
            }
            ClearInput();
            stepIndex++;
            frames = 0; lateMaxMs = 0; fpsSum = 0; fpsMin = 999f; maxSpeed = 0; sumSpeed = 0; speedSamples = 0; swayMax = 0; turnMax = 0; pitchMax = 0; enemyMin = 999f;
            shotIndexStep = 0;
            if (stepIndex >= steps.Count) { Finish("done"); return; }
            var n = steps[stepIndex];
            stepStart = clock;
            startZ = maxZ = bridge.State.origin.z;
            Plugin.Log.LogInfo($"[pilot] step {stepIndex + 1}/{steps.Count}: {n.Name}");
            try { n.Begin?.Invoke(); } catch (Exception e) { OnLog($"[pilot] step begin {n.Name}: {e.Message}", "", LogType.Exception); }
        }

        static int shotIndexStep;
        static float allowDeathUntil;

        static void RunStep(CharacterBody body)
        {
            if (stepIndex < 0 || stepIndex >= steps.Count) return;
            var s = steps[stepIndex];
            float t = clock - stepStart;
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) { float fps = 1f / dt; frames++; fpsSum += fps; fpsMin = Mathf.Min(fpsMin, fps); }
            if (t > 4f) lateMaxMs = Mathf.Max(lateMaxMs, dt * 1000f); // the worst frame once a step has settled (hitches)
            var st = bridge.State;
            float hs = Mathf.Sqrt(st.velocity.x * st.velocity.x + st.velocity.y * st.velocity.y);
            maxSpeed = Mathf.Max(maxSpeed, hs); sumSpeed += hs; speedSamples++;
            maxZ = Mathf.Max(maxZ, st.origin.z);
            var sw = bridge.Sway;
            swayMax = Mathf.Max(swayMax, Mathf.Max(Mathf.Abs(sw.yaw), Mathf.Abs(sw.pitch)));
            if (dt > 0f && frames > 1 && !bridge.Streaks.FreezesPlayer) turnMax = Mathf.Max(turnMax, Vector3.Angle(lastFwd, ViewForward)); // degrees in one frame
            pitchMax = Mathf.Max(pitchMax, Mathf.Abs(Mathf.Asin(Mathf.Clamp(ViewForward.y, -1f, 1f)) * Mathf.Rad2Deg));
            if (body != null)
                foreach (var cb in CharacterBody.readOnlyInstancesList)
                {
                    if (!Mw2Strike.IsEnemy(body, cb) || cb.isBoss) continue;
                    var d = cb.corePosition - body.corePosition;
                    if (Mathf.Abs(d.y) > 3f) continue;
                    d.y = 0f;
                    if (d.magnitude < enemyMin) { enemyMin = d.magnitude; enemyMinName = cb.name; }
                }
            lastFwd = ViewForward;
            if (roamFired) { Attack = false; roamFired = false; }
            if (roamAds) { Ads = false; roamAds = false; }
            try { s.Tick?.Invoke(t); } catch (Exception e) { OnLog($"[pilot] step tick {s.Name}: {e.Message}", "", LogType.Exception); }
            if (s.Roam) try { Roam(body); } catch (Exception e) { OnLog($"[pilot] roam: {e.Message}", "", LogType.Exception); }
            while (shotIndexStep < s.Shots.Length && t >= s.Shots[shotIndexStep])
            {
                Shot($"{s.Name}_{shotIndexStep}");
                shotIndexStep++;
            }
            if (t >= s.Duration || stepDone) { stepDone = false; SubLabel = null; Next(); }
            // A step may kill the player on purpose (Tactical Insertion's respawn) until allowDeathUntil.
            if ((body == null || body.healthComponent == null || !body.healthComponent.alive) && Time.unscaledTime >= allowDeathUntil) Finish("player died");
        }

        /// The loadout panel's hierarchy (components per node) for building MW2 rows into it.
        static void DumpLoadoutUi()
        {
            var sb = new System.Text.StringBuilder();
            void Walk(Transform t, int depth)
            {
                if (depth > 9) return;
                var comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));
                var rt = t as RectTransform;
                string size = rt != null ? $" {rt.rect.width:F0}x{rt.rect.height:F0}" : "";
                var tmp = t.GetComponent<TMPro.TMP_Text>();
                sb.AppendLine($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}{size} [{comps}]{(tmp != null ? " \"" + tmp.text + "\"" : "")}");
                for (int i = 0; i < t.childCount && i < 40; i++) Walk(t.GetChild(i), depth + 1);
            }
            foreach (var lp in UnityEngine.Object.FindObjectsOfType<RoR2.UI.LoadoutPanelController>(true)) Walk(lp.transform, 0);
            var csc = UnityEngine.Object.FindObjectOfType<RoR2.UI.CharacterSelectController>();
            if (csc != null) { sb.AppendLine("--- CharacterSelectController"); Walk(csc.transform, 0); }
            File.WriteAllText(Path.Combine(outDir, "loadout_ui.txt"), sb.ToString());
            Plugin.Log.LogInfo($"[pilot] loadout UI dumped ({sb.Length} chars)");
        }

        static string SkillState(GenericSkill s)
        {
            if (s == null) return "(no skill)";
            var m = s.stateMachine;
            return $"[{s.skillDef?.skillName} stock={s.stock}/{s.maxStock} cd={s.cooldownRemaining:F1} ready={s.IsReady()} esm={(m != null ? m.customName : "none")} enabled={(m != null && m.enabled)} state={m?.state?.GetType().Name} pending={(m != null && m.HasPendingState())}]";
        }

        static (int w, int h, FullScreenMode mode)? resWas;

        /// "res=1920x1080": the run plays in a window of that size (a friend's screen: HUD layout
        /// checks), put back in Finish. Screen only - RoR2's saved resolution setting is not touched.
        static void ForceResolution()
        {
            var m = System.Text.RegularExpressions.Regex.Match(Setting("res", "") ?? "", @"^(\d+)x(\d+)$");
            if (!m.Success) return;
            resWas = (Screen.width, Screen.height, Screen.fullScreenMode);
            Screen.SetResolution(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), FullScreenMode.Windowed);
            Plugin.Log.LogInfo($"[pilot] resolution {m.Value} windowed (was {resWas.Value.w}x{resWas.Value.h} {resWas.Value.mode})");
        }

        static void Finish(string why)
        {
            if (phase == Phase.Quit) return;
            ClearInput();
            if (resWas.HasValue) { Screen.SetResolution(resWas.Value.w, resWas.Value.h, resWas.Value.mode); resWas = null; }
            // Put the player's skin pick back.
            var lu = LocalUserManager.GetFirstLocalUser();
            var bi = BodyCatalog.FindBodyIndex(Mw2Survivor.BodyName);
            if (restoreSkin.HasValue && lu?.userProfile != null && bi != BodyIndex.None)
            {
                var lo = new Loadout();
                lu.userProfile.CopyLoadout(lo);
                lo.bodyLoadoutManager.SetSkinIndex(bi, restoreSkin.Value);
                lu.userProfile.SetLoadout(lo);
                restoreSkin = null;
            }
            Active = false;
            Mw2Recorder.Stop();
            try
            {
                var lines = new List<string> { $"result: {why}", $"steps: {Math.Max(0, stepIndex)}/{steps.Count}", $"errors: {errors.Values.Sum()} ({errors.Count} distinct)" };
                lines.AddRange(errors.OrderByDescending(kv => kv.Value).Take(40).Select(kv => $"  x{kv.Value}  {kv.Key}"));
                File.WriteAllLines(Path.Combine(outDir, "summary.txt"), lines);
                metrics?.Dispose();
            }
            catch { }
            Plugin.Log.LogInfo($"[pilot] playtest finished: {why}");
            Time.timeScale = 1f;
            phase = Phase.Quit; phaseTimer = 0f;
        }

        // ------------------------------------------------------------ the script

        static void Add(string name, float secs, Action begin = null, Action<float> tick = null, float[] shots = null, Action end = null)
        {
            if (filter.Length > 0 && filter != "all" && !filter.Split(',').Any(f => name.StartsWith(f.Trim()))) return;
            steps.Add(new Step { Name = name, Duration = secs, Begin = begin, Tick = tick, Shots = shots ?? new float[0], End = end });
        }

        /// The showcase's clip length: every gun the same (raise, bursts, reload at 4.6 s).
        const float CineClip = 8.5f;
        /// MW2's own reload length runs past the shared 8.5 s on the belt-fed LMGs and the SA80 (no
        /// Sleight of Hand in the reel): their clips run on until the reload is through. The timeline
        /// itself (raise 0, ADS 2.2, reload 4.6) is the same for every gun.
        static float CineLength(string gun) =>
            gun == "rpd_mp" || gun == "mg4_mp" ? 15.4f : gun == "m240_mp" ? 13.4f : gun == "sa80_mp" ? 10.6f : CineClip;
        static List<Mw2Cinema.Vista> vistas = new List<Mw2Cinema.Vista>();
        static Mw2Cinema.Vista? vista;
        static int scoutShown = -1;

        /// Stand on the vista and look where it looks (Look re-aimed against the current view).
        static void Park(Mw2Cinema.Vista v)
        {
            var b = bridge.LocalBody;
            if (b == null) return;
            bridge.TeleportTo(v.feet + Vector3.up * 0.15f);
            AimAt(v.yaw, v.pitch);
        }

        /// Between clips: back on the spot and the framing if anything moved it (recoil, a throw).
        static void Repark()
        {
            if (vista == null) return;
            var b = bridge.LocalBody;
            if (b != null && Vector3.Distance(b.footPosition, vista.Value.feet) > 0.6f) bridge.TeleportTo(vista.Value.feet + Vector3.up * 0.15f);
            AimAt(vista.Value.yaw, vista.Value.pitch);
        }

        /// Every frame of a clip: the framing back on the vista (a mouse moved over the game window
        /// turned the camera to the sky mid-recording, 10-05-26). MW2's sway still lands on top.
        static void HoldFrame()
        {
            if (vista != null) AimAt(vista.Value.yaw, vista.Value.pitch);
        }

        /// Turn the pilot's view to a world yaw (degrees, Unity: 0 = +z) and pitch (+ down).
        static void AimAt(float yaw, float pitch)
        {
            var f = ViewForward;
            float curYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float curPitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            Look.y += Mathf.DeltaAngle(yaw, curYaw);
            Look.x += pitch - curPitch;
        }

        /// The cameraman on the neutral team (playtest 10-05-26: "the camerman pilot shouldn't be
        /// targetable"): RoR2's AI looks for enemies in TeamMask.allButNeutral (BaseAI.FindEnemyHurtBox),
        /// so neither the monsters nor the survivor fighting them ever pick him.
        static void Untargetable(CharacterBody b)
        {
            if (b != null && b.teamComponent != null) b.teamComponent.teamIndex = TeamIndex.Neutral;
        }

        /// The animatic's match: monsters roam the shot (walked by the pilot - RoR2's AI with nobody
        /// near to fight stands still), and fight with their own AI once the survivor comes within
        /// 35 m; the dead are replaced from just past the frame's edges; the survivor is kept near.
        sealed class Roamer { public CharacterBody body; public bool flyer; public Vector3 goal; public float until, restUntil; public bool aiOff; }
        static readonly List<Roamer> roamers = new List<Roamer>();
        static readonly System.Random roamRng = new System.Random(11);
        static CharacterBody commando; // whoever the monsters fight: the animatic's MW2 soldier
        static Mw2Bot bot;
        /// After the nuke: nobody new walks in (the soldier's death plays out, then he stays down).
        static bool nuked;
        static int botClass;
        static float nextSpawn;
        static readonly (string master, bool flyer, int weight)[] castTable =
        {
            ("LemurianMaster", false, 3), ("BeetleMaster", false, 3), ("BeetleGuardMaster", false, 1), ("GolemMaster", false, 1),
            ("ImpMaster", false, 1), ("WispMaster", true, 2), ("JellyfishMaster", true, 1),
        };

        /// The take's depth (playtest 10-07-26, on the ledge at Shattered Abodes: "because our framing is
        /// different, we can put the enemies and our display of background stuff a bit further back"):
        /// every distance the crowd, the soldier and the Teleporter use, scaled (pilot "depth=").
        static float Depth = 1f;

        /// Whether the take's camera shows a spot on the ground (a body standing there): inside the
        /// frame's height (16:9 at the camera's field of view), in front, and not hidden behind the
        /// ground in between (the ledge's lip hid the near field, 10-07-26). `wide`: past the frame's
        /// sides counts too (walk-ins).
        static bool Shown(Vector3 feet, bool wide)
        {
            if (vista == null) return true;
            var eye = vista.Value.feet + Vector3.up * 1.7f;
            var head = feet + Vector3.up * 1.2f;
            var local = Quaternion.Inverse(Quaternion.Euler(vista.Value.pitch, vista.Value.yaw, 0f)) * (head - eye);
            if (local.z < 2f) return false;
            float tanV = Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad) * Mw2View.WorldWiden;
            float y = local.y / (local.z * tanV), x = local.x / (local.z * tanV * 16f / 9f);
            if (y < -0.85f || y > 0.85f) return false;
            if (!wide && (x < -0.92f || x > 0.92f)) return false;
            return !Physics.Linecast(eye, head, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
        }

        /// One monster of the cast: across the shot (edge 0) or just past its sides (edge 1).
        static bool SpawnCast(float edge)
        {
            if (vista == null) return false;
            var eye = vista.Value.feet + Vector3.up * 1.7f;
            int total = 0; foreach (var c in castTable) total += c.weight;
            int pick = roamRng.Next(total);
            var who = castTable[0];
            foreach (var c in castTable) { if (pick < c.weight) { who = c; break; } pick -= c.weight; }
            var prefab = MasterCatalog.FindMasterPrefab(who.master);
            if (prefab == null) return false;
            float side = roamRng.NextDouble() < 0.5 ? -1f : 1f;
            Vector3 dir = Vector3.forward, at = eye;
            // Somewhere the camera shows (or just past its sides), at the take's depth.
            for (int tries = 0; tries < 16; tries++)
            {
                float yaw = edge > 0f ? side * (46f + (float)roamRng.NextDouble() * 18f) : (float)(roamRng.NextDouble() * 100.0 - 50.0);
                float dist = (15f + (float)roamRng.NextDouble() * 40f) * Depth;
                dir = Quaternion.Euler(0f, vista.Value.yaw + yaw, 0f) * Vector3.forward;
                at = Mw2Strike.Ground(eye + dir * dist + Vector3.up * 3f);
                if (Shown(at, edge > 0f)) break;
            }
            at += Vector3.up * (who.flyer ? 7f + (float)roamRng.NextDouble() * 7f : 0.3f);
            var m = new MasterSummon { masterPrefab = prefab, position = at, rotation = Quaternion.LookRotation(-dir), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
            if (m == null || m.GetBody() == null) return false;
            roamers.Add(new Roamer { body = m.GetBody(), flyer = who.flyer });
            return true;
        }

        static void BattleTick()
        {
            if (vista == null) return;
            var eye = vista.Value.feet + Vector3.up * 1.7f;
            var fwd = Quaternion.Euler(0f, vista.Value.yaw, 0f) * Vector3.forward;
            float now = Time.time;
            roamers.RemoveAll(r => r.body == null || r.body.healthComponent == null || !r.body.healthComponent.alive);
            // Keep the crowd up: the dead replaced from past the frame's edges, so they walk in.
            if (roamers.Count < 9 && now >= nextSpawn && !nuked) { SpawnCast(1f); nextSpawn = now + 2.5f; }
            // The MW2 soldier plays his match (he keeps himself in the shot); if his body is ever gone
            // he's back from the side of the shot rather than missing from the rest of the take.
            // He's mortal (sturdy): down, his death plays out, then another soldier - another skin -
            // runs in from a side of the shot.
            if (bot != null && bot.Finished && !nuked)
            {
                bot.Destroy();
                float side = roamRng.NextDouble() < 0.5 ? -1f : 1f;
                string skin = null;
                for (int tries = 0; tries < 20 && Mw2Skins.Catalog.Count > 0; tries++)
                {
                    int ix = roamRng.Next(Mw2Skins.Catalog.Count);
                    if (Mw2Skins.Catalog[ix].cls == "riot") continue; // the riot bodies didn't build
                    skin = Mw2Skins.Key(1 + ix, roamRng.Next(8));
                    break;
                }
                Plugin.Log.LogInfo($"[bot] the MW2 soldier went down - {skin ?? "another"} in from the side");
                // From out of frame past a side, out on the field, into the shot (playtest 10-07-26: "increase
                // the spawn area for where the background player comes in" - from behind the camera they
                // came past it and over the drop in front of the ledge).
                var way = Mw2Bot.EntryPath(eye, fwd, side, roamRng);
                Plugin.Log.LogInfo($"[bot] next soldier from {way[0]} via {way[1]} to {way[2]}");
                botClass++;
                bot = Mw2Bot.Spawn(way[0] + Vector3.up * 0.3f, fwd, skin, botClass, new[] { way[1], way[2] });
                // airstrike (two jets, once a take - "tune it down a tad", 10-05-26), the Attack Helicopter over
                // the second (playtest 10-07-26; he lives longer for it), the Predator, then the sentry with the
                // fourth, who plays to the end (a dead owner's sentry goes with him)
                if (bot != null) { bot.StreakKind = (botClass + 1) % 4; bot.DieAt = botClass >= 3 ? 0f : bot.StreakKind == 2 ? 40f : 32f; }
                commando = bot != null ? bot.Body : null;
            }
            bot?.Tick(eye, fwd);
            // RoR2's AI turns on whoever hurt it whatever the team: the cameraman's rounds (the hip /
            // ADS acts) had Beetle Guards coming for the camera (10-05-26). Never him: back to the fight.
            var me = bridge.LocalBody;
            if (me != null)
                foreach (var r in roamers)
                {
                    if (r.body == null || r.body.master == null) continue;
                    foreach (var ai in r.body.master.GetComponents<RoR2.CharacterAI.BaseAI>())
                        if (ai.currentEnemy != null && ai.currentEnemy.gameObject == me.gameObject) ai.currentEnemy.Reset();
                }
            foreach (var r in roamers)
            {
                var b = r.body;
                if (b.inputBank == null || b.master == null) continue;
                bool fight = commando != null && Vector3.Distance(commando.corePosition, b.corePosition) < (r.aiOff ? 35f : 45f);
                if (fight == r.aiOff)
                {
                    foreach (var ai in b.master.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = fight;
                    r.aiOff = !fight;
                }
                if (!r.aiOff) continue;
                var to = r.goal - b.corePosition;
                if (!r.flyer) to.y = 0f;
                if (r.goal == Vector3.zero || to.magnitude < 2f || now > r.until)
                {
                    if (r.goal != Vector3.zero && roamRng.NextDouble() < 0.35) r.restUntil = now + 0.8f + (float)roamRng.NextDouble() * 1.6f;
                    var g = Vector3.zero;
                    for (int tries = 0; tries < 8; tries++)
                    {
                        float yaw = vista.Value.yaw + (float)(roamRng.NextDouble() * 120.0 - 60.0);
                        float dist = (12f + (float)roamRng.NextDouble() * 45f) * Depth;
                        g = Mw2Strike.Ground(eye + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * dist + Vector3.up * 3f);
                        if (Shown(g, true)) break;
                    }
                    r.goal = r.flyer ? g + Vector3.up * (6f + (float)roamRng.NextDouble() * 8f) : g;
                    r.until = now + 9f;
                    to = r.goal - b.corePosition;
                    if (!r.flyer) to.y = 0f;
                }
                var dir = to.sqrMagnitude > 1e-4f ? to.normalized : b.transform.forward;
                b.inputBank.moveVector = now < r.restUntil ? Vector3.zero : dir;
                b.inputBank.aimDirection = dir;
            }
        }

        /// Animatic diagnostics: where the monsters are, and which ones the camera sees.
        static void LogCast()
        {
            var cam = Camera.main;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb == bridge.LocalBody || cb.teamComponent == null || cb.teamComponent.teamIndex == TeamIndex.Neutral) continue;
                var vp = cam != null ? cam.WorldToViewportPoint(cb.corePosition) : Vector3.zero;
                bool seen = vp.z > 0f && vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f;
                Plugin.Log.LogInfo($"[pilot] cast: {cb.name} at {cb.corePosition} alive {cb.healthComponent != null && cb.healthComponent.alive} {(seen ? $"IN FRAME ({vp.x:F2},{vp.y:F2})" : "off frame")}");
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        // The inspect (MW2 has none; playtest 10-06-26: longer, deeper to the side, less smooth - "still feel
        // like it's in MW2" - and weapon checks with the gun's own moving parts). MW2's hands move in
        // quick snaps that settle (a little overshoot), then hold with a breath of sway. The check is MW2's
        // own clip cut short, so the left hand really works the part: FIRST_RAISE 35% -> 52% is the hand
        // on the charging handle, the bolt ~3 in back (PARTS diagnostic, masada_eotech_silencer_mp). It
        // runs at a light cant: done rolled to the side, the left arm swept across the lens. (No mag
        // check: the ACR's reload slaps the mag out - there's no frame of it held half out.)
        // Poses: Euler (pitch, yaw, roll) and shift (x / y toward the centre as a fraction of the gun's
        // offset, z pushed out as a fraction of its distance).
        // (the side at -40 / -32 swung the left sleeve across the lens on the way in, 10-06-26)
        // (playtest 10-07-26: it went too far left, and the stock clipped into the camera: less yaw and
        // centre pull, pushed out further)
        // (10-07-26, pilot "inspect_sweep": lifted less - 0.1 -> 0.04, port 0.08 -> 0.02, top 0.35 -> 0.2 -
        // the stock's cut-off end stays out of the frame the whole inspect: 775 samples of it on screen
        // before, 0 after)
        static Vector3 InspectSideE = new Vector3(-10f, -22f, -20f), InspectSideS = new Vector3(0.08f, 0.04f, 0.65f);
        static readonly Vector3 InspectCantE = new Vector3(-5f, -8f, -12f), InspectCantS = new Vector3(0.12f, 0.05f, 0.2f);
        // (about the chest at 24 / 42 the stock swung across the frame, 10-06-26)
        static Vector3 InspectPortE = new Vector3(-8f, 14f, 18f), InspectPortS = new Vector3(0.15f, 0.02f, 0.5f);
        // Muzzle dipped, its top turned up to the camera (the rack seen from above).
        // (playtest 10-07-26: the stock's open inside on the rack - its unmodelled right side shows; the
        // viewmodel's inner faces fill it now (Mw2Viewmodel.TwoSided). Out a little and nudged right:
        // the reaching arm's cut-off shoulder end came in at the right edge; turned further left (-12)
        // it came in more.)
        // (10-07-26: lifted less, 0.35 -> 0.2 - the turn to it showed the stock's cut-off end; the rack's
        // hand coming early hid it but read as the hand getting ready too soon. Then "i could see
        // through the guns buttstock" for 12 frames turning in: tipped 18 nose-down, the eye saw into
        // MW2's open stock shell until the rack's hand moved the gun - 155 rays of a 96x54 grid; pilot
        // "inspect_sweep": tipped 10 and canted 24, 2 at most, as at rest.)
        static Vector3 InspectTopE = new Vector3(10f, -4f, -24f), InspectTopS = new Vector3(-0.1f, 0.2f, 0.5f);
        static Vector3 InspectMidE = InspectPortE, InspectMidS = InspectPortS;
        /// Seconds the rack's hand comes off the grip before the rack itself, held at its start while the
        /// gun turns to the top (the stock's cut-off end showed in that turn until the hand took the gun,
        /// 10-07-26).
        static float RackLead = 0f;
        const float InspectLength = 7.5f; // inside the nuke's 10 s countdown // (6.8 switched poses too fast, playtest 10-06-26: hold longer, sway)

        /// MW2-style snap: fast out, a touch past, settle.
        static float Snap(float u)
        {
            u = Mathf.Clamp01(u);
            const float c = 1.15f; // overshoot
            float v = u - 1f;
            return 1f + (c + 1f) * v * v * v + c * v * v;
        }

        /// One of MW2's clips held at `frac` (a check), or let go (-1).
        static void Check(int slot, float from, float to, float t, float tOut, float hold, float tBack)
        {
            if (t < 0f || t > tOut + hold + tBack) { Mw2Viewmodel.PilotSlot = -1; return; }
            float f = t < tOut ? Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / tOut))
                : t < tOut + hold ? to
                : Mathf.Lerp(to, from, Mathf.SmoothStep(0f, 1f, (t - tOut - hold) / tBack));
            Mw2Viewmodel.PilotSlot = slot;
            Mw2Viewmodel.PilotFrac = f;
        }

        /// The rig at time `t` of the inspect; after the end, the plain pose and the hands let go.
        /// One continuous spline through the poses (playtest 10-06-26: blend the speeds, natural timing):
        /// each pose is two keys - arrive, and a slow drift on - so the hand eases out of one look,
        /// never quite stops, and picks up speed into the next (Catmull-Rom, still at both ends).
        static void Inspect(float t)
        {
            var keys = new (float t, Vector3 e, Vector3 s)[]
            {
                (0.0f, Vector3.zero, Vector3.zero),
                (0.85f, InspectSideE, InspectSideS),                    // its left side, deep
                (1.9f, InspectSideE * 1.07f + new Vector3(2f, 0f, 0f), InspectSideS),
                (2.75f, InspectMidE, InspectMidS),                      // the middle look (was the right side, canted)
                (3.7f, InspectMidE * 1.05f + new Vector3(-1.5f, 0f, 0f), InspectMidS),
                (4.5f, InspectTopE, InspectTopS),                       // top-down: the rack
                (6.55f, InspectTopE + new Vector3(1.5f, -1f, 1f), InspectTopS),
                (7.4f, Vector3.zero, Vector3.zero),                      // back to rest
            };
            Vector3 e = Vector3.zero, sh = Vector3.zero;
            if (t > 0f && t < keys[keys.Length - 1].t)
            {
                int i = 0;
                while (i + 2 < keys.Length && t >= keys[i + 1].t) i++;
                var k0 = keys[i]; var k1 = keys[i + 1];
                float dt = k1.t - k0.t, u = (t - k0.t) / dt;
                // Tangents from the neighbours (non-uniform Catmull-Rom); zero at the ends.
                Vector3 Tan(int j, bool euler)
                {
                    if (j <= 0 || j >= keys.Length - 1) return Vector3.zero;
                    var a = euler ? keys[j - 1].e : keys[j - 1].s;
                    var c = euler ? keys[j + 1].e : keys[j + 1].s;
                    return (c - a) / (keys[j + 1].t - keys[j - 1].t);
                }
                Vector3 Herm(Vector3 p0, Vector3 p1, Vector3 m0, Vector3 m1)
                {
                    float u2 = u * u, u3 = u2 * u;
                    return (2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * dt * m0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * dt * m1;
                }
                e = Herm(k0.e, k1.e, Tan(i, true), Tan(i + 1, true));
                sh = Herm(k0.s, k1.s, Tan(i, false), Tan(i + 1, false));
                // The hands sway while holding it up (MW2's idle is never still), faded in and out.
                float end = keys[keys.Length - 1].t;
                float swayW = Mathf.SmoothStep(0f, 1f, t / 1.0f) * Mathf.SmoothStep(0f, 1f, (end - t) / 1.0f);
                e += swayW * new Vector3(Mathf.Sin(t * 1.4f) * 1.4f, Mathf.Sin(t * 0.9f + 1f) * 1.8f, Mathf.Sin(t * 1.1f + 2f) * 1.2f);
            }
            Mw2Viewmodel.InspectEuler = e;
            Mw2Viewmodel.InspectShift = sh;
            // The chamber check while canted: MW2's first-time draw from the right hand leaving the grip
            // (22%) through the rack and back (78%), at ~0.4x speed, eased in and out (it snapped from
            // the grip into the middle of the rack and ran at full draw speed - playtest 10-06-26).
            // Timed like a hand: the reach and the pull slow, a beat with it back, the let-go a snap
            // (bolt home in ~4 frames), the hand back to the grip (playtest 10-06-26). In the clip the bolt
            // is back at 50-55% and home by 65%.
            const float RackFrom = 4.55f;
            var rack = new (float t, float f)[] { (0f, 0.22f), (1.0f, 0.40f), (1.65f, 0.53f), (1.85f, 0.53f), (1.92f, 0.65f), (2.4f, 0.78f) };
            float rt = t - RackFrom;
            // The rack's sound on the snap (playtest 10-07-26: "no sound effects are playing when the bolt
            // racks"): MW2's chamber sound is one sample - pull and slam ~0.28 s apart at MW2's speed - and
            // its notetrack (42% of the draw) fired in the slow pull, the slam half a second before the
            // bolt went home. Held back, and played so its slam lands on the snap (1.92).
            Mw2Viewmodel.SkipAlias = rt >= -0.5f - RackLead && rt < 3f ? RackAlias : null;
            if (rt < -RackLead) rackSounded = false;
            if (!rackSounded && rt >= 1.92f - 0.28f) { rackSounded = true; bridge.PilotPlayAlias(RackAlias); }
            if (rt >= -RackLead && rt < 0f)
            {
                Mw2Viewmodel.PilotGlide = 0.35f;
                Mw2Viewmodel.PilotSlot = 14;
                Mw2Viewmodel.PilotFrac = rack[0].f;
            }
            else if (rt >= 0f && rt < rack[rack.Length - 1].t)
            {
                int j = 0;
                while (j + 2 < rack.Length && rt >= rack[j + 1].t) j++;
                float u = (rt - rack[j].t) / (rack[j + 1].t - rack[j].t);
                bool snap = rack[j + 1].f - rack[j].f > 0.1f && rack[j + 1].t - rack[j].t < 0.1f;
                u = snap ? u : Mathf.SmoothStep(0f, 1f, u);
                Mw2Viewmodel.PilotGlide = 0.35f;
                Mw2Viewmodel.PilotSlot = 14;
                Mw2Viewmodel.PilotFrac = Mathf.Lerp(rack[j].f, rack[j + 1].f, u);
            }
            else Mw2Viewmodel.PilotSlot = -1;
        }

        const string RackAlias = "weap_masada_chamber_plr";
        static bool rackSounded;

        /// A rifle with an underbarrel grenade launcher or shotgun.
        static bool Underbarrel(string w) => w.EndsWith("_gl_mp") || w.EndsWith("_shotgun_mp");

        /// The nearest walkable ground node to a point (RoR2's own nav graph), else the ground under it.
        internal static Vector3 OnNodes(Vector3 p)
        {
            var g = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
            if (g != null)
            {
                var n = g.FindClosestNode(p, HullClassification.Human, 30f);
                if (n != RoR2.Navigation.NodeGraph.NodeIndex.invalid && g.GetNodePosition(n, out var at)) return at;
            }
            return Mw2Strike.Ground(p + Vector3.up * 3f);
        }

        static void Turn(float yawDegPerSec, float pitchDegPerSec = 0f)
        {
            Look += new Vector3(pitchDegPerSec, yawDegPerSec, 0f) * Time.unscaledDeltaTime;
        }

        static void Build()
        {
            steps.Clear();
            // Out of the drop pod first, the way a player does (E), then MW2 mode on.
            Add("exit_pod", 2.5f, () =>
            {
                // What E does in the drop pod: the seat ejects its passenger.
                var body = bridge.LocalBody;
                var seat = body != null ? body.currentVehicle : null;
                if (seat != null) seat.EjectPassenger(body.gameObject);
                Write($"{{\"pod\":\"{(seat != null ? "ejected" : "not in pod")}\"}}");
            }, null, new[] { 2.2f });
            Add("setup", 3f, () =>
            {
                var body = bridge.LocalBody;
                if (!bridge.Active) bridge.Toggle(body);
                bridge.Admin.God = true;
                bridge.Admin.InfiniteAmmo = true;
                if (reel) { SetConVar("director_combat_disable", "1"); Mw2Admin.KillMonsters(); }
                bridge.GiveWeapon("ak47_mp");
                hasHome = false;
            }, null, new[] { 2.5f });
            if (steps.Count > 0 && steps[steps.Count - 1].Name == "setup") steps[steps.Count - 1].Roam = true;
            // Focused animation checks (playtest: Intervention and SPAS-12 looked wrong).
            Add("anim_spas_hip", 2.4f, () => bridge.GiveWeapon("spas12_mp"), null, new[] { 2.2f });
            Add("anim_spas_fire", 1.4f, null, t => Attack = t < 0.1f, new[] { 0.12f, 0.45f, 0.8f, 1.2f });
            Add("anim_spas_ads_fire", 2.2f, () => Ads = true, t => Attack = t > 1.0f && t < 1.1f, new[] { 0.9f, 1.15f, 1.5f, 1.9f });
            Add("anim_spas_reload", 5.5f, null, t => Reload = t < 0.2f, new[] { 0.5f, 1.4f, 2.2f, 3.0f, 4.6f });
            Add("anim_cheytac_hip", 2.4f, () => bridge.GiveWeapon("cheytac_mp"), null, new[] { 2.2f });
            Add("anim_cheytac_fire", 1.6f, null, t => Attack = t < 0.1f, new[] { 0.1f, 0.35f, 0.7f, 1.1f });
            Add("anim_cheytac_reload", 4.6f, null, t => Reload = t < 0.2f, new[] { 0.6f, 1.6f, 2.6f, 3.8f });
            Add("switch_m4", 1.6f, () => bridge.GiveWeapon("m4_mp"), null, new[] { 1.4f });
            Add("switch_ak", 1.6f, () => bridge.GiveWeapon("ak47_mp"), null, new[] { 1.4f });
            Add("look_around", 3f, null, t => Turn(t < 1.5f ? 60f : -60f), new[] { 0.8f, 2.2f });
            Add("walk", 3f, () => { Moving = true; Fwd = 1f; }, null, new[] { 2f });
            Add("sprint", 3f, () => { Moving = true; Fwd = 1f; Sprint = true; }, null, new[] { 2f });
            Add("jump", 1.6f, null, t => Jump = t < 0.15f, new[] { 0.35f });
            Add("hip_fire", 2f, () => Attack = true, null, new[] { 0.25f, 1.2f });
            Add("ads", 1.6f, () => Ads = true, null, new[] { 1.3f });
            Add("ads_fire", 1.6f, () => { Ads = true; Attack = true; }, null, new[] { 1.1f });
            Add("reload", 3.2f, null, t => Reload = t < 0.25f, new[] { 0.7f, 1.6f });
            Add("sprint_anim", 2.5f, () => { Moving = true; Fwd = 1f; Sprint = true; }, null, new[] { 1.8f });
            foreach (var gun in new[] { "m4_mp", "spas12_mp", "deserteagle_mp", "rpd_mp", "barrett_mp" })
            {
                string g = gun;
                Add($"gun_{g}_hip", 2.2f, () => bridge.GiveWeapon(g), null, new[] { 2.0f });
                Add($"gun_{g}_ads", 1.8f, () => Ads = true, null, new[] { 1.5f });
                Add($"gun_{g}_fire", 1.2f, () => { Ads = true; Attack = true; }, null, new[] { 0.6f });
            }
            Add("scope_cheytac", 2.2f, () => bridge.GiveWeapon("cheytac_mp"), null, new[] { 2.0f });
            Add("scope_sway", 3f, () => Ads = true, null, new[] { 1.5f, 2.6f });
            Add("scope_breath", 3.5f, () => { Ads = true; Sprint = true; }, null, new[] { 2.8f });
            Add("scope_gasp", 3f, () => { Ads = true; Sprint = true; }, null, new[] { 2.0f });
            Add("back_to_ak", 2f, () => bridge.GiveWeapon("ak47_mp"), null, new[] { 1.8f });

            // Close-ups of the aircraft models (rotors, see-through surfaces) parked in front of the camera.
            foreach (var m in new[] { "vehicle_cobra_helicopter_fly_low", "vehicle_pavelow", "vehicle_apache_mp", "vehicle_little_bird_armed", "vehicle_av8b_harrier_jet_mp" })
            {
                string model = m;
                Mw2Vehicle shown = null;
                Add($"model_{model}", 3.5f, () =>
                {
                    var body = bridge.LocalBody;
                    var fwd = ViewForward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                    var at = body.corePosition + fwd * 22f + Vector3.up * 5f;
                    shown = Mw2Vehicle.Spawn(model, body, at, new Vector3(fwd.z, 0f, -fwd.x));
                }, t => { if (shown != null) shown.Place(shown.Position, shown.Forward); }, new[] { 1.2f, 3.0f }, () => { shown?.Destroy(); shown = null; });
            }
            // Stand still and let monsters walk up (personal-space check: enemy_min_m in metrics).
            Add("crowd", 25f, () => Mw2Admin.SpawnRing(bridge.LocalBody), null, new[] { 8f, 14f, 20f, 24f });
            // Take damage for real (blood, breathing, damage arrows); god mode back on below 45%.
            Add("hurt", 20f, () => { bridge.Admin.God = false; Mw2Admin.SpawnRing(bridge.LocalBody); }, t =>
            {
                var b = bridge.LocalBody;
                if (b != null && b.healthComponent != null && b.healthComponent.combinedHealthFraction < 0.45f) bridge.Admin.God = true; // one big hit can take 40%
            }, new[] { 6f, 9f, 12f, 15f, 19f }, () => bridge.Admin.God = true);
            // An earned, unused killstreak on MW2's d-pad (dpad_hd action slot 4).
            Add("dpad", 3f, () => bridge.Streaks.AdminGive(Native.StreakId("predator_missile")), null, new[] { 1.0f, 2.5f }, () => bridge.Streaks.EndAllForTest());
            // MW2 equipment and launchers (roadmap #4): cook a frag 1 s and throw, a flashbang, RPG, Thumper.
            Add("equip_frag", 6f, () => Look += new Vector3(-8f, 0f, 0f), t => Frag = t < 1.2f, new[] { 0.6f, 1.28f, 1.36f, 1.5f, 2.4f, 4.6f }, () => Look -= new Vector3(-8f, 0f, 0f));
            Add("equip_flash", 4f, () => Look += new Vector3(8f, 0f, 0f), t => Smoke = t < 0.15f, new[] { 0.7f, 1.6f, 2.2f, 3.2f }, () => Look -= new Vector3(8f, 0f, 0f));
            // MW2 knife (roadmap #5): a lemurian ~2.6 m ahead (inside the 128-unit lunge), stab, then a plain swing at air.
            Add("melee", 6f, () => { Mw2Admin.SpawnAhead(bridge.LocalBody, "LemurianMaster", 2.6f); },
                t => { FaceNearestEnemy(); Melee = (t > 2.0f && t < 2.1f) || (t > 4.5f && t < 4.6f); }, new[] { 1.9f, 2.1f, 2.3f, 2.6f, 4.7f });
            Add("launcher_rpg", 4f, () => bridge.GiveWeapon("rpg_mp"), t => { Ads = t > 1.4f && t < 3.2f; Attack = t > 2.2f && t < 2.35f; }, new[] { 1.8f, 2.25f, 2.3f, 2.4f, 2.9f, 3.6f });
            // Launchers against a Stone Golem 10 m ahead (playtest 10-04-26: launchers did nowhere near enough):
            // rounds have to strike the body (direct hit) and splash with MW2's falloff.
            // The M79 arms after 375 units (MW2's arming distance, ~19 m here): its golem stands at 25 m.
            // Sniper rifles (playtest 10-04-26: one or two shots) and the throwing knife (it did nothing):
            // the knife goes out as the lethal (Frag).
            foreach (var (gun, label, metres) in new[] { ("rpg_mp", "rpg", 10f), ("m79_mp", "m79", 25f), ("cheytac_mp", "sniper", 15f), ("throwingknife_mp", "knife", 8f) })
            {
                CharacterBody golem = null; float before = 0f; int logged = 0;
                Add($"launcher_hits_{label}", 6f, () =>
                {
                    bridge.Admin.God = true; logged = 0; golem = null;
                    if (label == "knife") bridge.SetLethalForTest(gun); else bridge.GiveWeapon(gun);
                    var b = bridge.LocalBody;
                    var prefab = MasterCatalog.FindMasterPrefab("GolemMaster");
                    // On the line the gun points along, 10 m out (wherever the pilot is looking).
                    var aim = b.inputBank.aimDirection;
                    var on = b.inputBank.aimOrigin + aim * metres;
                    var fwd = Vector3.ProjectOnPlane(aim, Vector3.up).normalized;
                    var m = prefab != null ? new MasterSummon { masterPrefab = prefab, position = Mw2Strike.Ground(on + Vector3.up * 2f) + Vector3.up * 0.3f, rotation = Quaternion.LookRotation(-fwd), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform() : null;
                    golem = m != null ? m.GetBody() : null;
                }, t =>
                {
                    var b = bridge.LocalBody;
                    if (golem == null || b == null) return;
                    if (logged == 0 && t > 2.4f) { logged = 1; before = golem.healthComponent.combinedHealth; Plugin.Log.LogInfo($"[pilot] {label}: golem {before:F0}/{golem.healthComponent.fullCombinedHealth:F0}, player damage {b.damage:F1}"); }
                    if (label == "knife") Frag = t > 2.5f && t < 2.6f; else Attack = t > 2.5f && t < 2.6f;
                    if (logged == 1 && t > 5.5f) { logged = 2; Plugin.Log.LogInfo($"[pilot] {label}: golem took {before - (golem != null && golem.healthComponent != null ? golem.healthComponent.combinedHealth : 0f):F0} ({(golem != null && golem.healthComponent.alive ? "alive" : "dead")})"); }
                }, new[] { 2.4f, 2.8f, 3.2f }, () => { Attack = Frag = false; if (golem != null && golem.healthComponent != null) golem.healthComponent.Suicide(); });
            }
            // Stinger lock-on (_stinger.gsc) on a RoR2 monster (playtest 10-05-26): a golem 30 m out, 2.5 m
            // off the aim line (inside the lock circle; a rocket flown straight misses it). ADS, lock,
            // fire: the rocket has to home in. Then with no target in the circle: no shot at all.
            {
                CharacterBody golem = null; float before = 0f; int logged = 0; int clipAtFire = -1;
                Add("stinger_lock", 9f, () =>
                {
                    bridge.Admin.God = true; logged = 0; golem = null; clipAtFire = -1;
                    bridge.GiveWeapon("stinger_mp");
                    var b = bridge.LocalBody;
                    // 30 m out in the first direction with a clear sightline to the ground there.
                    var eye = b.inputBank.aimOrigin;
                    var aim = b.inputBank.aimDirection;
                    var spot = Mw2Strike.Ground(eye + aim * 30f + Vector3.up * 2f);
                    for (int yawStep = 0; yawStep < 12; yawStep++)
                    {
                        var dir = Quaternion.Euler(0f, yawStep * 30f, 0f) * Vector3.ProjectOnPlane(aim, Vector3.up).normalized;
                        var g = Mw2Strike.Ground(eye + dir * 30f + Vector3.up * 2f);
                        if (!Physics.Linecast(eye, g + Vector3.up * 1.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) { spot = g; aim = dir; break; }
                    }
                    var prefab = MasterCatalog.FindMasterPrefab("GolemMaster");
                    var m = prefab != null ? new MasterSummon { masterPrefab = prefab, position = spot + Vector3.up * 0.3f, rotation = Quaternion.LookRotation(-aim), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform() : null;
                    golem = m != null ? m.GetBody() : null;
                    if (golem != null && golem.GetComponent<RoR2.CharacterMotor>() is var cm && cm != null) cm.enabled = false; // it stays put
                }, t =>
                {
                    var b = bridge.LocalBody;
                    if (golem == null || b == null) return;
                    // Look at it, then 6 degrees to the side: inside MW2's lock circle (~8.5), wide
                    // enough that a straight shot misses.
                    if (t > 0.3f && t < 0.35f && Camera.main != null)
                    {
                        var d = golem.corePosition - Camera.main.transform.position;
                        AimAt(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 6f, -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg);
                    }
                    holdUntil = Time.unscaledTime + 0.1f;
                    Ads = t > 0.8f;
                    if (Mathf.Abs(t - 1.5f) < 0.02f || Mathf.Abs(t - 2.6f) < 0.02f || Mathf.Abs(t - 3.6f) < 0.02f) { var mc = Camera.main; string off = golem != null && mc != null ? Vector3.Angle(mc.transform.forward, golem.corePosition - mc.transform.position).ToString("F1") : "-"; Plugin.Log.LogInfo($"[pilot] stinger_lock: t {t:F1} lock stage {bridge.LockStage} ({bridge.LockWhy}); golem {off} deg off the main camera"); }
                    if (logged == 0 && t > 3.8f) { logged = 1; before = golem.healthComponent.combinedHealth; clipAtFire = bridge.Last.clip; }
                    Attack = t > 4.0f && t < 4.15f;
                    if (logged == 1 && t > 8.0f) { logged = 2; Plugin.Log.LogInfo($"[pilot] stinger_lock: clip {clipAtFire} -> {bridge.Last.clip}, golem took {before - (golem != null && golem.healthComponent != null ? golem.healthComponent.combinedHealth : 0f):F0} of {before:F0} ({(golem != null && golem.healthComponent.alive ? "alive" : "dead")})"); }
                }, new[] { 1.5f, 2.6f, 3.9f, 4.4f, 4.8f }, () => { Attack = Ads = false; if (golem != null && golem.healthComponent != null) golem.healthComponent.Suicide(); });
                // The Javelin: lock a Golem ~40 m out, fire; the top attack climbs over it and dives in
                // (playtest 10-06-26: "ive never seen the javelin lockon launch system tested").
                CharacterBody jGolem = null; float jBefore = 0f, jLaunchY = 0f; int jLogged = 0;
                Add("javelin_lock", 12f, () =>
                {
                    bridge.Admin.God = true; jLogged = 0; jGolem = null;
                    bridge.GiveWeapon("javelin_mp");
                    var b = bridge.LocalBody;
                    var eye = b.inputBank.aimOrigin;
                    var aim = Vector3.ProjectOnPlane(b.inputBank.aimDirection, Vector3.up).normalized;
                    var spot = Mw2Strike.Ground(eye + aim * 40f + Vector3.up * 2f);
                    for (int yawStep = 0; yawStep < 12; yawStep++)
                    {
                        var dir = Quaternion.Euler(0f, yawStep * 30f, 0f) * aim;
                        var g = Mw2Strike.Ground(eye + dir * 40f + Vector3.up * 2f);
                        if (!Physics.Linecast(eye, g + Vector3.up * 1.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) { spot = g; break; }
                    }
                    var prefab = MasterCatalog.FindMasterPrefab("GolemMaster");
                    var m = prefab != null ? new MasterSummon { masterPrefab = prefab, position = spot + Vector3.up * 0.3f, rotation = Quaternion.LookRotation(eye - spot), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform() : null;
                    jGolem = m != null ? m.GetBody() : null;
                    if (jGolem != null && jGolem.GetComponent<RoR2.CharacterMotor>() is var cm && cm != null) cm.enabled = false;
                    if (m != null) foreach (var ai in m.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
                }, t =>
                {
                    var b = bridge.LocalBody;
                    if (b == null) return;
                    if (t < 4.0f && Camera.main != null && jGolem != null) // on it until the shot
                    {
                        var d = jGolem.corePosition - Camera.main.transform.position;
                        AimAt(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg);
                    }
                    holdUntil = Time.unscaledTime + 0.1f;
                    Ads = t > 0.8f && t < 4.6f;
                    foreach (float at in new[] { 1.5f, 2.5f, 3.5f })
                        if (Mathf.Abs(t - at) < 0.02f) Plugin.Log.LogInfo($"[pilot] javelin_lock: t {t:F1} lock stage {bridge.LockStage} ({bridge.LockWhy})");
                    if (jLogged == 0 && t > 3.9f && jGolem != null) { jLogged = 1; jBefore = jGolem.healthComponent.combinedHealth; jLaunchY = b.inputBank.aimOrigin.y; Mw2Projectiles.PilotMaxY = float.MinValue; }
                    Attack = t > 4.0f && t < 4.15f;
                    if (jLogged == 1 && t > 11.0f)
                    {
                        jLogged = 2;
                        bool gone = jGolem == null || jGolem.healthComponent == null;
                        Plugin.Log.LogInfo($"[pilot] javelin_lock: missile peaked {Mw2Projectiles.PilotMaxY - jLaunchY:F0} m over the launch; golem {(gone ? "gone (dead)" : $"took {jBefore - jGolem.healthComponent.combinedHealth:F0} of {jBefore:F0} ({(jGolem.healthComponent.alive ? "alive" : "dead")})")}");
                    }
                }, new[] { 2.5f, 3.8f, 4.6f, 5.3f, 6.0f, 7.0f }, () => { Attack = Ads = false; if (jGolem != null && jGolem.healthComponent != null) jGolem.healthComponent.Suicide(); });
                int clipBefore = -1;
                Add("stinger_nolock", 4f, () => { Mw2Admin.KillMonsters(); bridge.GiveWeapon("stinger_mp"); Look += new Vector3(-30f, 0f, 0f); clipBefore = -1; }, t =>
                {
                    Ads = t > 0.5f;
                    if (clipBefore < 0 && t > 2.4f) clipBefore = bridge.Last.clip;
                    Attack = t > 2.5f && t < 2.65f;
                    if (Mathf.Abs(t - 3.8f) < 0.02f) Plugin.Log.LogInfo($"[pilot] stinger_nolock: lock stage {bridge.LockStage}, clip {clipBefore} -> {bridge.Last.clip} (unchanged = no shot)");
                }, new[] { 2.0f }, () => { Attack = Ads = false; Look -= new Vector3(-30f, 0f, 0f); });
            }
            // Akimbo reloads: both guns part-used, so both reload (10-05-26: the left one never did).
            foreach (var g in new[] { "usp_akimbo_mp", "tmp_akimbo_mp", "model1887_akimbo_mp" })
            {
                string gun = g;
                Add($"akimbo_{gun.Replace("_akimbo_mp", "")}", 6f, () => bridge.GiveWeapon(gun), t =>
                {
                    if (t > 1.4f && t < 1.45f) bridge.PilotSpendClip();
                    Reload = t > 1.6f && t < 1.7f;
                    if (Mathf.Abs(t - 1.55f) < 0.02f || Mathf.Abs(t - 5.8f) < 0.02f) Plugin.Log.LogInfo($"[pilot] {gun}: t {t:F1} clips right {bridge.Last.clip} left {bridge.Last.clipLeft}");
                }, new[] { 1.2f, 2.0f, 2.6f, 3.2f, 4.0f }, () => Reload = false);
            }
            // MW2 attachments (roadmap #6): each combo is its own weapon def. Raise, hip, ADS + a burst.
            foreach (var a in new[] { "acog", "reflex", "eotech", "thermal", "silencer", "gl", "shotgun", "heartbeat", "fmj", "xmags" })
            {
                string w = $"ak47_{a}_mp";
                Add($"attach_{a}", 4.5f, () => bridge.GiveWeapon(w), t => { Ads = t > 2.0f && t < 4.2f; Attack = t > 3.2f && t < 3.6f; }, new[] { 1.6f, 3.0f, 3.4f, 4.1f }, () => bridge.GiveWeapon("ak47_mp"));
            }
            // Underbarrel alternates (MW2 +actionslot 3): on, fire it, back to the rifle, fire that.
            foreach (var a in new[] { "gl", "shotgun" })
            {
                string w = $"ak47_{a}_mp";
                bool on = false, off = false;
                Add($"alt_{a}", 8f, () => { on = off = false; bridge.GiveWeapon(w); Look += new Vector3(-4f, 0f, 0f); }, t =>
                {
                    if (t > 1.5f && !on) { on = true; bridge.ToggleAlternate(); }
                    if (t > 5.0f && !off) { off = true; bridge.ToggleAlternate(); }
                    Attack = (t > 3.0f && t < 3.15f) || (t > 6.6f && t < 6.9f);
                }, new[] { 1.4f, 2.2f, 3.3f, 4.0f, 5.6f, 6.8f }, () => { Look -= new Vector3(-4f, 0f, 0f); bridge.GiveWeapon("ak47_mp"); });
            }
            // HUD: XP bar part-filled (fill only inside the segment cells), and per-weapon MW2 reticles.
            bool xpAlt = false, xpRpg = false;
            Add("hud_xp", 9f, () => { xpAlt = xpRpg = false; bridge.AddXp(700); bridge.GiveWeapon("ak47_gl_mp"); }, t =>
            {
                if (t > 3.0f && !xpAlt) { xpAlt = true; bridge.ToggleAlternate(); }
                if (t > 6.0f && !xpRpg) { xpRpg = true; bridge.GiveWeapon("rpg_mp"); }
            }, new[] { 1.5f, 2.5f, 4.5f, 7.5f }, () => bridge.GiveWeapon("ak47_mp"));
            // RoR2 UI that lands on MW2's minimap (top-left, ~152 of 480 down): every visible graphic
            // whose screen rect overlaps it; plus the XP bar with some XP for the screenshot.
            Add("hudscan", 5f, () =>
            {
                bridge.AddXp(900);
                float mmBottom = Screen.height - 152f * Screen.height / 480f, mmRight = 200f * Screen.height / 480f;
                foreach (var g in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Graphic>())
                {
                    if (g == null || !g.isActiveAndEnabled || g.color.a < 0.05f) continue;
                    var rt = g.rectTransform;
                    var canvas = g.canvas;
                    if (canvas == null) continue;
                    var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    var c = new Vector3[4];
                    rt.GetWorldCorners(c);
                    var a = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
                    var b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
                    if (a.x < mmRight && b.y > mmBottom && b.x > 0f && a.y < Screen.height)
                    {
                        string path = g.name;
                        for (var t = g.transform.parent; t != null && path.Length < 160; t = t.parent) path = t.name + "/" + path;
                        Plugin.Log.LogInfo($"[pilot] over minimap: {path} ({a.x:F0},{a.y:F0})-({b.x:F0},{b.y:F0})");
                    }
                }
                // RoR2's HUD clusters (where its multiplayer ally cards live).
                foreach (var hud in RoR2.UI.HUD.readOnlyInstanceList)
                {
                    var spring = hud != null ? hud.transform.Find("MainContainer/MainUIArea/SpringCanvas") : null;
                    if (spring == null) continue;
                    foreach (Transform child in spring)
                    {
                        var crt = child as RectTransform;
                        if (crt == null) continue;
                        var c = new Vector3[4];
                        crt.GetWorldCorners(c);
                        var canvas = crt.GetComponentInParent<Canvas>();
                        var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                        var a = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
                        var b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
                        var kids = new System.Collections.Generic.List<string>();
                        foreach (Transform k in child) kids.Add(k.name + (k.gameObject.activeSelf ? "" : "(off)"));
                        Plugin.Log.LogInfo($"[pilot] cluster {child.name} ({a.x:F0},{a.y:F0})-({b.x:F0},{b.y:F0}): {string.Join(", ", kids)}");
                    }
                }
            }, null, new[] { 3f });
            // Pro perk challenges: Custom Class 1 (Sleight of Hand, Stopping Power, Steady Aim) shoots a
            // ring of targets from the hip; the challenge counters must move per kill.
            Add("prochallenges", 16f, () =>
            {
                Plugin.Instance.Class.Value = 1;
                bridge.ApplyClass();
                bridge.Admin.God = true;
                bridge.Admin.InfiniteAmmo = true;
                Mw2Admin.SpawnRing(bridge.LocalBody, null, 9f, true);
            }, t =>
            {
                // Kill each target as the player with an MW2 bullet (the path a real shot takes:
                // RoR2's death event -> bridge -> challenges).
                if (t < 3f) return;
                var me = bridge.LocalBody;
                foreach (var cb in CharacterBody.readOnlyInstancesList.ToArray())
                {
                    if (me == null || cb == null || !Mw2Strike.IsEnemy(me, cb) || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                    var hc = cb.healthComponent;
                    Mw2Challenges.With(Mw2Challenges.Cause.Bullet, () => hc.TakeDamage(new DamageInfo { attacker = me.gameObject, inflictor = me.gameObject, damage = 1e6f, position = cb.corePosition, damageType = DamageType.Generic, procCoefficient = 0f }));
                    break;
                }
            }, new[] { 8f, 15f }, () =>
            {
                foreach (var ch in new[] { "ch_sleightofhand_pro", "ch_stoppingpower_pro", "ch_bulletaccuracy_pro", "ch_marksman_m4" })
                    Plugin.Log.LogInfo($"[pilot] challenge {ch}: progress {Mw2Menus.PlayerData("challengeprogress." + ch)} state {Mw2Menus.PlayerData("challengestate." + ch)}");
                bridge.Admin.God = false; bridge.Admin.InfiniteAmmo = false;
            });
            // Kills with nothing else going on (no screenshots): late_frame_max_ms shows a per-kill hitch.
            Add("killhitch", 14f, () =>
            {
                bridge.Admin.God = true;
                bridge.Admin.InfiniteAmmo = true;
                Mw2Admin.SpawnRing(bridge.LocalBody, null, 9f, true);
            }, t =>
            {
                if (t < 4f || (int)(t * 2f) == (int)((t - Time.unscaledDeltaTime) * 2f)) return; // one kill per 0.5 s
                var me = bridge.LocalBody;
                foreach (var cb in CharacterBody.readOnlyInstancesList.ToArray())
                {
                    if (me == null || cb == null || !Mw2Strike.IsEnemy(me, cb) || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                    var hc = cb.healthComponent;
                    Mw2Challenges.With(Mw2Challenges.Cause.Bullet, () => hc.TakeDamage(new DamageInfo { attacker = me.gameObject, inflictor = me.gameObject, damage = 1e6f, position = cb.corePosition, damageType = DamageType.Generic, procCoefficient = 0f }));
                    break;
                }
            }, new float[0], () => { bridge.Admin.God = false; bridge.Admin.InfiniteAmmo = false; });
            // A RoR2 jump pad (its own JumpVolume) under the player: MW2 movement must take the launch.
            float padStartY = 0f, padPeakY = 0f; GameObject pad = null;
            Add("jumppad", 4f, () =>
            {
                var b = bridge.LocalBody;
                pad = new GameObject("MW2 test jump pad");
                pad.transform.position = b.footPosition + Vector3.up * 0.5f;
                var box = pad.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(4f, 2f, 4f);
                var jv = pad.AddComponent<JumpVolume>();
                jv.jumpVelocity = new Vector3(0f, 35f, 0f);
                jv.onJump = (UnityEngine.Events.UnityEvent<CharacterBody>)System.Activator.CreateInstance(typeof(JumpVolume).GetField("onJump").FieldType); // a stage pad has these; RoR2 calls them
                jv.jumpSoundString = "";
                jv.time = 2f;
                padStartY = padPeakY = b.footPosition.y;
            }, t =>
            {
                var b = bridge.LocalBody;
                if (b != null) padPeakY = Mathf.Max(padPeakY, b.footPosition.y);
                if (t > 1.5f && pad != null) { UnityEngine.Object.Destroy(pad); pad = null; }
            }, new[] { 0.6f, 1.2f }, () => Plugin.Log.LogInfo($"[pilot] jumppad: rose {padPeakY - padStartY:F1} m"));
            // A launch pad from playtests (10-04-26, 55 m/s up): the body runs a tick behind the sim, ~0.9 m at that
            // speed, which read as "parted" and cancelled the launch every time.
            int fastParted = 0;
            Add("jumppad_fast", 6f, () =>
            {
                var b = bridge.LocalBody; bridge.Admin.God = true; fastParted = bridge.PartedCount;
                pad = new GameObject("MW2 test jump pad (fast)");
                pad.transform.position = b.footPosition + Vector3.up * 0.5f;
                var box = pad.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(4f, 2f, 4f);
                var jv = pad.AddComponent<JumpVolume>();
                jv.jumpVelocity = new Vector3(-8.01f, 55.33f, -1.36f);
                jv.onJump = (UnityEngine.Events.UnityEvent<CharacterBody>)System.Activator.CreateInstance(typeof(JumpVolume).GetField("onJump").FieldType);
                jv.jumpSoundString = "";
                jv.time = 2f;
                padStartY = padPeakY = b.footPosition.y;
            }, t =>
            {
                var b = bridge.LocalBody;
                if (b != null) padPeakY = Mathf.Max(padPeakY, b.footPosition.y);
                if (t > 1.5f && pad != null) { UnityEngine.Object.Destroy(pad); pad = null; }
            }, new[] { 0.8f, 1.6f }, () => Plugin.Log.LogInfo($"[pilot] jumppad_fast: rose {padPeakY - padStartY:F1} m, parted {bridge.PartedCount - fastParted}"));
            // Jump + both feathers, logged finely (height per press, ground contact, re-seats).
            float jumpBase = 0f, lastJumpLog = 0f;
            Add("jumps", 4f, () => { var b0 = bridge.LocalBody; jumpBase = b0 != null ? b0.footPosition.y : 0f; lastJumpLog = 0f; if (b0 != null && b0.inventory != null && b0.inventory.GetItemCount(RoR2Content.Items.Feather) < 2) b0.inventory.GiveItem(RoR2Content.Items.Feather, 2); }, t =>
            {
                Jump = (t > 0.5f && t < 0.55f) || (t > 0.8f && t < 0.85f) || (t > 1.1f && t < 1.15f);
                var b = bridge.LocalBody;
                if (b == null || t - lastJumpLog < 0.1f) return;
                lastJumpLog = t;
                var m = b.characterMotor;
                Plugin.Log.LogInfo($"[pilot] jumps t {t:F1} up {b.footPosition.y - jumpBase:F2} m simGrounded {bridge.Last.grounded} simVz {bridge.Last.velocity.z:F0} kccStable {(m != null && m.Motor.GroundingStatus.IsStableOnGround)} bodyVy {(m != null ? m.velocity.y.ToString("F1") : "-")}");
            }, new[] { 1.2f }, () => { Jump = false; });
            // Curbs (playtest 10-04-26: stuck on curbs and rocks): walk into a 45 cm step, then a 1.2 m
            // block (jump onto it). The body has to follow the MW2 sim up, without re-seats.
            GameObject curb = null, block = null; float curbBase = 0f, lastCurbLog = 0f;
            Add("curb", 9f, () =>
            {
                var b = bridge.LocalBody; lastCurbLog = 0f;
                var fwd = ViewForward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                curbBase = b.footPosition.y;
                GameObject Box(float h, float dist, float len)
                {
                    var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    g.layer = LayerIndex.world.intVal;
                    g.transform.position = b.footPosition + fwd * (dist + len * 0.5f) + Vector3.up * (h * 0.5f - 0.05f);
                    g.transform.rotation = Quaternion.LookRotation(fwd);
                    g.transform.localScale = new Vector3(4f, h, len);
                    return g;
                }
                curb = Box(0.5f, 2.5f, 3f);
                block = Box(1.3f, 8f, 4f);
            }, t =>
            {
                Moving = t > 0.5f; Fwd = 1f;
                Jump = t > 3.6f && t < 3.7f;
                var b = bridge.LocalBody;
                if (b == null || t - lastCurbLog < 0.25f) return;
                lastCurbLog = t;
                var v = bridge.Last.velocity;
                Plugin.Log.LogInfo($"[pilot] curb t {t:F2} body up {b.footPosition.y - curbBase:F2} m sim up {Space.ToUnity(bridge.Last.origin).y - curbBase:F2} m speed {Mathf.Sqrt(v.x * v.x + v.y * v.y):F0} u/s");
            }, new[] { 1.6f, 4.2f }, () =>
            {
                Moving = Jump = false;
                if (curb != null) UnityEngine.Object.Destroy(curb);
                if (block != null) UnityEngine.Object.Destroy(block);
            });
            // Third person aims in first person (MW2's third-person playlists), then back.
            Add("ads_3p", 6f, () => { bridge.ToggleFirstPerson(); bridge.GiveWeapon("m4_acog_mp"); }, t =>
            {
                Ads = t > 2f && t < 3.5f;
            }, new[] { 1.5f, 3.0f, 5.0f }, () => { Ads = false; bridge.ToggleFirstPerson(); bridge.GiveWeapon("ak47_mp"); });
            // Predator under a roof (playtest 10-04-26: in a tunnel it launched beside him): a slab 4 m
            // overhead, then the missile - it must start up in the sky.
            GameObject roofSlab = null;
            Add("pred_roof", 6f, () =>
            {
                var b = bridge.LocalBody;
                roofSlab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                roofSlab.layer = LayerIndex.world.intVal;
                roofSlab.transform.position = b.footPosition + Vector3.up * 5f;
                roofSlab.transform.localScale = new Vector3(40f, 2f, 40f);
                bridge.Admin.God = true;
                bridge.Streaks.EndAllForTest();
                bridge.Streaks.AdminGive(Native.StreakId("predator_missile"));
                StreakPulse = true;
            }, null, new[] { 3.5f, 5.0f }, () => { if (roofSlab != null) UnityEngine.Object.Destroy(roofSlab); bridge.Streaks.EndAllForTest(); bridge.Admin.God = false; });
            // The killstreak laptop's screen (playtest 10-04-26: black): look down at it as it opens.
            Vector3 lookWas = Vector3.zero;
            Add("laptop", 4f, () =>
            {
                Mw2Viewmodel.OpaqueSeeThrough = settings.ContainsKey("opaquefx");
                bridge.Admin.God = true;
                lookWas = Look; Look = new Vector3(30f, Look.y, 0f);
                bridge.Streaks.EndAllForTest();
                bridge.Streaks.AdminGive(Native.StreakId("predator_missile"));
                StreakPulse = true;
            }, null, new[] { 0.7f, 0.9f, 1.1f, 1.3f, 1.5f }, () => { Look = lookWas; bridge.Streaks.EndAllForTest(); bridge.Admin.God = false; });
            // Two pads in a row (playtest 10-04-26: some pads don't work): the second must launch too.
            GameObject pad2 = null; float pad2Base = 0f, pad2Peak = 0f, lastPadLog = 0f; int padRound = 0;
            Add("jumppad2", 12f, () => { padRound = 0; lastPadLog = 0f; }, t =>
            {
                var b = bridge.LocalBody;
                if (b == null) return;
                GameObject MakePad()
                {
                    var g = new GameObject("MW2 test jump pad");
                    g.transform.position = b.footPosition + Vector3.up * 0.5f;
                    var box = g.AddComponent<BoxCollider>(); box.isTrigger = true; box.size = new Vector3(4f, 2f, 4f);
                    var jv = g.AddComponent<JumpVolume>();
                    jv.jumpVelocity = new Vector3(0f, 35f, 0f);
                    jv.onJump = (UnityEngine.Events.UnityEvent<CharacterBody>)System.Activator.CreateInstance(typeof(JumpVolume).GetField("onJump").FieldType);
                    jv.jumpSoundString = ""; jv.time = 2f;
                    return g;
                }
                if ((padRound == 0 && t > 0.5f) || (padRound == 1 && t > 6f))
                {
                    padRound++;
                    if (pad2 != null) UnityEngine.Object.Destroy(pad2);
                    pad2 = MakePad();
                    pad2Base = pad2Peak = b.footPosition.y;
                }
                if (pad2 != null && t > (padRound == 1 ? 2f : 7.5f)) { UnityEngine.Object.Destroy(pad2); pad2 = null; }
                pad2Peak = Mathf.Max(pad2Peak, b.footPosition.y);
                if (t - lastPadLog >= 0.5f)
                {
                    lastPadLog = t;
                    var m = b.characterMotor;
                    Plugin.Log.LogInfo($"[pilot] jumppad2 t {t:F1} round {padRound} rose {pad2Peak - pad2Base:F1} m grounded {(m != null && m.isGrounded)} noAirControl {(m != null && m.disableAirControlUntilCollision)} simGrounded {bridge.Last.grounded} kcc any {(m != null && m.Motor.GroundingStatus.FoundAnyGround)} stable {(m != null && m.Motor.GroundingStatus.IsStableOnGround)} vy {(m != null ? m.velocity.y.ToString("F2") : "-")} simFeet-body {(Space.ToUnity(bridge.Last.origin).y - b.footPosition.y):F2} transient-feet {(m != null ? (m.Motor.TransientPosition.y - b.footPosition.y).ToString("F2") : "-")}");
                }
            }, new[] { 1.5f, 7.0f }, () => { if (pad2 != null) UnityEngine.Object.Destroy(pad2); pad2 = null; });
            // The MW2 Soldier survivor: his skin, Tactical Insertion (plant, get knocked away, go back)
            // and One Man Army (Choose Class, the class changes after MW2's 6 s).
            Vector3 tiAt = Vector3.zero; int survStage = 0; string omaFrom = null; int classWas = 0; CharacterBody deadBody = null;
            Add("survivor", 16f, () =>
            {
                survStage = 0;
                var b = bridge.LocalBody;
                var sl = b.skillLocator;
                Plugin.Log.LogInfo($"[pilot] survivor: isMw2={Mw2Survivor.IsMw2(b)} body={b.name} skin={b.skinIndex} key={bridge.SkinKey} skills={sl?.primary?.skillDef?.skillName}/{sl?.secondary?.skillDef?.skillName}/{sl?.utility?.skillDef?.skillName}/{sl?.special?.skillDef?.skillName}");
                classWas = Plugin.Instance.Class.Value;
            }, t =>
            {
                var b = bridge.LocalBody;
                if (b == null) return;
                var sl = b.skillLocator;
                if (survStage == 0 && t >= 0.5f)
                {
                    survStage = 1;
                    tiAt = b.footPosition;
                    Mw2Survivor.FiringSpecial = true;
                    Plugin.Log.LogInfo($"[pilot] survivor: before TI {SkillState(sl.special)}");
                    bool ok;
                    try { ok = sl.special.ExecuteIfReady(); } finally { Mw2Survivor.FiringSpecial = false; }
                    Plugin.Log.LogInfo($"[pilot] survivor: TI planted={ok} at {tiAt} {SkillState(sl.special)}");
                }
                if (survStage == 1 && t >= 2.2f)
                {
                    survStage = 2;
                    // Away along the ground (straight up just fell back onto the flare).
                    var away = b.footPosition + Vector3.ProjectOnPlane(b.inputBank.aimDirection, Vector3.up).normalized * 10f + Vector3.up * 1f;
                    TeleportHelper.TeleportBody(b, away);
                    Plugin.Log.LogInfo($"[pilot] survivor: knocked to {b.footPosition} {SkillState(sl.special)}");
                }
                if (survStage == 2 && t >= 3.4f)
                {
                    survStage = 3;
                    // A second press does nothing (RoR2's own special input, e.g. Mouse 4).
                    bool again = sl.special.ExecuteIfReady();
                    Plugin.Log.LogInfo($"[pilot] survivor: {Vector3.Distance(b.footPosition, tiAt):F1} m from the flare, second press fired={again}; dying");
                    allowDeathUntil = Time.unscaledTime + 6f;
                    deadBody = b;
                    b.healthComponent.Suicide();
                }
                if (survStage == 3 && t >= 7.5f)
                {
                    survStage = 4;
                    Plugin.Log.LogInfo($"[pilot] survivor: after the respawn grounded={bridge.State.grounded} sim feet {Space.ToUnity(bridge.State.origin)} body feet {b.footPosition} motor grounded={b.characterMotor.isGrounded}");
                    Plugin.Log.LogInfo($"[pilot] survivor: respawned={b != deadBody} {Vector3.Distance(b.footPosition, tiAt):F1} m from the flare, special {SkillState(sl.special)}");
                    omaFrom = bridge.WeaponName;
                    Plugin.Log.LogInfo($"[pilot] survivor: before OMA {SkillState(sl.utility)}");
                    bool fired = sl.utility.ExecuteIfReady();
                    Plugin.Log.LogInfo($"[pilot] survivor: OMA fired={fired}");
                }
                if (survStage == 4 && t >= 7.9f)
                {
                    survStage = 41;
                    Plugin.Log.LogInfo($"[pilot] survivor: OMA menuOpen={Mw2Menus.IsOpen} pending={bridge.OmaPending}");
                    Mw2Menus.SimulateResponse(Plugin.Instance.Class.Value == 12 ? "class2" : "class1");
                    Mw2Menus.CloseAll();
                }
                if (survStage == 41 && t >= 10.9f) { survStage = 5; Plugin.Log.LogInfo($"[pilot] survivor: 3 s into the change, weapon {bridge.WeaponName} (was {omaFrom})"); }
                if (survStage == 5 && t >= 13.0f) { survStage = 51; bridge.ToggleFirstPerson(); } // look at his skin
                if (survStage == 51 && t >= 15.0f) { survStage = 6; Plugin.Log.LogInfo($"[pilot] survivor: after the change, weapon {bridge.WeaponName} (was {omaFrom})"); }
            }, new[] { 0.75f, 1.1f, 7.6f, 8.6f, 9.4f, 13.7f }, () =>
            {
                if (survStage >= 51) bridge.ToggleFirstPerson();
                if (Plugin.Instance.Class.Value != classWas) { Plugin.Instance.Class.Value = classWas; bridge.ApplyClass(); }
            });
            // Death as the MW2 Soldier: MW2's death animation and camera (Mw2DeathCam), not Commando.
            bool deathDone = false;
            Add("death", 4.6f, () => { deathDone = false; }, t =>
            {
                var b = bridge.LocalBody;
                if (!deathDone && t >= 0.5f && b != null)
                {
                    deathDone = true;
                    allowDeathUntil = Time.unscaledTime + 30f;
                    // Since RoR2 1.21 Suicide respects setup's god mode: off first, or nobody dies.
                    bridge.Admin.God = false; b.healthComponent.godMode = false;
                    b.healthComponent.Suicide();
                    Plugin.Log.LogInfo("[pilot] death: killed the player");
                }
                if (deathDone && Mathf.Abs(t - 2.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] death: deathcam active={Mw2DeathCam.Active}");
            }, new[] { 0.3f, 0.9f, 1.5f, 2.2f, 3.2f, 4.4f }, null);
            // Choose Class mid-life (F4), MW2's rule: right after spawning it swaps on the spot; once you've
            // fought, the pick waits for the next spawn (One Man Army is the mid-fight change). F6 off / on
            // isn't a spawn; ti_respawn (a new body) is - class_check after it logs the class it took.
            int classPhase = 0;
            Add("class_queue", 4f, () => classPhase = 0, t =>
            {
                var cfg = Plugin.Instance;
                if (classPhase == 0 && t >= 0.3f)
                {
                    classPhase = 1;
                    bool grace = bridge.InClassGrace;
                    string pick = cfg.Class.Value == 12 ? "class2" : "class1"; // MW2 default classes 12 / 13
                    cfg.OnMenuResponse(pick);
                    Plugin.Log.LogInfo($"[pilot] class: in grace={grace}, picked {pick} -> Class {cfg.Class.Value}, pending {bridge.PendingClass}");
                }
                Attack = t > 0.6f && t < 1.2f; // a burst: that's combat
                if (classPhase == 1 && t >= 1.6f)
                {
                    classPhase = 2;
                    int was = cfg.Class.Value;
                    cfg.OnMenuResponse("class3"); // 14
                    Plugin.Log.LogInfo($"[pilot] class: after fighting in grace={bridge.InClassGrace}, picked class3 -> Class {cfg.Class.Value} (was {was}), pending {bridge.PendingClass}, weapon {bridge.WeaponName}");
                }
                if (classPhase == 2 && t >= 2.2f) { classPhase = 3; bridge.Toggle(bridge.LocalBody); } // F6 off
                if (classPhase == 3 && t >= 2.8f) { classPhase = 4; bridge.Toggle(bridge.LocalBody); } // F6 on
                if (classPhase == 4 && t >= 3.5f)
                {
                    classPhase = 5;
                    Plugin.Log.LogInfo($"[pilot] class: after F6 off / on Class {cfg.Class.Value}, pending {bridge.PendingClass}, MW2 mode {bridge.Active}");
                }
            }, new[] { 1.9f }, () => Attack = false);
            // Tactical Insertion respawn (playtest 10-04-26: the camera stayed tipped after it; the streak
            // carried on): flare planted, two kills, third person aiming down the sights, killed; back
            // up on the flare 2 s later. Logs the camera's tilt from rest and the kill count.
            Add("ti_respawn", 9f, () =>
            {
                deathDone = false;
                bridge.Admin.God = false; // setup's god mode would turn the kill away
                var b = bridge.LocalBody;
                if (b == null) return;
                Mw2TacticalInsertion.ServerPlant(b, b.footPosition);
                bridge.Streaks.OnKill(); bridge.Streaks.OnKill();
                Plugin.Log.LogInfo($"[pilot] ti: kills before {bridge.Streaks.State().count}");
                if (bridge.FirstPerson) bridge.ToggleFirstPerson();
            }, t =>
            {
                var b = bridge.LocalBody;
                Ads = !deathDone && t > 0.3f;
                if (!deathDone && t >= 1.0f && b != null)
                {
                    deathDone = true;
                    allowDeathUntil = Time.unscaledTime + 30f;
                    b.healthComponent.Suicide();
                    Plugin.Log.LogInfo("[pilot] ti: killed the player");
                }
                if (Mathf.Abs(t - 8.5f) < 0.02f)
                {
                    var cam = Camera.main;
                    Plugin.Log.LogInfo($"[pilot] ti: after respawn body={(bridge.LocalBody != null)} deathcam={Mw2DeathCam.Active} camera tilt {(cam != null ? Quaternion.Angle(cam.transform.localRotation, Quaternion.identity) : -1f):F2} deg, kills {bridge.Streaks.State().count}");
                }
            }, new[] { 0.8f, 2.0f, 4.0f, 6.5f, 8.6f }, () => { Ads = false; if (!bridge.FirstPerson) bridge.ToggleFirstPerson(); });
            bool classChecked = false;
            Add("class_check", 1f, () => classChecked = false, t =>
            {
                if (classChecked || t < 0.5f) return;
                classChecked = true;
                Plugin.Log.LogInfo($"[pilot] class: after the respawn Class {Plugin.Instance.Class.Value}, pending {bridge.PendingClass}, weapon {bridge.WeaponName}");
            }, new[] { 0.6f }, null);
            // MW2's FOV in RoR2's pause-menu settings (playtest 10-04-26: mid-game, a slider): the console
            // setting round-trips, the pause menu's Settings show the slider.
            Add("fov_setting", 9f, null, t =>
            {
                if (Mathf.Abs(t - 0.5f) < 0.02f)
                {
                    RoR2.Console.instance.SubmitCmd(null, "mw2_cg_fov 80");
                    Plugin.Log.LogInfo($"[pilot] fov: console set -> FieldOfView {Plugin.Instance.FieldOfView.Value}, convar reads {RoR2.Console.instance.FindConVar("mw2_cg_fov")?.GetString()}");
                }
                if (Mathf.Abs(t - 2.0f) < 0.02f)
                {
                    var pm = HarmonyLib.AccessTools.GetDeclaredMethods(typeof(RoR2.PauseManager)).Where(m => m.Name.Contains("PauseScreen")).Select(m => m.Name + (m.IsStatic ? "(static)" : ""));
                    Plugin.Log.LogInfo($"[pilot] fov: PauseManager methods {string.Join(",", pm)}");
                    // RoR2's own pause screen prefab (PauseManager's static GameObject field), opened here.
                    var fields = HarmonyLib.AccessTools.GetDeclaredFields(typeof(RoR2.PauseManager)).Where(f => f.IsStatic && f.FieldType == typeof(GameObject)).ToList();
                    Plugin.Log.LogInfo($"[pilot] fov: PauseManager GameObject fields {string.Join(",", fields.Select(f => f.Name + "=" + ((f.GetValue(null) as GameObject)?.name ?? "null")))}");
                    var prefab = fields.Select(f => f.GetValue(null) as GameObject).FirstOrDefault(g => g != null && g.GetComponentInChildren<RoR2.UI.PauseScreenController>(true) != null);
                    if (prefab != null) UnityEngine.Object.Instantiate(prefab);
                }
                if (Mathf.Abs(t - 3.5f) < 0.02f)
                {
                    var ps = UnityEngine.Object.FindObjectOfType<RoR2.UI.PauseScreenController>();
                    var open = ps != null ? HarmonyLib.AccessTools.GetDeclaredMethods(ps.GetType()).FirstOrDefault(m => m.Name.Contains("Settings") && m.GetParameters().Length == 0) : null;
                    Plugin.Log.LogInfo($"[pilot] fov: pause screen {(ps != null)}, settings opener {open?.Name ?? "none"}; methods {(ps != null ? string.Join(",", HarmonyLib.AccessTools.GetDeclaredMethods(ps.GetType()).Select(m => m.Name)) : "")}");
                    open?.Invoke(ps, null);
                }
                if (Mathf.Abs(t - 5.5f) < 0.02f)
                {
                    var all = UnityEngine.Object.FindObjectsOfType<RoR2.UI.SettingsSlider>();
                    Plugin.Log.LogInfo($"[pilot] fov: active sliders {string.Join(", ", all.Select(x => x.settingName))}");
                    var mine = all.FirstOrDefault(x => x.settingName == Mw2FovSetting.Name);
                    if (mine == null)
                        foreach (var p in UnityEngine.Object.FindObjectsOfType<RoR2.UI.SettingsPanelController>(true))
                            if (p.GetComponentsInChildren<RoR2.UI.SettingsSlider>(true).Any(x => x.settingName == "fov")) { p.gameObject.SetActive(true); Plugin.Log.LogInfo($"[pilot] fov: showing panel {p.name}"); }
                }
                if (Mathf.Abs(t - 7.0f) < 0.02f)
                {
                    // Move the slider like a player (playtest 10-04-26: it changed nothing): the FOV must follow.
                    var mine = UnityEngine.Object.FindObjectsOfType<RoR2.UI.SettingsSlider>().FirstOrDefault(x => x.settingName == Mw2FovSetting.Name);
                    Plugin.Log.LogInfo($"[pilot] fov: slider shown {mine != null}, FieldOfView before {Plugin.Instance.FieldOfView.Value}");
                    if (mine != null && mine.slider != null) mine.slider.value = 86f;
                }
                if (Mathf.Abs(t - 7.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] fov: after the slider -> FieldOfView {Plugin.Instance.FieldOfView.Value}, cg_fov {Mw2View.CgFov}");
            }, new[] { 4.5f, 6.5f, 8.0f }, () => { Plugin.Instance.FieldOfView.Value = 65f; if (RoR2.PauseManager.isPaused) RoR2.Console.instance.SubmitCmd(null, "pause"); });
            // Third person animations follow the action (playtest 10-04-26: no Tactical Insertion throw):
            // a frag (pull pin, throw), a jump, a Tactical Insertion toss - seen from the front.
            bool anim3pTi = false;
            Vector3 anim3pLook = Vector3.zero;
            Add("anim3p", 9f, () => { anim3pTi = false; anim3pLook = Look; Look = new Vector3(15f, 0f, 0f); FrontCam = true; if (bridge.FirstPerson) bridge.ToggleFirstPerson(); }, t =>
            {
                Frag = t > 1.0f && t < 1.7f;
                Jump = t > 3.6f && t < 3.7f;
                if (t > 5.5f && !anim3pTi) { anim3pTi = true; Plugin.Log.LogInfo($"[pilot] anim3p: TI thrown={bridge.ThrowTacticalInsertion()}"); }
                foreach (var at in new[] { 1.4f, 1.9f, 5.8f, 6.1f })
                    if (Mathf.Abs(t - at) < 0.02f) Plugin.Log.LogInfo($"[pilot] anim3p: t={at} anim weapon {Native.WeaponString(bridge.AnimWeapon, 2)}");
            }, new[] { 1.4f, 1.85f, 2.05f, 3.75f, 4.0f, 5.7f, 5.95f, 6.2f }, () => { Frag = Jump = false; FrontCam = false; Look = anim3pLook; if (!bridge.FirstPerson) bridge.ToggleFirstPerson(); });
            // A teammate's UAV on our radar (echo: our own team message comes back as a teammate's).
            Add("team_uav", 3f, () => { Mw2Net.Echo = true; Mw2Net.SendTeam(Mw2Net.TeamUav, 5f); }, null, new[] { 2.0f }, () => { Mw2Net.Echo = false; });
            // A teammate's nuke: countdown, flash, slow motion here too; his kills, not ours (echo).
            int nukeEnemies = 0;
            Add("team_nuke", 13f, () => { Mw2Net.Echo = true; nukeEnemies = Mw2Strike.Enemies(bridge.LocalBody).Count(); Mw2Net.SendTeam(Mw2Net.TeamNuke, 10f); }, t =>
            {
                if (Mathf.Abs(t - 12.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] team_nuke: enemies {nukeEnemies} -> {Mw2Strike.Enemies(bridge.LocalBody).Count()}, time scale {Time.timeScale:F2}");
            }, new[] { 5f, 10.3f, 11f }, () => { Mw2Net.Echo = false; bridge.Streaks.EndAllForTest(); Time.timeScale = 1f; });
            // Scavenger: a bag dropped ahead, walked over -> a magazine of reserve, the pickup icon.
            int scavBefore = 0;
            Add("scavenger", 4f, () => { var b = bridge.LocalBody; scavBefore = bridge.PilotScavengerBag(b.footPosition + Vector3.ProjectOnPlane(b.inputBank.aimDirection, Vector3.up).normalized * 1.5f); }, t =>
            {
                Moving = t > 0.8f && t < 2.0f; Fwd = Moving ? 1f : 0f;
                if (Mathf.Abs(t - 3.0f) < 0.02f) Plugin.Log.LogInfo($"[pilot] scavenger: reserve {scavBefore} -> {bridge.State.stock}");
            }, new[] { 0.5f, 2.2f, 3.2f }, () => { Moving = false; Fwd = 0f; });
            // Bullet penetration: two 4-unit walls 4 m ahead with a Lemurian behind them; an AK burst
            // ([pen] lines: each wall's cost, hits through it).
            var penWalls = new List<GameObject>();
            Add("penetration", 5f, () =>
            {
                bridge.Admin.God = true; bridge.GiveWeapon("ak47_mp");
                var bank = bridge.LocalBody.inputBank;
                var eye = bank.aimOrigin; var fwd = bank.aimDirection;
                for (int w = 0; w < 2; w++)
                {
                    var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    wall.name = "MW2 pilot wall";
                    wall.layer = LayerIndex.world.intVal;
                    wall.transform.position = eye + fwd * (4f + w * 1.5f);
                    wall.transform.rotation = Quaternion.LookRotation(fwd);
                    wall.transform.localScale = new Vector3(3f, 3f, 4f * Space.Scale);
                    penWalls.Add(wall);
                }
                var prefab = MasterCatalog.FindMasterPrefab("LemurianMaster");
                if (prefab != null) new MasterSummon { masterPrefab = prefab, position = eye + fwd * 8f, rotation = Quaternion.LookRotation(-fwd), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
            }, t =>
            {
                Attack = t > 0.4f && t < 1.0f;
                if (Mathf.Abs(t - 0.3f) < 0.02f) Plugin.Log.LogInfo($"[pilot] penetration: walls at {penWalls[0].transform.position} / {penWalls[1].transform.position}, eye {bridge.LocalBody.inputBank.aimOrigin}");
            }, new[] { 0.3f, 0.7f }, () => { Attack = false; foreach (var w in penWalls) UnityEngine.Object.Destroy(w); penWalls.Clear(); });
            // Game over -> Continue -> a new run, as players do it (10-04-26: the streak carried over):
            // 4 kills, a real death, RoR2's report screen, its Continue, the lobby, a new run.
            int goStage = 0; float goNext = 0f; Run goRun = null;
            Add("gameover", 90f, () => { goStage = 0; goNext = 0f; }, t =>
            {
                var b = bridge.LocalBody;
                allowDeathUntil = Time.unscaledTime + 5f;
                switch (goStage)
                {
                    case 0:
                        if (t < 0.5f || b == null) return;
                        bridge.Streaks.AdminKills(13); // 12 earns the UAV: one held
                        var s0 = bridge.Streaks.State();
                        Plugin.Log.LogInfo($"[pilot] gameover: before death streak {s0.count}, held {s0.stackLen}");
                        goRun = Run.instance;
                        bridge.Admin.God = false;
                        b.ClearTimedBuffs(RoR2Content.Buffs.HiddenInvincibility);
                        while (b.HasBuff(RoR2Content.Buffs.HiddenInvincibility)) b.RemoveBuff(RoR2Content.Buffs.HiddenInvincibility);
                        goStage = 1; goNext = t + 0.3f;
                        break;
                    case 1:
                        if (t < goNext || b == null) return;
                        if (b.healthComponent.alive) b.healthComponent.Suicide(null, null, DamageType.Generic);
                        goStage = 2; goNext = t + 1f;
                        break;
                    case 2:
                        // RoR2's game-over report, then its Continue (what a player clicks).
                        var report = UnityEngine.Object.FindObjectOfType<RoR2.UI.GameEndReportPanelController>();
                        if (report == null || t < goNext) return;
                        Shot("gameover_report");
                        var buttons = report.GetComponentsInChildren<RoR2.UI.HGButton>(true);
                        Plugin.Log.LogInfo($"[pilot] gameover: report up at t={t:F1}");
                        var cont = buttons.FirstOrDefault(x => x.name == "AcceptButton");
                        if (cont != null) { cont.onClick.Invoke(); Plugin.Log.LogInfo($"[pilot] gameover: pressed {cont.name}"); }
                        goStage = 3; goNext = t + 3f;
                        break;
                    case 3:
                        if (SceneManager.GetActiveScene().name == "lobby" && t >= goNext && Run.instance == null)
                        {
                            goNext = t + 3f;
                            var lu = LocalUserManager.GetFirstLocalUser();
                            var bi = BodyCatalog.FindBodyIndex(Mw2Survivor.BodyName);
                            if (lu?.currentNetworkUser != null && bi != BodyIndex.None) lu.currentNetworkUser.CallCmdSetBodyPreference(bi);
                            Cmd("pregame_start_run");
                        }
                        if (Run.instance != null && Run.instance != goRun && b != null && b.healthComponent != null && b.healthComponent.alive) { goStage = 4; goNext = t + 4f; Plugin.Log.LogInfo($"[pilot] gameover: new run, body back at t={t:F1}"); }
                        break;
                    case 4:
                        if (t < goNext) return;
                        var s1 = bridge.Streaks.State();
                        Plugin.Log.LogInfo($"[pilot] gameover: new run streak {s1.count}, held {s1.stackLen}");
                        Shot("gameover_newrun");
                        goStage = 5;
                        Finish("done");
                        break;
                }
            }, null, () => { bridge.Admin.God = true; });
            // Stuck-spot hunt (playtest 10-04-26: "we still get stuck in spots"): walk / sprint across the stage
            // without jumping, turning when stuck or every few seconds; spots land in mw2_stuck.log.
            int wanderStuck = 0, wanderMoved = 0, wanderFalls = 0; float wanderTurnAt = 0f; int wanderStart = 0;
            Add("wander", 150f, () => { bridge.Admin.God = true; wanderStuck = wanderStart = bridge.StuckCount; wanderMoved = bridge.MovedCount; wanderFalls = 0; wanderTurnAt = 6f; }, t =>
            {
                Moving = true; Fwd = 1f; Sprint = ((int)(t / 10f)) % 2 == 0;
                if ((int)(t / 3f) != (int)((t - Time.unscaledDeltaTime) / 3f)) { var st = bridge.State; Plugin.Log.LogInfo($"[pilot] wander t={t:F0}: at ({st.origin.x:F0}, {st.origin.y:F0}, {st.origin.z:F0}) v ({st.velocity.x:F0}, {st.velocity.y:F0}, {st.velocity.z:F0}) grounded {st.grounded}"); }
                // Fell out of the map and was put back: away from that edge.
                if (bridge.MovedCount != wanderMoved) { wanderMoved = bridge.MovedCount; wanderFalls++; Look = new Vector3(0f, Look.y + 180f, 0f); wanderTurnAt = t + 8f; }
                if (bridge.StuckCount != wanderStuck || t >= wanderTurnAt)
                {
                    bool stuck = bridge.StuckCount != wanderStuck;
                    wanderStuck = bridge.StuckCount;
                    Look = new Vector3(0f, Look.y + (stuck ? R(100f, 260f) : R(-70f, 70f)), 0f);
                    wanderTurnAt = t + R(5f, 9f);
                }
            }, new[] { 30f, 75f, 120f }, () => { Moving = false; Fwd = 0f; Sprint = false; Plugin.Log.LogInfo($"[pilot] wander: stuck {bridge.StuckCount - wanderStart} times, fell out {wanderFalls}, stage {SceneManager.GetActiveScene().name}"); });
            // Flinch when shot: a hit from the front (view up), then from the right; camera pitch logged around it.
            int flinchHits = 0, flinchLogs = 0;
            Add("flinch", 3f, () =>
            {
                bridge.Admin.God = false; flinchHits = flinchLogs = 0;
                var b0 = bridge.LocalBody; // RoR2's spawn invincibility
                b0.ClearTimedBuffs(RoR2Content.Buffs.HiddenInvincibility);
                while (b0.HasBuff(RoR2Content.Buffs.HiddenInvincibility)) b0.RemoveBuff(RoR2Content.Buffs.HiddenInvincibility);
            }, t =>
            {
                var b = bridge.LocalBody; var cam = b != null ? b.inputBank : null;
                if (b == null) return;
                var fwd = Vector3.ProjectOnPlane(cam.aimDirection, Vector3.up).normalized;
                if ((flinchHits == 0 && t >= 0.5f) || (flinchHits == 1 && t >= 1.8f))
                {
                    flinchHits++;
                    var dir = t < 1f ? fwd : Vector3.Cross(Vector3.up, fwd);
                    b.healthComponent.TakeDamage(new DamageInfo { damage = b.healthComponent.fullCombinedHealth * 0.25f, position = b.corePosition + dir * 5f, attacker = null, inflictor = null, procCoefficient = 0f, damageType = DamageType.Generic, crit = false, force = Vector3.zero });
                }
                var at = new[] { 0.45f, 0.55f, 0.6f, 0.75f, 1.1f, 1.85f, 1.95f };
                if (flinchLogs < at.Length && t >= at[flinchLogs]) { flinchLogs++; Plugin.Log.LogInfo($"[pilot] flinch t={t:F2}: view kick {bridge.Kick}, health {b.healthComponent.health:F0}, god {b.healthComponent.godMode}, buffs {string.Join(",", Enumerable.Range(0, BuffCatalog.buffCount).Where(i => b.HasBuff((BuffIndex)i)).Select(i => BuffCatalog.GetBuffDef((BuffIndex)i).name))}"); }
            }, new[] { 0.6f, 1.9f }, () => { bridge.Admin.God = true; var b = bridge.LocalBody; if (b != null) b.healthComponent.HealFraction(1f, default); });
            // A teammate's crate (as Mw2Net KCrate delivers it): ours to take on a 3 s hold.
            Add("team_crate", 5f, () => { var b = bridge.LocalBody; bridge.Streaks.CrateFromTeam(424242u, b.footPosition + Vector3.ProjectOnPlane(b.inputBank.aimDirection, Vector3.up).normalized * 1f, 0u); }, t =>
            {
                Interact = t > 0.5f && t < 4f;
                if (Mathf.Abs(t - 2.0f) < 0.02f || Mathf.Abs(t - 4.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] team_crate: t={t:F1} crate there {bridge.Streaks.FirstCrate != null}");
            }, new[] { 1.5f, 3.0f, 4.2f }, () => { Interact = false; });
            // A teammate's soldier takes its pain / death conditions from RoR2's damage messages (echo:
            // our own soldier mirrored as a remote): a head-height hit from behind on him.
            Add("remote_hit", 4f, () => { Mw2Net.Echo = true; bridge.Admin.God = false; }, t =>
            {
                var b = bridge.LocalBody;
                if (Mathf.Abs(t - 1.5f) < 0.02f && b != null)
                {
                    var head = b.footPosition + Vector3.up * ((b.corePosition.y - b.footPosition.y) * 2f * 0.95f);
                    b.healthComponent.TakeDamage(new DamageInfo { damage = 1f, position = head, attacker = null, inflictor = null, procCoefficient = 0f, damageType = DamageType.Generic, crit = false, force = Vector3.zero });
                }
                if (Mathf.Abs(t - 3.0f) < 0.02f) Plugin.Log.LogInfo($"[pilot] remote_hit: echo soldier hit {Mw2Net.EchoHit():x8}");
            }, new[] { 2.0f }, () => { Mw2Net.Echo = false; bridge.Admin.God = true; });
            // Optics up close (playtest 10-03-26: the ACOG's textures were wrong): hip, then aimed.
            Add("optics", 9f, () => { bridge.Admin.God = true; bridge.GiveWeapon("m4_acog_mp"); }, t =>
            {
                Ads = t > 3f && t < 6f;
                if (t > 6.2f && t < 6.3f) bridge.GiveWeapon("ak47_acog_mp");
                Ads |= t > 7.5f;
            }, new[] { 2.5f, 4.5f, 5.5f, 8.6f }, () => { Ads = false; bridge.Admin.God = false; });

            // Akimbo (playtest 10-03-26: didn't work): left gun on attack, right gun on ADS, then both,
            // then a reload; an SMG pair after.
            Add("akimbo", 14f, () => { bridge.Admin.God = true; bridge.GiveWeapon("usp_akimbo_mp"); }, t =>
            {
                Attack = (t > 2.0f && t < 2.08f) || (t > 2.5f && t < 2.58f) || (t > 4.6f && t < 4.68f);
                Ads = (t > 3.2f && t < 3.28f) || (t > 3.7f && t < 3.78f) || (t > 4.6f && t < 4.68f);
                Reload = t > 5.6f && t < 5.8f;
                if (t > 9.0f && t < 9.1f) bridge.GiveWeapon("mp5k_akimbo_mp");
                Attack |= t > 11.5f && t < 12.6f;
                Ads |= t > 11.5f && t < 12.6f;
            }, new[] { 1.8f, 2.05f, 3.25f, 4.65f, 6.4f, 8.0f, 10.8f, 12.0f }, () => { Attack = Ads = Reload = false; bridge.Admin.God = false; });

            // Stuck in terrain (playtest 10-03-26): the sim shoved into the ground must come back out
            // without RoR2 fall damage.
            float stuckHp = 0f; int stuckStage = 0;
            Add("stuck", 5f, () => { stuckStage = 0; }, t =>
            {
                var b = bridge.LocalBody;
                if (b == null) return;
                if (stuckStage == 0 && t >= 1f) { stuckStage = 1; bridge.Admin.God = false; stuckHp = b.healthComponent.health; bridge.TestSink(1f); Plugin.Log.LogInfo($"[pilot] stuck: sim pushed 1 m into the ground at {b.footPosition}"); }
                if (stuckStage == 1 && t >= 4f) { stuckStage = 2; Plugin.Log.LogInfo($"[pilot] stuck: 3 s later grounded={bridge.State.grounded} sim {Space.ToUnity(bridge.State.origin)} body {b.footPosition} health {stuckHp:F0} -> {b.healthComponent.health:F0}"); }
            }, new[] { 1.3f, 3.5f }, null);

            // Balance (playtest 10-03-26): speed, feather air jumps, damage taken, MW2 regen.
            float tuneStartY = 0f, tunePeak = 0f, hpBefore = 0f; GameObject tuneAttacker = null; int tuneStage = 0;
            Add("tuning", 13f, () =>
            {
                tuneStage = 0;
                var b = bridge.LocalBody;
                Plugin.Log.LogInfo($"[pilot] tuning: move x{bridge.MoveSpeedScale():F3} enemies x{bridge.EnemySpeedScale():F3} sprint x{Plugin.Instance.SprintTimeScale.Value}");
                b.inventory.GiveItem(RoR2Content.Items.Feather, 2);
                tuneStartY = tunePeak = b.footPosition.y;
            }, t =>
            {
                var b = bridge.LocalBody;
                if (b == null) return;
                tunePeak = Mathf.Max(tunePeak, b.footPosition.y);
                // Jump, then two more in the air (one per feather).
                Jump = (t > 0.5f && t < 0.6f) || (t > 0.9f && t < 1.0f) || (t > 1.3f && t < 1.4f) || (t > 1.7f && t < 1.8f);
                if (tuneStage == 0 && t >= 3.5f)
                {
                    tuneStage = 1;
                    Plugin.Log.LogInfo($"[pilot] tuning: jumps rose {tunePeak - tuneStartY:F1} m with {b.maxJumpCount - 1} feathers");
                    bridge.Admin.God = false;
                    tuneAttacker = new GameObject("MW2 pilot attacker");
                    hpBefore = b.healthComponent.health;
                    b.healthComponent.TakeDamage(new DamageInfo { attacker = tuneAttacker, damage = 50f, position = b.corePosition, procCoefficient = 0f, damageType = DamageType.BypassArmor });
                    Plugin.Log.LogInfo($"[pilot] tuning: a 50 hit took {hpBefore - b.healthComponent.health:F1} (x{Plugin.Instance.DamageTakenScale.Value})");
                    hpBefore = b.healthComponent.health;
                }
                if (tuneStage == 1 && t >= 7.5f) { tuneStage = 2; Plugin.Log.LogInfo($"[pilot] tuning: 4 s after the hit {b.healthComponent.health:F1}/{b.healthComponent.fullHealth:F0} (no regen yet)"); }
                if (tuneStage == 2 && t >= 12.5f) { tuneStage = 3; Plugin.Log.LogInfo($"[pilot] tuning: 9 s after the hit {b.healthComponent.health:F1}/{b.healthComponent.fullHealth:F0} (regen)"); }
            }, new[] { 1.2f }, () => { Jump = false; if (tuneAttacker != null) UnityEngine.Object.Destroy(tuneAttacker); });
            // Vehicle showroom: each killstreak aircraft parked side-on in front of the camera, still,
            // with its script effects and attachments (door guns), one screenshot each.
            var showroom = new[] { "vehicle_pavelow", "vehicle_cobra_helicopter_fly_low", "vehicle_little_bird_armed", "vehicle_ac130_low_mp", "vehicle_av8b_harrier_jet_mp", "vehicle_mig29_desert", "vehicle_b2_bomber", "vehicle_apache_mp" };
            Mw2Vehicle showVeh = null; int shownIdx = -1;
            Add("showroom", showroom.Length * 2.5f + 0.5f, () => { shownIdx = -1; bridge.Admin.God = true; }, t =>
            {
                int i = Mathf.FloorToInt(t / 2.5f);
                if (i == shownIdx || i >= showroom.Length) return;
                shownIdx = i;
                showVeh?.Destroy();
                var cam = Camera.main; var b = bridge.LocalBody;
                if (cam == null || b == null) return;
                var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                float dist = showroom[i].Contains("b2") || showroom[i].Contains("ac130") ? 70f : 30f;
                var at = cam.transform.position + fwd * dist + Vector3.up * 4f;
                showVeh = Mw2Vehicle.Spawn(showroom[i], b, at, Vector3.Cross(Vector3.up, fwd));
                if (showroom[i] == "vehicle_pavelow") { showVeh.AttachModel("weapon_minigun", "tag_gunner_left"); showVeh.AttachModel("weapon_minigun", "tag_gunner_right"); }
                Plugin.Log.LogInfo($"[pilot] showroom {showroom[i]}");
            }, System.Linq.Enumerable.Range(0, showroom.Length).Select(i => i * 2.5f + 1.8f).ToArray(), () => { showVeh?.Destroy(); showVeh = null; bridge.Admin.God = false; });
            // Vehicle damage: an attack helicopter among Lesser Wisps (ranged), then forced damage
            // to walk it through MW2's smoke stages and the crash.
            float nextVehLog = 0f; int forced = 0; bool wispsNear = false;
            Add("vehdmg", 30f, () =>
            {
                bridge.Admin.God = true;
                bridge.Streaks.EndAllForTest();
                bridge.Streaks.AdminGive(Native.StreakId("helicopter"));
                StreakPulse = true;
                Mw2Admin.SpawnRing(bridge.LocalBody, new[] { "WispMaster", "WispMaster", "WispMaster", "WispMaster" }, 25f, false);
                nextVehLog = 0f; forced = 0; wispsNear = false;
            }, t =>
            {
                var hb = Mw2VehicleHealth.Hitboxes.FirstOrDefault();
                if (t >= nextVehLog)
                {
                    nextVehLog = t + 1f;
                    if (hb != null && (int)t % 3 == 1 && hb.body != null && hb.body.hurtBoxGroup != null)
                        foreach (var h in hb.body.hurtBoxGroup.hurtBoxes)
                            if (h != null && h.collider != null)
                                Plugin.Log.LogInfo($"[pilot] vehdmg hurtbox {h.name}: on {h.collider.enabled}, trigger {h.collider.isTrigger}, layer {LayerMask.LayerToName(h.gameObject.layer)}, centre {h.collider.bounds.center} size {h.collider.bounds.size}, body at {hb.transform.position}");
                    if ((int)t % 3 == 0)
                        foreach (var ai in UnityEngine.Object.FindObjectsOfType<RoR2.CharacterAI.BaseAI>())
                        {
                            if (ai.body == null || ai.body.teamComponent == null || ai.body.teamComponent.teamIndex != TeamIndex.Monster) continue;
                            var en = ai.currentEnemy != null ? ai.currentEnemy.gameObject : null;
                            var drv = ai.skillDriverEvaluation.dominantSkillDriver;
                            Plugin.Log.LogInfo($"[pilot] vehdmg ai {ai.body.name}: enemy {(en != null ? en.name : "none")}, to heli {(hb != null ? Vector3.Distance(ai.body.corePosition, hb.transform.position) : -1f):F0} m, driver {(drv != null ? drv.customName + " max " + drv.maxDistance : "none")}");
                        }
                    Plugin.Log.LogInfo(hb != null ? $"[pilot] vehdmg t {t:F0}: health {hb.combinedHealth:F0}/{hb.fullCombinedHealth:F0}, since hit {hb.timeSinceLastHit:F1} s" : $"[pilot] vehdmg t {t:F0}: no hitbox");
                }
                // Flying ranged monsters right beside the hovering helicopter: their shots must land.
                if (hb != null && forced == 0 && t > 8f && !wispsNear)
                {
                    wispsNear = true;
                    var wisp = MasterCatalog.FindMasterPrefab("WispMaster");
                    for (int w = 0; w < 4 && wisp != null; w++)
                        new MasterSummon { masterPrefab = wisp, position = hb.transform.position + Quaternion.Euler(0f, w * 90f, 0f) * Vector3.forward * 22f, rotation = Quaternion.identity, teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
                    Plugin.Log.LogInfo("[pilot] vehdmg: 4 wisps beside the helicopter");
                }
                // Cloaked, the player drops out of the monsters' targeting: the helicopter is what's left.
                var me = bridge.LocalBody;
                if (me != null && t > 3f && t < 18f && !me.HasBuff(RoR2Content.Buffs.Cloak)) me.AddTimedBuff(RoR2Content.Buffs.Cloak, 2f);
                if (hb != null && t > 18f + forced * 2f && forced < 4)
                {
                    forced++;
                    hb.TakeDamage(new DamageInfo { damage = hb.fullCombinedHealth * 0.3f, position = hb.transform.position, damageType = DamageType.Generic, procCoefficient = 0f });
                }
            }, new[] { 6f, 12f, 19.5f, 21.5f, 23.2f, 25f }, () => { bridge.Admin.God = false; bridge.Streaks.EndAllForTest(); });
            // A parked, damageable Pave Low 25 m up with ranged monsters around it and the player cloaked:
            // do monster shots register on an aircraft hitbox?
            Mw2Vehicle parked = null; float nextParkLog = 0f;
            Add("vehhit", 20f, () =>
            {
                var cam = Camera.main; var b = bridge.LocalBody;
                bridge.Admin.God = true;
                var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                var at = b.footPosition + fwd * 30f + Vector3.up * 25f;
                parked = Mw2Vehicle.Spawn("vehicle_pavelow", b, at, Vector3.Cross(Vector3.up, fwd));
                parked.Damageable(3000f);
                foreach (var (m, k) in new[] { ("WispMaster", 0), ("WispMaster", 1), ("GolemMaster", 2), ("LemurianMaster", 3) })
                {
                    var prefab = MasterCatalog.FindMasterPrefab(m);
                    if (prefab != null) new MasterSummon { masterPrefab = prefab, position = Mw2Strike.Ground(at + Quaternion.Euler(0f, k * 90f, 0f) * Vector3.forward * 20f) + Vector3.up, rotation = Quaternion.identity, teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
                }
                nextParkLog = 0f;
            }, t =>
            {
                var b = bridge.LocalBody;
                if (b != null && !b.HasBuff(RoR2Content.Buffs.Cloak)) b.AddTimedBuff(RoR2Content.Buffs.Cloak, 2f);
                parked?.Place(parked.Position, parked.Forward);
                var hb = Mw2VehicleHealth.Hitboxes.FirstOrDefault();
                if (t >= nextParkLog) { nextParkLog = t + 2f; Plugin.Log.LogInfo(hb != null ? $"[pilot] vehhit t {t:F0}: health {hb.combinedHealth:F0}/{hb.fullCombinedHealth:F0}, since hit {hb.timeSinceLastHit:F1} s, crashing {parked?.Crashing}" : $"[pilot] vehhit t {t:F0}: no hitbox (dead {parked?.Dead})"); }
            }, new[] { 4f, 10f, 16f }, () => { parked?.Destroy(); parked = null; bridge.Admin.God = false; });
            // The Distant Roost bowl the wander got pinned in for 140 s (10-04-26): put there, push four
            // ways for 7 s each; net travel per push, the stuck hops and node moves.
            var spot = new Vector3(38.3f, -184.1f, -309.4f);
            int spotPhase = 0, spotHops = 0; float spotAt = 0f; Vector3 spotFrom = Vector3.zero;
            Add("stuckspot", 60f, () =>
            {
                spotPhase = 0; bridge.Admin.God = true; allowDeathUntil = Time.unscaledTime + 40f;
                if (SceneManager.GetActiveScene().name != "blackbeach") Run.instance.AdvanceStage(SceneCatalog.FindSceneDef("blackbeach"));
            }, t =>
            {
                var b = bridge.LocalBody;
                if (spotPhase == 0)
                {
                    allowDeathUntil = Time.unscaledTime + 5f;
                    if (SceneManager.GetActiveScene().name != "blackbeach" || b == null || !b.healthComponent.alive || t < 8f) return;
                    TeleportHelper.TeleportBody(b, spot + Vector3.up * 0.3f);
                    spotPhase = 1; spotAt = t + 2f; spotHops = bridge.HopCount;
                    Plugin.Log.LogInfo($"[pilot] stuckspot: put at {spot}");
                    return;
                }
                if (spotPhase >= 1 && spotPhase <= 4 && b != null)
                {
                    if (t < spotAt) { Moving = false; Fwd = 0f; spotFrom = b.footPosition; return; }
                    if (spotPhase == 1 && t < spotAt + 0.05f) { Plugin.Log.LogInfo($"[pilot] stuckspot: grounded {bridge.State.grounded}, origin ({bridge.State.origin.x:F1}, {bridge.State.origin.y:F1}, {bridge.State.origin.z:F1})"); UnityWorldProbe.Log(bridge.State.origin); }
                    Look = new Vector3(0f, (spotPhase - 1) * 90f, 0f);
                    Moving = true; Fwd = 1f;
                    if (t >= spotAt + 7f)
                    {
                        Plugin.Log.LogInfo($"[pilot] stuckspot: push {spotPhase} (yaw +{(spotPhase - 1) * 90}): moved {Vector3.Distance(b.footPosition, spotFrom):F1} m, hops so far {bridge.HopCount - spotHops}, node moves {bridge.NodeOutCount}");
                        Shot($"stuckspot_{spotPhase}");
                        TeleportHelper.TeleportBody(b, spot + Vector3.up * 0.3f);
                        spotPhase++; spotAt = t + 2f;
                        if (spotPhase > 4) Finish("done");
                    }
                }
            }, null, () => { Moving = false; Fwd = 0f; });
            // Tonight's stuck spots (mw2_stuck.log, a two-PC session 10-06-26): each stage loaded,
            // put on the spot, pushed eight ways 3.5 s each; net travel, hops and node moves per push.
            var replay = new (string stage, Vector3 at)[]
            {
                ("lakes", new Vector3(-174.15f, -0.15f, -8.80f)),
                ("goolake", new Vector3(209.07f, -123.85f, 10.16f)),
                ("ironalluvium", new Vector3(-72.44f, 110.63f, -49.34f)),
                ("blackbeach2", new Vector3(46.07f, 12.77f, 84.28f)),
            };
            int rpSpot = 0, rpPush = 0, rpHops = 0, rpNodes = 0; float rpAt = 0f, rpLoaded = -1f; Vector3 rpFrom = Vector3.zero; bool rpPlaced = false;
            var rpMoved = new List<string>();
            Add("stuckreplay", 230f, () =>
            {
                rpSpot = 0; rpPush = 0; rpPlaced = false; rpLoaded = -1f; rpMoved.Clear(); bridge.Admin.God = true;
            }, t =>
            {
                allowDeathUntil = Time.unscaledTime + 5f;
                if (rpSpot >= replay.Length) { Moving = false; Fwd = 0f; return; }
                var (stage, at) = replay[rpSpot];
                var b = bridge.LocalBody;
                if (SceneManager.GetActiveScene().name != stage)
                {
                    if (rpLoaded != -2f) { rpLoaded = -2f; Moving = false; Run.instance.AdvanceStage(SceneCatalog.FindSceneDef(stage)); }
                    return;
                }
                if (b == null || !b.healthComponent.alive) return;
                if (rpLoaded < 0f) { rpLoaded = t; return; }
                if (t < rpLoaded + 6f) return; // the drop pod / spawn settles first
                if (!rpPlaced)
                {
                    bridge.Admin.God = true;
                    TeleportHelper.TeleportBody(b, at + Vector3.up * 0.3f);
                    rpPlaced = true; rpPush = 0; rpAt = t + 1.5f; rpHops = bridge.HopCount; rpNodes = bridge.NodeOutCount;
                    Plugin.Log.LogInfo($"[pilot] stuckreplay: {stage} put at {at}");
                    return;
                }
                if (t < rpAt) { Moving = false; Fwd = 0f; rpFrom = b.footPosition; return; }
                Look = new Vector3(0f, rpPush * 45f, 0f);
                Moving = true; Fwd = 1f;
                if (t >= rpAt + 3.5f)
                {
                    float m = Vector3.Distance(b.footPosition, rpFrom);
                    rpMoved.Add($"{m:F1}");
                    Plugin.Log.LogInfo($"[pilot] stuckreplay: {stage} push {rpPush + 1}: moved {m:F1} m, hops {bridge.HopCount - rpHops}, node moves {bridge.NodeOutCount - rpNodes}");
                    if (rpPush == 0) Shot($"stuckreplay_{stage}");
                    TeleportHelper.TeleportBody(b, at + Vector3.up * 0.3f);
                    rpPush++; rpAt = t + 1.5f;
                    if (rpPush >= 8)
                    {
                        Plugin.Log.LogInfo($"[pilot] stuckreplay: {stage} summary: moved {string.Join(" / ", rpMoved)} m, hops {bridge.HopCount - rpHops}, node moves {bridge.NodeOutCount - rpNodes}, seam skips so far {UnityWorld.SeamSkips}");
                        rpMoved.Clear(); rpSpot++; rpPlaced = false; rpLoaded = -1f;
                    }
                }
            }, null, () => { Moving = false; Fwd = 0f; });

            // A trap nothing but the last-resort escape gets out of: 30-unit walls all round (a hop
            // clears those) under a lid 50 units up (the hop can't): pushing must end on a ground node.
            var trap = new List<GameObject>(); Vector3 trapFrom = Vector3.zero; int trapNodes = 0, trapHops = 0;
            Add("stucktrap", 12f, () =>
            {
                bridge.Admin.God = true;
                var b = bridge.LocalBody; var feet = b.footPosition; trapFrom = feet;
                trapNodes = bridge.NodeOutCount; trapHops = bridge.HopCount;
                float u = Space.Scale;
                void Slab(Vector3 at, Vector3 size)
                {
                    var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    g.name = "MW2 pilot trap"; g.layer = LayerIndex.world.intVal;
                    g.transform.position = at; g.transform.localScale = size; trap.Add(g);
                }
                float half = 26f * u, wall = 30f * u, thick = 4f * u;
                Slab(feet + new Vector3(half, wall / 2, 0f), new Vector3(thick, wall, half * 2));
                Slab(feet + new Vector3(-half, wall / 2, 0f), new Vector3(thick, wall, half * 2));
                Slab(feet + new Vector3(0f, wall / 2, half), new Vector3(half * 2, wall, thick));
                Slab(feet + new Vector3(0f, wall / 2, -half), new Vector3(half * 2, wall, thick));
                Slab(feet + new Vector3(0f, 78f * u, 0f), new Vector3(half * 2.5f, thick, half * 2.5f));
            }, t =>
            {
                Moving = t > 0.5f; Fwd = Moving ? 1f : 0f;
            }, new[] { 3f, 11f }, () =>
            {
                Moving = false; Fwd = 0f;
                var b = bridge.LocalBody;
                Plugin.Log.LogInfo($"[pilot] stucktrap: moved {(b != null ? Vector3.Distance(b.footPosition, trapFrom) : -1f):F1} m, hops {bridge.HopCount - trapHops}, node moves {bridge.NodeOutCount - trapNodes}");
                foreach (var g in trap) UnityEngine.Object.Destroy(g); trap.Clear();
            });
            // A teammate's aircraft (echo: our own, mirrored as a remote's): the host's hitbox on its copy
            // takes the hits and the owner's aircraft shows them - smoking at 60%, then shot down.
            Mw2Vehicle netHeli = null; int netLogs = 0;
            Add("vehnet", 8f, () =>
            {
                var b = bridge.LocalBody; Mw2Net.Echo = true; netLogs = 0;
                var fwd = Vector3.ProjectOnPlane(b.inputBank.aimDirection, Vector3.up).normalized;
                netHeli = Mw2Vehicle.Spawn("vehicle_pavelow", b, b.footPosition + fwd * 30f + Vector3.up * 25f, Vector3.Cross(Vector3.up, fwd));
                netHeli.Damageable(3000f);
            }, t =>
            {
                netHeli?.Place(netHeli.Position, netHeli.Forward);
                var hb = Mw2Net.EchoHitbox();
                if (netLogs == 0 && t >= 2f) { netLogs++; Plugin.Log.LogInfo($"[pilot] vehnet: echo hitbox {(hb != null ? $"{hb.combinedHealth:F0}/{hb.fullCombinedHealth:F0}" : "none")}, stage {netHeli?.Stage}"); }
                if (netLogs == 1 && t >= 2.5f && hb != null) { netLogs++; hb.TakeDamage(new DamageInfo { damage = hb.fullCombinedHealth * 0.4f, position = hb.transform.position, damageType = DamageType.Generic, procCoefficient = 0f }); }
                if (netLogs == 2 && t >= 3.5f) { netLogs++; Plugin.Log.LogInfo($"[pilot] vehnet: after 40%: hitbox {(hb != null ? hb.combinedHealthFraction.ToString("F2") : "none")}, our heli stage {netHeli?.Stage}, crashing {netHeli?.Crashing}"); }
                if (netLogs == 3 && t >= 4f && hb != null) { netLogs++; hb.TakeDamage(new DamageInfo { damage = hb.fullCombinedHealth, position = hb.transform.position, damageType = DamageType.Generic, procCoefficient = 0f }); }
                if (netLogs == 4 && t >= 5f) { netLogs++; Plugin.Log.LogInfo($"[pilot] vehnet: after the killing hit: our heli crashing {netHeli?.Crashing}, dead {netHeli?.Dead}"); }
            }, new[] { 3.6f, 5.5f }, () => { Mw2Net.Echo = false; netHeli?.Destroy(); netHeli = null; });
            // Sentry close up: turret aiming off to the side, then killed (destroyed model, smoke).
            Mw2Vehicle sentryShow = null; Vector3 sentryAt = Vector3.zero, sentryAim = Vector3.zero;
            Add("sentryshow", 9f, () =>
            {
                var cam = Camera.main; var b = bridge.LocalBody;
                bridge.Admin.God = true;
                var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                sentryAt = Mw2Strike.Ground(b.footPosition + fwd * 10f - Vector3.Cross(Vector3.up, fwd) * 3f + Vector3.up * 2f);
                var side = Vector3.Cross(Vector3.up, fwd);
                sentryShow = Mw2Vehicle.Spawn("sentry_minigun", b, sentryAt, side, scriptFx: false);
                sentryShow.Damageable(1000f);
                var tu = sentryShow.Turret;
                if (tu != null) { tu.yawLimit = 180f; tu.pitchUp = -60f; tu.pitchDown = 45f; }
                sentryAim = sentryAt + fwd * 4f + side * 6f + Vector3.up * 3f;
            }, t =>
            {
                if (sentryShow == null) return;
                sentryShow.Place(sentryAt, sentryShow.Forward);
                if (!sentryShow.Crashing) sentryShow.Turret?.Aim(sentryAim, Time.deltaTime);
                if (t > 3f && !sentryShow.Crashing)
                {
                    var hb = Mw2VehicleHealth.Hitboxes.FirstOrDefault();
                    hb?.TakeDamage(new DamageInfo { damage = 1e6f, position = sentryAt, damageType = DamageType.Generic, procCoefficient = 0f });
                }
            }, new[] { 2.5f, 3.4f, 5.5f, 8f }, () => { sentryShow?.Destroy(); sentryShow = null; bridge.Admin.God = false; });
            // A frag thrown with the look level: frames every ~0.12 s after the release, to see it leave
            // the hand and arc away (playtest: thrown grenades / markers don't visibly leave the hand).
            Add("throwshow", 4f, () => { bridge.Admin.God = true; }, t =>
            {
                Frag = t > 0.8f && t < 1.0f;
            }, Setting("throwshots", "MW2_THROW_SHOTS") == "1" ? new[] { 0.9f, 1.15f, 1.27f, 1.39f, 1.51f, 1.63f, 1.8f, 2.1f } : new float[0], () => { bridge.Admin.God = false; });
            int lapKills = 0;
            // Loadout laps: past the top streak it starts again from the bottom with grown gaps.
            Add("kslaps", 16f, () =>
            {
                bridge.Admin.God = true;
                bridge.Streaks.SetLoadoutForTest(0.5f, "uav", "airdrop", "predator_missile");
                Plugin.Log.LogInfo($"[pilot] kslaps start {bridge.Streaks.LapText()}");
                Mw2Admin.SpawnRing(bridge.LocalBody, null, 9f, true);
                Mw2Admin.SpawnRing(bridge.LocalBody, null, 12f, true);
                lapKills = 0;
            }, t =>
            {
                if (t < 2f || (int)(t * 2f) == (int)((t - Time.unscaledDeltaTime) * 2f)) return;
                var me = bridge.LocalBody;
                foreach (var cb in CharacterBody.readOnlyInstancesList.ToArray())
                {
                    if (me == null || cb == null || !Mw2Strike.IsEnemy(me, cb) || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                    var hc = cb.healthComponent;
                    Mw2Challenges.With(Mw2Challenges.Cause.Bullet, () => hc.TakeDamage(new DamageInfo { attacker = me.gameObject, inflictor = me.gameObject, damage = 1e6f, position = cb.corePosition, damageType = DamageType.Generic, procCoefficient = 0f }));
                    lapKills++;
                    Plugin.Log.LogInfo($"[pilot] kslaps kill {lapKills}: {bridge.Streaks.LapText()}, stack {bridge.Streaks.State().stackLen}");
                    break;
                }
            }, new float[0], () => { bridge.Admin.God = false; bridge.Streaks.LoadLoadout(); });
            bool ksGiven = false;
            // Killstreak pickups (Mw2StreakItems): an AC-130 pickup lands in the inventory as a chest's
            // would; it should swap in for the nearest streak and drop that one as a pickup.
            Add("kspickup", 6f, () =>
            {
                var before = bridge.Streaks.LoadoutNames();
                Plugin.Log.LogInfo($"[pilot] kspickup before: {string.Join(",", before)}");
                ksGiven = false;
            }, t =>
            {
                if (ksGiven || t < 1.5f) return;
                ksGiven = true;
                uint ac = Native.StreakId("ac130");
                Plugin.Log.LogInfo($"[pilot] kspickup at 1.5 s: {string.Join(",", bridge.Streaks.LoadoutNames())}");
                if (Mw2StreakItems.Items.TryGetValue(ac, out var def)) bridge.LocalBody.inventory.GiveItem(def.itemIndex, 1);
                else Plugin.Log.LogWarning("[pilot] kspickup: no ac130 item");
            }, new[] { 2.0f, 4.0f }, () =>
            {
                var after = bridge.Streaks.LoadoutNames();
                int drops = UnityEngine.Object.FindObjectsOfType<GenericPickupController>().Length;
                Plugin.Log.LogInfo($"[pilot] kspickup after: {string.Join(",", after)}; pickups on the ground {drops}");
            });
            // The Last Stand perk: a lethal hit -> 10 s prone with a pistol, then bleed out.
            bool lsHit = false, lsMid = false, lsGiven = false;
            bool ls3p = false;
            Add("laststand", 19f, () => { lsHit = lsMid = lsGiven = ls3p = false; bridge.Admin.God = false; }, t =>
            {
                var b = bridge.LocalBody;
                var hc = b != null ? b.healthComponent : null;
                if (hc == null) return;
                if (t > 6f && !lsGiven) { lsGiven = true; Mw2Perks.Apply(b, new[] { "specialty_pistoldeath" }); }
                if (t > 7f && !lsHit)
                {
                    lsHit = true;
                    hc.TakeDamage(new DamageInfo { damage = hc.combinedHealth * 3f, position = b.corePosition, damageType = DamageType.Generic, procCoefficient = 0f });
                    Plugin.Log.LogInfo($"[pilot] last stand: alive {hc.alive}, health {hc.combinedHealth:F1}, in last stand {Mw2Deathstreaks.InLastStand}, weapon {Native.WeaponString(bridge.Last.weapon, 2)}");
                }
                if (t > 12f && !lsMid) { lsMid = true; Plugin.Log.LogInfo($"[pilot] last stand at 5 s: alive {hc.alive}, in last stand {Mw2Deathstreaks.InLastStand}, char stance {bridge.CharacterStance}, viewHeight {bridge.Last.viewHeight:F1}"); }
                // His crawl from the front (third person), then back.
                Moving = t > 9f && t < 11f; Fwd = Moving ? 1f : 0f;
                if (t > 9.5f && !ls3p) { ls3p = true; FrontCam = true; bridge.ToggleFirstPerson(); }
            }, new[] { 7.6f, 8.5f, 10.5f, 11.5f }, () => { Moving = false; Fwd = 0f; if (ls3p) { FrontCam = false; bridge.ToggleFirstPerson(); } });
            // Camos: Custom Class 1's M4A1 in Woodland (its unlock challenge marked done on the
            // pilot's copy of the player data), first person, then third person.
            bool camoWasUnlocked = false;
            Add("camo", 7.5f, () =>
            {
                // Custom classes need Create-a-Class (level 4): the unlocked profile for the step.
                camoWasUnlocked = Mw2Progress.Unlocked;
                Mw2Progress.SetProfile(true);
                Mw2Menus.SetPlayerData("customclasses.0.weaponsetups.0.weapon", "m4");
                Mw2Menus.SetPlayerData("customclasses.0.weaponsetups.0.camo", "red_tiger");
                Plugin.Instance.Class.Value = 1;
                bridge.ApplyClass();
            }, t =>
            {
                // Third person from the front, with the echo soldier beside him (the camo as
                // teammates get it over the network).
                if (t > 4f && bridge.FirstPerson) { FrontCam = true; Mw2Net.Echo = true; bridge.ToggleFirstPerson(); }
            }, new[] { 2.5f, 3.5f, 6.0f, 6.8f }, () => { FrontCam = false; Mw2Net.Echo = false; if (!bridge.FirstPerson) bridge.ToggleFirstPerson(); Mw2Progress.SetProfile(camoWasUnlocked); });
            // Deathstreaks (one death, the mod's rule): Painkiller cuts a hit to a third with MW2's overlay,
            // Final Stand turns a lethal hit into 20 s prone with a pistol, Martyrdom's frag on death.
            float dsHp = 0f; bool dsPlain = false, dsPk = false, dsHit1 = false, dsFinal = false, dsLethal = false, dsMartyr = false;
            Add("deathstreaks", 37f, () =>
            {
                dsPlain = dsPk = dsHit1 = dsFinal = dsLethal = dsMartyr = false;
            }, t =>
            {
                var b = bridge.LocalBody;
                var hc = b != null ? b.healthComponent : null;
                if (hc == null) return;
                if (t > 7f && !dsPlain)
                {
                    dsPlain = true;
                    dsHp = hc.combinedHealth;
                    hc.TakeDamage(new DamageInfo { damage = 30f, position = b.corePosition, damageType = DamageType.Generic, procCoefficient = 0f });
                    Plugin.Log.LogInfo($"[pilot] no deathstreak: 30 damage took {dsHp - hc.combinedHealth:F1} health");
                    hc.Networkhealth = hc.fullHealth;
                }
                if (t > 7.5f && !dsPk)
                {
                    dsPk = true;
                    Mw2Deathstreaks.OnDeath(null, bridge);
                    Mw2Deathstreaks.OnSpawn(b, "specialty_combathigh");
                    bridge.RefreshPerks();
                }
                if (t > 8f && !dsHit1)
                {
                    dsHit1 = true;
                    dsHp = hc.combinedHealth;
                    hc.TakeDamage(new DamageInfo { damage = 30f, position = b.corePosition, damageType = DamageType.Generic, procCoefficient = 0f });
                    Plugin.Log.LogInfo($"[pilot] painkiller: 30 damage took {dsHp - hc.combinedHealth:F1} health");
                }
                if (t > 9f && !dsFinal)
                {
                    dsFinal = true;
                    hc.Networkhealth = hc.fullHealth;
                    Mw2Deathstreaks.OnDeath(null, bridge);
                    Mw2Deathstreaks.OnSpawn(b, "specialty_finalstand");
                    bridge.RefreshPerks();
                }
                if (t > 10f && !dsLethal)
                {
                    dsLethal = true;
                    hc.TakeDamage(new DamageInfo { damage = hc.combinedHealth * 3f, position = b.corePosition, damageType = DamageType.Generic, procCoefficient = 0f });
                    Plugin.Log.LogInfo($"[pilot] final stand: alive {hc.alive}, health {hc.combinedHealth:F1}, last stand {Mw2Deathstreaks.InLastStand}, weapon {Native.WeaponString(bridge.Last.weapon, 2)}");
                }
                if (t > 31f && !dsMartyr)
                {
                    dsMartyr = true;
                    Plugin.Log.LogInfo($"[pilot] final stand over: last stand {Mw2Deathstreaks.InLastStand}, health {hc.combinedHealth:F1}/{hc.fullCombinedHealth:F1}, weapon {Native.WeaponString(bridge.Last.weapon, 2)}");
                    Mw2Deathstreaks.OnDeath(null, bridge);
                    Mw2Deathstreaks.OnSpawn(b, "specialty_grenadepulldeath");
                    Mw2Deathstreaks.OnDeath(b, bridge);
                }
            }, new[] { 8.3f, 11f, 20f, 30.8f, 35.2f });
            // Perks as items with their effects: Custom Class 1 (Sleight of Hand, Stopping Power,
            // Steady Aim). Logs the engine bits / damage multiplier, then times a reload.
            float perkReloadAt = -1f; bool perkFull = false; int perkFullClip = 0;
            Add("perks", 7f, () =>
            {
                Plugin.Instance.Class.Value = 1;
                bridge.ApplyClass();
                var b = bridge.LocalBody;
                Plugin.Log.LogInfo($"[pilot] perks: engine bits {Mw2Perks.EngineBits(b):x}, bullet x{Mw2Perks.BulletDamage(b)}, speed x{Mw2Perks.MoveSpeed(b)}, items {string.Join(",", Mw2Perks.Items.Keys.Where(k => Mw2Perks.Has(b, k)))}");
                perkReloadAt = -1f; perkFull = false; perkFullClip = 0;
            }, t =>
            {
                if (t < 1.0f) perkFullClip = bridge.Last.clip;
                Attack = t > 1.0f && t < 2.2f;
                Reload = t > 2.6f && t < 2.8f;
                if (t > 2.6f && perkReloadAt < 0f) perkReloadAt = t;
                if (perkReloadAt > 0f && !perkFull && t > 2.9f && bridge.Last.clip >= perkFullClip && perkFullClip > 0)
                {
                    perkFull = true;
                    Plugin.Log.LogInfo($"[pilot] perks: reload done {(t - perkReloadAt):F2} s after R");
                }
            }, new[] { 2.4f, 3.0f, 3.4f, 6.5f });
            // MW2's match start (#7.5): Choose Class over the frozen countdown; Enter takes the first
            // class (Grenadier), then the count, the intro vision fading back, and the boost.
            bool prematchPicked = false;
            Add("prematch", 9f, () => { prematchPicked = false; if (!Mw2Prematch.Running) Mw2Prematch.Begin(); }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                if (t > 1.2f && !prematchPicked) { prematchPicked = true; Mw2Menus.Inject(Mw2Menus.KeyEnter); }
                Write($"{{\"t\":{t:F2},\"frozen\":{(Mw2Prematch.Frozen ? 1 : 0)},\"menu\":{(Mw2Menus.IsOpen ? 1 : 0)},\"cursor\":{(LocalUserManager.GetFirstLocalUser()?.eventSystem?.isCursorVisible == true ? 1 : 0)}}}");
            }, new[] { 0.9f, 1.6f, 2.2f, 3.4f, 4.6f, 6.5f, 8.5f });
            // Leave the run (what ending a run does to scenes): nothing MW2 may outlive its stage in a
            // broken state (the intro vision volume did, 10-02-26).
            Add("leave_run", 8f, () => Cmd("disconnect"), null, new[] { 7.5f });
            // MW2's Create-a-Class, driven by keys like a player: Custom Class 1 -> Primary ->
            // Assault Rifles -> AK-47 -> Grenade Launcher (MW2 then offers camo) -> back out.
            var cacKeys = new List<(float at, uint keys)>();
            Add("cac", 12f, () =>
            {
                cacKeys.Clear();
                foreach (var k in new (float, uint)[] {
                    (1.0f, Mw2Menus.KeyEnter), (2.2f, Mw2Menus.KeyEnter), (3.4f, Mw2Menus.KeyEnter),
                    (4.4f, Mw2Menus.KeyDown), (4.5f, Mw2Menus.KeyDown), (4.6f, Mw2Menus.KeyDown), (4.7f, Mw2Menus.KeyDown),
                    (4.8f, Mw2Menus.KeyDown), (4.9f, Mw2Menus.KeyDown), (5.0f, Mw2Menus.KeyDown), (5.1f, Mw2Menus.KeyDown),
                    (5.6f, Mw2Menus.KeyEnter), (6.8f, Mw2Menus.KeyDown), (7.2f, Mw2Menus.KeyEnter),
                    (9.0f, Mw2Menus.KeyEsc), (9.4f, Mw2Menus.KeyEsc), (9.8f, Mw2Menus.KeyEsc), (10.2f, Mw2Menus.KeyEsc) })
                    cacKeys.Add(k);
                Mw2Menus.Open("cac_popup");
            }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                while (cacKeys.Count > 0 && t >= cacKeys[0].at) { Mw2Menus.Inject(cacKeys[0].keys); cacKeys.RemoveAt(0); }
            }, new[] { 0.8f, 2.0f, 3.2f, 4.2f, 5.4f, 6.6f, 8.4f, 10.8f }, () => Mw2Menus.CloseAll());
            // MW2 third-person soldier (#8): third person, then idle, run, sprint, fire, reload.
            Vector3 lookBefore = Vector3.zero;
            Add("body_3p", 10f, () => { lookBefore = Look; Look = new Vector3(-12f, 0f, 0f); bridge.ToggleFirstPerson(); }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                Moving = t > 2.0f && t < 5.5f;
                Fwd = Moving ? 1f : 0f;
                Sprint = t > 3.8f && t < 5.5f;
                Attack = t > 6.2f && t < 7.0f;
                Reload = t > 7.6f && t < 7.8f;
            }, new[] { 1.5f, 3.0f, 4.6f, 6.6f, 8.4f }, () => { bridge.ToggleFirstPerson(); Look = lookBefore; });
            // Multiplayer path solo (Mw2Net.Echo): your own state drawn as a remote MW2 soldier 2.5 m
            // to the right, in third person: run, fire, reload, frag, RPG.
            Vector3 echoLook = Vector3.zero;
            bool echoFrag = false, echoRpg = false;
            Add("net_echo", 12f, () => { Mw2Net.Echo = true; echoLook = Look; Look = new Vector3(18f, 0f, 0f); echoFrag = echoRpg = false; bridge.ToggleFirstPerson(); bridge.GiveWeapon("ak47_mp"); }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                Moving = t > 1.5f && t < 3.0f; Fwd = Moving ? 1f : 0f;
                Attack = t > 3.5f && t < 4.5f;
                Reload = t > 5.0f && t < 5.2f;
                Frag = t > 7.0f && t < 7.3f;
                if (t > 9.0f && !echoRpg) { echoRpg = true; bridge.GiveWeapon("rpg_mp"); }
                Ads = t > 10.0f && t < 11.5f;
                if (t > 10.8f && t < 10.95f) Attack = true;
            }, new[] { 2.2f, 3.8f, 5.6f, 7.6f, 8.2f, 11.0f, 11.6f }, () => { Mw2Net.Echo = false; Look = echoLook; bridge.ToggleFirstPerson(); bridge.GiveWeapon("ak47_mp"); });
            // Akimbo in third person (own body): MW2's akimbo stances, a gun in
            // each hand, both firing.
            Add("akimbo_3p", 9f, () => { echoLook = Look; Look = new Vector3(15f, 0f, 0f); FrontCam = true; bridge.ToggleFirstPerson(); bridge.GiveWeapon("usp_akimbo_mp"); }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                Moving = false;
                Attack = (t > 5.0f && t < 5.08f) || (t > 5.6f && t < 5.68f) || (t > 6.4f && t < 6.48f);
                Ads = (t > 5.3f && t < 5.38f) || (t > 5.9f && t < 5.98f) || (t > 6.4f && t < 6.48f);
                if (t > 7.0f && t < 7.1f) bridge.GiveWeapon("mp5k_akimbo_mp");
            }, new[] { 2.0f, 3.4f, 5.02f, 5.32f, 6.45f, 8.6f }, () => { Attack = Ads = false; FrontCam = false; Look = echoLook; bridge.ToggleFirstPerson(); bridge.GiveWeapon("ak47_mp"); });
            // Map traversal (playtest 10-04-26: too slow): walk, then a long sprint that must not run out.
            float lastSpeedLog = 0f;
            Add("sprint_long", 18f, () => { lastSpeedLog = 0f; }, t =>
            {
                Moving = true; Fwd = 1f; Sprint = t > 3f;
                if (t - lastSpeedLog >= 1.5f)
                {
                    lastSpeedLog = t;
                    var v = bridge.Last.velocity; var b = bridge.LocalBody;
                    Plugin.Log.LogInfo($"[pilot] t {t:F1} {(Sprint ? "sprint" : "walk")} {Mathf.Sqrt(v.x * v.x + v.y * v.y):F0} u/s = {Mathf.Sqrt(v.x * v.x + v.y * v.y) * Space.Scale:F2} m/s, sprinting {bridge.Last.sprinting}, view-vs-aim {(b != null && b.inputBank != null ? Vector3.Angle(new Vector3(ViewForward.x, 0f, ViewForward.z), new Vector3(b.inputBank.aimDirection.x, 0f, b.inputBank.aimDirection.z)).ToString("F0") : "-")} deg, body {(b != null && b.characterMotor != null ? new Vector3(b.characterMotor.velocity.x, 0f, b.characterMotor.velocity.z).magnitude.ToString("F2") : "-")} m/s");
                }
            }, new[] { 2f, 10f }, () => { Moving = Sprint = false; Fwd = 0f; });
            // Profiles (playtest 10-04-26): unlock-all is a separate profile, the own one kept as it was.
            int profileStage = 0;
            Add("profile", 4f, () => { profileStage = 0; }, t =>
            {
                string Show() => $"unlocked={Mw2Progress.Unlocked} xp {Mw2Progress.CurrentXp} prestige {Mw2Progress.Prestige} pdata experience {Mw2Menus.PlayerData("experience")} prestige {Mw2Menus.PlayerData("prestige")} ch_marksman_ak47 {Mw2Menus.PlayerData("challengestate.ch_marksman_ak47")}";
                if (t > 0.5f && profileStage == 0) { profileStage = 1; Plugin.Log.LogInfo($"[pilot] profile before: {Show()}"); Plugin.Log.LogInfo($"[pilot] profile -> unlocked: {Mw2Progress.SetProfile(true)}: {Show()}"); }
                if (t > 2.0f && profileStage == 1) { profileStage = 2; Plugin.Log.LogInfo($"[pilot] profile -> own: {Mw2Progress.SetProfile(false)}: {Show()}"); }
                if (t > 3.0f && profileStage == 2) { profileStage = 3; Plugin.Log.LogInfo($"[pilot] profile -> unlocked again: {Mw2Progress.SetProfile(true)}: {Show()}"); Mw2Progress.SetProfile(false); }
            }, new[] { 3.5f });
            // Stances (playtest 10-04-26: no crouch or prone): crouch, crawl, prone, jump up a step, sprint up.
            float lastStanceLog = 0f; int stancePulses = 0;
            Add("stance", 16f, () => { lastStanceLog = 0f; stancePulses = 0; bridge.Stance = 0; }, t =>
            {
                if (t > 1.5f && stancePulses == 0) { stancePulses = 1; CrouchPulse = true; }
                Moving = (t > 3f && t < 4.5f) || (t > 7.5f && t < 9f); Fwd = Moving ? 1f : 0f;
                if (t > 5f && stancePulses == 1) { stancePulses = 2; PronePulse = true; }
                Jump = t > 10f && t < 10.15f;   // prone -> crouch
                if (t > 12f && stancePulses == 2) { stancePulses = 3; CrouchPulse = true; } // crouch -> stand
                if (t - lastStanceLog >= 1f)
                {
                    lastStanceLog = t;
                    var v = bridge.Last.velocity;
                    Plugin.Log.LogInfo($"[pilot] stance t {t:F1} want {bridge.Stance} char {bridge.CharacterStance} pmFlags 0x{bridge.Last.pmFlags:x} viewHeight {bridge.Last.viewHeight:F1} speed {Mathf.Sqrt(v.x * v.x + v.y * v.y):F0} u/s grounded {bridge.Last.grounded} eye {(Camera.main != null && bridge.LocalBody != null ? (Camera.main.transform.position.y - bridge.LocalBody.footPosition.y).ToString("F2") : "-")} m above feet");
                }
            }, new[] { 1.2f, 2.8f, 4f, 6.5f, 8.5f, 11f, 13.5f }, () => { Moving = Jump = false; Fwd = 0f; bridge.Stance = 0; });
            // Riot shield (playtest 10-03-26: work as intended): a Lemurian in front gets blocked, then
            // behind (not blocked: god mode takes it), bash it twice; then the shield on his back
            // (switch to the USP) blocks it from behind, and third person shows both carries.
            int shieldStage = 0;
            Add("shield", 22f, () =>
            {
                shieldStage = 0;
                bridge.Admin.God = true;
                bridge.PilotLoadout("riotshield_mp", "usp_mp");
                Mw2Admin.SpawnAhead(bridge.LocalBody, "LemurianMaster", 6f);
            }, t =>
            {
                if (t < 5f || (t > 9f && t < 10.5f)) FaceNearestEnemy();
                else if (t < 9f) FaceNearestEnemy(away: true);
                Attack = (t > 9.6f && t < 9.7f) || (t > 11.0f && t < 11.1f);
                if (t > 11.9f && shieldStage == 0) { shieldStage = 1; bridge.CycleWeapon(); } // to the USP: shield onto the back
                if (t > 12f && t < 15.5f) FaceNearestEnemy(away: true);
                if (t > 16f && shieldStage == 1) { shieldStage = 2; FrontCam = true; bridge.ToggleFirstPerson(); }
                if (t > 18.5f && shieldStage == 2) { shieldStage = 3; bridge.CycleWeapon(); } // shield back in hand
            }, new[] { 1.5f, 4.0f, 7.5f, 9.7f, 11.15f, 13.5f, 17.5f, 20.5f, 21.8f }, () => { Attack = false; FrontCam = false; bridge.ToggleFirstPerson(); bridge.Admin.God = false; bridge.GiveWeapon("ak47_mp"); });
            // Reel: a ring of monsters, turn to the nearest and fire controlled bursts (hitmarkers, blood, +100s).
            Add("gunfight", 14f, () => { bridge.GiveWeapon("ak47_acog_mp"); Mw2Admin.SpawnRing(bridge.LocalBody, null, 9f, reel); }, t =>
            {
                FaceNearestEnemy();
                Ads = t > 2.0f;
                float ph = (t - 2.5f) % 1.2f;
                Attack = t > 2.5f && ph < 0.45f;
            }, new[] { 4f, 8f, 12f }, () => bridge.GiveWeapon("ak47_mp"));
            // Intervention bolt cycle (playtest: "after the first shot you have to reload"): scoped, three shots.
            float sniperLog = 0f;
            Add("sniper_cycle", 11f, () => { sniperLog = 0f; bridge.GiveWeapon("cheytac_mp"); }, t =>
            {
                Ads = t > 1.2f;
                Attack = (t > 2.5f && t < 2.6f) || (t > 4.3f && t < 4.4f) || (t > 6.1f && t < 6.2f);
                if (t - sniperLog > 0.2f)
                {
                    sniperLog = t;
                    var st = bridge.Last;
                    Plugin.Log.LogInfo($"[sniper] t {t:F1} ws {st.weaponstate:X} clip {st.clip} stock {st.stock} ads {st.adsFrac:F2} attack {Attack}");
                }
                Reload = t > 6.9f && t < 7.0f;
            }, new[] { 2.4f, 3.2f, 4.6f }, () => bridge.GiveWeapon("ak47_mp"));
            // RPG from the hip: the loaded rocket on the tube, gone on the shot, carried back in by the reload.
            Add("rpg_reload", 8f, () => bridge.GiveWeapon("rpg_mp"), t => Attack = t > 1.5f && t < 1.65f, new[] { 1.2f, 1.7f, 3.5f, 5.0f, 7.5f }, () => bridge.GiveWeapon("ak47_mp"));
            Add("launcher_m79", 4f, () => bridge.GiveWeapon("m79_mp"), t => { Ads = t > 1.4f && t < 3.2f; Attack = t > 2.2f && t < 2.35f; if (t < 2.0f) Look += new Vector3(-6f, 0f, 0f) * Time.unscaledDeltaTime; }, new[] { 2.4f, 3.0f, 3.6f }, () => bridge.GiveWeapon("ak47_mp"));
            // The most open ground on the stage (RoR2's ground nodes with clear sky and room around):
            // killstreak checks there, not wedged in a canyon. playtest 10-06-26 liked the backdrop it found
            // on Titanic Plains for showcase shots.
            Add("open_ground", 3f, () =>
            {
                var g = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
                int n = g != null ? g.GetNodeCount() : 0;
                Vector3 best = Vector3.zero; float bestScore = -1f;
                int mask = LayerIndex.world.mask;
                for (int i = 0; i < n; i += Mathf.Max(1, n / 600))
                {
                    if (!g.GetNodePosition(new RoR2.Navigation.NodeGraph.NodeIndex(i), out var p)) continue;
                    float up = Physics.Raycast(p + Vector3.up * 1.5f, Vector3.up, out var roof, 80f, mask, QueryTriggerInteraction.Ignore) ? roof.distance : 80f;
                    if (up < 40f || Physics.CheckSphere(p + Vector3.up * 12f, 9f, mask, QueryTriggerInteraction.Ignore)) continue;
                    float side = 0f;
                    for (int a = 0; a < 8; a++)
                    {
                        var d = Quaternion.Euler(0f, a * 45f, 0f) * Vector3.forward;
                        side += Physics.SphereCast(p + Vector3.up * 15f, 4f, d, out var h, 80f, mask, QueryTriggerInteraction.Ignore) ? h.distance : 80f;
                    }
                    if (up + side > bestScore) { bestScore = up + side; best = p; }
                }
                if (bestScore > 0f) { bridge.Admin.God = true; TeleportHelper.TeleportBody(bridge.LocalBody, best + Vector3.up * 0.3f); }
                // Left alone there (playtest 10-06-26: "dont let the player get attacked and moved", "turn off
                // enemies or freeze them"): monsters never pick the neutral team; no more spawn, and the
                // ones there stand still (targets for the streaks). Stays on the spot for every streak.
                Untargetable(bridge.LocalBody);
                foreach (var cd in UnityEngine.Object.FindObjectsOfType<CombatDirector>()) cd.enabled = false;
                foreach (var m in CharacterMaster.readOnlyInstancesList)
                    if (m != null && m.teamIndex == TeamIndex.Monster) foreach (var ai in m.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
                if (bestScore > 0f) { StayPut = true; openSpot = best; }
                Plugin.Log.LogInfo($"[pilot] open_ground: {(bestScore > 0f ? $"moved to {best} (score {bestScore:F0})" : "none found")} of {n} nodes on {SceneManager.GetActiveScene().name}");
            }, null, new[] { 2.5f });
            // The radar map frame by frame (playtest 10-06-26: "stop the minimap from flashing so much"):
            // a burst standing still, then walking.
            Add("minimap_frames", 5f, null, t => { Moving = t > 2.5f; Fwd = t > 2.5f ? 1f : 0f; },
                Enumerable.Range(0, 10).Select(i => 1.0f + i * 0.1f).Concat(Enumerable.Range(0, 10).Select(i => 3.5f + i * 0.1f)).ToArray(),
                () => { Moving = false; Fwd = 0f; });
            // Choose Class at rank 1, through One Man Army and through the change-class key (playtest 10-06-26:
            // One Man Army at level 1 showed every custom class, no locks).
            Add("oma_lowrank", 6f, () =>
            {
                Mw2Menus.SetPlayerData("experience", "0");
                Plugin.Log.LogInfo($"[pilot] oma_lowrank: custom class slots at rank 1: {Mw2Progress.CustomClassSlots}, customClasses.0.inUse {Mw2Menus.PlayerData("customclasses.0.inuse")}");
                bridge.OneManArmy();
            }, t =>
            {
                if (t > 2.8f && t < 2.83f) { Mw2Menus.CloseAll(); bridge.CancelOma(); }
                if (t > 3.2f && t < 3.23f) { Mw2Menus.SetFaction(Mw2Skins.LocalFaction()); Mw2Menus.Open("changeclass"); }
            }, new[] { 1.5f, 2.5f, 4.5f }, () => { Mw2Menus.CloseAll(); Mw2Menus.SetPlayerData("experience", Mw2Progress.CurrentXp.ToString()); });
            // A shot every 30 degrees from where he stands (stage / framing picks: playtest 10-07-26, the next
            // showcase on Shattered Abodes).
            float yaw360From = 0f;
            Add("yaw360", 15f, () => { yaw360From = Mathf.Atan2(ViewForward.x, ViewForward.z) * Mathf.Rad2Deg; Plugin.Log.LogInfo($"[pilot] yaw360 at {bridge.LocalBody?.footPosition} on {SceneManager.GetActiveScene().name}"); }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                // yaws=a,b,c (and pitch=p): those directions instead of every 30 degrees.
                var list = (Setting("yaws", "") ?? "").Split(',', ';').Select(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : float.NaN).Where(v => !float.IsNaN(v)).ToList();
                if (list.Count == 0) list = Enumerable.Range(0, 12).Select(i => i * 30f).ToList();
                float pitch = float.TryParse(Setting("pitch", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pv) ? pv : 2f;
                int k = Mathf.Min((int)(t / 1.2f), list.Count - 1);
                float yaw = Mathf.Repeat(list[k], 360f);
                AimAt(yaw, pitch);
                if (k < list.Count && Mathf.Repeat(t, 1.2f) > 1.0f && Mathf.Repeat(t - Time.unscaledDeltaTime, 1.2f) <= 1.0f && t < list.Count * 1.2f) Shot($"yaw_{yaw:F0}");
            });
            Streak("uav", 7f, new[] { 3f, 6f });
            Streak("counter_uav", 3f, new[] { 2f });
            Streak("airdrop", 16f, new[] { 1.0f, 2.0f, 3.5f, 7f, 15.5f }, t => { Attack = t > 0.8f && t < 1.6f; CrateWalk(t); });
            Streak("sentry", 7f, new[] { 0.8f, 2.5f, 6f }, t => Attack = t > 1.0f && t < 1.2f);
            Streak("predator_missile", 13f, new[] { 0.6f, 2.5f, 5.0f, 8.0f }, t => { if (t > 5.5f && t < 5.6f) AttackPulse = true; Attack = t > 5.5f && t < 5.8f; Look += new Vector3(0f, 10f, 0f) * Time.unscaledDeltaTime; });
            Streak("precision_airstrike", 12f, new[] { 0.5f, 1.0f, 4f, 7f, 9f }, Select(true));
            Streak("harrier_airstrike", 25f, new[] { 0.5f, 4f, 8f, 15f, 23f }, Select(false));
            Streak("helicopter", 22f, new[] { 6f, 14f, 20f });
            Streak("airdrop_mega", 30f, new[] { 8f, 13f, 20f, 28f }, t => { Attack = t > 0.8f && t < 1.6f; });
            Streak("helicopter_flares", 18f, new[] { 8f, 15f });
            Streak("stealth_airstrike", 23f, new[] { 0.5f, 1.0f, 7f, 11f, 15f }, Select(true));
            Streak("helicopter_minigun", 20f, new[] { 1.2f, 3f, 9f, 11f, 15f }, t =>
            {
                Attack = t > 6f && t < 16f; Turn(15f, t < 8f ? 8f : 0f);
                // One press (set for 30 ms it pressed several times a frame apart: black hot came out random).
                if (t > 10f && !thermalPressed) { thermalPressed = true; InteractPulse = true; Plugin.Log.LogInfo("[pilot] chopper gunner: use key (thermal toggle)"); }
                if (t < 1f) thermalPressed = false;
                if (t > 10.8f && t < 10.83f) Plugin.Log.LogInfo($"[pilot] chopper gunner: thermal black hot {Mw2Thermal.BlackHot}");
            });
            // Looking down in the chopper (playtest 10-04-26: the view stuck, couldn't look down): the "mouse"
            // keeps pushing RoR2's aim down 60 deg/s from 3 s; the ride camera's pitch is logged.
            {
                Streak("helicopter_minigun", 10f, new[] { 3f, 6f, 8f }, stepName: "ride_look", tick: t =>
                {
                    if (t > 3f && t < 7f) PushAimDown(60f * Time.unscaledDeltaTime);
                    if (Mathf.Abs(t - 2.9f) < 0.01f || Mathf.Abs(t - 5f) < 0.01f || Mathf.Abs(t - 7.5f) < 0.01f)
                        { StreakJob.PitchYaw(bridge.LocalBody, out var rp); Plugin.Log.LogInfo($"[pilot] ride_look t={t:F1}: ride camera pitch {(Mw2Fx.View != null ? Mw2Fx.View.transform.eulerAngles.x : -1f):F1}, RoR2 camera pitch {rp.x:F1}"); }
                });
                if (steps.Count > 0 && steps[steps.Count - 1].Name == "ride_look") steps[steps.Count - 1].Roam = false;
            }
            // Wisps (and Lemurians) around the player, for the AC-130 view (playtest 10-04-26: no Wisps in it).
            Add("spawn_wisps", 2f, () =>
            {
                int n = Mw2Admin.SpawnRing(bridge.LocalBody, new[] { "WispMaster", "WispMaster", "WispMaster", "LemurianMaster", "WispMaster", "LemurianMaster" }, 18f, true);
                Plugin.Log.LogInfo($"[pilot] spawned {n} (wisps + lemurians) at 18 m");
            });
            // Class rename (playtest 10-04-26: renaming didn't work): Custom Class 1's rename popup,
            // clear the name, type one, Enter; the new name must be in playerdata (not saved: pilot).
            string renameWas = null;
            Add("rename", 6f, () =>
            {
                renameWas = Mw2Menus.PlayerData("customClasses.0.name");
                Mw2Menus.SetLocal("classIndex", 0);
                Mw2Menus.Open("pc_rename");
            }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                if (t > 1.0f && renameWas != null && Mw2Menus.Local("classIndex") == 0 && !renamed)
                {
                    renamed = true;
                    bool took = Mw2Menus.Type(new string((char)8, 20) + "Pilot Rifles");
                    Plugin.Log.LogInfo($"[pilot] rename: field took text {took}, editing {Mw2Menus.Editing}");
                }
                if (t > 3.0f && t < 3.05f) Mw2Menus.Inject(Mw2Menus.KeyEnter);
                if (t > 4.5f && renameWas != null)
                {
                    Plugin.Log.LogInfo($"[pilot] rename: '{renameWas}' -> '{Mw2Menus.PlayerData("customClasses.0.name")}', menu open {Mw2Menus.IsOpen}");
                    Mw2Menus.SetPlayerData("customClasses.0.name", renameWas);
                    renameWas = null;
                }
            }, new[] { 0.8f, 2.0f, 3.6f }, () => { renamed = false; Mw2Menus.CloseAll(); });
            // The freeze watchdog (Mw2Watchdog): hold the main thread 18 s; a dump + log copy must
            // land in BepInEx\hangs.
            Add("freeze", 2f, () => { Mw2Watchdog.Armed = true; Mw2Watchdog.Beat(); System.Threading.Thread.Sleep(18000); Mw2Watchdog.Armed = false; }, null, new[] { 1f });
            // RoR2's item pickup popup with the MW2 HUD up (playtest 10-04-26: RoR2's pickup and stat
            // popups were drawn under the MW2 layer).
            Add("item_popup", 4f, () =>
            {
                var b = bridge.LocalBody;
                if (b == null || b.inventory == null || b.master == null) return;
                b.inventory.GiveItem(RoR2Content.Items.Hoof, 1);
                CharacterMasterNotificationQueue.PushItemNotification(b.master, RoR2Content.Items.Hoof.itemIndex);
            }, null, new[] { 0.6f, 1.5f, 3.0f });
            // Playtest fixes (10-06-26): killstreak pickups on the ground as their MW2 models, the rank
            // badge, the radar inside its frame.
            Vector3 fixLook = Vector3.zero;
            Add("fixcheck_pickups", 9f, () =>
            {
                var b = bridge.LocalBody;
                if (b == null || !UnityEngine.Networking.NetworkServer.active) return;
                var f = Flat(b.inputBank != null ? b.inputBank.aimDirection : b.transform.forward).normalized;
                var r = Vector3.Cross(Vector3.up, f);
                string[] names = { "nuke", "emp", "uav", "precision_airstrike", "sentry", "predator_missile", "airdrop" };
                fixLook = Look; Look = new Vector3(10f, Look.y, 0f);
                for (int i = 0; i < names.Length; i++)
                {
                    var pick = Mw2StreakItems.PickupOf(Native.StreakId(names[i]));
                    if (pick == PickupIndex.none) { Plugin.Log.LogInfo($"[pilot] no pickup for {names[i]}"); continue; }
                    var at = b.footPosition + f * 5.5f + r * ((i - 3f) * 1.6f) + Vector3.up * 1.5f;
                    PickupDropletController.CreatePickupDroplet(pick, at, Vector3.up * 2f);
                }
                HoldFrame();
            }, t => { holdUntil = Time.unscaledTime + 0.1f; }, new[] { 4.5f, 8f }, () => Look = fixLook);

            // After-playtest fixes (playtest 10-06-26, second two-PC session).
            // Diagonal sprint: W+D used to arrive as 90/127 forward, under MW2's sprint minimum.
            foreach (var (nm, r) in new[] { ("fix_sprint_fwd", 0f), ("fix_sprint_diag", 1f) })
            {
                float rr = r; string n2 = nm;
                Add(nm, 3f, () => { bridge.Admin.God = true; Moving = true; Fwd = 1f; Right = rr; Sprint = true; }, t =>
                {
                    if (Mathf.Abs(t - 2.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] {n2}: sprinting {bridge.Last.sprinting} speed {new Vector2(bridge.Last.velocity.x, bridge.Last.velocity.y).magnitude:F0} ups");
                }, new[] { 2.4f }, () => { Moving = false; Fwd = Right = 0f; Sprint = false; });
            }
            // ADS right out of a sprint, in third person, with sprint still held (RoR2's latched sprint).
            Add("fix_ads_sprint", 5f, () => { if (bridge.FirstPerson) bridge.ToggleFirstPerson(); bridge.GiveWeapon("m4_mp"); Moving = true; Fwd = 1f; Sprint = true; }, t =>
            {
                Ads = t > 1.5f;
                if (t > 1.5f) Fwd = 0.6f;
                foreach (float at in new[] { 1.6f, 2.2f, 3.0f, 4.5f })
                    if (Mathf.Abs(t - at) < 0.02f) Plugin.Log.LogInfo($"[pilot] ads after sprint t {at:F1}: ads {bridge.Last.adsFrac:F2} sprinting {bridge.Last.sprinting} view first person {bridge.ViewFirstPerson}");
            }, new[] { 1.4f, 2.2f, 3.2f }, () => { Ads = false; Moving = false; Fwd = 0f; Sprint = false; if (!bridge.FirstPerson) bridge.ToggleFirstPerson(); });
            // Rounds through a line of three Lemurians (Intervention, then M4).
            var pierce = new List<CharacterMaster>();
            GameObject pierceFloor = null;
            foreach (var g in new[] { "cheytac_mp", "m4_mp" })
            {
                string gun = g;
                Add($"fix_pierce_{g.Replace("_mp", "")}", 6f, () =>
                {
                    bridge.Admin.God = true; bridge.GiveWeapon(gun);
                    var b = bridge.LocalBody;
                    var f = Flat(b.inputBank.aimDirection).normalized;
                    // A clear lane: a slab high over the stage, the player and the Lemurians on it.
                    var floorAt = b.footPosition + Vector3.up * 60f;
                    if (pierceFloor == null)
                    {
                        pierceFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        pierceFloor.name = "MW2 pilot floor";
                        pierceFloor.layer = LayerIndex.world.intVal;
                        pierceFloor.transform.position = floorAt - Vector3.up * 0.5f + f * 8f;
                        pierceFloor.transform.rotation = Quaternion.LookRotation(f);
                        pierceFloor.transform.localScale = new Vector3(8f, 1f, 30f);
                    }
                    var top = pierceFloor.transform.position + Vector3.up * 0.5f;
                    bridge.TeleportTo(top - f * 6f + Vector3.up * 0.2f);
                    var prefab = MasterCatalog.FindMasterPrefab("LemurianMaster");
                    pierce.Clear();
                    for (int k = 0; k < 3; k++)
                    {
                        var m = new MasterSummon { masterPrefab = prefab, position = top - f * 6f + f * (6f + k * 2.2f) + Vector3.up * 0.3f, rotation = Quaternion.LookRotation(-f), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
                        if (m != null) pierce.Add(m);
                    }
                }, t =>
                {
                    var first = pierce.Count > 0 ? pierce[0].GetBody() : null;
                    // AI off at 1.0 s; aimed at the first all along; at 2.9 s the other two put right
                    // behind it on the eye -> first line, so one round meets all three.
                    if (t > 1.0f && t < 1.1f) foreach (var m in pierce) if (m != null) foreach (var ai in m.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
                    if (first != null && t > 1.0f && t < 3.0f)
                    {
                        var eye = bridge.LocalBody.inputBank.aimOrigin;
                        var d = first.corePosition - eye;
                        AimAt(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg);
                        if (t > 2.9f && t < 2.95f)
                            for (int k = 1; k < pierce.Count; k++)
                            {
                                var cb = pierce[k] != null ? pierce[k].GetBody() : null;
                                if (cb == null) continue;
                                var mid = first.corePosition + d.normalized * (2.2f * k);
                                TeleportHelper.TeleportBody(cb, mid - (cb.corePosition - cb.footPosition));
                            }
                    }
                    Attack = t > 3.0f && t < (gun == "m4_mp" ? 3.4f : 3.12f);
                    if (Mathf.Abs(t - 5.5f) < 0.02f)
                        Plugin.Log.LogInfo($"[pilot] pierce {gun}: " + string.Join(", ", pierce.Select(m => { var cb = m != null ? m.GetBody() : null; return cb == null ? "dead" : $"{cb.healthComponent.health:F0}/{cb.healthComponent.fullHealth:F0}"; })));
                }, new[] { 2.9f, 3.2f }, () => { Attack = false; foreach (var m in pierce) if (m != null && m.GetBody() != null) m.GetBody().healthComponent.Suicide(); pierce.Clear(); if (gun == "m4_mp" && pierceFloor != null) { UnityEngine.Object.Destroy(pierceFloor); pierceFloor = null; } });
            }
            // Backup Magazine x2 plus an Alien Head, lethal recharge sped up for the test.
            float lethalCdWas = 0f;
            Add("fix_backupmag", 5f, () =>
            {
                var b = bridge.LocalBody;
                Plugin.Log.LogInfo($"[pilot] backup mag before: {bridge.AmmoDebug()}");
                b.inventory.GiveItem(RoR2Content.Items.SecondarySkillMagazine, 2);
                b.inventory.GiveItem(RoR2Content.Items.AlienHead, 1);
                lethalCdWas = Plugin.Instance.LethalCooldown.Value;
                Plugin.Instance.LethalCooldown.Value = 0.6f;
                bridge.Admin.InfiniteAmmo = false; // the pilot's setup turns it on, which skips RoR2 ammo
            }, t =>
            {
                if (Mathf.Abs(t - 0.5f) < 0.02f || Mathf.Abs(t - 4.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] backup mag t {t:F1}: {bridge.AmmoDebug()}");
            }, null, () =>
            {
                Plugin.Instance.LethalCooldown.Value = lethalCdWas;
                bridge.Admin.InfiniteAmmo = true;
                var b = bridge.LocalBody;
                if (b != null) { b.inventory.RemoveItem(RoR2Content.Items.SecondarySkillMagazine.itemIndex, 2); b.inventory.RemoveItem(RoR2Content.Items.AlienHead.itemIndex, 1); }
            });
            // Third person only from the console setting the pause menu's Off / On drives, mid-run.
            Add("fix_thirdonly", 4f, () => { ThirdOnlyConfigTest = true; if (!bridge.FirstPerson) bridge.ToggleFirstPerson(); }, t =>
            {
                if (Mathf.Abs(t - 0.5f) < 0.02f) { ThirdOnlyTest = false; RoR2.Console.instance.SubmitCmd(null, "mw2_third_person_only 1"); }
                if (Mathf.Abs(t - 1.5f) < 0.02f) Plugin.Log.LogInfo($"[pilot] third only on: config {Plugin.Instance.ThirdPersonOnly.Value} convar {RoR2.Console.instance.FindConVar("mw2_third_person_only")?.GetString()} view first person {bridge.ViewFirstPerson}");
                if (Mathf.Abs(t - 2.0f) < 0.02f) RoR2.Console.instance.SubmitCmd(null, "mw2_third_person_only 0");
                if (Mathf.Abs(t - 3.0f) < 0.02f) Plugin.Log.LogInfo($"[pilot] third only off: config {Plugin.Instance.ThirdPersonOnly.Value} wants first person {bridge.FirstPerson} view first person {bridge.ViewFirstPerson}");
            }, new[] { 1.4f, 3.2f }, () => { ThirdOnlyConfigTest = false; Plugin.Instance.ThirdPersonOnly.Value = false; if (!bridge.FirstPerson) bridge.ToggleFirstPerson(); });
            // The take's two sound tracks against each other (playtest 10-07-26: "make sure the ... sound is synced
            // up correctly"): a RoR2 thud (Wwise, captured) and an MW2 shot (the tape) on the same frame once a
            // second; tools compare the onsets in ror2.wav and cinema.wav.
            int syncFrom = 0, syncPairs = 0;
            Add("cinema_sync", 30f, () =>
            {
                if (!Mw2Cinema.Start(outDir)) Plugin.Log.LogWarning("[pilot] cinema_sync: not recording");
                syncFrom = Time.frameCount; syncPairs = 0;
            }, t =>
            {
                int f = Time.frameCount - syncFrom;
                if (f > 0 && f % 60 == 0 && syncPairs < 8)
                {
                    syncPairs++;
                    if (Camera.main != null) Util.PlaySound("Play_UI_crit", Camera.main.gameObject); // short and sharp, in the Global bank, on the listener (the menu click was lost under the ambience; the pod impact rumbles on)
                    string alias = Native.WeaponString(Native.WeaponIndex("m4_mp"), 5);
                    if (!string.IsNullOrEmpty(alias)) bridge.PilotPlayAlias(alias);
                    Plugin.Log.LogInfo($"[pilot] cinema_sync: pair {syncPairs} at recorded frame {Mw2Cinema.FrameForTest} ({alias})");
                }
                if (syncPairs >= 8 && f >= 9 * 60 && Mw2Cinema.Recording) Mw2Cinema.Stop();
            }, null, () => { if (Mw2Cinema.Recording) Mw2Cinema.Stop(); });

            // RoR2 equipment that moves you (playtest 10-07-26): Milky Chrysalis (fly, jump held) and Volcanic
            // Egg (fireball dash); the body has to actually go, then MW2 movement takes it back.
            foreach (var (nm, eq, hold) in new[] { ("fix_equip_wings", RoR2Content.Equipment.Jetpack, true), ("fix_equip_egg", RoR2Content.Equipment.FireBallDash, false) })
            {
                var e2 = eq; bool jumpHold = hold; string n2 = nm;
                Vector3 eqFrom = Vector3.zero; float eqTop = 0f; bool eqHanded = false;
                Add(nm, 7f, () =>
                {
                    var b = bridge.LocalBody;
                    bridge.Admin.God = true;
                    // The other step's equipment off first (wings last 15 s and confounded the egg).
                    var jet = HarmonyLib.AccessTools.Method(typeof(JetpackController), "FindJetpackController")?.Invoke(null, new object[] { b.gameObject }) as JetpackController;
                    if (jet != null) UnityEngine.Object.Destroy(jet.gameObject);
                    b.inventory.SetEquipmentIndex(e2.equipmentIndex);
                    if (b.equipmentSlot != null) b.inventory.RestockEquipmentCharges(0, 1);
                    eqFrom = b.footPosition; eqTop = eqFrom.y; eqHanded = false;
                }, t =>
                {
                    var b = bridge.LocalBody;
                    if (b == null) return;
                    if (t > 0.5f && t < 0.55f && b.equipmentSlot != null) HarmonyLib.AccessTools.Method(typeof(EquipmentSlot), "ExecuteIfReady")?.Invoke(b.equipmentSlot, null);
                    Jump = jumpHold && t > 0.8f && t < 3.5f;
                    if (Jump && b.inputBank != null) b.inputBank.jump.PushState(true); // RoR2's own jump: the wings read it
                    if (bridge.RoR2MovingForTest) eqHanded = true;
                    eqTop = Mathf.Max(eqTop, b.footPosition.y);
                    if (Mathf.Abs(t - 6.5f) < 0.02f)
                        Plugin.Log.LogInfo($"[pilot] {n2}: handed to RoR2 {eqHanded}, rose {eqTop - eqFrom.y:F1} m, moved {Vector3.Distance(new Vector3(b.footPosition.x, 0f, b.footPosition.z), new Vector3(eqFrom.x, 0f, eqFrom.z)):F1} m across, MW2 owns again {!bridge.RoR2MovingForTest}");
                }, new[] { 1.5f, 3.0f }, () => { Jump = false; });
            }

            // RoR2 item displays on the MW2 body, third person from the front and the back.
            var shownItems = new[] { RoR2Content.Items.Bear, RoR2Content.Items.Syringe, RoR2Content.Items.Hoof, RoR2Content.Items.CritGlasses,
                RoR2Content.Items.Mushroom, RoR2Content.Items.Medkit, RoR2Content.Items.Feather, RoR2Content.Items.SprintBonus,
                RoR2Content.Items.Behemoth, RoR2Content.Items.Crowbar, RoR2Content.Items.BossDamageBonus, RoR2Content.Items.Infusion };
            // Inspect variants, each scored by how much of the gun's cut-off rear end (the stock's open
            // end) the recorded frame shows over the whole inspect (playtest 10-07-26: "i still see the inside
            // of the gun"). No recording: the ACR in first person, the inspect run once per variant.
            {
                var defaults = (InspectSideE, InspectSideS, InspectMidE, InspectMidS, InspectTopE, InspectTopS, RackLead);
                Action reset = () => (InspectSideE, InspectSideS, InspectMidE, InspectMidS, InspectTopE, InspectTopS, RackLead) = defaults;
                var variants = new (string name, Action set)[]
                {
                    ("now", () => { }),
                    ("p0 r-16", () => { InspectTopE.x = 0f; InspectTopE.z = -16f; }),
                    ("p4 r-16", () => { InspectTopE.x = 4f; InspectTopE.z = -16f; }),
                    ("p6 r-24", () => { InspectTopE.x = 6f; InspectTopE.z = -24f; }),
                    ("p0 r-24", () => { InspectTopE.x = 0f; InspectTopE.z = -24f; }),
                    ("p10 r-24", () => { InspectTopE.x = 10f; InspectTopE.z = -24f; }),
                    ("p-4 r-16", () => { InspectTopE.x = -4f; InspectTopE.z = -16f; }),
                    ("p4 r-20 sy.3", () => { InspectTopE.x = 4f; InspectTopE.z = -20f; InspectTopS.y = 0.3f; }),
                };
                const float Settle = 2.5f, Each = 8.2f;
                const string SweepGun = "masada_eotech_silencer_mp";
                float heldAt = -1f; // the step's time the ACR was first in hand (the swap took a while once: the sweep measured the Intervention)
                int cur = -1, svMax = 0, svSum = 0, svSamples = 0;
                float inMax = 0f, inSum = 0f, inRest = -1f;
                int seeMax = 0, seeSum = 0, seeRest = -1, armMax = 0, seeFrames = 0;
                var seeBins = new int[32]; // peak see-through rays per quarter second of the inspect
                var inSeg = new float[3];
                var perSegment = new int[3];
                Add("inspect_sweep", Settle + variants.Length * Each + 15f, () =>
                {
                    bridge.Admin.God = true;
                    bridge.GiveWeapon(SweepGun);
                    cur = -1; heldAt = -1f;
                }, t =>
                {
                    Ads = Attack = false;
                    if (bridge.WeaponName != SweepGun) { if (Time.frameCount % 30 == 0) bridge.GiveWeapon(SweepGun); return; }
                    if (heldAt < 0f) { heldAt = t; Plugin.Log.LogInfo($"[sweep] {SweepGun} in hand at {t:F1} s"); }
                    float st = t - heldAt;
                    if (st < Settle) return;
                    int k = (int)((st - Settle) / Each);
                    float segT = st - Settle - k * Each;
                    if (k >= variants.Length) { Mw2Viewmodel.InspectEuler = Mw2Viewmodel.InspectShift = Vector3.zero; Mw2Viewmodel.PilotSlot = -1; return; }
                    if (k != cur)
                    {
                        if (cur >= 0) Plugin.Log.LogInfo($"[sweep] {variants[cur].name}: SEE-THROUGH rays max {seeMax}, sum {seeSum}, probes with any {seeFrames} (at rest {seeRest}); arm backs max {armMax}; stock inside {inMax * 100f:F2}%; rear end max {svMax}");
                        if (cur >= 0) Plugin.Log.LogInfo($"[sweep] {variants[cur].name} timeline (per 0.25 s): {string.Join(" ", seeBins)}");
                        for (int b = 0; b < seeBins.Length; b++) seeBins[b] = 0;
                        seeMax = seeSum = armMax = seeFrames = 0; seeRest = -1;
                        cur = k; svMax = svSum = svSamples = 0; perSegment[0] = perSegment[1] = perSegment[2] = 0;
                        inMax = inSum = 0f; inRest = -1f; inSeg[0] = inSeg[1] = inSeg[2] = 0f;
                        reset(); variants[k].set();
                        Mw2Viewmodel.InspectEuler = Mw2Viewmodel.InspectShift = Vector3.zero; Mw2Viewmodel.PilotSlot = -1;
                        bridge.ViewmodelForTest.MarkRear();
                        Plugin.Log.LogInfo($"[sweep] {variants[k].name}: stock {bridge.ViewmodelForTest.MarkStock()} triangles");
                    }
                    if (segT < 0.3f)
                    {
                        if (segT > 0.2f && inRest < 0f)
                        {
                            float tv = Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad);
                            inRest = bridge.ViewmodelForTest.StockInside(tv, out _);
                            // At rest MW2 shows the gun's front faces only: whichever way round that comes out is "front".
                            Mw2Viewmodel.BackSign = 1f;
                            int r0 = bridge.ViewmodelForTest.SeeInside(tv, 96, 54, out int g0, out _);
                            if (g0 > 0 && r0 > g0 / 2) { Mw2Viewmodel.BackSign = -1f; r0 = g0 - r0; }
                            seeRest = r0;
                            Plugin.Log.LogInfo($"[sweep] {variants[k].name}: at rest {g0} gun rays, {r0} into the shell (sign {Mw2Viewmodel.BackSign})");
                        }
                        return; // a beat at rest
                    }
                    Inspect(segT - 0.3f);
                    if (Time.frameCount % 3 == 0)
                    {
                        int n = bridge.ViewmodelForTest.RearOnScreen(Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad), out _);
                        if (n > 0) { svMax = Mathf.Max(svMax, n); svSum += n; perSegment[segT - 0.3f < 2.2f ? 0 : segT - 0.3f < 3.7f ? 1 : 2] += n; }
                        svSamples++;
                        float ins = bridge.ViewmodelForTest.StockInside(Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad), out _);
                        int see = bridge.ViewmodelForTest.SeeInside(Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad), 96, 54, out _, out int armB);
                        if (see > 0) { seeMax = Mathf.Max(seeMax, see); seeSum += see; seeFrames++; }
                        int bin = Mathf.Clamp((int)((segT - 0.3f) / 0.25f), 0, seeBins.Length - 1);
                        seeBins[bin] = Mathf.Max(seeBins[bin], see);
                        armMax = Mathf.Max(armMax, armB);
                        float over = Mathf.Max(0f, ins - Mathf.Max(inRest, 0f));
                        inMax = Mathf.Max(inMax, ins); inSum += over; inSeg[segT - 0.3f < 2.2f ? 0 : segT - 0.3f < 3.7f ? 1 : 2] += over;
                    }
                }, null, () =>
                {
                    if (cur >= 0 && cur < variants.Length) Plugin.Log.LogInfo($"[sweep] {variants[cur].name}: SEE-THROUGH rays max {seeMax}, sum {seeSum}, probes with any {seeFrames} (at rest {seeRest}); arm backs max {armMax}; stock inside {inMax * 100f:F2}%; rear end max {svMax}");
                    if (cur >= 0 && cur < variants.Length) Plugin.Log.LogInfo($"[sweep] {variants[cur].name} timeline (per 0.25 s): {string.Join(" ", seeBins)}");
                    reset();
                    Mw2Viewmodel.InspectEuler = Mw2Viewmodel.InspectShift = Vector3.zero; Mw2Viewmodel.PilotSlot = -1; Mw2Viewmodel.PilotGlide = 0.12f; Mw2Viewmodel.SkipAlias = null;
                });
            }

            Add("fix_items", 8f, () =>
            {
                var b = bridge.LocalBody;
                foreach (var it in shownItems) b.inventory.GiveItem(it, 1);
                b.inventory.SetEquipmentIndex(RoR2Content.Equipment.CommandMissile.equipmentIndex);
                if (bridge.FirstPerson) bridge.ToggleFirstPerson();
                FrontCam = true;
            }, t =>
            {
                if (t > 4f) FrontCam = false;
            }, new[] { 2.0f, 3.5f, 6.0f, 7.5f }, () =>
            {
                FrontCam = false;
                var b = bridge.LocalBody;
                if (b != null) foreach (var it in shownItems) b.inventory.RemoveItem(it.itemIndex, 1);
                if (!bridge.FirstPerson) bridge.ToggleFirstPerson();
            });

            // Props with health on the stage (pots): no master, so not targets.
            Add("fix_props", 1f, () =>
            {
                var me = bridge.LocalBody;
                var props = CharacterBody.readOnlyInstancesList.Where(cb => cb != null && cb.master == null).Select(cb => $"{cb.name} ({cb.teamComponent?.teamIndex}) enemy {Mw2Strike.IsEnemy(me, cb)}").ToList();
                Plugin.Log.LogInfo($"[pilot] masterless bodies {props.Count}: {string.Join("; ", props.Take(12))} | chest streak chance {Plugin.Instance.StreakChestChance.Value}");
            }, null, null);

            // Thermal sight on the spawned enemies (playtest 10-04-26: "did we ever confirm the thermal site
            // works?"): raise, full ADS -> scope overlay + thermal, then back to hip -> normal.
            Add("thermal_scope", 6f, () => bridge.GiveWeapon("ak47_thermal_mp"), t => Ads = t > 1.5f && t < 4.5f,
                new[] { 1.2f, 3.0f, 4.2f, 5.6f }, () => { Ads = false; bridge.GiveWeapon("ak47_mp"); });
            // Heartbeat sensor (playtest 10-04-26: the attachment did nothing): the gun's screen shows the
            // monsters ahead. Spawned around the player, looked at from the hip.
            Add("heartbeat", 7f, () =>
            {
                bridge.Admin.God = true;
                bridge.GiveWeapon("ak47_heartbeat_mp");
                int n = Mw2Admin.SpawnRing(bridge.LocalBody, new[] { "LemurianMaster", "BeetleMaster", "LemurianMaster", "BeetleMaster" }, 14f, true);
                Plugin.Log.LogInfo($"[pilot] heartbeat: spawned {n}");
            }, t =>
            {
                if (t > 4.0f && t < 4.05f) Plugin.Log.LogInfo($"[pilot] heartbeat: {Mw2Heartbeat.Blips} blips on the screen, weapon {bridge.WeaponName}");
            }, new[] { 2.0f, 3.2f, 4.4f, 5.6f }, () => { bridge.Admin.God = false; bridge.GiveWeapon("ak47_mp"); });
            // 1 = primary, 2 = secondary (playtest 10-04-26).
            Add("slots", 4f, () => Plugin.Log.LogInfo($"[pilot] slots: start {bridge.WeaponName}"), t =>
            {
                if (t > 0.8f && t < 0.85f) { bridge.SelectWeapon(1); Plugin.Log.LogInfo($"[pilot] slots: 2 -> {bridge.WeaponName}"); }
                if (t > 2.2f && t < 2.25f) { bridge.SelectWeapon(0); Plugin.Log.LogInfo($"[pilot] slots: 1 -> {bridge.WeaponName}"); }
                if (t > 3.4f && t < 3.45f) { bridge.SelectWeapon(0); Plugin.Log.LogInfo($"[pilot] slots: 1 again -> {bridge.WeaponName}"); }
            }, new[] { 1.6f, 3.0f });
            int thermalFlips = 0;
            Streak("ac130", 24f, new[] { 1.2f, 4f, 9f, 14f, 19f }, t =>
            {
                if (t > 4.5f && t < 4.55f) AttackPulse = true;
                if (t > 7.5f && t < 7.55f) GunPulse = 1;
                Attack = (t > 8f && t < 11f) || (t > 14f && t < 18f);
                if (t > 12.5f && t < 12.55f) GunPulse = 2;
                // Black hot, then white hot again (latched: a slow frame can skip a 50 ms window).
                if ((thermalFlips == 0 && t > 6.0f) || (thermalFlips == 1 && t > 15.5f)) { thermalFlips++; InteractPulse = true; }
                Turn(6f, 3f);
            });
            Streak("emp", 4f, new[] { 0.3f, 2f });
            Streak("nuke", 17f, new[] { 2f, 9f, 10.6f, 12f, 16f });
            // ---- the animatic (pilot "animatic", playtest 10-05-26): one continuous take on a view that
            // reads as RoR2, monsters roaming through it, every gun swapped in place.
            List<Mw2Cinema.Vista> landViews = null;
            Add("anim_scout", 26f, () =>
            {
                var b = bridge.LocalBody;
                var seat = b != null ? b.currentVehicle : null;
                if (seat != null) seat.EjectPassenger(b.gameObject);
                if (!bridge.Active) bridge.Toggle(b);
                bridge.Admin.God = true;
                bridge.Admin.InfiniteAmmo = true;
                Mw2Perks.Give(b, new string[0]);
                Untargetable(b);
                Mw2Cinema.On = true;
                Mw2Cinema.HideHud = Setting("hud", "") != "1";
                Mw2Cinema.Stage(Setting("res", ""), b);
                landViews = Mw2Cinema.ScoutLand(b, 6);
                foreach (var v in landViews) Plugin.Log.LogInfo($"[pilot] land view: {v.feet} yaw {v.yaw} score {v.score:F2} (sky {v.open:F2}, floor {v.drop:F2})");
            }, t =>
            {
                int i = (int)(t / 4f);
                if (landViews != null && i < landViews.Count && i != scoutShown) { scoutShown = i; Park(landViews[i]); }
                if (landViews != null && i < landViews.Count) { holdUntil = Time.unscaledTime + 0.1f; AimAt(landViews[i].yaw, landViews[i].pitch); }
                if (Mathf.Repeat(t, 4f) > 3.2f && Mathf.Repeat(t - Time.unscaledDeltaTime, 4f) <= 3.2f && landViews != null && i < landViews.Count) Shot($"land_{i}");
            }, null, () => scoutShown = -1);

            // The take (pilot "animatic"): Titanic Plains, the picked spot E (10-05-26: ruins and the
            // Teleporter), the guns swapped in place at idle, one reload per category, then each
            // category's attachments on one gun. The monsters spawned here roam it the whole time.
            Add("anim_go_00_setup", 10f, () =>
            {
                Mw2Cinema.MusicAlias = "music_mainmenu_mp"; // MW2's lobby music under the take (playtest 10-05-26)
                // Stage-gen props off the set on request ("hideshrines=1"): a Halcyon Shrine stood mid-shot
                // in v8 - and it stayed ("i kinda like the shot with the statue", 10-06-26).
                int hid = 0;
                if (Setting("hideshrines", "") == "1")
                foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                {
                    if (go == null || go.transform.parent != null) continue;
                    string gn = go.name;
                    if (gn.IndexOf("Shrine", StringComparison.OrdinalIgnoreCase) < 0 && gn.IndexOf("Halcyon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    go.SetActive(false); hid++;
                }
                Plugin.Log.LogInfo($"[pilot] animatic: {hid} shrines hidden");
                var b = bridge.LocalBody;
                var seat = b != null ? b.currentVehicle : null;
                if (seat != null) seat.EjectPassenger(b.gameObject);
                if (!bridge.Active) bridge.Toggle(b);
                bridge.Admin.God = true;
                bridge.Admin.InfiniteAmmo = true;
                Mw2Perks.Give(b, new string[0]);
                // Clear the map first: once he's on the monsters' team, KillMonsters would take him too.
                Mw2Admin.KillMonsters();
                SetConVar("director_combat_disable", "1");
                Mw2Cinema.HoldConVar("enable_damage_numbers", "0"); // the bot's hits popped RoR2 damage numbers over the shot (put back after the take)
                Untargetable(b);
                Mw2Cinema.On = true;
                Mw2Cinema.HideHud = Setting("hud", "") != "1";
                Mw2Cinema.Stage(Setting("res", ""), b);
                vista = new Mw2Cinema.Vista { feet = new Vector3(-1.17f, -146.75f, -24.54f), yaw = 285f, pitch = 6f };
                // vista=x;y;z;yaw[;pitch]: another stage's spot (playtest 10-07-26: the next take on Shattered
                // Abodes).
                var vs = (Setting("vista", "") ?? "").Split(';');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (vs.Length >= 4 && float.TryParse(vs[0], System.Globalization.NumberStyles.Float, inv, out float ax) && float.TryParse(vs[1], System.Globalization.NumberStyles.Float, inv, out float ay)
                    && float.TryParse(vs[2], System.Globalization.NumberStyles.Float, inv, out float az) && float.TryParse(vs[3], System.Globalization.NumberStyles.Float, inv, out float ayaw))
                    vista = new Mw2Cinema.Vista { feet = new Vector3(ax, ay, az), yaw = ayaw, pitch = vs.Length >= 5 && float.TryParse(vs[4], System.Globalization.NumberStyles.Float, inv, out float ap) ? ap : 6f };
                Plugin.Log.LogInfo($"[pilot] animatic: vista {vista.Value.feet} yaw {vista.Value.yaw} pitch {vista.Value.pitch} on {SceneManager.GetActiveScene().name}");
                Park(vista.Value);
                bridge.GiveWeapon("m4_mp");
                bridge.PilotReady();
                var eye = vista.Value.feet + Vector3.up * 1.7f;
                var fwd = Quaternion.Euler(0f, vista.Value.yaw, 0f) * Vector3.forward;
                var inv2 = System.Globalization.CultureInfo.InvariantCulture;
                // (playtest 10-07-26, after v10: "the portal and our background actors can be pushed out even further")
                Depth = float.TryParse(Setting("depth", ""), System.Globalization.NumberStyles.Float, inv2, out float dpt) && dpt > 0.3f ? dpt : 2f;
                // wide=k: the world k times wider (tangent) than the gun; default the 21:9 screen's width in
                // the 16:9 frame (2.389 / 1.778), as the angle was picked on it.
                Mw2View.WorldWiden = float.TryParse(Setting("wide", ""), System.Globalization.NumberStyles.Float, inv2, out float wd) && wd >= 1f ? wd : (3440f / 1440f) / (16f / 9f);
                Plugin.Log.LogInfo($"[pilot] animatic: world {Mw2View.WorldWiden:F3}x wider than the gun ({Mw2View.Vertical(Mw2View.CgFov):F1} -> {2f * Mathf.Atan(Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad) * Mw2View.WorldWiden) * Mathf.Rad2Deg:F1} deg vertical)");
                Mw2Bot.Depth = Depth;
                Mw2Bot.Shown = Shown;
                // Where the ground starts to show (diagnostics): the nearest shown spot straight out and 25 degrees aside.
                foreach (float a in new[] { -25f, 0f, 25f })
                {
                    float near = -1f;
                    for (float d = 6f; d <= 120f && near < 0f; d += 3f) if (Shown(Mw2Strike.Ground(eye + Quaternion.Euler(0f, a, 0f) * fwd * d + Vector3.up * 3f), false)) near = d;
                    Plugin.Log.LogInfo($"[pilot] animatic: depth {Depth:F2}; at {a:+0;-0;0} degrees the ground shows from {(near < 0f ? "nowhere within 120" : near.ToString("F0"))} m");
                }
                // The Teleporter: far out in the background, left of the middle (playtest 10-07-26, after v11:
                // "a lot farther back", "a little bit in the left quadrant ... not too far left, but more
                // left middle") - the first spot the camera shows from 60 m x depth out (tpdepth= metres),
                // 15 degrees left (tpyaw=), searched further out then nearer.
                var tp = TeleporterInteraction.instance;
                if (tp != null)
                {
                    float tpDist = float.TryParse(Setting("tpdepth", ""), System.Globalization.NumberStyles.Float, inv2, out float tdv) && tdv > 5f ? tdv : 60f * Depth;
                    float tpYaw = float.TryParse(Setting("tpyaw", ""), System.Globalization.NumberStyles.Float, inv2, out float tyv) ? tyv : -15f;
                    var tpDir = Quaternion.Euler(0f, tpYaw, 0f) * fwd;
                    var tpAt = Mw2Strike.Ground(eye + tpDir * tpDist + Vector3.up * 3f);
                    bool tpShown = false;
                    for (int i = 0; i < 30 && !tpShown; i++)
                    {
                        float d = tpDist + (i % 2 == 0 ? 1f : -1f) * (i / 2) * 4f;
                        if (d < 20f) continue;
                        var c = Mw2Strike.Ground(eye + tpDir * d + Vector3.up * 3f);
                        if (Shown(c, false)) { tpAt = c; tpShown = true; }
                    }
                    tp.transform.position = tpAt;
                    var tpLocal = Quaternion.Inverse(Quaternion.Euler(vista.Value.pitch, vista.Value.yaw, 0f)) * (tpAt - eye);
                    float tpX = 0.5f + 0.5f * tpLocal.x / (tpLocal.z * Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad) * Mw2View.WorldWiden * 16f / 9f);
                    Plugin.Log.LogInfo($"[pilot] animatic: teleporter at {tp.transform.position} ({Flat(tpAt - eye).magnitude:F0} m out, {tpYaw:F0} deg, frame x {tpX:F2}, {(tpShown ? "in sight" : "NOT in sight")})");
                }
                // A crowd spread across the shot and past its edges, and a survivor (an AI Commando) to
                // fight them: a match going on in the background (playtest 10-05-26: "way too clustered").
                roamers.Clear();
                int n = 0;
                for (int k = 0; k < 9; k++) if (SpawnCast(k < 6 ? 0f : 1f)) n++;
                // The match's player: an MW2 soldier (playtest 10-05-26: "way better than a commando").
                bot?.Destroy();
                Mw2Bot.PlayerStreaks = bridge.StreaksRef;
                botClass = 0;
                var botAt = Mw2Strike.Ground(eye + Quaternion.Euler(0f, -15f, 0f) * fwd * 20f * Depth + Vector3.up * 3f);
                for (float d = 20f * Depth; d <= 20f * Depth + 30f && !Shown(botAt, false); d += 3f)
                    botAt = Mw2Strike.Ground(eye + Quaternion.Euler(0f, -15f, 0f) * fwd * d + Vector3.up * 3f);
                bot = Mw2Bot.Spawn(botAt + Vector3.up * 0.3f, fwd);
                if (bot != null) { bot.StreakKind = 1; bot.DieAt = 42f; } // ~32 s on screen (setup takes 10)
                commando = bot != null ? bot.Body : null;
                Plugin.Log.LogInfo($"[pilot] animatic: MW2 soldier {(bot != null ? "in" : "MISSING")}");
                Plugin.Log.LogInfo($"[pilot] animatic: {n} monsters roaming the shot");
            }, t => { holdUntil = Time.unscaledTime + 0.1f; HoldFrame(); BattleTick(); if (Mathf.Abs(t - 9f) < 0.02f) LogCast(); }, new[] { 2f, 9.5f }, () =>
            {
                Repark();
                if (!Mw2Cinema.Start(outDir)) Plugin.Log.LogWarning("[pilot] animatic: not recording");
            });
            // The relay (playtest 10-05-26): the reload animation keeps playing while the gun changes
            // under it - guns of one family (same kind of reload) hand it on mid-motion; at a family's
            // end the reload plays out to rest, a beat, and the next family starts its own.
            // Attachments come and go along the way (the same gun's reload with a sight popped on).
            // relay: the reload runs on through the family. Not where MW2's reload takes the gun off the
            // screen (akimbo, the launchers - a relay there is an empty frame, 10-05-26): those swap at
            // rest, and one of them plays its whole reload as the family's finale.
            var families = new (string name, string[] guns, string[] atts, bool relay)[]
            {
                // (the M4 and the ACR show with their underbarrels, further on - playtest 10-07-26: the noob
                // tubes felt out of place among the rifles)
                ("rifles", new[] { "m16", "scar", "ak47" }, new[] { "heartbeat", "thermal", "eotech", "silencer", "acog", "reflex" }, true),
                // The FAL at rest, no reload: MW2's is a mag knock (the hand goes off for the new mag, the
                // tilted FAL's long mag sticks out on its own meanwhile) and read as a mag dropping out
                // by itself, even played whole (playtest 10-05-26).
                ("fal", new[] { "fal" }, new[] { "heartbeat", "acog", "reflex" }, false),
                ("bullpups", new[] { "famas", "tavor", "fn2000", "aug", "sa80" }, new[] { "eotech", "silencer", "acog", "reflex", "thermal", "heartbeat", "grip" }, true),
                ("smgs", new[] { "p90", "mp5k", "ump45", "kriss", "pp2000", "tmp" }, // the Uzi shows in akimbo. The P90 opens it: the L86 finishes its reload, then a real switch to the P90 (playtest 10-06-26)
                    new[] { "reflex", "silencer", "eotech", "acog", "thermal" }, true),
                ("lmgs", new[] { "rpd", "mg4", "m240" }, new[] { "grip", "acog", "heartbeat", "thermal", "silencer", "reflex", "eotech" }, true),
                ("snipers", new[] { "cheytac", "barrett", "wa2000", "m21" }, new[] { "acog", "thermal", "silencer", "heartbeat" }, true),
                ("shield", new[] { "riotshield" }, new string[0], false),
                // (the Beretta opens them: drawn first, the tactical USP's knife popped up over the fist - playtest
                // 10-06-26; second, it glides in from the Beretta's hold)
                ("pistols", new[] { "beretta", "usp", "deserteagle", "deserteaglegold", "coltanaconda", "glock", "beretta393" }, new[] { "tactical", "silencer", "reflex", "eotech" }, true),
                // A few akimbos, in order of where their idle holds the guns (forward 0.37 -> 0.44 m):
                // all twelve had the hands sliding in and out at every swap (playtest 10-05-26).
                // The akimbo 1887s open it: drawn with MW2's first-time flip, then a shot from each in
                // turn, then the rest (playtest 10-05-26; the single 1887 isn't shown).
                ("akimbo", new[] { "model1887_akimbo", "deserteagle_akimbo" }, new string[0], false), // "they get the idea" (playtest 10-05-26)
                // The AA-12 glides in from the Striker: its own draw put the left hand across the gun
                // (playtest 10-05-26, twice).
                ("shotguns", new[] { "m1014", "striker", "aa12" }, // no SPAS-12: its one-frame glitch (playtest 10-05-26)
                     new[] { "grip", "reflex", "silencer", "eotech" }, true),
                // The underbarrels between the shotguns and the launchers (playtest 10-07-26: slot them where
                // they blend): the shotguns' blasts into the M4's Masterkey, the ACR's noob tube into the
                // M79 and the rest of the launchers.
                ("underbarrels", new[] { "m4", "masada" }, new string[0], false),
                ("launchers", new[] { "m79", "at4", "stinger", "javelin", "rpg" }, new string[0], false), // small to big (the blends jumped, 10-05-26)
            };
            // Each family plays acts (playtest 10-05-26: "a variation between reloading and shooting... ads
            // too, and hipfire"): its first guns fire from the hip, the next aim down the sights and
            // fire, the rest hand the reload on; one act flows into the next across the gun swaps.
            // 0 at rest, 1 hip fire, 2 ADS fire, 3 reload relay.
            var segs = new List<(string weapon, int family, float dur, int act)>();
            // Attachment kinds shown so far: a kind not yet seen goes first (playtest 10-05-26: the same red
            // dot on everything got old - it's a showcase of what there is).
            var shownAtt = new HashSet<string>();
            for (int fi = 0; fi < families.Length; fi++)
            {
                var fam = families[fi];
                var list = new List<string>();
                int rot = 0;
                foreach (var g in fam.guns)
                {
                    string plain = g + "_mp";
                    if (Native.WeaponIndex(plain) == 0) { Plugin.Log.LogInfo($"[pilot] relay: no {plain}, skipped"); continue; }
                    // One attachment on this gun: a kind not shown yet if it has one, else the family's
                    // next in turn.
                    string pick = null;
                    // The underbarrels on their hosts: the ACR's launcher, the M4's Masterkey (playtest 10-05-26).
                    if (fam.name == "underbarrels" && g == "masada" && Native.WeaponIndex("masada_gl_mp") != 0) pick = "gl";
                    else if (fam.name == "underbarrels" && g == "m4" && Native.WeaponIndex("m4_shotgun_mp") != 0) pick = "shotgun";
                    else if (g == "usp" && Native.WeaponIndex("usp_tactical_mp") != 0) pick = "tactical";
                    for (int k = 0; k < fam.atts.Length && pick == null; k++)
                    {
                        string a = fam.atts[(rot + k) % fam.atts.Length];
                        if (!shownAtt.Contains(a) && a != "gl" && a != "shotgun" && a != "tactical" && Native.WeaponIndex($"{g}_{a}_mp") != 0) { pick = a; rot += k + 1; }
                    }
                    // (each kind once in the whole reel - no repeats, playtest 10-05-26)
                    // Each gun once, in its best form: with a kind of attachment not shown yet, else plain
                    // (playtest 10-05-26: "only show the gun in its coolest form once").
                    if (pick != null) { list.Add($"{g}_{pick}_mp"); shownAtt.Add(pick); }
                    else list.Add(plain);
                }
                // The big sights first in the family, from the hip (coming out of the thermal into the
                // reload looked weird, 10-05-26).
                list = list.OrderBy(x => x.StartsWith("p90") ? -1 : x.Contains("_thermal") || x.Contains("_acog") ? 0 : Underbarrel(x) ? 1 : 2).ToList(); // then the underbarrels (the P90 stays first)
                int n = list.Count;
                bool scoped = fam.name == "snipers"; // a full-screen scope overlay in ADS: they fire from the hip
                for (int i = 0; i < n; i++)
                {
                    string w = list[i];
                    int act;
                    if (fam.name == "launchers") act = 0;
                    else if (fam.name == "shield") act = 1;               // bashes (playtest 10-05-26: "soo boring")
                    else if (fam.name == "akimbo") act = 1;              // akimbo has no ADS
                    else if (w.StartsWith("aa12")) act = 1;              // hip fire (its reload put an arm through the gun)
                    else if (fam.name == "fal") act = i < (n + 1) / 2 ? 1 : 2;
                    else if (!fam.relay) act = 0;
                    else if (n == 1) act = 3;                            // one-gun finales: the whole reload
                    else
                    {
                        // (playtest 10-07-26: "we arent utilizing hipfiring for the variation": ~45% hip, 25% aimed)
                        int hipN = Mathf.Max(1, Mathf.RoundToInt(n * 0.45f)), adsN = n <= 3 ? 0 : Mathf.Max(1, Mathf.RoundToInt(n * 0.25f));
                        act = i < hipN ? 1 : i < hipN + adsN ? 2 : 3;
                    }
                    if (act == 2 && (scoped || w.Contains("_thermal") || w.Contains("_acog"))) act = 1; // their zoom jumped between sight levels
                    if (Underbarrel(w)) act = 1; // switched to and fired from the hip
                    bool att = w.Count(c => c == '_') >= 2;
                    // (the RPG straight into its reload: "the animation is cool", playtest 10-06-26)
                    float dur = Underbarrel(w) ? 3.0f : w == "rpg_mp" ? 0.05f : fam.name == "launchers" ? 0.85f : fam.name == "shield" ? 1.8f : w.StartsWith("model1887_akimbo") ? 2.8f : w.Contains("_tactical") ? 0.25f + 0.53f * 0.82f : act == 0 ? 0.6f : fam.relay && n == 1 ? 0.05f
                              : act == 2 ? (att ? 0.85f : 1.0f) : (att ? 0.75f : 0.9f); // (trimmed to the music, 10-06-26)
                    segs.Add((w, fi, dur, act));
                }
            }
            // from=gun: start at that gun's beat (short test takes of the end of the reel).
            string from = Setting("from", "");
            if (!string.IsNullOrEmpty(from))
            {
                int k0 = segs.FindIndex(x => x.weapon.StartsWith(from));
                if (k0 > 0) segs.RemoveRange(0, k0);
            }
            // beats=N: only the first N (short test takes).
            if (int.TryParse(Setting("beats", ""), out int maxBeats) && maxBeats > 0 && segs.Count > maxBeats) segs.RemoveRange(maxBeats, segs.Count - maxBeats);
            const int ReloadSlot = 9; // asset_slots::RELOAD (a shell's insert on the shell-fed shotguns)
            const int RaiseSlot = 13, DropSlot = 16; // asset_slots::RAISE / DROP
            const int MeleeSlot = 7; // asset_slots::MELEE
            const int FireSlot = 3, AdsFireSlot = 32; // asset_slots::FIRE / ADS_FIRE
            // The akimbo 1887s come up with MW2's first-time pullout (FIRST_RAISE, 14): the flip.
            Func<string, int> RaiseOf = w => w.StartsWith("model1887_akimbo") && bridge.PilotSlotSeconds(14) > 0.05f ? 14 : RaiseSlot;
            const int StartSlot = 11, EndSlot = 12; // asset_slots::RELOAD_START / RELOAD_END (shell-fed guns)
            // Modes: 0 the act on the current gun, 1 the family's reload playing out to rest, 2 drop (a
            // family's gun away), 3 raise (the next family's gun up), 4 reload start, 5 reload end (the
            // shotguns' shell loop sits between them), 6 idle beat after a raise, 7 settle (sights down
            // before the reload act). Between families a real MW2 weapon switch, not a cut.
            int seg = -1; float segT = 0f, phase = 0f, phase2 = 0f, restT = 0f, lastT = 0f; int mode = 0;
            bool knifed = false, altOn = false; int knifeSeg = -1, swapLog = 0;
            Action<int> enter = k =>
            {
                seg = k; segT = 0f;
                SubLabel = segs[k].weapon.Replace("_mp", "") + (segs[k].act == 1 ? " hip" : segs[k].act == 2 ? " ads" : segs[k].act == 3 ? " reload" : "");
                float adsWas = bridge.Last.adsFrac;
                Mw2Viewmodel.SwapsExpected = 1;
                swapLog = 5;
                altOn = false;
                bridge.GiveWeapon(segs[k].weapon);
                bridge.PilotReady();
                if (segs[k].act == 2) bridge.PilotSetAds(adsWas); // ADS to ADS: the sights stay up
                // The RPG comes in empty (just fired): its finale reload brings the rocket in from below.
                if (segs[k].weapon == "rpg_mp") bridge.PilotSpendClip();
            };
            Add("anim_go_01_relay", 900f, () =>
            {
                Repark();
                seg = -1; phase = 0f; phase2 = 0f; mode = 3; lastT = 0f; knifed = false; knifeSeg = -1; // the take opens on the first gun's raise
                Plugin.Log.LogInfo($"[pilot] relay: {segs.Count} beats");
                // Each swap glides a quarter second from the old gun's pose (10-05-26: "really jumpy").
                Mw2Viewmodel.SwapBlend = float.TryParse(Setting("glide", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gl) ? gl : 0.25f;
                if (segs.Count > 0) enter(0);
            }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                HoldFrame();
                BattleTick();
                if (swapLog > 0 && Camera.main != null)
                {
                    // The frames after a swap (the Gold Deagle's two-frame view jump, 10-06-26).
                    var c = Camera.main;
                    Plugin.Log.LogInfo($"[swapcam] {SubLabel} -{swapLog}: fov {c.fieldOfView:F2} rot {c.transform.eulerAngles} ads {bridge.Last.adsFrac:F2} weapon {bridge.WeaponName}");
                    swapLog--;
                }
                float dt = Mathf.Max(0f, t - lastT);
                lastT = t;
                if (seg < 0 || seg >= segs.Count) { stepDone = true; return; }
                int act = segs[seg].act;
                bool sameNext = seg + 1 < segs.Count && segs[seg + 1].family == segs[seg].family;
                if (mode == 0)
                {
                    if (act == 1 || act == 2)
                    {
                        // Firing: short bursts from autos, a shot at a time from the rest; the magazine
                        // kept full so no unplanned reload breaks in.
                        Mw2Viewmodel.PilotSlot = -1;
                        bridge.PilotFillClip();
                        string w = segs[seg].weapon;
                        if (w.Contains("_tactical") && (!knifed || knifeSeg == seg))
                        {
                            // The first tactical knife: a stab, not a shot (playtest 10-05-26; once only). The
                            // pilot plays the gun's own melee clip start to end: through the sim's melee the
                            // gun left the screen for a moment after the stab (twice, 10-05-26).
                            Ads = Attack = Melee = false;
                            knifed = true; knifeSeg = seg;
                            float ms = bridge.PilotSlotSeconds(MeleeSlot);
                            // The whole stab: the clip ends with both arms out of the frame, and the beat
                            // ends there - the next pistol comes up from that pose (the swap glide), like a
                            // quick draw. Let go mid-clip, the USP floated back to the hand; reversed it
                            // looked weirder (playtest 10-06-26).
                            if (ms > 0.05f && segT > 0.25f)
                            {
                                Mw2Viewmodel.PilotSlot = MeleeSlot;
                                Mw2Viewmodel.PilotFrac = Mathf.Min((segT - 0.25f) / ms, 1f);
                            }
                        }
                        else if (Underbarrel(w))
                        {
                            // The underbarrel: switched to (MW2 raises it into view), fired - a grenade, or
                            // two shells - and back to the rifle (playtest 10-05-26: never shown).
                            Ads = false;
                            bool shotgun = w.Contains("_shotgun");
                            if (!altOn && segT > 0.2f && segT < 1.0f) { altOn = true; Mw2Viewmodel.SwapsExpected = 1; bridge.ToggleAlternate(); }
                            Attack = altOn && ((segT > 1.2f && segT < 1.3f) || (shotgun && segT > 2.0f && segT < 2.1f));
                            if (altOn && segT > segs[seg].dur - 0.7f) { altOn = false; Mw2Viewmodel.SwapsExpected = 1; bridge.ToggleAlternate(); }
                        }
                        else if (w.StartsWith("riotshield"))
                        {
                            // The riot shield: fire is its bash - two of them.
                            Ads = false;
                            Attack = (segT > 0.3f && segT < 0.4f) || (segT > 1.05f && segT < 1.15f);
                        }
                        else if (w.Contains("_akimbo"))
                        {
                            // Akimbo: both guns (the ADS button is the left trigger). Pistols and 1887s
                            // alternate a shot at a time; the automatics keep both going in overlapping
                            // bursts (playtest 10-05-26: "keep shooting the uzis").
                            bool semi = w.StartsWith("deserteagle") || w.StartsWith("usp") || w.StartsWith("beretta_") || w.StartsWith("coltanaconda") || w.StartsWith("model1887");
                            // (playtest 10-05-26: fire all the way through, the bursts looked weird.)
                            float cyc = segT < 0.15f ? -1f : (segT - 0.15f) % 0.3f;
                            Attack = semi ? cyc >= 0f && cyc < 0.08f : cyc >= 0f;
                            Ads = semi ? cyc >= 0.15f && cyc < 0.23f : segT > 0.2f;
                        }
                        else
                        {
                            // Sights up on the last hip gun before the ADS act, down on the last ADS gun before
                            // the next - the swap itself never changes the zoom (the Uzi's zoom-in, 10-05-26).
                            int nextAct = sameNext ? segs[seg + 1].act : -1;
                            float left = segs[seg].dur - segT;
                            bool bigSight = w.Contains("_thermal") || w.Contains("_acog") || w.StartsWith("cheytac") || w.StartsWith("barrett") || w.StartsWith("wa2000") || w.StartsWith("m21");
                            Ads = act == 2 ? !(nextAct != 2 && left < 0.4f) : nextAct == 2 && left < 0.4f && !bigSight;
                            // Firing on through the swaps (playtest 10-06-26): from a gun that was firing the next
                            // starts at once, and into a gun that will fire the trigger stays down to the swap.
                            int prevAct = seg > 0 && segs[seg - 1].family == segs[seg].family ? segs[seg - 1].act : -1;
                            bool fromFire = prevAct == 1 || prevAct == 2, intoFire = nextAct == 1 || nextAct == 2;
                            float lead = fromFire ? 0f : act == 2 ? 0.1f : 0.15f;
                            // Held, let go one frame per pull so the semi-autos pull again (the autos barely
                            // notice; playtest 10-05-26: the bursts looked weird) - a pull per fire animation
                            // (playtest 10-07-26: the Anaconda and the Barrett went "whacky" pulled every 0.15 s,
                            // their kick cut off by the next shot).
                            float fireSecs = bridge.PilotSlotSeconds(Ads ? AdsFireSlot : FireSlot);
                            if (fireSecs < 0.05f) fireSecs = bridge.PilotSlotSeconds(FireSlot);
                            float pull = Mathf.Clamp(fireSecs, 0.15f, 0.9f);
                            Attack = segT > lead && (intoFire || left > 0.15f) && ((segT - lead) % pull) > 1f / 60f;
                        }
                    }
                    else if (act == 3)
                    {
                        Attack = Ads = false;
                        float secs = bridge.PilotSlotSeconds(ReloadSlot);
                        if (secs > 0.05f)
                        {
                            // A swap may have moved it to where the new gun's pose matches (motion matching).
                            if (Mw2Viewmodel.PilotSlot == ReloadSlot) phase = Mw2Viewmodel.PilotFrac;
                            phase += dt / secs;
                            if (phase >= 1f) phase -= 1f;
                            Mw2Viewmodel.PilotSlot = ReloadSlot;
                            Mw2Viewmodel.PilotFrac = phase;
                        }
                        else Mw2Viewmodel.PilotSlot = -1;
                    }
                    else { Attack = Ads = false; Mw2Viewmodel.PilotSlot = -1; }
                    segT += dt;
                    if (segT >= segs[seg].dur)
                    {
                        if (sameNext)
                        {
                            int next = segs[seg + 1].act;
                            // After the knife the arms are out of the frame: the next pistol is drawn with
                            // MW2's raise, a real switch (glided in from off-screen, the screen sat empty a
                            // quarter second, then it popped up - 10-06-26).
                            if (segs[seg].weapon.Contains("_tactical")) { Attack = Ads = false; enter(seg + 1); mode = 3; phase2 = 0.55f; restT = 0f; } // (from where the draw enters the frame: from its start the screen sat empty half a second)
                            else if (act != 3 && next == 3) { Attack = Ads = false; mode = 7; restT = 0f; }
                            else enter(seg + 1);
                        }
                        else
                        {
                            Attack = Ads = false;
                            mode = act == 3 ? 1 : 2; // the reload plays out; otherwise straight to the switch
                            phase2 = 0f; restT = 0f;
                        }
                    }
                }
                else if (mode == 1)
                {
                    float secs = bridge.PilotSlotSeconds(ReloadSlot);
                    if (secs <= 0.05f) { mode = 2; phase2 = 0f; restT = 0f; return; }
                    if (Mw2Viewmodel.PilotSlot == ReloadSlot) phase = Mw2Viewmodel.PilotFrac;
                    phase += dt / secs;
                    if (phase >= 1f) { phase2 = 0f; restT = 0f; mode = bridge.PilotSlotSeconds(EndSlot) > 0.05f ? 5 : 2; return; }
                    Mw2Viewmodel.PilotSlot = ReloadSlot;
                    Mw2Viewmodel.PilotFrac = phase;
                }
                else if (mode == 7)
                {
                    // Sights down a moment, then the next gun opens the reload act from the start.
                    Mw2Viewmodel.PilotSlot = -1;
                    restT += dt;
                    if (restT >= 0.35f)
                    {
                        enter(seg + 1);
                        phase = 0f; phase2 = 0f; segT = 0f;
                        mode = bridge.PilotSlotSeconds(StartSlot) > 0.05f ? 4 : 0;
                    }
                }
                else if (mode == 2)
                {
                    // The family's gun goes away (MW2's drop); the last one just rests and the take ends.
                    if (seg + 1 >= segs.Count)
                    {
                        Mw2Viewmodel.PilotSlot = -1;
                        restT += dt;
                        if (restT >= 0.8f) stepDone = true;
                        return;
                    }
                    // A beat at rest before the switch (sights down, if they were up).
                    if (restT < 0.4f) { restT += dt; Mw2Viewmodel.PilotSlot = -1; return; }
                    float ds = bridge.PilotSlotSeconds(DropSlot);
                    phase2 += ds > 0.05f ? dt / ds : 1f;
                    Mw2Viewmodel.PilotSlot = DropSlot;
                    Mw2Viewmodel.PilotFrac = Mathf.Min(phase2, 1f);
                    if (phase2 >= 1f)
                    {
                        enter(seg + 1);
                        phase2 = 0f; mode = 3;
                        Mw2Viewmodel.PilotSlot = RaiseOf(segs[seg].weapon);
                        Mw2Viewmodel.PilotFrac = 0f;
                    }
                }
                else if (mode == 3)
                {
                    // The next family's gun comes up (MW2's raise)...
                    int raise = RaiseOf(segs[seg].weapon);
                    float rs = bridge.PilotSlotSeconds(raise);
                    phase2 += rs > 0.05f ? dt / rs : 1f;
                    Mw2Viewmodel.PilotSlot = raise;
                    Mw2Viewmodel.PilotFrac = Mathf.Min(phase2, 1f);
                    if (phase2 >= 1f) { mode = 6; restT = 0f; }
                }
                else if (mode == 6)
                {
                    // ...idles a moment, then its first act (a reload act opens with reload_start).
                    Mw2Viewmodel.PilotSlot = -1;
                    restT += dt;
                    if (restT >= (segs[seg].weapon == "rpg_mp" ? 0.05f : 0.5f)) // the RPG straight into its reload
                    {
                        phase = 0f; phase2 = 0f; segT = 0f;
                        mode = act == 3 && bridge.PilotSlotSeconds(StartSlot) > 0.05f ? 4 : 0;
                    }
                }
                else
                {
                    // Reload start (4) into the relay, or reload end (5) back to rest before the drop.
                    int slot = mode == 4 ? StartSlot : EndSlot;
                    float es = bridge.PilotSlotSeconds(slot);
                    phase2 += es > 0.05f ? dt / es : 1f;
                    Mw2Viewmodel.PilotSlot = slot;
                    Mw2Viewmodel.PilotFrac = Mathf.Min(phase2, 1f);
                    if (phase2 >= 1f)
                    {
                        if (mode == 4) { mode = 0; phase = 0f; }
                        else { mode = 2; phase2 = 0f; restT = 0f; }
                    }
                }
            }, null, () => { Mw2Viewmodel.PilotSlot = -1; Mw2Viewmodel.SwapBlend = 0f; Attack = Ads = Melee = false; });
            // The finale: MW2's own RPG reload (the rocket in from below - the sim's, not forced). The
            // shot through the middle was cut (10-06-26): it never flew straight.
            float loadedAt = -1f;
            Add("anim_go_02_finale", 9f, () =>
            {
                Repark(); Mw2Viewmodel.PilotSlot = -1; Mw2Viewmodel.SwapBlend = 0f; SubLabel = "rpg_reload"; loadedAt = -1f;
                if (bridge.WeaponName != "rpg_mp") { bridge.GiveWeapon("rpg_mp"); bridge.PilotReady(); bridge.PilotSpendClip(); } // short test takes
                // (else: its reload is already under way from the launcher beat - readied, it was cut and
                // restarted, and the rocket blinked out for two frames, playtest 10-06-26)
            }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                if (vista != null) AimAt(vista.Value.yaw, vista.Value.pitch); else HoldFrame();
                BattleTick();
                // MW2 auto-reloads the empty tube (often already under way from the launcher beat).
                // Not fired (playtest 10-06-26: "its not flying straight"): loaded, a beat to look,
                // then the ending puts it away.
                if (bridge.Last.clip > 0 && loadedAt < 0f) loadedAt = t;
                if (loadedAt < 0f && t > 0.05f && t < 0.15f) Reload = true; else Reload = false;
                if (loadedAt >= 0f && t > loadedAt + 1.4f) stepDone = true;
            }, null, () => { Attack = Reload = false; });
            // The ending (playtest 10-06-26): the RPG put away, the ACR drawn - holo sight, suppressor, Red
            // Tiger - and shown off, then the nuke called in without its "acquired" fanfare. MW2's own
            // 10 s countdown on the ACR, and the wave takes the cameraman too: MW2's death cam on his
            // body ends the take.
            float nukeT0 = -1f, diedAt = -1f, endT = 0f, calledAt = -1f;
            int deathFrame = -1;
            bool rearMarked = false;
            float reloadAt = -1f, backAt = -1f;
            int endStage = 0;
            string endGun = "masada_eotech_silencer_mp";
            Add("anim_go_03_nuke", 40f, () =>
            {
                SubLabel = "acr";
                nukeT0 = -1f; diedAt = -1f; calledAt = -1f; endStage = 0; endT = 0f; nuked = false; deathFrame = -1; rearMarked = false; Mw2Cinema.White = 0f; reloadAt = -1f; backAt = -1f;
                NukeJob.ShowcaseTimer = 0f;
                NukeJob.LastDetonation = -1f;
                NukeJob.StartedAt = -1f;
                NukeJob.HideCountdown = true; // (playtest 10-06-26: no countdown on the reel - "way cleaner")
                Mw2Cinema.ShowStreakHud = true;
                // (the whole step: counted in real time, the recorder's slower clock outran a fixed
                // window and the take ended as "player died", 10-06-26)
                allowDeathUntil = float.PositiveInfinity;
                if (Native.WeaponIndex(endGun) == 0) endGun = "masada_mp";
                // Fall camo (playtest 10-06-26; was Red Tiger).
                uint.TryParse(Mw2Menus.TableLookup("mp/camoTable.csv", 1, "orange_fall", 0), out uint camoId);
                // (the menus' tables may not be up in a pilot run: the id only keys the viewmodel cache -
                // the name paints the gun)
                Native.SetCamo(Native.WeaponIndex(endGun), "orange_fall", camoId != 0 ? camoId : 8u);
            }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                var me = bridge.LocalBody;
                if (diedAt < 0f) HoldFrame();
                // The battle keeps ticking after the cameraman goes - the soldier's own death has to
                // play out (untouched, he stood on in the dust, 10-06-26) - but nobody new comes in.
                if (NukeJob.LastDetonation > 0f) nuked = true;
                BattleTick();
                if (endStage == 0)
                {
                    // The RPG away (MW2's putaway)...
                    float ds = bridge.PilotSlotSeconds(16);
                    Mw2Viewmodel.PilotSlot = 16;
                    Mw2Viewmodel.PilotFrac = ds > 0.05f ? Mathf.Clamp01(t / ds) : 1f;
                    if (ds <= 0.05f || t >= ds)
                    {
                        endStage = 1; endT = t;
                        Mw2Viewmodel.SwapsExpected = 1;
                        bridge.GiveWeapon(endGun);
                        bridge.PilotReady();
                    }
                }
                else if (endStage == 1)
                {
                    // ...the ACR up (its plain draw: the rack is the inspect's last beat)...
                    float rs = bridge.PilotSlotSeconds(13);
                    Mw2Viewmodel.PilotSlot = 13;
                    Mw2Viewmodel.PilotFrac = rs > 0.05f ? Mathf.Clamp01((t - endT) / rs) : 1f;
                    if (rs <= 0.05f || t - endT >= rs) { endStage = 5; endT = t; Mw2Viewmodel.PilotSlot = -1; }
                }
                else if (endStage == 5)
                {
                    // ...fired - from the hip, then down the holo - the Fall camo under the muzzle flash,
                    // then reloaded (playtest 10-06-26)...
                    float ft = t - endT;
                    const float FireEnd = 2.7f;
                    if (ft < FireEnd) bridge.PilotFillClip();
                    Ads = ft > 1.2f && ft < FireEnd;
                    Attack = ft > 0.15f && ft < FireEnd && ((ft - 0.15f) % 0.15f) > 1f / 60f;
                    if (ft > FireEnd + 0.1f && reloadAt < 0f) { reloadAt = ft; bridge.PilotSpendClip(); }
                    Reload = reloadAt >= 0f && ft > reloadAt + 0.05f && ft < reloadAt + 0.15f;
                    float rl = bridge.PilotSlotSeconds(9); // RELOAD
                    if (reloadAt >= 0f && ft > reloadAt + 0.2f + Mathf.Max(rl, 1.5f) + 0.35f)
                    {
                        // ...and the nuke called (no aimed hang at the end).
                        Attack = Ads = Reload = false;
                        endStage = 2; endT = t;
                        SubLabel = "nuke";
                        bridge.Streaks.AdminGive(Native.StreakId("nuke"), silent: true);
                    }
                }
                else if (endStage == 2)
                {
                    if (t - endT > 0.3f && t - endT < 0.35f) StreakPulse = true;
                    if (NukeJob.StartedAt > 0f && calledAt < 0f) calledAt = t;
                    // MW2 brings the ACR back up itself once the trigger's clicked: the inspect starts as
                    // that draw ends - one draw, no second swap (two in a row were "TOO MUCH", 10-06-26).
                    if (calledAt >= 0f && backAt < 0f && t > calledAt + 0.2f && bridge.WeaponName == endGun) backAt = t;
                    float rs = bridge.PilotSlotSeconds(13); // RAISE
                    if (backAt >= 0f && t > backAt + Mathf.Max(rs, 0.6f)) { endStage = 4; endT = t; }
                }
                else if (endStage == 4)
                {
                    // ...inspected through the countdown (MW2 has none: the rig turned by hand - its left
                    // side, the right side canted, then top-down for the rack, and back).
                    Inspect(t - endT);
                    // How near the gun comes to the lens (playtest 10-07-26: the stock's inside showed on the rack).
                    // The stock's cut-off end in the frame (marked at rest, the inspect's first frame).
                    if (!rearMarked) { rearMarked = true; Plugin.Log.LogInfo($"[inspect] rear of the gun: {bridge.ViewmodelForTest.MarkRear()} vertices"); }
                    if (Time.frameCount % 6 == 0)
                    {
                        int rear = bridge.ViewmodelForTest.RearOnScreen(Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad), out float rearZ);
                        int seeIn = bridge.ViewmodelForTest.SeeInside(Mathf.Tan(Mw2View.Vertical(Mw2View.CgFov) * 0.5f * Mathf.Deg2Rad), 96, 54, out int gunRays, out _);
                        Plugin.Log.LogInfo($"[inspect] t {t - endT:F2} frame {Mw2Cinema.Frame}: rear end on screen {rear} see-through {seeIn} of {gunRays} gun rays euler {Mw2Viewmodel.InspectEuler} shift {Mw2Viewmodel.InspectShift} slot {Mw2Viewmodel.PilotSlot} {Mw2Viewmodel.PilotFrac:F2}");
                    }
                }
                if (NukeJob.LastDetonation > 0f && nukeT0 < 0f) nukeT0 = t;
                if (deathFrame >= 0)
                {
                    int since = Mw2Cinema.Frame - deathFrame;
                    Mw2Cinema.White = since < 30 ? 1f : Mathf.Clamp01(1f - (since - 30) / 30f);
                }
                // As the wave hits (MW2 kills 1.5 s after the flash): the cameraman goes too.
                // (1.25: under the white-out still - at 1.5 the nuke's red vision showed ~5 frames first)
                if (nukeT0 >= 0f && diedAt < 0f && t > nukeT0 + 1.25f && me != null && me.healthComponent != null)
                {
                    diedAt = t;
                    // Off on the body now: Admin only re-applies it next physics tick, and the kill met
                    // god mode still on (10-06-26: the take ended with him alive in the dust).
                    bridge.Admin.God = false;
                    me.healthComponent.godMode = false;
                    me.healthComponent.Suicide();
                    // RoR2 flashes its own red death screen for ~10 frames: the recorder holds white over it.
                    deathFrame = Mw2Cinema.Frame;
                    // Everything dies, and nothing more comes (the nuke's own kill runs from the player's
                    // body - gone a moment before it; Lesser Wisps were still flying in the death cam).
                    SetConVar("director_combat_disable", "1");
                    Mw2Admin.KillMonsters();
                    // MW2's nuke takes everyone - the background soldier too (playtest 10-06-26: "did our
                    // background character survive the nuke? impossible"). He's on our side, so the
                    // nuke's own kill (the enemies) passed him over.
                    var hc = bot != null && bot.Body != null ? bot.Body.healthComponent : null;
                    if (hc != null && hc.alive) { hc.godMode = false; hc.Suicide(); }
                }
                if (diedAt >= 0f && t > diedAt + 0.3f && t < diedAt + 1f && me != null && me.healthComponent != null && me.healthComponent.alive)
                {
                    me.healthComponent.godMode = false;
                    me.healthComponent.Suicide();
                }
                // The take ends on the death cam, pulled out over the body (playtest 10-07-26: "the video is
                // cutting off before the nuke hits and kills our character and shows his third person death
                // cam that pulls out" - it ended on the music's loop, 112.8 s, as the wave hit): 3.8 s after
                // the death (white 1 s, down 1 s, out by 2.2 s, a held look), before RoR2's game over at 4.5.
                // The menu music loops on seamlessly past its 112.8 s and fades over the last 1.5 s.
                const int DeathCamFrames = 228;
                if (deathFrame >= 0)
                {
                    int left = deathFrame + DeathCamFrames - Mw2Cinema.Frame;
                    Mw2Cinema.MusicLevel = 0.3f * Mathf.Clamp01(left / 90f);
                    if (left <= 0) stepDone = true;
                }
                if (diedAt >= 0f && t > diedAt + 15f) stepDone = true;
            }, null, () => { Mw2Cinema.MusicLevel = 0.3f; Reload = false; Mw2Cinema.White = 0f; Mw2Viewmodel.PilotGlide = 0.12f; NukeJob.ShowcaseTimer = 0f; NukeJob.HideCountdown = false; Mw2Cinema.ShowStreakHud = false; Ads = false; Mw2Viewmodel.PilotSlot = -1; Mw2Viewmodel.InspectEuler = Mw2Viewmodel.InspectShift = Vector3.zero; });
            Add("anim_go_zz_stop", 1f, () => Mw2Cinema.Stop(), null);

            // ---- the showcase reel (pilot "cinema", playtest 10-05-26): one frame, one backdrop, the guns
            // and items keep switching. cine_scout finds the vista and parks there; every cine_ step
            // after it is one clip in the recording (tools/cinema_cut.py splits them).
            Add("cine_scout", 9f, () =>
            {
                var b = bridge.LocalBody;
                var seat = b != null ? b.currentVehicle : null;
                if (seat != null) seat.EjectPassenger(b.gameObject);
                if (!bridge.Active) bridge.Toggle(b);
                bridge.Admin.God = true;
                bridge.Admin.InfiniteAmmo = true;
                SetConVar("director_combat_disable", "1");
                Mw2Admin.KillMonsters();
                // No perks: Sleight of Hand halves every reload, and the reel shows them at MW2's own
                // pace (playtest 10-05-26: "we shouldn't be using sleight of hand").
                Mw2Perks.Give(b, new string[0]);
                Mw2Cinema.On = true;
                Mw2Cinema.HideHud = Setting("hud", "") != "1";
                Mw2Cinema.Stage(Setting("res", ""), b);
                vistas = Mw2Cinema.Scout(b);
                foreach (var v in vistas) Plugin.Log.LogInfo($"[pilot] cinema vista: {v.feet} yaw {v.yaw} score {v.score:F2} (open {v.open:F2}, drop {v.drop:F2})");
                if (vistas.Count == 0) Plugin.Log.LogWarning("[pilot] cinema: no vista found - staying at the spawn");
            }, t =>
            {
                // A look at each candidate (2 s apart), then the pick (spot=N, default the best).
                int i = (int)(t / 2f);
                if (i < vistas.Count && i != scoutShown) { scoutShown = i; Park(vistas[i]); }
                if (Mathf.Repeat(t, 2f) > 1.6f && Mathf.Repeat(t - Time.unscaledDeltaTime, 2f) <= 1.6f && i < vistas.Count) Shot($"vista_{i}");
            }, null, () =>
            {
                int pick = int.TryParse(Setting("spot", ""), out int sp) ? sp : 0;
                if (vistas.Count > 0) vista = vistas[Mathf.Clamp(pick, 0, vistas.Count - 1)];
                // vista=x,y,z,yaw: an exact spot (the scout's order isn't the same every run). Distant
                // Roost's default: out over the abyss, only fog and the far ridges (playtest 10-05-26).
                string fixedSpot = Setting("vista", "") ?? "";
                if (fixedSpot.Length == 0 && SceneManager.GetActiveScene().name == "blackbeach" && Setting("spot", "") == null && Setting("scouted", "") == null) fixedSpot = "222.83;-247.63;1.16;32";
                var f = fixedSpot.Split(';');
                if (f.Length == 4 && float.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vx)
                    && float.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vy)
                    && float.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vz)
                    && float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vyaw))
                    vista = new Mw2Cinema.Vista { feet = new Vector3(vx, vy, vz), yaw = vyaw, pitch = 2f };
                if (vista != null) Park(vista.Value);
                Plugin.Log.LogInfo($"[pilot] cinema: screen {Screen.width}x{Screen.height}, vista {pick}");
                if (!Mw2Cinema.Start(outDir)) Plugin.Log.LogWarning("[pilot] cinema: not recording");
            });
            // Framing sweep (yawscan=1): from the vista, a shot every 10 degrees across 110 degrees, to
            // choose the framing (playtest 10-05-26: only the fog and the birds, no cliff in the frame).
            Add("cine_yawscan", 14f, () => { Repark(); bridge.GiveWeapon("m4_mp"); }, t =>
            {
                holdUntil = Time.unscaledTime + 0.1f;
                if (vista == null) return;
                int k = Mathf.Min((int)(t / 1.2f), 11);
                float yaw = vista.Value.yaw - 60f + k * 10f;
                AimAt(yaw, vista.Value.pitch);
                if (Mathf.Repeat(t, 1.2f) > 1.0f && Mathf.Repeat(t - 1f / 60f, 1.2f) <= 1.0f) Shot($"yaw_{yaw:F0}");
            });
            // QA sweep (pilot "qa_", playtest 10-05-26: "there's things like this we never did a pass over"):
            // every gun and every attachment you can see, three shots each - from the hip, aimed down
            // the sights, mid-reload - for contact sheets. qa=name1+name2 narrows it to those guns.
            {
                var qaBase = new[]
                {
                    "m4", "famas", "scar", "tavor", "fal", "m16", "masada", "fn2000", "ak47",
                    "mp5k", "ump45", "kriss", "p90", "uzi",
                    "sa80", "rpd", "mg4", "aug", "m240",
                    "cheytac", "barrett", "wa2000", "m21",
                    "riotshield",
                    "pp2000", "glock", "beretta393", "tmp",
                    "spas12", "aa12", "striker", "m1014", "model1887",
                    "usp", "coltanaconda", "deserteagle", "beretta", "deserteaglegold",
                    "at4", "m79", "stinger", "javelin", "rpg",
                };
                var qaAtt = new[] { "", "acog", "reflex", "eotech", "thermal", "silencer", "heartbeat", "gl", "shotgun", "grip", "tactical", "akimbo" };
                string only = Setting("qa", "") ?? "";
                ThirdOnlyTest = Setting("thirdonly", "") == "1";
                bool qaCleared = false;
                foreach (var g in qaBase)
                {
                    if (only.Length > 0 && !only.Split('+', ';').Contains(g)) continue;
                    foreach (var a in qaAtt)
                    {
                        string gun = a.Length == 0 ? $"{g}_mp" : $"{g}_{a}_mp";
                        bool akimbo = a == "akimbo", shield = g == "riotshield";
                        Add($"qa_{gun.Replace("_mp", "")}", 3.6f, () =>
                        {
                            if (Native.WeaponIndex(gun) == 0) { stepDone = true; return; }
                            // No monsters in the shots (playtest 10-05-26).
                            if (!qaCleared) { qaCleared = true; SetConVar("director_combat_disable", "1"); Mw2Admin.KillMonsters(); }
                            bridge.Admin.God = true;
                            bridge.GiveWeapon(gun);
                            bridge.PilotReady();
                            bridge.PilotResetSway();
                        }, t =>
                        {
                            holdUntil = Time.unscaledTime + 0.1f;
                            HoldFrame();
                            Ads = !akimbo && !shield && t > 1.2f && t < 2.4f;
                            if (!shield && t > 2.45f && t < 2.5f) bridge.PilotSpendClip();
                            Reload = !shield && t > 2.55f && t < 2.65f;
                        }, new[] { 1.0f, 2.15f, 3.25f }, () => { Ads = false; Reload = false; });
                    }
                }
            }
            // The guns, MW2's Create-a-Class order: assault rifles, SMGs, LMGs, snipers, riot shield,
            // machine pistols, shotguns, handguns, launchers. Raise, a short burst from the hip, ADS
            // and a burst, settle.
            var cineGuns = new[]
            {
                "m4_mp", "famas_mp", "scar_mp", "tavor_mp", "fal_mp", "m16_mp", "masada_mp", "fn2000_mp", "ak47_mp",
                "mp5k_mp", "ump45_mp", "kriss_mp", "p90_mp", "uzi_mp",
                "sa80_mp", "rpd_mp", "mg4_mp", "aug_mp", "m240_mp",
                "cheytac_mp", "barrett_mp", "wa2000_mp", "m21_mp",
                "riotshield_mp",
                "pp2000_mp", "glock_mp", "beretta393_mp", "tmp_mp",
                "spas12_mp", "aa12_mp", "striker_mp", "m1014_mp", "model1887_mp",
                "usp_mp", "coltanaconda_mp", "deserteagle_mp", "beretta_mp", "deserteaglegold_mp",
                "usp_akimbo_mp", "tmp_akimbo_mp", "model1887_akimbo_mp", "usp_tactical_mp",
                "at4_mp", "m79_mp", "stinger_mp", "javelin_mp", "rpg_mp",
                // Attachments, on the AK-47.
                "ak47_acog_mp", "ak47_reflex_mp", "ak47_eotech_mp", "ak47_thermal_mp", "ak47_silencer_mp", "ak47_heartbeat_mp",
            };
            int cineN = 0;
            foreach (var g in cineGuns)
            {
                string gun = g;
                bool launcher = gun.StartsWith("at4") || gun.StartsWith("m79") || gun.StartsWith("stinger") || gun.StartsWith("javelin") || gun.StartsWith("rpg");
                bool shield = gun.StartsWith("riotshield");
                bool single = launcher || gun.StartsWith("cheytac") || gun.StartsWith("barrett") || gun.StartsWith("wa2000") || gun.StartsWith("m21")
                    || gun.StartsWith("spas") || gun.StartsWith("striker") || gun.StartsWith("m1014") || gun.StartsWith("model1887") || gun.StartsWith("coltanaconda")
                    || gun.StartsWith("deserteagle") || gun.StartsWith("usp") || gun.StartsWith("beretta_") || gun.StartsWith("fal") || gun.StartsWith("m16") || gun.StartsWith("m21");
                // Every gun on the same clock, so an edit can cut gun to gun at the same moment (the
                // raise at 0, ADS at 2.2, the reload at 4.6 - playtest 10-05-26: one animation running on
                // through every gun). Nothing fired: shooting and its effects are for his gameplay.
                Add($"cine_{++cineN:00}_{gun.Replace("_mp", "")}", CineLength(gun), () => { Repark(); bridge.GiveWeapon(gun); bridge.PilotResetSway(); }, t =>
                {
                    holdUntil = Time.unscaledTime + 0.1f;
                    HoldFrame();
                    Ads = !shield && t > 2.2f && t < 4.2f;
                    // A part-used magazine, so MW2 plays its reload (it won't reload a full one).
                    if (!shield && t > 4.4f && t < 4.45f) bridge.PilotSpendClip();
                    Reload = !shield && t > 4.6f && t < 4.7f;
                }, null, () => { Ads = Reload = false; });
            }
            // Underbarrels: the AK-47's grenade launcher and shotgun, switched on and fired.
            foreach (var (a, gl) in new[] { ("gl", true), ("shotgun", false) })
            {
                string gun = $"ak47_{a}_mp";
                bool isGl = gl;
                Add($"cine_{++cineN:00}_ak47_{a}", 5.5f, () => { Repark(); bridge.GiveWeapon(gun); bridge.PilotResetSway(); }, t =>
                {
                    holdUntil = Time.unscaledTime + 0.1f;
                    HoldFrame();
                    if (t > 1.0f && t < 1.05f) bridge.ToggleAlternate();
                    Ads = t > 2.4f && t < 4.4f;
                }, null, () => { Ads = false; });
            }
            Add("cine_zz_stop", 1f, () => Mw2Cinema.Stop(), null);
            Add("end", 2f, null, null, new[] { 1f });
            Plugin.Log.LogInfo($"[pilot] {steps.Count} steps (filter '{filter}'): {string.Join(", ", steps.Select(x => x.Name).Take(12))}");
        }

        /// Seconds of MW2's call-in (weapon raise + click/throw) before the streak itself starts.
        const float CallIn = 1.6f;

        /// The mouse pulling down: RoR2's camera rig aim tilted down by `deg`.
        static void PushAimDown(float deg)
        {
            var b = bridge.LocalBody;
            if (b == null) return;
            if (!StreakJob.PitchYaw(b, out var py) || py.x + deg > 88f) return;
            StreakJob.SetAim(b, Quaternion.Euler(py.x + deg, py.y, 0f) * Vector3.forward);
        }

        /// Killstreak route checks from one open spot (open_ground): back on it for each streak, no walking.
        static bool StayPut, thermalPressed;
        static Vector3 openSpot;

        static void Streak(string name, float secs, float[] shots, Action<float> tick = null, string stepName = null)
        {
            var later = shots.Select(x => x + CallIn).ToList();
            later.InsertRange(0, new[] { 0.6f, 1.0f, 1.25f, 1.45f }); // the call-in item coming up (the laptop opening)
            shots = later.ToArray();
            secs += CallIn;
            var inner = tick;
            tick = t => { if (t >= CallIn) inner?.Invoke(t - CallIn); };
            stepName = stepName ?? $"ks_{name}";
            Add(stepName, secs, () =>
            {
                bridge.Streaks.EndAllForTest();
                if (StayPut && bridge.LocalBody != null) TeleportHelper.TeleportBody(bridge.LocalBody, openSpot + Vector3.up * 0.3f);
                Mw2Strike.StreakRounds = Mw2Strike.StreakRoundHits = 0;
                bridge.Admin.God = true; // the body stands open while a ride camera has the view
                if (reel) { Mw2Admin.KillMonsters(); Mw2Admin.SpawnRing(bridge.LocalBody, null, 22f, true); }
                uint id = Native.StreakId(name);
                bridge.Streaks.AdminGive(id);
                StreakPulse = true;
                Write($"{{\"streak\":\"{name}\",\"id\":{id}}}");
            }, tick, shots, () => { Plugin.Log.LogInfo($"[pilot] {stepName}: killstreak rounds {Mw2Strike.StreakRounds}, on enemies {Mw2Strike.StreakRoundHits}"); bridge.Streaks.EndAllForTest(); bridge.Admin.God = false; });
            if (steps.Count > 0 && steps[steps.Count - 1].Name == stepName) steps[steps.Count - 1].Roam = true;
        }

        // ------------------------------------------------------------ playing like a player

        // Roam: what a player does while a streak runs. Pick something to look at (an aircraft
        // in the air, else a visible enemy, else one of the longest open sightlines, and back
        // toward the start once it has wandered off), turn onto it with a mouse-like spring and
        // a little hand jitter, walk/strafe when there's room (never off a ledge), and shoot
        // enemies in bursts once they're under the crosshair. Off while a ride camera owns the view.
        static float yawVel, pitchVel, nextPick, holdUntil, strafeUntil, strafe;
        static Vector3 home, scanPoint;
        static bool hasHome, roamFired, roamAds, walking, firing;
        static HurtBox enemy, seen;
        static float reactUntil, burstUntil, pauseUntil;
        static Vector3 aimError;
        static readonly System.Random rng = new System.Random(7);
        static float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        /// Turn on to have Roam engage enemies (off: it watches aircraft and the horizon only).
        static bool Fight = false;

        static void Roam(CharacterBody body)
        {
            if (body == null || body.inputBank == null || Time.unscaledTime < holdUntil) return;
            float dt = Time.unscaledDeltaTime;
            if (bridge.Streaks.FreezesPlayer || dt <= 0f) { yawVel = pitchVel = 0f; return; }
            var eye = body.inputBank.aimOrigin;
            var feet = body.footPosition;
            if (!hasHome) { home = feet; hasHome = true; }
            int world = LayerIndex.world.mask;

            // 1. aircraft, 2. enemy, 3. scan point.
            Mw2Vehicle air = null; float bd = float.MaxValue;
            // Only aircraft you can actually see (a player doesn't stare at a cliff the jet is behind).
            foreach (var v in Mw2Vehicle.All)
            {
                float d = (v.Position - eye).sqrMagnitude;
                var rel = v.Position - eye;
                float elev = Elev(rel);
                if (elev >= 65f) continue;
                bool seen = Visible(eye, v.Position + Vector3.up * 2f, world);
                if (d < bd && (seen ? d < 600f * 600f : d < 150f * 150f)) { bd = d; air = v; }
            }
            if (enemy != null && (enemy.healthComponent == null || !enemy.healthComponent.alive || !Visible(eye, enemy.transform.position, world))) enemy = null;
            if (Time.unscaledTime >= nextPick)
            {
                nextPick = Time.unscaledTime + R(1.4f, 3.2f);
                // The pilot films killstreaks; fighting got in the way (playtest 10-02-26), so it doesn't.
                var e = Fight ? NearestEnemy(body, eye, world) : null;
                // A person needs a moment to notice someone new before turning onto them.
                if (e != null && e != seen) { seen = e; reactUntil = Time.unscaledTime + R(0.18f, 0.35f); aimError = UnityEngine.Random.insideUnitSphere * 0.6f; }
                enemy = e;
                scanPoint = ScanPoint(eye, feet, world);
            }
            Vector3 lookAt; string kind;
            bool reacting = enemy != null && Time.unscaledTime < reactUntil;
            if (air != null) { lookAt = air.Position; kind = "air"; }
            else if (Mw2Killstreaks.UavInSky is Vector3 uav) { lookAt = uav; kind = "air"; } // the UAV, way up
            else if (enemy != null && !reacting)
            {
                // Aim error shrinks while tracking (settling onto them).
                aimError = Vector3.MoveTowards(aimError, Vector3.zero, 0.5f * dt);
                lookAt = enemy.transform.position + aimError;
                kind = "enemy";
            }
            else { lookAt = scanPoint; kind = "scan"; }

            // Turn onto it: critically damped spring on yaw/pitch error, like a hand on a mouse.
            var to = lookAt - eye;
            var aim = ViewForward;
            var flatTo = new Vector3(to.x, 0f, to.z);
            float yawErr = flatTo.sqrMagnitude > 1f ? Vector3.SignedAngle(new Vector3(aim.x, 0f, aim.z), flatTo, Vector3.up) : 0f; // + = right
            float pitchErr = Elev(aim) - Mathf.Clamp(Elev(to), -55f, 65f); // + = target below
            float w = kind == "enemy" ? 6f : kind == "air" ? 5f : 4f;
            float zeta = kind == "enemy" ? 0.75f : 1f; // a little overshoot on a flick, like a hand
            yawVel += (w * w * yawErr - 2f * zeta * w * yawVel) * dt;
            pitchVel += (w * w * pitchErr - 2f * zeta * w * pitchVel) * dt;
            yawVel = Mathf.Clamp(yawVel, -420f, 420f);
            pitchVel = Mathf.Clamp(pitchVel, -240f, 240f);
            float tt = Time.unscaledTime;
            var jitter = new Vector3(Mathf.PerlinNoise(tt * 0.7f, 3.1f) - 0.5f, Mathf.PerlinNoise(1.7f, tt * 0.6f) - 0.5f, 0f) * 3f;
            Look += new Vector3(pitchVel * dt, -yawVel * dt, 0f) + jitter * dt;

            // Feet: wander the open ground near home; strafe while fighting or watching the sky.
            if (tt >= strafeUntil) { strafeUntil = tt + R(0.8f, 2f); strafe = rng.NextDouble() < 0.35 ? 0f : R(-0.7f, 0.7f); walking = rng.NextDouble() < 0.6; }
            var fwd = new Vector3(aim.x, 0f, aim.z);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            var right = new Vector3(fwd.z, 0f, -fwd.x);
            float f = kind == "scan" && walking && Mathf.Abs(yawErr) < 25f ? 0.8f : 0f;
            float r = kind == "scan" ? strafe * 0.4f : strafe;
            if (f > 0f && (!Clear(eye, fwd, 3f, world) || Ledge(feet, fwd, world))) f = 0f;
            if (r != 0f && (!Clear(eye, right * Mathf.Sign(r), 2f, world) || Ledge(feet, right * Mathf.Sign(r), world))) { r = -r; strafe = -strafe; }
            if (StayPut) { f = 0f; r = 0f; } // route checks: watch from the spot (it walked into a corner)
            Moving = f != 0f || r != 0f;
            Fwd = f; Right = r;

            // Fight like a player: ADS on anyone past ~12 m, open up once the sights are close
            // (5 deg), keep the trigger held while still roughly on them (12 deg), full-auto
            // bursts of 0.6-1.4 s with short breaks to re-centre.
            bool engaged = kind == "enemy" && !bridge.Streaks.CallingIn;
            float err = Mathf.Max(Mathf.Abs(yawErr), Mathf.Abs(pitchErr));
            if (engaged && to.magnitude > 12f && !Ads) { Ads = true; roamAds = true; }
            if (!engaged) firing = false;
            else if (!firing && err < 5f && tt >= pauseUntil) { firing = true; burstUntil = tt + R(0.6f, 1.4f); }
            else if (firing && (err > 12f || tt >= burstUntil)) { firing = false; pauseUntil = tt + R(0.15f, 0.35f); }
            if (firing && !Attack) { Attack = true; roamFired = true; }
        }

        static float Elev(Vector3 v) => Mathf.Asin(Mathf.Clamp(v.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        static bool Visible(Vector3 from, Vector3 to, int mask) => !Physics.Linecast(from, to, mask, QueryTriggerInteraction.Ignore);

        static bool Clear(Vector3 eye, Vector3 dir, float dist, int mask) =>
            !Physics.SphereCast(eye - Vector3.up * 0.6f, 0.4f, dir, out _, dist, mask, QueryTriggerInteraction.Ignore);

        /// More than a 4 m drop just ahead.
        static bool Ledge(Vector3 feet, Vector3 dir, int mask) =>
            !Physics.Raycast(feet + dir * 2f + Vector3.up * 1f, Vector3.down, 5f, mask, QueryTriggerInteraction.Ignore);

        static HurtBox NearestEnemy(CharacterBody me, Vector3 eye, int mask)
        {
            HurtBox best = null; float bd = 60f * 60f;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (!Mw2Strike.IsEnemy(me, cb) || cb.mainHurtBox == null) continue;
                var p = cb.mainHurtBox.transform.position;
                float d = (p - eye).sqrMagnitude;
                if (d < bd && Visible(eye, p, mask)) { bd = d; best = cb.mainHurtBox; }
            }
            return best;
        }

        /// One of the longest open sightlines from a 16-way fan, at a natural height; past 18 m
        /// from home, back toward home instead.
        static Vector3 ScanPoint(Vector3 eye, Vector3 feet, int mask)
        {
            var away = feet - home; away.y = 0f;
            if (away.magnitude > 18f) return home + Vector3.up * 1.5f;
            var dirs = new List<(float dist, Vector3 dir)>();
            for (int i = 0; i < 16; i++)
            {
                var d = Quaternion.Euler(0f, i * 22.5f + R(-8f, 8f), 0f) * Vector3.forward;
                float dist = Physics.Raycast(eye, d, out var hit, 80f, mask, QueryTriggerInteraction.Ignore) ? hit.distance : 80f;
                dirs.Add((dist, d));
            }
            dirs.Sort((a, b) => b.dist.CompareTo(a.dist));
            var pick = dirs[rng.Next(Mathf.Min(4, dirs.Count))];
            float up = R(-4f, 10f); // mostly a touch above the horizon
            var dir = Quaternion.AngleAxis(-up, Vector3.Cross(Vector3.up, pick.dir)) * pick.dir;
            return eye + dir * Mathf.Max(pick.dist, 10f);
        }

        /// The location selector: place the ring (0.4 s in), then confirm the heading (0.9 s).
        static Action<float> Select(bool direction)
        {
            float last = -1f;
            return t =>
            {
                if (last < 0.4f && t >= 0.4f) AttackPulse = true;
                if (direction && last < 0.9f && t >= 0.9f) AttackPulse = true;
                last = t;
            };
        }

        static float camLogAt;
        static void CamLog(float t)
        {
            if (Time.unscaledTime - camLogAt < 0.02f) return;
            camLogAt = Time.unscaledTime;
            var f = ViewForward;
            var k = bridge.Kick;
            Plugin.Log.LogInfo($"[cam] t {t:F3} look ({Look.x:F1},{Look.y:F1}) view pitch {-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg:F1} yaw {Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg:F1} kick ({k.x:F2},{k.y:F2},{k.z:F2}) cam {(Camera.main != null ? Camera.main.transform.eulerAngles.ToString("F1") : "-")}");
        }

        /// Turn the view toward the nearest enemy (melee test).
        static void FaceNearestEnemy(bool away = false)
        {
            var body = bridge.LocalBody;
            if (body == null) return;
            CharacterBody best = null; float bestD = float.MaxValue;
            foreach (var cb in Mw2Strike.Enemies(body))
            {
                float d = Vector3.Distance(cb.corePosition, body.corePosition);
                if (d < bestD) { bestD = d; best = cb; }
            }
            if (best == null) return;
            holdUntil = Time.unscaledTime + 0.1f;
            var to = best.corePosition - body.footPosition; to.y = 0f;
            if (away) to = -to;
            var aim = ViewForward; aim.y = 0f;
            float ang = Vector3.SignedAngle(aim, to, Vector3.up);
            Look += new Vector3(0f, -Mathf.Clamp(ang, -90f, 90f) * 6f * Time.unscaledDeltaTime, 0f);
        }

        /// Care Package: once the crate lands, walk to it and hold Interact.
        static void CrateWalk(float t)
        {
            var crate = bridge.Streaks.FirstCrate;
            var body = bridge.LocalBody;
            if (StayPut || crate == null || body == null || t < 9f) return;
            holdUntil = Time.unscaledTime + 0.1f; // crate walk drives, not Roam
            var to = crate.Pos - body.footPosition; to.y = 0f;
            var aim = ViewForward; aim.y = 0f;
            float ang = Vector3.SignedAngle(aim, to, Vector3.up); // + = right
            Look += new Vector3(0f, -Mathf.Clamp(ang, -90f, 90f) * 3f * Time.unscaledDeltaTime, 0f);
            Moving = true;
            Fwd = to.magnitude > 1.8f ? 1f : 0f;
            Interact = to.magnitude < 2.4f;
        }
    }
}
