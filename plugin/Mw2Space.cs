using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// Bodies don't overlap in MW2 (players collide), so a camera never ends up inside someone.
    /// RoR2 monsters' models stick out well past their capsules and walk right up to you; in
    /// first person their mesh then fills the view. Each physics step, enemies closer than their
    /// body radius + yours (+ margin, at least EnemyPersonalSpace) are pushed back out, level with
    /// you only (flyers above / below are left alone). Bosses are left alone (too big to matter).
    static class Mw2Space
    {
        public static int Pushes;
        static float nextLog;
        public static void KeepEnemiesOut(CharacterBody me, float minMetres)
        {
            if (me == null || !NetworkServer.active) return;
            var at = me.corePosition;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb == me || cb.isBoss || !Mw2Strike.IsEnemy(me, cb)) continue;
                var d = cb.corePosition - at;
                if (Mathf.Abs(d.y) > me.radius + cb.radius + 1.5f) continue;
                d.y = 0f;
                float min = Mathf.Max(me.radius + cb.radius + 0.35f, minMetres);
                float dist = d.magnitude;
                if (dist >= min) continue;
                var dir = dist > 1e-3f ? d / dist : Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
                var push = dir * (min - dist);
                // Straight onto the kinematic controller: rootMotion was ignored by monster AI.
                var kcc = cb.characterMotor != null ? cb.characterMotor.Motor : null;
                if (kcc != null) kcc.SetPosition(kcc.TransientPosition + push);
                else if (cb.rigidbody != null) cb.rigidbody.MovePosition(cb.rigidbody.position + push);
                else cb.transform.position += push;
                Pushes++;
                if (Time.time >= nextLog) { nextLog = Time.time + 1f; Plugin.Log.LogInfo($"[space] pushed {cb.name} out from {dist:F2} m to {min:F2} m"); }
            }
        }
    }
}
