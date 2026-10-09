//! Event, death and C ABI checks against the real install (ignored by default).

use super::*;

fn finite(pose: &[f32]) -> bool {
    pose.iter().all(|v| v.is_finite())
}

/// Positive pitch looks down: the gun and head must drop, negative must lift them.
#[test]
#[ignore = "needs the user's MW2 install"]
fn real_install_aim_pitch_sign() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let ak = crate::weapons::index_of("ak47_mp");
    let mut heights = Vec::new();
    for pitch in [-60.0f32, 0.0, 60.0] {
        let mut ch = Character::build("us_army").unwrap();
        let mut pose = vec![0.0f32; ch.bone_count() * 7];
        for _ in 0..30 {
            ch.step(&Mw2CharacterInput { dt: 1.0 / 60.0, weapon: ak, aim_pitch: pitch, ..Default::default() }, &mut pose);
        }
        let gun = ch.weapon_tag().unwrap();
        // muzzle direction: the tag's +Z (Unity forward) axis
        let q = Quat::from_xyzw(gun[3], gun[4], gun[5], gun[6]);
        let fwd = q * Vec3::Z;
        eprintln!("  pitch {pitch:+.0}: weapon tag y {:.2} fwd {:.2}, tag +Z -> {fwd:?}", gun[1], gun[2]);
        heights.push(gun[1]);
        if pitch != 0.0 {
            let mut rgb = vec![40u8; 400 * 300 * 3];
            let mut panel = Panel { w: 400, h: 300, rgb: &mut rgb, depth: vec![f32::MAX; 400 * 300] };
            let g = world_gun(ak);
            render_frame(&ch, &pose, g.as_ref(), &mut panel, 235);
            write_png(&PathBuf::from(format!(r"D:\Coding\mw2-ror2\target\character_preview\usarmy_pitch{}.png", pitch as i32)), 400, 300, &rgb);
        }
    }
    assert!(heights[0] > heights[1] && heights[1] > heights[2], "gun height should fall as pitch rises: {heights:?}");
}

/// Every anim the script can ask for should be in the captured set (the filter is by name prefix).
#[test]
#[ignore = "needs the user's MW2 install"]
fn real_install_script_anims_resolve() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let store = ensure_store(None).unwrap();
    let names = store.script.anim_names();
    let mut missing: Vec<&str> = names.iter().copied().filter(|n| store.clip(n).is_none()).collect();
    missing.sort();
    eprintln!("script names {} | missing clips {}: {missing:?}", names.len(), missing.len());
    let mut prefixes: HashMap<String, usize> = HashMap::new();
    for n in &names {
        *prefixes.entry(n.split('_').next().unwrap_or("").to_owned()).or_default() += 1;
    }
    eprintln!("prefixes {prefixes:?}");
}

/// Step a one-shot event, then run on until it settles; report what the playeranim script chose.
#[test]
#[ignore = "needs the user's MW2 install"]
fn real_install_events_and_abi() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let ak = crate::weapons::index_of("ak47_mp");
    let frag = crate::weapons::index_of("frag_grenade_mp");
    let usp = crate::weapons::index_of("usp_mp");
    let dt = 1.0 / 60.0;
    // (label, event, stance, ground speed, weapon)
    let cases: &[(&str, u32, u8, f32, u32)] = &[
        ("reload standing", EV_RELOAD, 0, 0.0, ak),
        ("reload crouched", EV_RELOAD, 1, 0.0, ak),
        ("reload prone", EV_RELOAD, 2, 0.0, ak),
        ("reload pistol", EV_RELOAD, 0, 0.0, usp),
        ("reload moving", EV_RELOAD, 0, 190.0, ak),
        ("melee standing", EV_MELEE, 0, 0.0, ak),
        ("melee pistol", EV_MELEE, 0, 0.0, usp),
        ("fire moving (script: none)", EV_FIRE, 0, 190.0, ak),
        ("fire crouched", EV_FIRE, 1, 0.0, ak),
        ("fire pistol", EV_FIRE, 0, 0.0, usp),
        ("melee pistol (knife)", EV_MELEE, 0, 0.0, usp),
        ("melee pistol lunge", EV_MELEE | EV_MELEE_CHARGE, 0, 0.0, usp),
        ("melee rifle", EV_MELEE, 0, 0.0, ak),
        ("throw frag standing", EV_THROW, 0, 0.0, frag),
        ("throw frag moving", EV_THROW, 0, 190.0, frag),
        ("jump (standing)", EV_JUMP, 0, 0.0, ak),
        ("jump (running)", EV_JUMP, 0, 190.0, ak),
        ("pain", EV_PAIN, 0, 0.0, ak),
        ("mantle", EV_MANTLE, 0, 0.0, ak),
        ("mantle up 57 (trans 0)", EV_MANTLE | (1 << 8), 0, 0.0, ak),
        ("mantle up 21 (trans 6)", EV_MANTLE | (7 << 8), 0, 0.0, ak),
    ];
    let mut pose = Vec::new();
    for (label, ev, stance, speed, weapon) in cases {
        let mut ch = Character::build("us_army").unwrap();
        pose.resize(ch.bone_count() * 7, 0.0);
        let base = Mw2CharacterInput { dt, stance: *stance, move_fwd: *speed, weapon: *weapon, ..Default::default() };
        for _ in 0..60 {
            ch.step(&base, &mut pose);
        }
        ch.step(&Mw2CharacterInput { event: *ev, ..base }, &mut pose);
        let after = ch.debug_state();
        let log = ch.events_log().to_vec();
        let mut hand_travel = 0.0f32;
        let h0 = ch.weapon_tag().unwrap();
        for _ in 0..40 {
            ch.step(&base, &mut pose);
            let h = ch.weapon_tag().unwrap();
            hand_travel = hand_travel.max(((h[0] - h0[0]).powi(2) + (h[1] - h0[1]).powi(2) + (h[2] - h0[2]).powi(2)).sqrt());
        }
        assert!(finite(&pose));
        eprintln!("  {label:<28} -> {after} {log:?}; weapon hand moved up to {hand_travel:.2} m in 0.67 s");
    }
    // airborne: jump takeoff, hold the pose in the air, land
    let mut ch = Character::build("us_army").unwrap();
    for i in 0..120 {
        let air = (30..90).contains(&i);
        let ev = match i {
            30 => EV_JUMP,
            90 => EV_LAND,
            _ => 0,
        };
        ch.step(&Mw2CharacterInput { dt, in_air: u8::from(air), move_fwd: 190.0, weapon: ak, event: ev, ..Default::default() }, &mut pose);
        if i % 15 == 0 {
            eprintln!("  jump arc f{i:03} air={air} {}", ch.debug_state());
        }
    }
    // weapon switch: playeranim's DROPWEAPON pull-out (RIOTSHIELDNEXT when the shield comes next)
    let shield = crate::weapons::index_of("riotshield_mp");
    for (label, from, to) in [("ak -> usp", ak, usp), ("ak -> riot shield", ak, shield)] {
        let mut ch = Character::build("us_army").unwrap();
        for _ in 0..30 {
            ch.step(&Mw2CharacterInput { dt, weapon: from, ..Default::default() }, &mut pose);
        }
        ch.step(&Mw2CharacterInput { dt, weapon: to, ..Default::default() }, &mut pose);
        eprintln!("  switch {label}: {} {:?}", ch.debug_state(), ch.events_log());
        assert!(ch.events_log().iter().any(|e| e.starts_with("event 6")), "{label}: no DROPWEAPON (event 6)");
    }
    // deaths and pain with a real hit: explosion from behind, head shot from the left
    for (label, hit) in [("explosion from behind", 0x8000_0000u32 | 2 | (3 << 16)), ("head shot from the left", 0x8000_0000 | (1 << 8) | (1 << 16))] {
        let mut ch = Character::build("us_army").unwrap();
        for _ in 0..30 {
            ch.step(&Mw2CharacterInput { dt, weapon: ak, ..Default::default() }, &mut pose);
        }
        ch.step(&Mw2CharacterInput { dt, weapon: ak, event: EV_PAIN, hit, ..Default::default() }, &mut pose);
        eprintln!("  pain, {label}: {} {:?}", ch.debug_state(), ch.events_log());
        ch.step(&Mw2CharacterInput { dt, weapon: ak, dead: 1, hit, ..Default::default() }, &mut pose);
        eprintln!("  death, {label}: {} {:?}", ch.debug_state(), ch.events_log());
    }
    // death from stand / run / crouch / prone
    for (label, stance, speed) in [("stand", 0u8, 0.0f32), ("run", 0, 190.0), ("crouch", 1, 0.0), ("prone", 2, 0.0)] {
        let mut ch = Character::build("us_army").unwrap();
        for _ in 0..60 {
            ch.step(&Mw2CharacterInput { dt, stance, move_fwd: speed, weapon: ak, ..Default::default() }, &mut pose);
        }
        let h0 = bone_pos(&ch, &pose, "j_head");
        for _ in 0..180 {
            ch.step(&Mw2CharacterInput { dt, stance, move_fwd: 0.0, dead: 1, weapon: ak, ..Default::default() }, &mut pose);
        }
        let h1 = bone_pos(&ch, &pose, "j_head");
        assert!(finite(&pose));
        eprintln!("  death from {label}: head {:.2} -> {:.2} m high after 3 s | {}", h0[1], h1[1], ch.debug_state());
        if stance == 0 && speed == 0.0 {
            let mut rgb = vec![40u8; 400 * 300 * 3];
            let mut panel = Panel { w: 400, h: 300, rgb: &mut rgb, depth: vec![f32::MAX; 400 * 300] };
            render_frame(&ch, &pose, None, &mut panel, 235);
            write_png(&PathBuf::from(r"D:\Coding\mw2-ror2\target\character_preview\usarmy_death_stand.png"), 400, 300, &rgb);
        }
    }

    // ---- through the C ABI, exactly as the plugin would ----
    let name = b"us_army";
    let c = unsafe { mw2_character_build(name.as_ptr(), name.len()) };
    assert!(!c.is_null());
    let mut info = Mw2CharacterInfo::default();
    assert_eq!(unsafe { mw2_character_info(c, &mut info) }, 1);
    let (n, b) = (info.vertex_count as usize, info.bone_count as usize);
    let mut pos = vec![0f32; n * 3];
    let mut nor = vec![0f32; n * 3];
    let mut uv = vec![0f32; n * 2];
    let mut bi = vec![0i32; n * 4];
    let mut bw = vec![0f32; n * 4];
    let mut idx = vec![0u32; info.index_count as usize];
    let mut bind = vec![0f32; b * 16];
    assert_eq!(
        unsafe { mw2_character_mesh(c, pos.as_mut_ptr(), nor.as_mut_ptr(), uv.as_mut_ptr(), bi.as_mut_ptr(), bw.as_mut_ptr(), idx.as_mut_ptr(), bind.as_mut_ptr()) },
        1
    );
    assert!(bi.iter().all(|&i| (i as usize) < b));
    assert!(idx.iter().all(|&i| (i as usize) < n));
    assert!(bw.chunks(4).all(|w| (w.iter().sum::<f32>() - 1.0).abs() < 0.01), "skin weights sum to 1");
    let (mut min, mut max) = ([f32::MAX; 3], [f32::MIN; 3]);
    for p in pos.chunks(3) {
        for a in 0..3 {
            min[a] = min[a].min(p[a]);
            max[a] = max[a].max(p[a]);
        }
    }
    eprintln!("  ABI mesh bounds (m, Unity space, bind pose): x {:.2}..{:.2} y {:.2}..{:.2} z {:.2}..{:.2}", min[0], max[0], min[1], max[1], min[2], max[2]);
    let mut textured = 0;
    for s in 0..info.surface_count {
        let mut si = Mw2SurfaceInfo::default();
        assert_eq!(unsafe { mw2_character_surface(c, s, &mut si) }, 1);
        assert!(si.index_start + si.index_count <= info.index_count);
        let (mut w, mut h) = (0u32, 0u32);
        let need = unsafe { mw2_character_surface_rgba(c, s, &mut w, &mut h, std::ptr::null_mut(), 0) };
        if need > 0 {
            let mut px = vec![0u8; need as usize];
            assert_eq!(unsafe { mw2_character_surface_rgba(c, s, &mut w, &mut h, px.as_mut_ptr(), need) }, need);
            textured += 1;
        }
        let mut nm = [0u8; 64];
        let mode = unsafe { mw2_character_surface_material(c, s, nm.as_mut_ptr(), 64) };
        let len = nm.iter().position(|&x| x == 0).unwrap();
        eprintln!("    surface {s}: {} tris, {w}x{h}, blend mode {mode}, material {}", si.index_count / 3, String::from_utf8_lossy(&nm[..len]));
    }
    assert!(textured > 10);
    let mut out = vec![0f32; b * 7];
    let input = Mw2CharacterInput { dt, move_fwd: 190.0, weapon: ak, aim_pitch: 20.0, ..Default::default() };
    let t = std::time::Instant::now();
    for _ in 0..1000 {
        assert_eq!(unsafe { mw2_character_step(c, &input, out.as_mut_ptr()) }, 1);
    }
    eprintln!("  mw2_character_step: {:.1} us per step (run, with pitch), {} bones", t.elapsed().as_secs_f64() * 1e6 / 1000.0, b);
    let mut tag = [0f32; 7];
    assert_eq!(unsafe { mw2_character_weapon_tag(c, tag.as_mut_ptr()) }, 1);
    eprintln!("  weapon tag {tag:?}");
    let jh = b"j_head";
    let i = unsafe { mw2_character_find_bone(c, jh.as_ptr(), jh.len()) };
    assert!(i > 0);
    let mut nm = [0u8; 32];
    assert_eq!(unsafe { mw2_character_bone_name(c, i as u32, nm.as_mut_ptr(), 32) }, 6);
    let mut parents = vec![0i32; b];
    assert_eq!(unsafe { mw2_character_bone_parents(c, parents.as_mut_ptr(), b as u32) }, b as i32);
    assert_eq!(parents[0], -1);
    let mut d = [0u8; 256];
    let n = unsafe { mw2_character_describe(c, d.as_mut_ptr(), 256) };
    eprintln!("  describe: {}", String::from_utf8_lossy(&d[..n as usize]));
    // world gun
    let mut wm = [0u8; 64];
    let l = unsafe { mw2_weapon_world_model(ak, wm.as_mut_ptr(), 64) };
    assert!(l > 0);
    let mut gi = Mw2CharacterInfo::default();
    assert_eq!(unsafe { mw2_character_weapon_info(ak, &mut gi) }, 1);
    let mut gp = vec![0f32; gi.vertex_count as usize * 3];
    let mut gn = vec![0f32; gi.vertex_count as usize * 3];
    let mut gu = vec![0f32; gi.vertex_count as usize * 2];
    let mut gx = vec![0u32; gi.index_count as usize];
    assert_eq!(unsafe { mw2_character_weapon_mesh(ak, gp.as_mut_ptr(), gn.as_mut_ptr(), gu.as_mut_ptr(), gx.as_mut_ptr()) }, 1);
    let mut gs = Mw2SurfaceInfo::default();
    assert_eq!(unsafe { mw2_character_weapon_surface(ak, 0, &mut gs) }, 1);
    eprintln!("  world gun {}: {} verts {} surfaces, surface0 {}x{}", String::from_utf8_lossy(&wm[..l as usize]), gi.vertex_count, gi.surface_count, gs.texture_width, gs.texture_height);
    // bad input never panics
    assert!(unsafe { mw2_character_build(std::ptr::null(), 0) }.is_null());
    let bogus = b"not_a_faction";
    assert!(unsafe { mw2_character_build(bogus.as_ptr(), bogus.len()) }.is_null());
    assert_eq!(unsafe { mw2_character_step(std::ptr::null_mut(), &input, out.as_mut_ptr()) }, 0);
    unsafe { mw2_character_destroy(c) };
}

/// Third-person reloads last the weapon's real reload (RPD: its reload / reloadEmpty, halved by
/// Sleight of Hand): the torso anim stretches to it (playtest 10-04-26: the soldier looked done while
/// the RPD couldn't fire yet).
#[test]
#[ignore = "needs the user's MW2 install"]
fn real_install_reload_anim_matches_reload_time() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let rpd = crate::weapons::index_of("rpd_mp");
    let t = crate::weapons::row(rpd).unwrap().timers;
    for (flags, want) in [(0, t.reload_ms), (EV_RELOAD_EMPTY, t.reload_empty_ms), (EV_RELOAD_FAST, t.reload_ms / 2)] {
        let mut ch = Character::build("us_army").unwrap();
        let mut pose = vec![0.0f32; ch.bone_count() * 7];
        let base = Mw2CharacterInput { dt: 1.0 / 60.0, weapon: rpd, ..Default::default() };
        for _ in 0..20 {
            ch.step(&base, &mut pose);
        }
        ch.step(&Mw2CharacterInput { event: EV_RELOAD | flags, ..base }, &mut pose);
        let log = ch.events_log().join(" | ");
        eprintln!("rpd reload flags {flags:#x}: {log}");
        assert!(log.contains(&format!("reload over {want} ms")), "{log}");
        let ti = ch.anim.bound_torso;
        let l = ch.anim.layers.iter().find(|l| l.id == ti).expect("torso layer");
        let clip_ms = l.clip.numframes as f32 / l.clip.framerate * 1000.0;
        eprintln!("  torso {} clip {clip_ms:.0} ms rate {:.2} -> {:.0} ms", ch.anim.name(ti), l.rate, clip_ms / l.rate);
        assert!((clip_ms / l.rate - want as f32).abs() < 50.0);
    }
}
