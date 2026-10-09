use super::*;

#[test]
fn struct_sizes_match_the_c_side() {
    assert_eq!(size_of::<Mw2HudState>(), 72);
    assert_eq!(size_of::<Mw2HudCmd>(), 64);
}

#[test]
fn font_enums_map_to_mw2_font_assets() {
    assert_eq!(font_name(9), "fonts/hudBigFont");
    assert_eq!(font_name(10), "fonts/hudSmallFont");
    assert_eq!(font_name(2), "fonts/bigFont");
    assert_eq!(font_name(0), "");
    // IW4L hands back lower-case handles; they map back to the enum.
    assert_eq!(font_enum_of("fonts/hudbigfont"), 9);
    assert_eq!(font_enum_of("fonts/extrabigfont"), 8);
    let mut buf = [0u8; 32];
    let n = unsafe { mw2_hud_font_name(10, buf.as_mut_ptr(), 32) } as usize;
    assert_eq!(&buf[..n], b"fonts/hudSmallFont");
}

#[test]
fn rotate_st_matches_iw4() {
    let id = rotate_st(0.0);
    assert_eq!(id, [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]]);
    let q = rotate_st(90.0);
    // Quarter turn: the top-left corner samples what was the top-right.
    assert!((q[0][0] - 1.0).abs() < 1e-5 && (q[0][1] - 0.0).abs() < 1e-5, "{q:?}");
    assert!((q[1][0] - 1.0).abs() < 1e-5 && (q[1][1] - 1.0).abs() < 1e-5, "{q:?}");
}

#[test]
fn alignment_places_the_stock_count_like_mw2() {
    // Real MW2 at 1000x563 puts the stock count's left edge near x 840-850.
    let pl = ScreenPlacement::setup_fullscreen(1000.0, 563.0, 1.0);
    let a = pl.apply_rect(-136.0, -32.7, 37.3, 0.7, 10, 10);
    assert!((a.x - 840.5).abs() < 0.5, "x {}", a.x);
    assert!((a.y - (563.0 - 32.7 * 563.0 / 480.0)).abs() < 0.01);
    // The full-width xp bar (alignment 4 horizontally) is the whole screen.
    let b = pl.apply_rect(0.0, -10.7, 853.3, 10.7, 4, 10);
    assert!(b.x.abs() < 1e-3 && (b.w - 853.3 * 1000.0 / 640.0).abs() < 0.5, "{b:?}");
}

#[test]
fn expressions_get_their_stand_in_opcodes() {
    // rank-for-xp (0x6F) and horizontal safe area (0xB3) are not in IW4L's evaluator.
    let e = Exp::compile("op 16 op 179 op 1 op 2 640").expect("compiled");
    assert!(e.stmt.is_some());
    let e = Exp::compile("op 16 op 111 op 108 s:657870657269656e6365 op 1 op 1").expect("compiled");
    assert!(e.stmt.is_some());
    // A dump cut by the loader's nested-statement buffer does not parse.
    let cut = Exp::compile("op 16 { op 16 op 74 s:6d702f72616e6b5461626c652e637376 op 17 0 op 17 f:3f7f }").expect("compiled");
    assert!(cut.stmt.is_none());
}

fn test_ctx(w: f32, h: f32) -> Ctx {
    let pl = ScreenPlacement::setup_fullscreen(w, h, 1.0);
    Ctx {
        st: Mw2HudState { screen_w: w, screen_h: h, ..Mw2HudState::default() },
        sx: pl.scale_virtual_to_real[0],
        sy: pl.scale_virtual_to_real[1],
        pl,
        winfo: None,
        xp: 0.0,
        xp_lo: 0.0,
        xp_hi: 0.0,
        wide: true,
        select_time: 0,
        dpad: Default::default(),
        frag_icon: String::new(),
        smoke_icon: String::new(),
        bindings: HashMap::new(),
        north_yaw: 0.0,
        ride: 0,
        xpbar_fitted: false,
    }
}

fn test_item(style: i32, rect: [f32; 4], background: &str) -> Item {
    Item {
        name: String::new(),
        text: String::new(),
        item_type: 0,
        style,
        owner_draw: 0,
        rect,
        horz: 10,
        vert: 10,
        fore: [1.0, 1.0, 1.0, 1.0],
        back: [0.2, 0.4, 0.6, 0.5],
        text_scale: 0.5,
        font: 0,
        text_align: 0,
        text_align_x: 0.0,
        text_align_y: 0.0,
        text_style: 0,
        background: background.to_owned(),
        vis: None,
        text_exp: None,
        material_exp: None,
        floats: Vec::new(),
        scripts: Vec::new(),
        static_flags: 0,
        dvar: String::new(),
        disabled: None,
    }
}

#[test]
fn filled_windows_are_solid_quads_and_stretches_flip_on_negative_size() {
    let c = test_ctx(1920.0, 1080.0);
    let d = HudData::default();
    let menu = Menu { name: "t".into(), rect: [0.0; 4], vis: None, floats: Vec::new(), dvars: Vec::new(), items: Vec::new(), scripts: Vec::new(), fullscreen: false };
    let host = Host { c: &c, d: &d, menu: &menu };
    let mut out = Out::default();
    let parent = [0.0; 4];
    paint_item(&mut out, &c, &d, &host, &menu, 0, &test_item(1, [-100.0, -50.0, 40.0, 20.0], ""), &parent, None);
    paint_item(&mut out, &c, &d, &host, &menu, 1, &test_item(3, [-100.0, -50.0, -40.0, 20.0], ",white"), &parent, None);
    // Fully off screen: dropped.
    paint_item(&mut out, &c, &d, &host, &menu, 2, &test_item(3, [-5000.0, -50.0, 40.0, 20.0], "white"), &parent, None);
    assert_eq!(out.cmds.len(), 2, "{:?}", out.cmds);
    // Filled window: the back colour, the screen's bottom-right corner is the origin (alignment 10).
    let solid = out.cmds[0];
    assert_eq!((solid.kind, solid.color), (2, [0.2, 0.4, 0.6, 0.5]));
    assert!((solid.rect[0] - (1920.0 - 100.0 * 2.25)).abs() < 1e-3 && (solid.rect[2] - 90.0).abs() < 1e-3);
    // A negative width flips s; the leading comma of a referenced material is dropped.
    let flipped = out.cmds[1];
    assert_eq!((flipped.kind, flipped.uv), (0, [1.0, 0.0, 0.0, 1.0]));
    assert_eq!(out.strings, ["white"]);
}

#[test]
fn xp_bar_edge_follows_the_fill_fraction() {
    let mut c = test_ctx(1920.0, 1080.0);
    c.xp_lo = 3600.0;
    c.xp_hi = 6200.0;
    c.xp = 3600.0 + 0.5 * 2600.0;
    // Layers with rank-table dumps: right edge = int((0.5 * 0.95 + 0.005) * 853.333) = 409 units.
    let x = xp_edge_x(&c, "op 16 op 74 s:6d702f72616e6b5461626c652e637376 op 1", 853.333).unwrap();
    assert_eq!(x, 409.0 - 853.333);
    // Rested-XP layers (no rested XP) sit far left of the screen.
    let rest = xp_edge_x(&c, "op 74 s:726573745850476f616c", 853.333).unwrap();
    assert!(rest < -853.333);
}

#[test]
fn abi_is_null_safe() {
    let mut cmd = Mw2HudCmd::default();
    assert_eq!(unsafe { mw2_hud_frame(std::ptr::null(), &mut cmd, 1) }, 0);
    let st = Mw2HudState::default();
    assert_eq!(unsafe { mw2_hud_frame(&st, std::ptr::null_mut(), 1) }, 0);
    assert_eq!(unsafe { mw2_hud_frame(&st, &mut cmd, 0) }, 0);
    assert_eq!(unsafe { mw2_hud_cmd_string(60000, std::ptr::null_mut(), 0) }, 0);
    unsafe { mw2_hud_rotate_st(1.0, std::ptr::null_mut()) };
}

// --- real-data layout + preview -------------------------------------------------------------

const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

fn text_of(strings: &[String], i: u16) -> &str {
    strings.get(usize::from(i)).map_or("?", String::as_str)
}

fn dump_frame(label: &str, cmds: &[Mw2HudCmd], strings: &[String]) {
    eprintln!("=== {label}: {} commands ===", cmds.len());
    for (i, c) in cmds.iter().enumerate() {
        let what = match c.kind {
            0 => format!("quad  {:<34}", text_of(strings, c.material)),
            1 => format!("text  {:<34}", format!("{:?} (font {} {})", text_of(strings, c.material), c.font, font_name(u32::from(c.font)))),
            _ => format!("solid {:<34}", ""),
        };
        let rot = if c.rotation_deg != 0.0 { format!(" rot {:.1}", c.rotation_deg) } else { String::new() };
        let extra = if c.kind == 1 {
            format!(" glyph scale {:.3}/{:.3} width {:.1}px style {}", c.rect[2], c.rect[3], c.uv[0], c._pad)
        } else {
            format!(" size {:.1}x{:.1}", c.rect[2], c.rect[3])
        };
        eprintln!("{i:3} {what} at ({:7.1},{:7.1}){extra} rgba({:.2},{:.2},{:.2},{:.2}){rot}", c.rect[0], c.rect[1], c.color[0], c.color[1], c.color[2], c.color[3]);
    }
}

fn digest(cmds: &[Mw2HudCmd]) -> u64 {
    let mut h = 1469598103934665603u64;
    for c in cmds {
        for v in c.rect.iter().chain(&c.color) {
            h = (h ^ u64::from(v.to_bits())).wrapping_mul(1099511628211);
        }
    }
    h
}

#[test]
#[ignore = "needs the MW2 install"]
fn real_zones_layout_and_preview() {
    assert!(std::path::Path::new(COMMON_MP).exists(), "MW2 install not found at {COMMON_MP}");
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        let items = mw2_hud_init();
        eprintln!("hud items loaded: {items}");
        assert!(items > 40, "menus loaded");
    }
    set_diagnostics(true);
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_preview");
    std::fs::create_dir_all(&dir).unwrap();

    // Equipment icons and killstreak d-pad icons come from the weapon / streak tables.
    let icon_of = |name: &str| crate::weapons::row(crate::weapons::index_of(name)).and_then(|r| r.hud_icon).unwrap_or_default();
    let (frag, smoke) = (icon_of("frag_grenade_mp"), icon_of("smoke_grenade_mp"));
    eprintln!("frag icon {frag:?}, smoke icon {smoke:?}");
    unsafe {
        mw2_hud_set_offhand(frag.as_ptr(), frag.len(), smoke.as_ptr(), smoke.len());
        let streaks = crate::killstreaks::with_table(|t| t.streaks.iter().take(2).map(|s| s.dpad.clone()).collect::<Vec<_>>()).unwrap_or_default();
        eprintln!("dpad icons {streaks:?}");
        for (i, icon) in streaks.iter().enumerate() {
            mw2_hud_set_dpad(4 - i as u32, icon.as_ptr(), icon.len(), 0, 1, 1, 1);
        }
        // Test labels for the key hints (the host supplies the player's real bindings).
        for (cmd, label) in [("+actionslot 3", "TEST3"), ("+actionslot 4", "TEST4"), ("+holdbreath", "TESTKEY")] {
            mw2_hud_set_binding(cmd.as_ptr(), cmd.len(), label.as_ptr(), label.len());
        }
    }

    let mut reasons: Vec<String> = Vec::new();
    let mut keep = |diag: Vec<String>| {
        for d in diag {
            if !reasons.contains(&d) {
                reasons.push(d);
            }
        }
    };
    let mut frames = 0;
    for (w, h) in [(1920.0f32, 1080.0f32), (2560.0, 1440.0)] {
        for (gun, clip, size, stock) in [("ak47_mp", 25, 30, 180), ("rpd_mp", 100, 100, 300)] {
            let weapon = crate::weapons::index_of(gun);
            assert!(weapon != 0, "{gun}");
            let st = Mw2HudState {
                screen_w: w,
                screen_h: h,
                time_ms: 10_000,
                weapon,
                clip,
                clip_size: size,
                stock,
                frags: 1,
                smokes: 1,
                yaw_deg: 37.0,
                xp_frac: 0.42,
                rank: 5,
                low_ammo_ok: 1,
                ..Mw2HudState::default()
            };
            let (cmds, strings, diag) = frame(&st);
            let label = format!("{gun} {w}x{h}");
            dump_frame(&label, &cmds, &strings);
            keep(diag);
            frames += 1;
            assert!(cmds.len() > 20, "{label}: commands");
            // The stock count is MW2's bold hudBigFont text in the bottom-right.
            let stock_cmd = cmds.iter().find(|c| c.kind == 1 && text_of(&strings, c.material).trim() == stock.to_string()).expect("stock text");
            assert_eq!(stock_cmd.font, 9, "stock uses hudBigFont");
            assert!(stock_cmd.rect[0] > w * 0.80 && stock_cmd.rect[1] > h * 0.85, "{label}: stock at {:?}", stock_cmd.rect);
            // Clip pips: one per round, a strip left of the stock count.
            let pips = cmds.iter().filter(|c| c.kind == 0 && text_of(&strings, c.material).starts_with("ammo_counter_")).count();
            assert_eq!(pips as i32, size, "{label}: pips");
            // XP bar spans the bottom edge.
            assert!(
                cmds.iter().any(|c| c.kind == 0 && text_of(&strings, c.material) == "720_xpbar_empty" && c.rect[3] < h * 0.03 && (c.rect[1] + c.rect[3] - h).abs() < 1.5),
                "{label}: xp bar"
            );
            // Compass ring rotates by -(yaw - north).
            assert!(
                cmds.iter().any(|c| c.kind == 0 && text_of(&strings, c.material).starts_with("hud_compass_letters") && (c.rotation_deg + 37.0).abs() < 1e-3),
                "{label}: compass"
            );
            let name = format!("{gun}_{}x{}", w as u32, h as u32);
            preview::write(&dir.join(format!("{name}_render.png")), &cmds, &strings, w as usize, h as usize, false);
            preview::write(&dir.join(format!("{name}_rects.png")), &cmds, &strings, w as usize, h as usize, true);
        }
    }

    // Low ammo + hold breath + weapon-name fade, 1920x1080.
    let weapon = crate::weapons::index_of("ak47_mp");
    let mut st = Mw2HudState {
        screen_w: 1920.0,
        screen_h: 1080.0,
        time_ms: 20_000,
        weapon,
        clip: 3,
        clip_size: 30,
        stock: 90,
        frags: 2,
        smokes: 3,
        yaw_deg: 200.0,
        xp_frac: 0.9,
        rank: 20,
        low_ammo_ok: 1,
        show_breath_hint: 1,
        ..Mw2HudState::default()
    };
    let _ = frame(&st); // first frame with a new weapon stamps the select time
    st.time_ms = 20_500; // 500 ms after the weapon change: the name is still fully shown
    let (cmds, strings, diag) = frame(&st);
    keep(diag);
    dump_frame("ak47 low ammo + breath hint 1920x1080", &cmds, &strings);
    let texts: Vec<&str> = cmds.iter().filter(|c| c.kind == 1).map(|c| text_of(&strings, c.material)).collect();
    eprintln!("texts: {texts:?}");
    assert!(texts.contains(&"Reload"), "low ammo warning");
    assert!(texts.iter().any(|t| t.contains("TESTKEY")), "breath hint with binding");
    assert!(texts.contains(&"AK-47"), "weapon name");
    assert!(texts.iter().any(|t| t.contains("TEST4")) && texts.iter().any(|t| t.contains("TEST3")), "d-pad key hints");
    preview::write(&dir.join("ak47_lowammo_1920x1080_render.png"), &cmds, &strings, 1920, 1080, false);
    st.time_ms = 25_000; // name faded out
    let (cmds, strings, _) = frame(&st);
    assert!(!cmds.iter().any(|c| c.kind == 1 && text_of(&strings, c.material) == "AK-47"), "weapon name fades");
    // Through the C ABI, same result as the Rust call.
    let mut buf = vec![Mw2HudCmd::default(); 512];
    let n = unsafe { mw2_hud_frame(&st, buf.as_mut_ptr(), 512) } as usize;
    assert_eq!(n, cmds.len());
    assert_eq!(digest(&buf[..n]), digest(&cmds));
    let mut s = [0u8; 64];
    let l = unsafe { mw2_hud_cmd_string(cmds[0].material, s.as_mut_ptr(), 64) } as usize;
    assert_eq!(&s[..l], strings[usize::from(cmds[0].material)].as_bytes());
    // Cost of a frame (expression evaluation included).
    let t0 = std::time::Instant::now();
    for i in 0..500 {
        st.time_ms = 30_000 + i * 16;
        let _ = frame(&st);
    }
    eprintln!("frame cost: {:.1} us", t0.elapsed().as_micros() as f64 / 500.0);
    // The compass passes as stored art, to see what each pass contributes.
    for m in ["hud_compass_alpha", "hud_compass_letters_step2", "hud_compass_letters_step3", "hud_compass_letters_shadow_step2", "hud_compass_letters_shadow_step3", "hud_weaponbar", "720_xpbar_empty", "720_xpbar", "720_xpbar_solid", "xpbar_solidfill", "xpbar_xpfill", "xpbar_restfill", "720_xpbar_fade", "720_xpbar_outline", "720_xpbar_outlineglow"] {
        preview::dump_texture(&dir, m);
    }
    eprintln!("{frames} frames; skipped-item reasons: {reasons:#?}");
}

/// The ride killstreaks' HUD menus (Predator, AC-130 per gun, Chopper Gunner) at 1920x1080:
/// previews in target/hud_preview/ride_*.png and the skipped-item reasons.
#[test]
#[ignore = "needs the MW2 install"]
fn ride_menus_preview() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        assert!(mw2_hud_init() > 40);
    }
    set_diagnostics(true);
    if let Ok(g) = DATA.read() {
        if let Some(d) = g.as_ref() {
            eprintln!("menus: {:?}", d.menus.iter().map(|m| (m.name.clone(), m.items.len())).collect::<Vec<_>>());
        }
    }
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_preview");
    std::fs::create_dir_all(&dir).unwrap();
    for (cmd, key) in [("+attack", "MOUSE1"), ("+activate", "E"), ("weapnext", "R")] {
        unsafe { mw2_hud_set_binding(cmd.as_ptr(), cmd.len(), key.as_ptr(), key.len()) };
    }
    for (kind, gun, label) in [(1u32, "remotemissile_projectile_mp", "predator"), (2, "ac130_105mm_mp", "ac130_105"), (2, "ac130_40mm_mp", "ac130_40"), (2, "ac130_25mm_mp", "ac130_25"), (3, "heli_remote_mp", "chopper")] {
        mw2_hud_set_ride(kind);
        let st = Mw2HudState { screen_w: 1920.0, screen_h: 1080.0, time_ms: 10_000, weapon: crate::weapons::index_of(gun), yaw_deg: 30.0, ..Mw2HudState::default() };
        let (cmds, strings, diag) = frame(&st);
        dump_frame(label, &cmds, &strings);
        eprintln!("{label} skipped: {diag:#?}");
        preview::write(&dir.join(format!("ride_{label}.png")), &cmds, &strings, 1920, 1080, false);
    }
    mw2_hud_set_ride(0);
}

/// Software renderer for the command list: real MW2 textures and font sheets over a grey checker,
/// plus an outline view of the rects.
pub(crate) mod preview {
    use super::*;

    struct Tex {
        w: usize,
        h: usize,
        /// Top row first.
        px: Vec<u8>,
    }

    fn load(name: &str) -> Option<Tex> {
        let (w, h, mut rgba) = crate::images::hud_rgba(name)?;
        let (w, h) = (w as usize, h as usize);
        // hud_rgba is bottom row first.
        for y in 0..h / 2 {
            let (a, b) = rgba.split_at_mut((h - 1 - y) * w * 4);
            a[y * w * 4..(y + 1) * w * 4].swap_with_slice(&mut b[..w * 4]);
        }
        Some(Tex { w, h, px: rgba })
    }

    fn sample(t: &Tex, s: f32, tt: f32) -> [f32; 4] {
        let x = ((s * t.w as f32).floor() as isize).clamp(0, t.w as isize - 1) as usize;
        let y = ((tt * t.h as f32).floor() as isize).clamp(0, t.h as isize - 1) as usize;
        let p = &t.px[(y * t.w + x) * 4..][..4];
        [f32::from(p[0]) / 255.0, f32::from(p[1]) / 255.0, f32::from(p[2]) / 255.0, f32::from(p[3]) / 255.0]
    }

    fn blend(img: &mut [f32], w: usize, x: usize, y: usize, rgba: [f32; 4]) {
        let o = (y * w + x) * 3;
        let a = rgba[3].clamp(0.0, 1.0);
        for k in 0..3 {
            img[o + k] = img[o + k] * (1.0 - a) + rgba[k] * a;
        }
    }

    fn bilerp(c: &[[f32; 2]; 4], u: f32, v: f32) -> [f32; 2] {
        let top = [c[0][0] + (c[1][0] - c[0][0]) * u, c[0][1] + (c[1][1] - c[0][1]) * u];
        let bot = [c[3][0] + (c[2][0] - c[3][0]) * u, c[3][1] + (c[2][1] - c[3][1]) * u];
        [top[0] + (bot[0] - top[0]) * v, top[1] + (bot[1] - top[1]) * v]
    }

    /// Colour over a checker (`<name>_color.png`) and the alpha channel as grey (`<name>_alpha.png`).
    pub fn dump_texture(dir: &std::path::Path, name: &str) {
        let Some(t) = load(name) else {
            eprintln!("texture {name}: not found");
            return;
        };
        eprintln!("texture {name}: {}x{}", t.w, t.h);
        let (mut color, mut alpha) = (Vec::with_capacity(t.w * t.h * 3), Vec::with_capacity(t.w * t.h * 3));
        for y in 0..t.h {
            for x in 0..t.w {
                let p = &t.px[(y * t.w + x) * 4..][..4];
                let bg = if ((x / 16) + (y / 16)) % 2 == 0 { 80.0 } else { 100.0 };
                let a = f32::from(p[3]) / 255.0;
                for k in 0..3 {
                    color.push((f32::from(p[k]) * a + bg * (1.0 - a)) as u8);
                    alpha.push(p[3]);
                }
            }
        }
        write_png(&dir.join(format!("{name}_color.png")), t.w, t.h, &color);
        write_png(&dir.join(format!("{name}_alpha.png")), t.w, t.h, &alpha);
    }

    pub fn write(path: &std::path::Path, cmds: &[Mw2HudCmd], strings: &[String], w: usize, h: usize, rects: bool) {
        let mut img = vec![0.0f32; w * h * 3];
        for y in 0..h {
            for x in 0..w {
                let v = if ((x / 32) + (y / 32)) % 2 == 0 { 0.30 } else { 0.36 };
                let o = (y * w + x) * 3;
                img[o..o + 3].copy_from_slice(&[v, v, v]);
            }
        }
        let mut cache: HashMap<String, Option<Tex>> = HashMap::new();
        for c in cmds {
            if rects {
                let col = match c.kind {
                    0 => [1.0, 0.8, 0.2, 1.0],
                    1 => [0.3, 1.0, 0.4, 1.0],
                    _ => [0.4, 0.7, 1.0, 1.0],
                };
                let (x0, y0) = (c.rect[0], c.rect[1]);
                // Text: the pen origin is the baseline; draw the measured width and em height above it.
                let (bw, bh) = if c.kind == 1 { (c.uv[0], -c.uv[1]) } else { (c.rect[2], c.rect[3]) };
                outline(&mut img, w, h, x0.min(x0 + bw), y0.min(y0 + bh), bw.abs(), bh.abs(), col);
                continue;
            }
            match c.kind {
                0 => {
                    let name = text_of(strings, c.material).to_owned();
                    let tex = cache.entry(name.clone()).or_insert_with(|| load(&name));
                    let corners = if c.rotation_deg != 0.0 {
                        let r = rotate_st(c.rotation_deg);
                        let (s0, t0, s1, t1) = (c.uv[0], c.uv[1], c.uv[2], c.uv[3]);
                        let m = |p: [f32; 2]| [s0 + p[0] * (s1 - s0), t0 + p[1] * (t1 - t0)];
                        [m(r[0]), m(r[1]), m(r[2]), m(r[3])]
                    } else {
                        [[c.uv[0], c.uv[1]], [c.uv[2], c.uv[1]], [c.uv[2], c.uv[3]], [c.uv[0], c.uv[3]]]
                    };
                    let (x0, y0) = (c.rect[0], c.rect[1]);
                    let (rw, rh) = (c.rect[2], c.rect[3]);
                    for y in (y0.floor().max(0.0) as usize)..((y0 + rh).ceil().min(h as f32) as usize) {
                        for x in (x0.floor().max(0.0) as usize)..((x0 + rw).ceil().min(w as f32) as usize) {
                            let (u, v) = ((x as f32 + 0.5 - x0) / rw, (y as f32 + 0.5 - y0) / rh);
                            let t = bilerp(&corners, u, v);
                            let texel = match tex {
                                Some(t_) => sample(t_, t[0], t[1]),
                                None => [1.0, 0.0, 1.0, 0.5],
                            };
                            let px = [texel[0] * c.color[0], texel[1] * c.color[1], texel[2] * c.color[2], texel[3] * c.color[3]];
                            if c.align == 3 {
                                let o = (y * w + x) * 3;
                                for k in 0..3 {
                                    img[o + k] = (img[o + k] + px[k] * px[3]).min(1.0);
                                }
                            } else {
                                blend(&mut img, w, x, y, px);
                            }
                        }
                    }
                }
                1 => {
                    let text = text_of(strings, c.material).to_owned();
                    let fname = font_name(u32::from(c.font));
                    let Some((sheet_name, glyphs)) = crate::fonts::with(fname, |f| (f.material.clone(), f.glyphs.clone())) else { continue };
                    let sheet = cache.entry(sheet_name.clone()).or_insert_with(|| load(&sheet_name));
                    let Some(sheet) = sheet.as_ref() else { continue };
                    // Sheets store glyphs as alpha, or as luminance with opaque alpha.
                    let use_alpha = sheet.px.chunks_exact(4).take(200_000).any(|p| p[3] < 250);
                    let (mut pen, base) = (c.rect[0], c.rect[1]);
                    let (xs, ys) = (c.rect[2], c.rect[3]);
                    let mut chars = text.chars().peekable();
                    while let Some(l) = next_letter(&mut chars) {
                        let Some(g) = glyphs.iter().find(|g| u32::from(g.letter) == l) else { continue };
                        let (gx, gy) = (pen + f32::from(g.x0) * xs, base + f32::from(g.y0) * ys);
                        let (gw, gh) = (f32::from(g.pixel_width) * xs, f32::from(g.pixel_height) * ys);
                        for y in (gy.floor().max(0.0) as usize)..((gy + gh).ceil().min(h as f32) as usize) {
                            for x in (gx.floor().max(0.0) as usize)..((gx + gw).ceil().min(w as f32) as usize) {
                                let (u, v) = ((x as f32 + 0.5 - gx) / gw, (y as f32 + 0.5 - gy) / gh);
                                let texel = sample(sheet, g.s0 + (g.s1 - g.s0) * u, g.t0 + (g.t1 - g.t0) * v);
                                let a = if use_alpha { texel[3] } else { texel[0] };
                                blend(&mut img, w, x, y, [c.color[0], c.color[1], c.color[2], a * c.color[3]]);
                            }
                        }
                        pen += f32::from(g.dx) * xs;
                    }
                }
                _ => {
                    for y in (c.rect[1].floor().max(0.0) as usize)..((c.rect[1] + c.rect[3]).ceil().min(h as f32) as usize) {
                        for x in (c.rect[0].floor().max(0.0) as usize)..((c.rect[0] + c.rect[2]).ceil().min(w as f32) as usize) {
                            blend(&mut img, w, x, y, c.color);
                        }
                    }
                }
            }
        }
        let bytes: Vec<u8> = img.iter().map(|v| (v.clamp(0.0, 1.0) * 255.0 + 0.5) as u8).collect();
        write_png(path, w, h, &bytes);
    }

    #[allow(clippy::too_many_arguments)]
    fn outline(img: &mut [f32], w: usize, h: usize, x: f32, y: f32, bw: f32, bh: f32, col: [f32; 4]) {
        let (x0, y0) = (x.max(0.0) as usize, y.max(0.0) as usize);
        let (x1, y1) = ((x + bw).min(w as f32 - 1.0) as usize, (y + bh).min(h as f32 - 1.0) as usize);
        for xx in x0..=x1.max(x0) {
            blend(img, w, xx.min(w - 1), y0.min(h - 1), col);
            blend(img, w, xx.min(w - 1), y1.min(h - 1), col);
        }
        for yy in y0..=y1.max(y0) {
            blend(img, w, x0.min(w - 1), yy.min(h - 1), col);
            blend(img, w, x1.min(w - 1), yy.min(h - 1), col);
        }
    }

    fn crc32(data: &[u8]) -> u32 {
        let mut crc = 0xFFFF_FFFFu32;
        for &b in data {
            crc ^= u32::from(b);
            for _ in 0..8 {
                crc = if crc & 1 != 0 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
            }
        }
        !crc
    }

    fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
        out.extend((data.len() as u32).to_be_bytes());
        let mut body = kind.to_vec();
        body.extend(data);
        out.extend(&body);
        out.extend(crc32(&body).to_be_bytes());
    }

    fn write_png(path: &std::path::Path, w: usize, h: usize, rgb: &[u8]) {
        let mut raw = Vec::with_capacity((w * 3 + 1) * h);
        for y in 0..h {
            raw.push(0);
            raw.extend(&rgb[y * w * 3..(y + 1) * w * 3]);
        }
        let mut out = vec![0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];
        let mut ihdr = Vec::new();
        ihdr.extend((w as u32).to_be_bytes());
        ihdr.extend((h as u32).to_be_bytes());
        ihdr.extend([8, 2, 0, 0, 0]);
        chunk(&mut out, b"IHDR", &ihdr);
        chunk(&mut out, b"IDAT", &miniz_oxide::deflate::compress_to_vec_zlib(&raw, 6));
        chunk(&mut out, b"IEND", &[]);
        std::fs::write(path, out).unwrap();
    }
}

/// XP bar layers in draw order with their materials' blend state (XP_FRAC=0.4).
#[test]
#[ignore = "needs the MW2 install"]
fn xpbar_layers() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    unsafe { mw2_hud_init() };
    let frac: f32 = std::env::var("XP_FRAC").ok().and_then(|v| v.parse().ok()).unwrap_or(0.4);
    let st = Mw2HudState { screen_w: 1920.0, screen_h: 1080.0, time_ms: 5000, weapon: 0, xp_frac: frac, rank: 15, ..Mw2HudState::default() };
    let (cmds, strings, _) = frame(&st);
    for c in &cmds {
        let name = strings.get(usize::from(c.material)).map_or("", String::as_str);
        if c.kind == 1 || !(name.contains("xp") || c.kind == 2) {
            continue;
        }
        let bits = crate::images::state_bits(name);
        eprintln!("k{} {:<24} rect ({:7.1},{:6.1} {:7.1}x{:5.1}) uv {:?} rgba {:?} blend {} bits {:x?}", c.kind, name, c.rect[0], c.rect[1], c.rect[2], c.rect[3], c.uv, c.color, c.align, bits);
    }
}

/// TEX=a,b: RGBA stats of HUD images (mean colour and alpha, share of opaque pixels).
#[test]
#[ignore = "needs the MW2 install"]
fn hud_image_stats() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    for n in std::env::var("TEX").unwrap_or("720_xpbar_empty,720_xpbar,720_xpbar_fade,xpbar_xpfill,720_xpbar_shadow".into()).split(',') {
        let Some((w, h, px)) = crate::images::hud_rgba(n) else { eprintln!("{n}: none"); continue };
        let k = (w * h) as f64;
        let mean = |c: usize| px.chunks_exact(4).map(|p| p[c] as f64).sum::<f64>() / k;
        let opaque = px.chunks_exact(4).filter(|p| p[3] > 200).count() as f64 / k;
        eprintln!("{n}: {w}x{h} mean rgb ({:.0},{:.0},{:.0}) alpha {:.0} opaque {:.0}% image {}", mean(0), mean(1), mean(2), mean(3), opaque * 100.0, crate::images::image_name(n));
    }
}

/// xpbar_hd items: their virtual rects / alignment and where they land on 21:9, 16:9 and 4:3.
#[test]
#[ignore = "needs the MW2 install"]
fn print_xpbar_layout() {
    const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let guard = DATA.read().unwrap();
    let d = guard.as_ref().unwrap();
    for m in d.menus.iter().filter(|m| m.name.contains("xpbar")) {
        eprintln!("MENU {} rect {:?}", m.name, m.rect);
        for it in &m.items {
            let mut line = format!("  '{}' bg '{}' rect {:?} horz {} vert {}", it.name, it.background, it.rect, it.horz, it.vert);
            for (w, h) in [(3440.0f32, 1440.0f32), (1920.0, 1080.0), (1280.0, 960.0)] {
                let pl = hud_iw4::ScreenPlacement::setup_fullscreen(w, h, 1.0);
                let a = pl.apply_rect(it.rect[0], it.rect[1], it.rect[2], it.rect[3], i32::from(it.horz), i32::from(it.vert));
                line += &format!(" | {w}x{h}: x {:.0}..{:.0}", a.x, a.x + a.w);
            }
            eprintln!("{line}");
        }
    }
}

/// MENU=remote_chopper_overlay_hd: every item's rect, material, visibility and float expressions.
#[test]
#[ignore = "needs the MW2 install"]
fn print_hud_menu_items() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        assert!(mw2_hud_init() > 40);
    }
    let want = std::env::var("MENU").unwrap_or_else(|_| "remote_chopper_overlay_hd".into());
    let g = DATA.read().unwrap();
    let d = g.as_ref().unwrap();
    for m in d.menus.iter().filter(|m| m.name == want) {
        for it in &m.items {
            eprintln!("item '{}' text {:?} bg {:?} ownerdraw {} rect {:?} align {}/{}", it.name, it.text, it.background, it.owner_draw, it.rect, it.horz, it.vert);
            if let Some(v) = &it.vis { eprintln!("    vis: {}", v.raw); }
            if let Some(v) = &it.text_exp { eprintln!("    text: {}", v.raw); }
            if let Some(v) = &it.material_exp { eprintln!("    material: {}", v.raw); }
            for (k, e) in &it.floats { eprintln!("    float {k}: {}", e.raw); }
        }
    }
}
