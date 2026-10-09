using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Mw2FxQuad
    {
        public fixed float corners[12]; // 4 x IW4 xyz
        public fixed float uv[8];       // 4 x uv (D3D: v down)
        public uint color;              // RGBA bytes
        public ushort material;         // 1-based
        public ushort sort;
    }

    /// MW2's own particle effects (FxEffectDef) from the user's MW2 files: mw2sim runs IW4's FX
    /// simulation and builds camera-facing quads; this draws them each frame, one mesh per MW2
    /// material, with the material's colour image and MW2's blend mode. Effects are named as
    /// MW2's scripts name them (loadfx), e.g. "explosions/clusterbomb".
    static unsafe class Mw2Fx
    {
        public static bool Ready { get; private set; }
        /// The camera the player sees through when a killstreak owns the view (ride / missile).
        public static Camera View;
        static bool tried;
        static Mw2FxQuad[] quads = new Mw2FxQuad[4096];
        static readonly Dictionary<string, uint> ids = new Dictionary<string, uint>();

        class Batch
        {
            public Material mat;
            public bool lit; // alpha-blended (smoke, dust): takes the scene's light like MW2's lit particles
            public Mesh mesh;
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<Vector2> uv = new List<Vector2>();
            public readonly List<Color32> c = new List<Color32>();
            public readonly List<int> t = new List<int>();
            public int minSort;
        }
        static readonly Dictionary<ushort, Batch> batches = new Dictionary<ushort, Batch>();

        public static void Init()
        {
            if (tried) return;
            tried = true;
            try
            {
                uint n = Native.mw2_fx_init();
                Ready = n > 0;
                Plugin.Log.LogInfo($"MW2 FX: {n} effects");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 FX unavailable: {e.Message}"); }
        }

        public static uint Find(string name)
        {
            if (!Ready || string.IsNullOrEmpty(name)) return 0;
            if (ids.TryGetValue(name, out var id)) return id;
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) id = Native.mw2_fx_find(p, (UIntPtr)b.Length);
            if (id == 0) Plugin.Log.LogWarning($"MW2 FX '{name}' not found");
            ids[name] = id;
            return id;
        }

        /// Play an effect at a Unity position, oriented along fwd (MW2's effect forward = +X).
        /// Multiplayer: one-shot effects this client plays are sent to the others (Mw2Net) unless
        /// suppressed (first-person view flashes, effects replayed from someone else).
        public static Action<string, Vector3, Vector3> Broadcast;
        public static int NoBroadcast;
        static readonly Dictionary<uint, bool> looping = new Dictionary<uint, bool>();

        public static uint Play(string name, Vector3 at, Vector3 fwd, Vector3? up = null)
        {
            uint id = Find(name);
            if (id == 0) return 0;
            if (Broadcast != null && NoBroadcast == 0)
            {
                if (!looping.TryGetValue(id, out bool loops)) looping[id] = loops = Native.mw2_fx_is_looping(id) == 1;
                if (!loops) Broadcast(name, at, fwd);
            }
            var b = stackalloc float[9];
            Pose(b, at, fwd, up);
            return Native.mw2_fx_play(id, b, b + 3, b + 6);
        }

        /// Point an effect up (impacts and explosions on the ground use the surface normal).
        public static uint PlayUp(string name, Vector3 at) => Play(name, at, Vector3.up, Vector3.forward);

        public static bool Move(uint handle, Vector3 at, Vector3 fwd, Vector3? up = null)
        {
            if (handle == 0) return false;
            var b = stackalloc float[9];
            Pose(b, at, fwd, up);
            return Native.mw2_fx_move(handle, b, b + 3, b + 6) == 1;
        }

        public static void Stop(uint handle) { if (handle != 0) Native.mw2_fx_stop(handle); }

        static void Pose(float* b, Vector3 at, Vector3 fwd, Vector3? up)
        {
            fwd = fwd.sqrMagnitude > 1e-8f ? fwd.normalized : Vector3.forward;
            var uw = up ?? (Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up);
            var p = Space.ToIw(at);
            var f = Space.DirToIw(fwd);
            var u = Space.DirToIw(Vector3.ProjectOnPlane(uw, fwd).normalized);
            b[0] = p.x; b[1] = p.y; b[2] = p.z; b[3] = f.x; b[4] = f.y; b[5] = f.z; b[6] = u.x; b[7] = u.y; b[8] = u.z;
        }

        /// Once per frame after the camera is placed: step the sim, rebuild and draw the meshes.
        public static void Render(Camera cam, float dt)
        {
            if (!Ready || cam == null) return;
            var cp = Space.ToIw(cam.transform.position);
            var cf = Space.DirToIw(cam.transform.forward);
            var cu = Space.DirToIw(cam.transform.up);
            var buf = stackalloc float[9];
            buf[0] = cp.x; buf[1] = cp.y; buf[2] = cp.z; buf[3] = cf.x; buf[4] = cf.y; buf[5] = cf.z; buf[6] = cu.x; buf[7] = cu.y; buf[8] = cu.z;
            uint n = Native.mw2_fx_update(dt, buf, buf + 3, buf + 6);
            if (n > quads.Length) quads = new Mw2FxQuad[Mathf.NextPowerOfTwo((int)n)];
            fixed (Mw2FxQuad* q = quads) n = Native.mw2_fx_quads(q, (uint)quads.Length);
            foreach (var b in batches.Values) { if (b == null) continue; b.v.Clear(); b.uv.Clear(); b.c.Clear(); b.t.Clear(); b.minSort = int.MaxValue; }
            for (int i = 0; i < n; i++)
            {
                ref var qd = ref quads[i];
                var b = BatchFor(qd.material);
                if (b == null) continue;
                int start = b.v.Count;
                uint col = qd.color;
                var c32 = new Color32((byte)col, (byte)(col >> 8), (byte)(col >> 16), (byte)(col >> 24));
                fixed (float* cs = qd.corners) fixed (float* us = qd.uv)
                    for (int k = 0; k < 4; k++)
                    {
                        b.v.Add(Space.ToUnity(new Vec3f(cs[k * 3], cs[k * 3 + 1], cs[k * 3 + 2])));
                        b.uv.Add(new Vector2(us[k * 2], us[k * 2 + 1])); // already in the bottom-row-first texture's space
                        b.c.Add(c32);
                    }
                b.t.Add(start); b.t.Add(start + 1); b.t.Add(start + 2);
                b.t.Add(start); b.t.Add(start + 2); b.t.Add(start + 3);
                b.minSort = Mathf.Min(b.minSort, qd.sort);
            }
            // MW2 lights smoke by the light grid (lightingFrac); unlit, it glowed white on dark
            // maps. Here: the scene's ambient level (+ a little sun), additive glows untouched.
            float light = SceneLight();
            foreach (var b in batches.Values)
            {
                if (b == null) continue;
                if (b.lit && b.mat.HasProperty("_TintColor")) b.mat.SetColor("_TintColor", new Color(0.5f * light, 0.5f * light, 0.5f * light, 0.5f));
                if (b.mesh == null) b.mesh = new Mesh { name = "MW2 FX", indexFormat = IndexFormat.UInt32 };
                b.mesh.Clear();
                if (b.v.Count == 0) continue;
                b.mesh.SetVertices(b.v);
                b.mesh.SetUVs(0, b.uv);
                b.mesh.SetColors(b.c);
                b.mesh.SetTriangles(b.t, 0, true);
                // Rough MW2 draw order between materials: by elem sort order, then Unity's distance sort.
                b.mat.renderQueue = 3000 + Mathf.Clamp(b.minSort, 0, 900);
                Graphics.DrawMesh(b.mesh, Matrix4x4.identity, b.mat, 0, null, 0, null, ShadowCastingMode.Off, false);
            }
            DrainSounds();
        }

        static float SceneLight()
        {
            var a = RenderSettings.ambientMode == AmbientMode.Trilight
                ? (RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor) * 0.5f
                : RenderSettings.ambientLight;
            float l = a.grayscale * 1.6f;
            var sun = RenderSettings.sun;
            if (sun != null && sun.isActiveAndEnabled) l += sun.intensity * sun.color.grayscale * 0.35f;
            return Mathf.Clamp(l, 0.2f, 1f);
        }

        static Batch BatchFor(ushort material)
        {
            if (material == 0) return null;
            if (batches.TryGetValue(material, out var b)) return b;
            if (!probed) ProbeShaders();
            b = null;
            try
            {
                var name = new byte[128];
                int blend;
                fixed (byte* p = name) blend = Native.mw2_fx_material(material, p, (uint)name.Length);
                if (blend >= 0)
                {
                    var tex = Texture(material);
                    var mat = MakeMaterial(blend, tex);
                    if (mat != null)
                    {
                        mat.name = "MW2 FX " + System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0');
                        b = new Batch { mat = mat, lit = blend == 2 || blend == 4 };
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 FX material {material}: {e.Message}"); }
            batches[material] = b;
            return b;
        }

        static Texture2D Texture(ushort material)
        {
            uint w, h;
            uint n = Native.mw2_fx_texture(material, out w, out h, null, 0);
            if (n == 0 || n != w * h * 4) return Texture2D.whiteTexture;
            var px = new byte[n];
            fixed (byte* o = px) Native.mw2_fx_texture(material, out w, out h, o, n);
            var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            tex.SetPixelData(px, 0); // top mip; Apply builds the rest
            tex.Apply(true, true);
            return tex;
        }

        // ------------------------------------------------------------------ shaders
        // Unity can't compile shaders at runtime, so MW2's blend modes map onto shaders RoR2
        // ships. Legacy particle shaders take texture x vertex colour with a fixed blend; RoR2's
        // Cloud Remap takes any blend through _SrcBlend/_DstBlend.
        static Shader additive, alphaBlend, multiply, cloud, glow;
        static bool probed;

        static void ProbeShaders()
        {
            probed = true;
            loaded = new Dictionary<string, Shader>();
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
                if (sh != null && !loaded.ContainsKey(sh.name)) loaded[sh.name] = sh;
            var names = new List<string>();
            foreach (var n in loaded.Keys) if (n.Contains("Particle") || n.Contains("FX") || n.Contains("Additive") || n.Contains("Sprite")) names.Add(n);
            Plugin.Log.LogInfo("MW2 FX: loaded FX-like shaders: " + string.Join(", ", names));
            additive = First("Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive", "Legacy Shaders/Particles/Additive (Soft)", "Legacy Shaders/Particles/Alpha Blended Premultiply");
            alphaBlend = First("Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "Mobile/Particles/Alpha Blended", "Sprites/Default");
            multiply = First("Legacy Shaders/Particles/Multiply", "Particles/Multiply", "Mobile/Particles/Multiply");
            cloud = First("Hopoo Games/FX/Cloud Remap");
            // Adds the colour whatever the alpha (a lit display: the killstreak laptop's screen).
            glow = First("Legacy Shaders/Particles/Alpha Blended Premultiply", "Particles/Additive (Soft)", "Mobile/Particles/Additive");
            Plugin.Log.LogInfo($"MW2 FX shaders: additive={additive?.name ?? "-"} blend={alphaBlend?.name ?? "-"} multiply={multiply?.name ?? "-"} cloud={cloud?.name ?? "-"} glow={glow?.name ?? "-"}");
        }

        static Dictionary<string, Shader> loaded;

        static Shader First(params string[] names)
        {
            foreach (var n in names)
            {
                if (loaded != null && loaded.TryGetValue(n, out var l) && l != null) return l;
                var s = FindShader(n);
                if (s != null) return s;
            }
            return null;
        }

        /// Shader.Find that never throws. RoR2BepInExPack (every Thunderstore install) detours it to
        /// RoR2's LegacyShaderAPI, which throws for any name RoR2 doesn't list ("No GUID record is
        /// available for shader name", 10-07-26) - that aborted the shader probe halfway.
        public static Shader FindShader(string name)
        {
            try { return Shader.Find(name); }
            catch (Exception) { return null; }
        }

        static Material guiAdditive;

        /// Additive material for 2D quads drawn with Graphics.DrawTexture in OnGUI (MW2 menu light layers).
        public static Material GuiAdditive()
        {
            if (guiAdditive != null) return guiAdditive;
            if (!probed) ProbeShaders();
            if (additive == null) return null;
            guiAdditive = new Material(additive);
            if (guiAdditive.HasProperty("_TintColor")) guiAdditive.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            return guiAdditive;
        }

        /// A see-through model surface (rotor-blur disc, glass) drawn with its MW2 blend mode.
        public static Material SurfaceMaterial(int blend, Texture2D tex)
        {
            if (!probed) ProbeShaders();
            var m = MakeMaterial(blend, tex);
            if (m != null) m.renderQueue = 3000;
            return m;
        }

        static Material MakeMaterial(int blend, Texture2D tex)
        {
            // 0 opaque, 1 alpha test, 2 blend, 3 additive, 4 multiply, 5 screen, 6 glow (alpha ignored)
            Shader s = blend == 6 ? (glow ?? additive) : blend == 3 || blend == 5 ? additive : blend == 4 ? multiply : alphaBlend;
            Material m;
            // Legacy particle shaders keep MW2's texture colours; RoR2's Cloud Remap (any blend mode,
            // but colour comes through its ramp) is the stand-in when the mode has none.
            if (s == null && cloud == null) s = alphaBlend;
            if (s != null)
            {
                m = new Material(s) { mainTexture = tex };
                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f)); // legacy particles: 0.5 grey = x1
                return m;
            }
            if (cloud == null) return null;
            m = new Material(cloud) { mainTexture = tex };
            var (src, dst) = blend == 3 ? (BlendMode.SrcAlpha, BlendMode.One) // vertex alpha carries MW2's fade
                : blend == 5 ? (BlendMode.OneMinusDstColor, BlendMode.One)
                : blend == 4 ? (BlendMode.DstColor, BlendMode.Zero)
                : (BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlend", (float)src);
            m.SetFloat("_DstBlend", (float)dst);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f); // FX quad winding isn't consistent
            m.SetTexture("_RemapTex", Texture2D.whiteTexture);
            m.SetColor("_TintColor", Color.white);
            m.SetFloat("_VertexColorOn", 1f);
            m.EnableKeyword("VERTEXCOLOR");
            return m;
        }

        // ------------------------------------------------------------------ sound elems
        static readonly byte[] soundBuf = new byte[4096];
        public static Action<string, Vector3> PlaySound;

        static void DrainSounds()
        {
            uint n;
            fixed (byte* p = soundBuf) n = Native.mw2_fx_sounds(p, (uint)soundBuf.Length);
            if (n == 0 || PlaySound == null) return;
            // Every player plays the effects themselves (sent with Broadcast), sounds included.
            Mw2Killstreaks.NoSoundBroadcast++;
            try { DrainLines(n); }
            finally { Mw2Killstreaks.NoSoundBroadcast--; }
        }

        static void DrainLines(uint n)
        {
            foreach (var line in System.Text.Encoding.UTF8.GetString(soundBuf, 0, (int)n).Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length < 4) continue;
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x);
                float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y);
                float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z);
                PlaySound(parts[0], Space.ToUnity(new Vec3f(x, y, z)));
            }
        }
    }
}
