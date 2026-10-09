//! `WeaponGeometry` → `weapon_iw4::WeaponCombatFacts`, the type MW2's fire/reload/spread
//! code runs on. Mirrors IW4L's two-step path (`asset_game::WeaponCatalog::capture`
//! copying geometry into `WeaponBodyFacts`, then `session::combat_table::validated_facts`)
//! collapsed into one function, for IW4 only (no dual-wield, no dual mags, as IW4L).

use fastfile_iw4::WeaponGeometry;
use weapon_iw4::{
    CapturedCombatInput, FireType, LOCATION_DAMAGE_IDENTITY, MissingCombatFacts, WeaponCombatFacts,
    bake_location_damage, location_damage_is_valid,
};

pub fn combat_facts(g: &WeaponGeometry) -> Result<WeaponCombatFacts, MissingCombatFacts> {
    let fire_type = FireType::from_i32(g.fire_type).map_err(|_| MissingCombatFacts::UnknownFireType)?;
    let segmented_reload = g.segmented_reload && g.reload_start_time_ms > 0;
    let reload_ammo_add = if g.reload_ammo_add > 0 {
        g.reload_ammo_add
    } else if segmented_reload {
        1
    } else {
        0
    };
    if let Some(table) = g.location_damage_mult {
        if !location_damage_is_valid(&table) {
            return Err(MissingCombatFacts::LocationDamage);
        }
    }
    WeaponCombatFacts::try_from_captured(CapturedCombatInput {
        dual_wield: false,
        fire_time_ms: g.fire_time_ms,
        fire_delay_ms: g.fire_delay_ms,
        raise_time_ms: g.raise_time_ms,
        drop_time_ms: g.drop_time_ms,
        alternate_weapon: 0,
        alternate_raise_time_ms: g.alternate_raise_time_ms,
        alternate_drop_time_ms: g.alternate_drop_time_ms,
        reload_time_ms: g.reload_time_ms,
        reload_empty_time_ms: g.reload_empty_time_ms,
        clip_size: g.clip_size,
        // IW4 capture never sets ammo_count_clip_relative, so these are plain round counts.
        start_ammo: g.start_ammo,
        max_ammo: g.max_ammo,
        ammo_index: g.ammo_index,
        clip_index: g.clip_index,
        fire_type: g.fire_type,
        weap_type: g.weap_type,
        weap_class: g.weap_class,
        player_anim_type: g.player_anim_type,
        inventory_type: g.inventory_type,
        impact_type: g.impact_type,
        shots_per_fire: g.shots_per_fire,
        burst_cooldown_ms: if fire_type.is_burst() { 200 } else { 0 },
        bolt_action: g.bolt_action,
        rechamber_time_ms: g.rechamber_time_ms,
        rechamber_bolt_time_ms: g.rechamber_bolt_time_ms,
        rechamber_bolt_delay_ms: g.rechamber_bolt_delay_ms,
        segmented_reload,
        reload_start_time_ms: g.reload_start_time_ms,
        reload_end_time_ms: g.reload_end_time_ms,
        reload_ammo_add,
        reload_add_time_ms: g.reload_add_time_ms,
        reload_empty_add_time_ms: 0,
        reload_start_add_time_ms: g.reload_start_add_time_ms,
        reload_start_add: g.reload_start_add,
        no_partial_reload: g.no_partial_reload,
        dual_mag: None,
        inherits_perks: g.inherits_perks,
        sprint_raise_time_ms: g.sprint_raise_time_ms,
        sprint_drop_time_ms: g.sprint_drop_time_ms,
        stunned_start_time_ms: g.stunned_start_time_ms,
        stunned_end_time_ms: g.stunned_end_time_ms,
        damage: g.damage,
        min_damage: g.min_damage,
        max_damage_range: g.max_damage_range,
        min_damage_range: g.min_damage_range,
        hip_spread_stand_min: g.hip_spread_stand_min,
        hip_spread_ducked_min: g.hip_spread_ducked_min,
        hip_spread_prone_min: g.hip_spread_prone_min,
        hip_spread_stand_max: g.hip_spread_stand_max,
        hip_spread_ducked_max: g.hip_spread_ducked_max,
        hip_spread_prone_max: g.hip_spread_prone_max,
        hip_spread_decay_rate: g.hip_spread_decay_rate,
        hip_spread_fire_add: g.hip_spread_fire_add,
        hip_spread_turn_add: g.hip_spread_turn_add,
        hip_spread_move_add: g.hip_spread_move_add,
        hip_spread_ducked_decay: g.hip_spread_ducked_decay,
        hip_spread_prone_decay: g.hip_spread_prone_decay,
        ads_spread: g.ads_spread,
        aim_down_sight: g.aim_down_sight,
        no_ads_when_mag_empty: g.no_ads_when_mag_empty,
        ads_reload_trans_time_ms: g.ads_reload_trans_time_ms,
        ads_in_rate: g.ads_in_rate,
        ads_out_rate: g.ads_out_rate,
        rechamber_while_ads: g.rechamber_while_ads,
        ads_fire_only: g.ads_fire_only,
        melee_damage: g.melee_damage,
        can_hold_breath: g.overlay_reticle != 0 && g.weap_class != 11,
        overlay_reticle: g.overlay_reticle,
        melee_time_ms: g.melee_time_ms,
        melee_delay_ms: g.melee_delay_ms,
        melee_charge_time_ms: g.melee_charge_time_ms,
        melee_charge_delay_ms: g.melee_charge_delay_ms,
        melee_charge_anim: false,
        knife_model: g.knife_model,
        quick_raise_time_ms: g.quick_raise_time_ms,
        quick_drop_time_ms: g.quick_drop_time_ms,
        select_requires_ammo: g.select_requires_ammo,
        offhand_hold_is_cancelable: g.offhand_hold_is_cancelable,
        ads_gun_kick_reduced_kick_bullets: g.kick.ads_gun_kick_reduced_kick_bullets,
        hip_gun_kick_reduced_kick_bullets: g.kick.hip_gun_kick_reduced_kick_bullets,
        location_damage: bake_location_damage(
            g.weap_type,
            g.weap_class,
            LOCATION_DAMAGE_IDENTITY,
            g.location_damage_mult,
        ),
    })
}
