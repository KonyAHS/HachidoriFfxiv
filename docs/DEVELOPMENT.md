# Build and release

Requires Windows, .NET 10 SDK and Dalamud API 15 development assemblies
(normally `%APPDATA%/XIVLauncher/addon/Hooks/dev`).

```powershell
./build.ps1 -Verify
```

`build.ps1` first builds the offline engine, `hdw_native.dll`, with
`native/build.ps1`. That needs git, CMake 3.22 or later and Visual Studio's C++
workload (x64); it fetches hoshidicts and Hachidori's `wasm/bindings.cpp` at the
commits pinned in `native/sources.json` into `native/src`, applies
`native/patches`, and only rebuilds when one of its inputs changed. Pass
`-SkipNative` to reuse an existing DLL. CI caches `native/bin`.

To follow a new Hachidori release, update `native/sources.json` (Hachidori's
commit and the `third_party/hoshidicts` submodule commit it pins, plus the
SHA-256 of its `bindings.cpp`), refresh the patches if they no longer apply,
copy `vendor/kanjidic/kanji-readings.json` and `vendor/kanwa/kanwa-readings.json`
into `HachidoriFfxiv/Offline/Data` when they changed, and run the parity check
below. `native/compat/hdw_libcxx_sort.hpp` reproduces Emscripten's libc++
`sort`/`partial_sort`, because hoshidicts leaves ranking ties to them; if a
newer Emscripten changes those algorithms, tied results will differ in the
parity check until the header follows.

The offline checks in `-Verify` run without a host. To compare offline answers
with live Hachidori (Anki with the Relay and Chrome with Hachidori sharing):

```powershell
dotnet run -c Release --project tests/HachidoriFfxiv.Parity
```

It syncs your dictionaries into a temporary store, sends the same segment,
lookup (at every character), kanji and image requests to both, and prints each
difference. `--lines FILE` uses your own lines, `--show TEXT` lists both result
lists for one lookup.

The installable package is
`HachidoriFfxiv/bin/x64/Release/HachidoriFfxiv/latest.zip`.
For development, add the adjacent `HachidoriFfxiv.dll` to Dalamud's Dev Plugin
Locations. Keep all dependency DLLs alongside it and disable any installed copy.

To update the existing dev plugin registered in the running game's directory,
use `./deploy-local.ps1` from the checkout with the integrated media and chat
features. It runs the regression suite, checks the built DLL for those features,
rejects version downgrades, backs up replaced files and checks the installed hash.
`./deploy-local.ps1 -WhatIf` runs the checks without replacing any files.
Check `%APPDATA%/XIVLauncher/dalamud.log` for a successful reload.

To release, update `<Version>` in the project, commit to `main`, then push a
matching tag, for example `v0.1.5.0`. The workflow builds, runs offline checks,
and publishes `latest.zip`, `pluginmaster.json`, `source.zip`,
`native-source.zip` (the patched sources `hdw_native.dll` was built from) and
checksums.
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
