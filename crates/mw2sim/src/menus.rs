//! MW2's front-end menus (Create-a-Class, its popups, class select) run from the zone's own
//! menuDefs: `localized_ui_mp.ff` carries the menus with their event scripts, `patch_mp` /
//! `common_mp` the string tables they look things up in (`mp/statsTable.csv`, `perkTable`,
//! `attachmentTable`, `classTable` ...). Items are painted by the HUD painter (`hud::paint_item`)
//! with a host that answers local vars, dvars, table lookups and player data, and the event
//! scripts (`onOpen`, `action`, `onFocus` ...) run here: `open` / `close` / `setFocus` /
//! `setItemColor` / `setPlayerData` / `setLocalVar*` / `play` ... The loadout they edit is MW2's
//! own playerdata layout (`customClasses[i].weaponSetups[0].attachment[1]` ...), kept as
//! `path=value` lines in a file the host names.
//!
//! Host contract: [`mw2_menu_init`] once, [`mw2_menu_open`] to show one, then each GUI frame
//! [`mw2_menu_input`] (cursor in screen pixels + key bits) and [`mw2_menu_frame`] (draw commands
//! as `mw2_hud_frame`, strings through `mw2_hud_cmd_string`), and [`mw2_menu_drain`] for the
//! sounds the scripts played.

use std::collections::HashMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::sync::Mutex;

use hud_iw4::{ExprError, ExprHost, Operand, WeaponLockView};
use mw2data::scripts::{MenuStmt, ScriptCapture, StringTable};

use crate::hud::{Ctx, HudData, ItemOverride, Menu, Mw2HudCmd, Out};

// Script kinds (mw2data::scripts::SCRIPT_KIND_NAMES).
const ON_OPEN: u8 = 0;
const ON_CLOSE: u8 = 1;
const ON_ESC: u8 = 3;
const MOUSE_ENTER: u8 = 6;
const MOUSE_EXIT: u8 = 7;
const ACTION: u8 = 8;
const ON_FOCUS: u8 = 10;
const LEAVE_FOCUS: u8 = 11;
const EXEC_KEY: u8 = 12;

/// Custom classes MW2 keeps (5, plus 5 more at prestige).
pub const CUSTOM_CLASSES: usize = 10;

#[derive(Default)]
struct Data {
    hud: HudData,
    menus: HashMap<String, Menu>,
    tables: HashMap<String, StringTable>,
}

struct OpenMenu {
    name: String,
    focus: Option<usize>,
    hover: Option<usize>,
    /// setItemColor results by item index.
    colors: HashMap<usize, ItemOverride>,
    /// show / hide by item name.
    shown: HashMap<String, bool>,
}

#[derive(Default)]
struct Runtime {
    stack: Vec<OpenMenu>,
    locals: HashMap<String, Operand>,
    dvars: HashMap<String, String>,
    pdata: HashMap<String, String>,
    pdata_path: Option<PathBuf>,
    sounds: Vec<String>,
    responses: Vec<String>,
    cursor: [f32; 2],
    time_ms: i32,
    screen: [f32; 2],
    /// The player's faction (mp/factionTable.csv key), for team names / emblems.
    faction: String,
    /// A menu whose first items are drawn under the stack (MW2's lobby backdrop: main_text's
    /// background, cloud overlay, mp image, glow, gradient), and how many of them.
    backdrop: Option<(String, usize)>,
    /// Challenge updates since playerdata was last written.
    unsaved: u32,
}

static DATA: Mutex<Option<Data>> = Mutex::new(None);
static RT: Mutex<Option<Runtime>> = Mutex::new(None);

/// String tables from a walked zone (first zone to carry a table wins). `killstreaks::load` feeds
/// patch_mp and common_mp through here.
pub fn add_tables(s: &ScriptCapture) {
    let Ok(mut g) = DATA.lock() else { return };
    let d = g.get_or_insert_with(Data::default);
    for t in &s.string_tables {
        d.tables.entry(t.name.to_ascii_lowercase()).or_insert_with(|| t.clone());
    }
}

/// Menus (and string tables) of another walked zone: patch_mp / common_mp carry the in-match
/// ones (`class`, `changeclass`). First zone to define a menu wins (patch_mp is walked first).
pub fn add_menus(s: &ScriptCapture) {
    add_tables(s);
    let Ok(mut g) = DATA.lock() else { return };
    let d = g.get_or_insert_with(Data::default);
    for (name, rect, ..) in &s.menus {
        if !d.menus.contains_key(name) {
            let mut menu = crate::hud::build_menu(s, name, *rect);
            patch_rename(&mut menu);
            d.menus.insert(name.clone(), menu);
        }
    }
}

/// The class rename popup (`pc_rename`, Create-a-Class -> RENAME). Its items come out of the zone
/// without their handlers (the edit field, OK and Cancel did nothing: playtest 10-04-26), so they get
/// MW2's: OK / Enter in the field write `customClasses[classIndex].name` from the field's local
/// `ui_classname` (which the menu's onOpen fills with the current name) and close; Cancel closes.
/// The field shows that local; `type_text` edits it.
fn patch_rename(menu: &mut Menu) {
    if !menu.name.eq_ignore_ascii_case(RENAME_MENU) {
        return;
    }
    const OK: &str = r#""play" "mouse_click" ; "setPlayerData" ( "customClasses" , localVarInt ( "classIndex" ) , "name" , localVarString ( "ui_classname" ) ) ; "exec" "uploadstats" ; "close" "self" ; "#;
    const CANCEL: &str = r#""play" "mouse_click" ; "close" "self" ; "#;
    // localVarString( "ui_classname" ) in the menus' expression dump form.
    let field_text = format!("op {} s:{}", 0x51, RENAME_LOCAL.bytes().map(|b| format!("{b:02x}")).collect::<String>());
    for it in &mut menu.items {
        let script = if it.name.eq_ignore_ascii_case(RENAME_FIELD) {
            it.text_exp = crate::hud::Exp::compile(&field_text);
            OK
        } else if it.name.eq_ignore_ascii_case("ok_button") {
            OK
        } else if it.text.eq_ignore_ascii_case("@MENU_CANCEL") {
            CANCEL
        } else {
            continue;
        };
        if !it.scripts.iter().any(|(k, _)| *k == ACTION) {
            it.scripts.push((ACTION, vec![MenuStmt::Script(script.to_owned())]));
        }
    }
}

const RENAME_MENU: &str = "pc_rename";
const RENAME_FIELD: &str = "rename_class_editfield";
const RENAME_LOCAL: &str = "ui_classname";
/// MW2's class names hold 15 characters.
const RENAME_MAX: usize = 15;
const BACKSPACE: char = '\u{8}';

/// Typed text for the open menu's edit field (the class rename): printable characters append,
/// backspace (U+0008) deletes. True if an edit field took it.
pub fn type_text(text: &str) -> bool {
    with(|rt, d| {
        if !input_menu(rt, d).is_some_and(|m| m.eq_ignore_ascii_case(RENAME_MENU)) {
            return false;
        }
        let mut name = rt.locals.get(RENAME_LOCAL).map(operand_text).unwrap_or_default();
        for ch in text.chars() {
            match ch {
                BACKSPACE => {
                    name.pop();
                }
                c if !c.is_control() && name.chars().count() < RENAME_MAX => name.push(c),
                _ => {}
            }
        }
        rt.locals.insert(RENAME_LOCAL.to_owned(), Operand::Str(name));
        true
    })
    .unwrap_or(false)
}

/// Is an edit field taking typed text (the host keeps keystrokes from everything else)?
pub fn editing() -> bool {
    with(|rt, d| input_menu(rt, d).is_some_and(|m| m.eq_ignore_ascii_case(RENAME_MENU))).unwrap_or(false)
}

/// Take the menus, localized strings and tables of `localized_ui_mp.ff` (already walked).
fn ingest(s: &mut ScriptCapture) {
    crate::images::add_from(s);
    crate::fonts::add(std::mem::take(&mut s.fonts));
    add_tables(s);
    let Ok(mut g) = DATA.lock() else { return };
    let d = g.get_or_insert_with(Data::default);
    for (k, v) in s.localize.drain(..) {
        d.hud.localize.entry(k.to_ascii_lowercase()).or_insert(v);
    }
    let names: Vec<(String, [f32; 4])> = s.menus.iter().map(|(n, r, ..)| (n.clone(), *r)).collect();
    for (name, rect) in names {
        if d.menus.contains_key(&name) {
            continue;
        }
        let mut menu = crate::hud::build_menu(s, &name, rect);
        patch_rename(&mut menu);
        d.menus.insert(name, menu);
    }
}

/// Walk the UI zone and set up the runtime; `pdata` is where the loadouts are kept.
pub fn init(zone_dir: &std::path::Path, pdata: Option<PathBuf>) -> Result<usize, String> {
    let (mut s, _) = mw2data::scripts_from_file(&zone_dir.join("localized_ui_mp.ff")).map_err(|e| e.to_string())?;
    ingest(&mut s);
    // The HUD's localized strings (weapon and perk names live in localized_common_mp) and rank table.
    if let (Ok(h), Ok(mut g)) = (crate::hud::DATA.read(), DATA.lock()) {
        if let (Some(h), Some(d)) = (h.as_ref(), g.as_mut()) {
            for (k, v) in &h.localize {
                d.hud.localize.entry(k.clone()).or_insert_with(|| v.clone());
            }
            if d.hud.ranktable.is_none() {
                d.hud.ranktable = h.ranktable.clone();
            }
        }
    }
    let n = DATA.lock().ok().and_then(|g| g.as_ref().map(|d| d.menus.len())).unwrap_or(0);
    let mut rt = Runtime { pdata_path: pdata, faction: "us_army".into(), ..Runtime::default() };
    rt.locals.insert("ui_team".into(), Operand::Str("marines".into()));
    rt.load_pdata();
    if let Ok(g) = DATA.lock() {
        if let Some(d) = g.as_ref() {
            rt.seed_defaults(d);
        }
    }
    if let Ok(mut g) = RT.lock() {
        *g = Some(rt);
    }
    Ok(n)
}

// --- player data ---------------------------------------------------------------------------

fn pkey(parts: &[String]) -> String {
    parts.iter().map(|p| p.to_ascii_lowercase()).collect::<Vec<_>>().join(".")
}

fn operand_text(o: &Operand) -> String {
    match o {
        Operand::Int(n) => n.to_string(),
        Operand::Float(f) => {
            if f.fract() == 0.0 && f.abs() < 1e9 {
                (*f as i64).to_string()
            } else {
                f.to_string()
            }
        }
        Operand::Str(s) => s.clone(),
    }
}

impl Runtime {
    fn load_pdata(&mut self) {
        let Some(p) = &self.pdata_path else { return };
        let Ok(text) = std::fs::read_to_string(p) else { return };
        for line in text.lines() {
            if let Some((k, v)) = line.split_once('=') {
                self.pdata.insert(k.trim().to_ascii_lowercase(), v.to_owned());
            }
        }
    }

    fn save_pdata(&self) {
        if PDATA_READONLY.load(std::sync::atomic::Ordering::Relaxed) {
            return;
        }
        let Some(p) = &self.pdata_path else { return };
        let mut keys: Vec<&String> = self.pdata.keys().collect();
        keys.sort();
        let mut s = String::new();
        for k in keys {
            s.push_str(k);
            s.push('=');
            s.push_str(&self.pdata[k]);
            s.push(char::from(10));
        }
        write_pdata(p.clone(), s);
    }

    /// New custom classes start as MW2's do: classTable columns 6..10 (M4A1, MP5K, RPD,
    /// Intervention, Riot Shield), named `Custom Class N`; the killstreaks from the default column.
    fn seed_defaults(&mut self, d: &Data) {
        // A new player's killstreaks, as MW2 starts everyone: UAV, Care Package, Predator Missile
        // (playtest 10-04-26: a fresh profile showed none).
        for (i, k) in ["uav", "airdrop", "predator_missile"].iter().enumerate() {
            let key = format!("killstreaks.{i}");
            if self.pdata.get(&key).is_none_or(|v| v.is_empty()) {
                self.pdata.insert(key, (*k).to_owned());
            }
        }
        let Some(t) = d.tables.get("mp/classtable.csv") else { return };
        let row = |name: &str| (0..t.rows).find(|&r| t.cell(r, 0).eq_ignore_ascii_case(name));
        let cell = |name: &str, col: usize| row(name).map(|r| t.cell(r, col).to_owned()).unwrap_or_default();
        let fields: [(&str, &str); 14] = [
            ("weaponSetups.0.weapon", "loadoutPrimary"),
            ("weaponSetups.0.attachment.0", "loadoutPrimaryAttachment"),
            ("weaponSetups.0.attachment.1", "loadoutPrimaryAttachment2"),
            ("weaponSetups.0.camo", "loadoutPrimaryCamo"),
            ("weaponSetups.1.weapon", "loadoutSecondary"),
            ("weaponSetups.1.attachment.0", "loadoutSecondaryAttachment"),
            ("weaponSetups.1.attachment.1", "loadoutSecondaryAttachment2"),
            ("weaponSetups.1.camo", "loadoutSecondaryCamo"),
            ("perks.0", "loadoutEquipment"),
            ("perks.1", "loadoutPerk1"),
            ("perks.2", "loadoutPerk2"),
            ("perks.3", "loadoutPerk3"),
            ("perks.4", "loadoutDeathStreak"),
            ("specialGrenade", "loadoutOffHand"),
        ];
        for i in 0..CUSTOM_CLASSES {
            let col = 6 + i % 5;
            let base = format!("customclasses.{i}");
            if self.pdata.contains_key(&format!("{base}.weaponsetups.0.weapon")) {
                continue;
            }
            for (path, src) in fields {
                self.pdata.insert(format!("{base}.{}", path.to_ascii_lowercase()), cell(src, col));
            }
            self.pdata.insert(format!("{base}.name"), format!("Custom Class {}", i + 1));
            self.pdata.insert(format!("{base}.inuse"), "true".into());
        }
        for (k, src) in [("killstreaks.0", "loadoutStreak1"), ("killstreaks.1", "loadoutStreak2"), ("killstreaks.2", "loadoutStreak3")] {
            if !self.pdata.contains_key(k) {
                self.pdata.insert(k.into(), cell(src, 11));
            }
        }
    }

    fn top(&self) -> Option<&OpenMenu> {
        self.stack.last()
    }

    fn open(&self, name: &str) -> Option<&OpenMenu> {
        self.stack.iter().rev().find(|m| m.name == name)
    }
}

// --- expression host -----------------------------------------------------------------------

struct MHost<'a> {
    rt: &'a Runtime,
    d: &'a Data,
    menu: &'a Menu,
}

impl MHost<'_> {
    fn static_dvar_name(&self, index: i32) -> Option<&str> {
        self.menu.dvars.iter().find(|(i, _)| *i == index).map(|(_, n)| n.as_str())
    }

    fn dvar(&self, name: &str) -> String {
        let lower = name.to_ascii_lowercase();
        if let Some(v) = self.rt.dvars.get(&lower) {
            return v.clone();
        }
        // Team names / emblems: mp/factionTable.csv rows for the player's faction (allies) and
        // MW2's default opposing faction (axis).
        let faction = |side: &str, col: usize| {
            let key = if side == "allies" { self.rt.faction.as_str() } else { "opforce_composite" };
            self.d.tables.get("mp/factiontable.csv").and_then(|t| (0..t.rows).find(|&r| t.cell(r, 0).eq_ignore_ascii_case(key)).map(|r| t.cell(r, col).to_owned())).unwrap_or_default()
        };
        match lower.as_str() {
            "g_teamname_allies" => return faction("allies", 1),
            "g_teamname_axis" => return faction("axis", 1),
            "g_teamicon_allies" => return faction("allies", 5),
            "g_teamicon_axis" => return faction("axis", 5),
            _ => {}
        }
        match lower.as_str() {
            "gamemode" => "mp".into(),
            "widescreen" => i32::from(self.rt.screen[0] / self.rt.screen[1].max(1.0) > 1.4).to_string(),
            "hidef" | "ui_multiplayer" => "1".into(),
            _ => String::new(),
        }
    }
}

fn int_of(s: &str) -> i32 {
    s.trim().parse::<i32>().or_else(|_| s.trim().parse::<f32>().map(|f| f as i32)).unwrap_or(0)
}

impl ExprHost for MHost<'_> {
    fn milliseconds(&self) -> i32 {
        self.rt.time_ms
    }

    fn static_dvar_int(&self, index: i32) -> Result<i32, ExprError> {
        let name = self.static_dvar_name(index).ok_or(ExprError::Host("static dvar row"))?;
        Ok(int_of(&self.dvar(name)))
    }

    fn static_dvar_string(&self, index: i32) -> Result<String, ExprError> {
        let name = self.static_dvar_name(index).ok_or(ExprError::Host("static dvar row"))?;
        Ok(self.dvar(name))
    }

    fn dvar_int(&self, name: &str) -> Result<i32, ExprError> {
        Ok(int_of(&self.dvar(name)))
    }

    fn dvar_bool(&self, name: &str) -> Result<i32, ExprError> {
        Ok(i32::from(int_of(&self.dvar(name)) != 0))
    }

    fn dvar_float(&self, name: &str) -> Result<f32, ExprError> {
        Ok(self.dvar(name).trim().parse().unwrap_or(0.0))
    }

    fn dvar_string(&self, name: &str) -> Result<String, ExprError> {
        Ok(self.dvar(name))
    }

    /// The player is always on the allies side (co-op against RoR2's monsters).
    fn team_field(&self, field: &str) -> Result<Operand, ExprError> {
        match field.to_ascii_lowercase().as_str() {
            "name" => Ok(Operand::Str("TEAM_ALLIES".into())),
            _ => Err(ExprError::Host("team field")),
        }
    }

    fn player_field(&self, _field: &str) -> Result<Operand, ExprError> {
        Err(ExprError::Host("player field"))
    }

    fn other_team_field(&self, _field: &str) -> Result<Operand, ExprError> {
        Err(ExprError::Host("other team field"))
    }

    fn local_var_string(&self, name: &str) -> Result<Operand, ExprError> {
        Ok(self.rt.locals.get(&name.to_ascii_lowercase()).cloned().unwrap_or(Operand::Str(String::new())))
    }

    fn time_left(&self) -> Result<i32, ExprError> {
        Ok(0)
    }

    fn score_at_rank(&self, _rank: i32) -> Result<i32, ExprError> {
        Ok(0)
    }

    fn gametype_name(&self) -> Result<Operand, ExprError> {
        Ok(Operand::Str(String::new()))
    }

    fn weapon_lock(&self) -> Result<WeaponLockView, ExprError> {
        Ok(WeaponLockView::default())
    }

    fn localize_string(&self, args: &[Operand]) -> Result<String, ExprError> {
        let Some(first) = args.first() else { return Ok(String::new()) };
        let key = operand_text(first);
        let mut text = localize(&self.d.hud, &key);
        for (i, a) in args.iter().enumerate().skip(1) {
            let v = localize(&self.d.hud, &operand_text(a));
            text = text.replace(&format!("&&{i}"), &v);
        }
        Ok(text)
    }

    fn table_lookup(&self, table: &str, col0: i32, key: &str, result_col: i32) -> Result<Operand, ExprError> {
        let Some(t) = self.d.tables.get(&table.to_ascii_lowercase()) else { return Ok(Operand::Str(String::new())) };
        let (col0, col) = (col0.max(0) as usize, result_col.max(0) as usize);
        Ok(Operand::Str(match (0..t.rows).find(|&r| t.cell(r, col0).eq_ignore_ascii_case(key)) {
            Some(r) => t.cell(r, col).to_owned(),
            None => String::new(),
        }))
    }

    fn table_lookup_by_row(&self, table: &str, row: i32, col: i32) -> Result<Operand, ExprError> {
        let Some(t) = self.d.tables.get(&table.to_ascii_lowercase()) else { return Ok(Operand::Str(String::new())) };
        Ok(Operand::Str(if row >= 0 && (row as usize) < t.rows { t.cell(row as usize, col.max(0) as usize).to_owned() } else { String::new() }))
    }

    fn player_data(&self, path: &[Operand]) -> Result<Operand, ExprError> {
        let parts: Vec<String> = path.iter().map(operand_text).collect();
        // customClasses[n].inUse is the engine's (the menus show a custom class only when set):
        // from the player's rank and prestige, not what's saved (playtest 10-04-26: every class at level 1).
        if parts.len() == 3 && parts[0].eq_ignore_ascii_case("customclasses") && parts[2].eq_ignore_ascii_case("inuse") {
            let n: usize = parts[1].parse().unwrap_or(usize::MAX);
            return Ok(Operand::Int(i32::from(n < custom_class_slots(self.rt, self.d))));
        }
        // MW2's three default killstreaks come unlocked (the Create-a-Streak menu leaves them out of
        // its "unlocks available" count but still checks the flag to pick them): unset reads true,
        // or one taken out could never go back in (playtest 10-04-26).
        if parts.len() == 2 && parts[0].eq_ignore_ascii_case("killstreakunlocked") && ["uav", "airdrop", "predator_missile"].iter().any(|d| parts[1].eq_ignore_ascii_case(d)) && !self.rt.pdata.contains_key(&pkey(&parts)) {
            return Ok(Operand::Int(1));
        }
        let v = self.rt.pdata.get(&pkey(&parts)).cloned().unwrap_or_default();
        // Bool fields (inUse, featureNew ...) are ints to the menus; setPlayerData writes "true".
        if v.eq_ignore_ascii_case("true") || v.eq_ignore_ascii_case("false") {
            return Ok(Operand::Int(i32::from(v.eq_ignore_ascii_case("true"))));
        }
        Ok(match v.parse::<i32>() {
            Ok(n) => Operand::Int(n),
            Err(_) => Operand::Str(v),
        })
    }

    fn is_item_unlocked(&self, item: &str) -> Result<i32, ExprError> {
        Ok(i32::from(unlocked(self.rt, self.d, item)))
    }

    fn get_perk(&self, _name: &str) -> Result<Operand, ExprError> {
        Ok(Operand::Int(0))
    }

    fn ui_active(&self) -> Result<i32, ExprError> {
        Ok(1)
    }

    /// `getFocusedItemX/Y/Width/Height`: the focused item of the menu being evaluated (popups
    /// open level with the button that opened them).
    fn focused_item_rect(&self) -> Result<[f32; 4], ExprError> {
        let open = self.rt.stack.iter().rev().find(|m| m.name == self.menu.name).ok_or(ExprError::Host("menu not open"))?;
        let it = open.focus.and_then(|i| self.menu.items.get(i)).ok_or(ExprError::Host("no focus"))?;
        Ok(it.rect)
    }

    fn menu_is_open(&self, name: &str) -> Result<i32, ExprError> {
        Ok(i32::from(self.rt.stack.iter().any(|m| m.name.eq_ignore_ascii_case(name))))
    }

    fn flashbanged(&self) -> Result<i32, ExprError> {
        Ok(0)
    }
}

/// `@KEY` / `KEY` -> localized text (the key itself if it's not a localize key).
fn localize(d: &HudData, s: &str) -> String {
    let key = s.strip_prefix('@').unwrap_or(s);
    d.localize.get(&key.to_ascii_lowercase()).cloned().unwrap_or_else(|| s.to_owned())
}

// --- scripts -------------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq)]
enum Tok {
    Word(String),
    Open,
    Close,
    Comma,
    Semi,
}

fn tokenize(s: &str) -> Vec<Tok> {
    let mut out = Vec::new();
    let mut it = s.chars().peekable();
    while let Some(&c) = it.peek() {
        match c {
            '"' => {
                it.next();
                let mut w = String::new();
                for c in it.by_ref() {
                    if c == '"' {
                        break;
                    }
                    w.push(c);
                }
                out.push(Tok::Word(w));
            }
            '(' => {
                it.next();
                out.push(Tok::Open);
            }
            ')' => {
                it.next();
                out.push(Tok::Close);
            }
            ',' => {
                it.next();
                out.push(Tok::Comma);
            }
            ';' => {
                it.next();
                out.push(Tok::Semi);
            }
            c if c.is_whitespace() => {
                it.next();
            }
            _ => {
                let mut w = String::new();
                while let Some(&c) = it.peek() {
                    if c.is_whitespace() || matches!(c, '"' | '(' | ')' | ',' | ';') {
                        break;
                    }
                    w.push(c);
                    it.next();
                }
                out.push(Tok::Word(w));
            }
        }
    }
    out
}

/// What a script asks the runtime to do; collected while the menu data is borrowed, applied after.
enum Act {
    Open(String),
    Close(String),
    Escape(String),
    FocusFirst,
    SetFocus(String),
    SetColor(String, bool, [f32; 4]),
    Show(String, bool),
    Play(String),
    Save,
    SetDvar(String, String),
    PlayerData(Vec<String>, String),
    Local(String, Operand),
    Response(String),
}

struct Scope<'a> {
    menu: &'a str,
    item: Option<usize>,
}

impl Runtime {
    /// Parse one script string into actions; `localVarInt( name )` / `localVarString( name )` in
    /// setPlayerData arguments read the locals as they are now.
    fn parse_script(&self, text: &str, acts: &mut Vec<Act>) {
        let toks = tokenize(text);
        let mut i = 0;
        let word = |i: usize| match toks.get(i) {
            Some(Tok::Word(w)) => Some(w.clone()),
            _ => None,
        };
        while i < toks.len() {
            let Some(cmd) = word(i) else {
                i += 1;
                continue;
            };
            i += 1;
            let arg = |i: &mut usize| -> String {
                let w = word(*i).unwrap_or_default();
                if matches!(toks.get(*i), Some(Tok::Word(_))) {
                    *i += 1;
                }
                w
            };
            match cmd.to_ascii_lowercase().as_str() {
                "open" | "openforgametype" => acts.push(Act::Open(arg(&mut i))),
                "close" | "closeforgametype" => acts.push(Act::Close(arg(&mut i))),
                "escape" => acts.push(Act::Escape(arg(&mut i))),
                "focusfirst" => acts.push(Act::FocusFirst),
                "setfocus" => acts.push(Act::SetFocus(arg(&mut i))),
                "play" => acts.push(Act::Play(arg(&mut i))),
                "show" => acts.push(Act::Show(arg(&mut i), true)),
                "hide" => acts.push(Act::Show(arg(&mut i), false)),
                "setitemcolor" => {
                    let item = arg(&mut i);
                    let which = arg(&mut i).to_ascii_lowercase();
                    let mut c = [0.0; 4];
                    for v in &mut c {
                        *v = arg(&mut i).trim().parse().unwrap_or(0.0);
                    }
                    acts.push(Act::SetColor(item, which.starts_with("back"), c));
                }
                "setdvar" => {
                    let n = arg(&mut i);
                    let v = arg(&mut i);
                    acts.push(Act::SetDvar(n, v));
                }
                "setlocalvarbool" | "setlocalvarint" | "setlocalvarfloat" | "setlocalvarstring" => {
                    let n = arg(&mut i);
                    let v = arg(&mut i);
                    acts.push(Act::Local(n, Operand::Str(v)));
                }
                "exec" | "execnow" => {
                    let c = arg(&mut i);
                    if c.split(';').any(|p| p.trim().eq_ignore_ascii_case("uploadstats")) {
                        acts.push(Act::Save);
                    }
                }
                "scriptmenuresponse" => acts.push(Act::Response(arg(&mut i))),
                "setplayerdata" => {
                    let mut vals = Vec::new();
                    if toks.get(i) == Some(&Tok::Open) {
                        i += 1;
                        while i < toks.len() && toks[i] != Tok::Close {
                            match &toks[i] {
                                Tok::Word(w) => {
                                    let lw = w.to_ascii_lowercase();
                                    if (lw == "localvarint" || lw == "localvarstring") && toks.get(i + 1) == Some(&Tok::Open) {
                                        let name = word(i + 2).unwrap_or_default().to_ascii_lowercase();
                                        let v = self.locals.get(&name).cloned().unwrap_or(Operand::Str(String::new()));
                                        vals.push(if lw == "localvarint" { int_of(&operand_text(&v)).to_string() } else { operand_text(&v) });
                                        i += 3; // name, ')' consumed below
                                        if toks.get(i) == Some(&Tok::Close) {
                                            i += 1;
                                        }
                                        continue;
                                    }
                                    vals.push(w.clone());
                                }
                                _ => {}
                            }
                            i += 1;
                        }
                        i += 1;
                    }
                    if let Some(v) = vals.pop() {
                        if !vals.is_empty() {
                            acts.push(Act::PlayerData(vals, v));
                        }
                    }
                }
                _ => {
                    // Unhandled command (uiScript, lerp, ...): skip its arguments.
                    while i < toks.len() && toks[i] != Tok::Semi {
                        if matches!(toks.get(i), Some(Tok::Word(w)) if is_command(w)) {
                            break;
                        }
                        i += 1;
                    }
                }
            }
        }
    }

    fn eval_stmts(&self, d: &Data, menu: &Menu, stmts: &[MenuStmt], acts: &mut Vec<Act>) {
        let mut last_if: Option<bool> = None;
        for st in stmts {
            match st {
                MenuStmt::Script(t) => {
                    // Locals set earlier in this handler must be visible to later commands.
                    self.parse_script(t, acts);
                    last_if = None;
                }
                MenuStmt::If(cond, body) => {
                    let host = MHost { rt: self, d, menu };
                    let ok = crate::hud::Exp::compile(cond).is_some_and(|e| e.truthy(&host).unwrap_or(false));
                    last_if = Some(ok);
                    if ok {
                        self.eval_stmts(d, menu, body, acts);
                    }
                }
                MenuStmt::Else(body) => {
                    if last_if == Some(false) {
                        self.eval_stmts(d, menu, body, acts);
                    }
                    last_if = None;
                }
                MenuStmt::SetLocal { kind, name, expr } => {
                    let host = MHost { rt: self, d, menu };
                    let v = match crate::hud::Exp::compile(expr) {
                        Some(e) => match kind {
                            3 => Operand::Int(i32::from(e.truthy(&host).unwrap_or(false))),
                            4 => Operand::Int(e.float(&host).map(|f| f as i32).unwrap_or(0)),
                            5 => Operand::Float(e.float(&host).unwrap_or(0.0)),
                            _ => Operand::Str(e.string(&host).unwrap_or_default()),
                        },
                        // A bare literal (`"false"`, `0`).
                        None => Operand::Str(expr.trim().trim_matches('"').to_owned()),
                    };
                    acts.push(Act::Local(name.clone(), v));
                    last_if = None;
                }
            }
        }
    }
}

const COMMANDS: &[&str] = &[
    "open", "close", "escape", "focusfirst", "setfocus", "play", "show", "hide", "setitemcolor", "setdvar", "exec", "execnow", "setplayerdata",
    "scriptmenuresponse", "uiscript", "lerp", "setlocalvarbool", "setlocalvarint", "setlocalvarfloat", "setlocalvarstring",
];

fn is_command(w: &str) -> bool {
    COMMANDS.iter().any(|c| c.eq_ignore_ascii_case(w))
}

/// Run one handler: statements are evaluated in order and their actions applied as they come, so
/// a `setLocalVar` is seen by the `if` after it.
fn run(rt: &mut Runtime, d: &Data, scope: Scope<'_>, stmts: &[MenuStmt], depth: u32) {
    if depth > 8 {
        return;
    }
    let Some(menu) = d.menus.get(scope.menu) else { return };
    let mut last_if: Option<bool> = None;
    for st in stmts {
        let mut acts = Vec::new();
        match st {
            MenuStmt::If(cond, body) => {
                let ok = {
                    let host = MHost { rt, d, menu };
                    crate::hud::Exp::compile(cond).is_some_and(|e| e.truthy(&host).unwrap_or(false))
                };
                last_if = Some(ok);
                if ok {
                    run(rt, d, Scope { menu: scope.menu, item: scope.item }, body, depth + 1);
                }
                continue;
            }
            MenuStmt::Else(body) => {
                if last_if == Some(false) {
                    run(rt, d, Scope { menu: scope.menu, item: scope.item }, body, depth + 1);
                }
                last_if = None;
                continue;
            }
            other => rt.eval_stmts(d, menu, std::slice::from_ref(other), &mut acts),
        }
        last_if = None;
        for a in acts {
            apply(rt, d, &scope, a, depth);
        }
    }
}

fn apply(rt: &mut Runtime, d: &Data, scope: &Scope<'_>, a: Act, depth: u32) {
    let this = scope.menu;
    match a {
        Act::Open(name) => open(rt, d, &name, depth + 1),
        Act::Close(name) => {
            let name = if name.eq_ignore_ascii_case("self") { this.to_owned() } else { name };
            close(rt, d, &name, depth + 1);
        }
        Act::Escape(name) => {
            let name = if name.eq_ignore_ascii_case("self") { this.to_owned() } else { name };
            escape(rt, d, &name, depth + 1);
        }
        Act::FocusFirst => {
            if let Some(i) = focusable(rt, d, this).first().copied() {
                set_focus(rt, d, this, Some(i), depth + 1);
            }
        }
        Act::SetFocus(item) => {
            let idx = d.menus.get(this).and_then(|m| m.items.iter().position(|it| it.name.eq_ignore_ascii_case(&item)));
            if idx.is_some() {
                set_focus(rt, d, this, idx, depth + 1);
            }
        }
        Act::SetColor(item, back, c) => {
            let Some(menu) = d.menus.get(this) else { return };
            let targets: Vec<usize> = if item.eq_ignore_ascii_case("self") {
                scope.item.into_iter().collect()
            } else {
                menu.items.iter().enumerate().filter(|(_, it)| it.name.eq_ignore_ascii_case(&item)).map(|(i, _)| i).collect()
            };
            if let Some(m) = rt.stack.iter_mut().rev().find(|m| m.name == this) {
                for i in targets {
                    let o = m.colors.entry(i).or_default();
                    if back {
                        o.back = Some(c);
                    } else {
                        o.fore = Some(c);
                    }
                }
            }
        }
        Act::Show(item, on) => {
            if let Some(m) = rt.stack.iter_mut().rev().find(|m| m.name == this) {
                m.shown.insert(item.to_ascii_lowercase(), on);
            }
        }
        Act::Play(alias) => rt.sounds.push(alias),
        Act::Save => rt.save_pdata(),
        Act::SetDvar(n, v) => {
            rt.dvars.insert(n.to_ascii_lowercase(), v);
        }
        Act::PlayerData(path, v) => {
            rt.pdata.insert(pkey(&path), v);
        }
        Act::Local(n, v) => {
            rt.locals.insert(n.to_ascii_lowercase(), v);
        }
        Act::Response(r) => rt.responses.push(r),
    }
}

fn scripts_of(stmts: &[(u8, Vec<MenuStmt>)], kind: u8) -> Option<&[MenuStmt]> {
    stmts.iter().find(|(k, _)| *k == kind).map(|(_, s)| s.as_slice())
}

fn open(rt: &mut Runtime, d: &Data, name: &str, depth: u32) {
    let Some((key, menu)) = d.menus.get_key_value(name).or_else(|| d.menus.iter().find(|(k, _)| k.eq_ignore_ascii_case(name))) else { return };
    if let Some(pos) = rt.stack.iter().position(|m| m.name == *key) {
        let m = rt.stack.remove(pos);
        rt.stack.push(m);
        return;
    }
    rt.stack.push(OpenMenu { name: key.clone(), focus: None, hover: None, colors: HashMap::new(), shown: HashMap::new() });
    if let Some(s) = scripts_of(&menu.scripts, ON_OPEN) {
        run(rt, d, Scope { menu: key, item: None }, s, depth);
    }
}

fn close(rt: &mut Runtime, d: &Data, name: &str, depth: u32) {
    let Some(pos) = rt.stack.iter().position(|m| m.name.eq_ignore_ascii_case(name)) else { return };
    let key = rt.stack[pos].name.clone();
    if let Some(s) = d.menus.get(&key).and_then(|m| scripts_of(&m.scripts, ON_CLOSE)) {
        run(rt, d, Scope { menu: &key, item: None }, s, depth);
    }
    if let Some(pos) = rt.stack.iter().position(|m| m.name == key) {
        rt.stack.remove(pos);
    }
}

fn escape(rt: &mut Runtime, d: &Data, name: &str, depth: u32) {
    match d.menus.get(name).and_then(|m| scripts_of(&m.scripts, ON_ESC)) {
        Some(s) => run(rt, d, Scope { menu: name, item: None }, s, depth),
        None => close(rt, d, name, depth),
    }
}

/// Items of `menu` that take focus (visible, with an action), in item order.
fn focusable(rt: &Runtime, d: &Data, menu: &str) -> Vec<usize> {
    let Some(m) = d.menus.get(menu) else { return Vec::new() };
    let Some(open) = rt.stack.iter().rev().find(|o| o.name == menu) else { return Vec::new() };
    let host = MHost { rt, d, menu: m };
    m.items
        .iter()
        .enumerate()
        .filter(|(_, it)| scripts_of(&it.scripts, ACTION).is_some())
        .filter(|(_, it)| item_visible(&host, open, it))
        .filter(|(_, it)| !it.disabled.as_ref().is_some_and(|e| e.truthy(&host).unwrap_or(false)))
        .map(|(i, _)| i)
        .collect()
}

fn item_visible(host: &MHost<'_>, open: &OpenMenu, it: &crate::hud::Item) -> bool {
    if let Some(on) = open.shown.get(&it.name.to_ascii_lowercase()) {
        if !on {
            return false;
        }
    }
    it.vis.as_ref().is_none_or(|v| v.truthy(host).unwrap_or(false))
}

fn set_focus(rt: &mut Runtime, d: &Data, menu: &str, item: Option<usize>, depth: u32) {
    let Some(old) = rt.stack.iter().rev().find(|m| m.name == menu).map(|m| m.focus) else { return };
    if old == item {
        return;
    }
    let Some(m) = d.menus.get(menu) else { return };
    if let Some(o) = old {
        if let Some(s) = m.items.get(o).and_then(|it| scripts_of(&it.scripts, LEAVE_FOCUS)) {
            run(rt, d, Scope { menu, item: Some(o) }, s, depth);
        }
    }
    if let Some(open) = rt.stack.iter_mut().rev().find(|o| o.name == menu) {
        open.focus = item;
    }
    if let Some(n) = item {
        if let Some(s) = m.items.get(n).and_then(|it| scripts_of(&it.scripts, ON_FOCUS)) {
            run(rt, d, Scope { menu, item: Some(n) }, s, depth);
        }
    }
}

fn activate(rt: &mut Runtime, d: &Data, menu: &str, item: usize) {
    if !focusable(rt, d, menu).contains(&item) {
        return;
    }
    if let Some(s) = d.menus.get(menu).and_then(|m| m.items.get(item)).and_then(|it| scripts_of(&it.scripts, ACTION)) {
        run(rt, d, Scope { menu, item: Some(item) }, s, 0);
    }
}

/// A menu's offset from its float expressions (`chrome.rs::apply_menu_float_rect`).
fn menu_parent(host: &MHost<'_>, menu: &Menu) -> [f32; 4] {
    let mut parent = menu.rect;
    for (key, exp) in &menu.floats {
        if let (0..=3, Ok(v)) = (key, exp.float(host)) {
            parent[*key as usize] = v;
        }
    }
    parent
}

fn menu_visible(host: &MHost<'_>, menu: &Menu) -> bool {
    menu.vis.as_ref().is_none_or(|v| v.truthy(host).unwrap_or(false))
}

/// Screen rect of an item as painted (pixels).
fn item_screen_rect(c: &Ctx, host: &MHost<'_>, menu: &Menu, index: usize, parent: &[f32; 4]) -> Option<[f32; 4]> {
    let it = menu.items.get(index)?;
    let mut scratch = Out::default();
    let s = crate::hud::evaluate_style(&mut scratch, c, host, menu, index, it, parent)?;
    let a = c.pl.apply_rect(s.rect[0], s.rect[1], s.rect[2], s.rect[3], i32::from(it.horz), i32::from(it.vert));
    let (x0, x1) = (a.x.min(a.x + a.w), a.x.max(a.x + a.w));
    let (y0, y1) = (a.y.min(a.y + a.h), a.y.max(a.y + a.h));
    Some([x0, y0, x1 - x0, y1 - y0])
}

// --- input and frames ----------------------------------------------------------------------

pub const KEY_CLICK: u32 = 1;
pub const KEY_ESC: u32 = 2;
pub const KEY_UP: u32 = 4;
pub const KEY_DOWN: u32 = 8;
pub const KEY_ENTER: u32 = 16;
/// F1: the menu's execKey handler. The zone reader keeps a handler's script but not its key code,
/// so this runs the menu's handlers as one; Create-a-Streak's only one is F1 "Clear Killstreaks".
pub const KEY_F1: u32 = 32;

/// The menu that takes input: the topmost one with something to focus (MW2's previews, e.g.
/// `cac_popup_preview` over `cac_popup`, open on top without taking the keys).
fn input_menu(rt: &Runtime, d: &Data) -> Option<String> {
    rt.stack.iter().rev().find(|m| !focusable(rt, d, &m.name).is_empty()).or_else(|| rt.top()).map(|m| m.name.clone())
}

fn input(rt: &mut Runtime, d: &Data, x: f32, y: f32, keys: u32) {
    rt.cursor = [x, y];
    let Some(top) = input_menu(rt, d) else { return };
    let Some(menu) = d.menus.get(&top) else { return };
    let ctx = Ctx::for_menus(rt.screen[0], rt.screen[1], rt.time_ms);
    // Mouse: the first focusable item under the cursor, in item order (IW4 / Q3 ui_shared
    // Menu_HandleMouseMove: the first item that takes focus keeps it). Create-a-Streak stacks three
    // buttons per row (select-or-unlock first, then locked / no-token variants that only click);
    // taking the last one made locked streaks impossible to unlock (playtest 10-03-26).
    let under = {
        let host = MHost { rt, d, menu };
        let parent = menu_parent(&host, menu);
        focusable(rt, d, &top)
            .into_iter()
            .find(|&i| item_screen_rect(&ctx, &host, menu, i, &parent).is_some_and(|r| x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]))
    };
    let hover = rt.open(&top).and_then(|m| m.hover);
    if under != hover {
        if let Some(h) = hover {
            if let Some(s) = menu.items.get(h).and_then(|it| scripts_of(&it.scripts, MOUSE_EXIT)) {
                run(rt, d, Scope { menu: &top, item: Some(h) }, s, 0);
            }
        }
        if let Some(m) = rt.stack.iter_mut().rev().find(|m| m.name == top) {
            m.hover = under;
        }
        if let Some(u) = under {
            if let Some(s) = menu.items.get(u).and_then(|it| scripts_of(&it.scripts, MOUSE_ENTER)) {
                run(rt, d, Scope { menu: &top, item: Some(u) }, s, 0);
            }
            set_focus(rt, d, &top, Some(u), 0);
        }
    }
    if keys & (KEY_UP | KEY_DOWN) != 0 {
        let list = focusable(rt, d, &top);
        if !list.is_empty() {
            let cur = rt.open(&top).and_then(|m| m.focus).and_then(|f| list.iter().position(|&i| i == f));
            let next = match (cur, keys & KEY_DOWN != 0) {
                (None, _) => 0,
                (Some(p), true) => (p + 1) % list.len(),
                (Some(p), false) => (p + list.len() - 1) % list.len(),
            };
            set_focus(rt, d, &top, Some(list[next]), 0);
        }
    }
    if keys & KEY_CLICK != 0 {
        if let Some(u) = under {
            activate(rt, d, &top, u);
        }
    } else if keys & KEY_ENTER != 0 {
        if let Some(f) = rt.open(&top).and_then(|m| m.focus) {
            activate(rt, d, &top, f);
        }
    }
    if keys & KEY_F1 != 0 {
        if let Some(s) = d.menus.get(&top).and_then(|m| scripts_of(&m.scripts, EXEC_KEY)) {
            run(rt, d, Scope { menu: &top, item: None }, s, 0);
        }
    }
    if keys & KEY_ESC != 0 {
        escape(rt, d, &top, 0);
    }
}

fn frame(rt: &Runtime, d: &Data) -> Out {
    let c = Ctx::for_menus(rt.screen[0], rt.screen[1], rt.time_ms);
    let mut out = Out::default();
    if let Some((name, count)) = &rt.backdrop {
        if let Some(menu) = d.menus.get(name) {
            let host = MHost { rt, d, menu };
            let parent = menu_parent(&host, menu);
            for (index, item) in menu.items.iter().enumerate().take(*count) {
                crate::hud::paint_item(&mut out, &c, &d.hud, &host, menu, index, item, &parent, None);
            }
        }
    }
    // IW4 paints from the topmost fullscreen menu up; what's under it isn't drawn.
    let from = rt.stack.iter().rposition(|m| d.menus.get(&m.name).is_some_and(|m| m.fullscreen)).unwrap_or(0);
    for open in &rt.stack[from..] {
        let Some(menu) = d.menus.get(&open.name) else { continue };
        let host = MHost { rt, d, menu };
        if !menu_visible(&host, menu) {
            continue;
        }
        let parent = menu_parent(&host, menu);
        for (index, item) in menu.items.iter().enumerate() {
            if open.shown.get(&item.name.to_ascii_lowercase()) == Some(&false) {
                continue;
            }
            crate::hud::paint_item(&mut out, &c, &d.hud, &host, menu, index, item, &parent, open.colors.get(&index));
        }
    }
    dest_alpha_masks(&mut out);
    out
}

/// MW2's menus mask with the destination alpha channel: materials that write only alpha (the
/// popup box `mockup_popup_bg_stencilfill`, `xpbar_stencilbase` clearing it) are never seen, and
/// materials blended by `DESTALPHA` (`mw2_popup_bg_fogscroll`) show only where alpha was written.
/// The host draws plain alpha-blended quads, so the writers become a clip rect here and the
/// dest-alpha layers are cropped to it (soft mask edges are lost).
fn dest_alpha_masks(out: &mut Out) {
    const COLOR_WRITE_RGB: u32 = 0x0800_0000;
    const COLOR_WRITE_ALPHA: u32 = 0x1000_0000;
    const BLEND_ZERO: u32 = 1;
    const BLEND_DESTALPHA: u32 = 7;
    const OP_MIN: u32 = 4;
    let mut clip: Option<[f32; 4]> = None;
    let strings = &out.strings;
    out.cmds.retain_mut(|c| {
        if c.kind != 0 {
            return true;
        }
        let name = strings.get(usize::from(c.material)).map_or("", String::as_str);
        let Some([w0, _]) = crate::images::state_bits(name) else { return true };
        let (src, dst, op) = (w0 & 0xf, (w0 >> 4) & 0xf, (w0 >> 8) & 7);
        if w0 & (COLOR_WRITE_RGB | COLOR_WRITE_ALPHA) == COLOR_WRITE_ALPHA {
            let r = c.rect;
            clip = if src == BLEND_ZERO && dst == BLEND_ZERO {
                None
            } else if op == OP_MIN {
                clip.map(|k| intersect(k, r))
            } else {
                Some(clip.map_or(r, |k| union(k, r)))
            };
            return false;
        }
        if src == BLEND_DESTALPHA {
            let Some(k) = clip else { return false };
            return crop(c, k);
        }
        true
    });
}

fn intersect(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    let (x0, y0) = (a[0].max(b[0]), a[1].max(b[1]));
    let (x1, y1) = ((a[0] + a[2]).min(b[0] + b[2]), (a[1] + a[3]).min(b[1] + b[3]));
    [x0, y0, (x1 - x0).max(0.0), (y1 - y0).max(0.0)]
}

fn union(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    let (x0, y0) = (a[0].min(b[0]), a[1].min(b[1]));
    let (x1, y1) = ((a[0] + a[2]).max(b[0] + b[2]), (a[1] + a[3]).max(b[1] + b[3]));
    [x0, y0, x1 - x0, y1 - y0]
}

/// Crop a quad to `k`, moving its texture coordinates with the edges. False if nothing is left.
fn crop(c: &mut Mw2HudCmd, k: [f32; 4]) -> bool {
    let r = c.rect;
    let n = intersect(r, k);
    if n[2] <= 0.0 || n[3] <= 0.0 || r[2] <= 0.0 || r[3] <= 0.0 {
        return false;
    }
    let (fx0, fy0) = ((n[0] - r[0]) / r[2], (n[1] - r[1]) / r[3]);
    let (fx1, fy1) = ((n[0] + n[2] - r[0]) / r[2], (n[1] + n[3] - r[1]) / r[3]);
    let [s0, t0, s1, t1] = c.uv;
    c.uv = [s0 + (s1 - s0) * fx0, t0 + (t1 - t0) * fy0, s0 + (s1 - s0) * fx1, t0 + (t1 - t0) * fy1];
    c.rect = n;
    true
}

/// Errors expressions of an open menu stack hit (tests and the pilot's diagnostics).
pub fn diagnose() -> Vec<String> {
    let (Ok(g), Ok(r)) = (DATA.lock(), RT.lock()) else { return Vec::new() };
    let (Some(d), Some(rt)) = (g.as_ref(), r.as_ref()) else { return Vec::new() };
    let c = Ctx::for_menus(rt.screen[0].max(1.0), rt.screen[1].max(1.0), rt.time_ms);
    let mut out = Out { diag_on: true, ..Out::default() };
    for open in &rt.stack {
        let Some(menu) = d.menus.get(&open.name) else { continue };
        let host = MHost { rt, d, menu };
        let parent = menu_parent(&host, menu);
        for (index, item) in menu.items.iter().enumerate() {
            crate::hud::paint_item(&mut out, &c, &d.hud, &host, menu, index, item, &parent, None);
        }
    }
    out.diag
}

fn with<T>(f: impl FnOnce(&mut Runtime, &Data) -> T) -> Option<T> {
    let g = DATA.lock().ok()?;
    let d = g.as_ref()?;
    let mut r = RT.lock().ok()?;
    let rt = r.as_mut()?;
    Some(f(rt, d))
}

pub fn open_menu(name: &str) -> bool {
    with(|rt, d| {
        // Menus open between fights (each stage's class select): flush held challenge progress.
        if rt.unsaved > 0 {
            rt.unsaved = 0;
            rt.save_pdata();
        }
        open(rt, d, name, 0);
        rt.stack.iter().any(|m| m.name.eq_ignore_ascii_case(name))
    })
    .unwrap_or(false)
}

pub fn set_local(name: &str, v: Operand) {
    with(|rt, _| {
        rt.locals.insert(name.to_ascii_lowercase(), v);
    });
}

pub fn player_data(path: &str) -> String {
    with(|rt, _| rt.pdata.get(&path.to_ascii_lowercase()).cloned().unwrap_or_default()).unwrap_or_default()
}

pub fn step(w: f32, h: f32, time_ms: i32, x: f32, y: f32, keys: u32) -> Option<Out> {
    with(|rt, d| {
        rt.screen = [w.max(1.0), h.max(1.0)];
        rt.time_ms = time_ms;
        input(rt, d, x, y, keys);
        frame(rt, d)
    })
}

pub fn open_names() -> Vec<String> {
    with(|rt, _| rt.stack.iter().map(|m| m.name.clone()).collect()).unwrap_or_default()
}

// --- unlocks and challenges -----------------------------------------------------------------

fn table<'a>(d: &'a Data, name: &str) -> Option<&'a StringTable> {
    d.tables.get(name)
}

fn row_of(t: &StringTable, col: usize, key: &str) -> Option<usize> {
    (0..t.rows).find(|&r| t.cell(r, col).eq_ignore_ascii_case(key))
}

fn pdata_int(rt: &Runtime, key: &str) -> i64 {
    rt.pdata.get(&key.to_ascii_lowercase()).and_then(|v| v.trim().parse().ok()).unwrap_or(0)
}

/// The player's rank id from their XP (rankTable: column 0 id, column 2 min XP).
fn rank_of(rt: &Runtime, d: &Data) -> i32 {
    let xp = pdata_int(rt, "experience");
    let Some(t) = table(d, "mp/ranktable.csv") else { return 0 };
    let mut best = 0;
    for r in 0..t.rows {
        if let (Ok(id), Ok(min)) = (t.cell(r, 0).parse::<i32>(), t.cell(r, 2).parse::<i64>()) {
            if xp >= min {
                best = best.max(id);
            }
        }
    }
    best
}

/// `ch_marksman_ak47_2` -> (`ch_marksman_ak47`, 2); a name that is itself a challenge -> tier 1.
fn challenge_ref(d: &Data, r: &str) -> (String, i64) {
    let t = table(d, "mp/allchallengestable.csv");
    if t.is_some_and(|t| row_of(t, 0, r).is_some()) {
        return (r.to_ascii_lowercase(), 1);
    }
    match r.rsplit_once('_') {
        Some((base, n)) if !n.is_empty() && n.chars().all(|c| c.is_ascii_digit()) => (base.to_ascii_lowercase(), n.parse().unwrap_or(1)),
        _ => (r.to_ascii_lowercase(), 1),
    }
}

/// challengeState: 1 while tier 1 is in progress, N+1 once tier N is done.
fn challenge_done(rt: &Runtime, d: &Data, r: &str) -> bool {
    let (name, tier) = challenge_ref(d, r);
    pdata_int(rt, &format!("challengestate.{name}")).max(1) > tier
}

/// Custom class slots the player has: none before Create-a-Class (unlockTable "cac", level 4);
/// then 5, one more per prestige up to 10 (MW2's prestige class slots - from memory of the game,
/// not a table in the zones).
fn custom_class_slots(rt: &Runtime, d: &Data) -> usize {
    if !unlocked(rt, d, "cac") {
        return 0;
    }
    (5 + pdata_int(rt, "prestige").clamp(0, 5) as usize).min(CUSTOM_CLASSES)
}

/// `custom_class_slots` for the host (the class row, the class played).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_menu_custom_class_slots() -> u32 {
    catch_unwind(|| with(|rt, d| custom_class_slots(rt, d) as u32).unwrap_or(0)).unwrap_or(0)
}

/// MW2's isItemUnlocked: the unlockTable row (column 2 rank, column 3 challenge) against the
/// player's rank and challenges. Items the table doesn't list are open.
fn unlocked(rt: &Runtime, d: &Data, item: &str) -> bool {
    let Some(t) = table(d, "mp/unlocktable.csv") else { return true };
    let Some(r) = row_of(t, 0, item) else { return true };
    let rank: i32 = t.cell(r, 2).trim().parse().unwrap_or(0);
    let ch = t.cell(r, 3).trim();
    // Prestige 10 at the top rank (id 69, "level 70") opens the prestige-10 rank-70 items.
    let have = rank_of(rt, d) + i32::from(pdata_int(rt, "prestige") >= 10);
    have >= rank && (ch.is_empty() || challenge_done(rt, d, ch))
}

/// Everything unlocked (playtest 10-03-26: friends test the whole game): every tier of every
/// challenge done (allChallengesTable targets met: attachments, camos, titles, emblems) and the
/// XP of the top rank (rankTable's last "next rank" XP). Saved to the player data. Returns
/// (challenges completed, XP).
pub fn unlock_all() -> Option<(usize, i64)> {
    with(|rt, d| {
        let mut n = 0;
        if let Some(t) = table(d, "mp/allchallengestable.csv") {
            for r in 0..t.rows {
                let name = t.cell(r, 0).trim().to_ascii_lowercase();
                if !name.starts_with("ch_") && !name.starts_with("pr_") {
                    continue;
                }
                let mut tiers = 0i64;
                let mut last = 0i64;
                while let Ok(target) = t.cell(r, 6 + tiers as usize * 2).trim().parse::<i64>() {
                    last = target;
                    tiers += 1;
                }
                // Target-less challenges (ch_prestige_10: reaching the rank is the challenge): tier 1.
                let tiers = tiers.max(1);
                rt.pdata.insert(format!("challengeprogress.{name}"), last.to_string());
                rt.pdata.insert(format!("challengestate.{name}"), (tiers + 1).to_string());
                n += 1;
            }
        }
        let xp = table(d, "mp/ranktable.csv")
            .map(|t| (0..t.rows).filter_map(|r| t.cell(r, 7).trim().parse::<i64>().ok()).max().unwrap_or(0))
            .unwrap_or(0);
        rt.pdata.insert("experience".into(), xp.to_string());
        rt.pdata.insert("prestige".into(), "10".into());
        rt.save_pdata();
        (n, xp)
    })
}

/// One completed challenge tier: (challenge, tier, XP reward).
pub type Completion = (String, i64, i64);

/// Add `amount` to a challenge and complete the tiers it reaches (allChallengesTable: columns
/// 6, 8, .. targets; 7, 9, .. XP).
fn challenge_add(rt: &mut Runtime, d: &Data, name: &str, amount: i64) -> Vec<Completion> {
    let mut done = Vec::new();
    let Some(t) = table(d, "mp/allchallengestable.csv") else { return done };
    let Some(r) = row_of(t, 0, name) else { return done };
    let key = name.to_ascii_lowercase();
    let progress = pdata_int(rt, &format!("challengeprogress.{key}")) + amount;
    rt.pdata.insert(format!("challengeprogress.{key}"), progress.to_string());
    let mut state = pdata_int(rt, &format!("challengestate.{key}")).max(1);
    loop {
        let col = 6 + (state as usize - 1) * 2;
        let Ok(target) = t.cell(r, col).trim().parse::<i64>() else { break };
        if progress < target {
            break;
        }
        let xp = t.cell(r, col + 1).trim().parse::<i64>().unwrap_or(0);
        done.push((key.clone(), state, xp));
        state += 1;
    }
    rt.pdata.insert(format!("challengestate.{key}"), state.to_string());
    done
}

/// A kill with `weapon` (full name, `ak47_gl_mp`): its Marksman, Expert (headshot) and
/// kills-with-attachment challenges.
pub fn challenge_kill(weapon: &str, headshot: bool) -> Vec<Completion> {
    with(|rt, d| {
        let name = weapon.strip_suffix("_mp").unwrap_or(weapon);
        let mut parts = name.split('_');
        let base = parts.next().unwrap_or("").to_owned();
        let atts: Vec<&str> = parts.collect();
        let mut done = challenge_add(rt, d, &format!("ch_marksman_{base}"), 1);
        if headshot {
            done.extend(challenge_add(rt, d, &format!("ch_expert_{base}"), 1));
        }
        for a in atts {
            done.extend(challenge_add(rt, d, &format!("ch_{base}_{a}"), 1));
        }
        save_progress(rt, !done.is_empty());
        done
    })
    .unwrap_or_default()
}

/// Write playerdata off the game thread (a write took ~40 ms there: a hitch on a challenge tier).
/// Newer snapshots win: a writer that finds a later one already written skips.
#[cfg(not(test))]
fn write_pdata(path: std::path::PathBuf, text: String) {
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(1);
    // Per file: with two profiles, a newer save of one must not cancel the other's.
    static WRITTEN: Mutex<Option<HashMap<std::path::PathBuf, u64>>> = Mutex::new(None);
    let snapshot = NEXT.fetch_add(1, Ordering::SeqCst);
    let job = move || {
        let mut written = WRITTEN.lock().unwrap_or_else(|e| e.into_inner());
        let map = written.get_or_insert_with(HashMap::new);
        if map.get(&path).is_some_and(|&w| w > snapshot) {
            return;
        }
        if let Some(dir) = path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        let _ = std::fs::write(&path, text);
        map.insert(path, snapshot);
    };
    if let Err(e) = std::thread::Builder::new().name("mw2-pdata-save".into()).spawn(job) {
        eprintln!("mw2 playerdata save thread: {e}");
    }
}

#[cfg(test)]
fn write_pdata(path: std::path::PathBuf, text: String) {
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let _ = std::fs::write(&path, text);
}

/// Writing playerdata on every kill hitched the game (playtest): save when a tier completes, else every
/// 10th update.
fn save_progress(rt: &mut Runtime, completed: bool) {
    rt.unsaved += 1;
    if completed || rt.unsaved >= 10 {
        rt.unsaved = 0;
        rt.save_pdata();
    }
}

/// Progress on any challenge by name (perk Pro challenges the host feeds: miles run, SoH kills ...).
pub fn challenge_progress(name: &str, amount: i64) -> Vec<Completion> {
    with(|rt, d| {
        let done = challenge_add(rt, d, name, amount);
        save_progress(rt, !done.is_empty());
        done
    })
    .unwrap_or_default()
}

/// Is `item` unlocked for the player (unlockTable key)?
pub fn item_unlocked(item: &str) -> bool {
    with(|rt, d| unlocked(rt, d, item)).unwrap_or(true)
}

/// Set a player-data value (`experience`).
pub fn set_player_data(path: &str, value: &str) {
    with(|rt, _| {
        rt.pdata.insert(path.to_ascii_lowercase(), value.to_owned());
    });
}

// --- loadouts ------------------------------------------------------------------------------

/// `_class.gsc::buildWeaponName`: base name, attachments ordered alphabetically, `_mp`. Falls
/// back to fewer attachments if the combo isn't a weapon the zone defines.
pub fn build_weapon_name(base: &str, a1: &str, a2: &str) -> String {
    let mut atts: Vec<&str> = [a1, a2].into_iter().filter(|a| !a.is_empty() && *a != "none").collect();
    atts.sort_unstable();
    atts.dedup();
    let name = |atts: &[&str]| {
        let mut n = base.to_owned();
        for a in atts {
            n.push('_');
            n.push_str(a);
        }
        n.push_str("_mp");
        n
    };
    for cand in [name(&atts), name(&atts[..atts.len().min(1)]), name(&[])] {
        if crate::weapons::index_of(&cand) != 0 {
            return cand;
        }
    }
    name(&atts)
}

/// A custom class as the game needs it: primary and secondary weapon names, lethal, tactical and
/// its count (statsTable column 5), the three killstreaks, perks 1-3 and the death streak.
/// A custom class field as the game hands it out (_class.gsc giveLoadout): items the player hasn't
/// unlocked fall back to the class's MW2 default (classTable columns 6-10, what new classes start
/// as), and the second attachments count only with Bling in perk 1.
fn custom_field(rt: &Runtime, d: &Data, class: usize, k: &str) -> String {
    let raw = |k: &str| rt.pdata.get(&format!("customclasses.{class}.{k}")).cloned().unwrap_or_default();
    let default = |src: &str| {
        let col = 6 + class % 5;
        table(d, "mp/classtable.csv").and_then(|t| row_of(t, 0, src).map(|r| t.cell(r, col).to_owned())).unwrap_or_default()
    };
    let weapon = |slot: &str| {
        let w = raw(&format!("weaponsetups.{slot}.weapon"));
        if unlocked(rt, d, &w) { w } else { default(if slot == "0" { "loadoutPrimary" } else { "loadoutSecondary" }) }
    };
    match k {
        "weaponsetups.0.weapon" => weapon("0"),
        "weaponsetups.1.weapon" => weapon("1"),
        "weaponsetups.0.attachment.0" | "weaponsetups.0.attachment.1" | "weaponsetups.1.attachment.0" | "weaponsetups.1.attachment.1" => {
            let slot = &k[13..14];
            let second = k.ends_with(".1");
            if second && raw("perks.1") != "specialty_bling" {
                return "none".into();
            }
            let a = raw(k);
            let base = weapon(slot);
            if a.is_empty() || a == "none" || base != raw(&format!("weaponsetups.{slot}.weapon")) || !unlocked(rt, d, &format!("{base} {a}")) {
                "none".into()
            } else {
                a
            }
        }
        "perks.1" | "perks.2" | "perks.3" => {
            let p = raw(k);
            if unlocked(rt, d, &p) { p } else { default(&format!("loadoutPerk{}", &k[6..7])) }
        }
        _ => raw(k),
    }
}

/// `class` 0-9 are the custom classes, 10-14 MW2's default classes (classTable columns 1-5, the
/// `changeclass` menu's `class0` .. `class4`).
pub fn class_loadout(class: usize) -> Option<Vec<String>> {
    with(|rt, d| {
        let default_class = class.checked_sub(CUSTOM_CLASSES).filter(|&c| c < 5);
        let class_table = d.tables.get("mp/classtable.csv");
        let table_field = |name: &str| -> Option<String> {
            let (c, t) = (default_class?, class_table?);
            (0..t.rows).find(|&r| t.cell(r, 0).eq_ignore_ascii_case(name)).map(|r| t.cell(r, c + 1).to_owned())
        };
        let get = |k: &str| match default_class {
            Some(_) => {
                let src = match k {
                    "weaponsetups.0.weapon" => "loadoutPrimary",
                    "weaponsetups.0.attachment.0" => "loadoutPrimaryAttachment",
                    "weaponsetups.0.attachment.1" => "loadoutPrimaryAttachment2",
                    "weaponsetups.1.weapon" => "loadoutSecondary",
                    "weaponsetups.1.attachment.0" => "loadoutSecondaryAttachment",
                    "weaponsetups.1.attachment.1" => "loadoutSecondaryAttachment2",
                    "perks.0" => "loadoutEquipment",
                    "perks.1" => "loadoutPerk1",
                    "perks.2" => "loadoutPerk2",
                    "perks.3" => "loadoutPerk3",
                    "perks.4" => "loadoutDeathStreak",
                    "specialgrenade" => "loadoutOffhand",
                    _ => "",
                };
                table_field(src).unwrap_or_default()
            }
            None => custom_field(rt, d, class, k),
        };
        let weapon = |slot: u32| {
            let base = get(&format!("weaponsetups.{slot}.weapon"));
            if base.is_empty() || base == "none" {
                return String::new();
            }
            build_weapon_name(&base, &get(&format!("weaponsetups.{slot}.attachment.0")), &get(&format!("weaponsetups.{slot}.attachment.1")))
        };
        let lookup = |table: &str, key: &str, col: usize| {
            d.tables.get(table).and_then(|t| (0..t.rows).find(|&r| t.cell(r, 4).eq_ignore_ascii_case(key)).map(|r| t.cell(r, col).to_owned())).unwrap_or_default()
        };
        // Camo per weapon slot, if the player has it for that gun (unlockTable "ak47 woodland").
        let camo = |slot: u32| {
            let c = get(&format!("weaponsetups.{slot}.camo"));
            let base = get(&format!("weaponsetups.{slot}.weapon"));
            if c.is_empty() || c == "none" || !unlocked(rt, d, &format!("{base} {c}")) { "none".to_owned() } else { c }
        };
        let special = get("specialgrenade");
        let tactical = if special.is_empty() || special == "none" { String::new() } else { format!("{special}_mp") };
        let tactical_count = lookup("mp/statstable.csv", &special, 5);
        vec![
            weapon(0),
            weapon(1),
            get("perks.0"),
            tactical,
            tactical_count,
            rt.pdata.get("killstreaks.0").cloned().unwrap_or_default(),
            rt.pdata.get("killstreaks.1").cloned().unwrap_or_default(),
            rt.pdata.get("killstreaks.2").cloned().unwrap_or_default(),
            get("perks.1"),
            get("perks.2"),
            get("perks.3"),
            get("perks.4"),
            camo(0),
            camo(1),
        ]
    })
}

// --- C ABI ---------------------------------------------------------------------------------

fn str_arg<'a>(p: *const u8, len: usize) -> Option<&'a str> {
    if p.is_null() {
        return Some("");
    }
    std::str::from_utf8(unsafe { std::slice::from_raw_parts(p, len) }).ok()
}

fn copy_out(s: &str, out: *mut u8, cap: u32) -> u32 {
    let b = s.as_bytes();
    if !out.is_null() {
        let n = b.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(b.as_ptr(), out, n) };
    }
    b.len() as u32
}

/// Load the menus (walks `localized_ui_mp.ff` in the zone directory `mw2_load_weapons` used) and
/// the saved loadouts at `pdata_path` (created on first save). Returns the number of menus.
///
/// # Safety
/// `pdata_path` points to `len` bytes of UTF-8 or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_init(pdata_path: *const u8, len: usize) -> u32 {
    catch_unwind(|| {
        let Some(dir) = crate::hud::ZONE_DIR.lock().ok().and_then(|z| z.clone()) else { return 0 };
        let path = str_arg(pdata_path, len).filter(|s| !s.is_empty()).map(PathBuf::from);
        init(&dir, path).map(|n| n as u32).unwrap_or(0)
    })
    .unwrap_or(0)
}

/// A cell of one of the menus' string tables: the row whose column `key_col` is `key` (any case).
pub fn table_cell(table: &str, key_col: usize, key: &str, col: usize) -> Option<String> {
    let g = DATA.lock().ok()?;
    let t = g.as_ref()?.tables.get(&table.to_ascii_lowercase())?;
    (0..t.rows).find(|&r| t.cell(r, key_col).eq_ignore_ascii_case(key)).map(|r| t.cell(r, col).to_owned())
}

/// Switch the player data (classes, challenges, unlocks, prestige) to another file: the current
/// one is saved first; a file that doesn't exist yet starts from MW2's defaults. 1 on success.
///
/// # Safety
/// `pdata_path` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_switch_pdata(pdata_path: *const u8, len: usize) -> i32 {
    catch_unwind(|| {
        let Some(path) = str_arg(pdata_path, len).filter(|s| !s.is_empty()).map(PathBuf::from) else { return 0 };
        with(|rt, d| {
            rt.save_pdata();
            rt.unsaved = 0;
            rt.pdata.clear();
            rt.pdata_path = Some(path);
            rt.load_pdata();
            rt.seed_defaults(d);
        })
        .map_or(0, |_| 1)
    })
    .unwrap_or(0)
}

/// Typed text for the open edit field (see `type_text`): UTF-8, backspace as U+0008. 1 if taken.
///
/// # Safety
/// `text` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_text(text: *const u8, len: usize) -> i32 {
    if text.is_null() {
        return 0;
    }
    std::panic::catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(text, len) };
        std::str::from_utf8(bytes).map_or(0, |t| i32::from(type_text(t)))
    })
    .unwrap_or(0)
}

/// 1 while an edit field takes typed text.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_menu_editing() -> i32 {
    std::panic::catch_unwind(|| i32::from(editing())).unwrap_or(0)
}

/// Playtest pilot: never write playerdata (its menu tests must not touch the player's classes).
static PDATA_READONLY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

#[unsafe(no_mangle)]
pub extern "C" fn mw2_pdata_readonly(on: i32) {
    PDATA_READONLY.store(on != 0, std::sync::atomic::Ordering::Relaxed);
}

/// Open a menu by name (runs its onOpen). 1 if it is open afterwards.
///
/// # Safety
/// `name` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_open(name: *const u8, len: usize) -> i32 {
    catch_unwind(|| str_arg(name, len).is_some_and(open_menu)).map(i32::from).unwrap_or(0)
}

/// Close every open menu (no scripts run), saving the loadouts.
#[unsafe(no_mangle)]
pub extern "C" fn mw2_menu_close_all() {
    let _ = catch_unwind(|| {
        with(|rt, _| {
            rt.stack.clear();
            rt.save_pdata();
        })
    });
}

/// How many menus are open (0: the host can take its input back).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_menu_depth() -> u32 {
    catch_unwind(|| with(|rt, _| rt.stack.len() as u32).unwrap_or(0)).unwrap_or(0)
}

/// Set a UI local variable (`classIndex` before opening `menu_cac_assault`).
///
/// # Safety
/// `name` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_set_local_int(name: *const u8, len: usize, value: i32) {
    let _ = catch_unwind(|| {
        if let Some(n) = str_arg(name, len) {
            set_local(n, Operand::Int(value));
        }
    });
}

/// Feed input and lay out the open menus: cursor in screen pixels (y down), `keys` the
/// `KEY_*` bits pressed this frame. Commands as `mw2_hud_frame` (strings via `mw2_hud_cmd_string`).
///
/// # Safety
/// `out` has room for `cap` commands.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn mw2_menu_frame(w: f32, h: f32, time_ms: i32, x: f32, y: f32, keys: u32, out: *mut Mw2HudCmd, cap: u32) -> u32 {
    if out.is_null() || cap == 0 {
        return 0;
    }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(o) = step(w, h, time_ms, x, y, keys) else { return 0 };
        let n = o.cmds.len().min(cap as usize);
        unsafe { std::ptr::copy_nonoverlapping(o.cmds.as_ptr(), out, n) };
        if let Ok(mut s) = crate::hud::STRINGS.lock() {
            *s = o.strings;
        }
        n as u32
    }))
    .unwrap_or(0)
}

/// Take what the scripts queued: `what` 0 = sound aliases played, 1 = script menu responses.
/// Newline-separated; returns the full length (copies up to `cap` bytes; call with the length to
/// get it all, the queue is cleared only when it fits).
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_drain(what: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        with(|rt, _| {
            let q = if what == 0 { &mut rt.sounds } else { &mut rt.responses };
            let s = q.join(&char::from(10).to_string());
            let n = copy_out(&s, out, cap);
            if n <= cap {
                q.clear();
            }
            n
        })
        .unwrap_or(0)
    }))
    .unwrap_or(0)
}

/// A UI local variable as an int (`classIndex` after the player picked a class in `cac_popup`).
///
/// # Safety
/// `name` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_local_int(name: *const u8, len: usize) -> i32 {
    catch_unwind(|| {
        let n = str_arg(name, len)?.to_ascii_lowercase();
        with(|rt, _| rt.locals.get(&n).map(|v| int_of(&operand_text(v))))?
    })
    .ok()
    .flatten()
    .unwrap_or(-1)
}

/// Custom class `class` (0-based) as newline-separated fields: primary, secondary, lethal,
/// tactical, tactical count, killstreak 1-3, perk 1-3, death streak. Returns the full length.
///
/// # Safety
/// `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_class_loadout(class: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| class_loadout(class as usize).map(|v| copy_out(&v.join(&char::from(10).to_string()), out, cap)).unwrap_or(0))).unwrap_or(0)
}

/// Unlock everything (`unlock_all`): every challenge tier done, top-rank XP, prestige 10 in the
/// player data. Returns the top-rank XP (0 if the menus aren't loaded); `challenges` gets how
/// many challenges were completed.
///
/// # Safety
/// `challenges` is null or valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_unlock_all(challenges: *mut u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| match unlock_all() {
        Some((n, xp)) => {
            if !challenges.is_null() {
                unsafe { *challenges = n as u32 };
            }
            xp.max(0) as u32
        }
        None => 0,
    }))
    .unwrap_or(0)
}

/// The player's faction (`us_army`, `socom_141` ... mp/factionTable.csv) for team names and emblems.
///
/// # Safety
/// `name` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_set_faction(name: *const u8, len: usize) {
    let _ = catch_unwind(|| {
        if let Some(n) = str_arg(name, len).filter(|n| !n.is_empty()) {
            with(|rt, _| rt.faction = n.to_ascii_lowercase());
        }
    });
}

/// Every row's cell in column `col` of a string table, newline-separated (perkTable names ...).
///
/// # Safety
/// `table` points to `table_len` bytes of UTF-8; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_table_column(table: *const u8, table_len: usize, col: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(t) = str_arg(table, table_len) else { return 0 };
        let v = DATA
            .lock()
            .ok()
            .and_then(|g| {
                let tab = g.as_ref()?.tables.get(&t.to_ascii_lowercase())?;
                Some((0..tab.rows).map(|r| tab.cell(r, col as usize).to_owned()).collect::<Vec<_>>().join(&char::from(10).to_string()))
            })
            .unwrap_or_default();
        copy_out(&v, out, cap)
    }))
    .unwrap_or(0)
}

/// `tableLookup( table, col0, key, col )` over the zones' string tables ("" if not found).
///
/// # Safety
/// `table` / `key` point to their lengths of UTF-8; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_table_lookup(table: *const u8, table_len: usize, col0: u32, key: *const u8, key_len: usize, col: u32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let (Some(t), Some(k)) = (str_arg(table, table_len), str_arg(key, key_len)) else { return 0 };
        let v = DATA
            .lock()
            .ok()
            .and_then(|g| {
                let tab = g.as_ref()?.tables.get(&t.to_ascii_lowercase())?;
                (0..tab.rows).find(|&r| tab.cell(r, col0 as usize).eq_ignore_ascii_case(k)).map(|r| tab.cell(r, col as usize).to_owned())
            })
            .unwrap_or_default();
        copy_out(&v, out, cap)
    }))
    .unwrap_or(0)
}

/// Draw the first `count` items of `menu` under the open menus (0 / empty name: none). MW2's
/// lobby backdrop is main_text's first 9 (everything before its logo and buttons).
///
/// # Safety
/// `menu` points to `len` bytes of UTF-8 or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_set_backdrop(menu: *const u8, len: usize, count: u32) {
    let _ = catch_unwind(|| {
        let name = str_arg(menu, len).unwrap_or("").to_owned();
        with(|rt, _| rt.backdrop = (!name.is_empty() && count > 0).then_some((name, count as usize)));
    });
}

/// Localized text for a key (`MP_MATCH_STARTING_IN`, with or without `@`); the key itself if unknown.
///
/// # Safety
/// `key` points to `len` bytes of UTF-8; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_localize(key: *const u8, len: usize, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(k) = str_arg(key, len) else { return 0 };
        let text = DATA.lock().ok().and_then(|g| g.as_ref().map(|d| localize(&d.hud, k))).unwrap_or_else(|| k.to_owned());
        copy_out(&text, out, cap)
    }))
    .unwrap_or(0)
}

/// Replace a localized string (menus and HUD): text for what something does in the mod where
/// that differs from MW2 (perks reworked for RoR2).
///
/// # Safety
/// `key` / `text` point to their lengths of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_localize_set(key: *const u8, key_len: usize, text: *const u8, text_len: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let (Some(k), Some(t)) = (str_arg(key, key_len), str_arg(text, text_len)) else { return 0 };
        let k = k.trim_start_matches('@').to_ascii_lowercase();
        let mut set = 0;
        if let Ok(mut g) = DATA.lock() {
            if let Some(d) = g.as_mut() {
                d.hud.localize.insert(k.clone(), t.to_owned());
                set = 1;
            }
        }
        if let Ok(mut g) = crate::hud::DATA.write() {
            if let Some(d) = g.as_mut() {
                d.localize.insert(k, t.to_owned());
                set = 1;
            }
        }
        set
    }))
    .unwrap_or(0)
}

/// Set a player-data value (`experience` from the host's XP, so rank unlocks follow it).
///
/// # Safety
/// `path` / `value` point to their lengths of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_set_player_data(path: *const u8, path_len: usize, value: *const u8, value_len: usize) {
    let _ = catch_unwind(|| {
        if let (Some(p), Some(v)) = (str_arg(path, path_len), str_arg(value, value_len)) {
            set_player_data(p, v);
        }
    });
}

/// 1 if MW2 would let the player use `item` (unlockTable key: `m4`, `ak47 gl`, `specialty_bling`).
///
/// # Safety
/// `item` points to `len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_item_unlocked(item: *const u8, len: usize) -> i32 {
    catch_unwind(|| str_arg(item, len).is_none_or(item_unlocked)).map(i32::from).unwrap_or(1)
}

fn completions_out(done: &[Completion], out: *mut u8, cap: u32) -> u32 {
    let s = done.iter().map(|(c, t, x)| format!("{c}|{t}|{x}")).collect::<Vec<_>>().join(&char::from(10).to_string());
    copy_out(&s, out, cap)
}

/// A kill with an MW2 weapon: challenge progress. Returns completed tiers as `name|tier|xp` lines.
///
/// # Safety
/// `weapon` points to `len` bytes of UTF-8; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_challenge_kill(weapon: *const u8, len: usize, headshot: i32, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| str_arg(weapon, len).map(|w| completions_out(&challenge_kill(w, headshot != 0), out, cap)).unwrap_or(0))).unwrap_or(0)
}

/// Progress on a challenge by name. Returns completed tiers as `name|tier|xp` lines.
///
/// # Safety
/// `name` points to `len` bytes of UTF-8; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_challenge_progress(name: *const u8, len: usize, amount: i64, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| str_arg(name, len).map(|n| completions_out(&challenge_progress(n, amount), out, cap)).unwrap_or(0))).unwrap_or(0)
}

/// Read a player-data value (`customClasses.0.weaponSetups.0.weapon`).
///
/// # Safety
/// `path` points to `len` bytes of UTF-8; `out` has room for `cap` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_menu_player_data(path: *const u8, len: usize, out: *mut u8, cap: u32) -> u32 {
    catch_unwind(AssertUnwindSafe(|| str_arg(path, len).map(|p| copy_out(&player_data(p), out, cap)).unwrap_or(0))).unwrap_or(0)
}

#[cfg(test)]
#[path = "menus_tests.rs"]
mod tests;
