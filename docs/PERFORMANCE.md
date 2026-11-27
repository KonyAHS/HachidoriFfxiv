# Performance & Resource Use

What Hachidori for FFXIV costs in memory, CPU, GPU, disk and network, and which settings change it. Use it to decide whether the plugin fits your machine, and which features to turn off if it doesn't.

Figures come from the plugin's default settings and one Windows 11 session with WebView2 popups and offline mode on. Measured values are marked *measured*. The others are worked out from buffer sizes in the code. Your numbers will differ with resolution, settings and dictionaries.

---

## At a Glance

| Part | Memory | CPU / GPU | Runs when | How to reduce it |
| --- | --- | --- | --- | --- |
| Text capture & hover lookups | A few MB | Tiny per frame; text is re-read only when it changes | Always | — |
| Popup window (WebView2, Windows) | ~300 MB private, in separate processes (*measured*) | 0% when idle (*measured*) | From plugin load | Use the built-in renderer instead |
| Voice line recording | ~23 MB (2-minute buffer) | One light background thread | **Voice line** on | Shorter buffer, or turn off |
| Screenshots | One full-resolution copy of the game image in VRAM | One GPU copy per frame | **Screenshot** or **Animation** on | Turn off both |
| Animated clips | ~56 MB of frames | Screen sampling 12 times a second; an encode per dialogue line | **Animation** on | Smaller, shorter, lower FPS, or off |
| Offline mode | Dictionary files mapped from disk, not copied into RAM | Only on lookups | Chrome is not linked | — |
| Immersion stats | Small | Saves every 15 s, off the game thread | Always | — |

With everything on at default settings, expect an estimated **100–150 MB more in the game process** plus **~300 MB in WebView2's own processes**. On Linux, Steam Deck and macOS the WebView2 part does not exist.

---

## Frame Time

The plugin runs some work on every game frame. That work is kept small so it does not cost FPS:

- **Dialogue capture** reads the dialogue, subtitle and battle talk windows each frame. It compares the raw bytes with the previous frame and only parses text when they differ, so a line on screen costs one byte comparison per frame.
- **Hover lookups** only look at the game window directly under the mouse, not every window on screen. Controller mode and Freeze Frame scan more, but only while they are active.
- **Slow work stays off the game thread.** Lookups, dictionary loading, audio cutting, clip encoding, AnkiConnect calls and stats saving all run in the background. A slow Chrome, Anki or disk delays the popup or card, not your frame.

---

## Memory

### Popup window (WebView2)

On Windows, popups are drawn by a real Microsoft Edge WebView2 instance (the same engine as Edge), so Hachidori's themes match Chrome exactly. That is the largest single cost:

- Runs as about six `msedgewebview2.exe` child processes of FFXIV, not inside the game's own memory.
- *Measured:* ~300 MB private memory (~470 MB working set, which double-counts shared pages). Used 0 ms of CPU over 10 idle seconds. The overlay is hidden when no popup is open.
- Starts with the plugin and stays running. Turning it off hides it straight away, but the processes only exit when the plugin reloads or the game closes.

**Built-in renderer instead:** In **Popup**, turn on **Show advanced** and turn off **Render popups with Hachidori Web (WebView2)**, then reload the plugin. Popups are then drawn with Dalamud's own UI, with no extra processes. You lose exact theme layout, custom CSS and theme scripts (see the [Feature Guide](FEATURES.md#popups--themes-webview2--imgui-fallback)). This renderer is always used on Linux, Steam Deck and macOS.

### Voice line recording

Records the last stretch of game audio so a voice line can be cut out when you mine a card.

- Stored as mono 48 kHz audio at **~11.5 MB per minute** of buffer.
- Default buffer is 2 minutes, **~23 MB**. **Voice ring buffer (s)** under **Mining → Show advanced** sets it from 30 s to 5 min (**~6–58 MB**).
- The buffer has a fixed size and never grows. A shorter buffer means you cannot mine lines that played longer ago.

### Animated clips

The plugin keeps recent screen frames so each dialogue line can become a short animated clip.

- Each frame is stored uncompressed: width × height × 4 bytes. At the default 720 px wide on a 16:9 screen, that is **~1.2 MB per frame**.
- It keeps the clip length plus one second. Defaults (3 s at 12 FPS) mean ~48 frames, **~56 MB**.
- The highest settings (1280 px, 24 FPS, 8 s) mean ~216 frames of ~3.7 MB, **about 800 MB**. Raise **Animation width**, **FPS** and **length** with care.
- Finished clips are compressed straight away. Only the last 6 lines' clips are kept, as small files.

### Offline mode

Offline dictionaries are memory-mapped: Windows reads pages from disk when a lookup touches them, and can drop them under memory pressure. A large dictionary collection does not become an equal amount of RAM. Dictionaries load on the first offline lookup, not at startup, and stay unloaded while Chrome is linked.

### Everything else

Lookup results are cached (up to 512 lookups), as are word splits and pronunciation audio. Line history keeps 100 lines by default (**History size** in **Reader**). Stats keep capped lists (500 mined cards, 200 recent dialogue lines) and grow slowly with days played. *Measured:* 80 KB on disk. Together these stay in the low tens of MB.

---

## CPU

- **Idle:** close to zero. The voice recorder thread wakes for each small packet of game audio. The stats saver wakes every 15 seconds and only writes if something changed.
- **Each dialogue line:** split into words in the background for stats. This is the same work as a lookup.
- **Animated clips** default to **on-demand encoding** (`EagerEncodeClips = false`): recent line frames stay in RAM and FFmpeg only runs when you actually click `+` to mine, eliminating continuous CPU spikes during cutscenes. Switch **Clip encoding** to **Pre-encode every line** under `/hachidori` → **Performance** if you prefer instant mining with background encodes.
- **Performance Presets & Cache Limits:** Open `/hachidori` → **Performance** to pick a premade preset (**Low / Steam Deck**, **Balanced**, or **High / Instant**), view live cache usage, clear caches immediately, or tune lookup, segment, pronunciation audio, line screenshot, voice replay, and clip cache caps individually.
- **Sentence translation** only runs when you mine a card. Local models (Ollama, LM Studio) use your own CPU/GPU for that moment.

---

## GPU

- **Screenshot or Animation on:** the plugin keeps a copy of the game image from before plugin windows are drawn, so cards show no popups. The copy is refreshed each frame. It costs one full-resolution texture in VRAM (**~15 MB at 1440p, ~33 MB at 4K**) and a texture copy per frame. Both are freed when Screenshot and Animation are both off.
- **Animation on:** the image is also scaled down and read back to system memory 12 times a second (at the default FPS).
- **WebView2 popups** use their own GPU process. It only draws while a popup is visible.

---

## Disk

| Item | Size | Notes |
| --- | --- | --- |
| Plugin + offline engine | ~6 MB | Plugin DLL plus `hdw_native.dll` |
| Offline dictionaries | Same as in Hachidori | *Measured:* 18 dictionaries ≈ 620 MB. Only changed dictionaries are downloaded on each sync. |
| WebView2 data | ~140 MB (*measured*) | Browser cache and profile in the plugin config folder |
| Hachidori Web bundle | ~5 MB | Cached copy of Hachidori's popup UI |
| Stats | Tens to hundreds of KB | Saved every 15 s when changed. Optional CSV/SQLite exports append as you read. |

All of it lives under `%AppData%\XIVLauncher\pluginConfigs\HachidoriFfxiv`. Screenshots, audio and clips are not saved there. They go straight to Anki through AnkiConnect. FFmpeg uses one short-lived temporary file per encode.

---

## Network

By default all traffic stays on your machine (`127.0.0.1`): Hachidori Relay in Anki, AnkiConnect, and a local TTS server if you set one up. Traffic leaves your machine only when:

- **Hachidori Web bundle sync** downloads Hachidori's popup UI from GitHub, if no matching browser extension is installed. Turn off **Auto-sync Hachidori Web when Hachidori updates** (Popup, Show advanced) to stop this.
- **Pronunciation audio** sources in your Hachidori settings point to online servers.
- **Sentence translation** uses a cloud provider (Gemini, Groq, OpenAI-compatible, Z.ai, DeepL). Only the sentence and nearby dialogue are sent, and only when you mine.
- **The GSM text feed** is off by default. When on, it listens on a local port (`6677`).

---

## Lighter Setups

**Low RAM (8–16 GB):** Use the built-in popup renderer (saves ~300 MB), keep animation at default size or turn it off, and shorten the voice buffer to 30–60 s.

**Weak CPU / Steam Deck:** Turn off **Animation**, or set it to GIF at 8 FPS and 480 px. Keep the voice buffer short. Offline mode is cheap to run and avoids keeping Chrome open, which usually saves more than it costs.

**Lookups only, no mining:** Turn off **Screenshot**, **Voice line** and **Animation** in **Mining**. What remains is text capture, the popup and stats, which cost very little on their own.
