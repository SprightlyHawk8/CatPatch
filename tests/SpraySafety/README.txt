Run dotnet run --project tests/SpraySafety/Checks.csproj from the v5 source folder.

25 checks exercise the actual decoder-independent image preflight and token bucket:
exact decimal 20 MB boundary, 4095/4096 dimension boundaries in every format, GIF
frame/decoded-memory limits, invalid/truncated/overlapping structures, appended
payloads, PNG CRC/length/APNG/compressed metadata, JPEG height-redefinition and
unsupported coding, 3000 deterministic malformed inputs, and cumulative quotas.
No native decoder, live HTTP request or Steam socket is invoked by this suite.

Native Unity/GDI and bounded backend-stream checks are in tests/Sprays. Fallback
attack regressions with actual transport/service/cache source are in
tests/SprayFallback. Passing these checks is not proof of a vulnerability-free
image decoder, Steam/Together transport or arbitrary multiplayer configuration.
