using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Hakata.Views;
using Xunit;

namespace Hakata;

/// <summary>
/// Enforces the invariant behind #0067: every type bound as x:DataType must either be immutable or
/// notify PropertyChanged on every settable property. A silent setter is exactly how the sidebar
/// star stayed grey — this makes the whole class of bug a red line instead of an audit find.
/// </summary>
public class PropertyTests
{
    // only the prefixes the app's own XAML uses; an unknown prefix is a failure, not a skip
    static readonly Dictionary<string, string> Prefixes = new()
    {
        ["vm"] = "Hakata.ViewModels",
        ["models"] = "Hakata.Models",
        ["m"] = "Hakata.Models",
        ["views"] = "Hakata.Views",
        ["c"] = "Hakata.Controls",
    };

    // Headless builders for the bound types that have no parameterless constructor. Each one
    // mirrors how SelfTest constructs the same view model without a running app: real settings
    // services over throwaway AppSettings, fakes for the audio hardware, no network calls. The
    // harness points HAKATA_HOME at a temp directory, so setters that persist do so harmlessly.
    static readonly Dictionary<Type, Func<object>> Builders = new()
    {
        [typeof(TranslateViewModel)] = BuildTranslateViewModel,
        [typeof(SettingsViewModel)] = () => new SettingsViewModel(new SettingsService(new AppSettings()), new HttpClient()),
        [typeof(PresetsViewModel)] = () => new PresetsViewModel(new SettingsService(new AppSettings())),
        [typeof(ChromeViewModel)] = () => new ChromeViewModel(new SettingsService(new AppSettings()), _ => { }, () => { }),
        [typeof(AboutViewModel)] = () => new AboutViewModel(
            new AppMetadata { APILink = "" }, new UpdateService(new AppMetadata { APILink = "" }, new HttpClient()),
            new AppSettings { CheckUpdatesOnLaunch = false }),
        [typeof(ProviderRow)] = () => new ProviderRow(NewProvidersOwner(), new ProviderConfig { Name = "propertytest" }),
        [typeof(ProviderRow.ExtraModelRow)] = () =>
            new ProviderRow.ExtraModelRow(new ProviderRow(NewProvidersOwner(), new ProviderConfig { Name = "propertytest" }), "extra-model"),
        [typeof(ModelChoice)] = () => new ModelChoice("model-id", "model label", false),
        [typeof(ProfilesViewModel.ProfileRow)] = () => new ProfilesViewModel.ProfileRow("name", "dir", false, false, false, false),
        [typeof(SlotsViewModel.EffortChoice)] = () => new SlotsViewModel.EffortChoice("default"),
        [typeof(UpdateViewModel)] = () => new UpdateViewModel(new UpdateResponse("v0.0.0", "https://example.test", "notes"), new AppMetadata()),
        [typeof(ProfilePickerWindow.Row)] = () => new ProfilePickerWindow.Row("name", "dir", false, false),
    };

    // Values for settable property types that have no parameterless constructor. The probe needs
    // a value that differs from the current one, because generated setters guard with value
    // equality and would swallow an equal value without raising.
    static readonly Dictionary<Type, Func<object?, object?>> Synthesisers = new()
    {
        [typeof(ProfilesViewModel.ProfileRow)] = _ =>
            new ProfilesViewModel.ProfileRow("propertytest", "propertytest dir", false, false, false, false),
    };

    // Settable properties whose silent setter is intentional: written wholesale before anything can
    // observe them and never bound in AXAML, so there is nothing to notify. Keyed "Type.Property";
    // --strict stays green for these and only new violations fail.
    static readonly HashSet<string> NonNotifying = new()
    {
        // HistoryEntry: the sidebar row binds only Preview/Ago/Target/AgoShort/HasCost/CostShort/
        // Favorite; these ride the entry as data, written once when the run (or its restore)
        // completes and read back as plain values when the row is opened in the panes.
        "HistoryEntry.TranslitSource", "HistoryEntry.TranslitTarget",
        "HistoryEntry.AlternativesDescription", "HistoryEntry.AlternativesJson",
        "HistoryEntry.InsightsJson", "HistoryEntry.Alternatives", "HistoryEntry.Insights",
        "HistoryEntry.PromptTokens", "HistoryEntry.CompletionTokens",
        "HistoryEntry.InputPricePerMTok", "HistoryEntry.OutputPricePerMTok",
        "HistoryEntry.UsageEstimated",
        // ProviderRow: the three catalogs are refetched wholesale by the /models refresh and are
        // only read through SlotsViewModel lookups, never bound.
        "ProviderRow.ChatCatalog", "ProviderRow.SpeechCatalog", "ProviderRow.SoundCatalog",
    };

    static ProvidersViewModel NewProvidersOwner() =>
        new(new SettingsViewModel(new SettingsService(new AppSettings())), new HttpClient(), new AppSettings());

    static object BuildTranslateViewModel()
    {
        var settings = new AppSettings();
        var dir = Path.Combine(Path.GetTempPath(), "hakata-propertytest");
        Directory.CreateDirectory(dir);
        var history = new History(dir);
        var provider = new OpenAiCompatibleProvider(new HttpClient(), settings);
        return new TranslateViewModel(new SettingsService(settings), history,
            new TranslationCoordinator(provider, settings, history), provider, new SilentRecorder(), new SilentPlayer());
    }

    /// <summary>A recorder that never touches a sound card; TranslateViewModel only needs the object to exist.</summary>
    sealed class SilentRecorder : IAudioRecorder
    {
        public bool IsRecording { get; private set; }
        public void Start() => IsRecording = true;
        public byte[] Stop() { IsRecording = false; return []; }
        public void Dispose() => IsRecording = false;
    }

    /// <summary>A player that keeps the clip in memory instead of opening a sound card.</summary>
    sealed class SilentPlayer : ISpeechPlayer
    {
        public bool IsPlaying { get; private set; }
        public void Play(byte[] audio, string mimeType) => IsPlaying = true;
        public void Stop() => IsPlaying = false;
        public void Dispose() => IsPlaying = false;
    }

    /// <summary>
    /// The sweep cannot tell "set once before display" (TranslitSource, Cost) from "mutated live"
    /// (Favorite); the allowlist above records the first kind, so the test gates the second.
    /// Resolution rot and throwing setters always fail. Runs strict: every settable property of
    /// a bound type must raise PropertyChanged.
    /// </summary>
    [Fact]
    public void SettablePropertiesOfBoundTypesNotify() => Run(strict: true);

    static void Run(bool strict)
    {
        var root = FindRoot();
        Assert.True(root is not null, "propertytest: repo root not found, nothing checked");

        var asm = typeof(ViewModels.AboutViewModel).Assembly;
        var bound = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .SelectMany(t => Regex.Matches(t, @"x:DataType=""([^""]+)""").Select(m => m.Groups[1].Value))
            .Distinct().OrderBy(s => s).ToList();

        var fails = new List<string>();
        var silent = new List<string>();
        var skipped = new List<string>();
        var noNotify = new List<string>();
        var probed = 0;

        foreach (var dt in bound)
        {
            var type = Resolve(dt, asm);
            if (type is null)
            {
                fails.Add($"propertytest: FAIL x:DataType \"{dt}\" does not resolve to a type");
                continue;
            }
            object? instance;
            try
            {
                // a parameterless ctor is probed directly; everything else goes through the
                // headless builders above, so DI-built view models are covered too
                instance = type.GetConstructor(Type.EmptyTypes) is not null
                    ? Activator.CreateInstance(type)
                    : Builders.TryGetValue(type, out var build) ? build() : null;
            }
            catch (Exception ex)
            {
                fails.Add($"propertytest: FAIL {type.Name} could not be constructed headless: {ex.GetBaseException().Message}");
                continue;
            }
            if (instance is null)
            {
                // named with the reason instead of a bare name: a type nobody can build is a
                // justified skip, not a hidden one
                skipped.Add($"{type.Name} (no parameterless ctor and no headless builder registered)");
                continue;
            }
            if (!typeof(INotifyPropertyChanged).IsAssignableFrom(type)) { noNotify.Add(type.Name); continue; }

            probed++;
            try { Probe(instance, type, fails, silent); }
            catch (Exception ex) { fails.Add($"propertytest: FAIL {type.Name} could not be probed: {ex.GetBaseException().Message}"); }
        }

        if (strict)
            fails.AddRange(silent.Select(name => $"propertytest: FAIL {name}: setter raised no PropertyChanged"));
        foreach (var f in fails) Console.WriteLine(f);
        foreach (var s in skipped) Console.WriteLine($"propertytest: skipped {s}");
        Console.WriteLine($"propertytest: {bound.Count} bound types, {probed} probed, {skipped.Count} skipped [{string.Join(", ", skipped)}]");
        Console.WriteLine($"propertytest: bound without notification [{string.Join(", ", noNotify)}]");
        Console.WriteLine($"propertytest: settable without notification [{string.Join(", ", silent)}]");
        Assert.True(fails.Count == 0, $"{fails.Count} propertytest failure(s): {string.Join("; ", fails)}");
    }

    static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "hakata.sln")))
                return dir.FullName;
        return null;
    }

    static Type? Resolve(string dataType, Assembly asm)
    {
        var colon = dataType.IndexOf(':');
        if (colon <= 0 || !Prefixes.TryGetValue(dataType[..colon], out var ns)) return null;
        return asm.GetType(ns + "." + dataType[(colon + 1)..].Replace(':', '+'));
    }

    static void Probe(object instance, Type type, List<string> fails, List<string> silent)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = prop.SetMethod;
            if (prop.GetIndexParameters().Length > 0 || setter is null || !setter.IsPublic) continue;
            // init-only setters cannot be assigned after construction, so the invariant cannot be broken there
            if (setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit")) continue;
            if (NonNotifying.Contains($"{type.Name}.{prop.Name}")) continue; // allowlisted above with its reason

            object? current;
            try { current = prop.GetValue(instance); }
            catch { continue; } // getter throws: nothing to synthesise from
            if (!TrySynthesise(prop.PropertyType, current, out var newValue)) continue;

            var notified = false;
            // an empty or null property name means "everything changed", so it counts for every probe
            void Handler(object? _, PropertyChangedEventArgs e) =>
                notified |= string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == prop.Name;

            var inpc = (INotifyPropertyChanged)instance;
            inpc.PropertyChanged += Handler;
            try
            {
                try { prop.SetValue(instance, newValue); }
                catch (Exception ex) { fails.Add($"propertytest: FAIL {type.Name}.{prop.Name}: setter threw {ex.GetBaseException().GetType().Name}"); continue; }
                if (!notified) silent.Add($"{type.Name}.{prop.Name}");
            }
            finally
            {
                try { prop.SetValue(instance, current); } // restore so the next property starts clean
                finally { inpc.PropertyChanged -= Handler; }
            }
        }
    }

    static bool TrySynthesise(Type type, object? current, out object? value)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            if (current is null) { value = null; return false; } // null -> null is a no-op for most setters
            type = underlying;
        }
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.String: value = (string)current! + "x"; return true;
            case TypeCode.Boolean: value = !(bool)current!; return true;
            case TypeCode.Int32: value = (int)current! + 1; return true;
            case TypeCode.Int64: value = (long)current! + 1; return true;
            case TypeCode.Double: value = (double)current! + 1; return true;
            case TypeCode.Decimal: value = (decimal)current! + 1; return true;
            case TypeCode.DateTime: value = (DateTime)current! + TimeSpan.FromDays(1); return true;
            default:
                if (type.IsEnum)
                {
                    var vals = Enum.GetValues(type);
                    value = vals.GetValue(((int)Array.IndexOf(vals, current!) + 1) % vals.Length)!;
                    return true;
                }
                if (type == typeof(Guid)) { value = Guid.NewGuid(); return true; }
                if (type == typeof(TimeSpan)) { value = (TimeSpan)current! + TimeSpan.FromMinutes(1); return true; }
                if (Synthesisers.TryGetValue(type, out var synthesise)) { value = synthesise(current); return true; }
                // Reference and collection properties: a fresh default instance (an empty list, a
                // new preset) differs from the current value, so the setter cannot hide behind an
                // equality guard. Interface-typed collections take a List of their element type,
                // which is assignable and equally distinct from the current instance.
                if (!type.IsValueType)
                {
                    if (type.GetConstructor(Type.EmptyTypes) is { } ctor)
                    {
                        try
                        {
                            var fresh = ctor.Invoke(null);
                            if (!Equals(fresh, current)) { value = fresh; return true; }
                        }
                        catch (Exception) { /* fall through to the list and synthesiser options */ }
                    }
                    var element = EnumerableElementType(type);
                    if (element is not null)
                    {
                        var list = typeof(List<>).MakeGenericType(element);
                        if (type.IsAssignableFrom(list)) { value = Activator.CreateInstance(list); return true; }
                    }
                }
                value = null;
                return false;
        }
    }

    static Type? EnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;
        foreach (var i in type.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }
}
