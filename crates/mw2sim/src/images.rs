//! MW2 HUD art (killstreak icons, minimap, overlays) for the host, decoded to RGBA8.
//! HUD code names materials (`specialty_carepackage`); the zones map each material to its
//! colour image (`specialty_care_package`), which lives in `main/*.iwd`. Rows come out
//! bottom-first, the order Unity's GUI expects.

use std::collections::HashMap;
use std::sync::RwLock;

pub static MATERIAL_IMAGES: RwLock<Option<HashMap<String, String>>> = RwLock::new(None);

pub fn add_materials(pairs: Vec<(String, String)>) {
    if let Ok(mut m) = MATERIAL_IMAGES.write() {
        let map = m.get_or_insert_with(HashMap::new);
        for (mat, img) in pairs {
            map.entry(mat.to_ascii_lowercase()).or_insert(img);
        }
    }
}

static MATERIAL_BLENDS: RwLock<Option<HashMap<String, i32>>> = RwLock::new(None);
static MATERIAL_BITS: RwLock<Option<HashMap<String, [u32; 2]>>> = RwLock::new(None);

/// Raw IW4 state bits of a 2D material (blend in word 0, depth / stencil in word 1).
pub fn state_bits(name: &str) -> Option<[u32; 2]> {
    MATERIAL_BITS.read().ok().and_then(|m| m.as_ref().and_then(|m| m.get(&name.trim_start_matches(',').to_ascii_lowercase()).copied()))
}

/// Take a walked zone's material -> image map and blend modes.
pub fn add_from(s: &mut mw2data::scripts::ScriptCapture) {
    add_materials(std::mem::take(&mut s.material_images));
    if let Ok(mut m) = MATERIAL_BLENDS.write() {
        let map = m.get_or_insert_with(HashMap::new);
        for (mat, bits) in std::mem::take(&mut s.material_state_bits) {
            map.entry(mat.to_ascii_lowercase()).or_insert_with(|| crate::fx::blend_mode(Some(bits)));
            if let Ok(mut r) = MATERIAL_BITS.write() {
                r.get_or_insert_with(HashMap::new).entry(mat.to_ascii_lowercase()).or_insert(bits);
            }
        }
    }
}

/// Blend state bits from a model capture (weapon zone materials).
pub fn add_state_bits(bits: Vec<(String, [u32; 2])>) {
    if let Ok(mut m) = MATERIAL_BLENDS.write() {
        let map = m.get_or_insert_with(HashMap::new);
        for (mat, b) in bits {
            map.entry(mat.to_ascii_lowercase()).or_insert_with(|| crate::fx::blend_mode(Some(b)));
            if let Ok(mut r) = MATERIAL_BITS.write() {
                r.get_or_insert_with(HashMap::new).entry(mat.to_ascii_lowercase()).or_insert(b);
            }
        }
    }
}

/// Blend byte for host draw commands: the blend mode, or 255 for materials that write only
/// destination alpha (MW2's 2D masks: `xpbar_stencilbase`, `720_xpbar` ...).
pub fn host_blend(name: &str) -> u8 {
    const COLOR_WRITE_RGB: u32 = 0x0800_0000;
    const COLOR_WRITE_ALPHA: u32 = 0x1000_0000;
    match state_bits(name) {
        Some([w0, _]) if w0 & (COLOR_WRITE_RGB | COLOR_WRITE_ALPHA) == COLOR_WRITE_ALPHA => 255,
        _ => blend(name).clamp(0, 254) as u8,
    }
}

/// A 2D material's IW4 state bits (blend factors / ops, colour write) for the host's compositor.
///
/// # Safety
/// `name` points to `len` bytes of UTF-8; `out` has room for 2 u32.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mw2_material_state_bits(name: *const u8, len: usize, out: *mut u32) -> i32 {
    if name.is_null() || out.is_null() {
        return 0;
    }
    let Ok(n) = std::str::from_utf8(unsafe { std::slice::from_raw_parts(name, len) }) else { return 0 };
    match state_bits(n) {
        Some([a, b]) => {
            unsafe {
                *out = a;
                *out.add(1) = b;
            }
            1
        }
        None => 0,
    }
}

/// MW2 blend mode of a 2D material (`fx::blend_mode`: 3 additive, 4 multiply, 2 blend ...).
pub fn blend(name: &str) -> i32 {
    MATERIAL_BLENDS.read().ok().and_then(|m| m.as_ref().and_then(|m| m.get(&name.trim_start_matches(',').to_ascii_lowercase()).copied())).unwrap_or(2)
}

pub fn image_name(name: &str) -> String {
    image_for(name)
}

fn image_for(name: &str) -> String {
    MATERIAL_IMAGES
        .read()
        .ok()
        .and_then(|m| m.as_ref().and_then(|m| m.get(&name.trim_start_matches(',').to_ascii_lowercase()).cloned()))
        .map(|img| img.trim_start_matches(',').to_owned())
        .unwrap_or_else(|| name.trim_start_matches(',').to_owned())
}

/// (width, height, RGBA8 bottom row first) for a HUD material or image name.
pub fn hud_rgba(name: &str) -> Option<(u32, u32, Vec<u8>)> {
    let img = image_for(name);
    let iwi = crate::models::texture(&img).or_else(|| crate::models::texture(name));
    let decoded = iwi.as_ref().and_then(|i| decode(i.format, i.width as usize, i.height as usize, &i.top_mip).map(|px| (i.width, i.height, px)));
    let Some((iw, ih, mut rgba)) = decoded else {
        // Wavelet-compressed images (menu art like logo_cod2): fx decodes those, rows already bottom-first.
        let t = crate::fx::texture_rgba(&img).or_else(|| crate::fx::texture_rgba(name))?;
        let (w, h, px) = &*t;
        return Some((*w, *h, px.clone()));
    };
    let (w, h) = (iw as usize, ih as usize);
    let row = w * 4;
    for y in 0..h / 2 {
        let (a, b) = rgba.split_at_mut((h - 1 - y) * row);
        a[y * row..y * row + row].swap_with_slice(&mut b[..row]);
    }
    Some((iw, ih, rgba))
}

/// A weapon's colour map with its paint (the material's detailMap) laid on: the colour map's
/// alpha is the paint mask, and painted texels are `colour * 2 * paint` (IW's detail blend: 0.5
/// grey leaves the colour alone). MW2 paints most of an ACOG with `detail_black_paint` (its colour
/// map alone is a pale grey); camos are paints too. The paint tiles at its own resolution.
/// RGBA8 in the IWI's own row order (as the compressed upload); None if either can't be decoded.
pub fn painted_rgba(color: &str, paint: &str) -> Option<(u32, u32, Vec<u8>)> {
    let c = crate::models::texture(color)?;
    let p = crate::models::texture(paint)?;
    let (w, h) = (c.width as usize, c.height as usize);
    let (pw, ph) = (p.width as usize, p.height as usize);
    let mut out = decode(c.format, w, h, &c.top_mip)?;
    let paint = decode(p.format, pw, ph, &p.top_mip)?;
    if pw == 0 || ph == 0 {
        return None;
    }
    for y in 0..h {
        for x in 0..w {
            let o = (y * w + x) * 4;
            let q = ((y % ph) * pw + (x % pw)) * 4;
            let mask = out[o + 3] as f32 / 255.0;
            for k in 0..3 {
                let col = out[o + k] as f32;
                let tint = 1.0 + (2.0 * paint[q + k] as f32 / 255.0 - 1.0) * mask;
                out[o + k] = (col * tint).round().clamp(0.0, 255.0) as u8;
            }
            out[o + 3] = 255;
        }
    }
    Some((c.width, c.height, out))
}

/// IWI formats: 1 ARGB32 (B,G,R,A bytes), 2 RGB24 (B,G,R), 3 A8L8 (L,A), 4 A8, 11/12/13 DXT1/3/5.
pub fn decode(format: u8, w: usize, h: usize, data: &[u8]) -> Option<Vec<u8>> {
    let mut out = vec![0u8; w * h * 4];
    match format {
        1 => {
            let src = data.get(..w * h * 4)?;
            for (o, s) in out.chunks_exact_mut(4).zip(src.chunks_exact(4)) {
                o.copy_from_slice(&[s[2], s[1], s[0], s[3]]);
            }
        }
        2 => {
            let src = data.get(..w * h * 3)?;
            for (o, s) in out.chunks_exact_mut(4).zip(src.chunks_exact(3)) {
                o.copy_from_slice(&[s[2], s[1], s[0], 255]);
            }
        }
        3 => {
            let src = data.get(..w * h * 2)?;
            for (o, s) in out.chunks_exact_mut(4).zip(src.chunks_exact(2)) {
                o.copy_from_slice(&[s[0], s[0], s[0], s[1]]);
            }
        }
        4 => {
            let src = data.get(..w * h)?;
            for (o, &a) in out.chunks_exact_mut(4).zip(src) {
                o.copy_from_slice(&[255, 255, 255, a]);
            }
        }
        11 | 12 | 13 => {
            let block = if format == 11 { 8 } else { 16 };
            let (bw, bh) = (w.div_ceil(4), h.div_ceil(4));
            if data.len() < bw * bh * block {
                return None;
            }
            for by in 0..bh {
                for bx in 0..bw {
                    let b = &data[(by * bw + bx) * block..][..block];
                    let (alpha, colour) = if format == 11 { (None, b) } else { (Some(&b[..8]), &b[8..]) };
                    let px = colour_block(colour, format == 11);
                    let alphas = alpha.map(|a| if format == 12 { explicit_alpha(a) } else { interpolated_alpha(a) });
                    for py in 0..4 {
                        for pxi in 0..4 {
                            let (x, y) = (bx * 4 + pxi, by * 4 + py);
                            if x >= w || y >= h {
                                continue;
                            }
                            let i = py * 4 + pxi;
                            let mut c = px[i];
                            if let Some(a) = alphas {
                                c[3] = a[i];
                            }
                            out[(y * w + x) * 4..][..4].copy_from_slice(&c);
                        }
                    }
                }
            }
        }
        _ => return None,
    }
    Some(out)
}

fn rgb565(v: u16) -> [u8; 3] {
    let r = ((v >> 11) & 31) as u32;
    let g = ((v >> 5) & 63) as u32;
    let b = (v & 31) as u32;
    [((r * 255 + 15) / 31) as u8, ((g * 255 + 31) / 63) as u8, ((b * 255 + 15) / 31) as u8]
}

fn colour_block(b: &[u8], dxt1: bool) -> [[u8; 4]; 16] {
    let c0v = u16::from_le_bytes([b[0], b[1]]);
    let c1v = u16::from_le_bytes([b[2], b[3]]);
    let (c0, c1) = (rgb565(c0v), rgb565(c1v));
    let mix = |a: u8, b: u8, wa: u32, wb: u32| ((a as u32 * wa + b as u32 * wb) / (wa + wb)) as u8;
    let mut pal = [[0u8; 4]; 4];
    pal[0] = [c0[0], c0[1], c0[2], 255];
    pal[1] = [c1[0], c1[1], c1[2], 255];
    if !dxt1 || c0v > c1v {
        pal[2] = [mix(c0[0], c1[0], 2, 1), mix(c0[1], c1[1], 2, 1), mix(c0[2], c1[2], 2, 1), 255];
        pal[3] = [mix(c0[0], c1[0], 1, 2), mix(c0[1], c1[1], 1, 2), mix(c0[2], c1[2], 1, 2), 255];
    } else {
        pal[2] = [mix(c0[0], c1[0], 1, 1), mix(c0[1], c1[1], 1, 1), mix(c0[2], c1[2], 1, 1), 255];
        pal[3] = [0, 0, 0, 0];
    }
    let bits = u32::from_le_bytes([b[4], b[5], b[6], b[7]]);
    std::array::from_fn(|i| pal[((bits >> (i * 2)) & 3) as usize])
}

fn explicit_alpha(a: &[u8]) -> [u8; 16] {
    let bits = u64::from_le_bytes(a[..8].try_into().unwrap());
    std::array::from_fn(|i| (((bits >> (i * 4)) & 15) as u8) * 17)
}

fn interpolated_alpha(a: &[u8]) -> [u8; 16] {
    let (a0, a1) = (a[0] as u32, a[1] as u32);
    let mut pal = [0u8; 8];
    pal[0] = a0 as u8;
    pal[1] = a1 as u8;
    if a0 > a1 {
        for k in 1..7 {
            pal[k + 1] = (((7 - k as u32) * a0 + k as u32 * a1) / 7) as u8;
        }
    } else {
        for k in 1..5 {
            pal[k + 1] = (((5 - k as u32) * a0 + k as u32 * a1) / 5) as u8;
        }
        pal[6] = 0;
        pal[7] = 255;
    }
    let mut bits = 0u64;
    for (i, &byte) in a[2..8].iter().enumerate() {
        bits |= (byte as u64) << (8 * i);
    }
    std::array::from_fn(|i| pal[((bits >> (i * 3)) & 7) as usize])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dxt1_solid_block() {
        // c0 = pure red (0xF800), all indices 0.
        let block = [0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0];
        let px = decode(11, 4, 4, &block).unwrap();
        assert_eq!(&px[..4], &[255, 0, 0, 255]);
        assert_eq!(&px[60..64], &[255, 0, 0, 255]);
    }

    #[test]
    fn dxt5_alpha_endpoints() {
        let mut block = [0u8; 16];
        block[0] = 200; // a0
        block[1] = 10; // a1, index bits 0 -> a0
        block[8..10].copy_from_slice(&0xFFFFu16.to_le_bytes()); // white
        let px = decode(13, 4, 4, &block).unwrap();
        assert_eq!(&px[..4], &[255, 255, 255, 200]);
    }
}

#[cfg(test)]
mod minimap_frame {
    /// FRAME=image: the alpha along the middle row and column of a HUD image (the minimap's window).
    #[test]
    #[ignore]
    fn frame_alpha() {
        const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
        unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
        let _ = crate::hud::load_from_dir(std::path::Path::new(COMMON_MP).parent().unwrap());
        let name = std::env::var("FRAME").unwrap_or_else(|_| "minimap_background".into());
        for m in ["hud_javelin_bg", "ac130_overlay_grain", "hud_javelin_lock_box", "minimap_background"] {
            eprintln!("FRAME blend {m}: {} bits {:x?} image {}", super::blend(m), super::state_bits(m), super::image_name(m));
        }
        let Some((w, h, px)) = super::hud_rgba(&name) else { panic!("no {name}") };
        let row: Vec<u8> = (0..w).map(|x| px[(((h / 2) * w + x) * 4 + 3) as usize]).collect();
        eprintln!("FRAME {name} {w}x{h} mid-row alpha: {:?}", row);
        let col: Vec<u8> = (0..h).map(|y| px[((y * w + w / 2) * 4 + 3) as usize]).collect();
        eprintln!("FRAME {name} mid-column alpha: {:?}", col);
    }
}
