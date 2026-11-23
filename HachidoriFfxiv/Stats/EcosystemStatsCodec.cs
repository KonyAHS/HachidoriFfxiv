using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Stats;

/// <summary>One timestamped reading line in the universal ExStatic / Rubi / GSM / Tidepool schema.</summary>
public sealed record EcosystemLine(
    string GameUuid,
    string LineId,
    string GameName,
    string Text,
    double UnixSeconds,
    int Characters,
    string LineSource = "hook",
    string Speaker = "");

/// <summary>One aggregated daily entry (for exSTATic Stats CSV, Kechimochi CSV, and SubMiner rollups).</summary>
public sealed record EcosystemDailyEntry(
    string DateKey,
    string Title,
    int Characters,
    int Lines,
    double ActiveSeconds,
    int CardsMined = 0,
    string SourceKind = "");

public sealed record EcosystemImportResult(
    string DetectedFormat,
    int ImportedLines,
    int ImportedDays,
    int SkippedDuplicates,
    long TotalJapaneseChars);

/// <summary>
/// Bidirectional codec for the Japanese immersion stats ecosystem:
/// exSTATic (lines + daily stats CSV), Rubi / LearnFromGames (history.csv),
/// GameSentenceMiner (ExStatic import CSV, live HTTP /api/import-exstatic sync, gsm.db),
/// Kechimochi (Activity Logs CSV), Tidepool / Telly (WAL SQLite log + lines_seen_exstatic view),
/// and SubMiner (immersion.sqlite).
/// </summary>
public static class EcosystemStatsCodec
{
    public const string DefaultGameUuid = "hachidori-ffxiv";
    public const string DefaultGameName = "FINAL FANTASY XIV";
    public const string ExStaticLinesHeader = "uuid,given_identifier,name,line,time,characters,line_source,speaker";
    public const string ExStaticStatsHeader = "uuid,name,given_identifier,type,date,chars_read,lines_read,time_read,read_speed";
    public const string KechimochiActivityHeader = "Date,Log Name,Default Activity Type,Duration,Language,Characters,Activity Type,Notes,Media Variant";

    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // -------------------------------------------------------------- ExStatic / Rubi / GSM Lines CSV

    public static string FormatExStaticLineRow(EcosystemLine line)
    {
        var chars = line.Characters > 0 ? line.Characters : ReadingTimeRule.CountEcosystemChars(line.Text);
        var timeStr = line.UnixSeconds.ToString("F6", CultureInfo.InvariantCulture);
        return string.Join(',',
            CsvField(line.GameUuid),
            CsvField(line.LineId),
            CsvField(line.GameName),
            CsvField(line.Text),
            timeStr,
            chars.ToString(CultureInfo.InvariantCulture),
            CsvField(string.IsNullOrEmpty(line.LineSource) ? "hook" : line.LineSource),
            CsvField(line.Speaker ?? string.Empty));
    }

    public static void AppendExStaticLines(string csvPath, IReadOnlyList<EcosystemLine> lines)
    {
        if (lines.Count == 0)
            return;

        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Format the whole batch first, then append it in one write. If the write fails part way,
        // the file is truncated back to its previous length so a retry cannot duplicate rows.
        using var stream = new FileStream(csvPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        var start = stream.Seek(0, SeekOrigin.End);
        var needsHeader = start == 0;
        var sb = new StringBuilder();
        if (needsHeader)
            sb.Append(ExStaticLinesHeader).Append('\n');
        foreach (var line in lines)
            sb.Append(FormatExStaticLineRow(line)).Append('\n');

        var encoding = needsHeader ? Utf8Bom : Utf8NoBom;
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(sb.ToString());
        try
        {
            if (preamble.Length > 0)
                stream.Write(preamble);
            stream.Write(body);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            try { stream.SetLength(start); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Writes a consistent copy of a live SQLite database, including rows still in its WAL file.
    /// Copying the main database file alone can omit committed transactions.
    /// </summary>
    public static void BackupSqlite(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var db = NativeSqlite.OpenReadWrite(sourcePath);
        db.Execute("VACUUM INTO ?1", destinationPath);
    }

    /// <summary>Deletes every row Hachidori writes to its live SQLite log, keeping the schema.</summary>
    public static void ClearTidepoolSqlite(string sqlitePath)
    {
        if (!NativeSqlite.IsAvailable || !File.Exists(sqlitePath))
            return;

        using var db = NativeSqlite.OpenReadWrite(sqlitePath);
        db.Execute(TidepoolSchemaSql);
        db.Execute("BEGIN IMMEDIATE");
        try
        {
            foreach (var table in new[] { "lines_seen", "lines_played", "cards_mined", "game_lines", "episodes_played" })
                db.Execute($"DELETE FROM {table}");
            db.Execute("COMMIT");
        }
        catch
        {
            try { db.Execute("ROLLBACK"); } catch { }
            throw;
        }

        // Fold the WAL back into the main file so the old rows are not left on disk there.
        try { db.Query("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
    }

    public static void WriteExStaticLinesCsv(string csvPath, IEnumerable<EcosystemLine> lines)
    {
        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(csvPath, append: false, Utf8Bom);
        writer.NewLine = "\n";
        writer.WriteLine(ExStaticLinesHeader);
        foreach (var line in lines)
            writer.WriteLine(FormatExStaticLineRow(line));
    }

    // -------------------------------------------------------------- exSTATic Daily Stats CSV

    public static void WriteExStaticStatsCsv(
        string csvPath,
        IReadOnlyDictionary<string, DailyBucket> days,
        string gameName = DefaultGameName,
        string gameUuid = DefaultGameUuid)
    {
        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(csvPath, append: false, Utf8Bom);
        writer.NewLine = "\n";
        writer.WriteLine(ExStaticStatsHeader);

        foreach (var (dateKey, bucket) in days.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (bucket.JapaneseChars <= 0 && bucket.Lines <= 0)
                continue;
            var timeSec = Math.Max(0.0, bucket.ActiveSeconds);
            var speedCps = timeSec > 0 ? bucket.JapaneseChars / timeSec : 0.0;
            writer.WriteLine(string.Join(',',
                CsvField(gameUuid),
                CsvField(gameName),
                CsvField(gameName),
                "vn",
                CsvField(dateKey),
                bucket.JapaneseChars.ToString(CultureInfo.InvariantCulture),
                bucket.Lines.ToString(CultureInfo.InvariantCulture),
                timeSec.ToString("F2", CultureInfo.InvariantCulture),
                speedCps.ToString("F6", CultureInfo.InvariantCulture)));
        }
    }

    // -------------------------------------------------------------- Kechimochi Activity Logs CSV ("kimochi")

    /// <summary>
    /// Exports daily stats matching Morgawr/kechimochi's strict Activity Logs CSV spec
    /// (docs/csv-formats.md): Date, Log Name, Default Activity Type, Duration (minutes),
    /// Language, Characters, Activity Type, Notes, Media Variant.
    /// </summary>
    public static void WriteKechimochiCsv(
        string csvPath,
        IReadOnlyDictionary<string, DailyBucket> days,
        string gameName = DefaultGameName,
        string activityType = "Playing",
        string mediaVariant = "Videogame")
    {
        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var normalizedAct = activityType is "Reading" or "Playing" or "Watching" or "Listening" or "None"
            ? activityType
            : "Playing";

        using var writer = new StreamWriter(csvPath, append: false, Utf8Bom);
        writer.NewLine = "\n";
        writer.WriteLine(KechimochiActivityHeader);

        foreach (var (dateKey, bucket) in days.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (bucket.JapaneseChars <= 0 && bucket.Lines <= 0)
                continue;
            var minutes = Math.Max(1, (int)Math.Round(bucket.ActiveSeconds / 60.0));
            var notes = bucket.CardsMined > 0
                ? $"{bucket.Lines} lines, {bucket.CardsMined} cards mined"
                : $"{bucket.Lines} lines";

            writer.WriteLine(string.Join(',',
                CsvField(dateKey),
                CsvField(gameName),
                "Playing",
                minutes.ToString(CultureInfo.InvariantCulture),
                "Japanese",
                bucket.JapaneseChars.ToString(CultureInfo.InvariantCulture),
                CsvField(normalizedAct),
                CsvField(notes),
                CsvField(mediaVariant)));
        }
    }

    // -------------------------------------------------------------- Tidepool / Telly / GSM WAL SQLite Log

    private const string TidepoolSchemaSql = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        CREATE TABLE IF NOT EXISTS meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        INSERT OR IGNORE INTO meta (key, value) VALUES
            ('writer', 'HachidoriFfxiv'),
            ('schema_version', '1'),
            ('about', 'FFXIV Japanese immersion log for Tidepool, GameSentenceMiner, and Rubi-compatible tooling. Times are unix milliseconds in Tidepool tables and unix seconds in lines_seen_exstatic and game_lines.');
        CREATE TABLE IF NOT EXISTS episodes_played (
            id INTEGER PRIMARY KEY,
            show_id INTEGER NOT NULL,
            show_title TEXT NOT NULL,
            show_title_native TEXT NOT NULL,
            anilist_id INTEGER,
            episode INTEGER NOT NULL,
            started_at INTEGER NOT NULL,
            ended_at INTEGER NOT NULL,
            seconds REAL NOT NULL DEFAULT 0,
            lines INTEGER NOT NULL DEFAULT 0,
            chars INTEGER NOT NULL DEFAULT 0,
            subtitles INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS episodes_played_by_time ON episodes_played (ended_at);
        CREATE TABLE IF NOT EXISTS lines_played (
            id INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES episodes_played (id),
            played_at INTEGER NOT NULL,
            chars INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS lines_played_by_time ON lines_played (played_at);
        CREATE TABLE IF NOT EXISTS lines_seen (
            id INTEGER PRIMARY KEY,
            line_played INTEGER NOT NULL REFERENCES lines_played (id),
            session_id INTEGER NOT NULL REFERENCES episodes_played (id),
            seen_at INTEGER NOT NULL,
            text TEXT NOT NULL,
            speaker TEXT NOT NULL DEFAULT '',
            chars INTEGER NOT NULL,
            source TEXT NOT NULL DEFAULT 'hook',
            line_uid TEXT NOT NULL UNIQUE
        );
        CREATE INDEX IF NOT EXISTS lines_seen_by_time ON lines_seen (seen_at);
        CREATE TABLE IF NOT EXISTS cards_mined (
            id INTEGER PRIMARY KEY,
            note_id INTEGER NOT NULL,
            show_title TEXT NOT NULL,
            anilist_id INTEGER,
            episode INTEGER NOT NULL DEFAULT 0,
            mined_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS game_lines (
            id TEXT PRIMARY KEY,
            game_name TEXT NOT NULL,
            line_text TEXT NOT NULL,
            timestamp REAL NOT NULL,
            speaker TEXT NOT NULL DEFAULT '',
            chars INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS game_lines_by_time ON game_lines (timestamp);
        CREATE VIEW IF NOT EXISTS lines_seen_exstatic AS
            SELECT 'hachidori-ffxiv' AS uuid,
                   s.line_uid AS given_identifier,
                   p.show_title AS name,
                   s.text AS line,
                   printf('%.6f', s.seen_at / 1000.0) AS time
            FROM lines_seen s JOIN episodes_played p ON p.id = s.session_id;
        """;

    public static void AppendTidepoolSqlite(
        string sqlitePath,
        IReadOnlyList<EcosystemLine> lines,
        double activeSecondsDelta,
        IReadOnlyList<(long NoteId, string Title, long MinedAtMs)>? minedCards = null)
    {
        if (!NativeSqlite.IsAvailable)
            return;
        if (lines.Count == 0 && (minedCards is null || minedCards.Count == 0))
            return;

        using var db = NativeSqlite.OpenReadWrite(sqlitePath);
        db.Execute(TidepoolSchemaSql);
        db.Execute("BEGIN IMMEDIATE");
        try
        {
            if (lines.Count > 0)
            {
                var first = lines[0];
                var firstMs = (long)Math.Round(first.UnixSeconds * 1000.0);
                var lastMs = (long)Math.Round(lines[^1].UnixSeconds * 1000.0);
                var sessionId = FindOrCreateTidepoolSession(db, first.GameName, firstMs);

                var insertedLines = 0;
                long insertedChars = 0;
                foreach (var l in lines)
                {
                    var existing = db.Query("SELECT 1 FROM lines_seen WHERE line_uid = ?1 LIMIT 1", l.LineId);
                    if (existing.Count > 0)
                        continue;

                    var atMs = (long)Math.Round(l.UnixSeconds * 1000.0);
                    var chars = l.Characters > 0 ? l.Characters : ReadingTimeRule.CountEcosystemChars(l.Text);

                    db.Execute("INSERT INTO lines_played (session_id, played_at, chars) VALUES (?1, ?2, ?3)",
                        sessionId, atMs, chars);
                    var playedId = db.LastInsertRowId();

                    db.Execute(
                        "INSERT INTO lines_seen (line_played, session_id, seen_at, text, speaker, chars, source, line_uid) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
                        playedId, sessionId, atMs, l.Text, l.Speaker ?? string.Empty, chars, l.LineSource, l.LineId);

                    db.Execute(
                        "INSERT OR IGNORE INTO game_lines (id, game_name, line_text, timestamp, speaker, chars) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                        l.LineId, l.GameName, l.Text, l.UnixSeconds, l.Speaker ?? string.Empty, chars);

                    insertedLines++;
                    insertedChars += chars;
                }

                if (insertedLines > 0 || activeSecondsDelta > 0)
                {
                    db.Execute(
                        "UPDATE episodes_played SET ended_at = MAX(ended_at, ?2), seconds = seconds + ?3, lines = lines + ?4, chars = chars + ?5 WHERE id = ?1",
                        sessionId, lastMs, Math.Max(0.0, activeSecondsDelta), insertedLines, insertedChars);
                }
            }

            if (minedCards is not null)
            {
                foreach (var (noteId, title, minedAtMs) in minedCards)
                {
                    db.Execute(
                        "INSERT INTO cards_mined (note_id, show_title, anilist_id, episode, mined_at) VALUES (?1, ?2, NULL, 0, ?3)",
                        noteId, string.IsNullOrWhiteSpace(title) ? DefaultGameName : title, minedAtMs);
                }
            }

            db.Execute("COMMIT");
        }
        catch
        {
            try { db.Execute("ROLLBACK"); } catch { }
            throw;
        }
    }

    private static long FindOrCreateTidepoolSession(NativeSqlite db, string gameTitle, long timestampMs)
    {
        // Reuse an active session if the last line was within 30 minutes (LearnFromGames SESSION_GAP_SECONDS).
        const long sessionGapMs = 30L * 60L * 1000L;
        var rows = db.Query(
            "SELECT id, ended_at FROM episodes_played WHERE show_title = ?1 ORDER BY ended_at DESC LIMIT 1",
            gameTitle);
        if (rows.Count > 0 && rows[0][0] is long id && rows[0][1] is long endedAt && Math.Abs(timestampMs - endedAt) <= sessionGapMs)
            return id;

        db.Execute(
            "INSERT INTO episodes_played (show_id, show_title, show_title_native, anilist_id, episode, started_at, ended_at, seconds, lines, chars, subtitles) VALUES (1, ?1, 'ファイナルファンタジーXIV', NULL, 0, ?2, ?2, 0, 0, 0, 1)",
            gameTitle, timestampMs);
        return db.LastInsertRowId();
    }

    // -------------------------------------------------------------- Direct HTTP Push to GameSentenceMiner

    /// <summary>
    /// Uploads an ExStatic Lines CSV straight into a running GameSentenceMiner instance
    /// via <c>POST /api/import-exstatic</c>. GSM deduplicates by (uuid, given_identifier).
    /// </summary>
    public static async Task<(bool Ok, string Message)> PushToGsmAsync(
        string gsmBaseUrl,
        string exStaticCsvPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gsmBaseUrl))
            return (false, "GSM URL is empty.");
        if (!File.Exists(exStaticCsvPath))
            return (false, "No history CSV found yet. Read some dialogue first.");

        var url = gsmBaseUrl.Trim().TrimEnd('/') + "/api/import-exstatic";
        var bytes = await File.ReadAllBytesAsync(exStaticCsvPath, ct).ConfigureAwait(false);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(fileContent, "file", Path.GetFileName(exStaticCsvPath));

        try
        {
            using var resp = await client.PostAsync(url, form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"GSM returned {(int)resp.StatusCode}: {ExtractJsonField(body, "error") ?? body}");

            var msg = ExtractJsonField(body, "message");
            return (true, string.IsNullOrWhiteSpace(msg) ? "Synced to GameSentenceMiner." : msg);
        }
        catch (Exception ex)
        {
            return (false, $"Could not reach GSM at {url}: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- Universal Importer (CSV & SQLite)

    public static (string Format, List<EcosystemLine> Lines, List<EcosystemDailyEntry> Daily) ReadImportFile(
        string filePath,
        string? titleFilter = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Import file not found.", filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".sqlite" or ".sqlite3" or ".db")
            return ReadSqliteImport(filePath, titleFilter);

        return ReadCsvImport(filePath, titleFilter);
    }

    private static (string Format, List<EcosystemLine> Lines, List<EcosystemDailyEntry> Daily) ReadCsvImport(
        string filePath,
        string? titleFilter)
    {
        var content = File.ReadAllText(filePath, Encoding.UTF8);
        if (content.Length > 0 && content[0] == '\uFEFF')
            content = content[1..];

        var rows = ParseCsv(content);
        if (rows.Count < 2)
            throw new InvalidDataException("CSV file has no data rows.");

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows[0].Count; i++)
        {
            var h = rows[0][i].Trim();
            if (h.Length > 0 && !headers.ContainsKey(h))
                headers[h] = i;
        }

        var filter = titleFilter?.Trim() ?? string.Empty;

        // 1. ExStatic / Rubi / LearnFromGames / Telly Lines CSV
        if (headers.ContainsKey("uuid") && headers.ContainsKey("given_identifier")
            && headers.ContainsKey("name") && headers.ContainsKey("line") && headers.ContainsKey("time"))
        {
            var lines = new List<EcosystemLine>(rows.Count - 1);
            var idxUuid = headers["uuid"];
            var idxId = headers["given_identifier"];
            var idxName = headers["name"];
            var idxLine = headers["line"];
            var idxTime = headers["time"];
            headers.TryGetValue("characters", out var idxChars);
            var hasChars = headers.ContainsKey("characters");
            headers.TryGetValue("line_source", out var idxSource);
            var hasSource = headers.ContainsKey("line_source");
            headers.TryGetValue("speaker", out var idxSpeaker);
            var hasSpeaker = headers.ContainsKey("speaker");

            for (var r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var name = Cell(row, idxName).Trim();
                var text = Cell(row, idxLine).Trim();
                if (text.Length == 0)
                    continue;
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!double.TryParse(Cell(row, idxTime).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var unixSec))
                    continue;
                // Handle millisecond timestamps if an external exporter wrote ms.
                if (unixSec > 100_000_000_000.0)
                    unixSec /= 1000.0;
                if (unixSec < 0 || unixSec > 32_503_680_000.0)
                    continue;

                var uuid = Cell(row, idxUuid).Trim();
                var lineId = Cell(row, idxId).Trim();
                var chars = hasChars && int.TryParse(Cell(row, idxChars).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedChars)
                    ? parsedChars
                    : ReadingTimeRule.CountEcosystemChars(text);
                var source = hasSource ? Cell(row, idxSource).Trim() : "hook";
                var speaker = hasSpeaker ? Cell(row, idxSpeaker).Trim() : string.Empty;

                lines.Add(new EcosystemLine(
                    string.IsNullOrEmpty(uuid) ? DefaultGameUuid : uuid,
                    string.IsNullOrEmpty(lineId) ? $"{uuid}-{unixSec:F6}-{r}" : lineId,
                    string.IsNullOrEmpty(name) ? DefaultGameName : name,
                    text,
                    unixSec,
                    chars,
                    source,
                    speaker));
            }

            var isRubi = headers.ContainsKey("platform") && headers.ContainsKey("title_id");
            return (isRubi ? "Rubi History CSV" : "ExStatic / GSM Lines CSV", lines, []);
        }

        // 2. exSTATic Daily Stats CSV
        if (headers.ContainsKey("date") && headers.ContainsKey("chars_read"))
        {
            var daily = new List<EcosystemDailyEntry>();
            var idxDate = headers["date"];
            var idxChars = headers["chars_read"];
            var hasName = headers.TryGetValue("name", out var idxName);
            var hasLines = headers.TryGetValue("lines_read", out var idxLines);
            var hasTime = headers.TryGetValue("time_read", out var idxTime);

            for (var r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var dateKey = NormalizeDateKey(Cell(row, idxDate));
                if (dateKey is null)
                    continue;
                var title = hasName ? Cell(row, idxName).Trim() : DefaultGameName;
                if (filter.Length > 0 && !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                _ = double.TryParse(Cell(row, idxChars).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var charsD);
                var chars = (int)Math.Round(charsD);
                var linesCount = hasLines && int.TryParse(Cell(row, idxLines).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lc) ? lc : 0;
                var timeSec = hasTime && double.TryParse(Cell(row, idxTime).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : 0;
                if (chars <= 0 && linesCount <= 0)
                    continue;

                daily.Add(new EcosystemDailyEntry(dateKey, title, chars, linesCount, Math.Max(0, timeSec), 0, "exSTATic"));
            }

            return ("exSTATic Stats CSV", [], daily);
        }

        // 3. Kechimochi Activity Logs CSV (supports both Default Activity Type and legacy Media Type)
        if (headers.ContainsKey("Date") && headers.ContainsKey("Log Name") && headers.ContainsKey("Duration"))
        {
            var daily = new List<EcosystemDailyEntry>();
            var idxDate = headers["Date"];
            var idxName = headers["Log Name"];
            var idxDuration = headers["Duration"];
            var hasChars = headers.TryGetValue("Characters", out var idxChars);

            for (var r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var dateKey = NormalizeDateKey(Cell(row, idxDate));
                if (dateKey is null)
                    continue;
                var title = Cell(row, idxName).Trim();
                if (filter.Length > 0 && !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                _ = double.TryParse(Cell(row, idxDuration).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes);
                var chars = hasChars && int.TryParse(Cell(row, idxChars).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
                if (chars <= 0 && minutes <= 0)
                    continue;

                daily.Add(new EcosystemDailyEntry(dateKey, title, Math.Max(0, chars), Math.Max(1, chars / 25), Math.Max(0, minutes * 60.0), 0, "Kechimochi"));
            }

            return ("Kechimochi Activity CSV", [], daily);
        }

        throw new InvalidDataException(
            $"Unrecognized CSV headers ({string.Join(", ", headers.Keys.Take(8))}). " +
            "Expected ExStatic/Rubi/GSM Lines CSV, exSTATic Stats CSV, or Kechimochi Activity CSV.");
    }

    private static (string Format, List<EcosystemLine> Lines, List<EcosystemDailyEntry> Daily) ReadSqliteImport(
        string sqlitePath,
        string? titleFilter)
    {
        if (!NativeSqlite.IsAvailable)
            throw new PlatformNotSupportedException("Native SQLite (e_sqlite3) is not available on this runtime.");

        var filter = titleFilter?.Trim() ?? string.Empty;
        using var db = NativeSqlite.OpenReadOnly(sqlitePath);

        // 1. Tidepool / Telly / Hachidori SQLite (lines_seen_exstatic view or lines_seen / episodes_played)
        if (db.TableExists("lines_seen_exstatic"))
        {
            var rows = db.Query("SELECT uuid, given_identifier, name, line, time FROM lines_seen_exstatic ORDER BY CAST(time AS REAL) ASC");
            var lines = new List<EcosystemLine>(rows.Count);
            foreach (var r in rows)
            {
                var name = AsString(r[2]);
                var text = AsString(r[3]).Trim();
                if (text.Length == 0)
                    continue;
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var unixSec = AsDouble(r[4]);
                if (unixSec > 100_000_000_000.0) unixSec /= 1000.0;
                lines.Add(new EcosystemLine(AsString(r[0]), AsString(r[1]), name, text, unixSec, ReadingTimeRule.CountEcosystemChars(text), "hook"));
            }
            return ("Tidepool / Telly SQLite", lines, []);
        }

        // 2. GameSentenceMiner (gsm.db) or LearnFromGames (lfg.db) game_lines table
        if (db.TableExists("game_lines"))
        {
            var cols = db.GetColumnNames("game_lines");
            var nameCol = cols.Contains("game_name") ? "game_name" : cols.Contains("scene") ? "scene" : "''";
            var textCol = cols.Contains("line_text") ? "line_text" : cols.Contains("text") ? "text" : "''";
            var idCol = cols.Contains("id") ? "id" : "rowid";
            var timeCol = cols.Contains("timestamp") ? "timestamp" : "0";
            var gameIdCol = cols.Contains("game_id") ? "game_id" : "'gsm'";

            var rows = db.Query($"SELECT {gameIdCol}, {idCol}, {nameCol}, {textCol}, {timeCol} FROM game_lines ORDER BY {timeCol} ASC");
            var lines = new List<EcosystemLine>(rows.Count);
            foreach (var r in rows)
            {
                var name = AsString(r[2]);
                var text = AsString(r[3]).Trim();
                if (text.Length == 0)
                    continue;
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var ts = ParseFlexibleTimestamp(r[4]);
                if (ts <= 0)
                    continue;
                var gid = AsString(r[0]);
                var lid = AsString(r[1]);
                lines.Add(new EcosystemLine(
                    string.IsNullOrEmpty(gid) ? "gsm" : gid,
                    $"gsm-{lid}",
                    string.IsNullOrEmpty(name) ? DefaultGameName : name,
                    text,
                    ts,
                    ReadingTimeRule.CountEcosystemChars(text),
                    "hook"));
            }
            return ("GameSentenceMiner / LFG SQLite", lines, []);
        }

        // 3. SubMiner (immersion.sqlite) — imm_sessions + imm_videos (+ optional imm_subtitle_lines)
        if (db.TableExists("imm_sessions") && db.TableExists("imm_videos"))
        {
            var daily = new List<EcosystemDailyEntry>();
            var rows = db.Query("""
                SELECT s.started_at_ms, v.canonical_title, s.active_watched_ms, s.lines_seen, s.tokens_seen, s.cards_mined
                FROM imm_sessions s
                JOIN imm_videos v ON v.video_id = s.video_id
                ORDER BY CAST(s.started_at_ms AS INTEGER) ASC
                """);

            foreach (var r in rows)
            {
                var title = AsString(r[1]);
                if (filter.Length > 0 && !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var startMs = AsDouble(r[0]);
                if (startMs <= 0)
                    continue;
                var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)startMs).UtcDateTime;
                var dateKey = utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var activeSec = Math.Max(0, AsDouble(r[2]) / 1000.0);
                var lineCount = (int)Math.Max(0, AsDouble(r[3]));
                var tokens = (int)Math.Max(0, AsDouble(r[4]));
                var cards = (int)Math.Max(0, AsDouble(r[5]));
                // SubMiner tracks tokens/lines; approximate JP chars cleanly if no raw lines table is present.
                var approxChars = tokens > 0 ? tokens * 2 : lineCount * 16;
                daily.Add(new EcosystemDailyEntry(dateKey, title, approxChars, lineCount, activeSec, cards, "SubMiner"));
            }
            return ("SubMiner SQLite", [], daily);
        }

        // 4. Tidepool / Telly counts-only fallback (episodes_played)
        if (db.TableExists("episodes_played"))
        {
            var daily = new List<EcosystemDailyEntry>();
            var rows = db.Query("SELECT ended_at, show_title, seconds, lines, chars FROM episodes_played ORDER BY ended_at ASC");
            foreach (var r in rows)
            {
                var title = AsString(r[1]);
                if (filter.Length > 0 && !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var endedMs = AsDouble(r[0]);
                if (endedMs <= 0)
                    continue;
                var dateKey = DateTimeOffset.FromUnixTimeMilliseconds((long)endedMs).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                daily.Add(new EcosystemDailyEntry(dateKey, title, (int)AsDouble(r[4]), (int)AsDouble(r[3]), AsDouble(r[2]), 0, "Tidepool"));
            }
            return ("Tidepool / Telly SQLite", [], daily);
        }

        throw new InvalidDataException("Unrecognized SQLite schema. Supported: GSM (gsm.db), SubMiner (immersion.sqlite), Tidepool/Telly (telly-log.sqlite), and LearnFromGames (lfg.db).");
    }

    // -------------------------------------------------------------- RFC 4180 CSV parser & helpers

    public static string CsvField(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == '"' && field.Length == 0)
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r')
                {
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                        i++;
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1 || row[0].Length > 0)
                        rows.Add(row);
                    row = new List<string>();
                }
                else if (c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1 || row[0].Length > 0)
                        rows.Add(row);
                    row = new List<string>();
                }
                else
                {
                    field.Append(c);
                }
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0)
                rows.Add(row);
        }

        return rows;
    }

    private static string Cell(List<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : string.Empty;

    private static string? NormalizeDateKey(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
            return null;
        string[] formats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d"];
        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTime.TryParseExact(raw, ["yyyy-MM", "yyyy/MM"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
            return m.ToString("yyyy-MM-01", CultureInfo.InvariantCulture);
        return null;
    }

    private static double ParseFlexibleTimestamp(object? val)
    {
        if (val is long l) return l > 100_000_000_000L ? l / 1000.0 : l;
        if (val is double d) return d > 100_000_000_000.0 ? d / 1000.0 : d;
        var s = AsString(val).Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed > 100_000_000_000.0 ? parsed / 1000.0 : parsed;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUnixTimeMilliseconds() / 1000.0;
        return 0;
    }

    private static string AsString(object? val) => val switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => val.ToString() ?? string.Empty,
    };

    private static double AsDouble(object? val) => val switch
    {
        null => 0,
        double d => d,
        long l => l,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => 0,
    };

    private static string? ExtractJsonField(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }
}
