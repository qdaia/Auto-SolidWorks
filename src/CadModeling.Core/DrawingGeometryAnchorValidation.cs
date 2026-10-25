using CadModeling.Ir;
namespace CadModeling.Core;

public static class DrawingGeometryAnchorValidation
{
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors=new List<ModelingDiagnostic>();
        errors.AddRange(DrawingCircularProjectionValidation.Validate(context));
        void Error(string code,string id,string message)=>errors.Add(new(code,DiagnosticSeverity.Error,message,"drawing_context.geometry_anchors."+id));
        if(!context.RequireGeometryAnchors && context.GeometryAnchors.Count==0 &&
            context.Dimensions.All(f=>f.BindingEvidence is not {StartAnchorId:not null} and not {EndAnchorId:not null} and not {TargetAnchorId:not null})) return errors;
        var anchors=new Dictionary<string,DrawingGeometryAnchor>(StringComparer.Ordinal);
        DrawingOmissionInventory? inventory=null;
        try
        {
            if(SourceWorkflow.Evidence(context) is { } review)
            {
                inventory=DrawingOmissionValidation.Load(review.InventoryPath,review.InventorySha256);
                if(!string.Equals(Path.GetFullPath(inventory.SourcePath),Path.GetFullPath(context.SourcePath),StringComparison.OrdinalIgnoreCase))
                { inventory=null; Error("ANCHOR_SOURCE","","几何锚点库存不属于当前原图。"); }
            }
            else Error("ANCHOR_SOURCE","","几何锚点必须引用真实原图库存。");
        }
        catch(Exception e) when(e is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { Error("ANCHOR_SOURCE","",e.Message); }
        foreach(var anchor in context.GeometryAnchors)
        {
            if(string.IsNullOrWhiteSpace(anchor.Id) || !anchors.TryAdd(anchor.Id,anchor))
            { Error("ANCHOR_ID",anchor.Id,"几何锚点 ID 必须唯一且非空。"); continue; }
            var view=context.Views.FirstOrDefault(v=>v.Id==anchor.ViewId);
            var feature=context.Features.FirstOrDefault(f=>f.Id==anchor.FeatureId);
            if(view is null || feature is null || !feature.ViewIds.Contains(anchor.ViewId) ||
                string.IsNullOrWhiteSpace(anchor.PhysicalEntityId) || !Enum.IsDefined(anchor.Kind) ||
                anchor.State!=DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(anchor.Evidence) || !DrawingOmissionValidation.ValidBox(anchor.Bounds))
                Error("ANCHOR_EVIDENCE",anchor.Id,"锚点需要有效特征/视图、物理对象身份、原图区域及已确认的来源解释。");
            if(anchor.ObservationIds.Count+anchor.CandidateIds.Count==0 || anchor.ObservationIds.Distinct(StringComparer.Ordinal).Count()!=anchor.ObservationIds.Count ||
                anchor.CandidateIds.Distinct(StringComparer.Ordinal).Count()!=anchor.CandidateIds.Count ||
                anchor.CandidateIds.Any(id=>inventory is null || !inventory.Candidates.Any(c=>c.Id==id && c.PageNumber==view?.PageNumber &&
                    c.Kind is DrawingCandidateKind.Geometry or DrawingCandidateKind.ResidualInk && Overlap(c.Bounds,anchor.Bounds))) ||
                anchor.ObservationIds.Any(id=>inventory is null || !inventory.Candidates.Any(c=>c.PageNumber==view?.PageNumber &&
                    c.ObservationIds.Contains(id) && c.Kind is DrawingCandidateKind.Geometry or DrawingCandidateKind.ResidualInk && Overlap(c.Bounds,anchor.Bounds))))
                Error("ANCHOR_OBSERVATION",anchor.Id,"锚点必须引用同页、相交区域的真实几何/残余图像观察，不能借用数字或远处对象。");
        }
        foreach(var group in context.GeometryAnchors.GroupBy(a=>a.PhysicalEntityId))
            if(group.Select(a=>a.Kind).Distinct().Count()>1 || group.Select(a=>a.FeatureId).Distinct().Count()>1)
                Error("ANCHOR_PHYSICAL_ID",group.Key,"同一物理实体在多视图中的类型与所属特征必须一致。");
        var measurements=new Dictionary<(string,string),string>();
        var circularValues=new Dictionary<(string,string),(double Diameter,string FactId)>();
        var critical=context.Features.Where(f=>f.Critical).SelectMany(f=>f.CriticalParameters).Select(p=>p.DimensionId).ToHashSet();
        foreach(var fact in context.Dimensions)
        {
            var e=fact.BindingEvidence;
            var required=context.RequireGeometryAnchors && (fact.Critical || critical.Contains(fact.Id));
            if(e is null)
            { if(required) Error("ANCHOR_BINDING_REQUIRED",fact.Id,"关键尺寸缺少对象绑定证据。"); continue; }
            DrawingGeometryAnchor? Get(string? id,bool target)
            {
                if(id is null) return null;
                if(!anchors.TryGetValue(id,out var anchor) || anchor.ViewId!=e.ViewId || target && anchor.FeatureId!=e.FeatureId)
                { Error("ANCHOR_BINDING",fact.Id,"锚点不属于尺寸的视图或目标特征。"); return null; }
                return anchor;
            }
            var start=Get(e.StartAnchorId,false);var end=Get(e.EndAnchorId,false);var target=Get(e.TargetAnchorId,true);
            if(e.QuantityKind is DrawingQuantityKind.Linear or DrawingQuantityKind.Angle)
            {
                if (SourceAnnotationValidation.ChamferAnnotation(context,fact) is not null)
                {
                    if(target is null || target.Construction is not null || target.Kind is not (DrawingAnchorKind.Line or DrawingAnchorKind.Contour) || !Overlap(e.TargetBounds,target.Bounds))
                        Error("ANCHOR_CHAMFER",fact.Id,"C倒角须定位具体倒角边，并由公共引线证明归属。");
                    else Compare(target.PhysicalEntityId+":chamfer_distance");
                    continue;
                }
                if (SourceAnnotationValidation.CompoundAnnotation(context, fact) is { } compound)
                {
                    if (target is null || target.Kind is not (DrawingAnchorKind.Circle or DrawingAnchorKind.Arc))
                        Error("ANCHOR_COMPOUND_HOLE", fact.Id, "复合孔深/角度须绑定具体孔圈，并由整条标注的公共引线证明归属。");
                    else Compare(target.PhysicalEntityId + ":" + fact.SourceComponentKey);
                    continue;
                }
                if(required || e.StartAnchorId is not null || e.EndAnchorId is not null)
                {
                    if(start is null || end is null || start.PhysicalEntityId==end.PhysicalEntityId)
                        Error("ANCHOR_ENDPOINTS",fact.Id,"起止端点必须指向两个不同的已确认物理对象。");
                    else
                    {
                        if(e.QuantityKind==DrawingQuantityKind.Angle && (start.Kind is not (DrawingAnchorKind.Line or DrawingAnchorKind.Axis or DrawingAnchorKind.Plane) ||
                            end.Kind is not (DrawingAnchorKind.Line or DrawingAnchorKind.Axis or DrawingAnchorKind.Plane)))
                            Error("ANCHOR_ANGLE",fact.Id,"角度端点需要线、轴或平面。");
                        var signature=System.Text.Json.JsonSerializer.Serialize(new[]{start.PhysicalEntityId,end.PhysicalEntityId}.Order(StringComparer.Ordinal));
                        Compare(signature);
                    }
                }
            }
            else if(e.QuantityKind is DrawingQuantityKind.Radius or DrawingQuantityKind.Diameter)
            {
                if(required || e.TargetAnchorId is not null)
                {
                    var circular=DrawingCircularProjectionValidation.CircularTarget(context,target);
                    if(circular is null)
                        Error("ANCHOR_CIRCULAR",fact.Id,"圆径必须定位到具体圆/弧轮廓。");
                    else
                    {
                        Compare(circular.PhysicalEntityId);
                        if(!Overlap(e.TargetBounds,target!.Bounds))
                            Error("ANCHOR_TARGET_REGION",fact.Id,"圆径引线目标区域与所选圆/弧锚点不相交；回查引线落点，不能借用邻近圆。");
                        if(fact.Status is DrawingFactStatus.Stated or DrawingFactStatus.Derived &&
                            fact.Unit is DrawingValueUnit.Millimeter or DrawingValueUnit.Inch or DrawingValueUnit.Meter)
                        {
                            var diameter=SourceInterpretationValidation.SourceLiteralValue(fact)*DimensionExpressions.Scale(fact.Unit)*
                                (e.QuantityKind==DrawingQuantityKind.Radius?2:1);
                            var key=(circular.FeatureId,circular.PhysicalEntityId);
                            if(!double.IsFinite(diameter) || diameter<=0)
                                Error("ANCHOR_CIRCULAR_VALUE",fact.Id,"源圆/弧的半径或直径必须为有限正数。");
                            else if(circularValues.TryGetValue(key,out var prior) && !DimensionExpressions.Equal(prior.Diameter,diameter))
                                Error("ANCHOR_CIRCULAR_CONFLICT",fact.Id,"同一物理圆/弧与尺寸 "+prior.FactId+" 的半径/直径不一致；不同 measurement_id 也不能隐藏冲突。");
                            else circularValues.TryAdd(key,(diameter,fact.Id));
                        }
                    }
                }
            }
            void Compare(string signature)
            {
                var key=(e.FeatureId,e.MeasurementId);
                if(measurements.TryGetValue(key,out var prior) && prior!=signature)
                    Error("ANCHOR_CROSS_VIEW",fact.Id,"同一测量在不同视图绑定到了不同物理对象；数值相等不能消除冲突。");
                else measurements[key]=signature;
            }
        }
        return errors;
    }
    private static bool Overlap(DrawingReviewBox a,DrawingReviewBox b)=>
        Math.Min(a.Right,b.Right)>Math.Max(a.Left,b.Left) && Math.Min(a.Bottom,b.Bottom)>Math.Max(a.Top,b.Top);
}
