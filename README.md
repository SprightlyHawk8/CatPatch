# CatPatch v5

This is the active CatPatch project and the target for new work.

- Multiplayer target: `CasualtiesMP` / Together
- Target framework: .NET Standard 2.1
- Namespace: `CatPatchspace`
- Host-transfer: Complete Ass probably

## Building from source

Copy `vars.targets.example` to `vars.targets` in this folder, then edit the
paths for your own Casualties: Unknown installation. `vars.targets` is ignored
by Git and should stay local to your computer.

The build needs the game's managed assemblies and the installed Together v5
multiplayer DLLs. A successful build copies `CatPatch.dll` into the game folder
set by `BaseGamePath`. Set `SteamPluginDir` only if you also want it copied to a
second game installation for testing purposes.

## Sprays and troubleshooting

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
