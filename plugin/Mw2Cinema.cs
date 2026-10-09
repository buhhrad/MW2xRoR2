using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;
using Path = System.IO.Path;

namespace MW2RoR2
{
    /// The showcase run (pilot `cinema`, playtest 10-05-26: a Borderlands-style reel - same frame, same
    /// backdrop, the guns and items keep switching). Picks a vista on the stage (open view off an
    /// edge), parks the player there in first person, and records every frame at a fixed 60 fps
    /// (Time.captureFramerate: the game steps 1/60 s per frame however long a frame takes to save)
    /// straight into ffmpeg, with the frame each step starts at written beside it for
    /// tools/cinema_cut.py. No titles: the clips are raw footage for an edit. No audio.
    static class Mw2Cinema
    {
        public static bool On, HideHud = true;
        public static int Fps = 60;
        static int Super = 1;

        // ------------------------------------------------------------ vista

        public struct Vista { public Vector3 feet; public float yaw, pitch, score, open, drop; }

        /// Spots with the widest open view (sky or far scenery across a ±30 degree fan at eye height),
        /// best ones looking off an edge, nothing in the foreground. Distinct spots, best first.
        public static List<Vista> Scout(CharacterBody body, int want = 4)
        {
            var found = new List<Vista>();
            var g = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
            int count = g != null ? g.GetNodeCount() : 0;
            if (count == 0) return found;
            int mask = LayerIndex.world.mask;
            var rng = new System.Random(1234);
            int samples = Math.Min(count, 700);
            for (int s = 0; s < samples; s++)
            {
                int i = count <= samples ? s : rng.Next(count);
                if (!g.GetNodePosition(new RoR2.Navigation.NodeGraph.NodeIndex(i), out var p)) continue;
                var eye = p + Vector3.up * 1.7f;
                if (Physics.Raycast(eye, Vector3.up, 60f, mask, QueryTriggerInteraction.Ignore)) continue; // under a roof
                for (int y = 0; y < 360; y += 15)
                {
                    // The middle of the frame clear far out (a rock pillar 150 m off filled it, pilot
                    // 10-05-26), the fan around it mostly so.
                    bool centre = true;
                    for (int dy = -10; dy <= 10 && centre; dy += 10)
                        if (Physics.Raycast(eye, Quaternion.Euler(-2f, y + dy, 0f) * Vector3.forward, 300f, mask, QueryTriggerInteraction.Ignore)) centre = false;
                    if (!centre) continue;
                    // The whole frame over the abyss (playtest 10-05-26: only the fog and what's far off, no
                    // ground or cliff in it): from above the horizon to the frame's bottom edge, across its
                    // width, nothing within 150 m.
                    float open = 0f; int rays = 0;
                    for (int dy = -36; dy <= 36; dy += 6)
                        foreach (float pitch in new[] { -8f, -2f, 6f, 14f, 22f })
                        {
                            var dir = Quaternion.Euler(pitch, y + dy, 0f) * Vector3.forward;
                            rays++;
                            if (!Physics.Raycast(eye, dir, out var hit, 600f, mask, QueryTriggerInteraction.Ignore)) open += 1f;
                            else if (hit.distance > 150f) open += Mathf.Clamp01((hit.distance - 150f) / 250f);
                        }
                    open /= rays;
                    if (open < 0.85f) continue;
                    var fwd = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                    // Nothing right in front (a rock filling the frame).
                    if (Physics.Raycast(eye, Quaternion.Euler(12f, y, 0f) * Vector3.forward, 7f, mask, QueryTriggerInteraction.Ignore)) continue;
                    // An edge: the ground drops away ahead.
                    float drop = 0f;
                    foreach (float ahead in new[] { 10f, 20f })
                    {
                        var at = eye + fwd * ahead;
                        drop += Physics.Raycast(at, Vector3.down, out var down, 60f, mask, QueryTriggerInteraction.Ignore) ? Mathf.Clamp01((down.distance - 1.7f) / 20f) : 1f;
                    }
                    drop *= 0.5f;
                    float score = open + drop * 0.35f;
                    found.Add(new Vista { feet = p, yaw = y, pitch = 2f, score = score, open = open, drop = drop });
                }
            }
            found.Sort((a, b) => b.score.CompareTo(a.score));
            var best = new List<Vista>();
            foreach (var v in found)
            {
                bool near = false;
                foreach (var b in best) if (Vector3.Distance(b.feet, v.feet) < 25f) { near = true; break; }
                if (!near) best.Add(v);
                if (best.Count >= want) break;
            }
            UnityEngine.Debug.Log($"[cinema] scouted {samples} nodes of {count}: {found.Count} open views");
            return best;
        }

        /// Views that read as RoR2 (playtest 10-05-26: the abyss shot "you can't even tell it's risk of rain
        /// 2"): walkable ground 15-90 m out across the bottom of the frame for monsters to roam
        /// through, open sky across the top, nothing in the face. Distinct spots, best first.
        public static List<Vista> ScoutLand(CharacterBody body, int want = 6)
        {
            var found = new List<Vista>();
            var g = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
            int count = g != null ? g.GetNodeCount() : 0;
            if (count == 0) return found;
            int mask = LayerIndex.world.mask;
            var rng = new System.Random(4321);
            int samples = Math.Min(count, 700);
            const float viewPitch = 6f;
            for (int s = 0; s < samples; s++)
            {
                int i = count <= samples ? s : rng.Next(count);
                if (!g.GetNodePosition(new RoR2.Navigation.NodeGraph.NodeIndex(i), out var p)) continue;
                var eye = p + Vector3.up * 1.7f;
                if (Physics.Raycast(eye, Vector3.up, 60f, mask, QueryTriggerInteraction.Ignore)) continue; // under a roof
                for (int y = 0; y < 360; y += 15)
                {
                    if (Physics.Raycast(eye, Quaternion.Euler(viewPitch, y, 0f) * Vector3.forward, 10f, mask, QueryTriggerInteraction.Ignore)) continue;
                    // The floor: rays through the bottom of the frame land on near-level ground 15-90 m out.
                    float floor = 0f, sky = 0f; int fr = 0, sr = 0;
                    for (int dy = -24; dy <= 24; dy += 8)
                    {
                        foreach (float pitch in new[] { viewPitch + 4f, viewPitch + 9f, viewPitch + 14f })
                        {
                            fr++;
                            if (Physics.Raycast(eye, Quaternion.Euler(pitch, y + dy, 0f) * Vector3.forward, out var hit, 120f, mask, QueryTriggerInteraction.Ignore)
                                && hit.distance > 15f && hit.distance < 90f && hit.normal.y > 0.8f) floor += 1f;
                        }
                        // The sky: rays through the top of the frame hit nothing for 400 m.
                        foreach (float pitch in new[] { viewPitch - 14f, viewPitch - 20f })
                        {
                            sr++;
                            if (!Physics.Raycast(eye, Quaternion.Euler(pitch, y + dy, 0f) * Vector3.forward, 400f, mask, QueryTriggerInteraction.Ignore)) sky += 1f;
                        }
                    }
                    floor /= fr; sky /= sr;
                    if (floor < 0.5f || sky < 0.6f) continue;
                    found.Add(new Vista { feet = p, yaw = y, pitch = viewPitch, score = floor + sky, open = sky, drop = floor });
                }
            }
            found.Sort((a, b) => b.score.CompareTo(a.score));
            var best = new List<Vista>();
            foreach (var v in found)
            {
                bool near = false;
                foreach (var b in best) if (Vector3.Distance(b.feet, v.feet) < 30f) { near = true; break; }
                if (!near) best.Add(v);
                if (best.Count >= want) break;
            }
            UnityEngine.Debug.Log($"[cinema] land scout: {samples} nodes of {count}: {found.Count} views");
            return best;
        }

        // ------------------------------------------------------------ capture

        static Process ffmpeg;
        static Stream pipe;
        static StreamWriter marks;
        static int frame;
        /// Frames written so far (the take's clock).
        public static int Frame => frame;
        /// White over the written frame (0-1), the recorder's own: the HUD stops drawing when the player
        /// dies, and RoR2's red death screen showed through the nuke's white-out (10-06-26).
        public static float White;
        static string lastStep;
        public static bool Recording { get; private set; }
        public static string OutFile { get; private set; }

        public static string FfmpegPath()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
            }
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
                foreach (var f in Directory.GetFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories)) return f;
            return null;
        }


        /// RoR2's HUD off for the run (the player's value back at Stop and on quit). The video is the
        /// game's own resolution: an unattended fullscreen RoR2 ignored resolution changes, so
        /// tools/cinema.ps1 sets it in RoR2's config before launch and puts it back after.
        public static void Stage(string res, CharacterBody body)
        {
            if (HideHud) HoldConVar("hud_enable", "0");
            // Real input off for the run: over Parsec a mouse or key went into the game and turned the
            // camera or switched guns mid-recording (10-05-26). The pilot drives everything itself.
            SetPlayerInput(false);
        }

        static void SetPlayerInput(bool on)
        {
            try { LocalUserManager.GetFirstLocalUser()?.inputPlayer?.controllers.maps.SetAllMapsEnabled(on); }
            catch (Exception e) { Plugin.Log.LogWarning($"[cinema] input {(on ? "on" : "off")}: {e.Message}"); }
            InputBlocked = !on;
        }

        /// The mod's own hotkeys ignore the keyboard while the showcase runs (In.Typing).
        public static bool InputBlocked { get; private set; }

        static string HudMarker => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "mw2ror2-cinema-hud.txt");

        /// RoR2 settings a take changes, with the player's own values to put back. RoR2 saves these
        /// (hud_enable, enable_damage_numbers): a take left the player's HUD off for a multiplayer session
        /// (10-06-26), and every take since 10-05 left his damage numbers off (found 10-07-26). Each is
        /// written to the marker as it's held, so a take killed midway is undone at the next launch.
        static readonly Dictionary<string, string> held = new Dictionary<string, string>();

        public static void HoldConVar(string name, string value)
        {
            var c = RoR2.Console.instance;
            if (c == null) return;
            if (!held.ContainsKey(name)) { held[name] = c.FindConVar(name)?.GetString() ?? "1"; Plugin.Log.LogInfo($"[cinema] {name} {value} for the take (the player's: {held[name]})"); }
            try { System.IO.File.WriteAllLines(HudMarker, System.Linq.Enumerable.Select(held, kv => kv.Key + " " + kv.Value)); } catch { }
            c.SubmitCmd(null, name + " " + value);
        }

        /// At start-up: a take that never finished left RoR2 settings changed (the HUD off, damage
        /// numbers off) - back to the player's own.
        public static void RecoverHud()
        {
            try
            {
                if (!System.IO.File.Exists(HudMarker)) return;
                var c = RoR2.Console.instance;
                if (c == null) return;
                foreach (var line in System.IO.File.ReadAllLines(HudMarker))
                {
                    var parts = line.Trim().Split(new[] { ' ' }, 2);
                    if (parts.Length == 1 && parts[0].Length > 0 && !parts[0].Contains("_")) c.SubmitCmd(null, "hud_enable " + parts[0]); // the old one-value marker
                    else if (parts.Length == 2) c.SubmitCmd(null, parts[0] + " " + parts[1]);
                }
                System.IO.File.Delete(HudMarker);
                Plugin.Log.LogInfo("[cinema] an unfinished take had changed RoR2's settings: restored");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[cinema] settings recover: {e.Message}"); }
        }

        public static void Unstage()
        {
            var c = RoR2.Console.instance;
            if (c != null) foreach (var kv in held) { c.SubmitCmd(null, kv.Key + " " + kv.Value); Plugin.Log.LogInfo($"[cinema] {kv.Key} back to {kv.Value}"); }
            held.Clear();
            try { System.IO.File.Delete(HudMarker); } catch { }
            if (InputBlocked) SetPlayerInput(true);
        }

        public static bool Start(string outDir)
        {
            if (Recording) return true;
            string exe = FfmpegPath();
            if (exe == null) { Plugin.Log.LogWarning("[cinema] ffmpeg not found: no recording"); return false; }
            // At least 1440 lines: a smaller screen is supersampled (Unity re-renders the frame).
            Super = Mathf.Clamp(Mathf.CeilToInt(1440f / Mathf.Max(Screen.height, 1)), 1, 4);
            int sh0 = Screen.height * Super, sw0 = Screen.width * Super;
            int h = sh0 & ~1, w = Mathf.Min(sw0, Mathf.RoundToInt(sh0 * 16f / 9f)) & ~1;
            OutFile = Path.Combine(outDir, "cinema.mp4");
            var psi = new ProcessStartInfo(exe,
                $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {w}x{h} -r {Fps} -i - -vf vflip -c:v libx264 -preset medium -crf 17 -pix_fmt yuv420p -movflags +faststart \"{OutFile}\"")
            { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true, RedirectStandardError = false };
            ffmpeg = Process.Start(psi);
            pipe = ffmpeg.StandardInput.BaseStream;
            marks = new StreamWriter(Path.Combine(outDir, "cinema.csv")) { AutoFlush = true };
            marks.WriteLine("frame,step");
            frame = 0; lastStep = null;
            WavFile = Path.Combine(outDir, "cinema.wav");
            Native.mw2_tape_start(); // the MW2 sounds, on the video's clock
            // RoR2's own sounds (Wwise) on the same clock (playtest 10-07-26: "the ror2 enemies werent playing
            // any audio"): Wwise renders offline - a frame of sound per rendered frame, however slow the
            // frames - and captures its output to a WAV, mixed in quiet under the MW2 tape. Its music off.
            Ror2Wav = Path.Combine(outDir, "ror2.wav");
            try
            {
                Mw2Audio.Ror2Music(false);
                AkSoundEngine.SetOfflineRenderingFrameTime(1f / Fps);
                AkSoundEngine.SetOfflineRendering(true);
                var started = AkSoundEngine.StartOutputCapture(Ror2Wav);
                Plugin.Log.LogInfo($"[cinema] RoR2 sound capture: {started}");
                if (started != AKRESULT.AK_Success) { AkSoundEngine.SetOfflineRendering(false); Ror2Wav = null; }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[cinema] RoR2 sound capture: {e.Message}"); Ror2Wav = null; }
            musicLoop = 0;
            Time.captureFramerate = Fps;
            Recording = true;
            Plugin.Instance.StartCoroutine(Loop(w, h));
            Plugin.Log.LogInfo($"[cinema] recording {w}x{h} at {Fps} fps -> {OutFile}");
            return true;
        }

        public static void Stop()
        {
            if (!Recording) return;
            Recording = false;
            Time.captureFramerate = 0;
            if (musicLoop != 0) { Native.mw2_loop_stop(musicLoop); musicLoop = 0; }
            MusicAlias = null;
            var wb = System.Text.Encoding.UTF8.GetBytes(WavFile ?? "");
            float secs;
            unsafe { fixed (byte* p = wb) secs = Native.mw2_tape_stop(p, (UIntPtr)wb.Length); }
            Plugin.Log.LogInfo($"[cinema] sound: {secs:F1} s -> {WavFile}");
            if (secs <= 0f) WavFile = null;
            if (Ror2Wav != null)
            {
                try { AkSoundEngine.StopOutputCapture(); AkSoundEngine.SetOfflineRendering(false); }
                catch (Exception e) { Plugin.Log.LogWarning($"[cinema] RoR2 sound capture stop: {e.Message}"); }
                Mw2Audio.Ror2Music(true);
            }
            Unstage();
            Mw2View.WorldWiden = 1f;
            Plugin.Instance.StartCoroutine(Finish());
        }

        static IEnumerator Finish()
        {
            yield return null;
            try { pipe?.Flush(); pipe?.Dispose(); } catch { }
            try { marks?.Dispose(); } catch { }
            if (ffmpeg != null && !ffmpeg.WaitForExit(120000)) Plugin.Log.LogWarning("[cinema] ffmpeg still encoding after 2 min");
            Plugin.Log.LogInfo($"[cinema] {frame} frames ({frame / (float)Fps:F1} s) in {OutFile}");
            ffmpeg = null; pipe = null; marks = null;
            // The tape under the picture (the video alone stays as cinema_silent.mp4).
            if (WavFile != null && File.Exists(WavFile) && File.Exists(OutFile))
            {
                string av = Path.Combine(Path.GetDirectoryName(OutFile), "cinema_av.mp4");
                try
                {
                    // RoR2's capture under the tape at Ror2Level (Wwise may write it as surround: down to stereo).
                    bool ror2 = Ror2Wav != null && File.Exists(Ror2Wav) && new FileInfo(Ror2Wav).Length > 1024;
                    string inv(float f) => f.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string args = ror2
                        ? $"-y -loglevel error -i \"{OutFile}\" -i \"{WavFile}\" -i \"{Ror2Wav}\" -filter_complex \"[1:a]aformat=sample_rates=48000:channel_layouts=stereo[m];[2:a]aformat=sample_rates=48000:channel_layouts=stereo,volume={inv(Ror2Level)},afade=t=out:st={inv(Mathf.Max(0f, frame / (float)Fps - 1.5f))}:d=1.5[r];[m][r]amix=inputs=2:duration=first:normalize=0[a]\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"{av}\""
                        : $"-y -loglevel error -i \"{OutFile}\" -i \"{WavFile}\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"{av}\"";
                    Plugin.Log.LogInfo($"[cinema] sound mux: MW2 tape{(ror2 ? $" + RoR2 at {Ror2Level:F2}" : " only")}");
                    var mux = Process.Start(new ProcessStartInfo(FfmpegPath(), args)
                    { UseShellExecute = false, CreateNoWindow = true });
                    if (mux != null && mux.WaitForExit(120000) && mux.ExitCode == 0 && File.Exists(av))
                    {
                        string silent = Path.Combine(Path.GetDirectoryName(OutFile), "cinema_silent.mp4");
                        if (File.Exists(silent)) File.Delete(silent);
                        File.Move(OutFile, silent);
                        File.Move(av, OutFile);
                        Plugin.Log.LogInfo("[cinema] sound muxed in");
                    }
                    else Plugin.Log.LogWarning("[cinema] sound mux failed");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[cinema] sound mux: {e.Message}"); }
            }
        }

        // ------------------------------------------------------------ swap dissolve

        /// A gun swap cross-fades (playtest 10-05-26: the ACR "just pops into existence"). The viewmodel
        /// shader can't fade, so the recorder does it: for DissolveFrames after a swap, each video
        /// frame is the ordinary frame (the new gun, the world moving on) blended with one more render
        /// of the same moment - game time held still - showing the old gun instead (parked, its last pose).
        public static int DissolveFrames = 8;
        /// A music loop under the take (the animatic: MW2's menu music), at MusicLevel x the MW2 volume.
        public static string MusicAlias;
        public static float MusicLevel = 0.3f;
        static uint musicLoop;
        static string WavFile;
        /// RoR2's own sounds (Wwise's output capture) and how loud they sit under the MW2 tape
        /// (playtest 10-07-26: "you can play it just really quiet").
        static string Ror2Wav;
        /// Pilot: video frames written so far.
        public static int FrameForTest => frame;
        public static float Ror2Level = 0.35f;
        /// The killstreaks' own HUD over the showcase (the nuke's countdown and white-out).
        public static bool ShowStreakHud;
        static readonly List<(GameObject old, GameObject neu)> fadePairs = new List<(GameObject, GameObject)>();
        static int fadeLeft, fadeTotal;
        static bool oldPass;
        static byte[] fadeNew;

        /// The pilot's clock (and the game's) stands still on the old gun's extra render.
        public static bool HoldingTime => oldPass;

        public static void SwapDissolve(GameObject oldRoot, GameObject newRoot)
        {
            if (!Recording || DissolveFrames <= 0 || oldRoot == null || oldRoot == newRoot) return;
            if (fadeLeft == 0 && !oldPass) fadePairs.Clear();
            fadePairs.Add((oldRoot, newRoot));
            fadeLeft = fadeTotal = DissolveFrames;
        }

        /// Before any camera draws: the old guns show only on their pass, the new ones not on it.
        static void PreCull(Camera c)
        {
            if (fadePairs.Count == 0) return;
            foreach (var (o, n) in fadePairs)
            {
                if (o != null) o.SetActive(oldPass);
                if (n != null && oldPass) n.SetActive(false);
            }
        }

        static void EndDissolve()
        {
            foreach (var (o, n) in fadePairs) { if (o != null) o.SetActive(false); if (n != null) n.SetActive(true); }
            fadePairs.Clear();
            fadeLeft = 0; oldPass = false; fadeNew = null;
            Time.timeScale = 1f;
            WwiseHold(false);
        }

        /// Wwise's offline clock with the game's: a dissolve's held render (time still) renders next to
        /// no sound - each one had RoR2's track running 1/60 s ahead (6 s over a take, 10-07-26).
        static void WwiseHold(bool hold)
        {
            if (Ror2Wav == null) return;
            try { AkSoundEngine.SetOfflineRenderingFrameTime(hold ? 1e-5f : 1f / Fps); }
            catch (Exception e) { Plugin.Log.LogWarning($"[cinema] RoR2 sound clock: {e.Message}"); }
        }

        static IEnumerator Loop(int w, int h)
        {
            Camera.onPreCull += PreCull;
            var eof = new WaitForEndOfFrame();
            var out_ = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "MW2 cinema frame" };
            while (Recording)
            {
                yield return eof;
                // The finished frame (world, viewmodel, scope overlay), supersampled when the screen is
                // smaller than the video (a remote session's 1280x720 desktop: Unity renders it again at
                // superSize x), its centre 16:9 when wider. Always through CaptureScreenshotAsTexture:
                // CaptureScreenshotIntoRenderTexture came out upside down and darker (10-05-26, 3440x1440).
                var shot = ScreenCapture.CaptureScreenshotAsTexture(Super);
                float crop = Mathf.Min(1f, (shot.height * 16f / 9f) / shot.width);
                Graphics.Blit(shot, out_, new Vector2(crop, 1f), new Vector2((1f - crop) * 0.5f, 0f));
                UnityEngine.Object.Destroy(shot);
                // Every rendered frame is a video frame (captureFramerate steps the game 1/60 s each):
                // wait for this one's readback here rather than let frames go by uncaptured.
                var req = AsyncGPUReadback.Request(out_, 0, TextureFormat.RGBA32);
                req.WaitForCompletion();
                if (req.hasError || pipe == null) continue;
                var bytes = req.GetData<byte>().ToArray();
                // A swap dissolve: this frame (new gun) is kept, the next render (time held, old gun)
                // is blended into it, and only that blend is a video frame.
                if (fadeLeft > 0 && !oldPass)
                {
                    fadeNew = bytes;
                    oldPass = true;
                    Time.timeScale = 0f;
                    WwiseHold(true);
                    continue;
                }
                if (oldPass)
                {
                    int wOld = Mathf.RoundToInt(256f * fadeLeft / (fadeTotal + 1)); // the old gun fades out
                    var a = fadeNew;
                    if (a != null && a.Length == bytes.Length)
                        for (int i = 0; i < a.Length; i++) a[i] = (byte)(a[i] + (((bytes[i] - a[i]) * wOld) >> 8));
                    bytes = a ?? bytes;
                    oldPass = false;
                    Time.timeScale = 1f;
                    WwiseHold(false);
                    if (--fadeLeft <= 0) EndDissolve();
                }
                string step = Mw2Pilot.CurrentStep;
                if (step != lastStep) { marks.WriteLine($"{frame},{step}"); lastStep = step; }
                if (White > 0.004f)
                {
                    int wa = Mathf.RoundToInt(Mathf.Clamp01(White) * 256f);
                    for (int i = 0; i < bytes.Length; i++)
                        if ((i & 3) != 3) bytes[i] = (byte)(bytes[i] + (((255 - bytes[i]) * wa) >> 8));
                }
                frame++;
                // The music bed (streamed: it may take a few tries to start), then the tape moves a frame on.
                if (MusicAlias != null)
                {
                    if (musicLoop == 0 && frame % 15 == 1) musicLoop = Mw2Audio.LoopStart(MusicAlias);
                    if (musicLoop != 0) Native.mw2_loop_volume(musicLoop, Plugin.Instance.Volume.Value * MusicLevel);
                }
                Native.mw2_tape_advance(1f / Fps);
                try { pipe.Write(bytes, 0, bytes.Length); }
                catch (Exception e) { Plugin.Log.LogWarning($"[cinema] write: {e.Message}"); Recording = false; }
            }
            Camera.onPreCull -= PreCull;
            EndDissolve();
            out_.Release();
        }
    }
}
