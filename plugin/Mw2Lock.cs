using System;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's Stinger lock-on (maps/mp/_stinger.gsc StingerUsageLoop, which runs for the Stinger and the
    /// AT4) with RoR2's monsters as the targets (playtest 10-05-26: "lock on to any risk of rain 2
    /// target"; MW2's own list is its aircraft). Aimed in (ADS >= 0.95): the nearest enemy inside the
    /// reticle circle (radius 75 at fov 65) with a clear sightline starts a lock; 1000 ms on it
    /// (still inside radius 85, sight lost for under 500 ms) locks it. "stinger_locking" beeps every
    /// 0.6 s while locking, "stinger_locked" every 0.25 s once locked. The sim gets the lock each
    /// frame: the Stinger fires only when locked (requireLockonToFire) and locked rockets home in.
    sealed class Mw2Lock
    {
        const float LockMs = 1000f, LostSightMs = 500f;
        const float AcquireRadius = 75f, HoldRadius = 85f, ReticleFov = 65f;
        const float LockingBeep = 0.6f, LockedBeep = 0.25f;

        int stage;
        CharacterBody target;
        float lockStart, lostSince, nextBeep;
        int weaponIdx = -1;
        string weaponName = "";

        public int Stage => stage;
        /// Pilot diagnostics: what the last update saw.
        public string Why = "";
        public CharacterBody Target => target;

        public void Update(CharacterBody self, uint weapon, float adsFrac, Camera cam, IntPtr sim, Mw2Audio audio)
        {
            if (sim == IntPtr.Zero) return;
            if (weaponIdx != (int)weapon) { weaponIdx = (int)weapon; weaponName = Native.WeaponString(weapon, 2) ?? ""; }
            bool usable = (weaponName == "stinger_mp" || weaponName == "at4_mp" || weaponName == "javelin_mp") && adsFrac >= 0.95f && cam != null
                          && self != null && self.healthComponent != null && self.healthComponent.alive;
            if (Mw2Pilot.Active) Why = $"weapon '{weaponName}' ads {adsFrac:F2} cam {(cam != null ? cam.name : "none")}";
            if (!usable) { Reset(sim); return; }
            Vector3 eye = cam.transform.position;
            float now = Time.time * 1000f;

            if (stage == 0)
            {
                // Searching: every enemy inside the reticle, the nearest one if it can be seen.
                CharacterBody nearest = null;
                float best = float.MaxValue;
                var team = self.teamComponent != null ? self.teamComponent.teamIndex : TeamIndex.Player;
                foreach (var b in CharacterBody.readOnlyInstancesList)
                {
                    if (b == null || b == self || b.healthComponent == null || !b.healthComponent.alive) continue;
                    if (b.teamComponent == null || !TeamManager.IsTeamEnemy(team, b.teamComponent.teamIndex)) continue;
                    if (!InReticle(cam, b.corePosition, AcquireRadius)) continue;
                    float d = (b.corePosition - self.corePosition).sqrMagnitude;
                    if (d < best) { best = d; nearest = b; }
                }
                if (Mw2Pilot.Active) Why += nearest == null ? ", none in the circle" : $", nearest {nearest.name} sight {SightTest(eye, nearest)}";
                if (nearest == null || !SightTest(eye, nearest)) { Send(sim); return; }
                target = nearest;
                stage = 1;
                lockStart = now;
                lostSince = 0f;
                nextBeep = 0f;
            }
            if (stage == 1)
            {
                if (!StillValid(cam)) { Reset(sim); return; }
                if (!SoftSight(eye, now)) { Send(sim); return; }
                Beep(audio, "stinger_locking", LockingBeep);
                if (now - lockStart >= LockMs)
                {
                    stage = 2;
                    nextBeep = 0f;
                }
            }
            if (stage == 2)
            {
                if (!SoftSight(eye, now)) { Send(sim); return; }
                if (!StillValid(cam)) { Reset(sim); return; }
                Beep(audio, "stinger_locked", LockedBeep);
            }
            Send(sim);
        }

        public void Reset(IntPtr sim)
        {
            if (stage != 0 && sim != IntPtr.Zero) { Native.mw2_set_lock(sim, 0, 0f, 0f, 0f); Native.mw2_hud_set_lock(0, 320f, 240f); }
            stage = 0;
            target = null;
        }

        void Send(IntPtr sim)
        {
            if (stage == 0 || target == null) { Native.mw2_set_lock(sim, 0, 0f, 0f, 0f); Native.mw2_hud_set_lock(0, 320f, 240f); return; }
            var p = Space.ToIw(target.corePosition);
            Native.mw2_set_lock(sim, stage, p.x, p.y, p.z);
            // The Javelin's CLU draws its lock box on the target (640x480 virtual).
            var cam = Camera.main;
            var v = cam != null ? cam.WorldToViewportPoint(target.corePosition) : new Vector3(0.5f, 0.5f, 1f);
            Native.mw2_hud_set_lock(stage, v.x * 640f, (1f - v.y) * 480f);
        }

        void Beep(Mw2Audio audio, string alias, float every)
        {
            if (Time.time < nextBeep) return;
            nextBeep = Time.time + every;
            audio?.PlayAlias(alias);
        }

        bool StillValid(Camera cam) =>
            target != null && target.healthComponent != null && target.healthComponent.alive && InReticle(cam, target.corePosition, HoldRadius);

        /// _stinger.gsc SoftSightTest: sight lost for under 500 ms keeps the lock.
        bool SoftSight(Vector3 eye, float now)
        {
            if (SightTest(eye, target)) { lostSince = 0f; return true; }
            if (lostSince == 0f) lostSince = now;
            if (now - lostSince >= LostSightMs) { stage = 0; target = null; return false; }
            return true;
        }

        /// _stinger.gsc LockSightTest: the target's origin, else the front or back of its bounds.
        static bool SightTest(Vector3 eye, CharacterBody b)
        {
            if (b == null) return false;
            float r = Mathf.Max(b.radius, 0.5f);
            var fwd = b.transform.forward;
            foreach (var p in new[] { b.corePosition, b.corePosition + fwd * r, b.corePosition - fwd * r })
                if (!Physics.Linecast(eye, p, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) return true;
            return false;
        }

        /// IW4 WorldPointInReticle_Circle(point, fov, radius): within `radius` of the screen centre
        /// on a 640-wide virtual screen whose horizontal field of view is `fov` (so a fixed cone,
        /// whatever the zoom).
        static bool InReticle(Camera cam, Vector3 point, float radius)
        {
            var t = cam.transform;
            var v = point - t.position;
            float z = Vector3.Dot(v, t.forward);
            if (z <= 0.01f) return false;
            float x = Vector3.Dot(v, t.right) / z, y = Vector3.Dot(v, t.up) / z;
            float px = Mathf.Sqrt(x * x + y * y) / Mathf.Tan(ReticleFov * 0.5f * Mathf.Deg2Rad) * 320f;
            return px <= radius;
        }
    }
}
