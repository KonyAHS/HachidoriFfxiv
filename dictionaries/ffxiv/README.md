# FFXIV Hachidori dictionary

A comprehensive Japanese → English dictionary ZIP for Hachidori and Yomitan. It
supplements a general Japanese dictionary with FFXIV's localized names, game
systems, character portraits, and Japanese player/Party Finder lingo.

## Download

- **Direct download (latest release):** [`FFXIV-Hachidori-JP-EN.zip`](https://github.com/KonyAHS/HachidoriFfxiv/releases/latest/download/FFXIV-Hachidori-JP-EN.zip)
- **Repository file:** [`FFXIV-Hachidori-JP-EN.zip`](FFXIV-Hachidori-JP-EN.zip)
- **Generator & data source code:** [`build_dictionary.py`](build_dictionary.py), [`validate_dictionary.py`](validate_dictionary.py), and the TSV/image datasets in this folder.

> **Spoiler notice:** Contains character names, titles, and portraits across all FFXIV expansions.

Dictionary revision `2026.11.24.1` (game snapshot **7.58**) contains
**37,840 entries** across 31 categories:

- **10,026 characters / NPCs** (including official NPC titles, full names,
  dotless spellings, and nicknames such as `ラマチ`, `ラハ`, `ゼノス`, `エメト`,
  `ヤシュトラ`)
- **192 character introductions** drawn from each character's first encounters,
  also shown on matching name variants. The descriptions avoid later plot
  developments. [Character descriptions](character_descriptions.tsv)
- **11,974 enemies / bosses**, **4,735 locations**, **1,380 FATEs**,
  **759 duties** (including Duty Roulettes & short names), **587 minions**,
  **358 mounts**, **43 jobs/classes**, **12 Guardian Deities (The Twelve)**
- **Game systems**: **1,027 player actions** (tagged by job), **481 job traits**,
  **3,321 status effects** (with descriptions), **1,056 official Auto-Translate**
  phrases (`Completion`), **298 emotes**, **95 weather types**, and **758
  Orchestrion rolls**
- **Player lingo & community slang**: **396 AkhMorning JP–EN entries**,
  **60 Party Finder terms**, **156 community slang / mechanics terms**,
  **89 acronyms**, **34 job abbreviations** (including single-character PF job
  codes), **32 JP Worlds**, and **28 Materia** entries. Includes raid calls and
  localized mechanic names from Eden's Promise and Asphodelos (Savage), plus
  treasure map grades G1–G14.
- **190 embedded images** (178 character portraits + 12 location artworks,
  optimized to ≤360px) illustrating **250 entries** across all expansions
  (Dawntrail 44, Endwalker 45, Shadowbringers 25, Earlier expansions 64).
- **Single-card Yomitan v3 `structured-content` formatting**: color-coded
  category & subtag badges, bold English headwords, clean notes/descriptions,
  and properly attributed inline portraits (no bogus `1. 2. 3.` numbering).

## Import

1. Download [`FFXIV-Hachidori-JP-EN.zip`](https://github.com/KonyAHS/HachidoriFfxiv/releases/latest/download/FFXIV-Hachidori-JP-EN.zip)
   (you can also open this link from inside FFXIV via `/hachidori config` → **Quick Guide** or **Connection**).
2. In Hachidori (or Yomitan), open **Settings → Dictionaries** and import
   `FFXIV-Hachidori-JP-EN.zip`.
3. Enable **FFXIV Hachidori JP–EN** and place it above your general dictionaries
   if you prefer FFXIV meanings first. Keep your general dictionaries enabled.
4. Try `ヤ・シュトラ`, `ラマチ`, `ゼノス`, `クリ目`, `取り抜け`, `頭割り`,
   `シャキる`, `オスッテ`, `被ダメージ上昇`, `リムサ・ロミンサ`, and `星芒祭`.
5. If you already have an older version of **FFXIV Hachidori JP–EN** installed,
   remove it before importing the new ZIP to avoid duplicate definitions.

## Sources and attribution

- [XIVAPI FFXIV datamining tables](https://github.com/xivapi/ffxiv-datamining)
  (pinned commit `a9f3a282a0e6070fdc0469b6d4e3e7ea17e16edf`, patch **7.58**).
- [AkhMorning FFXIV JP to EN Dictionary](https://www.akhmorning.com/resources/ffxiv-en-jp-dictionary/)
  for JP–EN community lingo, race slang, materia, worlds, and acronyms.
- [XIV JP Raids Terminology Guide](https://xivjpraids.com/terminology/) and
  curated Japanese Party Finder / raiding glossaries (`lingo.tsv`).
- [Gamer Escape FFXIV Wiki](https://ffxiv.gamerescape.com/), official Square Enix
  Dawntrail pages, Icy Veins, Huiji Wiki, FF14 Online Wiki, BTVA, and Mudae for
  embedded character/location illustrations (every image retains its source link
  in `image_manifest.json` and inline attribution).

FFXIV text, game imagery, and artwork © SQUARE ENIX.

## Rebuild & Validate

```powershell
python dictionaries/ffxiv/build_dictionary.py --fetch-images
python -m pip install -r dictionaries/ffxiv/requirements.txt
python dictionaries/ffxiv/validate_dictionary.py
```
