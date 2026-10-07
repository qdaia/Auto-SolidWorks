using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyDictionary<string,ShapeMeasurement> MeasureShapeRequirements(IModelDoc2 model, ModelVerificationSpec spec)
    {
        var result=new Dictionary<string,ShapeMeasurement>(StringComparer.Ordinal);
        var objects=new Dictionary<string,object>();
        foreach(var c in spec.EdgeShapes)
        {
            try
            {
                var edge=(IEdge)ResolveEntities(model,objects,c.Edge).Single();
                if(edge.GetStartVertex() is not IVertex start || edge.GetEndVertex() is not IVertex end) throw new InvalidOperationException("端点形状检查需要一个开放的原生边线。");
                result[c.Id]=new(true,EdgeLengthMm(edge),VertexPoint(start),VertexPoint(end));
            }
            catch(Exception ex){result[c.Id]=new(false,Error:ex.Message);}
        }
        foreach(var c in spec.SurfaceContinuity)
        {
            try
            {
                var edge=(IEdge)ResolveEntities(model,objects,c.Edge).Single();
                var faces=(edge.GetTwoAdjacentFaces2() as object[]??[]).OfType<IFace2>().ToArray();
                if(faces.Length!=2) throw new InvalidOperationException("连续性检查需要恰好两个相邻的原生面。");
                var range=edge.GetCurveParams3();var curve=(ICurve)edge.GetCurve();double maxGap=0,maxAngle=0,maxCurvature=0;
                for(var i=0;i<c.Samples;i++)
                {
                    var t=range.UMinValue+(range.UMaxValue-range.UMinValue)*i/(c.Samples-1);
                    var p=ToDoubles(curve.Evaluate2(t,c.RequireCurvatureContinuity?1:0),c.RequireCurvatureContinuity?6:3,"缝合曲线评估");
                    if(p.Any(x=>!double.IsFinite(x)))throw new InvalidOperationException("接缝曲线评估包含非有限值。");
                    var normals=new List<Vector3>();var derivatives=new List<SurfaceDerivatives>();var points=new List<Vector3>();
                    foreach(var face in faces)
                    {
                        var cp=ToDoubles(face.GetClosestPointOn(p[0],p[1],p[2]),5,"裁剪缝合点");
                        if(cp.Any(x=>!double.IsFinite(x)))throw new InvalidOperationException("裁剪面评估包含非有限值。");
                        points.Add(new(cp[0],cp[1],cp[2]));
                        maxGap=Math.Max(maxGap,Math.Sqrt(Math.Pow(cp[0]-p[0],2)+Math.Pow(cp[1]-p[1],2)+Math.Pow(cp[2]-p[2],2))*1000);
                        var normal=ToDoubles(((ISurface)face.GetSurface()).Evaluate(cp[3],cp[4],0,0),6,"缝合正向");
                        normals.Add(Unit(new(normal[3],normal[4],normal[5])));
                        if(c.RequireCurvatureContinuity)derivatives.Add(SurfaceDifferentialGeometry.FromNativeEvaluation(
                            ToDoubles(((ISurface)face.GetSurface()).Evaluate(cp[3],cp[4],2,2),30,"原生二阶曲面导数")));
                    }
                    var dot=normals[0].X*normals[1].X+normals[0].Y*normals[1].Y+normals[0].Z*normals[1].Z;
                    maxAngle=Math.Max(maxAngle,Math.Acos(Math.Clamp(Math.Abs(dot),0,1))*180/Math.PI);
                    maxGap=Math.Max(maxGap,PointDistance(points[0],points[1])*1000);
                    if(c.RequireCurvatureContinuity)maxCurvature=Math.Max(maxCurvature,
                        SurfaceDifferentialGeometry.MaximumNormalCurvatureDifferencePerMm(derivatives[0],derivatives[1],new(p[3],p[4],p[5])));
                }
                result[c.Id]=new(true,MaximumGapMm:maxGap,MaximumAngleDegrees:maxAngle,Samples:c.Samples,
                    MaximumNormalCurvatureDifferencePerMm:c.RequireCurvatureContinuity?maxCurvature:null,CurvatureSamples:c.RequireCurvatureContinuity?c.Samples:0);
            }
            catch(Exception ex){result[c.Id]=new(false,Error:ex.Message);}
        }
        return result;
    }
}
