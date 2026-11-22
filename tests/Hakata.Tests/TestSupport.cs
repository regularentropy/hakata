using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Xunit;

// The tests share one process-wide HAKATA_HOME and localhost mock ports, so they must never run
// in parallel with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
// The headless Avalonia app the [AvaloniaFact] tests run against.
[assembly: AvaloniaTestApplication(typeof(Hakata.Tests.TestAppBuilder))]

namespace Hakata.Tests
{
    /// <summary>Builds the headless app for the test session: the real app over the headless platform.</summary>
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Hakata.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .WithInterFont();
    }

    internal static class TestSupport
    {
        /// <summary>Points HAKATA_HOME at a fresh temp dir before anything else runs, so the real
        /// settings/history load never touches a developer's home, and HAKATA_NO_PICKER keeps the
        /// headless App init from ever blocking on the profile picker (App.axaml.cs:45-65).</summary>
        [ModuleInitializer]
        public static void Init()
        {
            var home = Path.Combine(Path.GetTempPath(), "hakata-tests-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("HAKATA_HOME", home);
            Environment.SetEnvironmentVariable("HAKATA_NO_PICKER", "1");
        }
    }
}

namespace Hakata
{
    /// <summary>Shared helpers for the test suites: temp homes, mock HTTP servers, view-model
    /// factories, timeout guards, audio fakes and headless rendering.</summary>
    public abstract class SelfTestBase
    {
        /// <summary>Starts a mock HTTP server on the prefix and answers every request with handle.
        /// When failMessage is given, a failed Start fails the test with that message instead of throwing.</summary>
        protected static HttpListener StartMock(string prefix, Func<HttpListenerContext, Task> handle, string? failMessage = null)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try { listener.Start(); }
            catch (Exception e)
            {
                if (failMessage is not null) Assert.Fail(failMessage + e.Message);
                throw;
            }
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { return; }
                    await handle(ctx);
                }
            });
            return listener;
        }

        /// <summary>Mock server that answers each of the given chat completions in order and
        /// records the request bodies.</summary>
        protected static async Task<(HttpListener Listener, string BaseUrl, List<string> Bodies)> StartMock(params string[] responses)
        {
            var port = 18080 + Random.Shared.Next(0, 500);
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            var bodies = new List<string>();

            _ = Task.Run(async () =>
            {
                for (var i = 0; i < responses.Length; i++)
                {
                    var ctx = await listener.GetContextAsync();
                    using (var sr = new StreamReader(ctx.Request.InputStream)) bodies.Add(await sr.ReadToEndAsync());
                    ctx.Response.ContentType = "application/json";
                    var payload = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = responses[i] } } } });
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
            await Task.Yield();
            return (listener, prefix + "v1", bodies);
        }

        /// <summary>Runs body with HAKATA_HOME pointed at a fresh temp dir, then restores the
        /// variable and deletes the dir. Sync form; use WithTempHomeAsync for async bodies.</summary>
        protected static T WithTempHome<T>(Func<string, T> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hakata-test-" + Guid.NewGuid().ToString("n"));
            var previous = Environment.GetEnvironmentVariable("HAKATA_HOME");
            try
            {
                Directory.CreateDirectory(dir);
                Environment.SetEnvironmentVariable("HAKATA_HOME", dir);
                return body(dir);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HAKATA_HOME", previous);
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        protected static void WithTempHome(Action<string> body) => WithTempHome<object?>(dir => { body(dir); return null; });

        /// <summary>The standard end-to-end view model: one provider from the settings, a history in dir,
        /// fakes for mic and player.</summary>
        protected static TranslateViewModel NewTranslateVm(AppSettings settings, string dir, HttpClient http, History? history = null)
        {
            history ??= new History(dir);
            var provider = new OpenAiCompatibleProvider(http, settings);
            return new TranslateViewModel(new SettingsService(settings), history,
                new TranslationCoordinator(provider, settings, history), provider, new FakeRecorder(), new FakePlayer());
        }

        /// <summary>The synchronous form gets the same guard: a blocking call fails the test instead of freezing the run.</summary>
        protected static Task WithTimeout(Action test, string what) => WithTimeout(Task.Run(test), what);

        protected static async Task WithTimeout(Task task, string what)
        {
            // a stalled mock server must fail the test, not hang the run
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) != task)
                Assert.Fail(what + ": timed out after 30s");
            await task;
        }

        /// <summary>Provider stub whose translation blocks until the token fires (a run that only ends via Cancel).</summary>
        protected sealed class BlockingStub : ITranslationProvider
        {
            public async IAsyncEnumerable<string> TranslateAsync(string text, string from, string to,
                TranslationOptions options, QualityTier tier, [EnumeratorCancellation] CancellationToken ct,
                Action<TokenUsage>? onUsage = null, Action<bool>? onCut = null)
            {
                await Task.Delay(Timeout.Infinite, ct); // runs until cancelled
                yield break; // unreachable
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

        /// <summary>Provider stub whose extras honor the token; everything else is out of scope here.</summary>
        protected sealed class ExtraStub : ITranslationProvider
        {
            public async Task<(string Description, List<Alternative> Items)> AlternativesAsync(
                string text, string existing, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null)
            {
                await Task.Delay(300, ct);
                return ("desc", [new Alternative { Text = "servus" }]);
            }

            public async Task<List<WordInsight>> InsightsAsync(
                string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null)
            {
                await Task.Delay(300, ct);
                return [new WordInsight { Word = "hallo" }];
            }

            public IAsyncEnumerable<string> TranslateAsync(string text, string from, string to,
                TranslationOptions options, QualityTier tier, CancellationToken ct, Action<TokenUsage>? onUsage = null,
                Action<bool>? onCut = null) => throw new NotSupportedException();

            public Task<string> DetectAsync(string text, CancellationToken ct, Action<TokenUsage>? onUsage = null) =>
                throw new NotSupportedException();

            public Task<(string Source, string Translation)> TransliterationAsync(
                string text, string translation, string from, string to, CancellationToken ct, Action<TokenUsage>? onUsage = null) =>
                throw new NotSupportedException();
        }

        /// <summary>A recorder that hands over one fixed take instead of touching a sound card.</summary>
        protected sealed class FakeRecorder : IAudioRecorder
        {
            public bool IsRecording { get; private set; }

            public void Start() => IsRecording = true;

            public byte[] Stop()
            {
                IsRecording = false;
                return Wav.FromPcm(new byte[3200], MicrophoneRecorder.SampleRate, 1, 16);
            }

            public void Dispose() => IsRecording = false;
        }

        /// <summary>A player that keeps the clip instead of opening a sound card.</summary>
        protected sealed class FakePlayer : ISpeechPlayer
        {
            public byte[]? LastClip { get; set; }

            public string? LastMime { get; private set; }

            public bool IsPlaying { get; private set; }

            public void Play(byte[] audio, string mimeType)
            {
                LastClip = audio;
                LastMime = mimeType;
                IsPlaying = true;
            }

            public void Stop() => IsPlaying = false;

            public void Dispose() => IsPlaying = false;
        }

        /// <summary>Scratch output under the OS temp dir, never a literal /tmp (Windows-first app).</summary>
        protected static string Scratch(string fileName) => Path.Combine(Path.GetTempPath(), fileName);

        protected static void Render(Window window, string path)
        {
            var pixel = new PixelSize((int)window.Width, (int)window.Height);
            using var shot = new RenderTargetBitmap(pixel, new Vector(96, 96));
            shot.Render(window);
            using var fs = File.Create(path);
            shot.Save(fs, PngBitmapEncoderOptions.Default);
        }

        protected static void RenderTo(Control target, Size size, string path)
        {
            var w = Math.Max(1, (int)size.Width);
            var h = Math.Max(1, (int)size.Height);
            using var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            bmp.Render(target);
            using var fs = File.Create(path);
            bmp.Save(fs, PngBitmapEncoderOptions.Default);
        }

        protected static void Pump(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(25);
            }
        }
    }
}
