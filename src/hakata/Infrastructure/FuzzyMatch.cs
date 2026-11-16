using System;
using Avalonia.Controls;

namespace Hakata.Infrastructure;

/// <summary>
/// Loose matching for the pickers: ignores case and separators, and tolerates gaps, so
/// "deepseek 4.1 flash" still finds "deepseek-v4.1-flash", and the abbreviation "dsv4flash" finds it too.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>The predicate as a value, so XAML can assign it: <c>ItemFilter="{x:Static infra:FuzzyMatch.Predicate}"</c>.</summary>
    public static readonly AutoCompleteFilterPredicate<object?> Predicate = Matches;

    /// <summary>Predicate for <c>AutoCompleteBox.ItemFilter</c>; an empty search lists everything.</summary>
    public static bool Matches(string? search, object? item)
    {
        var query = Normalize(search);
        if (query.Length == 0) return true;
        var name = Normalize(item?.ToString());
        if (name.Length == 0) return false;
        if (name.Contains(query, StringComparison.Ordinal)) return true;

        // Substring missed: accept the query as an in-order subsequence of the name.
        var matched = 0;
        foreach (var c in name)
            if (matched < query.Length && c == query[matched]) matched++;
        return matched == query.Length;
    }

    /// <summary>Lower-cases and drops everything that is not a letter or digit.</summary>
    static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) buffer[length++] = char.ToLowerInvariant(c);
        return new string(buffer, 0, length);
    }
}
