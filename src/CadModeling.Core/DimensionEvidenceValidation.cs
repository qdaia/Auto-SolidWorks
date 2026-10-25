using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>Checks declared attachments and dependencies; does not infer source geometry.</summary>
public static partial class DimensionEvidenceValidation
{
    public const string Revision = "dimension-evidence-4";
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors = new List<ModelingDiagnostic>();
        errors.AddRange(SourceWorkflow.Validate(context));
        errors.AddRange(DimensionExpressions.Validate(context));
        errors.AddRange(DrawingGeometryAnchorValidation.Validate(context));
        errors.AddRange(DrawingMeasurementIdentityValidation.Validate(context));
        errors.AddRange(DrawingDimensionTraceValidation.Validate(context));
        errors.AddRange(DrawingStageValidation.Validate(context));
        errors.AddRange(DimensionCalloutValidation.Assess(context).SelectMany(a => a.Diagnostics));
        errors.AddRange(SourceAnnotationValidation.Validate(context));
        void Error(string code, DrawingDimensionFact f, string message) =>
            errors.Add(new(code, DiagnosticSeverity.Error, message, "drawing_context.dimensions." + f.Id));
        // Duplicate IDs are diagnosed by the parent validator; never throw while collecting other issues.
        var facts = context.Dimensions.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var critical = context.Features.Where(f => f.Critical).SelectMany(f => f.CriticalParameters).Select(b => b.DimensionId).ToHashSet(StringComparer.Ordinal);
        var geometryObservations = new HashSet<string>(StringComparer.Ordinal);
        if (context.Dimensions.Any(f => f.DerivationKind == DrawingDerivationKind.Geometry) && SourceWorkflow.Evidence(context) is { } omission)
        {
            try
            {
                var inventory = DrawingOmissionValidation.Load(omission.InventoryPath, omission.InventorySha256);
                if (string.Equals(Path.GetFullPath(inventory.SourcePath), Path.GetFullPath(context.SourcePath), StringComparison.OrdinalIgnoreCase))
                    geometryObservations.UnionWith(inventory.Candidates.Where(c => c.Kind == DrawingCandidateKind.Geometry).SelectMany(c => c.ObservationIds));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            { /* The original omission validator reports integrity details; no geometric premise is accepted here. */ }
        }
        foreach (var f in context.Dimensions)
        {
            var required = context.RequireDimensionEvidence && (f.Critical || critical.Contains(f.Id));
            if (string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.ParameterPath))
                Error("DIMENSION_IDENTITY", f, "尺寸必须有非空 ID 和参数路径。");
            if (required && f.BindingEvidence is null)
                Error("DIMENSION_EVIDENCE_REQUIRED", f, "记录原图文字区域、所指对象、尺寸基准和引线/端点证据；数值相等不能证明绑定正确。");
            if (f.BindingEvidence is { } e)
            {
                var feature = context.Features.FirstOrDefault(x => x.Id == e.FeatureId);
                if (feature is null || !feature.OperationIds.Contains(f.OperationId) || !feature.ViewIds.Contains(e.ViewId) ||
                    !f.ViewIds.Contains(e.ViewId) || !context.Views.Any(v => v.Id == e.ViewId) ||
                    critical.Contains(f.Id) && !feature.CriticalParameters.Any(b => b.DimensionId == f.Id && b.OperationId == f.OperationId && b.ParameterPath == f.ParameterPath) ||
                    string.IsNullOrWhiteSpace(e.MeasurementId) || !Enum.IsDefined(e.QuantityKind) ||
                    !DrawingOmissionValidation.ValidBox(e.TextBounds) || !DrawingOmissionValidation.ValidBox(e.TargetBounds) ||
                    string.IsNullOrWhiteSpace(e.AttachmentExplanation) || string.IsNullOrWhiteSpace(e.Datum))
                    Error("DIMENSION_EVIDENCE_INVALID", f, "绑定证据需要所属特征的真实视图、唯一测量身份、有效原图区域、基准及引线解释。");
                if (e.QuantityKind is DrawingQuantityKind.Linear or DrawingQuantityKind.Angle &&
                    (string.IsNullOrWhiteSpace(e.StartReference) || string.IsNullOrWhiteSpace(e.EndReference) || e.StartReference.Trim() == e.EndReference.Trim()))
                    Error("DIMENSION_ENDPOINTS_REQUIRED", f, "线性/角度尺寸须分别说明两个不同的界线、面、中心线或射线；不要按文字距离选择对象。");
                var length = e.QuantityKind is DrawingQuantityKind.Linear or DrawingQuantityKind.Radius or DrawingQuantityKind.Diameter or DrawingQuantityKind.ThreadPitch;
                if (length && f.Unit is DrawingValueUnit.Degree or DrawingValueUnit.Unitless ||
                    e.QuantityKind == DrawingQuantityKind.Angle && f.Unit != DrawingValueUnit.Degree ||
                    e.QuantityKind == DrawingQuantityKind.Count && (f.Unit != DrawingValueUnit.Unitless || f.Value < 1 || f.Value != Math.Truncate(f.Value)))
                    Error("DIMENSION_QUANTITY_UNIT", f, "尺寸含义、单位或整数数量不一致。");
                var leaf = f.ParameterPath.Split('.').LastOrDefault() ?? "";
                if (leaf.Contains("radius", StringComparison.Ordinal) && e.QuantityKind != DrawingQuantityKind.Radius ||
                    leaf.Contains("diameter", StringComparison.Ordinal) && e.QuantityKind != DrawingQuantityKind.Diameter ||
                    leaf.EndsWith("_degrees", StringComparison.Ordinal) && e.QuantityKind != DrawingQuantityKind.Angle ||
                    (leaf == "count" || leaf.EndsWith("_count", StringComparison.Ordinal)) && e.QuantityKind != DrawingQuantityKind.Count)
                    Error("DIMENSION_TARGET_KIND", f, "量的含义与目标字段不符；半径/直径换算须建立显式派生尺寸。");
            }
            // Only exact, unambiguous simple literals are interpreted here. Complex callouts stay in visual review.
            var sourceSymbol = SourceSymbol().Match(f.SourceLiteral.Trim()).Groups["symbol"].Value.ToUpperInvariant();
            var targetLeaf = f.ParameterPath.Split('.').LastOrDefault() ?? "";
            if (f.Status == DrawingFactStatus.Stated &&
                (sourceSymbol == "R" && (targetLeaf.Contains("diameter", StringComparison.Ordinal) || f.BindingEvidence is { QuantityKind: DrawingQuantityKind.Diameter }) ||
                 sourceSymbol is "Ø" or "⌀" or "Φ" or "∅" && (targetLeaf.Contains("radius", StringComparison.Ordinal) || f.BindingEvidence is { QuantityKind: DrawingQuantityKind.Radius })))
                Error("DIMENSION_LITERAL_KIND", f, "原标注的半径/直径符号与绑定量相反；数量、公差后缀不能改变其含义。");
            if (f.DerivedFromDimensionIds.Count > 0 && f.Status != DrawingFactStatus.Derived)
                Error("DIMENSION_DEPENDENCY_STATUS", f, "只有派生尺寸可以声明推导前提。");
            if (!Enum.IsDefined(f.DerivationKind)) Error("DIMENSION_DEPENDENCY_KIND", f, "未知推导类型。");
            if (f.DerivationKind == DrawingDerivationKind.Geometry && (f.Status != DrawingFactStatus.Derived ||
                f.BindingEvidence is null || string.IsNullOrWhiteSpace(f.Derivation) || !f.ObservationIds.Any(geometryObservations.Contains)))
                Error("DIMENSION_GEOMETRY_PREMISE", f, "几何推导须引用当前图纸库存中的几何观测，记录局部绑定、基准与推导；不能引用虚构观测或按像素猜尺寸。");
            if (required && f.Status == DrawingFactStatus.Derived && f.NominalValue is null &&
                f.DerivationKind == DrawingDerivationKind.Arithmetic && f.DerivedFromDimensionIds.Count == 0)
                Error("DIMENSION_DEPENDENCY_REQUIRED", f, "派生尺寸须引用前提尺寸 ID，保留推导过程；不得用推算代替原标注。");
            if (f.DerivedFromDimensionIds.Distinct(StringComparer.Ordinal).Count() != f.DerivedFromDimensionIds.Count)
                Error("DIMENSION_DEPENDENCY_DUPLICATE", f, "推导前提 ID 不得重复。");
            foreach (var id in f.DerivedFromDimensionIds)
            {
                if (!facts.TryGetValue(id, out var premise)) Error("DIMENSION_DEPENDENCY_MISSING", f, "缺少推导前提：" + id);
                else if (premise.Status is DrawingFactStatus.Unknown or DrawingFactStatus.Assumed)
                    Error("DIMENSION_DEPENDENCY_UNRESOLVED", f, "推导前提尚未确定：" + id);
            }
        }
        // Kahn traversal avoids stack overflow on long or adversarial dependency chains.
        var pending = facts.ToDictionary(p => p.Key, p => p.Value.DerivedFromDimensionIds.Where(facts.ContainsKey).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var dependents = facts.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var pair in pending) foreach (var id in pair.Value) dependents[id].Add(pair.Key);
        var ready = new Queue<string>(pending.Where(p => p.Value.Count == 0).Select(p => p.Key));
        while (ready.TryDequeue(out var id))
        {
            pending.Remove(id);
            foreach (var child in dependents[id]) if (pending.TryGetValue(child, out var deps) && deps.Remove(id) && deps.Count == 0) ready.Enqueue(child);
        }
        foreach (var id in pending.Keys) Error("DIMENSION_DEPENDENCY_CYCLE", facts[id], "尺寸推导存在循环或依赖循环，不能作为已确定尺寸。");
        foreach (var group in context.Dimensions.Where(f => f.BindingEvidence is not null)
                     .GroupBy(f => (f.BindingEvidence!.FeatureId, f.BindingEvidence.MeasurementId)))
        {
            var first = group.First();
            foreach (var f in group.Skip(1))
                if (f.BindingEvidence!.QuantityKind != first.BindingEvidence!.QuantityKind ||
                    Family(f.Unit) != Family(first.Unit) || !Equal(Canonical(f), Canonical(first)))
                    Error("DIMENSION_CROSS_VIEW_CONFLICT", f, "同一特征的同一测量在不同绑定/视图中不一致；回查原标注、基准及量的含义。");
        }
        return errors;
    }
    private static int Family(DrawingValueUnit unit) => unit == DrawingValueUnit.Degree ? 1 : unit == DrawingValueUnit.Unitless ? 2 : 0;
    private static double Canonical(DrawingDimensionFact f) => SourceInterpretationValidation.SourceLiteralValue(f) * (f.Unit == DrawingValueUnit.Inch ? 25.4 : f.Unit == DrawingValueUnit.Meter ? 1000 : 1);
    private static bool Equal(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a-b) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
    [GeneratedRegex(@"^\s*(?:\d+\s*[-xX×]\s*)?(?<symbol>[RØ⌀φΦ∅])\s*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceSymbol();
}
