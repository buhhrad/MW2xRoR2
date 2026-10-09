using System;
using System.Runtime.InteropServices;

namespace MW2RoR2
{
    // Mirrors crates/mw2sim/src/lib.rs (ABI 2). Sizes are asserted on both sides.

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3f
    {
        public float x, y, z;
        public Vec3f(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2Trace
    {
        public float fraction;
        public Vec3f normal;
        public Vec3f endpos;
        public uint surfaceFlags;
        public byte startsolid;
        public byte allsolid;
        public byte walkable;
        public byte pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2Input
    {
        public int msec;
        public uint buttons;
        public sbyte forwardmove;
        public sbyte rightmove;
        public byte pad0, pad1;
        public float yaw;
        public float pitch;
        public float speedScale;
        public float fireRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2State
    {
        public Vec3f origin;
        public Vec3f velocity;
        public Vec3f viewangles;
        public float viewHeight;
        public uint pmFlags;
        public byte grounded;
        public byte sprinting;
        public byte pad0, pad1;
        public uint weapon;
        public int clip;
        public int stock;
        public int weaponstate;
        public float adsFrac;
        public uint shotsPending;
        public uint events;
        public Vec3f kickAngles;
        public float spreadDegrees;
        public int clipLeft; // akimbo's left clip; -1 = not akimbo
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2ClipInfo
    {
        public uint rate;
        public uint channels;
        public uint frames;
        public uint pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2ModelInfo
    {
        public uint vertexCount;
        public uint indexCount;
        public uint surfaceCount;
        public uint pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2SurfaceInfo
    {
        public uint indexStart;
        public uint indexCount;
        public uint textureFormat;
        public uint textureWidth;
        public uint textureHeight;
        public uint textureBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2ViewmodelInfo
    {
        public uint boneCount;
        public uint vertexCount;
        public uint indexCount;
        public uint surfaceCount;
    }

    public static class WeaponEvents
    {
        public const uint Shot = 1, Dry = 2, ReloadStart = 4, ReloadInsert = 8, ReloadEnd = 16, Rechamber = 32;
        public const uint MeleeStart = 512, MeleeCharge = 1024, MeleeHit = 2048;
    }

    public static class WeaponSound
    {
        public const uint Fire = 0, FireLast = 1, Dry = 2, Reload = 3, ReloadEmpty = 4, ReloadStart = 5, ReloadEnd = 6, Rechamber = 7, Raise = 8, Pullback = 9, Melee = 10;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2Reticle
    {
        public float center_size, side_size, min_ofs, side_pos, ads_in_frac, ads_out_frac;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2Shot
    {
        public Vec3f origin;
        public Vec3f dir;
        public float damage;
        public float minDamage;
        public float maxDamageRange;
        public float minDamageRange;
        public float maxRange;
        public uint weapon;
        public uint pellet;
        public uint hand; // 1 = akimbo's left gun
    }

    public static class Buttons
    {
        public const uint Attack = 0x1;
        public const uint Sprint = 0x2;
        public const uint Melee = 0x4;     // IW4 BUTTON_MELEE
        public const uint Reload = 0x10;
        public const uint Crouch = 0x200;
        public const uint Prone = 0x100;   // IW4 BUTTON_PRONE
        public const uint Jump = 0x400;
        public const uint Ads = 0x800;
        public const uint Breath = 0x2000;
        public const uint Frag = 0x4000;   // lethal offhand (hold = cook)
        public const uint Smoke = 0x8000;  // tactical offhand
        public const uint Throw = 0x80000; // IW4 BUTTON_THROW: akimbo's right gun
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2ViewSway
    {
        public float yaw, pitch, breathScale;
        public int holding, breathMs, canHold;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void TraceFn(IntPtr user, Vec3f* start, Vec3f* end, Vec3f* mins, Vec3f* maxs, uint mask, Mw2Trace* result);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Mw2StreakState
    {
        public uint count;
        public uint stackLen;
        public fixed uint stack[8];
        public uint loadoutLen;
        public fixed uint loadout[8];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2Rank
    {
        public uint id;
        public uint minXp;
        public uint xpToNext;
        public uint display;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2WeaponView
    {
        public float adsZoomFov;
        public float adsZoomInFrac;
        public float adsZoomOutFrac;
        public float overlayWidth;
        public float overlayHeight;
        public int overlayReticle;
        public int hasOverlay;
        public int pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mw2Glyph
    {
        public uint letter;
        public int x0, y0, dx, width, height;
        public float s0, t0, s1, t1;
    }

    public static unsafe class Native
    {
        public const uint ExpectedAbi = 31;
        public const int TraceSize = 36, InputSize = 28, StateSize = 96, ShotSize = 56, ClipInfoSize = 16, StreakStateSize = 76, RankSize = 16, WeaponViewSize = 32, GlyphSize = 40, ViewSwaySize = 24;
        const string Lib = "mw2sim";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string path);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_abi_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_create(ref Vec3f origin);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_destroy(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_set_origin(IntPtr sim, ref Vec3f origin);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_set_velocity(IntPtr sim, ref Vec3f velocity);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_step(IntPtr sim, ref Mw2Input input, TraceFn trace, IntPtr user, out Mw2State state);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_load_weapons(byte* path, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_index(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_give_weapon(IntPtr sim, uint index);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_take_shots(IntPtr sim, Mw2Shot* shots, uint cap);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_add_reserve(IntPtr sim, int rounds);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_sound(uint weapon, uint kind);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_clip_info(uint clip, out Mw2ClipInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_clip_samples(uint clip, float* samples, uint cap);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_play_sound(uint clip, float volume);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_play_weapon_sound(uint weapon, uint kind, float volume);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_play_alias(byte* name, UIntPtr len, float volume);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_model(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_model_info(uint model, out Mw2ModelInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_model_mesh(uint model, float* positions, float* normals, float* uvs, uint* indices);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_model_surface(uint model, uint surface, out Mw2SurfaceInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_model_surface_texture(uint model, uint surface, byte* data, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_surface_visible(uint weapon, uint surface);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_viewmodel_build(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_viewmodel_build_left(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_viewmodel_destroy(IntPtr vm);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_info(IntPtr vm, out Mw2ViewmodelInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_mesh(IntPtr vm, float* positions, float* normals, float* uvs, int* boneIndex, float* boneWeight, uint* indices, float* bindposes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_surface(IntPtr vm, uint surface, out Mw2SurfaceInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_viewmodel_surface_texture(IntPtr vm, uint surface, byte* data, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_surface_blend(IntPtr vm, uint surface);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_viewmodel_surface_color_map(IntPtr vm, uint surface, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_weapon_surface_blend(uint weapon, uint surface);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_step(IntPtr vm, IntPtr sim, float dt, float* pose);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_model_index(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_string(uint weapon, uint field, byte* output, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_ammo_counter(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_view(uint weapon, out Mw2WeaponView view);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_view_sway(IntPtr sim, float frametime, out Mw2ViewSway sway);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_font_info(byte* name, UIntPtr len, out int pixelHeight, out uint glyphs, byte* material, uint cap, out uint materialLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_font_glyphs(byte* name, UIntPtr len, Mw2Glyph* output, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_score(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_xp(uint id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_rank_for_xp(uint xp, out Mw2Rank rank, byte* icon, uint cap, out uint iconLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_loop_start(byte* name, UIntPtr len, float volume);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_loop_volume(uint id, float volume);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_loop_stop(uint id);
#if MW2_DEV
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_tape_start();
#endif
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_hud_set_lock(int stage, float x, float y);
#if MW2_DEV
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_tape_advance(float dt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float mw2_tape_stop(byte* path, UIntPtr len);
#endif
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_model_tag_frame(uint model, byte* name, UIntPtr len, float* out9);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_vision(byte* name, UIntPtr len, byte* output, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_hud_image(byte* name, UIntPtr len, out uint w, out uint h, byte* output, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_table_count();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_id(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_kills(uint id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_streaks_set_kill_scale(float scale);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_streaks_set_lap_growth(float growth);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_next_kills(IntPtr ks, uint id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_string(uint id, uint field, byte* output, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_streaks_create();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_streaks_destroy(IntPtr ks);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_set_loadout(IntPtr ks, uint* ids, uint n);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_kill(IntPtr ks, uint* earned, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_streak_new_life(IntPtr ks);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_streak_give(IntPtr ks, uint id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_take(IntPtr ks);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_streak_state(IntPtr ks, out Mw2StreakState state);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_roll_airdrop(uint roll);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_streak_roll_mega(uint roll);

        static readonly System.Collections.Generic.Dictionary<ulong, string> weaponStrings = new System.Collections.Generic.Dictionary<ulong, string>();

        /// Field 0: HUD icon material. Cached (the HUD asks every frame).
        public static string WeaponString(uint weapon, uint field)
        {
            ulong key = ((ulong)weapon << 8) | field;
            if (weaponStrings.TryGetValue(key, out var s)) return s;
            uint n = mw2_weapon_string(weapon, field, null, 0);
            s = "";
            if (n > 0)
            {
                var b = new byte[n];
                fixed (byte* p = b) mw2_weapon_string(weapon, field, p, n);
                s = System.Text.Encoding.UTF8.GetString(b);
            }
            weaponStrings[key] = s;
            return s;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_prefetch_weapon(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_model_surface_material(uint model, uint surface, byte* nameOut, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_model_surface_rgba(uint model, uint surface, out uint w, out uint h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_viewmodel_surface_rgba(IntPtr vm, uint surface, out uint w, out uint h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_model_tag(uint model, byte* name, UIntPtr len, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_move_events(IntPtr sim, int* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_alias_exists(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_projectile_sound(uint weapon, uint kind, byte* out_, uint cap);
        public static bool AliasExists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return mw2_alias_exists(p, (UIntPtr)b.Length) == 1;
        }
        // MW2 offhand + projectiles (ABI 19)
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_set_offhand(IntPtr sim, uint lethal, int lethalCount, uint tactical, int tacticalCount);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_offhand_viewmodel_weapon(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_offhand_ammo(IntPtr sim, out int lethal, out int tactical);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_missiles_clear(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_melee_target(IntPtr sim, float yaw, float dist);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_melee_damage(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_class(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mw2_missiles(IntPtr sim, Mw2Missile* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mw2_missile_events(IntPtr sim, Mw2MissileEvent* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_missile_detonate(IntPtr sim, uint id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_missile_remove(IntPtr sim, uint id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_projectile(uint weapon, byte* modelOut, uint cap, byte* trailOut, uint trailCap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_bounce_sound(uint weapon, uint surface, byte* out_, uint cap);
        // MW2 HUD menus (ABI 18)
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_hud_init();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mw2_hud_frame(Mw2HudState* state, Mw2HudCmd* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_hud_cmd_string(ushort index, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_hud_font_name(uint font, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_hud_set_offhand(byte* frag, UIntPtr fragLen, byte* smoke, UIntPtr smokeLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_hud_set_binding(byte* command, UIntPtr commandLen, byte* label, UIntPtr labelLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_hud_set_ride(uint kind);
        /// MW2's bullet_penetration_mp depth (units) for a penetration type through an IW4 surface type.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float mw2_pen_depth(int penetrateType, uint surfaceType);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_penetration(uint weapon, out int penetrateType, out float multiplier);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_mantle_trans_index(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_hud_set_north_yaw(float deg);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_hud_set_dpad(uint slot, byte* material, UIntPtr len, int ratio, uint atlasRows, uint atlasCols, int usable);
        // MW2 front-end menus: Create-a-Class (ABI 21)
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_init(byte* pdataPath, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_menu_switch_pdata(byte* pdataPath, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_menu_open(byte* name, UIntPtr len);
#if MW2_DEV
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_pdata_readonly(int on);
#endif
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_menu_text(byte* text, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_menu_editing();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_menu_close_all();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_depth();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_menu_set_local_int(byte* name, UIntPtr len, int value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_menu_local_int(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mw2_menu_frame(float w, float h, int timeMs, float x, float y, uint keys, Mw2HudCmd* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_drain(uint what, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_player_data(byte* path, UIntPtr len, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_class_loadout(uint klass, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_unlock_all(uint* challenges);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_sprint_scale(float scale);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_jump_scale(float scale);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_hybrid_movement(float airControl, int noLandSlowdown);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_max_ammo(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_viewmodel_surface_painted(IntPtr vm, uint surface, out uint w, out uint h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_localize(byte* key, UIntPtr len, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_localize_set(byte* key, UIntPtr keyLen, byte* text, UIntPtr textLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_menu_set_faction(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_table_lookup(byte* table, UIntPtr tableLen, uint col0, byte* key, UIntPtr keyLen, uint col, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float mw2_hudelem_em_px(int elemFont, float fontScale, float scaleY);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_table_column(byte* table, UIntPtr tableLen, uint col, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_menu_set_backdrop(byte* menu, UIntPtr len, uint count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_menu_set_player_data(byte* path, UIntPtr pathLen, byte* value, UIntPtr valueLen);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_menu_item_unlocked(byte* item, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_menu_custom_class_slots();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_perks(IntPtr sim, uint perks0);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_weapon_camo(uint weapon, byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_material_state_bits(byte* name, UIntPtr len, uint* out_);

        /// MW2 camoTable ids of the camos set per weapon (the viewmodel cache keys on them).
        public static readonly System.Collections.Generic.Dictionary<uint, uint> CamoIds = new System.Collections.Generic.Dictionary<uint, uint>();

        public static void SetCamo(uint weapon, string camo, uint camoId)
        {
            if (weapon == 0) return;
            var b = System.Text.Encoding.UTF8.GetBytes(camo ?? "");
            fixed (byte* p = b) mw2_set_weapon_camo(weapon, p, (UIntPtr)b.Length);
            if (camoId == 0) CamoIds.Remove(weapon); else CamoIds[weapon] = camoId;
        }
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mw2_weapon_equipment(uint weapon, out Mw2Equipment equipment);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_streaks_set_hardline(IntPtr ks, int on);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_challenge_kill(byte* weapon, UIntPtr len, int headshot, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_challenge_progress(byte* name, UIntPtr len, long amount, byte* out_, uint cap);
        // MW2 weapon effects (ABI 17): flash / eject FX names, tracers, impact table, viewmodel tags
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_fx(uint weapon, uint kind, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int mw2_weapon_tracer(uint weapon, out Mw2Tracer tracer);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_tracer_texture(ushort material, out uint w, out uint h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_tracer_blend(ushort material);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_impact_fx(uint impactType, uint surface, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_surface_name(uint surface, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_impact_type(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_tag(IntPtr vm, byte* name, UIntPtr len, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_tag_pose(IntPtr vm, byte* name, UIntPtr len, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_rocket_model(uint weapon, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_rocket_visible(IntPtr sim);
        // MW2 FX (ABI 16)
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_init();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_find(byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_play(uint fx, float* origin, float* fwd, float* up);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_fx_move(uint handle, float* origin, float* fwd, float* up);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_fx_stop(uint handle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_update(float dt, float* camOrigin, float* camFwd, float* camUp);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mw2_fx_quads(Mw2FxQuad* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_fx_material(ushort material, byte* nameOut, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_texture(ushort material, out uint w, out uint h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_sounds(byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_fx_live();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_ammo(IntPtr sim, out int clip, out int stock);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_set_ammo(IntPtr sim, int clip, int stock);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_reset_idle_sway(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_pilot_ready(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_pilot_set_ads(IntPtr sim, float frac);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_pilot_fill_clip(IntPtr sim);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_force(IntPtr vm, int slot, float frac);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float mw2_viewmodel_slot_seconds(IntPtr vm, uint slot);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_play(IntPtr vm, uint slot);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_viewmodel_find_bone(IntPtr vm, byte* name, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_viewmodel_set_force_glide(IntPtr vm, float secs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_viewmodel_blend_from(IntPtr vm, IntPtr old, float secs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float mw2_viewmodel_match_phase(IntPtr vm, IntPtr old, uint slot, float around);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_set_lock(IntPtr sim, int stage, float x, float y, float z);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_set_left_clip(IntPtr sim, int clip);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_weapon_alternate(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_fx_is_looping(uint fx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_viewmodel_sounds(IntPtr vm, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_character_build(byte* faction, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_character_build_ex(byte* faction, UIntPtr len, byte* cls, UIntPtr clsLen, uint variant, uint head);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_character_skins(byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_use_viewhands(IntPtr c);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mw2_character_destroy(IntPtr c);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_info(IntPtr c, out Mw2CharacterInfo info);
        /// A character bone's MW2 name (j_head, j_spine4...): its length, 0 if none.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_bone_name(IntPtr c, uint bone, byte* name, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_mesh(IntPtr c, float* positions, float* normals, float* uvs, int* boneIndex, float* boneWeight, uint* indices, float* bindposes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_surface(IntPtr c, uint surface, out Mw2SurfaceInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_character_surface_rgba(IntPtr c, uint surface, uint* w, uint* h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_surface_material(IntPtr c, uint surface, byte* name, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_step(IntPtr c, Mw2CharacterInput* input, float* pose);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_weapon_tag(IntPtr c, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_tag(IntPtr c, byte* name, UIntPtr len, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_is_akimbo(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_is_shield(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_prewarm();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_world_model(uint weapon, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_weapon_info(uint weapon, out Mw2CharacterInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_weapon_mesh(uint weapon, float* positions, float* normals, float* uvs, uint* indices);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_character_weapon_surface(uint weapon, uint surface, out Mw2SurfaceInfo info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_character_weapon_surface_rgba(uint weapon, uint surface, uint* w, uint* h, byte* out_, uint cap);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_reticle(uint weapon, out Mw2Reticle reticle);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mw2_viewmodel_build_as(uint weapon, uint look);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_weapon_alternate_raise_ms(uint weapon);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mw2_switch_alternate(IntPtr sim, uint weapon, int fallbackRaiseMs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_model_rotors(uint model, byte* classes, uint cap, float* pivots);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint mw2_prefetch(byte* names, UIntPtr len);

        public static uint Prefetch(System.Collections.Generic.IEnumerable<string> names)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", names));
            fixed (byte* p = b) return mw2_prefetch(p, (UIntPtr)b.Length);
        }

        public static uint ModelIndex(string name)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return mw2_model_index(p, (UIntPtr)b.Length);
        }

        public static uint StreakId(string name)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return mw2_streak_id(p, (UIntPtr)b.Length);
        }

        /// Field: 0 name, 1 earn sound, 2 earn dialog, 3 use dialog, 4 weapon, 5 icon, 6 crate icon, 7 dpad icon.
        public static string StreakString(uint id, uint field)
        {
            uint n = mw2_streak_string(id, field, null, 0);
            if (n == 0) return "";
            var b = new byte[n];
            fixed (byte* p = b) mw2_streak_string(id, field, p, n);
            return System.Text.Encoding.UTF8.GetString(b);
        }

        public static int PlayAlias(string name, float volume)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return mw2_play_alias(p, (UIntPtr)b.Length, volume);
        }

        public static int LoadWeapons(string path)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(path);
            fixed (byte* p = b) return mw2_load_weapons(p, (UIntPtr)b.Length);
        }

        public static uint WeaponIndex(string name)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = b) return mw2_weapon_index(p, (UIntPtr)b.Length);
        }
    }
}
