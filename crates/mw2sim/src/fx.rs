//! MW2 particle effects (FxEffectDef) for the host: spawn, tick and camera-facing quads.
//!
//! A lean port of IW4L's runtime (`fx` crate system/spawn/update/motion/trail/draw plus the
//! `render_fx::present` glue), built on the pure `fx_iw4` formulas: looping/one-shot spawn
//! schedules, delay/life/seed sampling, velocity graphs + gravity, run-relative orientation,
//! visual curves (size, colour, rotation), atlas frames, emitted / death / runner child
//! effects and trails. The `fx` crate itself is not a dependency: it pulls `diag` (serde) and
//! the glass / marks systems, none of which this needs.
//!
//! All positions and directions are IW4 world space (inches, Z up). Time is integer msec,
//! derived from an f64 accumulator of the host's dt so it never drifts.
//!
//! Drawn: billboard, oriented and tail sprites, and trails. Not simulated: world collision
//! (collision elems fly through the world, so impact children never fire), lights, decals,
//! emission elem defs. Model / cloud / spark-cloud / spark-fountain elems are only simulated
//! when they start child effects (an invisible emitter elem), never drawn.

use std::collections::{HashMap, VecDeque};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, Mutex, MutexGuard};

use fx_iw4::{self as iw, FxOrientFrame, FxOrientSpawnParams, FxSpriteAtlasUv};
use mw2data::fx::{FxCapture, FxEffect, FxElem, FxVisual, elem_type as et};

/// One textured quad, world space. Triangles (0,1,2), (0,2,3).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Mw2FxQuad {
    pub corners: [[f32; 3]; 4],
    /// Atlas frame applied; v = 0 is the bottom row of the `mw2_fx_texture` image.
    pub uv: [[f32; 2]; 4],
    pub color: [u8; 4],
    /// 1-based id for `mw2_fx_material` / `mw2_fx_texture`.
    pub material: u16,
    pub sort: u16,
}
const _: () = assert!(size_of::<Mw2FxQuad>() == 88);

/// Effects captured by the `common_mp` walk (`mw2_load_weapons`), waiting for `mw2_fx_init`.
pub static CAPTURED: Mutex<Option<FxCapture>> = Mutex::new(None);
static SYSTEM: Mutex<Option<System>> = Mutex::new(None);

const MAX_EFFECTS: usize = iw::FX_EFFECT_POOL_CAPACITY as usize;
const MAX_ELEMS: usize = iw::FX_ELEM_POOL_CAPACITY as usize;
const MAX_TRAILS: usize = iw::FX_TRAIL_POOL_CAPACITY as usize;
const MAX_TRAIL_ELEMS: usize = iw::FX_TRAIL_ELEM_POOL_CAPACITY as usize;
/// Child effects started per update (runaway emitter chains stop here).
const MAX_SPAWNS_PER_UPDATE: usize = 2048;
const MAX_PENDING_SOUNDS: usize = 256;
const MAX_SOUND_TEXT: usize = 64 * 1024;
const MAX_QUADS: usize = 65536;
const CAMERA_REGION_EMISSIVE: u8 = 2;
const TRAIL_DIR_BASIS: i32 = 0x4000;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

// ---- small vector helpers -------------------------------------------------------------------

fn add(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}
fn sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
fn scale(a: [f32; 3], s: f32) -> [f32; 3] {
    [a[0] * s, a[1] * s, a[2] * s]
}
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
}
fn finite3(v: [f32; 3]) -> bool {
    v.iter().all(|c| c.is_finite())
}

/// IW4 axis [forward, left, up] from a forward and an up hint.
fn axis_from(fwd: [f32; 3], up: Option<[f32; 3]>) -> [[f32; 3]; 3] {
    let f = iw::vec3_normalize(fwd);
    if iw::vec3_length_sq(f) < 0.5 {
        return iw::vector_vectors([0.0, 0.0, 1.0]);
    }
    let Some(up) = up else { return iw::vector_vectors(f) };
    let left = iw::vec3_normalize(cross(up, f));
    if iw::vec3_length_sq(left) < 0.5 {
        return iw::vector_vectors(f);
    }
    [f, left, cross(f, left)]
}

// ---- library --------------------------------------------------------------------------------

struct Material {
    name: String,
    image: Option<String>,
    blend: i32,
    drawable: bool,
}

#[derive(Default)]
struct ElemLinks {
    death: Option<u32>,
    emitted: Option<u32>,
    runners: Vec<Option<u32>>,
    /// The def names a child at all (IW4 keeps such elems alive past their own life).
    keep_alive: bool,
}

struct Library {
    effects: Vec<FxEffect>,
    links: Vec<Vec<ElemLinks>>,
    materials: Vec<Material>,
    by_name: HashMap<String, u32>,
}

fn normalize_name(name: &str) -> String {
    let n = name.trim().replace('\\', "/").to_ascii_lowercase();
    let n = n.strip_prefix("fx/").unwrap_or(&n);
    n.strip_suffix(".efx").unwrap_or(n).to_owned()
}

/// asset_material `MaterialDrawMode::from_state_bits`: 0 opaque, 1 alpha-test, 2 blend,
/// 3 additive, 4 multiply, 5 screen.
pub(crate) fn blend_mode(bits: Option<[u32; 2]>) -> i32 {
    let Some([w0, _]) = bits else { return 2 };
    let alpha_test_disabled = (w0 >> 11) & 1 != 0;
    let op = (w0 >> 8) & 7;
    let src = w0 & 0xf;
    let dst = (w0 >> 4) & 0xf;
    if op != 0 {
        return match (op, src, dst) {
            (1, 1, 3) => 4,
            (1, 2, 2) => 3,
            (1, 10, 2) => 5,
            _ => 2,
        };
    }
    if alpha_test_disabled { 0 } else { 1 }
}

impl Library {
    fn build(cap: FxCapture) -> Self {
        let FxCapture { effects, materials, .. } = cap;
        let mut by_name = HashMap::new();
        for (i, e) in effects.iter().enumerate() {
            by_name.entry(normalize_name(&e.name)).or_insert(i as u32);
        }
        let find = |n: &Option<String>| n.as_deref().and_then(|n| by_name.get(&normalize_name(n)).copied());
        let links = effects
            .iter()
            .map(|e| {
                e.elems
                    .iter()
                    .map(|el| ElemLinks {
                        death: find(&el.on_death),
                        emitted: find(&el.emitted),
                        runners: el
                            .visuals
                            .iter()
                            .map(|v| match v {
                                FxVisual::Runner(n) => by_name.get(&normalize_name(n)).copied(),
                                _ => None,
                            })
                            .collect(),
                        keep_alive: el.on_impact.is_some() || el.on_death.is_some() || el.emitted.is_some(),
                    })
                    .collect()
            })
            .collect();
        let materials = materials
            .into_iter()
            .map(|m| Material {
                // Distortion (heat haze) materials feed IW4's post-process pass; drawn as plain
                // sprites they show their normal-map texture.
                drawable: m.camera_region == CAMERA_REGION_EMISSIVE && !m.name.to_ascii_lowercase().contains("distortion"),
                blend: blend_mode(m.state_bits),
                name: m.name,
                image: m.image,
            })
            .collect();
        Self { effects, links, materials, by_name }
    }

    fn elem(&self, effect: u32, def: u8) -> &FxElem {
        &self.effects[effect as usize].elems[def as usize]
    }
}

// ---- runtime state --------------------------------------------------------------------------

#[derive(Clone, Copy)]
struct Elem {
    def: u8,
    seq: u8,
    msec_begin: i32,
    life: i32,
    origin: [f32; 3],
    base_vel: [f32; 3],
    emit_residual: u8,
}

#[derive(Clone, Copy)]
struct TrailSample {
    origin: [f32; 3],
    spawn_dist: f32,
    msec_begin: i32,
    basis: [[f32; 3]; 2],
    base_vel_z: f32,
    seq: u8,
}

struct Trail {
    def: u8,
    seq: i8,
    split_leftover: f32,
    samples: VecDeque<TrailSample>,
}

struct Effect {
    def: u32,
    handle: u32,
    seed: u16,
    msec_begin: i32,
    msec_last: i32,
    now: FxOrientFrame,
    spawn: FxOrientFrame,
    last: FxOrientFrame,
    distance: f32,
    looping: bool,
    elems: Vec<Elem>,
    trails: Vec<Trail>,
}

impl Effect {
    fn done(&self) -> bool {
        !self.looping && self.elems.is_empty() && self.trails.iter().all(|t| t.samples.is_empty())
    }
}

struct SpawnReq {
    def: u32,
    origin: [f32; 3],
    axis: [[f32; 3]; 3],
    msec: i32,
}

struct PendingSound {
    alias: String,
    origin: [f32; 3],
    msec: i32,
}

type Rgba = Arc<(u32, u32, Vec<u8>)>;

struct Runtime {
    seconds: f64,
    msec_now: i32,
    slots: Vec<Option<Effect>>,
    generations: Vec<u16>,
    live: usize,
    elem_count: usize,
    trail_count: usize,
    trail_elem_count: usize,
    holdrand: u32,
    /// Camera origin of the last `mw2_fx_update` (trail split thinning).
    camera: [f32; 3],
    spawn_queue: Vec<SpawnReq>,
    sounds: Vec<PendingSound>,
    sound_text: String,
    quads: Vec<Mw2FxQuad>,
    textures: HashMap<u16, Option<Rgba>>,
    /// Pool exhaustion counters (debug).
    pub failed_effects: u32,
    pub failed_elems: u32,
}

struct System {
    lib: Library,
    rt: Runtime,
}

impl Runtime {
    fn new() -> Self {
        Self {
            seconds: 0.0,
            msec_now: 0,
            slots: (0..MAX_EFFECTS).map(|_| None).collect(),
            generations: vec![0; MAX_EFFECTS],
            live: 0,
            elem_count: 0,
            trail_count: 0,
            trail_elem_count: 0,
            holdrand: iw::MSVCRT_HOLDRAND_DEFAULT,
            camera: [0.0; 3],
            spawn_queue: Vec::new(),
            sounds: Vec::new(),
            sound_text: String::new(),
            quads: Vec::new(),
            textures: HashMap::new(),
            failed_effects: 0,
            failed_elems: 0,
        }
    }

    fn slot_of(&self, handle: u32) -> Option<usize> {
        let slot = (handle & 0xffff).checked_sub(1)? as usize;
        let e = self.slots.get(slot)?.as_ref()?;
        (e.handle == handle).then_some(slot)
    }

    fn free(&mut self, slot: usize) {
        if let Some(e) = self.slots[slot].take() {
            self.elem_count = self.elem_count.saturating_sub(e.elems.len());
            self.trail_count = self.trail_count.saturating_sub(e.trails.len());
            self.trail_elem_count = self.trail_elem_count.saturating_sub(e.trails.iter().map(|t| t.samples.len()).sum::<usize>());
            self.live = self.live.saturating_sub(1);
        }
    }
}

fn spawn_params(el: &FxElem, seed: u32) -> FxOrientSpawnParams {
    let v = &el.view;
    FxOrientSpawnParams {
        spawn_origin: v.spawn_origin,
        spawn_offset_radius: [v.spawn_offset_radius_base, v.spawn_offset_radius_amplitude],
        spawn_offset_height: [v.spawn_offset_height_base, v.spawn_offset_height_amplitude],
        seed,
    }
}

fn spawn_origin_world(el: &FxElem, origin: [f32; 3], axis: [[f32; 3]; 3], seed: u32) -> [f32; 3] {
    let v = &el.view;
    iw::spawn_origin_world(
        origin,
        axis,
        v.spawn_origin,
        v.flags,
        v.spawn_offset_radius_base,
        v.spawn_offset_radius_amplitude,
        v.spawn_offset_height_base,
        v.spawn_offset_height_amplitude,
        seed,
    )
}

/// Start an effect (the `start_new_effect` port). The effect's first elems are spawned at `msec`.
fn spawn_effect(lib: &Library, rt: &mut Runtime, req: SpawnReq) -> Option<u32> {
    let def = lib.effects.get(req.def as usize)?;
    if !finite3(req.origin) || req.axis.iter().any(|a| !finite3(*a)) {
        return None;
    }
    let Some(slot) = rt.slots.iter().position(Option::is_none) else {
        rt.failed_effects = rt.failed_effects.saturating_add(1);
        return None;
    };
    rt.generations[slot] = rt.generations[slot].wrapping_add(1).max(1);
    let handle = (u32::from(rt.generations[slot]) << 16) | (slot as u32 + 1);
    let seed = iw::effect_random_seed_from_rand(iw::msvcrt_rand(&mut rt.holdrand));
    let frame = FxOrientFrame { origin: req.origin, axis: req.axis };
    let mut e = Effect {
        def: req.def,
        handle,
        seed,
        msec_begin: req.msec,
        msec_last: req.msec,
        now: frame,
        spawn: frame,
        last: frame,
        distance: 0.0,
        looping: def.view.msec_looping_life != 0 && def.view.looping_count > 0,
        elems: Vec::new(),
        trails: Vec::new(),
    };
    let looping = def.view.looping_count.max(0) as usize;
    let oneshot = def.view.one_shot_count.max(0) as usize;
    let drawn = (looping + oneshot).min(def.elems.len());
    for (i, el) in def.elems[..drawn].iter().enumerate() {
        if el.view.elem_type == et::TRAIL && rt.trail_count < MAX_TRAILS {
            rt.trail_count += 1;
            e.trails.push(Trail { def: i as u8, seq: 0, split_leftover: 0.0, samples: VecDeque::new() });
        }
    }
    for i in 0..looping.min(drawn) {
        if def.elems[i].view.elem_type != et::TRAIL {
            spawn_elem(lib, rt, &mut e, i as u8, 0, req.msec);
        }
    }
    for i in looping.min(drawn)..drawn {
        let v = &def.elems[i].view;
        if v.elem_type == et::TRAIL {
            continue;
        }
        let r = iw::random_table_u16(u32::from(e.seed), iw::FX_RAND_CH_ONESHOT_COUNT);
        let count = iw::sample_oneshot_spawn_count(v.spawn_a, v.spawn_b, r).clamp(0, 256);
        for seq in 0..count {
            spawn_elem(lib, rt, &mut e, i as u8, seq as u8, req.msec);
        }
    }
    rt.slots[slot] = Some(e);
    rt.live += 1;
    Some(handle)
}

fn spawn_elem(lib: &Library, rt: &mut Runtime, e: &mut Effect, def_index: u8, seq: u8, spawn_msec: i32) {
    let el = lib.elem(e.def, def_index);
    let v = &el.view;
    let after_delay_base = spawn_msec.wrapping_add(v.spawn_delay_msec_base);
    let delay = if v.spawn_delay_msec_amplitude != 0 {
        let r = iw::random_table_u16(iw::elem_random_seed(e.seed, seq, after_delay_base), iw::FX_RAND_CH_DELAY);
        iw::sample_life_span_msec(v.spawn_delay_msec_base, v.spawn_delay_msec_amplitude, r)
    } else {
        v.spawn_delay_msec_base
    };
    let msec_begin = spawn_msec.wrapping_add(delay);
    let seed = iw::elem_random_seed(e.seed, seq, msec_begin);
    let visual = iw::elem_visual_index(v.visual_count, seed);
    match v.elem_type {
        et::SOUND => {
            if let Some(FxVisual::Sound(alias)) = el.visuals.get(visual)
                && rt.sounds.len() < MAX_PENDING_SOUNDS
            {
                let origin = spawn_origin_world(el, e.now.origin, e.now.axis, seed);
                rt.sounds.push(PendingSound { alias: alias.clone(), origin, msec: msec_begin });
            }
            return;
        }
        et::RUNNER => {
            if let Some(&Some(child)) = lib.links[e.def as usize][def_index as usize].runners.get(visual) {
                let origin = spawn_origin_world(el, e.now.origin, e.now.axis, seed);
                let axis = if v.flags & iw::FX_ELEM_RUNNER_USES_RAND_ROT != 0 { iw::randomly_rotate_axis(e.now.axis, seed) } else { e.now.axis };
                rt.spawn_queue.push(SpawnReq { def: child, origin, axis, msec: msec_begin });
            }
            return;
        }
        et::DECAL | et::OMNI_LIGHT | et::SPOT_LIGHT => return,
        t if t > et::RUNNER => return,
        _ => {}
    }
    let keep_alive = lib.links[e.def as usize][def_index as usize].keep_alive;
    // Model / cloud / spark elems aren't drawn here; simulate them only when they start children.
    if !matches!(v.elem_type, et::BILLBOARD | et::ORIENTED | et::TAIL) && !keep_alive {
        return;
    }
    let life = iw::sample_life_span_msec(v.life_span_msec_base, v.life_span_msec_amplitude, iw::random_table_u16(seed, iw::FX_RAND_CH_LIFE));
    if !keep_alive && rt.msec_now >= msec_begin.wrapping_add(life) {
        return;
    }
    if rt.elem_count >= MAX_ELEMS {
        rt.failed_elems = rt.failed_elems.saturating_add(1);
        return;
    }
    let mode = iw::elem_run_mode(v.flags);
    let origin = if mode == iw::FX_ELEM_RUN_RELATIVE_TO_OFFSET {
        [0.0; 3]
    } else {
        let w = spawn_origin_world(el, e.now.origin, e.now.axis, seed);
        if mode == iw::FX_ELEM_RUN_RELATIVE_TO_SPAWN || mode == iw::FX_ELEM_RUN_RELATIVE_TO_EFFECT {
            iw::world_delta_to_local(w, e.now.origin, e.now.axis)
        } else {
            w
        }
    };
    rt.elem_count += 1;
    e.elems.push(Elem { def: def_index, seq, msec_begin, life, origin, base_vel: [0.0; 3], emit_residual: 0 });
}

fn spawn_looping(lib: &Library, rt: &mut Runtime, e: &mut Effect, prev: i32, now: i32) {
    let def = &lib.effects[e.def as usize];
    let looping = (def.view.looping_count.max(0) as usize).min(def.elems.len());
    for i in 0..looping {
        let v = &def.elems[i].view;
        if v.elem_type == et::TRAIL {
            continue;
        }
        let duration = v
            .spawn_delay_msec_base
            .wrapping_add(v.spawn_delay_msec_amplitude)
            .wrapping_add(v.life_span_msec_base)
            .wrapping_add(v.life_span_msec_amplitude);
        let begin = iw::looping_catchup_begin(prev, now, duration);
        let schedule = iw::looping_spawn_schedule(e.msec_begin, begin, now, v.spawn_a, v.spawn_b);
        // A long hitch can schedule far more than the pool holds; IW4 spawns them all.
        for s in schedule.take(MAX_ELEMS) {
            spawn_elem(lib, rt, e, i as u8, s.sequence as u8, s.msec);
        }
    }
}

/// `fx::motion::integrate_free_flight`: velocity graphs (local in orient space, world in world
/// space) plus base velocity and gravity. Returns (new stored origin, new base velocity).
fn free_flight(el: &FxElem, seed: u32, orient: iw::FxOrientation, origin: [f32; 3], base_vel: [f32; 3], age0: f32, age1: f32, life_ms: f32, dt: f32) -> ([f32; 3], [f32; 3]) {
    let flags = el.view.flags;
    let mut stored = origin;
    if iw::elem_uses_vel_local(flags) && el.vel_local.len() >= 2 {
        stored = add(stored, iw::integrate_velocity_graph(&el.vel_local, age0, age1, life_ms, seed));
    }
    let mut world = iw::orientation_pos_to_world(orient.origin, orient.axis, stored);
    if iw::elem_uses_vel_world(flags) && el.vel_world.len() >= 2 {
        world = add(world, iw::integrate_velocity_graph(&el.vel_world, age0, age1, life_ms, seed));
    }
    let mut vel = base_vel;
    if iw::elem_update_has_velocity_graph(flags) {
        let g = iw::elem_gravity_accel_z_sampled(el.view.gravity_base, el.view.gravity_amplitude, seed);
        world = add(world, scale(vel, dt));
        world[2] -= g * dt * dt * 0.5;
        vel[2] -= g * dt;
        stored = iw::orientation_pos_from_world(orient.origin, orient.axis, world);
    }
    (stored, vel)
}

fn update_effect(lib: &Library, rt: &mut Runtime, e: &mut Effect, prev: i32, now: i32) {
    if e.looping {
        spawn_looping(lib, rt, e, prev, now);
        if lib.effects[e.def as usize].view.msec_looping_life < now.wrapping_sub(e.msec_begin) {
            e.looping = false;
        }
    }
    let mut i = 0;
    while i < e.elems.len() {
        if update_elem(lib, rt, e, i, prev, now) {
            i += 1;
        } else {
            e.elems.swap_remove(i);
            rt.elem_count = rt.elem_count.saturating_sub(1);
        }
    }
    update_trails(lib, rt, e, prev, now);
    e.distance += iw::vec3_distance(e.last.origin, e.now.origin);
    e.last = e.now;
    e.msec_last = now;
}

fn update_elem(lib: &Library, rt: &mut Runtime, e: &mut Effect, i: usize, prev: i32, now: i32) -> bool {
    let mut el = e.elems[i];
    if now < el.msec_begin {
        return true;
    }
    let def = lib.elem(e.def, el.def);
    let links = &lib.links[e.def as usize][el.def as usize];
    let flags = def.view.flags;
    let seed = iw::elem_random_seed(e.seed, el.seq, el.msec_begin);
    let sp = spawn_params(def, seed);
    let orient = iw::get_orientation(flags, &e.now, &e.spawn, Some(sp));
    if now >= el.msec_begin.wrapping_add(el.life) {
        if let Some(child) = links.death {
            let origin = iw::orientation_pos_to_world(orient.origin, orient.axis, el.origin);
            rt.spawn_queue.push(SpawnReq { def: child, origin, axis: orient.axis, msec: now });
        }
        return false;
    }
    let t0 = prev.max(el.msec_begin);
    let life_ms = el.life.max(1) as f32;
    let age0 = t0.wrapping_sub(el.msec_begin).max(0) as f32 / life_ms;
    let age1 = now.wrapping_sub(el.msec_begin).max(0) as f32 / life_ms;
    let dt = now.wrapping_sub(t0).max(0) as f32 * 0.001;
    // Collision elems (FX_ELEM_USE_COLLISION) fly free: there is no world to trace against.
    let (origin, base_vel) = free_flight(def, seed, orient, el.origin, el.base_vel, age0, age1, life_ms, dt);
    if let Some(child) = links.emitted {
        // Emission spacing is measured in world space, so emitters riding a moving effect emit.
        let before = if el.msec_begin <= prev { iw::get_orientation(flags, &e.last, &e.spawn, Some(sp)) } else { orient };
        let wb = iw::orientation_pos_to_world(before.origin, before.axis, el.origin);
        let we = iw::orientation_pos_to_world(orient.origin, orient.axis, origin);
        let v = &def.view;
        let (base, max) = iw::emit_dist_range(v.emit_dist[0], v.emit_dist[1], v.emit_dist_variance[0], v.emit_dist_variance[1], seed);
        let mut holdrand = rt.holdrand;
        let sched = iw::process_emitting_schedule(el.emit_residual, wb, we, t0, now, base, max, || {
            iw::msvcrt_rand(&mut holdrand) as f32 * iw::FX_EMIT_CRT_RAND_SCALE as f32
        });
        rt.holdrand = holdrand;
        el.emit_residual = sched.new_residual;
        let axis = if flags & iw::FX_ELEM_EMIT_ORIENT_AXIS != 0 { e.now.axis } else { iw::vector_vectors(sub(we, wb)) };
        for s in sched.spawns() {
            rt.spawn_queue.push(SpawnReq { def: child, origin: iw::emit_lerp_origin(wb, we, s.lerp), axis, msec: s.msec_at_spawn });
        }
    }
    el.origin = origin;
    el.base_vel = base_vel;
    e.elems[i] = el;
    true
}

// ---- trails ---------------------------------------------------------------------------------

fn trail_life(el: &FxElem, effect_seed: u16, seq: u8) -> i32 {
    let seed = iw::trail_random_seed(effect_seed, seq as i8);
    iw::sample_life_span_msec(el.view.life_span_msec_base, el.view.life_span_msec_amplitude, iw::random_table_u16(seed, iw::FX_RAND_CH_LIFE))
}

/// `fx::trail::update_trail`: append one sample.
fn add_trail_sample(el: &FxElem, keep_alive: bool, rt: &mut Runtime, effect_seed: u16, trail: &mut Trail, origin: [f32; 3], axis: [[f32; 3]; 3], msec: i32, spawn_dist: f32) {
    let v = &el.view;
    let seed = iw::trail_random_seed(effect_seed, trail.seq);
    let mut msec_begin = v.spawn_delay_msec_base.wrapping_add(msec);
    if v.spawn_delay_msec_amplitude != 0 {
        msec_begin = msec_begin.wrapping_add(iw::sample_life_span_msec(0, v.spawn_delay_msec_amplitude, iw::random_table_u16(seed, iw::FX_RAND_CH_DELAY)));
    }
    let life = iw::sample_life_span_msec(v.life_span_msec_base, v.life_span_msec_amplitude, iw::random_table_u16(seed, iw::FX_RAND_CH_LIFE));
    if !(keep_alive || rt.msec_now < life.wrapping_add(msec_begin)) || rt.trail_elem_count >= MAX_TRAIL_ELEMS {
        return;
    }
    rt.trail_elem_count += 1;
    trail.samples.push_back(TrailSample {
        origin: spawn_origin_world(el, origin, axis, seed),
        spawn_dist,
        msec_begin,
        basis: [axis[1], axis[2]],
        base_vel_z: 0.0,
        seq: trail.seq as u8,
    });
    trail.seq = trail.seq.wrapping_add(1);
}

/// `fx::trail::update_effect_trails` + `apply_partial_last_trail_spawn_dist`.
fn update_trails(lib: &Library, rt: &mut Runtime, e: &mut Effect, prev: i32, now: i32) {
    if e.trails.is_empty() {
        return;
    }
    let (begin, end) = (e.last, e.now);
    let distance_delta = iw::vec3_distance(begin.origin, end.origin);
    let arc_delta = iw::effect_orient_arc(begin.axis, end.axis);
    let mut trails = std::mem::take(&mut e.trails);
    for trail in &mut trails {
        let el = lib.elem(e.def, trail.def);
        let Some(td) = el.trail.as_ref() else { continue };
        let keep_alive = lib.links[e.def as usize][trail.def as usize].keep_alive;
        let v = &el.view;
        if e.looping || (prev == e.msec_begin && trail.samples.is_empty()) {
            if trail.samples.is_empty() {
                add_trail_sample(el, keep_alive, rt, e.seed, trail, begin.origin, begin.axis, prev, e.distance);
            } else if e.looping && prev < now && trail.samples.len() >= 2 {
                // Replace the moving endpoint; only split samples stay in the history.
                trail.samples.pop_back();
                rt.trail_elem_count = rt.trail_elem_count.saturating_sub(1);
                trail.seq = trail.seq.wrapping_sub(1);
            }
            if e.looping && prev < now {
                let leftover_in = trail.split_leftover;
                let split = iw::trail_split_window(leftover_in, td.inv_split_time, (now - prev) as f32, td.inv_split_dist, distance_delta, td.inv_split_arc_dist, arc_delta);
                let (leftover, extra) = match split {
                    iw::FxTrailSplit::Hold { leftover } => (leftover, 0),
                    iw::FxTrailSplit::Interpolate { leftover, extra } => (leftover, extra.min(MAX_TRAIL_ELEMS as i32)),
                };
                trail.split_leftover = leftover;
                let acc = leftover + extra as f32;
                for k in 1..=extra {
                    let t = iw::trail_split_interpolant_t(k as f32, leftover_in, acc);
                    let origin = iw::trail_split_lerp_origin(begin.origin, end.origin, t);
                    let seq = trail.seq as u8;
                    if iw::trail_split_skips_update(seq, v.spawn_range_base, v.spawn_range_amplitude, rt.camera, origin) {
                        trail.seq = trail.seq.wrapping_add(1);
                    } else {
                        let msec = iw::trail_split_interpolant_msec(prev, now, t);
                        let axis = iw::trail_split_lerp_axis(begin.axis, end.axis, t);
                        add_trail_sample(el, keep_alive, rt, e.seed, trail, origin, axis, msec, e.distance + distance_delta * t);
                    }
                }
                add_trail_sample(el, keep_alive, rt, e.seed, trail, end.origin, end.axis, now, e.distance + distance_delta);
            }
        }
        // Age out: keep one dead sample ahead of the first live one (the fading tail end).
        let dead = trail.samples.iter().take_while(|s| !iw::trail_elem_keep(now, s.msec_begin, trail_life(el, e.seed, s.seq))).count();
        let drop = if dead == trail.samples.len() { dead } else { dead.saturating_sub(1) };
        trail.samples.drain(..drop);
        rt.trail_elem_count = rt.trail_elem_count.saturating_sub(drop);
        if prev != now {
            let graph = iw::elem_update_has_velocity_graph(v.flags);
            for s in trail.samples.iter_mut() {
                let seed = iw::trail_random_seed(e.seed, s.seq as i8);
                let life = trail_life(el, e.seed, s.seq);
                let (age0, age1) = iw::trail_elem_norm_ages(prev, now, s.msec_begin, life);
                let orient = iw::get_orientation(v.flags, &e.now, &e.spawn, Some(spawn_params(el, seed)));
                let mut stored = s.origin;
                if iw::elem_uses_vel_local(v.flags) && el.vel_local.len() >= 2 {
                    stored = add(stored, iw::integrate_velocity_graph(&el.vel_local, age0, age1, life as f32, seed));
                }
                let mut world = iw::orientation_pos_to_world(orient.origin, orient.axis, stored);
                if iw::elem_uses_vel_world(v.flags) && el.vel_world.len() >= 2 {
                    world = add(world, iw::integrate_velocity_graph(&el.vel_world, age0, age1, life as f32, seed));
                }
                if graph {
                    let dt = now.wrapping_sub(prev.max(s.msec_begin)).max(0) as f32 * 0.001;
                    let g = iw::elem_gravity_accel_z_sampled(v.gravity_base, v.gravity_amplitude, seed);
                    world[2] += s.base_vel_z * dt - g * dt * dt * 0.5;
                    s.base_vel_z -= g * dt;
                }
                s.origin = iw::orientation_pos_from_world(orient.origin, orient.axis, world);
            }
        }
        if e.looping
            && let Some(last) = trail.samples.back_mut()
        {
            let seed = iw::trail_random_seed(e.seed, last.seq as i8);
            last.spawn_dist = e.distance + distance_delta;
            last.origin = spawn_origin_world(el, end.origin, end.axis, seed);
            last.basis = [end.axis[1], end.axis[2]];
        }
    }
    e.trails = trails;
}

// ---- drawing --------------------------------------------------------------------------------

struct Camera {
    origin: [f32; 3],
    right: [f32; 3],
    up: [f32; 3],
}

struct Visual {
    size0: f32,
    size1: f32,
    color: [u8; 4],
    rotation: f32,
}

fn distance_fade(dist: f32, fade_in: [f32; 2], fade_out: [f32; 2]) -> f32 {
    let mut f = 1.0f32;
    if fade_in[1] != 0.0 {
        f = f.min(iw::distance_fade_range(dist, fade_in[0], fade_in[1]));
    }
    if fade_out[1] != 0.0 {
        f = f.min(1.0 - iw::distance_fade_range(dist, fade_out[0], fade_out[1]));
    }
    f.clamp(0.0, 1.0)
}

/// Size / colour / rotation curves at `norm` (colour converted from IW4 BGRA to RGBA).
fn visual_state(el: &FxElem, seed: u32, norm: f32, life_ms: i32, world: [f32; 3], cam: &Camera) -> Option<Visual> {
    let v = &el.view;
    let n = v.vis_state_interval_count;
    // Both size curves share the SIZE0 random draw, as IW4L's present glue does.
    let rand_size = iw::random_table_f32(seed, iw::FX_RAND_CH_SIZE0);
    let size0 = iw::evaluate_size0(&el.vis_samples, n, norm, rand_size)?;
    let size1 = iw::evaluate_size1(&el.vis_samples, n, norm, rand_size).unwrap_or(size0);
    let [b, g, r, a] = iw::evaluate_color_bgra(&el.vis_samples, n, norm, iw::random_table_f32(seed, iw::FX_RAND_CH_COLOR)).unwrap_or([255; 4]);
    let fade = distance_fade(iw::vec3_distance(world, cam.origin), v.fade_in_range, v.fade_out_range);
    let rotation = iw::evaluate_rotation_total(&el.vis_samples, n, norm, seed, v.initial_rotation, life_ms as f32).unwrap_or(0.0);
    Some(Visual { size0, size1, color: [r, g, b, (a as f32 * fade).round() as u8], rotation })
}

/// D3D atlas UVs (v down) for the quad corner order below, flipped to v-up.
fn atlas_uvs(uv: FxSpriteAtlasUv) -> [[f32; 2]; 4] {
    uv.corners().map(|[u, v]| [u, 1.0 - v])
}

struct QuadOut {
    quads: Vec<Mw2FxQuad>,
    keys: Vec<(u16, f32)>,
}

impl QuadOut {
    fn push(&mut self, q: Mw2FxQuad, cam: [f32; 3]) {
        if self.quads.len() >= MAX_QUADS || q.corners.iter().any(|c| !finite3(*c)) {
            return;
        }
        let c = scale(add(add(q.corners[0], q.corners[1]), add(q.corners[2], q.corners[3])), 0.25);
        self.keys.push((q.sort, iw::vec3_length_sq(sub(c, cam))));
        self.quads.push(q);
    }

    /// Centre, half extents along `right` / `up`, corners (-,-) (-,+) (+,+) (+,-).
    fn sprite(&mut self, center: [f32; 3], right: [f32; 3], up: [f32; 3], hx: f32, hy: f32, uv: FxSpriteAtlasUv, color: [u8; 4], material: u16, sort: u16, cam: [f32; 3]) {
        let (r, u) = (scale(right, hx), scale(up, hy));
        let corners = [sub(sub(center, r), u), add(sub(center, r), u), add(add(center, r), u), sub(add(center, r), u)];
        self.push(Mw2FxQuad { corners, uv: atlas_uvs(uv), color, material, sort }, cam);
    }
}

fn spin(right: [f32; 3], up: [f32; 3], rotation: f32) -> ([f32; 3], [f32; 3]) {
    let (s, c) = rotation.sin_cos();
    (add(scale(right, c), scale(up, s)), sub(scale(up, c), scale(right, s)))
}

fn material_of(lib: &Library, el: &FxElem, visual: usize) -> Option<u16> {
    match el.visuals.get(visual)? {
        FxVisual::Material(m) if lib.materials.get(*m as usize).is_some_and(|m| m.drawable) => Some(m + 1),
        _ => None,
    }
}

fn draw_effect(lib: &Library, rt: &Runtime, e: &Effect, cam: &Camera, out: &mut QuadOut) {
    let now = rt.msec_now;
    for el in &e.elems {
        let def = lib.elem(e.def, el.def);
        let v = &def.view;
        if !matches!(v.elem_type, et::BILLBOARD | et::ORIENTED | et::TAIL) || v.visual_count == 0 {
            continue;
        }
        if now < el.msec_begin || now >= el.msec_begin.wrapping_add(el.life) {
            continue;
        }
        let age = now.wrapping_sub(el.msec_begin);
        let norm = iw::elem_norm_time(age, el.life);
        let seed = iw::elem_random_seed(e.seed, el.seq, el.msec_begin);
        let Some(material) = material_of(lib, def, iw::elem_visual_index(v.visual_count, seed)) else { continue };
        let orient = iw::get_orientation(v.flags, &e.now, &e.spawn, Some(spawn_params(def, seed)));
        let world = iw::orientation_pos_to_world(orient.origin, orient.axis, el.origin);
        let Some(vis) = visual_state(def, seed, norm, el.life, world, cam) else { continue };
        if vis.size0 <= 0.0 || vis.color[3] == 0 {
            continue;
        }
        let uv = iw::sprite_atlas_uv(&def.atlas, seed, el.seq, age, norm);
        let sort = u16::from(v.sort_order);
        match v.elem_type {
            et::BILLBOARD => {
                let (r, u) = spin(cam.right, cam.up, vis.rotation);
                out.sprite(world, r, u, vis.size0, vis.size0, uv, vis.color, material, sort, cam.origin);
            }
            et::ORIENTED => {
                let axis = iw::get_elem_angles_axis(v.spawn_angles, v.angular_velocity, seed, age as f32, e.now.axis);
                let (r, u) = spin(iw::vec3_normalize(axis[1]), iw::vec3_normalize(axis[2]), vis.rotation);
                out.sprite(world, r, u, vis.size0, vis.size0, uv, vis.color, material, sort, cam.origin);
            }
            _ => {
                if vis.size1 <= 0.0 {
                    continue;
                }
                let vel = iw::get_velocity_at_time(v.flags, el.base_vel, age as f32, el.life.max(1) as f32, &def.vel_local, &def.vel_world, orient.axis, seed);
                let dir = iw::vec3_normalize(vel);
                if iw::vec3_length_sq(dir) < 0.5 {
                    continue;
                }
                let center = iw::tail_anchor_origin(world, dir, vis.size1);
                let Some(axes) = iw::tail_sprite_axes(dir, cam.origin, center) else { continue };
                let (r, u) = spin(axes[0], axes[1], vis.rotation);
                out.sprite(center, r, u, vis.size0, vis.size1, uv, vis.color, material, sort, cam.origin);
            }
        }
    }
    for trail in &e.trails {
        draw_trail(lib, rt, e, trail, cam, out);
    }
}

#[derive(Clone, Copy)]
struct Segment {
    pos: [f32; 3],
    basis: [[f32; 3]; 2],
    rotation: f32,
    size: [f32; 2],
    u: f32,
    color: [u8; 4],
}

fn lerp_segment(a: &Segment, b: &Segment, t: f32) -> Segment {
    let l3 = |x: [f32; 3], y: [f32; 3]| add(x, scale(sub(y, x), t));
    Segment {
        pos: l3(a.pos, b.pos),
        basis: [l3(a.basis[0], b.basis[0]), l3(a.basis[1], b.basis[1])],
        rotation: a.rotation + t * (b.rotation - a.rotation),
        size: [a.size[0] + t * (b.size[0] - a.size[0]), a.size[1] + t * (b.size[1] - a.size[1])],
        u: a.u + t * (b.u - a.u),
        color: a.color,
    }
}

/// `fx::draw::generate_trail_verts`, emitted as one quad per cross-section edge per segment.
fn draw_trail(lib: &Library, rt: &Runtime, e: &Effect, trail: &Trail, cam: &Camera, out: &mut QuadOut) {
    let now = rt.msec_now;
    let def = lib.elem(e.def, trail.def);
    let v = &def.view;
    let Some(td) = def.trail.as_ref() else { return };
    if v.visual_count == 0 || td.verts.is_empty() || td.inds.len() < 2 || td.repeat_dist == 0 {
        return;
    }
    let Some(material) = material_of(lib, def, 0) else { return };
    let Some(first_live) = trail.samples.iter().position(|s| s.msec_begin <= now) else { return };
    let u_offset = iw::trail_compute_u(trail.samples[first_live].spawn_dist, td.repeat_dist, td.scroll_time_msec, now) as f32;
    let last_index = trail.samples.len() - 1;
    let mut segments: Vec<Segment> = Vec::new();
    let mut last: Option<Segment> = None;
    let mut last_norm = 1.0f32;
    for (idx, s) in trail.samples.iter().enumerate().skip(first_live) {
        if s.msec_begin > now {
            continue;
        }
        let seed = iw::trail_random_seed(e.seed, s.seq as i8);
        let life = trail_life(def, e.seed, s.seq);
        let norm = iw::elem_norm_time(now.wrapping_sub(s.msec_begin), life);
        let Some(vis) = visual_state(def, seed, norm, life, s.origin, cam) else { continue };
        if vis.size0 <= 0.0 {
            continue;
        }
        let mut basis = s.basis;
        if v.flags & TRAIL_DIR_BASIS != 0
            && idx != 0
            && let Some(prev) = last
        {
            let d = iw::vec3_normalize(sub(s.origin, prev.pos));
            let side = [d[1], -d[0], 0.0];
            if iw::vec3_length_sq(side) > 0.0 {
                let side = iw::vec3_normalize(side);
                basis = [side, iw::vec3_normalize(cross(d, side))];
            }
        }
        let mut seg = Segment { pos: s.origin, basis, rotation: vis.rotation, size: [vis.size0, vis.size1], u: s.spawn_dist / td.repeat_dist as f32 + u_offset, color: vis.color };
        if norm < 1.0 {
            if idx == 0 {
                if s.seq == 0 {
                    seg.color[3] = 0;
                }
            } else if let Some(prev) = last {
                if last_norm >= 1.0 {
                    segments.push(lerp_segment(&prev, &seg, (1.0 - last_norm) / (norm - last_norm)));
                }
                if idx == last_index {
                    seg.color[3] = 0;
                }
            }
            segments.push(seg);
        }
        last_norm = norm;
        last = Some(seg);
    }
    let sort = u16::from(v.sort_order);
    let emit = |seg: &Segment| -> Vec<iw::FxTrailEmittedVert> {
        let state = iw::FxTrailSegmentDrawState { pos_world: seg.pos, basis: seg.basis, rotation: seg.rotation, size: seg.size, u_coord: seg.u, color_rgba: seg.color };
        td.verts.iter().map(|tv| iw::trail_emit_segment_vert(tv, &state)).collect()
    };
    let mut prev_verts: Option<(Segment, Vec<iw::FxTrailEmittedVert>)> = None;
    for seg in &segments {
        let verts = emit(seg);
        if let Some((pa, pv)) = &prev_verts {
            let color: [u8; 4] = std::array::from_fn(|c| ((u16::from(pa.color[c]) + u16::from(seg.color[c])) / 2) as u8);
            if color[3] != 0 {
                for pair in td.inds.chunks_exact(2) {
                    let (i0, i1) = (pair[0] as usize, pair[1] as usize);
                    let (Some(a0), Some(a1), Some(b0), Some(b1)) = (pv.get(i0), pv.get(i1), verts.get(i0), verts.get(i1)) else { continue };
                    // A geotrail is a star of flat planes; seen edge-on each one is a hard bright line, and
                    // looking down the trail (the RPG from the shooter) the star itself shows. Fade a plane
                    // as it turns edge-on so the trail reads as a soft volume.
                    let n = cross(sub(a1.xyz, a0.xyz), sub(b0.xyz, a0.xyz));
                    let view = sub(scale(add(add(a0.xyz, a1.xyz), add(b0.xyz, b1.xyz)), 0.25), cam.origin);
                    let (nl, vl) = (iw::vec3_length_sq(n).sqrt(), iw::vec3_length_sq(view).sqrt());
                    let facing = if nl > 1e-6 && vl > 1e-6 { ((n[0] * view[0] + n[1] * view[1] + n[2] * view[2]) / (nl * vl)).abs() } else { 1.0 };
                    let s = ((facing - 0.05) / 0.45).clamp(0.0, 1.0);
                    let mut color = color;
                    color[3] = (f32::from(color[3]) * (0.15 + 0.85 * s * s * (3.0 - 2.0 * s))) as u8;
                    if color[3] == 0 {
                        continue;
                    }
                    out.push(
                        Mw2FxQuad {
                            corners: [a0.xyz, a1.xyz, b1.xyz, b0.xyz],
                            uv: [[a0.u, 1.0 - a0.v], [a1.u, 1.0 - a1.v], [b1.u, 1.0 - b1.v], [b0.u, 1.0 - b0.v]],
                            color,
                            material,
                            sort,
                        },
                        cam.origin,
                    );
                }
            }
        }
        prev_verts = Some((*seg, verts));
    }
}

// ---- system ---------------------------------------------------------------------------------

impl System {
    fn tick(&mut self, dt: f32) {
        let System { lib, rt } = self;
        let dt = if dt.is_finite() { dt.clamp(0.0, 1.0) } else { 0.0 };
        rt.seconds += f64::from(dt);
        let now = (rt.seconds * 1000.0).floor() as i64 as i32;
        rt.msec_now = now;
        for slot in 0..MAX_EFFECTS {
            run_slot(lib, rt, slot, now);
        }
        // Children start where (and when) their parent asked; bring them up to now this frame.
        let mut started = 0usize;
        while !rt.spawn_queue.is_empty() && started < MAX_SPAWNS_PER_UPDATE {
            let queue = std::mem::take(&mut rt.spawn_queue);
            for req in queue {
                if started >= MAX_SPAWNS_PER_UPDATE {
                    break;
                }
                started += 1;
                if let Some(h) = spawn_effect(lib, rt, req)
                    && let Some(slot) = rt.slot_of(h)
                {
                    run_slot(lib, rt, slot, now);
                }
            }
        }
        rt.spawn_queue.clear();
        // Sounds whose spawn time has come.
        let mut i = 0;
        while i < rt.sounds.len() {
            if rt.sounds[i].msec <= now {
                let s = rt.sounds.swap_remove(i);
                if rt.sound_text.len() < MAX_SOUND_TEXT {
                    rt.sound_text.push_str(&format!("{}\t{}\t{}\t{}\n", s.alias, s.origin[0], s.origin[1], s.origin[2]));
                }
            } else {
                i += 1;
            }
        }
    }

    fn build_quads(&mut self, cam: Camera) -> usize {
        let System { lib, rt } = self;
        let mut out = QuadOut { quads: std::mem::take(&mut rt.quads), keys: Vec::new() };
        out.quads.clear();
        for e in rt.slots.iter().flatten() {
            draw_effect(lib, rt, e, &cam, &mut out);
        }
        let mut order: Vec<usize> = (0..out.quads.len()).collect();
        order.sort_by(|&a, &b| {
            let (ka, kb) = (out.keys[a], out.keys[b]);
            ka.0.cmp(&kb.0).then(kb.1.partial_cmp(&ka.1).unwrap_or(std::cmp::Ordering::Equal))
        });
        rt.quads = order.into_iter().map(|i| out.quads[i]).collect();
        rt.quads.len()
    }
}

fn run_slot(lib: &Library, rt: &mut Runtime, slot: usize, now: i32) {
    let Some(mut e) = rt.slots[slot].take() else { return };
    if e.msec_last > now {
        rt.slots[slot] = Some(e);
        return;
    }
    let prev = e.msec_last;
    update_effect(lib, rt, &mut e, prev, now);
    let done = e.done();
    rt.slots[slot] = Some(e);
    if done {
        rt.free(slot);
    }
}

fn with_system<R>(f: impl FnOnce(&mut System) -> R) -> Option<R> {
    let mut guard = lock(&SYSTEM);
    guard.as_mut().map(f)
}

unsafe fn read3(p: *const f32) -> Option<[f32; 3]> {
    if p.is_null() {
        return None;
    }
    let v = unsafe { [*p, *p.add(1), *p.add(2)] };
    finite3(v).then_some(v)
}

unsafe fn pose(origin: *const f32, fwd: *const f32, up: *const f32) -> Option<([f32; 3], [[f32; 3]; 3])> {
    let origin = unsafe { read3(origin) }?;
    // IW4 PlayFX with no direction faces the effect up (+Z).
    let fwd = unsafe { read3(fwd) }.unwrap_or([0.0, 0.0, 1.0]);
    Some((origin, axis_from(fwd, unsafe { read3(up) })))
}

/// IWI formats 6..=10: wavelet-compressed BGRA / BGRX / LA / L / A (asset_iw4's decoder).
fn decode_wavelet(bytes: &[u8]) -> Option<(u32, u32, Vec<u8>)> {
    let header = asset_iw4::IwiHeader::parse(bytes).ok()?;
    let format = asset_iw4::wavelet_check_header(&header).ok()?;
    let info = asset_iw4::img_format_info(format)?;
    let stride = asset_iw4::wavelet_pixel_stride(info.channels);
    let mut bits = asset_iw4::WaveletBits::new(bytes.get(asset_iw4::IWI_V8_HEADER_LEN..)?);
    let (w, h) = (u32::from(header.width), u32::from(header.height));
    let (mut parent, mut plane) = (Vec::new(), Vec::new());
    for level in (0..=asset_iw4::wavelet_top_level(&header)).rev() {
        let (lw, lh) = (asset_iw4::wavelet_level_size(w, level), asset_iw4::wavelet_level_size(h, level));
        let mut p = vec![0u8; lw as usize * lh as usize * stride];
        asset_iw4::wavelet_decompress_level(&mut bits, &mut parent, &mut p, lw, lh, info.channels, stride).ok()?;
        parent = p.clone();
        plane = p;
    }
    if plane.len() != w as usize * h as usize * stride {
        return None;
    }
    let rgba = plane
        .chunks_exact(stride)
        .flat_map(|p| match format {
            6 => [p[2], p[1], p[0], p[3]],
            7 => [p[2], p[1], p[0], 255],
            8 => [p[0], p[0], p[0], p[1]],
            9 => [p[0], p[0], p[0], 255],
            _ => [255, 255, 255, p[0]],
        })
        .collect();
    Some((w, h, rgba))
}

/// RGBA8, bottom row first, of `images/<image>.iwi`.
pub(crate) fn texture_rgba(image: &str) -> Option<Rgba> {
    // A leading ',' marks a zone-referenced image; the file has no comma.
    let bytes = crate::models::iwi_bytes(image.trim_start_matches(','))?;
    let (w, h, mut rgba) = match bytes.get(8) {
        Some(6..=10) => decode_wavelet(&bytes)?,
        _ => {
            let iwi = mw2data::models::parse_iwi(&bytes)?;
            let (w, h) = (iwi.width as usize, iwi.height as usize);
            let px = match iwi.format {
                // IW4 4 = luminance, 5 = alpha (images::decode treats 4 as alpha).
                4 => iwi.top_mip.get(..w * h)?.iter().flat_map(|&l| [l, l, l, 255]).collect(),
                5 => iwi.top_mip.get(..w * h)?.iter().flat_map(|&a| [255, 255, 255, a]).collect(),
                f => crate::images::decode(f, w, h, &iwi.top_mip)?,
            };
            (iwi.width, iwi.height, px)
        }
    };
    let (w, h) = (w as usize, h as usize);
    let row = w * 4;
    for y in 0..h / 2 {
        let (a, b) = rgba.split_at_mut((h - 1 - y) * row);
        a[y * row..y * row + row].swap_with_slice(&mut b[..row]);
    }
    Some(Arc::new((w as u32, h as u32, rgba)))
}

// ---- C ABI ----------------------------------------------------------------------------------

/// Build the effect table from what `mw2_load_weapons` captured. Returns the effect count
/// (0 = nothing captured yet). Idempotent.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_fx_init() -> u32 {
    catch_unwind(|| {
        let mut sys = lock(&SYSTEM);
        if let Some(s) = sys.as_ref() {
            return s.lib.effects.len() as u32;
        }
        let Some(cap) = lock(&CAPTURED).take() else { return 0 };
        if cap.effects.is_empty() {
            return 0;
        }
        let lib = Library::build(cap);
        let n = lib.effects.len() as u32;
        *sys = Some(System { lib, rt: Runtime::new() });
        n
    })
    .unwrap_or(0)
}

/// Effect id for a name such as `explosions/clusterbomb` (case-insensitive), 0 if missing.
///
/// # Safety
/// `name` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_find(name: *const u8, len: usize) -> u32 {
    if name.is_null() {
        return 0;
    }
    catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(name, len) };
        let Ok(name) = std::str::from_utf8(bytes) else { return 0 };
        with_system(|s| s.lib.by_name.get(&normalize_name(name)).map_or(0, |&i| i + 1)).unwrap_or(0)
    })
    .unwrap_or(0)
}

/// Play an effect at `origin` facing `fwd` (null = +Z) with `up` (null = derived). Returns a
/// handle (0 = failed). One-shot effects free themselves when done; looping ones run until
/// `mw2_fx_stop`.
///
/// # Safety
/// `origin` points to 3 floats; `fwd` / `up` point to 3 floats or are null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_play(fx: u32, origin: *const f32, fwd: *const f32, up: *const f32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some((origin, axis)) = (unsafe { pose(origin, fwd, up) }) else { return 0 };
        with_system(|s| {
            let System { lib, rt } = s;
            let def = fx.checked_sub(1)?;
            let h = spawn_effect(lib, rt, SpawnReq { def, origin, axis, msec: rt.msec_now })?;
            // Runner children of the first spawn start now too.
            let queue = std::mem::take(&mut rt.spawn_queue);
            for req in queue.into_iter().take(MAX_SPAWNS_PER_UPDATE) {
                let _ = spawn_effect(lib, rt, req);
            }
            Some(h)
        })
        .flatten()
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// Re-pose a live effect (attached / moving). Returns 1, or 0 if the handle is gone.
///
/// # Safety
/// As `mw2_fx_play`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_move(handle: u32, origin: *const f32, fwd: *const f32, up: *const f32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some((origin, axis)) = (unsafe { pose(origin, fwd, up) }) else { return 0 };
        with_system(|s| {
            let slot = s.rt.slot_of(handle)?;
            let e = s.rt.slots[slot].as_mut()?;
            e.now = FxOrientFrame { origin, axis };
            Some(1)
        })
        .flatten()
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// Stop spawning (looping elems / trails); what is alive plays out and the effect frees itself.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_fx_stop(handle: u32) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        with_system(|s| {
            if let Some(slot) = s.rt.slot_of(handle)
                && let Some(e) = s.rt.slots[slot].as_mut()
            {
                e.looping = false;
            }
        })
    }));
}

/// Advance every effect by `dt_seconds` and build quads facing this camera. Returns the quad
/// count (read them with `mw2_fx_quads`).
///
/// # Safety
/// `cam_origin` / `cam_fwd` point to 3 floats; `cam_up` points to 3 floats or is null (+Z).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_update(dt_seconds: f32, cam_origin: *const f32, cam_fwd: *const f32, cam_up: *const f32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(origin) = (unsafe { read3(cam_origin) }) else { return 0 };
        let fwd = unsafe { read3(cam_fwd) }.unwrap_or([1.0, 0.0, 0.0]);
        let up = unsafe { read3(cam_up) }.unwrap_or([0.0, 0.0, 1.0]);
        let axis = axis_from(fwd, Some(up));
        // axis = [forward, left, up]; IW4 left is -right.
        let cam = Camera { origin, right: scale(axis[1], -1.0), up: axis[2] };
        with_system(|s| {
            s.rt.camera = origin;
            s.tick(dt_seconds);
            s.build_quads(cam) as u32
        })
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// Copy up to `cap` of the last built quads (sorted by `sort`, then far to near). Returns the
/// number copied.
///
/// # Safety
/// `out` has room for `cap` quads.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_quads(out: *mut Mw2FxQuad, cap: u32) -> u32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        with_system(|s| {
            let n = s.rt.quads.len().min(cap as usize);
            unsafe { std::ptr::copy_nonoverlapping(s.rt.quads.as_ptr(), out, n) };
            n as u32
        })
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// Blend mode of a material (0 opaque, 1 alpha-test, 2 blend, 3 additive, 4 multiply,
/// 5 screen), writing its name (no NUL, truncated to `cap`). -1 = bad id.
///
/// # Safety
/// `name_out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_material(material: u16, name_out: *mut u8, cap: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        with_system(|s| {
            let m = s.lib.materials.get(usize::from(material).checked_sub(1)?)?;
            if !name_out.is_null() {
                let n = m.name.len().min(cap as usize);
                unsafe { std::ptr::copy_nonoverlapping(m.name.as_ptr(), name_out, n) };
            }
            Some(m.blend)
        })
        .flatten()
        .unwrap_or(-1)
    }))
    .unwrap_or(-1)
}

/// The material's colour image as RGBA8, bottom row first (as `mw2_hud_image`). Writes
/// width/height; copies when `out` is non-null and `cap` is large enough. Returns the byte
/// size, or 0 if the image can't be found or decoded.
///
/// # Safety
/// `w` / `h` valid; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_texture(material: u16, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        // Decode outside the lock: reading the iwd archives takes tens of ms.
        let cached = with_system(|s| match s.rt.textures.get(&material) {
            Some(hit) => Some(Ok(hit.clone())),
            None => s.lib.materials.get(usize::from(material).checked_sub(1)?).map(|m| Err(m.image.clone())),
        })
        .flatten();
        let rgba = match cached {
            Some(Ok(hit)) => hit,
            Some(Err(image)) => {
                let rgba = image.as_deref().and_then(texture_rgba);
                with_system(|s| s.rt.textures.insert(material, rgba.clone()));
                rgba
            }
            None => None,
        };
        let Some(rgba) = rgba else { return 0 };
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

/// Drain sound elems that played since the last call, as UTF-8 lines `alias\tx\ty\tz\n`.
/// Returns bytes written; whole lines only, the rest stays queued for the next call.
///
/// # Safety
/// `out` has room for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_fx_sounds(out: *mut u8, cap: u32) -> u32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        with_system(|s| {
            let text = &mut s.rt.sound_text;
            let mut n = 0usize;
            for line in text.split_inclusive('\n') {
                if n + line.len() > cap as usize {
                    break;
                }
                n += line.len();
            }
            unsafe { std::ptr::copy_nonoverlapping(text.as_ptr(), out, n) };
            text.drain(..n);
            n as u32
        })
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// Live effect count (debug).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_fx_live() -> u32 {
    catch_unwind(|| with_system(|s| s.rt.live as u32).unwrap_or(0)).unwrap_or(0)
}

/// The FX system is one global; tests that drive it take turns.
#[cfg(test)]
pub(crate) static TEST_LOCK: Mutex<()> = Mutex::new(());

#[cfg(test)]
mod tests {
    use super::*;

    const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

    fn quads() -> Vec<Mw2FxQuad> {
        let n = with_system(|s| s.rt.quads.len()).unwrap_or(0);
        let mut out = vec![Mw2FxQuad::default(); n];
        let got = unsafe { mw2_fx_quads(out.as_mut_ptr(), n as u32) } as usize;
        out.truncate(got);
        out
    }

    /// Real-install FX smoke test (run: cargo test --release -p mw2sim fx_ -- --ignored --nocapture).
    #[test]
    #[ignore]
    fn fx_effects_from_real_install() {
        let _turn = lock(&TEST_LOCK);
        assert!(unsafe { crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
        let count = mw2_fx_init();
        assert!(count > 100, "effects: {count}");
        assert_eq!(mw2_fx_init(), count, "idempotent");
        let names: Vec<String> = with_system(|s| s.lib.effects.iter().map(|e| e.name.clone()).collect()).unwrap();
        let trailish: Vec<&String> = names.iter().filter(|n| ["smoke_trail", "rocket", "missile", "geotrail"].iter().any(|k| n.contains(k))).collect();
        eprintln!("{count} effects; trail/rocket names: {trailish:?}");

        let cam = [0.0f32, -2000.0, 0.0];
        let cam_fwd = [0.0f32, 1.0, 0.0];
        let cam_up = [0.0f32, 0.0, 1.0];
        let cases: &[(&str, f32, [f32; 3])] = &[
            ("explosions/clusterbomb", 0.0, [0.0, 0.0, 1.0]),
            ("explosions/stealth_bomb_mp", 0.0, [0.0, 0.0, 1.0]),
            ("fire/jet_afterburner", 7000.0, [-1.0, 0.0, 0.0]),
            ("smoke/jet_contrail", 7000.0, [-1.0, 0.0, 0.0]),
            ("smoke/smoke_trail_white_heli_emitter", 1500.0, [-1.0, 0.0, 0.0]),
            ("fire/fire_smoke_trail_l_emitter", 1500.0, [-1.0, 0.0, 0.0]),
        ];
        let mut materials = std::collections::BTreeSet::new();
        let mut explosion_quads = 0;
        for &(name, speed, fwd) in cases {
            let id = unsafe { mw2_fx_find(name.as_ptr(), name.len()) };
            assert!(id != 0, "missing {name}");
            let mut pos = [0.0f32; 3];
            let h = unsafe { mw2_fx_play(id, pos.as_ptr(), fwd.as_ptr(), std::ptr::null()) };
            assert!(h != 0, "play {name}");
            let (mut peak, mut extent, mut used) = (0u32, 0f32, std::collections::BTreeSet::new());
            for _ in 0..120 {
                if speed != 0.0 {
                    pos[0] += speed / 60.0;
                    assert_eq!(unsafe { mw2_fx_move(h, pos.as_ptr(), fwd.as_ptr(), std::ptr::null()) }, 1, "{name} handle alive");
                }
                let n = unsafe { mw2_fx_update(1.0 / 60.0, cam.as_ptr(), cam_fwd.as_ptr(), cam_up.as_ptr()) };
                peak = peak.max(n);
                for q in quads() {
                    used.insert(q.material);
                    let c = scale(add(add(q.corners[0], q.corners[1]), add(q.corners[2], q.corners[3])), 0.25);
                    extent = extent.max(iw::vec3_distance(c, pos));
                }
            }
            mw2_fx_stop(h);
            let mut drain = 0;
            while mw2_fx_live() > 0 && drain < 60 * 120 {
                unsafe { mw2_fx_update(1.0 / 60.0, cam.as_ptr(), cam_fwd.as_ptr(), cam_up.as_ptr()) };
                drain += 1;
            }
            let mut sounds = vec![0u8; 4096];
            let sn = unsafe { mw2_fx_sounds(sounds.as_mut_ptr(), 4096) } as usize;
            let (fails_fx, fails_elem) = with_system(|s| (s.rt.failed_effects, s.rt.failed_elems)).unwrap();
            eprintln!(
                "{name}: peak {peak} quads, max extent {extent:.0} in, {} materials {used:?}, drained in {drain} frames (live {}), pool misses fx {fails_fx} elem {fails_elem}, sounds {:?}",
                used.len(),
                mw2_fx_live(),
                String::from_utf8_lossy(&sounds[..sn])
            );
            assert_eq!(mw2_fx_live(), 0, "{name} frees itself");
            if name == "explosions/clusterbomb" {
                explosion_quads = peak;
                for &m in &used {
                    let (mut w, mut hgt) = (0u32, 0u32);
                    let bytes = unsafe { mw2_fx_texture(m, &mut w, &mut hgt, std::ptr::null_mut(), 0) };
                    assert!(w > 0 && hgt > 0 && bytes == w * hgt * 4, "clusterbomb material {m} texture");
                }
            }
            materials.extend(used);
        }
        assert!(explosion_quads > 0);
        // Sound elems queue their alias where they spawn.
        let with_sound = with_system(|s| s.lib.effects.iter().position(|e| e.elems.iter().any(|el| el.view.elem_type == et::SOUND))).unwrap();
        if let Some(i) = with_sound {
            let h = unsafe { mw2_fx_play(i as u32 + 1, [10.0f32, 20.0, 30.0].as_ptr(), std::ptr::null(), std::ptr::null()) };
            for _ in 0..30 {
                unsafe { mw2_fx_update(1.0 / 60.0, cam.as_ptr(), cam_fwd.as_ptr(), cam_up.as_ptr()) };
            }
            mw2_fx_stop(h);
            let mut buf = vec![0u8; 4096];
            let n = unsafe { mw2_fx_sounds(buf.as_mut_ptr(), 4096) } as usize;
            let name = with_system(|s| s.lib.effects[i].name.clone()).unwrap();
            eprintln!("sound effect {name}: {:?}", String::from_utf8_lossy(&buf[..n]));
            assert!(n > 0, "{name} queued its sound");
            while mw2_fx_live() > 0 {
                unsafe { mw2_fx_update(0.25, cam.as_ptr(), cam_fwd.as_ptr(), cam_up.as_ptr()) };
            }
        }
        for m in materials {
            let mut name = [0u8; 128];
            let blend = unsafe { mw2_fx_material(m, name.as_mut_ptr(), 128) };
            let len = name.iter().position(|&b| b == 0).unwrap_or(128);
            let (mut w, mut h) = (0u32, 0u32);
            let bytes = unsafe { mw2_fx_texture(m, &mut w, &mut h, std::ptr::null_mut(), 0) };
            let mut px = vec![0u8; bytes as usize];
            let copied = unsafe { mw2_fx_texture(m, &mut w, &mut h, px.as_mut_ptr(), bytes) };
            let alpha_mean = px.chunks_exact(4).map(|p| u32::from(p[3])).sum::<u32>() / (w * h).max(1);
            eprintln!("material {m:3} blend {blend} {:40} texture {w}x{h} ({copied} bytes, mean alpha {alpha_mean})", String::from_utf8_lossy(&name[..len]));
        }
        assert_eq!(unsafe { mw2_fx_material(0, std::ptr::null_mut(), 0) }, -1);
        assert_eq!(unsafe { mw2_fx_play(0, [0.0f32; 3].as_ptr(), std::ptr::null(), std::ptr::null()) }, 0);
    }
}

#[cfg(test)]
mod render_check {
    //! CPU splat of the quads (perspective, textured, blended) into PNGs under target/fx_render/,
    //! to eyeball orientation, atlas frames and blend modes without the game.
    use super::*;

    fn png(path: &std::path::Path, w: usize, h: usize, rgb: &[u8]) {
        fn crc(data: &[u8]) -> u32 {
            let mut c = 0xffff_ffffu32;
            for &b in data {
                c ^= u32::from(b);
                for _ in 0..8 {
                    c = if c & 1 != 0 { 0xedb8_8320 ^ (c >> 1) } else { c >> 1 };
                }
            }
            !c
        }
        let mut raw = Vec::with_capacity((w * 3 + 1) * h);
        for y in 0..h {
            raw.push(0);
            raw.extend_from_slice(&rgb[y * w * 3..(y + 1) * w * 3]);
        }
        // zlib with stored deflate blocks.
        let mut z = vec![0x78, 0x01];
        let chunks: Vec<&[u8]> = raw.chunks(65535).collect();
        for (i, chunk) in chunks.iter().enumerate() {
            z.push(u8::from(i + 1 == chunks.len()));
            let n = chunk.len() as u16;
            z.extend_from_slice(&n.to_le_bytes());
            z.extend_from_slice(&(!n).to_le_bytes());
            z.extend_from_slice(chunk);
        }
        let (mut a, mut b) = (1u32, 0u32);
        for &x in &raw {
            a = (a + u32::from(x)) % 65521;
            b = (b + a) % 65521;
        }
        z.extend_from_slice(&((b << 16) | a).to_be_bytes());
        let mut out = vec![0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a];
        let mut chunk = |ty: &[u8], data: &[u8]| {
            out.extend_from_slice(&(data.len() as u32).to_be_bytes());
            let mut c = ty.to_vec();
            c.extend_from_slice(data);
            out.extend_from_slice(&c);
            out.extend_from_slice(&crc(&c).to_be_bytes());
        };
        let mut ihdr = Vec::new();
        ihdr.extend_from_slice(&(w as u32).to_be_bytes());
        ihdr.extend_from_slice(&(h as u32).to_be_bytes());
        ihdr.extend_from_slice(&[8, 2, 0, 0, 0]);
        chunk(b"IHDR", &ihdr);
        chunk(b"IDAT", &z);
        chunk(b"IEND", &[]);
        std::fs::write(path, out).unwrap();
    }

    fn splat(quads: &[Mw2FxQuad], cam: [f32; 3], fwd: [f32; 3], up: [f32; 3], textures: &mut HashMap<u16, Option<Rgba>>, size: usize) -> Vec<u8> {
        let axis = axis_from(fwd, Some(up));
        let (f, r, u) = (axis[0], scale(axis[1], -1.0), axis[2]);
        let focal = size as f32 * 0.5 / 35f32.to_radians().tan();
        let mut img = vec![[0.05f32, 0.06, 0.08]; size * size];
        for q in quads {
            let tex = textures
                .entry(q.material)
                .or_insert_with(|| {
                    let (mut w, mut h) = (0u32, 0u32);
                    let n = unsafe { mw2_fx_texture(q.material, &mut w, &mut h, std::ptr::null_mut(), 0) };
                    let mut px = vec![0u8; n as usize];
                    (n > 0 && unsafe { mw2_fx_texture(q.material, &mut w, &mut h, px.as_mut_ptr(), n) } == n).then(|| Arc::new((w, h, px)))
                })
                .clone();
            let Some(tex) = tex else { continue };
            let blend = unsafe { mw2_fx_material(q.material, std::ptr::null_mut(), 0) };
            let proj: Vec<Option<[f32; 2]>> = q
                .corners
                .iter()
                .map(|&p| {
                    let d = sub(p, cam);
                    let z = d[0] * f[0] + d[1] * f[1] + d[2] * f[2];
                    (z > 1.0).then(|| {
                        let x = d[0] * r[0] + d[1] * r[1] + d[2] * r[2];
                        let y = d[0] * u[0] + d[1] * u[1] + d[2] * u[2];
                        [size as f32 * 0.5 + focal * x / z, size as f32 * 0.5 - focal * y / z]
                    })
                })
                .collect();
            for tri in [[0usize, 1, 2], [0, 2, 3]] {
                let (Some(a), Some(b), Some(c)) = (proj[tri[0]], proj[tri[1]], proj[tri[2]]) else { continue };
                let area = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
                if area.abs() < 1e-6 {
                    continue;
                }
                let x0 = a[0].min(b[0]).min(c[0]).max(0.0) as usize;
                let x1 = (a[0].max(b[0]).max(c[0]).max(0.0) as usize + 1).min(size);
                let y0 = a[1].min(b[1]).min(c[1]).max(0.0) as usize;
                let y1 = (a[1].max(b[1]).max(c[1]).max(0.0) as usize + 1).min(size);
                for y in y0..y1 {
                    for x in x0..x1 {
                        let p = [x as f32 + 0.5, y as f32 + 0.5];
                        let w1 = ((p[0] - a[0]) * (c[1] - a[1]) - (p[1] - a[1]) * (c[0] - a[0])) / area;
                        let w2 = ((b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])) / area;
                        let w0 = 1.0 - w1 - w2;
                        if w0 < 0.0 || w1 < 0.0 || w2 < 0.0 {
                            continue;
                        }
                        let uv: [f32; 2] = std::array::from_fn(|k| w0 * q.uv[tri[0]][k] + w1 * q.uv[tri[1]][k] + w2 * q.uv[tri[2]][k]);
                        let (tw, th, px) = &*tex;
                        let tx = ((uv[0].rem_euclid(1.0) * *tw as f32) as usize).min(*tw as usize - 1);
                        let ty = ((uv[1].clamp(0.0, 1.0) * *th as f32) as usize).min(*th as usize - 1);
                        let t = &px[(ty * *tw as usize + tx) * 4..][..4];
                        let src: [f32; 3] = std::array::from_fn(|k| f32::from(t[k]) * f32::from(q.color[k]) / 65025.0);
                        let alpha = f32::from(t[3]) * f32::from(q.color[3]) / 65025.0;
                        let d = &mut img[y * size + x];
                        for k in 0..3 {
                            d[k] = match blend {
                                3 => d[k] + src[k] * alpha,
                                4 => d[k] * src[k],
                                5 => d[k] + src[k] * alpha * (1.0 - d[k]),
                                0 | 1 => {
                                    if alpha > 0.5 { src[k] } else { d[k] }
                                }
                                _ => d[k] * (1.0 - alpha) + src[k] * alpha,
                            };
                        }
                    }
                }
            }
        }
        img.iter().flat_map(|c| c.map(|v| (v.clamp(0.0, 1.0) * 255.0) as u8)).collect()
    }

    /// Run: cargo test --release -p mw2sim fx_render -- --ignored --nocapture
    #[test]
    #[ignore]
    fn fx_render_frames() {
        let _turn = lock(&TEST_LOCK);
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        assert!(unsafe { crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
        assert!(mw2_fx_init() > 100);
        let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/fx_render");
        std::fs::create_dir_all(&dir).unwrap();
        let mut textures = HashMap::new();
        let cases: &[(&str, f32, [f32; 3], [f32; 3], &[usize])] = &[
            ("explosions/clusterbomb", 0.0, [0.0, -600.0, 100.0], [0.0, 0.0, 1.0], &[6, 20, 60, 120]),
            ("explosions/clusterbomb_exp", 0.0, [0.0, -600.0, 100.0], [0.0, 0.0, 1.0], &[6, 20, 60]),
            ("explosions/aerial_explosion", 0.0, [0.0, -1200.0, 100.0], [0.0, 0.0, 1.0], &[10, 40]),
            ("fire/jet_afterburner", 0.0, [0.0, -300.0, 0.0], [-1.0, 0.0, 0.0], &[30]),
            ("smoke/jet_contrail", 3000.0, [0.0, -3000.0, 0.0], [-1.0, 0.0, 0.0], &[90]),
            ("fire/fire_smoke_trail_l_emitter", 1000.0, [0.0, -1500.0, 0.0], [-1.0, 0.0, 0.0], &[90]),
            ("smoke/smoke_geotrail_rpg", 1500.0, [0.0, -900.0, 0.0], [-1.0, 0.0, 0.0], &[10, 30, 60]),
            ("smoke/smoke_geotrail_fraggrenade", 940.0, [0.0, -600.0, 0.0], [-1.0, 0.0, 0.0], &[10, 30]),
            ("smoke/smoke_geotrail_m203", 1000.0, [0.0, -600.0, 0.0], [-1.0, 0.0, 0.0], &[10, 30]),
            // The nuke as _nuke.gsc places it: 5000 ahead of the player, facing back at him.
            ("explosions/player_death_nuke_flash", 0.0, [0.0, -5000.0, 60.0], [0.0, -1.0, 0.0], &[30, 90, 180, 300, 480]),
            ("explosions/player_death_nuke_flash#far", 0.0, [0.0, -30000.0, 3000.0], [0.0, -1.0, 0.0], &[90, 240, 480]),
            // _nuke.gsc's own pose: angles (0, yaw + 180, 90) - up is the forward's right.
            ("explosions/player_death_nuke_flash#mw2", 0.0, [0.0, -30000.0, 3000.0], [0.0, -1.0, 0.0], &[90, 240, 480]),
            ("explosions/player_death_nuke_flash#mw2near", 0.0, [0.0, -5000.0, 60.0], [0.0, -1.0, 0.0], &[60, 150, 300]),
        ];
        let only = std::env::var("FX_ONLY").ok();
        for &(name, speed, cam, fwd, frames) in cases {
            if only.as_deref().is_some_and(|o| !name.contains(o)) {
                continue;
            }
            let tag = name;
            let name = name.split('#').next().unwrap();
            let id = unsafe { mw2_fx_find(name.as_ptr(), name.len()) };
            let mut pos = [0.0f32; 3];
            if speed != 0.0 {
                pos[0] = -speed * 0.75;
            }
            let up = [fwd[1], -fwd[0], 0.0];
            let up_ptr = if tag.contains("#mw2") { up.as_ptr() } else { std::ptr::null() };
            let h = unsafe { mw2_fx_play(id, pos.as_ptr(), fwd.as_ptr(), up_ptr) };
            let cam_fwd = iw::vec3_normalize(sub([0.0, 0.0, cam[2] * 0.5], cam));
            for frame in 1..=*frames.last().unwrap() {
                if speed != 0.0 {
                    pos[0] += speed / 60.0;
                    unsafe { mw2_fx_move(h, pos.as_ptr(), fwd.as_ptr(), std::ptr::null()) };
                }
                let n = unsafe { mw2_fx_update(1.0 / 60.0, cam.as_ptr(), cam_fwd.as_ptr(), [0.0f32, 0.0, 1.0].as_ptr()) };
                if frames.contains(&frame) {
                    let qs = with_system(|s| s.rt.quads.clone()).unwrap();
                    let img = splat(&qs, cam, cam_fwd, [0.0, 0.0, 1.0], &mut textures, 512);
                    let file = dir.join(format!("{}_{frame:03}.png", tag.replace('/', "_").replace('#', "_")));
                    png(&file, 512, 512, &img);
                    eprintln!("{name} frame {frame}: {n} quads -> {}", file.display());
                }
            }
            mw2_fx_stop(h);
            while mw2_fx_live() > 0 {
                unsafe { mw2_fx_update(0.25, cam.as_ptr(), cam_fwd.as_ptr(), std::ptr::null()) };
            }
        }
    }
}

/// 1 if the effect (`mw2_fx_find` id) keeps spawning until stopped (looping elems), 0 if it plays
/// out on its own: only the latter can be replayed elsewhere from a single "play" (multiplayer).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_fx_is_looping(fx: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        with_system(|s| {
            let def = s.lib.effects.get(fx.checked_sub(1)? as usize)?;
            Some(i32::from(def.view.msec_looping_life != 0 && def.view.looping_count > 0))
        })
        .flatten()
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}
