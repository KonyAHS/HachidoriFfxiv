# FFXIV Hachidori dictionary

A Japanese → English dictionary ZIP for Hachidori and Yomitan. It supplements a
general Japanese dictionary with FFXIV's localized names and vocabulary.

The current release contains **29,870 entries**: 9,972 characters/NPCs, 4,727
locations, 43 jobs/classes, 741 duties, 1,380 FATEs, 11,972 enemies/bosses, and
other vocabulary. **131 images illustrate 140 entries**, including name variants:
119 character images and 12 location images. All 54,212 game-table row references
pass Japanese/English alignment checks.

Character image coverage now focuses on **Dawntrail (42)** and **Endwalker (42)**,
with **Shadowbringers (17)** and **earlier expansion characters (18)** also included.
These are focus groups, not claims about a character's first appearance. The
grouped [character coverage list](character_coverage.tsv) identifies every
target, its Japanese names, and whether an image was available.

Recent additions include Calyx (カリュクス), Halmarut (ハルマルト), Metem,
Golbez, Durante, Azdaja, Fandaniel, Nymeia, the four elemental archfiends,
Vamp Fatale, the Tyrant, Koana, Zoraal Ja, Gulool Ja Ja, Gulool Ja, Bakool Ja Ja,
Sphene, Cahciua, Namikka, Otis, Shale, Ketenramm, Yaana, Neyuni, Eutrope,
the first four Arcadion fighters, Sugar Riot, Howling Blade, Fourchenault,
Ameliance, Jullus, Nidhana, Matsya, Zero, Vrtra, Varshahn, Venat, Hermes,
Meteion, Hythlodaeus, Athena, Themis, and Erichthonios. Existing names that
already appeared as text now receive images. Machine-designation names such as
`N-7000` are also retained from the Japanese game table.

## Import

1. In Hachidori, open Settings → Dictionaries and import `FFXIV-Hachidori-JP-EN.zip`.
2. Enable **FFXIV Hachidori JP–EN**, and put it above your general dictionaries if
   you prefer FFXIV meanings first. Keep your general dictionaries enabled.
3. Try `ヤ・シュトラ`, `アルフィノ`, `リムサ・ロミンサ`, `竜騎士`, and `星芒祭`.
4. Hachidori for FFXIV uses the updated host dictionary through the relay. Reconnect if
   the dictionary list has not refreshed.

Import the ZIP directly; do not unzip it. Creating the archive does not import
it or alter your Hachidori settings.

If you already imported **FFXIV Hoshidori JP–EN**, remove that old dictionary in
Hachidori before importing this renamed ZIP to avoid duplicate vocabulary.

## Contents and limits

- Characters / NPCs, enemies / bosses, locations, jobs / classes (including
  crafting and gathering), duties, FATEs, races, clans, Grand Companies, allied
  societies, mounts, and minions from aligned Japanese and English game tables.
- A curated supplement adds seasonal events, expansion names, game vocabulary,
  community aliases, and readings for jobs. Curated definitions are original
  short explanations; `curated.tsv` records their sources.
- Selected recognizable characters and major locations include embedded images.
  Images work offline after import. Each illustrated definition links its image
  source page and credits SQUARE ENIX; `image_manifest.json` records exact URLs,
  dimensions, and hashes. Images are not available for every entry.
- Hachidori/Yomitan display the images. **Hachidori for FFXIV 0.1.2 adds dictionary-image
  rendering through the relay's media endpoint.** Image bytes have been checked
  against the live host; actual in-game GPU display still needs validation.
- **Names and images from all expansions can contain spoilers.** Definitions do
  not add plot biographies. Character outfits reflect the source wiki's image.
- The source snapshot is **7.57**, commit
  `76d22160d687b0e0318071a098ab9eb9d531605f` (2026-10-13). This is not a claim
  that every record is obtainable in the current game: the tables also retain
  old events and unused content. Obvious test/dummy strings and empty or
  unaligned records are excluded, but generic NPC labels remain.
- Matching uses identical row IDs across languages, not machine translation.
  Repeated NPC instances with the same Japanese/English name are consolidated.
  **1,266 duplicate battle entries are merged** into the matching character,
  minion, or mount entry when both the Japanese and English names match. For
  example, アルフィノ / Alphinaud is one character entry instead of separate
  NPC and enemy definitions. Every original game-table row is retained in the
  vocabulary provenance. Different localized meanings, names, and forms remain
  separate entries (for example, Alphinaud's avatar is not the same headword).
- Kana-only headwords receive a mechanically converted hiragana reading.
  Kanji readings are left empty unless curated; no guessed pronunciation or
  pitch-accent data is supplied. Long names may require increasing scan length.
- No item catalogue, quest-title dump, or full combat-action list is included.
  These would add many entries outside the requested names and events.

`vocabulary.tsv` is the complete searchable word list with provenance.
`source_manifest.json` reports category counts and source CSV hashes.

## Sources and attribution

- [XIVAPI's FFXIV datamining tables](https://github.com/xivapi/ffxiv-datamining),
  extracted by that project's contributors from the game client. The public
  [XIVAPI documentation](https://v2.xivapi.com/docs/guides/sheets/) also describes
  accessing these localized sheets. Its live endpoint returned HTTP 403 during
  this build, so the builder uses pinned GitHub CSVs instead.
- [Official Japanese job guide](https://jp.finalfantasyxiv.com/jobguide/battle/)
  and [Lodestone seasonal events](https://jp.finalfantasyxiv.com/lodestone/),
  with specific supplement sources in `curated.tsv`.
- [Gamer Escape FFXIV wiki](https://ffxiv.gamerescape.com/) for selected character
  portraits and location artwork. File-page links are recorded per image.
- [Official Dawntrail character artwork](https://na.finalfantasyxiv.com/dawntrail/story/)
  and [official blog artwork](https://na.finalfantasyxiv.com/blog/003754.html)
  supply additional recent characters where wiki portraits are missing.
- [Icy Veins FFXIV raid guides](https://www.icy-veins.com/ffxiv/),
  [Huiji FFXIV wiki](https://ff14.huijiwiki.com/), and
  [FF14 Online Wiki](https://ff14wiki.info/) supply selected additional game
  renders and screenshots. Every image retains its specific source attribution.
- [Behind The Voice Actors' Endwalker cast database](https://www.behindthevoiceactors.com/video-games/Final-Fantasy-XIV-Endwalker/)
  provides game-character headshots for ten additional characters. These are
  character pictures, not photographs of their voice actors. The
  [Mudae character database](https://mudae.net/character/8408157/Metem) supplies
  Metem's game portrait. Portrait resolution varies by source.

FFXIV text, game imagery, and artwork © SQUARE ENIX. Names and assets retain
their original rights. This project does not claim an open license to them or
official endorsement. Wiki prose and plot summaries are not copied.

## Rebuild

Requires Python 3.11+. Fetching/verifying images additionally requires Pillow.

```powershell
python dictionaries/ffxiv/build_dictionary.py --fetch-images
```

Downloaded tables and pages are cached in ignored `.cache/`; images and both
manifests are kept beside the archive. To rebuild with the existing image
manifest and embedded image files, omit `--fetch-images`. Source URLs are pinned
to the commit above so an ordinary rebuild does not silently update game data.

Validate the result against Yomitan's official dictionary schemas, decode every
embedded image, and check coverage and source alignment:

```powershell
python -m pip install -r dictionaries/ffxiv/requirements.txt
python dictionaries/ffxiv/validate_dictionary.py
```

These checks validate the archive itself. Import and live lookup in Hachidori,
including image display, still need to be checked on the actual host.
