# Hachidori for FFXIV

- Before replacing a DLL in a live FFXIV session, identify the installed build
  by its assembly version and the commit in its product version. Build from a
  checkout that includes the user's existing features plus the requested fix.
  Older checkouts used the same `0.1.7.0` version with different features.
  The original media work is on the local `media-mining` branch in the sibling
  `HachidoriFfxiv-media` checkout and is integrated here.
- Use `./deploy-local.ps1` to build, verify, back up and replace the configured
  Dalamud dev plugin. If this script is missing in a checkout, locate the
  integrated checkout before deploying. Avoid copying loose build outputs to the
  running game. Confirm the new build loaded in `dalamud.log`.
- Keep Anki mining, still screenshots, game voice recording, animated screenshots,
  pronunciation, NPC pictures, native chat hover and offline mode (with its
  `hdw_native.dll` beside the plugin) available when fixing other features. Check `./build.ps1 -Verify` for local regression coverage.
