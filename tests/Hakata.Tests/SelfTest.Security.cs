using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Hakata.Views;
using Xunit;

namespace Hakata;

public partial class SelfTest
{
    /// <summary>Security-audit entries C1/C2 (2026-11-16): a URL taken from a remote response may
    /// only reach the OS handler after LinkGuard validation. C1: UpdateViewModel.OpenUpdateLink
    /// on the feed's html_url. C2's window wiring is checked in UpdaterTest (--updatertest), which
    /// owns the headless window. The invariant: LinkGuard.OpenExternal refuses non-http(s) schemes
    /// and non-public hosts before any Process.Start.</summary>
    [Fact]
    public void SecurityLinks()
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
            Assert.True(!LinkGuard.OpenExternal(h), $"OpenExternal refuses [{h ?? "<null>"}] without dispatching");

        // the guard itself accepts the legit shape (public http(s)); no dispatch attempted here
        Assert.True(LinkGuard.IsFetchableLink("https://github.com/regularentropy/hakata", out _),
            "IsFetchableLink accepts a public https URL");
        Assert.True(LinkGuard.IsFetchableLink("http://93.184.216.34/x.png", out _),
            "public http image URL passes the fetchable-link check (scheme allowlist is http+https)");
        Assert.True(!LinkGuard.IsFetchableLink("https://127.0.0.1", out _), "IsFetchableLink rejects loopback");
        Assert.True(!LinkGuard.IsFetchableLink("https://user:pass@127.0.0.1/", out _), "IsFetchableLink rejects loopback behind userinfo");

        // C1: the update window's button refuses a feed html_url that is not a public http(s) URL
        var vm = new UpdateViewModel(new UpdateResponse("9.9.9", "file:///etc/hostname", "notes"), new AppMetadata());
        Assert.True(!vm.HasStatus, "update vm: no status before the first open attempt");
        vm.OpenUpdateLinkCommand.Execute(null);
        Assert.True(vm.HasStatus && vm.Status.Length > 0,
            "update vm: a non-http html_url is refused with a status message and never dispatched");
    }

    /// <summary>Provider-returned audio bytes must pass a managed WAV-header guard before any native
    /// decoder parses them — libsndfile CVE-2026-37555 overflows a 32-bit int on samplesperblock*blocks.
    /// Invariant: a hostile IMA-ADPCM header is rejected in managed code (the guard's
    /// InvalidDataException, so no SoundFileReader/AudioFileReader is ever constructed), while a sane
    /// clip and the exact int32 boundary still decode.</summary>
    [Fact]
    public void SecuritySpeechWavGuard()
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
        Assert.Throws<InvalidDataException>(() => SpeechPlayer.ThrowIfHostileWav(hostile, 48L + 32769u * 32771u));

        // boundary: 65535 * 32768 = 2147450880 <= int.MaxValue — accepted
        var boundary = ImaAdpcm(32768u * 32771u);
        SpeechPlayer.ThrowIfHostileWav(boundary, 48L + 32768u * 32771u);

        // declared-huge but real-small: clamped to the real size like libsndfile — accepted
        SpeechPlayer.ThrowIfHostileWav(hostile, hostile.Length);

        // samplesperblock not matching blockAlign (libsndfile requires equality) — rejected
        var mismatch = ImaAdpcm(1024, spb: 12345);
        Assert.Throws<InvalidDataException>(() => SpeechPlayer.ThrowIfHostileWav(mismatch, mismatch.Length));

        // zero channels / zero blockAlign — rejected
        var zeroCh = ImaAdpcm(1024);
        zeroCh[22] = 0; zeroCh[23] = 0; // channels field
        Assert.Throws<InvalidDataException>(() => SpeechPlayer.ThrowIfHostileWav(zeroCh, zeroCh.Length));

        // sane PCM clip from the recorder passes the guard
        var pcm = Wav.FromPcm(new byte[3200], MicrophoneRecorder.SampleRate, 1, 16);
        SpeechPlayer.ThrowIfHostileWav(pcm, pcm.Length); // recorder PCM WAV accepted

        // non-RIFF bytes pass through to the native decoder (the guard owns RIFF/WAVE only)
        SpeechPlayer.ThrowIfHostileWav(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 4);

        // sink level: the hostile sparse file through OpenAudio must fail with the guard's
        // InvalidDataException — pre-fix this reached libsndfile (SoundFileException, or abort rc 134)
        var path = Path.Combine(Path.GetTempPath(), "hakata-test-" + Guid.NewGuid().ToString("n") + ".wav");
        try
        {
            using (var fs = new FileStream(path, FileMode.CreateNew)) { fs.Write(hostile); fs.SetLength(48L + 32769u * 32771u); } // data chunk starts at 48
            Assert.Throws<InvalidDataException>(() => SpeechPlayer.OpenAudio(path)); // guard fires before any native decoder
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
    [Fact]
    public void SecurityCredentialedHostGuard()
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
            Assert.True(!Allowed(h, apiKey), $"credentialed host guard refuses [{h}] with a key attached");

        // transition-prefix wrappers judged directly on their address bytes (no DNS in the sandbox)
        byte[][] wrappers =
        {
            new byte[] { 0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 1 },   // NAT64 64:ff9b::127.0.0.1
            new byte[] { 0x20, 0x02, 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },     // 6to4 2002:7f00:1::
            new byte[] { 0x20, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0xff, 0xff, 0xfe }, // Teredo 2001::/~127.0.0.1
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 1 },             // IPv4-compatible ::127.0.0.1
        };
        foreach (var w in wrappers)
            Assert.True(!LinkGuard.MaySendCredentials(Req("https://[64:ff9b::1]/v1", apiKey), new[] { new IPAddress(w) }),
                $"credentialed host guard refuses wrapper [{new IPAddress(w)}]");

        // no key configured: headerless requests to local servers are still allowed
        foreach (var h in new[] { "http://127.0.0.1:18741/v1", "http://localhost:11434/v1", "http://[::1]:11434/v1" })
            Assert.True(Allowed(h, null), $"credentialed host guard allows headerless [{h}] (no key configured)");
        Assert.True(Allowed("http://127.0.0.1:18741/v1", "   "), "credentialed host guard allows a headerless request for a whitespace key");
        Assert.True(Req("http://127.0.0.1:18741/v1", null).Headers.Authorization is null,
            "credentialed host guard: WithAuth attaches no Authorization header without a key");

        // the real-provider path: https + public destination + key = allowed
        Assert.True(Allowed("https://93.184.216.34/v1", apiKey), "credentialed host guard allows https + public IP literal + key");
        Assert.True(LinkGuard.MaySendCredentials(Req("https://api.openai.com/v1", apiKey),
                new[] { IPAddress.Parse("151.101.1.69"), IPAddress.Parse("2606:4700::1111") }),
            "credentialed host guard allows https + public v4/v6 addresses + key");
        Assert.True(!LinkGuard.MaySendCredentials(Req("https://api.openai.com/v1", apiKey),
                new[] { IPAddress.Parse("151.101.1.69"), IPAddress.Parse("10.1.2.3") }),
            "credentialed host guard refuses a host that also resolves to a private address");
    }

    /// <summary>A URL named in settings for a request line must lose its query and fragment before
    /// it can enter one: a pasted console URL carrying ?api_key=… would otherwise be disclosed in
    /// the request line to the target host's logs and every on-path proxy — the same stripping the
    /// base-URL-derived routes already get. Guarded at the shared derivation and end-to-end at
    /// both speech call sites against a local listener, where the arriving request line must carry
    /// no query at all. The token below is an invented test value.</summary>
    [Fact]
    public async Task SecuritySpeechOverrideUrls()
    {
        const string secret = "test-query-token-0001";
        (string input, string expected)[] cases =
        {
            ("https://h/stt?api_key=" + secret, "https://h/stt"),
            ("https://h/stt?api_key=" + secret + "#frag", "https://h/stt"),
            ("https://h/stt#" + secret, "https://h/stt"),
            ("https://h/stt?a=1&api_key=" + secret, "https://h/stt"),
            ("https://h/stt?", "https://h/stt"),
            ("https://h/stt%3Fapi_key=" + secret, "https://h/stt"),
            ("https://h/stt%23" + secret, "https://h/stt"),
            ("HTTPS://H:8443/STT?api_key=" + secret, "https://h:8443/STT"),
            ("https://user:pass@h/stt?api_key=" + secret, "https://user:pass@h/stt"),
            ("https://h/stt", "https://h/stt"), // a clean override passes through untouched
        };
        foreach (var (input, expected) in cases)
            Assert.True(ProviderUrls.OverrideUrl(input) == expected,
                $"override url: [{input}] derives to [{expected}] — no query, no fragment");

        Assert.True(ProviderUrls.AudioUrl("https://x?api_key=" + secret) == "https://x/v1/audio/transcriptions",
            "override url: a query on the base is still dropped before the /v1 prefix on the stt route");
        Assert.True(ProviderUrls.SpeakUrl("https://x?token=xyz") == "https://x/v1/audio/speech",
            "override url: a query on the base is still dropped on the speak route");

        var port = 18800 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";
        var seen = new List<string>();
        using var listener = StartMock(prefix, ctx =>
        {
            lock (seen) seen.Add(ctx.Request.Url?.PathAndQuery ?? "<none>");
            var speech = (ctx.Request.Url?.AbsolutePath ?? "").EndsWith("/speech", StringComparison.Ordinal);
            var body = speech ? new byte[] { 0xFF, 0xFB, 0, 0 } : Encoding.UTF8.GetBytes("{\"text\":\"ok\"}");
            ctx.Response.ContentType = speech ? "audio/mpeg" : "application/json";
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
            return Task.CompletedTask;
        }, "override url: mock listener start: ");
        if (listener is null) return;
        try
        {
            var settings = new AppSettings
            {
                Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1" }],
                SpeechUrl = prefix + "stt?api_key=" + secret,
                SoundUrl = prefix + "speech?api_key=" + secret,
            };
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var provider = new OpenAiCompatibleProvider(http, settings);
            var (audio, _, _) = await provider.SpeakAsync("probe", CancellationToken.None);
            var text = await provider.TranscribeAsync(Wav.FromPcm(new byte[1600], 8000, 1, 16), CancellationToken.None);
            Assert.True(audio.Length == 4 && text == "ok",
                "override url: both speech calls still succeed against the local listener");
            List<string> lines;
            lock (seen) lines = [.. seen];
            Assert.True(lines.Contains("/speech"), "override url: the speak POST arrived as '/speech' with no query");
            Assert.True(lines.Contains("/stt"), "override url: the transcribe POST arrived as '/stt' with no query");
            Assert.True(lines.All(l => !l.Contains(secret) && !l.Contains("api_key", StringComparison.Ordinal)),
                "override url: no speech request line carries the query token");
        }
        finally { listener.Stop(); }
    }
    /// <summary>An image URL found in rendered markdown must resolve through the same guarded fetch
    /// as a validated link: only an absolute http(s) URL, judged at connect time on the exact
    /// address the socket uses — so file: and every other scheme, relative paths, and local or
    /// non-public targets are refused before anything is opened or connected, redirects are not
    /// followed, and the body is capped. The app's engines share one resolver instance, obtained
    /// here through the same factory the update window's engine is wired with.</summary>
    [AvaloniaFact]
    public async Task SecurityRenderedImageResources()
    {
        var engine = MarkdownEngines.CreateGuarded();
        Assert.True(ReferenceEquals(engine.Plugins.PathResolver, MarkdownEngines.GuardedPathResolver),
            "factory-built engine resolves images through the app's shared guarded resolver");
        var resolver = (GuardedImageResolver)MarkdownEngines.GuardedPathResolver;
        // refused without any fetch: hostile schemes, local files, local targets, malformed values
        string?[] refused =
        {
            "file:///etc/hostname", "FILE:///etc/hostname", "file:/etc/hostname", "file://localhost/etc/hostname",
            "file:///mnt/p/Development/C%23/hakata/README.md", @"\\evil\share\x.png",
            "data:image/png;base64,AAAA", "gopher://127.0.0.1:18743/x", "avares://hakata/Assets/app.png",
            "image.png", "./rel.png", "/abs/local.png", "", "   ",
            "http://127.0.0.1:18743/pixel.png", "https://[::1]/x.png", "http://0.0.0.0/x.png", "https://[::]/x.png",
            "https://localhost/x.png", "http://sub.local/x.png", "http://host.internal/x.png",
            "http://2130706433/x.png", "http://0x7f000001/x.png", "http://0177.0.0.1/x.png",
            "HTTP://127.0.0.1/x.png", "http://user:pass@127.0.0.1/x.png", "http://127.0.0.1./x.png",
            "http://[::ffff:127.0.0.1]/x.png", "http://[64:ff9b::7f00:1]/x.png", "http://[2002:7f00:1::]/x.png",
            "http://[2001::80ff:fffe]/x.png", "http://[::7f00:1]/x.png",
            "https://10.0.0.5/x.png", "https://172.16.0.1/x.png", "https://192.168.1.1/x.png",
            "https://169.254.169.254/x.png", "https://100.64.0.1/x.png", "https://[fd00::1]/x.png",
            "https://[fe80::1]/x.png", "https://[ff02::1]/x.png",
        };
        foreach (var r in refused)
        {
            var threw = false;
            try { using var s = await resolver.ResolveImageResource(r!); }
            catch (Exception) { threw = true; }
            Assert.True(threw, $"guarded image resolver refuses [{r ?? "<null>"}]");
        }
        // the client the resolver fetches with: no redirect following, address judged at connect time
        SocketsHttpHandler? handler = null;
        for (var t = typeof(HttpClient); t is not null && handler is null; t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                if (f.GetValue(LinkGuard.LinkHttp) is SocketsHttpHandler s) { handler = s; break; }
        Assert.True(handler is not null && !handler.AllowAutoRedirect,
            "the shared fetch client does not follow redirects");
        Assert.True(handler?.ConnectCallback is not null,
            "the shared fetch client validates the destination address at connect time");
        // the production cap the resolver applies to every image response is sane
        Assert.True(GuardedImageResolver.MaxImageBytes >= 1024 * 1024, "rendered-image body cap is set");
        // the cap mechanism itself, exercised with a hand-set small cap
        using var atCap = new MemoryStream(new byte[16]);
        var body = await ProviderHttp.ReadBytesCappedAsync(new StreamContent(atCap), 16, CancellationToken.None);
        Assert.True(body.Length == 16, "a body exactly at the cap is accepted");
        using var pastCap = new MemoryStream(new byte[17]);
        var capThrew = false;
        try { await ProviderHttp.ReadBytesCappedAsync(new StreamContent(pastCap), 16, CancellationToken.None); }
        catch (HttpRequestException) { capThrew = true; }
        Assert.True(capThrew, "a body past the cap is refused");
    }

    /// <summary>Release-notes markdown is parsed by a recursive engine, so the text handed to it
    /// is work-bounded first (TextRules.BoundRenderedMarkdown, applied at the release-notes
    /// property every render path reads): the total length is capped and every line's leading
    /// blockquote/list marker run is collapsed to a small depth, so content-shaped input cannot
    /// exhaust the parser stack or burn quadratic parse time. Ordinary notes pass through
    /// unchanged. The engine round-trips below only ever see sanitized text.</summary>
    [AvaloniaFact]
    public void SecurityRenderedMarkdownBounds()
    {
        // ordinary notes pass through byte-identical
        var notes = "## What's new\n- a fix\n- a feature\n\n> a quoted line\n>> a nested quote\n\n1. first\n2. second\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```\ncode fence\n```";
        Assert.True(TextRules.BoundRenderedMarkdown(notes) == notes, "ordinary release notes pass through unchanged");
        Assert.True(TextRules.BoundRenderedMarkdown("  ## What's new\n- a fix  ".Trim()) == "## What's new\n- a fix",
            "the trimmed notes shape passes through unchanged");
        Assert.True(TextRules.BoundRenderedMarkdown("") == "", "empty notes pass through unchanged");

        // hostile marker runs: blockquote, list, ordered list, mixed, CRLF-separated
        string[] deep =
        {
            string.Concat(Enumerable.Repeat("> ", 30_000)) + "x",
            string.Concat(Enumerable.Repeat("> ", 10_000)) + "x",
            string.Concat(Enumerable.Repeat(">", 60_000)) + "x",
            string.Concat(Enumerable.Repeat("- ", 30_000)) + "x",
            string.Concat(Enumerable.Repeat("* ", 30_000)) + "x",
            string.Concat(Enumerable.Repeat("1. ", 20_000)) + "x",
            string.Concat(Enumerable.Repeat("> - ", 10_000)) + "x",
            string.Concat(Enumerable.Repeat("- > ", 10_000)) + "x",
            string.Concat(Enumerable.Repeat("> > > x\r\n", 10_000)) + "x",
        };
        foreach (var d in deep)
        {
            var b = TextRules.BoundRenderedMarkdown(d);
            Assert.True(LeadingMarkers(b) <= TextRules.MaxMarkdownNesting,
                $"a {d.Length}-char marker run collapses to depth <= {TextRules.MaxMarkdownNesting} (got {LeadingMarkers(b)})");
        }
        // depth boundary: exactly at the cap is kept, one past it collapses
        var atCap = string.Concat(Enumerable.Repeat("> ", TextRules.MaxMarkdownNesting)) + "x";
        Assert.True(TextRules.BoundRenderedMarkdown(atCap) == atCap, "a marker run exactly at the depth cap is kept");
        var pastCap = string.Concat(Enumerable.Repeat("> ", TextRules.MaxMarkdownNesting + 1)) + "x";
        Assert.True(LeadingMarkers(TextRules.BoundRenderedMarkdown(pastCap)) == TextRules.MaxMarkdownNesting,
            "a marker run one past the depth cap collapses to the cap");

        // length cap: exactly at the cap is kept, one past it truncates
        var atLen = new string('a', TextRules.MaxRenderedMarkdownChars);
        Assert.True(TextRules.BoundRenderedMarkdown(atLen).Length == TextRules.MaxRenderedMarkdownChars,
            "notes exactly at the length cap are kept");
        var pastLen = new string('a', TextRules.MaxRenderedMarkdownChars + 1);
        Assert.True(TextRules.BoundRenderedMarkdown(pastLen).Length == TextRules.MaxRenderedMarkdownChars,
            "notes past the length cap are truncated to it");

        // engine round-trip on SANITIZED hostile input through the app's engine factory:
        // every transform must complete quickly (the unsanitized shapes abort the process,
        // so they are never fed to the engine here)
        var engine = MarkdownEngines.CreateGuarded();
        foreach (var d in deep)
        {
            var b = TextRules.BoundRenderedMarkdown(d);
            var sw = Stopwatch.StartNew();
            var ctrl = engine.Transform(b);
            sw.Stop();
            Assert.True(ctrl is not null && sw.ElapsedMilliseconds < 5_000,
                $"sanitized {d.Length}-char marker run renders via the app engine in {sw.ElapsedMilliseconds} ms");
        }
        // benign 60 KB notes: unchanged by the sanitizer and fast through the engine
        var benign = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 2222)) + new string('a', 7);
        Assert.True(benign.Length == 60_001 && TextRules.BoundRenderedMarkdown(benign) == benign,
            "a benign 60 001-char line passes through unchanged");
        var sw2 = Stopwatch.StartNew();
        var ctrl2 = engine.Transform(TextRules.BoundRenderedMarkdown(benign));
        sw2.Stop();
        Assert.True(ctrl2 is not null && sw2.ElapsedMilliseconds < 5_000,
            $"benign 60 KB notes render via the app engine in {sw2.ElapsedMilliseconds} ms");

        // the release-notes property (what the update window binds to) applies the bound
        var vm = new UpdateViewModel(new UpdateResponse("1.3.0", "https://example.invalid/x", deep[0]), new AppMetadata());
        Assert.True(LeadingMarkers(vm.ReleaseNotes) <= TextRules.MaxMarkdownNesting,
            "the release-notes property hands the engine depth-bounded text");
        var bigVm = new UpdateViewModel(
            new UpdateResponse("1.3.0", "https://example.invalid/x", new string('a', 300_000)), new AppMetadata());
        Assert.True(bigVm.ReleaseNotes.Length == TextRules.MaxRenderedMarkdownChars,
            "the release-notes property truncates oversize notes to the length cap");
    }

    /// <summary>Leading blockquote/list marker count of a line, mirroring the bound's scan.</summary>
    static int LeadingMarkers(string s)
    {
        var i = 0; var n = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t') { i++; continue; }
            if (c == '>') { n++; i++; continue; }
            if (c is '-' or '*' or '+' && i + 1 < s.Length && s[i + 1] == ' ') { n++; i++; continue; }
            if (char.IsDigit(c))
            {
                var j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j + 1 < s.Length && s[j] == '.' && s[j + 1] == ' ') { n++; i = j + 1; continue; }
            }
            break;
        }
        return n;
    }
}
