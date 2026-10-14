using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class SprayController : MonoBehaviour
    {
        private const float RadialOpenDelay = 0.28f;
        private const int SpraysPerPage = 5;
        private const double SprayFileRefreshIntervalSeconds = 1.0;

        private static readonly List<string> CachedSprayFiles = new List<string>();
        private static double _nextSprayFileRefreshAt;

        private readonly SprayVisualController _visualController = new SprayVisualController();
        private readonly Dictionary<string, Texture2D> _previewTextureCache = new Dictionary<string, Texture2D>();
        private readonly SprayUploadQueue _uploadQueue = new SprayUploadQueue();
        private SprayNetworkController _networkController;

        private bool _sprayKeyHeld;
        private bool _radialOpen;
        private float _sprayKeyDownAt;
        private int _radialPage;
        private Vector2 _sprayAnchorScreen;
        private double _nextAllowedSprayAt;
        private SpraySoundPlayer _soundPlayer;
        private string _cooldownPopupText = "";
        private double _cooldownPopupUntil;
        private string _lastSprayLayerKey = "";


        private static Texture2D CreateStaticTexture(byte[] bytes)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            if (!ImageConversion.LoadImage(texture, bytes))
            {
                Destroy(texture);
                return null;
            }

            return texture;
        }
        private bool TryGetPreviewTexture(string path, out Texture2D texture)
        {
            texture = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            if (_previewTextureCache.TryGetValue(path, out texture) && texture != null)
                return true;

            byte[] bytes = File.ReadAllBytes(path);

            Texture2D loadedTexture;

            if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                string error;
                if (!GifAnimationDecoder.TryCreateFirstFrameTexture(bytes, out loadedTexture, out error))
                {
                    Plugin.Logger.LogWarning("Failed to load GIF radial preview: " + error);
                    return false;
                }
            }
            else
            {
                loadedTexture = CreateStaticTexture(bytes);
            }

            if (loadedTexture == null)
                return false;

            loadedTexture.wrapMode = TextureWrapMode.Clamp;
            loadedTexture.filterMode = FilterMode.Bilinear;

            _previewTextureCache[path] = loadedTexture;
            texture = loadedTexture;
            return true;
        }



        private void Awake()
        {
            _soundPlayer = GetComponent<SpraySoundPlayer>();

            _networkController = new SprayNetworkController(_visualController);
            _networkController.RegisterHandlersOnce();
        }

        private void Update()
        {
            _networkController.Tick();
            _uploadQueue.Tick();
            _networkController.RegisterHandlersOnce();
            ClearSpraysOnLayerChange();

            _visualController.SetSpraysVisible(CatPatchClientSettings.ShowSprays);

            if (!CatPatchClientSettings.ShowSprays)
            {
                CancelSprayInput();
                return;
            }

            HandleSprayInput();
        }

        private void ClearSpraysOnLayerChange()
        {
            WorldGeneration world = WorldGeneration.world;
            if (world == null)
            {
                _lastSprayLayerKey = "";
                return;
            }

            string layerKey = BuildLayerKey(world);
            if (string.IsNullOrWhiteSpace(_lastSprayLayerKey))
            {
                _lastSprayLayerKey = layerKey;
                return;
            }

            if (string.Equals(_lastSprayLayerKey, layerKey, StringComparison.Ordinal))
                return;

            _lastSprayLayerKey = layerKey;
            CancelSprayInput();
            _networkController.ClearLayerState();

            int cleared = _visualController.ClearActiveSprays();
            if (cleared > 0)
                Plugin.Logger.LogInfo("Cleared " + cleared + " CatPatch spray(s) for new layer.");
        }

        private static string BuildLayerKey(WorldGeneration world)
        {
            return world.GetInstanceID()
                + ":"
                + world.totalTraveled
                + ":"
                + world.biomeDepth
                + ":"
                + world.amountOfLayers;
        }

        private void OnGUI()
        {
            if (!CatPatchClientSettings.ShowSprays)
                return;

            if (_radialOpen)
                DrawRadialMenu();

            DrawCooldownPopup();
        }

        private void HandleSprayInput()
        {
            if (CatPatchConsoleGuard.IsGameInputUiActive())
            {
                CancelSprayInput();
                return;
            }

            KeyCode sprayKey = CatPatchKeybinds.GetKeyCode(
                CatPatchKeybinds.SprayAction,
                CatPatchKeybinds.FallbackSprayKey);

            if (sprayKey == KeyCode.None)
            {
                CancelSprayInput();
                return;
            }

            if (Input.GetKeyDown(sprayKey))
            {
                _sprayKeyHeld = true;
                _sprayKeyDownAt = Time.unscaledTime;
                _sprayAnchorScreen = Input.mousePosition;
                return;
            }

            if (_sprayKeyHeld &&
                !_radialOpen &&
                Input.GetKey(sprayKey) &&
                Time.unscaledTime - _sprayKeyDownAt >= RadialOpenDelay &&
                GetAvailableSprayFiles().Count > 1)
            {
                _radialOpen = true;
                ClampRadialPage();
            }

            if (_radialOpen)
                HandleRadialPaging();

            if (!_sprayKeyHeld || !Input.GetKeyUp(sprayKey))
                return;

            if (_radialOpen)
            {
                string selectedPath;
                if (TryGetHoveredRadialSprayPath(out selectedPath))
                    SetSelectedSprayPath(selectedPath);

                CancelSprayInput();
                TryPlaceSelectedSpray(_sprayAnchorScreen);
                return;
            }

            CancelSprayInput();
            TryPlaceSelectedSpray(_sprayAnchorScreen);
        }

        private void CancelSprayInput()
        {
            _sprayKeyHeld = false;
            _radialOpen = false;
        }

        private void TryPlaceSelectedSpray(Vector2 screenPosition)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            bool useLocalCooldown = ShouldUseLocalSprayCooldown();
            if (useLocalCooldown && now < _nextAllowedSprayAt)
            {
                int remainingSeconds = Mathf.CeilToInt((float)(_nextAllowedSprayAt - now));
                ShowCooldownPopup(remainingSeconds);
                Plugin.Logger.LogInfo("Spray is on cooldown: " + remainingSeconds + "s remaining.");
                return;
            }

            string sprayPath = GetSelectedSprayPath();

            if (!File.Exists(sprayPath))
            {
                Plugin.Logger.LogWarning("No spray found at: " + sprayPath);
                return;
            }

            Vector2 position;
            if (!TryGetWorldPosition(screenPosition, out position))
                return;

            if (_soundPlayer != null) _soundPlayer.PlaySprayRequestSound();

            _nextAllowedSprayAt = useLocalCooldown
                ? now + CatPatchServerSettings.SprayCooldownSeconds
                : now;

            _uploadQueue.EnqueueFile(
                sprayPath,
                (sprayId, imageBytes, sourceFilePath) =>
                {
                    _networkController.SendOrSpawnSpray(
                        sprayId,
                        imageBytes,
                        position,
                        sourceFilePath);
                },
                error =>
                {
                    ShowSprayUploadFailure(error);
                });
        }

        private static bool ShouldUseLocalSprayCooldown()
        {
            try
            {
                return !KrokoshaScavMultiplayer.network_system_is_running ||
                    KrokoshaScavMultiplayer.is_server;
            }
            catch
            {
                return true;
            }
        }

        private static void ShowSprayUploadFailure(string error)
        {
            string message = "Spray failed to upload.";

            if (!string.IsNullOrWhiteSpace(error) &&
                error.IndexOf("backend_unreachable", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = "Spray failed: backend unreachable. Check DNS, VPN, firewall, or internet.";
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                message = "Spray failed: " + error;
            }

            HugController.ShowLocalMessage(message);
        }

        private void ShowCooldownPopup(int remainingSeconds)
        {
            _cooldownPopupText = "Spray cooldown: " + remainingSeconds + "s remaining";
            _cooldownPopupUntil = Time.realtimeSinceStartupAsDouble + 2.0;
        }

        private void DrawCooldownPopup()
        {
            if (string.IsNullOrWhiteSpace(_cooldownPopupText))
                return;

            if (Time.realtimeSinceStartupAsDouble >= _cooldownPopupUntil)
            {
                _cooldownPopupText = "";
                return;
            }

            GUIStyle style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
                wordWrap = true
            };

            float width = 300f;
            float height = 42f;

            Rect rect = new Rect(
                Screen.width * 0.5f - width * 0.5f,
                Screen.height * 0.78f,
                width,
                height);

            GUI.Box(rect, _cooldownPopupText, style);
        }

        private static bool TryGetWorldPosition(Vector2 screenPosition, out Vector2 position)
        {
            position = Vector2.zero;

            if (Camera.main == null)
                return false;

            Vector3 world = Camera.main.ScreenToWorldPoint(screenPosition);
            position = new Vector2(world.x, world.y);
            return true;
        }

        private static string GetSprayDirectory()
        {
            return Path.Combine(Paths.ConfigPath, "Cat-Patch", "sprays");
        }

        private static List<string> GetAvailableSprayFiles()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextSprayFileRefreshAt)
                return CachedSprayFiles;

            _nextSprayFileRefreshAt = now + SprayFileRefreshIntervalSeconds;
            Directory.CreateDirectory(GetSprayDirectory());

            CachedSprayFiles.Clear();

            string[] paths = Directory.GetFiles(GetSprayDirectory());
            for (int i = 0; i < paths.Length; i++)
            {
                string extension = Path.GetExtension(paths[i]);

                if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    CachedSprayFiles.Add(paths[i]);
                }
            }

            CachedSprayFiles.Sort(StringComparer.OrdinalIgnoreCase);
            return CachedSprayFiles;
        }

        private static string GetSelectedSprayPath()
        {
            List<string> files = GetAvailableSprayFiles();

            if (files.Count == 0)
                return "";

            string selectedFile = PlayerPrefs.GetString("cat_patch_lastspray", "");

            for (int i = 0; i < files.Count; i++)
            {
                if (string.Equals(Path.GetFileName(files[i]), selectedFile, StringComparison.OrdinalIgnoreCase))
                    return files[i];
            }

            return files[0];
        }

        private static void SetSelectedSprayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            PlayerPrefs.SetString("cat_patch_lastspray", Path.GetFileName(path));
            PlayerPrefs.Save();
        }

        private void HandleRadialPaging()
        {
            int pageCount = GetRadialPageCount(GetAvailableSprayFiles().Count);
            if (pageCount <= 1)
            {
                _radialPage = 0;
                return;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) < 0.01f)
                return;

            _radialPage += scroll < 0f ? 1 : -1;

            if (_radialPage < 0)
                _radialPage = pageCount - 1;

            if (_radialPage >= pageCount)
                _radialPage = 0;
        }

        private void OnDestroy()
        {
            foreach (Texture2D texture in _previewTextureCache.Values)
            {
                if (texture != null)
                    Destroy(texture);
            }

            _previewTextureCache.Clear();
        }
        private void ClampRadialPage()
        {
            int pageCount = GetRadialPageCount(GetAvailableSprayFiles().Count);
            _radialPage = pageCount <= 0 ? 0 : Mathf.Clamp(_radialPage, 0, pageCount - 1);
        }

        private static int GetRadialPageCount(int count)
        {
            if (count <= 0)
                return 0;

            return Mathf.CeilToInt(count / (float)SpraysPerPage);
        }

        private static List<string> GetRadialPage(List<string> allSprays, int page)
        {
            List<string> result = new List<string>();

            int start = Mathf.Max(0, page) * SpraysPerPage;
            int end = Mathf.Min(allSprays.Count, start + SpraysPerPage);

            for (int i = start; i < end; i++)
                result.Add(allSprays[i]);

            return result;
        }

        private void DrawRadialMenu()
        {
            List<string> files = GetAvailableSprayFiles();
            if (files.Count == 0)
                return;

            ClampRadialPage();

            Vector2 center = ToGuiPoint(_sprayAnchorScreen);
            List<string> page = GetRadialPage(files, _radialPage);

            GUI.Box(
                new Rect(center.x - 48f, center.y - 17f, 96f, 34f),
                (_radialPage + 1) + "/" + GetRadialPageCount(files.Count));

            string hoveredPath;
            TryGetHoveredRadialSprayPath(out hoveredPath);

            for (int i = 0; i < page.Count; i++)
            {
                Rect rect = GetRadialRect(center, i, page.Count);

                UnityEngine.Color oldColor = GUI.color;

                if (string.Equals(page[i], hoveredPath, StringComparison.OrdinalIgnoreCase))
                    GUI.color = new UnityEngine.Color(1f, 0.95f, 0.55f, 1f);

                GUI.Box(rect, "");

                Texture2D previewTexture;
                if (TryGetPreviewTexture(page[i], out previewTexture))
                {
                    GUI.DrawTexture(
                        new Rect(rect.x + 4f, rect.y + 4f, rect.width - 8f, rect.height - 8f),
                        previewTexture,
                        ScaleMode.ScaleToFit,
                        true);
                }
                else
                {
                    GUI.Label(rect, GetDisplayName(page[i]));
                }

                GUI.color = oldColor;
            }
        }

        private bool TryGetHoveredRadialSprayPath(out string path)
        {
            path = null;

            List<string> files = GetAvailableSprayFiles();
            if (files.Count == 0)
                return false;

            ClampRadialPage();

            List<string> page = GetRadialPage(files, _radialPage);
            Vector2 center = ToGuiPoint(_sprayAnchorScreen);
            Vector2 mouse = ToGuiPoint(Input.mousePosition);

            for (int i = 0; i < page.Count; i++)
            {
                if (GetRadialRect(center, i, page.Count).Contains(mouse))
                {
                    path = page[i];
                    return true;
                }
            }

            return false;
        }

        private static Rect GetRadialRect(Vector2 center, int index, int count)
        {
            float angle = ((float)index / Mathf.Max(1f, count) * 360f - 90f) * Mathf.Deg2Rad;
            Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 80f;

            return new Rect(
                center.x + offset.x - 35f,
                center.y + offset.y - 20f,
                70f,
                40f);
        }

        private static Vector2 ToGuiPoint(Vector2 screenPoint)
        {
            return new Vector2(screenPoint.x, Screen.height - screenPoint.y);
        }

        private static string GetDisplayName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? "spray";

            if (name.Length <= 12)
                return name;

            return name.Substring(0, 11) + ".";
        }
    }
}
