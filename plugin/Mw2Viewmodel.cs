using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    /// The full MW2 first-person viewmodel — viewhands + gun, one skeleton, MW2's own weapon
    /// animations — built at runtime from the user's MW2 files and drawn by the overlay camera.
    /// The native side does all the MW2 work (rig, clips, state machine, pose) and hands over
    /// Unity-space data: the mesh, bindposes, and each frame every bone's pose relative to the
    /// eye. Unity just skins it.
    unsafe class Mw2Viewmodel
    {
        IntPtr vm;
        uint builtFor;
        GameObject root;
        Transform[] bones = new Transform[0];
        float[] pose = new float[0];
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        public bool Exists => vm != IntPtr.Zero && root != null;

        string ColorMap(uint surface)
        {
            var b = new byte[128];
            uint n;
            fixed (byte* p = b) n = Native.mw2_viewmodel_surface_color_map(vm, surface, p, (uint)b.Length);
            return n == 0 || n > b.Length ? "" : System.Text.Encoding.UTF8.GetString(b, 0, (int)n);
        }
        /// Pilot diagnostics: see-through surfaces drawn opaque.
        public static bool OpaqueSeeThrough;
        /// 1 = akimbo's left gun (its left-handed anims); 0 = the right / only one.
        public uint Hand;

        /// World position of one of the gun's tags this frame (tag_flash, tag_brass...): MW2's
        /// eye-relative pose from the last step, placed where the viewmodel is drawn.
        public bool Tag(string name, out Vector3 world)
        {
            world = Vector3.zero;
            if (!Exists) return false;
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            var f = stackalloc float[3];
            int ok;
            fixed (byte* p = b) ok = Native.mw2_viewmodel_tag(vm, p, (UIntPtr)b.Length, f);
            if (ok != 1) return false;
            world = root.transform.TransformPoint(new Vector3(f[0], f[1], f[2]));
            return true;
        }

        // Built viewmodels kept per weapon (inactive) so switching back doesn't rebuild:
        // a build reads the rig, 37 clips and every texture, which hitched ~1 s per switch.
        class Rig { public IntPtr vm; public GameObject root; public Transform[] bones; public float[] pose; public SkinnedMeshRenderer smr; }
        readonly Dictionary<uint, Rig> parked = new Dictionary<uint, Rig>();
        static readonly HashSet<ulong> noRig = new HashSet<ulong>(); // build failed once: don't retry every frame

        void Park()
        {
            if (!Exists) return;
            root.SetActive(false);
            parked[builtFor] = new Rig { vm = vm, root = root, bones = bones, pose = pose, smr = smr };
            PreviousRoot = root;
            vm = IntPtr.Zero; root = null; smr = null; builtFor = 0;
        }

        bool Restore(uint weapon, Transform parent)
        {
            if (!parked.TryGetValue(weapon, out var r) || r.root == null) return false;
            parked.Remove(weapon);
            vm = r.vm; root = r.root; bones = r.bones; pose = r.pose; smr = r.smr; builtFor = weapon;
            if (parent != null) root.transform.SetParent(parent, false);
            var rt = root.transform.Find(RocketName);
            rocket = rt != null ? rt.gameObject : null;
            return true;
        }

        public void Destroy()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            if (vm != IntPtr.Zero) Native.mw2_viewmodel_destroy(vm);
            vm = IntPtr.Zero;
            builtFor = 0;
            foreach (var r in parked.Values)
            {
                if (r.root != null) UnityEngine.Object.Destroy(r.root);
                if (r.vm != IntPtr.Zero) Native.mw2_viewmodel_destroy(r.vm);
            }
            parked.Clear();
        }

        /// Build ahead of time (MW2 mode start) so the first switch to it is instant too.
        public void Prewarm(CharacterBody body, uint weapon, Transform parent, int layer)
        {
            if (weapon == 0 || weapon == builtFor || parked.ContainsKey(weapon)) return;
            uint current = builtFor;
            Park();
            Build(body, weapon, parent, layer);
            Park();
            if (current != 0) Restore(current, parent);
        }

        /// `look`: show the gun as this weapon (an alternate GL / shotgun keeps its parent rifle's
        /// attachments); 0 = the weapon itself.
        public void Build(CharacterBody body, uint weapon, Transform parent, int layer, uint look = 0)
        {
            uint camoWeapon = look != 0 ? look : weapon;
            Native.CamoIds.TryGetValue(camoWeapon, out uint camoId);
            uint key = weapon | (look << 12) | (camoId << 24); // weapon indices stay under 4096, camos under 256
            if (key == builtFor && Exists) return;
            Park();
            if (Restore(key, parent)) return;
            building = key;
            ulong rigKey = key | ((ulong)Hand << 32);
            if (noRig.Contains(rigKey)) return;
            vm = Hand == 1 ? Native.mw2_viewmodel_build_left(weapon) : look != 0 ? Native.mw2_viewmodel_build_as(weapon, look) : Native.mw2_viewmodel_build(weapon);
            if (vm == IntPtr.Zero) { noRig.Add(rigKey); Plugin.Log.LogWarning($"No MW2 viewmodel rig for weapon {weapon} (hand {Hand})."); return; }
            if (Native.mw2_viewmodel_info(vm, out var info) != 1) { Destroy(); return; }

            int n = (int)info.vertexCount, b = (int)info.boneCount;
            var pos = new float[n * 3];
            var nor = new float[n * 3];
            var uv = new float[n * 2];
            var bi = new int[n * 4];
            var bw = new float[n * 4];
            var idx = new uint[info.indexCount];
            var bp = new float[b * 16];
            int ok;
            fixed (float* p = pos) fixed (float* q = nor) fixed (float* t = uv) fixed (int* i = bi) fixed (float* w = bw) fixed (uint* x = idx) fixed (float* m = bp)
                ok = Native.mw2_viewmodel_mesh(vm, p, q, t, i, w, x, m);
            if (ok != 1) { Destroy(); return; }

            var mesh = new Mesh { name = $"mw2_viewmodel_{weapon}", indexFormat = IndexFormat.UInt32 };
            var verts = new Vector3[n];
            var norms = new Vector3[n];
            var uvs = new Vector2[n];
            var weights = new BoneWeight[n];
            for (int v = 0; v < n; v++)
            {
                verts[v] = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                norms[v] = new Vector3(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]);
                uvs[v] = new Vector2(uv[v * 2], uv[v * 2 + 1]);
                weights[v] = new BoneWeight
                {
                    boneIndex0 = bi[v * 4], weight0 = bw[v * 4],
                    boneIndex1 = bi[v * 4 + 1], weight1 = bw[v * 4 + 1],
                    boneIndex2 = bi[v * 4 + 2], weight2 = bw[v * 4 + 2],
                    boneIndex3 = bi[v * 4 + 3], weight3 = bw[v * 4 + 3],
                };
            }
            var bind = new Matrix4x4[b];
            for (int k = 0; k < b; k++)
            {
                var mm = new Matrix4x4();
                for (int e = 0; e < 16; e++) mm[e] = bp[k * 16 + e]; // column-major both sides
                bind[k] = mm;
            }
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.boneWeights = weights;
            var white = new Color32[n];
            for (int v = 0; v < n; v++) white[v] = new Color32(255, 255, 255, 255);
            mesh.colors32 = white; // see-through (particle) shaders multiply by vertex colour
            mesh.bindposes = bind;

            var template = Mw2Gun.TemplateMaterial(body, out string shaderNote);
            var mats = new List<Material>();
            mesh.subMeshCount = (int)info.surfaceCount;
            for (uint s = 0; s < info.surfaceCount; s++)
            {
                if (Native.mw2_viewmodel_surface(vm, s, out var si) != 1) continue;
                var tris = new int[si.indexCount];
                for (int k = 0; k < si.indexCount; k++) tris[k] = (int)idx[si.indexStart + k];
                mesh.SetTriangles(tris, (int)s);
                // The heartbeat sensor's screen shows MW2's motion tracker (Mw2Heartbeat), not its blank image.
                if (ColorMap(s) == "motion_tracker_screen_col")
                {
                    var screen = Mw2Fx.SurfaceMaterial(2, Mw2Heartbeat.Texture);
                    if (screen != null)
                    {
                        // The screen's UVs cover part of its image: stretch the display over exactly them.
                        Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
                        foreach (int t in tris) { lo = Vector2.Min(lo, uvs[t]); hi = Vector2.Max(hi, uvs[t]); }
                        if (hi.x > lo.x && hi.y > lo.y)
                        {
                            var k = new Vector2(1f / (hi.x - lo.x), 1f / (hi.y - lo.y));
                            screen.mainTextureScale = k;
                            screen.mainTextureOffset = new Vector2(-lo.x * k.x, -lo.y * k.y);
                        }
                        screen.renderQueue = 3100;
                        if (screen.HasProperty("_InvFade")) screen.SetFloat("_InvFade", 3000f);
                        mats.Add(screen);
                        continue;
                    }
                }
                // Sight lenses, reticles and glows (ACOG / red dot / holo / thermal) are see-through in MW2.
                int blend = Native.mw2_viewmodel_surface_blend(vm, s);
                if (blend >= 2)
                {
                    // Some sights' glass and dot sit a whole texture tile off (the MTAR's: u -1..0). MW2
                    // wraps; these materials clamp, so the dot shrank onto the black border and the lens
                    // smeared its rim across the glass (playtest 10-05-26: "the mtar red dot is broken").
                    // Shift the surface by whole tiles back over 0..1.
                    Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
                    foreach (int t in tris) { lo = Vector2.Min(lo, uvs[t]); hi = Vector2.Max(hi, uvs[t]); }
                    var shift = new Vector2(Mathf.Floor((lo.x + hi.x) * 0.5f), Mathf.Floor((lo.y + hi.y) * 0.5f));
                    if (tris.Length > 0 && shift != Vector2.zero)
                    {
                        var moved = new HashSet<int>();
                        foreach (int t in tris) if (moved.Add(t)) uvs[t] -= shift;
                        mesh.uv = uvs;
                    }
                }
                // Reticle glows (red dot / holo) are white DXT1 images with no alpha: rebuilt as MW2's red.
                bool reticle = blend == 2 && si.textureFormat == 11;
                var tex = Texture(s, si);
                if (reticle && tex != null) tex = ReticleTexture(tex);
                Material see = blend >= 2 && tex != null && !OpaqueSeeThrough ? Mw2Fx.SurfaceMaterial(blend, tex) : null;
                if (see != null) see.renderQueue = 3100; // after the gun's opaque surfaces
                // RoR2 runs soft particles: these shaders fade out near whatever is behind them (full
                // only ~1 m off it) - a lens or the laptop's display millimetres off the glass vanished.
                if (see != null && see.HasProperty("_InvFade")) see.SetFloat("_InvFade", 3000f);
                // Glass. MW2's lens technique shows the image as a tinted reflection, not raw:
                // - reflex / holo windows darker, clearer, neutral (drawn raw, the MTAR's lens was a
                //   mottled yellow disc, playtest 10-05-26);
                // - scope eyepieces denser: MW2 never shows the tube's inside (the Intervention's own
                //   lettering showed through mirrored, playtest 10-05-26).
                string cmap = ColorMap(s) ?? "";
                if (see != null && !reticle && cmap.IndexOf("lens", StringComparison.OrdinalIgnoreCase) >= 0 && see.HasProperty("_TintColor"))
                {
                    bool scope = cmap.IndexOf("scope", StringComparison.OrdinalIgnoreCase) >= 0 || cmap.IndexOf("acog", StringComparison.OrdinalIgnoreCase) >= 0;
                    see.SetColor("_TintColor", see.GetColor("_TintColor") * (scope ? new Color(0.45f, 0.5f, 0.55f, 1.6f) : new Color(0.55f, 0.6f, 0.65f, 0.55f)));
                }

                if (see != null && reticle)
                {
                    // MW2's reticle technique draws the glow far smaller than the lens quad it sits on;
                    // shrink it about the centre (clamped: the black border adds nothing).
                    // Measured against MW2's own ADS shot (a reference screenshot of the holographic sight):
                    // the holo ring is ~0.17 of the window width; the raw image on the lens quad drew 0.34 at 2.4.
                    // The plain red dots draw ~1/40 of the window (shrunk 4.8x like the holo ring they came
                    // out ~6 px at 1440, QA 10-05-26).
                    float k = (ColorMap(s) ?? "").IndexOf("eotech", StringComparison.OrdinalIgnoreCase) >= 0 ? 4.8f : 2.6f;
                    see.mainTextureScale = new Vector2(k, k);
                    see.mainTextureOffset = new Vector2((1f - k) * 0.5f, (1f - k) * 0.5f);
                }
                mats.Add(see ?? Mw2Gun.SurfaceMaterial(template, tex));
            }
            mesh.RecalculateBounds();

            root = new GameObject("MW2 Viewmodel");
            root.layer = layer;
            root.transform.SetParent(parent, false);
            bones = new Transform[b];
            for (int k = 0; k < b; k++)
            {
                var g = new GameObject($"bone{k}");
                g.layer = layer;
                g.transform.SetParent(root.transform, false);
                bones[k] = g.transform;
            }
            var meshGo = new GameObject("MW2 Viewmodel Mesh");
            meshGo.layer = layer;
            meshGo.transform.SetParent(root.transform, false);
            smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = root.transform;
            smr.sharedMaterials = mats.ToArray();
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = ShadowCastingMode.Off;
            smr.quality = SkinQuality.Bone4;
            pose = new float[b * 7];
            builtFor = key;
            gunBone = -2;
            BuildRocket(body, weapon, layer);
            Plugin.Log.LogInfo($"MW2 viewmodel built: {b} bones, {n} verts, {info.indexCount / 3} tris, {info.surfaceCount} surfaces, shader {shaderNote}, layer {layer}");
        }

        // The weapon being built: builtFor is only set once Build finishes, so it can't key the
        // texture cache (every weapon used to share weapon 0's entries).
        uint building;
        SkinnedMeshRenderer smr;

        /// MW2's red dot / holo reticle images are white glows (DXT1, no alpha) that MW2's reticle
        /// technique draws as solid red over whatever is behind the glass. Rebuilt as red with the
        /// image's brightness as alpha (alpha-blended, so it reads over bright smoke and sky), mipmapped
        /// so the shrunk ring stays smooth.
        static Texture2D ReticleTexture(Texture2D src)
        {
            Color32[] px;
            try { px = src.GetPixels32(); } catch { return src; }
            var red = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                int l = Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b));
                red[i] = new Color32(255, 28, 18, (byte)Mathf.Min(255, l * 3 / 2));
            }
            var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            t.SetPixels32(red);
            t.Apply(true, true);
            return t;
        }

        Texture2D Texture(uint surface, Mw2SurfaceInfo si)
        {
            if (si.textureBytes == 0 || si.textureWidth == 0 || si.textureHeight == 0) return null;
            TextureFormat fmt;
            switch (si.textureFormat)
            {
                case 11: fmt = TextureFormat.DXT1; break;
                case 13: fmt = TextureFormat.DXT5; break;
                case 1: fmt = TextureFormat.BGRA32; break;
                default: fmt = TextureFormat.RGBA32; break; // DXT3 etc.: decoded natively below
            }
            string key = $"{building}:{surface}:{si.textureWidth}x{si.textureHeight}:{si.textureBytes}";
            if (textures.TryGetValue(key, out var cached)) return cached;
            if (fmt == TextureFormat.RGBA32)
            {
                // Unity has no DXT3 (the Stinger's colour map): the sim decodes it (10-05-26: it drew
                // untextured, pale and see-through).
                uint dw, dh;
                uint dn = Native.mw2_viewmodel_surface_rgba(vm, surface, out dw, out dh, null, 0);
                Texture2D dec = null;
                if (dn != 0 && dn == dw * dh * 4)
                {
                    var px = new byte[dn];
                    fixed (byte* o = px) Native.mw2_viewmodel_surface_rgba(vm, surface, out dw, out dh, o, dn);
                    Mw2Gun.TopRowFirst(px, (int)dw, (int)dh);
                    dec = new Texture2D((int)dw, (int)dh, TextureFormat.RGBA32, true) { filterMode = FilterMode.Trilinear };
                    dec.SetPixelData(px, 0);
                    dec.Apply(true, true);
                }
                else Plugin.Log.LogWarning($"MW2 viewmodel texture format {si.textureFormat} (surface {surface}) didn't decode");
                textures[key] = dec;
                return dec;
            }
            // Painted surfaces (the colour map's alpha masks MW2's paint: a black ACOG, camos).
            var rgba = new byte[si.textureWidth * si.textureHeight * 4];
            uint pw, ph, pn;
            fixed (byte* p = rgba) pn = Native.mw2_viewmodel_surface_painted(vm, surface, out pw, out ph, p, (uint)rgba.Length);
            if (pn != 0 && pn == rgba.Length && pw == si.textureWidth && ph == si.textureHeight)
            {
                try
                {
                    // One level, like the compressed upload (LoadRawTextureData wants every mip's bytes).
                    var painted = new Texture2D((int)pw, (int)ph, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                    painted.LoadRawTextureData(rgba);
                    painted.Apply(false, true);
                    textures[key] = painted;
                    return painted;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"MW2 painted texture failed ({e.Message}); unpainted"); }
            }
            var data = new byte[si.textureBytes];
            uint got;
            fixed (byte* p = data) got = Native.mw2_viewmodel_surface_texture(vm, surface, p, (uint)data.Length);
            Texture2D tex = null;
            if (got == data.Length)
            {
                try
                {
                    tex = new Texture2D((int)si.textureWidth, (int)si.textureHeight, fmt, false);
                    tex.LoadRawTextureData(data);
                    tex.Apply(false, fmt != TextureFormat.DXT1); // DXT1 stays readable: reticles are rebuilt from it
                }
                catch (Exception e) { Plugin.Log.LogWarning($"MW2 viewmodel texture failed: {e.Message}"); tex = null; }
            }
            textures[key] = tex;
            return tex;
        }

        /// Debug (F10): log every submesh's material and render what the overlay camera sees
        /// to PNGs, once with the game shader and once unlit, to compare with the offline render.
        public void Dump(Camera overlay, string dir)
        {
            if (!Exists || overlay == null || smr == null) { Plugin.Log.LogInfo("[dump] no viewmodel"); return; }
            Directory.CreateDirectory(dir);
            var mesh = smr.sharedMesh;
            var uv = mesh.uv;
            var mats = smr.sharedMaterials;
            Plugin.Log.LogInfo($"[dump] weapon {builtFor}: {mesh.vertexCount} verts, {mesh.subMeshCount} submeshes, {mats.Length} materials, {uv.Length} uvs");
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var m = s < mats.Length ? mats[s] : null;
                var t = m != null ? m.mainTexture as Texture2D : null;
                var tris = mesh.GetTriangles(s);
                string uv0 = tris.Length > 0 ? uv[tris[0]].ToString("F4") : "-";
                string tex = t != null ? $"{t.width}x{t.height} {t.format} mips {t.mipmapCount}" : "none";
                string st = m != null ? $"{m.mainTextureScale}/{m.mainTextureOffset}" : "-";
                string kw = m != null ? string.Join(" ", m.shaderKeywords) : "";
                Plugin.Log.LogInfo($"[dump] sub {s}: {tris.Length} idx, uv[first] {uv0}, shader {(m != null ? m.shader.name : "null")}, tex {tex}, ST {st}, kw [{kw}]");
            }
            string stamp = DateTime.Now.ToString("HHmmss");
            Capture(overlay, System.IO.Path.Combine(dir, $"vm_{stamp}_game.png"));
            var unlit = new[] { "Unlit/Texture", "Legacy Shaders/Diffuse", "Sprites/Default", "UI/Default" }
                .Select(Mw2Fx.FindShader).FirstOrDefault(sh => sh != null && sh.isSupported);
            if (unlit != null)
            {
                var temp = mats.Select(o => new Material(unlit) { mainTexture = o != null ? o.mainTexture : null }).ToArray();
                smr.sharedMaterials = temp;
                Capture(overlay, System.IO.Path.Combine(dir, $"vm_{stamp}_{unlit.name.Replace('/', '_')}.png"));
                smr.sharedMaterials = mats;
                foreach (var m in temp) UnityEngine.Object.Destroy(m);
            }
            var shaders = Resources.FindObjectsOfTypeAll<Shader>().Select(sh => sh.name)
                .Where(n => n.StartsWith("Unlit") || n.StartsWith("Hopoo Games/Deferred") || n.StartsWith("Legacy")).Distinct();
            Plugin.Log.LogInfo($"[dump] wrote vm_{stamp}_*.png to {dir}; unlit={(unlit != null ? unlit.name : "none")}; shaders: {string.Join(", ", shaders)}");
        }

        static void Capture(Camera cam, string path)
        {
            const int w = 1280, h = 720;
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var target = cam.targetTexture; var clear = cam.clearFlags; var bg = cam.backgroundColor; var rect = cam.rect;
            cam.targetTexture = rt;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.27f, 0.35f, 0.43f, 1f);
            cam.rect = new Rect(0, 0, 1, 1);
            cam.Render();
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var img = new Texture2D(w, h, TextureFormat.RGB24, false);
            img.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            img.Apply();
            RenderTexture.active = active;
            cam.targetTexture = target; cam.clearFlags = clear; cam.backgroundColor = bg; cam.rect = rect;
            cam.ResetAspect();
            File.WriteAllBytes(path, img.EncodeToPNG());
            UnityEngine.Object.Destroy(img);
            RenderTexture.ReleaseTemporary(rt);
        }

        // Launchers: MW2's loaded rocket (the weapon's rocketModel) rides the gun's tag_clip, so the
        // reload anim carries the new one in; it is gone once fired (mw2_viewmodel_rocket_visible).
        const string RocketName = "MW2 Viewmodel Rocket";
        static readonly byte[] TagClip = System.Text.Encoding.UTF8.GetBytes("tag_clip");
        GameObject rocket;

        bool rocketWas;

        void BuildRocket(CharacterBody body, uint weapon, int layer)
        {
            rocket = null;
            var name = new byte[96];
            uint n;
            fixed (byte* p = name) n = Native.mw2_weapon_rocket_model(weapon, p, (uint)name.Length);
            if (n == 0) return;
            string model = System.Text.Encoding.UTF8.GetString(name, 0, (int)Math.Min(n, (uint)name.Length));
            rocket = Mw2Prop.Build(model, body, 0.0254f); // the viewmodel's inches -> metres, same axis mirror
            if (rocket == null) return;
            rocket.name = RocketName;
            foreach (var t in rocket.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            foreach (var r in rocket.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
            rocket.transform.SetParent(root.transform, false);
            Plugin.Log.LogInfo($"MW2 viewmodel rocket {model} on tag_clip");
        }

        void PoseRocket(IntPtr sim)
        {
            if (rocket == null) return;
            bool show = Native.mw2_viewmodel_rocket_visible(sim) == 1;
            var f = stackalloc float[7];
            int ok;
            fixed (byte* p = TagClip) ok = Native.mw2_viewmodel_tag_pose(vm, p, (UIntPtr)TagClip.Length, f);
            bool on = show && ok == 1;
            if (on != rocketWas && Mw2Cinema.Recording) Plugin.Log.LogInfo($"[rocket] frame {Mw2Cinema.Frame}: {(on ? "shown" : "hidden")} (visible {show}, tag {ok}, pilot slot {PilotSlot})");
            rocketWas = on;
            rocket.SetActive(on);
            if (ok != 1) return;
            rocket.transform.localPosition = new Vector3(f[0], f[1], f[2]);
            rocket.transform.localRotation = new Quaternion(f[3], f[4], f[5], f[6]);
        }


        static readonly byte[] soundBuf = new byte[1024];

        /// Sound aliases MW2's anim notetracks fired since the last call (reload clip out / in,
        /// bolt open / close...): the first-person weapon sounds.
        public string[] TakeSounds()
        {
            if (!Exists) return Array.Empty<string>();
            uint n;
            fixed (byte* p = soundBuf) n = Native.mw2_viewmodel_sounds(vm, p, (uint)soundBuf.Length);
            if (n == 0) return Array.Empty<string>();
            return System.Text.Encoding.UTF8.GetString(soundBuf, 0, (int)n).Split(new[] { (char)10 }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// Pilot showcase (the animatic relay): every viewmodel holds this clip slot at PilotFrac
        /// instead of the sim's anims; -1 = off.
        public static int PilotSlot = -1;
        /// Pilot showcase: seconds a gun swap glides from the old gun's pose to the new one's (0 = snap, as in play).
        public static float SwapBlend;
        /// Pilot showcase: the swap the pilot just made (only those dissolve; anything else rebuilt the
        /// viewmodel - one frame of a long-parked gun appeared mid-SPAS, 10-05-26).
        public static int SwapsExpected;
        /// The native rig (pilot: the swap glide reads the one it replaced).
        public IntPtr Handle => vm;
        /// The rig on show, and the one it replaced (parked): the showcase's swap dissolve shows both.
        public GameObject RootObject => root;
        public GameObject PreviousRoot { get; private set; }
        public static float PilotFrac;
        /// Seconds a held clip glides in and back out (the inspect's chamber check eases).
        public static float PilotGlide = 0.12f;
        bool pilotHeld;

        /// An inspect (MW2 has none - playtest 10-06-26: "it would be so cool"): the whole rig, gun
        /// and hands together, turned about the gun's root by these Euler degrees (view space: x pitch,
        /// y yaw, z roll) and moved by InspectShift (a fraction of the gun's offset from the view centre,
        /// so it comes toward the middle). Zero = the plain pose.
        public static Vector3 InspectEuler, InspectShift;
        /// A notetrack sound the pilot plays itself at the moment it should land (the inspect's rack).
        public static string SkipAlias;
        int gunBone = -2;

        // Two-sided while inspected: MW2 models only what first person sees - turned by hand, the
        // ACR's stock showed its missing right side as an open shell and the reaching arm its cut-off
        // sleeve end (playtest 10-07-26: "the emptiness of the acr buttstock"). RoR2's lit shader has no
        // cull switch, so a copy of the mesh wound the other way (normals flipped) draws the inner
        // faces where an opening shows them; wherever the outside is there, depth hides the copy.
        const string BackName = "MW2 Viewmodel Back";
        static bool twoSidedLogged;

        void TwoSided(bool on)
        {
            if (root == null || smr == null || smr.sharedMesh == null) return;
            var back = root.transform.Find(BackName);
            if (back == null)
            {
                if (!on) return;
                back = BuildBack().transform;
            }
            var r = back.GetComponent<SkinnedMeshRenderer>();
            if (r == null || r.enabled == on) return;
            r.enabled = on;
            if (!twoSidedLogged) { twoSidedLogged = true; Plugin.Log.LogInfo($"[inspect] two-sided {(on ? "on" : "off")}: inner faces of {smr.sharedMesh.vertexCount} verts"); }
        }

        GameObject BuildBack()
        {
            var src = smr.sharedMesh;
            var m = UnityEngine.Object.Instantiate(src);
            m.name = src.name + " (inside)";
            var n = src.normals;
            for (int i = 0; i < n.Length; i++) n[i] = -n[i];
            m.normals = n;
            var tg = src.tangents;
            if (tg.Length == n.Length)
            {
                for (int i = 0; i < tg.Length; i++) tg[i] = new Vector4(-tg[i].x, -tg[i].y, -tg[i].z, tg[i].w);
                m.tangents = tg;
            }
            // The gun's surfaces only: the arms' cut-off sleeve ends drew as big dark blocks from the
            // inside (10-07-26), and see-through sight glass would show twice.
            var mats = smr.sharedMaterials;
            var kept = new List<string>(); var skipped = new List<string>();
            for (int sm = 0; sm < m.subMeshCount; sm++)
            {
                string cm = vm != IntPtr.Zero ? ColorMap((uint)sm).ToLowerInvariant() : "";
                bool arms = cm.Contains("hand") || cm.Contains("arm") || cm.Contains("glove") || cm.Contains("sleeve") || cm.Contains("skin") || cm.Contains("watch");
                bool glass = sm < mats.Length && mats[sm] != null && mats[sm].renderQueue >= 2500;
                var t = m.GetTriangles(sm);
                if (arms || glass) { m.SetTriangles(new int[0], sm); skipped.Add(cm); continue; }
                for (int i = 0; i + 2 < t.Length; i += 3) { int x = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = x; }
                m.SetTriangles(t, sm);
                kept.Add(cm);
            }
            Plugin.Log.LogInfo($"[inspect] inner faces for {string.Join(", ", kept)}; not for {string.Join(", ", skipped)}");
            var go = new GameObject(BackName);
            go.layer = smr.gameObject.layer;
            go.transform.SetParent(root.transform, false);
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = m;
            r.bones = smr.bones;
            r.rootBone = smr.rootBone;
            r.sharedMaterials = smr.sharedMaterials;
            r.updateWhenOffscreen = true;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.quality = SkinQuality.Bone4;
            r.enabled = false;
            return go;
        }

        void Inspect()
        {
            TwoSided(InspectEuler != Vector3.zero || InspectShift != Vector3.zero);
            if (InspectEuler == Vector3.zero && InspectShift == Vector3.zero)
            {
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                return;
            }
            if (gunBone == -2)
            {
                var b = System.Text.Encoding.UTF8.GetBytes("j_gun");
                fixed (byte* p = b) gunBone = Native.mw2_viewmodel_find_bone(vm, p, (UIntPtr)b.Length);
            }
            var gun = gunBone >= 0 && gunBone < bones.Length ? bones[gunBone].localPosition : Vector3.zero;
            // Turned about the chest (below the view, just in front), not the gun: MW2's arms are open
            // meshes cut off near the shoulders, and swung about the gun the cut ends came into view
            // (playtest 10-06-26: "we see the insides of the arms"). The gun turns by the same angles.
            var pivot = new Vector3(0f, gun.y * 1.5f, gun.z * 0.15f);
            var r = Quaternion.Euler(InspectEuler);
            var shift = Vector3.Scale(new Vector3(-gun.x, -gun.y, gun.magnitude), InspectShift);
            root.transform.localRotation = r;
            root.transform.localPosition = pivot - r * pivot + shift;
        }

        Mesh probeMesh;
        /// Pilot: how near the gun and hands come to the eye - the smallest depth in front of the view
        /// camera of any vertex (metres; the overlay clips at 0.01) - and that vertex in view space.
        int[] rearVerts;

        bool ArmsOrGlass(int sm)
        {
            string cm = vm != IntPtr.Zero ? ColorMap((uint)sm).ToLowerInvariant() : "";
            var mats = smr.sharedMaterials;
            return cm.Contains("hand") || cm.Contains("arm") || cm.Contains("glove") || cm.Contains("sleeve") || cm.Contains("skin") || cm.Contains("watch")
                || (sm < mats.Length && mats[sm] != null && mats[sm].renderQueue >= 2500);
        }

        /// Pilot, at rest: the gun's rearmost vertices (its last 3 cm toward the shoulder) - where MW2
        /// cut the model off, never meant to be seen (the inspect's open stock end, 10-07-26).
        public int MarkRear()
        {
            rearVerts = null;
            if (!Exists || smr == null || root == null || root.transform.parent == null) return 0;
            if (probeMesh == null) probeMesh = new Mesh();
            smr.BakeMesh(probeMesh);
            var m = root.transform.parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            var v = probeMesh.vertices;
            var gun = new HashSet<int>();
            for (int sm = 0; sm < smr.sharedMesh.subMeshCount; sm++)
                if (!ArmsOrGlass(sm)) foreach (int i in smr.sharedMesh.GetTriangles(sm)) gun.Add(i);
            float min = float.MaxValue;
            foreach (int i in gun) min = Mathf.Min(min, m.MultiplyPoint3x4(v[i]).z);
            var rear = new List<int>();
            foreach (int i in gun) if (m.MultiplyPoint3x4(v[i]).z < min + 0.03f) rear.Add(i);
            rearVerts = rear.ToArray();
            return rearVerts.Length;
        }

        /// Pilot: how many of those rear vertices the recorded frame shows (the overlay's view, its
        /// centre 16:9), and the nearest of them (metres in front of the eye).
        public int RearOnScreen(float tanV, out float nearest)
        {
            nearest = float.NaN;
            if (rearVerts == null || !Exists || smr == null) return -1;
            smr.BakeMesh(probeMesh);
            var m = root.transform.parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            var v = probeMesh.vertices;
            float tanH = tanV * 16f / 9f;
            int n = 0;
            nearest = float.MaxValue;
            foreach (int i in rearVerts)
            {
                var q = m.MultiplyPoint3x4(v[i]);
                if (q.z <= 0.01f || Mathf.Abs(q.x) > q.z * tanH || Mathf.Abs(q.y) > q.z * tanV) continue;
                n++;
                nearest = Mathf.Min(nearest, q.z);
            }
            if (n == 0) nearest = float.NaN;
            return n;
        }

        int[] stockTris;

        /// Pilot, at rest: the stock's triangles (gun surfaces, all three corners in its last 30 cm
        /// toward the shoulder).
        public int MarkStock()
        {
            stockTris = null;
            if (!Exists || smr == null || root == null || root.transform.parent == null) return 0;
            if (probeMesh == null) probeMesh = new Mesh();
            smr.BakeMesh(probeMesh);
            var m = root.transform.parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            var v = probeMesh.vertices;
            float min = float.MaxValue;
            var mesh = smr.sharedMesh;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
                if (!ArmsOrGlass(sm)) foreach (int i in mesh.GetTriangles(sm)) min = Mathf.Min(min, m.MultiplyPoint3x4(v[i]).z);
            var tris = new List<int>();
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                if (ArmsOrGlass(sm)) continue;
                var t = mesh.GetTriangles(sm);
                for (int i = 0; i + 2 < t.Length; i += 3)
                    if (m.MultiplyPoint3x4(v[t[i]]).z < min + 0.3f && m.MultiplyPoint3x4(v[t[i + 1]]).z < min + 0.3f && m.MultiplyPoint3x4(v[t[i + 2]]).z < min + 0.3f)
                    { tris.Add(t[i]); tris.Add(t[i + 1]); tris.Add(t[i + 2]); }
            }
            stockTris = tris.ToArray();
            return stockTris.Length / 3;
        }

        /// Pilot: the stock seen from inside - the screen area (share of the recorded 16:9 frame) of its
        /// triangles facing away from the eye, where MW2 left it an open shell (playtest 10-07-26: "i could
        /// see through the guns buttstock"); and the area facing the eye.
        public float StockInside(float tanV, out float facing)
        {
            facing = 0f;
            if (stockTris == null || !Exists || smr == null) return -1f;
            smr.BakeMesh(probeMesh);
            var m = root.transform.parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            var v = probeMesh.vertices;
            float tanH = tanV * 16f / 9f, inside = 0f;
            for (int i = 0; i + 2 < stockTris.Length; i += 3)
            {
                var a = m.MultiplyPoint3x4(v[stockTris[i]]);
                var b = m.MultiplyPoint3x4(v[stockTris[i + 1]]);
                var c = m.MultiplyPoint3x4(v[stockTris[i + 2]]);
                if (a.z < 0.01f || b.z < 0.01f || c.z < 0.01f) continue;
                var pa = new Vector2(a.x / (a.z * tanH), a.y / (a.z * tanV));
                var pb = new Vector2(b.x / (b.z * tanH), b.y / (b.z * tanV));
                var pc = new Vector2(c.x / (c.z * tanH), c.y / (c.z * tanV));
                var mid = (pa + pb + pc) / 3f;
                if (Mathf.Abs(mid.x) > 1f || Mathf.Abs(mid.y) > 1f) continue;
                float area = Mathf.Abs((pb.x - pa.x) * (pc.y - pa.y) - (pc.x - pa.x) * (pb.y - pa.y)) * 0.5f / 4f;
                var n = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(n, a) > 0f) inside += area; else facing += area;
            }
            return inside;
        }

        GameObject rayGo;
        MeshCollider rayCol;
        Mesh rayMesh;
        bool[] rayTriGun;

        /// Pilot: what the eye sees of the gun, by rays through the recorded 16:9 frame (gridW x gridH)
        /// against the posed mesh (gun and arms; not the sight glass): rays whose first hit is the back
        /// of a gun surface - looking into MW2's open shell (playtest 10-07-26: "i could see through the guns
        /// buttstock") - and rays that hit the gun at all; the same for the arms' backs.
        public int SeeInside(float tanV, int gridW, int gridH, out int gunHits, out int armBacks)
        {
            gunHits = armBacks = 0;
            if (!Exists || smr == null || root == null || root.transform.parent == null) return -1;
            if (probeMesh == null) probeMesh = new Mesh();
            smr.BakeMesh(probeMesh);
            var mesh = smr.sharedMesh;
            if (rayGo == null) rayMesh = null; // the rig was rebuilt
            if (rayMesh == null)
            {
                rayMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                var tris = new List<int>(); var gun = new List<bool>();
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    string cm = vm != IntPtr.Zero ? ColorMap((uint)sm).ToLowerInvariant() : "";
                    bool arms = cm.Contains("hand") || cm.Contains("arm") || cm.Contains("glove") || cm.Contains("sleeve") || cm.Contains("skin") || cm.Contains("watch");
                    var mats = smr.sharedMaterials;
                    bool glass = sm < mats.Length && mats[sm] != null && mats[sm].renderQueue >= 2500;
                    if (glass) continue;
                    var t = mesh.GetTriangles(sm);
                    tris.AddRange(t);
                    for (int i = 0; i < t.Length / 3; i++) gun.Add(!arms);
                }
                rayMesh.vertices = probeMesh.vertices;
                rayMesh.triangles = tris.ToArray();
                rayTriGun = gun.ToArray();
                rayGo = new GameObject("MW2 Viewmodel Probe") { layer = smr.gameObject.layer };
                rayGo.transform.SetParent(smr.transform, false);
                rayCol = rayGo.AddComponent<MeshCollider>();
            }
            rayMesh.vertices = probeMesh.vertices;
            rayMesh.RecalculateBounds();
            rayCol.sharedMesh = null;
            rayCol.sharedMesh = rayMesh;
            Physics.SyncTransforms(); // the collider where the gun is drawn this frame
            var eye = root.transform.parent;
            float tanH = tanV * 16f / 9f;
            var tri = rayMesh.triangles;
            var v = rayMesh.vertices;
            var l2w = rayGo.transform.localToWorldMatrix;
            bool was = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            int inside = 0;
            try
            {
                for (int y = 0; y < gridH; y++)
                    for (int x = 0; x < gridW; x++)
                    {
                        float nx = ((x + 0.5f) / gridW * 2f - 1f) * tanH, ny = ((y + 0.5f) / gridH * 2f - 1f) * tanV;
                        var dir = eye.TransformDirection(new Vector3(nx, ny, 1f).normalized);
                        if (!Physics.Raycast(eye.position, dir, out var hit, 3f, 1 << rayGo.layer, QueryTriggerInteraction.Ignore) || hit.collider != rayCol) continue;
                        int ti = hit.triangleIndex;
                        if (ti < 0 || ti * 3 + 2 >= tri.Length) continue;
                        var a = l2w.MultiplyPoint3x4(v[tri[ti * 3]]);
                        var b = l2w.MultiplyPoint3x4(v[tri[ti * 3 + 1]]);
                        var c = l2w.MultiplyPoint3x4(v[tri[ti * 3 + 2]]);
                        bool back = Vector3.Dot(Vector3.Cross(b - a, c - a), dir) * BackSign > 0f;
                        bool isGun = ti < rayTriGun.Length && rayTriGun[ti];
                        if (isGun) { gunHits++; if (back) inside++; }
                        else if (back) armBacks++;
                    }
            }
            finally { Physics.queriesHitBackfaces = was; }
            return inside;
        }
        /// Which way round a triangle faces the eye (set by the pilot from the rest pose, where the
        /// gun is all front faces).
        public static float BackSign = 1f;

        public float NearestDepth(out Vector3 at)
        {
            at = Vector3.zero;
            if (!Exists || smr == null || root == null || root.transform.parent == null) return float.NaN;
            if (probeMesh == null) probeMesh = new Mesh();
            smr.BakeMesh(probeMesh);
            var m = root.transform.parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            float best = float.MaxValue;
            foreach (var v in probeMesh.vertices)
            {
                var q = m.MultiplyPoint3x4(v);
                if (q.z < best) { best = q.z; at = q; }
            }
            return best;
        }

        /// A clip slot's length in MW2 play (seconds; 0 = no clip there).
        public float SlotSeconds(int slot) => Exists ? Native.mw2_viewmodel_slot_seconds(vm, (uint)slot) : 0f;

        /// After the camera is placed: advance MW2 animation and pose every bone.
        public void Step(IntPtr sim, float dt, bool visible)
        {
            if (!Exists) return;
            root.SetActive(visible);
            if (sim == IntPtr.Zero) return;
            Native.mw2_viewmodel_set_force_glide(vm, PilotGlide);
            if (PilotSlot >= 0) { Native.mw2_viewmodel_force(vm, PilotSlot, PilotFrac); pilotHeld = true; }
            else if (pilotHeld) { Native.mw2_viewmodel_force(vm, -1, 0f); pilotHeld = false; }
            // The anim runs even when hidden (scoped): its notetracks are the bolt / reload sounds.
            int ok;
            fixed (float* p = pose) ok = Native.mw2_viewmodel_step(vm, sim, dt, p);
            if (!visible) return;
            if (ok != 1) return;
            for (int k = 0; k < bones.Length; k++)
            {
                int o = k * 7;
                bones[k].localPosition = new Vector3(pose[o], pose[o + 1], pose[o + 2]);
                bones[k].localRotation = new Quaternion(pose[o + 3], pose[o + 4], pose[o + 5], pose[o + 6]);
            }
            Inspect();
            PoseRocket(sim);
        }
    }
}
