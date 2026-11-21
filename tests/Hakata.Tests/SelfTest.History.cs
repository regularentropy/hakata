using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Microsoft.Data.Sqlite;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata;

/// <summary>Dependency-free checks for the parts that break silently: endpoints, SSE, prompts, JSON parsing, history.</summary>
public static partial class SelfTest
{
    static void HistorySidebar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        history.Add(new HistoryEntry { From = "en", To = "de", Source = "hello world", Output = "hallo welt" });
        history.Add(new HistoryEntry { From = "auto", To = "fr", Source = "good morning", Output = "bonjour", Cost = 0.25 });

        var messages = new List<string>();
        var vm = new HistoryViewModel(history, messages.Add, _ => { });
        Check(vm.Entries.Count == 2, "sidebar: lists every entry");
        vm.SearchText = "bonjour";
        Check(vm.Entries.Count == 1 && vm.Entries[0].Source == "good morning", "sidebar: search matches the output");
        vm.SearchText = "";
        Check(vm.Entries.Count == 2, "sidebar: clearing the search restores the list");

        // the stored extras are searchable: "casual" and "slang" appear nowhere in source/output/route
        vm.Entries[1].Alternatives = [new Alternative { Text = "hey", Explanation = "casual greeting" }];
        vm.Entries[1].Insights = [new WordInsight { Word = "hello", Kind = "slang" }];
        vm.SearchText = "casual";
        Check(vm.Entries.Count == 1 && vm.Entries[0].Source == "hello world",
            "sidebar: search matches a stored alternative's rationale");
        vm.SearchText = "slang";
        Check(vm.Entries.Count == 1 && vm.Entries[0].Source == "hello world",
            "sidebar: search matches a stored insight's kind");
        vm.SearchText = "";

        Check(vm.HasSpend && vm.SpendLabel == "~$0.2500 spent", "sidebar: shows the running spend of the stored entries");

        // clear and delete must drop the selection too, or CanAct stays true on ghost rows
        using (var h2 = new History(Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"))))
        {
            h2.Add(new HistoryEntry { From = "en", To = "de", Source = "a", Output = "b" });
            h2.Add(new HistoryEntry { From = "en", To = "de", Source = "c", Output = "d" });
            var vm2 = new HistoryViewModel(h2, _ => { }, _ => { });
            vm2.SetSelection(vm2.Entries.ToList());
            Check(vm2.CanAct, "sidebar: a selection makes the actions available");
            vm2.DeleteSelectedCommand.Execute(null);
            Check(vm2.Selection.Count == 0 && !vm2.CanAct, "sidebar: delete drops the selection and CanAct follows");
            h2.Add(new HistoryEntry { From = "en", To = "de", Source = "e", Output = "f" });
            vm2.Refresh();
            vm2.SetSelection(vm2.Entries.ToList());
            vm2.ClearCommand.Execute(null); // arms
            vm2.ClearCommand.Execute(null); // clears
            Check(vm2.Selection.Count == 0 && !vm2.CanAct && h2.Entries.Count == 0,
                "sidebar: clear drops the selection and CanAct follows");
        }

        var entry = vm.Entries[0];
        vm.ToggleFavoriteForCommand.Execute(entry);
        Check(entry.Favorite && vm.Entries[0].Favorite, "sidebar: row star toggles the entry");

        // Shift+click range: the list hands the model the whole picked set, and the buttons act on it.
        var batch = vm.Entries.ToList();
        vm.SetSelection(batch);
        Check(vm.Selection.Count == 2, "sidebar: the picked rows reach the model");
        vm.ToggleFavoriteCommand.Execute(null);
        Check(batch.All(e => e.Favorite) && messages[^1] == "Added 2 entries to favorites",
            "sidebar: favorite stars the whole selection, mixed set included");
        vm.ToggleFavoriteCommand.Execute(null);
        Check(batch.All(e => !e.Favorite) && messages[^1] == "Removed 2 entries from favorites",
            "sidebar: a fully starred selection unstars as one");

        vm.SelectedEntry = entry;
        vm.SetSelection([]);
        vm.DeleteSelectedCommand.Execute(null);
        Check(!history.Entries.Contains(entry), "sidebar: delete removes the selected entry");
        Check(history.Entries.Count == 1, "sidebar: a one-row selection deletes only that row");

        var second = vm.Entries[0];
        history.Add(new HistoryEntry { From = "en", To = "de", Source = "later", Output = "spaeter" });
        vm.Refresh();
        vm.SetSelection([second, vm.Entries[0]]);
        vm.DeleteSelectedCommand.Execute(null);
        Check(history.Entries.Count == 0 && vm.Selection.Count == 0 && vm.IsEmpty,
            "sidebar: delete removes every picked row and drops the selection");

        history.Add(new HistoryEntry { From = "en", To = "de", Source = "again", Output = "wieder" });
        vm.Refresh();
        Check(vm.Entries.Count == 1 && vm.Entries[0].Source == "again", "sidebar: refresh after delete shows the remaining row");
        // Refresh must update the list in place: a Reset would null the bound ListBox selection (#0049 class)
        var resets = 0;
        vm.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };
        var picked = vm.Entries[0];
        vm.SelectedEntry = picked;
        vm.Refresh();
        history.Add(new HistoryEntry { From = "en", To = "de", Source = "more", Output = "mehr" });
        vm.Refresh();
        Check(resets == 0, "sidebar: Refresh never fires a Reset on the entries list");
        Check(ReferenceEquals(vm.SelectedEntry, picked), "sidebar: the picked entry survives a refresh that adds rows");

        vm.ClearCommand.Execute(null);
        Check(history.Entries.Count == 2 && vm.ClearButtonText == "Confirm?", "sidebar: clear asks before deleting");
        vm.ClearCommand.Execute(null);
        Check(history.Entries.Count == 0 && vm.IsEmpty, "sidebar: the second clear wipes the history");
        Check(vm.FavoriteText.Contains("Favorite"), "sidebar: the star button still labels itself");
        try { Directory.Delete(dir, true); } catch (Exception) { }

        StartBusySaveProbe(); // runs in the background; checked at the end of HistoryPasswordProtection
    }

    // #9: SQLITE_BUSY / SQLITE_LOCKED must set a save notice instead of being swallowed. A real lock
    // on the shared-cache in-memory database is only reported after the connection's 30-second busy
    // timeout, longer than one test method may run, so the locked save runs as a background probe
    // here and its notice is asserted once the probe finishes (CheckBusySaveProbe).
    static History? busyStore;
    static SqliteConnection? busyLocker;
    static Task? busySave;
    static string? busyDir;

    static void StartBusySaveProbe()
    {
        busyDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        busyStore = new History(busyDir);
        busyStore.Add(new HistoryEntry { From = "en", To = "de", Source = "busy one", Output = "busy eins" });
        var memoryUri = (string)typeof(History).GetField("memoryUri",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(busyStore)!;
        busyLocker = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = memoryUri, Pooling = false }.ToString());
        busyLocker.Open();
        using var begin = busyLocker.CreateCommand();
        begin.CommandText = "BEGIN";
        begin.ExecuteNonQuery();
        using var read = busyLocker.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM Entries";
        read.ExecuteScalar(); // the open transaction holds the table lock for the whole probe
        busySave = Task.Run(() =>
            busyStore.Add(new HistoryEntry { From = "en", To = "de", Source = "busy two", Output = "busy zwei" }));
    }

    static void CheckBusySaveProbe()
    {
        if (busySave is null || busyStore is null)
        {
            Check(false, "history #9: a locked store sets a save notice instead of failing silently");
            return;
        }
        var waited = Stopwatch.StartNew();
        while (!busySave.IsCompleted && waited.ElapsedMilliseconds < 28500) Thread.Sleep(250);
        Check(busySave.IsCompleted && busyStore.SaveNotice is { Length: > 0 } && busyStore.SaveNotice.Contains("busy"),
            "history #9: a locked store sets a save notice instead of failing silently");
        busyLocker?.Dispose();
        if (busyDir is not null) { try { Directory.Delete(busyDir, true); } catch (Exception) { } }
    }

    /// <summary>#0067 shape: a cost added to an entry already on screen must raise the row's cost
    /// bindings — Refresh syncs rows in place, so nothing else re-reads the entry.</summary>
    static void CostRowFollows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        var entry = new HistoryEntry { From = "en", To = "de", Source = "hello", Output = "hallo" };
        history.Add(entry);

        var messages = new List<string>();
        var vm = new HistoryViewModel(history, messages.Add, _ => { });
        vm.Refresh();

        var raised = new List<string>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        Check(vm.AddCost(entry, 0.5), "cost row: AddCost on a listed entry succeeds");
        Check(entry.Cost == 0.5, "cost row: the added cost lands on the entry");
        Check(raised.Contains(nameof(HistoryEntry.CostShort)),
            "#0067: a cost added to a listed entry raises CostShort for the row's chip");
        Check(raised.Contains(nameof(HistoryEntry.HasCost)),
            "#0067: a cost added to a listed entry raises HasCost for the chip's visibility");

        try { Directory.Delete(dir, true); } catch (Exception) { }
    }

    /// <summary>The store on disk is an AES-GCM snapshot: no plaintext, tamper-evident, key-bound.</summary>
    static void HistoryEncryption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            var h = new History(dir);
            h.Add(new HistoryEntry { From = "en", To = "de", Source = "hello secret", Output = "hallo geheim" });
            var dbPath = Path.Combine(dir, "history.db");
            var bytes = File.ReadAllBytes(dbPath);
            Check(bytes.AsSpan().StartsWith("OTENC1"u8), "encryption: the store carries the encrypted header");
            Check(!Encoding.UTF8.GetString(bytes).Contains("hello secret"),
                "encryption: no source text sits in the file plaintext");
            Check(File.Exists(Path.Combine(dir, "history.key")), "encryption: the key file exists");
            if (!OperatingSystem.IsWindows())
                Check((File.GetUnixFileMode(Path.Combine(dir, "history.key")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    "encryption: history.key is owner-only off Windows");

            var reloaded = new History(dir);
            reloaded.Load();
            Check(reloaded.Entries.Count == 1 && reloaded.Entries[0].Source == "hello secret"
                && reloaded.RecoveryNotice is null, "encryption: the encrypted store reloads with its key");

            // a flipped byte must fail the GCM tag: the store is kept in place and never overwritten
            // (a replaced history.key lands here too — a swapped key must not clobber the store)
            bytes[^3] ^= 0xFF;
            File.WriteAllBytes(dbPath, bytes);
            var tamperedBytes = File.ReadAllBytes(dbPath);
            var tampered = new History(dir);
            tampered.Load();
            Check(tampered.Entries.Count == 0 && tampered.RecoveryNotice is { Length: > 0 }
                && !File.Exists(dbPath + ".unreadable"), "encryption: a tampered store is not read and not moved aside");
            tampered.Add(new HistoryEntry { Source = "x", Output = "y" });
            Check(File.ReadAllBytes(dbPath).SequenceEqual(tamperedBytes),
                "encryption: a failed-auth store is never overwritten (swapped-key protection)");

            // a missing key on an encrypted store disables writing instead of overwriting the ciphertext
            var dir2 = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            var h2 = new History(dir2);
            h2.Add(new HistoryEntry { Source = "a", Output = "b" });
            File.Delete(Path.Combine(dir2, "history.key"));
            var keyless = new History(dir2);
            keyless.Load();
            Check(keyless.RecoveryNotice is { Length: > 0 } && keyless.RecoveryNotice.Contains("not saved"),
                "encryption: a missing key reports that writing is disabled");
            keyless.Add(new HistoryEntry { Source = "c", Output = "d" });
            Check(File.ReadAllBytes(Path.Combine(dir2, "history.db")).AsSpan().StartsWith("OTENC1"u8),
                "encryption: a keyless store is not overwritten");
            try { Directory.Delete(dir2, true); } catch (Exception) { }

            // a planted plaintext store is refused like any unreadable one: not loaded, kept
            // aside under .unreadable, never re-encrypted in place
            var dir3 = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir3);
            var plainPath = Path.Combine(dir3, "history.db");
            File.WriteAllBytes(plainPath, "SQLite format 3\0plaintext, not a store this build reads"u8.ToArray());
            var refused = new History(dir3);
            refused.Load();
            Check(refused.Entries.Count == 0 && refused.RecoveryNotice is { Length: > 0 }
                && File.Exists(plainPath + ".unreadable") && !File.Exists(plainPath),
                "encryption: a plaintext store is refused and kept aside, not loaded or re-encrypted");
            try { Directory.Delete(dir3, true); } catch (Exception) { }

            // #12: a store truncated below magic+nonce+tag still starts with the magic but must
            // throw CryptographicException (Load reports "locked or denied" on anything else)
            var cryptoDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(cryptoDir);
            var cryptoKey = HistoryCrypto.LoadOrCreateKey(cryptoDir);
            var cipher = HistoryCrypto.Encrypt(cryptoKey!, Encoding.UTF8.GetBytes("SQLite format 3\0payload"));
            var truncated = cipher.AsSpan(0, 10).ToArray(); // magic present, far short of magic+nonce+tag
            Check(HistoryCrypto.IsEncrypted(truncated), "crypto: a truncated file still carries the magic");
            try
            {
                HistoryCrypto.Decrypt(cryptoKey!, truncated);
                Check(false, "crypto: a truncated store throws CryptographicException");
            }
            catch (CryptographicException)
            {
                Check(true, "crypto: a truncated store throws CryptographicException");
            }
            catch (Exception)
            {
                Check(false, "crypto: a truncated store throws CryptographicException, not a bad-argument error");
            }

            // #12: a DPAPI blob whose unwrapped plaintext is not base64 reads as Unreadable, not as
            // a thrown FormatException (on Windows Protect really wraps, off Windows the plain
            // string is not 32 bytes and the length check already yields Unreadable)
            File.WriteAllBytes(HistoryCrypto.KeyPath(cryptoDir, "dpapi.key"),
                Encoding.UTF8.GetBytes(KeyProtector.Protect("not base64!")));
            Check(HistoryCrypto.ReadKey(cryptoDir, "dpapi.key").Kind == HistoryCrypto.KeyKind.Unreadable,
                "crypto: a non-base64 DPAPI blob reads as unreadable");
            try { Directory.Delete(cryptoDir, true); } catch (Exception) { }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Store-file reads are size-capped before any buffer is sized from the file: an
    /// over-cap file is refused without being read, an at-cap file still reads, an over-cap
    /// encrypted store keeps the tampered-store posture (reported, kept in place, never
    /// overwritten), and no buffer sized by unauthenticated bytes is allocated before the
    /// authentication tag verifies.</summary>
    static void HistoryStoreSizeCaps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            // the capped read helpers: small and at-cap files pass, cap+1 is refused
            const int cap = 4096;
            var small = Path.Combine(dir, "small.bin");
            File.WriteAllBytes(small, "hello store"u8.ToArray());
            Check(StoreFile.ReadAllBytesCapped(small, cap).SequenceEqual("hello store"u8.ToArray()),
                "store caps: a small file reads through the capped helper");
            var atCap = Path.Combine(dir, "atcap.bin");
            File.WriteAllBytes(atCap, new byte[cap]);
            Check(StoreFile.ReadAllBytesCapped(atCap, cap).Length == cap,
                "store caps: a file exactly at the cap still reads");
            var overCap = Path.Combine(dir, "overcap.bin");
            File.WriteAllBytes(overCap, new byte[cap + 1]);
            try { StoreFile.ReadAllBytesCapped(overCap, cap); Check(false, "store caps: a file over the cap is refused"); }
            catch (InvalidDataException) { Check(true, "store caps: a file over the cap is refused"); }
            var textFile = Path.Combine(dir, "store.json");
            File.WriteAllText(textFile, "{\"a\":1}");
            Check(StoreFile.ReadAllTextCapped(textFile, cap) == "{\"a\":1}",
                "store caps: a small text file reads through the capped helper");
            Check(StoreFile.ReadAllTextCappedAsync(textFile, cap).GetAwaiter().GetResult() == "{\"a\":1}",
                "store caps: the async capped helper passes a small text file");
            File.WriteAllText(textFile, new string('x', cap + 1));
            try { StoreFile.ReadAllTextCapped(textFile, cap); Check(false, "store caps: an over-cap text file is refused"); }
            catch (InvalidDataException) { Check(true, "store caps: an over-cap text file is refused"); }
            try { StoreFile.ReadAllTextCappedAsync(textFile, cap).GetAwaiter().GetResult(); Check(false, "store caps: the async capped helper refuses an over-cap text file"); }
            catch (InvalidDataException) { Check(true, "store caps: the async capped helper refuses an over-cap text file"); }

            // an over-cap encrypted store: reported, kept in place, never read whole, never overwritten
            var key = HistoryCrypto.LoadOrCreateKey(dir);
            var dbPath = Path.Combine(dir, "history.db");
            var over = new byte[StoreFile.MaxStoreBytes + 1];
            "OTENC1"u8.CopyTo(over); // magic only — the rest is unauthenticated garbage
            File.WriteAllBytes(dbPath, over);
            var big = new History(dir);
            big.Load();
            Check(big.Entries.Count == 0 && big.RecoveryNotice is { Length: > 0 }
                && File.Exists(dbPath) && new FileInfo(dbPath).Length == over.Length
                && !File.Exists(dbPath + ".unreadable"),
                "store caps: an over-cap encrypted store is reported and kept in place, not moved aside");
            big.Add(new HistoryEntry { From = "en", To = "de", Source = "x", Output = "y" });
            Check(new FileInfo(dbPath).Length == over.Length,
                "store caps: an over-cap store is not overwritten by a save");
            // Decrypt called directly on over-cap bytes refuses at the cap, before any tag work
            try { HistoryCrypto.Decrypt(key!, over); Check(false, "store caps: Decrypt refuses an over-cap buffer"); }
            catch (AuthenticationTagMismatchException) { Check(false, "store caps: Decrypt refuses an over-cap buffer at the cap, not by the tag"); }
            catch (CryptographicException) { Check(true, "store caps: Decrypt refuses an over-cap buffer at the cap, not by the tag"); }

            // an over-cap plaintext store is still moved aside like any unreadable one
            var dir2 = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir2);
            try
            {
                var plainPath = Path.Combine(dir2, "history.db");
                File.WriteAllBytes(plainPath, new byte[StoreFile.MaxStoreBytes + 1]); // no magic
                var plain = new History(dir2);
                plain.Load();
                Check(plain.RecoveryNotice is { Length: > 0 } && File.Exists(plainPath + ".unreadable") && !File.Exists(plainPath),
                    "store caps: an over-cap plaintext store is moved aside without being read");
            }
            finally { try { Directory.Delete(dir2, true); } catch (Exception) { } }

            // an under-cap well-formed store still reads, decrypts and reloads
            var dir3 = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            try
            {
                var h = new History(dir3);
                h.Add(new HistoryEntry { From = "en", To = "de", Source = "round trip", Output = "Rundreise" });
                var stored = StoreFile.ReadAllBytesCapped(Path.Combine(dir3, "history.db"), StoreFile.MaxStoreBytes);
                var plainBytes = HistoryCrypto.Decrypt(HistoryCrypto.LoadKey(dir3)!, stored);
                Check(HistoryCrypto.IsEncrypted(stored) && plainBytes.Length > 0,
                    "store caps: an under-cap store reads through the capped helper and decrypts");
                var reloaded = new History(dir3);
                reloaded.Load();
                Check(reloaded.Entries.Count == 1 && reloaded.Entries[0].Source == "round trip" && reloaded.RecoveryNotice is null,
                    "store caps: an under-cap store reloads normally");
            }
            finally { try { Directory.Delete(dir3, true); } catch (Exception) { } }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Clips are ciphertext at rest under a cache-owned key, the file name hides the content
    /// hash, and a keyless cache is a miss.</summary>
    static void SoundCacheEncryption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var folder = Path.Combine(dir, "sound-cache");
        try
        {
            var cache = new SoundCache(folder);
            var contentKey = SoundCache.Key("https://p/v1", "https://p/audio/speech", "m", "v", "secret text");
            var audio = Encoding.ASCII.GetBytes("FAKE-MP3-DATA-0123456789");
            cache.Put("entry1", contentKey, audio, "audio/mpeg");
            var files = Directory.GetFiles(folder, "*.audio");
            Check(files.Length == 1, "cache encryption: one clip file");
            Check(Path.GetFileNameWithoutExtension(files[0]) != "entry1-" + contentKey,
                "cache encryption: the file name is a keyed MAC, not the content hash");
            var raw = File.ReadAllBytes(files[0]);
            Check(raw.AsSpan().StartsWith("OTENC1"u8) && !Encoding.ASCII.GetString(raw).Contains("FAKE-MP3-DATA"),
                "cache encryption: the clip on disk is ciphertext");
            Check(cache.Get("entry1", contentKey) is { } hit && hit.Audio.SequenceEqual(audio) && hit.MimeType == "audio/mpeg",
                "cache encryption: the clip decrypts back for playback");
            Check(File.Exists(Path.Combine(folder, "cache.key")), "cache encryption: the cache key exists");
            if (!OperatingSystem.IsWindows())
                Check((File.GetUnixFileMode(Path.Combine(folder, "cache.key")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0
                    && (File.GetUnixFileMode(files[0]) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    "cache encryption: key and clip are owner-only off Windows");

            // without the key a stored clip is a miss, never plaintext
            File.Delete(Path.Combine(folder, "cache.key"));
            Check(new SoundCache(folder).Get("entry1", contentKey) is null,
                "cache encryption: a keyless cache is a miss");

            // a clip that is not ciphertext is a miss, never served as plaintext
            new SoundCache(folder).Put("entry2", contentKey, [9], "audio/mpeg"); // recreates cache.key
            var fresh = Directory.GetFiles(folder, "*.audio").Single(f => Path.GetFileName(f).StartsWith("entry2-"));
            File.WriteAllBytes(fresh, Encoding.ASCII.GetBytes("audio/mpeg\nPLAINTEXT"));
            Check(new SoundCache(folder).Get("entry2", contentKey) is null,
                "cache encryption: a plaintext clip is a miss, not served");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Password protection wraps the data key, not the database: a locked store asks for the
    /// password, a wrong one is rejected, and a folder copied to a "new machine" unlocks with it.</summary>
    static void HistoryPasswordProtection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            var h = new History(dir);
            h.Add(new HistoryEntry { From = "en", To = "de", Source = "secret one", Output = "geheim eins" });
            h.Add(new HistoryEntry { From = "en", To = "de", Source = "secret two", Output = "geheim zwei" });

            // "set a password": wrap the existing data key, replace the key file
            var dek = HistoryCrypto.LoadKey(dir);
            Check(dek is { Length: 32 }, "password: the machine key loads before protection");
            File.WriteAllText(HistoryCrypto.KeyPath(dir), HistoryCrypto.WrapPassword(dek!, "hunter2"));
            Check(HistoryCrypto.IsPasswordHeader(File.ReadAllText(HistoryCrypto.KeyPath(dir))),
                "password: the key file is now password-wrapped");

            // "change password": re-wrap the same data key, the old password stops working
            var headerOne = HistoryCrypto.WrapPassword(dek!, "pw-one");
            var rewrapped = HistoryCrypto.UnwrapPassword(headerOne, "pw-one");
            var headerTwo = HistoryCrypto.WrapPassword(rewrapped!, "pw-two");
            Check(HistoryCrypto.IsPasswordHeader(headerTwo),
                "password: the changed key file is still password-wrapped");
            Check(HistoryCrypto.UnwrapPassword(headerTwo, "pw-two") is { } changed && changed.SequenceEqual(dek!),
                "password: changing re-wraps the same data key, the new password unwraps it");
            Check(HistoryCrypto.UnwrapPassword(headerTwo, "pw-one") is null,
                "password: the old password no longer unwraps after a change");

            // a crafted header must not turn unlock into a hours-long PBKDF2 grind
            var hugeParts = HistoryCrypto.WrapPassword(dek!, "pw").Split(':');
            hugeParts[3] = "2000000000";
            Check(HistoryCrypto.UnwrapPassword(string.Join(':', hugeParts), "pw") is null,
                "password: an out-of-range PBKDF2 iteration count is rejected");
            hugeParts[3] = "0";
            Check(HistoryCrypto.UnwrapPassword(string.Join(':', hugeParts), "pw") is null,
                "password: a zero PBKDF2 iteration count is rejected");

            // the settings dialog replaces the key file tmp+rename: nothing left behind, owner-only
            // off Windows, and the wrap still reads back (the dialog's private helper does exactly
            // this WriteRestrictedBytes + Move + RestrictFile sequence)
            var tmpKey = Path.Combine(dir, "keycheck.key");
            var wrap = HistoryCrypto.WrapPassword(dek!, "keyfile");
            StoreFile.WriteRestrictedBytes(tmpKey + ".tmp", Encoding.UTF8.GetBytes(wrap));
            File.Move(tmpKey + ".tmp", tmpKey, overwrite: true);
            StoreFile.RestrictFile(tmpKey);
            Check(!File.Exists(tmpKey + ".tmp"), "password: an atomic key write leaves no temp file");
            if (!OperatingSystem.IsWindows())
                Check((File.GetUnixFileMode(tmpKey) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    "password: the rewritten key file is owner-only off Windows");
            Check(HistoryCrypto.UnwrapPassword(File.ReadAllText(tmpKey), "keyfile") is { } fromKey
                  && fromKey.SequenceEqual(dek!),
                "password: the atomically written key file unwraps with its password");

            var locked = new History(dir);
            locked.Load();
            Check(locked.NeedsPassword && locked.Entries.Count == 0 && locked.RecoveryNotice is { Length: > 0 },
                "password: a password-protected store asks for the password and loads empty");
            Check(!locked.LoadWithPassword("wrong"), "password: a wrong password is rejected");
            Check(locked.LoadWithPassword("hunter2"), "password: the right password unlocks");
            Check(locked.Entries.Count == 2 && locked.Entries[0].Source == "secret two",
                "password: the unlocked store reads its entries");

            // saving while unlocked must keep the password wrap, not clobber the key file
            locked.Add(new HistoryEntry { From = "en", To = "de", Source = "secret three", Output = "geheim drei" });
            Check(File.ReadAllText(HistoryCrypto.KeyPath(dir)).StartsWith("pwd:v1:"),
                "password: saving keeps the password-wrapped key file");

            // "new machine": copy the folder, the machine key is useless there, the password unlocks
            var moved = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(moved);
            File.Copy(Path.Combine(dir, "history.db"), Path.Combine(moved, "history.db"));
            File.Copy(HistoryCrypto.KeyPath(dir), HistoryCrypto.KeyPath(moved));
            var newMachine = new History(moved);
            newMachine.Load();
            Check(newMachine.NeedsPassword, "password: a copied folder on a new machine asks for the password");
            Check(newMachine.LoadWithPassword("hunter2") && newMachine.Entries.Count == 3
                && newMachine.Entries[0].Source == "secret three",
                "password: the copied store unlocks on the new machine");
            Check(!File.ReadAllText(Path.Combine(moved, "history.db")).Contains("secret"),
                "password: the store on disk stays ciphertext");
            try { Directory.Delete(moved, true); } catch (Exception) { }

            // #8: a key file that wraps a DIFFERENT data key unwraps fine with the password but
            // fails the store's GCM tag — the unlock must report failure, not open an empty,
            // non-saving store as unlocked
            var wrongDek = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(HistoryCrypto.KeyPath(dir), HistoryCrypto.WrapPassword(wrongDek, "hunter2"));
            var storeBytes = File.ReadAllBytes(Path.Combine(dir, "history.db"));
            var mismatched = new History(dir);
            mismatched.Load();
            Check(mismatched.NeedsPassword, "password #8: the replaced key file still asks for the password");
            Check(!mismatched.LoadWithPassword("hunter2"),
                "password #8: an unlocked key that fails the store tag is not a successful unlock");
            Check(mismatched.RecoveryNotice is { Length: > 0 } && mismatched.Entries.Count == 0,
                "password #8: the failed unlock sets a recovery notice and loads nothing");
            mismatched.Add(new HistoryEntry { From = "en", To = "de", Source = "sneaky", Output = "schleich" });
            Check(File.ReadAllBytes(Path.Combine(dir, "history.db")).SequenceEqual(storeBytes),
                "password #8: the failed unlock does not overwrite the store");

            CheckBusySaveProbe(); // the background locked save from HistorySidebar finishes by now
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>The wrap header's iteration count is metadata, not a work factor: the KDF always
    /// runs at the build's own constant, so a header claiming millions of iterations is refused
    /// without a measurable KDF run, and past a cap of failed attempts unlock locks out until the
    /// process restarts.</summary>
    static void HistoryUnlockWorkFactor()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var pbkdf2 = (int)typeof(HistoryCrypto).GetField("Pbkdf2Iterations", flags)!.GetValue(null)!;
        var maxAttempts = (int)typeof(HistoryCrypto).GetField("MaxUnlockAttempts", flags)!.GetValue(null)!;
        var counter = typeof(HistoryCrypto).GetField("failedUnlockAttempts", flags)!;
        void ResetAttempts() => counter.SetValue(null, 0);

        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            var h = new History(dir);
            h.Add(new HistoryEntry { From = "en", To = "de", Source = "locked one", Output = "gesperrt eins" });
            var dek = HistoryCrypto.LoadKey(dir);
            Check(dek is { Length: 32 }, "work factor: the machine key loads before protection");
            var wrap = HistoryCrypto.WrapPassword(dek!, "hunter2");
            var parts = wrap.Split(':');
            Check(parts[3] == pbkdf2.ToString(),
                "work factor: the written header carries the build's own constant count");
            ResetAttempts();
            Check(HistoryCrypto.UnwrapPassword(wrap, "hunter2") is { } ok && ok.SequenceEqual(dek!),
                "work factor: a wrap written by this build still unwraps with its password");
            Check(HistoryCrypto.UnwrapPassword(wrap, "nope") is null,
                "work factor: a wrong password is still rejected");

            // any other count in the header is refused before any KDF work runs: null, fast
            ResetAttempts();
            var worst = 0L;
            foreach (var bad in new[] { "10000000", "9999999", "600001", "599999", "1", "0", "0600000", "2147483648", "-1", "abc", "" })
            {
                var p = (string[])parts.Clone();
                p[3] = bad;
                var sw = Stopwatch.StartNew();
                var r = HistoryCrypto.UnwrapPassword(string.Join(':', p), "hunter2");
                sw.Stop();
                worst = Math.Max(worst, sw.ElapsedMilliseconds);
                Check(r is null, $"work factor: header count '{bad}' does not unwrap");
            }
            Check(worst < 1000, $"work factor: every rejected count returned fast (worst {worst} ms)");

            // structural variants: wrong part count, wrong magic/version, broken or huge base64 salt
            ResetAttempts();
            var hugeSalt = (string[])parts.Clone();
            hugeSalt[2] = Convert.ToBase64String(new byte[1_000_000]);
            hugeSalt[3] = "10000000";
            var badB64 = (string[])parts.Clone();
            badB64[2] = "!!!not-base64!!!";
            var variants = new List<string>
            {
                wrap + ":extra",
                string.Join(':', parts.Take(6)),
                string.Join(':', new[] { "pwd", "v2", parts[2], parts[3], parts[4], parts[5], parts[6] }),
                string.Join(':', new[] { "pw", "v1", parts[2], parts[3], parts[4], parts[5], parts[6] }),
                string.Join(':', hugeSalt),
                string.Join(':', badB64),
                "",
            };
            var worstVar = 0L;
            foreach (var v in variants)
            {
                var sw = Stopwatch.StartNew();
                var r = HistoryCrypto.UnwrapPassword(v, "hunter2");
                sw.Stop();
                worstVar = Math.Max(worstVar, sw.ElapsedMilliseconds);
                Check(r is null, $"work factor: malformed header ({v.Length} chars) does not unwrap");
            }
            Check(worstVar < 1000, $"work factor: every malformed header returned fast (worst {worstVar} ms)");

            // lockout: past the cap of consecutive failed attempts even the correct password is
            // refused, on the store-level unlock path too, until the process restarts
            File.WriteAllText(HistoryCrypto.KeyPath(dir), wrap);
            var locked = new History(dir);
            locked.Load();
            Check(locked.NeedsPassword, "work factor: the password-wrapped store asks for the password");
            ResetAttempts();
            for (var i = 0; i < maxAttempts; i++)
                Check(!locked.LoadWithPassword("wrong"),
                    $"work factor: failed unlock attempt {i + 1}/{maxAttempts} is rejected");
            Check(!locked.LoadWithPassword("hunter2"),
                "work factor: past the attempt cap even the correct password is refused (lockout)");
            ResetAttempts();
            Check(locked.LoadWithPassword("hunter2") && locked.Entries.Count == 1
                && locked.Entries[0].Source == "locked one",
                "work factor: once the counter is cleared the correct password unlocks again");
            ResetAttempts();
        }
        finally
        {
            ResetAttempts();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static void HistoryRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var h = new History(dir);
        h.Add(new HistoryEntry { From = "en", To = "de", Source = "hello", Output = "hallo" });
        h.Add(new HistoryEntry { From = "auto", To = "fr", Source = "world", Output = "monde" });
        Check(h.Entries.Count == 2 && h.Entries[0].Source == "world", "history: newest first");
        h.Entries[1].Favorite = true;
        h.Entries[1].TranslitSource = "haro";
        h.Entries[1].TranslitTarget = "hallo";
        h.Entries[1].AlternativesDescription = "plain";
        h.Entries[1].Alternatives = [new Alternative { Text = "Hi", Explanation = "shorter" }];
        h.Entries[1].Insights = [new WordInsight { Word = "hallo", Kind = "greeting",
            Alternatives = [new Alternative { Text = "Guten Tag", Explanation = "formal" }] }];
        h.Save();
        var reloaded = new History(dir);
        reloaded.Load();
        Check(reloaded.Entries.Count == 2, "history: persisted");
        Check(reloaded.Entries.Any(e => e.Favorite), "history: favorite persisted");
        Check(reloaded.Entries[1].TranslitSource == "haro" && reloaded.Entries[1].TranslitTarget == "hallo",
            "history: transliteration persisted with its entry");
        Check(reloaded.Entries[1].AlternativesDescription == "plain"
            && reloaded.Entries[1].Alternatives is [{ Text: "Hi", Explanation: "shorter" }],
            "history: alternatives persisted with their entry");
        Check(reloaded.Entries[1].Insights is [{ Word: "hallo", Kind: "greeting", Alternatives: [{ Text: "Guten Tag" }] }],
            "history: word insights persisted with their entry");
        Check(new HistoryEntry().Alternatives.Count == 0 && new HistoryEntry().Insights.Count == 0
            && new HistoryEntry { AlternativesJson = "{corrupt" }.Insights.Count == 0,
            "history: missing or corrupt extras read back as empty");
        Check(reloaded.Entries[0].Route == "Detect Language → French", "history: route renders");
        Check(reloaded.Entries[1].Route == "English → German", "history: route renders known pair");
        reloaded.Clear();
        Check(reloaded.Entries.Count == 0, "history: clear");
        try { Directory.Delete(dir, true); } catch (Exception) { }

        var customDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var custom = new History(customDir); // directory does not exist yet
        custom.Add(new HistoryEntry { From = "en", To = "de", Source = "x", Output = "y" });
        Check(File.Exists(Path.Combine(customDir, "history.db")), "history: custom dir is created on save");
        if (!OperatingSystem.IsWindows())
            Check((File.GetUnixFileMode(Path.Combine(customDir, "history.db")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                "history: history.db is owner-only off Windows");
        if (!OperatingSystem.IsWindows())
        {
            // read after the bytes landed: the dir must already be 0700 when the temp files exist
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            const UnixFileMode SessionPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Check(File.GetUnixFileMode(customDir) == SessionPermissions,
                "history: the data dir is 0700 before the snapshot temp file lands");
            Check(File.GetUnixFileMode(Path.Combine(customDir, "history.db")) == OwnerOnly,
                "history: history.db is 0600 off Windows");
            new History(customDir).Load(); // exercises the decrypted load temp file too
            Check(Directory.GetFiles(customDir, "history.db.tmp-*").Length == 0
                  && Directory.GetFiles(customDir, "*.check").Length == 0
                  && Directory.GetFiles(customDir, "*.load-*").Length == 0,
                "history: save and load leave no plaintext temp, check or load files behind");
            Check(!Directory.Exists(Path.Combine(customDir, ".tmp"))
                  || Directory.GetFiles(Path.Combine(customDir, ".tmp")).Length == 0,
                "history: the .tmp scratch subdir is empty after save and load");
        }
        try { Directory.Delete(customDir, true); } catch (Exception) { }

        // env-home dir mode (#0055): the profile dir keeps the user's 0755, so the plaintext
        // snapshot temp must live in the 0700 .tmp subdir, never in the dir itself
        if (!OperatingSystem.IsWindows())
        {
            var homeDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            var previousHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
            try
            {
                Environment.SetEnvironmentVariable("HAKATA_HOME", homeDir);
                Directory.CreateDirectory(homeDir);
                const UnixFileMode Wide = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute; // 0755
                File.SetUnixFileMode(homeDir, Wide);
                var envHome = new History(homeDir);
                envHome.Add(new HistoryEntry { From = "en", To = "de", Source = "secret temp", Output = "geheim temp" });
                var tmpSub = Path.Combine(homeDir, ".tmp");
                Check(File.GetUnixFileMode(homeDir) == Wide,
                    "history: env-home dir mode keeps the user's 0755 on the profile dir");
                Check(Directory.Exists(tmpSub)
                      && File.GetUnixFileMode(tmpSub) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                    "history: the .tmp scratch subdir is 0700 even in env-home dir mode");
                Check(Directory.GetFiles(homeDir, "history.db.tmp-*").Length == 0
                      && Directory.GetFiles(homeDir, "history.db.load-*").Length == 0
                      && Directory.GetFiles(homeDir, "*.enc").Length == 0
                      && Directory.GetFiles(homeDir, "*.check").Length == 0,
                    "history: no plaintext temp sits in the 0755 profile dir after a save");
                // a killed save leaves its temp in .tmp; the next Load sweeps it once it is old —
                // a fresh temp may belong to an in-flight save and must be left alone
                File.WriteAllBytes(Path.Combine(tmpSub, "history.db.tmp-stale"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(tmpSub, "history.db.tmp-stale.enc"), new byte[] { 4, 5, 6 });
                File.WriteAllBytes(Path.Combine(tmpSub, "history.db.load-stale"), new byte[] { 7, 8, 9 });
                File.WriteAllBytes(Path.Combine(tmpSub, "history.db.tmp-live"), new byte[] { 10, 11 });
                var hourAgo = DateTime.UtcNow - TimeSpan.FromHours(1);
                File.SetLastWriteTimeUtc(Path.Combine(tmpSub, "history.db.tmp-stale"), hourAgo);
                File.SetLastWriteTimeUtc(Path.Combine(tmpSub, "history.db.tmp-stale.enc"), hourAgo);
                File.SetLastWriteTimeUtc(Path.Combine(tmpSub, "history.db.load-stale"), hourAgo);
                new History(homeDir).Load();
                Check(!File.Exists(Path.Combine(tmpSub, "history.db.tmp-stale"))
                      && !File.Exists(Path.Combine(tmpSub, "history.db.tmp-stale.enc"))
                      && !File.Exists(Path.Combine(tmpSub, "history.db.load-stale")),
                    "history: Load sweeps stale snapshot temp files from .tmp");
                Check(File.Exists(Path.Combine(tmpSub, "history.db.tmp-live")),
                    "history: Load leaves a fresh (in-flight save) temp file alone");
            }
            finally
            {
                Environment.SetEnvironmentVariable("HAKATA_HOME", previousHome);
                try { Directory.Delete(homeDir, true); } catch (Exception) { }
            }
        }

        // #0061: a plaintext store is refused wherever it sits — never loaded, never re-encrypted
        // in place; like any unreadable store it is kept aside under history.db.unreadable*
        if (!OperatingSystem.IsWindows())
        {
            var prevHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
            try
            {
                // a valid plaintext store with a canary row, built from a scratch profile's snapshot
                var scratch = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
                using (var hs = new History(scratch))
                {
                    hs.Load();
                    hs.Add(new HistoryEntry { From = "en", To = "de", Source = "canary-plain-0061", Output = "x" });
                }
                var plain = HistoryCrypto.Decrypt(HistoryCrypto.LoadKey(scratch)!, File.ReadAllBytes(Path.Combine(scratch, "history.db")));
                Directory.Delete(scratch, true);

                foreach (var wideMode in new[] { true, false })
                {
                    var planted = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
                    Directory.CreateDirectory(planted);
                    File.SetUnixFileMode(planted, wideMode
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                          | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite // 0777
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // 0700
                    File.WriteAllBytes(Path.Combine(planted, "history.db"), plain);
                    Environment.SetEnvironmentVariable("HAKATA_HOME", planted);
                    using (var hp = new History(planted))
                    {
                        hp.Load();
                        Check(!hp.Entries.Any(e => e.Source.Contains("canary-plain-0061"))
                              && hp.RecoveryNotice is { Length: > 0 },
                            "history #0061: a plaintext store is refused, not loaded");
                        Check(File.Exists(Path.Combine(planted, "history.db.unreadable"))
                              && File.ReadAllText(Path.Combine(planted, "history.db.unreadable")).Contains("canary-plain-0061")
                              && !File.Exists(Path.Combine(planted, "history.db")),
                            "history #0061: the planted plaintext store is kept aside, neither loaded nor re-encrypted");
                        Check((File.GetUnixFileMode(Path.Combine(planted, "history.db.unreadable"))
                               & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                            "history #0061: the moved-aside plaintext copy is owner-only, not world-readable");
                    }
                    Directory.Delete(planted, true);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("HAKATA_HOME", prevHome);
            }
        }

        // a store that cannot be written says so instead of failing silently
        var blockedDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(blockedDir);
        Directory.CreateDirectory(Path.Combine(blockedDir, "history.db")); // a directory where the database must go
        var blocked = new History(blockedDir);
        blocked.Add(new HistoryEntry { From = "en", To = "de", Source = "x", Output = "y" });
        Check(blocked.SaveNotice is { Length: > 0 } && blocked.SaveNotice.Contains("could not be saved"),
            "history: a failed save is reported, not swallowed");
        try { Directory.Delete(blockedDir, true); } catch (Exception) { }

        // #10: a mutation on a store that cannot be written still raises Changed (retention follows it)
        var roDir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var seed = new History(roDir);
        seed.Add(new HistoryEntry { From = "en", To = "de", Source = "a", Output = "b" });
        File.Delete(HistoryCrypto.KeyPath(roDir)); // an encrypted store without its key is not writable
        var ro = new History(roDir);
        ro.Load();
        Check(ro.RecoveryNotice is { Length: > 0 }, "history #10: the keyless store loads as not writable");
        var raised = 0;
        ro.Changed += () => raised++;
        ro.Add(new HistoryEntry { From = "en", To = "de", Source = "c", Output = "d" });
        Check(raised == 1 && ro.Entries.Count == 1,
            "history #10: Add on a non-writable store still raises Changed");
        try { Directory.Delete(roDir, true); } catch (Exception) { }

        // a cut at the limit never splits a surrogate pair
        var astral = new HistoryEntry { Source = new string('a', 89) + "😀" + new string('b', 10) };
        Check(!char.IsSurrogate(astral.Preview[^2]), "history: the preview cut never splits a surrogate pair");

    }

    /// <summary>The save's temp-snapshot read-back is capped on the open handle: a snapshot over
    /// the store cap aborts the save safely (previous history.db kept byte-identical, temps
    /// cleaned, failure reported) instead of sizing buffers from the temp file, and a normal
    /// save/load round-trip is unchanged.</summary>
    static void SaveSnapshotReadCap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(dir);
            // helper boundaries at the store cap (sparse files: the over-cap one is refused at
            // open and never read, the at-cap one still reads)
            var overCap = Path.Combine(dir, "over.tmp");
            using (var fs = File.Create(overCap)) fs.SetLength(StoreFile.MaxStoreBytes + 1);
            try { StoreFile.ReadAllBytesCapped(overCap, StoreFile.MaxStoreBytes); Check(false, "save temp cap: a temp over the store cap is refused before reading"); }
            catch (InvalidDataException) { Check(true, "save temp cap: a temp over the store cap is refused before reading"); }
            var atCap = Path.Combine(dir, "at.tmp");
            using (var fs = File.Create(atCap)) fs.SetLength(StoreFile.MaxStoreBytes);
            Check(StoreFile.ReadAllBytesCapped(atCap, StoreFile.MaxStoreBytes).Length == StoreFile.MaxStoreBytes,
                "save temp cap: a temp exactly at the store cap still reads");

            // a normal save writes the store and reloads (round trip unchanged)
            var h = new History(dir);
            h.Add(new HistoryEntry { From = "en", To = "de", Source = "hello", Output = "hallo" });
            var dbPath = Path.Combine(dir, "history.db");
            Check(h.SaveNotice is null && File.Exists(dbPath), "save temp cap: a normal save writes history.db with no notice");
            var reloaded = new History(dir);
            reloaded.Load();
            Check(reloaded.Entries.Count == 1 && reloaded.Entries[0].Source == "hello" && reloaded.RecoveryNotice is null,
                "save temp cap: a normal save reloads");
            var before = File.ReadAllBytes(dbPath);

            // 200 huge entries push the VACUUM temp past the store cap: the read-back is refused,
            // the save aborts, and the previous store survives untouched
            for (var i = 0; i < 200; i++)
                h.Entries.Insert(0, new HistoryEntry { From = "en", To = "de", Source = "s", Output = new string('x', 350_000) });
            h.Save();
            Check(h.SaveNotice is { Length: > 0 } && h.SaveNotice.Contains("could not be saved"),
                "save temp cap: an over-cap snapshot aborts the save with a notice");
            Check(File.Exists(dbPath) && File.ReadAllBytes(dbPath).SequenceEqual(before),
                "save temp cap: the previous history.db is kept byte-identical after an aborted save");
            var tmpDir = Path.Combine(dir, ".tmp");
            Check(!Directory.Exists(tmpDir) || Directory.GetFiles(tmpDir).Length == 0,
                "save temp cap: an aborted save leaves no temp files behind");

            // the store still saves and reloads afterwards
            h.Entries.Clear();
            h.Add(new HistoryEntry { From = "en", To = "de", Source = "after", Output = "danach" });
            var again = new History(dir);
            again.Load();
            Check(again.Entries.Count == 1 && again.Entries[0].Source == "after" && again.RecoveryNotice is null,
                "save temp cap: a small save after the aborted one persists and reloads");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    // ---- end to end against a local mock endpoint ------------------------
}
