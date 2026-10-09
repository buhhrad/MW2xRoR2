//! MW2's bullet penetration depths: `info/bullet_penetration_mp` (a rawfile in the zones),
//! "BULLET_PEN_TABLE\small_bark\<depth>\..." - per penetration type (small / medium / large) and
//! IW4 surface type, in units. The host walks bullets through walls with it, as IW4's
//! FireBulletPenetrate does (weapon_iw4::penetration; sim::bullet_collision in IW4L).

use std::sync::RwLock;
use weapon_iw4::{PenetrationDepthTable, split_pen_key};

static TABLE: RwLock<Option<PenetrationDepthTable>> = RwLock::new(None);

/// Keep the zone's penetration table (an earlier zone's copy wins).
pub fn add_from(s: &mw2data::scripts::ScriptCapture) {
    let Ok(mut t) = TABLE.write() else { return };
    if t.is_some() {
        return;
    }
    let Some(bytes) = s.find_raw("info/bullet_penetration_mp") else { return };
    if let Some(table) = parse(&String::from_utf8_lossy(bytes)) {
        *t = Some(table);
    }
}

fn parse(text: &str) -> Option<PenetrationDepthTable> {
    let rest = text.trim().strip_prefix("BULLET_PEN_TABLE")?;
    let mut table = PenetrationDepthTable::empty();
    let mut parts = rest.split(char::from(92u8)).filter(|p| !p.is_empty());
    while let Some(key) = parts.next() {
        let value: f32 = parts.next()?.trim().parse().ok()?;
        if let Some((ptype, surf)) = split_pen_key(key.trim()) {
            table.set_depth(ptype, surf, value);
        }
    }
    Some(table)
}

/// Max depth in units a bullet of `penetrate_type` goes through `surface_type` (0: none / no table).
#[unsafe(no_mangle)]
pub extern "C" fn mw2_pen_depth(penetrate_type: i32, surface_type: u32) -> f32 {
    std::panic::catch_unwind(|| TABLE.read().ok().and_then(|t| t.map(|t| t.depth(penetrate_type, surface_type))).unwrap_or(0.0)).unwrap_or(0.0)
}

/// A weapon's penetration type (0 none, 1 small, 2 medium, 3 large) and multiplier (FMJ raises it).
///
/// # Safety
/// `ptype` / `mult` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_weapon_penetration(weapon: u32, ptype: *mut i32, mult: *mut f32) -> i32 {
    if ptype.is_null() || mult.is_null() {
        return 0;
    }
    std::panic::catch_unwind(|| match crate::weapons::row(weapon & 0xFFFF) {
        Some(r) => {
            unsafe {
                *ptype = r.penetrate_type;
                *mult = r.penetrate_multiplier;
            }
            1
        }
        None => 0,
    })
    .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    #[test]
    fn parses_pen_table() {
        let t = super::parse("BULLET_PEN_TABLE\\small_wood\\8\\large_metal\\20").unwrap();
        assert_eq!(t.depth(1, 21), 8.0);
        assert_eq!(t.depth(3, 13), 20.0);
        assert_eq!(t.depth(2, 21), 0.0);
    }
}
