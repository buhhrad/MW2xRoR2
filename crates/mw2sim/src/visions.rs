//! MW2's vision sets (`vision/*.vision` rawfiles in patch_mp / common_mp): the film / glow dvars
//! a `visionSetNaked( "mpnuke", 3 )` switches to. Kept as text; the host maps them onto its
//! post-processing.

use std::collections::HashMap;
use std::sync::RwLock;

static VISIONS: RwLock<Option<HashMap<String, String>>> = RwLock::new(None);

/// Keep the zone's vision files (an earlier zone's copy wins: patch_mp over common_mp).
pub fn add_from(s: &mw2data::scripts::ScriptCapture) {
    let Ok(mut v) = VISIONS.write() else { return };
    let map = v.get_or_insert_with(HashMap::new);
    for (name, bytes) in &s.raw_files {
        let lower = name.replace('\\', "/").to_ascii_lowercase();
        if let Some(rest) = lower.strip_prefix("vision/") {
            let key = rest.trim_end_matches(".vision").to_owned();
            map.entry(key).or_insert_with(|| String::from_utf8_lossy(bytes).into_owned());
        }
    }
}

/// The text of `vision/<name>.vision`.
pub fn get(name: &str) -> Option<String> {
    VISIONS.read().ok()?.as_ref()?.get(&name.to_ascii_lowercase()).cloned()
}
