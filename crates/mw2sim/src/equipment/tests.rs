use std::ffi::c_void;

use super::*;
use crate::{Mw2Input, Mw2State, Mw2Trace, mw2_create, mw2_destroy, mw2_give_weapon, mw2_load_weapons, mw2_step, mw2_weapon_index};
use playerstate_iw4::buttons;

// ---- synthetic world and weapons (no game data needed) -----------------------------------------

/// A floor at z = 0 and optionally a wall at x = `wall_x`, both swept with the cube's half extent.
#[derive(Clone, Copy)]
struct World {
    floor: bool,
    wall_x: f32,
    floor_surface: u32,
    wall_surface: u32,
}

impl World {
    const FLAT: World = World { floor: true, wall_x: f32::INFINITY, floor_surface: 0, wall_surface: 0 };
    fn wall(x: f32) -> World {
        World { wall_x: x, ..World::FLAT }
    }

    fn trace(&self, s: [f32; 3], e: [f32; 3], half: f32) -> Mw2Trace {
        let mut best = Mw2Trace { fraction: 1.0, endpos: e, ..Default::default() };
        if self.floor {
            let (b0, b1) = (s[2] - half, e[2] - half);
            if b0 >= 0.0 && b1 < 0.0 {
                let f = b0 / (b0 - b1);
                if f < best.fraction {
                    best = Mw2Trace { fraction: f, normal: [0.0, 0.0, 1.0], surface_flags: self.floor_surface << 20, walkable: 1, ..Default::default() };
                }
            }
        }
        if self.wall_x.is_finite() {
            let (f0, f1) = (s[0] + half, e[0] + half);
            if f0 <= self.wall_x && f1 > self.wall_x {
                let f = (self.wall_x - f0) / (f1 - f0);
                if f < best.fraction {
                    best = Mw2Trace { fraction: f, normal: [-1.0, 0.0, 0.0], surface_flags: self.wall_surface << 20, ..Default::default() };
                }
            }
        }
        if best.fraction < 1.0 {
            best.endpos = [0, 1, 2].map(|i| s[i] + (e[i] - s[i]) * best.fraction);
        }
        best
    }
}

impl Sweep for World {
    fn sweep(&self, start: [f32; 3], end: [f32; 3], half: f32, _mask: u32) -> Mw2Trace {
        self.trace(start, end, half)
    }
}

fn bounce_table(v: f32) -> Option<[f32; SURF_TYPES]> {
    Some([v; SURF_TYPES])
}

/// frag_grenade_mp's numbers (common_mp.ff via `mw2weapons --show`).
fn frag() -> Arc<Equip> {
    Arc::new(Equip {
        offhand_class: 1,
        hold_fire_time_ms: 600,
        fuse_time_ms: 3500,
        cook_off_hold: true,
        projectile_rotates: true,
        timed_detonation: true,
        radius: 256,
        inner_damage: 150,
        outer_damage: 55,
        damage_cone_angle: 180.0,
        speed: 940,
        speed_up: 120,
        impact_damage: 15,
        weap_type: 1,
        weap_class: 6,
        parallel_bounce: bounce_table(0.5),
        perpendicular_bounce: bounce_table(0.25),
        ..Default::default()
    })
}

fn semtex() -> Arc<Equip> {
    Arc::new(Equip { offhand_class: 5, fuse_time_ms: 2000, cook_off_hold: false, stick_to_players: true, stickiness: 1, inner_damage: 200, ..(*frag()).clone() })
}

fn smoke() -> Arc<Equip> {
    Arc::new(Equip { offhand_class: 2, fuse_time_ms: 1000, cook_off_hold: false, radius: 0, inner_damage: 0, outer_damage: 0, speed: 960, explosion_type: 5, ..(*frag()).clone() })
}

fn rpg() -> Arc<Equip> {
    Arc::new(Equip {
        impact_explode: true,
        timed_detonation: false,
        projectile_rotates: false,
        fuse_time_ms: 0,
        radius: 400,
        inner_damage: 160,
        outer_damage: 60,
        speed: 1500,
        speed_up: 500,
        explosion_type: 1,
        impact_damage: 1000,
        weap_type: 2,
        weap_class: 7,
        ..(*frag()).clone()
    })
}

fn thumper() -> Arc<Equip> {
    Arc::new(Equip { offhand_class: 0, radius: 300, inner_damage: 155, outer_damage: 25, speed: 2400, speed_up: 10, activate_dist: 375, impact_damage: 135, weap_type: 2, weap_class: 6, ..(*rpg()).clone() })
}

fn claymore() -> Arc<Equip> {
    Arc::new(Equip { offhand_class: 5, fuse_time_ms: 3500, timed_detonation: false, projectile_rotates: false, stickiness: 4, speed: 0, speed_up: -10000, speed_forward: 2000, cook_off_hold: false, damage_cone_angle: 60.0, ..(*frag()).clone() })
}

fn knife() -> Arc<Equip> {
    Arc::new(Equip { offhand_class: 4, fuse_time_ms: 30000, cook_off_hold: false, stickiness: 5, speed: 1400, speed_up: 40, impact_damage: 135, radius: 0, inner_damage: 0, outer_damage: 0, weap_type: 1, weap_class: 9, explosion_type: 3, ..(*frag()).clone() })
}

fn rng() -> Rng {
    Rng::new(7)
}

/// Step a projectile list in `dt` ms slices up to `until_ms`, collecting (time, event).
fn run(m: &mut Missiles, world: &World, from: i32, until: i32, dt: i32, r: &mut Rng) -> Vec<(i32, Mw2MissileEvent)> {
    let mut log = Vec::new();
    let mut t = from;
    while t < until {
        t += dt;
        m.step(t, world, r);
        let mut got = Vec::new();
        m.take_events(&mut got, 64);
        log.extend(got.into_iter().map(|e| (t, e)));
    }
    log
}

fn kinds(log: &[(i32, Mw2MissileEvent)], kind: u8) -> Vec<i32> {
    log.iter().filter(|(_, e)| e.kind == kind).map(|(t, _)| *t).collect()
}

#[test]
fn abi_sizes() {
    assert_eq!(size_of::<Mw2Missile>(), 56);
    assert_eq!(size_of::<Mw2MissileEvent>(), 52);
    assert_eq!(size_of::<Mw2Equipment>(), 44);
    assert_eq!(crate::ABI_VERSION, 31);
}

#[test]
fn quaternion_maps_iw4_axes() {
    let id = quat_from_angles([0.0, 0.0, 0.0]);
    assert!((id[3].abs() - 1.0).abs() < 1e-5 && id[..3].iter().all(|v| v.abs() < 1e-5), "{id:?}");
    let rot = |q: [f32; 4], v: [f32; 3]| {
        let (x, y, z, w) = (q[0], q[1], q[2], q[3]);
        let t = [2.0 * (y * v[2] - z * v[1]), 2.0 * (z * v[0] - x * v[2]), 2.0 * (x * v[1] - y * v[0])];
        [v[0] + w * t[0] + (y * t[2] - z * t[1]), v[1] + w * t[1] + (z * t[0] - x * t[2]), v[2] + w * t[2] + (x * t[1] - y * t[0])]
    };
    // Yaw 90: IW4 forward (+x) turns to +y; up stays up.
    let q = quat_from_angles([0.0, 90.0, 0.0]);
    let f = rot(q, [1.0, 0.0, 0.0]);
    assert!(f[0].abs() < 1e-4 && (f[1] - 1.0).abs() < 1e-4, "{f:?}");
    let up = rot(q, [0.0, 0.0, 1.0]);
    assert!((up[2] - 1.0).abs() < 1e-4, "{up:?}");
    // The local y axis is the IW4 left: at yaw 0 it is world +y.
    let l = rot(quat_from_angles([0.0, 0.0, 0.0]), [0.0, 1.0, 0.0]);
    assert!((l[1] - 1.0).abs() < 1e-4, "{l:?}");
    // Pitch 45 (looking down): forward points down, as angle_vectors says.
    let f = rot(quat_from_angles([45.0, 0.0, 0.0]), [1.0, 0.0, 0.0]);
    let want = math_iw4::angle_vectors([45.0, 0.0, 0.0]).0;
    assert!((0..3).all(|i| (f[i] - want[i]).abs() < 1e-4), "{f:?} vs {want:?}");
    assert!(f[2] < -0.5);
}

#[test]
fn bounce_keeps_parallel_and_returns_perpendicular() {
    let out = bounce_velocity([100.0, 0.0, -100.0], [0.0, 0.0, 1.0], 0.5, 0.25);
    assert!((out[0] - 50.0).abs() < 1e-4 && out[1].abs() < 1e-4 && (out[2] - 25.0).abs() < 1e-4, "{out:?}");
    // A head-on hit comes straight back at the perpendicular fraction.
    let out = bounce_velocity([0.0, 0.0, -400.0], [0.0, 0.0, 1.0], 0.5, 0.25);
    assert!((out[2] - 100.0).abs() < 1e-3 && out[0].abs() < 1e-4, "{out:?}");
}

#[test]
fn launch_velocity_matches_iw4l() {
    let eq = frag();
    // Thrown level: 940 forward + 120 up.
    let v = grenade_launch_velocity([1.0, 0.0, 0.0], &eq, [0.0; 3]);
    assert_eq!(v, [940.0, 0.0, 120.0]);
    // The thrower's velocity along the launch direction is added.
    let v = grenade_launch_velocity([1.0, 0.0, 0.0], &eq, [190.0, 50.0, 0.0]);
    let dir = normalize([940.0, 0.0, 120.0]).unwrap();
    let along = dot([190.0, 50.0, 0.0], dir);
    assert!((v[0] - (940.0 + dir[0] * along)).abs() < 1e-3);
    // Claymore: all of it is the downward kick plus the forward push.
    let v = grenade_launch_velocity([1.0, 0.0, 0.0], &claymore(), [0.0; 3]);
    assert_eq!(v, [2000.0, 0.0, -10000.0]);
}

#[test]
fn cooked_frag_bounces_rests_and_explodes_on_its_fuse() {
    let w = World::FLAT;
    let mut m = Missiles::default();
    let mut r = rng();
    // Thrown at t = 10_000 with 2.5 s of fuse left (a 1 s cook of 3.5 s).
    m.throw(1, frag(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], Some(2500), 10_000, &mut r).unwrap();
    let log = run(&mut m, &w, 10_000, 14_000, 8, &mut r);
    let launch = kinds(&log, EV_LAUNCH);
    let bounces = kinds(&log, EV_BOUNCE);
    let explode = kinds(&log, EV_EXPLODE);
    assert_eq!(launch.len(), 1);
    assert!(!bounces.is_empty(), "no bounce: {log:?}");
    assert_eq!(explode.len(), 1);
    assert!((explode[0] - 12_500).abs() <= 8, "exploded at {}", explode[0]);
    let e = log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap().1;
    assert_eq!((e.radius, e.inner_damage, e.outer_damage), (256.0, 150.0, 55.0));
    assert!(e.origin[2] < 3.0 && e.origin[0] > 300.0, "should lie on the floor downrange: {:?}", e.origin);
    assert!(m.is_empty());
}

#[test]
fn frag_comes_to_rest_before_its_fuse() {
    let w = World::FLAT;
    let mut m = Missiles::default();
    let mut r = rng();
    m.throw(1, frag(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    run(&mut m, &w, 0, 3000, 8, &mut r);
    let mut out = [Mw2Missile::default(); 4];
    assert_eq!(m.snapshot(&mut out), 1);
    assert_eq!(out[0].state, STATE_RESTING, "{:?}", out[0]);
    assert_eq!(out[0].velocity, [0.0; 3]);
    assert!(out[0].fuse_ms_left > 0 && out[0].fuse_ms_left <= 500, "{:?}", out[0]);
    assert!((out[0].origin[2] - 1.0).abs() < 0.5, "{:?}", out[0].origin);
}

#[test]
fn frame_rate_does_not_move_the_blast() {
    let w = World::FLAT;
    let blast = |dt: i32| {
        let mut m = Missiles::default();
        let mut r = rng();
        m.throw(1, frag(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
        let log = run(&mut m, &w, 0, 5000, dt, &mut r);
        log.iter().find(|(_, e)| e.kind == EV_EXPLODE).map(|(t, e)| (*t, e.origin)).unwrap()
    };
    let (t8, o8) = blast(8);
    let (t16, o16) = blast(16);
    let (t50, o50) = blast(50);
    assert!((t8 - 3500).abs() <= 8 && (t16 - 3500).abs() <= 16 && (t50 - 3500).abs() <= 50, "{t8} {t16} {t50}");
    // The spin differs a little with the frame rate, but the grenade lands in the same region.
    let d = |a: [f32; 3], b: [f32; 3]| len([a[0] - b[0], a[1] - b[1], a[2] - b[2]]);
    assert!(d(o8, o16) < 120.0 && d(o8, o50) < 200.0, "{o8:?} {o16:?} {o50:?}");
}

#[test]
fn semtex_sticks_to_the_first_surface() {
    let w = World::wall(500.0);
    let mut m = Missiles::default();
    let mut r = rng();
    m.throw(2, semtex(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    let log = run(&mut m, &w, 0, 4000, 8, &mut r);
    let stick = log.iter().find(|(_, e)| e.kind == EV_STICK).expect("no stick event");
    assert!(stick.0 < 700, "stuck at {}", stick.0);
    assert!((stick.1.origin[0] - 498.75).abs() < 1.0, "{:?}", stick.1.origin);
    assert_eq!(stick.1.normal, [-1.0, 0.0, 0.0]);
    assert!(kinds(&log, EV_BOUNCE).is_empty());
    let explode = kinds(&log, EV_EXPLODE);
    assert_eq!(explode.len(), 1);
    assert!((explode[0] - 2000).abs() <= 8, "semtex exploded at {}", explode[0]);
}

#[test]
fn host_can_carry_a_semtex_on_an_enemy() {
    let w = World::FLAT;
    let mut m = Missiles::default();
    let mut r = rng();
    let id = m.throw(2, semtex(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    run(&mut m, &w, 0, 100, 8, &mut r);
    assert!(m.attach(id, [100.0, 0.0, 50.0]));
    assert!(m.attach(id, [110.0, 0.0, 50.0]));
    let log = run(&mut m, &w, 100, 2500, 8, &mut r);
    assert_eq!(kinds(&log, EV_STICK).len(), 1, "stick raised once: {log:?}");
    let e = log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap();
    assert_eq!(e.1.origin, [110.0, 0.0, 50.0]);
    assert!((e.0 - 2000).abs() <= 8);
}

#[test]
fn rocket_explodes_on_a_wall() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner()); // ROCKET_FLIGHT is global
    let w = World::wall(1000.0);
    for (mode, label) in [(ROCKET_BALLISTIC, "ballistic"), (ROCKET_LINEAR_WITH_UP, "iw4l-linear"), (ROCKET_FLAT, "flat")] {
        ROCKET_FLIGHT.store(mode, Ordering::Relaxed);
        let mut m = Missiles::default();
        let mut r = rng();
        m.fire_rocket(3, rpg(), [0.0, 0.0, 60.0], [1.0, 0.0, 0.0], [0.0; 3], 0).unwrap();
        let log = run(&mut m, &w, 0, 3000, 8, &mut r);
        let e = log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap_or_else(|| panic!("{label}: no explosion {log:?}"));
        assert!((e.1.origin[0] - 999.0).abs() < 1.0, "{label}: {:?}", e.1.origin);
        assert_eq!((e.1.radius, e.1.inner_damage, e.1.explosion_type), (400.0, 160.0, 1));
        eprintln!("{label}: RPG hits the wall at t={} ms, height {:.0} (aimed at 60)", e.0, e.1.origin[2]);
    }
    ROCKET_FLIGHT.store(ROCKET_FLAT, Ordering::Relaxed);
}

#[test]
fn launcher_round_is_a_dud_inside_its_activation_distance() {
    let mut r = rng();
    let mut m = Missiles::default();
    m.fire_launcher(4, thumper(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], 0, &mut r).unwrap();
    let log = run(&mut m, &World::wall(300.0), 0, 1000, 8, &mut r);
    assert_eq!(kinds(&log, EV_DUD).len(), 1, "{log:?}");
    assert!(kinds(&log, EV_EXPLODE).is_empty());
    assert!(m.is_empty());

    let mut m = Missiles::default();
    m.fire_launcher(4, thumper(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], 0, &mut r).unwrap();
    let log = run(&mut m, &World::wall(1000.0), 0, 1000, 8, &mut r);
    assert!(kinds(&log, EV_DUD).is_empty());
    assert_eq!(kinds(&log, EV_EXPLODE).len(), 1, "{log:?}");
}

#[test]
fn claymore_lands_and_waits_for_the_host() {
    let w = World::FLAT;
    let mut m = Missiles::default();
    let mut r = rng();
    // Thrown level at yaw 90: faces +y, falls to the floor.
    m.throw(5, claymore(), [0.0, 0.0, 60.0], [0.0, 90.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    let log = run(&mut m, &w, 0, 20_000, 16, &mut r);
    assert_eq!(kinds(&log, EV_STICK).len(), 1, "{log:?}");
    assert!(kinds(&log, EV_EXPLODE).is_empty(), "a claymore never times out");
    let mut out = [Mw2Missile::default(); 2];
    assert_eq!(m.snapshot(&mut out), 1);
    assert_eq!((out[0].state, out[0].kind, out[0].fuse_ms_left), (STATE_STUCK, KIND_PLACED, -1));
    // Facing +y: the quaternion turns +x onto +y.
    let q = out[0].quat;
    let fy = 2.0 * (q[0] * q[1] + q[3] * q[2]);
    assert!(fy > 0.9, "{q:?}");
    assert!(m.detonate_weapon(5) == 1);
    let mut got = Vec::new();
    m.take_events(&mut got, 8);
    assert_eq!(got.last().map(|e| (e.kind, e.flags)), Some((EV_EXPLODE, EVF_HOST)));
}

#[test]
fn knife_embeds_and_damages_only_on_the_hosts_word() {
    let w = World::wall(500.0);
    let mut m = Missiles::default();
    let mut r = rng();
    let id = m.throw(6, knife(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    let log = run(&mut m, &w, 0, 2000, 8, &mut r);
    assert_eq!(kinds(&log, EV_STICK).len(), 1, "{log:?}");
    assert!(kinds(&log, EV_EXPLODE).is_empty());
    assert_eq!(m.len(), 1);
    // An enemy in its path instead:
    let mut m = Missiles::default();
    let id2 = m.throw(6, knife(), [0.0, 0.0, 60.0], [0.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    run(&mut m, &World::FLAT, 0, 100, 8, &mut r);
    assert!(m.detonate(id2));
    let mut got = Vec::new();
    m.take_events(&mut got, 8);
    let e = got.last().unwrap();
    assert_eq!((e.kind, e.radius, e.inner_damage, e.outer_damage, e.flags), (EV_EXPLODE, 0.0, 135.0, 135.0, EVF_HOST));
    let _ = id;
}

#[test]
fn smoke_fuse_waits_for_the_ground() {
    let mut m = Missiles::default();
    let mut r = rng();
    // Thrown steeply upward: the 1 s fuse falls while it is still in the air.
    m.throw(7, smoke(), [0.0, 0.0, 60.0], [-80.0, 0.0, 0.0], [0.0; 3], None, 0, &mut r).unwrap();
    let log = run(&mut m, &World::FLAT, 0, 8000, 8, &mut r);
    let explode = kinds(&log, EV_EXPLODE);
    let bounce = kinds(&log, EV_BOUNCE);
    assert_eq!(explode.len(), 1, "{log:?}");
    assert!(explode[0] > 1000 + 50, "{explode:?}");
    assert!(bounce.first().is_some_and(|&b| b <= explode[0]), "it should have touched down first: {bounce:?} {explode:?}");
}

#[test]
fn rockets_fly_like_iw4l() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner()); // ROCKET_FLIGHT is global
    // Default: straight along the aim, like a bullet (where the crosshair is).
    ROCKET_FLIGHT.store(ROCKET_FLAT, Ordering::Relaxed);
    let mut m = Missiles::default();
    m.fire_rocket(8, rpg(), [0.0, 0.0, 60.0], [1.0, 0.0, 0.0], [0.0; 3], 0).unwrap();
    let log = run(&mut m, &World::wall(1000.0), 0, 5000, 8, &mut rng());
    let e = log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap();
    assert!((e.1.origin[2] - 60.0).abs() < 1.0, "on the aim line: {:?}", e.1.origin);
    // IW4L's mode: TR_LINEAR with projectile_speed_up in the launch velocity (climbs).
    ROCKET_FLIGHT.store(ROCKET_LINEAR_WITH_UP, Ordering::Relaxed);
    let mut m = Missiles::default();
    m.fire_rocket(8, rpg(), [0.0, 0.0, 60.0], [1.0, 0.0, 0.0], [0.0; 3], 0).unwrap();
    let log = run(&mut m, &World::wall(1000.0), 0, 5000, 8, &mut rng());
    ROCKET_FLIGHT.store(ROCKET_FLAT, Ordering::Relaxed);
    let e = log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap();
    let eq = rpg();
    let expect = 60.0 + 1000.0 * eq.speed_up as f32 / eq.speed as f32;
    assert!((e.1.origin[2] - expect).abs() < 20.0, "straight climb: {:?} vs z {expect}", e.1.origin);
    // Ballistic guidance (Javelin): lofted (dir + 0.3 up) on TR_GRAVITY, lands short of a far wall.
    let mut jav = (*rpg()).clone();
    jav.require_lock = true;
    jav.guidance = 3;
    jav.speed = 600;
    let mut m = Missiles::default();
    m.fire_rocket(8, Arc::new(jav), [0.0, 0.0, 60.0], [1.0, 0.0, 0.0], [0.0; 3], 0).unwrap();
    let log = run(&mut m, &World { floor: true, ..World::wall(f32::INFINITY) }, 0, 8000, 8, &mut rng());
    let e = log.iter().find(|(_, e)| e.kind == EV_EXPLODE).expect("javelin comes down");
    assert!(e.1.origin[2] < 5.0 && e.1.origin[0] > 100.0, "arc to the floor: {:?}", e.1.origin);
}

// ---- the real game data (run: cargo test --release -p mw2sim equipment::tests::real -- --ignored --nocapture) ----

const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

struct Rig {
    sim: *mut crate::Mw2Sim,
    world: Box<World>,
    t: i32,
    msec: i32,
    log: Vec<(i32, Mw2MissileEvent)>,
    state: Mw2State,
    max_missiles: usize,
}

extern "C" fn trace_cb(user: *mut c_void, start: *const [f32; 3], end: *const [f32; 3], mins: *const [f32; 3], maxs: *const [f32; 3], _mask: u32, out: *mut Mw2Trace) {
    let (w, s, e, mins, maxs) = unsafe { (&*(user as *const World), *start, *end, *mins, *maxs) };
    // The player box and the projectile cube both reduce to a half extent on the axis they sweep.
    let mut best = Mw2Trace { fraction: 1.0, endpos: e, ..Default::default() };
    if w.floor {
        let (b0, b1) = (s[2] + mins[2], e[2] + mins[2]);
        if b0 >= 0.0 && b1 < 0.0 {
            best = Mw2Trace { fraction: b0 / (b0 - b1), normal: [0.0, 0.0, 1.0], surface_flags: w.floor_surface << 20, walkable: 1, ..Default::default() };
        }
    }
    if w.wall_x.is_finite() {
        let (f0, f1) = (s[0] + maxs[0], e[0] + maxs[0]);
        if f0 <= w.wall_x && f1 > w.wall_x {
            let f = (w.wall_x - f0) / (f1 - f0);
            if f < best.fraction {
                best = Mw2Trace { fraction: f, normal: [-1.0, 0.0, 0.0], surface_flags: w.wall_surface << 20, ..Default::default() };
            }
        }
    }
    if best.fraction < 1.0 {
        best.endpos = [0, 1, 2].map(|i| s[i] + (e[i] - s[i]) * best.fraction);
    }
    unsafe { *out = best };
}

impl Rig {
    fn new(world: World, msec: i32) -> Rig {
        let sim = unsafe { mw2_create(&[0.0, 0.0, 0.0]) };
        Rig { sim, world: Box::new(world), t: 0, msec, log: Vec::new(), state: Mw2State::default(), max_missiles: 0 }
    }

    fn give(&mut self, name: &str) -> u32 {
        let idx = unsafe { mw2_weapon_index(name.as_ptr(), name.len()) };
        assert!(idx != 0, "{name}");
        assert_eq!(unsafe { mw2_give_weapon(self.sim, idx) }, 1);
        idx
    }

    fn step(&mut self, buttons: u32, yaw: f32, pitch: f32) {
        let input = Mw2Input { msec: self.msec, buttons, yaw, pitch, speed_scale: 1.0, fire_rate: 1.0, ..Default::default() };
        let user = &*self.world as *const World as *mut c_void;
        assert_eq!(unsafe { mw2_step(self.sim, &input, Some(trace_cb), user, &mut self.state) }, 1);
        self.t += self.msec;
        let mut buf = [Mw2MissileEvent::default(); 32];
        let n = unsafe { mw2_missile_events(self.sim, buf.as_mut_ptr(), 32) } as usize;
        for e in &buf[..n] {
            self.log.push((self.t, *e));
        }
        self.max_missiles = self.max_missiles.max(unsafe { mw2_missiles(self.sim, std::ptr::null_mut(), 0) } as usize);
    }

    fn idle(&mut self, ms: i32) {
        for _ in 0..ms / self.msec {
            self.step(0, 0.0, 0.0);
        }
    }

    fn missiles(&self) -> Vec<Mw2Missile> {
        let mut out = [Mw2Missile::default(); 16];
        let n = unsafe { mw2_missiles(self.sim, out.as_mut_ptr(), 16) } as usize;
        out[..n].to_vec()
    }

    /// Run until no projectile is alive (or `limit` ms), printing the first projectile's path
    /// every 100 ms.
    fn follow(&mut self, limit: i32, label: &str) {
        let start = self.t;
        let mut next = 0;
        while self.t - start < limit {
            self.step(0, 0.0, 0.0);
            let dt = self.t - start;
            if dt >= next {
                next += 100;
                if let Some(m) = self.missiles().first() {
                    eprintln!(
                        "  {label} +{dt:4} ms  id {} state {} kind {} flags {} at ({:8.1} {:8.1} {:7.1})  v ({:7.1} {:7.1} {:7.1})  fuse {}",
                        m.id, m.state, m.kind, m.flags, m.origin[0], m.origin[1], m.origin[2], m.velocity[0], m.velocity[1], m.velocity[2], m.fuse_ms_left
                    );
                }
            }
            if self.missiles().is_empty() && self.t - start > 200 {
                break;
            }
        }
    }

    fn events(&self, from: usize) -> Vec<String> {
        const NAMES: [&str; 6] = ["launch", "bounce", "stick", "explode", "dud", "cookoff"];
        self.log[from..]
            .iter()
            .map(|(t, e)| {
                format!(
                    "t={t} {} id {} surf {} flags {} xtype {} at ({:.1} {:.1} {:.1}) n ({:.2} {:.2} {:.2}) r {}/{} dmg {}/{}",
                    NAMES[e.kind as usize], e.id, e.surface, e.flags, e.explosion_type, e.origin[0], e.origin[1], e.origin[2], e.normal[0], e.normal[1], e.normal[2], e.radius, e.radius_min, e.inner_damage, e.outer_damage
                )
            })
            .collect()
    }
}

impl Drop for Rig {
    fn drop(&mut self) {
        unsafe { mw2_destroy(self.sim) };
    }
}

fn string(f: impl Fn(*mut u8, u32) -> u32) -> Option<String> {
    let mut buf = [0u8; 200];
    let n = f(buf.as_mut_ptr(), 200) as usize;
    (n > 0).then(|| String::from_utf8_lossy(&buf[..n]).into_owned())
}

fn surface_index(name: &str) -> u32 {
    (0..31).find(|&i| crate::weapon_fx::surface_name(i) == Some(name)).unwrap_or(0)
}

#[test]
#[ignore]
fn real_install_grenades_equipment_and_launchers() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        eprintln!("common_mp.ff not installed; skipping");
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    crate::fx::mw2_fx_init();
    let idx = |n: &str| unsafe { mw2_weapon_index(n.as_ptr(), n.len()) };
    let concrete = surface_index("concrete");
    let brick = surface_index("brick");
    let world = World { floor: true, wall_x: f32::INFINITY, floor_surface: concrete, wall_surface: brick };
    eprintln!("surfaces: floor concrete={concrete} wall brick={brick}");

    // -- projectile model, trail and sound names for every kind of equipment -------------------
    eprintln!("\n== projectile models / trail fx / bounce sounds ==");
    for n in [
        "frag_grenade_mp", "frag_grenade_short_mp", "semtex_mp", "throwingknife_mp", "claymore_mp", "c4_mp", "flash_grenade_mp", "concussion_grenade_mp",
        "smoke_grenade_mp", "rpg_mp", "at4_mp", "javelin_mp", "stinger_mp", "m79_mp", "gl_mp",
    ] {
        let w = idx(n);
        assert!(w != 0, "{n}");
        let model = string(|o, c| unsafe { mw2_weapon_projectile(w, o, c, std::ptr::null_mut(), 0) });
        let trail = string(|o, c| unsafe { crate::weapon_fx::mw2_weapon_fx(w, 7, o, c) });
        let mut both = [0u8; 200];
        let ml = unsafe { mw2_weapon_projectile(w, std::ptr::null_mut(), 0, both.as_mut_ptr(), 200) };
        let _ = ml;
        let trail2 = String::from_utf8_lossy(&both).trim_end_matches('\0').to_string();
        assert_eq!(trail.clone().unwrap_or_default(), trail2, "{n}: trail via both exports");
        let model_id = model.as_deref().map_or(0, |m| unsafe { crate::mw2_model_index(m.as_ptr(), m.len()) });
        let trail_id = trail.as_deref().map_or(0, |t| unsafe { crate::fx::mw2_fx_find(t.as_ptr(), t.len()) });
        let explosion = string(|o, c| unsafe { crate::weapon_fx::mw2_weapon_fx(w, 6, o, c) });
        let explosion_id = explosion.as_deref().map_or(0, |t| unsafe { crate::fx::mw2_fx_find(t.as_ptr(), t.len()) });
        assert!(explosion.is_none() || explosion_id != 0, "{n}: explosion fx {explosion:?} not found");
        assert!(trail.is_none() || trail_id != 0, "{n}: trail fx {trail:?} not found");
        assert!(model_id != 0, "{n}: model {model:?} is not in the mesh table");
        let bounce = string(|o, c| unsafe { mw2_weapon_bounce_sound(w, concrete, o, c) });
        let boom = string(|o, c| unsafe { mw2_weapon_projectile_sound(w, 0, o, c) });
        let loop_snd = string(|o, c| unsafe { mw2_weapon_projectile_sound(w, 1, o, c) });
        let mut info = Mw2Equipment::default();
        assert_eq!(unsafe { mw2_weapon_equipment(w, &mut info) }, 1);
        eprintln!(
            "{n:24} model {:?} (mesh id {model_id})  trail {:?} (fx id {trail_id})  explosion fx {:?} (fx id {})  bounce(concrete) {:?}  boom {:?} loop {:?}  [class {} kind {} xtype {} stick {} fuse {} speed {} act {} impact dmg {}]",
            model, trail, explosion, explosion_id, bounce, boom, loop_snd, info.offhand_class, info.kind, info.explosion_type, info.stickiness, info.fuse_ms, info.speed, info.activate_dist, info.impact_damage
        );
        assert!(model.is_some(), "{n} has no projectile model");
    }

    // -- A: frag, 1 s cook, then the 4 s cook-off ----------------------------------------------
    for msec in [8, 16] {
        eprintln!("\n== frag grenade, press / hold 1 s / release  ({msec} ms frames) ==");
        let mut rig = Rig::new(world, msec);
        rig.give("ak47_mp");
        let (frag, flash) = (idx("frag_grenade_mp"), idx("flash_grenade_mp"));
        assert_eq!(unsafe { mw2_set_offhand(rig.sim, frag, 2, flash, 2) }, 1);
        rig.idle(1500);
        let press = rig.t;
        let mut hold_start = None;
        let mut states = Vec::new();
        let vm_gun = crate::mw2_viewmodel_build(idx("ak47_mp"));
        let vm_frag = crate::mw2_viewmodel_build(frag);
        assert!(!vm_gun.is_null() && !vm_frag.is_null());
        let mut pose = vec![0f32; 4096 * 7];
        let mut last_playing = (usize::MAX, usize::MAX);
        let mut vm_step = |rig: &Rig, label: &str| {
            let which = unsafe { mw2_offhand_viewmodel_weapon(rig.sim) };
            let vm = if which == frag { vm_frag } else { vm_gun };
            unsafe { crate::mw2_viewmodel_step(vm, rig.sim, rig.msec as f32 / 1000.0, pose.as_mut_ptr()) };
            let slot = unsafe { &*vm }.playing().map_or(usize::MAX, |p| p.0);
            if (slot, which as usize) != last_playing {
                last_playing = (slot, which as usize);
                eprintln!("  {label} +{:4} ms viewmodel {} plays slot {:#x} (0x3 fire, 0x4 hold_fire, 0x13 quick_raise, 0x14 quick_drop, 0x10 drop)", rig.t - press, if which == frag { "frag" } else { "gun" }, slot);
            }
        };
        while hold_start.is_none() {
            rig.step(buttons::FRAG, 0.0, 0.0);
            if msec == 8 {
                vm_step(&rig, "pull");
            }
            if states.last() != Some(&rig.state.weaponstate) {
                states.push(rig.state.weaponstate);
                let vm = unsafe { mw2_offhand_viewmodel_weapon(rig.sim) };
                eprintln!("  +{:4} ms weapon state {:#x}  viewmodel weapon {}", rig.t - press, rig.state.weaponstate, if vm == 0 { "held gun".to_string() } else { format!("offhand {vm} ({})", if vm == frag { "frag" } else { "?" }) });
            }
            if rig.state.weaponstate == 0x12 {
                hold_start = Some(rig.t);
            }
            assert!(rig.t - press < 4000, "never reached the hold state");
        }
        let hold_start = hold_start.unwrap();
        while rig.t - hold_start < 1000 {
            rig.step(buttons::FRAG, 0.0, 0.0);
            if msec == 8 {
                vm_step(&rig, "hold");
            }
        }
        let release = rig.t;
        let mark = rig.log.len();
        let mut stage = rig.state.weaponstate;
        for _ in 0..140 {
            rig.step(0, 0.0, 0.0);
            if msec == 8 {
                vm_step(&rig, "throw");
            }
            if rig.state.weaponstate != stage {
                stage = rig.state.weaponstate;
                eprintln!("  +{:4} ms weapon state {:#x}  viewmodel weapon {}", rig.t - press, stage, unsafe { mw2_offhand_viewmodel_weapon(rig.sim) });
            }
        }
        unsafe {
            crate::mw2_viewmodel_destroy(vm_gun);
            crate::mw2_viewmodel_destroy(vm_frag);
        }
        rig.follow(6000, "frag");
        for line in rig.events(mark) {
            eprintln!("  {line}");
        }
        let launch = rig.log[mark..].iter().find(|(_, e)| e.kind == EV_LAUNCH).map(|x| x.0).expect("launch");
        let explode = rig.log[mark..].iter().find(|(_, e)| e.kind == EV_EXPLODE).map(|x| x.0).expect("explode");
        eprintln!(
            "  press t={press}  hold started +{}  released +{}  launched +{} (throw delay {} ms)  exploded +{} = {} ms after the hold began (fuse 3500)",
            hold_start - press, release - press, launch - press, launch - release, explode - press, explode - hold_start
        );
        assert!(((explode - hold_start) - 3500).abs() <= msec + 2, "cooked frag should go off 3.5 s after the hold began");
        let (mut l, mut t) = (0, 0);
        unsafe { mw2_offhand_ammo(rig.sim, &mut l, &mut t) };
        eprintln!("  ammo after throw: frag {l} flash {t}");
        assert_eq!((l, t), (1, 2));
        assert!(rig.log[mark..].iter().any(|(_, e)| e.kind == EV_BOUNCE));
    }

    eprintln!("\n== frag grenade held 4 s: cook-off in the hand ==");
    {
        let mut rig = Rig::new(world, 8);
        rig.give("ak47_mp");
        let (frag, flash) = (idx("frag_grenade_mp"), idx("flash_grenade_mp"));
        unsafe { mw2_set_offhand(rig.sim, frag, 2, flash, 2) };
        rig.idle(1500);
        let press = rig.t;
        let mut hold_start = 0;
        while rig.t - press < 7000 {
            rig.step(buttons::FRAG, 0.0, 0.0);
            if rig.state.weaponstate == 0x12 && hold_start == 0 {
                hold_start = rig.t;
            }
            if rig.log.iter().any(|(_, e)| e.kind == EV_COOKOFF) {
                break;
            }
        }
        for line in rig.events(0) {
            eprintln!("  {line}");
        }
        let c = rig.log.iter().find(|(_, e)| e.kind == EV_COOKOFF).expect("cook-off");
        eprintln!("  cook-off +{} ms after the hold began, weapon state now {:#x}, events {:#x}", c.0 - hold_start, rig.state.weaponstate, rig.state.events);
        assert!(((c.0 - hold_start) - 3500).abs() <= 10);
        let (mut l, mut t) = (0, 0);
        unsafe { mw2_offhand_ammo(rig.sim, &mut l, &mut t) };
        assert_eq!((l, t), (1, 2), "the cooked grenade is spent");
        rig.step(0, 0.0, 0.0);
        rig.idle(1500);
        eprintln!("  after release: weapon state {:#x}, viewmodel weapon {}", rig.state.weaponstate, unsafe { mw2_offhand_viewmodel_weapon(rig.sim) });
        assert_eq!(rig.state.weaponstate, 0);
        assert!(rig.missiles().is_empty());
    }

    eprintln!("
== semtex pin pull cancelled (OFFHAND_HOLD_CANCEL; MW2 allows it for semtex, not for frag) ==");
    {
        let mut rig = Rig::new(world, 8);
        rig.give("ak47_mp");
        let (semtex, flash) = (idx("semtex_mp"), idx("flash_grenade_mp"));
        unsafe { mw2_set_offhand(rig.sim, semtex, 2, flash, 2) };
        rig.idle(1500);
        while rig.state.weaponstate != 0x12 {
            rig.step(buttons::FRAG, 0.0, 0.0);
        }
        for _ in 0..20 {
            rig.step(buttons::FRAG | buttons::OFFHAND_HOLD_CANCEL, 0.0, 0.0);
        }
        rig.step(0, 0.0, 0.0);
        rig.idle(1500);
        let (mut l, mut t) = (0, 0);
        unsafe { mw2_offhand_ammo(rig.sim, &mut l, &mut t) };
        eprintln!("  weapon state {:#x}, semtex {l}, projectiles {}, events {:?}", rig.state.weaponstate, rig.missiles().len(), rig.events(0));
        assert_eq!((rig.state.weaponstate, l, rig.missiles().len()), (0, 2, 0));
    }

    // -- B: flash (tactical button), then semtex at a wall ---------------------------------------
    eprintln!("\n== flash grenade on the smoke button ==");
    {
        let mut rig = Rig::new(world, 8);
        rig.give("ak47_mp");
        let (frag, flash) = (idx("frag_grenade_mp"), idx("flash_grenade_mp"));
        unsafe { mw2_set_offhand(rig.sim, frag, 2, flash, 2) };
        rig.idle(1500);
        let mut hold = 0;
        let press = rig.t;
        for _ in 0..3000 / 8 {
            let b = if hold == 0 { buttons::SMOKE } else { 0 };
            rig.step(b, 0.0, 0.0);
            if rig.state.weaponstate == 0x12 && hold == 0 {
                hold = rig.t;
            }
        }
        for line in rig.events(0) {
            eprintln!("  {line}");
        }
        let launch = rig.log.iter().find(|(_, e)| e.kind == EV_LAUNCH).unwrap().0;
        let boom = rig.log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap();
        eprintln!("  pressed +0, hold state +{}, launched +{}, exploded +{} ({} ms after launch; fuse 1500), xtype {}", hold - press, launch - press, boom.0 - press, boom.0 - launch, boom.1.explosion_type);
        assert_eq!(boom.1.explosion_type, 2);
        assert!(((boom.0 - launch) - 1500).abs() <= 10);
    }

    eprintln!("\n== semtex thrown at a brick wall 500 units away ==");
    {
        let mut rig = Rig::new(World { wall_x: 500.0, ..world }, 8);
        rig.give("ak47_mp");
        let (semtex, flash) = (idx("semtex_mp"), idx("flash_grenade_mp"));
        assert_eq!(unsafe { mw2_set_offhand(rig.sim, semtex, 1, flash, 1) }, 1);
        rig.idle(1500);
        let mut released = false;
        for _ in 0..2500 / 8 {
            let b = if !released { buttons::FRAG } else { 0 };
            rig.step(b, 0.0, 0.0);
            if rig.state.weaponstate == 0x12 {
                released = true;
            }
        }
        rig.follow(4000, "semtex");
        for line in rig.events(0) {
            eprintln!("  {line}");
        }
        let launch = rig.log.iter().find(|(_, e)| e.kind == EV_LAUNCH).unwrap().0;
        let stick = rig.log.iter().find(|(_, e)| e.kind == EV_STICK).expect("stick");
        let boom = rig.log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap();
        eprintln!("  launched t={launch}  stuck +{} at x={:.1}  exploded +{} after launch (fuse 2000)", stick.0 - launch, stick.1.origin[0], boom.0 - launch);
        assert!(!rig.log.iter().any(|(_, e)| e.kind == EV_BOUNCE));
        assert!(((boom.0 - launch) - 2000).abs() <= 10);
    }

    // -- C: claymore and C4 -----------------------------------------------------------------------
    eprintln!("\n== claymore then C4 (detonator through the offhand button) ==");
    {
        let mut rig = Rig::new(world, 8);
        rig.give("ak47_mp");
        let (clay, c4) = (idx("claymore_mp"), idx("c4_mp"));
        unsafe { mw2_set_offhand(rig.sim, c4, 1, 0, 0) };
        rig.idle(1500);
        let tap = |rig: &mut Rig, ms: i32| {
            let mut left = ms;
            while left > 0 {
                rig.step(buttons::FRAG, 0.0, 0.0);
                left -= rig.msec;
            }
        };
        // C4: press and release (hold state), then it flies.
        let mut released = false;
        for _ in 0..3000 / 8 {
            let b = if !released { buttons::FRAG } else { 0 };
            rig.step(b, 0.0, 0.0);
            if rig.state.weaponstate == 0x12 {
                released = true;
            }
        }
        rig.idle(2000);
        let placed = rig.missiles();
        eprintln!("  c4 on the ground: {:?}", placed.iter().map(|m| (m.state, m.kind, m.fuse_ms_left, m.origin)).collect::<Vec<_>>());
        assert_eq!(placed.len(), 1);
        assert_eq!((placed[0].state, placed[0].kind, placed[0].fuse_ms_left), (STATE_STUCK, KIND_PLACED, -1));
        let (mut l, mut t) = (9, 9);
        unsafe { mw2_offhand_ammo(rig.sim, &mut l, &mut t) };
        assert_eq!(l, 0);
        // Out of C4 to throw: the button is the detonator.
        let mark = rig.log.len();
        for _ in 0..40 {
            rig.step(buttons::FRAG, 0.0, 0.0);
        }
        tap(&mut rig, 16);
        rig.idle(1500);
        for line in rig.events(mark) {
            eprintln!("  {line}");
        }
        assert!(rig.log[mark..].iter().any(|(_, e)| e.kind == EV_EXPLODE && e.flags & EVF_HOST != 0), "detonator did not fire the C4");
        assert!(rig.missiles().is_empty());

        unsafe { mw2_set_offhand(rig.sim, clay, 1, 0, 0) };
        let mark = rig.log.len();
        let mut released = false;
        for _ in 0..3000 / 8 {
            let b = if !released { buttons::FRAG } else { 0 };
            rig.step(b, 90.0, 0.0);
            if rig.state.weaponstate == 0x12 {
                released = true;
            }
        }
        rig.idle(3000);
        for line in rig.events(mark) {
            eprintln!("  {line}");
        }
        let m = rig.missiles();
        eprintln!("  claymore: {:?}", m.iter().map(|m| (m.state, m.kind, m.origin, m.quat)).collect::<Vec<_>>());
        assert_eq!(m.len(), 1);
        assert_eq!((m[0].state, m[0].kind), (STATE_STUCK, KIND_PLACED));
    }

    // -- D: throwing knife at a wall -----------------------------------------------------------------
    eprintln!("\n== throwing knife at a wall 600 units away ==");
    {
        let mut rig = Rig::new(World { wall_x: 600.0, ..world }, 8);
        rig.give("ak47_mp");
        let (knife, flash) = (idx("throwingknife_mp"), idx("flash_grenade_mp"));
        assert_eq!(unsafe { mw2_set_offhand(rig.sim, knife, 1, flash, 1) }, 1);
        rig.idle(1500);
        let mut released = false;
        for _ in 0..2500 / 8 {
            let b = if !released { buttons::FRAG } else { 0 };
            rig.step(b, 0.0, 0.0);
            if rig.state.weaponstate == 0x12 {
                released = true;
            }
        }
        rig.idle(600);
        for line in rig.events(0) {
            eprintln!("  {line}");
        }
        let m = rig.missiles();
        eprintln!("  knife: {:?}", m.iter().map(|m| (m.state, m.kind, m.origin)).collect::<Vec<_>>());
        assert!(m.iter().any(|m| m.kind == KIND_KNIFE));
    }

    // -- E: launchers ---------------------------------------------------------------------------------
    for (name, wall, label) in [("rpg_mp", 1000.0, "RPG into a wall 1000 units away"), ("at4_mp", 1000.0, "AT4 into a wall 1000 units away"), ("m79_mp", 300.0, "Thumper (M79) inside its 375 unit activation distance"), ("m79_mp", 1000.0, "Thumper (M79) at a wall 1000 units away")] {
        eprintln!("\n== {label} ==");
        let mut rig = Rig::new(World { wall_x: wall, ..world }, 8);
        let w = rig.give(name);
        rig.idle(1800);
        let clip_before = rig.state.clip;
        // MW2 launchers are ads-fire-only: aim, then pull the trigger.
        for _ in 0..80 {
            rig.step(buttons::ADS, 0.0, 0.0);
        }
        rig.step(buttons::ADS | buttons::ATTACK, 0.0, 0.0);
        for _ in 0..60 {
            rig.step(buttons::ADS, 0.0, 0.0);
        }
        rig.step(0, 0.0, 0.0);
        let shots = {
            let mut s = [crate::weapons::Mw2Shot::default(); 4];
            unsafe { crate::mw2_take_shots(rig.sim, s.as_mut_ptr(), 4) }
        };
        eprintln!("  clip {clip_before} -> {}, bullet shots queued: {shots}", rig.state.clip);
        assert_eq!(shots, 0, "a launcher queues no bullets");
        rig.follow(4000, name);
        for line in rig.events(0) {
            eprintln!("  {line}");
        }
        let model = string(|o, c| unsafe { mw2_weapon_projectile(w, o, c, std::ptr::null_mut(), 0) });
        let trail = string(|o, c| unsafe { crate::weapon_fx::mw2_weapon_fx(w, 7, o, c) });
        eprintln!("  projectile model {model:?}  trail fx {trail:?}");
        let dud = rig.log.iter().any(|(_, e)| e.kind == EV_DUD);
        let boom = rig.log.iter().any(|(_, e)| e.kind == EV_EXPLODE);
        match (name, wall as i32) {
            ("m79_mp", 300) => assert!(dud && !boom),
            _ => assert!(boom && !dud),
        }
    }

    // -- F: frame-rate sanity of the whole chain with 16.7 ms frames ----------------------------------------
    let mut rig = Rig::new(world, 17);
    rig.give("ak47_mp");
    let frag = idx("frag_grenade_mp");
    unsafe { mw2_set_offhand(rig.sim, frag, 1, 0, 0) };
    rig.idle(1530);
    let mut released = false;
    for _ in 0..200 {
        let b = if !released { buttons::FRAG } else { 0 };
        rig.step(b, 0.0, 0.0);
        if rig.state.weaponstate == 0x12 {
            released = true;
        }
    }
    rig.follow(5000, "frag@17ms");
    let launch = rig.log.iter().find(|(_, e)| e.kind == EV_LAUNCH).unwrap().0;
    let boom = rig.log.iter().find(|(_, e)| e.kind == EV_EXPLODE).unwrap().0;
    eprintln!("\n17 ms frames: launched t={launch}, exploded {} ms later (a tap throw leaves 3500 - release-to-throw delay)", boom - launch);
}

/// MW2 melee on the real ak47_mp: a stab with no target (melee_time / melee_delay), then a lunge
/// at a target 120 units ahead (melee_charge anim, pmove carries the player toward it).
#[test]
#[ignore]
fn real_install_melee_stab_and_lunge() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    let world = World { floor: true, wall_x: f32::INFINITY, floor_surface: 0, wall_surface: 0 };
    for (label, dist) in [("stab", 0.0f32), ("lunge", 120.0)] {
        let mut rig = Rig::new(world, 8);
        let ak = rig.give("ak47_mp");
        assert!(unsafe { crate::mw2_play_weapon_sound(ak, crate::weapons::SOUND_MELEE as u32, 0.0) } == 1, "ak47 has a melee swing sound");
        rig.idle(1500);
        let x0 = rig.state.origin[0];
        unsafe { crate::weapons::mw2_set_melee_target(rig.sim, 0.0, dist) };
        let (mut start, mut charge, mut hit) = (None, false, None);
        let mut max_speed = 0f32;
        for i in 0..150 {
            rig.step(if i < 2 { buttons::MELEE_CHARGE } else { 0 }, 0.0, 0.0);
            let ev = rig.state.events;
            if ev & crate::weapons::EV_MELEE_START != 0 {
                start = Some(rig.t);
                charge = ev & crate::weapons::EV_MELEE_CHARGE != 0;
            }
            if ev & crate::weapons::EV_MELEE_HIT != 0 && hit.is_none() {
                hit = Some(rig.t);
            }
            max_speed = max_speed.max(rig.state.velocity[0].hypot(rig.state.velocity[1]));
        }
        let moved = rig.state.origin[0] - x0;
        eprintln!("{label}: start {start:?} charge {charge} hit {hit:?} moved {moved:.1} units, peak speed {max_speed:.0} u/s, weaponstate now {:#x}", rig.state.weaponstate);
        assert!(start.is_some() && hit.is_some(), "{label}: melee never started / hit");
        if dist > 64.0 {
            // ak47_mp has no melee_charge anim, so MW2 plays the plain knife anim while pmove lunges.
            assert!(moved > 40.0, "lunge: player only moved {moved}");
        } else {
            assert!(!charge && moved.abs() < 5.0, "stab: no lunge");
            let _ = charge;
        }
    }
}

/// How far MW2's RPG view kick throws the camera (ADS and hip): IW4's kick is an angular
/// velocity that recentres, so the peak should be a few degrees, not tens.
#[test]
#[ignore]
fn real_install_rpg_kick_peak() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    let world = World { floor: true, wall_x: f32::INFINITY, floor_surface: 0, wall_surface: 0 };
    for (label, ads, msec) in [("hip 8ms", 0u32, 8), ("hip 16ms", 0, 16), ("hip 33ms", 0, 33), ("ads 16ms", 0x800, 16)] {
        let mut rig = Rig::new(world, msec);
        rig.give("rpg_mp");
        for _ in 0..200 {
            rig.step(ads, 0.0, 0.0);
        }
        let mut peak = [0f32; 3];
        for i in 0..150 {
            rig.step(ads | if i < 3 { buttons::ATTACK } else { 0 }, 0.0, 0.0);
            for k in 0..3 {
                if rig.state.kick_angles[k].abs() > peak[k].abs() {
                    peak[k] = rig.state.kick_angles[k];
                }
            }
        }
        eprintln!("rpg {label}: peak kick {peak:?} deg, now {:?}, ads frac {}", rig.state.kick_angles, rig.state.ads_frac);
    }
}

/// The launcher / grenade smoke trails (IW4 geotrails) moved like a projectile: do they draw?
#[test]
#[ignore]
fn real_install_projectile_trails_draw() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    crate::fx::mw2_fx_init();
    for (name, speed) in [("smoke/smoke_geotrail_rpg", 1500.0f32), ("smoke/smoke_geotrail_fraggrenade", 940.0), ("smoke/smoke_geotrail_m203", 1000.0)] {
        let id = unsafe { crate::fx::mw2_fx_find(name.as_ptr(), name.len()) };
        assert!(id != 0, "{name}");
        let (fwd, up) = ([1.0f32, 0.0, 0.0], [0.0f32, 0.0, 1.0]);
        let mut at = [0.0f32, 0.0, 60.0];
        let h = unsafe { crate::fx::mw2_fx_play(id, at.as_ptr(), fwd.as_ptr(), up.as_ptr()) };
        let cam = [-200.0f32, 0.0, 60.0];
        let mut counts = Vec::new();
        let mut buf = vec![crate::fx::Mw2FxQuad::default(); 4096];
        for frame in 0..30 {
            at[0] += speed / 60.0;
            let moved = unsafe { crate::fx::mw2_fx_move(h, at.as_ptr(), fwd.as_ptr(), up.as_ptr()) };
            unsafe { crate::fx::mw2_fx_update(1.0 / 60.0, cam.as_ptr(), fwd.as_ptr(), up.as_ptr()) };
            let n = unsafe { crate::fx::mw2_fx_quads(buf.as_mut_ptr(), buf.len() as u32) };
            if frame % 5 == 0 {
                counts.push((frame, moved, n));
            }
            if frame == 20 {
                let mut mats: Vec<u16> = buf[..n as usize].iter().map(|q| q.material).collect();
                mats.sort();
                mats.dedup();
                for m in mats {
                    let mut nm = [0u8; 128];
                    let blend = unsafe { crate::fx::mw2_fx_material(m, nm.as_mut_ptr(), 128) };
                    let (mut w, mut hh) = (0u32, 0u32);
                    let bytes = unsafe { crate::fx::mw2_fx_texture(m, &mut w, &mut hh, std::ptr::null_mut(), 0) };
                    eprintln!("  {name} material {m} '{}' blend {blend} texture {w}x{hh} ({bytes} bytes)", String::from_utf8_lossy(&nm).trim_end_matches(' '));
                }
            }
        }
        crate::fx::mw2_fx_stop(h);
        eprintln!("{name}: (frame, moved, quads) {counts:?}");
    }
}

/// RPG viewmodel rocket: shown loaded, gone after the shot, back `reloadShowRocketTime` into the reload.
#[test]
#[ignore]
fn real_install_rpg_viewmodel_rocket() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    let world = World { floor: true, wall_x: f32::INFINITY, floor_surface: 0, wall_surface: 0 };
    let mut rig = Rig::new(world, 8);
    let rpg = rig.give("rpg_mp");
    let mut name = [0u8; 64];
    let n = unsafe { mw2_weapon_rocket_model(rpg, name.as_mut_ptr(), 64) } as usize;
    eprintln!("rocket model {:?}", std::str::from_utf8(&name[..n]));
    assert!(n > 0);
    let mesh = unsafe { crate::mw2_model_index(name.as_ptr(), n) };
    eprintln!("rocket mesh id {mesh}");
    rig.idle(1500);
    let vis = |r: &Rig| unsafe { mw2_viewmodel_rocket_visible(r.sim) };
    assert_eq!(vis(&rig), 1, "loaded");
    let mut log = Vec::new();
    let mut last = 2;
    for i in 0..600 {
        let b = if i < 2 { buttons::ATTACK } else if (150..152).contains(&i) { buttons::RELOAD } else { 0 };
        rig.step(b, 0.0, 0.0);
        let v = vis(&rig);
        if v != last {
            log.push((rig.t, v, rig.state.weaponstate, rig.state.clip));
            last = v;
        }
    }
    eprintln!("(t ms, visible, weaponstate, clip): {log:?}");
    assert!(log.iter().any(|e| e.1 == 0) && log.last().is_some_and(|e| e.1 == 1), "hide on fire, show during reload");
}

/// Every AK-47 attachment variant builds a viewmodel (shared WeaponDef fields filled in).
#[test]
#[ignore]
fn real_install_attachment_variants_have_viewmodels() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    let mut missing = Vec::new();
    for a in ["", "_acog", "_reflex", "_eotech", "_thermal", "_silencer", "_gl", "_shotgun", "_heartbeat", "_fmj", "_xmags", "_fmj_reflex", "_reflex_silencer"] {
        let n = format!("ak47{a}_mp");
        let w = unsafe { mw2_weapon_index(n.as_ptr(), n.len()) };
        let vm = crate::mw2_viewmodel_build(w);
        if vm.is_null() {
            missing.push(n);
        } else {
            unsafe { crate::mw2_viewmodel_destroy(vm) };
        }
    }
    assert!(missing.is_empty(), "no viewmodel: {missing:?}");
}

/// Bolt-action (Intervention): fire, then the next shot must come after the rechamber, not a reload.
#[test]
#[ignore]
fn real_install_intervention_rechambers() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    let world = World { floor: true, wall_x: f32::INFINITY, floor_surface: 0, wall_surface: 0 };
    let mut rig = Rig::new(world, 8);
    let w = rig.give("cheytac_mp");
    let r = crate::weapons::row(w).unwrap();
    eprintln!("cheytac: clip {} fire {} ms rechamber {} ms, timers {:?}", r.facts.clip_size, r.facts.fire_time_ms, r.facts.rechamber_time_ms, r.timers.reload_ms);
    rig.idle(1500);
    let mut log = Vec::new();
    let (mut last_ws, mut last_clip) = (-1, -1);
    for i in 0..600 {
        let b = 0x800 | if (i % 60) < 2 { buttons::ATTACK } else { 0 }; // held ADS, as when scoped
        rig.step(b, 0.0, 0.0);
        if rig.state.weaponstate != last_ws || rig.state.clip != last_clip {
            last_ws = rig.state.weaponstate;
            last_clip = rig.state.clip;
            log.push((rig.t, last_ws, last_clip, rig.state.events));
            eprintln!("    ads {:.2}", rig.state.ads_frac);
        }
    }
    for l in &log {
        eprintln!("  t {:5} ws {:#x} clip {} ev {:#x}", l.0, l.1, l.2, l.3);
    }
}

/// Frag explosion: which effect plays and which sounds its sound elems ask for (and whether they exist).
#[test]
#[ignore]
fn real_install_frag_explosion_sound() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    crate::fx::mw2_fx_init();
    for n in ["frag_grenade_mp", "rpg_mp", "m79_mp", "c4_mp", "semtex_mp"] {
        let w = unsafe { mw2_weapon_index(n.as_ptr(), n.len()) };
        let it = crate::weapon_fx::mw2_weapon_impact_type(w);
        for surf in [0u32, surface_index("dirt"), surface_index("concrete")] {
            let fx = string(|o, c| unsafe { crate::weapon_fx::mw2_impact_fx(it.max(0) as u32, surf, o, c) });
            let own = string(|o, c| unsafe { crate::weapon_fx::mw2_weapon_fx(w, 6, o, c) });
            let name = own.clone().or(fx.clone()).unwrap_or_default();
            let id = unsafe { crate::fx::mw2_fx_find(name.as_ptr(), name.len()) };
            let (o, f) = ([0.0f32; 3], [0.0f32, 0.0, 1.0]);
            let h = unsafe { crate::fx::mw2_fx_play(id, o.as_ptr(), f.as_ptr(), std::ptr::null()) };
            let mut sounds = String::new();
            for _ in 0..30 {
                unsafe { crate::fx::mw2_fx_update(1.0 / 60.0, [0.0f32, -300.0, 50.0].as_ptr(), [0.0f32, 1.0, 0.0].as_ptr(), [0.0f32, 0.0, 1.0].as_ptr()) };
                let mut buf = [0u8; 2048];
                let k = unsafe { crate::fx::mw2_fx_sounds(buf.as_mut_ptr(), 2048) } as usize;
                sounds.push_str(&String::from_utf8_lossy(&buf[..k]));
            }
            crate::fx::mw2_fx_stop(h);
            let names: Vec<(String, bool)> = sounds.lines().map(|l| l.split_whitespace().next().unwrap_or("").to_string()).filter(|s| !s.is_empty()).map(|s| { let e = crate::sounds::exists(&s); (s, e) }).collect();
            let boom = string(|o, c| unsafe { mw2_weapon_projectile_sound(w, 0, o, c) });
            eprintln!("{n} surf {surf}: fx {name:?} (id {id}) sound elems {names:?} projExplosionSound {boom:?}");
        }
    }
}

/// Sleight of Hand (engine perk 0x4): reload states run at 1 / 0.5, so a reload takes half as long.
#[test]
#[ignore]
fn real_install_sleight_of_hand_halves_the_reload() {
    let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if !std::path::Path::new(COMMON_MP).exists() {
        return;
    }
    assert!(unsafe { mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
    let reload_ms = |perks: u32| {
        let world = World { floor: true, wall_x: f32::INFINITY, floor_surface: 0, wall_surface: 0 };
        let mut rig = Rig::new(world, 8);
        unsafe { crate::mw2_set_perks(rig.sim, perks) };
        rig.give("ak47_mp");
        rig.idle(1500);
        // Fire a few rounds, then reload and time until the clip is full again.
        for i in 0..40 {
            rig.step(if i % 4 < 2 { buttons::ATTACK } else { 0 }, 0.0, 0.0);
        }
        rig.idle(300);
        let start = rig.t;
        for _ in 0..600 {
            rig.step(0x10, 0.0, 0.0);
            if rig.state.clip == 30 {
                return rig.t - start;
            }
        }
        -1
    };
    let (plain, fast) = (reload_ms(0), reload_ms(4));
    eprintln!("AK-47 reload: {plain} ms, with Sleight of Hand {fast} ms");
    assert!(plain > 0 && fast > 0);
    assert!((fast as f32 / plain as f32 - 0.5).abs() < 0.08, "about half");
}
