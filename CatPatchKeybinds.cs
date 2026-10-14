using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CatPatchspace
{
    internal static class CatPatchKeybinds
    {
        public const string HugAction = "cat_patch_hug";
        public const string HugRejectAction = "cat_patch_reject_hug";
        public const string KissAction = "cat_patch_kiss";
        public const string EmoteWheelAction = "cat_patch_emote_wheel";
        public const string SprayAction = "cat_patch_spray";
        public const string SprayInspectAction = "cat_patch_inspect";
        public const string HostModerationAction = "cat_patch_host_moderation";

        public const KeyCode FallbackHugRejectKey = KeyCode.J;
        public const KeyCode FallbackSprayKey = KeyCode.G;
        public const KeyCode FallbackSprayInspectKey = KeyCode.I;
        public const KeyCode FallbackHostModerationKey = KeyCode.Backslash;

        public static void RegisterDefaultsOnce()
        {
            RegisterKeybind(HugAction, HugSettings.FallbackHugKey);
            RegisterKeybind(HugRejectAction, FallbackHugRejectKey);
            RegisterKeybind(KissAction, KeyCode.None);
            RegisterKeybind(EmoteWheelAction, KeyCode.None);
            RegisterKeybind(SprayAction, FallbackSprayKey);
            RegisterKeybind(SprayInspectAction, FallbackSprayInspectKey);
            RegisterKeybind(HostModerationAction, FallbackHostModerationKey);
        }

        public static int AddSettingsRows(List<Setting> settings)
        {
            if (settings == null)
                return 0;

            int added = 0;
            added += AddSettingsRow(settings, HugAction, HugSettings.FallbackHugKey);
            added += AddSettingsRow(settings, HugRejectAction, FallbackHugRejectKey);
            added += AddSettingsRow(settings, KissAction, KeyCode.None);
            added += AddSettingsRow(settings, EmoteWheelAction, KeyCode.None);
            added += AddSettingsRow(settings, SprayAction, FallbackSprayKey);
            added += AddSettingsRow(settings, SprayInspectAction, FallbackSprayInspectKey);
            added += AddSettingsRow(settings, HostModerationAction, FallbackHostModerationKey);
            return added;
        }

        public static bool GetKeyDown(string actionName, KeyCode fallback)
        {
            return Input.GetKeyDown(GetKeyCode(actionName, fallback));
        }

        public static bool GetKey(string actionName, KeyCode fallback)
        {
            return Input.GetKey(GetKeyCode(actionName, fallback));
        }

        public static KeyCode GetKeyCode(string actionName, KeyCode fallback)
        {
            try
            {
                KeyCode configuredKey = KeyBinds.GetBind(actionName);
                return configuredKey == KeyCode.None ? fallback : configuredKey;
            }
            catch
            {
                return fallback;
            }
        }

        public static string GetBindName(string actionName, KeyCode fallback)
        {
            string bindName = "";
            try
            {
                bindName = KeyBinds.GetBindName(actionName);
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(bindName))
                return bindName;

            return fallback.ToString();
        }

        public static bool IsBound(string actionName)
        {
            try
            {
                return KeyBinds.GetBind(actionName) != KeyCode.None;
            }
            catch
            {
                return false;
            }
        }

        private static void RegisterKeybind(string actionName, KeyCode defaultKey)
        {
            try
            {
                Util.RegisterKeybind(actionName, defaultKey);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("CatPatch keybind registration via Util failed for " + actionName + ": " + ex.Message);
            }

            RegisterDictionaryKeybind("Together.CoopKeybinds", "as_dict", actionName, defaultKey);
            RegisterDictionaryKeybind("CasualtiesTogetherUtils.Settings_DefaultSettings_Patch", "custom_binds", actionName, defaultKey);
            RegisterDictionaryKeybind("Settings_DefaultSettings_Patch", "custom_binds", actionName, defaultKey);
            RegisterRuntimeKeybind(actionName, defaultKey);
        }

        private static void RegisterRuntimeKeybind(string actionName, KeyCode defaultKey)
        {
            RegisterDictionaryKeybind("KeyBinds", "cachedBinds", actionName, defaultKey);
        }

        private static int AddSettingsRow(List<Setting> settings, string actionName, KeyCode defaultKey)
        {
            for (int i = 0; i < settings.Count; i++)
            {
                if (settings[i] != null && settings[i].name == actionName)
                    return 0;
            }

            KeyCode savedKey = KeyCode.None;
            try
            {
                savedKey = KeyBinds.GetBind(actionName);
            }
            catch
            {
            }

            settings.Add(new SettingKeybind
            {
                name = actionName,
                value = savedKey != KeyCode.None ? savedKey : defaultKey,
                apply = delegate { },
                category = Setting.SettingCategory.Input
            });
            return 1;
        }

        private static void RegisterDictionaryKeybind(string typeName, string fieldName, string actionName, KeyCode defaultKey)
        {
            try
            {
                Type type = FindType(typeName);
                if (type == null)
                    return;

                FieldInfo field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                IDictionary dictionary = field != null ? field.GetValue(null) as IDictionary : null;
                if (dictionary == null)
                    return;

                if (!dictionary.Contains(actionName))
                    dictionary[actionName] = defaultKey;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("CatPatch keybind registration failed for " + actionName + " in " + typeName + "." + fieldName + ": " + ex.Message);
            }
        }

        private static Type FindType(string typeName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(typeName);
                if (type != null)
                    return type;
            }

            return null;
        }
    }
}
