using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CatPatchspace
{
    internal static class LimbArmorRunSettings
    {
        internal const string LegArmorKey = "catpatchlegarmor";
        internal const string ArmArmorKey = "catpatcharmarmor";
        internal const string TorsoArmorKey = "catpatchtorsoarmor";
        internal const string HeadArmorKey = "catpatchheadarmor";

        private static readonly string[] Keys =
        {
            LegArmorKey,
            ArmArmorKey,
            TorsoArmorKey,
            HeadArmorKey
        };

        internal static void EnsureRegistered()
        {
            if (RunSettings.settingTypes == null)
                return;

            int insertionIndex = FindSettingIndex("bonuslimbarmor");
            insertionIndex = insertionIndex >= 0
                ? insertionIndex + 1
                : RunSettings.settingTypes.Count;

            for (int i = 0; i < Keys.Length; i++)
            {
                string key = Keys[i];
                int existingIndex = FindSettingIndex(key);
                if (existingIndex < 0)
                {
                    RunSettings.settingTypes.Insert(insertionIndex, new RunSettingFloat(key)
                    {
                        limits = new RangeF(0.0f, 4.0f)
                    });
                    insertionIndex++;
                }
                else if (existingIndex >= insertionIndex)
                    insertionIndex = existingIndex + 1;
            }

            if (RunSettings.presets == null)
                return;

            for (int i = 0; i < RunSettings.presets.Count; i++)
            {
                RunSettingsPreset preset = RunSettings.presets[i];
                if (preset != null)
                    EnsureDefaults(preset.presetValues);
            }
        }

        internal static void EnsureDefaults(IDictionary<string, object> settings)
        {
            if (settings == null)
                return;

            for (int i = 0; i < Keys.Length; i++)
            {
                if (!settings.ContainsKey(Keys[i]))
                    settings.Add(Keys[i], 0.0f);
            }
        }

        internal static float GetArmorForLimb(Limb limb)
        {
            if (limb == null)
                return 0.0f;

            string key;
            if (limb.isHead)
                key = HeadArmorKey;
            else if (limb.isArm)
                key = ArmArmorKey;
            else if (limb.isLegLimb)
                key = LegArmorKey;
            else
                key = TorsoArmorKey;

            return GetRunSettingFloat(key);
        }

        internal static bool TryGetLocaleText(string id, out string text)
        {
            switch (id)
            {
                case "runset" + LegArmorKey:
                    text = "Leg armor";
                    return true;
                case "runset" + LegArmorKey + "dsc":
                    text = "Additional armor applied to leg limbs.";
                    return true;
                case "runset" + ArmArmorKey:
                    text = "Arm armor";
                    return true;
                case "runset" + ArmArmorKey + "dsc":
                    text = "Additional armor applied to arm limbs.";
                    return true;
                case "runset" + TorsoArmorKey:
                    text = "Torso armor";
                    return true;
                case "runset" + TorsoArmorKey + "dsc":
                    text = "Additional armor applied to torso limbs.";
                    return true;
                case "runset" + HeadArmorKey:
                    text = "Head armor";
                    return true;
                case "runset" + HeadArmorKey + "dsc":
                    text = "Additional armor applied to the head.";
                    return true;
                default:
                    text = null;
                    return false;
            }
        }

        private static int FindSettingIndex(string key)
        {
            for (int i = 0; i < RunSettings.settingTypes.Count; i++)
            {
                RunSetting setting = RunSettings.settingTypes[i];
                if (setting != null && string.Equals(setting.name, key, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static float GetRunSettingFloat(string key)
        {
            try
            {
                object value;
                if (WorldGeneration.runSettings != null
                    && WorldGeneration.runSettings.TryGetValue(key, out value)
                    && value != null)
                {
                    return Math.Max(0.0f, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }

            return 0.0f;
        }
    }

    [HarmonyPatch(typeof(PreRunScript), "Awake")]
    internal static class LimbArmorPreRunAwakePatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            LimbArmorRunSettings.EnsureRegistered();
        }

        [HarmonyPostfix]
        private static void Postfix(PreRunScript __instance)
        {
            if (__instance != null)
                LimbArmorRunSettings.EnsureDefaults(__instance.runSettings);
        }
    }

    [HarmonyPatch(typeof(Limb), nameof(Limb.GetArmorReduction))]
    internal static class LimbArmorReductionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Limb __instance, ref float __result)
        {
            __result += LimbArmorRunSettings.GetArmorForLimb(__instance);
        }
    }
}
