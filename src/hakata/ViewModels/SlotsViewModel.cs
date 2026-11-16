using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// The models tab: the per-slot routing (provider and model per tier, speech, sound) and the reasoning
/// efforts. The provider list lives in <see cref="ProvidersViewModel"/>; the dialog, speech fields and
/// saving stay with <see cref="SettingsViewModel"/>, reached through the owner.
/// </summary>
public sealed partial class SlotsViewModel : Observable
{
    readonly SettingsViewModel owner;
    readonly AppSettings settings;

    internal SlotsViewModel(SettingsViewModel owner, AppSettings settings)
    {
        this.owner = owner;
        this.settings = settings;
        basicModel = settings.BasicModel;
        standardModel = settings.StandardModel;
        bestModel = settings.BestModel;
        basicProvider = settings.BasicProvider;
        standardProvider = settings.StandardProvider;
        bestProvider = settings.BestProvider;
        speechProvider = settings.SpeechProvider;
        soundProvider = settings.SoundProvider;
        speechModel = settings.SpeechModel;
        soundModel = settings.SoundModel;
        reasoningEffort = settings.ReasoningEffort;
        basicEffort = settings.BasicEffort;
        standardEffort = settings.StandardEffort;
        bestEffort = settings.BestEffort;
        RefreshEfforts(); // seeds the dropdowns: provider default first, then the stand-in vocabulary
    }

    // ---- a slot: the provider and model one route sends, plus its reasoning effort ----
    [ObservableProperty] private string? basicModel;
    [ObservableProperty] private string? standardModel;
    [ObservableProperty] private string? bestModel;
    [ObservableProperty] private string? basicProvider;
    [ObservableProperty] private string? standardProvider;
    [ObservableProperty] private string? bestProvider;
    [ObservableProperty] private string? speechProvider;
    [ObservableProperty] private string? soundProvider;
    [ObservableProperty] private string? speechModel;
    [ObservableProperty] private string? soundModel;
    [ObservableProperty] private string? reasoningEffort;
    [ObservableProperty] private string? basicEffort;
    [ObservableProperty] private string? standardEffort;
    [ObservableProperty] private string? bestEffort;
    [ObservableProperty] private string effortHint = "";

    /// <summary>
    /// One picker per slot, holding every provider's ids. An id that only one provider offers stays bare;
    /// an id two of them offer is written "provider · id". Picking one sets provider and model together.
    /// </summary>
    public ObservableCollection<string> BasicModels { get; } = [];
    public ObservableCollection<string> StandardModels { get; } = [];
    public ObservableCollection<string> BestModels { get; } = [];
    public ObservableCollection<string> SpeechModels { get; } = [];
    public ObservableCollection<string> SoundModels { get; } = [];

    /// <summary>One row of an effort dropdown. The empty value is the provider default, which sends no parameter.</summary>
    public sealed record EffortChoice(string Value)
    {
        public string Label => Value.Length == 0 ? "provider default" : Value;
        public override string ToString() => Label;
    }

    /// <summary>Effort values for the shared "all tiers" choice and one list per tier, each from its own model.</summary>
    public ObservableCollection<EffortChoice> EffortChoices { get; } = [];
    public ObservableCollection<EffortChoice> BasicEfforts { get; } = [];
    public ObservableCollection<EffortChoice> StandardEfforts { get; } = [];
    public ObservableCollection<EffortChoice> BestEfforts { get; } = [];

    static readonly string[] FallbackEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    // ---- callbacks from the providers tab ---------------------------------

    /// <summary>A row was added or removed: the pickers and effort lists read the list.</summary>
    internal void OnProvidersChanged()
    {
        RefreshEfforts();
        RaiseSlotTexts();
    }

    /// <summary>
    /// A row was renamed: the slots store names, not rows, so a rename has to carry them
    /// or they silently reroute.
    /// </summary>
    internal void OnProviderRenamed(string? oldName, string? newName)
    {
        if (oldName is not null)
        {
            var from = oldName.Trim();
            var to = newName?.Trim() ?? "";
            if (ProviderConfig.SameName(BasicProvider, from)) BasicProvider = to;
            if (ProviderConfig.SameName(StandardProvider, from)) StandardProvider = to;
            if (ProviderConfig.SameName(BestProvider, from)) BestProvider = to;
            if (ProviderConfig.SameName(SpeechProvider, from)) SpeechProvider = to;
            if (ProviderConfig.SameName(SoundProvider, from)) SoundProvider = to;
        }
        RaiseSlotTexts();
    }

    /// <summary>A row's default model changed: the efforts and prices read the model a slot sends to.</summary>
    internal void OnDefaultModelChanged()
    {
        RefreshEfforts();
        ApplyPublishedPrices();
        RaiseSlotTexts(); // the tier placeholder reads the first row's model
    }

    /// <summary>The listings answered: rebuild the slot pickers, then prices and efforts.</summary>
    internal void OnCatalogsLoaded()
    {
        RefreshSlotModels();
        ApplyPublishedPrices(); // appends its note after the status, so the cost chip in the main window is explained
        RefreshEfforts();
    }

    /// <summary>A row's extra-models line changed: the pickers widen, the slot labels follow.</summary>
    internal void OnExtraModelsChanged()
    {
        RefreshSlotModels();
        RaiseSlotTexts();
    }

    // ---- slot fields -----------------------------------------------------

    /// <summary>What a slot shows: the id alone when one provider offers it, "provider · id" when several do.</summary>
    string SlotText(string? provider, string? model, Func<ProviderRow, IReadOnlyList<string>> kind)
    {
        var name = provider?.Trim() ?? "";
        var id = model?.Trim() ?? "";
        if (name.Length == 0) return id;
        if (id.Length == 0) return name;
        return owner.Providers.Holders(kind, id) > 1 ? ProvidersViewModel.Label(name, id) : id;
    }

    /// <summary>
    /// Reads a slot field back into a provider and a model. "provider · id" routes to that provider, a listed
    /// id goes to the provider that lists it (the slot's own one wins a tie), and an id no listing has stays
    /// on the provider the slot already had, so a hand-typed id is never rerouted by accident.
    /// </summary>
    (string Provider, string Model) Split(string? currentProvider, string? text, Func<ProviderRow, IReadOnlyList<string>> kind)
    {
        var raw = text?.Trim() ?? "";
        if (raw.Length == 0) return ("", ""); // an empty field means "the first provider and its model"

        foreach (var row in owner.Providers.Rows)
        {
            var name = row.Label;
            var prefix = name + " · ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (name, raw[prefix.Length..].Trim());
            if (ProviderConfig.SameName(raw, name)) return (name, "");
        }

        var holders = owner.Providers.Rows.Where(r => kind(r).Contains(raw, StringComparer.OrdinalIgnoreCase)).ToList();
        if (holders.Count > 0)
        {
            var current = currentProvider?.Trim() ?? "";
            var preferred = holders.FirstOrDefault(r => ProviderConfig.SameName(r.Label, current));
            return ((preferred ?? holders[0]).Label, raw);
        }
        return (currentProvider?.Trim() ?? "", raw);
    }

    void RaiseSlotTexts() => Notify(nameof(BasicModelText), nameof(StandardModelText), nameof(BestModelText),
        nameof(SpeechModelText), nameof(SoundModelText), nameof(FallbackModelHint));

    /// <summary>Keys the five slots; the three chat tiers share the chat ids, speech and sound have their own.</summary>
    enum SlotTier { Basic, Standard, Best, Speech, Sound }

    string Text(SlotTier tier) => tier switch
    {
        SlotTier.Basic => SlotText(BasicProvider, BasicModel, owner.Providers.ChatIds),
        SlotTier.Standard => SlotText(StandardProvider, StandardModel, owner.Providers.ChatIds),
        SlotTier.Best => SlotText(BestProvider, BestModel, owner.Providers.ChatIds),
        SlotTier.Speech => SlotText(SpeechProvider, SpeechModel, owner.Providers.SpeechIds),
        _ => SlotText(SoundProvider, SoundModel, owner.Providers.SoundIds),
    };

    /// <summary>Reads the edited text back into the tier's provider and model, in that order, exactly as the per-tier setters did.</summary>
    void SetText(SlotTier tier, string? value)
    {
        var (provider, model) = Split(ProviderOf(tier), value, KindOf(tier));
        switch (tier)
        {
            case SlotTier.Basic: (BasicProvider, BasicModel) = (provider, model); break;
            case SlotTier.Standard: (StandardProvider, StandardModel) = (provider, model); break;
            case SlotTier.Best: (BestProvider, BestModel) = (provider, model); break;
            case SlotTier.Speech: (SpeechProvider, SpeechModel) = (provider, model); break;
            default: (SoundProvider, SoundModel) = (provider, model); break;
        }
    }

    string? ProviderOf(SlotTier tier) => tier switch
    {
        SlotTier.Basic => BasicProvider,
        SlotTier.Standard => StandardProvider,
        SlotTier.Best => BestProvider,
        SlotTier.Speech => SpeechProvider,
        _ => SoundProvider,
    };

    Func<ProviderRow, IReadOnlyList<string>> KindOf(SlotTier tier) => tier switch
    {
        SlotTier.Speech => owner.Providers.SpeechIds,
        SlotTier.Sound => owner.Providers.SoundIds,
        _ => owner.Providers.ChatIds,
    };

    public string BasicModelText { get => Text(SlotTier.Basic); set => SetText(SlotTier.Basic, value); }
    public string StandardModelText { get => Text(SlotTier.Standard); set => SetText(SlotTier.Standard, value); }
    public string BestModelText { get => Text(SlotTier.Best); set => SetText(SlotTier.Best, value); }
    public string SpeechModelText { get => Text(SlotTier.Speech); set => SetText(SlotTier.Speech, value); }
    public string SoundModelText { get => Text(SlotTier.Sound); set => SetText(SlotTier.Sound, value); }

    /// <summary>Greyed text of an empty tier field: one placeholder for all three boxes, the first provider's default model.</summary>
    public string FallbackModelHint
    {
        get
        {
            var row = owner.Providers.Rows.FirstOrDefault();
            if (row is null) return "no provider yet";
            return row.Model?.Trim() is { Length: > 0 } model ? ProvidersViewModel.Label(row.Label, model) : "no model set";
        }
    }

    // a slot that moved to another provider shows it in the label and, for speech, in the URL hint;
    // the efforts follow too, so a tier never keeps offering what the old provider published — and
    // switching the speech or sound model switches the voices it declares
    void OnSlotChanged(SlotTier tier, bool isModel)
    {
        OnPropertyChanged(tier switch
        {
            SlotTier.Basic => nameof(BasicModelText),
            SlotTier.Standard => nameof(StandardModelText),
            SlotTier.Best => nameof(BestModelText),
            SlotTier.Speech => nameof(SpeechModelText),
            _ => nameof(SoundModelText),
        });
        switch (tier)
        {
            case SlotTier.Basic or SlotTier.Standard or SlotTier.Best:
                RefreshEfforts();
                break;
            case SlotTier.Speech:
                if (!isModel) owner.OnSpeechProviderChanged();
                break;
            default: // Sound
                if (!isModel) owner.OnSoundProviderChanged();
                owner.RefreshSoundVoices();
                break;
        }
    }

    partial void OnBasicModelChanged(string? value) => OnSlotChanged(SlotTier.Basic, isModel: true);
    partial void OnStandardModelChanged(string? value) => OnSlotChanged(SlotTier.Standard, isModel: true);
    partial void OnBestModelChanged(string? value) => OnSlotChanged(SlotTier.Best, isModel: true);
    partial void OnSpeechModelChanged(string? value) => OnSlotChanged(SlotTier.Speech, isModel: true);
    partial void OnSoundModelChanged(string? value) => OnSlotChanged(SlotTier.Sound, isModel: true);
    partial void OnBasicProviderChanged(string? value) => OnSlotChanged(SlotTier.Basic, isModel: false);
    partial void OnStandardProviderChanged(string? value) => OnSlotChanged(SlotTier.Standard, isModel: false);
    partial void OnBestProviderChanged(string? value) => OnSlotChanged(SlotTier.Best, isModel: false);
    partial void OnSpeechProviderChanged(string? value) => OnSlotChanged(SlotTier.Speech, isModel: false);
    partial void OnSoundProviderChanged(string? value) => OnSlotChanged(SlotTier.Sound, isModel: false);

    /// <summary>The entry a provider's own listing has for a model id, or null when it published none.</summary>
    static ModelListing? Listing(ProviderRow row, string? modelId)
    {
        var id = modelId?.Trim() ?? "";
        if (id.Length == 0) return null;
        return row.ChatCatalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What one provider publishes about efforts for one model: the list, and whether it takes ours.</summary>
    readonly record struct EffortSupport(IReadOnlyList<string> Declared, bool Accepted)
    {
        public static EffortSupport Unknown { get; } = new([], true);
    }

    /// <summary>The model a slot actually sends: its own pick, else the provider row's default.</summary>
    static string SentModel(string? own, ProviderRow row) =>
        own?.Trim() is { Length: > 0 } picked ? picked : row.Model?.Trim() ?? "";

    /// <summary>
    /// What the provider behind a slot says about the model that slot sends to. A published parameter list
    /// without reasoning_effort means the model ignores our parameter, so no effort is offered there; a
    /// provider that publishes nothing keeps the common vocabulary, since the gateway may still take it.
    /// </summary>
    EffortSupport EffortsFor(string? providerName, string? model)
    {
        var row = owner.Providers.Row(providerName);
        if (row is null) return EffortSupport.Unknown;
        var id = SentModel(model, row);
        if (Listing(row, id) is not { } listing) return EffortSupport.Unknown;
        var declared = listing.Efforts is { Count: > 0 } efforts ? (IReadOnlyList<string>)efforts : [];
        var accepted = listing.Parameters is not { Count: > 0 } ||
                       listing.Parameters.Contains("reasoning_effort", StringComparer.OrdinalIgnoreCase);
        return new EffortSupport(declared, accepted);
    }

    /// <summary>Builds one effort dropdown: the provider default first, then what that model accepts.</summary>
    static void FillEfforts(ObservableCollection<EffortChoice> target, EffortSupport support, string? stored)
    {
        var wanted = new List<string> { "" };
        if (support.Accepted)
            wanted.AddRange(support.Declared.Count > 0 ? support.Declared : FallbackEfforts);
        // a stored value the provider no longer lists stays selectable instead of silently dropping out
        var value = stored?.Trim() ?? "";
        if (value.Length > 0 && !wanted.Contains(value, StringComparer.OrdinalIgnoreCase)) wanted.Add(value);

        if (target.Select(c => c.Value).SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var effort in wanted) target.Add(new EffortChoice(effort));
    }

    /// <summary>
    /// Fills the four effort dropdowns. Basic, Standard and Best each read the listing of the provider and
    /// model they route to, so a tier never offers an effort its own provider does not accept; the shared
    /// "all tiers" list is the union of every provider's published lists, so a value one provider declares stays pickable everywhere.
    /// </summary>
    void RefreshEfforts()
    {
        var basic = EffortsFor(BasicProvider, BasicModel);
        var standard = EffortsFor(StandardProvider, StandardModel);
        var best = EffortsFor(BestProvider, BestModel);
        var shared = new EffortSupport(
            basic.Declared.Concat(standard.Declared).Concat(best.Declared)
                .Concat(owner.Providers.Rows.SelectMany(r => EffortsFor(r.Name, r.Model).Declared))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            basic.Accepted || standard.Accepted || best.Accepted);

        FillEfforts(EffortChoices, shared, ReasoningEffort);
        FillEfforts(BasicEfforts, basic, BasicEffort);
        FillEfforts(StandardEfforts, standard, StandardEffort);
        FillEfforts(BestEfforts, best, BestEffort);

        // the whole explanation lives in the tooltip beside the label: the dialog stays a form
        EffortHint = "Each tier offers the efforts its own provider publishes for the model it sends to " +
                     "(OpenRouter: reasoning.supported_efforts; a provider that namespaces its metadata nests the same " +
                     "list under that namespace, as reasoning.supported); a model that publishes " +
                     "none gets the common names. Sent as reasoning_effort to reasoning models; the empty choice sends " +
                     "nothing. A tier that sets an effort also drops the temperature, which reasoning models reject.";
        RaiseEffortChoices();
    }

    /// <summary>Rebuilds the items, so every dropdown repoints at the choice that matches its stored string.</summary>
    void RaiseEffortChoices() =>
        Notify(nameof(ReasoningEffortChoice), nameof(BasicEffortChoice), nameof(StandardEffortChoice), nameof(BestEffortChoice));

    static EffortChoice FindEffort(ObservableCollection<EffortChoice> choices, string? value)
    {
        var wanted = value?.Trim() ?? "";
        return choices.FirstOrDefault(c => string.Equals(c.Value, wanted, StringComparison.OrdinalIgnoreCase))
               ?? choices[0];
    }

    // A ComboBox nulls its SelectedItem while the item list is rebuilt; that is not a user choice,
    // so the setters ignore null and keep the stored string.
    public EffortChoice ReasoningEffortChoice
    {
        get => FindEffort(EffortChoices, ReasoningEffort);
        set { if (value is not null) ReasoningEffort = value.Value; }
    }

    public EffortChoice BasicEffortChoice
    {
        get => FindEffort(BasicEfforts, BasicEffort);
        set { if (value is not null) BasicEffort = value.Value; }
    }

    public EffortChoice StandardEffortChoice
    {
        get => FindEffort(StandardEfforts, StandardEffort);
        set { if (value is not null) StandardEffort = value.Value; }
    }

    public EffortChoice BestEffortChoice
    {
        get => FindEffort(BestEfforts, BestEffort);
        set { if (value is not null) BestEffort = value.Value; }
    }

    partial void OnReasoningEffortChanged(string? value) => OnPropertyChanged(nameof(ReasoningEffortChoice));
    partial void OnBasicEffortChanged(string? value) => OnPropertyChanged(nameof(BasicEffortChoice));
    partial void OnStandardEffortChanged(string? value) => OnPropertyChanged(nameof(StandardEffortChoice));
    partial void OnBestEffortChanged(string? value) => OnPropertyChanged(nameof(BestEffortChoice));

    /// <summary>The stashed published price for the tier route the dialog shows; Save applies it.</summary>
    internal bool HasPublishedPrices { get; private set; }
    internal double PublishedInput { get; private set; }
    internal double PublishedOutput { get; private set; }

    /// <summary>
    /// Reads the published price for the model the dialog's current tier route sends to into a
    /// dialog-local stash; <see cref="SettingsViewModel"/>.Save applies it, so Cancel leaves the
    /// live prices untouched.
    /// </summary>
    void ApplyPublishedPrices()
    {
        HasPublishedPrices = false;
        // the tier's own provider, not simply the first: prices belong to the model actually sent
        var (providerName, model) = settings.Tier switch
        {
            QualityTier.Basic => (BasicProvider, BasicModel),
            QualityTier.Best => (BestProvider, BestModel),
            _ => (StandardProvider, StandardModel),
        };
        var row = owner.Providers.Row(providerName);
        if (row is null) return;
        // the model the tier actually sends: the slot's own, else the row's default model
        var id = SentModel(model, row);
        if (CatalogApi.PublishedPriceFor(row.ChatCatalog, id) is not { } price) return;
        (PublishedInput, PublishedOutput, HasPublishedPrices) = (price.Input, price.Output, true);
        owner.Providers.AppendStatus($" Prices from the listing: {price.Input} in / {price.Output} out per 1M tokens.");
    }

    /// <summary>Rebuilds the slot pickers from the fresh listings, then the slot labels.</summary>
    void RefreshSlotModels()
    {
        ProvidersViewModel.Fill(BasicModels, owner.Providers.Labels(owner.Providers.ChatIds));
        ProvidersViewModel.Fill(StandardModels, owner.Providers.Labels(owner.Providers.ChatIds));
        ProvidersViewModel.Fill(BestModels, owner.Providers.Labels(owner.Providers.ChatIds));
        ProvidersViewModel.Fill(SpeechModels, owner.Providers.Labels(owner.Providers.SpeechIds));
        ProvidersViewModel.Fill(SoundModels, owner.Providers.Labels(owner.Providers.SoundIds));

        // nothing chosen yet: the first sound model that declares voices is the useful default
        var sound = owner.Providers.Row(SoundProvider)?.SoundCatalog ?? Array.Empty<ModelListing>();
        if (string.IsNullOrWhiteSpace(SoundModel) && sound.Count > 0)
            SoundModel = (sound.FirstOrDefault(m => m.Voices is { Count: > 0 }) ?? sound[0]).Id;
        RaiseSlotTexts();
    }
}
