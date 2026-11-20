using System;
using System.IO;
using System.Threading;

namespace Hakata.Infrastructure;

/// <summary>Owns the single-instance guard of the current profile's data dir and can swap it at
/// runtime (hot profile switch): the new profile's guard is acquired before the old one is
/// released, so a failed switch leaves the old guard untouched. Windows guards by named mutex
/// plus show event; elsewhere by an exclusively locked app.lock file in the profile dir.</summary>
public sealed class InstanceGuard : IDisposable
{
    Mutex? mutex;
    EventWaitHandle? showEvent;
    FileStream? lockFile;

    public string? CurrentDir { get; private set; }

    /// <summary>Windows only: the per-profile "raise the window" event a second launch sets.</summary>
    public EventWaitHandle? ShowEvent { get; private set; }

    /// <summary>false = another instance owns <paramref name="dir"/>; the current guard is kept.</summary>
    public bool TryAcquire(string dir)
    {
        dir = Profiles.CanonicalDir(dir); // CurrentDir is always canonical, so same-dir checks are exact
        return Swap(dir);
    }

    /// <summary>Shared tail of TryAcquire/TrySwitch: acquire the new profile's guard, then release the old.</summary>
    bool Swap(string dir)
    {
        if (!TryAcquireInto(dir, out var m, out var ev, out var f)) return false;
        Release();
        Assign(dir, m, ev, f);
        return true;
    }

    /// <summary>Acquire the new profile's guard, then release the old; false (old kept) when the new is taken.
    /// The same dir (any spelling of it) is a true no-op: true, and the guard is not released.</summary>
    public bool TrySwitch(string dir)
    {
        dir = Profiles.CanonicalDir(dir);
        if (CurrentDir is { } current && Profiles.SameDir(dir, current)) return true;
        return Swap(dir);
    }

    public void Release()
    {
        mutex?.Dispose(); mutex = null;
        showEvent?.Dispose(); showEvent = null; ShowEvent = null;
        lockFile?.Dispose(); lockFile = null;
        CurrentDir = null;
    }

    public void Dispose() => Release();

    /// <summary>True when a live process currently holds <paramref name="dir"/>'s guard — the probe
    /// that marks running profiles in the manager. Windows: the named mutex exists only while some
    /// process has it open. Elsewhere: the app.lock file is exclusively locked by its holder — the OS
    /// releases that lock when the holder dies, so a crashed process leaves the lock free, exactly as
    /// if no launch had ever happened.</summary>
    public static bool IsHeld(string dir)
    {
        dir = Profiles.CanonicalDir(dir);
        // one probe can answer "held" transiently while another instance is mid-acquire (kernel
        // object or lock file not settled yet); confirm with a second probe before trusting it
        return ProbeHeld(dir) && ProbeHeld(dir);
    }

    static bool ProbeHeld(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            try { using var m = Mutex.OpenExisting(Program.MutexNameFor(dir)); return true; }
            catch (Exception) { return false; }
        }
        var lockPath = Path.Combine(dir, "app.lock");
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var f = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // the lock was free; this probe held it for an instant only
        }
        catch (Exception) { return true; }
    }

    void Assign(string dir, Mutex? m, EventWaitHandle? ev, FileStream? f)
    {
        mutex = m; showEvent = ev; ShowEvent = ev; lockFile = f;
        CurrentDir = dir;
    }

    /// <summary>Acquires into out params only on success; on failure the partial acquisitions are disposed.</summary>
    static bool TryAcquireInto(string dir, out Mutex? m, out EventWaitHandle? ev, out FileStream? f)
    {
        m = null; ev = null; f = null;
        if (OperatingSystem.IsWindows())
        {
            Mutex mutex;
            bool first;
            try { mutex = new Mutex(initiallyOwned: true, Program.MutexNameFor(dir), out first); }
            catch (Exception) { return false; } // an unusable kernel-object name is a failed acquire
            if (!first) { mutex.Dispose(); return false; }
            try { ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventNameFor(dir)); }
            catch (Exception) { mutex.Dispose(); return false; }
            m = mutex;
            return true;
        }
        var createdDir = false;
        try
        {
            createdDir = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            StoreFile.RestrictDir(dir); // a fresh profile dir is owner-only before app.lock lands (S1.8)
            f = StoreFile.OpenLockFile(Path.Combine(dir, "app.lock"));
            return true;
        }
        catch (Exception)
        {
            f?.Dispose(); f = null;
            // a failed second launch must leave nothing behind: drop the dir this call created —
            // non-recursive, so it succeeds only while the dir is still empty (our lock never
            // landed); a concurrent winner's dir with its app.lock and data inside is left alone
            if (createdDir)
                try { Directory.Delete(dir); } catch (Exception) { }
            return false;
        }
    }
}
