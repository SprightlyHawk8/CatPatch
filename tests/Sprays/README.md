# Local Unity spray checks

These checks run in the installed Unity/Mono runtime, including its actual
System.Drawing, Newtonsoft.Json, and Unity texture APIs. The main project
excludes this test source. The probe is optional and must not ship with CatPatch.

The hardened 1.1.0 pass has 166 checks, covering real PNG/JPEG/GIF rendering and
inspection plus huge-header rejection before decoding, texture/animation cache
budgets, placement quotas, finite-coordinate validation and the receiving client's
lobby-wide allowance. The linked fake HTTP backend checks early Content-Length
rejection, invalid contents, exact streamed byte boundaries and cancellation.
It makes no real backend request or live Steam lobby connection. Broader managed
preflight/fuzz and simulated-peer attacks are in SpraySafety and SprayFallback.

From the v5 project folder, build CatPatch normally, then:

```powershell
dotnet build tests/Sprays/Checks.csproj -c Debug
./tests/Sprays/New-Fixtures.ps1 -OutputDirectory ../backups/spray-fixtures
$env:CATPATCH_SPRAY_CHECK_DIR = (Resolve-Path ../backups/spray-fixtures).Path
```

You may add real GIFs to that local fixture directory. Do not commit private
images. The fixture generator creates an indexed GIF, a GIF with a partial
second frame and fast timing, and JPEG/PNG files deliberately named `.gif`.

With the game closed, copy only `tests/Sprays/bin/Debug/netstandard2.1/`'s
`CatPatch.SprayChecks.dll` into the test installation's CatPatch plugin folder.
Use the paths in `vars.targets`. Launch that installation with
`-batchmode -nographics`, inheriting the environment variable above. No player
input or saved world is needed. Check `BepInEx/LogOutput.log` for
`SPRAY_CHECKS_COMPLETE passed=... failed=0`, then close that exact test process
and remove `CatPatch.SprayChecks.dll` from the game's plugin folder.

The probe checks visible texture pixels through the actual selector, wall
sprite, and inspector paths; animation frame differences, looping, timing and
subframe composition; static files with misleading extensions; corrupt data;
menu bounds at 800×600, 720p, 1080p, 4K, and ultrawide resolutions, with unknown,
normal, and double DPI and multiple user sizes. The backend tests use a
separate linked copy with fake HTTP replies: they never replace the shipping
plugin's HTTP client or contact the backend. They check success, image rejection,
empty/malformed responses, download failure, DNS errors, timeouts, and logs.

This is a headless component check. It does not prove visible gameplay,
real mouse selection, multiplayer delivery, or another player's downloads.
Check those with matching DLLs on host and client. Outside inspection, GIF
sprays intentionally remain static first-frame sprites.
