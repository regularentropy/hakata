using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Hakata.Services;

/// <summary>The guard around fetching a file link a provider hands back: the socket connects only to
/// a public address that was judged before the connect, so a rebinding DNS answer or a hostile
/// response cannot turn the fetch into a probe of this machine or the local network.</summary>
internal static class LinkGuard
{
    /// <summary>
    /// The client a provider-handed file link is fetched with. No credentials, no redirects, and the
    /// socket connects to the exact address that was validated, so a DNS answer that changes between
    /// the check and the connect cannot slip through.
    /// </summary>
    internal static readonly HttpClient LinkHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = LinkConnectAsync,
    });

    static async ValueTask<Stream> LinkConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        // resolve once here and judge exactly the addresses returned; the socket then uses them
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        IPAddress? chosen = null;
        foreach (var address in addresses)
        {
            var mapped = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (IsPublicAddress(mapped)) { chosen = mapped; break; }
        }
        if (chosen is null)
            throw new HttpRequestException($"the file host {host} resolves only to local or private addresses");
        var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A link a provider hands back for its own file. Fetched only when it points at a public host, so a
    /// hostile response cannot make the app probe this machine or the local network.
    /// </summary>
    public static bool IsFetchableLink(string link, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https")) return false;
        if (IsLocalTarget(parsed)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Dispatches an external URL to the OS handler, but only after the same scheme and
    /// host validation a provider-handed link gets: a string taken from a remote response must
    /// never reach the shell unvalidated.</summary>
    public static bool OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsFetchableLink(url, out _)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool IsLocalTarget(Uri uri)
    {
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return true;
        // Uri.Host keeps the brackets on an IPv6 literal; the parser wants them gone
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        return !IsPublicAddress(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    }

    /// <summary>The gate on a client that carries credentials: a request with an
    /// Authorization header may only be sent to an https URL whose host resolves exclusively to
    /// public addresses. Judged on exactly the addresses the connect will use, so a DNS answer that
    /// changes between the check and the connect cannot slip a credential through. A headerless
    /// request (no key configured) is always allowed, so keyless local servers stay reachable.</summary>
    internal static bool MaySendCredentials(HttpRequestMessage request, IPAddress[] resolved)
    {
        if (request.Headers.Authorization is null) return true;
        if (!string.Equals(request.RequestUri?.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
        if (resolved.Length == 0) return false;
        foreach (var address in resolved)
        {
            var mapped = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (!IsPublicAddress(mapped)) return false;
        }
        return true;
    }

    /// <summary>True when the address is safe to connect to: not loopback, link-local, site-local,
    /// unique-local, private, CGNAT, multicast, reserved or unspecified. IPv4-mapped IPv6 is judged
    /// as its IPv4, and so are the transition prefixes that embed an IPv4 target (NAT64, 6to4,
    /// Teredo, IPv4-compatible), so an embedded 127.0.0.1 cannot hide behind a public-looking v6.</summary>
    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => PublicV4(b),
            AddressFamily.InterNetworkV6 => PublicV6(b),
            _ => false,
        };
    }

    static bool PublicV4(byte[] b) =>
        b[0] is not (0 or 10 or 100 or 127)
        && b[0] < 224                                              // multicast 224.0.0.0/4, reserved 240.0.0.0/4
        && !(b[0] == 172 && b[1] is >= 16 and <= 31)
        && !(b[0] == 192 && b[1] == 168)
        && !(b[0] == 192 && b[1] == 0 && b[2] == 0)                // 192.0.0.0/24 IETF protocol assignments
        && !(b[0] == 169 && b[1] == 254)                           // link-local
        && !(b[0] == 198 && b[1] is 18 or 19);                     // 198.18.0.0/15 benchmarking

    static bool PublicV6(byte[] b)
    {
        if ((b[0] & 0xfe) == 0xfc) return false;                   // unique local fc00::/7
        if (b[0] == 0xff) return false;                            // multicast ff00::/8
        // prefixes whose low bits carry an IPv4 destination: judge that destination, not the wrapper
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && IsZero(b, 4, 8))   // NAT64 64:ff9b::/96
            return PublicV4(b[12..]);
        if (b[0] == 0x20 && b[1] == 0x02)                                     // 6to4 2002::/16, v4 in bytes 2..5
            return PublicV4(b[2..6]);
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)            // Teredo 2001:0::/32, v4 obfuscated in low 32 bits
        {
            var v4 = new byte[4];
            for (var i = 0; i < 4; i++) v4[i] = (byte)~b[12 + i];
            return PublicV4(v4);
        }
        if (IsZero(b, 0, 12))                                                 // IPv4-compatible ::/96
            return PublicV4(b[12..]);
        return true;
    }

    static bool IsZero(byte[] b, int from, int count)
    {
        for (var i = from; i < from + count; i++) if (b[i] != 0) return false;
        return true;
    }
}
