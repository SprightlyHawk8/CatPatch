using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Together;
using CasualtiesTogetherUtils;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class CatPatchHostModerationMenu : MonoBehaviour
    {
        private static readonly Rect DefaultWindowRect = new Rect(0f, 20f, 1060f, 720f);
        private static readonly FieldInfo BodyTimeRagdolledField = typeof(Body).GetField("timeRagdolled", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] TabLabels =
        {
            "Actions",
            "Players",
            "Mute",
            "Sprays",
            "Bans",
            "Run Settings"
        };

        private static Rect _windowRect = DefaultWindowRect;
        private static bool _windowOpen;
        private static bool _windowPositionInitialized;
        private static Vector2 _scroll;
        private static ModerationTab _tab;
        private static ulong _actionsSelectedClientId;
        private static bool _actionsTargetDropdownOpen;
        private static Vector2 _actionsDropdownScroll;
        private static string _statusText = "";
        private static bool _statusImportant;
        private static double _statusUntil;
        private static Vector2 _sidePanelScroll;
        private static readonly HashSet<ulong> ServerChatMutedClientIds = new HashSet<ulong>();
        private static readonly HashSet<ulong> ServerVoiceMutedClientIds = new HashSet<ulong>();
        private static readonly Dictionary<ulong, double> MutedSprayUntilByClientId = new Dictionary<ulong, double>();
        private static string _sprayMuteMinutes = "10";
        private static string _activeSliderKey;
        private static readonly HashSet<MfcHazardCategory> _expandedMfcCategories =
            new HashSet<MfcHazardCategory>();
        private static string _mfcPresetText = "";

        private static GUIStyle _titleStyle;
        private static GUIStyle _subtitleStyle;
        private static GUIStyle _tabSelectedStyle;
        private static GUIStyle _sectionHeaderStyle;
        private static GUIStyle _rowMainStyle;
        private static GUIStyle _rowMetaStyle;
        private static GUIStyle _statusStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _actionButtonStyle;
        private static GUIStyle _playerButtonStyle;
        private static bool _stylesReady;
        // Unity rejects RectOffset construction from this MonoBehaviour's static constructor.
        private static RectOffset _zeroMargin;
        private static RectOffset ZeroMargin
        {
            get
            {
                if (_zeroMargin == null)
                    _zeroMargin = new RectOffset(0, 0, 0, 0);
                return _zeroMargin;
            }
        }
        private static readonly GUIContent RowMeasure = new GUIContent("0");
        private static bool _buttonSkinFailed;

        private void Update()
        {
            if (_windowOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                _windowOpen = false;
                return;
            }

            KeyCode moderationKey = CatPatchKeybinds.GetKeyCode(
                CatPatchKeybinds.HostModerationAction,
                CatPatchKeybinds.FallbackHostModerationKey);
            if (!Input.GetKeyDown(moderationKey))
                return;

            if (CatPatchConsoleGuard.IsGameInputUiActive())
                return;

            if (!IsModerationSessionAvailable())
                return;

            ToggleWindow();
        }

        private void OnGUI()
        {
            if (IsMultiplayerMenuAvailable())
                return;

            DrawWindowOverlay();
        }

        internal static bool IsWindowOpen
        {
            get { return _windowOpen; }
        }

        internal static bool IsPointerOverWindow()
        {
            if (!_windowOpen)
                return false;

            try
            {
                return _windowRect.Contains(UIBullshit.GetCursorPosInGUI());
            }
            catch
            {
                Vector2 mouse = Input.mousePosition;
                mouse.y = Screen.height - mouse.y;
                return _windowRect.Contains(mouse);
            }
        }

        internal static void DrawMenuButtonFromKrokMenu()
        {
            if (!IsMultiplayerMenuAvailable() || UIMainMenu.mainmenu_open)
            {
                return;
            }

            EnsureStyles();
            RefreshButtonStyle();
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
                DrawHostModerationOverlayButton();

                if (_windowOpen)
                    DrawModerationWindow();
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

        private static void DrawWindowOverlay()
        {
            if (!ShouldDrawUi())
            {
                _windowOpen = false;
                return;
            }

            if (!_windowOpen)
                return;

            EnsureStyles();
            RefreshButtonStyle();
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
                if (_windowOpen)
                    DrawModerationWindow();
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

        internal static bool DrawPatchedSidePanel()
        {
            try
            {
                if (!Net.IsRunning || !IsAnyKrokMenuOpen())
                    return false;

                EnsureStyles();
                RefreshButtonStyle();

                int oldDepth = GUI.depth;
                GUI.depth = -20000;
                bool inWorld = Util.IsInWorld();
                GUI.skin.label.alignment = TextAnchor.UpperLeft;

                try
                {
                    Rect rect = UIBullshit.ScaleRect(
                        0f,
                        540f - UIMainMenu.PANEL_SIZE_Y * 0.5f,
                        UIMainMenu.PANEL_SIZE_X,
                        UIMainMenu.PANEL_SIZE_Y,
                        inWorld);

                    UIBullshit._GUI_9SlicePanel(
                        rect,
                        inWorld ? 1f : 0.7f,
                        0.6980392f,
                        true,
                        UIBullshit.unscaled_uiBlockNano);

                    Rect screenRect = rect.ShrinkBorder((float)UIBullshit.uiBlockNanoBorderSize);
                    GUILayout.BeginArea(screenRect);

                    float fixedHeight = GUI.skin.label.fixedHeight;
                    int fontSize = GUI.skin.label.fontSize;
                    bool stretchHeight = GUI.skin.label.stretchHeight;
                    TextAnchor alignment = GUI.skin.label.alignment;
                    bool wordWrap = GUI.skin.label.wordWrap;
                    RectOffset margin = GUI.skin.label.margin;
                    Color labelColor = GUI.skin.label.normal.textColor;

                    try
                    {
                        GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                        GUI.skin.label.wordWrap = false;
                        GUI.skin.label.margin = ZeroMargin;
                        GUI.skin.label.fontSize = Mathf.RoundToInt(fontSize * 1.3f);
                        GUILayout.Label(GetLang("netpanel_title") + " v3.1.2", GUILayout.Height(GUI.skin.label.fontSize * 1.75f));

                        GUI.skin.label.fontSize = fontSize;
                        GUI.skin.label.alignment = alignment;
                        DrawPanelInfo();

                        bool inWorldSidePanel = Util.IsInWorld() && !UIMainMenu.IsOpen();
                        if (inWorldSidePanel)
                            DrawSettingsButton();
                        else
                            GUILayout.Label(" ");

                        DrawHostModerationLayoutButton();
                        DrawConnectedPlayersSection(inWorldSidePanel, fontSize);
                        DrawSidePanelFooter(screenRect, fontSize, wordWrap, stretchHeight);
                    }
                    finally
                    {
                        GUI.skin.label.fixedHeight = fixedHeight;
                        GUI.skin.label.fontSize = fontSize;
                        GUI.skin.label.stretchHeight = stretchHeight;
                        GUI.skin.label.alignment = alignment;
                        GUI.skin.label.wordWrap = wordWrap;
                        GUI.skin.label.margin = margin;
                        GUI.skin.label.normal.textColor = labelColor;
                        GUILayout.EndArea();
                    }
                }
                finally
                {
                    GUI.depth = oldDepth;
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Failed to draw patched MP side panel: " + ex.Message);
                return false;
            }
        }

        internal static void ToggleWindow()
        {
            _windowOpen = !_windowOpen;
            if (_windowOpen)
            {
                if (!_windowPositionInitialized)
                {
                    PositionWindowTopMiddle();
                    _windowPositionInitialized = true;
                }

                ClampWindowToScreen();
            }
        }

        internal static bool TryToggleWindow()
        {
            if (!IsModerationSessionAvailable())
                return false;

            ToggleWindow();
            return true;
        }

        private static void DrawPanelInfo()
        {
            string mode;
            if (KrokoshaScavMultiplayer.is_server)
                mode = KrokoshaScavMultiplayer.is_dedicated_server ? GetLang("netmode_server") : GetLang("netmode_host");
            else
                mode = GetLang("netmode_client");

            GUILayout.Label("   " + GetLang("netmode") + mode);

            if (KrokoshaScavMultiplayer.network_system_im_client_and_waiting_connecting)
            {
                GUILayout.Label(" ");
                return;
            }

            if (KrokoshaScavMultiplayer.is_server)
            {
                GUILayout.Label("   " + GetLang("netpanel_avgping") + Math.Round(ServerMain.AVERAGE_PING));
                return;
            }

            if (ScavPlayer.LocalPlayer != null)
            {
                GUILayout.Label("   " + GetLang("netpanel_ping") + ClientMain.LOCAL_PING);
                return;
            }

            GUILayout.Label(" ");
        }

        private static void DrawSettingsButton()
        {
            if (!GUILayout.Button(GetLang("netpanel_settings"), GUILayout.MinHeight(42f * GetMenuScale())))
                return;

            if (UIMainMenu.mainmenu_open && Util.IsInWorld())
            {
                Util.OpenBrightnessPanel(true);
                UIMainMenu.SetOpen(false);
            }
            else
            {
                UIMainMenu.SetOpen(!UIMainMenu.mainmenu_open);
                if (UIMainMenu.mainmenu_open)
                    UIMainMenu.current_tab = UIMainMenu.menutab_settings;
            }
        }

        private static void DrawHostModerationLayoutButton()
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled;

            try
            {
                if (GUILayout.Button("Host Moderation", GUILayout.MinHeight(42f * GetMenuScale())))
                {
                    ToggleWindow();
                    TryPlayMiniClick();
                }
            }
            finally
            {
                GUI.enabled = oldEnabled;
            }
        }

        private static void DrawHostModerationOverlayButton()
        {
            if (!Net.IsRunning || !IsAnyKrokMenuOpen())
                return;

            EnsureStyles();
            RefreshButtonStyle();

            Rect panelRect;
            if (!TryGetSidePanelContentRect(out panelRect))
                return;

            float scale = GetMenuScale();
            float margin = 8f * scale;
            float buttonHeight = Mathf.Max(42f, 40f * scale);
            float bottomReserved = Mathf.Max(116f, 116f * scale);
            float y = panelRect.yMax - bottomReserved - buttonHeight;
            y = Mathf.Max(panelRect.y + 148f * scale, y);
            Rect buttonRect = new Rect(panelRect.x + margin, y, panelRect.width - margin * 2f, buttonHeight);

            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled;

            try
            {
                if (GUI.Button(buttonRect, "Host Moderation", _buttonStyle))
                {
                    ToggleWindow();
                    TryPlayMiniClick();
                }
            }
            finally
            {
                GUI.enabled = oldEnabled;
            }

            CaptureInputForRect(buttonRect);
        }

        private static bool TryGetSidePanelContentRect(out Rect screenRect)
        {
            screenRect = Rect.zero;

            try
            {
                bool inWorld = Util.IsInWorld();
                Rect rect = UIBullshit.ScaleRect(
                    0f,
                    540f - UIMainMenu.PANEL_SIZE_Y * 0.5f,
                    UIMainMenu.PANEL_SIZE_X,
                    UIMainMenu.PANEL_SIZE_Y,
                    inWorld);

                screenRect = rect.ShrinkBorder((float)UIBullshit.uiBlockNanoBorderSize);
                return screenRect.width > 0f && screenRect.height > 0f;
            }
            catch
            {
                float scale = GetMenuScale();
                screenRect = new Rect(
                    0f,
                    Mathf.Max(0f, (Screen.height - 750f * scale) * 0.5f),
                    350f * scale,
                    750f * scale);
                return screenRect.width > 0f && screenRect.height > 0f;
            }
        }

        private static void DrawConnectedPlayersSection(bool inWorldSidePanel, int baseFontSize)
        {
            if (!Net.IsRunning || KrokoshaScavMultiplayer.network_system_im_client_and_waiting_connecting)
                return;

            GUILayout.BeginVertical();
            GUI.skin.label.fontSize = Mathf.RoundToInt(baseFontSize * 0.9f);

            try
            {
                GUILayout.BeginHorizontal();
                string connectedText = GetLang("netpanel_connectedplrs") +
                    GetPlayerEntries().Count + "/" +
                    KrokoshaScavMultiplayer.rules.PLAYER_COUNT_LIMIT;

                if (inWorldSidePanel)
                {
                    if (GUILayout.Button(connectedText, GUILayout.MinHeight(42f * GetMenuScale())))
                    {
                        UIMainMenu.SetOpen(true);
                        UIMainMenu.current_tab = UIMainMenu.menutab_directconnect;
                        TrySetConnectionSecondPanelToolbar(0);
                    }
                }
                else
                {
                    GUILayout.Label(connectedText);
                }

                GUILayout.EndHorizontal();

                _sidePanelScroll = GUILayout.BeginScrollView(_sidePanelScroll, GUILayout.ExpandHeight(true));
                foreach (ScavPlayer player in GetScavPlayersSnapshot())
                    DrawSidePanelPlayer(player);
                GUILayout.EndScrollView();
            }
            finally
            {
                GUI.skin.label.normal.textColor = Color.white;
                GUI.skin.label.fontSize = baseFontSize;
                GUILayout.EndVertical();
            }
        }

        private static void DrawSidePanelPlayer(ScavPlayer player)
        {
            if (player == null)
                return;

            float rowHeight = Mathf.Max(22f, GUI.skin.label.CalcSize(RowMeasure).y);
            GUILayout.BeginHorizontal(GUILayout.Height(rowHeight));

            try
            {
                Texture2D profile = player.IconAnySmallToLarge;
                if (profile != null)
                    GUILayout.Label(profile, GUILayout.Width(rowHeight), GUILayout.Height(rowHeight));

                GUI.skin.label.normal.textColor = player.playerColor;
                ulong clientId;
                string clientIdText = TryGetClientId(player, out clientId)
                    ? clientId.ToString(CultureInfo.InvariantCulture)
                    : "?";
                GUILayout.Label(clientIdText + ": " + (player.playerName ?? ""), GUILayout.ExpandWidth(true));
                GUILayout.Label("ping: " + player.ping_as_ms, GUILayout.Width(86f * GetMenuScale()));
            }
            finally
            {
                GUI.skin.label.normal.textColor = Color.white;
                GUILayout.EndHorizontal();
            }
        }

        private static void DrawSidePanelFooter(Rect screenRect, int baseFontSize, bool wordWrap, bool stretchHeight)
        {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.FlexibleSpace();

            if (Time.realtimeSinceStartupAsDouble - KrokoshaScavMultiplayer.last_multiplayer_status_message_change_time < 10.0)
            {
                GUI.skin.label.wordWrap = true;
                GUI.skin.label.stretchHeight = true;
                GUI.skin.label.fontSize = Mathf.RoundToInt(baseFontSize * 0.75f);
                GUILayout.Label(GetLang("netpanel_laststatus") + "\n" + KrokoshaScavMultiplayer.multiplayer_status_message);
                GUI.skin.label.wordWrap = wordWrap;
                GUI.skin.label.stretchHeight = stretchHeight;
            }

            GUILayout.FlexibleSpace();

            float width = screenRect.width * 0.7f;
            GUI.skin.label.fontSize = baseFontSize;
            GUI.skin.label.wordWrap = false;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            GUILayout.BeginHorizontal();
            GUILayout.Label("PACKET STABILITY: " + GetConnectionQualityText() + "%");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("SERVER TPS: " + ClientMain.SERVER_TPS, GUILayout.Width(width));
            GUILayout.Label("FPS: " + ClientMain.SERVER_FPS);
            GUILayout.EndHorizontal();

            if (Net.IsClient)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("LOCAL TPS: " + ServerMain.CURRENT_TPS, GUILayout.Width(width));
                GUILayout.Label("FPS: " + ServerMain.CURRENT_FPS);
                GUILayout.EndHorizontal();
            }
        }

        private static bool ShouldDrawUi()
        {
            return IsModerationSessionAvailable();
        }

        private static void DrawModerationWindow()
        {
            ClampWindowToScreen();
            _windowRect = GUI.Window(
                "CatPatchHostModeration".GetHashCode(),
                _windowRect,
                DrawWindowContents,
                "Host Moderation");
            CaptureInputForRect(_windowRect);
        }

        private static void DrawWindowContents(int windowId)
        {
            Rect rect = new Rect(8f, 22f, _windowRect.width - 16f, _windowRect.height - 30f);
            DrawCard(rect, false, true);

            Rect titleRect = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 64f);
            DrawCard(titleRect, true, true);
            GUI.Label(
                new Rect(titleRect.x + 12f, titleRect.y + 7f, titleRect.width - 190f, 22f),
                "Host Moderation",
                _titleStyle);

            string subtitle = IsSingleplayerSessionAvailable()
                ? "Singleplayer: actions affect the local player only."
                : IsHostSessionAvailable()
                    ? "Connected players: " + Math.Max(0, GetPlayerEntries().Count - 1)
                    : "Host a lobby to use moderation actions.";
            GUI.Label(
                new Rect(titleRect.x + 12f, titleRect.y + 31f, titleRect.width - 24f, 22f),
                subtitle,
                _subtitleStyle);

            Rect tabsRect = new Rect(rect.x + 8f, titleRect.yMax + 8f, 218f, rect.height - 120f);
            DrawCard(tabsRect, false, true);
            DrawTabs(tabsRect);

            Rect contentCard = new Rect(tabsRect.xMax + 8f, tabsRect.y, rect.xMax - tabsRect.xMax - 16f, tabsRect.height - 64f);
            DrawCard(contentCard, false, true);
            DrawTabContent(new Rect(contentCard.x + 10f, contentCard.y + 10f, contentCard.width - 20f, contentCard.height - 20f));

            Rect statusRect = new Rect(contentCard.x, contentCard.yMax + 8f, contentCard.width - 136f, 42f);
            if (Time.realtimeSinceStartupAsDouble < _statusUntil && !string.IsNullOrWhiteSpace(_statusText))
            {
                DrawCard(statusRect, _statusImportant, false);
                Color oldColor = _statusStyle.normal.textColor;
                _statusStyle.normal.textColor = _statusImportant
                    ? new Color(1f, 0.64f, 0.64f)
                    : new Color(0.72f, 0.92f, 0.72f);
                GUI.Label(new Rect(statusRect.x + 8f, statusRect.y + 9f, statusRect.width - 12f, 24f), _statusText, _statusStyle);
                _statusStyle.normal.textColor = oldColor;
            }

            if (DrawMpButton(new Rect(contentCard.xMax - 128f, contentCard.yMax + 8f, 128f, 50f), "Close", true))
                _windowOpen = false;

            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width - 110f, 20f));
        }

        private static void DrawTabs(Rect tabsRect)
        {
            float x = tabsRect.x + 8f;
            float y = tabsRect.y + 10f;
            float width = tabsRect.width - 16f;

            GUI.Label(new Rect(x, y, width, 32f), "Sections", _sectionHeaderStyle);
            y += 36f;

            for (int i = 0; i < TabLabels.Length; i++)
            {
                Rect rect = new Rect(x, y, width, 54f);
                if (_tab == (ModerationTab)i)
                {
                    DrawCard(rect, true, true);
                    GUI.Label(rect, TabLabels[i], _tabSelectedStyle);
                }
                else if (DrawMpButton(rect, TabLabels[i], true))
                {
                    _tab = (ModerationTab)i;
                    _scroll = Vector2.zero;
                }

                y += 60f;
            }

            y += 8f;
            GUI.Label(new Rect(x, y, width, 32f), "status", _sectionHeaderStyle);
            y += 34f;
            GUI.Label(
                new Rect(x, y, width, 36f),
                IsSingleplayerSessionAvailable() ? "Singleplayer" : IsHostSessionAvailable() ? "Working" : "Host only",
                _rowMetaStyle);
            y += 38f;
            GUI.Label(new Rect(x, y, width, 18f), "players: " + Math.Max(0, GetPlayerEntries().Count - 1), _rowMetaStyle);
        }

        private static void DrawTabContent(Rect contentRect)
        {
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(120f, contentRect.width - 44f), 2200f);
            _scroll = GUI.BeginScrollView(contentRect, _scroll, viewRect, false, true);
            float y = 0f;

            if (!IsModerationSessionAvailable())
            {
                GUI.Label(new Rect(8f, y + 6f, viewRect.width - 16f, 42f), "No access. Host a lobby to use this menu.", _rowMetaStyle);
                GUI.EndScrollView();
                return;
            }

            switch (_tab)
            {
                case ModerationTab.Actions:
                    DrawActionsTab(viewRect.width, ref y);
                    break;
                case ModerationTab.Players:
                    DrawPlayersTab(viewRect.width, ref y);
                    break;
                case ModerationTab.Mute:
                    DrawMuteTab(viewRect.width, ref y);
                    break;
                case ModerationTab.Sprays:
                    DrawSpraysTab(viewRect.width, ref y);
                    break;
                case ModerationTab.Bans:
                    DrawBansTab(viewRect.width, ref y);
                    break;
                case ModerationTab.MeowsFinalChallenge:
                    DrawMeowsFinalChallengeTab(viewRect.width, ref y);
                    break;
                default:
                    GUI.Label(new Rect(8f, y + 6f, viewRect.width - 16f, 42f), "This section will be rebuilt without the old backend dependencies.", _rowMetaStyle);
                    break;
            }

            GUI.EndScrollView();
        }

        private static void DrawActionsTab(float width, ref float y)
        {
            List<ModerationPlayerEntry> players = GetPlayerEntries();
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Player Actions", _sectionHeaderStyle);
            y += 36f;
            GUI.Label(new Rect(8f, y, width - 16f, 16f), "THE VOICES ARE WINNING TODAY!!!!!!!1", _rowMetaStyle);
            y += 20f;

            if (players.Count == 0)
            {
                GUI.Label(new Rect(8f, y, width - 16f, 20f), "No players connected.", _rowMetaStyle);
                y += 24f;
                return;
            }

            EnsureActionsSelection(players);
            ModerationPlayerEntry selected = GetSelectedActionsEntry(players);

            const float targetRowHeight = 64f;
            const float actionRowHeight = 50f;
            const float dropdownGap = 6f;
            Rect targetRect = new Rect(6f, y, width - 12f, targetRowHeight);
            DrawCard(targetRect, true, true);
            GUI.Label(new Rect(targetRect.x + 8f, targetRect.y + 22f, 58f, 20f), "Target", _rowMainStyle);
            string targetText = selected.ClientId + ": " + selected.Name;
            if (DrawActionButton(new Rect(targetRect.x + 64f, targetRect.y + 4f, Mathf.Max(180f, targetRect.width - 72f), targetRowHeight - 8f), targetText, true))
                _actionsTargetDropdownOpen = !_actionsTargetDropdownOpen;
            y += targetRowHeight + dropdownGap;

            if (_actionsTargetDropdownOpen)
            {
                float dropdownRowStep = actionRowHeight + 6f;
                float dropdownHeight = Mathf.Min(260f, 8f + players.Count * dropdownRowStep);
                Rect dropdownRect = new Rect(6f, y, width - 12f, dropdownHeight);
                DrawCard(dropdownRect, false, true);
                Rect viewRect = new Rect(0f, 0f, Mathf.Max(120f, dropdownRect.width - 44f), Mathf.Max(dropdownRect.height - 4f, players.Count * dropdownRowStep + 6f));
                _actionsDropdownScroll = GUI.BeginScrollView(
                    new Rect(dropdownRect.x + 2f, dropdownRect.y + 2f, dropdownRect.width - 4f, dropdownRect.height - 4f),
                    _actionsDropdownScroll,
                    viewRect,
                    false,
                    true);

                float rowY = 4f;
                for (int i = 0; i < players.Count; i++)
                {
                    ModerationPlayerEntry player = players[i];
                    Rect row = new Rect(4f, rowY, viewRect.width - 8f, actionRowHeight);
                    if (player.ClientId == _actionsSelectedClientId)
                        DrawCard(row, true, false);

                    string rowText = player.ClientId + ": " + player.Name + " (ping " + (player.PingMs >= 0 ? player.PingMs.ToString() : "?") + ")";
                    if (DrawActionButton(row, rowText, true))
                    {
                        _actionsSelectedClientId = player.ClientId;
                        _actionsTargetDropdownOpen = false;
                    }

                    rowY += dropdownRowStep;
                }

                GUI.EndScrollView();
                y += dropdownHeight + 8f;
            }

            Rect selectedRect = new Rect(6f, y, width - 12f, 52f);
            DrawCard(selectedRect, false, true);
            GUI.Label(new Rect(selectedRect.x + 8f, selectedRect.y + 7f, selectedRect.width - 16f, 100f), selected.ClientId + ": " + selected.Name, _rowMainStyle);
            GUI.Label(new Rect(selectedRect.x + 8f, selectedRect.y + 25f, selectedRect.width - 16f, 100f), "host command target", _rowMetaStyle);
            y += 58f;

            float gap = 8f;
            float buttonWidth = (width - 16f - gap * 3f) / 4f;
            float buttonHeight = actionRowHeight;

            DrawActionGridRow(width, ref y, selected, buttonWidth, buttonHeight, gap,
                new ActionButton("Tp To You", TryTeleportSelectedToHost, "tp_to_host"),
                new ActionButton("Tp To Them", TryTeleportHostToSelected, "tp_host_to_target"),
                new ActionButton("Heal/Revive", TryReviveSelected, "revive"),
                new ActionButton("Kill", TryKillSelected, "kill"));

            DrawActionGridRow(width, ref y, selected, buttonWidth, buttonHeight, gap,
                new ActionButton("Knock Out", TryKnockoutSelected, "knockout"),
                new ActionButton("Drop Inventory", TryDropInventorySelected, "drop_inventory"),
                new ActionButton("Explode", TryExplodeSelected, "explode"),
                new ActionButton("Obliterate", TryObliterateSelected, "obliterate"));

            DrawActionGridRow(width, ref y, selected, buttonWidth, buttonHeight, gap,
                new ActionButton("Mindwipe", TryMindwipeSelected, "mindwipe"),
                new ActionButton("Skin", TrySkinSelected, "skin"),
                new ActionButton("Stub toe", TryStubToeSelected, "stub_toe"),
                new ActionButton("Fling", TryFlingSelected, "fling"));

            DrawActionGridRow(width, ref y, selected, buttonWidth, buttonHeight, gap,
                new ActionButton("Last Stand", TryForceLastStandSelected, "last_stand"),
                new ActionButton("Transfer Host", TryTransferHostSelected, "transfer_host"),
                ActionButton.Empty,
                ActionButton.Empty);

            y += 6f;
        }

        private static void DrawPlayersTab(float width, ref float y)
        {
            List<ModerationPlayerEntry> players = GetPlayerEntries();
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Kick, Ban, Admin", _sectionHeaderStyle);
            y += 36f;

            if (players.Count == 0)
            {
                GUI.Label(new Rect(8f, y, width - 16f, 20f), "No players connected.", _rowMetaStyle);
                y += 24f;
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                ModerationPlayerEntry player = players[i];
                Rect row = new Rect(6f, y, width - 12f, 50f);
                DrawCard(row, false, true);

                string rowText = player.ClientId + ": " + player.Name + "  |  ping " + (player.PingMs >= 0 ? player.PingMs.ToString() : "?");
                GUI.Label(
                    new Rect(row.x + 8f, row.y + 16f, row.width - 316f, 18f),
                    rowText,
                    _rowMainStyle);

                bool canChangeAdmin = IsHostSessionAvailable() && player.ClientId > 0UL;
                bool canKick = IsHostSessionAvailable() && player.ClientId != 0UL && !IsLocalPlayer(player.ClientId);
                bool hasAdmin = HasAdminPrivilege(player.ClientId);

                Rect adminRect = new Rect(row.xMax - 304f, row.y + 4f, 96f, 42f);
                Rect kickRect = new Rect(row.xMax - 202f, row.y + 4f, 96f, 42f);
                Rect banRect = new Rect(row.xMax - 100f, row.y + 4f, 96f, 42f);

                if (DrawPlayerButton(adminRect, hasAdmin ? "Unadmin" : "Admin", canChangeAdmin))
                    TrySetAdminPrivilege(player.ClientId, !hasAdmin, player.Name);

                if (DrawPlayerButton(kickRect, "Kick", canKick))
                    TryKickPlayer(player.ClientId, player.Name);

                if (DrawPlayerButton(banRect, "Ban", canKick))
                    TryBanPlayer(player.ClientId, player.Name);
                y += 56f;
            }
        }

        private static void DrawBansTab(float width, ref float y)
        {
            List<BanList.BanEntry> entries = GetBanEntriesSnapshot();
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Banned Steam IDs", _sectionHeaderStyle);
            y += 36f;

            if (BanList.Instance == null)
            {
                GUI.Label(new Rect(8f, y, width - 16f, 36f), "Ban list is unavailable. Host a lobby first.", _rowMetaStyle);
                y += 40f;
                return;
            }

            if (entries.Count == 0)
            {
                GUI.Label(new Rect(8f, y, width - 16f, 18f), "None", _rowMetaStyle);
                y += 22f;
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                BanList.BanEntry entry = entries[i];
                if (entry == null)
                    continue;

                Rect row = new Rect(6f, y, width - 12f, 50f);
                DrawCard(row, false, true);

                string label = FormatBanEntry(entry);
                GUI.Label(new Rect(row.x + 8f, row.y + 8f, row.width - 128f, 18f), label, _rowMainStyle);

                string meta = entry.HasIP() ? "IP/name fallback ban" : "Steam ban";
                GUI.Label(new Rect(row.x + 8f, row.y + 26f, row.width - 128f, 16f), meta, _rowMetaStyle);

                if (DrawPlayerButton(new Rect(row.xMax - 112f, row.y + 4f, 104f, 42f), "Unban", true))
                {
                    bool removed = TryRemoveBanEntry(entry);
                    SetStatus(removed ? "Unbanned: " + label : "Failed to unban: " + label, !removed);
                }

                y += 56f;
            }
        }

        private static void DrawSpraysTab(float width, ref float y)
        {
            List<ModerationPlayerEntry> players = GetPlayerEntries();
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Spray Moderation", _sectionHeaderStyle);
            y += 36f;

            Rect settingsRect = new Rect(6f, y, width - 12f, 86f);
            DrawCard(settingsRect, true, false);
            GUI.Label(new Rect(settingsRect.x + 8f, settingsRect.y + 13f, 112f, 20f), "Mute minutes:", _rowMainStyle);
            _sprayMuteMinutes = GUI.TextField(new Rect(settingsRect.x + 122f, settingsRect.y + 8f, 72f, 32f), _sprayMuteMinutes ?? "10");

            if (DrawPlayerButton(new Rect(settingsRect.xMax - 126f, settingsRect.y + 6f, 118f, 42f), "Clear all", true))
            {
                string message;
                int cleared = SprayNetworkController.TryHostClearSprays(null, out message);
                SetStatus(cleared >= 0 ? message : "Clear failed: " + message, cleared < 0);
            }

            GUI.Label(new Rect(settingsRect.x + 8f, settingsRect.y + 54f, 114f, 20f), "Cooldown", _rowMainStyle);
            float cooldown = CatPatchServerSettings.SprayCooldownSeconds;
            Rect cooldownSliderRect = new Rect(settingsRect.x + 124f, settingsRect.y + 55f, Mathf.Max(120f, settingsRect.width - 198f), 22f);
            cooldown = DrawHorizontalSlider("spray_cooldown", cooldownSliderRect, cooldown, 0f, 120f);
            cooldown = Mathf.Round(cooldown);
            CatPatchServerSettings.SprayCooldownSeconds = cooldown;
            GUI.Label(new Rect(settingsRect.xMax - 66f, settingsRect.y + 54f, 58f, 20f), cooldown.ToString("0") + "s", _rowMainStyle);

            y += 94f;

            if (players.Count == 0)
            {
                GUI.Label(new Rect(8f, y, width - 16f, 20f), "No players connected.", _rowMetaStyle);
                y += 24f;
                return;
            }

            double muteMinutes;
            if (!double.TryParse((_sprayMuteMinutes ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out muteMinutes))
                muteMinutes = 10.0;

            muteMinutes = Math.Max(0.1, muteMinutes);

            for (int i = 0; i < players.Count; i++)
            {
                ModerationPlayerEntry player = players[i];
                int remainingSeconds = GetHostSprayMuteRemainingSeconds(player.ClientId);
                string status = remainingSeconds > 0 ? "Muted (" + remainingSeconds + "s)" : "Allowed";

                Rect row = new Rect(6f, y, width - 12f, 50f);
                DrawCard(row, false, true);
                GUI.Label(
                    new Rect(row.x + 8f, row.y + 16f, row.width - 292f, 18f),
                    player.ClientId + ": " + player.Name + "  |  " + status,
                    _rowMainStyle);

                Rect muteRect = new Rect(row.xMax - 280f, row.y + 4f, 86f, 42f);
                Rect unmuteRect = new Rect(row.xMax - 188f, row.y + 4f, 86f, 42f);
                Rect clearRect = new Rect(row.xMax - 96f, row.y + 4f, 88f, 42f);

                if (DrawPlayerButton(muteRect, "Mute", true))
                {
                    string message;
                    if (TrySetHostSprayMute(player.ClientId, muteMinutes, out message))
                        SetStatus(message, false);
                    else
                        SetStatus("Spray mute failed: " + message, true);
                }

                if (DrawPlayerButton(unmuteRect, "Unmute", true))
                {
                    string message;
                    if (TryClearHostSprayMute(player.ClientId, out message))
                        SetStatus(message, false);
                    else
                        SetStatus(message, true);
                }

                if (DrawPlayerButton(clearRect, "Clear", true))
                {
                    string message;
                    int cleared = SprayNetworkController.TryHostClearSprays(player.ClientId, out message);
                    SetStatus(cleared >= 0 ? message : "Spray clear failed: " + message, cleared < 0);
                }

                y += 56f;
            }
        }

        private static void DrawMeowsFinalChallengeTab(float width, ref float y)
        {
            CatPatchServerSettings.DogpileRadius = 1.00f;
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Dogpile Sleep", _sectionHeaderStyle);
            y += 38f;

            bool dogpileEnabled = CatPatchServerSettings.DogpileEnabled;
            bool newDogpileEnabled = DrawToggleRow(
                new Rect(8f, y, width - 16f, 38f),
                dogpileEnabled,
                "Sleeping on others gives good quality sleep?");
            if (newDogpileEnabled != dogpileEnabled)
                CatPatchServerSettings.DogpileEnabled = newDogpileEnabled;

            y += 44f;
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Piggyback Throw", _sectionHeaderStyle);
            y += 38f;

            bool piggybackThrowEnabled = CatPatchServerSettings.PiggybackThrowEnabled;
            bool newPiggybackThrowEnabled = DrawToggleRow(
                new Rect(8f, y, width - 16f, 38f),
                piggybackThrowEnabled,
                "Shove key throws the player on your back");
            if (newPiggybackThrowEnabled != piggybackThrowEnabled)
                CatPatchServerSettings.PiggybackThrowEnabled = newPiggybackThrowEnabled;
            y += 42f;

            DrawFloatSliderRow(
                width,
                ref y,
                "Throw strength",
                CatPatchServerSettings.PiggybackThrowStrength,
                5.0f,
                80.0f,
                1f,
                "",
                value => CatPatchServerSettings.PiggybackThrowStrength = value);
            y += 10f;

            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Meow's final challenge", _sectionHeaderStyle);
            y += 36f;
            GUI.Label(new Rect(8f, y, width - 16f, 18f), "This makes for any vile combo of traps.", _rowMetaStyle);
            y += 22f;

            bool challengeModeEnabled = CatPatchServerSettings.ChallengeModeEnabled;
            bool newChallengeModeEnabled = DrawToggleRow(
                new Rect(8f, y, width - 16f, 38f),
                challengeModeEnabled,
                "Enable challenge mode");
            if (newChallengeModeEnabled != challengeModeEnabled)
            {
                CatPatchServerSettings.ChallengeModeEnabled = newChallengeModeEnabled;
                if (newChallengeModeEnabled)
                    CatPatchServerSettings.SetAllChallengeTrapMultipliers(0.0f);
            }

            y += 42f;
            if (!newChallengeModeEnabled)
                return;

            DrawFloatSliderRow(
                width,
                ref y,
                "Trap amount",
                CatPatchServerSettings.ChallengeTrapDensity,
                0.1f,
                5.0f,
                10f,
                "x",
                value => CatPatchServerSettings.ChallengeTrapDensity = value);

            bool randomLandmineHeight = CatPatchServerSettings.ChallengeRandomLandmineHeight;
            bool newRandomLandmineHeight = DrawToggleRow(
                new Rect(8f, y, width - 16f, 38f),
                randomLandmineHeight,
                "Allow floating landmines");
            if (newRandomLandmineHeight != randomLandmineHeight)
                CatPatchServerSettings.ChallengeRandomLandmineHeight = newRandomLandmineHeight;
            y += 42f;

            bool randomSpikeOrientation = CatPatchServerSettings.ChallengeRandomSpikeOrientation;
            bool newRandomSpikeOrientation = DrawToggleRow(
                new Rect(8f, y, width - 16f, 38f),
                randomSpikeOrientation,
                "Random spike trap orientation");
            if (newRandomSpikeOrientation != randomSpikeOrientation)
                CatPatchServerSettings.ChallengeRandomSpikeOrientation = newRandomSpikeOrientation;
            y += 46f;

            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Hazard categories", _sectionHeaderStyle);
            y += 38f;

            for (int i = 0; i < MfcHazardCatalog.Categories.Length; i++)
                DrawMfcCategorySection(width, ref y, MfcHazardCatalog.Categories[i]);

            y += 8f;
            DrawMfcPresetSection(width, ref y);

            y += 8f;
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Starter kit item IDs", _sectionHeaderStyle);
            y += 36f;

            GUIStyle starterKitTextStyle = new GUIStyle(GUI.skin.textArea)
            {
                wordWrap = true
            };

            CatPatchServerSettings.ChallengeStarterKitItems = GUI.TextArea(
                new Rect(8f, y, width - 16f, 92f),
                CatPatchServerSettings.ChallengeStarterKitItems,
                starterKitTextStyle);
            y += 100f;
        }

        private static void DrawMfcCategorySection(float width, ref float y, MfcHazardCategory category)
        {
            bool expanded = _expandedMfcCategories.Contains(category);
            bool enabled = CatPatchServerSettings.IsChallengeCategoryEnabled(category);
            Rect row = new Rect(6f, y, width - 12f, 54f);
            DrawCard(row, false, true);

            string categoryName = MfcHazardCatalog.GetCategoryDisplayName(category);
            GUI.Label(
                new Rect(row.x + 10f, row.y + 8f, row.width - 280f, 20f),
                (expanded ? "v " : "> ") + categoryName,
                _rowMainStyle);
            GUI.Label(
                new Rect(row.x + 10f, row.y + 29f, row.width - 280f, 16f),
                enabled ? "Enabled" : "Disabled",
                _rowMetaStyle);

            Rect toggleRect = new Rect(row.xMax - 250f, row.y + 6f, 112f, 42f);
            Rect expandRect = new Rect(row.xMax - 130f, row.y + 6f, 122f, 42f);

            if (DrawPlayerButton(toggleRect, enabled ? "Disable" : "Enable", true))
            {
                CatPatchServerSettings.SetChallengeCategoryMultipliers(category, enabled ? 0.0f : 1.0f);
                if (category == MfcHazardCategory.Enemies)
                    CatPatchServerSettings.ChallengeElderThornbackCount = enabled ? 0 : 1;

                if (!enabled)
                    _expandedMfcCategories.Add(category);
            }

            if (DrawPlayerButton(expandRect, expanded ? "Hide" : "Show", true))
            {
                if (expanded)
                    _expandedMfcCategories.Remove(category);
                else
                    _expandedMfcCategories.Add(category);
            }

            y += 60f;
            if (!expanded)
                return;

            for (int i = 0; i < MfcHazardCatalog.Hazards.Length; i++)
            {
                MfcHazardDefinition hazard = MfcHazardCatalog.Hazards[i];
                if (hazard.Category != category)
                    continue;

                string trapId = hazard.Id;
                DrawFloatSliderRow(
                    width,
                    ref y,
                    hazard.DisplayName,
                    CatPatchServerSettings.GetChallengeTrapMultiplier(trapId),
                    0.0f,
                    10.0f,
                    10f,
                    "x",
                    value => CatPatchServerSettings.SetChallengeTrapMultiplier(trapId, value));
            }

            if (category == MfcHazardCategory.Enemies)
            {
                DrawIntSliderRow(
                    width,
                    ref y,
                    "Elder thornbacks",
                    CatPatchServerSettings.ChallengeElderThornbackCount,
                    0,
                    20,
                    value => CatPatchServerSettings.ChallengeElderThornbackCount = value);
            }

            y += 8f;
        }

        private static void DrawMfcPresetSection(float width, ref float y)
        {
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "MFC Presets", _sectionHeaderStyle);
            y += 36f;
            GUI.Label(new Rect(8f, y, width - 16f, 18f), "Export a setup or paste one here to import it.", _rowMetaStyle);
            y += 24f;

            Rect buttonRow = new Rect(6f, y, width - 12f, 48f);
            float buttonWidth = Mathf.Max(118f, (buttonRow.width - 18f) / 4f);
            if (DrawPlayerButton(new Rect(buttonRow.x, buttonRow.y + 3f, buttonWidth, 42f), "Export", true))
            {
                _mfcPresetText = CatPatchServerSettings.ExportChallengePreset();
                SetStatus("Exported MFC preset.", false);
            }

            if (DrawPlayerButton(new Rect(buttonRow.x + buttonWidth + 6f, buttonRow.y + 3f, buttonWidth, 42f), "Copy", !string.IsNullOrWhiteSpace(_mfcPresetText)))
            {
                GUIUtility.systemCopyBuffer = _mfcPresetText ?? "";
                SetStatus("Copied MFC preset.", false);
            }

            if (DrawPlayerButton(new Rect(buttonRow.x + (buttonWidth + 6f) * 2f, buttonRow.y + 3f, buttonWidth, 42f), "Import", true))
            {
                string message;
                bool ok = CatPatchServerSettings.TryImportChallengePreset(_mfcPresetText, out message);
                SetStatus(message, !ok);
            }

            if (DrawPlayerButton(new Rect(buttonRow.x + (buttonWidth + 6f) * 3f, buttonRow.y + 3f, buttonWidth, 42f), "Clear", true))
                _mfcPresetText = "";

            y += 52f;

            GUIStyle presetTextStyle = new GUIStyle(GUI.skin.textArea)
            {
                wordWrap = true
            };

            _mfcPresetText = GUI.TextArea(
                new Rect(8f, y, width - 16f, 82f),
                _mfcPresetText ?? "",
                presetTextStyle);
            y += 90f;
        }

        private static void DrawFloatSliderRow(
            float width,
            ref float y,
            string label,
            float value,
            float min,
            float max,
            float roundingMultiplier,
            string suffix,
            Action<float> setValue)
        {
            Rect row = new Rect(6f, y, width - 12f, 28f);
            GUI.Label(new Rect(row.x + 2f, row.y + 4f, 140f, 20f), label, _rowMainStyle);
            Rect sliderRect = new Rect(row.x + 148f, row.y + 7f, Mathf.Max(120f, row.width - 212f), 20f);
            float sliderValue = DrawHorizontalSlider("float_" + label, sliderRect, value, min, max);
            sliderValue = Mathf.Round(sliderValue * roundingMultiplier) / roundingMultiplier;
            setValue(sliderValue);
            string valueText = string.IsNullOrWhiteSpace(suffix)
                ? sliderValue.ToString("0.00")
                : sliderValue.ToString("0.0") + suffix;
            GUI.Label(new Rect(row.xMax - 54f, row.y + 4f, 52f, 20f), valueText, _rowMainStyle);
            y += 30f;
        }

        private static void DrawIntSliderRow(
            float width,
            ref float y,
            string label,
            int value,
            int min,
            int max,
            Action<int> setValue)
        {
            Rect row = new Rect(6f, y, width - 12f, 28f);
            GUI.Label(new Rect(row.x + 2f, row.y + 4f, 140f, 20f), label, _rowMainStyle);
            Rect sliderRect = new Rect(row.x + 148f, row.y + 7f, Mathf.Max(120f, row.width - 212f), 20f);
            int sliderValue = Mathf.RoundToInt(DrawHorizontalSlider("int_" + label, sliderRect, value, min, max));
            setValue(sliderValue);
            GUI.Label(new Rect(row.xMax - 54f, row.y + 4f, 52f, 20f), sliderValue.ToString("0"), _rowMainStyle);
            y += 30f;
        }

        private static bool DrawToggleRow(Rect rect, bool value, string label)
        {
            Rect boxRect = new Rect(rect.x + 2f, rect.y + 3f, 32f, 32f);
            Rect labelRect = new Rect(boxRect.xMax + 10f, rect.y + 8f, rect.width - boxRect.width - 18f, 22f);
            Rect screenRect = GUIUtility.GUIToScreenRect(rect);

            DrawCheckbox(boxRect, value);
            GUI.Label(labelRect, label, _rowMainStyle);
            TryRegisterCursorOverlap(screenRect);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                return !value;

            return value;
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

        private static void DrawSliderTrack(Rect sliderRect)
        {
            Rect trackRect = new Rect(sliderRect.x, sliderRect.y + 3f, sliderRect.width, 6f);
            Color oldColor = GUI.color;

            GUI.color = new Color(0.02f, 0.02f, 0.02f, 0.95f);
            GUI.DrawTexture(trackRect, Texture2D.whiteTexture);

            GUI.color = new Color(1f, 1f, 1f, 0.95f);
            GUI.DrawTexture(new Rect(trackRect.x, trackRect.y, trackRect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(trackRect.x, trackRect.yMax - 1f, trackRect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(trackRect.x, trackRect.y, 1f, trackRect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(trackRect.xMax - 1f, trackRect.y, 1f, trackRect.height), Texture2D.whiteTexture);

            GUI.color = oldColor;
        }

        private static float DrawHorizontalSlider(string key, Rect sliderRect, float value, float min, float max)
        {
            if (max <= min)
                return min;

            value = Mathf.Clamp(value, min, max);
            Rect hitRect = new Rect(sliderRect.x, sliderRect.y - 8f, sliderRect.width, sliderRect.height + 16f);
            Rect screenHitRect = GUIUtility.GUIToScreenRect(hitRect);
            TryRegisterCursorOverlap(screenHitRect);

            Event current = Event.current;
            Vector2 mousePosition = UIBullshit.GetCursorPosInGUI();
            bool mouseOver = screenHitRect.Contains(mousePosition);

            if (current != null && current.type == EventType.MouseDown && current.button == 0 && mouseOver)
            {
                _activeSliderKey = key;
                current.Use();
            }

            if (current != null && current.type == EventType.MouseUp && current.button == 0 && _activeSliderKey == key)
            {
                _activeSliderKey = null;
                current.Use();
            }

            if (Input.GetMouseButton(0) && _activeSliderKey == key)
            {
                float t = Mathf.InverseLerp(screenHitRect.x, screenHitRect.xMax, mousePosition.x);
                value = Mathf.Lerp(min, max, Mathf.Clamp01(t));
            }
            else if (!Input.GetMouseButton(0) && _activeSliderKey == key)
            {
                _activeSliderKey = null;
            }

            DrawSliderTrack(sliderRect);

            float normalized = Mathf.InverseLerp(min, max, value);
            float thumbX = Mathf.Lerp(sliderRect.x, sliderRect.xMax, normalized);
            Rect thumbRect = new Rect(thumbX - 6f, sliderRect.y - 1f, 12f, 16f);
            Color oldColor = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(thumbRect, Texture2D.whiteTexture);
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(thumbRect.x + 3f, thumbRect.y + 3f, thumbRect.width - 6f, thumbRect.height - 6f), Texture2D.whiteTexture);
            GUI.color = oldColor;

            return value;
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

        private static string FormatTrapName(string trapId)
        {
            return MfcHazardCatalog.GetDisplayName(trapId);
        }

        private static void DrawMuteTab(float width, ref float y)
        {
            List<ModerationPlayerEntry> players = GetPlayerEntries();
            GUI.Label(new Rect(8f, y, width - 16f, 34f), "Server Mutes", _sectionHeaderStyle);
            y += 36f;
            GUI.Label(new Rect(8f, y, width - 16f, 18f), "You can mute annoying people here", _rowMetaStyle);
            y += 22f;

            if (players.Count == 0)
            {
                GUI.Label(new Rect(8f, y, width - 16f, 20f), "No players connected.", _rowMetaStyle);
                y += 24f;
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                ModerationPlayerEntry player = players[i];
                bool chatMuted = IsClientServerChatMuted(player.ClientId);
                bool voiceMuted = IsClientServerVoiceMuted(player.ClientId);
                bool allMuted = chatMuted && voiceMuted;
                Rect row = new Rect(6f, y, width - 12f, 56f);
                DrawCard(row, false, true);

                GUI.Label(new Rect(row.x + 8f, row.y + 7f, row.width - 330f, 18f), player.ClientId + ": " + player.Name, _rowMainStyle);
                GUI.Label(
                    new Rect(row.x + 8f, row.y + 25f, row.width - 330f, 16f),
                    "chat: " + (chatMuted ? "muted" : "ok") + "   vc: " + (voiceMuted ? "muted" : "ok"),
                    _rowMetaStyle);

                Rect chatRect = new Rect(row.xMax - 318f, row.y + 6f, 100f, 42f);
                Rect voiceRect = new Rect(row.xMax - 214f, row.y + 6f, 100f, 42f);
                Rect allRect = new Rect(row.xMax - 110f, row.y + 6f, 100f, 42f);

                if (DrawPlayerButton(chatRect, chatMuted ? "Chat On" : "Chat Off", true))
                {
                    string message;
                    if (TrySetServerChatMuteFromHostUi(player.ClientId, !chatMuted, out message))
                        SetStatus(message, false);
                    else
                        SetStatus(string.IsNullOrWhiteSpace(message) ? "Mute action failed." : message, true);
                }

                if (DrawPlayerButton(voiceRect, voiceMuted ? "VC On" : "VC Off", true))
                {
                    string message;
                    if (TrySetServerVoiceMuteFromHostUi(player.ClientId, !voiceMuted, out message))
                        SetStatus(message, false);
                    else
                        SetStatus(string.IsNullOrWhiteSpace(message) ? "Voice mute action failed." : message, true);
                }

                if (DrawPlayerButton(allRect, allMuted ? "Unmute All" : "Mute All", true))
                {
                    string message;
                    if (TrySetServerMuteFromHostUi(player.ClientId, !allMuted, out message))
                        SetStatus(message, false);
                    else
                        SetStatus(string.IsNullOrWhiteSpace(message) ? "Mute action failed." : message, true);
                }

                y += 62f;
            }
        }

        private static bool DrawMpButton(Rect rect, string text, bool enabled)
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && enabled;

            try
            {
                return GUI.Button(rect, text ?? "", _buttonStyle) && enabled;
            }
            finally
            {
                GUI.enabled = oldEnabled;
            }
        }

        private static bool DrawActionButton(Rect rect, string text, bool enabled)
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && enabled;

            try
            {
                return GUI.Button(rect, text ?? "", _actionButtonStyle) && enabled;
            }
            finally
            {
                GUI.enabled = oldEnabled;
            }
        }

        private static bool DrawPlayerButton(Rect rect, string text, bool enabled)
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && enabled;

            try
            {
                return GUI.Button(rect, text ?? "", _playerButtonStyle) && enabled;
            }
            finally
            {
                GUI.enabled = oldEnabled;
            }
        }

        private static void DrawActionGridRow(
            float width,
            ref float y,
            ModerationPlayerEntry selected,
            float buttonWidth,
            float buttonHeight,
            float gap,
            ActionButton first,
            ActionButton second,
            ActionButton third,
            ActionButton fourth)
        {
            float x = 8f;
            DrawActionGridButton(new Rect(x, y, buttonWidth, buttonHeight), selected, first);
            x += buttonWidth + gap;
            DrawActionGridButton(new Rect(x, y, buttonWidth, buttonHeight), selected, second);
            x += buttonWidth + gap;
            DrawActionGridButton(new Rect(x, y, buttonWidth, buttonHeight), selected, third);
            x += buttonWidth + gap;
            DrawActionGridButton(new Rect(x, y, buttonWidth, buttonHeight), selected, fourth);
            y += buttonHeight + 6f;
        }

        private static void DrawActionGridButton(Rect rect, ModerationPlayerEntry selected, ActionButton button)
        {
            if (string.IsNullOrWhiteSpace(button.Label) || button.Action == null)
                return;

            if (DrawActionButton(rect, button.Label, true))
                RunAction(button.Action, selected, button.Tag);
        }

        private static void DrawCard(Rect rect, bool accent, bool showOutline)
        {
            Color color = GUI.color;
            GUI.color = accent ? new Color(0.055f, 0.11f, 0.095f, 0.99f) : new Color(0.018f, 0.022f, 0.03f, 0.99f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = Color.white;
            GUI.Box(rect, GUIContent.none);
            GUI.color = color;

            Color overlay = GUI.color;
            GUI.color = accent ? new Color(0.85f, 1f, 0.9f, 0.04f) : new Color(0f, 0f, 0f, 0.18f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = overlay;

            if (showOutline)
                DrawOutline(rect, accent ? new Color(0.52f, 0.82f, 0.6f, 0.45f) : new Color(0.63f, 0.72f, 0.82f, 0.28f), 1f);
        }

        private static void DrawOutline(Rect rect, Color color, float thickness)
        {
            if (rect.width <= 0f || rect.height <= 0f || thickness <= 0f)
                return;

            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = oldColor;
        }

        private static void CaptureInputForRect(Rect rect)
        {
            Event current = Event.current;
            if (current == null || !rect.Contains(current.mousePosition))
                return;

            switch (current.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                case EventType.KeyDown:
                case EventType.KeyUp:
                    current.Use();
                    break;
            }
        }

        private static List<ModerationPlayerEntry> GetPlayerEntries()
        {
            List<ModerationPlayerEntry> players = new List<ModerationPlayerEntry>();

            if (IsSingleplayerSessionAvailable())
            {
                Body localBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (localBody == null)
                    return players;

                players.Add(new ModerationPlayerEntry
                {
                    ClientId = 0UL,
                    Name = "Local Player",
                    PingMs = 0
                });
                return players;
            }

            try
            {
                foreach (ScavPlayer player in GetScavPlayersSnapshot())
                {
                    if (player == null)
                        continue;

                    ulong clientId;
                    if (!TryGetClientId(player, out clientId))
                        continue;

                    string name = StripRichTextTags(player.playerName ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        name = "Player " + clientId;

                    players.Add(new ModerationPlayerEntry
                    {
                        ClientId = clientId,
                        Name = name,
                        PingMs = RoundPingMs(player.ping)
                    });
                }
            }
            catch
            {
            }

            players.Sort((a, b) => a.ClientId.CompareTo(b.ClientId));
            return players;
        }

        private static List<ScavPlayer> GetScavPlayersSnapshot()
        {
            List<ScavPlayer> players = new List<ScavPlayer>();

            try
            {
                FieldInfo field = typeof(ScavPlayer).GetField(
                    "ClientIdToPlayerDict",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                object value = field != null ? field.GetValue(null) : null;
                IDictionary dictionary = value as IDictionary;
                if (dictionary != null)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        ScavPlayer player = entry.Value as ScavPlayer;
                        if (player != null && !players.Contains(player))
                            players.Add(player);
                    }
                }
            }
            catch
            {
            }

            if (players.Count == 0)
            {
                AddPlayersFromStaticList("AllLivingPlayers", players);
                AddPlayersFromStaticList("AllDeadPlayers", players);
            }

            return players;
        }

        private static void AddPlayersFromStaticList(string fieldName, List<ScavPlayer> players)
        {
            try
            {
                FieldInfo field = typeof(ScavPlayer).GetField(
                    fieldName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                IEnumerable enumerable = field != null ? field.GetValue(null) as IEnumerable : null;
                if (enumerable == null)
                    return;

                foreach (object entry in enumerable)
                {
                    ScavPlayer player = entry as ScavPlayer;
                    if (player != null && !players.Contains(player))
                        players.Add(player);
                }
            }
            catch
            {
            }
        }

        private static bool TryGetClientId(ScavPlayer player, out ulong clientId)
        {
            clientId = 0UL;
            if (player == null)
                return false;

            clientId = CatPatchNetId.ToUlong(player.playerId);
            return true;
        }

        private static bool TryConvertClientId(object value, out ulong clientId)
        {
            clientId = 0UL;
            if (value == null)
                return false;

            try
            {
                if (value is byte || value is ushort || value is uint || value is ulong ||
                    value is sbyte || value is short || value is int || value is long)
                {
                    clientId = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    return true;
                }

                FieldInfo idField = value.GetType().GetField(
                    "id",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (idField != null)
                {
                    clientId = Convert.ToUInt64(idField.GetValue(value), CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryBuildClientIdArgument(ulong clientId, Type targetType, out object argument)
        {
            argument = null;
            if (targetType == null)
                return false;

            try
            {
                if (targetType == typeof(uint))
                {
                    if (clientId > uint.MaxValue)
                        return false;

                    argument = (uint)clientId;
                    return true;
                }

                if (targetType == typeof(ushort))
                {
                    if (clientId > ushort.MaxValue)
                        return false;

                    argument = (ushort)clientId;
                    return true;
                }

                if (targetType == typeof(ulong))
                {
                    argument = clientId;
                    return true;
                }

                if (targetType.Name == "knetid")
                {
                    if (clientId > ushort.MaxValue)
                        return false;

                    argument = Activator.CreateInstance(targetType, (ushort)clientId);
                    return argument != null;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetNetPlayerByClientId(ulong clientId, out ScavPlayer player)
        {
            player = null;

            try
            {
                MethodInfo method = typeof(ScavPlayer).GetMethod(
                    "TryGetPlayerFromClientId",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (method == null)
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2)
                    return false;

                object clientIdArgument;
                if (!TryBuildClientIdArgument(clientId, parameters[0].ParameterType, out clientIdArgument))
                    return false;

                object[] args = { clientIdArgument, null };
                object result = method.Invoke(null, args);
                player = args[1] as ScavPlayer;
                return result is bool && (bool)result && player != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetNetPlayerAndBodyByClientId(ulong clientId, out ScavPlayer player, out Body body)
        {
            player = null;
            body = null;

            try
            {
                MethodInfo method = typeof(ScavPlayer).GetMethod(
                    "TryGetPlayerAndBodyFromClientId",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (method == null)
                    return false;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 3)
                    return false;

                object clientIdArgument;
                if (!TryBuildClientIdArgument(clientId, parameters[0].ParameterType, out clientIdArgument))
                    return false;

                object[] args = { clientIdArgument, null, null };
                object result = method.Invoke(null, args);
                player = args[1] as ScavPlayer;
                body = args[2] as Body;
                return result is bool && (bool)result && player != null && body != null;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureActionsSelection(List<ModerationPlayerEntry> players)
        {
            if (players == null || players.Count == 0)
            {
                _actionsSelectedClientId = 0UL;
                _actionsTargetDropdownOpen = false;
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].ClientId == _actionsSelectedClientId)
                    return;
            }

            _actionsSelectedClientId = players[0].ClientId;
        }

        private static ModerationPlayerEntry GetSelectedActionsEntry(List<ModerationPlayerEntry> players)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].ClientId == _actionsSelectedClientId)
                    return players[i];
            }

            return players[0];
        }

        private static void RunAction(Func<ulong, string, bool> action, ModerationPlayerEntry selected, string tag)
        {
            if (!IsModerationSessionAvailable())
            {
                SetStatus("Host only action.", true);
                return;
            }

            if (action == null)
                return;

            bool ok = false;
            try
            {
                ok = action(selected.ClientId, selected.Name ?? "");
            }
            catch (Exception ex)
            {
                SetStatus("Action failed: " + ex.Message, true);
            }

            if (!string.IsNullOrWhiteSpace(tag))
                Plugin.Logger.LogInfo("HostMod action " + tag + " target=" + selected.ClientId + " ok=" + ok);
        }

        private static bool TryGetTargetPlayer(ulong clientId, out ScavPlayer player, out Body body, out string error)
        {
            player = null;
            body = null;
            error = "";

            if (IsSingleplayerSessionAvailable())
            {
                body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null)
                {
                    error = "Local player body not found.";
                    return false;
                }

                return true;
            }

            if (clientId > uint.MaxValue)
            {
                error = "Player id is out of range.";
                return false;
            }

            if (!TryGetNetPlayerAndBodyByClientId(clientId, out player, out body) || player == null || body == null)
            {
                error = "Player body not found.";
                return false;
            }

            return true;
        }

        private static bool TryGetPlayer(ulong clientId, out ScavPlayer player, out string error)
        {
            player = null;
            error = "";

            if (!TryGetNetPlayerByClientId(clientId, out player) || player == null)
            {
                error = "Player not found.";
                return false;
            }

            return true;
        }

        private static bool IsLocalPlayer(ulong clientId)
        {
            try
            {
                ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
                ulong localClientId;
                return localPlayer != null && TryGetClientId(localPlayer, out localClientId) && localClientId == clientId;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasAdminPrivilege(ulong clientId)
        {
            ScavPlayer player;
            string error;
            if (!TryGetPlayer(clientId, out player, out error))
                return false;

            try
            {
                if (player.IsLocal)
                    return true;
            }
            catch
            {
            }

            try
            {
                return player.ServerPlayerState != null && player.ServerPlayerState.admin_privilege;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetAdminPrivilege(ulong clientId, bool enabled, string playerName)
        {
            if (!IsHostSessionAvailable())
            {
                SetStatus("Only host can change admin.", true);
                return false;
            }

            ScavPlayer player;
            string error;
            if (!TryGetPlayer(clientId, out player, out error))
            {
                SetStatus(error, true);
                return false;
            }

            try
            {
                if (player.IsLocal)
                {
                    SetStatus("Host admin cannot be changed.", true);
                    return false;
                }
            }
            catch
            {
            }

            string adminError;
            if (!AdminPrivilegeController.TrySetPrivilege(player, enabled, out adminError))
            {
                SetStatus("Admin toggle failed: " + adminError, true);
                return false;
            }

            string name = string.IsNullOrWhiteSpace(playerName) ? "Player " + clientId : playerName;
            SetStatus(
                enabled
                    ? "Granted admin to " + name + ". Admin mode activation requested."
                    : "Removed admin from " + name + ".",
                false);
            return true;
        }

        private static bool TryKickPlayer(ulong clientId, string playerName)
        {
            if (!IsHostSessionAvailable())
            {
                SetStatus("Host only action.", true);
                return false;
            }

            ScavPlayer player;
            string error;
            if (!TryGetPlayer(clientId, out player, out error))
            {
                SetStatus(error, true);
                return false;
            }

            try
            {
                if (player.IsLocal)
                {
                    SetStatus("Host cannot kick themselves.", true);
                    return false;
                }
            }
            catch
            {
            }

            player.Server_Kick("Kicked by host.");
            string name = string.IsNullOrWhiteSpace(playerName) ? "Player " + clientId : playerName;
            SetStatus("Kicked " + name + ".", false);
            return true;
        }

        private static bool TryBanPlayer(ulong clientId, string playerName)
        {
            if (!IsHostSessionAvailable())
            {
                SetStatus("Host only action.", true);
                return false;
            }

            if (BanList.Instance == null)
            {
                SetStatus("Ban list is unavailable.", true);
                return false;
            }

            ScavPlayer player;
            string error;
            if (!TryGetPlayer(clientId, out player, out error))
            {
                SetStatus(error, true);
                return false;
            }

            try
            {
                if (player.IsLocal)
                {
                    SetStatus("Host cannot ban themselves.", true);
                    return false;
                }
            }
            catch
            {
            }

            ulong steamId = player.SteamId;
            if (steamId == 0UL)
            {
                SetStatus("Cannot ban " + playerName + ": Steam ID unavailable.", true);
                return false;
            }

            string name = string.IsNullOrWhiteSpace(playerName) ? "Player " + clientId : playerName;
            BanList.Add("NULL", name, steamId);
            player.Server_Kick("Banned by host.");
            SetStatus("Banned " + name + ".", false);
            return true;
        }

        private static List<BanList.BanEntry> GetBanEntriesSnapshot()
        {
            List<BanList.BanEntry> entries = new List<BanList.BanEntry>();

            try
            {
                if (BanList.Instance == null || BanList.Entries == null)
                    return entries;

                for (int i = 0; i < BanList.Entries.Count; i++)
                {
                    BanList.BanEntry entry = BanList.Entries[i];
                    if (entry != null)
                        entries.Add(entry);
                }
            }
            catch
            {
            }

            return entries;
        }

        private static bool TryRemoveBanEntry(BanList.BanEntry entry)
        {
            if (entry == null || BanList.Instance == null)
                return false;

            try
            {
                if (entry.steamid != 0UL)
                    return BanList.RemoveBySteamId(entry.steamid);

                return BanList.Remove(entry);
            }
            catch
            {
                return false;
            }
        }

        private static string FormatBanEntry(BanList.BanEntry entry)
        {
            if (entry == null)
                return "";

            try
            {
                return entry.ToString();
            }
            catch
            {
                string text = "";
                if (entry.steamid != 0UL)
                    text += "SteamID:" + entry.steamid + "  ";

                if (!string.IsNullOrWhiteSpace(entry.ip) && entry.ip != "NULL")
                    text += "IP:" + entry.ip + "  ";

                return text + "Name:" + (entry.name ?? "");
            }
        }

        internal static bool IsClientServerChatMuted(ulong clientId)
        {
            return ServerChatMutedClientIds.Contains(clientId);
        }

        internal static bool IsClientServerVoiceMuted(ulong clientId)
        {
            return ServerVoiceMutedClientIds.Contains(clientId);
        }

        internal static bool ShouldBlockServerChatFromClient(uint clientId)
        {
            return IsHostSessionAvailable() && IsClientServerChatMuted(clientId);
        }

        internal static bool ShouldBlockServerChatFromClient(object clientId)
        {
            ulong parsedClientId;
            return TryConvertClientId(clientId, out parsedClientId) &&
                IsHostSessionAvailable() &&
                IsClientServerChatMuted(parsedClientId);
        }

        internal static bool ShouldBlockServerVoiceFromClient(uint clientId)
        {
            return IsHostSessionAvailable() && IsClientServerVoiceMuted(clientId);
        }

        internal static bool ShouldBlockServerVoiceFromClient(object clientId)
        {
            ulong parsedClientId;
            return TryConvertClientId(clientId, out parsedClientId) &&
                IsHostSessionAvailable() &&
                IsClientServerVoiceMuted(parsedClientId);
        }

        private static bool TrySetServerChatMuteFromHostUi(ulong clientId, bool muted, out string message)
        {
            return TrySetServerMuteSet(ServerChatMutedClientIds, clientId, muted, "Chat", out message);
        }

        private static bool TrySetServerVoiceMuteFromHostUi(ulong clientId, bool muted, out string message)
        {
            return TrySetServerMuteSet(ServerVoiceMutedClientIds, clientId, muted, "Voice", out message);
        }

        private static bool TrySetServerMuteFromHostUi(ulong clientId, bool muted, out string message)
        {
            string chatMessage;
            string voiceMessage;
            bool chatOk = TrySetServerChatMuteFromHostUi(clientId, muted, out chatMessage);
            bool voiceOk = TrySetServerVoiceMuteFromHostUi(clientId, muted, out voiceMessage);

            if (chatOk && voiceOk)
            {
                message = muted ? "Muted chat and voice for client " + clientId + "." : "Unmuted chat and voice for client " + clientId + ".";
                return true;
            }

            message = !chatOk ? chatMessage : voiceMessage;
            return false;
        }

        private static bool TrySetServerMuteSet(HashSet<ulong> set, ulong clientId, bool muted, string label, out string message)
        {
            message = "";

            if (!IsHostSessionAvailable())
            {
                message = "Host only action.";
                return false;
            }

            if (clientId == 0UL || IsLocalPlayer(clientId))
            {
                message = "Host cannot be muted.";
                return false;
            }

            if (muted)
                set.Add(clientId);
            else
                set.Remove(clientId);

            message = label + (muted ? " muted client " : " unmuted client ") + clientId + ".";
            return true;
        }

        internal static bool TrySetHostSprayMute(ulong clientId, double minutes, out string message)
        {
            message = "";

            if (!IsHostSessionAvailable())
            {
                message = "Host only.";
                return false;
            }

            minutes = Math.Max(0.1, minutes);
            MutedSprayUntilByClientId[clientId] = Time.realtimeSinceStartupAsDouble + minutes * 60.0;
            message = string.Format(CultureInfo.InvariantCulture, "Spray muted client {0} for {1:0.##} minute(s).", clientId, minutes);
            return true;
        }

        internal static bool TryClearHostSprayMute(ulong clientId, out string message)
        {
            message = "";

            if (!IsHostSessionAvailable())
            {
                message = "Host only.";
                return false;
            }

            if (MutedSprayUntilByClientId.Remove(clientId))
            {
                message = "Spray unmuted client " + clientId + ".";
                return true;
            }

            message = "Client " + clientId + " is not spray muted.";
            return false;
        }

        internal static int GetHostSprayMuteRemainingSeconds(ulong clientId)
        {
            double mutedUntil;
            if (!MutedSprayUntilByClientId.TryGetValue(clientId, out mutedUntil))
                return 0;

            double remaining = mutedUntil - Time.realtimeSinceStartupAsDouble;
            if (remaining <= 0.0)
            {
                MutedSprayUntilByClientId.Remove(clientId);
                return 0;
            }

            return Mathf.Max(1, Mathf.CeilToInt((float)remaining));
        }

        private static bool TryGetLocalHostPlayer(out ScavPlayer player, out Body body, out string error)
        {
            player = null;
            body = null;
            error = "";

            if (IsSingleplayerSessionAvailable())
            {
                body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null)
                {
                    error = "Local player body not found.";
                    return false;
                }

                return true;
            }

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
            {
                error = "Local host player not found.";
                return false;
            }

            ulong localClientId;
            if (!TryGetClientId(localPlayer, out localClientId))
            {
                error = "Local host player id not found.";
                return false;
            }

            return TryGetTargetPlayer(localClientId, out player, out body, out error);
        }

        private static bool TryTeleportSelectedToHost(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            ScavPlayer hostPlayer;
            Body hostBody;
            if (!TryGetLocalHostPlayer(out hostPlayer, out hostBody, out message))
            {
                SetStatus(message, true);
                return false;
            }

            if (player != null)
                player.Server_TeleportCharacter(hostBody.transform.position);
            else
                body.transform.position = hostBody.transform.position;
            SetStatus("Teleported " + name + " to you.", false);
            return true;
        }

        private static bool TryTeleportHostToSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            ScavPlayer hostPlayer;
            Body hostBody;
            if (!TryGetLocalHostPlayer(out hostPlayer, out hostBody, out message))
            {
                SetStatus(message, true);
                return false;
            }

            if (hostPlayer != null)
                hostPlayer.Server_TeleportCharacter(body.transform.position);
            else
                hostBody.transform.position = body.transform.position;
            SetStatus("Teleported you to " + name + ".", false);
            return true;
        }

        private static bool TryReviveSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            if (player != null)
                player.Server_ReviveCharacter(true);
            else
                body.ResetHealth(true);
            SetStatus("Revived " + name + ".", false);
            return true;
        }

        private static bool TryTransferHostSelected(ulong clientId, string name)
        {
            string message;
            bool ok = HostTransferController.TryOfferTransfer(clientId, name, out message);
            SetStatus(message, !ok);
            return ok;
        }

        private static bool TryKillSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            body.heartRate = 0f;
            body.temperature = 1000f;
            body.bloodOxygen = 0f;
            body.brainHealth = 0f;
            body.Ragdoll();
            SyncPlayerState(player);
            SetStatus("Killed " + name + ".", false);
            return true;
        }

        private static bool TryKnockoutSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            body.consciousness = 0f;
            body.Ragdoll();
            SyncPlayerState(player);
            SetStatus("Knocked out " + name + ".", false);
            return true;
        }

        private static bool TryDropInventorySelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            if (player != null)
                player.Server_DropAllInventory();
            else
                body.Body_DropAllItems();
            SetStatus("Dropped inventory for " + name + ".", false);
            return true;
        }

        private static bool TryExplodeSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            WorldGeneration.CreateExplosion(new ExplosionParams
            {
                position = body.transform.position
            });
            SetStatus("Boom on " + name + ".", false);
            return true;
        }

        private static bool TryObliterateSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            int count = 0;
            foreach (Limb limb in body.limbs)
            {
                if (limb == null || limb.dismembered)
                    continue;

                string limbName = (limb.fullName ?? "").ToLowerInvariant();
                if (!limbName.Contains("head") &&
                    !limbName.Contains("arm") &&
                    !limbName.Contains("hand") &&
                    !limbName.Contains("leg") &&
                    !limbName.Contains("foot"))
                    continue;

                limb.Dismember();
                count++;
            }

            SyncPlayerState(player);
            SetStatus("Obliterated " + name + " (" + count + " limbs).", false);
            return true;
        }

        private static bool TryMindwipeSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            try
            {
                LiquidType liquidType;
                if (Liquids.Registry != null && Liquids.Registry.TryGetValue("mindwipe", out liquidType) && liquidType != null)
                {
                    liquidType.onDrink(60f, body);
                }
                else if (!body.GetComponent<MindwipeScript>())
                {
                    body.gameObject.AddComponent<MindwipeScript>();
                }
            }
            catch
            {
                if (!body.GetComponent<MindwipeScript>())
                    body.gameObject.AddComponent<MindwipeScript>();
            }

            SyncPlayerState(player);
            SetStatus("Mindwiped " + name + ".", false);
            return true;
        }

        private static bool TrySkinSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            int count = 0;
            foreach (Limb limb in body.limbs)
            {
                if (limb == null || limb.dismembered)
                    continue;

                limb.skinHealth = 0f;
                count++;
            }

            SyncPlayerState(player);
            SetStatus("Skinned " + name + " (" + count + " limbs).", false);
            return true;
        }

        private static bool TryStubToeSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            Limb limb = FindLeftFootLimb(body);
            if (limb == null)
            {
                SetStatus("Left foot limb not found.", true);
                return false;
            }

            limb.BreakBone();
            limb.muscleHealth = 0f;
            limb.pain = Mathf.Max(100f, limb.pain);
            limb.skinHealth -= 20f;
            TryForceDislocateLimb(limb);
            body.Ragdoll();
            ForceRagdollForSeconds(body, 1.1f);
            SyncPlayerState(player);
            SetStatus("Stubbed toe for " + name + ".", false);
            return true;
        }

        private static bool TryFlingSelected(ulong clientId, string name)
        {
            ScavPlayer player;
            Body body;
            string message;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            float x = UnityEngine.Random.Range(-1f, 1f);
            float y = UnityEngine.Random.Range(0.9f, 1.8f);
            Vector2 direction = new Vector2(x, y);
            if (direction.sqrMagnitude < 0.001f)
                direction = new Vector2(0.2f, 1f);

            direction.Normalize();
            body.Ragdoll();
            ForceRagdollForSeconds(body, 2.4f);

            float force = UnityEngine.Random.Range(16f, 24f);
            if (body.rb != null)
            {
                body.rb.velocity = direction * force;
                body.rb.angularVelocity = UnityEngine.Random.Range(-220f, 220f);
            }

            foreach (Limb limb in body.limbs)
            {
                if (limb == null || limb.rb == null)
                    continue;

                Vector2 limbDirection = direction * UnityEngine.Random.Range(0.75f, 1.25f) + UnityEngine.Random.insideUnitCircle * 1.4f;
                limb.rb.velocity = limbDirection * force;
                limb.rb.angularVelocity += UnityEngine.Random.Range(-360f, 360f);
            }

            SyncPlayerState(player);
            SetStatus("Flung " + name + ".", false);
            return true;
        }

        private static bool TryForceLastStandSelected(ulong clientId, string name)
        {
            string message;
            if (!IsLastStandAllowed(out message))
            {
                SetStatus(message, true);
                return false;
            }

            ScavPlayer player;
            Body body;
            if (!TryGetTargetPlayer(clientId, out player, out body, out message))
            {
                SetStatus(message, true);
                return false;
            }

            if (body == null || !body.alive)
            {
                SetStatus(name + " cannot Last Stand right now.", true);
                return false;
            }

            Body_TryLastStand_MultiplayerPatch.ForceLastStand(body);
            SyncPlayerState(player);
            SetStatus("Forced Last Stand for " + name + ".", false);
            return true;
        }

        private static bool IsLastStandAllowed(out string message)
        {
            message = "";

            try
            {
                if (!KrokoshaScavMultiplayer.rules.LastStandAllowed)
                {
                    message = "Last Stand is disabled by the multiplayer rules.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                message = "Last Stand rule check failed: " + ex.Message;
                return false;
            }
        }

        private static Limb FindLeftFootLimb(Body body)
        {
            if (body == null || body.limbs == null)
                return null;

            Limb fallback = null;
            foreach (Limb limb in body.limbs)
            {
                if (limb == null || limb.dismembered)
                    continue;

                string fullName = ((limb.fullName ?? "") + " " + (limb.name ?? "")).ToLowerInvariant();
                if (string.Equals((limb.name ?? "").Trim(), "FootB", StringComparison.OrdinalIgnoreCase) || fullName.Contains("footb"))
                    return limb;

                if (fullName.Contains("left") && fullName.Contains("foot"))
                    return limb;

                if (fallback == null && fullName.Contains("foot"))
                    fallback = limb;
            }

            return fallback;
        }

        private static void TryForceDislocateLimb(Limb limb)
        {
            if (limb == null)
                return;

            try
            {
                MethodInfo method = limb.GetType().GetMethod("DislocateBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null && method.GetParameters().Length == 0)
                {
                    method.Invoke(limb, null);
                    return;
                }

                method = limb.GetType().GetMethod("Dislocate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null && method.GetParameters().Length == 0)
                {
                    method.Invoke(limb, null);
                    return;
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field = limb.GetType().GetField("boneDislocated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(limb, true);
                    return;
                }

                field = limb.GetType().GetField("dislocated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                    field.SetValue(limb, true);
            }
            catch
            {
            }
        }

        private static void ForceRagdollForSeconds(Body body, float seconds)
        {
            if (body == null || seconds <= 0f || BodyTimeRagdolledField == null)
                return;

            try
            {
                object value = BodyTimeRagdolledField.GetValue(body);
                float current = value is float ? (float)value : 0f;
                BodyTimeRagdolledField.SetValue(body, Mathf.Max(current, seconds));
            }
            catch
            {
            }
        }

        private static bool IsHostSessionAvailable()
        {
            try
            {
                return KrokoshaScavMultiplayer.network_system_is_running && KrokoshaScavMultiplayer.is_server;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSingleplayerSessionAvailable()
        {
            try
            {
                return Together.Plugin.FORCE_DISABLE_MP_MOD &&
                    !KrokoshaScavMultiplayer.network_system_is_running;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsModerationSessionAvailable()
        {
            return IsHostSessionAvailable() || IsSingleplayerSessionAvailable();
        }

        private static bool IsMultiplayerMenuAvailable()
        {
            try
            {
                return Net.IsRunning && IsAnyKrokMenuOpen();
            }
            catch
            {
                return false;
            }
        }

        private static void SyncPlayerState(ScavPlayer player)
        {
            if (player != null)
                player.Server_RemindPlayersCurrentState(false, true);
        }

        private static int RoundPingMs(double secondsValue)
        {
            if (secondsValue <= 0.0)
                return -1;

            return Mathf.Max(0, Mathf.RoundToInt((float)(secondsValue * 1000.0)));
        }

        private static string StripRichTextTags(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            char[] chars = value.ToCharArray();
            bool inTag = false;
            int write = 0;

            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }

                if (c == '>')
                {
                    inTag = false;
                    continue;
                }

                if (!inTag)
                    chars[write++] = c;
            }

            return new string(chars, 0, write);
        }

        private static void EnsureStyles()
        {
            if (_stylesReady)
                return;

            _stylesReady = true;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Normal,
                normal = { textColor = Color.white },
                alignment = TextAnchor.UpperLeft
            };

            _subtitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                normal = { textColor = new Color(0.82f, 0.88f, 0.95f) },
                alignment = TextAnchor.UpperLeft,
                wordWrap = true
            };

            _tabSelectedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleCenter
            };

            _sectionHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 21,
                fontStyle = FontStyle.Normal,
                normal = { textColor = new Color(0.92f, 0.96f, 1f) },
                alignment = TextAnchor.UpperLeft
            };

            _rowMainStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                alignment = TextAnchor.UpperLeft
            };

            _rowMetaStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.8f, 0.86f, 0.92f) },
                alignment = TextAnchor.UpperLeft,
                wordWrap = true
            };

            _statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                font = GUI.skin.button.font,
                fontSize = 14,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(12, 12, 6, 8),
                clipping = TextClipping.Clip
            };

            _actionButtonStyle = new GUIStyle(GUI.skin.button)
            {
                font = GUI.skin.button.font,
                fontSize = 14,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(5, 5, 2, 4),
                clipping = TextClipping.Clip
            };

            _playerButtonStyle = new GUIStyle(GUI.skin.button)
            {
                font = GUI.skin.button.font,
                fontSize = 13,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(5, 5, 2, 4),
                clipping = TextClipping.Clip
            };

            try
            {
                UIBullshit._GUI_SetButtonSkinTexture(_buttonStyle, true);
                UIBullshit._GUI_SetButtonSkinTexture(_actionButtonStyle, true);
                UIBullshit._GUI_SetButtonSkinTexture(_playerButtonStyle, true);
            }
            catch
            {
                _buttonSkinFailed = true;
            }

            SetButtonTextColor(_buttonStyle, Color.white);
            SetButtonTextColor(_actionButtonStyle, Color.white);
            SetButtonTextColor(_playerButtonStyle, Color.white);
            SyncButtonFonts();
        }

        private static void RefreshButtonStyle()
        {
            try
            {
                UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
                SetButtonTextColor(GUI.skin.button, Color.white);
            }
            catch
            {
            }

            if (_buttonSkinFailed)
                return;

            try
            {
                if (_buttonStyle != null)
                    UIBullshit._GUI_SetButtonSkinTexture(_buttonStyle, true);
                if (_actionButtonStyle != null)
                    UIBullshit._GUI_SetButtonSkinTexture(_actionButtonStyle, true);
                if (_playerButtonStyle != null)
                    UIBullshit._GUI_SetButtonSkinTexture(_playerButtonStyle, true);
            }
            catch
            {
                _buttonSkinFailed = true;
            }

            SetButtonTextColor(_buttonStyle, Color.white);
            SetButtonTextColor(_actionButtonStyle, Color.white);
            SetButtonTextColor(_playerButtonStyle, Color.white);
            SyncButtonFonts();
        }

        private static void SyncButtonFonts()
        {
            Font font = GUI.skin.button != null ? GUI.skin.button.font : null;
            if (font == null)
                return;

            if (_buttonStyle != null)
                _buttonStyle.font = font;

            if (_actionButtonStyle != null)
                _actionButtonStyle.font = font;

            if (_playerButtonStyle != null)
                _playerButtonStyle.font = font;
        }

        private static void SetButtonTextColor(GUIStyle style, Color color)
        {
            if (style == null)
                return;

            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        private static void PositionWindowTopMiddle()
        {
            _windowRect = new Rect(
                Mathf.Max(0f, (Screen.width - DefaultWindowRect.width) * 0.5f),
                DefaultWindowRect.y,
                DefaultWindowRect.width,
                DefaultWindowRect.height);
        }

        private static void CenterWindowIfOutsideScreen()
        {
            if (_windowRect.width <= 0f ||
                _windowRect.height <= 0f ||
                _windowRect.x < -_windowRect.width ||
                _windowRect.y < -_windowRect.height ||
                _windowRect.x > Screen.width ||
                _windowRect.y > Screen.height)
            {
                _windowRect = new Rect(
                    Mathf.Max(20f, (Screen.width - DefaultWindowRect.width) * 0.5f),
                    Mathf.Max(20f, (Screen.height - DefaultWindowRect.height) * 0.5f),
                    DefaultWindowRect.width,
                    DefaultWindowRect.height);
            }

            ClampWindowToScreen();
        }

        private static void ClampWindowToScreen()
        {
            float width = Mathf.Max(860f, Mathf.Min(Screen.width - 20f, _windowRect.width));
            float height = Mathf.Max(560f, Mathf.Min(Screen.height - 20f, _windowRect.height));
            _windowRect.width = width;
            _windowRect.height = height;
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - _windowRect.height));
        }

        private static bool IsAnyKrokMenuOpen()
        {
            try
            {
                return UIBullshit.IsAnyMenuOpen();
            }
            catch
            {
                return false;
            }
        }

        private static float GetMenuScale()
        {
            try
            {
                return Mathf.Clamp(UIMainMenu.GetMenuUIScale(), 0.5f, 3.0f);
            }
            catch
            {
                return 1.0f;
            }
        }

        private static void TrySetConnectionSecondPanelToolbar(int toolbarIndex)
        {
            try
            {
                FieldInfo field = typeof(UIMainMenu).GetField(
                    "_CONNECTIONSECONDPANEL_TOOLBAR",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (field != null)
                    field.SetValue(null, toolbarIndex);
            }
            catch
            {
            }
        }

        private static void TryPlayMiniClick()
        {
            try
            {
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick, 1f, 1f);
            }
            catch
            {
            }
        }

        private static void SetStatus(string message, bool important)
        {
            _statusText = (message ?? "").Trim();
            _statusImportant = important;
            _statusUntil = Time.realtimeSinceStartupAsDouble + 6.0;
        }

        private static string GetLang(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            try
            {
                return Lang.Get(key, false);
            }
            catch
            {
                switch (key)
                {
                    case "netpanel_title":
                        return "Multiplayer";
                    case "netmode":
                        return "Mode:";
                    case "netmode_host":
                        return "Host";
                    case "netmode_server":
                        return "Server";
                    case "netmode_client":
                        return "Client";
                    case "netpanel_avgping":
                        return "Average Ping:";
                    case "netpanel_ping":
                        return "Ping:";
                    case "netpanel_settings":
                        return "Settings";
                    case "netpanel_connectedplrs":
                        return "Connected Players:";
                    case "netpanel_laststatus":
                        return "Last Status:";
                    default:
                        return key;
                }
            }
        }

        private static string GetConnectionQualityText()
        {
            object value = Net.IsClient
                ? GetStaticFieldValue(typeof(ClientMain), "MY_CONNECTION_QUALITY")
                : GetStaticFieldValue(typeof(ServerMain), "AVG_CONNECTION_QUALITY");

            return value == null ? "100" : value.ToString();
        }

        private static object GetStaticFieldValue(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrWhiteSpace(fieldName))
                return null;

            try
            {
                System.Reflection.FieldInfo field = type.GetField(
                    fieldName,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);

                return field == null ? null : field.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private enum ModerationTab
        {
            Actions,
            Players,
            Mute,
            Sprays,
            Bans,
            MeowsFinalChallenge
        }

        private struct ModerationPlayerEntry
        {
            public ulong ClientId;
            public string Name;
            public int PingMs;
        }

        private struct ActionButton
        {
            public static readonly ActionButton Empty = new ActionButton("", null, "");

            public readonly string Label;
            public readonly Func<ulong, string, bool> Action;
            public readonly string Tag;

            public ActionButton(string label, Func<ulong, string, bool> action, string tag)
            {
                Label = label;
                Action = action;
                Tag = tag;
            }
        }
    }
}
