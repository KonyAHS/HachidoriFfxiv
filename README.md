<div align="center">

<img src="assets/hachidori-icon.png" alt="Hachidori for FFXIV logo" width="112" height="112">

# Hachidori for FFXIV

**Read Japanese as you play FFXIV.**<br>
Hover dialogue, subtitles, battle talk, chat, and frozen tooltips for Hachidori popups, audio, immersion stats, and one-click Anki mining with game media.

[![Latest release](https://img.shields.io/github/v/release/KonyAHS/HachidoriFfxiv?label=release&color=24624f)](https://github.com/KonyAHS/HachidoriFfxiv/releases/latest) [![FFXIV Dictionary](https://img.shields.io/badge/FFXIV_Dictionary-Download_ZIP-24624f)](https://github.com/KonyAHS/HachidoriFfxiv/releases/latest/download/FFXIV-Hachidori-JP-EN.zip) [![GitHub stars](https://img.shields.io/github/stars/KonyAHS/HachidoriFfxiv?color=24624f)](https://github.com/KonyAHS/HachidoriFfxiv/stargazers) [![Build status](https://github.com/KonyAHS/HachidoriFfxiv/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/KonyAHS/HachidoriFfxiv/actions/workflows/release.yml) [![Dalamud API 15](https://img.shields.io/badge/Dalamud_API-15-80602c)](#first-setup) [![GPL-3.0 license](https://img.shields.io/github/license/KonyAHS/HachidoriFfxiv?color=80602c)](LICENSE)

[First Setup](#first-setup) · [Features](#features) · [Modes](#built-in-extension--gsm) · [Commands](#commands) · [Docs](#documentation) · [Credits](#credits)

Dalamud plugin for FFXIV on **Windows**.

</div>

<p align="center">
  <a href="docs/screenshots/webview-popup.png">
    <img src="docs/screenshots/webview-popup.png" alt="Hachidori Web popup rendering in game over Japanese dialogue" width="680">
  </a>
</p>

---

## First Setup

### 1. Install the Plugin in Dalamud

1. In FFXIV (**Dalamud Settings → Experimental → Custom Plugin Repositories**), add:

   ```text
   https://github.com/KonyAHS/HachidoriFfxiv/releases/latest/download/pluginmaster.json
   ```

2. Save, open `/xlplugins`, and install **Hachidori for FFXIV**.
3. The **Setup** tab opens automatically on first install (or run `/hachidori`) to pick your **Dictionary Source**, **Mining Style** (**Keep GSM**, **Lightweight**, or **Full Media**), dictionaries, and live **Connection Doctor**.

### 2. Choose Your Dictionary Source

- **Option A — Built-in Dictionaries:**
  1. In `/hachidori` → **Setup**, select **Built-in Dictionaries**, then click **Import Yomitan `.zip`…** to load your Yomitan dictionaries directly, or click **Install JMdict (EN)** and **Install FFXIV Dictionary** (37,729 game terms & 190 portraits — *contains expansion spoilers*).
  2. For one-click Anki mining, install **[AnkiConnect](https://ankiweb.net/shared/info/2055492159)** (Anki add-on code `2055492159`), then open **Mining → Anki Card Mapping** to pick your deck and note type with live field dropdowns, one-click presets (**Lapis**, **JPMN**, **Senren**, **Kaishi**, **Smart Auto-Map**), and **Dry-Run Preview**.
- **Option B — Hachidori Browser Extension:**
  1. Install [Hachidori for Chrome](https://github.com/bee-san/hachidori#install-in-15-seconds) and import your dictionaries.
  2. Install **[AnkiConnect](https://ankiweb.net/shared/info/2055492159)** (`2055492159`) and **[Hachidori Relay](https://github.com/bee-san/hachidori-anki#install)** in Anki, then restart Anki.
  3. Enable **Sharing through Anki** in Hachidori **Settings → Sharing**, select **Hachidori Browser Extension** in **Setup**, and confirm green checks in **Connection Doctor** (enable **Offline Sync** in **Connection** to keep lookups and mining working when the browser is closed).

> Already use **GameSentenceMiner**, **exSTATic**, **Kechimochi**, or **Tidepool**? Choose **Keep GSM** in **Setup** to pair native in-game hover with GSM, click **Import Settings from GSM** in **Mining**, and import/export your reading history under `/hachidori stats` → **Data**.

---

## Features

**Native Hover Lookups & Real Hachidori Popups** — Hover directly over dialogue, subtitles, battle talk, and chat with no text overlay. Renders Hachidori's real browser popup inside FFXIV (with automatic ImGui fallback on Linux/macOS), complete with nested lookups, pitch accent, frequencies, kanji info, and audio. *(Shown above.)*

**Freeze Frame for Tooltips (`Ctrl + Shift + F`)** — Freezes the screen and visible item, skill, and status tooltips so you can hover and mine them without them vanishing.

<a href="docs/screenshots/freeze-frame.webp"><img src="docs/screenshots/freeze-frame.webp" alt="Freeze Frame scanning a frozen FFXIV action tooltip with a live dictionary popup" width="560"></a>

**One-Click Anki Mining with Game Media** — Attaches clean game screenshots, recorded voice lines or local TTS, animated clips, pitch-accent graphs, and AI sentence translations

<a href="docs/screenshots/anki-card.webp"><img src="docs/screenshots/anki-card.webp" alt="Mined Anki card featuring an animated FFXIV cutscene clip, pitch accent, audio, and dictionary definition" width="380"></a>

**Controller & Touchpad Mode (`/hachidori pad`)** — Read and mine from the couch using D-pad word stepping, a left-stick or DualSense/DS4 touchpad virtual cursor, nested popup diving, dialogue backlog (`/hachidori backlog`), cutscene voice repeat (`/hachidori replay`), and remappable bindings.

<a href="docs/screenshots/controller-mode.webp"><img src="docs/screenshots/controller-mode.webp" alt="Controller reading mode navigating FFXIV dialogue with the Hachidori Web popup and bottom button hints" width="560"></a>

**Immersion Stats & Anki Card Feed (`/hachidori stats`)** — Track reading pace, streaks, rank, heatmaps, chattiest NPCs, frequent & most-looked-up words, kanji encountered, and recent Anki cards.

<a href="docs/screenshots/immersion-stats.png"><img src="docs/screenshots/immersion-stats.png" alt="Hachidori Immersion Stats dashboard showing reading rank, KPI cards, daily chart, 30-day heatmap, and Immersion Clock" width="560"></a>

**Also included:**
- **Built-in & offline dictionaries** — Import Yomitan `.zip` archives directly into the plugin or auto-sync from Hachidori, with full offline Anki mining.
- **Performance & cache presets** — Choose **Low / Steam Deck**, **Balanced**, or **High / Instant** in **Performance**, view live memory footprint and cache counts, tune individual cache sizes, or clear caches on demand.
- **Reader, search, katakana character name & NPC pictures** — Optional line-history overlay, manual search, natural player name readings, and in-game NPC portrait cropping (`/hachidori pictures`).
- **FFXIV JP–EN dictionary** — Optional 37,729-entry dictionary for patch 7.58 with 190 portraits, installable directly from **Setup** ([guide & sources](dictionaries/ffxiv/README.md)).

---

## Built-in, Extension & GSM

Choose your dictionary source and mining style in `/hachidori` → **Setup** — irrelevant settings stay hidden for your chosen setup, and you can switch anytime (or reset setup without touching your immersion stats).

| Capability | Built-in Dictionaries | Hachidori Extension | Keep GSM |
| --- | :---: | :---: | :---: |
| Browser extension required | No | Yes *(optional offline sync)* | Optional |
| Direct Yomitan `.zip` import | Yes | Via extension | — |
| Lookups, furigana, pitch, kanji | Yes | Yes | Yes |
| Pronunciation audio | URL / JSON sources | All Hachidori sources | — |
| Anki mining, presets & clean screenshots | Yes *(AnkiConnect)* | Yes | Via GSM |
| Voice recording, TTS & animated clips | Built-in | Built-in | Via GSM |
| AI sentence translation | Built-in | Built-in | Via GSM |
| NPC picture edits | Needs extension | Yes | — |
| Local WebSocket text hook | — | — | Enabled (`:6677`) |

---

## Commands

`/hoshi` also works as an alias.

| Command | Action |
| --- | --- |
| `/hachidori` or `/hachidori config` | Open Settings (Setup, Performance, settings search) |
| `/hachidori <text>` | Look up Japanese text directly |
| `/hachidori freeze` | Freeze screen & tooltips (`Ctrl + Shift + F`) |
| `/hachidori stats` | Open Immersion Stats & Anki Card Feed |
| `/hachidori pad` | Toggle Controller reading mode |
| `/hachidori backlog` | Open dialogue backlog with cached media |
| `/hachidori replay` | Repeat latest cached voice line |
| `/hachidori reader` | Toggle optional line-history Reader overlay |
| `/hachidori search` | Toggle manual Search window |
| `/hachidori pictures` | Manage custom NPC picture dictionary |
| `/hachidori reconnect` | Reconnect to Hachidori Relay |
| `/hachidori logs` | Export redacted diagnostic log |
| `/hachidori help` | List these commands in chat |

---

## Documentation

Want to dig into configuration, platform fallbacks, or building from source?

- **[Feature Guide](docs/FEATURES.md)** — Built-in & extension popups, Freeze Frame, Anki field mapping & presets, Card media, Controller bindings, NPC pictures, and GSM WebSocket setup.
- **[OS & Steam Deck Compatibility](docs/COMPATIBILITY.md)** — Windows, Linux, Steam Deck, and macOS feature matrix and Steam Deck workflow tips.
- **[FFXIV Custom Dictionary](dictionaries/ffxiv/README.md)** — Dictionary contents, installation, sources, and generator code.
- **[Performance & Resource Use](docs/PERFORMANCE.md)** — Premade presets, cache controls, memory/CPU/GPU cost of each feature, and lighter setups for low-end PCs and Steam Deck.
- **[Troubleshooting & Limits](docs/TROUBLESHOOTING.md)** — Common fixes, diagnostics, and privacy scope.
- **[Changelog](CHANGELOG.md)** — Feature log for every release.
- **[Build & Release](docs/DEVELOPMENT.md)** — Building `hdw_native.dll`, parity tests, and packaging.

---

## Credits

This plugin builds on these open-source projects (separate works under their own licenses, unaffiliated with this plugin):

- **Works with:** [Hachidori](https://github.com/bee-san/hachidori) & [Hachidori Relay](https://github.com/bee-san/hachidori-anki) (dictionaries, web frontend, mining), [hoshidicts](https://github.com/Manhhao/hoshidicts) / [bee-san/hoshidicts](https://github.com/bee-san/hoshidicts) (lookup engine & reading tables), and [GameSentenceMiner](https://github.com/bpwhelan/GameSentenceMiner) (optional text feed & translation import).
- **Built on:** [Dalamud](https://github.com/goatcorp/Dalamud) & [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher), [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs), [Microsoft Edge WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/), and [NAudio](https://github.com/naudio/NAudio) / [NVorbis](https://github.com/NVorbis/NVorbis) / [Concentus](https://github.com/lostromb/concentus) for audio.
- **Inspired by:** [Yomitan](https://github.com/yomidevs/yomitan), [JL](https://github.com/rampaa/JL), and [TextToTalk](https://github.com/karashiiro/TextToTalk).

Dictionary data has [its own attribution](dictionaries/ffxiv/README.md#sources-and-attribution) (including [XIVAPI's datamining tables](https://github.com/xivapi/ffxiv-datamining)). Bundled library licenses are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Plugin source: **GPL-3.0-or-later** ([LICENSE](LICENSE)). FFXIV names, text, and images retain their original rights. Not affiliated with Square Enix.
