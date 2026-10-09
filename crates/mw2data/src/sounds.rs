//! MW2 sound capture during a zone walk. Mirrors IW4L's
//! `asset_audio::sound_catalog` hooks without its catalog machinery.
//!
//! IW4 `LoadedSound` data is plain PCM (MSS format 1). Streamed aliases point into the
//! `main/*.iwd` archives and are only recorded here (dir, name), not decoded.

use std::collections::HashMap;

use fastfile_iw4::{Iw4WireFormat, Ptr, ZonePtr, ZoneStream};

pub const MSS_PCM: i32 = 1;

// snd_alias_t field offsets (x86, x64) and size — fastfile_iw4/src/load/sound.rs.
const ALIAS_SIZE: (usize, usize) = (100, 136);
const ALIAS_NAME: (usize, usize) = (0, 0);
const ALIAS_SECONDARY: (usize, usize) = (8, 16);
const ALIAS_SOUND_FILE: (usize, usize) = (20, 40);
const ALIAS_VOL_MIN: (usize, usize) = (0x1c, 52);
const ALIAS_VOL_MAX: (usize, usize) = (0x20, 56);
const ALIAS_PITCH_MIN: (usize, usize) = (0x24, 60);
const ALIAS_PITCH_MAX: (usize, usize) = (0x28, 64);

#[derive(Clone, Debug)]
pub struct LoadedPcm {
    pub name: String,
    pub format: i32,
    pub rate: u32,
    pub bits: i32,
    pub channels: i32,
    pub bytes: Vec<u8>,
}

impl LoadedPcm {
    pub fn seconds(&self) -> f32 {
        let frame = (self.bits.max(8) as usize / 8) * self.channels.max(1) as usize;
        if self.rate == 0 || frame == 0 {
            return 0.0;
        }
        (self.bytes.len() / frame) as f32 / self.rate as f32
    }
}

#[derive(Clone, Debug)]
pub struct Alias {
    /// Name of the alias list this row belongs to (what weapons reference).
    pub list: String,
    pub name: String,
    pub secondary: Option<String>,
    pub loaded: Option<String>,
    pub streamed: Option<(String, String)>,
    pub vol: (f32, f32),
    pub pitch: (f32, f32),
}

#[derive(Default)]
pub struct SoundCapture {
    pub loaded: HashMap<String, LoadedPcm>,
    pub aliases: Vec<Alias>,
    file_to_loaded: HashMap<(u8, u32), String>,
    /// LoadedSound header -> name: alias rows that point back at a sound another row loaded inline.
    header_to_loaded: HashMap<(u8, u32), String>,
    file_to_streamed: HashMap<(u8, u32), (String, String)>,
    last_loaded_name: Option<String>,
    pub gaps: usize,
}

fn key(p: Ptr) -> (u8, u32) {
    (p.block, p.offset)
}

fn lay(s: &ZoneStream<'_>, (x86, x64): (usize, usize)) -> usize {
    s.layout(x86, x64)
}

fn name_at(s: &ZoneStream<'_>, parent: Ptr, field: usize) -> Option<String> {
    match s.ptr_at(parent, field).ok()? {
        ZonePtr::Offset(p) => s.cstr(s.resolve_alias(p)).ok().map(str::to_owned),
        ZonePtr::Null => Some(String::new()),
        _ => None,
    }
}

impl SoundCapture {
    pub fn capture_loaded_sound(&mut self, s: &ZoneStream<'_>, header: Ptr, pcm: Ptr, data_len: usize) -> fastfile_iw4::Result<()> {
        let name = name_at(s, header, 0).unwrap_or_default();
        let (format, rate, bits, channels) = if s.wire_format() == Iw4WireFormat::X64 {
            (
                i32::from(s.u16_at(header, 8)?),
                s.u32_at(header, 12)?,
                i32::from(s.u16_at(header, 22)?),
                i32::from(s.u16_at(header, 10)?),
            )
        } else {
            (s.i32_at(header, 4)?, s.u32_at(header, 16)?, s.i32_at(header, 20)?, s.i32_at(header, 24)?)
        };
        let bytes = s.slice_at(pcm, 0, data_len)?.to_vec();
        if name.is_empty() {
            self.gaps += 1;
            self.last_loaded_name = None;
            return Ok(());
        }
        self.last_loaded_name = Some(name.clone());
        self.header_to_loaded.insert(key(header), name.clone());
        if !bytes.is_empty() {
            self.loaded.insert(name.clone(), LoadedPcm { name, format, rate, bits, channels, bytes });
        }
        Ok(())
    }

    pub fn bind_last_loaded_to_sound_file(&mut self, file: Ptr) {
        match self.last_loaded_name.clone() {
            Some(name) => {
                self.file_to_loaded.insert(key(file), name);
            }
            None => self.gaps += 1,
        }
    }

    pub fn bind_streamed_sound_file(&mut self, file: Ptr, dir: &str, name: &str) {
        self.file_to_streamed.insert(key(file), (dir.to_owned(), name.to_owned()));
    }

    pub fn capture_sound(&mut self, s: &ZoneStream<'_>, list: Ptr, count: usize, head: Option<Ptr>) -> fastfile_iw4::Result<()> {
        let list_name = name_at(s, list, 0).unwrap_or_default();
        let Some(arr) = head else {
            if count > 0 {
                self.gaps += 1;
            }
            return Ok(());
        };
        let stride = lay(s, ALIAS_SIZE);
        for i in 0..count {
            let row = arr.at(i * stride);
            let name = name_at(s, row, lay(s, ALIAS_NAME)).unwrap_or_default();
            let secondary = name_at(s, row, lay(s, ALIAS_SECONDARY)).filter(|n| !n.is_empty());
            let (loaded, streamed) = match s.ptr_at(row, lay(s, ALIAS_SOUND_FILE)).ok() {
                Some(ZonePtr::Offset(p)) => {
                    let file = s.resolve_alias(p);
                    let loaded = self.file_to_loaded.get(&key(file)).cloned().or_else(|| {
                        // SAT_LOADED pointing at a LoadedSound loaded earlier (several rows share one).
                        let loaded_field = if s.wire_format() == Iw4WireFormat::X64 { 8 } else { 4 };
                        (s.u8_at(file, 0).ok()? == 1).then_some(())?;
                        let ZonePtr::Offset(h) = s.ptr_at(file, loaded_field).ok()? else { return None };
                        let h = s.resolve_alias(h);
                        // Either the LoadedSound header itself, or the loadSnd slot of an earlier SoundFile
                        // that loaded it inline (rows sharing one sound point there).
                        self.header_to_loaded.get(&key(h)).cloned().or_else(|| {
                            let owner = (h.block, h.offset.checked_sub(loaded_field as u32)?);
                            self.file_to_loaded.get(&owner).cloned()
                        })
                    });
                    (loaded, self.file_to_streamed.get(&key(file)).cloned())
                }
                _ => (None, None),
            };
            if loaded.is_none() && streamed.is_none() {
                self.gaps += 1;
            }
            let f = |off| s.f32_at(row, lay(s, off)).ok().filter(|v: &f32| v.is_finite());
            let vol = (f(ALIAS_VOL_MIN).unwrap_or(1.0), f(ALIAS_VOL_MAX).unwrap_or(1.0));
            let pitch = (f(ALIAS_PITCH_MIN).unwrap_or(1.0), f(ALIAS_PITCH_MAX).unwrap_or(1.0));
            self.aliases.push(Alias { list: list_name.clone(), name, secondary, loaded, streamed, vol, pitch });
        }
        Ok(())
    }

    /// Every playable variant of an alias list (MW2 picks one at random per play),
    /// following `secondary` up to two hops when the list itself has none.
    pub fn variants(&self, list: &str) -> Vec<&Alias> {
        let mut want = list.to_owned();
        for _ in 0..3 {
            let rows: Vec<&Alias> = self.aliases.iter().filter(|a| a.list == want).collect();
            let playable: Vec<&Alias> = rows.iter().copied().filter(|a| a.loaded.is_some()).collect();
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

    /// First playable source for an alias list name, following `secondary` up to two hops.
    pub fn resolve(&self, list: &str) -> Option<&Alias> {
        let mut want = list.to_owned();
        for _ in 0..3 {
            let rows: Vec<&Alias> = self.aliases.iter().filter(|a| a.list == want || a.name == want).collect();
            if let Some(hit) = rows.iter().find(|a| a.loaded.is_some() || a.streamed.is_some()) {
                return Some(hit);
            }
            match rows.iter().find_map(|a| a.secondary.clone()) {
                Some(next) => want = next,
                None => return None,
            }
        }
        None
    }
}
