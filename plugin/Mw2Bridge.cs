using System;
using System.Linq;
using System.Collections.Generic;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// IW4 space is right-handed Z-up (x forward, y left, z up), in map units.
    /// Unity is left-handed Y-up (x right, y up, z forward), in metres.
    /// One map unit = Space.Scale metres, chosen so MW2's 70-unit player matches the
    /// survivor's capsule height (pick scales from gameplay invariants, as IW4L does).
    static class Space
    {
        public static float Scale = 0.0254f;

        public static Vector3 ToUnity(Vec3f p) => new Vector3(-p.y, p.z, p.x) * Scale;
        public static Vec3f ToIw(Vector3 u)
        {
            Vector3 s = u / Scale;
            return new Vec3f(s.z, -s.x, s.y);
        }
        public static Vector3 DirToUnity(Vec3f d) => new Vector3(-d.y, d.z, d.x);
        public static Vec3f DirToIw(Vector3 d) => new Vec3f(d.z, -d.x, d.y);
    }

    static unsafe class UnityWorld
    {
        public static int Mask = LayerIndex.world.mask;
        const float ClipEpsilonUnits = 0.125f; // Quake-family surface clip epsilon

        /// Ground the sim may stand and walk on. MW2's is normal.z >= 0.7 (~45.6 degrees); RoR2's
        /// stages are built for its survivors' steeper stable slope, so slopes a survivor walks up
        /// stopped the MW2 soldier (playtest 10-04-26). The survivor's own limit, never below MW2's.
        static float walkNormalZ = 0.7f;

        public static void SetWalkableSlope(float survivorMaxDegrees)
        {
            float deg = Mathf.Clamp(survivorMaxDegrees, 45.57f, 70f);
            walkNormalZ = Mathf.Min(0.7f, Mathf.Cos(deg * Mathf.Deg2Rad));
            Plugin.Log.LogInfo($"MW2 walkable slope: {deg:F0} degrees (normal z >= {walkNormalZ:F3}; survivor's {survivorMaxDegrees:F0})");
        }
        const float SkinMetres = 0.001f;
        const float SnagUnits = 2f; // how high a ground seam may catch the box's bottom
        /// Sweeps that rode over a ground seam (pilot).
        public static int SeamSkips;

        // Box sweep through RoR2's physics scene, answered in IW4 terms.
        public static void Trace(IntPtr user, Vec3f* start, Vec3f* end, Vec3f* mins, Vec3f* maxs, uint mask, Mw2Trace* r)
        {
            var center = new Vec3f((mins->x + maxs->x) * 0.5f, (mins->y + maxs->y) * 0.5f, (mins->z + maxs->z) * 0.5f);
            var half = new Vec3f((maxs->x - mins->x) * 0.5f, (maxs->y - mins->y) * 0.5f, (maxs->z - mins->z) * 0.5f);
            // Axis-aligned in both spaces: IW (x,y,z) extents land on Unity (z,x,y).
            var halfU = new Vector3(half.y, half.z, half.x) * Space.Scale - new Vector3(SkinMetres, SkinMetres, SkinMetres);
            halfU = Vector3.Max(halfU, new Vector3(SkinMetres, SkinMetres, SkinMetres));

            var s = new Vec3f(start->x + center.x, start->y + center.y, start->z + center.z);
            var e = new Vec3f(end->x + center.x, end->y + center.y, end->z + center.z);
            Vector3 su = Space.ToUnity(s), eu = Space.ToUnity(e);

            r->fraction = 1f;
            r->endpos = *end;
            r->normal = default;
            r->startsolid = 0;
            r->allsolid = 0;
            r->walkable = 0;
            r->surfaceFlags = 0;

            if (Physics.CheckBox(su, halfU, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore))
            {
                r->fraction = 0f;
                r->endpos = *start;
                r->startsolid = 1;
                r->allsolid = (byte)(Physics.CheckBox(eu, halfU, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore) ? 1 : 0);
                r->normal = new Vec3f(0, 0, 1);
                return;
            }

            Vector3 delta = eu - su;
            float dist = delta.magnitude;
            if (dist < 1e-6f) return;

            var dir = delta / dist;
            float pull = ClipEpsilonUnits * Space.Scale;
            bool got = Physics.BoxCast(su, halfU, dir, out RaycastHit hit, Quaternion.identity, dist, Mask, QueryTriggerInteraction.Ignore);
            // Quake / IW clip per plane: a surface the box only grazes or leaves (moving along or
            // away from it) isn't hit at all. Unity reports a face the box rests against at any
            // sweep; with the epsilon pull below that read as "can't move" in every direction
            // that touched it, sliding along it included - wedged on RoR2 terrain (pilot 10-04-26,
            // Distant Roost). Sweep again a hair off that face.
            if (got && Vector3.Dot(dir, hit.normal) >= -1e-3f)
            {
                var off = su + hit.normal * pull;
                got = !Physics.CheckBox(off, halfU, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore)
                    && Physics.BoxCast(off, halfU, dir, out hit, Quaternion.identity, dist, Mask, QueryTriggerInteraction.Ignore)
                    && Vector3.Dot(dir, hit.normal) < -1e-3f;
            }
            // Unity's sweeps catch the seams between a ground mesh's triangles (internal edges) and
            // answer with a sideways normal: on flat RoR2 terrain MW2's box stopped dead, "blocked 0
            // units ahead" (playtest 10-06-26, six times in one session). A side hit at the box's bottom
            // with walkable ground right under it is that seam: swept again a hair higher. Kept only
            // when the box at its own height is clear where it ends up - a real lip overlaps it and
            // stays a step for MW2's step-up.
            if (got && hit.normal.y < walkNormalZ && Mathf.Abs(dir.y) < 0.5f)
            {
                float snag = SnagUnits * Space.Scale;
                float bottom = su.y + dir.y * hit.distance - halfU.y;
                if (hit.point.y < bottom + snag
                    && Physics.Raycast(new Vector3(hit.point.x, bottom + snag, hit.point.z), Vector3.down, out var under, 2f * snag, Mask, QueryTriggerInteraction.Ignore)
                    && under.normal.y >= walkNormalZ)
                {
                    var lifted = su + Vector3.up * snag;
                    if (!Physics.CheckBox(lifted, halfU, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore))
                    {
                        bool got2 = Physics.BoxCast(lifted, halfU, dir, out var hit2, Quaternion.identity, dist, Mask, QueryTriggerInteraction.Ignore)
                            && Vector3.Dot(dir, hit2.normal) < -1e-3f;
                        float reach = got2 ? hit2.distance : dist;
                        if (reach > hit.distance + 1e-4f
                            && !Physics.CheckBox(su + dir * Mathf.Max(reach - pull, 0f), halfU, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore))
                        {
                            SeamSkips++;
                            got = got2;
                            if (got2) hit = hit2;
                        }
                    }
                }
            }
            if (got)
            {
                // The epsilon gap is off the plane (perpendicular), not along the move:
                // (d1 - eps) / (d1 - d2) in CM_ClipBoxToBrush.
                float into = Mathf.Max(-Vector3.Dot(dir, hit.normal), 1e-3f);
                float f = Mathf.Clamp01((hit.distance - pull / into) / dist);
                r->fraction = f;
                r->endpos = new Vec3f(start->x + (end->x - start->x) * f, start->y + (end->y - start->y) * f, start->z + (end->z - start->z) * f);
                var n = Space.DirToIw(hit.normal);
                r->normal = n;
                r->walkable = (byte)(n.z >= walkNormalZ ? 1 : 0);
                // MW2 surface type in the IW4 surfaceFlags bits (>> 20 & 0x1f): footsteps,
                // landings and their sounds pick the material from it.
                r->surfaceFlags = (uint)(Mw2Gunfire.SurfaceIndex(hit.collider) & 0x1f) << 20;
            }
            // Projectiles (MW2's shot mask) also hit enemies: rockets flew through monsters and went
            // off on the ground behind them (playtest 10-04-26: launchers did nothing). A body struck
            // reads as flesh, which the explode event carries back for the direct-hit damage.
            if (mask == MissileMask) EnemySweep(su, halfU, dir, dist, start, end, r);
        }

        /// IW4L MASK_SHOT (equipment.rs MISSILE_MASK): only projectile sweeps pass it.
        const uint MissileMask = 0x0280_6831;

        static void EnemySweep(Vector3 su, Vector3 halfU, Vector3 dir, float dist, Vec3f* start, Vec3f* end, Mw2Trace* r)
        {
            var me = Plugin.Instance != null ? Plugin.Instance.Bridge.LocalBody : null;
            var team = me != null && me.teamComponent != null ? me.teamComponent.teamIndex : TeamIndex.Player;
            float best = float.MaxValue;
            RaycastHit bestHit = default;
            foreach (var h in Physics.BoxCastAll(su, halfU, dir, Quaternion.identity, dist, LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Collide))
            {
                var hb = h.collider != null ? h.collider.GetComponent<HurtBox>() : null;
                if (hb == null || hb.healthComponent == null || !hb.healthComponent.alive) continue;
                if (hb.teamIndex == team) continue;
                float d = h.distance > 0f ? h.distance : 0f;
                if (d < best) { best = d; bestHit = h; }
            }
            if (best == float.MaxValue) return;
            float f = Mathf.Clamp01(best / dist);
            if (f >= r->fraction) return;
            r->fraction = f;
            r->endpos = new Vec3f(start->x + (end->x - start->x) * f, start->y + (end->y - start->y) * f, start->z + (end->z - start->z) * f);
            r->normal = Space.DirToIw(bestHit.distance > 0f && bestHit.normal.sqrMagnitude > 0.5f ? bestHit.normal : -dir);
            r->walkable = 0;
            r->surfaceFlags = (uint)(Mw2Gunfire.FleshSurface & 0x1f) << 20;
        }
    }

    static unsafe class UnityWorldProbe
    {
        /// Pilot: what the sim's box sweeps see from the player's spot - 8 units each way and
        /// down - with the collider each one hits.
        public static void Log(Vec3f origin)
        {
            var mins = new Vec3f(-15f, -15f, 0f); var maxs = new Vec3f(15f, 15f, 70f);
            foreach (var (name, d) in new[] { ("+x", new Vec3f(8, 0, 0)), ("-x", new Vec3f(-8, 0, 0)), ("+y", new Vec3f(0, 8, 0)), ("-y", new Vec3f(0, -8, 0)), ("down", new Vec3f(0, 0, -2)), ("up", new Vec3f(0, 0, 8)) })
            {
                var start = origin; var end = new Vec3f(origin.x + d.x, origin.y + d.y, origin.z + d.z);
                Mw2Trace r;
                UnityWorld.Trace(IntPtr.Zero, &start, &end, &mins, &maxs, 0, &r);
                // The hit itself, for the collider's name.
                var su = Space.ToUnity(new Vec3f(origin.x, origin.y, origin.z + 35f));
                var half = new Vector3(15f, 35f, 15f) * Space.Scale - Vector3.one * 0.001f;
                var dir = Space.DirToUnity(d).normalized;
                string what = Physics.BoxCast(su, half, dir, out var hit, Quaternion.identity, d.z != 0 ? Mathf.Abs(d.z) * Space.Scale : 8f * Space.Scale, UnityWorld.Mask, QueryTriggerInteraction.Ignore) ? $"{hit.collider.name} ({hit.collider.GetType().Name}, convex {(hit.collider as MeshCollider)?.convex}) at {hit.distance / Space.Scale:F2}u point {hit.point}" : "nothing";
                var overl = Physics.OverlapBox(su, half, Quaternion.identity, UnityWorld.Mask, QueryTriggerInteraction.Ignore);
                Plugin.Log.LogInfo($"[probe] {name}: fraction {r.fraction:F2} startsolid {r.startsolid} allsolid {r.allsolid} normal ({r.normal.x:F2}, {r.normal.y:F2}, {r.normal.z:F2}) | cast hits {what} | overlaps {string.Join(", ", overl.Select(c => c.name))}");
            }
        }
    }

    /// Owns one MW2 sim bound to the local survivor while MW2 mode is on.
    unsafe class Mw2Bridge
    {
        const float Mw2WalkUnitsPerSec = 190f;
        const float Ror2SurvivorWalkMetresPerSec = 7f;

        IntPtr sim;
        CharacterBody body;
        CharacterMotor motor;
        Vector3 footToTransform;
        Vector3 target;
        bool haveTarget;
        float msAccumulator;
        Mw2State last;

        string[] loadout = new string[0];
        int loadoutSlot;
        string weaponName = "";
        bool reloadHeld;
        readonly Mw2Shot[] shotBuffer = new Mw2Shot[64];
        GameObject tracer, hitspark;
        readonly Mw2Audio audio = new Mw2Audio();
        /// The Stinger / AT4 lock-on (_stinger.gsc) on RoR2's monsters.
        readonly Mw2Lock lockOn = new Mw2Lock();
        public int LockStage => lockOn.Stage;
        public string LockWhy => lockOn.Why;
        readonly Mw2Gun gun = new Mw2Gun();
        readonly Mw2Viewmodel viewmodel = new Mw2Viewmodel();
        /// Pilot: the first-person gun.
        public Mw2Viewmodel ViewmodelForTest => viewmodel;
        // Akimbo: MW2 draws a second full viewmodel for the left gun.
        readonly Mw2Viewmodel viewmodelLeft = new Mw2Viewmodel { Hand = 1 };
        bool Akimbo => last.clipLeft >= 0;
        readonly Mw2Killstreaks streaks = new Mw2Killstreaks();
        public readonly Mw2Admin Admin = new Mw2Admin();
        readonly Mw2Progress progress = new Mw2Progress();
        public Mw2Progress Progress => progress;
        /// The MW2 rank badge beside the minimap (wired once).
        bool rankBadgeWired;
        public CharacterBody Body => body;
        public CharacterBody LocalBody => Plugin.CurrentBody();
        public Mw2State State => last;
        public Mw2ViewSway Sway => sway;
        public Mw2Killstreaks Streaks => streaks;
        public string[] Loadout => loadout;
        public void GiveWeapon(string name) => Give(name);
        /// The stance asked for: 0 stand, 1 crouch, 2 prone (the sim stands up only where it fits).
        public int Stance;
        bool sprintWasDown, jumpUsedForStance;
        /// MW2's eye height standing (IW4 DEFAULT_VIEWHEIGHT): crouch, prone and last stand lower the
        /// first-person camera by the difference.
        const float StandViewHeight = 60f;
        /// 0 stand, 1 crouch, 2 prone, 3 last stand: the third-person soldier's stance.
        public byte CharacterStance => (byte)(Mw2Deathstreaks.InLastStand ? 3 : (last.pmFlags & 0x1) != 0 ? 2 : (last.pmFlags & 0x2) != 0 ? 1 : 0);
        /// Pilot: a two-weapon class (riot shield + gun) without going through the menus.
        public void PilotLoadout(params string[] names) { loadout = names; loadoutSlot = 0; Give(names[0]); }
        public void AddXp(int xp) => progress.Add(xp);

        // ---- multiplayer (Mw2Net) ----
        public CharacterBody LocalBody2 => Active ? body : null;
        Stage attachedStage;
        public Mw2State Last => last;
        public Transform LocalCharacterRoot => character.Root;
        public string WeaponName => weaponName;
        /// Riot shield in hand (blocks the front), or in the class but not in hand (on his back).
        public bool ShieldHeld => Active && Armed && Mw2Shield.Is(last.weapon);
        public bool ShieldBack => Active && !ShieldHeld && System.Array.Exists(loadout, w => Mw2Shield.Is(Native.WeaponIndex(w)));
        public Mw2Killstreaks StreaksRef => streaks;
        uint netEvents;
        public uint TakeNetEvents() { uint e = netEvents; netEvents = 0; return e; }
        // F6 intent: MW2 mode follows the player onto each new body (stages, revives).
        bool wantOn;

        readonly Mw2View view = new Mw2View();
        // Third person until F7 (playtest 10-04-26); the pilot keeps first person, which its steps expect.
        bool? wantFp;
        bool thirdOnlyWas;
        bool wantFirstPerson
        {
            get
            {
                if (ThirdOnly) return false;
                if (wantFp == null) wantFp = Mw2Pilot.Active || !Plugin.Instance.ThirdPersonStart.Value;
                return wantFp.Value;
            }
            set => wantFp = value;
        }
        public bool FirstPerson => wantFirstPerson;
        /// ThirdPersonOnly (the pilot's steps still see in first person).
        internal static bool ThirdOnly => Mw2Pilot.ThirdOnlyTest || (Plugin.Instance.ThirdPersonOnly.Value && (!Mw2Pilot.Active || Mw2Pilot.ThirdOnlyConfigTest));

        public void LateFrame() { }

        public void ToggleFirstPerson()
        {
            if (ThirdOnly) { if (view.FirstPerson) view.Exit(); return; }
            wantFirstPerson = !wantFirstPerson;
            if (!Active || body == null) return;
            if (wantFirstPerson) view.Enter(body, 60f * Space.Scale); else view.Exit();
            Plugin.Log.LogInfo($"MW2 view: {(view.FirstPerson ? "first person" : "third person")}");
        }

        /// Called after RoR2 places its camera each frame.
        Camera lastCam;
        int fxFrame = -1;
        Camera restingFor;
        Vector3 resting;
        Quaternion restingRot;
        bool restoreTilt;

        /// Put the Scene Camera back on its resting spot under the rig, before anything moves it this
        /// frame (the crouch eye, the death camera, the pilot's front view).
        public void RestCamera(Camera cam)
        {
            if (cam == null) return;
            if (cam != restingFor) { restingFor = cam; resting = cam.transform.localPosition; restingRot = cam.transform.localRotation; }
            cam.transform.localPosition = resting;
            if (restoreTilt)
            {
                restoreTilt = false;
                cam.transform.localRotation = restingRot;
                view.ForgetKick();
            }
        }

        /// The Scene Camera's turn back to rest on the next frame: RoR2 only turns the rig, so
        /// a rotation written to the camera stays. The death camera looks down at the body; after
        /// a Tactical Insertion respawn the view stayed tipped that way (playtest 10-04-26).
        public void RestoreCameraTilt() => restoreTilt = true;

        /// MW2 effects and gunfire (everyone's), once a frame, from this camera.
        public void RenderFx(Camera cam)
        {
            if (fxFrame == Time.frameCount || cam == null) return;
            fxFrame = Time.frameCount;
            var fxCam = Mw2Fx.View != null ? Mw2Fx.View : cam;
            Mw2Fx.Render(fxCam, Time.deltaTime);
            Mw2Gunfire.Render(fxCam, Time.deltaTime);
        }
#if MW2_DEV
        public void DumpViewmodel(string dir) => viewmodel.Dump(view.Overlay, dir);
#endif

        bool hudBound;
        int fragsLeft, smokesLeft;
        uint iconsLethal, iconsTactical;

        /// MW2's own HUD menus, fed this frame's MW2 state.
        void DrawMenuHud()
        {
            if (!hudBound)
            {
                hudBound = true;
                Bind("+holdbreath", Mw2Binds.For("+holdbreath", "+breath_sprint") is KeyCode hb ? Mw2Binds.Label(hb) : "SHIFT");
                Bind("+actionslot 4", Mw2Binds.Label(Plugin.Instance.StreakKey.Value));
                progress.MenuBar = true;
            }
            if (Native.mw2_offhand_ammo(sim, out fragsLeft, out smokesLeft) != 1) fragsLeft = smokesLeft = 0;
            // Re-sent when the class's lethal / tactical change (playtest 10-04-26: the grenade icons
            // stayed on the first class's after switching).
            if (lethalWeapon != 0 && (lethalWeapon != iconsLethal || tacticalWeapon != iconsTactical))
            {
                iconsLethal = lethalWeapon; iconsTactical = tacticalWeapon;
                var f = System.Text.Encoding.UTF8.GetBytes(Native.WeaponString(lethalWeapon, 0));
                var t = System.Text.Encoding.UTF8.GetBytes(Native.WeaponString(tacticalWeapon, 0));
                unsafe { fixed (byte* pf = f) fixed (byte* pt = t) Native.mw2_hud_set_offhand(pf, (UIntPtr)f.Length, pt, (UIntPtr)t.Length); }
            }
            var fwd = lastCam != null ? lastCam.transform.forward : Vector3.forward;
            var iw = Space.DirToIw(fwd);
            var st = new Mw2HudState
            {
                screenW = Screen.width, screenH = Screen.height,
                timeMs = (int)(Time.unscaledTime * 1000f),
                weapon = last.weapon, altWeapon = Akimbo ? last.weapon : 0,
                clip = last.clip, clipSize = MagSize(), stock = last.stock,
                altClip = Akimbo ? last.clipLeft : 0, altClipSize = Akimbo ? MagSize() : 0,
                frags = fragsLeft, smokes = smokesLeft,
                yawDeg = Mathf.Atan2(iw.y, iw.x) * Mathf.Rad2Deg,
                adsFrac = last.adsFrac,
                weaponstate = last.weaponstate,
                xpFrac = progress.XpFrac, rank = progress.RankId,
                showBreathHint = (byte)(CanHoldBreath && !HoldingBreath && sway.breathMs == 0 && last.adsFrac > 0.99f ? 1 : 0),
                lowAmmoOk = (byte)(weaponName == OmaBag ? 0 : 1), // the One Man Army bag has no ammo to warn about
            };
            Mw2MenuHud.Draw(in st);
        }

        static unsafe void Bind(string command, string label)
        {
            var c = System.Text.Encoding.UTF8.GetBytes(command);
            var l = System.Text.Encoding.UTF8.GetBytes(label);
            fixed (byte* pc = c) fixed (byte* pl = l) Native.mw2_hud_set_binding(pc, (UIntPtr)c.Length, pl, (UIntPtr)l.Length);
        }

        // Killstreak call-in: hold the gun's ammo while the streak's weapon is out.
        string savedWeapon;
        int savedClip, savedStock;

        /// A RoR2 launch pad (JumpVolume) threw this player: MW2's movement owns the motor, so the
        /// launch goes into the sim (metres/s -> MW2 units/s); RoR2 setting the motor's velocity alone
        /// was overwritten the next tick (playtest: jump pads did nothing).
        public bool Launch(CharacterMotor m, Vector3 velocity)
        {
            if (!Owns(m) || sim == IntPtr.Zero) return false;
            // Applied right before each sim step until the sim has left the ground (a once-only pad
            // trigger set between steps, or one tick of it, was eaten by MW2's ground handling).
            pendingLaunch = Space.DirToIw(velocity / Mathf.Max(Space.Scale, 1e-4f)); // axes only: scale first
            launchTicks = 10;
            return true;
        }

        Vec3f pendingLaunch;
        int launchTicks;
        bool airborneSinceLaunch;

        // The care package / sentry smoke marker: MW2 throws it like a grenade wherever the player
        // aims (_airdrop.gsc waits on grenade_fire). While it is out it rides in the tactical slot
        // and fire works that slot (hold pulls the pin, release throws); the class's tactical comes
        // back once it has gone.
        bool markerSlot;
        int markerSavedTacticals;

        void MarkerThrow(ref uint buttons)
        {
            if (sim == IntPtr.Zero) return;
            string marker = streaks.MarkerInHand;
            Native.mw2_offhand_ammo(sim, out int frags, out int smokes);
            if (marker != null)
            {
                if (!markerSlot)
                {
                    markerSlot = true;
                    markerSavedTacticals = smokes;
                    Native.mw2_set_offhand(sim, lethalWeapon, frags, Native.WeaponIndex(marker), 1);
                    return;
                }
                if (smokes == 0) { streaks.MarkerThrown(); return; }
                if (streaks.PlayerAttack) buttons |= Buttons.Smoke;
            }
            else if (markerSlot && !streaks.CallingIn)
            {
                markerSlot = false;
                Native.mw2_set_offhand(sim, lethalWeapon, frags, tacticalWeapon, markerSavedTacticals);
            }
        }

        // ---------------------------------------------------------------- Tactical Insertion
        // MW2's flare (flare_mp, an offhand) thrown from the tactical slot like a streak marker: its
        // ignite and toss animations play, and where it comes to rest is the spawn point
        // (Mw2Projectiles -> Mw2TacticalInsertion.Landed).
        bool tiThrowing;
        float tiStarted;
        int tiSavedTacticals, tiSavedLethals;
        public uint TiFlare => tiFlare != 0 ? tiFlare : (tiFlare = Native.WeaponIndex("flare_mp"));
        uint tiFlare;

        public bool ThrowTacticalInsertion()
        {
            // Frozen (prematch) or in a menu: no throw - it's planted at his feet instead.
            if (!Active || sim == IntPtr.Zero || tiThrowing || markerSlot || TiFlare == 0 || HeldStill) return false;
            Native.mw2_offhand_ammo(sim, out int frags, out int smokes);
            tiSavedTacticals = smokes;
            tiSavedLethals = frags;
            // The lethal slot is empty for the toss: IW4 throws the first offhand of the button's
            // class, and flare_mp shares semtex / throwing knife's (5) - with Semtex as the lethal
            // the tactical button threw the Semtex (playtest 10-04-26).
            Native.mw2_set_offhand(sim, 0, 0, TiFlare, 1);
            tiThrowing = true;
            tiStarted = Time.time;
            return true;
        }

        void TacticalInsertionThrow(ref uint buttons)
        {
            if (!tiThrowing || sim == IntPtr.Zero) return;
            float t = Time.time - tiStarted;
            Native.mw2_offhand_ammo(sim, out _, out int smokes);
            // Hold the tactical button a moment (ignite), then let go (toss).
            if (smokes > 0 && t < 0.45f) buttons |= Buttons.Smoke;
            if ((smokes == 0 && t > 0.5f) || t > 3f)
            {
                tiThrowing = false;
                Native.mw2_set_offhand(sim, lethalWeapon, tiSavedLethals, tacticalWeapon, tiSavedTacticals);
            }
        }

        /// What the third-person soldier animates with instead of the gun (Mw2Character.AnimWeapon):
        /// the offhand from pin pull to throw (MW2's grenade / knife / hold sets; the Tactical
        /// Insertion flare is a grenade), the killstreak laptop for the whole ride (_killstreaks.gsc
        /// keeps the streak weapon in hand; the gun is only back once it's over).
        public uint AnimWeapon
        {
            get
            {
                if (sim == IntPtr.Zero || !Armed) return 0;
                if (streaks.FreezesPlayer && rideLaptop != 0) return rideLaptop;
                return Native.mw2_offhand_viewmodel_weapon(sim);
            }
        }
        uint rideLaptop;
        bool callInClicked;

        void CallInWeapon(string name)
        {
            if (!Active) return;
            rideLaptop = Native.WeaponIndex(name);
            savedWeapon = weaponName;
            if (Native.mw2_weapon_ammo(sim, out savedClip, out savedStock) != 1) savedWeapon = null;
            Give(name);
            Native.mw2_set_ammo(sim, 1, 0); // a round in the device so its click (fire anim) plays
        }

        void RestoreWeapon()
        {
            if (!Active || savedWeapon == null) return;
            Give(savedWeapon);
            Native.mw2_set_ammo(sim, savedClip, savedStock);
            savedWeapon = null;
        }

#if MW2_DEV
        public string GunDebug() => $"view firstPerson={view.FirstPerson} armed={Armed} | " + gun.Debug(lastCam);
#endif

        public void OnCamera(Camera cam)
        {
            lastCam = cam;
            CameraFov = cam.fieldOfView;
            UpdateSway(Time.deltaTime);
            // The pilot turns the first-person camera only: in third person RoR2 places the camera from
            // its own pitch, and rotating it afterwards aimed it at the sky from down by the feet.
            // Crouched / prone / last stand: MW2's lower eye (RoR2's camera sits at a standing eye).
            // RoR2 places the camera rig, never the Scene Camera under it: an offset written here
            // stays, so it is set from the camera's own resting spot every frame (playtest 10-04-26:
            // added each frame, crouching sank him through the map).
            if (view.FirstPerson && Active && last.viewHeight > 0f)
                cam.transform.position += Vector3.down * Mathf.Max(StandViewHeight - last.viewHeight, 0f) * Space.Scale;
            if (Mw2Pilot.Active && view.FirstPerson) Mw2Pilot.Steer(cam.transform);
            // Third person only: the over-the-shoulder view zooms like MW2's sights would.
            // The showcase holds its frame still: no recoil or sway on the camera (applied as per-frame
            // deltas on a camera the pilot re-aims every frame, a swap's reset kick swung the view ~9
            // degrees for two frames - the Gold Deagle, 10-06-26). The gun still kicks.
            var camKick = Mw2Pilot.Active && Mw2Cinema.Recording ? Vector3.zero : Kick + new Vector3(sway.pitch, sway.yaw, 0f);
            view.OnCamera(cam, camKick, Armed && (view.FirstPerson || ThirdOnly) ? ZoomK() : 1f);
            if (Mw2Pilot.Active && view.FirstPerson) Mw2Pilot.AfterCamera(cam.transform);
            // Pilot screenshots of the third-person soldier: a camera in front of him, 3/4 view.
            if (Mw2Pilot.FrontCam && !view.FirstPerson && character.Root != null)
            {
                var r = character.Root;
                var target = r.position + Vector3.up * 1.1f * r.localScale.x;
                cam.transform.position = target + (r.forward * 2.6f + r.right * 1.2f + Vector3.up * 0.3f) * r.localScale.x;
                cam.transform.LookAt(target);
            }
            bool riding = streaks.OnCamera(cam);
            RenderFx(cam);
            bool firstPerson = view.FirstPerson && Armed && view.OverlayTransform != null && !riding && !streaks.BlocksWeapon;
            // Scoped weapons: at full ADS MW2 hides the gun and shows the scope overlay.
            // The Javelin has no scope image but its CLU sight (javelin_overlay_hd) is a scope all the
            // same: MW2 takes the launcher off the screen there. Left drawn, its dark eyepiece filled
            // the CLU's window - you couldn't see the target through it (playtest 10-06-26).
            bool clu = weaponName != null && weaponName.StartsWith("javelin", StringComparison.OrdinalIgnoreCase);
            Scoped = (firstPerson || (ThirdOnly && Armed && !riding && !streaks.BlocksWeapon)) && (weaponView.hasOverlay != 0 || clu) && last.adsFrac >= 0.999f;
            ScopeUp = Scoped;
            // Thermal sight (the _thermal attachment, scope_overlay_m14_night): MW2 turns on thermal
            // vision while the overlay is up - white-hot enemies on a dark grey world.
            bool thermal = Scoped && weaponName != null && weaponName.IndexOf("_thermal", StringComparison.OrdinalIgnoreCase) >= 0;
            if (thermal != scopeThermal)
            {
                scopeThermal = thermal;
                if (thermal) Mw2Thermal.Begin(); else Mw2Thermal.End();
            }
            if (firstPerson)
            {
                // The full MW2 viewmodel (arms + gun, MW2 animations) lives in the overlay.
                uint offhandVm = sim != IntPtr.Zero ? Native.mw2_offhand_viewmodel_weapon(sim) : 0;
                var vmWas = viewmodel.Handle;
                viewmodel.Build(body, offhandVm != 0 ? offhandVm : last.weapon, view.OverlayTransform, Mw2View.Layer, offhandVm != 0 ? 0u : altFrom);
                // The showcase's swap glide: the new gun starts from the old one's pose (parked, still alive).
                bool rebuilt = vmWas != IntPtr.Zero && viewmodel.Handle != IntPtr.Zero && viewmodel.Handle != vmWas;
                bool expected = rebuilt && Mw2Viewmodel.SwapsExpected > 0;
                if (rebuilt && Mw2Viewmodel.SwapBlend > 0f && !expected)
                    Plugin.Log.LogInfo($"[cinema] unplanned viewmodel rebuild: weapon {Native.WeaponString(last.weapon, 2)}, offhand {offhandVm} (no dissolve)");
                if (expected) Mw2Viewmodel.SwapsExpected--;
                if (Mw2Viewmodel.SwapBlend > 0f && expected)
                {
                    // Mid-reload: the new gun picks its reload up where its pose matches the old gun's.
                    if (Mw2Viewmodel.PilotSlot >= 0)
                    {
                        float f = Native.mw2_viewmodel_match_phase(viewmodel.Handle, vmWas, (uint)Mw2Viewmodel.PilotSlot, Mw2Viewmodel.PilotFrac);
                        if (f >= 0f) Mw2Viewmodel.PilotFrac = f;
                    }
                    Native.mw2_viewmodel_blend_from(viewmodel.Handle, vmWas, Mw2Viewmodel.SwapBlend);
                    // (Aimed swaps too: their odd frame was the camera's recoil reset - off the camera now.)
                    Mw2Cinema.SwapDissolve(viewmodel.PreviousRoot, viewmodel.RootObject);
                }
                viewmodel.Step(sim, Time.deltaTime, !Scoped);
                if (weaponName != null && weaponName.IndexOf("_heartbeat", StringComparison.OrdinalIgnoreCase) >= 0) Mw2Heartbeat.Update(body, cam.transform);
                foreach (var alias in viewmodel.TakeSounds()) { if (alias == Mw2Viewmodel.SkipAlias) continue; audio.PlayAlias(alias, 1f); Plugin.Log.LogInfo($"[vmsound] {alias}"); }
                if (Akimbo && offhandVm == 0)
                {
                    var leftWas = viewmodelLeft.Handle;
                    viewmodelLeft.Build(body, last.weapon, view.OverlayTransform, Mw2View.Layer);
                    // A left gun glides from the old left gun, or from the single gun it joins.
                    var leftFrom = leftWas != IntPtr.Zero ? leftWas : vmWas;
                    if (Mw2Viewmodel.SwapBlend > 0f && leftFrom != IntPtr.Zero && viewmodelLeft.Handle != IntPtr.Zero && viewmodelLeft.Handle != leftWas)
                    {
                        Native.mw2_viewmodel_blend_from(viewmodelLeft.Handle, leftFrom, Mw2Viewmodel.SwapBlend);
                        if (leftWas != IntPtr.Zero) Mw2Cinema.SwapDissolve(viewmodelLeft.PreviousRoot, viewmodelLeft.RootObject);
                    }
                    viewmodelLeft.Step(sim, Time.deltaTime, true);
                    foreach (var alias in viewmodelLeft.TakeSounds()) audio.PlayAlias(alias, 1f);
                }
                else viewmodelLeft.Step(sim, 0f, false);
            }
            else { viewmodel.Step(sim, 0f, false); viewmodelLeft.Step(sim, 0f, false); }
            // MW2's third-person soldier (#8) stands in for the survivor's model; the old standalone gun
            // is only the fallback when the MW2 body can't be built.
            if (Plugin.Instance.Mw2Body.Value && !character.Exists && !characterTried && body != null)
            {
                characterTried = true;
                SkinKey = Mw2Skins.KeyFor(body);
                if (character.Build(body, SkinKey))
                {
                    // First-person arms to match the body: rebuild the viewmodel with them.
                    bool own = character.UseViewhands();
                    Plugin.Log.LogInfo($"MW2 viewhands: {(own ? "the character's own" : "base")} ({SkinKey})");
                    viewmodel.Destroy();
                    viewmodelLeft.Destroy();
                    var model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
                    characterHid = model != null ? model.GetComponent<CharacterModel>() : null;
                    if (characterHid != null) characterHid.invisibilityCount++;
                    Mw2ItemDisplays.Bind(characterHid, character);
                }
            }
            if (character.Exists)
            {
                // Third person only: his own back filled the scope picture - hidden while scoped.
                StepCharacter((!view.FirstPerson || riding) && !(ThirdOnly && Scoped && !riding));
                gun.SetVisible(false);
                return;
            }
            if (Armed && !(firstPerson && viewmodel.Exists))
            {
                try { gun.Build(body, last.weapon, Space.Scale); } catch (Exception e) { Plugin.Log.LogWarning($"MW2 gun model failed: {e}"); }
            }
            if (Armed && !(firstPerson && viewmodel.Exists))
                gun.PoseView(cam.transform, false, body != null && body.inputBank != null ? body.inputBank.aimDirection : cam.transform.forward, last.adsFrac, Space.Scale);
            gun.SetVisible(!(firstPerson && viewmodel.Exists));
        }

        Mw2WeaponView weaponView;

        Mw2Character character = new Mw2Character();
        bool characterTried;
        /// This life's character (Mw2Skins key), sent to teammates.
        public string SkinKey;
        CharacterModel characterHid;
        uint characterEvents; // sim weapon events since the last character step
        uint lastHit;         // Mw2Character.Hit of the last damage taken (pain / death anims)
        float nextPain;

        void StepCharacter(bool visible)
        {
            var dir = Space.DirToUnity(ViewForward(new Vec3f(0f, last.viewangles.y, 0f)));
            float yawUnity = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float yr = last.viewangles.y * Mathf.Deg2Rad;
            float vx = last.velocity.x, vy = last.velocity.y;
            var input = new Mw2CharacterInput
            {
                dt = Time.deltaTime,
                stance = CharacterStance,
                sprinting = last.sprinting,
                inAir = (byte)(last.grounded == 0 ? 1 : 0),
                dead = (byte)(body != null && body.healthComponent != null && !body.healthComponent.alive ? 1 : 0),
                moveFwd = vx * Mathf.Cos(yr) + vy * Mathf.Sin(yr),
                moveRight = vx * Mathf.Sin(yr) - vy * Mathf.Cos(yr), // IW4 +y is left
                aimPitch = last.viewangles.x,
                adsFrac = last.adsFrac,
                weapon = Armed ? last.weapon : 0u,
                events = characterEvents,
                hit = lastHit,
            };
            characterEvents = 0;
            character.StowedShield = ShieldBack ? Mw2Shield.Index : 0u;
            character.AnimWeapon = AnimWeapon;
            character.Step(body.footPosition, yawUnity, ref input, visible);
        }

        // Things that would hitch on first use (loadout viewmodels, HUD art, killstreak
        // models), built one at a time after MW2 mode starts instead of all at once.
        readonly Queue<Action> warm = new Queue<Action>();
        float nextWarm;

        void QueueWarmup(CharacterBody b)
        {
            warm.Clear();
            // Textures load on a native background thread right away (the slow part: iwd reads);
            // the queue below then only uploads/builds, a few ms each, once they're in.
            foreach (var w in loadout) Native.mw2_prefetch_weapon(Native.WeaponIndex(w));
            var names = new List<string>();
            foreach (var img in new[] { "ac130_overlay_grain", "minimap_background", "minimap_scanlines", "compass_radarline", "compassping_player",
                         "compassping_enemy", "damage_feedback", "hit_direction", "blood_splatter", "720_xpbar_empty", "720_xpbar_solid",
                         "ammo_counter_riflebullet_mp", "ammo_counter_bullet_mp", "ammo_counter_shotgunshell_mp", "ammo_counter_beltbullet_mp" })
            {
                names.Add(img);
                warm.Enqueue(() => Mw2Icons.Get(img));
            }
            var st = streaks.State();
            for (int i = 0; i < st.loadoutLen; i++)
            {
                uint id = st.loadout[i];
                names.Add(Native.StreakString(id, 5));
                names.Add(Native.StreakString(id, 7));
                warm.Enqueue(() => { Mw2Icons.Get(Native.StreakString(id, 5)); Mw2Icons.Get(Native.StreakString(id, 7)); });
            }
            var props = new[] { "com_plasticcase_friendly", "sentry_minigun", "sentry_minigun_obj", "vehicle_little_bird_armed", "vehicle_cobra_helicopter_fly_low",
                                "vehicle_pavelow", "vehicle_apache_mp", "vehicle_ac130_low_mp", "vehicle_av8b_harrier_jet_mp", "vehicle_mig29_desert", "vehicle_b2_bomber" };
            names.AddRange(props);
            // Thrown and launched things: their projectile models and (offhand / marker) viewmodels
            // were built on the first throw, a frame long enough that the throw was never seen
            // leaving the hand (playtest: big lag on the first knife).
            var cls = ClassLoadout();
            var thrown = new List<string>();
            if (cls != null && cls.Length > 3) { thrown.Add(cls[2]); thrown.Add(cls[3]); }
            else { thrown.Add(Plugin.Instance.Lethal.Value); thrown.Add(Plugin.Instance.Tactical.Value); }
            for (int i = 0; i < st.loadoutLen; i++) thrown.Add(Native.StreakString(st.loadout[i], 4)); // markers / laptops
            thrown.Add("remotemissile_projectile_mp");
            var projectiles = new List<string>();
            foreach (var w in thrown.Concat(loadout).Distinct())
            {
                uint wi = Native.WeaponIndex(w);
                if (wi == 0) continue;
                Mw2Projectiles.ProjectileOf(wi, out var pm, out _);
                if (!string.IsNullOrEmpty(pm) && !projectiles.Contains(pm)) projectiles.Add(pm);
            }
            names.AddRange(projectiles);
            Native.Prefetch(names);
            foreach (var m in projectiles) warm.Enqueue(() => Mw2Prop.Warm(m, body));
            foreach (var w in thrown.Distinct())
            {
                uint wi = Native.WeaponIndex(w);
                if (wi != 0) warm.Enqueue(() => { if (view.OverlayTransform != null) viewmodel.Prewarm(body, wi, view.OverlayTransform, Mw2View.Layer); });
            }
            foreach (var m in props) warm.Enqueue(() => Mw2Prop.Warm(m, body));
            foreach (var w in loadout)
            {
                uint idx = Native.WeaponIndex(w);
                warm.Enqueue(() => { if (view.OverlayTransform != null) viewmodel.Prewarm(body, idx, view.OverlayTransform, Mw2View.Layer); });
                if (w.Contains("_akimbo")) warm.Enqueue(() => { if (view.OverlayTransform != null) viewmodelLeft.Prewarm(body, idx, view.OverlayTransform, Mw2View.Layer); });
            }
            nextWarm = Time.unscaledTime + 4f; // give the texture thread a head start
        }
        public bool Scoped;
        /// The local player's scope overlay is up (the HUD leaves the crosshair off it).
        internal static bool ScopeUp;
        bool scopeThermal;
        Mw2ViewSway sway;
        bool wasHolding;
        float nextHeartbeat;

        /// MW2's scope sway + hold breath (sim), and its breath sounds (IW4L audio/breath.rs):
        /// breathin on hold, breathout on release, breathgasp when it ran out, heartbeat each
        /// second from 1 s in.
        void UpdateSway(float dt)
        {
            if (!Armed || Native.mw2_view_sway(sim, dt, out sway) != 1) { sway = default; wasHolding = false; return; }
            bool holding = sway.holding != 0;
            if (holding && !wasHolding) { audio.PlayAlias("weap_sniper_breathin"); nextHeartbeat = Time.time + 1f; }
            if (!holding && wasHolding) audio.PlayAlias(sway.breathMs > 4500 ? "weap_sniper_breathgasp" : "weap_sniper_breathout");
            if (holding && Time.time >= nextHeartbeat) { audio.PlayAlias("weap_sniper_heartbeat"); nextHeartbeat = Time.time + 1f; }
            wasHolding = holding;
        }

        public bool CanHoldBreath => sway.canHold != 0;
        public bool HoldingBreath => sway.holding != 0;

        /// The view zoom for the current ADS fraction: MW2 starts zooming at ads_zoom_in_frac
        /// and reaches ads_zoom_fov when fully aimed (zooming out reuses the same curve).
        float ZoomK()
        {
            if (weaponView.adsZoomFov <= 0f || last.adsFrac <= 0f) return 1f;
            float start = Mathf.Clamp(weaponView.adsZoomInFrac, 0f, 0.99f);
            float t = Mathf.Clamp01((last.adsFrac - start) / (1f - start));
            return Mw2View.ZoomRatio(Mathf.Lerp(Mw2View.CgFov, weaponView.adsZoomFov, t)); // cg_fov -> ads_zoom_fov
        }

        public bool Active => sim != IntPtr.Zero;

        /// MW2 mode should hold the player's body (F6 toggles; AutoStart turns it on from launch).
        public bool WantOn { get => wantOn; set => wantOn = value; }
        /// F6 turned MW2 mode on for a survivor that isn't the MW2 Soldier.
        public bool Manual;

        // ---------------------------------------------------------------- One Man Army
        /// The One Man Army skill opened Choose Class: the pick changes the class after MW2's delay
        /// (_perkfunctions.gsc omaUseBar: 6 s, 3 s with One Man Army Pro / specialty_omaquickchange).
        public bool OmaPending;
        bool omaChanging;
        float omaFrom, omaUntil;
        string omaLabel; // perkTable's localized name (One Man Army)

        public void OneManArmy()
        {
            if (!Active) return;
            // MW2's One Man Army bag comes out (onemanarmy_mp: pull-out, held through the change).
            if (weaponName != OmaBag) { omaReturnTo = weaponName; Give(OmaBag); }
            OmaPending = true;
            Mw2Menus.SetFaction(Mw2Skins.LocalFaction());
            Mw2Menus.Open("changeclass");
        }

        const string OmaBag = "onemanarmy_mp";
        string omaReturnTo;

        /// Choose Class closed without a pick: the bag goes away, the gun comes back.
        public void CancelOma()
        {
            OmaPending = false;
            if (weaponName == OmaBag && !string.IsNullOrEmpty(omaReturnTo)) Give(omaReturnTo);
        }

        // ---------------------------------------------------------------- Changing class (F4)
        /// MW2's rule: Choose Class swaps the class on the spot only right after spawning, before
        /// any combat (MW2's grace period, 15 s); after that the pick waits for the next spawn
        /// (RoR2: the next stage, a revive, a Tactical Insertion). One Man Army is the mid-fight
        /// change. F6 off / on isn't a new life: the class waits.
        public const float ClassGraceSeconds = 15f;
        public int PendingClass; // 0: none; else the Class value the next spawn takes
        CharacterBody lifeBody;
        float lifeStart;
        bool combatThisLife;

        public bool InClassGrace => Active && !combatThisLife && Time.time - lifeStart < ClassGraceSeconds;

        /// A new body (stage, revive): a new life - the class picked meanwhile comes in.
        void BeginLife(CharacterBody b)
        {
            if (b == lifeBody) return;
            lifeBody = b;
            lifeStart = Time.time;
            combatThisLife = false;
            if (PendingClass != 0)
            {
                Plugin.Instance.Class.Value = PendingClass;
                Plugin.Log.LogInfo($"MW2 class: {PendingClass} from this spawn (picked last life)");
                PendingClass = 0;
            }
        }

        string notice;
        float noticeUntil;

        /// A line in the middle of the screen, as MW2's iPrintLnBold.
        public void Notice(string text, float seconds = 3f)
        {
            notice = text;
            noticeUntil = Time.time + seconds;
        }

        void DrawNotice()
        {
            if (notice == null || Time.time > noticeUntil || Event.current == null || Event.current.type != EventType.Repaint) return;
            Mw2Font.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 30), notice, Mw2Hud.S(22f), Color.white, TextAnchor.MiddleCenter);
        }

        public void StartOmaChange()
        {
            OmaPending = false;
            omaChanging = true;
            omaFrom = Time.time;
            omaUntil = Time.time + (Mw2Perks.Has(body, "specialty_omaquickchange") ? 3f : 6f);
        }

        void UpdateOma()
        {
            if (!omaChanging || Time.time < omaUntil) return;
            omaChanging = false;
            ApplyClass();
        }

        void DrawOma()
        {
            if (!omaChanging || Event.current == null || Event.current.type != EventType.Repaint) return;
            float k = Mathf.InverseLerp(omaFrom, omaUntil, Time.time), w = Screen.width, h = Screen.height;
            var o = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(w / 2 - 120, h * 0.6f, 240, 8), Texture2D.whiteTexture);
            GUI.color = Color.white; GUI.DrawTexture(new Rect(w / 2 - 120, h * 0.6f, 240 * k, 8), Texture2D.whiteTexture);
            GUI.color = o;
            Mw2Font.Label(new Rect(0, h * 0.6f - 30, w, 24), (omaLabel ?? (omaLabel = Mw2Menus.Localize(Mw2Menus.TableLookup("mp/perkTable.csv", 1, "specialty_onemanarmy", 2)))), Mw2Hud.S(24f), Color.white, TextAnchor.MiddleCenter, Mw2Font.Small);
        }

        /// Tactical Insertion: back to the flare (RoR2 moves the motor; MotorVelocity re-seats the sim).
        public void TeleportTo(Vector3 at)
        {
            if (body != null) TeleportHelper.TeleportBody(body, at);
        }

        /// Prematch countdown or an MW2 menu: stand still, no shooting (looking around is fine).
        static bool HeldStill => Mw2Prematch.Frozen || Mw2Menus.IsOpen;

        string beforeLastStand;

        /// _damage.gsc last stand: the secondary if it is a pistol, else a Beretta (M9).
        public void EnterLastStand()
        {
            if (!Active) return;
            beforeLastStand = Native.WeaponString(last.weapon, 2);
            var cls = ClassLoadout();
            string secondary = cls != null ? cls[1] : "";
            string baseName = secondary.Split('_')[0];
            bool pistol = Mw2Menus.TableLookup("mp/statsTable.csv", 4, baseName, 2) == "weapon_pistol";
            Give(pistol ? secondary : "beretta_mp");
        }

        /// lastStandRespawnPlayer: up again with the gun from before.
        public void LeaveLastStand()
        {
            if (!Active) return;
            if (!string.IsNullOrEmpty(beforeLastStand) && Native.WeaponIndex(beforeLastStand) != 0) Give(beforeLastStand);
            beforeLastStand = null;
            RefreshPerks();
        }

        /// Hand the current class's perk items out again (a deathstreak ended).
        public void RefreshPerks()
        {
            var cls = ClassLoadout();
            if (Active && cls != null && cls.Length >= 11) Mw2Perks.Apply(body, new[] { cls[8], cls[9], cls[10] });
        }

        float sprintInches;

        /// _missions.gsc: each sprint's distance (inches) / 12 feeds Marathon Pro and Lightweight Pro.
        void SprintDistance(Vec3f from, bool wasSprinting)
        {
            if (last.sprinting != 0 && wasSprinting)
            {
                float dx = last.origin.x - from.x, dy = last.origin.y - from.y;
                sprintInches += Mathf.Sqrt(dx * dx + dy * dy);
            }
            else if (wasSprinting && last.sprinting == 0 && sprintInches > 0f)
            {
                long feet = (long)(sprintInches / 12f);
                sprintInches = 0f;
                if (Mw2Perks.Has(body, "specialty_marathon")) Mw2Challenges.Report(Mw2Challenges.Progress("ch_marathon_pro", feet), progress, streaks);
                if (Mw2Perks.Has(body, "specialty_lightweight")) Mw2Challenges.Report(Mw2Challenges.Progress("ch_lightweight_pro", feet), progress, streaks);
            }
        }

        /// The class picked in MW2's class menu, given now (MW2 does it on respawn).
        public void ApplyClass()
        {
            if (!Active || !Plugin.Instance.weaponsOk) return;
            var cls = ClassLoadout();
            if (cls == null) return;
            loadout = new[] { cls[0], cls[1] }.Where(w => w.Length > 0 && Native.WeaponIndex(w) != 0).ToArray();
            if (loadout.Length == 0) return;
            Plugin.Log.LogInfo($"MW2 class {Plugin.Instance.PlayClass}: {string.Join(" / ", cls)}");
            stowedAmmo.Clear();
            loadoutSlot = 0;
            Give(loadout[0]);
            GiveEquipment();
        }
        public bool IsLocalBody(CharacterBody b) => Active && b == body;
        public Vector3 Kick => (Armed ? new Vector3(last.kickAngles.x, last.kickAngles.y, last.kickAngles.z) : Vector3.zero) + DamageKick();

        // MW2's flinch when shot: the view knocked away from the hit, 100 ms in and 400 ms back
        // (Q3 / IW's DAMAGE_DEFLECT_TIME / DAMAGE_RETURN_TIME). The kick is damage x bg_viewKickScale
        // clamped to bg_viewKickMin..Max, plus bg_viewKickRandom of it as random yaw. APPROXIMATE:
        // 0.2 / 5 / 90 / 0.4 are IW4's dvar defaults as recalled, not read from the game. First
        // person fires along the camera's centre, so it throws the aim too, as in MW2.
        const float ViewKickScale = 0.2f, ViewKickMin = 5f, ViewKickMax = 90f, ViewKickRandom = 0.4f;
        Vector3 dmgKick;
        float dmgKickAt = -10f;

        Vector3 DamageKick()
        {
            float t = Time.time - dmgKickAt;
            if (t < 0f || t >= 0.5f) return Vector3.zero;
            return dmgKick * (t < 0.1f ? t / 0.1f : 1f - (t - 0.1f) / 0.4f);
        }

        void Flinch(DamageDealtMessage msg, float full)
        {
            if (!Plugin.Instance.DamageFlinch.Value || (msg.damageType & (DamageType.DoT | DamageType.FallDamage)) != 0) return;
            // Scaled down (FlinchScale: RoR2 hits all the time - at MW2's 5 degree minimum on every hit the
            // view never settled, "getting dizzy", playtest 10-06-26); a smaller hit doesn't restart a bigger
            // flinch still playing.
            float fs = Mathf.Clamp(Plugin.Instance.FlinchScale.Value, 0f, 2f);
            float kick = Mathf.Clamp(msg.damage / full * 100f * ViewKickScale, ViewKickMin, ViewKickMax) * fs;
            if (kick < 0.05f || (Time.time - dmgKickAt < 0.5f && kick < dmgKick.magnitude * 0.8f)) return;
            // Where it came from against the view: in front pitches the view up, a side rolls it.
            var from = msg.attacker != null ? msg.attacker.transform.position : msg.position;
            var to = Vector3.ProjectOnPlane(from - body.corePosition, Vector3.up).normalized;
            var fwd = Space.DirToUnity(ViewForward(new Vec3f(0f, last.viewangles.y, 0f)));
            float front = to.sqrMagnitude > 0f ? Vector3.Dot(to, fwd) : 1f;
            float side = to.sqrMagnitude > 0f ? Vector3.Dot(to, Vector3.Cross(Vector3.up, fwd)) : 0f; // + = from the right
            dmgKick = new Vector3(-kick * front, kick * ViewKickRandom * UnityEngine.Random.Range(-1f, 1f), kick * side);
            dmgKickAt = Time.time;
            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[flinch] {msg.damage:F0} of {full:F0} -> kick {kick:F1} deg (pitch {dmgKick.x:F1}, yaw {dmgKick.y:F1}, roll {dmgKick.z:F1})");
        }
        public float CameraFov = 60f;

        public void DrawHud()
        {
            if (!Active) return;
            if (!streaks.FreezesPlayer)
            {
                float hp = body != null && body.healthComponent != null ? body.healthComponent.combinedHealthFraction : 1f;
                if (Scoped)
                {
                    Mw2Hud.Scope(Native.WeaponString(last.weapon, 1), weaponView);
                    if (CanHoldBreath && !HoldingBreath && sway.breathMs == 0)
                        if (!Mw2MenuHud.Ready) Mw2Font.Label(new Rect(0, Screen.height * 0.78f, Screen.width, 24), "Hold Shift to Hold Breath", Mw2Hud.S(22f), new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleCenter, Mw2Font.Small);
                }
                // The showcase reel (Mw2Cinema): the gun and its scope only.
                if (Mw2Cinema.On && Mw2Cinema.HideHud) { if (Mw2Cinema.ShowStreakHud) streaks.DrawJobsHud(lastCam); return; }
                long g0 = Mw2Perf.Begin();
                Mw2Feedback.Draw(lastCam); // blood + damage direction, under the HUD
                Mw2Perf.End("g.feedback", g0); g0 = Mw2Perf.Begin();
                if (!rankBadgeWired) { rankBadgeWired = true; Mw2Killstreaks.RankBadge = r => progress.DrawRankBadge(r); }
                Mw2Hud.Draw(in last, weaponName, last.weapon, MagSize(), Armed, lastCam, hp, streaks.FreezesPlayer ? 0u : AnimWeapon);
                Mw2Net.DrawTeamRanks(lastCam);
                Mw2Perf.End("g.hud", g0); g0 = Mw2Perf.Begin();
                if (Armed && Mw2MenuHud.Ready) DrawMenuHud();
                Mw2Perf.End("g.menuhud", g0);
                // The view key, small, in MW2's hint style (smallFont at 0.65 alpha), above RoR2's
                // health bar.
                if (!ThirdOnly) Mw2Font.Label(new Rect(Screen.width * 0.063f, Screen.height * 0.84f, Screen.width * 0.3f, Mw2Hud.S(24f)),
                    $"[F7] {(wantFirstPerson ? "Third Person" : "First Person")}", Mw2Hud.S(20f), new Color(1f, 1f, 1f, 0.65f), TextAnchor.MiddleLeft, Mw2Font.Small);
            }
            long g1 = Mw2Perf.Begin();
            streaks.DrawHud(lastCam);
            DrawOma();
            DrawNotice();
            Mw2Perf.End("g.streaks", g1); g1 = Mw2Perf.Begin();
            if (!streaks.FreezesPlayer) progress.Draw();
            Mw2Perf.End("g.progress", g1);
        }
        public bool Owns(CharacterMotor m) => Active && haveTarget && m == motor && !ror2Moving;

        // RoR2 equipment that moves the body itself (playtest 10-07-26: "the teleporter, the wings that
        // stuff acts funny with our mod"): a vehicle seat (Eccentric Vase's tunnel, Volcanic Egg's
        // fireball) or Milky Chrysalis's wings. MW2's movement wrote the velocity every tick and fought
        // them; it steps aside while one is on, and the sim re-seats on the body after ("RoR2 moved").
        bool ror2Moving;
        static readonly System.Reflection.MethodInfo findJetpack = HarmonyLib.AccessTools.Method(typeof(JetpackController), "FindJetpackController");

        public bool RoR2MovingForTest => ror2Moving;

        /// Pilot: a sound played now (a notetrack the pilot times itself).
        public void PilotPlayAlias(string alias) { audio.PlayAlias(alias, 1f); Plugin.Log.LogInfo($"[vmsound] {alias} (pilot)"); }

        bool RoR2Moves()
        {
            if (body == null) return false;
            if (body.currentVehicle != null) return true;
            var jet = findJetpack != null ? findJetpack.Invoke(null, new object[] { body.gameObject }) as JetpackController : null;
            return jet != null && jet.isActiveAndEnabled;
        }
        bool Armed => Active && last.weapon != 0;

        public bool SuppressesSkill(GenericSkill skill)
        {
            // The MW2 Soldier's special is on RoR2's special bind (often bound to Mouse 4) and our own
            // key; not when it's RoR2's default R, which is MW2's reload.
            if (skill != null && skill.characterBody != null && Mw2Survivor.IsMw2(skill.characterBody) && skill == skill.characterBody.skillLocator?.special && !Mw2Survivor.FiringSpecial && In.Key(KeyCode.R)) return true;
            if (Active && body != null && streaks.FreezesPlayer && skill != null && skill.characterBody == body) return true;
            if (!Armed || body == null || skill == null || body.skillLocator == null) return false;
            return skill == body.skillLocator.primary || skill == body.skillLocator.secondary;
        }

        /// Multiplier on MW2's ground speeds. Automatic = MW2's own speed in metres (an MW2 unit is an
        /// inch; WorldScale makes a unit bigger so the soldier stands a survivor's height, which alone
        /// would run him ~1.5x too fast). playtest 10-03-26: sped up, MW2's run anims and footsteps felt
        /// wrong; the enemies slow down instead (EnemySpeedScale follows).
        public float MoveSpeedScale()
        {
            float cfg = Plugin.Instance.MoveSpeedScale.Value;
            return cfg > 0f ? cfg : Mw2OwnSpeed() * Mathf.Max(Plugin.Instance.MoveSpeedBoost.Value, 0.1f);
        }

        /// MW2's own ground speed in metres (an MW2 unit is an inch).
        static float Mw2OwnSpeed() => 0.0254f / Mathf.Max(Space.Scale, 0.001f);

        /// Enemies slow to MW2's own speed (not the boost: playtest 10-03-26 wanted him faster on
        /// RoR2's maps, not the monsters).
        public float EnemySpeedScale()
        {
            float cfg = Plugin.Instance.EnemySpeedScale.Value;
            if (cfg > 0f) return cfg;
            float own = Plugin.Instance.MoveSpeedScale.Value > 0f ? Plugin.Instance.MoveSpeedScale.Value : Mw2OwnSpeed();
            return Mathf.Clamp(Mw2WalkUnitsPerSec * Space.Scale * own / Ror2SurvivorWalkMetresPerSec, 0.3f, 1.5f);
        }

        // RoR2's extra jumps (Hopoo Feathers): MW2 jumps again in the air, one per feather.
        int airJumps;
        bool jumpWasDown;
        // sqrt(2 * g 800 * jump_height 39) units/s, times the jump height setting's square root.
        static float Mw2JumpSpeed => 250f * Mathf.Sqrt(Mathf.Clamp(Plugin.Instance.JumpHeightScale.Value, 0.5f, 4f));

        public void SampleKeys()
        {
            reloadHeld = In.Reload;
            // Key presses latch here (every frame) for the next sim step: read in the fixed step they
            // were missed, or seen twice when two steps ran in one frame (prone on, then off again).
            crouchPressed |= In.KeyDown(In.CrouchKey);
            pronePressed |= In.KeyDown(In.ProneKey);
        }
        bool crouchPressed, pronePressed;

        /// Per frame: killstreak input and updates.
        public void FrameUpdate(CharacterBody current)
        {
            if (Active) Mw2Feedback.Update(current, audio, Time.deltaTime);
            if (Active)
            {
                uint mv = Mw2Steps.Drain(sim, audio, last.sprinting != 0);
                characterEvents |= mv;
                netEvents |= mv;
            }
            if (Active) Mw2Projectiles.Update(sim, current);
            if (Active) Mw2Deathstreaks.Update(this);
            UpdateOma();
            Mw2TacticalInsertion.Update();
            Mw2Challenges.Update(streaks);
            Mw2RoR2Hud.Apply(Active && Mw2MenuHud.Ready);
            // Warm-up steps build meshes on the main thread (30-90 ms each). During the prematch freeze
            // (the player is held behind the grey countdown) they run back to back in ~30 ms slices;
            // otherwise one per 0.25 s. Trickling them into the first seconds of play hitched kills there.
            if (Active && warm.Count > 0 && (Mw2Prematch.Frozen || Time.unscaledTime >= nextWarm))
            {
                nextWarm = Time.unscaledTime + 0.25f;
                long w0 = Mw2Perf.Begin();
                do
                {
                    try { warm.Dequeue()(); } catch (Exception e) { Plugin.Log.LogWarning($"MW2 warm-up step failed: {e.Message}"); }
                } while (Mw2Prematch.Frozen && warm.Count > 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - w0) * 1000L / System.Diagnostics.Stopwatch.Frequency < 30);
                Mw2Perf.End("u.warm", w0);
            }
            if (Active) streaks.Update(current);
            AdsView();
        }

        /// MW2's third-person playlists aim in first person: in third person, ADS (and its ease
        /// back out) is seen down the sights, then back over the shoulder (playtest 10-04-26).
        void AdsView()
        {
            // Third person only switched off mid-run (the pause-menu setting): stay in third person,
            // F7 goes to first person from there.
            if (thirdOnlyWas && !ThirdOnly) wantFp = false;
            thirdOnlyWas = ThirdOnly;
            if (!Active || body == null || wantFirstPerson) return;
            if (ThirdOnly) { if (view.FirstPerson) view.Exit(); return; }
            var bank = body.inputBank;
            // On MW2's own ADS, not the button: MW2 refuses to aim mid-reload, mid-throw, mid-swap
            // and while sprinting, and going down the sights on the button alone left the view stuck
            // in first person at the hip until that ended (playtest 10-06-26).
            bool aim = Armed && !Akimbo && !Mw2Shield.Is(last.weapon) && !streaks.FreezesPlayer && !HeldStill
                && last.adsFrac > 0.01f;
            if (aim && !view.FirstPerson) view.Enter(body, 60f * Space.Scale);
            else if (!aim && view.FirstPerson) view.Exit();
        }

        /// Per physics tick, before FixedStep: re-take a new body after a stage change or revive.
        public void Keep(CharacterBody current)
        {
            if (!wantOn || Active || current == null || current.healthComponent == null || !current.healthComponent.alive) return;
            // MW2 mode is the MW2 Soldier's (Mw2Survivor); other survivors only by hand (F6).
            if (!Mw2Survivor.IsMw2(current) && !Manual) return;
            if (current.characterMotor == null || current.inputBank == null) return;
            // The match start begins with the body (no waiting for the takeover below).
            Mw2Prematch.OnAttached(this);
            // Without a drop pod RoR2 spawns the survivor rising out of the floor (its spawn state);
            // taking the body then started MW2 under the ground (playtest 10-02-26). Wait for its main state.
            var bodyMachine = EntityStateMachine.FindByCustomName(current.gameObject, "Body");
            if (bodyMachine != null && !(bodyMachine.state is EntityStates.GenericCharacterMain)) return;
            Toggle(current);
            if (Active) Plugin.Log.LogInfo("MW2 mode re-attached to the new body.");
        }

        public void Toggle(CharacterBody b)
        {
            if (Active) { wantOn = false; Release(); RefreshEnemyStats(); return; }
            if (b == null || b.characterMotor == null || b.inputBank == null) return;
            body = b;
            BeginLife(b); // before the class is read below
            attachedStage = Stage.instance;
            motor = b.characterMotor;
            UnityWorld.SetWalkableSlope(motor.Motor != null ? motor.Motor.MaxStableSlopeAngle : 0f);
            wantOn = true;
            var capsule = b.GetComponent<CapsuleCollider>();
            float height = capsule != null ? capsule.height * b.transform.lossyScale.y : 1.8f;
            // MW2's player is 70 units tall; WorldScale grows everything MW2 (soldier, sentry,
            // aircraft, FX, movement, jumps) together to fit RoR2's bigger world (playtest: 1.5x).
            Space.Scale = height / 70f * Mathf.Max(Plugin.Instance.WorldScale.Value, 0.1f);
            footToTransform = b.transform.position - b.footPosition;
            var origin = Space.ToIw(ClearStart(b.footPosition));
            Native.mw2_set_sprint_scale(Plugin.Instance.SprintTimeScale.Value);
            Native.mw2_set_jump_scale(Plugin.Instance.JumpHeightScale.Value);
            Native.mw2_set_hybrid_movement(Plugin.Instance.AirControl.Value, Plugin.Instance.NoLandingSlowdown.Value ? 1 : 0);
            sim = Native.mw2_create(ref origin);
            stuckFor = 0f;
            haveTarget = false;
            msAccumulator = 0f;
            last = default;
            audio.Attach(b.gameObject);
            b.hideCrosshair = true;
            RestoreCameraTilt(); // a new body: no death camera or old recoil left on the camera
            maxedThisLife.Clear();
            lastHit = 0;
            foreach (var bag in scavBags) if (bag.go != null) UnityEngine.Object.Destroy(bag.go);
            scavBags.Clear();
            if (wantFirstPerson) view.Enter(b, 60f * Space.Scale);

            if (Plugin.Instance.weaponsOk)
            {
                var cls = ClassLoadout();
                loadout = cls != null
                    ? new[] { cls[0], cls[1] }.Where(w => w.Length > 0 && Native.WeaponIndex(w) != 0).ToArray()
                    : Plugin.Instance.Loadout.Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (cls != null) Plugin.Log.LogInfo($"MW2 class {Plugin.Instance.PlayClass}: {string.Join(" / ", cls)}");
                loadoutSlot = 0;
                Give(loadout.Length > 0 ? loadout[0] : "ak47_mp");
                Mw2Deathstreaks.OnSpawn(b, cls != null && cls.Length > 11 ? cls[11] : null);
                GiveEquipment();
            }
            tracer = tracer ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/Tracers/TracerCommandoDefault");
            hitspark = hitspark ?? LegacyResourcesAPI.Load<GameObject>("Prefabs/Effects/HitsparkCommando");
            streaks.Audio = audio;
            progress.Audio = audio;
            streaks.OnUsed = id => progress.Add((int)Native.mw2_streak_xp(id));
            streaks.Grounded = () => last.grounded != 0;
            streaks.CallInGive = CallInWeapon;
            Mw2Fx.PlaySound = streaks.FxSound;
            Mw2Gunfire.ImpactSound = (alias, at) => { if (alias != null) streaks.FxSound(alias, at); };
            streaks.CallInRestore = RestoreWeapon;
            streaks.Sim = sim;
            streaks.Attach(b);
            streaks.LoadLoadout();
            QueueWarmup(b);
            Mw2Prematch.OnAttached(this);
            RefreshEnemyStats();
            Plugin.Log.LogInfo($"MW2 scale {Space.Scale * 100f:F2} cm/unit, move speed x{MoveSpeedScale():F2}, enemy speed x{EnemySpeedScale():F2}");
        }

        uint lethalWeapon, tacticalWeapon;
        int lethalMax, tacticalMax;

        /// Pilot: the ADS idle sway from its start (each showcase clip sways alike).
        /// Pilot: the held weapon ready now, no raise (the animatic swaps guns in place).
        public void PilotReady() { if (sim != IntPtr.Zero) Native.mw2_pilot_ready(sim); }
        /// Pilot: the held gun's clip slot length in MW2 play (seconds; 0 = no clip).
        public float PilotSlotSeconds(int slot) => viewmodel.SlotSeconds(slot);
        /// Pilot: the held magazine(s) full (the animatic's firing beats).
        public void PilotFillClip() { if (sim != IntPtr.Zero) Native.mw2_pilot_fill_clip(sim); }
        /// Pilot: the sights where they were after a swap (0..1).
        public void PilotSetAds(float frac) { if (sim != IntPtr.Zero) { Native.mw2_pilot_set_ads(sim, frac); last.adsFrac = frac; } }
        public void PilotResetSway() { if (sim != IntPtr.Zero) Native.mw2_reset_idle_sway(sim); }

        /// Pilot: a third of the magazine gone (at least a round), so a reload plays without a shot fired.
        public void PilotSpendClip()
        {
            if (sim == IntPtr.Zero || last.clip <= 0) return;
            Native.mw2_set_ammo(sim, Math.Max(0, last.clip - Math.Max(1, last.clip / 3)), Math.Max(last.stock, last.clip * 2));
            Native.mw2_set_left_clip(sim, Math.Max(0, last.clip - Math.Max(1, last.clip / 3))); // akimbo: the left gun too
        }

        /// Pilot: carry `name` as the tactical (one of it).
        public void SetTacticalForTest(string name)
        {
            tacticalWeapon = Native.WeaponIndex(name);
            tacticalMax = 1;
            if (sim != IntPtr.Zero) Native.mw2_set_offhand(sim, lethalWeapon, lethalMax, tacticalWeapon, 1);
        }

        /// Pilot: carry `name` as the lethal (one of it), e.g. the throwing knife for a damage test.
        public void SetLethalForTest(string name)
        {
            lethalWeapon = Native.WeaponIndex(name);
            lethalMax = 1;
            if (sim != IntPtr.Zero) Native.mw2_set_offhand(sim, lethalWeapon, 1, tacticalWeapon, tacticalMax);
        }

        /// MW2 class equipment for this life (config Equipment section).
        void GiveEquipment()
        {
            GiveClassEquipment();
            // A Tactical Insertion being thrown keeps its flare in the tactical slot (playtest 10-04-26:
            // a class handed out at spawn put the concussion back mid-throw and that got thrown); the
            // class's tacticals come back once the flare is out.
            if (tiThrowing && sim != IntPtr.Zero)
            {
                tiSavedTacticals = tacticalMax;
                tiSavedLethals = lethalMax;
                Native.mw2_set_offhand(sim, 0, 0, TiFlare, 1);
            }
        }

        void GiveClassEquipment()
        {
            var cfg = Plugin.Instance;
            var cls = ClassLoadout();
            if (cls != null)
            {
                // Equipment that isn't a weapon (Tactical Insertion, Blast Shield) leaves no lethal.
                lethalWeapon = Native.WeaponIndex(cls[2]);
                tacticalWeapon = Native.WeaponIndex(cls[3]);
                int tacticals = int.TryParse(cls[4], out var t) ? t : 1;
                lethalMax = lethalWeapon != 0 ? 1 : 0;
                tacticalMax = tacticalWeapon != 0 ? tacticals : 0;
                if (sim != IntPtr.Zero) Native.mw2_set_offhand(sim, lethalWeapon, lethalMax, tacticalWeapon, tacticalMax);
                // The class's camos on its two guns (camoTable id for the viewmodel cache).
                if (cls.Length >= 14)
                {
                    for (int slot = 0; slot < 2; slot++)
                    {
                        string camo = cls[12 + slot];
                        uint.TryParse(Mw2Menus.TableLookup("mp/camoTable.csv", 1, camo, 0), out uint camoId);
                        Native.SetCamo(Native.WeaponIndex(cls[slot]), camo, camo == "none" ? 0 : camoId);
                    }
                }
                // The class's perks 1-3 as items (the deathstreak comes later, on its trigger).
                if (cls.Length >= 11) Mw2Perks.Apply(body, new[] { cls[8], cls[9], cls[10] });
                return;
            }
            lethalWeapon = Native.WeaponIndex(cfg.Lethal.Value);
            tacticalWeapon = Native.WeaponIndex(cfg.Tactical.Value);
            // What the RoR2-ammo cooldowns refill to (left unset, the config loadout never recharged).
            lethalMax = lethalWeapon != 0 ? cfg.LethalCount.Value : 0;
            tacticalMax = tacticalWeapon != 0 ? cfg.TacticalCount.Value : 0;
            if (sim != IntPtr.Zero) Native.mw2_set_offhand(sim, lethalWeapon, cfg.LethalCount.Value, tacticalWeapon, cfg.TacticalCount.Value);
        }

        /// The Create-a-Class loadout in play (config MW2/Class), or null for the config lists.
        static string[] ClassLoadout()
        {
            var cfg = Plugin.Instance;
            if (!cfg.UseCustomClass.Value) return null;
            var cls = Mw2Menus.ClassLoadout(cfg.PlayClass - 1);
            return cls != null && cls.Length >= 5 && cls[0].Length > 0 ? cls : null;
        }

        // Per-weapon clip / reserve while it is put away (alternate GL, primary <-> secondary).
        readonly Dictionary<uint, (int clip, int stock)> stowedAmmo = new Dictionary<uint, (int, int)>();
        uint altFrom; // on the alternate (underbarrel GL / shotgun): the weapon it hangs off

        void StowCurrent()
        {
            if (sim != IntPtr.Zero && last.weapon != 0 && Native.mw2_weapon_ammo(sim, out int c, out int st) == 1) stowedAmmo[last.weapon] = (c, st);
        }

        void RestoreStowed(uint idx)
        {
            if (stowedAmmo.TryGetValue(idx, out var a)) Native.mw2_set_ammo(sim, a.clip, a.stock);
        }

        /// MW2 +actionslot 3: to the underbarrel grenade launcher / shotgun and back, with MW2's
        /// alternate raise (not a full draw), each keeping its own ammo.
        public void ToggleAlternate()
        {
            if (!Active || !Armed || streaks.CallingIn || streaks.FreezesPlayer) return;
            uint from = last.weapon;
            uint to = altFrom != 0 ? altFrom : Native.mw2_weapon_alternate(from);
            if (to == 0) return;
            StowCurrent();
            if (Native.mw2_switch_alternate(sim, to, Native.mw2_weapon_alternate_raise_ms(from)) != 1) return;
            altFrom = altFrom != 0 ? 0 : from;
            RestoreStowed(to);
            Armed_(to, Native.WeaponString(to, 2));
            Plugin.Log.LogInfo($"[weapon] alternate -> {weaponName}");
        }

        void Give(string name)
        {
            if (!Active) return;
            uint idx = Native.WeaponIndex(name);
            if (idx == 0) { Plugin.Log.LogWarning($"Unknown MW2 weapon '{name}'"); return; }
            StowCurrent();
            altFrom = 0;
            Native.mw2_give_weapon(sim, idx);
            RestoreStowed(idx);
            Armed_(idx, name);
        }

        void Armed_(uint idx, string name)
        {
            weaponName = name;
            last.weapon = idx;
            if (Native.mw2_weapon_view(idx, out weaponView) != 1) weaponView = default;
            if (body != null)
            {
                audio.Attach(body.gameObject);
                audio.Play(idx, WeaponSound.Raise);
                // The third-person gun builds when it's first needed (OnCamera), not on every switch.
                if (!view.FirstPerson) try { gun.Build(body, idx, Space.Scale); }
                catch (Exception e) { Plugin.Log.LogWarning($"MW2 gun model failed: {e}"); }
            }
            magSize = 0; // learned from the first full clip we see
        }

        /// The host (single player too) gets no DamageDealtMessage for its own body, only the
        /// server's report: the same hit as a message.
        public void OnServerDamage(DamageReport r)
        {
            if (r == null || body == null || r.victimBody != body || r.damageDealt <= 0f || r.damageInfo == null) return;
            OnDamageNotified(new DamageDealtMessage { victim = body.gameObject, damage = r.damageDealt, attacker = r.attacker, position = r.damageInfo.position, crit = r.damageInfo.crit, damageType = r.damageInfo.damageType });
        }

        float lastDamageAt = -1f, lastDamage;

        public void OnDamageNotified(DamageDealtMessage msg)
        {
            if (!Active || msg == null || body == null || msg.victim != body.gameObject) return;
            // A client-hosted host can see one hit both ways.
            if (Time.time - lastDamageAt < 0.1f && Mathf.Abs(msg.damage - lastDamage) < 0.01f) return;
            lastDamageAt = Time.time; lastDamage = msg.damage;
            combatThisLife = true;
            var from = msg.attacker != null ? msg.attacker.transform.position : msg.position;
            Mw2Hud.Damaged(from);
            // MW2's PAIN / DEATH pick by damage type, where it hit and from which side.
            float full = body.healthComponent != null ? Mathf.Max(body.healthComponent.fullCombinedHealth, 1f) : 100f;
            lastHit = Mw2Character.HitFrom(body, Space.DirToUnity(ViewForward(new Vec3f(0f, last.viewangles.y, 0f))), msg);
            Flinch(msg, full);
            // A flinch for a heavy hit only (RoR2 hits all the time; MW2's pain anim takes the whole body ~1.3 s).
            if (msg.damage >= full * 0.2f && Time.time >= nextPain)
            {
                nextPain = Time.time + 2f;
                characterEvents |= Mw2Character.EvPain;
                netEvents |= Mw2Character.EvPain;
            }
        }

        /// RoR2's death event (raised on the host only): the host relays it so each client can count
        /// its own kills and deaths (Mw2Net KKill -> OnNetKill / OnNetDeath).
        public void OnKill(DamageReport report)
        {
            // Clients count from the host's relay only (no double count if RoR2 ever raises it there).
            if (report == null || !UnityEngine.Networking.NetworkServer.active) return;
            if (Mw2Net.Online) Mw2Net.ServerDeath(report.attackerBody, report.victimBody);
            if (Active && body != null && report.victimBody == body) Mw2Deathstreaks.OnDeath(body, this);
            if (body != null && report.attackerBody == body && report.victimBody != body)
                Killed(report.victimBody != null ? report.victimBody.corePosition : (Vector3?)null, Mw2Progress.KillKind(report.victimBody));
        }

        /// From the host: this client's body killed something at `victimPos`.
        public void OnNetKill(Vector3 victimPos, byte kind)
        {
            if (body == null) return;
            Mw2Challenges.With(Mw2Challenges.Recent(), () => Killed(victimPos, kind));
        }

        void Killed(Vector3? victimPos, byte kind)
        {
            if (Active) Mw2Deathstreaks.OnKill();
            long p0 = Mw2Perf.Begin();
            if (Active) streaks.OnKill();
            Mw2Perf.End("k.streak", p0); p0 = Mw2Perf.Begin();
            if (Active) progress.Add(Mw2Progress.KillXp(kind));
            Mw2Perf.End("k.xp", p0); p0 = Mw2Perf.Begin();
            if (!Armed) return;
            // Gun challenges count bullet kills; the perk Pro challenges by kind of kill.
            if (Mw2Challenges.Current == Mw2Challenges.Cause.Bullet)
                Mw2Challenges.Report(Mw2Challenges.Kill(Native.WeaponString(last.weapon, 2)), progress, streaks);
            Mw2Perf.End("k.gun", p0); p0 = Mw2Perf.Begin();
            Mw2Challenges.Report(Mw2Challenges.PerkKill(body, victimPos, Native.WeaponString(last.weapon, 2), last.adsFrac > 0.5f), progress, streaks);
            Mw2Perf.End("k.perk", p0);
            // Scavenger: the kill drops MW2's scavenger bag where he fell (_weapons.gsc
            // dropScavengerForDeath); walking over it pays out. With a Create-a-Class loadout only
            // the perk does it, as in MW2; the config loadout keeps the AmmoMode stand-in.
            bool scavenger = ClassLoadout() != null
                ? Mw2Perks.Has(body, "specialty_scavenger")
                : string.Equals(Plugin.Instance.AmmoMode.Value, "Scavenger", StringComparison.OrdinalIgnoreCase);
            if (scavenger)
            {
                if (victimPos is Vector3 at) DropScavengerBag(at);
                else ScavengerPickup();
            }
        }

        // ---- MW2's scavenger bag (scavenger_bag_mp) ----
        class ScavBag { public GameObject go; public Vector3 at; public float until; }
        readonly List<ScavBag> scavBags = new List<ScavBag>();
        string scavModel;

        /// Pilot: a bag at `at`; the reserve before it is taken.
        public int PilotScavengerBag(Vector3 at) { Native.mw2_set_ammo(sim, last.clip, 0); DropScavengerBag(at); return 0; } // reserve emptied first

        void DropScavengerBag(Vector3 at)
        {
            if (Physics.Raycast(at + Vector3.up * 0.5f, Vector3.down, out var g, 30f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) at = g.point;
            if (scavModel == null)
            {
                var nb = new byte[96];
                int len;
                unsafe { fixed (byte* p = nb) len = Native.mw2_weapon_world_model(Native.WeaponIndex("scavenger_bag_mp"), p, 96); }
                scavModel = len > 0 ? System.Text.Encoding.UTF8.GetString(nb, 0, Math.Min(len, 96)) : "";
            }
            GameObject go = scavModel.Length > 0 ? Mw2Prop.Build(scavModel, body, Space.Scale) : null;
            if (go != null) go.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            // How long a dropped bag stays is an engine value not in the scripts: ~60 s (approximate).
            scavBags.Add(new ScavBag { go = go, at = at, until = Time.time + 60f });
        }

        /// Per step: walk over a bag (a little over MW2's pickup reach) to take it.
        void UpdateScavengerBags()
        {
            for (int i = scavBags.Count - 1; i >= 0; i--)
            {
                var b = scavBags[i];
                bool take = body != null && (body.footPosition - b.at).sqrMagnitude < 1.2f * 1.2f;
                if (!take && Time.time < b.until) continue;
                if (b.go != null) UnityEngine.Object.Destroy(b.go);
                scavBags.RemoveAt(i);
                if (take) ScavengerPickup();
            }
        }

        // ---- RoR2-style ammo (playtest 10-06-26: "more true to ror2 and feels better") ----
        // Guns: the reserve never runs out (magazines and reloads stay MW2's). Launchers and offhands:
        // one back at a time on a cooldown - like RoR2's skills - held or holstered. Scavenger Pro
        // (specialty_extraammo) turns them 25% faster.
        readonly Dictionary<uint, float> launcherCd = new Dictionary<uint, float>();
        float lethalCd, tacticalCd;

        static bool IsLauncher(string wn) => wn != null && (wn.StartsWith("at4") || wn.StartsWith("rpg") || wn.StartsWith("stinger") || wn.StartsWith("javelin") || wn.StartsWith("m79") || wn.StartsWith("gl_"));

        /// The launchers this class carries: its own, and the primary's underbarrel grenade launcher.
        IEnumerable<uint> Launchers()
        {
            foreach (var n in loadout)
            {
                uint w = Native.WeaponIndex(n);
                if (w == 0) continue;
                if (IsLauncher(n)) yield return w;
                uint alt = Native.mw2_weapon_alternate(w);
                if (alt != 0 && IsLauncher(Native.WeaponString(alt, 2))) yield return alt;
            }
        }

        void TickRoR2Ammo(float dt)
        {
            if (dt <= 0f || body == null) return;
            float speed = Mw2Perks.Has(body, "specialty_extraammo") ? 1f / 0.75f : 1f;
            if (!IsLauncher(weaponName)) Native.mw2_add_reserve(sim, -1);
            // RoR2's skill items, read off the MW2 Soldier's own skills (playtest 10-06-26: "backup mag
            // does absolutely nothing"): the secondary's extra charges (Backup Magazine) are extra
            // rockets and grenades, its cooldown items (Alien Head, Purity, ...) speed launchers and
            // lethals; the utility's speed tacticals.
            var sec = body.skillLocator != null ? body.skillLocator.secondary : null;
            var util = body.skillLocator != null ? body.skillLocator.utility : null;
            int bonus = BackupMags();
            float lcd = SkillCooldown(sec, Plugin.Instance.LauncherCooldown.Value);
            foreach (uint w in Launchers())
            {
                int cap = Native.mw2_weapon_max_ammo(w) + bonus;
                bool held = w == last.weapon;
                int clip, stock;
                if (held) { if (Native.mw2_weapon_ammo(sim, out clip, out stock) != 1) continue; }
                else if (stowedAmmo.TryGetValue(w, out var s)) { clip = s.clip; stock = s.stock; }
                else continue; // never drawn yet: still full
                if (cap <= 0 || clip + stock >= cap) { launcherCd[w] = 0f; continue; }
                launcherCd.TryGetValue(w, out float t);
                t += dt * speed;
                if (t >= lcd) { t = 0f; GiveRocket(w, held, clip, stock, cap); }
                launcherCd[w] = t;
            }
            if (Native.mw2_offhand_viewmodel_weapon(sim) != 0) return; // not mid-throw
            if (Native.mw2_offhand_ammo(sim, out int lethal, out int tactical) != 1) return;
            bool more = false;
            if (lethalWeapon != 0 && lethal < lethalMax + bonus) { lethalCd += dt * speed; if (lethalCd >= SkillCooldown(sec, Plugin.Instance.LethalCooldown.Value)) { lethalCd = 0f; lethal++; more = true; } } else lethalCd = 0f;
            if (tacticalWeapon != 0 && tactical < tacticalMax) { tacticalCd += dt * speed; if (tacticalCd >= SkillCooldown(util, Plugin.Instance.TacticalCooldown.Value)) { tacticalCd = 0f; tactical++; more = true; } } else tacticalCd = 0f;
            if (more) Native.mw2_set_offhand(sim, lethalWeapon, lethal, tacticalWeapon, tactical);
        }

        /// A RoR2 cooldown run through a skill's cooldown items, the way RoR2 does its own
        /// (scale, then flat reduction, at least 0.5 s).
        static float SkillCooldown(GenericSkill skill, float seconds)
        {
            seconds = Mathf.Max(seconds, 0.5f);
            if (skill == null) return seconds;
            return Mathf.Max(0.5f, seconds * skill.cooldownScale - skill.flatCooldownReduction);
        }

        void GiveRocket(uint w, bool held, int clip, int stock, int cap)
        {
            if (clip + stock >= cap) return;
            // Set rather than added: the sim's add caps reserve at MW2's max, and Backup Magazines
            // carry more than that.
            if (held) Native.mw2_set_ammo(sim, clip, stock + 1);
            else stowedAmmo[w] = (clip, stock + 1);
        }

        /// handleScavengerBagPickup: a magazine of reserve and +1 of each offhand, the pickup sound,
        /// and the scavenger icon at the crosshair (_damagefeedback "scavenger", 2.5 s).
        void ScavengerPickup()
        {
            Native.mw2_add_reserve(sim, MagSize());
            // RoR2 ammo: the bag is a rocket back in each launcher too (offhands below).
            if (Plugin.Instance.RoR2Ammo.Value)
                foreach (uint w in Launchers())
                {
                    int cap = Native.mw2_weapon_max_ammo(w) + BackupMags();
                    if (w == last.weapon) { if (Native.mw2_weapon_ammo(sim, out int c, out int st) == 1) GiveRocket(w, true, c, st, cap); }
                    else if (stowedAmmo.TryGetValue(w, out var s)) GiveRocket(w, false, s.clip, s.stock, cap);
                }
            // ch_scavenger_pro counts scavenger pickups.
            if (Mw2Perks.Has(body, "specialty_scavenger")) Mw2Challenges.Report(Mw2Challenges.Progress("ch_scavenger_pro", 1), progress, streaks);
            if (ClassLoadout() != null && Native.mw2_offhand_ammo(sim, out int frags, out int smokes) == 1)
                Native.mw2_set_offhand(sim, lethalWeapon, Mathf.Min(frags + 1, lethalMax + (Plugin.Instance.RoR2Ammo.Value ? BackupMags() : 0)), tacticalWeapon, Mathf.Min(smokes + 1, tacticalMax));
            audio.PlayAlias("scavenger_pack_pickup");
            Mw2Hud.ScavengerPickup();
        }

        /// Pilot: offhand counts and the caps they recharge to, for the RoR2-item checks.
        internal string AmmoDebug()
        {
            int le = -1, ta = -1;
            if (sim != IntPtr.Zero) Native.mw2_offhand_ammo(sim, out le, out ta);
            var sec = body != null && body.skillLocator != null ? body.skillLocator.secondary : null;
            return $"lethal {le}/{lethalMax + BackupMags()} tactical {ta}/{tacticalMax} backup mags {BackupMags()} launcher cd {SkillCooldown(sec, Plugin.Instance.LauncherCooldown.Value):F2}s lethal cd {SkillCooldown(sec, Plugin.Instance.LethalCooldown.Value):F2}s";
        }

        /// The camera is down the sights / in first person right now (pilot checks).
        internal bool ViewFirstPerson => view.FirstPerson;

        /// Extra charges on the MW2 Soldier's secondary skill (Backup Magazines).
        int BackupMags()
        {
            // Counted off the inventory: RoR2 gives the charges to secondaryBonusStockSkill, which
            // isn't the MW2 Soldier's secondary (a test with two read 0 off the skill).
            var inv = body != null ? body.inventory : null;
            return inv != null ? inv.GetItemCount(RoR2Content.Items.SecondarySkillMagazine) : 0;
        }

        int magSize;
        int MagSize() => magSize > 0 ? magSize : 30;

        /// 1 = primary, 2 = secondary, always (playtest 10-04-26); the slot already in hand stays.
        public void SelectWeapon(int slot)
        {
            if (!Active || slot < 0 || slot >= loadout.Length || weaponName == loadout[slot]) return;
            loadoutSlot = slot;
            Give(loadout[slot]);
        }

        public void CycleWeapon()
        {
            if (!Active || loadout.Length == 0) return;
            loadoutSlot = (loadoutSlot + 1) % loadout.Length;
            Give(loadout[loadoutSlot]);
        }

        static void RefreshEnemyStats()
        {
            foreach (var cb in CharacterBody.readOnlyInstancesList)
            {
                if (cb != null && cb.teamComponent != null && cb.teamComponent.teamIndex == TeamIndex.Monster)
                    cb.RecalculateStats();
            }
        }

        void Release()
        {
            streaks.Detach();
            streaks.Sim = IntPtr.Zero;
            viewmodel.Destroy();
            viewmodelLeft.Destroy();
            view.Exit();
            gun.Destroy();
            Mw2ItemDisplays.Unbind(characterHid);
            character.Destroy();
            characterTried = false;
            if (characterHid != null) characterHid.invisibilityCount--;
            characterHid = null;
            if (body != null) body.hideCrosshair = false;
            Mw2Projectiles.Clear();
            if (scopeThermal) { scopeThermal = false; Mw2Thermal.End(); }
            if (sim != IntPtr.Zero) Native.mw2_destroy(sim);
            sim = IntPtr.Zero;
            body = null;
            motor = null;
            haveTarget = false;
        }

        float frozenYaw, frozenPitch;

        public void FixedStep(CharacterBody current, TraceFn trace, float dt)
        {
            if (!Active) return;
            if (current == null || current != body || !body.healthComponent.alive)
            {
                // A client sees its own death here, before the body is let go (the host's kill
                // relay would arrive after it); the host counts it in OnKill. Dead, or gone without
                // a stage change (destroyed before its health arrived), is a death.
                bool gone = body == null || body.healthComponent == null || !body.healthComponent.alive || current == null;
                if (!UnityEngine.Networking.NetworkServer.active && gone && Stage.instance != null && Stage.instance == attachedStage)
                    Mw2Deathstreaks.OnDeath(body, this);
                // Killed (not a stage change): his MW2 soldier stays and dies MW2's way (Mw2DeathCam).
                if (body != null && body.healthComponent != null && !body.healthComponent.alive && character.Exists)
                {
                    var dir = Space.DirToUnity(ViewForward(new Vec3f(0f, last.viewangles.y, 0f)));
                    Mw2DeathCam.Begin(character, characterHid, body, lastCam, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, Armed ? last.weapon : 0u,
                        (byte)((last.pmFlags & 0x1) != 0 ? 2 : (last.pmFlags & 0x2) != 0 ? 1 : 0), lastHit);
                    character = new Mw2Character();
                    characterHid = null;
                }
                Release(); RefreshEnemyStats(); return;
            }

            bool moves = RoR2Moves();
            if (moves != ror2Moving)
            {
                ror2Moving = moves;
                Plugin.Log.LogInfo(moves ? $"MW2 movement: RoR2 equipment moves the body ({(body.currentVehicle != null ? body.currentVehicle.name : "wings")}) - handed over" : "MW2 movement: back from RoR2's equipment movement");
            }
            msAccumulator += dt * 1000f;
            int msec = Mathf.FloorToInt(msAccumulator);
            if (msec < 1) return;
            msAccumulator -= msec;

            var bank = body.inputBank;
            // Pilot: aim where its camera looks. It steers the camera only (Mw2Pilot.Steer) and RoR2's
            // aim never sees that, so with the view turned (the animatic's held frame) the RPG flew off
            // ~75 degrees to the side (10-05-26).
            var aimU = (Mw2Pilot.Active && view.FirstPerson ? Mw2Pilot.ViewForward : bank.aimDirection).normalized;
            // First person: MW2's eye (where its rockets and grenades leave from) isn't exactly RoR2's
            // camera, so along the view they flew parallel to the crosshair, off to one side (playtest
            // 10-05-26: "rpg was off"). Aim the sim from its eye at what the crosshair is on.
            if (view.FirstPerson && lastCam != null && last.viewHeight > 0f)
            {
                var camPos = lastCam.transform.position;
                float dist = Physics.Raycast(camPos, aimU, out var hit, 400f, LayerIndex.world.mask | LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Ignore)
                    ? Mathf.Max(hit.distance, 8f) : 400f;
                var eye = Space.ToUnity(last.origin) + Vector3.up * last.viewHeight * Space.Scale;
                var conv = (camPos + aimU * dist - eye).normalized;
                if (Vector3.Angle(conv, aimU) < 6f) aimU = conv;
            }
            var aim = Space.DirToIw(aimU);
            float yaw = Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(aim.z, -1f, 1f)) * Mathf.Rad2Deg;

            var move = Space.DirToIw(In.Move(bank));
            float yr = yaw * Mathf.Deg2Rad;
            float fwd = move.x * Mathf.Cos(yr) + move.y * Mathf.Sin(yr);
            float right = move.x * Mathf.Sin(yr) - move.y * Mathf.Cos(yr);
            // RoR2's move vector is normalized: W+D came in as 0.71 / 0.71 (90 of 127), under the
            // forward 105 that MW2's sprint needs, so diagonals dropped out of sprint and crawled
            // (playtest 10-06-26). MW2 on PC sends 127 / 127 for W+D and its cmd scale keeps diagonal
            // speed equal to straight: the bigger component carries the stick's full length.
            float big = Mathf.Max(Mathf.Abs(fwd), Mathf.Abs(right));
            if (big > 1e-4f)
            {
                float k = Mathf.Min(Mathf.Sqrt(fwd * fwd + right * right), 1f) / big;
                fwd *= k; right *= k;
            }

            uint buttons = 0;
            if (streaks.FreezesPlayer)
            {
                // Riding a killstreak: MW2 leaves the player standing where they used it.
                yaw = frozenYaw; pitch = frozenPitch;
                fwd = 0f; right = 0f;
            }
            else { frozenYaw = yaw; frozenPitch = pitch; }
            if (HeldStill) { fwd = 0f; right = 0f; }
            bool jumpDown = In.Jump(bank), jumpPressed = jumpDown && !jumpWasDown;
            jumpWasDown = jumpDown;
            // MW2 stances (PC: togglecrouch / toggleprone). Jump stands you up a step instead of
            // jumping (prone -> crouch -> stand), sprint stands you up.
            bool sprintDown = In.Sprint(bank), sprintPressed = sprintDown && !sprintWasDown;
            sprintWasDown = sprintDown;
            if (!jumpDown) jumpUsedForStance = false;
            if (!streaks.FreezesPlayer && !HeldStill && !Mw2Deathstreaks.InLastStand)
            {
                if (Mw2Pilot.Consume(ref crouchPressed) || Mw2Pilot.Consume(ref Mw2Pilot.CrouchPulse)) Stance = Stance == 1 ? 0 : 1;
                if (Mw2Pilot.Consume(ref pronePressed) || Mw2Pilot.Consume(ref Mw2Pilot.PronePulse)) Stance = Stance == 2 ? 0 : 2;
                if (Stance != 0 && jumpPressed) { Stance = Stance == 2 ? 1 : 0; jumpUsedForStance = true; }
                if (Stance != 0 && sprintPressed) Stance = 0;
            }
            crouchPressed = pronePressed = false; // not saved up while frozen / in menus / down
            if (Mw2Deathstreaks.InLastStand) { buttons |= Buttons.Prone; Stance = 0; } // last stand is prone
            else if (Stance == 1) buttons |= Buttons.Crouch;
            else if (Stance == 2) buttons |= Buttons.Prone;
            if (!streaks.FreezesPlayer && !HeldStill && jumpDown && !jumpUsedForStance) buttons |= Buttons.Jump;
            if (last.grounded != 0) airJumps = 0;
            else if (!streaks.FreezesPlayer && !HeldStill && jumpPressed && !jumpUsedForStance && airJumps < body.maxJumpCount - 1)
            {
                airJumps++;
                var jv = last.velocity;
                jv.z = Mw2JumpSpeed;
                pendingLaunch = jv;
                launchTicks = 2;
                Plugin.Log.LogInfo($"MW2 air jump {airJumps}/{body.maxJumpCount - 1}");
            }
            // A jump pad sets RoR2's "no air control until a collision" on the motor and only that
            // collision clears it - which the MW2 sim's landing doesn't make, so every pad after the
            // first was ignored (playtest 10-04-26). The sim landed: the motor has too.
            if (last.grounded == 0) { if (launchTicks == 0) airborneSinceLaunch = true; }
            else if (airborneSinceLaunch)
            {
                airborneSinceLaunch = false;
                var motor = body.characterMotor;
                if (motor != null && motor.disableAirControlUntilCollision) motor.disableAirControlUntilCollision = false;
            }
            bool breath = Armed && In.Ads(bank) && In.Sprint(bank);
            if (breath) buttons |= Buttons.Breath; // MW2 PC: the sprint key holds breath while aiming
            // RoR2's sprint latches on and nothing about aiming turns it off (the MW2 Soldier's M2
            // isn't a RoR2 skill), so after a sprint MW2 kept getting "sprint" under the ADS button
            // and never aimed (playtest 10-06-26: stuck at the hip in first person after sprinting).
            // Aiming wins, as in MW2.
            else if (!streaks.FreezesPlayer && !HeldStill && !(Armed && In.Ads(bank)) && (body.isSprinting || Mw2Pilot.Sprint)) buttons |= Buttons.Sprint;
            Admin.Apply(body);
            streaks.PlayerAttack = !Admin.Open && !HeldStill && In.Attack(bank);
            if (streaks.PlayerAttack && Armed) combatThisLife = true;
            if (streaks.CallInFire) buttons |= Buttons.Attack; // the detonator click
            // ...and its squeeze on screen: the sim never fires the device, so the clip is played here.
            if (streaks.CallInFire && !callInClicked && viewmodel.Handle != IntPtr.Zero) Native.mw2_viewmodel_play(viewmodel.Handle, 29); // DETONATE: viewmodel_C4_detonator_fire (3 is C4's throw)
            callInClicked = streaks.CallInFire;
            MarkerThrow(ref buttons);
            TacticalInsertionThrow(ref buttons);
            if (Armed && !streaks.FreezesPlayer && !streaks.BlocksWeapon && !streaks.CallingIn && !Admin.Open && !HeldStill)
            {
                // MW2's riot shield bashes on the attack button too.
                if (In.Attack(bank)) buttons |= Mw2Shield.Is(last.weapon) ? Buttons.Melee : Buttons.Attack;
                // Akimbo (MW2 PC): no aiming; the ADS button fires the right gun, attack the left.
                if (In.Ads(bank)) buttons |= Akimbo ? Buttons.Throw : Buttons.Ads;
                if (reloadHeld) buttons |= Buttons.Reload;
                // _damage.gsc last stand: DisableOffhandWeapons unless Last Stand Pro (laststandoffhand).
                bool offhands = !Mw2Deathstreaks.InLastStand || Mw2Perks.Has(body, "specialty_laststandoffhand");
                if (offhands && (In.Key(Plugin.Instance.LethalKey.Value) || Mw2Pilot.Frag)) buttons |= Buttons.Frag;
                if (offhands && (In.Key(Plugin.Instance.TacticalKey.Value) || Mw2Pilot.Smoke)) buttons |= Buttons.Smoke;
                if (In.Key(Plugin.Instance.MeleeKey.Value) || Mw2Pilot.Melee) buttons |= Buttons.Melee;
                Mw2Melee.UpdateTarget(sim, body, bank.aimOrigin, bank.aimDirection);
            }

            // RoR2 folds its sprint multiplier into moveSpeed; MW2 applies its own 1.5x.
            float sprintMul = body.isSprinting ? Mathf.Max(body.sprintingSpeedMultiplier, 0.01f) : 1f;
            float speedScale = body.baseMoveSpeed > 0f ? body.moveSpeed / sprintMul / body.baseMoveSpeed : 1f;
            speedScale *= MoveSpeedScale();
            speedScale *= Mw2Perks.MoveSpeed(body, last.sprinting != 0);
            Native.mw2_set_perks(sim, Mw2Perks.EngineBits(body) | (Plugin.Instance.UnlimitedSprint.Value ? Mw2Perks.MarathonBit : 0u));
            streaks.SetHardline(Mw2Perks.Has(body, "specialty_hardline"));

            if (launchTicks > 0) { launchTicks--; var lv = pendingLaunch; Native.mw2_set_velocity(sim, ref lv); }
            var input = new Mw2Input
            {
                msec = msec,
                buttons = buttons,
                forwardmove = (sbyte)Mathf.Clamp(Mathf.RoundToInt(fwd * 127f), -127, 127),
                rightmove = (sbyte)Mathf.Clamp(Mathf.RoundToInt(right * 127f), -127, 127),
                yaw = yaw,
                pitch = pitch,
                speedScale = speedScale,
                fireRate = Mathf.Max(body.attackSpeed, 0.01f),
            };
            lockOn.Update(body, last.weapon, last.adsFrac, lastCam != null ? lastCam : Camera.main, sim, audio);
            var wasAt = last.origin;
            bool wasSprinting = last.sprinting != 0;
            int ok = Native.mw2_step(sim, ref input, trace, IntPtr.Zero, out last);
            if (launchTicks > 0 && last.grounded == 0) launchTicks = 0; // airborne: the launch took
            SprintDistance(wasAt, wasSprinting);
            if (ok != 1) { Release(); RefreshEnemyStats(); return; }
            target = Space.ToUnity(last.origin) + footToTransform;
            haveTarget = true;
            Unstick(dt);
            UpdateScavengerBags();
            pushDir = Vector3.ProjectOnPlane(Space.DirToUnity(ViewForward(new Vec3f(0f, last.viewangles.y, 0f))) * input.forwardmove + Vector3.Cross(Vector3.up, Space.DirToUnity(ViewForward(new Vec3f(0f, last.viewangles.y, 0f)))) * input.rightmove, Vector3.up).normalized;
            UnstickGrounded(dt, input.forwardmove != 0 || input.rightmove != 0, wasAt);
            if (Armed && last.clip > magSize) magSize = last.clip;
            // First person: reload / rechamber sounds come from the viewmodel anims' notetracks.
            if (Armed) audio.OnEvents(last.weapon, last.events, last.clip, view.FirstPerson && viewmodel.Exists);
            if (last.events != 0)
            {
                uint ev = ((last.events & WeaponEvents.Shot) != 0 ? Mw2Character.EvFire : 0u)
                    | ((last.events & WeaponEvents.ReloadStart) != 0 ? Mw2Character.EvReload
                        | (last.clip == 0 ? Mw2Character.EvReloadEmpty : 0u)
                        | (Mw2Perks.Has(body, "specialty_fastreload") ? Mw2Character.EvReloadFast : 0u) : 0u)
                    | ((last.events & WeaponEvents.MeleeStart) != 0 ? Mw2Character.EvMelee | ((last.events & WeaponEvents.MeleeCharge) != 0 ? Mw2Character.EvMeleeCharge : 0u) : 0u)
                    | ((last.events & 128u) != 0 ? Mw2Character.EvThrow : 0u); // EV_OFFHAND_THROW
                characterEvents |= ev;
                netEvents |= ev;
            }
            if (Armed && (last.events & WeaponEvents.MeleeStart) != 0)
                Plugin.Log.LogInfo($"[melee] swing{((last.events & WeaponEvents.MeleeCharge) != 0 ? " (lunge anim)" : "")}");
            if (Armed && (last.events & WeaponEvents.MeleeHit) != 0)
            {
                bool fromCam = view.FirstPerson && lastCam != null;
                Mw2Melee.Hit(body, last.weapon, fromCam ? lastCam.transform.position : bank.aimOrigin, fromCam ? lastCam.transform.forward : bank.aimDirection, OnHit);
            }
            // _class.gsc: Scavenger Pro (specialty_extraammo) spawns with max ammo for the primary and
            // the secondary (not a launcher, weapon_projectile): filled the first time each is out.
            if (Armed && !maxedThisLife.Contains(last.weapon) && Mw2Perks.Has(body, "specialty_extraammo"))
            {
                maxedThisLife.Add(last.weapon);
                string wn = Native.WeaponString(last.weapon, 2);
                bool launcher = wn.StartsWith("at4") || wn.StartsWith("rpg") || wn.StartsWith("stinger") || wn.StartsWith("javelin") || wn.StartsWith("m79");
                bool classGun = ClassLoadout() is string[] cl && (wn == cl[0] || wn == cl[1]);
                if (classGun && !(launcher && wn != (ClassLoadout()?[0] ?? ""))) Native.mw2_add_reserve(sim, -1);
            }
            if (Armed && (Admin.InfiniteAmmo || string.Equals(Plugin.Instance.AmmoMode.Value, "Infinite", StringComparison.OrdinalIgnoreCase)))
                Native.mw2_add_reserve(sim, -1);
            else if (Armed && Plugin.Instance.RoR2Ammo.Value) TickRoR2Ammo(Time.deltaTime);

            if (last.shotsPending > 0)
            {
                uint n;
                fixed (Mw2Shot* p = shotBuffer) n = Native.mw2_take_shots(sim, p, (uint)shotBuffer.Length);
                FireShots(n, bank);
            }
        }

        // Each MW2 pellet becomes one RoR2 BulletAttack, so procs, crits and on-hit items run.
        void FireShots(uint count, InputBankTest bank)
        {
            if (count == 0 || !body.hasEffectiveAuthority || streaks.CallingIn) return;
            float reference = Mathf.Max(Plugin.Instance.DamageReference.Value, 1f);
            float damageMul = Mathf.Max(Plugin.Instance.DamageMultiplier.Value, 0f);
            if (Native.mw2_weapon_class(last.weapon) == 1) damageMul *= Mathf.Max(Plugin.Instance.SniperDamageScale.Value, 0f); // IW4 WEAPCLASS_SNIPER
            // RoR2's world is built ~2x MW2's; MW2's ranges scale with it (shotguns get extra).
            bool pellets = false;
            for (int k = 0; k < count; k++) pellets |= shotBuffer[k].pellet > 0; // a shotgun's pellets arrive together
            // RangeScale is the total vs MW2's ranges; WorldScale already stretches them.
            float rangeMul = Mathf.Max(Plugin.Instance.RangeScale.Value, 0.1f) / Mathf.Max(Plugin.Instance.WorldScale.Value, 0.1f) * (pellets ? Mathf.Max(Plugin.Instance.ShotgunRangeScale.Value, 0.1f) : 1f);
            bool crit = body.RollCrit();
            // First person: every round leaves along the camera's own centre ray (where the
            // crosshair / sights are this frame, recoil and sway included), turned by the spread
            // MW2 gave it relative to its view. Firing from MW2's eye point along MW2's angles
            // put ADS shots off the sights at close range.
            bool fromCam = view.FirstPerson && lastCam != null;
            var simFwd = Space.DirToUnity(ViewForward(last.viewangles));
            // MW2's bullet penetration: the weapon's type (small / medium / large) and multiplier (FMJ x2).
            if (Native.mw2_weapon_penetration(last.weapon, out int penType, out float penMul) == 0) penType = 0;
            if (Mw2Perks.Has(body, "specialty_bulletpenetration")) penMul *= 2f; // Deep Impact: perk_bulletPenetrationMultiplier (IW4 default 2, recalled - not read from the zones)
            for (int i = 0; i < count; i++)
            {
                var shot = shotBuffer[i];
                var shotDir = Space.DirToUnity(shot.dir);
                // Third person only: MW2's spread tightened (HipSpreadScale), aimed or not - no sights there,
                // the crosshair is all you aim with (playtest 10-06-26). First person keeps MW2's. Pellets keep
                // their pattern.
                var turn = Quaternion.FromToRotation(simFwd, shotDir);
                if (shot.pellet == 0 && ThirdOnly)
                    turn = Quaternion.SlerpUnclamped(Quaternion.identity, turn, Mathf.Max(Plugin.Instance.HipSpreadScale.Value, 0f));
                var aim = turn * (fromCam ? lastCam.transform.forward : simFwd);
                var from = fromCam ? lastCam.transform.position : bank.aimOrigin;
                var bodyHealth = body.healthComponent;
                // Third person: fire from RoR2's aim origin so rounds go to the crosshair,
                // in the direction MW2 chose (spread included). MW2's range falloff is
                // applied per hit; RoR2's own falloff is off so it isn't applied twice.
                var attack = new BulletAttack
                {
                    owner = body.gameObject,
                    weapon = body.gameObject,
                    origin = from,
                    aimVector = aim,
                    minSpread = 0f,
                    maxSpread = 0f,
                    bulletCount = 1,
                    damage = body.damage * (shot.damage / reference) * damageMul * Mw2Perks.BulletDamage(body),
                    modifyOutgoingDamageCallback = Mw2Perks.Has(body, "specialty_armorpiercing") ? Mw2PerkEffects.ArmorPiercing : (BulletAttack.ModifyOutgoingDamageCallback)null,
                    force = 100f,
                    falloffModel = BulletAttack.FalloffModel.None,
                    maxDistance = Mathf.Max(shot.maxRange * Space.Scale * rangeMul, 10f),
                    procCoefficient = 1f,
                    isCrit = crit,
                    radius = 0.1f,
                    smartCollision = true,
                    tracerEffectPrefab = Mw2Gunfire.Ready ? null : tracer,
                    hitEffectPrefab = Mw2Gunfire.Ready ? null : hitspark,
                    // Through bodies, as MW2's rounds go through players (playtest 10-06-26); the world
                    // still stops a segment (walls: the penetration loop below).
                    stopperMask = LayerIndex.world.mask,
                };
                float baseDamage = attack.damage, totalRange = attack.maxDistance;
                // Each enemy a round goes through costs its thickness / MW2's flesh depth for this
                // round, like a wall; one hit per enemy; out of damage, the round stops there.
                var struck = new HashSet<HealthComponent>();
                bool spent = false;
                float fleshDepth = penType > 0 ? Native.mw2_pen_depth(penType, (uint)Mw2Gunfire.FleshSurface) * penMul : 0f;
                var myTeam = body.teamComponent != null ? body.teamComponent.teamIndex : TeamIndex.None;
                Vector3 end = from + aim * attack.maxDistance;
                bool impacted = false, traced = false;
                // Per segment (one per wall the round goes through): damage left, distance before it,
                // and the world surface that stopped it.
                float penScale = 1f, travelled = 0f, wallDist = 0f;
                Collider wall = null;
                Vector3 wallPoint = default, wallNormal = default;
                attack.hitCallback = (BulletAttack ba, ref BulletAttack.BulletHit hit) =>
                {
                    bool enemy = hit.hitHurtBox != null && hit.hitHurtBox.healthComponent != null && hit.hitHurtBox.healthComponent != bodyHealth;
                    // Past an enemy that used the round up, or a second hurtbox of one already hit.
                    if (spent) return false;
                    if (enemy && !struck.Add(hit.hitHurtBox.healthComponent)) return false;
                    if (!impacted)
                    {
                        impacted = true;
                        if (!traced) { traced = true; end = hit.point; }
                        Mw2Gunfire.Impact(last.weapon, hit.point, hit.surfaceNormal, hit.collider, hit.hitHurtBox != null);
                    }
                    if (hit.hitHurtBox == null && wall == null) { wall = hit.collider; wallPoint = hit.point; wallNormal = hit.surfaceNormal; wallDist = hit.distance; }
                    ba.damage = baseDamage * penScale * Falloff(shot, (travelled + hit.distance) / Space.Scale / rangeMul);
                    bool result = BulletAttack.defaultHitCallback(ba, ref hit);
                    ba.damage = baseDamage;
                    if (enemy) OnHit();
                    // The victim body's team (a summoned monster's HurtBox.teamIndex can lag behind it).
                    var vb = enemy ? hit.hitHurtBox.healthComponent.body : null;
                    var vTeam = vb != null && vb.teamComponent != null ? vb.teamComponent.teamIndex : hit.hitHurtBox != null ? hit.hitHurtBox.teamIndex : TeamIndex.None;
                    if (enemy && Mw2Pilot.Active && struck.Count == 1) Plugin.Log.LogInfo($"[pen] first body {hit.hitHurtBox.healthComponent.name}: its team {vTeam}, hurtbox {hit.hitHurtBox.teamIndex}, mine {myTeam}");
                    if (enemy && vTeam != myTeam)
                    {
                        // Its width across the shot, in MW2 units (a Lemurian ~ a soldier; a golem is a wall).
                        var b = hit.collider != null ? hit.collider.bounds : new Bounds(hit.point, Vector3.one * 0.5f);
                        float thick = Mathf.Clamp(Mathf.Min(b.size.x, b.size.z) / Space.Scale, 8f, 200f);
                        float was = penScale;
                        penScale = fleshDepth > 0f ? penScale - thick / fleshDepth : 0f;
                        if (penScale <= 0f) { spent = true; if (!traced) { traced = true; end = hit.point; } }
                        if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[pen] through {hit.hitHurtBox.healthComponent.name}: {thick:F0}u of {fleshDepth:F0}u, x{was:F2} -> x{Mathf.Max(penScale, 0f):F2}");
                    }
                    if (enemy && travelled > 0f && Mw2Pilot.Active) Plugin.Log.LogInfo($"[pen] hit {hit.hitHurtBox.healthComponent.name} through the wall at x{penScale:F2}");
                    return result;
                };
                Mw2Challenges.With(Mw2Challenges.Cause.Bullet, attack.Fire);
                // IW4's FireBulletPenetrate: up to 5 walls. Each costs thickness / depth of the
                // damage left, depth being the shallower of the entry and exit surfaces' table
                // depths for this round (x the weapon's multiplier); grazing hits don't go in.
                for (int step = 0; step < 5 && penType > 0 && wall != null && !spent && penScale > 0f; step++)
                {
                    float dot = -Vector3.Dot(wallNormal, aim);
                    if (dot < 0.125f) break;
                    float depth = Native.mw2_pen_depth(penType, (uint)Mw2Gunfire.SurfaceIndex(wall)) * penMul;
                    if (depth <= 0f) break;
                    Vector3 entry = wallPoint;
                    float left = totalRange - travelled - wallDist;
                    if (left <= 0f) break;
                    // The next surface ahead, then back from it to where the round left this one.
                    Vector3 inside = entry + aim * (0.135f / dot * Space.Scale);
                    Vector3 far = Physics.Raycast(inside, aim, out var ahead, left, LayerIndex.world.mask, QueryTriggerInteraction.Ignore) ? ahead.point : entry + aim * left;
                    Vector3 back = far - aim * (0.01f * Space.Scale);
                    if (!Physics.Raycast(back, -aim, out var exit, Vector3.Distance(back, entry), LayerIndex.world.mask, QueryTriggerInteraction.Ignore)) break;
                    depth = Mathf.Min(depth, Native.mw2_pen_depth(penType, (uint)Mw2Gunfire.SurfaceIndex(exit.collider)) * penMul);
                    if (depth <= 0f) break;
                    float thickness = Mathf.Max(Vector3.Distance(exit.point, entry) / Space.Scale, 1f);
                    float was = penScale;
                    penScale -= thickness / depth;
                    if (Mw2Pilot.Active) Plugin.Log.LogInfo($"[pen] step {step}: {Mw2Gunfire.SurfaceName(Mw2Gunfire.SurfaceIndex(wall))} {thickness:F1}u of {depth:F1}u, damage x{was:F2} -> x{Mathf.Max(penScale, 0f):F2}");
                    if (penScale <= 0f) break;
                    Mw2Gunfire.Impact(last.weapon, exit.point, exit.normal, exit.collider, false);
                    travelled += wallDist + Vector3.Distance(entry, exit.point);
                    wall = null;
                    impacted = false;
                    attack.origin = exit.point + aim * 0.01f;
                    attack.maxDistance = totalRange - travelled;
                    if (attack.maxDistance <= 0.05f) break;
                    Mw2Challenges.With(Mw2Challenges.Cause.Bullet, attack.Fire);
                }
                // MW2's flash / eject / tracer from the gun's own tags (first person), else the eye.
                Vector3 muzzle = from, brass = from;
                var shotVm = shot.hand == 1 && viewmodelLeft.Exists ? viewmodelLeft : viewmodel;
                if (fromCam && shotVm.Exists)
                {
                    if (!shotVm.Tag("tag_flash", out muzzle)) muzzle = from + lastCam.transform.forward * 0.8f;
                    if (!shotVm.Tag("tag_brass", out brass)) brass = muzzle;
                    muzzle = Mw2View.OntoWorld(lastCam.transform, muzzle);
                    brass = Mw2View.OntoWorld(lastCam.transform, brass);
                }
                Mw2Gunfire.Shot(last.weapon, muzzle, brass, aim, end, i == 0, last.clip == 0, fromCam ? lastCam.transform : null);
            }
        }

        float lastHitSound;

        void OnHit()
        {
            Mw2Hud.Hitmarker();
            // One tick per burst of pellets, like MW2.
            if (Time.unscaledTime - lastHitSound > 0.05f)
            {
                lastHitSound = Time.unscaledTime;
                audio.PlayAlias("MP_hit_alert");
            }
        }

        /// MW2 bullet_damage_at_distance as a multiplier on the shot's base damage.
        /// IW4 angles (pitch + down, yaw + left) -> forward vector, IW4 axes.
        static Vec3f ViewForward(Vec3f angles)
        {
            float p = angles.x * Mathf.Deg2Rad, y = angles.y * Mathf.Deg2Rad;
            return new Vec3f(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), -Mathf.Sin(p));
        }

        static float Falloff(in Mw2Shot shot, float distUnits)
        {
            if (shot.damage <= 0f) return 1f;
            float min = shot.minDamage > 0f ? shot.minDamage : shot.damage;
            float range = shot.minDamageRange - shot.maxDamageRange;
            float dmg = shot.damage;
            if (dmg != min && range != 0f && distUnits >= shot.maxDamageRange)
                dmg = distUnits >= shot.minDamageRange ? min : Mathf.Lerp(shot.damage, min, Mathf.Clamp01((distUnits - shot.maxDamageRange) / range));
            return Mathf.Max(dmg, 0f) / shot.damage;
        }

        float stuckFor;
        readonly HashSet<uint> maxedThisLife = new HashSet<uint>(); // Scavenger Pro

        /// MW2's player box is 30x30x70 units; on slopes or beside props the survivor's
        /// feet can put it inside geometry, where MW2 movement treats the player as stuck.
        /// Lift until the box is clear (up to 3 m).
        static Vector3 ClearStart(Vector3 feet)
        {
            var half = new Vector3(15f, 35f, 15f) * Space.Scale;
            for (float up = 0.05f; up < 3f; up += 0.1f)
            {
                var p = feet + Vector3.up * up;
                if (!Physics.CheckBox(p + Vector3.up * half.y, half, Quaternion.identity, UnityWorld.Mask, QueryTriggerInteraction.Ignore))
                    return p;
            }
            return feet + Vector3.up * 0.05f;
        }

        /// If MW2 reports airborne with no motion for a while, the box is wedged in
        /// geometry; re-seat it at the nearest clear spot above.
        void Unstick(float dt)
        {
            float speed = Mathf.Abs(last.velocity.x) + Mathf.Abs(last.velocity.y) + Mathf.Abs(last.velocity.z);
            stuckFor = last.grounded == 0 && speed < 0.5f ? stuckFor + dt : 0f;
            if (stuckFor < 0.3f) return;
            stuckFor = 0f;
            var feetNow = Space.ToUnity(last.origin);
            var origin = Space.ToIw(ClearStart(feetNow + Vector3.up * 0.1f));
            Native.mw2_set_origin(sim, ref origin);
            Plugin.Log.LogInfo("MW2 movement: player box was wedged in geometry; lifted clear.");
        }

        // On the ground, pushing to move, going nowhere (playtest 10-04-26: still stuck in spots; none of
        // the other checks fired). Walking into a wall looks the same, so the box is only re-seated
        // when it actually overlaps the world; either way the spot is logged to reproduce it.
        float pushStillFor, lastStuckLog = -10f;
        Vector3 pushDir;
        Vec3f pushFrom;
        /// Times the player pushed a whole second without moving (the pilot's wander turns on it).
        public int StuckCount { get; private set; }
        /// Times RoR2 put the body somewhere else (fell out of the map, teleporter).
        public int MovedCount { get; private set; }
        /// Times the sim and the body drifted apart and were put back together (pilot).
        public int PartedCount { get; private set; }

        /// How high what's in front blocks, in MW2 units over the feet (the lowest clear height
        /// a box-wide probe finds, 0 = nothing in the way, -1 = blocked up past 80). MW2 steps up 18.
        float BlockedHeight(Vector3 feet, Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.01f) return 0f;
            float reach = 24f * Space.Scale;
            bool any = false;
            for (int h = 2; h <= 80; h += 2)
            {
                bool hit = Physics.Raycast(feet + Vector3.up * (h * Space.Scale), dir, reach, UnityWorld.Mask, QueryTriggerInteraction.Ignore);
                if (hit) { any = true; continue; }
                return any ? h : 0f;
            }
            return -1f;
        }

        void UnstickGrounded(float dt, bool pushing, Vec3f wasAt)
        {
            // Airborne counts too: on a slope too steep to stand on (a V between two) MW2 has no
            // ground, so no jump either, and the player sits there wedged (pilot 10-04-26, village).
            // Net travel over 2 s of pushing, not path length: sliding back and forth on a slope piles
            // up distance while going nowhere.
            if (!pushing) { pushStillFor = 0f; stuckStreak = 0; return; }
            if (pushStillFor == 0f) pushFrom = wasAt;
            pushStillFor += dt;
            // 1 s (was 2: playtest 10-06-26 still found himself stuck on things he shouldn't be).
            if (pushStillFor < 1f) return;
            float dx = last.origin.x - pushFrom.x, dy = last.origin.y - pushFrom.y, dz = last.origin.z - pushFrom.z;
            bool still = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) < 8f; // MW2 units in 1 s of pushing (walking covers ~190)
            pushStillFor = 0f;
            if (!still) { stuckStreak = 0; return; }
            StuckCount++;
            stuckStreak++;
            var feet = Space.ToUnity(last.origin);
            var half = new Vector3(14f, 34f, 14f) * Space.Scale; // the 30x30x70 box, a unit in from each side
            bool inside = Physics.CheckBox(feet + Vector3.up * (half.y + Space.Scale), half, Quaternion.identity, UnityWorld.Mask, QueryTriggerInteraction.Ignore);
            float blocked = BlockedHeight(feet, pushDir);
            // Pinned below something a jump clears, or wedged with no ground to jump from (RoR2's
            // steep little bowls - MW2 calls 45+ degrees unwalkable, RoR2's terrain doesn't): the hop
            // a player would jump, the way they push. A wall (taller than a jump) stays a wall.
            bool hop = !inside && blocked >= 0f && blocked <= JumpHeight && stuckStreak == 1;
            // Still there 2 s after the hop: RoR2's own ground node a few metres the way he pushes
            // (where RoR2 itself puts players and monsters) - never stuck for good.
            bool nodeOut = !inside && stuckStreak >= 2 && blocked >= 0f && NodeOut(feet, pushDir, out nodeAt);
            if (Time.time - lastStuckLog > 5f)
            {
                lastStuckLog = Time.time;
                bool down = Physics.Raycast(feet + Vector3.up * 0.5f, Vector3.down, out var g, 2f, UnityWorld.Mask, QueryTriggerInteraction.Ignore);
                string line = $"pushing {pushDir.x:F2},{pushDir.z:F2} (yaw {Mathf.Atan2(pushDir.x, pushDir.z) * Mathf.Rad2Deg:F0}) but not moving at {feet}, blocked {(blocked < 0f ? "above 80" : blocked.ToString("F0"))} units ahead{(blocked > 0f && blocked <= 18f ? " (under MW2's 18-unit step: a snag)" : "")}, {(last.grounded != 0 ? "grounded" : "airborne: wedged on steep slopes")}, on {(down ? g.collider.name + " normal " + g.normal : "no ground below")} (stage {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}){(inside ? "; box overlaps the world, lifted clear" : hop ? "; hopped out" : nodeOut ? $"; moved to RoR2's ground node at {nodeAt}" : "; box clear (a wall?)")}";
                Plugin.Log.LogInfo($"MW2 movement: {line}");
                StuckLog(line, true);
            }
            if (hop)
            {
                var flat = Space.DirToIw(pushDir);
                // jump_height 39 under g 800: sqrt(2 * 800 * 39) = 250 units/s up; walk speed along.
                pendingLaunch = new Vec3f(flat.x * 190f, flat.y * 190f, Mathf.Sqrt(2f * 800f * JumpHeight));
                launchTicks = 2;
                HopCount++;
            }
            if (nodeOut)
            {
                // The body goes (RoR2's teleport); the sim re-seats on it ("RoR2 moved the body").
                if (body != null) TeleportHelper.TeleportBody(body, nodeAt + Vector3.up * 0.1f);
                var still0 = new Vec3f(0f, 0f, 0f);
                Native.mw2_set_velocity(sim, ref still0);
                stuckStreak = 0;
                NodeOutCount++;
            }
            if (!inside) return;
            var origin = Space.ToIw(ClearStart(feet + Vector3.up * 0.1f));
            Native.mw2_set_origin(sim, ref origin);
        }
        int stuckStreak;
        Vector3 nodeAt;
        /// Times the stuck escape moved him to a ground node (pilot).
        public int NodeOutCount { get; private set; }

        /// The nearest RoR2 ground node 1.5-8 m away, ahead of `dir` if it has one (any side else).
        static bool NodeOut(Vector3 feet, Vector3 dir, out Vector3 at)
        {
            at = feet;
            var g = SceneInfo.instance != null ? SceneInfo.instance.groundNodes : null;
            int count = g != null ? g.GetNodeCount() : 0;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!g.GetNodePosition(new RoR2.Navigation.NodeGraph.NodeIndex(i), out var p)) continue;
                var d = p - feet;
                float m = d.magnitude;
                if (m < 1.5f || m > 8f || Mathf.Abs(d.y) > 3f) continue;
                // Ahead counts as nearer: prefer the way he was trying to go.
                float score = m - (dir.sqrMagnitude > 0f ? Vector3.Dot(Vector3.ProjectOnPlane(d, Vector3.up).normalized, dir) * 3f : 0f);
                if (score < best) { best = score; at = p; }
            }
            return best < float.MaxValue;
        }
        const float JumpHeight = 39f; // the sim's jump_height
        /// Times the stuck hop fired (pilot).
        public int HopCount { get; private set; }

        public Vector3 MotorVelocity(Vector3 motorPosition, float dt)
        {
            if (dt <= 0f) return Vector3.zero;
            var gap = target - motorPosition;
            Vector3 v = gap / dt;
            // A big gap means RoR2 moved us (teleporter, knockback); re-seat the sim.
            if (gap.sqrMagnitude > 9f && sim != IntPtr.Zero)
            {
                Reseat(motorPosition, "RoR2 moved the body");
                return Vector3.zero;
            }
            // The sim and the body parted (the sim's box slipped into terrain the body can't
            // follow it into): the body is where the world let it be, so the sim goes back there.
            // playtest 10-03-26: stuck in the ground, taking damage.
            // The body runs a tick behind the sim, so fast moves gap that far on their own: a 55 m/s
            // launch pad is ~0.9 m a tick, which read as parted and cancelled the launch every time
            // (playtest 10-04-26: "this one jump pad isn't working").
            float simSpeed = Space.DirToUnity(last.velocity).magnitude * Space.Scale;
            float allowed = 0.75f + simSpeed * Mathf.Max(dt, Time.deltaTime) * 1.5f;
            divergedFor = gap.sqrMagnitude > allowed * allowed ? divergedFor + dt : 0f;
            if (divergedFor > 0.2f && sim != IntPtr.Zero)
            {
                divergedFor = 0f;
                Reseat(motorPosition, $"the sim and the body parted by {gap.magnitude:F2} m");
                return Vector3.zero;
            }
            // Never pulled down faster than the sim itself falls: a catch-up yank read to RoR2 as a
            // hard landing (fall damage, every frame while stuck).
            float simDown = Mathf.Max(0f, -Space.DirToUnity(last.velocity).y * Space.Scale);
            float maxDown = simDown * 1.25f + 3f;
            if (v.y < -maxDown) v.y = -maxDown;
            return v;
        }

        float divergedFor;


        /// Pilot: push the sim's box `metres` into the ground under the body (the stuck-in-terrain bug).
        internal void TestSink(float metres)
        {
            if (sim == IntPtr.Zero || body == null) return;
            var origin = Space.ToIw(body.footPosition - Vector3.up * metres);
            Native.mw2_set_origin(sim, ref origin);
        }

        void Reseat(Vector3 motorPosition, string why)
        {
            var origin = Space.ToIw(ClearStart(motorPosition - footToTransform));
            Native.mw2_set_origin(sim, ref origin);
            target = motorPosition;
            Plugin.Log.LogInfo($"MW2 movement: re-seated the sim on the body ({why}).");
            if (why == "RoR2 moved the body") MovedCount++;
            else if (why.StartsWith("the sim and the body parted")) PartedCount++;
            if (why.StartsWith("the sim and the body parted")) StuckLog($"{why} at {motorPosition} (stage {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name})", false);
        }

        // Stuck spots for later (playtest 10-04-26: "we still get stuck in spots"; BepInEx keeps only
        // the last few logs): one line each in BepInEx/mw2_stuck.log, with a screenshot for the
        // first few of a session, so a playtest - his or a friend's - leaves the spots behind.
        static int stuckShots;
        static void StuckLog(string line, bool shot)
        {
            try
            {
                string dir = BepInEx.Paths.BepInExRootPath;
                string stamp = DateTime.Now.ToString("MM-dd-yy HH:mm:ss");
                string png = null;
                if (shot && stuckShots < 12)
                {
                    stuckShots++;
                    png = System.IO.Path.Combine(dir, "mw2_stuck", $"stuck {DateTime.Now:MM-dd-yy HH-mm-ss}.png");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(png));
                    ScreenCapture.CaptureScreenshot(png);
                }
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "mw2_stuck.log"), $"{stamp}  {line}{(png != null ? "  [" + System.IO.Path.GetFileName(png) + "]" : "")}{Environment.NewLine}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"MW2: stuck log failed: {e.Message}"); }
        }

        static string StateName(int s)
        {
            switch (s)
            {
                case 0: return "ready";
                case 1: case 2: return "raising";
                case 3: case 4: case 5: return "dropping";
                case 6: return "FIRING";
                case 7: return "rechamber";
                case 8: case 9: case 10: case 11: return "RELOADING";
                default: return s.ToString();
            }
        }

        public string Hud()
        {
            if (!Active) return "MW2 mode: OFF (F6)";
            float hs = Mathf.Sqrt(last.velocity.x * last.velocity.x + last.velocity.y * last.velocity.y);
            string gun = Armed ? $"   {weaponName} {last.clip}/{last.stock} {StateName(last.weaponstate)}{(last.adsFrac > 0.5f ? " ADS" : "")} (F8)" : "   no MW2 weapons loaded";
            return $"MW2 mode: ON (F6)   {hs:F0} u/s {(last.grounded != 0 ? "ground" : "air")}{(last.sprinting != 0 ? " SPRINT" : "")}{gun}   enemies x{EnemySpeedScale():F2}";
        }
    }
}
