using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// An MW2 soldier playing a match in the background of the animatic (playtest 10-05-26: "have a mw2
    /// soldier character run around in the background... an mw2 iconic class, intervention and dual
    /// 1887s... one man army to switch to like an assault rifle if it runs out of ammo"). A RoR2
    /// carrier body (Commando's, its AI off, its model hidden, god mode) does the moving and takes the
    /// hits; on it stands MW2's own third-person soldier (Mw2Character, as teammates are drawn), and
    /// this brain picks targets, moves, aims, fires MW2 rounds (MW2 muzzle flash and tracers,
    /// BulletAttack damage), reloads, switches guns and goes One Man Army when the class runs dry.
    /// Pilot only.
    sealed class Mw2Bot
    {
        enum Gun { Sniper, Shotguns, Bag, Rifle }

        sealed class Kit
        {
            public string name;
            public uint index;
            public int clip, clipSize, stock, stockSize;
            public float interval, reload, damage, range, spread;
            public int pellets = 1;
            public bool akimbo;
            /// How it's played: 0 sniper (stand, scope, shoot), 1 close (shotguns: rush), 2 automatic
            /// (mid range, bursts, strafing), 3 sidearm (mid range, a shot at a time).
            public int style;
        }

        /// MW2 classes, one per life (playtest 10-05-26: each new soldier "with a different class").
        public static readonly string[] Classes = { "sniper", "assault", "shotgun", "lmg", "smg" };
        string className = "sniper";
        // Entering: from past a side of the frame, out on the field, into the shot (never through it).
        Vector3[] entry;
        int entryStep;
        float entryAt;
        /// The take's depth (playtest 10-07-26: "put the enemies and our display of background stuff a bit
        /// further back"): every distance he plays at, scaled. And whether the camera shows a spot (in
        /// the frame, not hidden behind the ground in between; `wide`: past the frame's sides counts).
        public static float Depth = 1f;
        public static Func<Vector3, bool, bool> Shown;
        static bool OnCamera(Vector3 feet, bool wide = false) => Shown == null || Shown(feet, wide);
        float unseenFor;
        // The knife: up close, now and then (it one-shots - not so often he outlives the video).
        float nextKnife, backOffUntil;
        Vector3 stallFrom;
        float nextStallCheck;
        /// Staged lives (playtest 10-05-26: "stage the deaths" - four operators over the video): he goes
        /// down at this point of his life (0 = only if killed), and calls this one streak meanwhile
        /// (0 sentry, 1/3 airstrike, 2 Predator).
        public float DieAt;
        public int StreakKind = -1;

        public CharacterBody Body { get; private set; }
        /// The player's streak sound router: his shots and streaks are heard (MW2 aliases, by distance).
        public static Mw2Killstreaks PlayerStreaks;
        public bool Alive => Body != null && Body.healthComponent != null && Body.healthComponent.alive;
        /// Down long enough for MW2's death animation to play out: the next soldier can come in.
        public bool Finished => !Alive && deadFor > 3f;
        string skin = "socom_141";
        float deadFor;
        Vector3 lastFeet;
        float relocateAt;
        bool relocating;
        readonly Mw2Character ch = new Mw2Character();
        CharacterModel hidden;
        Mw2CharacterInput input;
        Kit sniper, shotguns, rifle, bag;
        Gun gun = Gun.Sniper;
        bool rifleClass;
        CharacterBody target;
        float now, nextTarget, nextShot, busyUntil, adsFrac, burstLeft;
        Vector3 wanderGoal;
        float wanderUntil;
        int hand;
        uint events;
        readonly System.Random rng = new System.Random(77);
        Vector3 eye, fwd; // the camera's: the shot he plays in
        int shots;
        float nextLog;
        Vector3 moveNow;            // eased: an MW2 soldier doesn't dart about like Commando (10-05-26)
        float yawNow = float.NaN;
        // His killstreaks (playtest 10-05-26: sentry, airstrike, predator - background, not in the way).
        Mw2Killstreaks streaks;
        /// His sounds against the camera's own (playtest 10-06-26: his gunfire and the rest were loud and
        /// distracting - he's the background).
        public static float Volume = 0.45f;
        int streakStep;
        float callUntil;
        /// His Predator in flight: he stays on the laptop, still, until it's down (playtest 10-05-26).
        BotPredatorJob flying;
        uint callWeapon;
        Action callIn;
        static readonly float[] StreakAt = { 12f, 34f, 58f, 84f };

        public static Mw2Bot Spawn(Vector3 at, Vector3 facing, string skinKey = null, int classIndex = 0, Vector3[] entryPath = null)
        {
            var prefab = MasterCatalog.FindMasterPrefab("CommandoMonsterMaster");
            if (prefab == null) return null;
            var m = new MasterSummon { masterPrefab = prefab, position = at, rotation = Quaternion.LookRotation(facing), teamIndexOverride = TeamIndex.Player, ignoreTeamMemberLimit = true }.Perform();
            var body = m != null ? m.GetBody() : null;
            if (body == null) return null;
            foreach (var ai in m.GetComponents<RoR2.CharacterAI.BaseAI>()) ai.enabled = false;
            // Mortal but sturdy (playtest 10-05-26: dying often would distract; then "tone back", then
            // "slightly weaker"): 1.6 times the health, a little armour.
            body.baseMaxHealth *= 2f;
            body.levelMaxHealth *= 2f;
            body.baseArmor += 15f;
            // No RoR2 knockback: golem claps and beetle-guard slams threw him metres up (playtest 10-05-26:
            // "why he jump so high"). Force / mass, so a heavy carrier only gets nudged.
            if (body.characterMotor != null) body.characterMotor.mass = 10000f;
            body.RecalculateStats();
            if (body.healthComponent != null) body.healthComponent.health = body.healthComponent.fullHealth;
            var bot = new Mw2Bot { Body = body };
            if (!string.IsNullOrEmpty(skinKey)) bot.skin = skinKey;
            // Commando's model hidden from the first frame (and through his death - it showed then).
            var mt = body.modelLocator != null ? body.modelLocator.modelTransform : null;
            bot.hidden = mt != null ? mt.GetComponent<CharacterModel>() : null;
            if (bot.hidden != null) bot.hidden.invisibilityCount++;
            Mw2ItemDisplays.Bind(bot.hidden, bot.ch);
            string acr = Native.WeaponIndex("masada_reflex_mp") != 0 ? "masada_reflex_mp" : "masada_mp";
            bot.className = Classes[((classIndex % Classes.Length) + Classes.Length) % Classes.Length];
            // Primary and secondary (the 'sniper' / 'shotguns' slots); low reserves so One Man Army comes up.
            switch (bot.className)
            {
                case "assault":
                    bot.sniper = Make(acr, 30, 30, 0.09f, 2.0f, 30f, 6000f, 1.4f, 2);
                    bot.shotguns = Make("deserteagle_mp", 7, 7, 0.35f, 1.96f, 45f, 3000f, 1.5f, 3);
                    break;
                case "smg":
                    bot.sniper = Make("ump45_mp", 32, 32, 0.1f, 2.5f, 30f, 3000f, 2f, 2);
                    bot.shotguns = Make("usp_mp", 12, 12, 0.3f, 1.6f, 30f, 3000f, 1.5f, 3);
                    break;
                case "lmg":
                    bot.sniper = Make("rpd_mp", 100, 0, 0.08f, 9.7f, 30f, 6000f, 2f, 2);
                    bot.shotguns = Make("usp_mp", 12, 12, 0.3f, 1.6f, 30f, 3000f, 1.5f, 3);
                    break;
                case "shotgun":
                    bot.sniper = Make("spas12_mp", 8, 8, 0.9f, 3.0f, 40f, 1200f, 4.5f, 1);
                    bot.sniper.pellets = 4;
                    bot.shotguns = Make("deserteagle_mp", 7, 7, 0.35f, 1.96f, 45f, 3000f, 1.5f, 3);
                    break;
                default: // sniper
                    bot.sniper = Make("cheytac_mp", 5, 5, 1.25f, 2.3f, 98f, 12000f, 0.25f, 0);
                    bot.shotguns = Make("model1887_akimbo_mp", 14, 7, 0.45f, 3.0f, 32f, 1200f, 4.5f, 1);
                    bot.shotguns.pellets = 4;
                    bot.shotguns.akimbo = true;
                    break;
            }
            bot.rifle = Make(acr, 30, 60, 0.09f, 2.0f, 30f, 6000f, 1.4f, 2);
            bot.entry = entryPath;
            bot.bag = Make("onemanarmy_mp", 0, 0, 1f, 0f, 0f, 0f, 0f);
            return bot;
        }

        static Kit Make(string name, int clip, int stock, float interval, float reload, float damage, float range, float spread, int style = 2) =>
            new Kit { name = name, index = Native.WeaponIndex(name), clip = clip, clipSize = clip, stock = stock, stockSize = stock, interval = interval, reload = reload, damage = damage, range = range, spread = spread, style = style };

        Kit Current => gun == Gun.Sniper ? sniper : gun == Gun.Shotguns ? shotguns : gun == Gun.Rifle ? rifle : bag;

        public void Destroy()
        {
            streaks?.Detach();
            Mw2ItemDisplays.Unbind(hidden);
            ch.Destroy();
            if (hidden != null) hidden.invisibilityCount--;
            hidden = null;
        }

        /// A way in for a new soldier: from out of frame past one side (`side` -1 left, 1 right), out on
        /// the field at the depth he plays at, across into the shot (playtest 10-07-26: "increase the spawn
        /// area for where the background player comes in" - they came in past the camera, over the drop
        /// in front of it). [0] is where he starts.
        public static Vector3[] EntryPath(Vector3 eye, Vector3 fwd, float side, System.Random rng)
        {
            var f = Flat(fwd).normalized;
            Vector3[] path = null;
            for (int tries = 0; tries < 16; tries++)
            {
                float d = (24f + (float)rng.NextDouble() * 18f) * Depth;
                var start = Mw2Pilot.OnNodes(eye + Quaternion.Euler(0f, side * (54f + (float)rng.NextDouble() * 12f), 0f) * f * d);
                var mid = Mw2Pilot.OnNodes(eye + Quaternion.Euler(0f, side * 32f, 0f) * f * d);
                var inside = Mw2Pilot.OnNodes(eye + Quaternion.Euler(0f, side * (8f + (float)rng.NextDouble() * 10f), 0f) * f * d * 0.95f);
                path = new[] { start, mid, inside };
                if (OnCamera(start, true) && OnCamera(mid, true) && OnCamera(inside) && Flat(start - eye).magnitude > 14f * Depth) break;
            }
            return path;
        }

        /// Every frame: `camEye`/`camFwd` are the shot's; he plays 14-50 m out across it.
        public void Tick(Vector3 camEye, Vector3 camFwd)
        {
            var b = Body;
            eye = camEye; fwd = camFwd;
            if (!Alive)
            {
                // Down: MW2's death animation where he fell (the carrier body may be gone by now).
                if (deadFor == 0f && b != null && b.modelLocator != null && b.modelLocator.modelTransform != null)
                    b.modelLocator.modelTransform.gameObject.SetActive(false); // Commando's ragdoll showed under him
                deadFor += Time.deltaTime;
                if (ch.Exists)
                {
                    input.dt = Time.deltaTime;
                    input.dead = 1;
                    input.moveFwd = input.moveRight = 0f;
                    input.events = 0;
                    ch.Step(lastFeet, float.IsNaN(yawNow) ? 0f : yawNow, ref input, true);
                }
                return;
            }
            if (b.inputBank == null) return;
            lastFeet = b.footPosition;
            if (DieAt > 0f && now >= DieAt && b.healthComponent != null)
            {
                DieAt = 0f;
                entry = null;
                b.healthComponent.godMode = false;
                Plugin.Log.LogInfo($"[bot] {className} goes down (staged)");
                b.healthComponent.Suicide();
                return;
            }
            float dt = Time.deltaTime;
            now += dt;
            if (!ch.Exists)
            {
                if (!ch.Build(b, skin) && !ch.Build(b, Mw2Skins.LocalFaction())) return;
                Plugin.Log.LogInfo($"[bot] MW2 soldier in ({skin}, {className}: {sniper.name} + {shotguns.name}), One Man Army");
            }

            if (streaks == null) { streaks = new Mw2Killstreaks(); streaks.Attach(b); streaks.Audio = PlayerStreaks?.Audio; streaks.SoundScale = Volume; }
            streaks.UpdateJobsOnly();
            TickStreaks();

            // Target: the nearest monster he can see, re-picked twice a second.
            if (now >= nextTarget || target == null || target.healthComponent == null || !target.healthComponent.alive)
            {
                nextTarget = now + 0.5f;
                target = null;
                float best = 60f;
                foreach (var cb in CharacterBody.readOnlyInstancesList)
                {
                    if (cb == null || cb.teamComponent == null || cb.teamComponent.teamIndex != TeamIndex.Monster) continue;
                    if (cb.healthComponent == null || !cb.healthComponent.alive) continue;
                    // Only fights that play in the shot: a monster by the camera drew him out of it.
                    if (!InShot(cb.corePosition, 35f, 10f * Depth, 34f * Depth) || !OnCamera(cb.footPosition)) continue; // fights near enough to read (he looked tiny at 30-45 m)
                    float d = Vector3.Distance(cb.corePosition, b.corePosition);
                    if (d < best && Sees(cb)) { best = d; target = cb; }
                }
            }
            float dist = target != null ? Vector3.Distance(target.corePosition, b.corePosition) : 999f;

            // The gun for the moment: the 1887s up close, the Intervention at range; One Man Army
            // when the class is dry (and back the other way when the rifle class is).
            if (now >= busyUntil)
            {
                Gun want = gun;
                if (gun == Gun.Bag) want = rifleClass ? Gun.Rifle : Gun.Sniper;
                else if (rifleClass) want = Gun.Rifle;
                else if (dist < 9f && shotguns.style == 1 && Has(shotguns)) want = Gun.Shotguns; // the 1887s up close
                else if (Has(sniper)) want = Gun.Sniper;
                else if (Has(shotguns)) want = Gun.Shotguns;
                bool classDry = rifleClass ? !Has(rifle) : !Has(sniper) && !Has(shotguns);
                if (classDry && gun != Gun.Bag)
                {
                    // One Man Army: the bag comes out, the class changes (6 s in MW2, 3 with Pro).
                    gun = Gun.Bag;
                    busyUntil = now + 3.5f;
                    rifleClass = !rifleClass;
                    Refill(rifleClass ? rifle : sniper);
                    if (!rifleClass) Refill(shotguns);
                    Plugin.Log.LogInfo($"[bot] One Man Army -> {(rifleClass ? rifle.name : className + " class")}");
                }
                else if (want != gun)
                {
                    gun = want;
                    busyUntil = now + 0.9f; // MW2's drop + raise on his model
                }
            }

            // Where to stand: the Intervention holds still in a lane; the 1887s close in; the rifle
            // keeps mid range and strafes. No target: roam the shot.
            var k = Current;
            Vector3 move = Vector3.zero;
            bool sprint = false;
            // A new soldier runs in from behind the camera, past it out of frame, into the shot - out
            // of harm's way until he's there (they died on the run in, 10-05-26).
            // Out of the camera's sight a while (down the drop in front of the ledge, 10-07-26: his shots
            // heard, nobody seen): back in from a side of the frame, as a new soldier comes in.
            unseenFor = !OnCamera(b.footPosition, true) && now >= callUntil ? unseenFor + dt : 0f;
            if (unseenFor > 2.5f && !(entry != null && entryStep < entry.Length && now - entryAt < 12f))
            {
                unseenFor = 0f;
                var way = EntryPath(eye, fwd, rng.NextDouble() < 0.5 ? -1f : 1f, rng);
                TeleportHelper.TeleportBody(b, way[0] + Vector3.up * 0.3f);
                entry = new[] { way[1], way[2] }; entryStep = 0; entryAt = now; relocating = false;
                Plugin.Log.LogInfo($"[bot] out of the camera's sight: back in from the side at {way[0]}");
            }
            bool entering = entry != null && entryStep < entry.Length && now - entryAt < 12f;
            if (b.healthComponent != null) b.healthComponent.godMode = entering && now - entryAt < 8f;
            if (entering)
            {
                // Stalled on the way (terrain behind the camera held one there all take, 10-05-26): on to the
                // next waypoint (they're out of frame).
                if (now >= nextStallCheck)
                {
                    if (Vector3.Distance(b.footPosition, stallFrom) < 1f && now - entryAt > 1f) { TeleportHelper.TeleportBody(b, entry[entryStep] + Vector3.up * 0.3f); entryStep++; }
                    stallFrom = b.footPosition;
                    nextStallCheck = now + 1.5f;
                }
                entering = entryStep < entry.Length;
            }
            if (entering)
            {
                move = Flat(entry[entryStep] - b.footPosition);
                if (move.magnitude < 2.5f) { entryStep++; move = Vector3.zero; }
                else { move = move.normalized; sprint = true; }
            }
            // Every so often he runs to a new spot across the shot (playtest 10-05-26: "running around in
            // the background"), sprinting - no shooting on the run, as in MW2.
            if (relocateAt == 0f) relocateAt = now + 7f;
            if (!entering && !relocating && now >= relocateAt && now >= callUntil) { relocating = true; PickWander(); }
            // Up close: the knife now and then (MW2's one-hit lunge), then distance to shoot.
            if (!entering && target != null && dist < 3f && now >= nextKnife && now >= busyUntil && now >= callUntil)
            {
                Knife(target);
                nextKnife = now + 7f;
                backOffUntil = now + 1.4f;
            }
            if (entering) { }
            else if (relocating)
            {
                move = Flat(wanderGoal - b.footPosition);
                if (move.magnitude < 2f || now > wanderUntil) { relocating = false; relocateAt = now + 7f + (float)rng.NextDouble() * 4f; move = Vector3.zero; }
                else { move = move.normalized; sprint = true; }
            }
            else if (target != null && (now < backOffUntil || (k.style != 1 && dist < 6f)))
            {
                // Getting distance to shoot, as a player does.
                move = Flat(b.corePosition - target.corePosition).normalized;
            }
            else if (target != null)
            {
                var to = target.corePosition - b.corePosition; to.y = 0f;
                var side = Vector3.Cross(Vector3.up, to.normalized);
                if (k.style == 0) move = Vector3.zero;
                else if (k.style == 1) { move = dist > 4f ? to.normalized : side; sprint = dist > 10f; }
                else move = (dist > 22f ? to.normalized : dist < 10f ? -to.normalized : Vector3.zero) + side * Mathf.Sin(now * 0.5f) * 0.5f;
            }
            else
            {
                if (wanderGoal == Vector3.zero || now > wanderUntil || Flat(wanderGoal - b.footPosition).magnitude < 2f) PickWander();
                move = Flat(wanderGoal - b.footPosition).normalized;
                sprint = true;
            }
            // Stay in the shot: back toward the middle of the frame when he drifts past its sides,
            // too near the camera or too far out (he followed monsters out of frame, 10-05-26).
            var centre = eye + fwd * 20f * Depth;
            var rel = Flat(b.footPosition - eye);
            float relYaw = Vector3.SignedAngle(Flat(fwd), rel, Vector3.up), relDist = rel.magnitude;
            // Walked back, never teleported (a jump back looked like a dash, 10-05-26); a sprint only
            // when well out of it. (A wider field further out with the depth - playtest 10-07-26.)
            if (!entering && (Mathf.Abs(relYaw) > 26f || relDist < 12f * Depth || relDist > 30f * Depth))
            {
                var inside = eye + Quaternion.Euler(0f, Mathf.Clamp(relYaw, -22f, 22f), 0f) * Flat(fwd).normalized * Mathf.Clamp(relDist, 14f * Depth, 28f * Depth);
                move += Flat(inside - b.footPosition).normalized;
                if (Mathf.Abs(relYaw) > 38f || relDist > 40f * Depth) sprint = true;
            }
            if (move.sqrMagnitude > 1f) move.Normalize();
            // MW2 pace: a soldier fighting walks his gun about (no sprint), sprints only to get
            // somewhere; starts, stops and turns eased instead of Commando's instant darts.
            if (target != null && !relocating && !entering) { sprint = false; move *= k.style == 0 ? 0.45f : 0.6f; }
            moveNow = Vector3.MoveTowards(moveNow, move, dt * 2.2f);
            // Calling in a streak he stands still, laptop / case in hand, as players have to.
            if (now < callUntil) { moveNow = Vector3.zero; sprint = false; }
            b.inputBank.moveVector = moveNow;
            b.isSprinting = sprint && moveNow.sqrMagnitude > 0.5f;

            // Aim and fire.
            var aim = target != null ? (target.corePosition - (b.corePosition + Vector3.up * 0.4f)).normalized : (move.sqrMagnitude > 0.01f ? move : Flat(b.transform.forward).normalized);
            b.inputBank.aimDirection = aim;
            bool aiming = target != null && k.style == 0 && gun != Gun.Bag && now >= busyUntil;
            adsFrac = Mathf.MoveTowards(adsFrac, aiming ? 1f : 0f, dt / 0.35f);
            bool ready = target != null && !relocating && !entering && now >= busyUntil && now >= nextShot && gun != Gun.Bag && now >= callUntil && Sees(target);
            if (ready && k.style == 0 && adsFrac < 1f) ready = false; // the scope up first
            if (ready && k.clip > 0) Fire(k, aim);
            else if (now >= busyUntil && gun != Gun.Bag && k.clip == 0 && k.stock > 0)
            {
                int take = Mathf.Min(k.clipSize, k.stock);
                k.stock -= take; k.clip = take;
                busyUntil = now + k.reload;
                events |= Mw2Character.EvReload;
            }

            // The MW2 soldier on the carrier.
            var vel = b.characterMotor != null ? b.characterMotor.velocity : Vector3.zero;
            var face = Flat(target != null ? aim : (vel.sqrMagnitude > 0.5f ? vel : b.transform.forward));
            float yawWant = face.sqrMagnitude > 1e-4f ? Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg : (float.IsNaN(yawNow) ? 0f : yawNow);
            yawNow = float.IsNaN(yawNow) ? yawWant : Mathf.MoveTowardsAngle(yawNow, yawWant, 300f * dt);
            float yaw = yawNow;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var local = Quaternion.Inverse(rot) * Flat(vel);
            input.dt = dt;
            input.stance = 0;
            input.sprinting = (byte)(b.isSprinting ? 1 : 0);
            input.inAir = (byte)(b.characterMotor != null && !b.characterMotor.isGrounded ? 1 : 0);
            input.dead = 0;
            input.moveFwd = local.z / Space.Scale;
            input.moveRight = local.x / Space.Scale;
            input.aimPitch = -Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg;
            input.adsFrac = adsFrac;
            input.weapon = now < callUntil && callWeapon != 0 ? callWeapon : k.index;
            input.primary = 0;
            input.events = events;
            events = 0;
            ch.Step(b.footPosition, yaw, ref input, true);
            if (now >= nextLog)
            {
                nextLog = now + 5f;
                var cam = Camera.main;
                var vp = cam != null ? cam.WorldToViewportPoint(b.corePosition) : Vector3.zero;
                Plugin.Log.LogInfo($"[bot] t {now:F0}: at {b.footPosition} {Flat(b.footPosition - centre).magnitude:F0} m from the shot's centre, viewport ({vp.x:F2},{vp.y:F2},{vp.z:F0}), {gun} clip {k.clip}/{k.stock}, shots {shots}, target {(target != null ? target.name + " " + dist.ToString("F0") + " m" : "none")}, move {move}, character {(ch.Exists ? "on" : "MISSING")}");
            }
        }

        void Fire(Kit k, Vector3 aim)
        {
            var b = Body;
            int h = k.akimbo ? (hand ^= 1) : 0;
            var muzzle = ch.Muzzle(out _, h) ?? (b.corePosition + Vector3.up * 0.4f + aim * 0.6f);
            for (int p = 0; p < k.pellets; p++)
            {
                var dir = k.spread > 0f
                    ? Quaternion.AngleAxis((float)rng.NextDouble() * 360f, aim) * Quaternion.AngleAxis((float)rng.NextDouble() * k.spread, Vector3.Cross(aim, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.Cross(aim, Vector3.up).normalized : Vector3.right) * aim
                    : aim;
                Mw2Strike.Bullet(b, muzzle, dir, k.damage, k.range, 0f, ror2Tracer: false);
                var end = muzzle + dir * 200f;
                if (Physics.Raycast(muzzle, dir, out var hit, 200f, LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore)) end = hit.point;
                Mw2Fx.NoBroadcast++;
                try { Mw2Gunfire.Shot(k.index, muzzle, muzzle, dir, end, p == 0, k.clip == 1, null, world: true); }
                finally { Mw2Fx.NoBroadcast--; }
            }
            // MW2's fire sound for the gun, faded with distance from the player (as teammates' shots).
            string alias = Native.WeaponString(k.index, 5);
            if (!string.IsNullOrEmpty(alias) && PlayerStreaks != null)
            {
                Mw2Killstreaks.NoSoundBroadcast++;
                try { PlayerStreaks.FxSound(alias, muzzle, Volume); }
                finally { Mw2Killstreaks.NoSoundBroadcast--; }
            }
            k.clip--;
            shots++;
            events |= Mw2Character.EvFire;
            // Automatics in bursts; the Intervention works its bolt; the 1887s take turns.
            if (k.style == 2)
            {
                // Long bursts, short breaks (playtest 10-05-26: "shoot his gun more full auto").
                if (burstLeft <= 0f) burstLeft = 9 + rng.Next(9);
                burstLeft--;
                nextShot = now + (burstLeft <= 0f ? 0.25f : k.interval);
            }
            else nextShot = now + k.interval;
        }

        /// His streaks on a schedule, each called in the MW2 way: the laptop (or the sentry case) in
        /// hand a moment, then the streak.
        void TickStreaks()
        {
            if (callIn != null && now >= callUntil) { var go = callIn; callIn = null; try { go(); } catch (Exception e) { Plugin.Log.LogWarning($"[bot] streak failed: {e.Message}"); } }
            if (flying != null) { if (flying.Finished) flying = null; else callUntil = Mathf.Max(callUntil, now + 0.1f); }
            int which;
            if (StreakKind >= 0)
            {
                // A staged life: its one streak, once he's in and has a moment.
                if (callIn != null || streakStep > 0 || now < 9f) return;
                streakStep = 1;
                which = StreakKind;
            }
            else
            {
                if (callIn != null || streakStep >= StreakAt.Length || now < StreakAt[streakStep]) return;
                which = streakStep++;
            }
            var b = Body;
            // Where: the busiest spot off to a side of the shot, well out - bombed in the middle, the
            // smoke filled the frame for seconds (10-05-26). Default: 22 degrees aside, 45 m out.
            float sideSign = rng.NextDouble() < 0.5 ? -1f : 1f;
            Vector3 spot = Mw2Strike.Ground(eye + Quaternion.Euler(0f, sideSign * 22f, 0f) * Flat(fwd).normalized * 45f * Depth + Vector3.up * 3f);
            int most = 0;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb.teamComponent == null || cb.teamComponent.teamIndex != TeamIndex.Monster || !InShot(cb.corePosition, 32f, 30f * Depth, 60f * Depth) || !OnCamera(cb.footPosition)) continue;
                if (Mathf.Abs(Vector3.SignedAngle(Flat(fwd), Flat(cb.corePosition - eye), Vector3.up)) < 14f) continue; // not the middle
                if (Mathf.Abs(cb.footPosition.y - eye.y) > 25f) continue; // fallen off the map
                int n = 0;
                foreach (var o in CharacterBody.readOnlyInstancesList)
                    if (o != null && o.teamComponent != null && o.teamComponent.teamIndex == TeamIndex.Monster && Vector3.Distance(o.corePosition, cb.corePosition) < 8f) n++;
                if (n > most) { most = n; spot = cb.footPosition; }
            }
            // The airstrikes' spot: the busiest cluster anywhere in the shot.
            Vector3 centre = Mw2Strike.Ground(eye + fwd * 32f * Depth + Vector3.up * 3f);
            int mostC = -1;
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb == null || cb.teamComponent == null || cb.teamComponent.teamIndex != TeamIndex.Monster || !InShot(cb.corePosition, 30f, 18f * Depth, 55f * Depth) || !OnCamera(cb.footPosition)) continue;
                if (Mathf.Abs(cb.footPosition.y - eye.y) > 25f) continue;
                int n = 0;
                foreach (var o in CharacterBody.readOnlyInstancesList)
                    if (o != null && o.teamComponent != null && o.teamComponent.teamIndex == TeamIndex.Monster && Vector3.Distance(o.corePosition, cb.corePosition) < 8f) n++;
                if (n > mostC) { mostC = n; centre = cb.footPosition; }
            }
            var heading = Flat(Quaternion.Euler(0f, 90f, 0f) * fwd).normalized; // across the shot
            switch (which % 4)
            {
                case 0: // Sentry Gun: the case out, then planted beside him facing the fight
                    callWeapon = Native.WeaponIndex("killstreak_sentry_mp");
                    callIn = () =>
                    {
                        var job = new SentryJob(streaks, Native.StreakId("sentry"));
                        var at = Mw2Strike.Ground(b.footPosition + Flat(fwd).normalized * 2.5f + Vector3.up * 2f);
                        job.Plant(at, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg);
                        streaks.Start(job);
                        Plugin.Log.LogInfo($"[bot] Sentry Gun down at {at}");
                    };
                    break;
                case 1: // Precision Airstrike: the laptop, then two jets across the shot (one per take: excessive, 10-05-26)
                    callWeapon = Native.WeaponIndex("killstreak_precision_airstrike_mp");
                    spot = centre; // on the middle of the frame (playtest 10-05-26: it looked best there)
                    // Directed for the shot: from behind the camera along its view, low enough to cross
                    // the top of the frame, then on into the fog.
                    callIn = () =>
                    {
                        // Each jet on a monster in the shot as it comes in, from any heading (playtest 10-05-26).
                        streaks.Start(new AirstrikeJob(streaks, false, spot, Flat(fwd).normalized) { Height = 500f, PickTarget = MonsterInShot, Jets = 2 });
                        Plugin.Log.LogInfo($"[bot] Precision Airstrike, jets on the monsters");
                    };
                    break;
                case 2: // Attack Helicopter (playtest 10-07-26: "an attack helicopter that is watching over our
                        // background actor"): in across the shot from a side, then hovering over his shoulder
                        // in the frame, its gun on what's near him; off out the far side before he goes down.
                    callWeapon = Native.WeaponIndex("killstreak_helicopter_mp");
                    callIn = () =>
                    {
                        float s = rng.NextDouble() < 0.5 ? -1f : 1f;
                        var across = Vector3.Cross(Vector3.up, Flat(fwd).normalized) * s;
                        float watch = DieAt > 0f ? Mathf.Max(DieAt - now - 5f, 12f) : 40f;
                        streaks.Start(new HeliJob(streaks, false, across) { Escort = HeliWatch, LeaveAt = watch, LeaveDir = across });
                        Plugin.Log.LogInfo($"[bot] Attack Helicopter over him for {watch:F0} s, in from the {(s < 0f ? "right" : "left")}");
                    };
                    break;
                case 3: // Predator Missile: the laptop, then the missile down out of the sky
                    callWeapon = Native.WeaponIndex("killstreak_predator_missile_mp");
                    callIn = () => { flying = new BotPredatorJob(streaks, spot, fwd); streaks.Start(flying); Plugin.Log.LogInfo($"[bot] Predator Missile on {spot}"); };
                    break;
            }
            callUntil = now + 2.2f;
        }

        /// Where his Attack Helicopter holds (playtest 10-07-26: it flew far too low and kept
        /// jumping - 22-28 m up it scraped the hills, and the vehicle's push out of
        /// the ground popped it up a frame at a time): at the Attack Helicopter's own height over the
        /// ground, beside him toward the middle of the frame and out past him - as far as it takes for
        /// the frame to show it up there (over his shoulder, from behind him).
        Vector3 HeliWatch()
        {
            var b = Body;
            var f = Flat(fwd).normalized;
            var right = Vector3.Cross(Vector3.up, f);
            var at = b != null ? b.footPosition : eye + f * 40f * Depth;
            float toMiddle = Vector3.SignedAngle(f, Flat(at - eye), Vector3.up) > 0f ? -1f : 1f;
            float side = 4f + (float)rng.NextDouble() * 12f;
            Vector3 p = at + Vector3.up * HeliJob.HoverHeight;
            for (float beyond = 10f; beyond <= 160f; beyond += 6f)
            {
                var flat = at + f * beyond + right * toMiddle * side;
                float ground = Physics.Raycast(flat + Vector3.up * 400f, Vector3.down, out var hit, 800f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore) ? hit.point.y : at.y;
                p = new Vector3(flat.x, Mathf.Max(ground, at.y) + HeliJob.HoverHeight, flat.z);
                if (OnCamera(p - Vector3.up * 1.2f)) break;
            }
            return p;
        }

        /// MW2's knife: the lunge on his model and a one-hit stab (its 135 damage, at arm's length).
        void Knife(CharacterBody t)
        {
            var b = Body;
            var from = b.corePosition + Vector3.up * 0.3f;
            var dir = (t.corePosition - from).normalized;
            Mw2Strike.Bullet(b, from, dir, 135f, 150f, 0f, ror2Tracer: false);
            events |= Mw2Character.EvMelee;
            busyUntil = now + 0.8f;
            if (PlayerStreaks != null)
            {
                Mw2Killstreaks.NoSoundBroadcast++;
                try { PlayerStreaks.FxSound("melee_knife_hit_body", t.corePosition, Volume); }
                finally { Mw2Killstreaks.NoSoundBroadcast--; }
            }
            Plugin.Log.LogInfo($"[bot] knifed {t.name}");
        }

        static bool Has(Kit k) => k.clip > 0 || k.stock > 0;
        static void Refill(Kit k) { k.clip = k.clipSize; k.stock = k.stockSize; }
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// Inside the camera's view cone: within `halfDeg` of its direction, `near`-`far` metres out.
        /// A random live monster in the shot (the airstrike jets' marks), else null.
        Vector3? MonsterInShot()
        {
            var all = new List<Vector3>();
            foreach (var cb in CharacterBody.readOnlyInstancesList)
                if (cb != null && cb.healthComponent != null && cb.healthComponent.alive && cb.teamComponent != null && cb.teamComponent.teamIndex == TeamIndex.Monster && InShot(cb.corePosition, 30f, 15f * Depth, 60f * Depth) && OnCamera(cb.footPosition))
                    all.Add(cb.footPosition);
            return all.Count > 0 ? all[rng.Next(all.Count)] : (Vector3?)null;
        }

        bool InShot(Vector3 p, float halfDeg, float near, float far)
        {
            var rel = Flat(p - eye);
            float d = rel.magnitude;
            return d >= near && d <= far && Mathf.Abs(Vector3.SignedAngle(Flat(fwd), rel, Vector3.up)) <= halfDeg;
        }

        bool Sees(CharacterBody cb)
        {
            var from = Body.corePosition + Vector3.up * 0.4f;
            return !Physics.Linecast(from, cb.corePosition, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
        }

        void PickWander()
        {
            // A spot the camera shows (the ground just past the ledge is under the frame, 10-07-26).
            for (int tries = 0; tries < 10; tries++)
            {
                float yaw = (float)(rng.NextDouble() * 70.0 - 35.0);
                float d = (14f + (float)rng.NextDouble() * 14f) * Depth;
                wanderGoal = Mw2Strike.Ground(eye + Quaternion.Euler(0f, yaw, 0f) * fwd * d + Vector3.up * 3f);
                if (OnCamera(wanderGoal)) break;
            }
            wanderUntil = now + 6f * Depth;
        }
    }

    /// The animatic's Predator Missile: MW2's missile (remotemissile_projectile_mp's model and trail)
    /// down out of the sky onto the fight at MW2's cruise speed, its blast and explosion - without the
    /// ride (the camera is the showcase's).
    sealed class BotPredatorJob : StreakJob
    {
        const float Cruise = 3000f, Radius = 450f, Inner = 1000f; // _remotemissile.gsc / the player's Predator
        readonly Vector3 target;
        Vector3 pos, dir;
        GameObject model;
        /// Down (or gone): its pilot may let go of the laptop.
        public bool Finished;

        public BotPredatorJob(Mw2Killstreaks k, Vector3 at, Vector3 camFwd)
        {
            K = k;
            target = at;
            // From high up and a little beyond the target: in through the top of the frame.
            pos = at + Vector3.up * 150f + new Vector3(camFwd.x, 0f, camFwd.z).normalized * 40f;
            dir = (at - pos).normalized;
            uint w = Native.WeaponIndex("remotemissile_projectile_mp");
            Mw2Projectiles.ProjectileOf(w, out var pm, out var trail);
            if (!string.IsNullOrEmpty(pm))
            {
                model = Mw2Prop.Build(pm, k.Body, Space.Scale);
                if (model != null && trail != null) Mw2Prop.SetTrail(model, trail);
            }
        }

        public override bool Update(float dt)
        {
            float step = Cruise * Space.Scale * dt;
            if (Physics.Raycast(pos, dir, out var hit, step + 0.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                Explode(hit.point);
                Finished = true;
                return false;
            }
            pos += dir * step;
            model?.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));
            if (Age >= 8f) Finished = true;
            return !Finished;
        }

        void Explode(Vector3 at)
        {
            var b = Body;
            float reference = Mathf.Max(Plugin.Instance.DamageReference.Value, 1f);
            if (b != null && b.hasEffectiveAuthority)
                new BlastAttack
                {
                    attacker = b.gameObject,
                    inflictor = b.gameObject,
                    teamIndex = b.teamComponent != null ? b.teamComponent.teamIndex : TeamIndex.Player,
                    baseDamage = b.damage * Inner / reference,
                    baseForce = 2000f,
                    position = at,
                    radius = Radius * Space.Scale,
                    falloffModel = BlastAttack.FalloffModel.Linear,
                    procCoefficient = 1f,
                    damageColorIndex = DamageColorIndex.Default,
                    damageType = DamageType.Generic,
                }.Fire();
            Mw2Projectiles.Explosion(Native.WeaponIndex("remotemissile_projectile_mp"), at);
        }

        public override void End() { Finished = true; if (model != null) UnityEngine.Object.Destroy(model); }
    }
}
