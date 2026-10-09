using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    /// A static MW2 model (e.g. the Care Package crate) built from the user's MW2 files, the
    /// same way as the gun: every surface, MW2 colour maps, RoR2's body shader.
    static unsafe class Mw2Prop
    {
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        // Built once per model; every prop instance shares the mesh and materials.
        static readonly Dictionary<string, (Mesh mesh, Material[] mats)> built = new Dictionary<string, (Mesh, Material[])>();
        // Helicopter rotors cut out of the hull: (mesh around its hub, hub position, spin axis, deg/s).
        static readonly Dictionary<string, List<(Mesh mesh, Vector3 hub, Vector3 axis, float speed)>> rotors = new Dictionary<string, List<(Mesh, Vector3, Vector3, float)>>();
        // Chin turret cut out of the hull: yaw part, barrel (pitch) part, pivots and muzzle (tag_flash), model space.
        struct TurretParts { public Mesh yaw, pitch; public Vector3 yawPivot, pitchPivot, muzzle; }
        static readonly Dictionary<string, TurretParts> turrets = new Dictionary<string, TurretParts>();

        /// Model-space (Unity, metres) position of an MW2 tag, trying the spellings MW2's models use.
        /// A tag's position in the model's local space (Unity metres, before the GameObject's scale),
        /// by model name; cached. False if the model or tag isn't captured.
        public static bool TagLocal(string modelName, string tag, out Vector3 at)
        {
            string key = modelName + "|" + tag;
            if (tagCache.TryGetValue(key, out var hit)) { at = hit.at; return hit.ok; }
            uint model = Native.ModelIndex(modelName);
            at = Vector3.zero;
            bool ok = model != 0 && Tag(model, Space.Scale, out at, tag);
            tagCache[key] = (ok, at);
            return ok;
        }
        static readonly Dictionary<string, (bool ok, Vector3 at)> tagCache = new Dictionary<string, (bool, Vector3)>();

        /// A tag's frame in the model's local space (Unity): position (metres, before the GameObject's
        /// scale), its forward (IW4 X: playFXOnTag aims effects along it) and up. Cached.
        public static bool TagFrame(string modelName, string tag, out Vector3 at, out Vector3 fwd, out Vector3 up)
        {
            string key = modelName + "|" + tag;
            if (!frameCache.TryGetValue(key, out var hit))
            {
                uint model = Native.ModelIndex(modelName);
                var f = stackalloc float[9];
                var b = System.Text.Encoding.UTF8.GetBytes(tag);
                int ok = 0;
                if (model != 0) fixed (byte* p = b) ok = Native.mw2_model_tag_frame(model, p, (UIntPtr)b.Length, f);
                hit = ok == 1
                    ? (true, new Vector3(-f[1], f[2], f[0]) * Space.Scale, new Vector3(-f[4], f[5], f[3]), new Vector3(-f[7], f[8], f[6]))
                    : (false, Vector3.zero, Vector3.forward, Vector3.up);
                frameCache[key] = hit;
            }
            at = hit.at; fwd = hit.fwd; up = hit.up;
            return hit.ok;
        }
        static readonly Dictionary<string, (bool ok, Vector3 at, Vector3 fwd, Vector3 up)> frameCache = new Dictionary<string, (bool, Vector3, Vector3, Vector3)>();

        static bool Tag(uint model, float metresPerUnit, out Vector3 at, params string[] names)
        {
            var f = stackalloc float[3];
            foreach (var n in names)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(n);
                int ok;
                fixed (byte* p = b) ok = Native.mw2_model_tag(model, p, (UIntPtr)b.Length, f);
                if (ok == 1) { at = new Vector3(-f[1], f[2], f[0]) * metresPerUnit; return true; }
            }
            at = Vector3.zero;
            return false;
        }

        static TurretParts BuildTurret(uint model, string modelName, Vector3[] vertices, Vector3[] normals, Vector2[] uvs, List<int[]>[] tris, byte[] cls, float mpu)
        {
            var tp = new TurretParts();
            if (System.Array.IndexOf(cls, (byte)3) < 0 && System.Array.IndexOf(cls, (byte)4) < 0) return tp;
            // Helicopter chin turrets (turret / barrel animate joints); the sentry turns about tag_aim.
            if (!Tag(model, mpu, out tp.yawPivot, "turret_animate_joint", "turret_animate_jnt", "turret_animate_jt", "tag_turret", "tag_aim")) return tp;
            if (!Tag(model, mpu, out tp.pitchPivot, "barrel_animate_joint", "barrel_animate_jnt", "tag_barrel", "tag_aim")) tp.pitchPivot = tp.yawPivot;
            if (!Tag(model, mpu, out tp.muzzle, "tag_flash")) tp.muzzle = tp.pitchPivot + Vector3.forward * 1.5f;
            Mesh Part(List<int[]> t, Vector3 pivot, string suffix)
            {
                if (t.TrueForAll(a => a.Length == 0)) return null;
                var local = new Vector3[vertices.Length];
                for (int v = 0; v < vertices.Length; v++) local[v] = vertices[v] - pivot;
                var m = new Mesh { name = modelName + suffix, indexFormat = IndexFormat.UInt32, vertices = local, normals = normals, uv = uvs };
                var col = new Color32[local.Length];
                for (int v = 0; v < col.Length; v++) col[v] = new Color32(255, 255, 255, 255);
                m.colors32 = col;
                m.subMeshCount = t.Count;
                for (int k = 0; k < t.Count; k++) m.SetTriangles(t[k], k);
                m.RecalculateBounds();
                return m;
            }
            tp.yaw = Part(tris[0], tp.yawPivot, "_turret");
            tp.pitch = Part(tris[1], tp.pitchPivot, "_barrel");
            if (tp.yaw == null && tp.pitch != null) tp.yaw = new Mesh(); // barrel-only rigs still slew
            return tp;
        }

        /// Build the shared mesh ahead of time (MW2 mode start) so the first use doesn't hitch.
        public static void Warm(string modelName, CharacterBody body)
        {
            var go = Build(modelName, body, Space.Scale);
            if (go != null) UnityEngine.Object.Destroy(go);
        }

        public static GameObject Build(string modelName, CharacterBody body, float metresPerUnit)
        {
            if (built.TryGetValue(modelName, out var cached) && cached.mesh != null)
                return Spawn(modelName, cached.mesh, cached.mats);
            uint model = Native.ModelIndex(modelName);
            if (model == 0 || Native.mw2_model_info(model, out var info) != 1 || info.vertexCount == 0)
            {
                Plugin.Log.LogWarning($"MW2 model '{modelName}' not captured; using a stand-in.");
                return null;
            }
            var pos = new float[info.vertexCount * 3];
            var nor = new float[info.vertexCount * 3];
            var uv = new float[info.vertexCount * 2];
            var idx = new uint[info.indexCount];
            int ok;
            fixed (float* p = pos) fixed (float* n = nor) fixed (float* t = uv) fixed (uint* i = idx)
                ok = Native.mw2_model_mesh(model, p, n, t, i);
            if (ok != 1) return null;

            // IW4 -> Unity is a mirror; it alone keeps front faces front (see Mw2Gun).
            var vertices = new Vector3[info.vertexCount];
            var normals = new Vector3[info.vertexCount];
            var uvs = new Vector2[info.vertexCount];
            for (int v = 0; v < info.vertexCount; v++)
            {
                vertices[v] = new Vector3(-pos[v * 3 + 1], pos[v * 3 + 2], pos[v * 3]) * metresPerUnit;
                normals[v] = new Vector3(-nor[v * 3 + 1], nor[v * 3 + 2], nor[v * 3]);
                uvs[v] = new Vector2(uv[v * 2], uv[v * 2 + 1]);
            }
            var mesh = new Mesh { name = modelName, indexFormat = IndexFormat.UInt32, vertices = vertices, normals = normals, uv = uvs };
            var white = new Color32[vertices.Length];
            for (int v = 0; v < white.Length; v++) white[v] = new Color32(255, 255, 255, 255);
            mesh.colors32 = white; // particle shaders on see-through surfaces read vertex colour
            // Rotor vertices (by their MW2 bone) come out of the hull and spin on their own.
            var cls = new byte[info.vertexCount];
            var piv = new float[6];
            uint rigVerts;
            fixed (byte* c = cls) fixed (float* pv = piv) rigVerts = Native.mw2_model_rotors(model, c, (uint)cls.Length, pv);
            bool hasRotors = rigVerts == info.vertexCount && System.Array.Exists(cls, v => v != 0);
            // Spinning parts by class: 1 main rotor, 2 tail rotor, 5-8 propellers (3 / 4: the turret).
            var spinClasses = new byte[] { 1, 2, 5, 6, 7, 8, 9 }; // 9: the sentry's barrel cluster (j_spin)
            var spinTris = new Dictionary<byte, List<int[]>>();
            foreach (var sc in spinClasses) spinTris[sc] = new List<int[]>();
            var turretTris = new[] { new List<int[]>(), new List<int[]>() }; // 0 turret (yaw), 1 barrel (pitch)
            var template = Mw2Gun.TemplateMaterial(body, out _);
            var subs = new List<int[]>();
            var mats = new List<Material>();
            for (uint s = 0; s < info.surfaceCount; s++)
            {
                if (Native.mw2_model_surface(model, s, out var si) != 1 || si.indexCount == 0) continue;
                var tris = new int[si.indexCount];
                for (int k = 0; k < si.indexCount; k++) tris[k] = (int)idx[si.indexStart + k];
                if (hasRotors)
                {
                    var hull = new List<int>(); var turret = new List<int>(); var barrel = new List<int>();
                    var spin = new Dictionary<byte, List<int>>();
                    foreach (var sc in spinClasses) spin[sc] = new List<int>();
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                    {
                        byte a = cls[tris[k]], b = cls[tris[k + 1]], c2 = cls[tris[k + 2]];
                        List<int> into = hull;
                        if (a == b && b == c2) into = a == 3 ? turret : a == 4 ? barrel : spin.TryGetValue(a, out var sl) ? sl : hull;
                        into.Add(tris[k]); into.Add(tris[k + 1]); into.Add(tris[k + 2]);
                    }
                    tris = hull.ToArray();
                    foreach (var sc in spinClasses) spinTris[sc].Add(spin[sc].ToArray());
                    turretTris[0].Add(turret.ToArray());
                    turretTris[1].Add(barrel.ToArray());
                    Plugin.Log.LogInfo($"MW2 {modelName} surface {s}: hull {hull.Count / 3}, main rotor {spin[1].Count / 3}, tail rotor {spin[2].Count / 3}, props {(spin[5].Count + spin[6].Count + spin[7].Count + spin[8].Count) / 3}, turret {turret.Count / 3}, barrel {barrel.Count / 3} tris");
                }
                subs.Add(tris);
                mats.Add(SurfaceMaterialFor(model, s, si, template, modelName));
            }
            if (subs.Count == 0) return null;
            mesh.subMeshCount = subs.Count;
            for (int k = 0; k < subs.Count; k++) mesh.SetTriangles(subs[k], k);
            mesh.RecalculateBounds();

            var arr = mats.ToArray();
            built[modelName] = (mesh, arr);
            var parts = new List<(Mesh, Vector3, Vector3, float)>();
            if (hasRotors)
                foreach (var sc in spinClasses)
                {
                    // Hub = centre of the part's own vertices (some rotor bones sit at the model origin).
                    Vector3 sum = Vector3.zero; int cnt = 0;
                    for (int v = 0; v < cls.Length; v++) if (cls[v] == sc) { sum += vertices[v]; cnt++; }
                    if (cnt == 0) continue;
                    var hub = sum / cnt;
                    var local = new Vector3[vertices.Length];
                    for (int v = 0; v < vertices.Length; v++) local[v] = vertices[v] - hub;
                    var rm = new Mesh { name = modelName + (sc == 1 ? "_main_rotor" : sc == 2 ? "_tail_rotor" : sc == 9 ? "_spin" : "_prop" + (sc - 4)), indexFormat = IndexFormat.UInt32, vertices = local, normals = normals, uv = uvs };
                    rm.colors32 = white; // the see-through rotor-blur shader multiplies by vertex colour
                    rm.subMeshCount = spinTris[sc].Count;
                    for (int k = 0; k < spinTris[sc].Count; k++) rm.SetTriangles(spinTris[sc][k], k);
                    rm.RecalculateBounds();
                    // Main rotor turns about the model's up (IW4 Z), the tail rotor about its side (IW4 Y),
                    // propellers about the nose (IW4 X). Rpm isn't in MW2's data; these read right at 60 fps.
                    var axis = sc == 1 ? Vector3.up : sc == 2 ? Vector3.right : Vector3.forward;
                    // The sentry's barrels turn about the line to its muzzle, and only while it fires.
                    if (sc == 9 && Tag(model, metresPerUnit, out var flash, "tag_flash") && (flash - hub).sqrMagnitude > 1e-6f) axis = (flash - hub).normalized;
                    parts.Add((rm, hub, axis, sc == 1 ? 900f : sc == 2 ? 2400f : sc == 9 ? 0f : 1800f));
                }
            rotors[modelName] = parts;
            turrets[modelName] = BuildTurret(model, modelName, vertices, normals, uvs, turretTris, cls, metresPerUnit);
            return Spawn(modelName, mesh, arr);
        }

        /// Multiplayer: every MW2 model this client spawns (aircraft, crates, sentries, grenades,
        /// rockets) for Mw2Net to mirror on the other players; models built while mirroring
        /// someone else's are not listed. Trail: the effect a projectile drags (`SetTrail`).
        public class Live { public GameObject go; public string model, trail; }
        public static readonly List<Live> Spawned = new List<Live>();
        public static int Mirroring;

        public static void SetTrail(GameObject go, string trail)
        {
            foreach (var l in Spawned) if (l.go == go) { l.trail = trail; return; }
        }

        static GameObject Spawn(string modelName, Mesh mesh, Material[] mats)
        {
            var go = new GameObject($"MW2 {modelName}");
            if (Mirroring == 0)
            {
                Spawned.RemoveAll(l => l.go == null);
                Spawned.Add(new Live { go = go, model = modelName });
            }
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
            if (turrets.TryGetValue(modelName, out var tr) && tr.yaw != null)
            {
                var yawGo = new GameObject(modelName + "_turret");
                yawGo.transform.SetParent(go.transform, false);
                yawGo.transform.localPosition = tr.yawPivot;
                yawGo.AddComponent<MeshFilter>().sharedMesh = tr.yaw;
                yawGo.AddComponent<MeshRenderer>().sharedMaterials = mats;
                var pitchGo = new GameObject(modelName + "_barrel");
                pitchGo.transform.SetParent(yawGo.transform, false);
                pitchGo.transform.localPosition = tr.pitchPivot - tr.yawPivot;
                if (tr.pitch != null)
                {
                    pitchGo.AddComponent<MeshFilter>().sharedMesh = tr.pitch;
                    pitchGo.AddComponent<MeshRenderer>().sharedMaterials = mats;
                }
                var t = go.AddComponent<Mw2Turret>();
                t.yawT = yawGo.transform;
                t.pitchT = pitchGo.transform;
                t.muzzleLocal = tr.muzzle - tr.pitchPivot;
            }
            if (rotors.TryGetValue(modelName, out var parts))
                foreach (var (rm, hub, axis, speed) in parts)
                {
                    var child = new GameObject(rm.name);
                    // The barrel cluster follows the turret's aim (on its pitch part); rotors ride the hull.
                    var pitchT = rm.name.EndsWith("_spin") ? go.GetComponent<Mw2Turret>()?.pitchT : null;
                    if (pitchT != null)
                    {
                        child.transform.SetParent(pitchT, false);
                        child.transform.localPosition = hub - (turrets.TryGetValue(modelName, out var tt) ? tt.pitchPivot : Vector3.zero);
                    }
                    else
                    {
                        child.transform.SetParent(go.transform, false);
                        child.transform.localPosition = hub;
                    }
                    child.AddComponent<MeshFilter>().sharedMesh = rm;
                    var cr = child.AddComponent<MeshRenderer>();
                    cr.sharedMaterials = mats;
                    var spin = child.AddComponent<Mw2Spin>();
                    spin.axis = axis;
                    spin.speed = speed;
                }
            return go;
        }

        /// MW2's material for one surface: opaque ones on RoR2's lit shader; blended ones (rotor
        /// blur discs, glass) see-through with their blend mode. Textures the DXT path can't
        /// take (DXT3, luminance, wavelet...) come decoded from mw2sim, never a blank grey.
        static Material SurfaceMaterialFor(uint model, uint s, Mw2SurfaceInfo si, Material template, string modelName)
        {
            var name = new byte[96];
            int blend;
            fixed (byte* p = name) blend = Native.mw2_model_surface_material(model, s, p, (uint)name.Length);
            string matName = System.Text.Encoding.UTF8.GetString(name).TrimEnd((char)0);
            var tex = Texture(model, s, si) ?? Rgba(model, s);
            if (blend >= 2)
            {
                Plugin.Log.LogInfo($"MW2 {modelName}: surface {s} '{matName}' see-through (blend {blend})");
                if (tex == null) return Hidden();
                var m = Mw2Fx.SurfaceMaterial(blend, tex);
                if (m != null) return m;
            }
            return Mw2Gun.SurfaceMaterial(template, tex);
        }

        static Material hidden;
        /// A see-through surface without a usable texture draws nothing rather than a grey slab.
        static Material Hidden()
        {
            if (hidden == null)
            {
                hidden = Mw2Fx.SurfaceMaterial(2, Texture2D.blackTexture);
                if (hidden != null) hidden.color = new Color(0f, 0f, 0f, 0f);
            }
            return hidden;
        }

        static Texture2D Rgba(uint model, uint surface)
        {
            uint w, h;
            uint n = Native.mw2_model_surface_rgba(model, surface, out w, out h, null, 0);
            if (n == 0 || n != w * h * 4) return null;
            var px = new byte[n];
            fixed (byte* o = px) Native.mw2_model_surface_rgba(model, surface, out w, out h, o, n);
            Mw2Gun.TopRowFirst(px, (int)w, (int)h);
            var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            tex.SetPixelData(px, 0);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D Texture(uint model, uint surface, Mw2SurfaceInfo si)
        {
            if (si.textureBytes == 0 || si.textureWidth == 0 || si.textureHeight == 0) return null;
            TextureFormat fmt;
            switch (si.textureFormat)
            {
                case 11: fmt = TextureFormat.DXT1; break;
                case 13: fmt = TextureFormat.DXT5; break;
                case 1: fmt = TextureFormat.BGRA32; break;
                default: return null;
            }
            string key = $"{model}:{surface}";
            if (textures.TryGetValue(key, out var cached) && cached != null) return cached;
            var data = new byte[si.textureBytes];
            uint got;
            fixed (byte* p = data) got = Native.mw2_model_surface_texture(model, surface, p, (uint)data.Length);
            Texture2D tex = null;
            if (got == data.Length)
            {
                try
                {
                    tex = new Texture2D((int)si.textureWidth, (int)si.textureHeight, fmt, false);
                    tex.LoadRawTextureData(data);
                    tex.Apply(false, true);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"MW2 prop texture {key} failed: {e.Message}"); tex = null; }
            }
            textures[key] = tex;
            return tex;
        }
    }

    /// Spins a rotor part every frame.
    class Mw2Spin : MonoBehaviour
    {
        public Vector3 axis = Vector3.up;
        public float speed = 900f;
        void Update() => transform.localRotation *= Quaternion.AngleAxis(speed * Time.deltaTime, axis);
    }

    /// A helicopter's chin turret (turret_animate_joint yaw, barrel_animate_joint pitch). It slews
    /// at a limited, eased rate like MW2's vehicle turret, so moving between targets is a sweep,
    /// never a snap; the gun fires only once the barrel is on (OnTarget).
    class Mw2Turret : MonoBehaviour
    {
        public Transform yawT, pitchT;
        public Vector3 muzzleLocal;
        // (playtest 10-07-26: the Attack Helicopter's gun was "snapping to targets, rather than smoothly
        // switching" at 140 / 100 deg/s, 420 deg/s^2)
        public float yawRate = 70f, pitchRate = 50f, accel = 160f;
        public float yawLimit = 120f, pitchUp = -15f, pitchDown = 75f;
        float yaw, pitch, yawVel, pitchVel, err = 180f;

        public Vector3 Muzzle => pitchT != null ? pitchT.TransformPoint(muzzleLocal) : transform.position;
        public Vector3 Barrel => pitchT != null ? pitchT.forward : transform.forward;
        /// Degrees between the barrel and the last aim point.
        public float Error => err;

        public void Aim(Vector3 world, float dt)
        {
            if (yawT == null || dt <= 0f) return;
            var d = transform.InverseTransformDirection(world - Muzzle);
            if (d.sqrMagnitude < 1e-6f) return;
            float wantYaw = Mathf.Clamp(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -yawLimit, yawLimit);
            float wantPitch = Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, pitchUp, pitchDown);
            Step(ref yaw, ref yawVel, Mathf.DeltaAngle(yaw, wantYaw), yawRate, dt);
            Step(ref pitch, ref pitchVel, wantPitch - pitch, pitchRate, dt);
            yawT.localRotation = Quaternion.Euler(0f, yaw, 0f);
            pitchT.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            err = Vector3.Angle(Barrel, world - Muzzle);
        }

        /// Accelerate toward the target angle and brake into it (no overshoot, no snap).
        void Step(ref float angle, ref float vel, float error, float rate, float dt)
        {
            float stop = Mathf.Sqrt(2f * accel * Mathf.Abs(error));
            float want = Mathf.Sign(error) * Mathf.Min(rate, stop);
            vel = Mathf.MoveTowards(vel, want, accel * dt);
            float stepDeg = vel * dt;
            if (Mathf.Abs(stepDeg) > Mathf.Abs(error)) { stepDeg = error; vel = 0f; }
            angle += stepDeg;
        }
    }
}
