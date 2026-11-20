using System;
using System.Security.Cryptography;
using System.Text;

namespace Hakata.Models;

/// <summary>
/// The API key as it sits in settings.json. On Windows it is encrypted with DPAPI for the current
/// user, so a copy of the file (backup, roaming profile, another account) carries no working key.
/// Off Windows there is no such protection: the key is stored in plain text, and settings.json is
/// created owner-only (0600) so other users cannot read it. The entropy string is format marking
/// that binds the ciphertext to this app — it is not protection from another process of the same
/// user. A plain value is still read, so an existing file and a hand-typed key keep working.
/// </summary>
public static class KeyProtector
{
    /// <summary>Marks an encrypted value; anything else is a plain key from a file written before this.</summary>
    const string Marker = "enc:v1:";

    // entropy binds the ciphertext to this app: another program running as the same user cannot decrypt it
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("hakata.settings.v1");

    /// <summary>Whether this platform can keep the key encrypted at rest at all.</summary>
    public static bool Available => OperatingSystem.IsWindows();

    public static string Protect(string key)
    {
        // shortcut: off Windows the key is stored plaintext (the 0600 settings.json is the protection);
        // no keyring is used — upgrade to libsecret/keyring when a shared-machine threat model needs it
        if (key.Length == 0 || !OperatingSystem.IsWindows()) return key;
        return Marker + Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// The key back, or empty when the ciphertext belongs to another user, machine or app. An empty key is
    /// visible as a 401 from the provider; sending the ciphertext as a bearer token would not be.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (!stored.StartsWith(Marker, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Marker.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return "";
        }
    }
}
