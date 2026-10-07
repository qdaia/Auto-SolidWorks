using CadModeling.Drawing.Contracts;
using CadModeling.Ir;

namespace CadModeling.Drawing.Ingestion;

public sealed record DimensionTargetDescriptor
{
    public required string TargetId { get; init; }
    public DimensionTargetKind Kind { get; init; } = DimensionTargetKind.Unknown;
    public string? ViewId { get; init; }
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public IReadOnlyList<string> FeatureIds { get; init; } = [];
    public IReadOnlyList<string> SemanticKinds { get; init; } = [];
    public string? OperationId { get; init; }
    public string? ParameterPath { get; init; }
}

public sealed record DimensionBindingRequest
{
    public required SourceFact Fact { get; init; }
    public string SourceRevisionId { get; init; } = string.Empty;
    public DimensionObservation? Observation { get; init; }
    public IReadOnlyList<DimensionTargetDescriptor> Targets { get; init; } = [];
    public double MinimumScore { get; init; } = 2;
    public double AmbiguityGap { get; init; } = 0.75;
}

public static class DimensionBindingResolver
{
    public static DimensionBinding Resolve(DimensionBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fact = request.Fact;
        if (string.IsNullOrWhiteSpace(request.SourceRevisionId))
            return Unresolved(fact, request.SourceRevisionId, request.Observation, "需要在确认绑定之前指定源修订标识。");
        if (fact.Fact.Status is FactStatus.Unknown or FactStatus.Assumed || fact.NumericValue is null)
            return Unresolved(fact, request.SourceRevisionId, request.Observation, "源特征不能独立声明或推导，不能驱动模型参数。");

        var nearby = request.Observation?.NearbyGeometryObservationIds.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var candidates = request.Targets.Select(target => Candidate(fact, request.Observation, target, nearby))
            .Where(item => item.HasAttachmentEvidence && item.AttachmentScore > 0)
            .OrderByDescending(item => item.AttachmentScore)
            .ThenBy(item => item.TargetId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0 || candidates[0].AttachmentScore < request.MinimumScore)
            return Unresolved(fact, request.SourceRevisionId, request.Observation,
                "没有几何目标有足够的附着证据和兼容的尺寸语义。", candidates);

        if (candidates.Length > 1 && candidates[0].AttachmentScore - candidates[1].AttachmentScore < request.AmbiguityGap)
            return Base(fact, request.SourceRevisionId, request.Observation) with
            {
                Decision = DimensionBindingDecision.Conflict,
                EvidenceStatus = EvidenceStatus.Conflict,
                Candidates = candidates,
                BindingBasis = $"候选中的顶级选项模糊不清（{candidates[0].AttachmentScore:0.###}vs{candidates[1].AttachmentScore:0.###}）；有意忽略数值上的相等性。",
                ConflictFactIds = [fact.FactId]
            };

        var selected = candidates[0];
        return Base(fact, request.SourceRevisionId, request.Observation) with
        {
            Decision = DimensionBindingDecision.Bound,
            EvidenceStatus = EvidenceStatus.Confirmed,
            Candidates = candidates,
            TargetFeatureIds = selected.FeatureIds,
            OperationId = selected.OperationId ?? string.Empty,
            ParameterPath = selected.ParameterPath ?? string.Empty,
            BindingBasis = selected.AttachmentBasis
        };
    }

    private static DimensionBindingCandidate Candidate(
        SourceFact fact,
        DimensionObservation? observation,
        DimensionTargetDescriptor target,
        IReadOnlySet<string> nearby)
    {
        var score = 0d;
        var reasons = new List<string>();
        var direct = target.ObservationIds.Intersect(fact.EvidenceIds, StringComparer.Ordinal).Count();
        if (direct > 0) { score += 4 + Math.Min(direct - 1, 2); reasons.Add("共享源证据"); }
        var proximity = target.ObservationIds.Count(nearby.Contains);
        if (proximity > 0) { score += 2 + Math.Min(proximity - 1, 2) * .5; reasons.Add("leader或接近几何特征候选项"); }
        var hasAttachmentEvidence = direct > 0 || proximity > 0;
        if (!string.IsNullOrWhiteSpace(fact.ViewId) && target.ViewId == fact.ViewId) { score += 1.5; reasons.Add("相同来源视图"); }
        if (SemanticContradiction(fact.Kind, target)) { score -= 100; reasons.Add("尺寸的语义与目标类型相矛盾"); }
        else if (SemanticCompatible(fact.Kind, target)) { score += 2; reasons.Add("尺寸含义匹配目标类型"); }
        if (target.FeatureIds.Count > 0) { score += .25; reasons.Add("目标属于解释特征"); }
        // Deliberately no comparison to a target's numeric value here. Equal numbers do not identify geometry.
        return new()
        {
            TargetId = target.TargetId,
            TargetKind = target.Kind,
            ViewId = target.ViewId,
            ObservationIds = target.ObservationIds,
            FeatureIds = target.FeatureIds,
            HasAttachmentEvidence = hasAttachmentEvidence,
            AttachmentScore = score,
            AttachmentBasis = reasons.Count == 0 ? "没有非数字附件证据" : string.Join("; ", reasons),
            OperationId = target.OperationId,
            ParameterPath = target.ParameterPath
        };
    }

    private static bool SemanticCompatible(SourceFactKind fact, DimensionTargetDescriptor target)
    {
        var semantic = target.SemanticKinds.Select(item => item.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (SemanticContradiction(fact, target)) return false;
        return fact switch
        {
            SourceFactKind.Diameter => target.Kind is DimensionTargetKind.Circle or DimensionTargetKind.Hole || semantic.Contains("diameter"),
            SourceFactKind.Radius => target.Kind is DimensionTargetKind.Arc || semantic.Contains("radius"),
            SourceFactKind.Depth => target.Kind is DimensionTargetKind.Depth or DimensionTargetKind.Hole or DimensionTargetKind.Slot || semantic.Contains("depth"),
            SourceFactKind.Count => target.Kind is DimensionTargetKind.Pattern or DimensionTargetKind.Hole || semantic.Contains("count"),
            SourceFactKind.Thickness => target.Kind is DimensionTargetKind.Face or DimensionTargetKind.Feature || semantic.Contains("thickness"),
            SourceFactKind.LinearDimension => target.Kind is DimensionTargetKind.Edge or DimensionTargetKind.Feature or DimensionTargetKind.Parameter || semantic.Contains("linear"),
            SourceFactKind.Angle => semantic.Contains("angle") || target.Kind is DimensionTargetKind.Parameter,
            _ => false
        };
    }

    private static bool SemanticContradiction(SourceFactKind fact, DimensionTargetDescriptor target)
    {
        var path = target.ParameterPath?.Trim().ToLowerInvariant() ?? string.Empty;
        var declaresDiameter = target.SemanticKinds.Any(item => item.Equals("diameter", StringComparison.OrdinalIgnoreCase)) ||
            path.EndsWith("diameter_mm", StringComparison.Ordinal);
        var declaresRadius = target.SemanticKinds.Any(item => item.Equals("radius", StringComparison.OrdinalIgnoreCase)) ||
            path.EndsWith("radius_mm", StringComparison.Ordinal);
        return fact == SourceFactKind.Radius && declaresDiameter ||
            fact == SourceFactKind.Diameter && declaresRadius ||
            fact == SourceFactKind.Count && target.Kind is DimensionTargetKind.Edge or DimensionTargetKind.Arc;
    }

    private static DimensionBinding Base(SourceFact fact, string sourceRevisionId, DimensionObservation? observation) => new()
    {
        BindingId = "binding-" + fact.FactId,
        SourceFactId = fact.FactId,
        SourceRevisionId = sourceRevisionId,
        SourceFactFingerprint = SourceFactRevisions.FactFingerprint(fact),
        DimensionObservationId = observation?.DimensionObservationId ?? fact.Candidates.FirstOrDefault()?.CandidateId ?? string.Empty,
        SourceRegionId = fact.SourceRegionId ?? string.Empty,
        RawLiteral = fact.Candidates.FirstOrDefault()?.RawLiteral ?? fact.InterpretedLiteral,
        Symbol = Symbol(fact.Kind),
        Value = new()
        {
            Value = fact.NumericValue ?? 0,
            Unit = fact.Unit ?? MeasurementUnit.Unitless,
            Symbol = Symbol(fact.Kind),
            Fact = fact.Fact
        },
        Role = DimensionRole.Driving,
        EvidenceIds = fact.EvidenceIds
    };

    private static DimensionBinding Unresolved(SourceFact fact, string sourceRevisionId, DimensionObservation? observation, string reason,
        IReadOnlyList<DimensionBindingCandidate>? candidates = null) => Base(fact, sourceRevisionId, observation) with
    {
        Decision = DimensionBindingDecision.Unresolved,
        EvidenceStatus = EvidenceStatus.Candidate,
        Candidates = candidates ?? [],
        BindingBasis = reason
    };

    private static string Symbol(SourceFactKind kind) => kind switch
    {
        SourceFactKind.Diameter => "diameter",
        SourceFactKind.Radius => "radius",
        SourceFactKind.Angle => "angle",
        SourceFactKind.Count => "count",
        SourceFactKind.Depth => "depth",
        SourceFactKind.Thickness => "thickness",
        _ => "linear"
    };
}

public static class DimensionConstraintSolver
{
    public static DimensionConstraintReport Evaluate(SourceFactsDocument source, IReadOnlyList<DimensionConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(constraints);
        var facts = source.Facts.ToDictionary(item => item.FactId, StringComparer.Ordinal);
        var issues = new List<DimensionConstraintIssue>();
        foreach (var constraint in constraints)
        {
            var missing = constraint.FactIds.Where(id => !facts.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                issues.Add(Issue(constraint, "DIM_CONSTRAINT_MISSING", "约束缺少来源事实。", missing));
                continue;
            }
            var selected = constraint.FactIds.Select(id => facts[id]).ToArray();
            if (selected.Any(item => item.NumericValue is null || item.Fact.Status is FactStatus.Unknown or FactStatus.Assumed))
            {
                issues.Add(Issue(constraint, "DIM_CONSTRAINT_UNKNOWN", "约束包含未解决或假设的源事实。", constraint.FactIds));
                continue;
            }
            if (constraint.NumericTolerance < 0 || !double.IsFinite(constraint.NumericTolerance))
            {
                issues.Add(Issue(constraint, "DIM_CONSTRAINT_TOLERANCE", "约束容差无效。", constraint.FactIds));
                continue;
            }

            switch (constraint.Kind)
            {
                case DimensionConstraintKind.Equality:
                    if (!CompatibleUnits(selected) || selected.Skip(1).Any(item => Math.Abs(item.NumericValue!.Value - selected[0].NumericValue!.Value) > constraint.NumericTolerance))
                        issues.Add(Issue(constraint, "DIM_EQUALITY_CONFLICT", "等值约束不一致；值未被平均或重写。", constraint.FactIds));
                    break;
                case DimensionConstraintKind.Sum:
                    if (!CompatibleUnits(selected) || constraint.ExpectedValue is null || Math.Abs(selected.Sum(item => item.NumericValue!.Value) - constraint.ExpectedValue.Value) > constraint.NumericTolerance)
                        issues.Add(Issue(constraint, "DIM_CHAIN_CONFLICT", "链尺寸和声明的总尺寸冲突；未应用尺寸重分布。", constraint.FactIds));
                    break;
                case DimensionConstraintKind.Count:
                    if (constraint.ExpectedCount is null || selected.Any(item => !TryCount(item, out _)) || selected.Sum(item => TryCount(item, out var count) ? count : 0) != constraint.ExpectedCount)
                        issues.Add(Issue(constraint, "DIM_COUNT_CONFLICT", "特征/阵列计数不一致。", constraint.FactIds));
                    break;
                case DimensionConstraintKind.DiameterRadius:
                    if (selected.Length != 2 || !CompatibleUnits(selected) || !DiameterRadiusConsistent(selected, constraint.NumericTolerance))
                        issues.Add(Issue(constraint, "DIM_DIAMETER_RADIUS_CONFLICT", "直径/半径的语义不一致。", constraint.FactIds));
                    break;
                case DimensionConstraintKind.SameAxis:
                case DimensionConstraintKind.EqualSpacing:
                    // These require geometry evidence rather than scalar source dimensions. Stage-1 never declares them
                    // satisfied from dimensions alone; a later geometry checker must supply deterministic measurements.
                    issues.Add(Issue(constraint, "DIM_GEOMETRY_CONSTRAINT_UNVERIFIABLE", "几何约束需要独立的测量几何形状，并且仅凭标量源事实无法证明。", constraint.FactIds));
                    break;
            }
        }
        return new() { Issues = issues };
    }

    private static bool CompatibleUnits(IReadOnlyList<SourceFact> facts) => facts.Count > 0 && facts.All(item => item.Unit == facts[0].Unit);
    private static bool DiameterRadiusConsistent(IReadOnlyList<SourceFact> facts, double tolerance)
    {
        var diameter = facts.SingleOrDefault(item => item.Kind == SourceFactKind.Diameter);
        var radius = facts.SingleOrDefault(item => item.Kind == SourceFactKind.Radius);
        return diameter?.NumericValue is { } d && radius?.NumericValue is { } r && Math.Abs(d - 2 * r) <= tolerance;
    }
    private static bool TryCount(SourceFact fact, out int count)
    {
        if (fact.Multiplicity is { } multiplicity && multiplicity >= 0) { count = multiplicity; return true; }
        if (fact.NumericValue is { } value && double.IsFinite(value) && value >= 0 && Math.Abs(value - Math.Round(value)) <= 1e-9 && value <= int.MaxValue)
        {
            count = (int)Math.Round(value);
            return true;
        }
        count = 0;
        return false;
    }
    private static DimensionConstraintIssue Issue(DimensionConstraint constraint, string code, string message, IReadOnlyList<string> facts) => new()
    {
        ConstraintId = constraint.ConstraintId,
        Code = code,
        Message = message,
        FactIds = facts
    };
}

public static class SourceFactPlanAdapter
{
    /// <summary>
    /// Compatibility bridge into the existing typed draft. Only independently known source facts with an explicit,
    /// unambiguous operation/parameter binding can be emitted; unresolved candidates never become model parameters.
    /// </summary>
    public static IReadOnlyList<DrawingDimensionFact> ToLegacyDimensions(SourceFactsDocument source, IReadOnlyList<DimensionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bindings);
        var facts = source.Facts.ToDictionary(item => item.FactId, StringComparer.Ordinal);
        var output = new List<DrawingDimensionFact>();
        foreach (var binding in bindings.Where(item => item.Decision == DimensionBindingDecision.Bound))
        {
            if (!facts.TryGetValue(binding.SourceFactId, out var fact))
                throw new InvalidOperationException($"绑定 '{binding.BindingId}' 引用了缺失的源事实 '{binding.SourceFactId}'。");
            if (!binding.SourceRevisionId.Equals(source.RevisionId, StringComparison.Ordinal))
                throw new InvalidOperationException($"绑定 '{binding.BindingId}' 对于源修订版 '{binding.SourceRevisionId}'，而不是当前修订版 '{source.RevisionId}' 创建。重新绑定源特征。");
            var currentFingerprint = SourceFactRevisions.FactFingerprint(fact);
            if (!binding.SourceFactFingerprint.Equals(currentFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"绑定 '{binding.BindingId}' 源特征在附加后更改。在编译前请先重新绑定源特征。");
            if (fact.Fact.Status is not (FactStatus.Stated or FactStatus.Derived) || fact.NumericValue is null || fact.Unit is null)
                throw new InvalidOperationException($"无法编译绑定 '{binding.BindingId}'，因为存在未解决的源事实。");
            if (string.IsNullOrWhiteSpace(binding.OperationId) || string.IsNullOrWhiteSpace(binding.ParameterPath))
                throw new InvalidOperationException($"绑定 ' {binding.BindingId} ' 缺少显式的 类型化计划 目标。");
            output.Add(new()
            {
                Id = fact.FactId,
                OperationId = binding.OperationId,
                ParameterPath = binding.ParameterPath,
                Value = fact.NumericValue.Value,
                Unit = Unit(fact.Unit.Value),
                Status = fact.Fact.Status == FactStatus.Derived ? DrawingFactStatus.Derived : DrawingFactStatus.Stated,
                SourceLiteral = fact.Candidates.FirstOrDefault()?.RawLiteral ?? fact.InterpretedLiteral,
                ViewIds = string.IsNullOrWhiteSpace(fact.ViewId) ? [] : [fact.ViewId],
                ObservationIds = fact.EvidenceIds,
                Derivation = fact.Derivation?.Formula,
                Critical = fact.Critical
            });
        }
        return output;
    }

    private static DrawingValueUnit Unit(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Millimeter => DrawingValueUnit.Millimeter,
        MeasurementUnit.Inch => DrawingValueUnit.Inch,
        MeasurementUnit.Meter => DrawingValueUnit.Meter,
        MeasurementUnit.Degree => DrawingValueUnit.Degree,
        MeasurementUnit.Unitless => DrawingValueUnit.Unitless,
        _ => throw new InvalidOperationException($"源事实单位 '{unit}' 无法驱动类型化建模参数。")
    };
}
