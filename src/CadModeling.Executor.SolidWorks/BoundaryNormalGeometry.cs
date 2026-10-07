using CadModeling.Core;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    // Finite source-derived samples, deliberately independent of the API's
    // tangency enum. This is not a general G1/G2 continuity certificate.
    private static void RequireBoundaryNormalGeometry(IModelDoc2 model,IFeature feature,BoundaryDefinitionRequest request)
    {
        if(!BoundaryNeedsNormal(request))return;
        var profiles=RequireBoundaryNormalSources(model,request);
        var faces=(feature.GetFaces() as object[]??[]).OfType<IFace2>().ToArray();
        if(faces.Length!=1||faces[0].GetEdges() is not object[] edges||edges.Length!=4||edges.Any(e=>e is not IEdge))
            throw new InvalidOperationException("BOUNDARY_NORMAL_GEOMETRY_UNAVAILABLE：法向检查需要完整单面四边曲面。");
        var face=faces[0];var surface=(ISurface)face.GetSurface();var app=ExistingBoundaryApplication();
        var math=(IMathUtility)app.GetMathUtility();
        double Distance(double[] a,double[] b)=>Math.Sqrt(Enumerable.Range(0,3).Sum(i=>(a[i]-b[i])*(a[i]-b[i])))*1000;
        for(int index=0;index<2;index++)
        {
            if(BoundarySurfaceContract.ExpectedTangency(request,0,index,2)!=1)continue;
            var sketch=(ISketch)profiles[index].GetSpecificFeature2();
            var segment=((object[])sketch.GetSketchSegments()).OfType<ISketchSegment>().Single(s=>!s.ConstructionGeometry);
            var curve=(ICurve)segment.GetCurve();
            if(!curve.GetEndParams(out var start,out var end,out var closed,out var periodic)||closed||periodic
                ||!double.IsFinite(start)||!double.IsFinite(end)||end<=start)
                throw new InvalidOperationException("BOUNDARY_NORMAL_GEOMETRY_UNAVAILABLE：来源样条参数范围无法认证。");
            var transform=sketch.ModelToSketchTransform.IInverse();
            var normal=ToDoubles(((IMathVector)((IMathVector)math.CreateVector(new double[]{0,0,1})).MultiplyTransform(transform)).ArrayData,3,"轮廓平面法线");
            var magnitude=Math.Sqrt(normal.Take(3).Sum(v=>v*v));
            if(magnitude<=1e-12)throw new InvalidOperationException("BOUNDARY_NORMAL_GEOMETRY_UNAVAILABLE：来源平面法线退化。");
            var points=Enumerable.Range(0,64).Select(i=>
            {
                var p=ToDoubles(curve.Evaluate2(start+(end-start)*(i+.5)/64,0),3,"来源样条有限取样");
                return ToDoubles(((IMathPoint)((IMathPoint)math.CreatePoint(p.Take(3).ToArray())).MultiplyTransform(transform)).ArrayData,3,"来源样条模型坐标");
            }).ToArray();
            var matches=edges.Cast<IEdge>().Where(edge=>points.All(p=>Distance(p,ToDoubles(edge.GetClosestPointOn(p[0],p[1],p[2]),3,"边界真实边最近点"))<=1e-5)).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException("BOUNDARY_NORMAL_SOURCE_MISMATCH：有限来源样条点未匹配唯一真实修剪边。");
            foreach(var p in points)
            {
                var cp=ToDoubles(face.GetClosestPointOn(p[0],p[1],p[2]),5,"边界修剪面最近点");
                var evaluated=ToDoubles(surface.Evaluate(cp[3],cp[4],0,0),6,"边界面有限法线");
                var n=Math.Sqrt(evaluated.Skip(3).Take(3).Sum(v=>v*v));
                var residual=n>1e-12?Math.Abs(Enumerable.Range(0,3).Sum(j=>normal[j]*evaluated[j+3]))/(magnitude*n):double.PositiveInfinity;
                if(Distance(p,cp)>1e-5||!double.IsFinite(residual)||residual>1e-6)
                    throw new InvalidOperationException("BOUNDARY_NORMAL_GEOMETRY_MISMATCH：原生控制值不能替代实际法向几何；64点取样不满足声明条件。");
            }
        }
    }
}
