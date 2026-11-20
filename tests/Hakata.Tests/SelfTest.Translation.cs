using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata;

/// <summary>Dependency-free checks for the parts that break silently: endpoints, SSE, prompts, JSON parsing, history.</summary>
public static partial class SelfTest
{
    /// <summary>The shared mock SSE answer: chunked text/event-stream, one write per chunk, then close.</summary>
    static async Task WriteSse(HttpListenerContext ctx, params string[] chunks)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
        foreach (var chunk in chunks) await w.WriteAsync(chunk);
        await w.FlushAsync();
        w.Close();
    }

    /// <summary>#0035: alternatives and insights run concurrently without cancelling each other,    /// and Cancel() still stops an extra that is in flight.</summary>
    static async Task ExtrasDontCancelEachOther()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var coordinator = new TranslationCoordinator(new ExtraStub(), new AppSettings(), new History(dir));
        try
        {
            var alt = coordinator.AlternativesAsync("hello", "hallo", "en", "de");
            var insights = coordinator.InsightsAsync("hello", "hallo", "en", "de");
            try
            {
                await Task.WhenAll(alt, insights);
                Check(true, "extras: concurrent alternatives and insights both finish");
            }
            catch (Exception e)
            {
                Check(false, $"extras: concurrent alternatives and insights both finish ({e.GetType().Name}: {e.Message})");
            }

            var inFlight = coordinator.AlternativesAsync("hello", "hallo", "en", "de");
            coordinator.Cancel();
            try
            {
                await inFlight;
                Check(false, "extras: Cancel() stops an in-flight extra");
            }
            catch (OperationCanceledException)
            {
                Check(true, "extras: Cancel() stops an in-flight extra");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    /// <summary>Content that promises more bytes than it sends, like a connection dropped mid-body.</summary>
    sealed class DeclaredContent : HttpContent
    {
        readonly byte[] payload;
        readonly long declared;
        internal DeclaredContent(byte[] payload, long declared) { this.payload = payload; this.declared = declared; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(payload).AsTask();
        protected override bool TryComputeLength(out long length) { length = declared; return true; }
    }

    static void Sse()
    {
        Check(SseParser.ParseSseDelta("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}") == "Hi", "sse: delta");
        Check(SseParser.ParseSseDelta("data: {\"choices\":[{\"message\":{\"content\":\"Plain\"}}]}") == "Plain", "sse: non-streaming body");
        Check(SseParser.ParseSseDelta("data: [DONE]") == null, "sse: done marker");
        Check(SseParser.ParseSseDelta("data: {\"choices\":[{\"delta\":{}}]}") == null, "sse: empty delta");
        Check(SseParser.ParseSseDelta(": keepalive") == null, "sse: comment line");
        Check(SseParser.ParseSseDelta("data: {broken") == null, "sse: malformed json");

        var usage = SseParser.ParseSseUsage("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}");
        Check(usage is { PromptTokens: 12, CompletionTokens: 7, Estimated: false }, "sse: usage chunk parsed");
        Check(SseParser.ParseSseUsage("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}") is null,
            "sse: a content chunk is not usage");
        Check(SseParser.ParseSseUsage("data: [DONE]") is null, "sse: done marker is not usage");

        // a translation that stops mid-sentence has to be spotted: an exhausted output budget and a
        // dropped connection both end the stream, and only one of them says why
        Check(SseParser.ParseSseFinish("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}") == "stop",
            "sse: finish_reason read");
        Check(SseParser.ParseSseFinish("data: {\"choices\":[{\"delta\":{}}]}") == "", "sse: no finish_reason");
        Check(SseParser.ParseSseFinish("data: [DONE]") == "", "sse: done marker has no finish_reason");
        Check(!SseParser.StreamWasCut(sawDone: true, "stop"), "cut: a clean stop is not a cut");
        Check(SseParser.StreamWasCut(sawDone: true, "length"), "cut: an exhausted budget is a cut");
        Check(SseParser.StreamWasCut(sawDone: false, ""), "cut: a dropped stream is a cut");
        Check(!SseParser.StreamWasCut(sawDone: false, "stop"), "cut: the last chunk may carry the reason");
    }

    /// <summary>Issue #0015: malformed provider JSON degrades instead of throwing InvalidOperationException.</summary>
    static void MalformedJson()
    {
        using var arrayRoot = JsonDocument.Parse("[1,2]");
        Check(Jsons.Str(arrayRoot.RootElement, "a") == "", "json: Str on a non-object element returns empty");
        Check(Jsons.TryGet("[1,2]", "a") is null, "json: TryGet on an array root returns null");
        Check(Jsons.TryGet("\"just text\"", "a") is null, "json: TryGet on a string root returns null");
        Check(Jsons.Elements("5", "x").Count() == 0, "json: Elements on a scalar root yields none");
        Check(Jsons.Elements("\"s\"", "x").Count() == 0, "json: Elements on a string root yields none");

        Check(SseParser.ParseSseUsage("data: [1, \"usage\"]") is null, "sse: usage line with an array root is null");
        Check(SseParser.ParseSseDelta("data: [1,2,3]") is null, "sse: delta line with an array root is null");
        Check(SseParser.ParseSseDelta("data: {\"choices\":\"x\"}") is null, "sse: choices as string is null");
        Check(SseParser.ParseSseDelta("data: {\"choices\":[\"x\"]}") is null, "sse: string choice entry is null");
        Check(SseParser.ParseSseDelta("data: {\"choices\":[{\"delta\":5}]}") is null, "sse: a scalar delta is null, not a crash");
        Check(SseParser.ParseSseDelta("data: {\"choices\":[{\"message\":\"x\"}]}") is null, "sse: a scalar message is null, not a crash");
        Check(SseParser.ParseSseDelta("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}") == "Hi", "sse: an object delta still yields its content");
        Check(SseParser.ParseSseFinish("data: [\"finish_reason\"]") == "", "sse: finish line with an array root is empty");
        Check(SseParser.ParseSseFinish("data: {\"choices\":\"x\"}") == "", "sse: finish with choices as string is empty");
        Check(SseParser.ParseSseFinish("data: {\"choices\":[\"x\"]}") == "", "sse: finish with a string choice is empty");

        var broken = CatalogApi.ParseCatalog("{\"data\":[1, null, true, [], {\"id\":\"ok\"}]}");
        Check(broken.Count == 1 && broken[0].Id == "ok", "catalog: non-object entries are skipped");
        Check(CatalogApi.ParseCatalog("{\"data\":[\"m1\", {\"id\":\"m2\"}]}").Count == 2,
            "catalog: bare string ids still parse alongside objects");
        Check(CatalogApi.ParseCatalog("\"just a string\"").Count == 0, "catalog: a string root yields no models");
        Check(CatalogApi.ParseCatalog("5").Count == 0, "catalog: a scalar root yields no models");
    }

    /// <summary>An endpoint that sends one endless SSE line must read as a cut stream, not blow up the memory.</summary>
    static async Task SseOverflow()
    {
        // the padding sits inside a real delta, so an unbounded reader would yield "x" from this line
        var line = "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}],\"pad\":\"" + new string(' ', 2_000_000) + "\"}";
        var http = new HttpClient(new StubHandler(line + "\n\ndata: [DONE]\n\n"));
        var provider = new OpenAiCompatibleProvider(http, new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = "http://stub", Model = "mock" }] });
        var got = 0;
        await foreach (var _ in provider.TranslateAsync("hello", "en", "de", new TranslationOptions(), QualityTier.Standard, default)) got++;
        Check(got == 0, $"sse overflow: a giant line ends the stream without deltas ({got})");

        // the same body split into normal chunks still streams: the reader is not simply broken
        var small = new HttpClient(new StubHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ab\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"cd\"}}]}\n\ndata: [DONE]\n\n"));
        var again = new OpenAiCompatibleProvider(small, new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = "http://stub", Model = "mock" }] });
        var text = "";
        await foreach (var delta in again.TranslateAsync("hello", "en", "de", new TranslationOptions(), QualityTier.Standard, default)) text += delta;
        Check(text == "abcd", $"sse overflow: normal chunks still stream ('{text}')");

        // the other way a stream read must be bounded: not one giant line, but no data at all
        await SseStall();
    }

    sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    /// <summary>The read-only boilerplate of a fake response stream.</summary>
    abstract class FakeStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A response stream that yields its payload once and then blocks forever: the stalled provider.</summary>
    sealed class StallStream(string payload) : FakeStream
    {
        readonly byte[] bytes = Encoding.UTF8.GetBytes(payload);
        int sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent < bytes.Length)
            {
                var n = Math.Min(buffer.Length, bytes.Length - sent);
                bytes.AsMemory(sent, n).CopyTo(buffer);
                sent += n;
                return n;
            }
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken); // then nothing, ever
            return 0;
        }
    }

    /// <summary>A response stream that hands out one line per read, pausing between reads: slow but alive.</summary>
    sealed class DripStream : FakeStream
    {
        readonly byte[][] lines;
        readonly TimeSpan gap;
        int next;
        internal DripStream(TimeSpan gap, params string[] lines)
        {
            this.gap = gap;
            this.lines = lines.Select(Encoding.UTF8.GetBytes).ToArray();
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (next >= lines.Length) return 0;
            await Task.Delay(gap, cancellationToken);
            var line = lines[next++];
            line.AsMemory().CopyTo(buffer);
            return line.Length;
        }
    }

    sealed class StallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new StallStream("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n")) });
    }

    /// <summary>No per-read idle deadline means a provider that sends one SSE line and then goes silent hangs
    /// the run forever (HttpClient.Timeout does not cover the body under ResponseHeadersRead): the read must
    /// end as a visible stall error within a bounded time, while a slow-but-alive stream must not be cut.</summary>
    static async Task SseStall()
    {
        var idle = TimeSpan.FromMilliseconds(300);

        // direct: one line, then silence — ReadLines must end with the stall error, not hang
        string? stallError = null;
        var hung = false;
        var direct = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(new StallStream("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n"), Encoding.UTF8);
                await foreach (var _ in ProviderHttp.ReadLines(reader, ProviderHttp.MaxSseLine, default, idle)) { }
            }
            catch (Exception e) { stallError = e.Message; }
        });
        if (await Task.WhenAny(direct, Task.Delay(TimeSpan.FromSeconds(6))) != direct) hung = true; else await direct;
        Check(!hung && stallError?.Contains("stalled") == true,
            $"sse stall: a silent stream ends the read with a stall error within 6s ('{stallError}')");

        // through the provider: the run must fail visibly, not hang — a stall is an error, not a user cancel
        var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = "http://stub", Model = "mock" }] };
        var provider = new OpenAiCompatibleProvider(new HttpClient(new StallHandler()), settings) { StreamIdleTimeout = idle };
        string? runError = null;
        hung = false;
        var run = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in provider.TranslateAsync("hello", "en", "de", new TranslationOptions(), QualityTier.Standard, default)) { }
            }
            catch (Exception e) { runError = e.Message; }
        });
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(6))) != run) hung = true; else await run;
        Check(!hung && runError?.Contains("stalled") == true,
            $"sse stall: a stalled stream fails the run with a visible error, not a hang ('{runError}')");

        // a slow but alive stream — every gap well inside the idle window, the total far past it — must complete
        using var drip = new StreamReader(new DripStream(TimeSpan.FromMilliseconds(150),
            "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"c\"}}]}\n\n",
            "data: [DONE]\n\n"), Encoding.UTF8);
        var got = new List<string?>();
        await foreach (var line in ProviderHttp.ReadLines(drip, ProviderHttp.MaxSseLine, default, TimeSpan.FromMilliseconds(400)))
            got.Add(line);
        Check(got.Count == 8 && got.Contains("data: [DONE]"),
            $"sse stall: the deadline restarts per read, a slow stream still completes ({got.Count} lines)");
    }

    /// <summary>Issue #0014: the final chunk can carry usage AND content — neither may swallow the other.</summary>
    static async Task SseCombinedChunk()
    {
        var http = new HttpClient(new StubHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ab\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"cd\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}\n\n" +
            "data: [DONE]\n\n"));
        var provider = new OpenAiCompatibleProvider(http, new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = "http://stub", Model = "mock" }] });
        var text = "";
        TokenUsage? seen = null;
        await foreach (var delta in provider.TranslateAsync("hello", "en", "de", new TranslationOptions(), QualityTier.Standard, default, u => seen = u))
            text += delta;
        Check(text == "abcd", $"sse edge: usage and content on one line keeps both ('{text}')");
        Check(seen is { PromptTokens: 12, CompletionTokens: 7 }, "sse edge: usage on the shared chunk is counted");
    }

    /// <summary>Issue #0014: [DONE] is terminal — the run must end there, not wait for EOF or read past it.</summary>
    static async Task SseDoneTerminal()
    {
        var port = 19180 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "sse edge: mock server start: " + e.Message); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
                await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"pre\"}}]}\n\n");
                await w.WriteAsync("data: [DONE]\n\n");
                await Task.Delay(300); // let DONE land before the trailing lines even exist
                await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"-AFTER\"}}]}\n\n");
                await Task.Delay(TimeSpan.FromMinutes(5)); // no EOF: the run must not wait for it
            }
            catch (Exception) { /* the client leaves at [DONE]; the aborted write is expected */ }
        });
        try
        {
            var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }] };
            using var http = new HttpClient();
            var provider = new OpenAiCompatibleProvider(http, settings);
            var text = "";
            var stalled = false;
            var sw = Stopwatch.StartNew();
            try
            {
                await foreach (var delta in provider.TranslateAsync("hello", "en", "de", new TranslationOptions(), QualityTier.Standard, default))
                {
                    text += delta;
                    if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
                }
            }
            catch (TimeoutException) { stalled = true; }
            Check(!stalled, $"sse edge: [DONE] ends the run without waiting for EOF ({sw.ElapsedMilliseconds}ms)");
            Check(text == "pre", $"sse edge: nothing after [DONE] is appended ('{text}')");
        }
        finally { listener.Stop(); }
    }

    /// <summary>Issue #0014: a length-limited run whose continuations are exhausted must signal the cut exactly once.</summary>
    static async Task SseLengthSignal()
    {
        var port = 19280 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "sse length: mock server start: " + e.Message); return; }
        var served = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { return; }
                try
                {
                    _ = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    served++;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Teil \"}}]}\n\n");
                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n");
                    await w.WriteAsync("data: [DONE]\n\n");
                    await w.FlushAsync();
                    w.Close();
                }
                catch (Exception) { /* client gone after DONE */ }
            }
        });
        try
        {
            var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }] };
            using var http = new HttpClient();
            var provider = new OpenAiCompatibleProvider(http, settings);
            var cuts = 0;
            var cutValue = false;
            await foreach (var delta in provider.TranslateAsync("hello", "en", "de", new TranslationOptions(),
                QualityTier.Standard, default, null, c => { cuts++; cutValue = c; }))
                _ = delta;
            Check(cuts == 1 && cutValue, $"sse length: onCut fires exactly once, true (got {cuts}x {cutValue})");
            Check(served == 4, $"sse length: continuations exhausted after {served} requests");

            var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
            var coordinator = new TranslationCoordinator(provider, settings, new History(dir));
            var outcome = await coordinator.TranslateAsync("hello", "en", "de", QualityTier.Standard, _ => { });
            Check(outcome.Truncated, "sse length: a length-limited run is flagged Truncated");
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
        finally { listener.Stop(); }
    }

    /// <summary>Every provider reply is read up to a limit: a hostile endpoint costs a request, not the process.</summary>
    static async Task BoundedResponses()
    {
        var text = await ProviderHttp.ReadTextCappedAsync(new StringContent(new string('x', 5_000_000)), 1_000, default);
        Check(text.Length == 1_000, $"cap: a text body stops at the limit ({text.Length})");

        // a truncated file is garbage, so an over-long one is refused rather than clipped
        var refused = false;
        try { await ProviderHttp.ReadBytesCappedAsync(new ByteArrayContent(new byte[4096]), 1_024, default); }
        catch (HttpRequestException) { refused = true; }
        Check(refused, "cap: a byte body past the limit is refused");
        var fits = await ProviderHttp.ReadBytesCappedAsync(new ByteArrayContent(new byte[10]), 1_024, default);
        Check(fits.Length == 10, "cap: a body inside the limit arrives whole");

        // a body that ends before the length its headers promised is a cut, not a complete answer
        var shortBody = new DeclaredContent(new byte[10], 1_000);
        var cut = false;
        try { await ProviderHttp.ReadTextCappedAsync(shortBody, 4_000, default); }
        catch (HttpRequestException e) { cut = e.Message.Contains("cut short"); }
        Check(cut, "cap: a body short of its declared length is refused");

        // a file link a provider hands back is followed only when it points at a public host
        foreach (var (url, what) in new[]
        {
            ("http://169.254.169.254/latest/meta-data", "cloud metadata"),
            ("http://127.0.0.1:8080/admin", "loopback"),
            ("http://localhost/x", "localhost"),
            ("http://192.168.1.1/x", "private range"),
            ("http://100.100.1.1/x", "CGNAT range"),
            ("http://[::ffff:127.0.0.1]/x", "IPv4-mapped loopback"),
            ("http://[::1]/x", "IPv6 loopback"),
            ("file:///etc/passwd", "file scheme"),
            // v6 prefixes that embed an IPv4 target must be judged by the embedded address
            ("http://[64:ff9b::7f00:1]/x", "NAT64 spelling of loopback"),
            ("http://[64:ff9b::a9fe:a9fe]/x", "NAT64 spelling of cloud metadata"),
            ("http://[2002:7f00:1::]/x", "6to4 spelling of loopback"),
            ("http://[2001::80ff:fffe]/x", "Teredo spelling of loopback"),
            ("http://[::7f00:1]/x", "IPv4-compatible loopback"),
            ("http://224.0.0.1/x", "multicast"),
            ("http://[ff02::1]/x", "IPv6 multicast"),
            ("http://240.0.0.1/x", "reserved range"),
            ("http://198.18.0.1/x", "benchmarking range"),
        })
            Check(!LinkGuard.IsFetchableLink(url, out _), $"link: {what} refused");
        Check(LinkGuard.IsFetchableLink("http://8.8.8.8/x", out _), "link: a plain public IPv4 is still fetched");
        Check(LinkGuard.IsFetchableLink("http://[2606:4700:4700::1111]/x", out _), "link: a plain public IPv6 is still fetched");
        Check(LinkGuard.IsFetchableLink("https://cdn.example.com/clip.mp3?sig=1", out var publicUri)
              && publicUri.Host == "cdn.example.com", "link: a public https file is fetched");
    }

    static void ViewModelCases()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var settings = new AppSettings
        {
            TargetLang = "de",
            Options = new TranslationOptions { Complexity = Complexity.Complex, Style = Style.Creative },
        };
        var history = new History(dir);
        using var http = new HttpClient();
        var coordinator = new TranslationCoordinator(new OpenAiCompatibleProvider(http, settings), settings, history);
        var vm = new TranslateViewModel(new SettingsService(settings), history, coordinator,
            new OpenAiCompatibleProvider(http, settings), new MicrophoneRecorder(), new FakePlayer());

        vm.InputText = "hello";
        Check(vm.CharCount == "5 characters" && !vm.InputHintVisible, "viewmodel: char count follows the input");
        vm.InputText = "a";
        Check(vm.CharCount == "1 character", "viewmodel: one character stays singular");
        vm.InputText = "";
        Check(vm.CharCount == "0 characters" && vm.InputHintVisible, "viewmodel: empty input shows the hint");

        Check(vm.Chrome.ComplexityIndex == (int)Complexity.Complex && vm.Chrome.StyleIndex == (int)Style.Creative,
            "viewmodel: options initialise from the stored settings");
        vm.Chrome.ComplexityIndex = (int)Complexity.Simple;
        Check(settings.Options.Complexity == Complexity.Simple, "viewmodel: option change is persisted");

        // a plain-HTTP speech/sound endpoint carries the bearer key in the clear: the hint says so
        var svm = new SettingsViewModel(new SettingsService(new AppSettings()));
        svm.SpeechUrl = "http://gateway.internal/v1/audio";
        Check(svm.SpeechUrlHint.Contains("unencrypted"), "settings: a plain-HTTP speech URL warns about the key");
        svm.SoundUrl = "https://gateway.internal/v1/audio";
        Check(!svm.SoundUrlHint.Contains("unencrypted"), "settings: an HTTPS sound URL stays unwarned");

        history.Add(new HistoryEntry { From = "en", To = "de", Source = "hello", Output = "hallo" });
        history.Add(new HistoryEntry { From = "auto", To = "fr", Source = "world", Output = "monde", Favorite = true });
        vm.History.Refresh();
        Check(vm.History.Entries.Count == 2 && !vm.History.IsEmpty, "viewmodel: history lists every entry");
        vm.History.ShowFavoritesCommand.Execute(null);
        Check(vm.History.Entries.Count == 1 && vm.History.Entries[0].Favorite && vm.History.IsFavoritesFilter,
            "viewmodel: favorites filter drops the rest");
        vm.History.ShowAllCommand.Execute(null);
        Check(vm.History.Entries.Count == 2 && vm.History.IsAllFilter, "viewmodel: all-entries filter restores the list");

        Check(!vm.ExtrasEnabled, "viewmodel: extras disabled before a translation");
        vm.OutputText = "hallo";
        Check(vm.ExtrasEnabled, "viewmodel: extras enabled once there is output");
        vm.Detected = "Detected (German)";
        vm.InputText = "new text";
        Check(vm.Detected.Length == 0, "viewmodel: editing the source drops the stale detection");
        Check(vm.IsStale && !vm.ExtrasEnabled, "viewmodel: editing the source marks the output stale and disables extras");
        Check(vm.StatusDisplay.Contains("Ctrl+Enter"), "viewmodel: the status line points at retranslation while stale");
        vm.IsStale = false;
        vm.InputText = new string('a', 20001);
        Check(vm.IsLongInput, "viewmodel: long input is flagged");

        var partial = coordinator.Commit("hi", "halb", "en", "de");
        Check(history.Entries[0] == partial && partial.Output == "halb", "viewmodel: cancelled run keeps its partial output");
        try { Directory.Delete(dir, true); } catch (Exception) { }

        // a key travels as a bearer header: an http endpoint sends it in the clear, and the row says so
        var dialog = new SettingsViewModel(new SettingsService(new AppSettings()), http);
        var row = dialog.Providers.Rows[0];
        row.BaseUrl = "http://localhost:11434/v1";
        Check(row.HttpWarn.Length > 0, "settings: an http provider is flagged");
        row.BaseUrl = "https://api.example/v1";
        Check(row.HttpWarn.Length == 0, "settings: an https provider is not flagged");
    }

    /// <summary>The extras read the text on screen, so they go out on the tier that produced it.</summary>
    static async Task ExtrasFollowTier()
    {
        // A answers its two calls in order: the alternatives, then the insights
        var (listenerA, baseA, bodiesA) = await StartMock(
            "{\"original_description\":\"from A\",\"alternatives\":[{\"text\":\"a\",\"explanation\":\"a\"}]}",
            "{\"insights\":[]}");
        var (listenerB, baseB, bodiesB) = await StartMock(
            "{\"original_description\":\"from B\",\"alternatives\":[{\"text\":\"b\",\"explanation\":\"b\"}]}");
        try
        {
            var settings = new AppSettings
            {
                Providers =
                [
                    new ProviderConfig { Name = "first", BaseUrl = baseA, Model = "model-a" },
                    new ProviderConfig { Name = "second", BaseUrl = baseB, Model = "model-b" },
                ],
                // Basic and Standard on the first provider, Best on the second one
                BestProvider = "second",
                BestModel = "model-b",
                Tier = QualityTier.Best,
            };
            using var http = new HttpClient();
            var provider = new OpenAiCompatibleProvider(http, settings);

            var (description, alternatives) = await provider.AlternativesAsync("hello", "hallo", "en", "de", default);
            Check(description == "from B" && alternatives.Count == 1 && bodiesA.Count == 0,
                "extras: alternatives follow the Best tier's provider, not Standard's");
            Check(bodiesB.Count == 1 && bodiesB[0].Contains("\"model\":\"model-b\""),
                "extras: the tier's own model is sent");

            settings.Tier = QualityTier.Standard;
            var second = await provider.AlternativesAsync("hello", "hallo", "en", "de", default);
            Check(second.Description == "from A" && bodiesA.Count == 1 && bodiesB.Count == 1,
                "extras: switching the tier switches the provider the extras use");
            Check(bodiesA[0].Contains("\"model\":\"model-a\""), "extras: the Standard model is sent after the switch");

            await provider.InsightsAsync("hello", "hallo", "en", "de", default);
            Check(bodiesA.Count == 2 && bodiesA[1].Contains("insights") && bodiesA[1].Contains("\"model\":\"model-a\""),
                "extras: word insights follow the same tier");
        }
        finally
        {
            listenerA.Stop();
            listenerB.Stop();
        }
    }

    /// <summary>The alternatives count is a setting: it is clamped to 1..5, defaults to 3, and the
    /// real provider sends it into the prompt of every alternatives request.</summary>
    static async Task AlternativesCountFlows()
    {
        Check(new TranslationOptions().AlternativesCount == 3, "alternatives count: a fresh options default is 3");
        var clamped = new TranslationOptions();
        clamped.AlternativesCount = 99;
        Check(clamped.AlternativesCount == 5, "alternatives count: 99 clamps to 5");
        clamped.AlternativesCount = 0;
        Check(clamped.AlternativesCount == 1, "alternatives count: 0 clamps to 1");
        Check(Prompts.AlternativesSystem("en", "de", 3).Contains("exactly 3"),
            "alternatives count: the default prompt still says exactly 3");

        var (listener, baseUrl, bodies) = await StartMock(
            "{\"original_description\":\"d1\",\"alternatives\":[]}",
            "{\"original_description\":\"d2\",\"alternatives\":[]}");
        try
        {
            var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = baseUrl, Model = "mock" }] };
            using var http = new HttpClient();
            var provider = new OpenAiCompatibleProvider(http, settings);

            settings.Options.AlternativesCount = 5;
            await provider.AlternativesAsync("hello", "hallo", "en", "de", default);
            Check(bodies.Count == 1 && bodies[0].Contains("exactly 5") && !bodies[0].Contains("exactly 3"),
                "alternatives count: the setting reaches the request prompt");

            settings.Options.AlternativesCount = 3;
            await provider.AlternativesAsync("hello", "hallo", "en", "de", default);
            Check(bodies.Count == 2 && bodies[1].Contains("exactly 3"),
                "alternatives count: back at the default the prompt says exactly 3 again");

            // the field lives in the settings dialog: its Save is the write path that lands the value
            var dialog = new SettingsViewModel(new SettingsService(settings), http) { AlternativesCount = 4 };
            dialog.SaveCommand.Execute(null);
            Check(settings.Options.AlternativesCount == 4,
                "alternatives count: the settings dialog saves the field");
            dialog.AlternativesCount = 9; // the model clamps, so 9 lands as 5
            dialog.SaveCommand.Execute(null);
            Check(settings.Options.AlternativesCount == 5,
                "alternatives count: the dialog's value is clamped on save");
        }
        finally
        {
            listener.Stop();
        }
    }

    static async Task PricedRun()
    {
        var port = 18980 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "priced run: mock server start: " + e.Message); return; }

        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            await WriteSse(ctx,
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":500}}\n\n",
                "data: [DONE]\n\n");
        });

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }],
            InputPricePerMTok = 3,
            OutputPricePerMTok = 15,
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var coordinator = new TranslationCoordinator(new OpenAiCompatibleProvider(http, settings), settings, history);
        try
        {
            var outcome = await coordinator.TranslateAsync("Hello", "en", "de", QualityTier.Standard, _ => { });
            Check(outcome.Usage is { PromptTokens: 1000, CompletionTokens: 500 }, "priced run: reported usage is kept");
            Check(Math.Abs(outcome.Entry.Cost - 0.0105) < 0.000001, $"priced run: cost is 1000×3 + 500×15 per 1M ({outcome.Entry.Cost})");
            Check(outcome.Entry.CostLabel == "~$0.0105", "priced run: the history row carries its own cost");
            Check(history.Entries[0].PromptTokens == 1000 && history.Entries[0].CompletionTokens == 500,
                "priced run: token counts are stored with the entry");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>A provider that states its own bill (usage.cost) wins over the token×price math,
    /// streamed and non-streamed alike.</summary>
    static async Task ReportedCostWins()
    {
        var prefix = $"http://127.0.0.1:{18990 + Random.Shared.Next(0, 200)}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "reported cost: mock server start: " + e.Message); return; }

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                string req;
                using (var sr = new StreamReader(ctx.Request.InputStream)) req = await sr.ReadToEndAsync();
                if (req.Contains("\"stream\":true"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n");
                    await w.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":500,\"cost\":0.0042}}\n\n");
                    await w.WriteAsync("data: [DONE]\n\n");
                    w.Close();
                }
                else
                {
                    ctx.Response.ContentType = "application/json";
                    var body = "{\"choices\":[{\"message\":{\"content\":\"{\\\"iso\\\":\\\"de\\\"}\"}}],\"usage\":{" +
                               "\"prompt_tokens\":10,\"completion_tokens\":2,\"cost\":0.0042}}";
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                    ctx.Response.Close();
                }
            }
        });

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }],
            InputPricePerMTok = 3,
            OutputPricePerMTok = 15,
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        var coordinator = new TranslationCoordinator(provider, settings, history);
        try
        {
            var outcome = await coordinator.TranslateAsync("Hello", "en", "de", QualityTier.Standard, _ => { });
            Check(Math.Abs(outcome.Entry.Cost - 0.0042) < 1e-9,
                $"reported cost: the streamed usage.cost beats the token math ({outcome.Entry.Cost})");
            Check(outcome.Entry.CostLabel == "~$0.0042", "reported cost: the history row carries the reported money");
            Check(outcome.Usage.PromptTokens == 1000 && outcome.Usage.CompletionTokens == 500,
                "reported cost: the reported counters are kept beside the cost");

            TokenUsage? seen = null;
            await provider.DetectAsync("こんにちは", default, u => seen = u);
            Check(seen is { ReportedCost: 0.0042 }, $"reported cost: a non-streaming usage.cost rides the usage ({seen})");
            Check(seen is not null && Pricing.Cost(seen, 3, 15) == 0.0042,
                "reported cost: pricing uses the reported amount over the token math");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>#0033: the label renders the stored Cost (authoritative — it includes speech and
    /// reported usage.cost), never a tokens×prices recomputation; the cost shows iff Cost > 0.</summary>
    static void UsageLabelFallback()
    {
        var old = new HistoryEntry { PromptTokens = 1000, CompletionTokens = 500, Cost = 0.0105 };
        Check(old.UsageLabel == "↑1000 ↓500 tokens · ~$0.0105",
            $"usage label: an old entry shows its frozen cost ('{old.UsageLabel}')");

        // the stored cost wins even when prices are recorded; the prices stay persisted for the record
        var priced = new HistoryEntry { PromptTokens = 1000, CompletionTokens = 500, Cost = 0.5, InputPricePerMTok = 3, OutputPricePerMTok = 15 };
        Check(priced.UsageLabel.EndsWith("~$0.5000"),
            $"usage label: a priced entry shows its frozen cost, not the token math ('{priced.UsageLabel}')");

        var tokens = new HistoryEntry { PromptTokens = 12, CompletionTokens = 7 };
        Check(tokens.UsageLabel == "↑12 ↓7 tokens" && !tokens.UsageLabel.Contains('$'),
            $"usage label: no cost, tokens only ('{tokens.UsageLabel}')");
    }

    /// <summary>Detect, alternatives, insights and transliteration are requests too: they land on the bill.</summary>
    static async Task ExtraCosts()
    {
        var port = 19680 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "extras: mock server start: " + e.Message); return; }

        // streams answer the translation; everything else gets a JSON body that carries its own usage
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var ctx = await listener.GetContextAsync();
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream)) body = await sr.ReadToEndAsync();
                if (body.Contains("\"stream\":true"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n");
                    await w.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":500}}\n\n");
                    await w.WriteAsync("data: [DONE]\n\n");
                    await w.FlushAsync();
                    w.Close();
                    continue;
                }
                var usage = "\"prompt_tokens\":10,\"completion_tokens\":2";
                string content;
                if (body.Contains("Identify the language"))
                    content = "{\\\"iso\\\":\\\"ja\\\",\\\"label\\\":\\\"Japanese\\\",\\\"is_mixed\\\":false}";
                else if (body.Contains("alternative translations"))
                {
                    usage = "\"prompt_tokens\":200,\"completion_tokens\":100";
                    content = "{\\\"original_description\\\":\\\"plain\\\",\\\"alternatives\\\":[{\\\"text\\\":\\\"Hi\\\",\\\"explanation\\\":\\\"shorter\\\"}]}";
                }
                else if (body.Contains("carry nuance"))
                {
                    usage = "\"prompt_tokens\":300,\"completion_tokens\":200";
                    content = "{\\\"insights\\\":[{\\\"word\\\":\\\"こんにちは\\\",\\\"kind\\\":\\\"greeting\\\",\\\"alternatives\\\":[{\\\"text\\\":\\\"hi\\\",\\\"explanation\\\":\\\"casual\\\"}]}]}";
                }
                else // the romanisation answer
                {
                    usage = "\"prompt_tokens\":50,\"completion_tokens\":30";
                    content = "{\\\"source\\\":\\\"konnichiwa\\\",\\\"translation\\\":\\\"hello\\\"}";
                }
                ctx.Response.ContentType = "application/json";
                var json = "{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}],\"usage\":{" + usage + "}}";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
                ctx.Response.Close();
            }
        });

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }],
            InputPricePerMTok = 3,
            OutputPricePerMTok = 15,
            SourceLang = "auto",
            TargetLang = "en",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        try
        {
            // provider level: a complete answer reports the provider's own counters, not an estimate
            TokenUsage? seen = null;
            await provider.DetectAsync("こんにちは", default, u => seen = u);
            Check(seen is { PromptTokens: 10, CompletionTokens: 2, Estimated: false },
                $"extras: a body with usage reports those tokens ({seen})");

            // a body without usage bills an estimate instead
            var (plainListener, plainUrl, _) = await StartMock("de");
            var plain = new OpenAiCompatibleProvider(new HttpClient(),
                new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = plainUrl, Model = "mock" }] });
            TokenUsage? est = null;
            await plain.DetectAsync("Hello", default, u => est = u);
            Check(est is { Estimated: true, PromptTokens: > 0, CompletionTokens: > 0 },
                $"extras: a body without usage is estimated ({est})");
            plainListener.Stop();

            // VM level: detect + run + all three extras land on the same entry's bill
            var vm = new TranslateViewModel(new SettingsService(settings), history,
                new TranslationCoordinator(provider, settings, history), provider, new FakeRecorder(), new FakePlayer());
            vm.InputText = "こんにちは";
            await vm.TranslateAsync();
            var entry = vm.History.Current;
            Check(!vm.IsStale, "stale: a finished run is fresh");
            vm.InputText = "こんにちは!";
            Check(vm.IsStale && !vm.ExtrasEnabled, "stale: adding a letter marks the output stale");
            vm.InputText = "こんにちは";
            Check(!vm.IsStale && vm.ExtrasEnabled && vm.StatusDisplay.Contains("Done"),
                "stale: deleting the letter back to the translated source clears the flag");
            // detect 10/2 (0.00006) rides the entry that also carries the run's 1000/500 (0.0105)
            Check(entry is not null && Math.Abs(entry.Cost - 0.01056) < 0.000001,
                $"extras: the detect cost rides the new entry ({entry?.Cost})");
            // #0033: the footer re-renders from the entry after every paid action; the fire-and-forget
            // transliteration may land in between, so wait for every tracked request and then poll
            await vm.WhenIdleAsync(); // the transliteration is tracked too, so the entry's bill is settled here
            for (var i = 0; i < 50 && entry is not null && vm.Usage.UsageLabel != entry.UsageLabel; i++) await Task.Delay(20);
            Check(entry is not null && vm.Usage.UsageLabel == entry.UsageLabel,
                $"extras: the footer mirrors the current entry after the run ('{vm.Usage.UsageLabel}')");
            // transliteration is fire-and-forget and may already have landed (the poll above can take
            // a second), so assert against the entry's current cost, not the run-only ~$0.0106 — the
            // exact post-transliteration numbers are pinned by the checks below
            Check(entry is not null && vm.Usage.UsageLabel.Contains(Pricing.CostLabel(entry.Cost)),
                $"extras: the footer carries the entry's cost ('{vm.Usage.UsageLabel}')");

            // the transliteration runs beside the finished run: wait for it before the stepwise extras
            for (var i = 0; i < 60 && !vm.HasTranslitTarget; i++) await Task.Delay(50); // 50/30 = 0.0006
            Check(entry is not null && Math.Abs(entry.Cost - 0.01116) < 0.000001,
                $"extras: transliteration adds its cost ({entry?.Cost})");
            Check(entry is not null && vm.Usage.UsageLabel == entry.UsageLabel && vm.Usage.UsageLabel.Contains("~$0.0112"),
                $"extras: the footer follows the entry after transliteration ('{vm.Usage.UsageLabel}')");
            // detect 10/2 rode the run's 1000/500, translit added 50/30 — both at the run's frozen prices
            Check(entry is { PromptTokens: 1060, CompletionTokens: 532, InputPricePerMTok: 3, OutputPricePerMTok: 15, UsageEstimated: false },
                $"extras: detect and transliteration tokens ride the entry, at the run's prices ({entry?.PromptTokens}/{entry?.CompletionTokens})");

            await vm.AlternativesAsync(); // 200/100 = 0.0021
            Check(entry is not null && entry.AlternativesDescription == "plain"
                && entry.Alternatives is [{ Text: "Hi", Explanation: "shorter" }],
                "extras: alternatives are stored on the entry");
            Check(entry is not null && Math.Abs(entry.Cost - 0.01326) < 0.000001,
                $"extras: alternatives add their cost ({entry?.Cost})");
            Check(entry is not null && vm.Usage.UsageLabel == entry.UsageLabel && vm.Usage.UsageLabel.Contains("~$0.0133"),
                $"extras: the footer follows the entry after alternatives ('{vm.Usage.UsageLabel}')");
            Check(entry is { PromptTokens: 1260, CompletionTokens: 632 },
                $"extras: alternatives add their tokens ({entry?.PromptTokens}/{entry?.CompletionTokens})");
            await vm.InsightsAsync(); // 300/200 = 0.0039
            Check(entry is not null && entry.Insights is [{ Word: "こんにちは", Kind: "greeting" }]
                && entry.Insights[0].Alternatives is [{ Text: "hi" }],
                "extras: insights are stored on the entry");
            Check(entry is not null && Math.Abs(entry.Cost - 0.01716) < 0.000001,
                $"extras: insights add their cost ({entry?.Cost})");
            Check(entry is not null && vm.Usage.UsageLabel == entry.UsageLabel && vm.Usage.UsageLabel.Contains("~$0.0172"),
                $"extras: the footer follows the entry after insights ('{vm.Usage.UsageLabel}')");
            Check(entry is { PromptTokens: 1560, CompletionTokens: 832 },
                $"extras: insights add their tokens ({entry?.PromptTokens}/{entry?.CompletionTokens})");
            Check(entry is not null && entry.UsageLabel.EndsWith(entry.CostLabel),
                $"extras: the entry's usage label ends with its own frozen cost ('{entry?.UsageLabel}')");
            Check(vm.History.SpendLabel.Contains("0.0172"),
                $"extras: the spend label carries the total ('{vm.History.SpendLabel}')");

            // the stored extras survive a reload and a restore shows them without a new request
            var reloaded = new History(dir);
            reloaded.Load();
            var persisted = reloaded.Entries.FirstOrDefault(e => e.Id == entry?.Id);
            Check(persisted is { Alternatives.Count: 1, Insights.Count: 1 }
                && persisted.Alternatives[0].Text == "Hi" && persisted.Insights[0].Word == "こんにちは",
                "extras: stored alternatives and insights survive a reload");
            vm.History.SelectedEntry = persisted; // restore fires on selection change
            Check(vm.AlternativesVisible && vm.Alternatives.Count == 1 && vm.AlternativesDescription == "plain",
                $"extras: a restore shows the stored alternatives ('{vm.AlternativesDescription}')");
            Check(vm.InsightsVisible && vm.Insights.Count == 1 && vm.Insights[0].Alternatives[0].Text == "hi",
                "extras: a restore shows the stored insights");

            // closing a panel hides it and drops its extras from the entry and the store
            vm.History.Current = entry; // reference-compared: the reloaded copy must not swallow the switch
            vm.CloseAlternativesCommand.Execute(null);
            Check(!vm.AlternativesVisible && vm.Alternatives.Count == 0
                && entry is { Alternatives.Count: 0, AlternativesDescription: "" },
                "extras: closing the alternatives panel hides it and drops them from the entry");
            vm.CloseInsightsCommand.Execute(null);
            Check(!vm.InsightsVisible && entry is { Insights.Count: 0 },
                "extras: closing the insights panel hides it and drops them from the entry");
            var again = new History(dir);
            again.Load();
            Check(again.Entries.FirstOrDefault(e => e.Id == entry?.Id) is { Alternatives.Count: 0, Insights.Count: 0 },
                "extras: closed panels are not stored either");

            // the setting keeps new extras off the history entirely while still showing them
            settings.SaveExtras = false;
            var vm2 = new TranslateViewModel(new SettingsService(settings), history,
                new TranslationCoordinator(provider, settings, history), provider, new FakeRecorder(), new FakePlayer());
            vm2.InputText = "こんにちは";
            await vm2.TranslateAsync();
            var entry2 = vm2.History.Current;
            await vm2.AlternativesAsync();
            await vm2.InsightsAsync();
            Check(entry2 is { Alternatives.Count: 0, Insights.Count: 0 } && vm2.AlternativesVisible && vm2.InsightsVisible,
                "extras: with keeping off, extras show on screen but stay off the entry");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>An extras request started for one entry bills that entry even when another entry is
    /// restored while the request is in flight; the restored entry keeps its own bill and its panes.</summary>
    static async Task ExtrasBillTheCapturedEntry()
    {
        var port = 19880 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "extras entry: mock server start: " + e.Message); return; }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var ctx = await listener.GetContextAsync();
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream)) body = await sr.ReadToEndAsync();
                if (body.Contains("\"stream\":true"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"hallo\"}}]}\n\n");
                    await w.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":500}}\n\n");
                    await w.WriteAsync("data: [DONE]\n\n");
                    await w.FlushAsync();
                    w.Close();
                    continue;
                }
                string usage;
                string content;
                if (body.Contains("alternative translations"))
                {
                    await gate.Task; // hold the request until the test has restored another entry
                    usage = "\"prompt_tokens\":200,\"completion_tokens\":100";
                    content = "{\\\"original_description\\\":\\\"plain\\\",\\\"alternatives\\\":[{\\\"text\\\":\\\"Hi\\\",\\\"explanation\\\":\\\"shorter\\\"}]}";
                }
                else // the romanisation answer, nothing to bill
                {
                    usage = "\"prompt_tokens\":0,\"completion_tokens\":0";
                    content = "{\\\"source\\\":\\\"\\\",\\\"translation\\\":\\\"\\\"}";
                }
                ctx.Response.ContentType = "application/json";
                var json = "{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}],\"usage\":{" + usage + "}}";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
                ctx.Response.Close();
            }
        });

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }],
            InputPricePerMTok = 3,
            OutputPricePerMTok = 15,
            SourceLang = "en",
            TargetLang = "de",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        var coordinator = new TranslationCoordinator(provider, settings, history);
        try
        {
            var vm = new TranslateViewModel(new SettingsService(settings), history, coordinator, provider, new FakeRecorder(), new FakePlayer());
            vm.InputText = "hello";
            await vm.TranslateAsync();
            await vm.WhenIdleAsync(); // the fire-and-forget transliteration is tracked too
            var entry = vm.History.Current;
            Check(entry is not null && entry.Cost > 0.0105 - 0.000001,
                $"extras entry: the run's own bill ({entry?.Cost})");
            var other = coordinator.Commit("bye", "tschuess", "en", "de");
            vm.History.Refresh();
            var runCost = entry!.Cost;
            var otherCost = other.Cost;

            var request = vm.AlternativesAsync(); // 200/100 = 0.0021 at the run's frozen prices
            await Task.Delay(150); // the request is in flight, blocked on the mock's gate
            vm.History.SelectedEntry = other; // restore another entry mid-flight
            gate.SetResult();
            await request;

            Check(Math.Abs(entry.Cost - (runCost + 0.0021)) < 0.000001,
                $"extras entry: a mid-flight restore still bills the captured entry ({entry.Cost} vs {runCost}+0.0021)");
            Check(Math.Abs(other.Cost - otherCost) < 0.000001,
                $"extras entry: the restored entry keeps its own bill ({other.Cost})");
            Check(entry.Alternatives is [{ Text: "Hi", Explanation: "shorter" }],
                "extras entry: the alternatives are stored on the captured entry");
            Check(vm.Alternatives.Count == 0 && !vm.AlternativesVisible,
                "extras entry: the restored entry's panes are not overwritten");
            Check(vm.Usage.UsageLabel == other.UsageLabel,
                $"extras entry: the footer still mirrors the restored entry ('{vm.Usage.UsageLabel}')");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static async Task StreamingRoundTrip()
    {
        var port = 18380 + Random.Shared.Next(0, 400);
        var prefix = $"http://127.0.0.1:{port}/";
        string? seenBody = null;
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "mock server start: " + e.Message); return; }

        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using (var sr = new StreamReader(ctx.Request.InputStream)) seenBody = await sr.ReadToEndAsync();
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            foreach (var d in new[] { "Hallo", " Welt", "!" })
            {
                await w.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{d}\"}}}}]}}\n\n");
                await w.FlushAsync();
            }
            await w.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}\n\n");
            await w.FlushAsync();
            await w.WriteAsync("data: [DONE]\n\n");
            await w.FlushAsync();
            w.Close();
        });

        var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock-model" }] };
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);

        var sb = new StringBuilder();
        TokenUsage? reported = null;
        var sw = Stopwatch.StartNew();
        await foreach (var chunk in provider.TranslateAsync("Hello world.", "en", "de", new TranslationOptions(),
                           QualityTier.Standard, default, u => reported = u))
        {
            sb.Append(chunk);
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("streaming stalled");
        }
        listener.Stop();

        Check(sb.ToString() == "Hallo Welt!", $"stream: assembled '{sb}'");
        Check(seenBody?.Contains("\"model\":\"mock-model\"") == true, "stream: model sent");
        Check(seenBody?.Contains("German") == true, "stream: prompt sent");
        Check(seenBody?.Contains("Hello world.") == true, "stream: text sent");
        Check(seenBody?.Contains("include_usage") == true, "stream: usage requested");
        Check(reported is { PromptTokens: 12, CompletionTokens: 7 }, "stream: usage reported to the caller");
    }

    /// <summary>A stream that stops mid-sentence has to be asked for the rest, and the second request
    /// has to carry what is already translated, otherwise the model starts over.</summary>
    static async Task ContinuationRoundTrip()
    {
        var port = 18780 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        var bodies = new List<string>();
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "continuation: mock server start: " + e.Message); return; }

        // first answer is cut by the output budget, second one finishes
        var chunks = new[]
        {
            new[] { "Hallo ", "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" },
            new[] { "Welt!", "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" },
        };
        var call = 0;
        _ = Task.Run(async () =>
        {
            while (call < chunks.Length)
            {
                var ctx = await listener.GetContextAsync();
                using (var sr = new StreamReader(ctx.Request.InputStream)) bodies.Add(await sr.ReadToEndAsync());
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                var script = chunks[call++];
                await w.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{script[0]}\"}}}}]}}\n\n");
                await w.WriteAsync(script[1]);
                await w.WriteAsync("data: [DONE]\n\n");
                await w.FlushAsync();
                w.Close();
            }
        });

        var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock-model" }] };
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        await foreach (var chunk in provider.TranslateAsync("Hello world.", "en", "de", new TranslationOptions(),
                           QualityTier.Standard, default))
        {
            sb.Append(chunk);
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("continuation stalled");
        }
        listener.Stop();

        Check(sb.ToString() == "Hallo Welt!", $"continuation: assembled '{sb}'");
        Check(bodies.Count == 2, $"continuation: one extra request, sent {bodies.Count}");
        Check(bodies.Count > 1 && bodies[1].Contains("TRANSLATION SO FAR") && bodies[1].Contains("Hallo"),
            "continuation: the second request carries the text so far");
    }

    static async Task StructuredRoundTrip()
    {
        var detectAnswer = "{\"iso\":\"fr\",\"label\":\"French\"}";
        var altAnswer = "{\"original_description\":\"Casual tone.\",\"alternatives\":[{\"text\":\"Salut le monde\",\"explanation\":\"Casual\"},{\"text\":\"Bonjour à tous\",\"explanation\":\"Neutral\"}]}";
        var insightAnswer = "{\"insights\":[{\"word\":\"quick\",\"kind\":\"Speed\",\"alternatives\":[{\"text\":\"rapide\",\"explanation\":\"Neutral\"},{\"text\":\"vif\",\"explanation\":\"Lively\"}]}]}";
        var translitAnswer = "{\"source\":\"konnichiwa sekai\",\"translation\":\"\"}";
        // a bare list instead of the envelope is still read; a body that is no JSON at all is the empty default
        var bareInsightAnswer = "[{\"word\":\"quick\",\"kind\":\"Speed\",\"alternatives\":[{\"text\":\"vif\",\"explanation\":\"Lively\"}]}]";
        var noJsonAnswer = "no json in here";

        var (listener, baseUrl, _) = await StartMock(detectAnswer, altAnswer, insightAnswer, translitAnswer,
            noJsonAnswer, noJsonAnswer, bareInsightAnswer, noJsonAnswer);
        var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = baseUrl, Model = "mock" }] };
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        try
        {
            Check(await provider.DetectAsync("bonjour", default) == "fr", "detect: iso parsed");

            var (description, alternatives) = await provider.AlternativesAsync("hello world", "hello world", "en", "fr", default);
            Check(description == "Casual tone." && alternatives.Count == 2, "alternatives: parsed");
            Check(alternatives[1].Text == "Bonjour à tous" && alternatives[1].HasExplanation, "alternatives: fields");

            var insights = await provider.InsightsAsync("hello world", "hello world", "en", "fr", default);
            Check(insights.Count == 1 && insights[0].Word == "quick", "insights: parsed");
            Check(insights[0].Alternatives.Count == 2 && insights[0].Alternatives[1].Text == "vif", "insights: variants");

            var (translitSource, translitTarget) = await provider.TransliterationAsync("こんにちは世界", "Hello world", "ja", "en", default);
            Check(translitSource == "konnichiwa sekai" && translitTarget.Length == 0,
                "transliteration: fields parsed, empty side stays hidden");
            Check((await provider.TransliterationAsync("hello", "hallo", "en", "de", default)) == ("", ""),
                "transliteration: latin to latin costs no request");

            var (lostDescription, lostItems) = await provider.AlternativesAsync("hello world", "hello world", "en", "fr", default);
            Check(lostDescription == "" && lostItems.Count == 0, "alternatives: an unparseable answer yields nothing");
            Check((await provider.InsightsAsync("hello world", "hello world", "en", "fr", default)).Count == 0,
                "insights: an unparseable answer yields nothing");
            var bareInsights = await provider.InsightsAsync("hello world", "hello world", "en", "fr", default);
            Check(bareInsights.Count == 1 && bareInsights[0].Word == "quick" && bareInsights[0].Alternatives.Count == 1,
                "insights: a bare array answer is read without an envelope");
            Check((await provider.TransliterationAsync("こんにちは世界", "Hello world", "ja", "en", default)) == ("", ""),
                "transliteration: an unparseable answer yields nothing");
        }
        finally
        {
            listener.Stop();
        }

        await ErrorBodyIsNotTranslation();
        await AuxCallsAreFenced();
    }

    /// <summary>Issue: a 200 body that is valid JSON without choices (an {"error":{...}} envelope) is an
    /// error surfaced via Describe, never the raw body shown, stored and billed as the translation.</summary>
    static async Task ErrorBodyIsNotTranslation()
    {
        var prefix = $"http://127.0.0.1:{19500 + Random.Shared.Next(0, 100)}/";
        using var listener = StartMock(prefix, async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"quota exceeded\"}}"));
            ctx.Response.Close();
        }, "error body: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }] };
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        try
        {
            var threw = false;
            string message = "";
            TokenUsage? billed = null;
            try { await provider.DetectAsync("hello", default, u => billed = u); }
            catch (HttpRequestException e) { threw = true; message = e.Message; }
            Check(threw && message.Contains("quota") && billed is null,
                $"error body: a 200 JSON body without choices fails as an error, never as text ('{message}')");
        }
        finally { listener.Stop(); }
    }

    /// <summary>Issue: detect, alternatives, insights and transliteration fence their user turn with a
    /// per-call nonce, exactly like the translate path's record.</summary>
    static async Task AuxCallsAreFenced()
    {
        var (listener, baseUrl, bodies) = await StartMock(
            "{\"iso\":\"fr\",\"label\":\"French\"}",
            "{\"original_description\":\"d\",\"alternatives\":[]}",
            "{\"insights\":[]}",
            "{\"source\":\"s\",\"translation\":\"t\"}");
        var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = baseUrl, Model = "mock" }] };
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        try
        {
            await provider.DetectAsync("hello", default);
            await provider.AlternativesAsync("hello", "hallo", "en", "fr", default);
            await provider.InsightsAsync("hello", "hallo", "en", "fr", default);
            await provider.TransliterationAsync("こんにちは", "hello", "ja", "en", default);
            Check(bodies.Count == 4, $"fence: all four aux calls went out ({bodies.Count})");

            var payloads = new[]
            {
                "hello",
                "SOURCE:\nhello\n\nEXISTING TRANSLATION:\nhallo",
                "SOURCE:\nhello\n\nEXISTING TRANSLATION:\nhallo",
                "SOURCE:\nこんにちは\n\nEXISTING TRANSLATION:\nhello",
            };
            for (var i = 0; i < bodies.Count && i < payloads.Length; i++)
            {
                using var doc = JsonDocument.Parse(bodies[i]);
                var turn = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
                var end = turn.IndexOf('>');
                var tag = turn.StartsWith('<') && end > 1 ? turn[1..end] : "";
                Check(tag.Length > 0 && turn.EndsWith($"\n</{tag}>") && turn.Contains($"\n{payloads[i]}\n"),
                    $"fence: aux call {i + 1} wraps its payload in open/close tags ('{turn.Replace("\n", "\\n")}')");
            }
        }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// The eraser empties the source and the translation it produced. A chunk or a romanisation that was
    /// still in flight must not repaint the cleared panes afterwards.
    /// </summary>
    static async Task ClearDropsResults()
    {
        var port = 19100 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";


        using var listener = StartMock(prefix, async ctx =>
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream)) body = await sr.ReadToEndAsync();
            // a streaming request is a translation, anything else is the romanisation that follows it
            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                try
                {
                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n");
                    await w.FlushAsync();
                    // the slow route holds the last chunk back until after the clear, and keeps the
                    // first two long enough apart for the coalescer to flush them
                    if (path.StartsWith("/slow/", StringComparison.Ordinal))
                    {
                        await Task.Delay(200);
                        await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\" Welt\"}}]}\n\n");
                        await w.FlushAsync();
                        await Task.Delay(900);
                        await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}]}\n\n");
                    }
                    else
                    {
                        await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\" Welt\"}}]}\n\n");
                    }
                    await w.WriteAsync("data: [DONE]\n\n");
                    await w.FlushAsync();
                    w.Close();
                }
                catch (Exception) { /* the client hung up: the clear dropped the run */ }
                return;
            }

            // the romanisation answer is held back so the clear lands while it is in flight
            await Task.Delay(600);
            ctx.Response.ContentType = "application/json";
            const string answer = "{\\\"source\\\":\\\"konnichiwa sekai\\\",\\\"translation\\\":\\\"hello world\\\"}";
            var json = "{\"choices\":[{\"message\":{\"content\":\"" + answer + "\"}}]}";
            try { await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json)); }
            catch (Exception) { /* the client hung up */ }
            ctx.Response.Close();
        }, "clear: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SourceLang = "ja", TargetLang = "en",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var vm = NewTranslateVm(settings, dir, http);
        try
        {
            // the baseline: a finished run carries its romanisation on both panes
            vm.InputText = "こんにちは世界";
            await vm.TranslateAsync();
            for (var i = 0; i < 60 && !vm.HasTranslitTarget; i++) await Task.Delay(50);
            Check(vm.OutputText == "Hallo Welt" && vm.HasTranslitSource && vm.HasTranslitTarget,
                $"clear: a finished run shows its transliteration ('{vm.TranslitSource}' / '{vm.TranslitTarget}')");

            // the entry keeps it: restoring from history brings the line back instead of paying again
            vm.History.SelectedEntry = vm.History.Current;
            Check(vm.HasTranslitSource && vm.TranslitTarget == "hello world",
                $"translit: a restored entry shows its saved romanisation ('{vm.TranslitTarget}')");
            Check(vm.History.Current?.TranslitTarget == "hello world",
                "translit: the finished entry stores its romanisation");

            vm.ClearInputCommand.Execute(null);
            Check(vm.InputText == "" && vm.OutputText == "" && !vm.HasTranslitSource && !vm.HasTranslitTarget,
                $"clear: the eraser empties the source, the translation and both transliterations ('{vm.OutputText}')");

            // the romanisation of the next run is still in flight when the clear lands
            vm.InputText = "こんにちは世界";
            await vm.TranslateAsync();
            vm.ClearInputCommand.Execute(null);
            await Task.Delay(1000);
            Check(vm.OutputText == "" && !vm.HasTranslitSource && !vm.HasTranslitTarget,
                $"clear: a romanisation in flight does not repaint the cleared panes ('{vm.TranslitTarget}')");

            // the same for a run that is still streaming: its remaining chunks belong to the erased text
            settings.Providers[0].BaseUrl = prefix + "slow";
            vm.InputText = "こんにちは世界";
            var running = vm.TranslateAsync();
            for (var i = 0; i < 100 && vm.OutputText.Length == 0; i++) await Task.Delay(20);
            Check(vm.OutputText == "Hallo Welt", $"clear: the run streams into the pane first ('{vm.OutputText}')");
            vm.ClearInputCommand.Execute(null);
            await running;
            await Task.Delay(1200);
            Check(vm.InputText == "" && vm.OutputText == "" && !vm.IsBusy,
                $"clear: a chunk still in flight stays out of the cleared panes ('{vm.OutputText}')");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Restoring a history entry while a run is still streaming takes the panes away from it: its
    /// remaining chunks, status and romanisation belong to the replaced text, not the restored one.
    /// </summary>
    static async Task RestoreKeepsInFlightRun()
    {
        var port = 19400 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";


        // the stream sends one chunk, then holds the rest until the test releases it
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = StartMock(prefix, async ctx =>
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream)) body = await sr.ReadToEndAsync();
            if (!body.Contains("\"stream\":true", StringComparison.Ordinal)) { ctx.Response.Close(); return; }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            try
            {
                await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n");
                await w.FlushAsync();
                // a gap long enough for the coalescer to flush the first chunk into the pane
                await Task.Delay(200);
                await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\" Welt\"}}]}\n\n");
                await w.FlushAsync();
                await gate.Task; // the restore lands while the rest of the stream is held back
                await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}]}\n\n");
                await w.WriteAsync("data: [DONE]\n\n");
                await w.FlushAsync();
                w.Close();
            }
            catch (Exception) { /* the client hung up */ }
        }, "restore: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SourceLang = "ja", TargetLang = "en",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        var pre = new HistoryEntry { Source = "Guten Tag", Output = "Good day", From = "ja", To = "en" };
        history.Add(pre);
        using var http = new HttpClient();
        var vm = NewTranslateVm(settings, dir, http, history);
        try
        {
            vm.InputText = "こんにちは世界";
            var task = vm.TranslateAsync();
            for (var i = 0; i < 100 && vm.OutputText.Length == 0; i++) await Task.Delay(20);
            Check(vm.OutputText == "Hallo Welt", $"restore: the run streams its first chunks in ('{vm.OutputText}')");

            vm.History.SelectedEntry = pre; // the restore takes the panes, but must not cancel the run
            Check(vm.OutputText == "Good day", "restore: the panes switch to the restored entry");
            Check(vm.IsBusy, "restore: the in-flight run keeps the busy flag set");
            gate.TrySetResult(); // the late chunks and DONE arrive now, after the panes changed hands
            try { await task; Check(true, "restore: the in-flight run ends without throwing"); }
            catch (Exception e) { Check(false, $"restore: the in-flight run threw ({e.Message})"); }
            Check(!vm.IsBusy, "restore: the finished run leaves the busy flag clear");
            Check(history.Entries.Any(e => e.Source == "こんにちは世界" && e.Output == "Hallo Welt!"),
                "restore: the finished run lands in the history list");
            await Task.Delay(1200); // give any late chunk or romanisation the chance to misfire
            Check(vm.OutputText == "Good day", $"restore: a late chunk does not repaint the restored panes ('{vm.OutputText}')");
            Check(vm.History.Current == pre, "restore: the panel stays on the restored entry");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Restoring an entry loads its frozen usage (tokens plus the prices of that run) into the footer,
    /// not the prices that are live by now.
    /// </summary>
    static async Task RestoreLoadsUsageIntoFooter()
    {
        var port = 19920 + Random.Shared.Next(0, 40);
        var prefix = $"http://127.0.0.1:{port}/";


        using var listener = StartMock(prefix, async ctx =>
        {
            await WriteSse(ctx,
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hallo\"}}]}\n\n",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":500}}\n\n",
                "data: [DONE]\n\n");
        }, "restore usage: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "mock" }],
            InputPricePerMTok = 3, OutputPricePerMTok = 15,
            SourceLang = "ja", TargetLang = "en",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var vm = NewTranslateVm(settings, dir, http);
        try
        {
            vm.InputText = "こんにちは";
            await vm.TranslateAsync();
            var entry = vm.History.Current;
            Check(entry is { PromptTokens: 1000, CompletionTokens: 500, Cost: > 0 },
                $"restore usage: the entry carries the run's usage and cost ({entry?.PromptTokens}/{entry?.CompletionTokens}, {entry?.Cost})");

            settings.InputPricePerMTok = 30; // the live price moves after the run: the restore must not follow it
            vm.History.SelectedEntry = entry;
            Check(vm.History.Current == entry, "restore usage: the restored entry is current");
            Check(entry is not null && vm.Usage.UsageLabel == entry.UsageLabel,
                $"restore usage: the footer shows the entry's frozen usage ('{vm.Usage.UsageLabel}')");
            Check(vm.Usage.UsageLabel.Contains("0.0105"),
                $"restore usage: the footer label carries the frozen cost ('{vm.Usage.UsageLabel}')");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Fake provider whose request blocks inside the provider until the test releases it:
    /// <see cref="started"/> fires the moment the request begins (before the provider's first await),
    /// so the test can watch the idle counter while the request is in flight.</summary>
    sealed class GatedStub : ITranslationProvider
    {
        internal volatile TaskCompletionSource? started;
        internal volatile ManualResetEventSlim? gate;

        public async IAsyncEnumerable<string> TranslateAsync(string text, string from, string to,
            TranslationOptions options, QualityTier tier, [EnumeratorCancellation] CancellationToken ct,
            Action<TokenUsage>? onUsage = null, Action<bool>? onCut = null)
        {
            started?.TrySetResult(); // the request has begun
            gate?.Wait(); // hold it inside the provider; runs before the first await, so a Track-before-Enter
                          // start leaves the counter un-entered while the request is already in flight
            await Task.CompletedTask;
            yield return "Hallo";
        }

        public Task<string> DetectAsync(string text, CancellationToken ct, Action<TokenUsage>? onUsage = null) =>
            throw new NotSupportedException();

        public Task<(string Description, List<Alternative> Items)> AlternativesAsync(
            string text, string existing, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null) =>
            throw new NotSupportedException();

        public Task<List<WordInsight>> InsightsAsync(
            string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null) =>
            throw new NotSupportedException();

        public Task<(string Source, string Translation)> TransliterationAsync(
            string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null) =>
            throw new NotSupportedException();
    }

    /// <summary>Idle-counter race: a request that blocks inside the provider must leave WhenIdleAsync and
    /// UntilIdleAsync pending for its whole flight — the counter must cover the request's start, and a stale
    /// completed idle from a previous cycle must not be served while a run is in flight — and releasing the
    /// provider must complete them.</summary>
    static async Task IdleCounterRace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var stub = new GatedStub();
        var coordinator = new TranslationCoordinator(stub, new AppSettings(), new History(dir));
        try
        {
            stub.started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stub.gate = new ManualResetEventSlim(false);
            var run = Task.Run(() => coordinator.TranslateAsync("hello", "en", "de", QualityTier.Standard, _ => { }));
            await stub.started.Task; // the request is in flight inside the provider now
            var pending = true;
            for (var i = 0; i < 20 && pending; i++)
            {
                if (coordinator.WhenIdleAsync().IsCompleted) pending = false;
                else await Task.Delay(5);
            }
            Check(pending, "idle race: WhenIdleAsync is NOT completed while a request is in flight");
            var until = coordinator.UntilIdleAsync();
            Check(!until.IsCompleted, "idle race: UntilIdleAsync is NOT completed while a request is in flight");
            stub.gate.Set(); // let the fake provider answer
            await WithTimeout(run, "idle race: the released run finishes");
            await WithTimeout(until, "idle race: UntilIdleAsync completes once the run is done");
            Check(coordinator.WhenIdleAsync().IsCompleted, "idle race: WhenIdleAsync completed after the run");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    /// <summary>Idle-counter hammer: 50 sequential gated runs — while each run is in flight the idle task must
    /// never read as completed (no stale completed TCS across cycles while active&gt;0), and after each run it must be.</summary>
    static async Task IdleHammerAcrossCycles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var stub = new GatedStub();
        var coordinator = new TranslationCoordinator(stub, new AppSettings(), new History(dir));
        try
        {
            const int cycles = 50;
            var inFlightCompleted = 0;
            var doneCompleted = 0;
            for (var c = 0; c < cycles; c++)
            {
                stub.started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stub.gate = new ManualResetEventSlim(false);
                var run = Task.Run(() => coordinator.TranslateAsync("hello", "en", "de", QualityTier.Standard, _ => { }));
                await stub.started.Task; // this run is in flight inside the provider
                for (var i = 0; i < 5; i++)
                {
                    if (coordinator.WhenIdleAsync().IsCompleted) inFlightCompleted++;
                    await Task.Delay(2);
                }
                stub.gate.Set();
                await WithTimeout(run, $"idle hammer: cycle {c} run finishes");
                if (coordinator.WhenIdleAsync().IsCompleted) doneCompleted++;
            }
            Check(inFlightCompleted == 0,
                $"idle hammer: {cycles} in-flight runs never saw a completed idle ({inFlightCompleted} hits)");
            Check(doneCompleted == cycles,
                $"idle hammer: idle completed after each of the {cycles} runs ({doneCompleted}/{cycles})");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // the settings profiles tab VM: the row markers (launch default vs running here) and the
    // delete refusals must carry a reason — "set B as default, still running in A, can't remove A"
    // was a silent dead end before the markers were split
}
