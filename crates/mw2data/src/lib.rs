//! Reads MW2 (IW4) weapon definitions from the user's own `common_mp.ff`.
//!
//! Path: file envelope → drop the auth chunk at the head of every 256-chunk group
//! (signed zones) → zlib inflate → `fastfile_iw4` zone walk, capturing every Weapon
//! asset's `WeaponGeometry` as it streams past. Mirrors IW4L's
//! `asset_transport::zone::parse_iw4_zone_image` without its zip/T5/IW5 baggage.

use fastfile_iw4::{
    AssetLinkSink, AssetSink, AssetType, FILE_PREAMBLE_LEN, MAGIC_AUTH_HEADER, MAX_XFILE_COUNT,
    Ptr, ScriptStrings, Signing, WeaponGeometry, ZonePtr, ZoneStream, load_asset_at_observed, load_zone,
    parse_file_header, parse_zone_header,
};

pub mod character;
pub mod facts;
pub mod fx;
pub mod iwd;
pub mod models;
pub mod rig;
pub mod scripts;
pub mod sounds;

const AUTHED_CHUNK_SIZE: usize = 0x2000;
const AUTHED_CHUNKS_PER_GROUP: usize = 256;

#[derive(Debug)]
pub enum LoadError {
    Io(std::io::Error),
    Header(String),
    Auth,
    Inflate(String),
    Zone(String),
}

impl std::fmt::Display for LoadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            LoadError::Io(e) => write!(f, "read: {e}"),
            LoadError::Header(e) => write!(f, "file header: {e}"),
            LoadError::Auth => write!(f, "signed zone without an auth header"),
            LoadError::Inflate(e) => write!(f, "inflate: {e}"),
            LoadError::Zone(e) => write!(f, "zone walk: {e}"),
        }
    }
}

/// Sound alias-list names a weapon references (player / first-person variants).
#[derive(Clone, Debug, Default)]
pub struct WeaponSoundNames {
    pub fire: Option<String>,
    pub fire_plr: Option<String>,
    pub fire_last_plr: Option<String>,
    pub empty_fire_plr: Option<String>,
    pub reload_plr: Option<String>,
    pub reload_empty_plr: Option<String>,
    pub reload_start_plr: Option<String>,
    pub reload_end_plr: Option<String>,
    pub rechamber_plr: Option<String>,
    pub raise_plr: Option<String>,
    /// Pin pull / equipment pull-back (offhand weapons).
    pub pullback_plr: Option<String>,
    /// The melee swing (`melee_<gun>_swing_plr`).
    pub melee_swipe_plr: Option<String>,
}

/// One weapon as it came out of the zone, with its geometry captured verbatim.
#[derive(Clone, Debug)]
pub struct CapturedWeapon {
    pub name: String,
    pub geometry: WeaponGeometry,
    pub sounds: WeaponSoundNames,
    /// First-person gun model (e.g. `viewmodel_ak47`) and world model, by XModel name.
    pub gun_model: Option<String>,
    pub world_model: Option<String>,
    /// The 37 viewmodel animation names by slot (IDLE 1, FIRE 3, RELOAD 9, ADS_UP 35, ...).
    pub xanims: Vec<Option<String>>,
    /// Akimbo (dual wield): WeaponDef's szXAnimsRightHanded / szXAnimsLeftHanded, 37 slots each.
    pub xanims_right: Vec<Option<String>>,
    pub xanims_left: Vec<Option<String>>,
    /// Bones whose subtree is hidden for this weapon variant (unequipped attachments).
    pub hide_tags: Vec<String>,
    /// HUD icon material (e.g. `hud_ak47`).
    pub hud_icon: Option<String>,
    /// Localize key of the weapon's display name (e.g. `WEAPON_AK47`).
    pub display_name: Option<String>,
    /// MW2's alternate weapon for this variant (underbarrel grenade launcher / shotgun:
    /// `gl_ak47_mp`, `shotgun_attach...`), toggled with +actionslot 3.
    pub alternate_weapon: Option<String>,
    /// Hip crosshair materials (reticleCenter / reticleSide): the side one is drawn four times
    /// around the spread gap, the center one in the middle (grenade launchers use a center).
    pub reticle_center: Option<String>,
    pub reticle_side: Option<String>,
    /// ADS overlay material (sniper scope reticle), if the weapon has one.
    pub overlay: Option<String>,
    /// Effect names (as `mw2_fx_find` takes them): first-person / world muzzle flash and
    /// shell ejects, and the same for the last round in the magazine.
    pub view_flash: Option<String>,
    pub world_flash: Option<String>,
    pub view_shell_eject: Option<String>,
    pub world_shell_eject: Option<String>,
    pub view_last_shot_eject: Option<String>,
    pub world_last_shot_eject: Option<String>,
    /// TracerDef name (see `ModelCapture::tracers`).
    pub tracer: Option<String>,
    /// Projectile (grenade / launched round) and rocket XModel names; the in-flight world model
    /// is `projectile_model` (the rocket model is the one shown loaded in the launcher).
    pub projectile_model: Option<String>,
    pub rocket_model: Option<String>,
    /// Effect names (as `mw2_fx_find` takes them): projectile trail, beacon, ignition, explosion.
    pub proj_trail_fx: Option<String>,
    pub proj_beacon_fx: Option<String>,
    pub proj_ignition_fx: Option<String>,
    pub explosion_fx: Option<String>,
    /// Sound alias names: per IW4 surface type bounce (31 entries), explosion, flight loop, ignition.
    pub bounce_sounds: Vec<Option<String>>,
    pub proj_explosion_sound: Option<String>,
    pub projectile_sound: Option<String>,
    pub proj_ignition_sound: Option<String>,
}

fn read_name(s: &ZoneStream<'_>, ptr: Option<Ptr>) -> Option<String> {
    ptr.and_then(|p| s.cstr(p).ok()).filter(|n| !n.is_empty()).map(str::to_owned)
}

#[derive(Debug, Default)]
pub struct WalkReport {
    pub assets_walked: usize,
    pub assets_total: usize,
    /// Set when the walk stopped early; weapons captured before that point are kept.
    pub stopped: Option<String>,
}

fn dechunk_authed(body: &[u8]) -> Result<Vec<u8>, LoadError> {
    if body.len() < 8 || &body[0..8] != MAGIC_AUTH_HEADER {
        return Err(LoadError::Auth);
    }
    let mut pos = AUTHED_CHUNK_SIZE;
    let mut out = Vec::with_capacity(body.len());
    let mut chunk_in_group = 0usize;
    while pos < body.len() {
        let end = (pos + AUTHED_CHUNK_SIZE).min(body.len());
        if chunk_in_group != 0 {
            out.extend_from_slice(&body[pos..end]);
        }
        pos = end;
        chunk_in_group = (chunk_in_group + 1) % (AUTHED_CHUNKS_PER_GROUP + 1);
    }
    Ok(out)
}

/// File bytes → inflated zone image.
pub fn zone_image(file: &[u8]) -> Result<Vec<u8>, LoadError> {
    let header = parse_file_header(file).map_err(|e| LoadError::Header(format!("{e:?}")))?;
    let body = &file[FILE_PREAMBLE_LEN..];
    let compressed = match header.signing {
        Signing::Unsigned => body.to_vec(),
        Signing::Signed => dechunk_authed(body)?,
    };
    miniz_oxide::inflate::decompress_to_vec_zlib(&compressed)
        .map_err(|e| LoadError::Inflate(format!("{e:?}")))
}

struct Links {
    weapons: Vec<CapturedWeapon>,
    sounds: sounds::SoundCapture,
    models: models::ModelCapture,
    scripts: scripts::ScriptCapture,
    /// Menu script capture state: open frames (branch / handler set), each with the statements
    /// it collected and the (menu, item, kind) they're for.
    menu_frames: Vec<MenuFrame>,
    /// Handler sets by body pointer, for sets the zone shares between items (reuse_menu_script_set).
    menu_sets: std::collections::HashMap<(u8, u32), Vec<scripts::MenuStmt>>,
    /// An itemDef whose handlers are loading (capture_menu_item .. capture_menu_item_layout):
    /// its scripts wait here and go onto the item when its layout lands.
    pending_item: Option<(String, String)>,
    pending_scripts: Vec<(u8, Vec<scripts::MenuStmt>)>,
    /// Temp-block fill after the last asset (where the next asset header starts).
    temp_watermark: usize,
}

struct MenuFrame {
    stmts: Vec<scripts::MenuStmt>,
    /// None: a handler set (body ptr); Some(None): else; Some(Some(cond)): if.
    branch: Option<Option<String>>,
    set_body: Option<(u8, u32)>,
    target: Option<(String, String, u8)>,
}

impl Links {
    /// Put a statement where it belongs: the innermost open frame, else the item / menu it's for.
    fn push_stmt(&mut self, menu: &str, item: &str, kind: u8, st: scripts::MenuStmt) {
        if let Some(f) = self.menu_frames.last_mut() {
            f.target.get_or_insert_with(|| (menu.to_owned(), item.to_owned(), kind));
            f.stmts.push(st);
            return;
        }
        self.place_stmts(menu, item, kind, vec![st]);
    }

    fn place_stmts(&mut self, menu: &str, item: &str, kind: u8, stmts: Vec<scripts::MenuStmt>) {
        if stmts.is_empty() {
            return;
        }
        if self.pending_item.as_ref().is_some_and(|(m, i)| m == menu && i == item) {
            if let Some(e) = self.pending_scripts.iter_mut().find(|(k, _)| *k == kind) {
                e.1.extend(stmts);
            } else {
                self.pending_scripts.push((kind, stmts));
            }
            return;
        }
        if item.is_empty() {
            if let Some(e) = self.scripts.menu_scripts.iter_mut().rev().find(|(m, k, _)| m == menu && *k == kind) {
                e.2.extend(stmts);
            } else {
                self.scripts.menu_scripts.push((menu.to_owned(), kind, stmts));
            }
            return;
        }
        // Item scripts load right after the item's layout: the latest item of that name.
        if let Some(it) = self.scripts.menu_items.iter_mut().rev().find(|i| i.menu == menu && i.name == item) {
            if let Some(e) = it.scripts.iter_mut().find(|(k, _)| *k == kind) {
                e.1.extend(stmts);
            } else {
                it.scripts.push((kind, stmts));
            }
        }
    }

    fn close_frame(&mut self) {
        let Some(f) = self.menu_frames.pop() else { return };
        let stmts = match &f.branch {
            Some(Some(cond)) => vec![scripts::MenuStmt::If(cond.clone(), f.stmts)],
            Some(None) => vec![scripts::MenuStmt::Else(f.stmts)],
            None => {
                if let Some(b) = f.set_body {
                    self.menu_sets.insert(b, f.stmts.clone());
                }
                f.stmts
            }
        };
        if let Some(parent) = self.menu_frames.last_mut() {
            if parent.target.is_none() {
                parent.target = f.target.clone();
            }
            parent.stmts.extend(stmts);
        } else if let Some((m, i, k)) = f.target {
            self.place_stmts(&m, &i, k, stmts);
        }
    }
}

/// A loaded IW4 menu Statement in `hud_iw4::expr` dump form (`op 16 op 80 s:<hex> op 1 ... 20`),
/// read from zone memory (entries and strings are fixed up to offsets once loaded). None if a
/// part was never fixed up (a function operand loaded inline).
fn dump_statement(s: &ZoneStream<'_>, p: Ptr, depth: u32, out: &mut Vec<String>) -> Option<()> {
    if depth > 8 {
        return None;
    }
    let n = s.i32_at(p, 0).ok()?;
    if !(0..=512).contains(&n) {
        return None;
    }
    let entries = match s.ptr_at(p, s.layout(4, 8)).ok()? {
        ZonePtr::Offset(a) => s.resolve_alias(a),
        ZonePtr::Null if n == 0 => return Some(()),
        _ => return None,
    };
    let size = s.layout(12, 24);
    let v = s.layout(4, 8);
    for i in 0..n as usize {
        let e = entries.at(i * size);
        if s.i32_at(e, 0).ok()? as u8 != 1 {
            out.push("op".into());
            out.push(s.i32_at(e, v).ok()?.to_string());
            continue;
        }
        let operand = e.at(v);
        match s.i32_at(operand, 0).ok()? as u8 {
            0 => out.push(s.i32_at(operand, v).ok()?.to_string()),
            1 => out.push(format!("f:{:08x}", s.u32_at(operand, v).ok()?)),
            2 => {
                let text = match s.ptr_at(operand, v).ok()? {
                    ZonePtr::Offset(t) => s.cstr_bytes(s.resolve_alias(t)).ok()?,
                    ZonePtr::Null => b"",
                    _ => return None,
                };
                out.push(format!("s:{}", text.iter().map(|b| format!("{b:02x}")).collect::<String>()));
            }
            3 => match s.ptr_at(operand, v).ok()? {
                ZonePtr::Offset(f) => {
                    out.push("{".into());
                    dump_statement(s, s.resolve_alias(f), depth + 1, out)?;
                    out.push("}".into());
                }
                ZonePtr::Null => {}
                _ => return None,
            },
            other => out.push(other.to_string()),
        }
    }
    Some(())
}

impl Links {
    /// The loader captures a menu's visibility / rect expressions only when it loads their
    /// statement; a menuDef pointing at a statement an earlier menu loaded (MW2 popups share
    /// `localVarFloat(ui_popupYPos) + ui_tabDepth * 20`) is skipped. The stream keeps every
    /// statement's dump, so they are looked up here once the menu has loaded.
    fn recover_shared_menu_exps(&mut self, s: &ZoneStream<'_>) {
        // The asset slot isn't fixed up yet when `loaded` runs; headers are allocated in the
        // temp block in load order, so the menuDef starts at the temp watermark the previous
        // asset left (checked against the name the loader just captured).
        let Some(name) = self.scripts.menus.last().map(|(n, ..)| n.clone()) else { return };
        let base = self.temp_watermark;
        let header = [base, 0].into_iter().flat_map(|b| (0..32).map(move |i| ((b + 3) & !3) + i * 4)).map(|o| Ptr { block: 0, offset: o as u32 }).find(|&p| {
            matches!(s.ptr_at(p, 0), Ok(ZonePtr::Offset(n)) if s.cstr(s.resolve_alias(n)).is_ok_and(|c| c == name))
        });
        let Some(header) = header else { return };
        let dump_at = |field: usize| match s.ptr_at(header, field) {
            Ok(ZonePtr::Offset(stmt)) => {
                let body = s.resolve_alias(stmt);
                s.expr_stmt_get(body).filter(|d| !d.is_empty()).map(str::to_owned).or_else(|| {
                    let mut toks = Vec::new();
                    dump_statement(s, body, 0, &mut toks).map(|()| toks.join(" "))
                })
            }
            _ => None,
        };
        for key in 0..4u32 {
            let k = key as usize;
            if self.scripts.menu_float_exps.iter().any(|(m, kk, _)| *m == name && *kk == key) {
                continue;
            }
            if let Some(d) = dump_at(s.layout(256 + k * 4, 312 + k * 8)) {
                self.scripts.menu_float_exps.push((name.clone(), key, d));
            }
        }
        if !self.scripts.menu_vis_exps.iter().any(|(m, _)| *m == name) {
            if let Some(d) = dump_at(s.layout(224, 264)) {
                self.scripts.menu_vis_exps.push((name, d));
            }
        }
    }
}

impl AssetLinkSink for Links {
    fn linked_asset_name(&self, slot: Ptr) -> Option<&str> {
        self.models.material_name_ref(slot)
    }

    fn loaded(
        &mut self,
        s: &ZoneStream<'_>,
        ty: AssetType,
        slot: Ptr,
        insert_slot: Option<Ptr>,
    ) -> fastfile_iw4::Result<()> {
        self.models.loaded(s, ty, slot, insert_slot);
        if ty == AssetType::Menu {
            self.recover_shared_menu_exps(s);
        }
        self.temp_watermark = s.watermark(0);
        if ty == AssetType::Weapon {
            if let Some(geometry) = s.weapon() {
                if let Some(name) = geometry.name.and_then(|p| s.cstr(p).ok()) {
                    if !name.is_empty() {
                        let g = &geometry;
                        let pick = |plr: Option<Ptr>, npc: Option<Ptr>| read_name(s, plr).or_else(|| read_name(s, npc));
                        let sounds = WeaponSoundNames {
                            fire: read_name(s, g.fire_sound_name),
                            fire_plr: pick(g.fire_sound_player_name, g.fire_sound_name),
                            fire_last_plr: pick(g.fire_last_sound_player_name, g.fire_last_sound_name),
                            empty_fire_plr: pick(g.empty_fire_sound_player_name, g.empty_fire_sound_name),
                            reload_plr: pick(g.reload_sound_player_name, g.reload_sound_name),
                            reload_empty_plr: pick(g.reload_empty_sound_player_name, g.reload_empty_sound_name),
                            reload_start_plr: pick(g.reload_start_sound_player_name, g.reload_start_sound_name),
                            reload_end_plr: pick(g.reload_end_sound_player_name, g.reload_end_sound_name),
                            rechamber_plr: pick(g.rechamber_sound_player_name, g.rechamber_sound_name),
                            raise_plr: pick(g.raise_sound_player_name, g.raise_sound_name),
                            pullback_plr: pick(g.pullback_sound_player_name, g.pullback_sound_name),
                            melee_swipe_plr: pick(g.melee_swipe_sound_player_name, g.melee_swipe_sound_name),
                        };
                        let gun_model = read_name(s, g.gun_xmodel_name);
                        let alternate_weapon = read_name(s, g.alternate_weapon_name);
                        let world_model = read_name(s, g.world_model_name);
                        let names = |arr: Option<_>| -> Vec<Option<String>> {
                            (0..37)
                                .map(|i| {
                                    let arr = arr?;
                                    match s.ptr_at(arr, i * s.pointer_bytes()).ok()? {
                                        ZonePtr::Offset(p) => s.cstr(s.resolve_alias(p)).ok().filter(|n| !n.is_empty()).map(str::to_owned),
                                        _ => None,
                                    }
                                })
                                .collect()
                        };
                        let xanims = names(g.sz_xanims);
                        let xanims_right = names(g.sz_xanims_right);
                        let xanims_left = names(g.sz_xanims_left);
                        let hide_tags = match (g.hide_tags, self.models.strings.as_ref()) {
                            (Some(arr), Some(strings)) => (0..32)
                                .filter_map(|i| s.u16_at(arr, i * 2).ok())
                                .filter(|&sid| sid != 0)
                                .filter_map(|sid| strings.get(s, sid).filter(|n| !n.is_empty()).map(str::to_owned))
                                .collect(),
                            _ => Vec::new(),
                        };
                        let hud_icon = geometry.hud_icon_slot.and_then(|slot| self.models.material_name(slot));
                        let display_name = read_name(s, geometry.display_name);
                        let overlay = geometry.overlay_material_slot.and_then(|slot| self.models.material_name(slot));
                        let reticle_center = geometry.reticle_center_material_slot.and_then(|slot| self.models.material_name(slot));
                        let reticle_side = geometry.reticle_side_material_slot.and_then(|slot| self.models.material_name(slot));
                        let fx = |slot: Option<Ptr>| slot.and_then(|slot| self.models.fx_name(slot));
                        let (view_flash, world_flash) = (fx(geometry.view_flash_slot), fx(geometry.world_flash_slot));
                        let (view_shell_eject, world_shell_eject) = (fx(geometry.view_shell_eject_slot), fx(geometry.world_shell_eject_slot));
                        let (view_last_shot_eject, world_last_shot_eject) = (fx(geometry.view_last_shot_eject_slot), fx(geometry.world_last_shot_eject_slot));
                        let tracer = geometry.tracer_slot.and_then(|slot| self.models.tracer_name(slot));
                        let (proj_trail_fx, proj_beacon_fx) = (fx(geometry.proj_trail_slot), fx(geometry.proj_beacon_slot));
                        let (proj_ignition_fx, explosion_fx) = (fx(geometry.proj_ignition_slot), fx(geometry.explosion_slot));
                        let bounce_sounds = geometry.bounce_sound_names.iter().map(|p| read_name(s, *p)).collect();
                        self.weapons.push(CapturedWeapon {
                            name: name.to_owned(),
                            geometry,
                            sounds,
                            gun_model,
                            world_model,
                            xanims,
                            xanims_right,
                            xanims_left,
                            hide_tags,
                            hud_icon,
                            display_name,
                            alternate_weapon,
                            reticle_center,
                            reticle_side,
                            overlay,
                            view_flash,
                            world_flash,
                            view_shell_eject,
                            world_shell_eject,
                            view_last_shot_eject,
                            world_last_shot_eject,
                            tracer,
                            projectile_model: read_name(s, g.projectile_model_name),
                            rocket_model: read_name(s, g.rocket_model_name),
                            proj_trail_fx,
                            proj_beacon_fx,
                            proj_ignition_fx,
                            explosion_fx,
                            bounce_sounds,
                            proj_explosion_sound: read_name(s, g.proj_explosion_sound_name),
                            projectile_sound: read_name(s, g.projectile_sound_name),
                            proj_ignition_sound: read_name(s, g.proj_ignition_sound_name),
                        });
                    }
                }
            }
        }
        Ok(())
    }

    fn capture_localize(&mut self, name: &str, value: &[u8]) -> fastfile_iw4::Result<()> {
        let end = value.iter().position(|&b| b == 0).unwrap_or(value.len());
        self.scripts.localize.push((name.to_owned(), String::from_utf8_lossy(&value[..end]).into_owned()));
        Ok(())
    }

    fn capture_raw_file(&mut self, name: &str, data: &[u8], zlib_compressed: bool) -> fastfile_iw4::Result<()> {
        self.scripts.raw(name, data, zlib_compressed);
        Ok(())
    }

    fn capture_font(&mut self, rec: &fastfile_iw4::FontCapture<'_>) -> fastfile_iw4::Result<()> {
        let glyphs = rec
            .glyphs
            .chunks_exact(24)
            .filter_map(|row| row.try_into().ok().map(fastfile_iw4::GlyphCapture::from_row))
            .collect();
        self.scripts.fonts.push(scripts::Font { name: rec.name.to_owned(), pixel_height: rec.pixel_height, material: rec.material.to_owned(), glyphs });
        Ok(())
    }

    fn capture_menu_def(&mut self, rec: &fastfile_iw4::MenuDefCapture<'_>) -> fastfile_iw4::Result<()> {
        let r = rec.rect;
        self.scripts.menus.push((rec.name.to_owned(), [r.x, r.y, r.w, r.h], r.horz_align, r.vert_align));
        self.scripts.menu_expr_dvars.push((rec.name.to_owned(), rec.expr_dvars.to_owned()));
        if rec.fullscreen != 0 {
            self.scripts.menu_fullscreen.push(rec.name.to_owned());
        }
        Ok(())
    }

    fn capture_menu_item_layout(&mut self, rec: &fastfile_iw4::MenuItemLayout<'_>) -> fastfile_iw4::Result<()> {
        let r = rec.rect;
        self.scripts.menu_items.push(scripts::MenuItem {
            menu: rec.menu.to_owned(),
            name: rec.name.to_owned(),
            text: String::from_utf8_lossy(rec.text).trim_end_matches(' ').to_owned(),
            item_type: rec.item_type,
            style: rec.style,
            owner_draw: rec.owner_draw,
            rect: [r.x, r.y, r.w, r.h],
            horz_align: r.horz_align,
            vert_align: r.vert_align,
            fore_color: rec.fore_color,
            back_color: rec.back_color,
            glow_color: rec.glow_color,
            text_scale: rec.text_scale,
            font: rec.font_enum,
            text_align: rec.text_align_mode,
            text_align_x: rec.text_align_x,
            text_align_y: rec.text_align_y,
            text_style: rec.text_style,
            background: rec.background.to_owned(),
            dvar: rec.dvar.to_owned(),
            vis_exp: String::new(),
            text_exp: String::new(),
            material_exp: String::new(),
            float_exps: Vec::new(),
            static_flags: rec.static_flags,
            scripts: std::mem::take(&mut self.pending_scripts),
            disabled_exp: String::new(),
        });
        self.pending_item = None;
        Ok(())
    }

    fn capture_menu_item(&mut self, menu: &str, item: &str, _text: &[u8], _owner_draw: i32, _item_type: i32) -> fastfile_iw4::Result<()> {
        self.pending_item = Some((menu.to_owned(), item.to_owned()));
        self.pending_scripts.clear();
        Ok(())
    }

    fn capture_menu_script(&mut self, menu: &str, item: &str, kind: fastfile_iw4::MenuScriptKind, script: &str) -> fastfile_iw4::Result<()> {
        self.push_stmt(menu, item, scripts::script_kind_byte(kind), scripts::MenuStmt::Script(script.to_owned()));
        Ok(())
    }

    fn capture_menu_set_local_var(&mut self, menu: &str, item: &str, kind: fastfile_iw4::MenuScriptKind, var_kind: i32, name: &str, expr: &str) -> fastfile_iw4::Result<()> {
        let st = scripts::MenuStmt::SetLocal { kind: var_kind as u8, name: name.to_owned(), expr: expr.to_owned() };
        self.push_stmt(menu, item, scripts::script_kind_byte(kind), st);
        Ok(())
    }

    fn begin_menu_event_branch(&mut self, menu: &str, item: &str, kind: fastfile_iw4::MenuScriptKind, condition: Option<&str>) -> fastfile_iw4::Result<()> {
        let target = Some((menu.to_owned(), item.to_owned(), scripts::script_kind_byte(kind)));
        self.menu_frames.push(MenuFrame { stmts: Vec::new(), branch: Some(condition.map(str::to_owned)), set_body: None, target });
        Ok(())
    }

    fn end_menu_event_branch(&mut self, _menu: &str, _item: &str, _kind: fastfile_iw4::MenuScriptKind) -> fastfile_iw4::Result<()> {
        self.close_frame();
        Ok(())
    }

    fn begin_menu_script_set(&mut self, body: Ptr) {
        self.menu_frames.push(MenuFrame { stmts: Vec::new(), branch: None, set_body: Some((body.block, body.offset)), target: None });
    }

    fn end_menu_script_set(&mut self, _insert_slot: Option<Ptr>) {
        self.close_frame();
    }

    fn reuse_menu_script_set(&mut self, body: Ptr, menu: &str, item: &str, kind: fastfile_iw4::MenuScriptKind) -> fastfile_iw4::Result<()> {
        if let Some(stmts) = self.menu_sets.get(&(body.block, body.offset)).cloned() {
            let k = scripts::script_kind_byte(kind);
            if let Some(f) = self.menu_frames.last_mut() {
                f.target.get_or_insert_with(|| (menu.to_owned(), item.to_owned(), k));
                f.stmts.extend(stmts);
            } else {
                self.place_stmts(menu, item, k, stmts);
            }
        }
        Ok(())
    }

    fn capture_menu_visible_exp(&mut self, menu: &str, dump: &str) -> fastfile_iw4::Result<()> {
        self.scripts.menu_vis_exps.push((menu.to_owned(), dump.to_owned()));
        Ok(())
    }

    fn capture_menu_float_exp(&mut self, menu: &str, key: u32, dump: &str) -> fastfile_iw4::Result<()> {
        self.scripts.menu_float_exps.push((menu.to_owned(), key, dump.to_owned()));
        Ok(())
    }

    fn capture_item_visible_exp(&mut self, menu: &str, item: &str, dump: &str) -> fastfile_iw4::Result<()> {
        if let Some(it) = self.scripts.menu_items.last_mut().filter(|i| i.menu == menu && i.name == item) {
            it.vis_exp = dump.to_owned();
        }
        Ok(())
    }

    fn capture_item_disabled_exp(&mut self, menu: &str, item: &str, dump: &str) -> fastfile_iw4::Result<()> {
        if let Some(it) = self.scripts.menu_items.last_mut().filter(|i| i.menu == menu && i.name == item) {
            it.disabled_exp = dump.to_owned();
        }
        Ok(())
    }

    fn capture_item_float_exp(&mut self, menu: &str, item: &str, key: u32, dump: &str) -> fastfile_iw4::Result<()> {
        if let Some(it) = self.scripts.menu_items.last_mut().filter(|i| i.menu == menu && i.name == item) {
            it.float_exps.push((key, dump.to_owned()));
        }
        Ok(())
    }

    fn capture_item_text_exp(&mut self, menu: &str, item: &str, dump: &str) -> fastfile_iw4::Result<()> {
        if let Some(it) = self.scripts.menu_items.last_mut().filter(|i| i.menu == menu && i.name == item) {
            it.text_exp = dump.to_owned();
        }
        Ok(())
    }

    fn capture_item_material_exp(&mut self, menu: &str, item: &str, dump: &str) -> fastfile_iw4::Result<()> {
        if let Some(it) = self.scripts.menu_items.last_mut().filter(|i| i.menu == menu && i.name == item) {
            it.material_exp = dump.to_owned();
        }
        Ok(())
    }

    fn capture_string_table(&mut self, s: &ZoneStream<'_>, header: Ptr) -> fastfile_iw4::Result<()> {
        self.scripts.table(s, header);
        Ok(())
    }

    fn alias(&mut self, ty: AssetType, slot: Ptr, target: Ptr) -> fastfile_iw4::Result<()> {
        self.models.alias(ty, slot, target);
        Ok(())
    }

    fn capture_fx(&mut self, s: &ZoneStream<'_>, geometry: fastfile_iw4::FxEffectDefGeometry) -> fastfile_iw4::Result<()> {
        let mut fx = std::mem::take(&mut self.models.fx);
        let before = fx.effects.len();
        let header = geometry.header;
        let models = &self.models;
        fx.capture(s, geometry, &|slot| models.fx_material(slot));
        self.models.fx = fx;
        self.models.fx_captured(before, header);
        Ok(())
    }

    fn capture_tracer(&mut self, s: &ZoneStream<'_>, geometry: fastfile_iw4::TracerDefGeometry) -> fastfile_iw4::Result<()> {
        self.models.capture_tracer(s, geometry);
        Ok(())
    }

    fn capture_impact_fx(&mut self, s: &ZoneStream<'_>, geometry: fastfile_iw4::FxImpactTableGeometry) -> fastfile_iw4::Result<()> {
        self.models.capture_impact_table(s, geometry);
        Ok(())
    }

    fn capture_xanim(&mut self, s: &ZoneStream<'_>, geometry: fastfile_iw4::XAnimPartsGeometry) -> fastfile_iw4::Result<()> {
        if let Some(strings) = self.models.strings.as_ref() {
            let wanted = geometry.name.and_then(|p| s.cstr(p).ok()).is_some_and(|n| n.starts_with("viewmodel_") || self.models.xanim_filter.is_some_and(|f| f(n)));
            if wanted {
                match rig::capture_xanim(s, strings, &geometry) {
                    Some(parts) => {
                        self.models.rig.xanims.insert(parts.name.clone(), parts);
                    }
                    None => self.models.rig.failed_xanims += 1,
                }
            }
        }
        Ok(())
    }

    fn remember_xmodel_surfaces(&mut self, slot: Ptr, surfaces: Ptr) {
        self.models.remember_xmodel_surfaces(slot, surfaces);
    }

    fn remember_xmodel_surface_name(&mut self, slot: Ptr, name: Ptr) {
        self.models.remember_xmodel_surface_name(slot, name);
    }

    fn xmodel_surface_name(&self, slot: Ptr) -> Option<Ptr> {
        self.models.xmodel_surface_name(slot)
    }

    fn xmodel_surfaces(&self, slot: Ptr) -> Option<Ptr> {
        self.models.xmodel_surfaces(slot)
    }

    fn xmodel_name_ptr(&self, slot: Ptr) -> Option<Ptr> {
        self.models.xmodel_name_ptr(slot)
    }

    fn remember_xmodel_name(&mut self, slot: Ptr, insert_slot: Option<Ptr>, name: Ptr) {
        self.models.remember_xmodel_name(slot, insert_slot, name);
    }

    fn capture_loaded_sound(&mut self, s: &ZoneStream<'_>, header: Ptr, pcm: Ptr, data_len: usize) -> fastfile_iw4::Result<()> {
        self.sounds.capture_loaded_sound(s, header, pcm, data_len)
    }

    fn bind_last_loaded_to_sound_file(&mut self, file: Ptr) -> fastfile_iw4::Result<()> {
        self.sounds.bind_last_loaded_to_sound_file(file);
        Ok(())
    }

    fn bind_streamed_sound_file(&mut self, file: Ptr, dir: &str, name: &str) -> fastfile_iw4::Result<()> {
        self.sounds.bind_streamed_sound_file(file, dir, name);
        Ok(())
    }

    fn capture_sound(&mut self, s: &ZoneStream<'_>, list: Ptr, count: usize, head: Option<Ptr>) -> fastfile_iw4::Result<()> {
        self.sounds.capture_sound(s, list, count, head)
    }
}

struct Sink {
    links: Links,
    walked: usize,
    total: usize,
}

impl AssetSink for Sink {
    fn set_script_strings(&mut self, strings: ScriptStrings) {
        self.links.models.strings = Some(strings);
    }

    fn begin_assets(&mut self, count: usize) {
        self.total = count;
    }

    fn load_asset(
        &mut self,
        s: &mut ZoneStream<'_>,
        _index: usize,
        ty: AssetType,
        slot: Ptr,
    ) -> fastfile_iw4::Result<()> {
        load_asset_at_observed(s, ty, slot, &mut self.links)?;
        self.walked += 1;
        Ok(())
    }
}

/// Walk a zone image, capturing weapons and sounds.
pub fn walk_image(image: &[u8]) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture), LoadError> {
    walk_image_with_models(image, Some(|_| false)).map(|(w, r, s, _)| (w, r, s))
}

/// Walk a zone image, also capturing every XModel whose name passes `model_filter`.
pub fn walk_image_with_models(
    image: &[u8],
    model_filter: Option<fn(&str) -> bool>,
) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture, models::ModelCapture), LoadError> {
    walk_image_full(image, model_filter, None)
}

/// Walk a zone image; models passing `rig_filter` also get skeletons/skins, and every
/// `viewmodel_*` XAnim is captured.
pub fn walk_image_full(
    image: &[u8],
    model_filter: Option<fn(&str) -> bool>,
    rig_filter: Option<fn(&str) -> bool>,
) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture, models::ModelCapture), LoadError> {
    let mut models = models::ModelCapture::with_filter(model_filter);
    models.rig_filter = rig_filter;
    let (links, report) = walk_links(image, models)?;
    Ok((links.weapons, report, links.sounds, links.models))
}

/// Walk a zone image for its rawfiles (GSC source) and string tables only.
pub fn scripts_from_file(path: &std::path::Path) -> Result<(scripts::ScriptCapture, WalkReport), LoadError> {
    let file = std::fs::read(path).map_err(LoadError::Io)?;
    let (links, report) = walk_links(&zone_image(&file)?, models::ModelCapture::with_filter(Some(|_| false)))?;
    let mut scripts = links.scripts;
    scripts.material_images = links.models.material_images();
    scripts.material_state_bits = links.models.material_state_bits();
    Ok((scripts, report))
}

fn walk_links(image: &[u8], models: models::ModelCapture) -> Result<(Links, WalkReport), LoadError> {
    let header = parse_zone_header(image).map_err(|e| LoadError::Zone(format!("header: {e:?}")))?;
    let mut blocks: [Vec<u8>; MAX_XFILE_COUNT] =
        std::array::from_fn(|i| vec![0u8; header.block_size[i] as usize]);
    let mut insert_map = vec![0u8; ZoneStream::insert_map_len(&header)];
    let [b0, b1, b2, b3, b4, b5, b6, b7] = &mut blocks;
    let views: [&mut [u8]; MAX_XFILE_COUNT] = [
        b0.as_mut_slice(),
        b1.as_mut_slice(),
        b2.as_mut_slice(),
        b3.as_mut_slice(),
        b4.as_mut_slice(),
        b5.as_mut_slice(),
        b6.as_mut_slice(),
        b7.as_mut_slice(),
    ];
    let mut stream = ZoneStream::new(image, views, &mut insert_map)
        .map_err(|e| LoadError::Zone(format!("stream: {e:?}")))?;
    let links = Links { weapons: Vec::new(), sounds: sounds::SoundCapture::default(), models, scripts: scripts::ScriptCapture::default(), menu_frames: Vec::new(), menu_sets: Default::default(), pending_item: None, pending_scripts: Vec::new(), temp_watermark: 0 };
    let mut sink = Sink { links, walked: 0, total: 0 };
    let walk = load_zone(&mut stream, &mut sink);
    let report = WalkReport {
        assets_walked: sink.walked,
        assets_total: sink.total,
        stopped: walk.err().map(|e| format!("{e:?}")),
    };
    let mut links = sink.links;
    share_weapdef_fields(&mut links.weapons);
    Ok((links, report))
}

/// Attachment variants share one WeaponDef (e.g. every ak47_*reflex* combo); the zone carries its
/// model / anim / sound / effect pointers only once, so the other variants read them empty. Fill
/// each variant's empty fields from a sibling with the same WeaponDef pointer (IW4L
/// asset_game::weapon_catalog does the same). Per-variant data (hide tags, names) stays its own.
pub fn share_weapdef_fields(weapons: &mut [CapturedWeapon]) {
    use std::collections::HashMap;
    let mut by_def: HashMap<(u8, u32), Vec<usize>> = HashMap::new();
    for (i, w) in weapons.iter().enumerate() {
        if let Some(d) = w.geometry.weap_def {
            by_def.entry((d.block, d.offset)).or_default().push(i);
        }
    }
    fn opt(dst: &mut Option<String>, src: &Option<String>) {
        if dst.is_none() {
            dst.clone_from(src);
        }
    }
    for group in by_def.values().filter(|g| g.len() > 1) {
        for &i in group {
            for &j in group {
                if i == j {
                    continue;
                }
                let src = weapons[j].clone();
                let w = &mut weapons[i];
                opt(&mut w.gun_model, &src.gun_model);
                opt(&mut w.world_model, &src.world_model);
                if w.xanims.iter().all(Option::is_none) {
                    w.xanims.clone_from(&src.xanims);
                }
                opt(&mut w.hud_icon, &src.hud_icon);
                opt(&mut w.overlay, &src.overlay);
                opt(&mut w.reticle_center, &src.reticle_center);
                opt(&mut w.reticle_side, &src.reticle_side);
                opt(&mut w.view_flash, &src.view_flash);
                opt(&mut w.world_flash, &src.world_flash);
                opt(&mut w.view_shell_eject, &src.view_shell_eject);
                opt(&mut w.world_shell_eject, &src.world_shell_eject);
                opt(&mut w.view_last_shot_eject, &src.view_last_shot_eject);
                opt(&mut w.world_last_shot_eject, &src.world_last_shot_eject);
                opt(&mut w.tracer, &src.tracer);
                opt(&mut w.projectile_model, &src.projectile_model);
                opt(&mut w.rocket_model, &src.rocket_model);
                opt(&mut w.proj_trail_fx, &src.proj_trail_fx);
                opt(&mut w.proj_beacon_fx, &src.proj_beacon_fx);
                opt(&mut w.proj_ignition_fx, &src.proj_ignition_fx);
                opt(&mut w.explosion_fx, &src.explosion_fx);
                if w.bounce_sounds.iter().all(Option::is_none) {
                    w.bounce_sounds.clone_from(&src.bounce_sounds);
                }
                opt(&mut w.proj_explosion_sound, &src.proj_explosion_sound);
                opt(&mut w.projectile_sound, &src.projectile_sound);
                opt(&mut w.proj_ignition_sound, &src.proj_ignition_sound);
                let (d, f) = (&mut w.sounds, &src.sounds);
                opt(&mut d.fire, &f.fire);
                opt(&mut d.fire_plr, &f.fire_plr);
                opt(&mut d.fire_last_plr, &f.fire_last_plr);
                opt(&mut d.empty_fire_plr, &f.empty_fire_plr);
                opt(&mut d.reload_plr, &f.reload_plr);
                opt(&mut d.reload_empty_plr, &f.reload_empty_plr);
                opt(&mut d.reload_start_plr, &f.reload_start_plr);
                opt(&mut d.reload_end_plr, &f.reload_end_plr);
                opt(&mut d.rechamber_plr, &f.rechamber_plr);
                opt(&mut d.raise_plr, &f.raise_plr);
                opt(&mut d.pullback_plr, &f.pullback_plr);
                opt(&mut d.melee_swipe_plr, &f.melee_swipe_plr);
            }
        }
    }
}

/// Like `walk_file_full`, also capturing XAnims whose name passes `xanim_filter` (besides `viewmodel_*`).
pub fn walk_file_rig(
    path: &std::path::Path,
    model_filter: Option<fn(&str) -> bool>,
    rig_filter: Option<fn(&str) -> bool>,
    xanim_filter: Option<fn(&str) -> bool>,
) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture, models::ModelCapture), LoadError> {
    let file = std::fs::read(path).map_err(LoadError::Io)?;
    let mut models = models::ModelCapture::with_filter(model_filter);
    models.rig_filter = rig_filter;
    models.xanim_filter = xanim_filter;
    let (links, report) = walk_links(&zone_image(&file)?, models)?;
    Ok((links.weapons, report, links.sounds, links.models))
}

pub fn walk_file_full(
    path: &std::path::Path,
    model_filter: Option<fn(&str) -> bool>,
    rig_filter: Option<fn(&str) -> bool>,
) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture, models::ModelCapture), LoadError> {
    let file = std::fs::read(path).map_err(LoadError::Io)?;
    walk_image_full(&zone_image(&file)?, model_filter, rig_filter)
}

pub fn walk_file_with_models(
    path: &std::path::Path,
    model_filter: Option<fn(&str) -> bool>,
) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture, models::ModelCapture), LoadError> {
    let file = std::fs::read(path).map_err(LoadError::Io)?;
    walk_image_with_models(&zone_image(&file)?, model_filter)
}

/// Walk a zone image and return every weapon it defines.
pub fn weapons_from_image(image: &[u8]) -> Result<(Vec<CapturedWeapon>, WalkReport), LoadError> {
    walk_image(image).map(|(w, r, _)| (w, r))
}

pub fn weapons_from_file(path: &std::path::Path) -> Result<(Vec<CapturedWeapon>, WalkReport), LoadError> {
    let file = std::fs::read(path).map_err(LoadError::Io)?;
    weapons_from_image(&zone_image(&file)?)
}

/// Full walk: weapons, report and every sound the zone carries.
pub fn walk_file(path: &std::path::Path) -> Result<(Vec<CapturedWeapon>, WalkReport, sounds::SoundCapture), LoadError> {
    let file = std::fs::read(path).map_err(LoadError::Io)?;
    walk_image(&zone_image(&file)?)
}
