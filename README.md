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
