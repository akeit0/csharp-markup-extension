namespace Csmx.Compiler;

internal static class CsmxCompat
{
    public static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public static bool StartsWithAt(string text, int index, string value) =>
        index >= 0
        && index <= text.Length
        && index + value.Length <= text.Length
        && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    public static string ReplaceOrdinal(string text, string oldValue, string newValue) =>
        text.Replace(oldValue, newValue);

    public static IEnumerable<string> SplitAndTrim(string value, params char[] separators)
    {
        foreach (var part in (value ?? string.Empty).Split(separators))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
