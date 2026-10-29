using UnityEngine;

namespace CatPatchspace
{
    internal enum MfcHazardCategory
    {
        RegularTraps,
        Enemies,
        EnvironmentalHazards,
        RadioactiveHazards
    }

    internal sealed class MfcHazardDefinition
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly MfcHazardCategory Category;
        public readonly float MinPerChunk;
        public readonly float MaxPerChunk;
        public readonly float SpawnYOffset;
        public readonly float RandomRotation;
        public readonly float SpawnYOffsetDeviation;
        public readonly bool RandomFlip;
        public readonly bool IsTrap;
        public readonly Vector2 Direction;

        public MfcHazardDefinition(
            string id,
            string displayName,
            MfcHazardCategory category,
            float minPerChunk,
            float maxPerChunk,
            float spawnYOffset,
            float randomRotation = 0.0f,
            float spawnYOffsetDeviation = 0.0f,
            bool randomFlip = false,
            bool isTrap = true,
            Vector2 direction = default(Vector2))
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
            MinPerChunk = minPerChunk;
            MaxPerChunk = maxPerChunk;
            SpawnYOffset = spawnYOffset;
            RandomRotation = randomRotation;
            SpawnYOffsetDeviation = spawnYOffsetDeviation;
            RandomFlip = randomFlip;
            IsTrap = isTrap;
            Direction = direction;
        }
    }

    internal static class MfcHazardCatalog
    {
        public static readonly MfcHazardCategory[] Categories =
        {
            MfcHazardCategory.RegularTraps,
            MfcHazardCategory.Enemies,
            MfcHazardCategory.EnvironmentalHazards,
            MfcHazardCategory.RadioactiveHazards
        };

        public static readonly MfcHazardDefinition[] Hazards =
        {
            new MfcHazardDefinition("spikestabber", "Spike stabber", MfcHazardCategory.RegularTraps, 0.40f, 0.50f, 0.0f),
            new MfcHazardDefinition("barbedwirefence", "Barbed wire", MfcHazardCategory.RegularTraps, 0.70f, 0.80f, 4.8f),
            new MfcHazardDefinition("beartrap", "Bear trap", MfcHazardCategory.RegularTraps, 0.20f, 0.25f, 1.0f),
            new MfcHazardDefinition("jumppad", "Jump pad", MfcHazardCategory.RegularTraps, 0.60f, 0.80f, 0.0f),
            new MfcHazardDefinition("landmine", "Landmine", MfcHazardCategory.RegularTraps, 0.13f, 0.16f, 0.4f),
            new MfcHazardDefinition("gunmine", "Gunmine", MfcHazardCategory.RegularTraps, 0.08f, 0.12f, 0.7f),
            new MfcHazardDefinition("sawblade", "Sawblade", MfcHazardCategory.RegularTraps, 0.08f, 0.12f, 0.8f),
            new MfcHazardDefinition("soundcannon", "Sound cannon", MfcHazardCategory.RegularTraps, 0.40f, 0.45f, 1.0f),
            new MfcHazardDefinition("coil", "Coil", MfcHazardCategory.RegularTraps, 0.20f, 0.30f, 2.0f),
            new MfcHazardDefinition("turret", "Turret", MfcHazardCategory.RegularTraps, 0.03f, 0.05f, 0.0f),

            new MfcHazardDefinition("caveticks", "Tick swarm", MfcHazardCategory.Enemies, 0.15f, 0.16f, 4.8f),
            new MfcHazardDefinition("wallbiter", "Wall biter", MfcHazardCategory.Enemies, 0.12f, 0.13f, 4.8f),
            new MfcHazardDefinition("shadecrawler", "Shade crawler", MfcHazardCategory.Enemies, 0.20f, 0.20f, 4.8f),
            new MfcHazardDefinition("thornbackyoung", "Thornback", MfcHazardCategory.Enemies, 0.24f, 0.26f, 4.8f),
            new MfcHazardDefinition("overgrowntick", "Overgrown tick", MfcHazardCategory.Enemies, 0.22f, 0.24f, 4.8f),
            new MfcHazardDefinition("grabberplant", "Grabber plant", MfcHazardCategory.Enemies, 0.40f, 0.50f, 0.0f),

            new MfcHazardDefinition("skullcrusher", "Skull crusher", MfcHazardCategory.EnvironmentalHazards, 1.10f, 1.20f, 1.0f, 10.0f, 0.0f, true, false, Vector2.up),
            new MfcHazardDefinition("geyser", "Geyser", MfcHazardCategory.EnvironmentalHazards, 0.70f, 0.80f, 0.6f, 0.0f, 0.0f, false, false),
            new MfcHazardDefinition("oilpipe", "Oil pipe", MfcHazardCategory.EnvironmentalHazards, 0.08f, 0.12f, 0.0f),
            new MfcHazardDefinition("glassshards", "Glass shards", MfcHazardCategory.EnvironmentalHazards, 0.25f, 0.35f, 0.1f),

            new MfcHazardDefinition("radbarrel", "Rad barrel", MfcHazardCategory.RadioactiveHazards, 1.20f, 1.30f, 1.5f),
            new MfcHazardDefinition("spentfuel", "Spent fuel", MfcHazardCategory.RadioactiveHazards, 0.15f, 0.20f, 0.8f)
        };

        public static string[] AllHazardIds
        {
            get
            {
                string[] ids = new string[Hazards.Length];
                for (int i = 0; i < Hazards.Length; i++)
                    ids[i] = Hazards[i].Id;

                return ids;
            }
        }

        public static string GetCategoryDisplayName(MfcHazardCategory category)
        {
            switch (category)
            {
                case MfcHazardCategory.RegularTraps:
                    return "Regular Traps";
                case MfcHazardCategory.Enemies:
                    return "Enemies";
                case MfcHazardCategory.EnvironmentalHazards:
                    return "Environmental Hazards";
                case MfcHazardCategory.RadioactiveHazards:
                    return "Radioactive Hazards";
                default:
                    return "Hazards";
            }
        }

        public static string GetDisplayName(string id)
        {
            for (int i = 0; i < Hazards.Length; i++)
            {
                if (Hazards[i].Id == id)
                    return Hazards[i].DisplayName;
            }

            return string.IsNullOrWhiteSpace(id) ? "Unknown" : char.ToUpperInvariant(id[0]) + id.Substring(1);
        }
    }
}
