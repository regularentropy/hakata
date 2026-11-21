using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hakata.Models;

namespace Hakata.Infrastructure;

/// <summary>
/// Named data directories ("profiles"): each lives in <c>&lt;base&gt;/profiles/&lt;name&gt;</c>,
/// including the "default" one (<c>&lt;base&gt;/profiles/default</c>). The base dir holds only the
/// <c>active-profile</c> pointer file, the profiles folder and the crash log, never profile data.
/// </summary>
public static class Profiles
{
    public const string DefaultName = "default";

    /// <summary>The base directory: HAKATA_HOME when set, else %AppData%/hakata.
    /// It is the structural root (pointer + profiles/), not a data dir itself.</summary>
    public static string BaseDir =>
        Environment.GetEnvironmentVariable("HAKATA_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hakata");

    /// <summary>The default profile's directory: <c>&lt;base&gt;/profiles/default</c>.</summary>
    public static string DefaultDir(string baseDir) => Path.Combine(baseDir, "profiles", DefaultName);

    /// <summary>Full path with no trailing directory separator above the root, so two spellings of one
    /// dir compare string-exact (Path.GetFullPath keeps a trailing separator on modern .NET). A
    /// filesystem root ("/", "C:\\") is returned unchanged: trimming it would yield "" or a
    /// drive-relative "C:".</summary>
    public static string CanonicalDir(string dir)
    {
        var full = Path.GetFullPath(dir);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Dir equality after canonicalization; case-insensitive on Windows, where the
    /// filesystem is, ordinal elsewhere.</summary>
    public static bool SameDir(string a, string b) =>
        string.Equals(CanonicalDir(a), CanonicalDir(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Profile names compare case-insensitively only on Windows, where the filesystem is;
    /// on Linux two names differing only by case are two different profiles.</summary>
    public static StringComparison NameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool NameEquals(string? a, string? b) => string.Equals(a, b, NameComparison);

    public sealed record ProfileEntry(string Name, string Dir, bool IsDefault);

    /// <summary>The default profile plus one entry per validly-named subdirectory of &lt;base&gt;/profiles,
    /// ordinal-sorted by name (the built-in default entry stays first). A subdirectory literally named
    /// "default" is skipped: it is the default entry's own dir, and that name is spelled exactly so
    /// on every platform.</summary>
    public static IReadOnlyList<ProfileEntry> List(string baseDir)
    {
        var entries = new List<ProfileEntry>();
        var profiles = Path.Combine(baseDir, "profiles");
        if (Directory.Exists(profiles))
        {
            foreach (var dir in Directory.EnumerateDirectories(profiles))
            {
                var name = Path.GetFileName(dir);
                if (!IsValidName(name) || NameEquals(name, DefaultName)) continue;
                entries.Add(new ProfileEntry(name, dir, IsDefault: false));
            }
        }
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        entries.Insert(0, new ProfileEntry(DefaultName, DefaultDir(baseDir), IsDefault: true));
        return entries;
    }

    /// <summary>Upper bound for the active-profile pointer: it holds one profile name (at most 32
    /// chars), so a bigger file is not a pointer this app wrote and is never read whole.</summary>
    internal const int MaxPointerBytes = 4096;

    /// <summary>The active profile name: the trimmed <c>active-profile</c> pointer when it names an
    /// existing profile, else the default.</summary>
    public static string ActiveName(string baseDir)
    {
        try
        {
            var pointer = Path.Combine(baseDir, "active-profile");
            // capped on the open handle: an oversized or swapped pointer is refused before any
            // buffer is sized and resolves to the default, exactly like an unreadable one
            var name = File.Exists(pointer) ? StoreFile.ReadAllTextCapped(pointer, MaxPointerBytes).Trim() : "";
            if (name.Length > 0 && List(baseDir).Any(e => NameEquals(e.Name, name))) return name;
        }
        catch (Exception) { /* an unreadable pointer resolves to the default */ }
        return DefaultName;
    }

    /// <summary>The profile whose dir is <paramref name="dir"/> — the run's actual data dir, which also
    /// covers --profile launches (they never write the pointer) and hot-swaps (the pointer is persisted
    /// only after the new window exists). Unknown dir → default.</summary>
    public static string ActiveNameForDir(string baseDir, string dir) =>
        List(baseDir).FirstOrDefault(e => SameDir(e.Dir, dir)) is { } hit
            ? hit.Name
            : DefaultName;

    /// <summary>The data directory this run uses: the env home when set, else the active profile's dir.</summary>
    public static string ResolveActiveDir(string? envHome, string baseDir)
    {
        if (!string.IsNullOrEmpty(envHome)) return envHome;
        try
        {
            // S1.3: resolution never throws — a profile that vanished between the pointer read and the
            // lookup, or any resolution failure, falls back to the default dir instead of throwing
            return List(baseDir).FirstOrDefault(e => NameEquals(e.Name, ActiveName(baseDir)))?.Dir
                ?? DefaultDir(baseDir);
        }
        catch (Exception)
        {
            return DefaultDir(baseDir);
        }
    }

    /// <summary>Records the active profile in the pointer file, written atomically like settings.json.</summary>
    public static void SetActive(string baseDir, string name)
    {
        if (!List(baseDir).Any(e => NameEquals(e.Name, name)))
            throw new InvalidOperationException($"profile '{name}' does not exist");
        Directory.CreateDirectory(baseDir);
        var pointer = Path.Combine(baseDir, "active-profile");
        // unique per call: two writers (this instance and the picker path) must not race on one fixed temp name
        StoreFile.WriteAtomic(pointer, tmp =>
        {
            File.WriteAllText(tmp, name);
            StoreFile.RestrictFile(tmp); // restrict before the move: the moved file keeps its mode
        });
        StoreFile.RestrictFile(pointer); // the pointer names private data directories
    }

    // \z, not $: .NET's $ also matches before a trailing '\n', which must not sneak past
    static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,31}\z", RegexOptions.Compiled);

    // Windows reserves these device names before any extension; rejected on every platform so a
    // Linux-legal name never breaks the profile dir when it is copied to a Windows machine
    static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL",
          "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>Profile names become directory names: 1–32 chars matching
    /// <c>^[A-Za-z0-9][A-Za-z0-9 _.-]{0,31}\z</c> (first char alphanumeric, so ".", "..", "-x", " x", "a b\n" all fail),
    /// not ending in '.' or ' ' (Windows silently strips both when creating a directory), not "default",
    /// no separators/invalid chars, and not a Windows reserved device name on the trimmed part
    /// before the first '.' (case-insensitive, on every platform).</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!NamePattern.IsMatch(name)) return false;
        // Windows strips trailing dots and spaces when creating a directory, so "abc." would
        // silently create "abc" and alias an existing profile
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
        // Windows matches a reserved device name before any extension and before a trailing
        // space, so the part before the first '.' is trimmed before the lookup
        return !ReservedDeviceNames.Contains(name.Split('.')[0].Trim());
    }

    /// <summary>Creates &lt;base&gt;/profiles/&lt;name&gt; and returns its directory.</summary>
    public static string Create(string baseDir, string name)
    {
        if (!IsValidName(name))
            throw new InvalidOperationException($"invalid profile name '{name}'");
        if (List(baseDir).Any(e => NameEquals(e.Name, name)))
            throw new InvalidOperationException($"profile '{name}' already exists");
        var dir = Path.Combine(baseDir, "profiles", name);
        Directory.CreateDirectory(dir);
        // the CreateDirectory chain (profiles/, profiles/<name>) lands 0755 off Windows; narrow it (S1.8)
        StoreFile.RestrictDir(Path.Combine(baseDir, "profiles"));
        StoreFile.RestrictDir(dir);
        return dir;
    }

    /// <summary>The shared Rename/Delete guard: the named profile must exist, and must not be the
    /// default, this run's own dir, or held by another live instance — Directory.Move/Delete on it
    /// would destroy live data. The verb ("renamed"/"deleted") only shapes the default-profile message.</summary>
    static ProfileEntry GuardNotRunning(string baseDir, string name, string verb)
    {
        var entry = List(baseDir).FirstOrDefault(e => NameEquals(e.Name, name))
            ?? throw new InvalidOperationException($"profile '{name}' does not exist");
        if (entry.IsDefault)
            throw new InvalidOperationException($"the default profile cannot be {verb}");
        if (SameDir(entry.Dir, AppSettings.Dir))
            throw new InvalidOperationException("the running app uses this profile");
        if (HeldByOtherInstance(entry.Dir))
            throw new InvalidOperationException("the profile is running in another instance");
        return entry;
    }

    /// <summary>Moves a non-default profile's directory; the pointer follows when it was active.</summary>
    public static void Rename(string baseDir, string oldName, string newName)
    {
        var entry = GuardNotRunning(baseDir, oldName, "renamed");
        if (!IsValidName(newName))
            throw new InvalidOperationException($"invalid profile name '{newName}'");
        if (List(baseDir).Any(e => NameEquals(e.Name, newName)))
            throw new InvalidOperationException($"profile '{newName}' already exists");
        // read the pointer before the move: after it the old name no longer resolves
        var wasActive = NameEquals(ActiveName(baseDir), oldName);
        var newDir = Path.Combine(baseDir, "profiles", newName);
        // hold the profile's own instance guard across the move: a concurrently launching
        // instance cannot acquire it in between, so the dir moved is never one it just opened
        using var guard = new InstanceGuard();
        if (!guard.TryAcquire(entry.Dir))
            throw new InvalidOperationException("the profile is running in another instance");
        Directory.Move(entry.Dir, newDir);
        if (wasActive)
        {
            try { SetActive(baseDir, newName); }
            catch (Exception)
            {
                Directory.Move(newDir, entry.Dir); // the pointer still names the old name
                throw;
            }
        }
    }

    /// <summary>Removes a non-default profile that is not the active one.</summary>
    public static void Delete(string baseDir, string name)
    {
        var entry = GuardNotRunning(baseDir, name, "deleted");
        if (NameEquals(ActiveName(baseDir), name))
            throw new InvalidOperationException("the active profile cannot be deleted");
        // same guard-held move as Rename: the check above can go stale while another instance launches
        using var guard = new InstanceGuard();
        if (!guard.TryAcquire(entry.Dir))
            throw new InvalidOperationException("the profile is running in another instance");
        Directory.Delete(entry.Dir, recursive: true);
    }

    /// <summary>True when a live instance other than this one holds the dir's guard. Directory.Move/Delete
    /// on it would destroy that instance's files, so Rename/Delete refuse here even when a caller
    /// forgets the UI-level check. This instance's own guard does not count.</summary>
    static bool HeldByOtherInstance(string dir) =>
        InstanceGuard.IsHeld(dir) && !(Program.Guard.CurrentDir is { } own && SameDir(dir, own));

    /// <summary>The profile named by a <c>--profile name</c> / <c>--profile=name</c> launch argument
    /// (this instance only); null when absent, dangling or empty. All other args are ignored.</summary>
    public static string? ParseProfileArg(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--profile")
                return i + 1 < args.Count && args[i + 1].Length > 0 ? args[i + 1] : null;
            if (args[i].StartsWith("--profile=", StringComparison.Ordinal))
            {
                var name = args[i].Substring("--profile=".Length);
                return name.Length > 0 ? name : null;
            }
        }
        return null;
    }

    /// <summary>The Dir of the List entry with this Name, null when no such profile.</summary>
    public static string? DirOf(string baseDir, string name) =>
        List(baseDir).FirstOrDefault(e => NameEquals(e.Name, name))?.Dir;

    /// <summary>"-" + the first 16 hex chars of SHA256 over the uppercased canonical dir path, so two
    /// spellings of one Windows dir (case, trailing separator) get one kernel-object name and two
    /// profiles different ones (mutex, show event).</summary>
    public static string InstanceSuffix(string dir) =>
        "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDir(dir).ToUpperInvariant())), 0, 8).ToLowerInvariant();
}
