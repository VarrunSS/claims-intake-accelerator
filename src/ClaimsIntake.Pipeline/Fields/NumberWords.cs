namespace ClaimsIntake.Pipeline.Fields;

/// <summary>
/// Reads an English amount written in words, "twelve thousand five hundred", up to 999,999. It is deliberately small:
/// numbers up to the hundreds of thousands cover claim amounts, and anything it does not understand is not guessed at.
/// </summary>
internal static class NumberWords
{
    private static readonly Dictionary<string, int> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>A regex alternation of every word the parser accepts, longest first.</summary>
    public static readonly string Pattern = string.Join('|', Small.Keys.Concat(["hundred", "thousand", "and"]).OrderByDescending(w => w.Length));

    public static bool TryParse(string text, out int value)
    {
        value = 0;
        var tokens = text.Split([' ', '-', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var total = 0;
        var current = 0;
        var sawNumber = false;
        foreach (var token in tokens)
        {
            if (token.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Small.TryGetValue(token, out var n))
            {
                current += n;
                sawNumber = true;
            }
            else if (token.Equals("hundred", StringComparison.OrdinalIgnoreCase) && current > 0)
            {
                current *= 100;
            }
            else if (token.Equals("thousand", StringComparison.OrdinalIgnoreCase) && current > 0)
            {
                total += current * 1000;
                current = 0;
            }
            else
            {
                return false;
            }
        }

        value = total + current;
        return sawNumber && value > 0;
    }
}
