//! MW2 XP and ranks, from the user's zones: `mp/ranktable.csv` (code_post_gfx_mp.ff) and
//! the score values in `maps/mp/gametypes/_rank.gsc` (patch_mp.ff, else common_mp.ff).
//! Team-based values apply (players vs monsters is a team game).

use std::sync::RwLock;

#[derive(Debug, Clone)]
pub struct Rank {
    pub id: u32,
    pub min_xp: u32,
    pub xp_to_next: u32,
    pub icon: String,
    /// The number MW2 shows (column 13).
    pub display: u32,
}

#[derive(Debug, Default)]
pub struct Progression {
    pub ranks: Vec<Rank>,
    /// registerScoreInfo values (team-based branch), e.g. ("kill", 100).
    pub scores: Vec<(String, u32)>,
}

pub static PROGRESSION: RwLock<Option<Progression>> = RwLock::new(None);

pub fn load(tables: &mw2data::scripts::ScriptCapture, rank_gsc: Option<&[u8]>) -> usize {
    let mut ranks = Vec::new();
    if let Some(t) = tables.find_table("mp/ranktable.csv") {
        for r in 0..t.rows {
            let (Ok(id), Ok(min_xp), Ok(xp_to_next)) = (t.cell(r, 0).parse(), t.cell(r, 2).parse(), t.cell(r, 3).parse()) else { continue };
            ranks.push(Rank { id, min_xp, xp_to_next, icon: t.cell(r, 6).to_owned(), display: t.cell(r, 13).parse().unwrap_or(id + 1) });
        }
    }
    let scores = rank_gsc.map(|g| team_scores(&String::from_utf8_lossy(g))).unwrap_or_default();
    let n = ranks.len();
    if let Ok(mut p) = PROGRESSION.write() {
        *p = Some(Progression { ranks, scores });
    }
    n
}

/// The `if ( level.teamBased )` block's registerScoreInfo values come first in _rank.gsc;
/// take the first value seen for each name.
fn team_scores(gsc: &str) -> Vec<(String, u32)> {
    let mut out: Vec<(String, u32)> = Vec::new();
    for line in gsc.lines() {
        let line = line.trim();
        if !line.starts_with("registerScoreInfo(") {
            continue;
        }
        let quoted: Vec<&str> = line.split('"').collect();
        if quoted.len() < 3 {
            continue;
        }
        let name = quoted[1];
        let value: String = quoted[2].chars().filter(|c| c.is_ascii_digit() || *c == '.').collect();
        let Ok(v) = value.parse::<f32>() else { continue };
        if v.fract() != 0.0 || out.iter().any(|(n, _)| n == name) {
            continue;
        }
        out.push((name.to_owned(), v as u32));
    }
    out
}

pub fn score(name: &str) -> u32 {
    PROGRESSION.read().ok().and_then(|p| p.as_ref().and_then(|p| p.scores.iter().find(|(n, _)| n == name).map(|(_, v)| *v))).unwrap_or(0)
}

pub fn rank_for(xp: u32) -> Option<Rank> {
    let p = PROGRESSION.read().ok()?;
    let ranks = &p.as_ref()?.ranks;
    ranks.iter().rev().find(|r| xp >= r.min_xp).or(ranks.first()).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn team_branch_wins() {
        let gsc = "if ( level.teamBased )\n{\n\t\tregisterScoreInfo( \"kill\", 100 );\n\t\tregisterScoreInfo( \"assist\", 20 );\n}\nelse\n{\n\t\tregisterScoreInfo( \"kill\", 50 );\n}\n\tregisterScoreInfo( \"loss\", 0.5 );";
        assert_eq!(team_scores(gsc), vec![("kill".to_string(), 100), ("assist".to_string(), 20)]);
    }
}
