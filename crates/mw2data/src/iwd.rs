//! Streamed MW2 sounds live as files inside the `main/*.iwd` archives (plain zips),
//! under `sound/`. Localized archives (`localized_<lang>_iw*.iwd`) hold voice lines.
//! Mirrors IW4L's `asset_transport::iwd::IwdSoundIndex` without its other indexes.

use std::collections::HashMap;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

/// Where each `sound/...` entry lives, keyed by its path below `sound/`, lowercased.
pub struct SoundIndex {
    /// Archive and entry index.
    entries: HashMap<String, (PathBuf, usize)>,
    /// Archives opened once and kept: opening one parses its whole zip directory, which cost
    /// 20-90 ms per read on the game thread (every new icon, texture and voice line).
    open: Mutex<HashMap<PathBuf, zip::ZipArchive<std::fs::File>>>,
}

impl SoundIndex {
    /// Index every `*.iwd` in `<mw2>/main`. Later archives override earlier ones, as the
    /// game's search order does (higher-numbered iwds patch lower ones).
    pub fn open(main_dir: &Path) -> std::io::Result<Self> {
        Self::open_prefix(main_dir, "sound/")
    }

    /// Same, for any top-level folder (`"images/"` for IWI textures).
    pub fn open_prefix(main_dir: &Path, prefix: &str) -> std::io::Result<Self> {
        let mut archives: Vec<PathBuf> = std::fs::read_dir(main_dir)?
            .filter_map(|e| e.ok().map(|e| e.path()))
            .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("iwd")))
            .collect();
        archives.sort();
        let mut entries = HashMap::new();
        for archive in archives {
            let Ok(file) = std::fs::File::open(&archive) else { continue };
            let Ok(mut zip) = zip::ZipArchive::new(file) else { continue };
            for i in 0..zip.len() {
                let Ok(entry) = zip.by_index_raw(i) else { continue };
                let name = entry.name().replace('\\', "/");
                let lower = name.to_ascii_lowercase();
                if let Some(rest) = lower.strip_prefix(prefix) {
                    entries.insert(rest.to_owned(), (archive.clone(), i));
                }
            }
        }
        Ok(Self { entries, open: Mutex::new(HashMap::new()) })
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Raw bytes of `dir/name` (as an alias's streamed file names it).
    pub fn read(&self, dir: &str, name: &str) -> Option<Vec<u8>> {
        let key = if dir.is_empty() { name.to_owned() } else { format!("{dir}/{name}") }
            .replace('\\', "/")
            .to_ascii_lowercase();
        let (archive, index) = self.entries.get(&key)?;
        let mut open = self.open.lock().unwrap_or_else(|e| e.into_inner());
        if !open.contains_key(archive) {
            let zip = zip::ZipArchive::new(std::fs::File::open(archive).ok()?).ok()?;
            open.insert(archive.clone(), zip);
        }
        let mut f = open.get_mut(archive)?.by_index(*index).ok()?;
        let mut out = Vec::with_capacity(f.size() as usize);
        f.read_to_end(&mut out).ok()?;
        Some(out)
    }
}

/// Decoded PCM from a RIFF WAVE file (PCM 8/16-bit only).
pub struct Wav {
    pub rate: u32,
    pub channels: u16,
    pub bits: u16,
    pub format: u16,
    pub data: Vec<u8>,
}

pub fn parse_wav(bytes: &[u8]) -> Option<Wav> {
    if bytes.len() < 12 || &bytes[0..4] != b"RIFF" || &bytes[8..12] != b"WAVE" {
        return None;
    }
    let (mut fmt, mut data) = (None, None);
    let mut pos = 12;
    while pos + 8 <= bytes.len() {
        let id = &bytes[pos..pos + 4];
        let len = u32::from_le_bytes(bytes[pos + 4..pos + 8].try_into().ok()?) as usize;
        let body = bytes.get(pos + 8..(pos + 8 + len).min(bytes.len()))?;
        match id {
            b"fmt " if body.len() >= 16 => {
                fmt = Some((
                    u16::from_le_bytes([body[0], body[1]]),
                    u16::from_le_bytes([body[2], body[3]]),
                    u32::from_le_bytes(body[4..8].try_into().ok()?),
                    u16::from_le_bytes([body[14], body[15]]),
                ));
            }
            b"data" => data = Some(body.to_vec()),
            _ => {}
        }
        pos += 8 + len + (len & 1);
    }
    let (format, channels, rate, bits) = fmt?;
    Some(Wav { rate, channels, bits, format, data: data? })
}
