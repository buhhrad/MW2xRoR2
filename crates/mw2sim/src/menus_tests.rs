use super::*;

const COMMON_MP: &str = r"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare 2\zone\english\common_mp.ff";

#[test]
fn scripts_tokenize_like_iw4() {
    let t = tokenize(r#""setPlayerData" ( "customClasses" , "localVarInt" ( "classIndex" ) , "perks" , 0 , "specialty_null" ) ; "open" "x";"#);
    assert_eq!(t.iter().filter(|t| matches!(t, Tok::Word(_))).count(), 9);
    assert_eq!(t.iter().filter(|t| **t == Tok::Semi).count(), 2);
}

#[test]
fn set_player_data_reads_locals() {
    let mut rt = Runtime::default();
    rt.locals.insert("classindex".into(), Operand::Int(3));
    let mut acts = Vec::new();
    rt.parse_script(r#""focusFirst" "setPlayerData" ( "customClasses" , "localVarInt" ( "classIndex" ) , "weaponSetups" , 0 , "weapon" , "m4" ) ; "play" "mouse_click" ;"#, &mut acts);
    assert!(matches!(acts[0], Act::FocusFirst));
    match &acts[1] {
        Act::PlayerData(p, v) => {
            assert_eq!(pkey(p), "customclasses.3.weaponsetups.0.weapon");
            assert_eq!(v, "m4");
        }
        _ => panic!("expected setPlayerData"),
    }
    assert!(matches!(&acts[2], Act::Play(a) if a == "mouse_click"));
}

fn render(label: &str) {
    let o = step(1920.0, 1080.0, 1000, -1.0, -1.0, 0).expect("menus loaded");
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/menu_preview");
    std::fs::create_dir_all(&dir).unwrap();
    crate::hud::tests::preview::write(&dir.join(format!("{label}.png")), &o.cmds, &o.strings, 1920, 1080, false);
    if std::env::var("MENU_DUMP").is_ok() {
        for (i, c) in o.cmds.iter().enumerate() {
            eprintln!("{i:3} k{} {:<28} ({:6.0},{:6.0} {:6.0}x{:6.0}) rgba({:.2},{:.2},{:.2},{:.2})", c.kind, o.strings.get(usize::from(c.material)).map_or("", String::as_str), c.rect[0], c.rect[1], c.rect[2], c.rect[3], c.color[0], c.color[1], c.color[2], c.color[3]);
        }
    }
    let mut missing: Vec<&str> = o.cmds.iter().filter(|c| c.kind == 0).map(|c| o.strings[usize::from(c.material)].as_str()).filter(|n| crate::images::hud_rgba(n).is_none()).collect();
    missing.dedup();
    eprintln!("   missing textures: {missing:?} -> images {:?} bits {:x?}", missing.iter().map(|n| crate::images::image_name(n)).collect::<Vec<_>>(), missing.iter().map(|n| crate::images::state_bits(n)).collect::<Vec<_>>());
    let texts: Vec<&str> = o.cmds.iter().filter(|c| c.kind == 1).map(|c| o.strings[usize::from(c.material)].as_str()).collect();
    eprintln!("== {label}: {} cmds, open {:?}\n   text: {:?}", o.cmds.len(), open_names(), texts);
    let mut diag = diagnose();
    diag.sort();
    diag.dedup();
    for l in diag.iter().take(40) {
        eprintln!("   diag: {l}");
    }
}

/// Click an item of the top menu by name (focus + action, as a mouse click does).
fn click(name: &str) {
    with(|rt, d| {
        let top = input_menu(rt, d).unwrap();
        let i = d.menus[&top].items.iter().position(|it| it.name.eq_ignore_ascii_case(name)).unwrap_or_else(|| panic!("{name} not in {top}"));
        set_focus(rt, d, &top, Some(i), 0);
        activate(rt, d, &top, i);
    });
}

#[test]
#[ignore = "needs the MW2 install"]
fn create_a_class_runs_from_the_zone() {
    assert!(std::path::Path::new(COMMON_MP).exists(), "MW2 install not found");
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        crate::hud::mw2_hud_init();
    }
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let pdata = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/menu_preview/pdata.txt");
    let _ = std::fs::remove_file(&pdata);
    let n = init(dir, Some(pdata.clone())).expect("ui zone");
    eprintln!("menus: {n}");
    assert!(n > 200);
    for k in ["customclasses.0.weaponsetups.0.weapon", "customclasses.0.perks.1", "customclasses.3.weaponsetups.0.weapon", "killstreaks.0", "customclasses.0.name"] {
        eprintln!("  {k} = {}", player_data(k));
    }
    assert_eq!(player_data("customclasses.0.weaponsetups.0.weapon"), "m4");
    assert_eq!(player_data("customclasses.3.weaponsetups.0.weapon"), "cheytac");

    with(|rt, _| rt.backdrop = Some(("main_text".into(), 9)));
    assert!(open_menu("cac_popup"));
    render("cac_popup");
    eprintln!("   buttons: {:?}", buttons());
    click("cac_customclass1");
    render("cac_from_popup");
    with(|rt, _| rt.stack.clear());

    // A level 1 player: MW2's padlocks on the assault rifles.
    set_player_data("experience", "0");
    set_local("classIndex", Operand::Int(0));
    assert!(open_menu("menu_cac_assault"));
    click("cac_primary");
    click("cac_primary_assault");
    render("cac_locked_level1");
    with(|rt, _| rt.stack.clear());

    // The rest as a level 70 player with Marksman I on the AK-47 (Grenade Launcher open).
    set_player_data("experience", "2516000");
    set_player_data("challengestate.ch_marksman_ak47", "2");
    set_local("classIndex", Operand::Int(0));
    assert!(open_menu("menu_cac_assault"));
    render("cac_assault");

    click("cac_primary");
    render("cac_weapon_primary");
    let top = open_names().last().cloned().unwrap();
    eprintln!("after primary click: {top}");
    eprintln!("   buttons: {:?}", buttons());

    click("cac_primary_assault");
    eprintln!("open: {:?}
   buttons: {:?}", open_names(), buttons());
    click("cac_weapon_ak47");
    eprintln!("after ak47: {:?} weapon={} att0={}", open_names(), player_data("customclasses.0.weaponsetups.0.weapon"), player_data("customclasses.0.weaponsetups.0.attachment.0"));
    eprintln!("   buttons: {:?}", buttons());
    render("cac_after_weapon");
    assert_eq!(player_data("customclasses.0.weaponsetups.0.weapon"), "ak47");

    click("cac_attachment_0");
    eprintln!("after attachment: {:?} att0={}", open_names(), player_data("customclasses.0.weaponsetups.0.attachment.0"));
    let lo = class_loadout(0).unwrap();
    eprintln!("class 0 loadout: {lo:?}");
    assert!(lo[0].starts_with("ak47_"), "primary {}", lo[0]);
    assert_eq!(lo[1], "usp_mp");
    assert_eq!(build_weapon_name("m4", "silencer", "acog"), "m4_acog_silencer_mp");

    // MW2's default classes (changeclass class0 .. class4) and the in-match class menu.
    let grenadier = class_loadout(10).unwrap();
    eprintln!("default class 0: {grenadier:?}");
    assert_eq!(grenadier[0], "famas_gl_mp");
    with(|rt, _| rt.stack.clear());
    assert!(open_menu("changeclass"), "changeclass from common_mp");
    render("changeclass");
    eprintln!("   buttons: {:?}", buttons());
    click("class_custom_1");
    let resp = with(|rt, _| std::mem::take(&mut rt.responses)).unwrap();
    eprintln!("   responses: {resp:?} open {:?}", open_names());
    assert_eq!(resp, vec!["custom1".to_owned()]);
}

/// Names of the top menu's focusable items.
fn buttons() -> Vec<String> {
    with(|rt, d| {
        let top = input_menu(rt, d).unwrap();
        focusable(rt, d, &top).into_iter().map(|i| d.menus[&top].items[i].name.clone()).collect()
    })
    .unwrap_or_default()
}

#[test]
#[ignore = "needs the MW2 install"]
fn print_items() {
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let (mut s, _) = mw2data::scripts_from_file(&dir.join("localized_ui_mp.ff")).unwrap();
    for (n, r, h, v) in &s.menus {
        if n.starts_with("popup_cac_weapon_primary") || n.starts_with("popup_cac_assault_primary") || n == "popup_primary_attachments" || n == "menu_cac_assault" {
            eprintln!("MENU {n} rect {r:?} align {h} {v}");
            for (m, k, dmp) in &s.menu_float_exps {
                if m == n {
                    eprintln!("   menu float[{k}]: {dmp}");
                }
            }
        }
    }
    let menu = std::env::var("MENU").unwrap_or("popup_cac_weapon_primary".into());
    crate::images::add_from(&mut s);
    unsafe { crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) };
    for m in ["white", "xpbar_stencilbase", "mw2_popup_bg_fogstencil", "mw2_popup_bg_fogscroll", "mockup_popup_bg_stencilfill", "small_box_lightfx", "big_menu_lightfx", "drop_shadow_t", "720_xpbar_solid", "720_xpbar", "xpbar_xpfill"] {
        let b = crate::images::state_bits(m);
        eprintln!("{m:30} blend {} bits {:x?}", crate::images::blend(m), b);
    }
    for (i, it) in s.menu_items.iter_mut().filter(|i| i.menu == menu).enumerate().take(12) {
        eprintln!("{i:2} '{}' type {} style {} flags {:#x} rect {:?} h{} v{} fore {:?} back {:?} bg '{}'", it.name, it.item_type, it.style, it.static_flags, it.rect, it.horz_align, it.vert_align, it.fore_color, it.back_color, it.background);
    }
}

fn dec_hex(s: &str) -> String {
    s.split(' ')
        .map(|t| match t.strip_prefix("s:") {
            Some(h) => format!("\"{}\"", (0..h.len() / 2).filter_map(|i| u8::from_str_radix(&h[i * 2..i * 2 + 2], 16).ok().map(char::from)).collect::<String>()),
            None => t.to_owned(),
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn print_stmts(st: &[MenuStmt], ind: usize) {
    for s in st {
        match s {
            MenuStmt::Script(t) => eprintln!("{:ind$}{}", "", t.trim()),
            MenuStmt::If(c, b) => {
                eprintln!("{:ind$}if {}", "", dec_hex(c));
                print_stmts(b, ind + 2);
            }
            MenuStmt::Else(b) => {
                eprintln!("{:ind$}else", "");
                print_stmts(b, ind + 2);
            }
            MenuStmt::SetLocal { kind, name, expr } => eprintln!("{:ind$}setLocal[{kind}] {name} = {}", "", dec_hex(expr)),
        }
    }
}

/// MENU_ZONE=common_mp.ff MENU=changeclass: a menu's scripts, decoded.
#[test]
#[ignore = "needs the MW2 install"]
fn print_scripts() {
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let zone = std::env::var("MENU_ZONE").unwrap_or("common_mp.ff".into());
    let (s, _) = mw2data::scripts_from_file(&dir.join(zone)).unwrap();
    let menu = std::env::var("MENU").unwrap_or("changeclass".into());
    let names = mw2data::scripts::SCRIPT_KIND_NAMES;
    eprintln!("fullscreen: {}", s.menu_fullscreen.contains(&menu));
    for (m, k, st) in &s.menu_scripts {
        if *m == menu {
            eprintln!("MENU {}:", names[*k as usize]);
            print_stmts(st, 2);
        }
    }
    for (i, it) in s.menu_items.iter().filter(|i| i.menu == menu).enumerate() {
        if it.scripts.is_empty() {
            continue;
        }
        eprintln!("item {i} '{}'", it.name);
        if !it.vis_exp.is_empty() { eprintln!("  visible: {}", dec_hex(&it.vis_exp)); }
        if !it.disabled_exp.is_empty() { eprintln!("  disabled: {}", dec_hex(&it.disabled_exp)); }
        for (k, st) in &it.scripts {
            eprintln!("  {}:", names[*k as usize]);
            print_stmts(st, 4);
        }
    }
}

/// LOC=KEY1,KEY2: localized strings as the menus see them.
#[test]
#[ignore = "needs the MW2 install"]
fn print_localized() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    let g = DATA.lock().unwrap();
    let d = g.as_ref().unwrap();
    for k in std::env::var("LOC").unwrap_or_default().split(',') {
        eprintln!("{k} = {:?}", localize(&d.hud, k));
    }
}

/// Where the arrow tip sits inside ui_cursor (IW4 draws it centred on the mouse point).
#[test]
#[ignore = "needs the MW2 install"]
fn cursor_hotspot() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    let (w, h, px) = crate::images::hud_rgba("ui_cursor").expect("ui_cursor");
    // Rows come bottom-first; report from the top.
    let opaque = |x: u32, y_top: u32| px[(((h - 1 - y_top) * w + x) * 4 + 3) as usize] > 128;
    let mut first = None;
    'outer: for s in 0..(w + h) {
        for x in 0..w.min(s + 1) {
            let y = s - x;
            if y < h && opaque(x, y) {
                first = Some((x, y));
                break 'outer;
            }
        }
    }
    let (mut x0, mut y0, mut x1, mut y1) = (w, h, 0, 0);
    for y in 0..h {
        for x in 0..w {
            if opaque(x, y) {
                x0 = x0.min(x);
                y0 = y0.min(y);
                x1 = x1.max(x);
                y1 = y1.max(y);
            }
        }
    }
    eprintln!("ui_cursor {w}x{h}: tip (top-left-most opaque) {first:?}, opaque bounds x {x0}..{x1} y {y0}..{y1}");
}

/// MW2's unlocks: rank gates (AK-47 is level 70) and challenge gates (AK-47 GL needs Marksman
/// tier 1 = 10 kills), challenge tiers paying MW2's XP.
#[test]
#[ignore = "needs the MW2 install"]
fn unlocks_follow_rank_and_challenges() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    set_player_data("experience", "0");
    assert!(item_unlocked("m4"), "M4A1 from the start");
    assert!(!item_unlocked("ak47"), "AK-47 is level 70");
    assert!(!item_unlocked("specialty_bling"), "Bling is level 21");
    set_player_data("experience", "2516000");
    assert!(item_unlocked("ak47"), "level 70 opens the AK-47");
    assert!(!item_unlocked("ak47 gl"), "the GL still needs Marksman");
    let mut done = Vec::new();
    for _ in 0..10 {
        done.extend(challenge_kill("ak47_mp", false));
    }
    eprintln!("after 10 AK kills: {done:?}");
    assert_eq!(done, vec![("ch_marksman_ak47".to_owned(), 1, 250)]);
    assert!(item_unlocked("ak47 gl"));
    assert!(!item_unlocked("ak47 reflex"), "red dot needs Marksman tier 2");
    with(|rt, d| eprintln!("rank at 85950 XP: {}", { rt.pdata.insert("experience".into(), "85950".into()); rank_of(rt, d) }));

    // A class built before the locks: AK-47 + GL + FMJ at level 16 hands out the class's default.
    set_player_data("customclasses.0.weaponsetups.0.weapon", "ak47");
    set_player_data("customclasses.0.weaponsetups.0.attachment.0", "gl");
    set_player_data("customclasses.0.weaponsetups.0.attachment.1", "fmj");
    let lo = class_loadout(0).unwrap();
    eprintln!("class 0 at level 16: {lo:?}");
    assert_eq!(lo[0], "m4_mp");
    set_player_data("experience", "2516000");
    let lo = class_loadout(0).unwrap();
    eprintln!("class 0 at level 70: {lo:?}");
    assert_eq!(lo[0], "ak47_gl_mp", "unlocked AK-47 with its GL (Marksman I done); FMJ dropped without Bling");
}

/// MODELS=ak47: model names loaded that contain the filter (camo variants, ...).
#[test]
#[ignore = "needs the MW2 install"]
fn print_models() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let f = std::env::var("MODELS").unwrap_or("ak47".into());
    let m = crate::models::MESHES.read().unwrap();
    for x in m.iter().filter(|x| x.name.contains(&f)) {
        eprintln!("model {} surfaces {:?}", x.name, x.surfaces.iter().map(|s| s.material.clone().unwrap_or_default()).collect::<Vec<_>>());
    }
}

/// Camo: the viewmodel switches to `<gun model>_<camo>`, whose materials carry the camo detail map.
#[test]
#[ignore = "needs the MW2 install"]
fn camo_viewmodel_swaps_models() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let w = crate::weapons::index_of("m4_mp");
    let plain: Vec<_> = crate::viewmodel::Viewmodel::build_as(w, 0).unwrap().surfaces.iter().map(|s| s.detail_map.clone()).collect();
    unsafe { crate::weapons::mw2_set_weapon_camo(w, b"woodland".as_ptr(), 8) };
    let wood: Vec<_> = crate::viewmodel::Viewmodel::build_as(w, 0).unwrap().surfaces.iter().map(|s| s.detail_map.clone()).collect();
    eprintln!("plain {:?}\nwoodland {:?}", &plain[..plain.len().min(4)], &wood[..wood.len().min(4)]);
    assert!(wood.iter().any(|d| d.as_deref() == Some("weapon_camo_woodland")));
    assert!(!plain.iter().any(|d| d.as_deref() == Some("weapon_camo_woodland")));
    unsafe { crate::weapons::mw2_set_weapon_camo(w, b"none".as_ptr(), 4) };
}

#[test]
#[ignore = "needs the MW2 install"]
fn print_rigs() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let f = std::env::var("MODELS").unwrap_or("m4".into());
    let r = crate::viewmodel::RIGS.read().unwrap();
    eprintln!("rigs: {} total", r.len());
    for m in r.iter().filter(|m| m.name.contains(&f)) {
        eprintln!("rig {}", m.name);
    }
}

/// MAT=name: a material's textures (semantic, name hash, image).
#[test]
#[ignore = "needs the MW2 install"]
fn print_material_textures() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    for m in std::env::var("MAT").unwrap_or("mc/mtl_weapon_m4_short_woodland,mc/mtl_weapon_m4_short".into()).split(',') {
        eprintln!("{m}: {:x?}", crate::models::material_textures(m));
    }
}

/// MW2's Create-a-Streak (menu_cas_popup): runs from the zone, picks write killstreaks.N.
#[test]
#[ignore = "needs the MW2 install"]
fn create_a_streak_runs() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    set_player_data("experience", "2516000");
    assert!(open_menu("menu_cas_popup"));
    render("cas");
    eprintln!("   buttons: {:?}", buttons());
    eprintln!("   killstreaks {:?}", (0..3).map(|i| player_data(&format!("killstreaks.{i}"))).collect::<Vec<_>>());
}

/// ZONE=mp_rust.ff ALIAS=boost: alias names in a zone's sound bank containing ALIAS.
#[test]
#[ignore = "needs the MW2 install"]
fn print_zone_aliases() {
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let zone = std::env::var("ZONE").unwrap_or("mp_rust.ff".into());
    let want = std::env::var("ALIAS").unwrap_or("boost".into());
    let (_, _, bank) = mw2data::walk_file(&dir.join(&zone)).unwrap();
    let mut names: Vec<&str> = bank.aliases.iter().map(|a| a.list.as_str()).filter(|n| n.to_ascii_lowercase().contains(&want)).collect();
    names.sort();
    names.dedup();
    eprintln!("{zone}: {} aliases, matching {want}: {names:?}", bank.aliases.len());
}

#[test]
#[ignore = "needs the MW2 install"]
fn martyrdom_frag_exists() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    for n in ["frag_grenade_short_mp", "frag_grenade_mp"] {
        let w = crate::weapons::index_of(n);
        let mut e = crate::equipment::Mw2Equipment::default();
        let ok = unsafe { crate::equipment::mw2_weapon_equipment(w, &mut e) };
        eprintln!("{n}: index {w} ok {ok} fuse {} ms radius {} inner {} outer {}", e.fuse_ms, e.radius, e.inner_damage, e.outer_damage);
    }
}

/// Create-a-Streak driven like a player at a high rank (85,950 XP): open a slot, pick another
/// streak, check it lands in playerdata. Prints each step's buttons.
#[test]
#[ignore = "needs the MW2 install"]
fn create_a_streak_picks() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    set_player_data("experience", "85950");
    eprintln!("rank-gated: cas unlocked {}", item_unlocked("cas"));
    for s in ["uav", "harrier_airstrike", "ac130", "emp", "nuke", "sentry", "helicopter_minigun"] {
        eprintln!("   {s}: unlocked {}", item_unlocked(s));
    }
    eprintln!("before: {:?}", (0..3).map(|i| player_data(&format!("killstreaks.{i}"))).collect::<Vec<_>>());
    assert!(open_menu("menu_cas_popup"));
    for step in 0..6 {
        let st = stack();
        let bs = buttons();
        eprintln!("step {step}: stack {st:?} buttons {bs:?}");
        render(&format!("cas_{step}"));
        let top = st.last().cloned().unwrap_or_default();
        if top.starts_with("popup_welcome") || top.starts_with("popup_tokens") {
            if bs.iter().any(|b| b == "button_ok") { click("button_ok"); } else { break; }
        } else if top == "popup_unlockconfirm" {
            eprintln!("   tokens: ui_numTokens {:?}, pdata {:?}", local_text("ui_numTokens"), (0..4).map(|i| player_data(&format!("killstreakUnlockTokens.{i}"))).collect::<Vec<_>>());
            click("button_yes");
            eprintln!("   unlocked airdrop: '{}'", player_data("killstreakunlocked.airdrop"));
        } else if top == "menu_cas_popup" && step == 1 {
            // Care Package (locked), clicked with the mouse at its row like a player.
            let r = with(|rt, d| {
                let menu = &d.menus["menu_cas_popup"];
                let ctx = Ctx::for_menus(rt.screen[0], rt.screen[1], rt.time_ms);
                let host = MHost { rt, d, menu };
                let parent = menu_parent(&host, menu);
                item_screen_rect(&ctx, &host, menu, 38, &parent)
            }).flatten().expect("care package row");
            let (x, y) = (r[0] + r[2] * 0.5, r[1] + r[3] * 0.5);
            let (w, h) = with(|rt, _| (rt.screen[0], rt.screen[1])).unwrap();
            super::step(w, h, 1000, x, y, 0);
            super::step(w, h, 1016, x, y, KEY_CLICK);
            super::step(w, h, 1032, x, y, 0);
        } else if let Some(b) = std::env::var("PICK").ok().and_then(|p| p.split(',').nth(step).map(str::to_owned)) {
            click(&b);
        } else {
            break;
        }
    }
    // F1 clears, then the Care Package row (now unlocked) is clicked, then Esc backs out.
    let r = with(|rt, d| {
        let menu = &d.menus["menu_cas_popup"];
        let ctx = Ctx::for_menus(rt.screen[0], rt.screen[1], rt.time_ms);
        let host = MHost { rt, d, menu };
        let parent = menu_parent(&host, menu);
        item_screen_rect(&ctx, &host, menu, 38, &parent)
    }).flatten().expect("care package row");
    let (w, h) = with(|rt, _| (rt.screen[0], rt.screen[1])).unwrap();
    let (x, y) = (r[0] + r[2] * 0.5, r[1] + r[3] * 0.5);
    super::step(w, h, 2000, x, y, KEY_F1);
    eprintln!("after F1: ui_numStreaks {}", local_text("ui_numStreaks"));
    super::step(w, h, 2016, x, y, KEY_CLICK);
    eprintln!("after pick: ui_streak1Name {}", local_text("ui_streak1Name"));
    render("cas_picked");
    super::step(w, h, 2032, x, y, KEY_ESC);
    eprintln!("closed: stack {:?}", stack());
    eprintln!("after: {:?}", (0..3).map(|i| player_data(&format!("killstreaks.{i}"))).collect::<Vec<_>>());
}

fn stack() -> Vec<String> {
    with(|rt, _| rt.stack.iter().map(|m| m.name.clone()).collect()).unwrap_or_default()
}

/// Menu names in the UI zones containing FILTER (default "streak").
#[test]
#[ignore = "needs the MW2 install"]
fn print_streak_menus() {
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let want = std::env::var("FILTER").unwrap_or("streak".into());
    for zone in ["localized_ui_mp.ff", "common_mp.ff", "patch_mp.ff", "ui_mp.ff"] {
        let Ok((s, _)) = mw2data::scripts_from_file(&dir.join(zone)) else { continue };
        let names: Vec<&str> = s.menus.iter().map(|m| m.0.as_str()).filter(|n| n.to_ascii_lowercase().contains(&want)).collect();
        eprintln!("{zone}: {names:?}");
    }
}

/// MENU=menu_cas_popup: each item's name, text and event scripts.
#[test]
#[ignore = "needs the MW2 install"]
fn print_menu_scripts() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    let want = std::env::var("MENU").unwrap_or("menu_cas_popup".into());
    with(|_, d| {
        for (name, m) in &d.menus {
            if !name.eq_ignore_ascii_case(&want) { continue; }
            for (k, st) in &m.scripts { eprintln!("MENU {k}: {st:?}"); }
            for (i, it) in m.items.iter().enumerate() {
                if it.scripts.is_empty() { continue; }
                eprintln!("ITEM {i} '{}' text {:?}", it.name, it.text);
                for (k, st) in &it.scripts { eprintln!("   {k}: {st:?}"); }
            }
        }
    });
}

fn local_text(name: &str) -> String {
    with(|rt, _| rt.locals.get(&name.to_ascii_lowercase()).map(|o| format!("{o:?}")).unwrap_or_default()).unwrap_or_default()
}

/// mp/factionTable.csv rows (faction refs, names, emblems, voice prefixes).
#[test]
#[ignore = "needs the MW2 install"]
fn faction_table_rows() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        crate::hud::mw2_hud_init();
    }
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).expect("ui zone");
    let g = DATA.lock().unwrap();
    // TABLE=mp/rankicontable.csv picks another table (default factionTable).
    let name = std::env::var("TABLE").unwrap_or_else(|_| "mp/factiontable.csv".into()).to_ascii_lowercase();
    let t = g.as_ref().unwrap().tables.get(&name).expect("table");
    for r in 0..t.rows {
        eprintln!("ROW {}", (0..14).map(|c| t.cell(r, c).to_owned()).collect::<Vec<_>>().join(" | "));
    }
}

/// Unlock everything: every unlockTable item (weapons, attachments, camos, perks ...) is open.
#[test]
#[ignore = "needs the MW2 install"]
fn unlock_all_opens_every_unlock() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        crate::hud::mw2_hud_init();
    }
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let pdata = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/menu_preview/pdata_unlock_all.txt");
    let _ = std::fs::remove_file(&pdata);
    init(dir, Some(pdata.clone())).expect("ui zone");
    let (n, xp) = unlock_all().expect("menus");
    eprintln!("unlocked: {n} challenges, xp {xp}");
    assert!(n > 300 && xp >= 2_516_000, "{n} {xp}");
    let locked: Vec<String> = with(|rt, d| {
        let t = table(d, "mp/unlocktable.csv").unwrap();
        // Rank 9999 is MW2's own "never" (cut items: gold guns, removed perks and streaks).
        (0..t.rows).filter(|&r| !t.cell(r, 0).is_empty() && t.cell(r, 2).trim() != "9999" && !unlocked(rt, d, t.cell(r, 0)))
            .map(|r| format!("{} (rank {} challenge {:?} known {})", t.cell(r, 0), t.cell(r, 2), t.cell(r, 3),
                table(d, "mp/allchallengestable.csv").is_some_and(|c| row_of(c, 0, challenge_ref(d, t.cell(r, 3).trim()).0.as_str()).is_some())))
            .collect()
    })
    .unwrap();
    eprintln!("still locked: {}", locked.len());
    for l in &locked { eprintln!("  LOCKED {l}"); }
    assert!(locked.is_empty(), "{locked:?}");
}

/// changeclass at rank 1 (fresh player data): each item's visibility expression and result.
#[test]
#[ignore = "needs the MW2 install"]
fn changeclass_visibility() {
    unsafe {
        assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000);
        crate::hud::mw2_hud_init();
    }
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let pdata = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../target/menu_preview/pdata_vis.txt");
    let _ = std::fs::remove_file(&pdata);
    init(dir, Some(pdata)).expect("ui zone");
    assert!(open_menu("changeclass"));
    with(|rt, d| {
        let m = d.menus.get("changeclass").unwrap();
        let host = MHost { rt, d, menu: m };
        let open = rt.stack.iter().rev().find(|o| o.name == "changeclass").unwrap();
        for it in &m.items {
            if let Some(v) = &it.vis {
                eprintln!("VIS {:28} {:5} {}", it.name, item_visible(&host, open, it), v.raw);
            }
        }
    });
    eprintln!("buttons at rank 1: {:?}", buttons());
}

/// TABLE=mp/splashTable.csv KEYS=a,b: rows of a zone string table (column layout checks).
#[test]
#[ignore = "needs the MW2 install"]
fn print_table_rows() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    let g = DATA.lock().unwrap();
    let d = g.as_ref().unwrap();
    let name = std::env::var("TABLE").unwrap_or_else(|_| "mp/splashtable.csv".into()).to_ascii_lowercase();
    let t = d.tables.get(&name).expect("table");
    let keys: Vec<String> = std::env::var("KEYS").unwrap_or_default().split(',').map(|s| s.to_ascii_lowercase()).collect();
    for r in 0..t.rows {
        let c01 = format!("{}|{}", t.cell(r, 0), t.cell(r, 1)).to_ascii_lowercase();
        if r < 2 || keys.iter().any(|k| !k.is_empty() && c01.split('|').any(|c| c == k.as_str())) {
            eprintln!("row {r}: {:?}", (0..12).map(|c| t.cell(r, c).to_owned()).collect::<Vec<_>>());
        }
    }
}

/// Create-a-Streak: dismiss the unlocks popup, then click UAV / Care Package and watch the picks
/// (playtest 10-04-26: they stay selected, can't be unselected).
#[test]
#[ignore = "needs the MW2 install"]
fn create_a_streak_toggle() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    set_player_data("experience", &std::env::var("XP").unwrap_or("2516000".into()));
    assert!(open_menu("menu_cas_popup"));
    let picks = || (0..3).map(|i| player_data(&format!("killstreaks.{i}"))).collect::<Vec<_>>();
    eprintln!("open: {:?} top {:?} picks {:?}", open_names(), buttons(), picks());
    if buttons().iter().any(|b| b == "button_ok") { click("button_ok"); let _ = step(1920.0, 1080.0, 1000, -1.0, -1.0, 0); }
    eprintln!("after ok: {:?} buttons {:?}", open_names(), buttons());
    let locals = || with(|rt, _| ["ui_streak1name", "ui_streak2name", "ui_streak3name", "ui_numstreaks"].iter().map(|k| rt.locals.get(*k).map(operand_text).unwrap_or_default()).collect::<Vec<_>>());
    eprintln!("locals at open {:?}", locals());
    for idx in std::env::var("CLICKS").unwrap_or("27,38,27".into()).split(',') {
        let i: usize = idx.parse().unwrap();
        with(|rt, d| { set_focus(rt, d, "menu_cas_popup", Some(i), 0); activate(rt, d, "menu_cas_popup", i); });
        let _ = step(1920.0, 1080.0, 1000, -1.0, -1.0, 0);
        eprintln!("clicked item {i}: locals {:?} picks {:?} open {:?}", locals(), picks(), open_names());
    }
    if open_names().iter().any(|m| m == "popup_unlockconfirm") {
        eprintln!("unlock popup buttons {:?}", buttons());
        with(|rt, d| {
            let f = focusable(rt, d, "popup_unlockconfirm");
            for &i in &f {
                let it = &d.menus["popup_unlockconfirm"].items[i];
                eprintln!("  popup item {i} '{}' {:?}", it.name, it.text_exp.as_ref().map(|e| e.raw.chars().take(80).collect::<String>()));
                if let Some(s) = scripts_of(&it.scripts, ACTION) { print_stmts(s, 4); }
            }
        });
        let first = with(|rt, d| focusable(rt, d, "popup_unlockconfirm").first().copied()).flatten();
        if let Some(i) = first {
            with(|rt, d| { set_focus(rt, d, "popup_unlockconfirm", Some(i), 0); activate(rt, d, "popup_unlockconfirm", i); });
            let _ = step(1920.0, 1080.0, 1000, -1.0, -1.0, 0);
            eprintln!("confirmed: locals {:?} open {:?} unlocked uav {:?}", locals(), open_names(), player_data("killstreakUnlocked.uav"));
        }
    }
    render("cas_after");
    let mut diag = diagnose(); diag.sort(); diag.dedup();
    for l in diag.iter().take(30) { eprintln!("   diag: {l}"); }
}

/// MENU=menu_cas_popup: the focusable items with their text and action script.
#[test]
#[ignore = "needs the MW2 install"]
fn print_menu_actions() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    set_player_data("experience", "2516000");
    let menu = std::env::var("MENU").unwrap_or("menu_cas_popup".into());
    assert!(open_menu(&menu));
    with(|rt, d| {
        let f = focusable(rt, d, &menu);
        for (i, it) in d.menus[&menu].items.iter().enumerate() {
            if !f.contains(&i) { continue; }
            eprintln!("item {i} '{}' text {:?} textexp {:?}", it.name, it.text, it.text_exp.as_ref().map(|e| e.raw.chars().take(120).collect::<String>()));
            if let Some(s) = scripts_of(&it.scripts, ACTION) { print_stmts(s, 4); }
        }
    });
}

/// Create-a-Streak with the mouse: click the UAV and Care Package rows where they're drawn.
#[test]
#[ignore = "needs the MW2 install"]
fn create_a_streak_mouse() {
    unsafe { assert!(crate::mw2_load_weapons(COMMON_MP.as_ptr(), COMMON_MP.len()) > 1000) };
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    init(dir, None).unwrap();
    set_player_data("experience", "2516000");
    assert!(open_menu("menu_cas_popup"));
    let locals = || with(|rt, _| ["ui_streak1name", "ui_streak2name", "ui_streak3name", "ui_numstreaks"].iter().map(|k| rt.locals.get(*k).map(operand_text).unwrap_or_default()).collect::<Vec<_>>());
    let mut t = 1000;
    let mut press = |x: f32, y: f32| {
        t += 50; let _ = step(1920.0, 1080.0, t, x, y, 0);
        t += 50; let _ = step(1920.0, 1080.0, t, x, y, KEY_CLICK);
        t += 50; let _ = step(1920.0, 1080.0, t, x, y, 0);
    };
    press(1217.0, 518.0); // the popup's OK
    eprintln!("open {:?} locals {:?}", open_names(), locals());
    for (label, x, y) in [("uav", 450.0, 270.0), ("care package", 450.0, 315.0), ("uav again", 450.0, 270.0), ("counter-uav (locked)", 450.0, 360.0)] {
        press(x, y);
        let hover = with(|rt, _| rt.open("menu_cas_popup").and_then(|m| m.hover));
        eprintln!("{label}: hover {:?} locals {:?} open {:?}", hover, locals(), open_names());
    }
    if open_names().iter().any(|m| m == "popup_unlockconfirm") {
        let yes = with(|_, d| d.menus["popup_unlockconfirm"].items.iter().position(|it| it.name == "button_yes")).flatten().unwrap();
        with(|rt, d| { set_focus(rt, d, "popup_unlockconfirm", Some(yes), 0); activate(rt, d, "popup_unlockconfirm", yes); });
        let _ = step(1920.0, 1080.0, 99_000, -1.0, -1.0, 0);
        eprintln!("unlock yes: counter_uav unlocked {:?} locals {:?} open {:?}", player_data("killstreakUnlocked.counter_uav"), locals(), open_names());
    }
}

/// MENU=name MENU_ZONE=zone.ff: every item of a menu - rect, alignment, material, colours and its
/// visibility / text / material expressions (HUD overlays have no scripts, so print_scripts skips them).
#[test]
#[ignore = "needs the MW2 install"]
fn print_menu_items() {
    let dir = std::path::Path::new(COMMON_MP).parent().unwrap();
    let zone = std::env::var("MENU_ZONE").unwrap_or("code_post_gfx_mp.ff".into());
    let (s, _) = mw2data::scripts_from_file(&dir.join(zone)).unwrap();
    let menu = std::env::var("MENU").unwrap_or("javelin_overlay_hd".into());
    for (n, r, h, v) in &s.menus {
        if *n == menu {
            eprintln!("MENU {n} rect {r:?} align {h} {v}");
        }
    }
    for (i, it) in s.menu_items.iter().filter(|i| i.menu == menu).enumerate() {
        eprintln!("{i:2} '{}' type {} style {} rect {:?} h{} v{} fore {:?} bg '{}' text_scale {}", it.name, it.item_type, it.style, it.rect, it.horz_align, it.vert_align, it.fore_color, it.background, it.text_scale);
        if !it.vis_exp.is_empty() { eprintln!("     visible: {}", dec_hex(&it.vis_exp)); }
        if !it.text_exp.is_empty() { eprintln!("     text: {}", dec_hex(&it.text_exp)); }
        if !it.material_exp.is_empty() { eprintln!("     material: {}", dec_hex(&it.material_exp)); }
        for (k, e) in &it.float_exps { eprintln!("     float[{k}]: {}", dec_hex(e)); }
    }
}
