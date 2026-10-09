//! MW2's in-match HUD, laid out the way IW4 lays it out and handed to the host as draw
//! commands.
//!
//! The layout data is MW2's own: the menu itemDefs of `weaponbar_hd`, `dpad_hd`, `xpbar_hd`
//! and `hold_breath_hint` (rects, alignment, colours, fonts, materials) plus their
//! visibility / rect / text expressions, all read from the user's `common_mp.ff` during
//! the existing zone walk (`killstreaks::load` calls [`ingest`]). Expressions run on IW4L's
//! `hud_iw4::expr` evaluator (Apache-2.0) with a host fed by [`Mw2HudState`], so what shows
//! and when is decided by MW2's expressions, not by guesses here.
//!
//! Ported from IW4L (Apache-2.0), file references are in the crate docs of each function:
//! * `hud_iw4::scrplace` — `ScreenPlacement::apply_rect` (used as a dependency, not copied):
//!   the horizontal / vertical alignment modes (4 = fullscreen, 8 = adjustable min, 10 =
//!   adjustable max, ...) and the 640x480 virtual space with the 4:3 sub-screen.
//! * `hud/src/chrome.rs` — item painting (`paint_item`, `evaluate_item_style`, `push_stretch`,
//!   `paint_text`, `push_owner_text*`, `push_owner_pic`), menu float expressions.
//! * `hud/src/ammo.rs` + `hud_iw4/src/ammo.rs` — clip pips, stock digits, low-ammo warning.
//! * `hud/src/weaponbar.rs` — owner-draw dispatch, offhand icons, compass ring, action slots.
//! * `hud/src/breath_hint.rs` — the hold-breath hint text.
//!
//! Draw-command contract for the host (see [`Mw2HudCmd`]): rects are screen pixels, y down.
//! Kind 0 quads are `material` stretched over `rect` with `uv` and tinted by `color`; a non-zero
//! `rotation_deg` is IW4's *RotateST*: the quad stays axis aligned and the texture is rotated
//! about the centre of `uv` (use [`mw2_hud_rotate_st`] for the four corner UVs). Kind 1 is text:
//! `rect.xy` is the pen origin (left, baseline) with alignment already applied, `rect.zw` the
//! glyph scale in screen pixels per font pixel (x, y), `uv[0..2]` the measured width / em height
//! in pixels, `font` the IW4 font enum (resolved, never 0/1), `_pad` the IW4 text style, and
//! the string is `mw2_hud_cmd_string(material)`. Kind 2 is a solid colour quad.

use std::collections::HashMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, RwLock};

use hud_iw4::{
    AmmoCounterClipKind, CLIP_PIP_EMPTY_ALPHA, CLIP_PIP_EMPTY_RGB, ExprError, ExprHost,
    LowAmmoWarningQuery, Operand, SAFE_AREA_DEFAULT, ScreenPlacement, Statement, WEAPON_NAME_FADE_DURATION_MS,
    WEAPON_NAME_FADE_TAIL_MS, WeaponLockView, ammo_counter_clip_kind, clip_pip_belt_xy,
    clip_pip_grid_xy, clip_pip_metrics, draw_player_weapon_low_ammo_warning, fade_color,
    hint_replace_bind, item_text_origin, low_ammo_warning_color_pair, low_ammo_warning_pulse_frac,
    next_letter, normalized_text_scale, ui_get_font_handle, ui_text_height, vec4_lerp,
};
use mw2data::scripts::{ScriptCapture, StringTable};

/// Menus the in-match HUD is built from, in draw order.
/// (javelin_overlay_hd: the Javelin's CLU, shown only aimed in with it - its items are
/// `adsjavelin()`-gated.)
pub const HUD_MENUS: [&str; 5] = ["weaponbar_hd", "dpad_hd", "xpbar_hd", "hold_breath_hint", "javelin_overlay_hd"];

/// The ride killstreaks' own HUD menus (`mw2_hud_set_ride` 1..3): the Predator's missile camera,
/// the AC-130 and the Chopper Gunner. While one is up it's the only menu drawn, as in MW2.
pub const RIDE_MENUS: [&str; 3] = ["missilecam_hud_hd", "ac130_hud_hd", "remote_chopper_overlay_hd"];

// The C# side checks Marshal.SizeOf against these at startup.
const _: () = assert!(size_of::<Mw2HudState>() == 72);
const _: () = assert!(size_of::<Mw2HudCmd>() == 64);

/// Game state the HUD needs for one frame.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Mw2HudState {
    pub screen_w: f32,
    pub screen_h: f32,
    pub time_ms: i32,
    /// `mw2_weapon_index` ids: the held weapon and the second one (akimbo / alternate), 0 = none.
    pub weapon: u32,
    pub alt_weapon: u32,
    pub clip: i32,
    pub clip_size: i32,
    pub stock: i32,
    /// Second hand's clip (akimbo). The second pip row shows when `alt_clip_size > 0`.
    pub alt_clip: i32,
    pub alt_clip_size: i32,
    pub frags: i32,
    pub smokes: i32,
    pub yaw_deg: f32,
    pub ads_frac: f32,
    /// IW4 weapon state (WEAPON_RELOADING etc.); reload states silence the low-ammo warning.
    pub weaponstate: i32,
    /// Fraction of the current rank's XP range (0..1) and the rank id (`mp/ranktable.csv` col 0).
    pub xp_frac: f32,
    pub rank: i32,
    pub show_breath_hint: u8,
    /// Nonzero while the player is alive and the low-ammo warning may show.
    pub low_ammo_ok: u8,
    pub _pad: [u8; 2],
}

/// One draw command; see the module docs for what each kind means.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Mw2HudCmd {
    /// 0 material quad, 1 text, 2 solid colour quad.
    pub kind: u8,
    /// IW4 font enum for text (2 bigFont .. 10 hudSmallFont, see [`font_name`]).
    pub font: u8,
    /// Kind 0: the material's MW2 blend mode (`fx::blend_mode`: 3 additive, 4 multiply, 2 blend,
    /// 0/1 opaque; 255 writes only destination alpha, a mask never seen on its own).
    /// Text: always 0 (the origin is already aligned).
    pub align: u8,
    /// Kind 1: the IW4 text style (3 shadowed, 6 shadowed more, ...).
    pub _pad: u8,
    pub rect: [f32; 4],
    pub uv: [f32; 4],
    pub color: [f32; 4],
    pub rotation_deg: f32,
    /// Kind 1: the menu text scale (e.g. 0.333) before the font's normalization.
    pub text_scale: f32,
    /// Index for `mw2_hud_cmd_string`: the material name (kind 0) or the text (kind 1).
    pub material: u16,
    /// Which of the frame's menus drew it (1-based; 0 from `mw2_menu_frame`).
    pub _pad2: u16,
}

// --- loaded data -------------------------------------------------------------------------

/// A compiled expression dump.
pub(crate) struct Exp {
    pub(crate) stmt: Option<Statement>,
    pub(crate) raw: String,
}

impl Exp {
    /// Two opcodes the zone uses are not in IW4L's evaluator but have exact stand-ins: 0xB3
    /// (adjusted safe area, horizontal) reads the same default as 0xB4 (vertical), 1.0; 0x6F
    /// (rank for XP) is rewritten to the player-data call, which the host answers by operand type.
    /// 0x30, between "in killcam" (0x2F) and the player field (0x31), only gates the AC-130 HUD
    /// (`!op48 && !ui_active`); read as another killcam flag (we have no killcams), so 0.
    pub(crate) fn compile(dump: &str) -> Option<Exp> {
        if dump.trim().is_empty() {
            return None;
        }
        let toks: Vec<&str> = dump.split_whitespace().collect();
        let mut fixed = String::with_capacity(dump.len());
        let mut i = 0;
        while i < toks.len() {
            if toks[i] == "op" && i + 1 < toks.len() {
                let op = match toks[i + 1] {
                    "111" => "108",
                    "179" => "180",
                    "48" => "47",
                    other => other,
                };
                fixed.push_str("op ");
                fixed.push_str(op);
                i += 2;
            } else {
                fixed.push_str(toks[i]);
                i += 1;
            }
            fixed.push(' ');
        }
        // The loader cuts nested statements at 1024 bytes; a cut inside a float literal still parses
        // (as a denormal), so such dumps are rejected here.
        let cut = toks.iter().any(|t| t.starts_with("f:") && t.len() != 10);
        Some(Exp { stmt: if cut { None } else { Statement::parse(&fixed).ok() }, raw: dump.to_owned() })
    }

    pub(crate) fn truthy(&self, host: &impl ExprHost) -> Result<bool, String> {
        let s = self.stmt.as_ref().ok_or("unparsed dump")?;
        s.is_true(host).map_err(|e| format!("{e:?}"))
    }

    pub(crate) fn float(&self, host: &impl ExprHost) -> Result<f32, String> {
        let s = self.stmt.as_ref().ok_or("unparsed dump")?;
        s.evaluate_float(host).map_err(|e| format!("{e:?}"))
    }

    pub(crate) fn string(&self, host: &impl ExprHost) -> Result<String, String> {
        let s = self.stmt.as_ref().ok_or("unparsed dump")?;
        s.evaluate_string(host).map_err(|e| format!("{e:?}"))
    }
}

pub(crate) struct Item {
    pub(crate) name: String,
    pub(crate) text: String,
    pub(crate) item_type: i32,
    pub(crate) style: i32,
    pub(crate) owner_draw: i32,
    pub(crate) rect: [f32; 4],
    pub(crate) horz: u8,
    pub(crate) vert: u8,
    pub(crate) fore: [f32; 4],
    pub(crate) back: [f32; 4],
    pub(crate) text_scale: f32,
    pub(crate) font: i32,
    pub(crate) text_align: i32,
    pub(crate) text_align_x: f32,
    pub(crate) text_align_y: f32,
    pub(crate) text_style: i32,
    pub(crate) background: String,
    pub(crate) vis: Option<Exp>,
    pub(crate) text_exp: Option<Exp>,
    pub(crate) material_exp: Option<Exp>,
    pub(crate) floats: Vec<(u32, Exp)>,
    /// Event scripts (menus.rs): (kind, statements), mw2data::scripts::SCRIPT_KIND_NAMES order.
    pub(crate) scripts: Vec<(u8, Vec<mw2data::scripts::MenuStmt>)>,
    pub(crate) static_flags: i32,
    pub(crate) dvar: String,
    pub(crate) disabled: Option<Exp>,
}

pub(crate) struct Menu {
    pub(crate) name: String,
    pub(crate) rect: [f32; 4],
    pub(crate) vis: Option<Exp>,
    pub(crate) floats: Vec<(u32, Exp)>,
    /// Static dvar rows the expressions refer to (`staticdvarbool 14` -> `g_hardcore`).
    pub(crate) dvars: Vec<(i32, String)>,
    pub(crate) items: Vec<Item>,
    pub(crate) scripts: Vec<(u8, Vec<mw2data::scripts::MenuStmt>)>,
    pub(crate) fullscreen: bool,
}

#[derive(Default)]
pub(crate) struct HudData {
    pub(crate) menus: Vec<Menu>,
    /// Lower-cased localize key -> text.
    pub(crate) localize: HashMap<String, String>,
    pub(crate) ranktable: Option<StringTable>,
}

pub(crate) static DATA: RwLock<Option<HudData>> = RwLock::new(None);
pub(crate) static ZONE_DIR: Mutex<Option<std::path::PathBuf>> = Mutex::new(None);
static DIAG: AtomicBool = AtomicBool::new(false);

fn parse_dvar_rows(list: &str) -> Vec<(i32, String)> {
    let mut toks = list.split_whitespace();
    let mut rows = Vec::new();
    while let (Some("d"), Some(row), Some(name)) = (toks.next(), toks.next(), toks.next()) {
        if let Ok(row) = row.parse() {
            rows.push((row, name.to_owned()));
        }
    }
    rows
}

pub(crate) fn build_menu(s: &ScriptCapture, name: &str, rect: [f32; 4]) -> Menu {
    let vis = s.menu_vis_exps.iter().find(|(m, _)| m == name).and_then(|(_, d)| Exp::compile(d));
    let floats = s
        .menu_float_exps
        .iter()
        .filter(|(m, ..)| m == name)
        .filter_map(|(_, k, d)| Exp::compile(d).map(|e| (*k, e)))
        .collect();
    let dvars = s.menu_expr_dvars.iter().find(|(m, _)| m == name).map(|(_, l)| parse_dvar_rows(l)).unwrap_or_default();
    let items = s
        .menu_items
        .iter()
        .filter(|i| i.menu == name)
        .map(|i| Item {
            name: i.name.clone(),
            text: i.text.clone(),
            item_type: i.item_type,
            style: i.style,
            owner_draw: i.owner_draw,
            rect: i.rect,
            horz: i.horz_align,
            vert: i.vert_align,
            fore: i.fore_color,
            back: i.back_color,
            text_scale: i.text_scale,
            font: i.font,
            text_align: i.text_align,
            text_align_x: i.text_align_x,
            text_align_y: i.text_align_y,
            text_style: i.text_style,
            background: i.background.clone(),
            vis: Exp::compile(&i.vis_exp),
            text_exp: Exp::compile(&i.text_exp),
            material_exp: Exp::compile(&i.material_exp),
            floats: i.float_exps.iter().filter_map(|(k, d)| Exp::compile(d).map(|e| (*k, e))).collect(),
            scripts: i.scripts.clone(),
            static_flags: i.static_flags,
            dvar: i.dvar.clone(),
            disabled: Exp::compile(&i.disabled_exp),
        })
        .collect();
    let scripts = s.menu_scripts.iter().filter(|(m, ..)| m == name).map(|(_, k, st)| (*k, st.clone())).collect();
    let fullscreen = s.menu_fullscreen.iter().any(|m| m == name);
    Menu { name: name.to_owned(), rect, vis, floats, dvars, items, scripts, fullscreen }
}

/// Take the HUD's share out of a walked zone: the HUD menus (first zone to define one wins; the
/// loader walks patch_mp before common_mp), localized strings and the rank table.
pub fn ingest(s: &mut ScriptCapture) {
    let Ok(mut guard) = DATA.write() else { return };
    let d = guard.get_or_insert_with(HudData::default);
    for (k, v) in s.localize.drain(..) {
        d.localize.entry(k.to_ascii_lowercase()).or_insert(v);
    }
    if d.ranktable.is_none() {
        d.ranktable = s.find_table("mp/ranktable.csv").cloned();
    }
    for name in HUD_MENUS.into_iter().chain(RIDE_MENUS) {
        if d.menus.iter().any(|m| m.name == name) {
            continue;
        }
        let Some((_, r, ..)) = s.menus.iter().find(|(n, ..)| n == name) else { continue };
        let menu = build_menu(s, name, *r);
        d.menus.push(menu);
    }
}

/// Remember where the zones are so `mw2_hud_init` can walk them itself if nothing was ingested.
pub fn note_zone_dir(dir: &std::path::Path) {
    if let Ok(mut z) = ZONE_DIR.lock() {
        *z = Some(dir.to_path_buf());
    }
}

fn item_count() -> u32 {
    DATA.read().ok().and_then(|d| d.as_ref().map(|d| d.menus.iter().map(|m| m.items.len() as u32).sum())).unwrap_or(0)
}

/// Load the HUD data from a zone directory (what `killstreaks::load` does as part of the weapon
/// load). Used by `mw2_hud_init` when the data was not ingested yet.
pub fn load_from_dir(dir: &std::path::Path) -> u32 {
    for zone in ["code_post_gfx_mp.ff", "patch_mp.ff", "common_mp.ff", "localized_code_post_gfx_mp.ff", "localized_common_mp.ff"] {
        if let Ok((mut s, _)) = mw2data::scripts_from_file(&dir.join(zone)) {
            crate::images::add_from(&mut s);
            crate::fonts::add(std::mem::take(&mut s.fonts));
            ingest(&mut s);
        }
    }
    item_count()
}

// --- per-process aux state the host feeds in beside the per-frame struct -------------------

#[derive(Default, Clone)]
pub(crate) struct DpadSlot {
    icon: String,
    /// `dpadIconRatio`: 1 is 2:1, 2 is 4:1 (height halved, centred).
    ratio: i32,
    atlas: [u8; 2],
    usable: bool,
}

#[derive(Default)]
struct Aux {
    last_weapon: u32,
    select_time: i32,
    frag_icon: String,
    smoke_icon: String,
    dpad: [DpadSlot; 4],
    bindings: HashMap<String, String>,
    north_yaw: f32,
    /// 0 the match HUD, 1..=3 a `RIDE_MENUS` entry.
    ride: u32,
    /// The launcher lock (the host's Stinger / Javelin lock-on): 0 none, 1 locking, 2 locked, and the
    /// target on screen in 640x480 virtual units.
    lock_stage: i32,
    lock_screen: [f32; 2],
}

/// The host's lock-on for the HUD (the Javelin's CLU box and lamps).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_hud_set_lock(stage: i32, x: f32, y: f32) {
    let _ = with_aux(|a| {
        a.lock_stage = stage;
        a.lock_screen = [x, y];
    });
}

static AUX: Mutex<Option<Aux>> = Mutex::new(None);
pub(crate) static STRINGS: Mutex<Vec<String>> = Mutex::new(Vec::new());

fn with_aux<T>(f: impl FnOnce(&mut Aux) -> T) -> Option<T> {
    let mut g = AUX.lock().ok()?;
    Some(f(g.get_or_insert_with(Aux::default)))
}

// --- weapon facts the HUD needs ------------------------------------------------------------

#[derive(Clone)]
pub(crate) struct WInfo {
    name: String,
    ammo_counter: i32,
    low_ammo_threshold: f32,
    clip_only: bool,
    display_name: Option<String>,
}

fn winfo(index: u32) -> Option<WInfo> {
    if index == 0 {
        return None;
    }
    let t = crate::weapons::TABLE.read().ok()?;
    let r = t.get(index as usize - 1)?;
    Some(WInfo {
        name: r.name.clone(),
        ammo_counter: r.ammo_counter,
        low_ammo_threshold: r.low_ammo_threshold,
        clip_only: r.clip_only,
        display_name: r.display_name.clone(),
    })
}

// --- fonts ---------------------------------------------------------------------------------

/// MW2 font asset name for an IW4 font enum (the `fonts/...` assets the loader captured).
pub fn font_name(font: u32) -> &'static str {
    match font {
        2 => "fonts/bigFont",
        3 => "fonts/smallFont",
        4 => "fonts/boldFont",
        5 => "fonts/consoleFont",
        6 => "fonts/objectiveFont",
        7 => "fonts/normalFont",
        8 => "fonts/extraBigFont",
        9 => "fonts/hudBigFont",
        10 => "fonts/hudSmallFont",
        _ => "",
    }
}

/// `ui_get_font_handle` returns IW4L's lower-cased names; map back to the enum.
fn font_enum_of(handle: &str) -> u8 {
    (2..=10u32).find(|&e| font_name(e).eq_ignore_ascii_case(handle)).unwrap_or(7) as u8
}

struct Measured {
    enum_: u8,
    /// Sum of glyph advances in font pixels.
    units: i32,
    /// Normalized scale: virtual pixels per font pixel.
    norm: f32,
}

fn measure(font_enum: i32, sy: f32, text_scale: f32, text: &str) -> Option<Measured> {
    let handle = ui_get_font_handle(font_enum, sy, text_scale);
    crate::fonts::with(handle, |f| {
        let mut width = 0i32;
        let mut max = 0i32;
        let mut chars = text.chars().peekable();
        while let Some(letter) = next_letter(&mut chars) {
            if letter == 13 || letter == 10 {
                width = 0;
                continue;
            }
            let Some(g) = f.glyphs.iter().find(|g| u32::from(g.letter) == letter) else { continue };
            width += i32::from(g.dx);
            max = max.max(width);
        }
        Measured { enum_: font_enum_of(handle), units: max, norm: normalized_text_scale(f.pixel_height, text_scale) }
    })
}

// --- evaluation host ----------------------------------------------------------------------

#[derive(Clone)]
pub(crate) struct Ctx {
    pub(crate) st: Mw2HudState,
    pub(crate) pl: ScreenPlacement,
    pub(crate) sx: f32,
    pub(crate) sy: f32,
    pub(crate) winfo: Option<WInfo>,
    /// Total XP synthesized from rank + fraction, and the rank's [lo, hi) range.
    pub(crate) xp: f32,
    pub(crate) xp_lo: f32,
    pub(crate) xp_hi: f32,
    pub(crate) wide: bool,
    pub(crate) select_time: i32,
    pub(crate) dpad: [DpadSlot; 4],
    pub(crate) frag_icon: String,
    pub(crate) smoke_icon: String,
    pub(crate) bindings: HashMap<String, String>,
    pub(crate) north_yaw: f32,
    /// `mw2_hud_set_ride`: 1 Predator, 2 AC-130, 3 Chopper Gunner, 0 none.
    pub(crate) ride: u32,
    /// This frame's XP bar placement is already fitted to the screen width.
    pub(crate) xpbar_fitted: bool,
}

struct Host<'a> {
    c: &'a Ctx,
    d: &'a HudData,
    menu: &'a Menu,
}

fn rank_row(t: &StringTable, rank: i32) -> Option<usize> {
    let key = rank.to_string();
    (0..t.rows).find(|&r| t.cell(r, 0) == key)
}

fn rank_for_xp(t: &StringTable, xp: f32) -> i32 {
    let mut best = 0;
    for r in 0..t.rows {
        let (Ok(id), Ok(lo)) = (t.cell(r, 0).parse::<i32>(), t.cell(r, 2).parse::<f32>()) else { continue };
        if xp >= lo {
            best = id;
        }
    }
    best
}

impl ExprHost for Host<'_> {
    fn milliseconds(&self) -> i32 {
        self.c.st.time_ms
    }

    fn static_dvar_int(&self, index: i32) -> Result<i32, ExprError> {
        let name = self.menu.dvars.iter().find(|(i, _)| *i == index).map(|(_, n)| n.as_str()).ok_or(ExprError::Host("static dvar row"))?;
        let lower = name.to_ascii_lowercase();
        Ok(match lower.as_str() {
            "widescreen" => i32::from(self.c.wide),
            "hidef" => 1,
            "g_hardcore" | "scr_gameended" | "onlinegame" | "xblive_privatematch" | "cg_thirdpersonspectator" | "splitscreen" => 0,
            _ => return Err(ExprError::Host("static dvar")),
        })
    }

    fn team_field(&self, _field: &str) -> Result<Operand, ExprError> {
        Err(ExprError::Host("team field"))
    }

    fn player_field(&self, field: &str) -> Result<Operand, ExprError> {
        let st = &self.c.st;
        let v = match field.to_ascii_lowercase().as_str() {
            "stockammo" => self.c.stock().unwrap_or(0),
            "fragammo" => st.frags,
            "smokeammo" => st.smokes,
            "clipammo" | "clipammo_left" => st.clip,
            _ => return Err(ExprError::Host("player field")),
        };
        Ok(Operand::Int(v))
    }

    fn other_team_field(&self, _field: &str) -> Result<Operand, ExprError> {
        Err(ExprError::Host("other team field"))
    }

    fn local_var_string(&self, _name: &str) -> Result<Operand, ExprError> {
        Err(ExprError::Host("local var"))
    }

    fn time_left(&self) -> Result<i32, ExprError> {
        Err(ExprError::Host("timeleft"))
    }

    fn score_at_rank(&self, _rank: i32) -> Result<i32, ExprError> {
        Err(ExprError::Host("score"))
    }

    fn gametype_name(&self) -> Result<Operand, ExprError> {
        Err(ExprError::Host("gametype"))
    }

    fn weapon_lock(&self) -> Result<WeaponLockView, ExprError> {
        let (stage, screen) = with_aux(|a| (a.lock_stage, a.lock_screen)).unwrap_or((0, [320.0, 240.0]));
        Ok(WeaponLockView {
            ads_javelin: self.c.ads_javelin(),
            time_ms: self.c.st.time_ms,
            // MW2's Javelin starts in top attack (DIR is the alternate mode).
            attack_top: true,
            locking: stage == 1,
            locked: stage == 2,
            screen_pos: screen,
            ..WeaponLockView::default()
        })
    }

    fn table_lookup(&self, table: &str, col0: i32, key: &str, result_col: i32) -> Result<Operand, ExprError> {
        if !table.eq_ignore_ascii_case("mp/ranktable.csv") {
            return Ok(Operand::Str(String::new()));
        }
        let Some(t) = self.d.ranktable.as_ref() else { return Err(ExprError::Host("rank table")) };
        let (col0, col) = (col0.max(0) as usize, result_col.max(0) as usize);
        Ok(Operand::Str(match (0..t.rows).find(|&r| t.cell(r, col0).eq_ignore_ascii_case(key)) {
            Some(r) => t.cell(r, col).to_owned(),
            None => String::new(),
        }))
    }

    /// `getplayerdata("experience")` answers the synthesized XP, `("restXPGoal")` 0 (no rested XP);
    /// the rewritten 0x6F call hands the XP value back and gets the rank id for it.
    fn player_data(&self, path: &[Operand]) -> Result<Operand, ExprError> {
        match path {
            [Operand::Str(s)] if s.eq_ignore_ascii_case("experience") => Ok(Operand::Float(self.c.xp)),
            [Operand::Str(s)] if s.eq_ignore_ascii_case("restXPGoal") => Ok(Operand::Int(0)),
            [Operand::Int(_) | Operand::Float(_)] => {
                let xp = match path[0] {
                    Operand::Int(n) => n as f32,
                    Operand::Float(f) => f,
                    Operand::Str(_) => 0.0,
                };
                let t = self.d.ranktable.as_ref().ok_or(ExprError::Host("rank table"))?;
                Ok(Operand::Int(rank_for_xp(t, xp)))
            }
            _ => Err(ExprError::Host("player data")),
        }
    }

    fn flashbanged(&self) -> Result<i32, ExprError> {
        Ok(0)
    }

    fn missilecam(&self) -> Result<i32, ExprError> {
        Ok(i32::from(self.c.ride == 1))
    }

    fn weapon_name(&self) -> Result<Operand, ExprError> {
        Ok(Operand::Str(self.c.winfo.as_ref().map(|w| w.name.clone()).unwrap_or_default()))
    }

    fn action_slot_usable(&self, slot: i32) -> Result<i32, ExprError> {
        let s = usize::try_from(slot - 1).ok().and_then(|i| self.c.dpad.get(i)).ok_or(ExprError::Host("action slot"))?;
        Ok(i32::from(s.usable && !s.icon.is_empty()))
    }

    fn key_binding(&self, command: &str) -> Result<Operand, ExprError> {
        self.c.bindings.get(command).map(|l| Operand::Str(l.clone())).ok_or(ExprError::Host("keybinding"))
    }
}

impl Ctx {
    /// A screen context for full-screen menus (no in-match state).
    pub(crate) fn for_menus(w: f32, h: f32, time_ms: i32) -> Ctx {
        let pl = ScreenPlacement::setup_fullscreen(w.max(1.0), h.max(1.0), 1.0);
        Ctx {
            st: Mw2HudState { screen_w: w, screen_h: h, time_ms, ..Mw2HudState::default() },
            sx: pl.scale_virtual_to_real[0],
            sy: pl.scale_virtual_to_real[1],
            pl,
            winfo: None,
            xp: 0.0,
            xp_lo: 0.0,
            xp_hi: 0.0,
            wide: w / h.max(1.0) > 1.4,
            select_time: 0,
            dpad: Default::default(),
            frag_icon: String::new(),
            smoke_icon: String::new(),
            bindings: HashMap::new(),
            north_yaw: 0.0,
            ride: 0,
            xpbar_fitted: false,
        }
    }

    fn kind(&self) -> Option<AmmoCounterClipKind> {
        ammo_counter_clip_kind(self.winfo.as_ref()?.ammo_counter)
    }

    /// `weaponbar_ammo`: None for alternate-weapon counters, 0 for no counter.
    fn stock(&self) -> Option<i32> {
        match self.kind()? {
            AmmoCounterClipKind::None => Some(0),
            AmmoCounterClipKind::AltWeapon => None,
            _ => Some(self.st.stock),
        }
    }

    fn ads_javelin(&self) -> bool {
        self.st.ads_frac >= 1.0 && self.winfo.as_ref().is_some_and(|w| w.name.eq_ignore_ascii_case("javelin_mp"))
    }

    fn dual(&self) -> bool {
        self.st.alt_clip_size > 0
    }
}

// --- painting ------------------------------------------------------------------------------

#[derive(Default)]
pub(crate) struct Out {
    pub(crate) cmds: Vec<Mw2HudCmd>,
    pub(crate) strings: Vec<String>,
    pub(crate) diag: Vec<String>,
    pub(crate) diag_on: bool,
}

impl Out {
    fn note(&mut self, msg: impl FnOnce() -> String) {
        if self.diag_on {
            self.diag.push(msg());
        }
    }

    fn string(&mut self, s: &str) -> u16 {
        if let Some(i) = self.strings.iter().position(|x| x == s) {
            return i as u16;
        }
        if self.strings.len() >= usize::from(u16::MAX) {
            return 0;
        }
        self.strings.push(s.to_owned());
        (self.strings.len() - 1) as u16
    }
}

fn stem(background: &str) -> Option<&str> {
    let s = background.trim().trim_start_matches(',').trim();
    (!s.is_empty()).then_some(s)
}

/// `chrome.rs::assign_color_slot`: slots 0..2 are r/g/b, 3 sets all three, 4 is alpha.
fn assign_color_slot(color: &mut [f32; 4], slot: u32, value: f32) {
    match slot {
        0..=2 => color[slot as usize] = value,
        3 => color[..3].fill(value),
        _ => color[3] = value,
    }
}

/// Script-set item colours (`setItemColor <item> backcolor|forecolor r g b a`).
#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct ItemOverride {
    pub(crate) back: Option<[f32; 4]>,
    pub(crate) fore: Option<[f32; 4]>,
}

pub(crate) struct Style {
    pub(crate) rect: [f32; 4],
    pub(crate) fore: [f32; 4],
    pub(crate) back: [f32; 4],
}

impl Style {
    /// `EvaluatedItemStyle::fill_color`: filled windows (style 1) use the back colour.
    fn fill(&self, item: &Item) -> [f32; 4] {
        if item.style == 1 { self.back } else { self.fore }
    }
}

/// Nothing to draw: invisible, empty, or entirely off screen (the xp bar slides its layers
/// far left of the screen when the fill is small).
fn off_screen(c: &Ctx, a: &hud_iw4::AppliedRect) -> bool {
    let (vw, vh) = (c.pl.real_viewport_size[0], c.pl.real_viewport_size[1]);
    a.x.max(a.x + a.w) <= 0.0 || a.y.max(a.y + a.h) <= 0.0 || a.x.min(a.x + a.w) >= vw || a.y.min(a.y + a.h) >= vh
}

fn quad(out: &mut Out, c: &Ctx, material: &str, rect: [f32; 4], horz: u8, vert: u8, color: [f32; 4], uv: [f32; 4], rotation: f32) {
    if color[3] <= 0.0 || rect[2].abs() <= f32::EPSILON || rect[3].abs() <= f32::EPSILON {
        return;
    }
    let a = c.pl.apply_rect(rect[0], rect[1], rect[2], rect[3], i32::from(horz), i32::from(vert));
    if off_screen(c, &a) {
        return;
    }
    let blend = crate::images::host_blend(material);
    let material = out.string(material);
    out.cmds.push(Mw2HudCmd { kind: 0, align: blend, rect: [a.x, a.y, a.w, a.h], uv, color, rotation_deg: rotation, material, ..Mw2HudCmd::default() });
}

fn solid(out: &mut Out, c: &Ctx, rect: [f32; 4], horz: u8, vert: u8, color: [f32; 4]) {
    if color[3] <= 0.0 || rect[2].abs() <= f32::EPSILON || rect[3].abs() <= f32::EPSILON {
        return;
    }
    let a = c.pl.apply_rect(rect[0], rect[1], rect[2], rect[3], i32::from(horz), i32::from(vert));
    if off_screen(c, &a) {
        return;
    }
    out.cmds.push(Mw2HudCmd { kind: 2, rect: [a.x, a.y, a.w, a.h], uv: [0.0, 0.0, 1.0, 1.0], color, ..Mw2HudCmd::default() });
}

/// `push_owner_text_run` / `paint_text`: text at a virtual-space origin, rounded to whole pixels.
#[allow(clippy::too_many_arguments)]
fn text_at(out: &mut Out, c: &Ctx, m: &Measured, text: &str, text_scale: f32, x: f32, y: f32, horz: u8, vert: u8, color: [f32; 4], style: i32) {
    if color[3] <= 0.0 || text.is_empty() {
        return;
    }
    let a = c.pl.apply_rect(x, y, m.norm, m.norm, i32::from(horz), i32::from(vert));
    let width_px = m.units as f32 * a.w;
    let height_px = ui_text_height(text_scale) * c.sy;
    let material = out.string(text);
    out.cmds.push(Mw2HudCmd {
        kind: 1,
        font: m.enum_,
        align: 0,
        _pad: style as u8,
        rect: [(a.x + 0.5).floor(), (a.y + 0.5).floor(), a.w, a.h],
        uv: [width_px, height_px, 0.0, 0.0],
        color,
        text_scale,
        material,
        ..Mw2HudCmd::default()
    });
}

fn paint_text_item(out: &mut Out, c: &Ctx, d: &HudData, item: &Item, rect: [f32; 4], color: [f32; 4], text: &str) {
    let text = resolve_localized(d, item, text);
    let Some(text) = text else { return };
    let text = replace_key_bindings(&text, &c.bindings);
    // Line breaks (the AC-130's side blocks): each line laid out on its own, one text height down.
    let h = ui_text_height(item.text_scale);
    for (i, line) in text.split(char::from(10)).enumerate() {
        if line.is_empty() {
            continue;
        }
        let Some(m) = measure(item.font, c.sy, item.text_scale, line) else {
            out.note(|| format!("font missing for item '{}'", item.name));
            return;
        };
        let w = m.units as f32 * m.norm;
        let (x, y) = item_text_origin(rect[0], rect[1], rect[2], rect[3], item.text_align, item.text_align_x, item.text_align_y, w, h);
        text_at(out, c, &m, line, item.text_scale, x, y + h * i as f32, item.horz, item.vert, color, item.text_style);
    }
}

/// IW4's "[{command}]" in UI text: the key bound to the command, as "[KEY]" (the host feeds the
/// player's keys through `mw2_hud_set_binding`); "UNBOUND" when it has none.
fn replace_key_bindings(text: &str, bindings: &HashMap<String, String>) -> String {
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while let Some(start) = rest.find("[{") {
        let Some(len) = rest[start..].find("}]") else { break };
        out.push_str(&rest[..start]);
        let cmd = &rest[start + 2..start + len];
        out.push('[');
        out.push_str(bindings.get(cmd).or_else(|| bindings.get(&cmd.to_ascii_lowercase())).map_or("UNBOUND", String::as_str));
        out.push(']');
        rest = &rest[start + len + 2..];
    }
    out.push_str(rest);
    out
}

/// `chrome.rs::resolve_text` for literal / `@KEY` text.
fn resolve_localized(d: &HudData, item: &Item, raw: &str) -> Option<String> {
    if raw.is_empty() {
        return None;
    }
    if let Some(key) = raw.strip_prefix('@').filter(|_| item.item_type != 4) {
        return d.localize.get(&key.to_ascii_lowercase()).cloned();
    }
    Some(raw.to_owned())
}

fn paint_menu(out: &mut Out, c: &Ctx, d: &HudData, menu: &Menu) {
    // MW2's HD XP bar is authored for 16:9 only (853.33 virtual units = a 16:9 screen's width at the
    // height-based scale): it stopped short on 21:9 and would run off a 16:10 / 4:3 screen. Its
    // horizontal scale is fitted to the screen width so it spans it on any monitor (playtest 10-03-26).
    if menu.name.starts_with("xpbar") && !c.xpbar_fitted {
        let mut fit = c.clone();
        fit.xpbar_fitted = true;
        fit.pl.scale_virtual_to_real[0] = fit.pl.real_viewport_size[0] / (640.0 * 4.0 / 3.0);
        fit.pl.real_adjustable_min[0] = 0.0;
        fit.pl.real_viewable_min[0] = 0.0;
        fit.sx = fit.pl.scale_virtual_to_real[0];
        return paint_menu(out, &fit, d, menu);
    }
    let host = Host { c, d, menu };
    if let Some(v) = &menu.vis {
        match v.truthy(&host) {
            Ok(true) => {}
            Ok(false) => return,
            Err(e) => {
                out.note(|| format!("{} menu vis: {e}", menu.name));
                return;
            }
        }
    }
    // Menu rect float expressions move the whole menu (chrome.rs::apply_menu_float_rect).
    let mut parent = menu.rect;
    for (key, exp) in &menu.floats {
        match (key, exp.float(&host)) {
            (0..=3, Ok(v)) => parent[*key as usize] = v,
            (_, Err(e)) => {
                out.note(|| format!("{} menu float {key}: {e}", menu.name));
                return;
            }
            _ => {}
        }
    }
    for (index, item) in menu.items.iter().enumerate() {
        // javelin_overlay_hd's "prompt_test" has no visibility test and prints a bare number in the
        // middle of the CLU - a leftover debug readout (playtest 10-06-26: the Javelin scope looked broken).
        if item.name == "prompt_test" {
            continue;
        }
        paint_item(out, c, d, &host, menu, index, item, &parent, None);
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn paint_item(out: &mut Out, c: &Ctx, d: &HudData, host: &impl ExprHost, menu: &Menu, index: usize, item: &Item, parent: &[f32; 4], ov: Option<&ItemOverride>) {
    if let Some(v) = &item.vis {
        match v.truthy(host) {
            Ok(true) => {}
            Ok(false) => return,
            Err(e) => {
                out.note(|| format!("{}[{index}] vis: {e}", menu.name));
                return;
            }
        }
    }
    let mut style = match evaluate_style(out, c, host, menu, index, item, parent) {
        Some(s) => s,
        None => return,
    };
    if let Some(o) = ov {
        if let Some(b) = o.back {
            style.back = b;
        }
        if let Some(f) = o.fore {
            style.fore = f;
        }
    }
    if item.owner_draw != 0 {
        owner_draw(out, c, d, item, style.rect, style.fore);
        return;
    }
    let material = match &item.material_exp {
        Some(e) => match e.string(host) {
            Ok(s) if !s.is_empty() => Some(s),
            Ok(_) => stem(&item.background).map(str::to_owned),
            Err(err) => {
                out.note(|| format!("{}[{index}] material: {err}", menu.name));
                return;
            }
        },
        None => stem(&item.background).map(str::to_owned),
    };
    let text = match (&item.text_exp, item.text.is_empty()) {
        (Some(e), _) => match e.string(host) {
            Ok(s) => s,
            Err(err) => {
                out.note(|| format!("{}[{index}] text: {err}", menu.name));
                String::new()
            }
        },
        (None, false) => item.text.clone(),
        (None, true) => String::new(),
    };
    match material {
        Some(m) => {
            // chrome.rs::push_stretch: negative sizes flip the texture.
            let r = style.rect;
            let (w, h) = (r[2].abs(), r[3].abs());
            let uv = [if r[2] < 0.0 { 1.0 } else { 0.0 }, if r[3] < 0.0 { 1.0 } else { 0.0 }, if r[2] < 0.0 { 0.0 } else { 1.0 }, if r[3] < 0.0 { 0.0 } else { 1.0 }];
            quad(out, c, &m, [r[0], r[1], w, h], item.horz, item.vert, style.fill(item), uv, 0.0);
            if !text.is_empty() && item.style != 5 {
                paint_text_item(out, c, d, item, style.rect, style.fore, &text);
            }
        }
        None if !text.is_empty() => paint_text_item(out, c, d, item, style.rect, style.fore, &text),
        None if item.style == 1 => solid(out, c, style.rect, item.horz, item.vert, style.back),
        None => {}
    }
}

/// `chrome.rs::evaluate_item_style`, plus the stand-in for the xp bar's bar-edge expression
/// whose dump the zone loader truncates (see `xp_edge_x`).
pub(crate) fn evaluate_style(out: &mut Out, c: &Ctx, host: &impl ExprHost, menu: &Menu, index: usize, item: &Item, parent: &[f32; 4]) -> Option<Style> {
    let mut s = Style { rect: item.rect, fore: item.fore, back: item.back };
    s.rect[0] += parent[0] - menu.rect[0];
    s.rect[1] += parent[1] - menu.rect[1];
    let mut broken_x: Option<&Exp> = None;
    for (key, exp) in &item.floats {
        // The xp bar's edge expressions (rank-table lookups) come out of the zone truncated.
        if *key == 0 && exp.raw.contains("op 74 ") {
            broken_x = Some(exp);
            continue;
        }
        match exp.float(host) {
            Ok(v) => match *key {
                0 => s.rect[0] = parent[0] + v,
                1 => s.rect[1] = parent[1] + v,
                2 => s.rect[2] = v,
                3 => s.rect[3] = v,
                4..=8 => assign_color_slot(&mut s.fore, key - 4, v),
                9..=13 => {}
                14..=18 => assign_color_slot(&mut s.back, key - 14, v),
                _ => {}
            },
            Err(e) => {
                if *key <= 3 {
                    out.note(|| format!("{}[{index}] float {key}: {e}", menu.name));
                    return None;
                }
            }
        }
    }
    if let Some(exp) = broken_x {
        match xp_edge_x(c, &exp.raw, s.rect[2]) {
            Some(x) => s.rect[0] = parent[0] + x,
            None => {
                out.note(|| format!("{}[{index}] float 0: unparsed dump", menu.name));
                return None;
            }
        }
    }
    Some(s)
}

/// The xp bar slides each layer left so its right edge lands on the fill fraction:
/// `x = int((frac * 0.95 + 0.005) * barWidth) - width`. The zone's dump of that expression is cut
/// at the loader's 1024 byte nested-statement buffer, so the shape is rebuilt from the intact parts
/// (the `0.95` / `0.005` constants, the `min(.., 0.9999)` clamp and the `- width` tail are in the
/// dump). The rested-XP layers use `restXPGoal` (no rested XP: 0), the rest use the XP.
fn xp_edge_x(c: &Ctx, raw: &str, width: f32) -> Option<f32> {
    const REST_XP_GOAL: &str = "s:726573745850476f616c";
    if !raw.contains("op 74") || c.xp_hi <= c.xp_lo {
        return None;
    }
    let value = if raw.contains(REST_XP_GOAL) { 0.0 } else { c.xp };
    let frac = ((value - c.xp_lo) / (c.xp_hi - c.xp_lo)).min(0.9999);
    let bar = 640.0 + 213.333 * f32::from(u8::from(c.wide));
    Some(((frac * 0.95 + 0.005) * bar).trunc() - width)
}

// --- owner draws ---------------------------------------------------------------------------

fn owner_draw(out: &mut Out, c: &Ctx, d: &HudData, item: &Item, rect: [f32; 4], color: [f32; 4]) {
    match item.owner_draw {
        // CG_PLAYER_WEAPON_BACKGROUND-style items are plain materials; these are the live ones.
        171..=174 => action_slot(out, c, item, rect, color),
        119 => stock(out, c, item, rect, color),
        117 => pips(out, c, item, rect, color, 0),
        121 => pips(out, c, item, rect, color, 1),
        81 => weapon_name(out, c, d, item, rect, color, true),
        83 => weapon_name(out, c, d, item, rect, color, false),
        103 => offhand(out, c, item, rect, color, true),
        104 => offhand(out, c, item, rect, color, false),
        120 => low_ammo(out, c, d, item, rect),
        166 => compass_ring(out, c, item, rect, color),
        71 => breath_hint(out, c, d, item, rect, color),
        other => out.note(|| format!("unsupported ownerdraw {other}")),
    }
}

/// OWNERDRAW 119: the stock count, right-aligned in three characters (`ammo.rs::stock_digits`).
fn stock(out: &mut Out, c: &Ctx, item: &Item, rect: [f32; 4], color: [f32; 4]) {
    if matches!(c.kind(), None | Some(AmmoCounterClipKind::None | AmmoCounterClipKind::AltWeapon)) {
        return;
    }
    let Some(count) = c.stock() else { return };
    let text = format!("{count:3}");
    let Some(m) = measure(item.font, c.sy, item.text_scale, &text) else {
        out.note(|| "font missing for stock".to_owned());
        return;
    };
    let (x, y) = item_text_origin(rect[0], rect[1], rect[2], rect[3], item.text_align, item.text_align_x, item.text_align_y, m.units as f32 * m.norm, ui_text_height(item.text_scale));
    text_at(out, c, &m, &text, item.text_scale, x, y, item.horz, item.vert, color, item.text_style);
}

/// OWNERDRAW 117 / 121: one pip per round in the clip, laid out by `hud_iw4::ammo`
/// (`clip_pip_grid_xy` / `clip_pip_belt_xy`), empty ones dimmed. 121 is the second hand.
fn pips(out: &mut Out, c: &Ctx, item: &Item, rect: [f32; 4], color: [f32; 4], hand: usize) {
    if hand == 1 && !c.dual() {
        return;
    }
    let weapon_info = if hand == 1 && c.st.alt_weapon != 0 { winfo(c.st.alt_weapon) } else { c.winfo.clone() };
    let Some(kind) = weapon_info.and_then(|w| ammo_counter_clip_kind(w.ammo_counter)) else { return };
    let Some(m) = clip_pip_metrics(kind) else { return };
    let (clip, size) = if hand == 0 { (c.st.clip, c.st.clip_size) } else { (c.st.alt_clip, c.st.alt_clip_size) };
    let m = hud_iw4::ClipPipMetrics { width: m.width * c.sx, height: m.height * c.sy, step_x: m.step_x * c.sx, step_y: m.step_y * c.sy, ..m };
    let applied = c.pl.apply_rect(rect[0], rect[1], 0.0, 0.0, i32::from(item.horz), i32::from(item.vert));
    let base = [applied.x, applied.y];
    let align = i32::from(item.horz);
    let n = size.clamp(0, 100);
    for local in 0..n {
        let [x, y] = if kind == AmmoCounterClipKind::Beltfed { clip_pip_belt_xy(m, base, local, size, align) } else { clip_pip_grid_xy(m, base, local, align) };
        let tint = if local < clip { color } else { [CLIP_PIP_EMPTY_RGB, CLIP_PIP_EMPTY_RGB, CLIP_PIP_EMPTY_RGB, CLIP_PIP_EMPTY_ALPHA * color[3]] };
        if tint[3] <= 0.0 {
            continue;
        }
        let material = out.string(m.image);
        out.cmds.push(Mw2HudCmd { kind: 0, rect: [x, y, m.width, m.height], uv: [0.0, 0.0, 1.0, 1.0], color: tint, material, ..Mw2HudCmd::default() });
    }
}

/// OWNERDRAW 81 / 83: the weapon's display name, right-aligned to the item's right edge minus 28,
/// fading after a weapon change (81) or steady in the killcam (83).
fn weapon_name(out: &mut Out, c: &Ctx, d: &HudData, item: &Item, rect: [f32; 4], color: [f32; 4], fade: bool) {
    let mut color = color;
    if fade {
        let Some(alpha) = fade_color(c.st.time_ms, c.select_time, WEAPON_NAME_FADE_DURATION_MS, WEAPON_NAME_FADE_TAIL_MS) else { return };
        color[3] *= alpha;
    }
    let Some(key) = c.winfo.as_ref().and_then(|w| w.display_name.as_ref()) else { return };
    let Some(name) = d.localize.get(&key.to_ascii_lowercase()) else { return };
    let Some(m) = measure(item.font, c.sy, item.text_scale, name) else { return };
    let width = (m.units as f32 * m.norm).trunc();
    let x = rect[0] + rect[2] - width - 28.0;
    text_at(out, c, &m, name, item.text_scale, x, rect[1], item.horz, item.vert, color, item.text_style);
}

/// OWNERDRAW 103 / 104: the frag / smoke icon, stacked by the menu's visibility expressions.
fn offhand(out: &mut Out, c: &Ctx, item: &Item, rect: [f32; 4], color: [f32; 4], frag: bool) {
    let icon = if frag { &c.frag_icon } else { &c.smoke_icon };
    if icon.is_empty() {
        return;
    }
    quad(out, c, icon, rect, item.horz, item.vert, color, [0.0, 0.0, 1.0, 1.0], 0.0);
}

/// OWNERDRAW 166: a compass ring pass, its texture rotated against the view yaw
/// (`weaponbar.rs::paint_compass_ring`): degrees = -(yaw - north).
fn compass_ring(out: &mut Out, c: &Ctx, item: &Item, rect: [f32; 4], color: [f32; 4]) {
    let Some(material) = stem(&item.background) else { return };
    let deg = -(c.st.yaw_deg - c.north_yaw);
    quad(out, c, material, rect, item.horz, item.vert, color, [0.0, 0.0, 1.0, 1.0], deg);
}

/// OWNERDRAW 120: the low-ammo / reload warning, pulsing between the kind's two colours
/// (`weaponbar.rs::paint_low_ammo`, `hud_iw4::ammo::draw_player_weapon_low_ammo_warning`).
fn low_ammo(out: &mut Out, c: &Ctx, d: &HudData, item: &Item, rect: [f32; 4]) {
    let (Some(w), Some(kind)) = (c.winfo.as_ref(), c.kind()) else { return };
    let hands = if c.dual() { 2 } else { 1 };
    let Some(warn) = draw_player_weapon_low_ammo_warning(LowAmmoWarningQuery {
        pm_type: if c.st.low_ammo_ok != 0 { 0 } else { 8 },
        e_flags: 0,
        weapon: c.st.weapon,
        ammo_counter_clip: w.ammo_counter,
        weaponstate: [c.st.weaponstate, c.st.weaponstate],
        hands,
        clip: [c.st.clip, c.st.alt_clip],
        clip_size: c.st.clip_size,
        stock: c.st.stock,
        threshold: w.low_ammo_threshold,
        clip_only: w.clip_only,
    }) else {
        return;
    };
    let _ = kind;
    let Some(text) = d.localize.get(&warn.loc_key().to_ascii_lowercase()) else {
        out.note(|| format!("missing string {}", warn.loc_key()));
        return;
    };
    let (c1, c2) = low_ammo_warning_color_pair(warn);
    let lerped = vec4_lerp(c1, c2, low_ammo_warning_pulse_frac(c.st.time_ms));
    let color = lerped.map(|v| v.clamp(0.0, 1.0));
    let Some(m) = measure(item.font, c.sy, item.text_scale, text) else { return };
    let (x, y) = item_text_origin(rect[0], rect[1], rect[2], rect[3], item.text_align, item.text_align_x, item.text_align_y, m.units as f32 * m.norm, ui_text_height(item.text_scale));
    text_at(out, c, &m, text, item.text_scale, x, y, item.horz, item.vert, color, item.text_style);
}

/// OWNERDRAW 171..174: the d-pad action slot icons (`weaponbar.rs::paint_action_slot`); wide
/// icons double the width, 4:1 icons also halve the height around the centre.
fn action_slot(out: &mut Out, c: &Ctx, item: &Item, rect: [f32; 4], color: [f32; 4]) {
    let slot = &c.dpad[(item.owner_draw - 171) as usize];
    if slot.icon.is_empty() {
        return;
    }
    let mut r = rect;
    match slot.ratio {
        1 => r[2] *= 2.0,
        2 => {
            r[2] *= 2.0;
            r[1] += r[3] * 0.25;
            r[3] *= 0.5;
        }
        _ => {}
    }
    // weaponbar.rs::action_slot_atlas_uv: animated sheets advance every 50 ms.
    let rows = usize::from(slot.atlas[0].max(1));
    let cols = usize::from(slot.atlas[1].max(1));
    let frame = (c.st.time_ms.max(0) as usize / 50) % (rows * cols);
    let s0 = (frame % cols) as f32 / cols as f32;
    let t0 = (frame / cols) as f32 / rows as f32;
    quad(out, c, &slot.icon, r, item.horz, item.vert, color, [s0, t0, s0 + 1.0 / cols as f32, t0 + 1.0 / rows as f32], 0.0);
}

/// OWNERDRAW 71: "Hold <key> to steady", centred on the item (`breath_hint.rs`). The string
/// (`PLATFORM_HOLD_BREATH`) names the key as `&&1`.
fn breath_hint(out: &mut Out, c: &Ctx, d: &HudData, item: &Item, rect: [f32; 4], color: [f32; 4]) {
    if c.st.show_breath_hint == 0 {
        return;
    }
    let Some(template) = d.localize.get("platform_hold_breath") else {
        out.note(|| "missing string PLATFORM_HOLD_BREATH".to_owned());
        return;
    };
    let binding = ["+holdbreath", "+melee_breath", "+breath_sprint"].iter().find_map(|cmd| c.bindings.get(*cmd)).map_or("UNBOUND", String::as_str);
    let text = hint_replace_bind(template, binding);
    let Some(m) = measure(item.font, c.sy, item.text_scale, &text) else { return };
    let width = m.units as f32 * m.norm;
    text_at(out, c, &m, &text, item.text_scale, rect[0] - width * 0.5, rect[1], item.horz, item.vert, color, item.text_style);
}

// --- entry point ---------------------------------------------------------------------------

/// Lay out one frame. Returns the commands in draw order, the string table the commands index
/// and (when diagnostics are on) the reasons items were skipped.
pub fn frame(st: &Mw2HudState) -> (Vec<Mw2HudCmd>, Vec<String>, Vec<String>) {
    let guard = DATA.read();
    let Some(d) = guard.as_ref().ok().and_then(|g| g.as_ref()) else { return (Vec::new(), Vec::new(), Vec::new()) };
    let (w, h) = (st.screen_w.max(1.0), st.screen_h.max(1.0));
    let aux = with_aux(|a| {
        if st.weapon != a.last_weapon {
            a.last_weapon = st.weapon;
            a.select_time = st.time_ms.max(1);
        }
        (a.select_time, a.dpad.clone(), a.frag_icon.clone(), a.smoke_icon.clone(), a.bindings.clone(), a.north_yaw, a.ride)
    });
    let Some((select_time, dpad, frag_icon, smoke_icon, bindings, north_yaw, ride)) = aux else { return (Vec::new(), Vec::new(), Vec::new()) };
    // MW2 drew the ride HUDs (AC-130, Predator, Chopper Gunner) in a 16:9 frame inside the title
    // safe area, so their corner text sits inset rather than on the edges of an ultrawide screen.
    // Lay them out in a centred 16:9 viewport with the default safe area, then shift right.
    let (pl, x0) = if ride > 0 {
        let w16 = w.min(h * 16.0 / 9.0);
        let pl = ScreenPlacement::setup(0.0, 0.0, w16, h, w16, h, SAFE_AREA_DEFAULT, SAFE_AREA_DEFAULT, SAFE_AREA_DEFAULT, SAFE_AREA_DEFAULT, 1.0);
        (pl, ((w - w16) * 0.5).floor())
    } else {
        (ScreenPlacement::setup_fullscreen(w, h, 1.0), 0.0)
    };

    // The xp bar menu works on total XP; rebuild it from the rank and fraction.
    let (mut xp, mut lo, mut hi) = (0.0, 0.0, 0.0);
    if let Some(t) = d.ranktable.as_ref() {
        if let Some(r) = rank_row(t, st.rank) {
            lo = t.cell(r, 2).parse().unwrap_or(0.0);
            hi = t.cell(r, 7).parse().unwrap_or(lo);
            xp = lo + st.xp_frac.clamp(0.0, 0.9999) * (hi - lo);
        }
    }
    let ctx = Ctx {
        st: *st,
        sx: pl.scale_virtual_to_real[0],
        sy: pl.scale_virtual_to_real[1],
        pl,
        winfo: winfo(st.weapon),
        xp,
        xp_lo: lo,
        xp_hi: hi,
        wide: w / h > 1.4,
        select_time,
        dpad,
        frag_icon,
        smoke_icon,
        bindings,
        north_yaw,
        ride,
        xpbar_fitted: false,
    };
    let mut out = Out { diag_on: DIAG.load(Ordering::Relaxed), ..Out::default() };
    let ride_menu = (ride as usize).checked_sub(1).and_then(|i| RIDE_MENUS.get(i));
    let menus: &[&str] = match ride_menu {
        Some(m) => std::slice::from_ref(m),
        None => &HUD_MENUS,
    };
    for (mi, name) in menus.iter().enumerate() {
        if let Some(menu) = d.menus.iter().find(|m| m.name == *name) {
            let from = out.cmds.len();
            paint_menu(&mut out, &ctx, d, menu);
            // Which menu drew it (1-based): the plugin composites each menu's dest-alpha layers apart.
            for c in &mut out.cmds[from..] {
                c._pad2 = mi as u16 + 1;
            }
        }
    }
    if x0 != 0.0 {
        // Full-frame layers (the grain / interference) still cover the whole monitor; only the
        // text and reticles keep to the 16:9 frame (playtest 10-04-26).
        let w16 = w - 2.0 * x0;
        for c in &mut out.cmds {
            if c.kind != 1 && c.rect[0] <= 1.0 && c.rect[2] >= w16 - 2.0 {
                c.rect[2] = w;
            } else {
                c.rect[0] += x0;
            }
        }
    }
    (out.cmds, out.strings, out.diag)
}

/// Turn diagnostics on or off (the reasons items were skipped, kept per frame).
pub fn set_diagnostics(on: bool) {
    DIAG.store(on, Ordering::Relaxed);
}

// --- C ABI ---------------------------------------------------------------------------------

fn copy_out(s: &str, out: *mut u8, cap: u32) -> u32 {
    if !out.is_null() {
        let n = s.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(s.as_ptr(), out, n) };
    }
    s.len() as u32
}

fn str_arg<'a>(p: *const u8, len: usize) -> Option<&'a str> {
    if p.is_null() {
        return Some("");
    }
    std::str::from_utf8(unsafe { std::slice::from_raw_parts(p, len) }).ok()
}

/// Number of HUD menu items available (0 if the zones aren't loaded). The data is captured while
/// `mw2_load_weapons` walks the zones; if that hasn't happened this walks them once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_init() -> u32 {
    catch_unwind(|| {
        let n = item_count();
        if n > 0 {
            return n;
        }
        let dir = ZONE_DIR.lock().ok().and_then(|z| z.clone());
        dir.map_or(0, |dir| load_from_dir(&dir))
    })
    .unwrap_or(0)
}

/// Lay out the HUD for `state` into `out[0..cap]`; returns how many commands were written, in
/// draw order. Strings for the commands come from `mw2_hud_cmd_string` (valid until the next call).
///
/// # Safety
/// `state` points to a valid `Mw2HudState`; `out` has room for `cap` commands.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_frame(state: *const Mw2HudState, out: *mut Mw2HudCmd, cap: u32) -> u32 {
    if state.is_null() || out.is_null() || cap == 0 {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let st = unsafe { *state };
        let (cmds, strings, _) = frame(&st);
        let n = cmds.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(cmds.as_ptr(), out, n) };
        if let Ok(mut s) = STRINGS.lock() {
            *s = strings;
        }
        n as u32
    }))
    .unwrap_or(0)
}

/// The material name or text of a command (no NUL). Returns its length; copies up to `cap` bytes.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_cmd_string(index: u16, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let s = STRINGS.lock().ok().and_then(|s| s.get(usize::from(index)).cloned()).unwrap_or_default();
        copy_out(&s, out, cap)
    }))
    .unwrap_or(0)
}

/// The MW2 font asset name (`fonts/hudBigFont`) for a draw command's font enum. Returns its length.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_font_name(font: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| copy_out(font_name(font), out, cap))).unwrap_or(0)
}

/// Set the frag and smoke icon materials (the equipment's HUD icons); empty hides the slot.
///
/// # Safety
/// Each pointer addresses its length in UTF-8 bytes (or is null).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_set_offhand(frag: *const u8, frag_len: usize, smoke: *const u8, smoke_len: usize) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let (Some(f), Some(s)) = (str_arg(frag, frag_len), str_arg(smoke, smoke_len)) else { return };
        with_aux(|a| {
            a.frag_icon = f.trim_start_matches(',').to_owned();
            a.smoke_icon = s.trim_start_matches(',').to_owned();
        });
    }));
}

/// Set one d-pad action slot (`slot` 1..4 as in `+actionslot N`; the menu draws 3 and 4): its icon
/// material, `dpadIconRatio` (0 square, 1 2:1, 2 4:1), the icon sheet's rows / columns (1, 1 for a
/// plain icon) and whether it can be used. An empty material clears the slot.
///
/// # Safety
/// `material` addresses `len` UTF-8 bytes (or is null).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_set_dpad(slot: u32, material: *const u8, len: usize, ratio: i32, atlas_rows: u32, atlas_cols: u32, usable: i32) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let Some(m) = str_arg(material, len) else { return };
        if !(1..=4).contains(&slot) {
            return;
        }
        with_aux(|a| {
            a.dpad[slot as usize - 1] = DpadSlot { icon: m.trim_start_matches(',').to_owned(), ratio, atlas: [atlas_rows.min(255) as u8, atlas_cols.min(255) as u8], usable: usable != 0 };
        });
    }));
}

/// Tell the HUD what key a command is bound to (`+actionslot 4` -> `5`, `+holdbreath` -> `SHIFT`).
/// The labels fill the d-pad key hints and the hold-breath hint.
///
/// # Safety
/// Each pointer addresses its length in UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_set_binding(command: *const u8, command_len: usize, label: *const u8, label_len: usize) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let (Some(c), Some(l)) = (str_arg(command, command_len), str_arg(label, label_len)) else { return };
        with_aux(|a| {
            if l.is_empty() {
                a.bindings.remove(c);
            } else {
                a.bindings.insert(c.to_owned(), l.to_owned());
            }
        });
    }));
}

/// The map's north in yaw degrees (the compass ring rotates by `-(yaw - north)`); default 0.
/// Pixel em height of script HUD text (`createFontString( font, fontScale )`): IW4's hudelem font
/// enum (3 objective, 6 hudbig ...), the font scale, and the screen's virtual-to-real y scale
/// (height / 480).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_hudelem_em_px(elem_font: i32, font_scale: f32, scale_y: f32) -> f32 {
    hud_iw4::hudelem_em_px(elem_font, font_scale, scale_y)
}

/// Which HUD `mw2_hud_frame` lays out: 0 the match HUD, 1 the Predator missile camera, 2 the
/// AC-130, 3 the Chopper Gunner (`RIDE_MENUS`).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_hud_set_ride(kind: u32) {
    let _ = catch_unwind(|| with_aux(|a| a.ride = kind.min(RIDE_MENUS.len() as u32)));
}

#[unsafe(no_mangle)]
pub extern "C" fn mw2_hud_set_north_yaw(deg: f32) {
    let _ = catch_unwind(|| {
        with_aux(|a| a.north_yaw = if deg.is_finite() { deg } else { 0.0 });
    });
}

/// The four corner UVs (TL, TR, BR, BL; s,t pairs) of a quad whose texture is rotated by `deg`
/// about the UV centre (IW4's RotateST with centre (0.5, 0.5), radius 0.5).
///
/// # Safety
/// `out` has room for 8 floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_hud_rotate_st(deg: f32, out: *mut f32) {
    if out.is_null() {
        return;
    }
    let uv = rotate_st(deg);
    unsafe { std::ptr::copy_nonoverlapping(uv.as_ptr().cast::<f32>(), out, 8) };
}

/// `hud/src/draw2d.rs::rotate_st_corners` for centre (0.5, 0.5), radius 0.5, scale 1.
pub fn rotate_st(deg: f32) -> [[f32; 2]; 4] {
    let rad = deg * (std::f32::consts::PI / 180.0);
    let (sin, cos) = rad.sin_cos();
    let step_s = [0.5 * cos, 0.5 * sin];
    let step_t = [-0.5 * sin, 0.5 * cos];
    [
        [0.5 - step_s[0] - step_t[0], 0.5 - step_s[1] - step_t[1]],
        [0.5 + step_s[0] - step_t[0], 0.5 + step_s[1] - step_t[1]],
        [0.5 + step_s[0] + step_t[0], 0.5 + step_s[1] + step_t[1]],
        [0.5 - step_s[0] + step_t[0], 0.5 - step_s[1] + step_t[1]],
    ]
}

#[cfg(test)]
#[path = "hud_tests.rs"]
pub(crate) mod tests;

#[cfg(test)]
mod menu_names {
    /// MENUS=1: every menu the MP zones define (finding a HUD menu's name).
    #[test]
    #[ignore]
    fn list_menus() {
        let dir = std::path::Path::new(r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english");
        for zone in ["code_post_gfx_mp.ff", "patch_mp.ff", "common_mp.ff"] {
            if let Ok((s, _)) = mw2data::scripts_from_file(&dir.join(zone)) {
                let names: Vec<&str> = s.menus.iter().map(|(n, ..)| n.as_str()).collect();
                eprintln!("MENUS {zone}: {}", names.join(" "));
            }
        }
    }
}
