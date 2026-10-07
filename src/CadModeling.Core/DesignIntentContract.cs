using System.Globalization;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record CompiledDesignEquation(string Target, string Equation, bool GlobalVariable,
    bool ReplaceExisting, DesignValue Value);
public sealed record CompiledDesignIntent(DesignIntentSpec Spec, IReadOnlyList<CompiledDesignEquation> Equations,
    IReadOnlyDictionary<string,IReadOnlyList<CompiledDesignEquation>>? ConfigurationEquations=null,
    bool RequiresDegreeEquationUnits=false, bool RequiresAutomaticSolveOrder=false)
{
    public IReadOnlyList<CompiledDesignEquation> ForConfiguration(string name)=>
        ConfigurationEquations is not null && ConfigurationEquations.TryGetValue(name,out var equations)?equations:Equations;
}

public static partial class DesignIntentContract
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(DesignIntentSpec? spec)
    {
        if (spec is null) return [];
        try { Compile(spec); return []; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        { return [new("DESIGN_INTENT_INVALID", DiagnosticSeverity.Error, ex.Message, "design_intent")]; }
    }

    public static bool Close(double actual, double expected) => double.IsFinite(actual) && double.IsFinite(expected)
        && Math.Abs(actual - expected) <= Math.Max(1e-10, Math.Abs(expected) * 1e-8);
    public static string EquationTarget(string equation)
    {
        var text = equation.TrimStart(); var end = text.StartsWith('"') ? text.IndexOf('"', 1) : -1;
        Require(end > 1 && text[(end + 1)..].TrimStart().StartsWith('='), "无法确认原生方程的目标身份。");
        return text[1..end];
    }
    // Whitespace inside a quoted native identifier is significant.
    public static string NormalizeEquation(string equation)
    {
        bool quoted = false; var text = new System.Text.StringBuilder();
        foreach (var ch in equation) { if (ch == '"') quoted = !quoted; if (quoted || !char.IsWhiteSpace(ch)) text.Append(ch); }
        return text.ToString();
    }
    public static DrawingValueUnit Unit(NativeDimensionParameterKind kind) => kind switch
    { NativeDimensionParameterKind.Length => DrawingValueUnit.Millimeter, NativeDimensionParameterKind.Angle => DrawingValueUnit.Degree,
        NativeDimensionParameterKind.Integer => DrawingValueUnit.Unitless, _ => throw new ArgumentException("未知参数类型。") };
    private static string Quote(string value) => "\"" + value + "\"";
    private static void DimensionName(string name) => Require(name.Split('@') is [var a, var b] && ValidName(a) && ValidName(b),
        "尺寸路径必须为准确的 参数名@特征名。");
    private static void CheckValue(DesignValue? value) => Require(value is not null && double.IsFinite(value.Value)
        && value.Unit is DrawingValueUnit.Millimeter or DrawingValueUnit.Degree or DrawingValueUnit.Unitless,
        "仅支持有限的 mm、Degree、Unitless 数值。");
    private static void CheckDimensionValue(DesignValue value)
    {
        if (value.Unit == DrawingValueUnit.Unitless)
            NativeDimensionValues.ToSystemValue(value.Value, NativeDimensionParameterKind.Integer, value.Unit);
    }
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && name == name.Trim()
        && !name.Any(c => char.IsControl(c) || c is '"' or '=' or '\\');
    private static Dictionary<string, T> ToDictionaryChecked<T>(this IEnumerable<T> items, Func<T, string> key, string label)
    {
        var exact = new Dictionary<string, T>(StringComparer.Ordinal); var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            Require(item is not null && ValidName(key(item)), label + "名称无效。"); var name = key(item);
            Require(folded.Add(name), label + "重复或存在大小写歧义：" + name); exact.Add(name, item);
        }
        return exact;
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new ArgumentException("DESIGN_INTENT: " + message); }
}
