using System.Globalization;
using System.Text.RegularExpressions;
using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Bounded grammar for AI-transcribed literals. Raw UTF-16 spans are preserved; no OCR/inferred values.</summary>
public static class HoleCalloutParser
{
    public const string Revision = "hole-callout-2";
    public sealed record Component(string Key, string Role, int Start, int Length, string Literal,
        double? Value, DrawingQuantityKind? QuantityKind, DrawingValueUnit Unit);
    public sealed record Result(string SourceLiteral, bool Complete, IReadOnlyList<Component> Components, string UnresolvedSuffix);
    private const string N = @"(?:\d+(?:[.,]\d+)?|[.,]\d+)";
    private const string D = @"[Ø⌀φΦ∅]";
    public static Result Parse(string raw, DrawingValueUnit lengthUnit = DrawingValueUnit.Millimeter)
    {
        if (raw is null || raw.Length > 4096) return new(raw ?? "", false, [], raw ?? "");
        var result = new List<Component>(); var at = 0; var stage = "bore"; var thread = false;
        Match Match(string pattern) => Regex.Match(raw[at..], @"^\s*(?:" + pattern + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        void Add(Match m, string group, string role, DrawingQuantityKind? kind = null, DrawingValueUnit? unit = null)
        {
            var g = m.Groups[group]; if (!g.Success) return;
            double? value = kind is null ? null : double.TryParse(g.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
            result.Add(new(role, role, at + g.Index, g.Length, g.Value, value, kind, unit ?? lengthUnit));
        }
        void Consume(Match m) => at += m.Length;
        var count = Match(@"(?<count>\d+)\s*[-x×]\s*(?=" + D + "|R|M|C)");
        if (count.Success) { Add(count, "count", "count", DrawingQuantityKind.Count, DrawingValueUnit.Unitless); Consume(count); }
        var head = Match(@"(?:(?<symbol>" + D + @"|R|M|C)\s*)?(?<value>" + N + @")(?<degree>[°º])?(?:\s*(?<unit>mm|in|m)\b)?");
        if (!head.Success) return new(raw, false, result, raw[at..]);
        var symbol = head.Groups["symbol"].Value.ToUpperInvariant(); thread = symbol == "M"; if (symbol == "C") stage = "chamfer";
        var explicitUnit = head.Groups["unit"].Value.ToLowerInvariant();
        if (explicitUnit.Length > 0) lengthUnit = explicitUnit == "in" ? DrawingValueUnit.Inch : explicitUnit == "m" ? DrawingValueUnit.Meter : DrawingValueUnit.Millimeter;
        var role = thread ? "thread_major_diameter" : symbol == "C" ? "chamfer_distance" : symbol == "R" ? "radius" : symbol.Length > 0 ? "bore_diameter" : head.Groups["degree"].Success ? "angle" : "linear";
        Add(head, "value", role, role == "angle" ? DrawingQuantityKind.Angle : role is "linear" or "chamfer_distance" ? DrawingQuantityKind.Linear : role == "radius" ? DrawingQuantityKind.Radius : DrawingQuantityKind.Diameter,
            role == "angle" ? DrawingValueUnit.Degree : lengthUnit); Consume(head);
        for (var i = 0; i < 32 && !string.IsNullOrWhiteSpace(raw[at..]); i++)
        {
            Match m;
            if (thread && (m = Match(@"[x×]\s*(?<pitch>" + N + @")")).Success)
            { Add(m, "pitch", "thread_pitch", DrawingQuantityKind.ThreadPitch); Consume(m); continue; }
            if (thread && (m = Match(@"-\s*(?<class>\d+[HhGg])\b")).Success)
            { Add(m, "class", "thread_class"); Consume(m); continue; }
            if ((m = Match(@"(?<tolerance>(?:±|\+/-)\s*" + N + @"|[+-]\s*" + N + @"\s*/\s*[+-]\s*" + N + @")")).Success)
            { Add(m, "tolerance", stage + "_tolerance"); Consume(m); continue; }
            if ((m = Match(@"(?<fit>[A-Z]{1,2}\d{1,2})\b")).Success)
            { Add(m, "fit", stage + "_fit"); Consume(m); continue; }
            if ((m = Match(@"(?<through>THRU(?:\s+ALL)?|THROUGH(?:\s+ALL)?|通孔|贯通)\b")).Success)
            { Add(m, "through", stage + "_through"); Consume(m); continue; }
            if ((m = Match(@"(?:↧|⌵DEPTH|DEPTH\b|深)\s*(?<depth>" + N + @")")).Success)
            { Add(m, "depth", thread && stage == "bore" ? "thread_depth" : stage + "_depth", DrawingQuantityKind.Linear); Consume(m); continue; }
            if ((m = Match(@"(?<mode>⌴|CBORE\b|COUNTERBORE\b|SPOTFACE\b|锪平|沉孔)\s*" + D + @"\s*(?<diameter>" + N + @")\s*(?<sf>SF\b)?")).Success)
            {
                stage = m.Groups["sf"].Success || m.Groups["mode"].Value is "锪平" || m.Groups["mode"].Value.Equals("SPOTFACE", StringComparison.OrdinalIgnoreCase) ? "spotface" : "counterbore";
                Add(m, "mode", stage + "_mode"); Add(m, "diameter", stage + "_diameter", DrawingQuantityKind.Diameter); Add(m, "sf", "spotface_flag"); Consume(m); continue;
            }
            if ((m = Match(@"(?<mode>⌵|CSK\b|COUNTERSINK\b|锪孔)\s*" + D + @"\s*(?<diameter>" + N + @")\s*[x×]\s*(?<angle>" + N + @")\s*[°º]")).Success)
            { stage = "countersink"; Add(m, "mode", "countersink_mode"); Add(m, "diameter", "countersink_diameter", DrawingQuantityKind.Diameter); Add(m, "angle", "countersink_angle", DrawingQuantityKind.Angle, DrawingValueUnit.Degree); Consume(m); continue; }
            if ((m = Match(@"(?<spacing>EQUALLY\s+SPACED\s+ON|EQ\.?\s*SP\.?\s+ON|均布于)\s*" + D + @"\s*(?<pcd>" + N + @")\s*(?:PCD|B\.?C\.?)\b")).Success)
            { Add(m, "spacing", "equal_spacing"); Add(m, "pcd", "pitch_circle_diameter", DrawingQuantityKind.Diameter); Consume(m); continue; }
            break;
        }
        var unique = !(symbol == "C" && (head.Groups["degree"].Success || result.Any(c => c.Role is not ("count" or "chamfer_distance" or "chamfer_tolerance")))) && result.Select(c => c.Key).Distinct().Count() == result.Count;
        var valid = result.All(c => c.QuantityKind is null || c.Value is > 0 || c.Role is "linear" or "angle" && c.Value == 0) && !result.Any(c => c.Role == "count" && c.Value != Math.Truncate(c.Value!.Value));
        return new(raw, unique && valid && (!thread || lengthUnit == DrawingValueUnit.Millimeter) && string.IsNullOrWhiteSpace(raw[at..]), result, raw[at..]);
    }
}
