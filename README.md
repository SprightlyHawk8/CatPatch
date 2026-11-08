# CatPatch v5

This is the active CatPatch project and the target for new work.

- Multiplayer target: `CasualtiesMP` / Together
- Target framework: .NET Standard 2.1
- Namespace: `CatPatchspace`
- Version: `1.1.0`

Host transfer has been removed from the DLL and host moderation menu. Its
unfinished implementation is retained in [archive/HostTransfer](archive/HostTransfer/README.txt)
for reference and is excluded from compilation.

## Building from source

Copy `vars.targets.example` to `vars.targets` in this folder, then edit the
paths for your own Casualties: Unknown installation. `vars.targets` is ignored
by Git and should stay local to your computer.

The build needs the game's managed assemblies and the installed Together v5
multiplayer DLLs. A successful build copies `CatPatch.dll` into the game folder
set by `BaseGamePath`. Set `SteamPluginDir` only if you also want it copied to a
second game installation for testing purposes.

## Menu input

Click the **CatPatch** button again to close CP settings. Both settings and the
updater capture input only over their rectangles; outside buttons remain usable.
The bug-report typing fix remains active. The extra Close control and custom
Escape handler have been removed.

The update-available popup uses the original IMGUI layout and buttons, isolated
from Together's multiplayer skin. Like the old CatPatch IMGUI menus, it captures
input over its window rectangle and blocks underlying buttons there. It does not
lock movement or disable the rest of the screen while waiting for a response.
Clicks outside the updater remain available; its closing click stays captured
over the old window rectangle through the following frame.

Main-menu buttons also poll raw mouse input in `AdaptiveButton.Update`, bypassing
IMGUI events and Canvas raycasts. CatPatch patches the actual `overlayActive`
property getter and guards `AdaptiveButton.Clicked` before any menu action runs.
These checks use live screen mouse coordinates and the windows' current bounds,
including after dragging, rather than a stale or window-local IMGUI event position.

The temporary menu click-tracing and startup hook-audit logs have been removed
for 1.1.0. The input guards remain active.

Update notifications still check the version and show the changelog from the
GitHub manifest. **Download on Nexus** opens the
[Nexus Mods files page](https://www.nexusmods.com/scavprototype/mods/69?tab=files)
in your browser, from both the notification and CP settings. Download and install
updates manually with the game closed. CatPatch no longer downloads update
packages, replaces its DLL, or creates an update helper.

CatPatch shortcuts also pause while Together's multiplayer/pause menu is open,
including its bug-report editor, while chat is focused, or while typing in a Unity
text field. Opening a menu cancels a held spray selector or emote wheel, so releasing
the key after closing the menu does not place a spray. Console/crafting guards remain.

## Sprays and troubleshooting

Sprays accept PNG, JPEG and GIF contents up to **20,000,000 bytes (20 MB)**, with
width and height each **below 4096 pixels**. GIFs also have a 256-frame limit and
a 64 MiB decoded RGBA-frame budget. These checks apply to both backend and lobby
images before native decoding; filenames do not determine the format. Unsupported
formats, appended data, APNG and compressed PNG metadata are rejected.
The [security review](SECURITY_REVIEW.txt) describes the protections and remaining
native-decoder/transport risks; these limits are not a malware scanner or a
guarantee against every game crash.

Sprays use the backend normally, with a lobby-host backup for connection failures.
In **CP → Client Preferences**, enable **Show sprays** and **Send sprays over
Steam fallback** to share backup copies of your images with the host. This
personal sending option defaults to **off**; existing saved choices are retained.
The host's separate **Lobby spray fallback** relay defaults to **on** and can
serve other players even when the host's personal sending option is off.
A successful backend upload places normally while its backup transfers in the background.
If the upload fails, placement waits for the host to accept the image instead.
Downloads try the local cache, then the backend, then the lobby host; images that
exist only in the lobby skip the backend. Receiving a backup does not require
enabling your own outgoing backup uploads.

Turning **Show sprays** off also switches **Send sprays over Steam fallback** off,
saves that choice, and cancels active personal transfers. Turning sprays back on
leaves sending off until you enable it again. The host can still relay other
players' images while hiding sprays locally. Hosts control that separately under
**Host Moderation → Sprays → Lobby spray fallback**.

The host and players using the backup need this updated CatPatch. Normal backend
sprays retain their existing IDs; older versions cannot display lobby-only images.
The hardened fallback uses protocol 2 and does not negotiate with protocol-1 peers.
Backend-ID uploads must match the host's verified cache or an authoritative backend
download. If the host cannot verify a normal ID, that backup is rejected; ordinary
backend placement still works. Content-hashed lobby-only images do not require the
host to reach the backend. Conflicting content cannot replace an existing ID.
Existing placement cooldowns, mutes and clear commands apply to both delivery paths.
The backup uses Together's lobby transport (Steam in Steam lobbies), with bounded
fragments, checksums, retries and session cleanup. No host-transfer support is added.

Host config: `AllowSprayFallback` defaults to true; `MaxSprayFallbackMiB` defaults
to 20 (range 1–20), with a non-negotiable cap of 20,000,000 bytes. Existing lower
host settings remain lower. The byte cache uses at most 64 MiB in memory and 256 MiB on disk
under `BepInEx/config/Cat-Patch/SprayFallbackCache`, with oldest entries evicted.
A lobby-only image is unavailable if the host no longer has its cached copy.
Peers can request only images placed in the current layer, with at most 256 IDs
remembered. Requests never make the host fetch arbitrary backend IDs. Per-peer and
global transfer/request quotas bound repeated offers, cancellations and packet work.
The renderer retains at most 128 wall sprays and 128 cached textures, with separate
encoded-byte and pixel budgets. Clear sprays or change layers to reclaim the wall
cache. The inspector retains only one decoded GIF at a time.

**Clear spray fallback cache on startup** defaults to on when **Show sprays** is
enabled. On the next game launch, this clears previous downloaded backup images
from `SprayFallbackCache` before opening the cache. Your original spray library
is untouched. Turn this option off to retain downloads across launches. Its saved
choice is retained when hiding/showing sprays; startup cleanup does not run when
Show sprays is off. Changes to this option apply on the next launch.
On upgrade, files from the old unauthenticated cache format are discarded once,
even when startup cleanup is disabled. New cache files have a `v2-` prefix.

Logs use `[Sprays] stage=fallback.*`, `upload.backend_failed`,
`download.backend_failed` and `download.fallback` to distinguish backend failures,
host negotiation, backup storage and delivery. A successful background seed means
the host accepted bytes; it does not prove every player displayed the image.
Run the simulated transport checks with
`dotnet run --project tests/SprayFallback/Checks.csproj`; see that folder's README
for the remaining live multiplayer checks.

Hold the spray key (default `G`) to choose a file, then release to place it.
Inspect a wall spray with `I` to play its GIF animation. The selector and
uninspected wall spray show the first frame. Image contents determine the
format; renaming a JPEG to `.gif` does not make it animate.

The selector uses larger logical sizes and display DPI scaling. Adjust
**Spray selector size** in CatPatch's **Client Preferences** (0.75–3x), or
`SprayMenuScale` in the BepInEx CatPatch config. Small windows shrink the menu
only enough to fit; opening near an edge keeps the choices on screen.

For failures, reproduce once and collect `BepInEx/LogOutput.log` from the
affected player and host. Search for `[Sprays]`, then follow the filename,
spray `id`, position, and HTTP `request` number:

| Stage | What it tells you |
| --- | --- |
| `selector.decode`, `placement`, `file.read` | Preview decoding, missing files/camera, cooldown, or a disk read failure |
| `upload.enqueue`, `upload.cache`, `upload.request`, `upload.complete` | Whether the upload started, reused an ID, or returned an ID |
| `http.send`, `http.read`, `json.decode`, `upload.response` | Endpoint, request number, elapsed milliseconds, HTTP status, backend rejection, malformed response, or exception chain; socket errors help distinguish DNS/connectivity failures, and canceled requests identify timeouts |
| `network.send`, `network.accept`, `network.broadcast`, `network.receive` | Request sent to host, accepted, relayed, and received by a peer |
| `network.reject`, `network.host_ack`, `network.client_ack` | Host cooldown/mute rejection, receipt acknowledgments, or timed-out peers |
| `download.*`, `render.*`, `inspect.decode` | Backend download/cache, image decoding and sprite creation, visibility/layer skips, or inspection animation decoding |

Acknowledgments confirm **packet receipt**, not successful download or display.
Check the receiving player's `download` and `render` lines as well. A backend
HTTP rejection such as `HTTP 400: invalid_image` is distinct from a connection
failure. Requests have a ten-second timeout. Logs omit the image/base64 payload;
they include filenames, spray IDs, positions, and connection error messages.

Focused local checks and their setup are in [tests/Sprays](tests/Sprays/README.md).

## Creator join announcement

The host detects the creator's Steam identity and sends `Mr CatPatch has joined.`
to connected CatPatch clients. Both the host and receiving players need CatPatch.
Alerts received while loading wait for the player HUD; leaving the session
clears any pending alert. Display names and session player numbers do not
identify the creator.

For a missing alert, collect the host's and affected client's
`BepInEx/LogOutput.log` and search for `[CreatorJoin]`. Follow `host monitoring`,
`creator detected` (including the identity source), `broadcast sent`,
`host announcement received`, and `announcement displayed`. A `waiting for
player HUD` line means the alert is retained until the HUD is ready. Identity,
send, and display exceptions are logged. A broadcast log proves a send attempt
completed, not that every peer displayed it.

Run the focused lifecycle checks without the game:
`dotnet run --project tests/CreatorJoin/Checks.csproj`. They compile the actual
announcer with game/network boundary stubs; real Steam joins still need a
host/client test.

## Editing Voice lines

Uh, voice lines are located in libs/TalkerCatPatch.cs and features/Kiss/KissDialogue.cs

Yikes those are some weird ass fucking lines I wrote with my friends at like 1 am

I am more open to changing them
