using System;
using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Mw2CharacterInput
    {
        public float dt;
        public byte stance, sprinting, inAir, dead; // stance 0 stand, 1 crouch, 2 prone
        public float moveFwd, moveRight;            // inches / s in the character's own frame
        public float aimPitch;                      // degrees, + = down
        public float adsFrac;
        public uint weapon;
        public uint events;                         // Mw2Character.Ev* one-shots
        public uint primary;                        // the gun in hand (0 = weapon); a change is a weapon switch
        public uint hit;                            // last hit for pain / death (Mw2Character.Hit), 0 = none
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Mw2CharacterInfo
    {
        public uint boneCount, vertexCount, indexCount, surfaceCount;
    }

    /// MW2's third-person soldier (roadmap #8): the faction's MP body + head from the user's MW2
    /// map zones, animated by MW2's own playeranim.script in mw2sim, holding the weapon's world
    /// model on tag_weapon_right. Stands in for the RoR2 survivor's model.
    unsafe class Mw2Character
    {
        public const uint EvFire = 1, EvReload = 2, EvMelee = 4, EvThrow = 8, EvJump = 16, EvLand = 32, EvPain = 64, EvMantle = 128, EvMeleeCharge = 1u << 12, EvReloadEmpty = 1u << 13, EvReloadFast = 1u << 14;

        /// Mw2CharacterInput.hit: playeranim's damage type (0 bullet, 1 light explosion, 2 explosion),
        /// hit location (0 torso, 1 head, 2 neck, 3 legs) and hit direction (0 front, 1 left, 2 right,
        /// 3 back), packed for the native side.
        public static uint Hit(int type, int location, int direction) =>
            0x8000_0000u | (uint)(type & 0xff) | ((uint)(location & 0xff) << 8) | ((uint)(direction & 0xff) << 16);

        /// The hit RoR2 reported on `victim` (facing `facing`) as playeranim's pain / death conditions:
        /// splash damage is an explosion (heavy past a quarter of his health), the height of the hit
        /// picks head / neck / legs / torso, the attacker's side picks the direction.
        public static uint HitFrom(CharacterBody victim, Vector3 facing, DamageDealtMessage msg)
        {
            float full = victim.healthComponent != null ? Mathf.Max(victim.healthComponent.fullCombinedHealth, 1f) : 100f;
            bool aoe = (msg.damageType & DamageType.AOE) != 0;
            int type = aoe ? (msg.damage >= full * 0.25f ? 2 : 1) : 0;
            float h = Mathf.Max((victim.corePosition.y - victim.footPosition.y) * 2f, 0.5f);
            float up = (msg.position.y - victim.footPosition.y) / h;
            int loc = aoe ? 0 : up > 0.9f ? 1 : up > 0.8f ? 2 : up < 0.45f ? 3 : 0;
            var from = msg.attacker != null ? msg.attacker.transform.position : msg.position;
            var to = Vector3.ProjectOnPlane(from - victim.footPosition, Vector3.up);
            var fwd = Vector3.ProjectOnPlane(facing, Vector3.up);
            float ang = to.sqrMagnitude > 1e-4f && fwd.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(fwd, to, Vector3.up) : 0f;
            int dir = Mathf.Abs(ang) <= 45f ? 0 : Mathf.Abs(ang) >= 135f ? 3 : ang < 0f ? 1 : 2;
            return Hit(type, loc, dir);
        }

        IntPtr ch;
        GameObject root, gunGo, gunLeftGo; // gunLeftGo: akimbo's second gun (tag_weapon_left)
        readonly Dictionary<uint, GameObject> animModels = new Dictionary<uint, GameObject>(); // offhands / laptops in hand
        GameObject stowGo; uint stowFor; // a riot shield on his back (tag_shield_back)
        bool gunOnLeft; // the riot shield is held on the left arm
        /// Weapon to show stowed on tag_shield_back (MW2's riot shield when it isn't in hand), 0 = none.
        public uint StowedShield;
        /// The item the body animates with when it isn't the gun in hand (the offhand being thrown -
        /// grenade, flare, knife, claymore - or a killstreak laptop during a ride); 0 = the gun. The
        /// gun model stays: rebuilding it on every throw would hitch.
        public uint AnimWeapon;
        /// A teammate's camo for the held gun (mp/camoTable id, 255 = none); 0 = your own camo setting.
        public uint Camo;
        uint gunCamo;
        Transform[] bones = new Transform[0];
        float[] pose = new float[0];
        uint gunFor;
        readonly float[] tag = new float[7];
        public bool Exists => ch != IntPtr.Zero && root != null;

        /// This character's own first-person arms (Spetsnaz sleeves, Militia arms...) for viewmodels
        /// built from now on; false = MW2's base (Ranger) arms.
        public bool UseViewhands() => ch != IntPtr.Zero && Native.mw2_character_use_viewhands(ch) == 1;
        public Transform Root => root != null ? root.transform : null;
        SkinnedMeshRenderer skin;
        /// The body's skin (bones + bind poses), for RoR2's item displays (Mw2ItemDisplays).
        public SkinnedMeshRenderer Skin => Exists ? skin : null;

        CharacterBody owner;

        /// `key`: a faction (`us_army`), or a skin's `faction|class|variant|head` (Mw2Skins).
        // Textures by MW2 material: skins share bodies / heads, so a skin swap (character select)
        // decodes only what it hasn't seen. They were also never freed per build (a leak per swap).
        static readonly Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();

        public bool Build(CharacterBody body, string key)
        {
            Destroy();
            owner = body;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long tNative = 0, tMesh = 0, tTex = 0;
            int decoded = 0;
            Mw2Skins.Parse(key, out var faction, out var cls, out int variant, out int head);
            var b = System.Text.Encoding.UTF8.GetBytes(faction);
            var cb = System.Text.Encoding.UTF8.GetBytes(cls);
            fixed (byte* p = b) fixed (byte* pc = cb) ch = Native.mw2_character_build_ex(p, (UIntPtr)b.Length, pc, (UIntPtr)cb.Length, (uint)Math.Max(variant, 0), (uint)Math.Max(head, 0));
            if (ch == IntPtr.Zero) { Plugin.Log.LogWarning($"MW2 character '{faction}' could not be built."); return false; }
            tNative = clock.ElapsedMilliseconds;
            if (Native.mw2_character_info(ch, out var info) != 1) { Destroy(); return false; }
            int n = (int)info.vertexCount, nb = (int)info.boneCount;
            var pos = new float[n * 3]; var nor = new float[n * 3]; var uv = new float[n * 2];
            var bi = new int[n * 4]; var bw = new float[n * 4]; var idx = new uint[info.indexCount]; var bp = new float[nb * 16];
            int ok;
            fixed (float* a = pos) fixed (float* c = nor) fixed (float* t = uv) fixed (int* i = bi) fixed (float* w = bw) fixed (uint* x = idx) fixed (float* m = bp)
                ok = Native.mw2_character_mesh(ch, a, c, t, i, w, x, m);
            if (ok != 1) { Destroy(); return false; }

            var mesh = new Mesh { name = $"mw2_character_{faction}", indexFormat = IndexFormat.UInt32 };
            var verts = new Vector3[n]; var norms = new Vector3[n]; var uvs = new Vector2[n]; var weights = new BoneWeight[n];
            var white = new Color32[n];
            for (int v = 0; v < n; v++)
            {
                verts[v] = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                norms[v] = new Vector3(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]);
                uvs[v] = new Vector2(uv[v * 2], uv[v * 2 + 1]);
                weights[v] = new BoneWeight
                {
                    boneIndex0 = bi[v * 4], weight0 = bw[v * 4], boneIndex1 = bi[v * 4 + 1], weight1 = bw[v * 4 + 1],
                    boneIndex2 = bi[v * 4 + 2], weight2 = bw[v * 4 + 2], boneIndex3 = bi[v * 4 + 3], weight3 = bw[v * 4 + 3],
                };
                white[v] = new Color32(255, 255, 255, 255);
            }
            var bind = new Matrix4x4[nb];
            for (int k = 0; k < nb; k++)
            {
                var mm = new Matrix4x4();
                for (int e = 0; e < 16; e++) mm[e] = bp[k * 16 + e]; // column-major both sides
                bind[k] = mm;
            }
            mesh.vertices = verts; mesh.normals = norms; mesh.uv = uvs; mesh.boneWeights = weights; mesh.bindposes = bind;
            mesh.colors32 = white;

            tMesh = clock.ElapsedMilliseconds;
            var template = Mw2Gun.TemplateMaterial(body, out _);
            var mats = new List<Material>();
            mesh.subMeshCount = (int)info.surfaceCount;
            for (uint s = 0; s < info.surfaceCount; s++)
            {
                if (Native.mw2_character_surface(ch, s, out var si) != 1) { mats.Add(Mw2Gun.SurfaceMaterial(template, null)); continue; }
                var tris = new int[si.indexCount];
                for (int k = 0; k < si.indexCount; k++) tris[k] = (int)idx[si.indexStart + k];
                mesh.SetTriangles(tris, (int)s);
                int blend;
                var nameBuf = new byte[96];
                fixed (byte* nm = nameBuf) blend = Native.mw2_character_surface_material(ch, s, nm, 96);
                int nameLen = Array.IndexOf(nameBuf, (byte)0);
                string matName = System.Text.Encoding.UTF8.GetString(nameBuf, 0, nameLen < 0 ? nameBuf.Length : nameLen);
                if (matName.Length == 0 || !textureCache.TryGetValue(matName, out var tex) || tex == null)
                {
                    uint ss = s;
                    tex = Rgba((w, h, o, cap) => Native.mw2_character_surface_rgba(ch, ss, w, h, o, cap));
                    decoded++;
                    if (matName.Length > 0 && tex != null) textureCache[matName] = tex;
                }
                Material see = blend >= 2 && tex != null ? Mw2Fx.SurfaceMaterial(blend, tex) : null;
                mats.Add(see ?? Mw2Gun.SurfaceMaterial(template, tex));
            }
            mesh.RecalculateBounds();
            tTex = clock.ElapsedMilliseconds;

            root = new GameObject($"MW2 Character {faction}");
            bones = new Transform[nb];
            var boneBuf = stackalloc byte[64];
            for (int k = 0; k < nb; k++)
            {
                // MW2's own bone names (j_head...): the death cam and RoR2's item displays find them.
                int len = Native.mw2_character_bone_name(ch, (uint)k, boneBuf, 64);
                string boneName = len > 0 ? System.Text.Encoding.UTF8.GetString(boneBuf, Math.Min(len, 64)) : $"bone{k}";
                var g = new GameObject(boneName);
                g.transform.SetParent(root.transform, false);
                bones[k] = g.transform;
            }
            var meshGo = new GameObject("MW2 Character Mesh");
            meshGo.transform.SetParent(root.transform, false);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = root.transform;
            skin = smr;
            smr.sharedMaterials = mats.ToArray();
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = ShadowCastingMode.On;
            pose = new float[nb * 7];
            Plugin.Log.LogInfo($"MW2 character {faction}: {nb} bones, {n} verts, {info.indexCount / 3} tris, {info.surfaceCount} surfaces; " +
                $"built in {clock.ElapsedMilliseconds} ms (native {tNative}, mesh {tMesh - tNative}, textures {tTex - tMesh}: {decoded} decoded, {info.surfaceCount - decoded} cached)");
            return true;
        }

        delegate uint RgbaFn(uint* w, uint* h, byte* o, uint cap);

        static Texture2D Rgba(RgbaFn fn)
        {
            uint w, h, n = fn(&w, &h, null, 0);
            if (n == 0 || n != w * h * 4) return null;
            var px = new byte[n];
            fixed (byte* o = px) fn(&w, &h, o, n);
            Mw2Gun.TopRowFirst(px, (int)w, (int)h);
            var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            tex.SetPixelData(px, 0);
            tex.Apply(true, true);
            return tex;
        }

        /// Pose for this frame: root on the feet facing `yawDeg`, MW2 anims from the input, the
        /// held gun's world model on tag_weapon_right.
        public void Step(Vector3 feet, float yawDeg, ref Mw2CharacterInput input, bool visible)
        {
            if (!Exists) return;
            root.SetActive(visible);
            root.transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, yawDeg, 0f));
            // Pose is in metres of MW2 inches. The character-select soldier keeps his own size: a run
            // sets Space.Scale from the body (x WorldScale), and back in the menu he came out huge
            // (playtest 10-06-26).
            root.transform.localScale = Vector3.one * (onPad ? 1f : Space.Scale / 0.0254f) * scaleMul;
            int ok;
            var animInput = input;
            animInput.primary = input.primary != 0 ? input.primary : input.weapon;
            if (AnimWeapon != 0) animInput.weapon = AnimWeapon; // MW2's playeranim picks the throw / laptop set from it
            fixed (float* p = pose) ok = Native.mw2_character_step(ch, &animInput, p);
            if (ok != 1) return;
            for (int k = 0; k < bones.Length; k++)
            {
                int o = k * 7;
                bones[k].localPosition = new Vector3(pose[o], pose[o + 1], pose[o + 2]);
                bones[k].localRotation = new Quaternion(pose[o + 3], pose[o + 4], pose[o + 5], pose[o + 6]);
            }
            // Rebuilt when the gun or its camo changes (your class's camo, or a teammate's).
            uint camo = Camo != 0 ? Camo : (Native.CamoIds.TryGetValue(input.weapon, out uint own) ? own : 255u);
            if (input.weapon != gunFor || camo != gunCamo) { gunCamo = camo; BuildGun(input.weapon); }
            // The offhand / laptop in use is what's in the hand (IW4 swaps the world model to the
            // offhand weapon for the throw); the gun is put away meanwhile. Built once, kept.
            GameObject inHand = null;
            bool offhand = AnimWeapon != 0 && AnimWeapon != input.weapon;
            if (offhand && !animModels.TryGetValue(AnimWeapon, out inHand))
                animModels[AnimWeapon] = inHand = WorldModel(AnimWeapon, $"MW2 World Offhand {AnimWeapon}"); // null: no world model (the flare)
            foreach (var kv in animModels) if (kv.Value != null && kv.Value != inHand) kv.Value.SetActive(false);
            if (offhand)
            {
                if (gunGo != null) gunGo.SetActive(false);
                if (gunLeftGo != null) gunLeftGo.SetActive(false);
                if (inHand != null)
                {
                    fixed (float* t = tag) ok = Native.mw2_character_weapon_tag(ch, t);
                    inHand.SetActive(ok == 1);
                    if (ok == 1)
                    {
                        inHand.transform.localPosition = new Vector3(tag[0], tag[1], tag[2]);
                        inHand.transform.localRotation = new Quaternion(tag[3], tag[4], tag[5], tag[6]);
                    }
                }
                StepStowed(input.weapon);
                return;
            }
            if (gunGo != null)
            {
                if (gunOnLeft) fixed (float* t = tag) fixed (byte* n = TagWeaponLeft) ok = Native.mw2_character_tag(ch, n, (UIntPtr)TagWeaponLeft.Length, t);
                else fixed (float* t = tag) ok = Native.mw2_character_weapon_tag(ch, t);
                gunGo.SetActive(ok == 1);
                if (ok == 1)
                {
                    gunGo.transform.localPosition = new Vector3(tag[0], tag[1], tag[2]);
                    gunGo.transform.localRotation = new Quaternion(tag[3], tag[4], tag[5], tag[6]);
                }
            }
            if (gunLeftGo != null)
            {
                fixed (float* t = tag) fixed (byte* n = TagWeaponLeft) ok = Native.mw2_character_tag(ch, n, (UIntPtr)TagWeaponLeft.Length, t);
                gunLeftGo.SetActive(ok == 1);
                if (ok == 1)
                {
                    gunLeftGo.transform.localPosition = new Vector3(tag[0], tag[1], tag[2]);
                    gunLeftGo.transform.localRotation = new Quaternion(tag[3], tag[4], tag[5], tag[6]);
                }
            }
            StepStowed(input.weapon);
        }

        static readonly byte[] TagWeaponLeft = System.Text.Encoding.ASCII.GetBytes("tag_weapon_left");
        static readonly byte[] TagShieldBack = System.Text.Encoding.ASCII.GetBytes("tag_shield_back");

        /// MW2's riot shield on his back while another weapon is out (`_class.gsc` trackRiotShield).
        void StepStowed(uint held)
        {
            uint want = StowedShield != held ? StowedShield : 0u;
            if (want != stowFor)
            {
                stowFor = want;
                if (stowGo != null) UnityEngine.Object.Destroy(stowGo);
                stowGo = want != 0 ? WorldModel(want, $"MW2 Stowed {want}") : null;
            }
            if (stowGo == null) return;
            int ok;
            fixed (float* t = tag) fixed (byte* n = TagShieldBack) ok = Native.mw2_character_tag(ch, n, (UIntPtr)TagShieldBack.Length, t);
            stowGo.SetActive(ok == 1);
            if (ok != 1) return;
            stowGo.transform.localPosition = new Vector3(tag[0], tag[1], tag[2]);
            stowGo.transform.localRotation = new Quaternion(tag[3], tag[4], tag[5], tag[6]);
        }

        void BuildGun(uint weapon)
        {
            gunFor = weapon;
            if (gunGo != null) UnityEngine.Object.Destroy(gunGo);
            if (gunLeftGo != null) UnityEngine.Object.Destroy(gunLeftGo);
            gunGo = gunLeftGo = null;
            // MW2's riot shield is held on the left arm (tag_weapon_left), not in the gun hand.
            gunOnLeft = Native.mw2_weapon_is_shield(weapon) == 1;
            // The camo rides on the weapon id (bits 16-23) for the native world-gun calls.
            uint packed = weapon != 0 && Camo != 0 ? weapon | (Camo << 16) : weapon;
            gunGo = WorldModel(packed, $"MW2 World Gun {weapon}");
            if (weapon != 0)
            {
                var nb = new byte[96];
                int len;
                fixed (byte* p = nb) len = Native.mw2_weapon_world_model(packed, p, 96);
                Plugin.Log.LogInfo($"MW2 world gun: {(len > 0 ? System.Text.Encoding.UTF8.GetString(nb, 0, Math.Min(len, 96)) : "?")} (camo {(Camo != 0 ? Camo.ToString() : "own")})");
            }
            // MW2 akimbo: the same world gun in the left hand too.
            if (gunGo != null && Native.mw2_weapon_is_akimbo(weapon) == 1)
            {
                gunLeftGo = new GameObject($"MW2 World Gun {weapon} (left)");
                gunLeftGo.transform.SetParent(root.transform, false);
                gunLeftGo.AddComponent<MeshFilter>().sharedMesh = gunGo.GetComponent<MeshFilter>().sharedMesh;
                var lr = gunLeftGo.AddComponent<MeshRenderer>();
                lr.sharedMaterials = gunGo.GetComponent<MeshRenderer>().sharedMaterials;
                lr.shadowCastingMode = ShadowCastingMode.On;
            }
        }

        /// A weapon's MW2 world model as a child of the root (posed by the caller), or null.
        GameObject WorldModel(uint weapon, string label)
        {
            if (weapon == 0 || Native.mw2_character_weapon_info(weapon, out var info) != 1 || info.vertexCount == 0) return null;
            int n = (int)info.vertexCount;
            var pos = new float[n * 3]; var nor = new float[n * 3]; var uv = new float[n * 2]; var idx = new uint[info.indexCount];
            int ok;
            fixed (float* a = pos) fixed (float* c = nor) fixed (float* t = uv) fixed (uint* x = idx) ok = Native.mw2_character_weapon_mesh(weapon, a, c, t, x);
            if (ok != 1) return null;
            var mesh = new Mesh { name = $"mw2_world_gun_{weapon}", indexFormat = IndexFormat.UInt32 };
            var verts = new Vector3[n]; var norms = new Vector3[n]; var uvs = new Vector2[n];
            for (int v = 0; v < n; v++)
            {
                verts[v] = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                norms[v] = new Vector3(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]);
                uvs[v] = new Vector2(uv[v * 2], uv[v * 2 + 1]);
            }
            mesh.vertices = verts; mesh.normals = norms; mesh.uv = uvs;
            var template = Mw2Gun.TemplateMaterial(owner, out _);
            var mats = new List<Material>();
            mesh.subMeshCount = (int)info.surfaceCount;
            for (uint s = 0; s < info.surfaceCount; s++)
            {
                if (Native.mw2_character_weapon_surface(weapon, s, out var si) != 1) { mats.Add(Mw2Gun.SurfaceMaterial(template, null)); continue; }
                var tris = new int[si.indexCount];
                for (int k = 0; k < si.indexCount; k++) tris[k] = (int)idx[si.indexStart + k];
                mesh.SetTriangles(tris, (int)s);
                uint ss = s;
                var tex = Rgba((w, h, o, cap) => Native.mw2_character_weapon_surface_rgba(weapon, ss, w, h, o, cap));
                // See-through surfaces (the riot shield's window) with MW2's blend, as props do; they
                // were drawn opaque, the back hardware showing where the glass should be clear.
                int blend = Native.mw2_character_weapon_surface_blend(weapon, s);
                var m = blend >= 2 && tex != null ? Mw2Fx.SurfaceMaterial(blend, tex) : null;
                mats.Add(m ?? Mw2Gun.SurfaceMaterial(template, tex));
            }
            mesh.RecalculateBounds();
            var go = new GameObject(label);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats.ToArray();
            r.shadowCastingMode = ShadowCastingMode.On;
            return go;
        }

        /// The held gun's muzzle (world), from the world model's barrel axis (+Z in its Unity space).
        public Vector3? Muzzle(out Vector3 forward, int hand = 0)
        {
            forward = Vector3.forward;
            var g = hand == 1 && gunLeftGo != null ? gunLeftGo : gunGo;
            if (g == null || !g.activeInHierarchy) return null;
            var t = g.transform;
            forward = t.forward;
            return t.position + t.forward * (0.55f * root.transform.localScale.x);
        }

        Mw2CharacterInput displayInput;
        float nextDisplayWeapon;

        /// Character select: the soldier on `parent`, standing with the class's primary.
        public bool BuildDisplay(Transform parent, string faction, Func<uint> weapon = null)
        {
            if (!Build(null, faction)) return false;
            root.transform.SetParent(parent, false);
            // Bigger than RoR2's survivors on the pad: the MW2 soldier is the one to show off (playtest 10-04-26).
            scaleMul = DisplayScale;
            onPad = true;
            displayWeapon = weapon ?? LocalDisplayWeapon;
            displayInput = new Mw2CharacterInput { weapon = DisplayOrM4() };
            nextDisplayWeapon = Time.unscaledTime + 0.5f;
            return true;
        }
        Func<uint> displayWeapon;
        const float DisplayScale = 1.35f;
        float scaleMul = 1f;
        bool onPad;

        uint DisplayOrM4() { uint w = displayWeapon != null ? displayWeapon() : 0; return w != 0 ? w : Native.WeaponIndex("m4_mp"); }

        /// The picked class's primary by name (what the lobby message carries), "m4_mp" without one.
        public static string LocalDisplayWeaponName()
        {
            var cls = Plugin.Instance.UseCustomClass.Value ? Mw2Menus.ClassLoadout(Plugin.Instance.PlayClass - 1) : null;
            return cls != null && cls.Length > 0 && Native.WeaponIndex(cls[0]) != 0 ? cls[0] : "m4_mp";
        }

        /// The picked class's primary (default or custom), the M4 without one.
        public static uint LocalDisplayWeapon()
        {
            var cls = Plugin.Instance.UseCustomClass.Value ? Mw2Menus.ClassLoadout(Plugin.Instance.PlayClass - 1) : null;
            uint w = cls != null && cls.Length > 0 ? Native.WeaponIndex(cls[0]) : 0;
            return w != 0 ? w : Native.WeaponIndex("m4_mp");
        }

        /// Character select: put this one away (kept built for a swap back).
        public void Hide() { if (root != null) root.SetActive(false); }

        public void StepDisplay(Transform parent, float dt)
        {
            if (!Exists || parent == null) return;
            // A class picked in the Loadout tab, or edited in Create-a-Class: he holds its primary (playtest 10-04-26).
            if (Time.unscaledTime >= nextDisplayWeapon) { nextDisplayWeapon = Time.unscaledTime + 0.5f; displayInput.weapon = DisplayOrM4(); }
            var input = displayInput;
            input.dt = dt;
            Step(parent.position, parent.eulerAngles.y, ref input, true);
        }

        public void Destroy()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null; gunGo = gunLeftGo = stowGo = null; gunFor = stowFor = gunCamo = 0;
            animModels.Clear(); // children of root: gone with it
            if (ch != IntPtr.Zero) Native.mw2_character_destroy(ch);
            ch = IntPtr.Zero;
        }
    }
}
