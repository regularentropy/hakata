using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Material.Icons;
using Xunit;

namespace Hakata;

/// <summary>Dependency-free checks for the parts that break silently: endpoints, SSE, prompts, JSON parsing, history.</summary>
public partial class SelfTest
{
    /// <summary>The two-provider settings both multi-provider tests build: vendor + openrouter on /v1.</summary>
    static SettingsService TwoProviderSettings(string vendorPrefix, string openrouterPrefix,
        string vendorModel = "", string openrouterModel = "", string openrouterApiKey = "") =>
        new(new AppSettings
        {
            Providers =
            [
                new ProviderConfig { Name = "vendor", BaseUrl = vendorPrefix + "/v1", Model = vendorModel },
                new ProviderConfig { Name = "openrouter", BaseUrl = openrouterPrefix + "/v1", Model = openrouterModel, ApiKey = openrouterApiKey },
            ],
        });

    /// <summary>The settings dialog doubles as first-run setup: a pasted link is all it takes.</summary>
    [AvaloniaFact]
    public async Task SetupCase()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var empty = new SettingsViewModel(new SettingsService(new AppSettings()), http);
        Assert.True(empty.IsFirstRun, "setup: no provider on file marks the dialog as first run");
        Assert.True(empty.WindowTitle.Contains("Set up"), "setup: first run renames the dialog");
        Assert.True(empty.CancelText.Contains("Later"), "setup: first run offers Later, not Cancel");
        Assert.True(empty.Providers.Rows.Count == 1 && empty.Providers.Rows[0].Name == "Provider 1"
              && string.IsNullOrEmpty(empty.Providers.Rows[0].BaseUrl),
            "setup: the dialog opens with one empty provider row instead of a bare list");
        empty.SaveCommand.Execute(null);
        Assert.True(empty.Error.Contains("Add a provider first"),
            $"setup: saving the empty row asks for a link instead of writing nothing ({empty.Error})");

        var port = 18900 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (path.StartsWith("/hang/", StringComparison.Ordinal))
                {
                    // never answered: the probe must fail on its own timeout, not wait this out
                    try { await Task.Delay(TimeSpan.FromSeconds(30)); ctx.Response.Abort(); } catch { }
                    continue;
                }
                if (path.EndsWith("/audio/models", StringComparison.Ordinal))
                {
                    // a provider with an audio catalog, and one without (a 404, e.g. OpenAI)
                    if (path.StartsWith("/plain/", StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }
                    ctx.Response.ContentType = "application/json";
                    // the tts half declares its voices and a default voice
                    var tts = ctx.Request.Url?.Query.Contains("type=tts", StringComparison.Ordinal) == true;
                    var body = tts
                        ? "{\"models\":[{\"id\":\"tts-model-x\",\"type\":\"tts\",\"params\":{\"tts\":{\"voices\":[\"voice-a\",\"voice-b\"],\"default_voice\":\"voice-b\"}}}]}"
                        : "{\"data\":[{\"id\":\"stt-one\",\"type\":\"stt\"}]}";
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                    ctx.Response.Close();
                    continue;
                }
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"model-z\"},{\"id\":\"model-a\"}]}");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        try
        {
            // a bare host and a full chat/completions link both have to end up on /models
            var link = prefix + "chat/completions";
            var vm = new SettingsViewModel(new SettingsService(new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = link }] }), http);
            Assert.True(!vm.IsFirstRun && vm.WindowTitle == "Settings", "setup: a provider on file keeps the plain settings title");
            Assert.True(vm.Providers.Rows.Count == 1 && vm.Providers.Rows[0].Name == "p",
                "setup: the provider on file shows up as the first row");
            await Task.Delay(700); // the removed ctor reload debounced at 400ms: a fetch on open would have landed by now
            Assert.True(vm.Providers.Rows[0].Models.Count == 0, "setup: the dialog opens without fetching the model list (#0081)");
            vm.Providers.RefreshModelsCommand.Execute(null);
            for (var i = 0; i < 100 && vm.Providers.Rows[0].Models.Count == 0; i++) await Task.Delay(100);
            Assert.True(vm.Providers.Rows[0].Models.Count == 2, "setup: Refresh loads the model listing for the connection on file");
            Assert.True(vm.Providers.Rows[0].Model == "model-a", "setup: an empty provider model field takes the first listed model");
            for (var i = 0; i < 100 && !vm.Providers.ModelStatus.Contains("Using"); i++) await Task.Delay(100);
            Assert.True(vm.Providers.ModelStatus.Contains("Using"), $"setup: the status says which model it picked ({vm.Providers.ModelStatus})");

            for (var i = 0; i < 100 && vm.Slots.SpeechModels.Count == 0; i++) await Task.Delay(100);
            Assert.True(vm.Slots.SpeechModels.Count == 1 && vm.Slots.SpeechModels[0] == "stt-one",
                "setup: the speech field is filled from the provider's audio model list");

            for (var i = 0; i < 100 && vm.Slots.SoundModels.Count == 0; i++) await Task.Delay(100);
            Assert.True(vm.Slots.SoundModels.Count == 1 && vm.Slots.SoundModels[0] == "tts-model-x",
                "setup: the sound field lists only text-to-speech models");
            Assert.True(vm.Slots.SoundModel == "tts-model-x", $"setup: an empty sound model takes the published one ({vm.Slots.SoundModel})");
            Assert.True(vm.SoundVoices.SequenceEqual(["voice-a", "voice-b"]),
                $"setup: the voice list comes from the provider ({string.Join(",", vm.SoundVoices)})");
            Assert.True(vm.SoundVoice == "voice-b", $"setup: the published default voice fills the empty field ({vm.SoundVoice})");

            // a provider with no audio catalog keeps the chat ids on offer instead of an empty picker
            var plain = new SettingsViewModel(new SettingsService(new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "plain" }] }), http);
            plain.Providers.RefreshModelsCommand.Execute(null);
            for (var i = 0; i < 100 && plain.Slots.SpeechModels.Count == 0; i++) await Task.Delay(100);
            Assert.True(plain.Slots.SpeechModels.Count == 2 && plain.Slots.SpeechModels[1] == "model-z",
                $"setup: no audio catalog falls back to the chat ids ({string.Join(",", plain.Slots.SpeechModels)})");
            Assert.True(plain.Slots.SoundModels.Count == 0 && plain.SoundVoices.Count > 0 && plain.SoundVoices[0] == "alloy",
                "setup: a provider that publishes no voices keeps the OpenAI names");
            Assert.True(plain.SoundVoice == "", "setup: no published default voice leaves the field empty");

            // a hanging endpoint must fail the probe on its own timeout instead of pinning the dialog;
            // the client here allows 2 minutes, so only the probe's own bound can fire within the poll
            using var hangHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var hang = new SettingsViewModel(new SettingsService(new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "hang" }] }), hangHttp);
            hang.Providers.TestConnectionCommand.Execute(null);
            for (var i = 0; i < 200 && !hang.Providers.ModelStatus.Contains("timed out"); i++) await Task.Delay(100);
            Assert.True(hang.Providers.ModelStatus.Contains("timed out"),
                $"setup: a hanging endpoint times the probe out ({hang.Providers.ModelStatus})");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Endpoints()
    {
        Assert.True(ProviderUrls.Endpoint("https://api.openai.com/v1") == "https://api.openai.com/v1/chat/completions", "endpoint: /v1 base");
        Assert.True(ProviderUrls.Endpoint("http://localhost:11434/") == "http://localhost:11434/v1/chat/completions", "endpoint: bare base");
        Assert.True(ProviderUrls.Endpoint("https://x/v1/chat/completions") == "https://x/v1/chat/completions", "endpoint: full path");
        Assert.True(ProviderUrls.Endpoint("https://api.vendor.example/v3/compat") == "https://api.vendor.example/v3/compat/chat/completions", "endpoint: /v3/compat base has a path, no /v1 inserted");
        Assert.True(ProviderUrls.Endpoint("https://api.vendor.example/") == "https://api.vendor.example/v1/chat/completions", "endpoint: bare host still gets /v1");
        Assert.True(ProviderUrls.Endpoint("https://gw/v1/acc/openrouter") == "https://gw/v1/acc/openrouter/chat/completions", "endpoint: mid-path /v1 gateway treated as full base");
        Assert.True(ProviderUrls.ModelsUrl("https://api.vendor.example/v3/compat") == "https://api.vendor.example/v3/compat/models", "models url: pathed base");
        Assert.True(ProviderUrls.ModelsUrl("http://localhost:11434") == "http://localhost:11434/v1/models", "models url: bare base");
        Assert.True(ProviderUrls.Endpoint("https://gw/v1?token=abc#f") == "https://gw/v1/chat/completions",
            "endpoint: a query and fragment on the base are dropped, the suffix lands on the path");
        var emptyBase = false;
        try { _ = ProviderUrls.Endpoint(""); } catch (HttpRequestException) { emptyBase = true; }
        Assert.True(emptyBase, "endpoint: an empty base URL fails as HttpRequestException, not a late InvalidOperationException");
        var blankBase = false;
        try { _ = ProviderUrls.ModelsUrl("   "); } catch (HttpRequestException) { blankBase = true; }
        Assert.True(blankBase, "endpoint: a blank base URL fails the same way on the models route");

        var intTier = false;
        try { _ = JsonSerializer.Deserialize<AppSettings>("{\"Tier\":99}", AppJson.Options); } catch (JsonException) { intTier = true; }
        Assert.True(intTier, "settings: a raw int enum value is rejected instead of loading as undefined");
        var clamped = new AppSettings { Tier = (QualityTier)99, SidebarWidth = 99999, WindowWidth = -5, WindowHeight = 0 };
        SettingsService.Normalize(clamped);
        Assert.True(clamped.Tier == QualityTier.Standard && clamped.SidebarWidth == AppSettings.MaxSidebarWidth
              && clamped.WindowWidth == 1040 && clamped.WindowHeight == 700,
            "settings: Normalize clamps an undefined tier and out-of-range geometry");

        Assert.True(Pricing.Cost(new TokenUsage(1_000_000, 500_000, Estimated: false, ReportedCost: 0), 10, 20) == 0,
            "pricing: a reported cost of 0 means the run was free, not billed via token*price");
        Assert.True(Pricing.Cost(new TokenUsage(1_000_000, 500_000, Estimated: false), 10, 20) == 20,
            "pricing: no reported cost still falls back to token*price");

        Assert.True(Jsons.TryParse("{\"a\":1}") is not null, "json slice: a matching closer parses");
        Assert.True(Jsons.TryParse("{\"a\":1]") is null, "json slice: a closer that does not match the opener is rejected");
        Assert.True(Jsons.TryParse("[1,2] trailing") is not null, "json slice: array opener still closes on ]");
        var dupes = new System.Collections.ObjectModel.ObservableCollection<string> { "x" };
        CollectionSync.InPlace(dupes, new List<string> { "x", "x" });
        Assert.True(dupes.Count == 2, "sync: value-equal duplicates are kept, not dropped");
        Assert.True(ProviderUrls.Endpoint("http://h:8080/chat/completions") == "http://h:8080/chat/completions",
            "endpoint: a root-level full endpoint is returned unchanged, no /v1 re-inserted");
        Assert.True(ProviderUrls.ModelsUrl("https://x?api_key=1") == "https://x/v1/models",
            "models url: a query on the base is dropped before the /v1 prefix");
        Assert.True(ProviderUrls.AudioUrl("https://x?api_key=1") == "https://x/v1/audio/transcriptions",
            "audio url: a query on the base is dropped before the /v1 prefix");
        var priceCatalog = new List<ModelListing>
        {
            new("gpt-4o-mini", 0.15, 0.6),
            new("free-model", null, 0.6),
        };
        Assert.True(CatalogApi.PublishedPriceFor(priceCatalog, "GPT-4o-Mini") is { } pp
              && pp.Input == 0.15 && pp.Output == 0.6, "autoprice: published pair found, case-insensitive");
        Assert.True(CatalogApi.PublishedPriceFor(priceCatalog, "free-model") is null, "autoprice: half-published pair ignored");
        Assert.True(CatalogApi.PublishedPriceFor(priceCatalog, "") is null
              && CatalogApi.PublishedPriceFor(priceCatalog, "unknown") is null, "autoprice: empty or unknown id yields nothing");
    }

    [Fact]
    public void CatalogEfforts()
    {
        var catalog = CatalogApi.ParseCatalog(
            "{\"data\":[" +
            "{\"id\":\"a\",\"reasoning\":{\"supported_efforts\":[\"max\",\"high\",\"low\"],\"default_effort\":\"high\"}," +
            " \"supported_parameters\":[\"reasoning_effort\"]}," +
            "{\"id\":\"b\",\"vendor\":{\"reasoning\":{\"supported\":[\"high\",\"low\"],\"default\":\"low\"}}}," +
            "{\"id\":\"c\",\"reasoning\":{\"mandatory\":true}}," +
            "{\"id\":\"d\",\"reasoning\":{\"supported_efforts\":[],\"supported_parameters\":[]}}]}");
        var a = catalog.First(m => m.Id == "a");
        Assert.True(a.Efforts!.SequenceEqual(["max", "high", "low"]) && a.DefaultEffort == "high",
            "models: OpenRouter supported_efforts keeps the provider's order and default");
        Assert.True(a.Parameters!.SequenceEqual(["reasoning_effort"]), "models: the accepted parameter names are parsed");
        var b = catalog.First(m => m.Id == "b");
        Assert.True(b.Efforts!.SequenceEqual(["high", "low"]) && b.DefaultEffort == "low" && b.Parameters is null,
            "models: a namespaced vendor effort list parses, and no parameter list is invented");
        Assert.True(catalog.First(m => m.Id == "c").Efforts is { Count: 0 } && catalog.First(m => m.Id == "c").DefaultEffort is null,
            "models: a model that publishes no effort selection gets an empty list");
        Assert.True(catalog.First(m => m.Id == "d").Efforts is { Count: 0 },
            "models: an empty effort array stays empty");
    }

    /// <summary>A file the app cannot read is kept beside its own name, never replaced by defaults.</summary>
    [Fact]
    public void UnreadableFiles()
    {
        WithTempHome(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
            var loaded = SettingsService.Load();
            Assert.True(loaded.Providers.Count == 0, "settings: a corrupt file loads as defaults");
            Assert.True(File.Exists(Path.Combine(dir, "settings.json.bad")) && !File.Exists(Path.Combine(dir, "settings.json")),
                "settings: the corrupt file is kept beside the original name");
            Assert.True(loaded.RecoveryNotice is { Length: > 0 }, "settings: the recovery is reported");

            // a settings file that is merely unreadable right now is not moved and not overwritten
            if (!OperatingSystem.IsWindows())
            {
                File.Move(Path.Combine(dir, "settings.json.bad"), Path.Combine(dir, "settings.json"));
                var deniedSettings = Path.Combine(dir, "settings.json");
                File.SetUnixFileMode(deniedSettings, UnixFileMode.None);
                try
                {
                    var denied = SettingsService.Load();
                    Assert.True(denied.PersistenceDisabled && File.Exists(deniedSettings) && !File.Exists(deniedSettings + ".bad"),
                        "settings: a denied file is left alone and writing is disabled");
                    new SettingsService(denied).Save();
                    Assert.True(File.GetUnixFileMode(deniedSettings) == UnixFileMode.None,
                        "settings: a disabled store is not overwritten by defaults");
                }
                finally
                {
                    File.SetUnixFileMode(deniedSettings, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            // a database this run cannot open is moved aside, so its rows survive the next save
            File.WriteAllText(Path.Combine(dir, "history.db"), "this is not a database");
            var history = new History(dir);
            history.Load();
            Assert.True(history.Entries.Count == 0, "history: an unreadable database loads empty");
            Assert.True(File.Exists(Path.Combine(dir, "history.db.unreadable")), "history: the unreadable database is kept");
            Assert.True(history.RecoveryNotice is not null && history.RecoveryNotice.Contains("history.db.unreadable"),
                "history: the recovery is reported");
            history.Add(new HistoryEntry { Source = "a", Output = "b", From = "en", To = "de" });
            var resumed = new History(dir);
            resumed.Load();
            Assert.True(resumed.Entries.Count == 1, $"history: writing resumes in a fresh database ({resumed.Entries.Count})");

            // a second corrupt store gets the next free name, never overwriting the first
            File.WriteAllText(Path.Combine(dir, "history.db"), "still not a database");
            var again = new History(dir);
            again.Load();
            Assert.True(File.Exists(Path.Combine(dir, "history.db.unreadable-2"))
                  && File.ReadAllText(Path.Combine(dir, "history.db.unreadable")) == "this is not a database",
                "history: a second unreadable store keeps the first recovery candidate");

            // a store that is merely unreadable right now (denied permissions) is not moved and not written
            if (!OperatingSystem.IsWindows())
            {
                var deniedDir = Path.Combine(dir, "denied");
                Directory.CreateDirectory(deniedDir);
                var deniedPath = Path.Combine(deniedDir, "history.db");
                File.WriteAllText(deniedPath, "not a database, and unreadable");
                File.SetUnixFileMode(deniedPath, UnixFileMode.None);
                try
                {
                    var denied = new History(deniedDir);
                    denied.Load();
                    Assert.True(File.Exists(deniedPath) && !File.Exists(deniedPath + ".unreadable"),
                        "history: a denied store is left at its name, nothing moved aside");
                    Assert.True(denied.RecoveryNotice is { Length: > 0 } && denied.RecoveryNotice.Contains("not saved"),
                        "history: a denied store reports that writing is disabled");
                    var deniedWrite = new History(deniedDir);
                    deniedWrite.Load();
                    deniedWrite.Add(new HistoryEntry { Source = "x", Output = "y" });
                    Assert.True(File.GetUnixFileMode(deniedPath) == UnixFileMode.None,
                        "history: a denied store is not overwritten by defaults");
                }
                finally
                {
                    File.SetUnixFileMode(deniedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            // a folder name carrying a ";" is a path, not extra connection keywords
            var odd = Path.Combine(dir, "home;weird");
            var oddHistory = new History(odd);
            oddHistory.Load();
            oddHistory.Add(new HistoryEntry { Source = "x", Output = "y" });
            var oddAgain = new History(odd);
            oddAgain.Load();
            Assert.True(oddAgain.Entries.Count == 1 && oddAgain.Entries[0].Source == "x",
                "history: a path with a semicolon is not read as connection keywords");
        });
    }

    /// <summary>Issue #0015: a settings file with null graphs loads usable instead of NRE-ing consumers.</summary>
    [Fact]
    public void SettingsNullGraphs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hakata-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var previous = Environment.GetEnvironmentVariable("HAKATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{\"providers\":[null,null],\"options\":null}");
            var loaded = SettingsService.Load();
            Assert.True(loaded.Providers is { Count: 0 }, "settings: null provider entries are dropped");
            Assert.True(loaded.Options is not null, "settings: null options become defaults");
            Assert.True(loaded.Route(QualityTier.Standard) is not null, "settings: Route works on the normalized settings");
            Assert.True(!loaded.HasProvider, "settings: HasProvider survives the normalization");

            var nulled = JsonSerializer.Deserialize<AppSettings>("{\"providers\":null,\"options\":null}", AppJson.Options)!;
            SettingsService.Normalize(nulled);
            Assert.True(nulled.Providers is { Count: 0 } && nulled.Options is not null,
                "settings: null graphs normalize to defaults");

            // field-level nulls: every string a consumer dereferences comes back usable
            var fields = JsonSerializer.Deserialize<AppSettings>(
                "{\"Providers\":[{\"Name\":null,\"BaseUrl\":null,\"ApiKey\":null,\"Model\":null}],"
                + "\"Options\":{\"Context\":null,\"Preset\":null,\"Presets\":[null]}}", AppJson.Options)!;
            SettingsService.Normalize(fields);
            var provider = fields.Providers[0];
            Assert.True(provider.Name == "" && provider.BaseUrl == "" && provider.Model == "" && provider.ApiKey == "",
                "settings: null provider fields normalize to empty strings");
            Assert.True(fields.Options.Context == "" && fields.Options.Preset == "" && fields.Options.Presets.Count == 0,
                "settings: null option fields normalize to empty strings");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", previous);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>One streamed run against a mock endpoint, to inspect the request the provider actually sends.</summary>
    [Fact]
    public async Task EffortPayload()
    {
        foreach (var (effort, expected) in new[] { ("high", true), ("", false) })
        {
            var (listener, baseUrl, bodies) = await StartMock("Hallo");
            try
            {
                var settings = new AppSettings { Providers = [new ProviderConfig { Name = "p", BaseUrl = baseUrl, Model = "mock" }], ReasoningEffort = effort };
                using var http = new HttpClient();
                var provider = new OpenAiCompatibleProvider(http, settings);
                await foreach (var _ in provider.TranslateAsync("hello", "en", "de", new TranslationOptions(),
                                   QualityTier.Standard, TestContext.Current.CancellationToken))
                {
                }
                var body = bodies[0];
                Assert.True(body.Contains("\"reasoning_effort\":\"" + effort + "\"") == expected,
                    $"effort: reasoning_effort sent only when set ({effort})");
                Assert.True(body.Contains("\"temperature\"") != expected, $"effort: temperature dropped with an effort ({effort})");
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    /// <summary>Deleting a preset is one accidental click away from the context box, so it asks first.</summary>
    [AvaloniaFact]
    public void PresetDeleteAsks()
    {
        WithTempHome(dir =>
        {
            var settings = new AppSettings();
            settings.Options.Presets.Add(new TranslationPreset { Name = "Minecraft Gaming", Context = "blocks and mobs" });
            var vm = new PresetsViewModel(new SettingsService(settings));
            vm.SelectedPreset = vm.Items[0];

            vm.DeletePresetCommand.Execute(null);
            Assert.True(vm.Items.Count == 1 && vm.DeleteArmed && vm.DeleteText == "Confirm?",
                "presets: delete asks before removing the preset");
            vm.DeletePresetCommand.Execute(null);
            Assert.True(vm.Items.Count == 0 && !vm.DeleteArmed, "presets: the second delete removes the preset");
        });
    }

    /// <summary>Selecting through the picker (the combo binds Items) must apply the preset, and the
    /// selection must survive a settings reload — Reload syncs Items in place, never with a Reset,
    /// because Avalonia clears a bound SelectedItem on a Reset.</summary>
    [AvaloniaFact]
    public void PresetPickerSelectionApplies()
    {
        var settings = new AppSettings();
        var gaming = new TranslationPreset { Name = "Gaming", Context = "in-game", Formality = Formality.Informal };
        var universal = new TranslationPreset { Name = "Universal", Context = "general" };
        settings.Options.Presets.AddRange([gaming, universal]);

        var vm = new PresetsViewModel(new SettingsService(settings));
        Assert.True(vm.Items.Count == 2 && vm.Items[0] == gaming, "presets: the picker lists presets in stored order");

        vm.SelectedPreset = vm.Items[1];
        Assert.True(settings.Options.Preset == universal.Id && settings.Options.ActivePreset == universal,
            "presets: picking a preset applies it and stores the id");

        vm.SelectedPreset = vm.Items[0];
        Assert.True(settings.Options.Preset == gaming.Id && settings.Options.Formality == Formality.Informal,
            "presets: picking another preset applies its options");
        var resets = 0;
        vm.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };
        vm.Reload();
        Assert.True(resets == 0, "presets: Reload never fires a Reset on the picker list");
        Assert.True(vm.SelectedPreset == gaming && settings.Options.ActivePreset == gaming,
            "presets: the selection survives a settings reload");
    }

    /// <summary>Presets are option bundles keyed by id: selecting applies them, the chrome sees real edits.</summary>
    [AvaloniaFact]
    public void PresetUpgradeCases()
    {
        WithTempHome(dir =>
        {

            // selecting a preset applies its five options and stores the id, not the name
            var settings = new AppSettings();
            var legal = new TranslationPreset
            {
                Name = "Legal contract",
                Context = "defined terms stay exact",
                Formality = Formality.Formal,
                Complexity = Complexity.Complex,
                Style = Style.Literal,
                SpeakerGender = Gender.Female,
                AddresseeGender = Gender.Male,
            };
            settings.Options.Presets.Add(legal);
            var applied = 0;
            var vm = new PresetsViewModel(new SettingsService(settings), () => applied++);
            vm.SelectedPreset = legal;
            Assert.True(settings.Options.Formality == Formality.Formal && settings.Options.Complexity == Complexity.Complex
                  && settings.Options.Style == Style.Literal && settings.Options.SpeakerGender == Gender.Female
                  && settings.Options.AddresseeGender == Gender.Male && settings.Options.Preset == legal.Id && applied == 1,
                "presets: selecting a preset applies its options and stores the id");

            // right after applying nothing is modified; a hand edit through the chrome changes that
            var chrome = new ChromeViewModel(new SettingsService(settings), _ => { }, () => { });
            Assert.True(!vm.IsModified, "presets: a freshly applied preset is not modified");
            var raised = 0;
            chrome.OptionsEdited += () => raised++;
            chrome.FormalityIndex = (int)settings.Options.Formality;
            Assert.True(raised == 0 && !vm.IsModified, "presets: a no-op option write raises nothing and stays unmodified");
            chrome.ComplexityIndex = (int)Complexity.Simple;
            Assert.True(raised == 1 && vm.IsModified, "presets: a real option change through the chrome marks the preset modified");

            // rename keeps the id and the selection; a duplicate name is refused without adding anything
            var idBefore = legal.Id;
            vm.NewPresetName = "Contract";
            vm.RenamePresetCommand.Execute(null);
            Assert.True(legal.Name == "Contract" && legal.Id == idBefore && vm.SelectedPreset == legal && vm.NewPresetName == "",
                "presets: rename keeps the id and the selection");
            var reported = "";
            var vm2 = new PresetsViewModel(new SettingsService(settings), null, m => reported = m);
            var other = new TranslationPreset { Name = "Other" };
            vm2.Items.Add(other);
            settings.Options.Presets.Add(other);
            vm2.SelectedPreset = legal;
            vm2.NewPresetName = "OTHER";
            vm2.RenamePresetCommand.Execute(null);
            Assert.True(reported == "A preset with that name already exists" && legal.Name == "Contract" && vm2.Items.Count == 2,
                "presets: a duplicate rename is refused without adding a preset");

            // adding a preset captures the live tone options, so selecting the new row cannot wipe them (#0078)
            var cap = new AppSettings { Options = { Formality = Formality.Formal, Complexity = Complexity.Complex,
                Style = Style.Creative, SpeakerGender = Gender.Female, AddresseeGender = Gender.Male } };
            var capVm = new PresetsViewModel(new SettingsService(cap));
            capVm.NewPresetName = "Snapshot";
            capVm.AddPresetCommand.Execute(null);
            var snapshot = capVm.SelectedPreset!;
            Assert.True(snapshot.Name == "Snapshot" && snapshot.Formality == Formality.Formal && snapshot.Complexity == Complexity.Complex
                  && snapshot.Style == Style.Creative && snapshot.SpeakerGender == Gender.Female && snapshot.AddresseeGender == Gender.Male
                  && cap.Options.Formality == Formality.Formal && cap.Options.Complexity == Complexity.Complex
                  && cap.Options.Style == Style.Creative && cap.Options.SpeakerGender == Gender.Female
                  && cap.Options.AddresseeGender == Gender.Male && !capVm.IsModified,
                "presets: add captures the live options and selecting the new preset changes nothing");

            // the Settings tab's list is management only: browsing it must not change the live options
            var manageSettings = new AppSettings { Options = { Formality = Formality.Informal } };
            var managePreset = new TranslationPreset { Name = "Formal one", Formality = Formality.Formal };
            manageSettings.Options.Presets.Add(managePreset);
            var manage = new PresetsViewModel(new SettingsService(manageSettings), null, null, manageOnly: true);
            manage.SelectedPreset = managePreset;
            Assert.True(manageSettings.Options.Formality == Formality.Informal && manageSettings.Options.Preset == "",
                "presets: the manage-only list selects without applying or activating");

            // applying a preset goes through the one write path, so the bundle is on disk without a window close
            var applySvc = new SettingsService(new AppSettings());
            applySvc.Save(); // the file exists from the start, so a missing save is a failed check, not a crash
            var formal = new TranslationPreset { Name = "Formal", Formality = Formality.Formal };
            applySvc.Settings.Options.Presets.Add(formal);
            var applyVm = new PresetsViewModel(applySvc);
            applyVm.SelectedPreset = formal;
            using (var applyDoc = JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath)))
            {
                var appliedOptions = applyDoc.RootElement.GetProperty("Options");
                Assert.True(appliedOptions.GetProperty("Formality").GetString() == "formal"
                      && appliedOptions.GetProperty("Preset").GetString() == formal.Id,
                    "presets: applying a preset saves the option bundle and its id to settings.json immediately");
            }

            // the context box writes through the same path
            var contextSvc = new SettingsService(new AppSettings());
            contextSvc.Save();
            var contextVm = new PresetsViewModel(contextSvc);
            contextVm.Context = "a museum placard";
            using (var contextDoc = JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath)))
            {
                Assert.True(contextDoc.RootElement.GetProperty("Options").GetProperty("Context").GetString() == "a museum placard",
                    "presets: a context edit is saved to settings.json immediately");
            }

            // a nameless import entry is skipped instead of landing as a blank row
            var importVm = new PresetsViewModel(new SettingsService(new AppSettings()));
            Assert.True(importVm.MergeJson("[{\"id\":\"x\"}]") == 0 && importVm.Items.Count == 0,
                "presets: importing an entry without a name adds no row");
            Assert.True(importVm.MergeJson("[{\"id\":\"y\",\"name\":\"   \"}]") == 0 && importVm.Items.Count == 0,
                "presets: importing a whitespace-only name adds no row");

            // presets are universal: no language pair is stored on them or their JSON
            var presetJson = mover0Json();
            Assert.True(!presetJson.Contains("\"from\"", StringComparison.OrdinalIgnoreCase)
                  && !presetJson.Contains("\"to\"", StringComparison.OrdinalIgnoreCase),
                "presets: preset JSON carries no From/To language fields");
            static string mover0Json()
            {
                var s = new AppSettings();
                s.Options.Presets.Add(new TranslationPreset { Name = "P", Context = "c" });
                return new PresetsViewModel(new SettingsService(s)).ToJson();
            }

            // move up/down reorders both lists and keeps the selection
            var msettings = new AppSettings();
            var a = new TranslationPreset { Name = "A" };
            var b = new TranslationPreset { Name = "B" };
            var c = new TranslationPreset { Name = "C" };
            msettings.Options.Presets.AddRange([a, b, c]);
            var mover = new PresetsViewModel(new SettingsService(msettings));
            mover.SelectedPreset = c;
            mover.MoveUpCommand.Execute(null);
            Assert.True(mover.Items.SequenceEqual(msettings.Options.Presets)
                  && mover.Items.Select(p => p.Name).SequenceEqual(["A", "C", "B"]) && mover.SelectedPreset == c,
                "presets: move up reorders both lists and keeps the selection");
            mover.MoveDownCommand.Execute(null);
            Assert.True(mover.Items.Select(p => p.Name).SequenceEqual(["A", "B", "C"]) && mover.SelectedPreset == c,
                "presets: move down restores the order and keeps the selection");

            // ToJson/MergeJson round-trip adds only new entries and returns the added count
            var export = mover.ToJson();
            var target = new PresetsViewModel(new SettingsService(new AppSettings()));
            var added = target.MergeJson(export);
            Assert.True(added == 3 && target.Items.Count == 3 && target.Items.All(p => p.Id.Length == 32),
                "presets: merge adds every new entry, gives ids, and returns the count");
            Assert.True(target.MergeJson(export) == 0 && target.Items.Count == 3,
                "presets: merging the same json again adds nothing");
            var collision = JsonSerializer.Serialize(new[] { new TranslationPreset { Name = "a" } }, AppJson.Options);
            Assert.True(target.MergeJson(collision) == 0 && target.Items.Count == 3,
                "presets: a name collision (ignore case) is not merged");
            Assert.True(target.MergeJson("{ not json") == 0, "presets: garbage json merges as zero");

            // a Preset that is not an id selects nothing; normalize clears the dangling value
            var named = new AppSettings();
            var namedPreset = new TranslationPreset { Name = "Minecraft Gaming", Context = "blocks and mobs" };
            named.Options.Presets.Add(namedPreset);
            named.Options.Preset = "Minecraft Gaming";
            Assert.True(named.Options.ActivePreset is null && named.Options.ActiveContext == "",
                "presets: a Preset that is not an id selects no preset");
            SettingsService.Normalize(named);
            Assert.True(named.Options.Preset == "" && named.Options.ActivePreset is null,
                "presets: normalize clears a preset value that matches no preset id");
        });
    }

    /// <summary>A fresh install gets the four starter presets; an existing file without presets gets none.</summary>
    [Fact]
    public void PresetStarterCases()
    {
        WithTempHome(dir =>
        {
            var fresh = SettingsService.Load(); // no settings.json exists yet
            var names = fresh.Options.Presets.Select(p => p.Name).ToList();
            Assert.True(fresh.Options.Presets.Count == 4
                  && names.Contains("In-game chat") && names.Contains("Legal contract")
                  && names.Contains("Academic abstract") && names.Contains("Customer support reply")
                  && fresh.Options.Presets.All(p => p.Id.Length == 32),
                "presets: a fresh install seeds the four starter presets with ids");
            var legal = fresh.Options.Presets.First(p => p.Name == "Legal contract");
            Assert.True(legal.Formality == Formality.Formal && legal.Complexity == Complexity.Complex && legal.Style == Style.Literal,
                "presets: the legal starter carries formal/complex/literal");

            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Options\":{\"Context\":\"a restaurant menu\"}}");
            var existing = SettingsService.Load();
            Assert.True(existing.Options.Presets.Count == 0 && existing.Options.ActiveContext == "a restaurant menu",
                "presets: an existing settings file without presets loads none");
        });
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        const string settingsJson = """
        {
          "Providers": [
            { "Name": "openai", "BaseUrl": "https://api.openai.com/v1", "ApiKey": "sk-test", "Model": "gpt-4o-mini" }
          ],
          "BasicModel": "",
          "StandardModel": "",
          "BestModel": "",
          "SourceLang": "en",
          "TargetLang": "de",
          "Theme": "dark",
          "OptionsVisible": true,
          "SidebarVisible": true,
          "Tier": "Best",
          "Options": {
            "Formality": "formal",
            "Complexity": "complex",
            "Style": "literal",
            "SpeakerGender": "female",
            "AddresseeGender": "unknown",
            "Context": "a restaurant menu"
          }
        }
        """;
        var s = JsonSerializer.Deserialize<AppSettings>(settingsJson, AppJson.Options)!;
        Assert.True(s.Theme == ThemeMode.Dark && s.Tier == QualityTier.Best, "settings: enums load");
        Assert.True(s.Options.Formality == Formality.Formal && s.Options.Complexity == Complexity.Complex &&
              s.Options.Style == Style.Literal && s.Options.SpeakerGender == Gender.Female &&
              s.Options.AddresseeGender == Gender.Unknown, "settings: option enum values load");
        Assert.True(s.Options.Context == "a restaurant menu" && s.OptionsVisible, "settings: fields stay intact");
        Assert.True(s.Options.Presets.Count == 0 && s.Options.ActiveContext == "a restaurant menu",
            "settings: options load without presets");
        Assert.True(s.Transliteration, "settings: a file without the field keeps transliteration on");
        Assert.True(s.TransliterationMaxChars == 400 && s.WantsTransliteration("short"),
            "settings: a file without the limit gets the default and transliterates short text");
        Assert.True(!s.WantsTransliteration(new string('あ', 401)) && s.WantsTransliteration(new string('あ', 400)),
            "settings: the character limit cuts off long sources");
        Assert.True(s.SpeechPricePerMinute == 0 && s.SoundPricePerMChars == 0,
              "settings: a file without the manual speech prices leaves them unset");
        s.Transliteration = false;
        Assert.True(!s.WantsTransliteration("short"), "settings: the switch off beats any limit");
        s.Transliteration = true;
        s.TransliterationMaxChars = 0;
        Assert.True(!s.WantsTransliteration("short"), "settings: a limit of zero turns transliteration off");
        s.TransliterationMaxChars = 400;

        s.SpeechPricePerMinute = 12;
        s.SoundPricePerMChars = 30;
        var mcPreset = new TranslationPreset { Name = "Minecraft Gaming", Context = "blocks and mobs" };
        s.Options.Presets.Add(mcPreset);
        s.Options.Preset = mcPreset.Id;

        var rewritten = JsonSerializer.Serialize(s, AppJson.Options);
        var back = JsonSerializer.Deserialize<AppSettings>(rewritten, AppJson.Options)!;
        Assert.True(back.Theme == ThemeMode.Dark && back.Options.Style == Style.Literal, "settings: enums round-trip");
        Assert.True(rewritten.Contains("\"Theme\": \"dark\"") && rewritten.Contains("\"Style\": \"literal\"")
              && rewritten.Contains("\"Tier\": \"best\""),
            "settings: enums written as lowercase tokens");
        Assert.True(back.Options.Presets.Count == 1 && back.Options.ActiveContext == "blocks and mobs",
            "settings: presets round-trip");
        Assert.True(back.SpeechPricePerMinute == 12 && back.SoundPricePerMChars == 30,
            "settings: the manual speech prices round-trip");

        var dir = Path.Combine(Path.GetTempPath(), "hakata-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), settingsJson);
        var previousHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", dir);
            var loaded = SettingsService.Load();
            Assert.True(loaded.Theme == ThemeMode.Dark && loaded.Options.Style == Style.Literal,
                "settings: settings.json loads from disk");
            Assert.True(loaded.Providers.Count == 1 && loaded.Providers[0].ApiKey == "sk-test"
                  && loaded.Providers[0].BaseUrl == "https://api.openai.com/v1" && loaded.Providers[0].Model == "gpt-4o-mini",
                "settings: the provider list loads from disk");

            loaded.Options.Style = Style.Creative;
            new SettingsService(loaded).Save();
            var saved = SettingsService.Load();
            Assert.True(saved.Options.Style == Style.Creative && saved.Options.Formality == Formality.Formal && saved.Tier == QualityTier.Best,
                "settings: SettingsService.Save round-trips");

            loaded.Transliteration = false;
            loaded.TransliterationMaxChars = 900;
            loaded.TransliterationOnSource = false;
            new SettingsService(loaded).Save();
            var switched = SettingsService.Load();
            Assert.True(!switched.Transliteration && switched.TransliterationMaxChars == 900 && !switched.TransliterationOnSource,
                "settings: transliteration switch, limit and source toggle survive a save");
            Assert.True(switched.SaveExtras, "settings: keeping extras in history defaults to on");
            loaded.SaveExtras = false;
            new SettingsService(loaded).Save();
            Assert.True(!SettingsService.Load().SaveExtras, "settings: the extras-in-history switch survives a save");
            Assert.True(!File.Exists(Path.Combine(dir, "settings.json.tmp")),
                "settings: an atomic save leaves no temp file");

            // off Windows the key file is created 0600 and the app-owned dir 0700
            if (!OperatingSystem.IsWindows())
            {
                Assert.True((File.GetUnixFileMode(Path.Combine(dir, "settings.json")) & UnixFileMode.UserRead | UnixFileMode.UserWrite) != 0
                      && (File.GetUnixFileMode(Path.Combine(dir, "settings.json")) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    "settings: settings.json is readable by its owner only");

                // a settings.json that arrived 0644 (copied in from elsewhere) is narrowed on load
                var wide = Path.Combine(dir, "settings.json");
                File.SetUnixFileMode(wide, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                SettingsService.Load();
                Assert.True((File.GetUnixFileMode(wide) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    "settings: a 0644 settings.json is narrowed to 0600 by Load");
            }

            // a crafted entry id may not walk out of the sound-cache folder
            var cache = new SoundCache(Path.Combine(dir, "sound-cache"));
            var key = SoundCache.Key("a", "b", "c", "d", "e");
            cache.Put("..\\evil", key, [1, 2], "audio/mpeg");
            var clips = Directory.GetFiles(Path.Combine(dir, "sound-cache"), "*.audio");
            var escaped = Directory.GetFiles(dir, "*.audio");
            Assert.True(clips.Length == 1 && escaped.Length == 0
                  && cache.Get("..\\evil", key) is { Audio: [1, 2] },
                "sound cache: an entry id with separators stays inside the folder");
            if (!OperatingSystem.IsWindows())
                Assert.True((File.GetUnixFileMode(clips[0]) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    "sound cache: clips are owner-only off Windows");

            // pruning is not a licence over the whole folder: a file the cache did not write stays
            var foreign = Path.Combine(dir, "sound-cache", "holiday.audio");
            File.WriteAllText(foreign, "not ours");
            cache.Prune([]);
            Assert.True(File.Exists(foreign) && cache.Get("..\\evil", key) is null,
                "sound cache: prune deletes only files it wrote");

            // the playback clip is written into the app dir owner-only, and a clip the decoder rejects is deleted, not left behind
            var player = new SpeechPlayer(loaded);
            try { player.Play([1, 2, 3], "audio/mpeg"); } catch (Exception) { /* no sound card in the harness */ }
            Assert.True(Directory.GetFiles(dir, "hakata-speech-*").Length == 0,
                "speech: a clip the decoder cannot open is deleted, not left in the app dir");
            if (!OperatingSystem.IsWindows())
            {
                // a decodable header plays (or fails at the device) but the file mode is already owner-only at creation
                var wav = Wav.FromPcm(new byte[3200], 16000, 1, 16);
                try { player.Play(wav, "audio/wav"); } catch (Exception) { }
                Assert.True(Directory.GetFiles(dir, "hakata-speech-*").Length == 0,
                    "speech: a playable clip is cleaned up after playback");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", previousHome);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>The key in settings.json is DPAPI ciphertext, and a plain key from an older file still loads.</summary>
    [Fact]
    public void KeyProtection()
    {
        Assert.True(KeyProtector.Unprotect("") == "" && KeyProtector.Unprotect("sk-plain") == "sk-plain",
            "keys: a plain key from an older file is read as it stands");
        if (!KeyProtector.Available)
        {
            Assert.True(KeyProtector.Protect("sk-test") == "sk-test",
                "keys: this platform has no DPAPI, so the key stays as it is");
            return;
        }

        var sealedKey = KeyProtector.Protect("sk-test");
        Assert.True(sealedKey.StartsWith("enc:v1:") && !sealedKey.Contains("sk-test"), "keys: a key is written encrypted");
        Assert.True(KeyProtector.Unprotect(sealedKey) == "sk-test", "keys: the encrypted key decrypts for its own user");
        Assert.True(KeyProtector.Unprotect("enc:v1:bm90IGEgY2lwaGVy") == "", "keys: a ciphertext from elsewhere yields no key");

        var config = new ProviderConfig { Name = "p", BaseUrl = "https://api.openai.com/v1", ApiKey = "sk-secret" };
        var json = JsonSerializer.Serialize(config, AppJson.Options);
        var back = JsonSerializer.Deserialize<ProviderConfig>(json, AppJson.Options)!;
        Assert.True(!json.Contains("sk-secret") && back.ApiKey == "sk-secret",
            "keys: settings.json holds ciphertext, the app holds the key");
    }

    /// <summary>Every slot can name its own provider; the route is what the provider actually sends to.</summary>
    [Fact]
    public void MultiProviderRoutes()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderConfig { Name = "Vendor", BaseUrl = "https://a.example/v1", ApiKey = "key-a", Model = "a-default" },
                new ProviderConfig { Name = "OpenRouter", BaseUrl = "https://api.openrouter.ai/api/v1", ApiKey = "key-b" },
            ],
            BasicProvider = "openrouter", // a stored name matches case-insensitively
            BasicModel = "cheap",
            StandardProvider = "OpenRouter",
            StandardModel = "mid",
            BestModel = "strong",
            SpeechProvider = "OpenRouter",
            SpeechModel = "whisper-large-v3",
            SoundProvider = "gone", // a name the list no longer holds
        };

        var basic = settings.Route(QualityTier.Basic);
        Assert.True(basic.BaseUrl == "https://api.openrouter.ai/api/v1" && basic.ApiKey == "key-b" && basic.Model == "cheap",
            "multi: the Basic tier reaches its own provider, key and model");
        var standard = settings.Route(QualityTier.Standard);
        Assert.True(standard.BaseUrl == settings.Providers[1].BaseUrl && standard.ApiKey == "key-b" && standard.Model == "mid",
            "multi: the Standard tier routes to the same provider with another model");
        var best = settings.Route(QualityTier.Best);
        Assert.True(best.BaseUrl == settings.Providers[0].BaseUrl && best.ApiKey == "key-a" && best.Model == "strong",
            "multi: a tier that names no provider uses the first one on the list, with its own model");

        var speech = settings.SpeechRoute;
        Assert.True(speech.BaseUrl == settings.Providers[1].BaseUrl && speech.ApiKey == "key-b" && settings.EffectiveSpeechModel == "whisper-large-v3",
            "multi: the mic uploads to the provider its slot names");
        var sound = settings.SoundRoute;
        Assert.True(sound.BaseUrl == settings.Providers[0].BaseUrl && sound.Model == "" && settings.EffectiveSoundModel == "tts-1",
            "multi: a provider name that is gone falls back to the first one, and speech never borrows the chat model");
        Assert.True(settings.Route(QualityTier.Standard).Model == "mid" && settings.Providers[0].Model == "a-default",
            "multi: each provider keeps its own default model");

        Assert.True(!new AppSettings().HasProvider, "settings: no provider on file is a first run");
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, AppJson.Options), AppJson.Options)!;
        Assert.True(back.Providers.Count == 2 && back.Providers[1].ApiKey == "key-b" && back.Providers[1].Name == "OpenRouter"
              && back.Providers[0].Model == "a-default" && back.BasicProvider == "openrouter" && back.SoundProvider == "gone",
            "settings: the provider list and the slots that name them round-trip");

        settings.Tier = QualityTier.Basic;
        Assert.True(settings.ProviderLabel == "OpenRouter · cheap", $"label: names the provider and the model of the tier ({settings.ProviderLabel})");
    }

    /// <summary>Two mock endpoints: each tier has to reach the provider it names, with its own model.</summary>
    [Fact]
    public async Task MultiProviderDispatch()
    {
        var (listenerA, baseA, bodiesA) = await StartMock("Hallo");
        var (listenerB, baseB, bodiesB) = await StartMock("Hallo", "Hallo"); // Basic and Standard both come here
        try
        {
            var settings = new AppSettings
            {
                Providers =
                [
                    new ProviderConfig { Name = "first", BaseUrl = baseA, ApiKey = "key-a", Model = "shared" },
                    new ProviderConfig { Name = "second", BaseUrl = baseB, ApiKey = "key-b" },
                ],
                BasicProvider = "second",
                BasicModel = "cheap",
                StandardProvider = "second",
                StandardModel = "mid",
                BestModel = "strong",
            };
            using var http = new HttpClient();
            var provider = new OpenAiCompatibleProvider(http, settings);
            await foreach (var _ in provider.TranslateAsync("one", "en", "de", new TranslationOptions(), QualityTier.Basic, TestContext.Current.CancellationToken)) { }
            await foreach (var _ in provider.TranslateAsync("two", "en", "de", new TranslationOptions(), QualityTier.Standard, TestContext.Current.CancellationToken)) { }
            await foreach (var _ in provider.TranslateAsync("three", "en", "de", new TranslationOptions(), QualityTier.Best, TestContext.Current.CancellationToken)) { }

            Assert.True(bodiesA.Count == 1 && bodiesA[0].Contains("\"model\":\"strong\""),
                "multi dispatch: the Best tier reaches the first provider with its own model");
            Assert.True(bodiesB.Count == 2 && bodiesB[0].Contains("\"model\":\"cheap\"") && bodiesB[1].Contains("\"model\":\"mid\""),
                $"multi dispatch: Basic and Standard reach the second provider, {bodiesB.Count} requests, one model each");
            Assert.True(!bodiesA.Any(b => b.Contains("\"model\":\"cheap\"")) && !bodiesB.Any(b => b.Contains("\"model\":\"strong\"")),
                "multi dispatch: no request leaks to the other provider");
        }
        finally
        {
            listenerA.Stop();
            listenerB.Stop();
        }
    }

    /// <summary>Two providers in one list: one picker per slot, and a pick sets provider and model together.</summary>
    [AvaloniaFact]
    public async Task SettingsMultiProvider()
    {
        var portA = 19700 + Random.Shared.Next(0, 100);
        var portB = 19800 + Random.Shared.Next(0, 100);
        // a-one exists on both, so it is the one id the pickers have to spell out
        using var listenerA = ModelsServer($"http://127.0.0.1:{portA}/", "[\"a-one\",\"a-two\"]");
        using var listenerB = ModelsServer($"http://127.0.0.1:{portB}/", "[\"a-one\",\"b-one\"]");
        var home = Path.Combine(Path.GetTempPath(), "hakata-test-" + Guid.NewGuid().ToString("n"));
        var previousHome = Environment.GetEnvironmentVariable("HAKATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", home);
            using var http = new HttpClient();
            var settings = TwoProviderSettings($"http://127.0.0.1:{portA}", $"http://127.0.0.1:{portB}",
                vendorModel: "a-one", openrouterApiKey: "kb");
            var vm = new SettingsViewModel(settings, http);
            Assert.True(vm.Providers.Rows.Count == 2 && vm.Providers.Rows[0].Name == "vendor" && vm.Providers.Rows[1].Name == "openrouter",
                "settings: every provider is a row of the one list, none of them favoured");
            vm.Providers.RefreshModelsCommand.Execute(null);

            for (var i = 0; i < 100 && vm.Providers.Rows[1].Models.Count == 0; i++) await Task.Delay(100);
            Assert.True(vm.Providers.Rows[0].Models.SequenceEqual(["a-one", "a-two"]) && vm.Providers.Rows[1].Models.SequenceEqual(["a-one", "b-one"]),
                $"settings: each row lists its own provider's ids ({string.Join(",", vm.Providers.Rows[0].Models)} / {string.Join(",", vm.Providers.Rows[1].Models)})");
            Assert.True(vm.Providers.Rows[0].Model == "a-one" && vm.Providers.Rows[1].Model == "a-one",
                "settings: an empty row model is filled from that provider's own listing");

            // one picker per slot; only the shared id carries a provider prefix
            for (var i = 0; i < 100 && vm.Slots.BasicModels.Count == 0; i++) await Task.Delay(100);
            Assert.True(vm.Slots.BasicModels.SequenceEqual(["vendor · a-one", "a-two", "openrouter · a-one", "b-one"]),
                $"settings: the tier picker holds every provider's ids, prefixing only the shared one ({string.Join(",", vm.Slots.BasicModels)})");
            Assert.True(vm.Slots.BasicModels.SequenceEqual(vm.Slots.SpeechModels), "settings: speech offers the same ids (no audio catalog on these)");
            Assert.True(vm.Slots.BasicModelText == "", "settings: an untouched slot shows nothing");

            vm.Slots.BasicModelText = "a-two"; // unique: no prefix needed
            Assert.True(vm.Slots.BasicProvider == "vendor" && vm.Slots.BasicModel == "a-two",
                "settings: a bare id goes to the provider that lists it");

            vm.Slots.BasicModelText = "b-one";
            Assert.True(vm.Slots.BasicProvider == "openrouter" && vm.Slots.BasicModel == "b-one",
                "settings: another bare id moves the slot to the provider that offers it");

            vm.Slots.StandardModelText = "openrouter · a-one"; // the shared id, spelled out
            Assert.True(vm.Slots.StandardProvider == "openrouter" && vm.Slots.StandardModel == "a-one",
                "settings: a prefixed shared id picks that provider");

            vm.Slots.StandardModelText = "a-one"; // shared, and the slot's own provider holds it
            Assert.True(vm.Slots.StandardProvider == "openrouter" && vm.Slots.StandardModel == "a-one",
                "settings: a bare shared id keeps the provider the slot already had");

            vm.Slots.BestModelText = "openrouter · hand-typed";
            Assert.True(vm.Slots.BestProvider == "openrouter" && vm.Slots.BestModel == "hand-typed",
                "settings: a typed 'provider · id' routes there, listed or not");

            vm.Slots.BestModelText = "unknown-id";
            Assert.True(vm.Slots.BestProvider == "openrouter" && vm.Slots.BestModel == "unknown-id",
                "settings: an unlisted id stays on the provider the slot already had");

            vm.Slots.BestModelText = "";
            Assert.True(vm.Slots.BestProvider == "" && vm.Slots.BestModel == "",
                "settings: clearing the field hands the slot back to the first provider");

            // a provider without a model would send an empty model id, so the dialog refuses it
            vm.Providers.Rows[1].Model = "";
            vm.Slots.BestModelText = "openrouter";
            vm.SaveCommand.Execute(null);
            Assert.True(vm.Error.Contains("Pick a model for the Best tier"), $"settings: a provider without a model is refused ({vm.Error})");
            vm.Providers.Rows[1].Model = "b-one";
            vm.Slots.BestModelText = "";
            vm.SaveCommand.Execute(null);
            Assert.True(settings.Settings.Providers.Count == 2 && settings.Settings.Providers[1].Model == "b-one"
                  && settings.Settings.Providers[1].ApiKey == "kb",
                "settings: the list is saved as the only provider surface");
            Assert.True(settings.Settings.Route(QualityTier.Basic).BaseUrl == $"http://127.0.0.1:{portB}/v1"
                  && settings.Settings.Route(QualityTier.Basic).Model == "b-one",
                "settings: the saved Basic tier routes to the provider it picked");
            Assert.True(settings.Settings.Route(QualityTier.Best).BaseUrl == $"http://127.0.0.1:{portA}/v1"
                  && settings.Settings.Route(QualityTier.Best).Model == "a-one",
                "settings: an empty slot falls back to the first provider and its model");

            // a slot naming a provider that was removed falls back to the first row
            settings.Settings.Providers = [new ProviderConfig { Name = "only", BaseUrl = $"http://127.0.0.1:{portA}/v1", Model = "a-one" }];
            var again = new SettingsViewModel(settings, http);
            again.Slots.StandardProvider = "openrouter"; // no such row any more
            again.SaveCommand.Execute(null);
            Assert.True(settings.Settings.StandardProvider == "", "settings: a stale provider name is dropped on save");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAKATA_HOME", previousHome);
            try { Directory.Delete(home, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// The settings routing defects of #0009: a provider switch rebuilds the effort lists, a slot
    /// naming a provider that is gone with no model of its own is refused on Save, and a catalog
    /// reload stashes the published price dialog-locally instead of mutating the live settings.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsRouting()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var settings = new SettingsService(new AppSettings
        {
            Providers =
            [
                new ProviderConfig { Name = "vendor", BaseUrl = "https://vendor.example/v1", Model = "shared" },
                new ProviderConfig { Name = "openrouter", BaseUrl = "https://openrouter.example/v1", ApiKey = "kb", Model = "shared" },
            ],
        });
        var vm = new SettingsViewModel(settings, http);
        vm.Providers.Row("vendor")!.ChatCatalog = [new ModelListing("shared", null, null, ["minimal", "high"])];
        vm.Providers.Row("openrouter")!.ChatCatalog = [new ModelListing("shared", null, null, ["low"])];

        vm.Slots.BasicProvider = "vendor";
        Assert.True(vm.Slots.BasicEfforts.Select(c => c.Value).SequenceEqual(["", "minimal", "high"]),
            "routing: the basic tier offers the efforts its own provider publishes");
        vm.Slots.BasicProvider = "openrouter"; // same model id, different provider
        Assert.True(vm.Slots.BasicEfforts.Select(c => c.Value).SequenceEqual(["", "low"]),
            "routing: switching providers with the same model rebuilds the effort list");

        // a slot naming a provider that is gone with no model of its own is refused on Save
        var ghost = new SettingsService(new AppSettings
        {
            Providers = [new ProviderConfig { Name = "only", BaseUrl = "https://only.example/v1", Model = "m" }],
        });
        var gvm = new SettingsViewModel(ghost, http);
        var closed = false;
        gvm.CloseRequested += (_, _) => closed = true;
        gvm.Slots.BasicProvider = "removed"; // renamed away; no row carries it any more
        gvm.SaveCommand.Execute(null);
        Assert.True(gvm.Error.Contains("Pick a model for the Basic tier"),
            $"routing: a slot naming a removed provider with an empty model is refused ({gvm.Error})");
        Assert.True(!closed, "routing: the refused save leaves the dialog open");

        // a catalog reload stashes the published price dialog-locally; the live prices move on Save
        var priced = new SettingsService(new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = "https://p.example/v1", Model = "m" }],
        });
        var pvm = new SettingsViewModel(priced, http);
        pvm.Providers.Row("p")!.ChatCatalog = [new ModelListing("m", 2, 8)];
        pvm.Slots.OnCatalogsLoaded(); // the listings answered
        Assert.True(priced.Settings.InputPricePerMTok == 0 && priced.Settings.OutputPricePerMTok == 0,
            "routing: a catalog reload leaves the live prices untouched before Save");
        Assert.True(pvm.Slots.HasPublishedPrices && pvm.Slots.PublishedInput == 2 && pvm.Slots.PublishedOutput == 8,
            "routing: the published price is stashed in the dialog");
        var usage = new UsageViewModel(priced, new EmptyCatalog()); // the footer VM on the same service, as MainWindow builds it
        var priceRaised = new List<string>();
        usage.PropertyChanged += (_, e) => priceRaised.Add(e.PropertyName!);
        pvm.SaveCommand.Execute(null);
        Assert.True(priced.Settings.InputPricePerMTok == 2 && priced.Settings.OutputPricePerMTok == 8,
            "routing: Save applies the stashed published price");
        Assert.True(priceRaised.Contains(nameof(UsageViewModel.InputPrice)) && priceRaised.Contains(nameof(UsageViewModel.OutputPrice)),
            "routing: the dialog's SetPrices raises PricesChanged, so the footer VM re-notifies InputPrice/OutputPrice with no RefreshPrices");

        // the user's typed prices win: a configured price is not overwritten by the listing
        var typed = new SettingsService(new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = "https://p.example/v1", Model = "m" }],
            InputPricePerMTok = 5,
        });
        var tvm = new SettingsViewModel(typed, http);
        tvm.Providers.Row("p")!.ChatCatalog = [new ModelListing("m", 2, 8)];
        tvm.Slots.OnCatalogsLoaded();
        var typedClosed = false;
        tvm.CloseRequested += (_, _) => typedClosed = true;
        tvm.SaveCommand.Execute(null);
        Assert.True(typed.Settings.InputPricePerMTok == 5 && typedClosed,
            "routing: Save keeps the user's typed price and closes the dialog");

        await ReloadAbortKeepsWalking(); // a row timeout must not abort the provider walk
        await ReloadSpinnerSurvivesSupersede(); // a superseded reload must not clear the spinner
        SaveFailureIsLiveButUnsaved(); // a failed save is live but unsaved, and republishes the label
    }

    /// <summary>Issue 5: a row whose /models hangs past its row timeout must not abort the walk — the
    /// audio-catalog calls then run on the already-expired row token, and that OperationCanceledException
    /// has to become a recorded failure, so later providers still load and OnCatalogsLoaded still runs.</summary>
    static async Task ReloadAbortKeepsWalking()
    {
        using var http = new HttpClient(new HangFirstProviderHandler());
        var vm = new SettingsViewModel(TwoProviderSettings("http://hang.test", "http://ok.test", "hang-model", "ok-model"), http);
        vm.Providers.TestConnectionCommand.Execute(null); // probe: the status names the failing provider
        // the hanging row burns its row timeout; the healthy row must still be walked afterwards
        for (var i = 0; i < 200 && vm.Providers.Rows[1].Models.Count == 0; i++) await Task.Delay(100);
        Assert.True(vm.Providers.Rows[1].Models.SequenceEqual(["ok-model"]),
            $"reload abort: a timed-out row does not abort the walk — the next provider still loads ({string.Join(",", vm.Providers.Rows[1].Models)})");
        for (var i = 0; i < 100 && vm.Slots.BasicModels.Count == 0; i++) await Task.Delay(100);
        Assert.True(vm.Slots.BasicModels.Contains("ok-model"),
            "reload abort: OnCatalogsLoaded ran and rebuilt the pickers from the surviving provider");
        for (var i = 0; i < 50 && !vm.Providers.ModelStatus.Contains("Connection failed"); i++) await Task.Delay(100);
        Assert.True(vm.Providers.ModelStatus.Contains("vendor: timed out"),
            $"reload abort: the status names the timed-out provider, not the generic timeout message ({vm.Providers.ModelStatus})");
    }

    /// <summary>Only the first provider hangs forever; the second answers one model and no audio catalog.</summary>
    sealed class HangFirstProviderHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "hang.test")
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); // never answers: the row timeout must fire
            if (request.RequestUri?.AbsolutePath.EndsWith("/audio/models", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.NotFound); // no audio catalog, like OpenAI
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"ok-model\"}]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Issue 6: a superseded reload must not clear ModelsLoading while the newer one still runs.</summary>
    static async Task ReloadSpinnerSurvivesSupersede()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new BlockingHandler(() => Interlocked.Increment(ref calls) == 1, gate));
        var vm = new SettingsViewModel(new SettingsService(new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = "http://block.test/v1", Model = "m" }],
        }), http);
        vm.Providers.TestConnectionCommand.Execute(null); // the first reload blocks inside the handler
        for (var i = 0; i < 100 && !vm.Providers.ModelsLoading; i++) await Task.Delay(50);
        Assert.True(vm.Providers.ModelsLoading, "spinner: the first reload sets ModelsLoading");
        vm.Providers.TestConnectionCommand.Execute(null); // supersedes the first reload
        await Task.Delay(500); // the cancelled first run reaches its finally in this window
        Assert.True(vm.Providers.ModelsLoading,
            "spinner: the superseded reload does not clear ModelsLoading while the newer one still runs");
        gate.SetResult(); // let the second reload's request answer
        for (var i = 0; i < 100 && vm.Providers.ModelsLoading; i++) await Task.Delay(100);
        Assert.True(!vm.Providers.ModelsLoading, "spinner: ModelsLoading clears once the newest reload finishes");
    }

    /// <summary>The first request holds until its own cancellation; later ones wait on the gate, then list one model.</summary>
    sealed class BlockingHandler(Func<bool> holdsFirst, TaskCompletionSource gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (holdsFirst())
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (request.RequestUri?.AbsolutePath.EndsWith("/audio/models", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"m\"}]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Issue 7: a save that fails after the live settings were mutated must say the new config is
    /// live but unsaved, and must republish the header model label like a successful write does.</summary>
    static void SaveFailureIsLiveButUnsaved() => WithTempHome(dir =>
    {
        // a directory where settings.json should be makes the atomic rename fail
        Directory.CreateDirectory(Path.Combine(dir, "settings.json"));
        var svc = new SettingsService(new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = "https://p.example/v1", Model = "m" }],
        });
        using var http = new HttpClient();
        var vm = new SettingsViewModel(svc, http);
        vm.Providers.Row("p")!.ChatCatalog = [new ModelListing("m", 2, 8)];
        vm.Slots.OnCatalogsLoaded(); // stashes a published price, so Save writes prices before the file write
        var republished = 0;
        vm.LabelRefreshRequested += () => republished++;
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.SaveCommand.Execute(null);
        Assert.True(vm.Error.Contains("Running on the new settings, but saving failed"),
            $"save failure: the error says the new config is live but not saved ({vm.Error})");
        Assert.True(republished == 1, "save failure: the header model label republish was attempted");
        Assert.True(!closed, "save failure: the failed save leaves the dialog open");
        Assert.True(svc.Settings.InputPricePerMTok == 2 && svc.Settings.Providers.Count == 1,
            "save failure: the live settings carry the new values even though the disk write failed");
    });

    /// <summary>A catalog that publishes nothing, so nothing auto-prices in these checks.</summary>
    sealed class EmptyCatalog : IModelCatalog
    {
        public Task<List<ModelListing>> ListModelCatalogAsync(ProviderRoute route, CancellationToken ct = default) =>
            Task.FromResult<List<ModelListing>>([]);
    }

    /// <summary>#0068/#0069: one write path for the price pair — SettingsService.SetPrices applies, saves and
    /// raises PricesChanged, so a footer edit lands on disk and a write from another party (the auto-fill)
    /// still reaches the bindings; the old setter guard compared against a value the model did not own.</summary>
    [AvaloniaFact]
    public void PriceWritePath() => WithTempHome(_ =>
    {
        var svc = new SettingsService(new AppSettings()); // no providers, so nothing auto-prices
        var usage = new UsageViewModel(svc, new EmptyCatalog());
        var raised = new List<string>();
        usage.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        usage.InputPrice = 7;
        Assert.True(raised.Contains(nameof(UsageViewModel.InputPrice)) && svc.Settings.InputPricePerMTok == 7,
            "prices: a footer edit applies to the settings and notifies the binding");
        Assert.True(File.Exists(AppSettings.FilePath) && File.ReadAllText(AppSettings.FilePath).Contains("\"InputPricePerMTok\": 7"),
            "#0068: the footer price edit is saved to settings.json, not just held in memory");

        raised.Clear();
        usage.OutputPrice = 21;
        Assert.True(raised.Contains(nameof(UsageViewModel.OutputPrice)) && svc.Settings.OutputPricePerMTok == 21,
            "prices: the output price edit applies and notifies");

        // #0069: a price written by another party must reach the bindings even though the view model
        // never saw the write happen
        var fresh = new SettingsService(new AppSettings());
        var usage2 = new UsageViewModel(fresh, new EmptyCatalog());
        var raised2 = new List<string>();
        usage2.PropertyChanged += (_, e) => raised2.Add(e.PropertyName!);
        fresh.SetPrices(3, 15, onlyIfUntouched: true);
        Assert.True(raised2.Contains(nameof(UsageViewModel.InputPrice)) && raised2.Contains(nameof(UsageViewModel.OutputPrice))
              && fresh.Settings.InputPricePerMTok == 3 && fresh.Settings.OutputPricePerMTok == 15,
            "#0069: a price written outside the view model still raises PropertyChanged for both prices");

        // a pair the user typed wins over a published one
        raised2.Clear();
        fresh.SetPrices(2, 8, onlyIfUntouched: true);
        Assert.True(fresh.Settings.InputPricePerMTok == 3 && fresh.Settings.OutputPricePerMTok == 15
              && !raised2.Contains(nameof(UsageViewModel.InputPrice)) && !raised2.Contains(nameof(UsageViewModel.OutputPrice)),
            "prices: onlyIfUntouched keeps the typed pair and raises nothing");

        // the footer re-prices the last run when a price lands
        usage.SetUsage(new TokenUsage(1_000_000, 500_000, Estimated: false));
        Assert.True(usage.UsageLabel.EndsWith("~$17.5000"),
            $"prices: the footer prices the last run from the live prices ('{usage.UsageLabel}')");
        usage.InputPrice = 1;
        Assert.True(usage.UsageLabel.EndsWith("~$11.5000"),
            $"prices: a price write re-prices the last run's label ('{usage.UsageLabel}')");
    });

    [AvaloniaFact]
    public async Task ChromePersists()
    {
        WithTempHome(_ =>
        {
            var failuresSeen = new List<string>();
            var svc = new SettingsService(new AppSettings());
            var chrome = new ChromeViewModel(svc, status => failuresSeen.Add(status), () => { });

            chrome.ToggleThemeCommand.Execute(null);
            chrome.SelectBestTierCommand.Execute(null);
            chrome.ToggleSidebarCommand.Execute(null);

            using var doc = JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("Theme").GetString() == "light",
                "chrome: the theme toggle is saved to settings.json without a window close");
            Assert.True(root.GetProperty("Tier").GetString() == "best",
                "chrome: a tier switch is saved to settings.json without a window close");
            Assert.True(!root.GetProperty("SidebarVisible").GetBoolean() && failuresSeen.Count == 0,
                "chrome: the sidebar toggle is saved and no save error was reported");

            // the tone pills and the gender combos go through the same one write path, so they are on
            // disk immediately as well, not only when the window closes
            chrome.SetFormalityCommand.Execute("1"); // the formal pill
            chrome.SetStyleCommand.Execute("2"); // the creative pill
            chrome.SpeakerIndex = (int)Gender.Female;
            chrome.AddresseeIndex = (int)Gender.Male;
            using var doc2 = JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath));
            var options = doc2.RootElement.GetProperty("Options");
            Assert.True(options.GetProperty("Formality").GetString() == "formal"
                  && options.GetProperty("Style").GetString() == "creative"
                  && options.GetProperty("SpeakerGender").GetString() == "female"
                  && options.GetProperty("AddresseeGender").GetString() == "male"
                  && failuresSeen.Count == 0,
                "chrome: a tone pill or gender change is saved to settings.json immediately, without a window close");
        });
        // hosted here so the fixture needs no extra entry point
        await StarFollowsFreshRun();
        await UpdateCheckOpensOnce();
    }

    /// <summary>Each provider publishes its own effort lists; a tier reads only the list of the model it sends to.</summary>
    [AvaloniaFact]
    public async Task PerProviderEfforts()
    {
        var portA = 19900 + Random.Shared.Next(0, 40);
        var portB = 19950 + Random.Shared.Next(0, 40);
        // a namespaced shape: the list sits one level down, under vendor.reasoning.supported
        using var vendor = CatalogServer($"http://127.0.0.1:{portA}/",
            "{\"data\":[{\"id\":\"shared/gpt-5\",\"vendor\":{\"reasoning\":{\"supported\":[\"high\",\"medium\",\"low\"],\"default\":\"medium\"}}}]}");
        // OpenRouter's shape: reasoning.supported_efforts, highest first, plus default_effort
        using var openrouter = CatalogServer($"http://127.0.0.1:{portB}/",
            "{\"data\":[{\"id\":\"shared/gpt-5\",\"supported_parameters\":[\"reasoning_effort\",\"include_reasoning\"],"
            + "\"reasoning\":{\"supported_efforts\":[\"max\",\"xhigh\",\"high\"],\"default_effort\":\"high\"}},"
            + "{\"id\":\"only/here\",\"reasoning\":{\"supported_efforts\":[\"minimal\"]}},"
            // an object-form reasoning model: it takes efforts, but not as reasoning_effort
            + "{\"id\":\"object/only\",\"supported_parameters\":[\"reasoning\"],"
            + "\"reasoning\":{\"supported_efforts\":[\"high\",\"low\"]}}]}");
        try
        {
            using var http = new HttpClient();
            var settings = TwoProviderSettings($"http://127.0.0.1:{portA}", $"http://127.0.0.1:{portB}",
                "shared/gpt-5", "only/here");
            var vm = new SettingsViewModel(settings, http);
            vm.Providers.RefreshModelsCommand.Execute(null);
            for (var i = 0; i < 100 && (vm.Providers.Rows[0].ChatCatalog.Count == 0 || vm.Providers.Rows[1].ChatCatalog.Count == 0); i++)
                await Task.Delay(100);

            // both providers publish "shared/gpt-5" with different lists: the tier must read its own provider's
            Assert.True(vm.Providers.Rows[0].ChatCatalog.Count == 1 && vm.Providers.Rows[1].ChatCatalog.Count == 3,
                $"efforts: every provider keeps its own catalog instead of one merged by model id "
                + $"({vm.Providers.Rows[0].ChatCatalog.Count}/{vm.Providers.Rows[1].ChatCatalog.Count})");

            vm.Slots.BasicProvider = "vendor";
            vm.Slots.BasicModel = "shared/gpt-5";
            vm.Slots.StandardProvider = "openrouter";
            vm.Slots.StandardModel = "shared/gpt-5";
            for (var i = 0; i < 50 && !vm.Slots.BasicEfforts.Any(c => c.Value == "low"); i++) await Task.Delay(100);

            Assert.True(vm.Slots.BasicEfforts.Select(c => c.Value).SequenceEqual(["", "high", "medium", "low"]),
                $"efforts: the Basic tier offers the vendor's list for its model ({string.Join(",", vm.Slots.BasicEfforts.Select(c => c.Value))})");
            Assert.True(vm.Slots.StandardEfforts.Select(c => c.Value).SequenceEqual(["", "max", "xhigh", "high"]),
                $"efforts: the Standard tier offers OpenRouter's list for the same model id ({string.Join(",", vm.Slots.StandardEfforts.Select(c => c.Value))})");
            Assert.True(vm.Slots.BestEfforts.Select(c => c.Value).SequenceEqual(["", "high", "medium", "low"]),
                $"efforts: an empty Best slot takes the first provider and its list ({string.Join(",", vm.Slots.BestEfforts.Select(c => c.Value))})");

            // the shared choice is their union, so any tier can be given any effort the setup knows
            var shared = vm.Slots.EffortChoices.Select(c => c.Value).ToList();
            Assert.True(shared.Contains("low") && shared.Contains("xhigh") && shared.Contains("minimal"),
                $"efforts: the all-tiers list is the union across providers ({string.Join(",", shared)})");

            // the provider published a parameter list without reasoning_effort: the model would ignore it
            vm.Slots.StandardModel = "object/only";
            Assert.True(vm.Slots.StandardEfforts.Select(c => c.Value).SequenceEqual([""]),
                $"efforts: a model that does not accept reasoning_effort offers only the provider default ({string.Join(",", vm.Slots.StandardEfforts.Select(c => c.Value))})");
            vm.Slots.StandardModel = "shared/gpt-5";

            // a model whose provider publishes nothing falls back to the common vocabulary
            vm.Slots.BestProvider = "openrouter";
            vm.Slots.BestModel = "unlisted-model";
            await Task.Delay(50);
            Assert.True(vm.Slots.BestEfforts.Select(c => c.Value).Contains("none") && vm.Slots.BestEfforts.Count > 4,
                "efforts: a model no listing describes falls back to the common names");

        }
        finally
        {
            vendor.Stop();
            openrouter.Stop();
        }
    }

    /// <summary>A /models endpoint answering one fixed body; every other route is a 404.</summary>
    static HttpListener CatalogServer(string prefix, string json)
    {
        return StartMock(prefix, async ctx =>
        {
            try
            {
                ctx.Response.StatusCode = 404;
                if (ctx.Request.Url?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
                }
            }
            catch (Exception) { /* the client went away */ }
            finally { ctx.Response.Close(); }
        });
    }

    /// <summary>A /models endpoint that answers with the given ids; every other route is a 404.</summary>
    static HttpListener ModelsServer(string prefix, string jsonIds)
    {
        return StartMock(prefix, async ctx =>
        {
            try
            {
                ctx.Response.StatusCode = 404; // no audio catalog on this one
                if (ctx.Request.Url?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":" + jsonIds + "}"));
                }
            }
            catch (Exception) { /* the client went away */ }
            finally { ctx.Response.Close(); }
        });
    }

    [Fact]
    public async Task ModelsListing()
    {
        var port = 18680 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        string? seenAuth = null;
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var calls = 0;
        _ = Task.Run(async () =>
        {
            while (calls < 3)
            {
                var ctx = await listener.GetContextAsync();
                seenAuth = ctx.Request.Headers["Authorization"];
                ctx.Response.ContentType = "application/json";
                if (calls == 2) ctx.Response.StatusCode = 401;
                var payload = calls switch
                {
                    0 => "{\"data\":[{\"id\":\"model-z\",\"pricing\":{\"prompt\":\"0.000003\",\"completion\":\"0.000015\"},\"vendor\":{\"reasoning\":{\"supported\":[\"max\",\"xhigh\",\"high\",\"medium\",\"low\",\"minimal\"],\"default\":\"medium\"}}},{\"id\":\"model-a\",\"reasoning\":{\"supported_efforts\":[\"high\",\"low\"]}},{\"id\":\"model-a\"}]}",
                    1 => "total garbage not json",
                    _ => "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}",
                };
                var bytes = Encoding.UTF8.GetBytes(payload);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                calls++;
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            using var http = new HttpClient();
            var catalog = await CatalogApi.ListModelCatalogAsync(http, prefix + "v3/compat", "test-key", TestContext.Current.CancellationToken);
            Assert.True(seenAuth == "Bearer test-key", "models: key sent as bearer");
            Assert.True(catalog.Count == 2 && catalog[0].Id == "model-a" && catalog[1].Id == "model-z", "models: data shape parsed, sorted, deduped");
            Assert.True(catalog[1].PromptPerMTok is { } prompt && Math.Abs(prompt - 3) < 0.001 &&
                  catalog[1].CompletionPerMTok is { } completion && Math.Abs(completion - 15) < 0.001,
                "models: published per-token prices become per-1M prices");
            Assert.True(catalog[0].PromptPerMTok is null, "models: a model without pricing stays unpriced");
            Assert.True(catalog[1].Efforts is ["max", "xhigh", "high", "medium", "low", "minimal"] &&
                  catalog[1].DefaultEffort == "medium", "models: a namespaced reasoning effort list is parsed");
            Assert.True(catalog[0].Efforts is ["high", "low"], "models: openrouter supported_efforts parsed");
            var none = await CatalogApi.ListModelsAsync(http, prefix + "v3/compat", null, TestContext.Current.CancellationToken);
            Assert.True(none.Count == 0, "models: garbage response yields empty list");
            try
            {
                await CatalogApi.ListModelsAsync(http, prefix + "v3/compat", "bad-key", TestContext.Current.CancellationToken);
                Assert.Fail("models: 401 surfaces as an error");
            }
            catch (HttpRequestException ex)
            {
                Assert.True(ex.Message.Contains("Incorrect API key provided"),
                    $"models: 401 surfaces the provider's message ({ex.Message})");
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [AvaloniaFact]
    public async Task SettingsEffortOptions()
    {
        var port = 19380 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = StartMock(prefix, async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            const string body = "{\"data\":[{\"id\":\"openai/gpt-5-mini\",\"vendor\":{\"reasoning\":{\"supported\":[\"max\",\"high\",\"medium\",\"low\",\"minimal\"],\"default\":\"medium\"}}}]}";
            var bytes = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });
        try
        {
            using var http = new HttpClient();
            var settings = new SettingsService(new AppSettings
            {
                Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v3/compat", Model = "openai/gpt-5-mini" }],
                StandardEffort = "ludicrous", // a stored value the provider never listed
            });
            var vm = new SettingsViewModel(settings, http);
            vm.Providers.RefreshModelsCommand.Execute(null);
            // the fallback vocabulary contains xhigh; the provider's five-value list does not
            for (var i = 0; i < 100 && vm.Slots.EffortChoices.Any(c => c.Value == "xhigh"); i++) await Task.Delay(100);

            var labels = vm.Slots.StandardEfforts.Select(c => c.Label).ToList();
            Assert.True(labels[0] == "provider default", "effort dropdown: an empty choice means the provider default");
            Assert.True(labels.Contains("max") && labels.Contains("minimal"), "effort dropdown: takes the provider's own list");
            Assert.True(!labels.Contains("xhigh"), "effort dropdown: offers nothing the provider did not list");
            Assert.True(labels.Contains("ludicrous"), "effort dropdown: a stored value the provider dropped stays selectable");
            Assert.True(vm.Slots.EffortHint.Contains("its own provider publishes"), "effort dropdown: says where the values came from");

            Assert.True(vm.Slots.StandardEffortChoice.Value == "ludicrous", "effort dropdown: selection follows the stored value");
            vm.Slots.StandardEffortChoice = vm.Slots.StandardEfforts.First(c => c.Value == "high");
            Assert.True(vm.Slots.StandardEffort == "high", "effort dropdown: picking a value fills the field");
            vm.SaveCommand.Execute(null);
            Assert.True(settings.Settings.StandardEffort == "high", "effort dropdown: save keeps the picked effort");
            vm.Slots.StandardEffortChoice = vm.Slots.StandardEfforts[0];
            vm.SaveCommand.Execute(null);
            Assert.True(settings.Settings.StandardEffort == "", "effort dropdown: picking the default clears the setting");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>The header model switcher: the menu lists the current tier's provider models with the
    /// active one marked, picking writes the per-tier override through the one save path, and an empty
    /// pick returns the tier to the provider's default.</summary>
    [AvaloniaFact]
    public async Task ModelSwitcher()
    {
        var port = 19760 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        using var server = ModelsServer(prefix, "[{\"id\":\"model-a\"},{\"id\":\"model-b\"}]");
        await WithTempHomeAsync(async dir =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var bare = NewTranslateVm(new AppSettings(), dir, http);
            Assert.True(bare.ModelName == "no provider connected",
                "model menu: with no provider the header button carries the first-run hint");
            await bare.LoadModelMenuAsync();
            Assert.True(bare.ModelChoices.Count == 0 && bare.ModelMenuStatus.Contains("no provider"),
                "model menu: with no provider the menu offers only its Settings row");

            var settings = new AppSettings
            {
                Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "model-a" }],
            };
            var vm = NewTranslateVm(settings, dir, http);
            Assert.True(vm.ModelName == "model-a", "model menu: the header shows the provider default when no override is set");
            var raised = new List<string>(); // #0074: the header button needs the notification, not just the new value
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            await vm.LoadModelMenuAsync();
            Assert.True(vm.ModelChoices.Count == 1 && vm.ModelChoices[0].Id == "" && vm.ModelChoices[0].IsCurrent,
                "model menu: only user-added models are offered — here just the provider default, marked (no /models listing)");

            vm.PickModel("model-b");
            Assert.True(settings.StandardModel == "model-b" && settings.Route(QualityTier.Standard).Model == "model-b"
                && vm.ModelName == "model-b",
                "model menu: picking a model sets the current tier's override and republishes the header label");
            Assert.True(raised.Contains(nameof(TranslateViewModel.ModelName)),
                "model menu: picking raises PropertyChanged(ModelName), so the header button repaints (#0074)");
            Assert.True(vm.ModelChoices.Single(c => c.IsCurrent).Id == "model-b",
                "model menu: the picked row is the marked one");
            Assert.True(SettingsService.Load().StandardModel == "model-b",
                "model menu: the pick goes through Mutate, so it lands in settings.json");

            vm.Chrome.SelectBestTierCommand.Execute(null);
            Assert.True(raised.Contains(nameof(TranslateViewModel.ModelName)),
                "model menu: a tier switch raises PropertyChanged(ModelName) (#0074)");
            vm.PickModel("model-a");
            Assert.True(settings.BestModel == "model-a" && settings.StandardModel == "model-b",
                "model menu: another tier gets its own override, the first tier keeps its pick");

            vm.PickModel("");
            Assert.True(settings.BestModel == "" && settings.Route(QualityTier.Best).Model == "model-a" && vm.ModelName == "model-a",
                "model menu: an empty pick clears the override and the provider default is sent again");
        });
    }

    /// <summary>Per-provider extra models: hand-entered ids offered in the slot pickers beside the /models
    /// listing, persisted on save, and listed by the header switcher without contacting the provider.</summary>
    [AvaloniaFact]
    public async Task ExtraModelsOffered()
    {
        var port = 19900 + Random.Shared.Next(0, 90);
        var prefix = $"http://127.0.0.1:{port}/";
        using var server = ModelsServer(prefix, "[{\"id\":\"model-a\"},{\"id\":\"model-b\"}]");
        await WithTempHomeAsync(async dir =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var settingsService = new SettingsService(new AppSettings
            {
                Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", Model = "model-a" }],
            });
            var vm = new SettingsViewModel(settingsService, http);
            var row = vm.Providers.Rows[0];

            // (a) the pickers union the listing with the deduped extras
            row.ExtraModels.Add(new ProviderRow.ExtraModelRow(row, "model-x"));
            row.ExtraModels.Add(new ProviderRow.ExtraModelRow(row, "model-y"));
            row.ExtraModels.Add(new ProviderRow.ExtraModelRow(row, ""));
            row.ExtraModels.Add(new ProviderRow.ExtraModelRow(row, "MODEL-X"));
            Assert.True(row.AllChatIds.Contains("model-x"),
                "extra models: adding a row invalidates the cached id list via CollectionChanged (#0075/#0077)");
            vm.Providers.RefreshModelsCommand.Execute(null);
            for (var i = 0; i < 100 && row.Models.Count == 0; i++) await Task.Delay(100);
            vm.OnExtraModelsChanged();
            Assert.True(row.Models.SequenceEqual(["model-a", "model-b"]),
                "extra models: the /models listing itself is untouched by the extras");
            Assert.True(vm.Slots.StandardModels.SequenceEqual(["model-a", "model-b", "model-x", "model-y"]),
                $"extra models: the picker unions listing and extras, deduped ({string.Join(",", vm.Slots.StandardModels)})");

            ProvidersViewModel.Fill(row.Models, ["model-a", "model-b", "model-c"]);
            Assert.True(row.AllChatIds.Contains("model-c"),
                "extra models: a refilled /models listing invalidates the cached id list");
            vm.OnExtraModelsChanged();
            Assert.True(vm.Slots.StandardModels.Contains("model-c"),
                "extra models: the picker follows the refilled listing");

            // (b) an id two providers offer is labelled with the provider name; the Id setter wires the pickers
            vm.Providers.AddProviderCommand.Execute(null);
            var p2 = vm.Providers.Rows[1];
            p2.Name = "p2";
            var extra = new ProviderRow.ExtraModelRow(p2, null);
            p2.ExtraModels.Add(extra);
            extra.Id = "model-x";
            Assert.True(vm.Slots.StandardModels.Contains("p · model-x") && vm.Slots.StandardModels.Contains("p2 · model-x"),
                $"extra models: a shared extra id carries the provider prefix ({string.Join(",", vm.Slots.StandardModels)})");

            // (c) save round-trips the normalized list
            vm.SaveCommand.Execute(null);
            var loaded = SettingsService.Load();
            Assert.True(loaded.Providers.Count >= 1 && loaded.Providers[0].ExtraModels.SequenceEqual(["model-x", "model-y"]),
                $"extra models: save round-trips the normalized list ({string.Join(",", loaded.Providers[0].ExtraModels)})");

            // (d) the header switcher lists only the user-added models: default + extras, not the listing
            var tvm = NewTranslateVm(settingsService.Settings, dir, http);
            await tvm.LoadModelMenuAsync();
            Assert.True(tvm.ModelChoices.Select(c => c.Id).SequenceEqual(["", "model-x", "model-y"])
                && tvm.ModelChoices[0].IsCurrent && !tvm.ModelChoices.Any(c => c.Id == "model-b"),
                $"extra models: the switcher lists default + extras only, no /models listing ({string.Join(",", tvm.ModelChoices.Select(c => c.Id))})");

            // (e) the switcher never contacts the provider: a dead endpoint still lists default + extras
            var deadPort = 19990 + Random.Shared.Next(0, 9);
            var deadPrefix = $"http://127.0.0.1:{deadPort}/";
            using var dead = StartMock(deadPrefix, async ctx =>
            {
                try { ctx.Response.StatusCode = 404; }
                catch (Exception) { /* the client went away */ }
                finally { ctx.Response.Close(); }
            });
            var settings2 = new AppSettings
            {
                Providers = [new ProviderConfig { Name = "p", BaseUrl = deadPrefix + "v1", Model = "model-a",
                    ExtraModels = ["model-x", "model-y"] }],
            };
            var tvm2 = NewTranslateVm(settings2, dir, http);
            await tvm2.LoadModelMenuAsync();
            Assert.True(tvm2.ModelChoices.Select(c => c.Id).SequenceEqual(["", "model-x", "model-y"])
                  && tvm2.ModelChoices[0].IsCurrent && tvm2.ModelMenuStatus == "",
                "extra models: the switcher lists default + extras without a /models answer");
        });
    }

    /// <summary>The star follows a fresh run: after a row is picked in the list and a new translation
    /// lands, toggling favorite acts on the entry the panes show, so the icon and the action agree.</summary>
    static async Task StarFollowsFreshRun()
    {
        var port = 18700 + Random.Shared.Next(0, 200);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream)) body = await reader.ReadToEndAsync();
                if (body.Contains("\"stream\":true"))
                {
                    await WriteSse(ctx,
                        "data: {\"choices\":[{\"delta\":{\"content\":\"hallo\"}}]}\n\n",
                        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n",
                        "data: [DONE]\n\n");
                }
                else
                {
                    ctx.Response.ContentType = "application/json";
                    var json = "{\"choices\":[{\"message\":{\"content\":\"\"}}],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}";
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
                    ctx.Response.Close();
                }
            }
        });

        var settings = new AppSettings
        {
            Providers = [new ProviderConfig { Name = "p", BaseUrl = prefix + "v1", ApiKey = "test-key", Model = "mock" }],
            SourceLang = "en",
            TargetLang = "de",
            Transliteration = false,
        };
        var dir = Path.Combine(Path.GetTempPath(), "hakata-test-" + Guid.NewGuid().ToString("n"));
        using var http = new HttpClient();
        var vm = NewTranslateVm(settings, dir, http);
        try
        {
            vm.InputText = "first text";
            await vm.TranslateAsync();
            await vm.WhenIdleAsync();
            var first = vm.History.Current;
            Assert.True(first is not null, "star target: the first run lands as the current entry");

            // clicking the row picks it in the list while the panes show it
            vm.History.SelectedEntry = first;
            vm.History.SetSelection([first]);

            vm.InputText = "second text";
            await vm.TranslateAsync();
            await vm.WhenIdleAsync();
            var second = vm.History.Current;
            Assert.True(second is not null && !ReferenceEquals(first, second),
                "star target: the second run becomes the current entry");

            vm.History.ToggleFavoriteCommand.Execute(null);
            Assert.True(second is { Favorite: true } && first is { Favorite: false }
                  && vm.History.FavoriteIcon == MaterialIconKind.Star,
                "star target: after a fresh run the star toggles the current entry, matching the icon it shows");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>Two checks that find the same release open the update window once; the details button
    /// can still reopen it on demand.</summary>
    static async Task UpdateCheckOpensOnce()
    {
        var release = new UpdateResponse("9.9.9", "https://example.invalid/release", "");
        var about = new AboutViewModel(new AppMetadata { APILink = "https://example.invalid/api" },
            new StubUpdates(release), new AppSettings());
        var opened = 0;
        about.UpdateFound += _ => opened++;
        await about.CheckCommand.ExecuteAsync(null);
        await about.CheckCommand.ExecuteAsync(null);
        Assert.True(opened == 1 && about.HasUpdate,
            "update modal: a second check for the same release opens no additional window");
        about.ShowDetailsCommand.Execute(null);
        Assert.True(opened == 2, "update modal: the details button still reopens the window on demand");
    }

    sealed class StubUpdates(UpdateResponse? result) : IUpdateService
    {
        public Task<UpdateResponse?> CheckForUpdates() => Task.FromResult(result);
    }
}
