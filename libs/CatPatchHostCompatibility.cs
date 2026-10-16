using Together;

namespace CatPatchspace
{
    internal static class CatPatchHostCompatibility
    {
        private static bool _hostMissingCatPatch;
        private static bool _warnedThisSession;

        public static bool HostMissingCatPatch
        {
            get { return _hostMissingCatPatch; }
        }

        public static bool CanSendHostFeature(string featureName)
        {
            if (KrokoshaScavMultiplayer.is_server || !_hostMissingCatPatch)
                return true;

            ShowHostMissingMessage(featureName);
            return false;
        }

        public static void MarkHostMissing(string featureName)
        {
            if (KrokoshaScavMultiplayer.is_server)
                return;

            _hostMissingCatPatch = true;
            ShowHostMissingMessage(featureName);
        }

        public static void MarkHostResponded()
        {
            _hostMissingCatPatch = false;
        }

        public static void Reset()
        {
            _hostMissingCatPatch = false;
            _warnedThisSession = false;
        }

        private static void ShowHostMissingMessage(string featureName)
        {
            if (_warnedThisSession)
                return;

            _warnedThisSession = true;
            string feature = string.IsNullOrWhiteSpace(featureName) ? "CatPatch features" : featureName;
            HugController.ShowLocalMessage("Host does not appear to have CatPatch. " + feature + " is disabled on this server.");
        }
    }
}
