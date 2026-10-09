using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MW2RoR2
{
    /// Playtest video: the final screen (world, viewmodel, HUD) at ~15 fps, downscaled on the GPU
    /// and read back asynchronously, written as numbered JPGs + frames.csv (time, step) for
    /// tools/review_playtest.py to turn into an MP4.
    static class Mw2Recorder
    {
        public static bool Recording;
        static string dir;
        static int frame;
        static StreamWriter index;
        const int W = 1280, Fps = 15;

        public static void Start(string outDir)
        {
            if (Recording) return;
            dir = Path.Combine(outDir, "video");
            Directory.CreateDirectory(dir);
            index = new StreamWriter(Path.Combine(dir, "frames.csv")) { AutoFlush = true };
            index.WriteLine("frame,time,step");
            Recording = true;
            Plugin.Instance.StartCoroutine(Loop());
        }

        public static void Stop()
        {
            Recording = false;
            try { index?.Dispose(); } catch { }
            index = null;
        }

        static IEnumerator Loop()
        {
            var eof = new WaitForEndOfFrame();
            int h = Mathf.RoundToInt(W * (float)Screen.height / Mathf.Max(Screen.width, 1)) & ~1;
            var small = new RenderTexture(W, h, 0, RenderTextureFormat.ARGB32);
            float next = 0f;
            while (Recording)
            {
                yield return eof;
                if (Time.unscaledTime < next) continue;
                next = Time.unscaledTime + 1f / Fps;
                var full = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
                ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
                Graphics.Blit(full, small);
                RenderTexture.ReleaseTemporary(full);
                int n = frame++;
                float t = Time.unscaledTime;
                string step = Mw2Pilot.CurrentStep;
                AsyncGPUReadback.Request(small, 0, TextureFormat.RGBA32, req =>
                {
                    if (req.hasError || dir == null) return;
                    var data = req.GetData<byte>();
                    var jpg = ImageConversion.EncodeNativeArrayToJPG(data, GraphicsFormat.R8G8B8A8_UNorm, (uint)W, (uint)h, 0, 80);
                    try
                    {
                        File.WriteAllBytes(Path.Combine(dir, $"{n:00000}.jpg"), jpg.ToArray());
                        index?.WriteLine($"{n},{t:F3},{step}");
                    }
                    catch { }
                    jpg.Dispose();
                });
            }
            yield return new WaitForSecondsRealtime(0.5f);
            small.Release();
        }
    }
}
