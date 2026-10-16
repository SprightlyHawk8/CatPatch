using Together;
using System;
using System.Reflection;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class CatPatchHostSettingsMenu : MonoBehaviour
    {
        private static CatPatchHostSettingsMenu _instance;
        private static bool _windowOpen;
        private static bool _windowPositionInitialized;
        private static Rect _windowRect = new Rect(0f, 0f, 560f, 520f);
        private static Vector2 _windowScrollPosition;

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void OnGUI()
        {
            if (TryGetKrokMenuOpen())
                return;

            DrawOverlay();
        }

        internal static void DrawOverlayFromKrokMenu()
        {
            DrawOverlay();
        }

        private static void DrawOverlay()
        {
            if (!ShouldShowMenuButton())
            {
                _windowOpen = false;
                return;
            }

            int oldDepth = GUI.depth;
            Color oldColor = GUI.color;
            Color oldBackgroundColor = GUI.backgroundColor;
            Color oldContentColor = GUI.contentColor;
            bool oldEnabled = GUI.enabled;
            GUI.depth = -30000;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            GUI.enabled = true;

            try
            {
                DrawSettingsButton();

                if (_windowOpen)
                    DrawSettingsWindow();
            }
            finally
            {
                GUI.depth = oldDepth;
                GUI.color = oldColor;
                GUI.backgroundColor = oldBackgroundColor;
                GUI.contentColor = oldContentColor;
                GUI.enabled = oldEnabled;
            }
        }

        private static void DrawSettingsButton()
        {
            float scale = GetMenuScale();
            float size = 103f * scale;
            float gap = 2f * scale;

            Rect buttonRect = new Rect(
                Screen.width * 0.635f + gap,
                Screen.height - size,
                size,
                size);

            GUIStyle style = CreateKrokStyleButton();

            UnityEngine.Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = new UnityEngine.Color(0.9f, 0.9f, 0.9f, 1f);

            try
            {
                if (GUI.Button(buttonRect, "Cat\nPatch", style))
                    _windowOpen = !_windowOpen;
            }
            finally
            {
                GUI.backgroundColor = previousBackground;
            }
        }

        private static void DrawSettingsWindow()
        {
            InitializeWindowPositionOnce();

            _windowRect = GUI.Window(
                "CatPatchHostSettings".GetHashCode(),
                _windowRect,
                DrawWindowContents,
                "CatPatch Client Settings");
        }

        private static void InitializeWindowPositionOnce()
        {
            if (_windowPositionInitialized)
                return;

            _windowPositionInitialized = true;

            const float margin = 20f;
            _windowRect.x = Mathf.Max(margin, Screen.width - _windowRect.width - margin);
            _windowRect.y = Mathf.Max(margin, (Screen.height - _windowRect.height) * 0.5f);
        }

        private static void DrawWindowContents(int windowId)
        {
            GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                wordWrap = true
            };

            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 15,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            _windowScrollPosition = GUILayout.BeginScrollView(
                _windowScrollPosition,
                false,
                true,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.ExpandHeight(true));

            GUILayout.Label("Client Preferences", labelStyle);

            bool showSprays = CatPatchClientSettings.ShowSprays;
            bool newShowSprays = DrawToggleRow("Show sprays", showSprays, labelStyle);
            if (newShowSprays != showSprays)
                CatPatchClientSettings.ShowSprays = newShowSprays;

            DrawUpdaterControls(labelStyle, buttonStyle);

            GUILayout.Space(12f);
            if (GUILayout.Button("Suggest a feature, join the discord", buttonStyle, GUILayout.Height(48f)))
                Application.OpenURL("https://discord.gg/vdrXr6vXHZ");

            GUILayout.Space(12f);
            GUILayout.Label(
                "Host settings moved. Open server and click Host Moderation on the left.\n\n\nDEFAULT KEYBINDS:\n\nH: Hug\nG: Spray\nI: Inspect Spray",
                labelStyle);

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private static bool DrawToggleRow(string label, bool value, GUIStyle labelStyle)
        {
            Rect rect = GUILayoutUtility.GetRect(0f, 40f, GUILayout.ExpandWidth(true));
            Rect boxRect = new Rect(rect.x + 2f, rect.y + 4f, 30f, 30f);
            Rect labelRect = new Rect(boxRect.xMax + 10f, rect.y + 8f, rect.width - boxRect.width - 16f, 24f);
            Rect screenRect = GUIUtility.GUIToScreenRect(rect);

            DrawCheckbox(boxRect, value);
            GUI.Label(labelRect, label, labelStyle);
            TryRegisterCursorOverlap(screenRect);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                return !value;

            return value;
        }

        private static bool IsCursorInScreenRect(Rect screenRect)
        {
            try
            {
                return screenRect.Contains(UIBullshit.GetCursorPosInGUI());
            }
            catch
            {
                return false;
            }
        }

        private static void TryRegisterCursorOverlap(Rect screenRect)
        {
            try
            {
                UIBullshit.CheckCursorOverlap(screenRect);
            }
            catch
            {
            }
        }

        private static void DrawCheckbox(Rect rect, bool value)
        {
            Color oldColor = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 3f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 3f, rect.width, 3f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 3f, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - 3f, rect.y, 3f, rect.height), Texture2D.whiteTexture);

            if (value)
            {
                Rect inner = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, rect.height - 16f);
                GUI.DrawTexture(inner, Texture2D.whiteTexture);
            }

            GUI.color = oldColor;
        }

        private static void DrawUpdaterControls(GUIStyle labelStyle, GUIStyle buttonStyle)
        {
            CatPatchUpdater updater = GetUpdater();
            if (updater == null)
                return;

            GUILayout.Space(8f);
            GUILayout.Label("Updates", labelStyle);

            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && !updater.IsBusy;
            try
            {
                if (GUILayout.Button("Check Now for Update", buttonStyle, GUILayout.Height(42f)))
                    updater.ForceCheckNow();
            }
            finally
            {
                GUI.enabled = oldEnabled;
            }

            GUILayout.EndHorizontal();

            GUILayout.Label("Installed: " + Plugin.ModVersion, labelStyle);

            string latestVersion = updater.LatestVersion;
            if (!string.IsNullOrWhiteSpace(latestVersion))
                GUILayout.Label("Latest: " + latestVersion, labelStyle);

            GUILayout.Label(updater.Status, labelStyle);

            string error = updater.Error;
            if (!string.IsNullOrWhiteSpace(error))
                GUILayout.Label("Error: " + error, labelStyle);

            if (updater.UpdateAvailable)
            {
                foreach (string change in updater.GetChangelog())
                    GUILayout.Label("- " + change, labelStyle);
            }

        }

        private static CatPatchUpdater GetUpdater()
        {
            CatPatchUpdater updater = CatPatchUpdater.Instance;
            if (updater != null)
                return updater;

            try
            {
                if (Plugin.Instance != null)
                    return Plugin.Instance.gameObject.AddComponent<CatPatchUpdater>();
            }
            catch
            {
            }

            return null;
        }

        private static void DrawChallengeTrapMultiplierSliders()
        {
            string[] trapIds = CatPatchServerSettings.ChallengeTrapIds;
            for (int i = 0; i < trapIds.Length; i++)
            {
                string trapId = trapIds[i];

                GUILayout.BeginHorizontal();
                GUILayout.Label(FormatTrapName(trapId), GUILayout.Width(120f));

                float multiplier = CatPatchServerSettings.GetChallengeTrapMultiplier(trapId);
                multiplier = GUILayout.HorizontalSlider(multiplier, 0.0f, 10.0f);
                multiplier = Mathf.Round(multiplier * 10f) / 10f;

                CatPatchServerSettings.SetChallengeTrapMultiplier(trapId, multiplier);

                GUILayout.Label(multiplier.ToString("0.0") + "x", GUILayout.Width(50f));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Elder thornbacks", GUILayout.Width(120f));

            int elderThornbacks = CatPatchServerSettings.ChallengeElderThornbackCount;
            float elderThornbackSlider = GUILayout.HorizontalSlider(elderThornbacks, 0.0f, 20.0f);
            elderThornbacks = Mathf.RoundToInt(elderThornbackSlider);

            CatPatchServerSettings.ChallengeElderThornbackCount = elderThornbacks;

            GUILayout.Label(elderThornbacks.ToString("0"), GUILayout.Width(50f));
            GUILayout.EndHorizontal();
        }

        private static string FormatTrapName(string trapId)
        {
            switch (trapId)
            {
                case "spikestabber":
                    return "Spike stabber";
                case "barbedwirefence":
                    return "Barbed wire";
                case "CaveTicks":
                    return "Cave ticks A";
                case "caveticks":
                    return "Cave ticks B";
                case "jumppad":
                    return "Jump pad";
                case "soundcannon":
                    return "Sound cannon";
                case "wallbiter":
                    return "Wall biter";
                case "shadecrawler":
                    return "Shade crawler";
                case "thornbackyoung":
                    return "Thornback";
                case "overgrowntick":
                    return "Overgrown tick";
                case "grabberplant":
                    return "Grabber plant";
                case "skullcrusher":
                    return "Skull crusher";
                case "turret":
                    return "Turret";
                default:
                    if (string.IsNullOrWhiteSpace(trapId))
                        return "Unknown";

                    return char.ToUpperInvariant(trapId[0]) + trapId.Substring(1);
            }
        }

        private static bool ShouldShowMenuButton()
        {
            if (IsBaseRunSettingsScreenOpen())
                return false;

            if (TryGetKrokMenuOpen())
                return true;

            try
            {
                return PlayerCamera.main == null;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsBaseRunSettingsScreenOpen()
        {
            try
            {
                return PreRunScript.instance != null
                    && PreRunScript.instance.runSettingsScreen != null
                    && PreRunScript.instance.runSettingsScreen.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        private static GUIStyle CreateKrokStyleButton()
        {
            float scale = GetMenuScale();
            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(14f * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter
            };

            try
            {
                InvokeKrokStaticMethod("UIBullshit", "_GUI_SetButtonSkinTexture", style, true);
            }
            catch
            {
                // Fall back to the default Unity GUI button style if Krok's helper changes.
            }

            return style;
        }

        private static float GetMenuScale()
        {
            try
            {
                object result = InvokeKrokStaticMethod("UIMainMenu", "GetMenuUIScale");
                if (result is float)
                    return Mathf.Clamp((float)result, 0.5f, 3.0f);
            }
            catch
            {
            }

            return 1.0f;
        }

        private static bool TryGetKrokMenuOpen()
        {
            try
            {
                if (UIBullshit.IsAnyMenuOpen())
                    return true;
            }
            catch
            {
            }

            try
            {
                if (UIMainMenu.mainmenu_open)
                    return true;
            }
            catch
            {
            }

            try
            {
                object result = InvokeKrokStaticMethod("UIMainMenu", "IsOpen");
                return result is bool && (bool)result;
            }
            catch
            {
                return false;
            }
        }

        private static object InvokeKrokStaticMethod(string typeName, string methodName, params object[] args)
        {
            Type type = FindKrokType(typeName);
            if (type == null)
                return null;

            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
                return null;

            return method.Invoke(null, args);
        }

        private static Type FindKrokType(string typeName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType("Together." + typeName);
                if (type != null)
                    return type;

                type = assemblies[i].GetType(typeName);
                if (type != null)
                    return type;
            }

            return null;
        }
    }
}
