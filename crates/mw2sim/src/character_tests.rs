//! Character tests. The real-install one is `#[ignore]`d: run it with
//! `cargo test --release -p mw2sim character -- --ignored --nocapture`.

use super::*;
use std::collections::HashMap;

const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

const MINI_SCRIPT: &str = r#"
DEFINES
set movetype moving = walk AND run
ANIMATIONS
STATE COMBAT
{
	idle
	{
		playerAnimType pistol
		{
			both pb_idle_pistol
		}
		default
		{
			both pb_idle
		}
	}
	idlecr
	{
		default
		{
			both pb_idle_crouch
		}
	}
	run
	{
		strafing left
		{
			both pb_run_left
		}
		default
		{
			both pb_run
		}
	}
	sprint
	{
		default
		{
			both pb_sprint
		}
	}
}
EVENTS
reload
{
	default
	{
		torso pt_reload duration 1000
	}
}
"#;

/// A store with no clips: enough to check which anims MW2's script picks for an input.
fn script_only_animator() -> (Animator, DObj) {
    let script = data::parse_player_anim_script(MINI_SCRIPT.as_bytes(), &|_| true).unwrap();
    let store = Arc::new(Store {
        common: data::CommonCharData::default(),
        script,
        anims: HashMap::new(),
        clips: Mutex::new(HashMap::new()),
        props: Mutex::new(HashMap::new()),
        zone_dir: PathBuf::new(),
        rigs: Mutex::new(HashMap::new()),
        meshes: Mutex::new(HashMap::new()),
        missing: Mutex::new(HashMap::new()),
    });
    let pose = xmodel_runtime::ModelPoseSrc {
        name: "t".into(),
        num_bones: 1,
        num_root_bones: 1,
        scale: 1.0,
        no_scale_part_bits: [0; 6],
        bone_names: vec!["tag_origin".into()],
        parent_list: Vec::new(),
        quats: Vec::new(),
        trans: Vec::new(),
        base_mat: vec![(Quat::IDENTITY, Vec3::ZERO)],
    };
    (Animator::new(store), DObj::build(&[(&pose, None)]).unwrap())
}

#[test]
fn script_picks_by_movetype_strafing_and_blocks_during_events() {
    let (mut a, d) = script_only_animator();
    let base = Mw2CharacterInput { dt: 1.0 / 60.0, ..Default::default() };
    a.update(&d, &base);
    assert_eq!(a.legs_name(), "pb_idle");
    // run forward, then strafe left (pure sideways -> `strafing left`)
    a.update(&d, &Mw2CharacterInput { move_fwd: 190.0, ..base });
    assert_eq!(a.legs_name(), "pb_run");
    a.update(&d, &Mw2CharacterInput { move_right: -190.0, ..base });
    assert_eq!(a.legs_name(), "pb_run_left");
    // sprint needs the sprint flag, standing, not backwards
    a.update(&d, &Mw2CharacterInput { move_fwd: 285.0, sprinting: 1, ..base });
    assert_eq!(a.legs_name(), "pb_sprint");
    a.update(&d, &Mw2CharacterInput { move_fwd: -190.0, sprinting: 1, ..base });
    assert_eq!(a.legs_name(), "pb_idle", "no runbk entry in this script: falls back to idle's list");
    // crouch idle
    a.update(&d, &Mw2CharacterInput { stance: 1, ..base });
    assert_eq!(a.legs_name(), "pb_idle_crouch");
    // reload puts a torso anim up and holds it for its duration (+50 ms)
    a.update(&d, &Mw2CharacterInput { event: EV_RELOAD, ..base });
    assert_eq!(a.torso_name(), "pt_reload");
    assert!((a.torso_timer - 1050).abs() <= 20, "{}", a.torso_timer);
    for _ in 0..30 {
        a.update(&d, &base);
    }
    assert_eq!(a.torso_name(), "pt_reload", "movement does not clear a running torso anim");
    for _ in 0..60 {
        a.update(&d, &base);
    }
    assert_eq!(a.torso_name(), "-", "idle's `both` entry clears the torso once its timer ran out");
}

#[test]
fn movement_choice_matches_iw4_table() {
    let (a, _) = script_only_animator();
    let base = Mw2CharacterInput { dt: 1.0 / 60.0, ..Default::default() };
    let mt = |i: Mw2CharacterInput| a.movetype_for(&i).unwrap();
    assert_eq!(mt(base), 1); // idle
    assert_eq!(mt(Mw2CharacterInput { stance: 1, ..base }), 2); // idlecr
    assert_eq!(mt(Mw2CharacterInput { stance: 2, ..base }), 3); // idleprone
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 190.0, ..base }), 10); // run
    assert_eq!(mt(Mw2CharacterInput { move_fwd: -190.0, ..base }), 11); // runbk
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 100.0, ads_frac: 1.0, ..base }), 4); // walk
    assert_eq!(mt(Mw2CharacterInput { move_fwd: -100.0, ads_frac: 1.0, ..base }), 5); // walkbk
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 110.0, stance: 1, ..base }), 12); // runcr
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 110.0, stance: 1, ads_frac: 1.0, ..base }), 6); // walkcr
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 40.0, stance: 2, ..base }), 8); // walkprone
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 285.0, sprinting: 1, ..base }), 20); // sprint
    assert_eq!(mt(Mw2CharacterInput { move_fwd: 285.0, sprinting: 1, stance: 1, ..base }), 12); // no sprint crouched
    assert!(a.movetype_for(&Mw2CharacterInput { in_air: 1, ..base }).is_none());
    assert_eq!(std::mem::size_of::<Mw2CharacterInput>(), 40);
}

fn bone_pos(ch: &Character, pose: &[f32], name: &str) -> [f32; 3] {
    let i = ch.find_bone(name).unwrap_or_else(|| panic!("no bone {name}"));
    [pose[i * 7], pose[i * 7 + 1], pose[i * 7 + 2]]
}

// ---- tiny software rasteriser + PNG writer for the previews ----

fn crc32(data: &[u8]) -> u32 {
    let mut table = [0u32; 256];
    for (n, t) in table.iter_mut().enumerate() {
        let mut c = n as u32;
        for _ in 0..8 {
            c = if c & 1 != 0 { 0xedb8_8320 ^ (c >> 1) } else { c >> 1 };
        }
        *t = c;
    }
    let mut c = 0xffff_ffffu32;
    for &b in data {
        c = table[((c ^ u32::from(b)) & 0xff) as usize] ^ (c >> 8);
    }
    c ^ 0xffff_ffff
}

fn write_png(path: &std::path::Path, w: usize, h: usize, rgb: &[u8]) {
    let mut raw = Vec::with_capacity((w * 3 + 1) * h);
    for y in 0..h {
        raw.push(0);
        raw.extend_from_slice(&rgb[y * w * 3..(y + 1) * w * 3]);
    }
    let z = miniz_oxide::deflate::compress_to_vec_zlib(&raw, 6);
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a];
    let mut chunk = |kind: &[u8; 4], body: &[u8]| {
        out.extend_from_slice(&(body.len() as u32).to_be_bytes());
        let mut c = kind.to_vec();
        c.extend_from_slice(body);
        out.extend_from_slice(&c);
        out.extend_from_slice(&crc32(&c).to_be_bytes());
    };
    let mut ihdr = Vec::new();
    ihdr.extend_from_slice(&(w as u32).to_be_bytes());
    ihdr.extend_from_slice(&(h as u32).to_be_bytes());
    ihdr.extend_from_slice(&[8, 2, 0, 0, 0]);
    chunk(b"IHDR", &ihdr);
    chunk(b"IDAT", &z);
    chunk(b"IEND", &[]);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(path, out).unwrap();
}

fn avg_colour(s: &Surface) -> [f32; 3] {
    static CACHE: Mutex<Option<HashMap<String, [f32; 3]>>> = Mutex::new(None);
    let key = s.color_map.clone().unwrap_or_default();
    if let Some(c) = CACHE.lock().unwrap().as_ref().and_then(|m| m.get(&key)) {
        return *c;
    }
    let c = avg_colour_uncached(s);
    CACHE.lock().unwrap().get_or_insert_with(HashMap::new).insert(key, c);
    c
}

fn avg_colour_uncached(s: &Surface) -> [f32; 3] {
    let Some(rgba) = s.color_map.as_deref().and_then(crate::fx::texture_rgba) else { return [0.6, 0.6, 0.6] };
    let (_, _, px) = &*rgba;
    let (mut r, mut g, mut b, mut n) = (0.0, 0.0, 0.0, 0.0);
    for p in px.chunks_exact(4).step_by(7) {
        if p[3] > 128 {
            r += f32::from(p[0]);
            g += f32::from(p[1]);
            b += f32::from(p[2]);
            n += 1.0;
        }
    }
    if n == 0.0 { [0.6, 0.6, 0.6] } else { [r / n / 255.0, g / n / 255.0, b / n / 255.0] }
}

const PX_PER_M: f32 = 150.0;

struct Panel<'a> {
    w: usize,
    h: usize,
    rgb: &'a mut [u8],
    depth: Vec<f32>,
}

/// Side view: screen x = Unity Z (forward to the right), screen y = Unity Y; depth along -X.
fn draw_tris(p: &mut Panel<'_>, ox: usize, verts: &[[f32; 3]], indices: &[u32], colour: [f32; 3], px_per_m: f32) {
    let (w, h) = (p.w, p.h);
    let project = |v: [f32; 3]| (ox as f32 + v[2] * px_per_m, h as f32 - 12.0 - v[1] * px_per_m, -v[0]);
    for t in indices.chunks_exact(3) {
        let (a, b, c) = (verts[t[0] as usize], verts[t[1] as usize], verts[t[2] as usize]);
        let n = (Vec3::from_array(b) - Vec3::from_array(a)).cross(Vec3::from_array(c) - Vec3::from_array(a)).normalize_or_zero();
        let lit = 0.35 + 0.65 * n.dot(Vec3::new(-0.3, 0.6, 0.5).normalize()).abs();
        let col = [(colour[0] * lit * 255.0) as u8, (colour[1] * lit * 255.0) as u8, (colour[2] * lit * 255.0) as u8];
        let (pa, pb, pc) = (project(a), project(b), project(c));
        let minx = pa.0.min(pb.0).min(pc.0).floor().max(0.0) as i32;
        let maxx = pa.0.max(pb.0).max(pc.0).ceil().min(w as f32 - 1.0) as i32;
        let miny = pa.1.min(pb.1).min(pc.1).floor().max(0.0) as i32;
        let maxy = pa.1.max(pb.1).max(pc.1).ceil().min(h as f32 - 1.0) as i32;
        let den = (pb.1 - pc.1) * (pa.0 - pc.0) + (pc.0 - pb.0) * (pa.1 - pc.1);
        if den.abs() < 1e-6 {
            continue;
        }
        for y in miny..=maxy {
            for x in minx..=maxx {
                let (fx, fy) = (x as f32 + 0.5, y as f32 + 0.5);
                let l1 = ((pb.1 - pc.1) * (fx - pc.0) + (pc.0 - pb.0) * (fy - pc.1)) / den;
                let l2 = ((pc.1 - pa.1) * (fx - pc.0) + (pa.0 - pc.0) * (fy - pc.1)) / den;
                let l3 = 1.0 - l1 - l2;
                if l1 < 0.0 || l2 < 0.0 || l3 < 0.0 {
                    continue;
                }
                let z = l1 * pa.2 + l2 * pb.2 + l3 * pc.2;
                let di = y as usize * w + x as usize;
                if z < p.depth[di] {
                    p.depth[di] = z;
                    p.rgb[di * 3..di * 3 + 3].copy_from_slice(&col);
                }
            }
        }
    }
}

fn render_frame(ch: &Character, pose: &[f32], gun: Option<&WorldGun>, panel: &mut Panel<'_>, ox: usize) {
    let skinned = ch.skin_cpu(pose);
    for s in &ch.surfaces {
        let idx = &ch.indices[s.index_start as usize..(s.index_start + s.index_count) as usize];
        draw_tris(panel, ox, &skinned, idx, avg_colour(s), PX_PER_M);
    }
    if let (Some(g), Some(t)) = (gun, ch.weapon_tag()) {
        let m = Mat4::from_rotation_translation(Quat::from_xyzw(t[3], t[4], t[5], t[6]), Vec3::new(t[0], t[1], t[2]));
        let verts: Vec<[f32; 3]> = g.positions.iter().map(|p| m.transform_point3(Vec3::from_array(*p)).to_array()).collect();
        for s in &g.surfaces {
            let idx = &g.indices[s.index_start as usize..(s.index_start + s.index_count) as usize];
            draw_tris(panel, ox, &verts, idx, avg_colour(s), PX_PER_M);
        }
    }
}

#[derive(Clone, Copy)]
struct Scenario {
    name: &'static str,
    stance: u8,
    sprint: bool,
    fwd: f32,
    right: f32,
    ads: f32,
    fire: bool,
}

const fn sc(name: &'static str, stance: u8, sprint: bool, fwd: f32, right: f32, ads: f32, fire: bool) -> Scenario {
    Scenario { name, stance, sprint, fwd, right, ads, fire }
}

const SCENARIOS: &[Scenario] = &[
    sc("idle", 0, false, 0.0, 0.0, 0.0, false),
    sc("run", 0, false, 190.0, 0.0, 0.0, false),
    sc("sprint", 0, true, 285.0, 0.0, 0.0, false),
    sc("crouchwalk", 1, false, 110.0, 0.0, 0.0, false),
    sc("strafe_right", 0, false, 0.0, 190.0, 0.0, false),
    sc("run_diag_right", 0, false, 134.0, 134.0, 0.0, false),
    sc("run_back", 0, false, -150.0, 0.0, 0.0, false),
    sc("run_back_right", 0, false, -106.0, 106.0, 0.0, false),
    sc("ads", 0, false, 0.0, 0.0, 1.0, false),
    sc("fire", 0, false, 0.0, 0.0, 0.0, true),
    sc("prone", 2, false, 0.0, 0.0, 0.0, false),
];

fn run_scenario(ch: &mut Character, sc: Scenario, weapon: u32, gun: Option<&WorldGun>, tag: &str) {
    let mut pose = vec![0.0f32; ch.bone_count() * 7];
    let dt = 1.0 / 60.0;
    // settle into the stance first so the transition does not pollute the 1 s window
    let settle = Mw2CharacterInput { dt, stance: sc.stance, weapon, ..Default::default() };
    for _ in 0..90 {
        ch.step(&settle, &mut pose);
    }
    let mut head = Vec::new();
    let (mut lfoot, mut rfoot, mut hand) = (Vec::new(), Vec::new(), Vec::new());
    let mut log = String::new();
    let mut frames: Vec<Vec<f32>> = Vec::new();
    for i in 0..60 {
        let inp = Mw2CharacterInput {
            dt,
            stance: sc.stance,
            sprinting: u8::from(sc.sprint),
            move_fwd: sc.fwd,
            move_right: sc.right,
            ads_frac: sc.ads,
            weapon,
            event: if sc.fire && i % 6 == 0 { EV_FIRE } else { 0 },
            ..Default::default()
        };
        ch.step(&inp, &mut pose);
        head.push(bone_pos(ch, &pose, "j_head"));
        lfoot.push(bone_pos(ch, &pose, "j_ankle_le"));
        rfoot.push(bone_pos(ch, &pose, "j_ankle_ri"));
        hand.push(ch.weapon_tag().map_or([0.0; 3], |t| [t[0], t[1], t[2]]));
        if i % 20 == 0 || !ch.events_log().is_empty() {
            log.push_str(&format!("      t={:.2} {} {:?}\n", i as f32 * dt, ch.debug_state(), ch.events_log()));
        }
        if i == 0 || i == 20 || i == 40 {
            frames.push(pose.clone());
        }
    }
    let range = |v: &[[f32; 3]], axis: usize| v.iter().fold((f32::MAX, f32::MIN), |(lo, hi), p| (lo.min(p[axis]), hi.max(p[axis])));
    let (hy0, hy1) = range(&head, 1);
    let (lz0, lz1) = range(&lfoot, 2);
    let (rz0, rz1) = range(&rfoot, 2);
    let (ly0, ly1) = range(&lfoot, 1);
    let (ry0, ry1) = range(&rfoot, 1);
    let (hz0, hz1) = range(&hand, 2);
    let (hh0, hh1) = range(&hand, 1);
    eprintln!(
        "    [{}] head y {:.2}..{:.2} | L foot y {:.2}..{:.2} fwd {:+.2}..{:+.2} | R foot y {:.2}..{:.2} fwd {:+.2}..{:+.2} | weapon tag y {:.2}..{:.2} fwd {:+.2}..{:+.2}",
        sc.name, hy0, hy1, ly0, ly1, lz0, lz1, ry0, ry1, rz0, rz1, hh0, hh1, hz0, hz1
    );
    eprint!("{log}");
    // contact sheet of three frames
    let (pw, ph) = (400usize, 300usize);
    let mut rgb = vec![40u8; pw * 3 * ph * 3];
    let mut panel = Panel { w: pw * 3, h: ph, rgb: &mut rgb, depth: vec![f32::MAX; pw * 3 * ph] };
    for x in 0..pw * 3 {
        let y = ph - 12;
        panel.rgb[(y * pw * 3 + x) * 3..(y * pw * 3 + x) * 3 + 3].copy_from_slice(&[90, 110, 90]);
    }
    for (k, p) in frames.iter().enumerate() {
        ch.last_pose = p.clone();
        render_frame(ch, p, gun, &mut panel, k * pw + 235);
    }
    let path = PathBuf::from(r"D:\Coding\mw2-ror2\target\character_preview").join(format!("{tag}_{}.png", sc.name));
    write_png(&path, pw * 3, ph, &rgb);
}

#[test]
#[ignore = "needs the user's MW2 install"]
fn real_install_characters() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let t = std::time::Instant::now();
    let ak = crate::weapons::index_of("ak47_mp");
    let pistol = crate::weapons::index_of("usp_mp");
    assert!(ak != 0 && pistol != 0);
    eprintln!("world model of ak47_mp = {:?}, usp_mp = {:?}", weapon_world_model(ak), weapon_world_model(pistol));
    {
        let store = ensure_store(None).expect("store");
        eprintln!(
            "store ready in {:.2?}: {} player xanims, script {} movement items / {} event items ({} skipped, {} unresolved anims)",
            t.elapsed(),
            store.anims.len(),
            store.script.item_count,
            store.script.event_item_count,
            store.script.skipped_items,
            store.script.unresolved_anims
        );
    }
    for faction in ["us_army", "socom_141", "opforce_composite", "seals_udt", "militia"] {
        let t = std::time::Instant::now();
        let ch = match Character::build(faction) {
            Ok(c) => c,
            Err(e) => {
                eprintln!("{faction}: BUILD FAILED: {e}");
                assert!(faction != "us_army" && faction != "socom_141", "{e}");
                continue;
            }
        };
        eprintln!(
            "{faction}: built in {:.2?} -> faction {} | mptype {} | body {} | head {:?} | {} bones, {} verts, {} tris, {} surfaces (textured {})",
            t.elapsed(),
            ch.resolved_faction,
            ch.spec.mptype,
            ch.body_name,
            ch.head_name,
            ch.bone_count(),
            ch.positions.len(),
            ch.indices.len() / 3,
            ch.surfaces.len(),
            ch.surfaces.iter().filter(|s| s.color_map.is_some()).count()
        );
        eprintln!("  viewhands {:?}, heads pool {:?}", ch.spec.viewhands, ch.spec.heads);
        let tags: Vec<&str> = ch.bone_names.iter().map(String::as_str).filter(|n| n.starts_with("tag_")).collect();
        eprintln!("  tags: {tags:?}");
        assert!(ch.weapon_tag().is_some(), "tag_weapon_right");
        let gun = world_gun(ak);
        if let Some(g) = &gun {
            eprintln!("  ak47 world gun {}: {} verts, {} tris, {} surfaces", g.model, g.positions.len(), g.indices.len() / 3, g.surfaces.len());
        }
        let tag = faction.replace('_', "");
        for s in SCENARIOS {
            let mut fresh = Character::build_with(faction, "assault", 0, 0).unwrap();
            run_scenario(&mut fresh, *s, ak, gun.as_ref(), &tag);
        }
        let mut fresh = Character::build_with(faction, "assault", 0, 0).unwrap();
        let pg = world_gun(pistol);
        run_scenario(&mut fresh, sc("pistol_idle", 0, false, 0.0, 0.0, 0.0, false), pistol, pg.as_ref(), &tag);
    }
}

#[test]
#[ignore = "needs the user's MW2 install"]
fn debug_run_cycle() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let ak = crate::weapons::index_of("ak47_mp");
    let mut ch = Character::build("us_army").unwrap();
    let names: Vec<&str> = ch.bone_names.iter().map(String::as_str).collect();
    eprintln!("bones: {names:?}");
    let store = ensure_store(None).unwrap();
    for n in ["pb_combatrun_forward_loop", "pb_sprint", "pb_stand_alert", "pb_combatrun_right_loop"] {
        let c = store.clip(n).unwrap();
        eprintln!("{n}: dur {:.3}s fps {} frames {} looping {} move_speed {:.1} in/s tracks {}", c.duration(), c.framerate, c.numframes, c.looping, c.move_speed(), c.tracks.len());
    }
    let mut pose = vec![0.0f32; ch.bone_count() * 7];
    for i in 0..90 {
        let inp = Mw2CharacterInput { dt: 1.0 / 60.0, move_fwd: 190.0, weapon: ak, ..Default::default() };
        ch.step(&inp, &mut pose);
        if i % 3 == 0 {
            let l = bone_pos(&ch, &pose, "j_ankle_le");
            let r = bone_pos(&ch, &pose, "j_ankle_ri");
            let layers: Vec<String> = ch.anim.layers.iter().map(|l| format!("{}:{:.2}@{:.2}x{:.2}", ch.anim.name(l.id), l.weight, l.time, l.rate)).collect();
            eprintln!("f{i:02} L y {:.2} z {:+.2} | R y {:.2} z {:+.2} | {layers:?}", l[1], l[2], r[1], r[2]);
        }
    }
}

/// Akimbo third person: the akimbo script items play, the pose stays finite, and
/// tag_weapon_left exists for the second gun.
#[test]
#[ignore = "needs the user's MW2 install"]
fn akimbo_third_person() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    for w in ["usp_akimbo_mp", "mp5k_akimbo_mp", "usp_mp"] {
        let weapon = crate::weapons::index_of(w);
        let mut ch = Character::build("us_army").unwrap();
        let mut pose = vec![0.0f32; ch.bone_count() * 7];
        for i in 0..120 {
            let inp = Mw2CharacterInput { dt: 1.0 / 60.0, weapon, move_fwd: if i > 60 { 190.0 } else { 0.0 }, event: if i == 40 { EV_FIRE } else { 0 }, ..Default::default() };
            ch.step(&inp, &mut pose);
            let bad = pose.iter().filter(|v| !v.is_finite()).count();
            if i % 20 == 0 || bad > 0 {
                let layers: Vec<String> = ch.anim.layers.iter().map(|l| format!("{}:{:.2}", ch.anim.name(l.id), l.weight)).collect();
                eprintln!("{w} f{i:03} nonfinite {bad} head {:?} | {layers:?}", bone_pos(&ch, &pose, "j_head"));
            }
        }
        eprintln!("{w}: tag_weapon_left {:?}", ch.tag("tag_weapon_left"));
    }
    let ch = Character::build("us_army").unwrap();
    eprintln!("tag_shield_back {:?}", ch.tag("tag_shield_back"));
    let mut names: Vec<String> = crate::sounds::BANKS.read().unwrap().iter().flat_map(|b| b.aliases.iter().map(|a| a.list.clone())).filter(|n| { let l = n.to_ascii_lowercase(); l.contains("riot") || l.contains("shield") || l.starts_with("melee") }).collect();
    names.sort();
    names.dedup();
    eprintln!("ALIASES {names:?}");
    for w in ["riotshield_mp", "usp_mp", "ak47_mp", "m4_mp"] {
        let i = crate::weapons::index_of(w);
        let r = crate::weapons::row(i).unwrap();
        eprintln!("{w}: melee {} type {} anim_type {} clip {} world {:?} ads {}", r.facts.melee_damage, r.facts.weap_type, r.facts.player_anim_type, r.facts.clip_size, weapon_world_model(i), r.facts.aim_down_sight);
    }
}

#[path = "character_events_tests.rs"]
mod events;

/// The skin catalog, and that every body in it loads from the install.
#[test]
#[ignore]
fn skin_catalog_loads() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let cat = skin_catalog().expect("catalog");
    let store = ensure_store(None).expect("store");
    let mut n = 0;
    for l in cat.lines() {
        let body = l.split('\t').nth(3).unwrap();
        assert!(store.models_for(body, &[], 0, &[]).is_some(), "{l}");
        n += 1;
    }
    eprintln!("{n} skins:\n{cat}");
    assert!(n > 60);
}

/// A skin built for character select (head 0), then spawned with another rolled head: both have a head.
#[test]
#[ignore]
fn skin_head_reroll_keeps_head() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let a = Character::build_with("opforce_airborne", "smg", 1, 0).unwrap();
    for h in 1..6 {
        let b = Character::build_with("opforce_airborne", "smg", 1, h).unwrap();
        eprintln!("head {h}: {:?}, {} bones (head 0: {:?}, {} bones)", b.head_name, b.bone_count(), a.head_name, a.bone_count());
        assert!(b.head_name.is_some(), "head {h} missing");
    }
}

/// A skin's own viewhands become the first-person arms (Spetsnaz arms, not the base Ranger ones).
#[test]
#[ignore]
fn skin_viewhands() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let m4 = crate::weapons::index_of("m4_mp");
    let base = crate::viewmodel::Viewmodel::build(m4).expect("base viewmodel");
    let base_maps: Vec<_> = base.surfaces.iter().map(|s| s.color_map.clone()).collect();
    for (f, c, v) in [("opforce_airborne", "smg", 1), ("opforce_airborne", "smg", 0), ("militia", "assault", 0), ("socom_141_arctic", "lmg", 0), ("us_army", "assault", 0)] {
        let ch = Character::build_with(f, c, v, 0).unwrap();
        let set = use_viewhands(&ch);
        let vm = crate::viewmodel::Viewmodel::build(m4).expect("viewmodel");
        let maps: Vec<_> = vm.surfaces.iter().map(|s| s.color_map.clone()).collect();
        eprintln!("{f}: viewhands {:?} set={set} -> surfaces {:?} (base {:?})", ch.spec.viewhands, maps, base_maps);
        // The base arms are the Rangers' (us_army_* textures): us_army may keep them.
        assert!(set || f == "us_army", "{f}: no viewhands");
    }
    crate::viewmodel::set_hands(None);
}

/// MW2's logo images as PNGs (picking the MW2 Soldier's portrait).
#[test]
#[ignore]
fn dump_logo_images() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let t = std::time::Instant::now();
    while !crate::sounds::iwd_ready() && t.elapsed().as_secs() < 120 {
        std::thread::sleep(std::time::Duration::from_millis(200));
    }
    let out = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/logo_preview");
    let _ = std::fs::create_dir_all(&out);
    let names: Vec<String> = std::env::var("IMAGES").map(|v| v.split(',').map(str::to_owned).collect()).unwrap_or_else(|_| ["logo_cod2", "cardtitle_mw2_black", "logo_iw", "cardicon_prestige_10"].iter().map(|s| s.to_string()).collect());
    for name in names.iter().map(String::as_str) {
        match crate::fx::texture_rgba(name) {
            Some(rgba) => {
                let (w, h, px) = &*rgba;
                // RGBA bottom-up -> RGB top-down on black.
                let (w, h) = (*w as usize, *h as usize);
                let mut rgb = vec![0u8; w * h * 3];
                for y in 0..h {
                    for x in 0..w {
                        let s = ((h - 1 - y) * w + x) * 4;
                        let a = px[s + 3] as u32;
                        for c in 0..3 {
                            rgb[(y * w + x) * 3 + c] = (px[s + c] as u32 * a / 255) as u8;
                        }
                    }
                }
                write_png(&out.join(format!("{name}.png")), w, h, &rgb);
                let mut a = vec![0u8; w * h * 3];
                let mut raw = vec![0u8; w * h * 3];
                for y in 0..h {
                    for x in 0..w {
                        let s = ((h - 1 - y) * w + x) * 4;
                        for c in 0..3 {
                            a[(y * w + x) * 3 + c] = px[s + 3];
                            raw[(y * w + x) * 3 + c] = px[s + c];
                        }
                    }
                }
                write_png(&out.join(format!("{name}_alpha.png")), w, h, &a);
                write_png(&out.join(format!("{name}_rgb.png")), w, h, &raw);
                eprintln!("{name}: {w}x{h}");
            }
            None => eprintln!("{name}: not found"),
        }
    }
}

/// Every skin built with heads 0..3: which come out without a head (character select showed a
/// headless soldier, playtest 10-04-26).
#[test]
#[ignore]
fn skin_heads_all() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let cat = skin_catalog().expect("catalog");
    let (mut ok, mut bad) = (0, 0);
    for l in cat.lines() {
        let c: Vec<&str> = l.split('\t').collect();
        let variant: usize = c[2].parse().unwrap_or(0);
        for h in 0..4 {
            match Character::build_with(c[0], c[1], variant, h) {
                Ok(ch) if ch.head_name.is_some() => {
                    ok += 1;
                    for s in ch.surfaces.iter().filter(|s| s.color_map.is_none()) { if h == 0 { eprintln!("NO TEXTURE {} {} v{}: {:?}", c[0], c[1], variant, s.material); } }
                }
                Ok(ch) => { bad += 1; eprintln!("NO HEAD {} {} v{} h{}: body {} (heads {:?})", c[0], c[1], variant, h, ch.body_name, ch.spec.heads); }
                Err(e) => { bad += 1; eprintln!("FAIL {} {} v{} h{}: {e}", c[0], c[1], variant, h); }
            }
        }
    }
    eprintln!("heads ok {ok}, missing {bad}");
}

/// CHAR=socom_141_arctic CLASS=sniper: the mptype and character scripts MW2 runs for a skin.
#[test]
#[ignore]
fn print_character_scripts() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let store = ensure_store(None).expect("store");
    let f = std::env::var("CHAR").unwrap_or_else(|_| "socom_141_arctic".into());
    let c = std::env::var("CLASS").unwrap_or_else(|_| "sniper".into());
    let lookup = |n: &str| store.common.raw_file(n);
    let spec = data::resolve_spec(&lookup, &f, &c, 0).expect("spec");
    eprintln!("mptype {} character {}", spec.mptype, spec.character);
    for n in [format!("character/{}.gsc", spec.character)] {
        eprintln!("--- {n}\n{}", lookup(&n).map(|b| String::from_utf8_lossy(&b).to_string()).unwrap_or_default());
    }
}

/// GUN=riotshield_mp: the world model's surfaces (material, colour map, sizes) - the riot shield's
/// front showed a grid of tiles (playtest 10-04-26).
#[test]
#[ignore]
fn print_world_gun_surfaces() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let name = std::env::var("GUN").unwrap_or_else(|_| "riotshield_mp".into());
    let w = crate::weapons::index_of(&name) | (255 << 16);
    let g = world_gun(w).expect("world gun");
    eprintln!("{name}: model {} - {} verts, {} tris, {} surfaces", g.model, g.positions.len(), g.indices.len() / 3, g.surfaces.len());
    for (i, s) in g.surfaces.iter().enumerate() {
        eprintln!("  surface {i}: {:?}", s);
    }
}

/// GUN / SURF: a world-model surface's decoded colour image to target/gun_tex_<w>x<h>.rgba.
#[test]
#[ignore]
fn dump_world_gun_texture() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let name = std::env::var("GUN").unwrap_or_else(|_| "riotshield_mp".into());
    let surf: u32 = std::env::var("SURF").ok().and_then(|s| s.parse().ok()).unwrap_or(1);
    let w = crate::weapons::index_of(&name) | (255 << 16);
    let (mut tw, mut th) = (0u32, 0u32);
    let n = unsafe { mw2_character_weapon_surface_rgba(w, surf, &mut tw, &mut th, std::ptr::null_mut(), 0) };
    let mut px = vec![0u8; n as usize];
    unsafe { mw2_character_weapon_surface_rgba(w, surf, &mut tw, &mut th, px.as_mut_ptr(), n) };
    let out = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(format!("../../target/gun_tex_{tw}x{th}.rgba"));
    std::fs::write(&out, &px).unwrap();
    eprintln!("wrote {} ({tw}x{th})", out.display());
}

/// CHAR / CLASS / HEAD: a built character's surfaces with material, colour map and whether it decodes.
#[test]
#[ignore]
fn print_character_surfaces() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let f = std::env::var("CHAR").unwrap_or_else(|_| "socom_141_arctic".into());
    let c = std::env::var("CLASS").unwrap_or_else(|_| "sniper".into());
    let h: usize = std::env::var("HEAD").ok().and_then(|s| s.parse().ok()).unwrap_or(0);
    let ch = Character::build_with(&f, &c, 0, h).expect("build");
    eprintln!("{f} {c}: body {} head {:?}, {} surfaces", ch.body_name, ch.head_name, ch.surfaces.len());
    for (i, s) in ch.surfaces.iter().enumerate() {
        let (mut w, mut hh) = (0u32, 0u32);
        let n = unsafe { mw2_character_surface_rgba(&ch, i as u32, &mut w, &mut hh, std::ptr::null_mut(), 0) };
        eprintln!("  {i}: {:?} {:?} -> {}x{} ({} bytes) blend {} bits {:x?}", s.material, s.color_map, w, hh, n, if s.state_bits.is_some() { crate::fx::blend_mode(s.state_bits) } else { 0 }, s.state_bits);
    }
}

/// GUN: the world model as target/<gun>.obj (Unity space, UVs as decoded) for checking its mapping.
#[test]
#[ignore]
fn export_world_gun_obj() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let name = std::env::var("GUN").unwrap_or_else(|_| "riotshield_mp".into());
    let g = world_gun(crate::weapons::index_of(&name) | (255 << 16)).expect("world gun");
    let mut s = String::new();
    for p in &g.positions { s += &format!("v {} {} {}\n", p[0], p[1], p[2]); }
    for t in &g.uvs { s += &format!("vt {} {}\n", t[0], t[1]); }
    for (si, surf) in g.surfaces.iter().enumerate() {
        s += &format!("g surf{si}\n");
        let tris = &g.indices[surf.index_start as usize..(surf.index_start + surf.index_count) as usize];
        for t in tris.chunks(3) { s += &format!("f {0}/{0} {1}/{1} {2}/{2}\n", t[0] + 1, t[1] + 1, t[2] + 1); }
    }
    let out = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(format!("../../target/{name}.obj"));
    std::fs::write(&out, s).unwrap();
    eprintln!("wrote {}", out.display());
}

/// WEAPONS=a,b: reticle fields (center / side material, sizes) from the weapon defs.
#[test]
#[ignore]
fn print_weapon_reticles() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    for n in std::env::var("WEAPONS").unwrap_or("throwingknife_mp,frag_grenade_mp,semtex_mp,c4_mp,claymore_mp,flash_grenade_mp".into()).split(',') {
        let w = crate::weapons::index_of(n);
        match crate::weapons::row(w) {
            Some(r) => eprintln!("{n}: center {:?} side {:?} {:?}", r.reticle_center, r.reticle_side, r.reticle),
            None => eprintln!("{n}: no row"),
        }
    }
}

/// WEAPONS=a,b: explosive numbers (radius, min radius, inner / outer damage, projectile speed).
#[test]
#[ignore]
fn print_weapon_explosions() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    for n in std::env::var("WEAPONS").unwrap_or("rpg_mp,at4_mp,m79_mp,gl_ak47_mp,gl_m4_mp,stinger_mp,javelin_mp,frag_grenade_mp,semtex_mp,c4_mp,claymore_mp".into()).split(',') {
        let w = crate::weapons::index_of(n);
        match crate::weapons::row(w) {
            Some(r) => { let g = &r.equip; eprintln!("{n}: radius {} min {} inner {} outer {} speed {} impact {} arm {} explosion_type {}", g.radius, g.radius_min, g.inner_damage, g.outer_damage, g.speed, g.impact_damage, g.activate_dist, g.explosion_type); }
            None => eprintln!("{n}: no row"),
        }
    }
}

/// WEAPONS=a,b: ADS view (zoom fov, overlay material and size) - the thermal / sniper scopes.
#[test]
#[ignore]
fn print_weapon_views() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    for n in std::env::var("WEAPONS").unwrap_or("ak47_thermal_mp,m4_thermal_mp,cheytac_thermal_mp,barrett_mp,cheytac_mp,ak47_acog_mp".into()).split(',') {
        let w = crate::weapons::index_of(n);
        match crate::weapons::row(w) {
            Some(r) => eprintln!("{n}: zoom {} overlay {:?} {}x{} reticle {}", r.view.ads_zoom_fov, r.overlay, r.view.overlay_width, r.view.overlay_height, r.view.overlay_reticle),
            None => eprintln!("{n}: no row"),
        }
    }
}

/// VISIONS=a,b: MW2's vision files (the AC-130 / Chopper Gunner thermal toggle swaps the map's
/// thermal vision for `missilecam`).
#[test]
#[ignore]
fn print_visions() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    for n in std::env::var("VISIONS").unwrap_or("missilecam,black_bw,ac130,ac130_inverted,thermal_mp".into()).split(',') {
        match crate::visions::get(n) {
            Some(t) => eprintln!("== {n}\n{t}"),
            None => eprintln!("== {n}: none"),
        }
    }
}

/// MATCH=a,b: HUD/2D material and image names containing any of the words (what MW2 ships for
/// a feature, e.g. the heartbeat sensor's screen).
#[test]
#[ignore]
fn print_material_names() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let words: Vec<String> = std::env::var("MATCH").unwrap_or("heartbeat,motion".into()).split(',').map(str::to_owned).collect();
    let g = crate::images::MATERIAL_IMAGES.read().unwrap();
    let mut hits: Vec<_> = g.as_ref().map(|m| m.iter().filter(|(k, v)| words.iter().any(|w| k.contains(w.as_str()) || v.contains(w.as_str()))).map(|(k, v)| format!("{k} -> {v}")).collect()).unwrap_or_default();
    hits.sort();
    for h in hits { eprintln!("{h}"); }
}

/// WEAPONS=a,b: IW4 weapClass per weapon (the sniper damage scale keys on it).
#[test]
#[ignore]
fn print_weapon_classes() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    for n in std::env::var("WEAPONS").unwrap_or("cheytac_mp,barrett_mp,wa2000_mp,m21_mp,cheytac_thermal_mp,ak47_mp,rpd_mp,ump45_mp,spas12_mp,usp_mp,rpg_mp,throwingknife_mp".into()).split(',') {
        eprintln!("{n}: class {}", crate::weapons::mw2_weapon_class(crate::weapons::index_of(n)));
    }
}

/// IMAGES=a,b: HUD/material images to target/hud_check as PNG-ready RGBA (name_WxH.rgba).
#[test]
#[ignore]
fn dump_hud_images() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/hud_check");
    let _ = std::fs::create_dir_all(&dir);
    for n in std::env::var("IMAGES").unwrap_or("motiontracker3d_bg,motiontracker3d_sweep,motiontracker3d_ping_enemy_mp,motiontracker3d_ping_friendly_mp,motion_tracker_screen_col".into()).split(',') {
        match crate::images::hud_rgba(n) {
            Some((w, h, px)) => { let _ = std::fs::write(dir.join(format!("{n}_{w}x{h}.rgba")), &px); eprintln!("{n}: {w}x{h}"); }
            None => eprintln!("{n}: none"),
        }
    }
}

/// RAW=name: print a rawfile (GSC etc.) from patch_mp / common_mp; RAW=?substr lists names.
#[test]
#[ignore = "needs the MW2 install"]
fn print_raw_file() {
    let dir = std::path::Path::new(r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english");
    let want = std::env::var("RAW").unwrap_or("?stinger".into());
    for zone in ["patch_mp.ff", "common_mp.ff"] {
        let Ok((s, _)) = mw2data::scripts_from_file(&dir.join(zone)) else { continue };
        if let Some(sub) = want.strip_prefix('?') {
            for (n, b) in &s.raw_files {
                if n.contains(sub) {
                    eprintln!("{zone}: {n} ({} bytes)", b.len());
                }
            }
        } else if let Some(b) = s.find_raw(&want) {
            eprintln!("{}", String::from_utf8_lossy(b));
            return;
        }
    }
}

/// WM=word: every weapon -> world model pair whose weapon or model name contains the word
/// (which model a killstreak's pickup can show).
#[test]
#[ignore = "needs the user's MW2 install"]
fn print_world_models() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
    }
    let store = ensure_store(None).unwrap();
    let words: Vec<String> = std::env::var("WM").unwrap_or_else(|_| "killstreak".into()).split(',').map(str::to_string).collect();
    let mut v: Vec<_> = store.common.world_models.iter().filter(|(k, m)| words.iter().any(|w| k.contains(w.as_str()) || m.contains(w.as_str()))).collect();
    v.sort();
    for (k, m) in v {
        eprintln!("WM {k} -> {m}");
    }
    // Every loaded mesh (anything Mw2Prop can build) whose name contains one of the words.
    let meshes = crate::models::MESHES.read().unwrap();
    let names: Vec<&str> = meshes.iter().map(|m| m.name.as_str()).filter(|n| words.iter().any(|w| n.contains(w.as_str()))).collect();
    eprintln!("MESH {}: {names:?}", names.len());
}
