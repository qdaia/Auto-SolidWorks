using System.Text.RegularExpressions;

namespace CadModeling.Drawing.Ingestion;

public sealed record OcrBenchmarkCase
{
    public string CaseId { get; init; } = string.Empty;
    public IReadOnlyList<string> ExpectedTokens { get; init; } = [];
    public IReadOnlyList<string> ActualTokens { get; init; } = [];
}

public sealed record OcrBenchmarkMetrics
{
    public int ExpectedTokenCount { get; init; }
    public int ActualTokenCount { get; init; }
    public int MatchedTokenCount { get; init; }
    public double Precision { get; init; }
    public double Recall { get; init; }
    public int CriticalExpected { get; init; }
    public int CriticalMatched { get; init; }
    public double CriticalSymbolPreservation { get; init; }
    public int NumericExpected { get; init; }
    public int NumericMatched { get; init; }
    public double NumericTokenPreservation { get; init; }
}

public static partial class OcrBenchmark
{
    private static readonly string[] CriticalMarkers = ["Ø", "Φ", "R", "THRU", "深度", "±"];

    public static OcrBenchmarkMetrics Evaluate(IEnumerable<OcrBenchmarkCase> cases)
    {
        var expected = cases.SelectMany(item => item.ExpectedTokens).Select(Normalize).Where(item => item.Length > 0).ToArray();
        var actual = cases.SelectMany(item => item.ActualTokens).Select(Normalize).Where(item => item.Length > 0).ToArray();
        var remaining = actual.GroupBy(item => item, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var matched = 0;
        foreach (var token in expected)
            if (remaining.TryGetValue(token, out var count) && count > 0)
            {
                matched++;
                remaining[token] = count - 1;
            }

        var critical = expected.Where(IsCritical).ToArray();
        var numeric = expected.Where(item => NumberToken().IsMatch(item)).ToArray();
        var criticalMatched = MultisetMatch(critical, actual);
        var numericMatched = MultisetMatch(numeric, actual);
        return new()
        {
            ExpectedTokenCount = expected.Length,
            ActualTokenCount = actual.Length,
            MatchedTokenCount = matched,
            Precision = Ratio(matched, actual.Length),
            Recall = Ratio(matched, expected.Length),
            CriticalExpected = critical.Length,
            CriticalMatched = criticalMatched,
            CriticalSymbolPreservation = Ratio(criticalMatched, critical.Length),
            NumericExpected = numeric.Length,
            NumericMatched = numericMatched,
            NumericTokenPreservation = Ratio(numericMatched, numeric.Length)
        };
    }

    private static int MultisetMatch(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var remaining = actual.GroupBy(item => item, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var matched = 0;
        foreach (var token in expected)
            if (remaining.TryGetValue(token, out var count) && count > 0)
            {
                matched++;
                remaining[token] = count - 1;
            }
        return matched;
    }

    private static bool IsCritical(string token) => CriticalMarkers.Any(marker => token.Contains(marker, StringComparison.Ordinal));
    private static string Normalize(string value) => string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 1d : numerator / (double)denominator;

    [GeneratedRegex(@"(?<![A-Z])[-+]?\d+(?:\.\d+)?(?:MM|IN)?(?![A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberToken();
}
