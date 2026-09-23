namespace CadModeling.Core;

/// <summary>Raw display record emitted by SOLIDWORKS IView.GetPolylines7, before CAD-specific style-name lookup.</summary>
public sealed record ProjectionDisplayPolylineRecord
{
    public int Type { get; init; }
    public IReadOnlyList<double> GeometryData { get; init; } = [];
    public double LineColorToken { get; init; }
    public double LineStyleToken { get; init; }
    public double LineFontToken { get; init; }
    public double LineWeightToken { get; init; }
    public double LayerIdToken { get; init; }
    public double LayerOverrideToken { get; init; }
    public IReadOnlyList<double> PointsXyz { get; init; } = [];
    public int PointCount => PointsXyz.Count / 3;
}

public static class ProjectionDisplayPolylineParser
{
    public static IReadOnlyList<ProjectionDisplayPolylineRecord> Parse(IReadOnlyList<double> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new List<ProjectionDisplayPolylineRecord>();
        var cursor = 0;
        while (cursor < data.Count)
        {
            var type = ExactInt(Read("type"), "polyline type", minimum: 0);
            var geometryCount = ExactInt(Read("geometry-data size"), "polyline geometry-data size", minimum: 0);
            if (cursor + geometryCount > data.Count) throw new InvalidDataException("Polyline record is truncated in geometry data.");
            var geometry = Slice(data, cursor, geometryCount, "geometry data"); cursor += geometryCount;
            var color = Read("line color");
            var style = Read("line style");
            var font = Read("line font");
            var weight = Read("line weight");
            var layer = Read("layer id");
            var layerOverride = Read("layer override");
            var pointCount = ExactInt(Read("point count"), "polyline point count", minimum: 2);
            checked
            {
                var coordinateCount = pointCount * 3;
                if (cursor + coordinateCount > data.Count) throw new InvalidDataException("Polyline record is truncated in point data.");
                var points = Slice(data, cursor, coordinateCount, "point data"); cursor += coordinateCount;
                if (geometry.Any(value => !double.IsFinite(value)) || points.Any(value => !double.IsFinite(value)))
                    throw new InvalidDataException("Polyline geometry/point data contains non-finite values.");
                result.Add(new()
                {
                    Type = type,
                    GeometryData = geometry,
                    LineColorToken = color,
                    LineStyleToken = style,
                    LineFontToken = font,
                    LineWeightToken = weight,
                    LayerIdToken = layer,
                    LayerOverrideToken = layerOverride,
                    PointsXyz = points
                });
            }
        }
        return result;

        double Read(string name)
        {
            if (cursor >= data.Count) throw new InvalidDataException($"Polyline record is truncated before {name}.");
            var value = data[cursor++];
            if (!double.IsFinite(value)) throw new InvalidDataException($"Polyline {name} is non-finite.");
            return value;
        }
    }

    private static IReadOnlyList<double> Slice(IReadOnlyList<double> data, int start, int count, string name)
    {
        if (start < 0 || count < 0 || start > data.Count - count) throw new InvalidDataException($"Polyline {name} range is invalid.");
        var values = new double[count];
        for (var i = 0; i < count; i++) values[i] = data[start + i];
        return values;
    }

    private static int ExactInt(double value, string name, int minimum)
    {
        if (!double.IsFinite(value) || Math.Abs(value - Math.Round(value)) > 1e-9 || value < minimum || value > int.MaxValue)
            throw new InvalidDataException($"{name} is not a bounded integer >= {minimum}.");
        return (int)Math.Round(value);
    }
}

public static class ProjectionLineStyleResolver
{
    /// <summary>
    /// Implements the GetPolylines7 token contract: LineStyle != -1 uses GetLineFontName(LineStyle);
    /// LineStyle == -1 uses the manual LineFont token with GetLineFontName2(LineFont).
    /// </summary>
    public static string? Resolve(double lineStyleToken, double lineFontToken,
        Func<int, string?> styleNameLookup, Func<int, string?> manualFontNameLookup)
    {
        ArgumentNullException.ThrowIfNull(styleNameLookup);
        ArgumentNullException.ThrowIfNull(manualFontNameLookup);
        if (!double.IsFinite(lineStyleToken) || Math.Abs(lineStyleToken - Math.Round(lineStyleToken)) > 1e-9) return null;
        var style = (int)Math.Round(lineStyleToken);
        string? name;
        if (style == -1)
        {
            if (!TryToken(lineFontToken, out var font)) return null;
            name = manualFontNameLookup(font);
        }
        else
        {
            if (style < 0) return null;
            name = styleNameLookup(style);
        }
        return Classify(name);
    }

    public static string? Classify(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;
        var normalized = new string(rawName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized switch
        {
            "visible" or "visibleedge" or "visibleedges" => "visible",
            "hidden" or "hiddenedge" or "hiddenedges" => "hidden",
            "center" or "centre" or "centerline" or "centreline" or "centerlines" or "centrelines" => "center",
            _ => null
        };
    }

    private static bool TryToken(double value, out int token)
    {
        token = default;
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || Math.Abs(value - Math.Round(value)) > 1e-9) return false;
        token = (int)Math.Round(value);
        return true;
    }
}
