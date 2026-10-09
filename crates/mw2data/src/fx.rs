//! MW2 (IW4) particle effects (FxEffectDef) captured off the zone walk.
//!
//! Lean port of IW4L's `asset_game::fx_catalog` capture (`capture`, `capture_elem`,
//! `capture_visuals`, `capture_trail_def`, `parse_vel_graph_channel`) without its asset-graph
//! layer: child effects, runner effects and sound aliases are kept as names, materials as an
//! index into [`FxCapture::materials`] (name, colour image, blend state bits).

use std::collections::HashMap;

use fastfile_iw4::{FxEffectDefGeometry, Iw4WireFormat, Ptr, ZonePtr, ZoneStream};
use fx_iw4::{FxEffectDefView, FxElemDefView, FxElemVec3Range, FxTrailVertex};

/// IW4 elem types (FxElemType).
pub mod elem_type {
    pub const BILLBOARD: u8 = 0;
    pub const ORIENTED: u8 = 1;
    pub const TAIL: u8 = 2;
    pub const TRAIL: u8 = 3;
    pub const CLOUD: u8 = 4;
    pub const SPARK_CLOUD: u8 = 5;
    pub const SPARK_FOUNTAIN: u8 = 6;
    pub const MODEL: u8 = 7;
    pub const OMNI_LIGHT: u8 = 8;
    pub const SPOT_LIGHT: u8 = 9;
    pub const SOUND: u8 = 10;
    pub const DECAL: u8 = 11;
    pub const RUNNER: u8 = 12;

    pub const fn is_sprite(t: u8) -> bool {
        matches!(t, 0..=6)
    }

    pub fn name(t: u8) -> &'static str {
        match t {
            BILLBOARD => "billboard",
            ORIENTED => "oriented",
            TAIL => "tail",
            TRAIL => "trail",
            CLOUD => "cloud",
            SPARK_CLOUD => "spark_cloud",
            SPARK_FOUNTAIN => "spark_fountain",
            MODEL => "model",
            OMNI_LIGHT => "omni_light",
            SPOT_LIGHT => "spot_light",
            SOUND => "sound",
            DECAL => "decal",
            RUNNER => "runner",
            _ => "unknown",
        }
    }
}

const FX_ELEM_DEF_X86: usize = 0xfc;
const FX_ELEM_DEF_X64: usize = 288;
const VEL_SAMPLE: usize = 96;
const VIS_SAMPLE: usize = 48;
const ELEM_VISUALS_X86: usize = 4;
const TRAIL_DEF_X86: usize = 0x24;
const TRAIL_VERTEX: usize = 20;
const ATLAS_OFF: usize = 0xa8;

/// What one visual slot of an elem draws or plays.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FxVisual {
    /// Index into [`FxCapture::materials`].
    Material(u16),
    /// A sprite visual whose material could not be resolved.
    MissingMaterial,
    Model(String),
    Runner(String),
    Sound(String),
    /// Decal (impact mark) materials; not drawn by this port.
    Mark,
    None,
}

#[derive(Clone, Debug, PartialEq)]
pub struct FxTrail {
    pub scroll_time_msec: i32,
    pub repeat_dist: i32,
    pub inv_split_dist: f32,
    pub inv_split_arc_dist: f32,
    pub inv_split_time: f32,
    pub verts: Vec<FxTrailVertex>,
    pub inds: Vec<u16>,
}

#[derive(Clone, Debug)]
pub struct FxElem {
    pub view: FxElemDefView,
    /// FxElemAtlas bytes (behavior, index, fps, loopCount, colIndexBits, rowIndexBits, entryCount).
    pub atlas: [u8; 8],
    pub vel_local: Vec<FxElemVec3Range>,
    pub vel_world: Vec<FxElemVec3Range>,
    /// Raw FxElemVisStateSample rows (48 bytes each, vis_state_interval_count + 1 of them).
    pub vis_samples: Vec<u8>,
    pub visuals: Vec<FxVisual>,
    pub on_impact: Option<String>,
    pub on_death: Option<String>,
    pub emitted: Option<String>,
    pub trail: Option<FxTrail>,
}

#[derive(Clone, Debug)]
pub struct FxEffect {
    pub name: String,
    pub view: FxEffectDefView,
    pub elems: Vec<FxElem>,
}

#[derive(Clone, Debug, Default)]
pub struct FxMaterial {
    pub name: String,
    /// TS_COLOR_MAP image name (else the first image), for `images/<name>.iwi`.
    pub image: Option<String>,
    /// GfxStateBits loadBits of the material's emissive (else unlit / first colour) technique.
    pub state_bits: Option<[u32; 2]>,
    /// Material camera region (2 = emissive, the pass IW4 draws effects in).
    pub camera_region: u8,
}

/// One TracerDef (bullet tracer streak). Distances are IW4 inches (speed in inches per second).
#[derive(Clone, Debug, Default)]
pub struct TracerDef {
    pub name: String,
    /// Material name, if the slot resolved.
    pub material: Option<String>,
    /// 1-based id into [`FxCapture::materials`] (name, colour image, blend), 0 = none.
    pub material_id: u16,
    pub draw_interval: i32,
    pub speed: f32,
    pub beam_length: f32,
    pub beam_width: f32,
    pub screw_radius: f32,
    pub screw_dist: f32,
    /// Five RGBA colour keys (0..1), start of the streak's life to the end.
    pub colors: [[f32; 4]; 5],
}

/// Rows in an FxImpactTable, and the columns of each row: 31 surface types, then 4 flesh cells.
pub const IMPACT_ROWS: usize = 15;
pub const IMPACT_SURFACES: usize = 31;
pub const IMPACT_FLESH: usize = 4;
pub const IMPACT_COLUMNS: usize = IMPACT_SURFACES + IMPACT_FLESH;

/// An FxImpactTable: `rows[row][column]` is an effect name (None = no effect).
#[derive(Clone, Debug, Default)]
pub struct ImpactTable {
    pub name: String,
    pub rows: Vec<Vec<Option<String>>>,
}

#[derive(Clone, Debug, Default)]
pub struct FxCapture {
    pub effects: Vec<FxEffect>,
    pub materials: Vec<FxMaterial>,
    material_ids: HashMap<String, u16>,
    /// Effects skipped because their bytes did not decode.
    pub failed: usize,
}

/// Material facts the walk resolved for a zone slot: (name, colour image, state bits).
pub type MaterialLookup<'a> = &'a dyn Fn(Ptr) -> Option<FxMaterial>;

fn read_name_field(s: &ZoneStream<'_>, p: Ptr, field: usize) -> Option<String> {
    match s.ptr_at(p, field) {
        Ok(ZonePtr::Offset(name)) => s.cstr(s.resolve_alias(name)).ok().filter(|n| !n.is_empty()).map(str::to_owned),
        _ => None,
    }
}

fn copy_ptr_array(s: &ZoneStream<'_>, parent: Ptr, field: usize, count: usize, stride: usize) -> Vec<u8> {
    if count == 0 {
        return Vec::new();
    }
    let Ok(ZonePtr::Offset(arr)) = s.ptr_at(parent, field) else {
        return Vec::new();
    };
    s.slice_at(s.resolve_alias(arr), 0, count.saturating_mul(stride)).map(<[u8]>::to_vec).unwrap_or_default()
}

/// FxElemVelStateSample: local {velocity, totalDelta} then world {velocity, totalDelta}.
fn parse_vel_graph_channel(bytes: &[u8], fenceposts: usize, world: bool) -> Vec<FxElemVec3Range> {
    let base = if world { 0x30 } else { 0 };
    let mut out = Vec::with_capacity(fenceposts);
    for i in 0..fenceposts {
        let off = i * VEL_SAMPLE + base;
        let Some(range) = bytes.get(off..off + 24) else { break };
        let read = |at: usize| f32::from_le_bytes([range[at], range[at + 1], range[at + 2], range[at + 3]]);
        out.push(FxElemVec3Range {
            base: std::array::from_fn(|axis| read(axis * 4)),
            amplitude: std::array::from_fn(|axis| read(12 + axis * 4)),
        });
    }
    out
}

impl FxCapture {
    /// Fill in materials this zone only saw as a reference to another zone's asset (a name
    /// starting with ',', no image: the tracer streak material `,gfx_tracer` lives in
    /// `code_post_gfx_mp.ff`). `find` looks the bare name up in that zone. Returns how many resolved.
    pub fn resolve_referenced(&mut self, find: impl Fn(&str) -> Option<FxMaterial>) -> usize {
        let mut n = 0;
        for m in self.materials.iter_mut().filter(|m| m.name.starts_with(',') && m.image.is_none()) {
            if let Some(real) = find(m.name.trim_start_matches(',')) {
                *m = real;
                n += 1;
            }
        }
        n
    }

    /// Index of this material in [`FxCapture::materials`], adding it if new (`u16::MAX` = table full).
    pub fn intern_material(&mut self, m: FxMaterial) -> u16 {
        self.material_id(m)
    }

    fn material_id(&mut self, m: FxMaterial) -> u16 {
        if let Some(&id) = self.material_ids.get(&m.name) {
            return id;
        }
        if self.materials.len() >= u16::MAX as usize {
            return u16::MAX;
        }
        let id = self.materials.len() as u16;
        self.material_ids.insert(m.name.clone(), id);
        self.materials.push(m);
        id
    }

    fn material_visual(&mut self, s: &ZoneStream<'_>, slot: Ptr, materials: MaterialLookup<'_>) -> FxVisual {
        let alias = match s.ptr_at(slot, 0) {
            Ok(ZonePtr::Offset(target)) => Some(s.resolve_alias(target)),
            _ => None,
        };
        match materials(slot).or_else(|| alias.and_then(materials)).filter(|m| !m.name.is_empty()) {
            Some(m) => FxVisual::Material(self.material_id(m)),
            None => FxVisual::MissingMaterial,
        }
    }

    /// `capture_fx` hook body. Never fails the walk: undecodable effects are counted and skipped.
    pub fn capture(&mut self, s: &ZoneStream<'_>, g: FxEffectDefGeometry, materials: MaterialLookup<'_>) {
        let Some(name) = g.name.and_then(|p| s.cstr(p).ok()).filter(|n| !n.is_empty()).map(str::to_owned) else {
            self.failed += 1;
            return;
        };
        let x64 = s.wire_format() == Iw4WireFormat::X64;
        let view = if x64 {
            (|| {
                Some(FxEffectDefView {
                    flags: s.i32_at(g.header, 8).ok()?,
                    msec_looping_life: s.i32_at(g.header, 16).ok()?,
                    looping_count: g.looping_count,
                    one_shot_count: g.one_shot_count,
                    emission_count: g.emission_count,
                })
            })()
        } else {
            s.slice_at(g.header, 0, fx_iw4::FX_EFFECT_DEF_SIZE).ok().and_then(fx_iw4::effect_def_view)
        };
        let Some(view) = view.filter(|v| {
            v.looping_count == g.looping_count && v.one_shot_count == g.one_shot_count && v.emission_count == g.emission_count
        }) else {
            self.failed += 1;
            return;
        };
        let mut elems = Vec::with_capacity(g.elem_def_count);
        if let Some(arr) = g.elem_defs {
            let stride = s.layout(FX_ELEM_DEF_X86, FX_ELEM_DEF_X64);
            for i in 0..g.elem_def_count {
                match self.capture_elem(s, arr.at(i * stride), materials) {
                    Some(e) => elems.push(e),
                    None => {
                        self.failed += 1;
                        return;
                    }
                }
            }
        } else if g.elem_def_count > 0 {
            self.failed += 1;
            return;
        }
        self.effects.push(FxEffect { name, view, elems });
    }

    fn capture_elem(&mut self, s: &ZoneStream<'_>, p: Ptr, materials: MaterialLookup<'_>) -> Option<FxElem> {
        let raw = s.slice_at(p, 0, s.layout(FX_ELEM_DEF_X86, FX_ELEM_DEF_X64)).ok()?.to_vec();
        let view = if s.wire_format() == Iw4WireFormat::X64 { fx_iw4::elem_def_view_x64(&raw)? } else { fx_iw4::elem_def_view(&raw)? };
        let mut atlas = [0u8; 8];
        atlas.copy_from_slice(raw.get(ATLAS_OFF..ATLAS_OFF + 8)?);
        let vel_count = view.vel_interval_count as usize + 1;
        let vis_count = view.vis_state_interval_count as usize + 1;
        let vel_samples = copy_ptr_array(s, p, s.layout(0xb4, 184), vel_count, VEL_SAMPLE);
        let vis_samples = copy_ptr_array(s, p, s.layout(0xb8, 192), vis_count, VIS_SAMPLE);
        let visuals = self.capture_visuals(s, p, &view, materials);
        let trail = if view.elem_type == elem_type::TRAIL { capture_trail(s, p) } else { None };
        Some(FxElem {
            view,
            atlas,
            vel_local: parse_vel_graph_channel(&vel_samples, vel_count, false),
            vel_world: parse_vel_graph_channel(&vel_samples, vel_count, true),
            vis_samples,
            visuals,
            on_impact: read_name_field(s, p, s.layout(216, 232)),
            on_death: read_name_field(s, p, s.layout(220, 240)),
            emitted: read_name_field(s, p, s.layout(224, 248)),
            trail,
        })
    }

    fn capture_visuals(&mut self, s: &ZoneStream<'_>, p: Ptr, view: &FxElemDefView, materials: MaterialLookup<'_>) -> Vec<FxVisual> {
        let t = view.elem_type;
        let count = view.visual_count as usize;
        let vis = p.at(s.layout(0xbc, 200));
        if matches!(t, elem_type::OMNI_LIGHT | elem_type::SPOT_LIGHT) || count == 0 {
            return vec![FxVisual::None];
        }
        if t == elem_type::DECAL {
            return vec![FxVisual::Mark; count];
        }
        // One visual is stored inline; several hang off a pointer to an FxElemVisuals array.
        let slots: Vec<Ptr> = if count > 1 {
            match s.ptr_at(vis, 0) {
                Ok(ZonePtr::Offset(arr)) => {
                    let arr = s.resolve_alias(arr);
                    let stride = s.layout(ELEM_VISUALS_X86, 8);
                    (0..count).map(|i| arr.at(i * stride)).collect()
                }
                _ => {
                    return vec![if elem_type::is_sprite(t) { FxVisual::MissingMaterial } else { FxVisual::None }; count];
                }
            }
        } else {
            vec![vis]
        };
        slots
            .into_iter()
            .map(|slot| {
                if elem_type::is_sprite(t) {
                    self.material_visual(s, slot, materials)
                } else if t == elem_type::MODEL {
                    model_name(s, slot).map_or(FxVisual::None, FxVisual::Model)
                } else if t == elem_type::RUNNER {
                    read_name_field(s, slot, 0).map_or(FxVisual::None, FxVisual::Runner)
                } else if t == elem_type::SOUND {
                    read_name_field(s, slot, 0).map_or(FxVisual::None, FxVisual::Sound)
                } else {
                    FxVisual::None
                }
            })
            .collect()
    }
}

fn model_name(s: &ZoneStream<'_>, slot: Ptr) -> Option<String> {
    let ZonePtr::Offset(body) = s.ptr_at(slot, 0).ok()? else { return None };
    let body = s.resolve_alias(body);
    match s.ptr_at(body, 0).ok()? {
        ZonePtr::Offset(name) => s.cstr(s.resolve_alias(name)).ok().filter(|n| !n.is_empty()).map(str::to_owned),
        _ => None,
    }
}

fn capture_trail(s: &ZoneStream<'_>, elem: Ptr) -> Option<FxTrail> {
    let ZonePtr::Offset(trail) = s.ptr_at(elem, s.layout(0xf4, 272)).ok()? else { return None };
    let trail = s.resolve_alias(trail);
    let header = s.slice_at(trail, 0, s.layout(TRAIL_DEF_X86, 48)).ok()?;
    let i32_at = |o: usize| i32::from_le_bytes([header[o], header[o + 1], header[o + 2], header[o + 3]]);
    let f32_at = |o: usize| f32::from_le_bytes([header[o], header[o + 1], header[o + 2], header[o + 3]]);
    let vert_count = i32_at(0x14).max(0) as usize;
    let ind_count = s.i32_at(trail, s.layout(0x1c, 32)).ok()?.max(0) as usize;
    let vert_bytes = copy_ptr_array(s, trail, 0x18, vert_count, TRAIL_VERTEX);
    let verts = vert_bytes
        .chunks_exact(TRAIL_VERTEX)
        .map(|c| {
            let f = |o: usize| f32::from_le_bytes([c[o], c[o + 1], c[o + 2], c[o + 3]]);
            FxTrailVertex { pos: [f(0), f(4)], normal: [f(8), f(12)], tex_coord: f(16) }
        })
        .collect();
    let inds = copy_ptr_array(s, trail, s.layout(0x20, 40), ind_count, 2).chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
    Some(FxTrail {
        scroll_time_msec: i32_at(0),
        repeat_dist: i32_at(4),
        inv_split_dist: f32_at(8),
        inv_split_arc_dist: f32_at(0xc),
        inv_split_time: f32_at(0x10),
        verts,
        inds,
    })
}

#[cfg(test)]
mod tests {
    /// Run: cargo test --release -p mw2data fx_dump -- --ignored --nocapture
    #[test]
    #[ignore]
    fn fx_dump() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        let (_, report, _, models) = crate::walk_file_with_models(std::path::Path::new(COMMON_MP), Some(|_| false)).expect("walk");
        let fx = &models.fx;
        eprintln!("walk {}/{} stopped {:?}; {} effects, {} failed, {} materials", report.assets_walked, report.assets_total, report.stopped, fx.effects.len(), fx.failed, fx.materials.len());
        let wanted = ["explosions/clusterbomb", "explosions/stealth_bomb_mp", "fire/jet_afterburner", "smoke/jet_contrail", "smoke/smoke_trail_white_heli_emitter", "smoke/smoke_trail_white_heli", "fire/fire_smoke_trail_l_emitter", "explosions/clusterbomb_exp"];
        for e in &fx.effects {
            let show = wanted.contains(&e.name.as_str()) || e.name.contains("smoke_trail") || e.name.contains("rocket");
            if !show {
                continue;
            }
            eprintln!("{} loop_life {} loop {} oneshot {} emit {}", e.name, e.view.msec_looping_life, e.view.looping_count, e.view.one_shot_count, e.view.emission_count);
            if !wanted.contains(&e.name.as_str()) {
                continue;
            }
            for (i, el) in e.elems.iter().enumerate() {
                let v = &el.view;
                let vis: Vec<String> = el.visuals.iter().map(|x| match x {
                    super::FxVisual::Material(m) => {
                        let m = &fx.materials[*m as usize];
                        format!("{}[{:?}]{:x?} r{}", m.name, m.image, m.state_bits, m.camera_region)
                    }
                    other => format!("{other:?}"),
                }).collect();
                eprintln!("  [{i}] {} flags {:#x} spawn {}/{} delay {}+{} life {}+{} sort {} vis_int {} vel_int {} atlas {:?} children {:?}/{:?}/{:?} emit {:?} trail {} vis {:?}",
                    super::elem_type::name(v.elem_type), v.flags, v.spawn_a, v.spawn_b, v.spawn_delay_msec_base, v.spawn_delay_msec_amplitude,
                    v.life_span_msec_base, v.life_span_msec_amplitude, v.sort_order, v.vis_state_interval_count, v.vel_interval_count, el.atlas,
                    el.on_impact, el.on_death, el.emitted, v.emit_dist, el.trail.is_some(), vis);
            }
        }
    }
}
