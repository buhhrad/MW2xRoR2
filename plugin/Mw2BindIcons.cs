using RoR2;
using RoR2.Skills;
using RoR2.UI;
using UnityEngine;

namespace MW2RoR2
{
    /// Fire / ADS on the MW2 Soldier's skill bar: the input they're on, drawn as a mouse with the
    /// left / right button lit, or a gamepad's right / left trigger when the player is on a
    /// controller (playtest 10-03-26: they were blank white squares). RoR2's bind label stays under it.
    class Mw2BindSkillDef : SkillDef
    {
        public bool right; // ADS: right mouse / left trigger

        public override Sprite GetCurrentIcon(GenericSkill skillSlot)
            => Mw2BindIcons.Get(right, Mw2BindIcons.OnGamepad());
    }

    static class Mw2BindIcons
    {
        static readonly Sprite[] cache = new Sprite[4];

        public static bool OnGamepad()
        {
            var es = LocalUserManager.GetFirstLocalUser()?.eventSystem;
            return es != null && es.currentInputSource == MPEventSystem.InputSource.Gamepad;
        }

        static readonly Sprite[] arrows = new Sprite[2];

        /// A chevron (selector previous / next).
        public static Sprite Arrow(bool right)
        {
            int k = right ? 1 : 0;
            if (arrows[k] != null) return arrows[k];
            const int N = 128;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float fx = right ? x + 0.5f : N - (x + 0.5f), fy = Mathf.Abs(y + 0.5f - 64f);
                    // Two strokes meeting at the tip (x 84, y 64), from (44, 64 +- 40).
                    float d = Mathf.Abs((fx - 44f) - (40f - fy));
                    bool on = fx >= 40f && fx <= 88f && fy <= 42f && d < 11f;
                    px[y * N + x] = on ? new Color32(255, 255, 255, 255) : default;
                }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = right ? "mw2_next" : "mw2_prev", filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return arrows[k] = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
        }

        static Sprite plus;

        /// A plus in a frame: "create / edit" buttons on the MW2 loadout rows.
        public static Sprite Plus()
        {
            if (plus != null) return plus;
            const int N = 128;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - 64f), dy = Mathf.Abs(y + 0.5f - 64f);
                    bool bar = (dx < 7f && dy < 38f) || (dy < 7f && dx < 38f);
                    bool frame = Mathf.Max(dx, dy) > 54f && Mathf.Max(dx, dy) < 60f;
                    px[y * N + x] = bar ? new Color32(255, 255, 255, 255) : frame ? new Color32(255, 255, 255, 140) : default;
                }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "mw2_plus", filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return plus = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
        }

        public static Sprite Get(bool ads, bool pad)
        {
            int i = (ads ? 1 : 0) + (pad ? 2 : 0);
            if (cache[i] != null) return cache[i];
            const int N = 128;
            var px = new Color32[N * N];
            var dim = new Color32(255, 255, 255, 70);
            var lit = new Color32(255, 255, 255, 255);
            var line = new Color32(255, 255, 255, 200);
            // Inside a rounded rectangle (x0..x1, y0..y1, radius r), y up.
            bool Box(int x, int y, float x0, float y0, float x1, float y1, float r)
            {
                float cx = Mathf.Clamp(x + 0.5f, x0 + r, x1 - r), cy = Mathf.Clamp(y + 0.5f, y0 + r, y1 - r);
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                return x + 0.5f >= x0 && x + 0.5f <= x1 && y + 0.5f >= y0 && y + 0.5f <= y1 && dx * dx + dy * dy <= r * r;
            }
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Color32 c = default;
                    if (!pad)
                    {
                        // Mouse: body 34..94 x 14..114, buttons above y 72, split at x 64.
                        bool body = Box(x, y, 34, 14, 94, 114, 28f);
                        bool inner = Box(x, y, 39, 19, 89, 109, 23f);
                        if (body && !inner) c = line;
                        else if (inner && y >= 72)
                        {
                            bool split = Mathf.Abs(x + 0.5f - 64f) < 2.5f, seam = Mathf.Abs(y + 0.5f - 72f) < 2.5f;
                            bool mine = ads ? x >= 64 : x < 64;
                            c = split || seam ? line : mine ? lit : dim;
                        }
                    }
                    else
                    {
                        // Gamepad: body outline 14..114 x 18..76, triggers on top at the shoulders.
                        bool body = Box(x, y, 14, 18, 114, 76, 26f), inner = Box(x, y, 19, 23, 109, 71, 21f);
                        bool lt = Box(x, y, 20, 82, 50, 112, 10f), rt = Box(x, y, 78, 82, 108, 112, 10f);
                        if (body && !inner) c = line;
                        else if (lt) c = ads ? lit : dim;
                        else if (rt) c = ads ? dim : lit;
                    }
                    px[y * N + x] = c;
                }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = $"mw2_bind_{i}", filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return cache[i] = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
        }
    }
}
