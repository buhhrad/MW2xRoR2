using System;
using System.Collections.Generic;

namespace MW2RoR2
{
    /// MW2's movement sounds from MW2's own movement events (movement_iw4 PM footsteps / jump /
    /// crash-land / mantle): the local player's aliases are qstep_run_plr_<surface>,
    /// qstep_walk_plr_<surface>, step_sprint_plr_<surface>, step_prone_plr_<surface>,
    /// Land_plr_<surface>, each with its gear_rattle_plr_* foley.
    static unsafe class Mw2Steps
    {
        const int Sprint = 0x6b, Run = 0x6c, Walk = 0x6d, Prone = 0x6e, Jump = 0x6f, LandFirst = 0x70, Mantle = 0xae;
        static readonly int[] buf = new int[64 * 2];
        static readonly Dictionary<string, string> resolved = new Dictionary<string, string>();

        /// Plays the movement sounds; returns the third-person soldier's events among them
        /// (Mw2Character.EvJump / EvLand / EvMantle: playeranim's jump, land and mantle).
        public static uint Drain(IntPtr sim, Mw2Audio audio, bool sprinting)
        {
            uint chEvents = 0;
            if (sim == IntPtr.Zero || audio == null) return 0;
            uint n;
            fixed (int* p = buf) n = Native.mw2_move_events(sim, p, 64);
            for (int i = 0; i < n; i++)
            {
                int ev = buf[i * 2], parm = buf[i * 2 + 1];
                switch (ev)
                {
                    case Sprint: Play(audio, "step_sprint_plr_", parm); audio.PlayAlias("gear_rattle_plr_sprint"); break;
                    // PM only raises run/walk steps; like MW2's cgame, a run step while sprinting
                    // plays the sprint set.
                    case Run when sprinting: Play(audio, "step_sprint_plr_", parm); audio.PlayAlias("gear_rattle_plr_sprint"); break;
                    case Run: Play(audio, "qstep_run_plr_", parm); audio.PlayAlias("gear_rattle_plr_run"); break;
                    case Walk: Play(audio, "qstep_walk_plr_", parm); audio.PlayAlias("gear_rattle_plr_walk"); break;
                    case Prone: Play(audio, "step_prone_plr_", parm); audio.PlayAlias("gear_rattle_plr_prone"); break;
                    case Jump: Play(audio, "qstep_run_plr_", parm); chEvents |= Mw2Character.EvJump; break; // push-off step
                    case Mantle:
                        audio.PlayAlias("gear_rattle_plr_mantle");
                        // Which of MW2's climbs (bits 8-11: transIndex + 1) for the soldier's matching anim.
                        int ti = Native.mw2_mantle_trans_index(sim);
                        chEvents |= Mw2Character.EvMantle | (ti >= 0 && ti < 7 ? (uint)(ti + 1) << 8 : 0u);
                        break;
                    default:
                        if (ev >= LandFirst && ev < LandFirst + 32)
                        {
                            Play(audio, "Land_plr_", ev - LandFirst);
                            audio.PlayAlias("gear_rattle_plr_land");
                            chEvents |= Mw2Character.EvLand;
                        }
                        break;
                }
            }
            return chEvents;
        }

        /// prefix + surface name, falling back to the _default variant when a surface has none.
        static void Play(Mw2Audio audio, string prefix, int surface)
        {
            string key = prefix + surface;
            if (!resolved.TryGetValue(key, out var alias))
            {
                alias = prefix + Mw2Gunfire.SurfaceName(surface);
                if (!Exists(alias)) alias = prefix + "default";
                if (!Exists(alias)) alias = null;
                resolved[key] = alias;
                Plugin.Log.LogInfo($"[steps] {prefix}<{Mw2Gunfire.SurfaceName(surface)}> -> {alias ?? "(none)"}");
            }
            if (alias != null) audio.PlayAlias(alias);
        }

        public static bool Exists(string alias)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(alias);
            fixed (byte* p = b) return Native.mw2_alias_exists(p, (UIntPtr)b.Length) == 1;
        }
    }
}
