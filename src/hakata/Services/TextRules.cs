using System.Text;

namespace Hakata.Services;

/// <summary>The character-level rules a translation's text must pass: control characters that
/// never belong in a translation, and the scripts that ask for a romanised spelling.</summary>
public static class TextRules
{
    /// <summary>
    /// Drops the characters a translation never needs: bidi controls display in a different order than
    /// they execute (Trojan Source), zero-width marks smuggle hidden text through copy-paste, and the
    /// rest of the control range misbehaves in terminals. Removal is per character, so a character
    /// split across streamed chunks is still caught.
    /// </summary>
    internal static string StripHidden(string text)
    {
        StringBuilder? cleaned = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsHidden(c))
            {
                cleaned ??= new StringBuilder(text, 0, i, text.Length);
                continue;
            }
            cleaned?.Append(c);
        }
        return cleaned?.ToString() ?? text;
    }

    static bool IsHidden(char c) =>
        c is (>= '\u200B' and <= '\u200F')      // zero-width marks, left-to-right marks
          or (>= '\u202A' and <= '\u202E')      // bidi embedding and overrides
          or (>= '\u2060' and <= '\u2064')      // invisible separators and plus signs
          or (>= '\u2066' and <= '\u206F')      // bidi isolates
          or '\uFEFF'                           // BOM / zero-width no-break space
          or (>= '\u0080' and <= '\u009F')      // C1, including more bidi escapes
        // other C0: terminal escapes, but the three real whitespace controls stay
        || (c < ' ' && c != '\n' && c != '\r' && c != '\t');

    /// <summary>True when the text holds letters of a script that is normally romanised. Accented Latin is not one.</summary>
    public static bool NeedsRomanisation(string text)
    {
        foreach (var c in text)
            if (c is (>= '\u0370' and <= '\u1FFF') // Greek, Cyrillic, Hebrew, Arabic, Indic, Thai
                  or (>= '\u2E80' and <= '\u9FFF') // CJK, kana
                  or (>= '\uAC00' and <= '\uD7AF') // Hangul
                  or (>= '\uF900' and <= '\uFAFF')) // CJK compatibility
                return true;
        return false;
    }

    /// <summary>Upper bound for markdown handed to the renderer: long enough for real release
    /// notes, short enough that worst-case parse work stays small even for hostile input.</summary>
    internal const int MaxRenderedMarkdownChars = 64_000;

    /// <summary>Deepest blockquote/list nesting kept on one line. The markdown engine's parser
    /// recurses per nesting level, so a longer run of leading markers on one line could exhaust
    /// the process stack (an uncatchable abort); runs past this are collapsed before rendering.</summary>
    internal const int MaxMarkdownNesting = 16;

    /// <summary>
    /// Bounds the parse work rendered markdown can drive the engine into: the total length is
    /// capped at <see cref="MaxRenderedMarkdownChars"/>, and on every line a leading run of
    /// blockquote (<c>&gt;</c>) or list (<c>-</c>/<c>*</c>/<c>+</c>/<c>1.</c>) markers deeper than
    /// <see cref="MaxMarkdownNesting"/> is collapsed to that depth, so content can no longer make
    /// the recursive parser run unbounded. Ordinary notes pass through unchanged.
    /// </summary>
    internal static string BoundRenderedMarkdown(string text)
    {
        var src = text.Length > MaxRenderedMarkdownChars ? text[..MaxRenderedMarkdownChars] : text;
        StringBuilder? sb = null;
        var pos = 0;
        while (pos < src.Length)
        {
            var nl = src.IndexOf('\n', pos);
            var lineEnd = nl < 0 ? src.Length : nl;
            var (count, contentStart) = ScanMarkerPrefix(src, pos, lineEnd);
            if (count > MaxMarkdownNesting)
            {
                if (sb is null) { sb = new StringBuilder(src.Length); sb.Append(src, 0, pos); }
                var keepEnd = MarkerEnd(src, pos, lineEnd, MaxMarkdownNesting);
                sb.Append(src, pos, keepEnd - pos);
                sb.Append(src, contentStart, lineEnd - contentStart);
            }
            else sb?.Append(src, pos, lineEnd - pos);
            if (nl < 0) break;
            sb?.Append('\n');
            pos = nl + 1;
        }
        return sb?.ToString() ?? src;
    }

    /// <summary>Counts the leading run of blockquote/list markers on [start,end) and returns where
    /// the line's real content begins (the first character after that marker prefix).</summary>
    static (int count, int contentStart) ScanMarkerPrefix(string s, int start, int end)
    {
        var i = start; var count = 0;
        while (i < end)
        {
            var c = s[i];
            if (c is ' ' or '\t') { i++; continue; }
            if (c == '>') { count++; i++; continue; }
            if (IsListMarker(s, i, end)) { count++; i++; continue; }
            if (char.IsDigit(c))
            {
                var j = i;
                while (j < end && char.IsDigit(s[j])) j++;
                if (j + 1 < end && s[j] == '.' && s[j + 1] is ' ' or '\t') { count++; i = j + 1; continue; }
            }
            break;
        }
        return (count, i);
    }

    /// <summary>Offset just past the <paramref name="maxMarkers"/>-th leading marker of the line,
    /// used to keep only the first markers of an over-deep prefix.</summary>
    static int MarkerEnd(string s, int start, int end, int maxMarkers)
    {
        var i = start; var count = 0;
        while (i < end && count < maxMarkers)
        {
            var c = s[i];
            if (c is ' ' or '\t') { i++; continue; }
            if (c == '>') { count++; i++; continue; }
            if (IsListMarker(s, i, end)) { count++; i++; continue; }
            if (char.IsDigit(c))
            {
                var j = i;
                while (j < end && char.IsDigit(s[j])) j++;
                if (j + 1 < end && s[j] == '.' && s[j + 1] is ' ' or '\t') { count++; i = j + 1; continue; }
            }
            break;
        }
        return i;
    }

    static bool IsListMarker(string s, int i, int end) =>
        s[i] is '-' or '*' or '+' && i + 1 < end && s[i + 1] is ' ' or '\t';
}
