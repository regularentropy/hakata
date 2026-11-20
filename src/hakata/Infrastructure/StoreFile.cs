using System;
using System.IO;
using System.Text;

namespace Hakata.Infrastructure;

/// <summary>File permissions and quarantine moves for the private files this app keeps beside settings.json.</summary>
internal static class StoreFile
{
    /// <summary>
    /// Owner-only file mode (0600) off Windows, for stores this app creates beside settings.json: the history database, the sound cache and the crash log carry private content too.
    /// </summary>
    internal static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* a mode that cannot be narrowed is not worth failing the caller */ }
    }

    /// <summary>
    /// Narrows an app-owned directory to owner-only (0700) off Windows when any wider bit is set.
    /// The user's own HAKATA_HOME root keeps its mode; directories the app creates under
    /// it (profiles/, a profile dir) are still narrowed. No-op on Windows.
    /// </summary>
    internal static void RestrictDir(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        // the user's own HAKATA_HOME root keeps its mode; directories the app creates under
        // it (profiles/, a profile dir) are still narrowed
        if (Environment.GetEnvironmentVariable("HAKATA_HOME") is { Length: > 0 } home
            && Profiles.SameDir(dir, home)) return;
        try
        {
            var current = File.GetUnixFileMode(dir);
            var wanted = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((current & ~wanted) != 0) File.SetUnixFileMode(dir, wanted);
        }
        catch (Exception) { /* a mode that cannot be narrowed is not worth failing the caller */ }
    }

    /// <summary>
    /// Writes bytes with the 0600 mode set on the handle before the first byte lands (off Windows),
    /// so the file is never readable by anyone else even in the window before a later RestrictFile.
    /// </summary>
    internal static void WriteRestrictedBytes(string path, byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            return;
        }
        const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
        File.SetUnixFileMode(file.SafeFileHandle, Mode); // set before the bytes land
        file.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Appends text with the 0600 mode set on the handle before the first byte lands (off Windows),
    /// so a log carrying stack traces or URLs is never readable by anyone else; an existing wider file is
    /// narrowed the same way. On Windows a plain append. Callers keep their own try/catch: a log that cannot
    /// be written must not throw.</summary>
    internal static void AppendRestricted(string path, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.AppendAllText(path, text);
            return;
        }
        const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append, // opens or creates, positioned at the end
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = Mode, // applies at creation; the handle call below covers an existing file
        });
        File.SetUnixFileMode(file.SafeFileHandle, Mode); // set before the bytes land
        var bytes = Encoding.UTF8.GetBytes(text);
        file.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Replaces <paramref name="path"/> atomically: <paramref name="writeTmp"/> writes a unique temp file
    /// beside it (Guid-named, so concurrent writers cannot race on one fixed temp), the temp is renamed
    /// over the target, and on any failure the temp is deleted quietly and the exception rethrown.
    /// </summary>
    internal static void WriteAtomic(string path, Action<string> writeTmp)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            writeTmp(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    /// <summary>Deletes each path, ignoring every failure — cleanup must never mask the real error.</summary>
    internal static void DeleteQuietly(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    /// <summary>An exclusive lock file (app.lock): FileShare.None, created 0600 off Windows
    /// before the handle is handed out; an existing wider file is narrowed after open.</summary>
    internal static FileStream OpenLockFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // the lock names private data, like the stores beside it
        var file = new FileStream(path, options);
        RestrictFile(path); // OpenOrCreate keeps an existing wider lock file's mode
        return file;
    }

    /// <summary>Moves a file aside under the first free name (name, name-2, …); null when even that fails.</summary>
    internal static string? MoveAside(string path, string suffix)
    {
        try
        {
            for (var i = 0; ; i++)
            {
                var candidate = i == 0 ? path + suffix : $"{path}{suffix}-{i + 1}";
                if (File.Exists(candidate)) continue;
                File.Move(path, candidate, overwrite: false);
                // a moved-aside plaintext store keeps its 0644 mode otherwise — the copy is private history
                RestrictFile(candidate);
                return Path.GetFileName(candidate);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
