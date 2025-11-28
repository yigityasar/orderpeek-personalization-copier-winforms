using System.Globalization;
using System.Text.RegularExpressions;

public static class NameFormatter
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TextInfo Ti = Tr.TextInfo;
    private static readonly Regex LetterSeq = new(@"[\p{L}]+", RegexOptions.Compiled);

    public static string FormatCustomerName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = (i == 0)
                ? TitleCaseSmart(parts[i])
                : (IsAllUpper(parts[i]) ? Ti.ToUpper(parts[i]) : TitleCaseSmart(parts[i]));
        }

        return string.Join(' ', parts);
    }

    private static bool IsAllUpper(string word)
    {
        bool hasLetter = LetterSeq.IsMatch(word);
        return hasLetter && word == Ti.ToUpper(word);
    }

    private static string TitleCaseSmart(string word)
    {
        return LetterSeq.Replace(word, m =>
        {
            var s = m.Value;
            if (s.Length == 1) return Ti.ToUpper(s);

            var lower = Ti.ToLower(s);
            return Ti.ToUpper(lower[..1]) + lower[1..];
        });
    }
}
