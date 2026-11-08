using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;
using Together;

namespace CatPatchspace
{
    internal sealed class CatPatchUpdater : MonoBehaviour
    {
        private const string DefaultManifestUrl = "https://raw.githubusercontent.com/SprightlyHawk8/CatPatch-Releases/main/manifest.json";
        internal const string NexusDownloadsUrl = "https://www.nexusmods.com/scavprototype/mods/69?tab=files";
        private const int RequestTimeoutMs = 5000;
        private const double StartupCheckDelaySeconds = 2.0;

        private static readonly object Sync = new object();

        private CatPatchUpdateManifest _manifest;
        private string _status = "Checking for CatPatch updates...";
        private string _error;
        private bool _isChecking;
        private bool _updateAvailable;
        private bool _showStartupPopup;
        private bool _startupCheckStarted;
        private double _startupCheckAt;
        private Rect _popupRect;
        private Vector2 _popupScrollPosition;
        private GUISkin _popupSkin;

        public bool UpdateAvailable
        {
            get
            {
                lock (Sync)
                    return _updateAvailable;
            }
        }

        public bool IsBusy
        {
            get
            {
                lock (Sync)
                    return _isChecking;
            }
        }

        public string Status
        {
            get
            {
                lock (Sync)
                    return _status;
            }
        }

        public string Error
        {
            get
            {
                lock (Sync)
                    return _error;
            }
        }

        public string LatestVersion
        {
            get
            {
                lock (Sync)
                    return _manifest != null ? _manifest.LatestVersion : null;
            }
        }

        public static CatPatchUpdater Instance { get; private set; }

        internal static bool IsPopupOpen
        {
            get
            {
                var updater = Instance;
                if (updater == null || !updater.isActiveAndEnabled)
                    return false;
                lock (Sync)
                    return updater._showStartupPopup && updater._updateAvailable && updater._manifest != null;
            }
        }

        internal static bool IsPointerOverPopup()
        {
            return IsPopupOpen && Instance._popupRect.Contains(CatPatchUiInput.GetLiveGuiPointerPosition());
        }

        internal static bool TryGetCaptureRect(out Rect rect)
        {
            rect = Instance != null ? Instance._popupRect : default;
            return IsPopupOpen;
        }

        internal static void BringPopupToFront()
        {
            if (IsPopupOpen)
                GUI.BringWindowToFront("CatPatchUpdatePopup".GetHashCode());
        }

        private void Awake()
        {
            Instance = this;

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
            }
        }

        private void Start()
        {
            _startupCheckAt = Time.realtimeSinceStartupAsDouble + StartupCheckDelaySeconds;
            _popupRect = CreateDefaultPopupRect();
        }

        private void OnDestroy()
        {
            if (_popupSkin != null)
                Destroy(_popupSkin);
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!_startupCheckStarted && Time.realtimeSinceStartupAsDouble >= _startupCheckAt)
            {
                _startupCheckStarted = true;
                ForceCheckNow();
            }
        }

        public void ForceCheckNow()
        {
            string manifestUrl = CatPatchClientSettings.UpdateManifestUrl;

            lock (Sync)
            {
                if (_isChecking)
                    return;

                _startupCheckStarted = true;
                _isChecking = true;
                _error = null;
                _status = "Checking for CatPatch updates...";
            }

            ThreadPool.QueueUserWorkItem(_ => CheckForUpdatesWorker(manifestUrl));
        }

        public static void OpenNexusDownloads()
        {
            Application.OpenURL(NexusDownloadsUrl);
        }

        public List<string> GetChangelog()
        {
            lock (Sync)
            {
                if (_manifest == null || _manifest.Changelog == null)
                    return new List<string>();

                return new List<string>(_manifest.Changelog);
            }
        }

        private void CheckForUpdatesWorker(string manifestUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(manifestUrl))
                    throw new InvalidDataException("Update manifest URL is empty. Meow failed, kill him with sticks");

                string json = DownloadString(manifestUrl);
                CatPatchUpdateManifest manifest = JsonConvert.DeserializeObject<CatPatchUpdateManifest>(json);
                ValidateManifest(manifest);

                bool available = IsRemoteVersionNewer(manifest.LatestVersion, Plugin.ModVersion);

                lock (Sync)
                {
                    _manifest = manifest;
                    _updateAvailable = available;
                    if (available)
                        _showStartupPopup = true;
                    _status = available
                        ? "CatPatch " + manifest.LatestVersion + " is available :)"
                        : "CatPatch is up to date.";
                }

                Plugin.Logger.LogInfo("CatPatch updater check complete. Latest=" + manifest.LatestVersion + ", current=" + Plugin.ModVersion + ", available=" + available);
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _error = ex.Message;
                    _updateAvailable = false;
                    _showStartupPopup = false;
                    _status = "CatPatch update check unavailable. :( all my hard work";
                }

                Plugin.Logger.LogWarning("CatPatch update check failed: " + ex);
            }
            finally
            {
                lock (Sync)
                    _isChecking = false;
            }
        }

        private void OnGUI()
        {
            if (UIBullshit.main != null && UIBullshit.main.isActiveAndEnabled)
                return;
            DrawOverlay();
        }

        internal static void DrawOverlayFromKrokMenu()
        {
            if (Instance != null && Instance.isActiveAndEnabled)
                Instance.DrawOverlay();
        }

        private void DrawOverlay()
        {
            CatPatchUpdateManifest manifest;
            string status;
            string error;

            lock (Sync)
            {
                if (!_showStartupPopup || !_updateAvailable || _manifest == null)
                    return;

                manifest = _manifest;
                status = _status;
                error = _error;
            }

            int oldDepth = GUI.depth;
            bool oldEnabled = GUI.enabled;
            GUISkin oldSkin = GUI.skin;
            try
            {
                // This entry runs before Together applies its MP skin. Keep a private
                // copy so this updater continues using the original IMGUI controls.
                if (_popupSkin == null)
                    _popupSkin = Instantiate(oldSkin);
                GUI.skin = _popupSkin;
                GUI.depth = -31000;
                GUI.enabled = true;
                ClampPopupToScreen();
                _popupRect = GUI.Window(
                    "CatPatchUpdatePopup".GetHashCode(),
                    _popupRect,
                    _ => DrawUpdatePopup(manifest, status, error),
                    "CatPatch Update Available");
                GUI.BringWindowToFront("CatPatchUpdatePopup".GetHashCode());
                CatPatchUiInput.CaptureUpdaterInput(_popupRect);
            }
            finally
            {
                GUI.depth = oldDepth;
                GUI.enabled = oldEnabled;
                GUI.skin = oldSkin;
            }
        }

        private void DrawUpdatePopup(
            CatPatchUpdateManifest manifest,
            string status,
            string error)
        {
            GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true
            };

            GUILayout.BeginVertical();

            GUILayout.Label("CatPatch " + manifest.LatestVersion + " is available. You currently have " + Plugin.ModVersion + ".", labelStyle);
            GUILayout.Space(6f);

            float scrollHeight = Mathf.Max(170f, _popupRect.height - 175f);
            _popupScrollPosition = GUILayout.BeginScrollView(_popupScrollPosition, GUILayout.Height(scrollHeight));

            if (manifest.Changelog != null && manifest.Changelog.Count > 0)
            {
                GUILayout.Label("Changelog", labelStyle);
                for (int i = 0; i < manifest.Changelog.Count; i++)
                    GUILayout.Label("- " + manifest.Changelog[i], labelStyle);

                GUILayout.Space(6f);
            }

            GUILayout.Label(status, labelStyle);

            if (!string.IsNullOrWhiteSpace(error))
                GUILayout.Label("Error: " + error, labelStyle);

            GUILayout.Space(6f);
            GUILayout.Label("Download the update from Nexus Mods, then close the game and install it manually.", labelStyle);

            GUILayout.EndScrollView();
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Download on Nexus"))
                OpenNexusDownloads();

            if (GUILayout.Button("Later"))
                DismissStartupPopup();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.enabled = true;

            GUI.DragWindow(new Rect(0f, 0f, _popupRect.width, 24f));
        }

        private void DismissStartupPopup()
        {
            lock (Sync)
                _showStartupPopup = false;
            CatPatchUiInput.HoldUpdaterClosingInput(_popupRect);
        }

        private static Rect CreateDefaultPopupRect()
        {
            float width = Mathf.Min(720f, Screen.width - 40f);
            float height = Mathf.Min(520f, Screen.height - 40f);
            return new Rect(
                (Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f,
                width,
                height);
        }

        private void ClampPopupToScreen()
        {
            if (_popupRect.width <= 0f || _popupRect.height <= 0f)
                _popupRect = CreateDefaultPopupRect();

            _popupRect.width = Mathf.Min(Mathf.Max(_popupRect.width, 520f), Screen.width - 20f);
            _popupRect.height = Mathf.Min(Mathf.Max(_popupRect.height, 360f), Screen.height - 20f);
            _popupRect.x = Mathf.Clamp(_popupRect.x, 10f, Screen.width - _popupRect.width - 10f);
            _popupRect.y = Mathf.Clamp(_popupRect.y, 10f, Screen.height - _popupRect.height - 10f);
        }

        private static string DownloadString(string url)
        {
            using (TimeoutWebClient client = CreateClient())
                return client.DownloadString(url);
        }

        private static TimeoutWebClient CreateClient()
        {
            TimeoutWebClient client = new TimeoutWebClient(RequestTimeoutMs);
            client.Headers[HttpRequestHeader.UserAgent] = "CatPatch/" + Plugin.ModVersion;
            client.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return client;
        }

        private static void ValidateManifest(CatPatchUpdateManifest manifest)
        {
            if (manifest == null)
                throw new InvalidDataException("Update manifest was empty. Uh i dunno lol");

            if (string.IsNullOrWhiteSpace(manifest.LatestVersion))
                throw new InvalidDataException("Update manifest is missing latest_version. thats weird");
        }

        private static bool IsRemoteVersionNewer(string remoteVersion, string currentVersion)
        {
            Version remote = ParseVersion(remoteVersion);
            Version current = ParseVersion(currentVersion);
            return remote.CompareTo(current) > 0;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new Version(0, 0, 0);

            string normalized = value.Trim().TrimStart('v', 'V');
            Version version;
            if (Version.TryParse(normalized, out version))
                return version;

            return new Version(0, 0, 0);
        }

        public static string DefaultUrl
        {
            get { return DefaultManifestUrl; }
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;

            public TimeoutWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                    request.Timeout = _timeoutMs;

                HttpWebRequest httpRequest = request as HttpWebRequest;
                if (httpRequest != null)
                {
                    httpRequest.ReadWriteTimeout = _timeoutMs;
                    httpRequest.KeepAlive = false;
                }

                return request;
            }
        }
    }
}
