using BepInEx.Configuration;

namespace CatPatchspace
{
    internal static class CatPatchClientSettings
    {
        private static ConfigFile _config;
        private static ConfigEntry<bool> _showSprays;
        private static ConfigEntry<string> _updateManifestUrl;
        private static bool _showSpraysValue = false;

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

            _updateManifestUrl = config.Bind(
                "Client Settings",
                "UpdateManifestUrl",
                CatPatchUpdater.DefaultUrl,
                "Raw URL for the CatPatch update manifest.");
        }
    }
}
