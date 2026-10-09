//! MW2's own game rules that live in the zone as data: rawfiles (GSC script source, e.g.
//! `maps/mp/killstreaks/_killstreaks.gsc`) and string tables (e.g. `mp/killstreakTable.csv`).
//! The string table layout follows IW4L's `iw4_string_table` (Apache-2.0).

use fastfile_iw4::{Ptr, ZonePtr, ZoneStream};

#[derive(Debug, Clone)]
pub struct StringTable {
    pub name: String,
    pub columns: usize,
    pub rows: usize,
    pub cells: Vec<String>,
}

impl StringTable {
    pub fn cell(&self, row: usize, column: usize) -> &str {
        self.cells.get(row * self.columns + column).map_or("", String::as_str)
    }

    pub fn to_csv(&self) -> String {
        let mut out = String::new();
        for r in 0..self.rows {
            let row: Vec<&str> = (0..self.columns).map(|c| self.cell(r, c)).collect();
            out.push_str(&row.join(","));
            out.push('\n');
        }
        out
    }
}

#[derive(Debug, Default)]
pub struct ScriptCapture {
    /// (name, bytes) with zlib already undone.
    pub raw_files: Vec<(String, Vec<u8>)>,
    pub string_tables: Vec<StringTable>,
    /// (material, colour image) for every material in the zone (HUD icons resolve through this).
    pub material_images: Vec<(String, String)>,
    /// (material, blend state bits) for the zone's materials (2D menus pick additive / blend).
    pub material_state_bits: Vec<(String, [u32; 2])>,
    /// Font assets (`fonts/hudBigFont` ...): glyph metrics + the glyph sheet material.
    pub fonts: Vec<Font>,
    /// Menu itemDefs (HUD layout): rects, ownerdraws, colours, fonts, backgrounds.
    pub menu_items: Vec<MenuItem>,
    /// MenuDefs: (name, rect x, y, w, h, horz align, vert align).
    pub menus: Vec<(String, [f32; 4], u8, u8)>,
    /// Menus flagged fullscreen: nothing under them is drawn.
    pub menu_fullscreen: Vec<String>,
    /// Menu visibility expression dumps (menu, dump) and rect float expressions (menu, target, dump).
    pub menu_vis_exps: Vec<(String, String)>,
    pub menu_float_exps: Vec<(String, u32, String)>,
    /// Per-menu static dvar list as the zone stores it (`d <row> <name> ...`); expressions refer to rows.
    pub menu_expr_dvars: Vec<(String, String)>,
    /// Localized strings (`WEAPON_AK47` -> `AK-47`), present in the localized zones.
    pub localize: Vec<(String, String)>,
    /// Menu-level event scripts (onOpen, onClose, onEsc ...): (menu, kind, statements).
    pub menu_scripts: Vec<(String, u8, Vec<MenuStmt>)>,
}

/// One statement of an IW4 menu event handler set, as the zone stores it.
#[derive(Debug, Clone)]
pub enum MenuStmt {
    /// Script text, e.g. `open popup_cac_perk1; play mouse_click;`.
    Script(String),
    /// `if (condition) { ... }` - condition is an expression dump (`hud_iw4::expr`).
    If(String, Vec<MenuStmt>),
    /// `else { ... }`, following an If.
    Else(Vec<MenuStmt>),
    /// setLocalVarBool / Int / Float / String (IW4 event types 3..6): name = expression dump.
    SetLocal { kind: u8, name: String, expr: String },
}

/// MenuScriptKind as a stable byte (fastfile_iw4 order).
pub fn script_kind_byte(k: fastfile_iw4::MenuScriptKind) -> u8 {
    use fastfile_iw4::MenuScriptKind as K;
    match k {
        K::OnOpen => 0,
        K::OnClose => 1,
        K::OnCloseRequest => 2,
        K::OnEsc => 3,
        K::MouseEnterText => 4,
        K::MouseExitText => 5,
        K::MouseEnter => 6,
        K::MouseExit => 7,
        K::Action => 8,
        K::Accept => 9,
        K::OnFocus => 10,
        K::LeaveFocus => 11,
        K::ExecKey => 12,
    }
}

pub const SCRIPT_KIND_NAMES: [&str; 13] = ["onOpen", "onClose", "onCloseRequest", "onEsc", "mouseEnterText", "mouseExitText", "mouseEnter", "mouseExit", "action", "accept", "onFocus", "leaveFocus", "execKey"];

/// One menu itemDef as MW2 lays it out (640x480 virtual units, aligned by horz/vert align).
#[derive(Debug, Clone)]
pub struct MenuItem {
    pub menu: String,
    pub name: String,
    pub text: String,
    pub item_type: i32,
    pub style: i32,
    pub owner_draw: i32,
    pub rect: [f32; 4],
    pub horz_align: u8,
    pub vert_align: u8,
    pub fore_color: [f32; 4],
    pub back_color: [f32; 4],
    pub glow_color: [f32; 4],
    pub text_scale: f32,
    pub font: i32,
    pub text_align: i32,
    pub text_align_x: f32,
    pub text_align_y: f32,
    pub text_style: i32,
    pub background: String,
    pub dvar: String,
    /// Expression dumps (IW4 statement dumps): visibility, text, material, and per-target floats
    /// (0..3 rect x/y/w/h, 4..8 fore colour, 9..13 glow, 14..18 back).
    pub vis_exp: String,
    pub text_exp: String,
    pub material_exp: String,
    pub float_exps: Vec<(u32, String)>,
    pub static_flags: i32,
    /// Event scripts on this item: (kind, statements) - action, onFocus, mouseEnter ...
    pub scripts: Vec<(u8, Vec<MenuStmt>)>,
    /// `disabled when ( ... )`: the item can't take focus or be activated while true (locked items).
    pub disabled_exp: String,
}

#[derive(Debug, Clone)]
pub struct Font {
    pub name: String,
    pub pixel_height: i32,
    pub material: String,
    pub glyphs: Vec<fastfile_iw4::GlyphCapture>,
}

impl ScriptCapture {
    pub fn raw(&mut self, name: &str, data: &[u8], zlib_compressed: bool) {
        let bytes = if zlib_compressed {
            match miniz_oxide::inflate::decompress_to_vec_zlib(data) {
                Ok(b) => b,
                Err(_) => return,
            }
        } else {
            let end = data.iter().position(|&b| b == 0).unwrap_or(data.len());
            data[..end].to_vec()
        };
        self.raw_files.push((name.to_owned(), bytes));
    }

    pub fn table(&mut self, s: &ZoneStream<'_>, header: Ptr) {
        let name = match s.ptr_at(header, 0).ok() {
            Some(ZonePtr::Offset(p)) => s.cstr(s.resolve_alias(p)).ok().unwrap_or("").to_owned(),
            _ => return,
        };
        let columns = s.i32_at(header, s.layout(4, 8)).unwrap_or(0).max(0) as usize;
        let rows = s.i32_at(header, s.layout(8, 12)).unwrap_or(0).max(0) as usize;
        let mut cells = Vec::with_capacity(columns * rows);
        if let Some(ZonePtr::Offset(p)) = s.ptr_at(header, s.layout(12, 16)).ok() {
            let arr = s.resolve_alias(p);
            let cell_size = s.layout(8, 16);
            for i in 0..columns * rows {
                let value = match s.ptr_at(arr.at(i * cell_size), 0).ok() {
                    Some(ZonePtr::Offset(p)) => s.cstr(s.resolve_alias(p)).ok().unwrap_or("").to_owned(),
                    _ => String::new(),
                };
                cells.push(value);
            }
        }
        self.string_tables.push(StringTable { name, columns, rows, cells });
    }

    pub fn find_table(&self, name: &str) -> Option<&StringTable> {
        self.string_tables.iter().find(|t| t.name.eq_ignore_ascii_case(name))
    }

    pub fn find_raw(&self, name: &str) -> Option<&[u8]> {
        self.raw_files.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, b)| b.as_slice())
    }
}
