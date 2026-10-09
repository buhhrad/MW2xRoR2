//! MW2 XModel capture: LOD0 mesh (positions, normals, UVs, triangles) per surface,
//! with each surface's colorMap image name. Port of the static-mesh part of IW4L's
//! `asset_model::model_skel::capture_model_skel_iw4`, plus the material/image link
//! tracking from `asset_material::material_catalog` (assets are reached through
//! pointer slots that get loaded inline or aliased to an earlier copy).
//!
//! Coordinates are IW4 model space: inches, Z up, X forward.

use std::collections::HashMap;

use fastfile_iw4::{AssetType, Iw4WireFormat, Ptr, ScriptStrings, ZonePtr, ZoneStream};

use crate::rig::{RigCapture, RigModel, RigSurface};

const XSURFACE: (usize, usize) = (64, 88);
const GFX_PACKED_VERTEX: usize = 32;
const TS_COLOR_MAP: u8 = 2;

#[derive(Clone, Copy, Debug)]
enum Link {
    Direct(usize),
    Alias(Ptr),
}

fn bind(map: &mut HashMap<Ptr, Link>, slot: Ptr, link: Link) {
    map.insert(slot, link);
}

fn resolve(map: &HashMap<Ptr, Link>, mut slot: Ptr) -> Option<usize> {
    for _ in 0..32 {
        match *map.get(&slot)? {
            Link::Direct(i) => return Some(i),
            Link::Alias(t) => slot = t,
        }
    }
    None
}

#[derive(Clone, Debug)]
pub struct Surface {
    pub index_start: u32,
    pub index_count: u32,
    pub material: Option<String>,
    /// colorMap image name (look up `images/<name>.iwi` in the iwd archives).
    pub color_map: Option<String>,
    /// The material's blend state (GfxStateBits loadBits), e.g. a helicopter's rotor-blur disc.
    pub state_bits: Option<[u32; 2]>,
}

#[derive(Clone, Debug, Default)]
pub struct Mesh {
    pub name: String,
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub indices: Vec<u32>,
    pub surfaces: Vec<Surface>,
}

struct MaterialRec {
    name: String,
    color_image: Option<usize>,
    /// Every texture: (semantic, IW4 name hash, image) - camo materials keep the gun's colour map
    /// and add their pattern in other samplers.
    textures: Vec<(u8, u32, Option<usize>)>,
    /// GfxStateBits loadBits of the emissive (else unlit / first colour) technique.
    state_bits: Option<[u32; 2]>,
    camera_region: u8,
}

/// IW4 technique slots tried for a material's blend state: emissive, unlit, then any colour slot.
fn pick_state_bits(s: &ZoneStream<'_>, g: &fastfile_iw4::MaterialGeometry) -> Option<[u32; 2]> {
    let table = g.state_bits?;
    let entries = g.state_bits_entry?;
    let read = |i: usize| Some([s.u32_at(table, i * 8).ok()?, s.u32_at(table, i * 8 + 4).ok()?]);
    let valid = |e: u8| (e as usize) < g.state_bits_count;
    [5usize, 4].into_iter().chain(6..entries.len()).map(|slot| entries[slot]).find(|&e| valid(e)).and_then(|e| read(e as usize))
        .or_else(|| (g.state_bits_count == 1).then(|| read(0)).flatten())
}

#[derive(Default)]
pub struct ModelCapture {
    /// Only models whose name passes this filter are captured (they're big).
    pub filter: Option<fn(&str) -> bool>,
    images: HashMap<Ptr, Link>,
    image_names: Vec<String>,
    materials: HashMap<Ptr, Link>,
    material_recs: Vec<MaterialRec>,
    surfaces: HashMap<Ptr, Ptr>,
    surface_names: HashMap<Ptr, Ptr>,
    model_names: HashMap<Ptr, Ptr>,
    pub meshes: Vec<Mesh>,
    pub skipped_shared_surfaces: usize,
    pub failed: usize,
    /// Models whose name passes this also get skeleton + skin captured into `rig`.
    pub rig_filter: Option<fn(&str) -> bool>,
    /// XAnims whose name passes this are captured too (`viewmodel_*` always are).
    pub xanim_filter: Option<fn(&str) -> bool>,
    pub strings: Option<ScriptStrings>,
    pub rig: RigCapture,
    /// Every FxEffectDef the zone carries.
    pub fx: crate::fx::FxCapture,
    /// Every TracerDef the zone carries.
    pub tracers: Vec<crate::fx::TracerDef>,
    /// Every FxImpactTable the zone carries (MW2 has one, "default").
    pub impact_tables: Vec<crate::fx::ImpactTable>,
    /// Zone slot -> index into `fx.effects` / `tracers`.
    fx_links: HashMap<Ptr, Link>,
    tracer_links: HashMap<Ptr, Link>,
    /// What the capture hooks just stored; the `loaded` call for the same asset binds it.
    /// (`load_fx` / `load_tracer` run their capture hook before `links.loaded` fires.)
    pending_fx: Option<(usize, Ptr)>,
    pending_tracer: Option<(usize, Ptr)>,
}

fn half_to_f32(bits: u16) -> f32 {
    let sign = u32::from(bits & 0x8000) << 16;
    let exponent = (bits >> 10) & 0x1f;
    let mantissa = u32::from(bits & 0x03ff);
    let value = match exponent {
        0 if mantissa == 0 => sign,
        0 => {
            let mut mantissa = mantissa;
            let mut exponent = -14i32;
            while mantissa & 0x0400 == 0 {
                mantissa <<= 1;
                exponent -= 1;
            }
            sign | (((exponent + 127) as u32) << 23) | ((mantissa & 0x03ff) << 13)
        }
        0x1f => sign | 0x7f80_0000 | (mantissa << 13),
        _ => sign | ((u32::from(exponent) + 112) << 23) | (mantissa << 13),
    };
    f32::from_bits(value)
}

/// dpvs_iw4::skin_unpack_unit_vec
fn unpack_unit_vec(packed: u32) -> [f32; 3] {
    const W_BIAS: f32 = -192.0; // f32::from_bits(0xc340_0000)
    const DECODE: f32 = 127.0 * 255.0;
    const BIAS: f32 = 127.0; // f32::from_bits(0x42fe_0000)
    let b = packed.to_le_bytes();
    let scale = (f32::from(b[3]) - W_BIAS) / DECODE;
    let v = [(f32::from(b[0]) - BIAS) * scale, (f32::from(b[1]) - BIAS) * scale, (f32::from(b[2]) - BIAS) * scale];
    let l = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if l == 0.0 { [0.0, 0.0, 1.0] } else { [v[0] / l, v[1] / l, v[2] / l] }
}

impl ModelCapture {
    pub fn with_filter(filter: Option<fn(&str) -> bool>) -> Self {
        Self { filter, ..Self::default() }
    }

    pub fn remember_xmodel_surfaces(&mut self, slot: Ptr, surfaces: Ptr) {
        self.surfaces.insert(slot, surfaces);
    }
    pub fn xmodel_surfaces(&self, slot: Ptr) -> Option<Ptr> {
        self.surfaces.get(&slot).copied()
    }
    pub fn remember_xmodel_surface_name(&mut self, slot: Ptr, name: Ptr) {
        self.surface_names.insert(slot, name);
    }
    pub fn xmodel_surface_name(&self, slot: Ptr) -> Option<Ptr> {
        self.surface_names.get(&slot).copied()
    }
    pub fn remember_xmodel_name(&mut self, slot: Ptr, insert_slot: Option<Ptr>, name: Ptr) {
        self.model_names.insert(slot, name);
        if let Some(i) = insert_slot {
            self.model_names.insert(i, name);
        }
    }
    pub fn xmodel_name_ptr(&self, slot: Ptr) -> Option<Ptr> {
        self.model_names.get(&slot).copied()
    }

    pub fn alias(&mut self, ty: AssetType, slot: Ptr, target: Ptr) {
        match ty {
            AssetType::Image => bind(&mut self.images, slot, Link::Alias(target)),
            AssetType::Material => bind(&mut self.materials, slot, Link::Alias(target)),
            AssetType::Fx => bind(&mut self.fx_links, slot, Link::Alias(target)),
            AssetType::Tracer => bind(&mut self.tracer_links, slot, Link::Alias(target)),
            _ => {}
        }
    }

    pub fn loaded(&mut self, s: &ZoneStream<'_>, ty: AssetType, slot: Ptr, insert_slot: Option<Ptr>) {
        match ty {
            AssetType::Image => {
                let Some(g) = s.latest_image() else { return };
                let name = g.name.and_then(|p| s.cstr(p).ok()).unwrap_or("").to_owned();
                self.image_names.push(name);
                let idx = self.image_names.len() - 1;
                bind(&mut self.images, slot, Link::Direct(idx));
                if let Some(i) = insert_slot {
                    bind(&mut self.images, i, Link::Direct(idx));
                }
            }
            AssetType::Material => {
                let Some(g) = s.latest_material() else { return };
                let name = g.name.and_then(|p| s.cstr(p).ok()).unwrap_or("").to_owned();
                let mut color_image = None;
                let mut first_image = None;
                let mut textures = Vec::new();
                if let Some(table) = g.textures {
                    for i in 0..g.texture_count {
                        let tex = table.at(i * g.texture_stride);
                        textures.push((s.u8_at(tex, 7).unwrap_or(0), s.u32_at(tex, 0).unwrap_or(0), resolve(&self.images, tex.at(8))));
                        if s.u8_at(tex, 7).ok() == Some(TS_COLOR_MAP) && color_image.is_none() {
                            color_image = resolve(&self.images, tex.at(8));
                        }
                        if first_image.is_none() {
                            first_image = resolve(&self.images, tex.at(8));
                        }
                    }
                }
                // HUD materials (killstreak icons, minimap) carry a 2D image, not a colour map.
                let color_image = color_image.or(first_image);
                let state_bits = pick_state_bits(s, &g);
                self.material_recs.push(MaterialRec { name, color_image, textures, state_bits, camera_region: g.camera_region });
                let idx = self.material_recs.len() - 1;
                bind(&mut self.materials, slot, Link::Direct(idx));
                if let Some(i) = insert_slot {
                    bind(&mut self.materials, i, Link::Direct(idx));
                }
                if let Some(h) = g.header {
                    bind(&mut self.materials, h, Link::Direct(idx));
                }
            }
            AssetType::Fx => {
                if let Some((idx, header)) = self.pending_fx.take() {
                    for p in [Some(slot), insert_slot, Some(header)].into_iter().flatten() {
                        bind(&mut self.fx_links, p, Link::Direct(idx));
                    }
                }
            }
            AssetType::Tracer => {
                if let Some((idx, header)) = self.pending_tracer.take() {
                    for p in [Some(slot), insert_slot, Some(header)].into_iter().flatten() {
                        bind(&mut self.tracer_links, p, Link::Direct(idx));
                    }
                }
            }
            AssetType::XModel => {
                let Some(g) = s.xmodel() else { return };
                let Some(name) = g.name.and_then(|p| s.cstr(p).ok()) else { return };
                if self.filter.is_some_and(|f| !f(name)) {
                    return;
                }
                let name = name.to_owned();
                let rig = self.rig_filter.is_some_and(|f| f(&name));
                match self.capture_lod0(s, &g, name.clone()) {
                    Some(mesh) => {
                        if rig {
                            match self.capture_rig(s, &g, &mesh) {
                                Some(r) => self.rig.models.push(r),
                                None => self.rig.failed_models += 1,
                            }
                        }
                        self.meshes.push(mesh);
                    }
                    None => self.failed += 1,
                }
            }
            _ => {}
        }
    }

    /// The material a zone pointer slot refers to (e.g. a weapon's HUD icon).
    pub fn material_name(&self, slot: Ptr) -> Option<String> {
        resolve(&self.materials, slot).and_then(|i| self.material_recs.get(i)).map(|m| m.name.clone())
    }

    /// As `material_name`, borrowed (menu item backgrounds that share an earlier material).
    pub fn material_name_ref(&self, slot: Ptr) -> Option<&str> {
        resolve(&self.materials, slot).and_then(|i| self.material_recs.get(i)).map(|m| m.name.as_str())
    }

    /// The effect a zone pointer slot refers to (a weapon's muzzle flash, an impact table cell),
    /// by the name `mw2_fx_find` accepts.
    pub fn fx_name(&self, slot: Ptr) -> Option<String> {
        resolve(&self.fx_links, slot).and_then(|i| self.fx.effects.get(i)).map(|e| e.name.clone())
    }

    /// The TracerDef a zone pointer slot refers to, by name.
    pub fn tracer_name(&self, slot: Ptr) -> Option<String> {
        resolve(&self.tracer_links, slot).and_then(|i| self.tracers.get(i)).map(|t| t.name.clone())
    }

    /// `capture_fx` hook tail: remember which effect (if any) the hook just stored.
    pub fn fx_captured(&mut self, before: usize, header: Ptr) {
        let n = self.fx.effects.len();
        self.pending_fx = (n > before).then_some((n - 1, header));
    }

    /// `capture_tracer` hook body.
    pub fn capture_tracer(&mut self, s: &ZoneStream<'_>, g: fastfile_iw4::TracerDefGeometry) {
        self.pending_tracer = None;
        let Some(name) = g.name.and_then(|p| s.cstr(p).ok()).filter(|n| !n.is_empty()).map(str::to_owned) else { return };
        let alias = |slot: Ptr| match s.ptr_at(slot, 0) {
            Ok(ZonePtr::Offset(target)) => Some(s.resolve_alias(target)),
            _ => None,
        };
        let material = g
            .material_slot
            .and_then(|slot| self.fx_material(slot).or_else(|| alias(slot).and_then(|a| self.fx_material(a))))
            .filter(|m| !m.name.is_empty());
        let (material_name, material_id) = match material {
            Some(m) => {
                let name = m.name.clone();
                let id = self.fx.intern_material(m);
                (Some(name), if id == u16::MAX { 0 } else { id + 1 })
            }
            None => (None, 0),
        };
        self.tracers.push(crate::fx::TracerDef {
            name,
            material: material_name,
            material_id,
            draw_interval: g.draw_interval as i32,
            speed: g.speed,
            beam_length: g.beam_length,
            beam_width: g.beam_width,
            screw_radius: g.screw_radius,
            screw_dist: g.screw_dist,
            colors: g.colors,
        });
        if let Some(header) = g.header {
            self.pending_tracer = Some((self.tracers.len() - 1, header));
        }
    }

    /// `capture_impact_fx` hook body: 15 rows x (31 surface + 4 flesh) effect names.
    pub fn capture_impact_table(&mut self, s: &ZoneStream<'_>, g: fastfile_iw4::FxImpactTableGeometry) {
        use crate::fx::{IMPACT_COLUMNS, ImpactTable};
        let name = g.name.and_then(|p| s.cstr(p).ok()).unwrap_or("").to_owned();
        let stride = s.layout(IMPACT_COLUMNS * 4, IMPACT_COLUMNS * 8);
        let rows = (0..g.row_count)
            .map(|row| (0..IMPACT_COLUMNS).map(|col| self.fx_name(g.entries.at(row * stride + col * s.pointer_bytes()))).collect())
            .collect();
        self.impact_tables.push(ImpactTable { name, rows });
    }

    /// Material name, colour image and blend state bits for a zone slot (FX visuals).
    pub fn fx_material(&self, slot: Ptr) -> Option<crate::fx::FxMaterial> {
        let m = resolve(&self.materials, slot).and_then(|i| self.material_recs.get(i))?;
        Some(crate::fx::FxMaterial {
            name: m.name.clone(),
            image: m.color_image.and_then(|i| self.image_names.get(i).cloned()).filter(|n| !n.is_empty()),
            state_bits: m.state_bits,
            camera_region: m.camera_region,
        })
    }

    /// A material this zone defined, by exact name (colour image and blend state).
    pub fn material_by_name(&self, name: &str) -> Option<crate::fx::FxMaterial> {
        let m = self.material_recs.iter().find(|m| m.name == name)?;
        Some(crate::fx::FxMaterial {
            name: m.name.clone(),
            image: m.color_image.and_then(|i| self.image_names.get(i).cloned()).filter(|n| !n.is_empty()),
            state_bits: m.state_bits,
            camera_region: m.camera_region,
        })
    }

    /// Every material with more than a colour map: (material, [(semantic, name hash, image)]).
    pub fn all_material_textures(&self) -> Vec<(String, Vec<(u8, u32, String)>)> {
        self.material_recs
            .iter()
            .filter(|m| m.textures.len() > 1)
            .map(|m| (m.name.clone(), m.textures.iter().map(|(sem, h, i)| (*sem, *h, i.and_then(|i| self.image_names.get(i).cloned()).unwrap_or_default())).collect()))
            .collect()
    }

    /// A material's textures as (semantic, IW4 name hash, image name).
    pub fn material_textures(&self, name: &str) -> Vec<(u8, u32, String)> {
        self.material_recs
            .iter()
            .find(|m| m.name == name)
            .map(|m| m.textures.iter().map(|(sem, h, i)| (*sem, *h, i.and_then(|i| self.image_names.get(i).cloned()).unwrap_or_default())).collect())
            .unwrap_or_default()
    }

    /// Every material the zone defined that has blend state: (material, state bits).
    pub fn material_state_bits(&self) -> Vec<(String, [u32; 2])> {
        self.material_recs.iter().filter_map(|m| m.state_bits.map(|b| (m.name.clone(), b))).collect()
    }

    /// Every material the zone defined, with its colour image: (material, image).
    pub fn material_images(&self) -> Vec<(String, String)> {
        self.material_recs
            .iter()
            .filter_map(|m| m.color_image.and_then(|i| self.image_names.get(i)).map(|img| (m.name.clone(), img.clone())))
            .collect()
    }

    /// Skeleton, per-vertex skin and per-surface part bits for an already-captured mesh.
    fn capture_rig(&self, s: &ZoneStream<'_>, g: &fastfile_iw4::XModelGeometry, mesh: &Mesh) -> Option<RigModel> {
        let strings = self.strings.as_ref()?;
        let pose = crate::rig::capture_pose(s, strings, g, &mesh.name)?;
        let surfaces_ptr = g.lod_xsurfaces[0]?;
        let stride = if s.wire_format() == Iw4WireFormat::X64 { XSURFACE.1 } else { XSURFACE.0 };
        let mut skin = Vec::with_capacity(mesh.positions.len());
        let mut surfaces = Vec::with_capacity(mesh.surfaces.len());
        for (si, ms) in mesh.surfaces.iter().enumerate() {
            let surface = surfaces_ptr.at(si * stride);
            let vertex_count = s.u16_at(surface, 2).ok()? as usize;
            let vert_list_count = s.u32_at(surface, s.layout(32, 48)).ok()? as usize;
            skin.extend(crate::rig::decode_surface_skin(s, surface, vertex_count, vert_list_count, g.num_bones)?);
            let mut part_bits = [0u32; 6];
            for (i, w) in part_bits.iter_mut().enumerate() {
                *w = s.u32_at(surface, s.layout(40, 64) + i * 4).ok()?;
            }
            surfaces.push(RigSurface {
                index_start: ms.index_start,
                index_count: ms.index_count,
                material: ms.material.clone(),
                color_map: ms.color_map.clone(),
                part_bits,
            });
        }
        if skin.len() != mesh.positions.len() {
            return None;
        }
        Some(RigModel {
            name: mesh.name.clone(),
            pose,
            positions: mesh.positions.clone(),
            normals: mesh.normals.clone(),
            uvs: mesh.uvs.clone(),
            indices: mesh.indices.clone(),
            skin,
            surfaces,
        })
    }

    fn capture_lod0(&mut self, s: &ZoneStream<'_>, g: &fastfile_iw4::XModelGeometry, name: String) -> Option<Mesh> {
        let lod = 0;
        let count = usize::from(g.lod_numsurfs[lod]);
        let Some(surfaces) = g.lod_xsurfaces[lod] else {
            // Surfaces shared from a separate XModelSurfs asset; not handled yet.
            self.skipped_shared_surfaces += 1;
            return None;
        };
        let stride = if s.wire_format() == Iw4WireFormat::X64 { XSURFACE.1 } else { XSURFACE.0 };
        let mut mesh = Mesh { name, ..Mesh::default() };
        let handle_base = usize::from(g.lod_surf_index[lod]);
        for si in 0..count {
            let surface = surfaces.at(si * stride);
            let vertex_count = s.u16_at(surface, 2).ok()? as usize;
            let tri_count = s.u16_at(surface, 4).ok()? as usize;
            let vertices = match s.ptr_at(surface, s.layout(28, 40)).ok()? {
                ZonePtr::Offset(p) => s.resolve_alias(p),
                _ => return None,
            };
            let triangles = match s.ptr_at(surface, s.layout(12, 16)).ok()? {
                ZonePtr::Offset(p) => s.resolve_alias(p),
                _ => return None,
            };
            let base = mesh.positions.len() as u32;
            let index_start = mesh.indices.len() as u32;
            for vi in 0..vertex_count {
                let v = vertices.at(vi * GFX_PACKED_VERTEX);
                mesh.positions.push([s.f32_at(v, 0).ok()?, s.f32_at(v, 4).ok()?, s.f32_at(v, 8).ok()?]);
                let uv = s.u32_at(v, 20).ok()?;
                mesh.uvs.push([half_to_f32((uv >> 16) as u16), half_to_f32(uv as u16)]);
                mesh.normals.push(unpack_unit_vec(s.u32_at(v, 24).ok()?));
            }
            for ii in 0..tri_count * 3 {
                let idx = s.u16_at(triangles, ii * 2).ok()?;
                if usize::from(idx) >= vertex_count {
                    return None;
                }
                mesh.indices.push(base + u32::from(idx));
            }
            let slot = handle_base + si;
            let material = (slot < g.material_handle_count)
                .then(|| g.material_handles.and_then(|h| resolve(&self.materials, h.at(slot * s.pointer_bytes()))))
                .flatten()
                .and_then(|m| self.material_recs.get(m));
            mesh.surfaces.push(Surface {
                index_start,
                index_count: mesh.indices.len() as u32 - index_start,
                material: material.map(|m| m.name.clone()),
                color_map: material.and_then(|m| m.color_image).and_then(|i| self.image_names.get(i).cloned()).filter(|n| !n.is_empty()),
                state_bits: material.and_then(|m| m.state_bits),
            });
        }
        Some(mesh)
    }
}

/// IWI v8 texture, top mip only. `format`: 1 BGRA8, 2 RGB8, 11 DXT1, 12 DXT3, 13 DXT5.
pub struct Iwi {
    pub width: u32,
    pub height: u32,
    pub format: u8,
    pub top_mip: Vec<u8>,
}

pub fn parse_iwi(bytes: &[u8]) -> Option<Iwi> {
    if bytes.len() < 32 || &bytes[..3] != b"IWi" || bytes[3] != 8 {
        return None;
    }
    let format = bytes[8];
    let width = u32::from(u16::from_le_bytes([bytes[10], bytes[11]]));
    let height = u32::from(u16::from_le_bytes([bytes[12], bytes[13]]));
    let mip0_end = u32::from_le_bytes(bytes[16..20].try_into().ok()?) as usize;
    let mip0_start = u32::from_le_bytes(bytes[20..24].try_into().ok()?) as usize;
    let end = mip0_end.min(bytes.len());
    let start = if (32..end).contains(&mip0_start) { mip0_start } else { 32 };
    Some(Iwi { width, height, format, top_mip: bytes.get(start..end)?.to_vec() })
}
