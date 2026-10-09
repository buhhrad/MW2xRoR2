using RoR2;
using UnityEngine;

namespace MW2RoR2
{
    /// First-person MW2 view on RoR2's camera: RoR2's own camera-params override puts the
    /// camera at the survivor's eye; the survivor's model is hidden; MW2 recoil is added as
    /// a per-frame delta (RoR2 builds each frame's rotation from the last, so adding the
    /// full kick every frame would stack and climb).
    class Mw2View
    {
        CameraTargetParams ctp;
        CameraTargetParams.CameraParamsOverrideHandle handle;
        CharacterModel hiddenModel;
        Vector3 appliedKick;
        public bool FirstPerson { get; private set; }

        // The viewmodel is an overlay, as SkyCraft draws Minecraft's hand over Skyrim: a
        // second camera that renders only the viewmodel layer, in MW2 view space (eye at the
        // origin, true MW2 inches, MW2's 65 deg viewmodel FOV), on top of RoR2's frame.
        // It can't clip into walls and doesn't depend on RoR2's camera settings.
        public const float Mw2ViewmodelFov = 65f;
        /// IW's cg_fov is the horizontal angle on a 4:3 screen (wider screens see more at the sides);
        /// Unity's fieldOfView is vertical: 2 atan(tan(65/2) * 3/4) = 51.1 deg. Drawn at 65 vertical
        /// the gun sat ~27% too small and far, and the cut ends of the sleeves came into view (playtest
        /// 10-04-26: the SPAS's left arm edge while running).
        /// The player's cg_fov (config FieldOfView; MW2's 65 by default). IW4 draws the world and the
        /// gun with it, so a wider FOV shows more and the gun sits smaller, as in MW2.
        public static float CgFov => Plugin.Instance != null ? Mathf.Clamp(Plugin.Instance.FieldOfView.Value, 65f, 90f) : Mw2ViewmodelFov;
        public static float Vertical(float cgFov) => 2f * Mathf.Atan(Mathf.Tan(cgFov * 0.5f * Mathf.Deg2Rad) * 0.75f) * Mathf.Rad2Deg;
        public static float Mw2ViewmodelFovVertical => Vertical(CgFov);
        /// The world camera wider than the gun's, as a tangent ratio (the showcase: its 16:9 frame shows
        /// what a 21:9 screen does across, the gun drawn the same - playtest 10-07-26: the building on
        /// the right of his 315 pick was cut off). 1 everywhere else.
        public static float WorldWiden = 1f;

        /// A point on the gun (drawn by the overlay at the gun's FOV) moved to where the wider world
        /// camera shows it at the same spot on screen: its flash, brass and tracer start at the barrel.
        public static Vector3 OntoWorld(Transform cam, Vector3 p)
        {
            if (WorldWiden == 1f || cam == null) return p;
            var l = cam.InverseTransformPoint(p);
            l.x *= WorldWiden; l.y *= WorldWiden;
            return cam.TransformPoint(l);
        }
        public const float InchesToMetres = 0.0254f;
        static int layer = -1;
        Camera overlay;

        public static int Layer
        {
            get
            {
                if (layer >= 0) return layer;
                for (int i = 31; i >= 8; i--)
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { layer = i; break; }
                if (layer < 0) layer = 31;
                Plugin.Log.LogInfo($"MW2 viewmodel overlay layer: {layer}");
                return layer;
            }
        }

        public Transform OverlayTransform => overlay != null ? overlay.transform : null;
        public Camera Overlay => overlay;

        void EnsureOverlay(Camera main)
        {
            if (overlay != null && overlay.transform.parent == main.transform) return;
            if (overlay != null) Object.Destroy(overlay.gameObject);
            var go = new GameObject("MW2 Viewmodel Camera");
            go.transform.SetParent(main.transform, false);
            overlay = go.AddComponent<Camera>();
            overlay.clearFlags = CameraClearFlags.Depth;
            overlay.cullingMask = 1 << Layer;
            overlay.nearClipPlane = 0.01f;
            overlay.farClipPlane = 5f;
            overlay.useOcclusionCulling = false;
            overlay.allowHDR = main.allowHDR;
            overlay.renderingPath = main.renderingPath;
        }

        void DropOverlay()
        {
            if (overlay != null) Object.Destroy(overlay.gameObject);
            overlay = null;
        }

        public void Enter(CharacterBody body, float eyeHeightMetres)
        {
            Exit();
            ctp = body.GetComponent<CameraTargetParams>();
            if (ctp != null && ctp.cameraParams != null)
            {
                var data = ctp.cameraParams.data;
                data.idealLocalCameraPos = Vector3.zero;
                data.isFirstPerson = true;
                handle = ctp.AddParamsOverride(new CameraTargetParams.CameraParamsOverrideRequest
                {
                    cameraParamsData = data,
                    priority = 10f,
                }, 0.15f);
                FirstPerson = true;
            }
            var model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
            hiddenModel = model != null ? model.GetComponent<CharacterModel>() : null;
            if (hiddenModel != null) hiddenModel.invisibilityCount++;
            Mw2ItemDisplays.SetFirstPerson(hiddenModel, true);
        }

        public void Exit()
        {
            DropOverlay();
            if (ctp != null && FirstPerson) ctp.RemoveParamsOverride(handle, 0.15f);
            Mw2ItemDisplays.SetFirstPerson(hiddenModel, false);
            if (hiddenModel != null) hiddenModel.invisibilityCount--;
            hiddenModel = null;
            ctp = null;
            FirstPerson = false;
        }

        /// The kick on the camera is gone (its rotation was put back to rest): track from zero.
        /// Not on Enter / Exit: the camera keeps its rotation through those (third person's ADS
        /// switches both ways), and forgetting the kick there left it on the camera for good.
        public void ForgetKick() => appliedKick = Vector3.zero;

        /// After RoR2 places the camera this frame.
        // RoR2 sets the camera FOV each frame; if it hasn't (the value is still what we wrote),
        // zoom from the FOV it had before, so ADS zoom never compounds.
        float writtenFov = -1f, baseFov = 60f;

        /// MW2's ADS zoom as a tangent ratio against cg_fov 65: k = tan(fov/2) / tan(65/2).
        public static float ZoomRatio(float mw2Fov) => Mathf.Tan(mw2Fov * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(CgFov * 0.5f * Mathf.Deg2Rad);

        static float Zoomed(float fov, float k) => 2f * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * k) * Mathf.Rad2Deg;

        public void OnCamera(Camera cam, Vector3 kickNow, float zoomK = 1f)
        {
            if (Mathf.Abs(cam.fieldOfView - writtenFov) > 0.01f) baseFov = cam.fieldOfView;
            // First person sees with MW2's cg_fov; third person keeps RoR2's camera FOV.
            if (FirstPerson) baseFov = Zoomed(Vertical(CgFov), WorldWiden);
            cam.fieldOfView = Zoomed(baseFov, Mathf.Clamp(zoomK, 0.01f, 1f));
            writtenFov = cam.fieldOfView;
            // IW4: pitch + = down, yaw + = left. Unity: Euler x + = down, y + = right.
            var delta = kickNow - appliedKick;
            cam.transform.rotation *= Quaternion.Euler(delta.x, -delta.y, -delta.z);
            appliedKick = kickNow;
            if (!FirstPerson) { DropOverlay(); return; }
            EnsureOverlay(cam);
            cam.cullingMask &= ~(1 << Layer); // the world camera never draws the viewmodel
            overlay.depth = cam.depth + 1f;
            // MW2 draws the gun with the zoomed view too (sights grow as you aim).
            overlay.fieldOfView = Zoomed(Mw2ViewmodelFovVertical, Mathf.Clamp(zoomK, 0.01f, 1f));
            overlay.rect = cam.rect;
            overlay.transform.localPosition = Vector3.zero;
            overlay.transform.localRotation = Quaternion.identity;
        }
    }
}
