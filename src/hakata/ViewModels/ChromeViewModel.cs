using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>The chrome around the translation run: quality tier, panels, theme, and the tone options.
/// Owned by <see cref="TranslateViewModel"/>; the parent is reached through the owner callbacks.</summary>
public sealed partial class ChromeViewModel : Observable
{
    readonly SettingsService settingsService;
    readonly Action<string> setStatus;
    readonly Action refreshProvider;

    AppSettings settings => settingsService.Settings;

    /// <summary>Raised only when one of the five option values really changed — a preset apply writes the
    /// settings directly and must not look like a hand edit (or the preset would instantly read modified).</summary>
    public event Action? OptionsEdited;

    QualityTier tier;

    public ChromeViewModel(SettingsService settingsService, Action<string> setStatus, Action refreshProviderLabel)
    {
        this.settingsService = settingsService;
        this.setStatus = setStatus;
        this.refreshProvider = refreshProviderLabel;
        tier = settings.Tier;
        optionsVisible = settings.OptionsVisible;
        sidebarVisible = settings.SidebarVisible;
        sidebarWidth = settings.SidebarWidth;
        themeIcon = ThemeUi.Apply(settings.Theme);
    }

    public QualityTier Tier
    {
        get => tier;
        private set
        {
            if (!SetProperty(ref tier, value)) return;
            Persist(s => s.Tier = value);
        }
    }

    public bool IsBasicTier => tier == QualityTier.Basic;
    public bool IsStandardTier => tier == QualityTier.Standard;
    public bool IsBestTier => tier == QualityTier.Best;

    [RelayCommand] void SelectBasicTier() => SelectTier(QualityTier.Basic);
    [RelayCommand] void SelectStandardTier() => SelectTier(QualityTier.Standard);
    [RelayCommand] void SelectBestTier() => SelectTier(QualityTier.Best);

    void SelectTier(QualityTier value)
    {
        Tier = value;
        // A toggle button writes IsChecked locally when clicked, so republish even if the tier did not change.
        OnPropertyChanged(nameof(IsBasicTier));
        OnPropertyChanged(nameof(IsStandardTier));
        OnPropertyChanged(nameof(IsBestTier));
        refreshProvider(); // the label names the model the tier will actually send
    }

    [RelayCommand]
    void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    partial void OnSidebarVisibleChanged(bool value) => Persist(s => s.SidebarVisible = value);

    // saved when the window closes: this fires on every frame of a splitter drag
    partial void OnSidebarWidthChanged(double value) => settings.SidebarWidth = value;

    partial void OnOptionsVisibleChanged(bool value) => Persist(s => s.OptionsVisible = value);

    // live chrome state is saved as it changes; a locked file reports instead of killing the toggle
    void Persist(Action<AppSettings> apply) =>
        settingsService.TryMutate(apply, "Could not save settings: ", setStatus);

    public ThemeMode Theme => settings.Theme;

    [RelayCommand]
    void ToggleTheme()
    {
        var next = ThemeUi.Next(settings.Theme);
        Persist(s => s.Theme = next);
        ThemeIcon = ThemeUi.Apply(settings.Theme);
        OnPropertyChanged(nameof(Theme));
    }

    // Options persist as they change: the index in the combo box is the enum value. A no-op write
    // raises nothing, so re-setting the current value cannot mark the selected preset modified.
    public int FormalityIndex
    {
        get => (int)settings.Options.Formality;
        set
        {
            if (value < 0 || settings.Options.Formality == (Formality)value) return;
            Persist(s => s.Options.Formality = (Formality)value);
            OptionsEdited?.Invoke();
        }
    }

    public int ComplexityIndex
    {
        get => (int)settings.Options.Complexity;
        set
        {
            if (value < 0 || settings.Options.Complexity == (Complexity)value) return;
            Persist(s => s.Options.Complexity = (Complexity)value);
            OptionsEdited?.Invoke();
        }
    }

    public int StyleIndex
    {
        get => (int)settings.Options.Style;
        set
        {
            if (value < 0 || settings.Options.Style == (Style)value) return;
            Persist(s => s.Options.Style = (Style)value);
            OptionsEdited?.Invoke();
        }
    }

    // The tone options are three pills each, so the index is set by command and the pills re-read it.
    [RelayCommand]
    void SetFormality(string index)
    {
        if (int.TryParse(index, out var i)) FormalityIndex = i;
        OnPropertyChanged(nameof(FormalityIndex));
    }

    [RelayCommand]
    void SetComplexity(string index)
    {
        if (int.TryParse(index, out var i)) ComplexityIndex = i;
        OnPropertyChanged(nameof(ComplexityIndex));
    }

    [RelayCommand]
    void SetStyle(string index)
    {
        if (int.TryParse(index, out var i)) StyleIndex = i;
        OnPropertyChanged(nameof(StyleIndex));
    }

    public int SpeakerIndex
    {
        get => (int)settings.Options.SpeakerGender;
        set
        {
            if (value < 0 || settings.Options.SpeakerGender == (Gender)value) return;
            Persist(s => s.Options.SpeakerGender = (Gender)value);
            OptionsEdited?.Invoke();
        }
    }

    public int AddresseeIndex
    {
        get => (int)settings.Options.AddresseeGender;
        set
        {
            if (value < 0 || settings.Options.AddresseeGender == (Gender)value) return;
            Persist(s => s.Options.AddresseeGender = (Gender)value);
            OptionsEdited?.Invoke();
        }
    }

    /// <summary>Republishes the five option indices so the combos repaint after a preset applied them.</summary>
    internal void RefreshOptions()
    {
        OnPropertyChanged(nameof(FormalityIndex));
        OnPropertyChanged(nameof(ComplexityIndex));
        OnPropertyChanged(nameof(StyleIndex));
        OnPropertyChanged(nameof(SpeakerIndex));
        OnPropertyChanged(nameof(AddresseeIndex));
    }

    [ObservableProperty] private bool optionsVisible;
    [ObservableProperty] private bool sidebarVisible;
    [ObservableProperty] private double sidebarWidth;
    [ObservableProperty] private MaterialIconKind themeIcon;

    /// <summary>The release a background check found, as "1.3.0"; empty when this build is current.
    /// The header button is the only surface — a startup check must not open a window.</summary>
    [ObservableProperty] private string availableUpdate = "";

    public bool HasUpdate => AvailableUpdate.Length > 0;
    public MaterialIconKind SettingsIcon => HasUpdate ? MaterialIconKind.Update : MaterialIconKind.Cog;

    public string SettingsTip => HasUpdate
        ? $"Version {AvailableUpdate} is available — open Settings, then About"
        : "Provider, models, connection (Ctrl+,)";

    partial void OnAvailableUpdateChanged(string value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(SettingsIcon));
        OnPropertyChanged(nameof(SettingsTip));
    }

    /// <summary>A background check found a newer release: light the settings button.</summary>
    internal void ShowUpdate(string tag) => AvailableUpdate = tag;
}
