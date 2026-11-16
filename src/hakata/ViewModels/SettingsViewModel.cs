using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// The settings dialog: the speech and display fields and the save / cancel actions. The provider list
/// lives in <see cref="ProvidersViewModel"/>, the model routing and efforts in <see cref="SlotsViewModel"/>.
/// </summary>
public sealed partial class SettingsViewModel : Observable
{
    readonly SettingsService settingsService;

    [ObservableProperty] private string? speechUrl;
    [ObservableProperty] private string? soundVoice;
    [ObservableProperty] private string? soundUrl;
    [ObservableProperty] private decimal speechPricePerMinute;
    [ObservableProperty] private decimal soundPricePerMChars;
    [ObservableProperty] private bool cacheSound;
    [ObservableProperty] private decimal soundCacheEntries;
    [ObservableProperty] private bool transliteration;
    [ObservableProperty] private decimal transliterationMaxChars;
    [ObservableProperty] private decimal sidebarWidth;
    [ObservableProperty] private bool transliterationOnSource;
    [ObservableProperty] private bool saveExtras;
    [ObservableProperty] private decimal alternativesCount;
    [ObservableProperty] private string error = "";

    /// <summary>The providers tab: the rows, their listings and the reload that fills the pickers.</summary>
    public ProvidersViewModel Providers { get; }

    /// <summary>The models tab: the per-slot routing and the reasoning efforts.</summary>
    public SlotsViewModel Slots { get; }

    /// <summary>The profiles tab: the named data directories and the switch / create actions.</summary>
    public ProfilesViewModel Profiles { get; }

    /// <summary>The presets tab: rename, reorder, import and export of the named option bundles.</summary>
    public PresetsViewModel Presets { get; }

    /// <summary>The about tab: the build, its links and the update check.</summary>
    public AboutViewModel About { get; }

    /// <summary>Voices the selected speech model publishes, else the OpenAI-compatible names.</summary>
    public ObservableCollection<string> SoundVoices { get; } = [];

    /// <summary>Stand-in for a provider that publishes no voice list: the OpenAI names.</summary>
    static readonly string[] FallbackVoices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    /// <summary>No provider on file: the dialog presents itself as first-run setup.</summary>
    public bool IsFirstRun { get; }

    public string WindowTitle => IsFirstRun ? "Set up your provider" : "Settings";
    public string CancelText => IsFirstRun ? "Later  Esc" : "Cancel  Esc";

    /// <summary>The dialog owns no window reference: it asks whoever wired it up to close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when a save applied the new settings live but could not persist them: the header
    /// model label is stale until republished the way RefreshProviderLabel does after a successful write.</summary>
    public event Action? LabelRefreshRequested;

    public SettingsViewModel(SettingsService settingsService) : this(settingsService, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) { }

    public SettingsViewModel(SettingsService settingsService, HttpClient http)
    {
        this.settingsService = settingsService;
        var settings = settingsService.Settings;
        speechUrl = settings.SpeechUrl;
        soundVoice = settings.SoundVoice;
        soundUrl = settings.SoundUrl;
        speechPricePerMinute = (decimal)settings.SpeechPricePerMinute;
        soundPricePerMChars = (decimal)settings.SoundPricePerMChars;
        cacheSound = settings.CacheSound;
        soundCacheEntries = settings.SoundCacheEntries;
        transliteration = settings.Transliteration;
        transliterationMaxChars = settings.TransliterationMaxChars;
        transliterationOnSource = settings.TransliterationOnSource;
        saveExtras = settings.SaveExtras;
        alternativesCount = settings.Options.AlternativesCount;
        sidebarWidth = (decimal)settings.SidebarWidth;
        Providers = new ProvidersViewModel(this, http, settings);
        Slots = new SlotsViewModel(this, settings);
        Profiles = new ProfilesViewModel();
        Presets = new PresetsViewModel(settingsService, null, m => Error = m, manageOnly: true);
        var metadata = new AppMetadata();
        About = new AboutViewModel(metadata, new UpdateService(metadata, http), settings);
        IsFirstRun = !settings.HasProvider;
        RefreshSoundVoices(); // seeds the voice picker before the catalog answers
        // no catalog fetch on open (#0081): it lagged the dialog, and the Refresh button or a
        // connection-field edit loads the pickers on demand
    }

    // ---- callbacks from the providers tab ---------------------------------

    /// <summary>A row was added or removed: the pickers and effort lists read the list.</summary>
    internal void OnProvidersChanged() => Slots.OnProvidersChanged();

    /// <summary>A row's extra-models line changed: the pickers widen, the slot labels follow.</summary>
    internal void OnExtraModelsChanged() => Slots.OnExtraModelsChanged();

    /// <summary>A row was renamed: the slots store names, so the rename carries them.</summary>
    internal void OnProviderRenamed(string? oldName, string? newName) => Slots.OnProviderRenamed(oldName, newName);

    /// <summary>The speech provider changed: the speech URL hint names the new endpoint.</summary>
    internal void OnSpeechProviderChanged() => OnPropertyChanged(nameof(SpeechUrlHint));

    /// <summary>The sound provider changed: the sound URL hint names the new endpoint.</summary>
    internal void OnSoundProviderChanged() => OnPropertyChanged(nameof(SoundUrlHint));

    /// <summary>A connection field changed: the speech URL hints name the provider's endpoint.</summary>
    internal void OnConnectionChanged()
    {
        OnPropertyChanged(nameof(SpeechUrlHint));
        OnPropertyChanged(nameof(SoundUrlHint));
    }

    /// <summary>A row's default model changed: the efforts and prices read the model a slot sends to.</summary>
    internal void OnDefaultModelChanged() => Slots.OnDefaultModelChanged();

    /// <summary>The listings answered: rebuild the slots, then the voice picker, and clear the error.</summary>
    internal void OnCatalogsLoaded()
    {
        Slots.OnCatalogsLoaded();
        RefreshSoundVoices();
        Error = "";
    }

    // ---- voices -----------------------------------------------------------

    /// <summary>
    /// Offers the voices the sound provider publishes for the selected speech model, fills an empty voice
    /// field from its published default, and falls back to the OpenAI names when nothing is declared.
    /// </summary>
    internal void RefreshSoundVoices()
    {
        var catalog = Providers.Row(Slots.SoundProvider)?.SoundCatalog ?? Array.Empty<ModelListing>();
        var id = Slots.SoundModel?.Trim() ?? "";
        var listing = id.Length > 0 ? catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
        var voices = listing?.Voices is { Count: > 0 } declared
            ? declared
            : catalog.FirstOrDefault(m => m.Voices is { Count: > 0 })?.Voices ?? [];
        var wanted = voices.Count > 0 ? voices.ToList() : FallbackVoices.ToList();

        if (!wanted.SequenceEqual(SoundVoices))
        {
            SoundVoices.Clear();
            foreach (var voice in wanted) SoundVoices.Add(voice);
        }

        // only an empty field is filled: a voice the user typed is never replaced
        var detected = listing?.DefaultVoice
                       ?? catalog.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.DefaultVoice))?.DefaultVoice;
        if (string.IsNullOrWhiteSpace(SoundVoice) && detected is { Length: > 0 }) SoundVoice = detected;
    }

    // ---- speech URL hints -------------------------------------------------

    /// <summary>Names the endpoint the mic button will actually post to, so the derived guess is never invisible.</summary>
    public string SpeechUrlHint
    {
        get
        {
            var typed = SpeechUrl?.Trim() ?? "";
            if (typed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "Plain HTTP — the API key travels unencrypted to this endpoint.";
            var baseUrl = ProvidersViewModel.Url(Providers.Row(Slots.SpeechProvider));
            return baseUrl.Length == 0
                ? "Empty posts to /audio/transcriptions on the provider's base URL."
                : "Empty posts to " + ProviderUrls.AudioUrl(baseUrl) +
                  ". Set this when the chat URL has no transcription route (OpenRouter, Ollama, gateway compat paths).";
        }
    }

    partial void OnSpeechUrlChanged(string? value) => OnPropertyChanged(nameof(SpeechUrlHint));

    /// <summary>Same service for the sound button: names the endpoint the speech request will hit.</summary>
    public string SoundUrlHint
    {
        get
        {
            var typed = SoundUrl?.Trim() ?? "";
            if (typed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "Plain HTTP — the API key travels unencrypted to this endpoint.";
            var baseUrl = ProvidersViewModel.Url(Providers.Row(Slots.SoundProvider));
            return baseUrl.Length == 0
                ? "Empty posts to /audio/speech on the provider's base URL."
                : "Empty posts to " + ProviderUrls.SpeakUrl(baseUrl) +
                  ". Set this when the chat URL has no speech route (OpenRouter, Ollama, gateway compat paths).";
        }
    }

    partial void OnSoundUrlChanged(string? value) => OnPropertyChanged(nameof(SoundUrlHint));

    // ---- save ------------------------------------------------------------

    static bool IsHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");

    /// <summary>Every check Save runs before persisting, in order; null when the dialog may save.</summary>
    string? Validate(IReadOnlyList<ProviderRow> rows, string? speechUrl, string? soundUrl)
    {
        var providers = new List<ProviderConfig>();
        foreach (var row in rows)
        {
            var provider = row.ToConfig();
            if (provider.BaseUrl.Length == 0)
            {
                // nothing to connect to: an untouched row is dropped, a half-typed one is an error
                if (provider.ApiKey.Length > 0 || provider.Model.Length > 0)
                    return "Every provider needs an http(s) base URL.";
                continue;
            }
            if (provider.Name.Length == 0)
                return "Every provider needs a name.";
            if (!IsHttp(provider.BaseUrl))
                return $"Provider \"{provider.Name}\" needs an http(s) base URL.";
            if (providers.Any(p => ProviderConfig.SameName(p.Name, provider.Name)))
                return $"Two providers are named \"{provider.Name}\".";
            providers.Add(provider);
        }

        if (providers.Count == 0)
            return "Add a provider first: paste its API link.";

        // the two speech overrides are pasted by hand, so they need the same check the base URLs get
        foreach (var (label, url) in new[]
                 {
                     ("Speech to text", speechUrl?.Trim() ?? ""),
                     ("Text to speech", soundUrl?.Trim() ?? ""),
                 })
        {
            if (url.Length == 0 || IsHttp(url)) continue;
            return $"{label} needs an http(s) URL, or leave it empty.";
        }

        // a slot naming a provider but no model inherits that provider's default model; a name no
        // row carries has nothing to inherit, so it is always a validation error
        foreach (var (slot, providerName, model) in new[]
                 {
                     ("Basic", Slots.BasicProvider, Slots.BasicModel),
                     ("Standard", Slots.StandardProvider, Slots.StandardModel),
                     ("Best", Slots.BestProvider, Slots.BestModel),
                 })
        {
            // an unnamed slot routes to the first provider at runtime, so it is never this error
            if (string.IsNullOrWhiteSpace(providerName) || !string.IsNullOrWhiteSpace(model)) continue;
            var connection = providers.FirstOrDefault(p => ProviderConfig.SameName(p.Name, providerName));
            if (connection?.Model?.Trim() is { Length: > 0 }) continue;
            return $"Pick a model for the {slot} tier.";
        }

        return null;
    }

    [RelayCommand]
    void Save()
    {
        var error = Validate(Providers.Rows, SpeechUrl, SoundUrl);
        if (error != null)
        {
            Error = error;
            return;
        }

        // validation passed, so every empty-base-url row is an untouched row: drop it like Save did
        var providers = Providers.Rows.Select(r => r.ToConfig()).Where(p => p.BaseUrl.Length > 0).ToList();

        var settings = settingsService.Settings;
        settings.Providers = providers;
        settings.BasicModel = Slots.BasicModel?.Trim() ?? "";
        settings.StandardModel = Slots.StandardModel?.Trim() ?? "";
        settings.BestModel = Slots.BestModel?.Trim() ?? "";
        settings.BasicProvider = Known(Slots.BasicProvider, providers);
        settings.StandardProvider = Known(Slots.StandardProvider, providers);
        settings.BestProvider = Known(Slots.BestProvider, providers);
        settings.SpeechProvider = Known(Slots.SpeechProvider, providers);
        settings.SoundProvider = Known(Slots.SoundProvider, providers);
        settings.SpeechModel = Slots.SpeechModel?.Trim() ?? "";
        settings.SpeechUrl = SpeechUrl?.Trim() ?? "";
        settings.SoundModel = Slots.SoundModel?.Trim() ?? "";
        settings.SoundVoice = SoundVoice?.Trim() ?? "";
        settings.SoundUrl = SoundUrl?.Trim() ?? "";
        settings.SpeechPricePerMinute = (double)SpeechPricePerMinute;
        settings.SoundPricePerMChars = (double)SoundPricePerMChars;
        settings.CacheSound = CacheSound;
        settings.SoundCacheEntries = (int)SoundCacheEntries;
        settings.ReasoningEffort = Slots.ReasoningEffort?.Trim() ?? "";
        settings.BasicEffort = Slots.BasicEffort?.Trim() ?? "";
        settings.StandardEffort = Slots.StandardEffort?.Trim() ?? "";
        settings.BestEffort = Slots.BestEffort?.Trim() ?? "";
        settings.Transliteration = Transliteration;
        settings.TransliterationMaxChars = (int)TransliterationMaxChars;
        settings.TransliterationOnSource = TransliterationOnSource;
        settings.SaveExtras = SaveExtras;
        settings.Options.AlternativesCount = (int)AlternativesCount;
        settings.CheckUpdatesOnLaunch = About.AutoCheck;
        settings.SidebarWidth = Math.Clamp((double)SidebarWidth, AppSettings.MinSidebarWidth, AppSettings.MaxSidebarWidth);
        // the catalog price lands only at save time: Cancel leaves the live prices untouched.
        // SetPrices applies, saves and raises PricesChanged, which is what wakes the footer; the Save
        // below is then a second atomic write of the same file, cheaper than a second write path.
        try
        {
            if (Slots.HasPublishedPrices) settingsService.SetPrices(Slots.PublishedInput, Slots.PublishedOutput, onlyIfUntouched: true);
            settingsService.Save();
        }
        catch (Exception ex)
        {
            // the fields above already mutated the live settings, so the app runs on the new config
            // while the disk keeps the old file: say so, and republish the header model label the
            // way a successful write does (RefreshProviderLabel after PickModel or a closed dialog)
            Error = "Running on the new settings, but saving failed: " + ex.Message;
            LabelRefreshRequested?.Invoke();
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A slot naming a provider that is no longer on the list falls back to the first one.</summary>
    static string Known(string? name, List<ProviderConfig> providers)
    {
        var wanted = name?.Trim() ?? "";
        if (wanted.Length == 0) return "";
        return providers.FirstOrDefault(p => ProviderConfig.SameName(p.Name, wanted))?.Name ?? "";
    }

    [RelayCommand]
    void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // bounds for the sidebar width input; the main window's splitter clamps to the same consts
    public decimal SidebarWidthMin => (decimal)AppSettings.MinSidebarWidth;
    public decimal SidebarWidthMax => (decimal)AppSettings.MaxSidebarWidth;

    /// <summary>Display tab: put the history sidebar width field back to the default.</summary>
    [RelayCommand]
    void ResetSidebarWidth() => SidebarWidth = (decimal)AppSettings.DefaultSidebarWidth;
}
