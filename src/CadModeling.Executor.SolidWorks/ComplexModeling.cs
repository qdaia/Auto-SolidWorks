using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static object[] BodiesWithNativeFeatureMembership(IModelDoc2 model,object[] bodies,IFeature feature)
    {
        // Trims often generate edges but no new faces. Native body feature membership
        // establishes scope; an empty/unknown membership never becomes all part bodies.
        return bodies.OfType<IBody2>().Where(body=>(body.GetFeatures() as object[]??[]).OfType<IFeature>()
            .Any(owner=>SameNativeEntity(model,feature,owner))).Cast<object>().ToArray();
    }

    private static object CommitVerifiedTrimPoints(IModelDoc2 model,IBody2[] originals,Vector3[] points,bool sew)
    {
        if(originals.Length==0||originals.Length!=points.Length)throw new ArgumentException("剪裁需要每个目标一个已证明的内部点。");
        for(var i=0;i<originals.Length;i++)
        {
            var p=points[i];var data=model.ISelectionManager.CreateSelectData();data.Mark=0;
            data.X=Mm(p.X);data.Y=Mm(p.Y);data.Z=Mm(p.Z);
            if(!originals[i].Select2(true,data))throw new InvalidOperationException("TRIM_COMMIT: 在已证明的点上目标选择失败。");
        }
        return model.FeatureManager.PostTrimSurface(sew)??throw new InvalidOperationException("TRIM_COMMIT: SolidWorks 返回了没有原生剪裁特征。");
    }
    private static bool HasComplexLoftControls(LoftOptions? l) => l is not null && (l.CenterlineId is not null || l.GuideInfluence is not null
        || l.StartTangentLengthMm is not null || l.EndTangentLengthMm is not null || l.ReverseStartTangent || l.ReverseEndTangent);

    private static void SelectLoftCenterline(IModelDoc2 model, NativeFeatureOptions o, IReadOnlyDictionary<string,object> objects)
    {
        if(o.Loft?.CenterlineId is not { } id)return;
        var feature=ResolveFeature(model,objects,id);
        if(feature.GetSpecificFeature2() is ISketch sketch)
        {
            var segments=(sketch.GetSketchSegments() as object[]??[]).OfType<ISketchSegment>().Where(s=>!s.ConstructionGeometry).ToArray();
            if(segments.Length==0)throw new InvalidOperationException("LOFT_CENTERLINE: 没有可执行的草图段。");
        }
        else if(feature.GetDefinition() is not IHelixFeatureData)throw new InvalidOperationException("LOFT_CENTERLINE: 预期找到一个原生草图或螺旋线。");
        SelectFeature(model,feature,true,4);
    }

    private static void ConfigureComplexLoft(IModelDoc2 model,IFeature feature,NativeFeatureOptions o)
    {
        if(!HasComplexLoftControls(o.Loft))return;
        if(feature.GetDefinition() is not ILoftFeatureData data || !data.AccessSelections(model,null))
            throw new InvalidOperationException("LOFT_CONTROL: 原生定义不可达。");
        var changed=false;
        try
        {
            var l=o.Loft!;
            if(l.StartTangentLengthMm is { } start)data.StartTangentLength=Mm(start);
            if(l.EndTangentLengthMm is { } end)data.EndTangentLength=Mm(end);
            if(l.StartCondition==SurfaceEndCondition.NormalToProfile)data.ReverseStartTangentDirection=l.ReverseStartTangent;
            if(l.EndCondition==SurfaceEndCondition.NormalToProfile)data.ReverseEndTangentDirection=l.ReverseEndTangent;
            if(l.GuideInfluence is { } influence)data.GuideCurveInfluence=(int)influence;
            if(!feature.ModifyDefinition(data,model,null))throw new InvalidOperationException("LOFT_CONTROL: 更新请求的定义失败。");
            changed=true;
        }
        finally{if(!changed)data.ReleaseSelectionAccess();}
    }

    private static void VerifyComplexLoft(IModelDoc2 model,IFeature feature,NativeFeatureOptions o,IReadOnlyDictionary<string,object>? objects=null)
    {
        if(!HasComplexLoftControls(o.Loft))return;
        if(feature.GetDefinition() is not ILoftFeatureData data || !data.AccessSelections(model,null))
            throw new InvalidOperationException("LOFT_CONTROL: 保留的定义不可访问。");
        try
        {
            var l=o.Loft!;
            if(data.GetProfileCount()!=o.ProfileIds.Count || data.GetGuideCurvesCount()!=o.GuideIds.Count || data.Close!=l.Close
                || data.MaintainTangency!=l.MaintainTangency || data.StartTangencyType!=(short)l.StartCondition || data.EndTangencyType!=(short)l.EndCondition
                || l.StartTangentLengthMm is { } start && (!double.IsFinite(data.StartTangentLength) || Math.Abs(data.StartTangentLength-Mm(start))>1e-9)
                || l.EndTangentLengthMm is { } end && (!double.IsFinite(data.EndTangentLength) || Math.Abs(data.EndTangentLength-Mm(end))>1e-9)
                || l.StartCondition==SurfaceEndCondition.NormalToProfile && data.ReverseStartTangentDirection!=l.ReverseStartTangent
                || l.EndCondition==SurfaceEndCondition.NormalToProfile && data.ReverseEndTangentDirection!=l.ReverseEndTangent
                || l.GuideInfluence is { } influence && data.GuideCurveInfluence!=(int)influence)
                throw new InvalidOperationException("LOFT_CONTROL: 轮廓、引导、切点长度/方向或影响没有在原生创建中存活。");
            if(l.CenterlineId is { } id)
            {
                var expected=ResolveFeature(model,objects??new Dictionary<string,object>(),id);
                var retained=data.Centerline;
                var same=retained is IFeature actual && SameNativeEntity(model,expected,actual);
                if(!same && expected.GetSpecificFeature2() is ISketch sketch)
                {
                    var retainedSketch=retained as ISketch ?? (retained as ISketchSegment)?.GetSketch() as ISketch;
                    same=retainedSketch is not null && SameNativeEntity(model,sketch,retainedSketch);
                }
                if(!same)
                    throw new InvalidOperationException("LOFT_CENTERLINE: 无法证明保留的中心线身份。");
            }
        }
        finally{data.ReleaseSelectionAccess();}
    }

    // A bounding box can contain a point outside a concave/curved trimmed patch.
    // Select only by distance to the native trimmed faces; reject points near piece boundaries.
    private static bool TrimRegionContainsPoint(IBody2 body,Vector3 point,double toleranceMm)
    {
        if(body.GetType()!=(int)swBodyType_e.swSheetBody)throw new InvalidOperationException("TRIM_REGION: 预期得到一个曲面体。");
        var faces=(body.GetFaces() as object[]??[]).OfType<IFace2>().ToArray();
        if(faces.Length==0)throw new InvalidOperationException("TRIM_REGION：原生面清单缺失。");
        bool matched=false;
        foreach(var face in faces)
        {
            var closest=ToDoubles(face.GetClosestPointOn(Mm(point.X),Mm(point.Y),Mm(point.Z)),3,"修剪区域最接近点");
            if(closest.Any(x=>!double.IsFinite(x)))throw new InvalidOperationException("TRIM_REGION: 最近点无效。");
            if(PointDistance(point,new(closest[0]*1000,closest[1]*1000,closest[2]*1000))>toleranceMm)continue;
            matched=true;
            foreach(var edge in (face.GetEdges() as object[]??[]).OfType<IEdge>())
            {
                var nearest=ToDoubles(edge.GetClosestPointOn(Mm(point.X),Mm(point.Y),Mm(point.Z)),3,"剪裁区域边界点");
                if(nearest.Any(x=>!double.IsFinite(x)))throw new InvalidOperationException("TRIM_REGION: 边界测量无效。");
                if(PointDistance(point,new(nearest[0]*1000,nearest[1]*1000,nearest[2]*1000))<=toleranceMm)
                    throw new InvalidOperationException("TRIM_REGION: 该点位于或过于接近边界；请提供一个内部曲面点。");
            }
        }
        return matched;
    }
}
