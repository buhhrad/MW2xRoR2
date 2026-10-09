//! What a shot looks like in MW2, straight from the weapon definitions in `common_mp.ff`:
//! muzzle flash / shell eject effect names, the TracerDef (bullet streak), the impact table
//! (which effect a round makes when it hits a surface) and the IW4 surface type names; plus
//! the viewmodel's muzzle tag position.
//!
//! Effects are not simulated here: `mw2_weapon_fx` / `mw2_impact_fx` return names for
//! `mw2_fx_find` + `mw2_fx_play`. Tracer materials are entries of the FX material table, so
//! `mw2_tracer_texture` / `mw2_tracer_blend` are `mw2_fx_texture` / `mw2_fx_material` under
//! another name, and (like those) need `mw2_fx_init` to have run.
//!
//! Units: IW4 inches, Z up (tracer speed is inches per second); the viewmodel tag is Unity
//! space, metres, relative to the eye (see `viewmodel`).

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::RwLock;

use mw2data::fx::{ImpactTable, TracerDef};

use crate::viewmodel::Viewmodel;
use crate::weapons;

/// Loaded once with the weapons (`mw2_load_weapons`).
pub static TRACERS: RwLock<Vec<TracerDef>> = RwLock::new(Vec::new());
pub static IMPACT: RwLock<Vec<ImpactTable>> = RwLock::new(Vec::new());

/// `mw2_weapon_fx` kinds.
pub const FX_VIEW_FLASH: u32 = 0;
pub const FX_WORLD_FLASH: u32 = 1;
pub const FX_VIEW_SHELL_EJECT: u32 = 2;
pub const FX_WORLD_SHELL_EJECT: u32 = 3;
pub const FX_VIEW_LAST_SHOT_EJECT: u32 = 4;
pub const FX_WORLD_LAST_SHOT_EJECT: u32 = 5;
/// Projectile weapons (grenades, equipment, rockets): the explosion effect (smoke grenade plume,
/// flashbang flash...), the in-flight trail, the beacon and the rocket ignition.
pub const FX_EXPLOSION: u32 = 6;
pub const FX_PROJECTILE_TRAIL: u32 = 7;
pub const FX_PROJECTILE_BEACON: u32 = 8;
pub const FX_PROJECTILE_IGNITION: u32 = 9;

/// Surface columns of an impact table row: 0..=30 are the IW4 surface types
/// (`SURFACE_NAMES`), 31..=34 the four flesh variants (see [`flesh_column`]).
pub const SURFACE_TYPES: usize = mw2data::fx::IMPACT_SURFACES;
pub const SURFACE_COLUMNS: usize = mw2data::fx::IMPACT_COLUMNS;
/// IW4's surface type used for flesh hits (its table cell is usually empty; the flesh cells
/// hold the effects).
pub const SURFACE_FLESH: u32 = 7;

/// IW4 surface type names in table order (`movement_iw4::SURFACE_TYPE_NAMES`; index 0 is "default").
pub fn surface_name(surface: u32) -> Option<&'static str> {
    const FLESH_NAMES: [&str; 4] = ["flesh_body", "flesh_body_fatal", "flesh_head", "flesh_head_fatal"];
    let i = surface as usize;
    movement_iw4::SURFACE_TYPE_NAMES.get(i).copied().or_else(|| FLESH_NAMES.get(i.checked_sub(SURFACE_TYPES)?).copied())
}

/// Column of the flesh cell for a hit: head (bit 0 of `flesh_hit_flags`) and fatal (bit 1).
pub fn flesh_column(head: bool, fatal: bool) -> u32 {
    SURFACE_TYPES as u32 + fx_iw4::flesh_effect_index(fx_iw4::flesh_hit_flags(head, fatal)) as u32
}

/// Copy a string out like the other name exports: no NUL; `out` null returns the full length.
unsafe fn put(s: &str, out: *mut u8, cap: u32) -> u32 {
    if out.is_null() {
        return s.len() as u32;
    }
    let n = s.len().min(cap as usize);
    unsafe { std::ptr::copy_nonoverlapping(s.as_ptr(), out, n) };
    n as u32
}

fn impact_cell(row: u32, surface: u32) -> Option<String> {
    let tables = IMPACT.read().ok()?;
    // MW2 has one table, "default".
    let table = tables.iter().find(|t| t.name == "default").or_else(|| tables.first())?;
    table.rows.get(row as usize)?.get(surface as usize)?.clone()
}

// ---- C ABI ----------------------------------------------------------------------------------

/// A weapon's effect name for `kind` (`FX_*`: 0 view flash, 1 world flash, 2 view shell eject,
/// 3 world shell eject, 4 view last-shot eject, 5 world last-shot eject, 6 explosion, 7 projectile
/// trail, 8 projectile beacon, 9 projectile ignition), as `mw2_fx_find` takes it. Returns the bytes written (no NUL), 0 if the weapon has none; `out` null returns
/// the length.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_fx(weapon: u32, kind: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(row) = weapons::row(weapon) else { return 0 };
        let name = match kind {
            0..=5 => row.effects.get(kind as usize).and_then(Option::as_deref),
            FX_EXPLOSION => row.equip.explosion_fx.as_deref(),
            FX_PROJECTILE_TRAIL => row.equip.trail_fx.as_deref(),
            FX_PROJECTILE_BEACON => row.equip.beacon_fx.as_deref(),
            FX_PROJECTILE_IGNITION => row.equip.ignition_fx.as_deref(),
            _ => None,
        };
        let Some(name) = name else { return 0 };
        unsafe { put(name, out, cap) }
    }))
    .unwrap_or(0)
}

/// A weapon's `impact_type` (1 small bullet, 2 large bullet, 3 armour-piercing bullet, 4 explosive
/// bullet, 5 shotgun, 6 shotgun explosive, 7 grenade bounce, 8 grenade explode, 9 rocket explode,
/// 10 projectile dud; 0 none). -1 = bad weapon.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_weapon_impact_type(weapon: u32) -> i32 {
    catch_unwind(|| weapons::row(weapon).map_or(-1, |r| r.facts.impact_type)).unwrap_or(-1)
}

/// One tracer streak (TracerDef). Lengths are IW4 inches, `speed` inches per second.
///
/// `colors[0]` is the end of the beam nearest the muzzle (trailing), `colors[4]` the leading end;
/// IW4 interpolates linearly between the five keys along the beam. RGBA, 0..1.
/// `draw_interval`: one tracer every N rounds fired (the Nth round shows the first one); 0 = never.
/// `material` is an FX material id for `mw2_tracer_texture` / `mw2_tracer_blend` (0 = none).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Mw2Tracer {
    pub draw_interval: i32,
    pub speed: f32,
    pub beam_length: f32,
    pub beam_width: f32,
    pub screw_radius: f32,
    pub screw_dist: f32,
    pub colors: [[f32; 4]; 5],
    pub material: u16,
    pub _pad: u16,
}

pub(crate) fn tracer_of(weapon: u32) -> Option<Mw2Tracer> {
    let name = weapons::row(weapon)?.tracer?;
    let tracers = TRACERS.read().ok()?;
    let t = tracers.iter().find(|t| t.name == name)?;
    Some(Mw2Tracer {
        draw_interval: t.draw_interval,
        speed: t.speed,
        beam_length: t.beam_length,
        beam_width: t.beam_width,
        screw_radius: t.screw_radius,
        screw_dist: t.screw_dist,
        colors: t.colors,
        material: t.material_id,
        _pad: 0,
    })
}

/// The weapon's tracer. Returns 1 and fills `out`, or 0 if it has none.
///
/// # Safety
/// `out` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_tracer(weapon: u32, out: *mut Mw2Tracer) -> i32 {
    if out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| match tracer_of(weapon) {
        Some(t) => {
            unsafe { *out = t };
            1
        }
        None => 0,
    }))
    .unwrap_or(0)
}

/// A tracer material's colour image as RGBA8, bottom row first (as `mw2_hud_image`); same as
/// `mw2_fx_texture`. Needs `mw2_fx_init`.
///
/// # Safety
/// `w` / `h` valid; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_tracer_texture(material: u16, w: *mut u32, h: *mut u32, out: *mut u8, cap: u32) -> u32 {
    unsafe { crate::fx::mw2_fx_texture(material, w, h, out, cap) }
}

/// A tracer material's blend mode (same codes as `mw2_fx_material`: 0 opaque, 1 alpha-test,
/// 2 blend, 3 additive, 4 multiply, 5 screen; -1 = bad id).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_tracer_blend(material: u16) -> i32 {
    unsafe { crate::fx::mw2_fx_material(material, std::ptr::null_mut(), 0) }
}

/// Row of the impact table for a weapon `impact_type` (`exit` != 0: the bullet's exit hole
/// after passing through a surface). -1 = this type has no row.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_impact_row(impact_type: u32, exit: i32) -> i32 {
    catch_unwind(|| fx_iw4::impact_table_row(impact_type as i32, exit != 0).map_or(-1, |r| r as i32)).unwrap_or(-1)
}

/// The effect a weapon `impact_type` (`mw2_weapon_impact_type`) makes on `surface` (0..=30 IW4
/// surface types per `mw2_surface_name`, 31..=34 flesh body / body fatal / head / head fatal):
/// the table row for that type (entry hit, not exit) then the cell. For the flesh surface type (7)
/// an empty cell falls back to the flesh body cell. Returns the bytes written (no NUL), 0 for none.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_impact_fx(impact_type: u32, surface: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(row) = fx_iw4::impact_table_row(impact_type as i32, false) else { return 0 };
        let mut cell = impact_cell(row as u32, surface);
        if cell.is_none() && surface == SURFACE_FLESH {
            cell = impact_cell(row as u32, flesh_column(false, false));
        }
        match cell {
            Some(name) => unsafe { put(&name, out, cap) },
            None => 0,
        }
    }))
    .unwrap_or(0)
}

/// As `mw2_impact_fx` but addressing the table by raw `row` (0..=14), with no flesh fallback.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_impact_fx_row(row: u32, surface: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match impact_cell(row, surface) {
        Some(name) => unsafe { put(&name, out, cap) },
        None => 0,
    }))
    .unwrap_or(0)
}

/// IW4 surface type name for a table column (0..=30: default, bark, brick, ... slush; 31..=34
/// flesh_body, flesh_body_fatal, flesh_head, flesh_head_fatal). Returns the bytes written, 0 past
/// the end.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_surface_name(surface: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match surface_name(surface) {
        Some(name) => unsafe { put(name, out, cap) },
        None => 0,
    }))
    .unwrap_or(0)
}

/// A bone of the built viewmodel as of the last `mw2_viewmodel_step` (rest pose if never
/// stepped): `out` gets x, y, z in the same space as the skinned viewmodel vertices (Unity,
/// metres, relative to the eye). `tag_flash` resolves to `tag_flash_silenced` on silencer
/// variants, else falls back through `tag_flash_silenced`, `tag_barrel`. Returns 1, or 0 if the bone doesn't exist.
///
/// # Safety
/// `vm` from `mw2_viewmodel_build`; `name` points to `len` UTF-8 bytes; `out` has room for 3 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_tag(vm: *const Viewmodel, name: *const u8, len: usize, out: *mut f32) -> i32 {
    unsafe { tag(vm, name, len, out, 3) }
}

/// As `mw2_viewmodel_tag` with the rotation too: `out` gets 7 floats (x, y, z, then the quaternion
/// x, y, z, w; the bone's pose rotation in the same mapping as `mw2_viewmodel_step`'s per-bone output).
///
/// # Safety
/// As `mw2_viewmodel_tag`, `out` has room for 7 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_viewmodel_tag_pose(vm: *const Viewmodel, name: *const u8, len: usize, out: *mut f32) -> i32 {
    unsafe { tag(vm, name, len, out, 7) }
}

unsafe fn tag(vm: *const Viewmodel, name: *const u8, len: usize, out: *mut f32, floats: usize) -> i32 {
    if vm.is_null() || name.is_null() || out.is_null() {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let v = unsafe { &*vm };
        let Ok(name) = std::str::from_utf8(unsafe { std::slice::from_raw_parts(name, len) }) else { return 0 };
        match v.tag(name) {
            Some(p) => {
                unsafe { std::ptr::copy_nonoverlapping(p.as_ptr(), out, floats) };
                1
            }
            None => 0,
        }
    }))
    .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fx::{TEST_LOCK, mw2_fx_find, mw2_fx_init};

    const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

    fn fx_found(name: &str) -> bool {
        unsafe { mw2_fx_find(name.as_ptr(), name.len()) != 0 }
    }

    fn string(f: impl Fn(*mut u8, u32) -> u32) -> Option<String> {
        let mut buf = [0u8; 160];
        let n = f(buf.as_mut_ptr(), 160) as usize;
        (n > 0).then(|| String::from_utf8_lossy(&buf[..n]).into_owned())
    }

    #[test]
    fn surface_names_and_flesh_columns() {
        assert_eq!(surface_name(0), Some("default"));
        assert_eq!(surface_name(7), Some("flesh"));
        assert_eq!(surface_name(30), Some("slush"));
        assert_eq!(surface_name(31), Some("flesh_body"));
        assert_eq!(surface_name(34), Some("flesh_head_fatal"));
        assert_eq!(surface_name(35), None);
        assert_eq!([flesh_column(false, false), flesh_column(false, true), flesh_column(true, false), flesh_column(true, true)], [31, 32, 33, 34]);
        assert_eq!(size_of::<Mw2Tracer>(), 108);
    }

    /// Real-install weapon effect audit (run: cargo test --release -p mw2sim weapon_fx_real -- --ignored --nocapture).
    #[test]
    #[ignore]
    fn weapon_fx_real_install() {
        let _turn = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        assert!(unsafe { crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) } > 1000);
        let effects = mw2_fx_init();
        assert!(effects > 100);
        let tracers = TRACERS.read().unwrap().clone();
        let tables = IMPACT.read().unwrap().clone();
        eprintln!("{effects} effects, {} tracer defs, {} impact tables {:?}", tracers.len(), tables.len(), tables.iter().map(|t| (&t.name, t.rows.len())).collect::<Vec<_>>());
        for t in &tracers {
            eprintln!("  tracer {:24} mat {:?} id {} every {} speed {} len {} width {} screw r{} d{}", t.name, t.material, t.material_id, t.draw_interval, t.speed, t.beam_length, t.beam_width, t.screw_radius, t.screw_dist);
        }

        // Whole-table coverage: every weapon's names must be real effects.
        let table = weapons::TABLE.read().unwrap().clone();
        let (mut with_flash, mut with_eject, mut with_tracer, mut missing) = (0, 0, 0, Vec::new());
        for r in &table {
            for (k, n) in r.effects.iter().enumerate() {
                if let Some(n) = n
                    && !fx_found(n)
                {
                    missing.push(format!("{} kind {k}: {n}", r.name));
                }
            }
            with_flash += usize::from(r.effects[0].is_some());
            with_eject += usize::from(r.effects[2].is_some());
            with_tracer += usize::from(r.tracer.is_some());
        }
        eprintln!("{} weapons: {with_flash} view flash, {with_eject} view eject, {with_tracer} tracer; names not found by mw2_fx_find: {missing:?}", table.len());
        assert!(missing.is_empty(), "{missing:?}");

        let kinds = ["view flash", "world flash", "view eject", "world eject", "view last eject", "world last eject"];
        for w in ["ak47_mp", "m4_mp", "rpd_mp", "spas12_mp", "cheytac_mp", "deserteagle_mp"] {
            let idx = unsafe { crate::mw2_weapon_index(w.as_ptr(), w.len()) };
            assert!(idx != 0, "{w}");
            eprintln!("{w} (impact_type {}):", mw2_weapon_impact_type(idx));
            for (k, label) in kinds.iter().enumerate() {
                let name = string(|o, c| unsafe { mw2_weapon_fx(idx, k as u32, o, c) });
                eprintln!("    {label:16} {name:?} -> fx id {}", name.as_deref().map_or(0, |n| unsafe { mw2_fx_find(n.as_ptr(), n.len()) }));
            }
            let mut t = Mw2Tracer::default();
            if unsafe { mw2_weapon_tracer(idx, &mut t) } == 1 {
                let blend = mw2_tracer_blend(t.material);
                let (mut tw, mut th) = (0u32, 0u32);
                let bytes = unsafe { mw2_tracer_texture(t.material, &mut tw, &mut th, std::ptr::null_mut(), 0) };
                eprintln!("    tracer {t:?} blend {blend} texture {tw}x{th} ({bytes} bytes)");
                assert!(bytes > 0 && bytes == tw * th * 4, "{w} tracer texture");
            } else {
                eprintln!("    tracer none");
            }
        }

        // Impact table.
        let table = tables.iter().find(|t| t.name == "default").unwrap_or(&tables[0]);
        eprintln!("surface names: {:?}", (0..40).map_while(surface_name).collect::<Vec<_>>());
        let type_names = ["none", "bullet_small", "bullet_large", "bullet_ap", "bullet_explode", "shotgun", "shotgun_explode", "grenade_bounce", "grenade_explode", "rocket_explode", "projectile_dud"];
        for row in 0..table.rows.len() {
            let owners: Vec<String> = (1..=10u32)
                .flat_map(|t| [false, true].map(|exit| (t, exit)))
                .filter(|&(t, exit)| mw2_impact_row(t, i32::from(exit)) == row as i32)
                .map(|(t, exit)| format!("{}{}", type_names[t as usize], if exit { " (exit)" } else { "" }))
                .collect();
            let filled = table.rows[row].iter().filter(|c| c.is_some()).count();
            eprintln!("row {row:2} = {owners:?}: {filled}/{SURFACE_COLUMNS} cells filled");
        }
        let probe = ["dirt", "concrete", "metal", "flesh", "water", "wood"];
        for t in 1..=10u32 {
            let row = mw2_impact_row(t, 0);
            if row < 0 {
                continue;
            }
            let mut line = format!("type {t:2} {:16} row {row:2}:", type_names[t as usize]);
            for s in probe {
                let col = (0..SURFACE_TYPES as u32).find(|&c| surface_name(c) == Some(s)).unwrap();
                let name = string(|o, c| unsafe { mw2_impact_fx(t, col, o, c) });
                if let Some(n) = &name {
                    assert!(fx_found(n), "{n} not found");
                }
                line += &format!(" {s}={}", name.as_deref().unwrap_or("-"));
            }
            eprintln!("{line}");
        }
        // Raw flesh cells and the nonflesh[7] cell for the small-bullet row.
        let row = mw2_impact_row(1, 0) as u32;
        for col in [SURFACE_FLESH, 31, 32, 33, 34] {
            let name = string(|o, c| unsafe { mw2_impact_fx_row(row, col, o, c) });
            eprintln!("row {row} col {col} ({}) = {name:?}", surface_name(col).unwrap());
        }

        // Muzzle tag.
        for w in ["ak47_mp", "m4_mp", "ak47_silencer_mp", "m4_silencer_mp", "spas12_mp", "deserteagle_mp"] {
            let idx = unsafe { crate::mw2_weapon_index(w.as_ptr(), w.len()) };
            if idx == 0 {
                eprintln!("{w}: not a weapon");
                continue;
            }
            let row = weapons::row(idx).unwrap();
            eprintln!("{w}: hide_tags {:?} view flash {:?} gun {:?}", row.hide_tags, row.effects[0], row.gun_model);
            let vm = crate::mw2_viewmodel_build(idx);
            assert!(!vm.is_null(), "{w} viewmodel");
            for tag in ["tag_flash", "tag_brass", "tag_flash_silenced", "tag_barrel", "tag_view", "nonexistent"] {
                let mut p = [0f32; 7];
                let ok = unsafe { mw2_viewmodel_tag_pose(vm, tag.as_ptr(), tag.len(), p.as_mut_ptr()) };
                let mut xyz = [0f32; 3];
                let ok3 = unsafe { mw2_viewmodel_tag(vm, tag.as_ptr(), tag.len(), xyz.as_mut_ptr()) };
                assert_eq!(ok, ok3);
                assert!(ok == 0 || xyz == [p[0], p[1], p[2]]);
                eprintln!("{w:18} {tag:20} ok {ok} pos (Unity m, eye-relative) {:.4?}", &p[..3]);
            }
            unsafe { crate::mw2_viewmodel_destroy(vm) };
        }
    }
}
