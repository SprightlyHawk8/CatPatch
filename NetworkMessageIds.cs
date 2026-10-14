namespace CatPatchspace
{
    internal static class NetworkMessageIds
    {
        // V5 transports these keys as namespaced named messages. The numeric values
        // remain stable logical keys for the existing CatPatch payload contracts.
        public const ushort SpraySpawn = 31050;
        public const ushort SprayServerAck = 31051;
        public const ushort SprayClientAck = 31052;
        public const ushort SprayAlert = 31053;
        public const ushort SprayReject = 31054;
        public const ushort SprayClear = 31055;

        public const ushort HugRequest = 31060;
        public const ushort HugPrompt = 31061;
        public const ushort HugResponse = 31062;
        public const ushort HugStart = 31063;
        public const ushort HugAlert = 31064;
        public const ushort HugPromptAck = 31065;

        public const ushort KissRequest = 31070;
        public const ushort KissPrompt = 31071;
        public const ushort KissResponse = 31072;
        public const ushort KissStart = 31073;
        public const ushort KissAlert = 31074;
        public const ushort KissUnavailable = 31075;

        public const ushort EmoteRequest = 31080;
        public const ushort EmoteStart = 31081;

        public const ushort PiggybackThrowRequest = 31090;
        public const ushort PiggybackThrowExecute = 31091;

        public const ushort SelfHarmRescueStart = 31100;
        public const ushort SelfHarmRescueEnd = 31101;
        public const ushort SelfHarmRescueAttempt = 31102;
        public const ushort SelfHarmRescueResult = 31103;
        public const ushort SelfHarmRescueState = 31104;
        public const ushort SelfHarmRescueAlert = 31105;
        public const ushort SelfHarmTreatmentAccept = 31106;
        public const ushort SelfHarmRescueIntervene = 31107;
        public const ushort SelfHarmRescueRetaliation = 31108;
        public const ushort SelfHarmRescueObserveStart = 31109;
        public const ushort SelfHarmRescueHandState = 31110;
        public const ushort GunSuicidePushCancel = 31111;
        public const ushort GunSuicidePresentationStart = 31112;
        public const ushort GunSuicidePresentationEnd = 31113;

        public const ushort CreatorJoinAlert = 31120;
        public const ushort AdminPrivilegeGranted = 31121;

        public const ushort HostTransferOffer = 31130;
        public const ushort HostTransferResponse = 31131;
        public const ushort HostTransferSnapshotBegin = 31132;
        public const ushort HostTransferSnapshotChunk = 31133;
        public const ushort HostTransferSnapshotAck = 31134;
        public const ushort HostTransferCommit = 31135;
        public const ushort HostTransferAbort = 31136;
        public const ushort HostTransferCommitReady = 31137;

        public const ushort LegacyHugRequest = 30990;
        public const ushort LegacyHugAlert = 30994;
    }
}
