//! MW2 player movement behind a C ABI.
//!
//! The host (Risk of Rain 2) owns the world: every collision query `pmove` makes is
//! forwarded to a host callback that sweeps a box through the host's physics scene.
//! All positions crossing this boundary are IW4 map units (inches), Z up; the host
//! converts to and from its own space.
//!
//! Panics never cross the boundary: every entry point catches unwinds and reports
//! failure through its return value.

pub mod audio;
pub mod character;
pub mod equipment;
pub mod fonts;
pub mod fx;
pub mod hud;
pub mod menus;
pub mod images;
pub mod kick;
pub mod penetration;
pub mod progression;
pub mod killstreaks;
pub mod models;
pub mod sounds;
pub mod viewmodel;
pub mod visions;
pub mod weapon_fx;
pub mod weapons;

use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};

use movement_iw4::jump::JumpLaunchContext;
use movement_iw4::{
    ANGLE2SHORT, AdsFracContext, AdsIntentContext, AirMoveContext, CmdScaleWalkContext,
    CollisionBackend, FlatMantleAnimLength, GroundTraceInput, MeleeChargeWeaponDelays,
    MoveBounds, PmoveSingleContext, SprintContext, ViewAngleClamp, WalkMoveContext,
    ZeroMantleRootDelta, get_max_sprint_time, pmove,
};
use playerstate_iw4::{ENTITYNUM_NONE, PlayerState, UserCmd, pm_flags};
use trace_iw4::{ENTITYNUM_WORLD, HITTYPE_ENTITY, Trace};

/// Bumped whenever a `#[repr(C)]` struct or a function signature here changes.
pub const ABI_VERSION: u32 = 31;

/// Sprint length multiplier on MW2's 4 s (host setting: RoR2's maps are far bigger than MW2's).
static SPRINT_TIME_SCALE: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0x3F80_0000); // 1.0

fn sprint_time_scale() -> f32 {
    f32::from_bits(SPRINT_TIME_SCALE.load(std::sync::atomic::Ordering::Relaxed))
}

/// Set the sprint length multiplier (1 = MW2's 4 s; Marathon still sprints forever).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_set_sprint_scale(scale: f32) {
    let s = if scale.is_finite() { scale.clamp(0.25, 10.0) } else { 1.0 };
    SPRINT_TIME_SCALE.store(s.to_bits(), std::sync::atomic::Ordering::Relaxed);
}

/// Jump height multiplier on MW2's 39 units (host setting: RoR2's curbs and rocks a survivor
/// jumps onto were out of MW2's reach).
static JUMP_HEIGHT_SCALE: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0x3F80_0000); // 1.0

fn jump_height_scale() -> f32 {
    f32::from_bits(JUMP_HEIGHT_SCALE.load(std::sync::atomic::Ordering::Relaxed))
}

/// Set the jump height multiplier (1 = MW2's 39 units).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_set_jump_scale(scale: f32) {
    let s = if scale.is_finite() { scale.clamp(0.5, 4.0) } else { 1.0 };
    JUMP_HEIGHT_SCALE.store(s.to_bits(), std::sync::atomic::Ordering::Relaxed);
}

/// RoR2-style movement on top of MW2's (playtest 10-06-26: "the inertia you get when trying to jump from a
/// sprint, more ror2 hybridey"): extra air acceleration (MW2's own is 1.0 - barely any steering in the
/// air) and no landing slowdown (MW2 runs friction up to 2.5x after a jump, 2x after a hard landing).
static AIR_CONTROL: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0); // 0.0
static NO_LAND_SLOWDOWN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Extra air acceleration (0 = MW2's alone) and whether landing slows the player (MW2's) or not.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_set_hybrid_movement(air_control: f32, no_land_slowdown: i32) {
    let a = if air_control.is_finite() { air_control.clamp(0.0, 20.0) } else { 0.0 };
    AIR_CONTROL.store(a.to_bits(), std::sync::atomic::Ordering::Relaxed);
    NO_LAND_SLOWDOWN.store(no_land_slowdown != 0, std::sync::atomic::Ordering::Relaxed);
}

const CONTENTS_SOLID: u32 = 1;

// The C# side checks Marshal.SizeOf against these at startup.
const _: () = assert!(size_of::<Mw2Trace>() == 36);
const _: () = assert!(size_of::<Mw2Input>() == 28);
const _: () = assert!(size_of::<Mw2State>() == 96);
const _: () = assert!(size_of::<Mw2ClipInfo>() == 16);
const _: () = assert!(size_of::<weapons::Mw2Shot>() == 56);
const _: () = assert!(size_of::<fx::Mw2FxQuad>() == 88);
const _: () = assert!(size_of::<weapon_fx::Mw2Tracer>() == 108);

/// One box sweep, filled in by the host.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2Trace {
    pub fraction: f32,
    pub normal: [f32; 3],
    pub endpos: [f32; 3],
    pub surface_flags: u32,
    pub startsolid: u8,
    pub allsolid: u8,
    pub walkable: u8,
    pub _pad: u8,
}

/// Host collision callback: sweep the box `mins..maxs` from `start` to `end`.
pub type Mw2TraceFn = extern "C" fn(
    user: *mut c_void,
    start: *const [f32; 3],
    end: *const [f32; 3],
    mins: *const [f32; 3],
    maxs: *const [f32; 3],
    mask: u32,
    out: *mut Mw2Trace,
);

/// One tick of player input.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2Input {
    /// Milliseconds since the previous step (pmove clamps to 1..=200).
    pub msec: i32,
    /// `playerstate_iw4::buttons` bits (ATTACK 0x1, SPRINT 0x2, CROUCH 0x200, JUMP 0x400, ADS 0x800,
    /// ...). Equipment: FRAG 0x4000 throws the lethal slot, SMOKE 0x8000 the tactical slot
    /// (`mw2_set_offhand`): hold to pull the pin (and cook, for grenades that cook), release to
    /// throw; OFFHAND_HOLD_CANCEL 0x200000 cancels a pull for weapons that allow it.
    pub buttons: u32,
    pub forwardmove: i8,
    pub rightmove: i8,
    pub _pad: [u8; 2],
    /// Degrees. Yaw 0 = +X; pitch positive looks down.
    pub yaw: f32,
    pub pitch: f32,
    /// Multiplies MW2's 190 u/s base speed (host move-speed items).
    pub speed_scale: f32,
    /// Divides MW2 weapon timers (host attack speed). 1 = stock.
    pub fire_rate: f32,
}

/// Player state after a step.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2State {
    pub origin: [f32; 3],
    pub velocity: [f32; 3],
    pub viewangles: [f32; 3],
    pub view_height: f32,
    pub pm_flags: u32,
    pub grounded: u8,
    pub sprinting: u8,
    pub _pad: [u8; 2],
    /// Weapon table index (0 = unarmed).
    pub weapon: u32,
    pub clip: i32,
    pub stock: i32,
    /// `weapon_iw4::WeaponState` as i32 (0 ready, 6 firing, 8 reloading, ...).
    pub weaponstate: i32,
    /// 0 = hip, 1 = fully aimed down sights.
    pub ads_frac: f32,
    /// Pellets queued by this step; drain with `mw2_take_shots`.
    pub shots_pending: u32,
    /// `weapons::EV_*` bits raised during this step.
    pub events: u32,
    /// MW2 view kick (pitch, yaw, roll degrees) to add to the camera; self-centering.
    pub kick_angles: [f32; 3],
    /// Current spread cone in degrees (crosshair gap).
    pub spread_degrees: f32,
    /// Akimbo's left clip; -1 when the weapon isn't akimbo.
    pub clip_left: i32,
}

/// Shape of a PCM clip; samples come out as interleaved f32 via `mw2_clip_samples`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2ClipInfo {
    pub rate: u32,
    pub channels: u32,
    pub frames: u32,
    pub _pad: u32,
}

pub struct Mw2Sim {
    ps: PlayerState,
    server_time: i32,
    old_buttons: u32,
    old_angles: [i32; 3],
    armed: Option<weapons::Armed>,
    rng: weapons::Rng,
    shots: Vec<weapons::Mw2Shot>,
    events: u32,
    /// MW2's viewmodel anim state: the hand's `weap_anim` and a count of its changes (the
    /// viewmodel runs per frame, the sim per tick, so it reads the latest change).
    vm_anim: i32,
    vm_anim_seq: u32,
    /// Akimbo's left hand anim event and its sequence (the left viewmodel's).
    vm_anim_left: i32,
    vm_anim_seq_left: u32,
    fire_rate: f32,
    /// Movement events (footsteps, jump, landing, mantle) from MW2's player-state event ring,
    /// queued for the host's sounds: (event id, parm).
    move_events: Vec<[i32; 2]>,
    last_event_seq: i32,
    /// Scope sway clock and crouch/prone factor (IW4L view_camera_idle state).
    weap_idle_time: i32,
    /// _stinger.gsc's stingerStage as the host runs it: 0 none, 1 locking, 2 locked (on `lock_pos`).
    lock_stage: i32,
    lock_pos: [f32; 3],
    view_last_idle_factor: f32,
    /// Lethal / tactical equipment (`mw2_set_offhand`) and the player's live grenades, rockets
    /// and placed charges (`equipment`).
    offhand: equipment::Offhand,
    missiles: equipment::Missiles,
    /// MW2's lunge target for the next melee press: yaw (degrees) toward it and distance
    /// (units, 0 = none), set by the host each frame (`mw2_set_melee_target`).
    melee_target: (f32, u8),
}

struct HostWorld {
    trace: Mw2TraceFn,
    user: *mut c_void,
}

impl equipment::Sweep for HostWorld {
    fn sweep(&self, start: [f32; 3], end: [f32; 3], half: f32, mask: u32) -> Mw2Trace {
        let mut out = Mw2Trace { fraction: 1.0, endpos: end, ..Mw2Trace::default() };
        let (mins, maxs) = ([-half; 3], [half; 3]);
        (self.trace)(self.user, &start, &end, &mins, &maxs, mask, &mut out);
        out
    }
}

impl CollisionBackend for HostWorld {
    fn trace(&self, input: GroundTraceInput) -> Trace {
        let mut out = Mw2Trace {
            fraction: 1.0,
            endpos: input.end,
            ..Mw2Trace::default()
        };
        (self.trace)(
            self.user,
            &input.start,
            &input.end,
            &input.mins,
            &input.maxs,
            input.tracemask,
            &mut out,
        );
        let mut trace = Trace {
            fraction: out.fraction,
            normal: out.normal,
            endpos: out.endpos,
            surface_flags: out.surface_flags,
            startsolid: out.startsolid,
            allsolid: out.allsolid,
            walkable: out.walkable,
            ..Trace::default()
        };
        if out.fraction < 1.0 || out.startsolid != 0 {
            // World geometry reports as an entity hit on ENTITYNUM_WORLD
            // (trace_iw4::brush_sweep_hit_kind); without it the player never grounds.
            trace.contents = CONTENTS_SOLID;
            trace.hit_type = HITTYPE_ENTITY;
            trace.hit_id = ENTITYNUM_WORLD;
        }
        trace
    }
}

/// IW4L `sim::step::pmove_context`; neutral values when unarmed.
fn context(old_buttons: u32, weapon: Option<&weapons::WeaponRow>, fire_rate: f32) -> PmoveSingleContext {
    let f = weapon.map(|w| weapons::scaled(w.facts, fire_rate));
    let air = AirMoveContext {
        player_spectate_speed_scale: 1.0,
        shellshock_gravity_scale: 1.0,
        shellshock_gravity_bias: 0.0,
    };
    PmoveSingleContext {
        walk: WalkMoveContext {
            cmd_scale: CmdScaleWalkContext {
                player_back_speed_scale: 0.7,
                player_strafe_speed_scale: 0.8,
                player_sprint_speed_scale: 1.5,
                player_last_stand_crawl_speed_scale: 0.15,
                weapon_move_speed_scale: weapon.map_or(1.0, |w| w.move_speed_scale),
                weapon_ads_move_speed_scale: weapon.map_or(1.0, |w| w.ads_move_speed_scale),
                shellshock_affects_movement: false,
            },
            weapon_move_scale: 1.0,
            old_buttons,
            jump: JumpLaunchContext {
                jump_height: 39.0 * jump_height_scale(),
                dive: false,
                crouch_jump_scale: 1.0,
                jump_ladder_push_vel: 128.0,
            },
            air,
        },
        air,
        bounds: MoveBounds {
            mins: [-15.0, -15.0, 0.0],
            maxs: [15.0, 15.0, 70.0],
            tracemask: 0x0281_0011,
        },
        view_angles: ViewAngleClamp {
            pitch_up: 85.0,
            pitch_down: 85.0,
            unclamped_pitch_bit: false,
        },
        sprint: SprintContext {
            weapon_max_sprint_time: get_max_sprint_time(weapon.map_or(1.0, |w| w.sprint_duration_scale), 4.0 * sprint_time_scale()),
            sprint_forever: false,
            min_sprint_time_seconds: 1.0,
            sprint_delay_seconds: 0.0,
            sprint_forward_minimum: 105,
            stand_up_clear: true,
            sprint_recharge_pause_seconds: 0.0,
        },
        ads_intent: AdsIntentContext {
            ads_allowed: f.is_none_or(|f| f.aim_down_sight),
            weapon_def_scope: f.is_some_and(|f| f.overlay_reticle != 0),
            sprint_hold_ads: false,
        },
        ads_frac: AdsFracContext {
            aim_down_sight: f.is_none_or(|f| f.aim_down_sight),
            ads_in_rate: f.map_or(1.0, |f| f.ads_in_rate),
            ads_out_rate: f.map_or(1.0, |f| f.ads_out_rate),
            ads_reload_trans_time_ms: f.map_or(0, |f| f.ads_reload_trans_time_ms),
            segmented_reload: f.is_some_and(|f| f.segmented_reload),
            rechamber_while_ads: f.is_some_and(|f| f.rechamber_while_ads),
            ads_fire_only: f.is_some_and(|f| f.ads_fire_only),
        },
        melee_charge: MeleeChargeWeaponDelays {
            melee_delay_ms: f.map_or(0, |f| f.melee_delay_ms),
            melee_charge_delay_ms: f.map_or(0, |f| f.melee_charge_delay_ms),
        },
        player_melee_range: movement_iw4::MELEE_CHARGE_PLAYER_MELEE_RANGE_DEFAULT,
        old_buttons,
        weapon_blocks_prone: false,
        can_hold_breath: f.is_some_and(|f| f.can_hold_breath),
    }
}

/// IW4L `sim::world::spawn_player_state`.
fn spawn(origin: [f32; 3]) -> PlayerState {
    let mut ps = PlayerState::ZERO;
    ps.origin = origin;
    ps.speed = 190;
    ps.health = 100;
    ps.max_health = 100;
    ps.move_speed_scale_multiplier = 1.0;
    ps.view_height_target = 60;
    ps.view_height_current = 60.0;
    ps.gravity = 800;
    ps.ground_entity_num = ENTITYNUM_NONE;
    ps.other_flags |= playerstate_iw4::other_flags::PLAYER;
    ps.corpse_index = -1;
    ps
}

impl Mw2Sim {
    fn new(origin: [f32; 3]) -> Self {
        let server_time = 1000;
        let mut ps = spawn(origin);
        ps.command_time = server_time;
        Self {
            ps,
            server_time,
            old_buttons: 0,
            old_angles: [0; 3],
            armed: None,
            rng: weapons::Rng::new(0x5EED_1234),
            shots: Vec::new(),
            events: 0,
            vm_anim: 0,
            vm_anim_seq: 0,
            vm_anim_left: 0,
            vm_anim_seq_left: 0,
            fire_rate: 1.0,
            move_events: Vec::new(),
            last_event_seq: 0,
            weap_idle_time: 0,
            lock_stage: 0,
            lock_pos: [0.0; 3],
            view_last_idle_factor: 1.0,
            offhand: equipment::Offhand::default(),
            missiles: equipment::Missiles::default(),
            melee_target: (0.0, 0),
        }
    }

    /// Movement events added this tick: footsteps 0x6b sprint / 0x6c run / 0x6d walk / 0x6e prone
    /// (parm = surface type), 0x6f jump, 0x70+surface landing, 0xae mantle.
    fn queue_move_events(&mut self) {
        let ps = &self.ps;
        let mut seq = self.last_event_seq;
        if ps.event_sequence - seq > 4 {
            seq = ps.event_sequence - 4;
        }
        while seq < ps.event_sequence {
            let (ev, parm) = match seq & 3 {
                0 => (ps.events_0, ps.event_parms_0),
                1 => (ps.events_1, ps.event_parms_1),
                2 => (ps.events_2, ps.event_parms_2),
                _ => (ps.events_3, ps.event_parms_3),
            };
            let movement = (0x6b..=0x6f).contains(&ev) || (0x70..0x70 + 32).contains(&ev) || ev == 0xae;
            if movement && self.move_events.len() < 64 {
                self.move_events.push([ev, parm]);
            }
            seq += 1;
        }
        self.last_event_seq = ps.event_sequence;
    }

    fn step(&mut self, input: &Mw2Input, world: &HostWorld) {
        self.server_time += input.msec.clamp(1, 200);
        self.ps.move_speed_scale_multiplier = if input.speed_scale > 0.0 { input.speed_scale } else { 1.0 };
        let mut cmd = UserCmd {
            server_time: self.server_time,
            buttons: input.buttons,
            forwardmove: input.forwardmove,
            rightmove: input.rightmove,
            ..UserCmd::default()
        };
        cmd.angles[0] = (input.pitch * ANGLE2SHORT) as i32 & 0xffff;
        cmd.angles[1] = (input.yaw * ANGLE2SHORT) as i32 & 0xffff;
        if let Some(armed) = &self.armed {
            armed.mirror_into(&mut self.ps);
        }
        let ctx = context(self.old_buttons, self.armed.as_ref().map(|a| &a.row), input.fire_rate);
        // On the ground after a jump: MW2's landing drag off (the jump flag and its timer drive it).
        let grounded_before = self.ps.ground_entity_num != ENTITYNUM_NONE;
        if NO_LAND_SLOWDOWN.load(std::sync::atomic::Ordering::Relaxed) && grounded_before {
            self.ps.pm_flags &= !(playerstate_iw4::pm_flags::JUMPING | playerstate_iw4::pm_flags::TIME_HARDLANDING);
        }
        let _ = pmove(
            &mut self.ps,
            &mut cmd,
            ctx,
            world,
            &FlatMantleAnimLength::default(),
            &ZeroMantleRootDelta,
        );
        // In the air: steering toward the stick on top of MW2's own (Quake-style accelerate, which
        // never adds speed past the wish speed - momentum is kept, direction follows the input).
        let air = f32::from_bits(AIR_CONTROL.load(std::sync::atomic::Ordering::Relaxed));
        if air > 0.0 && self.ps.ground_entity_num == ENTITYNUM_NONE && (input.forwardmove != 0 || input.rightmove != 0) {
            let yaw = input.yaw.to_radians();
            let (fwd, right) = ([yaw.cos(), yaw.sin()], [yaw.sin(), -yaw.cos()]);
            let (f, r) = (f32::from(input.forwardmove), f32::from(input.rightmove));
            let mut wish = [f * fwd[0] + r * right[0], f * fwd[1] + r * right[1]];
            let len = (wish[0] * wish[0] + wish[1] * wish[1]).sqrt();
            if len > 1e-3 {
                wish = [wish[0] / len, wish[1] / len];
                let wish_speed = self.ps.speed as f32 * (len / 127.0).min(1.0);
                let current = self.ps.velocity[0] * wish[0] + self.ps.velocity[1] * wish[1];
                let add = wish_speed - current;
                if add > 0.0 {
                    let dt = input.msec.clamp(1, 200) as f32 / 1000.0;
                    let accel = (air * dt * wish_speed).min(add);
                    self.ps.velocity[0] += accel * wish[0];
                    self.ps.velocity[1] += accel * wish[1];
                }
            }
        }
        self.queue_move_events();
        self.events = 0;
        // A Stinger lock (the host's StingerUsageLoop): locked rockets fly at it, and a weapon that
        // needs one (requireLockonToFire: the Stinger) doesn't fire without it.
        self.missiles.guide = (self.lock_stage == 2).then_some(self.lock_pos);
        if self.lock_stage != 2 && self.armed.as_ref().and_then(|a| equipment::equip_of(a.index)).is_some_and(|e| e.require_lock) {
            cmd.buttons &= !weapon_iw4::BUTTON_ATTACK;
        }
        if let Some(armed) = &mut self.armed {
            let msec = input.msec.clamp(1, 200);
            let mut launcher = equipment::Launcher { offhand: &mut self.offhand, missiles: &mut self.missiles, now: self.server_time };
            self.events = armed.tick(
                &mut self.ps,
                &cmd,
                self.old_buttons,
                self.old_angles,
                msec,
                input.fire_rate,
                &mut self.rng,
                &mut self.shots,
                &mut launcher,
                self.melee_target,
            );
        }
        self.missiles.step(self.server_time, world, &mut self.rng);
        if let Some(a) = self.armed.as_ref() {
            if a.hand.weap_anim != self.vm_anim {
                self.vm_anim = a.hand.weap_anim;
                self.vm_anim_seq = self.vm_anim_seq.wrapping_add(1);
            }
            if let Some(l) = &a.left {
                if l.weap_anim != self.vm_anim_left {
                    self.vm_anim_left = l.weap_anim;
                    self.vm_anim_seq_left = self.vm_anim_seq_left.wrapping_add(1);
                }
            }
        }
        self.fire_rate = input.fire_rate;
        self.old_buttons = input.buttons;
        self.old_angles = cmd.angles;
    }

    fn state(&self) -> Mw2State {
        Mw2State {
            origin: self.ps.origin,
            velocity: self.ps.velocity,
            viewangles: self.ps.viewangles,
            view_height: self.ps.view_height_current,
            pm_flags: self.ps.pm_flags,
            grounded: u8::from(self.ps.ground_entity_num != ENTITYNUM_NONE),
            sprinting: u8::from(self.ps.pm_flags & pm_flags::SPRINTING != 0),
            _pad: [0; 2],
            weapon: self.armed.as_ref().map_or(0, |a| a.index),
            clip: self.armed.as_ref().map_or(0, |a| a.hand.clip),
            stock: self.armed.as_ref().map_or(0, |a| a.hand.stock),
            weaponstate: self.armed.as_ref().map_or(0, |a| a.hand.weaponstate),
            ads_frac: self.ps.f_weapon_pos_frac,
            shots_pending: self.shots.len() as u32,
            events: self.events,
            kick_angles: self.armed.as_ref().map_or([0.0; 3], |a| a.kick.kick_angles),
            spread_degrees: self.armed.as_ref().map_or(0.0, |a| a.spread_degrees),
            clip_left: self.armed.as_ref().and_then(|a| a.left.as_ref()).map_or(-1, |l| l.clip),
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn mw2_abi_version() -> u32 {
    ABI_VERSION
}

/// Returns null on failure. Free with `mw2_destroy`.
///
/// # Safety
/// `origin` must point to three floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_create(origin: *const [f32; 3]) -> *mut Mw2Sim {
    catch_unwind(|| {
        let origin = if origin.is_null() { [0.0; 3] } else { unsafe { *origin } };
        Box::into_raw(Box::new(Mw2Sim::new(origin)))
    })
    .unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `sim` must come from `mw2_create` and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_destroy(sim: *mut Mw2Sim) {
    if !sim.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(sim) })));
    }
}

/// Teleport: set origin, zero velocity, drop to airborne.
///
/// # Safety
/// `sim` from `mw2_create`; `origin` points to three floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_origin(sim: *mut Mw2Sim, origin: *const [f32; 3]) -> i32 {
    if sim.is_null() || origin.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        sim.ps.origin = unsafe { *origin };
        sim.ps.velocity = [0.0; 3];
        sim.ps.ground_entity_num = ENTITYNUM_NONE;
        1
    }))
    .unwrap_or(0)
}

/// Overwrite velocity (host knockback, launch pads). Units per second.
///
/// # Safety
/// `sim` from `mw2_create`; `velocity` points to three floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_velocity(sim: *mut Mw2Sim, velocity: *const [f32; 3]) -> i32 {
    if sim.is_null() || velocity.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        sim.ps.velocity = unsafe { *velocity };
        // A push upward (a launch pad) leaves the ground; pmove would otherwise clip it back.
        if sim.ps.velocity[2] > 0.0 {
            sim.ps.ground_entity_num = ENTITYNUM_NONE;
        }
        1
    }))
    .unwrap_or(0)
}

/// Advance one tick. Returns 1 on success, 0 on bad arguments, -1 if the sim panicked
/// (state is left as it was before the panic point; `out` is not written).
///
/// # Safety
/// `sim` from `mw2_create`; `input`/`out` valid; `trace` must be callable for the
/// duration of the call with `user`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_step(
    sim: *mut Mw2Sim,
    input: *const Mw2Input,
    trace: Option<Mw2TraceFn>,
    user: *mut c_void,
    out: *mut Mw2State,
) -> i32 {
    let (Some(trace), false, false, false) = (trace, sim.is_null(), input.is_null(), out.is_null())
    else {
        return 0;
    };
    let result = catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let world = HostWorld { trace, user };
        sim.step(unsafe { &*input }, &world);
        sim.state()
    }));
    match result {
        Ok(state) => {
            unsafe { *out = state };
            1
        }
        Err(_) => -1,
    }
}

/// Load every weapon from a `common_mp.ff` (UTF-8 path, not NUL-terminated).
/// Returns the number of weapons, or -1 on failure.
///
/// # Safety
/// `path` points to `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_load_weapons(path: *const u8, len: usize) -> i32 {
    if path.is_null() {
        return -1;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(path, len) };
        let Ok(path) = std::str::from_utf8(bytes) else { return -1 };
        let path = std::path::Path::new(path);
        let n = weapons::load(path).map_or(-1, |n| n as i32);
        // Killstreak rules sit in the zones beside common_mp; weapons work without them.
        if let Some(dir) = path.parent() {
            let _ = killstreaks::load(dir);
        }
        n
    })
    .unwrap_or(-1)
}

/// Weapon table index for an MW2 weapon name such as `ak47_mp`, or 0 if unknown.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_index(name: *const u8, len: usize) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, weapons::index_of)
    })
    .unwrap_or(0)
}

/// Arm the player (full clip + start ammo). Index 0 disarms. Returns 1 on success.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_give_weapon(sim: *mut Mw2Sim, index: u32) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        if index == 0 {
            sim.armed = None;
            sim.ps.weapon = 0;
            return 1;
        }
        let Some(row) = weapons::row(index) else { return 0 };
        let armed = weapons::Armed::new(index, row);
        // A new gun is a clean hand: any pin pull in progress is dropped (the equipment stays).
        sim.ps.weap_flags &= !playerstate_iw4::weap_flags::OFFHAND_VIEW;
        sim.ps.off_hand_index = 0;
        sim.ps.grenade_time_left = 0;
        armed.mirror_into(&mut sim.ps);
        sim.armed = Some(armed);
        1
    }))
    .unwrap_or(0)
}

/// The held weapon's clip and reserve (to restore after a killstreak call-in).
///
/// # Safety
/// `sim` from `mw2_create`; out pointers valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_ammo(sim: *const Mw2Sim, clip: *mut i32, stock: *mut i32) -> i32 {
    if sim.is_null() || clip.is_null() || stock.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(a) = unsafe { &*sim }.armed.as_ref() else { return 0 };
        unsafe {
            *clip = a.hand.clip;
            *stock = a.hand.stock;
        }
        1
    }))
    .unwrap_or(0)
}

/// The Stinger / AT4 lock the host's StingerUsageLoop holds: `stage` 0 none, 1 locking, 2 locked
/// on the IW4 world point `x, y, z` (refresh it every frame: the target moves).
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_lock(sim: *mut Mw2Sim, stage: i32, x: f32, y: f32, z: f32) {
    if !sim.is_null() {
        let sim = unsafe { &mut *sim };
        sim.lock_stage = stage;
        sim.lock_pos = [x, y, z];
    }
}

/// Pilot showcase: the held weapon ready at once (no raise), its hands idle, so guns swap in place.
/// Play never calls it.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_pilot_ready(sim: *mut Mw2Sim) {
    if sim.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let Some(a) = sim.armed.as_mut() else { return };
        for h in std::iter::once(&mut a.hand).chain(a.left.as_mut()) {
            h.weaponstate = weapon_iw4::WeaponState::Ready as i32;
            h.weapon_time = 0;
            h.weapon_delay = 0;
            weapon_iw4::start_weapon_anim(&mut h.weap_anim, weapon_iw4::weap_anim_event::IDLE);
        }
        a.mirror_into(&mut sim.ps);
    }));
}

/// Pilot showcase: hold the viewmodel's `slot` clip at `frac` (0..1) instead of the sim's anims;
/// `slot` < 0 lets go (back to idle). Returns 0 when the weapon has no clip in that slot.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_force(vm: *mut viewmodel::Viewmodel, slot: i32, frac: f32) -> i32 {
    if vm.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| i32::from(unsafe { &mut *vm }.force(usize::try_from(slot).ok(), frac)))).unwrap_or(0)
}

/// Pilot showcase: `new` glides from `old`'s last pose over `secs` (gun swaps without the snap).
///
/// # Safety
/// `new` and `old` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_blend_from(new: *mut viewmodel::Viewmodel, old: *const viewmodel::Viewmodel, secs: f32) {
    if new.is_null() || old.is_null() || std::ptr::eq(new, old) {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe { &mut *new }.blend_from(unsafe { &*old }, secs)));
}

/// Pilot showcase: the point (0..1) of `new`'s `slot` clip whose pose is nearest `old`'s current
/// one (motion matching for the reload relay); -1 when it can't tell.
///
/// # Safety
/// `new` and `old` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_match_phase(new: *const viewmodel::Viewmodel, old: *const viewmodel::Viewmodel, slot: u32, around: f32) -> f32 {
    if new.is_null() || old.is_null() || std::ptr::eq(new, old) {
        return -1.0;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe { &*new }.match_phase(slot as usize, unsafe { &*old }, around).unwrap_or(-1.0))).unwrap_or(-1.0)
}

/// Pilot showcase: how long a held clip glides in and out (seconds).
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_set_force_glide(vm: *mut viewmodel::Viewmodel, secs: f32) {
    if !vm.is_null() && secs.is_finite() {
        unsafe { &mut *vm }.force_glide = secs.clamp(0.0, 2.0);
    }
}

/// A viewmodel bone's index by name (the pose's order), -1 if it has none.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`; `name` points at `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_find_bone(vm: *const viewmodel::Viewmodel, name: *const u8, len: usize) -> i32 {
    if vm.is_null() || name.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let n = std::str::from_utf8(unsafe { std::slice::from_raw_parts(name, len) }).unwrap_or("");
        unsafe { &*vm }.find_bone(n).map_or(-1, |i| i as i32)
    }))
    .unwrap_or(-1)
}

/// Play a viewmodel clip slot once from its start (the killstreak trigger's click). 1 played, 0 none.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_play(vm: *mut viewmodel::Viewmodel, slot: u32) -> i32 {
    if vm.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| i32::from(unsafe { &mut *vm }.play_once(slot as usize)))).unwrap_or(0)
}

/// A viewmodel slot's length in MW2 play (seconds): the weapon's timer, else the clip's length.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_slot_seconds(vm: *const viewmodel::Viewmodel, slot: u32) -> f32 {
    if vm.is_null() {
        return 0.0;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe { &*vm }.slot_seconds(slot as usize))).unwrap_or(0.0)
}

/// Pilot showcase: the held magazine (both, akimbo) full - the firing beats never run dry
/// into an unplanned reload. Play never calls it.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_pilot_fill_clip(sim: *mut Mw2Sim) {
    if sim.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let Some(a) = sim.armed.as_mut() else { return };
        let size = a.row.facts.clip_size.max(0);
        a.hand.clip = size;
        if let Some(l) = a.left.as_mut() {
            l.clip = size;
        }
        a.mirror_into(&mut sim.ps);
    }));
}

/// Pilot showcase: the sights where they were (0..1) after a gun swap - given a new gun the view
/// dropped out of ADS for a frame (the zoom blinked, 10-05-26). Play never calls it.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_pilot_set_ads(sim: *mut Mw2Sim, frac: f32) {
    if !sim.is_null() {
        unsafe { (*sim).ps.f_weapon_pos_frac = frac.clamp(0.0, 1.0) };
    }
}

/// Restart the ADS idle sway clock (pilot showcase: every gun's clip sways in the same phase, so
/// the edit can swap guns mid-motion). Play never calls it.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_reset_idle_sway(sim: *mut Mw2Sim) {
    if !sim.is_null() {
        let sim = unsafe { &mut *sim };
        sim.weap_idle_time = 0;
        sim.view_last_idle_factor = 1.0;
    }
}

/// Set the held weapon's clip and reserve.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_ammo(sim: *mut Mw2Sim, clip: i32, stock: i32) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let Some(a) = sim.armed.as_mut() else { return 0 };
        a.hand.clip = clip.max(0);
        a.hand.stock = stock.max(0);
        a.mirror_into(&mut sim.ps);
        1
    }))
    .unwrap_or(0)
}

/// Akimbo: set the left gun's clip (pilot showcase: both guns part-used, so both reload).
/// Returns 0 when the weapon isn't akimbo.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_left_clip(sim: *mut Mw2Sim, clip: i32) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let Some(l) = sim.armed.as_mut().and_then(|a| a.left.as_mut()) else { return 0 };
        l.clip = clip.max(0);
        1
    }))
    .unwrap_or(0)
}

/// Copy up to `cap` queued pellets into `out` and clear the queue. Returns the count copied.
///
/// # Safety
/// `sim` from `mw2_create`; `out` has room for `cap` shots.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_take_shots(sim: *mut Mw2Sim, out: *mut weapons::Mw2Shot, cap: u32) -> u32 {
    if sim.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let n = sim.shots.len().min(cap as usize);
        for (i, shot) in sim.shots.drain(..).take(n).enumerate() {
            unsafe { *out.add(i) = shot };
        }
        sim.shots.clear();
        n as u32
    }))
    .unwrap_or(0)
}

/// The sim's current mantle transition (IW4 `ps.mantleState` transIndex, 0-6: the 57 .. 21 unit
/// climbs), for the third-person character's matching climb; -1 if `sim` is null.
///
/// # Safety
/// `sim` from `mw2_create` or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_mantle_trans_index(sim: *const Mw2Sim) -> i32 {
    if sim.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe { &*sim }.ps.mantle_trans_index)).unwrap_or(-1)
}

/// Add `rounds` to the held weapon's reserve, capped at MW2's max ammo
/// (`rounds < 0` fills it). Returns the new reserve, or -1 if unarmed.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_add_reserve(sim: *mut Mw2Sim, rounds: i32) -> i32 {
    if sim.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let Some(armed) = sim.armed.as_mut() else { return -1 };
        let cap = armed.row.facts.max_ammo.max(armed.row.facts.clip_size).max(0);
        armed.hand.stock = if rounds < 0 { cap } else { armed.hand.stock.saturating_add(rounds).min(cap) };
        armed.hand.stock
    }))
    .unwrap_or(-1)
}

/// A weapon's most ammo carried in all (clip + reserve, MW2's maxAmmo), 0 if unknown.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_max_ammo(weapon: u32) -> i32 {
    catch_unwind(|| weapons::row(weapon).map_or(0, |r| r.facts.max_ammo.max(r.facts.clip_size).max(0))).unwrap_or(0)
}

/// Clip id for one of a weapon's sounds (`weapons::SOUND_*`), or 0.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_sound(weapon: u32, kind: u32) -> u32 {
    catch_unwind(|| weapons::row(weapon).map_or(0, |r| r.sounds.get(kind as usize).copied().unwrap_or(0))).unwrap_or(0)
}

/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_clip_info(clip: u32, out: *mut Mw2ClipInfo) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(p) = weapons::clip(clip) else { return 0 };
        let bytes_per = (p.bits as usize / 8).max(1);
        let ch = p.channels.max(1) as usize;
        unsafe {
            *out = Mw2ClipInfo { rate: p.rate, channels: ch as u32, frames: (p.bytes.len() / (bytes_per * ch)) as u32, _pad: 0 };
        }
        1
    }))
    .unwrap_or(0)
}

/// Write up to `cap` interleaved f32 samples of a clip into `out`. Returns samples written.
///
/// # Safety
/// `out` has room for `cap` floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_clip_samples(clip: u32, out: *mut f32, cap: u32) -> u32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(p) = weapons::clip(clip) else { return 0 };
        let out = unsafe { std::slice::from_raw_parts_mut(out, cap as usize) };
        let mut n = 0usize;
        if p.bits == 16 {
            for (dst, src) in out.iter_mut().zip(p.bytes.chunks_exact(2)) {
                *dst = i16::from_le_bytes([src[0], src[1]]) as f32 / 32768.0;
                n += 1;
            }
        } else {
            for (dst, &b) in out.iter_mut().zip(p.bytes.iter()) {
                *dst = (b as f32 - 128.0) / 128.0;
                n += 1;
            }
        }
        n as u32
    }))
    .unwrap_or(0)
}

/// Play a clip on the default Windows output device (RoR2 disables Unity audio).
/// Returns 1 if queued, 0 for an unknown clip or no output device.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_play_sound(clip: u32, volume: f32) -> i32 {
    catch_unwind(|| {
        if clip == 0 {
            return 0;
        }
        let pcm = weapons::DECODED.read().ok().and_then(|d| d.get(clip as usize - 1).cloned().flatten());
        match pcm {
            Some(p) => i32::from(audio::play(p, volume, 1.0)),
            None => 0,
        }
    })
    .unwrap_or(0)
}

/// Play any MW2 sound alias by name (e.g. `US_1mc_achieve_predator`, `mp_hit_alert`)
/// with MW2's variants and volume/pitch ranges.
/// Returns 1 if queued, 0 if unknown or no device, 2 if its streamed file isn't indexed yet.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_play_alias(name: *const u8, len: usize, volume: f32) -> i32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, |n| sounds::play(n, volume))
    })
    .unwrap_or(0)
}

/// MW2's engine perk bits for the player (IW4L `ps.perks[0]`): 0x2 Steady Aim (hip spread x0.65),
/// 0x4 Sleight of Hand (reload x2), 0x0100_0000 Lightweight (view bob x0.75), 0x0200_0000 Marathon
/// (unlimited sprint), others as IW4L reads them. Script-side perks are the host's.
///
/// # Safety
/// `sim` is a live handle from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_perks(sim: *mut Mw2Sim, perks0: u32) {
    if let Some(s) = unsafe { sim.as_mut() } {
        s.ps.perks[0] = perks0;
    }
}

/// 1 if an alias with this name exists in the loaded zones.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_alias_exists(name: *const u8, len: usize) -> i32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, |n| i32::from(sounds::exists(n)))
    })
    .unwrap_or(0)
}

/// Play one of a weapon's sounds the way MW2 does: a random variant of the alias list,
/// with that variant's random volume and pitch ranges. Returns 1 if queued.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_play_weapon_sound(weapon: u32, kind: u32, volume: f32) -> i32 {
    catch_unwind(|| {
        let Some(row) = weapons::row(weapon) else { return 0 };
        let Some(list) = row.variants.get(kind as usize) else { return 0 };
        if list.is_empty() {
            return 0;
        }
        let pick = ((audio::rand_range(0.0, 1.0) * list.len() as f32) as usize).min(list.len() - 1);
        let v = list[pick];
        let pcm = weapons::DECODED.read().ok().and_then(|d| d.get(v.clip as usize - 1).cloned().flatten());
        let Some(pcm) = pcm else { return 0 };
        let vol = volume * audio::rand_range(v.vol.0, v.vol.1.max(v.vol.0));
        let pitch = audio::rand_range(v.pitch.0, v.pitch.1.max(v.pitch.0));
        i32::from(audio::play(pcm, vol, if pitch > 0.0 { pitch } else { 1.0 }))
    })
    .unwrap_or(0)
}

/// Gun model id for a weapon (0 = none).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_model(weapon: u32) -> u32 {
    catch_unwind(|| weapons::row(weapon).map_or(0, |r| r.model)).unwrap_or(0)
}

/// A HUD material or image (e.g. `specialty_uav`, `minimap_background`) as RGBA8, bottom
/// row first. Writes width/height; copies the pixels when `cap` is large enough. Returns the
/// Copy MW2's `vision/<name>.vision` text into `out` (no NUL). Returns its length (0: none);
/// call with `out` null to size the buffer.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_vision(name: *const u8, len: usize, out: *mut u8, cap: u32) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        let Ok(name) = std::str::from_utf8(bytes) else { return 0 };
        let Some(text) = visions::get(name) else { return 0 };
        if !out.is_null() && cap as usize >= text.len() {
            unsafe { std::ptr::copy_nonoverlapping(text.as_ptr(), out, text.len()) };
        }
        text.len() as u32
    }))
    .unwrap_or(0)
}

/// byte size, or 0 if it can't be found or decoded.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes; `w`/`h` valid; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_image(name: *const u8, len: usize, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if name.is_null() || w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        let Ok(name) = std::str::from_utf8(bytes) else { return 0 };
        // Callers ask twice (size, then pixels): the last decode is kept for the second call.
        static LAST: std::sync::Mutex<Option<(String, u32, u32, Vec<u8>)>> = std::sync::Mutex::new(None);
        let mut last = LAST.lock().unwrap_or_else(|e| e.into_inner());
        if last.as_ref().is_none_or(|l| l.0 != name) {
            let Some((iw, ih, rgba)) = images::hud_rgba(name) else { return 0 };
            *last = Some((name.to_owned(), iw, ih, rgba));
        }
        let Some((_, iw, ih, rgba)) = last.as_ref() else { return 0 };
        let (iw, ih) = (*iw, *ih);
        unsafe {
            *w = iw;
            *h = ih;
            if !out.is_null() && cap as usize >= rgba.len() {
                std::ptr::copy_nonoverlapping(rgba.as_ptr(), out, rgba.len());
            }
        }
        rgba.len() as u32
    }))
    .unwrap_or(0)
}

/// Copy a weapon string into `out` (no NUL). Field 0: HUD icon material, 1: ADS overlay
/// (scope) material, 2: the weapon's name (`ak47_gl_mp`), 3 / 4: hip crosshair center / side
/// material, 5: third-person fire sound alias. Returns the length.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_string(weapon: u32, field: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(row) = weapons::row(weapon) else { return 0 };
        let s = match field {
            0 => row.hud_icon.clone().unwrap_or_default(),
            1 => row.overlay.clone().unwrap_or_default(),
            2 => row.name.clone(),
            3 => row.reticle_center.clone().unwrap_or_default(),
            4 => row.reticle_side.clone().unwrap_or_default(),
            5 => row.fire_world.clone().unwrap_or_default(),
            _ => return 0,
        };
        if !out.is_null() {
            let n = s.len().min(cap as usize);
            unsafe { std::ptr::copy_nonoverlapping(s.as_ptr(), out, n) };
        }
        s.len() as u32
    }))
    .unwrap_or(0)
}

/// What a weapon's ADS does to the view (all straight from the weapon def).
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct Mw2WeaponView {
    /// Field of view when fully aimed, in MW2's cg_fov degrees (cg_fov is 65).
    pub ads_zoom_fov: f32,
    /// ADS fraction where the zoom starts (in) / ends (out).
    pub ads_zoom_in_frac: f32,
    pub ads_zoom_out_frac: f32,
    /// Overlay size on MW2's 640x480 virtual screen.
    pub overlay_width: f32,
    pub overlay_height: f32,
    pub overlay_reticle: i32,
    pub has_overlay: i32,
    pub _pad: i32,
}

const _: () = assert!(size_of::<Mw2WeaponView>() == 32);

/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_view(weapon: u32, out: *mut Mw2WeaponView) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(row) = weapons::row(weapon) else { return 0 };
        unsafe { *out = row.view };
        1
    }))
    .unwrap_or(0)
}

/// MW2 font metrics: writes pixel height and glyph count, copies the glyph sheet material
/// name into `material` (up to `cap`). Returns 1 if the font exists.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes; the out pointers are valid; `material` holds `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_font_info(name: *const u8, len: usize, pixel_height: *mut i32, glyphs: *mut u32, material: *mut u8, cap: u32, material_len: *mut u32) -> i32 {
    if name.is_null() || pixel_height.is_null() || glyphs.is_null() || material_len.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        let Ok(name) = std::str::from_utf8(bytes) else { return 0 };
        fonts::with(name, |f| unsafe {
            *pixel_height = f.pixel_height;
            *glyphs = f.glyphs.len() as u32;
            *material_len = f.material.len() as u32;
            if !material.is_null() {
                let n = f.material.len().min(cap as usize);
                std::ptr::copy_nonoverlapping(f.material.as_ptr(), material, n);
            }
            1
        })
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct Mw2Glyph {
    pub letter: u32,
    pub x0: i32,
    pub y0: i32,
    pub dx: i32,
    pub width: i32,
    pub height: i32,
    pub s0: f32,
    pub t0: f32,
    pub s1: f32,
    pub t1: f32,
}

const _: () = assert!(size_of::<Mw2Glyph>() == 40);

/// Copy a font's glyphs (up to `cap`). Returns how many were written.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes; `out` has room for `cap` glyphs.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_font_glyphs(name: *const u8, len: usize, out: *mut Mw2Glyph, cap: u32) -> u32 {
    if name.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        let Ok(name) = std::str::from_utf8(bytes) else { return 0 };
        fonts::with(name, |f| {
            let n = f.glyphs.len().min(cap as usize);
            for (i, g) in f.glyphs.iter().take(n).enumerate() {
                unsafe {
                    *out.add(i) = Mw2Glyph {
                        letter: u32::from(g.letter),
                        x0: i32::from(g.x0),
                        y0: i32::from(g.y0),
                        dx: i32::from(g.dx),
                        width: i32::from(g.pixel_width),
                        height: i32::from(g.pixel_height),
                        s0: g.s0,
                        t0: g.t0,
                        s1: g.s1,
                        t1: g.t1,
                    };
                }
            }
            n as u32
        })
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// Scope sway and hold breath for one rendered frame.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct Mw2ViewSway {
    /// Degrees to add to the view (IW4: yaw + = left, pitch + = down).
    pub yaw: f32,
    pub pitch: f32,
    /// MW2's hold_breath_scale: 0 while holding, 1 normally, up to 4.5 after running out.
    pub breath_scale: f32,
    pub holding: i32,
    /// hold_breath_timer (ms): counts up while held, 4500 + 1000 when the breath ran out.
    pub breath_ms: i32,
    pub can_hold: i32,
}

const _: () = assert!(size_of::<Mw2ViewSway>() == 24);

/// MW2's scope sway: only with an overlay reticle, scaled by ADS fraction and hold breath.
/// Port of IW4L `weapon_iw4::view_bob::view_camera_idle` (Apache-2.0), standing, per frame.
///
/// # Safety
/// `sim` from `mw2_create`; `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_view_sway(sim: *mut Mw2Sim, frametime: f32, out: *mut Mw2ViewSway) -> i32 {
    if sim.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let mut r = Mw2ViewSway {
            breath_scale: sim.ps.hold_breath_scale,
            holding: i32::from(sim.ps.weap_flags & playerstate_iw4::weap_flags::HOLD_BREATH != 0),
            breath_ms: sim.ps.hold_breath_timer,
            ..Default::default()
        };
        let Some(armed) = sim.armed.as_ref() else {
            unsafe { *out = r };
            return 1;
        };
        r.can_hold = i32::from(armed.row.facts.can_hold_breath);
        let frac = sim.ps.f_weapon_pos_frac;
        let overlay = armed.row.view.overlay_reticle;
        if overlay != 0 && frac > 0.0 {
            let ps = weapon_iw4::WeaponPlacementPsInputs {
                weapon_pos_frac: frac,
                aim_down_sight: frac > 0.0,
                overlay_reticle: overlay,
                ..Default::default()
            };
            let (amount, speed) = weapon_iw4::weapon_idle_amount_speed(ps, armed.row.idle);
            let add = (speed * weapon_iw4::WEAPON_IDLE_TIME_MS_SCALE * frametime).round() as i32;
            sim.weap_idle_time = sim.weap_idle_time.wrapping_add(add);
            // Standing: the crouch/prone factor target is 1.0.
            let last = sim.view_last_idle_factor;
            if last != 1.0 {
                let step = frametime * weapon_iw4::WEAPON_IDLE_FACTOR_LERP;
                sim.view_last_idle_factor = if last > 1.0 { (last - step).max(1.0) } else { (last + step).min(1.0) };
            }
            let scale = sim.view_last_idle_factor * amount * frac * sim.ps.hold_breath_scale * weapon_iw4::WEAPON_IDLE_SIN_SCALE;
            let t = sim.weap_idle_time as f32;
            r.yaw = (t * weapon_iw4::WEAPON_IDLE_YAW_FREQ).sin() * scale;
            r.pitch = (t * weapon_iw4::WEAPON_IDLE_PITCH_FREQ).sin() * scale;
        }
        unsafe { *out = r };
        1
    }))
    .unwrap_or(0)
}

/// MW2's ammo counter style for the HUD (ammoCounterClip: 0 none, 1 magazine,
/// 2 short magazine, 3 shotgun, 4 rocket, 5 belt-fed, 6 alt weapon).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_ammo_counter(weapon: u32) -> i32 {
    catch_unwind(|| weapons::row(weapon).map_or(0, |r| r.ammo_counter)).unwrap_or(0)
}

/// MW2 score for an event (`kill`, `headshot`, `assist`, ...), team-based values from _rank.gsc.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_score(name: *const u8, len: usize) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, progression::score)
    })
    .unwrap_or(0)
}

/// XP a killstreak awards when used (killstreakTable column 13).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streak_xp(id: u32) -> u32 {
    catch_unwind(|| killstreaks::def(id).map_or(0, |d| d.xp)).unwrap_or(0)
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct Mw2Rank {
    pub id: u32,
    pub min_xp: u32,
    pub xp_to_next: u32,
    pub display: u32,
}

const _: () = assert!(size_of::<Mw2Rank>() == 16);

/// The MW2 rank for a total XP (mp/ranktable.csv). Copies the rank icon material into
/// `icon` (up to `cap`). Returns 1, or 0 if the table didn't load.
///
/// # Safety
/// `out` valid; `icon` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_rank_for_xp(xp: u32, out: *mut Mw2Rank, icon: *mut u8, cap: u32, icon_len: *mut u32) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(r) = progression::rank_for(xp) else { return 0 };
        unsafe {
            *out = Mw2Rank { id: r.id, min_xp: r.min_xp, xp_to_next: r.xp_to_next, display: r.display };
            if !icon.is_null() {
                let n = r.icon.len().min(cap as usize);
                std::ptr::copy_nonoverlapping(r.icon.as_ptr(), icon, n);
            }
            if !icon_len.is_null() {
                *icon_len = r.icon.len() as u32;
            }
        }
        1
    }))
    .unwrap_or(0)
}

/// Start loading every texture a weapon's viewmodel uses, on a background thread.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_prefetch_weapon(weapon: u32) -> u32 {
    catch_unwind(|| {
        let vm = mw2_viewmodel_build(weapon);
        if vm.is_null() {
            return 0;
        }
        let names: Vec<String> = unsafe { &*vm }.surfaces.iter().filter_map(|s| s.color_map.clone()).collect();
        unsafe { mw2_viewmodel_destroy(vm) };
        let n = names.len() as u32;
        models::prefetch(names);
        n
    })
    .unwrap_or(0)
}

/// Start loading a captured model's textures, or HUD materials/images, on a background thread.
/// `names` is newline-separated; each is a model name (its surface colour maps are loaded) or a
/// HUD material / image name.
///
/// # Safety
/// `names` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_prefetch(names: *const u8, len: usize) -> u32 {
    if names.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(names, len) };
        let Ok(text) = std::str::from_utf8(bytes) else { return 0 };
        let mut images = Vec::new();
        for n in text.lines().map(str::trim).filter(|n| !n.is_empty()) {
            let model = models::index_of(n);
            if model != 0 {
                models::with(model, |m| images.extend(m.surfaces.iter().filter_map(|s| s.color_map.clone())));
            } else {
                images.push(images::image_name(n));
            }
        }
        let k = images.len() as u32;
        models::prefetch(images);
        k
    })
    .unwrap_or(0)
}

/// Rotor parts of a captured helicopter model: per vertex (the model's mesh order) 0 = hull,
/// 1 = main rotor, 2 = tail rotor (by the vertex's bone name), and each rotor's pivot (its bone's
/// bind position, IW4 inches; main then tail). Returns the vertex count, or 0 without a rig.
///
/// # Safety
/// `classes` has room for `cap` bytes; `pivots` for 6 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_rotors(model: u32, classes: *mut u8, cap: u32, pivots: *mut f32) -> u32 {
    if classes.is_null() || pivots.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(name) = models::with(model, |m| m.name.clone()) else { return 0 };
        let rigs = viewmodel::RIGS.read().unwrap();
        let Some(rig) = rigs.iter().find(|r| r.name == name) else { return 0 };
        let sentry = rig.name.starts_with("sentry_minigun");
        let class_of = |bone: usize| -> u8 {
            let n = rig.pose.bone_names.get(bone).map(|s| s.to_ascii_lowercase()).unwrap_or_default();
            // The sentry: everything but the tripod (bi_*) and the root tags turns about tag_aim.
            if sentry {
                // j_spin: the minigun's barrel cluster, spun while it fires (class 9).
                if n == "j_spin" {
                    return 9;
                }
                return if n.starts_with("bi_") || ["tag_origin", "tag_dummy", "tag_pivot", "tag_aim_pivot"].contains(&n.as_str()) { 0 } else { 4 };
            }
            // 1 main rotor, 2 tail rotor, 3 turret (yaw), 4 barrel (pitch, child of the turret),
            // 5-8 propellers (the C-130's tag_prop_l_1 / l_2 / r_1 / r_2).
            if n.contains("rotor") { if n.contains("tail") { 2 } else { 1 } }
            else if n.starts_with("turret_animate") { 3 }
            else if n.starts_with("barrel_animate") { 4 }
            else if let Some(p) = n.strip_prefix("tag_prop_") { match p { "l_1" => 5, "l_2" => 6, "r_1" => 7, "r_2" => 8, _ => 0 } }
            else { 0 }
        };
        let piv = unsafe { std::slice::from_raw_parts_mut(pivots, 6) };
        piv.fill(0.0);
        for bone in 0..rig.pose.num_bones {
            let c = class_of(bone);
            // Pivots cover the two rotor classes only (turret pivots come from mw2_model_tag);
            // indexing them for classes 3/4 overran the 6-float buffer and the panic dropped the
            // whole rotor split for turreted helicopters.
            if (1..=2).contains(&c) && piv[(c as usize - 1) * 3..(c as usize) * 3].iter().all(|v| *v == 0.0) {
                if let Some((_, t)) = rig.pose.base_mat.get(bone) {
                    piv[(c as usize - 1) * 3..(c as usize) * 3].copy_from_slice(&t.to_array());
                }
            }
        }
        let n = rig.skin.len().min(cap as usize);
        let out = unsafe { std::slice::from_raw_parts_mut(classes, n) };
        for (i, sk) in rig.skin.iter().take(n).enumerate() {
            out[i] = class_of(sk.bones[0] as usize);
        }
        rig.skin.len() as u32
    }))
    .unwrap_or(0)
}

/// A tag's (bone's) base-pose position in model space (IW4 units), e.g. `tag_flash`,
/// `tag_engine_left`, `turret_animate_joint`. 1 found, 0 not.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes; `out` to 3 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_tag(model: u32, name: *const u8, len: usize, out: *mut f32) -> i32 {
    if name.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let want = std::str::from_utf8(unsafe { std::slice::from_raw_parts(name, len) }).unwrap_or("").to_ascii_lowercase();
        let Some(model_name) = models::with(model, |m| m.name.clone()) else { return 0 };
        let rigs = viewmodel::RIGS.read().unwrap();
        let Some(rig) = rigs.iter().find(|r| r.name == model_name) else { return 0 };
        let Some(bone) = rig.pose.bone_names.iter().position(|b| b.to_ascii_lowercase() == want) else { return 0 };
        let Some((_, t)) = rig.pose.base_mat.get(bone) else { return 0 };
        unsafe { std::ptr::copy_nonoverlapping(t.to_array().as_ptr(), out, 3) };
        1
    }))
    .unwrap_or(0)
}

/// A tag's base-pose frame in model space (IW4): position, then its X (forward) and Z (up) axes,
/// 9 floats. playFXOnTag aims an effect along the tag's X. 1 found, 0 not.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes; `out` to 9 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_tag_frame(model: u32, name: *const u8, len: usize, out: *mut f32) -> i32 {
    if name.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let want = std::str::from_utf8(unsafe { std::slice::from_raw_parts(name, len) }).unwrap_or("").to_ascii_lowercase();
        let Some(model_name) = models::with(model, |m| m.name.clone()) else { return 0 };
        let rigs = viewmodel::RIGS.read().unwrap();
        let Some(rig) = rigs.iter().find(|r| r.name == model_name) else { return 0 };
        let Some(bone) = rig.pose.bone_names.iter().position(|b| b.to_ascii_lowercase() == want) else { return 0 };
        let Some((q, t)) = rig.pose.base_mat.get(bone) else { return 0 };
        let fwd = q.normalize() * glam::Vec3::X;
        let up = q.normalize() * glam::Vec3::Z;
        let v = [t.x, t.y, t.z, fwd.x, fwd.y, fwd.z, up.x, up.y, up.z];
        unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), out, 9) };
        1
    }))
    .unwrap_or(0)
}

/// Drain the movement events queued since the last call into `out` as (event, parm) pairs.
///
/// # Safety
/// `sim` from `mw2_create`; `out` has room for `cap` pairs.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_move_events(sim: *mut Mw2Sim, out: *mut [i32; 2], cap: u32) -> u32 {
    if sim.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let s = unsafe { &mut *sim };
        let n = s.move_events.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(s.move_events.as_ptr(), out, n) };
        s.move_events.drain(..n);
        n as u32
    }))
    .unwrap_or(0)
}

/// Model id for a captured XModel name (e.g. `com_plasticcase_friendly`), or 0.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_index(name: *const u8, len: usize) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, models::index_of)
    })
    .unwrap_or(0)
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2ModelInfo {
    pub vertex_count: u32,
    pub index_count: u32,
    pub surface_count: u32,
    pub _pad: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2SurfaceInfo {
    pub index_start: u32,
    pub index_count: u32,
    /// 0 none, 1 BGRA8, 11 DXT1, 13 DXT5 (others are reported but not passed through).
    pub texture_format: u32,
    pub texture_width: u32,
    pub texture_height: u32,
    pub texture_bytes: u32,
}

const _: () = assert!(size_of::<Mw2ModelInfo>() == 16);
const _: () = assert!(size_of::<Mw2SurfaceInfo>() == 24);

/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_info(model: u32, out: *mut Mw2ModelInfo) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        models::with(model, |m| unsafe {
            *out = Mw2ModelInfo {
                vertex_count: m.positions.len() as u32,
                index_count: m.indices.len() as u32,
                surface_count: m.surfaces.len() as u32,
                _pad: 0,
            };
        })
        .map_or(0, |_| 1)
    }))
    .unwrap_or(0)
}

/// Copy the mesh out: `positions`/`normals` hold 3 floats per vertex, `uvs` 2 per vertex
/// (IW4 model space: inches, Z up, X forward); `indices` u32 triangle list (IW4 winding).
///
/// # Safety
/// Buffers sized from `mw2_model_info`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_mesh(model: u32, positions: *mut f32, normals: *mut f32, uvs: *mut f32, indices: *mut u32) -> i32 {
    if positions.is_null() || normals.is_null() || uvs.is_null() || indices.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        models::with(model, |m| unsafe {
            let n = m.positions.len();
            let pos = std::slice::from_raw_parts_mut(positions, n * 3);
            let nor = std::slice::from_raw_parts_mut(normals, n * 3);
            let uv = std::slice::from_raw_parts_mut(uvs, n * 2);
            for i in 0..n {
                pos[i * 3..i * 3 + 3].copy_from_slice(&m.positions[i]);
                nor[i * 3..i * 3 + 3].copy_from_slice(&m.normals[i]);
                uv[i * 2..i * 2 + 2].copy_from_slice(&m.uvs[i]);
            }
            std::slice::from_raw_parts_mut(indices, m.indices.len()).copy_from_slice(&m.indices);
        })
        .map_or(0, |_| 1)
    }))
    .unwrap_or(0)
}

/// Surface ranges plus its colorMap's format/size (reads the texture's header from the iwd).
///
/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_surface(model: u32, surface: u32, out: *mut Mw2SurfaceInfo) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(s) = models::with(model, |m| m.surfaces.get(surface as usize).cloned()).flatten() else { return 0 };
        let tex = s.color_map.as_deref().and_then(models::texture);
        unsafe {
            *out = Mw2SurfaceInfo {
                index_start: s.index_start,
                index_count: s.index_count,
                texture_format: tex.as_ref().map_or(0, |t| u32::from(t.format)),
                texture_width: tex.as_ref().map_or(0, |t| t.width),
                texture_height: tex.as_ref().map_or(0, |t| t.height),
                texture_bytes: tex.as_ref().map_or(0, |t| t.top_mip.len() as u32),
            };
        }
        1
    }))
    .unwrap_or(0)
}

/// A model surface's material name (written to `name_out`, truncated) and MW2 blend mode:
/// 0 opaque, 1 alpha-test, 2 blend, 3 additive, 4 multiply, 5 screen; -1 = bad model/surface.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_surface_material(model: u32, surface: u32, name_out: *mut u8, cap: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(s) = models::with(model, |m| m.surfaces.get(surface as usize).cloned()).flatten() else { return -1 };
        if !name_out.is_null() && cap > 0 {
            let name = s.material.as_deref().unwrap_or("").as_bytes();
            let n = name.len().min(cap as usize - 1);
            unsafe {
                std::ptr::copy_nonoverlapping(name.as_ptr(), name_out, n);
                *name_out.add(n) = 0;
            }
        }
        // No captured state: an ordinary opaque surface (fx's default of "blend" is for FX).
        if s.state_bits.is_some() { fx::blend_mode(s.state_bits) } else { 0 }
    }))
    .unwrap_or(-1)
}

/// A model surface's colour image as RGBA8 (bottom row first, like `mw2_hud_image`), decoded
/// natively for every IWI format (DXT3, wavelet, luminance...). Query with `out` null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_surface_rgba(model: u32, surface: u32, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(s) = models::with(model, |m| m.surfaces.get(surface as usize).cloned()).flatten() else { return 0 };
        let Some(rgba) = s.color_map.as_deref().and_then(fx::texture_rgba) else { return 0 };
        let (iw, ih, px) = &*rgba;
        unsafe {
            *w = *iw;
            *h = *ih;
            if !out.is_null() && cap as usize >= px.len() {
                std::ptr::copy_nonoverlapping(px.as_ptr(), out, px.len());
            }
        }
        px.len() as u32
    }))
    .unwrap_or(0)
}

/// 1 if this surface of the weapon's model should be drawn for this weapon variant
/// (attachments not on the weapon are hidden, as MW2 does with hide-tags).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_surface_visible(weapon: u32, surface: u32) -> i32 {
    catch_unwind(|| {
        let Some(row) = weapons::row(weapon) else { return 0 };
        models::with(row.model, |m| {
            m.surfaces.get(surface as usize).map_or(0, |s| i32::from(models::surface_visible(&row.name, s.material.as_deref())))
        })
        .unwrap_or(0)
    })
    .unwrap_or(0)
}

/// Copy a surface's colorMap top mip (raw IWI pixel data, D3D row order) into `out`.
///
/// # Safety
/// `out` has room for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_model_surface_texture(model: u32, surface: u32, out: *mut u8, cap: u32) -> u32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(name) = models::with(model, |m| m.surfaces.get(surface as usize).and_then(|s| s.color_map.clone())).flatten() else { return 0 };
        let Some(t) = models::texture(&name) else { return 0 };
        let n = t.top_mip.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(t.top_mip.as_ptr(), out, n) };
        n as u32
    }))
    .unwrap_or(0)
}

/// The left hand's viewmodel of an akimbo weapon (its left-handed anims); null if the weapon
/// isn't akimbo. Step it with `mw2_viewmodel_step` like the right one.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_viewmodel_build_left(weapon: u32) -> *mut viewmodel::Viewmodel {
    catch_unwind(|| viewmodel::Viewmodel::build_hand(weapon, 1).map_or(std::ptr::null_mut(), |v| Box::into_raw(Box::new(v)))).unwrap_or(std::ptr::null_mut())
}

/// Build the full first-person viewmodel (viewhands + this weapon's gun, rigged on
/// `tag_weapon`, with its MW2 animations). Null if the weapon has no viewmodel rig.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_viewmodel_build(weapon: u32) -> *mut viewmodel::Viewmodel {
    catch_unwind(|| viewmodel::Viewmodel::build(weapon).map_or(std::ptr::null_mut(), |v| Box::into_raw(Box::new(v)))).unwrap_or(std::ptr::null_mut())
}

/// Drain the sound aliases the viewmodel's anim notetracks fired (reload clip out / in, bolt,
/// raise...), newline-separated into `out`. Returns bytes written.
///
/// # Safety
/// `vm` valid; `out` has room for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_sounds(vm: *mut viewmodel::Viewmodel, out: *mut u8, cap: u32) -> u32 {
    if vm.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &mut *vm };
        let mut n = 0usize;
        let mut taken = 0;
        for name in &v.notetrack_sounds {
            let need = name.len() + 1;
            if n + need > cap as usize {
                break;
            }
            unsafe {
                std::ptr::copy_nonoverlapping(name.as_ptr(), out.add(n), name.len());
                *out.add(n + name.len()) = 10; // '\n'
            }
            n += need;
            taken += 1;
        }
        v.notetrack_sounds.drain(..taken);
        n as u32
    }))
    .unwrap_or(0)
}

/// `mw2_viewmodel_build` for `weapon`'s anims with `look`'s gun model and hide tags (an
/// alternate GL / shotgun shown on its parent rifle).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_viewmodel_build_as(weapon: u32, look: u32) -> *mut viewmodel::Viewmodel {
    catch_unwind(|| viewmodel::Viewmodel::build_as(weapon, look).map_or(std::ptr::null_mut(), |v| Box::into_raw(Box::new(v)))).unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `vm` from `mw2_viewmodel_build`, not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_destroy(vm: *mut viewmodel::Viewmodel) {
    if !vm.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(vm) })));
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2ViewmodelInfo {
    pub bone_count: u32,
    pub vertex_count: u32,
    pub index_count: u32,
    pub surface_count: u32,
}
const _: () = assert!(size_of::<Mw2ViewmodelInfo>() == 16);

/// # Safety
/// `vm` valid, `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_info(vm: *const viewmodel::Viewmodel, out: *mut Mw2ViewmodelInfo) -> i32 {
    if vm.is_null() || out.is_null() {
        return 0;
    }
    let v = unsafe { &*vm };
    unsafe {
        *out = Mw2ViewmodelInfo {
            bone_count: v.bone_count() as u32,
            vertex_count: v.positions.len() as u32,
            index_count: v.indices.len() as u32,
            surface_count: v.surfaces.len() as u32,
        };
    }
    1
}

/// Unity-space mesh: positions/normals 3 floats, uvs 2, bone indices 4 i32 + weights
/// 4 f32 per vertex, u32 triangles (winding already flipped), bindposes 16 floats
/// (column-major) per bone. Buffers sized from `mw2_viewmodel_info`.
///
/// # Safety
/// All buffers valid for the sizes above.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_mesh(
    vm: *const viewmodel::Viewmodel,
    positions: *mut f32,
    normals: *mut f32,
    uvs: *mut f32,
    bone_index: *mut i32,
    bone_weight: *mut f32,
    indices: *mut u32,
    bindposes: *mut f32,
) -> i32 {
    if vm.is_null() || positions.is_null() || normals.is_null() || uvs.is_null() || bone_index.is_null() || bone_weight.is_null() || indices.is_null() || bindposes.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let n = v.positions.len();
        unsafe {
            std::ptr::copy_nonoverlapping(v.positions.as_ptr().cast::<f32>(), positions, n * 3);
            std::ptr::copy_nonoverlapping(v.normals.as_ptr().cast::<f32>(), normals, n * 3);
            std::ptr::copy_nonoverlapping(v.uvs.as_ptr().cast::<f32>(), uvs, n * 2);
            std::ptr::copy_nonoverlapping(v.bone_index.as_ptr().cast::<i32>(), bone_index, n * 4);
            std::ptr::copy_nonoverlapping(v.bone_weight.as_ptr().cast::<f32>(), bone_weight, n * 4);
            std::ptr::copy_nonoverlapping(v.indices.as_ptr(), indices, v.indices.len());
            std::ptr::copy_nonoverlapping(v.bindposes.as_ptr().cast::<f32>(), bindposes, v.bindposes.len() * 16);
        }
        1
    }))
    .unwrap_or(0)
}

/// Surface range + colorMap texture info (same struct as `mw2_model_surface`).
///
/// # Safety
/// `vm`, `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_surface(vm: *const viewmodel::Viewmodel, surface: u32, out: *mut Mw2SurfaceInfo) -> i32 {
    if vm.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let Some(s) = v.surfaces.get(surface as usize) else { return 0 };
        let tex = s.color_map.as_deref().and_then(models::texture);
        unsafe {
            *out = Mw2SurfaceInfo {
                index_start: s.index_start,
                index_count: s.index_count,
                texture_format: tex.as_ref().map_or(0, |t| u32::from(t.format)),
                texture_width: tex.as_ref().map_or(0, |t| t.width),
                texture_height: tex.as_ref().map_or(0, |t| t.height),
                texture_bytes: tex.as_ref().map_or(0, |t| t.top_mip.len() as u32),
            };
        }
        1
    }))
    .unwrap_or(0)
}

/// A viewmodel surface's colour map image name into `out` (no NUL); its length, 0 if none. The
/// heartbeat sensor's screen is the surface drawing `motion_tracker_screen_col`.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_surface_color_map(vm: *const viewmodel::Viewmodel, surface: u32, out: *mut u8, cap: u32) -> u32 {
    if vm.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let Some(name) = v.surfaces.get(surface as usize).and_then(|s| s.color_map.as_deref()) else { return 0 };
        if !out.is_null() && cap as usize >= name.len() {
            unsafe { std::ptr::copy_nonoverlapping(name.as_ptr(), out, name.len()) };
        }
        name.len() as u32
    }))
    .unwrap_or(0)
}

/// A viewmodel surface's MW2 blend mode (0 opaque, 2 blend, 3 additive, 4 multiply, 5 screen):
/// sight lenses and reticles are see-through.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_surface_blend(vm: *const viewmodel::Viewmodel, surface: u32) -> i32 {
    if vm.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe { &*vm }.surfaces.get(surface as usize).map_or(0, |s| s.blend))).unwrap_or(0)
}

/// # Safety
/// `vm` valid; `out` has room for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_surface_texture(vm: *const viewmodel::Viewmodel, surface: u32, out: *mut u8, cap: u32) -> u32 {
    if vm.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let Some(name) = v.surfaces.get(surface as usize).and_then(|s| s.color_map.clone()) else { return 0 };
        let Some(t) = models::texture(&name) else { return 0 };
        let n = t.top_mip.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(t.top_mip.as_ptr(), out, n) };
        n as u32
    }))
    .unwrap_or(0)
}

/// A viewmodel surface's colour image as RGBA8 (bottom row first), decoded natively for every IWI
/// format: the fallback for formats Unity can't load compressed (DXT3: the Stinger's). Query with
/// `out` null.
///
/// # Safety
/// `vm` valid; `w` / `h` valid; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_surface_rgba(vm: *const viewmodel::Viewmodel, surface: u32, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if vm.is_null() || w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let Some(rgba) = v.surfaces.get(surface as usize).and_then(|s| s.color_map.as_deref()).and_then(fx::texture_rgba) else { return 0 };
        let (iw, ih, px) = &*rgba;
        unsafe {
            *w = *iw;
            *h = *ih;
            if !out.is_null() && cap as usize >= px.len() {
                std::ptr::copy_nonoverlapping(px.as_ptr(), out, px.len());
            }
        }
        px.len() as u32
    }))
    .unwrap_or(0)
}

/// An opaque viewmodel surface's colour map with its paint laid on (`images::painted_rgba`), RGBA8
/// in the compressed texture's row order. Returns the byte count (`w * h * 4`; 0 = the surface has
/// no paint, or it can't be decoded: use `mw2_viewmodel_surface_texture`).
///
/// # Safety
/// `vm` valid; `w` / `h` valid; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_surface_painted(vm: *const viewmodel::Viewmodel, surface: u32, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if vm.is_null() || w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let Some(s) = v.surfaces.get(surface as usize) else { return 0 };
        let (Some(color), Some(paint)) = (s.color_map.as_deref(), s.detail_map.as_deref()) else { return 0 };
        if s.blend != 0 {
            return 0;
        }
        let Some((iw, ih, px)) = images::painted_rgba(color, paint) else { return 0 };
        unsafe {
            *w = iw;
            *h = ih;
            if !out.is_null() && cap as usize >= px.len() {
                std::ptr::copy_nonoverlapping(px.as_ptr(), out, px.len());
            }
        }
        px.len() as u32
    }))
    .unwrap_or(0)
}

/// Advance the viewmodel animation by `dt` seconds from the sim's current weapon state
/// (events since the last call, weapon state, ADS fraction) and write the pose: per bone
/// 7 floats (Unity-space position xyz relative to the eye, rotation xyzw).
///
/// # Safety
/// `vm` and `sim` valid; `out` has room for 7 * bone_count floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_step(vm: *mut viewmodel::Viewmodel, sim: *mut Mw2Sim, dt: f32, out: *mut f32) -> i32 {
    if vm.is_null() || sim.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &mut *vm };
        let sim = unsafe { &mut *sim };
        // The left viewmodel (akimbo) follows the left hand: its anims, its clip.
        let left = v.hand == 1;
        let clip = sim.armed.as_ref().map_or(0, |a| if left { a.left.as_ref().map_or(0, |l| l.clip) } else { a.hand.clip });
        let (anim, seq) = if left { (sim.vm_anim_left, sim.vm_anim_seq_left) } else { (sim.vm_anim, sim.vm_anim_seq) };
        let ads = sim.ps.f_weapon_pos_frac;
        let fast = sim.armed.as_ref().is_some_and(|a| weapon_iw4::perk_fastreload_eligible(sim.ps.perks[0], a.row.facts.inherits_perks));
        let reload_scale = if fast { 1.0 / weapon_iw4::PERK_WEAP_RELOAD_MULTIPLIER_DEFAULT } else { 1.0 };
        v.advance(dt.clamp(0.0, 0.25), anim, seq, sim.fire_rate, reload_scale);
        let out = unsafe { std::slice::from_raw_parts_mut(out, v.bone_count() * 7) };
        v.pose(ads, clip, out);
        // IW4 (cg dual wield): the right gun shifted right by dualWieldViewModelOffset, the left
        // one left by it.
        if v.dual_offset != 0.0 {
            let dx = v.dual_offset * viewmodel::INCHES_TO_METRES * if left { -1.0 } else { 1.0 };
            for b in out.chunks_exact_mut(7) {
                b[0] += dx;
            }
        }
        // After the akimbo shift: the old pose it glides from had it too.
        v.apply_swap_blend(out, dt.clamp(0.0, 0.25));
        v.last_pose.clear();
        v.last_pose.extend_from_slice(out);
        1
    }))
    .unwrap_or(0)
}

/// One player's killstreak state for the HUD.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct Mw2StreakState {
    /// Kills this life.
    pub count: u32,
    pub stack_len: u32,
    /// Earned, unused streak ids, newest first.
    pub stack: [u32; 8],
    pub loadout_len: u32,
    /// Loadout streak ids, ascending by kills.
    pub loadout: [u32; 8],
}

const _: () = assert!(size_of::<Mw2StreakState>() == 76);

/// Number of killstreaks in MW2's table (0 if it didn't load).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streak_table_count() -> u32 {
    catch_unwind(|| killstreaks::with_table(|t| t.streaks.len() as u32).unwrap_or(0)).unwrap_or(0)
}

/// Streak id for a table name such as `predator_missile`, or 0.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_id(name: *const u8, len: usize) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, killstreaks::id_of)
    })
    .unwrap_or(0)
}

/// Kills a streak needs (MW2's count times the kill scale), or 0 for an unknown id.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streak_kills(id: u32) -> u32 {
    catch_unwind(|| killstreaks::def(id).map_or(0, |d| killstreaks::scaled_kills(d.kills))).unwrap_or(0)
}

/// The kill count (this life) at which this player's `id` comes on the current lap; 0 if unknown.
///
/// # Safety
/// `ks` from `mw2_streaks_create` (or null).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_next_kills(ks: *const killstreaks::Streaks, id: u32) -> u32 {
    if ks.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let k = unsafe { &*ks };
        if killstreaks::def(id).is_some() { k.kills_for(id) } else { 0 }
    }))
    .unwrap_or(0)
}

/// Each lap of the loadout multiplies its kill gaps by this (>= 1).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streaks_set_lap_growth(growth: f32) {
    let _ = catch_unwind(|| killstreaks::set_lap_growth(growth));
}

/// Start an MW2 sound alias looping (a vehicle's playLoopSound). Returns its id, 0 on failure.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_loop_start(name: *const u8, len: usize, volume: f32) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        std::str::from_utf8(bytes).map_or(0, |n| sounds::loop_start(n, volume))
    })
    .unwrap_or(0)
}

#[cfg(feature = "dev")]
/// Start the recorder's tape: every MW2 sound from here on is also mixed on the recording's clock.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_tape_start() {
    let _ = catch_unwind(audio::tape_start);
}

#[cfg(feature = "dev")]
/// One recorded frame of `dt` seconds went by (the tape's loops play through it).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_tape_advance(dt: f32) {
    let _ = catch_unwind(|| audio::tape_advance(dt));
}

#[cfg(feature = "dev")]
/// End the tape and write it as a WAV (UTF-8 path); its length in seconds, -1 on failure.
///
/// # Safety
/// `path` points at `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_tape_stop(path: *const u8, len: usize) -> f32 {
    if path.is_null() {
        return -1.0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(path, len) };
        std::str::from_utf8(bytes).map_or(-1.0, audio::tape_stop)
    })
    .unwrap_or(-1.0)
}

/// Set a loop's volume (the host follows the vehicle's distance with it).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_loop_volume(id: u32, volume: f32) {
    let _ = catch_unwind(|| audio::loop_volume(id, volume));
}

#[unsafe(no_mangle)]
pub extern "C" fn mw2_loop_stop(id: u32) {
    let _ = catch_unwind(|| audio::loop_stop(id));
}

/// Multiply every streak's kill count (RoR2's enemy numbers); 1 = MW2's.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streaks_set_kill_scale(scale: f32) {
    let _ = catch_unwind(|| killstreaks::set_kill_scale(scale));
}

/// Copy one of a streak's strings into `out` (no NUL). Field: 0 name, 1 earn sound alias,
/// 2 earn dialog key, 3 use dialog key, 4 weapon, 5 HUD icon, 6 crate icon, 7 HUD dpad icon.
/// Returns the full length.
///
/// # Safety
/// `out` has room for `cap` bytes (may be null when `cap` is 0).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_string(id: u32, field: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(d) = killstreaks::def(id) else { return 0 };
        let s = match field {
            0 => d.name,
            1 => d.earn_sound,
            2 => d.earn_dialog,
            3 => d.use_dialog,
            4 => d.weapon,
            5 => d.icon,
            6 => d.crate_icon,
            7 => d.dpad,
            _ => return 0,
        };
        if !out.is_null() {
            let n = s.len().min(cap as usize);
            unsafe { std::ptr::copy_nonoverlapping(s.as_ptr(), out, n) };
        }
        s.len() as u32
    }))
    .unwrap_or(0)
}

/// A player's killstreak state. It lives apart from the sim so it survives RoR2 bodies
/// (stages, revives): a new body is a new life, unused streaks are kept.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streaks_create() -> *mut killstreaks::Streaks {
    catch_unwind(|| Box::into_raw(Box::new(killstreaks::Streaks::default()))).unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `ks` from `mw2_streaks_create`, not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streaks_destroy(ks: *mut killstreaks::Streaks) {
    if !ks.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(ks) })));
    }
}

/// Hardline on or off: every streak needs one kill fewer.
///
/// # Safety
/// `ks` from `mw2_streaks_create` or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streaks_set_hardline(ks: *mut killstreaks::Streaks, on: i32) {
    if let Some(ks) = unsafe { ks.as_mut() } {
        ks.hardline = on != 0;
    }
}

/// Set the player's three (up to 8) streaks by id.
///
/// # Safety
/// `ks` from `mw2_streaks_create`; `ids` points to `n` ids.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_set_loadout(ks: *mut killstreaks::Streaks, ids: *const u32, n: u32) -> u32 {
    if ks.is_null() || (ids.is_null() && n > 0) {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ks = unsafe { &mut *ks };
        let ids = if n == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(ids, n as usize) } };
        ks.set_loadout(ids);
        ks.loadout().len() as u32
    }))
    .unwrap_or(0)
}

/// A kill by the player (any weapon, streaks included). Writes the streaks it earned
/// into `earned` (up to `cap`) and returns how many.
///
/// # Safety
/// `ks` from `mw2_streaks_create`; `earned` has room for `cap` ids.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_kill(ks: *mut killstreaks::Streaks, earned: *mut u32, cap: u32) -> u32 {
    if ks.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ks = unsafe { &mut *ks };
        let got = ks.kill();
        if !earned.is_null() {
            for (i, id) in got.iter().take(cap as usize).enumerate() {
                unsafe { *earned.add(i) = *id };
            }
        }
        got.len() as u32
    }))
    .unwrap_or(0)
}

/// A new life: kill count and earn progress reset; unused streaks are kept.
///
/// # Safety
/// `ks` from `mw2_streaks_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_new_life(ks: *mut killstreaks::Streaks) {
    if ks.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe { &mut *ks }.new_life()));
}

/// Put a streak on top of the stack (Care Package contents).
///
/// # Safety
/// `ks` from `mw2_streaks_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_give(ks: *mut killstreaks::Streaks, id: u32) -> i32 {
    if ks.is_null() || killstreaks::def(id).is_none() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        unsafe { &mut *ks }.give(id);
        1
    }))
    .unwrap_or(0)
}

/// Use the newest streak: removes it and returns its id (0 if none). The host checks it
/// can run the streak first (via `mw2_streak_state`).
///
/// # Safety
/// `ks` from `mw2_streaks_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_take(ks: *mut killstreaks::Streaks) -> u32 {
    if ks.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe { &mut *ks }.take().unwrap_or(0))).unwrap_or(0)
}

/// # Safety
/// `ks` from `mw2_streaks_create`; `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_streak_state(ks: *const killstreaks::Streaks, out: *mut Mw2StreakState) -> i32 {
    if ks.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let s = unsafe { &*ks };
        let mut st = Mw2StreakState { count: s.count, ..Default::default() };
        for (i, id) in s.stack.iter().take(8).enumerate() {
            st.stack[i] = *id;
        }
        st.stack_len = s.stack.len().min(8) as u32;
        for (i, id) in s.loadout().iter().take(8).enumerate() {
            st.loadout[i] = *id;
        }
        st.loadout_len = s.loadout().len().min(8) as u32;
        unsafe { *out = st };
        1
    }))
    .unwrap_or(0)
}

/// Roll an Emergency Airdrop crate (`airdrop_mega` weights). 0 = ammo.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streak_roll_mega(roll: u32) -> u32 {
    catch_unwind(|| killstreaks::roll_drop(true, roll).map_or(0, |n| killstreaks::id_of(&n))).unwrap_or(0)
}

/// Roll a Care Package by MW2's crate weights. Returns the streak id, or 0 for MW2's
/// "ammo" crate (or if the weights didn't load).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_streak_roll_airdrop(roll: u32) -> u32 {
    catch_unwind(|| killstreaks::roll_airdrop(roll).map_or(0, |n| killstreaks::id_of(&n))).unwrap_or(0)
}


/// Weapon anim audit (run: cargo test --release -p mw2sim anim_audit -- --ignored --nocapture).
/// Plays a routine through the real sim + viewmodel for every base MP gun and reports which
/// clips MW2 asked for, what played, and where the viewmodel froze on a finished clip.
#[cfg(test)]
mod build_bench {
    use super::*;
    #[test]
    #[ignore]
    fn viewmodel_build_cost() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        // Let the background iwd image index finish so it isn't counted.
        let _ = models::texture("specialty_uav");
        for name in ["ak47_mp", "m4_mp", "spas12_mp", "cheytac_mp", "ak47_mp"] {
            let idx = unsafe { mw2_weapon_index(name.as_ptr(), name.len()) };
            let t0 = std::time::Instant::now();
            let vm = mw2_viewmodel_build(idx);
            let t_build = t0.elapsed();
            let v = unsafe { &*vm };
            let t1 = std::time::Instant::now();
            let mut bytes = 0usize;
            for s in &v.surfaces {
                if let Some(t) = s.color_map.as_deref().and_then(models::texture) { bytes += t.top_mip.len(); }
            }
            let t_tex = t1.elapsed();
            eprintln!("{name}: build {t_build:?}, {} surface textures {:.1} MB in {t_tex:?}", v.surfaces.len(), bytes as f32 / 1e6);
            unsafe { mw2_viewmodel_destroy(vm) };
        }
    }
}

#[cfg(test)]
mod streak_weapons {
    use super::*;
    #[test]
    #[ignore]
    fn streak_weapon_viewmodels() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        for id in 1..=16u32 {
            let mut buf = [0u8; 96];
            let n = unsafe { mw2_streak_string(id, 4, buf.as_mut_ptr(), 96) } as usize;
            let w = String::from_utf8_lossy(&buf[..n.min(96)]).to_string();
            let idx = unsafe { mw2_weapon_index(w.as_ptr(), w.len()) };
            let row = weapons::row(idx);
            let vm = mw2_viewmodel_build(idx);
            let clips: Vec<String> = if vm.is_null() { vec![] } else {
                let v = unsafe { &*vm };
                (0..37).filter(|&s| v.playing_slot_exists(s)).map(|s| format!("{s}")).collect()
            };
            eprintln!("streak {id:2} weapon {w:34} idx {idx:4} gun {:?} vm {} slots {:?} raise {:?} fire {:?}", row.as_ref().and_then(|r| r.gun_model.clone()), !vm.is_null(), clips,
                row.as_ref().map(|r| r.timers.raise_ms), row.as_ref().map(|r| r.facts.fire_time_ms));
            if !vm.is_null() { unsafe { mw2_viewmodel_destroy(vm) }; }
        }
    }
}

#[cfg(test)]
mod anim_audit {
    use super::*;

    const SLOT_NAMES: [&str; 37] = [
        "root", "idle", "empty_idle", "fire", "hold_fire", "lastshot", "rechamber", "melee", "melee_charge", "reload",
        "reload_empty", "reload_start", "reload_end", "raise", "first_raise", "breach_raise", "drop", "alt_raise", "alt_drop",
        "quick_raise", "quick_drop", "empty_raise", "empty_drop", "sprint_in", "sprint_loop", "sprint_out", "stunned_start",
        "stunned_loop", "stunned_end", "detonate", "nv_wear", "nv_remove", "ads_fire", "ads_lastshot", "ads_rechamber", "ads_up", "ads_down",
    ];

    extern "C" fn floor(_user: *mut c_void, start: *const [f32; 3], end: *const [f32; 3], mins: *const [f32; 3], _maxs: *const [f32; 3], _mask: u32, out: *mut Mw2Trace) {
        let (start, end, mins) = unsafe { (*start, *end, *mins) };
        let out = unsafe { &mut *out };
        let (b0, b1) = (start[2] + mins[2], end[2] + mins[2]);
        if b0 < 0.0 {
            *out = Mw2Trace { fraction: 0.0, normal: [0.0, 0.0, 1.0], endpos: start, startsolid: 1, walkable: 1, ..Default::default() };
        } else if b1 < 0.125 && b1 < b0 {
            let f = ((b0 - 0.125) / (b0 - b1)).clamp(0.0, 1.0);
            let mut endpos = start;
            for a in 0..3 {
                endpos[a] += (end[a] - start[a]) * f;
            }
            *out = Mw2Trace { fraction: f, normal: [0.0, 0.0, 1.0], endpos, walkable: 1, ..Default::default() };
        }
    }

    /// Akimbo (MW2 dual wield): two clips, the left gun on ATTACK and the right on THROW (IW4's
    /// last_weapon_hand = 1), shots tagged by hand, both viewmodels build and animate.
    /// `cargo test --release -p mw2sim akimbo_hands -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn akimbo_hands() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        if !std::path::Path::new(COMMON_MP).exists() {
            return;
        }
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let akimbos: Vec<String> = weapons::TABLE.read().unwrap().iter().filter(|r| r.akimbo).map(|r| r.name.clone()).collect();
        eprintln!("akimbo rows ({}): {}", akimbos.len(), akimbos.join(" "));
        assert!(akimbos.iter().any(|n| n == "usp_akimbo_mp"), "usp_akimbo_mp should be akimbo");
        let name = "usp_akimbo_mp";
        let idx = unsafe { mw2_weapon_index(name.as_ptr(), name.len()) };
        let row = weapons::row(idx).unwrap();
        eprintln!("{name}: dual offset {} in", row.dual_offset);
        let right = mw2_viewmodel_build(idx);
        let left = mw2_viewmodel_build_left(idx);
        assert!(!right.is_null() && !left.is_null(), "both akimbo viewmodels build");
        let sim = unsafe { mw2_create(&[0.0, 0.0, 16.0]) };
        unsafe { mw2_give_weapon(sim, idx) };
        let mut st = Mw2State::default();
        let (rv, lv) = unsafe { (&mut *right, &mut *left) };
        let mut rpose = vec![0f32; rv.bone_count() * 7];
        let mut lpose = vec![0f32; lv.bone_count() * 7];
        let mut fired = [0u32; 2];
        let mut left_slots = std::collections::BTreeSet::new();
        let b = playerstate_iw4::buttons::ATTACK;
        let t = playerstate_iw4::buttons::THROW;
        // raise, tap left x3, tap right x3, both x2
        let mut script: Vec<(f32, u32)> = vec![(1.6, 0)];
        for _ in 0..3 { script.push((0.05, b)); script.push((0.4, 0)); }
        for _ in 0..3 { script.push((0.05, t)); script.push((0.4, 0)); }
        for _ in 0..2 { script.push((0.05, b | t)); script.push((0.4, 0)); }
        for (secs, buttons) in script {
            for _ in 0..(secs / 0.008) as i32 {
                let input = Mw2Input { msec: 8, speed_scale: 1.0, fire_rate: 1.0, buttons, ..Default::default() };
                unsafe { mw2_step(sim, &input, Some(floor), std::ptr::null_mut(), &mut st) };
                let mut shots = [weapons::Mw2Shot::default(); 16];
                let n = unsafe { mw2_take_shots(sim, shots.as_mut_ptr(), 16) };
                for s in &shots[..n as usize] {
                    fired[s.hand.min(1) as usize] += 1;
                }
                unsafe {
                    mw2_viewmodel_step(right, sim, 0.008, rpose.as_mut_ptr());
                    mw2_viewmodel_step(left, sim, 0.008, lpose.as_mut_ptr());
                }
                if let Some((slot, _, _)) = lv.playing() {
                    left_slots.insert(slot);
                }
            }
        }
        eprintln!("shots right {} left {}; clip right {} left {}; left anims {:?}", fired[0], fired[1], st.clip, st.clip_left, left_slots);
        eprintln!("root x right {:.3} left {:.3}", rpose[0], lpose[0]);
        assert!(fired[0] >= 5 && fired[1] >= 5, "both hands fire");
        assert!(st.clip_left >= 0 && st.clip_left < row.facts.clip_size, "left clip spent");
        assert!(!left_slots.is_empty(), "left viewmodel animates");
    }

    /// Weapons matching WEAPONS (comma list of substrings): gun model, anim slots, offhand class.
    #[test]
    #[ignore]
    fn weapon_rows() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let want: Vec<String> = std::env::var("WEAPONS").unwrap_or_default().split(',').map(str::to_owned).collect();
        {
            let rigs = viewmodel::RIGS.read().unwrap();
            let camo: Vec<&str> = rigs.iter().map(|r| r.name.as_str()).filter(|n| ["woodland", "desert", "arctic", "digital", "urban", "tiger", "fall", "gold", "prestige"].iter().any(|c| n.contains(c))).collect();
            eprintln!("CAMO RIGS ({} of {}): {:?}", camo.len(), rigs.len(), &camo[..camo.len().min(30)]);
            let ms: Vec<String> = models::MESHES.read().unwrap().iter().map(|m| m.name.clone()).chain(models::MATERIAL_TEXTURES.read().unwrap().as_ref().map(|t| t.iter().flat_map(|(k, v)| std::iter::once(k.clone()).chain(v.iter().map(|x| x.2.clone()))).collect::<Vec<_>>()).unwrap_or_default()).filter(|n| n.contains("woodland") || n.contains("camo")).take(40).collect();
            eprintln!("CAMO MODELS/MATS: {ms:?}");
            drop(rigs);
            let m4 = weapons::index_of("m4_mp");
            for camo in ["", "woodland"] {
                unsafe { weapons::mw2_set_weapon_camo(m4, camo.as_ptr(), camo.len()) };
                if let Some(v) = viewmodel::Viewmodel::build(m4) {
                    for (i, s) in v.surfaces.iter().enumerate() {
                        let painted = match (s.color_map.as_deref(), s.detail_map.as_deref()) {
                            (Some(c), Some(d)) => images::painted_rgba(c, d).map(|(w, h, _)| format!("{w}x{h}")),
                            _ => None,
                        };
                        eprintln!("CAMO '{camo}' surf {i}: color {:?} detail {:?} blend {} painted {:?}", s.color_map, s.detail_map, s.blend, painted);
                    }
                }
            }
            let rt = "red_tiger";
            unsafe { weapons::mw2_set_weapon_camo(m4, rt.as_ptr(), rt.len()) };
            eprintln!("CAMO world model {:?}", character::weapon_world_model(m4));
            if let Some(g) = character::world_gun(m4) {
                for (i, s) in g.surfaces.iter().enumerate() {
                    let detail = s.material.as_deref().and_then(|m| models::material_textures(m).into_iter().find(|t| t.0 == 3).map(|t| t.2));
                    let (mut w, mut h) = (0u32, 0u32);
                    let n = unsafe { character::mw2_character_weapon_surface_rgba(m4, i as u32, &mut w, &mut h, std::ptr::null_mut(), 0) };
                    eprintln!("CAMO world surf {i}: material {:?} color {:?} detail {detail:?} -> {w}x{h} ({n} bytes)", s.material, s.color_map);
                }
            }
            unsafe { weapons::mw2_set_weapon_camo(m4, std::ptr::null(), 0) };
            for fof in ["hud_fofbox_hostile", "hud_fofbox_self", "hud_fofbox_self_sp", "hud_fofbox_friendly", "hud_fofbox", "hud_fofbox_ally", "veh_hud_target", "veh_hud_friendly", "hud_fofbox_neutral", "remotemissile_infantry_target", "ac130_overlay_25mm"] {
                let (mut w, mut h) = (0u32, 0u32);
                let n = unsafe { mw2_hud_image(fof.as_ptr(), fof.len(), &mut w, &mut h, std::ptr::null_mut(), 0) };
                eprintln!("FOF {fof}: {w}x{h} ({n} bytes)");
                if n > 0 && fof.starts_with("hud_fofbox") {
                    let mut px = vec![0u8; n as usize];
                    unsafe { mw2_hud_image(fof.as_ptr(), fof.len(), &mut w, &mut h, px.as_mut_ptr(), n) };
                    let _ = std::fs::write(format!("{}/../../target/{fof}_{w}x{h}.rgba", env!("CARGO_MANIFEST_DIR")), &px);
                }
            }
            let lap = weapons::index_of("killstreak_predator_missile_mp");
            if let Some(v) = viewmodel::Viewmodel::build(lap) {
                for (i, s) in v.surfaces.iter().enumerate() {
                    let tex = s.color_map.as_deref().and_then(models::texture).map(|t| format!("{}x{} fmt {}", t.width, t.height, t.format));
                    eprintln!("LAPTOP surf {i}: color {:?} ({tex:?}) detail {:?} blend {} tris {}", s.color_map, s.detail_map, s.blend, s.index_count / 3);
                }
            }
            for img in ["uav_graphic", "uav_scanlines", "uav_cursor"] {
                if let Some(t) = models::texture(img) {
                    if let Some(px) = images::decode(t.format, t.width as usize, t.height as usize, &t.top_mip) {
                        let n = (px.len() / 4) as f64;
                        let (mut r, mut g, mut b, mut a) = (0f64, 0f64, 0f64, 0f64);
                        for c in px.chunks_exact(4) { r += c[0] as f64; g += c[1] as f64; b += c[2] as f64; a += c[3] as f64; }
                        eprintln!("LAPTOP image {img}: mean rgba {:.0} {:.0} {:.0} {:.0}", r / n, g / n, b / n, a / n);
                        let _ = std::fs::write(format!("{}/../../target/laptop_{img}_{}x{}.rgba", env!("CARGO_MANIFEST_DIR"), t.width, t.height), &px);
                    }
                }
            }
            for m in ["mc/mtl_uav_graphic", "mc/mtl_uav_scanlines", "mc/mtl_uav_cursor", "mc/mtl_uav_control"] {
                let bits = models::MESHES.read().unwrap().iter().flat_map(|x| x.surfaces.iter()).find(|s| s.material.as_deref() == Some(m)).and_then(|s| s.state_bits);
                if let Some([w0, w1]) = bits {
                    eprintln!("LAPTOP bits {m}: w0 {w0:#010x} w1 {w1:#010x} op {} src {} dst {} srcA {} dstA {} opA {}", (w0 >> 8) & 7, w0 & 0xf, (w0 >> 4) & 0xf, (w0 >> 12) & 0xf, (w0 >> 16) & 0xf, (w0 >> 20) & 7);
                }
                eprintln!("LAPTOP material {m}: blend {} textures {:?}", models::material_blend(m), models::material_textures(m));
            }
            if let Some(v) = viewmodel::Viewmodel::build(lap) {
                for si in [6usize, 10, 11, 12, 13] {
                    let s = &v.surfaces[si];
                    let mut bones = std::collections::BTreeMap::new();
                    let (mut lo, mut hi) = ([f32::MAX; 3], [f32::MIN; 3]);
                    for &ix in &v.indices[s.index_start as usize..(s.index_start + s.index_count) as usize] {
                        let b = v.bone_index[ix as usize][0];
                        *bones.entry(v.bone_label(b as usize)).or_insert(0) += 1;
                        let p = v.positions[ix as usize];
                        for k in 0..3 { lo[k] = lo[k].min(p[k]); hi[k] = hi[k].max(p[k]); }
                    }
                    let (mut ulo, mut uhi) = ([f32::MAX; 2], [f32::MIN; 2]);
                    for &ix in &v.indices[s.index_start as usize..(s.index_start + s.index_count) as usize] {
                        let t = v.uvs[ix as usize];
                        for k in 0..2 { ulo[k] = ulo[k].min(t[k]); uhi[k] = uhi[k].max(t[k]); }
                    }
                    eprintln!("LAPTOP surf {si} bones {bones:?} bind box {lo:?}..{hi:?} uv {ulo:?}..{uhi:?} normal {:?}", v.normals[v.indices[s.index_start as usize] as usize]);
                }
            }
            if let Some(m) = viewmodel::RIGS.read().unwrap().iter().find(|r| r.name == "viewmodel_uav_control_unit") {
                let p = &m.pose;
                for (i, n) in p.bone_names.iter().enumerate() {
                    let parent = if i >= p.num_root_bones { let step = p.parent_list[i - p.num_root_bones] as usize; p.bone_names.get(i - step).cloned().unwrap_or_default() } else { "(root)".into() };
                    eprintln!("LAPTOP bone {i} {n} <- {parent}");
                }
                let row = weapons::row(lap).unwrap();
                for (slot, a) in row.xanims.iter().enumerate() {
                    if let Some(a) = a { eprintln!("LAPTOP anim slot {slot}: {a}"); }
                }
                eprintln!("LAPTOP rig surfaces: {:?}", m.surfaces.iter().map(|s| (s.material.clone(), s.color_map.clone())).collect::<Vec<_>>());
            }
        }
        for r in weapons::TABLE.read().unwrap().iter() {
            if !want.iter().any(|w| !w.is_empty() && r.name.contains(w.as_str())) {
                continue;
            }
            let anims: Vec<String> = r.xanims.iter().enumerate().filter_map(|(i, a)| a.as_ref().map(|a| format!("{i}:{a}"))).collect();
            let right: Vec<String> = r.xanims_right.iter().enumerate().filter_map(|(i, a)| a.as_ref().map(|a| format!("{i}:{a}"))).collect();
            let left: Vec<String> = r.xanims_left.iter().enumerate().filter_map(|(i, a)| a.as_ref().map(|a| format!("{i}:{a}"))).collect();
            eprintln!("HANDED {} right {}
    left {}", r.name, right.join(" "), left.join(" "));
            let vm = viewmodel::Viewmodel::build(weapons::index_of(&r.name)).is_some();
            let captured = r.gun_model.as_deref().map(|g| (models::index_of(g) != 0, viewmodel::RIGS.read().unwrap().iter().find(|m| m.name == g).map(|m| m.pose.bone_names.clone())));
            eprintln!("CAPTURED {} (mesh, rig bones) {:?}", r.name, captured);
            eprintln!("ROW {} gun {:?} vm_builds {vm} offhand {} type {} class {} world {:?}
    anims {}", r.name, r.gun_model, r.equip.offhand_class, r.facts.weap_type, r.facts.weap_class, character::weapon_world_model(weapons::index_of(&r.name)), anims.join(" "));
        }
    }

    /// Launcher numbers: launch speed, upward speed, guidance, spread (rocket flight).
    #[test]
    #[ignore]
    fn launcher_numbers() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        for r in weapons::TABLE.read().unwrap().iter() {
            let e = &r.equip;
            if e.speed > 0 && r.equip.offhand_class == 0 && !r.name.contains('_') == false && (e.is_rocket() || r.name.starts_with("gl") || r.name.starts_with("m79")) {
                eprintln!("LAUNCHER {:22} speed {:5} up {:4} guidance {} rocket {} hipSpread {:.1}-{:.1} adsSpread {:.1}", r.name, e.speed, e.speed_up, e.guidance, e.is_rocket(), r.facts.hip_spread_stand_min, r.facts.hip_spread_stand_max, r.facts.ads_spread);
            }
        }
    }

    /// Every player weapon (base + attachment variants, offhands) has what playing it needs:
    /// viewmodel rig, world model, fire sound, muzzle flashes, projectile model, HUD icon.
    /// `cargo test --release -p mw2sim weapon_asset_audit -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn weapon_asset_audit() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        if !std::path::Path::new(COMMON_MP).exists() {
            return;
        }
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        fx::mw2_fx_init();
        let t0 = std::time::Instant::now();
        while !sounds::iwd_ready() && t0.elapsed().as_secs() < 120 {
            std::thread::sleep(std::time::Duration::from_millis(200));
        }
        // Killstreak / vehicle / script weapons aren't in anyone's hands as a class weapon.
        const NOT_PLAYER: [&str; 22] = ["ac130", "cobra", "pavelow", "littlebird", "harrier", "sentry", "remote", "stealth", "nuke", "killstreak",
            "airdrop", "uav", "predator", "defaultweapon", "barrel", "heli", "turret", "artillery", "mig29", "apache", "deploy", "briefcase"];
        let rows: Vec<weapons::WeaponRow> = weapons::TABLE.read().unwrap().iter().filter(|r| {
            r.name.ends_with("_mp") && !NOT_PLAYER.iter().any(|k| r.name.contains(k))
        }).cloned().collect();
        let mut fails: std::collections::BTreeMap<&str, Vec<String>> = Default::default();
        let mut fx_ok = std::collections::HashMap::new();
        let mut fx = |name: &str| -> bool {
            *fx_ok.entry(name.to_owned()).or_insert_with(|| unsafe { fx::mw2_fx_find(name.as_ptr(), name.len()) } != 0)
        };
        let (mut guns, mut offhands) = (0, 0);
        for r in &rows {
            let idx = weapons::index_of(&r.name);
            let offhand = r.equip.offhand_class != 0;
            if offhand { offhands += 1 } else { guns += 1 }
            let mut fail = |k: &'static str| fails.entry(k).or_default().push(r.name.clone());
            if r.gun_model.is_some() && viewmodel::Viewmodel::build(idx).is_none() { fail("viewmodel doesn't build") }
            if !offhand && r.gun_model.is_none() { fail("no viewmodel gun model") }
            match character::weapon_world_model(idx) {
                Some(m) if models::index_of(&m) == 0 => fail("world model not captured"),
                None if !offhand => fail("no world model"),
                _ => {}
            }
            if !offhand && r.sounds[weapons::SOUND_FIRE] == 0 { fail("no fire sound") }
            if !offhand && r.facts.weap_type != 2 {
                match &r.effects[0] { Some(e) if !fx(e) => fail("view flash fx missing"), None => fail("no view flash fx"), _ => {} }
                match &r.effects[1] { Some(e) if !fx(e) => fail("world flash fx missing"), None => fail("no world flash fx"), _ => {} }
            }
            if let Some(m) = &r.equip.projectile_model {
                if models::index_of(m) == 0 { fail("projectile model not captured") }
            }
            match &r.hud_icon {
                Some(i) if images::hud_rgba(i).is_none() => fail("HUD icon image missing"),
                None if !offhand => fail("no HUD icon"),
                _ => {}
            }
        }
        eprintln!("audited {} player weapons ({guns} guns incl. attachment variants, {offhands} offhand / equipment)", rows.len());
        for (k, v) in &fails {
            let mut bases: Vec<String> = v.iter().map(|n| n.split('_').next().unwrap_or(n).to_owned()).collect();
            bases.dedup();
            eprintln!("FAIL {k}: {} weapons ({} base): {}", v.len(), bases.len(), v.iter().take(14).cloned().collect::<Vec<_>>().join(", "));
        }
    }

    #[test]
    #[ignore]
    fn anim_audit() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        if !std::path::Path::new(COMMON_MP).exists() {
            return;
        }
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let only = std::env::var("AUDIT").unwrap_or_default();
        let names: Vec<String> = weapons::TABLE.read().unwrap().iter().map(|r| r.name.clone()).filter(|n| {
            let base = n.strip_suffix("_mp").unwrap_or("");
            !base.is_empty() && !base.contains('_') && (only.is_empty() || only.split(',').any(|o| o == n))
        }).collect();
        let mut flagged = 0;
        for name in &names {
            let idx = unsafe { mw2_weapon_index(name.as_ptr(), name.len()) };
            let vm = mw2_viewmodel_build(idx);
            if vm.is_null() {
                continue;
            }
            let sim = unsafe { mw2_create(&[0.0, 0.0, 16.0]) };
            unsafe { mw2_give_weapon(sim, idx) };
            let v = unsafe { &mut *vm };
            let mut pose = vec![0f32; v.bone_count() * 7];
            let mut st = Mw2State::default();
            let mut log: Vec<String> = Vec::new();
            let mut issues: Vec<String> = Vec::new();
            let mut last_slot: Option<usize> = None;
            let mut held = 0f32;
            let mut last_event = u32::MAX;
            let mut last_seq = u32::MAX;
            let mut t = 0f32;
            // (seconds, buttons): tap fire 4x, ADS + tap fire 3x, empty the clip, reload, sprint.
            let mut script: Vec<(f32, u32)> = vec![(1.6, 0)];
            for _ in 0..4 { script.push((0.05, playerstate_iw4::buttons::ATTACK)); script.push((0.9, 0)); }
            script.push((0.6, playerstate_iw4::buttons::ADS));
            for _ in 0..3 { script.push((0.05, playerstate_iw4::buttons::ADS | playerstate_iw4::buttons::ATTACK)); script.push((0.9, playerstate_iw4::buttons::ADS)); }
            script.push((0.5, 0));
            script.push((8.0, playerstate_iw4::buttons::ATTACK)); // empties a mag (auto weapons) / taps on semi
            script.push((0.1, 0));
            script.push((0.1, playerstate_iw4::buttons::RELOAD));
            script.push((7.0, 0));
            script.push((2.5, playerstate_iw4::buttons::SPRINT));
            script.push((1.5, 0));
            for (secs, buttons) in script {
                let ticks = (secs / 0.008) as i32;
                for k in 0..ticks {
                    // Semi-auto: re-press every 0.15 s while "holding" attack.
                    let b = if buttons & playerstate_iw4::buttons::ATTACK != 0 && secs > 1.0 && (k % 19) > 9 { buttons & !playerstate_iw4::buttons::ATTACK } else { buttons };
                    let input = Mw2Input { msec: 8, speed_scale: 1.0, fire_rate: 1.0, buttons: b, forwardmove: if b & playerstate_iw4::buttons::SPRINT != 0 { 127 } else { 0 }, ..Default::default() };
                    unsafe { mw2_step(sim, &input, Some(floor), std::ptr::null_mut(), &mut st) };
                    let s = unsafe { &mut *sim };
                    let mut shots = [weapons::Mw2Shot::default(); 16];
                    unsafe { mw2_take_shots(sim, shots.as_mut_ptr(), 16) };
                    unsafe { mw2_viewmodel_step(vm, sim, 0.008, pose.as_mut_ptr()) };
                    t += 0.008;
                    let ev = s.vm_anim as u32 & weapon_iw4::WEAP_ANIM_EVENT_MASK;
                    let wanted = weapon_iw4::slot_for_weap_anim_event(ev);
                    let playing = v.playing();
                    if ev != last_event || s.vm_anim_seq != last_seq || playing.map(|p| p.0) != last_slot {
                        let w = wanted.map_or("idle".to_string(), |w| SLOT_NAMES.get(w).unwrap_or(&"?").to_string());
                        let p = playing.map_or("idle".to_string(), |(sl, _, d)| format!("{} ({:.2}s)", SLOT_NAMES.get(sl).unwrap_or(&"?"), d));
                        log.push(format!("  {t:6.2}s state {:2} clip {:2}  mw2 wants {w:14} -> plays {p}", s.armed.as_ref().map_or(0, |a| a.hand.weaponstate), st.clip));
                        if let (Some(w), None) = (wanted, playing) {
                            issues.push(format!("missing clip for {} at {t:.2}s", SLOT_NAMES.get(w).unwrap_or(&"?")));
                        }
                        if held > 0.3 {
                            issues.push(format!("froze {held:.2}s on finished {} before {:.2}s", last_slot.map_or("idle", |x| SLOT_NAMES.get(x).copied().unwrap_or("?")), t));
                        }
                        held = 0.0;
                        last_event = ev;
                        last_seq = s.vm_anim_seq;
                        last_slot = playing.map(|p| p.0);
                    }
                    if let Some((_, time, d)) = playing { if time >= d && d > 0.0 { held += 0.008; } }
                }
            }
            issues.dedup();
            let verbose = !only.is_empty();
            if !issues.is_empty() || verbose {
                flagged += usize::from(!issues.is_empty());
                eprintln!("== {name}: {} issue(s)", issues.len());
                for i in issues.iter().take(12) { eprintln!("   ! {i}"); }
                if verbose { for l in &log { eprintln!("{l}"); } }
            }
            unsafe { mw2_viewmodel_destroy(vm) };
            unsafe { mw2_destroy(sim) };
        }
        eprintln!("audited {} guns, {flagged} with issues", names.len());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    extern "C" fn floor(
        _user: *mut c_void,
        start: *const [f32; 3],
        end: *const [f32; 3],
        mins: *const [f32; 3],
        _maxs: *const [f32; 3],
        _mask: u32,
        out: *mut Mw2Trace,
    ) {
        let (start, end, mins) = unsafe { (*start, *end, *mins) };
        let out = unsafe { &mut *out };
        let (b0, b1) = (start[2] + mins[2], end[2] + mins[2]);
        if b0 < 0.0 {
            *out = Mw2Trace { fraction: 0.0, normal: [0.0, 0.0, 1.0], endpos: start, startsolid: 1, walkable: 1, ..Default::default() };
        } else if b1 < 0.125 && b1 < b0 {
            let f = ((b0 - 0.125) / (b0 - b1)).clamp(0.0, 1.0);
            let mut endpos = start;
            for a in 0..3 {
                endpos[a] += (end[a] - start[a]) * f;
            }
            *out = Mw2Trace { fraction: f, normal: [0.0, 0.0, 1.0], endpos, walkable: 1, ..Default::default() };
        }
    }

    const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

    #[test]
    fn ak47_fires_mw2_rounds_through_the_c_abi() {
        if !std::path::Path::new(COMMON_MP).exists() {
            eprintln!("skipped: no MW2 install");
            return;
        }
        unsafe {
            let n = mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len());
            assert!(n > 1000, "weapons loaded: {n}");
            // Killstreak props and the streak table load with the weapons.
            let crate_name = "com_plasticcase_friendly";
            let crate_id = mw2_model_index(crate_name.as_ptr(), crate_name.len());
            let mut ci = Mw2ModelInfo::default();
            assert!(crate_id != 0 && mw2_model_info(crate_id, &mut ci) == 1 && ci.vertex_count > 0, "care package crate model");
            eprintln!("crate: {} verts {} surfaces; {} killstreaks", ci.vertex_count, ci.surface_count, mw2_streak_table_count());
            assert!(mw2_streak_table_count() >= 16);
            for heli in ["vehicle_cobra_helicopter_fly_low", "vehicle_pavelow", "vehicle_apache_mp", "vehicle_little_bird_armed"] {
                let id = mw2_model_index(heli.as_ptr(), heli.len());
                let mut cls = vec![0u8; 40000];
                let mut piv = [0f32; 6];
                let n = mw2_model_rotors(id, cls.as_mut_ptr(), cls.len() as u32, piv.as_mut_ptr());
                let main = cls[..n as usize].iter().filter(|c| **c == 1).count();
                let tail = cls[..n as usize].iter().filter(|c| **c == 2).count();
                let mut mi = Mw2ModelInfo::default();
                mw2_model_info(id, &mut mi);
                eprintln!("{heli}: rig {n} verts (mesh {}), main rotor {main} verts at {:?}, tail {tail} at {:?}", mi.vertex_count, &piv[..3], &piv[3..]);
            }
            {
                let meshes = models::MESHES.read().unwrap();
                let names: Vec<String> = meshes.iter().filter(|m| m.name.starts_with("vehicle_") || m.name.starts_with("sentry_") || m.name.starts_with("projectile_")).map(|m| format!("{}({}v)", m.name, m.positions.len())).collect();
                eprintln!("killstreak models: {}", names.join(" "));
            }
            let kill = "kill";
            assert_eq!(mw2_score(kill.as_ptr(), kill.len()), 100, "team-based kill score from _rank.gsc");
            let mut rank = Mw2Rank::default();
            let mut icon = [0u8; 64];
            let mut icon_len = 0u32;
            assert_eq!(mw2_rank_for_xp(600, &mut rank, icon.as_mut_ptr(), 64, &mut icon_len), 1);
            eprintln!("600 xp -> rank {:?} icon {}", rank, String::from_utf8_lossy(&icon[..icon_len as usize]));
            assert_eq!((rank.id, rank.min_xp), (1, 500));
            // Scopes: the Intervention zooms to 15 and has MW2's scope overlay.
            let ch = "cheytac_mp";
            let cheytac = mw2_weapon_index(ch.as_ptr(), ch.len());
            let mut view = Mw2WeaponView::default();
            assert_eq!(mw2_weapon_view(cheytac, &mut view), 1);
            let mut ov = [0u8; 64];
            let n = mw2_weapon_string(cheytac, 1, ov.as_mut_ptr(), 64) as usize;
            let ov = String::from_utf8_lossy(&ov[..n.min(64)]).to_string();
            let (mut w, mut h) = (0u32, 0u32);
            let px = mw2_hud_image(ov.as_ptr(), ov.len(), &mut w, &mut h, std::ptr::null_mut(), 0);
            eprintln!("cheytac view {view:?} overlay {ov} {w}x{h}");
            {
                let mut buf = vec![0u8; px as usize];
                mw2_hud_image(ov.as_ptr(), ov.len(), &mut w, &mut h, buf.as_mut_ptr(), px);
                let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_check");
                let _ = std::fs::write(dir.join(format!("scope_{w}x{h}.rgba")), &buf);
            }
            assert!(view.ads_zoom_fov == 15.0 && view.has_overlay == 1 && px > 0, "sniper scope overlay");
            // Scope sway, and MW2's hold breath (BREATH while fully aimed) calming it.
            {
                let sniper = mw2_create(&[0.0, 0.0, 16.0]);
                assert_eq!(mw2_give_weapon(sniper, cheytac), 1);
                let mut st = Mw2State::default();
                let mut sw = Mw2ViewSway::default();
                let ads = Mw2Input { msec: 8, speed_scale: 1.0, fire_rate: 1.0, buttons: playerstate_iw4::buttons::ADS, ..Default::default() };
                let mut max_free = 0f32;
                for _ in 0..400 {
                    mw2_step(sniper, &ads, Some(floor), std::ptr::null_mut(), &mut st);
                    mw2_view_sway(sniper, 0.008, &mut sw);
                    max_free = max_free.max(sw.yaw.abs().max(sw.pitch.abs()));
                }
                let breath = Mw2Input { buttons: playerstate_iw4::buttons::ADS | playerstate_iw4::buttons::BREATH, ..ads };
                for _ in 0..375 {
                    mw2_step(sniper, &breath, Some(floor), std::ptr::null_mut(), &mut st);
                    mw2_view_sway(sniper, 0.008, &mut sw);
                }
                eprintln!("scope sway: free max {max_free:.3} deg; after 3 s held: holding {} scale {:.2} timer {} ms", sw.holding, sw.breath_scale, sw.breath_ms);
                assert!(sw.can_hold == 1 && max_free > 0.05, "sniper sways when scoped");
                assert!(sw.holding == 1 && sw.breath_scale < 0.2, "holding breath steadies the scope");
                for _ in 0..250 {
                    mw2_step(sniper, &breath, Some(floor), std::ptr::null_mut(), &mut st);
                    mw2_view_sway(sniper, 0.008, &mut sw);
                }
                eprintln!("after 5 s held: holding {} timer {} ms scale {:.2}", sw.holding, sw.breath_ms, sw.breath_scale);
                assert!(sw.holding == 0 && sw.breath_ms > 4500 && sw.breath_scale > 1.5, "breath runs out at 4.5 s: gasp sway");
                mw2_destroy(sniper);
            }
            // MW2's HUD fonts.
            for f in ["fonts/hudBigFont", "fonts/hudSmallFont", "fonts/bigFont", "fonts/objectiveFont", "fonts/normalFont"] {
                let (mut ph, mut gc, mut ml) = (0i32, 0u32, 0u32);
                let mut mat = [0u8; 64];
                let ok = mw2_font_info(f.as_ptr(), f.len(), &mut ph, &mut gc, mat.as_mut_ptr(), 64, &mut ml);
                let mat = String::from_utf8_lossy(&mat[..(ml as usize).min(64)]).to_string();
                let px = mw2_hud_image(mat.as_ptr(), mat.len(), &mut w, &mut h, std::ptr::null_mut(), 0);
                eprintln!("font {f}: ok {ok} {ph}px {gc} glyphs, sheet {mat} {w}x{h} ({px} bytes)");
                if f == "fonts/hudBigFont" && px > 0 {
                    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_check");
                    let mut sheet = vec![0u8; px as usize];
                    mw2_hud_image(mat.as_ptr(), mat.len(), &mut w, &mut h, sheet.as_mut_ptr(), px);
                    let _ = std::fs::write(dir.join(format!("font_sheet_{w}x{h}.rgba")), &sheet);
                    let mut gl = vec![Mw2Glyph::default(); gc as usize];
                    let n = mw2_font_glyphs(f.as_ptr(), f.len(), gl.as_mut_ptr(), gc);
                    let csv: String = gl[..n as usize].iter().map(|g| format!("{},{},{},{},{},{},{},{},{},{}
", g.letter, g.x0, g.y0, g.dx, g.width, g.height, g.s0, g.t0, g.s1, g.t1)).collect();
                    let _ = std::fs::write(dir.join("font_hudbig.csv"), csv);
                }
            }
            // HUD art resolves material -> image (specialty_carepackage -> specialty_care_package).
            for icon in ["specialty_uav", "specialty_carepackage", "specialty_predator_missile", "dpad_killstreak_uav", "minimap_background", "compass_radarline", "compassping_player", "ac130_overlay_grain", "specialty_uav_crate"] {
                let (mut w, mut h) = (0u32, 0u32);
                let n = mw2_hud_image(icon.as_ptr(), icon.len(), &mut w, &mut h, std::ptr::null_mut(), 0);
                eprintln!("hud {icon}: {w}x{h} ({n} bytes)");
                assert!(n > 0 && n == w * h * 4, "hud image {icon}");
                let mut px = vec![0u8; n as usize];
                mw2_hud_image(icon.as_ptr(), icon.len(), &mut w, &mut h, px.as_mut_ptr(), n);
                let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_check");
                let _ = std::fs::create_dir_all(&dir);
                let _ = std::fs::write(dir.join(format!("{icon}_{w}x{h}.rgba")), &px);
            }
            let name = "ak47_mp";
            let ak = mw2_weapon_index(name.as_ptr(), name.len());
            assert!(ak != 0);
            let mut icon = [0u8; 64];
            let n = mw2_weapon_string(ak, 0, icon.as_mut_ptr(), 64) as usize;
            let icon = String::from_utf8_lossy(&icon[..n.min(64)]).to_string();
            eprintln!("ak47 hud icon {icon}, ammo counter {}", mw2_weapon_ammo_counter(ak));
            let (mut w, mut h) = (0u32, 0u32);
            assert!(!icon.is_empty() && mw2_hud_image(icon.as_ptr(), icon.len(), &mut w, &mut h, std::ptr::null_mut(), 0) > 0, "weapon hud icon {icon}");
            for m in [icon.as_str(), "damage_feedback", "hit_direction", "blood_splatter", "720_xpbar_solid", "720_xpbar_empty", "rank_pvt1", "hud_weaponbar", "ammo_counter_riflebullet_mp", "ammo_counter_bullet_mp", "ammo_counter_shotgunshell_mp", "ammo_counter_beltbullet_mp", "ammo_counter_rocket_mp"] {
                let n = mw2_hud_image(m.as_ptr(), m.len(), &mut w, &mut h, std::ptr::null_mut(), 0);
                eprintln!("hud {m}: {w}x{h} {}", if n > 0 { "ok" } else { "MISSING" });
                if n > 0 {
                    let mut px = vec![0u8; n as usize];
                    mw2_hud_image(m.as_ptr(), m.len(), &mut w, &mut h, px.as_mut_ptr(), n);
                    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_check");
                    let _ = std::fs::create_dir_all(&dir);
                    let _ = std::fs::write(dir.join(format!("{m}_{w}x{h}.rgba")), &px);
                }
            }
            let sim = mw2_create(&[0.0, 0.0, 16.0]);
            assert_eq!(mw2_give_weapon(sim, ak), 1);
            let mut st = Mw2State::default();
            let idle = Mw2Input { msec: 8, speed_scale: 1.0, fire_rate: 1.0, ..Default::default() };
            for _ in 0..250 {
                mw2_step(sim, &idle, Some(floor), std::ptr::null_mut(), &mut st);
            }
            assert_eq!(st.clip, 30, "AK starts with a full mag");
            // Hold fire for one second at 125 fps.
            let fire = Mw2Input { buttons: playerstate_iw4::buttons::ATTACK, ..idle };
            let mut shots = 0u32;
            let mut buf = [weapons::Mw2Shot::default(); 16];
            for _ in 0..125 {
                mw2_step(sim, &fire, Some(floor), std::ptr::null_mut(), &mut st);
                shots += mw2_take_shots(sim, buf.as_mut_ptr(), 16);
            }
            eprintln!("ak47 after 1 s of fire: kick {:?} deg, spread {:.2} deg", st.kick_angles, st.spread_degrees);
            eprintln!("ak47_mp: {shots} rounds in 1 s, clip {} stock {}, last damage {}", st.clip, st.stock, buf[0].damage);
            // 85 ms per round => 11 or 12 rounds in a second.
            assert!((11..=12).contains(&shots), "rounds fired: {shots}");
            assert_eq!(st.clip, 30 - shots as i32);
            assert_eq!(buf[0].damage, 40.0);
            // SPAS-12 (boltAction, rechamber 467 ms): every round is pumped before the next, held
            // fire or tapped (playtest 10-04-26: "shoot a full clip without cocking it back").
            let spas_name = "spas12_mp";
            let spas = mw2_weapon_index(spas_name.as_ptr(), spas_name.len());
            // Tapped at two rhythms: a press landing on the tick the fire time ran out fired again
            // straight from FIRING with the pump skipped (80 ms taps).
            for (mode, tap) in [("held", 0), ("tapped 24 ms", 3), ("tapped 80 ms", 10)] {
                let s2 = mw2_create(&[0.0, 0.0, 16.0]);
                assert_eq!(mw2_give_weapon(s2, spas), 1);
                for _ in 0..250 {
                    mw2_step(s2, &idle, Some(floor), std::ptr::null_mut(), &mut st);
                }
                let mut times = Vec::new();
                let mut states = Vec::new();
                for i in 0..375 {
                    let down = tap == 0 || (i / tap) % 2 == 0;
                    let inp = if down { fire } else { idle };
                    mw2_step(s2, &inp, Some(floor), std::ptr::null_mut(), &mut st);
                    let got = mw2_take_shots(s2, buf.as_mut_ptr(), 16);
                    if got > 0 { times.push(i * 8); }
                    if states.last() != Some(&st.weaponstate) { states.push(st.weaponstate); }
                }
                eprintln!("spas12 {mode} 3 s: trigger pulls that fired at {times:?} ms; clip {}; weapon states {states:?}", st.clip);
                let gaps: Vec<i32> = times.windows(2).map(|w| w[1] - w[0]).collect();
                assert!(gaps.iter().all(|&g| g >= 900), "spas12 {mode}: a round every fire + rechamber (470 + 467 ms), gaps {gaps:?}");
                if tap == 0 { assert_eq!(times.len(), 1, "spas12 held: semi-auto, one round per pull"); }
                mw2_destroy(s2);
            }
            let fire_clip = mw2_weapon_sound(ak, weapons::SOUND_FIRE as u32);
            assert!(fire_clip != 0, "AK fire sound resolved");
            let mut info = Mw2ClipInfo::default();
            assert_eq!(mw2_clip_info(fire_clip, &mut info), 1);
            eprintln!("ak47 fire clip: {} Hz x{} ch, {:.2} s", info.rate, info.channels, info.frames as f32 / info.rate as f32);
            assert_eq!(info.rate, 44100);
            let model = mw2_weapon_model(ak);
            let mut mi = Mw2ModelInfo::default();
            assert_eq!(mw2_model_info(model, &mut mi), 1, "AK has a gun model");
            let mut si = Mw2SurfaceInfo::default();
            assert_eq!(mw2_model_surface(model, 0, &mut si), 1);
            eprintln!("ak47 model: {} verts {} tris {} surfaces; surface 0 texture fmt {} {}x{} ({} bytes)", mi.vertex_count, mi.index_count / 3, mi.surface_count, si.texture_format, si.texture_width, si.texture_height, si.texture_bytes);
            assert!(si.texture_bytes > 0);
            let shown: Vec<u32> = (0..mi.surface_count).filter(|&s| mw2_weapon_surface_visible(ak, s) == 1).collect();
            eprintln!("ak47_mp shows {} of {} surfaces", shown.len(), mi.surface_count);
            assert!(shown.len() < mi.surface_count as usize, "attachments hidden on the bare AK");
            let vm = mw2_viewmodel_build(ak);
            assert!(!vm.is_null(), "AK viewmodel rig builds");
            let mut vi = Mw2ViewmodelInfo::default();
            assert_eq!(mw2_viewmodel_info(vm, &mut vi), 1);
            let mut pose = vec![0f32; vi.bone_count as usize * 7];
            let mut ok = true;
            for _ in 0..120 {
                ok &= mw2_viewmodel_step(vm, sim, 1.0 / 60.0, pose.as_mut_ptr()) == 1;
            }
            assert!(ok && pose.iter().all(|x| x.is_finite()), "pose finite");
            // The gun's root sits on tag_weapon: somewhere in front of the eye (Unity +Z), within arm's reach.
            let v = unsafe { &*vm };
            let gun_root = v.bone_count() - 36;
            let p = &pose[gun_root * 7..gun_root * 7 + 3];
            eprintln!("viewmodel: {} bones {} verts {} tris {} surfaces; gun root at {:?} m from eye", vi.bone_count, vi.vertex_count, vi.index_count / 3, vi.surface_count, p);
            assert!(p[2] > 0.0 && p[2] < 1.0, "gun in front of the eye");
            // Dump CPU-skinned frames for an offline render: hip idle, then fully ADS.
            let dump = |name: &str, pose: &[f32]| {
                let verts = v.skin_cpu(pose);
                let mut out = String::new();
                for p in &verts { out.push_str(&format!("v {} {} {}
", p[0], p[1], p[2])); }
                for s in &v.surfaces {
                    for t in v.indices[s.index_start as usize..(s.index_start + s.index_count) as usize].chunks_exact(3) {
                        out.push_str(&format!("f {} {} {}
", t[0] + 1, t[1] + 1, t[2] + 1));
                    }
                }
                let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/vm_check");
                let _ = std::fs::create_dir_all(&dir);
                let _ = std::fs::write(dir.join(format!("{name}.obj")), out);
            };
            dump("hip", &pose);
            // Textured check: UVs per vertex + each surface's colorMap as a DDS.
            {
                let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/vm_check");
                let mut uvs = String::new();
                for t in &v.uvs { uvs.push_str(&format!("{} {}
", t[0], t[1])); }
                let _ = std::fs::write(dir.join("uvs.txt"), uvs);
                let mut surf_txt = String::new();
                for (i, s) in v.surfaces.iter().enumerate() {
                    surf_txt.push_str(&format!("{} {} {}
", s.index_start, s.index_count, s.color_map.clone().unwrap_or_default()));
                    if let Some(t) = s.color_map.as_deref().and_then(models::texture) {
                        let fourcc: &[u8; 4] = match t.format { 11 => b"DXT1", 13 => b"DXT5", 12 => b"DXT3", _ => continue };
                        let mut dds = Vec::with_capacity(128 + t.top_mip.len());
                        dds.extend_from_slice(b"DDS ");
                        let mut h = [0u32; 31];
                        h[0] = 124; h[1] = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000; h[2] = t.height; h[3] = t.width; h[4] = t.top_mip.len() as u32;
                        h[18] = 32; h[19] = 0x4; h[20] = u32::from_le_bytes(*fourcc); h[26] = 0x1000;
                        for w in h { dds.extend_from_slice(&w.to_le_bytes()); }
                        dds.extend_from_slice(&t.top_mip);
                        let _ = std::fs::write(dir.join(format!("surf{i}.dds")), dds);
                    }
                }
                let _ = std::fs::write(dir.join("surfaces.txt"), surf_txt);
            }
            let ads_in = Mw2Input { buttons: playerstate_iw4::buttons::ADS, ..idle };
            for _ in 0..125 {
                mw2_step(sim, &ads_in, Some(floor), std::ptr::null_mut(), &mut st);
                mw2_viewmodel_step(vm, sim, 1.0 / 125.0, pose.as_mut_ptr());
            }
            eprintln!("after ADS: ads_frac {:.2}, gun root at {:?}", st.ads_frac, &pose[gun_root * 7..gun_root * 7 + 3]);
            dump("ads", &pose);
            mw2_viewmodel_destroy(vm);
            for (n, wname) in [("fire", "ak47_mp"), ("fire", "m4_mp"), ("fire", "cheytac_mp")] {
                let w = weapons::row(weapons::index_of(wname)).unwrap();
                let v = &w.variants[weapons::SOUND_FIRE];
                eprintln!("{wname} {n}: {} variants {:?}", v.len(), v.iter().map(|x| (x.vol, x.pitch)).collect::<Vec<_>>());
            }
            // Double attack speed => twice the rounds.
            let fast = Mw2Input { fire_rate: 2.0, ..fire };
            let mut fast_shots = 0u32;
            for _ in 0..125 {
                mw2_step(sim, &fast, Some(floor), std::ptr::null_mut(), &mut st);
                fast_shots += mw2_take_shots(sim, buf.as_mut_ptr(), 16);
            }
            eprintln!("ak47_mp at 2x attack speed: {fast_shots} rounds in 1 s (clip now {})", st.clip);
            mw2_destroy(sim);
        }
    }

    #[test]
    fn walks_at_mw2_speed_through_the_c_abi() {
        unsafe {
            let sim = mw2_create(&[0.0, 0.0, 16.0]);
            assert!(!sim.is_null());
            let mut st = Mw2State::default();
            let idle = Mw2Input { msec: 8, speed_scale: 1.0, fire_rate: 1.0, ..Default::default() };
            for _ in 0..125 {
                assert_eq!(mw2_step(sim, &idle, Some(floor), std::ptr::null_mut(), &mut st), 1);
            }
            assert_eq!(st.grounded, 1);
            let walk = Mw2Input { msec: 8, forwardmove: 127, speed_scale: 1.0, fire_rate: 1.0, ..Default::default() };
            for _ in 0..250 {
                mw2_step(sim, &walk, Some(floor), std::ptr::null_mut(), &mut st);
            }
            let speed = (st.velocity[0].powi(2) + st.velocity[1].powi(2)).sqrt();
            assert!((speed - 190.0).abs() < 0.01, "walk speed {speed}");
            let fast = Mw2Input { speed_scale: 1.5, ..walk };
            for _ in 0..250 {
                mw2_step(sim, &fast, Some(floor), std::ptr::null_mut(), &mut st);
            }
            let speed = (st.velocity[0].powi(2) + st.velocity[1].powi(2)).sqrt();
            assert!((speed - 285.0).abs() < 0.01, "scaled walk speed {speed}");
            mw2_destroy(sim);
        }
    }
}

#[cfg(test)]
mod heli_bones {
    use super::*;
    /// Bone names (and base positions) of the killstreak aircraft rigs: which ones are rotors,
    /// turrets and barrels.
    #[test]
    #[ignore]
    fn heli_rig_bones() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        // MODEL=vehicle_ac130_low_mp loads one model first (rigs are built on first use).
        if let Ok(m) = std::env::var("MODEL") {
            let idx = unsafe { mw2_model_index(m.as_ptr(), m.len()) };
            let mut c = vec![0u8; 1 << 20];
            let mut piv = [0f32; 6];
            let n = unsafe { mw2_model_rotors(idx, c.as_mut_ptr(), c.len() as u32, piv.as_mut_ptr()) };
            eprintln!("{m}: index {idx}, rig verts {n}");
            for tag in std::env::var("TAGS").unwrap_or_default().split(',').filter(|t| !t.is_empty()) {
                let mut f = [0f32; 9];
                let ok = unsafe { mw2_model_tag_frame(idx, tag.as_ptr(), tag.len(), f.as_mut_ptr()) };
                eprintln!("  {tag}: ok {ok} pos ({:.0},{:.0},{:.0}) fwd ({:.2},{:.2},{:.2}) up ({:.2},{:.2},{:.2})", f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8]);
            }
        }
        let rigs = viewmodel::RIGS.read().unwrap();
        for r in rigs.iter().filter(|r| r.name.starts_with("vehicle_") || r.name.starts_with("sentry_")) {
            let names: Vec<String> = (0..r.pose.num_bones)
                .map(|b| {
                    let n = r.pose.bone_names.get(b).cloned().unwrap_or_default();
                    let t = r.pose.base_mat.get(b).map(|(_, t)| t.to_array()).unwrap_or([0.0; 3]);
                    format!("{n}@({:.0},{:.0},{:.0})", t[0], t[1], t[2])
                })
                .collect();
            eprintln!("{} ({} bones): {}", r.name, r.pose.num_bones, names.join(" "));
        }
    }
}
