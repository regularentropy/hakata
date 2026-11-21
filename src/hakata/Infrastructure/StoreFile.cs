using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

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
    /// Upper bound for one store-file read (history.db, settings.json, a key file, a cache clip,
    /// an imported preset). The history holds at most 200 entries and cache clips are capped at
    /// write time, so 64 MB is generous for every store this app writes; a bigger file is not one
    /// of ours and must never size a buffer.
    /// </summary>
    internal const long MaxStoreBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Opens a store file for reading only when its size is within the cap: the length is checked
    /// on the open handle before anything is allocated, so an oversized file is refused without
    /// being read (a later swap of the file cannot enlarge the allocation either, because the
    /// buffer is sized from this handle's length). Throws <see cref="InvalidDataException"/> when
    /// the file is over <paramref name="max"/>.
    /// </summary>
    internal static FileStream OpenReadCapped(string path, long max)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
        try
        {
            if (fs.Length > max || fs.Length > int.MaxValue)
                throw new InvalidDataException($"{Path.GetFileName(path)} is {fs.Length} bytes, over the {max}-byte cap");
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Reads a whole store file, refusing an over-cap file before allocating (see <see cref="OpenReadCapped"/>).</summary>
    internal static byte[] ReadAllBytesCapped(string path, long max)
    {
        using var fs = OpenReadCapped(path, max);
        // one length capture: a file grown between the open-time check and the allocation (a local
        // writer can swap the file inside that window) is refused here instead of sizing the buffer
        var len = fs.Length;
        if (len > max || len > int.MaxValue)
            throw new InvalidDataException($"{Path.GetFileName(path)} is {len} bytes, over the {max}-byte cap");
        var bytes = new byte[(int)len];
        fs.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Reads a whole text store file, refusing an over-cap file before allocating (see
    /// <see cref="OpenReadCapped"/>). The capped bytes are decoded in memory, so a file grown after
    /// the open-time check still cannot grow the decoded string past the cap.</summary>
    internal static string ReadAllTextCapped(string path, long max)
    {
        var bytes = ReadAllBytesCapped(path, max);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Async twin of <see cref="ReadAllTextCapped"/> for a path; the cap is also enforced
    /// while reading (see <see cref="ReadAllTextCappedAsync(Stream, long)"/>), so a file grown after
    /// the open-time check cannot grow the result past the cap.</summary>
    internal static async Task<string> ReadAllTextCappedAsync(string path, long max)
    {
        using var fs = OpenReadCapped(path, max);
        return await ReadAllTextCappedAsync(fs, max);
    }

    /// <summary>
    /// Reads text from an already-open stream under the cap. The stream may not be seekable, so
    /// its length cannot be checked up front — the cap is enforced while reading instead, so a
    /// huge stream still never drives an allocation past it.
    /// </summary>
    internal static async Task<string> ReadAllTextCappedAsync(Stream stream, long max)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = new StringBuilder();
        var buf = new char[81920];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            if (text.Length + n > max)
                throw new InvalidDataException($"store text is over the {max}-byte cap");
            text.Append(buf, 0, n);
        }
        return text.ToString();
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
