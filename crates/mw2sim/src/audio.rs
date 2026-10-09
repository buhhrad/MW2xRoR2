//! MW2 weapon audio played straight to the default Windows output device.
//!
//! Risk of Rain 2 runs all its sound through Wwise and ships with Unity's own audio
//! disabled (`AudioClip.Create` yields empty clips), so the host can't play our PCM.
//! A dedicated thread owns the rodio output stream (it isn't `Send`); plays arrive
//! over a channel and are mixed there.

use std::sync::mpsc::{Sender, channel};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use rodio::Source;

/// Decoded clip, shared by every play of it.
#[derive(Clone)]
pub struct Pcm {
    samples: Arc<[f32]>,
    channels: u16,
    rate: u32,
}

impl Pcm {
    /// "N s, rate Hz, channels" (tests and logs).
    pub fn describe(&self) -> String {
        let frames = self.samples.len() / usize::from(self.channels.max(1));
        format!("{:.1} s, {} Hz, {} ch", frames as f32 / self.rate.max(1) as f32, self.rate, self.channels)
    }

    pub fn from_loaded(p: &mw2data::sounds::LoadedPcm) -> Option<Self> {
        let samples: Vec<f32> = match p.bits {
            16 => p.bytes.chunks_exact(2).map(|b| i16::from_le_bytes([b[0], b[1]]) as f32 / 32768.0).collect(),
            8 => p.bytes.iter().map(|&b| (b as f32 - 128.0) / 128.0).collect(),
            _ => return None,
        };
        if samples.is_empty() || p.channels <= 0 || p.rate == 0 {
            return None;
        }
        Some(Self { samples: samples.into(), channels: p.channels as u16, rate: p.rate })
    }
}

struct Playback {
    pcm: Pcm,
    pos: usize,
}

impl Iterator for Playback {
    type Item = f32;
    fn next(&mut self) -> Option<f32> {
        let s = self.pcm.samples.get(self.pos).copied();
        self.pos += 1;
        s
    }
}

impl Source for Playback {
    fn current_frame_len(&self) -> Option<usize> {
        Some(self.pcm.samples.len().saturating_sub(self.pos))
    }
    fn channels(&self) -> u16 {
        self.pcm.channels
    }
    fn sample_rate(&self) -> u32 {
        self.pcm.rate
    }
    fn total_duration(&self) -> Option<Duration> {
        let frames = self.pcm.samples.len() / self.pcm.channels.max(1) as usize;
        Some(Duration::from_secs_f64(frames as f64 / self.pcm.rate as f64))
    }
}

enum Cmd {
    Play(Pcm, f32, f32),
    /// A looping sound (playLoopSound on a vehicle): id, clip, volume.
    Loop(u32, Pcm, f32),
    LoopVolume(u32, f32),
    LoopStop(u32),
}

fn sender() -> Option<&'static Mutex<Sender<Cmd>>> {
    static TX: OnceLock<Option<Mutex<Sender<Cmd>>>> = OnceLock::new();
    TX.get_or_init(|| {
        let (tx, rx) = channel::<Cmd>();
        let (ready_tx, ready_rx) = channel::<bool>();
        std::thread::Builder::new()
            .name("mw2-audio".into())
            .spawn(move || {
                let Ok((_stream, handle)) = rodio::OutputStream::try_default() else {
                    let _ = ready_tx.send(false);
                    return;
                };
                let _ = ready_tx.send(true);
                let mut loops: std::collections::HashMap<u32, rodio::Sink> = std::collections::HashMap::new();
                // Volumes set while a loop's clip was still decoding (applied when it starts).
                let mut early: std::collections::HashMap<u32, f32> = std::collections::HashMap::new();
                while let Ok(cmd) = rx.recv() {
                    match cmd {
                        Cmd::Play(pcm, volume, pitch) => {
                            let _ = handle.play_raw(Playback { pcm, pos: 0 }.speed(pitch).amplify(volume));
                        }
                        Cmd::Loop(id, pcm, volume) => {
                            if let Ok(sink) = rodio::Sink::try_new(&handle) {
                                sink.set_volume(early.remove(&id).unwrap_or(volume));
                                sink.append(Playback { pcm, pos: 0 }.repeat_infinite());
                                loops.insert(id, sink);
                            }
                        }
                        Cmd::LoopVolume(id, volume) => match loops.get(&id) {
                            Some(s) => s.set_volume(volume),
                            None => {
                                early.insert(id, volume);
                            }
                        },
                        Cmd::LoopStop(id) => {
                            early.remove(&id);
                            if let Some(s) = loops.remove(&id) {
                                s.stop();
                            }
                        }
                    }
                }
            })
            .ok()?;
        match ready_rx.recv_timeout(Duration::from_secs(3)) {
            Ok(true) => Some(Mutex::new(tx)),
            _ => None,
        }
    })
    .as_ref()
}

/// Returns false when no output device could be opened.
pub fn play(pcm: Pcm, volume: f32, pitch: f32) -> bool {
    let pitch = if pitch.is_finite() { pitch.clamp(0.5, 2.0) } else { 1.0 };
    #[cfg(feature = "dev")]
    tape_one_shot(&pcm, volume.clamp(0.0, 2.0), pitch);
    let Some(tx) = sender() else { return false };
    tx.lock().map(|tx| tx.send(Cmd::Play(pcm, volume.clamp(0.0, 2.0), pitch)).is_ok()).unwrap_or(false)
}

static NEXT_LOOP: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);

/// A new loop id (the clip may still be decoding when it's handed out).
pub fn new_loop_id() -> u32 {
    NEXT_LOOP.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

pub fn loop_start(id: u32, pcm: Pcm, volume: f32) -> bool {
    #[cfg(feature = "dev")]
    tape_loop(|t| {
        let volume = t.early.remove(&id).unwrap_or(volume);
        t.loops.insert(id, TapeLoop { pcm: pcm.clone(), volume, pos: 0.0 });
    });
    let Some(tx) = sender() else { return false };
    tx.lock().map(|tx| tx.send(Cmd::Loop(id, pcm, volume.clamp(0.0, 2.0))).is_ok()).unwrap_or(false)
}

pub fn loop_volume(id: u32, volume: f32) {
    #[cfg(feature = "dev")]
    tape_loop(|t| match t.loops.get_mut(&id) {
        Some(l) => l.volume = volume.clamp(0.0, 2.0),
        None => {
            t.early.insert(id, volume.clamp(0.0, 2.0));
        }
    });
    if let Some(tx) = sender() {
        let _ = tx.lock().map(|tx| tx.send(Cmd::LoopVolume(id, volume.clamp(0.0, 2.0))));
    }
}

pub fn loop_stop(id: u32) {
    #[cfg(feature = "dev")]
    tape_loop(|t| {
        t.loops.remove(&id);
        t.early.remove(&id);
    });
    if let Some(tx) = sender() {
        let _ = tx.lock().map(|tx| tx.send(Cmd::LoopStop(id)));
    }
}

/// Uniform in [lo, hi], from a process-wide xorshift.
pub fn rand_range(lo: f32, hi: f32) -> f32 {
    use std::sync::atomic::{AtomicU64, Ordering};
    static STATE: AtomicU64 = AtomicU64::new(0x9E37_79B9_7F4A_7C15);
    let mut x = STATE.load(Ordering::Relaxed);
    x ^= x << 13;
    x ^= x >> 7;
    x ^= x << 17;
    STATE.store(x, Ordering::Relaxed);
    let u = (x >> 40) as f32 / (1u64 << 24) as f32;
    if hi > lo { lo + (hi - lo) * u } else { lo }
}

// ---- the recorder's tape ------------------------------------------------------------------------
//
// The showcase recorder runs the game at a fixed 60 frames a step, far slower than real time, so the
// sound out of the speakers is no use to it. While a tape runs, every play and loop is also mixed
// here at the recording's own clock (advanced a frame at a time by the recorder) and written out as
// a WAV the recorder puts under the video. Dev builds only (`--features dev`): the recorder isn't in
// the released mod.

#[cfg(feature = "dev")]
const TAPE_RATE: u32 = 48_000;
#[cfg(feature = "dev")]
/// Ten minutes, stereo.
const TAPE_MAX_FRAMES: usize = TAPE_RATE as usize * 600;

#[cfg(feature = "dev")]
struct TapeLoop {
    pcm: Pcm,
    volume: f32,
    pos: f64,
}

#[cfg(feature = "dev")]
struct Tape {
    buf: Vec<f32>,
    clock: f64,
    loops: std::collections::HashMap<u32, TapeLoop>,
    early: std::collections::HashMap<u32, f32>,
}

#[cfg(feature = "dev")]
fn tape() -> &'static Mutex<Option<Tape>> {
    static TAPE: OnceLock<Mutex<Option<Tape>>> = OnceLock::new();
    TAPE.get_or_init(|| Mutex::new(None))
}

#[cfg(feature = "dev")]
fn tape_loop(f: impl FnOnce(&mut Tape)) {
    if let Ok(mut g) = tape().lock()
        && let Some(t) = g.as_mut()
    {
        f(t);
    }
}

#[cfg(feature = "dev")]
/// Mix `frames` output frames of `pcm` from source frame `pos` (stepping `step` source frames per
/// output frame, linear between) into the tape at output frame `at`. Returns where the source got to.
fn mix(buf: &mut Vec<f32>, at: usize, pcm: &Pcm, mut pos: f64, step: f64, frames: usize, volume: f32, wrap: bool) -> f64 {
    let ch = usize::from(pcm.channels.max(1));
    let total = pcm.samples.len() / ch;
    if total < 2 || volume <= 0.0 || at >= TAPE_MAX_FRAMES {
        return pos + step * frames as f64;
    }
    let frames = frames.min(TAPE_MAX_FRAMES - at);
    if buf.len() < (at + frames) * 2 {
        buf.resize((at + frames) * 2, 0.0);
    }
    let s = |f: usize, c: usize| pcm.samples[f * ch + c.min(ch - 1)];
    for i in 0..frames {
        if wrap {
            pos %= total as f64;
        } else if pos >= (total - 1) as f64 {
            break;
        }
        let i0 = pos as usize;
        let i1 = if i0 + 1 < total { i0 + 1 } else { 0 };
        let f = (pos - i0 as f64) as f32;
        let o = (at + i) * 2;
        buf[o] += (s(i0, 0) + (s(i1, 0) - s(i0, 0)) * f) * volume;
        buf[o + 1] += (s(i0, 1) + (s(i1, 1) - s(i0, 1)) * f) * volume;
        pos += step;
    }
    pos
}

#[cfg(feature = "dev")]
fn tape_one_shot(pcm: &Pcm, volume: f32, pitch: f32) {
    tape_loop(|t| {
        let step = f64::from(pitch) * f64::from(pcm.rate) / f64::from(TAPE_RATE);
        let total = pcm.samples.len() / usize::from(pcm.channels.max(1));
        let frames = (total as f64 / step).ceil() as usize;
        let at = (t.clock * f64::from(TAPE_RATE)).round() as usize;
        mix(&mut t.buf, at, pcm, 0.0, step, frames, volume, false);
    });
}

#[cfg(feature = "dev")]
/// Start a fresh tape (the recorder's first frame is its time 0).
pub fn tape_start() {
    if let Ok(mut g) = tape().lock() {
        *g = Some(Tape { buf: Vec::new(), clock: 0.0, loops: Default::default(), early: Default::default() });
    }
}

#[cfg(feature = "dev")]
/// One recorded frame went by: the loops play through it and the clock moves on.
pub fn tape_advance(dt: f32) {
    tape_loop(|t| {
        let dt = if dt.is_finite() { f64::from(dt.clamp(0.0, 1.0)) } else { 0.0 };
        let from = (t.clock * f64::from(TAPE_RATE)).round() as usize;
        let to = ((t.clock + dt) * f64::from(TAPE_RATE)).round() as usize;
        let Tape { buf, loops, .. } = t;
        for l in loops.values_mut() {
            let step = f64::from(l.pcm.rate) / f64::from(TAPE_RATE);
            l.pos = mix(buf, from, &l.pcm, l.pos, step, to - from, l.volume, true);
        }
        t.clock += dt;
    });
}

#[cfg(feature = "dev")]
/// End the tape and write it as a 16-bit stereo WAV at `path`: soft-clipped, the last second faded.
/// The tape's length in seconds, or -1 when there was none or the file couldn't be written.
pub fn tape_stop(path: &str) -> f32 {
    let Some(t) = tape().lock().ok().and_then(|mut g| g.take()) else { return -1.0 };
    let frames = ((t.clock * f64::from(TAPE_RATE)).round() as usize).min(TAPE_MAX_FRAMES);
    let fade = TAPE_RATE as usize;
    let mut pcm = Vec::with_capacity(frames * 4);
    for i in 0..frames * 2 {
        let f = i / 2;
        let g = if f + fade > frames { (frames - f) as f32 / fade as f32 } else { 1.0 };
        let x = t.buf.get(i).copied().unwrap_or(0.0) * g;
        pcm.extend_from_slice(&((x.tanh() * 32767.0) as i16).to_le_bytes());
    }
    let mut wav = Vec::with_capacity(44 + pcm.len());
    let le32 = |v: u32| v.to_le_bytes();
    wav.extend_from_slice(b"RIFF");
    wav.extend_from_slice(&le32(36 + pcm.len() as u32));
    wav.extend_from_slice(b"WAVEfmt ");
    wav.extend_from_slice(&le32(16));
    wav.extend_from_slice(&1u16.to_le_bytes());
    wav.extend_from_slice(&2u16.to_le_bytes());
    wav.extend_from_slice(&le32(TAPE_RATE));
    wav.extend_from_slice(&le32(TAPE_RATE * 4));
    wav.extend_from_slice(&4u16.to_le_bytes());
    wav.extend_from_slice(&16u16.to_le_bytes());
    wav.extend_from_slice(b"data");
    wav.extend_from_slice(&le32(pcm.len() as u32));
    wav.extend_from_slice(&pcm);
    match std::fs::write(path, wav) {
        Ok(()) => (frames as f64 / f64::from(TAPE_RATE)) as f32,
        Err(_) => -1.0,
    }
}

#[cfg(all(test, feature = "dev"))]
mod tape_tests {
    use super::*;

    #[test]
    fn a_shot_lands_on_the_frame_clock() {
        tape_start();
        let pcm = Pcm { samples: vec![0.5f32; 4800].into(), channels: 1, rate: 48_000 };
        for _ in 0..30 {
            tape_advance(1.0 / 60.0);
        }
        tape_one_shot(&pcm, 1.0, 1.0); // at 0.5 s
        for _ in 0..60 {
            tape_advance(1.0 / 60.0);
        }
        {
            let g = tape().lock().unwrap();
            let t = g.as_ref().unwrap();
            assert_eq!(t.buf[(24_000 - 1) * 2], 0.0);
            assert!((t.buf[(24_000 + 10) * 2] - 0.5).abs() < 1e-6 && (t.buf[(24_000 + 10) * 2 + 1] - 0.5).abs() < 1e-6);
            assert!((t.clock - 1.5).abs() < 1e-5);
        }
        let p = std::env::temp_dir().join("mw2_tape_test.wav");
        let secs = tape_stop(p.to_str().unwrap());
        assert!((secs - 1.5).abs() < 1e-3, "{secs}");
        assert_eq!(std::fs::metadata(&p).unwrap().len(), 44 + 72_000 * 4);
    }
}
