using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2RoR2
{
    /// Text in MW2's own fonts (fonts/hudBigFont, hudSmallFont, objectiveFont from the user's
    /// localized_code_post_gfx_mp zone): glyph metrics + the glyph sheet, drawn with IMGUI.
    unsafe class Mw2Font
    {
        public const string Big = "fonts/hudBigFont", Small = "fonts/hudSmallFont", Objective = "fonts/objectiveFont";

        static readonly Dictionary<string, Mw2Font> fonts = new Dictionary<string, Mw2Font>();

        int pixelHeight;
        Texture2D sheet;
        readonly Dictionary<char, Mw2Glyph> glyphs = new Dictionary<char, Mw2Glyph>();

        static Mw2Font Get(string name)
        {
            if (fonts.TryGetValue(name, out var f)) return f;
            f = null;
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            var mat = stackalloc byte[128];
            int ph; uint count, matLen;
            int ok;
            fixed (byte* p = b) ok = Native.mw2_font_info(p, (UIntPtr)b.Length, out ph, out count, mat, 128, out matLen);
            if (ok == 1 && count > 0)
            {
                var arr = new Mw2Glyph[count];
                uint n;
                fixed (byte* p = b) fixed (Mw2Glyph* g = arr) n = Native.mw2_font_glyphs(p, (UIntPtr)b.Length, g, count);
                var mb = new byte[Math.Min(matLen, 128u)];
                for (int i = 0; i < mb.Length; i++) mb[i] = mat[i];
                var sheet = Mw2Icons.Get(System.Text.Encoding.UTF8.GetString(mb));
                if (sheet != null)
                {
                    f = new Mw2Font { pixelHeight = Math.Max(ph, 1), sheet = sheet };
                    for (int i = 0; i < n; i++) f.glyphs[(char)arr[i].letter] = arr[i];
                }
            }
            if (f == null) Plugin.Log.LogWarning($"MW2 font '{name}' not available; HUD text falls back to Unity's font.");
            fonts[name] = f;
            return f;
        }

        float Width(string text, float scale)
        {
            float w = 0f;
            foreach (char c in text)
                if (glyphs.TryGetValue(c, out var g) || glyphs.TryGetValue(char.ToUpperInvariant(c), out g)) w += g.dx * scale;
            return w;
        }

        void Run(string text, float x, float baseline, float scale, Color colour)
        {
            var old = GUI.color;
            GUI.color = colour;
            foreach (char c in text)
            {
                if (!glyphs.TryGetValue(c, out var g) && !glyphs.TryGetValue(char.ToUpperInvariant(c), out g)) continue;
                if (g.width > 0 && g.height > 0)
                {
                    var r = new Rect(x + g.x0 * scale, baseline + g.y0 * scale, g.width * scale, g.height * scale);
                    // Sheet rows are bottom-first for the GUI; glyph t runs from the top.
                    GUI.DrawTextureWithTexCoords(r, sheet, new Rect(g.s0, 1f - g.t1, g.s1 - g.s0, g.t1 - g.t0), true);
                }
                x += g.dx * scale;
            }
            GUI.color = old;
        }

        static readonly Color[] codes =
        {
            Color.black, new Color(1f, 0.36f, 0.36f), new Color(0.55f, 1f, 0.36f), new Color(1f, 1f, 0.36f),
            new Color(0.36f, 0.5f, 1f), new Color(0.36f, 1f, 1f), new Color(1f, 0.36f, 1f), Color.white, Color.white, Color.white,
        };

        /// MW2 HUD text exactly where the menus put it: `x`/`baseline` the pen origin in pixels,
        /// `scale` pixels per font pixel. ^N switches colour like MW2; `shadow` = text style 3/6.
        public static void RunAt(string font, string text, float x, float baseline, float scale, Color colour, bool shadow)
        {
            if (string.IsNullOrEmpty(text)) return;
            var f = Get(font) ?? Get(Big);
            if (f == null) return;
            var col = colour;
            int i = 0;
            float pen = x;
            while (i < text.Length)
            {
                int j = text.IndexOf('^', i);
                if (j < 0) j = text.Length;
                if (j > i)
                {
                    string seg = text.Substring(i, j - i);
                    float sh = Mathf.Max(1f, scale * 2f);
                    if (shadow) f.Run(seg, pen + sh, baseline + sh, scale, new Color(0f, 0f, 0f, col.a * 0.7f));
                    f.Run(seg, pen, baseline, scale, col);
                    pen += f.Width(seg, scale);
                }
                if (j + 1 < text.Length && char.IsDigit(text[j + 1]))
                {
                    var c = codes[text[j + 1] - '0'];
                    col = new Color(c.r, c.g, c.b, colour.a);
                    i = j + 2;
                }
                else i = j + 1;
            }
        }

        public static float Measure(string text, float size, string font = Big)
        {
            var f = Get(font);
            return f != null ? f.Width(text, size / f.pixelHeight) : text.Length * size * 0.5f;
        }

        static GUIStyle fallback;

        /// MW2 text with ^N colour codes (localized hints: "Press ^3[MOUSE1]^7 to ..."), `size` pixels
        /// tall, centred horizontally in `r` and vertically on its middle.
        public static void LabelCentredCoded(Rect r, string text, float size, Color colour, string font = Small)
        {
            if (string.IsNullOrEmpty(text)) return;
            var f = Get(font) ?? Get(Big);
            if (f == null) { Label(r, System.Text.RegularExpressions.Regex.Replace(text, @"\^\d", ""), size, colour, TextAnchor.MiddleCenter, font); return; }
            float scale = size / f.pixelHeight;
            float w = f.Width(System.Text.RegularExpressions.Regex.Replace(text, @"\^\d", ""), scale);
            RunAt(font, text, r.x + (r.width - w) / 2f, r.y + (r.height - size) / 2f + size, scale, colour, true);
        }

        /// Draw `text` `size` pixels tall inside `r`, placed by `anchor`, with MW2's dark drop shadow.
        public static void Label(Rect r, string text, float size, Color colour, TextAnchor anchor, string font = Big)
        {
            if (string.IsNullOrEmpty(text)) return;
            var f = Get(font);
            if (f == null)
            {
                fallback = fallback ?? new GUIStyle(GUI.skin.label);
                fallback.fontSize = Mathf.RoundToInt(size * 0.8f);
                fallback.alignment = anchor;
                var o = GUI.color; GUI.color = colour; GUI.Label(r, text, fallback); GUI.color = o;
                return;
            }
            float scale = size / f.pixelHeight;
            float w = f.Width(text, scale);
            float x;
            switch (anchor)
            {
                case TextAnchor.UpperCenter: case TextAnchor.MiddleCenter: case TextAnchor.LowerCenter: x = r.x + (r.width - w) / 2f; break;
                case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: x = r.xMax - w; break;
                default: x = r.x; break;
            }
            float top;
            switch (anchor)
            {
                case TextAnchor.UpperLeft: case TextAnchor.UpperCenter: case TextAnchor.UpperRight: top = r.y; break;
                case TextAnchor.LowerLeft: case TextAnchor.LowerCenter: case TextAnchor.LowerRight: top = r.yMax - size; break;
                default: top = r.y + (r.height - size) / 2f; break;
            }
            float baseline = top + size;
            float sh = Mathf.Max(1f, size * 0.06f);
            f.Run(text, x + sh, baseline + sh, scale, new Color(0f, 0f, 0f, colour.a * 0.6f));
            f.Run(text, x, baseline, scale, colour);
        }
    }
}
