//! MW2 killstreak rules, read from the user's own zones: `mp/killstreakTable.csv`
//! (code_post_gfx_mp.ff) for kill counts, sounds, dialog and icons, and the Care Package
//! weights from `maps/mp/killstreaks/_airdrop.gsc` (patch_mp.ff, else common_mp.ff).
//! The earn/stack logic ports `_killstreaks.gsc`: every kill this life counts (streak kills
//! included), each loadout streak is earned once per life in kill order
//! (`checkKillstreakReward` + `lastEarnedStreak`), earned streaks stack newest-first and
//! using one takes the newest (`giveKillstreak` / `shuffleKillStreaksFILO`). What a streak
//! does is the host's job.

use std::sync::RwLock;

#[derive(Debug, Clone)]
pub struct StreakDef {
    /// Table id (column 0); the host refers to streaks by this.
    pub id: u32,
    pub name: String,
    pub kills: u32,
    /// Splash sound alias (column 7), e.g. `mp_killstreak_radar`.
    pub earn_sound: String,
    /// Dialog keys (columns 8 and 9); the alias is `<faction>_1mc_<key>` / `<faction>_1mc_use_<key>`.
    pub earn_dialog: String,
    pub use_dialog: String,
    pub weapon: String,
    pub icon: String,
    /// Icon over a Care Package holding this streak (column 15).
    pub crate_icon: String,
    /// The HUD's current-streak icon (column 16, an animated sheet).
    pub dpad: String,
    /// XP for using it (column 13; `registerScoreInfo( "killstreak_" + ref, ... )`).
    pub xp: u32,
}

#[derive(Debug, Default)]
pub struct Table {
    pub streaks: Vec<StreakDef>,
    /// Care Package (`airdrop`) contents: (streak name or "ammo", weight).
    pub airdrop: Vec<(String, u32)>,
    /// Emergency Airdrop (`airdrop_mega`) contents.
    pub airdrop_mega: Vec<(String, u32)>,
}

pub static TABLE: RwLock<Option<Table>> = RwLock::new(None);

pub fn load(zone_dir: &std::path::Path) -> Result<usize, String> {
    crate::hud::note_zone_dir(zone_dir);
    let (mut scripts, _) = mw2data::scripts_from_file(&zone_dir.join("code_post_gfx_mp.ff")).map_err(|e| e.to_string())?;
    crate::images::add_from(&mut scripts);
    crate::fonts::add(std::mem::take(&mut scripts.fonts));
    crate::hud::ingest(&mut scripts);
    crate::menus::add_tables(&scripts);
    let csv = scripts.find_table("mp/killstreakTable.csv").ok_or("mp/killstreakTable.csv not found")?;
    let mut streaks = Vec::new();
    for r in 0..csv.rows {
        let Ok(id) = csv.cell(r, 0).parse::<u32>() else { continue };
        let Ok(kills) = csv.cell(r, 4).parse::<u32>() else { continue };
        if id == 0 {
            continue;
        }
        streaks.push(StreakDef {
            id,
            name: csv.cell(r, 1).to_owned(),
            kills,
            earn_sound: csv.cell(r, 7).to_owned(),
            earn_dialog: csv.cell(r, 8).to_owned(),
            use_dialog: csv.cell(r, 9).to_owned(),
            weapon: csv.cell(r, 12).to_owned(),
            icon: csv.cell(r, 14).to_owned(),
            crate_icon: csv.cell(r, 15).to_owned(),
            dpad: csv.cell(r, 16).to_owned(),
            xp: csv.cell(r, 13).parse().unwrap_or(0),
        });
    }
    // patch_mp.ff carries the patched scripts; common_mp.ff the rest.
    let mut airdrop = Vec::new();
    let mut airdrop_mega = Vec::new();
    let mut rank_gsc: Option<Vec<u8>> = None;
    for zone in ["patch_mp.ff", "common_mp.ff"] {
        if let Ok((mut s, _)) = mw2data::scripts_from_file(&zone_dir.join(zone)) {
            crate::images::add_from(&mut s);
            crate::fonts::add(std::mem::take(&mut s.fonts));
            crate::hud::ingest(&mut s);
            crate::menus::add_menus(&s);
            crate::visions::add_from(&s);
            crate::penetration::add_from(&s);
            if airdrop.is_empty() {
                if let Some(gsc) = s.find_raw("maps/mp/killstreaks/_airdrop.gsc") {
                    airdrop = crate_weights(&String::from_utf8_lossy(gsc), "airdrop");
                    airdrop_mega = crate_weights(&String::from_utf8_lossy(gsc), "airdrop_mega");
                }
            }
            if rank_gsc.is_none() {
                rank_gsc = s.find_raw("maps/mp/gametypes/_rank.gsc").map(<[u8]>::to_vec);
            }
        }
    }
    crate::progression::load(&scripts, rank_gsc.as_deref());
    // MW2's fonts (hudBigFont, objectiveFont, ...) and their glyph sheet live in the localized zone.
    for zone in ["localized_code_post_gfx_mp.ff", "localized_common_mp.ff"] {
        if let Ok((mut s, _)) = mw2data::scripts_from_file(&zone_dir.join(zone)) {
            crate::images::add_from(&mut s);
            crate::fonts::add(std::mem::take(&mut s.fonts));
            crate::hud::ingest(&mut s);
        }
    }
    let n = streaks.len();
    *TABLE.write().map_err(|_| "killstreak table poisoned")? = Some(Table { streaks, airdrop, airdrop_mega });
    Ok(n)
}

/// `addCrateType( "airdrop", "uav", getDvarInt( "scr_airdrop_uav", 17 ), ... )` -> ("uav", 17).
fn crate_weights(gsc: &str, drop_type: &str) -> Vec<(String, u32)> {
    let mut out = Vec::new();
    for line in gsc.lines() {
        let line = line.trim();
        if !line.starts_with("addCrateType") {
            continue;
        }
        let quoted: Vec<&str> = line.split('"').skip(1).step_by(2).collect();
        if quoted.first() != Some(&drop_type) || quoted.len() < 2 {
            continue;
        }
        // The weight is the last integer before the crate function.
        let before_func = line.rsplit_once("::").map_or(line, |(a, _)| a);
        let weight = before_func
            .split(|c: char| !c.is_ascii_digit())
            .filter(|t| !t.is_empty())
            .last()
            .and_then(|t| t.parse().ok())
            .unwrap_or(0);
        out.push((quoted[1].to_owned(), weight));
    }
    out
}

pub fn with_table<T>(f: impl FnOnce(&Table) -> T) -> Option<T> {
    TABLE.read().ok()?.as_ref().map(f)
}

pub fn id_of(name: &str) -> u32 {
    with_table(|t| t.streaks.iter().find(|s| s.name.eq_ignore_ascii_case(name)).map_or(0, |s| s.id)).unwrap_or(0)
}

pub fn def(id: u32) -> Option<StreakDef> {
    with_table(|t| t.streaks.iter().find(|s| s.id == id).cloned()).flatten()
}

/// One player's streak state.
#[derive(Debug, Default)]
pub struct Streaks {
    /// Hardline (_class.gsc: each streak's kill requirement - 1).
    pub hardline: bool,
    /// Loadout streak ids, ascending by kill count.
    loadout: Vec<u32>,
    /// Kills this life (`cur_kill_streak`).
    pub count: u32,
    /// `lastEarnedStreak`: the loadout streak earned last this life.
    last_earned: Option<u32>,
    /// Earned and unused, newest first (`pers["killstreaks"]`).
    pub stack: Vec<u32>,
    /// Laps of the loadout this life (playtest 10-03-26: RoR2's kills keep climbing, so past the top
    /// streak the loadout starts again from the bottom, each lap needing more kills than the last).
    pub lap: u32,
    /// This life's kill count when the current lap began.
    lap_start: u32,
}

impl Streaks {
    pub fn set_loadout(&mut self, ids: &[u32]) {
        let mut l: Vec<(u32, u32)> = ids.iter().filter_map(|&id| def(id).map(|d| (d.kills, id))).collect();
        l.sort();
        l.dedup_by_key(|x| x.1);
        self.loadout = l.into_iter().map(|(_, id)| id).collect();
        // A loadout changed mid-life (a killstreak pickup) counts the streaks already passed as
        // earned; only those above this life's count are still to come.
        let count = self.count;
        self.last_earned = self.loadout.iter().copied().filter(|&id| self.kills_for(id) <= count).last();
    }

    /// The kill count this life at which `id` comes this lap (scaled, Hardline taken off, each
    /// lap's gaps grown by the lap growth from where the lap began).
    pub fn kills_for(&self, id: u32) -> u32 {
        let less = u32::from(self.hardline);
        def(id).map_or(u32::MAX, |d| {
            let base = scaled_kills(d.kills.saturating_sub(less).max(1)) as f32;
            self.lap_start + (base * lap_growth().powi(self.lap as i32)).round().max(1.0) as u32
        })
    }

    pub fn loadout(&self) -> &[u32] {
        &self.loadout
    }

    /// A kill this life. Returns the streaks it earned, in order.
    pub fn kill(&mut self) -> Vec<u32> {
        self.count += 1;
        let mut earned = Vec::new();
        for &id in &self.loadout.clone() {
            if self.kills_for(id) > self.count {
                break;
            }
            if self.last_earned.is_some_and(|last| self.kills_for(id) <= self.kills_for(last)) {
                continue;
            }
            self.give(id);
            self.last_earned = Some(id);
            earned.push(id);
        }
        // The top streak of the lap is in: the next lap starts from the bottom.
        if self.last_earned.is_some() && self.last_earned == self.loadout.last().copied() {
            self.lap += 1;
            self.lap_start = self.count;
            self.last_earned = None;
        }
        earned
    }

    /// Put a streak on top of the stack (earned, or from a Care Package).
    pub fn give(&mut self, id: u32) {
        self.stack.insert(0, id);
    }

    /// Use the newest streak.
    pub fn take(&mut self) -> Option<u32> {
        (!self.stack.is_empty()).then(|| self.stack.remove(0))
    }

    /// A new life: the count and earn progress reset, unused streaks stay (MW2 keeps them in `pers`).
    pub fn new_life(&mut self) {
        self.count = 0;
        self.last_earned = None;
        self.lap = 0;
        self.lap_start = 0;
    }
}

/// RoR2 throws far more enemies at you than an MW2 lobby (playtest 10-03-26): every streak's kill
/// count is multiplied by this (f32 bits; default 1 until the host sets it).
static KILL_SCALE: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0x3f80_0000);

pub fn set_kill_scale(scale: f32) {
    let s = if scale.is_finite() && scale > 0.0 { scale } else { 1.0 };
    KILL_SCALE.store(s.to_bits(), std::sync::atomic::Ordering::Relaxed);
}

/// How much each lap of the loadout grows its kill gaps (f32 bits; 1.5 by default).
static LAP_GROWTH: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0x3fc0_0000);

pub fn set_lap_growth(g: f32) {
    let g = if g.is_finite() && g >= 1.0 { g } else { 1.0 };
    LAP_GROWTH.store(g.to_bits(), std::sync::atomic::Ordering::Relaxed);
}

fn lap_growth() -> f32 {
    f32::from_bits(LAP_GROWTH.load(std::sync::atomic::Ordering::Relaxed))
}

/// MW2's kill count for a streak, scaled (at least 1).
pub fn scaled_kills(kills: u32) -> u32 {
    let s = f32::from_bits(KILL_SCALE.load(std::sync::atomic::Ordering::Relaxed));
    ((kills as f32 * s).round() as u32).max(1)
}

/// Roll a Care Package by MW2's weights. Returns the streak name ("ammo" included).
pub fn roll_airdrop(roll: u32) -> Option<String> {
    roll_drop(false, roll)
}

/// Roll a crate: `mega` = Emergency Airdrop weights, otherwise Care Package weights.
pub fn roll_drop(mega: bool, roll: u32) -> Option<String> {
    with_table(|t| {
        let weights = if mega { &t.airdrop_mega } else { &t.airdrop };
        let total: u32 = weights.iter().map(|(_, w)| w).sum();
        if total == 0 {
            return None;
        }
        let mut v = roll % total;
        for (name, w) in weights {
            if v < *w {
                return Some(name.clone());
            }
            v -= w;
        }
        None
    })
    .flatten()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crate_lines_parse() {
        let gsc = "\taddCrateType( \"airdrop\",\t\t\"uav\", \t\tgetDvarInt( \"scr_airdrop_uav\", 17 ),\t\t::killstreakCrateThink );\n\taddCrateType( \"airdrop_mega\", \"uav\", getDvarInt( \"scr_airdrop_mega_uav\", 12 ), ::killstreakCrateThink );\n\taddCrateType( \"nuke_drop\", \"nuke\", 100, ::nukeCrateThink );";
        assert_eq!(crate_weights(gsc, "airdrop"), vec![("uav".to_string(), 17)]);
        assert_eq!(crate_weights(gsc, "nuke_drop"), vec![("nuke".to_string(), 100)]);
    }

    #[test]
    fn mw2_table_and_rules() {
        let dir = std::path::Path::new(r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english");
        if !dir.exists() {
            return;
        }
        let n = load(dir).expect("load killstreak table");
        assert!(n >= 16, "{n} streaks");
        let (uav, care, pred) = (id_of("uav"), id_of("airdrop"), id_of("predator_missile"));
        assert_eq!([def(uav).unwrap().kills, def(care).unwrap().kills, def(pred).unwrap().kills], [3, 4, 5]);
        let w = with_table(|t| t.airdrop.clone()).unwrap();
        eprintln!("care package weights: {w:?}");
        assert!(w.iter().any(|(n, w)| n == "uav" && *w > 0));
        let mega = with_table(|t| t.airdrop_mega.clone()).unwrap();
        assert_eq!(mega.iter().map(|(_, w)| w).sum::<u32>(), 100, "airdrop_mega weights {mega:?}");
        let mut s = Streaks::default();
        s.set_loadout(&[pred, uav, care]);
        let earned: Vec<Vec<u32>> = (0..6).map(|_| s.kill()).collect();
        assert_eq!(earned, vec![vec![], vec![], vec![uav], vec![care], vec![pred], vec![]]);
        assert_eq!(s.stack, vec![pred, care, uav], "newest first");
        assert_eq!(s.take(), Some(pred));
        s.new_life();
        assert_eq!((s.count, s.stack.len()), (0, 2), "unused streaks survive a new life");
        assert_eq!((0..3).flat_map(|_| s.kill()).collect::<Vec<_>>(), vec![uav]);
    }
}

#[cfg(test)]
mod laps {
    use super::*;

    /// Past the top streak the loadout laps from the bottom, gaps grown 1.5x from where the lap began.
    #[test]
    fn loadout_laps_with_growing_gaps() {
        let mut s = Streaks::default();
        // Without the zone tables kills_for can't see defs; check the arithmetic on the lap fields.
        s.lap = 1;
        s.lap_start = 28;
        set_kill_scale(4.0);
        set_lap_growth(1.5);
        assert_eq!(scaled_kills(3), 12);
        let gap = (scaled_kills(3) as f32 * lap_growth().powi(s.lap as i32)).round() as u32;
        assert_eq!(s.lap_start + gap, 46);
        set_kill_scale(1.0);
    }
}
