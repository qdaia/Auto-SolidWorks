using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Conservative world envelopes: separation proves no contact; envelope overlap never proves a valid merge.</summary>
public static class ConnectionPreflight
{
    private sealed record Box(double[] Min,double[] Max);
    public static IReadOnlyList<ModelingDiagnostic> Validate(GenericModelDraft draft)
    {
        var result=new List<ModelingDiagnostic>();var previous=new List<Box>();var complete=draft.SourceModelPath is null;
        foreach(var op in draft.Operations)
        {
            if(op.Type==GenericOperationKind.NativeFeature && op.Feature?.Kind!=NativeFeatureKind.Hole) complete=false;
            if(op.Type!=GenericOperationKind.ExtrudeBoss)continue;
            var box=Envelope(draft,op);
            if(box is null){complete=false;continue;}
            if(op.Merge && previous.Count>0 && complete)
            {
                var contacts=previous.Select(b=>Enumerable.Range(0,3).Select(i=>Math.Min(b.Max[i],box.Max[i])-Math.Max(b.Min[i],box.Min[i])).ToArray()).ToArray();
                if(contacts.All(c=>c.Any(v=>v < -1e-7)))
                    result.Add(new("BOSS_CONNECTION_GAP",DiagnosticSeverity.Error,"该合并拉伸与所有先前可计算实体包络分离，无法连接。回查耳根/壁厚的源投影和基准；不得自动延长或加料。","operations."+op.Id));
                else if(contacts.Where(c=>c.All(v=>v>=-1e-7)).All(c=>c.Count(v=>Math.Abs(v)<=1e-7)>=2))
                    result.Add(new("BOSS_EDGE_CONTACT_RISK",DiagnosticSeverity.Warning,"实体包络最多在棱或点接触，存在零厚度风险；保持源尺寸，原生前先复核连接拓扑。","operations."+op.Id));
            }
            previous.Add(box);
        }
        return result;
    }
    private static Box? Envelope(GenericModelDraft draft,GenericOperationDraft op)
    {
        if(op.EndCondition is not (ExtrudeEndCondition.Blind or ExtrudeEndCondition.MidPlane) || op.DepthMm is not >0)return null;
        var sketch=draft.Operations.FirstOrDefault(o=>o.Id==op.SketchId);
        if(sketch is null || sketch.FaceAttachment is not null || sketch.PlaneId is not null)return null;
        var frame=sketch.Frame??new SketchFrame{Normal=sketch.Plane switch{ReferencePlane.Top=>new(0,1,0),ReferencePlane.Right=>new(1,0,0),_=>new(0,0,1)},XDirection=sketch.Plane==ReferencePlane.Right?new(0,0,-1):new(1,0,0)};
        if(!ModelVerification.Finite(frame.OriginMm)||!ModelVerification.Finite(frame.Normal)||!ModelVerification.Finite(frame.XDirection)||
            ModelVerification.Norm(frame.Normal)<1e-12||ModelVerification.Norm(frame.XDirection)<1e-12)return null;
        var n=ModelVerification.Unit(frame.Normal);var x=ModelVerification.Unit(frame.XDirection);
        if(Math.Abs(ModelVerification.Dot(n,x))>1e-8)return null;
        var y=new Vector3(n.Y*x.Z-n.Z*x.Y,n.Z*x.X-n.X*x.Z,n.X*x.Y-n.Y*x.X);
        var points=new List<ProfilePoint>();
        if(sketch.DrivenProfile is {} driven)
        {
            if(!double.IsFinite(driven.RadiusMm)||driven.RadiusMm<=0)return null;
            var r=driven.RadiusMm;
            if(driven.Kind==DrivenProfileKind.RoundEar && driven.WebHeightMm is >0)
                points.AddRange([new(-r,0),new(r,driven.WebHeightMm.Value+r)]);
            else if(driven.Kind is DrivenProfileKind.HalfAnnulus or DrivenProfileKind.QuarterAnnulus)
                points.AddRange([new(driven.Kind==DrivenProfileKind.HalfAnnulus?-r:0,0),new(r,r)]);
            else return null;
        }
        else foreach(var p in sketch.Primitives.Where(p=>!p.Construction))
        {
            if(p.Type==GenericPrimitiveKind.CenteredRectangle && p.WidthMm is >0 && p.HeightMm is >0)
                points.AddRange([new((p.CenterXmm??0)-p.WidthMm.Value/2,(p.CenterYmm??0)-p.HeightMm.Value/2),new((p.CenterXmm??0)+p.WidthMm.Value/2,(p.CenterYmm??0)+p.HeightMm.Value/2)]);
            else if(p.Type==GenericPrimitiveKind.Circle && p.DiameterMm is >0)
                points.AddRange([new((p.CenterXmm??0)-p.DiameterMm.Value/2,(p.CenterYmm??0)-p.DiameterMm.Value/2),new((p.CenterXmm??0)+p.DiameterMm.Value/2,(p.CenterYmm??0)+p.DiameterMm.Value/2)]);
            else if(p.Type==GenericPrimitiveKind.Polygon)points.AddRange(p.Points);
            else return null; // An unsupported curve cannot justify a disconnected-solid verdict.
        }
        if(points.Count==0 || points.Any(p=>!double.IsFinite(p.Xmm)||!double.IsFinite(p.Ymm)))return null;
        var start=op.StartOffsetMm*(op.ReverseStartOffset?-1:1);
        var distance=op.DepthMm.Value*(op.ReverseDirection?-1:1);
        var lo=op.EndCondition==ExtrudeEndCondition.MidPlane?start-op.DepthMm.Value/2:Math.Min(start,start+distance);
        var hi=op.EndCondition==ExtrudeEndCondition.MidPlane?start+op.DepthMm.Value/2:Math.Max(start,start+distance);
        var world=(from a in new[]{points.Min(p=>p.Xmm),points.Max(p=>p.Xmm)}
            from b in new[]{points.Min(p=>p.Ymm),points.Max(p=>p.Ymm)} from c in new[]{lo,hi}
            select ModelVerification.Add(frame.OriginMm,ModelVerification.Add(ModelVerification.Scale(x,a),ModelVerification.Add(ModelVerification.Scale(y,b),ModelVerification.Scale(n,c))))).ToArray();
        return new([world.Min(p=>p.X),world.Min(p=>p.Y),world.Min(p=>p.Z)],[world.Max(p=>p.X),world.Max(p=>p.Y),world.Max(p=>p.Z)]);
    }
}
