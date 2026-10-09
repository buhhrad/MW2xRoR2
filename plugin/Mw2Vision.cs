using System;
using System.Collections.Generic;
using System.Globalization;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace MW2RoR2
{
    /// MW2 vision sets (vision/*.vision from your common_mp / patch_mp) on RoR2's post-processing,
    /// switched like visionSetNaked( name, seconds ): each set fades in while the others fade out.
    /// Mapping (IW4's film shader isn't Unity's colour grade, so the look is close, not exact):
    ///   r_filmDesaturation d -> saturation -100 d;  r_filmContrast c -> contrast 100 (c - 1);
    ///   r_filmBrightness b -> post exposure 2 b;  r_filmLightTint / DarkTint -> gain / lift;
    ///   r_glow + r_glowBloomIntensity0 / Cutoff -> bloom intensity / threshold.  r_filmInvert: not done.
    /// Volumes are made once and kept across scenes, switched off at weight 0: a volume torn down
    /// with a stage stayed registered with RoR2's post-processing and threw every frame on the next
    /// scene (a crash, 10-02-26: run end -> character select).
    static class Mw2Vision
    {
        class Layer
        {
            public PostProcessVolume volume;
            public float weight, target, rate;
        }

        static readonly Dictionary<string, Layer> layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        static float nextPriority = 100000f; // above RoR2Application's own global volume (99999)

        /// Re-register the live vision volumes with RoR2's post-processing (a camera made after a
        /// vision set came on never blended it: the AC-130's thermal, playtest 10-04-26).
        public static void Reregister()
        {
            foreach (var kv in layers)
            {
                var v = kv.Value.volume;
                if (v == null || !v.enabled) continue;
                v.enabled = false;
                v.enabled = true;
            }
        }

        /// The prematch's mpIntro, driven directly by its timer.
        public static void Intro(float weight) => Set("mpintro", weight);

        /// Hold `name` at `weight` now (no fade).
        public static void Set(string name, float weight)
        {
            var l = Get(name);
            if (l == null) return;
            l.weight = l.target = Mathf.Clamp01(weight);
            l.rate = 0f;
            Apply(l);
        }

        /// visionSetNaked( name, seconds ): fade `name` in and every other set out over `seconds`.
        public static void FadeTo(string name, float seconds)
        {
            var l = Get(name);
            foreach (var kv in layers) Fade(kv.Value, kv.Value == l ? 1f : 0f, seconds);
        }

        /// Back to the map's own look (visionSetNaked( mapname, seconds )).
        public static void FadeOut(float seconds)
        {
            foreach (var kv in layers) Fade(kv.Value, 0f, seconds);
        }

        static void Fade(Layer l, float target, float seconds)
        {
            if (l == null) return;
            l.target = target;
            if (seconds <= 0f) { l.weight = target; l.rate = 0f; Apply(l); }
            else l.rate = Mathf.Abs(target - l.weight) / seconds;
        }

        /// Per frame (real time: the nuke's slow motion mustn't stretch the fades).
        public static void Update()
        {
            foreach (var kv in layers)
            {
                var l = kv.Value;
                if (l.rate <= 0f || l.weight == l.target) continue;
                // (the recorder runs the game frame by frame: its clock, or the fades race ahead)
                float dt = Time.captureFramerate > 0 ? 1f / Time.captureFramerate : Time.unscaledDeltaTime;
                l.weight = Mathf.MoveTowards(l.weight, l.target, l.rate * dt);
                Apply(l);
            }
        }

        static void Apply(Layer l)
        {
            if (l.volume == null) return;
            l.volume.weight = l.weight;
            bool on = l.weight > 0.001f;
            if (l.volume.enabled != on) l.volume.enabled = on;
        }

        static Layer Get(string name)
        {
            if (layers.TryGetValue(name, out var l)) return l.volume != null ? l : null;
            var profile = Build(name);
            l = new Layer();
            layers[name] = l;
            if (profile == null) { Plugin.Log.LogWarning($"MW2 vision '{name}' not found"); return null; }
            var any = UnityEngine.Object.FindObjectOfType<PostProcessVolume>();
            var go = new GameObject("MW2 vision " + name) { layer = any != null ? any.gameObject.layer : 0 };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            l.volume = go.AddComponent<PostProcessVolume>();
            l.volume.isGlobal = true;
            l.volume.priority = nextPriority++;
            l.volume.sharedProfile = profile;
            l.volume.weight = 0f;
            l.volume.enabled = false;
            go.SetActive(true);
            return l;
        }

        /// A profile from MW2's vision file, or one of ours (`thermal`: see Mw2Thermal).
        static PostProcessProfile Build(string name)
        {
            var profile = ScriptableObject.CreateInstance<PostProcessProfile>();
            if (name.Equals(Mw2Thermal.VisionName, StringComparison.OrdinalIgnoreCase) || name.Equals(Mw2Thermal.BlackHotName, StringComparison.OrdinalIgnoreCase))
            {
                Mw2Thermal.Grade(profile, name.Equals(Mw2Thermal.BlackHotName, StringComparison.OrdinalIgnoreCase));
                return profile;
            }
            var v = Read(name);
            if (v == null) return null;
            if (Num(v, "r_filmEnable", 0f) > 0.5f)
            {
                var grade = profile.AddSettings<ColorGrading>();
                grade.gradingMode.Override(GradingMode.HighDefinitionRange); // a stage's LUT mode ignores the rest
                grade.saturation.Override(Mathf.Clamp(-100f * Num(v, "r_filmDesaturation", 0f), -100f, 100f));
                grade.contrast.Override(Mathf.Clamp(100f * (Num(v, "r_filmContrast", 1f) - 1f), -100f, 100f));
                grade.postExposure.Override(2f * Num(v, "r_filmBrightness", 0f));
                var light = Vec(v, "r_filmLightTint");
                var dark = Vec(v, "r_filmDarkTint");
                grade.gain.Override(new Vector4((light.x - 1f) * 0.3f, (light.y - 1f) * 0.3f, (light.z - 1f) * 0.3f, 0f));
                grade.lift.Override(new Vector4((dark.x - 1f) * 0.3f, (dark.y - 1f) * 0.3f, (dark.z - 1f) * 0.3f, 0f));
            }
            if (Num(v, "r_glow", 0f) > 0.5f && Num(v, "r_glowBloomIntensity0", 0f) > 0f)
            {
                var bloom = profile.AddSettings<Bloom>();
                bloom.intensity.Override(Num(v, "r_glowBloomIntensity0", 0f));
                bloom.threshold.Override(Mathf.Max(0.05f, Num(v, "r_glowBloomCutoff", 0.5f)));
            }
            return profile;
        }

        static unsafe Dictionary<string, string> Read(string name)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            uint n;
            fixed (byte* p = b) n = Native.mw2_vision(p, (UIntPtr)b.Length, null, 0);
            if (n == 0) return null;
            var buf = new byte[n];
            fixed (byte* p = b) fixed (byte* o = buf) Native.mw2_vision(p, (UIntPtr)b.Length, o, n);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in System.Text.Encoding.UTF8.GetString(buf).Split('\n'))
            {
                var line = raw.Trim();
                int q = line.IndexOf('"');
                if (q <= 0) continue;
                int q2 = line.IndexOf('"', q + 1);
                if (q2 < 0) continue;
                d[line.Substring(0, q).Trim()] = line.Substring(q + 1, q2 - q - 1).Trim();
            }
            return d;
        }

        static float Num(Dictionary<string, string> v, string key, float fallback) =>
            v.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

        static Vector3 Vec(Dictionary<string, string> v, string key)
        {
            if (!v.TryGetValue(key, out var s)) return Vector3.one;
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float P(int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 1f;
            return new Vector3(P(0), P(1), P(2));
        }
    }

    /// The AC-130 / Chopper Gunner thermal view (_ac130.gsc, _helicopter.gsc: ThermalVisionOn, white
    /// hot). IW4 draws thermal with its own renderer (heat per surface) and applies black_bw on top
    /// of that; RoR2 has no heat pass, so this is an approximation: the world graded dark and grey,
    /// every enemy overlaid bright white, under the AC-130 grain the job already draws. The rides'
    /// +activate switch (thermalVision()) flips to missilecam's black hot: a bright grey world with
    /// the enemies black.
    static class Mw2Thermal
    {
        public const string VisionName = "thermal", BlackHotName = "thermal_bhot";
        static int users;
        static float nextScan;
        static Material white, black;
        public static bool BlackHot { get; private set; }
        static readonly Dictionary<CharacterModel, TemporaryOverlayInstance> hot = new Dictionary<CharacterModel, TemporaryOverlayInstance>();
        static readonly Dictionary<Renderer, Material[]> swapped = new Dictionary<Renderer, Material[]>();

        public static void Grade(PostProcessProfile profile, bool blackHot = false)
        {
            var grade = profile.AddSettings<ColorGrading>();
            grade.enabled.Override(true);
            grade.gradingMode.Override(GradingMode.HighDefinitionRange); // a stage's LUT mode ignores the rest
            grade.saturation.Override(-100f);
            // missilecam (black hot): film contrast 3.7, brightness 1 - a washed-out bright world.
            // White hot: a mid-grey world under the white bodies. At -1.3 / 35 a bright, foggy stage
            // (Titanic Plains) came out black through the scope - mean 27 of 255 (QA 10-05-26).
            grade.contrast.Override(blackHot ? 45f : 25f);
            grade.postExposure.Override(blackHot ? 1.0f : 0.3f);
            var bloom = profile.AddSettings<Bloom>();
            bloom.intensity.Override(blackHot ? 0f : 1.5f);
            bloom.threshold.Override(0.9f);
            // RoR2's own stage effects off in the thermal view: its edge outlines (SobelOutline),
            // screen-space reflections (HopooSSR) and motion blur work from earlier frames and
            // traced the player's view on the ground into the Predator / AC-130 / chopper camera
            // (playtest 10-04-26: "an outline of the main camera"). MW2's thermal has none of them.
            profile.AddSettings<MotionBlur>().enabled.Override(false);
            profile.AddSettings<AmbientOcclusion>().enabled.Override(false);
            foreach (var name in new[] { "SobelOutline", "HopooSSR" })
            {
                var t = FindType(name);
                if (t != null && typeof(PostProcessEffectSettings).IsAssignableFrom(t)) profile.AddSettings(t).enabled.Override(false);
                else Plugin.Log.LogWarning($"MW2 thermal: RoR2's {name} not found; it stays on");
            }
        }

        static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types) if (t != null && t.Name == name) return t;
            }
            return null;
        }

        public static bool On => users > 0;

        public static void Begin()
        {
            if (users++ == 0) { BlackHot = false; Mw2Vision.FadeTo(VisionName, 0f); nextScan = 0f; }
        }

        public static void End()
        {
            if (users == 0 || --users > 0) return;
            BlackHot = false;
            Mw2Vision.FadeOut(0f);
            Cool();
        }

        /// White hot <-> black hot (visionSetThermalForPlayer( "missilecam" / the map's thermal, seconds )).
        public static void SetBlackHot(bool on, float seconds)
        {
            if (users == 0 || on == BlackHot) return;
            BlackHot = on;
            Mw2Vision.FadeTo(on ? BlackHotName : VisionName, seconds);
            Cool(); // the next scan repaints every enemy in the new colour
            nextScan = 0f;
        }

        static void Cool()
        {
            foreach (var kv in hot) Remove(kv.Value);
            hot.Clear();
            foreach (var kv in swapped) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            swapped.Clear();
        }

        /// Per frame: keep every living enemy white hot (new spawns too).
        public static void Update()
        {
            if (users == 0 || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;
            if (white == null)
            {
                var src = LegacyResourcesAPI.Load<Material>("Materials/matHuntressFlashBright");
                if (src == null) return;
                // White hot: the flash material's own tint is yellow-green.
                white = new Material(src);
                foreach (var prop in new[] { "_TintColor", "_Color", "_EmColor" })
                    if (white.HasProperty(prop)) white.SetColor(prop, Color.white);
                // Black hot: the same overlay alpha-blended, its colour ramp (_RemapTex: the tint
                // alone left it pink) opaque black. Unity's sprite / Standard shaders drew nothing as
                // a RoR2 overlay.
                black = new Material(src);
                if (black.HasProperty("_SrcBlend")) black.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (black.HasProperty("_DstBlend")) black.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                var ramp = new Texture2D(4, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                ramp.SetPixels32(new[] { new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255) });
                ramp.Apply(false, true);
                if (black.HasProperty("_RemapTex")) black.SetTexture("_RemapTex", ramp);
                foreach (var prop in new[] { "_TintColor", "_Color", "_EmColor" })
                    if (black.HasProperty(prop)) black.SetColor(prop, new Color(0f, 0f, 0f, 1f));
                Plugin.Log.LogInfo($"MW2 thermal: black hot ramp {(black.HasProperty("_RemapTex") ? "set" : "missing")}");
            }
            var heat = BlackHot ? black : white;
            var me = Plugin.Instance.Bridge.LocalBody2;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb == me || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                if (me != null && !Mw2Strike.IsEnemy(me, cb)) continue;
                var model = cb.modelLocator != null && cb.modelLocator.modelTransform != null ? cb.modelLocator.modelTransform.GetComponent<CharacterModel>() : null;
                if (model == null || hot.ContainsKey(model)) continue;
                // Fire and glow (particles skip RoR2's overlays) burn white too: a Wisp is mostly its
                // flame, and its mask alone was a dot or two from the gunship (playtest 10-04-26).
                foreach (var pr in model.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    Burn(pr);
                // The model's own parts that skip overlays (a Wisp's mask flame: orange from the gunship).
                foreach (var ri in model.baseRendererInfos)
                    if (ri.ignoreOverlays && ri.renderer != null) Burn(ri.renderer);
                var o = TemporaryOverlayManager.AddOverlay(model.gameObject);
                o.duration = float.PositiveInfinity;
                o.animateShaderAlpha = false;
                o.destroyComponentOnEnd = true;
                o.originalMaterial = heat;
                o.AddToCharacterModel(model);
                hot[model] = o;
            }
        }

        static void Burn(Renderer r)
        {
            if (swapped.ContainsKey(r)) return;
            swapped[r] = r.sharedMaterials;
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = BlackHot ? black : white;
            r.sharedMaterials = mats;
        }

        static void Remove(TemporaryOverlayInstance o)
        {
            try { o?.RemoveFromCharacterModel(); o?.Destroy(); } catch (Exception e) { Plugin.Log.LogWarning($"MW2 thermal: overlay removal: {e.Message}"); }
        }
    }
}
