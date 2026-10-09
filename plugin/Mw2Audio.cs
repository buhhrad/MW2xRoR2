using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2 weapon sounds. RoR2 runs its audio through Wwise and ships with Unity audio
    /// disabled (AudioClip.Create comes back empty), so the native side plays the PCM it
    /// read from the user's own MW2 install straight to the Windows output device.
    class Mw2Audio
    {
        bool warned;

        public void Attach(GameObject go) { }

        // RoR2's master/SFX volume convars and pause flag, found by reflection so a
        // game update degrades to "full volume, never paused" instead of crashing.
        static object masterVar, sfxVar, msxVar;
        static string msxRtpc;
        static MethodInfo getString;
        static Func<bool> paused;
        static bool probed;

        static void Probe()
        {
            probed = true;
            try
            {
                var asm = typeof(RoR2.CharacterBody).Assembly;
                foreach (var t in asm.GetTypes())
                {
                    var m = t.GetField("cvVolumeMaster", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m == null) continue;
                    masterVar = m.GetValue(null);
                    sfxVar = t.GetField("cvVolumeSfx", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    msxVar = t.GetField("cvVolumeMsx", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                    msxRtpc = msxVar?.GetType().GetField("rtpcName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(msxVar) as string;
                    getString = masterVar?.GetType().GetMethod("GetString", Type.EmptyTypes);
                    break;
                }
                var pm = asm.GetType("RoR2.PauseManager");
                var prop = pm?.GetProperty("isPaused", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var field = pm?.GetField("isPaused", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) paused = () => (bool)prop.GetValue(null);
                else if (field != null) paused = () => (bool)field.GetValue(null);
                Plugin.Log.LogInfo($"MW2 audio follows game volume: {(masterVar != null ? "yes" : "no")}, pause: {(paused != null ? "yes" : "no")}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MW2 audio: couldn't read game volume settings ({e.GetType().Name}); using WeaponVolume only.");
            }
        }

        static float Percent(object convar)
        {
            if (convar == null || getString == null) return 1f;
            try
            {
                var str = getString.Invoke(convar, null) as string;
                return float.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                    ? Mathf.Clamp01(v > 1f ? v / 100f : v) : 1f;
            }
            catch { return 1f; }
        }

        /// A 2D MW2 sound (menus, splashes, announcer) at the MW2 volume times RoR2's master and SFX
        /// sliders, like the weapon sounds. Returns Native.PlayAlias's result (0 = unknown alias).
        public static int PlayUi(string alias, float scale = 1f)
        {
            if (string.IsNullOrEmpty(alias)) return 0;
            float volume = Plugin.Instance.Volume.Value * GameVolume() * scale;
            return volume > 0.001f ? Native.PlayAlias(alias, volume) : 1;
        }

        /// A looping MW2 sound (a vehicle's playLoopSound); 0 if the alias is unknown.
        public static unsafe uint LoopStart(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return 0;
            var b = System.Text.Encoding.UTF8.GetBytes(alias);
            fixed (byte* p = b) return Native.mw2_loop_start(p, (UIntPtr)b.Length, 0f);
        }

        /// Loop volume: `scale` (distance) times the MW2 volume and RoR2's master / SFX sliders;
        /// silent while the game is paused.
        public static void LoopVolume(uint id, float scale)
        {
            if (id == 0) return;
            float v = GamePaused() ? 0f : Plugin.Instance.Volume.Value * GameVolume() * scale;
            Native.mw2_loop_volume(id, v);
        }

        /// Loop volume for music: the MW2 volume and RoR2's master / music sliders.
        public static void MusicVolume(uint id)
        {
            if (id == 0) return;
            if (!probed) Probe();
            Native.mw2_loop_volume(id, Plugin.Instance.Volume.Value * Percent(masterVar) * Percent(msxVar));
        }

        /// RoR2's own music on or off (its Wwise music bus RTPC; the music slider itself is left alone).
        public static void Ror2Music(bool on)
        {
            if (!probed) Probe();
            if (string.IsNullOrEmpty(msxRtpc)) return;
            try { AkSoundEngine.SetRTPCValue(msxRtpc, on ? Percent(msxVar) * 100f : 0f); }
            catch (Exception e) { Plugin.Log.LogWarning($"RoR2 music mute: {e.Message}"); }
        }

        public static void LoopStop(uint id)
        {
            if (id != 0) Native.mw2_loop_stop(id);
        }

        static float GameVolume()
        {
            if (!probed) Probe();
            return Percent(masterVar) * Percent(sfxVar);
        }

        static bool GamePaused()
        {
            if (!probed) Probe();
            try { return paused != null && paused(); } catch { return false; }
        }

        public void Play(uint weapon, uint kind)
        {
            if (weapon == 0 || GamePaused()) return;
            if (Native.mw2_weapon_sound(weapon, kind) == 0) return;
            float volume = Plugin.Instance.Volume.Value * GameVolume();
            if (volume <= 0.001f) return;
            if (Native.mw2_play_weapon_sound(weapon, kind, volume) == 0 && !warned)
            {
                warned = true;
                Plugin.Log.LogWarning("MW2 audio: no output device could be opened; weapon sounds are off.");
            }
        }

        /// Any MW2 alias by name (hit alert, announcer, killstreak effects).
        /// A weapon's fire sound at `scale` of the weapon volume (killstreak guns, faded by distance).
        public void PlayWeapon(uint weapon, float scale)
        {
            if (weapon == 0 || GamePaused()) return;
            float volume = Plugin.Instance.Volume.Value * GameVolume() * scale;
            if (volume > 0.001f) Native.mw2_play_weapon_sound(weapon, WeaponSound.Fire, volume);
        }

        public void PlayAlias(string alias, float scale = 1f)
        {
            if (GamePaused()) return;
            float volume = Plugin.Instance.Volume.Value * GameVolume() * scale;
            if (volume > 0.001f) Native.PlayAlias(alias, volume);
        }

        /// Turn one step's weapon events into sounds. Segmented reloads (shotguns) get
        /// start / per-shell / end; others get one reload clip at the start.
        public void OnEvents(uint weapon, uint events, int clipAfter, bool notetracks = false)
        {
            if (events == 0) return;
            if (notetracks) events &= ~(WeaponEvents.ReloadStart | WeaponEvents.ReloadInsert | WeaponEvents.ReloadEnd | WeaponEvents.Rechamber);
            if ((events & WeaponEvents.Shot) != 0)
                Play(weapon, clipAfter == 0 && Native.mw2_weapon_sound(weapon, WeaponSound.FireLast) != 0 ? WeaponSound.FireLast : WeaponSound.Fire);
            if ((events & WeaponEvents.Dry) != 0) Play(weapon, WeaponSound.Dry);
            bool segmented = Native.mw2_weapon_sound(weapon, WeaponSound.ReloadStart) != 0;
            if ((events & WeaponEvents.ReloadStart) != 0)
                Play(weapon, segmented ? WeaponSound.ReloadStart : (clipAfter == 0 && Native.mw2_weapon_sound(weapon, WeaponSound.ReloadEmpty) != 0 ? WeaponSound.ReloadEmpty : WeaponSound.Reload));
            if ((events & WeaponEvents.ReloadInsert) != 0 && segmented) Play(weapon, WeaponSound.Reload);
            if ((events & WeaponEvents.ReloadEnd) != 0 && segmented) Play(weapon, WeaponSound.ReloadEnd);
            if ((events & WeaponEvents.Rechamber) != 0) Play(weapon, WeaponSound.Rechamber);
            if ((events & WeaponEvents.MeleeStart) != 0) Play(weapon, WeaponSound.Melee);
        }
    }
}
