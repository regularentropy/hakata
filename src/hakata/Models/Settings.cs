using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hakata.Models;

/// <summary>File format of settings.json: indented, enums as their lowercase token.</summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>One provider: a named OpenAI-compatible connection with its own default model.</summary>
public sealed class ProviderConfig
{
    public string Name { get; set; } = "";  // what the per-slot pickers show, and how a route finds it
    public string BaseUrl { get; set; } = "";

    // the key in memory is always the plain one; only what lands in settings.json is encrypted
    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    /// <summary>The same key as the file carries it: DPAPI ciphertext, or the plain value of an older file.</summary>
    [JsonPropertyName("ApiKey")]
    public string StoredKey
    {
        get => KeyProtector.Protect(ApiKey);
        set => ApiKey = KeyProtector.Unprotect(value ?? "");
    }

    // the model a slot that names this provider and no model sends
    public string Model { get; set; } = "";

    // hand-entered model ids the provider accepts but its /models listing may not show; offered in the
    // model pickers only — nothing retries or falls back to them on its own
    public List<string> ExtraModels { get; set; } = [];

    /// <summary>Case-insensitive match on the trimmed name; the one rule every name comparison uses.</summary>
    public static bool SameName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>First item whose label matches <paramref name="name"/> (SameName), else the first item,
    /// else <paramref name="fallback"/> when the list is empty.</summary>
    public static T? FirstNamed<T>(IReadOnlyList<T> items, string? name, Func<T, string?> label, T? fallback) where T : class
    {
        var wanted = name?.Trim() ?? "";
        if (wanted.Length > 0)
        {
            var named = items.FirstOrDefault(item => SameName(label(item), wanted));
            if (named is not null) return named;
        }
        return items.Count > 0 ? items[0] : fallback;
    }
}

/// <summary>Where one request goes: the connection it is sent to, the model and the reasoning effort.</summary>
public sealed record ProviderRoute(string Provider, string BaseUrl, string ApiKey, string Model, string Effort);

/// <summary>User's own providers plus app state that outlives a run.</summary>
public sealed class AppSettings
{
    // the providers, all equal; a slot that names none uses the first one
    public List<ProviderConfig> Providers { get; set; } = [];

    public string BasicModel { get; set; } = "";     // optional per-tier overrides
    public string StandardModel { get; set; } = "";
    public string BestModel { get; set; } = "";

    // provider a tier or a speech slot sends to; empty means the first provider
    public string BasicProvider { get; set; } = "";
    public string StandardProvider { get; set; } = "";
    public string BestProvider { get; set; } = "";
    public string SpeechProvider { get; set; } = "";
    public string SoundProvider { get; set; } = "";

    // speech-to-text model sent to /audio/transcriptions by the mic button
    public string SpeechModel { get; set; } = "whisper-1";

    // transcription endpoint; empty means "derive it from BaseUrl" (ProviderUrls.AudioUrl)
    public string SpeechUrl { get; set; } = "";

    // text-to-speech model sent to /audio/speech by the sound button; empty takes the first model the
    // provider's audio catalog offers (OpenAI has none, so tts-1 is sent)
    public string SoundModel { get; set; } = "";

    // voice name; empty uses the provider's published default for the model (OpenAI: alloy)
    public string SoundVoice { get; set; } = "";

    // speech endpoint; empty means "derive it from BaseUrl" (ProviderUrls.SpeakUrl)
    public string SoundUrl { get; set; } = "";

    // manual speech prices for providers that publish no rate and no usage cost; 0 = unset.
    // Used only when the response's usage.cost and the published catalog rate are both missing.
    public double SpeechPricePerMinute { get; set; }
    public double SoundPricePerMChars { get; set; }

    // keep the rendered clips on disk: the same translation plays without a new request
    public bool CacheSound { get; set; } = true;

    // how many of the newest history entries keep their audio besides the favorites; 0 keeps none
    public int SoundCacheEntries { get; set; } = 20;

    // reasoning effort sent as reasoning_effort; empty = let the provider decide
    public string ReasoningEffort { get; set; } = "";
    public string BasicEffort { get; set; } = "";     // optional per-tier overrides
    public string StandardEffort { get; set; } = "";
    public string BestEffort { get; set; } = "";

    // ui state
    public string SourceLang { get; set; } = "auto";
    public string TargetLang { get; set; } = "en";
    // follow the OS until the user picks a side: the header button then cycles light → dark → system
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool OptionsVisible { get; set; }
    public bool SidebarVisible { get; set; } = true;
    public const double DefaultSidebarWidth = 210;
    public const double MinSidebarWidth = 150; // the splitter and the settings input clamp to these
    public const double MaxSidebarWidth = 420;
    public double SidebarWidth { get; set; } = DefaultSidebarWidth; // dragged width of the history sidebar
    public QualityTier Tier { get; set; } = QualityTier.Standard;

    // a Latin-letter line under texts in a non-Latin script, one extra request per run
    public bool Transliteration { get; set; } = true;

    // romanising a long text reads as noise, so the line stops past this many source characters
    public int TransliterationMaxChars { get; set; } = 400;

    // false leaves the source pane alone and romanises only the translation
    public bool TransliterationOnSource { get; set; } = true;

    /// <summary>Whether this source text gets a transliteration at all.</summary>
    public bool WantsTransliteration(string source) =>
        Transliteration && TransliterationMaxChars > 0 && source.Length <= TransliterationMaxChars;

    // alternatives and word insights ride the history entry, so a restore shows them without a new
    // request; false keeps them on screen only and never writes them to the history
    public bool SaveExtras { get; set; } = true;

    // read the release feed quietly at launch; only a newer release shows itself (header button)
    public bool CheckUpdatesOnLaunch { get; set; } = true;

    // window geometry, restored on the next run
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 700;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    // translation options
    public TranslationOptions Options { get; set; } = new();

    // cost estimate, per 1M tokens, in whatever currency the provider bills; 0 hides the cost
    // shortcut: one price pair for every provider, so a setup that spreads its tiers over a cheap and a
    // premium provider prices all of them alike; add per-provider prices when a wrong estimate starts to matter
    public double InputPricePerMTok { get; set; }
    public double OutputPricePerMTok { get; set; }

    /// <summary>True once any provider has a base URL; a first run has none.</summary>
    [JsonIgnore]
    public bool HasProvider =>
        Providers.Any(p => !string.IsNullOrWhiteSpace(p.BaseUrl));

    /// <summary>
    /// Where a quality tier sends: its own provider and model when set, else the shared defaults.
    /// Detect and transliteration stay on the basic route; alternatives and insights follow the tier the
    /// window is set to, so they come from the route that wrote the translation.
    /// </summary>
    public ProviderRoute Route(QualityTier tier)
    {
        var (provider, model, effort) = tier switch
        {
            QualityTier.Basic => (BasicProvider, BasicModel, BasicEffort),
            QualityTier.Best => (BestProvider, BestModel, BestEffort),
            _ => (StandardProvider, StandardModel, StandardEffort),
        };
        var connection = Connection(provider);
        return MakeRoute(connection, FirstNonEmpty(model, connection.Model).Trim(),
            FirstNonEmpty(effort, ReasoningEffort).Trim());
    }

    /// <summary>The connection the mic uploads to; the speech model never falls back to the chat model.</summary>
    [JsonIgnore]
    public ProviderRoute SpeechRoute => RouteFor(SpeechProvider, SpeechModel);

    /// <summary>The connection the sound button posts to.</summary>
    [JsonIgnore]
    public ProviderRoute SoundRoute => RouteFor(SoundProvider, SoundModel);

    /// <summary>
    /// The provider a name refers to. A slot that names none, or names one that is gone, uses the first
    /// provider on the list.
    /// </summary>
    public ProviderConfig Connection(string? providerName) =>
        ProviderConfig.FirstNamed(Providers, providerName, p => p.Name, new ProviderConfig())!;

    ProviderRoute RouteFor(string? providerName, string? model)
    {
        return MakeRoute(Connection(providerName), model?.Trim() ?? "", "");
    }

    private static ProviderRoute MakeRoute(ProviderConfig connection, string model, string effort) =>
        new(connection.Name?.Trim() ?? "", connection.BaseUrl?.Trim() ?? "",
            connection.ApiKey?.Trim() ?? "", model, effort);

    /// <summary>The model the current quality tier will actually send, overrides included.</summary>
    [JsonIgnore]
    public string EffectiveModel => Route(Tier).Model;

    /// <summary>The transcription model to upload to; the OpenAI id stands in for an empty field.</summary>
    [JsonIgnore]
    public string EffectiveSpeechModel => Fallback(SpeechRoute.Model, "whisper-1");

    /// <summary>The text-to-speech model to post to; the OpenAI id stands in for an empty field.</summary>
    [JsonIgnore]
    public string EffectiveSoundModel => Fallback(SoundRoute.Model, "tts-1");

    /// <summary>The voice to ask for; OpenAI's default voice stands in for an empty field.</summary>
    [JsonIgnore]
    public string EffectiveSoundVoice =>
        string.IsNullOrWhiteSpace(SoundVoice) ? "alloy" : SoundVoice.Trim();

    [JsonIgnore]
    public string ProviderLabel
    {
        get
        {
            var route = Route(Tier);
            var where = route.Provider.Length > 0 ? route.Provider
                : route.BaseUrl.Length > 0 ? HostOf(route.BaseUrl)
                : "no provider";
            return route.Model.Length > 0 ? $"{where} · {route.Model}" : $"{where} · no model set";
        }
    }

    static string Fallback(string model, string instead) => model.Length > 0 ? model : instead;

    static string FirstNonEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback ?? "" : preferred;

    static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // settings and history share one directory
    /// <summary>Set by Program.Main to the resolved profile dir. Wins over HAKATA_HOME so the
    /// env var keeps its original meaning (user-set custom home) for Profiles.BaseDir and the dir-mode
    /// check — publishing the profile dir into the env var would make BaseDir return the profile itself.</summary>
    internal static string? DirOverride;

    public static string Dir =>
        DirOverride is { Length: > 0 } profileDir ? profileDir
            : Environment.GetEnvironmentVariable("HAKATA_HOME") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hakata");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>True when this run could not read the store: writing is disabled so defaults never
    /// land on the user's data, and the caller can say so on the status line.</summary>
    [JsonIgnore]
    public bool PersistenceDisabled { get; internal set; }

    /// <summary>What happened while loading, for the status line: null when the store read fine.</summary>
    [JsonIgnore]
    public string? RecoveryNotice { get; internal set; }
}

