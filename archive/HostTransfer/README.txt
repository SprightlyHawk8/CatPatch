Archived host transfer - 2026-11-07

Removed at the user's request because host transfer is unreliable. This folder
is reference material, not a feature or standalone project. CatPatch.csproj
excludes archive/**/*.cs from compilation. No transfer component, packet
handlers, prompts, or transfer-specific QoL hooks are installed by CatPatch.

Preserved unchanged from features/HostTransfer:
- HostTransferController.cs: previously active protocol-5 save/snapshot transfer,
  Steam lobby ownership change, transport reconnect and player restoration.
- HostTransferControllerV5.cs: earlier diagnostic controller, already excluded
  before archival. It declares the same controller type as the newer file;
  never enable both together.
- QoLHostTransferCompatibility.cs: optional QoL seed/save-loading adaptations,
  including the QoLSeedState snapshot type.
- RetainedWorldSnapshotProbe.cs: terrain, player and sync-object fingerprints
  and comparison helpers that may be useful for future migration investigation.

NetworkMessageIds.cs preserves the removed packet constants. The live registry
reserves 31130-31137 to avoid repurposing old clients' transfer packets. This
archived registry fragment cannot be compiled alongside the live registry as-is.

Integration removed from the active build:
- Plugin.Awake no longer adds HostTransferController.
- Host Moderation no longer exposes Transfer Host or TryTransferHostSelected.
- The active packet registry no longer exposes the transfer message constants.

These sources depend on CatPatch helpers and the historical installed Together,
game, Steamworks, LiteNetLib and optional QoL APIs; they are not self-contained.
Prior checkpoint claims are historical. Do not restore this by simply removing
the build exclusion: verify current APIs, authority/lifecycle, save safety and
matching-client multiplayer behavior first. Spray fallback uses its own lobby
relay and does not depend on this archive.
