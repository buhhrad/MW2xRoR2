using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    // Release builds only. The playtest pilot (Mw2Pilot.cs), the showcase recorder (Mw2Cinema.cs,
    // Mw2Recorder.cs, Mw2Bot.cs) and the playtest test tools (Mw2Admin.cs) are compiled into dev builds
    // alone (-c Dev, MW2_DEV; tools\deploy.ps1). These stand in for them in the released mod and are
    // always off: the game plays as a dev build does with no playtest running.

    static class Mw2Pilot
    {
        public static bool Active => false;
        public static bool Ran => false;
        public static bool Attack => false;
        public static bool Ads => false;
        public static bool Sprint => false;
        public static bool Jump => false;
        public static bool Interact => false;
        public static bool Reload => false;
        public static bool Moving => false;
        public static bool Frag => false;
        public static bool Smoke => false;
        public static bool Melee => false;
        public static bool FrontCam => false;
        public static float Fwd => 0f;
        public static float Right => 0f;
        public static bool AttackPulse, StreakPulse, CrouchPulse, PronePulse, InteractPulse;
        public static int GunPulse = -1;
        public static Vector3 ViewForward => Vector3.forward;
        public static bool ThirdOnlyTest => false;
        public static bool ThirdOnlyConfigTest => false;
        public static void Steer(Transform cam) { }
        public static void AfterCamera(Transform cam) { }
        public static bool Consume(ref bool pulse) => false;
        public static bool Wants(string step) => false;
    }

    static class Mw2Cinema
    {
        public static bool On => false;
        public static bool HideHud => false;
        public static bool ShowStreakHud => false;
        public static bool Recording => false;
        public static bool InputBlocked => false;
        public static int Frame => 0;
        public static void SwapDissolve(GameObject oldRoot, GameObject newRoot) { }
    }

    sealed class Mw2Admin
    {
        public bool Open => false;
        public bool InfiniteAmmo => false;
        public void Apply(CharacterBody body) { }
    }
}
