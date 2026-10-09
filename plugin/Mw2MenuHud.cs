using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MW2RoR2
{
    [StructLayout(LayoutKind.Sequential)]
    struct Mw2HudState
    {
        public float screenW, screenH;
        public int timeMs;
        public uint weapon, altWeapon;
        public int clip, clipSize, stock, altClip, altClipSize, frags, smokes;
        public float yawDeg, adsFrac;
        public int weaponstate;
        public float xpFrac;
        public int rank;
        public byte showBreathHint, lowAmmoOk, pad0, pad1;
    } // 72 bytes (asserted in mw2sim)

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Mw2HudCmd
    {
        public byte kind, font, align, style;
        public fixed float rect[4];
        public fixed float uv[4];
        public fixed float color[4];
        public float rotationDeg, textScale;
        public ushort material, menu; // menu: which of the frame's menus drew it (1-based)
    } // 64 bytes

    /// MW2's in-match HUD (weaponbar_hd, xpbar_hd, dpad_hd, hold_breath_hint) laid out by MW2's
    /// own menus and expressions in mw2sim; this only draws the commands it returns.
    static unsafe class Mw2MenuHud
    {
        public static bool Ready { get; private set; }
        static bool tried;
        static readonly Mw2HudCmd[] cmds = new Mw2HudCmd[640];
        static readonly byte[] strBuf = new byte[512];

        public static void Init()
        {
            if (tried) return;
            tried = true;
            try
            {
                uint n = Native.mw2_hud_init();
                Ready = n > 0;
                Plugin.Log.LogInfo($"MW2 HUD menus: {n} items");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2 HUD menus unavailable: {e.Message}"); }
        }

        static string Str(ushort index)
        {
            uint n;
            fixed (byte* p = strBuf) n = Native.mw2_hud_cmd_string(index, p, (uint)strBuf.Length);
            return n > 0 ? System.Text.Encoding.UTF8.GetString(strBuf, 0, (int)Math.Min(n, (uint)strBuf.Length)) : "";
        }

        static readonly string[] fontNames = new string[16];
        static string FontName(byte font)
        {
            if (font < fontNames.Length && fontNames[font] != null) return fontNames[font];
            uint n;
            fixed (byte* p = strBuf) n = Native.mw2_hud_font_name(font, p, (uint)strBuf.Length);
            string s = n > 0 ? System.Text.Encoding.UTF8.GetString(strBuf, 0, (int)n) : Mw2Font.Big;
            if (font < fontNames.Length) fontNames[font] = s;
            return s;
        }

        static readonly string[] dpad = new string[5];

        /// A d-pad action slot's icon (MW2's 8x4 animated dpad sheets); "" clears it.
        public static void SetDpad(int slot, string material, int cols, int rows)
        {
            if (!Ready || slot < 1 || slot > 4 || dpad[slot] == material) return;
            dpad[slot] = material;
            var b = System.Text.Encoding.UTF8.GetBytes(material ?? "");
            fixed (byte* p = b) Native.mw2_hud_set_dpad((uint)slot, p, (UIntPtr)b.Length, 0, (uint)rows, (uint)cols, string.IsNullOrEmpty(material) ? 0 : 1);
        }

        public static void Draw(in Mw2HudState state)
        {
            if (!Ready || Event.current == null || Event.current.type != EventType.Repaint) return;
            uint n;
            var st = state;
            fixed (Mw2HudCmd* c = cmds) n = Native.mw2_hud_frame(&st, c, (uint)cmds.Length);
            DrawCmds(cmds, n, true);
        }

        public const int RidePredator = 1, RideAc130 = 2, RideChopper = 3;
        static bool rideBound;

        /// A ride killstreak's own MW2 HUD menu (missilecam_hud / ac130_hud / remote_chopper_overlay:
        /// reticle, overlays, labels, key hints) in place of the match HUD. `weapon` is the ride's
        /// MW2 weapon (the AC-130 menu shows the gun in use).
        public static void DrawRide(int kind, string weapon)
        {
            if (!Ready || Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!rideBound)
            {
                rideBound = true;
                Bind("+attack", "MOUSE1"); // the ride's fire is RoR2's primary (Mouse 1 by default)
                Bind("weapnext", "R");      // Ac130Job: R cycles the guns
                Bind("+activate", "E");     // RideJob.ThermalToggle: RoR2's interact (E by default)
            }
            var st = new Mw2HudState
            {
                screenW = Screen.width, screenH = Screen.height,
                timeMs = (int)(Time.unscaledTime * 1000f),
                weapon = string.IsNullOrEmpty(weapon) ? 0u : Native.WeaponIndex(weapon),
                lowAmmoOk = 0,
            };
            uint n;
            Native.mw2_hud_set_ride((uint)kind);
            try { fixed (Mw2HudCmd* c = cmds) n = Native.mw2_hud_frame(&st, c, (uint)cmds.Length); }
            finally { Native.mw2_hud_set_ride(0); }
            DrawCmds(cmds, n);
        }

        static void Bind(string command, string label)
        {
            var c = System.Text.Encoding.UTF8.GetBytes(command);
            var l = System.Text.Encoding.UTF8.GetBytes(label);
            fixed (byte* pc = c) fixed (byte* pl = l) Native.mw2_hud_set_binding(pc, (UIntPtr)c.Length, pl, (UIntPtr)l.Length);
        }

        static readonly List<Mw2Blend.Layer> group = new List<Mw2Blend.Layer>();
        static readonly int[] groupFirst = new int[16], groupLast = new int[16];

        /// Draw commands from mw2_hud_frame / mw2_menu_frame (strings valid until the next frame call).
        /// `blendGroups`: in each menu, the layers from the first to the last one that blends through the
        /// screen's alpha (the compass letters, the XP bar) are composited the way IW4 draws them (Mw2Blend).
        public static void DrawCmds(Mw2HudCmd[] cmds, uint n, bool blendGroups = false)
        {
            for (int m = 0; m < groupFirst.Length; m++) groupFirst[m] = groupLast[m] = -1;
            if (blendGroups)
            {
                for (int i = 0; i < n; i++)
                {
                    if (cmds[i].kind != 0 || cmds[i].menu >= groupFirst.Length || !Mw2Blend.Special(Mw2Blend.Bits(Str(cmds[i].material)))) continue;
                    int m = cmds[i].menu;
                    if (groupFirst[m] < 0) groupFirst[m] = i;
                    groupLast[m] = i;
                }
            }
            for (int i = 0; i < n; i++)
            {
                int gm = cmds[i].menu < groupFirst.Length ? cmds[i].menu : 0;
                if (blendGroups && i == groupFirst[gm])
                {
                    int last = groupLast[gm];
                    group.Clear();
                    for (int j = i; j <= last; j++)
                    {
                        ref var g = ref cmds[j];
                        if (g.kind != 0) continue;
                        group.Add(new Mw2Blend.Layer
                        {
                            material = Str(g.material),
                            rect = new Rect(g.rect[0], g.rect[1], g.rect[2], g.rect[3]),
                            uv = new Rect(g.uv[0], g.uv[1], g.uv[2] - g.uv[0], g.uv[3] - g.uv[1]),
                            color = new Color(g.color[0], g.color[1], g.color[2], g.color[3]),
                            rotationDeg = g.rotationDeg,
                        });
                    }
                    long perf0 = Mw2Perf.Begin();
                    Mw2Blend.Draw(group, gm);
                    Mw2Perf.End("xpbar", perf0);
                    i = last;
                    continue;
                }
                ref var cmd = ref cmds[i];
                var col = new Color(cmd.color[0], cmd.color[1], cmd.color[2], cmd.color[3]);
                if (col.a <= 0.001f) continue;
                switch (cmd.kind)
                {
                    case 0: Quad(ref cmd, col); break;
                    case 1:
                        Mw2Font.RunAt(FontName(cmd.font), Str(cmd.material), cmd.rect[0], cmd.rect[1], cmd.rect[2], col, cmd.style == 3 || cmd.style == 6);
                        break;
                    case 2:
                        Fill(new Rect(cmd.rect[0], cmd.rect[1], cmd.rect[2], cmd.rect[3]), col);
                        break;
                }
            }
        }

        static void Quad(ref Mw2HudCmd cmd, Color col)
        {
            string name = Str(cmd.material);
            // The compass vignette needs a blend state the menus don't carry; drawn plain it is a
            // grey square behind the ring, so it's left out.
            if (name == "hud_compass_alpha") return;
            // Writes only destination alpha (MW2's 2D masks): never visible by itself.
            if (cmd.align == 255) return;
            var tex = Mw2Icons.Get(name);
            if (tex == null) return;
            var r = new Rect(cmd.rect[0], cmd.rect[1], cmd.rect[2], cmd.rect[3]);
            // The Chopper Gunner's vignette is authored 16:9 (854 of 480); wider screens (an ultrawide's
            // 21:9) would show the picture bare at the sides, so it's stretched across the width.
            if (name == "nightvision_overlay_goggles" && r.width < Screen.width) r = new Rect(0f, r.y, Screen.width, r.height);
            // Film grain (the ride overlays' ac130 / javelin grain): tiled at its own size and moved
            // every frame, live static rather than a fixed smudge.
            if (name.EndsWith("_grain"))
            {
                var o = GUI.color; GUI.color = col;
                GUI.DrawTextureWithTexCoords(r, tex, new Rect(UnityEngine.Random.value, UnityEngine.Random.value, r.width / tex.width, r.height / tex.height), true);
                GUI.color = o;
                return;
            }
            float s0 = cmd.uv[0], t0 = cmd.uv[1], s1 = cmd.uv[2], t1 = cmd.uv[3];
            // HUD images are bottom-row-first; menu t runs from the top.
            var uv = new Rect(s0, 1f - t1, s1 - s0, t1 - t0);
            if (cmd.align == 3 && Mathf.Abs(cmd.rotationDeg) <= 0.01f && Mw2Fx.GuiAdditive() is Material add)
            {
                Graphics.DrawTexture(r, tex, uv, 0, 0, 0, 0, col, add);
                return;
            }
            var old = GUI.color;
            GUI.color = col;
            if (Mathf.Abs(cmd.rotationDeg) > 0.01f)
            {
                // IW4 RotateST spins the texture about its centre inside the rect; for the round
                // compass ring that's the same picture as spinning the quad.
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(-cmd.rotationDeg, r.center);
                GUI.DrawTextureWithTexCoords(r, tex, uv, true);
                GUI.matrix = m;
            }
            else GUI.DrawTextureWithTexCoords(r, tex, uv, true);
            GUI.color = old;
        }

        static void Fill(Rect r, Color c)
        {
            var o = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = o;
        }
    }
}
