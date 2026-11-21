using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Media;

/// <summary>Sentence translation backends: GSM's providers plus local Codex / Claude Code CLIs.</summary>
public enum TranslationProvider
{
    Gemini,
    Groq,
    OpenAI,
    Ollama,
    LmStudio,
    Zai,
    DeepL,
    Codex,
    ClaudeCode,
}

/// <summary>One provider's settings, named as in GameSentenceMiner.</summary>
[Serializable]
public sealed class AiProviderSettings
{
    public string Model { get; set; } = string.Empty;
    public string BackupModel { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ReasoningEffort { get; set; } = string.Empty;
}

/// <summary>Translates a mined sentence with the configured provider, trying the backup model on failure.</summary>
public sealed class Translator : IDisposable
{
    private readonly Configuration config;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public Translator(Configuration config) => this.config = config;

    public static readonly IReadOnlyDictionary<TranslationProvider, (string Model, string Url)> Defaults = new Dictionary<TranslationProvider, (string, string)>
    {
        [TranslationProvider.Gemini] = ("gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta"),
        [TranslationProvider.Groq] = ("meta-llama/llama-4-scout-17b-16e-instruct", "https://api.groq.com/openai/v1"),
        [TranslationProvider.OpenAI] = ("gpt-4.1-mini", "https://api.openai.com/v1"),
        [TranslationProvider.Ollama] = ("gemma3", "http://localhost:11434"),
        [TranslationProvider.LmStudio] = (string.Empty, "http://localhost:1234/v1"),
        [TranslationProvider.Zai] = ("glm-4.5-flash", "https://api.z.ai/api/paas/v4"),
        [TranslationProvider.DeepL] = (string.Empty, "https://api-free.deepl.com/v2"),
        [TranslationProvider.Codex] = (string.Empty, string.Empty),
        [TranslationProvider.ClaudeCode] = ("haiku", string.Empty),
    };

    public AiProviderSettings Settings(TranslationProvider provider)
    {
        if (!config.AiProviders.TryGetValue(provider, out var settings))
        {
            var (model, url) = Defaults[provider];
            config.AiProviders[provider] = settings = new AiProviderSettings { Model = model, Url = url };
        }

        return settings;
    }

    public async Task<string> TranslateAsync(string sentence, IReadOnlyList<string> context, string speaker, string gameTitle, CancellationToken ct = default)
    {
        var provider = config.TranslationProvider;
        var settings = Settings(provider);
        if (provider == TranslationProvider.DeepL)
            return await DeepLAsync(sentence, settings, ct).ConfigureAwait(false);

        var prompt = TranslationPrompt.Build(config.TranslationLanguage, config.UseCannedTranslationPrompt, config.UseCannedContextPrompt,
            config.CustomTranslationPrompt, config.CustomFullPrompt, config.DialogueContextLength, sentence, context, speaker, gameTitle);
        try
        {
            return Clean(await AskAsync(provider, settings, settings.Model, prompt, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && settings.BackupModel.Trim().Length > 0)
        {
            return Clean(await AskAsync(provider, settings, settings.BackupModel.Trim(), prompt, ct).ConfigureAwait(false));
        }
    }

    private static string Clean(string text)
    {
        text = text.Trim();
        // Some models wrap the answer in a code fence or quotes despite the prompt.
        if (text.StartsWith("```", StringComparison.Ordinal))
            text = Regex.Replace(text, @"^```[a-zA-Z]*\s*|\s*```$", string.Empty).Trim();
        return text;
    }

    private Task<string> AskAsync(TranslationProvider provider, AiProviderSettings s, string model, string prompt, CancellationToken ct) => provider switch
    {
        TranslationProvider.Gemini => GeminiAsync(s, model, prompt, ct),
        TranslationProvider.Codex => CodexAsync(model, prompt, ct),
        TranslationProvider.ClaudeCode => ClaudeAsync(model, prompt, ct),
        TranslationProvider.Ollama => ChatAsync(OllamaUrl(s.Url), s.ApiKey, model, s.ReasoningEffort, prompt, ct),
        _ => ChatAsync(s.Url.Trim().Length > 0 ? s.Url : Defaults[provider].Url, s.ApiKey, model, s.ReasoningEffort, prompt, ct),
    };

    private static string OllamaUrl(string url)
    {
        url = (url.Trim().Length > 0 ? url : "http://localhost:11434").TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.Ordinal) ? url : $"{url}/v1";
    }

    // ------------------------------------------------------------- HTTP APIs

    private async Task<string> GeminiAsync(AiProviderSettings s, string model, string prompt, CancellationToken ct)
    {
        var root = (s.Url.Trim().Length > 0 ? s.Url : Defaults[TranslationProvider.Gemini].Url).TrimEnd('/');
        var generation = new JsonObject
        {
            ["temperature"] = config.TranslationTemperature,
            ["topP"] = config.TranslationTopP,
            ["maxOutputTokens"] = config.TranslationMaxTokens,
        };
        // GSM's thinking settings: none for Gemini 2.5 Flash, a minimal level for Gemini 3 and Gemma 4.
        var lower = model.ToLowerInvariant();
        if (lower.StartsWith("gemini-2.5", StringComparison.Ordinal))
            generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = lower.Contains("-pro") ? -1 : 0 };
        else if (lower.StartsWith("gemini-3", StringComparison.Ordinal) || lower.StartsWith("gemma-4", StringComparison.Ordinal))
            generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = lower.Contains("flash") || lower.StartsWith("gemma") ? "minimal" : "low" };
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["generationConfig"] = generation,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{root}/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", s.ApiKey.Trim());
        var reply = await SendAsync(request, ct).ConfigureAwait(false);
        var parts = reply?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
        var text = string.Concat(parts?.OfType<JsonObject>().Where(p => p["thought"]?.GetValue<bool>() != true).Select(p => p["text"]?.ToString()) ?? []);
        return text.Length > 0 ? text : throw new InvalidOperationException("Gemini returned no text.");
    }

    /// <summary>OpenAI-compatible chat completions: OpenAI, Groq, LM Studio, Z.ai and Ollama.</summary>
    private async Task<string> ChatAsync(string url, string key, string model, string reasoning, string prompt, CancellationToken ct)
    {
        async Task<string> Once(string tokenField)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
                ["temperature"] = config.TranslationTemperature,
                ["top_p"] = config.TranslationTopP,
                [tokenField] = config.TranslationMaxTokens,
            };
            if (reasoning.Trim().Length > 0)
                body["reasoning_effort"] = reasoning.Trim();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (key.Trim().Length > 0)
                request.Headers.Add("Authorization", $"Bearer {key.Trim()}");
            var reply = await SendAsync(request, ct).ConfigureAwait(false);
            return reply?["choices"]?[0]?["message"]?["content"]?.ToString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException("The model returned no text.");
        }

        try
        {
            return await Once("max_tokens").ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("max_completion_tokens", StringComparison.Ordinal))
        {
            // Newer OpenAI models only accept max_completion_tokens (GSM retries the same way).
            return await Once("max_completion_tokens").ConfigureAwait(false);
        }
    }

    private async Task<string> DeepLAsync(string sentence, AiProviderSettings s, CancellationToken ct)
    {
        var key = s.ApiKey.Trim();
        var root = s.Url.Trim().Length > 0 ? s.Url.TrimEnd('/')
            : key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com/v2" : "https://api.deepl.com/v2";
        var target = (config.DeepLTargetLanguage.Trim().Length > 0 ? config.DeepLTargetLanguage.Trim() : "EN-US").ToUpperInvariant();
        if (target == "EN") target = "EN-US";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{root}/translate")
        {
            Content = new StringContent(new JsonObject { ["text"] = new JsonArray(sentence), ["target_lang"] = target }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"DeepL-Auth-Key {key}");
        var reply = await SendAsync(request, ct).ConfigureAwait(false);
        return reply?["translations"]?[0]?["text"]?.ToString() ?? throw new InvalidOperationException("DeepL returned no translation.");
    }

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = text.Length > 300 ? text[..300] : text;
            throw new HttpRequestException($"{(int)response.StatusCode}: {detail}");
        }

        return JsonNode.Parse(text);
    }

    // ------------------------------------------------------------ local CLIs

    /// <summary>Codex, read-only and ephemeral, in an empty folder; the prompt goes in on stdin.</summary>
    private Task<string> CodexAsync(string model, string prompt, CancellationToken ct)
    {
        var answer = Path.Combine(Path.GetTempPath(), $"hachidori-ffxiv-{Guid.NewGuid():N}.txt");
        var args = new List<string> { "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never", "-o", answer };
        if (model.Trim().Length > 0) { args.Add("-m"); args.Add(model.Trim()); }
        args.Add("-");
        return RunCliAsync(Cli("codex", config.CodexPath), args, prompt, answer, ct);
    }

    /// <summary>Claude Code in print mode with no tools and no saved session; the prompt goes in on stdin.</summary>
    private Task<string> ClaudeAsync(string model, string prompt, CancellationToken ct)
    {
        var args = new List<string> { "-p", "--output-format", "text", "--tools", "", "--no-session-persistence", "--strict-mcp-config" };
        if (model.Trim().Length > 0) { args.Add("--model"); args.Add(model.Trim()); }
        return RunCliAsync(Cli("claude", config.ClaudeCodePath), args, prompt, null, ct);
    }

    private static string Cli(string name, string configured)
    {
        if (configured.Trim().Length > 0)
            return configured.Trim().Trim('"');
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                var path = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(path))
                    return path;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var path in new[]
                 {
                     Path.Combine(home, ".local", "bin", name + ".exe"),
                     Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", name + ".exe"),
                 })
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException($"{name} was not found. Install it or set its path in Settings → Mining.");
    }

    private static async Task<string> RunCliAsync(string exe, List<string> args, string prompt, string? answerFile, CancellationToken ct)
    {
        var workDir = Directory.CreateTempSubdirectory("hachidori-ffxiv-ai-").FullName;
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} did not start");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var err = (await errors.ConfigureAwait(false)).Trim();
                var last = err.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? stdout.Trim();
                throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} exited with {process.ExitCode}: {last.Trim()}");
            }

            var text = answerFile is not null && File.Exists(answerFile) ? await File.ReadAllTextAsync(answerFile, ct).ConfigureAwait(false) : stdout;
            return text.Trim().Length > 0 ? text : throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} returned no text.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { /* already gone */ }
            }

            if (answerFile is not null)
            {
                try { File.Delete(answerFile); } catch { /* best effort */ }
            }

            try { Directory.Delete(workDir, true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------- GSM import

    /// <summary>Copies the AI settings of GSM's active profile. Returns a summary, or throws.</summary>
    public string ImportFromGsm()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSentenceMiner", "config.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("GSM's config.json was not found.");
        var root = JsonNode.Parse(File.ReadAllText(path));
        var profile = root?["current_profile"]?.ToString() ?? "Default";
        var profileNode = root?["configs"]?[profile] ?? root?["configs"]?["Default"];
        var ai = profileNode?["ai"] as JsonObject ?? throw new InvalidDataException("GSM's config has no AI settings.");
        string S(string key) => ai[key]?.ToString() ?? string.Empty;

        void Set(TranslationProvider p, string model, string backup, string key, string url = "", string reasoning = "")
        {
            var s = Settings(p);
            if (model.Length > 0) s.Model = model;
            s.BackupModel = backup;
            if (key.Length > 0) s.ApiKey = key;
            if (url.Length > 0) s.Url = url;
            s.ReasoningEffort = reasoning;
        }

        Set(TranslationProvider.Gemini, S("gemini_model"), S("gemini_backup_model"), S("gemini_api_key").Length > 0 ? S("gemini_api_key") : S("api_key"));
        Set(TranslationProvider.Groq, S("groq_model"), S("groq_backup_model"), S("groq_api_key"));
        Set(TranslationProvider.OpenAI, S("open_ai_model"), S("open_ai_backup_model"), S("open_ai_api_key"), S("open_ai_url"), S("open_ai_reasoning_effort"));
        Set(TranslationProvider.Ollama, S("ollama_model"), S("ollama_backup_model"), string.Empty, S("ollama_url"));
        Set(TranslationProvider.LmStudio, S("lm_studio_model"), S("lm_studio_backup_model"), S("lm_studio_api_key"), S("lm_studio_url"));
        Set(TranslationProvider.Zai, S("zai_model"), S("zai_backup_model"), S("zai_api_key"));
        Set(TranslationProvider.DeepL, string.Empty, string.Empty, S("deepl_api_key"));

        var provider = S("provider").Replace(" ", string.Empty).ToLowerInvariant() switch
        {
            "gemini" => TranslationProvider.Gemini,
            "groq" => TranslationProvider.Groq,
            "openai" => TranslationProvider.OpenAI,
            "ollama" => TranslationProvider.Ollama,
            "lmstudio" => TranslationProvider.LmStudio,
            "zai" or "z.ai" => TranslationProvider.Zai,
            "deepl" => TranslationProvider.DeepL,
            _ => config.TranslationProvider,
        };
        config.TranslationProvider = provider;
        if (S("anki_field").Length > 0) config.TranslationField = S("anki_field");
        if (S("deepl_target_lang").Length > 0) config.DeepLTargetLanguage = S("deepl_target_lang");
        config.UseCannedTranslationPrompt = ai["use_canned_translation_prompt"]?.GetValue<bool>() ?? true;
        config.UseCannedContextPrompt = ai["use_canned_context_prompt"]?.GetValue<bool>() ?? false;
        config.CustomTranslationPrompt = S("custom_prompt");
        config.CustomFullPrompt = S("custom_full_prompt");
        if (ai["dialogue_context_length"] is JsonValue length && length.TryGetValue<int>(out var lines)) config.DialogueContextLength = lines;
        if (ai["temperature"] is JsonValue t && t.TryGetValue<double>(out var temperature)) config.TranslationTemperature = temperature;
        if (ai["top_p"] is JsonValue p && p.TryGetValue<double>(out var topP)) config.TranslationTopP = topP;
        if (ai["max_output_tokens"] is JsonValue m && m.TryGetValue<int>(out var max)) config.TranslationMaxTokens = max;
        var native = profileNode?["general"]?["native_language"]?.ToString();
        if (native is { Length: > 0 }) config.TranslationLanguage = LanguageName(native);
        return $"Imported GSM's AI settings ({S("provider")}, field “{config.TranslationField}”).";
    }

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "en" => "English", "es" => "Spanish", "fr" => "French", "de" => "German", "pt" => "Portuguese", "it" => "Italian",
        "ru" => "Russian", "zh" => "Chinese", "ko" => "Korean", "ja" => "Japanese", "vi" => "Vietnamese", "id" => "Indonesian",
        _ => code,
    };

    public void Dispose() => http.Dispose();
}
