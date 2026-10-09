using System;
using System.IO;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's own desktop icon (the gold "2" in its ring), read from the player's iw4mp.exe
    /// (playtest 10-03-26: the MW2 Soldier's portrait). The PE resource tree's largest RT_ICON, decoded
    /// from its DIB (8 / 24 / 32 bit with the 1-bit mask) or PNG. Nothing is shipped: like every
    /// other MW2 asset here, it's read from the install.
    static class Mw2ExeIcon
    {
        /// `feather`: fade the square's corners out round the icon's ring (inner 88% of the radius
        /// solid, gone at the edge).
        public static Texture2D Load(string exePath, bool feather = false)
        {
            try
            {
                if (!File.Exists(exePath)) return null;
                byte[] d = File.ReadAllBytes(exePath);
                byte[] best = Largest(d);
                return best != null ? Decode(best, feather) : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MW2 exe icon: {e.Message}");
                return null;
            }
        }

        static int U16(byte[] d, int o) => d[o] | d[o + 1] << 8;
        static int I32(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;

        static byte[] Largest(byte[] d)
        {
            int pe = I32(d, 0x3c);
            int nsec = U16(d, pe + 6), optsz = U16(d, pe + 20), opt = pe + 24;
            int ddir = opt + (U16(d, opt) == 0x10b ? 96 : 112);
            int rsrc = I32(d, ddir + 16);
            int Off(int rva)
            {
                for (int i = 0; i < nsec; i++)
                {
                    int s = opt + optsz + i * 40;
                    int vsize = I32(d, s + 8), va = I32(d, s + 12), rawsz = I32(d, s + 16), raw = I32(d, s + 20);
                    if (rva >= va && rva < va + Math.Max(vsize, rawsz)) return rva - va + raw;
                }
                return -1;
            }
            int b = Off(rsrc);
            if (b < 0) return null;
            byte[] pick = null;
            int pickArea = 0;
            // type (RT_ICON = 3) -> name -> language -> data
            int n0 = U16(d, b + 12) + U16(d, b + 14);
            for (int i = 0; i < n0; i++)
            {
                int tid = I32(d, b + 16 + i * 8), p = I32(d, b + 20 + i * 8) & 0x7fffffff;
                if (tid != 3) continue;
                int d1 = b + p, n1 = U16(d, d1 + 12) + U16(d, d1 + 14);
                for (int j = 0; j < n1; j++)
                {
                    int d2 = b + (I32(d, d1 + 20 + j * 8) & 0x7fffffff);
                    if (U16(d, d2 + 12) + U16(d, d2 + 14) == 0) continue;
                    int leaf = b + I32(d, d2 + 20);
                    int drva = I32(d, leaf), dsz = I32(d, leaf + 4), o = Off(drva);
                    if (o < 0 || o + dsz > d.Length) continue;
                    int area;
                    if (dsz > 8 && d[o] == 0x89 && d[o + 1] == (byte)'P') area = 1 << 20; // PNG: the big one
                    else area = Math.Abs(I32(d, o + 4) * (I32(d, o + 8) / 2));
                    if (area <= pickArea) continue;
                    pickArea = area;
                    pick = new byte[dsz];
                    Buffer.BlockCopy(d, o, pick, 0, dsz);
                }
            }
            return pick;
        }

        static Texture2D Decode(byte[] b, bool feather)
        {
            if (b[0] == 0x89 && b[1] == (byte)'P')
            {
                var png = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return ImageConversion.LoadImage(png, b) ? png : null;
            }
            int hs = I32(b, 0), w = I32(b, 4), h = I32(b, 8) / 2, bpp = U16(b, 14);
            int ncol = bpp <= 8 ? (I32(b, 32) != 0 ? I32(b, 32) : 1 << bpp) : 0;
            int pal = hs, px0 = hs + ncol * 4;
            int stride = (w * bpp + 31) / 32 * 4, mstride = (w + 31) / 32 * 4, m0 = px0 + stride * h;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) // DIB rows run bottom-up, as Unity's do
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4, r, g, bl, a = 255;
                    if (bpp == 8) { int c = b[px0 + y * stride + x]; bl = b[pal + c * 4]; g = b[pal + c * 4 + 1]; r = b[pal + c * 4 + 2]; }
                    else if (bpp == 24) { int s = px0 + y * stride + x * 3; bl = b[s]; g = b[s + 1]; r = b[s + 2]; }
                    else if (bpp == 32) { int s = px0 + y * stride + x * 4; bl = b[s]; g = b[s + 1]; r = b[s + 2]; a = b[s + 3]; }
                    else return null;
                    if (bpp != 32 && m0 + y * mstride + x / 8 < b.Length && ((b[m0 + y * mstride + x / 8] >> (7 - x % 8)) & 1) != 0) a = 0;
                    if (feather)
                    {
                        float dx = (x + 0.5f) / w - 0.5f, dy = (y + 0.5f) / h - 0.5f;
                        float rr = Mathf.Sqrt(dx * dx + dy * dy) / 0.5f;
                        a = (int)(a * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.88f, 1f, rr))));
                    }
                    rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)bl; rgba[o + 3] = (byte)a;
                }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.LoadRawTextureData(rgba);
            tex.Apply(false, true);
            return tex;
        }
    }
}
