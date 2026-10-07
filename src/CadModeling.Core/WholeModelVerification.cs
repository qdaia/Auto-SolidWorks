using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record WholeModelMeasurement(bool Complete,int SolidBodyCount,int SurfaceBodyCount,int FaceCount,
    int EdgeCount,int OpenEdgeCount,IReadOnlyDictionary<LocalSurfaceKind,int> SurfaceFaceCounts,
    double SurfaceAreaMm2,double VolumeMm3,string? Error=null,int BodyFaultCount=0);

public static partial class ModelVerification
{
    private static void ValidateWholeModelChecks(ModelVerificationSpec spec,Action<string> error)
    {
        if(spec.RequireWholeModelInventory&&spec.WholeModelChecks.Count==0)error("模型库存是必需的；有限样本无法满足这一要求。");
        foreach(var c in spec.WholeModelChecks)
            if(c.SolidBodyCount<0||c.SurfaceBodyCount<0||c.SolidBodyCount+c.SurfaceBodyCount==0||c.FaceCount<1||c.EdgeCount<0||c.OpenEdgeCount<0||c.OpenEdgeCount>c.EdgeCount||
               c.SurfaceFaceCounts.Any(p=>!Enum.IsDefined(p.Key)||p.Value<0)||c.SurfaceFaceCounts.Values.Sum() != c.FaceCount||
               c.SurfaceAreaMm2 is {} a&&!Positive(a)||c.VolumeMm3 is {} v&&(!double.IsFinite(v)||v<0)||!Positive(c.AreaToleranceMm2)||!Positive(c.VolumeToleranceMm3))
                error($"全模型检查 '{c.Id}' 需要完整的非负计数，全曲面类型的面分区，有限值和正公差。");
    }
    private static IEnumerable<VerificationCheckResult> EvaluateWholeModelChecks(ModelVerificationSpec spec,WholeModelMeasurement? m)
    {
        foreach(var c in spec.WholeModelChecks)
        {
            if(m is null||!m.Complete||m.FaceCount<1||m.SurfaceFaceCounts.Values.Sum()!=m.FaceCount||!double.IsFinite(m.SurfaceAreaMm2)||!double.IsFinite(m.VolumeMm3))
            {yield return new(c.Id,false,"unverifiable",m?.Error??"模型库存缺失或不完整。");continue;}
            var passed=m.BodyFaultCount==0&&m.SolidBodyCount==c.SolidBodyCount&&m.SurfaceBodyCount==c.SurfaceBodyCount&&m.FaceCount==c.FaceCount&&m.EdgeCount==c.EdgeCount&&m.OpenEdgeCount==c.OpenEdgeCount&&
                Enum.GetValues<LocalSurfaceKind>().All(k=>m.SurfaceFaceCounts.GetValueOrDefault(k)==c.SurfaceFaceCounts.GetValueOrDefault(k))&&
                (c.SurfaceAreaMm2 is null||Math.Abs(m.SurfaceAreaMm2-c.SurfaceAreaMm2.Value)<=c.AreaToleranceMm2)&&
                (c.VolumeMm3 is null||Math.Abs(m.VolumeMm3-c.VolumeMm3.Value)<=c.VolumeToleranceMm3);
            yield return new(c.Id,passed,passed?"passed":"mismatch",passed?"完整可见和不可见的体/面/线库存符合源要求。":"模型整体结构、曲面划分、面积或体积与独立源要求不同。",new Dictionary<string,string>{
                ["scope"]="whole_model_inventory",["bodies"]=$"{m.SolidBodyCount} 个实体，{m.SurfaceBodyCount} 个曲面体",["faces"]=m.FaceCount.ToString(),["edges"]=m.EdgeCount.ToString(),["open_edges"]=m.OpenEdgeCount.ToString(),
                ["surface_kinds"]=string.Join("; ",m.SurfaceFaceCounts.Select(p=>$"{p.Key}: {p.Value}")),["area_mm2"]=F(m.SurfaceAreaMm2),["volume_mm3"]=F(m.VolumeMm3),
                ["native_body_faults"]=m.BodyFaultCount.ToString(),
                ["limits"]="结构库存和可选的全局度量；不能证明位置等价性、G1/G2连续性或完整的绘图语义。"});
        }
    }
}
