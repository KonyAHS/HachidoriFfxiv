// Hachidori for FFXIV WebView2 chrome.* shim and host bridge.
// Runs before Hachidori's unmodified content scripts, loads them dynamically from
// manifest.json, routes runtime RPCs to C# HachidoriWebBridge, plays pronunciation
// audio via Hachidori's own audio-offscreen.js, tracks popup bounding rects for
// transparent overlay hit-testing, injects the themed NPC picture camera button,
// and handles controller/gamepad actions.
// SPDX-License-Identifier: GPL-3.0-or-later
(function () {
  "use strict";

  const ASSET_BASE = "https://hachidori.ffxiv.local/";
  const OVERLAY_BASE = "https://ffxiv-host.local/";
  const HOST_TAG = "hachidori-host";
  const webview = globalThis.chrome?.webview ?? null;
  const postToHost = (payload) => {
    try {
      webview?.postMessage(payload);
    } catch (error) {
      console.error("hachidori-ffxiv: postMessage failed", error);
    }
  };

  // Capture the shadow root attached to <hachidori-host> so we can measure popup
  // rects, inject the themed NPC picture action button, and drive controller nav.
  let hachidoriShadow = null;
  let hachidoriHostEl = null;
  const nativeAttachShadow = Element.prototype.attachShadow;
  Element.prototype.attachShadow = function (init) {
    const root = nativeAttachShadow.call(this, init);
    if (this.localName === HOST_TAG) {
      hachidoriHostEl = this;
      hachidoriShadow = root;
      observeShadow(root);
    }
    return root;
  };

  // Respect plugin AudioVolume on any <audio> playback inside the WebView.
  let audioVolume = 0.8;
  const nativeMediaPlay = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...args) {
    try {
      this.volume = Math.max(0, Math.min(1, audioVolume));
    } catch { /* ignore */ }
    return nativeMediaPlay.apply(this, args);
  };

  let manifest = { manifest_version: 3, name: "Hachidori", version: "0.0.0", content_scripts: [] };
  let storageState = { options: {}, dictionaryState: null, lookupStats: null, wordStatusOverrides: null };
  let npcPicturesAvailable = false;
  let readerApi = null;
  let audioService = null;
  let audioServicePromise = null;
  let booted = false;

  const pendingRpc = new Map();
  let nextRpcId = 1;
  const messageListeners = new Set();
  const storageListeners = new Set();

  function dispatchListeners(listeners, ...args) {
    for (const listener of [...listeners]) {
      try {
        listener(...args);
      } catch (error) {
        console.warn("hachidori-ffxiv: listener error", error);
      }
    }
  }

  function callHostRpc(message) {
    return new Promise((resolve, reject) => {
      const id = nextRpcId++;
      pendingRpc.set(id, { resolve, reject, type: message?.type, requestId: message?.requestId });
      postToHost({ kind: "rpc", id, message });
    });
  }

  function settle(promise, callback) {
    if (typeof callback !== "function") return promise;
    promise.then(
      (value) => { callback(value); },
      (error) => {
        runtime.lastError = { message: error?.message || String(error) };
        try {
          callback();
        } finally {
          runtime.lastError = undefined;
        }
      },
    );
    return undefined;
  }

  async function ensureAudioService() {
    if (audioService) return audioService;
    if (!audioServicePromise) {
      audioServicePromise = import(`${ASSET_BASE}audio-offscreen.js`)
        .then((mod) => {
          audioService = mod.createAudioService(window);
          return audioService;
        })
        .catch((error) => {
          audioServicePromise = null;
          throw error;
        });
    }
    return audioServicePromise;
  }

  function currentAudioSources() {
    const opts = globalThis.HDReaderOptions?.normaliseOptions?.(storageState.options) ?? storageState.options ?? {};
    return Array.isArray(opts.audioSources) ? opts.audioSources : [];
  }

  async function handleAudioMessage(message) {
    const type = message.type;
    const requestId = message.requestId;
    try {
      const service = await ensureAudioService();
      const enriched = {
        ...message,
        owner: message.owner ?? "ffxiv-host",
        sources: Array.isArray(message.sources) ? message.sources : currentAudioSources(),
        language: message.language ?? "ja",
      };
      const result = await service(enriched);
      return { ok: true, type: `${type}_result`, requestId, ...result };
    } catch (error) {
      return { ok: false, type: `${type}_result`, requestId, error: error?.message || String(error) };
    }
  }

  function runPageUserScripts() {
    try {
      const scripts = globalThis.HDReaderOptions?.javaScriptUserScripts?.(storageState.options?.userScripts) ?? [];
      for (const script of scripts) {
        if (typeof script?.js !== "string" || !script.js.trim()) continue;
        try {
          // Run enabled user script JS in page world just like Hachidori's search/overlay host.
          const fn = new Function(script.js);
          fn();
        } catch (error) {
          console.warn(`hachidori-ffxiv: user script "${script.name || script.id}" failed`, error);
        }
      }
    } catch (error) {
      console.warn("hachidori-ffxiv: user scripts runner failed", error);
    }
  }

  async function handleSendMessage(message) {
    if (!message || typeof message !== "object") {
      return { ok: false, error: "empty message" };
    }
    const { target, type, requestId } = message;

    if (target === "hachidori-audio-events" && type === "hd_audio_playing") {
      dispatchListeners(messageListeners, message, { id: runtime.id }, () => {});
      return { ok: true };
    }

    if (target === "hachidori-audio-ui") {
      return { ok: true };
    }

    if (target === "hachidori-audio") {
      return handleAudioMessage(message);
    }

    if (target === "hachidori-page-zoom" && type === "hd_page_zoom") {
      return { ok: true, type: "hd_page_zoom_result", requestId, zoomFactor: 1 };
    }

    if (target === "hachidori-page-user-scripts" && type === "hd_user_scripts_run") {
      runPageUserScripts();
      return { ok: true, type: "hd_user_scripts_run_result", requestId };
    }

    if (type === "hd_open_external") {
      postToHost({ kind: "openExternal", url: message.url || "" });
      return { ok: true, type: "hd_open_external_result", requestId };
    }

    if (type === "hd_open_settings") {
      postToHost({ kind: "openSettings", section: message.section || "" });
      return { ok: true, type: "hd_open_settings_result", requestId };
    }

    return callHostRpc(message);
  }

  function readStorage(keys) {
    if (keys == null) return { ...storageState };
    if (typeof keys === "string") {
      return Object.hasOwn(storageState, keys) ? { [keys]: storageState[keys] } : {};
    }
    if (Array.isArray(keys)) {
      const out = {};
      for (const key of keys) {
        if (Object.hasOwn(storageState, key)) out[key] = storageState[key];
      }
      return out;
    }
    if (typeof keys === "object") {
      const out = {};
      for (const [key, fallback] of Object.entries(keys)) {
        out[key] = Object.hasOwn(storageState, key) && storageState[key] !== undefined ? storageState[key] : fallback;
      }
      return out;
    }
    return {};
  }

  const eventApi = (listeners) => ({
    addListener(listener) { listeners.add(listener); },
    removeListener(listener) { listeners.delete(listener); },
    hasListener(listener) { return listeners.has(listener); },
  });

  const runtime = {
    lastError: undefined,
    id: "hachidori-ffxiv-host",
    getURL(path) {
      const clean = String(path ?? "").replace(/^\/+/u, "");
      if (clean === "startup.html") return `${OVERLAY_BASE}host.html`;
      if (clean === "overlay-mode.js") return `${OVERLAY_BASE}overlay-mode.js`;
      return new URL(clean, ASSET_BASE).href;
    },
    getManifest() {
      return manifest;
    },
    sendMessage(message, callback) {
      return settle(handleSendMessage(message), callback);
    },
    onMessage: eventApi(messageListeners),
  };

  globalThis.chrome = {
    ...(globalThis.chrome ?? {}),
    runtime,
    storage: {
      local: {
        get(keys, callback) {
          return settle(Promise.resolve(readStorage(keys)), callback);
        },
      },
      onChanged: eventApi(storageListeners),
    },
    tabs: {
      create(info) {
        if (info?.url) postToHost({ kind: "openExternal", url: info.url });
        return Promise.resolve({});
      },
    },
    dom: {
      openOrClosedShadowRoot(el) {
        return el === hachidoriHostEl ? hachidoriShadow : el?.shadowRoot ?? null;
      },
    },
  };

  // ------------------------------------------------------------------ Shadow DOM tracking (rects + NPC camera)

  let rectsFrame = 0;
  let rectsTimer = 0;
  let lastRectsJson = "";
  let shadowObserver = null;
  let shadowResizeObserver = null;
  let lastReportedMatch = "";

  function visiblePopups() {
    if (!hachidoriShadow) return [];
    return [...hachidoriShadow.querySelectorAll(".gsm-hoshidicts-popup")].filter((el) => !el.hidden);
  }

  function computeRects() {
    const rects = [];
    const popups = visiblePopups();
    const dpr = window.devicePixelRatio || 1;
    const isCrystal = hachidoriHostEl?.dataset?.hoshidictsRenderer === "crystal";
    for (const el of popups) {
      const r = el.getBoundingClientRect();
      if (r.width <= 0 || r.height <= 0) continue;
      // Include protruding shadow / Crystal top crest so hover and paint aren't clipped.
      const padTop = isCrystal ? 26 : 8;
      const padSide = 8;
      rects.push({
        left: Math.floor((r.left - padSide) * dpr),
        top: Math.floor((r.top - padTop) * dpr),
        right: Math.ceil((r.right + padSide) * dpr),
        bottom: Math.ceil((r.bottom + padSide) * dpr),
      });
    }
    if (hachidoriShadow) {
      for (const extra of hachidoriShadow.querySelectorAll(".gsm-hoshidicts-image-hover-preview, .gsm-hoshidicts-audio-menu")) {
        if (extra.hidden) continue;
        const style = window.getComputedStyle(extra);
        if (style.display === "none" || style.visibility === "hidden") continue;
        const r = extra.getBoundingClientRect();
        if (r.width <= 0 || r.height <= 0) continue;
        rects.push({
          left: Math.floor((r.left - 4) * dpr),
          top: Math.floor((r.top - 4) * dpr),
          right: Math.ceil((r.right + 4) * dpr),
          bottom: Math.ceil((r.bottom + 4) * dpr),
        });
      }
    }
    return { rects, visibleCount: popups.length };
  }

  function flushRectsSync() {
    if (rectsFrame) {
      window.cancelAnimationFrame(rectsFrame);
      rectsFrame = 0;
    }
    if (rectsTimer) {
      window.clearTimeout(rectsTimer);
      rectsTimer = 0;
    }
    injectNpcCameraButtons();
    const state = computeRects();
    const json = JSON.stringify(state);
    if (json !== lastRectsJson) {
      lastRectsJson = json;
      postToHost({ kind: "rects", rects: state.rects, visibleCount: state.visibleCount });
    }
    reportMatchedText();
  }

  function scheduleRectsSync() {
    if (!rectsFrame) rectsFrame = window.requestAnimationFrame(flushRectsSync);
    // Safety fallback in case Chromium skips rAF while the overlay HWND is SW_HIDE.
    if (!rectsTimer) rectsTimer = window.setTimeout(flushRectsSync, 24);
  }

  function reportMatchedText() {
    // Report the root popup's first expression / matched text length so FFXIV highlights the word.
    const rootPopup = visiblePopups()[0];
    if (!rootPopup) {
      if (lastReportedMatch !== "") {
        lastReportedMatch = "";
        postToHost({ kind: "match", length: 0, expression: "", reading: "" });
      }
      return;
    }
    const exprEl = rootPopup.querySelector(".gsm-hoshidicts-expression, .jl-spelling, .nazeka-word, .crystal-spelling");
    const readEl = rootPopup.querySelector(".gsm-hoshidicts-reading, .jl-reading, .nazeka-reading, .crystal-reading");
    const expression = extractBaseText(exprEl);
    const reading = (readEl?.textContent || "").trim();
    const key = `${expression}\u0001${reading}`;
    if (key !== lastReportedMatch) {
      lastReportedMatch = key;
      postToHost({ kind: "match", length: currentMatchLength || expression.length, expression, reading });
    }
  }

  function extractBaseText(el) {
    if (!el) return "";
    // Exclude <rt>/<rp> ruby readings when reading the headword expression.
    const clone = el.cloneNode(true);
    for (const rt of clone.querySelectorAll("rt, rp")) rt.remove();
    return (clone.textContent || "").trim();
  }

  function createCameraIconSvg() {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 20 20");
    svg.setAttribute("width", "16");
    svg.setAttribute("height", "16");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "1.6");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");
    svg.setAttribute("aria-hidden", "true");
    svg.style.display = "block";
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", "M3.5 6.5h2.2l1.2-2h6.2l1.2 2h2.2a1.5 1.5 0 0 1 1.5 1.5v7a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 2 15V8a1.5 1.5 0 0 1 1.5-1.5Z");
    const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle");
    circle.setAttribute("cx", "10");
    circle.setAttribute("cy", "11");
    circle.setAttribute("r", "2.8");
    svg.append(path, circle);
    return svg;
  }

  function injectNpcCameraButtons() {
    if (!hachidoriShadow) return;
    const actionBars = hachidoriShadow.querySelectorAll(".gsm-hoshidicts-entry-actions");
    for (const bar of actionBars) {
      // Skip nested .gsm-hoshidicts-entry-actions containers (render/popup.js nests a child row).
      if (bar.parentElement?.closest(".gsm-hoshidicts-entry-actions")) continue;
      const existing = bar.querySelector(".ffxiv-npc-camera-btn");
      if (!npcPicturesAvailable) {
        if (existing) existing.remove();
        continue;
      }
      if (existing) continue;
      // Avoid adding camera button inside kanji view (no term expression/reading pair).
      const entry = bar.closest(".gsm-hoshidicts-entry, .jl-entry, .crystal-result, .crystal-block, .gsm-hoshidicts-popup");
      if (!entry || entry.querySelector(".gsm-hoshidicts-kanji-back")) continue;
      const exprEl = entry.querySelector(".gsm-hoshidicts-expression, .jl-spelling, .nazeka-word, .crystal-spelling")
        ?? bar.closest(".gsm-hoshidicts-popup")?.querySelector(".gsm-hoshidicts-expression, .jl-spelling, .nazeka-word, .crystal-spelling");
      const readEl = entry.querySelector(".gsm-hoshidicts-reading, .jl-reading, .nazeka-reading, .crystal-reading")
        ?? bar.closest(".gsm-hoshidicts-popup")?.querySelector(".gsm-hoshidicts-reading, .jl-reading, .nazeka-reading, .crystal-reading");
      const expression = extractBaseText(exprEl);
      if (!expression) continue;
      const reading = (readEl?.textContent || "").trim();

      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "gsm-hoshidicts-text-action-button ffxiv-npc-camera-btn";
      btn.title = "Attach an NPC picture in Hachidori (FFXIV)";
      btn.setAttribute("aria-label", btn.title);
      btn.style.display = "inline-flex";
      btn.style.alignItems = "center";
      btn.style.justifyContent = "center";
      btn.append(createCameraIconSvg());
      btn.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        postToHost({ kind: "npcCapture", expression, reading });
      });
      bar.append(btn);
    }
  }

  function observeShadow(root) {
    shadowObserver?.disconnect();
    shadowResizeObserver?.disconnect();
    shadowObserver = new MutationObserver(() => {
      if (shadowResizeObserver) {
        for (const el of root.querySelectorAll(".gsm-hoshidicts-popup")) {
          shadowResizeObserver.observe(el);
        }
      }
      injectNpcCameraButtons();
      scheduleRectsSync();
    });
    shadowObserver.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ["hidden", "style", "class", "data-hoshidicts-renderer"] });
    if (typeof ResizeObserver === "function") {
      shadowResizeObserver = new ResizeObserver(() => scheduleRectsSync());
    }
    window.addEventListener("hachidori-popup-shown", scheduleRectsSync);
    window.addEventListener("hachidori-popup-hidden", scheduleRectsSync);
  }

  // Track latest lookup match length returned from hd_lookup.
  let currentMatchLength = 0;

  // ------------------------------------------------------------------ Host commands from C#

  function applySnapshot(nextSnapshot, notifyChanged) {
    if (!nextSnapshot || typeof nextSnapshot !== "object") return;
    const changes = {};
    const keys = new Set([...Object.keys(storageState), ...Object.keys(nextSnapshot)]);
    for (const key of keys) {
      const oldValue = storageState[key];
      const newValue = nextSnapshot[key];
      const oldJson = JSON.stringify(oldValue);
      const newJson = JSON.stringify(newValue);
      if (oldJson !== newJson) {
        changes[key] = { oldValue, newValue };
        if (newValue === undefined) delete storageState[key];
        else storageState[key] = newValue;
      }
    }
    if (notifyChanged && Object.keys(changes).length > 0) {
      dispatchListeners(storageListeners, changes, "local");
      scheduleRectsSync();
    }
  }

  function showLookup(payload) {
    if (!readerApi || !payload) return;
    const sentence = String(payload.sentence ?? "");
    const charIndex = Math.max(0, Math.min(sentence.length, Number(payload.charIndex) || 0));
    const rect = payload.anchorRect ?? { left: 100, top: 100, width: 16, height: 20 };
    const dpr = window.devicePixelRatio || 1;
    if (payload.documentTitle) document.title = String(payload.documentTitle);

    const beforeEl = document.getElementById("ffxiv-before");
    const targetEl = document.getElementById("ffxiv-target");
    if (!beforeEl || !targetEl) return;

    const beforeText = sentence.slice(0, charIndex);
    const targetText = sentence.slice(charIndex);
    if (!targetText) return;

    beforeEl.textContent = beforeText;
    targetEl.textContent = targetText;
    const cssLeft = (rect.left || 0) / dpr;
    const cssTop = (rect.top || 0) / dpr;
    const cssWidth = Math.max(8, (rect.width || 16) / dpr);
    const cssHeight = Math.max(12, (rect.height || 20) / dpr);
    targetEl.style.left = `${cssLeft}px`;
    targetEl.style.top = `${cssTop}px`;
    targetEl.style.minWidth = `${cssWidth}px`;
    targetEl.style.height = `${cssHeight}px`;
    targetEl.style.fontSize = `${cssHeight}px`;

    const textNode = targetEl.firstChild;
    if (!textNode) return;

    const sel = window.getSelection();
    if (!sel) return;
    const range = document.createRange();
    const firstStep = textNode.data.codePointAt(0) > 0xffff ? 2 : 1;
    const selectLen = Math.min(textNode.length, Math.max(firstStep, Number(payload.tokenLength) || firstStep));
    range.setStart(textNode, 0);
    range.setEnd(textNode, selectLen);
    sel.removeAllRanges();
    sel.addRange(range);

    currentMatchLength = 0;
    readerApi.act("scanTextAtSelection");
    scheduleRectsSync();
  }

  function closeAllPopups() {
    window.getSelection()?.removeAllRanges();
    if (!readerApi) return;
    for (let i = 0; i < 8; i++) {
      if (visiblePopups().length === 0) break;
      if (!readerApi.act("close")) break;
    }
    flushRectsSync();
  }

  function scrollPopup(depth, deltaY) {
    const popups = visiblePopups();
    const target = popups[Math.min(depth, popups.length - 1)];
    if (!target) return;
    const scroller = target.querySelector(".gsm-hoshidicts-scroll, .jl-scroll, .nazeka-scroll, .plain-scroll, .crystal-scroll, .gsm-hoshidicts-content") ?? target;
    scroller.scrollTop += Number(deltaY) || 0;
  }

  function handleHostMessage(msg) {
    if (!msg || typeof msg !== "object") return;
    switch (msg.kind) {
      case "rpcReply": {
        const entry = pendingRpc.get(msg.id);
        if (!entry) return;
        pendingRpc.delete(msg.id);
        if (msg.error) {
          entry.reject(new Error(msg.error));
        } else {
          const reply = msg.reply ?? {};
          // Capture primary hd_lookup matched length for native hover highlight.
          if (entry.type === "hd_lookup") {
            if (Array.isArray(reply.results) && reply.results.length > 0) {
              const matched = reply.results[0]?.matched;
              if (typeof matched === "string" && matched.length > 0) {
                currentMatchLength = matched.length;
                scheduleRectsSync();
              }
            } else if (visiblePopups().length === 0) {
              postToHost({ kind: "lookupMiss" });
            }
          }
          entry.resolve(reply);
        }
        return;
      }
      case "init": {
        if (typeof msg.audioVolume === "number") audioVolume = msg.audioVolume;
        if (typeof msg.npcPicturesAvailable === "boolean") npcPicturesAvailable = msg.npcPicturesAvailable;
        applySnapshot(msg.snapshot, booted);
        if (!booted) void bootHachidori();
        return;
      }
      case "storageUpdate": {
        if (typeof msg.audioVolume === "number") audioVolume = msg.audioVolume;
        if (typeof msg.npcPicturesAvailable === "boolean") npcPicturesAvailable = msg.npcPicturesAvailable;
        applySnapshot(msg.snapshot, true);
        return;
      }
      case "wordStatusChanged": {
        dispatchListeners(messageListeners, {
          target: "hachidori-anki-content",
          type: "hd_anki_word_status_changed",
          revision: msg.revision ?? null,
        }, { id: runtime.id }, () => {});
        return;
      }
      case "showLookup": {
        showLookup(msg);
        return;
      }
      case "closeAll": {
        closeAllPopups();
        return;
      }
      case "closeTop": {
        readerApi?.act("close");
        scheduleRectsSync();
        return;
      }
      case "command": {
        if (msg.action) readerApi?.act(msg.action);
        scheduleRectsSync();
        return;
      }
      case "scroll": {
        scrollPopup(Number(msg.depth) || 0, msg.deltaY);
        return;
      }
      case "inspect": {
        injectNpcCameraButtons();
        const popups = visiblePopups();
        const root = popups[0] ?? null;
        const computed = root ? window.getComputedStyle(root) : null;
        const npcButtons = root ? root.querySelectorAll(".ffxiv-npc-camera-btn") : [];
        let maxNpcPerBar = 0;
        if (root) {
          for (const bar of root.querySelectorAll(".gsm-hoshidicts-entry-actions")) {
            if (bar.parentElement?.closest(".gsm-hoshidicts-entry-actions")) continue;
            const c = bar.querySelectorAll(".ffxiv-npc-camera-btn").length;
            if (c > maxNpcPerBar) maxNpcPerBar = c;
          }
        }
        const mineBtn = root?.querySelector(".gsm-hoshidicts-mine-button") ?? null;
        const audioButtons = root ? [...root.querySelectorAll(".gsm-hoshidicts-audio-button")] : [];
        const audioVisible = audioButtons.some((b) => !b.hidden && window.getComputedStyle(b).display !== "none");
        const dpr = window.devicePixelRatio || 1;
        let npcButtonRect = null;
        if (npcButtons.length > 0) {
          const br = npcButtons[0].getBoundingClientRect();
          if (br.width > 0 && br.height > 0) {
            npcButtonRect = {
              left: Math.floor(br.left * dpr),
              top: Math.floor(br.top * dpr),
              right: Math.ceil(br.right * dpr),
              bottom: Math.ceil(br.bottom * dpr),
            };
          }
        }
        postToHost({
          kind: "inspectReply",
          id: msg.id,
          renderer: hachidoriHostEl?.dataset?.hoshidictsRenderer ?? "",
          theme: hachidoriHostEl?.dataset?.hoshidictsTheme ?? "",
          variant: hachidoriHostEl?.dataset?.hoshidictsThemeVariant ?? "",
          visibleCount: popups.length,
          text: root?.textContent?.slice(0, 600) ?? "",
          background: computed?.backgroundColor ?? "",
          borderColor: computed?.borderColor ?? "",
          color: computed?.color ?? "",
          hasNpcButton: npcButtons.length > 0,
          npcButtonCount: npcButtons.length,
          maxNpcButtonsPerBar: maxNpcPerBar,
          hasMineButton: Boolean(mineBtn),
          mineButtonState: mineBtn?.dataset?.state ?? "",
          hasAudioButton: audioButtons.length > 0,
          audioButtonVisible: audioVisible,
          rects: computeRects().rects,
          npcButtonRect,
        });
        return;
      }
    }
  }

  webview?.addEventListener("message", (event) => handleHostMessage(event.data));

  // ------------------------------------------------------------------ Dynamic manifest loader

  function loadCss(url) {
    return new Promise((resolve) => {
      const link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = url;
      link.onload = () => resolve();
      link.onerror = () => resolve();
      document.head.appendChild(link);
    });
  }

  function loadScript(url) {
    return new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = url;
      script.async = false;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error(`Failed to load ${url}`));
      document.head.appendChild(script);
    });
  }

  async function bootHachidori() {
    if (booted) return;
    booted = true;
    try {
      const resp = await fetch(runtime.getURL("manifest.json"));
      if (!resp.ok) throw new Error(`manifest.json HTTP ${resp.status}`);
      manifest = await resp.json();
      const [cs] = manifest.content_scripts ?? [];
      for (const css of cs?.css ?? []) {
        await loadCss(runtime.getURL(css));
      }
      for (const js of cs?.js ?? []) {
        await loadScript(runtime.getURL(js));
      }
      readerApi = await globalThis.HDReaderReady;
      postToHost({ kind: "ready", version: manifest.version || "unknown" });
    } catch (error) {
      console.error("hachidori-ffxiv: boot failed", error);
      postToHost({ kind: "bootError", error: error?.stack || error?.message || String(error) });
    }
  }

  // Expose for debugging/automated tests.
  globalThis.__ffxivHachidori = {
    handleHostMessage,
    get shadow() { return hachidoriShadow; },
    get host() { return hachidoriHostEl; },
    get reader() { return readerApi; },
    get storage() { return storageState; },
  };

  window.addEventListener("DOMContentLoaded", () => {
    postToHost({ kind: "loaded" });
  });
}());
