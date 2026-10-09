using System;
using System.Collections.Generic;
using RoR2;
using RoR2.CharacterAI;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2RoR2
{
    /// A running killstreak. Numbers are MW2's (docs/killstreak-params.md, cited inline), in MW2
    /// units; RoR2 stand-ins for map-driven or player-vs-player parts are marked "RoR2:".
    abstract class StreakJob
    {
        /// Care Package / Emergency Airdrop flight height over the drop site, MW2 units. MW2 flies them at
        /// the map's airstrikeheight (getFlyHeightOffset); 850 is only its fallback, and the real maps sit
        /// well above it. 850 read as skimming the ground here (playtest 10-03-26): ~1500 (approximate).
        protected const float DropHeight = 1500f;
        public Mw2Killstreaks K;
        protected CharacterBody Body => K.Body;
        public float Age;
        /// Rides the player (camera + freeze), like AC-130 and Chopper Gunner.
        public virtual bool Rides => false;
        /// Player can move but not shoot (carrying a sentry).
        public virtual bool BlocksWeapon => false;
        /// One per airspace slot (helicopters, AC-130); null = no limit.
        public virtual string Airspace => null;
        public abstract bool Update(float dt);
        public virtual void OnCamera(Camera main, Mw2RideCam cam) { }
        public virtual void DrawHud(Camera cam) { }
        public virtual void End() { }

        protected static float U(float mw2Units) => mw2Units * Space.Scale;

        /// Flight heights over MW2's (Killstreaks.FlightHeight; playtest 10-06-26: "things look like they
        /// need to be higher", then "a little bit higher"): planes take it whole, helicopters and the
        /// Harrier - which have to see and shoot what's under them - a third of the raise.
        protected static float Air => Mathf.Max(Plugin.Instance.StreakFlightHeight.Value, 0.5f);
        protected static float HeliAir => 1f + (Air - 1f) / 3f; // 1.5 at the default 2.5
        public static float AirScale => Air;

        /// How close the roaming aircraft (Attack Helicopter, Pave Low, Harrier) may come to you:
        /// diving down onto the player read as unrealistic (playtest 10-06-26).
        protected const float MinDistance = 30f;

        /// `p` pushed out to MinDistance from the owner (and at least 12 m over their head when close).
        protected Vector3 KeepAway(Vector3 p)
        {
            if (Body == null) return p;
            var me = Body.corePosition;
            var d = p - me;
            if (d.magnitude >= MinDistance) return p;
            var flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            // Out along the ground and up: never straight down at you.
            var dir = (flat.normalized * 0.8f + Vector3.up * 0.6f).normalized;
            if (d.y > 0f && d.sqrMagnitude > 1e-4f) dir = Vector3.Slerp(dir, d.normalized, 0.5f).normalized;
            var q = me + dir * MinDistance;
            q.y = Mathf.Max(q.y, me.y + 12f);
            return q;
        }
        /// IW4 vehicle speeds (Vehicle_SetSpeed) are in mph: 17.6 inches per second each.
        protected static float Mph(float mph) => mph * 17.6f * Space.Scale;
        protected static float Rand(float a, float b) => UnityEngine.Random.Range(a, b);
        protected static int RandInt(int a, int bExclusive) => UnityEngine.Random.Range(a, bExclusive);

        protected Vector3 Facing()
        {
            var f = Body.inputBank.aimDirection; f.y = 0f;
            return f.sqrMagnitude > 1e-4f ? f.normalized : Body.transform.forward;
        }

        protected static Vector3 RandomFlat()
        {
            float a = Rand(0f, 360f) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        protected void Sound(string alias, float scale = 1f) => K.PlayAlias(alias, scale);

        /// MW2 sounds are 2D here; fade with distance from the player so far guns stay quiet.
        protected float Near(Vector3 at, float fullMetres = 15f, float zeroMetres = 150f)
        {
            float d = Vector3.Distance(at, Body.corePosition);
            return Mathf.Clamp01(1f - (d - fullMetres) / (zeroMetres - fullMetres));
        }

        protected void WeaponSound(uint weapon, Vector3 at, float scale = 0.6f)
        {
            float v = Near(at) * scale;
            if (v > 0.02f) K.PlayWeapon(weapon, v);
            // The others hear it from where it fires (their own distance falloff).
            if (Mw2Killstreaks.NoSoundBroadcast == 0) Mw2Killstreaks.SoundBroadcast?.Invoke(Native.WeaponString(weapon, 5), at);
        }

        static readonly Dictionary<string, uint> weaponIds = new Dictionary<string, uint>();
        protected static uint Weapon(string name)
        {
            if (!weaponIds.TryGetValue(name, out var id)) weaponIds[name] = id = Native.WeaponIndex(name);
            return id;
        }

        // Mouse look while riding: RoR2's aim still turns with the mouse; its per-frame turn is our input.
        // RoR2's aim is pitch-limited, so each frame it goes back to level (same heading): at its limit
        // the mouse turned nothing and the chopper / AC-130 view stuck short of looking down (playtest
        // 10-04-26). The aim from before the job comes back when it ends (Stop).
        // Read as the camera mode's own pitch / yaw (what the mouse moves; the aim direction is the
        // crosshair ray, a few degrees off level even with the camera level).
        Vector3 lastAim, startAim;
        Vector2 lastPy, startPy;
        bool pyStarted;
        protected Vector2 MouseDelta()
        {
            if (PitchYaw(Body, out var py))
            {
                if (!pyStarted) { pyStarted = true; startPy = lastPy = py; }
                var d = new Vector2(Mathf.DeltaAngle(lastPy.y, py.y), py.x - lastPy.x);
                lastPy = SetAim(Body, Quaternion.Euler(0f, py.y, 0f) * Vector3.forward) ? new Vector2(0f, py.y) : py;
                return d;
            }
            var now = Body.inputBank.aimDirection;
            if (lastAim == Vector3.zero) { lastAim = startAim = now; return Vector2.zero; }
            float dYaw = Vector3.SignedAngle(Flat(lastAim), Flat(now), Vector3.up);
            float dPitch = Mathf.Asin(Mathf.Clamp(-now.y, -1f, 1f)) * Mathf.Rad2Deg - Mathf.Asin(Mathf.Clamp(-lastAim.y, -1f, 1f)) * Mathf.Rad2Deg;
            lastAim = now;
            return new Vector2(dYaw, dPitch);
        }

        /// The job is over (Mw2Killstreaks): its End, then the aim it borrowed back.
        public void Stop()
        {
            End();
            if (pyStarted) SetAim(Body, Quaternion.Euler(startPy.x, startPy.y, 0f) * Vector3.forward);
            else if (startAim != Vector3.zero) SetAim(Body, startAim);
        }

        static System.Reflection.MethodInfo instanceData, setLook;
        static System.Reflection.FieldInfo pyField, pitchField, yawField;
        static Type setFor;
        static bool aimWarned;

        static object CameraData(CharacterBody b)
        {
            if (b == null) return null;
            foreach (var rig in CameraRigController.readOnlyInstancesList)
            {
                if (rig == null || rig.target != b.gameObject || rig.cameraMode == null) continue;
                instanceData = instanceData ?? HarmonyLib.AccessTools.Method(typeof(RoR2.CameraModes.CameraModeBase), "DebugGetInstanceData");
                return instanceData?.Invoke(rig.cameraMode, new object[] { rig });
            }
            return null;
        }

        /// RoR2's camera pitch (x, + down) and yaw (y) in degrees, as the mouse leaves them.
        public static bool PitchYaw(CharacterBody b, out Vector2 py)
        {
            py = Vector2.zero;
            var data = CameraData(b);
            if (data == null) return false;
            pyField = pyField != null && pyField.DeclaringType == data.GetType() ? pyField : HarmonyLib.AccessTools.Field(data.GetType(), "pitchYaw");
            if (pyField == null) return false;
            var pair = pyField.GetValue(data);
            pitchField = pitchField ?? HarmonyLib.AccessTools.Field(pair.GetType(), "pitch");
            yawField = yawField ?? HarmonyLib.AccessTools.Field(pair.GetType(), "yaw");
            if (pitchField == null || yawField == null) return false;
            py = new Vector2((float)pitchField.GetValue(pair), (float)yawField.GetValue(pair));
            return true;
        }

        /// RoR2's camera on `b` looks along `dir`: the rig's camera mode keeps the pitch / yaw in its
        /// per-rig InstanceData (CameraModePlayerBasic.InstanceData.SetPitchYawFromLookVector).
        public static bool SetAim(CharacterBody b, Vector3 dir)
        {
            var data = CameraData(b);
            if (data == null) return false;
            if (setFor != data.GetType()) { setFor = data.GetType(); setLook = HarmonyLib.AccessTools.Method(setFor, "SetPitchYawFromLookVector", new[] { typeof(Vector3) }); }
            if (setLook == null)
            {
                if (!aimWarned) { aimWarned = true; Plugin.Log.LogWarning($"MW2: no SetPitchYawFromLookVector on {data.GetType().FullName}; ride views keep RoR2's pitch limit."); }
                return false;
            }
            setLook.Invoke(data, new object[] { dir });
            return true;
        }

        protected static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

        protected static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }

        protected static void TimeLeft(float seconds)
        {
            float h = Screen.height;
            int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
            Mw2Font.Label(new Rect(0, h * 0.06f, Screen.width, 40), $"{s / 60}:{s % 60:00}", Mw2Hud.S(34f), Color.white, TextAnchor.MiddleCenter);
        }
    }

    /// Ride killstreak intro (initRideKillstreak): 1.0 s laptop, fade to black_bw over 0.75 s, 0.8 s.
    abstract class RideJob : StreakJob
    {
        protected const float Intro = 1.8f;
        public override bool Rides => true;
        protected bool Live => Age >= Intro;
        /// The ride's MW2 HUD menu (Mw2MenuHud.Ride*) and the weapon it shows.
        protected abstract int RideMenu { get; }
        protected virtual string RideWeapon => null;

        /// _ac130.gsc / _helicopter.gsc thermalVision(): +activate flips the thermal between the map's
        /// white hot and missilecam's black hot (fades 0.62 s in, 0.51 s back).
        protected void ThermalToggle()
        {
            // MW2's use key (+activate, F) as well as RoR2's interact (E): it only listened to E, and MW2
            // players press F (playtest 10-06-26: "chopper gunner thermal toggle doesnt work").
            bool use = In.InteractPressed(Body.inputBank) || (Mw2Binds.Use is KeyCode k && In.KeyDown(k));
            if (Live && use) Mw2Thermal.SetBlackHot(!Mw2Thermal.BlackHot, Mw2Thermal.BlackHot ? 0.51f : 0.62f);
        }

        public override void DrawHud(Camera cam)
        {
            if (Age < Intro) Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, Mathf.Clamp01((Age - 1.0f) / 0.75f)));
            else Mw2MenuHud.DrawRide(RideMenu, RideWeapon);
        }
    }

    // ------------------------------------------------------------------ crates

    /// An MW2 crate: falls from its drop height, shows its contents, opens on a 500 ms hold
    /// (crateOwnerCaptureThink), disappears after 90 s (_airdrop.gsc:715). Teammates can take it
    /// too, on a 3 s hold (crateOtherCaptureThink, useHoldThink's 3000 ms): once it has landed it
    /// goes to them (Mw2Net KCrate) as a Remote crate at the same spot (its model arrives as a
    /// prop); whoever takes it, it's gone for everyone.
    class Mw2Crate
    {
        public Vector3 Pos;
        public uint Contents; // 0 = MW2's ammo crate
        public readonly uint Id;
        public readonly bool Remote; // a teammate's crate
        float fall, held, life, still;
        bool landed, announced;
        GameObject go;
        Rigidbody rb;
        readonly CharacterBody owner;
        readonly HashSet<CharacterBody> crushed = new HashSet<CharacterBody>();
        const float CaptureSeconds = 0.5f, OtherCaptureSeconds = 3f, Lifetime = 90f;

        public static uint Roll(bool mega, Func<uint, bool> built)
        {
            uint id = 0;
            for (int tries = 0; tries < 32; tries++)
            {
                uint roll = (uint)UnityEngine.Random.Range(0, int.MaxValue);
                id = mega ? Native.mw2_streak_roll_mega(roll) : Native.mw2_streak_roll_airdrop(roll);
                if (id == 0 || built(id)) break;
            }
            return id;
        }

        /// A teammate's crate that has landed at `at`.
        public Mw2Crate(uint id, Vector3 at, uint contents)
        {
            Id = id;
            Remote = true;
            Pos = at;
            Contents = contents;
            landed = announced = true;
        }

        public Mw2Crate(CharacterBody body, Vector3 from, uint contents)
        {
            Id = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Pos = from;
            Contents = contents;
            owner = body;
            go = Mw2Prop.Build("com_plasticcase_friendly", body, Space.Scale);
            if (go != null)
            {
                go.transform.SetPositionAndRotation(Pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                // _airdrop.gsc: Unlink(), PhysicsLaunchServer( (0,0,0), (randomInt(5) x3) ): a real
                // physics fall that tumbles and settles (it dropped on a straight line before).
                var mf = go.GetComponent<MeshFilter>();
                var box = go.AddComponent<BoxCollider>();
                if (mf != null && mf.sharedMesh != null) { box.center = mf.sharedMesh.bounds.center; box.size = mf.sharedMesh.bounds.size; }
                go.layer = LayerIndex.debris.intVal;
                rb = go.AddComponent<Rigidbody>();
                rb.mass = 40f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.AddForce(new Vector3(UnityEngine.Random.Range(0, 5), UnityEngine.Random.Range(0, 5), UnityEngine.Random.Range(0, 5)), ForceMode.VelocityChange);
                rb.AddTorque(UnityEngine.Random.insideUnitSphere * 2f, ForceMode.VelocityChange);
            }
        }

        /// Returns false when gone (opened or timed out).
        public bool Update(float dt, CharacterBody body, Action<Mw2Crate> open)
        {
            life += dt;
            if (life > Lifetime) { if (!Remote) Mw2Net.SendCrate(false, Id, Pos, Contents); Destroy(); return false; }
            if (landed && !announced) { announced = true; Mw2Net.SendCrate(true, Id, Pos, Contents); }
            if (!landed && rb != null)
            {
                Pos = go.transform.position;
                // Falling fast onto someone crushes them (MW2's crate kills what it lands on).
                if (rb.velocity.y < -6f) Crush();
                // physics_finished: it has come to rest.
                still = rb.velocity.sqrMagnitude < 0.04f && rb.angularVelocity.sqrMagnitude < 0.04f ? still + dt : 0f;
                if (still > 0.4f || life > 20f)
                {
                    landed = true;
                    rb.isKinematic = true;
                    Pos = go.transform.position - go.transform.up * (go.GetComponent<BoxCollider>()?.size.y * 0.5f ?? 0f);
                }
                return true;
            }
            if (!landed)
            {
                fall += Physics.gravity.magnitude * dt;
                float step = fall * dt;
                if (Physics.Raycast(Pos + Vector3.up * 0.1f, Vector3.down, out var hit, step + 0.1f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                {
                    Pos = hit.point;
                    landed = true;
                }
                else Pos += Vector3.down * step;
                if (go != null) go.transform.position = Pos;
                return true;
            }
            bool near = Vector3.Distance(body.footPosition, Pos) < 2.5f;
            if (near && In.Interact(body.inputBank)) held += dt; else held = 0f;
            if (held >= (Remote ? OtherCaptureSeconds : CaptureSeconds)) { open(this); Mw2Net.SendCrate(false, Id, Pos, Contents); Destroy(); return false; }
            return true;
        }

        void Crush()
        {
            if (owner == null) return;
            var at = go.transform.position;
            foreach (var cb in Mw2Strike.Enemies(owner))
            {
                if (cb == null || crushed.Contains(cb) || cb.healthComponent == null || !cb.healthComponent.alive) continue;
                if (Vector3.Distance(cb.corePosition, at) > 1.6f + cb.radius) continue;
                crushed.Add(cb);
                Mw2Strike.Blast(owner, cb.corePosition, 24f, 100000f, 100000f, false);
            }
        }

        public void Destroy()
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            go = null;
        }

        public void Draw(Camera cam, CharacterBody body)
        {
            if (cam == null) return;
            var sp = cam.WorldToScreenPoint(Pos + Vector3.up * 1.2f);
            if (sp.z > 0f)
            {
                float size = Mathf.Clamp(Screen.height * 0.05f * 10f / Mathf.Max(sp.z, 1f), 24f, 72f);
                var r = new Rect(sp.x - size / 2, Screen.height - sp.y - size, size, size);
                string mat = Contents != 0 ? Native.StreakString(Contents, 6) : "waypoint_ammo_friendly";
                if (Mw2Icons.Get(mat) != null) Mw2Icons.Draw(r, mat, Color.white);
            }
            if (!landed) return;
            float w = Screen.width, h = Screen.height;
            if (held > 0f)
            {
                var o = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(w / 2 - 100, h * 0.6f, 200, 8), Texture2D.whiteTexture);
                GUI.color = Color.white; GUI.DrawTexture(new Rect(w / 2 - 100, h * 0.6f, 200 * Mathf.Clamp01(held / (Remote ? OtherCaptureSeconds : CaptureSeconds)), 8), Texture2D.whiteTexture);
                GUI.color = o;
            }
            else if (Vector3.Distance(body.footPosition, Pos) < 2.5f)
                Mw2Font.Label(new Rect(0, h * 0.6f, w, 24), $"Hold Interact for {(Contents != 0 ? Mw2Killstreaks.Pretty(Native.StreakString(Contents, 0)) : "Ammo")}", Mw2Hud.S(24f), Color.white, TextAnchor.MiddleCenter, Mw2Font.Small);
        }
    }

    /// Care Package: a little bird flies in from 15000 out at a random heading (250 mph, 75 mph
    /// after 2 s; _airdrop.gsc:730-839), drops the crate over the marker and leaves at 300 mph.
    class CarePackageJob : StreakJob
    {
        readonly Vector3 site;
        Mw2Vehicle bird;
        Vector3 dir, path, pos;
        // The flight in phases (playtest 10-06-26: it climbed terrain, dropped from 150 m, then
        // teleported a bunch): level in over the top of everything, straight down over the
        // marker to a drop height the sky there allows, the crate, straight back up and away.
        enum Leg { In, Hover, Out }
        Leg leg;
        readonly float cruise, dropY;
        float since;
        Vector3 vel;
        const float Glide = 90f; // metres out it starts down from cruise

        public CarePackageJob(Mw2Killstreaks k, Vector3 site)
        {
            K = k;
            this.site = site;
            var ground = Mw2Strike.Ground(site);
            float r = 4f;
            // The drop height: MW2's 1500 units over the marker, under any roof there.
            float room = Mw2Strike.Ceiling(ground) - ground.y;
            dropY = ground.y + Mathf.Clamp(Mathf.Min(U(DropHeight), room - r - 3f), 8f, U(DropHeight));
            bool roofed = room < U(DropHeight) * HeliAir + r;
            // The heading with the longest clear run in at the drop height (MW2 picks it at random).
            var start = RandomFlat();
            float bestClear = -1f;
            for (int i = 0; i < 8; i++)
            {
                var d = Quaternion.Euler(0f, i * 45f, 0f) * start;
                var at = new Vector3(site.x, dropY, site.z);
                float clear = Physics.SphereCast(at, r, -d, out var h, U(8000f), LayerIndex.world.mask, QueryTriggerInteraction.Ignore) ? h.distance : U(8000f);
                if (clear > bestClear) { bestClear = clear; dir = d; }
            }
            var from = site - dir * U(8000f);
            // Open sky over the marker: cruise in over the top of everything. Under a roof: come in
            // low along that clear heading, as far out as it's clear.
            cruise = roofed ? dropY : Mw2Strike.RouteHeight(from, site, Mathf.Max(dropY, ground.y + U(DropHeight) * HeliAir), 15f, overRoofs: true);
            if (roofed) from = site - dir * Mathf.Max(Mathf.Min(bestClear - r, U(8000f)), 20f);
            path = pos = new Vector3(from.x, cruise, from.z);
            bird = Mw2Vehicle.Spawn("vehicle_little_bird_armed", Body, pos, dir);
            bird?.Damageable(500f); // _airdrop.gsc:1061
            leg = Leg.In;
        }

        public override bool Update(float dt)
        {
            // Shot down (Mw2VehicleHealth): the crash plays out, then the streak is over.
            if (bird != null && bird.Dead) { End(); return false; }
            since += dt;
            var over = new Vector3(site.x, cruise, site.z);
            switch (leg)
            {
                case Leg.In:
                {
                    // One glide (playtest 10-06-26: the straight drop down was "wayy to rigid"): level until
                    // Glide metres out, then easing down as it slows, settling over the marker.
                    float h = new Vector2(site.x - path.x, site.z - path.z).magnitude;
                    float y = Mathf.Lerp(dropY, cruise, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(h / Glide)));
                    path = Vector3.SmoothDamp(path, new Vector3(site.x, y, site.z), ref vel, 2.6f, Mph(250f), dt);
                    if (h < 1.5f && Mathf.Abs(pos.y - dropY) < 2f && vel.magnitude < 3f) { leg = Leg.Hover; since = 0f; }
                    if (since > 25f) { leg = Leg.Hover; since = 0f; } // held up somewhere: drop where it is
                    break;
                }
                case Leg.Hover:
                    path = Vector3.SmoothDamp(path, new Vector3(site.x, dropY, site.z), ref vel, 0.6f, Mph(30f), dt);
                    if (since > 0.6f)
                    {
                        // Linked at tag_ground (32,0,5) then PhysicsLaunched: falls from under the bird.
                        K.AddCrate(new Mw2Crate(Body, pos - Vector3.up * 1.5f, Mw2Crate.Roll(false, K.IsBuilt)));
                        Plugin.Log.LogInfo($"MW2 care package: crate dropped {Age:F1} s after the call, {pos.y - site.y:F0} m over the marker");
                        leg = Leg.Out; since = 0f;
                    }
                    break;
                case Leg.Out:
                {
                    // Nose down and away, climbing as it speeds up.
                    var away = new Vector3(site.x, cruise, site.z) + dir * 250f;
                    path = Vector3.SmoothDamp(path, away, ref vel, 3f, Mph(300f), dt);
                    if (since > 7f) { End(); return false; }
                    break;
                }
            }
            pos = bird != null ? bird.Fly(path, dir, dt) : path;
            return true;
        }

        public override void End() { bird?.Destroy(); bird = null; }
    }

    /// Emergency Airdrop: a C-130 flies the owner-to-marker line (24000 each side, 2000/s),
    /// drops 4 crates 0.1 s apart over the site, each rolled from airdrop_mega (_airdrop.gsc:852-933).
    class EmergencyAirdropJob : StreakJob
    {
        readonly Vector3 site, dir;
        Vector3 path, pos;
        float cruise;
        Mw2Vehicle plane;
        int dropped;
        float nextDrop;
        bool boomed;

        public EmergencyAirdropJob(Mw2Killstreaks k, Vector3 site)
        {
            K = k;
            this.site = site;
            dir = Flat(site - Body.footPosition);
            var start = site - dir * U(12000f);
            // The route's clear height (MW2: the map's airstrikeheight), at least 850 over the site: one
            // level for the run you see (following the terrain read as weird, playtest 10-06-26).
            float h = Mw2Strike.RouteHeight(site - dir * U(8000f), site + dir * U(8000f), site.y + U(DropHeight) * Air * 1.5f, 40f, overRoofs: true);
            cruise = h;
            path = pos = new Vector3(start.x, h, start.z);
            plane = Mw2Vehicle.Spawn("vehicle_ac130_low_mp", Body, pos, dir, scriptFx: false); // doC130FlyBy plays none
            plane?.Loop("veh_ac130_dist_loop", 1000f); // _airdrop.gsc:874
        }

        public override bool Update(float dt)
        {
            path += dir * U(2000f) * dt;
            path.y = cruise; // level (the hull still lifts off anything taller beyond the planned run)
            pos = plane != null ? plane.Fly(path, dir, dt) : path;
            var flat = site - pos; flat.y = 0f;
            float d2 = flat.magnitude;
            if (!boomed && d2 < U(768f)) { boomed = true; Sound("veh_ac130_sonic_boom"); }
            bool overSite = d2 < U(256f) || Vector3.Dot(flat, dir) < 0f;
            if (overSite && dropped < 4 && Age >= nextDrop)
            {
                // Linked at tag_ground (64, 32, -128).
                var at = pos - Vector3.up * U(128f) + Vector3.Cross(Vector3.up, dir) * U(32f);
                K.AddCrate(new Mw2Crate(Body, at, Mw2Crate.Roll(true, K.IsBuilt)));
                dropped++;
                nextDrop = Age + 0.1f;
            }
            if (Vector3.Dot(pos - site, dir) > U(12000f)) { End(); return false; }
            return true;
        }

        public override void End() { plane?.Destroy(); plane = null; }
    }

    // ------------------------------------------------------------------ Counter-UAV / EMP

    /// Counter-UAV (30 s, _uav.gsc:19) blocks the enemy team's radar. RoR2: monsters have no
    /// radar, so it scrambles their targeting instead - enemy AI drops its target every 3 s.
    class JamJob : StreakJob
    {
        readonly float duration;
        readonly string label;
        float nextJam;

        public JamJob(Mw2Killstreaks k, float seconds, string label) { K = k; duration = seconds; this.label = label; }

        public override bool Update(float dt)
        {
            if (Age >= nextJam)
            {
                nextJam = Age + 3f;
                StreakHost.Run(StreakHost.Jam, Body, Vector3.zero); // the host's monster AI
            }
            return Age < duration;
        }

        public override void DrawHud(Camera cam)
        {
            Mw2Font.Label(new Rect(20, Screen.height * 0.10f + Screen.height * 0.22f + 6, 400, 24), $"{label}  {Mathf.CeilToInt(duration - Age)}", Mw2Hud.S(20f), new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleLeft, Mw2Font.Small);
        }
    }

    /// EMP: immediate (the 5 s delay is commented out, _emp.gsc:78), 60 s. MW2 destroys every
    /// enemy vehicle and turret (radiusDamage 5000) and jams the team. RoR2: mechanical enemies
    /// take that 5000, the rest are stunned by the flash and jammed (targeting scrambled) for 60 s.
    class EmpJob : JamJob
    {
        float flash = 1f;

        public EmpJob(Mw2Killstreaks k) : base(k, 60f, "EMP")
        {
            Sound("emp_activate");
            // _emp.gsc: visionSetNaked( "coup_sunblind", 0.1 ), then back to the map's over 3.0 s.
            Mw2Vision.FadeTo("coup_sunblind", 0.1f);
            StreakHost.Run(StreakHost.Emp, Body, Vector3.zero);
        }

        public override bool Update(float dt)
        {
            if (flash > 0f && (flash -= (Time.captureFramerate > 0 ? 1f / Time.captureFramerate : Time.unscaledDeltaTime)) <= 0.9f) { flash = 0f; Mw2Vision.FadeOut(3f); }
            return base.Update(dt);
        }
    }

    // ------------------------------------------------------------------ Nuke

    /// Tactical Nuke: 10 s countdown nobody can stop (scr_nukeTimer 10, cancel mode 0), slow
    /// motion at detonation, everyone dies 1.5 s later (_nuke.gsc). RoR2: every enemy on the
    /// stage dies (credited to you), you survive, the run goes on.
    class NukeJob : StreakJob
    {
        /// The showcase's countdown (0 = MW2's 10 s; the animatic uses 3 "for the sake of time").
        public static float ShowcaseTimer;
        /// Time.time of the last detonation (the pilot times the cameraman's death from it).
        public static float LastDetonation = -1f;
        readonly float Timer = ShowcaseTimer > 0f ? ShowcaseTimer : 10f;
        int lastTick = -1;
        bool incoming, detonated, killed, visionOn, aftermath, cleared;
        float flash, aftermathAt;
        readonly bool teammates; // a teammate's nuke: everything but the kill (his does that)

        /// Time.time the last countdown started (the showcase changes guns while it runs).
        public static float StartedAt = -1f;
        /// The showcase's clean frame: no countdown numbers (the flash and white-out stay).
        public static bool HideCountdown;
        /// The showcase: hold the white-out this many more frames (over RoR2's own red death screen),
        /// then fade it in half a second.
        public static int HoldWhiteFrames;
        bool fastFade;
        public NukeJob(Mw2Killstreaks k, bool teammates = false) { K = k; this.teammates = teammates; StartedAt = Time.time; }

        public override bool Update(float dt)
        {
            float t = Age;
            int sec = Mathf.FloorToInt(t);
            if (t < Timer && sec != lastTick) { lastTick = sec; Sound("ui_mp_nukebomb_timer"); }
            if (!incoming && t >= Timer - 3.3f) { incoming = true; Sound("nuke_incoming"); }
            if (!detonated && t >= Timer)
            {
                detonated = true;
                LastDetonation = Time.time;
                Sound("nuke_explosion");
                Sound("nuke_wave");
                Time.timeScale = 0.25f; // setSlowMotion(1.0, 0.25, 0.5)
                // nukeEffects: nuke_flash (explosions/player_death_nuke_flash) 5000 ahead of the player.
                var cam = Camera.main;
                uint fx = 0;
                if (cam != null)
                {
                    var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                    if (fwd.sqrMagnitude < 0.5f) fwd = Vector3.forward;
                    // _nuke.gsc's pose: angles (0, yaw + 180, 90) - facing the player, rolled 90, so the
                    // effect's up is its forward's right (horizontal). With our default world-up the dust
                    // wall stood on end: a thin column of puffs marching at the camera (playtest 10-06-26).
                    var fi = Space.DirToIw(-fwd);
                    var upU = Space.DirToUnity(new Vec3f(fi.y, -fi.x, 0f));
                    fx = Mw2Fx.Play("explosions/player_death_nuke_flash", cam.transform.position + fwd * U(5000f), -fwd, upU);
                }
                // MW2's flash fills the view; our FX renderer draws it far weaker (playtest 10-03-26), so
                // the white-out stays on top of it.
                flash = 1f;
            }
            // nukeVision: visionSetNaked( "mpnuke", 3 ) 0.25 s after; "mpnuke_aftermath" over 5 s at the
            // deaths (MW2 then holds it to the round's end; here 10 s, then back over 5).
            // (the showcase skips mpnuke: in the half second it showed between the white-out and the
            // death cam it was a few frames of solid red - 10-06-26)
            if (detonated && !visionOn && t >= Timer + 0.25f) { visionOn = true; if (!HideCountdown) Mw2Vision.FadeTo("mpnuke", 3f); }
            if (killed && !aftermath) { aftermath = true; aftermathAt = t; Mw2Vision.FadeTo("mpnuke_aftermath", 5f); }
            if (aftermath && !cleared && t >= aftermathAt + 15f) { cleared = true; Mw2Vision.FadeOut(5f); }
            if (detonated && !killed && t >= Timer + 1.5f)
            {
                killed = true;
                if (!teammates) StreakHost.Run(StreakHost.Nuke, Body, Vector3.zero);
            }
            if (HoldWhiteFrames > 0) { HoldWhiteFrames--; flash = 1f; fastFade = true; }
            else if (detonated) flash = Mathf.Max(0f, flash - dt / (fastFade ? 0.5f : 4f));
            if (killed) Time.timeScale = Mathf.MoveTowards(Time.timeScale, 1f, dt / 2f); // back to 1.0 over 2 s
            bool done = cleared && Time.timeScale >= 0.999f && flash <= 0f;
            if (done) Time.timeScale = 1f;
            return !done;
        }

        public override void DrawHud(Camera cam)
        {
            if (!detonated && !HideCountdown) Mw2Font.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 60), $"0:{Mathf.CeilToInt(Timer - Age):00}", Mw2Hud.S(64f), new Color(1f, 0.3f, 0.2f), TextAnchor.MiddleCenter, Mw2Font.Objective);
            if (flash > 0f) Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(1f, 0.95f, 0.85f, flash));
        }
    }

    // ------------------------------------------------------------------ Sentry

    /// Sentry Gun (_autosentry.gsc): carried, placed with M1, 90 s while placed. Bursts of
    /// randomIntRange(20, 121) shots 50 ms apart, 0.15-0.35 s pauses, overheats past 8.0 heat
    /// (+0.05 a shot), cools 1.0/s. sentry_minigun_mp: 20 damage. RoR2: it can't be destroyed,
    /// and its range (a turret-def value, not in the scripts) is about 2000 units.
    class SentryJob : StreakJob
    {
        const float Timeout = 90f, Range = 2000f, OverheatAt = 8f;
        readonly uint id;
        bool placed;
        GameObject ghost;
        Mw2Vehicle gun;
        Vector3 pos;
        float nextOverheatFx;
        float yaw, timeLeft = Timeout, heat, cooldown, nextShot, burstLeft, pause, spin;
        bool overheated, wasAttack = true;
        CharacterBody target;
        int beeps;
        float nextBeep;

        public SentryJob(Mw2Killstreaks k, uint streakId) { K = k; id = streakId; ghost = Mw2Prop.Build("sentry_minigun_obj", k.Body, Space.Scale); }

        public override bool BlocksWeapon => !placed;

        /// Down where it is: the player's attack press, or the animatic's MW2 soldier at once.
        public void Plant(Vector3 at, float? yawDeg = null)
        {
            if (yawDeg.HasValue) yaw = yawDeg.Value;
            placed = true;
            pos = at;
            if (ghost != null) UnityEngine.Object.Destroy(ghost);
            gun = Mw2Vehicle.Spawn("sentry_minigun", Body, pos, Quaternion.Euler(0f, yaw, 0f) * Vector3.forward, scriptFx: false);
            gun?.Damageable(1000f); // _autosentry.gsc: health 1000
            var tu = gun?.Turret;
            if (tu != null) { tu.yawLimit = 180f; tu.pitchUp = -60f; tu.pitchDown = 45f; }
            Sound("sentry_gun_plant");
        }
        /// The killstreak key while carrying cancels (MW2: +actionslot 4 on the first carry).
        public bool CancelRequested;

        bool PlacePoint(out Vector3 at)
        {
            var probe = Body.footPosition + Facing() * 2f + Vector3.up * 1.5f;
            at = probe;
            if (!Physics.Raycast(probe, Vector3.down, out var hit, 3.5f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) return false;
            at = hit.point;
            return hit.normal.y > 0.7f && K.OnGround;
        }

        public override bool Update(float dt)
        {
            if (!placed)
            {
                if (CancelRequested) return false; // End() hands the streak back
                bool ok = PlacePoint(out var at);
                yaw = Mathf.Atan2(Facing().x, Facing().z) * Mathf.Rad2Deg;
                if (ghost != null) ghost.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
                bool attack = In.Attack(Body.inputBank);
                if (ok && attack && !wasAttack) Plant(at);
                wasAttack = attack;
                return true;
            }
            // Shot down by monsters (Mw2VehicleHealth): it plays out MW2's death, then the streak ends.
            if (gun != null && (gun.Crashing || gun.Dead))
            {
                gun.Place(pos, gun.Forward);
                if (gun.Dead) { End(); return false; }
                return true;
            }
            timeLeft -= dt;
            if (timeLeft <= 0f)
            {
                Sound($"{Mw2Skins.Voice()}_1mc_sentry_gone");
                End();
                return false;
            }
            var turret = gun?.Turret;
            var muzzle = turret != null ? turret.Muzzle : pos + Vector3.up * U(48f);
            if (target == null || !Mw2Strike.IsEnemy(Body, target) || (target.corePosition - muzzle).magnitude > U(Range) || !Mw2Strike.Sees(muzzle, target))
            {
                var t = Mw2Strike.Nearest(Body, muzzle, U(Range), true);
                if (t != target) { beeps = t != null ? 3 : 0; nextBeep = Age; }
                target = t;
            }
            if (beeps > 0 && Age >= nextBeep) { beeps--; nextBeep = Age + 0.1f; Sound("sentry_gun_beep", Near(pos)); } // 3 lock beeps 0.1 s apart
            // Cooling: 1.0 per second whether recovering or idle.
            if (overheated) { heat -= dt; if (heat <= 0f) { heat = 0f; overheated = false; } }
            gun?.Place(pos, Quaternion.Euler(0f, yaw, 0f) * Vector3.forward);
            // _autosentry.gsc sentry_overheat_fx: overheat smoke out of tag_flash while it cools.
            if (overheated && gun != null && Age >= nextOverheatFx) { nextOverheatFx = Age + 0.3f; /* SENTRY_FX_TIME */ gun.BurstTag("smoke/sentry_turret_overheat_smoke", "tag_flash", Vector3.zero); }
            if (target != null)
            {
                // The gun turns and pitches on the tripod (the base stays planted).
                if (turret != null) turret.Aim(target.corePosition, dt);
                bool onTarget = turret != null ? turret.Error < 10f : true;
                if (!overheated && beeps == 0 && onTarget)
                {
                    if (pause > 0f) pause -= dt;
                    else
                    {
                        if (burstLeft <= 0f) burstLeft = RandInt(20, 121);
                        while (Age >= nextShot && burstLeft > 0f)
                        {
                            nextShot = Age + 0.05f;
                            burstLeft--;
                            heat += 0.05f;
                            var dir = (target.corePosition - muzzle).normalized;
                            Mw2Strike.Bullet(Body, muzzle, dir, 20f, 15000f, 1f);
                            // MW2's sentry_minigun_fire (the weapon's own fire sound came out silent, 10-05-26).
                            Sound("sentry_minigun_fire", Near(pos) * 0.12f); // 20 rounds a second: quiet each
                            if (Mw2Killstreaks.NoSoundBroadcast == 0) Mw2Killstreaks.SoundBroadcastVol?.Invoke("sentry_minigun_fire", pos, 0.12f);
                            // sentry_minigun_mp's own world muzzle flash and tracer.
                            var end = muzzle + dir * U(15000f);
                            if (Physics.Raycast(muzzle, dir, out var sh, U(15000f), LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore)) end = sh.point;
                            Mw2Fx.NoBroadcast++;
                            try { Mw2Gunfire.Shot(Weapon("sentry_minigun_mp"), muzzle, muzzle, dir, end, true, false, null, world: true); }
                            finally { Mw2Fx.NoBroadcast--; }
                            if (heat > OverheatAt) { overheated = true; break; }
                        }
                        if (burstLeft <= 0f) pause = Rand(0.15f, 0.35f);
                    }
                }
            }
            else if (!overheated) heat = Mathf.Max(0f, heat - dt);
            // The barrels spin up while it fires and wind down after (10-05-26: "the sentry gun turret
            // not spinning"). The rate isn't in MW2's data: a blur at full speed.
            bool firing = target != null && !overheated && pause <= 0f && burstLeft > 0f;
            spin = Mathf.MoveTowards(spin, firing ? 1800f : 0f, (firing ? 3600f : 900f) * dt);
            gun?.SetSpin(spin);
            return true;
        }

        public override void DrawHud(Camera cam)
        {
            if (!placed)
            {
                // _autosentry.gsc: ForceUseHintOn( &"SENTRY_PLACE" ), the bind filled in like IW4's hints.
                string fire = Mw2Binds.Label(Mw2Binds.For("+attack") ?? KeyCode.Mouse0);
                string hint = Mw2Menus.Localize("SENTRY_PLACE").Replace("{+attack}", fire);
                Mw2Font.LabelCentredCoded(new Rect(0, Screen.height * 0.62f, Screen.width, 24), hint, Mw2Hud.S(24f), Color.white);
                return;
            }
            if (cam == null) return;
            var sp = cam.WorldToScreenPoint(pos + Vector3.up * U(65f));
            if (sp.z > 0f) Mw2Icons.Draw(new Rect(sp.x - 14, Screen.height - sp.y - 28, 28, 28), "compassping_sentry_friendly", Color.white);
        }

        public override void End()
        {
            if (ghost != null) UnityEngine.Object.Destroy(ghost);
            gun?.Destroy();
            ghost = null;
            gun = null;
            // Cancelled before placing: MW2 gives it back.
            if (!placed) K.GiveBack(id);
        }
    }

    // ------------------------------------------------------------------ airstrikes

    /// A jet pass (doPlaneStrike, _airstrike.gsc:847-973): 24000 each side of the target at
    /// 7000/s. 1.0 s before bomb time it drops a cluster bomb; 2.1 s later 12 impact traces run
    /// 0.05 s apart, pitched 55 down to 5 degrees, each losRadiusDamage(512, 200, 30).
    class JetPass
    {
        readonly Vector3 target, dir;
        readonly float height, bombTime;
        readonly Mw2Vehicle jet;
        float t;
        int hits = -1;
        float nextHit;
        public bool Done;
        // The CBU-97 itself (spawnbomb): leaves the jet with its speed / 1.5 under gravity
        // (moveGravity), shown 1.1 s until it bursts (callStrike_bombEffect, _airstrike.gsc:714-760).
        GameObject bomb;
        Vector3 bombPos, bombVel;
        float bombLeft;
        readonly CharacterBody body;

        /// Seconds after the bombs leave that the jet is gone into the fog (the showcase's directed pass:
        /// flying straight down the view it hung at the top of the frame, 10-05-26); 0 = flies on.
        public float VanishAfter;

        public JetPass(CharacterBody body, string model, Vector3 target, Vector3 dir, float extraZ, float baseHeight = 850f)
        {
            this.target = target;
            this.dir = dir;
            this.body = body;
            height = baseHeight + extraZ;
            bombTime = (24000f + 1500f) / 7000f;
            // One level height for the run you can see: MW2's height over the mark, raised to clear the
            // highest ground 8000 units either way. Following the terrain up and down (60-120 m of
            // climbing per pass) read as weird on RoR2's stages (playtest 10-06-26).
            flyY = Mw2Strike.RouteHeight(target - dir * 8000f * Space.Scale, target + dir * 8000f * Space.Scale, target.y + height * Space.Scale * StreakJob.AirScale, 20f, overRoofs: true);
            jet = Mw2Vehicle.Spawn(model, body, Pos(0f), dir);
        }

        readonly float flyY;

        Vector3 Pos(float time) { var p = target + dir * (time * 7000f - 24000f) * Space.Scale; p.y = flyY; return p; }

        public void Update(float dt, Mw2Killstreaks k)
        {
            t += dt;
            jet?.Fly(Pos(t), dir, dt);
            if (VanishAfter > 0f && jet != null && t > bombTime + VanishAfter) jet.SetVisible(false);
            if (hits < 0 && t >= bombTime - 1f)
            {
                hits = 0; nextHit = t + 2.1f; k.PlayAlias("veh_mig29_sonic_boom");
                bomb = Mw2Prop.Build("projectile_cbu97_clusterbomb", body, Space.Scale);
                if (bomb != null) bomb.transform.localScale *= Mathf.Max(Plugin.Instance.VehicleScale.Value, 0.1f);
                bombPos = jet != null ? jet.Position : Pos(t);
                bombVel = dir * (7000f / 1.5f) * Space.Scale;
                bombLeft = 1.1f;
            }
            if (bomb != null)
            {
                bombVel += Vector3.down * 800f * Space.Scale * dt;
                bombPos += bombVel * dt;
                bomb.transform.SetPositionAndRotation(bombPos, Quaternion.LookRotation(bombVel.normalized, Vector3.up));
                bombLeft -= dt;
                if (bombLeft <= 0f)
                {
                    Mw2Fx.Play("explosions/clusterbomb", bombPos, bombVel);
                    UnityEngine.Object.Destroy(bomb); bomb = null;
                }
            }
            while (hits >= 0 && hits < 12 && t >= nextHit)
            {
                float pitch = 55f - hits * (50f / 12f);
                float yawJitter = UnityEngine.Random.Range(0, 10) - 5;
                var aim = Quaternion.AngleAxis(yawJitter, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.Cross(Vector3.up, dir)) * dir;
                var from = target + Vector3.up * 850f * Space.Scale;
                if (Physics.Raycast(from, aim, out var hit, 10000f * Space.Scale, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                {
                    Mw2Strike.Blast(k.Body, hit.point + Vector3.up * 16f * Space.Scale, 512f, 200f, 30f, !Mw2Fx.Ready, killstreak: true);
                    // explosions/clusterbomb's sparks burst into clusterbomb_exp where they land; FX has
                    // no world collision, so the strike plays them at its own trace hits.
                    Mw2Fx.Play("explosions/clusterbomb_exp", hit.point, hit.normal);
                    if (hits % 3 == 0) k.PlayAlias("exp_airstrike_bomb");
                }
                hits++;
                nextHit += 0.05f;
            }
            if (t > 48000f / 7000f) { jet?.Destroy(); Done = true; }
        }

        public void Destroy()
        {
            jet?.Destroy();
            if (bomb != null) UnityEngine.Object.Destroy(bomb);
            bomb = null;
        }
    }

    /// MW2's location selector (beginLocationSelection "map_artillery_selector", _airstrike.gsc:1040):
    /// the laptop opens a map, the mouse moves the target ring, fire places it; Precision
    /// Airstrike and Stealth Bomber then turn an arrow with the mouse for the attack heading and
    /// fire confirms. ADS / the killstreak key cancels (the streak stays). The player can't move
    /// or shoot meanwhile. RoR2: no compass map, so the map is a live top-down view, the
    /// player's facing up; the ring is MW2's targetSize (138 px at 720).
    class LocationSelectJob : StreakJob
    {
        public override bool Rides => true;
        readonly bool chooseDirection;
        readonly Action<Vector3, Vector3> done;
        readonly Action cancelled;
        readonly Vector3 centre, up, right;
        Vector2 cursor;        // metres from centre (x right, y up on screen)
        float arrow;           // degrees clockwise from screen-up
        bool placed, finished;
        const float HalfHeight = 110f; // metres of map above/below the centre
        const float MetresPerDegree = 3.2f;

        public LocationSelectJob(Mw2Killstreaks k, bool chooseDirection, Action<Vector3, Vector3> done, Action cancelled)
        {
            K = k;
            this.chooseDirection = chooseDirection;
            this.done = done;
            this.cancelled = cancelled;
            centre = Body.footPosition;
            up = Facing();
            right = new Vector3(up.z, 0f, -up.x);
            // Start on what you were aiming at, like MW2 starts on your position.
            var aim = Mw2Strike.AimPoint(Body, HalfHeight) - centre;
            cursor = new Vector2(Vector3.Dot(aim, right), Vector3.Dot(aim, up));
            MouseDelta();
        }

        Vector3 World(Vector2 c) => Mw2Strike.Ground(centre + right * c.x + up * c.y);

        public override bool Update(float dt)
        {
            if (finished) return false;
            var md = MouseDelta();
            if (Age > 0.15f && (In.Ads(Body.inputBank) || In.StreakKeyDown))
            {
                finished = true;
                cancelled?.Invoke();
                return false;
            }
            if (!placed)
            {
                cursor += new Vector2(md.x, -md.y) * MetresPerDegree;
                float lim = HalfHeight * 0.95f;
                cursor = new Vector2(Mathf.Clamp(cursor.x, -lim * Screen.width / Screen.height, lim * Screen.width / Screen.height), Mathf.Clamp(cursor.y, -lim, lim));
            }
            else arrow += md.x * 2.5f;
            if (Age > 0.2f && In.AttackPressed(Body.inputBank))
            {
                K.PlayAlias("mouse_click");
                if (!placed && chooseDirection) { placed = true; arrow = 0f; return true; }
                var d = Quaternion.AngleAxis(arrow, Vector3.up) * up;
                finished = true;
                done?.Invoke(World(cursor), d);
                return false;
            }
            return true;
        }

        public override void OnCamera(Camera main, Mw2RideCam cam)
        {
            float height = 400f;
            cam.PlaceOrtho(main, centre + Vector3.up * height, Quaternion.LookRotation(Vector3.down, up), HalfHeight);
        }

        public override void DrawHud(Camera cam)
        {
            float w = Screen.width, h = Screen.height;
            var scan = Mw2Icons.Get("minimap_scanlines");
            if (scan != null)
            {
                var o = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTextureWithTexCoords(new Rect(0, 0, w, h), scan, new Rect(0, Time.time * 0.05f, w / 256f, h / 256f), true);
                GUI.color = o;
            }
            float px = h / (2f * HalfHeight);
            var at = new Vector2(w / 2f + cursor.x * px, h / 2f - cursor.y * px);
            float size = h * (138f / 720f);
            var ring = Mw2Icons.Get("map_artillery_selector");
            var r = new Rect(at.x - size / 2f, at.y - size / 2f, size, size);
            if (ring != null) GUI.DrawTexture(r, ring); else Fill(new Rect(at.x - 3, at.y - 3, 6, 6), Color.white);
            if (placed)
            {
                var arrowTex = Mw2Icons.Get("map_location_selector_arrow");
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(arrow, at);
                if (arrowTex != null) GUI.DrawTexture(new Rect(at.x - size / 2f, at.y - size, size, size), arrowTex);
                else Fill(new Rect(at.x - 2, at.y - size * 0.7f, 4, size * 0.7f), Color.white);
                GUI.matrix = m;
            }
            // You, on the map.
            Fill(new Rect(w / 2f - 4, h / 2f - 4, 8, 8), new Color(0.4f, 1f, 0.4f, 0.9f));
            HintText(placed ? "Choose a direction" : "Select a location", h);
        }

        static GUIStyle hint;
        static void HintText(string text, float h)
        {
            hint = hint ?? new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0, h * 0.1f, Screen.width, 40), text, hint);
        }
    }

    /// Precision Airstrike: 3 jets 1.5-2.5 s apart along your heading. Harrier Strike: 2 jets
    /// at a random heading, then a hovering Harrier. RoR2: the map selector becomes where you aim.
    class AirstrikeJob : StreakJob
    {
        readonly List<JetPass> passes = new List<JetPass>();
        readonly Vector3 target, dir;
        readonly string model;
        int left;
        float next;
        readonly bool harrier;

        /// The showcase's directed pass (playtest 10-05-26: "close enough to be in the top of the frame and
        /// then fly off into the fog"): the jets' height over the target, MW2 units (MW2: 850 + up to 500).
        public float? Height;
        /// Each jet's own mark, picked as it comes in (the showcase: a monster in the shot), along the
        /// job's heading. MW2's carpet starts ~600 units past the mark and runs on, so the mark is pulled back
        /// that far for the first bombs to walk through the monster.
        public Func<Vector3?> PickTarget;
        /// The showcase's jet count (MW2: 3).
        public int Jets { set => left = Mathf.Clamp(value, 1, 3); }

        public AirstrikeJob(Mw2Killstreaks k, bool harrier, Vector3? at = null, Vector3? heading = null)
        {
            K = k;
            this.harrier = harrier;
            target = at ?? Mw2Strike.AimPoint(Body, 200f);
            dir = harrier ? RandomFlat() : heading ?? Facing();
            model = harrier ? "vehicle_av8b_harrier_jet_mp" : "vehicle_mig29_desert";
            left = harrier ? 2 : 3;
        }

        public override bool Update(float dt)
        {
            if (left > 0 && Age >= next)
            {
                float extra = Height.HasValue ? UnityEngine.Random.Range(0, 120) : passes.Count == 0 ? UnityEngine.Random.Range(0, 500) : UnityEngine.Random.Range(0, 200);
                // The showcase's pass: each jet its own lane and height, one after another, so the
                // contrails don't stack into one line (playtest 10-05-26); they fly on into the fog.
                var lane = Height.HasValue ? Vector3.Cross(Vector3.up, dir).normalized * new[] { 0f, -16f, 16f }[passes.Count % 3] : Vector3.zero;
                float lift = Height.HasValue ? new[] { 0f, 90f, 45f }[passes.Count % 3] : 0f;
                var picked = PickTarget?.Invoke();
                if (picked.HasValue)
                {
                    // Still along the view (a random heading kept them out of the shot, 10-05-26), but
                    // each over its own monster: the lanes fall where the monsters are.
                    passes.Add(new JetPass(Body, model, picked.Value - dir * 900f * Space.Scale, dir, extra + lift, Height ?? 850f));
                }
                else passes.Add(Height.HasValue ? new JetPass(Body, model, target + lane, dir, extra + lift, Height.Value) : new JetPass(Body, model, target, dir, extra));
                left--;
                // Harriers: each pass its own aircraft, seconds apart, then the one that stays comes back
                // a while later (playtest 10-06-26: back to back they read as one jet, the hover one right
                // behind). MW2's MiGs keep their 1.5-2.5 s.
                next = Age + (harrier ? Rand(5f, 7f) : Rand(1.5f, 2.5f));
                if (left == 0 && harrier) K.Start(new HarrierJob(K, target, dir), delay: Rand(9f, 12f));
            }
            foreach (var p in passes) if (!p.Done) p.Update(dt, K);
            return left > 0 || passes.Exists(p => !p.Done);
        }

        public override void End() { foreach (var p in passes) p.Destroy(); }
    }

    /// Stealth Bomber: a B-2 along your heading, 12000 before to 48000 past the target at
    /// 2000/s; a bomb every 0.1 s from 4500 before to 4500 past, landing within 512 of its
    /// track after 0.85 * (height / 2000) s; losRadiusDamage(896, 300, 50) and shellshock
    /// (8 s at the centre to 4 s at 512) on enemies (_airstrike.gsc:549-655).
    /// RoR2: shellshock becomes a slow for the same time.
    class StealthBomberJob : StreakJob
    {
        readonly Vector3 target, dir;
        readonly float height;
        Mw2Vehicle plane;
        float nextBomb;
        bool boomed;
        int bombs;
        struct Falling { public Vector3 at; public float when; public GameObject bomb; public Vector3 from; public float start; }
        readonly List<Falling> falling = new List<Falling>();

        public StealthBomberJob(Mw2Killstreaks k, Vector3? at = null, Vector3? heading = null)
        {
            K = k;
            target = at ?? Mw2Strike.AimPoint(Body, 200f);
            dir = heading ?? Facing();
            height = 950f + UnityEngine.Random.Range(0, 1000);
            // 950-1950 over the target, or the route's clear height if the stage rises higher.
            heightY = Mw2Strike.RouteHeight(target - dir * U(12000f), target + dir * U(12000f), target.y + U(height) * Air, 25f, overRoofs: true); // level over the run you see
            plane = Mw2Vehicle.Spawn("vehicle_b2_bomber", Body, Pos(), dir);
        }

        readonly float heightY;
        float nextBayFx;
        float Along => Age * 2000f - 12000f;
        Vector3 Pos() { var p = target + dir * Along * Space.Scale; p.y = heightY; return p; }

        public override bool Update(float dt)
        {
            plane?.Fly(Pos(), dir, dt);
            float d = Along;
            if (!boomed && Mathf.Abs(d) < 1500f) { boomed = true; Sound("veh_b2_sonic_boom"); }
            // playBombFx: stealth_bomb_mp out of both bomb bays every 0.5 s while it bombs.
            if (Mathf.Abs(d) < 4500f && Age >= nextBayFx && plane != null)
            {
                nextBayFx = Age + 0.5f;
                plane.BurstTag("explosions/stealth_bomb_mp", "tag_left_alamo_missile", new Vector3(-0.15f, -0.5f, 0f));
                plane.BurstTag("explosions/stealth_bomb_mp", "tag_right_alamo_missile", new Vector3(0.15f, -0.5f, 0f));
            }
            if (Mathf.Abs(d) < 4500f && Age >= nextBomb)
            {
                nextBomb = Age + 0.1f;
                var over = Pos();
                var rnd = UnityEngine.Random.insideUnitCircle * 512f * Space.Scale;
                // _airstrike.gsc:626: traced down from the plane, not from the aim point's height
                // (from there a ray started inside any hill higher than the target).
                var from = new Vector3(over.x + rnd.x, over.y, over.z + rnd.y);
                var ground = Physics.Raycast(from, Vector3.down, out var bh, 3000f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)
                    ? bh.point : Mw2Strike.Ground(new Vector3(from.x, target.y, from.z));
                // The bomb itself (projectile_stealth_bomb_mk84, playtest 10-06-26: "i dont see the stealth
                // bomber dropping any bombs ... i see the explosions"): every other one out of the bays,
                // falling under MW2's gravity from the plane's real height (it flies higher than MW2 now).
                // Out of the bays with the bomber's speed, arcing down (playtest 10-06-26: they came in
                // vertical): where it lands is that far on along the track.
                float fall = Mathf.Sqrt(2f * Mathf.Max(over.y - ground.y, 1f) / U(800f));
                var flight = new Vector3(from.x, over.y, from.z) + dir * U(2000f) * 0.8f * fall;
                if (Physics.Raycast(flight, Vector3.down, out var lh, 3000f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) ground = lh.point;
                fall = Mathf.Sqrt(2f * Mathf.Max(over.y - ground.y, 1f) / U(800f));
                GameObject bomb = null;
                if (bombs % 2 == 0 && plane != null)
                {
                    bomb = Mw2Prop.Build("projectile_stealth_bomb_mk84", Body, Space.Scale);
                    if (bomb != null) bomb.transform.localScale *= Mathf.Max(Plugin.Instance.VehicleScale.Value, 0.1f);
                }
                falling.Add(new Falling { at = ground, when = Age + fall, bomb = bomb, from = new Vector3(from.x, over.y, from.z) - Vector3.up * 1.5f, start = Age });
            }
            for (int i = falling.Count - 1; i >= 0; i--)
            {
                var fb = falling[i];
                if (fb.bomb != null)
                {
                    // A ballistic arc: steady forward speed, falling faster and faster, the nose along
                    // the flight path.
                    float k = Mathf.Clamp01((Age - fb.start) / Mathf.Max(fb.when - fb.start, 0.01f));
                    var p = Vector3.Lerp(fb.from, new Vector3(fb.at.x, fb.from.y, fb.at.z), k);
                    p.y = Mathf.Lerp(fb.from.y, fb.at.y, k * k);
                    float T = Mathf.Max(fb.when - fb.start, 0.01f);
                    var vel = (new Vector3(fb.at.x - fb.from.x, 0f, fb.at.z - fb.from.z) / T) + Vector3.down * (2f * (fb.from.y - fb.at.y) * k / T);
                    fb.bomb.transform.SetPositionAndRotation(p, Quaternion.LookRotation(vel.sqrMagnitude > 1e-4f ? vel.normalized : dir, Vector3.up));
                }
                if (Age < fb.when) continue;
                if (fb.bomb != null) UnityEngine.Object.Destroy(fb.bomb);
                var at = fb.at;
                Mw2Strike.Blast(Body, at + Vector3.up * 16f * Space.Scale, 896f, 300f, 50f, bombs % 2 == 0, "explosions/artilleryExp_dirt_brown", killstreak: true); // level.mortareffect
                // The bays' explosions/stealth_bomb_mp (two every 0.5 s) is a falling bomb whose impact
                // effect is clusterbomb_exp; without FX collision it's played where the bombs land.
                if (bombs % 5 == 1 || bombs % 5 == 3) Mw2Fx.Play("explosions/clusterbomb_exp", at, Vector3.up);
                if (bombs % 2 == 0) Sound("exp_airstrike_bomb", Near(at, 20f, 200f));
                Shellshock(at);
                bombs++;
                falling.RemoveAt(i);
            }
            if (Along > 48000f) { End(); return false; }
            return true;
        }

        void Shellshock(Vector3 at) => StreakHost.Run(StreakHost.Shellshock, Body, at, 512f * Space.Scale);

        public override void End()
        {
            plane?.Destroy(); plane = null;
            foreach (var f in falling) if (f.bomb != null) UnityEngine.Object.Destroy(f.bomb);
            falling.Clear();
        }
    }

    // ------------------------------------------------------------------ air support

    /// The Harrier that stays (_harrier.gsc): flies in from 24000 out at 250 mph, hovers about
    /// 1200 over the ground drifting toward the enemies (15 mph), 45 s. Targets within 8192 and
    /// beyond 768 (2D) in sight, best = least turn from the nose; fires bursts of 25
    /// harrier_20mm_mp (40 damage) 0.1 s apart with 1 s between, once within 10 degrees.
    class HarrierJob : StreakJob
    {
        Mw2Vehicle jet;
        Vector3 pos, goal, nose;
        readonly Vector3 inDir;
        bool arrived, leaving;
        float nextMove, burst, nextShot, pause;
        CharacterBody target;

        float inY;
        bool spawned;
        static float HarrierAir => Mathf.Max(Air * 0.8f, 1f); // 2x MW2's at the default (playtest: "needs to be higher for sure")

        public HarrierJob(Mw2Killstreaks k, Vector3 target, Vector3 dir)
        {
            K = k;
            inDir = dir;
            var tg = Mw2Strike.Ground(target);
            goal = new Vector3(tg.x, Mw2Strike.RingHeight(tg, U(256f), U(1200f) * HarrierAir, overRoofs: true), tg.z); // getCorrectHeight, over the top
            goal = KeepAway(goal);
            pos = goal - dir * U(8000f); // in from 8000 out (was 24000)
            // Level in over the top of everything, then down onto the hover (playtest 10-06-26: it "kinda
            // teleported in and acted really weird on the way in").
            inY = Mw2Strike.RouteHeight(pos, goal, goal.y, 20f, overRoofs: true);
            pos.y = inY;
            nose = dir;
            // The jet itself comes when the job starts: started with a delay after the strike, it was
            // spawned at once and hung there in sight while the bombs went off (playtest 10-06-26).
        }

        public override bool Update(float dt)
        {
            if (jet == null && !spawned)
            {
                spawned = true;
                jet = Mw2Vehicle.Spawn("vehicle_av8b_harrier_jet_mp", Body, pos, nose);
                jet?.Damageable(3000f); // _harrier.gsc:63
            }
            // Shot down (Mw2VehicleHealth): the crash plays out, then the streak is over.
            if (jet != null && jet.Dead) { End(); return false; }
            if (leaving)
            {
                pos += nose * Mph(250f) * dt + Vector3.up * U(900f) * dt * 0.3f;
                if (jet != null) pos = jet.Fly(pos, nose, dt);
                if (Age > 45f + 8f) { End(); return false; }
                return true;
            }
            if (Age >= 45f) { leaving = true; Sound("harrier_fly_away"); return true; }
            float speed = arrived ? Mph(15f) : Mph(250f);
            // Flying in: level at the route height until over the hover spot, then down onto it.
            var goalNow = goal;
            if (!arrived && new Vector2(goal.x - pos.x, goal.z - pos.z).magnitude > U(400f)) goalNow.y = Mathf.Max(goal.y, inY);
            if (!arrived && (goal - pos).magnitude < U(768f))
            {
                arrived = true;
                jet?.StopAttached("smoke/jet_contrail"); // stopHarrierWingFx on station (_harrier.gsc:450)
                jet?.StopLoop("harrier_engine_high");
                jet?.Loop("harrier_idle_high", 500f); // hovering
            }
            if (arrived && Age >= nextMove)
            {
                // Toward the average enemy position; ground + 1200 (getCorrectHeight).
                nextMove = Age + UnityEngine.Random.Range(3, 6);
                Vector3 sum = Vector3.zero; int n = 0;
                foreach (var cb in Mw2Strike.Enemies(Body)) if ((cb.corePosition - pos).magnitude < U(8192f)) { sum += cb.corePosition; n++; }
                var centre = n > 0 ? sum / n : Body.corePosition;
                var cg = Mw2Strike.Ground(centre);
                goal = new Vector3(cg.x, Mw2Strike.RingHeight(cg, U(256f), U(1200f) * HarrierAir, overRoofs: true), cg.z); // getCorrectHeight, over the top
                goal = Mw2Strike.SeeingSpot(Body, centre, new[] { U(1500f), U(3000f) }, U(1200f) * HarrierAir, U(8192f), goal, openSky: true); // layered stages
                goal = KeepAway(goal);
            }
            pos = Vector3.MoveTowards(pos, goalNow, speed * dt);
            if (jet != null && jet.Crashing) return true;
            if (target == null || !Mw2Strike.IsEnemy(Body, target) || !Valid(target)) target = Best();
            var want = target != null ? Flat(target.corePosition - pos) : (arrived ? nose : inDir);
            nose = Vector3.RotateTowards(nose, want, 45f * Mathf.Deg2Rad * dt, 0f); // setYawSpeed 45
            if (jet != null) pos = jet.Fly(pos, nose, dt);
            if (target != null && arrived && Vector3.Angle(nose, Flat(target.corePosition - pos)) <= 10f)
            {
                if (pause > 0f) pause -= dt;
                else
                {
                    if (burst <= 0f) burst = 25f;
                    while (Age >= nextShot && burst > 0f)
                    {
                        nextShot = Age + 0.1f;
                        burst--;
                        var aimAt = target.footPosition + Vector3.up * U(50f);
                        Mw2Strike.Bullet(Body, pos, (aimAt - pos).normalized, 40f, 16000f, 1.5f);
                        WeaponSound(Weapon("harrier_20mm_mp"), pos, 0.4f);
                    }
                    if (burst <= 0f) pause = 1f;
                }
            }
            return true;
        }

        bool Valid(CharacterBody cb)
        {
            var flat = cb.corePosition - pos; flat.y = 0f;
            // Ranges along the ground: RoR2 stages put aircraft higher than MW2's maps did, and a 3D
            // range shrank what they could reach below (playtest: high vehicles must still hit the ground).
            return flat.magnitude <= U(8192f) && flat.magnitude >= U(768f) && Mw2Strike.Sees(pos, cb);
        }

        CharacterBody Best()
        {
            CharacterBody best = null; float bestAngle = float.MaxValue;
            foreach (var cb in Mw2Strike.Enemies(Body))
            {
                if (!Valid(cb)) continue;
                float a = Vector3.Angle(nose, Flat(cb.corePosition - pos));
                if ((cb.corePosition - pos).magnitude > U(2000f)) a += 40f; // getBestTarget: +40 beyond 2000
                if (a < bestAngle) { bestAngle = a; best = cb; }
            }
            return best;
        }

        public override void End() => jet?.Destroy();
    }

    /// Attack Helicopter (60 s, cobra_20mm_mp: 40-shot bursts 50 ms apart, 0.5-2.0 s between,
    /// highest-threat visible enemy within 3500; _helicopter.gsc) and Pave Low (60 s, half
    /// speed, two door guns of pavelow_minigun_mp: 40-80 shot bursts 0.1 s apart, 1-2 s pauses).
    /// Flight is heli_fly_well: pick the attack spot with the most enemies, fly there at 30-50 mph
    /// (accel 15-30) and hover, then the next; heading turns at setyawspeed 75 deg/s and never
    /// follows targets: the turret (tag_flash) aims on its own. RoR2: no map nodes, so attack
    /// spots are around the enemy cluster near you at MW2's flight height (approximate).
    class HeliJob : StreakJob
    {
        readonly bool pavelow;
        readonly Mw2Vehicle heli;
        Vector3 pos, vel, goal, face;
        float entry = -1f, hoverUntil, speed, accel, nextMuzzleLog;
        bool leaving, arrived;
        // Approximate (map path nodes in MW2); 1000 read as skimming the ground (playtest 10-04-26).
        const float Height = 1500f, Standoff = 1200f;
        readonly Dictionary<CharacterBody, float> antithreat = new Dictionary<CharacterBody, float>();
        readonly Gun[] guns;

        class Gun
        {
            public Vector3 offset;
            public CharacterBody target;
            public float burst, nextShot, pause, unseen;
            public Vector2 aimOffset;
            public string tag;
            public Transform model; // the Pave Low's weapon_minigun on its gunner tag
        }

        public override string Airspace => "chopper";

        /// The showcase's escort: where to hold (asked again at each move) instead of MW2's busiest-area
        /// standoff, when to leave (the job's age) and which way.
        public Func<Vector3> Escort;
        /// Its hover height over the ground (MW2's 1500 units, times the streak height setting).
        public static float HoverHeight => U(Height) * HeliAir;
        public float LeaveAt = float.MaxValue;
        public Vector3 LeaveDir;
        bool escortSet;

        public HeliJob(Mw2Killstreaks k, bool pavelow, Vector3 from = default)
        {
            K = k;
            this.pavelow = pavelow;
            var dir = from.sqrMagnitude > 1e-4f ? Flat(from).normalized : RandomFlat();
            // In from 6000 out (was 15000: a 570 m crossing over whatever stood between).
            pos = Body.footPosition - dir * U(6000f) + Vector3.up * U(Height) * HeliAir;
            face = dir;
            heli = Mw2Vehicle.Spawn(pavelow ? "vehicle_pavelow" : "vehicle_cobra_helicopter_fly_low", Body, pos, dir);
            if (heli != null) heli.YawSpeed = 75f;
            heli?.Damageable(pavelow ? 3000f : 1500f); // heli_maxhealth 1500, flares x2
            guns = pavelow
                ? new[] { new Gun { offset = new Vector3(-1.4f, -1.2f, 1.5f), tag = "tag_gunner_left" }, new Gun { offset = new Vector3(1.4f, -1.2f, 1.5f), tag = "tag_gunner_right" } }
                : new[] { new Gun { offset = new Vector3(0f, U(-160f), U(144f)), tag = "tag_flash" } };
            // _helicopter.gsc:807-820: spawnTurret pavelow_minigun_mp, setModel weapon_minigun, linked at
            // tag_gunner_left / right.
            if (pavelow && heli != null) foreach (var g in guns) g.model = heli.AttachModel("weapon_minigun", g.tag);
            PickGoal(true);
        }

        float Slow => pavelow ? 0.5f : 1f;

        /// get_best_area_attack_node: the spot with the most enemies near you; we hover at a
        /// standoff ring around it so the hull isn't parked on the player.
        void PickGoal(bool entering)
        {
            Vector3 best = Body.footPosition; int bestN = -1;
            foreach (var a in Mw2Strike.Enemies(Body))
            {
                if ((a.footPosition - Body.footPosition).magnitude > 120f) continue;
                int n = 0;
                foreach (var b in Mw2Strike.Enemies(Body)) if ((a.footPosition - b.footPosition).magnitude < U(1024f)) n++;
                if (n > bestN) { bestN = n; best = a.footPosition; }
            }
            if (Escort != null)
            {
                goal = KeepAway(Escort());
                face = Flat(best - goal);
                if (face.sqrMagnitude < 1e-3f) face = Flat(Body.footPosition - goal);
                speed = (entering ? Mph(80f) : Mph(30f + RandInt(0, 20))) * Slow;
                accel = (entering ? Mph(30f) : Mph(15f + RandInt(0, 15))) * Slow;
                arrived = false;
                return;
            }
            var ring = RandomFlat() * U(Standoff) * Rand(0.6f, 1f);
            // MW2's height over the highest ground between here and the goal: one height for the
            // whole move (over one sample at the goal it climbed and dropped over every rock).
            var g = Mw2Strike.Ground(best + ring);
            // Capped over the goal's ground: clearing the tallest peak on the route put the Pave Low
            // 368 m up (10-06-26); the hull's look-ahead climbs over what's in the way.
            goal = new Vector3(g.x, Mathf.Min(Mw2Strike.RouteHeight(pos, g, g.y + U(Height) * HeliAir, 15f, overRoofs: true), g.y + U(Height) * HeliAir * 2.5f), g.z);
            goal = KeepAway(goal);
            // Layered stages: a spot that actually sees the enemies (under overhangs, beside floors).
            if (!entering) goal = Mw2Strike.SeeingSpot(Body, best, new[] { U(Standoff) * 0.5f, U(Standoff) }, U(Height) * HeliAir, U(pavelow ? 5000f : 3500f), goal, openSky: true);
            face = Flat(best - goal); // look across the fight, not at one target
            // heli_fly_well: 30-50 mph, accel 15-30 (entry path flies in faster).
            speed = (entering ? Mph(80f) : Mph(30f + RandInt(0, 20))) * Slow;
            accel = (entering ? Mph(30f) : Mph(15f + RandInt(0, 15))) * Slow;
            arrived = false;
        }

        public override bool Update(float dt)
        {
            // Shot down (Mw2VehicleHealth): the crash plays out, then the streak is over.
            if (heli != null && heli.Dead) { End(); return false; }
            // An escort flies its way in to where it holds (set after the constructor's MW2 entry goal).
            if (Escort != null && !escortSet) { escortSet = true; PickGoal(true); }
            if (leaving)
            {
                // Vehicle_SetSpeed(100, 45) out of the map.
                var away = LeaveDir.sqrMagnitude > 1e-4f ? Flat(LeaveDir) : Flat(pos - Body.footPosition);
                vel = Vector3.MoveTowards(vel, (away + Vector3.up * 0.15f).normalized * Mph(100f), Mph(45f) * dt);
                pos += vel * dt;
                if (heli != null) pos = heli.Fly(pos, away, dt);
                if (Age > Mathf.Min(entry + 60f, LeaveAt) + 10f) { End(); return false; }
                return true;
            }
            // Vehicle motion: accelerate toward the goal, brake to stop on it (no snapping).
            var to = goal - pos;
            float dist = to.magnitude;
            float brake = Mathf.Sqrt(2f * accel * Mathf.Max(dist - U(64f), 0f));
            var want = dist > 1e-3f ? to / dist * Mathf.Min(speed, brake) : Vector3.zero;
            vel = Vector3.MoveTowards(vel, want, accel * dt);
            pos += vel * dt; // (the 30 m keep-away is on its goals: snapping the position read as a teleport)
            if (!arrived && dist < U(256f)) // setneargoalnotifydist(256)
            {
                arrived = true;
                hoverUntil = Age + Rand(3f, 6f);
                if (entry < 0f) entry = Age;
            }
            if (entry < 0f && Age > 20f) entry = Age; // entry path took too long: start the clock anyway
            if (arrived && Age >= hoverUntil) PickGoal(false);
            if ((entry >= 0f && Age >= entry + 60f) || Age >= LeaveAt) { leaving = true; heli?.LogStats("leaving"); }
            // Heading: along the flight while travelling, across the fight while hovering.
            var heading = vel.sqrMagnitude > 4f && !arrived ? Flat(vel) : face;
            if (heli != null) pos = heli.Fly(pos, heading, dt);
            if (entry >= 0f) foreach (var g in guns) Fire(g, dt, heli != null ? heli.Forward : heading);
            return true;
        }

        void Fire(Gun g, float dt, Vector3 face)
        {
            if (heli != null && heli.Crashing) return; // going down: guns quiet
            var turret = !pavelow && heli != null ? heli.Turret : null;
            // From the model as drawn this frame: the Cobra's turret muzzle, the Pave Low's door
            // gunners (tag_gunner_left / right). The old fixed offsets from the flight-path point
            // ignored scale, bank and bob, so rounds came out of the middle of the hull.
            Vector3 muzzle;
            if (turret != null) muzzle = turret.Muzzle;
            else if (heli == null || !(heli.TagPoint(g.tag, out muzzle) || heli.TagPoint("tag_barrel", out muzzle) || heli.TagPoint("tag_turret", out muzzle)))
                muzzle = pos + Quaternion.LookRotation(face) * g.offset;
            float range = pavelow ? 5000f : 3500f; // heli_visual_range 3500; Pave Low: pavelow_minigun_mp min-damage range
            // A target kept through a second of lost sight (a rock, the hull's own bob): the gun swung
            // between targets each time the line flickered (playtest 10-07-26: "snapping to targets").
            g.unseen = g.target != null && !Mw2Strike.Sees(muzzle, g.target) ? g.unseen + dt : 0f;
            if (g.target == null || !Mw2Strike.IsEnemy(Body, g.target) || Mw2Strike.FlatDistance(g.target.corePosition, muzzle) > U(range) || g.unseen > 1f)
            {
                g.target = Pick(muzzle, range);
                g.unseen = 0f;
            }
            if (g.target == null) return;
            // setTurretTargetEnt: the turret slews onto the target with the burst's offset and
            // only opens up once it's on (waitOnTargetOrDeath); between bursts it keeps tracking.
            var off0 = g.aimOffset * Mathf.Clamp01(g.burst / 40f);
            var aimPoint = g.target.footPosition + new Vector3(off0.x, U(40f), off0.y);
            if (turret != null) turret.Aim(aimPoint, dt);
            if (g.model != null)
            {
                // The door gun swings onto its target like MW2's linked turret.
                var want = Quaternion.LookRotation(aimPoint - g.model.position, Vector3.up);
                g.model.rotation = Quaternion.RotateTowards(g.model.rotation, want, 240f * dt);
            }
            if (g.pause > 0f) { g.pause -= dt; return; }
            if (!Mw2Strike.Sees(muzzle, g.target)) return; // held on it, not shooting through the rock
            if (turret != null && turret.Error > 6f) return;
            if (g.burst <= 0f)
            {
                g.burst = pavelow ? RandInt(40, 81) : 40f; // heli_turretClipSize
                // attack_primary: start 96 out (60% of the time near the target's facing), converge in.
                float ang = Rand(0f, 360f) * Mathf.Deg2Rad;
                g.aimOffset = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * U(96f);
            }
            float gap = pavelow ? 0.1f : 0.05f;
            while (Age >= g.nextShot && g.burst > 0f)
            {
                g.nextShot = Age + gap;
                g.burst--;
                // Aim offset: radius 96 around the target at z 40, tightening each shot.
                var off = g.aimOffset * Mathf.Clamp01(g.burst / 40f) + UnityEngine.Random.insideUnitCircle * U(6f);
                var aimAt = g.target.footPosition + new Vector3(off.x, U(40f), off.y);
                float dist = (aimAt - muzzle).magnitude / Space.Scale;
                float dmg = pavelow ? (dist <= 2000f ? 40f : Mathf.Lerp(40f, 20f, Mathf.InverseLerp(2000f, 5000f, dist))) : 10f;
                if (heli?.Hull != null && Age >= nextMuzzleLog)
                {
                    nextMuzzleLog = Age + 1f;
                    var l = heli.Hull.InverseTransformPoint(muzzle);
                    Plugin.Log.LogInfo($"[heli] {(pavelow ? "pavelow" : "cobra")} muzzle on hull ({l.x:F2},{l.y:F2},{l.z:F2}) m");
                }
                // From the barrel: rounds go where the turret actually points (bursts walk on).
                var dir = turret != null ? Vector3.Slerp(turret.Barrel, (aimAt - muzzle).normalized, 0.5f).normalized : (aimAt - muzzle).normalized;
                Mw2Strike.Bullet(Body, muzzle, dir, dmg, 16000f, 1f);
                WeaponSound(Weapon(pavelow ? "pavelow_minigun_mp" : "cobra_20mm_mp"), pos, 0.35f);
            }
            if (g.burst <= 0f)
            {
                g.pause = pavelow ? Rand(1f, 2f) : Rand(0.5f, 2f);
                antithreat[g.target] = (antithreat.TryGetValue(g.target, out var a) ? a : 0f) + 100f;
            }
        }

        CharacterBody Pick(Vector3 from, float range)
        {
            CharacterBody best = null; float bestScore = float.MinValue;
            foreach (var cb in Mw2Strike.Enemies(Body))
            {
                float d = Mw2Strike.FlatDistance(cb.corePosition, from) / Space.Scale; // along the ground (see Harrier)
                if (d > range || !Mw2Strike.Sees(from, cb)) continue;
                float score = (range - d) / range * 100f - (antithreat.TryGetValue(cb, out var a) ? a : 0f);
                if (score > bestScore) { bestScore = score; best = cb; }
            }
            return best;
        }

        public override void End() => heli?.Destroy();
    }

    // ------------------------------------------------------------------ rides

    /// Chopper Gunner (40 s after entry, _helicopter.gsc:774): you ride as gunner of an Apache
    /// that holds over the busiest enemy area 5-10 s at a time. Your gun is cobra_player_minigun_mp
    /// (75 damage, 80 ms, explosive rounds: radius 128, 120 to 40). View clamp: 180 left/right,
    /// 0 up, 180 down. RoR2: hover height/orbit radius approximate; thermal is Mw2Thermal (grey world, white-hot enemies).
    class ChopperGunnerJob : RideJob
    {
        float orbitY = -1f, orbitYs = -1f;
        Vector3 areaS, vel;
        readonly Mw2Vehicle heli;
        Vector3 pos, area;
        float angle, entry = -1f, nextArea, yaw, pitch = 35f, nextShot;
        const float Radius = 1200f, Height = 1200f;

        public override string Airspace => "chopper";
        protected override int RideMenu => Mw2MenuHud.RideChopper;
        protected override string RideWeapon => "heli_remote_mp"; // _helicopter.gsc gives heli_remote_mp for the ride

        public ChopperGunnerJob(Mw2Killstreaks k)
        {
            K = k;
            Mw2Thermal.Begin(); // heliRide: ThermalVisionOn
            area = Body.footPosition;
            var dir = RandomFlat();
            pos = area - dir * U(6000f) + Vector3.up * U(Height) * HeliAir; // in from 6000 out (was 15000)
            pos.y = Mw2Strike.RouteHeight(pos, area, pos.y, 15f, overRoofs: true); // in over the top
            heli = Mw2Vehicle.Spawn("vehicle_apache_mp", Body, pos, dir);
            heli?.Damageable(1500f);
            if (heli != null) heli.YawSpeed = 75f; // setyawspeed: turns, never snaps
            yaw = Mathf.Atan2(Facing().x, Facing().z) * Mathf.Rad2Deg;
        }

        public override bool Update(float dt)
        {
            // Shot down (Mw2VehicleHealth): the crash plays out, then the streak is over.
            if (heli != null && heli.Dead) { K.Splash("Chopper Gunner", "Shot down", 2f); End(); return false; }
            if (Age >= nextArea)
            {
                nextArea = Age + 5f + UnityEngine.Random.Range(0, 5);
                // heli_fly_well: the spot with the most enemies within 1024.
                Vector3 best = Body.footPosition; int bestN = -1;
                foreach (var a in Mw2Strike.Enemies(Body))
                {
                    int n = 0;
                    foreach (var b in Mw2Strike.Enemies(Body)) if ((a.footPosition - b.footPosition).magnitude < U(1024f)) n++;
                    if (n > bestN && (a.footPosition - Body.footPosition).magnitude < 120f) { bestN = n; best = a.footPosition; }
                }
                area = best;
                orbitY = -1f;
            }
            if (orbitY < 0f)
            {
                // The orbit clears everything on its ring, and the way over to it (sliding between
                // fight areas it ground along Shattered Abodes' arches, 10-06-26).
                orbitY = Mw2Strike.RingHeight(Mw2Strike.Ground(area), U(Radius), U(Height) * HeliAir, overRoofs: true);
                orbitY = Mathf.Max(orbitY, Mw2Strike.RouteHeight(pos, area, pos.y, 15f, overRoofs: true));
            }
            // A new fight area slides the orbit over and its height eases (they jumped, and the ride
            // camera with them: "chopper gunner was being choppy flying around", playtest 10-06-26).
            if (orbitYs < 0f) { orbitYs = orbitY; areaS = area; }
            orbitYs = Mathf.MoveTowards(orbitYs, orbitY, 5f * dt);
            areaS = Vector3.MoveTowards(areaS, area, Mph(25f) * dt);
            angle += Mph(30f) / U(Radius) * Mathf.Rad2Deg * dt;
            var orbit = new Vector3(areaS.x, orbitYs, areaS.z) + new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f, Mathf.Sin(angle * Mathf.Deg2Rad)) * U(Radius);
            pos = Vector3.SmoothDamp(pos, orbit, ref vel, 1.2f, Mph(100f), dt);
            if (entry < 0f && ((pos - orbit).magnitude < 3f || Age > Intro + 8f)) entry = Age;
            if (heli != null) pos = heli.Fly(pos, Flat(areaS - pos), dt);
            if (!Live) { MouseDelta(); return true; }
            ThermalToggle();
            var md = MouseDelta();
            yaw += md.x;
            pitch = Mathf.Clamp(pitch + md.y, 0f, 89f);
            if (In.Attack(Body.inputBank) && Age >= nextShot)
            {
                nextShot = Age + 0.08f;
                var eye = pos - Vector3.up * 1.2f;
                var dir = View() * Quaternion.Euler(UnityEngine.Random.Range(-0.6f, 0.6f), UnityEngine.Random.Range(-0.6f, 0.6f), 0f) * Vector3.forward;
                Mw2Strike.Bullet(Body, eye, dir, 75f, 16000f);
                if (Physics.Raycast(eye, dir, out var hit, U(16000f), LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore))
                    Mw2Strike.Blast(Body, hit.point, 128f, 120f, 40f, false, killstreak: true);
                WeaponSound(Weapon("cobra_player_minigun_mp"), Body.corePosition, 0.5f);
            }
            return entry < 0f || Age < entry + 40f;
        }

        Quaternion View() => Quaternion.Euler(pitch, yaw, 0f);

        public override void OnCamera(Camera main, Mw2RideCam cam)
        {
            heli?.SetVisible(!Live);
            if (!Live) { cam.Drop(); return; }
            cam.Place(main, pos - Vector3.up * 1.2f, View(), 65f);
        }

        public override void DrawHud(Camera cam)
        {
            base.DrawHud(cam); // MW2's remote_chopper_overlay: viper reticle, grain, vignette
            if (!Live) return;
            if (entry >= 0f) TimeLeft(entry + 40f - Age);
        }

        public override void End() { heli?.Destroy(); Mw2Thermal.End(); }
    }

    /// AC-130 (40 s, _ac130.gsc): circles the area once every 70 s; view clamped 35 degrees
    /// each way. 105 mm (radius 600, 1200 to 300, clip 1, 5.0 s reload), 40 mm (150, 500 to 50,
    /// clip 4, 300 ms, 3.0 s), 25 mm (32, 500 to 10, clip 20, 70 ms, 1.5 s). Shells travel at
    /// 3000 / 5000 / 10000. R or 1-3 switch guns. RoR2: orbit radius/altitude are a map rig tag
    /// in MW2 (not in scripts) - approximate here; thermal is Mw2Thermal (grey world, white-hot enemies).
    class Ac130Job : RideJob
    {
        const float Duration = 40f, Radius = 3500f, Altitude = 4500f;
        Vector3 centre;
        float orbit, yawOff, pitchOff, darken;
        int gun; // 0 = 105, 1 = 40, 2 = 25
        readonly int[] clip = { 1, 4, 20 };
        readonly float[] reloadUntil = new float[3];
        float nextShot;
        float gunReadyAt = -1f;
        struct Shell { public Vector3 at; public float when; public int gun; }
        readonly List<Shell> shells = new List<Shell>();
        static readonly string[] Names = { "ac130_105mm_mp", "ac130_40mm_mp", "ac130_25mm_mp" };
        static readonly float[] Radii = { 600f, 150f, 32f }, Inner = { 1200f, 500f, 500f }, Outer = { 300f, 50f, 10f };
        static readonly float[] FireTime = { 0.1f, 0.3f, 0.07f }, Reload = { 5f, 3f, 1.5f }, Speed = { 3000f, 5000f, 10000f };
        static readonly int[] Clip = { 1, 4, 20 };
        // ads_zoom_fov of ac130_105mm_mp / 40mm / 25mm.
        static readonly float[] Zoom = { 45f, 20f, 5f };

        public override string Airspace => "ac130";
        protected override int RideMenu => Mw2MenuHud.RideAc130;
        protected override string RideWeapon => Names[gun];

        public Ac130Job(Mw2Killstreaks k)
        {
            K = k;
            Mw2Thermal.Begin(); // _ac130.gsc: ThermalVisionOn, white hot
            centre = Body.footPosition;
            orbit = Rand(0f, 360f);
            // Under a roof (caves, covered stages) the orbit stays below it, or every shell would land
            // on the ceiling.
            altitude = U(Altitude);
            if (Physics.Raycast(centre + Vector3.up * 2f, Vector3.up, out var roof, U(Altitude), LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                altitude = Mathf.Max(roof.distance - 6f, 15f);
            // "fasten_seatbelts" is only MW2's missile-incoming warning (_ac130.gsc:1666), not the start.
            // The plane itself on its orbit (MW2's planeModel, playAC130Effects): seen from the
            // ground and by everyone else; hidden from the rider's own camera, which sits in it.
            plane = Mw2Vehicle.Spawn("vehicle_ac130_low_mp", Body, Plane(), Tangent());
            // _ac130.gsc:400 loops veh_ac130_ext_dist, which MW2's multiplayer ships as null.wav: the
            // plane is silent, as in MW2 (playtest 10-04-26: the drone the mod played isn't in the game).
            plane?.Damageable(1000f); // _ac130.gsc:402
        }

        Mw2Vehicle plane;
        Vector3 Tangent() => new Vector3(-Mathf.Sin(orbit * Mathf.Deg2Rad), 0f, Mathf.Cos(orbit * Mathf.Deg2Rad));

        public override void End() { plane?.Destroy(); plane = null; Mw2Thermal.End(); if (ambience != 0) { Mw2Audio.LoopStop(ambience); ambience = 0; } }

        // ---- the crew on the radio (_ac130.gsc context_Sensative_Dialog, as MW2's multiplayer runs it:
        // its "enemy in sight" lines look at an empty list there, so they never play) ----
        float shotLineAt = -1f, radioFreeAt, lastTransmission = -10f, killWindowEnd = -1f;
        int killsInWindow;
        readonly List<string> radioQueue = new List<string>();
        static readonly string[] Actions = { "ac130_plt_scanrange", "ac130_plt_cleanup", "ac130_plt_targetreset", "ac130_plt_azimuthsweep" };
        static readonly float[] ActionTimeouts = { 70f, 80f, 55f, 100f };
        readonly float[] actionPlayed = { -1000f, -1000f, -1000f, -1000f };
        static readonly string[] KillSingle = { "ac130_plt_gottahurt", "ac130_fco_iseepieces", "ac130_fco_oopsiedaisy", "ac130_fco_goodkill", "ac130_fco_yougothim", "ac130_fco_yougothim2", "ac130_fco_thatsahit", "ac130_fco_directhit", "ac130_fco_rightontarget", "ac130_fco_okyougothim", "ac130_fco_within2feet" };
        static readonly string[] KillGroup = { "ac130_fco_nice", "ac130_fco_directhits", "ac130_fco_iseepieces", "ac130_fco_goodkill", "ac130_fco_yougothim", "ac130_fco_yougothim2", "ac130_fco_thatsahit", "ac130_fco_directhit", "ac130_fco_rightontarget", "ac130_fco_okyougothim" };
        readonly HashSet<string> played = new HashSet<string>();

        /// playSoundOverRadio: one line at a time, 4 s each, in the team's voice; a line that has to be
        /// heard (kills) waits its turn, the rest are dropped while the radio is busy.
        void Radio(string alias, bool force)
        {
            if (Age < radioFreeAt) { if (force) radioQueue.Add(alias); return; }
            string voiced = $"{Mw2Skins.Voice()}_{alias}";
            Sound(voiced);
            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[radio] {voiced} ({(Mw2Steps.Exists(voiced) ? "plays" : "MISSING")})");
            radioFreeAt = Age + 4f;
            lastTransmission = radioFreeAt;
        }

        void RadioUpdate()
        {
            if (!Live) return;
            if (radioQueue.Count > 0 && Age >= radioFreeAt) { var a = radioQueue[0]; radioQueue.RemoveAt(0); Radio(a, false); }
            // Kills over a 1 s window: two or more get a "small group" line, one gets a line 1 time in 3.
            if (killWindowEnd > 0f && Age >= killWindowEnd)
            {
                killWindowEnd = -1f;
                bool group = killsInWindow >= 2;
                killsInWindow = 0;
                if (group || UnityEngine.Random.Range(0, 3) == 1) Radio(Pick(group ? KillGroup : KillSingle), true);
            }
            // Filler: 3 s with nothing on the radio, an "action" line (each with its own timeout).
            if (Age >= radioFreeAt && Age - lastTransmission >= 3f)
            {
                lastTransmission = Age;
                int g = UnityEngine.Random.Range(0, Actions.Length);
                if (Age - actionPlayed[g] >= ActionTimeouts[g]) { actionPlayed[g] = Age; Radio(Actions[g], false); }
            }
        }

        /// A random line not played yet; all played, start over.
        string Pick(string[] lines)
        {
            var fresh = new List<string>();
            foreach (var l in lines) if (!played.Contains(l)) fresh.Add(l);
            if (fresh.Count == 0) { foreach (var l in lines) played.Remove(l); fresh.AddRange(lines); }
            var s = fresh[UnityEngine.Random.Range(0, fresh.Count)];
            played.Add(s);
            return s;
        }

        /// The gunner killed something (_damage.gsc: level notify "ai_killed" for the AC-130 player).
        public void OnKill()
        {
            killsInWindow++;
            if (killWindowEnd < 0f) killWindowEnd = Age + 1f;
        }

        // _ac130.gsc init_sounds: setAC130Ambience( "ambient_ac130_int1" ), the cabin the gunner hears.
        uint ambience;
        bool ambienceTried;
        void Ambience()
        {
            if (ambienceTried || !Live) return;
            ambienceTried = true; // not in the zones the mod loads (a map's ambient track): then silent
            ambience = Mw2Audio.LoopStart("ambient_ac130_int1");
            if (ambience != 0) Mw2Audio.LoopVolume(ambience, 1f);
            else Plugin.Log.LogInfo("MW2 AC-130: ambient_ac130_int1 not playable");
        }

        float altitude;
        Vector3 Plane() => centre + new Vector3(Mathf.Cos(orbit * Mathf.Deg2Rad), 0f, Mathf.Sin(orbit * Mathf.Deg2Rad)) * U(Radius) + Vector3.up * altitude;

        Quaternion View()
        {
            var look = Quaternion.LookRotation(centre - Plane(), Vector3.up);
            return look * Quaternion.Euler(pitchOff, yawOff, 0f);
        }

        public override bool Update(float dt)
        {
            Ambience();
            // Shot down (Mw2VehicleHealth): the crash plays out, then the streak is over.
            if (plane != null && plane.Dead) { K.Splash("AC-130", "Shot down", 2f); End(); return false; }
            orbit += 360f / 70f * dt; // rotateyaw(360, 70)
            if (plane != null)
            {
                plane.Place(Plane(), Tangent(), 20f); // banked into the left-hand pylon turn
                plane.SetVisible(!Live);
            }
            for (int i = shells.Count - 1; i >= 0; i--)
            {
                if (Age < shells[i].when) continue;
                var s = shells[i];
                Mw2Strike.Blast(Body, s.at, Radii[s.gun], Inner[s.gun], Outer[s.gun], true, killstreak: true);
                if (s.gun == 0) darken = 1f;
                shells.RemoveAt(i);
            }
            darken = Mathf.Max(0f, darken - dt / 0.8f);
            if (!Live) { MouseDelta(); return true; }
            ThermalToggle();
            var md = MouseDelta();
            yawOff = Mathf.Clamp(yawOff + md.x, -35f, 35f);
            pitchOff = Mathf.Clamp(pitchOff + md.y, -35f, 35f);
            int pick = In.GunPick();
            if (pick < 0 && In.KeyDown(KeyCode.R)) pick = (gun + 1) % 3;
            if (pick >= 0 && pick != gun) { gun = pick; Sound("ac130_weapon_switch"); }
            if (clip[gun] == 0 && Age >= reloadUntil[gun]) clip[gun] = Clip[gun];
            if (gunReadyAt > 0f && Age >= gunReadyAt) { gunReadyAt = -1f; Radio("ac130_gnr_gunready1", false); }
            if (shotLineAt > 0f && Age >= shotLineAt) { shotLineAt = -1f; if (UnityEngine.Random.Range(0, 2) == 0) Radio("ac130_gnr_shot1", false); }
            RadioUpdate();
            bool fire = gun == 0 ? In.AttackPressed(Body.inputBank) : In.Attack(Body.inputBank);
            if (fire && clip[gun] > 0 && Age >= nextShot)
            {
                nextShot = Age + FireTime[gun];
                clip[gun]--;
                if (clip[gun] == 0) reloadUntil[gun] = Age + Reload[gun];
                var from = Plane();
                var dir = View() * Vector3.forward;
                if (Physics.Raycast(from, dir, out var hit, U(30000f), LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore))
                    shells.Add(new Shell { at = hit.point, when = Age + hit.distance / U(Speed[gun]), gun = gun });
                K.PlayWeapon(Weapon(Names[gun]), 0.6f);
                // Everyone else (playtest 10-06-26: what does the other player get?): the gun's world flash
                // on the plane and its third-person fire sound from up there, distance-faded on their
                // side. Not played here - the gunner sits in the plane.
                uint wpn = Weapon(Names[gun]);
                string flash = Mw2Gunfire.WorldFlash(wpn);
                if (flash != null) Mw2Fx.Broadcast?.Invoke(flash, from, dir);
                string fire3p = Native.WeaponString(wpn, 5);
                if (!string.IsNullOrEmpty(fire3p)) Mw2Killstreaks.SoundBroadcastVol?.Invoke(fire3p, from, gun == 0 ? 3f : 2f);
                if (gun == 0) { gunReadyAt = Age + 5.5f; shotLineAt = Age + 0.5f; } // _ac130.gsc: shot line 0.5 s after, ready 5 s later
            }
            return Age < Intro + Duration || shells.Count > 0;
        }

        public override void OnCamera(Camera main, Mw2RideCam cam)
        {
            if (!Live) { cam.Drop(); return; }
            var p = Plane();
            for (int i = 0; i < 24 && Physics.CheckSphere(p, 6f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore); i++) p += Vector3.up * 4f;
            cam.Place(main, p, View(), Zoom[gun]);
        }

        public override void DrawHud(Camera cam)
        {
            base.DrawHud(cam); // MW2's ac130_hud: the gun's overlay, the side text blocks, gun list, key hints
            if (!Live) return;
            float w = Screen.width, h = Screen.height;
            if (darken > 0f) Fill(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, 0.6f * Mathf.Clamp01(darken * 1.25f)));
            // ac130_hud's "Reloading" (its visibility op isn't in the evaluator): centre, 130 of
            // 480 below the middle.
            if (clip[gun] == 0)
                Mw2Font.Label(new Rect(0, h / 2f + h * 130f / 480f - 20f, w, 40), Mw2Menus.Localize("AC130_RELOADING"), Mw2Hud.S(28f), Color.white, TextAnchor.MiddleCenter);
            TimeLeft(Intro + Duration - Age);
        }
    }

    /// Killstreak effects RoR2 only lets the host apply (direct damage, kills, buffs). A client's
    /// streak asks the host (Mw2Net KStreak), which runs them with the client's body as the owner.
    static class StreakHost
    {
        public const byte Emp = 1, Nuke = 2, Shellshock = 3, Flash = 4, Concussion = 5, Jam = 6;

        /// `a` / `b`: radii in metres (the caller's own scale: the host may not be in MW2 mode).
        public static void Run(byte what, CharacterBody owner, Vector3 at, float a = 0f, float b = 0f)
        {
            if (owner == null) return;
            if (NetworkServer.active) Apply(what, owner, at, a, b);
            else Mw2Net.SendStreak(owner, what, at, a, b);
        }

        /// Server only.
        public static void Apply(byte what, CharacterBody owner, Vector3 at, float a = 0f, float b = 0f)
        {
            if (!NetworkServer.active || owner == null) return;
            switch (what)
            {
                case Emp:
                    foreach (var cb in new List<CharacterBody>(Mw2Strike.Enemies(owner)))
                    {
                        if ((cb.bodyFlags & CharacterBody.BodyFlags.Mechanical) != 0)
                        {
                            cb.healthComponent.TakeDamage(new DamageInfo
                            {
                                attacker = owner.gameObject, inflictor = owner.gameObject, damage = Mw2Strike.Damage(owner, 5000f),
                                position = cb.corePosition, procCoefficient = 1f, crit = false, damageType = DamageType.Generic,
                            });
                        }
                        var stun = cb.GetComponent<SetStateOnHurt>();
                        if (stun != null && stun.canBeStunned) stun.SetStun(2f); // RoR2: the flash
                    }
                    break;
                case Nuke:
                    foreach (var cb in new List<CharacterBody>(Mw2Strike.Enemies(owner)))
                        cb.healthComponent.Suicide(owner.gameObject, owner.gameObject);
                    break;
                case Flash:
                case Concussion:
                    Mw2Projectiles.ApplyTactical(what == Flash, at, a, b, owner);
                    break;
                case Jam:
                    // Monster AI runs on the host: drop every enemy's target (Counter-UAV / EMP).
                    foreach (var cb in Mw2Strike.Enemies(owner))
                    {
                        var master = cb.master;
                        if (master == null) continue;
                        foreach (var ai in master.GetComponents<BaseAI>())
                            if (ai != null && ai.currentEnemy != null) ai.currentEnemy.Reset();
                    }
                    break;
                case Shellshock:
                    float r = a > 0f ? a : 512f * Space.Scale;
                    foreach (var cb in Mw2Strike.Enemies(owner))
                    {
                        float d = Vector3.Distance(cb.corePosition, at);
                        if (d > r) continue;
                        cb.AddTimedBuff(RoR2Content.Buffs.Slow50, Mathf.Lerp(8f, 4f, d / r));
                    }
                    break;
            }
        }
    }
}
