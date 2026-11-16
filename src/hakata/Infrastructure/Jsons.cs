using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Hakata.Infrastructure;

/// <summary>Tolerant JSON reading: models wrap JSON in prose or fences far too often.</summary>
public static class Jsons
{
    /// <summary>First balanced JSON value (object or array) in the text, or the text itself.</summary>
    public static string Slice(string raw)
    {
        var start = raw.IndexOfAny(['{', '[']);
        if (start < 0) return raw;
        var depth = 0;
        var inString = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0) return raw[start..(i + 1)];
        }
        return raw[start..];
    }

    public static JsonElement? TryParse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(Slice(raw));
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

        // TryGetProperty throws InvalidOperationException on a non-object element, so every
    // provider-controlled read goes through this guard instead (issue #0015).
    internal static bool TryProp(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value);
    }

    public static string Str(JsonElement el, string name) =>
        TryProp(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    public static string Str(string raw, string name) => TryGet(raw, name) ?? "";

    public static string? TryGet(string raw, string name)
    {
        if (TryParse(raw) is not { } el || !TryProp(el, name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return v.GetString();
    }

    /// <summary>Array property of an object, or the array itself when the model returns a bare list.</summary>
    public static IEnumerable<JsonElement> Elements(string raw, string name) =>
        TryParse(raw) is { } el ? Elements(el, name) : [];

    /// <inheritdoc cref="Elements(string, string)"/>
    public static IEnumerable<JsonElement> Elements(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().ToList()
        : TryProp(el, name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().ToList()
            : [];
}
