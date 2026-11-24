using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Anki;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Offline Anki note rendering, SVG dictionary images, fast batched Anki preflight,
/// and the particle-aware word split. The renderer's HTML is compared with Hachidori's
/// own in the parity tool (--anki); these pin the shapes that comparison established.
/// </summary>
internal static class AnkiChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckWordSplit(check);
        CheckSvg(check);
        CheckFields(check);
        await CheckBatchedPreflightAsync(check);
        await CheckApiKeyAndBatchedMediaUploadAsync(check);
    }

    private static void CheckWordSplit(Action<bool, string> check)
    {
        // 黙っては|おるまい: the segmenter reads はおるまい as 羽織る, with は|おるまい as its alternative.
        var reply = JsonNode.Parse("""
            {"segments":[{"id":0,"spans":[
              {"start":0,"length":3,"functionWord":false,"alternative":[{"start":0,"length":1,"functionWord":false},{"start":1,"length":2,"functionWord":false}]},
              {"start":3,"length":5,"functionWord":false,"alternative":[{"start":3,"length":1,"functionWord":true},{"start":4,"length":4,"functionWord":false}]}
            ]}]}
            """);
        var spans = WordSpan.Parse(reply, "黙ってはおるまい");
        check(WordSpan.At(spans, 3) == new WordSpan(3, 5) && WordSpan.At(spans, 5) == new WordSpan(4, 4),
            "a word that swallowed a particle still opens whole, and its later characters open the word after the particle");
        check(WordSpan.At(spans, 2) == new WordSpan(0, 3), "a compound's later characters still open the whole compound");

        // 不語仙の座卓: a multi-word phrase with an interior particle (の) exposes 座卓 after it.
        var phraseReply = JsonNode.Parse("""
            {"segments":[{"id":0,"spans":[
              {"start":0,"length":6,"functionWord":false,"alternative":[
                {"start":0,"length":3,"functionWord":false},
                {"start":3,"length":1,"functionWord":true},
                {"start":4,"length":2,"functionWord":false}
              ]}
            ]}]}
            """);
        var phraseSpans = WordSpan.Parse(phraseReply, "不語仙の座卓");
        check(WordSpan.At(phraseSpans, 0) == new WordSpan(0, 6) && WordSpan.At(phraseSpans, 4) == new WordSpan(4, 2),
            "a phrase with an interior particle opens whole at the start and exposes the word after the particle");
    }

    private static void CheckSvg(Action<bool, string> check)
    {
        var badge = Encoding.UTF8.GetBytes("""
            <svg lang='ja' width='128' height='128' viewBox='0 0 128 128' xmlns='http://www.w3.org/2000/svg' version='1.1'>
              <rect width='128' height='128' ry='20' stroke='black' fill='transparent' stroke-width='8'/>
              <text text-anchor='middle' x='50%' y='50%' dy='.35em' font-family='sans-serif' font-size='100px' fill='black'>漢</text>
            </svg>
            """);
        check(SvgImage.LooksLikeSvg(badge) && !SvgImage.LooksLikeSvg([0x89, (byte)'P', (byte)'N', (byte)'G']), "SVG dictionary images are told apart from bitmaps");
        var image = SvgImage.Parse(badge);
        var primitives = image.Layout(Vector2.Zero, new Vector2(32, 32));
        var frame = primitives.OfType<SvgShape>().Single();
        var text = primitives.OfType<SvgText>().Single();
        check(image.Width == 128 && frame.Closed && frame.Fill is null && frame.Stroke == 0xFF000000 && Math.Abs(frame.StrokeWidth - 2) < 0.01f
              && text.Text == "漢" && text.Anchor == "middle" && Math.Abs(text.Size - 25) < 0.01f && Math.Abs(text.Baseline.X - 16) < 0.01f,
            "daijirin's SVG badges become a rounded frame and centred text at the requested size");
        var curves = SvgImage.Parse("""
            <svg viewBox="0 0 40 20"><path d="M10,10 c 0,10 10,10 10,0 s 10,-10 10,0 z" fill="none" stroke="black"/></svg>
            """u8.ToArray());
        var curve = curves.Layout(Vector2.Zero, new Vector2(40, 20)).OfType<SvgShape>().Single();
        check(curve.Closed && curve.Points.Any(p => Vector2.Distance(p, new Vector2(15, 17.5f)) < 0.25f)
              && curve.Points.Any(p => Vector2.Distance(p, new Vector2(25, 2.5f)) < 0.25f),
            "SVG relative and smooth curves render on opposite sides of their shared endpoints");
        check(SvgImage.Color("#1A73E8", null) == 0xFFE8731A && SvgImage.Color("none", 1) is null && SvgImage.Color("currentColor", null) == SvgImage.CurrentColor,
            "SVG colours convert to ImGui's ABGR, with none and currentColor kept apart");
        var polygon = SvgImage.Parse("""
            <svg viewBox="0 0 10 10"><polygon points="0,0 10,0 10,10 5,5 0,10" fill="black"/></svg>
            """u8.ToArray());
        var fill = polygon.Layout(Vector2.Zero, new Vector2(10, 10)).OfType<SvgShape>().Single();
        var filledArea = fill.Triangles.Chunk(3).Sum(indices =>
        {
            var ab = fill.Points[indices[1]] - fill.Points[indices[0]];
            var ac = fill.Points[indices[2]] - fill.Points[indices[0]];
            return Math.Abs(ab.X * ac.Y - ab.Y * ac.X) / 2;
        });
        check(Math.Abs(filledArea - 75) < 0.01f, "concave SVG fills leave the notch unpainted");
    }

    private static void CheckFields(Action<bool, string> check)
    {
        var request = JsonNode.Parse("""
            {"matched":"冒険者","deinflected":"冒険者","trace":[],
             "term":{"expression":"冒険者","reading":"ぼうけんしゃ","rules":"n","score":0,
               "furigana":[{"text":"冒","reading":"ぼう"},{"text":"険","reading":"けん"},{"text":"者","reading":"しゃ"}],
               "glossaries":[{"dictionary":"Test","glossary":"[\"adventurer {{c1::x}} <b>\"]","definitionTags":"n","termTags":"",
                 "tags":[{"name":"n","category":"partOfSpeech","order":0,"score":0,"notes":"noun"}]}],
               "frequencies":[{"dictionary":"Jiten","frequencyMode":"rank-based","frequencies":[{"value":100,"displayValue":"100"}]},
                              {"dictionary":"Other","frequencies":[{"value":300,"displayValue":"300㋕"}]}],
               "pitches":[{"dictionary":"Accent","pitches":[{"position":0,"pattern":"","nasal":[],"devoice":[]}],"transcriptions":[]}]},
             "generation":1,"sentence":"冒険者さん、待って","matchOffset":0,"searchQuery":"冒険者","popupSelectionText":"",
             "documentTitle":"FINAL FANTASY XIV","pageUrl":"","dictionaryAliases":{},"dictionaryIds":{},"frequencyDictionaries":["Jiten","Other"],
             "captureUnavailable":["sentence-audio","gif"],"screenshot":{"token":"t","filename":"shot.jpg"}}
            """)!.AsObject();
        var templates = new Dictionary<string, FieldTemplate>
        {
            ["Expression"] = new("{expression}"), ["ExpressionFurigana"] = new("{furigana-plain}"),
            ["Sentence"] = new("{cloze-prefix}<b>{cloze-body}</b>{cloze-suffix}"), ["Glossary"] = new("{glossary}"),
            ["FreqSort"] = new("{frequency-harmonic-rank}"), ["Frequency"] = new("{frequencies}"),
            ["PitchCategories"] = new("{pitch-accent-categories}"), ["Picture"] = new("{screenshot}"),
            ["MiscInfo"] = new("{document-title}"), ["Empty"] = new("{sentence-audio}<br>x"),
        };
        var fields = AnkiFields.Build(request, templates, options => new AnkiGlossary(request, null, []).Render(options));
        check(fields["Expression"] == "冒険者" && fields["ExpressionFurigana"] == "冒[ぼう] 険[けん] 者[しゃ]"
              && fields["Sentence"] == "<b>冒険者</b>さん、待って" && fields["MiscInfo"] == "FINAL FANTASY XIV",
            "offline note fields fill the expression, furigana, cloze sentence and document title");
        check(fields["FreqSort"] == "150" && fields["Frequency"] == "<ul style=\"text-align: left;\"><li>Jiten: 100</li><li>Other: 300㋕</li></ul>"
              && fields["PitchCategories"] == "heiban" && fields["Picture"] == "<img src=\"shot.jpg\">" && fields["Empty"] == "x",
            "offline frequency, pitch, screenshot and empty-line markers render as Hachidori's do");
        check(fields["Glossary"] == "<div class=\"yomitan-glossary\" style=\"text-align: left; contain: layout style paint; isolation: isolate;\"><ol>"
              + "<li data-dictionary=\"Test\"><div><i class=\"yomitan-glossary-meta\">(n, Test)</i> "
              + "<div class=\"gsm-hoshidicts-glossary-content\" data-hoshidicts-dictionary=\"Test\">adventurer &#123;&#123;c1::x&#125;&#125; &lt;b&gt;</div>"
              + "</div></li></ol><small class=\"yomitan-glossary-details\">Rules: n</small></div>",
            "offline glossaries serialize as Hachidori's, with cloze braces in definitions encoded");
        var styled = new HtmlElement("span");
        styled.ClassName = "a";
        styled.SetData("scName", "x");
        styled.SetStyle("margin", "0 0");
        styled.SetAttribute("lang", "ja");
        check(styled.OuterHtml == "<span class=\"a\" data-sc-name=\"x\" lang=\"ja\" style=\"margin: 0px;\"></span>",
            "inline styles join the attributes when serialized and report values as Chrome does");
    }

    private static async Task CheckBatchedPreflightAsync(Action<bool, string> check)
    {
        var actions = new List<string>();
        var findQueries = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        JsonNode? DispatchAction(string action, JsonObject? p)
        {
            return action switch
            {
                "deckNames" => new JsonArray("Default", "Japanese Mining"),
                "modelNamesAndIds" => new JsonObject { ["Kiku"] = 1789795159387L },
                "modelFieldNames" => new JsonArray("Expression", "Glossary", "Picture"),
                "canAddNotesWithErrorDetail" => new JsonArray((p?["notes"] as JsonArray ?? []).Select(n =>
                {
                    var expr = n?["fields"]?["Expression"]?.ToString() ?? "";
                    var allowDup = n?["options"]?["allowDuplicate"]?.GetValue<bool>() == true;
                    if (expr == "処断" && !allowDup)
                        return (JsonNode)new JsonObject { ["canAdd"] = false, ["error"] = "cannot create note because it is a duplicate" };
                    return (JsonNode)new JsonObject { ["canAdd"] = true, ["error"] = null };
                }).ToArray()),
                "findNotes" => HandleFind(p?["query"]?.ToString() ?? ""),
                _ => null,
            };
        }

        JsonNode HandleFind(string q)
        {
            findQueries.Add(q);
            return q.Contains("処断", StringComparison.Ordinal) ? new JsonArray(9001L) : new JsonArray();
        }

        app.MapPost("/", async context =>
        {
            var body = await JsonNode.ParseAsync(context.Request.Body) as JsonObject;
            var action = body?["action"]?.ToString() ?? "";
            actions.Add(action);
            JsonNode? result;
            if (action == "multi")
            {
                var sub = new JsonArray();
                foreach (var item in (body?["params"]?["actions"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var subAction = item["action"]?.ToString() ?? "";
                    sub.Add(new JsonObject { ["result"] = DispatchAction(subAction, item["params"] as JsonObject), ["error"] = null });
                }
                result = sub;
            }
            else
            {
                result = DispatchAction(action, body?["params"] as JsonObject);
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(new JsonObject { ["result"] = result, ["error"] = null }.ToJsonString());
        });

        await app.StartAsync();
        var url = app.Urls.Single();

        JsonObject MakeSnapshot(string behavior = "new", string scope = "deck") => JsonNode.Parse($$"""
        {
          "options": {
            "anki": {
              "url": "{{url}}",
              "apiKey": "",
              "templates": [{
                "id": "default",
                "name": "Default",
                "deck": "Japanese Mining",
                "model": "Kiku",
                "duplicateBehavior": "{{behavior}}",
                "duplicateScope": "{{scope}}",
                "captureScreenshot": true,
                "tags": ["hachidori"],
                "fieldTemplates": {
                  "Expression": { "value": "{expression}", "overwriteMode": "coalesce" },
                  "Glossary": { "value": "{glossary-brief}", "overwriteMode": "coalesce" },
                  "Picture": { "value": "{screenshot}", "overwriteMode": "coalesce" }
                }
              }]
            }
          }
        }
        """)!.AsObject();

        JsonObject MakeReq(string expression, bool allowDup = false) => new()
        {
            ["term"] = new JsonObject { ["expression"] = expression, ["reading"] = expression },
            ["matched"] = expression,
            ["sentence"] = expression,
            ["matchOffset"] = 0,
            ["generation"] = 1,
            ["configKey"] = "linked:fake-host-key",
            ["templateId"] = "default",
            ["allowDuplicate"] = allowDup,
        };

        var snap = MakeSnapshot();
        using var http = new HttpClient();
        using var miner = new OfflineAnkiMiner(() => snap, () => null, http);

        // 1. Status discovery uses ONE multi call and caches subsequent calls.
        var st1 = await miner.StatusAsync("default");
        var st2 = await miner.StatusAsync("default");
        check(st1.Available && st2.Available && actions.Count == 1,
            "checking Anki status twice only needs one server round trip");

        // 2. Live-mode batch preflight checks 4 entries (addable + duplicate + addable + forced) in 2 HTTP calls,
        //    without needing LocalEngine or Chrome audio probes.
        actions.Clear();
        var batch = await miner.TryLivePreflightManyAsync([
            MakeReq("焦り"),
            MakeReq("処断"),
            MakeReq("冒険者"),
            MakeReq("処断", allowDup: true),
        ]);
        check(batch is { Length: 4 }
              && batch[0].State == "addable" && batch[0].CanAdd && batch[0].Screenshot
              && batch[1].State == "duplicate" && batch[1].CanAdd && batch[1].NoteIds.SequenceEqual([9001L]) && batch[1].Screenshot
              && batch[2].State == "addable" && batch[2].CanAdd
              && batch[3].State == "addable" && batch[3].CanAdd,
            "batched local preflight resolves addable, duplicate (with noteIds), and allowDuplicate entries accurately");
        check(actions.Count <= 3,
            $"preflighting four entries with duplicates stays within three AnkiConnect round trips (got: {actions.Count})");
        check(findQueries.Single().Contains("dupe:1789795159387,処断", StringComparison.Ordinal)
              && findQueries.Single().Contains("deck:Japanese Mining", StringComparison.Ordinal),
            "deck-scoped duplicate lookup combines dupe: and deck: into one findNotes query");

        // 3. Warm batch preflight with no duplicates takes ONLY 1 AnkiConnect call!
        actions.Clear();
        var warm = await miner.TryLivePreflightManyAsync([MakeReq("焦り"), MakeReq("冒険者"), MakeReq("英雄")]);
        check(warm is { Length: 3 } && warm.All(d => d.State == "addable" && d.CanAdd)
              && actions.Count == 1,
            $"warm live batch preflight checks all entries in 1 AnkiConnect call (got: {string.Join(", ", actions)})");

        // 4. Overwrite and cross-model "all" scope return null to fall back cleanly to live host.
        snap = MakeSnapshot(behavior: "overwrite", scope: "deck");
        miner.InvalidateCache();
        check(await miner.TryLivePreflightManyAsync([MakeReq("焦り")]) is null,
            "live fast preflight falls back to live host for overwrite duplicate behavior");

        snap = MakeSnapshot(behavior: "prevent", scope: "all");
        miner.InvalidateCache();
        check(await miner.TryLivePreflightManyAsync([MakeReq("焦り")]) is null,
            "live fast preflight falls back to live host for cross-model 'all' duplicate scope");

        await app.StopAsync();
    }

    private static async Task CheckApiKeyAndBatchedMediaUploadAsync(Action<bool, string> check)
    {
        const string RequiredKey = "test-anki-secret-42";
        var storedFiles = new List<string>();
        var multiSubValid = true;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        JsonNode? Dispatch(string action, JsonObject? p, out string? error)
        {
            error = null;
            return action switch
            {
                "deckNames" => new JsonArray("Japanese Mining"),
                "modelNamesAndIds" => new JsonObject { ["Kiku"] = 1789795159387L },
                "modelFieldNames" => new JsonArray("Expression", "Picture"),
                "canAddNotesWithErrorDetail" => new JsonArray((p?["notes"] as JsonArray ?? []).Select(_ =>
                    (JsonNode)new JsonObject { ["canAdd"] = true, ["error"] = null }).ToArray()),
                "storeMediaFile" => StoreMedia(p),
                "addNote" => JsonValue.Create(7777L),
                "notesInfo" => new JsonArray(new JsonObject
                {
                    ["noteId"] = 7777L,
                    ["fields"] = new JsonObject
                    {
                        ["Expression"] = new JsonObject { ["value"] = "冒険者", ["order"] = 0 },
                        ["Picture"] = new JsonObject { ["value"] = "", ["order"] = 1 },
                    },
                }),
                "guiSelectedNotes" => new JsonArray(),
                "updateNoteFields" => null,
                _ => null,
            };
        }

        JsonNode StoreMedia(JsonObject? p)
        {
            var name = p?["filename"]?.ToString() ?? string.Empty;
            storedFiles.Add(name);
            return JsonValue.Create(name)!;
        }

        app.MapPost("/", async context =>
        {
            var body = await JsonNode.ParseAsync(context.Request.Body) as JsonObject;
            var key = body?["key"]?.ToString();
            var version = body?["version"] is JsonValue vv && vv.TryGetValue<int>(out var v) ? v : 0;
            context.Response.ContentType = "application/json";
            if (!string.Equals(key, RequiredKey, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync(new JsonObject { ["result"] = null, ["error"] = "valid api key must be provided" }.ToJsonString());
                return;
            }
            if (version != 6)
            {
                await context.Response.WriteAsync(new JsonObject { ["result"] = null, ["error"] = "unsupported version" }.ToJsonString());
                return;
            }

            var action = body?["action"]?.ToString() ?? "";
            if (action == "multi")
            {
                var sub = new JsonArray();
                foreach (var item in (body?["params"]?["actions"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var subKey = item["key"]?.ToString();
                    var subVer = item["version"] is JsonValue sv && sv.TryGetValue<int>(out var svInt) ? svInt : 0;
                    if (!string.Equals(subKey, RequiredKey, StringComparison.Ordinal) || subVer != 6)
                    {
                        multiSubValid = false;
                        sub.Add(new JsonObject { ["result"] = null, ["error"] = "valid api key and version 6 must be provided" });
                        continue;
                    }
                    var res = Dispatch(item["action"]?.ToString() ?? "", item["params"] as JsonObject, out var subErr);
                    sub.Add(new JsonObject { ["result"] = res, ["error"] = subErr });
                }
                await context.Response.WriteAsync(new JsonObject { ["result"] = sub, ["error"] = null }.ToJsonString());
                return;
            }

            var result = Dispatch(action, body?["params"] as JsonObject, out var err);
            await context.Response.WriteAsync(new JsonObject { ["result"] = result, ["error"] = err }.ToJsonString());
        });

        await app.StartAsync();
        var url = app.Urls.Single();

        // 1. Direct operations surface authentication errors and use the configured key.
        string? apiKey = null;
        using (var connect = new HachidoriFfxiv.Media.AnkiConnect(() => url, () => apiKey))
        {
            var authError = false;
            try { await connect.FieldsAsync(7777L); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("valid api key")) { authError = true; }
            check(authError, "AnkiConnect surfaces the server's authentication error when a key is missing");

            apiKey = RequiredKey;
            var fields = await connect.FieldsAsync(7777L);
            var stored = await connect.StoreMediaAsync("media-clip.ogg", [1, 2, 3]);
            await connect.UpdateFieldsAsync(7777L, new Dictionary<string, string> { ["Picture"] = "<img src=\"media-clip.ogg\">" });
            check(fields.Count == 2 && stored == "media-clip.ogg",
                "AnkiConnect attaches API key to notesInfo, storeMediaFile, and updateNoteFields");
        }

        // 2. OfflineAnkiMiner batched media upload (rich-HTML Picture field) includes version: 6 and key on sub-actions.
        storedFiles.Clear();
        multiSubValid = true;
        var snap = JsonNode.Parse($$"""
        {
          "options": {
            "anki": {
              "url": "{{url}}",
              "apiKey": "{{RequiredKey}}",
              "templates": [{
                "id": "default",
                "name": "Default",
                "deck": "Japanese Mining",
                "model": "Kiku",
                "duplicateBehavior": "new",
                "duplicateScope": "deck",
                "captureScreenshot": true,
                "tags": ["hachidori"],
                "fieldTemplates": {
                  "Expression": { "value": "{expression}", "overwriteMode": "coalesce" },
                  "Picture": { "value": "<div>{screenshot}</div>", "overwriteMode": "coalesce" }
                }
              }, {
                "id": "secondary",
                "name": "Secondary",
                "deck": "Japanese Mining",
                "model": "Kiku",
                "duplicateBehavior": "prevent",
                "duplicateScope": "deck",
                "captureScreenshot": false,
                "tags": ["secondary"],
                "fieldTemplates": {
                  "Expression": { "value": "{expression}", "overwriteMode": "coalesce" }
                }
              }]
            }
          }
        }
        """)!.AsObject();
        using var native = HachidoriFfxiv.Offline.NativeEngine.LoadFrom(AppContext.BaseDirectory);
        using var localEngine = new HachidoriFfxiv.Offline.LocalEngine(native);
        using var http = new HttpClient();
        using var miner = new OfflineAnkiMiner(() => snap, () => localEngine, http);
        var status = await miner.StatusAsync("default");
        var implicitStatus = await miner.StatusAsync(null);
        var secondaryStatus = await miner.StatusAsync("secondary");
        check(status.Available && implicitStatus.Available && status.ConfigKey == implicitStatus.ConfigKey
              && secondaryStatus.Available && secondaryStatus.ConfigKey != status.ConfigKey,
            "offline Anki status resolves default templateId consistently (null == default) while keeping other templates distinct");

        // 3. Offline preflight + submit with explicit templateId="default" accepts configKey from StatusAsync(null),
        //    and secondary templates keep distinct configKeys and preflight cleanly with their own status key.
        var req = new JsonObject
        {
            ["term"] = new JsonObject { ["expression"] = "冒険者", ["reading"] = "ぼうけんしゃ" },
            ["matched"] = "冒険者",
            ["sentence"] = "冒険者さん",
            ["matchOffset"] = 0,
            ["generation"] = localEngine.Generation,
            ["configKey"] = implicitStatus.ConfigKey,
            ["templateId"] = "default",
        };
        var preflight = await miner.PreflightManyAsync([req]);
        check(preflight.Length == 1 && preflight[0].State == "addable" && preflight[0].CanAdd,
            "offline preflight accepts default templateId using configKey from implicit null template status");

        var secondaryReq = new JsonObject
        {
            ["term"] = new JsonObject { ["expression"] = "残留", ["reading"] = "ざんりゅう" },
            ["matched"] = "残留",
            ["sentence"] = "残留",
            ["matchOffset"] = 0,
            ["generation"] = localEngine.Generation,
            ["configKey"] = secondaryStatus.ConfigKey,
            ["templateId"] = "secondary",
        };
        var secondaryMismatch = (JsonObject)secondaryReq.DeepClone();
        secondaryMismatch["configKey"] = implicitStatus.ConfigKey;
        var secondaryPreflights = await miner.PreflightManyAsync([secondaryReq, secondaryMismatch]);
        check(secondaryPreflights.Length == 2
              && secondaryPreflights[0].State == "addable" && secondaryPreflights[0].CanAdd
              && secondaryPreflights[1].State == "error"
              && (secondaryPreflights[1].Error?.Contains("Anki configuration changed", StringComparison.Ordinal) ?? false),
            "offline preflight matches secondary template configKey and rejects keys from a different template");

        var shot = new HachidoriFfxiv.Hachidori.ScreenshotMedia("tok", "hachidori-screenshot-test.jpg", Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0]));
        var decision = await miner.SubmitAsync(req, shot);
        check(decision.State == "added" && decision.NoteIds.SequenceEqual([7777L])
              && multiSubValid && storedFiles.Contains("hachidori-screenshot-test.jpg"),
            "offline batched media upload includes version 6 and API key on every multi sub-action");

        await app.StopAsync();
    }
}
