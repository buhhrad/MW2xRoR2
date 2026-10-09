//! MW2 gun models for the host: meshes captured during the weapon walk, textures read
//! from `main/*.iwd` (`images/<name>.iwi`, top mip) on demand.

use std::path::PathBuf;
use std::sync::{OnceLock, RwLock};

use mw2data::models::{Iwi, Mesh};

pub static MESHES: RwLock<Vec<Mesh>> = RwLock::new(Vec::new());
static IMAGES: OnceLock<Option<mw2data::iwd::SoundIndex>> = OnceLock::new();
static MAIN_DIR: OnceLock<PathBuf> = OnceLock::new();

/// Remember where main/ is and index its images/ entries in the background
/// (scanning every iwd takes seconds; a texture request just waits for it to finish).
pub fn set_main_dir(dir: PathBuf) {
    if MAIN_DIR.set(dir).is_ok() {
        let _ = std::thread::Builder::new().name("mw2-iwd-images".into()).spawn(|| {
            let _ = images();
        });
    }
}

/// Materials' textures beyond the colour map (semantic, IW4 name hash, image), by material name.
pub static MATERIAL_TEXTURES: RwLock<Option<std::collections::HashMap<String, Vec<(u8, u32, String)>>>> = RwLock::new(None);

pub fn material_textures(material: &str) -> Vec<(u8, u32, String)> {
    MATERIAL_TEXTURES.read().ok().and_then(|m| m.as_ref().and_then(|m| m.get(material).cloned())).unwrap_or_default()
}

/// The colour map some loaded gun model gives `material` (a character's holstered pistol names its
/// material with a leading comma and carries no image of its own: `,mc/mtl_weapon_beretta`).
pub fn color_map_of_material(material: &str) -> Option<String> {
    let want = material.trim_start_matches(',');
    let meshes = MESHES.read().ok()?;
    meshes.iter().flat_map(|m| m.surfaces.iter()).find(|s| s.material.as_deref().map(|x| x.trim_start_matches(',')) == Some(want) && s.color_map.is_some()).and_then(|s| s.color_map.clone())
}

pub fn index_of(name: &str) -> u32 {
    MESHES.read().ok().and_then(|m| m.iter().position(|x| x.name == name)).map_or(0, |i| i as u32 + 1)
}

pub fn with<R>(id: u32, f: impl FnOnce(&Mesh) -> R) -> Option<R> {
    if id == 0 {
        return None;
    }
    MESHES.read().ok()?.get(id as usize - 1).map(f)
}

fn images() -> Option<&'static mw2data::iwd::SoundIndex> {
    IMAGES
        .get_or_init(|| MAIN_DIR.get().and_then(|d| mw2data::iwd::SoundIndex::open_prefix(d, "images/").ok()))
        .as_ref()
}

/// MW2 multiplayer gun models carry every attachment; the game hides the parts that
/// aren't equipped (by bone hide-tags). Attachments use their own materials, so this
/// decides per surface from the material name and the attachments in the weapon name
/// (`ak47_acog_silencer_mp`).
pub fn surface_visible(weapon_name: &str, material: Option<&str>) -> bool {
    let Some(material) = material else { return true };
    let m = material.to_ascii_lowercase();
    let equipped: Vec<&str> = weapon_name.trim_end_matches("_mp").split('_').skip(1).collect();
    let has = |a: &str| equipped.contains(&a);
    let optic = has("acog") || has("reflex") || has("eotech") || has("thermal");
    const GROUPS: &[(&[&str], &[&str])] = &[
        (&["acog"], &["acog"]),
        (&["reflex"], &["reflex", "red_dot", "reddot"]),
        (&["eotech"], &["eotech", "holo"]),
        (&["thermal"], &["thermal"]),
        (&["silencer"], &["suppressor", "silencer"]),
        (&["shotgun"], &["masterkey", "shotgun_attach"]),
        (&["gl"], &["m203", "gp25", "grenade_launcher", "_gl_", "gl_"]),
        (&["heartbeat"], &["motion_tracker", "heartbeat"]),
        (&["grip"], &["foregrip", "grip"]),
    ];
    for (attachments, keywords) in GROUPS {
        if keywords.iter().any(|k| m.contains(k)) {
            return attachments.iter().any(|a| has(a));
        }
    }
    // Rail mounts only come with an optic.
    if m.contains("ris_mount") || m.contains("rail") {
        return optic;
    }
    true
}

/// Decoded top mips, kept once read: reading one from the iwd zips costs ~20-40 ms, and a
/// viewmodel has ~15 of them (a weapon switch used to stall ~0.4 s, twice over).
static TEXTURES: RwLock<Option<std::collections::HashMap<String, Option<std::sync::Arc<Iwi>>>>> = RwLock::new(None);

/// The whole `images/<name>.iwi` file (wavelet-compressed images need more than the top mip).
pub fn iwi_bytes(name: &str) -> Option<Vec<u8>> {
    images()?.read("", &format!("{name}.iwi"))
}

pub fn texture(name: &str) -> Option<std::sync::Arc<Iwi>> {
    if let Some(hit) = TEXTURES.read().ok().and_then(|c| c.as_ref().and_then(|m| m.get(name).cloned())) {
        return hit;
    }
    let iwi = images()
        .and_then(|i| i.read("", &format!("{name}.iwi")))
        .and_then(|bytes| mw2data::models::parse_iwi(&bytes))
        .map(std::sync::Arc::new);
    if let Ok(mut c) = TEXTURES.write() {
        c.get_or_insert_with(Default::default).insert(name.to_owned(), iwi.clone());
    }
    iwi
}

/// Read textures into the cache on a background thread (MW2 mode start), so the game thread
/// never waits on the archives.
pub fn prefetch(names: Vec<String>) {
    if names.is_empty() {
        return;
    }
    let _ = std::thread::Builder::new().name("mw2-prefetch".into()).spawn(move || {
        for n in names {
            let _ = texture(&n);
        }
    });
}

/// The `main/` directory `weapons::load` registered (the MW2 install's iwd folder); the zone
/// folder sits beside it (`<root>/zone/<language>`).
pub fn main_dir() -> Option<PathBuf> {
    MAIN_DIR.get().cloned()
}

static ZONE_DIR: OnceLock<PathBuf> = OnceLock::new();

/// The zone folder common_mp.ff was loaded from. Its language folder is the install's own
/// (`zone/english`, `zone/german`, ...), so the character zones are read from the same place.
pub fn set_zone_dir(dir: PathBuf) {
    let _ = ZONE_DIR.set(dir);
}

pub fn zone_dir() -> Option<PathBuf> {
    ZONE_DIR.get().cloned()
}

/// MW2 blend mode (`fx::blend_mode`: 0 opaque, 2 blend, 3 additive...) of a material by name, from
/// any captured static model surface that uses it (rig surfaces keep the name, not the state).
pub fn material_blend(material: &str) -> i32 {
    let Ok(meshes) = MESHES.read() else { return 0 };
    meshes
        .iter()
        .flat_map(|m| m.surfaces.iter())
        .find(|s| s.material.as_deref() == Some(material) && s.state_bits.is_some())
        .map_or(0, |s| crate::fx::blend_mode(s.state_bits))
}
