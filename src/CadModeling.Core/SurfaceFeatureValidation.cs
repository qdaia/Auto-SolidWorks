using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>Offline surface contracts. These checks never certify native surface quality.</summary>
public static class SurfaceFeatureValidation
{
    public static bool IsNewSurface(NativeFeatureKind kind) => kind is NativeFeatureKind.SurfaceBoundary
        or NativeFeatureKind.SurfaceFill or NativeFeatureKind.SurfaceSweep or NativeFeatureKind.SurfaceOffset;

    public static void ValidateOptions(NativeFeatureOptions o, string path, List<ModelingDiagnostic> errors)
    {
        void Require(bool ok, string message)
        { if (!ok) errors.Add(new("SURFACE_PARAMETER", DiagnosticSeverity.Error, message, path)); }
        bool CurveIds(IReadOnlyList<string> ids) => ids.All(x => !string.IsNullOrWhiteSpace(x))
            && ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Count;
        var s = o.Surface ?? new();
        if (o.Surface is not null)
        {
            Require(IsNewSurface(o.Kind), "曲面控制仅在SurfaceBoundary、SurfaceFill、SurfaceSweep和SurfaceOffset上支持。");
            Require(Enum.IsDefined(s.StartCondition) && Enum.IsDefined(s.EndCondition) && Enum.IsDefined(s.SweepOrientation), "未知的曲面端条件或扫掠方向。");
            Require(double.IsFinite(s.ConnectionToleranceMm) && s.ConnectionToleranceMm >= 0.0001 && s.ConnectionToleranceMm <= 0.1,
                "曲面连接公差必须在 [0.0001, 0.1] mm。");
            Require(s.FillResolution is >= 1 and <= 3, "填充分辨率必须是1、2或3。");
            Require(o.Kind == NativeFeatureKind.SurfaceBoundary || (!s.RequireBoundaryCornerMatch && s.StartCondition == SurfaceEndCondition.None && s.EndCondition == SurfaceEndCondition.None),
                "边界角/端条件控制要求 SurfaceBoundary。");
            Require(o.Kind == NativeFeatureKind.SurfaceSweep || s.SweepOrientation == SurfaceSweepOrientation.FollowPath, "扫掠方向需要满足 SurfaceSweep。");
            Require(o.Kind == NativeFeatureKind.SurfaceFill || (s.FillResolution == 2 && s.OptimizeFill), "填充控制需要 SurfaceFill。");
        }
        if (IsNewSurface(o.Kind) || o.Kind == NativeFeatureKind.SurfaceLoft)
        {
            Require(CurveIds(o.ProfileIds) && CurveIds(o.GuideIds), "曲面曲线 ID 必须非空且在每个方向上唯一。");
            Require(!o.ProfileIds.Intersect(o.GuideIds, StringComparer.OrdinalIgnoreCase).Any(), "曲线既不能既是轮廓/边界，又是引导/约束。");
            Require(o.ProfileIds.Count <= 128 && o.GuideIds.Count <= 128, "每个曲面方向最多支持 128 条曲线。");
        }
        switch (o.Kind)
        {
            case NativeFeatureKind.SurfaceBoundary:
                Require(o.ProfileIds.Count >= 2, "SurfaceBoundary 需要至少两个按顺序排列的方向 1 profile_ids；guide_ids 属于方向 2。");
                Require(o.Selections.Count == 0 && o.SketchId is null && o.PathSketchId is null, "SurfaceBoundary 只使用 profile_ids 和 guide_ids。");
                Require(!s.RequireBoundaryCornerMatch || (o.ProfileIds.Count == 2 && o.GuideIds.Count == 2), "边界角匹配需要恰好两个轮廓和两个引导。");
                Require(!o.TryToFormSolid, "SurfaceBoundary 创建曲面体；使用 SurfaceKnit 形成体。");
                break;
            case NativeFeatureKind.SurfaceFill:
                Require((o.ProfileIds.Count > 0 || s.FillBoundaries.Count > 0) && o.Selections.Count == 0 && o.SketchId is null && o.PathSketchId is null,
                    "SurfaceFill 需要边界草图 profile_ids 和可选的内部约束草图 guide_ids。");
                Require(!o.TryToFormSolid, "SurfaceFill 创建曲面体；使用 SurfaceKnit 形成体。");
                break;
            case NativeFeatureKind.SurfaceSweep:
                Require(!string.IsNullOrWhiteSpace(o.SketchId) && !string.IsNullOrWhiteSpace(o.PathSketchId), "SurfaceSweep 需要 sketch_id 和 path_sketch_id。");
                Require(!string.Equals(o.SketchId, o.PathSketchId, StringComparison.OrdinalIgnoreCase), "扫描轮廓和路径必须不同。");
                Require(!o.GuideIds.Any(x => string.Equals(x, o.SketchId, StringComparison.OrdinalIgnoreCase) || string.Equals(x, o.PathSketchId, StringComparison.OrdinalIgnoreCase)), "扫掠引导不能重复使用轮廓或路径。");
                Require(o.ProfileIds.Count == 0 && o.Selections.Count == 0 && !o.TryToFormSolid, "SurfaceSweep 使用 sketch_id/path_sketch_id/guide_ids 并创建一个曲面体。");
                break;
            case NativeFeatureKind.SurfaceOffset:
                Require(o.Selections.Count > 0 && o.Selections.All(x => x.Kind == EntityKind.Face), "SurfaceOffset 需要面的选择。");
                Require(double.IsFinite(o.DistanceMm) && o.DistanceMm >= 0, "偏移距离必须是非负数；零复制会生成面，反向操作会改变方向。");
                Require(o.ProfileIds.Count == 0 && o.GuideIds.Count == 0 && o.SketchId is null && o.PathSketchId is null && !o.TryToFormSolid, "SurfaceOffset 只使用面的选择，并创建一个曲面体。");
                break;
            case NativeFeatureKind.SurfaceKnit:
                Require(o.Selections.Count > 0 && o.Selections.All(x => x.Kind is EntityKind.Body or EntityKind.Face), "SurfaceKnit 需要曲面体或面的选择。");
                Require(o.DistanceMm == 0 || (double.IsFinite(o.DistanceMm) && o.DistanceMm >= 0.0001 && o.DistanceMm <= 0.1), "缝合公差必须为零（默认 0.01 mm）或在 [0.0001, 0.1] mm 范围内。");
                break;
        }
    }

    public static void ValidateReferences(NativeFeatureOperation op, ModelingPlan plan, int index, string path, List<ModelingDiagnostic> diagnostics)
    {
        var o = op.Options;
        if (!IsNewSurface(o.Kind) && o.Kind != NativeFeatureKind.SurfaceLoft) return;
        // Inspect earlier operations directly, not a dictionary that already contains the current operation.
        var earlier = plan.Operations.Take(index).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var ids = o.ProfileIds.Concat(o.GuideIds).Concat(new[] { o.SketchId, o.PathSketchId }.OfType<string>()).ToArray();
        foreach (var id in ids)
        {
            if (!earlier.TryGetValue(id, out var target) || !AdvancedFeatureValidation.IsCurve(target))
                diagnostics.Add(new("SURFACE_SKETCH_REFERENCE", DiagnosticSeverity.Error, $"曲面 '{id}' 必须引用先前的轮廓草图操作。", path));
            if (!op.DependsOn.Contains(id, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new("SURFACE_DEPENDENCY", DiagnosticSeverity.Error, $"曲面依赖 '{id}' 编译操作中缺失。", path));
        }
        foreach (var query in o.Selections.Where(x => x.FeatureId is not null))
        {
            if (!earlier.ContainsKey(query.FeatureId!) || !op.DependsOn.Contains(query.FeatureId!, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new("SURFACE_DEPENDENCY", DiagnosticSeverity.Error, $"选中的特征 '{query.FeatureId}' 必须是先前声明的依赖项。", path));
        }
        if (o.Kind != NativeFeatureKind.SurfaceBoundary || o.Surface?.RequireBoundaryCornerMatch != true) return;
        if (o.ProfileIds.Count != 2 || o.GuideIds.Count != 2) return;
        var ends = new List<Vector3[]>();
        foreach (var id in o.ProfileIds.Concat(o.GuideIds))
        {
            if (!earlier.TryGetValue(id, out var target) || !TryCurveEndpoints(target, out var points))
            {
                diagnostics.Add(new("SURFACE_CORNERS_UNVERIFIABLE", DiagnosticSeverity.Error,
                    $"无法验证 '{id}' 的端角。请使用显式的开放空间曲线，或者一个开放的样条/连接的开放曲线在一个显式的框架上，没有求解约束、尺寸、编辑或面附着。", path));
                return;
            }
            ends.Add(points);
        }
        var tolerance = o.Surface.ConnectionToleranceMm;
        var profileCorners = ends.Take(2).SelectMany(x => x).ToArray();
        for (var i = 0; i < profileCorners.Length; i++)
        for (var j = i + 1; j < profileCorners.Length; j++)
            if (Distance(profileCorners[i], profileCorners[j]) <= tolerance)
            {
                diagnostics.Add(new("SURFACE_CORNER_DEGENERATE", DiagnosticSeverity.Error,
                    "端点限定的片体需要四个分离的角落，且这些角落之间的距离超过连接公差。", path));
                return;
            }
        // Each of the four corners must use one distinct endpoint from each curve family.
        // Enumerate endpoint orientation only; never mutate, reorder or snap supplied geometry.
        var bestGap = double.PositiveInfinity;
        for (var mask = 0; mask < 16; mask++)
        {
            var maxGap = 0d;
            for (var p = 0; p < 2; p++)
            for (var g = 0; g < 2; g++)
                maxGap = Math.Max(maxGap, Distance(ends[p][g ^ ((mask >> p) & 1)], ends[2 + g][p ^ ((mask >> (2 + g)) & 1)]));
            bestGap = Math.Min(bestGap, maxGap);
        }
        if (!double.IsFinite(bestGap) || bestGap > tolerance)
            diagnostics.Add(new("SURFACE_CORNER_GAP", DiagnosticSeverity.Error,
                $"2x2 边界在模型空间中的角点不匹配：最优匹配的最大间隙{bestGap:G9}毫米超过{tolerance:G9}毫米。请修正曲线／坐标系；未进行自动吸附。", path));
        else
            diagnostics.Add(new("SURFACE_CORNERS_CHECKED", DiagnosticSeverity.Info,
                $"四模型空间端点角之间在{tolerance:G9}mm 内匹配。内部分割、自相交和 G1/G2 曲面连续性未验证。", path));
    }

    private static bool TryEndpoints(ProfileSketchOperation s, out Vector3[] points)
    {
        points = [];
        if (s.Frame is not { } frame || s.FaceAttachment is not null || s.PlaneId is not null || s.Primitives.Count != 1
            || s.Constraints.Count != 0 || s.Dimensions.Count != 0 || s.Edits.Count != 0) return false;
        var frameErrors = new List<ModelingDiagnostic>();
        NativeFeatureValidation.ValidateFrame(frame, "frame", frameErrors);
        if (frameErrors.Count != 0) return false;
        ProfilePoint a, b;
        switch (s.Primitives[0])
        {
            case SplineProfile { Closed: false } spline when spline.Points.Count >= 2:
                a = spline.Points[0]; b = spline.Points[^1]; break;
            case OpenCurveProfile { Construction: false } curve when curve.Curves.Count > 0:
                a = curve.Curves[0].Start; b = curve.Curves[^1].End;
                for (var i = 1; i < curve.Curves.Count; i++)
                    if (Math.Abs(curve.Curves[i-1].End.Xmm-curve.Curves[i].Start.Xmm) > 1e-7 || Math.Abs(curve.Curves[i-1].End.Ymm-curve.Curves[i].Start.Ymm) > 1e-7) return false;
                break;
            default: return false;
        }
        var n = Unit(frame.Normal);
        var dot = frame.XDirection.X*n.X + frame.XDirection.Y*n.Y + frame.XDirection.Z*n.Z;
        var u = Unit(new(frame.XDirection.X-dot*n.X, frame.XDirection.Y-dot*n.Y, frame.XDirection.Z-dot*n.Z));
        var v = new Vector3(n.Y*u.Z-n.Z*u.Y, n.Z*u.X-n.X*u.Z, n.X*u.Y-n.Y*u.X);
        Vector3 Map(ProfilePoint p) => new(frame.OriginMm.X+u.X*p.Xmm+v.X*p.Ymm,
            frame.OriginMm.Y+u.Y*p.Xmm+v.Y*p.Ymm, frame.OriginMm.Z+u.Z*p.Xmm+v.Z*p.Ymm);
        points = [Map(a), Map(b)];
        return points.All(NativeFeatureValidation.Finite) && Distance(points[0], points[1]) > 1e-7;
    }
    private static bool TryCurveEndpoints(ModelingOperation operation,out Vector3[] points)
    {
        if(operation is ProfileSketchOperation sketch)return TryEndpoints(sketch,out points);
        points=[];
        if(operation is not NativeFeatureOperation { Options.Kind: NativeFeatureKind.SpatialCurve, Options.SpatialCurve: { Closed:false } curve }
            || curve.PointsMm.Count<2 || curve.PointsMm.Any(p=>!NativeFeatureValidation.Finite(p)))return false;
        points=[curve.PointsMm[0],curve.PointsMm[^1]];
        return Distance(points[0],points[1])>1e-7;
    }
    private static Vector3 Unit(Vector3 v) { var n = NativeFeatureValidation.Norm(v); return new(v.X/n,v.Y/n,v.Z/n); }
    private static double Distance(Vector3 a, Vector3 b) => NativeFeatureValidation.Norm(new(a.X-b.X,a.Y-b.Y,a.Z-b.Z));
}
