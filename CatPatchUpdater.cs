using BepInEx;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class CatPatchUpdater : MonoBehaviour
    {
        private const string DefaultManifestUrl = "https://raw.githubusercontent.com/SprightlyHawk8/CatPatch-Releases/main/manifest.json";
        private const int RequestTimeoutMs = 5000;
        private const double StartupCheckDelaySeconds = 2.0;

        private static readonly object Sync = new object();

        private CatPatchUpdateManifest _manifest;
        private string _status = "Checking for CatPatch updates...";
        private string _error;
        private string _downloadPath;
        private bool _isChecking;
        private bool _isDownloading;
        private bool _updateAvailable;
        private bool _downloadComplete;
        private bool _showStartupPopup;
        private bool _applyStarted;
        private bool _startupCheckStarted;
        private double _startupCheckAt;
        private Rect _popupRect;
        private Vector2 _popupScrollPosition;

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
                    return _isChecking || _isDownloading;
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

        public string DownloadPath
        {
            get
            {
                lock (Sync)
                    return _downloadPath;
            }
        }

        public bool DownloadComplete
        {
            get
            {
                lock (Sync)
                    return _downloadComplete;
            }
        }

        public bool CanApplyDownloadedUpdate
        {
            get
            {
                lock (Sync)
                    return _downloadComplete && _manifest != null && IsDllPackage(_manifest, _downloadPath);
            }
        }

        public static CatPatchUpdater Instance { get; private set; }

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
                if (_isChecking || _isDownloading)
                    return;

                _startupCheckStarted = true;
                _isChecking = true;
                _error = null;
                _status = "Checking for CatPatch updates...";
            }

            ThreadPool.QueueUserWorkItem(_ => CheckForUpdatesWorker(manifestUrl));
        }

        public void DownloadUpdate()
        {
            lock (Sync)
            {
                if (_isChecking || _isDownloading || !_updateAvailable || _manifest == null)
                    return;

                _isDownloading = true;
                _error = null;
                _downloadComplete = false;
                _applyStarted = false;
                _status = "Downloading CatPatch " + _manifest.LatestVersion + "...";
            }

            ThreadPool.QueueUserWorkItem(_ => DownloadUpdateWorker());
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
                    _downloadComplete = false;
                    _downloadPath = null;
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

        private void DownloadUpdateWorker()
        {
            CatPatchUpdateManifest manifest;
            lock (Sync)
                manifest = _manifest;

            try
            {
                string fileName = GetDownloadFileName(manifest.DownloadUrl, manifest.LatestVersion);
                string updateDir = Path.Combine(Paths.ConfigPath, "CatPatch", "Updates");
                Directory.CreateDirectory(updateDir);

                string tempPath = Path.Combine(updateDir, fileName + ".tmp");
                string finalPath = Path.Combine(updateDir, fileName);

                DownloadFile(manifest.DownloadUrl, tempPath);

                string actualHash = ComputeSha256(tempPath);
                string expectedHash = NormalizeHash(manifest.Sha256);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded update failed SHA-256 verification.");

                if (File.Exists(finalPath))
                    File.Delete(finalPath);

                File.Move(tempPath, finalPath);

                lock (Sync)
                {
                    _downloadPath = finalPath;
                    _downloadComplete = true;
                    _status = IsDllPackage(manifest, finalPath)
                        ? "CatPatch update downloaded. Click Apply & Quit to replace the DLL after the game closes."
                        : "CatPatch update downloaded. Close the game, then extract the BepInEx folder from the zip into your game folder.";
                }

                Plugin.Logger.LogInfo("CatPatch update downloaded and verified: " + finalPath);
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _error = ex.Message;
                    _downloadComplete = false;
                    _status = "CatPatch update download failed.";
                }

                Plugin.Logger.LogWarning("CatPatch update download failed: " + ex);
            }
            finally
            {
                lock (Sync)
                    _isDownloading = false;
            }
        }

        public void ApplyDownloadedUpdateAndQuit()
        {
            string sourcePath;
            CatPatchUpdateManifest manifest;

            lock (Sync)
            {
                if (_applyStarted || !_downloadComplete || _manifest == null || string.IsNullOrWhiteSpace(_downloadPath))
                    return;

                sourcePath = _downloadPath;
                manifest = _manifest;

                if (!IsDllPackage(manifest, sourcePath))
                {
                    _error = "Automatic replacement only supports direct CatPatch.dll downloads.";
                    return;
                }

                _applyStarted = true;
                _error = null;
                _status = "Starting CatPatch update helper...";
            }

            try
            {
                string helperPath = EnsureApplyHelperScript();
                string destinationPath = Assembly.GetExecutingAssembly().Location;
                string logPath = Path.Combine(Paths.ConfigPath, "CatPatch", "Updates", "CatPatchUpdateHelper.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteArgument(helperPath) +
                        " -ProcessId " + Process.GetCurrentProcess().Id +
                        " -SourcePath " + QuoteArgument(sourcePath) +
                        " -DestinationPath " + QuoteArgument(destinationPath) +
                        " -LogPath " + QuoteArgument(logPath),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(startInfo);

                lock (Sync)
                    _status = "CatPatch update helper started. Quitting game...";

                Plugin.Logger.LogInfo("CatPatch update helper started: " + helperPath);
                Application.Quit();
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _applyStarted = false;
                    _error = ex.Message;
                    _status = "Failed to start CatPatch update helper. All my hard work :(";
                }

                Plugin.Logger.LogWarning("Failed to start CatPatch update helper: " + ex);
            }
        }

        private void OnGUI()
        {
            CatPatchUpdateManifest manifest;
            string status;
            string error;
            string downloadPath;
            bool isBusy;
            bool downloadComplete;
            bool canApply;

            lock (Sync)
            {
                if (!_showStartupPopup || !_updateAvailable || _manifest == null)
                    return;

                manifest = _manifest;
                status = _status;
                error = _error;
                downloadPath = _downloadPath;
                isBusy = _isChecking || _isDownloading;
                downloadComplete = _downloadComplete;
                canApply = _downloadComplete && IsDllPackage(_manifest, _downloadPath);
            }

            ClampPopupToScreen();
            _popupRect = GUI.Window(
                "CatPatchUpdatePopup".GetHashCode(),
                _popupRect,
                _ => DrawUpdatePopup(manifest, status, error, downloadPath, isBusy, downloadComplete, canApply),
                "CatPatch Update Available");
        }

        private void DrawUpdatePopup(
            CatPatchUpdateManifest manifest,
            string status,
            string error,
            string downloadPath,
            bool isBusy,
            bool downloadComplete,
            bool canApply)
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

            if (downloadComplete)
            {
                GUILayout.Space(6f);
                GUILayout.Label("Downloaded to:", labelStyle);
                GUILayout.Label(downloadPath, labelStyle);
                GUILayout.Label(canApply
                    ? "Click Apply & Quit. CatPatch will close the game and the helper in BepInEx\\patchers will replace CatPatch.dll after the process exits."
                    : "Close the game, then extract the zip's BepInEx folder into the game folder.", labelStyle);
            }
            else
            {
                GUILayout.Space(6f);
                GUILayout.Label(IsDllPackage(manifest, null)
                    ? "This update downloads CatPatch.dll directly. After download, CatPatch can apply it after quitting the game."
                    : "This update package contains a BepInEx folder. After downloading, close the game and merge that folder into your game directory.", labelStyle);
            }

            GUILayout.EndScrollView();
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();

            GUI.enabled = !isBusy && !downloadComplete;
            if (GUILayout.Button("Download"))
                DownloadUpdate();

            GUI.enabled = !isBusy && canApply;
            if (GUILayout.Button("Apply & Quit"))
                ApplyDownloadedUpdateAndQuit();

            GUI.enabled = true;
            if (GUILayout.Button(downloadComplete ? "Close" : "Later"))
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

        private static void DownloadFile(string url, string path)
        {
            using (TimeoutWebClient client = CreateClient())
                client.DownloadFile(url, path);
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

            if (string.IsNullOrWhiteSpace(manifest.DownloadUrl))
                throw new InvalidDataException("Update manifest is missing download_url. :blush:");

            if (string.IsNullOrWhiteSpace(manifest.Sha256))
                throw new InvalidDataException("Update manifest is missing sha256. hmmmmm");
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

        private static string GetDownloadFileName(string downloadUrl, string latestVersion)
        {
            try
            {
                string fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
            catch
            {
            }

            return "CatPatch-" + latestVersion + ".zip";
        }

        private static bool IsDllPackage(CatPatchUpdateManifest manifest, string downloadPath)
        {
            if (manifest != null && string.Equals(manifest.PackageType, "dll", StringComparison.OrdinalIgnoreCase))
                return true;

            if (manifest != null && !string.IsNullOrWhiteSpace(manifest.FileName) && manifest.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(downloadPath) && downloadPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return true;

            if (manifest != null && !string.IsNullOrWhiteSpace(manifest.DownloadUrl))
            {
                try
                {
                    return new Uri(manifest.DownloadUrl).AbsolutePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                }
            }

            return false;
        }

        private static string EnsureApplyHelperScript()
        {
            string patchersDir = Path.Combine(Paths.BepInExRootPath, "patchers");
            Directory.CreateDirectory(patchersDir);

            string helperPath = Path.Combine(patchersDir, "CatPatchApplyUpdate.ps1");
            File.WriteAllText(helperPath, GetApplyHelperScript(), Encoding.UTF8);
            return helperPath;
        }

        private static string GetApplyHelperScript()
        {
            return
@"param(
    [int]$ProcessId,
    [string]$SourcePath,
    [string]$DestinationPath,
    [string]$LogPath
)

function Write-CatPatchLog {
    param([string]$Message)
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -LiteralPath $LogPath -Value ""[$stamp] $Message""
}

try {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    Write-CatPatchLog ""Waiting for game process $ProcessId to exit.""

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($process) {
        Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 700

    if (!(Test-Path -LiteralPath $SourcePath)) {
        throw ""Downloaded update was not found: $SourcePath""
    }

    $destinationDir = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null

    if (Test-Path -LiteralPath $DestinationPath) {
        Copy-Item -LiteralPath $DestinationPath -Destination ($DestinationPath + '.bak') -Force
    }

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
    Write-CatPatchLog ""Updated CatPatch.dll successfully.""
    exit 0
}
catch {
    Write-CatPatchLog ""Failed to update CatPatch.dll: $($_.Exception.Message)""
    exit 1
}
";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    builder.Append(hash[i].ToString("x2"));

                return builder.ToString();
            }
        }

        private static string NormalizeHash(string value)
        {
            return (value ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
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
