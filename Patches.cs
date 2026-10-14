using HarmonyLib;
using Together;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace CatPatchspace
{
    internal class Patches
    {
        [HarmonyPatch(typeof(ConsoleScript))]
        internal static class ConsolePatch
        {
            [HarmonyPatch(nameof(ConsoleScript.Start))]
            [HarmonyPostfix]
            private static void StartPatch()
            {
                ConsoleScript.instance.LogToConsole("CatPatch is loaded!");

                bool commandExists = false;
                for (int i = 0; i < ConsoleScript.Commands.Count; i++)
                {
                    if (string.Equals(ConsoleScript.Commands[i].name, "catpatchmoderation", StringComparison.OrdinalIgnoreCase))
                    {
                        commandExists = true;
                        break;
                    }
                }

                if (!commandExists)
                {
                    ConsoleScript.Commands.Add(new Command(
                        "catpatchmoderation",
                        "Opens or closes the CatPatch Host Moderation menu.",
                        delegate(string[] args)
                        {
                            if (!CatPatchHostModerationMenu.TryToggleWindow())
                                ConsoleScript.instance.LogToConsole("CatPatch Host Moderation is only available in a world for the multiplayer host or in singleplayer.");
                        },
                        null,
                        Array.Empty<ValueTuple<string, string>>()));
                }
            }
        }

        [HarmonyPatch(typeof(UIMainMenu), "_GUI_DrawSideMenuWithPlayerList")]
        internal static class MultiplayerSideMenuPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                return true;
            }
        }

        [HarmonyPatch(typeof(UIBullshit), "__GUI__AllTheMenus")]
        internal static class MultiplayerAllMenusPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                CatPatchHostSettingsMenu.DrawOverlayFromKrokMenu();
                CatPatchHostModerationMenu.DrawMenuButtonFromKrokMenu();
            }
        }

        [HarmonyPatch]
        internal static class ModerationPointerOverUiPatch
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> TargetMethods()
            {
                MethodInfo[] methods = typeof(UIUtil).GetMethods(
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == "IsPointerOverUIElement" && methods[i].ReturnType == typeof(bool))
                        yield return methods[i];
                }
            }

            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (!__result && CatPatchHostModerationMenu.IsPointerOverWindow())
                    __result = true;
            }
        }

        [HarmonyPatch]
        internal static class ModerationAdaptiveButtonOverlayPatch
        {
            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
            {
                MethodInfo[] methods = typeof(AdaptiveButton).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == "overlayActive" && methods[i].ReturnType == typeof(bool))
                        return methods[i];
                }

                return null;
            }

            [HarmonyPrepare]
            private static bool Prepare()
            {
                return TargetMethod() != null;
            }

            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (CatPatchHostModerationMenu.IsWindowOpen)
                    __result = true;
            }
        }

        [HarmonyPatch(typeof(SettingsMenu), "Start")]
        internal static class SettingsMenuStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                CatPatchKeybinds.RegisterDefaultsOnce();
                CatPatchKeybinds.AddSettingsRows(Settings.GetAllSettings());
            }
        }

        [HarmonyPatch(typeof(SettingsMenu), "SelectTab", new[] { typeof(Setting.SettingCategory) })]
        internal static class SettingsMenuKeybindDescriptionPatch
        {
            private static readonly FieldInfo SpawnedSettingsField = typeof(SettingsMenu).GetField(
                "spawnedSettings",
                BindingFlags.Instance | BindingFlags.NonPublic);

            [HarmonyPostfix]
            private static void Postfix(SettingsMenu __instance)
            {
                IList rows = SpawnedSettingsField != null ? SpawnedSettingsField.GetValue(__instance) as IList : null;
                if (rows == null)
                    return;

                for (int i = 0; i < rows.Count; i++)
                {
                    GameObject row = rows[i] as GameObject;
                    if (row == null || row.transform.childCount == 0)
                        continue;

                    Transform labelTransform = row.transform.GetChild(0);
                    TextMeshProUGUI label = labelTransform.GetComponent<TextMeshProUGUI>();
                    if (label == null || label.text != "Host Moderation")
                        continue;

                    UITooltip tooltip = labelTransform.GetComponent<UITooltip>();
                    if (tooltip != null)
                    {
                        tooltip.tipName = "Host Moderation";
                        tooltip.tipDesc = "(usable in singleplayer with disabled mp mod)";
                    }
                }
            }
        }

        [HarmonyPatch]
        internal static class CatPatchKeybindLocalePatch
        {
            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Locale), "GetOther", new[] { typeof(string) });
            }

            [HarmonyPostfix]
            private static void Postfix(string __0, ref string __result)
            {
                string id = __0;
                string runSettingText;
                if (LimbArmorRunSettings.TryGetLocaleText(id, out runSettingText))
                    __result = runSettingText;
                else if (id == "gameset" + CatPatchKeybinds.HostModerationAction)
                    __result = "Host Moderation";
                else if (id == "gameset" + CatPatchKeybinds.HostModerationAction + "dsc")
                    __result = "(usable in singleplayer with disabled mp mod)";
            }
        }

        [HarmonyPatch(typeof(Settings), "DefaultSettings")]
        internal static class SettingsDefaultSettingsPatch
        {
            private static bool _logged;

            [HarmonyPrefix]
            private static void Prefix()
            {
                CatPatchKeybinds.RegisterDefaultsOnce();
            }

            [HarmonyPostfix]
            private static void Postfix(ref List<Setting> __result)
            {
                int added = CatPatchKeybinds.AddSettingsRows(__result);
                if (_logged)
                    return;

                _logged = true;
                Plugin.Logger?.LogInfo("CatPatch keybind settings integration active; added " + added + " controls rows.");
            }
        }

        [HarmonyPatch(typeof(Chat), "Server_PlayerChatMessageSend")]
        internal static class ServerChatMutePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(object clientId)
            {
                return !CatPatchHostModerationMenu.ShouldBlockServerChatFromClient(clientId);
            }
        }

        [HarmonyPatch]
        internal static class ServerVoiceMutePatch
        {
            [HarmonyPrepare]
            private static bool Prepare()
            {
                bool canPatch = TargetMethod() != null;
                if (!canPatch)
                    Plugin.Logger?.LogWarning("Voice mute patch skipped: Krokosha MP voice receiver method was not found.");

                return canPatch;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Voicechat), "ServerReceiver__VoiceChatBlob")
                    ?? FindServerVoiceBlobMethod();
            }

            private static MethodBase FindServerVoiceBlobMethod()
            {
                MethodInfo[] methods = typeof(Voicechat).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    string name = methods[i].Name;
                    if (name.Contains("Server") && name.Contains("Voice") && name.Contains("Blob"))
                        return methods[i];
                }

                return null;
            }

            [HarmonyPrefix]
            private static bool Prefix(object clientId)
            {
                return !CatPatchHostModerationMenu.ShouldBlockServerVoiceFromClient(clientId);
            }
        }
    }
}
