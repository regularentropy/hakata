using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.ViewModels;

/// <summary>
/// The profiles tab: the named data directories and the create / rename / delete / switch actions.
/// Switching swaps the profile in-process via App.SwitchProfileAsync — no relaunch.
/// </summary>
public sealed partial class ProfilesViewModel : Observable
{
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private string renameName = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool statusIsError;
    [ObservableProperty] private ProfileRow? selected;
    [ObservableProperty] private bool deleteArmed;

    public ObservableCollection<ProfileRow> Rows { get; } = [];

    public string DeleteText => DeleteArmed ? "Click again to confirm" : "Delete";

    // the armed button means "click again": the accessible name must say that, with the target profile
    public string DeleteAutomationName => DeleteArmed ? $"Delete {Selected?.Name} — click again to confirm" : "Delete profile";

    public ProfilesViewModel() => Refresh();

    partial void OnDeleteArmedChanged(bool value)
    {
        Notify(nameof(DeleteText));
        Notify(nameof(DeleteAutomationName));
    }

    partial void OnSelectedChanged(ProfileRow? value)
    {
        // a new selection must never inherit the armed state aimed at the previous one
        DeleteArmed = false;
        RenameName = value?.Name ?? ""; // the rename field starts from the row's current name
        Notify(nameof(DeleteAutomationName));
    }

    /// <summary>One status line: red only when <paramref name="error"/> (a refusal or a failed action), muted otherwise.</summary>
    void Say(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    /// <summary>True (after a refusal message) while a hot profile swap is mid-flight.</summary>
    bool Switching()
    {
        // mid-swap the outgoing profile's guard is still this process's: act on nothing until it settles
        if (Application.Current is App { IsSwitching: true })
        {
            Say("A profile switch is in progress — try again in a moment", error: true);
            return true;
        }
        return false;
    }

    /// <summary>The selected row, or null (after "select a profile first") when nothing is picked.</summary>
    ProfileRow? RequireSelected(bool say = true)
    {
        var row = Selected;
        if (row is null && say) Say("Select a profile first", error: true);
        return row;
    }

    /// <summary>The three refusals Rename and Delete share: the built-in default, running here, running elsewhere.</summary>
    bool GuardRowUsable(ProfileRow row, string verb)
    {
        if (row.IsDefault) { Say($"The default profile is the built-in one and cannot be {verb}", error: true); return false; }
        if (row.IsRunning) { Say($"This instance is running in '{row.Name}' — switch to another profile first", error: true); return false; }
        if (row.Running) { Say($"'{row.Name}' is running in another instance — close it there first", error: true); return false; }
        return true;
    }

    /// <summary>Runs a profile mutation, reports the outcome, refreshes the list.</summary>
    void Guarded(Action action)
    {
        try { action(); Say(""); }
        catch (Exception ex) { Say(ex.Message, error: true); }
        Refresh();
    }

    /// <summary>Reloads the rows from disk and keeps the selection by name when it survives.</summary>
    public void Refresh()
    {
        var baseDir = Profiles.BaseDir;
        var active = Profiles.ActiveName(baseDir);
        var currentDir = AppSettings.Dir;
        // "running elsewhere" = another process holds the dir's guard; this process's own guard
        // (the profile this instance runs) does not count — same probe as the picker window
        var own = Program.Guard.CurrentDir;
        var previousName = Selected?.Name;
        var wanted = Profiles.List(baseDir)
            .Select(e => new ProfileRow(e.Name, e.Dir, e.IsDefault,
                Profiles.NameEquals(e.Name, active), // the launch default (pointer)
                Profiles.SameDir(e.Dir, currentDir),                              // the profile this instance runs
                InstanceGuard.IsHeld(e.Dir) && !(own is not null && Profiles.SameDir(e.Dir, own)))).ToList();
        // in place: a Reset would null the bound SelectedItem (#0049); records compare by value, so IndexOf matches
        CollectionSync.InPlace(Rows, wanted);
        var rebuilt = previousName is null || !wanted.Any(r => Profiles.NameEquals(r.Name, previousName));
        Selected = Rows.FirstOrDefault(r => Profiles.NameEquals(r.Name, previousName)) ?? Rows.FirstOrDefault();
        if (rebuilt) RenameName = Selected?.Name ?? ""; // the row list changed: the field must name the row that is selected now
        DeleteArmed = false;
    }

    [RelayCommand]
    void Create()
    {
        if (Switching()) return;
        Guarded(() => { Profiles.Create(Profiles.BaseDir, NewName.Trim()); NewName = ""; });
    }

    [RelayCommand]
    void Rename()
    {
        if (Switching()) return;
        if (RequireSelected() is not { } row) return;
        if (!GuardRowUsable(row, "renamed")) return;
        var newName = RenameName.Trim();
        Guarded(() => Profiles.Rename(Profiles.BaseDir, row.Name, newName));
        Selected = Rows.FirstOrDefault(r => Profiles.NameEquals(r.Name, newName));
    }

    [RelayCommand]
    void Delete()
    {
        if (Switching()) return;
        if (RequireSelected() is not { } row) return;
        if (!GuardRowUsable(row, "deleted")) return;
        if (row.IsLaunchDefault) { Say($"A plain launch opens '{row.Name}' — set another profile as the default before deleting it", error: true); return; }
        if (!DeleteArmed)
        {
            DeleteArmed = true; // the folder goes away recursively: never on one click
            Say($"Click Delete again to remove '{row.Name}' and all its data");
            return;
        }
        Guarded(() => Profiles.Delete(Profiles.BaseDir, row.Name));
    }

    [RelayCommand]
    async Task Switch()
    {
        var row = Selected;
        // the launch-default row is a valid switch target even while another profile runs:
        // switching to it is how the previous default becomes deletable
        if (row is null) return;
        if (row.IsRunning) { Say($"This instance is already running '{row.Name}'", error: true); return; }
        if (Application.Current is App app)
        {
            if (app.IsSwitching) return; // a double-tap lands here while the first switch runs: no false error
            Say(""); // a previous refusal must not linger under a switch that is running now
            var ok = await app.SwitchProfileAsync(row.Name);
            if (!ok) Say("Could not switch — the profile may be running in another instance, or the unlock was cancelled", error: true);
        }
        Refresh();
    }

    /// <summary>Makes the selected profile the one a plain launch opens (the active-profile pointer),
    /// without switching now — also fine for a profile running in another instance.</summary>
    [RelayCommand]
    void SetDefault()
    {
        if (Switching()) return;
        var row = Selected;
        if (row is null) return; // any existing profile can be the launch default, including the running one
        try
        {
            Profiles.SetActive(Profiles.BaseDir, row.Name);
            Say($"'{row.Name}' is the default now — the next plain launch opens it");
        }
        catch (Exception ex) { Say(ex.Message, error: true); }
        Refresh();
    }

    /// <summary>Runs the selected profile in a second app instance (--profile: own mutex, pointer untouched).</summary>
    [RelayCommand]
    void OpenNew()
    {
        if (Switching()) return;
        if (RequireSelected() is not { } row) return;
        if (row.IsRunning) { Say("This instance is already running that profile", error: true); return; }
        if (row.Running)
        {
            Program.RaiseInstance(row.Dir);
            Say($"Raised the running instance of '{row.Name}'");
            return;
        }
        Relaunch.Self(profile: row.Name);
    }

    /// <summary>One profile as the list shows it; record equality keeps Refresh() from churning the list.
    /// IsLaunchDefault = the active-profile pointer (what a plain launch opens), IsRunning = the dir
    /// this instance uses — a row can be both, and they are deliberately separate markers.</summary>
    public sealed record ProfileRow(string Name, string Dir, bool IsDefault, bool IsLaunchDefault, bool IsRunning, bool Running);
}
