# Feature Guide

Detailed setup and behavior for Hachidori for FFXIV. For installation and a visual overview, see the [README](../README.md).

---

## Popups & Themes (WebView2 + ImGui Fallback)

By default on Windows, popups render **Hachidori's real browser UI** inside FFXIV via Microsoft Edge WebView2 over a click-through DirectComposition overlay (clicking or scrolling never steals OS focus from FFXIV):

- **Full upstream parity:** Every theme in Hachidori **Settings → Design** (Default and palettes, Nazeka, Plain, JL, Bee's Theme, Crystal with Grey/Beige/Retro Blue, and future upstream themes), opacity, custom popup CSS, pitch accent graphs, and dictionary images/SVGs render identically to Chrome.
- **Automatic bundle sync:** Keeps a cached copy of Hachidori's web frontend matched to your host version (checking local Chrome/Edge/Brave/Vivaldi extensions first, otherwise downloading from `bee-san/hachidori` Releases), with automatic rollback if a bundle fails to boot.
- **In-game overrides:** Open `/hachidori config` → **Theme** to override the theme or variant for FFXIV only, add FFXIV-only CSS, point to a custom extension folder, or click **Sync Hachidori Web now**.
- **Built-in ImGui fallback:** Used automatically on Linux/Steam Deck/macOS, if WebView2 Runtime is missing, while syncing, or when **Hachidori Web** is toggled off. Interprets bundled theme stylesheets for colors, vertical gradients, borders, shadows, Crystal's frame/crest, and headword styling (layout rules like flex/grid, CSS images, and theme JS are ignored). Maintainers can refresh fallback stylesheets with:

```powershell
python -I scripts/sync_hachidori_themes.py <extension folder>
```

---

## Freeze Frame for Tooltips

FFXIV hover tooltips (items, actions, traits, statuses) vanish as soon as you move the mouse away. **Freeze Frame** captures them for scanning:

1. Hover any item, skill, or UI element so its Japanese tooltip is visible.
2. Press **`Ctrl + Shift + F`** or run `/hachidori freeze`. (Change the hotkey in **Popup → Freeze Frame hotkey**: `Ctrl + Shift + F`, `Alt + F`, `Ctrl + Space`, `Pause / Break`, `F9`, `Shift + Middle Click`, or command-only.)
3. The plugin captures the screen and all visible native Japanese text blocks, prioritizing tooltips (`ItemDetail`, `ActionDetail`, `Tooltip`, `*Detail`, `*Tooltip`).
4. Hover any word on the frozen tooltip to look up, play audio, or mine to Anki. Use the top bar to **Show/Hide Boxes**, **Send to Search**, or **Exit**.
5. Press **`Esc`**, **`B / ○`** (gamepad), right-click outside a popup, or press the hotkey again to resume the game.

---

## Immersion Stats & Anki Card Feed

Run `/hachidori stats` to view progress across **Session**, **Today**, **7 Days**, **30 Days**, and **All Time**:

- **Rank, streaks & KPIs:** FFXIV immersion rank with lifetime character XP bar, current/best daily streaks, total Japanese characters (with kanji % and reading equivalents), lines & pace (chars/hr and active time), Anki cards mined, and unique kanji encountered (% of Joyo kanji).
- **Overview:** Daily characters bar chart (marking mining days), 30-day heatmap, 24-hour Immersion Clock with reading persona, and source breakdown (Dialogue, Battle Talk, Chat).
- **NPC Dialogue:** Chattiest NPCs leaderboard (click any NPC to open their dictionary entry & portrait) plus a filterable, hoverable Recent NPC Dialogue Feed.
- **Top Words & Kanji:** **Most Parsed Non-Trivial Words** (filters particles, pronouns, auxiliaries, and single kana), **Most Looked Up / Nemesis Words**, and a clickable **Kanji Encountered** grid.
- **Anki Cards:** Browse cards mined in FFXIV (`IMG`, `AUDIO`, `ANIM`, `TL` badges, pitch/frequency, hoverable sentence, **Open in Anki**) or switch to the live **Latest from AnkiConnect** feed.
- **Achievements & Export:** Unlock reading/mining milestones or export full stats history to JSON.

---

## Offline Mode

By default, lookups go to Hachidori in Chrome. Offline mode stores a synced copy of your dictionaries and settings in the plugin's config folder so lookups, audio, and mining keep working with Chrome closed.

### Setup

1. With Hachidori linked, open `/hachidori config` → **Connection** (or **Quick Guide**) and click **Set up offline mode**. Dictionaries download once (~same disk space as Hachidori).
2. Live Hachidori is always preferred while linked. On each reconnect, the plugin compares dictionary revisions, downloads only changes, and removes deleted dictionaries. Settings (order, theme, audio sources) sync automatically.
3. The **Connection** tab shows the active backend, disk usage, last sync time, and **Turn off and delete offline data**.

### Offline behavior & caveats

- **Engine parity:** Uses Hachidori's compiled `hoshidicts` engine (`hdw_native.dll`) and settings so results match Chrome.
- **Anki mining:** Renders your Hachidori Anki Template locally and adds the note via AnkiConnect (with pronunciation, screenshot, and dictionary images).
  - Duplicate checking uses Anki's first-field check rather than Hachidori's word index.
  - Templates that overwrite existing notes, use Smaller Anki cards, or use pitch graph markers require Chrome.
- **Audio:** Uses Audio URL and Yomitan JSON sources directly (browser speech requires Chrome).
- **Other limits:** Lookup counts sync on the next Chrome link; NPC picture edits require Chrome. If Hachidori updates to a newer dictionary format than the bundled engine, incompatible dictionaries stay live-only until the plugin updates.

---

## Card Media: Screenshot, Voice, TTS, Animation & Translation

Configure card media under `/hachidori config` → **Mining**:

- **Screenshot:** Fills `{screenshot}` in your Hachidori Anki Template. Plugin windows (popups, reader, stats) are excluded.
- **Voice line:** Records FFXIV process audio on Windows 10 (2004+) and falls back to system-output loopback on Linux / Steam Deck and older Windows 10. Trims to detected speech with 1 s padding before and after. Lower in-game BGM volume for cleaner clips.
- **TTS for unvoiced lines:** When no game voice is detected (or on macOS without loopback), speaks the sentence via a local VOICEVOX-compatible API (VOICEVOX, AivisSpeech, etc.) or any custom audio URL.
- **Animation:** Captures a few seconds from when the line appeared (default 720 px, 12 fps, 3 s) as animated **AVIF**, **WebP**, or **GIF**. AVIF and WebP use FFmpeg (auto-detected on `PATH`, in GSM's folder, or standard Linux/macOS host paths under Wine); falls back to built-in GIF without FFmpeg.
- **Sentence translation:** Uses GSM-style prompts with surrounding dialogue context via **Gemini**, **Groq**, **OpenAI-compatible APIs**, **Ollama**, **LM Studio**, **Z.ai**, **DeepL**, or your local **Codex** / **Claude Code** CLI login. **Import from GSM** copies your provider, models, keys, prompts, and target field in one click.

### AnkiConnect field mapping

Voice lines, TTS audio, animations, and translations are attached through [AnkiConnect](https://ankiweb.net/shared/info/2055492159) right after Hachidori creates the note. Default target fields match names like *Sentence Audio*, *GIF* (without a GIF field, the animation replaces the screenshot), and *SentenceTranslation*. Customize field names under **Mining → Advanced settings**.

---

## Controller & Virtual Cursor Mode

Read dialogue and UI windows with a gamepad. While active, the plugin captures controller input so FFXIV ignores it, and returns control on exit.

### Entering & requirements

- **Recommended:** Bind `/hachidori pad` to a cross-hotbar macro so FFXIV doesn't react to the activation press.
- **Hold trigger:** Hold **Back / Select** (customizable under `/hachidori config` → **Controller**).
- **Dalamud setting:** Enable **"Enable plugin gamepad input"** in Dalamud settings so controller mode can block game input (the hint bar warns if off).

### Default controls

All action bindings and PlayStation/Xbox labels are customizable under **Controller** / **Controller → Advanced**.

| Input (Xbox / PlayStation) | Action |
| --- | --- |
| **Left Stick** | Glide virtual cursor across visible dialogue and UI windows |
| **Touchpad** *(optional)* | Direct virtual cursor steering (DualSense / DualShock 4) |
| **D-pad** | Step word by word (◀▶) and line by line (▲▼) |
| **Right Stick** | Scroll popup |
| **A / ✕** | Add entry to Anki |
| **X / □** | Play pronunciation audio |
| **Y / △** | Toggle word / character mode |
| **LT / L2** · **RT / R2** | Previous / next dictionary entry |
| **RB / R1** | Dive into popup for nested lookups |
| **LB / L1** | Cycle open UI windows (or back out of a nested popup) |
| **B / ○** | Back out of nested popup, or leave mode |
| **Back / Start** | Leave mode |

Exits automatically when dialogue closes (configurable).

---

## NPC Pictures & Player Name

- **NPC pictures (`/hachidori pictures`):** Click the camera button on a popup to crop an NPC screenshot into one shared **FFXIV NPC pictures** dictionary on your Hachidori host. `/hachidori pictures` lists, removes, and merges older single-picture dictionaries. Source images live in the plugin's `npc-pictures` config folder and rebuild the dictionary on each edit (requires Hachidori linked dictionary import support).
- **Katakana player name:** Configure your character's katakana reading in **Capture** so sentences, TTS, and mined cards read naturally.

---

## GSM WebSocket Text Feed (Opt-in)

If you still want [GameSentenceMiner](https://github.com/bpwhelan/GameSentenceMiner) to receive live game text:

1. Open `/hachidori config`, enable **Advanced settings** (if **GSM** is hidden), open **GSM**, and enable **Publish text for GSM**.
2. Choose a preset (toggling checkboxes switches to **Custom**; optionally prepend speaker names):

   | Preset | Sends |
   | --- | --- |
   | Dialogue only | Dialogue, cutscene subtitles, battle talk |
   | Dialogue and popups *(recommended)* | Above + yes/no prompts and choice menus |
   | Everything, including chat | Above + selected chat channels (including other players) |

3. Keep port `6677` if free (GSM already listens on `localhost:6677`, `2333`, and `9001`).
4. Confirm a connected client in the **GSM** tab, then click **Send test line**.

Lines are sent locally as `{"sentence":"…"}`. Website origins are refused, while local programs can connect.
