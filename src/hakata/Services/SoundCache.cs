using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>
/// Rendered speech clips, one file per history entry and variant. The same text through the same
/// provider, model and voice plays back without a request; a file is named after its entry, so the
/// retention policy can drop the audio of entries that age out of the keep list.
/// Clips are AES-256-GCM encrypted at rest with the cache's own key (cache.key in the folder,
/// DPAPI-wrapped on Windows, 0600 elsewhere) — a copied folder yields no plaintext audio — and the
/// file name is an HMAC of the content hash, so it does not even leak a verifiable hash of the text.
/// </summary>
public sealed class SoundCache(string folder)
{
    readonly string folder = folder;

    public string Folder => folder;

    /// <summary>One key per distinct render: provider, endpoint, model, voice and text.</summary>
    public static string Key(string baseUrl, string url, string model, string voice, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', baseUrl.Trim(), url.Trim(), model, voice, text))))[..32].ToLowerInvariant();

    /// <summary>The clip for this entry and variant, or null when it was never rendered (or cannot be read).</summary>
    public (byte[] Audio, string MimeType)? Get(string entryId, string key)
    {
        try
        {
            var k = HistoryCrypto.LoadKey(folder, "cache.key");
            if (k is null) return null; // no readable key: the cache is a miss, never an error
            var bytes = File.ReadAllBytes(PathOf(entryId, key, k));
            if (!HistoryCrypto.IsEncrypted(bytes)) return null;
            var plain = HistoryCrypto.Decrypt(k, bytes);
            var split = Array.IndexOf(plain, (byte)'\n');
            if (split <= 0) return null;
            return (plain[(split + 1)..], Encoding.UTF8.GetString(plain, 0, split));
        }
        catch (Exception)
        {
            return null; // an unreadable or missing cache is a miss, never an error
        }
    }

    public void Put(string entryId, string key, byte[] audio, string mimeType)
    {
        try
        {
            Directory.CreateDirectory(folder);
            StoreFile.RestrictDir(folder); // clips are private; an env-mode dir may exist wider (S1.8)
            var k = HistoryCrypto.LoadOrCreateKey(folder, "cache.key");
            var plain = Encoding.UTF8.GetBytes(mimeType + "\n").Concat(audio).ToArray();
            var path = PathOf(entryId, key, k);
            // a clip of the user's text is private, like the history: the mode is set on the file
            // handle before the first byte lands, so the clip is never briefly world-readable
            StoreFile.WriteRestrictedBytes(path, HistoryCrypto.Encrypt(k, plain));
        }
        catch (Exception)
        {
            // a cache that cannot write costs a request, not the feature
        }
    }

    /// <summary>The files this cache wrote, as (path, entryId): only names shaped <entryId>-<32 hex>.audio.
    /// Empty when the folder does not exist.</summary>
    static IEnumerable<(string Path, string EntryId)> OwnClips(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.audio"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var cut = name.IndexOf('-');
            // only a file this cache wrote is its to delete: <entryId>-<32 hex>.audio (prune's rule, too)
            if (cut <= 0 || !IsCacheKey(name[(cut + 1)..])) continue;
            yield return (path, name[..cut]);
        }
    }

    /// <summary>Deletes every clip whose entry is not in the keep list; that is the whole retention policy.</summary>
    public void Prune(IReadOnlyCollection<string> keepEntryIds)
    {
        try
        {
            var keep = new HashSet<string>(keepEntryIds, StringComparer.Ordinal);
            foreach (var (path, entryId) in OwnClips(folder))
            {
                if (keep.Contains(entryId)) continue;
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // a cache that cannot prune just grows; the next press tries again
        }
    }

    // GetFileName keeps an entry id with separators from walking out of the folder; the name is a keyed
    // MAC of the content hash, so the folder does not reveal a verifiable hash of the text
    string PathOf(string entryId, string key, byte[] cacheKey)
    {
        var mac = Convert.ToHexString(HMACSHA256.HashData(cacheKey, Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(folder, Path.GetFileName(entryId) + "-" + mac + ".audio");
    }

    static bool IsCacheKey(string value) =>
        value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
