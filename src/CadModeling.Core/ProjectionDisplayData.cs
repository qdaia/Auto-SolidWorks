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
            var type = ExactInt(Read("type"), "多段线类型", minimum: 0);
            var geometryCount = ExactInt(Read("几何数据大小"), "多线段几何数据大小", minimum: 0);
            if (cursor + geometryCount > data.Count) throw new InvalidDataException("曲线记录在几何数据中被截断。");
            var geometry = Slice(data, cursor, geometryCount, "几何数据"); cursor += geometryCount;
            var color = Read("线颜色");
            var style = Read("线样式");
            var font = Read("线字体");
            var weight = Read("线宽");
            var layer = Read("层 ID");
            var layerOverride = Read("层覆盖");
            var pointCount = ExactInt(Read("点数"), "多段线点的数量", minimum: 2);
            checked
            {
                var coordinateCount = pointCount * 3;
                if (cursor + coordinateCount > data.Count) throw new InvalidDataException("多段线记录在点数据中被截断。");
                var points = Slice(data, cursor, coordinateCount, "点数据"); cursor += coordinateCount;
                if (geometry.Any(value => !double.IsFinite(value)) || points.Any(value => !double.IsFinite(value)))
                    throw new InvalidDataException("折线 几何/点数据包含非有限值。");
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
            if (cursor >= data.Count) throw new InvalidDataException($"多段线记录在{name}之前被截断。");
            var value = data[cursor++];
            if (!double.IsFinite(value)) throw new InvalidDataException($"多段线{name}是 非有限的。");
            return value;
        }
    }

    private static IReadOnlyList<double> Slice(IReadOnlyList<double> data, int start, int count, string name)
    {
        if (start < 0 || count < 0 || start > data.Count - count) throw new InvalidDataException($"多段线{name}范围无效。");
        var values = new double[count];
        for (var i = 0; i < count; i++) values[i] = data[start + i];
        return values;
    }

    private static int ExactInt(double value, string name, int minimum)
    {
        if (!double.IsFinite(value) || Math.Abs(value - Math.Round(value)) > 1e-9 || value < minimum || value > int.MaxValue)
            throw new InvalidDataException($"{name}不是一个有界的整数 >={minimum}。");
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
