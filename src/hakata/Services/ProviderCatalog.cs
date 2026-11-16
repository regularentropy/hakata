using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>One row of a provider's /models listing: prices per 1M tokens, reasoning efforts and, for a
/// speech model, the voices and output formats it publishes.</summary>
public sealed record ModelListing(string Id, double? PromptPerMTok, double? CompletionPerMTok,
    List<string>? Efforts = null, string? DefaultEffort = null,
    List<string>? Voices = null, string? DefaultVoice = null, List<string>? Formats = null,
    double? PricePerMChars = null, double? PricePerMinute = null,
    List<string>? Parameters = null);

/// <summary>The catalog half of <see cref="OpenAiCompatibleProvider"/>: the /models listing
/// with the prices and reasoning efforts some providers publish.</summary>
public static class CatalogApi
{
    /// <summary>Model ids from the OpenAI-compatible /models listing, sorted. Throws HttpRequestException on bad status.</summary>
    public static async Task<List<string>> ListModelsAsync(HttpClient http, string baseUrl, string? apiKey, CancellationToken ct) =>
        (await ListModelCatalogAsync(http, baseUrl, apiKey, ct)).Select(m => m.Id).ToList();

    /// <summary>The published per-1M price pair for a model id, rounded for the settings fields; null when absent.</summary>
    public static (double Input, double Output)? PublishedPriceFor(IEnumerable<ModelListing> catalog, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var listing = catalog.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (listing?.PromptPerMTok is not { } prompt || listing.CompletionPerMTok is not { } completion) return null;
        return (Math.Round(prompt, 4), Math.Round(completion, 4));
    }

    /// <summary>The shared GET of every catalog route: auth header, status check, parse.</summary>
    static async Task<List<ModelListing>> GetCatalogAsync(HttpClient http, string url, string? apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ProviderHttp.WithAuth(req, apiKey);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ProviderHttp.EnsureOk(res, ct);
        return ParseCatalog(await ProviderHttp.ReadTextCappedAsync(res.Content, ProviderHttp.MaxBody, ct));
    }

    /// <summary>
    /// The /models listing with the per-token prices some providers (OpenRouter and friends) publish.
    /// Prices come back per 1M tokens; null means the provider said nothing about that model.
    /// </summary>
    public static Task<List<ModelListing>> ListModelCatalogAsync(HttpClient http, string baseUrl, string? apiKey, CancellationToken ct) =>
        GetCatalogAsync(http, ProviderUrls.ModelsUrl(baseUrl), apiKey, ct);

    /// <summary>Ids and prices out of a /models-shaped body: a bare array, or an object holding one.</summary>
    public static List<ModelListing> ParseCatalog(string body)
    {
        var found = new Dictionary<string, ModelListing>(StringComparer.OrdinalIgnoreCase);
        if (Jsons.TryParse(body) is { } el)
        {
            var arr = el.ValueKind == JsonValueKind.Array ? el
                : Jsons.TryProp(el, "data", out var d) && d.ValueKind == JsonValueKind.Array ? d
                : Jsons.TryProp(el, "models", out var m) && m.ValueKind == JsonValueKind.Array ? m
                : default;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String ? item.GetString() : Jsons.Str(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var (prompt, completion) = Prices(item);
                    var listing = new ModelListing(id, prompt, completion, Efforts(item), DefaultEffort(item),
                        VoiceList(item, "voices"), TtsName(item, "default_voice"), VoiceList(item, "formats"),
                        FlatPrice(item, "price_per_m_chars"), FlatPrice(item, "price_per_minute"), Parameters(item));
                    // a duplicate id can be poorer than the first: keep whatever either one knew
                    found[id] = found.TryGetValue(id, out var seen) ? Merge(seen, listing) : listing;
                }
        }
        return found.Values.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static (double? Prompt, double? Completion) Prices(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            return (null, null);
        return (PerMillion(pricing, "prompt"), PerMillion(pricing, "completion"));
    }

    /// <summary>Fills the gaps of a duplicate listing entry: later duplicates rarely repeat what the first said.</summary>
    static ModelListing Merge(ModelListing seen, ModelListing fresh) => seen with
    {
        PromptPerMTok = seen.PromptPerMTok ?? fresh.PromptPerMTok,
        CompletionPerMTok = seen.CompletionPerMTok ?? fresh.CompletionPerMTok,
        Efforts = seen.Efforts is { Count: > 0 } ? seen.Efforts : fresh.Efforts,
        DefaultEffort = seen.DefaultEffort ?? fresh.DefaultEffort,
        Voices = seen.Voices is { Count: > 0 } ? seen.Voices : fresh.Voices,
        DefaultVoice = seen.DefaultVoice ?? fresh.DefaultVoice,
        Formats = seen.Formats is { Count: > 0 } ? seen.Formats : fresh.Formats,
        PricePerMChars = seen.PricePerMChars ?? fresh.PricePerMChars,
        PricePerMinute = seen.PricePerMinute ?? fresh.PricePerMinute,
        Parameters = seen.Parameters is { Count: > 0 } ? seen.Parameters : fresh.Parameters,
    };

    /// <summary>A flat billing rate published beside the token prices, e.g. `pricing.price_per_m_chars`.</summary>
    static double? FlatPrice(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("pricing", out var pricing) &&
        pricing.ValueKind == JsonValueKind.Object && pricing.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var amount) && amount > 0
            ? amount
            : null;

    /// <summary>The speech block of an entry (`params.tts`): a provider that serves TTS publishes voices,
    /// a default voice and the formats it can return. Providers that publish none stay free text.</summary>
    static JsonElement? TtsBlock(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("tts", out var tts) || tts.ValueKind != JsonValueKind.Object)
            return null;
        return tts;
    }

    static string? TtsName(JsonElement item, string name) =>
        TtsBlock(item) is { } block && block.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Maps each element of a JSON array to a string, keeping the first occurrence of every
    /// non-blank value in order; an empty list when the value is not an array.</summary>
    static List<string> CollectDistinct(JsonElement values, Func<JsonElement, string?> map)
    {
        var found = new List<string>();
        if (values.ValueKind != JsonValueKind.Array) return found;
        foreach (var value in values.EnumerateArray())
        {
            var id = map(value);
            if (!string.IsNullOrWhiteSpace(id) && !found.Contains(id)) found.Add(id);
        }
        return found;
    }

    /// <summary>A string list from the speech block; a voice may be a plain id or an object naming one.</summary>
    static List<string>? VoiceList(JsonElement item, string name)
    {
        if (TtsBlock(item) is not { } block || !block.TryGetProperty(name, out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return null;
        return CollectDistinct(values, value => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => NamedVoice(value),
            _ => null,
        });
    }

    static string NamedVoice(JsonElement voice)
    {
        var id = Jsons.Str(voice, "id");
        return id.Length > 0 ? id : Jsons.Str(voice, "name");
    }

    /// <summary>
    /// Effort names a model accepts: `supported` inside the reasoning object, or OpenRouter's
    /// `supported_efforts` at its top; every other provider just says nothing.
    /// </summary>
    static List<string> Efforts(JsonElement item)
    {
        var efforts = new List<string>();
        foreach (var block in ReasoningBlocks(item))
        {
            foreach (var name in new[] { "supported", "supported_efforts" })
            {
                if (!block.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) continue;
                foreach (var effort in CollectDistinct(values, value => value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                }))
                    if (!efforts.Contains(effort)) efforts.Add(effort);
            }
        }
        return efforts;
    }

    /// <summary>
    /// The parameter names the model accepts (OpenRouter publishes them). Empty means the provider said
    /// nothing, which is not the same as "no parameters".
    /// </summary>
    static List<string>? Parameters(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("supported_parameters", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;
        return CollectDistinct(values, value => value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    static string? DefaultEffort(JsonElement item)
    {
        foreach (var block in ReasoningBlocks(item))
            foreach (var name in new[] { "default", "default_effort" })
                if (block.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
        return null;
    }

    /// <summary>The reasoning object of an entry: providers that namespace their metadata nest it one level
    /// down, every other provider keeps it at the top.</summary>
    static IEnumerable<JsonElement> ReasoningBlocks(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) yield break;
        if (item.TryGetProperty("reasoning", out var top) && top.ValueKind == JsonValueKind.Object) yield return top;
        // a vendor namespace is any object property holding a reasoning object of its own
        foreach (var property in item.EnumerateObject())
            if (property.Name != "reasoning" && property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("reasoning", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
                yield return nested;
    }

    static double? PerMillion(JsonElement pricing, string name)
    {
        if (!pricing.TryGetProperty(name, out var value)) return null;
        var perToken = value.ValueKind switch
        {
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            _ => double.NaN,
        };
        return double.IsNaN(perToken) || perToken <= 0 ? null : perToken * 1_000_000;
    }

    /// <summary>The speech-to-text entries of the audio catalog; throws HttpRequestException when the provider has none.</summary>
    public static Task<List<ModelListing>> ListAudioModelsAsync(HttpClient http, string baseUrl, string? apiKey, CancellationToken ct,
        string? type = null) =>
        GetCatalogAsync(http, ProviderUrls.AudioModelsUrl(baseUrl, type), apiKey, ct);
}

/// <summary>The speech routes of <see cref="OpenAiCompatibleProvider"/>: audio URLs, the WAV
/// upload with its base64 fallback, and the audio model catalog live in <see cref="CatalogApi"/>.</summary>
public sealed partial class OpenAiCompatibleProvider
{
    /// <summary>The catalog of the provider one route sends to.</summary>
    public Task<List<ModelListing>> ListModelCatalogAsync(ProviderRoute route, CancellationToken ct = default) =>
        CatalogApi.ListModelCatalogAsync(http, route.BaseUrl, route.ApiKey, ct);
}
