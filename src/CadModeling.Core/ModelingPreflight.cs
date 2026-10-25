using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Bounded analytic risk checks; no topology inference or automatic dimension changes.</summary>
public static class ModelingPreflight
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(GenericModelDraft draft)
    {
        var result = new List<ModelingDiagnostic>();
        result.AddRange(ExtrusionPlacement.Validate(draft));
        result.AddRange(ConnectionPreflight.Validate(draft));
        result.AddRange(NativeDimensionPreflight.Validate(draft));
        foreach(var op in draft.Operations)
            if(op.DrivenProfile is not null && op.Type!=GenericOperationKind.ProfileSketch ||
                op.PlacementIntent is not null && op.Type is not (GenericOperationKind.ExtrudeBoss or GenericOperationKind.ExtrudeCut))
                result.Add(new("WORKFLOW_FIELD_UNUSED",DiagnosticSeverity.Error,"驱动轮廓只用于ProfileSketch，厚度定位只用于拉伸；不允许静默忽略字段。","operations."+op.Id));
        foreach(var sketch in draft.Operations.Where(o=>o.CenterPositionDimensionName is not null||o.CenterPositionDimensionAxis is not null))
            if(!sketch.AutoDimensionPrimitives||string.IsNullOrWhiteSpace(sketch.CenterPositionDimensionName)||
                sketch.CenterPositionDimensionAxis is not ("X" or "Y")||sketch.Primitives.FirstOrDefault() is not {Type:GenericPrimitiveKind.Circle} first||
                Math.Abs((sketch.CenterPositionDimensionAxis=="X"?first.CenterXmm:first.CenterYmm)??0)<1e-8)
                result.Add(new("SKETCH_POSITION_ALIAS_INVALID",DiagnosticSeverity.Error,"定位尺寸名称需要自动圆草图、X/Y轴和对应非零首圆坐标。","operations."+sketch.Id));
        foreach (var sketch in draft.Operations.Where(o => o.Type == GenericOperationKind.ProfileSketch && o.AutoDimensionPrimitives))
            foreach (var d in sketch.Dimensions.Where(d => d.Kind is SketchDimensionKind.Diameter or SketchDimensionKind.Radius))
                if (d.Entities.Count == 1 && d.Entities[0].PrimitiveIndex >= 0 && d.Entities[0].PrimitiveIndex < sketch.Primitives.Count &&
                    d.Entities[0].Part == SketchEntityPart.Segment && sketch.Primitives[d.Entities[0].PrimitiveIndex].Type == GenericPrimitiveKind.Circle)
                    result.Add(new("SKETCH_DUPLICATE_DRIVING_DIMENSION", DiagnosticSeverity.Error,
                        "自动定义圆已有直径驱动尺寸；不能再叠加半径/直径尺寸 " + d.Name + "。读取实际名称或选择独立几何检查。", "operations." + sketch.Id));
        // A circular boss may later be modified/merged. Report a risk, never claim the kernel's unique cause.
        var circles = new List<(GenericOperationDraft Boss, SketchFrame Frame, GenericPrimitiveDraft Circle)>();
        foreach (var op in draft.Operations)
        {
            if (op.Type is GenericOperationKind.ExtrudeBoss or GenericOperationKind.ExtrudeCut)
            {
                var sourceSketch = draft.Operations.FirstOrDefault(s => s.Id == op.SketchId);
                var extrusionFrame = sourceSketch is { FaceAttachment: null, PlaneId: null }
                    ? sourceSketch.Frame ?? StandardFrame(sourceSketch.Plane ?? ReferencePlane.Front) : null;
                if(sourceSketch is not null&&extrusionFrame is null)
                    result.Add(new("EXTRUDE_DIRECTION_UNRESOLVED",DiagnosticSeverity.Info,"引用基准面或面附着的方向需要原生读取后核对；不能离线猜测材料一侧。","operations."+op.Id));
                if (extrusionFrame is not null && Valid(extrusionFrame))
                {
                    var normal = ModelVerification.Unit(extrusionFrame.Normal);
                    var direction = ModelVerification.Scale(normal, op.ReverseDirection ? -1 : 1);
                    // These are typed-plan semantics; the executor inverts FeatureCut4's API flip.
                    var apiFlip = op.Type == GenericOperationKind.ExtrudeCut ? !op.ReverseDirection : op.ReverseDirection;
                    result.Add(new("EXTRUDE_DIRECTION_TRACE", DiagnosticSeverity.Info, FormattableString.Invariant(
                        $"拉伸 {op.Id}：原点 ({extrusionFrame.OriginMm.X:G12},{extrusionFrame.OriginMm.Y:G12},{extrusionFrame.OriginMm.Z:G12}) mm；法向 ({normal.X:G12},{normal.Y:G12},{normal.Z:G12})；计划方向 ({direction.X:G12},{direction.Y:G12},{direction.Z:G12})；reverse_direction={op.ReverseDirection}；api_flip={apiFlip}；end={op.EndCondition ?? ExtrudeEndCondition.Blind}；depth_mm={op.DepthMm:G12}；start_offset_mm={op.StartOffsetMm:G12}；reverse_start_offset={op.ReverseStartOffset}。需按源图核对实体一侧；未证明原生交集。"), "operations." + op.Id));
                }
            }
            if (op.Type == GenericOperationKind.ExtrudeBoss && op.EndCondition is null or ExtrudeEndCondition.Blind)
            {
                var sketch = draft.Operations.FirstOrDefault(s => s.Id == op.SketchId);
                if (sketch is { FaceAttachment: null, PlaneId: null } && sketch.Primitives.Count == 1 && sketch.Primitives[0] is { Type: GenericPrimitiveKind.Circle, Construction: false } c &&
                    sketch.Frame is { } frame && Valid(frame)) circles.Add((op, frame, c));
            }
            if (op.Feature is { Kind: NativeFeatureKind.Hole, Frame: { } holeFrame } hole && Valid(holeFrame) && ModelVerification.Positive(hole.DiameterMm))
            {
                var hnormal = ModelVerification.Unit(holeFrame.Normal);
                var cutDirection=ModelVerification.Scale(hnormal,hole.Reverse?-1:1);
                result.Add(new("HOLE_DIRECTION_TRACE",DiagnosticSeverity.Info,
                    FormattableString.Invariant($"孔 {op.Id}：起始点 ({holeFrame.OriginMm.X:G12},{holeFrame.OriginMm.Y:G12},{holeFrame.OriginMm.Z:G12}) mm；法向 ({hnormal.X:G12},{hnormal.Y:G12},{hnormal.Z:G12})；切除方向 ({cutDirection.X:G12},{cutDirection.Y:G12},{cutDirection.Z:G12})；reverse={hole.Reverse}。需与源孔底/实体一侧独立核对。"),"operations."+op.Id));
                foreach (var (boss, frame, c) in circles)
                {
                    var normal = ModelVerification.Unit(frame.Normal);
                    if (Math.Abs(ModelVerification.Dot(hnormal, normal)) < 1 - 1e-10 || c.DiameterMm is not { } diameter || !ModelVerification.Positive(diameter)) continue;
                    var center = Point(frame, new(c.CenterXmm ?? 0, c.CenterYmm ?? 0));
                    foreach (var hc in hole.HoleCenters)
                    {
                        var delta = ModelVerification.Sub(Point(holeFrame, hc), center);
                        var lateral = ModelVerification.Sub(delta, ModelVerification.Scale(normal, ModelVerification.Dot(delta, normal)));
                        var clearance = diameter / 2 - ModelVerification.Norm(lateral) - hole.DiameterMm / 2;
                        if (Math.Abs(clearance) <= 1e-7)
                            result.Add(new("HOLE_ZERO_THICKNESS_RISK", DiagnosticSeverity.Warning,
                                $"孔操作 {op.Id} 与圆柱 {boss.Id} 的标称径向净距为 0 mm，可能产生零厚度；需核对轴向交集和当前实体边界。保持原尺寸，尺寸偏差必须记录授权。", "operations." + op.Id));
                    }
                }
            }
            if (op.Feature is { Kind: NativeFeatureKind.Chamfer, ChamferMode: ChamferMode.TwoDistances } chamfer && chamfer.DistanceMm != chamfer.SecondDistanceMm)
                result.Add(new("CHAMFER_DISTANCE_ORIENTATION", DiagnosticSeverity.Warning,
                    "非等距倒角的两个距离由原生相邻面次序决定；不能直接当作横/竖距离，需声明倒角面点/法向检查。", "operations." + op.Id));
            if(op.Feature is {Kind: NativeFeatureKind.RevolveBoss or NativeFeatureKind.RevolveCut} revolve)
            {
                var axis=draft.Operations.FirstOrDefault(o=>o.Id==revolve.AxisId)?.Feature;
                var sketch=draft.Operations.FirstOrDefault(o=>o.Id==revolve.SketchId);
                var primitive=sketch?.Primitives.FirstOrDefault(p=>!p.Construction);
                if(axis is {Kind:NativeFeatureKind.ReferenceAxis,AxisStartMm:{ } a,AxisEndMm:{ } b} &&
                    sketch?.Frame is { } frame && Valid(frame) && primitive is not null &&
                    ModelVerification.Finite(a)&&ModelVerification.Finite(b)&&ModelVerification.Norm(ModelVerification.Sub(b,a))>1e-12&&double.IsFinite(revolve.AngleDegrees))
                {
                    var local=primitive.Type==GenericPrimitiveKind.Circle?new ProfilePoint(primitive.CenterXmm??0,primitive.CenterYmm??0):primitive.Points.FirstOrDefault();
                    if(local is not null)
                    {
                        var start=Point(frame,local);
                        var mid=Rotate(start,a,b,revolve.AngleDegrees/2,revolve.Reverse);
                        var end=Rotate(start,a,b,revolve.AngleDegrees,revolve.Reverse);
                        result.Add(new("REVOLVE_DIRECTION_TRACE",DiagnosticSeverity.Info,FormattableString.Invariant(
                            $"旋转 {op.Id} 的源轮廓代表点，按显式轴正向的右手约定：start=({start.X:G12},{start.Y:G12},{start.Z:G12})，mid=({mid.X:G12},{mid.Y:G12},{mid.Z:G12})，end=({end.X:G12},{end.Y:G12},{end.Z:G12}) mm；reverse={revolve.Reverse}。这是计划解析轨迹，不能替代原生方向与源形状验收。"),"operations."+op.Id));
                    }
                }
            }
        }
        return result;
    }
    public static Vector3 Rotate(Vector3 point,Vector3 axisStart,Vector3 axisEnd,double angleDegrees,bool reverse=false)
    {
        if(!ModelVerification.Finite(point)||!ModelVerification.Finite(axisStart)||!ModelVerification.Finite(axisEnd)||
            !double.IsFinite(angleDegrees)||ModelVerification.Norm(ModelVerification.Sub(axisEnd,axisStart))<1e-12)
            throw new ArgumentException("旋转轨迹需要有限点、角度和非零轴。");
        var axis=ModelVerification.Unit(ModelVerification.Sub(axisEnd,axisStart));
        var p=ModelVerification.Sub(point,axisStart);
        var theta=(angleDegrees%360)/180*Math.PI*(reverse?-1:1);
        var cross=new Vector3(axis.Y*p.Z-axis.Z*p.Y,axis.Z*p.X-axis.X*p.Z,axis.X*p.Y-axis.Y*p.X);
        return ModelVerification.Add(axisStart,ModelVerification.Add(ModelVerification.Scale(p,Math.Cos(theta)),
            ModelVerification.Add(ModelVerification.Scale(cross,Math.Sin(theta)),ModelVerification.Scale(axis,ModelVerification.Dot(axis,p)*(1-Math.Cos(theta))))));
    }
    private static bool Valid(SketchFrame frame) => ModelVerification.Finite(frame.OriginMm) && ModelVerification.Finite(frame.Normal) &&
        ModelVerification.Finite(frame.XDirection) && ModelVerification.Norm(frame.Normal) > 1e-12 && ModelVerification.Norm(frame.XDirection) > 1e-12 &&
        Math.Abs(ModelVerification.Dot(ModelVerification.Unit(frame.Normal), ModelVerification.Unit(frame.XDirection))) < 1e-8;
    private static SketchFrame StandardFrame(ReferencePlane plane) => new()
    {
        OriginMm=new(0,0,0),
        Normal=plane switch { ReferencePlane.Top=>new(0,1,0), ReferencePlane.Right=>new(1,0,0), _=>new(0,0,1) },
        XDirection=plane==ReferencePlane.Right?new(0,0,-1):new(1,0,0)
    };
    private static Vector3 Point(SketchFrame f, ProfilePoint p)
    {
        var n = ModelVerification.Unit(f.Normal); var x = ModelVerification.Unit(f.XDirection);
        var y = new Vector3(n.Y*x.Z-n.Z*x.Y, n.Z*x.X-n.X*x.Z, n.X*x.Y-n.Y*x.X);
        return ModelVerification.Add(f.OriginMm, ModelVerification.Add(ModelVerification.Scale(x, p.Xmm), ModelVerification.Scale(y, p.Ymm)));
    }
}
