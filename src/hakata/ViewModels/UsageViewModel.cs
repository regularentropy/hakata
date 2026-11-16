using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// Token usage of the last run and the per-million prices that turn it into a cost.
/// Prices live in the main window, next to the cost they produce, not in the settings dialog.
/// </summary>
public sealed partial class UsageViewModel : Observable
{
    readonly SettingsService settingsService;
    readonly IModelCatalog provider;
    readonly AppSettings settings;
    TokenUsage? lastUsage;

    public UsageViewModel(SettingsService settingsService, IModelCatalog provider)
    {
        this.settingsService = settingsService;
        this.provider = provider;
        settings = settingsService.Settings;
        // the three price writers can no longer each forget to notify
        settingsService.PricesChanged += OnPricesChanged;

        // OpenRouter-style providers publish per-model prices on /models; fill an untouched pair once, quietly.
        if (settings.InputPricePerMTok <= 0 && settings.OutputPricePerMTok <= 0 && settings.HasProvider)
            _ = AutoPriceAsync();
    }

    [ObservableProperty] private string usageLabel = "";

    public bool HasUsage => UsageLabel.Length > 0;
    public bool NoUsage => !HasUsage;

    partial void OnUsageLabelChanged(string value)
    {
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(NoUsage));
    }

    public decimal InputPrice
    {
        get => (decimal)settings.InputPricePerMTok;
        set => settingsService.SetPrices((double)value, settings.OutputPricePerMTok);
    }

    public decimal OutputPrice
    {
        get => (decimal)settings.OutputPricePerMTok;
        set => settingsService.SetPrices(settings.InputPricePerMTok, (double)value);
    }

    public void SetUsage(TokenUsage usage)
    {
        lastUsage = usage;
        UsageLabel = usage.Label(settings);
    }

    /// <summary>A restored entry renders with the prices frozen on it, not the live ones.</summary>
    public void SetUsage(HistoryEntry entry)
    {
        lastUsage = null;
        UsageLabel = entry.UsageLabel;
    }

    void OnPricesChanged()
    {
        OnPropertyChanged(nameof(InputPrice));
        OnPropertyChanged(nameof(OutputPrice));
        RepriceLastRun();
    }

    /// <summary>A new run (or a cleared window) owns no usage yet.</summary>
    public void Reset()
    {
        lastUsage = null;
        UsageLabel = "";
    }

    void RepriceLastRun()
    {
        if (lastUsage is { } usage) UsageLabel = usage.Label(settings);
    }

    async Task AutoPriceAsync()
    {
        try
        {
            // the prices of the listing the current tier actually sends to
            var route = settings.Route(settings.Tier);
            var catalog = await provider.ListModelCatalogAsync(route);
            if (CatalogApi.PublishedPriceFor(catalog, route.Model) is not { } price) return;
            settingsService.SetPrices(price.Input, price.Output, onlyIfUntouched: true); // the user typed meanwhile wins
        }
        catch
        {
            // pricing is cosmetic; a failed fetch just leaves it manual
        }
    }
}
