using System.Collections.Generic;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's regular HUD, drawn with IMGUI from MW2's own art: spread crosshair, the
    /// damage_feedback hitmarker, the weapon block (HUD icon, ammo counter icons in the gun's
    /// ammoCounterClip style, clip | reserve), hit_direction arcs and the blood overlay.
    /// Laid out to keep RoR2's skill and item bars readable.
    static class Mw2Hud
    {
        static float hitmarkerUntil;
        static GUIStyle ammoStyle, stockStyle, nameStyle, smallStyle;

        public static void Hitmarker() => hitmarkerUntil = Time.unscaledTime + 0.22f;
        static float scavengerUntil;
        /// _damagefeedback "scavenger": the scavenger_pickup icon (64x32 at -36, 32 of 640x480) for 2.5 s.
        public static void ScavengerPickup() => scavengerUntil = Time.unscaledTime + 2.5f;

        struct Hit { public Vector3 from; public float at; }
        static readonly List<Hit> hits = new List<Hit>();

        /// The local player took damage from a point in the world.
        public static void Damaged(Vector3 from)
        {
            hits.Add(new Hit { from = from, at = Time.unscaledTime });
            if (hits.Count > 8) hits.RemoveAt(0);
        }

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["ak47_mp"] = "AK-47", ["cheytac_mp"] = "Intervention", ["spas12_mp"] = "SPAS-12", ["ump45_mp"] = "UMP45",
            ["m4_mp"] = "M4A1", ["aa12_mp"] = "AA-12", ["barrett_mp"] = "Barrett .50cal", ["deserteagle_mp"] = "Desert Eagle",
            ["rpd_mp"] = "RPD", ["model1887_mp"] = "Model 1887", ["masada_mp"] = "ACR", ["scar_mp"] = "SCAR-H",
            ["fal_mp"] = "FAL", ["famas_mp"] = "FAMAS", ["tavor_mp"] = "TAR-21", ["fn2000_mp"] = "F2000", ["m16_mp"] = "M16A4",
            ["mp5k_mp"] = "MP5K", ["kriss_mp"] = "Vector", ["p90_mp"] = "P90", ["uzi_mp"] = "Mini-Uzi", ["sa80_mp"] = "L86 LSW",
            ["mg4_mp"] = "MG4", ["aug_mp"] = "AUG HBAR", ["m240_mp"] = "M240", ["wa2000_mp"] = "WA2000", ["m21_mp"] = "M21 EBR",
            ["striker_mp"] = "Striker", ["m1014_mp"] = "M1014", ["ranger_mp"] = "Ranger", ["usp_mp"] = "USP .45",
            ["beretta_mp"] = "M9", ["coltanaconda_mp"] = ".44 Magnum", ["glock_mp"] = "G18", ["pp2000_mp"] = "PP2000",
            ["tmp_mp"] = "TMP", ["beretta393_mp"] = "M93 Raffica",
        };

        public static string Pretty(string internalName) => Names.TryGetValue(internalName, out var n) ? n : internalName;

        static void Styles()
        {
            if (ammoStyle != null) return;
            ammoStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight };
            ammoStyle.normal.textColor = Color.white;
            stockStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerLeft };
            stockStyle.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
            nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight };
            nameStyle.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
        }

        /// MW2's scope overlay: the weapon's overlay material at ads_overlay_width x height on
        /// MW2's 640x480 virtual screen, centred, everything outside it black.
        public static void Scope(string overlay, Mw2WeaponView v)
        {
            float w = Screen.width, h = Screen.height;
            float oh = h * Mathf.Clamp(v.overlayHeight > 0f ? v.overlayHeight / 480f : 1f, 0.1f, 1f);
            float ow = h * (v.overlayWidth > 0f ? v.overlayWidth / 480f : 1f);
            var r = new Rect((w - ow) / 2f, (h - oh) / 2f, ow, oh);
            var black = Color.black;
            Rect(0, 0, w, r.y, black);
            Rect(0, r.yMax, w, h - r.yMax, black);
            Rect(0, r.y, r.x, oh, black);
            Rect(r.xMax, r.y, w - r.xMax, oh, black);
            if (Mw2Icons.Get(overlay) != null) Mw2Icons.Draw(r, overlay, Color.white);
        }

        /// Text size in pixels at 1080p, scaled to the current screen.
        public static float S(float px1080) => Mathf.Round(px1080 * Screen.height / 1080f);

        static void Rect(float x, float y, float w, float h, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// IW4's clip pips per ammoCounterClip (hud_iw4 clip_pip_metrics, 640x480 HUD units):
        /// width, height, step x, per row, step y, art. 0 none / 6 alt weapon draw nothing.
        static bool Pips(int kind, out float pw, out float ph, out float sx, out int wrap, out float sy, out string art)
        {
            pw = ph = sx = sy = 0f; wrap = 1; art = null;
            switch (kind)
            {
                case 1: pw = 4f; ph = 20f; sx = 4f; wrap = 50; sy = -20f; art = "ammo_counter_bullet_mp"; return true;
                case 2: pw = 9f; ph = 20f; sx = 9f; wrap = 16; sy = -20f; art = "ammo_counter_riflebullet_mp"; return true;
                case 3: pw = 11f; ph = 20f; sx = 11f; wrap = 14; sy = -20f; art = "ammo_counter_shotgunshell_mp"; return true;
                case 4: pw = 11f; ph = 20f; sx = 11f; wrap = 14; sy = -20f; art = "ammo_counter_rocket_mp"; return true;
                case 5: pw = 7f; ph = 4f; sx = 8f; wrap = 20; sy = -16f; art = "ammo_counter_beltbullet_mp"; return true;
                default: return false;
            }
        }

        /// One pip's top-left (screen px; y down), right-aligned at base: clip_pip_grid_xy for
        /// magazines, clip_pip_belt_xy (a zigzag belt, rows alternating direction) for belts.
        static Vector2 PipAt(int kind, int i, int clipSize, Vector2 basePx, float u, float pw, float ph, float sx, int wrap, float sy)
        {
            if (kind != 5)
            {
                float x = basePx.x - pw * u - (i % wrap) * sx * u;
                float y = basePx.y - ph * 0.5f * u + (i / wrap) * sy * u;
                return new Vector2(x, y);
            }
            float bx = basePx.x, by = ph * 0.5f * u * (clipSize / wrap) + basePx.y, step = sx * u;
            int last = Mathf.Min(i, clipSize - 1);
            for (int k = 0; k <= last; k++)
            {
                if (k % wrap == 0) { step = -step; by += sy * u; bx += step; }
                if (k == last) break;
                bx += step;
            }
            return new Vector2(bx, by);
        }

        static readonly Dictionary<uint, (Mw2Reticle r, string center, string side)> reticles = new Dictionary<uint, (Mw2Reticle, string, string)>();

        static bool DrawReticle(uint weapon, in Mw2State s, float fov, float cx, float cy, float h)
        {
            if (!reticles.TryGetValue(weapon, out var ret))
            {
                Native.mw2_weapon_reticle(weapon, out var r);
                string c = Native.WeaponString(weapon, 3), sd = Native.WeaponString(weapon, 4);
                reticles[weapon] = ret = (r, string.IsNullOrEmpty(c) ? null : c, string.IsNullOrEmpty(sd) ? null : sd);
            }
            if (ret.center == null && ret.side == null) return false;
            float factor = h / 480f;
            // IW4 transition_to_ads (adsCrosshairInFrac 1); third person only keeps it full size aimed in.
            float trans = Mw2Bridge.ThirdOnly ? 1f : 1f - 0.5f * Mathf.Clamp01(s.adsFrac);
            float tanHalf = Mathf.Max(Mathf.Tan(Mathf.Max(fov, 1f) * 0.5f * Mathf.Deg2Rad), 1e-4f);
            float size = ret.r.side_size * trans;
            // The spread as fired: third person only, MW2's tightened by HipSpreadScale (Mw2Bridge).
            float spreadK = Mw2Bridge.ThirdOnly ? Mathf.Max(Plugin.Instance.HipSpreadScale.Value, 0f) : 1f;
            float gapV = Mathf.Tan(Mathf.Clamp(s.spreadDegrees * spreadK * trans, 0f, 80f) * Mathf.Deg2Rad) * 240f / tanHalf;
            if (gapV < ret.r.min_ofs) gapV = ret.r.min_ofs;
            gapV -= ret.r.side_pos * size;
            var tint = Color.white;
            if (ret.side != null && size > 0f)
            {
                float sz = size * factor, gap = gapV * factor;
                var m = GUI.matrix;
                for (int i = 0; i < 4; i++)
                {
                    float dx = i == 1 ? gap : i == 3 ? -gap : 0f, dy = i == 0 ? -gap : i == 2 ? gap : 0f;
                    GUIUtility.RotateAroundPivot(i * 90f, new Vector2(cx + dx, cy + dy));
                    Mw2Icons.Draw(new Rect(cx + dx - sz / 2, cy + dy - sz / 2, sz, sz), ret.side, tint);
                    GUI.matrix = m;
                }
            }
            if (ret.center != null && ret.r.center_size > 0f)
            {
                float sz = ret.r.center_size * trans * factor;
                Mw2Icons.Draw(new Rect(cx - sz / 2, cy - sz / 2, sz, sz), ret.center, tint);
            }
            return true;
        }

        public static void Draw(in Mw2State s, string weapon, uint weaponIndex, int magSize, bool armed, Camera cam, float healthFrac, uint offhand = 0)
        {
            Styles();
            float w = Screen.width, h = Screen.height;
            float cx = w * 0.5f, cy = h * 0.5f;

            // Hurt: MW2's blood overlay, stronger as health drops.
            if (healthFrac < 0.6f) Mw2Icons.Draw(new Rect(0, 0, w, h), "blood_splatter", new Color(1f, 1f, 1f, Mathf.Clamp01((0.6f - healthFrac) / 0.5f)));

            // Damage direction: the hit_direction arc around the crosshair, pointing at the source.
            hits.RemoveAll(x => Time.unscaledTime - x.at > 1.5f);
            if (cam != null)
                foreach (var hit in hits)
                {
                    var d = hit.from - cam.transform.position;
                    d.y = 0f;
                    var f = cam.transform.forward; f.y = 0f;
                    if (d.sqrMagnitude < 1e-4f || f.sqrMagnitude < 1e-4f) continue;
                    float ang = Vector3.SignedAngle(f, d, Vector3.up); // + = to the right
                    float a = 1f - (Time.unscaledTime - hit.at) / 1.5f;
                    var m = GUI.matrix;
                    GUIUtility.RotateAroundPivot(ang, new Vector2(cx, cy));
                    float aw = h * 0.22f, ah = aw * 0.5f, radius = h * 0.16f;
                    Mw2Icons.Draw(new Rect(cx - aw / 2, cy - radius - ah / 2, aw, ah), "hit_direction", new Color(1f, 1f, 1f, a));
                    GUI.matrix = m;
                }

            // Crosshair: the weapon's own MW2 reticle (reticleSide x4 around the spread gap and / or
            // reticleCenter: grenade launchers, RPG) placed like IW4 (hud_iw4::crosshair), in 480-virtual
            // units; shrinks while aiming in, gone at full ADS.
            float fov = cam != null ? cam.fieldOfView : 60f;
            // An offhand in hand (throwing knife, grenades, C4...) shows its own reticle, as IW4 draws
            // the crosshair of the weapon in the viewmodel (playtest 10-04-26: the knife had none).
            uint reticleWeapon = offhand != 0 ? offhand : weaponIndex;
            // Third person only: no sights, so the crosshair stays up aimed in too (not under a scope).
            bool keep = Mw2Bridge.ThirdOnly && s.adsFrac >= 0.999f && !Mw2Bridge.ScopeUp;
            if (armed && (s.adsFrac < 0.999f || keep) && DrawReticle(reticleWeapon, s, fov, cx, cy, h)) { }
            else if (armed && s.adsFrac < 0.95f)
            {
                float halfFov = Mathf.Max(fov, 1f) * 0.5f * Mathf.Deg2Rad;
                float gap = Mathf.Tan(Mathf.Clamp(s.spreadDegrees, 0.2f, 45f) * Mathf.Deg2Rad) / Mathf.Tan(halfFov) * cy;
                gap = Mathf.Clamp(gap, 4f, h * 0.3f);
                float len = 10f, thick = 2f;
                var c = new Color(1f, 1f, 1f, 0.85f * (1f - s.adsFrac));
                Rect(cx - thick * 0.5f, cy - gap - len, thick, len, c);
                Rect(cx - thick * 0.5f, cy + gap, thick, len, c);
                Rect(cx - gap - len, cy - thick * 0.5f, len, thick, c);
                Rect(cx + gap, cy - thick * 0.5f, len, thick, c);
            }

            if (Time.unscaledTime < scavengerUntil)
            {
                float k = h / 480f, a = Mathf.Clamp01((scavengerUntil - Time.unscaledTime) / 0.5f);
                Mw2Icons.Draw(new Rect(cx - 36f * k, cy + 32f * k, 64f * k, 32f * k), "scavenger_pickup", new Color(1f, 1f, 1f, a));
            }
            // Hitmarker: MW2's damage_feedback (the X sits in the top half of a 1:2 image).
            if (Time.unscaledTime < hitmarkerUntil)
            {
                float hs = Mathf.Round(h * 0.035f);
                Mw2Icons.Draw(new Rect(cx - hs / 2, cy - hs / 2, hs, hs * 2f), "damage_feedback", Color.white);
            }

            if (!armed || Mw2MenuHud.Ready) return; // MW2's weaponbar_hd draws the weapon block
            // Weapon block, bottom right, clear of RoR2's skill bar (still used: utility/special).
            float right = w - 30f, baseY = h * 0.80f;
            float ammoW = 70f;
            Mw2Font.Label(new Rect(right - 90f - ammoW, baseY - 40f, ammoW, 40f), $"{s.clip}", S(38f), Color.white, TextAnchor.LowerRight);
            Mw2Font.Label(new Rect(right - 86f, baseY - 34f, 86f, 30f), $"| {s.stock}", S(24f), new Color(1f, 1f, 1f, 0.8f), TextAnchor.LowerLeft);
            // Rounds as MW2 draws them (IW4 clip pips): full = white, spent = 0.3 grey at 0.4 alpha.
            int mag = Mathf.Max(magSize, s.clip);
            int kind = Native.mw2_weapon_ammo_counter(weaponIndex);
            if (mag > 0 && Pips(kind, out float pw, out float ph, out float sx, out int wrap, out float sy, out string artName))
            {
                var art = Mw2Icons.Get(artName);
                float u = h / 480f; // MW2's HUD is 640x480 virtual units, scaled by height
                var basePx = new Vector2(right - 90f - ammoW - 8f, baseY - 8f - ph * 0.5f * u);
                for (int i = 0; i < mag && i < 300; i++)
                {
                    var at = PipAt(kind, i, mag, basePx, u, pw, ph, sx, wrap, sy);
                    var r = new Rect(at.x, at.y, pw * u, ph * u);
                    var old = GUI.color;
                    GUI.color = i < s.clip ? Color.white : new Color(0.3f, 0.3f, 0.3f, 0.4f);
                    if (art != null) GUI.DrawTexture(r, art, ScaleMode.StretchToFill, true);
                    else GUI.DrawTexture(r, Texture2D.whiteTexture);
                    GUI.color = old;
                }
            }
            string icon = Native.WeaponString(weaponIndex, 0);
            var iconTex = Mw2Icons.Get(icon);
            float iconH = Mathf.Round(h * 0.04f);
            float iconW = 0f;
            if (iconTex != null)
            {
                iconW = iconH * iconTex.width / Mathf.Max(iconTex.height, 1);
                Mw2Icons.Draw(new Rect(right - iconW, baseY - 44f - iconH, iconW, iconH), icon, Color.white);
            }
            // Name beside the icon (clear of the killstreak list above).
            Mw2Font.Label(new Rect(right - iconW - 10f - 300f, baseY - 44f - iconH, 300f, iconH), Pretty(weapon), S(22f), new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleRight, Mw2Font.Small);

            // MW2's low-ammo / reload prompts, under the crosshair.
            bool reloading = s.weaponstate >= 8 && s.weaponstate <= 11;
            if (!reloading && mag > 0 && s.clip == 0) Mw2Font.Label(new Rect(0, cy + h * 0.08f, w, 24), s.stock > 0 ? "Press R to Reload" : "No Ammo", S(24f), new Color(1f, 1f, 1f, 0.9f), TextAnchor.MiddleCenter, Mw2Font.Small);
            else if (!reloading && mag > 0 && s.clip <= Mathf.Max(1, mag / 4)) Mw2Font.Label(new Rect(0, cy + h * 0.08f, w, 24), "Reload", S(24f), new Color(1f, 1f, 1f, 0.9f), TextAnchor.MiddleCenter, Mw2Font.Small);
        }
    }
}
