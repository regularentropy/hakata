using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hakata.Infrastructure;

namespace Hakata.Views;

/// <summary>Picks the profile to open; Open returns the chosen name, Cancel/Esc returns null.
/// In manager mode (a second launch while the active profile is already running elsewhere) the window
/// lists every profile with the running ones marked; a free one picked there is handed back so this
/// launch runs it in-process.</summary>
public partial class ProfilePickerWindow : Window
{
    /// <summary>One row: the profile plus whether another instance currently holds its dir.</summary>
    public sealed record Row(string Name, string Dir, bool IsDefault, bool Running);

    readonly IReadOnlyList<Row> rows;
    readonly bool manager;
    internal readonly TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>XAML designer entry point; the app goes through <see cref="PickAsync"/> at startup and
    /// <see cref="RunManager"/> for a second launch.</summary>
    public ProfilePickerWindow() : this([], Profiles.DefaultName, manager: false) { }

    ProfilePickerWindow(IReadOnlyList<Profiles.ProfileEntry> profiles, string activeName, bool manager)
    {
        this.manager = manager;
        // "running" = another process holds the dir's guard; this process's own guard (the active
        // profile at a startup picker) does not count
        var current = Program.Guard.CurrentDir;
        rows = profiles.Select(e => new Row(e.Name, e.Dir, e.IsDefault,
            InstanceGuard.IsHeld(e.Dir) && !(current is not null && Profiles.SameDir(e.Dir, current)))).ToList();
        InitializeComponent();
        Title = TitleText.Text = manager ? "Profiles" : "Choose profile";
        // the "new instance" button only makes sense in manager mode, when another instance already
        // runs the active profile — at the startup picker nothing runs yet to run beside
        NewInstanceButton.IsVisible = manager;
        HintText.Text = manager
            ? "running = open in another instance · Open raises that window or switches this launch to a free profile · Open in new instance runs it beside"
            : "Open loads the selected profile here";
        // rows are the entries themselves (name + directory + running flag); selection starts at the profile the launch resolves to
        List.ItemsSource = rows;
        var start = rows.ToList().FindIndex(r => Profiles.NameEquals(r.Name, activeName));
        List.SelectedIndex = start >= 0 ? start : 0;
    }

    void OnOpen(object? sender, RoutedEventArgs e) => OpenSelected();

    void OnRowDoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    void OpenSelected()
    {
        if (List.SelectedItem is not Row row) return;
        if (manager)
        {
            // a running profile is raised in its own instance; a free profile is handed to App,
            // which switches this process into it in-process (no relaunch)
            if (row.Running) Program.RaiseInstance(row.Dir);
            completion.TrySetResult(row.Running ? null : row.Name);
            Close();
            return;
        }
        completion.TrySetResult(row.Name);
        Close();
    }

    void OnOpenNew(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Row row) return;
        // a profile already running elsewhere: --profile would just bounce back to the manager,
        // so raise the existing window instead
        if (row.Running) Program.RaiseInstance(row.Dir);
        else Relaunch.Self(profile: row.Name);
        completion.TrySetResult(null);
        Close();
    }

    void OnCancel(object? sender, RoutedEventArgs e) => ModalUi.Cancel(this, completion);

    /// <summary>Shows the picker; resolves with the chosen profile name, or null when cancelled.</summary>
    public static Task<string?> PickAsync(Window? owner, IReadOnlyList<Profiles.ProfileEntry> profiles, string activeName)
    {
        var window = new ProfilePickerWindow(profiles, activeName, manager: false);
        return ModalUi.ShowModal(window, window.completion, owner);
    }

    /// <summary>Manager mode for a second launch: resolves with the picked profile name (null when
    /// cancelled or a running profile was raised). The caller switches the process into the picked
    /// profile in-process, so no relaunch happens here.</summary>
    public static Task<string?> RunManager(IClassicDesktopStyleApplicationLifetime desktop,
        IReadOnlyList<Profiles.ProfileEntry> profiles, string activeName)
    {
        var window = new ProfilePickerWindow(profiles, activeName, manager: true);
        desktop.MainWindow = window;
        return ModalUi.ShowModal(window, window.completion, owner: null);
    }
}
