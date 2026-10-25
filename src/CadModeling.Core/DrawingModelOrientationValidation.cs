using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Checks signed source-ray to executed sketch-axis correspondence, not whole-shape equivalence.</summary>
public static class DrawingModelOrientationValidation
{
    public const string Revision = "source-model-direction-1";
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context, IEnumerable<(string Id, SketchFrame? Frame)> sketches)
    {
        var errors = new List<ModelingDiagnostic>();
        void Error(string code, string text) => errors.Add(new(code, DiagnosticSeverity.Error, text, "drawing_context.model_orientation"));
        var facts = context.Dimensions.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // Follow the executable expression graph, not the narrative premise list.
        HashSet<string> Angles(DimensionExpression? expression, HashSet<string> visiting)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (expression is null) return result;
            if (expression.Kind == DimensionExpressionKind.Reference && expression.DimensionId is { } id && facts.TryGetValue(id, out var f) && visiting.Add(id))
            {
                if (f.Status == DrawingFactStatus.Stated && f.Unit == DrawingValueUnit.Degree) result.Add(id);
                result.UnionWith(Angles(f.Expression, visiting));
                visiting.Remove(id);
            }
            foreach (var child in expression.Arguments) result.UnionWith(Angles(child, visiting));
            return result;
        }
        var required = new HashSet<(string Dimension, string Operation)>();
        foreach (var f in context.Dimensions.Where(f => f.ParameterPath.StartsWith("frame.x_direction.", StringComparison.Ordinal)))
            foreach (var id in Angles(f.Expression, [])) required.Add((id, f.OperationId));
        if (context.ModelOrientation is not { } orientation)
        {
            if (context.WorkflowRevision == SourceWorkflow.OrientedFourStageRevision && required.Count > 0)
                Error("SOURCE_MODEL_ORIENTATION_REQUIRED", "角度驱动的草图方向必须声明主视图右/上基向量，并将原图角度射线绑定到实际草图X轴，防止前后镜像。");
            return errors;
        }
        var right = orientation.FrontRight; var up = orientation.FrontUp;
        if (orientation.State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(orientation.Evidence) ||
            !Unit(right) || !Unit(up) || Math.Abs(Dot(right, up)) > 1e-6)
        { Error("SOURCE_MODEL_ORIENTATION_BASIS", "主视图右/上基向量必须为有限正交单位向量，并明确复核其对应的原图方向。"); return errors; }
        var toward = Cross(right, up);
        var frames = sketches.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Frame, StringComparer.Ordinal);
        var seen = new HashSet<(string Dimension, string Operation)>();
        foreach (var binding in orientation.DirectionBindings)
        {
            var key = (binding.DimensionId, binding.OperationId);
            if (!seen.Add(key) || !required.Contains(key) || !frames.TryGetValue(binding.OperationId, out var frame) || frame is null ||
                !facts.TryGetValue(binding.DimensionId, out var fact) || fact.BindingEvidence is not { QuantityKind: DrawingQuantityKind.Angle } evidence ||
                evidence.Trace is not { Kind: DrawingDimensionTraceKind.AngularDimension, AngularVertex: { } vertex, Path.Count: >= 2 } trace ||
                !context.Features.Any(f => f.Id == evidence.FeatureId && f.OperationIds.Contains(binding.OperationId)))
            { Error("SOURCE_MODEL_DIRECTION_BINDING", "方向绑定必须唯一，并引用实际驱动同一特征草图X轴的源角度及其原图射线。"); continue; }
            var view = context.Views.FirstOrDefault(v => v.Id == evidence.ViewId);
            (Vector3 R, Vector3 U)? basis = view?.Kind switch
            {
                ModelDrawingView.Front => (right, up), ModelDrawingView.Top => (right, Neg(toward)),
                ModelDrawingView.Left => (toward, up), ModelDrawingView.Right => (Neg(toward), up),
                ModelDrawingView.Bottom => (right, toward), ModelDrawingView.Rear => (Neg(right), up), _ => null
            };
            if (basis is null) { Error("SOURCE_MODEL_DIRECTION_VIEW", "方向映射需要标准正交视图；辅助/剖切视图须先建立受支持的正交来源绑定。"); continue; }
            try
            {
                var ray = DrawingDimensionTraceValidation.CalibratedRay(context, evidence.ViewId, vertex, binding.UseEndRay ? trace.Path[^1] : trace.Path[0]);
                var target = Add(Scale(basis.Value.R, ray.X), Scale(basis.Value.U, ray.Y));
                var actual = frame.XDirection;
                var norm = Math.Sqrt(Dot(target, target) * Dot(actual, actual));
                var cosine = Dot(target, actual) / norm;
                if (!double.IsFinite(cosine) || norm < 1e-9 || cosine < Math.Cos(2 * Math.PI / 180))
                    Error("SOURCE_MODEL_DIRECTION_MISMATCH", $"源角度 '{binding.DimensionId}' 射线与草图 '{binding.OperationId}' 的正X轴方向不一致；检查视图前后符号/镜像。带符号方向余弦={cosine:G6}，容差2度。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            { Error("SOURCE_MODEL_DIRECTION_SOURCE", ex.Message); }
        }
        foreach (var missing in required.Except(seen))
            Error("SOURCE_MODEL_DIRECTION_REQUIRED", $"源角度 '{missing.Dimension}' 驱动 '{missing.Operation}' 的草图X轴，但缺少原图射线方向绑定。");
        return errors;
    }
    private static double Dot(Vector3 a, Vector3 b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static bool Unit(Vector3? a) => a is not null && double.IsFinite(Dot(a,a)) && Math.Abs(Dot(a,a)-1)<1e-6;
    private static Vector3 Neg(Vector3 a) => Scale(a,-1);
    private static Vector3 Scale(Vector3 a,double s) => new(a.X*s,a.Y*s,a.Z*s);
    private static Vector3 Add(Vector3 a,Vector3 b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    private static Vector3 Cross(Vector3 a,Vector3 b) => new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
}
