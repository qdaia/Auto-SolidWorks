using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    private static (Vector3 CenterMm,double RadiusMm) ReadSphereParameters(ISurface surface)
    {
        var p=ToDoubles(surface.SphereParams,4,"球的参数");
        var center=new Vector3(p[0]*1000,p[1]*1000,p[2]*1000);
        var radius=p[3]*1000;
        if(!ModelVerification.Finite(center)||!ModelVerification.Positive(radius))
            throw new InvalidOperationException("原生球的中心或半径无效。");
        return(center,radius);
    }
    private static IReadOnlyList<MeasuredSphere> MeasureSpheres(IModelDoc2 model)
    {
        var result=new List<MeasuredSphere>();
        foreach(var body in (((IPartDoc)model).GetBodies2(0,false) as object[]??[]).Cast<IBody2>())
        foreach(var face in (body.GetFaces() as object[]??[]).Cast<IFace2>())
        {
            var surface=(ISurface)face.GetSurface();
            if(!surface.IsSphere())continue;
            var p=ReadSphereParameters(surface);
            var uv=ToDoubles(face.GetUVBounds(),4,"球体 UV 坐标范围");
            var sample=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"球的正(norm)向");
            var radial=ModelVerification.Sub(new(sample[0]*1000,sample[1]*1000,sample[2]*1000),p.CenterMm);
            var normal=new Vector3(sample[3],sample[4],sample[5]);
            if(!ModelVerification.Finite(normal)||ModelVerification.Norm(normal)<1e-12)
                throw new InvalidOperationException("无法测量球的法线。");
            if(face.FaceInSurfaceSense())normal=ModelVerification.Scale(normal,-1);
            result.Add(new(p.CenterMm,p.RadiusMm,face.GetArea()*1_000_000,
                ModelVerification.Dot(normal,radial)<0,Persistent(model,face),(face.GetFeature() as IFeature)?.Name));
        }
        return result;
    }
}
