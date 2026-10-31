<img src="assets/icon.svg" width="64" height="64" alt="">

# Hachidori for FFXIV

Hover Japanese dialogue, subtitles, battle talk and chat in FFXIV for dictionary
popups, pronunciation, nested lookups and Anki mining. An optional reader and
search window are included. Windows; Dalamud API 15. This is a custom-repository
plugin, with no official Dalamud listing.

## First setup

1. Install [Hachidori for Chrome](https://github.com/bee-san/hachidori#install-in-15-seconds)
   and import your Japanese dictionaries. Use a recent version with Sharing support.
2. In Hachidori, open **Settings → Sharing → Download the Anki add-on**.
   Install [Hachidori Relay](https://github.com/bee-san/hachidori-anki#install) in
   Anki via **Tools → Add-ons → Install from file**, then restart Anki.
3. Keep Anki and Chrome open. In Hachidori **Settings → Sharing**, confirm
   **Sharing through Anki** is active and your dictionaries are available.
4. Launch FFXIV with [XIVLauncher/Dalamud](https://goatcorp.github.io/).
   In **Dalamud Settings → Experimental → Custom Plugin Repositories**, add:

   ```text
   https://github.com/KonyAHS/HachidoriFfxiv/releases/latest/download/pluginmaster.json
   ```

5. Save, open `/xlplugins`, search **Hachidori for FFXIV**, and install it.
6. Run `/hachidori config` and confirm a linked host and dictionary count.
   Local defaults are host `127.0.0.1`, relay port `8771`, and audio API port `19633`.
   Keep them unchanged for a same-computer setup.

The plugin uses your Hachidori host's dictionaries and templates; it is not a
standalone dictionary and a generic Yomitan API server is insufficient.
No compiling or manual DLL installation is needed.

## Use

- Hover Japanese game text to look up words. Move into a definition and hover
  another Japanese word for a nested lookup. Set **Scan key** in the Popup tab
  if you prefer holding Shift, Ctrl or Alt instead of plain hover.
- Run `/hachidori 冒険者` to try a lookup, `/hachidori search` for search, or
  `/hachidori reader` for the optional reader/history overlay.
- Use the pronunciation button for dictionary audio. For Anki, configure a
  template in Hachidori, select it in the plugin settings, then use **+** on an entry.
- The camera button lets you crop an NPC screenshot into a dictionary on your
  host. This requires host support for linked dictionary imports.
- Optional: import [the FFXIV Japanese–English dictionary](dictionaries/ffxiv/README.md)
  into Hachidori for game names and vocabulary. **Contains expansion spoilers.**

## Troubleshooting and limits

- **No host:** keep Anki/Chrome running, check Hachidori Sharing, then use
  `/hachidori reconnect`. If needed, update Hachidori and the Relay add-on.
- **No popup:** check your scan key and enable **Show native hover targets (debug)**.
  Set the game's text language to Japanese. Unusual layouts/fonts may affect hover.
- **No audio:** check port `19633` and pronunciation sources in Hachidori.
- **Old development build:** disable it before installing this version; `/hoshi`
  remains an alias, and old `Hoshidori.json` settings are migrated when possible.
- `/hachidori logs` exports diagnostics locally. Review exports before attaching
  them to an [issue](https://github.com/KonyAHS/HachidoriFfxiv/issues).

This is an early release. Native hover, cutscene layouts, GPU screenshots and
real Anki saves need in-game validation. Video OCR, game voice-line recording,
Anki screenshot/video attachments and arbitrary dictionary CSS are unsupported.
Lookups send the selected text to your configured Hachidori host; mining and NPC
image imports write to your Anki/Hachidori setup when requested.

[Build and release](docs/DEVELOPMENT.md) · [License](LICENSE) ·
[Third-party notices](THIRD-PARTY-NOTICES.txt)

Plugin source: GPL-3.0-or-later. FFXIV names, text and images retain their original
rights; see the dictionary's attribution. Not affiliated with Square Enix.
