using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MW2RoR2
{
    /// RoR2's opening cutscene, skipped the way a player does it (playtest 10-04-26: press space twice
    /// once it has fully loaded, or wait it out): when IntroCutsceneController says skipping is
    /// allowed (RoR2Application.loadFinished), the skip a press makes, then its own TransitionToTitle
    /// (setting shouldSkip alone left it playing). Not the intro_skip setting: skipping
    /// the whole intro that way left RoR2's input uninitialized and the run never started (10-02-26).
    static class Mw2IntroSkip
    {
        static PropertyInfo loadFinished;

        /// RoR2Application's own "loading finished" (the content the intro hides).
        static bool Loaded()
        {
            if (loadFinished == null)
                loadFinished = typeof(RoR2.RoR2Application).GetProperty("loadFinished", Any) ?? typeof(RoR2.RoR2Application).GetProperty("isInitialLoadFinished", Any);
            if (loadFinished == null) return true;
            var g = loadFinished.GetGetMethod(true);
            return g.Invoke(g.IsStatic ? null : RoR2.RoR2Application.instance, null) is bool b && b;
        }
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static bool done;
        static Type controller;
        static float requestedAt = -1f;

        public static void Update()
        {
            if (done || !Plugin.Instance.SkipIntro.Value) return;
            if (SceneManager.GetActiveScene().name != "intro")
            {
                if (requestedAt >= 0f) { done = true; Plugin.Log.LogInfo($"MW2: left RoR2's intro at {Time.realtimeSinceStartup:F1}s"); }
                return;
            }
            controller = controller ?? typeof(RoR2.Run).Assembly.GetType("RoR2.IntroCutsceneController");
            if (controller == null) { done = true; return; }
            var c = UnityEngine.Object.FindObjectOfType(controller);
            if (c == null) return;
            try
            {
                if (requestedAt < 0f)
                {
                    if (!Loaded()) return; // "after it has fully loaded"
                    // The two presses: a skip request, then shouldSkip (which raises the intro's own skip).
                    var req = controller.GetField("skipRequested", Any);
                    req?.SetValue(req.IsStatic ? null : c, true);
                    var should = controller.GetProperty("shouldSkip", Any);
                    var set = should?.GetSetMethod(true);
                    set?.Invoke(set.IsStatic ? null : c, new object[] { true });
                    requestedAt = Time.unscaledTime;
                    Plugin.Log.LogInfo($"MW2: RoR2 finished loading, intro skipped at {Time.realtimeSinceStartup:F1}s");
                }
                else if (Time.unscaledTime - requestedAt > 0.3f)
                {
                    done = true;
                    var toTitle = controller.GetMethod("TransitionToTitle", Any);
                    if (toTitle != null && toTitle.GetParameters().Length == 0) toTitle.Invoke(toTitle.IsStatic ? null : c, null);
                    Plugin.Log.LogInfo($"MW2: RoR2's intro after the skip request; TransitionToTitle at {Time.realtimeSinceStartup:F1}s");
                }
            }
            catch (Exception e) { done = true; Plugin.Log.LogWarning($"MW2: intro skip failed: {e.InnerException?.Message ?? e.Message}"); }
        }
    }
}
