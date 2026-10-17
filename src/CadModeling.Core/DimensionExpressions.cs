using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>Single calculation engine for MCP normalization and immutable IR validation.</summary>
public static class DimensionExpressions
{
    public const string Revision = "dimension-expressions-1";
    private readonly record struct Quantity(double Value, int Family);
    private static InvalidDataException Error(string code, string detail) => new(code + ": " + detail);
    public static int Family(DrawingValueUnit unit) => unit switch
    {
        DrawingValueUnit.Millimeter or DrawingValueUnit.Inch or DrawingValueUnit.Meter => 1,
        DrawingValueUnit.Degree => 2, DrawingValueUnit.Unitless => 0,
        _ => throw Error("DIMENSION_EXPRESSION_UNIT", "未知单位。")
    };
    public static double Scale(DrawingValueUnit unit) => unit switch
    {
        DrawingValueUnit.Inch => 25.4, DrawingValueUnit.Meter => 1000,
        DrawingValueUnit.Millimeter or DrawingValueUnit.Degree or DrawingValueUnit.Unitless => 1,
        _ => throw Error("DIMENSION_EXPRESSION_UNIT", "未知单位。")
    };
    public static bool Equal(double a, double b) => double.IsFinite(a) && double.IsFinite(b)
        && Math.Abs(a-b) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    public static IReadOnlySet<string> References(DimensionExpression expression)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(DimensionExpression Node, int Depth)>();
        pending.Push((expression, 0)); var count = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > 256 || item.Depth > 32) throw Error("DIMENSION_EXPRESSION_LIMIT", "表达式最多 256 节点、32 层。");
            var node = item.Node;
            if (node is null || !Enum.IsDefined(node.Kind) || node.Arguments is null)
                throw Error("DIMENSION_EXPRESSION_SHAPE", "表达式节点无效。");
            if (node.Kind == DimensionExpressionKind.Reference)
            {
                if (string.IsNullOrWhiteSpace(node.DimensionId) || node.Value is not null || node.Unit is not null || node.Arguments.Count != 0)
                    throw Error("DIMENSION_EXPRESSION_SHAPE", "Reference 只允许 dimension_id。");
                ids.Add(node.DimensionId);
            }
            else if (node.Kind == DimensionExpressionKind.Constant)
            {
                if (node.DimensionId is not null || node.Value is not { } value || !double.IsFinite(value) || node.Unit is null || node.Arguments.Count != 0)
                    throw Error("DIMENSION_EXPRESSION_SHAPE", "Constant 需要有限 value 和明确 unit。");
                _ = Family(node.Unit.Value);
            }
            else
            {
                if (node.DimensionId is not null || node.Value is not null || node.Unit is not null ||
                    node.Arguments.Count != (node.Kind == DimensionExpressionKind.Negate ? 1 : 2))
                    throw Error("DIMENSION_EXPRESSION_SHAPE", "运算符只能包含正确数量的 arguments。");
                foreach (var arg in node.Arguments) pending.Push((arg, item.Depth+1));
            }
        }
        return ids;
    }

    private static Quantity Evaluate(DimensionExpression node, IReadOnlyDictionary<string, DrawingDimensionFact> facts)
    {
        Quantity Checked(double value, int family)
        {
            if (!double.IsFinite(value)) throw Error("DIMENSION_EXPRESSION_NONFINITE", "运算溢出或无效。");
            return new(value, family);
        }
        if (node.Kind == DimensionExpressionKind.Constant) return Checked(node.Value!.Value * Scale(node.Unit!.Value), Family(node.Unit.Value));
        if (node.Kind == DimensionExpressionKind.Reference)
        {
            if (!facts.TryGetValue(node.DimensionId!, out var f)) throw Error("DIMENSION_EXPRESSION_MISSING", node.DimensionId!);
            if (f.Status is not (DrawingFactStatus.Stated or DrawingFactStatus.Derived) || f.NominalValue is not null)
                throw Error("DIMENSION_EXPRESSION_PREMISE", f.Id + " 前提未确认或含授权执行偏差，须明确拆分标称与执行量。");
            return Checked(f.Value * Scale(f.Unit), Family(f.Unit));
        }
        var a = Evaluate(node.Arguments[0], facts);
        if (node.Kind == DimensionExpressionKind.Negate) return Checked(-a.Value, a.Family);
        var b = Evaluate(node.Arguments[1], facts);
        switch (node.Kind)
        {
            case DimensionExpressionKind.Add:
            case DimensionExpressionKind.Subtract:
                if (a.Family != b.Family) throw Error("DIMENSION_EXPRESSION_UNIT", "加减量纲不一致。");
                return Checked(node.Kind == DimensionExpressionKind.Add ? a.Value+b.Value : a.Value-b.Value, a.Family);
            case DimensionExpressionKind.Multiply:
                if (a.Family != 0 && b.Family != 0) throw Error("DIMENSION_EXPRESSION_UNIT", "乘法需要至少一个无量纲因子；暂不支持面积/体积。");
                return Checked(a.Value*b.Value, a.Family == 0 ? b.Family : a.Family);
            case DimensionExpressionKind.Divide:
                if (b.Value == 0) throw Error("DIMENSION_EXPRESSION_ZERO", "除数不能为零。");
                if (b.Family != 0 && a.Family != b.Family) throw Error("DIMENSION_EXPRESSION_UNIT", "除法量纲不支持。");
                return Checked(a.Value/b.Value, b.Family == 0 ? a.Family : 0);
            default: throw Error("DIMENSION_EXPRESSION_SHAPE", "未知运算。");
        }
    }

    public static DrawingPlanContext Resolve(DrawingPlanContext context)
    {
        if (context.Dimensions.Count > 10000 || context.DimensionEquations.Count > 10000)
            throw Error("DIMENSION_EXPRESSION_LIMIT", "尺寸或关系数量超限。");
        var facts = new Dictionary<string, DrawingDimensionFact>(StringComparer.Ordinal);
        foreach (var fact in context.Dimensions)
            if (string.IsNullOrWhiteSpace(fact.Id) || !facts.TryAdd(fact.Id, fact)) throw Error("DIMENSION_EXPRESSION_ID", "尺寸 ID 必须非空且唯一。");
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var fact in context.Dimensions)
        {
            var refs = fact.Expression is null ? fact.DerivedFromDimensionIds.ToHashSet(StringComparer.Ordinal) : References(fact.Expression).ToHashSet(StringComparer.Ordinal);
            if (fact.Expression is not null)
            {
                if (fact.Status != DrawingFactStatus.Derived || fact.DerivationKind != DrawingDerivationKind.Arithmetic || fact.NominalValue is not null || refs.Count == 0)
                    throw Error("DIMENSION_EXPRESSION_STATUS", fact.Id + " 公式必须为无偏差的算术派生量且引用源尺寸。");
                if (fact.DerivedFromDimensionIds.Count > 0 && (fact.DerivedFromDimensionIds.Count != refs.Count || !refs.SetEquals(fact.DerivedFromDimensionIds)))
                    throw Error("DIMENSION_EXPRESSION_DEPENDENCIES", fact.Id + " 声明前提与公式不一致。");
                facts[fact.Id] = fact with { DerivedFromDimensionIds = refs.Order(StringComparer.Ordinal).ToArray() };
            }
            foreach (var id in refs) if (!facts.ContainsKey(id)) throw Error("DIMENSION_EXPRESSION_MISSING", fact.Id + " -> " + id);
            dependencies[fact.Id] = refs;
        }
        var dependents = facts.Keys.ToDictionary(id=>id, _=>new List<string>(), StringComparer.Ordinal);
        foreach (var pair in dependencies) foreach (var id in pair.Value) dependents[id].Add(pair.Key);
        var ready = new Queue<string>(dependencies.Where(p=>p.Value.Count==0).Select(p=>p.Key));
        var processed = 0;
        while (ready.TryDequeue(out var id))
        {
            var f = facts[id]; processed++;
            if (f.Expression is { } expression)
            {
                var q = Evaluate(expression, facts);
                if (q.Family != Family(f.Unit)) throw Error("DIMENSION_EXPRESSION_UNIT", id + " 公式结果与尺寸单位不一致。");
                facts[id] = f with { Value = q.Value / Scale(f.Unit) };
            }
            foreach (var child in dependents[id]) if (dependencies[child].Remove(id) && dependencies[child].Count==0) ready.Enqueue(child);
        }
        if (processed != facts.Count) throw Error("DIMENSION_EXPRESSION_CYCLE", "派生尺寸存在循环。");
        var equationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var equation in context.DimensionEquations)
        {
            if (string.IsNullOrWhiteSpace(equation.Id) || !equationIds.Add(equation.Id) || string.IsNullOrWhiteSpace(equation.SourceExplanation))
                throw Error("DIMENSION_EQUATION_ID", "尺寸链需要唯一 ID 和来源解释。");
            _ = References(equation.Left); _ = References(equation.Right);
            var a = Evaluate(equation.Left, facts); var b = Evaluate(equation.Right, facts);
            if (a.Family != b.Family || !Equal(a.Value,b.Value)) throw Error("DIMENSION_EQUATION_CONFLICT", equation.Id + " 尺寸链不闭合；回查源图，不能修改标称值凑合。");
        }
        return context with { Dimensions = context.Dimensions.Select(f=>facts[f.Id]).ToArray() };
    }

    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        if (!context.Dimensions.Any(f=>f.Expression is not null) && context.DimensionEquations.Count==0) return [];
        try
        {
            var resolved = Resolve(context);
            return context.Dimensions.Zip(resolved.Dimensions).Where(p=>p.First.Expression is not null &&
                    (!Equal(p.First.Value,p.Second.Value) || !p.First.DerivedFromDimensionIds.ToHashSet(StringComparer.Ordinal).SetEquals(p.Second.DerivedFromDimensionIds)))
                .Select(p=>new ModelingDiagnostic("DIMENSION_EXPRESSION_STALE", DiagnosticSeverity.Error,
                    "公式计算值或依赖已过期，重新编译输入。", "drawing_context.dimensions."+p.First.Id)).ToArray();
        }
        catch (InvalidDataException e)
        {
            return [new(e.Message.Split(':')[0], DiagnosticSeverity.Error, e.Message, "drawing_context.dimensions")];
        }
    }
}
