# Changelog

All notable changes to **Hachidori for FFXIV** are documented in this file.

> **Release policy:** Every release MUST have a `## v<version>` section below with bullet-point feature descriptions. Both `./build.ps1 -Verify` and the GitHub Actions release workflow enforce this and refuse to publish releases without feature notes.

## v2.0.5

Quick dialogue voice replay, controller dialogue backlog, durable build-stamped diagnostics, consistent Anki template resolution, and expanded FFXIV raid dictionary coverage.

### Voice Replay & Controller Dialogue Backlog
- **Quick voice line replay (`/hachidori replay` / `/hachidori repeat`):** Caches recent voiced lines for instant replay via slash command, a configurable keyboard/mouse/back-paddle keybind (e.g., `F13`–`F24`, Mouse 4/5, or modifier chords), a dedicated cutscene gamepad button/combo (`Press` or `Hold` trigger style, cutscene-only option), or inside controller mode and the dialogue backlog.
- **Controller-first dialogue backlog (`/hachidori backlog` / `/hachidori history`):** On-demand dialogue history drawer for scanning, looking up, replaying voice lines, and mining previous lines with cached screenshot previews and media badges (works with both gamepad and mouse). Includes configurable bindings (`L3` history, `R3` replay voice by default) and options for auto-opening backlog, pinning on dialogue advance, and including chat lines.
- **Improved dialogue capture:** Refined dialogue and voice line capture heuristics to preserve lines cleanly across dialogue transitions and associate cached media reliably.

### Anki Mining & FFXIV Dictionary
- **Consistent Anki template resolution across backends:** Passes the selected Anki template ID through status, preflight, submit, and browse across both live Hachidori and Offline Mode, and closes open popups cleanly when switching backends.
- **Expanded FFXIV JP–EN dictionary (`2026.11.24.1`, 37,840 entries):** Added Japanese raid calls, localized Savage mechanic names (Eden's Promise and Asphodelos), Ninja/gameplay terms, treasure map grades (`G1`–`G14`), and expanded materia entries.

### Diagnostics & Release Hygiene
- **Durable build-stamped diagnostics:** Persists session logs (stamped with plugin commit/dirty state, DLL SHA-256, FFXIV version, Dalamud version, and native engine manifest) to survive crashes, forwards throttled errors/warnings to `dalamud.log`, correlates requests with operation IDs and durations, and fingerprints repeated exceptions.
- **Enforced release feature logs:** Added `CHANGELOG.md` and automated release-notes validation in `./build.ps1 -Verify`, `scripts/package-release.ps1`, and `.github/workflows/release.yml` so every release must include curated feature descriptions before publishing.

## v2.0.4

Reliability, controller navigation, audio isolation, and patch-day resilience release for Hachidori for FFXIV.

### Controller & Embedded WebView2 Popup
- **Controller dive in WebView2 popups (#8):** Navigate definitions inside browser-style WebView2 popups with a gamepad, dive into nested child popups, step back layer by layer, auto-scroll active entries, and display the controller cursor underline above the overlay.
- **WebView2 DOM resilience & automatic rollback (#14):** Added shadow-root and geometric popup detection fallbacks so mouse hover and hit-testing survive upstream Hachidori CSS/tag renames. A hidden post-boot lookup smoke test now verifies rendering before marking a bundle healthy, automatically rolling back to the last known-good bundle (with cache-busting) if an update breaks.

### Voice Recording, Mining & AnkiConnect
- **Clean voice recording without pronunciation bleed (#8):** Tracks plugin and WebView2 pronunciation playback (`PluginAudioTracker`) and extracts clean game voice audio directly from FFXIV `.scd` files via Lumina (`ScdVoiceExtractor`) whenever pronunciation overlaps a voiced line, while masking pronunciation audio from the RMS fallback detector.
- **Complete AnkiConnect API key support (#8):** Propagates Hachidori's configured `anki.apiKey` (plus an optional override in *Settings → Mining → Advanced → AnkiConnect & Fields*) across `MediaMiner`, `AnkiCardFeed`, connection tests, and offline batched `multi` media uploads.
- **Resilient voice detection & hot-patch overrides (#14):** Increased `SoundManager.SoundDataPool` polling to ~25 Hz as a responsive co-primary detector alongside native hooks, added an amber status indicator in *Settings → Mining* if voice hooks fail to attach, and exposed optional signature override fields for patch-day fixes without a rebuild.

### Patch-Day Resilience, Diagnostics & Future Expansions
- **New `/hachidori diag` command (#14):** Prints a one-command in-game health summary covering UI capture nodes, voice detection mode, WebView2 smoke test/fallback status, relay protocol state, and MSQ index (also included in exported diagnostic logs).
- **Hardened FFXIV UI capture (#14):** Switched `AddonTalk` to geometric text-node selection (preserving short lines and transition frames), added `AddonTalkSubtitle` fallback, dynamically discovers all loaded `ChatLogPanel` addons, and uses `DepthLayer` alongside name heuristics for tooltip detection.
- **Hachidori Relay protocol hardening (#14):** Added protocol version range negotiation, capability checks, actionable handshake mismatch errors mentioning Offline Mode fallback, and a 2-second close-handshake timeout to prevent stalled disconnects.
- **8.0+ expansion readiness & Canary CI (#14):** Dynamically registers future MSQ expansions from Lumina's `ExVersion` sheet with updated JP/EN quest genre filtering, and adds a daily Canary CI workflow testing against Dalamud staging (`stg`) and the latest upstream Hachidori web releases.

### Stability & Architecture
- **Shutdown cleanup fix (#8):** Fixed mismatched event unsubscriptions in `HachidoriService.Dispose()`.
- **Zero-debt layered architecture & codebase cleanup (#15–#20):** Extracted `DalamudServices`, eliminated all namespace and type dependency cycles into a strict layered DAG, organized `Media` and `ConfigWindow` into cohesive modules, simplified popup/relay/OST internals, and enforced architectural rules in CI with `HachidoriFfxiv.ArchCheck`.
- **Curated release notes workflow:** Updated `.github/workflows/release.yml` to publish annotated tag notes automatically on release tags.

## v2.0.3

Sentence mining and dictionary glossary accuracy release for Hachidori for FFXIV.

### Anki Mining & Lookup Context
- **Mine full FFXIV sentences:** Normalizes the hovered text box (joining line wraps, keeping paragraph breaks as spaces, and trimming whitespace), remaps the lookup offset before opening the popup, and restores the full text-box sentence during Anki preflight, batch preflight, and submission when Yomitan/Hachidori splits at a terminator.
- **Preserve all dictionary glossaries:** Restores every dictionary's glossary when a per-dictionary theme narrows `term.glossaries`, keeping the clicked dictionary first for `{glossary-first}` templates.
- **Accurate popup anchoring:** Anchors the hidden lookup target with an inline before/target layout so the embedded popup lines up precisely with the hovered Japanese character.
- **Regression coverage:** Added automated checks covering lookup context normalization and Anki mining enrichment.

## v2.0.2

Native game voice detection, BGM OST tracking fixes, batched Anki preflight, configurable controller triggers, and early-encounter FFXIV character descriptions.

### Voice Detection & BGM OST Injection
- **Native game voice line detection:** Hooks `LoadSoundFile` and `PlaySpecificSound` (paired with `SoundManager` pool monitoring) to detect real cutscene and battle voice lines directly from FFXIV instead of relying only on RMS loudness heuristics.
- **Lumina `.scd` voice duration reading:** Reads voice `.scd` durations directly from game headers so consecutive voiced lines and loud background music record clean game audio rather than falling back to TTS.
- **BGM OST tracking fix:** Fixed `BGMSystem` scene detection (stopped treating `PlayState == 0` as paused, ignored inactive `BgmId == 0` and sentinel `9999`, and prioritized active Orchestrion playback) so game OST injection mixes cleanly behind TTS clips.

### Anki Mining & Controller Mode
- **Batched Anki preflight & mine notifications:** Batches Anki duplicate/preflight checks for faster popup responses and adds an optional Dalamud toast notification when a card is mined (`Show notification when a card is created`).
- **Configurable controller triggers & manual scan mode:** Added **Hold** vs **Press** controller trigger modes, an optional keyboard/mouse/back-paddle toggle keybind (`F13`–`F24`, Mouse 4/5, modifier chords), and a toggle to disable automatic cursor scanning so you can move the cursor freely and press a dedicated Look up button (`A / ✕` by default).

### Dictionary & Documentation
- **Early-encounter FFXIV character descriptions (`2026.11.23.1`):** Added 192 spoiler-conscious character introductions drawn from each character's first encounters (also surfaced on matching name variants) to `FFXIV-Hachidori-JP-EN.zip`.
- **Performance & resource guide:** Added `docs/PERFORMANCE.md` documenting memory, CPU, GPU, disk, and network costs for each feature along with lightweight presets for low-end PCs and Steam Deck.

## v2.0.1

### What's Changed
- **Dalamud installer polish:** Shortened the `/hachidori` help text shown in `/xlplugins` (use `/hachidori help` for the full command list), set the GitHub repository icon in the plugin manifest, and removed the unused **Open** button from the installer entry.
- **Reader overlay off by default:** Automatically disables the optional reader overlay once when migrating settings from earlier builds so native hover is the clean default.

## v2.0

Major release introducing the embedded Hachidori Web popup, Freeze Frame tooltip scanning, cross-platform compatibility fallbacks, and an expanded Immersion Stats suite.

### Embedded Hachidori Web Popup & Freeze Frame
- **Real Hachidori Web popup (#3):** Runs Hachidori's web frontend inside FFXIV via an embedded WebView2 host and bridge with automatic bundle sync, delivering full theme, layout, audio, and nested-lookup parity.
- **Freeze Frame capture (`Ctrl + Shift + F`) (#3):** Freezes the screen and visible item, skill, and status tooltips so you can hover and mine them without them vanishing, with configurable hotkeys and improved native chat alignment and segmentation caching.

### Cross-Platform Compatibility & Dictionary Shortcut
- **Linux (Wine/Proton), Steam Deck & macOS fallbacks (#3):** Automatic platform detection with ImGui popup fallback, WinMM/FFmpeg audio fallbacks, and system loopback recording.
- **In-game FFXIV dictionary shortcut:** Added detection and a direct download shortcut for `FFXIV-Hachidori-JP-EN.zip` in settings.

### Immersion Stats & Ecosystem Support
- **Expanded stats dashboard:** Adds Goals, MSQ/expansion progress, NPCs, Words, Anki, and Achievements tabs.
- **Ecosystem import/export:** Supports exSTATic, Rubi, GSM, Kechimochi, Tidepool, Telly, and SubMiner with a live `history.csv` and SQLite log.
- **Persistence & responsiveness:** Single-writer persistence with duplicate-safe batch retries, `VACUUM INTO` SQLite exports (capturing WAL rows), full `Clear all` cleanup, and off-draw-thread snapshots and file actions.

### Media, Versioning & Docs
- **Game OST injection for TTS:** Optionally blends the active game BGM behind generated TTS clips.
- **Encoder throttling:** Limits concurrent background animation encodes to two and cancels encodes for evicted clips.
- **2.x versioning & docs:** Switched to `2.x` versioning (`v2.0`, assembly `2.0.0.0`) and refreshed `README.md` with demo visuals and dedicated feature, compatibility, and troubleshooting guides under `docs/`.

## v0.1.7.3

Major feature release adding Offline Mode, built-in Anki media mining, Hachidori popup themes, Controller Reading Mode, Immersion Stats, and the Patch 7.58 FFXIV dictionary.

### Offline Mode & Native Engine
- **Built-in native lookup engine (`hdw_native.dll`):** Compiles Hachidori's lookup engine (`hoshidicts` + `bindings.cpp`) natively with matching result tie-breaking order and bundled KANJIDIC/kanwa furigana tables.
- **Automatic sync & seamless fallback:** Syncs dictionaries and settings whenever Chrome/Hachidori is linked, then keeps lookups, URL/JSON pronunciation audio, and Anki mining (via AnkiConnect using your Hachidori template and glossary renderer) working when Chrome is closed.
- **Native source publishing:** Publishes `native-source.zip` with the patched native sources alongside release assets.

### In-Game Anki Media Mining & AI Translation
- **Game media capture (no GSM required):** Captures clean game screenshots (plugin UI hidden), records game-only voice lines via Windows process loopback (speech-trimmed, leveled, Ogg Opus), generates VOICEVOX/custom-URL TTS for unvoiced lines, and background-encodes animated line clips (720p / 12fps AVIF or WebP via FFmpeg, with built-in GIF89a fallback).
- **Sentence translation:** Supports Gemini, Groq, OpenAI-compatible APIs, Ollama, LM Studio, Z.ai, DeepL, and local Codex/Claude Code CLIs with GSM prompt templates and **Import from GSM**.

### Hachidori Themes & SVG Images
- **Hachidori popup themes (#1):** Follows your selected Hachidori theme (Default palettes, Nazeka, Plain, JL, Bee's Theme, Crystal), opacity, and custom CSS, with a new **Theme** settings tab and automatic 4.5:1 text contrast adjustment.
- **SVG dictionary images:** Renders SVG assets such as Daijirin badges and KanjiVG stroke diagrams directly in popups.

### Controller Mode & Immersion Stats
- **Controller reading mode (`/hachidori pad`):** Step word-by-word or character-by-character over dialogue, battle talk, and subtitles with D-pad/stick, navigate UI windows with a virtual cursor/touchpad, dive into nested popups, and customize controller bindings.
- **Immersion Stats & Anki card feed:** Dedicated window tracking reading, lookups, NPCs, and mining activity alongside recent Anki cards and settings reset controls.

### Hover, Chat, Dictionary & Performance
- **Native chat hover fixes (#2):** Restricted hover to the active chat tab/panel, aligned hitboxes with native chat rows while scrolling, and improved line-wrapped segmentation, interior particle handling, character-mode lookup, and popup scroll reset.
- **NPC pictures & Patch 7.58 dictionary:** Consolidates NPC portraits into a single Hachidori dictionary and updates `FFXIV-Hachidori-JP-EN.zip` (now attached directly to releases) to `2026.11.22.1` (Patch 7.58) with expanded character coverage and optimized portraits.
- **Settings & performance:** Added an **Advanced settings** toggle to keep everyday options uncluttered, reused capture buffers, computed themes asynchronously, and trimmed unused audio library code.

## v0.1.7.0

### What's Changed
- **Crisp Japanese text at every size:** Rasterizes the Japanese face of Noto Sans CJK at each exact pixel size drawn rather than scaling a single font texture (fixing blurriness below 25px with Dalamud's default AXIS font).
- **Consistent rendering:** Applies across headwords, definitions, furigana, and chips in both popups and the reader overlay, with whole-pixel bold offsets for cleaner rendering.

## v0.1.6.0

### What's Changed
- **Chat hover fix:** Read chat text directly from `ChatLogPanel` addons (fixing cases where hovering the outer `ChatLog` window returned no text) and treat each chat message as its own sentence for cleaner lookups and sentence extraction.
- **Opt-in GameSentenceMiner (GSM) text feed:** Added a loopback WebSocket feed with content and port presets (off by default).
- **Documentation:** Expanded `README.md` with feature overview and credits.

## v0.1.5.0

Initial custom-repository release of **Hachidori for FFXIV** (Dalamud API 15, Windows).

### Highlights
- **Native hover lookups:** Hover Japanese dialogue, subtitles, battle talk, and chat directly in game for dictionary popups with furigana, pitch accent, frequency tags, kanji info, and nested lookups powered by Hachidori via Hachidori Relay.
- **Flexible scanning & overlays:** Plain hover or optional scan key (`Shift`/`Ctrl`/`Alt`), plus an optional reader/history overlay (`/hachidori reader`) and manual search window (`/hachidori search`).
- **Pronunciation & Anki mining:** Play dictionary audio and mine words straight to Anki using your configured Hachidori template.
- **NPC pictures & player names:** Crop NPC screenshots into a linked Hachidori dictionary and render natural katakana readings for your character's name.
- **Optional FFXIV JP–EN dictionary:** Includes the build tooling and dataset in `dictionaries/ffxiv/` for FFXIV names and terminology.
