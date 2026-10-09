//! MW2's fonts (`fonts/hudBigFont` ...) from the user's zones: glyph metrics plus the glyph
//! sheet material (decoded like any HUD image).

use std::sync::RwLock;

pub static FONTS: RwLock<Vec<mw2data::scripts::Font>> = RwLock::new(Vec::new());

pub fn add(fonts: Vec<mw2data::scripts::Font>) {
    if let Ok(mut all) = FONTS.write() {
        for f in fonts {
            if !all.iter().any(|g| g.name.eq_ignore_ascii_case(&f.name)) {
                all.push(f);
            }
        }
    }
}

pub fn with<T>(name: &str, f: impl FnOnce(&mw2data::scripts::Font) -> T) -> Option<T> {
    let all = FONTS.read().ok()?;
    all.iter().find(|g| g.name.eq_ignore_ascii_case(name)).map(f)
}
