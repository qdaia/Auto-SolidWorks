using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>有界解析／完整非周期样条 B-Rep 的只读修订认证；不是通用形状等价或 G2 证明。</summary>
public sealed record SavedRevisionEntity(GeometryCandidate Candidate, IReadOnlyList<double> TrimData)
{
    // Native face-to-edge incidence is separate from sampled trim coordinates.
    // Required for spheres; older analytic inventories without it retain their
    // previous scope and cannot silently acquire spherical certification.
    public IReadOnlyList<string>? BoundaryEdgeReferences { get; init; }
    public SavedSplineCurve? SplineCurve { get; init; }
    public SavedSplineSurface? SplineSurface { get; init; }
    public SavedSplineTrimLoop? SplineTrimLoop { get; init; }
}

// Coordinates retain the native representation and SI units. Rational weights
// are included, never discarded or reconstructed from sampled points.
public sealed record SavedSplineCurve(int Order, int Dimension, int ControlPointCount,
    bool Periodic, IReadOnlyList<double> Knots, IReadOnlyList<double> ControlPoints);
public sealed record SavedSplineSurface(int UOrder, int VOrder, int UControlPointCount,
    int VControlPointCount, int Dimension, bool UPeriodic, bool VPeriodic, bool SameOrientation,
    bool FaceInSurfaceSense, IReadOnlyList<double> UKnots, IReadOnlyList<double> VKnots,
    IReadOnlyList<double> ControlPoints);
public sealed record SavedSplineTrimEdge(string EdgeReference, bool Sense, SavedSplineCurve ParametricCurve);
public sealed record SavedSplineTrimLoop(bool Outer, IReadOnlyList<SavedSplineTrimEdge> Edges);

public sealed record SavedRevisionState
{
    public required string ModelSha256 { get; init; }
    public required string Configuration { get; init; }
    public required string ParameterState { get; init; }
    public required GeometrySnapshot Geometry { get; init; }
    public required IReadOnlyList<SavedRevisionEntity> Entities { get; init; }
    public bool Complete { get; init; }
    public bool CleanSavedModel { get; init; }
    public bool NativeBodiesValid { get; init; }
}

public sealed record SavedRevisionCertification(bool Passed, string Message, int EntityCount);

public static class SavedRevisionCertifier
{
    // 这些是数值读回误差界限，不是允许源设计变化的公差。
    private const double AbsoluteTolerance = 1e-7;
    private const double RelativeTolerance = 1e-9;

    public static SavedRevisionCertification Compare(SavedRevisionState saved, SavedRevisionState observed,
        Func<string, string, bool> sameNativeIdentity, bool requireCleanObserved)
    {
        SavedRevisionCertification Fail(string message) => new(false, message, saved.Entities?.Count ?? 0);
        if (!Valid(saved) || !Valid(observed) || !saved.CleanSavedModel || requireCleanObserved && !observed.CleanSavedModel)
            return Fail("保存修订缺少干净起点、完整有界库存、有效原生体或有限测量。");
        if (!saved.ModelSha256.Equals(observed.ModelSha256, StringComparison.OrdinalIgnoreCase)
            || saved.Configuration != observed.Configuration || saved.ParameterState != observed.ParameterState)
            return Fail("保存文件、活动配置、特征控制或驱动参数发生变化。");
        var a = saved.Geometry; var b = observed.Geometry;
        if (a.SolidBodyCount != b.SolidBodyCount || a.SurfaceBodyCount != b.SurfaceBodyCount
            || a.FaceCount != b.FaceCount || a.EdgeCount != b.EdgeCount
            || !Numbers(GeometryNumbers(a), GeometryNumbers(b)) || saved.Entities.Count != observed.Entities.Count)
            return Fail("重建／重开的整件几何或完整实体数量与保存起点不同。");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in saved.Entities)
        {
            var c = entity.Candidate;
            var matches = observed.Entities.Where(e => e.Candidate.Signature.EntityKind == c.Signature.EntityKind
                && sameNativeIdentity(c.NativePersistentReference!, e.Candidate.NativePersistentReference!)).ToArray();
            if (matches.Length != 1 || !used.Add(matches[0].Candidate.CandidateId))
                return Fail("完整库存的原生持久身份不能形成唯一双射。");
            var other = matches[0];
            var owners = GeometryOwnership.Owners(c); var nextOwners = GeometryOwnership.Owners(other.Candidate);
            if (owners.Count != nextOwners.Count || owners.Any(o => nextOwners.Count(n => n.FeatureId == o.FeatureId
                && sameNativeIdentity(o.PersistentReference, n.PersistentReference)) != 1)
                || !SameSignature(c.Signature, other.Candidate.Signature) || !Numbers(entity.TrimData, other.TrimData)
                || !SameBoundary(entity.BoundaryEdgeReferences, other.BoundaryEdgeReferences, sameNativeIdentity)
                || !SameSpline(entity, other, sameNativeIdentity))
                return Fail("实体完整几何、修剪范围或全部实际来源身份发生变化。");
        }
        return new(true, "保存起点、完整有界库存、参数及重建／重开状态一致。", saved.Entities.Count);
    }

    private static bool Valid(SavedRevisionState s)
    {
        if (!s.Complete || !s.NativeBodiesValid || s.ModelSha256 is not { Length: 64 }
            || !s.ModelSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(s.Configuration)
            || string.IsNullOrWhiteSpace(s.ParameterState) || s.Geometry is null || s.Entities is null
            || s.Entities.Count is 0 or > 4096 || GeometryNumbers(s.Geometry).Any(n => !double.IsFinite(n))) return false;
        if (s.Entities.Select(e => e.Candidate.CandidateId).Distinct(StringComparer.Ordinal).Count() != s.Entities.Count) return false;
        if (s.Entities.Count(e => e.Candidate.Signature.EntityKind == EntityKind.Face) != s.Geometry.FaceCount
            || s.Entities.Count(e => e.Candidate.Signature.EntityKind == EntityKind.Edge) != s.Geometry.EdgeCount
            || s.Entities.Count(e => e.Candidate.Signature.EntityKind == EntityKind.Body) != s.Geometry.SolidBodyCount + s.Geometry.SurfaceBodyCount) return false;
        var edgeReferences = s.Entities.Where(e => e.Candidate.Signature.EntityKind == EntityKind.Edge)
            .Select(e => e.Candidate.NativePersistentReference).ToHashSet(StringComparer.Ordinal);
        return s.Entities.All(e => e.Candidate.NativePersistentReference is { Length: > 0 }
            && GeometryRefResolver.TryValidate(e.Candidate, out _) && e.TrimData is { Count: > 0 }
            && e.TrimData.All(double.IsFinite) && Supported(e)
            && (e.BoundaryEdgeReferences is null || e.Candidate.Signature.EntityKind == EntityKind.Face
                && e.BoundaryEdgeReferences.Count <= 4096
                && e.BoundaryEdgeReferences.All(p => !string.IsNullOrWhiteSpace(p) && edgeReferences.Contains(p))
                && e.BoundaryEdgeReferences.Distinct(StringComparer.Ordinal).Count() == e.BoundaryEdgeReferences.Count)
            && (e.Candidate.Signature.GeometryKind != GeometryKind.Sphere || e.BoundaryEdgeReferences is not null)
            && (e.Candidate.Signature.EntityKind == EntityKind.Body || GeometryOwnership.Owners(e.Candidate).Count > 0));
    }

    private static bool Supported(SavedRevisionEntity e)
    {
        var s = e.Candidate.Signature;
        if (s.EntityKind == EntityKind.Edge && s.GeometryKind == GeometryKind.BSpline)
            return e.SplineSurface is null && e.SplineTrimLoop is null && ValidCurve(e.SplineCurve, 3);
        if (s.EntityKind == EntityKind.Face && s.GeometryKind == GeometryKind.BSpline)
        {
            var n = e.SplineSurface; var loop = e.SplineTrimLoop;
            return e.SplineCurve is null && n is not null && !n.UPeriodic && !n.VPeriodic && n.SameOrientation
                && n.Dimension is 3 or 4 && n.UControlPointCount <= 8192 / Math.Max(1, n.VControlPointCount)
                && ValidKnots(n.UKnots, n.UOrder, n.UControlPointCount)
                && ValidKnots(n.VKnots, n.VOrder, n.VControlPointCount)
                && ValidPoints(n.ControlPoints, n.Dimension, n.UControlPointCount * n.VControlPointCount, 3)
                && loop is { Outer: true, Edges.Count: >= 3 and <= 4096 }
                && e.BoundaryEdgeReferences is not null && loop.Edges.Count == e.BoundaryEdgeReferences.Count
                && loop.Edges.Select(x => x.EdgeReference).Distinct(StringComparer.Ordinal).Count() == loop.Edges.Count
                && loop.Edges.All(x => e.BoundaryEdgeReferences.Contains(x.EdgeReference, StringComparer.Ordinal)
                    && ValidCurve(x.ParametricCurve, 2));
        }
        if (e.SplineSurface is not null || e.SplineCurve is not null || e.SplineTrimLoop is not null) return false;
        return s.EntityKind switch
        {
            EntityKind.Body => true,
            EntityKind.Face => s.GeometryKind is GeometryKind.Plane or GeometryKind.Cylinder
                || s.GeometryKind == GeometryKind.Sphere && s.AnchorMm is not null && s.RadiusMm is > 0
                    && s.AreaMm2 is > 0 && s.Direction is null,
            EntityKind.Edge => s.GeometryKind is GeometryKind.Line or GeometryKind.Circle,
            _ => false
        };
    }

    private static bool ValidCurve(SavedSplineCurve? c, int spatialDimension) => c is not null && !c.Periodic
        && ValidKnots(c.Knots, c.Order, c.ControlPointCount)
        && ValidPoints(c.ControlPoints, c.Dimension, c.ControlPointCount, spatialDimension);
    private static bool ValidKnots(IReadOnlyList<double>? knots, int order, int count) => order is >= 2 and <= 16
        && count >= order && count <= 8192 && knots is not null && knots.Count == count + order
        && knots.All(double.IsFinite) && knots.Zip(knots.Skip(1)).All(p => p.First <= p.Second)
        && knots[order - 1] < knots[count];
    private static bool ValidPoints(IReadOnlyList<double>? points, int dimension, int count, int spatialDimension) =>
        (dimension == spatialDimension || dimension == spatialDimension + 1) && count > 0 && count <= 8192
        && points is not null && points.Count == dimension * count && points.All(double.IsFinite)
        && (dimension == spatialDimension || Enumerable.Range(0, count).All(i => points[i * dimension + spatialDimension] > 0));

    private static bool SameSpline(SavedRevisionEntity a, SavedRevisionEntity b, Func<string, string, bool> identity)
    {
        if (!SameCurve(a.SplineCurve, b.SplineCurve)) return false;
        var x = a.SplineSurface; var y = b.SplineSurface;
        if (x is null ? y is not null : y is null || x.UOrder != y.UOrder || x.VOrder != y.VOrder
            || x.UControlPointCount != y.UControlPointCount || x.VControlPointCount != y.VControlPointCount
            || x.Dimension != y.Dimension || x.UPeriodic != y.UPeriodic || x.VPeriodic != y.VPeriodic
            || x.SameOrientation != y.SameOrientation || x.FaceInSurfaceSense != y.FaceInSurfaceSense
            || !SplineNumbers(x.UKnots, y.UKnots) || !SplineNumbers(x.VKnots, y.VKnots)
            || !SplineNumbers(x.ControlPoints, y.ControlPoints)) return false;
        var u = a.SplineTrimLoop; var v = b.SplineTrimLoop;
        if (u is null) return v is null;
        if (v is null || u.Outer != v.Outer || u.Edges.Count != v.Edges.Count) return false;
        // A cyclic change in the loop's first coedge is harmless; reversing its
        // order or sense is not. Require a unique rotation with native identity.
        return Enumerable.Range(0, v.Edges.Count).Count(offset => u.Edges.Select((e, i) =>
            (e, other: v.Edges[(i + offset) % v.Edges.Count])).All(p => p.e.Sense == p.other.Sense
                && identity(p.e.EdgeReference, p.other.EdgeReference)
                && SameCurve(p.e.ParametricCurve, p.other.ParametricCurve))) == 1;
    }
    private static bool SameCurve(SavedSplineCurve? a, SavedSplineCurve? b) => a is null ? b is null : b is not null
        && a.Order == b.Order && a.Dimension == b.Dimension && a.ControlPointCount == b.ControlPointCount
        && a.Periodic == b.Periodic && SplineNumbers(a.Knots, b.Knots) && SplineNumbers(a.ControlPoints, b.ControlPoints);
    // SI coordinates use a tighter readback bound than the legacy mm signatures.
    private static bool SplineNumbers(IReadOnlyList<double> a, IReadOnlyList<double> b) => a.Count == b.Count
        && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 1e-12 + 1e-12 * Math.Max(Math.Abs(p.First), Math.Abs(p.Second)));

    private static bool SameBoundary(IReadOnlyList<string>? a, IReadOnlyList<string>? b,
        Func<string, string, bool> sameNativeIdentity)
    {
        if (a is null) return b is null;
        if (b is null || a.Count != b.Count) return false;
        var used = new HashSet<int>();
        foreach (var reference in a)
        {
            var matches = Enumerable.Range(0, b.Count).Where(i => sameNativeIdentity(reference, b[i])).ToArray();
            if (matches.Length != 1 || !used.Add(matches[0])) return false;
        }
        return true;
    }

    private static bool SameSignature(GeometrySignature a, GeometrySignature b) => a.EntityKind == b.EntityKind
        && a.GeometryKind == b.GeometryKind && Vector(a.AnchorMm, b.AnchorMm) && Vector(a.Direction, b.Direction)
        && Scalar(a.RadiusMm, b.RadiusMm) && Scalar(a.AreaMm2, b.AreaMm2);
    private static bool Vector(Vector3? a, Vector3? b) => a is null ? b is null : b is not null
        && Close(a.X, b.X) && Close(a.Y, b.Y) && Close(a.Z, b.Z);
    private static bool Scalar(double? a, double? b) => a is null ? b is null : b is not null && Close(a.Value, b.Value);
    private static bool Numbers(IReadOnlyList<double> a, IReadOnlyList<double> b) => a.Count == b.Count
        && a.Zip(b).All(p => Close(p.First, p.Second));
    private static bool Close(double a, double b) => double.IsFinite(a) && double.IsFinite(b)
        && Math.Abs(a - b) <= AbsoluteTolerance + RelativeTolerance * Math.Max(Math.Abs(a), Math.Abs(b));
    private static double[] GeometryNumbers(GeometrySnapshot g) => [g.VolumeMm3, g.SurfaceAreaMm2,
        g.CenterOfMassMm.X, g.CenterOfMassMm.Y, g.CenterOfMassMm.Z, g.BoundingBoxMm.X, g.BoundingBoxMm.Y, g.BoundingBoxMm.Z];
}
