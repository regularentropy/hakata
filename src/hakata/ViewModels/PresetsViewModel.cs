using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// Translation presets: named option bundles, one selected at a time. <see cref="Items"/> mirrors the
/// settings list object-for-object (that list is what gets serialised) and is what the picker binds.
/// </summary>
public sealed partial class PresetsViewModel : Observable
{
    readonly SettingsService settingsService;
    readonly AppSettings settings;
    readonly Action? optionsApplied;
    readonly Action<string>? report;
    readonly bool manageOnly;

    public PresetsViewModel(SettingsService settingsService, Action? optionsApplied = null,
        Action<string>? report = null, bool manageOnly = false)
    {
        this.settingsService = settingsService;
        this.optionsApplied = optionsApplied;
        this.report = report;
        this.manageOnly = manageOnly;
        settings = settingsService.Settings;
        Items = new(settings.Options.Presets);
        selectedPreset = settings.Options.ActivePreset;
    }

    /// <summary>Every preset in stored order — the same objects <see cref="TranslationOptions.Presets"/> holds.</summary>
    public ObservableCollection<TranslationPreset> Items { get; }

    [ObservableProperty] private TranslationPreset? selectedPreset;
    [ObservableProperty] private string newPresetName = "";
    [ObservableProperty] private bool deleteArmed;

    public bool HasPreset => SelectedPreset is not null;

    public string DeleteText => DeleteArmed ? "Confirm?" : "Delete";
    public string DeleteTip => DeleteArmed
        ? "Click again to delete this preset"
        : "Delete the selected preset";

    /// <summary>Names the context box after the preset it currently edits.</summary>
    public string ContextLabel => SelectedPreset is { } preset
        ? $"Context for \u201c{preset.Name}\u201d (not translated, only used to disambiguate)"
        : "Context (not translated, only used to disambiguate)";

    public string? Context
    {
        get => SelectedPreset?.Context ?? settings.Options.Context;
        set
        {
            var text = value ?? "";
            if (SelectedPreset is { } preset)
            {
                if (preset.Context == text) return;
                Mutate(_ => preset.Context = text);
            }
            else
            {
                if (settings.Options.Context == text) return;
                Mutate(s => s.Options.Context = text);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>True when a preset is selected and the live options differ from its bundle.</summary>
    public bool IsModified
    {
        get
        {
            if (SelectedPreset is not { } preset) return false;
            var o = settings.Options;
            return o.Formality != preset.Formality || o.Complexity != preset.Complexity || o.Style != preset.Style
                || o.SpeakerGender != preset.SpeakerGender || o.AddresseeGender != preset.AddresseeGender;
        }
    }

    partial void OnSelectedPresetChanged(TranslationPreset? value)
    {
        DeleteArmed = false;
        // the Settings tab only manages presets: picking a row there to rename or move it must not
        // rewrite the live options or the active preset of the window that is translating
        if (!manageOnly)
        {
            if (value is { } preset)
            {
                // selecting applies the preset's whole option bundle; the id (not the name) is what
                // persists, so a later rename cannot silently deselect it. The bundle goes through the
                // one write path, so it is applied and saved in the same step.
                Mutate(s =>
                {
                    s.Options.Formality = preset.Formality;
                    s.Options.Complexity = preset.Complexity;
                    s.Options.Style = preset.Style;
                    s.Options.SpeakerGender = preset.SpeakerGender;
                    s.Options.AddresseeGender = preset.AddresseeGender;
                    s.Options.Preset = preset.Id;
                });
                optionsApplied?.Invoke(); // the chrome combos repaint from the settings they now hold
            }
            else
            {
                Mutate(s => s.Options.Preset = ""); // clearing leaves the option values alone
            }
        }
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(HasPreset));
        OnPropertyChanged(nameof(IsModified));
        ClearPresetCommand.NotifyCanExecuteChanged();
        DeletePresetCommand.NotifyCanExecuteChanged();
        RenamePresetCommand.NotifyCanExecuteChanged();
        NotifyMoveAndSave();
    }

    partial void OnNewPresetNameChanged(string value)
    {
        AddPresetCommand.NotifyCanExecuteChanged();
        RenamePresetCommand.NotifyCanExecuteChanged();
    }

    partial void OnDeleteArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(DeleteTip));
    }

    // hotkeys (Ctrl+1..9, Ctrl+P) reach the picker even when the options panel is hidden
    public void SelectIndex(int index)
    {
        if (index >= 0 && index < Items.Count) SelectedPreset = Items[index];
    }

    public void CycleNext()
    {
        if (Items.Count == 0) return;
        var i = SelectedPreset is { } selected ? Items.IndexOf(selected) : -1;
        SelectedPreset = Items[(i + 1) % Items.Count]; // IndexOf -1 (nothing selected) wraps to the first
    }

    /// <summary>Re-reads the settings list (another window may have edited it) and re-resolves the selection.
    /// Items updates in place — a Reset would null the Settings tab's bound ListBox selection (#0049 class).</summary>
    public void Reload()
    {
        CollectionSync.InPlace(Items, settings.Options.Presets); // in place: a Reset would null the bound selection (#0049)
        SelectedPreset = settings.Options.ActivePreset;
        // a rename or an edit in the other window leaves the label stale when the selection object is unchanged
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(IsModified));
    }

    /// <summary>The chrome edited an option by hand — the marker is computed, so just republish it.</summary>
    public void NoteOptionsEdited() => OnPropertyChanged(nameof(IsModified));

    bool CanAddPreset => !string.IsNullOrWhiteSpace(NewPresetName);
    bool CanRenamePreset => SelectedPreset is not null && !string.IsNullOrWhiteSpace(NewPresetName);

    /// <summary>Stores the context box and the current tone options under a name; an existing name is
    /// overwritten instead of duplicated. The live options go in, not the preset defaults: selecting the
    /// new row applies its bundle, so defaults here would wipe the user's settings (#0078).</summary>
    [RelayCommand(CanExecute = nameof(CanAddPreset))]
    void AddPreset()
    {
        var name = NewPresetName.Trim();
        var preset = Items.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            preset = new TranslationPreset { Name = name };
            Items.Add(preset);
            settings.Options.Presets.Add(preset);
        }
        var o = settings.Options;
        preset.Context = Context ?? "";
        preset.Formality = o.Formality;
        preset.Complexity = o.Complexity;
        preset.Style = o.Style;
        preset.SpeakerGender = o.SpeakerGender;
        preset.AddresseeGender = o.AddresseeGender;
        NewPresetName = "";
        DeleteArmed = false;
        SelectedPreset = preset;
        TrySave();
    }

    /// <summary>Renames in place: the id (what settings.json references) and the selection survive.</summary>
    [RelayCommand(CanExecute = nameof(CanRenamePreset))]
    void RenamePreset()
    {
        if (SelectedPreset is not { } preset) return;
        var name = NewPresetName.Trim();
        if (Items.Any(p => !ReferenceEquals(p, preset) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            report?.Invoke("A preset with that name already exists");
            return;
        }
        preset.Name = name;
        NewPresetName = "";
        OnPropertyChanged(nameof(ContextLabel));
        TrySave();
    }

    /// <summary>Deleting is not undoable, so the first click only arms the button.</summary>
    [RelayCommand(CanExecute = nameof(HasPreset))]
    void DeletePreset()
    {
        if (SelectedPreset is not { } preset) return;
        if (!DeleteArmed)
        {
            DeleteArmed = true;
            return;
        }

        Items.Remove(preset);
        settings.Options.Presets.Remove(preset);
        DeleteArmed = false;
        SelectedPreset = null;
        TrySave();
    }

    [RelayCommand(CanExecute = nameof(HasPreset))]
    void ClearPreset()
    {
        DeleteArmed = false;
        SelectedPreset = null;
    }

    bool CanMoveUp => SelectedPreset is { } up && Items.IndexOf(up) > 0;

    bool CanMoveDown
    {
        get
        {
            if (SelectedPreset is not { } down) return false;
            var i = Items.IndexOf(down);
            return i >= 0 && i < Items.Count - 1;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    void MoveDown() => Move(1);

    void Move(int delta)
    {
        if (SelectedPreset is not { } preset) return;
        var from = settings.Options.Presets.IndexOf(preset);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= settings.Options.Presets.Count) return;
        // Items and the settings list hold the same objects in the same order, so both move together
        settings.Options.Presets.RemoveAt(from);
        settings.Options.Presets.Insert(to, preset);
        Items.Move(from, to); // Move keeps the selection bound to the same object; RemoveAt nulls it
        SelectedPreset = preset; // the bound picker can still drop SelectedItem on Move — re-assert it
        NotifyMoveAndSave();
    }

    void NotifyMoveAndSave()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        TrySave();
    }

    public string ToJson() => JsonSerializer.Serialize(Items, AppJson.Options);

    /// <summary>Adds entries whose id is unknown and whose name is free (ignore case); returns how many landed.</summary>
    public int MergeJson(string json)
    {
        TranslationPreset[] entries;
        try { entries = JsonSerializer.Deserialize<TranslationPreset[]>(json, AppJson.Options) ?? []; }
        catch (JsonException) { return 0; } // garbage in, nothing merged
        var added = 0;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            // a nameless entry cannot be shown in the picker and Normalize drops it on the next load, so it is not merged at all
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (entry.Id.Length > 0 && settings.Options.Presets.Any(p => p.Id == entry.Id)) continue;
            if (settings.Options.Presets.Any(p => string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (entry.Id.Length == 0) entry.Id = Guid.NewGuid().ToString("n");
            settings.Options.Presets.Add(entry);
            Items.Add(entry);
            added++;
        }
        TrySave();
        return added;
    }

    /// <summary>The one write path for a settings-owned value: apply and save in the same step, so a
    /// crash cannot lose the change. A failed save is reported, never thrown out of a setter.</summary>
    void Mutate(Action<AppSettings> apply) =>
        settingsService.TryMutate(apply, "Could not save presets: ", report);

    /// <summary>A save that fails (locked file, full disk) is reported, never thrown out of a command.</summary>
    void TrySave() => settingsService.TrySave("Could not save presets: ", report);
}
