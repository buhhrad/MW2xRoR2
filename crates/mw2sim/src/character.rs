//! MW2 third-person player characters: a faction's body + head as one skinned rig, animated
//! by MW2's own player animation script (`mp/playeranim.script`) the way IW4L's remote-player
//! pipeline does (`render_anim::remote_body` + `xmodel_runtime::player_body`).
//!
//! What is MW2's own logic here, ported from IW4L:
//! * the movement-type choice (`footsteps_anim_move_type` + `PM_MOVE_ANIM_TABLE`: idle / walk /
//!   run / sprint x stand / crouch / prone x forward / back),
//! * the script itself: conditions (`playerAnimType`, `weaponclass`, `movetype`, `strafing`,
//!   `weapon_position`, `firing`...), first-match item selection, `commands[client % n]`, event
//!   items for fire / reload / melee / jump / land / pain / death / stance changes,
//! * `play_anim`'s legs/torso timers and restart toggles, the blend times
//!   (`client_anim_blend_ms`), the legs-parent 0.01 weight under a torso overlay, locomotion
//!   phase carry-over, and the playback rate from the real ground speed over the clip's own
//!   move speed (`client_anim_playback_rate`),
//! * the aim controller (`apply_player_controller`: pitch spread over back_low / back_mid /
//!   back_up, prone / crouch variants) and the legs-vs-torso yaw swing (`player_angles`).
//!
//! Akimbo is IW4L's `last_weapon_hand == 1` (from the held weapon: `_akimbo` rows).
//!
//! Simplified: no leaning, no riot shield / turret conditions (written false), no
//! flinch / stumble movetypes, mantle is a fixed override, and the torso yaw never twists
//! (the host faces the model along the view yaw; the legs swing to the movement direction).
//!
//! Space: the pose handed to the host is Unity space (metres, Y up, Z forward, left-handed)
//! relative to the model origin (`tag_origin`, at the feet, facing +Z), MODEL-SPACE per bone
//! (not parent-local): the host makes every bone a direct child of one root transform, exactly
//! as the first-person viewmodel does. Mesh, bindposes and winding follow `viewmodel.rs`.

use std::collections::HashMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use anim_iw4::{
    PLAYER_ANIM_INDEX_MASK, PLAYER_ANIM_RESTART_TOGGLE, advance_goal_weight, advance_leaf_time, client_anim_blend_ms,
    client_anim_playback_rate, goal_time_from_blend_ms, random, XANIM_LEGS_PARENT_WEIGHT_WHEN_TORSO,
};
use glam::{Mat4, Quat, Vec3};
use mw2data::character as data;
use mw2data::character::{AnimCommand, AnimItem, PlayerAnimScript};
use mw2data::models::Mesh;
use mw2data::rig::RigModel;
use xmodel_runtime::{
    AnimClip, AnimInstance, Attach, DObj, PlayerControllerInput, RawXAnimParts, apply_player_controller,
    tp_head_attach_tag, tp_weapon_attach_tag,
};

use crate::{Mw2SurfaceInfo, models};

pub const INCHES_TO_METRES: f32 = 0.0254;

/// `Mw2CharacterInput::event` bits (one-shot: set for the step the thing happens).
pub const EV_FIRE: u32 = 1;
pub const EV_RELOAD: u32 = 2;
pub const EV_MELEE: u32 = 4;
/// Offhand throw; plays MW2's grenade-throw FIREWEAPON entry (set the grenade as `weapon`).
pub const EV_THROW: u32 = 8;
pub const EV_JUMP: u32 = 16;
pub const EV_LAND: u32 = 32;
pub const EV_PAIN: u32 = 64;
/// Starts a mantle. Bits 8-11 of `event`: the sim's mantle transition + 1 (`ps.mantleState`
/// transIndex, 0-6: the 57 .. 21 unit climbs); 0 = unknown (a ~0.9 s `MANTLE_OVER_MID`).
pub const EV_MANTLE: u32 = 128;
/// With `EV_MELEE`: the melee lunge (MW2's charged melee).
pub const EV_MELEE_CHARGE: u32 = 1 << 12;
/// With `EV_RELOAD`: the magazine was empty (the weapon's reloadEmpty time).
pub const EV_RELOAD_EMPTY: u32 = 1 << 13;
/// With `EV_RELOAD`: Sleight of Hand (reload states at 1 / perk_weapReloadMultiplier).
pub const EV_RELOAD_FAST: u32 = 1 << 14;

// playeranim.script event indices (anim_iw4::ANIM_ET_NAMES) and movetypes (ANIM_MT_NAMES).
const ET_PAIN: u8 = 0;
const ET_DEATH: u8 = 1;
const ET_FIREWEAPON: u8 = 2;
const ET_JUMP: u8 = 3;
const ET_LAND: u8 = 5;
const ET_DROPWEAPON: u8 = 6;
const ET_RELOAD: u8 = 10;
const ET_CROUCH_TO_PRONE: u8 = 11;
const ET_PRONE_TO_CROUCH: u8 = 12;
const ET_STAND_TO_CROUCH: u8 = 13;
const ET_CROUCH_TO_STAND: u8 = 14;
const ET_STAND_TO_PRONE: u8 = 15;
const ET_PRONE_TO_STAND: u8 = 16;
const ET_MELEEATTACK: u8 = 17;
const ET_KNIFE_MELEE: u8 = 18;
const ET_KNIFE_MELEE_CHARGE: u8 = 19;

const MT_IDLE: u8 = 1;
const MT_IDLECR: u8 = 2;
const MT_IDLEPRONE: u8 = 3;
const MT_IDLELASTSTAND: u8 = 51;
const MT_SPRINT: u8 = 20;
const MT_MANTLE_UP_57: u8 = 22;
const MT_MANTLE_OVER_MID: u8 = 30;
/// IW4 mantle: each transition's climb (mp_mantle_up_57 .. _21) and the "over" that follows it
/// (movement_iw4 MANTLE_TRANS_OVER_ANIM: 8 high, 9 mid, 10 low -> movetypes 29 / 30 / 31).
const MANTLE_UP_ANIMS: [&str; 7] = ["mp_mantle_up_57", "mp_mantle_up_51", "mp_mantle_up_45", "mp_mantle_up_39", "mp_mantle_up_33", "mp_mantle_up_27", "mp_mantle_up_21"];
const MANTLE_OVER_FOR: [u8; 7] = [29, 29, 30, 30, 30, 31, 31];
const MANTLE_OVER_ANIMS: [&str; 3] = ["mp_mantle_over_high", "mp_mantle_over_mid", "mp_mantle_over_low"];
/// `PM_MOVE_ANIM_TABLE` (movement_iw4::footstep): index = stumble + 2 walking + 4 stance + 16 back,
/// stance 0 stand / 1 prone / 2 crouch.
const PM_MOVE_ANIM_TABLE: [u8; 32] = [
    10, 36, 4, 38, 8, 8, 8, 8, 12, 40, 6, 40, 52, 52, 52, 52, 11, 37, 5, 39, 9, 9, 9, 9, 13, 41, 7, 41, 53, 53, 53, 53,
];
const PLAYER_MOVE_THRESHOLD: f32 = 10.0;

// ---------------------------------------------------------------------------------------------
// Shared store: scripts, player XAnims, models (loaded from the user's install on first use)
// ---------------------------------------------------------------------------------------------

struct Store {
    common: data::CommonCharData,
    script: PlayerAnimScript,
    anims: HashMap<String, Arc<RawXAnimParts>>,
    clips: Mutex<HashMap<String, Option<Arc<AnimClip>>>>,
    props: Mutex<HashMap<String, (i32, bool)>>,
    zone_dir: PathBuf,
    rigs: Mutex<HashMap<String, Arc<RigModel>>>,
    meshes: Mutex<HashMap<String, Mesh>>,
    /// zones that were walked for a model name that was not there
    missing: Mutex<HashMap<String, ()>>,
}

static STORE: Mutex<Option<Arc<Store>>> = Mutex::new(None);
static NEXT_CLIENT: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

impl Store {
    fn clip(&self, name: &str) -> Option<Arc<AnimClip>> {
        let mut clips = self.clips.lock().ok()?;
        if let Some(c) = clips.get(name) {
            return c.clone();
        }
        let decoded = self.anims.get(name).and_then(|raw| AnimClip::from_parts(raw).ok()).map(Arc::new);
        clips.insert(name.to_owned(), decoded.clone());
        decoded
    }

    /// (authored blend ms, plays only while standing still) for an anim name.
    fn props(&self, anim: &str) -> (i32, bool) {
        let Ok(mut p) = self.props.lock() else { return (0, false) };
        *p.entry(anim.to_owned()).or_insert_with(|| self.script.properties(anim))
    }
}

/// Point the character system at a zone folder holding `common_mp.ff` (and the map zones).
/// Safe to call repeatedly; the first successful load is kept.
pub fn init(zone_dir: &Path) -> Result<(), String> {
    ensure_store(Some(zone_dir)).map(|_| ())
}

fn ensure_store(zone_dir: Option<&Path>) -> Result<Arc<Store>, String> {
    let mut guard = STORE.lock().map_err(|_| "character store poisoned".to_string())?;
    if let Some(s) = guard.as_ref() {
        return Ok(s.clone());
    }
    let dir = match zone_dir {
        Some(d) => d.to_path_buf(),
        None => models::zone_dir()
            .or_else(|| models::main_dir().and_then(|m| m.parent().map(|root| root.join("zone").join("english"))))
            .ok_or("MW2 install not known yet: call mw2_load_weapons or mw2_character_init first")?,
    };
    let common_path = dir.join("common_mp.ff");
    let mut common = data::load_common(&common_path).map_err(|e| format!("{}: {e}", common_path.display()))?;
    let atr = common.raw_file("animtrees/multiplayer.atr").ok_or("multiplayer.atr missing from common_mp")?;
    let names = data::atr_names(atr);
    let script_bytes = common.raw_file("mp/playeranim.script").ok_or("playeranim.script missing from common_mp")?;
    let script = data::parse_player_anim_script(script_bytes, &|n| names.contains(n))
        .map_err(|e| format!("playeranim.script: {e}"))?;
    let anims = std::mem::take(&mut common.xanims).into_iter().map(|(k, v)| (k, Arc::new(v))).collect();
    let store = Arc::new(Store {
        common,
        script,
        anims,
        clips: Mutex::new(HashMap::new()),
        props: Mutex::new(HashMap::new()),
        zone_dir: dir,
        rigs: Mutex::new(HashMap::new()),
        meshes: Mutex::new(HashMap::new()),
        missing: Mutex::new(HashMap::new()),
    });
    *guard = Some(store.clone());
    Ok(store)
}

fn zone_hints(body: &str) -> &'static [&'static str] {
    let b = body;
    if b.contains("us_army") || b.contains("army_sniper") {
        &["mp_highrise", "mp_terminal", "mp_nightshift", "mp_invasion"]
    } else if b.contains("desert_tf141") || b.contains("tf141_desert") {
        &["mp_rust", "mp_afghan", "mp_favela", "mp_quarry", "mp_rundown", "mp_boneyard"]
    } else if b.contains("forest_tf141") || b.contains("tf141_forest") {
        &["mp_brecourt", "mp_estate", "mp_underpass"]
    } else if b.contains("tf141") {
        &["mp_derail"]
    } else if b.contains("airborne") {
        &["mp_brecourt", "mp_estate", "mp_highrise", "mp_nightshift", "mp_terminal"]
    } else if b.contains("arab") {
        &["mp_rust", "mp_afghan", "mp_checkpoint", "mp_boneyard", "mp_invasion"]
    } else if b.contains("arctic") {
        &["mp_derail", "mp_subbase"]
    } else if b.contains("militia") || b.contains("miltia") {
        &["mp_favela", "mp_quarry", "mp_rundown", "mp_underpass"]
    } else if b.contains("seal_udt") || b.contains("udt") {
        &["mp_checkpoint", "mp_subbase", "mp_rundown"]
    } else {
        &[]
    }
}

impl Store {
    /// Body + head models, cached; walks map zones (hinted ones first) until the body is found.
    /// `heads` is the faction's head pool, all loaded with the body (a skin's character-select build
    /// uses head 0; the spawn rolls another); `prefer` picks one (wraps), else the next that loaded.
    /// `extra` (the character's viewhands) loads along with them.
    fn models_for(&self, body: &str, heads: &[String], prefer: usize, extra: &[String]) -> Option<(Arc<RigModel>, Option<Arc<RigModel>>)> {
        let have = |n: &str| self.rigs.lock().ok().and_then(|r| r.get(n).cloned());
        let pick = || -> Option<Arc<RigModel>> {
            (0..heads.len()).map(|i| &heads[(prefer + i) % heads.len()]).find_map(|h| have(h))
        };
        // The pool loads with the body (same zone), so a cached body has whichever heads exist.
        if let Some(b) = have(body) {
            return Some((b, pick()));
        }
        let mut want: Vec<String> = heads.to_vec();
        want.extend(extra.iter().cloned());
        let first = self.models_for_load(body, &want)?;
        Some((first, pick()))
    }

    fn models_for_load(&self, body: &str, heads: &[String]) -> Option<Arc<RigModel>> {
        let have = |n: &str| self.rigs.lock().ok().and_then(|r| r.get(n).cloned());
        // `mp_body_seal_soccom_*` (the MP `socom_141` set) ships in no zone of the retail install; only the
        // SP zones carry `body_seal_soccom_*`. Don't scan every map zone (8 s) to find that out.
        if body.contains("soccom") && have(body).is_none() {
            return None;
        }
        if have(body).is_none() {
            let mut zones: Vec<String> = zone_hints(body).iter().map(|z| (*z).to_owned()).collect();
            if let Ok(rd) = std::fs::read_dir(&self.zone_dir) {
                let mut rest: Vec<String> = rd
                    .filter_map(|e| e.ok())
                    .filter_map(|e| e.file_name().to_str().map(str::to_owned))
                    .filter(|n| n.starts_with("mp_") && n.ends_with(".ff") && !n.ends_with("_load.ff"))
                    .map(|n| n.trim_end_matches(".ff").to_owned())
                    .filter(|n| !zones.contains(n))
                    .collect();
                rest.sort();
                zones.extend(rest);
            }
            let mut want = vec![body.to_owned()];
            want.extend(heads.iter().cloned());
            for zone in zones {
                let key = format!("{zone}:{body}");
                if self.missing.lock().ok()?.contains_key(&key) {
                    continue;
                }
                let path = self.zone_dir.join(format!("{zone}.ff"));
                let Ok(loaded) = data::load_models(&path, &want) else { continue };
                let found = loaded.rigs.iter().any(|r| r.name == body);
                if let Ok(mut r) = self.rigs.lock() {
                    for rig in loaded.rigs {
                        r.entry(rig.name.clone()).or_insert_with(|| Arc::new(rig));
                    }
                }
                if let Ok(mut m) = self.meshes.lock() {
                    for mesh in loaded.meshes {
                        m.entry(mesh.name.clone()).or_insert(mesh);
                    }
                }
                if found {
                    break;
                }
                self.missing.lock().ok()?.insert(key, ());
            }
        }
        have(body)
    }
}

// ---------------------------------------------------------------------------------------------
// Geometry helpers (IW4 -> Unity, as in viewmodel.rs)
// ---------------------------------------------------------------------------------------------

fn to_unity(p: [f32; 3], s: f32) -> [f32; 3] {
    [-p[1] * s, p[2] * s, p[0] * s]
}

fn quat_to_unity(q: Quat) -> [f32; 4] {
    [q.y, -q.z, -q.x, q.w]
}

fn axis_matrix(s: f32) -> Mat4 {
    Mat4::from_cols_array(&[0.0, 0.0, s, 0.0, -s, 0.0, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0])
}

#[derive(Clone, Debug)]
pub struct Surface {
    pub index_start: u32,
    pub index_count: u32,
    pub color_map: Option<String>,
    pub material: Option<String>,
    pub state_bits: Option<[u32; 2]>,
}

// ---------------------------------------------------------------------------------------------
// Animator
// ---------------------------------------------------------------------------------------------

#[derive(Clone, Copy, Debug, Default)]
struct Conds {
    written: u32,
    value: [u64; 18],
}

impl Conds {
    fn set_value(&mut self, cond: u8, v: u32) {
        self.written |= 1 << cond;
        self.value[cond as usize] = u64::from(v);
    }
    fn set_bit(&mut self, cond: u8, bit: u8) {
        if bit < 64 {
            self.written |= 1 << cond;
            self.value[cond as usize] |= 1u64 << bit;
        }
    }
}

// anim_iw4::ANIM_COND_*
const C_PLAYERANIMTYPE: u8 = 0;
const C_WEAPONCLASS: u8 = 1;
const C_MOUNTED: u8 = 2;
const C_MOVETYPE: u8 = 3;
const C_FIRING: u8 = 5;
const C_WEAPON_POSITION: u8 = 6;
const C_STRAFING: u8 = 7;
const C_PERK: u8 = 8;
const C_DAMAGETYPE: u8 = 9;
const C_HITLOCATION: u8 = 10;
const C_HITDIRECTION: u8 = 11;
const C_AKIMBO: u8 = 12;
const C_RIOTSHIELDNEXT: u8 = 14;
const C_PLAYERANIMTYPEPRIMARY: u8 = 15;
const C_FASTMANTLE: u8 = 16;

fn item_matches(item: &AnimItem, c: &Conds) -> bool {
    for cond in &item.conditions {
        let idx = cond.index as usize;
        if idx >= 18 {
            return false;
        }
        if cond.bitflags {
            if cond.bits & c.value[idx] == 0 {
                return false;
            }
        } else {
            if c.written & (1 << cond.index) == 0 || c.value[idx] as i32 != cond.value {
                return false;
            }
        }
    }
    true
}

fn matching_item<'a>(items: &'a [AnimItem], c: &Conds) -> Option<&'a AnimItem> {
    items.iter().find(|i| !i.skip && item_matches(i, c))
}

/// One leaf of MW2's player animtree: a clip with its own weight ramp and time.
struct Layer {
    id: u16,
    torso: bool,
    clip: Arc<AnimClip>,
    tracks: Vec<Option<usize>>,
    /// Normalised clip time 0..1.
    time: f32,
    cycle: i16,
    rate: f32,
    weight: f32,
    goal_weight: f32,
    goal_time: f32,
    move_speed: f32,
}

#[derive(Clone, Copy, Debug, Default)]
struct Branch {
    weight: f32,
    goal_weight: f32,
    goal_time: f32,
}

/// `Mw2CharacterInput::hit` into playeranim's damage conditions (bullet / torso / front when unset).
fn set_hit(c: &mut Conds, hit: u32) {
    let (t, l, d) = if hit & 0x8000_0000 != 0 { (hit & 0xff, (hit >> 8) & 0xff, (hit >> 16) & 0xff) } else { (0, 0, 0) };
    c.set_value(C_DAMAGETYPE, t.min(2));
    c.set_value(C_HITLOCATION, l.min(3));
    c.set_value(C_HITDIRECTION, d.min(3));
}

/// Weapon facts the animator needs.
#[derive(Clone, Copy, Debug, Default)]
struct WeaponAnim {
    id: u32,
    anim_type: i32,
    class: i32,
    known: bool,
    akimbo: bool,
}

struct Animator {
    store: Arc<Store>,
    names: Vec<String>,
    ids: HashMap<String, u16>,
    layers: Vec<Layer>,
    legs_branch: Branch,
    torso_branch: Branch,
    /// `ps.legs_anim` / `ps.torso_anim`: index | restart toggle (0x200).
    legs_raw: u16,
    torso_raw: u16,
    legs_timer: i32,
    torso_timer: i32,
    /// What the layers were last set up for (index only).
    bound_legs: u16,
    bound_torso: u16,
    bound_legs_raw: u16,
    bound_torso_raw: u16,
    legs_moving: bool,
    torso_moving: bool,
    movetype: u8,
    stance: u8,
    was_air: bool,
    was_dead: bool,
    dead: bool,
    fire_hold: f32,
    mantle_left: f32,
    /// Of `mantle_left`, the climb part; then the over.
    mantle_up_left: f32,
    mantle_up_mt: u8,
    mantle_over_mt: u8,
    weapon: WeaponAnim,
    /// The gun in hand (`Mw2CharacterInput::primary`).
    primary: WeaponAnim,
    client: u32,
    seed: u32,
    torso_swing: entity_iw4::SwingState,
    legs_swing: entity_iw4::SwingState,
    legs_offset_deg: f32,
    last_event_log: Vec<String>,
    /// The reload the torso is playing, in the weapon's real time (ms; 0: none): its anim
    /// stretches to it (the RPD's ran ~half its reload: the soldier looked done while the gun
    /// couldn't fire yet, playtest 10-04-26).
    reload_ms: i32,
    /// The next command's duration in place of its clip length (the reload's).
    duration_override: Option<i32>,
}

fn weapon_anim(id: u32) -> WeaponAnim {
    match crate::weapons::row(id) {
        Some(r) => WeaponAnim { id, anim_type: r.facts.player_anim_type, class: r.facts.weap_class, known: true, akimbo: r.akimbo },
        None => WeaponAnim { id, anim_type: 0, class: 0, known: false, akimbo: false },
    }
}

impl Animator {
    fn new(store: Arc<Store>) -> Self {
        let client = NEXT_CLIENT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        Animator {
            store,
            names: Vec::new(),
            ids: HashMap::new(),
            layers: Vec::new(),
            legs_branch: Branch { weight: 1.0, goal_weight: 1.0, goal_time: 0.0 },
            torso_branch: Branch { weight: 0.0, goal_weight: 0.0, goal_time: 0.0 },
            legs_raw: 0,
            torso_raw: 0,
            legs_timer: 0,
            torso_timer: 0,
            bound_legs: 0,
            bound_torso: 0,
            bound_legs_raw: 0,
            bound_torso_raw: 0,
            legs_moving: false,
            torso_moving: false,
            movetype: MT_IDLE,
            stance: 0,
            was_air: false,
            was_dead: false,
            dead: false,
            fire_hold: 0.0,
            mantle_left: 0.0,
            mantle_up_left: 0.0,
            mantle_up_mt: MT_MANTLE_OVER_MID,
            mantle_over_mt: MT_MANTLE_OVER_MID,
            weapon: WeaponAnim::default(),
            primary: WeaponAnim::default(),
            client,
            seed: 0x1234_5678 ^ client.wrapping_mul(0x9e37_79b9),
            torso_swing: entity_iw4::SwingState::new(0.0),
            legs_swing: entity_iw4::SwingState::new(0.0),
            legs_offset_deg: 0.0,
            last_event_log: Vec::new(),
            reload_ms: 0,
            duration_override: None,
        }
    }

    fn intern(&mut self, name: &str) -> u16 {
        if let Some(&id) = self.ids.get(name) {
            return id;
        }
        self.names.push(name.to_owned());
        let id = self.names.len() as u16;
        self.ids.insert(name.to_owned(), id);
        id
    }

    fn name(&self, id: u16) -> &str {
        if id == 0 { "-" } else { self.names.get(id as usize - 1).map_or("?", String::as_str) }
    }

    pub fn legs_name(&self) -> &str {
        self.name(self.legs_raw & PLAYER_ANIM_INDEX_MASK)
    }

    pub fn torso_name(&self) -> &str {
        self.name(self.torso_raw & PLAYER_ANIM_INDEX_MASK)
    }

    // ---- MW2's movetype choice ----

    fn movetype_for(&self, input: &Mw2CharacterInput) -> Option<u8> {
        let stance = match input.stance {
            1 => 2u8, // crouch
            2 => 1u8, // prone
            3 => 3u8, // last stand (PM_MOVE_ANIM_TABLE's 4th stance: crawllaststand / bk)
            _ => 0u8, // stand
        };
        let speed = input.move_fwd.hypot(input.move_right);
        if self.mantle_left > 0.0 {
            return Some(if self.mantle_up_left > 0.0 { self.mantle_up_mt } else { self.mantle_over_mt });
        }
        if input.in_air != 0 {
            return None;
        }
        let idle = match stance {
            1 => MT_IDLEPRONE,
            2 => MT_IDLECR,
            3 => MT_IDLELASTSTAND,
            _ => MT_IDLE,
        };
        if speed <= PLAYER_MOVE_THRESHOLD {
            return Some(idle);
        }
        let walking = input.ads_frac >= 0.5;
        let backward = input.move_fwd < 0.0;
        if stance == 0 && !backward && input.sprinting != 0 {
            return Some(MT_SPRINT);
        }
        let index = 2 * usize::from(walking) + 4 * usize::from(stance) + 16 * usize::from(backward);
        Some(PM_MOVE_ANIM_TABLE[index])
    }

    /// IW4L `sim::player_anim_script::anim_strafing`: 0 not, 1 left, 2 right.
    fn strafing(&self, input: &Mw2CharacterInput) -> u32 {
        let (f, r) = (input.move_fwd, input.move_right);
        let len = f.hypot(r);
        if len == 0.0 || len <= PLAYER_MOVE_THRESHOLD || f.abs() > len * 0.5 {
            0
        } else if r > 0.0 {
            2
        } else {
            1
        }
    }

    fn conditions(&self, input: &Mw2CharacterInput, movetype: u8) -> Conds {
        let mut c = Conds::default();
        c.set_value(C_MOUNTED, 0);
        c.set_value(C_FIRING, u32::from(self.fire_hold > 0.0));
        c.set_value(C_FASTMANTLE, 0);
        if self.weapon.known || self.weapon.id == 0 {
            c.set_bit(C_PLAYERANIMTYPE, self.weapon.anim_type.clamp(0, 63) as u8);
            let primary = if self.primary.id != 0 && self.primary.known { self.primary } else { self.weapon };
            c.set_bit(C_PLAYERANIMTYPEPRIMARY, primary.anim_type.clamp(0, 63) as u8);
            if self.weapon.id != 0 {
                c.set_bit(C_WEAPONCLASS, self.weapon.class.clamp(0, 63) as u8);
            }
        }
        c.set_value(C_AKIMBO, u32::from(self.weapon.akimbo));
        c.set_bit(C_MOVETYPE, movetype);
        c.set_value(C_STRAFING, self.strafing(input));
        c.set_value(C_PERK, 0);
        c.set_value(C_RIOTSHIELDNEXT, 0);
        c.set_value(C_WEAPON_POSITION, u32::from(input.ads_frac >= 0.5));
        c
    }

    fn command_duration_ms(&self, cmd: &AnimCommand) -> i32 {
        if let Some(ms) = cmd.duration_ms {
            return if ms <= 0 { 500 } else { ms };
        }
        match self.store.clip(&cmd.anim) {
            Some(c) if c.framerate > 0.0 => {
                let ms = (c.numframes as f32 / c.framerate * 1000.0) as i32;
                if ms <= 0 { 500 } else { ms }
            }
            _ => 500,
        }
    }

    // ---- sim::player_anim_script::play_anim / execute_command / apply / apply_event ----

    fn play_anim(&mut self, anim: u16, legs: bool, duration: i32, set_timer: bool, is_continue: bool, force: bool) {
        let (timer, raw) = if legs { (self.legs_timer, self.legs_raw) } else { (self.torso_timer, self.torso_raw) };
        if timer >= 50 && !force {
            return;
        }
        let same = (raw & PLAYER_ANIM_INDEX_MASK) == (anim & PLAYER_ANIM_INDEX_MASK);
        if is_continue && same {
            if set_timer {
                if legs {
                    self.legs_timer = duration;
                } else {
                    self.torso_timer = duration;
                }
            }
            return;
        }
        let written = (anim & PLAYER_ANIM_INDEX_MASK) | ((raw & PLAYER_ANIM_RESTART_TOGGLE) ^ PLAYER_ANIM_RESTART_TOGGLE);
        if legs {
            self.legs_raw = written;
            if set_timer {
                self.legs_timer = duration;
            }
        } else {
            self.torso_raw = written;
            if set_timer {
                self.torso_timer = duration;
            }
        }
    }

    fn execute_command(&mut self, cmd: &AnimCommand, set_timer: bool, is_continue: bool, force: bool) {
        let duration = self.duration_override.unwrap_or_else(|| self.command_duration_ms(cmd)).saturating_add(50);
        let id = self.intern(&cmd.anim);
        if cmd.body_part == 1 || cmd.body_part == 3 {
            self.play_anim(id, true, duration, set_timer, is_continue, force);
        }
        if cmd.body_part == 2 {
            self.play_anim(id, false, duration, set_timer, is_continue, force);
        } else if cmd.body_part == 3 {
            self.play_anim(0, false, duration, set_timer, is_continue, force);
        }
    }

    fn apply_movement(&mut self, movetype: u8, conds: &Conds) {
        let store = self.store.clone();
        let pick = matching_item(store.script.slot(0, movetype), conds)
            .or_else(|| if movetype != MT_IDLE { matching_item(store.script.slot(0, MT_IDLE), conds) } else { None });
        let Some(item) = pick else { return };
        if item.commands.is_empty() {
            return;
        }
        let cmd = item.commands[self.client as usize % item.commands.len()].clone();
        self.execute_command(&cmd, false, true, false);
    }

    fn apply_event(&mut self, event: u8, conds: &Conds, force: bool) -> bool {
        let store = self.store.clone();
        let Some(item) = matching_item(store.script.event(event), conds) else { return false };
        if item.commands.is_empty() {
            return false;
        }
        let pick = random(&mut self.seed) as usize % item.commands.len();
        let cmd = item.commands[pick].clone();
        self.last_event_log.push(format!("event {event}: legs/torso <- {} ({})", cmd.anim, item.raw));
        self.execute_command(&cmd, true, false, force);
        true
    }

    // ---- sim -> tree: IW4L xmodel_runtime::apply_player_anim_goals ----

    fn layer_index(&self, id: u16) -> Option<usize> {
        self.layers.iter().position(|l| l.id == id)
    }

    fn ensure_layer(&mut self, id: u16, torso: bool, mask: Option<&std::collections::HashSet<String>>, dobj: &DObj) -> Option<usize> {
        if let Some(i) = self.layer_index(id) {
            // A legs layer re-masks against the current torso overlay.
            if !torso {
                let clip = self.layers[i].clip.clone();
                self.layers[i].tracks = masked_tracks(dobj, &clip, mask);
            }
            return Some(i);
        }
        let name = self.name(id).to_owned();
        let clip = self.store.clip(&name)?;
        let tracks = if torso { dobj.tracks_for(&clip) } else { masked_tracks(dobj, &clip, mask) };
        let (_, stationary) = self.store.props(&name);
        let move_speed = if stationary { 0.0 } else { clip.move_speed() };
        self.layers.push(Layer {
            id,
            torso,
            clip,
            tracks,
            time: 0.0,
            cycle: 0,
            rate: 1.0,
            weight: 0.0,
            goal_weight: 0.0,
            goal_time: 0.0,
            move_speed,
        });
        Some(self.layers.len() - 1)
    }

    fn sync_tree(&mut self, dobj: &DObj) {
        let legs_idx = self.legs_raw & PLAYER_ANIM_INDEX_MASK;
        let torso_idx = self.torso_raw & PLAYER_ANIM_INDEX_MASK;
        let legs_restart_changed = (self.legs_raw & PLAYER_ANIM_RESTART_TOGGLE) != (self.bound_legs_raw & PLAYER_ANIM_RESTART_TOGGLE);
        let torso_restart_changed = (self.torso_raw & PLAYER_ANIM_RESTART_TOGGLE) != (self.bound_torso_raw & PLAYER_ANIM_RESTART_TOGGLE);
        if legs_idx == self.bound_legs && torso_idx == self.bound_torso && !legs_restart_changed && !torso_restart_changed {
            return;
        }
        if legs_idx == 0 {
            return;
        }
        let (old_legs, old_torso) = (self.bound_legs, self.bound_torso);
        let (old_legs_moving, old_torso_moving) = (self.legs_moving, self.torso_moving);

        // IW4L overlay_legs_clip: the torso clip's tracks replace the legs clip's.
        let torso_names: Option<std::collections::HashSet<String>> = (torso_idx != 0)
            .then(|| self.store.clip(&self.name(torso_idx).to_owned()))
            .flatten()
            .map(|c| c.tracks.iter().map(|t| t.name.clone()).collect());
        let Some(li) = self.ensure_layer(legs_idx, false, torso_names.as_ref(), dobj) else { return };
        let ti = if torso_idx != 0 { self.ensure_layer(torso_idx, true, None, dobj) } else { None };

        let legs_props = self.store.props(&self.name(legs_idx).to_owned());
        let torso_props = self.store.props(&self.name(torso_idx).to_owned());
        self.legs_moving = self.layers[li].move_speed > 0.0;
        self.torso_moving = ti.is_some_and(|i| self.layers[i].move_speed > 0.0);

        let legs_time = goal_time_from_blend_ms(client_anim_blend_ms(
            legs_idx,
            legs_props.0,
            old_legs != 0,
            false,
            old_legs_moving,
            self.legs_moving,
        ));
        let torso_time = goal_time_from_blend_ms(client_anim_blend_ms(
            torso_idx,
            torso_props.0,
            old_torso != 0,
            true,
            old_torso_moving,
            self.torso_moving,
        ));

        // Locomotion phase carry-over between looping move anims.
        let phase = (old_legs != legs_idx
            && old_legs_moving
            && self.legs_moving
            && self.layers[li].clip.looping)
            .then(|| self.layer_index(old_legs).filter(|&o| self.layers[o].clip.looping).map(|o| self.layers[o].time))
            .flatten();

        let set_goal = |l: &mut Layer, goal: f32, goal_time: f32| {
            l.goal_weight = goal;
            l.goal_time = goal_time;
            if goal_time <= 0.0 {
                l.weight = goal;
            }
        };
        if old_legs != 0 && old_legs != legs_idx {
            if let Some(o) = self.layer_index(old_legs) {
                set_goal(&mut self.layers[o], 0.0, legs_time);
            }
        }
        if old_torso != 0 && old_torso != torso_idx {
            if let Some(o) = self.layer_index(old_torso) {
                set_goal(&mut self.layers[o], 0.0, torso_time);
            }
        }
        let complete = |l: &mut Layer, goal_time: f32| {
            l.time = 0.0;
            l.cycle = 0;
            l.goal_weight = 1.0;
            l.goal_time = goal_time;
            if goal_time <= 0.0 {
                l.weight = 1.0;
            }
        };
        let li = self.layer_index(legs_idx).unwrap_or(li);
        if old_legs == legs_idx && !legs_restart_changed {
            set_goal(&mut self.layers[li], 1.0, legs_time);
        } else {
            complete(&mut self.layers[li], legs_time);
        }
        if let Some(t) = ti.and_then(|_| self.layer_index(torso_idx)) {
            if old_torso == torso_idx && !torso_restart_changed {
                set_goal(&mut self.layers[t], 1.0, torso_time);
            } else {
                complete(&mut self.layers[t], torso_time);
            }
        }
        if let Some(p) = phase {
            self.layers[li].time = p;
        }
        let (lg, tg) = if torso_idx != 0 { (XANIM_LEGS_PARENT_WEIGHT_WHEN_TORSO, 1.0) } else { (1.0, 0.0) };
        for (b, goal) in [(&mut self.legs_branch, lg), (&mut self.torso_branch, tg)] {
            b.goal_weight = goal;
            b.goal_time = torso_time;
            if torso_time <= 0.0 {
                b.weight = goal;
            }
        }
        self.bound_legs = legs_idx;
        self.bound_torso = torso_idx;
        self.bound_legs_raw = self.legs_raw;
        self.bound_torso_raw = self.torso_raw;
    }

    /// IW4L `apply_player_anim_rates`: the current legs / torso leaf plays at real ground speed
    /// over its clip's own move speed.
    fn update_rates(&mut self, speed: f32) {
        let (li, ti) = (self.bound_legs, self.bound_torso);
        for id in [li, ti] {
            if id == 0 {
                continue;
            }
            if let Some(i) = self.layer_index(id) {
                let ms = self.layers[i].move_speed;
                // dist over exactly one second, so the speed is the distance.
                let rate = client_anim_playback_rate([speed, 0.0, 0.0], [0.0; 3], 2000, 1000, ms, false);
                if let Some(r) = rate {
                    self.layers[i].rate = r;
                }
            }
        }
        // A reload anim plays over the weapon's real reload time.
        if ti != 0 && self.reload_ms > 0 && self.name(ti).contains("reload") {
            if let Some(i) = self.layer_index(ti) {
                let c = &self.layers[i].clip;
                if c.framerate > 0.0 {
                    let clip_ms = c.numframes as f32 / c.framerate * 1000.0;
                    self.layers[i].rate = (clip_ms / self.reload_ms as f32).clamp(0.2, 5.0);
                }
            }
        }
    }

    fn advance_tree(&mut self, dt: f32) {
        let (lw, tw) = (self.legs_branch.weight != 0.0, self.torso_branch.weight != 0.0);
        for (b, _) in [(&mut self.legs_branch, ()), (&mut self.torso_branch, ())] {
            let (w, g) = advance_goal_weight(b.weight, b.goal_weight, b.goal_time, dt, true);
            b.weight = w;
            b.goal_time = g;
        }
        for l in &mut self.layers {
            let parent = if l.torso { tw } else { lw };
            let (w, g) = advance_goal_weight(l.weight, l.goal_weight, l.goal_time, dt, parent);
            l.weight = w;
            l.goal_time = g;
        }
        for l in &mut self.layers {
            if l.weight == 0.0 {
                continue;
            }
            let (t, c) = advance_leaf_time(l.time, l.cycle, l.rate, l.clip.frequency(), dt, l.clip.looping);
            l.time = t;
            l.cycle = c;
        }
        let (bl, bt) = (self.bound_legs, self.bound_torso);
        self.layers.retain(|l| l.id == bl || l.id == bt || l.weight > 0.0 || l.goal_weight > 0.0);
    }

    /// One animation frame. Updates the animator; the pose is computed separately.
    fn update(&mut self, dobj: &DObj, input: &Mw2CharacterInput) {
        self.last_event_log.clear();
        let dt = if input.dt.is_finite() { input.dt.clamp(0.0, 0.25) } else { 0.0 };
        let ms = (dt * 1000.0).round() as i32;
        self.legs_timer = (self.legs_timer - ms).max(0);
        self.torso_timer = (self.torso_timer - ms).max(0);
        self.fire_hold = (self.fire_hold - dt).max(0.0);
        self.mantle_left = (self.mantle_left - dt).max(0.0);
        self.mantle_up_left = (self.mantle_up_left - dt).max(0.0);

        // A weapon switch (the gun in hand changed, not an offhand coming up): playeranim's
        // DROPWEAPON on the old weapon, RIOTSHIELDNEXT when the riot shield is what comes next.
        let primary_in = if input.primary != 0 { input.primary } else { input.weapon };
        if primary_in != self.primary.id {
            let next = weapon_anim(primary_in);
            if self.primary.id != 0 && primary_in != 0 && input.dead == 0 && !self.was_dead {
                let mut c = self.conditions(input, self.movetype);
                c.set_value(C_RIOTSHIELDNEXT, u32::from(next.anim_type == 15));
                self.apply_event(ET_DROPWEAPON, &c, false);
            }
            self.primary = next;
        }
        if input.weapon != self.weapon.id {
            self.weapon = weapon_anim(input.weapon);
        }
        let ev = input.event;
        if ev & EV_MANTLE != 0 {
            let t = ((ev >> 8) & 0xf) as usize;
            if (1..=7).contains(&t) {
                // MW2's own climb for the ledge height, then its over, each for its clip's length.
                let i = t - 1;
                let len = |name: &str| self.store.clip(name).filter(|c| c.framerate > 0.0).map_or(0.45, |c| c.numframes as f32 / c.framerate);
                let over = MANTLE_OVER_FOR[i];
                self.mantle_up_mt = MT_MANTLE_UP_57 + i as u8;
                self.mantle_over_mt = over;
                self.mantle_up_left = len(MANTLE_UP_ANIMS[i]);
                self.mantle_left = self.mantle_up_left + len(MANTLE_OVER_ANIMS[(over - 29) as usize]);
            } else {
                self.mantle_up_mt = MT_MANTLE_OVER_MID;
                self.mantle_over_mt = MT_MANTLE_OVER_MID;
                self.mantle_up_left = 0.0;
                self.mantle_left = 0.9;
            }
        }
        if ev & (EV_FIRE | EV_THROW) != 0 {
            self.fire_hold = 0.15;
        }

        let dead = input.dead != 0;
        let moving_type = self.movetype_for(input);
        let movetype = moving_type.unwrap_or(self.movetype);
        let conds = self.conditions(input, movetype);

        if dead {
            if !self.was_dead {
                // DEATH ignores timers; the previous movetype picks the animation.
                let mut c = conds;
                set_hit(&mut c, input.hit);
                self.apply_event(ET_DEATH, &c, true);
            }
        } else {
            // Stance changes play MW2's transition events first.
            if input.stance != self.stance {
                let ev = match (self.stance, input.stance) {
                    (0, 1) => ET_STAND_TO_CROUCH,
                    (1, 0) => ET_CROUCH_TO_STAND,
                    (1, 2) => ET_CROUCH_TO_PRONE,
                    (2, 1) => ET_PRONE_TO_CROUCH,
                    (0, 2) => ET_STAND_TO_PRONE,
                    _ => ET_PRONE_TO_STAND,
                };
                self.apply_event(ev, &conds, false);
            }
            if ev & EV_JUMP != 0 {
                self.apply_event(ET_JUMP, &conds, false);
            }
            if ev & EV_LAND != 0 {
                self.apply_event(ET_LAND, &conds, false);
            }
            if ev & EV_PAIN != 0 {
                let mut c = conds;
                set_hit(&mut c, input.hit);
                self.apply_event(ET_PAIN, &c, false);
            }
            if ev & EV_RELOAD != 0 {
                self.reload_ms = crate::weapons::row(self.weapon.id).map_or(0, |r| {
                    let ms = if ev & EV_RELOAD_EMPTY != 0 && r.timers.reload_empty_ms > 0 { r.timers.reload_empty_ms } else { r.timers.reload_ms };
                    if ev & EV_RELOAD_FAST != 0 { (ms as f32 * weapon_iw4::PERK_WEAP_RELOAD_MULTIPLIER_DEFAULT) as i32 } else { ms }
                });
                self.duration_override = (self.reload_ms > 0).then_some(self.reload_ms);
                self.last_event_log.push(format!("reload over {} ms (empty {}, fast {})", self.reload_ms, ev & EV_RELOAD_EMPTY != 0, ev & EV_RELOAD_FAST != 0));
                self.apply_event(ET_RELOAD, &conds, false);
                self.duration_override = None;
            }
            if ev & EV_MELEE != 0 {
                // A pistol's melee is the knife (playeranim's knife_melee / knife_melee_charge use the
                // pt_melee_pistol anims); everything else swings the gun (meleeattack).
                let et = if self.weapon.anim_type == 2 {
                    if ev & EV_MELEE_CHARGE != 0 { ET_KNIFE_MELEE_CHARGE } else { ET_KNIFE_MELEE }
                } else {
                    ET_MELEEATTACK
                };
                self.apply_event(et, &conds, false);
            }
            if ev & (EV_FIRE | EV_THROW) != 0 {
                self.apply_event(ET_FIREWEAPON, &conds, false);
            }
            // pmove runs the movement script every frame; airborne frames keep the last anim.
            if moving_type.is_some() {
                self.movetype = movetype;
                self.apply_movement(movetype, &conds);
            }
        }
        self.stance = input.stance;
        self.was_air = input.in_air != 0;
        self.was_dead = dead;
        self.dead = dead;

        self.sync_tree(dobj);
        let speed = input.move_fwd.hypot(input.move_right);
        self.update_rates(speed);
        self.advance_tree(dt);

        // Legs follow the movement direction (IW4L entity_iw4::player_angles; the host faces the
        // model along the view yaw, so the base yaw is 0 and the torso destination is 0).
        // Pure strafes have their own anims (body square to the view), so only forward / backward
        // diagonals turn the legs; backward movement is mirrored so the legs never exceed 90 degrees.
        let strafing = self.strafing(input);
        let move_yaw = if speed > PLAYER_MOVE_THRESHOLD && !dead && strafing == 0 {
            let mut yaw = (-input.move_right).atan2(input.move_fwd).to_degrees();
            if yaw > 90.0 {
                yaw -= 180.0;
            } else if yaw < -90.0 {
                yaw += 180.0;
            }
            yaw
        } else {
            0.0
        };
        let out = entity_iw4::player_angles(
            entity_iw4::PlayerAngleInput {
                base_yaw: 0.0,
                movement_yaw: move_yaw,
                dvars: entity_iw4::PlayerAngleDvars::default(),
                frametime_ms: dt * 1000.0,
            },
            &mut self.torso_swing,
            &mut self.legs_swing,
        );
        self.legs_offset_deg = if dead { 0.0 } else { entity_iw4::legs_offset_deg(out.torso.angle, out.legs.angle) };
    }

    /// Bone world matrices (IW4 model space, inches) for the current animation state.
    fn world(&self, dobj: &DObj, aim_pitch: f32, stance: u8) -> Vec<Mat4> {
        let (lw, tw) = (self.legs_branch.weight, self.torso_branch.weight);
        let mut anims: Vec<AnimInstance<'_>> = Vec::with_capacity(self.layers.len());
        for l in &self.layers {
            let w = l.weight * if l.torso { tw } else { lw };
            if w <= 0.0 {
                continue;
            }
            anims.push(AnimInstance { clip: &l.clip, tracks: &l.tracks, time: l.time * l.clip.duration(), weight: w, parts: None });
        }
        let dead = self.dead;
        let mut world = dobj.pose_with_controller(&anims, &dobj.all_parts(), Mat4::IDENTITY, |d, _, locals| {
            if !dead {
                apply_player_controller(
                    d,
                    locals,
                    PlayerControllerInput { view_pitch_deg: aim_pitch, prone: stance == 2, crouch: stance == 1, lean_frac: 0.0 },
                );
            }
        });
        if !dead {
            yaw_legs(dobj, &mut world, self.legs_offset_deg.to_radians());
        }
        world
    }
}

/// Turn the legs (pelvis + both hips' subtrees) about the vertical axis through the pelvis, so
/// they follow the movement direction while the upper body keeps facing the view. MW2 does this
/// with `pelvis` / `torso_stabilizer` counter-rotations in bone space; doing it on the posed
/// model-space matrices gives the same result without depending on each bone's bind axes.
fn yaw_legs(dobj: &DObj, world: &mut [Mat4], theta: f32) {
    if theta.abs() < 1e-5 {
        return;
    }
    let Some(pelvis) = dobj.find("pelvis") else { return };
    let pivot = world[pelvis].w_axis.truncate();
    let turn = Mat4::from_translation(pivot) * Mat4::from_rotation_z(theta) * Mat4::from_translation(-pivot);
    let mut leg = vec![false; dobj.bones.len()];
    for (i, b) in dobj.bones.iter().enumerate() {
        leg[i] = i == pelvis
            || b.name == "j_hip_le"
            || b.name == "j_hip_ri"
            || b.parent.is_some_and(|p| p != pelvis && leg[p]);
        if leg[i] {
            world[i] = turn * world[i];
        }
    }
}

fn masked_tracks(dobj: &DObj, clip: &AnimClip, mask: Option<&std::collections::HashSet<String>>) -> Vec<Option<usize>> {
    let mut tracks = dobj.tracks_for(clip);
    if let Some(mask) = mask {
        for (t, track) in tracks.iter_mut().zip(&clip.tracks) {
            if mask.contains(&track.name) {
                *t = None;
            }
        }
    }
    tracks
}

// ---------------------------------------------------------------------------------------------
// The character
// ---------------------------------------------------------------------------------------------

pub struct Character {
    pub spec: data::CharacterSpec,
    /// Faction whose models were actually used (differs from the request when a fallback applied).
    pub resolved_faction: String,
    pub body_name: String,
    pub head_name: Option<String>,
    dobj: DObj,
    anim: Animator,
    // Unity-space mesh
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub bone_index: Vec<[i32; 4]>,
    pub bone_weight: Vec<[f32; 4]>,
    pub indices: Vec<u32>,
    pub surfaces: Vec<Surface>,
    pub bindposes: Vec<[f32; 16]>,
    pub bone_names: Vec<String>,
    pub bone_parents: Vec<i32>,
    /// Last pose written (7 floats per bone), for tag lookups.
    pub last_pose: Vec<f32>,
    weapon_bone: Option<usize>,
}

/// Fallbacks when a faction's models are not in this install (the MP `socom_141` bodies
/// `mp_body_seal_soccom_*` exist in no zone; the shipped TF141 MP skins are the terrain variants).
fn faction_fallbacks(faction: &str) -> &'static [&'static str] {
    match faction {
        "socom_141" => &["socom_141_desert", "socom_141_forest", "socom_141_arctic"],
        _ => &[],
    }
}

impl Character {
    pub fn build(faction: &str) -> Result<Self, String> {
        Self::build_with(faction, "assault", 0, 0)
    }

    /// `class`: assault / smg / lmg / shotgun / sniper / riot. `variant` picks the body variant,
    /// `head` the head from the faction's pool (both wrap).
    pub fn build_with(faction: &str, class: &str, variant: usize, head: usize) -> Result<Self, String> {
        let store = ensure_store(None)?;
        let mut tried = Vec::new();
        let mut candidates = vec![faction.to_owned()];
        candidates.extend(faction_fallbacks(faction).iter().map(|s| (*s).to_owned()));
        for f in &candidates {
            let lookup = |n: &str| store.common.raw_file(n);
            let Some(spec) = data::resolve_spec(&lookup, f, class, variant) else {
                tried.push(format!("{f}: no mptype"));
                continue;
            };
            let hands: Vec<String> = spec.viewhands.iter().cloned().collect();
            let Some((body, head_rig)) = store.models_for(&spec.body, &spec.heads, head, &hands) else {
                tried.push(format!("{f}: body {} not found in any map zone", spec.body));
                continue;
            };
            return Self::assemble(store.clone(), spec, f.clone(), body, head_rig);
        }
        Err(format!("no character for faction {faction}: {}", tried.join("; ")))
    }

    fn assemble(store: Arc<Store>, spec: data::CharacterSpec, resolved: String, body: Arc<RigModel>, head: Option<Arc<RigModel>>) -> Result<Self, String> {
        let mut specs: Vec<(&xmodel_runtime::ModelPoseSrc, Option<Attach>)> = vec![(&body.pose, None)];
        let head_tag = tp_head_attach_tag(&body.pose.bone_names);
        let head = head.filter(|_| head_tag.is_some());
        if let (Some(h), Some(tag)) = (&head, head_tag) {
            specs.push((&h.pose, Some(Attach { parent_model: 0, tag: tag.into() })));
        }
        let dobj = DObj::build(&specs).map_err(|e| e.to_string())?;
        let s = INCHES_TO_METRES;
        let mut ch = Character {
            resolved_faction: resolved,
            body_name: body.name.clone(),
            head_name: head.as_ref().map(|h| h.name.clone()),
            spec,
            anim: Animator::new(store.clone()),
            positions: Vec::new(),
            normals: Vec::new(),
            uvs: Vec::new(),
            bone_index: Vec::new(),
            bone_weight: Vec::new(),
            indices: Vec::new(),
            surfaces: Vec::new(),
            bindposes: Vec::new(),
            bone_names: dobj.bones.iter().map(|b| b.name.clone()).collect(),
            bone_parents: dobj.bones.iter().map(|b| b.parent.map_or(-1, |p| p as i32)).collect(),
            last_pose: Vec::new(),
            weapon_bone: tp_weapon_attach_tag(&body.pose.bone_names).and_then(|t| dobj.find(t)),
            dobj,
        };
        let mut models: Vec<&Arc<RigModel>> = vec![&body];
        if let Some(h) = &head {
            models.push(h);
        }
        let mesh_bits = store.meshes.lock().map_err(|_| "mesh table poisoned".to_string())?;
        for (slot, model) in models.into_iter().enumerate() {
            let base_bone = ch.dobj.models[slot].base as i32;
            let base_vertex = ch.positions.len() as u32;
            for i in 0..model.positions.len() {
                ch.positions.push(to_unity(model.positions[i], s));
                ch.normals.push(to_unity(model.normals[i], 1.0));
                ch.uvs.push(model.uvs[i]);
                let sk = model.skin[i];
                ch.bone_index.push([0, 1, 2, 3].map(|k| base_bone + i32::from(sk.bones[k])));
                ch.bone_weight.push(sk.weights);
            }
            let bits = mesh_bits.get(&model.name);
            for (si, surf) in model.surfaces.iter().enumerate() {
                let tris = &model.indices[surf.index_start as usize..(surf.index_start + surf.index_count) as usize];
                let start = ch.indices.len() as u32;
                for t in tris.chunks_exact(3) {
                    ch.indices.extend_from_slice(&[base_vertex + t[0], base_vertex + t[1], base_vertex + t[2]]);
                }
                ch.surfaces.push(Surface {
                    index_start: start,
                    index_count: ch.indices.len() as u32 - start,
                    color_map: surf.color_map.clone().or_else(|| surf.material.as_deref().and_then(crate::models::color_map_of_material)),
                    material: surf.material.clone(),
                    state_bits: bits.and_then(|m| m.surfaces.get(si)).and_then(|x| x.state_bits),
                });
            }
        }
        drop(mesh_bits);
        let m = axis_matrix(s);
        let mi = m.inverse();
        ch.bindposes = ch.dobj.bones.iter().map(|b| (m * b.bind_world.inverse() * mi).to_cols_array()).collect();
        // Start in the idle pose.
        let mut rest = vec![0.0; ch.dobj.bones.len() * 7];
        ch.pose_into(0.0, 0, &mut rest);
        ch.last_pose = rest;
        Ok(ch)
    }

    pub fn bone_count(&self) -> usize {
        self.dobj.bones.len()
    }

    pub fn find_bone(&self, name: &str) -> Option<usize> {
        self.dobj.find(name)
    }

    fn pose_into(&self, aim_pitch: f32, stance: u8, out: &mut [f32]) {
        let world = self.anim.world(&self.dobj, aim_pitch, stance);
        for (i, w) in world.iter().enumerate() {
            let (_, r, t) = w.to_scale_rotation_translation();
            let p = to_unity(t.to_array(), INCHES_TO_METRES);
            let q = quat_to_unity(r.normalize());
            if let Some(slot) = out.get_mut(i * 7..i * 7 + 7) {
                slot.copy_from_slice(&[p[0], p[1], p[2], q[0], q[1], q[2], q[3]]);
            }
        }
    }

    /// Advance one frame and write the pose (7 floats per bone: Unity-space position xyz in
    /// metres, rotation xyzw, MODEL space relative to the character origin).
    pub fn step(&mut self, input: &Mw2CharacterInput, out: &mut [f32]) {
        self.anim.update(&self.dobj, input);
        let pitch = if input.aim_pitch.is_finite() { input.aim_pitch } else { 0.0 };
        self.pose_into(pitch, input.stance, out);
        self.last_pose.clear();
        self.last_pose.extend_from_slice(out);
    }

    /// A bone's pose from the last `step` (rest pose before the first).
    pub fn tag(&self, name: &str) -> Option<[f32; 7]> {
        let bone = self.dobj.find(name)?;
        self.last_pose.get(bone * 7..bone * 7 + 7).map(|p| [p[0], p[1], p[2], p[3], p[4], p[5], p[6]])
    }

    pub fn weapon_tag(&self) -> Option<[f32; 7]> {
        let bone = self.weapon_bone?;
        self.last_pose.get(bone * 7..bone * 7 + 7).map(|p| [p[0], p[1], p[2], p[3], p[4], p[5], p[6]])
    }

    /// (legs anim, torso anim, legs timer ms, torso timer ms, movetype) for logs and tests.
    pub fn debug_state(&self) -> String {
        format!(
            "legs={} torso={} legs_timer={}ms torso_timer={}ms movetype={} layers={} legs_off={:.1}deg",
            self.anim.legs_name(),
            self.anim.torso_name(),
            self.anim.legs_timer,
            self.anim.torso_timer,
            self.anim.movetype,
            self.anim.layers.len(),
            self.anim.legs_offset_deg
        )
    }

    pub fn events_log(&self) -> &[String] {
        &self.anim.last_event_log
    }

    /// CPU-skin the mesh with a pose from `step` (for offline previews): Unity-space positions.
    pub fn skin_cpu(&self, pose: &[f32]) -> Vec<[f32; 3]> {
        let mats: Vec<Mat4> = (0..self.bone_count())
            .map(|i| {
                let o = i * 7;
                let bone = Mat4::from_rotation_translation(
                    Quat::from_xyzw(pose[o + 3], pose[o + 4], pose[o + 5], pose[o + 6]),
                    Vec3::new(pose[o], pose[o + 1], pose[o + 2]),
                );
                bone * Mat4::from_cols_array(&self.bindposes[i])
            })
            .collect();
        self.positions
            .iter()
            .zip(&self.bone_index)
            .zip(&self.bone_weight)
            .map(|((p, bi), bw)| {
                let v = Vec3::from_array(*p);
                let mut acc = Vec3::ZERO;
                for k in 0..4 {
                    if bw[k] > 0.0 {
                        acc += mats[bi[k] as usize].transform_point3(v) * bw[k];
                    }
                }
                acc.to_array()
            })
            .collect()
    }
}

// ---------------------------------------------------------------------------------------------
// World weapon (third-person gun)
// ---------------------------------------------------------------------------------------------

/// A weapon's world model as a static Unity-space mesh in the model's own space (parent it at
/// the character's `tag_weapon_right` pose; no extra offset, as IW4L attaches it).
pub struct WorldGun {
    pub model: String,
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub indices: Vec<u32>,
    pub surfaces: Vec<Surface>,
}

/// World model XModel name of a weapon (`ak47_mp` -> `weapon_ak47`).
/// World-gun calls take `weapon | camo << 16`: camo 0 = this player's own camo for the weapon
/// (`weapons::camo`), 255 = none, n = mp/camoTable.csv id n (a teammate's camo, from the network).
fn split_camo(weapon: u32) -> (u32, Option<String>) {
    let (index, code) = (weapon & 0xFFFF, (weapon >> 16) & 0xFF);
    let camo = match code {
        0 => crate::weapons::camo(index),
        255 => None,
        n => crate::menus::table_cell("mp/camoTable.csv", 0, &n.to_string(), 1).filter(|c| !c.is_empty() && c != "none"),
    };
    (index, camo)
}

pub fn weapon_world_model(weapon: u32) -> Option<String> {
    let (weapon, camo) = split_camo(weapon);
    let row = crate::weapons::row(weapon)?;
    let store = ensure_store(None).ok()?;
    let name = store.common.world_models.get(&row.name).cloned()?;
    // The camo's world model when the zone has one (weapon_ak47_tactical_woodland).
    if let Some(c) = camo {
        if let Some(camo_model) = crate::weapons::camo_model_names(&name, &c).into_iter().find(|m| models::index_of(m) != 0) {
            return Some(camo_model);
        }
    }
    Some(name)
}

/// Attachments the weapon variant does not carry are left out (`models::surface_visible`).
pub fn world_gun(weapon: u32) -> Option<WorldGun> {
    let row = crate::weapons::row(weapon & 0xFFFF)?;
    let name = weapon_world_model(weapon)?;
    let id = models::index_of(&name);
    let s = INCHES_TO_METRES;
    models::with(id, |m| {
        let mut g = WorldGun { model: name.clone(), positions: Vec::new(), normals: Vec::new(), uvs: Vec::new(), indices: Vec::new(), surfaces: Vec::new() };
        g.positions = m.positions.iter().map(|p| to_unity(*p, s)).collect();
        g.normals = m.normals.iter().map(|n| to_unity(*n, 1.0)).collect();
        g.uvs = m.uvs.clone();
        for surf in &m.surfaces {
            if !models::surface_visible(&row.name, surf.material.as_deref()) {
                continue;
            }
            let start = g.indices.len() as u32;
            g.indices.extend_from_slice(&m.indices[surf.index_start as usize..(surf.index_start + surf.index_count) as usize]);
            g.surfaces.push(Surface {
                index_start: start,
                index_count: g.indices.len() as u32 - start,
                color_map: surf.color_map.clone(),
                material: surf.material.clone(),
                state_bits: surf.state_bits,
            });
        }
        g
    })
}

// ---------------------------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------------------------

/// Per-frame input. 32 bytes.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2CharacterInput {
    /// Seconds since the last step.
    pub dt: f32,
    /// 0 stand, 1 crouch, 2 prone, 3 last stand (MW2's laststand idle / crawl anims).
    pub stance: u8,
    pub sprinting: u8,
    pub in_air: u8,
    pub dead: u8,
    /// Ground velocity in the character's own frame, units (inches) per second: forward and right.
    pub move_fwd: f32,
    pub move_right: f32,
    /// Degrees, positive = looking down (IW4 / Unity euler-X convention).
    pub aim_pitch: f32,
    /// 0..1, MW2 `fWeaponPosFrac`.
    pub ads_frac: f32,
    /// Weapon table index (`mw2_weapon_index`), 0 = none.
    pub weapon: u32,
    /// `EV_*` bit flags, one-shot.
    pub event: u32,
    /// The gun in hand while `weapon` is an offhand / laptop being used (MW2's
    /// playerAnimTypePrimary: the riot shield's own grenade / knife throws); 0 = `weapon`.
    /// A change of it is a weapon switch (playeranim's DROPWEAPON pull-out).
    pub primary: u32,
    /// The last hit, for PAIN and DEATH: bit 31 set, then damage type (bits 0-7: bullet,
    /// explosion_light, explosion), hit location (8-15: torso, head, neck, legs) and hit
    /// direction (16-23: front, left, right, back), as playeranim's condition values.
    pub hit: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2CharacterInfo {
    pub bone_count: u32,
    pub vertex_count: u32,
    pub index_count: u32,
    pub surface_count: u32,
}

const _: () = assert!(size_of::<Mw2CharacterInput>() == 40);
const _: () = assert!(size_of::<Mw2CharacterInfo>() == 16);

fn str_arg<'a>(p: *const u8, len: usize) -> Option<&'a str> {
    if p.is_null() {
        return None;
    }
    std::str::from_utf8(unsafe { std::slice::from_raw_parts(p, len) }).ok()
}

/// Point the character system at the MW2 `zone/english` folder (UTF-8 path). Optional:
/// `mw2_load_weapons` already tells it where the install is. Loads `common_mp.ff`'s player
/// anims and scripts now (about a second) so the first `mw2_character_build` is quicker.
/// Returns 1 on success, 0 on failure.
///
/// # Safety
/// `path` points to `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_init(path: *const u8, len: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(p) = str_arg(path, len) else { return 0 };
        i32::from(init(Path::new(p)).is_ok())
    }))
    .unwrap_or(0)
}

/// Build a character for a faction ref name (`us_army`, `opforce_composite`, `socom_141`,
/// `seals_udt`, `militia`...), assault class, first body and head. Null on failure.
///
/// # Safety
/// `faction` points to `len` UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_build(faction: *const u8, len: usize) -> *mut Character {
    unsafe { mw2_character_build_ex(faction, len, std::ptr::null(), 0, 0, 0) }
}

/// Like `mw2_character_build` with the class (`assault`, `smg`, `lmg`, `shotgun`, `sniper`,
/// `riot`; empty = assault) and which body variant / head to use (both wrap).
///
/// # Safety
/// The strings point to their lengths in UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_build_ex(faction: *const u8, len: usize, class: *const u8, class_len: usize, variant: u32, head: u32) -> *mut Character {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(f) = str_arg(faction, len) else { return std::ptr::null_mut() };
        let class = str_arg(class, class_len).filter(|c| !c.is_empty()).unwrap_or("assault");
        match Character::build_with(f, class, variant as usize, head as usize) {
            Ok(c) => Box::into_raw(Box::new(c)),
            Err(_) => std::ptr::null_mut(),
        }
    }))
    .unwrap_or(std::ptr::null_mut())
}

/// Every MP body MW2's scripts give a faction x class (the survivor's skins): one line per
/// unique body, `faction\tclass\tvariant\tbody`, factions in `_teams.gsc` order. The MP
/// `socom_141` set (`mp_body_seal_soccom_*`) ships in no MP zone, so it's left out.
pub fn skin_catalog() -> Result<String, String> {
    let store = ensure_store(None)?;
    let teams = store.common.raw_file("maps/mp/gametypes/_teams.gsc").ok_or("_teams.gsc missing")?;
    let teams = String::from_utf8_lossy(teams);
    let mut factions: Vec<String> = Vec::new();
    for l in teams.lines() {
        if let Some(f) = l.trim().strip_prefix("case \"").and_then(|r| r.split('"').next()) {
            if !factions.iter().any(|x| x == f) {
                factions.push(f.to_owned());
            }
        }
    }
    let lookup = |n: &str| store.common.raw_file(n);
    let mut seen = std::collections::HashSet::new();
    let mut out = String::new();
    for f in &factions {
        for class in ["assault", "smg", "lmg", "shotgun", "sniper", "riot"] {
            let Some(s0) = data::resolve_spec(&lookup, f, class, 0) else { continue };
            for v in 0..s0.variants {
                let Some(s) = data::resolve_spec(&lookup, f, class, v) else { continue };
                if s.body.contains("soccom") || !seen.insert(s.body.clone()) {
                    continue;
                }
                out.push_str(&format!("{f}\t{class}\t{v}\t{}\n", s.body));
            }
        }
    }
    Ok(out)
}

/// The character's own first-person arms (`setViewmodel` in its character script, loaded from the
/// zone with the body) become the viewmodel's hands; false (base hands kept) if it has none.
pub fn use_viewhands(ch: &Character) -> bool {
    let rig = ch.spec.viewhands.as_deref().and_then(|n| {
        let store = ensure_store(None).ok()?;
        let cached = store.rigs.lock().ok()?.get(n).cloned();
        // Not in the zone the body came from (smg_b and friends ride in other maps): its own search.
        cached.or_else(|| store.models_for_load(n, &[]))
    });
    crate::viewmodel::set_hands(rig.as_deref().cloned())
}

/// `use_viewhands` for a built character: 1 if its own arms are now the viewmodel's, 0 if the
/// base arms are (none for it). Rebuild the viewmodel afterwards.
///
/// # Safety
/// `c` from `mw2_character_build`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_use_viewhands(c: *const Character) -> i32 {
    if c.is_null() {
        crate::viewmodel::set_hands(None);
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| i32::from(use_viewhands(unsafe { &*c })))).unwrap_or(0)
}

/// Load every skin's models (body, its head pool) on a background thread, in the selector's order:
/// a character's first build reads them out of the map zones (~0.5 s each, a hitch on every new
/// skin at character select - playtest 10-04-26); cached, a build takes ~1 ms. Once per session; 1 if
/// the thread started.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_character_prewarm() -> i32 {
    static STARTED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
    if STARTED.swap(true, std::sync::atomic::Ordering::SeqCst) {
        return 0;
    }
    let job = || {
        let _ = catch_unwind(|| {
            let Ok(catalog) = skin_catalog() else { return };
            for line in catalog.lines() {
                let p: Vec<&str> = line.split('\t').collect();
                if p.len() < 3 {
                    continue;
                }
                let _ = Character::build_with(p[0], p[1], p[2].parse().unwrap_or(0), 0);
            }
        });
    };
    i32::from(std::thread::Builder::new().name("mw2-skin-prewarm".into()).spawn(job).is_ok())
}

/// `skin_catalog` into `out` (UTF-8, up to `cap` bytes). Returns the full length (call again with
/// a bigger buffer if it's more than `cap`), 0 on failure.
///
/// # Safety
/// `out` valid for `cap` bytes (or null with `cap` 0).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_skins(out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Ok(s) = skin_catalog() else { return 0 };
        let b = s.as_bytes();
        if !out.is_null() && b.len() <= cap as usize {
            unsafe { std::ptr::copy_nonoverlapping(b.as_ptr(), out, b.len()) };
        }
        b.len() as u32
    }))
    .unwrap_or(0)
}

/// # Safety
/// `c` from `mw2_character_build`, not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_destroy(c: *mut Character) {
    if !c.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(c) })));
    }
}

/// # Safety
/// `c`, `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_info(c: *const Character, out: *mut Mw2CharacterInfo) -> i32 {
    if c.is_null() || out.is_null() {
        return 0;
    }
    let ch = unsafe { &*c };
    unsafe {
        *out = Mw2CharacterInfo {
            bone_count: ch.bone_count() as u32,
            vertex_count: ch.positions.len() as u32,
            index_count: ch.indices.len() as u32,
            surface_count: ch.surfaces.len() as u32,
        };
    }
    1
}

/// Unity-space mesh, same layout as `mw2_viewmodel_mesh`: positions / normals 3 floats, uvs 2,
/// bone indices 4 i32 + weights 4 f32 per vertex, u32 triangles, bindposes 16 floats
/// (column-major) per bone. Buffers sized from `mw2_character_info`.
///
/// # Safety
/// All buffers valid for the sizes above.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_mesh(
    c: *const Character,
    positions: *mut f32,
    normals: *mut f32,
    uvs: *mut f32,
    bone_index: *mut i32,
    bone_weight: *mut f32,
    indices: *mut u32,
    bindposes: *mut f32,
) -> i32 {
    if c.is_null() || positions.is_null() || normals.is_null() || uvs.is_null() || bone_index.is_null() || bone_weight.is_null() || indices.is_null() || bindposes.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*c };
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

fn surface_info(s: &Surface) -> Mw2SurfaceInfo {
    let tex = s.color_map.as_deref().and_then(models::texture);
    Mw2SurfaceInfo {
        index_start: s.index_start,
        index_count: s.index_count,
        texture_format: tex.as_ref().map_or(0, |t| u32::from(t.format)),
        texture_width: tex.as_ref().map_or(0, |t| t.width),
        texture_height: tex.as_ref().map_or(0, |t| t.height),
        texture_bytes: tex.as_ref().map_or(0, |t| t.top_mip.len() as u32),
    }
}

fn surface_rgba(s: &Surface, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    let Some(rgba) = s.color_map.as_deref().and_then(crate::fx::texture_rgba) else { return 0 };
    let (iw, ih, px) = &*rgba;
    unsafe {
        *w = *iw;
        *h = *ih;
        if !out.is_null() && cap as usize >= px.len() {
            std::ptr::copy_nonoverlapping(px.as_ptr(), out, px.len());
        }
    }
    px.len() as u32
}

/// A world gun's surface with its paint laid on (camos, the ACOG's black: `images::painted_rgba`,
/// as the viewmodel does), rows flipped like `surface_rgba`; unpainted surfaces as they are.
/// Painted results are kept: the host asks for the size, then the pixels.
fn weapon_surface_rgba(s: &Surface, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    static PAINTED: Mutex<Option<HashMap<(String, String), Option<Arc<(u32, u32, Vec<u8>)>>>>> = Mutex::new(None);
    let key = match (s.color_map.as_deref(), s.material.as_deref()) {
        (Some(c), Some(m)) if crate::models::material_blend(m) == 0 => {
            crate::models::material_textures(m).into_iter().find(|t| t.0 == 3).map(|t| (c.to_owned(), t.2))
        }
        _ => None,
    };
    let Some(key) = key else { return surface_rgba(s, w, h, out, cap) };
    let painted = {
        let mut g = PAINTED.lock().unwrap_or_else(|e| e.into_inner());
        let map = g.get_or_insert_with(HashMap::new);
        map.entry(key.clone())
            .or_insert_with(|| {
                crate::images::painted_rgba(&key.0, &key.1).map(|(iw, ih, mut px)| {
                    let row = iw as usize * 4;
                    let rows = ih as usize;
                    for y in 0..rows / 2 {
                        let (a, b) = px.split_at_mut((rows - 1 - y) * row);
                        a[y * row..y * row + row].swap_with_slice(&mut b[..row]);
                    }
                    Arc::new((iw, ih, px))
                })
            })
            .clone()
    };
    let Some(p) = painted else { return surface_rgba(s, w, h, out, cap) };
    let (iw, ih, px) = &*p;
    unsafe {
        *w = *iw;
        *h = *ih;
        if !out.is_null() && cap as usize >= px.len() {
            std::ptr::copy_nonoverlapping(px.as_ptr(), out, px.len());
        }
    }
    px.len() as u32
}

fn write_str(s: &str, out: *mut u8, cap: u32) {
    if !out.is_null() && cap > 0 {
        let b = s.as_bytes();
        let n = b.len().min(cap as usize - 1);
        unsafe {
            std::ptr::copy_nonoverlapping(b.as_ptr(), out, n);
            *out.add(n) = 0;
        }
    }
}

/// Surface range + colorMap info (same struct as `mw2_model_surface`).
///
/// # Safety
/// `c`, `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_surface(c: *const Character, surface: u32, out: *mut Mw2SurfaceInfo) -> i32 {
    if c.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        let Some(s) = ch.surfaces.get(surface as usize) else { return 0 };
        unsafe { *out = surface_info(s) };
        1
    }))
    .unwrap_or(0)
}

/// The surface's colour image as RGBA8 (bottom row first), decoded natively (like
/// `mw2_model_surface_rgba`). Query with `out` null, then call with a buffer of that size.
///
/// # Safety
/// `c`, `w`, `h` valid; `out` has `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_surface_rgba(c: *const Character, surface: u32, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if c.is_null() || w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        ch.surfaces.get(surface as usize).map_or(0, |s| surface_rgba(s, w, h, out, cap))
    }))
    .unwrap_or(0)
}

/// Material name (written to `name_out`) and MW2 blend mode of a surface (0 opaque, 1 alpha-test,
/// 2 blend, 3 additive, 4 multiply, 5 screen); -1 = bad surface.
///
/// # Safety
/// `c` valid; `name_out` has `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_surface_material(c: *const Character, surface: u32, name_out: *mut u8, cap: u32) -> i32 {
    if c.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        let Some(s) = ch.surfaces.get(surface as usize) else { return -1 };
        write_str(s.material.as_deref().unwrap_or(""), name_out, cap);
        if s.state_bits.is_some() { crate::fx::blend_mode(s.state_bits) } else { 0 }
    }))
    .unwrap_or(-1)
}

/// Advance the animation by `input.dt` and write the pose: 7 floats per bone (Unity-space
/// position xyz in metres, rotation xyzw), MODEL space relative to the character origin at the
/// feet; set every bone as a direct child of one root transform (as the viewmodel does).
///
/// # Safety
/// `c`, `input` valid; `pose_out` has room for 7 * bone_count floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_step(c: *mut Character, input: *const Mw2CharacterInput, pose_out: *mut f32) -> i32 {
    if c.is_null() || input.is_null() || pose_out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &mut *c };
        let input = unsafe { *input };
        let out = unsafe { std::slice::from_raw_parts_mut(pose_out, ch.bone_count() * 7) };
        ch.step(&input, out);
        1
    }))
    .unwrap_or(0)
}

/// `tag_weapon_right` from the last step (7 floats, same space as the pose). 1 found.
///
/// # Safety
/// `c` valid, `out` has 7 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_weapon_tag(c: *const Character, out: *mut f32) -> i32 {
    if c.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        match ch.weapon_tag() {
            Some(t) => {
                unsafe { std::ptr::copy_nonoverlapping(t.as_ptr(), out, 7) };
                1
            }
            None => 0,
        }
    }))
    .unwrap_or(0)
}

/// Any bone / tag by name from the last step (`tag_weapon_left`, `tag_stowed_back`, `j_head`...).
///
/// # Safety
/// `c` valid, `name` points to `len` bytes, `out` has 7 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_tag(c: *const Character, name: *const u8, len: usize, out: *mut f32) -> i32 {
    if c.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        let Some(n) = str_arg(name, len) else { return 0 };
        match ch.tag(n) {
            Some(t) => {
                unsafe { std::ptr::copy_nonoverlapping(t.as_ptr(), out, 7) };
                1
            }
            None => 0,
        }
    }))
    .unwrap_or(0)
}

/// 1 if the weapon is MW2 akimbo (a gun in each hand: the second world gun goes on
/// `tag_weapon_left`), else 0.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_is_akimbo(weapon: u32) -> i32 {
    catch_unwind(|| i32::from(crate::weapons::row(weapon).is_some_and(|r| r.akimbo))).unwrap_or(0)
}

/// 1 if the weapon is MW2's riot shield (weapType 3: held on tag_weapon_left, stowed on
/// tag_shield_back), else 0.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_is_shield(weapon: u32) -> i32 {
    catch_unwind(|| i32::from(crate::weapons::row(weapon).is_some_and(|r| r.facts.weap_type == 3))).unwrap_or(0)
}

/// Bone index by name, or -1.
///
/// # Safety
/// `c` valid, `name` points to `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_find_bone(c: *const Character, name: *const u8, len: usize) -> i32 {
    if c.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        str_arg(name, len).and_then(|n| ch.find_bone(n)).map_or(-1, |i| i as i32)
    }))
    .unwrap_or(-1)
}

/// Bone name (NUL-terminated, truncated) for building the transform tree; returns its length.
///
/// # Safety
/// `c` valid; `out` has `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_bone_name(c: *const Character, bone: u32, out: *mut u8, cap: u32) -> i32 {
    if c.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        let Some(n) = ch.bone_names.get(bone as usize) else { return -1 };
        write_str(n, out, cap);
        n.len() as i32
    }))
    .unwrap_or(-1)
}

/// Parent bone index per bone (-1 for roots), `bone_count` i32s. Only needed to mirror the
/// hierarchy; the pose itself is model space.
///
/// # Safety
/// `c` valid; `out` has `cap` i32s.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_bone_parents(c: *const Character, out: *mut i32, cap: u32) -> i32 {
    if c.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        let n = ch.bone_parents.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(ch.bone_parents.as_ptr(), out, n) };
        n as i32
    }))
    .unwrap_or(0)
}

/// What the character is made of and doing, for logs: models, faction actually used, current
/// legs / torso anims. Written NUL-terminated; returns the length.
///
/// # Safety
/// `c` valid; `out` has `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_describe(c: *const Character, out: *mut u8, cap: u32) -> i32 {
    if c.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let ch = unsafe { &*c };
        let s = format!(
            "faction={} body={} head={} {}",
            ch.resolved_faction,
            ch.body_name,
            ch.head_name.as_deref().unwrap_or("-"),
            ch.debug_state()
        );
        write_str(&s, out, cap);
        s.len() as i32
    }))
    .unwrap_or(-1)
}

/// A weapon's world model XModel name (NUL-terminated into `out`); returns its length, 0 if the
/// weapon has none or the character data is not loaded. Static mesh through
/// `mw2_model_index` / `mw2_model_mesh` (IW4 space), or Unity-ready via `mw2_character_weapon_*`.
///
/// # Safety
/// `out` has `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_world_model(weapon: u32, out: *mut u8, cap: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| match weapon_world_model(weapon) {
        Some(n) => {
            write_str(&n, out, cap);
            n.len() as i32
        }
        None => 0,
    }))
    .unwrap_or(0)
}

/// Counts for the Unity-space world model of a weapon (vertex_count, index_count, surface_count;
/// bone_count 0). 1 if the weapon has a captured world model.
///
/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_weapon_info(weapon: u32, out: *mut Mw2CharacterInfo) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| match world_gun(weapon) {
        Some(g) => {
            unsafe {
                *out = Mw2CharacterInfo {
                    bone_count: 0,
                    vertex_count: g.positions.len() as u32,
                    index_count: g.indices.len() as u32,
                    surface_count: g.surfaces.len() as u32,
                };
            }
            1
        }
        None => 0,
    }))
    .unwrap_or(0)
}

/// The world model as a Unity-space static mesh in the model's own space: parent it to the
/// `mw2_character_weapon_tag` pose. Attachments the variant lacks are already left out.
///
/// # Safety
/// Buffers sized from `mw2_character_weapon_info`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_weapon_mesh(weapon: u32, positions: *mut f32, normals: *mut f32, uvs: *mut f32, indices: *mut u32) -> i32 {
    if positions.is_null() || normals.is_null() || uvs.is_null() || indices.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(g) = world_gun(weapon) else { return 0 };
        let n = g.positions.len();
        unsafe {
            std::ptr::copy_nonoverlapping(g.positions.as_ptr().cast::<f32>(), positions, n * 3);
            std::ptr::copy_nonoverlapping(g.normals.as_ptr().cast::<f32>(), normals, n * 3);
            std::ptr::copy_nonoverlapping(g.uvs.as_ptr().cast::<f32>(), uvs, n * 2);
            std::ptr::copy_nonoverlapping(g.indices.as_ptr(), indices, g.indices.len());
        }
        1
    }))
    .unwrap_or(0)
}

/// Surface range + colorMap info of the Unity-space world model.
///
/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_weapon_surface(weapon: u32, surface: u32, out: *mut Mw2SurfaceInfo) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(g) = world_gun(weapon) else { return 0 };
        let Some(s) = g.surfaces.get(surface as usize) else { return 0 };
        unsafe { *out = surface_info(s) };
        1
    }))
    .unwrap_or(0)
}

/// A world-model surface's blend (0 opaque, 2+ see-through: the riot shield's window), as
/// `mw2_model_surface_material` reports it for props; -1 if there's no such surface.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_character_weapon_surface_blend(weapon: u32, surface: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(g) = world_gun(weapon) else { return -1 };
        let Some(s) = g.surfaces.get(surface as usize) else { return -1 };
        if s.state_bits.is_some() { crate::fx::blend_mode(s.state_bits) } else { 0 }
    }))
    .unwrap_or(-1)
}

/// Colour image of a world-model surface as RGBA8 (see `mw2_character_surface_rgba`).
///
/// # Safety
/// `w`, `h` valid; `out` has `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_character_weapon_surface_rgba(weapon: u32, surface: u32, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    if w.is_null() || h.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(g) = world_gun(weapon) else { return 0 };
        g.surfaces.get(surface as usize).map_or(0, |s| weapon_surface_rgba(s, w, h, out, cap))
    }))
    .unwrap_or(0)
}

#[cfg(test)]
#[path = "character_tests.rs"]
mod tests;
