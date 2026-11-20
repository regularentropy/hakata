using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Hakata.Models;

namespace Hakata.Infrastructure;

/// <summary>SQLite context over a history connection string: the shared-cache in-memory store, or a temp snapshot file while loading.</summary>
public sealed class HistoryDbContext(string connectionString) : DbContext
{
    public DbSet<HistoryEntry> Entries => Set<HistoryEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        // the builder, not interpolation: a value carrying ";" must stay one keyword's value
        options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = connectionString,
            Pooling = false,
        }.ToString());

    // The entry model carries no ordering field; the store needs one, so it lives as a shadow column.
    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<HistoryEntry>().Property<int>("Position");
}

/// <summary>Translation history, capped. The database lives in memory; history.db on disk is an
/// AES-256-GCM encrypted snapshot rewritten atomically on every mutation (see <see cref="HistoryCrypto"/>),
/// so there are no plaintext WAL/SHM sidecars and a crash mid-save keeps the previous snapshot.</summary>
public sealed class History : IDisposable
{
    const int Max = 200;
    readonly string dir;
    // a named shared-cache in-memory database, unique per instance so two stores in one process
    // never share tables; the anchor connection keeps it alive for the lifetime of this object
    readonly string memoryUri;
    readonly SqliteConnection anchor;
    bool writable = true;
    // set when the store was unlocked with a password (or a key was adopted from Settings)
    byte[]? dataKey;

    /// <summary>True when the key file is password-wrapped and no password has been entered yet.</summary>
    public bool NeedsPassword { get; private set; }

    /// <summary>The unlocked data key, for stores that share it (none yet — the sound cache keeps its own).</summary>
    internal byte[]? DataKey => dataKey;

    internal void AdoptKey(byte[] dek) => dataKey = dek;

    public History(string? dir = null)
    {
        this.dir = dir ?? AppSettings.Dir;
        memoryUri = $"file:hakata-history-{Guid.NewGuid():n}?mode=memory&cache=shared";
        anchor = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = memoryUri, Pooling = false }.ToString());
        anchor.Open();
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public List<HistoryEntry> Entries { get; private set; } = [];

    /// <summary>Raised after every mutation (add, delete, favorite, clear) has been attempted, a failed save included: retention follows it.</summary>
    public event Action? Changed;

    /// <summary>What happened while loading, for the status line: null when the store read fine.</summary>
    public string? RecoveryNotice { get; private set; }

    public void Load()
    {
        var path = Path.Combine(dir, "history.db");
        SweepStaleTemp(); // a killed save/load leaves its temp in .tmp; clear it before reading
        try
        {
            if (!File.Exists(path))
            {
                Entries = [];
                writable = true;
                return;
            }
            var bytes = File.ReadAllBytes(path);
            if (!HistoryCrypto.IsEncrypted(bytes))
            {
                // a plaintext store is not a format this build reads: keep it aside like any
                // unreadable store — never loaded, never overwritten in place
                UnreadableMoveAside(path);
                return;
            }
            if (KeyFileIsPassword() && dataKey is null)
            {
                // the store is intact, it just needs the password; never overwrite it
                NeedsPassword = true;
                UnreadableNoWrite("history is password-protected — unlock it to load it");
                return;
            }
            // a missing or un-unwrappable key means the snapshot is intact but unreadable:
            // never overwrite it with a fresh empty store
            if (ResolveKey() is not { } key)
            {
                UnreadableNoWrite("history.db is encrypted but history.key could not be read (another user or machine); history is not saved this run");
                return;
            }
            try { bytes = HistoryCrypto.Decrypt(key, bytes); }
            // a store that fails its GCM tag (replaced key file, moved store, tampering) is kept
            // in place and never overwritten — same posture as an unreadable key above
            catch (CryptographicException)
            {
                UnreadableNoWrite("history.db does not match history.key (replaced key file or moved store); history is not saved this run");
                return;
            }
            LoadSnapshot(bytes);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            UnreadableMoveAside(path);
        }
        catch (Exception)
        {
            // busy, locked or denied: the rows are the user's, so nothing is moved and nothing is
            // written this run — defaults must never land on a store that could not be read
            UnreadableNoWrite("history.db could not be read (locked or denied); history is not saved this run");
        }
    }

    /// <summary>Unlocks a password-wrapped key file: false on a wrong password, true when the store
    /// loads with the unlocked key. The data key itself never changes, so nothing is re-encrypted.</summary>
    public bool LoadWithPassword(string password)
    {
        if (!KeyFileIsPassword()) return false;
        string header;
        try { header = File.ReadAllText(HistoryCrypto.KeyPath(dir)); }
        catch (Exception) { return false; } // the key file vanished between the check and the read
        var dek = HistoryCrypto.UnwrapPassword(header, password);
        if (dek is null) return false;
        dataKey = dek;
        NeedsPassword = false;
        RecoveryNotice = null;
        Load();
        // Load reports its own outcome: an unlocked key that fails the store's GCM tag (a key
        // file from another profile) leaves a RecoveryNotice and must not read as success
        return RecoveryNotice is null && !NeedsPassword;
    }

    bool KeyFileIsPassword() =>
        HistoryCrypto.ReadKey(dir).Kind == HistoryCrypto.KeyKind.Password;

    byte[]? ResolveKey() =>
        dataKey ?? (HistoryCrypto.ReadKey(dir) is { Kind: HistoryCrypto.KeyKind.Machine, Dek: { } d } ? d : null);

    /// <summary>Creates the owner-only .tmp scratch subdir and returns it. In env-home dir mode the
    /// profile dir itself keeps the user's mode (RestrictDir skips the env root), so the plaintext
    /// snapshot temp files sit one level deeper, shielded by a 0700 directory.</summary>
    string EnsureTempDir()
    {
        var tmpDir = Path.Combine(dir, ".tmp");
        Directory.CreateDirectory(tmpDir);
        StoreFile.RestrictDir(tmpDir); // skips only the exact env root, so .tmp under it IS narrowed
        return tmpDir;
    }

    /// <summary>Best-effort sweep of the .tmp scratch files a killed save/load left behind. The patterns
    /// also match an in-flight Save's temp files, so only files old enough that no live save can still be
    /// writing them are stale — deleting a live one makes that Save fail.</summary>
    void SweepStaleTemp()
    {
        var tmpDir = Path.Combine(dir, ".tmp");
        if (!Directory.Exists(tmpDir)) return;
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            foreach (var pattern in new[] { "history.db.tmp-*", "history.db.load-*" })
                foreach (var file in Directory.GetFiles(tmpDir, pattern))
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) > cutoff) continue; // maybe a live save's temp
                        File.Delete(file);
                    }
                    catch (Exception) { }
        }
        catch (Exception) { }
    }

    /// <summary>Loads a decrypted database image into memory.</summary>
    void LoadSnapshot(byte[] bytes)
    {
        // the decrypted image lands in the 0700 .tmp subdir (env-home dir mode keeps the profile
        // dir itself at the user's mode); EnsureTempDir throwing is caught by Load's caller-side try
        var tmp = Path.Combine(EnsureTempDir(), "history.db.load-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            // narrow the directory before the decrypted image lands, so the temp file is never
            // reachable through a still-wide directory even for the moment before its own 0600
            StoreFile.RestrictDir(dir);
            StoreFile.WriteRestrictedBytes(tmp, bytes);
            List<HistoryEntry> loaded;
            using (var db = new HistoryDbContext(tmp)) // the context takes the data source, not a built connection string
            {
                db.Database.EnsureCreated();
                loaded = [.. db.Entries.OrderBy(e => EF.Property<int>(e, "Position"))];
            }
            using (var db = Open())
            {
                db.Entries.ExecuteDelete();
                for (var i = 0; i < loaded.Count; i++)
                {
                    db.Add(loaded[i]);
                    db.Entry(loaded[i]).Property("Position").CurrentValue = i;
                }
                db.SaveChanges();
            }
            Entries = loaded;
            writable = true;
        }
        finally
        {
            StoreFile.DeleteQuietly(tmp);
        }
    }

    void UnreadableMoveAside(string path)
    {
        Entries = [];
        // only a store that is not a database at all is moved aside, under the first free name,
        // so an earlier recovery candidate survives
        RecoveryNotice = StoreFile.MoveAside(path, ".unreadable") is { } name
            ? $"history.db was not a readable database and was kept as {name}"
            : "history.db is not a readable database and could not be kept; history is not saved this run";
        if (RecoveryNotice.Contains("not saved")) writable = false;
        else { MoveSidecars(path); writable = true; }
    }

    void UnreadableNoWrite(string notice)
    {
        Entries = [];
        writable = false;
        RecoveryNotice = notice;
    }

    /// <summary>Corruption means the file is not a database: SQLITE_CORRUPT / SQLITE_NOTADB.</summary>
    static bool IsCorruption(Exception ex) =>
        ex is SqliteException { SqliteErrorCode: 11 or 26 }; // SQLITE_CORRUPT, SQLITE_NOTADB

    /// <summary>WAL/SHM sidecars of a moved-aside unreadable database go beside it, so nothing is stranded.</summary>
    static void MoveSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = path + suffix;
            if (!File.Exists(side)) continue;
            try { StoreFile.MoveAside(side, ".unreadable" + suffix); } catch (Exception) { }
        }
    }

    /// <summary>What the last Save could not do, for the status line: null when it wrote fine.</summary>
    public string? SaveNotice { get; private set; }

    /// <summary>The one report the save failure earns; the next save replaces it.</summary>
    internal void ClearSaveNotice() => SaveNotice = null;

    public void Add(HistoryEntry entry)
    {
        Entries.Insert(0, entry);
        if (Entries.Count > Max) Entries.RemoveRange(Max, Entries.Count - Max);
        Save();
    }

    public void Clear()
    {
        Entries.Clear();
        Save();
    }

    /// <summary>
    /// Rewrites the in-memory store, snapshots it to a temp file, encrypts the snapshot, verifies the
    /// ciphertext decrypts to the same rows, and only then atomically replaces history.db. At most 200
    /// rows, and every mutation (add, delete, favorite, clear) already funnels through here.
    /// </summary>
    public void Save()
    {
        if (!writable)
        {
            // a store that could not be read is not overwritten, but the mutation was still
            // attempted and Changed promises to fire after a failed save included
            Changed?.Invoke();
            return;
        }
        var path = Path.Combine(dir, "history.db");
        var tmpDir = Path.Combine(dir, ".tmp");
        var tmpPlain = Path.Combine(tmpDir, "history.db.tmp-" + Guid.NewGuid().ToString("n"));
        var tmpEnc = tmpPlain + ".enc";
        var tmpCheck = tmpPlain + ".check";
        try
        {
            Directory.CreateDirectory(dir);
            // 0700 before SQLite creates the plaintext VACUUM target with the umask's 0644:
            // VACUUM INTO refuses an existing file, so the directory is what shields it —
            // in env-home dir mode the profile dir keeps the user's mode, so the temp goes
            // into the 0700 .tmp subdir instead
            StoreFile.RestrictDir(dir);
            EnsureTempDir();
            // an unlocked password key wins; a locked (password-wrapped, no password) file yields null
            // and the save reports failure instead of clobbering the key file
            var key = dataKey ?? HistoryCrypto.LoadOrCreateKey(dir);
            if (key is null) throw new InvalidDataException("the history key is locked");
            using (var db = Open())
            {
                db.Database.EnsureCreated();
                using var tx = db.Database.BeginTransaction();
                db.Entries.ExecuteDelete();
                for (var i = 0; i < Entries.Count; i++)
                {
                    db.Add(Entries[i]);
                    db.Entry(Entries[i]).Property("Position").CurrentValue = i;
                }
                db.SaveChanges();
                tx.Commit();
            }
            // serialize the in-memory database (VACUUM INTO copies it out whole)
            using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = memoryUri, Pooling = false }.ToString()))
            {
                raw.Open();
                using var vacuum = raw.CreateCommand();
                vacuum.CommandText = $"VACUUM INTO '{tmpPlain.Replace("'", "''")}'";
                vacuum.ExecuteNonQuery();
            }
            StoreFile.RestrictFile(tmpPlain);
            var enc = HistoryCrypto.Encrypt(key, File.ReadAllBytes(tmpPlain));
            StoreFile.WriteRestrictedBytes(tmpEnc, enc);
            // flush the ciphertext to disk before the rename: a power loss between write and
            // flush could otherwise leave a zero-length history.db and lose the whole history
            using (var encStream = new FileStream(tmpEnc, FileMode.Open, FileAccess.Write, FileShare.None))
                encStream.Flush(flushToDisk: true);
            // never replace the store with ciphertext that does not decrypt back to the same rows
            var check = HistoryCrypto.Decrypt(key, enc);
            StoreFile.WriteRestrictedBytes(tmpCheck, check);
            long count;
            using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmpCheck, Pooling = false }.ToString()))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Entries";
                count = (long)(cmd.ExecuteScalar() ?? 0L);
            }
            if (count != Entries.Count)
                throw new InvalidDataException($"encrypted snapshot holds {count} of {Entries.Count} rows");
            File.Move(tmpEnc, path, overwrite: true);
            // the snapshot is the user's private text: the file and its key stay owner-only
            StoreFile.RestrictFile(path);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // SQLITE_BUSY / SQLITE_LOCKED: a concurrent writer holds the store; the next save
            // retries, but this failure is reported once instead of swallowed
            SaveNotice = "history is busy; entries stay on screen and save on the next change";
        }
        catch (Exception)
        {
            // a store that cannot be written must not fail silently: say so once, keep the screen text
            SaveNotice = "history could not be saved (disk full or denied); entries stay on screen but are not stored";
        }
        finally
        {
            StoreFile.DeleteQuietly(tmpPlain, tmpEnc, tmpCheck);
        }
        Changed?.Invoke();
    }

    HistoryDbContext Open() => new(memoryUri);

    public void Dispose() => anchor.Dispose();
}
