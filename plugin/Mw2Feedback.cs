using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// How getting hurt feels in MW2, on top of RoR2's health (items and regen stay RoR2's):
    /// - blood: splatter_alt full screen at IW4's intensity, max(1 - health fraction) easing
    ///   down at 0.3/s (hud_iw4 blood_overlay_lerp, HUD_BLOOD_OVERLAY_LERP_RATE_DEFAULT);
    /// - breathing (_healthoverlay.gsc playerBreathingSound): below 35% health, breathing_hurt
    ///   every 0.784 + 0.1..0.9 s; breathing_better once when health starts coming back after
    ///   being "very hurt" (<= 55%, level.healthOverlayCutoff);
    /// - damage direction: hit_direction arrows around the crosshair pointing at the attacker.
    ///   Sizes/time are CoD's cg_hudDamageIcon* defaults (width 128, height 64, offset 128,
    ///   2000 ms) - approximate: not in IW4L.
    static class Mw2Feedback
    {
        static float blood, nextBreath, lastHealth = 1f, lastHurtTime = -999f;
        static bool veryHurt, hooked;
        static CharacterBody body;
        static Mw2Audio audio;

        class Hit { public Vector3 from; public float at; }
        static readonly List<Hit> hits = new List<Hit>();
        static bool arrowWarned, arrowLogged;
        const float IconTime = 2f, IconW = 128f, IconH = 64f, IconOffset = 128f;

        public static void Update(CharacterBody b, Mw2Audio a, float dt)
        {
            body = b; audio = a;
            if (!hooked)
            {
                hooked = true;
                // Clients get DamageDealtMessages; the host (single player) only sees the server report.
                GlobalEventManager.onClientDamageNotified += OnDamage;
                GlobalEventManager.onServerDamageDealt += OnServerDamage;
            }
            if (b == null || b.healthComponent == null || !b.healthComponent.alive)
            {
                blood = 0f; veryHurt = false; hits.Clear();
                return;
            }
            float frac = Mathf.Clamp01(b.healthComponent.combinedHealthFraction);
            // blood_overlay_lerp
            float target = 1f - frac;
            if (target < blood) blood -= dt * 0.3f;
            if (blood < target) blood = target;
            if (frac < lastHealth - 1e-4f) lastHurtTime = Time.time;
            if (frac <= 0.55f) veryHurt = true;
            // Recovering after being very hurt: breathing_better once (MW2 plays it when regen starts).
            if (veryHurt && frac > lastHealth + 1e-4f && Time.time - lastHurtTime > 0.5f)
            {
                veryHurt = false;
                audio?.PlayAlias("breathing_better");
            }
            if (frac >= 0.999f) veryHurt = false;
            lastHealth = frac;
            if (frac < 0.35f && Time.time >= nextBreath)
            {
                audio?.PlayAlias("breathing_hurt");
                nextBreath = Time.time + 0.784f + 0.1f + Random.value * 0.8f;
            }
        }

        static void OnServerDamage(DamageReport r)
        {
            if (body == null || r == null || r.victimBody != body || r.damageDealt <= 0f) return;
            var from = r.attackerBody != null ? r.attackerBody.corePosition : (r.damageInfo != null ? r.damageInfo.position : body.corePosition);
            AddHit(from);
        }

        static float lastHitAdded;
        static void AddHit(Vector3 from)
        {
            // Client + server can both report one hit; one arrow per hit.
            if (Time.time - lastHitAdded < 0.02f) return;
            lastHitAdded = Time.time;
            if (hits.Count == 0) Plugin.Log.LogInfo($"[feedback] hit from {from} (player at {(body != null ? body.corePosition : Vector3.zero)})");
            hits.Add(new Hit { from = from, at = Time.time });
            if (hits.Count > 8) hits.RemoveAt(0);
        }

        static void OnDamage(DamageDealtMessage msg)
        {
            if (body == null || msg == null || msg.victim != body.gameObject) return;
            AddHit(msg.attacker != null ? msg.attacker.transform.position : msg.position);
        }

        // splatter_alt = blood_defocus_color revealed through blood_defocus_mask: the more hurt,
        // the further the blood creeps in from the edges (the centre stays clearest). Composited
        // on the CPU at the images' own size, only when the intensity moves.
        static Texture2D colorTex, maskTex, bloodOut;
        static Color32[] colorPx, maskPx, outPx;
        static float bloodBuilt = -1f;
        static bool bloodTried;

        static Texture2D BloodTexture(float intensity)
        {
            if (!bloodTried)
            {
                bloodTried = true;
                colorTex = Mw2Icons.Get("splatter_alt");
                maskTex = Mw2Icons.Get("blood_defocus_mask");
                if (colorTex == null) return null;
                // Half resolution is plenty for a soft full-screen overlay (1280x720 source).
                int w = Mathf.Max(colorTex.width / 2, 1), h = Mathf.Max(colorTex.height / 2, 1);
                colorPx = Readable(colorTex, w, h).GetPixels32();
                maskPx = maskTex != null ? Readable(maskTex, w, h).GetPixels32() : Vignette(w, h);
                outPx = new Color32[colorPx.Length];
                bloodOut = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                Plugin.Log.LogInfo($"MW2 blood: colour {w}x{h}, mask {(maskTex != null ? "blood_defocus_mask" : "vignette stand-in")}");
            }
            if (colorPx == null) return null;
            if (Mathf.Abs(intensity - bloodBuilt) < 0.02f) return bloodOut;
            bloodBuilt = intensity;
            float cut = 1f - intensity;
            for (int i = 0; i < outPx.Length; i++)
            {
                float m = maskPx[i].r / 255f;
                float a = Mathf.Clamp01((m - cut) * 4f) * (colorPx[i].a / 255f);
                var c = colorPx[i];
                outPx[i] = new Color32(c.r, c.g, c.b, (byte)(a * 255f));
            }
            bloodOut.SetPixels32(outPx);
            bloodOut.Apply(false);
            return bloodOut;
        }

        /// GPU textures from Mw2Icons may not be CPU-readable: copy through a RenderTexture.
        static Texture2D Readable(Texture2D src, int w = 0, int h = 0)
        {
            w = w > 0 ? w : src.width; h = h > 0 ? h : src.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply(false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            return t;
        }

        /// Stand-in mask when blood_defocus_mask isn't a HUD image: bright at the edges, dark centre.
        static Color32[] Vignette(int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = (x + 0.5f) / w * 2f - 1f, dy = (y + 0.5f) / h * 2f - 1f;
                    byte v = (byte)(Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 1.25f) * 255f);
                    px[y * w + x] = new Color32(v, v, v, 255);
                }
            return px;
        }

        public static void Draw(Camera cam)
        {
            float w = Screen.width, h = Screen.height;
            if (blood > 0.001f)
            {
                var tex = BloodTexture(blood);
                if (tex != null) GUI.DrawTexture(new Rect(0, 0, w, h), tex, ScaleMode.StretchToFill, true);
            }
            if (cam == null || body == null || hits.Count == 0) return;
            var arrow = Mw2Icons.Get("hit_direction");
            if (arrow == null) { if (!arrowWarned) { arrowWarned = true; Plugin.Log.LogWarning("[feedback] hit_direction texture missing"); } return; }
            if (!arrowLogged) { arrowLogged = true; Plugin.Log.LogInfo($"[feedback] drawing hit_direction {arrow.width}x{arrow.height}"); }
            float u = h / 480f;
            var centre = new Vector2(w / 2f, h / 2f);
            var fwd = cam.transform.forward; fwd.y = 0f;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var hit = hits[i];
                float age = Time.time - hit.at;
                if (age > IconTime) { hits.RemoveAt(i); continue; }
                var to = hit.from - body.corePosition; to.y = 0f;
                if (to.sqrMagnitude < 0.01f || fwd.sqrMagnitude < 1e-4f) continue;
                float ang = Vector3.SignedAngle(fwd, to, Vector3.up); // + = attacker to the right
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, centre);
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - age / IconTime));
                GUI.DrawTexture(new Rect(centre.x - IconW * u / 2f, centre.y - IconOffset * u - IconH * u / 2f, IconW * u, IconH * u), arrow, ScaleMode.StretchToFill, true);
                GUI.color = old;
                GUI.matrix = m;
            }
        }
    }
}
