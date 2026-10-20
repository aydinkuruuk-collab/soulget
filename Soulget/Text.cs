using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Soulget;

/// <summary>
/// Text helpers that behave exactly like the Python version (str.lower, \w, isdigit,
/// len...). Track identities are built from these, so any difference would make
/// soulget think it doesn't have a file it already downloaded.
/// </summary>
public static class Text
{
    /// <summary>Unicode decomposition with accents removed: "Café" -> "Cafe".</summary>
    public static string StripAccents(string s)
    {
        var d = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(d.Length);
        foreach (var r in d.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Python's str.lower(), including the Greek final sigma rule.</summary>
    public static string Lower(string s)
    {
        var lower = s.ToLowerInvariant();
        if (lower.IndexOf('σ') < 0) return lower;
        var chars = lower.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] != 'σ' || s[i] != 'Σ') continue;
            bool before = i > 0 && char.IsLetter(s[i - 1]);
            bool after = i + 1 < s.Length && char.IsLetter(s[i + 1]);
            if (before && !after) chars[i] = 'ς';
        }
        return new string(chars);
    }

    /// <summary>Python's "word character, but not underscore" ([^\W_]).</summary>
    static bool IsAlnum(Rune r)
    {
        if (Rune.IsLetterOrDigit(r)) return true;
        var cat = Rune.GetUnicodeCategory(r);
        return cat is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    /// <summary>Lower-case words with punctuation removed: "Don't Stop (Remix)" -> [dont, stop, remix].</summary>
    public static List<string> Tokens(string s)
    {
        s = Lower(StripAccents(s ?? ""));
        s = s.Replace("&", " and ").Replace("'", "").Replace("’", "").Replace("`", "");
        var outList = new List<string>();
        var cur = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            if (IsAlnum(r)) cur.Append(r.ToString());
            else if (cur.Length > 0) { outList.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) outList.Add(cur.ToString());
        return outList;
    }

    public static string Norm(string s) => string.Join(" ", Tokens(s));

    public static HashSet<string> TokenSet(string s) => new(Tokens(s), StringComparer.Ordinal);

    /// <summary>Length in characters as Python counts them (code points).</summary>
    public static int Len(string s) => s.EnumerateRunes().Count();

    /// <summary>Python's s[:n] (code points, not UTF-16 units).</summary>
    public static string Take(string s, int n)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (i++ >= n) break;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    public static bool IsDigits(string s) => s.Length > 0 && s.EnumerateRunes().All(Rune.IsDigit);
    public static bool IsAlpha(string s) => s.Length > 0 && s.EnumerateRunes().All(Rune.IsLetter);

    /// <summary>Python's int() on a digit string (any script's digits); null if too big.</summary>
    public static long? ToNumber(string s)
    {
        long n = 0;
        foreach (var r in s.EnumerateRunes())
        {
            var v = Rune.GetNumericValue(r);
            if (v < 0 || v > 9) return null;
            if (n > (long.MaxValue - 9) / 10) return null;
            n = n * 10 + (long)v;
        }
        return n;
    }

    /// <summary>Python's str.strip(chars).</summary>
    public static string Strip(string s, string chars) => s.Trim(chars.ToCharArray());

    /// <summary>A regex that behaves like Python's re for the simple patterns used here.</summary>
    public static Regex Re(string pattern, bool ignoreCase = false) =>
        new(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant);

    /// <summary>Python's re.split(pattern, s, maxsplit=1)[0].</summary>
    public static string BeforeFirst(Regex re, string s)
    {
        var m = re.Match(s);
        return m.Success ? s[..m.Index] : s;
    }

    public static string PyStrip(string s) => s.Trim();

    /// <summary>Python's str.zfill(width).</summary>
    public static string ZFill(string s, int width) => s.Length >= width ? s : new string('0', width - s.Length) + s;
}
