using CadModeling.Ir;
namespace CadModeling.Core;

public sealed partial class GenericPlanCompiler
{
    private static NativeFeatureOperation CompileNativeFeature(GenericOperationDraft source)
    {
        var options = source.Feature!;
        var dependencies = source.DependsOn.ToList();
        foreach (var id in options.ProfileIds.Concat(options.GuideIds)
            .Concat(new[] { options.SketchId, options.PathSketchId, options.AxisId, options.Loft?.CenterlineId })
            .Concat(AdvancedFeatureValidation.Queries(options).Select(s => s.FeatureId)).OfType<string>()) AddDependency(dependencies, id);
        return new NativeFeatureOperation { Id = source.Id, Name = source.Name, DependsOn = dependencies, Options = options };
    }
}

public static class NativeFeatureValidation
{
    public static void Validate(NativeFeatureOperation operation, string path, List<ModelingDiagnostic> errors)
    {
        var o = operation.Options;
        SurfaceFeatureValidation.ValidateOptions(o, path, errors);
        AdvancedFeatureValidation.Validate(o, path, errors);
        void Require(bool ok, string message) { if (!ok) errors.Add(new("FEATURE_PARAMETER", DiagnosticSeverity.Error, message, path)); }
        bool Positive(double x) => double.IsFinite(x) && x > 0;
        foreach(var property in typeof(NativeFeatureOptions).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            Require(double.IsFinite((double)property.GetValue(o)!), $"{property.Name}必须是有限的。");
        Require(Finite(o.TranslationMm) && Finite(o.RotationDegrees), "体变换坐标必须是有限的。");
        if (o.Frame is { } frame) ValidateFrame(frame, path, errors);
        foreach (var s in o.Selections)
        {
            Require(Positive(s.ToleranceMm), "实体选择容差必须为正数。");
            if (s.PositionMm is { } p) Require(Finite(p), "选择点必须是有限的。");
            if (s.Direction is { } n) Require(Finite(n) && Norm(n) > 1e-10, "选择方向必须是非零的。");
            if (s.RadiusMm is { } r) Require(Positive(r), "选择半径必须为正数。");
        }
        switch (o.Kind)
        {
            case NativeFeatureKind.ReferencePlane:
                Require(o.Frame is not null, "一个基准面需要一个原点、法线和X方向。"); break;
            case NativeFeatureKind.ReferenceAxis:
                Require(o.AxisStartMm is not null && o.AxisEndMm is not null, "基准轴需要两个不同的点。");
                if (o.AxisStartMm is { } a && o.AxisEndMm is { } b)
                    Require(Finite(a) && Finite(b) && Norm(new(a.X-b.X,a.Y-b.Y,a.Z-b.Z)) > 1e-9, "轴点必须是有限的且不同。");
                break;
            case NativeFeatureKind.Chamfer:
                Require(o.Selections.Count > 0 && Positive(o.DistanceMm), "需要选择边并指定正数距离以创建倒角。");
                if (o.ChamferMode == ChamferMode.DistanceAngle) Require(o.AngleDegrees > 0 && o.AngleDegrees < 90, "倒角角度必须在0度和90度之间。");
                if (o.ChamferMode == ChamferMode.TwoDistances) Require(Positive(o.SecondDistanceMm), "第二条倒角的距离必须为正数。");
                break;
            case NativeFeatureKind.Fillet:
                Require(o.VariableFillet is not null || o.Selections.Count > 0 && Positive(o.RadiusMm), "圆角需要选择并且填写正数的半径，或者明确填写variable_fillet边。"); break;
            case NativeFeatureKind.RevolveBoss: case NativeFeatureKind.RevolveCut:
                Require(o.SketchId is not null && o.AxisId is not null, "旋转需要轮廓草图和轴。");
                Require(double.IsFinite(o.AngleDegrees) && o.AngleDegrees > 0 && o.AngleDegrees <= 360, "旋转角度必须在(0, 360].");
                Require(double.IsFinite(o.ThicknessMm) && o.ThicknessMm >= 0, "特征薄旋转厚度不能为负数。"); break;
            case NativeFeatureKind.Shell: case NativeFeatureKind.Thicken:
                Require(Positive(o.ThicknessMm), "厚度必须为正数。"); break;
            case NativeFeatureKind.LinearPattern:
                Require(o.Count >= 2 && Positive(o.SpacingMm), "线性阵列需要 count >= 2 且间距为正。"); break;
            case NativeFeatureKind.CircularPattern:
                Require(o.Count >= 2 && o.AngleDegrees > 0 && o.AngleDegrees <= 360, "圆周阵列需要数量 >=2，角度位于 (0,360] 度。"); break;
            case NativeFeatureKind.LoftBoss: case NativeFeatureKind.LoftCut: case NativeFeatureKind.SurfaceLoft:
                Require(o.ProfileIds.Count >= 2, "放样需要至少两个轮廓。"); break;
            case NativeFeatureKind.SweepBoss: case NativeFeatureKind.SweepCut:
                Require(o.SketchId is not null && o.PathSketchId is not null, "扫描需要轮廓和路径草图。"); break;
            case NativeFeatureKind.Hole:
                Require(Positive(o.DiameterMm) && o.HoleCenters.Count > 0, "孔需要正数直径和位置。");
                Require(o.ThroughAll || Positive(o.DepthMm), "盲孔深度必须为正数。");
                Require(o.HoleCenters.All(p=>double.IsFinite(p.Xmm)&&double.IsFinite(p.Ymm)), "孔中心必须是有限的。");
                if(o.HoleKind==HoleKind.Counterbore) Require(o.CounterboreDiameterMm>o.DiameterMm && Positive(o.CounterboreDepthMm), "锪孔需要更大的直径和正数深度。");
                if(o.HoleKind==HoleKind.Countersink) Require(o.CountersinkDiameterMm>o.DiameterMm && o.CountersinkAngleDegrees>0 && o.CountersinkAngleDegrees<180, "锪孔需要更大的直径和包含角度在(0,180).");
                if(o.HoleKind==HoleKind.Tapped) Require(o.ThreadMajorDiameterMm>o.DiameterMm && !string.IsNullOrWhiteSpace(o.ThreadDesignation), "需要显式的公称直径和螺纹设计；diameter_mm 是钻孔直径。");
                Require(o.ThreadDepthMm is null || o.HoleKind==HoleKind.Tapped && Positive(o.ThreadDepthMm.Value)
                    && (o.ThroughAll || o.ThreadDepthMm.Value<=o.DepthMm), "有效牙深只适用于攻丝孔，必须为正数且不大于盲孔钻孔深度。");
                break;
            case NativeFeatureKind.ThinExtrude: case NativeFeatureKind.SheetMetalBase: case NativeFeatureKind.Rib:
                Require(o.SketchId is not null && Positive(o.ThicknessMm), "这个特征需要草图和正数厚度。");
                if(o.Kind==NativeFeatureKind.ThinExtrude) Require(Positive(o.DepthMm), "特征厚度必须为正数。");
                if(o.Kind==NativeFeatureKind.SheetMetalBase) Require(o.RadiusMm>=0, "弯曲半径不能为负数。"); break;
            case NativeFeatureKind.SurfaceExtrude:
                Require(o.SketchId is not null && Positive(o.DepthMm), "曲面拉伸需要草图和正数深度。"); break;
            case NativeFeatureKind.WeldmentMember:
                Require(o.PathSketchId is not null && o.ProfilePath is not null && Path.IsPathFullyQualified(o.ProfilePath) && File.Exists(o.ProfilePath)
                    && o.ProfilePath.EndsWith(".sldlfp",StringComparison.OrdinalIgnoreCase), "结构成员需要路径草图和现有的绝对 SLDLFP 轮廓路径。"); break;
            case NativeFeatureKind.TrimWeldment:
                Require(o.DistanceMm>=0 && o.Selections.Any(s=>s.SelectionMark==2) && o.Selections.Any(s=>s.SelectionMark!=2), "切割焊件需要目标体、标记-2的修剪工具，以及非负的间隙。"); break;
            case NativeFeatureKind.SurfaceTrim:
                Require(o.Selections.Any(s=>s.SelectionMark!=2) && o.Selections.Any(s=>s.SelectionMark==2), "曲面修剪需要修剪工具和标记-2区域来保持。");
                Require(o.Selections.Where(s=>s.SelectionMark==2).All(s=>s.Kind==EntityKind.Body && s.PositionMm is not null), "每个保留的曲面体需要在其保留区域内部的一个点。"); break;
            case NativeFeatureKind.SurfacePlanar: case NativeFeatureKind.SketchPattern:
                Require(o.SketchId is not null, "这个特征需要一个草图。"); break;
            case NativeFeatureKind.EdgeFlange:
                Require(o.Selections.Count>0 && o.Selections.All(s=>s.Kind==EntityKind.Edge) && Positive(o.DistanceMm) && o.AngleDegrees>0 && o.AngleDegrees<180,
                    "边缘折边需要边缘、正数长度和一个角度，在(0,180)."); break;
            case NativeFeatureKind.Draft:
                Require(o.Selections.Count>1 && o.AngleDegrees>0 && o.AngleDegrees<90, "拔模需要中性平面、拔模面和一个角度（0,90）。"); break;
            case NativeFeatureKind.SetDimension:
                Require(o.DimensionName?.Split('@').Length>=2 && double.IsFinite(o.DimensionValue), "编辑尺寸需要特征名称和有限数值。");
                Require(o.DimensionUnit is null || Enum.IsDefined(o.DimensionUnit.Value), "尺寸单位无效。");
                Require(!o.DimensionIsAngle || o.DimensionUnit is null or DrawingValueUnit.Degree, "角度标志与单位冲突。");
                if(o.DimensionUnit==DrawingValueUnit.Unitless)
                    Require(o.DimensionValue>=1 && o.DimensionValue<=int.MaxValue && o.DimensionValue==Math.Truncate(o.DimensionValue), "无单位数量必须是正整数。");
                break;
        }
    }
    public static bool Finite(Vector3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    public static double Norm(Vector3 v) => Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);
    public static void ValidateFrame(SketchFrame f, string path, List<ModelingDiagnostic> errors)
    {
        var cross = new Vector3(f.Normal.Y*f.XDirection.Z-f.Normal.Z*f.XDirection.Y,
            f.Normal.Z*f.XDirection.X-f.Normal.X*f.XDirection.Z,f.Normal.X*f.XDirection.Y-f.Normal.Y*f.XDirection.X);
        if (!Finite(f.OriginMm) || !Finite(f.Normal) || !Finite(f.XDirection) || Norm(cross) < 1e-10)
            errors.Add(new("FRAME_INVALID", DiagnosticSeverity.Error, "草图框架需要有限的原点和非平行的非零法向/X方向。", path));
    }
}
