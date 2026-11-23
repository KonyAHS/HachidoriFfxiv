// Hachidori for FFXIV WebView2 overlay capabilities.
// Served in place of overlay-mode.js so Hachidori runs in overlay mode while
// still requesting clean game screenshots through FFXIV's MediaMiner.
// SPDX-License-Identifier: GPL-3.0-or-later

export const OVERLAY_MODE = true;

export const HOST_CAPABILITIES = Object.freeze({
  browserShortcuts: false,
  customLinks: true,
  customJavaScript: true,
  pageWorldUserScripts: true,
  linkButtons: true,
  externalLinkHost: true,
  localFileAccessPrompt: false,
  lowMemoryMode: true,
});

export const MINING_CAPABILITIES = Object.freeze({
  screenshot: true,
  browserSpeech: false,
});
