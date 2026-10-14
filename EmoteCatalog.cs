using System;

namespace CatPatchspace
{
    internal static class EmoteCatalog
    {
        private static readonly EmoteDefinition[] Emotes =
        {
            new EmoteDefinition("sit", "Sit", "ExperimentSit", "ArmsSit"),
            new EmoteDefinition("lay", "Lay", "ExperimentSit", "ArmsSit", false, true, false, 0.0f, 0.0f, 0.55f),
            new EmoteDefinition("sleep", "Sleep", "ExperimentSit", "ArmsSit", true, true, false, 0.0f, 0.0f, 0.99f)
        };

        public static EmoteDefinition[] All
        {
            get { return Emotes; }
        }

        public static bool TryGet(string id, out EmoteDefinition emote)
        {
            emote = null;

            if (string.IsNullOrWhiteSpace(id))
                return false;

            for (int i = 0; i < Emotes.Length; i++)
            {
                if (string.Equals(Emotes[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    emote = Emotes[i];
                    return true;
                }
            }

            return false;
        }
    }
}
