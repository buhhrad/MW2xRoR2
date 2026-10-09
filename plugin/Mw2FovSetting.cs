using System.Linq;
using HarmonyLib;
using RoR2;
using RoR2.ConVar;
using RoR2.UI;
using UnityEngine;

namespace MW2RoR2
{
    /// MW2's cg_fov as a slider in RoR2's own settings (the pause menu's Settings too), so it can be
    /// changed mid-game (playtest 10-04-26). A console setting "mw2_cg_fov" carries the value (RoR2's
    /// settings controls read and write console settings by name); the slider is a copy of one of
    /// RoR2's, pointed at it. The value itself stays in the plugin config (View.FieldOfView).
    static class Mw2FovSetting
    {
        public const string Name = "mw2_cg_fov";
        const string Token = "MW2_SETTINGS_CG_FOV";
        static bool registered;

        class FovConVar : BaseConVar
        {
            public FovConVar() : base(Name, ConVarFlags.None, "65", "MW2 first-person field of view (cg_fov, 65-90).") { }

            public override void SetString(string newValue)
            {
                if (TextSerialization.TryParseInvariant(newValue, out float v))
                    Plugin.Instance.FieldOfView.Value = Mathf.Round(Mathf.Clamp(v, 65f, 90f));
            }

            public override string GetString() => TextSerialization.ToStringInvariant(Plugin.Instance.FieldOfView.Value);
        }

        public const string ThirdOnlyName = "mw2_third_person_only";
        const string ThirdOnlyToken = "MW2_SETTINGS_THIRD_PERSON_ONLY";

        /// View.ThirdPersonOnly as an Off / On setting beside the FOV slider (playtest 10-06-26: "we need
        /// that as a setting"); takes effect at once, mid-run too.
        class ThirdOnlyConVar : BaseConVar
        {
            public ThirdOnlyConVar() : base(ThirdOnlyName, ConVarFlags.None, "0", "MW2 third person only: never first person, aiming zooms over the shoulder (0/1).") { }

            public override void SetString(string newValue)
            {
                if (TextSerialization.TryParseInvariant(newValue, out int v)) Plugin.Instance.ThirdPersonOnly.Value = v != 0;
            }

            public override string GetString() => Plugin.Instance.ThirdPersonOnly.Value ? "1" : "0";
        }

        public static void Init(Harmony harmony)
        {
            // Awake since RoR2 1.21 (10-08-26 patch: the panel's Start went, the FOV and Third Person
            // Only settings silently disappeared).
            var awake = AccessTools.Method(typeof(SettingsPanelController), "Awake");
            if (awake != null) harmony.Patch(awake, postfix: new HarmonyMethod(typeof(Mw2FovSetting), nameof(PanelStart)));
            else Plugin.Log.LogWarning("MW2 FOV setting: SettingsPanelController.Awake not found");
            Mw2Survivor.SetString(Token, "MW2 First-Person FOV");
            Mw2Survivor.SetString(ThirdOnlyToken, "MW2 Third Person Only");
        }

        /// Per frame until RoR2's console exists: register the setting once.
        public static void Update()
        {
            if (registered || RoR2.Console.instance == null) return;
            registered = true;
            var register = AccessTools.Method(typeof(RoR2.Console), "RegisterConVarInternal");
            if (register == null) { Plugin.Log.LogWarning("MW2 FOV setting: Console.RegisterConVarInternal not found"); return; }
            register.Invoke(register.IsStatic ? null : RoR2.Console.instance, new object[] { new FovConVar() });
            register.Invoke(register.IsStatic ? null : RoR2.Console.instance, new object[] { new ThirdOnlyConVar() });
            Plugin.Log.LogInfo($"MW2 FOV setting: console setting {Name} registered ({(RoR2.Console.instance.FindConVar(Name) != null ? "found" : "NOT found")})");
        }

        /// A settings panel opened: under RoR2's Screen Shake slider (Gameplay), a copy for MW2's cg_fov.
        static void PanelStart(SettingsPanelController __instance)
        {
            if (__instance == null || !registered) return;
            var sliders = __instance.GetComponentsInChildren<SettingsSlider>(true);
            if (sliders.Length == 0 || sliders.Any(s => s.settingName == Name)) return;
            // RoR2 has no FOV setting of its own: ours goes under Screen Shake on the Gameplay tab
            // (the tab Settings opens on).
            var template = sliders.FirstOrDefault(s => s.settingName == "screenShakeScale");
            if (Mw2Pilot.Active) Plugin.Log.LogInfo($"MW2 FOV setting: panel {__instance.name} sliders: {string.Join(", ", sliders.Select(x => x.settingName))}");
            if (template == null) return;
            var go = template.gameObject;
            bool was = go.activeSelf;
            go.SetActive(false); // the copy starts inactive, so it never binds to "fov"
            var copy = Object.Instantiate(go, go.transform.parent);
            go.SetActive(was);
            copy.name = "MW2 cg_fov";
            copy.transform.SetSiblingIndex(go.transform.GetSiblingIndex() + 1);
            var s = copy.GetComponent<SettingsSlider>();
            // Screen Shake is a profile field; ours is a console setting. Left on the profile the
            // copy looked for a "mw2_cg_fov" save field and changed nothing (playtest 10-04-26: the slider
            // didn't work; log: "Save field mw2_cg_fov is not defined").
            s.settingSource = BaseSettingsControl.SettingSource.ConVar;
            s.useConfirmationDialog = false;
            s.settingName = Name;
            s.nameToken = Token;
            s.minValue = 65f;
            s.maxValue = 90f;
            s.formatString = "{0:0}";
            foreach (var label in copy.GetComponentsInChildren<LanguageTextMeshController>(true))
                if (label.token == template.nameToken) label.token = Token;
            copy.SetActive(true);
            Plugin.Log.LogInfo($"MW2 FOV setting: slider added beside RoR2's '{template.settingName}' ({template.nameToken}) in {__instance.name}");
            AddThirdOnly(__instance, copy.transform);
        }

        /// Under the FOV slider: a copy of one of the panel's Off / On carousels, pointed at
        /// mw2_third_person_only.
        static void AddThirdOnly(SettingsPanelController panel, Transform after)
        {
            var carousels = panel.GetComponentsInChildren<CarouselController>(true);
            if (carousels.Any(c => c.settingName == ThirdOnlyName)) return;
            var template = carousels.FirstOrDefault(c => c.choices != null && c.choices.Length == 2
                && c.choices[0].convarValue == "0" && c.choices[1].convarValue == "1");
            if (template == null)
            {
                Plugin.Log.LogWarning($"MW2 third-person-only setting: no Off / On control to copy in {panel.name} ({string.Join(", ", carousels.Select(c => c.settingName))})");
                return;
            }
            var go = template.gameObject;
            bool was = go.activeSelf;
            go.SetActive(false); // the copy starts inactive, so it never binds to the original setting
            var copy = Object.Instantiate(go, after.parent);
            go.SetActive(was);
            copy.name = "MW2 third person only";
            copy.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);
            var c2 = copy.GetComponent<CarouselController>();
            c2.settingSource = BaseSettingsControl.SettingSource.ConVar;
            c2.useConfirmationDialog = false;
            c2.settingName = ThirdOnlyName;
            c2.nameToken = ThirdOnlyToken;
            foreach (var label in copy.GetComponentsInChildren<LanguageTextMeshController>(true))
                if (label.token == template.nameToken) label.token = ThirdOnlyToken;
            copy.SetActive(true);
            Plugin.Log.LogInfo($"MW2 third-person-only setting: added (copy of '{template.settingName}') in {panel.name}");
        }
    }
}
