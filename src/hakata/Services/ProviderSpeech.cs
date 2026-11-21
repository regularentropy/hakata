using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>The speech half of <see cref="OpenAiCompatibleProvider"/>: the WAV upload with its
/// base64 fallback and the speech render. The audio URLs live in <see cref="ProviderUrls"/>,
/// the audio model catalog in <see cref="CatalogApi"/>.</summary>
public sealed partial class OpenAiCompatibleProvider
{
    // fetched once per provider for the prices they publish; a provider without one answers 404 and that failed fetch is
    // dropped again, so the next speech call asks once more
    readonly ConcurrentDictionary<string, Task<List<ModelListing>?>> catalogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One rendered clip past this is refused: a file link is not a place to stream bytes into memory.</summary>
    internal const int MaxSpeechBytes = 32_000_000;

    /// <summary>Uploads one WAV recording and returns the transcript text.</summary>
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, Action<double>? onCost = null)
    {
        var route = settings.SpeechRoute;
        // a chat-only surface has no transcription route at all, so the user can name the endpoint instead
        var url = string.IsNullOrWhiteSpace(settings.SpeechUrl) ? ProviderUrls.AudioUrl(route.BaseUrl) : ProviderUrls.OverrideUrl(settings.SpeechUrl);
        var model = settings.EffectiveSpeechModel;

        double reported;
        string text;
        using (var upload = await PostAsync(url, WavForm(wav, model), route.ApiKey, ct))
        {
            // 400/404/415: the route is there but wants base64 audio in a JSON body instead of a file part
            // (some surfaces take {audio: data-uri, model}, OpenRouter {input_audio}); OpenAI, Groq and local
            // servers take the multipart form and answer 200 straight away
            if (upload.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnsupportedMediaType)
            {
                using var json = await PostAsync(url, WavJson(wav, model), route.ApiKey, ct);
                (text, reported) = await TranscriptAsync(json, ct);
            }
            else
            {
                (text, reported) = await TranscriptAsync(upload, ct);
            }
        }

        // the provider's own number wins; then its published per-minute rate; then the manual price
        // the user typed for providers that publish nothing
        var perMinute = (await ListingAsync(route, model, tts: false, ct))?.PricePerMinute
                        ?? (settings.SpeechPricePerMinute > 0 ? settings.SpeechPricePerMinute : null);
        onCost?.Invoke(reported > 0 ? reported : Pricing.PerMinutes(Wav.Seconds(wav), perMinute) ?? 0);
        return text;
    }

    async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        ProviderHttp.WithAuth(req, apiKey);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    static HttpContent WavForm(byte[] wav, string model)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent(model), "model");
        return form;
    }

    static HttpContent WavJson(byte[] wav, string model)
    {
        var payload = new { model, audio = "data:audio/wav;base64," + Convert.ToBase64String(wav) };
        return new StringContent(JsonSerializer.Serialize(payload, ProviderHttp.JsonOpts), Encoding.UTF8, "application/json");
    }

    static async Task<(string Text, double ReportedCost)> TranscriptAsync(HttpResponseMessage res, CancellationToken ct)
    {
        await ProviderHttp.EnsureOk(res, ct);
        var body = await ProviderHttp.ReadTextCappedAsync(res.Content, ProviderHttp.MaxBody, ct);
        var root = Jsons.TryParse(body);
        // an OpenAI-compatible endpoint answers {"text": "…"}; a raw body is the transcript itself
        var transcript = root is { ValueKind: JsonValueKind.Object } parsed
                         && parsed.TryGetProperty("text", out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
        var text = (transcript ?? body).Trim();
        // some providers bill the call and say so (`usage` carrying `cost` and `seconds`)
        var cost = root is { ValueKind: JsonValueKind.Object } billed &&
                   billed.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                   usage.TryGetProperty("cost", out var reported) && reported.TryGetDouble(out var amount)
            ? amount
            : 0;
        return (text, cost);
    }

    /// <summary>The catalog entry for a speech model; the published prices live there. The fetch is cached per provider,
    /// and a failed one is dropped so the next call retries: concurrent callers normally share one task.</summary>
    Task<ModelListing?> ListingAsync(ProviderRoute route, string model, bool tts, CancellationToken ct)
    {
        var key = (tts ? "tts|" : "stt|") + route.BaseUrl + "|" + route.ApiKey;
        return Fetch();

        async Task<ModelListing?> Fetch()
        {
            // the cached task must not capture any one caller's token: one cancel would hand every
            // concurrent waiter a null ("free"); the fetch runs on its own token, each caller
            // cancels only its own wait
            var task = catalogs.GetOrAdd(key, _ => FetchCatalog(route, tts, CancellationToken.None));
            List<ModelListing>? list;
            try { list = await task.WaitAsync(ct); }
            catch (OperationCanceledException) { return null; }
            // a failed or cancelled fetch must not cache its null forever, so drop the entry for a retry (#0057);
            // done after the await because GetOrAdd inserts only once the factory returns
            if (list is null) catalogs.TryRemove(key, out _);
            return list?.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        }
    }

    async Task<List<ModelListing>?> FetchCatalog(ProviderRoute route, bool tts, CancellationToken ct)
    {
        try
        {
            return await CatalogApi.ListAudioModelsAsync(http, route.BaseUrl, route.ApiKey, ct, tts ? "tts" : null);
        }
        catch (Exception)
        {
            // no catalog (yet) means no published price, which the caller reads as "this one is free to us"
            return null;
        }
    }

    /// <summary>Renders text to speech. The compat route answers raw MP3; the native route beside it answers a
    /// JSON envelope carrying base64 audio (or a URL to fetch it), so both shapes are unwrapped here.</summary>
    public async Task<(byte[] Audio, string MimeType, double Cost)> SpeakAsync(string text, CancellationToken ct)
    {
        var route = settings.SoundRoute;
        // a chat-only surface has no speech route at all, so the user can name the endpoint instead
        var url = string.IsNullOrWhiteSpace(settings.SoundUrl) ? ProviderUrls.SpeakUrl(route.BaseUrl) : ProviderUrls.OverrideUrl(settings.SoundUrl);
        // no response_format on purpose: the provider then answers in a format its model supports, and the
        // response says which one (a wav-only model asked for mp3 just fails)
        var payload = new
        {
            model = settings.EffectiveSoundModel,
            input = text,
            voice = settings.EffectiveSoundVoice,
        };
        using var res = await PostAsync(url, new StringContent(JsonSerializer.Serialize(payload, ProviderHttp.JsonOpts), Encoding.UTF8, "application/json"), route.ApiKey, ct);
        await ProviderHttp.EnsureOk(res, ct);
        var mime = res.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        var body = await ProviderHttp.ReadBytesCappedAsync(res.Content, MaxSpeechBytes, ct);
        if (mime is not ("application/json" or "text/plain"))
            return (body, mime, await SynthesisCostAsync(route, text, reported: 0, ct));

        var json = Encoding.UTF8.GetString(body);
        var envelope = await EnvelopeAsync(json, ct) ?? throw new HttpRequestException(ProviderHttp.Describe(json));
        return (envelope.Audio, envelope.MimeType, await SynthesisCostAsync(route, text, envelope.ReportedCost, ct));
    }

    /// <summary>The provider's own price for the render, else its published per-character rate,
    /// else the manual price from settings.</summary>
    async Task<double> SynthesisCostAsync(ProviderRoute route, string text, double reported, CancellationToken ct)
    {
        var perMChars = (await ListingAsync(route, settings.EffectiveSoundModel, tts: true, ct))?.PricePerMChars
                        ?? (settings.SoundPricePerMChars > 0 ? settings.SoundPricePerMChars : null);
        return reported > 0 ? reported : Pricing.PerChars(text.Length, perMChars) ?? 0;
    }

    /// <summary>The audio inside a JSON speech envelope, or null when the body is not one.</summary>
    async Task<(byte[] Audio, string MimeType, double ReportedCost)?> EnvelopeAsync(string json, CancellationToken ct)
    {
        if (Jsons.TryParse(json) is not { ValueKind: JsonValueKind.Object } root) return null;
        var audio = root.TryGetProperty("audio", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var mime = Jsons.Str(audio, "mime_type");
        if (mime.Length == 0) mime = "audio/mpeg";

        var cost = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                   usage.TryGetProperty("cost", out var billed) && billed.TryGetDouble(out var amount)
            ? amount
            : 0;

        var base64 = Jsons.Str(audio, "b64_json");
        if (base64.Length > 0)
        {
            // refused before decoding: the allocation happens inside Convert, not after a length check
            if (base64.Length > MaxSpeechBytes * 4 / 3 + 4) throw ProviderHttp.TooBig(MaxSpeechBytes);
            try { return (Convert.FromBase64String(base64), mime, cost); }
            catch (FormatException) { return null; }
        }

        // a stored file is handed back as a link; it is signed, so the provider key stays here.
        // The link client carries no credentials, follows no redirects and connects only to the
        // address it validated, so a rebinding DNS answer cannot turn this into an internal probe.
        if (Jsons.Str(audio, "url") is { Length: > 0 } link && LinkGuard.IsFetchableLink(link, out var uri))
        {
            using var fetched = await LinkGuard.LinkHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            await ProviderHttp.EnsureOk(fetched, ct);
            return (await ProviderHttp.ReadBytesCappedAsync(fetched.Content, MaxSpeechBytes, ct), mime, cost);
        }
        return null;
    }
}
