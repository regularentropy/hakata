using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hakata.Models;

public sealed record LangItem(string Iso, string Name)
{
    public override string ToString() => Name;
}

public enum QualityTier { Basic, Standard, Best }

public enum ThemeMode { Light, Dark, System }

public enum Formality { Default, Formal, Informal }

public enum Complexity { Simple, Standard, Complex }

public enum Style { Natural, Literal, Creative }

public enum Gender { Unknown, Male, Female }

/// <summary>Persisted spelling of an enum: the lowercase token settings.json carries.</summary>
public static class EnumToken
{
    public static string Token<T>(this T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

/// <summary>A named bundle of translation options the user can switch to, e.g. "Legal contract".
/// The <see cref="Id"/> is what settings.json references; the name is only for display.</summary>
public sealed class TranslationPreset : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    string id = Guid.NewGuid().ToString("n");
    string name = "";
    string context = "";
    Formality formality = Formality.Default;
    Complexity complexity = Complexity.Standard;
    Style style = Style.Natural;
    Gender speakerGender = Gender.Unknown;
    Gender addresseeGender = Gender.Unknown;

    public string Id { get => id; set => Set(ref id, value ?? ""); }
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public string Context { get => context; set => Set(ref context, value ?? ""); }
    public Formality Formality { get => formality; set => Set(ref formality, value); }
    public Complexity Complexity { get => complexity; set => Set(ref complexity, value); }
    public Style Style { get => style; set => Set(ref style, value); }
    public Gender SpeakerGender { get => speakerGender; set => Set(ref speakerGender, value); }
    public Gender AddresseeGender { get => addresseeGender; set => Set(ref addresseeGender, value); }

    public override string ToString() => Name;

    void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Per-request translation options.</summary>
public sealed class TranslationOptions
{
    List<TranslationPreset> presets = [];

    public Formality Formality { get; set; } = Formality.Default;
    public Complexity Complexity { get; set; } = Complexity.Standard;
    public Style Style { get; set; } = Style.Natural;
    public Gender SpeakerGender { get; set; } = Gender.Unknown;
    public Gender AddresseeGender { get; set; } = Gender.Unknown;
    public string Context { get; set; } = "";

    int alternativesCount = 3;

    /// <summary>How many alternative translations the Alternatives panel asks for, clamped to 1..5.</summary>
    public int AlternativesCount { get => alternativesCount; set => alternativesCount = Math.Clamp(value, 1, 5); }

    /// <summary>Id of the selected preset; empty means the plain <see cref="Context"/> is used.</summary>
    public string Preset { get; set; } = "";
    public List<TranslationPreset> Presets { get => presets; set => presets = value ?? []; }

    /// <summary>The selected preset, matched by id; null when none is selected.</summary>
    [JsonIgnore]
    public TranslationPreset? ActivePreset
    {
        get
        {
            if (Preset.Length == 0) return null;
            return presets.FirstOrDefault(p => p.Id == Preset);
        }
    }

    /// <summary>The context a prompt gets: the selected preset's own context, else the plain one.</summary>
    [JsonIgnore]
    public string ActiveContext => ActivePreset?.Context ?? Context;
}

public sealed record HistoryEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Bumps the relative timestamp so an open sidebar does not keep saying "just now".</summary>
    public void RaiseAgoChanged() => Notify(nameof(Ago), nameof(AgoShort));

    // a record may only derive from another record, so the shared model base cannot hold this: the
    // same idiom as TranslationPreset's Set, kept here in three lines instead of at every enum call site
    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }

    void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string From { get; init; } = "auto";
    public string To { get; init; } = "en";
    public string Source { get; init; } = "";
    public string Output { get; init; } = "";
    public DateTime At { get; init; } = DateTime.UtcNow;
    bool favorite;

    /// <summary>Starred. Notifies, because the sidebar row swaps its star glyph, colour and visibility on the
    /// same control: Refresh syncs rows in place (CollectionSync), so nothing rebuilds the bindings.</summary>
    public bool Favorite
    {
        get => favorite;
        set => Set(ref favorite, value);
    }

    // the romanised sides, stored once the extra request answered: a restored entry shows them again
    // instead of paying for the same call twice
    public string TranslitSource { get; set; } = "";
    public string TranslitTarget { get; set; } = "";

    // alternatives and word insights likewise arrive after the run, so they ride the row as JSON text;
    // a row written before they existed (or a corrupt blob) reads back as empty
    public string AlternativesDescription { get; set; } = "";
    public string AlternativesJson { get; set; } = "";
    public string InsightsJson { get; set; } = "";

    [NotMapped, JsonIgnore]
    public List<Alternative> Alternatives
    {
        get => ReadList<Alternative>(AlternativesJson);
        set => AlternativesJson = JsonSerializer.Serialize(value);
    }

    [NotMapped, JsonIgnore]
    public List<WordInsight> Insights
    {
        get => ReadList<WordInsight>(InsightsJson);
        set => InsightsJson = JsonSerializer.Serialize(value);
    }

    static List<T> ReadList<T>(string json)
    {
        if (json.Length == 0) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    // what this pass cost, frozen at run time so later price edits do not rewrite the past;
    // the sound button adds its own render cost to the same number, the extras add their tokens
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    double cost;

    /// <summary>The row's chip binds its computed values (CostShort/HasCost) off Cost and rows are synced in place, so the setter notifies them itself.</summary>
    public double Cost
    {
        get => cost;
        set
        {
            if (Set(ref cost, value)) Notify(nameof(CostShort), nameof(HasCost));
        }
    }

    // the prices in effect when the pass ran, frozen with the cost so the footer can re-render
    // the same number after a restore
    public double InputPricePerMTok { get; set; }
    public double OutputPricePerMTok { get; set; }
    public bool UsageEstimated { get; set; }

    [JsonIgnore] public TokenUsage Usage => new(PromptTokens, CompletionTokens, UsageEstimated);
    // the stored Cost is authoritative: it includes speech cost and any reported usage.cost,
    // which a tokens×prices recomputation could never reproduce
    [JsonIgnore] public string UsageLabel
    {
        get
        {
            var tokens = $"↑{PromptTokens} ↓{CompletionTokens} tokens" + (UsageEstimated ? " (approx.)" : "");
            return Cost > 0 ? tokens + " · " + Pricing.CostLabel(Cost) : tokens;
        }
    }

    [JsonIgnore] public bool HasCost => Cost > 0;

    /// <summary>Money for this entry, or empty when the run happened before any price was configured.</summary>
    [JsonIgnore]
    public string CostLabel => Cost > 0 ? Pricing.CostLabel(Cost) : "";

    /// <summary>The same money without the estimate tilde: a sidebar row has no space to explain it.</summary>
    [JsonIgnore]
    public string CostShort => Cost > 0 ? "$" + Cost.ToString("0.0000", CultureInfo.InvariantCulture) : "";

    public string Ago
    {
        get
        {
            var d = DateTime.UtcNow - At;
            if (d < TimeSpan.FromMinutes(1)) return "just now";
            if (d < TimeSpan.FromHours(1)) return AgoText((int)d.TotalMinutes, "minute");
            if (d < TimeSpan.FromDays(1)) return AgoText((int)d.TotalHours, "hour");
            return AgoText((int)d.TotalDays, "day");
        }
    }

    static string AgoText(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    /// <summary>The same age in a form that fits a 210px sidebar next to the target language and the cost.</summary>
    public string AgoShort
    {
        get
        {
            var d = DateTime.UtcNow - At;
            if (d < TimeSpan.FromMinutes(1)) return "now";
            if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min";
            if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h";
            return $"{(int)d.TotalDays} d";
        }
    }

    public string Preview => Source.Length <= 90 ? Source : Source[..CutIndex(Source, 90)] + "…";

    /// <summary>The index to slice at: one back from the limit when it would split a surrogate pair.</summary>
    static int CutIndex(string text, int limit) => char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    public string Route => $"{LanguageNames.Label(From)} → {LanguageNames.Label(To)}";

    /// <summary>The target language alone: the sidebar row already shows the source as its preview.</summary>
    [JsonIgnore] public string Target => LanguageNames.Label(To);
}

public static class LanguageNames
{
    public static string Label(string iso) => iso == "auto" ? "Detect Language" : Languages.NameOf(iso);
}

/// <summary>Token counts for one request, as reported by the provider or estimated when it stays quiet.</summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens, bool Estimated, double? ReportedCost = null)
{
    /// <summary>Rough count for providers that do not report usage: about four characters per token.</summary>
    public static int Estimate(string text) => text.Length == 0 ? 0 : (text.Length + 3) / 4;

    /// <summary>Usage for providers that never reported one: character estimates for both sides.</summary>
    public static TokenUsage EstimatedFor(string source, string output) =>
        new(Estimate(source), Estimate(output), Estimated: true);

    /// <summary>
    /// "↑123 ↓456 tokens · ~$0.0042" — the price part appears when a price is configured or the provider reported one.
    /// </summary>
    public string Label(AppSettings settings) => Label(settings.InputPricePerMTok, settings.OutputPricePerMTok);

    public string Label(double inputPricePerMTok, double outputPricePerMTok)
    {
        var tokens = $"↑{PromptTokens} ↓{CompletionTokens} tokens";
        if (Estimated) tokens += " (approx.)";
        var cost = Pricing.Cost(this, inputPricePerMTok, outputPricePerMTok);
        return cost is null ? tokens : tokens + " · " + Pricing.CostLabel(cost.Value);
    }
}

/// <summary>What the request and the response cost: the cost the provider reported when it gave one, else the prices the user entered.</summary>
public static class Pricing
{
    /// <summary>Null when no price is configured, so the UI can stay quiet instead of showing $0.0000.</summary>
    public static double? Cost(TokenUsage usage, AppSettings settings) =>
        Cost(usage, settings.InputPricePerMTok, settings.OutputPricePerMTok);

    public static double? Cost(TokenUsage usage, double inputPricePerMTok, double outputPricePerMTok)
    {
        if (usage.ReportedCost is { } reported && reported > 0) return reported;
        if (inputPricePerMTok <= 0 && outputPricePerMTok <= 0) return null;
        return usage.PromptTokens / 1_000_000d * inputPricePerMTok +
               usage.CompletionTokens / 1_000_000d * outputPricePerMTok;
    }

    /// <summary>Characters at a price per 1M of them (speech synthesis); null when no price is known.</summary>
    public static double? PerChars(int characters, double? perMillion) =>
        perMillion is > 0 ? characters / 1_000_000d * perMillion.Value : null;

    /// <summary>Seconds at a price per minute (speech recognition); null when no price is known.</summary>
    public static double? PerMinutes(double seconds, double? perMinute) =>
        perMinute is > 0 ? seconds / 60d * perMinute.Value : null;

    /// <summary>Money for the UI, culture-stable so the decimal point never moves.</summary>
    public static string CostLabel(double cost) =>
        "~$" + cost.ToString("0.0000", CultureInfo.InvariantCulture);
}

/// <summary>One alternative translation with its streamed rationale.</summary>
public sealed class Alternative
{
    public string Text { get; set; } = "";
    public string Explanation { get; set; } = "";
    public bool HasExplanation => Explanation.Length > 0;
}

/// <summary>Word-level insight: a word worth offering variants for.</summary>
public sealed class WordInsight
{
    public string Word { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<Alternative> Alternatives { get; set; } = [];
}
