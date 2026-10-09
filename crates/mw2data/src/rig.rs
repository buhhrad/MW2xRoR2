//! Skinned MW2 models (viewhands, viewmodel guns) and XAnim clips, captured during a
//! zone walk. Bevy-free ports of IW4L's `asset_model::model_skel` (`capture_pose_src`,
//! `decode_surface_skin`) and `asset_anim::xanim_catalog::capture_xanim`; the runtime
//! types (`ModelPoseSrc`, `RawXAnimParts`) are IW4L's own `xmodel_runtime`.
//!
//! Vertices are model-space bind pose (skin = world[bone] * inverse(bind_world[bone])).

use std::collections::HashMap;

use fastfile_iw4::{Ptr, ScriptStrings, XAnimPartsGeometry, XModelGeometry, ZonePtr, ZoneStream};
use glam::{Quat, Vec3};
use xmodel_runtime::{ClipNotify, ModelPoseSrc, RawDeltaTrans, RawXAnimParts};

const BONE_STRIDE: u16 = 64;
const SKIN_BLEND_WEIGHT_SCALE: f32 = 1.0 / 65536.0;
const XMODEL_QUAT: usize = 8;
const DOBJ_ANIM_MAT: usize = 32;
const XANIM_NOTIFY_INFO: usize = 8;

#[derive(Clone, Copy, Debug, Default)]
pub struct VertSkin {
    pub bones: [u16; 4],
    pub weights: [f32; 4],
}

/// Per-surface data for a skinned model.
#[derive(Clone, Debug)]
pub struct RigSurface {
    pub index_start: u32,
    pub index_count: u32,
    pub material: Option<String>,
    pub color_map: Option<String>,
    /// XSurface partBits: which bones this surface depends on (for hide tags).
    pub part_bits: [u32; 6],
}

#[derive(Clone, Debug)]
pub struct RigModel {
    pub name: String,
    pub pose: ModelPoseSrc,
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub indices: Vec<u32>,
    pub skin: Vec<VertSkin>,
    pub surfaces: Vec<RigSurface>,
}

fn bone_at(raw: u16, num_bones: usize) -> Option<u16> {
    if raw % BONE_STRIDE != 0 {
        return None;
    }
    let bone = raw / BONE_STRIDE;
    ((bone as usize) < num_bones).then_some(bone)
}

/// IW4L `asset_model::model_skel::decode_surface_skin`.
pub fn decode_surface_skin(s: &ZoneStream<'_>, surface: Ptr, vertex_count: usize, vert_list_count: usize, num_bones: usize) -> Option<Vec<VertSkin>> {
    let mut skins = vec![VertSkin::default(); vertex_count];
    if vert_list_count > 0 {
        if let Ok(ZonePtr::Offset(list_ptr)) = s.ptr_at(surface, s.layout(36, 56)) {
            let list = s.resolve_alias(list_ptr);
            let mut vertex = 0usize;
            for i in 0..vert_list_count {
                let entry = list.at(i * s.layout(12, 16));
                let bone = bone_at(s.u16_at(entry, 0).ok()?, num_bones)?;
                let run = s.u16_at(entry, 2).ok()? as usize;
                for _ in 0..run {
                    if vertex >= vertex_count {
                        break;
                    }
                    skins[vertex] = VertSkin { bones: [bone, 0, 0, 0], weights: [1.0, 0.0, 0.0, 0.0] };
                    vertex += 1;
                }
            }
            if vertex == vertex_count {
                return Some(skins);
            }
        }
    }
    let vi = surface.at(s.layout(16, 24));
    let counts = [
        s.i16_at(vi, 0).ok()?.max(0) as usize,
        s.i16_at(vi, 2).ok()?.max(0) as usize,
        s.i16_at(vi, 4).ok()?.max(0) as usize,
        s.i16_at(vi, 6).ok()?.max(0) as usize,
    ];
    let blend = match s.ptr_at(vi, 8).ok()? {
        ZonePtr::Offset(p) => s.resolve_alias(p),
        ZonePtr::Null if counts.iter().all(|&c| c == 0) => {
            for skin in &mut skins {
                *skin = VertSkin { bones: [0; 4], weights: [1.0, 0.0, 0.0, 0.0] };
            }
            return Some(skins);
        }
        _ => return None,
    };
    let mut cursor = 0usize;
    let mut vertex = 0usize;
    for (bucket, count) in counts.iter().enumerate() {
        let influences = bucket + 1;
        for _ in 0..*count {
            let start = cursor;
            cursor += 1 + (influences - 1) * 2;
            let mut skin = VertSkin::default();
            skin.bones[0] = bone_at(s.u16_at(blend, start * 2).ok()?, num_bones)?;
            let mut remaining = 1.0f32;
            for extra in 1..influences {
                let off = start + 1 + (extra - 1) * 2;
                skin.bones[extra] = bone_at(s.u16_at(blend, off * 2).ok()?, num_bones)?;
                let w = s.u16_at(blend, off * 2 + 2).ok()? as f32 * SKIN_BLEND_WEIGHT_SCALE;
                skin.weights[extra] = w;
                remaining -= w;
            }
            skin.weights[0] = remaining;
            if vertex >= vertex_count {
                return None;
            }
            skins[vertex] = skin;
            vertex += 1;
        }
    }
    (vertex == vertex_count).then_some(skins)
}

/// IW4L `capture_model_skel_iw4` bone section + `capture_pose_src`.
pub fn capture_pose(s: &ZoneStream<'_>, strings: &ScriptStrings, g: &XModelGeometry, name: &str) -> Option<ModelPoseSrc> {
    let bone_names_ptr = g.bone_names?;
    let base_mat = g.base_mat?;
    let mut names = Vec::with_capacity(g.num_bones);
    let mut binds = Vec::with_capacity(g.num_bones);
    for i in 0..g.num_bones {
        let id = s.u16_at(bone_names_ptr, i * 2).ok()?;
        names.push(strings.get(s, id).unwrap_or("").to_owned());
        let m = base_mat.at(i * DOBJ_ANIM_MAT);
        binds.push((
            Quat::from_xyzw(s.f32_at(m, 0).ok()?, s.f32_at(m, 4).ok()?, s.f32_at(m, 8).ok()?, s.f32_at(m, 12).ok()?),
            Vec3::new(s.f32_at(m, 16).ok()?, s.f32_at(m, 20).ok()?, s.f32_at(m, 24).ok()?),
        ));
    }
    let num_child = g.num_bones.saturating_sub(g.num_root_bones);
    let (parent_list, quats, trans) = if num_child == 0 {
        (Vec::new(), Vec::new(), Vec::new())
    } else {
        let pl = g.parent_list?;
        let qa = g.quats?;
        let ta = g.trans?;
        let mut parent_list = Vec::with_capacity(num_child);
        let mut quats = Vec::with_capacity(num_child);
        let mut trans = Vec::with_capacity(num_child);
        for i in 0..num_child {
            parent_list.push(s.u8_at(pl, i).ok()?);
            let o = i * XMODEL_QUAT;
            quats.push([s.i16_at(qa, o).ok()?, s.i16_at(qa, o + 2).ok()?, s.i16_at(qa, o + 4).ok()?, s.i16_at(qa, o + 6).ok()?]);
            let t = i * 12;
            trans.push([s.f32_at(ta, t).ok()?, s.f32_at(ta, t + 4).ok()?, s.f32_at(ta, t + 8).ok()?]);
        }
        (parent_list, quats, trans)
    };
    Some(ModelPoseSrc {
        name: name.to_owned(),
        num_bones: g.num_bones,
        num_root_bones: g.num_root_bones,
        scale: g.scale,
        no_scale_part_bits: g.no_scale_part_bits,
        bone_names: names,
        parent_list,
        quats,
        trans,
        base_mat: binds,
    })
}

fn copy_u8(s: &ZoneStream<'_>, ptr: Option<Ptr>, count: usize) -> Vec<u8> {
    ptr.and_then(|p| s.slice_at(p, 0, count).ok().map(<[u8]>::to_vec)).unwrap_or_default()
}

fn copy_u16(s: &ZoneStream<'_>, ptr: Option<Ptr>, count: usize) -> Vec<u16> {
    ptr.and_then(|p| s.slice_at(p, 0, count * 2).ok())
        .map(|b| b.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect())
        .unwrap_or_default()
}

fn copy_u32(s: &ZoneStream<'_>, ptr: Option<Ptr>, count: usize) -> Vec<u32> {
    ptr.and_then(|p| s.slice_at(p, 0, count * 4).ok())
        .map(|b| b.chunks_exact(4).map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect())
        .unwrap_or_default()
}

fn copy_f32_3(s: &ZoneStream<'_>, p: Ptr, off: usize) -> Option<[f32; 3]> {
    Some([s.f32_at(p, off).ok()?, s.f32_at(p, off + 4).ok()?, s.f32_at(p, off + 8).ok()?])
}

fn copy_delta_trans(s: &ZoneStream<'_>, geo: fastfile_iw4::XAnimDeltaTransGeometry) -> Option<RawDeltaTrans> {
    if let Some(constant) = geo.constant {
        return Some(RawDeltaTrans { size: 0, small: geo.small != 0, mins: copy_f32_3(s, constant, 0)?, step: [0.0; 3], indices: Vec::new(), packed: Vec::new() });
    }
    let ms = geo.mins_step?;
    let n = geo.size as usize + 1;
    let indices = if geo.indices_are_bytes { copy_u8(s, geo.indices, n).into_iter().map(u16::from).collect() } else { copy_u16(s, geo.indices, n) };
    let packed = copy_u8(s, geo.frames, if geo.small != 0 { 3 * n } else { 6 * n });
    Some(RawDeltaTrans { size: geo.size, small: geo.small != 0, mins: copy_f32_3(s, ms, 0)?, step: copy_f32_3(s, ms, 12)?, indices, packed })
}

/// IW4L `asset_anim::xanim_catalog::capture_xanim` (IW4 branch).
pub fn capture_xanim(s: &ZoneStream<'_>, strings: &ScriptStrings, g: &XAnimPartsGeometry) -> Option<RawXAnimParts> {
    let name = s.cstr(g.name?).ok().filter(|n| !n.is_empty())?.to_owned();
    let track_count = g.bone_count[9] as usize;
    let mut names = Vec::with_capacity(track_count);
    match g.names {
        Some(arr) => {
            for i in 0..track_count {
                let sid = s.u16_at(arr, i * 2).unwrap_or(0);
                names.push(strings.get(s, sid).unwrap_or("").to_owned());
            }
        }
        None if track_count > 0 => return None,
        None => {}
    }
    let mut notifies = Vec::with_capacity(g.notify_count);
    if let Some(arr) = g.notify {
        for i in 0..g.notify_count {
            let b = arr.at(i * XANIM_NOTIFY_INFO);
            notifies.push(ClipNotify { name: strings.get(s, s.u16_at(b, 0).unwrap_or(0)).unwrap_or("").to_owned(), time: s.f32_at(b, 4).unwrap_or(0.0) });
        }
    }
    let indices = if g.indices_are_bytes { copy_u8(s, g.indices, g.index_count).into_iter().map(u16::from).collect() } else { copy_u16(s, g.indices, g.index_count) };
    Some(RawXAnimParts {
        name,
        data_byte: copy_u8(s, g.data_byte, g.data_byte_count),
        data_short: copy_u16(s, g.data_short, g.data_short_count),
        data_int: copy_u32(s, g.data_int, g.data_int_count),
        random_data_byte: copy_u8(s, g.random_data_byte, g.random_data_byte_count),
        random_data_short: copy_u16(s, g.random_data_short, g.random_data_short_count),
        random_data_int: copy_u32(s, g.random_data_int, g.random_data_int_count),
        numframes: g.numframes,
        flags: g.flags,
        bone_count: g.bone_count,
        framerate: g.framerate,
        names,
        notifies,
        indices,
        delta_trans: copy_delta_trans(s, g.delta_trans),
    })
}

/// Everything the viewmodel needs from one zone walk.
#[derive(Default)]
pub struct RigCapture {
    pub models: Vec<RigModel>,
    pub xanims: HashMap<String, RawXAnimParts>,
    pub failed_models: usize,
    pub failed_xanims: usize,
}
