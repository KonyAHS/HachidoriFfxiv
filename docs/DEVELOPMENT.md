# Build and release

Requires Windows, .NET 10 SDK and Dalamud API 15 development assemblies
(normally `%APPDATA%/XIVLauncher/addon/Hooks/dev`).

```powershell
./build.ps1 -Verify
```

The installable package is
`HachidoriFfxiv/bin/x64/Release/HachidoriFfxiv/latest.zip`.
For development, add the adjacent `HachidoriFfxiv.dll` to Dalamud's Dev Plugin
Locations. Keep all dependency DLLs alongside it and disable any installed copy.

To release, update `<Version>` in the project, commit to `main`, then push a
matching tag, for example `v0.1.5.0`. The workflow builds, runs offline checks,
and publishes `latest.zip`, `pluginmaster.json`, `source.zip` and checksums.
The feed uses version-specific package links; the installation URL always
points to the latest release's feed. No manual feed commits are needed.

Run the packaging step locally from a committed checkout with:

```powershell
./scripts/package-release.ps1 -Repository KonyAHS/HachidoriFfxiv -Tag v0.1.5.0
```

CI downloads official Dalamud assemblies. It stops if the stable distribution
has moved beyond API 15, so a new game/API version must be handled explicitly.
Offline checks do not replace testing native hover, cutscenes, screenshots,
audio and real Anki saves in game.

Keep personal configuration, diagnostic exports, crash dumps and debug symbols
out of commits and release assets. Use a GitHub no-reply commit email.
