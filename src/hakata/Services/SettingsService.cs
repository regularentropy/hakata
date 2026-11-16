using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>
/// The only writer of settings.json. Save points, and nothing else: main window close, settings
/// dialog save (theme included), the theme toggle, preset edits and an auto-filled price.
/// </summary>
public sealed class SettingsService(AppSettings settings)
{
    public AppSettings Settings { get; } = settings;

    // captured at construction: a hot profile swap re-points AppSettings.Dir, but this instance
    // must keep writing the profile's settings.json it was built for, never the new profile's
    readonly string dir = AppSettings.Dir;
    readonly string filePath = AppSettings.FilePath;

    public static AppSettings Load()
    {
        if (!File.Exists(AppSettings.FilePath)) return FreshInstall();
        try
        {
            var raw = File.ReadAllText(AppSettings.FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(raw, AppJson.Options) ?? Corrupt();
            Normalize(settings);
            StoreFile.RestrictFile(AppSettings.FilePath); // the file holds keys: owner-only off Windows (S1.8)
            return settings;
        }
        catch (JsonException)
        {
            return Corrupt();
        }
        catch (Exception)
        {
            // locked or denied right now: the file is the user's, so it stays untouched and
            // writing is disabled for this run instead of overwriting it with defaults
            return Transient();
        }
    }

    /// <summary>Issue #0015: a valid file can still carry null graphs; normalize so consumers cannot NRE.</summary>
    internal static void Normalize(AppSettings settings)
    {
        settings.Providers ??= [];
        settings.Providers.RemoveAll(p => p is null);
        foreach (var provider in settings.Providers)
        {
            provider.Name ??= "";
            provider.BaseUrl ??= "";
            provider.Model ??= "";
            provider.ExtraModels ??= [];
            provider.ExtraModels = provider.ExtraModels.Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        settings.Options ??= new TranslationOptions();
        settings.Options.Context ??= "";
        settings.Options.Preset ??= "";
        // nameless presets cannot be shown in the picker and are dropped here
        settings.Options.Presets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));
        foreach (var preset in settings.Options.Presets)
        {
            if (string.IsNullOrEmpty(preset.Id)) preset.Id = Guid.NewGuid().ToString("n");
            preset.Context ??= "";
        }
        // a preset deleted since the last save leaves a dangling id here: clear it
        settings.Options.Preset = settings.Options.ActivePreset?.Id ?? "";
    }

    /// <summary>A first start seeds four deletable presets so the picker is not empty; they are ordinary
    /// presets and land in settings.json on the first save like anything else. A file that exists — even
    /// without presets — is never seeded.</summary>
    static AppSettings FreshInstall()
    {
        var settings = new AppSettings();
        settings.Options.Presets.AddRange(
        [
            new TranslationPreset
            {
                Name = "In-game chat",
                Context = "Short player chat in an online game, where slang, abbreviations and missing punctuation are normal.",
                Formality = Formality.Informal,
                Complexity = Complexity.Simple,
            },
            new TranslationPreset
            {
                Name = "Legal contract",
                Context = "A legal contract, where defined terms and clause numbering must stay exactly as written.",
                Formality = Formality.Formal,
                Complexity = Complexity.Complex,
                Style = Style.Literal,
            },
            new TranslationPreset
            {
                Name = "Academic abstract",
                Context = "An academic abstract, where the terminology must stay precise and the argument intact.",
                Formality = Formality.Formal,
                Complexity = Complexity.Complex,
            },
            new TranslationPreset
            {
                Name = "Customer support reply",
                Context = "A customer support reply, which should be friendly, clear and answer the question directly.",
                Formality = Formality.Formal,
                Complexity = Complexity.Simple,
            },
        ]);
        return settings;
    }

    static AppSettings Corrupt()
    {
        var settings = new AppSettings();
        settings.RecoveryNotice = StoreFile.MoveAside(AppSettings.FilePath, ".bad") is { } name
            ? $"settings.json was not readable and was kept as {name}"
            : "settings.json was not readable and could not be kept";
        return settings;
    }

    static AppSettings Transient()
    {
        var settings = new AppSettings
        {
            PersistenceDisabled = true,
            RecoveryNotice = "settings.json could not be read (locked or denied); changes are not saved this run",
        };
        return settings;
    }

    public void Save()
    {
        if (Settings.PersistenceDisabled) return; // a store this run could not read is never overwritten
        Directory.CreateDirectory(dir);
        StoreFile.RestrictDir(dir);
        // write beside the file and rename, so a crash mid-save cannot truncate settings.json
        StoreFile.WriteAtomic(filePath, tmp => WriteRestricted(tmp, JsonSerializer.Serialize(Settings, AppJson.Options)));
    }

    /// <summary>Raised after a price write lands, so the cost label and the price flyout re-read it.</summary>
    public event Action? PricesChanged;

    /// <summary>
    /// The only way a price changes outside the settings dialog: apply both, save, tell the bindings.
    /// A view model guarding a value it does not own is what shipped #0068/#0069.
    /// </summary>
    public void SetPrices(double input, double output, bool onlyIfUntouched = false)
    {
        // a pair the user typed wins over a published one
        if (onlyIfUntouched && (Settings.InputPricePerMTok > 0 || Settings.OutputPricePerMTok > 0)) return;
        if (Settings.InputPricePerMTok == input && Settings.OutputPricePerMTok == output) return;
        Settings.InputPricePerMTok = input;
        Settings.OutputPricePerMTok = output;
        Save();
        PricesChanged?.Invoke();
    }

    /// <summary>Applies a live setting and saves it in the same step, so a toggle cannot forget the
    /// save. Window geometry and the settings dialog keep writing directly: they save once, on purpose.</summary>
    public void Mutate(Action<AppSettings> apply)
    {
        apply(Settings);
        Save();
    }

    /// <summary>The one swallow-and-report around a settings write: a failed save (locked file, full
    /// disk) is reported through the caller's status line, never thrown out of a setter or command.
    /// A store this run could not read (<see cref="AppSettings.PersistenceDisabled"/>) already says so
    /// in its recovery notice, so it is not reported twice.</summary>
    public void TryMutate(Action<AppSettings> apply, string couldNotSave, Action<string>? report)
    {
        try { Mutate(apply); }
        catch (Exception ex) { ReportSaveFailure(couldNotSave, ex, report); }
    }

    /// <summary>A bare save (no change to apply), reported the same way.</summary>
    public void TrySave(string couldNotSave, Action<string>? report)
    {
        try { Save(); }
        catch (Exception ex) { ReportSaveFailure(couldNotSave, ex, report); }
    }

    void ReportSaveFailure(string couldNotSave, Exception ex, Action<string>? report)
    {
        if (report is not null && !Settings.PersistenceDisabled) report(couldNotSave + ex.Message);
    }

    /// <summary>
    /// Off Windows the file carries the plaintext key, so it is created 0600 before anything is
    /// written and the app-owned directory 0700 (narrowed when an older build left it wide).
    /// An HAKATA_HOME directory keeps its own mode; only the files inside are restricted.
    /// </summary>
    internal static void WriteRestricted(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, contents);
            return;
        }
        const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
        File.SetUnixFileMode(file.SafeFileHandle, Mode); // set before the key bytes land
        using var writer = new StreamWriter(file);
        writer.Write(contents);
    }
}
