using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's 2D layers that blend through the screen's alpha channel (the XP bar: clear alpha, write
    /// the cell shape into it, clip it at the fill edge with MIN, draw the fill against DESTALPHA) run
    /// here as IW4 draws them: Direct3D 9 blending from each material's own state bits, on the CPU.
    /// Every pixel keeps its colour as k * (the game behind) + c and the destination alpha, so the
    /// result is one texture (alpha 1 - k) that leaves see-through what MW2 leaves see-through.
    static unsafe class Mw2Blend
    {
        const uint ColorWriteRgb = 0x0800_0000, ColorWriteAlpha = 0x1000_0000;
        const int Zero = 1, One = 2, SrcColor = 3, InvSrcColor = 4, SrcAlpha = 5, InvSrcAlpha = 6, DestAlpha = 7, InvDestAlpha = 8;

        static readonly Dictionary<string, uint> bits = new Dictionary<string, uint>();

        /// State bits word 0 of a material (0 if the zones didn't give any).
        public static uint Bits(string material)
        {
            if (bits.TryGetValue(material, out var b)) return b;
            var name = System.Text.Encoding.UTF8.GetBytes(material);
            uint* o = stackalloc uint[2];
            int ok;
            fixed (byte* p = name) ok = Native.mw2_material_state_bits(p, (UIntPtr)name.Length, o);
            b = ok == 1 ? o[0] : 0u;
            bits[material] = b;
            return b;
        }

        /// Needs the compositor: writes only colour or only alpha, or reads the destination alpha.
        public static bool Special(uint w0)
        {
            if (w0 == 0) return false;
            uint write = w0 & (ColorWriteRgb | ColorWriteAlpha);
            int src = (int)(w0 & 0xf), dst = (int)((w0 >> 4) & 0xf);
            return write != (ColorWriteRgb | ColorWriteAlpha) || src == DestAlpha || src == InvDestAlpha || dst == DestAlpha || dst == InvDestAlpha;
        }

        public struct Layer
        {
            public string material;
            public Rect rect;
            public Rect uv; // s0, t0 (from the top), width, height
            public Color color;
            public float rotationDeg; // IW4 RotateST: the texture spins about the rect's centre
        }

        /// One composite (a menu's dest-alpha layers): the last finished build is drawn until the next
        /// is ready. The build runs on a worker thread (a kill's XP rebuilt it on the main thread and
        /// hitched in playtests).
        class Cache
        {
            public Texture2D tex;
            public string builtKey, pendingKey;
            public Rect builtRect, pendingRect;
            public volatile Color32[] ready;
            public int readyW, readyH;
            public System.Threading.Tasks.Task worker;
        }

        static readonly Dictionary<int, Cache> caches = new Dictionary<int, Cache>();

        /// Composite `layers` (in draw order) and draw the result. `slot` keeps one cache per group
        /// (the compass and the XP bar each have their own).
        public static void Draw(List<Layer> layers, int slot)
        {
            if (layers.Count == 0) return;
            if (!caches.TryGetValue(slot, out var c)) caches[slot] = c = new Cache();
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            var key = new System.Text.StringBuilder();
            foreach (var l in layers)
            {
                x0 = Mathf.Min(x0, l.rect.xMin); y0 = Mathf.Min(y0, l.rect.yMin);
                x1 = Mathf.Max(x1, l.rect.xMax); y1 = Mathf.Max(y1, l.rect.yMax);
                // The UVs too: the compass letters scroll as the player turns and the d-pad sheets
                // animate (playtest 10-04-26: "the hud feels frozen").
                key.Append(l.material).Append('|').Append(l.rect.x.ToString("F0")).Append(',').Append(l.rect.y.ToString("F0")).Append(',')
                   .Append(l.rect.width.ToString("F0")).Append(',').Append(l.rect.height.ToString("F0")).Append('|')
                   .Append(l.uv.x.ToString("F4")).Append(',').Append(l.uv.y.ToString("F4")).Append(',').Append(l.uv.width.ToString("F4")).Append(',').Append(l.uv.height.ToString("F4")).Append('|')
                   .Append(l.color.ToString()).Append('|').Append(l.rotationDeg.ToString("F1")).Append(';');
            }
            x0 = Mathf.Max(0f, Mathf.Floor(x0)); y0 = Mathf.Max(0f, Mathf.Floor(y0));
            x1 = Mathf.Min(Screen.width, Mathf.Ceil(x1)); y1 = Mathf.Min(Screen.height, Mathf.Ceil(y1));
            int w = (int)(x1 - x0), h = (int)(y1 - y0);
            if (w <= 0 || h <= 0 || w * h > 4_000_000) return;
            string k = key.ToString();
            var done = c.ready;
            if (done != null)
            {
                c.ready = null;
                Upload(c, done, c.readyW, c.readyH);
                c.builtKey = c.pendingKey;
                c.builtRect = c.pendingRect;
            }
            if (k != c.builtKey && k != c.pendingKey && (c.worker == null || c.worker.IsCompleted))
            {
                // Texture fetches and the blending run off the main thread (the first build decodes every
                // layer's image: ~400 ms at load in); the state bits are cheap and stay here.
                var input = new List<(Layer l, uint w0)>();
                foreach (var l in layers) input.Add((l, Bits(l.material)));
                int ox = (int)x0, oy = (int)y0;
                c.pendingKey = k;
                c.pendingRect = new Rect(x0, y0, w, h);
                c.worker = System.Threading.Tasks.Task.Run(() =>
                {
                    var jobs = new List<(Layer l, byte[] px, int tw, int th, uint w0)>();
                    foreach (var (l, w0) in input)
                        if (Mw2Icons.Pixels(l.material, out var px, out int tw, out int th)) jobs.Add((l, px, tw, th, w0));
                    var result = Build(jobs, ox, oy, w, h);
                    c.readyW = w; c.readyH = h;
                    c.ready = result;
                });
            }
            if (c.tex != null && c.builtKey != null) GUI.DrawTexture(c.builtRect, c.tex, ScaleMode.StretchToFill, true);
        }

        static void Upload(Cache c, Color32[] px, int w, int h)
        {
            if (c.tex == null || c.tex.width != w || c.tex.height != h)
            {
                if (c.tex != null) UnityEngine.Object.Destroy(c.tex);
                c.tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            c.tex.SetPixels32(px);
            c.tex.Apply(false, false);
        }

        static Color32[] Build(List<(Layer l, byte[] px, int tw, int th, uint w0)> jobs, int ox, int oy, int w, int h)
        {
            int n = w * h;
            var kk = new float[n];
            var cr = new float[n];
            var cg = new float[n];
            var cb = new float[n];
            var da = new float[n];
            for (int i = 0; i < n; i++) kk[i] = 1f;
            foreach (var (l, px, tw, th, w0) in jobs)
            {
                bool writeRgb = w0 == 0 || (w0 & ColorWriteRgb) != 0;
                bool writeA = w0 == 0 || (w0 & ColorWriteAlpha) != 0;
                int src = w0 == 0 ? SrcAlpha : (int)(w0 & 0xf);
                int dst = w0 == 0 ? InvSrcAlpha : (int)((w0 >> 4) & 0xf);
                int op = w0 == 0 ? 1 : (int)((w0 >> 8) & 7);
                int srcA = (int)((w0 >> 16) & 0xf), dstA = (int)((w0 >> 20) & 0xf), opA = (int)((w0 >> 24) & 7);
                if (opA == 0) { srcA = src; dstA = dst; opA = op; } // no separate alpha: alpha blends like colour
                int rx0 = Mathf.Max(ox, Mathf.FloorToInt(l.rect.xMin)), rx1 = Mathf.Min(ox + w, Mathf.CeilToInt(l.rect.xMax));
                int ry0 = Mathf.Max(oy, Mathf.FloorToInt(l.rect.yMin)), ry1 = Mathf.Min(oy + h, Mathf.CeilToInt(l.rect.yMax));
                // The compass ring turns with the view: the picture spins counter-clockwise by the
                // angle (as Mw2MenuHud.Quad draws it), so each pixel samples the inverse rotation.
                bool rotated = Mathf.Abs(l.rotationDeg) > 0.01f;
                float cos = Mathf.Cos(l.rotationDeg * Mathf.Deg2Rad), sin = Mathf.Sin(l.rotationDeg * Mathf.Deg2Rad);
                float rw = Mathf.Max(l.rect.width, 1e-3f), rh = Mathf.Max(l.rect.height, 1e-3f), cx = l.rect.center.x, cy = l.rect.center.y;
                for (int y = ry0; y < ry1; y++)
                {
                    float v = l.uv.y + ((y + 0.5f - l.rect.y) / rh) * l.uv.height;
                    int ty = Mathf.Clamp((int)((1f - v) * th), 0, th - 1); // texture rows are bottom-first
                    for (int x = rx0; x < rx1; x++)
                    {
                        float u = l.uv.x + ((x + 0.5f - l.rect.x) / rw) * l.uv.width;
                        if (rotated)
                        {
                            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                            float lu = (cos * dx - sin * dy) / rw + 0.5f, lv = (sin * dx + cos * dy) / rh + 0.5f;
                            if (lu < 0f || lu > 1f || lv < 0f || lv > 1f) continue;
                            u = l.uv.x + lu * l.uv.width;
                            ty = Mathf.Clamp((int)((1f - (l.uv.y + lv * l.uv.height)) * th), 0, th - 1);
                        }
                        int tx = Mathf.Clamp((int)(u * tw), 0, tw - 1);
                        int t = (ty * tw + tx) * 4;
                        float sr = px[t] / 255f * l.color.r, sg = px[t + 1] / 255f * l.color.g, sb = px[t + 2] / 255f * l.color.b, sa = px[t + 3] / 255f * l.color.a;
                        int i = (y - oy) * w + (x - ox);
                        float a = da[i];
                        if (writeRgb)
                        {
                            if (op == 0)
                            {
                                kk[i] = 0f; cr[i] = sr; cg[i] = sg; cb[i] = sb;
                            }
                            else if (op == 1 || op == 2 || op == 3)
                            {
                                // Scalar factors (alpha based); colour-based ones use the source colour's mean.
                                float fs = Factor(src, sr, sg, sb, sa, a), fd = Factor(dst, sr, sg, sb, sa, a);
                                // D3D: add = S*fs + D*fd, subtract = S*fs - D*fd, revsubtract = D*fd - S*fs,
                                // with D = k * game + c.
                                float ss = op == 3 ? -fs : fs, sd = op == 2 ? -fd : fd;
                                kk[i] *= sd;
                                cr[i] = sr * ss + cr[i] * sd;
                                cg[i] = sg * ss + cg[i] * sd;
                                cb[i] = sb * ss + cb[i] * sd;
                            }
                        }
                        if (writeA)
                        {
                            float na;
                            switch (opA)
                            {
                                case 0: na = sa; break;
                                case 4: na = Mathf.Min(sa, a); break;
                                case 5: na = Mathf.Max(sa, a); break;
                                default:
                                    float fs = AlphaFactor(srcA, sa, a), fd = AlphaFactor(dstA, sa, a);
                                    na = opA == 2 ? sa * fs - a * fd : opA == 3 ? a * fd - sa * fs : sa * fs + a * fd;
                                    break;
                            }
                            da[i] = Mathf.Clamp01(na);
                        }
                    }
                }
            }
            var outPx = new Color32[n];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float alpha = Mathf.Clamp01(1f - kk[i]);
                    float inv = alpha > 1e-4f ? 1f / alpha : 0f;
                    // Unity textures are bottom-first; the buffer's rows run down the screen.
                    outPx[(h - 1 - y) * w + x] = new Color32(
                        (byte)(Mathf.Clamp01(cr[i] * inv) * 255f), (byte)(Mathf.Clamp01(cg[i] * inv) * 255f),
                        (byte)(Mathf.Clamp01(cb[i] * inv) * 255f), (byte)(alpha * 255f));
                }
            }
            return outPx;
        }

        static float Factor(int f, float sr, float sg, float sb, float sa, float da)
        {
            switch (f)
            {
                case Zero: return 0f;
                case One: return 1f;
                case SrcColor: return (sr + sg + sb) / 3f;
                case InvSrcColor: return 1f - (sr + sg + sb) / 3f;
                case SrcAlpha: return sa;
                case InvSrcAlpha: return 1f - sa;
                case DestAlpha: return da;
                case InvDestAlpha: return 1f - da;
                default: return 1f;
            }
        }

        static float AlphaFactor(int f, float sa, float da)
        {
            switch (f)
            {
                case Zero: return 0f;
                case One: return 1f;
                case SrcColor: case SrcAlpha: return sa;
                case InvSrcColor: case InvSrcAlpha: return 1f - sa;
                case DestAlpha: return da;
                case InvDestAlpha: return 1f - da;
                default: return 1f;
            }
        }
    }
}
