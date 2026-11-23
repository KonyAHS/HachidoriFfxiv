# OS & Steam Deck Compatibility

FFXIV + Dalamud is primarily developed and tested on **Windows 10 (2004+) / Windows 11**, where every feature works natively. On **Linux** (XIVLauncher.Core / Wine / Proton), **Steam Deck** (SteamOS), **macOS** (XIV on Mac / Wine / CrossOver), and **older Windows 10**, the plugin detects your environment automatically and enables built-in fallbacks where Windows-only APIs are unavailable.

> **Note:** Non-Windows platforms run FFXIV and Dalamud through Wine/Proton compatibility layers and are supported on a best-effort basis. The **Quick Guide** tab in `/hachidori config` shows your detected environment and any active fallbacks.

## Feature Matrix

| Feature | Windows 10 (2004+) / 11 | Linux / Steam Deck (XIVLauncher.Core) | macOS (XIV on Mac) | Automatic fallback |
| --- | --- | --- | --- | --- |
| **Native hover, chat, stats, reader, search, Anki mining** | Full | Full | Full | — |
| **Popup rendering** | Hachidori Web (WebView2) | Built-in ImGui renderer | Built-in ImGui renderer | Skips WebView2/DirectComposition on Wine & Gamescope (or if WebView2 Runtime is missing) and renders bundled Hachidori themes via ImGui |
| **Offline mode (`hdw_native.dll`)** | Full | Full | Full | Statically linked CRT (no VC++ redist required); falls back to live Hachidori if loading fails |
| **Freeze Frame (tooltips)** | Full (`Ctrl + Shift + F`) | Full (`/hachidori freeze` or hotkey; `B`/`○` exits) | Full | Dim readable text cards if GPU viewport capture is unavailable; `KeyState` fallback under Wayland/Gamescope |
| **Voice line recording** | FFXIV-only (process loopback) | System output audio (PipeWire / PulseAudio loopback) | Unavailable by default (CoreAudio has no native loopback) | Process loopback → standard WASAPI system loopback → optional local TTS (VOICEVOX / custom) |
| **Pronunciation audio** | Ogg/Opus/WAV + Media Foundation (MP3/AAC) via WASAPI | Ogg/Opus/WAV built-in; MP3/AAC via host FFmpeg if Wine MF fails | Ogg/Opus/WAV built-in; MP3/AAC via host FFmpeg if Wine MF fails | Media Foundation → piped FFmpeg fallback; WASAPI output → WinMM (`waveOut`) fallback |
| **Screenshots & animations** | JPEG + FFmpeg AVIF/WebP or GIF | JPEG + host FFmpeg AVIF/WebP or GIF | JPEG + host FFmpeg AVIF/WebP or GIF | Auto-detects Windows & host Linux/macOS FFmpeg; falls back to pure-C# GIF encoder |
| **Controller reading mode (`/hachidori pad`)** | Full (+ DualSense/DS4 HID touchpad) | Full (+ Steam Deck trackpads via Steam Input mouse) | Full | Left stick, D-pad, and mouse/trackpad virtual cursor work everywhere; Windows HID touchpad safely disabled if unsupported |

## Steam Deck & Linux Tips

- **Recommended Steam Deck workflow (Gaming Mode):** Open native Linux **Anki** (with Hachidori Relay) and **Chrome** (with Hachidori) once in **Desktop Mode**, launch FFXIV, confirm the link in `/hachidori config`, and enable **Offline Mode** in **Connection**. Once synced, lookups and pronunciation audio keep working in **Gaming Mode** with Chrome closed (open Anki when mining new cards).
- **Localhost sharing:** Wine/Proton shares `127.0.0.1` with the Linux/macOS host (including standard Flatpak Anki/Chrome installs), so keep default host `127.0.0.1` and ports `8771`, `19633`, and `8765`.
- **Cross-hotbar macros:** Bind `/hachidori pad` (Controller mode) and `/hachidori freeze` (Freeze Frame for tooltips) to cross-hotbar macros. In Freeze Frame, press **B / ○** (or click **Exit**) to resume.
- **Gamepad blocking & trackpads:** Enable **"Enable plugin gamepad input"** in Dalamud settings so `/hachidori pad` blocks game inputs while active. On Steam Deck, trackpads automatically glide the virtual cursor when mapped to mouse in Steam Input.
- **FFmpeg for MP3 audio & AVIF/WebP:** Install `ffmpeg` on the host (`/usr/bin/ffmpeg` on Linux/SteamOS, `/opt/homebrew/bin/ffmpeg` on Apple Silicon macOS) or set its path in **Mining → Advanced** if MP3 pronunciation fails under Wine Media Foundation or if you want AVIF/WebP clips instead of GIF.
