using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// The providers tab: the provider rows, their model listings and the reload / probe that fills them.
/// Slots and efforts stay with <see cref="SlotsViewModel"/>, speech and saving with <see cref="SettingsViewModel"/>; this class calls back
/// into it through the internal On* methods whenever the list changes what a slot would route to.
/// </summary>
public sealed partial class ProvidersViewModel : Observable
{
    static readonly TimeSpan TypeDebounce = TimeSpan.FromMilliseconds(400);
    // the shared client's own timeout is 5 minutes; a hanging endpoint must not pin the dialog that long
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    readonly SettingsViewModel owner;
    readonly HttpClient http;
    CancellationTokenSource? modelsCts;

    [ObservableProperty] private string modelStatus = "";
    [ObservableProperty] private bool modelsLoading;
    [ObservableProperty] private bool showKey;

    /// <summary>Every provider; the list is the only place one lives, and a slot that names none uses the first.</summary>
    public ObservableCollection<ProviderRow> Rows { get; } = [];

    public bool KeyRevealed => ShowKey;
    public string KeyToggleText => ShowKey ? "Hide" : "Show";
    public MaterialIconKind KeyIcon => ShowKey ? MaterialIconKind.VisibilityOff : MaterialIconKind.Visibility;

    internal ProvidersViewModel(SettingsViewModel owner, HttpClient http, AppSettings settings)
    {
        this.owner = owner;
        this.http = http;
        foreach (var provider in settings.Providers) Rows.Add(new ProviderRow(this, provider));
        // the dialog never opens on a bare list: a first row is always there to be typed into
        if (Rows.Count == 0) Rows.Add(new ProviderRow(this, new ProviderConfig { Name = NextProviderName() }));
    }

    /// <summary>The provider a name refers to; a slot that names none, or a name that is gone, gets the first.</summary>
    internal ProviderRow? Row(string? providerName) =>
        ProviderConfig.FirstNamed(Rows, providerName, r => r.Label, null);

    internal static string Url(ProviderRow? row) => row?.BaseUrl?.Trim() ?? "";

    internal IReadOnlyList<string> ChatIds(ProviderRow row) => row.AllChatIds;

    /// <summary>Speech ids: the audio catalog when the provider publishes one, else its chat ids.</summary>
    internal IReadOnlyList<string> SpeechIds(ProviderRow row) =>
        row.SpeechCatalog.Count > 0 ? row.SpeechCatalog.Select(m => m.Id).ToList() : row.AllChatIds;

    internal IReadOnlyList<string> SoundIds(ProviderRow row) => row.SoundCatalog.Select(m => m.Id).ToList();

    internal int Holders(Func<ProviderRow, IReadOnlyList<string>> kind, string id) =>
        Rows.Count(r => kind(r).Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Every provider's ids under one picker, prefixed only where an id is not unique.</summary>
    internal IEnumerable<string> Labels(Func<ProviderRow, IReadOnlyList<string>> kind)
    {
        // one lookup per call, not a Holders scan per id: the pickers rebuild on every keystroke in an
        // extra-model box, and the per-id scan was O(rows² · ids²) per rebuild (#0073)
        var holders = Rows.SelectMany(row => kind(row).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => (row.Label, Id: id)))
            .ToLookup(pair => pair.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
            foreach (var id in kind(row))
                yield return holders[id].Count() > 1 ? Label(row.Label, id) : id;
    }

    /// <summary>What a slot shows: the id alone when one provider offers it, "provider · id" when several do.</summary>
    internal static string Label(string? provider, string? model) => provider?.Trim() is { Length: > 0 } name
        ? model?.Trim() is { Length: > 0 } id ? name + " · " + id : name
        : model?.Trim() ?? "";

    internal static void Fill(ObservableCollection<string> target, IEnumerable<string> wanted)
    {
        var list = wanted.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var id in list) target.Add(id);
    }

    internal void AppendStatus(string text) => ModelStatus += text;

    [RelayCommand]
    void AddProvider()
    {
        var row = new ProviderRow(this, new ProviderConfig { Name = NextProviderName() });
        Rows.Add(row);
        owner.OnProvidersChanged();
    }

    string NextProviderName()
    {
        for (var i = Rows.Count + 1; ; i++)
        {
            var candidate = "Provider " + i;
            if (!Rows.Any(r => ProviderConfig.SameName(r.Name, candidate))) return candidate;
        }
    }

    internal void RemoveProvider(ProviderRow row)
    {
        Rows.Remove(row);
        owner.OnProvidersChanged();
        QueueModelsReload();
    }

    internal void RenameProvider(ProviderRow row, string? oldName, string? newName)
    {
        // the slots store names, not rows: a rename has to carry them or they silently reroute
        owner.OnProviderRenamed(oldName, newName);
        QueueModelsReload(); // the pickers label ids with the provider names
    }

    internal void ProviderConnectionChanged()
    {
        QueueModelsReload();
        owner.OnConnectionChanged();
    }

    internal void ProviderModelChanged() => owner.OnDefaultModelChanged();

    /// <summary>A row's extra-models line changed: the pickers widen, the slot labels follow.</summary>
    internal void OnExtraModelsChanged() => owner.OnExtraModelsChanged();

    [RelayCommand]
    void ToggleKey() => ShowKey = !ShowKey;

    partial void OnShowKeyChanged(bool value)
    {
        Notify(nameof(KeyRevealed), nameof(KeyToggleText), nameof(KeyIcon));
        foreach (var row in Rows) row.RaiseKeyVisibility();
    }

    // ---- the model lists -------------------------------------------------

    /// <summary>Fires a models reload on its own cancellation chain, so a newer request cancels the older.</summary>
    void Reload(TimeSpan delay, bool probe)
    {
        CancelCurrent();
        var cts = new CancellationTokenSource();
        modelsCts = cts;
        _ = ReloadModelsAsync(delay, probe, cts);
    }

    /// <summary>Cancels a pending reload — closing the dialog must not leave its probe running.</summary>
    internal void CancelPending() => CancelCurrent();

    void CancelCurrent()
    {
        var cts = modelsCts;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* a finished run already disposed its own source */ }
    }

    /// <summary>Reloads the listings when a connection field settles, so the user picks instead of typing ids.</summary>
    internal void QueueModelsReload() => Reload(TypeDebounce, probe: false);

    [RelayCommand]
    void RefreshModels() => QueueModelsReload();

    /// <summary>Explicit probe: same request as the reload, but it reports the connection itself.</summary>
    [RelayCommand]
    void TestConnection() => Reload(TimeSpan.Zero, probe: true);

    /// <summary>One row's fetch outcome: null chat means the chat call failed and the row keeps its old list.</summary>
    readonly record struct RowCatalogs(ProviderRow Row, IReadOnlyList<ModelListing>? Chat,
        IReadOnlyList<ModelListing> Speech, IReadOnlyList<ModelListing> Sound, string? PickedDefault);

    /// <summary>Fetches and parses every configured row's catalogs, failures included; throws as the walk does.</summary>
    async Task<(List<string> failures, List<RowCatalogs> fetched)> FetchCatalogsAsync(CancellationToken ct)
    {
        var failures = new List<string>();
        var fetched = new List<RowCatalogs>();
        foreach (var row in Rows.ToList()) // a provider added mid-load must not break the walk
        {
            var url = Url(row);
            if (url.Length == 0)
            {
                fetched.Add(new RowCatalogs(row, Array.Empty<ModelListing>(),
                    Array.Empty<ModelListing>(), Array.Empty<ModelListing>(), null));
                continue;
            }
            var apiKey = row.ApiKey?.Trim() ?? "";
            using var rowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rowCts.CancelAfter(RequestTimeout);
            IReadOnlyList<ModelListing>? chat = null;
            string? pickedDefault = null;
            try
            {
                var catalog = await CatalogApi.ListModelCatalogAsync(http, url, apiKey, rowCts.Token);
                chat = catalog;
                // a pasted link is enough: the provider's own first model fills an empty field
                if (string.IsNullOrWhiteSpace(row.Model) && catalog.Count > 0)
                    pickedDefault = catalog[0].Id;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { failures.Add($"{row.Label}: timed out"); }
            catch (Exception ex) { failures.Add($"{row.Label}: {ex.Message}"); }
            IReadOnlyList<ModelListing> speech, sound;
            try
            {
                speech = await AudioCatalogAsync(url, apiKey, type: null, rowCts.Token);
                sound = await AudioCatalogAsync(url, apiKey, type: "tts", rowCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                // the row's own timeout fired after its chat catalog answered, so rowCts is already
                // expired: record the failure and keep walking instead of aborting the other providers
                var timeout = $"{row.Label}: timed out";
                if (!failures.Contains(timeout)) failures.Add(timeout); // the chat call may have recorded it already
                speech = sound = Array.Empty<ModelListing>();
            }
            fetched.Add(new RowCatalogs(row, chat, speech, sound, pickedDefault));
        }
        return (failures, fetched);
    }

    /// <summary>Puts the fetched catalogs on the rows; returns the labels of the defaults picked for empty model fields.</summary>
    List<string> ApplyCatalogs(List<RowCatalogs> fetched)
    {
        var picked = new List<string>();
        foreach (var f in fetched)
        {
            if (f.Chat is { } chat)
            {
                f.Row.ChatCatalog = chat;
                Fill(f.Row.Models, chat.Select(m => m.Id));
                if (f.PickedDefault is { Length: > 0 } id)
                {
                    f.Row.Model = id;
                    picked.Add(Label(f.Row.Label, f.Row.Model));
                }
            }
            f.Row.SpeechCatalog = f.Speech;
            f.Row.SoundCatalog = f.Sound;
        }
        return picked;
    }

    async Task ReloadModelsAsync(TimeSpan delay, bool probe, CancellationTokenSource myCts)
    {
        var ct = myCts.Token;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct); // debounce typing in the URL/key fields
            var configured = Rows.Where(r => Url(r).Length > 0).ToList();
            if (configured.Count == 0)
            {
                // first run: an empty field is not a failed connection
                ModelStatus = probe ? "Enter the provider's API link first." : "";
                return;
            }
            ModelsLoading = true;
            ModelStatus = probe ? "Connecting…" : "Loading models…";

            var (failures, fetched) = await FetchCatalogsAsync(ct);
            var picked = ApplyCatalogs(fetched);

            var verb = probe ? "Connected" : "Loaded";
            var first = configured[0];
            ModelStatus = failures.Count == 0
                ? Rows.Count == 1
                    ? first.Models.Count == 0
                        ? $"{verb} — the provider listed no models. Type a model id."
                        : $"{verb} — {first.Models.Count} models available. Pick below or type an id."
                    : $"{verb} — {configured.Count} providers, {string.Join(", ", configured.Select(r => r.Label))}."
                : probe
                    ? "Connection failed: " + string.Join(" ", failures)
                    : "Model list unavailable — type a model id, or press Test to check the connection.";
            if (picked.Count > 0) ModelStatus += $" Using {string.Join(", ", picked)}.";

            owner.OnCatalogsLoaded(); // rebuilds the slot pickers, prices and efforts from the fresh listings
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            ModelStatus = probe
                ? "Connection failed: timed out"
                : "Model list unavailable — type a model id, or press Test to check the connection.";
        }
        catch (Exception ex)
        {
            // the silent auto-load must not shout a raw provider error at a first-run user
            ModelStatus = probe
                ? "Connection failed: " + ex.Message
                : "Model list unavailable — type a model id, or press Test to check the connection.";
        }
        finally
        {
            // a superseded reload must not clear the spinner while the newer one still runs; each run
            // retires its own source here, so the token is never used after it is disposed
            if (ReferenceEquals(modelsCts, myCts))
            {
                ModelsLoading = false;
                modelsCts = null;
            }
            myCts.Dispose();
        }
    }

    /// <summary>An audio listing, or an empty one: a provider without those routes (OpenAI, Groq) answers 404.</summary>
    async Task<IReadOnlyList<ModelListing>> AudioCatalogAsync(string url, string apiKey, string? type, CancellationToken ct)
    {
        try
        {
            return await CatalogApi.ListAudioModelsAsync(http, url, apiKey, ct, type);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Array.Empty<ModelListing>();
        }
    }
}
