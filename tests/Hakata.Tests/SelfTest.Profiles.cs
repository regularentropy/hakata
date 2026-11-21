using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.ViewModels;

namespace Hakata;

/// <summary>Dependency-free checks for the parts that break silently: endpoints, SSE, prompts, JSON parsing, history.</summary>
public static partial class SelfTest
{
    /// <summary>The two startup-path guards: a group/world-writable HAKATA_HOME is detected by the
    /// mode check, and a failed active-profile pointer write is retried, then logged instead of
    /// thrown or swallowed. Skipped on Windows, which has no Unix file mode.</summary>
    static void StartupGuardCases()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        const UnixFileMode Full = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            File.SetUnixFileMode(dir, Full);
            Check(Program.HomeWritableByOthers(dir), "startup guard: a 0777 HAKATA_HOME is writable by others");
            File.SetUnixFileMode(dir, OwnerOnly);
            Check(!Program.HomeWritableByOthers(dir), "startup guard: a 0700 HAKATA_HOME is not writable by others");
            Check(!Program.HomeWritableByOthers(Path.Combine(dir, "missing")), "startup guard: a nonexistent dir is not reported writable");
        }
        finally
        {
            Directory.Delete(dir);
        }

        // the pointer helper must not let a failed write escape, and must leave a note in the log
        // instead. Skipped as root: root writes through a 0500 directory, so the failure cannot be
        // produced here in the first place.
        if (Environment.UserName == "root") return;
        var baseDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var savedCrash = Program.CrashLogPath;
        try
        {
            Profiles.Create(baseDir, "A");
            // read-execute only: even the owner cannot write, which is what a read-only mount looks like
            const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
            File.SetUnixFileMode(baseDir, ReadOnly);
            var log = Path.Combine(Path.GetTempPath(), "hakata-selftest-log-" + Guid.NewGuid().ToString("n"));
            Program.CrashLogPath = log;
            App.SetActivePointerAsync(baseDir, "A").GetAwaiter().GetResult(); // must not throw
            Check(File.Exists(log) && File.ReadAllText(log).Contains("active-profile pointer not updated"),
                "startup guard: a pointer write against a read-only base dir is logged, not thrown");
        }
        finally
        {
            Program.CrashLogPath = savedCrash;
            File.SetUnixFileMode(baseDir, OwnerOnly); // restore write access so the tree can be removed
            Directory.Delete(baseDir, recursive: true);
        }
    }

    static void ProfilesVmCases()
    {
        StartupGuardCases();
        var baseDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var savedHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
        var savedOverride = AppSettings.DirOverride;
        try
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", baseDir);
            Directory.CreateDirectory(baseDir);
            Profiles.Create(baseDir, "A");
            Profiles.Create(baseDir, "B");
            var vm = new ProfilesViewModel();
            Check(vm.Rows.Count == 3, "profiles vm: lists app data + A + B");

            // running in A, A is the launch default
            AppSettings.DirOverride = Profiles.DirOf(baseDir, "A");
            Profiles.SetActive(baseDir, "A");
            vm.Refresh();
            var a = vm.Rows.First(r => r.Name == "A");
            Check(a is { IsDefault: false, IsLaunchDefault: true, IsRunning: true },
                "profiles vm: A is both launch default and running here");

            // switching to the row this instance already runs must say so, not sit there silently
            vm.Selected = a;
            vm.SwitchCommand.Execute(null);
            Check(vm.StatusIsError && vm.Status.Contains("'A'"),
                "profiles vm: switching to the running profile names it in a red error");
            Check(vm.Rows.First(r => r.Name == Profiles.DefaultName) is { IsDefault: true, IsRunning: false, IsLaunchDefault: false },
                "profiles vm: the built-in default row is none of the movable markers");

            // deleting the running profile is refused with a reason, and never arms
            vm.Selected = a;
            vm.DeleteCommand.Execute(null);
            Check(!vm.DeleteArmed && vm.Status.Contains("switch"), "profiles vm: deleting the running profile says switch first");
            vm.DeleteCommand.Execute(null);
            Check(!vm.DeleteArmed && Directory.Exists(Profiles.DirOf(baseDir, "A")!), "profiles vm: a refused delete never arms or removes");

            // set B as the launch default without switching: A is the previous default, still running
            vm.Selected = vm.Rows.First(r => r.Name == "B");
            vm.SetDefaultCommand.Execute(null);
            vm.Refresh();
            a = vm.Rows.First(r => r.Name == "A");
            Check(a is { IsLaunchDefault: false, IsRunning: true }, "profiles vm: after SetDefault(B), A is the previous default, still running");
            vm.Selected = vm.Rows.First(r => r.Name == "B");
            vm.DeleteCommand.Execute(null);
            Check(!vm.DeleteArmed && vm.Status.Contains("plain launch"), "profiles vm: deleting the launch default says set another first");

            // switch the run to B (what App.SwitchProfileAsync does to the dir): A becomes removable
            AppSettings.DirOverride = Profiles.DirOf(baseDir, "B");
            vm.Refresh();
            a = vm.Rows.First(r => r.Name == "A");
            Check(a is { IsLaunchDefault: false, IsRunning: false }, "profiles vm: after switching to B, A is a plain profile");
            vm.Selected = a;
            vm.DeleteCommand.Execute(null);
            Check(vm.DeleteArmed, "profiles vm: delete arms on the previous default once it is not running");
            vm.DeleteCommand.Execute(null);
            Check(!Directory.Exists(Path.Combine(baseDir, "profiles", "A")), "profiles vm: the previous default is deleted after switching away");

            // the app data row can never be deleted, and says why
            vm.Refresh();
            vm.Selected = vm.Rows.First(r => r.Name == Profiles.DefaultName);
            vm.DeleteCommand.Execute(null);
            Check(!vm.DeleteArmed && vm.Status.Contains("built-in"), "profiles vm: deleting the built-in default is refused with a reason");
            Check(vm.Rows.Any(r => r.Name == Profiles.DefaultName && r.IsDefault), "profiles vm: the built-in default survives");

            // a profile running in another instance (its dir guard is held elsewhere) is refused at
            // the VM layer too: red error, and Delete never arms
            var cDir = Profiles.Create(baseDir, "C");
            using (var held = new InstanceGuard())
            {
                Check(held.TryAcquire(cDir), "profiles vm: a throwaway guard takes C's dir (another instance)");
                vm.Refresh();
                var c = vm.Rows.First(r => r.Name == "C");
                Check(c is { Running: true, IsRunning: false }, "profiles vm: C is marked running elsewhere, not running here");
                vm.Selected = c;
                vm.DeleteCommand.Execute(null);
                Check(!vm.DeleteArmed && vm.Status.Contains("running in another instance"),
                    "profiles vm: deleting a profile running in another instance is refused and never arms");
                vm.RenameCommand.Execute(null);
                Check(vm.Status.Contains("running in another instance") && Directory.Exists(cDir),
                    "profiles vm: renaming a profile running in another instance is refused, the dir stays");
            }

            // a successful rename round-trip: the rebuilt row list must leave the rename field on the new name
            vm.Refresh(); // C's guard is gone now: the row must no longer be marked running elsewhere
            vm.Selected = vm.Rows.First(r => r.Name == "C");
            vm.RenameName = "C2";
            vm.RenameCommand.Execute(null);
            Check(vm.Rows.Any(r => r.Name == "C2") && vm.RenameName == "C2",
                "profiles vm: after a successful rename the rename field shows the new name");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", savedHome);
            AppSettings.DirOverride = savedOverride;
            try { Directory.Delete(baseDir, true); } catch (Exception) { }
        }
    }

    static void ProfilesCases()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(baseDir);
            var list = Profiles.List(baseDir);
            Check(list.Count == 1 && list[0].Name == Profiles.DefaultName && list[0].IsDefault
                && list[0].Dir == Path.Combine(baseDir, "profiles", "default"),
                "profiles: a fresh base lists only the default profile, in profiles/default");
            Check(Profiles.ActiveName(baseDir) == Profiles.DefaultName, "profiles: no pointer falls back to default");

            var workDir = Profiles.Create(baseDir, "work");
            Check(Directory.Exists(workDir) && workDir == Path.Combine(baseDir, "profiles", "work"),
                "profiles: create makes the profile directory");
            Check(Profiles.List(baseDir).Count == 2 && Profiles.List(baseDir)[1].Name == "work",
                "profiles: create adds a list entry");
            try { Profiles.Create(baseDir, "work"); Check(false, "profiles: duplicate create is refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: duplicate create is refused"); }
            try { Profiles.Create(baseDir, "default"); Check(false, "profiles: creating 'default' is refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: creating 'default' is refused"); }
            try { Profiles.Create(baseDir, "bad/name"); Check(false, "profiles: invalid names are refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: invalid names are refused"); }

            Profiles.SetActive(baseDir, "work");
            Check(Profiles.ActiveName(baseDir) == "work", "profiles: set-active round-trips");
            Check(File.ReadAllText(Path.Combine(baseDir, "active-profile")).Trim() == "work",
                "profiles: the pointer file names the active profile");
            Check(!Directory.GetFiles(baseDir, "active-profile.*.tmp").Any(),
                "profiles: SetActive leaves no temp file behind");
            Check(Profiles.ResolveActiveDir(null, baseDir) == workDir,
                "profiles: resolve without env uses the active profile");
            Check(Profiles.ResolveActiveDir("/somewhere/else", baseDir) == "/somewhere/else",
                "profiles: resolve prefers the env home");
            Check(Profiles.ResolveActiveDir("", baseDir) == workDir,
                "profiles: an empty env home falls back to the active profile");
            Check(Profiles.ActiveNameForDir(baseDir, workDir) == "work",
                "profiles: the profile is found by its data dir (#0040)");
            Check(Profiles.ActiveNameForDir(baseDir, baseDir) == Profiles.DefaultName,
                "profiles: the base dir maps to the default profile");
            Check(Profiles.ActiveNameForDir(baseDir, Path.Combine(baseDir, "nope")) == Profiles.DefaultName,
                "profiles: an unknown dir maps to the default profile");

            // canonical dirs: trailing separators never matter, case only on Windows
            Check(Profiles.CanonicalDir(workDir + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar) == workDir
                && Profiles.CanonicalDir(workDir) == workDir,
                "profiles: CanonicalDir trims trailing separators");
            if (!OperatingSystem.IsWindows()) // on Windows '/' resolves to the current drive's root ("C:\")
                Check(Profiles.CanonicalDir("/") == "/",
                    "profiles: CanonicalDir keeps a filesystem root instead of emptying it");
            Check(Profiles.CanonicalDir(workDir + Path.AltDirectorySeparatorChar) == workDir,
                "profiles: CanonicalDir trims an alt-separator trailing slash");
            var missingBase = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            Check(Profiles.ResolveActiveDir(null, missingBase) == Profiles.DefaultDir(missingBase),
                "profiles: resolving a base dir that does not exist returns the default dir, never throws");
            Check(Profiles.SameDir(workDir, workDir + Path.DirectorySeparatorChar),
                "profiles: SameDir treats a trailing separator as the same dir");
            if (OperatingSystem.IsWindows())
                Check(Profiles.SameDir(workDir, workDir.ToUpperInvariant()),
                    "profiles: SameDir is case-insensitive on Windows");
            else
                Check(!Profiles.SameDir(workDir, workDir.ToUpperInvariant()),
                    "profiles: SameDir is case-sensitive off Windows");
            Check(Profiles.ActiveNameForDir(baseDir, workDir + Path.DirectorySeparatorChar) == "work",
                "profiles: ActiveNameForDir resolves a dir with a trailing slash");

            File.WriteAllText(Path.Combine(baseDir, "active-profile"), "gone");
            Check(Profiles.ActiveName(baseDir) == Profiles.DefaultName,
                "profiles: a pointer to a missing profile falls back to default");

            Profiles.SetActive(baseDir, "work"); // the rename must follow the pointer only when it points here
            Profiles.Rename(baseDir, "work", "rest");
            Check(!Directory.Exists(workDir) && Directory.Exists(Path.Combine(baseDir, "profiles", "rest")),
                "profiles: rename moves the directory");
            Check(Profiles.ActiveName(baseDir) == "rest", "profiles: rename updates the pointer when active");
            try { Profiles.Rename(baseDir, Profiles.DefaultName, "x"); Check(false, "profiles: renaming default is refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: renaming default is refused"); }
            try { Profiles.Delete(baseDir, "rest"); Check(false, "profiles: deleting the active profile is refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: deleting the active profile is refused"); }
            try { Profiles.Delete(baseDir, Profiles.DefaultName); Check(false, "profiles: deleting default is refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: deleting default is refused"); }

            var spareDir = Profiles.Create(baseDir, "spare");
            Profiles.Delete(baseDir, "spare");
            Check(!Directory.Exists(spareDir) && Profiles.List(baseDir).Count == 2,
                "profiles: a non-active profile is deleted");
            try { Profiles.SetActive(baseDir, "spare"); Check(false, "profiles: activating a deleted profile is refused"); }
            catch (InvalidOperationException) { Check(true, "profiles: activating a deleted profile is refused"); }

            Check(!Profiles.IsValidName(null) && !Profiles.IsValidName("") && !Profiles.IsValidName("default")
                && !Profiles.IsValidName("Default") && !Profiles.IsValidName(new string('a', 33))
                && !Profiles.IsValidName("a/b") && !Profiles.IsValidName("a\\b") && !Profiles.IsValidName("a\0b")
                && !Profiles.IsValidName(".") && !Profiles.IsValidName("..")
                && !Profiles.IsValidName("CON") && !Profiles.IsValidName("com1") && !Profiles.IsValidName("LPT9")
                && !Profiles.IsValidName("-x") && !Profiles.IsValidName(" x") && !Profiles.IsValidName("a b\n")
                && !Profiles.IsValidName("abc.") && !Profiles.IsValidName("con "),
                "profiles: invalid names are rejected (33+ chars, '.', '..', reserved device names, leading '-'/' ', newline, trailing '.' or ' ')");
            Check(Profiles.IsValidName("Work-1") && Profiles.IsValidName("a.b") && Profiles.IsValidName("My Profile")
                && Profiles.IsValidName("a b") && Profiles.IsValidName(new string('a', 32)),
                "profiles: ordinary names pass (32 chars, dots, inner spaces)");

            var suffix = Profiles.InstanceSuffix(baseDir);
            Check(suffix.Length == 17 && suffix[0] == '-' && suffix == Profiles.InstanceSuffix(baseDir),
                $"profiles: the instance suffix is stable and 16 hex chars ('{suffix}')");
            Check(Profiles.InstanceSuffix(baseDir + "x") != suffix,
                "profiles: different dirs get different suffixes");
            Check(Profiles.InstanceSuffix(baseDir + Path.DirectorySeparatorChar) == suffix
                && Profiles.InstanceSuffix(baseDir.ToUpperInvariant()) == suffix,
                "profiles: case- and separator-variant spellings of one dir share a suffix");

            Directory.CreateDirectory(Path.Combine(baseDir, "profiles", "default"));
            Check(Profiles.List(baseDir).Count(e => !e.IsDefault) == 1,
                "profiles: a subdir literally named 'default' is skipped");

            // M3: only validly-named subdirs are listed, ordinal-sorted, the default entry first;
            // on Linux a and A are two profiles and both list, on Windows the second create throws
            var ordDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            try
            {
                Directory.CreateDirectory(ordDir);
                Profiles.Create(ordDir, "b");
                Profiles.Create(ordDir, "A");
                if (OperatingSystem.IsWindows())
                {
                    try { Profiles.Create(ordDir, "a"); Check(false, "profiles: a case-duplicate create is refused on Windows"); }
                    catch (InvalidOperationException) { Check(true, "profiles: a case-duplicate create is refused on Windows"); }
                }
                else
                {
                    Profiles.Create(ordDir, "a");
                }
                if (!OperatingSystem.IsWindows()) // 'con' is a reserved device name; creating it is only safe off Windows
                    Directory.CreateDirectory(Path.Combine(ordDir, "profiles", "con"));
                var ord = Profiles.List(ordDir);
                if (OperatingSystem.IsWindows())
                    Check(ord.Count == 3 && ord[0].IsDefault && ord[1].Name == "A" && ord[2].Name == "b",
                        "profiles: list is ordinal-sorted with the default first, invalid names skipped (Windows)");
                else
                    Check(ord.Count == 4 && ord[0].IsDefault && ord[1].Name == "A" && ord[2].Name == "a" && ord[3].Name == "b",
                        "profiles: list is ordinal-sorted with the default first, invalid names skipped");
            }
            finally { try { Directory.Delete(ordDir, true); } catch (Exception) { } }

            // M2: name uniqueness is case-insensitive only on Windows; off it, case-variants coexist
            var caseDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            try
            {
                Directory.CreateDirectory(caseDir);
                Profiles.Create(caseDir, "work");
                if (OperatingSystem.IsWindows())
                {
                    try { Profiles.Create(caseDir, "Work"); Check(false, "profiles: creating 'Work' next to 'work' is refused on Windows"); }
                    catch (InvalidOperationException) { Check(true, "profiles: creating 'Work' next to 'work' is refused on Windows"); }
                }
                else
                {
                    var upper = Profiles.Create(caseDir, "Work");
                    Check(Directory.Exists(upper)
                        && Profiles.DirOf(caseDir, "work") == Path.Combine(caseDir, "profiles", "work")
                        && Profiles.DirOf(caseDir, "Work") == upper,
                        "profiles: work and Work are two profiles off Windows, each resolving to its own dir");
                }
            }
            finally { try { Directory.Delete(caseDir, true); } catch (Exception) { } }

            // --profile launch arg (per-instance profile override)
            Check(Profiles.ParseProfileArg(Array.Empty<string>()) is null
                && Profiles.ParseProfileArg(new[] { "--verbose", "-x" }) is null
                && Profiles.ParseProfileArg(new[] { "--profile" }) is null,
                "profiles: ParseProfileArg is null for empty, no-match and dangling args");
            Check(Profiles.ParseProfileArg(new[] { "--profile", "rest" }) == "rest"
                && Profiles.ParseProfileArg(new[] { "--profile=rest" }) == "rest"
                && Profiles.ParseProfileArg(new[] { "--verbose", "--profile=rest", "extra" }) == "rest",
                "profiles: ParseProfileArg reads both forms and ignores other args");
            var restDir = Path.Combine(baseDir, "profiles", "rest");
            Check(Profiles.DirOf(baseDir, "rest") == restDir
                && (OperatingSystem.IsWindows() ? Profiles.DirOf(baseDir, "REST") == restDir : Profiles.DirOf(baseDir, "REST") is null),
                "profiles: DirOf resolves the profile's dir (ignore-case only on Windows)");
            Check(Profiles.DirOf(baseDir, "missing") is null, "profiles: DirOf is null for a missing name");

            // the dir this run lives in (AppSettings.Dir) is protected even when it is not the
            // pointer-active profile, so a UI mistake can never delete/rename the live data dir
            var savedOverride = AppSettings.DirOverride;
            File.WriteAllText(Path.Combine(baseDir, "active-profile"), Profiles.DefaultName); // 'rest' not pointer-active
            try
            {
                AppSettings.DirOverride = restDir;
                try { Profiles.Delete(baseDir, "rest"); Check(false, "profiles: deleting the live run dir is refused"); }
                catch (InvalidOperationException) { Check(true, "profiles: deleting the live run dir is refused"); }
                try { Profiles.Rename(baseDir, "rest", "moved"); Check(false, "profiles: renaming the live run dir is refused"); }
                catch (InvalidOperationException) { Check(true, "profiles: renaming the live run dir is refused"); }
                Check(Directory.Exists(restDir) && !Directory.Exists(Path.Combine(baseDir, "profiles", "moved")),
                    "profiles: the refused rename left the directory in place");
            }
            finally
            {
                AppSettings.DirOverride = savedOverride;
                Profiles.SetActive(baseDir, "rest"); // the surrounding checks expect the pointer back on 'rest'
            }

            // a profile whose dir guard another live instance holds cannot be deleted or renamed:
            // the recursive delete / move would destroy that instance's files
            var otherDir = Profiles.Create(baseDir, "other");
            using (var held = new InstanceGuard())
            {
                Check(held.TryAcquire(otherDir), "profiles: a throwaway guard takes the other profile's dir");
                try { Profiles.Delete(baseDir, "other"); Check(false, "profiles: deleting a profile running in another instance is refused"); }
                catch (InvalidOperationException) { Check(true, "profiles: deleting a profile running in another instance is refused"); }
                try { Profiles.Rename(baseDir, "other", "x"); Check(false, "profiles: renaming a profile running in another instance is refused"); }
                catch (InvalidOperationException) { Check(true, "profiles: renaming a profile running in another instance is refused"); }
                Check(Directory.Exists(otherDir), "profiles: the held profile's directory survives the refused delete/rename");
            }

            // Program.Main's precedence as pure calls: env home > --profile arg > active-profile pointer
            var argName = Profiles.ParseProfileArg(new[] { "--profile=rest" });
            var argDir = argName is null ? null : Profiles.DirOf(baseDir, argName);
            File.WriteAllText(Path.Combine(baseDir, "active-profile"), Profiles.DefaultName); // pointer disagrees with the arg
            Check(argDir == restDir && Profiles.ResolveActiveDir(null, baseDir) == Profiles.DefaultDir(baseDir)
                && (argDir ?? Profiles.ResolveActiveDir(null, baseDir)) == restDir,
                "profiles: Main precedence — the --profile arg wins over the pointer, default resolves to profiles/default");
            Check(Profiles.ResolveActiveDir("/env/home", baseDir) == "/env/home",
                "profiles: Main precedence — the env home wins over the arg");

            // #0039: the profile dir must travel via DirOverride, never by overwriting HAKATA_HOME,
            // or Profiles.BaseDir starts returning the active profile's own directory
            var savedHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
            try
            {
                Environment.SetEnvironmentVariable("HAKATA_HOME", baseDir);
                AppSettings.DirOverride = Path.Combine(baseDir, "profiles", "rest");
                Check(AppSettings.Dir == AppSettings.DirOverride,
                    "profiles: DirOverride wins for AppSettings.Dir (#0039)");
                Check(Profiles.BaseDir == baseDir,
                    "profiles: DirOverride does not leak into Profiles.BaseDir (#0039)");
                AppSettings.DirOverride = null;
                Check(AppSettings.Dir == baseDir, "profiles: clearing DirOverride restores the env home");
            }
            finally
            {
                AppSettings.DirOverride = null;
                Environment.SetEnvironmentVariable("HAKATA_HOME", savedHome);
            }
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch (Exception) { }
        }
    }

    static void InstanceGuardCases()
    {
        var root = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        var dirC = Path.Combine(root, "c");
        var g1 = new InstanceGuard();
        var g2 = new InstanceGuard();
        var g3 = new InstanceGuard();
        try
        {
            Check(g1.TryAcquire(dirA) && g1.CurrentDir == dirA,
                "instance guard: first acquire succeeds and tracks the dir");
            Check(!g1.TryAcquire(dirA) && g1.CurrentDir == dirA,
                "instance guard: re-acquiring the held dir fails in-process and keeps the guard");
            Check(!g2.TryAcquire(dirA), "instance guard: another guard cannot take the held dir");
            Check(g2.TryAcquire(dirB) && g2.CurrentDir == dirB,
                "instance guard: another guard takes a free dir");
            Check(!g1.TrySwitch(dirB) && g1.CurrentDir == dirA,
                "instance guard: switching to a taken dir fails and the old guard stays");
            Check(g1.TrySwitch(dirC) && g1.CurrentDir == dirC,
                "instance guard: switching to a free dir succeeds");
            Check(g1.TrySwitch(dirC + Path.DirectorySeparatorChar) && g1.CurrentDir == dirC
                && !g2.TryAcquire(dirC) && g2.CurrentDir == dirB,
                "instance guard: TrySwitch to the same dir with a trailing slash is a true no-op (guard kept)");
            Check(g3.TryAcquire(dirA) && g3.CurrentDir == dirA,
                "instance guard: the dir released by the switch is free again");
            Check(!g2.TrySwitch(dirC) && g2.CurrentDir == dirB,
                "instance guard: switching to a dir now held elsewhere fails and keeps the old guard");
            if (OperatingSystem.IsWindows())
                Check(g1.ShowEvent is not null, "instance guard: Windows exposes the show event");
            else
                Check(g1.ShowEvent is null, "instance guard: non-Windows has no show event");
            g1.Release();
            Check(g1.CurrentDir is null && g1.ShowEvent is null, "instance guard: release clears the state");
            Check(g3.TryAcquire(dirC), "instance guard: release frees the dir for another guard");
            var dirD = Path.Combine(root, "d");
            Check(!InstanceGuard.IsHeld(dirD), "IsHeld: a missing dir reports free");
            // g2 holds dirB, g3 holds dirC, dirA was released by g3's acquire
            Check(InstanceGuard.IsHeld(dirB) && InstanceGuard.IsHeld(dirC) && !InstanceGuard.IsHeld(dirA),
                "IsHeld: held dirs report held, a free dir does not");
            g2.Release();
            Check(!InstanceGuard.IsHeld(dirB), "IsHeld: a released dir reports free");
            // M15: a profile dir created at acquire is owner-only from the start, before app.lock lands
            var dirE = Path.Combine(root, "e");
            Check(g2.TryAcquire(dirE) && g2.CurrentDir == dirE,
                "instance guard: acquire creates and takes a fresh dir");
            if (!OperatingSystem.IsWindows())
            {
                Check(File.GetUnixFileMode(dirE) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                    "instance guard: a freshly created profile dir is 0700 off Windows");
                Check(File.GetUnixFileMode(Path.Combine(dirE, "app.lock")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "instance guard: app.lock is created 0600 off Windows");
            }
            // #0053: two launches racing on a fresh dir — the loser must never delete the winner's
            // dir (its app.lock and data); only a still-empty dir the loser created may be dropped
            for (var i = 0; i < 30; i++)
            {
                var raceDir = Path.Combine(root, "race" + i);
                using var a = new InstanceGuard();
                using var b = new InstanceGuard();
                var t1 = Task.Run(() => a.TryAcquire(raceDir));
                var t2 = Task.Run(() => b.TryAcquire(raceDir));
                Task.WaitAll(t1, t2);
                Check(t1.Result != t2.Result
                      && File.Exists(Path.Combine(raceDir, "app.lock"))
                      && InstanceGuard.IsHeld(raceDir),
                    "instance guard: a racing loser never deletes the winner's lock (#0053)");
            }
        }
        finally
        {
            g1.Dispose(); g2.Dispose(); g3.Dispose();
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    /// <summary>The active-profile pointer is read capped on the open handle: a normal pointer
    /// resolves the named profile, while an oversized, symlinked, non-UTF-8 or otherwise
    /// unreadable pointer falls back to the default profile without being read whole.</summary>
    static void PointerReadCaps()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var pointer = Path.Combine(baseDir, "active-profile");
        try
        {
            Directory.CreateDirectory(baseDir);
            Profiles.Create(baseDir, "A");
            const int cap = Profiles.MaxPointerBytes;

            // a normal pointer (with trailing newline) resolves the named profile
            File.WriteAllText(pointer, "A\n");
            Check(Profiles.ActiveName(baseDir) == "A" && Profiles.ResolveActiveDir(null, baseDir) == Profiles.DirOf(baseDir, "A"),
                "profile pointer: a normal pointer resolves the named profile");

            // exactly at the cap (name padded with spaces, trimmed on read) still reads and resolves
            File.WriteAllText(pointer, "A" + new string(' ', cap - 1));
            Check(new FileInfo(pointer).Length == cap && Profiles.ActiveName(baseDir) == "A",
                "profile pointer: a pointer exactly at the cap still reads and resolves");

            // one byte over the cap: refused before any buffer is sized, default fallback
            File.WriteAllText(pointer, "A" + new string(' ', cap));
            Check(new FileInfo(pointer).Length == cap + 1 && Profiles.ActiveName(baseDir) == Profiles.DefaultName
                && Profiles.ResolveActiveDir(null, baseDir) == Profiles.DefaultDir(baseDir),
                "profile pointer: a pointer over the cap falls back to the default without being read");
            try { StoreFile.ReadAllTextCapped(pointer, cap); Check(false, "profile pointer: the capped helper refuses an over-cap pointer"); }
            catch (InvalidDataException) { Check(true, "profile pointer: the capped helper refuses an over-cap pointer"); }

            // a non-UTF-8 pointer within the cap reads as garbage and falls back like an unknown name
            File.WriteAllBytes(pointer, [0xFF, 0xFE, 0x00, 0x41]);
            Check(Profiles.ActiveName(baseDir) == Profiles.DefaultName,
                "profile pointer: a non-UTF-8 pointer falls back to the default");

            // a pointer naming a profile that does not exist falls back (unchanged behavior)
            File.WriteAllText(pointer, "Z");
            Check(Profiles.ActiveName(baseDir) == Profiles.DefaultName,
                "profile pointer: a pointer naming a missing profile falls back to the default");

            // a symlink to an oversized file is capped too: the open handle's length follows the link
            if (!OperatingSystem.IsWindows())
            {
                var big = Path.Combine(baseDir, "big.bin");
                using (var fs = File.Create(big)) fs.SetLength(8L * 1024 * 1024);
                File.Delete(pointer);
                File.CreateSymbolicLink(pointer, big);
                Check(Profiles.ActiveName(baseDir) == Profiles.DefaultName
                    && Profiles.ResolveActiveDir(null, baseDir) == Profiles.DefaultDir(baseDir),
                    "profile pointer: a symlinked pointer to an oversized file falls back to the default");
                File.Delete(pointer);
            }

            // a directory in place of the pointer is not a readable file: default fallback
            Directory.CreateDirectory(pointer);
            Check(Profiles.ActiveName(baseDir) == Profiles.DefaultName,
                "profile pointer: a directory in place of the pointer falls back to the default");
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch (Exception) { }
        }
    }
}
