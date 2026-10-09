using UnityEngine;
using RoR2;

namespace MW2RoR2
{
    /// The minimap's picture: RoR2's stages have no map art, so an orthographic camera straight above
    /// the player draws the stage (world geometry only, no fog or post effects) into a small texture
    /// each frame, the player's facing up. Its near plane sits a little above the player, so roofs,
    /// arches and overhangs higher up don't cover the map.
    static class Mw2Minimap
    {
        const float Height = 80f, Headroom = 20f;
        static Camera cam;
        static RenderTexture rt;

        /// The map around `at` (metres to the edge: `halfSize`), forward up; null if it can't be drawn.
        public static Texture Render(Camera main, Vector3 at, Vector3 forward, float halfSize)
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return rt;
            if (forward.sqrMagnitude < 1e-4f) return rt;
            if (cam == null)
            {
                if (rt == null) rt = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32) { name = "MW2 Minimap", antiAliasing = 1 };
                var go = new GameObject("MW2 Minimap Camera");
                cam = go.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false; // drawn on demand below
                cam.orthographic = true;
                cam.cullingMask = LayerIndex.world.mask | LayerIndex.defaultLayer.mask;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.02f, 0.03f, 0.03f, 1f);
                cam.useOcclusionCulling = false; // baked for ground-level views
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.depthTextureMode = DepthTextureMode.None;
                cam.targetTexture = rt;
            }
            cam.orthographicSize = halfSize;
            cam.aspect = 1f;
            cam.nearClipPlane = Height - Headroom;
            cam.farClipPlane = Height + 400f;
            cam.transform.SetPositionAndRotation(at + Vector3.up * Height, Quaternion.LookRotation(Vector3.down, forward.normalized));
            // Lit like the stage: on a night stage the map came out near black (QA 10-05-26). Flat,
            // bright ambient for this render only.
            var amb = RenderSettings.ambientLight; var mode = RenderSettings.ambientMode; float inten = RenderSettings.ambientIntensity;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.75f, 0.75f, 0.75f);
            RenderSettings.ambientIntensity = 1.5f;
            // No shadows on the radar: seen from straight above, the sun's shadow cascades flipped from
            // frame to frame and the map flashed (playtest 10-06-26: "stop the minimap from flashing").
            float shadowDist = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = 0f;
            try { cam.Render(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"minimap: {e.Message}"); return null; }
            finally { RenderSettings.ambientMode = mode; RenderSettings.ambientLight = amb; RenderSettings.ambientIntensity = inten; QualitySettings.shadowDistance = shadowDist; }
            return rt;
        }
    }
}
