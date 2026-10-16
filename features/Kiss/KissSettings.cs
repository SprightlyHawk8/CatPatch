using UnityEngine;

namespace CatPatchspace
{
    internal static class KissSettings
    {
        public const float SearchRadius = 4.0f;
        public const float MaxAcceptedDistance = 4.5f;
        public const float MaxVisualDistance = 5.5f;
        public const float DurationSeconds = 2.2f;
        public const double RequestCooldownSeconds = 1.0;
        public const double RequestAckTimeoutSeconds = 3.0;
        public const double PromptLifetimeSeconds = 8.0;

        public const ushort RequestMsg = NetworkMessageIds.KissRequest;
        public const ushort PromptMsg = NetworkMessageIds.KissPrompt;
        public const ushort ResponseMsg = NetworkMessageIds.KissResponse;
        public const ushort StartMsg = NetworkMessageIds.KissStart;
        public const ushort AlertMsg = NetworkMessageIds.KissAlert;
        public const ushort UnavailableMsg = NetworkMessageIds.KissUnavailable;
    }
}
