using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2 HUD art (killstreak icons, minimap, overlays) from the user's MW2 files, by HUD
    /// material name. Decoded natively to RGBA, rows already in Unity's order.
    static unsafe class Mw2Icons
    {
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<string, (byte[] px, int w, int h)> pixels = new Dictionary<string, (byte[], int, int)>(StringComparer.OrdinalIgnoreCase);

        /// The decoded RGBA (rows bottom-first, like the texture) for CPU compositing. Safe off the
        /// main thread (Mw2Blend's worker fetches with it).
        public static bool Pixels(string name, out byte[] px, out int w, out int h)
        {
            px = null; w = h = 0;
            if (string.IsNullOrEmpty(name)) return false;
            (byte[] px, int w, int h) e;
            bool have;
            lock (pixels) have = pixels.TryGetValue(name, out e);
            if (!have)
            {
                byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
                uint uw, uh, n;
                fixed (byte* p = b) n = Native.mw2_hud_image(p, (UIntPtr)b.Length, out uw, out uh, null, 0);
                if (n > 0 && n == uw * uh * 4)
                {
                    var data = new byte[n];
                    fixed (byte* p = b) fixed (byte* o = data) Native.mw2_hud_image(p, (UIntPtr)b.Length, out uw, out uh, o, n);
                    e = (data, (int)uw, (int)uh);
                }
                lock (pixels) pixels[name] = e;
            }
            if (e.px == null) return false;
            px = e.px; w = e.w; h = e.h;
            return true;
        }

        public static Texture2D Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (cache.TryGetValue(name, out var t)) return t;
            long perf0 = Mw2Perf.Begin();
            try { return Load(name); } finally { Mw2Perf.End("icon:" + name, perf0); }
        }

        static Texture2D Load(string name)
        {
            Texture2D tex = null;
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            uint w, h, n;
            fixed (byte* p = b) n = Native.mw2_hud_image(p, (UIntPtr)b.Length, out w, out h, null, 0);
            if (n > 0 && n == w * h * 4)
            {
                var px = new byte[n];
                fixed (byte* p = b) fixed (byte* o = px) Native.mw2_hud_image(p, (UIntPtr)b.Length, out w, out h, o, n);
                tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                tex.LoadRawTextureData(px);
                tex.Apply(false, true);
            }
            else Plugin.Log.LogWarning($"MW2 HUD image '{name}' not found");
            cache[name] = tex;
            return tex;
        }

        /// An image MW2 draws additively (black is empty: the thermal FOF boxes are drawn on solid
        /// black): its alpha taken from its brightness, for the IMGUI's alpha blending.
        public static void DrawGlow(Rect r, string name, Color tint)
        {
            if (string.IsNullOrEmpty(name)) return;
            string key = name + "#glow";
            if (!cache.TryGetValue(key, out var t))
            {
                t = null;
                if (Pixels(name, out var px, out int w, out int h))
                {
                    var g = (byte[])px.Clone();
                    for (int i = 0; i < g.Length; i += 4) g[i + 3] = Math.Max(g[i], Math.Max(g[i + 1], g[i + 2]));
                    t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    t.LoadRawTextureData(g);
                    t.Apply(false, true);
                }
                cache[key] = t;
            }
            if (t == null) return;
            var old = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        public static void Draw(Rect r, string name, Color tint)
        {
            var t = Get(name);
            if (t == null) return;
            var old = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// MW2's dpad killstreak icons are 8x4 sheets of 48x48 frames (the UAV dish spins).
        public static void DrawSheetFrame(Rect r, string name, int frame, Color tint, int cols = 8, int rows = 4)
        {
            var t = Get(name);
            if (t == null) return;
            if (t.width != t.height * 2) { Draw(r, name, tint); return; }
            frame = ((frame % (cols * rows)) + cols * rows) % (cols * rows);
            int cx = frame % cols, cy = frame / cols;
            // GUI texcoords start bottom-left; sheet frame 0 is top-left.
            var uv = new Rect((float)cx / cols, 1f - (float)(cy + 1) / rows, 1f / cols, 1f / rows);
            var old = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(r, t, uv, true);
            GUI.color = old;
        }
    }
}
