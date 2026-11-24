using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.WebView;
using Microsoft.Web.WebView2.Core;

/// <summary>
/// Phase 1 + Phase 3 checks for the WebView2 Hachidori frontend host:
/// bundle manager filtering/validation/rollback, chrome.* RPC bridge projection + media hooks,
/// and live headless WebView2 rendering of Hachidori's unmodified themes.
/// </summary>
internal static class WebViewChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckBundleFiltering(check);
        CheckBundleLifecycleAndRollback(check);
        await CheckBridgeProjectionAndRpcAsync(check);
        await CheckLiveWebViewThemesAsync(check);
    }

    private static void CheckBundleFiltering(Action<bool, string> check)
    {
        check(HachidoriBundleManager.ShouldIncludeBundleEntry("manifest.json"), "bundle filter keeps manifest.json");
        check(HachidoriBundleManager.ShouldIncludeBundleEntry("vendor/themes/crystal/theme.js"), "bundle filter keeps theme scripts");
        check(HachidoriBundleManager.ShouldIncludeBundleEntry("vendor/themes/crystal/theme.css"), "bundle filter keeps theme CSS");
        check(HachidoriBundleManager.ShouldIncludeBundleEntry("vendor/languages/registry-reader.js"), "bundle filter keeps language registry reader");
        check(!HachidoriBundleManager.ShouldIncludeBundleEntry("vendor/hoshidicts.wasm"), "bundle filter excludes heavy WASM binaries");
        check(!HachidoriBundleManager.ShouldIncludeBundleEntry("vendor/languages/ja/transforms.json"), "bundle filter excludes engine-only transforms");
        check(!HachidoriBundleManager.ShouldIncludeBundleEntry("../escape.js"), "bundle filter blocks path traversal");
    }

    private static void CheckBundleLifecycleAndRollback(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "hachidori-bundle-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src1 = Path.Combine(temp, "src-0.5.2");
            var src2 = Path.Combine(temp, "src-0.6.0");
            WriteMinimalBundle(src1, "0.5.2", ["default", "crystal", "bee"]);
            WriteMinimalBundle(src2, "0.6.0", ["default", "crystal", "bee", "sakura"]);

            using var mgr = new HachidoriBundleManager(Path.Combine(temp, "cache"));
            check(!mgr.Ready, "fresh bundle manager starts unready without cache");
            check(mgr.InstallFromDirectory(src1, "test"), "bundle manager installs valid v0.5.2 directory");
            check(mgr.Ready && mgr.ActiveVersion == "0.5.2", "active version matches installed manifest");
            check(mgr.Themes.Any(t => t.Slug == "crystal") && mgr.DefaultPalettes.Contains("solarized-light"),
                "bundle manager reads themes from vendor/themes/index.json and palettes from reader.css");
            mgr.MarkBootSuccess("0.5.2");
            check(mgr.LastGoodVersion == "0.5.2", "boot success records lastGoodVersion");

            check(mgr.InstallFromDirectory(src2, "test"), "bundle manager upgrades to v0.6.0");
            check(mgr.ActiveVersion == "0.6.0" && mgr.Themes.Any(t => t.Slug == "sakura"),
                "upgraded bundle exposes new upstream themes without C# changes");

            check(mgr.MarkBootFailure("0.6.0", "simulated syntax error"), "boot failure triggers rollback");
            check(mgr.ActiveVersion == "0.5.2", "rollback restores lastGoodVersion v0.5.2");

            var zipStream = new MemoryStream();
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                void AddEntry(string path, string content)
                {
                    var e = zip.CreateEntry("extension/" + path);
                    using var w = new StreamWriter(e.Open(), Encoding.UTF8);
                    w.Write(content);
                }
                AddEntry("manifest.json", """{"manifest_version":3,"name":"Hachidori","version":"0.7.0"}""");
                AddEntry("content.js", "// ok");
                AddEntry("theme-host.js", "// ok");
                AddEntry("render/popup.js", "// ok");
                AddEntry("render/glossary.js", "// ok");
                AddEntry("render/reader.css", ":host([data-hoshidicts-theme=\"dark\"]) {}");
                AddEntry("vendor/themes/index.json", """{"schema":1,"themes":[{"slug":"future","name":"Future","description":"New theme"}]}""");
                AddEntry("vendor/hoshidicts.wasm", "should-be-skipped");
            }
            zipStream.Position = 0;
            check(mgr.InstallFromZip(zipStream, "test-zip"), "bundle manager installs from chrome zip archive");
            check(mgr.ActiveVersion == "0.7.0" && mgr.Themes.Any(t => t.Slug == "future")
                  && !File.Exists(Path.Combine(mgr.ActivePath!, "vendor", "hoshidicts.wasm")),
                "zip extraction strips wrapper folder, skips wasm, and discovers new themes");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task CheckBridgeProjectionAndRpcAsync(Action<bool, string> check)
    {
        var opts = new WebBridgeOptions
        {
            HachidoriThemeOverride = "crystal",
            HachidoriVariantOverride = "blue",
            FollowHachidoriOpacity = false,
            PopupBackgroundAlpha = 0.72f,
            ExtraPopupCss = ".gsm-hoshidicts-popup { outline: 2px solid magenta; }",
            AnkiTemplateId = "tpl-ffxiv",
            CaptureScreenshot = true,
        };
        var hostSnapshot = JsonNode.Parse("""
        {
          "options": {
            "revision": 5,
            "popupTheme": "default",
            "popupThemeVariant": "auto",
            "popupOpacityPercent": 90,
            "customPopupCss": "/* host css */",
            "anki": {
              "templates": [
                { "id": "tpl-default", "name": "Default" },
                { "id": "tpl-ffxiv", "name": "FFXIV" }
              ]
            }
          }
        }
        """)!.AsObject();

        var backend = new MockBackend(hostSnapshot);
        var bridge = new HachidoriWebBridge(() => opts, backend);

        var p1 = bridge.BuildProjectedSnapshot();
        var p2 = bridge.BuildProjectedSnapshot();
        opts = opts with { PopupWidth = 520f };
        var p3 = bridge.BuildProjectedSnapshot();
        var opts1 = p1["options"]!.AsObject();
        var opts2 = p2["options"]!.AsObject();
        var opts3 = p3["options"]!.AsObject();
        check(opts1["popupTheme"]?.ToString() == "crystal" && opts1["popupThemeVariant"]?.ToString() == "blue",
            "bridge projects theme and variant overrides into chrome.storage snapshot");
        check(opts1["popupOpacityPercent"]?.GetValue<int>() == 72,
            "bridge projects plugin opacity when FollowHachidoriOpacity is off");
        check(opts1["customPopupCss"]?.ToString().Contains("outline: 2px solid magenta") == true
              && opts1["customPopupCss"]?.ToString().Contains("/* host css */") == true,
            "bridge merges host customPopupCss with FFXIV ExtraPopupCss");
        check(opts1["anki"]?["templates"]?[0]?["id"]?.ToString() == "tpl-ffxiv",
            "bridge promotes configured AnkiTemplateId to primary template");
        check(opts2["revision"]!.GetValue<int>() == opts1["revision"]!.GetValue<int>()
              && opts3["revision"]!.GetValue<int>() > opts2["revision"]!.GetValue<int>(),
            "bridge keeps options.revision stable on unchanged snapshots and bumps monotonically when options change");
        check(hostSnapshot["options"]?["popupTheme"]?.ToString() == "default",
            "bridge never mutates the host snapshot");

        // Batch preflight fan-out (sharing-protocol.js only forwards single hd_anki_preflight).
        var batchReply = await bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hachidori-anki",
            ["type"] = "hd_anki_preflight_batch",
            ["requestId"] = "batch-1",
            ["requests"] = new JsonArray(
                new JsonObject { ["term"] = new JsonObject { ["expression"] = "冒険者", ["reading"] = "ぼうけんしゃ" } },
                new JsonObject { ["term"] = new JsonObject { ["expression"] = "英雄", ["reading"] = "えいゆう" } }),
        });
        check(batchReply["ok"]?.GetValue<bool>() == true
              && batchReply["type"]?.ToString() == "hd_anki_preflight_batch_result"
              && batchReply["replies"] is JsonArray batchReplies && batchReplies.Count == 2
              && batchReplies[0]?["state"]?.ToString() == "addable",
            "bridge delegates hd_anki_preflight_batch and returns replies in order");

        // Stale preflight batch cancellation when a new lookup opens or popups close.
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnPreflightBatch = async (_, token) =>
        {
            slowStarted.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); }
            catch (OperationCanceledException) { slowCancelled.TrySetResult(true); throw; }
            return [];
        };
        var cancelledBatchTask = bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hachidori-anki",
            ["type"] = "hd_anki_preflight_batch",
            ["requestId"] = "batch-stale",
            ["requests"] = new JsonArray(new JsonObject { ["term"] = new JsonObject { ["expression"] = "焦り" } }),
        });
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.CancelPendingPreflights();
        var cancelledReply = await cancelledBatchTask.WaitAsync(TimeSpan.FromSeconds(2));
        backend.OnPreflightBatch = null;
        check(await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)) && cancelledReply["ok"]?.GetValue<bool>() == false,
            "bridge cancels superseded preflight batches immediately when a new lookup starts or popups close");

        // Screenshot capture -> Anki submit -> post-mine media attachment + stats recording.
        var shotReply = await bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hachidori-anki",
            ["type"] = "hd_anki_screenshot",
            ["requestId"] = "shot-1",
        });
        var token = shotReply["token"]?.ToString();
        var filename = shotReply["filename"]?.ToString();
        check(shotReply["ok"]?.GetValue<bool>() == true && !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(filename),
            "bridge answers hd_anki_screenshot using FFXIV MediaMiner");

        var submitReply = await bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hachidori-anki",
            ["type"] = "hd_anki_submit",
            ["requestId"] = "submit-1",
            ["request"] = new JsonObject
            {
                ["sentence"] = "冒険者さん、待って！",
                ["term"] = new JsonObject { ["expression"] = "冒険者", ["reading"] = "ぼうけんしゃ" },
                ["screenshot"] = new JsonObject { ["token"] = token, ["filename"] = filename },
            },
        });
        check(submitReply["ok"]?.GetValue<bool>() == true
              && submitReply["type"]?.ToString() == "hd_anki_submit_result"
              && submitReply["requestId"]?.ToString() == "submit-1"
              && backend.SubmittedScreenshot?.Token == token
              && backend.AttachedNoteId == 12345
              && backend.RecordedMinedNoteId == 12345,
            "bridge attaches FFXIV screenshot, runs post-mine media hook, and records immersion stats on Anki submit");

        // NormalizeLookupContext joins single FFXIV line wraps, turns paragraph breaks into spaces, and remaps offsets.
        var wrappedRaw = "  待て、ラウバーン局長！\nだが、思\n案の末に殺めたこともまた事実！  ";
        var wrappedExpected = "待て、ラウバーン局長！だが、思案の末に殺めたこともまた事実！";
        var (normSent, normIdx, normLen) = HachidoriWebBridge.NormalizeLookupContext(
            wrappedRaw, wrappedRaw.IndexOf("思", StringComparison.Ordinal), 3);
        check(normSent == wrappedExpected
              && normIdx == wrappedExpected.IndexOf("思案", StringComparison.Ordinal)
              && normLen == 2,
            $"NormalizeLookupContext joins wrapped lines and remaps offsets (sent='{normSent}', idx={normIdx}, len={normLen})");

        var paraRaw = "第一文。\n\n第二文の言葉。";
        var paraExpected = "第一文。 第二文の言葉。";
        var (paraSent, paraIdx, _) = HachidoriWebBridge.NormalizeLookupContext(
            paraRaw, paraRaw.IndexOf("第二文", StringComparison.Ordinal), 3);
        check(paraSent == paraExpected && paraIdx == paraExpected.IndexOf("第二文", StringComparison.Ordinal),
            $"NormalizeLookupContext turns blank-line paragraph breaks into a space (sent='{paraSent}', idx={paraIdx})");

        // Full FFXIV text box sentence + full multi-dictionary glossaries restoration on Crystal/JL projected submits.
        var fullBox = "待て、ラウバーン局長！正式な裁きも経ず冒険者さん、お待たせしました！";
        var wordStart = fullBox.IndexOf("冒険者", StringComparison.Ordinal);
        bridge.SetActiveLookupContext(fullBox, wordStart);
        await bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hoshidicts-offscreen",
            ["type"] = "hd_lookup",
            ["requestId"] = "lookup-enrich",
            ["text"] = "冒険者さん",
        });

        // Simulate Crystal theme projecting term.glossaries down to only the 2nd dictionary ("FFXIV") and Hachidori splitting sentence.
        await bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hachidori-anki",
            ["type"] = "hd_anki_submit",
            ["requestId"] = "submit-crystal-2nd",
            ["request"] = new JsonObject
            {
                ["sentence"] = "正式な裁きも経ず冒険者さん、お待たせしました！",
                ["matchOffset"] = 8,
                ["matched"] = "冒険者",
                ["term"] = new JsonObject
                {
                    ["expression"] = "冒険者",
                    ["reading"] = "ぼうけんしゃ",
                    ["glossaries"] = new JsonArray(
                        new JsonObject { ["dictionary"] = "FFXIV", ["glossary"] = "[\"Adventurer (Warrior of Light)\"]" }),
                },
            },
        });
        var enrichedDicts = (backend.SubmittedRequest?["term"]?["glossaries"] as JsonArray ?? [])
            .Select(g => g?["dictionary"]?.ToString() ?? string.Empty).ToArray();
        check(backend.SubmittedRequest?["sentence"]?.ToString() == fullBox
              && backend.SubmittedRequest?["matchOffset"]?.GetValue<int>() == wordStart
              && backend.AttachedSentence == fullBox,
            "bridge restores full multi-sentence FFXIV text box and matchOffset for root lookups");
        check(enrichedDicts.SequenceEqual(["FFXIV", "Jitendex"]),
            $"bridge restores remaining lookup dictionaries while keeping clicked Crystal section first ([{string.Join(", ", enrichedDicts)}])");

        // Nested definition lookup (different sentence not from the FFXIV box) keeps its own definition sentence untouched.
        await bridge.HandleRpcAsync(new JsonObject
        {
            ["target"] = "hachidori-anki",
            ["type"] = "hd_anki_submit",
            ["requestId"] = "submit-nested",
            ["request"] = new JsonObject
            {
                ["sentence"] = "危険を冒して旅をする人。",
                ["matchOffset"] = 0,
                ["matched"] = "危険",
                ["term"] = new JsonObject
                {
                    ["expression"] = "危険",
                    ["reading"] = "きけん",
                    ["glossaries"] = new JsonArray(
                        new JsonObject { ["dictionary"] = "Jitendex", ["glossary"] = "[\"danger\"]" }),
                },
            },
        });
        check(backend.SubmittedRequest?["sentence"]?.ToString() == "危険を冒して旅をする人。"
              && backend.SubmittedRequest?["matchOffset"]?.GetValue<int>() == 0,
            "bridge preserves definition sentence when mining from a nested popup");
        bridge.ClearActiveLookupContext();
    }

    private static async Task CheckLiveWebViewThemesAsync(Action<bool, string> check)
    {
        if (!WebViewPopupHost.IsRuntimeAvailable(out var wvVersion))
        {
            Console.WriteLine("SKIP: live WebView2 rendering check (WebView2 runtime not installed)");
            return;
        }

        var bundleSource = Environment.GetEnvironmentVariable("HACHIDORI_TEST_BUNDLE");
        var temp = Path.Combine(Path.GetTempPath(), "hachidori-wv-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bundles = new HachidoriBundleManager(Path.Combine(temp, "bundles"));
            bool haveBundle;
            if (!string.IsNullOrWhiteSpace(bundleSource) && Directory.Exists(bundleSource))
                haveBundle = bundles.InstallFromDirectory(bundleSource, "env");
            else
                haveBundle = await bundles.EnsureBundleAsync(targetVersion: null, allowNetwork: false);

            if (!haveBundle)
            {
                Console.WriteLine($"SKIP: live WebView2 theme check (no local Hachidori bundle; set HACHIDORI_TEST_BUNDLE to run; runtime {wvVersion})");
                return;
            }

            var opts = new WebBridgeOptions
            {
                HachidoriThemeOverride = "crystal",
                HachidoriVariantOverride = "blue",
                FollowHachidoriOpacity = true,
            };

            var mockSnapshot = JsonNode.Parse("""
            {
              "options": {
                "revision": 1,
                "hoverEnabled": true,
                "lookupMode": "hover",
                "popupTheme": "crystal",
                "popupThemeVariant": "blue",
                "popupOpacityPercent": 90,
                "popupWidthPx": 460,
                "popupHeightPx": 380,
                "showLookupCounts": true,
                "audioSources": [{ "id": "default-tts", "type": "text-to-speech-reading", "enabled": true, "url": "", "voice": "" }],
                "anki": {
                  "url": "http://127.0.0.1:8765",
                  "templates": [{ "id": "default", "name": "Default", "deck": "Japanese", "model": "Mining", "fieldTemplates": { "Word": { "value": "{expression}" } } }]
                }
              },
              "dictionaryState": {
                "schemaVersion": 1,
                "revision": 1,
                "dictionaries": [
                  { "id": "dict-1", "title": "Jitendex", "enabled": true, "termCount": 100000, "frequencyCount": 0, "pitchCount": 0, "kanjiCount": 0 },
                  { "id": "dict-2", "title": "FFXIV", "enabled": true, "termCount": 5000, "frequencyCount": 0, "pitchCount": 0, "kanjiCount": 0 }
                ],
                "groups": []
              }
            }
            """)!.AsObject();

            var backend = new MockBackend(mockSnapshot);
            var bridge = new HachidoriWebBridge(() => opts, backend);
            var npcCaptureTcs = new TaskCompletionSource<(string Expr, string Reading)>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.OnNpcCaptureRequested = (expr, reading) => npcCaptureTcs.TrySetResult((expr, reading));

            using var host = new WebViewPopupHost(
                Path.Combine(temp, "wv2-userdata"),
                bridge,
                bundles,
                ownerHwnd: 0,
                headless: true,
                initialWidth: 900,
                initialHeight: 600);

            var ready = await host.WaitUntilReadyAsync(TimeSpan.FromSeconds(20));
            check(ready, $"real WebView2 ({wvVersion}) boots unmodified Hachidori v{bundles.ActiveVersion} frontend{(ready ? "" : $" [error: {host.LastError}]")}");
            if (!ready) return;

            const long RequiredExStyles = 0x00000080L | 0x08000000L | 0x00000020L | 0x00080000L | 0x00200000L;
            var actualExStyle = GetWindowLongPtr(host.OverlayHwnd, -20).ToInt64();
            check((actualExStyle & RequiredExStyles) == RequiredExStyles,
                $"WebView2 overlay HWND permanently has click-through DirectComposition styles (0x{actualExStyle:X})");

            host.PushStorageUpdate(bridge.BuildProjectedSnapshot(), 0.8f, npcPicturesAvailable: true);
            await Task.Delay(350);
            var multiSentenceRaw = "待て、ラウバーン局長！\n冒険者さん、お待たせしました！";
            var multiSentenceNorm = "待て、ラウバーン局長！冒険者さん、お待たせしました！";
            var rawWordOffset = multiSentenceRaw.IndexOf("冒険者", StringComparison.Ordinal);
            var normWordOffset = multiSentenceNorm.IndexOf("冒険者", StringComparison.Ordinal);
            host.ShowLookup(multiSentenceRaw, rawWordOffset, new Vector2(80, 60), new Vector2(140, 84), "FINAL FANTASY XIV", tokenLength: 3);

            WebViewInspectResult? crystal = null;
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(150);
                crystal = await host.InspectAsync();
                if (crystal.VisibleCount > 0 && crystal.Text.Contains("冒険者")
                    && crystal.MineButtonState is not "" and not "checking")
                    break;
            }

            check(crystal is { VisibleCount: > 0 } && crystal.Renderer == "crystal"
                  && crystal.Text.Contains("冒険者") && crystal.Text.Contains("adventurer"),
                $"WebView2 renders unmodified Hachidori Crystal theme (renderer={crystal?.Renderer}, bg={crystal?.Background})");
            check(crystal is { HasMineButton: true, HasAudioButton: true, AudioButtonVisible: true, HasNpcButton: true, MaxNpcButtonsPerBar: 1, Rects.Length: > 0 },
                $"WebView2 Crystal popup exposes Anki mine (state={crystal?.MineButtonState}), visible audio, single NPC camera button per bar (max={crystal?.MaxNpcButtonsPerBar}, total={crystal?.NpcButtonCount}), and hit-test rects");
            check(crystal?.MineButtonState is "addable" or "ready" or "add",
                $"WebView2 Anki preflight batch transitions mine button out of checking (state={crystal?.MineButtonState})");
            check(host.MatchedLength == 3, $"WebView2 reports exact matched word length ({host.MatchedLength}) for FFXIV highlight");

            // Controller dive inside WebView2 popup: extract definition scan blocks, scan a nested word, open child popup, close child.
            host.SetControllerMode(true);
            IReadOnlyList<WebPopupScanBlock> scanBlocks = [];
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(100);
                scanBlocks = host.GetScanBlocks(0);
                if (scanBlocks.Count > 0) break;
            }
            var layerRect = host.GetPopupLayerRect(0);
            var targetBlock = scanBlocks.FirstOrDefault(b => b.Block.ScanText.Contains("危険", StringComparison.Ordinal));
            var targetIdx = targetBlock?.Block.ScanText.IndexOf("危険", StringComparison.Ordinal) ?? -1;
            var targetCell = targetBlock?.Cells.FirstOrDefault(c => c.Index == targetIdx) ?? default;
            check(scanBlocks.Count > 0 && layerRect is not null && targetBlock is not null && targetCell.Right > targetCell.Left,
                $"WebView2 controller mode extracts definition scan blocks and glyph rects (blocks={scanBlocks.Count}, cells={targetBlock?.Cells.Count ?? 0})");
            if (targetBlock is not null && targetCell.Right > targetCell.Left)
            {
                host.SetControllerCursor([(targetCell.Left, targetCell.Top, targetCell.Right, targetCell.Bottom)], "#A89CE4");
                host.ScanPopupAt(0, (targetCell.Left + targetCell.Right) * 0.5f, (targetCell.Top + targetCell.Bottom) * 0.5f);
                for (var i = 0; i < 25 && host.VisibleCount < 2; i++)
                    await Task.Delay(120);
                check(host.VisibleCount >= 2, $"WebView2 controller dive opens a nested child popup (visibleCount={host.VisibleCount})");
                host.CloseFrom(1);
                for (var i = 0; i < 20 && host.VisibleCount > 1; i++)
                    await Task.Delay(80);
                check(host.VisibleCount == 1, $"WebView2 CloseFrom(1) closes nested child popup while keeping root open (visibleCount={host.VisibleCount})");
            }
            host.SetControllerCursor(null);
            host.SetControllerMode(false);

            // End-to-end Crystal mine: verify full text box sentence (before + after word) AND all dictionaries arrive at Anki submit.
            backend.ResetSubmit();
            host.SendCommand("addNote");
            for (var i = 0; i < 30 && backend.SubmittedRequest is null; i++)
                await Task.Delay(100);
            var liveDicts = (backend.SubmittedRequest?["term"]?["glossaries"] as JsonArray ?? [])
                .Select(g => g?["dictionary"]?.ToString() ?? string.Empty).ToArray();
            check(backend.SubmittedRequest?["sentence"]?.ToString() == multiSentenceNorm
                  && backend.SubmittedRequest?["matchOffset"]?.GetValue<int>() == normWordOffset,
                $"WebView2 Crystal mine submits full multi-sentence FFXIV text box (sentence='{backend.SubmittedRequest?["sentence"]}', offset={backend.SubmittedRequest?["matchOffset"]})");
            check(liveDicts.SequenceEqual(["Jitendex", "FFXIV"]),
                $"WebView2 Crystal mine keeps all lookup dictionaries for MainDefinition + Glossary ([{string.Join(", ", liveDicts)}])");

            // End-to-end synthetic mouse input test via CoreWebView2CompositionController.SendMouseInput.
            if (crystal?.NpcButtonRect is { } btnRect)
            {
                var cx = (btnRect.Left + btnRect.Right) / 2;
                var cy = (btnRect.Top + btnRect.Bottom) / 2;
                host.SendMouseInput(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, cx, cy);
                host.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, cx, cy);
                host.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, cx, cy);
                var clicked = await Task.WhenAny(npcCaptureTcs.Task, Task.Delay(3000)) == npcCaptureTcs.Task;
                var (expr, _) = clicked ? await npcCaptureTcs.Task : (Expr: "", Reading: "");
                check(clicked && expr == "冒険者",
                    $"DirectComposition SendMouseInput clicks buttons inside popup without OS focus (clicked={clicked}, expr={expr})");
            }
            else
            {
                check(false, "InspectAsync returned NPC button rect for SendMouseInput click test");
            }

            var previewDir = Environment.GetEnvironmentVariable("HACHIDORI_TEST_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(previewDir))
            {
                Directory.CreateDirectory(previewDir);
                File.WriteAllBytes(Path.Combine(previewDir, "crystal-blue.png"), await host.CapturePngAsync());
            }

            // Live hot-swap to Bee's Theme without reloading the page.
            opts = opts with { HachidoriThemeOverride = "bee", HachidoriVariantOverride = "auto" };
            host.PushStorageUpdate(bridge.BuildProjectedSnapshot(), 0.8f, npcPicturesAvailable: true);
            WebViewInspectResult? bee = null;
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(150);
                bee = await host.InspectAsync();
                if (bee.Renderer == "bee" && bee.VisibleCount > 0) break;
            }
            check(bee is { Renderer: "bee", VisibleCount: > 0, MaxNpcButtonsPerBar: 1 } && bee.Text.Contains("adventurer") && bee.Background != crystal!.Background,
                $"WebView2 hot-swaps live to Bee's Theme (renderer={bee?.Renderer}, bg={bee?.Background}, maxCam={bee?.MaxNpcButtonsPerBar})");

            if (!string.IsNullOrWhiteSpace(previewDir))
                File.WriteAllBytes(Path.Combine(previewDir, "bee.png"), await host.CapturePngAsync());

            // Live hot-swap across remaining upstream renderers (jl, nazeka, plain).
            // Plain intentionally omits headers and action controls (contentMode: "text").
            foreach (var (slug, expectedCam) in new[] { ("jl", 1), ("nazeka", 1), ("plain", 0) })
            {
                opts = opts with { HachidoriThemeOverride = slug, HachidoriVariantOverride = "auto" };
                host.PushStorageUpdate(bridge.BuildProjectedSnapshot(), 0.8f, npcPicturesAvailable: true);
                WebViewInspectResult? themed = null;
                for (var i = 0; i < 25; i++)
                {
                    await Task.Delay(120);
                    themed = await host.InspectAsync();
                    if (themed.Renderer == slug && themed.VisibleCount > 0) break;
                }
                check(themed is { VisibleCount: > 0 } && themed.Renderer == slug && themed.MaxNpcButtonsPerBar == expectedCam,
                    $"WebView2 hot-swaps live to {slug} theme (renderer={themed?.Renderer}, bg={themed?.Background}, maxCam={themed?.MaxNpcButtonsPerBar})");
            }

            // Live hot-swap to Default + solarized-light + custom CSS override.
            opts = opts with
            {
                HachidoriThemeOverride = "solarized-light",
                ExtraPopupCss = ".gsm-hoshidicts-popup { border-color: rgb(255, 0, 128) !important; }",
            };
            host.PushStorageUpdate(bridge.BuildProjectedSnapshot(), 0.8f, npcPicturesAvailable: true);
            WebViewInspectResult? def = null;
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(150);
                def = await host.InspectAsync();
                if (def.Renderer == "default" && def.Theme == "solarized-light" && def.BorderColor.Contains("255, 0, 128")) break;
            }
            check(def is { Renderer: "default", Theme: "solarized-light", VisibleCount: > 0 } && def.BorderColor.Contains("255, 0, 128"),
                $"WebView2 hot-swaps to Default/solarized-light + applies custom CSS (border={def?.BorderColor})");

            host.CloseAll();
            WebViewInspectResult? closed = null;
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(80);
                closed = await host.InspectAsync();
                if (closed.VisibleCount == 0) break;
            }
            check(closed is { VisibleCount: 0 } && host.VisibleCount == 0, "CloseAll dismisses WebView2 popups and clears hit-test rects");

            // Non-headless visibility + cross-thread click-through check:
            // Starts hidden (SW_HIDE) with visibleCount==0, shows only when a popup opens, stays click-through, hides on CloseAll.
            using var liveHost = new WebViewPopupHost(
                Path.Combine(temp, "wv2-live-userdata"),
                bridge,
                bundles,
                ownerHwnd: 0,
                headless: false,
                initialWidth: 900,
                initialHeight: 600);
            var liveReady = await liveHost.WaitUntilReadyAsync(TimeSpan.FromSeconds(20));
            check(liveReady, $"non-headless DirectComposition WebView2 initializes while SW_HIDE{(liveReady ? "" : $" [error: {liveHost.LastError}]")}");
            if (liveReady)
            {
                liveHost.SyncOverlayFrame(100, 100, 900, 600, gameVisible: true);
                await Task.Delay(120);
                check(!IsWindowVisible(liveHost.OverlayHwnd), "overlay HWND stays hidden (SW_HIDE) when no popups are open");

                liveHost.PushStorageUpdate(bridge.BuildProjectedSnapshot(), 0.8f, npcPicturesAvailable: true);
                await Task.Delay(250);
                liveHost.ShowLookup("冒険者さん、お待たせしました！", 0, new Vector2(80, 60), new Vector2(140, 84), "FINAL FANTASY XIV", tokenLength: 3);
                for (var i = 0; i < 30 && liveHost.VisibleCount == 0; i++)
                    await Task.Delay(100);
                check(liveHost.VisibleCount > 0, $"ShowLookup produces popup rects even when overlay HWND starts SW_HIDE (visibleCount={liveHost.VisibleCount})");

                liveHost.SyncOverlayFrame(100, 100, 900, 600, gameVisible: true);
                await Task.Delay(150);
                check(IsWindowVisible(liveHost.OverlayHwnd), "overlay HWND becomes visible when popup opens");

                var pt = new WinPoint { X = 250, Y = 250 };
                var hit = WindowFromPoint(pt);
                check(hit != liveHost.OverlayHwnd, $"visible DirectComposition overlay HWND is 100% click-through cross-thread (hit=0x{hit:X}, overlay=0x{liveHost.OverlayHwnd:X})");

                liveHost.CloseAll();
                for (var i = 0; i < 20 && liveHost.VisibleCount > 0; i++)
                    await Task.Delay(80);
                liveHost.SyncOverlayFrame(100, 100, 900, 600, gameVisible: true);
                await Task.Delay(120);
                check(!IsWindowVisible(liveHost.OverlayHwnd), "overlay HWND hides again (SW_HIDE) after CloseAll");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private sealed class MockBackend(JsonObject snapshot) : IHachidoriWebBackend
    {
        public JsonObject Snapshot { get; } = snapshot;
        public bool CanCaptureScreenshot => true;
        public bool CanAttachMedia => true;
        public bool NpcPicturesAvailable => true;
        public ScreenshotMedia? SubmittedScreenshot { get; private set; }
        public JsonObject? SubmittedRequest { get; private set; }
        public string? AttachedSentence { get; private set; }
        public long AttachedNoteId { get; private set; }
        public long RecordedMinedNoteId { get; private set; }
        public Func<IReadOnlyList<JsonObject>, CancellationToken, Task<JsonObject[]>>? OnPreflightBatch { get; set; }

        public void ResetSubmit()
        {
            SubmittedRequest = null;
            SubmittedScreenshot = null;
            AttachedSentence = null;
            AttachedNoteId = 0;
            RecordedMinedNoteId = 0;
        }

        public Task<JsonObject[]> PreflightAnkiBatchAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct)
        {
            if (OnPreflightBatch is not null)
                return OnPreflightBatch(requests, ct);
            return Task.FromResult(requests.Select(_ => new JsonObject
            {
                ["ok"] = true, ["state"] = "addable", ["canAdd"] = true, ["screenshot"] = true, ["noteIds"] = new JsonArray(),
            }).ToArray());
        }

        public Task<ScreenshotMedia?> CaptureScreenshotAsync(CancellationToken ct) =>
            Task.FromResult<ScreenshotMedia?>(new ScreenshotMedia("tok-1", "ffxiv-shot.jpg", "AQID"));

        public Task<JsonObject> SubmitAnkiAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct)
        {
            SubmittedRequest = (JsonObject)request.DeepClone();
            SubmittedScreenshot = screenshot;
            return Task.FromResult(new JsonObject
            {
                ["ok"] = true,
                ["state"] = "added",
                ["canAdd"] = true,
                ["noteId"] = 12345L,
                ["noteIds"] = new JsonArray(12345L),
            });
        }

        public Task<string?> AttachPostMineMediaAsync(long noteId, string sentence, string? screenshotFilename, CancellationToken ct)
        {
            AttachedNoteId = noteId;
            AttachedSentence = sentence;
            return Task.FromResult<string?>("attached voice + AVIF");
        }

        public void RecordLookup(JsonObject lookupReply) { }

        public void RecordMinedCard(long noteId, JsonObject miningRequest, string sentence, bool hasScreenshot) =>
            RecordedMinedNoteId = noteId;

        public Task<JsonObject> SendRuntimeMessageAsync(string target, string type, JsonObject message, CancellationToken ct)
        {
            if (type == "hd_lookup")
            {
                var query = message["text"]?.ToString() ?? string.Empty;
                if (query.StartsWith("危険", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonNode.Parse("""
                    {
                      "ok": true,
                      "generation": 1,
                      "dictionaryCount": 2,
                      "results": [{
                        "matched": "危険",
                        "deinflected": "危険",
                        "trace": [],
                        "preprocessorSteps": 0,
                        "term": {
                          "expression": "危険",
                          "reading": "きけん",
                          "rules": "n",
                          "score": 100,
                          "furigana": [{"text":"危険","reading":"きけん"}],
                          "glossaries": [
                            { "dictionary": "Jitendex", "glossary": "[\"danger; peril; hazard\"]", "definitionTags": "n", "termTags": "common", "tags": [{"name":"n","category":"partOfSpeech","order":0,"score":0,"notes":"noun"}] }
                          ],
                          "frequencies": [],
                          "pitches": []
                        }
                      }]
                    }
                    """)!.AsObject());
                }
                return Task.FromResult(JsonNode.Parse("""
                {
                  "ok": true,
                  "generation": 1,
                  "dictionaryCount": 2,
                  "results": [{
                    "matched": "冒険者",
                    "deinflected": "冒険者",
                    "trace": [],
                    "preprocessorSteps": 0,
                    "term": {
                      "expression": "冒険者",
                      "reading": "ぼうけんしゃ",
                      "rules": "n",
                      "score": 100,
                      "furigana": [{"text":"冒険者","reading":"ぼうけんしゃ"}],
                      "glossaries": [
                        { "dictionary": "Jitendex", "glossary": "[\"adventurer (危険を冒す人)\"]", "definitionTags": "n", "termTags": "common", "tags": [{"name":"n","category":"partOfSpeech","order":0,"score":0,"notes":"noun"}] },
                        { "dictionary": "FFXIV", "glossary": "[\"Adventurer (Warrior of Light)\"]", "definitionTags": "", "termTags": "", "tags": [] }
                      ],
                      "frequencies": [{"dictionary":"BCCWJ","frequencies":[{"value":1200,"displayValue":"1200"}]}],
                      "pitches": [{"dictionary":"Kanjium","pitches":[{"position":3,"pattern":"LHHHL","nasal":[],"devoice":[]}],"transcriptions":[]}]
                    }
                  }]
                }
                """)!.AsObject());
            }
            if (type == "hd_styles")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["generation"] = 1, ["styles"] = new JsonArray() });
            if (type == "hd_anki_status")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["available"] = true, ["configKey"] = "test-key" });
            if (type == "hd_anki_preflight" || type == "hd_anki_view")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["state"] = "addable", ["canAdd"] = true, ["screenshot"] = true, ["noteIds"] = new JsonArray() });
            if (type == "hd_lookup_stats_read" || type == "hd_lookup_stats_record")
                return Task.FromResult(new JsonObject { ["ok"] = true, ["descriptor"] = new JsonObject { ["generation"] = "g1", ["revision"] = 1 }, ["statistics"] = new JsonObject { ["term"] = "冒険者", ["reading"] = "ぼうけんしゃ", ["lookupCount"] = 4 } });
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }

    private static void WriteMinimalBundle(string dir, string version, string[] themeSlugs)
    {
        Directory.CreateDirectory(Path.Combine(dir, "render"));
        Directory.CreateDirectory(Path.Combine(dir, "vendor", "themes"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $$"""{"manifest_version":3,"name":"Hachidori","version":"{{version}}","content_scripts":[{"js":["content.js"]}]}""");
        File.WriteAllText(Path.Combine(dir, "content.js"), "// test");
        File.WriteAllText(Path.Combine(dir, "theme-host.js"), "// test");
        File.WriteAllText(Path.Combine(dir, "render", "popup.js"), "// test");
        File.WriteAllText(Path.Combine(dir, "render", "glossary.js"), "// test");
        File.WriteAllText(Path.Combine(dir, "render", "reader.css"),
            ":host([data-hoshidicts-theme=\"default\"]){} :host([data-hoshidicts-theme=\"solarized-light\"]){}");
        var themesJson = new JsonArray(themeSlugs.Select(s => (JsonNode)new JsonObject
        {
            ["slug"] = s,
            ["name"] = s,
            ["description"] = $"Theme {s}",
        }).ToArray());
        File.WriteAllText(Path.Combine(dir, "vendor", "themes", "index.json"),
            new JsonObject { ["schema"] = 1, ["themes"] = themesJson }.ToJsonString());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(WinPoint pt);
}
