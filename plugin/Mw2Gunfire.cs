using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    /// mw2sim Mw2Tracer (TracerDef), 108 bytes. IW4 inches; colours tail (0) .. head (4).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    unsafe struct Mw2Tracer
    {
        public int drawInterval;
        public float speed, beamLength, beamWidth, screwRadius, screwDist;
        public fixed float colors[20];
        public ushort material, pad;
    }

    /// What a shot looks like in MW2, from the weapon's own data: view muzzle flash at the gun's
    /// tag_flash, shell eject at tag_brass (last-shot eject on the final round), MW2's tracer
    /// (TracerDef: every drawInterval-th round, a beam of beamLength x beamWidth flying at speed,
    /// coloured along its length) and the impact table's effect for the surface each round hits.
    /// Replaces RoR2's Commando tracer and hitspark when MW2 FX are available.
    static unsafe class Mw2Gunfire
    {
        public static bool Ready => Mw2Fx.Ready;

        // ------------------------------------------------------------------ per weapon data
        class WeaponFx
        {
            public string viewFlash, worldFlash, viewEject, worldEject, viewLastEject, worldLastEject;
            public bool hasTracer;
            public Mw2Tracer tracer;
            public Material tracerMat;
            public int impactType;
            public int roundsSinceTracer;
        }
        static readonly Dictionary<uint, WeaponFx> weapons = new Dictionary<uint, WeaponFx>();

        static WeaponFx For(uint weapon)
        {
            if (weapons.TryGetValue(weapon, out var w)) return w;
            w = new WeaponFx
            {
                viewFlash = FxName(weapon, 0), worldFlash = FxName(weapon, 1),
                viewEject = FxName(weapon, 2), worldEject = FxName(weapon, 3),
                viewLastEject = FxName(weapon, 4), worldLastEject = FxName(weapon, 5),
                impactType = Native.mw2_weapon_impact_type(weapon),
            };
            if (Native.mw2_weapon_tracer(weapon, out w.tracer) == 1)
            {
                w.hasTracer = true;
                w.tracerMat = TracerMaterial(w.tracer.material);
            }
            weapons[weapon] = w;
            Plugin.Log.LogInfo($"MW2 gunfire weapon {weapon}: flash '{w.viewFlash}', eject '{w.viewEject}', tracer {(w.hasTracer ? $"every {w.tracer.drawInterval}, {w.tracer.speed} u/s, {w.tracer.beamLength}x{w.tracer.beamWidth}" : "none")}, impact type {w.impactType}");
            return w;
        }

        static string FxName(uint weapon, uint kind)
        {
            var buf = new byte[128];
            uint n;
            fixed (byte* p = buf) n = Native.mw2_weapon_fx(weapon, kind, p, (uint)buf.Length);
            return n > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, (int)Math.Min(n, (uint)buf.Length)) : null;
        }

        static readonly Dictionary<ushort, Material> tracerMats = new Dictionary<ushort, Material>();
        static Material TracerMaterial(ushort material)
        {
            if (tracerMats.TryGetValue(material, out var m)) return m;
            uint w, h;
            uint n = Native.mw2_tracer_texture(material, out w, out h, null, 0);
            Texture2D tex = Texture2D.whiteTexture;
            if (n > 0 && n == w * h * 4)
            {
                var px = new byte[n];
                fixed (byte* o = px) Native.mw2_tracer_texture(material, out w, out h, o, n);
                tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
                tex.SetPixelData(px, 0);
                tex.Apply(true, true);
            }
            int blend = Native.mw2_tracer_blend(material);
            m = Mw2Fx.SurfaceMaterial(blend < 0 ? 3 : blend, tex);
            tracerMats[material] = m;
            return m;
        }

        // ------------------------------------------------------------------ a shot
        /// The player fired one round: flash + eject (once per trigger pull for pellets) and,
        /// every drawInterval rounds, a tracer from the muzzle to where the round ended.
        /// A weapon's MW2 world (third-person) muzzle flash effect, or null.
        public static string WorldFlash(uint weapon) => Ready && weapon != 0 ? For(weapon).worldFlash : null;

        public static void Shot(uint weapon, Vector3 muzzle, Vector3 brass, Vector3 dir, Vector3 end, bool firstPellet, bool lastRound, Transform view, bool world = false)
        {
            if (!Ready) return;
            var w = For(weapon);
            if (firstPellet)
            {
                // Someone else's gun (multiplayer): MW2's world flash / eject; ours: the view ones,
                // never sent to other players (they draw our world flash from our fire event).
                string flash = world && w.worldFlash != null ? w.worldFlash : w.viewFlash;
                Mw2Fx.NoBroadcast++;
                try { if (flash != null) Mw2Fx.Play(flash, muzzle, dir, view != null ? view.up : Vector3.up); }
                finally { Mw2Fx.NoBroadcast--; }
                string eject = world ? (lastRound && w.worldLastEject != null ? w.worldLastEject : w.worldEject)
                    : (lastRound && w.viewLastEject != null ? w.viewLastEject : w.viewEject);
                // Brass flies out to the right of the gun.
                Mw2Fx.NoBroadcast++;
                try { if (eject != null) Mw2Fx.Play(eject, brass, view != null ? view.right : Vector3.right, view != null ? view.up : Vector3.up); }
                finally { Mw2Fx.NoBroadcast--; }
            }
            if (w.hasTracer && w.tracerMat != null)
            {
                int every = Mathf.Max(w.tracer.drawInterval, 1);
                if (++w.roundsSinceTracer >= every)
                {
                    w.roundsSinceTracer = 0;
                    Tracers.Add(new Tracer { from = muzzle, to = end, w = w });
                }
            }
        }

        // ------------------------------------------------------------------ impacts
        /// The round hit something: the impact table's effect for this weapon's impact type and
        /// the surface (flesh for anything with a hurtbox).
        public static void Impact(uint weapon, Vector3 point, Vector3 normal, Collider collider, bool flesh)
        {
            if (!Ready) return;
            var w = For(weapon);
            int surf = flesh ? Surface("flesh") : SurfaceFor(collider);
            string fx = ImpactFx(w.impactType, surf);
            if (fx != null) Mw2Fx.Play(fx, point + normal * 0.02f, normal);
            // The crack of the round on what it hit: bullet_<small|large|ap>_<surface>, shotguns
            // bulletspray_small_<surface> (one per trigger pull, not per pellet).
            if (ImpactSound != null && Time.time - lastImpactSound > 0.03f)
            {
                lastImpactSound = Time.time;
                string kind = w.impactType == 2 ? "bullet_large_" : w.impactType == 3 ? "bullet_ap_" : w.impactType == 5 ? "bulletspray_small_" : "bullet_small_";
                ImpactSound(SoundFor(kind, flesh ? "flesh" : SurfaceName(surf)), point);
            }
        }

        public static Action<string, Vector3> ImpactSound;
        static float lastImpactSound;
        static readonly Dictionary<string, string> soundNames = new Dictionary<string, string>();

        static string SoundFor(string kind, string surface)
        {
            string key = kind + surface;
            if (soundNames.TryGetValue(key, out var n)) return n;
            n = Exists(key) ? key : Exists(kind + "default") ? kind + "default" : null;
            soundNames[key] = n;
            Plugin.Log.LogInfo($"[gunfire] impact sound {key} -> {n ?? "(none)"}");
            return n;
        }

        static bool Exists(string alias)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(alias);
            fixed (byte* p = b) return Native.mw2_alias_exists(p, (UIntPtr)b.Length) == 1;
        }

        static readonly Dictionary<long, string> impactNames = new Dictionary<long, string>();
        static string ImpactFx(int type, int surf)
        {
            long key = ((long)type << 32) | (uint)surf;
            if (impactNames.TryGetValue(key, out var n)) return n;
            var buf = new byte[128];
            uint len;
            fixed (byte* p = buf) len = Native.mw2_impact_fx((uint)Mathf.Max(type, 0), (uint)Mathf.Max(surf, 0), p, (uint)buf.Length);
            n = len > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, (int)Math.Min(len, (uint)buf.Length)) : null;
            impactNames[key] = n;
            return n;
        }

        // MW2 surface types by name (the index order is MW2's own, read from mw2sim).
        static Dictionary<string, int> surfaces;
        /// IW4's flesh surface index (a projectile that struck a body).
        public static int FleshSurface => Surface("flesh");

        static int Surface(string name)
        {
            if (surfaces == null)
            {
                surfaces = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var buf = new byte[64];
                for (uint i = 0; i < 64; i++)
                {
                    uint len;
                    fixed (byte* p = buf) len = Native.mw2_surface_name(i, p, (uint)buf.Length);
                    if (len == 0) break;
                    var s = System.Text.Encoding.UTF8.GetString(buf, 0, (int)len);
                    if (!surfaces.ContainsKey(s)) surfaces[s] = (int)i;
                }
            }
            return surfaces.TryGetValue(name, out var idx) ? idx : (surfaces.TryGetValue("default", out var d) ? d : 0);
        }

        /// MW2 surface type index for a collider (cached per collider), for the movement trace.
        static readonly Dictionary<int, int> colliderSurface = new Dictionary<int, int>();
        public static int SurfaceIndex(Collider c)
        {
            if (c == null) return 0;
            int id = c.GetInstanceID();
            if (colliderSurface.TryGetValue(id, out var s)) return s;
            s = SurfaceFor(c);
            if (colliderSurface.Count > 4096) colliderSurface.Clear();
            colliderSurface[id] = s;
            return s;
        }

        /// IW4 surface type name by index (dirt, concrete...).
        public static string SurfaceName(int index)
        {
            Surface("default");
            foreach (var kv in surfaces) if (kv.Value == index) return kv.Key;
            return "default";
        }

        /// RoR2 SurfaceDef (by name) -> the nearest MW2 surface type.
        static readonly Dictionary<SurfaceDef, int> surfaceCache = new Dictionary<SurfaceDef, int>();
        static readonly HashSet<string> logged = new HashSet<string>();
        static int SurfaceFor(Collider c)
        {
            var def = c != null ? SurfaceDefProvider.GetObjectSurfaceDef(c, Vector3.zero) : null;
            if (def == null) return Surface("rock");
            if (surfaceCache.TryGetValue(def, out var cached)) return cached;
            string n = def.name.ToLowerInvariant();
            string mw2 =
                n.Contains("metal") || n.Contains("robot") || n.Contains("mech") ? "metal" :
                n.Contains("wood") || n.Contains("tree") || n.Contains("bark") ? "wood" :
                n.Contains("water") ? "water" :
                n.Contains("snow") ? "snow" :
                n.Contains("ice") || n.Contains("crystal") ? "ice" :
                n.Contains("glass") ? "glass" :
                n.Contains("sand") ? "sand" :
                n.Contains("mud") ? "mud" :
                n.Contains("grass") || n.Contains("foliage") || n.Contains("plant") ? "grass" :
                n.Contains("dirt") || n.Contains("soil") ? "dirt" :
                n.Contains("flesh") || n.Contains("organic") || n.Contains("blood") ? "flesh" :
                n.Contains("concrete") || n.Contains("brick") || n.Contains("tile") ? "concrete" :
                "rock";
            int idx = Surface(mw2);
            surfaceCache[def] = idx;
            if (logged.Add(def.name)) Plugin.Log.LogInfo($"MW2 surface: RoR2 '{def.name}' -> {mw2}");
            return idx;
        }

        // ------------------------------------------------------------------ tracers
        class Tracer { public Vector3 from, to; public WeaponFx w; public float travelled; }
        static readonly List<Tracer> Tracers = new List<Tracer>();
        static Mesh tracerMesh;
        static readonly List<Vector3> tv = new List<Vector3>();
        static readonly List<Vector2> tuv = new List<Vector2>();
        static readonly List<Color32> tc = new List<Color32>();
        static readonly List<int> tt = new List<int>();

        /// Per frame: move each tracer at its MW2 speed and draw it as a camera-facing beam
        /// (beamLength long, beamWidth wide, IW4 inches), tinted by the TracerDef's colours.
        public static void Render(Camera cam, float dt)
        {
            if (Tracers.Count == 0 || cam == null) return;
            if (tracerMesh == null) tracerMesh = new Mesh { name = "MW2 tracers", indexFormat = IndexFormat.UInt32 };
            Material mat = null;
            tv.Clear(); tuv.Clear(); tc.Clear(); tt.Clear();
            for (int i = Tracers.Count - 1; i >= 0; i--)
            {
                var t = Tracers[i];
                var path = t.to - t.from;
                float total = path.magnitude;
                float speed = Mathf.Max(t.w.tracer.speed, 1f) * Space.Scale;
                t.travelled += speed * dt;
                float len = Mathf.Max(t.w.tracer.beamLength, 1f) * Space.Scale;
                if (t.travelled - len > total || total < 1e-3f) { Tracers.RemoveAt(i); continue; }
                var dir = path / total;
                var head = t.from + dir * Mathf.Min(t.travelled, total);
                var tail = t.from + dir * Mathf.Clamp(t.travelled - len, 0f, total);
                float half = Mathf.Clamp(t.w.tracer.beamWidth, 0.5f, 4f) * Space.Scale * 0.5f; // first-person cap
                var side = Vector3.Cross(dir, (cam.transform.position - (head + tail) * 0.5f).normalized).normalized * half;
                int b = tv.Count;
                tv.Add(tail - side); tv.Add(tail + side); tv.Add(head + side); tv.Add(head - side);
                tuv.Add(new Vector2(0f, 0f)); tuv.Add(new Vector2(0f, 1f)); tuv.Add(new Vector2(1f, 1f)); tuv.Add(new Vector2(1f, 0f));
                var cTail = Col(t.w.tracer, 0); var cHead = Col(t.w.tracer, 4);
                tc.Add(cTail); tc.Add(cTail); tc.Add(cHead); tc.Add(cHead);
                tt.Add(b); tt.Add(b + 1); tt.Add(b + 2); tt.Add(b); tt.Add(b + 2); tt.Add(b + 3);
                mat = t.w.tracerMat;
            }
            tracerMesh.Clear();
            if (tv.Count == 0 || mat == null) return;
            tracerMesh.SetVertices(tv);
            tracerMesh.SetUVs(0, tuv);
            tracerMesh.SetColors(tc);
            tracerMesh.SetTriangles(tt, 0, true);
            Graphics.DrawMesh(tracerMesh, Matrix4x4.identity, mat, 0, null, 0, null, ShadowCastingMode.Off, false);
        }

        static Color32 Col(in Mw2Tracer t, int i)
        {
            fixed (float* c = t.colors)
                return new Color32((byte)(Mathf.Clamp01(c[i * 4]) * 255), (byte)(Mathf.Clamp01(c[i * 4 + 1]) * 255), (byte)(Mathf.Clamp01(c[i * 4 + 2]) * 255), (byte)(Mathf.Clamp01(c[i * 4 + 3]) * 255));
        }
    }
}
