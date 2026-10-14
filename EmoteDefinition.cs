namespace CatPatchspace
{
    internal sealed class EmoteDefinition
    {
        public readonly string Id;
        public readonly string Label;
        public readonly string BodyState;
        public readonly string ArmsState;
        public readonly bool KeepEyesClosed;
        public readonly bool FreezeAtEnd;
        public readonly bool StartFromSit;
        public readonly float SitPrepSeconds;
        public readonly float TransitionSeconds;
        public readonly float HoldNormalizedTime;

        public EmoteDefinition(
            string id,
            string label,
            string bodyState,
            string armsState,
            bool keepEyesClosed = false,
            bool freezeAtEnd = false,
            bool startFromSit = false,
            float sitPrepSeconds = 0.0f,
            float transitionSeconds = 0.0f,
            float holdNormalizedTime = -1.0f)
        {
            Id = id;
            Label = label;
            BodyState = bodyState;
            ArmsState = armsState;
            KeepEyesClosed = keepEyesClosed;
            FreezeAtEnd = freezeAtEnd;
            StartFromSit = startFromSit;
            SitPrepSeconds = sitPrepSeconds;
            TransitionSeconds = transitionSeconds;
            HoldNormalizedTime = holdNormalizedTime;
        }
    }
}
