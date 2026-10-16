using UnityEngine;

namespace CatPatchspace
{
    internal static class HugSettings
    {
        public const KeyCode FallbackHugKey = KeyCode.H;
        public const float SearchRadius = 4.5f;
        public const float MaxAcceptedHugDistance = 5.0f;
        public const float MaxVisualHugDistance = 6.0f;
        public const float DurationSeconds = 3.0f;
        public const double RequestCooldownSeconds = 1.0;
        public const double RequestAckTimeoutSeconds = 3.0;
        public const double PromptAckTimeoutSeconds = 2.5;
        public const double PromptLifetimeSeconds = 10.0;
        public const double HugHardLockSeconds = 10.0;
        public const double HugMoodCooldownSeconds = 600.0;
        public const float HugMoodScaleStartProgress = 0.7f;
        public const float HugMoodScaleMinAtStart = 0.01f;

        public const ushort HugRequestMsg = NetworkMessageIds.HugRequest;
        public const ushort HugPromptMsg = NetworkMessageIds.HugPrompt;
        public const ushort HugResponseMsg = NetworkMessageIds.HugResponse;
        public const ushort HugStartMsg = NetworkMessageIds.HugStart;
        public const ushort HugAlertMsg = NetworkMessageIds.HugAlert;
        public const ushort HugPromptAckMsg = NetworkMessageIds.HugPromptAck;
    }
}
