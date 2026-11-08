namespace CatPatchspace
{
    // Archived portion of the old registry; merge deliberately if revisiting.
    internal static class NetworkMessageIds
    {
        public const ushort HostTransferOffer = 31130;
        public const ushort HostTransferResponse = 31131;
        public const ushort HostTransferSnapshotBegin = 31132;
        public const ushort HostTransferSnapshotChunk = 31133;
        public const ushort HostTransferSnapshotAck = 31134;
        public const ushort HostTransferCommit = 31135;
        public const ushort HostTransferAbort = 31136;
        public const ushort HostTransferCommitReady = 31137;
    }
}
