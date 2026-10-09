using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Mw2Missile
    {
        public uint id, weapon;
        public fixed float origin[3];
        public fixed float quat[4];   // IW4 axes, xyzw
        public fixed float velocity[3];
        public byte state, kind, pad0, pad1; // state 0 flying 1 resting 2 stuck; kind 0 grenade 1 rocket 2 placed 3 knife
        public int fuseMsLeft;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Mw2MissileEvent
    {
        public byte kind, surface, pad0, pad1; // 0 launch 1 bounce 2 stick 3 explode 4 dud 5 cookoff
        public uint id, weapon;
        public fixed float origin[3];
        public fixed float normal[3];
        public float radius, radiusMin, innerDamage, outerDamage;
    }

    /// MW2's grenades, launcher rounds and equipment, simulated by mw2sim (IW4 physics: bounce,
    /// stick, fuse, impact explode) and shown here: the projectile model in flight with its MW2
    /// trail effect, bounce sounds, and explosions with the impact table's per-surface effect
    /// (grenade_explode / rocket_explode rows) as RoR2 blasts at MW2's radius and damage.
    static unsafe class Mw2Projectiles
    {
        class Live { public GameObject model; public uint trail; public string trailFx; public bool seen; public string name; public int frames, shown; public float minCam = float.MaxValue; public Vector3 simPos; public float sinceTick; }
        static readonly Dictionary<uint, Live> live = new Dictionary<uint, Live>();
        /// Pilot: the highest a projectile has flown (world y), reset by the step that reads it.
        public static float PilotMaxY = float.MinValue;
        static readonly Mw2Missile[] missiles = new Mw2Missile[64];
        static readonly Mw2MissileEvent[] events = new Mw2MissileEvent[64];
        static readonly byte[] str = new byte[160], str2 = new byte[160];

        public static void Update(IntPtr sim, CharacterBody body)
        {
            if (sim == IntPtr.Zero || body == null) return;
            uint n;
            fixed (Mw2Missile* m = missiles) n = Native.mw2_missiles(sim, m, (uint)missiles.Length);
            foreach (var l in live.Values) l.seen = false;
            for (int i = 0; i < n; i++)
            {
                ref var m = ref missiles[i];
                if (!live.TryGetValue(m.id, out var l)) live[m.id] = l = Spawn(m.weapon, body);
                l.seen = true;
                var pos = Space.ToUnity(new Vec3f(m.origin[0], m.origin[1], m.origin[2]));
                if (Mw2Pilot.Active && pos.y > PilotMaxY) PilotMaxY = pos.y;
                var rot = Mw2Rot(m.quat[0], m.quat[1], m.quat[2], m.quat[3]);
                // The sim moves projectiles on physics ticks (~50 Hz); at higher frame rates a thrown
                // knife or grenade stepped. Between ticks it glides on along its velocity.
                if (pos != l.simPos) { l.simPos = pos; l.sinceTick = 0f; } else l.sinceTick += Time.deltaTime;
                if (m.state == 0)
                {
                    var v = Space.DirToUnity(new Vec3f(m.velocity[0], m.velocity[1], m.velocity[2])) * Space.Scale;
                    pos += v * Mathf.Min(l.sinceTick, 0.05f);
                }
                if (l.model != null)
                {
                    l.model.SetActive((m.pad0 & 1) == 0); // flags bit 0: still in MW2's launch no-draw window
                    l.model.transform.SetPositionAndRotation(pos, rot);
                }
                l.frames++;
                if (l.model != null && l.model.activeSelf) l.shown++;
                var cam = Camera.main;
                if (cam != null) l.minCam = Mathf.Min(l.minCam, Vector3.Distance(cam.transform.position, pos));
                var vel = Space.DirToUnity(new Vec3f(m.velocity[0], m.velocity[1], m.velocity[2]));
                if (Mw2Pilot.Active && l.frames % 10 == 1 && cam != null)
                {
                    var vp = cam.WorldToViewportPoint(pos);
                    Plugin.Log.LogInfo($"[proj] {l.name} f{l.frames}: at {pos} viewport ({vp.x:F2},{vp.y:F2},{vp.z:F0}) vel {vel * Space.Scale} state {m.state}");
                }
                if (l.trailFx != null && m.state == 0)
                {
                    var back = vel.sqrMagnitude > 1e-4f ? -vel.normalized : rot * Vector3.back;
                    if (l.trail == 0 || !Mw2Fx.Move(l.trail, pos, back)) l.trail = Mw2Fx.Play(l.trailFx, pos, back);
                }
                else if (l.trail != 0) { Mw2Fx.Stop(l.trail); l.trail = 0; }
                // The Tactical Insertion flare at rest is the spawn point (the planted prop takes over).
                if (m.state != 0 && m.weapon == Plugin.Instance.Bridge.TiFlare)
                {
                    Mw2TacticalInsertion.Landed(body, pos);
                    Native.mw2_missile_remove(sim, m.id);
                    continue;
                }
                // Rockets and impact rounds go off on a monster, not only the world.
                if (m.state == 0 && (m.kind == 1 || m.kind == 3) && TouchesEnemy(body, pos)) { if (struck.Count > 64) struck.Clear(); struck.Add(m.id); Native.mw2_missile_detonate(sim, m.id); }
            }
            var gone = new List<uint>();
            foreach (var kv in live) if (!kv.Value.seen) gone.Add(kv.Key);
            foreach (var id in gone) Despawn(id);

            fixed (Mw2MissileEvent* e = events) n = Native.mw2_missile_events(sim, e, (uint)events.Length);
            for (int i = 0; i < n; i++) OnEvent(ref events[i], body, sim);
        }

        static readonly Dictionary<uint, bool> equipment = new Dictionary<uint, bool>();

        /// Thrown or placed explosives (not the launchers, which share their weapon class).
        static bool IsEquipment(uint weapon)
        {
            if (equipment.TryGetValue(weapon, out bool e)) return e;
            string n = Native.WeaponString(weapon, 2);
            e = n.StartsWith("frag_grenade") || n.StartsWith("semtex") || n.StartsWith("c4") || n.StartsWith("claymore");
            equipment[weapon] = e;
            return e;
        }

        /// Projectiles TouchesEnemy set off: their explode event is a hit on a body. (Not cleared on
        /// despawn: the sim can drop the missile the tick before its explode event comes.)
        static readonly HashSet<uint> struck = new HashSet<uint>();

        static void OnEvent(ref Mw2MissileEvent e, CharacterBody body, IntPtr sim)
        {
            var at = Space.ToUnity(new Vec3f(e.origin[0], e.origin[1], e.origin[2]));
            var normal = Space.DirToUnity(new Vec3f(e.normal[0], e.normal[1], e.normal[2]));
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.up;
            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[proj] event {e.kind} {Native.WeaponString(e.weapon, 2)} surface {e.surface} (flesh {Mw2Gunfire.FleshSurface}) at {at} radius {e.radius} inner {e.innerDamage}");
            switch (e.kind)
            {
                case 2: // stick
                    // A throwing knife that stuck in a body: its impact damage there (MW2 kills with
                    // it), then it's gone (playtest 10-04-26: the knife did nothing - since projectile
                    // traces strike monsters it sticks in them instead of going off).
                    if (e.surface == Mw2Gunfire.FleshSurface && e.radius <= 0f && Native.mw2_weapon_class(e.weapon) == 9 // WEAPCLASS_THROWINGKNIFE
                        && Mw2Melee.ThrownKnife(body, e.weapon, at, -normal))
                    {
                        if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[pilot] direct hit {Native.WeaponString(e.weapon, 2)} (stuck)");
                        Native.mw2_missile_remove(sim, e.id);
                        Despawn(e.id);
                    }
                    break;
                case 1: // bounce
                    var bounce = BounceSound(e.weapon, e.surface);
                    if (bounce != null) Mw2Gunfire.ImpactSound?.Invoke(bounce, at);
                    break;
                case 3: // explode
                case 5: // cook-off
                    if (e.weapon == Plugin.Instance.Bridge.TiFlare) { Mw2TacticalInsertion.Landed(body, at); Despawn(e.id); break; }
                    string fx = ExplosionFx(e.weapon, e.surface);
                    // The throwing knife: no blast, its 135 impact where it struck (the sim reports it as
                    // inner damage with radius 0). Its explosion type (3, NONE) is also the concussion's,
                    // which read it as a tactical with no damage at all (playtest 10-04-26: the knife did nothing).
                    bool blade = e.radius <= 0f && e.innerDamage > 0f;
                    bool tactical = !blade && (e.pad1 == 2 || e.pad1 == 3 || e.pad1 == 5); // flash / concussion / smoke: no damage blast
                    // Danger Close, and the extra every MW2 round gets (DamageMultiplier: a launcher is a gun).
                    float dc = Mw2Perks.ExplosiveDamage(body) * Mathf.Max(Plugin.Instance.DamageMultiplier.Value, 0f) * Mathf.Max(Plugin.Instance.ExplosiveDamageScale.Value, 0f);
                    if (IsEquipment(e.weapon)) dc *= Mathf.Max(Plugin.Instance.EquipmentDamageScale.Value, 0f); // frag / Semtex / C4 / claymore
                    // A body hit: the sim reports flesh, or our TouchesEnemy set it off on a monster.
                    bool onBody = struck.Remove(e.id) || e.surface == Mw2Gunfire.FleshSurface;
                    if (blade)
                    {
                        // Kills like the knife in hand (Mw2Melee.ThrownKnife); in the world it does nothing.
                        bool struckOne = Mw2Melee.ThrownKnife(body, e.weapon, at, -normal);
                        if (fx != null) Mw2Fx.Play(fx, at, normal);
                        if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[pilot] direct hit {Native.WeaponString(e.weapon, 2)}: struck {struckOne} (on body {onBody})");
                    }
                    else
                    {
                        // A direct hit first deals the weapon's own damage there, as MW2 does (RPG / AT4 /
                        // Stinger 1000, grenade launchers 135), then the splash.
                        if (!tactical && onBody && Native.mw2_weapon_equipment(e.weapon, out var eq) == 1 && eq.impact_damage > 0)
                        {
                            float impact = eq.impact_damage * dc;
                            Mw2Challenges.With(Mw2Challenges.Cause.Explosive, () => Mw2Strike.Blast(body, at, 40f, impact, impact, false));
                            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[pilot] direct hit {Native.WeaponString(e.weapon, 2)}: {eq.impact_damage} MW2 damage");
                        }
                        if (!tactical && e.radius > 0f && e.innerDamage > 0f)
                        {
                            float radius = e.radius, inner = e.innerDamage * dc, outer = e.outerDamage * dc;
                            Mw2Challenges.With(Mw2Challenges.Cause.Explosive, () => Mw2Strike.Blast(body, at, radius, inner, outer, true, fx));
                        }
                        else if (fx != null) Mw2Fx.Play(fx, at, normal);
                    }
                    Tactical(e.pad1, at, e.radius, e.radiusMin, body);
                    // A Care Package / Emergency Airdrop / Sentry marker: the drop comes here.
                    if (Native.WeaponString(e.weapon, 2).EndsWith("_marker_mp")) Plugin.Instance.Bridge.Streaks.MarkerLanded(at);
                    ExplosionSound(e.weapon, e.surface, e.pad1, at);
                    Despawn(e.id);
                    break;
                case 4: // dud: lands without going off
                    break;
            }
        }

        static Live Spawn(uint weapon, CharacterBody body)
        {
            var l = new Live();
            uint n;
            fixed (byte* a = str) fixed (byte* b = str2) n = Native.mw2_weapon_projectile(weapon, a, (uint)str.Length, b, (uint)str2.Length);
            if (n > 0)
            {
                string model = System.Text.Encoding.UTF8.GetString(str, 0, (int)Math.Min(n, (uint)str.Length)).TrimEnd((char)0);
                l.name = model;
                l.model = Mw2Prop.Build(model, body, Space.Scale);
                var r = l.model != null ? l.model.GetComponent<MeshRenderer>() : null;
                var mats = r != null ? string.Join(",", Array.ConvertAll(r.sharedMaterials, x => x == null ? "null" : $"{x.shader.name}:{(x.mainTexture != null ? x.mainTexture.name : "notex")}")) : "-";
                Plugin.Log.LogInfo($"[proj] spawn {model}: {(l.model == null ? "NO MODEL" : $"size {r.bounds.size} layer {l.model.layer}")} mats {mats}");
            }
            else Plugin.Log.LogInfo($"[proj] spawn weapon {weapon}: no projectile model name");
            int t = Array.IndexOf(str2, (byte)0);
            string trail = System.Text.Encoding.UTF8.GetString(str2, 0, t < 0 ? str2.Length : t);
            l.trailFx = string.IsNullOrEmpty(trail) ? null : trail;
            if (l.model != null && l.trailFx != null) Mw2Prop.SetTrail(l.model, l.trailFx);
            Array.Clear(str2, 0, str2.Length);
            return l;
        }

        /// A weapon's projectile model and trail effect names (weapon def), or nulls.
        public static void ProjectileOf(uint weapon, out string model, out string trail)
        {
            model = trail = null;
            uint n;
            fixed (byte* a = str) fixed (byte* b = str2) n = Native.mw2_weapon_projectile(weapon, a, (uint)str.Length, b, (uint)str2.Length);
            if (n > 0) model = System.Text.Encoding.UTF8.GetString(str, 0, (int)Math.Min(n, (uint)str.Length)).TrimEnd((char)0);
            int t = Array.IndexOf(str2, (byte)0);
            string tr = System.Text.Encoding.UTF8.GetString(str2, 0, t < 0 ? str2.Length : t);
            trail = string.IsNullOrEmpty(tr) ? null : tr;
            Array.Clear(str2, 0, str2.Length);
        }

        /// A weapon's explosion (its MW2 effect and sound) with no projectile, e.g. Martyrdom's frag.
        public static void Explosion(uint weapon, Vector3 at)
        {
            string fx = ExplosionFx(weapon, 0);
            if (fx != null) Mw2Fx.Play(fx, at, Vector3.up);
            ExplosionSound(weapon, 0, 0, at);
        }

        /// IW4: the weapon's projExplosionSound (C4, flashbang, smoke) else the client's per-surface
        /// grenade_explode_<surface> (default when the surface has none); rockets add their layer.
        static void ExplosionSound(uint weapon, int surface, byte explosionType, Vector3 at)
        {
            string boom;
            uint n;
            fixed (byte* p = str) n = Native.mw2_weapon_projectile_sound(weapon, 0, p, (uint)str.Length);
            boom = n > 0 ? System.Text.Encoding.UTF8.GetString(str, 0, (int)Math.Min(n, (uint)str.Length)) : null;
            if (boom == null && (explosionType == 0 || explosionType == 1))
            {
                string bySurface = "grenade_explode_" + Mw2Gunfire.SurfaceName(surface);
                boom = Native.AliasExists(bySurface) ? bySurface : "grenade_explode_default";
            }
            if (boom != null) Mw2Gunfire.ImpactSound?.Invoke(boom, at);
            if (explosionType == 1) Mw2Gunfire.ImpactSound?.Invoke("rocket_explode_layer", at);
        }

        static void Despawn(uint id)
        {
            if (!live.TryGetValue(id, out var l)) return;
            Plugin.Log.LogInfo($"[proj] gone {l.name}: {l.frames} frames, drawn {l.shown}, closest to camera {l.minCam:F1} m");
            if (l.model != null) UnityEngine.Object.Destroy(l.model);
            if (l.trail != 0) Mw2Fx.Stop(l.trail);
            live.Remove(id);
        }

        /// MW2's tacticals on RoR2 enemies: flash (explosion_type 2) stuns, concussion (type 3
        /// with no damage) slows, both shorter at the edge of the radius (MW2: full effect inside
        /// radius_min). Smoke (5) is just its MW2 smoke cloud.
        static void Tactical(byte explosionType, Vector3 at, float radiusUnits, float minUnits, CharacterBody body)
        {
            if (radiusUnits <= 0f || (explosionType != 2 && explosionType != 3)) return;
            // Stuns and slows are the host's to give: a client's flash goes through it.
            StreakHost.Run(explosionType == 2 ? StreakHost.Flash : StreakHost.Concussion, body, at, radiusUnits * Space.Scale, minUnits * Space.Scale);
        }

        /// Host: a flash (stun) or concussion (slow) of radius `r` (full inside `rMin`) metres.
        public static void ApplyTactical(bool flash, Vector3 at, float r, float rMin, CharacterBody body)
        {
            if (!UnityEngine.Networking.NetworkServer.active || r <= 0f) return;
            byte explosionType = (byte)(flash ? 2 : 3);
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (!Mw2Strike.IsEnemy(body, cb)) continue;
                float d = Vector3.Distance(cb.corePosition, at);
                if (d > r) continue;
                float k = d <= rMin ? 1f : Mathf.InverseLerp(r, rMin, d);
                if (explosionType == 2)
                {
                    var stun = cb.GetComponent<SetStateOnHurt>();
                    // Longer than first tuned (playtest 10-05-26: stun and slow for a bit longer).
                    if (stun != null && stun.canBeStunned) stun.SetStun(Mathf.Lerp(3f, 6.5f, k));
                }
                else cb.AddTimedBuff(RoR2Content.Buffs.Slow80, Mathf.Lerp(4f, 9f, k));
            }
        }

        public static void Clear()
        {
            foreach (var id in new List<uint>(live.Keys)) Despawn(id);
        }

        static bool TouchesEnemy(CharacterBody me, Vector3 at)
        {
            foreach (var hb in Physics.OverlapSphere(at, 0.4f, LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Collide))
            {
                var h = hb.GetComponent<HurtBox>();
                if (h != null && h.healthComponent != null && h.healthComponent.body != null && Mw2Strike.IsEnemy(me, h.healthComponent.body)) return true;
            }
            return false;
        }

        /// The impact table's explosion row for the weapon (grenade_explode / rocket_explode ...),
        /// on the surface it went off on.
        static string ExplosionFx(uint weapon, int surface)
        {
            uint own;
            fixed (byte* p = str) own = Native.mw2_weapon_fx(weapon, 6, p, (uint)str.Length);
            if (own > 0) return System.Text.Encoding.UTF8.GetString(str, 0, (int)Math.Min(own, (uint)str.Length));
            uint len;
            fixed (byte* p = str) len = Native.mw2_impact_fx((uint)Math.Max(Native.mw2_weapon_impact_type(weapon), 0), (uint)surface, p, (uint)str.Length);
            return len > 0 ? System.Text.Encoding.UTF8.GetString(str, 0, (int)Math.Min(len, (uint)str.Length)) : null;
        }

        static readonly Dictionary<long, string> bounceNames = new Dictionary<long, string>();
        static string BounceSound(uint weapon, int surface)
        {
            long key = ((long)weapon << 8) | (uint)surface;
            if (bounceNames.TryGetValue(key, out var s)) return s;
            uint len;
            fixed (byte* p = str) len = Native.mw2_weapon_bounce_sound(weapon, (uint)surface, p, (uint)str.Length);
            s = len > 0 ? System.Text.Encoding.UTF8.GetString(str, 0, (int)Math.Min(len, (uint)str.Length)) : null;
            bounceNames[key] = s;
            return s;
        }

        /// IW4 quaternion (IW4 axes) -> Unity rotation (the same mirror the models use).
        static Quaternion Mw2Rot(float x, float y, float z, float w) => new Quaternion(y, -z, -x, w);
    }
}
