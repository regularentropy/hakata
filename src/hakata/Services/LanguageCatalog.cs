using System;
using System.Collections.Generic;
using System.Linq;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>Language list ordering and the text → ISO resolution behind the language boxes.</summary>
public static class LanguageCatalog
{
    /// <summary>All languages, most used in history first, optionally preceded by "Detect Language" and the source-only "Website URL" row.</summary>
    public static List<LangItem> Build(IEnumerable<HistoryEntry> history, bool includeAuto)
    {
        var counts = new Dictionary<string, int>();
        foreach (var e in history)
        {
            counts[e.To] = counts.GetValueOrDefault(e.To) + 1;
            if (e.From != "auto") counts[e.From] = counts.GetValueOrDefault(e.From) + 1;
        }

        var list = new List<LangItem>();
        if (includeAuto)
        {
            list.Add(new LangItem("auto", "Detect Language"));
            list.Add(new LangItem("url", "Website URL")); // source-only: never offered as a target
        }
        list.AddRange(Languages.All
            .Select(l => new LangItem(l.Iso, l.Name))
            .OrderByDescending(l => counts.GetValueOrDefault(l.Iso))
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Selected item, else a name or ISO match on the typed text, else the fallback.</summary>
    public static string Resolve(IReadOnlyList<LangItem> items, string? text, LangItem? selected, string fallback)
    {
        if (selected is not null) return selected.Iso;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return fallback;
        return Find(items, trimmed)?.Iso ?? fallback;
    }

    /// <summary>The item whose name or ISO equals the typed text, or null when the text is not a language.</summary>
    public static LangItem? Find(IReadOnlyList<LangItem> items, string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        return items.FirstOrDefault(l =>
            string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l.Iso, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
