using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyList<MeasuredCone> MeasureCones(IModelDoc2 model)
    {
        if(model is not IPartDoc part)return[];
        return (part.GetBodies2(0,false) as object[]??[]).OfType<IBody2>()
            .SelectMany(b=>b.GetFaces() as object[]??[]).OfType<IFace2>()
            .Where(f=>((ISurface)f.GetSurface()).IsCone()).Select(f=>MeasureCone(model,f)).ToArray();
    }

    private static MeasuredCone MeasureCone(IModelDoc2 model,IFace2 face)
    {
        var surface=(ISurface)face.GetSurface();
        if(!surface.IsCone())throw new InvalidOperationException("Native face is not an analytic cone.");
        var p=ToDoubles(surface.ConeParams2,11,"native cone parameters");
        var origin=new Vector3(p[0]*1000,p[1]*1000,p[2]*1000);
        var axis=ModelVerification.Unit(new(p[3],p[4],p[5]));
        var uv=ToDoubles(face.GetUVBounds(),4,"trimmed cone bounds");
        var a=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[2],0,0),6,"cone start");
        var b=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[3],0,0),6,"cone end");
        var mid=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"cone normal");
        Vector3 Point(double[] values)=>new(values[0]*1000,values[1]*1000,values[2]*1000);
        Vector3 OnAxis(double[] values)=>AxisProjection(origin,axis,Point(values));
        var start=OnAxis(a);var end=OnAxis(b);
        var ra=ModelVerification.Norm(ModelVerification.Sub(Point(a),start));
        var rb=ModelVerification.Norm(ModelVerification.Sub(Point(b),end));
        var normal=new Vector3(mid[3],mid[4],mid[5]);
        if(face.FaceInSurfaceSense())normal=ModelVerification.Scale(normal,-1);
        var radial=ModelVerification.Sub(Point(mid),OnAxis(mid));
        var angle=Math.Abs(p[7])*360/Math.PI;
        if(!ModelVerification.Finite(normal)||ModelVerification.Norm(normal)<=1e-12||!double.IsFinite(angle)||angle<=0||angle>=180)
            throw new InvalidOperationException("Native conical wall has invalid normal/angle evidence.");
        var token=Persistent(model,face);
        var boundary=ConeHasCompleteBoundaries(model,face,start,end,axis,ra,rb,token);
        return new(start,end,axis,ra,rb,angle,ModelVerification.Dot(normal,radial)<0,boundary.Complete,token,(face.GetFeature() as IFeature)?.Name){BoundaryEvidence=boundary.Evidence};
    }

    private static (bool Complete,string Evidence) ConeHasCompleteBoundaries(IModelDoc2 model,IFace2 face,Vector3 start,Vector3 end,Vector3 axis,double ra,double rb,string? faceToken)
    {
        const double tolerance=.001;
        var height=ModelVerification.Norm(ModelVerification.Sub(end,start));
        if(faceToken is null||ra<=0||rb<=0||height<=0||!double.IsFinite(ra+rb+height))return(false,"Invalid finite annular-cone dimensions or face identity.");
        // IFace2.GetArea is approximate. Completeness is established by the native
        // trimmed topology: two full end circles and only same-face periodic seams.
        // Circular support alone is insufficient: a slot leaves circular arcs.
        var atStart=0;var atEnd=0;
        foreach(var edge in (face.GetEdges() as object[]??[]).OfType<IEdge>())
        {
            var curve=(ICurve)edge.GetCurve();
            if(curve.IsCircle())
            {
                var trim=edge.GetCurveParams3();
                var sweep=Math.Abs(trim.UMaxValue-trim.UMinValue);
                if(!double.IsFinite(sweep)||Math.Abs(sweep-2*Math.PI)>1e-8)return(false,$"Boundary circle is trimmed to an arc: sweep={sweep:R} rad.");
                var data=ToDoubles(curve.CircleParams,7,"cone boundary circle");
                var center=new Vector3(data[0]*1000,data[1]*1000,data[2]*1000);
                var direction=ModelVerification.Unit(new(data[3],data[4],data[5]));
                if(Math.Abs(ModelVerification.Dot(axis,direction))<.999999)return(false,"Boundary circle has a different axis.");
                var first=ModelVerification.Norm(ModelVerification.Sub(center,start))<=tolerance&&Math.Abs(data[6]*1000-ra)<=tolerance;
                var last=ModelVerification.Norm(ModelVerification.Sub(center,end))<=tolerance&&Math.Abs(data[6]*1000-rb)<=tolerance;
                if(!first&&!last)return(false,$"Boundary circle disagrees with trimmed ends: center={center}, radius={data[6]*1000:R} mm.");
                if(first)atStart++;if(last)atEnd++;
            }
            else if(curve.IsLine())
            {
                // A true periodic seam has the very same conical face on both sides.
                // Split or slotted walls remain unsupported rather than hiding an outlet.
                var adjacent=(edge.GetTwoAdjacentFaces2() as object[]??[]).OfType<IFace2>().ToArray();
                if(adjacent.Length!=2||adjacent.Any(f=>Persistent(model,f)!=faceToken))return(false,$"Line boundary is not a same-face periodic seam: adjacent={adjacent.Length}, same-face={adjacent.Count(f=>Persistent(model,f)==faceToken)}.");
            }
            else return(false,"Boundary curve is neither an analytic circle nor a line.");
        }
        return(atStart==1&&atEnd==1,$"Full end circles: start={atStart}, end={atEnd}; every other boundary is a same-face periodic seam.");
    }

    private static MeasuredCosmeticThread? ReadCosmeticThread(IModelDoc2 model,IFeature feature)
    {
        if(feature.IsSuppressed()||feature.GetDefinition() is not ICosmeticThreadFeatureData thread)return null;
        var accessed=false;
        try
        {
            accessed=thread.AccessSelections(model,null);
            if(!accessed)throw new InvalidOperationException("Native cosmetic thread selection access failed.");
            var edge=(IEdge?)thread.Edge??throw new InvalidOperationException("Native cosmetic thread has no entrance edge.");
            var curve=(ICurve)edge.GetCurve();
            if(!curve.IsCircle())throw new InvalidOperationException("Cosmetic thread entrance is not circular.");
            var data=ToDoubles(curve.CircleParams,7,"thread entrance circle");
            var token=Persistent(model,edge);
            var diameter=thread.Diameter*1000;var depth=thread.BlindDepth*1000;
            var through=thread.ApplyThread==(int)swCosmeticThreadType_e.swApplyCosmeticThread_ThroughFeature;
            var blind=thread.ApplyThread==(int)swCosmeticThreadType_e.swApplyCosmeticThread_Blind;
            var complete=token is not null&&thread.DiameterType==(int)swCosmeticThreadDiameterType_e.swCosmeticThread_MajorDiameter&&
                double.IsFinite(diameter)&&diameter>0&&!string.IsNullOrWhiteSpace(thread.ThreadCallout)&&(through||blind&&double.IsFinite(depth)&&depth>0);
            return new(feature.Name,token,new(data[0]*1000,data[1]*1000,data[2]*1000),ModelVerification.Unit(new(data[3],data[4],data[5])),
                data[6]*1000,diameter,thread.ThreadCallout,through,through?0:depth,complete);
        }
        finally{if(accessed)thread.ReleaseSelectionAccess();}
    }
}
