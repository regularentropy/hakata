using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

public interface ITranslationProvider
{
    IAsyncEnumerable<string> TranslateAsync(string text, string from, string to,
        TranslationOptions options, QualityTier tier, CancellationToken ct, Action<TokenUsage>? onUsage = null,
        Action<bool>? onCut = null);

    Task<string> DetectAsync(string text, CancellationToken ct, Action<TokenUsage>? onUsage = null);
    Task<(string Description, List<Alternative> Items)> AlternativesAsync(string text, string existing, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null);
    Task<List<WordInsight>> InsightsAsync(string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null);

    /// <summary>Latin-letter spellings of both sides; empty side means it needs none.</summary>
    Task<(string Source, string Translation)> TransliterationAsync(string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null);
}

public interface ISpeechProvider
{
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, Action<double>? onCost = null);
    Task<(byte[] Audio, string MimeType, double Cost)> SpeakAsync(string text, CancellationToken ct);
}

public interface IModelCatalog
{
    Task<List<ModelListing>> ListModelCatalogAsync(ProviderRoute route, CancellationToken ct = default);
}

/// <summary>Streams from any OpenAI-compatible chat completions endpoint.
/// The model catalog lives in <see cref="CatalogApi"/>, the speech routes in the speech half of the same class.</summary>
public sealed partial class OpenAiCompatibleProvider(HttpClient http, AppSettings settings) : ITranslationProvider, ISpeechProvider, IModelCatalog
{
    /// <summary>Continuations one translation may ask for before the text is taken as it stands.</summary>
    const int Continuations = 3;

    /// <summary>Characters one translation may collect before the stream is taken as cut: a hostile or
    /// looping endpoint then costs a request, not the process.</summary>
    const int MaxTranslation = 2_000_000;

    /// <summary>Per-read idle deadline on the SSE stream (default <see cref="ProviderHttp.StreamIdle"/>):
    /// a provider that stops sending mid-stream is cut after this long instead of hanging the run.
    /// Settable so tests can shorten it.</summary>
    internal TimeSpan StreamIdleTimeout { get; set; } = ProviderHttp.StreamIdle;

    public async IAsyncEnumerable<string> TranslateAsync(string text, string from, string to,
        TranslationOptions options, QualityTier tier, [EnumeratorCancellation] CancellationToken ct,
        Action<TokenUsage>? onUsage = null, Action<bool>? onCut = null)
    {
        // the tier decides which provider and model answer; the fence is a per-run nonce so a source
        // text that itself carries the closing tag cannot close the fence and speak as instructions
        var fence = NewFence();
        var system = Prompts.TranslateSystem(from, to, options, tier, fence);
        var route = settings.Route(tier);
        var usage = SumUsage(onUsage);
        var soFar = new StringBuilder();
        var cut = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var before = soFar.Length;
                cut = false;
                var user = attempt == 0
                    ? Prompts.SourceRecord(text, to, fence)
                    : Prompts.ContinuationUser(text, soFar.ToString(), to, fence);
                await foreach (var delta in StreamAsync(route, system, user, 0.2, ct, usage, c => cut = c))
                {
                    soFar.Append(delta);
                    if (soFar.Length > MaxTranslation) { cut = true; yield break; }
                    yield return delta;
                }
                // a stream the provider ended mid-sentence (dropped connection, exhausted output budget) is
                // asked for the rest; a continuation that adds nothing means there was nothing left to add
                if (!cut || attempt == Continuations || soFar.Length == before) yield break;
            }
        }
        finally
        {
            // exactly one signal per run, whatever way it ends: was the last stream cut short?
            onCut?.Invoke(cut);
        }
    }

    /// <summary>A per-run fence nonce: a source text that itself carries the closing tag cannot close
    /// the fence and speak as instructions.</summary>
    static string NewFence() => "source_text-" + Guid.NewGuid().ToString("n")[..6];

    /// <summary>Adds up the usage of every request of one run: a continuation is billed on its own.</summary>
    static Action<TokenUsage>? SumUsage(Action<TokenUsage>? onUsage)
    {
        if (onUsage is null) return null;
        var total = new TokenUsage(0, 0, false);
        return u => onUsage(total = new TokenUsage(total.PromptTokens + u.PromptTokens,
            total.CompletionTokens + u.CompletionTokens, total.Estimated || u.Estimated,
            total.ReportedCost is null && u.ReportedCost is null
                ? null : (total.ReportedCost ?? 0) + (u.ReportedCost ?? 0)));
    }

    /// <summary>Detection is a one-line job ahead of the run: it stays on the cheap Basic route.</summary>
    public async Task<string> DetectAsync(string text, CancellationToken ct, Action<TokenUsage>? onUsage = null)
    {
        var fence = NewFence();
        var raw = await CompleteAsync(settings.Route(QualityTier.Basic), Prompts.DetectSystem(fence),
            Prompts.FencedUserText(Prompts.UserText(text), fence), 0, ct, onUsage);
        if (Jsons.TryGet(raw, "iso") is { Length: > 0 } iso) return iso;
        return "auto";
    }

    /// <summary>Alternatives for the translation on screen, so they come from the tier that produced it.</summary>
    public async Task<(string Description, List<Alternative> Items)> AlternativesAsync(
        string text, string existing, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null)
    {
        var fence = NewFence();
        var raw = await CompleteAsync(settings.Route(settings.Tier),
            Prompts.AlternativesSystem(from, to, settings.Options.AlternativesCount, fence),
            Prompts.FencedUserText(Prompts.UserText(text, existing), fence), 0.7, ct, onUsage);
        if (Jsons.TryParse(raw) is not { } root) return ("", []);
        var items = new List<Alternative>();
        foreach (var el in Jsons.Elements(root, "alternatives"))
        {
            var alt = new Alternative
            {
                Text = Jsons.Str(el, "text"),
                Explanation = Jsons.Str(el, "explanation"),
            };
            if (alt.Text.Length > 0) items.Add(alt);
        }
        return (Jsons.Str(root, "original_description"), items);
    }

    /// <summary>Insights read the translation on screen, on the same route that wrote it.</summary>
    public async Task<List<WordInsight>> InsightsAsync(string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null)
    {
        var fence = NewFence();
        var raw = await CompleteAsync(settings.Route(settings.Tier), Prompts.InsightsSystem(from, to, fence),
            Prompts.FencedUserText(Prompts.UserText(text, translation), fence), 0.6, ct, onUsage);
        if (Jsons.TryParse(raw) is not { } root) return [];
        var list = new List<WordInsight>();
        foreach (var el in Jsons.Elements(root, "insights"))
        {
            var insight = new WordInsight { Word = Jsons.Str(el, "word"), Kind = Jsons.Str(el, "kind") };
            foreach (var a in Jsons.Elements(el, "alternatives"))
                insight.Alternatives.Add(new Alternative { Text = Jsons.Str(a, "text"), Explanation = Jsons.Str(a, "explanation") });
            if (insight.Word.Length > 0 && insight.Alternatives.Count > 0) list.Add(insight);
        }
        return list;
    }

    public async Task<(string Source, string Translation)> TransliterationAsync(
        string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null)
    {
        // Latin text has nothing to romanise: skip the round trip entirely
        if (!TextRules.NeedsRomanisation(text) && !TextRules.NeedsRomanisation(translation)) return ("", "");
        var fence = NewFence();
        var raw = await CompleteAsync(settings.Route(QualityTier.Basic), Prompts.TransliterationSystem(from, to, fence),
            Prompts.FencedUserText(Prompts.UserText(text, translation), fence), 0, ct, onUsage);
        if (Jsons.TryParse(raw) is not { } root) return ("", "");
        return (Jsons.Str(root, "source"), Jsons.Str(root, "translation"));
    }

    // ---- transport -------------------------------------------------------

    async IAsyncEnumerable<string> StreamAsync(ProviderRoute route, string system, string user, double temperature,
        [EnumeratorCancellation] CancellationToken ct, Action<TokenUsage>? onUsage = null, Action<bool>? onCut = null)
    {
        using var req = Request(route, system, user, temperature, stream: true);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ProviderHttp.EnsureOk(res, ct);

        var finish = "";
        var done = false;
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await foreach (var maybe in ProviderHttp.ReadLines(reader, ProviderHttp.MaxSseLine, ct, StreamIdleTimeout))
        {
            // an overlong line means a broken or hostile endpoint: the stream is taken as cut
            if (maybe is not { } line) { onCut?.Invoke(true); yield break; }
            // the last chunk carries the billing counters, but can also carry text — parse both
            if (SseParser.ParseSseUsage(line) is { } usage) onUsage?.Invoke(usage);
            var data = line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : "";
            if (data == "[DONE]") { done = true; break; } // [DONE] is terminal: what follows is not part of the answer
            var delta = SseParser.ParseSseDelta(line);
            if (delta is { Length: > 0 }) yield return delta;
            var reason = SseParser.ParseSseFinish(line);
            if (reason.Length > 0) finish = reason;
        }
        onCut?.Invoke(SseParser.StreamWasCut(done, finish));
    }

    async Task<string> CompleteAsync(ProviderRoute route, string system, string user, double temperature,
        CancellationToken ct, Action<TokenUsage>? onUsage = null)
    {
        using var req = Request(route, system, user, temperature, stream: false);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ProviderHttp.EnsureOk(res, ct);
        var body = await ProviderHttp.ReadTextCappedAsync(res.Content, ProviderHttp.MaxBody, ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            // a body that is no JSON at all is an error, not the translation text
            throw new HttpRequestException(ProviderHttp.Describe(body));
        }
        using (doc)
        {
            var root = doc.RootElement;
            // a 200 body that parses but carries no answer (an {"error":{...}} envelope) is an error
            // like any other failure, never the raw body shown and billed as the translation
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var raw) || raw.ValueKind != JsonValueKind.String)
                throw new HttpRequestException(ProviderHttp.Describe(body));
            var content = TextRules.StripHidden(raw.GetString() ?? "");
            // the billing counters ride along when the provider sends them; otherwise estimate, like a stream
            if (onUsage is not null &&
                root.TryGetProperty("usage", out var u) &&
                u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var prompt) &&
                u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var completion))
            {
                // the provider's own bill, when it states one beside the counters
                double? cost = u.TryGetProperty("cost", out var billed) &&
                               billed.ValueKind == JsonValueKind.Number &&
                               billed.TryGetDouble(out var amount) && amount > 0 ? amount : null;
                onUsage(new TokenUsage(prompt, completion, Estimated: false, ReportedCost: cost));
            }
            // the estimate covers the system prompt too: it is sent and billed like the user text
            else onUsage?.Invoke(TokenUsage.EstimatedFor(system + "\n" + user, content));
            return content;
        }
    }

    HttpRequestMessage Request(ProviderRoute route, string system, string user, double temperature, bool stream)
    {
        // reasoning models reject a custom temperature, so an effort request leaves it at the provider default
        var wantsEffort = route.Effort.Length > 0;
        var payload = new
        {
            model = route.Model,
            stream,
            temperature = wantsEffort ? (double?)null : temperature,
            reasoning_effort = wantsEffort ? route.Effort : null,
            // ask for the final usage chunk; servers that do not know the knob ignore it
            stream_options = stream ? new { include_usage = true } : null,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, ProviderUrls.Endpoint(route.BaseUrl));
        ProviderHttp.WithAuth(req, route.ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, ProviderHttp.JsonOpts), Encoding.UTF8, "application/json");
        return req;
    }
}
