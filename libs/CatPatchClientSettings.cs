using BepInEx.Configuration;

namespace CatPatchspace
{
    internal static class CatPatchClientSettings
    {
        private static ConfigFile _config;
        private static ConfigEntry<bool> _showSprays;
        private static ConfigEntry<float> _sprayMenuScale;
        private static ConfigEntry<string> _updateManifestUrl;
        private static bool _showSpraysValue = false;

        public static float SprayMenuScale
        {
            get { return _sprayMenuScale != null ? _sprayMenuScale.Value : 1f; }
            set { if (_sprayMenuScale != null) _sprayMenuScale.Value = value; }
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
