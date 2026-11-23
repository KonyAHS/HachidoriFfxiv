# Troubleshooting & Limits

## Common Issues

| Symptom | What to check |
| --- | --- |
| **No host connected** | Keep Anki and Chrome running, verify **Sharing through Anki** in Hachidori **Settings → Sharing**, then run `/hachidori reconnect`. Update Hachidori and Hachidori Relay if outdated. |
| **Popups using ImGui instead of Hachidori Web** | Automatic on Linux, Steam Deck, and macOS (WebView2 + DirectComposition is Windows-only). On Windows, check **Theme** in `/hachidori config`, verify **Microsoft Edge WebView2 Runtime** is installed, and click **Sync Hachidori Web now**. |
| **No popup on hover** | Check your **Scan key** in **Popup**, verify FFXIV's text language is Japanese, and enable **Show native hover targets (debug)** under **Popup → Advanced**. |
| **Tooltip vanishes before hovering** | Press `Ctrl + Shift + F` (or run `/hachidori freeze`) while the tooltip is open to scan it in Freeze Frame. |
| **No chat popup** | Enable **Include native chat text** in **Popup** and **Chat** in **Capture**. Lines with inline icons can offset hover positions slightly. |
| **No pronunciation audio / MP3 fails on Linux/macOS** | Check audio API port `19633` and Hachidori audio sources. On Linux/Steam Deck/macOS, Wine's Media Foundation often lacks MP3/AAC codecs — install host `ffmpeg` (or set its path in **Mining → Advanced**) or prefer Ogg/Opus sources. |
| **Voice recording on Linux / macOS / older Win 10** | Linux/Steam Deck and Windows 10 older than 2004 fall back to system-output loopback (lower other apps' audio for cleaner clips). On macOS, CoreAudio lacks built-in loopback — use **TTS** in **Mining** or a virtual loopback driver. |
| **Offline mode not answering** | Check **Connection** for warnings, then open Chrome with Hachidori once and click **Sync now**. |
| **Controller still moves the game** | Enable **"Enable plugin gamepad input"** in Dalamud settings. |
| **GSM not connecting** | Check the **GSM** tab for a port-in-use warning and pick another port. |
| **Old development build** | Disable any old local dev copy before installing the release build. `/hoshi` stays available as an alias and `Hoshidori.json` migrates automatically when possible. |

## Exporting Diagnostics

Run `/hachidori logs` in-game to export a redacted diagnostic log locally. Review the file before attaching it to a [GitHub issue](https://github.com/KonyAHS/HachidoriFfxiv/issues).

## Scope & Privacy

- Reads native FFXIV UI text directly rather than using video OCR.
- Lookups send hovered text to your configured local Hachidori host (or stay completely local in offline mode).
- Optional sentence translation sends the mined sentence and recent dialogue context only to the AI provider or local CLI you configure.
- Anki mining and NPC picture imports write to your Anki and Hachidori setup only when you request them.
