using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Hakata.Models;

namespace Hakata.Infrastructure;

/// <summary>
/// AES-256-GCM for the history snapshot on disk. The database itself lives in memory; every Save
/// serializes it to a temp file, encrypts that, verifies the ciphertext by decrypting it again, and
/// only then atomically replaces history.db — a crash mid-save leaves the previous file intact.
/// The 32-byte key is random per install: DPAPI-wrapped on Windows (same scheme as the API keys in
/// settings.json), a raw 0600 file off Windows. No SQLCipher/SEE: AES-GCM is in the BCL, so there is
/// no native encryption dependency to license.
/// </summary>
internal static class HistoryCrypto
{
    // file layout: magic + 12-byte GCM nonce + 16-byte GCM tag + ciphertext
    static readonly byte[] Magic = "OTENC1"u8.ToArray();
    const int NonceSize = 12;
    const int TagSize = 16;

    public static bool IsEncrypted(byte[] file) => file.AsSpan().StartsWith(Magic);

    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plaintext.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, cipher, tag);
        var file = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
        Magic.CopyTo(file, 0);
        nonce.CopyTo(file, Magic.Length);
        tag.CopyTo(file, Magic.Length + NonceSize);
        cipher.CopyTo(file, Magic.Length + NonceSize + TagSize);
        return file;
    }

    /// <summary>Throws <see cref="CryptographicException"/> on a tampered or wrongly-keyed file.</summary>
    public static byte[] Decrypt(byte[] key, byte[] file)
    {
        // a file cut below magic + nonce + tag is a truncated store: report it as a failed
        // decryption, not as a bad argument, so Load treats it like any tampered store
        if (file.Length < Magic.Length + NonceSize + TagSize || !IsEncrypted(file))
            throw new CryptographicException("not an encrypted history");
        var nonce = file.AsSpan(Magic.Length, NonceSize);
        var tag = file.AsSpan(Magic.Length + NonceSize, TagSize);
        var cipher = file.AsSpan(Magic.Length + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    public static string KeyPath(string dir, string fileName = "history.key") => Path.Combine(dir, fileName);

    public enum KeyKind { Missing, Machine, Password, Unreadable }

    /// <summary>How the key file reads: the data key itself when machine-unwrappable, Password when a
    /// password is needed, Unreadable when the file exists but cannot be unwrapped (another user or
    /// machine on Windows, or a corrupt file).</summary>
    public sealed record KeyRead(KeyKind Kind, byte[]? Dek);

    public static bool IsPasswordHeader(string text) => text.StartsWith("pwd:v1:", StringComparison.Ordinal);

    public static KeyRead ReadKey(string dir, string fileName = "history.key")
    {
        var path = KeyPath(dir, fileName);
        if (!File.Exists(path)) return new(KeyKind.Missing, null);
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception) { return new(KeyKind.Unreadable, null); }
        if (raw.AsSpan().StartsWith("pwd:v1:"u8)) return new(KeyKind.Password, null);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Unprotect returns "" when the DPAPI blob belongs to another user or machine
                var dek = KeyProtector.Unprotect(Encoding.UTF8.GetString(raw));
                return dek.Length == 0 ? new(KeyKind.Unreadable, null)
                    : new(KeyKind.Machine, Convert.FromBase64String(dek));
            }
            catch (Exception)
            {
                // a blob whose unwrapped plaintext is not the base64 key is unreadable, not an error
                return new(KeyKind.Unreadable, null);
            }
        }
        return raw.Length == 32 ? new(KeyKind.Machine, raw) : new(KeyKind.Unreadable, null);
    }

    /// <summary>The machine-unwrappable data key, or null. Never creates the file.</summary>
    public static byte[]? LoadKey(string dir, string fileName = "history.key") =>
        ReadKey(dir, fileName) is { Kind: KeyKind.Machine, Dek: { } dek } ? dek : null;

    /// <summary>The key-file bytes for a machine-bound wrap: DPAPI ciphertext on Windows, raw 0600 off it.</summary>
    public static byte[] MachineKeyFileBytes(byte[] dek) =>
        OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetBytes(KeyProtector.Protect(Convert.ToBase64String(dek)))
            : dek;

    /// <summary>Loads the key, creating a fresh random one (and its file) when none exists yet. A
    /// password-wrapped or unreadable key file is never clobbered: null is returned instead.</summary>
    public static byte[]? LoadOrCreateKey(string dir, string fileName = "history.key")
    {
        var existing = ReadKey(dir, fileName);
        if (existing is { Kind: KeyKind.Machine, Dek: { } dek }) return dek;
        if (existing.Kind != KeyKind.Missing) return null;
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(dir);
        var path = KeyPath(dir, fileName);
        // write with the 0600 mode on the handle before the first byte lands — a plain
        // WriteAllBytes would expose the raw DEK in a 0644 window until the RestrictFile below
        StoreFile.WriteRestrictedBytes(path, MachineKeyFileBytes(key));
        return key;
    }

    // envelope wrap: the password never touches the database, it wraps the 32-byte data key, so setting
    // or changing a password rewrites only this small file, and a copied folder + the password unlocks
    // the store on any machine
    const int Pbkdf2Iterations = 600_000;
    // a crafted key-file header must not turn unlock into a hours-long PBKDF2 grind (DoS)
    const int MaxPbkdf2Iterations = 10_000_000;

    /// <summary>pwd:v1:salt:iterations:nonce:tag:wrapped — PBKDF2-SHA256 derives the KEK, AES-GCM wraps the DEK.</summary>
    public static string WrapPassword(byte[] dek, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var wrapped = new byte[dek.Length];
        using (var gcm = new AesGcm(kek, TagSize)) gcm.Encrypt(nonce, dek, wrapped, tag);
        return string.Join(':',
            "pwd", "v1", Convert.ToBase64String(salt), Pbkdf2Iterations.ToString(),
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(wrapped));
    }

    /// <summary>The data key, or null on a wrong password (the GCM tag fails) or a malformed header.</summary>
    public static byte[]? UnwrapPassword(string header, string password)
    {
        try
        {
            var parts = header.Split(':');
            if (parts.Length != 7 || parts[0] != "pwd" || parts[1] != "v1") return null;
            if (!int.TryParse(parts[3], out var iterations) || iterations is < 1 or > MaxPbkdf2Iterations) return null;
            var kek = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
                Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, 32);
            var wrapped = Convert.FromBase64String(parts[6]);
            var dek = new byte[wrapped.Length];
            using var gcm = new AesGcm(kek, TagSize);
            gcm.Decrypt(Convert.FromBase64String(parts[4]), wrapped, Convert.FromBase64String(parts[5]), dek);
            return dek;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
