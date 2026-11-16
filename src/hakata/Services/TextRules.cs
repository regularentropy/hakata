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
}
