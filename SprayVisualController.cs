using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class SprayVisualController
    {
        private const float TargetWorldWidth = 7.5f;

        private readonly List<GameObject> _activeSprays = new List<GameObject>();
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private bool _lastVisible;
        private bool _hasLastVisible;

        public void SetSpraysVisible(bool visible)
        {
            if (_hasLastVisible && _lastVisible == visible)
                return;

            _hasLastVisible = true;
            _lastVisible = visible;

            for (int i = _activeSprays.Count - 1; i >= 0; i--)
            {
                GameObject sprayObject = _activeSprays[i];

                if (sprayObject == null)
                {
                    _activeSprays.RemoveAt(i);
                    continue;
                }

                if (sprayObject.activeSelf != visible)
                    sprayObject.SetActive(visible);
            }
        }

        public int ClearActiveSprays()
        {
            int cleared = 0;

            for (int i = _activeSprays.Count - 1; i >= 0; i--)
            {
                GameObject sprayObject = _activeSprays[i];
                _activeSprays.RemoveAt(i);

                if (sprayObject == null)
                    continue;

                Object.Destroy(sprayObject);
                cleared++;
            }

            return cleared;
        }

        public int ClearActiveSpraysByOwner(ulong? ownerClientId)
        {
            if (ownerClientId == null)
                return ClearActiveSprays();

            int cleared = 0;

            for (int i = _activeSprays.Count - 1; i >= 0; i--)
            {
                GameObject sprayObject = _activeSprays[i];

                if (sprayObject == null)
                {
                    _activeSprays.RemoveAt(i);
                    continue;
                }

                SprayInstance instance = sprayObject.GetComponent<SprayInstance>();
                if (instance == null || instance.OwnerClientId != ownerClientId.Value)
                    continue;

                _activeSprays.RemoveAt(i);
                Object.Destroy(sprayObject);
                cleared++;
            }

            return cleared;
        }

        public bool ClearActiveSpray(string sprayId, Vector2 worldPosition)
        {
            string normalizedSprayId = NormalizeCacheKey(sprayId);
            int x = Mathf.RoundToInt(worldPosition.x * 100f);
            int y = Mathf.RoundToInt(worldPosition.y * 100f);

            for (int i = _activeSprays.Count - 1; i >= 0; i--)
            {
                GameObject sprayObject = _activeSprays[i];

                if (sprayObject == null)
                {
                    _activeSprays.RemoveAt(i);
                    continue;
                }

                SprayInstance instance = sprayObject.GetComponent<SprayInstance>();
                if (instance == null)
                    continue;

                if (!string.Equals(NormalizeCacheKey(instance.SprayId), normalizedSprayId, System.StringComparison.Ordinal))
                    continue;

                int sprayX = Mathf.RoundToInt(sprayObject.transform.position.x * 100f);
                int sprayY = Mathf.RoundToInt(sprayObject.transform.position.y * 100f);
                if (sprayX != x || sprayY != y)
                    continue;

                _activeSprays.RemoveAt(i);
                Object.Destroy(sprayObject);
                return true;
            }

            return false;
        }

        public bool TryCreateSpray(
            string visualCacheKey,
            byte[] imageBytes,
            Vector2 worldPosition,
            ulong ownerClientId,
            out GameObject sprayObject,
            out string error)
        {
            sprayObject = null;
            error = "";

            if (imageBytes == null || imageBytes.Length == 0)
            {
                error = "No image bytes were provided.";
                return false;
            }

            Sprite sprite;
            if (!TryGetOrCreateSprite(
                    visualCacheKey,
                    imageBytes,
                    out sprite,
                    out error))
            {
                return false;
            }

            sprayObject = new GameObject("CatPatch Spray");
            sprayObject.transform.position = new Vector3(worldPosition.x, worldPosition.y, 1f);

            float scale = Random.Range(0.65f, 0.85f);
            float tilt = Random.Range(-8f, 8f);

            sprayObject.transform.localScale = new Vector3(scale, scale, 1f);
            sprayObject.transform.rotation = Quaternion.Euler(0f, 0f, tilt);

            SpriteRenderer renderer = sprayObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = -10;

            BoxCollider2D collider = sprayObject.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;

            SprayInstance instance = sprayObject.AddComponent<SprayInstance>();
            instance.SprayId = visualCacheKey ?? "";
            instance.OwnerClientId = ownerClientId;
            instance.ImageBytes = imageBytes;
            instance.Renderer = renderer;

            _activeSprays.Add(sprayObject);
            sprayObject.SetActive(CatPatchClientSettings.ShowSprays);

            return true;
        }

        private bool TryGetOrCreateSprite(
            string cacheKey,
            byte[] imageBytes,
            out Sprite sprite,
            out string error)
        {
            sprite = null;
            error = "";

            cacheKey = NormalizeCacheKey(cacheKey);

            if (!string.IsNullOrWhiteSpace(cacheKey) &&
                _spriteCache.TryGetValue(cacheKey, out sprite) &&
                sprite != null)
            {
                return true;
            }

            Texture2D texture;
            if (GifAnimationDecoder.IsGifBytes(imageBytes))
            {
                if (!GifAnimationDecoder.TryCreateFirstFrameTexture(imageBytes, out texture, out error))
                    return false;
            }
            else
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                if (!ImageConversion.LoadImage(texture, imageBytes))
                {
                    Object.Destroy(texture);
                    error = "Failed to load image bytes into Texture2D.";
                    return false;
                }

                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
            }

            float pixelsPerUnit = texture.width / TargetWorldWidth;

            sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);

            if (!string.IsNullOrWhiteSpace(cacheKey))
                _spriteCache[cacheKey] = sprite;

            return true;
        }

        private static string NormalizeCacheKey(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                return "";

            return cacheKey.Trim().ToLowerInvariant();
        }
    }
}
