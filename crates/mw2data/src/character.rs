//! MW2 third-person player characters, data side: which models a faction wears (read from
//! MW2's own `mptype` / `character` / `xmodelalias` / `_teams` scripts), MW2's player
//! animation script (`mp/playeranim.script`, parsed the way IW4L's `asset_anim::playeranim_parse`
//! does, minus the animtree resolution), and the zone loaders that pull the bodies, heads and
//! `pb_/pl_/pt_` player XAnims out of the user's own zones.
//!
//! The tokenizer and script grammar are ports of IW4L (Apache-2.0); nothing from the game is
//! stored in the repo, everything is read from the install at run time.

use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::{Mutex, RwLock};

use anim_iw4::{
    ANIM_BODY_PART_NAMES, ANIM_COND_IS_BITFLAGS, ANIM_COND_NAMES, ANIM_ET_NAMES, ANIM_MT_NAMES, ANIM_PARSE_MODES,
    ANIM_STATE_NAMES, anim_cond_evaluable, anim_cond_null_value_defaults_to_one, anim_cond_value_names,
};
use xmodel_runtime::RawXAnimParts;

use crate::models::{Mesh, ModelCapture};
use crate::rig::RigModel;

// ---------------------------------------------------------------------------------------------
// Faction -> models
// ---------------------------------------------------------------------------------------------

/// What a faction/class wears: body model, the head pool `attachHead` picks from, viewhands.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CharacterSpec {
    pub faction: String,
    pub class: String,
    /// `mptype_*` script that picked the body (e.g. `mptype_us_army_assault`).
    pub mptype: String,
    /// `character\mp_character_*` script of the chosen variant.
    pub character: String,
    pub body: String,
    pub head_alias: Option<String>,
    pub heads: Vec<String>,
    pub viewhands: Option<String>,
    /// Number of body variants (`get_random_character(N)`).
    pub variants: usize,
}

/// A raw file by name (`/` or `\`, any case).
pub type RawLookup<'a> = &'a dyn Fn(&str) -> Option<&'a [u8]>;

pub fn norm_name(name: &str) -> String {
    name.replace('\\', "/").to_ascii_lowercase()
}

fn text(raw: RawLookup<'_>, name: &str) -> Option<String> {
    raw(name).map(|b| String::from_utf8_lossy(b).into_owned())
}

/// `mptype\NAME::main` for a faction + class, read from `_teams.gsc`'s `setTeamModels`.
pub fn mptype_for(raw: RawLookup<'_>, faction: &str, class: &str) -> Option<String> {
    let teams = text(raw, "maps/mp/gametypes/_teams.gsc")?;
    let case = format!("case \"{faction}\":");
    let start = teams.find(&case)? + case.len();
    let body = &teams[start..];
    let end = body.find("break;").unwrap_or(body.len());
    let want = format!("[\"{}\"]", class.to_ascii_uppercase());
    for line in body[..end].lines() {
        if let Some(i) = line.find(&want) {
            let rest = &line[i + want.len()..];
            let m = rest.find("mptype\\")? + "mptype\\".len();
            let name = rest[m..].split("::").next()?;
            return Some(name.trim().to_owned());
        }
    }
    None
}

fn quoted_after<'a>(src: &'a str, key: &str) -> Option<&'a str> {
    let i = src.find(key)? + key.len();
    let rest = &src[i..];
    let q0 = rest.find('"')? + 1;
    let q1 = rest[q0..].find('"')? + q0;
    Some(&rest[q0..q1])
}

/// Resolve a faction + class (`assault`, `smg`, `lmg`, `shotgun`, `sniper`, `riot`) to the models
/// MW2's scripts give it. `variant` picks among the class's body variants (wraps).
pub fn resolve_spec(raw: RawLookup<'_>, faction: &str, class: &str, variant: usize) -> Option<CharacterSpec> {
    let mptype = mptype_for(raw, faction, class)?;
    let script = text(raw, &format!("mptype/{mptype}.gsc"))?;
    // `case N:\n character\NAME::main();` inside main(); precache() repeats them all.
    let main_end = script.find("precache()").unwrap_or(script.len());
    let mut characters = Vec::new();
    for line in script[..main_end].lines() {
        if let Some(i) = line.find("character\\") {
            if let Some(name) = line[i + "character\\".len()..].split("::").next() {
                characters.push(name.trim().to_owned());
            }
        }
    }
    if characters.is_empty() {
        return None;
    }
    let character = characters[variant % characters.len()].clone();
    let src = text(raw, &format!("character/{character}.gsc"))?;
    let body = quoted_after(&src, "setModel(")?.to_owned();
    let viewhands = quoted_after(&src, "setViewmodel(").map(str::to_owned);
    let head_alias = quoted_after(&src, "attachHead(").map(str::to_owned);
    let mut heads = Vec::new();
    if let Some(alias) = &head_alias {
        if let Some(a) = text(raw, &format!("xmodelalias/{alias}.gsc")) {
            for line in a.lines() {
                if line.trim_start().starts_with("a[") {
                    if let Some(n) = quoted_after(line, "=") {
                        heads.push(n.to_owned());
                    }
                }
            }
        }
    }
    // Sniper (ghillie) and riot bodies attach one fixed head instead of a pool:
    // `self attach("head_allies_tf141_arctic_sniper", "", true); self.headModel = "...";`
    if heads.is_empty() {
        if let Some(h) = quoted_after(&src, "headModel =").or_else(|| quoted_after(&src, "attach(")) {
            heads.push(h.to_owned());
        }
    }
    Some(CharacterSpec {
        faction: faction.to_owned(),
        class: class.to_owned(),
        mptype,
        character,
        body,
        head_alias,
        heads,
        viewhands,
        variants: characters.len(),
    })
}

// ---------------------------------------------------------------------------------------------
// playeranim.script
// ---------------------------------------------------------------------------------------------

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AnimCommand {
    /// 1 legs, 2 torso, 3 both.
    pub body_part: u8,
    /// Lowercase XAnim name.
    pub anim: String,
    pub duration_ms: Option<i32>,
    pub blend_ms: Option<i32>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AnimCondition {
    pub index: u8,
    pub bitflags: bool,
    pub bits: u64,
    pub value: i32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AnimItem {
    /// Uses a condition MW2's own evaluator can't test (the item never matches).
    pub skip: bool,
    pub conditions: Vec<AnimCondition>,
    pub raw: String,
    pub commands: Vec<AnimCommand>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlayerAnimScript {
    /// (state, movetype, items): the MOVE table.
    pub slots: Vec<(u8, u8, Vec<AnimItem>)>,
    /// (event, items).
    pub events: Vec<(u8, Vec<AnimItem>)>,
    pub item_count: usize,
    pub event_item_count: usize,
    pub skipped_items: usize,
    pub command_count: usize,
    pub unresolved_anims: usize,
}

impl PlayerAnimScript {
    pub fn slot(&self, state: u8, movetype: u8) -> &[AnimItem] {
        self.slots.iter().find(|(s, m, _)| *s == state && *m == movetype).map_or(&[], |(_, _, i)| i.as_slice())
    }

    pub fn event(&self, event: u8) -> &[AnimItem] {
        self.events.iter().find(|(e, _)| *e == event).map_or(&[], |(_, i)| i.as_slice())
    }

    /// Every distinct anim name any command uses.
    pub fn anim_names(&self) -> HashSet<&str> {
        self.slots
            .iter()
            .flat_map(|(_, _, i)| i.iter())
            .chain(self.events.iter().flat_map(|(_, i)| i.iter()))
            .flat_map(|i| i.commands.iter())
            .map(|c| c.anim.as_str())
            .collect()
    }

    /// Blend time the script authors for an anim (last `blendtime` seen, event FIREWEAPON = 30 ms),
    /// and whether it only plays while standing still (IW4L `animation_properties`).
    pub fn properties(&self, anim: &str) -> (i32, bool) {
        let (mut blend, mut stationary) = (0, false);
        for (_, movetype, items) in &self.slots {
            for c in items.iter().flat_map(|i| &i.commands).filter(|c| c.anim == anim) {
                if c.body_part != 2 {
                    stationary |= (22..32).contains(movetype);
                }
                if let Some(ms) = c.blend_ms {
                    blend = ms;
                }
            }
        }
        for (event, items) in &self.events {
            for c in items.iter().flat_map(|i| &i.commands).filter(|c| c.anim == anim) {
                stationary |= matches!(*event, 1 | 10);
                if *event == 2 {
                    blend = 30;
                }
                if let Some(ms) = c.blend_ms {
                    blend = ms;
                }
            }
        }
        (blend, stationary)
    }
}

/// Every identifier in `animtrees/multiplayer.atr`: the anims the tree has leaves for. The
/// script's commands naming anything else are dropped (IW4L: "unresolved").
pub fn atr_names(atr: &[u8]) -> HashSet<String> {
    let mut p = Tok::new(atr);
    let mut out = HashSet::new();
    loop {
        let t = p.parse(true);
        if t.is_empty() {
            break;
        }
        if t.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'_') {
            out.insert(t.to_ascii_lowercase());
        }
    }
    out
}

fn index_ci(token: &str, table: &[&str]) -> Option<usize> {
    table.iter().position(|name| token.eq_ignore_ascii_case(name))
}

/// Port of IW4L `playeranim_parse::parse_player_anim_script`. `known` says whether the animtree
/// has a leaf of that (lowercase) name.
pub fn parse_player_anim_script(script: &[u8], known: &dyn Fn(&str) -> bool) -> Result<PlayerAnimScript, String> {
    let mut parser = Tok::new(script);
    let mut parsed = PlayerAnimScript::default();
    let mut mode = 0i32;
    let mut indent = 0u8;
    let (mut state, mut movetype, mut event) = (0u8, 0u8, 0u8);
    let mut current_items: Option<Vec<AnimItem>> = None;
    let mut current_event_items: Option<Vec<AnimItem>> = None;
    let mut aliases: HashMap<(u8, String), u64> = HashMap::new();

    fn flush_slot(parsed: &mut PlayerAnimScript, cur: &mut Option<Vec<AnimItem>>, state: u8, movetype: u8) {
        if let Some(items) = cur.take() {
            parsed.slots.push((state, movetype, items));
        }
    }
    fn flush_event(parsed: &mut PlayerAnimScript, cur: &mut Option<Vec<AnimItem>>, event: u8) {
        if let Some(items) = cur.take() {
            parsed.events.push((event, items));
        }
    }

    loop {
        let token = parser.parse(true);
        if token.is_empty() {
            break;
        }
        if let Some(m) = index_ci(&token, ANIM_PARSE_MODES) {
            flush_slot(&mut parsed, &mut current_items, state, movetype);
            flush_event(&mut parsed, &mut current_event_items, event);
            mode = m as i32;
            indent = 0;
            continue;
        }
        if mode == 0 {
            if token.eq_ignore_ascii_case("set") {
                parse_define(&mut parser, &mut aliases)?;
            }
            continue;
        }
        if mode != 1 && mode != 4 {
            continue;
        }
        if token == "{" {
            if indent >= 3 {
                return Err(parser.bad("unexpected '{'"));
            }
            indent += 1;
            continue;
        }
        if token == "}" {
            if indent == 0 {
                return Err(parser.bad("unexpected '}'"));
            }
            indent -= 1;
            if mode == 1 && indent == 1 {
                flush_slot(&mut parsed, &mut current_items, state, movetype);
            }
            if mode == 4 && indent == 0 {
                flush_event(&mut parsed, &mut current_event_items, event);
            }
            continue;
        }
        if mode == 4 {
            match indent {
                0 => {
                    flush_event(&mut parsed, &mut current_event_items, event);
                    let Some(et) = index_ci(&token, ANIM_ET_NAMES) else {
                        skip_unknown_block(&mut parser);
                        continue;
                    };
                    event = et as u8;
                    current_event_items = Some(Vec::new());
                }
                1 => {
                    parser.unget();
                    let item = parse_item(&mut parser, known, &mut parsed, &aliases)?;
                    if let Some(items) = current_event_items.as_mut() {
                        if items.len() >= 128 {
                            return Err(parser.bad("exceeded maximum items per script (128)"));
                        }
                        parsed.event_item_count += 1;
                        parsed.skipped_items += usize::from(item.skip);
                        parsed.command_count += item.commands.len();
                        items.push(item);
                    }
                }
                _ => return Err(parser.bad("unexpected token")),
            }
            continue;
        }
        match indent {
            0 => {
                if !token.eq_ignore_ascii_case("state") {
                    return Err(parser.bad("expected 'state'"));
                }
                let name = parser.parse(false);
                if name.is_empty() {
                    return Err(parser.bad("expected state type"));
                }
                state = index_ci(&name, ANIM_STATE_NAMES).unwrap_or(0) as u8;
            }
            1 => {
                flush_slot(&mut parsed, &mut current_items, state, movetype);
                let Some(mt) = index_ci(&token, ANIM_MT_NAMES) else {
                    skip_unknown_block(&mut parser);
                    continue;
                };
                movetype = mt as u8;
                current_items = Some(Vec::new());
            }
            2 => {
                parser.unget();
                let item = parse_item(&mut parser, known, &mut parsed, &aliases)?;
                if let Some(items) = current_items.as_mut() {
                    if items.len() >= 128 {
                        return Err(parser.bad("exceeded maximum items per script (128)"));
                    }
                    parsed.item_count += 1;
                    parsed.skipped_items += usize::from(item.skip);
                    parsed.command_count += item.commands.len();
                    items.push(item);
                }
            }
            _ => return Err(parser.bad("unexpected token")),
        }
    }
    flush_slot(&mut parsed, &mut current_items, state, movetype);
    flush_event(&mut parsed, &mut current_event_items, event);
    Ok(parsed)
}

fn parse_item(
    parser: &mut Tok<'_>,
    known: &dyn Fn(&str) -> bool,
    parsed: &mut PlayerAnimScript,
    aliases: &HashMap<(u8, String), u64>,
) -> Result<AnimItem, String> {
    let (skip, conditions, raw) = parse_conditions(parser, aliases)?;
    let brace = parser.parse(true);
    if brace != "{" {
        parser.unget();
        return Err(parser.bad("expected '{' after conditions"));
    }
    let mut commands = Vec::new();
    loop {
        let token = parser.parse(true);
        if token.is_empty() {
            return Err(parser.bad("unterminated command list"));
        }
        if token == "}" {
            break;
        }
        let Some(body_part) = index_ci(&token, ANIM_BODY_PART_NAMES).filter(|i| *i > 0) else {
            parser.unget();
            break;
        };
        let anim = parser.parse(false);
        if anim.is_empty() {
            return Err(parser.bad("expected animation"));
        }
        let (mut duration_ms, mut blend_ms) = (None, None);
        loop {
            let extra = parser.parse(false);
            if extra.is_empty() {
                break;
            }
            if extra.eq_ignore_ascii_case("duration") {
                duration_ms = parser.parse(false).parse().ok();
            } else if extra.eq_ignore_ascii_case("blendtime") {
                blend_ms = Some(parser.parse(false).parse().map_err(|_| parser.bad("expected blendtime value"))?);
            } else if extra.eq_ignore_ascii_case("sound") {
                let _ = parser.parse(false);
            } else if extra.eq_ignore_ascii_case("turretanim") {
                continue;
            } else {
                parser.unget();
                break;
            }
        }
        let anim = anim.to_ascii_lowercase();
        if known(&anim) {
            if commands.len() >= 12 {
                return Err(parser.bad("exceeded maximum number of animations (12)"));
            }
            commands.push(AnimCommand { body_part: body_part as u8, anim, duration_ms, blend_ms });
        } else {
            parsed.unresolved_anims += 1;
        }
    }
    Ok(AnimItem { skip, conditions, raw, commands })
}

fn parse_define(parser: &mut Tok<'_>, aliases: &mut HashMap<(u8, String), u64>) -> Result<(), String> {
    let cond = parser.parse(false);
    let Some(index) = index_ci(&cond, ANIM_COND_NAMES) else {
        return Err(parser.bad("unknown define condition"));
    };
    let alias = parser.parse(false).to_ascii_lowercase();
    let eq = parser.parse(false);
    if eq != "=" {
        parser.unget();
        return Err(parser.bad("expected '=' in define"));
    }
    let mut bits = 0u64;
    let mut subtract = false;
    loop {
        let token = parser.parse(false);
        if token.is_empty() {
            break;
        }
        if token.eq_ignore_ascii_case("set") || index_ci(&token, ANIM_PARSE_MODES).is_some() {
            parser.unget();
            break;
        }
        if token.eq_ignore_ascii_case("AND") {
            continue;
        }
        if token.eq_ignore_ascii_case("NOT") || token.eq_ignore_ascii_case("MINUS") {
            subtract = true;
            if bits == 0 {
                bits = u64::MAX;
            }
            continue;
        }
        if let Some(more) = resolve_cond_value(index as u8, &token, aliases) {
            if subtract {
                bits &= !more;
            } else {
                bits |= more;
            }
        }
    }
    aliases.insert((index as u8, alias), bits);
    Ok(())
}

/// A condition value token as a bit mask (`Bit` values are `1 << n`).
fn resolve_cond_value(index: u8, token: &str, aliases: &HashMap<(u8, String), u64>) -> Option<u64> {
    resolve_cond(index, token, aliases).map(|(bits, _)| bits)
}

/// (bits, is a single named value with its index).
fn resolve_cond(index: u8, token: &str, aliases: &HashMap<(u8, String), u64>) -> Option<(u64, Option<u8>)> {
    if ANIM_COND_IS_BITFLAGS[usize::from(index)] && token.eq_ignore_ascii_case("all") {
        return Some((u64::MAX, None));
    }
    if let Some(v) = index_ci(token, anim_cond_value_names(index)) {
        return Some((1u64 << v, Some(v as u8)));
    }
    aliases.get(&(index, token.to_ascii_lowercase())).map(|b| (*b, None))
}

fn parse_conditions(
    parser: &mut Tok<'_>,
    aliases: &HashMap<(u8, String), u64>,
) -> Result<(bool, Vec<AnimCondition>, String), String> {
    let mut skip = false;
    let mut saw = false;
    let mut conditions = Vec::new();
    let mut raw = Vec::new();
    let mut current: Option<AnimCondition> = None;
    let mut subtract = false;
    loop {
        let token = parser.parse(false);
        if token.is_empty() {
            break;
        }
        if token == "{" {
            parser.unget();
            break;
        }
        if token == "," {
            raw.push(",".to_owned());
            if let Some(c) = current.take() {
                conditions.push(c);
            }
            subtract = false;
            continue;
        }
        raw.push(token.clone());
        if token.eq_ignore_ascii_case("default") {
            saw = true;
            continue;
        }
        if let Some(cond) = current.as_mut() {
            if let Some((bits, single)) = resolve_cond(cond.index, &token, aliases) {
                if cond.bitflags {
                    if subtract {
                        cond.bits &= !bits;
                    } else {
                        cond.bits |= bits;
                    }
                } else if let Some(v) = single {
                    cond.value = i32::from(v);
                } else {
                    cond.bits = bits;
                    cond.bitflags = true;
                }
                continue;
            }
        }
        if let Some(index) = index_ci(&token, ANIM_COND_NAMES) {
            if let Some(c) = current.take() {
                conditions.push(c);
            }
            saw = true;
            if !anim_cond_evaluable(index as u8) {
                skip = true;
            }
            let value = i32::from(anim_cond_null_value_defaults_to_one(index as u8));
            current = Some(AnimCondition { index: index as u8, bitflags: ANIM_COND_IS_BITFLAGS[index], bits: 0, value });
            subtract = false;
            continue;
        }
        if token.eq_ignore_ascii_case("AND") {
            continue;
        }
        if token.eq_ignore_ascii_case("NOT") || token.eq_ignore_ascii_case("MINUS") {
            if let Some(cond) = current.as_mut().filter(|c| c.bitflags) {
                subtract = true;
                if cond.bits == 0 {
                    cond.bits = u64::MAX;
                }
            } else {
                skip = true;
            }
            continue;
        }
        if current.is_some() {
            skip = true;
            continue;
        }
        return Err(parser.bad("unknown condition token"));
    }
    if let Some(c) = current.take() {
        conditions.push(c);
    }
    if !saw {
        return Err(parser.bad("no conditions found"));
    }
    Ok((skip, conditions, raw.join(" ")))
}

fn skip_unknown_block(parser: &mut Tok<'_>) {
    let mut depth = 0i32;
    loop {
        let token = parser.parse(true);
        if token.is_empty() {
            return;
        }
        if token == "{" {
            depth += 1;
        } else if token == "}" {
            depth -= 1;
            if depth <= 0 {
                return;
            }
        }
    }
}

/// IW4L's `AtrParser` token reader (C-style comments, quoted strings, numbers, punctuation).
struct Tok<'a> {
    src: &'a [u8],
    pos: usize,
    hit_eof: bool,
    last_offset: usize,
}

impl<'a> Tok<'a> {
    fn new(src: &'a [u8]) -> Self {
        Self { src, pos: 0, hit_eof: false, last_offset: 0 }
    }

    fn unget(&mut self) {
        if self.last_offset <= self.src.len() {
            self.pos = self.last_offset;
            self.hit_eof = false;
        }
    }

    fn bad(&self, message: &str) -> String {
        format!("{message} at byte {}", self.last_offset)
    }

    fn parse(&mut self, allow_line_breaks: bool) -> String {
        if self.hit_eof {
            return String::new();
        }
        let Some((token, next, offset)) = parse_ext(self.src, self.pos, allow_line_breaks) else {
            self.hit_eof = true;
            self.last_offset = self.src.len();
            return String::new();
        };
        if token.is_empty() {
            self.last_offset = offset;
            return String::new();
        }
        self.pos = next;
        self.last_offset = offset;
        token.to_owned()
    }
}

type TokOut<'a> = Option<(&'a str, usize, usize)>;

fn parse_ext(src: &[u8], start: usize, allow_line_breaks: bool) -> TokOut<'_> {
    let (data, has_newlines) = skip_whitespace(src, start)?;
    if has_newlines && !allow_line_breaks {
        return Some(("", start, start));
    }
    let c = src[data];
    if c == b'"' {
        return parse_quoted(src, data);
    }
    if c.is_ascii_digit()
        || (c == b'.' && src.get(data + 1).is_some_and(|n| n.is_ascii_digit()))
        || (c == b'-' && src.get(data + 1).is_some_and(|n| n.is_ascii_digit()))
    {
        return parse_number(src, data);
    }
    if c.is_ascii_alphabetic() || c == b'_' || c == b'/' || c == b'\\' {
        return parse_ident(src, data);
    }
    for punct in ["+=", "-=", "*=", "/=", "&=", "|=", "++", "--", "&&", "||", "<=", ">=", "==", "!="] {
        let bytes = punct.as_bytes();
        if src[data..].starts_with(bytes) {
            return Some((punct, data + bytes.len(), data));
        }
    }
    let end = data + 1;
    Some((std::str::from_utf8(&src[data..end]).ok()?, end, data))
}

fn skip_whitespace(src: &[u8], mut i: usize) -> Option<(usize, bool)> {
    let mut has_newlines = false;
    loop {
        while i < src.len() && src[i] <= b' ' {
            if src[i] == b'\n' {
                has_newlines = true;
            }
            i += 1;
        }
        if i >= src.len() {
            return None;
        }
        if src[i] == b'/' && src.get(i + 1) == Some(&b'/') {
            i += 2;
            while i < src.len() && src[i] != b'\n' {
                i += 1;
            }
            continue;
        }
        if src[i] == b'/' && src.get(i + 1) == Some(&b'*') {
            i += 2;
            while i + 1 < src.len() && !(src[i] == b'*' && src[i + 1] == b'/') {
                i += 1;
            }
            if i + 1 < src.len() {
                i += 2;
            }
            continue;
        }
        return Some((i, has_newlines));
    }
}

fn parse_ident(src: &[u8], start: usize) -> TokOut<'_> {
    let mut i = start + 1;
    while i < src.len() && (src[i].is_ascii_alphanumeric() || src[i] == b'_') {
        i += 1;
    }
    Some((std::str::from_utf8(&src[start..i]).ok()?, i, start))
}

fn parse_number(src: &[u8], start: usize) -> TokOut<'_> {
    let mut i = start + 1;
    while i < src.len() && (src[i].is_ascii_digit() || src[i] == b'.') {
        i += 1;
    }
    if i < src.len() && (src[i] == b'e' || src[i] == b'E') {
        i += 1;
        if i < src.len() && (src[i] == b'+' || src[i] == b'-') {
            i += 1;
        }
        while i < src.len() && src[i].is_ascii_digit() {
            i += 1;
        }
    }
    Some((std::str::from_utf8(&src[start..i]).ok()?, i, start))
}

fn parse_quoted(src: &[u8], start: usize) -> TokOut<'_> {
    let mut i = start + 1;
    while i < src.len() {
        if src[i] == b'\\' && matches!(src.get(i + 1), Some(&b'"') | Some(&b'\\')) {
            i += 2;
            continue;
        }
        if src[i] == b'"' {
            return Some((std::str::from_utf8(&src[start + 1..i]).ok()?, i + 1, start));
        }
        i += 1;
    }
    Some((std::str::from_utf8(&src[start + 1..]).ok()?, src.len(), start))
}

// ---------------------------------------------------------------------------------------------
// Zone loaders
// ---------------------------------------------------------------------------------------------

/// Player-anim XAnim names: `pb_` (both), `pl_` (legs), `pt_` (torso), `pm_`, and the mantles.
pub fn is_player_anim(name: &str) -> bool {
    name.starts_with("pb_")
        || name.starts_with("pl_")
        || name.starts_with("pt_")
        || name.starts_with("pm_")
        || name.starts_with("mp_mantle_")
}

fn keep_raw(name: &str) -> bool {
    let n = norm_name(name);
    n == "mp/playeranim.script"
        || n == "animtrees/multiplayer.atr"
        || n == "maps/mp/gametypes/_teams.gsc"
        || n.starts_with("mptype/")
        || n.starts_with("character/mp_character")
        || n.starts_with("xmodelalias/")
}

/// What `common_mp.ff` gives the character code.
#[derive(Default)]
pub struct CommonCharData {
    pub xanims: HashMap<String, RawXAnimParts>,
    /// Rawfiles the model/anim scripts need, names normalised (`/`, lowercase).
    pub raw: Vec<(String, Vec<u8>)>,
    /// Weapon name (`ak47_mp`) -> world model XModel name (`weapon_ak47`).
    pub world_models: HashMap<String, String>,
    pub failed_xanims: usize,
}

impl CommonCharData {
    pub fn raw_file(&self, name: &str) -> Option<&[u8]> {
        let want = norm_name(name);
        self.raw.iter().find(|(n, _)| *n == want).map(|(_, b)| b.as_slice())
    }
}

/// Walk `common_mp.ff` for the player XAnims, the character/animation scripts and the weapons'
/// world model names.
pub fn load_common(zone: &Path) -> Result<CommonCharData, crate::LoadError> {
    let file = std::fs::read(zone).map_err(crate::LoadError::Io)?;
    let image = crate::zone_image(&file)?;
    let mut models = ModelCapture::with_filter(Some(|_| false));
    models.xanim_filter = Some(is_player_anim);
    let (links, report) = crate::walk_links(&image, models)?;
    if let Some(stopped) = report.stopped {
        return Err(crate::LoadError::Zone(format!("stopped at {}/{}: {stopped}", report.assets_walked, report.assets_total)));
    }
    let mut out = CommonCharData::default();
    out.failed_xanims = links.models.rig.failed_xanims;
    out.xanims = links
        .models
        .rig
        .xanims
        .into_iter()
        .filter(|(k, _)| is_player_anim(k))
        .map(|(k, v)| (k.to_ascii_lowercase(), v))
        .collect();
    out.raw = links.scripts.raw_files.into_iter().filter(|(n, _)| keep_raw(n)).map(|(n, b)| (norm_name(&n), b)).collect();
    for w in links.weapons {
        if let Some(m) = w.world_model {
            out.world_models.insert(w.name, m);
        }
    }
    Ok(out)
}

static WANTED: RwLock<Vec<String>> = RwLock::new(Vec::new());
static WALK_LOCK: Mutex<()> = Mutex::new(());

fn wanted(name: &str) -> bool {
    WANTED.read().is_ok_and(|w| w.iter().any(|n| n == name))
}

/// Skinned models (and static meshes, for their blend state) from one zone.
#[derive(Default)]
pub struct ZoneModels {
    pub rigs: Vec<RigModel>,
    pub meshes: Vec<Mesh>,
}

/// Capture only the named models (skeleton + skin) from a zone. Fast (~0.5 s per map zone).
pub fn load_models(zone: &Path, names: &[String]) -> Result<ZoneModels, crate::LoadError> {
    let _guard = WALK_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    *WANTED.write().map_err(|_| crate::LoadError::Zone("wanted list poisoned".into()))? = names.to_vec();
    let file = std::fs::read(zone).map_err(crate::LoadError::Io)?;
    let image = crate::zone_image(&file)?;
    let mut models = ModelCapture::with_filter(Some(wanted));
    models.rig_filter = Some(wanted);
    let (links, _) = crate::walk_links(&image, models)?;
    let m = links.models;
    Ok(ZoneModels { rigs: m.rig.models, meshes: m.meshes })
}

#[cfg(test)]
mod tests {
    use super::*;

    const SCRIPT: &str = r#"
DEFINES
set movetype moving = walk AND run
set weaponclass autofire = mg AND smg
ANIMATIONS
STATE COMBAT
{
	idle
	{
		playerAnimType pistol, weapon_position ads
		{
			both pb_stand_ads_pistol
		}
		default
		{
			both pb_stand_alert
		}
	}
	run
	{
		strafing left
		{
			both pb_combatrun_left_loop
		}
		default
		{
			both pb_combatrun_forward_loop
		}
	}
}
EVENTS
fireweapon
{
	weaponclass autofire, movetype moving
	{
	}
	weaponclass autofire
	{
		torso pt_stand_shoot_auto duration 150
	}
}
"#;

    #[test]
    fn parses_a_small_script() {
        let known = |_: &str| true;
        let s = parse_player_anim_script(SCRIPT.as_bytes(), &known).unwrap();
        assert_eq!(s.slots.len(), 2);
        let idle = s.slot(0, 1);
        assert_eq!(idle.len(), 2);
        assert_eq!(idle[0].commands[0].anim, "pb_stand_ads_pistol");
        // playerAnimType pistol -> bit 2; weapon_position ads -> value 1
        assert_eq!(idle[0].conditions[0].bits, 1 << 2);
        assert_eq!(idle[0].conditions[1].value, 1);
        let run = s.slot(0, 10);
        assert_eq!(run[0].conditions[0].value, 1); // strafing left
        let fire = s.event(2);
        assert_eq!(fire.len(), 2);
        assert!(fire[0].commands.is_empty());
        assert_eq!(fire[1].commands[0].duration_ms, Some(150));
        // weaponclass autofire = mg (2) | smg (3)
        assert_eq!(fire[1].conditions[0].bits, (1 << 2) | (1 << 3));
        // movetype moving = walk (4) | run (10)
        assert_eq!(fire[0].conditions[1].bits, (1 << 4) | (1 << 10));
    }

    #[test]
    fn mptype_lookup_from_teams_script() {
        let teams = br#"
setTeamModels( team, charSet )
{
	switch ( charSet )
	{
		case "us_army":
			game[team + "_model"]["ASSAULT"] = mptype\mptype_us_army_assault::main;
			game[team + "_model"]["SMG"] = mptype\mptype_us_army_smg::main;
			break;
		case "socom_141":
			game[team + "_model"]["ASSAULT"] = mptype\mptype_socom_assault::main;
			break;
	}
}
"#;
        let files: Vec<(String, Vec<u8>)> = vec![
            ("maps/mp/gametypes/_teams.gsc".into(), teams.to_vec()),
            ("mptype/mptype_us_army_assault.gsc".into(), b"main()\n{\n\tswitch( x )\n\t{\n\tcase 0:\n\t\tcharacter\\mp_character_us_army_assault_a::main();\n\t\tbreak;\n\tcase 1:\n\t\tcharacter\\mp_character_us_army_assault_b::main();\n\t\tbreak;\n\t}\n}\nprecache()\n{\n\tcharacter\\mp_character_us_army_assault_a::precache();\n}\n".to_vec()),
            ("character/mp_character_us_army_assault_b.gsc".into(), b"main()\n{\n\tself setModel(\"mp_body_us_army_assault_b\");\n\tcodescripts\\character::attachHead( \"alias_us_army_heads\", xmodelalias\\alias_us_army_heads::main() );\n\tself setViewmodel(\"viewhands_us_army\");\n}\n".to_vec()),
            ("xmodelalias/alias_us_army_heads.gsc".into(), b"main()\n{\n\ta[0] = \"head_us_army_a\";\n\ta[1] = \"head_us_army_b\";\n\treturn a;\n}\n".to_vec()),
        ];
        let lookup = |n: &str| files.iter().find(|(f, _)| *f == norm_name(n)).map(|(_, b)| b.as_slice());
        let spec = resolve_spec(&lookup, "us_army", "assault", 1).unwrap();
        assert_eq!(spec.mptype, "mptype_us_army_assault");
        assert_eq!(spec.body, "mp_body_us_army_assault_b");
        assert_eq!(spec.heads, vec!["head_us_army_a", "head_us_army_b"]);
        assert_eq!(spec.viewhands.as_deref(), Some("viewhands_us_army"));
        assert_eq!(spec.variants, 2);
        assert!(mptype_for(&lookup, "us_army", "riot").is_none());
    }

    /// Sniper (ghillie) and riot bodies attach one fixed head (the arctic TF141 sniper showed headless).
    #[test]
    fn fixed_head_from_attach() {
        let teams = br#"
setTeamModels( team, charSet )
{
	switch ( charSet )
	{
		case "socom_141_arctic":
			game[team + "_model"]["SNIPER"] = mptype\mptype_tf141_arctic_sniper::main;
			break;
	}
}
"#;
        let files: Vec<(String, Vec<u8>)> = vec![
            ("maps/mp/gametypes/_teams.gsc".into(), teams.to_vec()),
            ("mptype/mptype_tf141_arctic_sniper.gsc".into(), br#"main()
{
	character\mp_character_tf141_arctic_sniper::main();
}
precache()
{
}
"#.to_vec()),
            ("character/mp_character_tf141_arctic_sniper.gsc".into(), br#"main()
{
	self setModel("mp_body_tf141_arctic_sniper");
	self attach("head_allies_tf141_arctic_sniper", "", true);
	self.headModel = "head_allies_tf141_arctic_sniper";
	self setViewmodel("viewhands_sniper_tf141_arctic");
}
"#.to_vec()),
        ];
        let lookup = |n: &str| files.iter().find(|(f, _)| *f == norm_name(n)).map(|(_, b)| b.as_slice());
        let spec = resolve_spec(&lookup, "socom_141_arctic", "sniper", 0).unwrap();
        assert_eq!(spec.body, "mp_body_tf141_arctic_sniper");
        assert_eq!(spec.heads, vec!["head_allies_tf141_arctic_sniper"]);
    }
}
