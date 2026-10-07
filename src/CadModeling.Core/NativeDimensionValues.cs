using CadModeling.Ir;
namespace CadModeling.Core;

public enum NativeDimensionParameterKind { Length, Angle, Integer }

public static class NativeDimensionValues
{
    public static double ToSystemValue(double value, NativeDimensionParameterKind kind,
        DrawingValueUnit? unit = null, bool legacyAngle = false)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("尺寸值必须有限。");
        if (!Enum.IsDefined(kind) || unit is { } u && !Enum.IsDefined(u))
            throw new ArgumentException("未知参数类型或单位。");
        if (legacyAngle && unit is { } explicitUnit && explicitUnit != DrawingValueUnit.Degree)
            throw new ArgumentException("角度标志与显式单位冲突。");
        unit ??= legacyAngle ? DrawingValueUnit.Degree : kind switch
        {
            NativeDimensionParameterKind.Length => DrawingValueUnit.Millimeter,
            NativeDimensionParameterKind.Angle => DrawingValueUnit.Degree,
            _ => DrawingValueUnit.Unitless
        };
        return (kind, unit) switch
        {
            (NativeDimensionParameterKind.Length, DrawingValueUnit.Millimeter) when value >= 0 => value / 1000,
            (NativeDimensionParameterKind.Length, DrawingValueUnit.Meter) when value >= 0 => value,
            (NativeDimensionParameterKind.Length, DrawingValueUnit.Inch) when value >= 0 => value * 0.0254,
            (NativeDimensionParameterKind.Angle, DrawingValueUnit.Degree) => value / 180 * Math.PI,
            (NativeDimensionParameterKind.Integer, DrawingValueUnit.Unitless)
                when value >= 1 && value <= int.MaxValue && value == Math.Truncate(value) => value,
            _ => throw new ArgumentException("参数类型与单位不兼容，或长度/整数值不合法。整数参数必须保持无单位整数。")
        };
    }

    public static bool Matches(double actual, double expected, NativeDimensionParameterKind kind) =>
        double.IsFinite(actual) && double.IsFinite(expected) && (kind == NativeDimensionParameterKind.Integer
            ? actual == expected : Math.Abs(actual - expected) <= Math.Max(1e-10, Math.Abs(expected) * 1e-8));
}
