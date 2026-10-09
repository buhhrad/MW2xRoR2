//! Any MW2 sound alias by name: announcer lines, hit alerts, killstreak effects.
//!
//! Alias rows and embedded PCM come from the zone walks done at load time; streamed
//! files are read from the `main/*.iwd` archives, indexed on a background thread
//! (indexing every archive takes a few seconds). Decoded clips are cached.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock, RwLock};

use mw2data::sounds::{Alias, SoundCapture};

use crate::audio::{self, Pcm};

pub static BANKS: RwLock<Vec<SoundCapture>> = RwLock::new(Vec::new());
static IWD: OnceLock<Option<mw2data::iwd::SoundIndex>> = OnceLock::new();
static IWD_DIR: OnceLock<PathBuf> = OnceLock::new();
static CACHE: Mutex<Option<HashMap<String, Option<Pcm>>>> = Mutex::new(None);
/// Alias rows by lowercase list name: (bank, row) indices, rebuilt when the banks grow.
static INDEX: Mutex<Option<(usize, HashMap<String, Vec<(usize, usize)>>)>> = Mutex::new(None);
/// Clips being decoded on a worker thread.
static PENDING: Mutex<Option<std::collections::HashSet<String>>> = Mutex::new(None);

/// Remember where `main/` is and start indexing its archives in the background.
pub fn start_iwd_index(main_dir: PathBuf) {
    if IWD_DIR.set(main_dir).is_ok() {
        let _ = std::thread::Builder::new().name("mw2-iwd-index".into()).spawn(|| {
            let _ = iwd();
        });
    }
}

fn iwd() -> Option<&'static mw2data::iwd::SoundIndex> {
    IWD.get_or_init(|| IWD_DIR.get().and_then(|d| mw2data::iwd::SoundIndex::open(d).ok())).as_ref()
}

pub fn iwd_ready() -> bool {
    IWD.get().is_some_and(Option::is_some)
}

/// Rows of an alias list (IW4 looks aliases up case-insensitively: scripts play `US_spawn_music`,
/// the zone defines `us_spawn_music`).
fn variants(banks: &[SoundCapture], list: &str) -> Vec<Alias> {
    let total: usize = banks.iter().map(|b| b.aliases.len()).sum();
    let mut index = INDEX.lock().unwrap_or_else(|e| e.into_inner());
    if index.as_ref().is_none_or(|(n, _)| *n != total) {
        let mut map: HashMap<String, Vec<(usize, usize)>> = HashMap::new();
        for (bi, b) in banks.iter().enumerate() {
            for (ai, a) in b.aliases.iter().enumerate() {
                map.entry(a.list.to_ascii_lowercase()).or_default().push((bi, ai));
            }
        }
        *index = Some((total, map));
    }
    let Some((_, map)) = index.as_ref() else { return Vec::new() };
    let mut want = list.to_owned();
    for _ in 0..3 {
        let rows: Vec<&Alias> = map.get(&want.to_ascii_lowercase()).map(|v| v.iter().map(|&(bi, ai)| &banks[bi].aliases[ai]).collect()).unwrap_or_default();
        let playable: Vec<Alias> = rows.iter().filter(|a| a.loaded.is_some() || a.streamed.is_some()).map(|a| (*a).clone()).collect();
        if !playable.is_empty() {
            return playable;
        }
        match rows.iter().find_map(|a| a.secondary.clone()) {
            Some(next) => want = next,
            None => return Vec::new(),
        }
    }
    Vec::new()
}

fn decode(banks: &[SoundCapture], row: &Alias) -> Option<Pcm> {
    if let Some(name) = &row.loaded {
        return banks.iter().find_map(|b| b.loaded.get(name)).and_then(Pcm::from_loaded);
    }
    let (dir, name) = row.streamed.as_ref()?;
    // Streamed files only become playable once the background index is done.
    let bytes = IWD.get()?.as_ref()?.read(dir, name)?;
    if name.to_ascii_lowercase().ends_with(".mp3") {
        return decode_mp3(format!("{dir}/{name}"), bytes);
    }
    let wav = mw2data::iwd::parse_wav(&bytes)?;
    if wav.format != 1 {
        return None;
    }
    Pcm::from_loaded(&mw2data::sounds::LoadedPcm {
        name: format!("{dir}/{name}"),
        format: mw2data::sounds::MSS_PCM,
        rate: wav.rate,
        bits: i32::from(wav.bits),
        channels: i32::from(wav.channels),
        bytes: wav.data,
    })
}

/// MW2's streamed music (`music/hz_mp_spawn_05.mp3` for the spawn music) to 16-bit PCM.
fn decode_mp3(name: String, bytes: Vec<u8>) -> Option<Pcm> {
    use rodio::Source;
    let dec = rodio::Decoder::new(std::io::Cursor::new(bytes)).ok()?;
    let (rate, channels) = (dec.sample_rate(), dec.channels());
    let mut pcm = Vec::new();
    for s in dec {
        pcm.extend_from_slice(&s.to_le_bytes());
    }
    Pcm::from_loaded(&mw2data::sounds::LoadedPcm { name, format: mw2data::sounds::MSS_PCM, rate: rate as _, bits: 16, channels: i32::from(channels), bytes: pcm })
}

/// Does this alias exist at all (in any loaded zone)?
pub fn exists(list: &str) -> bool {
    BANKS.read().map(|b| !variants(&b, list).is_empty()).unwrap_or(false)
}

/// Play a random variant of `list` with its MW2 volume/pitch ranges.
/// 1 = queued, 0 = unknown alias or no device, 2 = streamed file not indexed yet.
pub fn play(list: &str, volume: f32) -> i32 {
    let Ok(banks) = BANKS.read() else { return 0 };
    let rows = variants(&banks, list);
    if rows.is_empty() {
        return 0;
    }
    let pick = ((audio::rand_range(0.0, 1.0) * rows.len() as f32) as usize).min(rows.len() - 1);
    let row = &rows[pick];
    let key = row.loaded.clone().unwrap_or_else(|| row.streamed.as_ref().map(|(d, n)| format!("{d}/{n}")).unwrap_or_default());
    let vol = volume * audio::rand_range(row.vol.0, row.vol.1.max(row.vol.0));
    let pitch = audio::rand_range(row.pitch.0, row.pitch.1.max(row.pitch.0));
    let pitch = if pitch > 0.0 { pitch } else { 1.0 };
    let cached = CACHE.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_with(HashMap::new).get(&key).cloned();
    match cached {
        Some(Some(pcm)) => i32::from(audio::play(pcm, vol, pitch)),
        Some(None) => 0,
        None => {
            if row.loaded.is_none() && !iwd_ready() {
                return 2;
            }
            // A first play decodes off the caller's thread (a streamed line reopens its .iwd and
            // read the whole zip directory: ~90 ms on the game thread per new announcer line), then
            // plays. A repeat while that runs is dropped, like a voice that's already talking.
            if !PENDING.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_with(Default::default).insert(key.clone()) {
                return 1;
            }
            let row = row.clone();
            let spawned = std::thread::Builder::new().name("mw2-sound-decode".into()).spawn(move || {
                let p = BANKS.read().ok().and_then(|b| decode(&b, &row));
                CACHE.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_with(HashMap::new).insert(key.clone(), p.clone());
                PENDING.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_with(Default::default).remove(&key);
                if let Some(pcm) = p {
                    audio::play(pcm, vol, pitch);
                }
            });
            i32::from(spawned.is_ok())
        }
    }
}

/// Start `list` looping (playLoopSound) at `volume`; returns its loop id (0: unknown alias or no
/// streamed index yet). A clip not decoded yet decodes off the caller's thread and starts after.
pub fn loop_start(list: &str, volume: f32) -> u32 {
    let Ok(banks) = BANKS.read() else { return 0 };
    let rows = variants(&banks, list);
    let Some(row) = rows.first().cloned() else { return 0 };
    if row.loaded.is_none() && !iwd_ready() {
        return 0;
    }
    let key = row.loaded.clone().unwrap_or_else(|| row.streamed.as_ref().map(|(d, n)| format!("{d}/{n}")).unwrap_or_default());
    let id = audio::new_loop_id();
    let cached = CACHE.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_with(HashMap::new).get(&key).cloned();
    match cached {
        Some(Some(pcm)) => {
            audio::loop_start(id, pcm, volume);
        }
        Some(None) => return 0,
        None => {
            drop(banks);
            let _ = std::thread::Builder::new().name("mw2-loop-decode".into()).spawn(move || {
                let p = BANKS.read().ok().and_then(|b| decode(&b, &row));
                CACHE.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_with(HashMap::new).insert(key, p.clone());
                if let Some(pcm) = p {
                    audio::loop_start(id, pcm, volume);
                }
            });
        }
    }
    id
}

#[cfg(test)]
mod melee_alias_scan {
    /// MW2's streamed spawn music (an .mp3 in the iwds) decodes, found by IW4's case-insensitive name.
    #[test]
    #[ignore]
    fn spawn_music_mp3_decodes() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let t = std::time::Instant::now();
        while !super::iwd_ready() && t.elapsed().as_secs() < 120 {
            std::thread::sleep(std::time::Duration::from_millis(200));
        }
        let banks = super::BANKS.read().unwrap();
        let rows = super::variants(&banks, &std::env::var("ALIAS").unwrap_or_else(|_| "US_spawn_music".into()));
        assert!(!rows.is_empty(), "alias found case-insensitively");
        let t = std::time::Instant::now();
        let pcm = super::decode(&banks, &rows[0]);
        eprintln!("decoded {:?} in {:?}: {}", rows[0].streamed, t.elapsed(), pcm.as_ref().map(|p| p.describe()).unwrap_or_default());
        assert!(pcm.is_some());
    }

    /// Lists the melee / knife alias lists in the user's MW2 sound banks (names only).
    #[test]
    #[ignore]
    fn melee_aliases() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        if !std::path::Path::new(COMMON_MP).exists() {
            return;
        }
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let banks = super::BANKS.read().unwrap();
        let mut lists: Vec<&str> = banks.iter().flat_map(|b| b.aliases.iter()).map(|a| a.list.as_str()).filter(|l| { let f = std::env::var("ALIAS_FILTER").unwrap_or_else(|_| "melee|knife".into()); f.split('|').any(|k| l.contains(k)) }).collect();
        lists.sort();
        lists.dedup();
        for l in lists {
            eprintln!("{l} playable={}", super::exists(l));
        }
        if let Ok(one) = std::env::var("ALIAS_SHOW") {
            for a in banks.iter().flat_map(|b| b.aliases.iter()).filter(|a| a.list == one) {
                eprintln!("ROW {a:?}");
            }
        }
    }
}

#[cfg(test)]
mod vehicle_loops {
    /// MW2's music / menu sound aliases in the loaded banks.
    #[test]
    #[ignore]
    fn list_music_aliases() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let banks = super::BANKS.read().unwrap();
        let mut seen = std::collections::BTreeSet::new();
        for b in banks.iter() {
            for a in &b.aliases {
                let l = a.list.to_ascii_lowercase();
                if l.contains("music") || l.contains("menu") {
                    let src = a.loaded.clone().unwrap_or_else(|| a.streamed.as_ref().map(|(d, n)| format!("{d}/{n}")).unwrap_or_default());
                    seen.insert(format!("{l} -> {src}"));
                }
            }
        }
        for s in seen { eprintln!("ALIAS {s}"); }
    }

    /// Every vehicle loop alias decodes (a failed decode on the loop thread was silent).
    #[test]
    #[ignore]
    fn vehicle_loops_decode() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let t = std::time::Instant::now();
        while !super::iwd_ready() && t.elapsed().as_secs() < 120 {
            std::thread::sleep(std::time::Duration::from_millis(200));
        }
        let banks = super::BANKS.read().unwrap();
        for a in ["pavelow_engine_high", "littlebird_engine_high", "mp_hind_helicopter", "mp_cobra_helicopter", "harrier_engine_high", "harrier_idle_high",
                  "veh_mig29_dist_loop", "veh_b2_dist_loop", "veh_ac130_dist_loop", "veh_ac130_ext_dist", "cobra_helicopter_dying_loop"] {
            let rows = super::variants(&banks, a);
            let desc: Vec<String> = rows.iter().map(|r| {
                let src = r.loaded.clone().unwrap_or_else(|| r.streamed.as_ref().map(|(d, n)| format!("{d}/{n}")).unwrap_or_default());
                let pcm = super::decode(&banks, r);
                format!("{src} -> {}", pcm.map(|p| p.describe()).unwrap_or_else(|| "DECODE FAILED".into()))
            }).collect();
            eprintln!("{a}: {} rows: {desc:?}", rows.len());
        }
    }
}
