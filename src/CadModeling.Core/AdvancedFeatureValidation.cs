using CadModeling.Ir;
namespace CadModeling.Core;

public static class AdvancedFeatureValidation
{
    public static IEnumerable<EntityQuery> Queries(NativeFeatureOptions o) => o.Selections
        .Concat(o.VariableFillet?.Edges.Select(e => e.Edge) ?? [])
        .Concat(o.Surface?.FillBoundaries.SelectMany(b => b.SupportFace is null ? new[] { b.Edge } : new[] { b.Edge, b.SupportFace }) ?? []);
    public static void Validate(NativeFeatureOptions o, string path, List<ModelingDiagnostic> errors)
    {
        void Require(bool ok, string text) { if (!ok) errors.Add(new("ADVANCED_PARAMETER", DiagnosticSeverity.Error, text, path)); }
        bool Positive(double v) => double.IsFinite(v) && v > 0;
        if(o.PhysicalThread is { } thread)
        {
            Require(o.Kind==NativeFeatureKind.PhysicalThread && o.Selections.Count==1 && o.Selections[0] is {Kind:EntityKind.Edge,Geometry:GeometryKind.Circle,AllMatches:false},"实体螺纹需要一个准确圆形入口边。");
            try{PhysicalThreadContract.Validate(thread);}catch(ArgumentException e){Require(false,e.Message);}
        }
        Require(o.Kind!=NativeFeatureKind.PhysicalThread || o.PhysicalThread is not null,"实体螺纹必须有显式规格与原生牙型合同。");
        foreach (var q in Queries(o))
        {
            Require(Positive(q.ToleranceMm), "实体公差必须是有限且正数。");
            Require(!q.RequirePersistentIdentity || !string.IsNullOrWhiteSpace(q.PersistentReference)&&!q.AllMatches,
                "严格身份选择需要单一持久引用，不能使用全匹配或几何后备。");
            Require(q.PositionMm is null || NativeFeatureValidation.Finite(q.PositionMm), "查询位置必须是有限的。");
            Require(q.Direction is null || NativeFeatureValidation.Finite(q.Direction) && NativeFeatureValidation.Norm(q.Direction)>1e-10, "查询方向必须是有限且非零。");
            Require(q.RadiusMm is null || Positive(q.RadiusMm.Value), "查询半径必须是有限且正数。");
            Require(Enum.IsDefined(q.Kind) && Enum.IsDefined(q.Geometry), "未知的实体查询类型或几何。");
            Require(q.LengthMm is null || (q.Kind == EntityKind.Edge && Positive(q.LengthMm.Value)), "长度过滤需要一个边和正数毫米长度。");
            Require(q.AreaMm2 is null || (q.Kind == EntityKind.Face && Positive(q.AreaMm2.Value)), "面积滤波需要一个面和正数毫米面积。");
            Require(Positive(q.AreaToleranceMm2), "特征面积公差必须为正数。");
            Require(q.StartPointMm is null && q.EndPointMm is null || q.Kind == EntityKind.Edge && q.StartPointMm is not null && q.EndPointMm is not null
                && NativeFeatureValidation.Finite(q.StartPointMm) && NativeFeatureValidation.Finite(q.EndPointMm), "边缘端点过滤需要两个有限点。");
            Require(q.AdjacentFaceCount is null || q.Kind == EntityKind.Edge && q.AdjacentFaceCount is >= 1 and <= 2, "边线相邻面的数量必须为 1 或 2。");
        }
        if (o.SpatialCurve is { } c)
        {
            Require(o.Kind == NativeFeatureKind.SpatialCurve, "spatial_curve 控制要求 SpatialCurve。");
            Require(Enum.IsDefined(c.CurveKind) && c.PointsMm.Count >= (c.Closed ? 3 : 2) && c.PointsMm.Count <= 4096, "空间曲线需要 2..4096 点，或者至少 3 用于封闭曲线。");
            Require(c.PointsMm.All(NativeFeatureValidation.Finite), "空间曲线点必须是有限模型毫米坐标。");
            Require(!c.PointsMm.Zip(c.PointsMm.Skip(1)).Any(p => Distance(p.First, p.Second) < 1e-7), "连续的空间点必须不同。");
            Require(!c.Closed || c.PointsMm.Count == 0 || Distance(c.PointsMm[0], c.PointsMm[^1]) > 1e-7, "不要重复第一点：closed=true 明确关闭曲线。");
        }
        if (o.Kind == NativeFeatureKind.SpatialCurve) Require(o.SpatialCurve is not null && o.Selections.Count == 0 && o.SketchId is null, "SpatialCurve 需要 spatial_curve 并且没有草图/选择输入。");
        if (o.Helix is { } h)
        {
            Require(o.Kind == NativeFeatureKind.Helix && !string.IsNullOrWhiteSpace(o.SketchId), "螺旋线需要螺旋线控制和一个圆 sketch_id。");
            Require(Positive(h.PitchMm) && Positive(h.Revolutions) && double.IsFinite(h.PitchMm*h.Revolutions), "螺旋线的螺距和圈数必须是有限的正数。");
            Require(double.IsFinite(h.StartAngleDegrees) && h.StartAngleDegrees >= 0 && h.StartAngleDegrees < 360, "螺旋线起始角度必须在[0, 360)度范围内。");
        }
        if (o.Kind == NativeFeatureKind.Helix) Require(o.Helix is not null, "螺旋线需要显式的螺距和圈数。");
        if (o.LinearPattern is { } p)
        {
            Require(o.Kind == NativeFeatureKind.LinearPattern && p.SecondCount is >= 1 and <= 10000, "方向计数必须在 1..10000 上为 LinearPattern。");
            Require((long)o.Count*p.SecondCount<=10000, "二维阵列限定为最多 10000 个实例。");
            Require(double.IsFinite(p.SecondSpacingMm) && (p.SecondCount > 1 ? p.SecondSpacingMm > 0 : p.SecondSpacingMm == 0), "第二间距必须为正数以在激活的第二方向上有效，否则为零。");
            Require(p.SecondCount == 1 || o.Selections.Any(q => q.SelectionMark == 2), "二维阵列需要一个标记-2方向参考。");
            Require(p.SecondCount > 1 || !p.SecondDirectionSeedOnly && !p.ReverseSecondDirection, "第二方向标志需要激活的第二方向。");
        }
        if (o.Sweep is { } s)
        {
            Require(o.Kind is NativeFeatureKind.SweepBoss or NativeFeatureKind.SweepCut or NativeFeatureKind.SurfaceSweep, "裁剪控制需要一个扫描特征。");
            Require(Enum.IsDefined(s.Orientation) && double.IsFinite(s.TwistAngleDegrees) && Math.Abs(s.TwistAngleDegrees)<=36000, "扫掠方向/角度必须有效且最多为100圈。");
            Require(s.Orientation is SweepOrientation.TwistAlongPath or SweepOrientation.TwistWithConstantNormal || s.TwistAngleDegrees == 0, "旋转角度需要显式的旋转方向。");
            Require(s.Orientation != SweepOrientation.FollowFirstGuide || o.GuideIds.Count >= 1, "FollowFirstGuide 需要至少一个引导。");
            Require(s.Orientation != SweepOrientation.FollowTwoGuides || o.GuideIds.Count >= 2, "FollowTwoGuides 需要两个导向。");
            Require(o.Surface is null || o.Surface.SweepOrientation == SurfaceSweepOrientation.FollowPath, "不要将传统的曲面定向与先进的扫掠定向结合在一起。");
        }
        if (o.Loft is { } l)
        {
            Require(o.Kind is NativeFeatureKind.LoftBoss or NativeFeatureKind.LoftCut or NativeFeatureKind.SurfaceLoft, "放样控制需要一个放样特征。");
            Require(Enum.IsDefined(l.StartCondition) && Enum.IsDefined(l.EndCondition), "未知的放样端条件。");
            Require(o.Kind!=NativeFeatureKind.SurfaceLoft || l.CenterlineId is null && l.GuideInfluence is null && l.StartTangentLengthMm is null
                && l.EndTangentLengthMm is null && !l.ReverseStartTangent && !l.ReverseEndTangent,
                "复杂定义的放样当前需要 LoftBoss 或 LoftCut；SurfaceLoft 只保留其现有的端条件控制。");
            Require(!l.Close || o.ProfileIds.Count >= 3 && l.StartCondition == SurfaceEndCondition.None && l.EndCondition == SurfaceEndCondition.None, "封闭的放样需要至少 3 个轮廓，且没有端条件。");
            Require(l.CenterlineId is null || !string.IsNullOrWhiteSpace(l.CenterlineId) && !o.ProfileIds.Concat(o.GuideIds).Contains(l.CenterlineId,StringComparer.OrdinalIgnoreCase) && !l.Close,
                "放样中心线必须与轮廓/引导不同，并且需要一个开放的放样。");
            Require(l.GuideInfluence is null || Enum.IsDefined(l.GuideInfluence.Value) && o.GuideIds.Count>0,
                "放样引导影响需要显式的引导和已知的影响模式。");
            Require(l.StartTangentLengthMm is null || Positive(l.StartTangentLengthMm.Value) && l.StartCondition==SurfaceEndCondition.NormalToProfile,
                "起点切向长度需要正数长度和NormalToProfile起点条件。");
            Require(l.EndTangentLengthMm is null || Positive(l.EndTangentLengthMm.Value) && l.EndCondition==SurfaceEndCondition.NormalToProfile,
                "端切长度需要正数长度，并且满足 NormalToProfile 端条件。");
            Require(!l.ReverseStartTangent || l.StartCondition==SurfaceEndCondition.NormalToProfile, "开始反转切线需要正常的开始条件。");
            Require(!l.ReverseEndTangent || l.EndCondition==SurfaceEndCondition.NormalToProfile, "端部倒圆需要端部正法条件。");
        }
        if (o.VariableFillet is { } f)
        {
            Require(o.Kind == NativeFeatureKind.Fillet && o.Selections.Count == 0 && f.Edges.Count is >= 1 and <= 128, "变量圆角使用 1..128 显式边线条目；选择必须为空。");
            foreach (var e in f.Edges)
                Require(e.Edge.Kind == EntityKind.Edge && !e.Edge.AllMatches && Positive(e.StartRadiusMm) && Positive(e.EndRadiusMm)
                    && NativeFeatureValidation.Finite(e.StartPointMm) && NativeFeatureValidation.Finite(e.EndPointMm) && Distance(e.StartPointMm,e.EndPointMm)>e.Edge.ToleranceMm,
                    "变量圆角需要一个开放的边、不同的模型端点和正数的端点半径。");
        }
        var boundaries = o.Surface?.FillBoundaries ?? [];
        Require(boundaries.Count==0 || o.Kind==NativeFeatureKind.SurfaceFill, "fill_boundaries 控制要求 SurfaceFill。");
        if (boundaries.Count > 0)
        {
            Require(o.Kind == NativeFeatureKind.SurfaceFill && o.ProfileIds.Count == 0 && boundaries.Count <= 128, "填充边界的边界不能混合使用 profile_ids。");
            foreach (var b in boundaries)
                Require(Enum.IsDefined(b.Contact) && b.Edge.Kind == EntityKind.Edge && !b.Edge.AllMatches
                    && (b.Contact == SurfaceContact.Contact ? b.SupportFace is null : b.SupportFace is { Kind: EntityKind.Face, AllMatches: false }),
                    "填充 G1/G2 约束需要一个单一的边和一个相邻的支持面；接触没有支持面。");
        }
    }
    public static double Distance(Vector3 a,Vector3 b) => NativeFeatureValidation.Norm(new(a.X-b.X,a.Y-b.Y,a.Z-b.Z));
    public static bool IsCurve(ModelingOperation op) => op is ProfileSketchOperation || op is NativeFeatureOperation { Options.Kind: NativeFeatureKind.SpatialCurve or NativeFeatureKind.Helix };
    public static void ValidateReferences(NativeFeatureOperation op, ModelingPlan plan, int index, string path, List<ModelingDiagnostic> errors)
    {
        var o=op.Options; var earlier=plan.Operations.Take(index).ToArray();
        void Require(bool ok,string text){if(!ok)errors.Add(new("ADVANCED_REFERENCE",DiagnosticSeverity.Error,text,path));}
        if(o.Loft?.CenterlineId is { } centerline)
            Require(earlier.Any(x=>x.Id.Equals(centerline,StringComparison.OrdinalIgnoreCase)&&IsCurve(x)) && op.DependsOn.Contains(centerline,StringComparer.OrdinalIgnoreCase),
                $"放样中心线 '{centerline}' 必须是先前声明的曲线依赖项。");
        if(o.Kind == NativeFeatureKind.Helix)
        {
            var sketch=earlier.OfType<ProfileSketchOperation>().FirstOrDefault(x=>x.Id.Equals(o.SketchId,StringComparison.OrdinalIgnoreCase));
            Require(sketch is not null && sketch.Primitives.Count==1 && sketch.Primitives[0] is CircleProfile && sketch.Edits.Count==0, "输入的螺旋线必须是一个没有编辑的单圆草图。");
        }
        if(o.Kind is NativeFeatureKind.SweepBoss or NativeFeatureKind.SweepCut or NativeFeatureKind.SurfaceSweep or NativeFeatureKind.LoftBoss or NativeFeatureKind.LoftCut)
            foreach(var id in o.ProfileIds.Concat(o.GuideIds).Concat(new[]{o.SketchId,o.PathSketchId}.OfType<string>()))
                Require(earlier.Any(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)&&IsCurve(x)) && op.DependsOn.Contains(id,StringComparer.OrdinalIgnoreCase), $"曲线 '{id}' 必须是先前声明的曲线依赖项。");
        foreach(var id in Queries(o).Skip(o.Selections.Count).Select(q=>q.FeatureId).OfType<string>())
            Require(earlier.Any(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)) && op.DependsOn.Contains(id,StringComparer.OrdinalIgnoreCase),$"选择 '{id}' 必须是先前声明的依赖项。");
    }
}
