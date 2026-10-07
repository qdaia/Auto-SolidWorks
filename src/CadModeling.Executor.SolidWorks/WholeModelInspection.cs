using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static LocalSurfaceKind WholeSurfaceKind(ISurface surface)=>(swSurfaceTypes_e)surface.Identity() switch
    {
        swSurfaceTypes_e.PLANE_TYPE=>LocalSurfaceKind.Plane,swSurfaceTypes_e.CYLINDER_TYPE=>LocalSurfaceKind.Cylinder,
        swSurfaceTypes_e.CONE_TYPE=>LocalSurfaceKind.Cone,swSurfaceTypes_e.SPHERE_TYPE=>LocalSurfaceKind.Sphere,
        swSurfaceTypes_e.TORUS_TYPE=>LocalSurfaceKind.Torus,swSurfaceTypes_e.BSURF_TYPE=>LocalSurfaceKind.BSpline,_=>LocalSurfaceKind.Other
    };
    private static WholeModelMeasurement MeasureWholeModel(IModelDoc2 model,GeometrySnapshot? geometry)
    {
        try
        {
            if(model is not IPartDoc part||geometry is null)throw new InvalidOperationException("模型级库存需要重建零件几何。");
            var solids=(part.GetBodies2((int)swBodyType_e.swSolidBody,false) as object[]??[]).Cast<IBody2>().ToArray();
            var sheets=(part.GetBodies2((int)swBodyType_e.swSheetBody,false) as object[]??[]).Cast<IBody2>().ToArray();
            var bodies=solids.Concat(sheets).ToArray();
            var partition=new Dictionary<LocalSurfaceKind,int>();int faceCount=0,edgeCount=0,openEdges=0,faultCount=0;double area=0;
            foreach(var body in bodies)
            {
                var faults=body.Check3??throw new InvalidOperationException("原生体故障检查返回无结果。");
                if(faults.Count<0)throw new InvalidOperationException("原生体故障计数无效。");
                faultCount=checked(faultCount+faults.Count);
                var faces=(body.GetFaces() as object[]??[]).Cast<IFace2>().ToArray();
                var edges=(body.GetEdges() as object[]??[]).Cast<IEdge>().ToArray();
                if(faces.Length!=body.GetFaceCount()||edges.Length!=body.GetEdgeCount())throw new InvalidOperationException("原生体特征/边完整库存不全。");
                foreach(var face in faces)
                {
                    var kind=WholeSurfaceKind((ISurface)face.GetSurface());partition[kind]=partition.GetValueOrDefault(kind)+1;
                    var a=face.GetArea()*1_000_000;if(!double.IsFinite(a)||a<=0)throw new InvalidOperationException("原生面的面积无效。");area+=a;
                }
                foreach(var edge in edges)
                {
                    if(edge.GetTwoAdjacentFaces2() is not object[] adjacent||adjacent.Length!=2)throw new InvalidOperationException("原生边线相邻缺失。");
                    if(adjacent.Any(f=>f is null))openEdges++;
                }
                faceCount+=faces.Length;edgeCount+=edges.Length;
            }
            if(bodies.Length==0||faceCount==0||solids.Length!=geometry.SolidBodyCount||sheets.Length!=geometry.SurfaceBodyCount||faceCount!=geometry.FaceCount||edgeCount!=geometry.EdgeCount)
                throw new InvalidOperationException("模型库存与测量几何体不符。");
            return new(true,solids.Length,sheets.Length,faceCount,edgeCount,openEdges,partition,area,geometry.VolumeMm3,BodyFaultCount:faultCount);
        }
        catch(Exception ex){return new(false,0,0,0,0,0,new Dictionary<LocalSurfaceKind,int>(),0,0,ex.Message);}
    }
}
