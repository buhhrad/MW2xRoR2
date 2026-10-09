//! MW2 grenades, equipment and launcher projectiles.
//!
//! Three pieces, all driven from `Mw2Sim::step`:
//!
//! * `Offhand`: the player's lethal ("frag" button) and tactical ("smoke" button) equipment,
//!   fed to IW4L's `weapon_iw4` offhand state machine (pin pull, cook, throw, cook-off,
//!   detonator) through `OffhandCmd`.
//! * `Missiles`: the live projectiles of one player, stepped with MW2's timing (ms) against the
//!   host's world trace. This is the part of IW4L's `sim` crate (`equipment.rs`, `missile.rs`)
//!   that does not need bevy: launch velocity, TR_GRAVITY flight, per-surface bounce, settle,
//!   sticky equipment, fuse / timed detonation, impact explode, activation distance (dud).
//! * the C ABI for both (`mw2_set_offhand`, `mw2_missiles`, `mw2_missile_events`, ...).
//!
//! The sim never touches enemies, players or damage. The host draws the models and trails,
//! plays sounds and explosions, applies damage from the explode events, and tells the sim when a
//! projectile touched an enemy (`mw2_missile_detonate`, `mw2_missile_attach`).
//!
//! Units: IW4 inches, Z up, milliseconds on the sim clock (`Mw2Sim::server_time`).

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Arc;
use std::sync::atomic::{AtomicU32, Ordering};

use entity_iw4::{
    GRENADE_BLADE_SPIN_PITCH, GRENADE_SPIN_PITCH_MAX, GRENADE_SPIN_PITCH_MIN, GRENADE_SPIN_ROLL_MAX,
    GRENADE_SPIN_ROLL_MIN, MissileLandAnglesIn, TR_GRAVITY, TR_LINEAR, TR_STATIONARY, Trajectory,
    evaluate_trajectory, evaluate_trajectory_delta, fire_grenade_no_draw_ms, fire_missile_apos,
    init_grenade_apos, init_grenade_pos, missile_land_angles, truncated_tr_delta,
};
use mw2data::CapturedWeapon;
use playerstate_iw4::PlayerState;
use weapon_iw4::{CURSOR_HINT_NONE, OFFHAND_INV_SLOTS, OffhandCmd, OffhandInvRow, WeaponCombatFacts};

use crate::weapons::{self, Rng};
use crate::{Mw2Sim, Mw2Trace};

/// IW4L `MASK_SHOT` (`MASK_BULLET_WORLD`): what projectiles collide with. The host ignores masks
/// today and sweeps its world layer.
pub const MISSILE_MASK: u32 = 0x0280_6831;

/// Half extent (inches) of the cube swept for a projectile. IW4L traces a point; the host's
/// box sweep is more robust with a little size, and a 1" half extent lets a 2" grenade rest on
/// the floor with its centre 1" up.
pub const MISSILE_HALF: f32 = 1.0;

const GRENADE_FUSE_CAP_MS: i32 = 60_000;
const GRENADE_DEFAULT_FUSE_MS: i32 = 30_000;
const ROCKET_CLEANUP_MS: i32 = 60_000;
/// IW4L `MATCH_TICK_MS`: the delay before a smoke grenade's postponed fuse is re-checked.
const FUSE_RETRY_MS: i32 = 50;
/// A bounce must change velocity by more than this (units/s) to raise a bounce event.
const BOUNCE_EVENT_SPEED_DELTA: f32 = 100.0;
/// A grenade that leaves a floor bounce slower than this comes to rest. (IW4L uses 20 u/s for
/// throwing knives only; plain grenades have no stop rule there, so this is MW2-like but
/// approximate.)
const REST_SPEED: f32 = 20.0;
const WEAPCLASS_THROWINGKNIFE: i32 = 9;
const OFFHAND_CLASS_SMOKE: i32 = 2;
const SURF_TYPES: usize = 31;
const MAX_MISSILES: usize = 96;
const MAX_EVENTS: usize = 512;

/// `Mw2Missile::kind`.
pub const KIND_GRENADE: u8 = 0;
pub const KIND_ROCKET: u8 = 1;
pub const KIND_PLACED: u8 = 2;
pub const KIND_KNIFE: u8 = 3;

/// `Mw2Missile::state`.
pub const STATE_FLYING: u8 = 0;
pub const STATE_RESTING: u8 = 1;
pub const STATE_STUCK: u8 = 2;

/// `Mw2MissileEvent::kind`.
pub const EV_LAUNCH: u8 = 0;
pub const EV_BOUNCE: u8 = 1;
pub const EV_STICK: u8 = 2;
pub const EV_EXPLODE: u8 = 3;
pub const EV_DUD: u8 = 4;
pub const EV_COOKOFF: u8 = 5;

/// `Mw2MissileEvent::flags`: the host forced this through `mw2_missile_detonate` (a rocket or
/// knife touching an enemy, a C4 trigger) rather than the sim's own world contact / fuse.
pub const EVF_HOST: u8 = 1;

/// How rockets fly. Default = straight and flat along the aim, from the eye (where the
/// crosshair points, like bullets; playtest 10-03-26). IW4L's straight line with
/// `projectile_speed_up` added (rpg_mp: 1500 forward + 500 up) climbed ~18 degrees over the
/// crosshair; ballistic made an RPG shot fall back out of the sky. Ballistic-guidance weapons
/// (missileGuidance 3, Javelin top attack) still loft. The host can switch (`mw2_set_rocket_flight`).
pub const ROCKET_BALLISTIC: u32 = 1;
pub const ROCKET_LINEAR_WITH_UP: u32 = 0;
pub const ROCKET_FLAT: u32 = 2;
static ROCKET_FLIGHT: AtomicU32 = AtomicU32::new(ROCKET_FLAT);

// ---- weapon facts -----------------------------------------------------------------------------

/// Everything the projectile and offhand code needs from one weapon definition, captured at
/// load (`weapons::load`).
#[derive(Clone, Debug, Default)]
pub struct Equip {
    pub offhand_class: i32,
    pub hold_fire_time_ms: i32,
    pub fuse_time_ms: i32,
    pub cook_off_hold: bool,
    pub has_detonator: bool,
    pub detonate_delay_ms: i32,
    pub detonate_time_ms: i32,
    pub projectile_rotates: bool,
    pub timed_detonation: bool,
    pub impact_explode: bool,
    pub stick_to_players: bool,
    pub stickiness: i32,
    pub radius: i32,
    pub radius_min: i32,
    pub inner_damage: i32,
    pub outer_damage: i32,
    pub damage_cone_angle: f32,
    pub guidance: i32,
    pub ignition_delay_ms: i32,
    pub require_lock: bool,
    pub speed: i32,
    pub speed_up: i32,
    pub speed_forward: i32,
    pub activate_dist: i32,
    pub explosion_type: i32,
    pub impact_damage: i32,
    pub weap_type: i32,
    pub weap_class: i32,
    pub parallel_bounce: Option<[f32; SURF_TYPES]>,
    pub perpendicular_bounce: Option<[f32; SURF_TYPES]>,
    pub projectile_model: Option<String>,
    pub rocket_model: Option<String>,
    /// Viewmodel rocket (`rocket_model` on the gun's tag_clip): reload length and how far into
    /// it the new rocket appears (`reloadShowRocketTime`).
    pub reload_time_ms: i32,
    pub reload_show_rocket_ms: i32,
    pub trail_fx: Option<String>,
    pub beacon_fx: Option<String>,
    pub ignition_fx: Option<String>,
    pub explosion_fx: Option<String>,
    pub bounce_sounds: Vec<Option<String>>,
    pub explosion_sound: Option<String>,
    pub projectile_sound: Option<String>,
    pub ignition_sound: Option<String>,
}

impl Equip {
    pub fn from_captured(w: &CapturedWeapon, facts: &WeaponCombatFacts) -> Self {
        let g = &w.geometry;
        Self {
            offhand_class: g.offhand_class,
            hold_fire_time_ms: g.hold_fire_time_ms,
            fuse_time_ms: g.fuse_time_ms,
            cook_off_hold: g.cook_off_hold,
            has_detonator: g.has_detonator,
            detonate_delay_ms: g.detonate_delay_ms,
            detonate_time_ms: g.detonate_time_ms,
            projectile_rotates: g.projectile_rotates,
            timed_detonation: g.timed_detonation,
            impact_explode: g.proj_impact_explode,
            stick_to_players: g.stick_to_players,
            stickiness: g.stickiness,
            radius: g.explosion_radius,
            radius_min: g.explosion_radius_min,
            inner_damage: g.explosion_inner_damage,
            outer_damage: g.explosion_outer_damage,
            damage_cone_angle: g.damage_cone_angle,
            guidance: g.missile_guidance,
            ignition_delay_ms: g.ignition_delay_ms,
            require_lock: g.require_lock_to_fire,
            speed: g.projectile_speed,
            speed_up: g.projectile_speed_up,
            speed_forward: g.projectile_speed_forward,
            activate_dist: g.projectile_activate_dist,
            explosion_type: g.projectile_explosion_type,
            impact_damage: g.damage,
            weap_type: facts.weap_type,
            weap_class: facts.weap_class,
            parallel_bounce: g.parallel_bounce,
            perpendicular_bounce: g.perpendicular_bounce,
            projectile_model: w.projectile_model.clone(),
            rocket_model: w.rocket_model.clone(),
            reload_time_ms: facts.reload_time_ms,
            reload_show_rocket_ms: g.reload_show_rocket_time_ms,
            trail_fx: w.proj_trail_fx.clone(),
            beacon_fx: w.proj_beacon_fx.clone(),
            ignition_fx: w.proj_ignition_fx.clone(),
            explosion_fx: w.explosion_fx.clone(),
            bounce_sounds: w.bounce_sounds.clone(),
            explosion_sound: w.proj_explosion_sound.clone(),
            projectile_sound: w.projectile_sound.clone(),
            ignition_sound: w.proj_ignition_sound.clone(),
        }
    }

    pub fn is_throwing_knife(&self) -> bool {
        self.weap_class == WEAPCLASS_THROWINGKNIFE
    }

    /// IW4L `EquipmentRuntimeFacts::is_usable`.
    pub fn is_usable(&self) -> bool {
        (self.speed > 0 || self.stickiness != 0) && (self.fuse_time_ms > 0 || self.impact_damage > 0 || self.inner_damage > 0)
    }

    /// Claymore / C4: stays where it lands until triggered.
    pub fn is_placed(&self) -> bool {
        !self.timed_detonation && matches!(self.stickiness, 1..=4) && self.offhand_class != 0
    }

    /// A rocket launcher (fired from the gun), as opposed to a grenade launcher.
    pub fn is_rocket(&self) -> bool {
        self.weap_type == weapon_iw4::WEAPTYPE_PROJECTILE && self.weap_class != weapon_iw4::WEAPCLASS_GRENADE
    }

    /// Guided launchers (Javelin, Stinger): flown straight until guidance is ported.
    pub fn is_guided(&self) -> bool {
        self.require_lock
    }

    /// `Mw2Missile::kind` for what this weapon launches.
    pub fn kind(&self) -> u8 {
        if self.is_throwing_knife() {
            KIND_KNIFE
        } else if self.is_rocket() {
            KIND_ROCKET
        } else if self.is_placed() {
            KIND_PLACED
        } else {
            KIND_GRENADE
        }
    }

    fn bounce(&self, surf: u8) -> (f32, f32) {
        let i = (surf as usize).min(SURF_TYPES - 1);
        let par = self.parallel_bounce.map_or(0.0, |a| a[i]);
        let perp = self.perpendicular_bounce.map_or(0.0, |a| a[i]);
        (par, perp)
    }

    /// Bounce sound alias for a surface type. Weapons whose definition carries none (the frag
    /// grenade in common_mp has no bounce sounds) borrow from another weapon with the same
    /// projectile model that does.
    pub fn bounce_sound(&self, surf: u32) -> Option<String> {
        let own = self.bounce_sounds.get(surf as usize).cloned().flatten();
        if own.is_some() || self.bounce_sounds.iter().any(Option::is_some) {
            return own;
        }
        let model = self.projectile_model.as_deref()?;
        let table = weapons::TABLE.read().ok()?;
        table
            .iter()
            .find(|r| r.equip.projectile_model.as_deref() == Some(model) && r.equip.bounce_sounds.iter().any(Option::is_some))
            .and_then(|r| r.equip.bounce_sounds.get(surf as usize).cloned().flatten())
    }
}

/// The shared equipment facts of a weapon-table entry.
pub fn equip_of(weapon: u32) -> Option<Arc<Equip>> {
    if weapon == 0 {
        return None;
    }
    weapons::TABLE.read().ok()?.get(weapon as usize - 1).map(|r| r.equip.clone())
}

// ---- vector helpers ---------------------------------------------------------------------------

fn len(v: [f32; 3]) -> f32 {
    (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt()
}

fn dot(a: [f32; 3], b: [f32; 3]) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

fn normalize(v: [f32; 3]) -> Option<[f32; 3]> {
    let l = len(v);
    (l >= 1e-6).then(|| [v[0] / l, v[1] / l, v[2] / l])
}

fn mad(base: [f32; 3], s: f32, dir: [f32; 3]) -> [f32; 3] {
    [base[0] + s * dir[0], base[1] + s * dir[1], base[2] + s * dir[2]]
}

fn forward(angles: [f32; 3]) -> [f32; 3] {
    math_iw4::angle_vectors(angles).0
}

/// IW4 axes (x forward, y left, z up) of an angle triple as a quaternion (x, y, z, w).
pub fn quat_from_angles(angles: [f32; 3]) -> [f32; 4] {
    let (f, right, u) = math_iw4::angle_vectors(angles);
    let l = [-right[0], -right[1], -right[2]];
    // Rotation matrix with columns f, l, u.
    let (m00, m01, m02) = (f[0], l[0], u[0]);
    let (m10, m11, m12) = (f[1], l[1], u[1]);
    let (m20, m21, m22) = (f[2], l[2], u[2]);
    let tr = m00 + m11 + m22;
    let q = if tr > 0.0 {
        let s = (tr + 1.0).sqrt() * 2.0;
        [(m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s]
    } else if m00 > m11 && m00 > m22 {
        let s = (1.0 + m00 - m11 - m22).sqrt() * 2.0;
        [0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s]
    } else if m11 > m22 {
        let s = (1.0 + m11 - m00 - m22).sqrt() * 2.0;
        [(m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s]
    } else {
        let s = (1.0 + m22 - m00 - m11).sqrt() * 2.0;
        [(m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s]
    };
    let n = (q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]).sqrt();
    if n > 0.0 { [q[0] / n, q[1] / n, q[2] / n, q[3] / n] } else { [0.0, 0.0, 0.0, 1.0] }
}

/// IW4L `sim::equipment::bounce_velocity` coefficients applied as MW2's weapon file names them:
/// the part of the velocity along the surface keeps `parallel`, the part into the surface comes
/// back out scaled by `perpendicular`. (IW4L reflects the whole vector and scales it by an
/// incidence-blended factor; both reduce to `perpendicular` for a head-on hit and `parallel`
/// for a graze.)
pub fn bounce_velocity(incoming: [f32; 3], normal: [f32; 3], parallel: f32, perpendicular: f32) -> [f32; 3] {
    let Some(n) = normalize(normal) else { return [0.0; 3] };
    let d = dot(incoming, n);
    let tangent = [incoming[0] - d * n[0], incoming[1] - d * n[1], incoming[2] - d * n[2]];
    let out = d.abs() * perpendicular;
    [tangent[0] * parallel + n[0] * out, tangent[1] * parallel + n[1] * out, tangent[2] * parallel + n[2] * out]
}

/// IW4L `sim::equipment::project_owner_velocity`: the part of the thrower's velocity along the
/// launch direction is added to the launch.
fn project_owner_velocity(launch: [f32; 3], owner: [f32; 3]) -> [f32; 3] {
    let Some(dir) = normalize(launch) else { return launch };
    let along = dot(owner, dir);
    mad(launch, along, dir)
}

/// IW4L `sim::equipment::grenade_launch_velocity`.
fn grenade_launch_velocity(direction: [f32; 3], eq: &Equip, owner: [f32; 3]) -> [f32; 3] {
    let speed = eq.speed as f32;
    let mut v = [direction[0] * speed, direction[1] * speed, direction[2] * speed + eq.speed_up as f32];
    if eq.speed_forward != 0
        && let Some(flat) = normalize([direction[0], direction[1], 0.0])
    {
        let extra = eq.speed_forward as f32;
        v = mad(v, extra, flat);
    }
    project_owner_velocity(v, owner)
}

fn flrand(rng: &mut Rng, min: f32, max: f32) -> f32 {
    min + (max - min) * (rng.next_u32() as f32 * (1.0 / 4_294_967_296.0))
}

// ---- the sweep the host provides --------------------------------------------------------------

/// A box sweep through the host's world (`Mw2TraceFn`, with the cube of `half` inches).
pub trait Sweep {
    fn sweep(&self, start: [f32; 3], end: [f32; 3], half: f32, mask: u32) -> Mw2Trace;
}

// ---- events and exported structs --------------------------------------------------------------

/// One live projectile as the host draws it.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2Missile {
    pub id: u32,
    /// Weapon table index (`mw2_weapon_index`); `mw2_weapon_projectile` names its model and trail.
    pub weapon: u32,
    pub origin: [f32; 3],
    /// Orientation in IW4 axes (x forward, y left, z up), xyzw.
    pub quat: [f32; 4],
    pub velocity: [f32; 3],
    /// 0 flying, 1 resting (bounced to a stop), 2 stuck (sticky / placed / embedded).
    pub state: u8,
    /// 0 grenade, 1 rocket, 2 placed (claymore / C4), 3 throwing knife.
    pub kind: u8,
    /// Bit 0: still inside MW2's launch no-draw window (`fire_grenade_no_draw_ms`, 20..50 ms);
    /// don't draw it yet.
    pub flags: u8,
    pub _pad: u8,
    /// Milliseconds until the fuse fires, -1 if it has none (or it was cleared when it settled).
    pub fuse_ms_left: i32,
}

/// Something that happened to a projectile. Drained with `mw2_missile_events`.
///
/// Per `kind`:
/// * 0 launch: `origin` is the start, `normal` the launch direction.
/// * 1 bounce: `surface` is the IW4 surface type (`mw2_weapon_bounce_sound`, `mw2_impact_fx`
///   with impact type 7 for the effect).
/// * 2 stick: stuck to a surface (Semtex, C4, claymore, throwing knife) or host `mw2_missile_attach`.
/// * 3 explode: `radius`/`radius_min`/`inner_damage`/`outer_damage` are the weapon's splash
///   numbers. A throwing knife has radius 0 and its damage in `inner_damage`/`outer_damage`
///   (only ever raised by `mw2_missile_detonate`, i.e. a hit on an enemy). `explosion_type`
///   (0 grenade, 1 rocket, 2 flashbang, 3 none (concussion / knife), 5 smoke as found in
///   common_mp) says what to show: flash and stun effects and smoke are the host's.
/// * 4 dud: hit something before its activation distance; no splash.
/// * 5 cookoff: a cooked grenade went off in the player's hand (`id` 0, `origin` the eye);
///   same numbers as an explode.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2MissileEvent {
    pub kind: u8,
    pub surface: u8,
    /// `EVF_*`.
    pub flags: u8,
    pub explosion_type: u8,
    pub id: u32,
    pub weapon: u32,
    pub origin: [f32; 3],
    pub normal: [f32; 3],
    pub radius: f32,
    pub radius_min: f32,
    pub inner_damage: f32,
    pub outer_damage: f32,
}

/// Static facts about a weapon's projectile / equipment for the host (`mw2_weapon_equipment`).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2Equipment {
    /// 0 none, 1 frag, 2 smoke button class (smoke, concussion), 3 flash, 4 throwing knife,
    /// 5 other lethal (semtex, claymore, C4).
    pub offhand_class: i32,
    /// `Mw2Missile::kind` this weapon launches.
    pub kind: u8,
    pub explosion_type: u8,
    /// 0 none, 1 any surface, 2 any surface oriented, 3 floor, 4 floor with yaw (claymore), 5 knife.
    pub stickiness: u8,
    /// Bit 0 cook-off hold, 1 timed detonation, 2 explodes on impact, 3 sticks to players,
    /// 4 has a detonator (C4), 5 tumbles in flight, 6 guided (needs a lock).
    pub flags: u8,
    pub fuse_ms: i32,
    pub speed: i32,
    pub activate_dist: i32,
    /// Direct-hit damage (MW2's `damage`): a rocket or knife touching an enemy.
    pub impact_damage: i32,
    pub radius: f32,
    pub radius_min: f32,
    pub inner_damage: f32,
    pub outer_damage: f32,
    pub cone_angle: f32,
}

// ---- projectiles ------------------------------------------------------------------------------

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Launch {
    Thrown { remaining_fuse_ms: Option<i32> },
    Launcher,
}

#[derive(Clone)]
struct Missile {
    id: u32,
    weapon: u32,
    eq: Arc<Equip>,
    kind: u8,
    pos: Trajectory,
    apos: Trajectory,
    origin: [f32; 3],
    launch_ms: i32,
    last_ms: i32,
    detonate_at: Option<i32>,
    cleanup_at: i32,
    travel: f32,
    live: bool,
    grounded: bool,
    state: u8,
    /// Fired with a Stinger / AT4 lock held: steers at `Missiles::guide`.
    homing: bool,
    /// A locked Javelin's top attack: the height it climbs to (IW4 units, z); NaN before it's set.
    apex_z: f32,
    /// Fire and forget: the target as last held, flown at after the lock is let go (aiming out
    /// after the shot took the target away and the Javelin climbed off at 50 degrees, 10-06-26).
    last_guide: Option<[f32; 3]>,
}

impl Missile {
    /// IW4L `ProjectileState::is_armed`.
    fn armed(&self) -> bool {
        self.live && (self.eq.activate_dist <= 0 || self.travel >= self.eq.activate_dist as f32)
    }

    fn velocity_at(&self, t: i32) -> [f32; 3] {
        evaluate_trajectory_delta(&self.pos, t)
    }

    fn orientation(&self, now: i32) -> [f32; 3] {
        if self.kind == KIND_ROCKET {
            let v = self.velocity_at(now);
            if len(v) > 1.0 {
                return math_iw4::vect_to_angles(v);
            }
        }
        evaluate_trajectory(&self.apos, now)
    }
}

/// IW4L `sticks_to_surface`.
fn sticks_to_surface(eq: &Equip, normal: [f32; 3]) -> bool {
    matches!(eq.stickiness, 1 | 2) || (matches!(eq.stickiness, 3 | 4) && normal[2] > 0.7)
}

/// IW4L `grenade_deadlines` (detonation time, cleanup time).
fn grenade_deadlines(eq: &Equip, launch: Launch, now: i32) -> (Option<i32>, i32) {
    let cap_at = now.saturating_add(GRENADE_FUSE_CAP_MS);
    if eq.is_throwing_knife() {
        return (None, cap_at);
    }
    match launch {
        Launch::Launcher if eq.fuse_time_ms <= 0 => {
            if eq.activate_dist != 0 {
                (None, cap_at)
            } else {
                (Some(now.saturating_add(GRENADE_DEFAULT_FUSE_MS).min(cap_at)), cap_at)
            }
        }
        Launch::Thrown { remaining_fuse_ms } => {
            let authored = eq.fuse_time_ms;
            let fuse = if eq.timed_detonation { remaining_fuse_ms.filter(|&ms| ms > 0).unwrap_or(authored) } else { authored };
            let fuse = if fuse <= 0 { GRENADE_DEFAULT_FUSE_MS } else { fuse.min(GRENADE_FUSE_CAP_MS) };
            (Some(now.saturating_add(fuse)), cap_at)
        }
        Launch::Launcher => (Some(now.saturating_add(eq.fuse_time_ms.min(GRENADE_FUSE_CAP_MS))), cap_at),
    }
}

/// IW4L `grenade_spin_rates`.
fn grenade_spin_rates(rng: &mut Rng) -> (f32, f32) {
    let pitch_sign = if rng.next_u32() & 1 == 0 { 1.0 } else { -1.0 };
    let pitch = flrand(rng, GRENADE_SPIN_PITCH_MIN, GRENADE_SPIN_PITCH_MAX);
    let roll_sign = if rng.next_u32() & 1 == 0 { 1.0 } else { -1.0 };
    let roll = flrand(rng, GRENADE_SPIN_ROLL_MIN, GRENADE_SPIN_ROLL_MAX);
    (pitch_sign * pitch, roll_sign * roll)
}

fn event(kind: u8, m: &Missile, origin: [f32; 3], normal: [f32; 3], surface: u8, flags: u8) -> Mw2MissileEvent {
    let eq = &m.eq;
    let (radius, radius_min, inner, outer) = if matches!(kind, EV_EXPLODE | EV_COOKOFF) {
        if eq.is_throwing_knife() {
            (0.0, 0.0, eq.impact_damage.max(0) as f32, eq.impact_damage.max(0) as f32)
        } else {
            (eq.radius.max(0) as f32, eq.radius_min.max(0) as f32, eq.inner_damage.max(0) as f32, eq.outer_damage.max(0) as f32)
        }
    } else {
        (0.0, 0.0, 0.0, 0.0)
    };
    Mw2MissileEvent {
        kind,
        surface,
        flags,
        explosion_type: eq.explosion_type.clamp(0, 255) as u8,
        id: m.id,
        weapon: m.weapon,
        origin,
        normal,
        radius,
        radius_min,
        inner_damage: inner,
        outer_damage: outer,
    }
}

/// IW4L `stick_missile`: freeze position and orientation where they are.
fn freeze(m: &mut Missile, t: i32, origin: [f32; 3]) {
    m.origin = origin;
    m.pos = Trajectory { tr_time: t, tr_type: TR_STATIONARY, tr_duration: 0, tr_delta: [0.0; 3], tr_base: origin };
    m.apos = Trajectory { tr_time: t, tr_type: TR_STATIONARY, tr_duration: 0, tr_delta: [0.0; 3], tr_base: evaluate_trajectory(&m.apos, t) };
}

/// IW4L `apply_missile_land_angles`.
fn land_angles(m: &mut Missile, hit_ms: i32, normal: [f32; 3], rng: &mut Rng) {
    let spin_random = rng.next_u32() as f32 * (1.0 / 4_294_967_296.0);
    let wall_spin_addend = ((rng.next_u32() & 0x7f) as i32 - 63) as f32;
    m.apos = missile_land_angles(MissileLandAnglesIn { apos: m.apos, normal, hit_time_ms: hit_ms, force_align: false, spin_random, wall_spin_addend }).apos;
}

/// All live projectiles of one player, and the events they raised.
#[derive(Default)]
pub struct Missiles {
    /// The lock the player holds (_stinger.gsc WeaponLockFinalize), IW4 world units: rockets fired
    /// while it's set fly at it. The host refreshes it every frame (the target moves).
    pub guide: Option<[f32; 3]>,
    list: Vec<Missile>,
    events: Vec<Mw2MissileEvent>,
    next_id: u32,
    now: i32,
}

impl Missiles {
    pub fn len(&self) -> usize {
        self.list.len()
    }

    pub fn is_empty(&self) -> bool {
        self.list.is_empty()
    }

    pub fn clear(&mut self) {
        self.list.clear();
    }

    pub fn take_events(&mut self, out: &mut Vec<Mw2MissileEvent>, cap: usize) {
        let n = self.events.len().min(cap);
        out.extend(self.events.drain(..n));
    }

    pub fn event_count(&self) -> usize {
        self.events.len()
    }

    fn push_event(&mut self, e: Mw2MissileEvent) {
        if self.events.len() >= MAX_EVENTS {
            self.events.remove(0);
        }
        self.events.push(e);
    }

    fn alloc(&mut self) -> u32 {
        self.next_id = self.next_id.wrapping_add(1).max(1);
        self.next_id
    }

    fn add(&mut self, m: Missile) {
        if self.list.len() >= MAX_MISSILES {
            // Drop the oldest projectile that is not a placed charge.
            if let Some(i) = self.list.iter().position(|x| x.kind != KIND_PLACED) {
                self.list.remove(i);
            } else {
                self.list.remove(0);
            }
        }
        self.list.push(m);
    }

    /// A thrown offhand weapon leaves the hand (IW4L `spawn_offhand_projectile`).
    pub fn throw(&mut self, weapon: u32, eq: Arc<Equip>, origin: [f32; 3], angles: [f32; 3], owner_vel: [f32; 3], remaining_fuse_ms: Option<i32>, now: i32, rng: &mut Rng) -> Option<u32> {
        self.spawn_grenade(weapon, eq, origin, angles, owner_vel, Launch::Thrown { remaining_fuse_ms }, now, rng)
    }

    /// A grenade launcher round (M79, Thumper, underbarrel GL): IW4L `GrenadeLaunchKind::Launcher`.
    pub fn fire_launcher(&mut self, weapon: u32, eq: Arc<Equip>, origin: [f32; 3], angles: [f32; 3], owner_vel: [f32; 3], now: i32, rng: &mut Rng) -> Option<u32> {
        self.spawn_grenade(weapon, eq, origin, angles, owner_vel, Launch::Launcher, now, rng)
    }

    #[allow(clippy::too_many_arguments)]
    fn spawn_grenade(&mut self, weapon: u32, eq: Arc<Equip>, origin: [f32; 3], angles: [f32; 3], owner_vel: [f32; 3], launch: Launch, now: i32, rng: &mut Rng) -> Option<u32> {
        if !eq.is_usable() {
            return None;
        }
        let direction = forward(angles);
        let (pitch_rate, roll_rate) = match launch {
            Launch::Launcher => (0.0, 0.0),
            Launch::Thrown { .. } if eq.is_throwing_knife() => (GRENADE_BLADE_SPIN_PITCH, 0.0),
            Launch::Thrown { .. } if eq.projectile_rotates => grenade_spin_rates(rng),
            Launch::Thrown { .. } => (0.0, 0.0),
        };
        let apos = if pitch_rate == 0.0 && roll_rate == 0.0 { fire_missile_apos(direction) } else { init_grenade_apos(direction, now, pitch_rate, roll_rate) };
        let velocity = grenade_launch_velocity(direction, &eq, owner_vel);
        let pos = init_grenade_pos(origin, velocity, now);
        let (detonate_at, cleanup_at) = grenade_deadlines(&eq, launch, now);
        let id = self.alloc();
        let kind = if launch == Launch::Launcher { KIND_GRENADE } else { eq.kind() };
        let m = Missile {
            id,
            weapon,
            kind,
            origin,
            launch_ms: now + fire_grenade_no_draw_ms(len(velocity)),
            last_ms: now,
            detonate_at,
            cleanup_at,
            travel: 0.0,
            live: true,
            grounded: false,
            state: STATE_FLYING,
            homing: false,
            apex_z: f32::NAN,
            last_guide: None,
            pos,
            apos,
            eq,
        };
        let dir = normalize(pos.tr_delta).unwrap_or(direction);
        self.push_event(event(EV_LAUNCH, &m, origin, dir, 0, 0));
        self.add(m);
        Some(id)
    }

    /// A rocket leaves the launcher (IW4L `sim::missile::fire_missile`). `dir` already carries the
    /// weapon's spread. Guided launchers fly straight until guidance is ported.
    #[allow(clippy::too_many_arguments)]
    pub fn fire_rocket(&mut self, weapon: u32, eq: Arc<Equip>, origin: [f32; 3], dir: [f32; 3], owner_vel: [f32; 3], now: i32) -> Option<u32> {
        if eq.speed <= 0 {
            return None;
        }
        let speed = eq.speed as f32;
        let mode = ROCKET_FLIGHT.load(Ordering::Relaxed);
        // IW4L: ballistic guidance (Javelin) launches lofted, dir + 0.3 up. Locked, the Javelin flies
        // its top attack instead (steered: `steer_javelin`), leaving the tube at its climb angle.
        let javelin = eq.guidance == 3 && self.guide.is_some();
        let dir = if javelin {
            let h = (dir[0] * dir[0] + dir[1] * dir[1]).sqrt().max(1e-4);
            let (c, s) = (JAVELIN_CLIMB_DEG.to_radians().cos(), JAVELIN_CLIMB_DEG.to_radians().sin());
            [dir[0] / h * c, dir[1] / h * c, s]
        } else if eq.guidance == 3 {
            normalize([dir[0], dir[1], dir[2] + 0.3]).unwrap_or(dir)
        } else {
            dir
        };
        let up = if mode == ROCKET_FLAT || javelin { 0.0 } else { eq.speed_up as f32 };
        let raw = [dir[0] * speed + owner_vel[0], dir[1] * speed + owner_vel[1], dir[2] * speed + up + owner_vel[2]];
        let ballistic = (eq.guidance == 3 && !javelin) || (mode == ROCKET_BALLISTIC && !eq.is_guided());
        let pos = Trajectory {
            tr_time: now,
            tr_type: if ballistic { TR_GRAVITY } else { TR_LINEAR },
            tr_duration: 0,
            tr_delta: truncated_tr_delta(raw),
            tr_base: origin,
        };
        let id = self.alloc();
        let m = Missile {
            id,
            weapon,
            kind: KIND_ROCKET,
            origin,
            launch_ms: now + fire_grenade_no_draw_ms(len(raw)),
            last_ms: now,
            detonate_at: None,
            cleanup_at: now.saturating_add(ROCKET_CLEANUP_MS),
            travel: 0.0,
            live: true,
            grounded: false,
            state: STATE_FLYING,
            homing: self.guide.is_some(),
            apex_z: f32::NAN,
            last_guide: self.guide,
            pos,
            apos: fire_missile_apos(dir),
            eq,
        };
        self.push_event(event(EV_LAUNCH, &m, origin, dir, 0, 0));
        self.add(m);
        Some(id)
    }

    /// A cooked grenade went off in the hand: IW4L `explode_offhand_in_hand`.
    pub fn cook_off(&mut self, weapon: u32, eq: Arc<Equip>, origin: [f32; 3], now: i32) {
        let m = Missile {
            id: 0,
            weapon,
            kind: KIND_GRENADE,
            origin,
            launch_ms: now,
            last_ms: now,
            detonate_at: None,
            cleanup_at: now,
            travel: 0.0,
            live: false,
            grounded: false,
            state: STATE_FLYING,
            homing: false,
            apex_z: f32::NAN,
            last_guide: None,
            pos: Trajectory::default(),
            apos: Trajectory::default(),
            eq,
        };
        self.push_event(event(EV_COOKOFF, &m, origin, [0.0, 0.0, 1.0], 0, 0));
    }

    /// Detonate one projectile on the host's word (it touched an enemy, or a C4 trigger).
    /// Returns false for an unknown id or one that already ended. A launcher round still inside
    /// its activation distance is a dud, as for a world hit.
    pub fn detonate(&mut self, id: u32) -> bool {
        let Some(i) = self.list.iter().position(|m| m.id == id && m.live) else { return false };
        let m = self.list.remove(i);
        let origin = m.origin;
        if m.eq.impact_explode && !m.armed() && !m.eq.is_placed() {
            self.push_event(event(EV_DUD, &m, origin, [0.0, 0.0, 1.0], 0, EVF_HOST));
        } else {
            self.push_event(event(EV_EXPLODE, &m, origin, [0.0, 0.0, 1.0], 0, EVF_HOST));
        }
        true
    }

    /// Detonate every placed charge of a weapon (C4's detonator). Returns how many went off.
    pub fn detonate_weapon(&mut self, weapon: u32) -> u32 {
        let ids: Vec<u32> = self.list.iter().filter(|m| m.weapon == weapon && m.kind == KIND_PLACED).map(|m| m.id).collect();
        ids.iter().filter(|&&id| self.detonate(id)).count() as u32
    }

    pub fn remove(&mut self, id: u32) -> bool {
        let before = self.list.len();
        self.list.retain(|m| m.id != id);
        self.list.len() != before
    }

    /// Stick a projectile where the host says it is (a Semtex that touched an enemy, then
    /// following that enemy each frame). Raises a stick event the first time.
    pub fn attach(&mut self, id: u32, origin: [f32; 3]) -> bool {
        let now = self.now;
        let Some(i) = self.list.iter().position(|m| m.id == id && m.live) else { return false };
        let first = self.list[i].state != STATE_STUCK;
        {
            let m = &mut self.list[i];
            freeze(m, now, origin);
            m.state = STATE_STUCK;
            if !m.eq.timed_detonation {
                m.detonate_at = None;
                m.cleanup_at = i32::MAX;
            }
        }
        if first {
            let m = self.list[i].clone();
            self.push_event(event(EV_STICK, &m, origin, [0.0, 0.0, 1.0], 0, EVF_HOST));
        }
        true
    }

    /// Fill `out` with the live projectiles; returns the number written.
    pub fn snapshot(&self, out: &mut [Mw2Missile]) -> usize {
        let now = self.now;
        let mut n = 0;
        for m in &self.list {
            let Some(slot) = out.get_mut(n) else { break };
            let flying = m.pos.tr_type != TR_STATIONARY;
            *slot = Mw2Missile {
                id: m.id,
                weapon: m.weapon,
                origin: m.origin,
                quat: quat_from_angles(m.orientation(now)),
                velocity: if flying { m.velocity_at(now) } else { [0.0; 3] },
                state: m.state,
                kind: m.kind,
                flags: u8::from(now < m.launch_ms),
                _pad: 0,
                fuse_ms_left: m.detonate_at.filter(|_| m.live).map_or(-1, |d| (d - now).max(0)),
            };
            n += 1;
        }
        n
    }

    /// Advance every projectile to `now` (sim clock, ms).
    pub fn step(&mut self, now: i32, world: &dyn Sweep, rng: &mut Rng) {
        self.now = now;
        let mut list = std::mem::take(&mut self.list);
        let mut events = std::mem::take(&mut self.events);
        let guide = self.guide;
        list.retain_mut(|m| {
            if m.homing {
                if guide.is_some() {
                    m.last_guide = guide;
                }
                if let Some(g) = m.last_guide {
                    if m.eq.guidance == 3 { steer_javelin(m, g, now, world) } else { steer(m, g, now) }
                }
            }
            think(m, now, world, rng, &mut events)
        });
        // Anything spawned while stepping (nothing today) is kept behind the survivors.
        list.append(&mut self.list);
        self.list = list;
        self.events.append(&mut events);
        let over = self.events.len().saturating_sub(MAX_EVENTS);
        if over > 0 {
            self.events.drain(..over);
        }
    }
}

/// How fast a locked rocket turns onto its target, degrees per second. MW2's engine guidance isn't
/// in the scripts (WeaponLockFinalize hands the lock to the missile): a tuned value, quick enough
/// to catch a RoR2 enemy that moves.
const HOMING_TURN_DEG_PER_S: f32 = 160.0;

/// A locked rocket turns toward `target` for this step: the straight-line flight re-based at where
/// it is now, its speed kept, its nose (`apos`) following.
fn steer(m: &mut Missile, target: [f32; 3], now: i32) {
    if !m.live || m.pos.tr_type != TR_LINEAR || now <= m.last_ms {
        return;
    }
    let v = m.pos.tr_delta;
    let speed = len(v);
    let (Some(cur), Some(want)) = (normalize(v), normalize([target[0] - m.origin[0], target[1] - m.origin[1], target[2] - m.origin[2]])) else { return };
    let cos = (cur[0] * want[0] + cur[1] * want[1] + cur[2] * want[2]).clamp(-1.0, 1.0);
    let angle = cos.acos();
    let max = HOMING_TURN_DEG_PER_S.to_radians() * (now - m.last_ms) as f32 / 1000.0;
    let dir = if angle <= max || angle < 1e-4 {
        want
    } else {
        // Rotate `cur` toward `want` by `max` within their plane.
        let t = max / angle;
        let (a, b) = (((1.0 - t) * angle).sin() / angle.sin(), (t * angle).sin() / angle.sin());
        normalize([cur[0] * a + want[0] * b, cur[1] * a + want[1] * b, cur[2] * a + want[2] * b]).unwrap_or(want)
    };
    m.pos = Trajectory { tr_time: m.last_ms, tr_type: TR_LINEAR, tr_duration: 0, tr_delta: [dir[0] * speed, dir[1] * speed, dir[2] * speed], tr_base: m.origin };
    m.apos = fire_missile_apos(dir);
}

/// The Javelin's top attack (playtest 10-06-26: "ive never seen the javelin lockon launch system
/// tested" - it flew IW4L's plain lob and never homed): up at its climb angle to a height over the
/// target, level toward it, then down onto it once it's within a 45-degree dive. MW2's climb comes
/// from engine dvars (missileJavelinClimb*), not the scripts: these values are approximate.
const JAVELIN_CLIMB_DEG: f32 = 50.0;
const JAVELIN_CLIMB_UNITS: f32 = 1600.0;
const JAVELIN_TURN_DEG_PER_S: f32 = 110.0;

fn steer_javelin(m: &mut Missile, target: [f32; 3], now: i32, world: &dyn Sweep) {
    if !m.live || m.pos.tr_type != TR_LINEAR || now <= m.last_ms {
        return;
    }
    let o = m.origin;
    let to = [target[0] - o[0], target[1] - o[1], target[2] - o[2]];
    let horiz = (to[0] * to[0] + to[1] * to[1]).sqrt();
    if m.apex_z.is_nan() {
        // Higher of where it left and the target, plus the climb (less on a short shot).
        m.apex_z = o[2].max(target[2]) + JAVELIN_CLIMB_UNITS.min(horiz * 0.7).max(300.0);
        // A roof over the climb (an overhang, a cave): MW2's DIR mode - straight in, no top attack
        // (climbing into the rock it went off short of the target, 10-06-26).
        let up = world.sweep(o, [o[0], o[1], m.apex_z + 64.0], MISSILE_HALF, MISSILE_MASK);
        let mid = [o[0] + to[0] * 0.5, o[1] + to[1] * 0.5, o[2]];
        let up_mid = world.sweep(mid, [mid[0], mid[1], m.apex_z + 64.0], MISSILE_HALF, MISSILE_MASK);
        if up.fraction < 1.0 || up_mid.fraction < 1.0 {
            m.apex_z = f32::NEG_INFINITY;
        }
    }
    let above = o[2] - target[2];
    let flat = if horiz > 1e-3 { [to[0] / horiz, to[1] / horiz] } else { [1.0, 0.0] };
    let want = if m.apex_z == f32::NEG_INFINITY || horiz <= above.max(0.0) + 64.0 {
        // The dive: straight at it.
        normalize(to).unwrap_or([0.0, 0.0, -1.0])
    } else if o[2] < m.apex_z - 32.0 {
        let (c, s) = (JAVELIN_CLIMB_DEG.to_radians().cos(), JAVELIN_CLIMB_DEG.to_radians().sin());
        [flat[0] * c, flat[1] * c, s]
    } else {
        [flat[0], flat[1], 0.0]
    };
    let v = m.pos.tr_delta;
    let speed = len(v);
    let Some(cur) = normalize(v) else { return };
    let cos = (cur[0] * want[0] + cur[1] * want[1] + cur[2] * want[2]).clamp(-1.0, 1.0);
    let angle = cos.acos();
    let max = JAVELIN_TURN_DEG_PER_S.to_radians() * (now - m.last_ms) as f32 / 1000.0;
    let dir = if angle <= max || angle < 1e-4 {
        want
    } else {
        let t = max / angle;
        let (a, b) = (((1.0 - t) * angle).sin() / angle.sin(), (t * angle).sin() / angle.sin());
        normalize([cur[0] * a + want[0] * b, cur[1] * a + want[1] * b, cur[2] * a + want[2] * b]).unwrap_or(want)
    };
    m.pos = Trajectory { tr_time: m.last_ms, tr_type: TR_LINEAR, tr_duration: 0, tr_delta: [dir[0] * speed, dir[1] * speed, dir[2] * speed], tr_base: m.origin };
    m.apos = fire_missile_apos(dir);
}

/// One projectile for one step: IW4L `sim::equipment::think_projectile` against the host trace.
/// Returns false when the projectile is gone.
fn think(m: &mut Missile, now: i32, world: &dyn Sweep, rng: &mut Rng, events: &mut Vec<Mw2MissileEvent>) -> bool {
    let prev = m.last_ms;
    if now <= prev {
        return true;
    }
    m.last_ms = now;
    if now >= m.cleanup_at && m.detonate_at.is_none_or(|d| d > m.cleanup_at || !m.live) {
        return false;
    }
    let eq = m.eq.clone();

    // The fuse can fall inside this step: evaluate the flight only up to it.
    let mut eval = now;
    if let Some(d) = m.detonate_at.filter(|&d| m.live && d <= now) {
        eval = eval.min(d).max(prev);
    }
    if m.cleanup_at > prev && m.cleanup_at <= eval {
        eval = m.cleanup_at;
    }
    let start = m.origin;
    let stationary = m.pos.tr_type == TR_STATIONARY;
    let end = if stationary { start } else { evaluate_trajectory(&m.pos, eval) };
    let tr = if stationary { Mw2Trace { fraction: 1.0, endpos: end, ..Default::default() } } else { world.sweep(start, end, MISSILE_HALF, MISSILE_MASK) };
    let hit = !stationary && (tr.fraction < 1.0 || tr.startsolid != 0);
    let (hit_end, normal, fraction) = if tr.startsolid != 0 {
        let d = [start[0] - end[0], start[1] - end[1], start[2] - end[2]];
        (start, normalize(d).unwrap_or([0.0, 0.0, 1.0]), 0.0)
    } else {
        (tr.endpos, tr.normal, tr.fraction)
    };
    let contact_ms = prev + ((eval - prev) as f32 * fraction) as i32;

    let mut fuse_due = m.live && m.detonate_at.is_some_and(|d| d <= now);
    // IW4L `waits_for_ground`: smoke-class fuses (smoke and concussion) are held until the
    // grenade has touched a floor.
    if fuse_due && !m.grounded && eq.offhand_class == OFFHAND_CLASS_SMOKE {
        fuse_due = false;
        m.detonate_at = Some(now.saturating_add(FUSE_RETRY_MS));
    }
    let contact_before_fuse = hit && m.detonate_at.is_none_or(|d| contact_ms <= d);

    if contact_before_fuse {
        return contact(m, hit_end, normal, fraction, tr.surface_flags, prev, eval, now, rng, events);
    }
    if fuse_due {
        let at = m.detonate_at.unwrap_or(eval).max(prev);
        let origin = if stationary { m.origin } else { evaluate_trajectory(&m.pos, at) };
        m.origin = origin;
        events.push(event(EV_EXPLODE, m, origin, [0.0, 0.0, 1.0], 0, 0));
        return false;
    }
    if now >= m.cleanup_at {
        return false;
    }
    if !stationary {
        m.travel += len([end[0] - start[0], end[1] - start[1], end[2] - start[2]]);
        m.origin = end;
    }
    true
}

/// A flying projectile met the world at `end` (fraction `fraction` of the way through the step).
#[allow(clippy::too_many_arguments)]
fn contact(m: &mut Missile, end: [f32; 3], normal: [f32; 3], fraction: f32, surface_flags: u32, prev: i32, eval: i32, now: i32, rng: &mut Rng, events: &mut Vec<Mw2MissileEvent>) -> bool {
    let eq = m.eq.clone();
    let surf = trace_iw4::surface_type_from_flags(surface_flags);
    let hit_ms = prev + ((eval - prev) as f32 * fraction) as i32;
    m.travel += len([end[0] - m.origin[0], end[1] - m.origin[1], end[2] - m.origin[2]]);
    m.origin = end;
    let armed = m.armed();

    if eq.is_throwing_knife() {
        return knife_impact(m, hit_ms, normal, surf, rng, events);
    }
    if armed && eq.impact_explode {
        events.push(event(EV_EXPLODE, m, end, normal, surf, 0));
        return false;
    }
    if sticks_to_surface(&eq, normal) {
        settle(m, hit_ms, end, normal, surf, rng, events);
        return true;
    }
    if !armed {
        // Hit something before the fuse armed (Thumper inside its activation distance): a dud.
        m.live = false;
        events.push(event(EV_DUD, m, end, normal, surf, 0));
        return false;
    }
    bounce(m, hit_ms, now, end, normal, surf, rng, events);
    true
}

/// IW4L `bounce_missile`, plus a rest rule for grenades that stop bouncing.
#[allow(clippy::too_many_arguments)]
fn bounce(m: &mut Missile, hit_ms: i32, _now: i32, end: [f32; 3], normal: [f32; 3], surf: u8, rng: &mut Rng, events: &mut Vec<Mw2MissileEvent>) {
    let floor = normal[2] > 0.7;
    m.grounded |= floor;
    let incoming = evaluate_trajectory_delta(&m.pos, hit_ms);
    let (par, perp) = m.eq.bounce(surf);
    let outgoing = bounce_velocity(incoming, normal, par, perp);
    m.origin = end;
    m.pos = Trajectory { tr_time: hit_ms, tr_type: TR_GRAVITY, tr_duration: 0, tr_delta: outgoing, tr_base: end };
    land_angles(m, hit_ms, normal, rng);
    let delta = len([outgoing[0] - incoming[0], outgoing[1] - incoming[1], outgoing[2] - incoming[2]]);
    if delta > BOUNCE_EVENT_SPEED_DELTA {
        events.push(event(EV_BOUNCE, m, end, normal, surf, 0));
    }
    if floor && len(outgoing) < REST_SPEED {
        // Lie flat where it stopped.
        let angles = missile_land_angles(MissileLandAnglesIn { apos: m.apos, normal, hit_time_ms: hit_ms, force_align: true, spin_random: 0.0, wall_spin_addend: 0.0 }).angles;
        freeze(m, hit_ms, end);
        m.apos.tr_base = angles;
        m.state = STATE_RESTING;
    }
}

/// IW4L `settle_equipment`: stick where it landed.
fn settle(m: &mut Missile, hit_ms: i32, end: [f32; 3], normal: [f32; 3], surf: u8, rng: &mut Rng, events: &mut Vec<Mw2MissileEvent>) {
    let eq = m.eq.clone();
    let yaw = evaluate_trajectory(&m.apos, hit_ms)[1];
    if eq.stickiness == 3 {
        land_angles(m, hit_ms, normal, rng);
    }
    let origin = mad(end, 0.25, normal);
    freeze(m, hit_ms, origin);
    if matches!(eq.stickiness, 2 | 4) {
        let mut angles = m.apos.tr_base;
        if eq.stickiness == 4 {
            angles = [math_iw4::pitch_for_yaw_on_normal(yaw, normal), yaw, 0.0];
        } else {
            let facing = forward(angles);
            let along = dot(facing, normal);
            angles = math_iw4::vect_to_angles([facing[0] - along * normal[0], facing[1] - along * normal[1], facing[2] - along * normal[2]]);
        }
        let (_, right, up) = math_iw4::angle_vectors(angles);
        angles[2] = dot(normal, right).atan2(dot(normal, up)).to_degrees();
        m.apos.tr_base = angles;
    }
    if !eq.timed_detonation {
        m.detonate_at = None;
        m.cleanup_at = i32::MAX;
    }
    m.grounded |= normal[2] > 0.7;
    m.state = STATE_STUCK;
    events.push(event(EV_STICK, m, origin, normal, surf, 0));
}

/// IW4L `knife_impact` against the world: bounce off glancing hits, embed in head-on ones, lie
/// flat on the floor.
fn knife_impact(m: &mut Missile, hit_ms: i32, normal: [f32; 3], surf: u8, rng: &mut Rng, events: &mut Vec<Mw2MissileEvent>) -> bool {
    let eq = m.eq.clone();
    let incoming = evaluate_trajectory_delta(&m.pos, hit_ms);
    let (par, perp) = eq.bounce(surf);
    let velocity = bounce_velocity(incoming, normal, par, perp);
    let speed = len(velocity);
    let direction = normalize(velocity).unwrap_or([0.0; 3]);
    let incidence = dot(direction, normal);
    let floor = normal[2] > 0.7;
    let stop = (floor && speed < 20.0) || incidence > 0.7;
    if !stop {
        m.origin = [m.origin[0] + normal[0] * 0.1, m.origin[1] + normal[1] * 0.1, m.origin[2] + (normal[2] * 0.1).min(0.0)];
        m.pos = Trajectory { tr_time: hit_ms, tr_type: TR_GRAVITY, tr_duration: 0, tr_delta: velocity, tr_base: m.origin };
        land_angles(m, hit_ms, normal, rng);
        events.push(event(EV_BOUNCE, m, m.origin, normal, surf, 0));
        return true;
    }
    let mut angles = evaluate_trajectory(&m.apos, hit_ms);
    let mut origin = m.origin;
    if floor && (speed < 20.0 || incidence < 0.7) {
        let f = forward(angles);
        let d = dot(f, normal);
        angles = math_iw4::vect_to_angles([f[0] - d * normal[0], f[1] - d * normal[1], f[2] - d * normal[2]]);
        let (_, right, up) = math_iw4::angle_vectors(angles);
        angles[2] = dot(normal, right).atan2(dot(normal, up)).to_degrees() + 90.0;
        origin[2] -= 1.0;
    } else {
        angles[0] = normal[2].atan2(normal[0].hypot(normal[1])).to_degrees() + flrand(rng, -15.0, 15.0);
        let f = forward(angles);
        origin = [origin[0] - normal[0] * 1.5 - f[0] * 4.5, origin[1] - normal[1] * 1.5 - f[1] * 4.5, origin[2] - normal[2] * 1.5 - f[2] * 4.5];
    }
    freeze(m, hit_ms, origin);
    m.apos.tr_base = angles;
    m.state = STATE_STUCK;
    events.push(event(EV_STICK, m, origin, normal, surf, 0));
    true
}

// ---- the offhand inventory --------------------------------------------------------------------

/// One offhand weapon the player carries.
#[derive(Clone, Copy, Debug, Default)]
pub struct OffhandSlot {
    pub weapon: u32,
    pub class: i32,
    pub ammo: i32,
    hold_fire_ms: i32,
    fire_time_ms: i32,
    fire_delay_ms: i32,
    fuse_ms: i32,
    cook_off_hold: bool,
    cancelable: bool,
    weap_type: i32,
    has_detonator: bool,
    detonate_delay_ms: i32,
    detonate_time_ms: i32,
}

fn scale_ms(ms: i32, rate: f32) -> i32 {
    let rate = if rate.is_finite() && rate > 0.01 { rate } else { 1.0 };
    if ms <= 0 || (rate - 1.0).abs() < 1e-4 { ms } else { ((ms as f32 / rate).round() as i32).max(1) }
}

impl OffhandSlot {
    pub fn new(weapon: u32, ammo: i32) -> Option<Self> {
        let row = weapons::row(weapon)?;
        let e = &row.equip;
        // A thrown weapon MW2 holds in the main hand (airdrop_marker_mp ...) has no offhand class;
        // the host throws it through the tactical slot, as smoke (OFFHAND_CLASS_SMOKE_GRENADE 2).
        let class = if e.offhand_class == 0 && row.facts.weap_type == weapon_iw4::WEAPTYPE_GRENADE { 2 } else { e.offhand_class };
        if class == 0 {
            return None;
        }
        Some(Self {
            weapon,
            class,
            ammo: ammo.max(0),
            hold_fire_ms: e.hold_fire_time_ms,
            fire_time_ms: row.facts.fire_time_ms,
            fire_delay_ms: row.facts.fire_delay_ms,
            fuse_ms: e.fuse_time_ms,
            cook_off_hold: e.cook_off_hold,
            // The cancel flag is Some for every MW2 weapon; None never panics IW4L's lookup.
            cancelable: row.facts.offhand_hold_is_cancelable.unwrap_or(false),
            weap_type: row.facts.weap_type,
            has_detonator: e.has_detonator,
            detonate_delay_ms: e.detonate_delay_ms,
            detonate_time_ms: e.detonate_time_ms,
        })
    }

    fn row(&self, rate: f32) -> OffhandInvRow {
        OffhandInvRow {
            weapon: self.weapon,
            offhand_class: self.class,
            ammo: self.ammo,
            hold_fire_time_ms: scale_ms(self.hold_fire_ms, rate),
            fire_time_ms: scale_ms(self.fire_time_ms, rate),
            fire_delay_ms: scale_ms(self.fire_delay_ms, rate),
            // The fuse is the grenade's own clock, not an animation: attack speed leaves it alone.
            fuse_time_ms: self.fuse_ms,
            cook_off_hold: self.cook_off_hold,
            offhand_hold_is_cancelable: Some(self.cancelable),
            weap_type: self.weap_type,
            has_detonator: self.has_detonator,
            detonate_delay_ms: scale_ms(self.detonate_delay_ms, rate),
            detonate_time_ms: scale_ms(self.detonate_time_ms, rate),
        }
    }
}

/// The lethal (`BUTTON_FRAG`) and tactical (`BUTTON_SMOKE`) equipment slots.
#[derive(Clone, Copy, Debug, Default)]
pub struct Offhand {
    pub lethal: Option<OffhandSlot>,
    pub tactical: Option<OffhandSlot>,
}

impl Offhand {
    fn slots(&self) -> impl Iterator<Item = &OffhandSlot> {
        self.lethal.iter().chain(self.tactical.iter())
    }

    /// The inventory `weapon_iw4`'s offhand state machine reads (IW4L `advance_weapon_command`).
    pub fn to_cmd(&self, ps: &PlayerState, held_quick_drop_ms: i32, fire_rate: f32) -> OffhandCmd {
        let mut inventory = [OffhandInvRow::default(); OFFHAND_INV_SLOTS];
        for (i, s) in self.slots().enumerate() {
            inventory[i] = s.row(fire_rate);
        }
        OffhandCmd {
            inventory,
            offhand_primary: ps.offhand_primary,
            offhand_secondary: ps.offhand_secondary,
            cmd_off_hand_index: 0,
            cmd_off_hand_owned: false,
            cursor_hint_ent: CURSOR_HINT_NONE,
            held_quick_drop_time_ms: held_quick_drop_ms,
            off_hand_index: ps.off_hand_index,
            grenade_time_left: ps.grenade_time_left,
        }
    }

    /// Take back the ammo the state machine spent (a throw or a cook-off).
    pub fn sync(&mut self, cmd: &OffhandCmd) {
        for s in self.lethal.iter_mut().chain(self.tactical.iter_mut()) {
            if let Some(r) = cmd.inventory.iter().find(|r| r.weapon == s.weapon) {
                s.ammo = r.ammo;
            }
        }
    }
}

/// Everything `Armed::tick` needs to hand projectiles to the sim.
pub struct Launcher<'a> {
    pub offhand: &'a mut Offhand,
    pub missiles: &'a mut Missiles,
    pub now: i32,
}

/// Rocket / grenade-launcher / offhand events out of a weapon tick, for `Armed::tick`.
pub fn on_offhand_event(l: &mut Launcher<'_>, ps: &PlayerState, event: weapon_iw4::WeaponTickEvent, rng: &mut Rng) -> u32 {
    use weapon_iw4::WeaponTickEvent as E;
    let eye = [ps.origin[0], ps.origin[1], ps.origin[2] + ps.view_height_current];
    match event {
        E::OffhandPrepare { .. } => weapons::EV_OFFHAND_PREPARE,
        E::OffhandUsed { weapon, remaining_fuse_ms } => {
            if let Some(eq) = equip_of(weapon) {
                l.missiles.throw(weapon, eq, eye, ps.viewangles, ps.velocity, remaining_fuse_ms, l.now, rng);
            }
            weapons::EV_OFFHAND_THROW
        }
        E::OffhandCookedOff { weapon } => {
            if let Some(eq) = equip_of(weapon) {
                l.missiles.cook_off(weapon, eq, eye, l.now);
            }
            weapons::EV_OFFHAND_COOKOFF
        }
        E::Detonated { weapon } => {
            l.missiles.detonate_weapon(weapon);
            0
        }
        _ => 0,
    }
}

/// Spawn what a gun shot launches (`FireWeaponKind::GrenadeLauncher` / `Missile`). Returns
/// false for bullets (the caller queues `Mw2Shot`s).
#[allow(clippy::too_many_arguments)]
pub fn fire_gun(l: &mut Launcher<'_>, weapon: u32, eq: &Arc<Equip>, kind: Option<weapon_iw4::FireWeaponKind>, origin: [f32; 3], angles: [f32; 3], spread_degrees: f32, owner_vel: [f32; 3], rng: &mut Rng) -> bool {
    use weapon_iw4::FireWeaponKind as K;
    match kind {
        Some(K::GrenadeLauncher) => {
            l.missiles.fire_launcher(weapon, eq.clone(), origin, angles, owner_vel, l.now, rng);
            true
        }
        Some(K::Missile) => {
            let dir = weapons::direction_on_plane(angles, spread_degrees, rng, weapon_iw4::ROCKET_SPREAD_PLANE);
            l.missiles.fire_rocket(weapon, eq.clone(), origin, dir, owner_vel, l.now);
            true
        }
        // A thrown-type weapon in the hands (the care package / sentry smoke markers): thrown on the
        // fire release like an offhand grenade, so it lands where the player aims it; the host calls
        // the drop where it goes off (_airdrop.gsc waits on its "explode").
        Some(K::ThrownGrenade) => {
            l.missiles.throw(weapon, eq.clone(), origin, angles, owner_vel, None, l.now, rng);
            true
        }
        Some(K::Bullet) | None => false,
    }
}

// ---- C ABI ------------------------------------------------------------------------------------

unsafe fn put(s: &str, out: *mut u8, cap: u32) -> u32 {
    if out.is_null() {
        return s.len() as u32;
    }
    let n = s.len().min(cap as usize);
    unsafe { std::ptr::copy_nonoverlapping(s.as_ptr(), out, n) };
    n as u32
}

/// Set the player's offhand equipment: the lethal slot (the "frag" button, `BUTTON_FRAG`
/// 0x4000) and the tactical slot (the "smoke" button, `BUTTON_SMOKE` 0x8000), as weapon-table
/// indices (`mw2_weapon_index`: `frag_grenade_mp`, `semtex_mp`, `throwingknife_mp`,
/// `claymore_mp`, `c4_mp`; `flash_grenade_mp`, `concussion_grenade_mp`, `smoke_grenade_mp`) with
/// how many the player carries. A weapon id of 0 empties the slot. Returns 1, or 0 for a null
/// sim or a weapon that isn't offhand equipment (nothing changes then).
///
/// Holding the button pulls the pin (the held gun lowers first), a weapon with MW2's cook-off
/// hold keeps cooking while the button stays down, and releasing throws. Cooking past the fuse
/// blows it up in the hand (a cook-off event). `OFFHAND_HOLD_CANCEL` 0x200000 cancels a pull in
/// progress for weapons that allow it. A C4 row stays with count 0 so its button detonates.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_offhand(sim: *mut Mw2Sim, lethal_weapon: u32, lethal_count: i32, tactical_weapon: u32, tactical_count: i32) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let slot = |w: u32, n: i32| if w == 0 { Some(None) } else { OffhandSlot::new(w, n).map(Some) };
        let (Some(lethal), Some(tactical)) = (slot(lethal_weapon, lethal_count), slot(tactical_weapon, tactical_count)) else { return 0 };
        sim.offhand = Offhand { lethal, tactical };
        sim.ps.offhand_primary = lethal.map_or(0, |s| s.class);
        sim.ps.offhand_secondary = tactical.map_or(0, |s| s.class);
        1
    }))
    .unwrap_or(0)
}

/// How many of each offhand weapon the player has left (a throw or cook-off spends one).
///
/// # Safety
/// `sim` from `mw2_create`; out pointers valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_offhand_ammo(sim: *const Mw2Sim, lethal: *mut i32, tactical: *mut i32) -> i32 {
    if sim.is_null() || lethal.is_null() || tactical.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &*sim };
        unsafe {
            *lethal = sim.offhand.lethal.map_or(0, |s| s.ammo);
            *tactical = sim.offhand.tactical.map_or(0, |s| s.ammo);
        }
        1
    }))
    .unwrap_or(0)
}

/// The weapon whose viewmodel should be shown right now: the offhand weapon during the pin pull,
/// hold, throw and detonator states, 0 while the held gun is on screen (including while it
/// lowers and raises around a throw). Swap the viewmodel rig when this changes; the sim's
/// `weap_anim` already carries the offhand weapon's animation events (`mw2_viewmodel_step`
/// reads them), so a freshly built rig starts on the pin-pull animation.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_offhand_viewmodel_weapon(sim: *const Mw2Sim) -> u32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ps = &unsafe { &*sim }.ps;
        if ps.weap_flags & playerstate_iw4::weap_flags::OFFHAND_VIEW != 0 { ps.off_hand_index.max(0) as u32 } else { 0 }
    }))
    .unwrap_or(0)
}

/// Copy up to `cap` live projectiles into `out` (null `out` just counts them). Returns the count.
///
/// # Safety
/// `sim` from `mw2_create`; `out` has room for `cap` entries.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_missiles(sim: *const Mw2Sim, out: *mut Mw2Missile, cap: u32) -> u32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &*sim };
        if out.is_null() {
            return sim.missiles.len() as u32;
        }
        let out = unsafe { std::slice::from_raw_parts_mut(out, cap as usize) };
        sim.missiles.snapshot(out) as u32
    }))
    .unwrap_or(0)
}

/// Move up to `cap` queued projectile events into `out` (oldest first) and drop them from the
/// queue; the rest stay queued. Returns the count copied.
///
/// # Safety
/// `sim` from `mw2_create`; `out` has room for `cap` events.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_missile_events(sim: *mut Mw2Sim, out: *mut Mw2MissileEvent, cap: u32) -> u32 {
    if sim.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &mut *sim };
        let mut taken = Vec::new();
        sim.missiles.take_events(&mut taken, cap as usize);
        for (i, e) in taken.iter().enumerate() {
            unsafe { *out.add(i) = *e };
        }
        taken.len() as u32
    }))
    .unwrap_or(0)
}

/// The host says this projectile touched an enemy (rocket, impact grenade, throwing knife) or a
/// C4 was triggered: it ends now with an explode event flagged `EVF_HOST` (a dud if a launcher
/// round is still inside its activation distance). Returns 1, or 0 for an unknown id.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_missile_detonate(sim: *mut Mw2Sim, id: u32) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| i32::from(unsafe { &mut *sim }.missiles.detonate(id)))).unwrap_or(0)
}

/// Detonate every placed charge (C4) of a weapon, e.g. for MW2's double-tap trigger. The offhand
/// state machine does this by itself when the player presses the equipment button with no C4 left
/// to throw. Returns how many exploded.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_detonate_placed(sim: *mut Mw2Sim, weapon: u32) -> u32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| unsafe { &mut *sim }.missiles.detonate_weapon(weapon))).unwrap_or(0)
}

/// Stick a projectile at `origin` (a Semtex that hit an enemy: call again every frame with the
/// enemy's new position to carry it). The sim's fuse keeps running. Raises a stick event once.
/// Returns 1, or 0 for an unknown id.
///
/// # Safety
/// `sim` from `mw2_create`; `origin` points to three floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_missile_attach(sim: *mut Mw2Sim, id: u32, origin: *const [f32; 3]) -> i32 {
    if sim.is_null() || origin.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| i32::from(unsafe { &mut *sim }.missiles.attach(id, unsafe { *origin })))).unwrap_or(0)
}

/// Remove a projectile without an event (a claymore the enemy destroyed, an expired host cleanup).
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_missile_remove(sim: *mut Mw2Sim, id: u32) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| i32::from(unsafe { &mut *sim }.missiles.remove(id)))).unwrap_or(0)
}

/// Remove every projectile (the player died or changed stage).
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_missiles_clear(sim: *mut Mw2Sim) {
    if !sim.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| unsafe { &mut *sim }.missiles.clear()));
    }
}

/// A weapon's in-flight model and trail effect, by name (no NUL): the projectile model
/// (`mw2_model_index` takes it), falling back to the rocket model; the trail effect
/// (`mw2_fx_find` takes it) goes to `trail_fx_out`. Returns the model name's length (0 = the
/// weapon has none); either buffer may be null. The trail's length alone is
/// `mw2_weapon_fx(weapon, 7, null, 0)`.
///
/// # Safety
/// The buffers have room for their caps or are null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_projectile(weapon: u32, model_out: *mut u8, cap: u32, trail_fx_out: *mut u8, trail_cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(eq) = equip_of(weapon) else { return 0 };
        if let Some(trail) = eq.trail_fx.as_deref() {
            if !trail_fx_out.is_null() {
                unsafe { put(trail, trail_fx_out, trail_cap) };
            }
        }
        match eq.projectile_model.as_deref().or(eq.rocket_model.as_deref()) {
            Some(m) => unsafe { put(m, model_out, cap) },
            None => 0,
        }
    }))
    .unwrap_or(0)
}

/// The bounce sound alias (`mw2_play_alias`) for a weapon on an IW4 surface type (0..=30,
/// `mw2_surface_name`). Returns the bytes written, 0 for none; `out` null returns the length.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_bounce_sound(weapon: u32, surface: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match equip_of(weapon).and_then(|e| e.bounce_sound(surface)) {
        Some(name) => unsafe { put(&name, out, cap) },
        None => 0,
    }))
    .unwrap_or(0)
}

/// Other sound aliases of a projectile weapon: `kind` 0 explosion, 1 flight loop (rocket
/// engine), 2 ignition. Returns the bytes written, 0 for none; `out` null returns the length.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_projectile_sound(weapon: u32, kind: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(eq) = equip_of(weapon) else { return 0 };
        let name = match kind {
            0 => eq.explosion_sound.as_deref(),
            1 => eq.projectile_sound.as_deref(),
            2 => eq.ignition_sound.as_deref(),
            _ => None,
        };
        match name {
            Some(n) => unsafe { put(n, out, cap) },
            None => 0,
        }
    }))
    .unwrap_or(0)
}

/// Static projectile / equipment facts of a weapon. Returns 1, or 0 for an unknown weapon.
///
/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_equipment(weapon: u32, out: *mut Mw2Equipment) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(e) = equip_of(weapon) else { return 0 };
        let flags = u8::from(e.cook_off_hold)
            | u8::from(e.timed_detonation) << 1
            | u8::from(e.impact_explode) << 2
            | u8::from(e.stick_to_players) << 3
            | u8::from(e.has_detonator) << 4
            | u8::from(e.projectile_rotates) << 5
            | u8::from(e.require_lock) << 6;
        unsafe {
            *out = Mw2Equipment {
                offhand_class: e.offhand_class,
                kind: e.kind(),
                explosion_type: e.explosion_type.clamp(0, 255) as u8,
                stickiness: e.stickiness.clamp(0, 255) as u8,
                flags,
                fuse_ms: e.fuse_time_ms,
                speed: e.speed,
                activate_dist: e.activate_dist,
                impact_damage: e.impact_damage,
                radius: e.radius as f32,
                radius_min: e.radius_min as f32,
                inner_damage: e.inner_damage as f32,
                outer_damage: e.outer_damage as f32,
                cone_angle: e.damage_cone_angle,
            };
        }
        1
    }))
    .unwrap_or(0)
}

/// Choose how rockets fly (process-wide): 1 ballistic (gravity, `projectile_speed_up` as the
/// initial loft), 0 IW4L's straight line with `projectile_speed_up` added to the climb, 2 straight
/// and flat (default: `projectile_speed_up` ignored). Guided launchers always fly straight.
/// Returns the previous mode.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_set_rocket_flight(mode: u32) -> u32 {
    ROCKET_FLIGHT.swap(mode.min(2), Ordering::Relaxed)
}

/// The rocket model MW2 shows loaded on a launcher's viewmodel (`rocketModel`, attached at the
/// gun's `tag_clip`). Returns the bytes written, 0 for none.
///
/// # Safety
/// `out` has room for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_rocket_model(weapon: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match equip_of(weapon).and_then(|e| e.rocket_model.clone()) {
        Some(m) => unsafe { put(&m, out, cap) },
        None => 0,
    }))
    .unwrap_or(0)
}

/// Sim time the rocket was last shown by a reload (not a loaded tube).
static RELOAD_ROCKET_AT: std::sync::atomic::AtomicI32 = std::sync::atomic::AtomicI32::new(i32::MIN);

/// Whether the held launcher's viewmodel shows its rocket now (IW4: loaded, or far enough into
/// the reload that the new one is in the hand). 1 / 0.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_rocket_visible(sim: *const Mw2Sim) -> i32 {
    if sim.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let sim = unsafe { &*sim };
        let Some(a) = sim.armed.as_ref() else { return 0 };
        let eq = &a.row.equip;
        if weapon_iw4::viewmodel_rocket_should_be_attached(a.hand.clip, a.hand.weaponstate, a.hand.weapon_time, eq.reload_time_ms, eq.reload_show_rocket_ms) {
            // Shown by the reload (not a loaded tube): remember when.
            if a.hand.clip <= 0 {
                RELOAD_ROCKET_AT.store(sim.server_time, std::sync::atomic::Ordering::Relaxed);
            }
            return 1;
        }
        // The tick the reload ends the state has left the reload family before the round is in the
        // tube: one frame with no rocket (playtest 10-06-26). Shown by a reload a moment ago: still shown.
        let at = RELOAD_ROCKET_AT.load(std::sync::atomic::Ordering::Relaxed);
        i32::from(a.hand.clip <= 0 && at != i32::MIN && sim.server_time.wrapping_sub(at) >= 0 && sim.server_time.wrapping_sub(at) <= 100)
    }))
    .unwrap_or(0)
}

const _: () = assert!(size_of::<Mw2Missile>() == 56);
const _: () = assert!(size_of::<Mw2MissileEvent>() == 52);
const _: () = assert!(size_of::<Mw2Equipment>() == 44);

#[cfg(test)]
mod tests;
