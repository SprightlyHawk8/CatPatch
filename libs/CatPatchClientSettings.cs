using BepInEx.Configuration;

namespace CatPatchspace
{
    internal static class CatPatchClientSettings
    {
        private static ConfigFile _config;
        private static ConfigEntry<bool> _showSprays;
        private static ConfigEntry<bool> _sendSprayFallback;
        private static ConfigEntry<bool> _clearSprayFallbackCacheOnStartup;
        private static bool _sendSprayFallbackValue = true;
        internal static int SprayPreferenceGeneration { get; private set; }
        private static ConfigEntry<float> _sprayMenuScale;
        private static ConfigEntry<string> _updateManifestUrl;
        private static bool _showSpraysValue = false;

        public static float SprayMenuScale
        {
            get { return _sprayMenuScale != null ? _sprayMenuScale.Value : 1f; }
            set { if (_sprayMenuScale != null) _sprayMenuScale.Value = value; }
        }

        public static bool SendSprayFallback
        {
            get { return ShowSprays && (_sendSprayFallback != null ? _sendSprayFallback.Value : _sendSprayFallbackValue); }
            set
            {
                _sendSprayFallbackValue = ShowSprays && value;
                if (_sendSprayFallback != null)
                    _sendSprayFallback.Value = _sendSprayFallbackValue;
                _config?.Save();
            }
        }

        public static bool ClearSprayFallbackCacheOnStartup
        {
            get { return ShowSprays && (_clearSprayFallbackCacheOnStartup?.Value ?? true); }
            set
            {
                if (_clearSprayFallbackCacheOnStartup != null)
                    _clearSprayFallbackCacheOnStartup.Value = value;
                _config?.Save();
            }
        }

        private static void DisableFallbackWhenHidden()
        {
            if (!ShowSprays)
            {
                SprayPreferenceGeneration++;
                SendSprayFallback = false;
            }
        }

        public static bool ShowSprays
        {
            get
            {
                if (_showSprays != null)
                    _showSpraysValue = _showSprays.Value;

                return _showSpraysValue;
            }

            set
            {
                _showSpraysValue = value;
                if (!value)
                {
                    SprayPreferenceGeneration++;
                    _sendSprayFallbackValue = false;
                    if (_sendSprayFallback != null)
                        _sendSprayFallback.Value = false;
                }
                if (_showSprays != null)
                {
                    _showSprays.Value = value;
                    _config?.Save();
                }
            }
        }

        public static string UpdateManifestUrl
        {
            get
            {
                return _updateManifestUrl != null
                    ? _updateManifestUrl.Value
                    : CatPatchUpdater.DefaultUrl;
            }
        }

        public static void Bind(ConfigFile config)
        {
            _config = config;
            _showSprays = config.Bind(
                "Client Settings",
                "ShowSprays",
                false,
                "Show CatPatch sprays placed by players.");
            _showSpraysValue = _showSprays.Value;

            _clearSprayFallbackCacheOnStartup = config.Bind("Client Settings", "ClearSprayFallbackCacheOnStartup", true,
                "Clear downloaded spray fallback cache files when the game starts with ShowSprays enabled. Your original spray library is never deleted. Changes apply on the next launch.");

            _sendSprayFallback = config.Bind("Client Settings", "SendSprayFallback", true,
                "Send your spray images to the lobby host as a backend backup. Automatically disabled when ShowSprays is off. Host relay for other players is independent.");
            _showSprays.SettingChanged += (_, __) => DisableFallbackWhenHidden();
            DisableFallbackWhenHidden();

            _sprayMenuScale = config.Bind("Client Settings", "SprayMenuScale", 1f,
                new ConfigDescription("Spray selector size multiplier, in addition to display DPI scaling.",
                    new AcceptableValueRange<float>(0.75f, 3f)));

            _updateManifestUrl = config.Bind(
                "Client Settings",
                "UpdateManifestUrl",
                CatPatchUpdater.DefaultUrl,
                "Raw URL for the CatPatch update manifest.");
        }
    }
}
