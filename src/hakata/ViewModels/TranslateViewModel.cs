using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>The main window's state: language picks, the translation run, and the billing that joins them.
/// Everything around it lives in child view models: <see cref="Chrome"/>, <see cref="History"/>, <see cref="Presets"/>, <see cref="Usage"/>, <see cref="Dictation"/>, <see cref="Speak"/>.</summary>
public sealed partial class TranslateViewModel : Observable, IDisposable
{
    readonly SettingsService settingsService;
    readonly TranslationCoordinator coordinator;
    int run;
    /// <summary>The run whose result the panes currently show. History restore bumps it, so an in-flight
    /// run keeps going but stops repainting the panes; its result still lands in the history list.</summary>
    int shown;
    /// <summary>Why the shown result is stale: the source was edited, or a preset changed the options.</summary>
    string staleReason = "Source edited";
    /// <summary>The trimmed source the shown output was produced from; editing back to it clears the stale flag.</summary>
    string shownSource = "";

    AppSettings settings => settingsService.Settings;

    public List<LangItem> SourceLanguages { get; }
    public List<LangItem> TargetLanguages { get; }
    public HistoryViewModel History { get; }
    public PresetsViewModel Presets { get; }
    public UsageViewModel Usage { get; }
    public DictationViewModel Dictation { get; }
    public SpeakViewModel Speak { get; }
    public ChromeViewModel Chrome { get; }
    public ModelMenuViewModel ModelMenu { get; }

    /// <summary>Nothing can be sent without a provider, so the run button says so up front.</summary>
    public bool HasProvider => settings.HasProvider;

    // ---- header model switcher: lives in ModelMenu; these forwarders keep the tests and the
    // code-behind handlers compiling against the parent view model.
    public ObservableCollection<ModelChoice> ModelChoices => ModelMenu.ModelChoices;
    public string ModelMenuTitle => ModelMenu.ModelMenuTitle;
    public string ModelMenuStatus => ModelMenu.ModelMenuStatus;

    /// <summary>The model the current quality tier sends; the header button shows it and opens the switcher.
    /// Without a provider it carries the first-run call to action instead.</summary>
    public string ModelName
    {
        get
        {
            if (!settings.HasProvider) return "no provider connected";
            return settings.EffectiveModel is { Length: > 0 } model ? model : "no model set";
        }
    }

    [ObservableProperty] private string? sourceText;
    [ObservableProperty] private LangItem? sourceSelected;
    [ObservableProperty] private string? targetText;
    [ObservableProperty] private LangItem? targetSelected;
    [ObservableProperty] private string? inputText;
    [ObservableProperty] private string outputText = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string charCount = "";
    [ObservableProperty] private string detected = "";
    [ObservableProperty] private string alternativesDescription = "";
    [ObservableProperty] private List<Alternative> alternatives = [];
    [ObservableProperty] private List<WordInsight> insights = [];
    [ObservableProperty] private bool alternativesVisible;
    [ObservableProperty] private bool alternativesFailed;
    [ObservableProperty] private bool insightsVisible;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isError;
    [ObservableProperty] private bool isStale;
    [ObservableProperty] private string languageHint = "";
    [ObservableProperty] private string translitSource = "";
    [ObservableProperty] private string translitTarget = "";

    public TranslateViewModel(SettingsService settingsService, History history, TranslationCoordinator coordinator,
        OpenAiCompatibleProvider provider, IAudioRecorder recorder, ISpeechPlayer player)
    {
        this.settingsService = settingsService;
        this.coordinator = coordinator;

        SourceLanguages = LanguageCatalog.Build(history.Entries, includeAuto: true);
        TargetLanguages = LanguageCatalog.Build(history.Entries, includeAuto: false);
        History = new HistoryViewModel(history, message => Status = message, Restore);
        // a store that could not be read is reported once, not silently replaced
        if (history.RecoveryNotice is { } historyNotice) Status = historyNotice;
        else if (settings.RecoveryNotice is { } settingsNotice) Status = settingsNotice;

        sourceText = LanguageNames.Label(settings.SourceLang);
        targetText = LanguageNames.Label(settings.TargetLang);
        Chrome = new ChromeViewModel(settingsService, m => Status = m, RefreshProviderLabel);
        charCount = CountLabel(0);
        Presets = new(settingsService,
            () => { Chrome.RefreshOptions(); MarkOptionsChanged(); },
            m => Status = m);
        Chrome.OptionsEdited += () => Presets.NoteOptionsEdited();
        ModelMenu = new(settingsService, () => Chrome.Tier, m => Status = m, RefreshProviderLabel);
        Usage = new(settingsService, provider);
        Dictation = new(recorder, provider, coordinator, settings, message => Status = message,
            e => IsError = e, AppendToInput, AddCost);
        Speak = new(provider, settings, player, history, () => History.Current, () => OutputText,
            message => Status = message, e => IsError = e, AddCost, coordinator);
    }

    /// <summary>
    /// The provider bills speech too. The amount joins the entry the panel is showing; a dictation made
    /// before any translation rides on the next entry instead of vanishing.
    /// </summary>
    void AddCost(double cost)
    {
        if (cost <= 0) return;
        if (History.AddCost(cost) && History.Current is { } current) Usage.SetUsage(current);
        else coordinator.AddPendingCost(cost);
    }

    /// <summary>Speech is billed to the entry that was current when the render started; an entry that
    /// vanished meanwhile (or none at all) lets the amount ride the next one.</summary>
    void AddCost(HistoryEntry? entry, double cost)
    {
        if (cost <= 0) return;
        if (entry is null || !History.AddCost(entry, cost)) coordinator.AddPendingCost(cost);
        else if (ReferenceEquals(entry, History.Current)) Usage.SetUsage(entry);
    }

    /// <summary>An extra request's tokens join the entry's bill at the prices frozen on it; an entry
    /// that vanished (or none at all) lets them ride the next one at the live prices.</summary>
    void AddUsage(HistoryEntry? entry, TokenUsage usage)
    {
        var cost = entry is not null
            ? Pricing.Cost(usage, entry.InputPricePerMTok, entry.OutputPricePerMTok) ?? 0
            : Pricing.Cost(usage, settings) ?? 0;
        if (entry is not null && History.AddUsage(entry, usage, cost))
        {
            if (ReferenceEquals(entry, History.Current)) Usage.SetUsage(entry);
            return;
        }
        coordinator.AddPendingUsage(usage);
    }

    /// <summary>Dictation extends what is already typed instead of throwing it away.</summary>
    void AppendToInput(string text)
    {
        var existing = InputText?.TrimEnd() ?? "";
        InputText = existing.Length > 0 ? existing + " " + text : text;
    }

    // ---- languages -------------------------------------------------------

    bool CanSwap => ResolveSource("auto") != "auto";

    [RelayCommand(CanExecute = nameof(CanSwap))]
    void Swap()
    {
        var from = ResolveSource(settings.SourceLang);
        var to = ResolveTarget(settings.TargetLang);
        if (from == "auto") return; // nothing to swap into the source slot
        SetLanguages(to, from);
    }

    string ResolveSource(string fallback) =>
        LanguageCatalog.Resolve(SourceLanguages, SourceText, SourceSelected, fallback);

    string ResolveTarget(string fallback) =>
        LanguageCatalog.Resolve(TargetLanguages, TargetText, TargetSelected, fallback);

    void SetLanguages(string from, string to)
    {
        SourceSelected = SourceLanguages.FirstOrDefault(l => l.Iso == from);
        SourceText = LanguageNames.Label(from);
        TargetSelected = TargetLanguages.FirstOrDefault(l => l.Iso == to);
        TargetText = LanguageNames.Label(to);
    }

    public void RefreshProviderLabel()
    {
        OnPropertyChanged(nameof(HasProvider)); // a provider added in Settings unblocks the run button
        OnPropertyChanged(nameof(ModelName));
    }

    /// <summary>Rebuilds the model menu for the current tier (see <see cref="ModelMenu"/>).</summary>
    public Task LoadModelMenuAsync() => ModelMenu.LoadModelMenuAsync();

    /// <summary>Sets the current tier's model override (see <see cref="ModelMenu"/>).</summary>
    public void PickModel(string? id) => ModelMenu.PickModel(id);

    // ---- input / output chrome -------------------------------------------

    public bool InputHintVisible => string.IsNullOrEmpty(InputText);
    public bool OutputHintVisible => string.IsNullOrEmpty(OutputText);
    public bool HasDetected => Detected.Length > 0;
    public bool HasLanguageHint => LanguageHint.Length > 0;
    public bool NotBusy => !IsBusy;

    /// <summary>The status line, unless the shown result is stale — then the reason and the retranslate hint win.</summary>
    public string StatusDisplay => IsStale && !IsBusy ? $"{staleReason} — Ctrl+Enter to retranslate" : Status;

    /// <summary>A preset applied new options: the shown result no longer reflects them until a re-run.</summary>
    void MarkOptionsChanged()
    {
        staleReason = "Options changed";
        IsStale = OutputText.Trim().Length > 0;
        OnPropertyChanged(nameof(StatusDisplay));
    }

    /// <summary>Alternatives and word insights need a finished translation for the current source:
    /// no output, a live run, or an edited source disables them.</summary>
    public bool ExtrasEnabled => !IsBusy && !IsStale && OutputText.Trim().Length > 0;

    /// <summary>Providers reject very long prompts; warn before the user waits for a 400.</summary>
    public bool IsLongInput => (InputText?.Length ?? 0) > 20000;

    partial void OnInputTextChanged(string? value)
    {
        CharCount = CountLabel(value?.Length ?? 0);
        OnPropertyChanged(nameof(InputHintVisible));
        OnPropertyChanged(nameof(IsLongInput));
        LanguageHint = ""; // the previous run's hint no longer describes this text
        if (Detected.Length > 0) Detected = "";
        // an edit leaves the shown result stale until the next run replaces it — unless the edit
        // returns the input to the text the shown output was produced from
        staleReason = "Source edited";
        IsStale = OutputText.Trim().Length > 0 && (value?.Trim() ?? "") != shownSource;
        OnPropertyChanged(nameof(StatusDisplay)); // the reason may have changed while IsStale did not
    }

    partial void OnIsStaleChanged(bool value)
    {
        OnPropertyChanged(nameof(ExtrasEnabled));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusDisplay));

    partial void OnOutputTextChanged(string value)
    {
        OnPropertyChanged(nameof(OutputHintVisible));
        OnPropertyChanged(nameof(ExtrasEnabled));
    }

    public bool HasTranslitSource => TranslitSource.Length > 0;
    public bool HasTranslitTarget => TranslitTarget.Length > 0;

    partial void OnTranslitSourceChanged(string value) => OnPropertyChanged(nameof(HasTranslitSource));
    partial void OnTranslitTargetChanged(string value) => OnPropertyChanged(nameof(HasTranslitTarget));

    partial void OnDetectedChanged(string value) => OnPropertyChanged(nameof(HasDetected));

    partial void OnLanguageHintChanged(string value) => OnPropertyChanged(nameof(HasLanguageHint));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(NotBusy));
        OnPropertyChanged(nameof(ExtrasEnabled));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    partial void OnSourceTextChanged(string? value) => OnLanguageInputChanged();
    partial void OnTargetTextChanged(string? value) => OnLanguageInputChanged();
    partial void OnSourceSelectedChanged(LangItem? value) => OnLanguageInputChanged();
    partial void OnTargetSelectedChanged(LangItem? value) => OnLanguageInputChanged();

    void OnLanguageInputChanged()
    {
        LanguageHint = "";
        SwapCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Explains a typed language that matched nothing, instead of silently falling back.</summary>
    string LanguageHintFor()
    {
        var parts = new List<string>();
        if (UnknownLanguage(SourceText, SourceLanguages, SourceSelected) is { } source)
            parts.Add($"\u201c{source}\u201d is not a known language \u2014 detection will be used.");
        if (UnknownLanguage(TargetText, TargetLanguages, TargetSelected) is { } target)
            parts.Add($"\u201c{target}\u201d is not a known language \u2014 the previous target stays.");
        return string.Join(" ", parts);
    }

    static string? UnknownLanguage(string? typed, IReadOnlyList<LangItem> items, LangItem? selected)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0 || selected is not null) return null;
        return LanguageCatalog.Find(items, text) is null ? text : null;
    }

    static string CountLabel(int n) => $"{n} character{(n == 1 ? "" : "s")}";

    // ---- history ---------------------------------------------------------

    void Restore(HistoryEntry entry)
    {
        // a selection switch must not kill a speak render or a translation in flight: both finish and
        // land on the entry they started for, they just stop repainting the panes from here on
        if (Dictation.IsRecording) Dictation.DiscardRecording();
        Dictation.Cancel();
        shown++; // chunks still arriving belong to the replaced view
        History.Current = entry;
        // shownSource first: assigning InputText before it would read as an edit and flicker IsStale
        shownSource = entry.Source.Trim();
        InputText = entry.Source;
        OutputText = entry.Output;
        SetLanguages(entry.From, entry.To);
        ResetResult();
        // extras stored on the entry come back with it, no new request needed
        AlternativesDescription = entry.AlternativesDescription;
        Alternatives = entry.Alternatives;
        AlternativesVisible = Alternatives.Count > 0;
        Insights = entry.Insights;
        InsightsVisible = Insights.Count > 0;
        Usage.SetUsage(entry); // the footer re-renders the entry's own frozen usage, not the live prices
        // the romanisation was paid for once: restoring the entry brings it back with the text
        TranslitSource = entry.TranslitSource;
        TranslitTarget = entry.TranslitTarget;
        IsStale = false; // the restored output matches the restored source (shownSource set above)
        Status = "Restored from history";
    }

    /// <summary>A fresh translation (or a restored one) starts with empty side panels.</summary>
    void ResetResult()
    {
        Alternatives = [];
        AlternativesVisible = false;
        AlternativesFailed = false;
        AlternativesDescription = "";
        Insights = [];
        InsightsVisible = false;
        Detected = "";
        TranslitSource = "";
        TranslitTarget = "";
        IsError = false;
    }

    // ---- translation -----------------------------------------------------

    [RelayCommand]
    public async Task TranslateAsync()
    {
        var text = InputText?.Trim() ?? "";
        if (text.Length == 0)
        {
            Status = "Nothing to translate";
            return;
        }

        var myRun = ++run; // a newer run owns the status line from here on
        shown = myRun; // ...and the panes
        var from = ResolveSource("auto");
        var to = ResolveTarget(settings.TargetLang);
        settings.SourceLang = from;
        settings.TargetLang = to;
        LanguageHint = LanguageHintFor();

        OutputText = "";
        ResetResult();
        IsStale = false;
        shownSource = text; // the run's source: edits away from it go stale, edits back clear it
        // the run's own partial text: the pane may have changed hands while streaming
        var streamed = "";
        try
        {
            IsBusy = true;
            Status = "Translating…";
            var outcome = await coordinator.TranslateAsync(text, from, to, Chrome.Tier,
                soFar => { streamed = soFar; if (myRun == shown) OutputText = soFar; },
                iso => { if (myRun == shown) Detected = $"Detected ({LanguageNames.Label(iso)})"; });
            if (myRun != run) return; // a clear superseded this run; its result belongs to nobody
            History.Refresh(); // the finished entry lands in the list even if the user moved on
            if (myRun != shown) return; // the panes show another entry now — leave them alone
            History.Current = outcome.Entry;
            // A fresh result is what the panes and the star now show, so a row picked before the run no longer keeps the focus.
            History.SelectedEntry = null;
            History.SetSelection([]);
            Status = outcome.Truncated
                ? $"Done in {outcome.Elapsed.TotalSeconds:0.0}s — output limit reached, result may be incomplete"
                : $"Done in {outcome.Elapsed.TotalSeconds:0.0}s";
            Usage.SetUsage(outcome.Entry); // the entry is authoritative: it already includes detect usage
            // cosmetic and slower than the translation: it lands beside the finished text, never on the status line
            _ = LoadTransliterationAsync(outcome.Entry, text, streamed, from, to, myRun);
        }
        catch (OperationCanceledException)
        {
            if (myRun != run) return;
            var usage = new TokenUsage(TokenUsage.Estimate(text), TokenUsage.Estimate(streamed), Estimated: true);
            if (streamed.Trim().Length > 0)
            {
                var entry = coordinator.Commit(text, streamed, from, to, usage);
                History.Refresh(); // the partial is in the list even if the user moved on
                if (myRun != shown) return;
                History.Current = entry;
                // The kept partial is the new current entry, so the previous list selection no longer matches the panes.
                History.SelectedEntry = null;
                History.SetSelection([]);
                Usage.SetUsage(entry);
                Status = "Cancelled — partial result kept in history";
            }
            else
            {
                if (myRun != shown) return;
                Usage.SetUsage(usage); // no entry exists, so the footer keeps the bare estimate
                Status = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            if (myRun != run) return; // a superseded run's failure belongs to nobody
            if (myRun != shown) return; // the panes show another entry; its failure is not theirs to show
            IsError = true;
            Status = "Error: " + ex.Message;
        }
        finally
        {
            if (myRun == run) IsBusy = false;
        }
    }

    /// <summary>Romanises both sides of a finished translation and stores them on its entry; a failure
    /// leaves the panes as they are.</summary>
    async Task LoadTransliterationAsync(HistoryEntry entry, string source, string output, string from, string to, int myRun)
    {
        if (!settings.WantsTransliteration(source)) return;
        try
        {
            TokenUsage? usage = null;
            var (translitSource, translitTarget) = await coordinator.TransliterationAsync(source, output, from, to, u => usage = u);
            // the request was paid even when a newer run has superseded this one
            AddUsage(entry, usage ?? TokenUsage.EstimatedFor(source, output));
            if (myRun != shown) return; // the panes show another entry; its transliteration is not theirs to show
            var romanisedSource = settings.TransliterationOnSource ? translitSource : "";
            TranslitSource = romanisedSource;
            TranslitTarget = translitTarget;
            History.SaveTransliteration(entry, romanisedSource, translitTarget);
        }
        catch
        {
            // the translation itself succeeded; never turn this into an error
        }
    }

    /// <summary>Stops the streaming run; whatever already arrived stays on screen.</summary>
    [RelayCommand]
    void Stop() => Cancel();

    [RelayCommand]
    public async Task AlternativesAsync()
    {
        if (!NeedTranslation("alternatives", out var source, out var output, out var from, out var to)) return;
        // the entry whose translation these describe: billed and stored on it even if the panes move on
        var entry = History.Current;
        AlternativesVisible = true;
        AlternativesFailed = false;
        Alternatives = [];
        AlternativesDescription = $"Asking for {settings.Options.AlternativesCount} alternatives…";
        try
        {
            var (description, items) = await BilledAsync(entry, source, output, u => coordinator.AlternativesAsync(source, output, from, to, u));
            if (settings.SaveExtras) History.SaveAlternatives(entry, description, items);
            if (!ReferenceEquals(entry, History.Current)) return; // the panes show another entry now
            AlternativesDescription = description;
            Alternatives = items;
            AlternativesVisible = items.Count > 0;
            Status = items.Count == 0 ? "No alternatives returned" : $"{items.Count} alternatives";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(entry, History.Current)) Status = "Alternatives cancelled";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(entry, History.Current)) return;
            AlternativesFailed = true;
            AlternativesDescription = "Error: " + ex.Message;
            Status = "Alternatives failed";
            IsError = true;
        }
    }

    [RelayCommand]
    public async Task InsightsAsync()
    {
        if (!NeedTranslation("word insights", out var source, out var output, out var from, out var to)) return;
        // same as alternatives: the entry captured at the start owns the bill, not whatever is current later
        var entry = History.Current;
        InsightsVisible = true;
        Insights = [];
        Status = "Reading the nuance…";
        try
        {
            var insights = await BilledAsync(entry, source, output, u => coordinator.InsightsAsync(source, output, from, to, u));
            if (settings.SaveExtras) History.SaveInsights(entry, insights);
            if (!ReferenceEquals(entry, History.Current)) return; // the panes show another entry now
            Insights = insights;
            InsightsVisible = insights.Count > 0;
            Status = insights.Count == 0 ? "No insights returned" : $"{insights.Count} words with variants";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(entry, History.Current)) Status = "Word insights cancelled";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(entry, History.Current)) return;
            Status = "Word insights failed: " + ex.Message;
            IsError = true;
        }
    }

    public async Task RunExtrasAsync()
    {
        await AlternativesAsync();
        await InsightsAsync();
    }

    /// <summary>Runs one extras request and bills it: the provider charges even when zero items come
    /// back, so a run that reports no usage is charged the estimate.</summary>
    async Task<T> BilledAsync<T>(HistoryEntry? entry, string source, string output, Func<Action<TokenUsage>, Task<T>> call)
    {
        TokenUsage? usage = null;
        var result = await call(u => usage = u);
        AddUsage(entry, usage ?? TokenUsage.EstimatedFor(source, output));
        return result;
    }

    /// <summary>Closing a panel dismisses it for good: hidden, and dropped from the entry so a
    /// restore does not bring it back.</summary>
    [RelayCommand]
    void CloseAlternatives()
    {
        AlternativesVisible = false;
        Alternatives = [];
        AlternativesDescription = "";
        History.SaveAlternatives(History.Current, "", []);
    }

    [RelayCommand]
    void CloseInsights()
    {
        InsightsVisible = false;
        Insights = [];
        History.SaveInsights(History.Current, []);
    }

    bool NeedTranslation(string what, out string source, out string output, out string from, out string to)
    {
        source = InputText?.Trim() ?? "";
        output = OutputText.Trim();
        from = ResolveSource("auto");
        to = ResolveTarget(settings.TargetLang);
        // an edited source must not be sent to extras and billed onto the entry whose stored source differs
        if (IsStale)
        {
            Status = $"{staleReason} — Ctrl+Enter to retranslate first";
            return false;
        }
        if (source.Length > 0 && output.Length > 0) return true;
        Status = "Translate first, then ask for " + what;
        return false;
    }

    // ---- clear -----------------------------------------------------------

    /// <summary>Empties the source and the translation it produced: both describe the deleted text.</summary>
    [RelayCommand]
    void ClearInput()
    {
        Cancel();
        run++; // a chunk or a romanisation still in flight belongs to the cleared text
        IsBusy = false;
        InputText = "";
        OutputText = "";
        ResetResult();
        shownSource = "";
        IsStale = false;
    }

    [RelayCommand]
    void ClearAll()
    {
        ClearInput();
        Status = "";
        Usage.Reset();
    }

    public void Cancel(bool speak = true)
    {
        if (Dictation.IsRecording) Dictation.DiscardRecording();
        Dictation.Cancel();
        if (speak) Speak.Cancel();
        coordinator.Cancel();
    }

    /// <summary>Completes when no request is in flight — awaited after Cancel so a profile swap never disposes a running graph.</summary>
    public Task WhenIdleAsync() => coordinator.UntilIdleAsync();

    /// <summary>The DI graph disposes this on a profile swap; the child VMs hold subscriptions to shared services.</summary>
    public void Dispose()
    {
        Speak.Dispose();
        Usage.Dispose();
    }
}

/// <summary>One row of the header model switcher: the id to send, its label, and whether it is the active pick.
/// Rebuilt on every open and pick, so the rows need no change notification of their own.</summary>
public sealed record ModelChoice(string Id, string Label, bool IsCurrent);
