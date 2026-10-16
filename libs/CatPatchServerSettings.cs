using BepInEx.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CatPatchspace
{
    internal static class CatPatchServerSettings
    {
        private static ConfigEntry<float> _sprayCooldownSeconds;
        private static ConfigEntry<bool> _dogpileEnabled;
        private static ConfigEntry<float> _dogpileRadius;
        private static ConfigEntry<bool> _piggybackThrowEnabled;
        private static ConfigEntry<float> _piggybackThrowStrength;
        private static ConfigEntry<bool> _challengeModeEnabled;
        private static ConfigEntry<float> _challengeTrapDensity;
        private static ConfigEntry<string> _challengeStarterKitItems;
        private static ConfigEntry<bool> _challengeRandomLandmineHeight;
        private static ConfigEntry<bool> _challengeRandomSpikeOrientation;
        private static ConfigEntry<int> _challengeElderThornbackCount;
        private static float _sprayCooldownSecondsValue = 20f;
        private static bool _dogpileEnabledValue = true;
        private static float _dogpileRadiusValue = 1.00f;
        private static bool _piggybackThrowEnabledValue = false;
        private static float _piggybackThrowStrengthValue = 28.0f;
        private static bool _challengeModeEnabledValue = false;
        private static float _challengeTrapDensityValue = 1.0f;
        private static string _challengeStarterKitItemsValue = DefaultChallengeStarterKitItems;
        private static bool _challengeRandomLandmineHeightValue = false;
        private static bool _challengeRandomSpikeOrientationValue = false;
        private static int _challengeElderThornbackCountValue = 0;
        private static readonly Dictionary<string, ConfigEntry<float>> _challengeTrapMultipliers =
            new Dictionary<string, ConfigEntry<float>>();
        private static readonly Dictionary<string, float> _challengeTrapMultiplierValues =
            new Dictionary<string, float>();

        public const string OldChallengeStarterKitItems = "bandage,tourniquet,splint,waterbottle,flashlight,flimsyknife";
        public const string ShortChallengeStarterKitItems = "medkit,combatpen,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,tourniquet,chestdrain,belt,pistol,headlamp";
        public const string DefaultChallengeStarterKitItems =
            "medkit,combatpen,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,medicalsuture,tourniquet,chestdrain,belt,pistol,headlamp";

        public static readonly string[] ChallengeTrapIds = MfcHazardCatalog.AllHazardIds;

        public static float SprayCooldownSeconds
        {
            get
            {
                if (_sprayCooldownSeconds != null)
                    _sprayCooldownSecondsValue = _sprayCooldownSeconds.Value;

                return Mathf.Clamp(_sprayCooldownSecondsValue, 0f, 300f);
            }

            set
            {
                _sprayCooldownSecondsValue = Mathf.Clamp(value, 0f, 300f);
                if (_sprayCooldownSeconds != null)
                    _sprayCooldownSeconds.Value = _sprayCooldownSecondsValue;
            }
        }

        public static bool DogpileEnabled
        {
            get
            {
                if (_dogpileEnabled != null)
                    _dogpileEnabledValue = _dogpileEnabled.Value;

                return _dogpileEnabledValue;
            }

            set
            {
                _dogpileEnabledValue = value;
                if (_dogpileEnabled != null)
                    _dogpileEnabled.Value = value;
            }
        }

        public static float DogpileRadius
        {
            get
            {
                return _dogpileRadiusValue;
            }

            set
            {
                _dogpileRadiusValue = 1.00f;
                if (_dogpileRadius != null)
                    _dogpileRadius.Value = _dogpileRadiusValue;
            }
        }

        public static bool PiggybackThrowEnabled
        {
            get
            {
                if (_piggybackThrowEnabled != null)
                    _piggybackThrowEnabledValue = _piggybackThrowEnabled.Value;

                return _piggybackThrowEnabledValue;
            }

            set
            {
                _piggybackThrowEnabledValue = value;
                if (_piggybackThrowEnabled != null)
                    _piggybackThrowEnabled.Value = value;
            }
        }

        public static float PiggybackThrowStrength
        {
            get
            {
                if (_piggybackThrowStrength != null)
                    _piggybackThrowStrengthValue = _piggybackThrowStrength.Value;

                return Mathf.Clamp(_piggybackThrowStrengthValue, 5.0f, 80.0f);
            }

            set
            {
                _piggybackThrowStrengthValue = Mathf.Clamp(value, 5.0f, 80.0f);
                if (_piggybackThrowStrength != null)
                    _piggybackThrowStrength.Value = _piggybackThrowStrengthValue;
            }
        }

        public static bool ChallengeModeEnabled
        {
            get
            {
                if (_challengeModeEnabled != null)
                    _challengeModeEnabledValue = _challengeModeEnabled.Value;

                return _challengeModeEnabledValue;
            }

            set
            {
                _challengeModeEnabledValue = value;
                if (_challengeModeEnabled != null)
                    _challengeModeEnabled.Value = value;
            }
        }

        public static float ChallengeTrapDensity
        {
            get
            {
                if (_challengeTrapDensity != null)
                    _challengeTrapDensityValue = _challengeTrapDensity.Value;

                return Mathf.Clamp(_challengeTrapDensityValue, 0.1f, 5.0f);
            }

            set
            {
                _challengeTrapDensityValue = Mathf.Clamp(value, 0.1f, 5.0f);
                if (_challengeTrapDensity != null)
                    _challengeTrapDensity.Value = _challengeTrapDensityValue;
            }
        }

        public static bool ChallengeRandomLandmineHeight
        {
            get
            {
                if (_challengeRandomLandmineHeight != null)
                    _challengeRandomLandmineHeightValue = _challengeRandomLandmineHeight.Value;

                return _challengeRandomLandmineHeightValue;
            }

            set
            {
                _challengeRandomLandmineHeightValue = value;
                if (_challengeRandomLandmineHeight != null)
                    _challengeRandomLandmineHeight.Value = value;
            }
        }

        public static string ChallengeStarterKitItems
        {
            get
            {
                if (_challengeStarterKitItems != null)
                    _challengeStarterKitItemsValue = _challengeStarterKitItems.Value ?? "";

                return _challengeStarterKitItemsValue ?? "";
            }

            set
            {
                _challengeStarterKitItemsValue = value ?? "";
                if (_challengeStarterKitItems != null)
                    _challengeStarterKitItems.Value = _challengeStarterKitItemsValue;
            }
        }

        public static bool ChallengeRandomSpikeOrientation
        {
            get
            {
                if (_challengeRandomSpikeOrientation != null)
                    _challengeRandomSpikeOrientationValue = _challengeRandomSpikeOrientation.Value;

                return _challengeRandomSpikeOrientationValue;
            }

            set
            {
                _challengeRandomSpikeOrientationValue = value;
                if (_challengeRandomSpikeOrientation != null)
                    _challengeRandomSpikeOrientation.Value = value;
            }
        }

        public static int ChallengeElderThornbackCount
        {
            get
            {
                if (_challengeElderThornbackCount != null)
                    _challengeElderThornbackCountValue = _challengeElderThornbackCount.Value;

                return Mathf.Clamp(_challengeElderThornbackCountValue, 0, 20);
            }

            set
            {
                _challengeElderThornbackCountValue = Mathf.Clamp(value, 0, 20);
                if (_challengeElderThornbackCount != null)
                    _challengeElderThornbackCount.Value = _challengeElderThornbackCountValue;
            }
        }

        public static float GetChallengeTrapMultiplier(string trapId)
        {
            ConfigEntry<float> entry;
            if (string.IsNullOrWhiteSpace(trapId) ||
                !_challengeTrapMultipliers.TryGetValue(trapId, out entry) ||
                entry == null)
            {
                float fallbackValue;
                return _challengeTrapMultiplierValues.TryGetValue(trapId, out fallbackValue)
                    ? Mathf.Clamp(fallbackValue, 0.0f, 10.0f)
                    : 1.0f;
            }

            _challengeTrapMultiplierValues[trapId] = entry.Value;
            return Mathf.Clamp(entry.Value, 0.0f, 10.0f);
        }

        public static void SetChallengeTrapMultiplier(string trapId, float value)
        {
            ConfigEntry<float> entry;
            if (string.IsNullOrWhiteSpace(trapId) ||
                !_challengeTrapMultipliers.TryGetValue(trapId, out entry) ||
                entry == null)
            {
                _challengeTrapMultiplierValues[trapId] = Mathf.Clamp(value, 0.0f, 10.0f);
                return;
            }

            _challengeTrapMultiplierValues[trapId] = Mathf.Clamp(value, 0.0f, 10.0f);
            entry.Value = _challengeTrapMultiplierValues[trapId];
        }

        public static void SetAllChallengeTrapMultipliers(float value)
        {
            for (int i = 0; i < ChallengeTrapIds.Length; i++)
                SetChallengeTrapMultiplier(ChallengeTrapIds[i], value);
        }

        public static void SetChallengeCategoryMultipliers(MfcHazardCategory category, float value)
        {
            for (int i = 0; i < MfcHazardCatalog.Hazards.Length; i++)
            {
                MfcHazardDefinition hazard = MfcHazardCatalog.Hazards[i];
                if (hazard.Category == category)
                    SetChallengeTrapMultiplier(hazard.Id, value);
            }
        }

        public static bool IsChallengeCategoryEnabled(MfcHazardCategory category)
        {
            for (int i = 0; i < MfcHazardCatalog.Hazards.Length; i++)
            {
                MfcHazardDefinition hazard = MfcHazardCatalog.Hazards[i];
                if (hazard.Category == category && GetChallengeTrapMultiplier(hazard.Id) > 0.0f)
                    return true;
            }

            return category == MfcHazardCategory.Enemies && ChallengeElderThornbackCount > 0;
        }

        public static string ExportChallengePreset()
        {
            ChallengePreset preset = new ChallengePreset
            {
                version = 1,
                globalDensity = ChallengeTrapDensity,
                randomLandmineHeight = ChallengeRandomLandmineHeight,
                randomSpikeOrientation = ChallengeRandomSpikeOrientation,
                elderThornbacks = ChallengeElderThornbackCount,
                starterKit = ChallengeStarterKitItems,
                hazards = new Dictionary<string, float>()
            };

            for (int i = 0; i < ChallengeTrapIds.Length; i++)
            {
                string trapId = ChallengeTrapIds[i];
                preset.hazards[trapId] = GetChallengeTrapMultiplier(trapId);
            }

            string json = JsonConvert.SerializeObject(preset, Formatting.None);
            return "CATPATCH-MFC:v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static bool TryImportChallengePreset(string rawPreset, out string message)
        {
            message = "";
            if (string.IsNullOrWhiteSpace(rawPreset))
            {
                message = "Preset text is empty.";
                return false;
            }

            try
            {
                string trimmed = rawPreset.Trim();
                const string prefix = "CATPATCH-MFC:v1:";
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(prefix.Length);

                string json = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
                ChallengePreset preset = JsonConvert.DeserializeObject<ChallengePreset>(json);
                if (preset == null)
                {
                    message = "Preset could not be read.";
                    return false;
                }

                ChallengeTrapDensity = preset.globalDensity;
                ChallengeRandomLandmineHeight = preset.randomLandmineHeight;
                ChallengeRandomSpikeOrientation = preset.randomSpikeOrientation;
                ChallengeElderThornbackCount = preset.elderThornbacks;
                ChallengeStarterKitItems = preset.starterKit ?? "";

                SetAllChallengeTrapMultipliers(0.0f);
                if (preset.hazards != null)
                {
                    foreach (KeyValuePair<string, float> pair in preset.hazards)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key))
                            SetChallengeTrapMultiplier(pair.Key, pair.Value);
                    }
                }

                message = "Imported MFC preset.";
                return true;
            }
            catch (Exception ex)
            {
                message = "Preset import failed: " + ex.Message;
                return false;
            }
        }

        public static void Bind(ConfigFile config)
        {
            _sprayCooldownSeconds = config.Bind(
                "Server Settings",
                "SprayCooldownSeconds",
                20f,
                "Cooldown in seconds before a player can place another spray.");
            _sprayCooldownSecondsValue = _sprayCooldownSeconds.Value;

            _dogpileEnabled = config.Bind(
                "Server Settings",
                "DogpileEnabled",
                true,
                "Allow nearby sleeping players to form a dogpile and improve sleep quality for later sleepers.");
            _dogpileEnabledValue = _dogpileEnabled.Value;

            _dogpileRadius = config.Bind(
                "Server Settings",
                "DogpileRadius",
                1.00f,
                "Distance in world units used to group nearby sleeping players into a dogpile. CatPatch currently hardcodes this to 1.00.");
            _dogpileRadius.Value = 1.00f;
            _dogpileRadiusValue = 1.00f;

            _piggybackThrowEnabled = config.Bind(
                "Server Settings",
                "PiggybackThrowEnabled",
                false,
                "Allow carriers to press the multiplayer shove key to throw the player on their back.");
            _piggybackThrowEnabledValue = _piggybackThrowEnabled.Value;

            _piggybackThrowStrength = config.Bind(
                "Server Settings",
                "PiggybackThrowStrength",
                28.0f,
                "Velocity applied to a piggybacked player when thrown by their carrier.");
            _piggybackThrowStrengthValue = _piggybackThrowStrength.Value;

            _challengeModeEnabled = config.Bind(
                "Server Settings",
                "ChallengeModeEnabled",
                false,
                "Enable CatPatch challenge mode. Host/server spawns extra traps every layer and gives starter kits.");
            _challengeModeEnabledValue = _challengeModeEnabled.Value;

            _challengeTrapDensity = config.Bind(
                "Server Settings",
                "ChallengeTrapDensity",
                1.0f,
                "Multiplier for CatPatch challenge mode extra trap density.");
            _challengeTrapDensityValue = _challengeTrapDensity.Value;

            _challengeStarterKitItems = config.Bind(
                "Server Settings",
                "ChallengeStarterKitItems",
                DefaultChallengeStarterKitItems,
                "Comma-separated item IDs granted once to each player in challenge mode.");

            if (_challengeStarterKitItems.Value == OldChallengeStarterKitItems ||
                _challengeStarterKitItems.Value == ShortChallengeStarterKitItems)
            {
                _challengeStarterKitItems.Value = DefaultChallengeStarterKitItems;
            }
            _challengeStarterKitItemsValue = _challengeStarterKitItems.Value ?? "";

            _challengeRandomLandmineHeight = config.Bind(
                "Server Settings",
                "ChallengeRandomLandmineHeight",
                false,
                "Randomize extra challenge mode landmine height from 0 to 10 world units above the ground.");
            _challengeRandomLandmineHeightValue = _challengeRandomLandmineHeight.Value;

            _challengeRandomSpikeOrientation = config.Bind(
                "Server Settings",
                "ChallengeRandomSpikeOrientation",
                false,
                "Randomize extra challenge mode spike trap rotation.");
            _challengeRandomSpikeOrientationValue = _challengeRandomSpikeOrientation.Value;

            _challengeElderThornbackCount = config.Bind(
                "Server Settings",
                "ChallengeElderThornbackCount",
                0,
                "Number of elder thornbacks to spawn randomly in each challenge mode layer.");
            _challengeElderThornbackCountValue = _challengeElderThornbackCount.Value;

            _challengeTrapMultipliers.Clear();
            _challengeTrapMultiplierValues.Clear();
            for (int i = 0; i < ChallengeTrapIds.Length; i++)
            {
                string trapId = ChallengeTrapIds[i];
                _challengeTrapMultipliers[trapId] = config.Bind(
                    "Server Settings",
                    "ChallengeTrapMultiplier_" + trapId,
                    0.0f,
                    "Per-trap multiplier for CatPatch challenge mode spawns. 0 disables this trap.");
                _challengeTrapMultiplierValues[trapId] = _challengeTrapMultipliers[trapId].Value;
            }
        }

        private sealed class ChallengePreset
        {
            public int version;
            public float globalDensity;
            public bool randomLandmineHeight;
            public bool randomSpikeOrientation;
            public int elderThornbacks;
            public string starterKit;
            public Dictionary<string, float> hazards;
        }
    }
}
