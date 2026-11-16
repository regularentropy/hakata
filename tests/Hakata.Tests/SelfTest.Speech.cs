using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.SoundFile;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata;

/// <summary>Dependency-free checks for the parts that break silently: endpoints, SSE, prompts, JSON parsing, history.</summary>
public static partial class SelfTest
{
    /// <summary>Async form of WithTempHome: the temp dir and env var live until the body's task completes.</summary>
    static async Task WithTempHomeAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var previous = Environment.GetEnvironmentVariable("HAKATA_HOME");
        try
        {
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("HAKATA_HOME", dir);
            await body(dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", previous);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Cancel during a speak render (the profile-swap path): the in-flight request is dropped, no clip
    /// reaches the player, the command reports the cancelled status, and the coordinator goes idle.</summary>
    static async Task SpeakCancelDropsInFlightRender()
    {
        var port = 19300 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception e) { Check(false, "speak cancel: mock server start: " + e.Message); return; }

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try { _ = await listener.GetContextAsync(); } // never answered: the render hangs until Cancel
                catch { return; }
            }
        });

        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SoundUrl = prefix + "hold/audio/speech",
        };
        var history = new History(dir);
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        var player = new FakePlayer();
        var vm = new TranslateViewModel(new SettingsService(settings), history,
            new TranslationCoordinator(provider, settings, history), provider, new FakeRecorder(), player);
        try
        {
            vm.OutputText = "Hallo world";
            var speaking = vm.Speak.ToggleCommand.ExecuteAsync(null);
            await Task.Delay(200); // the mock holds the answer, so the render is in flight
            vm.Speak.Cancel();
            await WithTimeout(speaking, "speak cancel: the command returns after Cancel");
            Check(!player.IsPlaying && player.LastClip is null,
                "speak cancel: a cancelled render never reaches the player");
            Check(vm.Status == "Playback stopped" && !vm.Speak.IsSpeaking,
                $"speak cancel: the command reports the cancelled status ('{vm.Status}')");
            Check(vm.WhenIdleAsync().IsCompleted, "speak cancel: the coordinator is idle once the render is dropped");

            // the same through TranslateViewModel.Cancel(), the path a profile swap takes
            var speaking2 = vm.Speak.ToggleCommand.ExecuteAsync(null);
            await Task.Delay(200);
            vm.Cancel();
            await WithTimeout(speaking2, "speak cancel: vm.Cancel returns after the render is dropped");
            Check(!player.IsPlaying && vm.Status == "Playback stopped" && !vm.Speak.IsSpeaking,
                $"speak cancel: vm.Cancel() reaches the speak render too ('{vm.Status}')");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Issue #0057: a failed catalog fetch must not cache its null forever — the next call retries.</summary>
    static async Task SpeechCatalogRetry()
    {
        var catalogGets = 0;
        using var http = new HttpClient(new CatalogRetryHandler(() => ++catalogGets));
        var provider = new OpenAiCompatibleProvider(http, new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = "http://stub/v1", Model = "mock" }] });
        var wav = Wav.FromPcm(new byte[3200], 16000, 1, 16); // one second of audio
        var cost1 = 0.0;
        await provider.TranscribeAsync(wav, default, c => cost1 = c);
        var cost2 = 0.0;
        await provider.TranscribeAsync(wav, default, c => cost2 = c);
        Check(catalogGets == 2, $"speech retry: a failed catalog fetch is re-fetched on the next call (got {catalogGets} gets)");
        Check(cost1 == 0, $"speech retry: the failed fetch prices as free ({cost1})");
        Check(Math.Abs(cost2 - 0.01) < 1e-9, $"speech retry: the retried fetch prices from the catalog ({cost2})");
    }

    sealed class CatalogRetryHandler(Func<int> nextCatalogGet) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                // the first catalog read fails the way a dropped connection does; later ones publish a price
                if (nextCatalogGet() == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"models\":[{\"id\":\"whisper-1\",\"pricing\":{\"billing_unit\":\"per_minute\",\"price_per_minute\":6}}]}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hallo\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    static void SpeechUrls()
    {
        Check(ProviderUrls.AudioUrl("https://api.openai.com/v1") == "https://api.openai.com/v1/audio/transcriptions",
            "speech url: /v1 base");
        Check(ProviderUrls.AudioUrl("http://localhost:8000") == "http://localhost:8000/v1/audio/transcriptions",
            "speech url: bare base gets /v1");
        Check(ProviderUrls.AudioUrl("https://gw/v1/proxy") == "https://gw/v1/proxy/audio/transcriptions",
            "speech url: pathed base keeps its path");
        Check(ProviderUrls.AudioUrl("https://api.vendor.example/v3/compat") == "https://api.vendor.example/v3/audio/transcriptions",
            "speech url: a /compat chat mirror drops that segment, the audio route sits on the native base");
        Check(ProviderUrls.AudioModelsUrl("https://api.vendor.example/v3/compat") == "https://api.vendor.example/v3/audio/models",
            "speech models url: the audio catalog sits beside the transcription route");
        Check(ProviderUrls.AudioModelsUrl("http://localhost:8000") == "http://localhost:8000/v1/audio/models",
            "speech models url: bare base gets /v1");
        Check(ProviderUrls.AudioUrl("https://openrouter.ai/api/v1") == "https://openrouter.ai/api/v1/audio/transcriptions",
            "speech url: openrouter keeps its own path");
        Check(ProviderUrls.AudioUrl("https://api.groq.com/openai/v1") == "https://api.groq.com/openai/v1/audio/transcriptions",
            "speech url: groq's nested /v1 doubles up like its chat route");
        Check(new AppSettings().EffectiveSpeechModel == "whisper-1", "speech model: empty field falls back to whisper-1");
        Check(new AppSettings { SpeechModel = " whisper-large-v3 " }.EffectiveSpeechModel == "whisper-large-v3",
            "speech model: a typed id is trimmed and sent as is");

        Check(ProviderUrls.SpeakUrl("https://api.openai.com/v1") == "https://api.openai.com/v1/audio/speech",
            "sound url: /v1 base");
        Check(ProviderUrls.SpeakUrl("http://localhost:8000") == "http://localhost:8000/v1/audio/speech",
            "sound url: bare base gets /v1");
        Check(ProviderUrls.SpeakUrl("https://api.vendor.example/v3/compat") == "https://api.vendor.example/v3/audio/speech",
            "sound url: a /compat chat mirror drops that segment");
        Check(ProviderUrls.AudioModelsUrl("https://api.vendor.example/v3/compat", "tts") ==
              "https://api.vendor.example/v3/audio/models?type=tts",
            "sound models url: the catalog filters by type");
        Check(new AppSettings().EffectiveSoundModel == "tts-1" && new AppSettings().EffectiveSoundVoice == "alloy",
            "sound model: empty fields fall back to tts-1 and alloy");
        Check(new AppSettings { SoundModel = " gpt-4o-mini-tts ", SoundVoice = "nova" }.EffectiveSoundModel == "gpt-4o-mini-tts",
            "sound model: a typed id is trimmed and sent as is");

        var wav = Wav.FromPcm(new byte[8], 16000, 1, 16);
        Check(wav.Length == Wav.HeaderBytes + 8, "wav: header plus payload");
        Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE",
            "wav: RIFF/WAVE tags");
        Check(BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 34) == 16,
            "wav: sample rate and bit depth in the fmt chunk");
        Check(Encoding.ASCII.GetString(wav, 36, 4) == "data" && BitConverter.ToInt32(wav, 40) == 8,
            "wav: data chunk declares the payload");
        Check(Wav.FromPcm([], 16000, 1, 16).Length == Wav.HeaderBytes, "wav: an empty take is just the header");
    }

    /// <summary>A player that behaves like a device: Play blocks until Stop, as a real clip does.</summary>
    sealed class BlockingPlayer : ISpeechPlayer
    {
        readonly ManualResetEventSlim finished = new();

        public bool Stopped { get; private set; }

        public bool IsPlaying { get; private set; }

        public void Play(byte[] audio, string mimeType)
        {
            IsPlaying = true;
            finished.Wait(TimeSpan.FromSeconds(20));
            IsPlaying = false;
        }

        public void Stop()
        {
            Stopped = true;
            finished.Set();
        }

        public void Dispose() => Stop();
    }

    /// <summary>The mic button: record, upload to /audio/transcriptions, land the text in the source box.</summary>
    static async Task DictationRoundTrip()
    {
        var port = 19120 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        string? seenBody = null;
        string? seenAuth = null;
        string? seenPath = null;


        using var listener = StartMock(prefix, async ctx =>
        {
            // the per-minute price lookup runs after the transcript; it is not the request under test
            if (ctx.Request.Url?.AbsolutePath.EndsWith("/audio/models", StringComparison.Ordinal) == true)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            seenAuth = ctx.Request.Headers["Authorization"];
            seenPath = ctx.Request.Url?.AbsolutePath;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                seenBody = await sr.ReadToEndAsync();
            // this route mirrors a base64-only gateway: a file upload is the wrong content type for it
            var wantsBase64 = seenPath?.StartsWith("/base64/", StringComparison.Ordinal) == true;
            if (wantsBase64 && ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.Ordinal) == true)
            {
                ctx.Response.StatusCode = 415;
                ctx.Response.Close();
                return;
            }
            // a route that answers the transcript as plain text instead of a JSON envelope
            if (seenPath?.StartsWith("/raw/", StringComparison.Ordinal) == true)
            {
                ctx.Response.ContentType = "text/plain";
                try { await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("  Hallo rohe Welt  ")); }
                catch (Exception) { /* the client hung up */ }
                ctx.Response.Close();
                return;
            }
            // a route that answers slowly, so a cancel can be pressed while the upload is in flight
            if (seenPath?.StartsWith("/slow/", StringComparison.Ordinal) == true) await Task.Delay(800);
            ctx.Response.ContentType = "application/json";
            try { await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"text\":\"Hallo Welt\"}")); }
            catch (Exception) { /* the client hung up: the cancel case, not a failure */ }
            ctx.Response.Close();
        }, "dictation: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SpeechModel = "whisper-large-v3",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var vm = NewTranslateVm(settings, dir, http);
        try
        {
            vm.InputText = "Schon getippt:";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(vm.Dictation.IsRecording && vm.Dictation.MicLabel == "Stop", "dictation: the first press starts recording");
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(!vm.Dictation.IsRecording, "dictation: the second press stops recording");
            Check(vm.InputText == "Schon getippt: Hallo Welt", $"dictation: the transcript extends the source box ('{vm.InputText}')");
            Check(seenPath == "/v1/audio/transcriptions", $"dictation: posted to the transcriptions endpoint ({seenPath})");
            Check(seenAuth == "Bearer test-key", "dictation: the provider key is used");
            Check(seenBody?.Contains("whisper-large-v3") == true, "dictation: the selected speech model is sent");
            Check(seenBody?.Contains("speech.wav") == true && seenBody?.Contains("audio/wav") == true,
                "dictation: the recording is a WAV upload");

            vm.InputText = "";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(vm.InputText == "Hallo Welt", "dictation: an empty source box takes the transcript alone");

            // a chat-only base URL (gateway /v3/compat, OpenRouter, Ollama) has no transcription route
            settings.SpeechUrl = prefix + "stt/audio/transcriptions";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(seenPath == "/stt/audio/transcriptions", $"dictation: an explicit transcription URL wins over the guess ({seenPath})");

            // a gateway that wants base64 in a JSON body answers the multipart upload with 415
            settings.Providers[0].BaseUrl = prefix + "base64";
            settings.SpeechUrl = "";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(seenPath == "/base64/audio/transcriptions", $"dictation: the fallback keeps the derived route ({seenPath})");
            Check(seenBody?.Contains("data:audio/wav;base64,") == true,
                "dictation: a 415 answer is retried with base64 audio in a JSON body");

            var settingsVm = new SettingsViewModel(new SettingsService(settings), http);
            Check(settingsVm.SpeechUrlHint.Contains(prefix + "base64/audio/transcriptions"),
                "speech settings: the hint names the derived endpoint so the guess is never invisible");
            Check(settingsVm.SpeechUrl == settings.SpeechUrl, "speech settings: the override loads back into the field");

            // Esc during an upload drops it: the transcript must not land after the box was cleared
            vm.InputText = "";
            settings.Providers[0].BaseUrl = prefix + "slow";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            var upload = vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await Task.Delay(150);
            vm.Cancel();
            await upload;
            Check(vm.InputText == "" && vm.Status == "Transcription cancelled",
                $"dictation: a cancelled upload leaves the box alone ('{vm.InputText}' / '{vm.Status}')");

            // a provider that answers the transcript as plain text: the body is the transcript
            settings.SpeechUrl = prefix + "raw/audio/transcriptions";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(vm.InputText == "Hallo rohe Welt", $"dictation: a plain-text answer is the transcript ('{vm.InputText}')");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>The sound button: post the translation to /audio/speech, hand the audio to the player.</summary>
    static async Task SpeakRoundTrip()
    {
        var port = 19320 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        string? seenBody = null;
        string? seenAuth = null;
        string? seenPath = null;
        var clip = Encoding.ASCII.GetBytes("ID3-fake-mp3-payload");
        var speechPosts = new int[1];
        var wavClip = Wav.FromPcm(new byte[64], 16000, 1, 16);


        using var listener = StartMock(prefix, async ctx =>
        {
            // the per-character price lookup runs after the clip; it is not the request under test
            if (ctx.Request.Url?.AbsolutePath.EndsWith("/audio/models", StringComparison.Ordinal) == true)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            seenAuth = ctx.Request.Headers["Authorization"];
            seenPath = ctx.Request.Url?.AbsolutePath;
            if (seenPath?.EndsWith("/audio/speech", StringComparison.Ordinal) == true) speechPosts[0]++;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                seenBody = await sr.ReadToEndAsync();
            // the view model prices itself from /models on construction; answer that before the audio routes
            if (seenPath?.EndsWith("/models", StringComparison.Ordinal) == true)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"mock\"}]}"));
                ctx.Response.Close();
                return;
            }
            // a chat-only gateway answers the speech route with JSON instead of audio
            if (seenPath?.StartsWith("/chatty/", StringComparison.Ordinal) == true)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"no speech route here\"}}"));
                ctx.Response.Close();
                return;
            }
            // a slow render, so the Stop can arrive while the provider is still working
            if (seenPath?.StartsWith("/slow/", StringComparison.Ordinal) == true)
            {
                await Task.Delay(600);
                try
                {
                    ctx.Response.ContentType = "audio/mpeg";
                    await ctx.Response.OutputStream.WriteAsync(clip);
                }
                catch (Exception) { /* the client gave up on the clip */ }
                ctx.Response.Close();
                return;
            }
            // a connection that drops mid-body: headers promise 1000 bytes, 10 arrive
            if (seenPath?.StartsWith("/short/", StringComparison.Ordinal) == true)
            {
                try
                {
                    ctx.Response.ContentLength64 = 1000;
                    await ctx.Response.OutputStream.WriteAsync(clip, 0, 10);
                    ctx.Response.Close();
                }
                catch (Exception) { /* closing short of the declared length may throw here */ }
                return;
            }
            // the native surface hands the audio back in a JSON envelope instead of raw bytes
            if (seenPath?.StartsWith("/native/", StringComparison.Ordinal) == true)
            {
                ctx.Response.ContentType = "application/json";
                var envelope = "{\"id\":\"s1\",\"audio\":{\"b64_json\":\"" + Convert.ToBase64String(wavClip) +
                               "\",\"mime_type\":\"audio/wav\"}}";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(envelope));
                ctx.Response.Close();
                return;
            }
            ctx.Response.ContentType = "audio/mpeg";
            await ctx.Response.OutputStream.WriteAsync(clip);
            ctx.Response.Close();
        }, "sound: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SoundModel = "gpt-4o-mini-tts", SoundVoice = "nova",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        using var http = new HttpClient();
        var player = new FakePlayer();
        var provider = new OpenAiCompatibleProvider(http, settings);
        var vm = new TranslateViewModel(new SettingsService(settings), history,
            new TranslationCoordinator(provider, settings, history), provider, new FakeRecorder(), player);
        try
        {
            // nothing translated yet: the button explains itself instead of posting an empty input
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(vm.Status == "Translate first, then listen" && player.LastClip is null,
                $"sound: an empty translation is not spoken ('{vm.Status}')");

            vm.OutputText = "Hallo world";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(seenPath == "/v1/audio/speech", $"sound: posted to the speech endpoint ({seenPath})");
            Check(seenAuth == "Bearer test-key", "sound: the provider key is used");
            Check(seenBody?.Contains("gpt-4o-mini-tts") == true && seenBody?.Contains("\"nova\"") == true,
                $"sound: the selected model and voice are sent ({seenBody})");
            Check(seenBody?.Contains("Hallo world") == true, "sound: the translation is the input");
            Check(player.LastClip is { Length: > 0 } && player.LastClip!.SequenceEqual(clip) && player.LastMime == "audio/mpeg",
                "sound: the returned audio reaches the player with its type");
            Check(!vm.Speak.IsSpeaking && vm.Status == "Playback finished", $"sound: playback ends and says so ('{vm.Status}')");

            // a chat-only base URL has no speech route: the user can name the endpoint instead
            settings.SoundUrl = prefix + "native/audio/speech";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(seenPath == "/native/audio/speech", $"sound: an explicit speech URL wins over the guess ({seenPath})");
            Check(player.LastClip is { Length: > 0 } && player.LastClip!.SequenceEqual(wavClip) && player.LastMime == "audio/wav",
                $"sound: a JSON envelope is unwrapped into real audio ({player.LastMime})");

            // a JSON body where audio belongs is an error, not a clip to play
            settings.SoundUrl = prefix + "chatty/audio/speech";
            player.LastClip = null;
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(vm.IsError && vm.Status.Contains("no speech route here") && player.LastClip is null,
                $"sound: a JSON answer is reported instead of played ('{vm.Status}')");

            // a body cut mid-transfer is an error, never playback; HttpListener resets the
            // connection here, so the reset wins over the declared-length message — the exact
            // "cut short" wording is checked at the helper level in BoundedResponses
            settings.SoundUrl = prefix + "short/audio/speech";
            player.LastClip = null;
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(vm.IsError && !vm.Status.Contains("Playback finished") && player.LastClip is null,
                $"sound: a body cut mid-transfer is refused ('{vm.Status}')");

            await SpeakCacheCases(prefix, clip, http, speechPosts);
            await StopStopsEverything(prefix, clip);
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Rendered audio is kept on disk per history entry, and retention follows the history.</summary>
    static async Task SpeakCacheCases(string prefix, byte[] clip, HttpClient http, int[] speechPosts)
    {
        await WithTempHomeAsync(async home =>
        {
            // the cache lives under the app data folder
            var clips = Path.Combine(home, "sound-cache");
            int Files(string entryId) => Directory.Exists(clips) ? Directory.GetFiles(clips, entryId + "-*.audio").Length : 0;
            var settings = new AppSettings
            {
                Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
                SoundModel = "m", SoundVoice = "v", CacheSound = true, SoundCacheEntries = 20,
            };
            var provider = new OpenAiCompatibleProvider(http, settings);
            var history = new History(home);
            var player = new FakePlayer();
            var vm = new TranslateViewModel(new SettingsService(settings), history,
                new TranslationCoordinator(provider, settings, history), provider, new FakeRecorder(), player);

            // the clip belongs to the entry the sound button is speaking for
            var older = new HistoryEntry { Source = "Hallo world", Output = "Hallo world" };
            history.Add(older);
            vm.History.Current = older;
            vm.OutputText = older.Output;

            var before = speechPosts[0];
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(speechPosts[0] == before + 1 && vm.Status == "Playback finished",
                $"sound cache: the first press renders and stores ('{vm.Status}')");
            Check(Files(older.Id) == 1, $"sound cache: one clip for the entry ({Files(older.Id)})");

            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(speechPosts[0] == before + 1, "sound cache: the second press sends no request");
            Check(vm.Status == "Played from cache" && player.LastClip!.SequenceEqual(clip),
                $"sound cache: the stored clip is what plays ('{vm.Status}')");

            // export serves the cached clip without another request
            var exported = await vm.Speak.ClipForExportAsync();
            Check(speechPosts[0] == before + 1 && exported is { } ex && ex.Audio.SequenceEqual(clip) && ex.MimeType.Length > 0,
                "export: the cached clip is exported without a new render");

            // Ctrl+click ignores the stored clip and renders again; the fresh one replaces it
            await vm.Speak.ForceToggleCommand.ExecuteAsync(null);
            Check(speechPosts[0] == before + 2 && vm.Status == "Playback finished",
                $"sound cache: the force press renders again ('{vm.Status}')");
            Check(Files(older.Id) == 1, $"sound cache: the re-render replaces the stored clip ({Files(older.Id)})");

            // another voice, another text: more clips of the same entry
            settings.SoundVoice = "w";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(speechPosts[0] == before + 3 && Files(older.Id) == 2,
                "sound cache: a different voice renders its own clip");
            vm.OutputText = "Ganz andere Worte";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(speechPosts[0] == before + 4 && Files(older.Id) == 3, "sound cache: new text renders again");

            // retention: the limit counts history entries, newest first
            settings.SoundCacheEntries = 1;
            var newest = new HistoryEntry { Source = "Hallo world", Output = "Hallo world" };
            history.Add(newest); // adding raises Changed, so the policy applies without a press
            Check(Files(older.Id) == 0, $"sound cache: an entry past the limit loses its audio ({Files(older.Id)})");
            vm.History.Current = newest;
            vm.OutputText = newest.Output;
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(Files(newest.Id) == 1, $"sound cache: the newest entry keeps its audio ({Files(newest.Id)})");

            // a favorite keeps its audio however old it is, even with the limit at zero
            settings.SoundCacheEntries = 0;
            older.Favorite = true;
            history.Save(); // raises Changed
            Check(Files(newest.Id) == 0, "sound cache: limit zero drops a non-favorite entry");
            vm.History.Current = older;
            vm.OutputText = older.Output;
            settings.SoundVoice = "v";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(Files(older.Id) == 1, $"sound cache: a favorite keeps its audio ({Files(older.Id)})");
            Check(Files(newest.Id) == 0, "sound cache: the non-favorite stays out");

            // clearing the history clears the audio with it
            history.Clear();
            Check(Files(older.Id) == 0 && Files(newest.Id) == 0, "sound cache: clearing the history removes the audio");

            // the switch off means always fresh audio and nothing stored
            settings.CacheSound = false;
            vm.History.Current = older;
            var rendered = speechPosts[0];
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(speechPosts[0] == rendered + 1 && vm.Status == "Playback finished" && Files(older.Id) == 0,
                "sound cache: switched off, the same text is rendered again and nothing is kept");
        });
    }

    /// <summary>Speech is billed like the translation: the provider's own number first, its published rate second.</summary>
    static async Task SpeechCostCases()
    {
        var port = 19520 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        var clip = Encoding.ASCII.GetBytes("ID3-fake-mp3-payload");


        using var listener = StartMock(prefix, async ctx =>
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var tts = ctx.Request.Url?.Query.Contains("type=tts", StringComparison.Ordinal) == true;
            string body;
            if (path.StartsWith("/manual/", StringComparison.Ordinal))
            {
                // a provider that publishes no audio catalog at all: only the manual fields price it
                if (path.EndsWith("/audio/models", StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                if (path.EndsWith("/audio/speech", StringComparison.Ordinal))
                {
                    ctx.Response.ContentType = "audio/mpeg";
                    await ctx.Response.OutputStream.WriteAsync(clip);
                    ctx.Response.Close();
                    return;
                }
                body = "{\"text\":\"hallo\"}";
            }
            else if (path.EndsWith("/audio/models", StringComparison.Ordinal))
                // a speech catalog that publishes its own flat prices
                body = tts
                    ? "{\"models\":[{\"id\":\"tts-model\",\"pricing\":{\"billing_unit\":\"per_m_chars\",\"price_per_m_chars\":22}}]}"
                    : "{\"models\":[{\"id\":\"stt-model\",\"pricing\":{\"billing_unit\":\"per_minute\",\"price_per_minute\":6}}]}";
            else if (path.StartsWith("/costedstt/", StringComparison.Ordinal))
                body = "{\"text\":\"hallo\",\"usage\":{\"cost\":0.004,\"seconds\":1}}";
            else if (path.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
                body = "{\"text\":\"hallo\"}"; // a provider that bills but says nothing
            else if (path.StartsWith("/costed/", StringComparison.Ordinal))
                body = "{\"audio\":{\"b64_json\":\"" + Convert.ToBase64String(clip) +
                       "\",\"mime_type\":\"audio/mpeg\"},\"usage\":{\"characters\":12,\"cost\":0.005}}";
            else if (path.EndsWith("/audio/speech", StringComparison.Ordinal))
            {
                ctx.Response.ContentType = "audio/mpeg";
                await ctx.Response.OutputStream.WriteAsync(clip);
                ctx.Response.Close();
                return;
            }
            else body = "{\"data\":[{\"id\":\"mock\"}]}";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
            ctx.Response.Close();
        }, "speech cost: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SpeechModel = "stt-model", SoundModel = "tts-model", SoundVoice = "v", CacheSound = false,
        };
        await WithTempHomeAsync(async home =>
        {
            var history = new History(home);
            using var http = new HttpClient();
            // manual prices are set from the start and must lose to the reported costs and catalog rates below
            settings.SpeechPricePerMinute = 60;
            settings.SoundPricePerMChars = 44;
            var provider = new OpenAiCompatibleProvider(http, settings);
            var coordinator = new TranslationCoordinator(provider, settings, history);
            var vm = new TranslateViewModel(new SettingsService(settings), history, coordinator, provider,
                new FakeRecorder(), new FakePlayer());

            var entry = new HistoryEntry { Source = "Hallo world", Output = "Hallo world" };
            history.Add(entry);
            vm.History.Current = entry;
            vm.OutputText = entry.Output;

            // the provider's own number wins
            settings.SoundUrl = prefix + "costed/audio/speech";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            Check(Math.Abs(entry.Cost - 0.005) < 1e-9, $"speech cost: the reported render cost lands on the entry ({entry.Cost})");
            Check(vm.History.SpendLabel.Contains("0.0050"), $"speech cost: the spend label counts it ('{vm.History.SpendLabel}')");
            // #0033 core regression: speech cost is visible in the footer, straight from the entry
            Check(vm.Usage.UsageLabel == entry.UsageLabel && vm.Usage.UsageLabel.Contains("~$0.0050"),
                $"speech cost: the footer shows the entry's grown cost ('{vm.Usage.UsageLabel}')");

            // no usage in the response: the published per-character price decides
            settings.SoundUrl = prefix + "priced/audio/speech";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            var expected = entry.Output.Length / 1_000_000d * 22;
            Check(Math.Abs(entry.Cost - (0.005 + expected)) < 1e-9,
                $"speech cost: an unpublished render is priced from the catalog ({entry.Cost:0.000000} vs {0.005 + expected:0.000000})");
            Check(vm.Usage.UsageLabel == entry.UsageLabel,
                $"speech cost: the footer follows the entry after the catalog-priced render ('{vm.Usage.UsageLabel}')");

            // dictation: no usage in the answer, so seconds × the published per-minute price
            settings.SpeechUrl = "";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            var dictated = 0.005 + expected + Wav.Seconds(Wav.FromPcm(new byte[3200], 16000, 1, 16)) / 60d * 6;
            Check(Math.Abs(entry.Cost - dictated) < 1e-9, $"speech cost: dictation adds its minutes ({entry.Cost:0.000000})");
            Check(vm.Usage.UsageLabel == entry.UsageLabel,
                $"speech cost: the footer follows the entry after dictation ('{vm.Usage.UsageLabel}')");

            // a reported transcription cost beats the published rate
            settings.SpeechUrl = prefix + "costedstt/audio/transcriptions";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            Check(Math.Abs(entry.Cost - (dictated + 0.004)) < 1e-9,
                $"speech cost: a reported transcription cost wins ({entry.Cost:0.000000})");
            Check(vm.Usage.UsageLabel == entry.UsageLabel && vm.Usage.UsageLabel.Contains(Pricing.CostLabel(dictated + 0.004)),
                $"speech cost: the footer carries the reported transcription cost ('{vm.Usage.UsageLabel}')");

            // a provider with no catalog and no reported cost: the manual settings fields price it
            settings.SpeechPricePerMinute = 12;
            settings.SoundPricePerMChars = 30;
            settings.Providers[0].BaseUrl = prefix + "manual/v1";
            settings.SoundUrl = prefix + "manual/audio/speech";
            await vm.Speak.ToggleCommand.ExecuteAsync(null);
            var manualTts = entry.Output.Length / 1_000_000d * 30;
            Check(Math.Abs(entry.Cost - (dictated + 0.004 + manualTts)) < 1e-9,
                $"speech cost: an unpublished render falls back to the manual per-M-chars price ({entry.Cost:0.000000})");
            Check(vm.Usage.UsageLabel == entry.UsageLabel,
                $"speech cost: the footer follows the entry after the manual-priced render ('{vm.Usage.UsageLabel}')");
            settings.SpeechUrl = prefix + "manual/audio/transcriptions";
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            await vm.Dictation.ToggleMicCommand.ExecuteAsync(null);
            var manualStt = Wav.Seconds(Wav.FromPcm(new byte[3200], 16000, 1, 16)) / 60d * 12;
            Check(Math.Abs(entry.Cost - (dictated + 0.004 + manualTts + manualStt)) < 1e-9,
                $"speech cost: dictation without a catalog falls back to the manual per-minute price ({entry.Cost:0.000000})");
            Check(vm.Usage.UsageLabel == entry.UsageLabel,
                $"speech cost: the footer follows the entry after the manual-priced dictation ('{vm.Usage.UsageLabel}')");

            // with no entry to sit on, the amount rides the next one
            vm.History.Current = null;
            coordinator.AddPendingCost(0.25);
            var next = coordinator.Commit("a", "b", "en", "de");
            Check(Math.Abs(next.Cost - 0.25) < 1e-9, $"speech cost: an entryless cost rides the next entry ({next.Cost})");
        });
    }

    /// <summary>Stop is the same button's second press: it must reach a playing clip and a running render.</summary>
    static async Task StopStopsEverything(string prefix, byte[] clip)
    {
        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SoundModel = "m", SoundVoice = "v",
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        using var http = new HttpClient();
        var provider = new OpenAiCompatibleProvider(http, settings);
        var blocker = new BlockingPlayer();
        var sharedHistory = new History(dir);
        var vm = new TranslateViewModel(new SettingsService(settings), sharedHistory,
            new TranslationCoordinator(provider, settings, sharedHistory), provider, new FakeRecorder(), blocker);
        try
        {
            vm.OutputText = "Hallo world";
            var playing = vm.Speak.ToggleCommand.ExecuteAsync(null);
            for (var i = 0; i < 100 && !blocker.IsPlaying; i++) await Task.Delay(50);
            Check(blocker.IsPlaying && vm.Speak.IsSpeaking, "sound stop: a clip is playing before the second press");
            Check(vm.Speak.ToggleCommand.CanExecute(null), "sound stop: the running press leaves the button clickable");

            await vm.Speak.ToggleCommand.ExecuteAsync(null); // the Stop press
            await playing;
            Check(blocker.Stopped && !blocker.IsPlaying, "sound stop: the playing clip stops");
            Check(!vm.Speak.IsSpeaking && vm.Status == "Playback stopped", $"sound stop: the button returns to Listen ('{vm.Status}')");

            // the same press during the render: the request is dropped and no clip is played
            var silent = new BlockingPlayer();
            settings.SoundUrl = prefix + "slow/audio/speech";
            settings.Providers[0].BaseUrl = prefix + "v1";
            var renderHistory = new History(dir + "b");
            var renderVm = new TranslateViewModel(new SettingsService(settings), renderHistory,
                new TranslationCoordinator(provider, settings, renderHistory), provider, new FakeRecorder(), silent);
            renderVm.OutputText = "Hallo world";
            var rendering = renderVm.Speak.ToggleCommand.ExecuteAsync(null);
            await Task.Delay(200); // the mock holds the answer for 600ms
            Check(!silent.IsPlaying && renderVm.Speak.SoundLabel == "Stop", "sound stop: the render phase shows Stop too");
            await renderVm.Speak.ToggleCommand.ExecuteAsync(null);
            await rendering;
            Check(!silent.IsPlaying && renderVm.Status == "Playback stopped" && !renderVm.IsError,
                $"sound stop: a render in flight is dropped, not played ('{renderVm.Status}')");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
            try { Directory.Delete(dir + "b", true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// A render's cost is billed to the entry that was current when the render started, not to whichever
    /// entry the panel shows when the answer finally arrives.
/// </summary>
    static async Task SpeechCostHitsCapturedEntry()
    {
        var port = 19620 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";
        var clip = Encoding.ASCII.GetBytes("ID3-fake-mp3-payload");


        // the render is held back so the selection switch lands while it is in flight
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = StartMock(prefix, async ctx =>
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var tts = ctx.Request.Url?.Query.Contains("type=tts", StringComparison.Ordinal) == true;
            string body;
            if (path.EndsWith("/audio/models", StringComparison.Ordinal))
                body = tts
                    ? "{\"models\":[{\"id\":\"tts-model\",\"pricing\":{\"billing_unit\":\"per_m_chars\",\"price_per_m_chars\":22}}]}"
                    : "{\"models\":[{\"id\":\"stt-model\",\"pricing\":{\"billing_unit\":\"per_minute\",\"price_per_minute\":6}}]}";
            else if (path.StartsWith("/slow/", StringComparison.Ordinal))
            {
                renderStarted.TrySetResult();
                await gate.Task;
                ctx.Response.ContentType = "audio/mpeg";
                await ctx.Response.OutputStream.WriteAsync(clip);
                ctx.Response.Close();
                return;
            }
            else body = "{\"data\":[{\"id\":\"mock\"}]}";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
            ctx.Response.Close();
        }, "speech cost: mock server start: ");
        if (listener is null) return;

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SoundModel = "tts-model", SoundVoice = "v", CacheSound = false,
            SoundUrl = prefix + "slow/audio/speech",
        };
        var home = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var previous = Environment.GetEnvironmentVariable("HAKATA_HOME");
        Environment.SetEnvironmentVariable("HAKATA_HOME", home);
        var dir = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n"));
        var history = new History(dir);
        var e1 = new HistoryEntry { Source = "Hallo", Output = "Hello", From = "en", To = "de" };
        var e2 = new HistoryEntry { Source = "Tag", Output = "Day", From = "en", To = "de" };
        history.Add(e1);
        history.Add(e2);
        using var http = new HttpClient();
        var vm = NewTranslateVm(settings, dir, http, history);
        try
        {
            vm.History.Current = e1;
            vm.OutputText = e1.Output;
            var speaking = vm.Speak.ToggleCommand.ExecuteAsync(null);
            for (var i = 0; i < 100 && !renderStarted.Task.IsCompleted; i++) await Task.Delay(20);
            Check(renderStarted.Task.IsCompleted, "speech cost: the render is in flight before the switch");

            vm.History.SelectedEntry = e2; // the panel moves to e2 while the render for e1 runs
            gate.TrySetResult();
            try { await speaking; Check(true, "speech cost: the render finishes without throwing"); }
            catch (Exception e) { Check(false, $"speech cost: the render threw ({e.Message})"); }
            Check(e1.Cost > 0, $"speech cost: the captured entry carries the render's cost ({e1.Cost})");
            Check(e2.Cost == 0, $"speech cost: the entry selected mid-render stays free ({e2.Cost})");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", previous);
            listener.Stop();
            try { Directory.Delete(home, true); } catch (Exception) { }
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>The clip the player decodes itself: whichever backend the OS gets — Media Foundation/ACM
    /// on Windows, libsndfile elsewhere — must read back the WAV the recorder wrote. Skipped out loud when
    /// the native library is absent, because that is a deployment fact, not an app bug.</summary>
    static Task AudioDecodeRoundTrip()
    {
        // exactly what the recorder produces: 16 kHz mono 16-bit PCM, 3200 bytes = 100 ms
        var wav = Wav.FromPcm(new byte[3200], MicrophoneRecorder.SampleRate, 1, 16);
        var path = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n") + ".wav");
        try
        {
            File.WriteAllBytes(path, wav);
            WaveStream reader;
            try { reader = SpeechPlayer.OpenAudio(path); }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
            {
                Check(true, "audio decode: skipped, no native decoder on this machine (" + e.GetType().Name + ")");
                return Task.CompletedTask;
            }
            using (reader)
            {
                // pinning the choice, not just the result: on Windows a WAV decodes with either backend
                Check(OperatingSystem.IsWindows() ? reader is AudioFileReader : reader is SoundFileReader,
                    $"audio decode: the {(OperatingSystem.IsWindows() ? "Windows" : "Linux")} decoder is the one in use ({reader.GetType().Name})");
                Check(reader.WaveFormat.SampleRate == MicrophoneRecorder.SampleRate && reader.WaveFormat.Channels == 1,
                    $"audio decode: the clip reads back as 16 kHz mono (got {reader.WaveFormat.SampleRate} Hz, {reader.WaveFormat.Channels} ch)");
                Check(Math.Abs(reader.TotalTime.TotalSeconds - 0.1) < 0.01,
                    $"audio decode: the clip's length survives the round trip ({reader.TotalTime.TotalSeconds:0.###} s)");
            }
            return Task.CompletedTask;
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }
}
