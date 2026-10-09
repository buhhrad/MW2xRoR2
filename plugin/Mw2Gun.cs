using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    /// The MW2 gun model, built at runtime from the user's own MW2 files: mesh from the
    /// weapon's XModel, colorMap textures from the iwd archives (DXT passed straight to
    /// Unity), lit by RoR2's own shader. Held at the survivor's hand, pointed where they aim.
    unsafe class Mw2Gun
    {
        GameObject go;
        uint builtFor;
        Transform hand;
        CharacterBody owner;
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        public void Destroy()
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            go = null;
            builtFor = 0;
        }

        public void Build(CharacterBody body, uint weapon, float metresPerUnit)
        {
            if (weapon == builtFor && go != null) return;
            Destroy();
            owner = body;
            uint model = Native.mw2_weapon_model(weapon);
            if (model == 0 || Native.mw2_model_info(model, out var info) != 1 || info.vertexCount == 0) return;

            var pos = new float[info.vertexCount * 3];
            var nor = new float[info.vertexCount * 3];
            var uv = new float[info.vertexCount * 2];
            var idx = new uint[info.indexCount];
            int ok;
            fixed (float* p = pos) fixed (float* n = nor) fixed (float* t = uv) fixed (uint* i = idx)
                ok = Native.mw2_model_mesh(model, p, n, t, i);
            if (ok != 1) return;

            // IW4 (x fwd, y left, z up) -> Unity (x right, y up, z fwd). This is a mirror, which
            // by itself turns IW4's counter-clockwise front faces into Unity's clockwise ones,
            // so triangle order is kept as is.
            var vertices = new Vector3[info.vertexCount];
            var normals = new Vector3[info.vertexCount];
            var uvs = new Vector2[info.vertexCount];
            for (int v = 0; v < info.vertexCount; v++)
            {
                vertices[v] = new Vector3(-pos[v * 3 + 1], pos[v * 3 + 2], pos[v * 3]) * metresPerUnit;
                normals[v] = new Vector3(-nor[v * 3 + 1], nor[v * 3 + 2], nor[v * 3]);
                uvs[v] = new Vector2(uv[v * 2], uv[v * 2 + 1]); // raw D3D-order texture data, so no V flip
            }

            var mesh = new Mesh { name = $"mw2_model_{model}", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;

            var model3d = body.modelLocator != null ? body.modelLocator.modelTransform : null;
            var template = TemplateMaterial(body, out string shaderNote);
            var subs = new List<int[]>();
            var mats = new List<Material>();
            for (uint s = 0; s < info.surfaceCount; s++)
            {
                if (Native.mw2_weapon_surface_visible(weapon, s) != 1) continue;
                if (Native.mw2_model_surface(model, s, out var si) != 1 || si.indexCount == 0) continue;
                var tris = new int[si.indexCount];
                for (int k = 0; k + 2 < si.indexCount; k += 3)
                {
                    tris[k] = (int)idx[si.indexStart + k];
                    tris[k + 1] = (int)idx[si.indexStart + k + 1];
                    tris[k + 2] = (int)idx[si.indexStart + k + 2];
                }
                subs.Add(tris);
                mats.Add(SurfaceMaterial(template, Texture(model, s, si)));
            }
            if (subs.Count == 0) return;
            mesh.subMeshCount = subs.Count;
            for (int k = 0; k < subs.Count; k++) mesh.SetTriangles(subs[k], k);
            mesh.RecalculateBounds();

            go = new GameObject("MW2 Gun");
            // Same layer as the survivor's model so RoR2's camera actually draws it.
            if (model3d != null) go.layer = model3d.gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats.ToArray();
            r.shadowCastingMode = ShadowCastingMode.On;
            builtFor = weapon;
            builtScale = metresPerUnit;
            worldScale = metresPerUnit;
            worldLayer = go.layer;
            baseVertices = null;

            hand = null;
            var locator = model3d != null ? model3d.GetComponent<ChildLocator>() : null;
            foreach (var name in new[] { "HandR", "MuzzleRight", "Gun", "Muzzle", "HandL" })
            {
                hand = locator != null ? locator.FindChild(name) : null;
                if (hand != null) break;
            }
            Plugin.Log.LogInfo($"MW2 gun built: {info.vertexCount} verts, {subs.Count}/{info.surfaceCount} surfaces, held at {(hand != null ? hand.name : "body")}, layer {go.layer}, shader {shaderNote}, size {mesh.bounds.size}");
        }

        Texture2D Texture(uint model, uint surface, Mw2SurfaceInfo si)
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
            string key = $"{model}:{surface}";
            if (textures.TryGetValue(key, out var cached)) return cached;
            if (fmt == TextureFormat.RGBA32)
            {
                uint dw, dh;
                uint dn = Native.mw2_model_surface_rgba(model, surface, out dw, out dh, null, 0);
                Texture2D dec = null;
                if (dn != 0 && dn == dw * dh * 4)
                {
                    var px = new byte[dn];
                    fixed (byte* o = px) Native.mw2_model_surface_rgba(model, surface, out dw, out dh, o, dn);
                    TopRowFirst(px, (int)dw, (int)dh);
                    dec = new Texture2D((int)dw, (int)dh, TextureFormat.RGBA32, true);
                    dec.SetPixelData(px, 0);
                    dec.Apply(true, true);
                }
                textures[key] = dec;
                return dec;
            }
            var data = new byte[si.textureBytes];
            uint n;
            fixed (byte* p = data) n = Native.mw2_model_surface_texture(model, surface, p, (uint)data.Length);
            Texture2D tex = null;
            if (n == data.Length)
            {
                try
                {
                    tex = new Texture2D((int)si.textureWidth, (int)si.textureHeight, fmt, false);
                    tex.LoadRawTextureData(data);
                    tex.Apply(false, true);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"MW2 texture {key} ({si.textureWidth}x{si.textureHeight} fmt {si.textureFormat}) failed: {e.Message}");
                    tex = null;
                }
            }
            textures[key] = tex;
            return tex;
        }

        /// RoR2's own lit shader, so the gun is lit like everything else. Falls back to the
        /// survivor's body material, then Unity's Standard.
        static bool loggedTemplate;

        /// One MW2 surface material: MW2's colour map at its own UVs, flat normals, no
        /// emission. RoR2's deferred shader reads garbage normals from an empty normal slot
        /// (the model goes near-black with stray rim highlights), so it gets a flat one.
        internal static Material SurfaceMaterial(Material template, Texture2D tex)
        {
            var mat = template != null ? new Material(template) : new Material(Mw2Fx.FindShader("Standard"));
            mat.shaderKeywords = new string[0];
            if (tex != null) mat.mainTexture = tex;
            mat.mainTextureScale = Vector2.one;
            mat.mainTextureOffset = Vector2.zero;
            foreach (var prop in new[] { "_NormalTex", "_BumpMap" })
                if (mat.HasProperty(prop)) mat.SetTexture(prop, Texture2D.normalTexture);
            foreach (var prop in new[] { "_EmTex", "_FlowHeightmap" })
                if (mat.HasProperty(prop)) mat.SetTexture(prop, Texture2D.blackTexture);
            if (mat.HasProperty("_NormalStrength")) mat.SetFloat("_NormalStrength", 0f);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_EmPower")) mat.SetFloat("_EmPower", 0f);
            return mat;
        }

        /// Native RGBA comes bottom row first (Unity's order for HUD / FX art); MW2's model UVs are
        /// D3D's, top-left origin, and go to Unity raw (as the DXT path's raw data does). Rows back
        /// to top-first for a texture those UVs read - upside down, every third-person character,
        /// head and world gun sampled the wrong parts of its atlas (playtest 10-04-26: the riot shield's
        /// bolts stretched over its corners, the half-black heads).
        internal static void TopRowFirst(byte[] px, int w, int h)
        {
            int row = w * 4;
            var tmp = new byte[row];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * row, b = (h - 1 - y) * row;
                Buffer.BlockCopy(px, a, tmp, 0, row);
                Buffer.BlockCopy(px, b, px, a, row);
                Buffer.BlockCopy(tmp, 0, px, b, row);
            }
        }

        static Shader cachedLit;

        internal static Material TemplateMaterial(CharacterBody body, out string note)
        {
            // Unity's Standard is stripped from RoR2's build (Shader.Find returns it but it
            // draws nothing). Use RoR2's lit deferred shader, found by name: taking "the first
            // textured renderer" on the survivor picked Commando's pistol-spin FX shader (Opaque
            // Cloud Remap - flat white) whenever MW2 mode started inside the drop pod.
            const string Lit = "Hopoo Games/Deferred/Standard";
            if (cachedLit == null) cachedLit = Mw2Fx.FindShader(Lit);
            if (cachedLit != null && cachedLit.isSupported)
            {
                note = $"named:{Lit}";
                return new Material(cachedLit);
            }
            var model = body != null && body.modelLocator != null ? body.modelLocator.modelTransform : null;
            if (model != null)
            {
                foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m != null && m.shader != null && m.HasProperty("_MainTex") && m.shader.name == Lit)
                    {
                        note = $"body:{m.shader.name}";
                        // Only the shader: the survivor's material carries its own tiling/offset
                        // and keywords (limb removal, splatmaps...) that scramble MW2's UVs.
                        if (!loggedTemplate)
                        {
                            loggedTemplate = true;
                            Plugin.Log.LogInfo($"MW2 material template {m.name}: _MainTex_ST {m.mainTextureScale}/{m.mainTextureOffset}, keywords [{string.Join(" ", m.shaderKeywords)}] (not copied)");
                        }
                        return new Material(m.shader);
                    }
                }
            }
            var std = Mw2Fx.FindShader("Standard");
            note = std != null ? "Standard (fallback)" : "none";
            return std != null ? new Material(std) : null;
        }

        static Vector3 ParseOffset(string s, Vector3 fallback)
        {
            var p = s.Split(',');
            if (p.Length != 3) return fallback;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var f)
                && float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var l)
                && float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out var u)
                ? new Vector3(f, l, u) : fallback;
        }

        float builtScale;
        Vector3[] baseVertices;

        /// Rescale the mesh between world scale (third person) and true MW2 inches (overlay).
        void SetScale(float metresPerUnit)
        {
            if (go == null || Mathf.Approximately(builtScale, metresPerUnit)) return;
            var mf = go.GetComponent<MeshFilter>();
            if (baseVertices == null) baseVertices = mf.sharedMesh.vertices;
            float k = metresPerUnit / builtScale;
            var v = new Vector3[baseVertices.Length];
            for (int i = 0; i < v.Length; i++) v[i] = baseVertices[i] * (metresPerUnit / builtScale);
            mf.sharedMesh.vertices = v;
            mf.sharedMesh.RecalculateBounds();
            _ = k;
        }

        /// Called every frame after the camera is placed.
        public void PoseView(Transform cam, bool firstPerson, Vector3 aimDirection, float adsFrac, float metresPerUnit)
        {
            if (go == null || owner == null) return;
            if (firstPerson)
            {
                go.layer = Mw2View.Layer;
                SetScale(Mw2View.InchesToMetres);
                metresPerUnit = Mw2View.InchesToMetres;
                // MW2 viewmodels are authored in view space with the eye at the origin; MW2's
                // arms animation is what holds them lower-right at the hip and brings the sights
                // to the eye in ADS. Until arms exist, blend between two hand-tuned offsets.
                var hip = ParseOffset(Plugin.Instance.ViewHipOffset.Value, new Vector3(6f, -7f, -6f));
                var ads = ParseOffset(Plugin.Instance.ViewAdsOffset.Value, new Vector3(3f, 0f, -3.4f));
                var iw = Vector3.Lerp(hip, ads, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(adsFrac)));
                // IW (forward, left, up) -> Unity camera space (right, up, forward).
                var local = new Vector3(-iw.y, iw.z, iw.x) * metresPerUnit;
                go.transform.SetPositionAndRotation(cam.position + cam.rotation * local, cam.rotation);
                return;
            }
            go.layer = worldLayer;
            SetScale(worldScale);
            Vector3 at = hand != null ? hand.position : owner.corePosition + owner.transform.right * 0.25f;
            go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(aimDirection.sqrMagnitude > 0f ? aimDirection : owner.transform.forward, Vector3.up));
        }

        int worldLayer;
        float worldScale;

        public void SetVisible(bool visible)
        {
            if (go != null && go.activeSelf != visible) go.SetActive(visible);
        }

        public string Debug(Camera cam)
        {
            if (go == null) return "no gun object";
            var r = go.GetComponent<MeshRenderer>();
            var rel = cam != null ? cam.transform.InverseTransformPoint(r.bounds.center) : Vector3.zero;
            bool layerOn = cam != null && (cam.cullingMask & (1 << go.layer)) != 0;
            return $"gun active={go.activeInHierarchy} visible={r.isVisible} layer={go.layer} cameraDrawsLayer={layerOn} relToCam={rel} size={r.bounds.size} shader={r.sharedMaterial?.shader?.name} near={cam?.nearClipPlane}";
        }
    }
}
