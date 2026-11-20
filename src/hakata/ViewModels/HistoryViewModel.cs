using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.ViewModels;

/// <summary>The history sidebar: the visible entries, the favorites filter, search and the star / delete / clear actions.</summary>
public sealed partial class HistoryViewModel : Observable
{
    readonly History history;
    readonly Action<string> report;
    readonly Action<HistoryEntry> restore;

    public HistoryViewModel(History history, Action<string> report, Action<HistoryEntry> restore)
    {
        this.history = history;
        this.report = report;
        this.restore = restore;
        Refresh();
    }

    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    [ObservableProperty] private bool favoritesOnly;
    [ObservableProperty] private HistoryEntry? selectedEntry;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string emptyText = "";
    [ObservableProperty] private string? searchText;
    [ObservableProperty] private bool clearArmed;

    HistoryEntry? current;

    /// <summary>
    /// The entry the star acts on. Kept apart from the list selection so a fresh translation can be starred,
    /// and notified so the header star and the sidebar button follow it.
    /// </summary>
    public HistoryEntry? Current
    {
        get => current;
        set
        {
            if (ReferenceEquals(current, value)) return;
            current = value;
            OnPropertyChanged();
            RaiseActionState();
        }
    }

    public bool IsAllFilter => !FavoritesOnly;

    /// <summary>Total spent across every stored entry, not just the visible filter: it is the running bill.</summary>
    public string SpendLabel { get; private set; } = "";
    public bool HasSpend { get; private set; }
    public bool IsFavoritesFilter => FavoritesOnly;
    public bool CanAct => current is not null || Selection.Count > 0;

    /// <summary>Every row picked in the list: Shift+click extends, Ctrl+click toggles one.</summary>
    public IReadOnlyList<HistoryEntry> Selection { get; private set; } = [];

    public void SetSelection(IEnumerable<HistoryEntry> entries)
    {
        Selection = [.. entries];
        RaiseActionState();
    }

    public MaterialIconKind FavoriteIcon => current is { Favorite: true } ? MaterialIconKind.Star : MaterialIconKind.StarOutline;
    public string FavoriteText => current is { Favorite: true } ? "Unfavorite" : "Favorite";
    // short label: the rail is 210px and "Clear history" beside "Unfavorite" overflows the row
    public string ClearButtonText => ClearArmed ? "Confirm?" : "Clear";

    /// <summary>Says how much the armed click will delete, instead of a bare "confirm".</summary>
    public string ClearTip => ClearArmed
        ? $"Click again to delete all {history.Entries.Count} entries"
        : "Clear the whole history (asks once)";

    void RaiseActionState() => Notify(nameof(CanAct), nameof(FavoriteIcon), nameof(FavoriteText));

    partial void OnFavoritesOnlyChanged(bool value) => Refresh();

    partial void OnSearchTextChanged(string? value) => Refresh();

    partial void OnClearArmedChanged(bool value) =>
        Notify(nameof(ClearButtonText), nameof(ClearTip));

    partial void OnSelectedEntryChanged(HistoryEntry? value)
    {
        DisarmClear();
        if (value is not null) restore(value);
    }

    public void Refresh()
    {
        // a save that failed (disk full, denied) is reported once, exactly like a failed load
        if (history.SaveNotice is { } saveNotice)
        {
            history.ClearSaveNotice();
            report(saveNotice);
        }

        var query = SearchText?.Trim() ?? "";
        var wanted = history.Entries.Where(entry =>
            (!FavoritesOnly || entry.Favorite) && (query.Length == 0 || Matches(entry, query))).ToList();
        CollectionSync.InPlace(Entries, wanted); // in place: a Reset would null the bound SelectedItem (#0049)
        var spent = history.Entries.Sum(e => e.Cost);
        HasSpend = spent > 0;
        SpendLabel = HasSpend ? Pricing.CostLabel(spent) + " spent" : "";
        Notify(nameof(SpendLabel), nameof(HasSpend), nameof(ClearTip));

        IsEmpty = Entries.Count == 0;
        EmptyText = query.Length > 0
            ? "No history matches that search."
            : FavoritesOnly
                ? "No favorites yet. Translate something and star it."
                : "Nothing here yet… Try translating something!";
        DisarmClear();
    }

    static bool Matches(HistoryEntry entry, string query) =>
        entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Output.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.Route.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        // the stored extras are searched too: a word from an insight or an alternative finds its entry
        entry.AlternativesJson.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        entry.InsightsJson.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-renders the relative timestamps without rebuilding the list.</summary>
    public void RefreshAgo()
    {
        foreach (var entry in Entries) entry.RaiseAgoChanged();
    }

    void DisarmClear()
    {
        if (ClearArmed) ClearArmed = false;
    }

    [RelayCommand] void ShowAll() => Show(favoritesOnly: false);
    [RelayCommand] void ShowFavorites() => Show(favoritesOnly: true);

    void Show(bool favoritesOnly)
    {
        FavoritesOnly = favoritesOnly;
        // A toggle button writes IsChecked locally when clicked, so republish even if the filter did not change.
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsFavoritesFilter));
    }

    /// <summary>Clearing every entry is not undoable, so the first click only arms the button.</summary>
    [RelayCommand]
    void Clear()
    {
        if (!ClearArmed)
        {
            ClearArmed = true;
            report("Click Clear again to delete all history");
            return;
        }

        history.Clear();
        Current = null;
        SetSelection([]); // ghost rows must not stay actionable after the clear
        Refresh();
        report("History cleared");
    }

    [RelayCommand]
    void ToggleFavorite()
    {
        var entries = Targets();
        if (entries.Count == 0)
        {
            report("Nothing to favorite yet");
            return;
        }

        // One drive for the whole batch: anything still unstarred means star them all, else unstar all.
        var star = entries.Any(e => !e.Favorite);
        foreach (var entry in entries) entry.Favorite = star;
        AfterFavoriteChange();
        report(entries.Count == 1
            ? star ? "Added to favorites" : "Removed from favorites"
            : star ? $"Added {entries.Count} entries to favorites" : $"Removed {entries.Count} entries from favorites");
    }

    [RelayCommand]
    void ToggleFavoriteFor(HistoryEntry? entry)
    {
        if (entry is not null) Toggle(entry);
    }

    /// <summary>
    /// Speech is billed outside the translation (the mic before it, the sound button after it), so the amount
    /// is added to the entry the panel is showing. False when there is no such entry.
    /// </summary>
    public bool AddCost(double cost) => current is not null && AddCost(current, cost);

    /// <summary>Adds the amount to the given entry; false when it is gone from the history by now.</summary>
    public bool AddCost(HistoryEntry entry, double cost)
    {
        if (cost <= 0 || !history.Entries.Contains(entry)) return false;
        return CommitCost(entry, cost);
    }

    /// <summary>Shared tail of AddCost/AddUsage: the entry is already validated and updated.</summary>
    bool CommitCost(HistoryEntry entry, double cost)
    {
        entry.Cost += cost; // the setter raises Cost/CostShort/HasCost for the row's chip
        history.Save();
        Refresh(); // the spend label follows; the chip follows through the setter's raise
        return true;
    }

    /// <summary>Adds an extra request's usage to the given entry; unlike AddCost the tokens count even
    /// when no price is configured, so the cost may stay 0. False when the entry is gone by now.</summary>
    public bool AddUsage(HistoryEntry entry, TokenUsage usage, double cost)
    {
        if (!history.Entries.Contains(entry)) return false;
        entry.PromptTokens += usage.PromptTokens;
        entry.CompletionTokens += usage.CompletionTokens;
        if (usage.Estimated) entry.UsageEstimated = true;
        return CommitCost(entry, cost);
    }

    /// <summary>
    /// The romanisation arrives after the entry is stored, so the row is written again with it. Nobody
    /// else sees this text: the sidebar row shows the source, the panes read it back on restore.
    /// </summary>
    public void SaveTransliteration(HistoryEntry entry, string source, string target)
    {
        entry.TranslitSource = source;
        entry.TranslitTarget = target;
        history.Save();
    }

    /// <summary>Alternatives arrive after the entry is stored, so the row is written again with them;
    /// a restored entry shows them without paying for the same request twice. No-op when the entry
    /// is gone from the history by now.</summary>
    public void SaveAlternatives(HistoryEntry? entry, string description, List<Alternative> items)
    {
        if (entry is null || !history.Entries.Contains(entry)) return;
        entry.AlternativesDescription = description;
        entry.Alternatives = items;
        history.Save();
    }

    /// <summary>Word insights likewise ride the entry they were read from.</summary>
    public void SaveInsights(HistoryEntry? entry, List<WordInsight> insights)
    {
        if (entry is null || !history.Entries.Contains(entry)) return;
        entry.Insights = insights;
        history.Save();
    }

    void Toggle(HistoryEntry entry)
    {
        entry.Favorite = !entry.Favorite;
        AfterFavoriteChange();
        report(entry.Favorite ? "Added to favorites" : "Removed from favorites");
    }

    void AfterFavoriteChange()
    {
        history.Save();
        Refresh();
        RaiseActionState();
    }

    /// <summary>The rows the star and delete buttons act on: the whole selection, else the picked row.</summary>
    List<HistoryEntry> Targets()
    {
        if (Selection.Count > 0) return [.. Selection];
        if (SelectedEntry is { } picked) return [picked];
        return current is { } entry ? [entry] : [];
    }

    [RelayCommand]
    void DeleteSelected()
    {
        var entries = Targets();
        if (entries.Count == 0)
        {
            report("Select an entry first");
            return;
        }

        foreach (var entry in entries) history.Entries.Remove(entry);
        if (current is not null && entries.Contains(current)) Current = null;
        history.Save();
        SelectedEntry = null;
        SetSelection([]); // notify: CanAct must not stay true on the deleted rows
        Refresh();
        report(entries.Count == 1 ? "Deleted from history" : $"Deleted {entries.Count} entries from history");
    }
}
