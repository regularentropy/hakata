using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.Views;

namespace Hakata;

public partial class App : Application
{
    ServiceProvider? services;
    bool switching;

    /// <summary>A raise signal that arrived before a MainWindow existed; drained once one does.</summary>
    static volatile bool pendingRaise;

    /// <summary>True while a profile hot-swap is in flight; a second Switch call must stay silent
    /// instead of flashing a false "Could not switch" error.</summary>
    public bool IsSwitching => switching;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The unlock dialog and the profile picker are the windows before MainWindow exists, so shut down on main-window close, not last-window close.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            // a second launch lands here when the active profile is already running elsewhere: show the
            // profile manager (running profiles marked). Picking a free profile switches this process into
            // it in-process — no relaunch; the manager flag is cleared so the fall-through runs the app.
            if (Environment.GetEnvironmentVariable("HAKATA_MANAGER_ONLY") == "1")
            {
                Environment.SetEnvironmentVariable("HAKATA_MANAGER_ONLY", null);
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the manager must not end the process
                var picked = await ProfilePickerWindow.RunManager(desktop, Profiles.List(Profiles.BaseDir), Profiles.ActiveName(Profiles.BaseDir));
                var newDir = picked is null ? null : Profiles.DirOf(Profiles.BaseDir, picked);
                if (newDir is null || !Program.Guard.TryAcquire(newDir))
                {
                    if (newDir is not null) Program.RaiseInstance(newDir); // taken meanwhile: raise that instance
                    desktop.Shutdown();
                    return;
                }
                AppSettings.DirOverride = newDir;
                Program.CrashLogPath = Path.Combine(newDir, "crash.log");
                await SetActivePointerAsync(Profiles.BaseDir, picked!);
                Environment.SetEnvironmentVariable("HAKATA_NO_PICKER", "1"); // the picker already ran
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            // the picker runs before the password dialog because each profile has its own key file
            else if (Environment.GetEnvironmentVariable("HAKATA_PROFILE_AUTO") == "1"
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HAKATA_NO_PICKER"))
                && Profiles.List(Profiles.BaseDir).Count >= 2)
            {
                var profiles = Profiles.List(Profiles.BaseDir);
                var active = Profiles.ActiveName(Profiles.BaseDir);
                var chosen = await ProfilePickerWindow.PickAsync(null, profiles, active);
                if (chosen is null)
                {
                    // X / Esc / Cancel: the user declined to open a profile here.
                    // ShutdownMode is OnMainWindowClose and MainWindow is still null, so closing the picker
                    // does not end the app on its own — without this, startup falls through and opens the
                    // active profile (and its unlock prompt) anyway (issue #0046).
                    desktop.Shutdown();
                    return;
                }
                if (chosen != active)
                {
                    var newDir = Profiles.DirOf(Profiles.BaseDir, chosen);
                    if (newDir is not null && Program.Guard.TrySwitch(newDir))
                    {
                        // switch in-process: no services or main window exist yet, so there is nothing to
                        // tear down and no second process to wait for — continue below with the new profile
                        AppSettings.DirOverride = newDir;
                        Program.CrashLogPath = Path.Combine(newDir, "crash.log");
                        await SetActivePointerAsync(Profiles.BaseDir, chosen);
                    }
                    else if (newDir is not null)
                    {
                        Program.RaiseInstance(newDir); // another instance owns it: raise its window, keep the active profile
                    }
                }
            }
            services = AppServices.Create();
            var history = services.GetRequiredService<History>();
            if (history.NeedsPassword)
            {
                // unlock before the main window shows; cancelling the prompt closes the app
                // instead of opening it over a locked store
                if (await PasswordWindow.PromptUnlockAsync(null, history.LoadWithPassword) is null)
                {
                    desktop.Shutdown();
                    return;
                }
            }
            var mainWindow = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = mainWindow;
            // assigned after an awaited unlock, the lifetime's one-time ShowMainWindow() already ran — show it here
            if (!mainWindow.IsVisible) mainWindow.Show();
            if (pendingRaise) { pendingRaise = false; RaiseMainWindow(desktop); } // a raise that arrived during startup
            desktop.Exit += (_, _) => services?.Dispose();
            ListenForSecondInstance(desktop);
            CheckForUpdatesInBackground(mainWindow, services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The opt-in startup check. Silent by design: a missing feed, a network failure and "you are up to
    /// date" never touch the window — a found release lights the settings button in the header and opens
    /// the update window.
    /// </summary>
    static async void CheckForUpdatesInBackground(MainWindow window, ServiceProvider services)
    {
        try
        {
            if (!services.GetRequiredService<SettingsService>().Settings.CheckUpdatesOnLaunch) return;
            var metadata = new AppMetadata();
            if (metadata.APILink.Length == 0) return; // no feed configured: nothing to ask
            var update = await new UpdateService(metadata, services.GetRequiredService<HttpClient>())
                .CheckForUpdates();
            if (update is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.ViewModel.Chrome.ShowUpdate(update.Tag);
                UpdaterWindow.ShowFor(window, update, metadata);
            });
        }
        catch (Exception)
        {
            // a background check must never interrupt startup
        }
    }

    /// <summary>Hot profile switch, no restart: swaps the data dir, the single-instance guard and the
    /// whole service graph (settings/history/providers) in-process, then shows a new MainWindow and
    /// closes the old one. A locked target profile prompts for its history password; a refused switch
    /// (target owned by another instance) or a cancelled unlock rolls back to the old profile.</summary>
    public async Task<bool> SwitchProfileAsync(string name)
    {
        var baseDir = Profiles.BaseDir;
        var newDir = Profiles.DirOf(baseDir, name);
        if (newDir is null) return false;
        if (Profiles.SameDir(newDir, AppSettings.Dir)) return true; // already active
        if (switching) return false;
        switching = true;
        try
        {
            if (!Program.Guard.TrySwitch(newDir)) return false; // another instance owns the target; old guard untouched
            var oldDir = AppSettings.Dir;
            AppSettings.DirOverride = newDir;
            // invariant from here on: Program.Guard.CurrentDir == AppSettings.Dir — every failure
            // path below restores the two together, they never diverge
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var oldWindow = desktop?.MainWindow;
            if (oldWindow is not null) oldWindow.IsEnabled = false; // no input on the old window during the swap
            var oldServices = services;
            var newServices = AppServices.Create();
            var committed = false;
            MainWindow? newWindow = null;
            try
            {
                var history = newServices.GetRequiredService<History>();
                if (history.NeedsPassword)
                {
                    if (await PasswordWindow.PromptUnlockAsync(oldWindow, history.LoadWithPassword) is null)
                        throw new OperationCanceledException("unlock cancelled"); // roll back to the old profile
                }
                // build and show the new window before committing: everything up to here is still reversible
                newWindow = newServices.GetRequiredService<MainWindow>();
                // assign MainWindow before closing the old window: with OnMainWindowClose, closing the
                // old one first would shut the app down
                desktop!.MainWindow = newWindow;
                newWindow.Show();
                if (pendingRaise) { pendingRaise = false; RaiseMainWindow(desktop); } // a raise that arrived during the swap
                // commit point: the old graph gets closed and disposed below, so nothing after this
                // line may roll the swap back (a rollback would revive disposed services)
                services = newServices;
                committed = true;
            }
            catch (Exception)
            {
                // past the commit point there is nothing left to roll back to; unreachable today because
                // the commit is the last statement of the try, and it must stay that way
                if (committed) return true;
                // any exception or cancel: roll the dir and the guard back together, keep the old window;
                // re-take the guard FIRST — while it still points at the new profile, the dir must stay
                // there too, or the guard and AppSettings.Dir diverge (invariant above)
                for (var i = 0; ; i++)
                {
                    if (Program.Guard.TrySwitch(oldDir))
                    {
                        AppSettings.DirOverride = oldDir;
                        break;
                    }
                    if (i >= 30)
                    {
                        // the old guard could not be re-taken: the dir follows the guard (invariant above)
                        AppSettings.DirOverride = Program.Guard.CurrentDir;
                        break;
                    }
                    await Task.Delay(100);
                }
                newServices.Dispose();
                try { newWindow?.Close(); } catch (Exception) { /* a window that never showed */ }
                if (desktop is not null && !ReferenceEquals(desktop.MainWindow, oldWindow))
                    desktop.MainWindow = oldWindow;
                if (oldWindow is not null) oldWindow.IsEnabled = true;
                return false;
            }
            try { oldWindow?.Close(); } catch (Exception) { /* the swap stands even if the old window resists */ }
            if (oldServices is not null)
            {
                try
                {
                    if (oldWindow is MainWindow oldMain)
                    {
                        // cancel in-flight work on the live old view model, then wait until it has actually
                        // finished (cancellation ends the requests via their token — no fixed sleep), so
                        // disposing the graph cannot fault a running request into crash.log
                        oldMain.ViewModel.Cancel();
                        await oldMain.ViewModel.WhenIdleAsync();
                    }
                }
                catch (Exception) { /* the swap is committed: a stuck old graph cannot undo it */ }
                oldServices.Dispose();
            }
            Program.CrashLogPath = Path.Combine(newDir, "crash.log"); // the swap is committed: crashes belong to the new profile
            // the swap is committed, so a pointer failure must not report a failed switch — but the next
            // plain launch would open the old profile; retry, then note it in the launch dir's crash log
            await SetActivePointerAsync(baseDir, name);
            return true;
        }
        finally
        {
            switching = false;
        }
    }

    /// <summary>Updates the active-profile pointer with a short retry, and logs a note when it still
    /// fails. The app already runs on the new directory at this point, so a failed write must not
    /// abort the switch — but the next plain launch would silently open the old profile, so the
    /// failure is recorded in the crash log instead of being swallowed.</summary>
    internal static async Task SetActivePointerAsync(string baseDir, string name)
    {
        for (var i = 0; ; i++)
        {
            try { Profiles.SetActive(baseDir, name); break; }
            catch (Exception ex)
            {
                if (i >= 2)
                {
                    var log = Program.CrashLogPath is { Length: > 0 } p ? p : Path.Combine(AppSettings.Dir, "crash.log");
                    Program.LogNote(log, $"active-profile pointer not updated to '{name}': {ex.Message}");
                    break;
                }
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Brings the running window to the front. Before the window exists (startup, mid-swap)
    /// the raise is parked in <see cref="pendingRaise"/> and drained once a window is shown; a raise
    /// on a closed window is best-effort and swallowed.</summary>
    static void RaiseMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not { } window) { pendingRaise = true; return; } // startup: not built yet
        try
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
        catch (Exception) { /* closed or closing: a raise is best-effort */ }
    }

    /// <summary>Windows: a second launch signals this one instead of running beside it and rewriting the
    /// same history. Polls the guard's current show event, so a hot profile switch is picked up within ~250 ms.</summary>
    static void ListenForSecondInstance(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!OperatingSystem.IsWindows()) return;

        var listener = new Thread(() =>
        {
            while (true)
            {
                var show = Program.Guard.ShowEvent;
                if (show is null) { Thread.Sleep(200); continue; }
                bool signaled;
                try { signaled = show.WaitOne(250); }
                catch (ObjectDisposedException) { continue; } // the event was swapped out mid-wait
                if (signaled)
                    Dispatcher.UIThread.Post(() => RaiseMainWindow(desktop));
            }
        }) { IsBackground = true, Name = "second instance listener" };
        listener.Start();
    }
}