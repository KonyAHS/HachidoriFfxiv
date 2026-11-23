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

        // 5. Customizable goals, FFXIV expansion estimates, Rubi reading-time rule, and ecosystem codecs
        CheckGoalsExpansionsAndEcosystem(check);

        // 6. Live history.csv / SQLite stores: clear, consistent export and write retry
        CheckPersistenceStores(check);
    }

    private static void CheckPersistenceStores(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hachidori-store-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTime(2026, 11, 22, 14, 0, 0, DateTimeKind.Utc);
            var exportDir = Path.Combine(dir, "exports");
            using var tracker = new StatsTracker(dir, segmenter: null, utcNow: () => now);

            // Write retry: a history.csv write that fails is kept and written once on the next flush.
            tracker.RecordLine("Talk", "アルフィノ", "最初の台詞です。", now);
            tracker.Flush();
            var csvPath = tracker.HistoryCsvPath!;
            using (new FileStream(csvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                tracker.RecordLine("Talk", "アルフィノ", "書けなかった台詞です。", now.AddSeconds(10));
                tracker.Flush();
            }
            tracker.Flush();
            var csvText = File.ReadAllText(csvPath);
            check(CountOccurrences(csvText, "最初の台詞です。") == 1 && CountOccurrences(csvText, "書けなかった台詞です。") == 1,
                "a failed history.csv append is retried on the next flush without duplicating rows");

            if (NativeSqlite.IsAvailable && tracker.TidepoolSqlitePath is { } livePath)
            {
                // SQLite export with another connection open: committed rows still in the WAL must be included.
                using (var reader = NativeSqlite.OpenReadWrite(livePath))
                {
                    _ = reader.Query("SELECT COUNT(*) FROM lines_seen");
                    tracker.RecordLine("Talk", "ヤ・シュトラ", "WALに残っている台詞よ。", now.AddSeconds(20));
                    tracker.Flush();
                    var liveRows = Convert.ToInt64(reader.Query("SELECT COUNT(*) FROM lines_seen")[0][0]);
                    var exported = tracker.ExportTidepoolSqlite(exportDir);
                    using var copy = NativeSqlite.OpenReadOnly(exported);
                    var exportedRows = Convert.ToInt64(copy.Query("SELECT COUNT(*) FROM lines_seen")[0][0]);
                    check(liveRows == 3 && exportedRows == liveRows,
                        "SQLite export includes committed rows still in the WAL of an open database");
                }
            }

            // Clear all: aggregates, history.csv and the live SQLite log are all emptied.
            tracker.ClearAll();
            var snap = tracker.GetSnapshot(StatsRange.AllTime);
            var csvAfter = File.Exists(csvPath) ? File.ReadAllText(csvPath) : string.Empty;
            var linesExport = File.ReadAllText(tracker.ExportExStaticLinesCsv(exportDir));
            var sqliteCleared = true;
            if (NativeSqlite.IsAvailable && tracker.TidepoolSqlitePath is { } clearedPath && File.Exists(clearedPath))
            {
                using var db = NativeSqlite.OpenReadOnly(clearedPath);
                sqliteCleared = Convert.ToInt64(db.Query("SELECT COUNT(*) FROM lines_seen")[0][0]) == 0
                    && Convert.ToInt64(db.Query("SELECT COUNT(*) FROM game_lines")[0][0]) == 0;
            }
            check(snap.Lines == 0 && !csvAfter.Contains("最初の台詞です。") && !linesExport.Contains("最初の台詞です。") && sqliteCleared,
                "Clear all empties stats, the live history.csv, the SQLite log and later lines exports");

            tracker.RecordLine("Talk", "アリゼー", "消去の後の台詞よ。", now.AddMinutes(5));
            tracker.Flush();
            var csvNew = File.ReadAllText(csvPath);
            check(csvNew.Contains("消去の後の台詞よ。") && !csvNew.Contains("書けなかった台詞です。"),
                "lines recorded after Clear all are written to a fresh history.csv");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static void CheckGoalsExpansionsAndEcosystem(Action<bool, string> check)
    {
        // Rubi adaptive reading time & skip-mode filter
        var (creditNormal, pace1) = ReadingTimeRule.AdaptiveCredit(10_000_000L, 12, 0);
        check(creditNormal == 10_000_000L && pace1 > 0 && pace1 < ReadingTimeRule.PaceStart,
            "Rubi adaptive reading-time rule credits normal line gaps and lowers pace on fast reads");

        var (creditCapped, _) = ReadingTimeRule.AdaptiveCredit(900_000_000L, 10, ReadingTimeRule.PaceStart);
        check(creditCapped == 30_000_000L,
            "Rubi adaptive reading-time rule caps AFK gaps at 2.5x expected pace (30s for 10 chars at start pace)");

        var skipped = ReadingTimeRule.DetectSkippedIndices(
        [
            (5_000_000L, 20),
            (400_000L, 18),
            (300_000L, 22),
            (250_000L, 15),
            (200_000L, 8),
            (6_000_000L, 20),
        ]);
        check(skipped.Contains(1) && skipped.Contains(2) && skipped.Contains(3) && skipped.Contains(4) && !skipped.Contains(0) && !skipped.Contains(5),
            "Rubi skip-mode filter drops 3+ rapid lines >= 12 chars and trailing fast lines without affecting normal reads");

        check(ReadingTimeRule.CountEcosystemChars("「10時に駅で待ってるね！」") == 11,
            "ecosystem character counter counts Japanese letters and ASCII digits/letters without punctuation");

        check(ReadingTimeRule.StepValue(ReadingTimeRule.CharGoalSteps, 5_000, up: true) == 7_500
              && ReadingTimeRule.StepValue(ReadingTimeRule.CharGoalSteps, 5_000, up: false) == 4_000,
            "goal stepper steps cleanly up and down through LearnFromGames presets");

        // FFXIV expansion estimates & cumulative offset
        var journey = ExpansionEstimates.Compute(
            lifetimeJapaneseChars: 87_000,
            focusExpansionId: "hw",
            startingOffsetChars: ExpansionEstimates.CumulativeBefore("hw"),
            charsPerHour: 10_000,
            dailyCharGoal: 5_000);
        check(ExpansionEstimates.All.Count == 6 && ExpansionEstimates.TotalMsqChars > 1_800_000
              && journey.FocusExpansion?.Id == "hw"
              && journey.FocusCurrentChars == 87_000
              && Math.Abs(journey.FocusPercent - 50.0) < 0.5
              && journey.Expansions[0].Completed
              && journey.Expansions[1].Active
              && journey.EstimatedDaysAtGoal is > 17 and < 18,
            "FFXIV expansion estimates track cumulative MSQ progress, starting offsets, and goal ETAs");

        // MSQ quest name -> cumulative offset lookup (JP & EN, exact/partial, without double-counting tracked chars)
        var msqIndex = new MsqQuestIndex(
        [
            new(65537, "arr", "2.x", "冒険者への手引き", "Close to Home", "新生エオルゼア", 2_048, 0, 0),
            new(66064, "arr", "2.x", "究極幻想アルテマウェポン", "The Ultimate Weapon", "新生エオルゼア", 527, 260_000, 260_000),
            new(67205, "hw", "3.x", "蒼天のイシュガルド", "Heavensward", "蒼天のイシュガルド", 1_486, 480_000, 124_000),
            new(69190, "shb", "5.x", "漆黒のヴィランズ", "Shadowbringers", "漆黒のヴィランズ", 3_120, 1_010_000, 233_000),
        ]);
        var byJa = msqIndex.FindBest("究極幻想アルテマウェポン");
        var byEn = msqIndex.FindBest("heavensward");
        var byPartial = msqIndex.Search("ヴィランズ");
        check(byJa is { RowId: 66064, CumulativeBeforeChars: 260_000 }
              && byEn is { RowId: 67205, ExpansionId: "hw" }
              && byPartial.Count == 1 && byPartial[0].RowId == 69190
              && MsqQuestIndex.ComputeStartingOffset(byEn!, alreadyTrackedChars: 30_000) == 450_000
              && MsqQuestIndex.ComputeStartingOffset(byJa!, alreadyTrackedChars: 300_000) == 0,
            "MSQ quest name resolver matches Japanese/English/partial names and computes non-double-counted starting offsets");

        // Ecosystem export & import round-trips (ExStatic/Rubi/GSM CSV, exSTATic Stats CSV, Kechimochi CSV, Tidepool SQLite, SubMiner SQLite)
        var dir = Path.Combine(Path.GetTempPath(), "hachidori-eco-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var now = new DateTime(2026, 11, 22, 14, 0, 0, DateTimeKind.Utc);
            string exStaticLinesPath;
            string exStaticStatsPath;
            string kechimochiPath;
            string? tidepoolSqlitePath = null;

            using (var tracker = new StatsTracker(dir, segmenter: null, utcNow: () => now))
            {
                tracker.RecordLine("Talk", "アルフィノ", "帝国軍が侵攻してきた。急いで戻ろう。", now.AddDays(-1));
                tracker.RecordLine("Talk", "ヤ・シュトラ", "エーテルの流れが乱れているわ。気をつけて。", now.AddDays(-1).AddSeconds(12));
                tracker.RecordLine("Talk", "エスティニアン", "竜の眼が反応している。蛮神の気配だ。", now);
                tracker.RecordLine("Talk", "アリゼー", "ここで立ち止まるわけにはいかないのよ！", now.AddSeconds(14));
                tracker.Flush();

                // Verify live history.csv exists and matches ExStatic/Rubi/GSM columns
                check(tracker.HistoryCsvPath is not null && File.Exists(tracker.HistoryCsvPath),
                    "live append-only history.csv is written in ExStatic/Rubi/GSM format");

                // Goal progress + 7d comparison + 12-month series
                var goals = new GoalSettings(DailyCharGoal: 20, DailyMinutesGoal: 0, DailyCardsGoal: 0, GoalExpansionId: "arr");
                var snap7 = tracker.GetSnapshot(StatsRange.Last7Days, goals);
                var snap12 = tracker.GetSnapshot(StatsRange.Last12Months, goals);
                check(snap7.Goals is { TodayCharsMet: true, CurrentGoalStreak: 2, BestGoalStreak: 2 }
                      && snap7.Comparison is not null
                      && snap12.DailySeries.Count == 12,
                    "customizable daily goals compute goal streaks, 7-day comparisons, and 12-month monthly bars");

                var exportDir = Path.Combine(dir, "exports");
                exStaticLinesPath = tracker.ExportExStaticLinesCsv(exportDir);
                exStaticStatsPath = tracker.ExportExStaticStatsCsv(exportDir);
                kechimochiPath = tracker.ExportKechimochiCsv(exportDir, "Playing");

                var linesHeader = File.ReadLines(exStaticLinesPath).First().TrimStart('\uFEFF');
                var statsHeader = File.ReadLines(exStaticStatsPath).First().TrimStart('\uFEFF');
                var kechiText = File.ReadAllText(kechimochiPath);
                var kechiHeader = File.ReadLines(kechimochiPath).First().TrimStart('\uFEFF');

                check(linesHeader == EcosystemStatsCodec.ExStaticLinesHeader
                      && statsHeader == EcosystemStatsCodec.ExStaticStatsHeader
                      && kechiHeader == EcosystemStatsCodec.KechimochiActivityHeader
                      && kechiText.Contains("FINAL FANTASY XIV,Playing,"),
                    "ExStatic Lines CSV, exSTATic Stats CSV, and Kechimochi Activity CSV write exact ecosystem headers");

                if (NativeSqlite.IsAvailable)
                {
                    tidepoolSqlitePath = tracker.ExportTidepoolSqlite(exportDir);
                    using var db = NativeSqlite.OpenReadOnly(tidepoolSqlitePath);
                    var viewRows = db.Query("SELECT uuid, given_identifier, name, line, time FROM lines_seen_exstatic");
                    var gameRows = db.Query("SELECT id, game_name, line_text FROM game_lines");
                    check(viewRows.Count == 4 && gameRows.Count == 4,
                        "Tidepool / Telly / GSM WAL SQLite log populates lines_seen_exstatic view and game_lines table");
                }
            }

            // Re-import ExStatic lines CSV into a fresh tracker and verify idempotency (no double-counting)
            var importDir = Path.Combine(dir, "import-target");
            using (var target = new StatsTracker(importDir, segmenter: null, utcNow: () => now))
            {
                var r1 = target.ImportFromFile(exStaticLinesPath);
                var r2 = target.ImportFromFile(exStaticLinesPath);
                check(r1.ImportedLines == 4 && r1.TotalJapaneseChars > 40 && r2.ImportedLines == 0 && r2.SkippedDuplicates == 4,
                    "ExStatic / Rubi / GSM Lines CSV imports accurately and deduplicates re-imports");

                // Kechimochi CSV import
                var kRes = target.ImportFromFile(kechimochiPath);
                check(kRes.DetectedFormat == "Kechimochi Activity CSV" && kRes.ImportedDays == 2,
                    "Kechimochi Activity CSV imports daily history cleanly");

                // exSTATic Stats CSV import
                var sRes = target.ImportFromFile(exStaticStatsPath);
                check(sRes.DetectedFormat == "exSTATic Stats CSV" && sRes.ImportedDays == 2,
                    "exSTATic Daily Stats CSV imports daily history cleanly");

                if (NativeSqlite.IsAvailable)
                {
                    // SubMiner SQLite fixture import
                    var subMinerDb = Path.Combine(dir, "immersion.sqlite");
                    using (var sm = NativeSqlite.OpenReadWrite(subMinerDb))
                    {
                        sm.Execute("""
                            CREATE TABLE imm_videos (video_id INTEGER PRIMARY KEY, canonical_title TEXT NOT NULL);
                            CREATE TABLE imm_sessions (session_id INTEGER PRIMARY KEY, video_id INTEGER NOT NULL, started_at_ms TEXT NOT NULL, active_watched_ms INTEGER NOT NULL, lines_seen INTEGER NOT NULL, tokens_seen INTEGER NOT NULL, cards_mined INTEGER NOT NULL);
                            INSERT INTO imm_videos VALUES (1, 'FFXIV Cutscenes');
                            INSERT INTO imm_sessions VALUES (1, 1, '1795262400000', 1800000, 120, 900, 4);
                            """);
                    }
                    var smRes = target.ImportFromFile(subMinerDb);
                    check(smRes.DetectedFormat == "SubMiner SQLite" && smRes.ImportedDays == 1 && smRes.TotalJapaneseChars == 1800,
                        "SubMiner immersion.sqlite imports sessions and tokens cleanly");

                    // Tidepool SQLite import
                    if (tidepoolSqlitePath is not null)
                    {
                        var tpTargetDir = Path.Combine(dir, "tp-import");
                        using var tpTarget = new StatsTracker(tpTargetDir, segmenter: null, utcNow: () => now);
                        var tpRes = tpTarget.ImportFromFile(tidepoolSqlitePath);
                        check(tpRes.DetectedFormat == "Tidepool / Telly SQLite" && tpRes.ImportedLines == 4,
                            "Tidepool / Telly SQLite log imports cleanly via lines_seen_exstatic");
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
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
            Set("NotifyOnAnkiMine", false);
            Set("GifFps", 20);
            Set("ControllerCursorSpeed", 1500f);
            Set("ControllerAutoScan", false);
            var gamepadButtonsType = cfgType.GetProperty("ControllerScanButton")!.PropertyType;
            Set("ControllerScanButton", Enum.Parse(gamepadButtonsType, "R3"));
            var triggerModeType = cfgType.GetProperty("ControllerTriggerMode")!.PropertyType;
            Set("ControllerTriggerMode", Enum.Parse(triggerModeType, "Press"));
            Set("ControllerToggleKey", 0x7C);
            Set("ControllerToggleKeyCtrl", true);
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
            check(Get<bool>("CaptureScreenshot") && Get<bool>("NotifyOnAnkiMine") && Get<int>("GifFps") == 12 && Get<float>("ControllerCursorSpeed") == 1500f,
                "ResetMiningDefaults restores Mining settings without affecting Controller settings");

            Call("ResetControllerDefaults");
            Call("ResetReaderDefaults");
            Call("ResetCaptureDefaults");
            Call("ResetPublishDefaults");
            Call("ResetConnectionDefaults");
            check(Get<float>("ControllerCursorSpeed") == 900f && Get<bool>("ControllerAutoScan")
                  && Get<object>("ControllerScanButton").ToString() == "South"
                  && Get<object>("ControllerTriggerMode").ToString() == "Hold"
                  && Get<int>("ControllerToggleKey") == 0 && !Get<bool>("ControllerToggleKeyCtrl")
                  && Get<float>("ReaderFontSize") == 26f
                  && Get<bool>("CaptureChat") && Get<int>("PublishPort") == 6677 && Get<int>("RelayPort") == 8771,
                "all remaining per-tab ResetDefaults restore exact Configuration defaults");
        }
        finally
        {
            alc.Unload();
        }
    }
}
