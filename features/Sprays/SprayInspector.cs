using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class SprayInspector : MonoBehaviour
    {
        private readonly Dictionary<string, GifAnimationData> _gifAnimationCache =
            new Dictionary<string, GifAnimationData>();

        private readonly HashSet<string> _failedGifCacheKeys =
            new HashSet<string>();

        private SprayInstance _hoveredSpray;
        private SprayInstance _previousHoveredSpray;
        private SprayInstance _previewSpray;
        private readonly List<SprayInstance> _sprayCache = new List<SprayInstance>();
        private int _sprayCacheVersion = -1;
        private double _previewStartedAt;
        private string _downloadStatus = "";
        private double _downloadStatusUntil;
        private static Font _inspectFont;
        private GUIStyle _sprayPreviewTitleStyle;
        private GUIStyle _sprayPreviewButtonStyle;

        private void Awake()
        {
            Plugin.Logger.LogInfo("SprayInspector loaded.");
        }

        private void Update()
        {
            if (!CatPatchClientSettings.ShowSprays)
            {
                ClosePreview();
                ClearHoverHighlight();
                return;
            }

            if (_previewSpray != null && Input.GetKeyDown(KeyCode.Escape))
            {
                ClosePreview();
                return;
            }

            UpdateHoveredSpray();
            ApplyHoverHighlight();
            HandleInspectInput();
        }

        private void OnDisable()
        {
            ClosePreview();
            ClearHoverHighlight();
        }

        private void OnDestroy()
        {
            foreach (GifAnimationData animation in _gifAnimationCache.Values)
                animation?.DestroyTextures();

            _gifAnimationCache.Clear();
            _failedGifCacheKeys.Clear();
        }

        private void OnGUI()
        {
            if (!CatPatchClientSettings.ShowSprays)
                return;

            if (_previewSpray == null || _previewSpray.Renderer == null)
                return;

            Sprite sprite = _previewSpray.Renderer.sprite;
            if (sprite == null || sprite.texture == null)
                return;

            Texture2D previewTexture = GetPreviewTexture(_previewSpray, sprite.texture);
            if (previewTexture == null)
                return;

            EnsureSprayPreviewStyles();

            float sourceWidth = Mathf.Max(1f, previewTexture.width);
            float sourceHeight = Mathf.Max(1f, previewTexture.height);
            if (previewTexture == sprite.texture)
            {
                sourceWidth = Mathf.Max(1f, sprite.textureRect.width);
                sourceHeight = Mathf.Max(1f, sprite.textureRect.height);
            }

            float maxImageWidth = Mathf.Max(120f, Screen.width * 0.74f);
            float maxImageHeight = Mathf.Max(120f, Screen.height * 0.68f);
            float imageScale = Mathf.Min(maxImageWidth / sourceWidth, maxImageHeight / sourceHeight);
            if (float.IsNaN(imageScale) || float.IsInfinity(imageScale) || imageScale <= 0f)
                imageScale = 1f;

            float imageWidth = sourceWidth * imageScale;
            float imageHeight = sourceHeight * imageScale;
            float panelWidth = imageWidth + 28f;
            float panelHeight = imageHeight + 126f;
            Rect rect = new Rect(
                (Screen.width - panelWidth) * 0.5f,
                (Screen.height - panelHeight) * 0.5f,
                panelWidth,
                panelHeight);

            DrawSprayPreviewPanel(rect);
            CaptureInputForRect(rect);

            GUI.Label(
                new Rect(rect.x + 12f, rect.y + 10f, rect.width - 88f, 22f),
                "Spray Preview",
                _sprayPreviewTitleStyle);

            Color oldBackground = GUI.backgroundColor;
            GUI.backgroundColor = Color.white;

            try
            {
                if (GUI.Button(
                        new Rect(rect.xMax - 98f, rect.y + 6f, 86f, 42f),
                        "Close",
                        _sprayPreviewButtonStyle))
                {
                    ClosePreview();
                    return;
                }

                Rect imageRect = new Rect(rect.x + 14f, rect.y + 54f, imageWidth, imageHeight);
                DrawSpritePreviewTexture(imageRect, previewTexture, sprite);

                if (GUI.Button(
                        new Rect(rect.x + 14f, rect.yMax - 52f, 126f, 42f),
                        "Download",
                        _sprayPreviewButtonStyle))
                {
                    DownloadPreviewSpray(_previewSpray);
                }

                DrawDownloadStatus(rect);
            }
            finally
            {
                GUI.backgroundColor = oldBackground;
            }

            Event current = Event.current;
            if (current != null &&
                current.type == EventType.MouseDown &&
                current.button == 0 &&
                !rect.Contains(current.mousePosition))
            {
                ClosePreview();
                current.Use();
            }
        }

        private void DrawDownloadStatus(Rect rect)
        {
            if (string.IsNullOrWhiteSpace(_downloadStatus) ||
                Time.realtimeSinceStartupAsDouble >= _downloadStatusUntil)
            {
                return;
            }

            GUIStyle statusStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                font = GetInspectFont(),
                fontSize = 13,
                normal = { textColor = Color.white },
                wordWrap = true
            };

            GUI.Label(
                new Rect(rect.x + 150f, rect.yMax - 52f, rect.width - 164f, 42f),
                _downloadStatus,
                statusStyle);
        }

        private void EnsureSprayPreviewStyles()
        {
            if (_sprayPreviewTitleStyle == null)
            {
                _sprayPreviewTitleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    font = GetInspectFont(),
                    fontSize = 14,
                    fontStyle = FontStyle.Normal,
                    normal = { textColor = Color.white },
                    clipping = TextClipping.Clip
                };
            }

            if (_sprayPreviewButtonStyle == null)
            {
                _sprayPreviewButtonStyle = CreateKrokStyleButton();
                _sprayPreviewButtonStyle.fontSize = 14;
                _sprayPreviewButtonStyle.fontStyle = FontStyle.Normal;
                _sprayPreviewButtonStyle.padding = new RectOffset(5, 5, 4, 6);
                _sprayPreviewButtonStyle.clipping = TextClipping.Clip;
            }

        }

        private static void DrawSprayPreviewPanel(Rect rect)
        {
            Color oldColor = GUI.color;
            GUI.color = Color.white;
            GUI.Box(rect, GUIContent.none);
            GUI.color = oldColor;

            Rect inner = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f);
            Color oldGuiColor = GUI.color;
            GUI.color = new Color(0.03f, 0.09f, 0.055f, 0.94f);
            GUI.DrawTexture(inner, Texture2D.whiteTexture);
            GUI.color = oldGuiColor;
        }

        private static void DrawSpritePreviewTexture(Rect position, Texture2D texture, Sprite sprite)
        {
            if (texture == null)
                return;

            if (sprite != null && texture == sprite.texture)
            {
                Rect texCoords = new Rect(
                    sprite.textureRect.x / texture.width,
                    sprite.textureRect.y / texture.height,
                    sprite.textureRect.width / texture.width,
                    sprite.textureRect.height / texture.height);

                GUI.DrawTextureWithTexCoords(position, texture, texCoords, true);
                return;
            }

            GUI.DrawTexture(position, texture, ScaleMode.ScaleToFit, true);
        }

        private static void CaptureInputForRect(Rect rect)
        {
            Event current = Event.current;
            if (current == null || !rect.Contains(current.mousePosition))
                return;

            if (current.type == EventType.MouseDrag ||
                current.type == EventType.ScrollWheel ||
                current.type == EventType.ContextClick)
            {
                current.Use();
            }
        }

        private void HandleInspectInput()
        {
            if (CatPatchConsoleGuard.IsGameInputUiActive())
                return;

            if (!CatPatchKeybinds.GetKeyDown(
                    CatPatchKeybinds.SprayInspectAction,
                    CatPatchKeybinds.FallbackSprayInspectKey))
            {
                return;
            }

            if (_previewSpray != null)
            {
                ClosePreview();
                return;
            }

            if (_hoveredSpray == null)
                return;

            _previewSpray = _hoveredSpray;
            _previewStartedAt = Time.realtimeSinceStartupAsDouble;
        }

        private Texture2D GetPreviewTexture(SprayInstance spray, Texture2D fallbackTexture)
        {
            GifAnimationData animation;
            if (!TryGetGifAnimation(spray, out animation))
                return fallbackTexture;

            return animation.GetFrame(Time.realtimeSinceStartupAsDouble - _previewStartedAt) ?? fallbackTexture;
        }

        private bool TryGetGifAnimation(SprayInstance spray, out GifAnimationData animation)
        {
            animation = null;

            if (spray == null)
                return false;

            string cacheKey = BuildGifCacheKey(spray);
            if (string.IsNullOrWhiteSpace(cacheKey) || _failedGifCacheKeys.Contains(cacheKey))
                return false;

            if (_gifAnimationCache.TryGetValue(cacheKey, out animation))
                return animation != null && animation.IsValid;

            string error;
            if (!TryLoadGifAnimation(spray, out animation, out error))
            {
                _failedGifCacheKeys.Add(cacheKey);
                Plugin.Logger.LogWarning("[Sprays] stage=inspect.decode id=" + spray.SprayId + " failed: " + error);
                return false;
            }

            if (animation == null || !animation.IsValid)
            {
                _failedGifCacheKeys.Add(cacheKey);
                return false;
            }

            _gifAnimationCache[cacheKey] = animation;
            Plugin.Logger.LogInfo("[Sprays] stage=inspect.decode id=" + spray.SprayId + " ok frames=" + animation.Frames.Length);
            return true;
        }

        private static bool TryLoadGifAnimation(
            SprayInstance spray,
            out GifAnimationData animation,
            out string error)
        {
            if (GifAnimationDecoder.IsGifBytes(spray.ImageBytes))
                return GifAnimationDecoder.TryLoad(spray.ImageBytes, out animation, out error);

            if ((spray.ImageBytes == null || spray.ImageBytes.Length == 0) && IsLocalGifPath(spray.SourceFilePath))
                return GifAnimationDecoder.TryLoadFromFile(spray.SourceFilePath, out animation, out error);

            animation = null;
            error = "Spray does not have local or downloaded GIF data.";
            return false;
        }

        private static bool IsLocalGifPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildGifCacheKey(SprayInstance spray)
        {
            if (spray == null)
                return "";

            // Inspect the same bytes used by the wall sprite, including locally
            // placed sprays. File extensions and later disk edits are not its format.
            if (spray.ImageBytes != null && spray.ImageBytes.Length > 0)
                return GifAnimationDecoder.IsGifBytes(spray.ImageBytes)
                    ? "bytes|" + (spray.SprayId ?? "") + "|" + spray.ImageBytes.Length
                    : "";

            string path = spray.SourceFilePath;
            if (IsLocalGifPath(path))
            {
                try
                {
                    return "file|" + path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
                }
                catch
                {
                    return "file|" + path;
                }
            }

            return "";
        }

        private void ClosePreview()
        {
            _previewSpray = null;
            _previewStartedAt = 0.0;
            _downloadStatus = "";
            _downloadStatusUntil = 0.0;
        }

        private void DownloadPreviewSpray(SprayInstance spray)
        {
            string error;
            string savedPath;
            if (!TrySaveSprayToFolder(spray, out savedPath, out error))
            {
                SetDownloadStatus(error);
                Plugin.Logger.LogWarning("Spray download from inspect failed: " + error);
                return;
            }

            string message = "Saved spray to " + Path.GetFileName(savedPath) + ".";
            SetDownloadStatus(message);
            HugController.ShowLocalMessage(message);
        }

        private void SetDownloadStatus(string message)
        {
            _downloadStatus = string.IsNullOrWhiteSpace(message) ? "" : message;
            _downloadStatusUntil = Time.realtimeSinceStartupAsDouble + 4.0;
        }

        private static bool TrySaveSprayToFolder(SprayInstance spray, out string savedPath, out string error)
        {
            savedPath = "";
            error = "";

            byte[] bytes;
            string extension;
            if (!TryGetDownloadBytes(spray, out bytes, out extension, out error))
                return false;

            string sprayDirectory = GetSprayDirectory();
            Directory.CreateDirectory(sprayDirectory);

            string baseName = BuildDownloadBaseName(spray);
            string path = GetUniqueDownloadPath(sprayDirectory, baseName, extension);
            File.WriteAllBytes(path, bytes);

            savedPath = path;
            return true;
        }

        private static bool TryGetDownloadBytes(
            SprayInstance spray,
            out byte[] bytes,
            out string extension,
            out string error)
        {
            bytes = null;
            extension = ".png";
            error = "";

            if (spray == null)
            {
                error = "No spray selected.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(spray.SourceFilePath) && File.Exists(spray.SourceFilePath))
            {
                bytes = File.ReadAllBytes(spray.SourceFilePath);
                extension = NormalizeImageExtension(Path.GetExtension(spray.SourceFilePath), bytes);
                if (bytes != null && bytes.Length > 0)
                    return true;

                error = "The source spray file is empty.";
                return false;
            }

            if (spray.ImageBytes != null && spray.ImageBytes.Length > 0)
            {
                bytes = spray.ImageBytes;
                extension = DetectImageExtension(bytes);
                return true;
            }

            error = "This spray has no image bytes available to save.";
            return false;
        }

        private static string GetSprayDirectory()
        {
            return Path.Combine(Paths.ConfigPath, "Cat-Patch", "sprays");
        }

        private static string BuildDownloadBaseName(SprayInstance spray)
        {
            string sprayId = spray != null ? spray.SprayId : "";
            if (!string.IsNullOrWhiteSpace(sprayId))
                return "catpatch-spray-" + SanitizeFileName(sprayId);

            return "catpatch-spray-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        }

        private static string GetUniqueDownloadPath(string directory, string baseName, string extension)
        {
            extension = string.IsNullOrWhiteSpace(extension) ? ".png" : extension;
            string path = Path.Combine(directory, baseName + extension);
            if (!File.Exists(path))
                return path;

            for (int i = 1; i < 1000; i++)
            {
                path = Path.Combine(directory, baseName + "-" + i + extension);
                if (!File.Exists(path))
                    return path;
            }

            return Path.Combine(directory, baseName + "-" + Guid.NewGuid().ToString("N") + extension);
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] chars = fileName.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalidChars.Length; j++)
                {
                    if (chars[i] == invalidChars[j])
                    {
                        chars[i] = '-';
                        break;
                    }
                }
            }

            return new string(chars);
        }

        private static string NormalizeImageExtension(string extension, byte[] bytes)
        {
            extension = string.IsNullOrWhiteSpace(extension) ? "" : extension.ToLowerInvariant();
            if (extension == ".gif" || extension == ".png" || extension == ".jpg" || extension == ".jpeg")
                return extension == ".jpeg" ? ".jpg" : extension;

            return DetectImageExtension(bytes);
        }

        private static string DetectImageExtension(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4)
                return ".png";

            if (GifAnimationDecoder.IsGifBytes(bytes))
                return ".gif";

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47)
            {
                return ".png";
            }

            if (bytes[0] == 0xFF && bytes[1] == 0xD8)
                return ".jpg";

            return ".png";
        }

        private void UpdateHoveredSpray()
        {
            _hoveredSpray = null;

            if (Camera.main == null)
                return;

            Vector3 mouseWorld =
                Camera.main.ScreenToWorldPoint(Input.mousePosition);

            List<SprayInstance> sprays = GetSprayCache();

            float bestDistance = float.MaxValue;

            for (int i = 0; i < sprays.Count; i++)
            {
                SprayInstance spray = sprays[i];

                if (spray == null || spray.Renderer == null)
                    continue;

                Bounds bounds = spray.Renderer.bounds;

                bool mouseInside =
                    mouseWorld.x >= bounds.min.x &&
                    mouseWorld.x <= bounds.max.x &&
                    mouseWorld.y >= bounds.min.y &&
                    mouseWorld.y <= bounds.max.y;

                if (!mouseInside)
                    continue;

                float distance =
                    Vector2.Distance(
                        bounds.center,
                        mouseWorld);

                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                _hoveredSpray = spray;
            }
        }

        private List<SprayInstance> GetSprayCache()
        {
            if (_sprayCacheVersion != SprayInstance.RegistryVersion)
                _sprayCacheVersion = SprayInstance.CopyActiveInstancesTo(_sprayCache);

            return _sprayCache;
        }

        private void ApplyHoverHighlight()
        {
            if (_previousHoveredSpray != null && _previousHoveredSpray != _hoveredSpray)
                SetSprayColor(_previousHoveredSpray, Color.white);

            if (_hoveredSpray != null)
                SetSprayColor(_hoveredSpray, GetHoverColor());

            _previousHoveredSpray = _hoveredSpray;
        }

        private void ClearHoverHighlight()
        {
            if (_previousHoveredSpray != null)
                SetSprayColor(_previousHoveredSpray, Color.white);

            _previousHoveredSpray = null;
            _hoveredSpray = null;
        }

        private static void SetSprayColor(SprayInstance spray, Color color)
        {
            if (spray == null || spray.Renderer == null)
                return;

            spray.Renderer.color = color;
        }

        private static Color GetHoverColor()
        {
            float alpha =
                0.55f +
                0.35f *
                (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f));

            return new Color(0.62f, 0.86f, 1f, alpha);
        }

        private static GUIStyle CreateKrokStyleButton()
        {
            float scale = GetMenuScale();
            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                font = GetInspectFont(),
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

        private static Font GetInspectFont()
        {
            if (_inspectFont != null)
                return _inspectFont;

            try
            {
                _inspectFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Retro Gaming", "RetroGaming", "Retro Gaming Regular" },
                    16);
            }
            catch
            {
                _inspectFont = null;
            }

            if (_inspectFont == null)
                _inspectFont = GUI.skin != null && GUI.skin.font != null
                    ? GUI.skin.font
                    : Resources.GetBuiltinResource<Font>("Arial.ttf");

            return _inspectFont;
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
