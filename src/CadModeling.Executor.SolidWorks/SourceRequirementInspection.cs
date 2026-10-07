using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyList<MeasuredCylinder> MeasureCylinders(IModelDoc2 model)
    {
        if(model is not IPartDoc part) throw new InvalidOperationException("需要对一个零件进行圆柱形要求的检查。");
        var result=new List<MeasuredCylinder>();
        foreach(var body in (part.GetBodies2(0,false) as object[]??[]).Cast<IBody2>())
        foreach(var face in (body.GetFaces() as object[]??[]).Cast<IFace2>())
        {
            var surface=(ISurface)face.GetSurface();
            if(!surface.IsCylinder()) continue;
            var data=ToDoubles(surface.CylinderParams,7,"圆柱参数");
            var origin=new Vector3(data[0]*1000,data[1]*1000,data[2]*1000);
            var direction=Unit(new(data[3],data[4],data[5]));
            var uv=ToDoubles(face.GetUVBounds(),4,"圆柱体 UV 边界");
            // UV values bound the actual trimmed face. Projection removes the radial coordinate.
            var p0=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[2],0,0),6,"圆柱体开始");
            var p1=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[3],0,0),6,"圆柱端面");
            Vector3 AxisPoint(double[] p)
            {
                var position=new Vector3(p[0]*1000,p[1]*1000,p[2]*1000);
                return ModelVerification.Add(origin,ModelVerification.Scale(direction,ModelVerification.Dot(ModelVerification.Sub(position,origin),direction)));
            }
            var mid=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"圆柱的法线");
            var point=new Vector3(mid[0]*1000,mid[1]*1000,mid[2]*1000);
            var radial=ModelVerification.Sub(point,AxisPoint(mid));
            var normal=new Vector3(mid[3],mid[4],mid[5]);
            if(!ModelVerification.Finite(normal)||ModelVerification.Norm(normal)<1e-12||ModelVerification.Norm(radial)<1e-12)
                throw new InvalidOperationException("圆柱的法向或径向方向无效。");
            if(face.FaceInSurfaceSense()) normal=ModelVerification.Scale(normal,-1);
            var interior=ModelVerification.Dot(normal,radial)<0;
            result.Add(new(AxisPoint(p0),AxisPoint(p1),direction,data[6]*1000,face.GetArea()*1_000_000,interior,(face.GetFeature() as IFeature)?.Name));
        }
        return result;
    }

    private static ModelVerificationResult VerifySourceRequirements(IModelDoc2 model,ModelVerificationSpec spec,
        GeometrySnapshot? geometry,out IReadOnlyList<MeasuredCylinder> cylinders)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.source_verification");
        cylinders=[];
        var measurementErrors=new List<VerificationCheckResult>();
        try { if(spec.CylinderGroups.Count>0) cylinders=MeasureCylinders(model); }
        catch(Exception ex)
        { measurementErrors.AddRange(spec.CylinderGroups.Select(c=>new VerificationCheckResult(c.Id,false,"unverifiable","实际的圆柱几何无法测量："+ex.Message))); }
        var dimensions=new Dictionary<string,double>(StringComparer.Ordinal);
        foreach(var check in spec.NativeDimensions)
        {
            var split=check.DimensionName.Split('@');
            // Suppressed features may still retain old parameter values; do not accept them.
            var owner=split.Length>=2?FindFeatureByName(model,split[1]):null;
            if(owner is null||owner.IsSuppressed()) continue;
            var dimension=model.Parameter(check.DimensionName) as IDimension;
            if(dimension is not null) dimensions[check.DimensionName]=dimension.GetSystemValue2("");
        }
        var measuredSpec=measurementErrors.Count>0?spec with {CylinderGroups=[]}:spec;
        var localMeasurements=MeasureLocalRequirements(model,spec);
        var wholeModel=spec.WholeModelChecks.Count>0?MeasureWholeModel(model,geometry):null;
        var evaluated=ModelVerification.Evaluate(measuredSpec,cylinders,dimensions,geometry,localMeasurements,wholeModel,MeasureShapeRequirements(model,spec));
        return new(evaluated.Checks.Concat(measurementErrors).ToArray());
    }

    private static IReadOnlyDictionary<string,IReadOnlyList<LocalPointMeasurement>> MeasureLocalRequirements(IModelDoc2 model,ModelVerificationSpec spec)
    {
        var result=new Dictionary<string,IReadOnlyList<LocalPointMeasurement>>(StringComparer.Ordinal);
        if(spec.SurfaceSamples.Count+spec.BoundaryClearances.Count==0)return result;
        var requests=spec.SurfaceSamples.Select(c=>(c.Id,c.PointsMm,Surface:true,c.ToleranceMm))
            .Concat(spec.BoundaryClearances.Select(c=>(c.Id,c.PointsMm,Surface:false,c.ToleranceMm))).ToArray();
        try
        {
            if(model is not IPartDoc part)throw new InvalidOperationException("局部几何检查需要一个实体部件。");
            var bodies=(part.GetBodies2(0,false) as object[]??[]).Concat(part.GetBodies2(1,false) as object[]??[]).Cast<IBody2>().ToArray();
            var faces=bodies.SelectMany(b=>(b.GetFaces() as object[]??[]).Cast<IFace2>()).ToArray();
            if(bodies.Length==0||faces.Length==0||faces.Length!=bodies.Sum(b=>b.GetFaceCount()))throw new InvalidOperationException("边界库存不完整。");
            var areaChecks=spec.SurfaceSamples.Where(c=>c.ExpectedAreaMm2 is not null).ToArray();
            var areaBudget=areaChecks.Length>0?areaChecks.Min(c=>c.AreaToleranceMm2)/(4*faces.Length):0;
            var areas=new Dictionary<int,(double Area,double Error)>();
            var areaFailures=new Dictionary<int,string>();
            foreach(var request in requests)
            {
                var points=new List<LocalPointMeasurement>();
                foreach(var point in request.PointsMm)
                {
                    var measured=new List<LocalFaceMeasurement>();string? error=null;
                    for(var faceIndex=0;faceIndex<faces.Length;faceIndex++)
                    {
                        var face=faces[faceIndex];
                        try
                        {
                            // IFace2 (trimmed topology), not ISurface's unbounded analytic extension.
                            var cp=ToDoubles(face.GetClosestPointOn(point.X/1000,point.Y/1000,point.Z/1000),5,"草图面上最接近点");
                            var closest=new Vector3(cp[0]*1000,cp[1]*1000,cp[2]*1000);
                            if(!ModelVerification.Finite(closest))throw new InvalidOperationException("非有限最接近点。");
                            LocalSurfaceKind? kind=null;double? diameter=null,angle=null,area=null,radius=null;Vector3? center=null;double areaError=0;var normal=new Vector3(0,0,0);
                            if(request.Surface&&ModelVerification.Norm(ModelVerification.Sub(closest,point))<=request.ToleranceMm)
                            {
                                var surface=(ISurface)face.GetSurface();
                                if(surface.IsPlane())kind=LocalSurfaceKind.Plane;
                                else if(surface.IsCylinder()) {kind=LocalSurfaceKind.Cylinder;diameter=ToDoubles(surface.CylinderParams,7,"圆柱参数")[6]*2000;}
                                else if(surface.IsCone()) {kind=LocalSurfaceKind.Cone;angle=Math.Abs(ToDoubles(surface.ConeParams2,11,"圆锥参数")[7])*180/Math.PI;}
                                else if(surface.IsSphere()) {kind=LocalSurfaceKind.Sphere;var p=ReadSphereParameters(surface);radius=p.RadiusMm;center=p.CenterMm;}
                                else kind=WholeSurfaceKind(surface);
                                if(kind is not null)
                                {
                                    if(areaChecks.Any(c=>c.Id==request.Id))
                                    {
                                        if(areaFailures.TryGetValue(faceIndex,out var priorFailure))throw new InvalidOperationException(priorFailure);
                                        if(!areas.TryGetValue(faceIndex,out var measuredArea))
                                        {
                                            try {areas[faceIndex]=measuredArea=MeasureBoundaryFaceArea(face,areaBudget);}
                                            catch(Exception ex) {areaFailures[faceIndex]=ex.Message;throw;}
                                        }
                                        area=measuredArea.Area;areaError=measuredArea.Error;
                                    }
                                    var evaluated=ToDoubles(surface.Evaluate(cp[3],cp[4],0,0),6,"本地曲面法线");
                                    normal=new(evaluated[3],evaluated[4],evaluated[5]);
                                    if(face.FaceInSurfaceSense())normal=ModelVerification.Scale(normal,-1);
                                }
                            }
                            measured.Add(new(closest,normal,kind,diameter,angle,faceIndex,area,areaError,radius,center));
                        }
                        catch(Exception ex) {error=ex.Message;break;}
                    }
                    points.Add(new(measured,error is null&&measured.Count==faces.Length,error));
                }
                result[request.Id]=points;
            }
        }
        catch(Exception ex)
        {
            foreach(var request in requests)result[request.Id]=request.PointsMm.Select(_=>new LocalPointMeasurement([],false,ex.Message)).ToArray();
        }
        return result;
    }

    private static IFeature? FindFeatureByName(IModelDoc2 model,string name)
    {
        var matches=new List<IFeature>();var seen=new HashSet<int>();
        void Visit(IFeature f)
        {
            if(!seen.Add(f.GetID()))return;
            if(f.Name.Equals(name,StringComparison.OrdinalIgnoreCase))matches.Add(f);
            for(var child=f.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature())Visit(child);
        }
        for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature())Visit(f);
        return matches.Count==1?matches[0]:null;
    }
}
