//! The full MW2 first-person viewmodel: viewhands + gun in one skeleton (gun on the
//! hands' `tag_weapon`, as IW4L's `asset_game::fpv_assembly`), animated with the weapon's
//! own XAnims, posed every frame from the sim's weapon state the way IW4L's
//! `render_anim::anim::viewmodel_controller` does, and expressed relative to the rig's
//! `tag_view` bone (the eye).
//!
//! Everything handed to the host is already in Unity space: metres (true MW2 inches),
//! Y up, Z forward, left-handed. IW4 -> Unity is the mirror (x, y, z) -> (-y, z, x) and
//! rotations map as q -> (q.y, -q.z, -q.x, q.w). Triangle order is kept: IW4's front faces
//! are counter-clockwise in its right-handed space, and the mirror alone makes them
//! clockwise, which is Unity's front. (Reversing it as well drew every model inside out.)

use std::collections::HashMap;
use std::sync::{Arc, RwLock};

use glam::{Mat4, Quat, Vec3};
use mw2data::rig::RigModel;
use xmodel_runtime::{AnimClip, AnimInstance, Attach, DObj, RawXAnimParts};

pub const INCHES_TO_METRES: f32 = 0.0254;
/// How far the killstreak laptop's display layers are lifted off its screen glass (metres).
const SCREEN_LIFT_M: f32 = 0.004;
pub const HANDS_MODEL: &str = "viewmodel_base_viewhands";

// Weapon anim slots (asset_iw4::size::weap_anim).
pub const IDLE: usize = 1;
pub const EMPTY_IDLE: usize = 2;
pub const FIRE: usize = 3;
pub const LASTSHOT: usize = 5;
pub const RECHAMBER: usize = 6;
pub const RELOAD: usize = 9;
pub const RELOAD_EMPTY: usize = 10;
pub const RELOAD_START: usize = 11;
pub const RELOAD_END: usize = 12;
pub const RAISE: usize = 13;
pub const SPRINT_IN: usize = 23;
pub const SPRINT_LOOP: usize = 24;
pub const SPRINT_OUT: usize = 25;
pub const ADS_FIRE: usize = 32;
pub const ADS_LASTSHOT: usize = 33;
pub const ADS_UP: usize = 35;
pub const ADS_DOWN: usize = 36;
pub const SLOTS: usize = 37;

/// MW2's weapon anim slots (asset_iw4::size::weap_anim; IW4L, Apache-2.0).
pub mod asset_slots {
    pub const IDLE: usize = 0x1;
    pub const EMPTY_IDLE: usize = 0x2;
    pub const FIRE: usize = 0x3;
    pub const HOLD_FIRE: usize = 0x4;
    pub const LASTSHOT: usize = 0x5;
    pub const RECHAMBER: usize = 0x6;
    pub const MELEE: usize = 0x7;
    pub const MELEE_CHARGE: usize = 0x8;
    pub const RELOAD: usize = 0x9;
    pub const RELOAD_EMPTY: usize = 0xA;
    pub const RELOAD_START: usize = 0xB;
    pub const RELOAD_END: usize = 0xC;
    pub const RAISE: usize = 0xD;
    pub const FIRST_RAISE: usize = 0xE;
    pub const DROP: usize = 0x10;
    pub const ALT_RAISE: usize = 0x11;
    pub const ALT_DROP: usize = 0x12;
    pub const QUICK_RAISE: usize = 0x13;
    pub const QUICK_DROP: usize = 0x14;
    pub const EMPTY_RAISE: usize = 0x15;
    pub const EMPTY_DROP: usize = 0x16;
    pub const SPRINT_IN: usize = 0x17;
    pub const SPRINT_LOOP: usize = 0x18;
    pub const SPRINT_OUT: usize = 0x19;
    pub const STUNNED_LOOP: usize = 0x1B;
    pub const ADS_FIRE: usize = 0x20;
    pub const ADS_LASTSHOT: usize = 0x21;
    pub const ADS_RECHAMBER: usize = 0x22;
}

pub static RIGS: RwLock<Vec<RigModel>> = RwLock::new(Vec::new());
/// Raw clips by lowercase name; decoded lazily.
pub static XANIMS: RwLock<Option<HashMap<String, Arc<RawXAnimParts>>>> = RwLock::new(None);
static DECODED: RwLock<Option<HashMap<String, Option<Arc<AnimClip>>>>> = RwLock::new(None);

fn clip(name: &str) -> Option<Arc<AnimClip>> {
    let key = name.to_ascii_lowercase();
    if let Some(c) = DECODED.read().ok()?.as_ref().and_then(|m| m.get(&key).cloned()) {
        return c;
    }
    let raw = XANIMS.read().ok()?.as_ref()?.get(&key).cloned();
    let decoded = raw.and_then(|r| AnimClip::from_parts(&r).ok()).map(Arc::new);
    DECODED.write().ok()?.get_or_insert_with(HashMap::new).insert(key, decoded.clone());
    decoded
}

fn rig(name: &str) -> Option<RigModel> {
    RIGS.read().ok()?.iter().find(|m| m.name == name).cloned()
}

/// The arms the viewmodel is built with: the player's character's own (`set_hands`), else
/// `HANDS_MODEL`.
static HANDS: RwLock<Option<String>> = RwLock::new(None);

/// Use `hands` (a character's viewhands rig from its map zone) for viewmodels built from now on;
/// None = MW2's base arms. True if they're set.
pub fn set_hands(hands: Option<RigModel>) -> bool {
    let name = hands.as_ref().map(|h| h.name.clone());
    if let (Some(h), Ok(mut rigs)) = (hands, RIGS.write()) {
        if !rigs.iter().any(|m| m.name == h.name) {
            rigs.push(h);
        }
    }
    let set = name.is_some();
    if let Ok(mut cur) = HANDS.write() {
        *cur = name;
    }
    set
}

fn hands_rig() -> Option<RigModel> {
    let cur = HANDS.read().ok().and_then(|h| h.clone());
    cur.and_then(|n| rig(&n)).or_else(|| rig(HANDS_MODEL))
}

/// IW4 -> Unity for points (scaled) and directions.
fn to_unity(p: [f32; 3], s: f32) -> [f32; 3] {
    [-p[1] * s, p[2] * s, p[0] * s]
}

fn quat_to_unity(q: Quat) -> [f32; 4] {
    [q.y, -q.z, -q.x, q.w]
}

/// Matrix form of the IW4 -> Unity map (scale folded in).
fn axis_matrix(s: f32) -> Mat4 {
    // columns: where IW x, y, z go in Unity
    Mat4::from_cols_array(&[0.0, 0.0, s, 0.0, -s, 0.0, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0])
}

pub struct Surface {
    pub index_start: u32,
    pub index_count: u32,
    pub color_map: Option<String>,
    /// detailMap image (semantic 3): MW2 camos keep the gun's colour map and swap this
    /// (detail_kaki_paint -> weapon_camo_woodland). Not drawn yet.
    pub detail_map: Option<String>,
    /// MW2 blend mode of its material (lenses, reticles, glows are see-through).
    pub blend: i32,
}

#[derive(Default, Clone, Copy)]
struct Play {
    slot: usize,
    time: f32,
    rate: f32,
    looping: bool,
}

pub struct Viewmodel {
    pub weapon: u32,
    dobj: DObj,
    view_bone: usize,
    clips: Vec<Option<(Arc<AnimClip>, Vec<Option<usize>>)>>,
    // Unity-space mesh
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub bone_index: Vec<[i32; 4]>,
    pub bone_weight: Vec<[f32; 4]>,
    pub indices: Vec<u32>,
    pub surfaces: Vec<Surface>,
    pub bindposes: Vec<[f32; 16]>,
    // animation state
    action: Option<Play>,
    idle_time: f32,
    /// Last `weap_anim` change seen from the sim, and its event.
    anim_seq: u32,
    last_event: u32,
    /// An action fading out into idle: (slot, frozen time, seconds into the fade).
    fade: Option<(usize, f32, f32)>,
    timers: Timers,
    /// The pose `mw2_viewmodel_step` last wrote (7 floats per bone), for tag lookups.
    pub last_pose: Vec<f32>,
    /// Sound aliases named by the notetracks the playing anim crossed since the last drain
    /// (`mw2_viewmodel_sounds`): MW2's first-person reload / bolt / raise sounds come from these.
    pub notetrack_sounds: Vec<String>,
    hide_tags: Vec<String>,
    /// 0 the right (or only) gun, 1 akimbo's left one.
    pub hand: u32,
    /// Akimbo: each gun's sideways shift in IW4 units (`dualWieldViewModelOffset`); 0 otherwise.
    pub dual_offset: f32,
    /// Pilot showcase: (slot, 0..1) held instead of what the sim plays (`mw2_viewmodel_force`).
    pub forced: Option<(usize, f32)>,
    /// Pilot showcase: seconds a held clip glides in from the pose it had, and back out (0.12 by
    /// default; the inspect's chamber check eases slower - its rack snapped, 10-06-26).
    pub force_glide: f32,
    /// Pilot showcase: the gun this one replaced, bone by bone (name -> pose), and how far through
    /// the glide from its pose to this one we are (`blend_from`): a swap without the snap.
    pub swap_from: Option<(HashMap<String, [f32; 7]>, f32, f32)>,
    /// The old gun's root (j_gun) pose for the swap glide: the new gun rides it as one rigid piece.
    swap_gun_root: Option<[f32; 7]>,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Timers {
    pub raise_ms: i32,
    pub drop_ms: i32,
    pub quick_raise_ms: i32,
    pub quick_drop_ms: i32,
    pub melee_ms: i32,
    pub melee_charge_ms: i32,
    pub reload_ms: i32,
    pub reload_empty_ms: i32,
    pub reload_start_ms: i32,
    pub reload_end_ms: i32,
    pub reload_add_ms: i32,
    pub sprint_raise_ms: i32,
    pub sprint_drop_ms: i32,
}

/// Which gun bones a weapon variant hides: named in its hide tags, or below one.
fn hidden_bones(pose: &xmodel_runtime::ModelPoseSrc, hide_tags: &[String]) -> Vec<bool> {
    let mut hidden = vec![false; pose.num_bones];
    for i in 0..pose.num_bones {
        let named = hide_tags.iter().any(|t| *t == pose.bone_names[i]);
        let parent_hidden = if i >= pose.num_root_bones {
            let step = pose.parent_list[i - pose.num_root_bones] as usize;
            i.checked_sub(step).is_some_and(|p| hidden[p])
        } else {
            false
        };
        hidden[i] = named || parent_hidden;
    }
    hidden
}

impl Viewmodel {
    pub fn build(weapon: u32) -> Option<Self> {
        Self::build_as(weapon, 0)
    }

    /// As `build`, with the gun model and hide tags of `look` (0 = the weapon's own): MW2's
    /// alternate weapons (underbarrel GL / shotgun) keep the parent rifle's attachments on screen.
    pub fn build_as(weapon: u32, look: u32) -> Option<Self> {
        Self::build_with(weapon, look, 0)
    }

    /// One hand of an akimbo weapon (MW2 draws two full viewmodels, each with its own handed
    /// anims: szXAnimsRightHanded / szXAnimsLeftHanded). None for hand 1 of a single gun.
    pub fn build_hand(weapon: u32, hand: u32) -> Option<Self> {
        Self::build_with(weapon, 0, hand)
    }

    fn build_with(weapon: u32, look: u32, hand: u32) -> Option<Self> {
        let mut row = crate::weapons::row(weapon)?;
        if hand != 0 && !row.akimbo {
            return None;
        }
        if row.akimbo {
            let handed = if hand == 0 { &row.xanims_right } else { &row.xanims_left };
            let merged: Vec<Option<String>> = (0..row.xanims.len().max(handed.len()))
                .map(|i| handed.get(i).cloned().flatten().or_else(|| row.xanims.get(i).cloned().flatten()))
                .collect();
            row.xanims = merged;
        }
        if let Some(parent) = (look != 0).then(|| crate::weapons::row(look)).flatten() {
            if parent.gun_model.is_some() {
                row.gun_model = parent.gun_model;
            }
            row.hide_tags = parent.hide_tags;
        }
        // The camo's own gun model when the zone has one (the alternate weapon wears its parent's).
        if let (Some(c), Some(g)) = (crate::weapons::camo(if look != 0 { look } else { weapon }), row.gun_model.as_deref()) {
            // ("_dust" style finishes are swapped, not added: the ACR's base is
            // viewmodel_magpul_masada_dust, its camo viewmodel_magpul_masada_red_tiger.)
            if let Some(camo_model) = crate::weapons::camo_model_names(g, &c).into_iter().find(|m| rig(m).is_some()) {
                row.gun_model = Some(camo_model);
            }
        }
        let hands = hands_rig()?;
        let gun = rig(row.gun_model.as_deref()?)?;
        let dobj = DObj::build(&[(&hands.pose, None), (&gun.pose, Some(Attach { parent_model: 0, tag: "tag_weapon".into() }))]).ok()?;
        let view_bone = dobj.find("tag_view")?;
        let hidden = hidden_bones(&gun.pose, &row.hide_tags);
        let s = INCHES_TO_METRES;

        let mut vm = Viewmodel {
            weapon,
            view_bone,
            clips: Vec::new(),
            positions: Vec::new(),
            normals: Vec::new(),
            uvs: Vec::new(),
            bone_index: Vec::new(),
            bone_weight: Vec::new(),
            indices: Vec::new(),
            surfaces: Vec::new(),
            bindposes: Vec::new(),
            action: None,
            idle_time: 0.0,
            anim_seq: u32::MAX,
            last_event: 0,
            fade: None,
            timers: row.timers,
            last_pose: Vec::new(),
            notetrack_sounds: Vec::new(),
            hide_tags: row.hide_tags.clone(),
            hand,
            dual_offset: if row.akimbo { row.dual_offset } else { 0.0 },
            forced: None,
            force_glide: 0.12,
            swap_from: None,
            swap_gun_root: None,
            dobj,
        };
        for (model_slot, model, hide) in [(0usize, &hands, None), (1usize, &gun, Some(&hidden))] {
            let base_bone = vm.dobj.models[model_slot].base as i32;
            let base_vertex = vm.positions.len() as u32;
            for i in 0..model.positions.len() {
                vm.positions.push(to_unity(model.positions[i], s));
                vm.normals.push(to_unity(model.normals[i], 1.0));
                vm.uvs.push(model.uvs[i]);
                let sk = model.skin[i];
                vm.bone_index.push([0, 1, 2, 3].map(|k| base_bone + i32::from(sk.bones[k])));
                vm.bone_weight.push(sk.weights);
            }
            for surf in &model.surfaces {
                let tris = &model.indices[surf.index_start as usize..(surf.index_start + surf.index_count) as usize];
                if let Some(hide) = hide {
                    let hidden_verts = tris.iter().filter(|&&v| hide.get(model.skin[v as usize].bones[0] as usize).copied().unwrap_or(false)).count();
                    if hidden_verts * 2 > tris.len() {
                        continue;
                    }
                }
                let start = vm.indices.len() as u32;
                for t in tris.chunks_exact(3) {
                    vm.indices.extend_from_slice(&[base_vertex + t[0], base_vertex + t[1], base_vertex + t[2]]);
                }
                let mut blend = surf.material.as_deref().map_or(0, crate::models::material_blend);
                // The killstreak laptop's display (mtl_uav_graphic / _scanlines / _cursor): MW2's shader
                // lights it; alpha-blended, its near-zero alpha left the glass black (playtest 10-04-26).
                // 6 = a glow that adds the colour whatever the alpha (the host's lit-screen shader).
                if blend == 2 && surf.material.as_deref().is_some_and(|m| m.contains("mtl_uav_")) {
                    blend = 6;
                }
                let detail_map = surf.material.as_deref().and_then(|m| crate::models::material_textures(m).into_iter().find(|t| t.0 == 3).map(|t| t.2));
                vm.surfaces.push(Surface { index_start: start, index_count: vm.indices.len() as u32 - start, color_map: surf.color_map.clone(), detail_map, blend });
            }
        }
        // The laptop's display layers sit on the screen glass itself: drawn after it (see-through)
        // they lost the depth test to it. Lifted a few mm off the glass along their normals.
        let mut lifted = std::collections::HashSet::new();
        for surf in vm.surfaces.iter().filter(|s| s.blend == 6) {
            for &ix in &vm.indices[surf.index_start as usize..(surf.index_start + surf.index_count) as usize] {
                if lifted.insert(ix) {
                    let (p, n) = (vm.positions[ix as usize], vm.normals[ix as usize]);
                    vm.positions[ix as usize] = [p[0] + n[0] * SCREEN_LIFT_M, p[1] + n[1] * SCREEN_LIFT_M, p[2] + n[2] * SCREEN_LIFT_M];
                }
            }
        }
        let m = axis_matrix(s);
        let mi = m.inverse();
        vm.bindposes = vm.dobj.bones.iter().map(|b| (m * b.bind_world.inverse() * mi).to_cols_array()).collect();
        vm.clips = row
            .xanims
            .iter()
            .map(|n| {
                let c = clip(n.as_deref()?)?;
                let tracks = vm.dobj.tracks_for(&c);
                Some((c, tracks))
            })
            .collect();
        vm.clips.resize(SLOTS, None);
        Some(vm)
    }

    fn has(&self, slot: usize) -> bool {
        self.clips.get(slot).is_some_and(Option::is_some)
    }

    fn duration(&self, slot: usize) -> f32 {
        self.clips.get(slot).and_then(|c| c.as_ref()).map_or(0.0, |(c, _)| c.duration())
    }

    /// Weapon-def timer that sets a slot's playback rate (IW4L weap_anim_rate ANIM_RATE_TABLE);
    /// None = the clip plays at its own speed (fire, last shot, rechamber, ADS fire...).
    fn slot_timer_ms(&self, slot: usize) -> Option<i32> {
        use asset_slots::*;
        let t = &self.timers;
        Some(match slot {
            RELOAD => t.reload_ms,
            RELOAD_EMPTY => t.reload_empty_ms,
            RELOAD_START => t.reload_start_ms,
            RELOAD_END => t.reload_end_ms,
            RAISE | ALT_RAISE | EMPTY_RAISE => t.raise_ms,
            DROP | ALT_DROP | EMPTY_DROP => t.drop_ms,
            QUICK_RAISE => t.quick_raise_ms,
            QUICK_DROP => t.quick_drop_ms,
            MELEE => t.melee_ms,
            MELEE_CHARGE => t.melee_charge_ms,
            SPRINT_IN => t.sprint_raise_ms,
            SPRINT_OUT => t.sprint_drop_ms,
            _ => return None,
        })
    }

    /// A slot this weapon has no clip for falls back to the closest one it does.
    fn resolve_slot(&self, slot: usize) -> Option<usize> {
        use asset_slots::*;
        let chain: &[usize] = match slot {
            LASTSHOT => &[LASTSHOT, FIRE],
            ADS_FIRE => &[ADS_FIRE, FIRE],
            ADS_LASTSHOT => &[ADS_LASTSHOT, ADS_FIRE, LASTSHOT, FIRE],
            ADS_RECHAMBER => &[ADS_RECHAMBER, RECHAMBER],
            RELOAD_EMPTY => &[RELOAD_EMPTY, RELOAD],
            FIRST_RAISE | QUICK_RAISE | EMPTY_RAISE | ALT_RAISE => &[slot, RAISE],
            QUICK_DROP | EMPTY_DROP | ALT_DROP => &[slot, DROP],
            _ => return self.has(slot).then_some(slot),
        };
        chain.iter().copied().find(|&s| self.has(s))
    }

    /// Drive the controller from MW2's own animation state: the weapon code sets the hand's
    /// `weap_anim` (event | restart bit) on every state change, exactly as MW2's viewmodel
    /// reads it. `anim_seq` counts changes so a restarted event replays.
    pub fn advance(&mut self, dt: f32, weap_anim: i32, anim_seq: u32, fire_rate: f32, reload_scale: f32) {
        // Pilot showcase: the clip the pilot holds, at its point; the sim's events pass unplayed.
        if let Some((slot, frac)) = self.forced {
            self.anim_seq = anim_seq;
            let new = self.duration(slot) * frac.clamp(0.0, 1.0);
            // Its sounds as the hold moves on through it (the relay's reloads were silent: mag out,
            // mag in and bolt are notetracks, 10-05-26). A new clip starts where it's picked up.
            let old = match self.action { Some(a) if a.slot == slot => a.time, _ => new };
            if new > old
                && let Some(Some((clip, _))) = self.clips.get(slot)
            {
                for n in clip.crossed_notify_records(old, new) {
                    if crate::sounds::exists(&n.name) {
                        self.notetrack_sounds.push(n.name);
                    }
                }
            }
            self.action = Some(Play { slot, time: new, rate: 0.0, looping: false });
            self.fade = None;
            self.idle_time += dt;
            return;
        }
        let fr = if fire_rate > 0.01 { fire_rate } else { 1.0 };
        if anim_seq != self.anim_seq {
            self.anim_seq = anim_seq;
            let event = weap_anim as u32 & weapon_iw4::WEAP_ANIM_EVENT_MASK;
            self.last_event = event;
            match weapon_iw4::slot_for_weap_anim_event(event).and_then(|s| self.resolve_slot(s)) {
                None => {
                    // Idle blends in over IDLE_INTERRUPT_GOAL_TIME (0.5 s) from wherever the
                    // last action stopped; actions themselves cut in at once (ACTION_GOAL_TIME 0).
                    if let Some(a) = self.action {
                        self.fade = Some((a.slot, a.time.min(self.duration(a.slot)), 0.0));
                    }
                    self.action = None;
                }
                Some(slot) => {
                    self.fade = None;
                    let d = self.duration(slot);
                    let rate = match self.slot_timer_ms(slot) {
                        Some(ms) if ms > 0 && d > 0.0 => d / (ms as f32 / 1000.0) * fr,
                        _ => fr,
                    };
                    // Sleight of Hand runs the reload states faster; the clips keep up (IW4L's
                    // viewmodel controller takes the predicted perks for the same reason).
                    let rate = if matches!(slot, asset_slots::RELOAD | asset_slots::RELOAD_EMPTY | asset_slots::RELOAD_START | asset_slots::RELOAD_END) {
                        rate * reload_scale.max(0.01)
                    } else {
                        rate
                    };
                    // Shell-by-shell reloads restart RELOAD once per shell (the restart bit flips),
                    // so only true loops loop.
                    let looping = matches!(slot, asset_slots::SPRINT_LOOP | asset_slots::STUNNED_LOOP);
                    self.action = Some(Play { slot, time: 0.0, rate, looping });
                }
            }
        }
        if let Some(a) = &mut self.action {
            let old = a.time;
            a.time += dt * a.rate;
            let (slot, new) = (a.slot, a.time);
            if let Some(Some((clip, _))) = self.clips.get(slot) {
                for n in clip.crossed_notify_records(old, new) {
                    // Sound notetracks are alias names (weap_ak47_clipout_plr); the rest
                    // (viewmodel_small rumble, end) aren't sounds.
                    if crate::sounds::exists(&n.name) {
                        self.notetrack_sounds.push(n.name);
                    }
                }
            }
        }
        if let Some(a) = self.action {
            let d = self.duration(a.slot);
            if a.looping && d > 0.0 {
                if let Some(x) = &mut self.action {
                    x.time %= d;
                }
            } else if a.time >= d {
                // Hold the last frame until MW2 starts the next anim (the weapon state may
                // outlast the clip, e.g. a reload timer longer than its clip).
                if let Some(x) = &mut self.action {
                    x.time = d;
                }
            }
        }
        if let Some(f) = &mut self.fade {
            f.2 += dt;
            if f.2 >= weapon_iw4::IDLE_INTERRUPT_GOAL_TIME_SECS {
                self.fade = None;
            }
        }
        self.idle_time += dt;
    }

    /// Pilot showcase: hold `slot` at `frac` of its length (None: let go, fading back to idle).
    /// False when the weapon has no clip in that slot.
    pub fn force(&mut self, slot: Option<usize>, frac: f32) -> bool {
        match slot {
            Some(s) if self.has(s) => {
                // A change of clip (or the hold starting) glides from the pose it had: MW2 blends one
                // anim into the next; held cold, a clip's first frame snapped in (a one-frame SPAS
                // glitch, 10-05-26).
                if self.forced.map(|f| f.0) != Some(s) {
                    self.glide_from_self(self.force_glide);
                }
                self.forced = Some((s, frac));
                true
            }
            Some(_) => false,
            None => {
                if let Some((s, f)) = self.forced.take() {
                    if self.force_glide > 0.2 {
                        // A slow let-go: glide from the held pose back to the idle.
                        self.glide_from_self(self.force_glide);
                        self.fade = None;
                    } else {
                        self.fade = Some((s, self.duration(s) * f.clamp(0.0, 1.0), 0.0));
                    }
                    self.action = None;
                }
                true
            }
        }
    }

    /// Pilot showcase: glide from `old`'s last pose to this one's over `secs`, and take over its idle
    /// clock so idle doesn't restart. The arms glide bone by bone (the same viewhands rig); the gun
    /// moves as one rigid piece from where the old gun's root was (bone by bone, its bolt and mag
    /// came flying in from the old gun's parts, playtest 10-05-26).
    pub fn blend_from(&mut self, old: &Viewmodel, secs: f32) {
        if old.last_pose.len() != old.bone_count() * 7 || secs <= 0.0 {
            return;
        }
        let mut from = HashMap::new();
        for i in 0..old.bone_count() {
            let p = &old.last_pose[i * 7..i * 7 + 7];
            from.insert(old.bone_label(i), [p[0], p[1], p[2], p[3], p[4], p[5], p[6]]);
        }
        self.swap_gun_root = from.get("j_gun").copied();
        self.idle_time = old.idle_time;
        self.swap_from = Some((from, 0.0, secs));
    }

    /// Pilot showcase (motion matching): the point of this gun's `slot` clip (0..1) whose pose is
    /// nearest `old`'s current one - gun root, both wrists and the barrel's turn - so a reload
    /// handed to a different gun carries on from where the hands and gun really are, not from the
    /// same fraction of a different animation (10-05-26: MW2's reloads differ 5-26 inches at the
    /// same fraction; the AK "popped up"). `around` (the current fraction) wins near-ties, so the
    /// reload keeps moving forward. None when either side lacks what's needed.
    pub fn match_phase(&self, slot: usize, old: &Viewmodel, around: f32) -> Option<f32> {
        if !self.has(slot) || old.last_pose.len() != old.bone_count() * 7 {
            return None;
        }
        let (c, tracks) = self.clips.get(slot)?.as_ref()?;
        let names = ["j_gun", "j_wrist_le", "j_wrist_ri"];
        let mine: Vec<usize> = names.iter().map(|n| self.dobj.find(n)).collect::<Option<Vec<_>>>()?;
        let theirs: Vec<usize> = names.iter().map(|n| old.dobj.find(n)).collect::<Option<Vec<_>>>()?;
        let at = |i: usize| -> (Vec3, Quat) {
            let p = &old.last_pose[i * 7..i * 7 + 7];
            (Vec3::new(p[0], p[1], p[2]), Quat::from_xyzw(p[3], p[4], p[5], p[6]).normalize())
        };
        let target: Vec<(Vec3, Quat)> = theirs.iter().map(|&i| at(i)).collect();
        const N: usize = 48;
        let mut best = (f32::MAX, around);
        for k in 0..N {
            let f = k as f32 / N as f32;
            let a = [AnimInstance { clip: c, tracks, time: c.duration() * f, weight: 1.0, parts: None }];
            let world = self.dobj.pose(&a, &self.dobj.all_parts(), Mat4::IDENTITY);
            let eye_inv = world[self.view_bone].inverse();
            let mut score = 0.0;
            for (j, &bi) in mine.iter().enumerate() {
                let (_, r, t) = (eye_inv * world[bi]).to_scale_rotation_translation();
                let p = Vec3::from_array(to_unity(t.to_array(), INCHES_TO_METRES));
                let gap = p.distance(target[j].0);
                score += if j == 0 { gap } else { 0.5 * gap };
                if j == 0 {
                    let q = Quat::from_array(quat_to_unity(r.normalize()));
                    // the barrel's turn: 10 degrees counts like ~1 inch of gap
                    score += q.angle_between(target[0].1) * 0.15;
                }
            }
            let wrap = (f - around).abs().min(1.0 - (f - around).abs());
            score += wrap * 0.05;
            if score < best.0 {
                best = (score, f);
            }
        }
        Some(best.1)
    }

    /// Glide from this rig's own last pose over `secs` (a change of held clip).
    fn glide_from_self(&mut self, secs: f32) {
        if self.last_pose.len() != self.bone_count() * 7 || self.swap_from.is_some() {
            return;
        }
        let mut from = HashMap::new();
        for i in 0..self.bone_count() {
            let p = &self.last_pose[i * 7..i * 7 + 7];
            from.insert(self.bone_label(i), [p[0], p[1], p[2], p[3], p[4], p[5], p[6]]);
        }
        self.swap_gun_root = from.get("j_gun").copied();
        self.swap_from = Some((from, 0.0, secs));
    }

    /// Lay the swap glide over a fresh pose (7 floats a bone), then advance it by `dt`.
    pub fn apply_swap_blend(&mut self, out: &mut [f32], dt: f32) {
        let Some((from, t, secs)) = &mut self.swap_from else { return };
        let u = (*t / *secs).clamp(0.0, 1.0);
        let w = 1.0 - u * u * (3.0 - 2.0 * u); // old pose's weight: 1 -> 0, smoothstep
        let gun_root = self.dobj.find("j_gun");
        let gun_model = gun_root.map(|g| self.dobj.bones[g].model);
        // The gun's rigid move: from its own root pose to the blended root.
        let rigid = match (gun_root, self.swap_gun_root) {
            (Some(g), Some(o)) if out.len() >= g * 7 + 7 => {
                let n = &out[g * 7..g * 7 + 7];
                let (np, nq) = (Vec3::new(n[0], n[1], n[2]), Quat::from_xyzw(n[3], n[4], n[5], n[6]).normalize());
                let (op, oq) = (Vec3::new(o[0], o[1], o[2]), Quat::from_xyzw(o[3], o[4], o[5], o[6]).normalize());
                let bq = nq.slerp(oq, w);
                let bp = np.lerp(op, w);
                let d_rot = bq * nq.inverse();
                Some((d_rot, bp - d_rot * np))
            }
            _ => None,
        };
        for i in 0..self.dobj.bones.len() {
            let Some(b) = out.get_mut(i * 7..i * 7 + 7) else { continue };
            if gun_model.is_some_and(|m| self.dobj.bones[i].model == m) {
                if let Some((d_rot, d_pos)) = rigid {
                    let p = d_rot * Vec3::new(b[0], b[1], b[2]) + d_pos;
                    let q = (d_rot * Quat::from_xyzw(b[3], b[4], b[5], b[6])).normalize();
                    b.copy_from_slice(&[p.x, p.y, p.z, q.x, q.y, q.z, q.w]);
                }
                continue;
            }
            let Some(o) = from.get(&self.dobj.bones[i].name) else { continue };
            for k in 0..3 {
                b[k] += (o[k] - b[k]) * w;
            }
            let nq = Quat::from_xyzw(b[3], b[4], b[5], b[6]).normalize();
            let q = nq.slerp(Quat::from_xyzw(o[3], o[4], o[5], o[6]).normalize(), w);
            b[3..7].copy_from_slice(&[q.x, q.y, q.z, q.w]);
        }
        *t += dt;
        if *t >= *secs {
            self.swap_from = None;
            self.swap_gun_root = None;
        }
    }

    /// A bone's index in the pose by name.
    pub fn find_bone(&self, name: &str) -> Option<usize> {
        self.dobj.find(name)
    }

    /// Play `slot` once from the start (the killstreak trigger's click: the sim never fires the device,
    /// so its clacker squeeze never played, 10-05-26). False when there's no clip there.
    pub fn play_once(&mut self, slot: usize) -> bool {
        if !self.has(slot) {
            return false;
        }
        self.fade = None;
        self.action = Some(Play { slot, time: 0.0, rate: 1.0, looping: false });
        true
    }

    /// A slot's length in MW2 play: its weapon-def timer (reload_ms...) or the clip's own length.
    pub fn slot_seconds(&self, slot: usize) -> f32 {
        if !self.has(slot) {
            return 0.0; // no clip there (a timer alone doesn't make one: magazine guns' reload_start)
        }
        match self.slot_timer_ms(slot) {
            Some(ms) if ms > 0 => ms as f32 / 1000.0,
            _ => self.duration(slot),
        }
    }

    /// Whether this weapon has a clip in a slot (tests).
    pub fn playing_slot_exists(&self, slot: usize) -> bool {
        self.has(slot)
    }

    /// Which clip slot is playing (debug / tests).
    pub fn playing(&self) -> Option<(usize, f32, f32)> {
        self.action.map(|a| (a.slot, a.time, self.duration(a.slot)))
    }

    /// Current pose: per bone Unity-space (position xyz, rotation xyzw) relative to the eye.
    pub fn pose(&self, ads_frac: f32, clip_count: i32, out: &mut [f32]) {
        let mut anims: Vec<AnimInstance<'_>> = Vec::with_capacity(4);
        let base = self.action.map_or_else(
            || {
                let slot = if clip_count == 0 && self.has(EMPTY_IDLE) { EMPTY_IDLE } else { IDLE };
                let d = self.duration(slot).max(1e-3);
                (slot, self.idle_time % d)
            },
            |a| (a.slot, a.time.min(self.duration(a.slot))),
        );
        // Blending back to idle: the frozen action fades out as idle fades in (weights sum to 1).
        let fade = if self.action.is_none() { self.fade } else { None };
        let idle_w = fade.map_or(1.0, |f| (f.2 / weapon_iw4::IDLE_INTERRUPT_GOAL_TIME_SECS).clamp(0.0, 1.0));
        if let Some((slot, time, _)) = fade {
            if let Some((c, tracks)) = self.clips.get(slot).and_then(|c| c.as_ref()) {
                anims.push(AnimInstance { clip: c, tracks, time, weight: 1.0 - idle_w, parts: None });
            }
        }
        if let Some((c, tracks)) = self.clips.get(base.0).and_then(|c| c.as_ref()) {
            anims.push(AnimInstance { clip: c, tracks, time: base.1, weight: if fade.is_some() { idle_w.max(1e-3) } else { 1.0 }, parts: None });
        }
        let scrub = weapon_iw4::ads_overlay_scrub(ads_frac.clamp(0.0, 1.0));
        if let Some((c, tracks)) = self.clips.get(ADS_DOWN).and_then(|c| c.as_ref()) {
            anims.push(AnimInstance { clip: c, tracks, time: c.duration() * scrub.ads_down_time_norm, weight: 1.0, parts: None });
        }
        if let Some((c, tracks)) = self.clips.get(ADS_UP).and_then(|c| c.as_ref()) {
            if scrub.ads_up_weight > 0.0 {
                anims.push(AnimInstance { clip: c, tracks, time: c.duration() * scrub.ads_up_time_norm, weight: scrub.ads_up_weight, parts: None });
            }
        }
        let world = self.dobj.pose(&anims, &self.dobj.all_parts(), Mat4::IDENTITY);
        let eye_inv = world[self.view_bone].inverse();
        for (i, w) in world.iter().enumerate() {
            let rel = eye_inv * *w;
            let (_, r, t) = rel.to_scale_rotation_translation();
            let p = to_unity(t.to_array(), INCHES_TO_METRES);
            let q = quat_to_unity(r.normalize());
            if let Some(slot) = out.get_mut(i * 7..i * 7 + 7) {
                slot.copy_from_slice(&[p[0], p[1], p[2], q[0], q[1], q[2], q[3]]);
            }
        }
    }

    /// A bone's pose (Unity-space position xyz relative to the eye, rotation xyzw) from the last
    /// `mw2_viewmodel_step` (the idle pose at rest if it was never stepped). `tag_flash` falls
    /// back to `tag_flash_silenced` (preferred when this variant shows the gun's silencer, i.e.
    /// `tag_silencer` exists and isn't hidden), then `tag_barrel`.
    pub fn tag(&self, name: &str) -> Option<[f32; 7]> {
        // A variant that shows the gun's silencer attachment (its hide tags don't name it) flashes
        // from the silencer's muzzle.
        let silenced = self.dobj.find("tag_silencer").is_some() && !self.hide_tags.iter().any(|t| t == "tag_silencer");
        let candidates: Vec<&str> = if name == "tag_flash" {
            if silenced { vec!["tag_flash_silenced", "tag_flash", "tag_barrel"] } else { vec!["tag_flash", "tag_flash_silenced", "tag_barrel"] }
        } else {
            vec![name]
        };
        let bone = candidates.into_iter().find_map(|n| self.dobj.find(n))?;
        let rest;
        let pose: &[f32] = if self.last_pose.len() == self.bone_count() * 7 {
            &self.last_pose
        } else {
            let mut buf = vec![0.0; self.bone_count() * 7];
            self.pose(0.0, 1, &mut buf);
            rest = buf;
            &rest
        };
        pose.get(bone * 7..bone * 7 + 7).map(|p| [p[0], p[1], p[2], p[3], p[4], p[5], p[6]])
    }

    /// A bone's name (diagnostics).
    pub fn bone_label(&self, i: usize) -> String {
        self.dobj.bones.get(i).map_or_else(|| format!("#{i}"), |b| b.name.clone())
    }

    pub fn bone_count(&self) -> usize {
        self.dobj.bones.len()
    }

    /// CPU-skin the mesh with a pose from `pose()` (for offline checks): Unity-space
    /// eye-relative vertex positions, exactly what Unity's SkinnedMeshRenderer computes.
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn yaw_left_maps_to_unity_negative_y() {
        let q = Quat::from_rotation_z(std::f32::consts::FRAC_PI_2); // IW: +90 deg yaw = left
        let u = quat_to_unity(q);
        let qu = Quat::from_xyzw(u[0], u[1], u[2], u[3]);
        // In Unity, turning left is a negative rotation about +Y.
        let fwd = qu * Vec3::Z;
        assert!((fwd - Vec3::new(-1.0, 0.0, 0.0)).length() < 1e-5, "{fwd:?}");
        // and the mapping agrees with the point map
        let iw_fwd_rotated = q * Vec3::X; // IW forward turned left -> IW +Y
        let as_unity = to_unity(iw_fwd_rotated.to_array(), 1.0);
        assert!((Vec3::from_array(as_unity) - fwd).length() < 1e-5);
    }
}

#[cfg(test)]
mod attachment_scan {
    /// Prints, per AK-47 variant, its gun model, hide tags and which gun bones end up hidden.
    #[test]
    #[ignore]
    fn acog_surfaces() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        for n in ["m4_acog_mp", "ak47_acog_mp", "scar_acog_mp", "m4_mp"] {
            let w = crate::weapons::index_of(n);
            let row = crate::weapons::row(w).unwrap();
            let gun = super::rig(row.gun_model.as_deref().unwrap()).unwrap();
            eprintln!("== {n}: gun model {:?}", row.gun_model);
            for (i, surf) in gun.surfaces.iter().enumerate() {
                let tex = surf.material.as_deref().map(crate::models::material_textures).unwrap_or_default();
                let tris = surf.index_count / 3;
                let t = surf.color_map.as_deref().and_then(crate::models::texture);
                eprintln!("  surf {i}: material {:?} color_map {:?} fmt {:?} {}x{} bytes {} blend {} tris {tris} textures {:?}", surf.material, surf.color_map, t.as_ref().map(|t| t.format), t.as_ref().map_or(0, |t| t.width), t.as_ref().map_or(0, |t| t.height), t.as_ref().map_or(0, |t| t.top_mip.len()), surf.material.as_deref().map_or(0, crate::models::material_blend), tex);
            }
        }
    }

    #[test]
    #[ignore]
    fn ak47_variant_hide_tags() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        if !std::path::Path::new(COMMON_MP).exists() {
            return;
        }
        let _turn = crate::fx::TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        for a in ["", "_silencer", "_gl", "_reflex", "_acog", "_shotgun"] {
            let n = format!("ak47{a}_mp");
            let w = unsafe { crate::mw2_weapon_index(n.as_ptr(), n.len()) };
            let row = crate::weapons::row(w).unwrap();
            let gun = super::rig(row.gun_model.as_deref().unwrap()).unwrap();
            let hidden = super::hidden_bones(&gun.pose, &row.hide_tags);
            let shown: Vec<&str> = (0..gun.pose.num_bones).filter(|&i| !hidden[i]).map(|i| gun.pose.bone_names[i].as_str()).filter(|b| !b.starts_with("j_")).collect();
            eprintln!("{n}: gun {:?}\n  hide {:?}\n  shown tags {:?}", row.gun_model, row.hide_tags, shown);
        }
        if let Ok(list) = std::env::var("NOTIFY_DUMP") {
            // NOTIFY_DUMP=1 for the usual four, or a comma list of weapons.
            let names: Vec<String> = if list == "1" { ["ak47_mp", "cheytac_mp", "spas12_mp", "m4_mp"].iter().map(|s| s.to_string()).collect() } else { list.split(',').map(str::to_string).collect() };
            for n in names.iter().map(String::as_str) {
                let w = unsafe { crate::mw2_weapon_index(n.as_ptr(), n.len()) };
                let vm = super::Viewmodel::build(w).unwrap();
                for (slot, c) in vm.clips.iter().enumerate() {
                    if let Some((clip, _)) = c {
                        if !clip.notifies.is_empty() {
                            let list: Vec<String> = clip.notifies.iter().map(|x| format!("{}@{:.2}", x.name, x.time)).collect();
                            eprintln!("NOTIFY {n} slot {slot:#x} ({:.2}s): {}", clip.duration(), list.join(" "));
                        }
                    }
                }
            }
        }
        for n in ["ak47_mp", "gl_ak47_mp", "ak47_shotgun_attach_mp", "rpg_mp", "m79_mp", "spas12_mp", "cheytac_mp"] {
            let w = unsafe { crate::mw2_weapon_index(n.as_ptr(), n.len()) };
            let r = crate::weapons::row(w).unwrap();
            eprintln!("reticle {n}: center {:?} side {:?} {:?}", r.reticle_center, r.reticle_side, r.reticle);
        }
        for n in ["ak47_gl_mp", "ak47_shotgun_mp", "ak47_acog_gl_mp", "m4_gl_mp", "m4_shotgun_mp"] {
            let w = unsafe { crate::mw2_weapon_index(n.as_ptr(), n.len()) };
            let alt = crate::weapons::mw2_weapon_alternate(w);
            let back = crate::weapons::mw2_weapon_alternate(alt);
            let r = crate::weapons::row(alt);
            eprintln!("{n} ({w}) -> alt {alt} {:?} raise {:?} -> back {back}", r.as_ref().map(|r| r.name.clone()), crate::weapons::row(w).map(|r| (r.alternate_raise_ms, r.alternate_drop_ms)));
        }
        for n in ["ak47_reflex_mp", "ak47_acog_mp", "ak47_eotech_mp"] {
            let w = unsafe { crate::mw2_weapon_index(n.as_ptr(), n.len()) };
            let vm = super::Viewmodel::build(w).unwrap();
            for (i, s) in vm.surfaces.iter().enumerate().filter(|(_, s)| s.blend >= 2) {
                let t = s.color_map.as_deref().and_then(crate::models::texture);
                eprintln!("{n} vm surface {i}: blend {} color_map {:?} iwi {:?}", s.blend, s.color_map, t.map(|t| (t.width, t.height, t.format)));
            }
        }
        let name = "viewmodel_ak47_tactical";
        let m = unsafe { crate::mw2_model_index(name.as_ptr(), name.len()) };
        eprintln!("static model id {m}");
        let mut info = crate::Mw2ModelInfo::default();
        if m != 0 && unsafe { crate::mw2_model_info(m, &mut info) } == 1 {
            for s in 0..info.surface_count {
                let mut nm = [0u8; 96];
                let blend = unsafe { crate::mw2_model_surface_material(m, s, nm.as_mut_ptr(), 96) };
                eprintln!("  surface {s}: '{}' blend {blend}", String::from_utf8_lossy(&nm).trim_end_matches('\0'));
            }
        }
    }
}

#[cfg(test)]
mod reload_tracks {
    /// WEAPONS=a,b: which bones each weapon's reload clip drives on its viewmodel rig, and how many
    /// of the clip's tracks found no bone (playtest 10-05-26: the FAL's mag floats out, no hands).
    #[test]
    #[ignore = "needs the MW2 install"]
    fn print_reload_tracks() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        // RIGS=word: every viewmodel rig (gun model) name containing it - camo variants.
        if let Ok(word) = std::env::var("RIGS") {
            let rigs = super::RIGS.read().unwrap();
            let v: Vec<&str> = rigs.iter().map(|r| r.name.as_str()).filter(|n| n.contains(&word)).collect();
            eprintln!("RIGS {}: {v:?}", v.len());
            return;
        }
        // XANIMS=word: every viewmodel anim name in the zones containing it.
        if let Ok(word) = std::env::var("XANIMS") {
            if let Some(x) = super::XANIMS.read().unwrap().as_ref() {
                let mut v: Vec<&String> = x.keys().filter(|k| k.contains(&word)).collect();
                v.sort();
                eprintln!("XANIMS {} matching '{word}' of {}: {:?}", v.len(), x.len(), v);
            }
            return;
        }
        // MATS=word: the material -> image names containing it (HUD art lookups).
        if let Ok(word) = std::env::var("MATS") {
            if let Some(m) = crate::images::MATERIAL_IMAGES.read().unwrap().as_ref() {
                let mut v: Vec<_> = m.iter().filter(|(k, i)| k.contains(&word) || i.contains(&word)).collect();
                v.sort();
                for (k, i) in v { eprintln!("MAT {k} -> {i}"); }
            }
            return;
        }
        for n in std::env::var("WEAPONS").unwrap_or("fal_mp,m16_mp".into()).split(',') {
            let w = crate::weapons::index_of(n);
            let row = crate::weapons::row(w).unwrap();
            let vm = super::Viewmodel::build(w).unwrap();
            // CURVES=1: where the gun and both hands are through the reload (eye space, inches), 21
            // samples, one line per weapon: groups the animatic's relay by how alike reloads move.
            if std::env::var("CURVES").is_ok() {
                let slot = if vm.has(super::asset_slots::RELOAD) { super::asset_slots::RELOAD } else { continue };
                let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) else { continue };
                let bones: Vec<Option<usize>> = ["j_gun", "j_wrist_le", "j_wrist_ri"].iter().map(|b| vm.dobj.find(b)).collect();
                let mut row = Vec::new();
                for k in 0..=20 {
                    let t = c.duration() * k as f32 / 20.0;
                    let a = [xmodel_runtime::AnimInstance { clip: c, tracks, time: t, weight: 1.0, parts: None }];
                    let world = vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY);
                    let eye = world[vm.view_bone].inverse();
                    for b in &bones {
                        let p = b.map(|i| (eye * world[i]).w_axis).unwrap_or(glam::Vec4::ZERO);
                        row.push(format!("{:.2},{:.2},{:.2}", p.x, p.y, p.z));
                    }
                    // the gun's barrel direction
                    let fwdv = bones[0].map(|i| (eye * world[i]).x_axis).unwrap_or(glam::Vec4::ZERO);
                    row.push(format!("{:.3},{:.3},{:.3}", fwdv.x, fwdv.y, fwdv.z));
                }
                eprintln!("CURVE {n} {}", row.join(","));
                continue;
            }
            if std::env::var("DUAL").is_ok() {
                let mut pose = vec![0.0f32; vm.bone_count() * 7];
                vm.pose(0.0, 30, &mut pose);
                let g = vm.dobj.find("j_gun").map(|i| [pose[i * 7], pose[i * 7 + 1], pose[i * 7 + 2]]).unwrap_or_default();
                eprintln!("DUAL {n} offset {} idle gun at {:.3} {:.3} {:.3}", vm.dual_offset, g[0], g[1], g[2]);
                continue;
            }
            // KNIFE=1: the tactical knife's bone (tag_knife) per clip - is it animated, and where is it
            // (eye space, inches) at the clip's start and end.
            if std::env::var("KNIFE").is_ok() {
                let kb = vm.dobj.find("tag_knife");
                for slot in [super::asset_slots::IDLE, super::asset_slots::RAISE, super::asset_slots::FIRST_RAISE, super::asset_slots::DROP, super::asset_slots::FIRE, super::asset_slots::MELEE] {
                    let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) else { continue };
                    let bound = c.tracks.iter().zip(tracks).any(|(t, b)| t.name == "tag_knife" && b.is_some());
                    let at = |t: f32| {
                        let a = [xmodel_runtime::AnimInstance { clip: c, tracks, time: t, weight: 1.0, parts: None }];
                        let w = vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY);
                        kb.map(|i| (w[vm.view_bone].inverse() * w[i]).w_axis.truncate()).unwrap_or_default()
                    };
                    eprintln!("KNIFE {n} slot {slot} {} animated {bound}: start {:.1?} end {:.1?}", c.name, at(0.0), at(c.duration()));
                }
                continue;
            }
            // MELEE=1: the gun root and both wrists through the MELEE clip against the idle (eye space,
            // inches): where a held stab can start and end without the pistol popping.
            if std::env::var("MELEE").is_ok() || std::env::var("SLOTPOSE").is_ok() {
                let idle = vm.clips.get(super::asset_slots::IDLE).and_then(|c| c.as_ref());
                let slot: usize = std::env::var("SLOTPOSE").ok().and_then(|v| v.parse().ok()).unwrap_or(super::asset_slots::MELEE);
                let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) else { continue };
                let bones: Vec<Option<usize>> = ["j_gun", "j_wrist_ri", "j_wrist_le"].iter().map(|b| vm.dobj.find(b)).collect();
                let at = |clip: &xmodel_runtime::AnimClip, tr: &_, t: f32| {
                    let a = [xmodel_runtime::AnimInstance { clip, tracks: tr, time: t, weight: 1.0, parts: None }];
                    let w = vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY);
                    let eye = w[vm.view_bone].inverse();
                    bones.iter().map(|b| b.map(|i| (eye * w[i]).w_axis.truncate()).unwrap_or_default()).collect::<Vec<_>>()
                };
                let rest = idle.map(|(ic, it)| at(ic, it, 0.0));
                for k in 0..=20 {
                    let t = c.duration() * k as f32 / 20.0;
                    let p = at(c, tracks, t);
                    let d: Vec<String> = p.iter().enumerate().map(|(i, v)| format!("{:.1}", rest.as_ref().map(|r| (*v - r[i]).length()).unwrap_or(0.0))).collect();
                    eprintln!("MELEE {n} {:.2} gun/ri/le off idle: {}", k as f32 / 20.0, d.join(" "));
                }
                continue;
            }
            // PARTS=1: the gun's moving parts (bolt, charging handle, mag) and the left hand through
            // RELOAD and FIRST_RAISE - how far each is from its rest spot on the gun (inches), 21
            // samples: where a mag check or a chamber check can be cut from MW2's own clips.
            if std::env::var("PARTS").is_ok() {
                let gun_root = vm.dobj.find("j_gun");
                let names: Vec<String> = vm.dobj.bones.iter().map(|b| b.name.clone()).filter(|b| b.contains("bolt") || b.contains("charg") || b.contains("clip") || b.contains("mag") || b == "j_wrist_le").collect();
                eprintln!("PARTS {n} bones {names:?}");
                for slot in [super::asset_slots::RELOAD, super::asset_slots::FIRST_RAISE, super::asset_slots::RAISE] {
                    let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) else { continue };
                    let rest = {
                        let a = [xmodel_runtime::AnimInstance { clip: c, tracks, time: 0.0, weight: 1.0, parts: None }];
                        vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY)
                    };
                    for name in &names {
                        let Some(bi) = vm.dobj.find(name) else { continue };
                        let mut row = Vec::new();
                        for k in 0..=20 {
                            let t = c.duration() * k as f32 / 20.0;
                            let a = [xmodel_runtime::AnimInstance { clip: c, tracks, time: t, weight: 1.0, parts: None }];
                            let w = vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY);
                            let g = gun_root.map(|g| w[g].inverse()).unwrap_or(glam::Mat4::IDENTITY);
                            let g0 = gun_root.map(|g| rest[g].inverse()).unwrap_or(glam::Mat4::IDENTITY);
                            let d = (g * w[bi]).w_axis.truncate() - (g0 * rest[bi]).w_axis.truncate();
                            row.push(format!("{:.1}", d.length()));
                        }
                        eprintln!("PARTS {n} slot {slot} {} ({:.2}s) {name}: {}", c.name, c.duration(), row.join(" "));
                    }
                }
                continue;
            }
            // AUDIT=1: the gun's own sounds (fire, reload, raise...) and the clips a showcase beat needs.
            if std::env::var("AUDIT").is_ok() {
                let missing: Vec<usize> = [super::asset_slots::IDLE, super::asset_slots::FIRE, super::asset_slots::RAISE, super::asset_slots::DROP, super::asset_slots::RELOAD].into_iter().filter(|&s| !vm.has(s)).collect();
                let snd: Vec<usize> = row.variants.iter().map(|v| v.len()).collect();
                eprintln!("AUDIT {n} sounds(fire,last,empty,reload,reload_empty,rstart,rend,rechamber,raise,pullback,melee) {snd:?} missing_clips {missing:?} fire_world {:?}", row.fire_world);
                continue;
            }
            if std::env::var("OVERLAY").is_ok() {
                eprintln!("OVERLAY {n}: {:?}", row.overlay);
                continue;
            }
            if std::env::var("LEFT").is_ok() {
                for slot in [super::asset_slots::IDLE, super::asset_slots::FIRE, super::asset_slots::RAISE] {
                    if let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) {
                        let t: Vec<String> = c.tracks.iter().zip(tracks).filter(|(t, _)| t.name.ends_with("_le") || t.name.contains("_le_") || t.name.contains("tag_")).map(|(t, b)| format!("{}{}", t.name, if b.is_some() { "" } else { "(UNBOUND)" })).collect();
                        eprintln!("LEFT {n} slot {slot} {}: {} tracks, left {t:?}", c.name, c.tracks.len());
                    }
                }
                for b in ["j_shoulder_le", "j_elbow_le", "j_wrist_le", "j_gun", "tag_weapon", "tag_weapon_left"] {
                    match vm.dobj.find(b) { Some(i) => eprintln!("LEFT {n} bone {b}: model {}", vm.dobj.bones[i].model), None => eprintln!("LEFT {n} bone {b}: none") }
                }
                continue;
            }
            if std::env::var("SLOTS").is_ok() {
                for (slot, name) in row.xanims.iter().enumerate() {
                    if let Some(name) = name {
                        let secs = if vm.clips.get(slot).is_some_and(|c| c.is_some()) { vm.duration(slot) } else { -1.0 };
                        eprintln!("SLOT {n} {slot} {name} {secs:.2}s");
                    }
                }
                continue;
            }
            if std::env::var("BOLT").is_ok() {
                let bones: Vec<String> = vm.dobj.bones.iter().map(|b| b.name.clone()).filter(|b| b.contains("bolt") || b.contains("charge") || b.contains("slide")).collect();
                eprintln!("BOLT {n} model bones {bones:?}");
                for slot in [super::asset_slots::FIRE, super::asset_slots::LASTSHOT, super::asset_slots::RECHAMBER, super::asset_slots::IDLE] {
                    let name = row.xanims.get(slot).cloned().flatten();
                    match vm.clips.get(slot).and_then(|c| c.as_ref()) {
                        Some((c, tracks)) => {
                            let t: Vec<String> = c.tracks.iter().zip(tracks).filter(|(t, _)| t.name.contains("bolt") || t.name.contains("charge") || t.name.contains("slide") || t.name.contains("reload") || t.name.contains("cover")).map(|(t, b)| format!("{}{}", t.name, if b.is_some() { "" } else { "(UNBOUND)" })).collect();
                            eprintln!("BOLT {n} slot {slot} {}: {t:?}", c.name);
                        }
                        None => eprintln!("BOLT {n} slot {slot}: no clip ({name:?})"),
                    }
                }
                continue;
            }
            if std::env::var("MODELS").is_ok() {
                for b in ["j_gun", "tag_weapon", "j_wrist_ri", "j_wrist_le", "tag_clip", "j_bolt", "j_shoulder_ri"] {
                    if let Some(i) = vm.dobj.find(b) { eprintln!("MODEL {n} {b}: model {}", vm.dobj.bones[i].model); }
                }
                continue;
            }
            if std::env::var("TIMERS").is_ok() { eprintln!("TIMER {n} reload {} empty {} raise {}", vm.timers.reload_ms, vm.timers.reload_empty_ms, vm.timers.raise_ms); continue; }
            // LENS=1: the see-through surfaces' images - mean colour and alpha, and how much is near clear.
            if std::env::var("LENS").is_ok() {
                for (si, sf) in vm.surfaces.iter().enumerate() {
                    if sf.blend < 2 { continue }
                    let Some(name) = sf.color_map.as_deref() else { continue };
                    let Some(t) = crate::models::texture(name) else { continue };
                    let Some(px) = crate::images::decode(t.format, t.width as usize, t.height as usize, &t.top_mip) else { eprintln!("LENS {n} surf {si} {name}: undecodable"); continue };
                    let k = (px.len() / 4).max(1) as f64;
                    let mean: Vec<f64> = (0..4).map(|c| px.iter().skip(c).step_by(4).map(|&v| v as f64).sum::<f64>() / k).collect();
                    let clear = px.chunks_exact(4).filter(|p| p[3] < 40).count() as f64 / k;
                    let centre = { let (w, h) = (t.width as usize, t.height as usize); let o = ((h / 2) * w + w / 2) * 4; px[o..o + 4].to_vec() };
                    let idx = &vm.indices[sf.index_start as usize..(sf.index_start + sf.index_count) as usize];
                    let (mut lo, mut hi) = ([f32::MAX; 3], [f32::MIN; 3]);
                    let (mut ulo, mut uhi) = ([f32::MAX; 2], [f32::MIN; 2]);
                    for &i in idx { let p = vm.positions[i as usize]; let u = vm.uvs[i as usize]; for c in 0..3 { lo[c] = lo[c].min(p[c]); hi[c] = hi[c].max(p[c]); } for c in 0..2 { ulo[c] = ulo[c].min(u[c]); uhi[c] = uhi[c].max(u[c]); } }
                    let size: Vec<f32> = (0..3).map(|c| hi[c] - lo[c]).collect();
                    eprintln!("LENS {n} surf {si} {name} {}x{} fmt {} blend {}: mean rgba {:.0?} clear {:.2} centre {:?} size {size:.2?} uv {ulo:.2?}-{uhi:.2?}", t.width, t.height, t.format, sf.blend, mean, clear, centre);
                }
                continue;
            }
            if std::env::var("SURF").is_ok() {
                for (si, sf) in vm.surfaces.iter().enumerate() {
                    let mut h: std::collections::BTreeMap<String, usize> = Default::default();
                    let idx = &vm.indices[sf.index_start as usize..(sf.index_start + sf.index_count) as usize];
                    let mut seen = std::collections::HashSet::new();
                    for &i in idx {
                        if !seen.insert(i) { continue }
                        let (bi, bw) = (vm.bone_index[i as usize], vm.bone_weight[i as usize]);
                        let k = (0..4).max_by(|a, b| bw[*a].total_cmp(&bw[*b])).unwrap();
                        *h.entry(vm.bone_label(bi[k] as usize)).or_default() += 1;
                    }
                    let mut v: Vec<_> = h.into_iter().collect();
                    v.sort_by(|a, b| b.1.cmp(&a.1));
                    v.truncate(5);
                    eprintln!("{n} surf {si} {:?} blend {} detail {:?}: {v:?}", sf.color_map, sf.blend, sf.detail_map);
                    if let Some(t) = sf.color_map.as_deref().and_then(crate::models::texture) {
                        eprintln!("    texture {}x{} format {} top_mip {} bytes", t.width, t.height, t.format, t.top_mip.len());
                    }
                }
            }
            for slot in [super::ADS_DOWN, super::ADS_UP, super::IDLE] {
                if let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) {
                    let bound: Vec<&str> = c.tracks.iter().zip(tracks).filter(|(_, b)| b.is_some()).map(|(t, _)| t.name.as_str()).collect();
                    eprintln!("{n} overlay slot {slot} {}: {} bound {:?}", c.name, bound.len(), bound.iter().filter(|b| b.contains("clip") || b.contains("wrist")).collect::<Vec<_>>());
                }
            }
            for slot in [super::asset_slots::RELOAD, super::asset_slots::RELOAD_EMPTY] {
                let name = row.xanims.get(slot).cloned().flatten();
                let Some((c, tracks)) = vm.clips.get(slot).and_then(|c| c.as_ref()) else { eprintln!("{n} slot {slot}: no clip ({name:?})"); continue };
                let bound: Vec<&str> = c.tracks.iter().zip(tracks).filter(|(_, b)| b.is_some()).map(|(t, _)| t.name.as_str()).collect();
                let unbound: Vec<&str> = c.tracks.iter().zip(tracks).filter(|(_, b)| b.is_none()).map(|(t, _)| t.name.as_str()).collect();
                let arms = bound.iter().filter(|b| b.contains("wrist") || b.contains("elbow") || b.contains("shoulder")).count();
                eprintln!("{n} slot {slot} {}: {} tracks, {} bound ({arms} arm), unbound {:?}", c.name, c.tracks.len(), bound.len(), unbound);
                // The clip alone over time: left wrist and mag bones relative to the gun (inches).
                let watch: Vec<(&str, usize)> = ["j_wrist_le", "tag_clip", "tag_clip_02", "j_clip_release"]
                    .iter().filter_map(|b| vm.dobj.find(b).map(|i| (*b, i))).collect();
                let mags: Vec<String> = vm.dobj.bones.iter().map(|b| b.name.clone()).filter(|b| b.contains("mag") || b.contains("clip")).collect();
                eprintln!("  mag/clip bones {mags:?}; notifies {:?}", c.notifies.iter().map(|x| format!("{:.2} {}", x.time, x.name)).collect::<Vec<_>>());
                // DUMP=dir: skinned verts at a few clip times (Unity eye space) + dominant bone, for a plot.
                if let Ok(dir) = std::env::var("DUMP") {
                    for t in [0.0f32, 0.3, 0.5, 0.7, 0.9, 1.1, 1.3] {
                        let a = [xmodel_runtime::AnimInstance { clip: c, tracks, time: t, weight: 1.0, parts: None }];
                        let world = vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY);
                        let eye = world[vm.view_bone].inverse();
                        let mut pose = vec![0.0f32; vm.bone_count() * 7];
                        for (i, w) in world.iter().enumerate() {
                            let (_, r, tr) = (eye * *w).to_scale_rotation_translation();
                            let p = super::to_unity(tr.to_array(), super::INCHES_TO_METRES);
                            let q = super::quat_to_unity(r.normalize());
                            pose[i * 7..i * 7 + 7].copy_from_slice(&[p[0], p[1], p[2], q[0], q[1], q[2], q[3]]);
                        }
                        let verts = vm.skin_cpu(&pose);
                        let mut out = String::new();
                        for (v, (bi, bw)) in verts.iter().zip(vm.bone_index.iter().zip(&vm.bone_weight)) {
                            let k = (0..4).max_by(|a, b| bw[*a].total_cmp(&bw[*b])).unwrap();
                            out += &format!("{},{},{},{}\n", v[0], v[1], v[2], vm.bone_label(bi[k] as usize));
                        }
                        std::fs::write(format!("{dir}/{n}_{slot}_{t:.1}.csv"), out).unwrap();
                    }
                }
                let d = c.duration();
                for k in 0..=40 {
                    let t = d * k as f32 / 40.0;
                    let a = [xmodel_runtime::AnimInstance { clip: c, tracks, time: t, weight: 1.0, parts: None }];
                    let world = vm.dobj.pose(&a, &vm.dobj.all_parts(), glam::Mat4::IDENTITY);
                    let eye = world[vm.view_bone].inverse();
                    let row: Vec<String> = watch.iter().map(|(b, i)| { let p = (eye * world[*i]).w_axis; format!("{b} {:6.1} {:6.1} {:6.1}", p.x, p.y, p.z) }).collect();
                    let gun = world[vm.dobj.find("j_gun").unwrap()].inverse();
                    let rel: Vec<String> = ["tag_clip", "tag_clip_02"].iter().filter_map(|b| vm.dobj.find(b).map(|i| (b, i))).map(|(b, i)| {
                        let (_, r, tr) = (gun * world[i]).to_scale_rotation_translation();
                        let (ax, ang) = r.to_axis_angle();
                        format!("{b} gun-local {:5.1} {:5.1} {:5.1} rot {:5.1} deg about {:4.1} {:4.1} {:4.1}", tr.x, tr.y, tr.z, ang.to_degrees(), ax.x, ax.y, ax.z)
                    }).collect();
                    eprintln!("  t {t:5.2}  {} || {}", row.join(" | "), rel.join(" | "));
                }
            }
        }
    }
}

#[cfg(test)]
mod rig_bones {
    /// RIG=name: a model rig's bones with how many vertices each skins (which part moves what).
    #[test]
    #[ignore = "needs the MW2 install"]
    fn print_rig_bones() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let want = std::env::var("RIG").unwrap_or("sentry_minigun".into());
        let rigs = super::RIGS.read().unwrap();
        for r in rigs.iter().filter(|r| r.name.starts_with(&want)) {
            let mut counts = vec![0usize; r.pose.num_bones];
            for sk in &r.skin {
                if let Some(c) = counts.get_mut(sk.bones[0] as usize) { *c += 1; }
            }
            for (i, n) in r.pose.bone_names.iter().enumerate() {
                eprintln!("RIG {} bone {i} {n}: {} verts", r.name, counts.get(i).copied().unwrap_or(0));
            }
        }
    }
}
