using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// Dying as the MW2 Soldier (playtest 10-03-26: he turned back into Commando): MW2 mode lets go of
    /// the body, but the MW2 soldier stays where he fell and plays MW2's own death animation
    /// (playeranim.script DEATH, chosen from his last movetype), and the camera goes from his eyes
    /// down with him, then pulls back into third person over the body, MW2 style. Ends when the
    /// player has a living body again (Tactical Insertion, Dio's, the next stage).
    static class Mw2DeathCam
    {
        static Mw2Character corpse;
        static CharacterModel hidden;
        static Vector3 feet, eyePos;
        static Quaternion eyeRot;
        static float yaw, started;
        static Mw2CharacterInput input;
        static Transform head;
        static CharacterBody deadBody;

        public static bool Active => corpse != null;

        // Solo, RoR2's stats screen came up ~2 s after death, before the soldier was even down
        // (playtest 10-03-26): the host holds game over for the death camera (Stage.Update checks
        // master.preventGameOver every 2 s; OnBodyDeath clears it on a final death, so this runs
        // after it).
        const float HoldGameOver = 4.5f;
        static CharacterMaster held;
        static float holdUntil;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            var death = HarmonyLib.AccessTools.Method(typeof(CharacterMaster), "OnBodyDeath");
            if (death != null) harmony.Patch(death, postfix: new HarmonyLib.HarmonyMethod(typeof(Mw2DeathCam), nameof(Hold)));
        }

        static void Hold(CharacterMaster __instance, CharacterBody body)
        {
            if (!UnityEngine.Networking.NetworkServer.active || __instance == null || __instance.preventGameOver || !Mw2Survivor.IsMw2(body)) return;
            if (LocalUserManager.GetFirstLocalUser()?.cachedMasterController?.master != __instance) return;
            held = __instance;
            held.preventGameOver = true; // a revive (Dio's, Tactical Insertion) holds it itself: not touched
            holdUntil = Time.time + HoldGameOver;
        }

        static void Release()
        {
            if (held != null) held.preventGameOver = false;
            held = null;
        }

        /// Take over the dead player's MW2 soldier (and Commando's hidden model) from MW2 mode.
        public static void Begin(Mw2Character ch, CharacterModel commando, CharacterBody body, Camera cam, float yawDeg, uint weapon, byte stance, uint hit = 0)
        {
            EndCorpse(); // not the game-over hold: OnBodyDeath may have set it a frame ago
            corpse = ch;
            hidden = commando;
            deadBody = body;
            feet = body != null ? body.footPosition : ch.Root.position;
            yaw = yawDeg;
            eyePos = cam != null ? cam.transform.position : feet + Vector3.up * 1.6f;
            eyeRot = cam != null ? cam.transform.rotation : Quaternion.Euler(0f, yaw, 0f);
            started = Time.time;
            input = new Mw2CharacterInput { dead = 1, weapon = weapon, stance = stance, hit = hit }; // the killing hit picks MW2's death
            head = null;
            foreach (var t in ch.Root.GetComponentsInChildren<Transform>(true))
                if (t.name == "j_head") { head = t; break; }
            Plugin.Log.LogInfo("MW2 death cam: the soldier goes down (MW2 death animation, third person)");
        }

        public static void Update()
        {
            // Released on its own clock: also when no corpse ever showed (MW2 mode off, no body).
            if (held != null && Time.time > holdUntil) Release();
            if (corpse == null) return;
            var me = LocalUserManager.GetFirstLocalUser()?.cachedBody;
            // Up again (a new body), or the run / stage is gone: hand the screen back.
            if ((me != null && me != deadBody && me.healthComponent != null && me.healthComponent.alive) || Run.instance == null || Time.time - started > 30f)
            {
                End();
                return;
            }
            // With a teammate still up, RoR2's spectator camera takes over once he's down.
            if (UnityEngine.Networking.NetworkServer.active == false || PlayerCharacterMasterController.instances.Count > 1)
                if (Time.time - started > HoldGameOver && AnyTeammateAlive()) { End(); return; }
            input.dt = Time.deltaTime;
            corpse.Step(feet, yaw, ref input, true);
        }

        static bool AnyTeammateAlive()
        {
            foreach (var pc in PlayerCharacterMasterController.instances)
            {
                var b = pc != null && pc.master != null ? pc.master.GetBody() : null;
                if (b != null && b != deadBody && b.healthComponent != null && b.healthComponent.alive) return true;
            }
            return false;
        }

        /// After RoR2 placed its camera (CameraRigController.LateUpdate): ours instead.
        public static void OnCamera(Camera cam)
        {
            if (corpse == null || cam == null) return;
            cam.enabled = true; // a killstreak camera may have had it off when he died
            float t = Time.time - started;
            var target = head != null ? head.position : feet + Vector3.up * 0.5f;
            // 0 - 1 s: still his eyes, going down with the head and tipping toward the ground.
            var downPos = head != null ? head.position : eyePos;
            var downRot = Quaternion.Slerp(eyeRot, Quaternion.LookRotation(Vector3.ProjectOnPlane(eyeRot * Vector3.forward, Vector3.up).normalized + Vector3.down * 0.6f), Mathf.Clamp01(t / 1f));
            // 1 - 2.2 s: back and up over the body, looking at it (stays there).
            var flat = Vector3.ProjectOnPlane(eyeRot * Vector3.forward, Vector3.up).normalized;
            if (flat.sqrMagnitude < 0.01f) flat = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var outPos = feet - flat * 3.2f + Vector3.up * 2.4f;
            // Not through a wall: stop short of whatever's between the body and the spot.
            if (Physics.Linecast(target, outPos, out var hit, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) outPos = hit.point + (target - outPos).normalized * 0.3f;
            var outRot = Quaternion.LookRotation(target - outPos);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, 2.2f, t));
            cam.transform.SetPositionAndRotation(Vector3.Lerp(downPos, outPos, k), Quaternion.Slerp(downRot, outRot, k));
        }

        public static void End()
        {
            bool had = corpse != null;
            Release();
            EndCorpse();
            if (had) Plugin.Instance?.Bridge?.RestoreCameraTilt(); // it turned the camera down at the body
        }

        static void EndCorpse()
        {
            if (corpse != null) corpse.Destroy();
            corpse = null;
            if (hidden != null) hidden.invisibilityCount--;
            hidden = null;
            deadBody = null;
        }
    }
}
