using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// RoR2's item and equipment displays on the MW2 soldier (playtest 10-06-26: "items we get from ror2
    /// need to be shown on our player models just like they do in the game"). RoR2 hangs them on the
    /// Commando model's ChildLocator points (Head, Chest, ThighL...), and that model is hidden behind
    /// the MW2 body. Here the displays stay visible while the model they belong to is hidden, and each
    /// frame before rendering the ChildLocator points are put on the matching MW2 bones, turned by
    /// the difference between the two rigs' rest poses, so the items ride the MW2 body.
    static class Mw2ItemDisplays
    {
        // Commando's ChildLocator points -> MW2 skeleton bones.
        static readonly (string ror2, string mw2)[] Map =
        {
            ("Pelvis", "j_mainroot"), ("Stomach", "j_spinelower"), ("Chest", "j_spine4"),
            ("Head", "j_head"), ("HeadCenter", "j_head"),
            ("UpperArmL", "j_shoulder_le"), ("LowerArmL", "j_elbow_le"), ("HandL", "j_wrist_le"),
            ("UpperArmR", "j_shoulder_ri"), ("LowerArmR", "j_elbow_ri"), ("HandR", "j_wrist_ri"),
            ("ThighL", "j_hip_le"), ("CalfL", "j_knee_le"), ("FootL", "j_ankle_le"),
            ("ThighR", "j_hip_ri"), ("CalfR", "j_knee_ri"), ("FootR", "j_ankle_ri"),
        };

        class Binding
        {
            public CharacterModel model;
            public Mw2Character mw2;
            public readonly List<(Transform point, Transform bone, Quaternion turn, int depth)> pairs = new List<(Transform, Transform, Quaternion, int)>();
            public bool built;
        }

        static readonly Dictionary<CharacterModel, Binding> bound = new Dictionary<CharacterModel, Binding>();
        // The model whose displays are hidden anyway: the local player's, while in first person.
        static readonly HashSet<CharacterModel> firstPerson = new HashSet<CharacterModel>();

        public static void Init(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(ItemDisplay), nameof(ItemDisplay.SetVisibilityLevel));
            if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(typeof(Mw2ItemDisplays), nameof(VisibilityPrefix)));
            else Plugin.Log.LogWarning("MW2 item displays: ItemDisplay.SetVisibilityLevel not found");
            Application.onBeforeRender += Pose;
        }

        /// The MW2 body `mw2` stands in for `model`: its item displays show on it.
        public static void Bind(CharacterModel model, Mw2Character mw2)
        {
            if (model == null || mw2 == null) return;
            bound[model] = new Binding { model = model, mw2 = mw2 };
            Refresh(model);
        }

        public static void Unbind(CharacterModel model)
        {
            if (model == null || !bound.Remove(model)) return;
            firstPerson.Remove(model);
            Refresh(model);
        }

        /// First person: no items in front of the camera (the body is hidden there too).
        public static void SetFirstPerson(CharacterModel model, bool on)
        {
            if (model == null) return;
            if (on ? firstPerson.Add(model) : firstPerson.Remove(model)) Refresh(model);
        }

        static void Refresh(CharacterModel model)
        {
            if (model == null) return;
            foreach (var d in model.GetComponentsInChildren<ItemDisplay>(true)) d.SetVisibilityLevel(model.visibility);
        }

        static void VisibilityPrefix(ItemDisplay __instance, ref VisibilityLevel newVisibilityLevel)
        {
            if (bound.Count == 0 || __instance == null) return;
            var model = __instance.GetComponentInParent<CharacterModel>();
            if (model != null && bound.ContainsKey(model) && !firstPerson.Contains(model) && newVisibilityLevel == VisibilityLevel.Invisible)
                newVisibilityLevel = VisibilityLevel.Visible;
        }

        static readonly List<CharacterModel> dead = new List<CharacterModel>();

        static void Pose()
        {
            if (bound.Count == 0) return;
            dead.Clear();
            foreach (var kv in bound)
            {
                var b = kv.Value;
                if (kv.Key == null || b.mw2 == null) { dead.Add(kv.Key); continue; }
                if (!b.mw2.Exists) continue; // built a frame or two later (bots), or rebuilt
                if (!b.built && !Build(b)) continue;
                foreach (var (point, bone, turn, _) in b.pairs)
                {
                    if (point == null || bone == null) continue;
                    point.SetPositionAndRotation(bone.position, bone.rotation * turn);
                }
            }
            foreach (var m in dead) bound.Remove(m);
        }

        /// Pairs each ChildLocator point with its MW2 bone; the turn takes the MW2 bone's rest
        /// orientation to the Commando point's, both in their own character frame (+z forward, +y up).
        static bool Build(Binding b)
        {
            var mSkin = b.mw2.Skin;
            if (mSkin == null) return false; // not built yet: next frame
            b.built = true;
            var locator = b.model.GetComponent<ChildLocator>();
            var cSkin = b.model.baseRendererInfos.Select(r => r.renderer as SkinnedMeshRenderer).FirstOrDefault(r => r != null && r.bones != null && r.bones.Length > 0 && r.sharedMesh != null);
            if (locator == null || mSkin == null || cSkin == null)
            {
                Plugin.Log.LogWarning($"MW2 item displays: no {(locator == null ? "ChildLocator" : mSkin == null ? "MW2 skin" : "Commando skin")} on {b.model.name}");
                return false;
            }
            var mBones = mSkin.bones; var mBind = mSkin.sharedMesh.bindposes;
            var cBones = cSkin.bones; var cBind = cSkin.sharedMesh.bindposes;
            // Commando's mesh frame within its model (FBX meshes carry a turn of their own).
            var cMeshToModel = Quaternion.Inverse(b.model.transform.rotation) * cSkin.transform.rotation;
            var missing = new List<string>();
            foreach (var (ror2, mw2) in Map)
            {
                var point = locator.FindChild(ror2);
                int mi = System.Array.FindIndex(mBones, t => t != null && t.name == mw2);
                if (point == null || mi < 0) { missing.Add(point == null ? ror2 : mw2); continue; }
                // Commando rest: the nearest skinned bone at or above the point, then the point's
                // own (static) turn under it.
                Quaternion cRest = Quaternion.identity; bool found = false;
                for (var t = point; t != null && t != b.model.transform; t = t.parent)
                {
                    int ci = System.Array.IndexOf(cBones, t);
                    if (ci < 0) continue;
                    var under = Quaternion.Inverse(t.rotation) * point.rotation;
                    cRest = cMeshToModel * cBind[ci].inverse.rotation * under;
                    found = true;
                    break;
                }
                if (!found) { missing.Add(ror2 + " (no bone)"); continue; }
                var mRest = mBind[mi].inverse.rotation;
                int depth = 0;
                for (var t = point; t != null; t = t.parent) depth++;
                b.pairs.Add((point, mBones[mi], Quaternion.Inverse(mRest) * cRest, depth));
            }
            // Parents first: a point placed after its child would carry the child off.
            b.pairs.Sort((x, y) => x.depth.CompareTo(y.depth));
            Plugin.Log.LogInfo($"MW2 item displays on {b.model.name}: {b.pairs.Count} points on MW2 bones{(missing.Count > 0 ? "; missing " + string.Join(", ", missing) : "")}");
            if (Mw2Pilot.Active)
                Plugin.Log.LogInfo($"MW2 item displays: Commando points {string.Join(", ", Enumerable.Range(0, locator.Count).Select(i => locator.FindChildName(i)))}; MW2 bones {string.Join(", ", mBones.Select(t => t.name))}");
            return true;
        }
    }
}
