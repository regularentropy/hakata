using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata;

public static partial class SelfTest
{
    /// <summary>Security-audit entries C1/C2 (2026-11-16): a URL taken from a remote response may
    /// only reach the OS handler after LinkGuard validation. C1: UpdateViewModel.OpenUpdateLink
    /// on the feed's html_url. C2's window wiring is checked in UpdaterTest (--updatertest), which
    /// owns the headless window. The invariant: LinkGuard.OpenExternal refuses non-http(s) schemes
    /// and non-public hosts before any Process.Start.</summary>
    static void SecurityLinks()
    {
        // refused before any dispatch: hostile schemes, local targets, malformed values
        string?[] hostile =
        {
            "file:///etc/hostname", "FILE:///etc/hostname", "file:/etc/hostname", "file://localhost/etc/hostname",
            "javascript:alert(1)", "data:text/html,<script>1</script>", @"\\evil\share\x.exe",
            "https://127.0.0.1/x", "https://[::1]/x", "https://0.0.0.0/", "https://localhost/x",
            "https://host.local/x", "https://host.internal/x", " file:///etc/hostname",
            "file:///etc/hostname\r\nhttps://github.com", "", null, "   ",
        };
        foreach (var h in hostile)
            Check(!LinkGuard.OpenExternal(h), $"OpenExternal refuses [{h ?? "<null>"}] without dispatching");

        // the guard itself accepts the legit shape (public http(s)); no dispatch attempted here
        Check(LinkGuard.IsFetchableLink("https://github.com/regularentropy/hakata", out _),
            "IsFetchableLink accepts a public https URL");
        Check(!LinkGuard.IsFetchableLink("https://127.0.0.1", out _), "IsFetchableLink rejects loopback");
        Check(!LinkGuard.IsFetchableLink("https://user:pass@127.0.0.1/", out _), "IsFetchableLink rejects loopback behind userinfo");

        // C1: the update window's button refuses a feed html_url that is not a public http(s) URL
        var vm = new UpdateViewModel(new UpdateResponse("9.9.9", "file:///etc/hostname", "notes"), new AppMetadata());
        Check(!vm.HasStatus, "update vm: no status before the first open attempt");
        vm.OpenUpdateLinkCommand.Execute(null);
        Check(vm.HasStatus && vm.Status.Length > 0,
            "update vm: a non-http html_url is refused with a status message and never dispatched");
    }

    /// <summary>Provider-returned audio bytes must pass a managed WAV-header guard before any native
    /// decoder parses them — libsndfile CVE-2026-37555 overflows a 32-bit int on samplesperblock*blocks.
    /// Invariant: a hostile IMA-ADPCM header is rejected in managed code (the guard's
    /// InvalidDataException, so no SoundFileReader/AudioFileReader is ever constructed), while a sane
    /// clip and the exact int32 boundary still decode.</summary>
    static void SecuritySpeechWavGuard()
    {
        static byte[] ImaAdpcm(uint dataLen, int blockAlign = 32771, int spb = 65535)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36u + dataLen); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(20u);
            w.Write((ushort)0x11); w.Write((ushort)1); w.Write(8000u);
            w.Write(8000u * (uint)blockAlign / (uint)spb);
            w.Write((ushort)blockAlign); w.Write((ushort)4); w.Write((ushort)2); w.Write((ushort)spb);
            w.Write("data"u8); w.Write(dataLen);
            return ms.ToArray(); // bare 48-byte header (fmt chunk 20); the fileLen argument carries the sparse size
        }

        // overflow: 65535 * 32769 = 2147516415 > int.MaxValue — rejected when the real file is that big
        var hostile = ImaAdpcm(32769u * 32771u);
        try { SpeechPlayer.ThrowIfHostileWav(hostile, 48L + 32769u * 32771u); Check(false, "speech wav guard: overflow header rejected"); }
        catch (InvalidDataException) { Check(true, "speech wav guard: samplesperblock*blocks > int32.MaxValue rejected in managed code"); }

        // boundary: 65535 * 32768 = 2147450880 <= int.MaxValue — accepted
        var boundary = ImaAdpcm(32768u * 32771u);
        try { SpeechPlayer.ThrowIfHostileWav(boundary, 48L + 32768u * 32771u); Check(true, "speech wav guard: product <= int32.MaxValue accepted"); }
        catch (InvalidDataException) { Check(false, "speech wav guard: int32 boundary wrongly rejected"); }

        // declared-huge but real-small: clamped to the real size like libsndfile — accepted
        try { SpeechPlayer.ThrowIfHostileWav(hostile, hostile.Length); Check(true, "speech wav guard: declared size clamped to real file length"); }
        catch (InvalidDataException) { Check(false, "speech wav guard: clamped small-real-file header wrongly rejected"); }

        // samplesperblock not matching blockAlign (libsndfile requires equality) — rejected
        var mismatch = ImaAdpcm(1024, spb: 12345);
        try { SpeechPlayer.ThrowIfHostileWav(mismatch, mismatch.Length); Check(false, "speech wav guard: spb mismatch rejected"); }
        catch (InvalidDataException) { Check(true, "speech wav guard: IMA ADPCM samplesperblock mismatch rejected"); }

        // zero channels / zero blockAlign — rejected
        var zeroCh = ImaAdpcm(1024);
        zeroCh[22] = 0; zeroCh[23] = 0; // channels field
        try { SpeechPlayer.ThrowIfHostileWav(zeroCh, zeroCh.Length); Check(false, "speech wav guard: zero channels rejected"); }
        catch (InvalidDataException) { Check(true, "speech wav guard: zero channels rejected"); }

        // sane PCM clip from the recorder passes the guard
        var pcm = Wav.FromPcm(new byte[3200], MicrophoneRecorder.SampleRate, 1, 16);
        SpeechPlayer.ThrowIfHostileWav(pcm, pcm.Length);
        Check(true, "speech wav guard: recorder PCM WAV accepted");

        // non-RIFF bytes pass through to the native decoder (the guard owns RIFF/WAVE only)
        SpeechPlayer.ThrowIfHostileWav(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 4);
        Check(true, "speech wav guard: non-RIFF bytes pass through");

        // sink level: the hostile sparse file through OpenAudio must fail with the guard's
        // InvalidDataException — pre-fix this reached libsndfile (SoundFileException, or abort rc 134)
        var path = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n") + ".wav");
        try
        {
            using (var fs = new FileStream(path, FileMode.CreateNew)) { fs.Write(hostile); fs.SetLength(48L + 32769u * 32771u); } // data chunk starts at 48
            try { SpeechPlayer.OpenAudio(path); Check(false, "speech wav guard: OpenAudio rejects the hostile file"); }
            catch (InvalidDataException) { Check(true, "speech wav guard: OpenAudio throws the guard's InvalidDataException before any native decoder"); }
            catch (Exception e) { Check(false, $"speech wav guard: OpenAudio threw {e.GetType().Name} (native decoder reached) — {e.Message}"); }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>The main HttpClient must never carry an Authorization header to a cleartext or
    /// non-public destination. The invariant is guarded at the sink (AppServices.ConnectGuardedAsync
    /// → LinkGuard.MaySendCredentials): with a key attached, http:// and any
    /// local/private/ULA/link-local/CGNAT target is refused — including IPv4-mapped,
    /// NAT64/6to4/Teredo/IPv4-compatible wrappers, decimal/hex/octal IP literals, "localhost",
    /// userinfo tricks and trailing-dot hosts; without a key, headerless requests to local servers
    /// stay allowed (keyless Ollama/LM Studio); https + public host + key is allowed. The key below
    /// is an invented test token.</summary>
    static void SecurityCredentialedHostGuard()
    {
        const string apiKey = "test-bearer-key-0001";
        static HttpRequestMessage Req(string url, string? key)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            ProviderHttp.WithAuth(req, key);
            return req;
        }
        static IPAddress[] Resolve(string url)
        {
            var host = new Uri(url).Host;
            if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return new[] { ip };
            try { return Dns.GetHostAddressesAsync(host).GetAwaiter().GetResult(); }
            catch (Exception) { return Array.Empty<IPAddress>(); } // unresolvable: refused downstream anyway
        }
        static bool Allowed(string url, string? key) => LinkGuard.MaySendCredentials(Req(url, key), Resolve(url));

        // with a key attached, every cleartext or non-public target is refused
        string[] hostile =
        {
            "http://127.0.0.1:18741/v1", "https://127.0.0.1:18741/v1", "http://[::1]:18741/v1", "https://[::1]/v1",
            "http://0.0.0.0:18741/v1", "https://0.0.0.0/", "https://[::]/v1",
            "http://10.0.0.5/v1", "https://172.16.0.1/v1", "https://172.31.255.255/v1", "https://192.168.1.1/v1",
            "https://169.254.169.254/v1", "https://100.64.0.1/v1", "https://192.0.0.8/v1", "https://198.18.0.1/v1",
            "https://224.0.0.1/v1", "https://240.0.0.1/v1", "https://[fd00::1]/v1", "https://[fe80::1]/v1", "https://[ff02::1]/v1",
            "http://localhost:18741/v1", "https://localhost/v1", "http://127.0.0.1.:18741/v1", "https://sub.localhost/v1",
            "http://user:pass@127.0.0.1:18741/v1", "HTTP://127.0.0.1:18741/v1", "http://x@127.0.0.1:18741/v1",
            "http://2130706433:18741/v1", "http://0x7f000001:18741/v1", "http://0177.0.0.1:18741/v1",
            "http://[::ffff:127.0.0.1]:18741/v1", "http://[64:ff9b::7f00:1]:18741/v1",
            "http://[2002:7f00:1::]:18741/v1", "http://[2001::80ff:fffe]:18741/v1", "http://[::7f00:1]:18741/v1",
            "http://93.184.216.34/v1", // even a public host: cleartext http must not carry the key
        };
        foreach (var h in hostile)
            Check(!Allowed(h, apiKey), $"credentialed host guard refuses [{h}] with a key attached");

        // transition-prefix wrappers judged directly on their address bytes (no DNS in the sandbox)
        byte[][] wrappers =
        {
            new byte[] { 0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 1 },   // NAT64 64:ff9b::127.0.0.1
            new byte[] { 0x20, 0x02, 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },     // 6to4 2002:7f00:1::
            new byte[] { 0x20, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0xff, 0xfe }, // Teredo 2001::/~127.0.0.1
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 1 },             // IPv4-compatible ::127.0.0.1
        };
        foreach (var w in wrappers)
            Check(!LinkGuard.MaySendCredentials(Req("https://[64:ff9b::1]/v1", apiKey), new[] { new IPAddress(w) }),
                $"credentialed host guard refuses wrapper [{new IPAddress(w)}]");

        // no key configured: headerless requests to local servers are still allowed
        foreach (var h in new[] { "http://127.0.0.1:18741/v1", "http://localhost:11434/v1", "http://[::1]:11434/v1" })
            Check(Allowed(h, null), $"credentialed host guard allows headerless [{h}] (no key configured)");
        Check(Allowed("http://127.0.0.1:18741/v1", "   "), "credentialed host guard allows a headerless request for a whitespace key");
        Check(Req("http://127.0.0.1:18741/v1", null).Headers.Authorization is null,
            "credentialed host guard: WithAuth attaches no Authorization header without a key");

        // the real-provider path: https + public destination + key = allowed
        Check(Allowed("https://93.184.216.34/v1", apiKey), "credentialed host guard allows https + public IP literal + key");
        Check(LinkGuard.MaySendCredentials(Req("https://api.openai.com/v1", apiKey),
                new[] { IPAddress.Parse("151.101.1.69"), IPAddress.Parse("2606:4700::1111") }),
            "credentialed host guard allows https + public v4/v6 addresses + key");
        Check(!LinkGuard.MaySendCredentials(Req("https://api.openai.com/v1", apiKey),
                new[] { IPAddress.Parse("151.101.1.69"), IPAddress.Parse("10.1.2.3") }),
            "credentialed host guard refuses a host that also resolves to a private address");
    }
}
