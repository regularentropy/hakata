using System;
using System.Globalization;
using System.Text.Json;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>The per-line readers of the OpenAI-compatible SSE stream: deltas, billing counters,
/// finish reasons, and the rule that decides a stream ended mid-answer.</summary>
public static class SseParser
{
    /// <summary>The usage counters of a stream, or null for any line that is not one.</summary>
    public static TokenUsage? ParseSseUsage(string line)
    {
        if (DataOf(line) is not { } data || !data.Contains("\"usage\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!Jsons.TryProp(doc.RootElement, "usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
            var prompt = IntOf(usage, "prompt_tokens");
            var completion = IntOf(usage, "completion_tokens");
            if (prompt is null && completion is null) return null;
            // some providers bill the stream themselves and say so alongside the counters
            double? cost = usage.TryGetProperty("cost", out var billed) &&
                           billed.ValueKind == JsonValueKind.Number &&
                           billed.TryGetDouble(out var amount) && amount > 0
                ? amount : null;
            return new TokenUsage(prompt ?? 0, completion ?? 0, Estimated: false, ReportedCost: cost);
        }
        catch (Exception) { return null; } // a malformed usage line (string/float counters, junk) is not fatal
    }

    // some providers send the counters as strings ("42") or floats (42.0); both must count, not throw or drop
    static int? IntOf(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.Number when value.TryGetDouble(out var d) => (int)d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => (int)s,
            _ => null,
        };
    }

    /// <summary>The finish_reason of one SSE line, or empty when the line carries none.</summary>
    public static string ParseSseFinish(string line)
    {
        if (DataOf(line) is not { } data || !data.Contains("\"finish_reason\"", StringComparison.Ordinal)) return "";
        try
        {
            using var doc = JsonDocument.Parse(data);
            return TryFirstChoice(doc.RootElement, out var choice) ? Jsons.Str(choice, "finish_reason") : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>True when the answer was not finished: an exhausted output budget and a dropped
    /// connection look the same from here, and both leave the translation mid-sentence.</summary>
    public static bool StreamWasCut(bool sawDone, string finish) =>
        finish is "length" or "max_tokens" || (!sawDone && finish.Length == 0);

    /// <summary>Extracts the text fragment of one SSE line, or null for anything else.</summary>
    public static string? ParseSseDelta(string line)
    {
        if (DataOf(line) is not { } data) return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!TryFirstChoice(doc.RootElement, out var choice)) return null;
            // TryProp only guards the parent: a scalar delta/message would make TryGetProperty throw (#0052)
            if (Jsons.TryProp(choice, "delta", out var delta) && delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
            if (Jsons.TryProp(choice, "message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
            return null;
        }
        catch (JsonException) { return null; } // partial/keepalive lines are not fatal
    }

    /// <summary>The JSON payload of one <c>data:</c> line, or null for a keepalive, [DONE] or any other line.</summary>
    static string? DataOf(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var data = line[5..].Trim();
        return data.Length == 0 || data == "[DONE]" ? null : data;
    }

    /// <summary>The first choice of a parsed chunk; false when the chunk carries none.</summary>
    static bool TryFirstChoice(JsonElement root, out JsonElement choice)
    {
        choice = default;
        if (!Jsons.TryProp(root, "choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return false;
        choice = choices[0];
        return true;
    }
}
