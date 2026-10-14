using Together;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class KissPromptState
    {
        public knetid RequesterClientId { get; private set; } = CatPatchNetId.None;
        public string RequesterName { get; private set; } = "";
        public double ExpiresAt { get; private set; }

        public bool HasIncomingPrompt
        {
            get { return !CatPatchNetId.IsNone(RequesterClientId) && Time.realtimeSinceStartupAsDouble < ExpiresAt; }
        }

        public void Set(knetid requesterClientId, string requesterName)
        {
            RequesterClientId = requesterClientId;
            RequesterName = string.IsNullOrWhiteSpace(requesterName) ? "Player" : requesterName;
            ExpiresAt = Time.realtimeSinceStartupAsDouble + KissSettings.PromptLifetimeSeconds;
        }

        public void Update()
        {
            if (!CatPatchNetId.IsNone(RequesterClientId) && Time.realtimeSinceStartupAsDouble >= ExpiresAt)
                Clear();
        }

        public void Clear()
        {
            RequesterClientId = CatPatchNetId.None;
            RequesterName = "";
            ExpiresAt = 0.0;
        }
    }
}
