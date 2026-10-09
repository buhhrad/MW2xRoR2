//! MW2 weapon handling inside mw2sim: IW4L's `weapon_iw4` state machine (fire timing,
//! reload, rechamber, ADS coupling, spread) driven the way IW4L's
//! `sim::combat::advance_weapon_command` drives it, minus melee, dual wield
//! and weapon switching. Every accepted bullet shot becomes one `Mw2Shot` per pellet with
//! MW2's own damage-falloff numbers; the host applies damage. Rockets and launcher rounds
//! become projectiles and offhand equipment (grenades, claymores...) is thrown through the
//! same state machine; both live in `equipment`.

use std::sync::{Arc, RwLock};

use playerstate_iw4::{ENTITYNUM_NONE, PlayerState, UserCmd};
use weapon_iw4::{
    AIM_SPREAD_MOVE_SPEED_THRESHOLD_DEFAULT, AimSpreadMotion, AimSpreadState, SpreadOverrideState,
    WeaponCmd, WeaponCombatFacts, WeaponHandState, WeaponTickEvent, add_aim_spread_fire,
    adjust_aim_spread_scale, fire_weapon_spread_degrees, get_spread_for_weapon,
    perk_weap_spread_multiplier, spawn_weapon_hand, weapon_hands,
};

/// Weapon sound slots (`mw2_weapon_sound` kinds).
pub const SOUND_FIRE: usize = 0;
pub const SOUND_FIRE_LAST: usize = 1;
pub const SOUND_DRY: usize = 2;
pub const SOUND_RELOAD: usize = 3;
pub const SOUND_RELOAD_EMPTY: usize = 4;
pub const SOUND_RELOAD_START: usize = 5;
pub const SOUND_RELOAD_END: usize = 6;
pub const SOUND_RECHAMBER: usize = 7;
pub const SOUND_RAISE: usize = 8;
/// Pin pull / equipment pull-back (offhand weapons; `EV_OFFHAND_PREPARE` is its cue).
pub const SOUND_PULLBACK: usize = 9;
/// Melee swing (`EV_MELEE_START` is its cue).
pub const SOUND_MELEE: usize = 10;
pub const SOUND_SLOTS: usize = 11;

/// Weapon events reported in `Mw2State::events` for the step that produced them.
pub const EV_SHOT: u32 = 1;
pub const EV_DRY: u32 = 2;
pub const EV_RELOAD_START: u32 = 4;
pub const EV_RELOAD_INSERT: u32 = 8;
pub const EV_RELOAD_END: u32 = 16;
pub const EV_RECHAMBER: u32 = 32;
/// Offhand: the pin was pulled (`mw2_offhand_viewmodel_weapon` names the weapon; its
/// `SOUND_PULLBACK` is the cue), the throw left the hand (the projectile's launch event follows),
/// a cooked grenade went off in the hand.
pub const EV_OFFHAND_PREPARE: u32 = 64;
pub const EV_OFFHAND_THROW: u32 = 128;
pub const EV_OFFHAND_COOKOFF: u32 = 256;
/// Melee: the swing started (`EV_MELEE_CHARGE` too when it is MW2's lunge at a target the host
/// named with `mw2_set_melee_target`), and the blade reached the hit point (`melee_delay` /
/// `melee_charge_delay` later) - the host's moment to trace for a hit and play hit / miss.
pub const EV_MELEE_START: u32 = 512;
pub const EV_MELEE_CHARGE: u32 = 1024;
pub const EV_MELEE_HIT: u32 = 2048;

/// One weapon definition from the user's `common_mp.ff`.
#[derive(Clone, Debug)]
pub struct WeaponRow {
    pub name: String,
    pub facts: WeaponCombatFacts,
    pub move_speed_scale: f32,
    pub ads_move_speed_scale: f32,
    pub sprint_duration_scale: f32,
    /// Clip ids into `CLIPS` (1-based, 0 = none) per sound slot: the first variant.
    pub sounds: [u32; SOUND_SLOTS],
    /// Every variant per slot with MW2's per-alias volume and pitch ranges.
    pub variants: [Vec<Variant>; SOUND_SLOTS],
    /// Recoil parameters, verbatim from the weapon definition.
    pub kick: fastfile_iw4::WeaponKickCapture,
    /// Gun model id in `models::MESHES` (0 = none).
    pub model: u32,
    /// Viewmodel gun XModel name, the 37 anim slot names, and hide tags.
    pub gun_model: Option<String>,
    pub xanims: Vec<Option<String>>,
    /// Akimbo: the right / left hand's anim slots (WeaponDef szXAnimsRightHanded / LeftHanded).
    pub xanims_right: Vec<Option<String>>,
    pub xanims_left: Vec<Option<String>>,
    /// MW2 akimbo (dual wield): a `_akimbo` weapon with a left hand to animate (as IW4L decides
    /// it: `gsc_give_weapon_is_akimbo` + a bound left-handed idle), and its viewmodel offset (IW4
    /// units, `dualWieldViewModelOffset`).
    pub akimbo: bool,
    pub dual_offset: f32,
    pub hide_tags: Vec<String>,
    pub timers: crate::viewmodel::Timers,
    /// HUD icon material and MW2's ammo counter style (ammoCounterClip).
    pub hud_icon: Option<String>,
    pub ammo_counter: i32,
    /// HUD: localize key of the display name (`WEAPON_AK47`), the low-ammo warning threshold
    /// (fraction of the clip) and whether the weapon only warns about the clip.
    pub display_name: Option<String>,
    pub low_ammo_threshold: f32,
    pub clip_only: bool,
    /// ADS view: overlay (scope) material and the numbers that drive the zoom.
    pub overlay: Option<String>,
    pub view: crate::Mw2WeaponView,
    /// Idle sway amounts (scope sway when an overlay reticle is up).
    pub idle: weapon_iw4::WeaponIdleInputs,
    /// FX names per `weapon_fx::FX_*` kind (view flash, world flash, view / world shell eject,
    /// view / world last-shot eject), as `mw2_fx_find` takes them.
    pub effects: [Option<String>; 6],
    /// TracerDef name (looked up in `weapon_fx::TRACERS`).
    pub tracer: Option<String>,
    /// Grenade / equipment / projectile facts and names (`equipment`).
    pub equip: Arc<crate::equipment::Equip>,
    /// Alternate weapon name (underbarrel GL / shotgun) and the alt raise / drop times.
    pub alternate: Option<String>,
    pub alternate_raise_ms: i32,
    pub alternate_drop_ms: i32,
    /// Hip crosshair (`mw2_weapon_reticle`): materials and the weapon def's reticle numbers.
    pub reticle_center: Option<String>,
    pub reticle_side: Option<String>,
    pub reticle: crate::weapons::Mw2Reticle,
    /// Bullet penetration: type (0 none, 1 small, 2 medium, 3 large) and depth multiplier.
    pub penetrate_type: i32,
    pub penetrate_multiplier: f32,
    /// Third-person (other players') fire sound alias (fireSound, not the _plr one).
    pub fire_world: Option<String>,
}

#[derive(Clone, Copy, Debug)]
pub struct Variant {
    pub clip: u32,
    pub vol: (f32, f32),
    pub pitch: (f32, f32),
}

/// Loaded once per process; index = position + 1 (0 = no weapon).
pub static TABLE: RwLock<Vec<WeaponRow>> = RwLock::new(Vec::new());
/// PCM clips referenced by weapons; id = position + 1.
pub static CLIPS: RwLock<Vec<mw2data::sounds::LoadedPcm>> = RwLock::new(Vec::new());
/// The same clips decoded for playback (parallel to `CLIPS`).
pub static DECODED: RwLock<Vec<Option<crate::audio::Pcm>>> = RwLock::new(Vec::new());

pub fn load(path: &std::path::Path) -> Result<usize, String> {
    let (weapons, report, common_bank, captured_models) = mw2data::walk_file_full(
        path,
        // weapon/viewmodel meshes, plus the killstreak props the host shows (Care Package crate).
        Some(|n| n.starts_with("viewmodel_") || n.starts_with("weapon_") || n == "com_plasticcase_friendly" || n == "mil_emergency_flare_mp" || n.starts_with("vehicle_") || n.starts_with("sentry_") || n.starts_with("projectile_")),
        // Viewmodels (and weapon_*_viewmodel ones: the One Man Army bag), plus aircraft: rotor /
        // propeller bones spin, tags place their MW2 effects.
        Some(|n| n.starts_with("viewmodel_") || (n.starts_with("weapon_") && n.ends_with("_viewmodel")) || n.starts_with("sentry_minigun") || (n.starts_with("vehicle_") && ["cobra", "pavelow", "apache", "little_bird", "mi24", "mi-28", "ac130", "c130", "uav", "mig29", "harrier", "av8b", "b2"].iter().any(|h| n.contains(h)))),
    )
    .map_err(|e| e.to_string())?;
    crate::images::add_materials(captured_models.material_images());
    crate::images::add_state_bits(captured_models.material_state_bits());
    *crate::models::MATERIAL_TEXTURES.write().map_err(|_| "material table poisoned".to_string())? = Some(captured_models.all_material_textures().into_iter().collect());
    let mw2data::models::ModelCapture { meshes, rig, fx, tracers, impact_tables, .. } = captured_models;
    // The tracer streak material is a reference into code_post_gfx_mp.ff.
    let mut fx = fx;
    if fx.materials.iter().any(|m| m.name.starts_with(',') && m.image.is_none()) {
        if let Some(dir) = path.parent() {
            if let Ok((_, _, _, zone)) = mw2data::walk_file_with_models(&dir.join("code_post_gfx_mp.ff"), Some(|_| false)) {
                fx.resolve_referenced(|name| zone.material_by_name(name));
            }
        }
    }
    *crate::weapon_fx::TRACERS.write().map_err(|_| "tracer table poisoned".to_string())? = tracers;
    *crate::weapon_fx::IMPACT.write().map_err(|_| "impact table poisoned".to_string())? = impact_tables;
    // Effects ride the same walk; `mw2_fx_init` builds the runtime table from them.
    *crate::fx::CAPTURED.lock().unwrap_or_else(|e| e.into_inner()) = Some(fx);
    *crate::models::MESHES.write().map_err(|_| "model table poisoned".to_string())? = meshes;
    *crate::viewmodel::RIGS.write().map_err(|_| "rig table poisoned".to_string())? = rig.models;
    *crate::viewmodel::XANIMS.write().map_err(|_| "xanim table poisoned".to_string())? =
        Some(rig.xanims.into_iter().map(|(k, v)| (k.to_ascii_lowercase(), std::sync::Arc::new(v))).collect());
    if let Some(stopped) = report.stopped {
        return Err(format!("zone walk stopped at {}/{}: {stopped}", report.assets_walked, report.assets_total));
    }
    // Weapon audio lives in the localized zone beside common_mp.
    let mut banks = Vec::new();
    if let Some(dir) = path.parent() {
        if let Ok((_, _, b)) = mw2data::walk_file(&dir.join("localized_common_mp.ff")) {
            banks.push(b);
        }
        // The announcer's match-start lines (<prefix>1mc_boost) are in this one.
        if let Ok((_, _, b)) = mw2data::walk_file(&dir.join("localized_code_post_gfx_mp.ff")) {
            banks.push(b);
        }
    }
    banks.push(common_bank);

    let mut clips: Vec<mw2data::sounds::LoadedPcm> = Vec::new();
    let mut clip_ids: std::collections::HashMap<String, u32> = Default::default();
    let mut variants_for = |alias: &Option<String>| -> Vec<Variant> {
        let Some(alias) = alias else { return Vec::new() };
        let Some(bank) = banks.iter().find(|b| !b.variants(alias).is_empty()) else { return Vec::new() };
        let mut out = Vec::new();
        for row in bank.variants(alias) {
            let Some(name) = row.loaded.as_ref() else { continue };
            let id = match clip_ids.get(name) {
                Some(&id) => id,
                None => {
                    let id = match bank.loaded.get(name) {
                        Some(p) if p.format == mw2data::sounds::MSS_PCM && (p.bits == 16 || p.bits == 8) => {
                            clips.push(p.clone());
                            clips.len() as u32
                        }
                        _ => 0,
                    };
                    clip_ids.insert(name.clone(), id);
                    id
                }
            };
            if id != 0 {
                out.push(Variant { clip: id, vol: row.vol, pitch: row.pitch });
            }
        }
        out
    };

    let mut rows = Vec::with_capacity(weapons.len());
    for w in weapons {
        if let Ok(facts) = mw2data::facts::combat_facts(&w.geometry) {
            let sn = &w.sounds;
            let variants: [Vec<Variant>; SOUND_SLOTS] = [
                variants_for(&sn.fire_plr),
                variants_for(&sn.fire_last_plr),
                variants_for(&sn.empty_fire_plr),
                variants_for(&sn.reload_plr),
                variants_for(&sn.reload_empty_plr),
                variants_for(&sn.reload_start_plr),
                variants_for(&sn.reload_end_plr),
                variants_for(&sn.rechamber_plr),
                variants_for(&sn.raise_plr),
                variants_for(&sn.pullback_plr),
                variants_for(&sn.melee_swipe_plr),
            ];
            let sounds = std::array::from_fn(|i| variants[i].first().map_or(0, |v| v.clip));
            let equip = Arc::new(crate::equipment::Equip::from_captured(&w, &facts));
            rows.push(WeaponRow {
                name: w.name.clone(),
                facts,
                move_speed_scale: w.geometry.move_speed_scale,
                ads_move_speed_scale: w.geometry.ads_move_speed_scale,
                sprint_duration_scale: w.geometry.sprint_duration_scale,
                sounds,
                variants,
                kick: w.geometry.kick,
                model: w
                    .gun_model
                    .as_deref()
                    .map(crate::models::index_of)
                    .filter(|&m| m != 0)
                    .or_else(|| w.world_model.as_deref().map(crate::models::index_of))
                    .unwrap_or(0),
                gun_model: w.gun_model.clone(),
                xanims: w.xanims.clone(),
                xanims_right: w.xanims_right.clone(),
                xanims_left: w.xanims_left.clone(),
                akimbo: w.name.contains("_akimbo") && !w.geometry.no_dual_wield && w.xanims_left.get(1).is_some_and(Option::is_some),
                dual_offset: w.geometry.dual_wield_view_model_offset,
                hide_tags: w.hide_tags.clone(),
                hud_icon: w.hud_icon.clone(),
                ammo_counter: w.geometry.ammo_counter_clip,
                display_name: w.display_name.clone(),
                low_ammo_threshold: w.geometry.low_ammo_warning_threshold,
                clip_only: w.geometry.clip_only,
                overlay: w.overlay.clone(),
                effects: [
                    w.view_flash.clone(),
                    w.world_flash.clone(),
                    w.view_shell_eject.clone(),
                    w.world_shell_eject.clone(),
                    w.view_last_shot_eject.clone(),
                    w.world_last_shot_eject.clone(),
                ],
                tracer: w.tracer.clone(),
                equip,
                alternate: w.alternate_weapon.clone(),
                alternate_raise_ms: w.geometry.alternate_raise_time_ms,
                alternate_drop_ms: w.geometry.alternate_drop_time_ms,
                fire_world: w.sounds.fire.clone(),
                reticle_center: w.reticle_center.clone(),
                reticle_side: w.reticle_side.clone(),
                penetrate_type: w.geometry.penetrate_type,
                penetrate_multiplier: w.geometry.penetrate_multiplier,
                reticle: Mw2Reticle {
                    center_size: w.geometry.reticle_center_size as f32,
                    side_size: w.geometry.i_reticle_side_size as f32,
                    min_ofs: w.geometry.i_reticle_min_ofs as f32,
                    side_pos: w.geometry.hip_reticle_side_pos,
                    ads_in_frac: w.geometry.ads_crosshair_in_frac,
                    ads_out_frac: w.geometry.ads_crosshair_out_frac,
                },
                idle: weapon_iw4::WeaponIdleInputs {
                    ads_idle_amount: w.geometry.idle.ads_idle_amount,
                    hip_idle_amount: w.geometry.idle.hip_idle_amount,
                    ads_idle_speed: w.geometry.idle.ads_idle_speed,
                    hip_idle_speed: w.geometry.idle.hip_idle_speed,
                    idle_crouch_factor: w.geometry.idle.idle_crouch_factor,
                    idle_prone_factor: w.geometry.idle.idle_prone_factor,
                },
                view: crate::Mw2WeaponView {
                    ads_zoom_fov: w.geometry.ads_zoom_fov,
                    ads_zoom_in_frac: w.geometry.ads_zoom_in_frac,
                    ads_zoom_out_frac: w.geometry.ads_zoom_out_frac,
                    overlay_width: w.geometry.ads_overlay_width,
                    overlay_height: w.geometry.ads_overlay_height,
                    overlay_reticle: w.geometry.overlay_reticle,
                    has_overlay: i32::from(w.overlay.is_some()),
                    _pad: 0,
                },
                timers: crate::viewmodel::Timers {
                    raise_ms: facts.raise_time_ms,
                    drop_ms: facts.drop_time_ms,
                    quick_raise_ms: facts.quick_raise_time_ms,
                    quick_drop_ms: facts.quick_drop_time_ms,
                    melee_ms: facts.melee_time_ms,
                    melee_charge_ms: facts.melee_charge_time_ms,
                    reload_ms: facts.reload_time_ms,
                    reload_empty_ms: facts.reload_empty_time_ms,
                    reload_start_ms: facts.reload_start_time_ms,
                    reload_end_ms: facts.reload_end_time_ms,
                    reload_add_ms: facts.reload_add_time_ms,
                    sprint_raise_ms: facts.sprint_raise_time_ms,
                    sprint_drop_ms: facts.sprint_drop_time_ms,
                },
            });
        }
    }
    let n = rows.len();
    drop(variants_for);
    *crate::sounds::BANKS.write().map_err(|_| "sound banks poisoned".to_string())? = banks;
    // <mw2>/zone/<language>/common_mp.ff -> <mw2>/main
    if let Some(zone) = path.parent() {
        crate::models::set_zone_dir(zone.to_path_buf());
    }
    if let Some(root) = path.parent().and_then(|p| p.parent()).and_then(|p| p.parent()) {
        crate::sounds::start_iwd_index(root.join("main"));
        crate::models::set_main_dir(root.join("main"));
    }
    // Killstreak call-in weapons whose viewmodel reference didn't resolve by name use MW2's
    // shared ones: the detonator (killstreak_uav_mp), the laptop, or the smoke grenade.
    let donor = |name: &str| rows.iter().find(|r| r.name == name).filter(|r| r.gun_model.is_some()).map(|r| (r.gun_model.clone(), r.xanims.clone()));
    // The airstrikes (no model at all: they open MW2's location selector) show the laptop.
    let (trigger, smoke, laptop) = (donor("killstreak_uav_mp"), donor("airdrop_marker_mp"), donor("killstreak_ac130_mp"));
    for r in rows.iter_mut().filter(|r| r.gun_model.is_none()) {
        let d = if r.name.ends_with("_marker_mp") { &smoke } else if r.name.starts_with("killstreak_") && r.name.contains("airstrike") { &laptop } else if r.name.starts_with("killstreak_") { &trigger } else { &None };
        if let Some((g, x)) = d {
            r.gun_model = g.clone();
            r.xanims = x.clone();
        }
    }
    *TABLE.write().map_err(|_| "weapon table poisoned".to_string())? = rows;
    *DECODED.write().map_err(|_| "clip table poisoned".to_string())? = clips.iter().map(crate::audio::Pcm::from_loaded).collect();
    *CLIPS.write().map_err(|_| "clip table poisoned".to_string())? = clips;
    Ok(n)
}

pub fn clip(id: u32) -> Option<mw2data::sounds::LoadedPcm> {
    if id == 0 {
        return None;
    }
    CLIPS.read().ok()?.get(id as usize - 1).cloned()
}

/// Camo per weapon (mp/camoTable.csv names: woodland, desert ...): IW4 keeps one gun model per camo
/// (gunXModel[camo]); the zones name them `<model>_<camo>` (viewmodel_ak47_tactical_woodland).
static CAMOS: RwLock<Option<std::collections::HashMap<u32, String>>> = RwLock::new(None);

/// The camo model names to try for a gun model, in order: `<model>_<camo>`, then with the model's
/// last `_part` swapped for the camo (a default finish: masada_dust -> masada_red_tiger).
pub fn camo_model_names(model: &str, camo: &str) -> Vec<String> {
    let mut v = vec![format!("{model}_{camo}")];
    if let Some((base, _)) = model.rsplit_once('_') {
        v.push(format!("{base}_{camo}"));
    }
    v
}

pub fn camo(weapon: u32) -> Option<String> {
    CAMOS.read().ok()?.as_ref()?.get(&weapon).cloned()
}

/// Set (or clear with "" / "none") the camo a weapon is drawn with.
///
/// # Safety
/// `name` points to `len` bytes of UTF-8 or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_weapon_camo(weapon: u32, name: *const u8, len: usize) {
    let n = if name.is_null() { "" } else { std::str::from_utf8(unsafe { std::slice::from_raw_parts(name, len) }).unwrap_or("") };
    if let Ok(mut g) = CAMOS.write() {
        let m = g.get_or_insert_with(std::collections::HashMap::new);
        if n.is_empty() || n == "none" {
            m.remove(&weapon);
        } else {
            m.insert(weapon, n.to_owned());
        }
    }
}

pub fn index_of(name: &str) -> u32 {
    TABLE
        .read()
        .ok()
        .and_then(|t| t.iter().position(|r| r.name == name))
        .map_or(0, |i| i as u32 + 1)
}

pub fn row(index: u32) -> Option<WeaponRow> {
    if index == 0 {
        return None;
    }
    TABLE.read().ok()?.get(index as usize - 1).cloned()
}

/// RoR2 attack speed shortens every MW2 weapon timer. Timers already running keep
/// their old length until the next state change (see docs/weapons-plan.md).
pub fn scaled(mut f: WeaponCombatFacts, rate: f32) -> WeaponCombatFacts {
    let rate = if rate.is_finite() && rate > 0.01 { rate } else { 1.0 };
    if (rate - 1.0).abs() < 1e-4 {
        return f;
    }
    let s = |ms: i32| if ms <= 0 { ms } else { ((ms as f32 / rate).round() as i32).max(1) };
    f.fire_time_ms = s(f.fire_time_ms);
    f.fire_delay_ms = s(f.fire_delay_ms);
    f.reload_time_ms = s(f.reload_time_ms);
    f.reload_empty_time_ms = s(f.reload_empty_time_ms);
    f.reload_start_time_ms = s(f.reload_start_time_ms);
    f.reload_end_time_ms = s(f.reload_end_time_ms);
    f.reload_add_time_ms = s(f.reload_add_time_ms);
    f.reload_start_add_time_ms = s(f.reload_start_add_time_ms);
    f.rechamber_time_ms = s(f.rechamber_time_ms);
    f.rechamber_bolt_time_ms = s(f.rechamber_bolt_time_ms);
    f.rechamber_bolt_delay_ms = s(f.rechamber_bolt_delay_ms);
    f.burst_cooldown_ms = s(f.burst_cooldown_ms);
    f.ads_reload_trans_time_ms = s(f.ads_reload_trans_time_ms);
    f
}

/// One pellet, in IW4 units (Z up). Damage fields are MW2's raw values.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2Shot {
    pub origin: [f32; 3],
    pub dir: [f32; 3],
    pub damage: f32,
    pub min_damage: f32,
    pub max_damage_range: f32,
    pub min_damage_range: f32,
    pub max_range: f32,
    pub weapon: u32,
    pub pellet: u32,
    /// 0 the right hand, 1 the left (akimbo).
    pub hand: u32,
}

/// xorshift64, as IW4L's `MatchRng`.
pub struct Rng(u64);

impl Rng {
    pub fn new(seed: u64) -> Self {
        Self(seed ^ 0x9E37_79B9_7F4A_7C15)
    }
    pub fn next_u32(&mut self) -> u32 {
        let mut x = self.0;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.0 = x;
        (x.wrapping_mul(0x2545_F491_4F6C_DD1D) >> 32) as u32
    }
}

/// IW4L `sim::combat::spread_direction_on_plane` with plane = 1.
fn pellet_direction(angles: [f32; 3], spread_degrees: f32, rng: &mut Rng) -> [f32; 3] {
    direction_on_plane(angles, spread_degrees, rng, 1.0)
}

/// IW4L `sim::combat::spread_direction_on_plane`: a direction inside the spread cone; bullets use
/// plane 1, rockets `weapon_iw4::ROCKET_SPREAD_PLANE`.
pub fn direction_on_plane(angles: [f32; 3], spread_degrees: f32, rng: &mut Rng, plane: f32) -> [f32; 3] {
    let (forward, right, up) = math_iw4::angle_vectors(angles);
    if spread_degrees <= 0.0 {
        return forward;
    }
    let unit = |draw: u32| draw as f32 / u32::MAX as f32;
    let radius = unit(rng.next_u32());
    let theta = unit(rng.next_u32()) * core::f32::consts::TAU;
    let lateral = plane * spread_degrees.to_radians().tan() * radius;
    let q = [
        plane * forward[0] + lateral * (theta.cos() * right[0] + theta.sin() * up[0]),
        plane * forward[1] + lateral * (theta.cos() * right[1] + theta.sin() * up[1]),
        plane * forward[2] + lateral * (theta.cos() * right[2] + theta.sin() * up[2]),
    ];
    let len = (q[0] * q[0] + q[1] * q[1] + q[2] * q[2]).sqrt();
    if len > 0.0 { [q[0] / len, q[1] / len, q[2] / len] } else { forward }
}

pub struct Armed {
    pub index: u32,
    pub row: WeaponRow,
    pub hand: WeaponHandState,
    /// Akimbo's left hand (IW4 hand 1: its own clip, state and anims; stock is shared).
    pub left: Option<WeaponHandState>,
    pub kick: crate::kick::ViewKick,
    /// Current spread cone in degrees (what MW2 would draw as the crosshair gap).
    pub spread_degrees: f32,
}

impl Armed {
    pub fn new(index: u32, row: WeaponRow) -> Self {
        let mut hand = spawn_weapon_hand(index, &row.facts);
        let left = row.akimbo.then(|| {
            // IW4: two clips from the start ammo, the rest shared stock (spawn_clip_stock).
            let (clip0, clip1, stock) = weapon_iw4::spawn_clip_stock(&row.facts, 1);
            hand.clip = clip0;
            hand.stock = stock;
            WeaponHandState { hand_index: 1, clip: clip1, ..hand }
        });
        Self { index, row, hand, left, kick: crate::kick::ViewKick::default(), spread_degrees: 0.0 }
    }

    /// 1 when akimbo (IW4's `last_weapon_hand`), else 0.
    pub fn last_hand(&self) -> i32 {
        i32::from(self.left.is_some())
    }

    /// Mirror the hand into the player state fields pmove reads (ADS, sprint).
    pub fn mirror_into(&self, ps: &mut PlayerState) {
        ps.weapon = self.index;
        ps.weaponstate_primary = self.hand.weaponstate;
        ps.weapon_time = self.hand.weapon_time;
        ps.weapon_delay = self.hand.weapon_delay;
        ps.weap_anim = self.hand.weap_anim;
        ps.weapon_restrict_kick_time = self.hand.weapon_restrict_kick_time;
        // Akimbo: IW4's second hand (pmove's is_ads_allowed refuses ADS while it's held).
        ps.last_weapon_hand = self.last_hand();
        if let Some(l) = &self.left {
            ps.weaponstate_secondary = l.weaponstate;
            ps.weapon_time_secondary = l.weapon_time;
            ps.weapon_delay_secondary = l.weapon_delay;
            ps.weap_anim_secondary = l.weap_anim;
        }
    }

    /// Run after pmove. Appends one `Mw2Shot` per pellet fired this tick; rockets, launcher
    /// rounds and thrown offhand equipment go to `launcher` instead.
    #[allow(clippy::too_many_arguments)]
    pub fn tick(
        &mut self,
        ps: &mut PlayerState,
        cmd: &UserCmd,
        old_buttons: u32,
        old_angles: [i32; 3],
        msec: i32,
        fire_rate: f32,
        rng: &mut Rng,
        shots: &mut Vec<Mw2Shot>,
        launcher: &mut crate::equipment::Launcher<'_>,
        melee_target: (f32, u8),
    ) -> u32 {
        let facts = scaled(self.row.facts, fire_rate);
        let mut raised = 0u32;
        let frametime = msec as f32 * 0.001;

        let mut spread = AimSpreadState {
            aim_spread_scale: ps.aim_spread_scale,
            spread_override: ps.spread_override,
            spread_override_state: ps.spread_override_state,
        };
        let motion = AimSpreadMotion {
            frametime,
            cmd_angles: cmd.angles,
            old_angles,
            forwardmove: cmd.forwardmove,
            rightmove: cmd.rightmove,
            velocity_xy: [ps.velocity[0], ps.velocity[1]],
            speed: ps.speed,
            move_speed_threshold: AIM_SPREAD_MOVE_SPEED_THRESHOLD_DEFAULT,
        };
        adjust_aim_spread_scale(
            &mut spread,
            &facts.spread_facts(),
            &facts.aim_spread_decay_facts(),
            ps.ground_entity_num,
            ps.pm_type,
            ps.e_flags,
            ps.f_weapon_pos_frac,
            &motion,
        );
        ps.aim_spread_scale = spread.aim_spread_scale;
        ps.spread_override_state = spread.spread_override_state;

        let mut wcmd = WeaponCmd {
            msec,
            server_time: cmd.server_time,
            stun_time: ps.stun_time,
            buttons: cmd.buttons,
            old_buttons,
            cmd_weapon: self.index as u16,
            pm_flags: ps.pm_flags,
            weap_flags: ps.weap_flags,
            pm_type: ps.pm_type,
            e_flags: ps.e_flags,
            // Akimbo: IW4 fires the left hand on ATTACK and the right on THROW.
            last_weapon_hand: self.last_hand(),
            f_weapon_pos_frac: ps.f_weapon_pos_frac,
            is_in_air: ps.ground_entity_num == ENTITYNUM_NONE,
            cmd_weapon_owned: true,
            switch_raise_time_ms: facts.raise_time_ms,
            switch_quick_raise_time_ms: facts.quick_raise_time_ms,
            offhand: launcher.offhand.to_cmd(ps, facts.quick_drop_time_ms, fire_rate),
            melee_charge_yaw: melee_target.0,
            melee_charge_dist: melee_target.1,
            // Engine perks (Sleight of Hand: reload states run at 1 / perk_weapReloadMultiplier).
            perks0: ps.perks[0],
            ..WeaponCmd::default()
        };
        // A pump / bolt gun with its rechamber pending, on the tick its fire time runs out: no trigger.
        // IW4 starts the rechamber from READY; a press landing on that tick fired again straight from
        // FIRING and the SPAS-12 / Model 1887 / Intervention never pumped (playtest 10-04-26).
        // A trigger still held keeps its shot count (semi-auto: a released trigger resets it, and
        // the held one would refire after the pump).
        let pump_tick = facts.bolt_action
            && self.hand.rechamber_pending
            && self.hand.weaponstate == weapon_iw4::WeaponState::Firing as i32
            && self.hand.weapon_time <= msec as i32;
        let held_shots = self.hand.shot_count;
        if pump_tick {
            wcmd.buttons &= !playerstate_iw4::buttons::ATTACK;
        }
        wcmd.melee_charge.melee_charge_yaw = ps.melee_charge_yaw;
        wcmd.melee_charge.melee_charge_dist = ps.melee_charge_dist;
        wcmd.melee_charge.melee_charge_time = ps.melee_charge_time;
        let mut hands = [self.hand, self.left.unwrap_or(WeaponHandState { hand_index: 1, ..WeaponHandState::default() })];
        let events = weapon_hands(&mut hands, &facts, &mut wcmd, self.last_hand());
        self.hand = hands[0];
        if pump_tick && cmd.buttons & playerstate_iw4::buttons::ATTACK != 0 {
            self.hand.shot_count = self.hand.shot_count.max(held_shots);
        }
        self.hand.weapon = self.index; // no switching yet
        if self.left.is_some() {
            let mut l = hands[1];
            l.weapon = self.index;
            self.left = Some(l);
        }
        ps.pm_flags = wcmd.pm_flags;
        ps.weap_flags = wcmd.weap_flags;
        if let Some(charge) = wcmd.melee_started {
            // The lunge: pmove (calc_melee_charge_time / melee_charge_move) carries it from here.
            ps.melee_charge_yaw = wcmd.melee_charge.melee_charge_yaw;
            ps.melee_charge_dist = wcmd.melee_charge.melee_charge_dist;
            ps.melee_charge_time = wcmd.melee_charge.melee_charge_time;
            raised |= EV_MELEE_START | if charge { EV_MELEE_CHARGE } else { 0 };
        }
        launcher.offhand.sync(&wcmd.offhand);
        ps.off_hand_index = wcmd.offhand.off_hand_index;
        ps.grenade_time_left = wcmd.offhand.grenade_time_left;
        self.mirror_into(ps);

        for &(hand, event) in events.iter().flatten() {
            // The left hand (akimbo) fires, dry-fires and reloads too; the rest is the right's.
            let left_ok = matches!(
                event,
                WeaponTickEvent::ShotAccepted { .. } | WeaponTickEvent::EmptyClick | WeaponTickEvent::ReloadStarted | WeaponTickEvent::ReloadEnded
            );
            if hand != 0 && !(hand == 1 && self.left.is_some() && left_ok) {
                continue;
            }
            raised |= match event {
                WeaponTickEvent::ShotAccepted { .. } => EV_SHOT,
                WeaponTickEvent::EmptyClick => EV_DRY,
                WeaponTickEvent::ReloadStarted => EV_RELOAD_START,
                WeaponTickEvent::ReloadInsert => EV_RELOAD_INSERT,
                WeaponTickEvent::ReloadEnded => EV_RELOAD_END,
                WeaponTickEvent::RechamberWeapon => EV_RECHAMBER,
                WeaponTickEvent::MeleeFired => EV_MELEE_HIT,
                _ => 0,
            };
            raised |= crate::equipment::on_offhand_event(launcher, ps, event, rng);
            if let WeaponTickEvent::ShotAccepted { .. } = event {
                self.kick.seed_fire(&self.row.kick, ps.f_weapon_pos_frac, ps.weap_flags, ps.recoil_scale, self.hand.weapon_restrict_kick_time > 0);
                let origin = [ps.origin[0], ps.origin[1], ps.origin[2] + ps.view_height_current];
                add_aim_spread_fire(&mut ps.aim_spread_scale, ps.f_weapon_pos_frac, facts.hip_spread_fire_add);
                let ads_frac = ps.f_weapon_pos_frac.clamp(0.0, 1.0);
                let cone = get_spread_for_weapon(
                    ps.view_height_current,
                    ps.spread_override,
                    SpreadOverrideState::from_i32(ps.spread_override_state),
                    &facts.spread_facts(),
                    perk_weap_spread_multiplier(ps.perks[0]),
                );
                let spread_degrees = fire_weapon_spread_degrees(cone, facts.ads_spread, ads_frac, ps.aim_spread_scale);
                let min_damage = if facts.min_damage > 0 { facts.min_damage } else { facts.damage };
                let kind = weapon_iw4::fire_weapon_kind(facts.weap_type, facts.weap_class);
                let launched = crate::equipment::fire_gun(launcher, self.index, &self.row.equip, kind, origin, ps.viewangles, spread_degrees, ps.velocity, rng);
                for pellet in 0..if launched { 0 } else { facts.pellet_count().max(1) } {
                    shots.push(Mw2Shot {
                        origin,
                        dir: pellet_direction(ps.viewangles, spread_degrees, rng),
                        damage: facts.damage as f32,
                        min_damage: min_damage as f32,
                        max_damage_range: facts.max_damage_range,
                        min_damage_range: facts.min_damage_range,
                        max_range: facts.bullet_range(),
                        weapon: self.index,
                        pellet: pellet as u32,
                        hand: u32::from(hand),
                    });
                }
            }
        }
        self.kick.advance(&self.row.kick, ps.f_weapon_pos_frac, msec);
        let cone = get_spread_for_weapon(
            ps.view_height_current,
            ps.spread_override,
            SpreadOverrideState::from_i32(ps.spread_override_state),
            &facts.spread_facts(),
            perk_weap_spread_multiplier(ps.perks[0]),
        );
        self.spread_degrees = fire_weapon_spread_degrees(cone, facts.ads_spread, ps.f_weapon_pos_frac.clamp(0.0, 1.0), ps.aim_spread_scale);
        raised
    }
}

/// Name MW2's melee lunge target for the next melee press: `yaw` (degrees, IW4) toward it and
/// `dist` (IW4 units) to close, 0 for none. The host does what IW4's client melee-charge search
/// does (an enemy in front within lunge range); farther than `player_meleeRange` (64) MW2 lunges
/// (`melee_charge` anim, pmove carries the player), nearer it is a plain stab.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_set_melee_target(sim: *mut crate::Mw2Sim, yaw: f32, dist: f32) {
    if sim.is_null() {
        return;
    }
    let sim = unsafe { &mut *sim };
    sim.melee_target = if dist.is_finite() && dist > 0.0 && yaw.is_finite() { (yaw, dist.clamp(1.0, 255.0) as u8) } else { (0.0, 0) };
}

/// The weapon's MW2 melee damage (`meleeDamage`, 135 on MW2's guns), 0 if unknown.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_melee_damage(weapon: u32) -> i32 {
    std::panic::catch_unwind(|| row(weapon).map_or(0, |r| r.facts.melee_damage)).unwrap_or(0)
}

/// The weapon's IW4 `weapClass` (0 rifle, 1 sniper, 2 mg, 3 smg, 4 spread, 5 pistol, 6 grenade,
/// 7 rocket launcher ...), -1 if unknown.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_class(weapon: u32) -> i32 {
    std::panic::catch_unwind(|| row(weapon).map_or(-1, |r| r.facts.weap_class)).unwrap_or(-1)
}

/// The weapon's MW2 alternate (underbarrel grenade launcher / shotgun) as a weapon index, 0 for
/// none. MW2 toggles it with +actionslot 3; the alternate's own alternate leads back.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_alternate(weapon: u32) -> u32 {
    std::panic::catch_unwind(|| row(weapon).and_then(|r| r.alternate).map_or(0, |n| index_of(&n))).unwrap_or(0)
}

/// Switch to / from the alternate weapon the MW2 way: the new weapon comes up in
/// `RaisingAltswitch` for its `altRaiseTime` (else `fallback_raise_ms`) playing its alt_raise
/// anim, instead of a full draw. Ammo is the host's to restore (`mw2_set_ammo`). 1 on success.
///
/// # Safety
/// `sim` from `mw2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_switch_alternate(sim: *mut crate::Mw2Sim, weapon: u32, fallback_raise_ms: i32) -> i32 {
    if sim.is_null() || weapon == 0 {
        return 0;
    }
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if unsafe { crate::mw2_give_weapon(sim, weapon) } != 1 {
            return 0;
        }
        let sim = unsafe { &mut *sim };
        let Some(a) = sim.armed.as_mut() else { return 0 };
        let raise = if a.row.alternate_raise_ms > 0 { a.row.alternate_raise_ms } else { fallback_raise_ms.max(1) };
        a.hand.weaponstate = weapon_iw4::WeaponState::RaisingAltswitch as i32;
        a.hand.weapon_time = raise;
        a.hand.weapon_delay = 0;
        a.hand.weap_anim = ((!(a.hand.weap_anim as u32) & 0x200) | 0x12) as i32; // start_weapon_anim(ALT_RAISE): flip restart bit
        a.mirror_into(&mut sim.ps);
        1
    }))
    .unwrap_or(0)
}

/// The weapon's MW2 alternate raise time (ms), 0 if none.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_alternate_raise_ms(weapon: u32) -> i32 {
    std::panic::catch_unwind(|| row(weapon).map_or(0, |r| r.alternate_raise_ms)).unwrap_or(0)
}

/// MW2's hip crosshair numbers for a weapon (IW4 reticleCenterSize, reticleSideSize,
/// reticleMinOfs, hipReticleSidePos, adsCrosshairIn/OutFrac), in MW2's 640x480 virtual units.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2Reticle {
    pub center_size: f32,
    pub side_size: f32,
    pub min_ofs: f32,
    pub side_pos: f32,
    pub ads_in_frac: f32,
    pub ads_out_frac: f32,
}
const _: () = assert!(std::mem::size_of::<Mw2Reticle>() == 24);

/// Fill `out` with the weapon's crosshair numbers; the materials are `mw2_weapon_string` fields
/// 3 (center) and 4 (side). 1 on success.
///
/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_reticle(weapon: u32, out: *mut Mw2Reticle) -> i32 {
    if out.is_null() {
        return 0;
    }
    std::panic::catch_unwind(|| row(weapon).map(|r| r.reticle))
        .ok()
        .flatten()
        .map_or(0, |r| {
            unsafe { *out = r };
            1
        })
}
