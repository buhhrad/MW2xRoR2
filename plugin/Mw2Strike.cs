using System.Collections.Generic;
using System.Linq;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// Shared killstreak parts. All damage is RoR2 damage from the player's body (BlastAttack /
    /// BulletAttack, proc coefficient 1), so items proc and kills count as the player's.
    /// MW2 numbers come in MW2 units and MW2 damage; Space.Scale and DamageReference convert.
    static class Mw2Strike
    {
        public static float Damage(CharacterBody body, float mw2Damage) =>
            body.damage * mw2Damage / Mathf.Max(Plugin.Instance.DamageReference.Value, 1f);

        /// MW2 RadiusDamage(radius, max, min) as RoR2 blasts: `outer` everywhere in the radius plus
        /// (`inner` - `outer`) falling off linearly to the edge = MW2's inner-to-outer line. One RoR2
        /// linear blast of `inner` went to 0 at the edge and gave a launcher a fraction of MW2's splash
        /// (playtest 10-04-26). The flat part carries the procs; the falloff part adds damage only.
        public static void Blast(CharacterBody body, Vector3 at, float radiusUnits, float inner, float outer, bool fx = true, string mw2Fx = null, bool killstreak = false)
        {
            if (body == null) return;
            if (killstreak) { float k = Mw2Perks.KillstreakExplosiveDamage(body); inner *= k; outer *= k; } // Danger Close Pro
            outer = Mathf.Clamp(outer, 0f, inner);
            float radius = radiusUnits * Space.Scale;
            if (body.hasEffectiveAuthority)
            {
                bool crit = body.RollCrit();
                var team = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.Player;
                if (outer > 0f) Fire(body, team, at, radius, Damage(body, outer), BlastAttack.FalloffModel.None, 1f, crit, 1500f);
                if (inner > outer) Fire(body, team, at, radius, Damage(body, inner - outer), BlastAttack.FalloffModel.Linear, outer > 0f ? 0f : 1f, crit, outer > 0f ? 0f : 1500f);
            }
            // MW2's own effect when the strike names one, else RoR2's explosion.
            if (fx && (mw2Fx == null || Mw2Fx.Play(mw2Fx, at, Vector3.up) == 0)) ExplosionFx(at, radius);
        }

        static void Fire(CharacterBody body, TeamIndex team, Vector3 at, float radius, float damage, BlastAttack.FalloffModel falloff, float proc, bool crit, float force)
        {
            new BlastAttack
            {
                attacker = body.gameObject,
                inflictor = body.gameObject,
                teamIndex = team,
                baseDamage = damage,
                baseForce = force,
                position = at,
                radius = radius,
                falloffModel = falloff,
                procCoefficient = proc,
                crit = crit,
                damageColorIndex = DamageColorIndex.Default,
                damageType = DamageType.Generic,
            }.Fire();
        }

        static GameObject tracer, hitspark;

        /// One MW2 bullet (turret, helicopter gun) as a RoR2 BulletAttack.
        /// Killstreak rounds fired / landing on an enemy (the latter counted in pilot runs).
        public static int StreakRounds, StreakRoundHits;

        public static void Bullet(CharacterBody body, Vector3 origin, Vector3 dir, float mw2Damage, float rangeUnits, float spreadDeg = 0f, bool ror2Tracer = true)
        {
            if (body == null || !body.hasEffectiveAuthority) return;
            // Spread and the tracer are done here: BulletAttack draws its tracer from the
            // "weapon" object (the player's gun, or a networked object it can serialize), so
            // turret and aircraft rounds drew from the player. Spawned by hand it starts at
            // the turret / aircraft gun.
            if (spreadDeg > 0f) dir = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), dir) * Quaternion.AngleAxis(UnityEngine.Random.Range(0f, spreadDeg), Vector3.Cross(dir, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.Cross(dir, Vector3.up).normalized : Vector3.right) * dir;
            float maxDist = Mathf.Max(rangeUnits * Space.Scale, 10f);
            tracer = tracer ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/Tracers/TracerCommandoDefault");
            hitspark = hitspark ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/HitsparkCommando");
            var attack = new BulletAttack
            {
                owner = body.gameObject,
                origin = origin,
                aimVector = dir,
                minSpread = 0f,
                maxSpread = 0f,
                damage = Damage(body, mw2Damage),
                force = 200f,
                procCoefficient = 1f,
                maxDistance = maxDist,
                isCrit = body.RollCrit(),
                tracerEffectPrefab = null,
                hitEffectPrefab = hitspark,
                falloffModel = BulletAttack.FalloffModel.None,
                radius = 0.2f,
                smartCollision = true,
                // Rounds pass the owner's team: an aircraft fires from inside its own hitbox (a body on
                // the owner's team around the hull), which stopped every Harrier round in hover
                // (playtest 10-04-26: "no damage from its turret").
                filterCallback = BulletAttack.ignoreAlliesFilterCallback,
            };
            // Pilot: count killstreak rounds that land on an enemy (the Harrier's hit nothing).
            if (Mw2Pilot.Active)
            {
                var team = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.Player;
                attack.hitCallback = (BulletAttack ba, ref BulletAttack.BulletHit h) =>
                {
                    if (h.hitHurtBox != null && h.hitHurtBox.teamIndex != team) StreakRoundHits++;
                    return BulletAttack.defaultHitCallback(ba, ref h);
                };
            }
            StreakRounds++;
            attack.Fire();
            tracer = tracer ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/Tracers/TracerCommandoDefault");
            if (tracer != null && ror2Tracer) // the animatic's MW2 soldier draws MW2's own
            {
                var end = Physics.Raycast(origin, dir, out var hit, maxDist, LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore) ? hit.point : origin + dir * maxDist;
                EffectManager.SpawnEffect(tracer, new EffectData { origin = end, start = origin }, true);
            }
        }

        static GameObject explosionFx;
        public static void ExplosionFx(Vector3 at, float radiusMetres)
        {
            if (explosionFx == null)
                foreach (var path in new[] { "Prefabs/Effects/OmniEffect/OmniExplosionVFX", "Prefabs/Effects/OmniEffect/OmniExplosionVFXQuick", "Prefabs/Effects/ImpactEffects/ExplosionVFX" })
                {
                    explosionFx = LegacyResourcesAPI.Load<GameObject>(path);
                    if (explosionFx != null) break;
                }
            if (explosionFx != null) EffectManager.SpawnEffect(explosionFx, new EffectData { origin = at, scale = radiusMetres }, true);
        }

        public static bool IsEnemy(CharacterBody me, CharacterBody other) =>
            other != null && other != me && other.teamComponent != null && me.teamComponent != null &&
            other.teamComponent.teamIndex != me.teamComponent.teamIndex && other.teamComponent.teamIndex != TeamIndex.Neutral &&
            other.healthComponent != null && other.healthComponent.alive && // neutral isn't a side (the showcase's cameraman drew the sentry's fire)
            other.master != null; // props with health (pots, barrels) aren't combatants: no AI / player master (playtest 10-06-26: the Predator boxed pots)

        public static IEnumerable<CharacterBody> Enemies(CharacterBody me)
        {
            foreach (var cb in CharacterBody.readOnlyInstancesList)
                if (IsEnemy(me, cb)) yield return cb;
        }

        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            var d = a - b; d.y = 0f;
            return d.magnitude;
        }

        public static bool Sees(Vector3 from, CharacterBody target)
        {
            var to = target.corePosition;
            var d = to - from;
            return !Physics.Raycast(from, d.normalized, d.magnitude - 0.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
        }

        /// Nearest enemy within range (metres), optionally only ones in line of sight.
        public static CharacterBody Nearest(CharacterBody me, Vector3 from, float rangeMetres, bool sight)
        {
            CharacterBody best = null;
            float bestD = rangeMetres * rangeMetres;
            foreach (var cb in Enemies(me))
            {
                float d = (cb.corePosition - from).sqrMagnitude;
                if (d >= bestD) continue;
                if (sight && !Sees(from, cb)) continue;
                best = cb;
                bestD = d;
            }
            return best;
        }

        /// Ground point under (or along) a ray, for aiming strikes.
        public static Vector3 AimPoint(CharacterBody body, float maxMetres = 120f)
        {
            var bank = body.inputBank;
            int mask = LayerIndex.world.mask;
            if (Physics.Raycast(bank.aimOrigin, bank.aimDirection, out var hit, maxMetres, mask, QueryTriggerInteraction.Ignore)) return hit.point;
            var flat = bank.aimDirection; flat.y = 0f;
            var ahead = body.footPosition + (flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward) * 20f + Vector3.up * 50f;
            return Physics.Raycast(ahead, Vector3.down, out var down, 300f, mask, QueryTriggerInteraction.Ignore) ? down.point : body.footPosition;
        }

        public static Vector3 Ground(Vector3 above)
        {
            // From 50 m up, or from just under a roof that's lower (else the roof's top was 'ground').
            float start = Mathf.Min(above.y + 50f, Ceiling(above, 50f) - 0.5f);
            return Physics.Raycast(new Vector3(above.x, start, above.z), Vector3.down, out var hit, 500f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore) ? hit.point : above;
        }

        /// Where to hover over a fight on a layered stage: of a ring of spots around `centre` (8
        /// headings x `radii`, at `height` and 60% of it over the local ground, clear of terrain), the
        /// one that sees the most enemies within `range` (along the ground) of `me`. MW2's open maps
        /// never needed it; RoR2's floors and overhangs hid whole levels from a hover picked by height
        /// alone (playtest 10-03-26). Ties keep the earlier (higher, nearer) spot.
        public static Vector3 SeeingSpot(CharacterBody me, Vector3 centre, float[] radii, float height, float range, Vector3 fallback, bool openSky = false)
        {
            var enemies = new List<CharacterBody>();
            foreach (var cb in Enemies(me)) if (cb != null && cb.healthComponent != null && cb.healthComponent.alive && FlatDistance(cb.corePosition, centre) < range * 1.5f) enemies.Add(cb);
            if (enemies.Count == 0) return fallback;
            Vector3 best = fallback; int bestN = -1;
            foreach (float r in radii)
                for (int i = 0; i < 8; i++)
                    foreach (float hk in new[] { 1f, 0.6f })
                    {
                        var g = Ground(centre + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * r);
                        var spot = g + Vector3.up * height * hk;
                        float roofY = Ceiling(g);
                        // Aircraft hover in the open: under an arch they ground against its legs
                        // (the Attack Helicopter: 494 wall hits in 20 s on Shattered Abodes, 10-06-26).
                        if (openSky && !float.IsPositiveInfinity(Ceiling(spot, 150f))) continue;
                        if (spot.y > roofY - 4f) spot.y = Mathf.Max(roofY - 4f, g.y + 3f); // stay under a roof
                        if (Physics.CheckSphere(spot, 4f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) continue;
                        int n = 0;
                        foreach (var cb in enemies) if (FlatDistance(cb.corePosition, spot) <= range && Sees(spot, cb)) n++;
                        if (n > bestN) { bestN = n; best = spot; }
                    }
            return best;
        }

        /// `clear` metres over the highest ground on a ring of `radius` around `centre` (and the centre):
        /// an orbit flown at one height that clears what's under it (MW2's getCorrectHeight takes the
        /// highest of several ground traces).
        public static float RingHeight(Vector3 centre, float radius, float clear, bool overRoofs = false)
        {
            if (overRoofs)
            {
                // An orbit over the top of everything on its ring (the Chopper Gunner circled through arch legs).
                float best = float.NegativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    var a = centre + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * radius;
                    var b = centre + Quaternion.Euler(0f, (i + 1) * 45f, 0f) * Vector3.forward * radius;
                    best = Mathf.Max(best, OverAll(a, b, centre.y + clear, clear * 0.5f));
                }
                return best;
            }
            int mask = LayerIndex.world.mask;
            float top = float.NegativeInfinity, roof = float.PositiveInfinity;
            for (int i = 0; i <= 8; i++)
            {
                var p = i == 8 ? centre : centre + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * radius;
                float c = Ceiling(p);
                roof = Mathf.Min(roof, c);
                if (Physics.Raycast(new Vector3(p.x, Mathf.Min(p.y + 1500f, c - 0.5f), p.z), Vector3.down, out var hit, 3000f, mask, QueryTriggerInteraction.Ignore)) top = Mathf.Max(top, hit.point.y);
            }
            float h = (float.IsNegativeInfinity(top) ? centre.y : top) + clear;
            return float.IsPositiveInfinity(roof) ? h : Mathf.Max(Mathf.Min(h, roof - 6f), (float.IsNegativeInfinity(top) ? centre.y : top) + 3f);
        }

        /// MW2 flies drops and strikes at the map's `airstrikeheight` (getFlyHeightOffset), a height
        /// the level sets above its geometry. RoR2 stages have none, so: the highest ground under
        /// the route from `a` to `b` (sampled every 15 m, traced down from far above) plus `clear`
        /// metres, never below `atLeast`. A straight flight at that height never meets a rock.
        public static float RouteHeight(Vector3 a, Vector3 b, float atLeast, float clear = 12f, bool overRoofs = false)
        {
            if (overRoofs) return OverAll(a, b, atLeast, clear);
            int mask = LayerIndex.world.mask;
            float top = float.NegativeInfinity, roof = float.PositiveInfinity;
            a.y = b.y = Mathf.Max(a.y, b.y);
            int n = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / 15f), 1, 400);
            for (int i = 0; i <= n; i++)
            {
                var p = Vector3.Lerp(a, b, i / (float)n);
                // Under a roof (RoR2's caves, tunnels, covered floors) the ground is what's under it,
                // and the flight stays below it; from far above the roof read as the ground.
                float c = Ceiling(p);
                roof = Mathf.Min(roof, c);
                var from = new Vector3(p.x, Mathf.Min(p.y + 1500f, c - 0.5f), p.z);
                if (Physics.Raycast(from, Vector3.down, out var hit, 3000f, mask, QueryTriggerInteraction.Ignore)) top = Mathf.Max(top, hit.point.y);
            }
            float h = float.IsNegativeInfinity(top) ? atLeast : Mathf.Max(atLeast, top + clear);
            return float.IsPositiveInfinity(roof) ? h : Mathf.Max(Mathf.Min(h, roof - Mathf.Min(clear, 6f)), Mathf.Min(top + 3f, roof - 2f));
        }

        /// Planes over the top of everything on the route - arches, overhangs and their legs included
        /// (staying under them threaded jets through Shattered Abodes' arches, 10-06-26): the highest
        /// world surface traced from far above, every 15 m and a little either side, plus `clear`.
        static float OverAll(Vector3 a, Vector3 b, float atLeast, float clear)
        {
            int mask = LayerIndex.world.mask;
            float top = float.NegativeInfinity;
            var along = new Vector3(b.x - a.x, 0f, b.z - a.z);
            var side = along.sqrMagnitude > 1e-4f ? new Vector3(along.z, 0f, -along.x).normalized * 12f : Vector3.zero;
            int n = Mathf.Clamp(Mathf.CeilToInt(along.magnitude / 15f), 1, 400);
            for (int i = 0; i <= n; i++)
            {
                var p = Vector3.Lerp(a, b, i / (float)n);
                foreach (var o in new[] { Vector3.zero, side, -side })
                {
                    var from = new Vector3(p.x + o.x, Mathf.Max(a.y, b.y) + 2000f, p.z + o.z);
                    if (Physics.Raycast(from, Vector3.down, out var hit, 4000f, mask, QueryTriggerInteraction.Ignore)) top = Mathf.Max(top, hit.point.y);
                }
            }
            return float.IsNegativeInfinity(top) ? atLeast : Mathf.Max(atLeast, top + clear);
        }

        /// Terrain following for a flight plan at `cruise`: the height to fly now, clear of the ground
        /// over the next `ahead` metres along `dir` (re-sampled every 0.25 s into `cache`), never below
        /// `cruise`. Drop-zone-only clearance let the little bird plough into hills on the approach.
        public static float FollowHeight(Vector3 at, Vector3 dir, float cruise, float ahead, ref float cache, ref float nextSample)
        {
            if (Time.time >= nextSample)
            {
                nextSample = Time.time + 0.25f;
                var a = new Vector3(at.x, cruise, at.z);
                cache = RouteHeight(a, a + Flat(dir) * ahead, cruise, 12f);
            }
            return Mathf.Max(cruise, cache);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        /// The underside of a roof over `at` (traced up, `maxUp` metres), +inf if open sky.
        public static float Ceiling(Vector3 at, float maxUp = 400f) =>
            Physics.Raycast(at + Vector3.up * 2f, Vector3.up, out var h, maxUp, LayerIndex.world.mask, QueryTriggerInteraction.Ignore) ? h.point.y : float.PositiveInfinity;
    }

    /// A flying MW2 vehicle (jet, helicopter, bomber, AC-130): its MW2 model, placed each frame.
    class Mw2Vehicle : IShootable
    {
        GameObject go;
        public Vector3 Position { get; private set; }
        public Vector3 Forward { get; private set; } = Vector3.forward;

        /// Per-model size on top of VehicleScale. The Pave Low reads right at VehicleScale; the
        /// smaller helicopters read toy-like next to RoR2's megaliths (playtest 10-02-26).
        /// The Attack Helicopter's Cobra and the Pave Low hover right over the fight: at the size boost
        /// for far-off jets (x2, the Cobra x1.5 more) they were ~60 m long and filled the sky (playtest
        /// 10-04-26: "wayyy too big"). They stay MW2-sized.
        static bool HoversOverFight(string model) => model == "vehicle_cobra_helicopter_fly_low" || model == "vehicle_pavelow";

        static float ModelScale(string model) =>
            model.Contains("cobra") || model.Contains("apache") ? 1.5f : model.Contains("little_bird") ? 1.6f : 1f;

        /// The chin turret, when the model has one (Cobra, Apache, Hind, Mi-28).
        public Mw2Turret Turret => go != null ? go.GetComponent<Mw2Turret>() : null;

        /// Max turn rate in deg/s (MW2 setyawspeed); 0 = turn at once (jets on a fixed heading).
        public float YawSpeed;
        float yawVel;

        string model;
        CharacterBody owner;

        /// Another MW2 model linked at one of this one's tags (linkTo( self, tag, (0,0,0), (0,0,0) )),
        /// e.g. the Pave Low's weapon_minigun door guns. Goes with the hull.
        public Transform AttachModel(string childModel, string tag)
        {
            if (go == null) return null;
            var child = Mw2Prop.Build(childModel, owner, Space.Scale);
            if (child == null) return null;
            child.transform.SetParent(go.transform, false);
            child.transform.localPosition = Mw2Prop.TagLocal(model, tag, out var local) ? local : Vector3.zero;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            return child.transform;
        }

        /// World position of one of the model's MW2 tags (tag_gunner_left, tag_flash...) on the hull
        /// as drawn this frame: bank, lean, hover bob and scale included.
        public bool TagPoint(string tag, out Vector3 at)
        {
            at = Position;
            if (go == null || !Mw2Prop.TagLocal(model, tag, out var local)) return false;
            at = go.transform.TransformPoint(local);
            return true;
        }

        public Transform Hull => go != null ? go.transform : null;

        /// `scriptFx`: the effects MW2's scripts put on this model (lights, engines, contrails). The
        /// Emergency Airdrop's C-130 flies with none (_airdrop.gsc doC130FlyBy), the AC-130 with
        /// playAC130Effects, so jobs sharing a model can say which.
        public static Mw2Vehicle Spawn(string model, CharacterBody body, Vector3 at, Vector3 forward, bool scriptFx = true)
        {
            var v = new Mw2Vehicle { go = Mw2Prop.Build(model, body, Space.Scale), model = model, owner = body };
            All.Add(v);
            // RoR2's world is built far bigger than MW2's; true-scale aircraft read as toys.
            if (v.go != null)
            {
                v.go.transform.localScale = model.StartsWith("sentry_") || HoversOverFight(model) ? Vector3.one : Vector3.one * Mathf.Max(Plugin.Instance.VehicleScale.Value, 0.1f) * ModelScale(model);
                var r = v.go.GetComponent<Renderer>();
                // A collision sphere a bit smaller than the hull, so it hugs terrain without snagging.
                if (r != null) v.Radius = Mathf.Clamp(Mathf.Min(r.bounds.extents.x, Mathf.Min(r.bounds.extents.y, r.bounds.extents.z)) * 1.2f, 1.5f, 12f);
                // Helicopters hover-bob (a main rotor turns about up); propeller planes don't.
                v.Hover = System.Array.Exists(v.go.GetComponentsInChildren<Mw2Spin>(), s => s.axis == Vector3.up);
            }
            v.Position = at;
            v.Place(at, forward);
            if (scriptFx) v.AttachFor(model);
            return v;
        }

        public float Radius = 3f;
        const float Descend = 25f; // m/s back down to the flight path once clear

        /// Move toward this frame's flight-path point the way a character moves: the hull is a
        /// sphere that stops on terrain instead of passing through it. Walls ahead turn into a
        /// climb (aircraft pull up over them), slopes are slid along, and once clear it eases
        /// back down to MW2's flight height. Returns where it actually is.
        public Vector3 Fly(Vector3 want, Vector3 forward, float dt)
        {
            // Shot down: the crash moves it, not the flight plan.
            if (Dead) return Position;
            if (Crashing) { Place(Position, Forward); return Position; }
            int mask = LayerIndex.world.mask;
            var cur = Position;
            // Look ahead like a pilot: climb over what's coming before reaching it.
            float floor = AvoidFloor(want, forward, dt, mask);
            if (want.y < floor) { statClimb += Mathf.Max(0f, floor - Mathf.Max(want.y, cur.y)); want.y = floor; }
            if (want.y < cur.y) want.y = Mathf.Max(want.y, cur.y - Descend * dt);
            var delta = want - cur;
            float dist = delta.magnitude;
            if (dist > 1e-4f)
            {
                var dir = delta / dist;
                if (Physics.SphereCast(cur, Radius, dir, out var hit, dist, mask, QueryTriggerInteraction.Ignore))
                {
                    float travel = Mathf.Max(hit.distance - 0.05f, 0f);
                    statWallHits++;
                    cur += dir * travel;
                    float rest = dist - travel;
                    // Last resort (look-ahead missed it): steep face = hold at the wall and climb at
                    // a believable rate; shallow = slide along it. Converting the whole blocked move
                    // into climb raced the aircraft up the megaliths.
                    if (hit.normal.y < 0.5f) cur += Vector3.up * Mathf.Min(rest, 12f * dt);
                    else cur += Vector3.ProjectOnPlane(dir * rest, hit.normal);
                }
                else cur = want;
            }
            int lifted = 0;
            for (int i = 0; i < 24 && Physics.CheckSphere(cur, Radius * 0.9f, mask, QueryTriggerInteraction.Ignore); i++)
            { cur += Vector3.up * Radius * 0.5f; statLifts++; lifted++; }
            if (Mw2Pilot.Active && go != null && Position != Vector3.zero)
            {
                // A jump: more than 1.5 m in one frame (an Attack Helicopter hovers at 0.4 m a frame).
                float jump = (cur - Position).magnitude;
                if (jump > 1.5f)
                {
                    statJumps++;
                    if (statJumps <= 6) Plugin.Log.LogInfo($"[veh] {model} jumped {jump:F1} m in a frame at {cur} ({(lifted > 0 ? $"pushed out of the ground {lifted}x" : "flight plan")}, asked {(want - Position).magnitude:F1} m)");
                }
            }
            if (Mw2Pilot.Active && go != null)
            {
                var step0 = cur - Position; statFlown += new Vector2(step0.x, step0.z).magnitude;
                if (step0.y > 0f) statRise += step0.y; else statFall -= step0.y;
                if (Time.time >= statNextGround)
                {
                    statNextGround = Time.time + 0.5f;
                    if (Physics.Raycast(cur, Vector3.down, out var gh, 600f, mask, QueryTriggerInteraction.Ignore)) { statMinUp = Mathf.Min(statMinUp, gh.distance); statMaxUp = Mathf.Max(statMaxUp, gh.distance); }
                }
            }
            // Motion so it doesn't hang in the air: bank into turns (all aircraft), nose-down lean
            // with speed and a slow hover bob (helicopters).
            var flatFwd = forward; flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude > 1e-6f && dt > 0f)
            {
                float yawRate = Vector3.SignedAngle(Flat(Forward), flatFwd.normalized, Vector3.up) / dt;
                bank = Mathf.Lerp(bank, Mathf.Clamp(-yawRate * 0.6f, -35f, 35f), Mathf.Clamp01(dt * 2f));
                float speed = (cur - Position).magnitude / dt;
                var step = cur - Position;
                float vy = step.y / dt, vh = new Vector2(step.x, step.z).magnitude / dt;
                // Helicopters dip the nose with speed; jets pitch with their climb / dive.
                float pitchTo = Hover ? Mathf.Clamp(speed * 0.4f, 0f, 14f) : -Mathf.Clamp(Mathf.Atan2(vy, Mathf.Max(vh, 1f)) * Mathf.Rad2Deg, -30f, 30f);
                lean = Mathf.Lerp(lean, pitchTo, Mathf.Clamp01(dt * 1.5f));
            }
            var shown = cur;
            if (Hover) shown.y += Mathf.Sin(Time.time * 1.3f + phase) * 0.35f * Mathf.Max(Plugin.Instance.VehicleScale.Value, 0.5f);
            Position = cur;
            var forwardWas = Forward;
            if (flatFwd.sqrMagnitude > 1e-6f)
            {
                // No time, no turn: a frame with time held (the showcase's swap dissolve renders one, a
                // paused game) snapped a turning helicopter straight to its new heading (playtest 10-07-26:
                // "the heli would like jump frames when it was turning").
                if (YawSpeed <= 0f) Forward = flatFwd.normalized;
                else if (dt > 0f)
                {
                    // setyawspeed(75, 45, 45): accelerate the turn, brake into the goal heading.
                    float err = Vector3.SignedAngle(Flat(Forward), flatFwd.normalized, Vector3.up);
                    float turnTo = Mathf.Clamp(err * 2f, -YawSpeed, YawSpeed);
                    yawVel = Mathf.MoveTowards(yawVel, turnTo, 45f * dt * 2f);
                    Forward = Quaternion.AngleAxis(Mathf.Clamp(yawVel * dt, -Mathf.Abs(err), Mathf.Abs(err)), Vector3.up) * Flat(Forward);
                }
            }
            if (Mw2Pilot.Active && go != null && YawSpeed > 0f && Vector3.Angle(forwardWas, Forward) > 4f)
            {
                statTurnJumps++;
                if (statTurnJumps <= 6) Plugin.Log.LogInfo($"[veh] {model} turned {Vector3.Angle(forwardWas, Forward):F1} deg in a frame (dt {dt:F4})");
            }
            if (go != null) go.transform.SetPositionAndRotation(shown, Quaternion.LookRotation(Forward, Vector3.up) * Quaternion.Euler(lean, 0f, bank));
            Mw2VehicleHealth.Follow(proxy, cur);
            UpdateFx();
            return cur;
        }

        // ---------------------------------------------------------------- terrain avoidance
        // RoR2's maps are full of megaliths and arches far taller than MW2's flight heights.
        // Each frame the corridor ahead (about 2.5 s of travel) is probed: the tallest surface
        // under it, below any overhang, sets a floor the aircraft climbs to in advance, then
        // eases back down from once clear. The collision sphere below stays as the safety net.
        float avoidFloor = float.NegativeInfinity;

        float AvoidFloor(Vector3 want, Vector3 forward, float dt, int mask)
        {
            if (dt <= 0f) return avoidFloor;
            var travel = want - Position; travel.y = 0f;
            float speed = travel.magnitude / dt;
            var dir = travel.sqrMagnitude > 1e-4f ? travel.normalized : Flat(forward);
            float look = Mathf.Clamp(speed * 2.5f, 25f, 300f);
            float clearance = Radius * 1.5f + 6f;
            float baseY = Mathf.Max(Position.y, want.y);
            float need = float.NegativeInfinity;
            var side = new Vector3(dir.z, 0f, -dir.x) * Radius * 1.5f;
            for (int i = 0; i <= 6; i++)
            {
                float f = i <= 4 ? i / 4f : 0.6f;
                var p = Position + dir * look * f + (i == 5 ? side : i == 6 ? -side : Vector3.zero);
                p.y = baseY;
                // Start under any roof within 150 m (caves, arches), then look straight down.
                float top = Physics.Raycast(p, Vector3.up, out var roof, 150f, mask, QueryTriggerInteraction.Ignore) ? roof.point.y - Radius : p.y + 150f;
                var from = new Vector3(p.x, Mathf.Max(top, p.y), p.z);
                if (Physics.Raycast(from, Vector3.down, out var hit, (from.y - p.y) + 400f, mask, QueryTriggerInteraction.Ignore))
                    need = Mathf.Max(need, hit.point.y + clearance);
            }
            if (float.IsNegativeInfinity(avoidFloor)) avoidFloor = need;
            else if (need > avoidFloor) avoidFloor = Mathf.MoveTowards(avoidFloor, need, Mathf.Max(30f, speed * 0.8f) * dt);
            else avoidFloor = Mathf.MoveTowards(avoidFloor, need, 8f * dt);
            return avoidFloor;
        }

        float bank, lean;
        readonly float phase = UnityEngine.Random.value * 10f;
        /// Helicopters hover-bob and lean; set when the model has rotors.
        public bool Hover;

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        // ---------------------------------------------------------------- damage (Mw2VehicleHealth)
        GameObject proxy;
        int stage;
        /// MW2 damage stage shown (0 clean, 1 smoking, 2 heavy) - pilot checks.
        public int Stage => stage;
        float crashT;
        Vector3 crashVel;
        public bool Crashing { get; private set; }
        /// Shot down and exploded: the job ends.
        public bool Dead { get; private set; }

        string Family => model.StartsWith("sentry_") ? "sentry" : model.Contains("pavelow") ? "pavelow" : model.Contains("little_bird") ? "littlebird"
            : model.Contains("mi24") || model.Contains("mi-28") ? "hind" : model.Contains("harrier") ? "harrier"
            : model.Contains("ac130") ? "ac130" : "cobra";

        /// Monsters can shoot this one down: `mw2Health` in MW2 points. The host makes its hitbox;
        /// a client's goes out with its props (Mw2Net) and the host makes one on its copy.
        public void Damageable(float mw2Health)
        {
            if (go == null || proxy != null || Shootable.ContainsKey(go.GetInstanceID())) return;
            foreach (var gone in Shootable.Where(kv => kv.Value.v.go == null).Select(kv => kv.Key).ToList()) Shootable.Remove(gone);
            Shootable[go.GetInstanceID()] = (this, mw2Health, Mathf.Max(Radius * 1.4f, 3f));
            if (NetworkServer.active) proxy = Mw2VehicleHealth.Create(this, Position, owner, mw2Health, Mathf.Max(Radius * 1.4f, 3f));
        }

        /// Shootable aircraft by model instance (the props message's id): health, hitbox radius.
        public static readonly Dictionary<int, (Mw2Vehicle v, float health, float radius)> Shootable = new Dictionary<int, (Mw2Vehicle, float, float)>();

        /// Mw2Net: a shootable one's MW2 health and hitbox radius for teammates (0: not, or going down).
        public static float SharedHealth(GameObject model, out float radius)
        {
            radius = 0f;
            if (model == null || !Shootable.TryGetValue(model.GetInstanceID(), out var s)) return 0f;
            if (s.v.go != model || s.v.Crashing || s.v.Dead) return 0f;
            radius = s.radius;
            return s.health;
        }

        /// Mw2Net: the host's hitbox on our aircraft took a hit (fraction of health left), or the killing one.
        public static void RemoteHit(int modelId, float healthFraction, bool kill)
        {
            if (!Shootable.TryGetValue(modelId, out var s) || s.v.go == null || s.v.Crashing || s.v.Dead) return;
            if (kill) s.v.Kill(); else s.v.Damaged(healthFraction);
        }

        void Sound(string alias)
        {
            if (!string.IsNullOrEmpty(alias)) Plugin.Instance.Bridge.StreaksRef?.FxSound(alias, Position);
        }

        /// Health fell to `frac`: MW2's damage stages (_helicopter.gsc 1245-1260: light smoke past a
        /// third of its health gone, heavy past two thirds; _harrier.gsc 951-963: damaged burners).
        public void Damaged(float frac)
        {
            string fam = Family;
            if (fam == "harrier")
            {
                if (stage < 1 && frac <= 0.5f)
                {
                    stage = 1;
                    AttachTag("fire/jet_afterburner_harrier_damaged", "tag_engine_left", new Vector3(-0.12f, 0f, -0.95f), true);
                    AttachTag("fire/jet_afterburner_harrier_damaged", "tag_engine_right", new Vector3(0.12f, 0f, -0.95f), true);
                    AttachTag("smoke/smoke_trail_black_heli_emitter", "tag_engine_left", new Vector3(-0.12f, 0f, -0.95f), true);
                }
                return;
            }
            if (fam == "ac130") return;
            if (stage < 1 && frac <= 0.67f) { stage = 1; AttachTag("smoke/smoke_trail_white_heli_emitter", "tag_engine_left", new Vector3(-0.3f, 0.3f, -0.3f), true); Sound(fam + "_helicopter_hit"); }
            if (stage < 2 && frac <= 0.34f)
            {
                stage = 2;
                StopAttached("smoke/smoke_trail_white_heli_emitter");
                AttachTag("smoke/smoke_trail_black_heli_emitter", "tag_engine_left", new Vector3(-0.3f, 0.3f, -0.3f), true);
                Sound(fam + "_helicopter_damaged");
            }
        }

        /// Shot down: MW2's crash (heli_crash / heli_spin / heli_explode, the Harrier's death dive,
        /// the AC-130's air burst). Dead once it has blown up.
        public void Kill()
        {
            if (Crashing || Dead || go == null) return;
            Crashing = true;
            crashT = 0f;
            crashVel = Forward * 10f;
            string fam = Family;
            if (fam == "ac130") return; // explodes on the next frame
            if (fam == "sentry")
            {
                // setModel( modelDestroyed ), sentry_explode + sentry_gun_explosion on tag_aim.
                BurstTag("explosions/sentry_gun_explosion", "tag_aim", Vector3.zero);
                Sound("sentry_explode");
                if (go != null)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
                    wreck = Mw2Prop.Build("sentry_minigun_destroyed", owner, Space.Scale);
                    wreck?.transform.SetPositionAndRotation(go.transform.position, go.transform.rotation);
                }
                Mw2VehicleHealth.Remove(proxy);
                proxy = null;
                return;
            }
            if (fam == "harrier") { Sound("harrier_jet_crash"); return; }
            AttachTag("fire/fire_smoke_trail_L_emitter", "tag_engine_left", new Vector3(-0.3f, 0.3f, -0.3f), true);
            BurstTag("explosions/helicopter_explosion_secondary_small", "tag_engine_left", new Vector3(-0.3f, 0.3f, -0.3f));
            Sound(fam + "_helicopter_secondary_exp");
            Sound(fam + "_helicopter_hit");
            Loop(fam + "_helicopter_dying_loop", 500f);
        }

        bool secondBoom;

        /// The crash, frame by frame: helicopters spin down (one turn per 2 s) with a second
        /// secondary explosion at 3 s and blow up at 4.5 s or on the ground; the Harrier dives.
        GameObject wreck;
        float nextSmoke;

        Vector3 CrashStep(float dt)
        {
            crashT += dt;
            string fam = Family;
            var at = Position;
            if (fam == "sentry")
            {
                // 1.5 s later: sentry_explode_smoke, car_damage_blacksmoke on tag_aim every 0.4 s for 8 s.
                if (crashT >= 1.5f && nextSmoke == 0f) { Sound("sentry_explode_smoke"); nextSmoke = crashT; }
                if (nextSmoke > 0f && crashT >= nextSmoke && crashT < 9.5f) { nextSmoke = crashT + 0.4f; BurstTag("smoke/car_damage_blacksmoke", "tag_aim", Vector3.zero); }
                if (crashT >= 9.5f) { Dead = true; StopFx(); if (wreck != null) Object.Destroy(wreck); if (go != null) go.SetActive(false); }
                return at;
            }
            if (fam == "ac130") { Explode("explosions/aerial_explosion_ac130_coop", null); return at; }
            crashVel += Vector3.down * (fam == "harrier" ? 14f : 9f) * dt;
            at += crashVel * dt;
            if (fam != "harrier")
            {
                Forward = Quaternion.AngleAxis(180f * dt, Vector3.up) * Forward;
                if (!secondBoom && crashT >= 3f)
                {
                    secondBoom = true;
                    BurstTag("explosions/helicopter_explosion_secondary_small", "tag_engine_left", new Vector3(-0.3f, 0.3f, -0.3f));
                    Sound(fam + "_helicopter_secondary_exp");
                }
            }
            else Forward = Vector3.Slerp(Forward, (Forward + Vector3.down).normalized, dt);
            bool ground = Physics.Raycast(Position, (at - Position).normalized, (at - Position).magnitude + Radius, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            if (ground || crashT >= (fam == "harrier" ? 2.5f : 4.5f))
            {
                string death = fam == "harrier" ? "explosions/aerial_explosion_harrier"
                    : fam == "pavelow" ? "explosions/aerial_explosion_pavelow_mp"
                    : fam == "littlebird" ? "explosions/aerial_explosion_littlebird_mp"
                    : fam == "hind" ? "explosions/aerial_explosion_hind_chernobyl_mp"
                    : model.Contains("apache") ? "explosions/aerial_explosion_apache_mp" : "explosions/aerial_explosion_cobra_low_mp";
                Explode(death, fam == "harrier" ? "harrier_jet_crash" : fam + "_helicopter_crash");
            }
            return at;
        }

        void Explode(string fx, string sound)
        {
            if (Dead) return;
            if (go != null && Mw2Prop.TagFrame(model, "tag_deathfx", out var tl, out var tf, out var tu))
                Mw2Fx.Play(fx, go.transform.TransformPoint(tl), go.transform.TransformDirection(tf), go.transform.TransformDirection(tu));
            else Mw2Fx.Play(fx, Position, Forward, Vector3.up);
            Sound(sound);
            Dead = true;
            StopFx();
            if (go != null) go.SetActive(false);
            Mw2VehicleHealth.Remove(proxy);
            proxy = null;
        }

        public void Place(Vector3 at, Vector3 forward, float bankDeg = 0f)
        {
            if (Dead) return;
            if (Crashing) { at = CrashStep(Time.deltaTime); forward = Forward; bankDeg = 0f; if (Dead) return; }
            Position = at;
            if (forward.sqrMagnitude > 1e-6f) Forward = forward.normalized;
            if (go != null) go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(Forward, Vector3.up) * Quaternion.Euler(0f, 0f, bankDeg));
            Mw2VehicleHealth.Follow(proxy, at);
            UpdateFx();
        }

        // ---------------------------------------------------------------- MW2 FX on the hull
        class Attached { public uint handle; public string name, tag; public Vector3 frac; public bool back; }
        readonly List<Attached> attached = new List<Attached>();
        Bounds local;
        float nextDust;
        bool dust;

        /// Loop an MW2 effect at a point on the hull: frac in -1..1 of the hull's local
        /// half-extents (x right, y up, z nose). back = effect forward points out the tail.
        public void Attach(string fx, Vector3 frac, bool back = false)
        {
            if (!Mw2Fx.Ready || go == null) return;
            var a = new Attached { name = fx, frac = frac, back = back };
            attached.Add(a);
            MoveFx(a);
        }

        Vector3 Point(Vector3 frac) => go.transform.TransformPoint(local.center + Vector3.Scale(local.extents, frac));

        /// Loop an MW2 effect on one of the model's tags (playFXOnTag); `fallback` (hull fractions)
        /// when the model lacks the tag.
        public void AttachTag(string fx, string tag, Vector3 fallback, bool back = false)
        {
            if (!Mw2Fx.Ready || go == null) return;
            var a = new Attached { name = fx, tag = tag, frac = fallback, back = back };
            attached.Add(a);
            MoveFx(a);
        }

        void MoveFx(Attached a)
        {
            Vector3 at, fwd, up;
            if (a.tag != null && Mw2Prop.TagFrame(model, a.tag, out var tl, out var tf, out var tu))
            {
                // playFXOnTag: the effect takes the tag's own orientation (the Harrier's nozzles point
                // back and down; aiming every engine straight out the tail was wrong, playtest 10-03-26).
                at = go.transform.TransformPoint(tl);
                fwd = go.transform.TransformDirection(tf);
                up = go.transform.TransformDirection(tu);
            }
            else
            {
                at = Point(a.frac);
                fwd = a.back ? -go.transform.forward : go.transform.forward;
                up = go.transform.up;
            }
            if (a.handle == 0 || !Mw2Fx.Move(a.handle, at, fwd, up)) a.handle = Mw2Fx.Play(a.name, at, fwd, up);
        }

        void UpdateFx()
        {
            UpdateLoops();
            if (go == null || !Mw2Fx.Ready) return;
            if (!visible)
            {
                // Hidden from this camera (it rides inside the hull): its lights and engines too.
                foreach (var a in attached) if (a.handle != 0) { Mw2Fx.Stop(a.handle); a.handle = 0; }
                return;
            }
            foreach (var a in attached) MoveFx(a);
            // Rotor wash (treadfx/heli_dust_default) on the ground under a low helicopter.
            if (dust && Time.time >= nextDust)
            {
                nextDust = Time.time + 0.2f;
                if (Physics.Raycast(Position, Vector3.down, out var hit, 40f * Mathf.Max(Plugin.Instance.VehicleScale.Value, 0.5f), LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                    Mw2Fx.Play("treadfx/heli_dust_default", hit.point, hit.normal);
            }
        }

        /// MW2's effects for this model, as its scripts attach them.
        void AttachFor(string model)
        {
            if (!Mw2Fx.Ready || go == null) return;
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) local = mf.sharedMesh.bounds;
            if (model.Contains("pavelow")) Loop("pavelow_engine_high", 400f);
            else if (model.Contains("little_bird")) Loop("littlebird_engine_high", 350f);
            else if (model.Contains("mi24") || model.Contains("mi-28")) Loop("mp_hind_helicopter", 400f);
            else if (model.Contains("cobra") || model.Contains("apache")) Loop("mp_cobra_helicopter", 400f);
            else if (model.Contains("harrier")) Loop("harrier_engine_high", 500f);
            else if (model.Contains("mig29")) Loop("veh_mig29_dist_loop", 900f);          // _airstrike.gsc:688
            else if (model.Contains("b2")) Loop("veh_b2_dist_loop", 1000f);              // _airstrike.gsc:529
            bool harrier = model.Contains("harrier");
            if (model.Contains("mig29") || harrier)
            {
                // _airstrike.gsc playPlaneFx / _harrier.gsc playHarrierFx: afterburners on the engine
                // tags (the Harrier has four), contrails on the wingtips.
                string burner = harrier ? "fire/jet_afterburner_harrier" : "fire/jet_afterburner";
                AttachTag(burner, "tag_engine_right", new Vector3(0.12f, 0f, -0.95f), true);
                AttachTag(burner, "tag_engine_left", new Vector3(-0.12f, 0f, -0.95f), true);
                if (harrier)
                {
                    AttachTag(burner, "tag_engine_right2", new Vector3(0.2f, 0f, -0.6f), true);
                    AttachTag(burner, "tag_engine_left2", new Vector3(-0.2f, 0f, -0.6f), true);
                }
                AttachTag("smoke/jet_contrail", "tag_right_wingtip", new Vector3(1f, 0f, -0.35f), true);
                AttachTag("smoke/jet_contrail", "tag_left_wingtip", new Vector3(-1f, 0f, -0.35f), true);
                if (harrier) LightsOn("tag_light_L_wing", "tag_light_R_wing", "tag_light_belly", "tag_light_tail");
            }
            else if (model.Contains("ac130"))
            {
                // _ac130.gsc playAC130Effects.
                AttachTag("misc/aircraft_light_red_blink", "tag_light_belly", new Vector3(0f, -0.8f, 0.3f));
                AttachTag("fire/jet_engine_ac130", "tag_body", new Vector3(0f, 0f, 0.1f), true);
                AttachTag("misc/aircraft_light_white_blink", "tag_light_tail", new Vector3(0f, 0.6f, -1f));
                AttachTag("misc/aircraft_light_wingtip_red", "tag_light_top", new Vector3(0f, 0.8f, 0.4f));
            }
            else if (model.Contains("pavelow"))
            {
                // _helicopter.gsc pavelowLightFX.
                LightsOn("tag_light_L_wing1", "tag_light_R_wing1", "tag_light_belly", "tag_light_tail");
                AttachTag("misc/aircraft_light_white_blink", "tag_light_tail2", new Vector3(0f, 0.3f, -0.9f));
                AttachTag("misc/aircraft_light_red_blink", "tag_light_cockpit01", new Vector3(0f, 0.2f, 0.9f));
                dust = true;
            }
            else if (model.Contains("cobra") || model.Contains("apache") || model.Contains("little_bird") || model.Contains("mi24") || model.Contains("mi-28"))
            {
                // _helicopter.gsc defaultLightFX.
                LightsOn("tag_light_L_wing", "tag_light_R_wing", "tag_light_belly", "tag_light_tail");
                dust = true;
            }
        }

        /// The heli / harrier light set: green left wingtip, red right, red belly blink, white tail blink.
        void LightsOn(string left, string right, string belly, string tail)
        {
            AttachTag("misc/aircraft_light_wingtip_green", left, new Vector3(-0.6f, 0f, 0f));
            AttachTag("misc/aircraft_light_wingtip_red", right, new Vector3(0.6f, 0f, 0f));
            AttachTag("misc/aircraft_light_red_blink", belly, new Vector3(0f, -0.8f, 0.1f));
            AttachTag("misc/aircraft_light_white_blink", tail, new Vector3(0f, 0.2f, -1f));
        }

        // ---------------------------------------------------------------- loop sounds
        class LoopSnd { public uint id; public string alias; public float range, gain; }
        float nextLoopLog;
        readonly List<LoopSnd> loops = new List<LoopSnd>();

        /// playLoopSound on this vehicle: heard out to `range` metres from the camera, full at the
        /// hull (our audio has no 3D panning; distance sets the volume).
        public void Loop(string alias, float range, float gain = 1f)
        {
            uint id = Mw2Audio.LoopStart(alias);
            if (id == 0) { Plugin.Log.LogInfo($"MW2 vehicle loop '{alias}' not playable"); return; }
            loops.Add(new LoopSnd { id = id, alias = alias, range = range, gain = gain });
            UpdateLoops();
        }

        public void StopLoop(string alias)
        {
            foreach (var l in loops) if (l.alias == alias) Mw2Audio.LoopStop(l.id);
            loops.RemoveAll(l => l.alias == alias);
        }

        void UpdateLoops()
        {
            if (loops.Count == 0) return;
            var cam = Camera.main;
            float d = cam != null ? Vector3.Distance(cam.transform.position, Position) : 0f;
            foreach (var l in loops)
            {
                float k = Mathf.Clamp01(1f - d / Mathf.Max(l.range, 1f));
                Mw2Audio.LoopVolume(l.id, k * k * l.gain);
                if (Mw2Pilot.Active && Time.time >= nextLoopLog) { nextLoopLog = Time.time + 1f; Plugin.Log.LogInfo($"[loop] {l.alias} #{l.id}: {d:F0} m, k {k * k:F2}"); }
            }
        }

        void StopFx()
        {
            foreach (var l in loops) Mw2Audio.LoopStop(l.id);
            loops.Clear();
            foreach (var a in attached) Mw2Fx.Stop(a.handle);
            attached.Clear();
        }

        /// One-shot MW2 effect at one of the model's tags (playFXOnTag of a one-shot effect).
        public void BurstTag(string fx, string tag, Vector3 fallback)
        {
            if (go == null || !Mw2Fx.Ready) return;
            if (Mw2Prop.TagFrame(model, tag, out var tl, out var tf, out var tu))
                Mw2Fx.Play(fx, go.transform.TransformPoint(tl), go.transform.TransformDirection(tf), go.transform.TransformDirection(tu));
            else Mw2Fx.Play(fx, Point(fallback), -go.transform.up, go.transform.forward);
        }

        /// Stop every attached loop of one effect (e.g. stopHarrierWingFx: the contrails).
        public void StopAttached(string fx)
        {
            foreach (var a in attached) if (a.name == fx) Mw2Fx.Stop(a.handle);
            attached.RemoveAll(a => a.name == fx);
        }

        /// One-shot MW2 effect at a hull point (e.g. the B-2's bomb bays).
        public void Burst(string fx, Vector3 frac, Vector3 dir)
        {
            if (go != null) Mw2Fx.Play(fx, Point(frac), dir);
        }

        /// Hide the hull from its own ride camera (Chopper Gunner sits inside it).
        /// The sentry's barrel cluster: degrees per second (0 = still).
        public void SetSpin(float degPerSec)
        {
            if (go == null) return;
            foreach (var s in go.GetComponentsInChildren<Mw2Spin>())
                if (s.name.EndsWith("_spin")) s.speed = degPerSec;
        }

        public void SetVisible(bool on)
        {
            if (go == null || visible == on) return;
            visible = on;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = on;
        }
        bool visible = true;

        // Pilot: how the flight went (route checks - playtest 10-06-26: helicopters and killstreaks did weird things).
        float statFlown, statRise, statFall, statClimb, statMinUp = float.MaxValue, statMaxUp, statNextGround;
        int statWallHits, statLifts, statJumps, statTurnJumps;

        public void LogStats(string when)
        {
            if (Mw2Pilot.Active && go != null && statFlown > 0f)
                Plugin.Log.LogInfo($"[veh] {model} ({when}): flew {statFlown:F0} m, rose {statRise:F0} m / fell {statFall:F0} m, terrain climbs {statClimb:F0} m, wall hits {statWallHits}, lifted out of geometry {statLifts}x, jumps {statJumps}, turn jumps {statTurnJumps}, height over ground {statMinUp:F0}-{statMaxUp:F0} m");
        }

        public void Destroy()
        {
            LogStats("gone");
            if (wreck != null) Object.Destroy(wreck);
            Mw2VehicleHealth.Remove(proxy);
            proxy = null;
            StopFx();
            All.Remove(this);
            if (go != null) Object.Destroy(go);
            go = null;
        }

        /// Every aircraft in the air (the playtest pilot keeps the camera on them).
        public static readonly List<Mw2Vehicle> All = new List<Mw2Vehicle>();
    }
}

namespace MW2RoR2
{
    /// A killstreak's own camera (Predator, AC-130, Chopper Gunner) drawn over the main one,
    /// so RoR2's camera rig, which builds each frame from the last, is never touched.
    class Mw2RideCam
    {
        Camera cam;
        public bool Live => cam != null;

        /// Straight-down map view (MW2's location selector), half-height in metres.
        public void PlaceOrtho(Camera main, Vector3 at, Quaternion rot, float halfHeight)
        {
            Place(main, at, rot, main.fieldOfView);
            cam.orthographic = true;
            cam.orthographicSize = halfHeight;
        }

        /// The main camera's post-processing on a ride camera (its object still inactive), so MW2
        /// vision sets (thermal, nuke) show through it too.
        public static void PostProcessing(GameObject go, Camera main)
        {
            // Its own depth (and normals) textures: RoR2's outline / fog effects read the depth
            // texture, and without one of its own this camera's post-processing used the main
            // camera's - the player's view on the ground showed through the missile camera as
            // outlines (playtest 10-04-26).
            var cam = go.GetComponent<Camera>();
            if (cam != null) cam.depthTextureMode |= main.depthTextureMode | DepthTextureMode.Depth | DepthTextureMode.DepthNormals;
            var src = main.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>()
                ?? Object.FindObjectOfType<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
            var resources = src != null ? HarmonyLib.AccessTools.Field(typeof(UnityEngine.Rendering.PostProcessing.PostProcessLayer), "m_Resources")?.GetValue(src) as UnityEngine.Rendering.PostProcessing.PostProcessResources : null;
            if (resources != null)
            {
                var layer = go.AddComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                layer.Init(resources);
                layer.volumeLayer = src.volumeLayer;
                layer.volumeTrigger = go.transform;
            }
            Plugin.Log.LogInfo($"{go.name}: post-processing {(resources != null ? "from " + src.gameObject.name + ", volume layers " + src.volumeLayer.value : "not found")}");
        }

        public void Place(Camera main, Vector3 at, Quaternion rot, float fov)
        {
            if (cam == null)
            {
                var go = new GameObject("MW2 Ride Camera");
                go.SetActive(false);
                cam = go.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.cullingMask = main.cullingMask & ~(1 << Mw2View.Layer);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = Mathf.Max(main.farClipPlane, 3000f);
                // The stage's occlusion data is baked for ground-level cameras; from the gunship /
                // chopper height it culled monsters until the view was right on top of them
                // (playtest 10-04-26: no Wisps in the AC-130).
                cam.useOcclusionCulling = false;
                PostProcessing(go, main);
                go.SetActive(true);
                Mw2Vision.Reregister();
            }
            cam.depth = main.depth + 5f;
            Mw2Fx.View = cam;
            cam.orthographic = false;
            cam.fieldOfView = fov;
            cam.transform.SetPositionAndRotation(at, rot);
        }

        public void Drop()
        {
            if (cam != null && Mw2Fx.View == cam) Mw2Fx.View = null;
            if (cam != null) Object.Destroy(cam.gameObject);
            cam = null;
        }
    }
}
