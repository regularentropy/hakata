using Avalonia;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata;

class Program
{
    /// <summary>One writer per history database: a second instance would rewrite the table from its own copy.
    /// Windows decides by named mutex (per-profile suffix); elsewhere by an exclusively locked file in the
    /// active profile's data folder.</summary>
    internal const string MutexName = @"Local\Hakata.SingleInstance";

    /// <summary>What a second launch sets to raise the running window instead of starting beside it
    /// (base name; instances append the per-profile suffix).</summary>
    internal const string ShowEventName = @"Local\Hakata.ShowWindow";

    /// <summary>Kernel-object names carry the per-profile suffix, so two profiles can run side by side.</summary>
    internal static string MutexNameFor(string dir) => MutexName + Profiles.InstanceSuffix(dir);

    internal static string ShowEventNameFor(string dir) => ShowEventName + Profiles.InstanceSuffix(dir);

    /// <summary>Windows: sets the show event of the instance running <paramref name="dir"/>, if one exists.</summary>
    internal static void RaiseInstance(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { EventWaitHandle.OpenExisting(ShowEventNameFor(dir)).Set(); } catch (Exception) { }
    }

    /// <summary>Owns the single-instance guard of the active profile's dir; swappable at runtime
    /// for a hot profile switch. The OS releases whatever it holds on exit or crash.</summary>
    internal static readonly InstanceGuard Guard = new();

    /// <summary>The active profile's crash.log; re-pointed on a committed hot-swap. Empty before
    /// Main runs; LogCrash then computes the path from AppSettings.Dir.</summary>
    internal static string CrashLogPath = "";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // The base dir (HAKATA_HOME when set, else %AppData%/hakata) is the structural
        // root — pointer file plus profiles/ only; every profile, "default" included, lives in
        // <base>/profiles/<name>. With HAKATA_HOME set the app keeps dir mode: the env path
        // itself is the data dir, verbatim.
        var envHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
        var dirMode = !string.IsNullOrEmpty(envHome);
        if (dirMode)
        {
            // children inherit these launch flags, but dir mode has no profiles layer under the env
            // path — an inherited MANAGER_ONLY=1 would make a plain first launch show a manager and exit
            Environment.SetEnvironmentVariable("HAKATA_PROFILE_AUTO", null);
            Environment.SetEnvironmentVariable("HAKATA_MANAGER_ONLY", null);
        }
        var baseDir = Profiles.BaseDir;
        // dir mode keeps the env root's mode by design, so check it here: a group/world-writable
        // data dir lets another local user replace settings.json/history.key (rename needs only
        // write permission on the dir, not on the 0600 files). Refuse to start rather than run on
        // a directory another user can tamper with; the note lands in the same vulnerable dir, so
        // the stderr line is the reliable signal.
        if (dirMode && HomeWritableByOthers(baseDir))
        {
            var msg = $"HAKATA_HOME {baseDir} is group/world-writable: another local user can replace settings.json/history.key";
            Console.Error.WriteLine(msg);
            LogNote(Path.Combine(baseDir, "crash.log"), msg);
            return 3;
        }
        if (!dirMode)
        {
            StoreFile.RestrictDir(baseDir); // the base holds the pointer: owner-only off Windows (S1.8)
        }
        // resolve the active profile before anything reads the data dir; publish it via DirOverride,
        // NOT the env var — Profiles.BaseDir must keep reading the original env value.
        // precedence: env home (dir mode) > --profile <name> (this instance only) > the active-profile pointer
        var argName = Profiles.ParseProfileArg(args);
        string? argDir = null;
        if (!dirMode && argName is not null)
        {
            argDir = Profiles.DirOf(baseDir, argName);
            if (argDir is null)
                Console.Error.WriteLine($"unknown profile '{argName}', using the active one");
        }
        var activeDir = dirMode ? envHome! : argDir ?? Profiles.ResolveActiveDir(null, baseDir);
        if (!dirMode)
        {
            AppSettings.DirOverride = activeDir;
            if (argName is not null)
                // a --profile launch must not show the picker, even with the flag inherited from a parent
                // (an unknown name falls through to the active profile, but still counts as --profile)
                Environment.SetEnvironmentVariable("HAKATA_PROFILE_AUTO", null);
            else
                // marks this run as the one allowed to show the profile picker (child relaunches skip it)
                Environment.SetEnvironmentVariable("HAKATA_PROFILE_AUTO", "1");
        }

        CrashLogPath = Path.Combine(activeDir, "crash.log");
        // a relaunched child (profile picker switch) can reach acquire before the parent has exited
        // and released its guard, so retry briefly before signalling the existing instance
        for (var i = 0; ; i++)
        {
            if (Guard.TryAcquire(activeDir)) break;
            if (i >= 30)
            {
                RaiseInstance(activeDir);
                if (!OperatingSystem.IsWindows())
                    Console.Error.WriteLine("Hakata is already running.");
                // dir mode: the env path IS the data dir — there is no profiles layer under it, so a
                // manager would only offer an empty <env>/profiles/default; just exit
                if (dirMode) return 0;
                // the active profile is already running: show the profile manager (all profiles
                // listed, running ones marked) instead of exiting without a window
                Environment.SetEnvironmentVariable("HAKATA_MANAGER_ONLY", "1");
                return Run(args);
            }
            Thread.Sleep(100);
        }
        return Run(args);
    }

    static int Run(string[] args)
    {
        // the app has no dialog to show a crash in: one line per failure in the app data folder
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static void LogCrash(Exception? ex)
    {
        try
        {
            var path = CrashLogPath is { Length: > 0 } captured ? captured : Path.Combine(AppSettings.Dir, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // one file per era: a steady crash loop must not fill the disk
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
            {
                File.Move(path, path + ".old", overwrite: true);
                StoreFile.RestrictFile(path + ".old"); // the rotated log carries the same traces
            }
            // traces can embed a ?api_key= URL: 0600 on the handle before the bytes land
            StoreFile.AppendRestricted(path,
                $"{DateTime.UtcNow:O} {ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}{ex?.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            StoreFile.RestrictFile(path); // stack traces name paths; other users read nothing here
        }
        catch (Exception)
        {
            // a crash that cannot be logged is still a crash
        }
    }

    /// <summary>True when the directory's Unix mode allows group members or other users to write it.
    /// Always false on Windows, which has no Unix file mode, and for a directory that does not exist.
    /// Used for the HAKATA_HOME root in dir mode: write permission on the directory alone is enough
    /// to rename over the owner-only files inside it.</summary>
    internal static bool HomeWritableByOthers(string dir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(dir)) return false;
        var mode = File.GetUnixFileMode(dir);
        return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
    }

    /// <summary>One note line into a log (crash.log): a log that cannot be written must never throw.</summary>
    internal static void LogNote(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            StoreFile.AppendRestricted(path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // a note that cannot be logged is not worth failing the caller for
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
