using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyList<MeasuredCylinder> MeasureCylinders(IModelDoc2 model)
    {
        if(model is not IPartDoc part) throw new InvalidOperationException("Cylindrical requirement inspection requires a part.");
        var result=new List<MeasuredCylinder>();
        foreach(var body in (part.GetBodies2(0,false) as object[]??[]).Cast<IBody2>())
        foreach(var face in (body.GetFaces() as object[]??[]).Cast<IFace2>())
        {
            var surface=(ISurface)face.GetSurface();
            if(!surface.IsCylinder()) continue;
            var data=ToDoubles(surface.CylinderParams,7,"cylinder parameters");
            var origin=new Vector3(data[0]*1000,data[1]*1000,data[2]*1000);
            var direction=Unit(new(data[3],data[4],data[5]));
            var uv=ToDoubles(face.GetUVBounds(),4,"cylinder UV bounds");
            // UV values bound the actual trimmed face. Projection removes the radial coordinate.
            var p0=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[2],0,0),6,"cylinder start");
            var p1=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,uv[3],0,0),6,"cylinder end");
            Vector3 AxisPoint(double[] p)
            {
                var position=new Vector3(p[0]*1000,p[1]*1000,p[2]*1000);
                return ModelVerification.Add(origin,ModelVerification.Scale(direction,ModelVerification.Dot(ModelVerification.Sub(position,origin),direction)));
            }
            var mid=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"cylinder normal");
            var point=new Vector3(mid[0]*1000,mid[1]*1000,mid[2]*1000);
            var radial=ModelVerification.Sub(point,AxisPoint(mid));
            var normal=new Vector3(mid[3],mid[4],mid[5]);
            if(!ModelVerification.Finite(normal)||ModelVerification.Norm(normal)<1e-12||ModelVerification.Norm(radial)<1e-12)
                throw new InvalidOperationException("Cylinder normal or radial direction is invalid.");
            if(face.FaceInSurfaceSense()) normal=ModelVerification.Scale(normal,-1);
            var interior=ModelVerification.Dot(normal,radial)<0;
            result.Add(new(AxisPoint(p0),AxisPoint(p1),direction,data[6]*1000,face.GetArea()*1_000_000,interior,(face.GetFeature() as IFeature)?.Name));
        }
        return result;
    }

    private static ModelVerificationResult VerifySourceRequirements(IModelDoc2 model,ModelVerificationSpec spec,
        GeometrySnapshot? geometry,out IReadOnlyList<MeasuredCylinder> cylinders)
    {
        cylinders=[];
        var measurementErrors=new List<VerificationCheckResult>();
        try { if(spec.CylinderGroups.Count>0) cylinders=MeasureCylinders(model); }
        catch(Exception ex)
        { measurementErrors.AddRange(spec.CylinderGroups.Select(c=>new VerificationCheckResult(c.Id,false,"unverifiable","Actual cylindrical geometry could not be measured: "+ex.Message))); }
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
        var evaluated=ModelVerification.Evaluate(measuredSpec,cylinders,dimensions,geometry,localMeasurements);
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
            if(model is not IPartDoc part)throw new InvalidOperationException("Local geometry inspection requires a solid part.");
            var bodies=(part.GetBodies2(0,false) as object[]??[]).Cast<IBody2>().ToArray();
            var faces=bodies.SelectMany(b=>(b.GetFaces() as object[]??[]).Cast<IFace2>()).ToArray();
            if(bodies.Length==0||faces.Length==0||faces.Length!=bodies.Sum(b=>b.GetFaceCount()))throw new InvalidOperationException("Solid boundary inventory is incomplete.");
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
                            var cp=ToDoubles(face.GetClosestPointOn(point.X/1000,point.Y/1000,point.Z/1000),5,"trimmed face closest point");
                            var closest=new Vector3(cp[0]*1000,cp[1]*1000,cp[2]*1000);
                            if(!ModelVerification.Finite(closest))throw new InvalidOperationException("Nonfinite closest point.");
                            LocalSurfaceKind? kind=null;double? diameter=null,angle=null,area=null;double areaError=0;var normal=new Vector3(0,0,0);
                            if(request.Surface&&ModelVerification.Norm(ModelVerification.Sub(closest,point))<=request.ToleranceMm)
                            {
                                var surface=(ISurface)face.GetSurface();
                                if(surface.IsPlane())kind=LocalSurfaceKind.Plane;
                                else if(surface.IsCylinder()) {kind=LocalSurfaceKind.Cylinder;diameter=ToDoubles(surface.CylinderParams,7,"cylinder parameters")[6]*2000;}
                                else if(surface.IsCone()) {kind=LocalSurfaceKind.Cone;angle=Math.Abs(ToDoubles(surface.ConeParams2,11,"cone parameters")[7])*180/Math.PI;}
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
                                    var evaluated=ToDoubles(surface.Evaluate(cp[3],cp[4],0,0),6,"local surface normal");
                                    normal=new(evaluated[3],evaluated[4],evaluated[5]);
                                    if(face.FaceInSurfaceSense())normal=ModelVerification.Scale(normal,-1);
                                }
                            }
                            measured.Add(new(closest,normal,kind,diameter,angle,faceIndex,area,areaError));
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
