using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's heartbeat sensor attachment (the `_heartbeat` guns): the motion tracker screen on the
    /// gun shows enemies (red) and teammates (green) ahead, lit by a sweep that runs out from the
    /// bottom of the fan and fading after (playtest 10-04-26: the attachment did nothing). Drawn from
    /// MW2's own art (motiontracker3d_bg / _sweep / _ping_enemy_mp / _ping_friendly_mp) into a texture
    /// the viewmodel puts on its screen surface (motion_tracker_screen_col).
    /// Range, sweep period and fan angle are IW4 engine settings (motionTracker* dvars) not in the
    /// zones: the numbers here are approximate.
    static class Mw2Heartbeat
    {
        const int Size = 128;
        const float RangeUnits = 2000f, Period = 3f, SweepTime = 1.2f, Fade = 2.2f, HalfAngle = 50f;
        // Where the fan starts and how far it reaches, in texture pixels (rows from the bottom), read
        // off motiontracker3d_bg.
        const float OriginX = 64f, OriginY = 14f, Reach = 92f;

        static Texture2D tex;
        static Color32[] buf, bg, sweep, enemy, friend, rotated;
        static int sweepW, sweepH, pingW;
        static float nextDraw;
        static readonly List<(Vector2 at, float dist, bool ally)> blips = new List<(Vector2, float, bool)>();
        public static int Blips => blips.Count;

        public static Texture2D Texture
        {
            get
            {
                if (tex == null)
                {
                    tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "MW2 heartbeat sensor", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    buf = new Color32[Size * Size];
                    bg = Load("motiontracker3d_bg", out _, out _);
                    // MW2 blends the screen in; ours is the surface itself: onto black, opaque (the half
                    // alpha let the world show through the gun's screen).
                    if (bg != null)
                        for (int i = 0; i < bg.Length; i++)
                        {
                            var c = bg[i]; float a = c.a / 255f;
                            bg[i] = new Color32((byte)(c.r * a), (byte)(c.g * a), (byte)(c.b * a), 255);
                        }
                    rotated = new Color32[Size * Size];
                    sweep = Load("motiontracker3d_sweep", out sweepW, out sweepH);
                    enemy = Load("motiontracker3d_ping_enemy_mp", out pingW, out _);
                    friend = Load("motiontracker3d_ping_friendly_mp", out _, out _);
                    Draw(0f);
                }
                return tex;
            }
        }

        static Color32[] Load(string name, out int w, out int h)
        {
            w = h = 0;
            if (!Mw2Icons.Pixels(name, out var px, out w, out h)) { Plugin.Log.LogWarning($"MW2 heartbeat: {name} missing"); return null; }
            var c = new Color32[w * h];
            for (int i = 0; i < c.Length; i++) c[i] = new Color32(px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]);
            return c; // rows bottom-first, as Unity textures
        }

        /// Per frame while a heartbeat gun is in hand: who's out there (ahead of `view`), redrawn ~20 Hz.
        public static void Update(CharacterBody me, Transform view)
        {
            if (me == null || view == null || Time.time < nextDraw) return;
            nextDraw = Time.time + 0.05f;
            _ = Texture;
            var fwd = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, fwd);
            float range = RangeUnits * Space.Scale;
            blips.Clear();
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb == me || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                bool ally = !Mw2Strike.IsEnemy(me, cb);
                if (ally && !cb.isPlayerControlled) continue; // teammates, not their drones
                var d = cb.corePosition - me.corePosition;
                float f = Vector3.Dot(d, fwd), r = Vector3.Dot(d, right);
                float dist = Mathf.Sqrt(f * f + r * r);
                if (dist > range || f <= 0f || Mathf.Abs(Mathf.Atan2(r, f) * Mathf.Rad2Deg) > HalfAngle) continue;
                blips.Add((new Vector2(OriginX + r / range * Reach, OriginY + f / range * Reach), dist / range, ally));
            }
            Draw(Time.time);
        }

        static void Draw(float now)
        {
            if (bg != null && bg.Length == buf.Length) System.Array.Copy(bg, buf, buf.Length);
            else for (int i = 0; i < buf.Length; i++) buf[i] = new Color32(10, 30, 35, 255);
            float phase = now % Period;
            // The sweep: MW2's arc image growing out from the fan's origin.
            if (sweep != null && phase < SweepTime)
            {
                float rad = phase / SweepTime * Reach;
                Blit(sweep, sweepW, sweepH, OriginX - rad, OriginY, rad * 2f, rad, 1f - phase / SweepTime * 0.5f);
            }
            // Pings: lit when the sweep reaches them, fading after (the last sweep's, until the next).
            float sinceStart = phase;
            foreach (var (at, dist, ally) in blips)
            {
                float lit = dist * SweepTime;
                float age = sinceStart >= lit ? sinceStart - lit : sinceStart + Period - lit;
                float a = Mathf.Clamp01(1f - age / Fade);
                if (a <= 0f) continue;
                var img = ally ? friend : enemy;
                if (img == null) continue;
                float s = 14f;
                Blit(img, pingW, pingW, at.x - s * 0.5f, at.y - s * 0.5f, s, s, a);
            }
            // The gun's screen maps v downwards (u still runs left to right): flipped, the fan's
            // forward is up on the screen (pilot 10-05-26: it pointed down, then sideways).
            for (int y = 0; y < Size; y++)
                System.Array.Copy(buf, y * Size, rotated, (Size - 1 - y) * Size, Size);
            tex.SetPixels32(rotated);
            tex.Apply(false);
        }

        /// `src` (w x h, bottom-first) scaled into the rect, over what's there by its alpha x `alpha`.
        static void Blit(Color32[] src, int w, int h, float x0, float y0, float rw, float rh, float alpha)
        {
            if (rw < 1f || rh < 1f) return;
            int ix0 = Mathf.Max(0, Mathf.FloorToInt(x0)), ix1 = Mathf.Min(Size, Mathf.CeilToInt(x0 + rw));
            int iy0 = Mathf.Max(0, Mathf.FloorToInt(y0)), iy1 = Mathf.Min(Size, Mathf.CeilToInt(y0 + rh));
            for (int y = iy0; y < iy1; y++)
            {
                int sy = Mathf.Clamp((int)((y + 0.5f - y0) / rh * h), 0, h - 1);
                for (int x = ix0; x < ix1; x++)
                {
                    int sx = Mathf.Clamp((int)((x + 0.5f - x0) / rw * w), 0, w - 1);
                    var c = src[sy * w + sx];
                    float k = c.a / 255f * alpha;
                    if (k <= 0.004f) continue;
                    ref var d = ref buf[y * Size + x];
                    d = new Color32((byte)(d.r + (c.r - d.r) * k), (byte)(d.g + (c.g - d.g) * k), (byte)(d.b + (c.b - d.b) * k), 255);
                }
            }
        }
    }
}
