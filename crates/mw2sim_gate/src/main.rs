//! Gate test 1: run IW4L's MW2 player movement (`movement_iw4::pmove`) outside IW4L,
//! against a world that is a single infinite floor at z = 0, and measure what comes out.
//!
//! Units are IW4 map units (inches), Z up. The context values mirror IW4L's
//! `sim::step::pmove_context` with a neutral weapon (all move scales 1.0).

use movement_iw4::jump::JumpLaunchContext;
use movement_iw4::{
    ANGLE2SHORT, AdsFracContext, AdsIntentContext, AirMoveContext, CmdScaleWalkContext,
    CollisionBackend, FlatMantleAnimLength, GroundTraceInput, MeleeChargeWeaponDelays,
    MoveBounds, PmoveSingleContext, SprintContext, ViewAngleClamp, WalkMoveContext,
    ZeroMantleRootDelta, get_max_sprint_time, pmove,
};
use playerstate_iw4::{ENTITYNUM_NONE, PlayerState, UserCmd, buttons};
use trace_iw4::{ENTITYNUM_WORLD, HITTYPE_ENTITY, Trace};

const CONTENTS_SOLID: u32 = 1;
/// Quake-family traces stop this far short of the surface they hit.
const SURFACE_CLIP_EPSILON: f32 = 0.125;
const FRAME_MS: i32 = 8; // 125 fps, the classic CoD jump framerate

/// The whole world: solid below z = 0, empty above.
struct FlatFloor;

impl CollisionBackend for FlatFloor {
    fn trace(&self, input: GroundTraceInput) -> Trace {
        let start_bottom = input.start[2] + input.mins[2];
        let end_bottom = input.end[2] + input.mins[2];
        let mut trace = Trace {
            fraction: 1.0,
            endpos: input.end,
            ..Trace::default()
        };
        if start_bottom < 0.0 {
            trace.startsolid = 1;
            trace.allsolid = u8::from(end_bottom < 0.0);
            trace.fraction = 0.0;
            trace.endpos = input.start;
            trace.normal = [0.0, 0.0, 1.0];
            trace.contents = CONTENTS_SOLID;
            trace.walkable = 1;
            // World brushes report as entity hits on ENTITYNUM_WORLD (trace_iw4::brush_sweep_hit_kind).
            trace.hit_type = HITTYPE_ENTITY;
            trace.hit_id = ENTITYNUM_WORLD;
            return trace;
        }
        if end_bottom < SURFACE_CLIP_EPSILON && end_bottom < start_bottom {
            let travel = start_bottom - end_bottom;
            let fraction = ((start_bottom - SURFACE_CLIP_EPSILON) / travel).clamp(0.0, 1.0);
            trace.fraction = fraction;
            for axis in 0..3 {
                trace.endpos[axis] =
                    input.start[axis] + (input.end[axis] - input.start[axis]) * fraction;
            }
            trace.normal = [0.0, 0.0, 1.0];
            trace.contents = CONTENTS_SOLID;
            trace.walkable = 1;
            // World brushes report as entity hits on ENTITYNUM_WORLD (trace_iw4::brush_sweep_hit_kind).
            trace.hit_type = HITTYPE_ENTITY;
            trace.hit_id = ENTITYNUM_WORLD;
        }
        trace
    }
}

/// IW4L `sim::step::pmove_context` with a neutral weapon.
fn context(old_buttons: u32) -> PmoveSingleContext {
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
                weapon_move_speed_scale: 1.0,
                weapon_ads_move_speed_scale: 1.0,
                shellshock_affects_movement: false,
            },
            weapon_move_scale: 1.0,
            old_buttons,
            jump: JumpLaunchContext {
                jump_height: 39.0,
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
            weapon_max_sprint_time: get_max_sprint_time(1.0, 4.0),
            sprint_forever: false,
            min_sprint_time_seconds: 1.0,
            sprint_delay_seconds: 0.0,
            sprint_forward_minimum: 105,
            stand_up_clear: true,
            sprint_recharge_pause_seconds: 0.0,
        },
        ads_intent: AdsIntentContext {
            ads_allowed: true,
            weapon_def_scope: false,
            sprint_hold_ads: false,
        },
        ads_frac: AdsFracContext {
            aim_down_sight: true,
            ads_in_rate: 1.0,
            ads_out_rate: 1.0,
            ads_reload_trans_time_ms: 0,
            segmented_reload: false,
            rechamber_while_ads: false,
            ads_fire_only: false,
        },
        melee_charge: MeleeChargeWeaponDelays {
            melee_delay_ms: 0,
            melee_charge_delay_ms: 0,
        },
        player_melee_range: movement_iw4::MELEE_CHARGE_PLAYER_MELEE_RANGE_DEFAULT,
        old_buttons,
        weapon_blocks_prone: false,
        can_hold_breath: false,
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
    ps.ground_entity_num = 0x7fe;
    ps.other_flags |= playerstate_iw4::other_flags::PLAYER;
    ps.corpse_index = -1;
    ps
}

struct Sim {
    ps: PlayerState,
    time: i32,
    old_buttons: u32,
}

impl Sim {
    fn frame(&mut self, buttons: u32, forward: i8, right: i8, yaw_deg: f32) {
        self.time += FRAME_MS;
        let mut cmd = UserCmd {
            server_time: self.time,
            buttons,
            forwardmove: forward,
            rightmove: right,
            ..UserCmd::default()
        };
        cmd.angles[1] = (yaw_deg * ANGLE2SHORT) as i32 & 0xffff;
        let _ = pmove(
            &mut self.ps,
            &mut cmd,
            context(self.old_buttons),
            &FlatFloor,
            &FlatMantleAnimLength::default(),
            &ZeroMantleRootDelta,
        );
        self.old_buttons = buttons;
    }
    fn hspeed(&self) -> f32 {
        let v = self.ps.velocity;
        (v[0] * v[0] + v[1] * v[1]).sqrt()
    }
    fn grounded(&self) -> bool {
        self.ps.ground_entity_num != ENTITYNUM_NONE
    }
}

fn frames(seconds: f32) -> usize {
    (seconds * 1000.0 / FRAME_MS as f32) as usize
}

fn main() {
    let mut sim = Sim { ps: spawn([0.0, 0.0, 32.0]), time: 1000, old_buttons: 0 };
    sim.ps.command_time = sim.time;
    sim.ps.ground_entity_num = ENTITYNUM_NONE;

    // 1. Drop from 32 units and settle.
    for _ in 0..frames(1.0) {
        sim.frame(0, 0, 0, 0.0);
    }
    println!(
        "settle : z = {:.3}  grounded = {}  vz = {:.3}",
        sim.ps.origin[2], sim.grounded(), sim.ps.velocity[2]
    );

    // 2. Walk forward 2 s.
    for _ in 0..frames(2.0) {
        sim.frame(0, 127, 0, 0.0);
    }
    println!("walk   : speed = {:.2} u/s  x = {:.1}", sim.hspeed(), sim.ps.origin[0]);

    // 3. Sprint forward 1.5 s.
    for _ in 0..frames(1.5) {
        sim.frame(buttons::SPRINT, 127, 0, 0.0);
    }
    println!("sprint : speed = {:.2} u/s  pm_flags = {:#x}", sim.hspeed(), sim.ps.pm_flags);

    // 4. Stop, then one standing jump; record apex and airtime.
    for _ in 0..frames(1.5) {
        sim.frame(0, 0, 0, 0.0);
    }
    let base = sim.ps.origin[2];
    let (mut apex, mut air_frames, mut jumped) = (base, 0usize, false);
    for i in 0..frames(2.0) {
        let b = if i == 0 { buttons::JUMP } else { 0 };
        sim.frame(b, 0, 0, 0.0);
        apex = apex.max(sim.ps.origin[2]);
        if !sim.grounded() {
            air_frames += 1;
            jumped = true;
        } else if jumped {
            break;
        }
    }
    println!(
        "jump   : apex = {:.2} units above floor  airtime = {} ms",
        apex - base,
        air_frames as i32 * FRAME_MS
    );

    // 5. Strafe-jump: tap jump on every landing, hold forward + strafe, sweep yaw.
    let mut yaw = 0.0_f32;
    let mut peak = 0.0_f32;
    let mut jumps = 0;
    for _ in 0..frames(6.0) {
        let b = if sim.grounded() && sim.old_buttons & buttons::JUMP == 0 {
            jumps += 1;
            buttons::JUMP
        } else {
            0
        };
        let right: i8 = if (jumps % 2) == 0 { 127 } else { -127 };
        yaw += if right > 0 { -0.9 } else { 0.9 };
        sim.frame(b, 127, right, yaw);
        peak = peak.max(sim.hspeed());
    }
    println!("bhop   : {jumps} jumps  peak speed = {peak:.2} u/s");

    // 6. Air-strafe sweep: from a 190 u/s walk, hold forward+right and keep the
    //    wish direction a fixed offset from the velocity heading; tap jump on landing.
    for offset in [30.0_f32, 45.0, 60.0, 75.0, 85.0] {
        let mut s = Sim { ps: spawn([0.0, 0.0, 0.0]), time: 1000, old_buttons: 0 };
        s.ps.command_time = s.time;
        for _ in 0..frames(2.0) {
            s.frame(0, 127, 0, 0.0);
        }
        let (mut best, mut hops) = (0.0_f32, 0);
        for _ in 0..frames(4.0) {
            let v = s.ps.velocity;
            let heading = v[1].atan2(v[0]).to_degrees();
            // forward+right wishes 45 deg right of yaw; aim it `offset` deg right of travel.
            let yaw = heading - offset + 45.0;
            let b = if s.grounded() && s.old_buttons & buttons::JUMP == 0 {
                hops += 1;
                buttons::JUMP
            } else {
                0
            };
            s.frame(b, 127, 127, yaw);
            best = best.max(s.hspeed());
        }
        println!("strafe : offset {offset:>4.0} deg  {hops:>2} hops  peak = {best:.2} u/s  end = {:.2}", s.hspeed());
    }
}
