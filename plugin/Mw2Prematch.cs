using RoR2;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace MW2RoR2
{
    /// MW2's match start, at the start of a run (_gamelogic.gsc matchStartTimer, _music_and_dialog.gsc
    /// onPlayerSpawned): spawn standing (no drop pod), MW2's class menu open, controls frozen for
    /// scr_game_matchstarttime (5 s) under "MATCH STARTING IN" and a pulsing yellow count, the
    /// mpIntro vision fading to colour from 2, the faction's spawn music + gametype announcer, and the
    /// announcer's boost when it's over.
    static class Mw2Prematch
    {
        /// _tweakables.gsc: scr_game_matchstarttime default.
        const float Duration = 5f;
        /// _hud.gsc fontPulseInit: in 2 frames, out 4 frames (0.05 s each), up to 2x.
        const float PulseIn = 0.1f, PulseOut = 0.2f;

        static Run doneFor;
        static float start = -1f;
        static bool boosted;
        static string voicePrefix = "US_";

        public static bool Frozen => start >= 0f && Time.time - start < Duration;
        public static bool Running => start >= 0f;

        /// MW2 mode took the player's body: the first time in a run, the match starts.
        public static void OnAttached(Mw2Bridge bridge)
        {
            var cfg = Plugin.Instance;
            if (!cfg.Prematch.Value || (Mw2Pilot.Active && !Mw2Pilot.Wants("prematch"))) return;
            if (Run.instance == null || Run.instance.stageClearCount != 0 || doneFor == Run.instance) return;
            Begin();
        }

        public static void Begin()
        {
            doneFor = Run.instance;
            start = Time.time;
            if (Stage.instance != null) Plugin.Log.LogInfo($"MW2 prematch: starts {Stage.instance.entryTime.timeSince:F2} s after the stage began");
            boosted = false;
            string faction = Mw2Skins.LocalFaction();
            Mw2Menus.SetFaction(faction);
            // mp/factionTable.csv column 7: the voice prefix (US_, AB_, ...).
            string prefix = Mw2Skins.Table(faction, 7);
            if (prefix.Length > 0) voicePrefix = prefix;
            Play(voicePrefix + "spawn_music");
            // war.gsc: game["dialog"]["gametype"] = "tm_death" (team deathmatch, the co-op stand-in).
            Play(voicePrefix + "1mc_tm_death");
            Mw2Vision.Intro(1f);
            Mw2Menus.Open("changeclass");
        }

        static void Play(string alias)
        {
            if (Mw2Audio.PlayUi(alias) == 0) Plugin.Log.LogInfo($"MW2 prematch: no sound '{alias}'");
        }

        public static void Update()
        {
            if (start < 0f) return;
            float t = Time.time - start;
            // Count 2 shows 3.1 s in; from there the map's own look comes back over 3 s.
            Mw2Vision.Intro(1f - Mathf.Clamp01((t - 3.1f) / 3f));
            if (t >= Duration && !boosted)
            {
                boosted = true;
                // _music_and_dialog.gsc: after prematch_done, offense_obj / defense_obj = "boost".
                Play(voicePrefix + "1mc_boost");
            }
            if (t >= Duration + 3.2f)
            {
                start = -1f;
                Mw2Vision.Intro(0f);
            }
        }

        /// OnGUI. hidewheninmenu: nothing while the class menu is up.
        public static void Draw()
        {
            if (!Frozen || Mw2Menus.IsOpen || Event.current == null || Event.current.type != EventType.Repaint) return;
            float t = Time.time - start;
            int tick = Mathf.FloorToInt(t);
            float into = t - tick;
            // setValue lands at the top of each pulse; before the first one the number is empty.
            int value = into >= PulseIn ? (int)Duration - tick : (int)Duration - tick + 1;
            float pulse = into < PulseIn ? 1f + into / PulseIn : 1f + Mathf.Clamp01(1f - (into - PulseIn) / PulseOut);
            float sy = Screen.height / 480f;
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            // createServerFontString( "objective", 1.5 ) at CENTER, CENTER, 0, -40.
            float textPx = Native.mw2_hudelem_em_px(3, 1.5f, sy);
            Mw2Font.Label(new Rect(0, centre.y - 40f * sy - textPx, Screen.width, textPx * 2f), Mw2Menus.Localize("MP_MATCH_STARTING_IN"), textPx, Color.white, TextAnchor.MiddleCenter, Mw2Font.Objective);
            if (value <= (int)Duration)
            {
                // createServerFontString( "hudbig", 1 ), colour (1,1,0), at the centre.
                float numPx = Native.mw2_hudelem_em_px(6, 1f * pulse, sy);
                Mw2Font.Label(new Rect(0, centre.y - numPx, Screen.width, numPx * 2f), value.ToString(), numPx, new Color(1f, 1f, 0f), TextAnchor.MiddleCenter, Mw2Font.Big);
            }
        }
    }

}
