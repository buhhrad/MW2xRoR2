//! MW2 view kick (recoil) — port of IW4L's `render_anim::anim::view_kick_state`
//! driven by `weapon_iw4`'s retail kick functions. Kick is an additive, self-centering
//! offset on the view; the host adds it to its camera.

use fastfile_iw4::WeaponKickCapture;
use weapon_iw4::{
    FireRecoilPsScales, GunKickRange, ViewKickRange, fire_recoil_gun_range, fire_recoil_view_range,
    kick_angles, kick_angles_center_speed, weapon_fire_recoil,
};

#[derive(Clone, Debug)]
pub struct ViewKick {
    pub kick_avel: [f32; 3],
    pub kick_angles: [f32; 3],
    rng: u32,
}

impl Default for ViewKick {
    fn default() -> Self {
        Self { kick_avel: [0.0; 3], kick_angles: [0.0; 3], rng: 0xA341_316C }
    }
}

impl ViewKick {
    fn unit01(&mut self) -> f32 {
        let mut x = self.rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        self.rng = if x == 0 { 1 } else { x };
        x as f32 / u32::MAX as f32
    }

    pub fn seed_fire(&mut self, k: &WeaponKickCapture, weapon_pos_frac: f32, weap_flags: u32, recoil_scale: i32, reduce_window_active: bool) {
        let view = fire_recoil_view_range(
            weapon_pos_frac,
            ViewKickRange { pitch_min: k.hip_view_kick_pitch_min, pitch_max: k.hip_view_kick_pitch_max, yaw_min: k.hip_view_kick_yaw_min, yaw_max: k.hip_view_kick_yaw_max },
            ViewKickRange { pitch_min: k.ads_view_kick_pitch_min, pitch_max: k.ads_view_kick_pitch_max, yaw_min: k.ads_view_kick_yaw_min, yaw_max: k.ads_view_kick_yaw_max },
        );
        let gun = fire_recoil_gun_range(
            weapon_pos_frac,
            GunKickRange { pitch_min: k.hip_gun_kick_pitch_min, pitch_max: k.hip_gun_kick_pitch_max, yaw_min: k.hip_gun_kick_yaw_min, yaw_max: k.hip_gun_kick_yaw_max },
            GunKickRange { pitch_min: k.ads_gun_kick_pitch_min, pitch_max: k.ads_gun_kick_pitch_max, yaw_min: k.ads_gun_kick_yaw_min, yaw_max: k.ads_gun_kick_yaw_max },
        );
        let reduced_percent = if !reduce_window_active {
            0.0
        } else if weapon_pos_frac == 1.0 {
            k.ads_gun_kick_reduced_kick_percent
        } else {
            k.hip_gun_kick_reduced_kick_percent
        };
        let ps = FireRecoilPsScales { reduce_window_active, reduced_percent, weap_flags, recoil_scale };
        let unit01 = [self.unit01(), self.unit01(), self.unit01(), self.unit01()];
        self.kick_avel = weapon_fire_recoil(view, gun, ps, unit01).kick_avel;
    }

    pub fn advance(&mut self, k: &WeaponKickCapture, weapon_pos_frac: f32, msec: i32) {
        if msec <= 0 {
            return;
        }
        let center = kick_angles_center_speed(1, weapon_pos_frac, k.f_hip_view_kick_center_speed, k.f_ads_view_kick_center_speed);
        kick_angles(&mut self.kick_angles, &mut self.kick_avel, msec, center);
    }
}
