# CatPatch V5 Port Status

## Rescue and host-transfer playtest fixes — 2026-10-09

- Replaced the runtime-missing `JToken.ToString(Formatting)` snapshot call
  with one-argument JSON serialization; used the same serializer for the
  current-layer save adjustment.
- Resolved self-harm rescue ownership from `ScavPlayer.LocalPlayer.body`
  instead of the camera's current body, added player-ID fallback when a body
  has no attached network component, and logged local/server/client starts.
  The failed playtest used the no-gun path; old logs did not identify which
  rescue stage failed.
- Built and deployed the v5 DLL with zero warnings/errors. Steam main-menu
  startup succeeded. Rescue flashing and host transfer still require a
  matching-DLL, two-player gameplay retest.

## Together 5.0.0test5 compatibility — 2026-10-03

- Updated renamed player/body lookup APIs across hugs, kisses, emotes,
  dialogue, challenge starter kits, rescue, piggyback throws, and moderation.
- Retargeted team-selector patching to `Con.PerformActionOnPlayersByName`.
- Updated host-transfer capture/apply to `LastStartGameAnnouncementPacket`;
  replaced the removed validity flag with host/world readiness and initialized
  RNG checks. Migration protocol 5 rejects older packet layouts.
- Updated the voice receiver hook spelling and the migration startup diagnostic
  to the lifecycle method actually used (`OnTransportConnected`).
- Aligned the displayed plugin version with the project version: 1.0.6.
- Build/deploy succeeded with zero warnings/errors against the installed
  playtest. Main-menu initialization succeeded; multiplayer feature checks,
  QoL coexistence, and protocol-v5 host transfer remain unverified.
- Original edited source and the previous Steam DLL are backed up under
  `backups/playtest-2026-10-03/`.

## Checkpoint 1: Compiling V5 Foundation

Completed:

- Retargeted CatPatch from `KrokoshaCasualtiesMP.dll` to `CasualtiesMP.dll`.
- Build references resolve from the installed
  `BepInEx/plugins/Together` folder; multiplayer DLLs should not be added to the
  project as source files or bundled with CatPatch.
- Retargeted the project from .NET Framework 4.8 to .NET Standard 2.1.
- Updated the BepInEx dependency GUID to `CasualtiesMP`.
- Migrated the multiplayer namespace from `KrokoshaCasualtiesMP` to `Together`.
- Migrated player references from `NetPlayer` to `ScavPlayer` and updated renamed members.
- Added `CatPatchNetwork`, which maps CatPatch's existing logical message keys to
  Together v5 named messages under `meow.catpatch.v5.*`.
- Updated all active CatPatch message writers and receiver registrations to use
  the v5 named-message API.
- Updated v5 keybind and UI reflection namespace lookups.
- Kept the release single-DLL compatible by resolving the game's existing
  `System.Drawing.dll` GIF decoder at runtime instead of adding
  `System.Drawing.Common.dll` as a new dependency.
- Verified a non-deploying debug build with zero warnings and zero errors.
- Validated the guarded v5 host-migration snapshot with three players and two
  different transfer targets.
- Enabled the experimental full transfer stage: the host sends a chunked,
  SHA-256-verified Together multiplayer save; the recipient installs it with a
  recoverable backup; the old host then transfers Steam lobby ownership and all
  peers rebuild their Together v5 transports inside the same lobby.
- The promoted host regathers peers, applies the transferred start-game state and
  compiled run settings, loads the multiplayer save, and restores player
  positions by Steam identity.
- Completed a two-player runtime authority switch with player health, inventory,
  identity, and position restoration intact.
- Corrected the observed one-layer advance by rewriting only the in-memory
  migration archive's world and per-player `save.sv` biome fields to the current
  layer; normal Together save progression is unchanged.
- Added an optional, reflection-only QoL Unknown migration guard after a
  promoted-host test showed QoL copying its remembered slot over Together's
  verified `mp_save/save.sv`. The guard forces the transferred root save,
  clears stale QoL slot-load state, and controls QoL seeded world generation
  only through the controlled reload and position restore.
- Advanced host migration to protocol v4. The source host's optional QoL seed
  capability/state is distributed to every peer; compatible QoL clients use
  that exact seed, while non-QoL source runs continue to suppress recipient-only
  QoL worldgen.
- Added CatPatch-owned live position metadata captured from the authoritative
  host bodies. Restoration now waits for Together's remote worldgen and delayed
  spawn-location flow, then checks and corrects any post-restore movement.
- Added post-reload terrain verification against the source host's block/fluid
  fingerprint.
- Added retained-world diagnostics to the commit barrier. The host
  fingerprints Together's block/fluid serializers, player/body bindings, and
  `SyncInfo` registry; every peer reports a comparison before ownership changes.
  Mismatches are logged only and the controlled PreGen reload remains the
  fallback.
- Ported the historical CatPatch player-grappling interaction to Together v5.
  Grappling hooks can latch onto another player's body or limbs and apply the
  original distance-scaled pull to both players while Together replicates their
  resulting body velocities.

Experimental / not yet runtime-verified:

- Protocol-v4 QoL seed propagation, authoritative live positions, delayed-spawn
  settling, and post-reload terrain verification.
- The first multiplayer results from the retained-world diagnostic. Relevant
  log entries begin with `retained-world host probe`, `retained-world peer
  probe`, and `retained-world readiness`.
- The current first implementation uses a controlled PreGen reload. Replacing
  LiteEntitySystem managers while keeping the live world scene loaded remains a
  later seamlessness milestone.

Still requires runtime multiplayer testing:

- Host-to-client, client-to-host, and client-to-client custom messages.
- Keybind registration and persistence in Together v5.
- Main-menu, in-game, client-settings, and moderation UI integration.
- Each gameplay subsystem, migrated and tested independently.
- Player grappling with host and client shooters, including simultaneous
  movement, death during a pull, and hook return cleanup.
- Repeat the two-player transfer to verify same-layer reload, then test the full
  transfer with three players.
