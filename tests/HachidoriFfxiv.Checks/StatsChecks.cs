using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Stats;

internal static class StatsChecks
{
    public static void Run(Action<bool, string> check)
    {
        // 1. Non-trivial word filtering & Japanese char counting
        check(!NonTrivialWords.IsNonTrivial("の") && !NonTrivialWords.IsNonTrivial("する")
              && !NonTrivialWords.IsNonTrivial("それ") && !NonTrivialWords.IsNonTrivial("ている")
              && !NonTrivialWords.IsNonTrivial("123") && !NonTrivialWords.IsNonTrivial("……"),
            "non-trivial filter drops particles, auxiliaries, pronouns and noise");

        check(NonTrivialWords.IsNonTrivial("冒険者") && NonTrivialWords.IsNonTrivial("エオルゼア")
              && NonTrivialWords.IsNonTrivial("蛮神") && NonTrivialWords.IsNonTrivial("竜")
              && NonTrivialWords.IsNonTrivial("クリスタル"),
            "non-trivial filter retains meaningful FFXIV vocabulary and kanji terms");

        var (jp, kanji, unique) = NonTrivialWords.CountJapaneseChars("冒険者𠮷よ、進め！");
        check(jp == 7 && kanji == 5 && unique.Count == 5,
            "Japanese character counter counts kana/kanji accurately and preserves surrogate pairs");

        var fallback = NonTrivialWords.ExtractFallbackWords("エスティニアンは蛮神と戦った。");
        check(fallback.Contains("エスティニアン") && fallback.Contains("蛮神"),
            "fallback word extractor extracts kanji and katakana compounds when engine is offline");

        var rank = NonTrivialWords.GetRank(105_000);
        check(rank.Title == "Sharlayan Archivist" && rank.NextTarget == 250_000,
            "immersion rank resolves FFXIV title and next milestone");

        // 2. StatsTracker multi-day tracking, 7d vs 30d windows, NPCs, lookups, Anki cards, persistence
        var dir = Path.Combine(Path.GetTempPath(), "hachidori-stats-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            using (var tracker = new StatsTracker(dir, segmenter: null, utcNow: () => now))
            {
                // 20 days ago: should show in 30d and AllTime, NOT in 7d
                var dayOld = now.AddDays(-20);
                tracker.RecordLine("Talk", "アルフィノ", "帝国軍が侵攻してきた。", dayOld);
                tracker.RecordParsedWords(["帝国軍", "が", "侵攻", "する"], dayOld.ToLocalTime(), includeSession: false);

                // Yesterday & today (2-day streak)
                var yesterday = now.AddDays(-1);
                tracker.RecordLine("Talk", "ヤ・シュトラ", "エーテルの流れが乱れているわ。", yesterday);
                tracker.RecordParsedWords(["エーテル", "の", "流れ", "乱れる"], yesterday.ToLocalTime(), includeSession: false);

                tracker.RecordLine("Talk", "エスティニアン", "竜の眼が反応している。蛮神の気配だ。", now);
                // Duplicate immediate capture should be ignored
                tracker.RecordLine("Talk", "エスティニアン", "竜の眼が反応している。蛮神の気配だ。", now.AddSeconds(1));
                // Popup source should be ignored
                tracker.RecordLine("Popup", "", "はい", now.AddSeconds(2));

                tracker.RecordParsedWords(["蛮神", "蛮神", "気配", "の", "だ"], now.ToLocalTime(), includeSession: true);

                var lookup = LookupResult.Parse(JsonNode.Parse("""
                {"matched":"蛮神","deinflected":"蛮神","term":{"expression":"蛮神","reading":"ばんしん",
                 "glossaries":[{"dictionary":"FFXIV","glossary":"[\"Primal\"]"}],
                 "frequencies":[{"dictionary":"JPDB","frequencies":[{"value":1200,"displayValue":"1200"}]}],
                 "pitches":[{"dictionary":"Kanjium","pitches":[{"position":1,"pattern":"HLLL"}]}]}}
                """)!.AsObject());
                tracker.RecordLookup(lookup);
                tracker.RecordMinedCard(1730000000000L, lookup, "蛮神の気配だ。",
                    "エスティニアン", hasScreenshot: true, hasAudio: true, hasAnimation: false, hasTranslation: true);

                var snap7 = tracker.GetSnapshot(StatsRange.Last7Days);
                var snap30 = tracker.GetSnapshot(StatsRange.Last30Days);

                check(snap7.Lines == 2 && snap30.Lines == 3,
                    "stats aggregate lines accurately across 7-day and 30-day windows and drop duplicates/popups");
                check(snap7.CurrentStreakDays == 2 && snap7.BestStreakDays >= 2,
                    "daily reading streak counts consecutive active days");
                check(snap7.TopParsedWords.FirstOrDefault()?.Word == "蛮神" && snap7.TopParsedWords.All(w => w.Word != "帝国軍"),
                    "7-day non-trivial word ranking highlights recent vocabulary and excludes older words");
                check(snap30.TopParsedWords.Any(w => w.Word == "帝国軍"),
                    "30-day non-trivial word ranking includes words from 20 days ago");
                check(snap7.TopNpcs.Any(n => n.Speaker == "エスティニアン") && snap7.TopLookedUpWords.Any(w => w.Word == "蛮神"),
                    "NPC leaderboard and lookup stats record speakers and looked-up words");
                check(snap7.RecentMinedCards.Count == 1 && snap7.RecentMinedCards[0].Speaker == "エスティニアン"
                      && snap7.RecentMinedCards[0].HasScreenshot && snap7.RecentMinedCards[0].HasAudio && snap7.RecentMinedCards[0].HasTranslation,
                    "mined Anki cards store speaker, media flags and metadata");

                tracker.Flush();
            }

            // Reload from disk to verify atomic persistence
            using (var reloaded = new StatsTracker(dir, segmenter: null, utcNow: () => now))
            {
                var snapAll = reloaded.GetSnapshot(StatsRange.AllTime);
                check(snapAll.Lines == 3 && snapAll.RecentMinedCards.Count == 1 && snapAll.TopNpcs.Count == 3,
                    "stats persist atomically to disk and reload cleanly");
                var exported = reloaded.ExportToFile(dir);
                check(File.Exists(exported) && File.ReadAllText(exported).Contains("エスティニアン"),
                    "stats JSON export writes complete immersion data");
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }

        // 3. AnkiConnect note parsing
        var sampleNote = JsonNode.Parse("""
        {
          "noteId": 1730000000123,
          "modelName": "Lapis",
          "tags": ["ffxiv", "hachidori"],
          "fields": {
            "Expression": {"value": "<b>蛮神</b>", "order": 0},
            "Reading": {"value": "ばんしん", "order": 1},
            "Sentence": {"value": "蛮神の気配だ。[sound:line.ogg]", "order": 2},
            "Picture": {"value": "<img src=\"shot.jpg\">", "order": 3}
          }
        }
        """)!.AsObject();
        var parsedNote = AnkiCardFeed.ParseNote(sampleNote);
        check(parsedNote is { Expression: "蛮神", Reading: "ばんしん", Sentence: "蛮神の気配だ。", HasAudio: true, HasImage: true },
            "AnkiConnect note parser extracts clean fields and audio/image flags");

        // 4. Per-tab configuration reset defaults (verified directly on the built plugin assembly)
        CheckConfigurationResets(check);
    }

    private static void CheckConfigurationResets(Action<bool, string> check)
    {
        string? outputDir = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "HachidoriFfxiv", "bin", "x64", "Release");
            if (File.Exists(Path.Combine(candidate, "HachidoriFfxiv.dll")))
            {
                outputDir = candidate;
                break;
            }
        }
        if (outputDir is null)
            throw new FileNotFoundException("Built HachidoriFfxiv.dll not found for configuration reset check.");

        var dalamudDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
        var alc = new AssemblyLoadContext("config-reset-check", isCollectible: true);
        alc.Resolving += (_, name) =>
        {
            var local = Path.Combine(outputDir, name.Name + ".dll");
            if (File.Exists(local)) return alc.LoadFromAssemblyPath(local);
            var hook = Path.Combine(dalamudDir, name.Name + ".dll");
            if (File.Exists(hook)) return alc.LoadFromAssemblyPath(hook);
            return null;
        };
        try
        {
            var asm = alc.LoadFromAssemblyPath(Path.Combine(outputDir, "HachidoriFfxiv.dll"));
            var cfgType = asm.GetType("HachidoriFfxiv.Configuration")!;
            var cfg = Activator.CreateInstance(cfgType)!;
            T Get<T>(string prop) => (T)cfgType.GetProperty(prop)!.GetValue(cfg)!;
            void Set(string prop, object val) => cfgType.GetProperty(prop)!.SetValue(cfg, val);
            void Call(string method) => cfgType.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!.Invoke(cfg, null);

            Set("PopupWidth", 800f);
            Set("PopupFontSize", 30f);
            Set("FollowHachidoriTheme", false);
            Set("HachidoriThemeOverride", "crystal");
            Set("CaptureScreenshot", false);
            Set("GifFps", 20);
            Set("ControllerCursorSpeed", 1500f);
            Set("ReaderFontSize", 40f);
            Set("CaptureChat", false);
            Set("PublishPort", 9001);
            Set("RelayPort", 9999);

            Call("ResetPopupDefaults");
            check(Get<float>("PopupWidth") == 480f && Get<float>("PopupFontSize") == 18f && !Get<bool>("FollowHachidoriTheme"),
                "ResetPopupDefaults restores Popup settings without affecting Theme settings");

            Call("ResetThemeDefaults");
            check(Get<bool>("FollowHachidoriTheme") && Get<string>("HachidoriThemeOverride") == string.Empty && !Get<bool>("CaptureScreenshot"),
                "ResetThemeDefaults restores Theme settings without affecting Mining settings");

            Call("ResetMiningDefaults");
            check(Get<bool>("CaptureScreenshot") && Get<int>("GifFps") == 12 && Get<float>("ControllerCursorSpeed") == 1500f,
                "ResetMiningDefaults restores Mining settings without affecting Controller settings");

            Call("ResetControllerDefaults");
            Call("ResetReaderDefaults");
            Call("ResetCaptureDefaults");
            Call("ResetPublishDefaults");
            Call("ResetConnectionDefaults");
            check(Get<float>("ControllerCursorSpeed") == 900f && Get<float>("ReaderFontSize") == 26f
                  && Get<bool>("CaptureChat") && Get<int>("PublishPort") == 6677 && Get<int>("RelayPort") == 8771,
                "all remaining per-tab ResetDefaults restore exact Configuration defaults");
        }
        finally
        {
            alc.Unload();
        }
    }
}
