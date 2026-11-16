using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>The header model switcher: swap the model the current tier sends without opening Settings
/// (a provider may refuse a text with one model and accept it with another). The menu lists only
/// the user-added models — the provider's default and its optional extras — not the /models listing.</summary>
public sealed partial class ModelMenuViewModel : Observable
{
    readonly SettingsService settingsService;
    readonly Func<QualityTier> tier;
    readonly Action<string> setStatus;
    readonly Action refreshHeaderLabel;

    public ModelMenuViewModel(SettingsService settingsService, Func<QualityTier> tier,
        Action<string> setStatus, Action refreshHeaderLabel)
    {
        this.settingsService = settingsService;
        this.tier = tier;
        this.setStatus = setStatus;
        this.refreshHeaderLabel = refreshHeaderLabel;
    }

    /// <summary>One row of the header model menu; an empty <see cref="ModelChoice.Id"/> means the provider's default.</summary>
    public ObservableCollection<ModelChoice> ModelChoices { get; } = [];

    [ObservableProperty] private string modelMenuTitle = "";
    [ObservableProperty] private string modelMenuStatus = "";
    public bool HasModelMenuStatus => ModelMenuStatus.Length > 0;
    partial void OnModelMenuStatusChanged(string value) => OnPropertyChanged(nameof(HasModelMenuStatus));

    /// <summary>Rebuilds the model menu for the current tier. No network: it lists only what the user
    /// added — the provider's default model and its optional extras, plus a tier override already set.</summary>
    public Task LoadModelMenuAsync()
    {
        if (!settingsService.Settings.HasProvider)
        {
            // the menu then offers only its Settings row
            ModelMenuTitle = "Model";
            ModelMenuStatus = "no provider connected";
            ModelChoices.Clear();
            return Task.CompletedTask;
        }
        ModelMenuTitle = $"Model — {TierName(tier())} tier";
        ModelMenuStatus = "";
        FillModelChoices();
        return Task.CompletedTask;
    }

    void FillModelChoices()
    {
        var settings = settingsService.Settings;
        var route = settings.Route(tier());
        var current = route.Model;
        var connection = settings.Connection(route.Provider);
        var fallback = connection.Model?.Trim() ?? "";
        // only the user-added models, read fresh so a Settings edit shows up on the next open
        var offered = ProviderRow.ParseIds(connection.ExtraModels);
        ModelChoices.Clear();
        // the provider's own default gets its own row (empty id = send the default)
        if (fallback.Length > 0)
            ModelChoices.Add(new ModelChoice("", $"provider default · {fallback}", ProviderConfig.SameName(fallback, current)));
        foreach (var id in offered)
            if (!ProviderConfig.SameName(id, fallback))
                ModelChoices.Add(new ModelChoice(id, id, ProviderConfig.SameName(id, current)));
        // a tier override set earlier still shows as the active row
        if (current.Length > 0 && !ProviderConfig.SameName(current, fallback) &&
            !offered.Any(id => ProviderConfig.SameName(id, current)))
            ModelChoices.Add(new ModelChoice(current, current, true));
    }

    /// <summary>Sets the current tier's model override (empty = the provider's default) through the one save path.</summary>
    public void PickModel(string? id)
    {
        var picked = id?.Trim() ?? "";
        var currentTier = tier();
        try
        {
            settingsService.Mutate(s =>
            {
                switch (currentTier)
                {
                    case QualityTier.Basic: s.BasicModel = picked; break;
                    case QualityTier.Best: s.BestModel = picked; break;
                    default: s.StandardModel = picked; break;
                }
            });
        }
        catch (Exception ex)
        {
            // Mutate applies before it saves, so the pick is already live — only the persist
            // failed; say so and still republish the header label
            refreshHeaderLabel();
            setStatus("Model switched, but settings could not be saved: " + ex.Message);
            return;
        }
        setStatus(picked.Length > 0
            ? $"{TierName(currentTier)} tier now sends {picked}"
            : $"{TierName(currentTier)} tier back on the provider default");
        refreshHeaderLabel(); // republishes the header label
        FillModelChoices();
    }

    static string TierName(QualityTier value) => value switch
    {
        QualityTier.Basic => "Basic",
        QualityTier.Best => "Best",
        _ => "Standard",
    };
}
