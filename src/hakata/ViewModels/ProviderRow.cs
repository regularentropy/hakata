using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// One provider, edited in place. A rename carries the slots that named the old name along, so a tier
/// never silently falls back to another provider.
/// </summary>
public sealed partial class ProviderRow : Observable
{
    readonly ProvidersViewModel owner;

    public ProviderRow(ProvidersViewModel owner, ProviderConfig config)
    {
        this.owner = owner;
        name = config.Name;
        baseUrl = config.BaseUrl;
        apiKey = config.ApiKey;
        model = config.Model;
        foreach (var id in config.ExtraModels ?? []) ExtraModels.Add(new ExtraModelRow(this, id));
        // after the seeding loop, so construction itself fires nothing
        Models.CollectionChanged += (_, _) => allChatIds = null; // a refilled listing invalidates the cache
        ExtraModels.CollectionChanged += (_, _) =>
        {
            allChatIds = null;
            owner.OnExtraModelsChanged();
        };
    }

    [ObservableProperty] private string? name;
    [ObservableProperty] private string? baseUrl;
    [ObservableProperty] private string? apiKey;

    /// <summary>This provider's default model: what a slot naming it and no model sends.</summary>
    [ObservableProperty] private string? model;

    /// <summary>What this provider's /models listing answered; the pickers offer these ids.</summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>One hand-entered optional model id, edited in its own box on the provider card.</summary>
    public sealed partial class ExtraModelRow : Observable
    {
        readonly ProviderRow owner;

        public ExtraModelRow(ProviderRow owner, string? id)
        {
            this.owner = owner;
            this.id = id;
        }

        [ObservableProperty] private string? id;

        /// <summary>The catalog the box completes against: this provider's /models ids.</summary>
        public ObservableCollection<string> Models => owner.Models;

        partial void OnIdChanged(string? value) => owner.OnExtraModelEdited();

        [RelayCommand]
        void Remove() => owner.RemoveExtraModel(this);
    }

    /// <summary>The hand-entered optional models: the pickers offer them even when /models doesn't list them.</summary>
    public ObservableCollection<ExtraModelRow> ExtraModels { get; } = [];

    IReadOnlyList<string>? allChatIds;

    /// <summary>Every id the pickers may offer for this provider: the /models listing first, then the extras.
    /// Cached: the pickers read this per id while rebuilding on every keystroke in an extra-model box,
    /// so recomputing it per access made typing lag on large catalogs.</summary>
    public IReadOnlyList<string> AllChatIds => allChatIds ??= BuildAllChatIds();

    IReadOnlyList<string> BuildAllChatIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>(Models.Count);
        foreach (var id in Models) if (seen.Add(id)) ids.Add(id);
        foreach (var id in ParseIds(ExtraModels.Select(r => r.Id))) if (seen.Add(id)) ids.Add(id);
        return ids;
    }

    /// <summary>Trims, drops empties and dedupes the given ids, keeping order.</summary>
    internal static List<string> ParseIds(IEnumerable<string?>? ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in ids ?? [])
        {
            var id = raw?.Trim() ?? "";
            if (id.Length > 0 && seen.Add(id)) list.Add(id);
        }
        return list;
    }

    // the listings behind the pickers: the ids come from Models, the efforts and prices from here.
    // They stay per provider on purpose: two providers can publish the same model id and disagree.
    public IReadOnlyList<ModelListing> ChatCatalog { get; set; } = [];
    public IReadOnlyList<ModelListing> SpeechCatalog { get; set; } = [];
    public IReadOnlyList<ModelListing> SoundCatalog { get; set; } = [];

    /// <summary>The name as the dialog and the stored slots spell it.</summary>
    public string Label => (Name?.Trim() ?? "") is { Length: > 0 } name ? name : "unnamed provider";

    // the mask is one switch for the whole list, but a row can only see its own data context
    public bool KeyRevealed => owner.KeyRevealed;
    public string KeyToggleText => owner.KeyToggleText;
    public MaterialIconKind KeyIcon => owner.KeyIcon;
    public IRelayCommand ToggleKeyCommand => owner.ToggleKeyCommand;

    /// <summary>Called by the list when the reveal switch flips, so every row's mask follows it.</summary>
    internal void RaiseKeyVisibility() =>
        Notify(nameof(KeyRevealed), nameof(KeyToggleText), nameof(KeyIcon));

    /// <summary>A key travels as a bearer header, so an http endpoint sends it unencrypted.</summary>
    public string HttpWarn =>
        BaseUrl?.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
            ? "plain HTTP — the API key travels unencrypted" : "";

    public ProviderConfig ToConfig() => new()
    {
        Name = Name?.Trim() ?? "",
        BaseUrl = BaseUrl?.Trim() ?? "",
        ApiKey = ApiKey?.Trim() ?? "",
        Model = Model?.Trim() ?? "",
        ExtraModels = ParseIds(ExtraModels.Select(r => r.Id)),
    };

    partial void OnNameChanged(string? oldValue, string? newValue) => owner.RenameProvider(this, oldValue, newValue);
    partial void OnBaseUrlChanged(string? value)
    {
        owner.ProviderConnectionChanged();
        OnPropertyChanged(nameof(HttpWarn));
    }
    partial void OnApiKeyChanged(string? value) => owner.ProviderConnectionChanged();
    partial void OnModelChanged(string? value) => owner.ProviderModelChanged();

    // extras only widen the pickers: not a connection change, so no reload of the listings.
    // An Id edit is not a collection change, so it invalidates and notifies here; add/remove of a
    // row is covered by the ExtraModels.CollectionChanged subscription in the ctor.
    internal void OnExtraModelEdited()
    {
        allChatIds = null;
        owner.OnExtraModelsChanged();
    }

    internal void RemoveExtraModel(ExtraModelRow row) => ExtraModels.Remove(row);

    [RelayCommand]
    void AddExtraModel() => ExtraModels.Add(new ExtraModelRow(this, ""));

    [RelayCommand]
    void Remove() => owner.RemoveProvider(this);
}
