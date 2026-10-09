using System.Collections.Generic;
using RoR2.UI;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's weaponbar_hd owns the bottom-right corner (349.3 x 56 in MW2's 640x480 space), which
    /// is where RoR2 keeps its skill and equipment icons. In MW2 mode those RoR2 icons slide left
    /// to sit just clear of MW2's weapon bar; turning MW2 mode off puts them back.
    static class Mw2RoR2Hud
    {
        const float WeaponbarWidth = 349.3f + 40f; // MW2 units, plus a gap for the compass ring
        static readonly Dictionary<RectTransform, Vector3> moved = new Dictionary<RectTransform, Vector3>();
        static readonly Dictionary<RectTransform, Vector3> scaled = new Dictionary<RectTransform, Vector3>();
        static readonly Dictionary<RectTransform, Vector3> applied = new Dictionary<RectTransform, Vector3>();
        static readonly List<RectTransform> drifted = new List<RectTransform>();
        static readonly Dictionary<RectTransform, Vector2> wantLeft = new Dictionary<RectTransform, Vector2>();

        /// Moves `rt` to `to`, remembering where it was (Restore) and where we put it (drift check).
        static void Place(RectTransform rt, Vector3 to)
        {
            if (!moved.ContainsKey(rt)) moved[rt] = rt.position;
            rt.position = to;
            applied[rt] = to;
        }

        /// RoR2 moved `rt` itself: its new spot is the original now; our scale comes off.
        static void Forget(RectTransform rt)
        {
            if (rt != null && scaled.TryGetValue(rt, out var s)) rt.localScale = s;
            moved.Remove(rt); scaled.Remove(rt); applied.Remove(rt); wantLeft.Remove(rt);
        }
        static float lastShift = float.NaN;
        static int lastW, lastH;
        static float settleUntil;

        static readonly List<GameObject> hidden = new List<GameObject>();

        public static void Apply(bool mw2)
        {
            if (!mw2) { Restore(); return; }
            // A resolution change: put everything back, then wait for RoR2's canvases to lay out at the
            // new size before measuring again (measuring the same frame placed the money panel over
            // the minimap).
            if (Screen.width != lastW || Screen.height != lastH) { Restore(); lastW = Screen.width; lastH = Screen.height; settleUntil = Time.unscaledTime + 0.3f; }
            if (Time.unscaledTime < settleUntil) return;
            // RoR2 re-lays out its HUD for a while after a resolution change (and on some screens):
            // anything it moved off our spot is measured and placed again.
            drifted.Clear();
            foreach (var kv in applied) if (kv.Key == null || (kv.Key.position - kv.Value).sqrMagnitude > 1e-6f) drifted.Add(kv.Key);
            foreach (var rt in drifted) { if (Mw2Pilot.Active && rt != null) Plugin.Log.LogInfo($"[hud] {rt.name} moved by RoR2, placing again (frame {Time.frameCount})"); Forget(rt); }
            // The skill row: where it really drew (RoR2's HUD scaling and layout land after us) is
            // measured each frame and nudged onto its mark.
            foreach (var kv in wantLeft)
            {
                var rt = kv.Key;
                if (rt == null || !applied.ContainsKey(rt)) continue;
                var canvas = rt.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                if (!DrawnExtent(rt, cam, out float l, out _, out float bt)) continue;
                float ex = kv.Value.x - l, ey = kv.Value.y - bt;
                if (Mathf.Abs(ex) < 1f && Mathf.Abs(ey) < 1f) continue;
                rt.position += ScreenToWorld(rt, cam, rt.right, 0, ex) + ScreenToWorld(rt, cam, rt.up, 1, ey);
                applied[rt] = rt.position;
                if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[hud] {rt.name} drew at {l:F0},{bt:F0}, nudged {ex:F0},{ey:F0} (frame {Time.frameCount})");
            }
            foreach (var hud in HUD.readOnlyInstanceList)
            {
                if (hud == null) continue;
                // M1 / M2 are MW2's fire / aim in MW2 mode: another survivor's primary / secondary icons
                // don't apply (the MW2 Soldier's are MW2's Fire / ADS, kept).
                // (No body yet - a respawn - says nothing: hiding then left the MW2 Soldier's hidden.)
                var shown = hud.targetBodyObject != null ? hud.targetBodyObject.GetComponent<RoR2.CharacterBody>() : null;
                if (hud.skillIcons != null && shown != null && !Mw2Survivor.IsMw2(shown))
                    foreach (var s in hud.skillIcons)
                        if (s != null && s.gameObject.activeSelf && (s.targetSkillSlot == RoR2.SkillSlot.Primary || s.targetSkillSlot == RoR2.SkillSlot.Secondary))
                        {
                            s.gameObject.SetActive(false);
                            hidden.Add(s.gameObject);
                        }
                foreach (var mt in new[] { hud.moneyText, hud.lunarCoinText })
                {
                    var rt = mt != null ? mt.transform.parent as RectTransform : null;
                    if (rt == null || moved.ContainsKey(rt)) continue;
                    var canvas = rt.GetComponentInParent<Canvas>();
                    if (canvas == null) continue;
                    var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    var c = new Vector3[4];
                    rt.GetWorldCorners(c);
                    var tl = RectTransformUtility.WorldToScreenPoint(cam, c[1]); // screen y runs up
                    if (mt == hud.lunarCoinText && hud.moneyText != null && hud.moneyText.transform.parent == rt) continue;
                    // Under the minimap's frame, left edges lined up (playtest 10-06-26: it sat over the
                    // minimap at 1080p and off to the side of it).
                    var frame = Mw2Killstreaks.RadarFrame();
                    float gap = 10f * Screen.height / 1080f;
                    float dy = Mathf.Min(0f, Screen.height - (frame.yMax + gap) - tl.y);
                    float dx = frame.x - tl.x;
                    // The lunar coins' own panel stacks under the money's.
                    if (mt == hud.lunarCoinText && hud.moneyText != null && moved.ContainsKey(hud.moneyText.transform.parent as RectTransform))
                    {
                        var m = new Vector3[4];
                        ((RectTransform)hud.moneyText.transform.parent).GetWorldCorners(m);
                        dy = Mathf.Min(0f, RectTransformUtility.WorldToScreenPoint(cam, m[0]).y - 4f * Screen.height / 1080f - tl.y);
                    }
                    if (Mathf.Abs(dy) < 1f && Mathf.Abs(dx) < 1f) continue;
                    RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, Vector2.zero, cam, out var a);
                    RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, new Vector2(dx, dy), cam, out var b);
                    Place(rt, rt.position + (b - a));
                }
                // RoR2's ally cards (teammates, drones, turrets) start where MW2's minimap is; they
                // stack under the money / lunar coins instead (playtest: minimap clashed with RoR2's UI).
                var left = hud.transform.Find("MainContainer/MainUIArea/SpringCanvas/LeftCluster") as RectTransform;
                var money = hud.moneyText != null ? hud.moneyText.transform.parent as RectTransform : null;
                if (left != null && money != null && !moved.ContainsKey(left) && moved.ContainsKey(money))
                {
                    var canvas = left.GetComponentInParent<Canvas>();
                    var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                    var lc = new Vector3[4];
                    left.GetWorldCorners(lc);
                    float leftTop = RectTransformUtility.WorldToScreenPoint(cam, lc[1]).y;
                    float below = float.MaxValue;
                    foreach (var mt in new[] { hud.moneyText, hud.lunarCoinText })
                    {
                        var r = mt != null ? mt.transform.parent as RectTransform : null;
                        if (r == null || !r.gameObject.activeInHierarchy) continue;
                        var mc = new Vector3[4];
                        r.GetWorldCorners(mc);
                        below = Mathf.Min(below, RectTransformUtility.WorldToScreenPoint(cam, mc[0]).y);
                    }
                    if (below < float.MaxValue)
                    {
                        float dy = Mathf.Min(0f, below - 8f * Screen.height / 1080f - leftTop);
                        if (Mathf.Abs(dy) >= 1f)
                        {
                            RectTransformUtility.ScreenPointToWorldPointInRectangle(left, Vector2.zero, cam, out var a0);
                            RectTransformUtility.ScreenPointToWorldPointInRectangle(left, new Vector2(0f, dy), cam, out var b0);
                            Place(left, left.position + (b0 - a0));
                        }
                    }
                }
                var parents = new HashSet<RectTransform>();
                if (hud.skillIcons != null) foreach (var s in hud.skillIcons) if (s != null && s.transform.parent is RectTransform p) parents.Add(p);
                if (hud.equipmentIcons != null) foreach (var e in hud.equipmentIcons) if (e != null && e.transform.parent is RectTransform p) parents.Add(p);
                foreach (var rt in parents)
                {
                    if (moved.ContainsKey(rt)) continue;
                    var canvas = rt.GetComponentInParent<Canvas>();
                    if (canvas == null) continue;
                    var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    // Where the group's right edge is now, and where MW2's weapon bar starts.
                    // The row's drawn extent: its children spill past its own rect (Tab / Shift / Q).
                    if (!DrawnExtent(rt, cam, out float leftPx, out float rightPx, out float bottomPx)) continue;
                    float targetPx = Screen.width - WeaponbarWidth * Screen.height / 480f;
                    float shiftPx = Mathf.Min(0f, targetPx - rightPx);
                    // 16:9 and narrower: MW2's weapon bar is wide enough that sliding clear of it runs the
                    // row into RoR2's health bar (a friend's 1920x1080, 10-06-26). Stop at the health bar;
                    // if the row still doesn't fit between the two, shrink it and line its left edge up.
                    float minLeft = HealthBarRight(hud, cam) + 14f * Screen.height / 1080f;
                    float scale = 1f;
                    if (leftPx + shiftPx < minLeft)
                    {
                        shiftPx = Mathf.Min(0f, minLeft - leftPx);
                        float room = targetPx - minLeft, width = rightPx - leftPx;
                        if (width > 1f && room < width) scale = Mathf.Max(0.6f, room / width);
                    }
                    if (Mathf.Abs(shiftPx) < 1f && scale == 1f) continue;
                    var origin = rt.position;
                    if (scale != 1f)
                    {
                        scaled[rt] = rt.localScale;
                        rt.localScale *= scale;
                        DrawnExtent(rt, cam, out float scaledLeft, out _);
                        shiftPx = minLeft - scaledLeft;
                    }
                    if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[hud] {rt.name} left {leftPx:F0} right {rightPx:F0} healthbar {minLeft:F0} weaponbar {targetPx:F0} shift {shiftPx:F0} scale {scale:F2}");
                    DrawnExtent(rt, cam, out float nowLeft, out _);
                    var placed = rt.position + ScreenToWorld(rt, cam, rt.right, 0, shiftPx);
                    rt.position = origin;
                    Place(rt, placed);
                    // Checked against where it really draws each frame (RoR2's HUD layout lands after us).
                    wantLeft[rt] = new Vector2(nowLeft + shiftPx, bottomPx); // bottom: where it sat before shrinking
                    lastShift = shiftPx;
                }
                LiftNotification(hud, parents);
            }
        }

        static readonly System.Reflection.FieldInfo currentNotification = HarmonyLib.AccessTools.Field(typeof(NotificationUIController), "currentNotification");

        /// RoR2's pickup popup (item name + what it does) sits at the bottom centre, where the skill
        /// icons land once they slide clear of MW2's weapon bar on a wide screen: it went under them
        /// (playtest 10-04-26). Each popup is lifted to just above the skill row.
        static void LiftNotification(HUD hud, HashSet<RectTransform> skillRows)
        {
            var ctrl = hud.GetComponent<NotificationUIController>() ?? hud.GetComponentInChildren<NotificationUIController>();
            var note = ctrl != null ? currentNotification?.GetValue(ctrl) as GenericNotification : null;
            var rt = note != null ? note.transform as RectTransform : null;
            if (rt == null || moved.ContainsKey(rt) || skillRows.Count == 0) return;
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var c = new Vector3[4];
            float rowTop = float.MinValue, rowLeft = float.MaxValue, rowRight = float.MinValue;
            foreach (var row in skillRows)
            {
                if (row == null) continue;
                row.GetWorldCorners(c);
                var lo = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
                var hi = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
                rowTop = Mathf.Max(rowTop, hi.y); rowLeft = Mathf.Min(rowLeft, lo.x); rowRight = Mathf.Max(rowRight, hi.x);
            }
            rt.GetWorldCorners(c);
            var nLo = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
            var nHi = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            moved[rt] = rt.position; // also marks it handled (a destroyed popup's entry is skipped on restore)
            if (nHi.x < rowLeft || nLo.x > rowRight) return; // not over the skill row
            float dy = rowTop + 34f * Screen.height / 1080f - nLo.y; // screen y runs up; the item icon hangs below the rect
            if (dy <= 0f) return;
            RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, Vector2.zero, cam, out var a);
            RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, new Vector2(0f, dy), cam, out var b);
            rt.position += b - a;
        }

        /// World offset along `axis` that moves `rt` `px` screen pixels (screen x: 0, y: 1). RoR2's bottom HUD clusters sit on a
        /// tilted plane, so projecting a screen delta onto it (ScreenPointToWorldPointInRectangle)
        /// over- or under-shot by about the aspect ratio; this measures along the rect's own x axis.
        static Vector3 ScreenToWorld(RectTransform rt, Camera cam, Vector3 axis, int screenAxis, float px)
        {
            // A step about the rect's own world size (canvases can be scaled very small or large).
            var p = rt.position;
            var step = axis * Mathf.Max(1e-4f, Mathf.Abs(rt.rect.width * rt.lossyScale.x));
            float k = RectTransformUtility.WorldToScreenPoint(cam, p + step)[screenAxis] - RectTransformUtility.WorldToScreenPoint(cam, p)[screenAxis];
            return Mathf.Abs(k) < 1e-4f ? Vector3.zero : step * (px / k);
        }

        /// Screen-px left / right of what `rt` draws (its active children's rects).
        static bool DrawnExtent(RectTransform rt, Camera cam, out float left, out float right) => DrawnExtent(rt, cam, out left, out right, out _);

        /// ... and its bottom (screen px, y up).
        static bool DrawnExtent(RectTransform rt, Camera cam, out float left, out float right, out float bottom)
        {
            left = float.MaxValue; right = float.MinValue; bottom = float.MaxValue;
            var c = new Vector3[4];
            foreach (var g in rt.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                g.rectTransform.GetWorldCorners(c);
                var lo = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
                left = Mathf.Min(left, lo.x);
                bottom = Mathf.Min(bottom, lo.y);
                right = Mathf.Max(right, RectTransformUtility.WorldToScreenPoint(cam, c[2]).x);
            }
            return left <= right;
        }

        /// Right edge (screen px) of RoR2's health bar, 0 if there is none.
        static float HealthBarRight(HUD hud, Camera cam)
        {
            var hb = hud.healthBar != null ? hud.healthBar.transform as RectTransform : null;
            if (hb == null || !hb.gameObject.activeInHierarchy) return 0f;
            var c = new Vector3[4];
            hb.GetWorldCorners(c);
            return RectTransformUtility.WorldToScreenPoint(cam, c[2]).x;
        }

        static void Restore()
        {
            foreach (var kv in scaled) if (kv.Key != null) kv.Key.localScale = kv.Value;
            scaled.Clear();
            foreach (var kv in moved) if (kv.Key != null) kv.Key.position = kv.Value;
            moved.Clear();
            applied.Clear();
            wantLeft.Clear();
            foreach (var g in hidden) if (g != null) g.SetActive(true);
            hidden.Clear();
        }
    }
}
