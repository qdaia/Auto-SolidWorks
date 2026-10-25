using System.Security.Cryptography;
using System.Text.Json;
using CadModeling.Ir;
namespace CadModeling.Core;

public static class SourceMaterialCoverage
{
    public const string Revision = "source-material-intervals-1";
    public sealed record Generation(IReadOnlyList<SourceMaterialRegionCheck> Checks,IReadOnlyList<ModelingDiagnostic> Diagnostics);
    public static Generation Generate(DrawingPlanContext context)
    {
        var checks=new List<SourceMaterialRegionCheck>();var errors=new List<ModelingDiagnostic>();
        void Error(string code,string path,string message)=>errors.Add(new(code,DiagnosticSeverity.Error,message,"drawing_context.shape_decomposition."+path));
        if(context.ShapeDecomposition?.Features is not { } features)return new(checks,errors);
        var digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{context.ShapeDecomposition,context.Dimensions},ModelingIrJson.Options)));
        foreach(var feature in features)
        {
            if(feature?.Regions is null || feature.AxialMaps is null)continue; // Original structural validator reports malformed input.
            foreach(var region in feature.Regions)
            {
                if(region is null)continue;
                var segments=new List<SourceMaterialSegment>(region.ProbeSegments??[]);
                foreach(var map in feature.AxialMaps)
                {
                    if(map?.Layers is null || !ModelVerification.Finite(map.OriginMm) || !ModelVerification.Finite(map.AxisDirection) || !ModelVerification.Finite(map.ZeroAngleDirection) ||
                        Math.Abs(ModelVerification.Norm(map.AxisDirection)-1)>1e-7 || Math.Abs(ModelVerification.Norm(map.ZeroAngleDirection)-1)>1e-7 ||
                        Math.Abs(ModelVerification.Dot(map.AxisDirection,map.ZeroAngleDirection))>1e-7 || !double.IsFinite(map.InnerRadiusMm) || !double.IsFinite(map.OuterRadiusMm) || map.InnerRadiusMm<0 || map.OuterRadiusMm-map.InnerRadiusMm<=.004)continue;
                    var radialY=Cross(map.AxisDirection,map.ZeroAngleDirection);
                    foreach(var layer in map.Layers)
                    {
                        if(layer?.Sectors is null || !double.IsFinite(layer.MinimumMm) || !double.IsFinite(layer.MaximumMm) || layer.MaximumMm-layer.MinimumMm<=.004)continue;
                        foreach(var sector in layer.Sectors.Where(s=>s?.RegionId==region.Id))
                        {
                            if(!double.IsFinite(sector.StartDegrees)||!double.IsFinite(sector.EndDegrees)||sector.EndDegrees<=sector.StartDegrees)continue;
                            // All source layers and sectors receive three axial and three angular probes.
                            for(var ai=0;ai<3;ai++)for(var ti=0;ti<3;ti++)
                            {
                                var h=layer.MinimumMm+(layer.MaximumMm-layer.MinimumMm)*(ai+1)/4;
                                var angle=(sector.StartDegrees+(sector.EndDegrees-sector.StartDegrees)*(ti+1)/4)*Math.PI/180;
                                var direction=ModelVerification.Add(ModelVerification.Scale(map.ZeroAngleDirection,Math.Cos(angle)),ModelVerification.Scale(radialY,Math.Sin(angle)));
                                var origin=ModelVerification.Add(map.OriginMm,ModelVerification.Scale(map.AxisDirection,h));
                                segments.Add(new(){Id=JsonSerializer.Serialize(new[]{map.Id,layer.Id,sector.StartDegrees.ToString("R",System.Globalization.CultureInfo.InvariantCulture),ai.ToString(),ti.ToString()}),
                                    StartMm=ModelVerification.Add(origin,ModelVerification.Scale(direction,map.InnerRadiusMm+.002)),
                                    EndMm=ModelVerification.Add(origin,ModelVerification.Scale(direction,map.OuterRadiusMm-.002)),
                                    SourceExplanation=$"原图分区 {map.Id}/{layer.Id}/{region.Id}；径向完整内部区间，轴向和角向各三点；不认证未采样位置。"});
                            }
                        }
                    }
                }
                if(segments.Count<3)
                    Error("SOURCE_MATERIAL_PROBES_REQUIRED",feature.FeatureId+"."+region.Id,"每个材料/空缺区需要至少3条源独立内部检查线段；轴向分区自动生成，其余区域明确填写 probe_segments。");
                if(segments.Count>4096)
                    Error("SOURCE_MATERIAL_PROBE_LIMIT",feature.FeatureId+"."+region.Id,"每区检查线段不能超过4096条；分解应保持有界。");
                var identity=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]{feature.FeatureId,region.Id})))[..24];
                checks.Add(new(){Id="source-material-"+identity,FeatureId=feature.FeatureId,RegionId=region.Id,SourceLiteral=region.Description,
                    SourceDecompositionDigest=digest,ExpectedState=region.MaterialState,Segments=segments});
            }
        }
        if(checks.Sum(c=>c.Segments.Count)>16384)Error("SOURCE_MATERIAL_PROBE_LIMIT","","整个计划检查线段不能超过16384条。");
        return new(checks,errors);
    }

    public static IReadOnlyList<ModelingDiagnostic> Validate(ModelVerificationSpec spec,DrawingPlanContext? context)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string code,string id,string message)=>errors.Add(new(code,DiagnosticSeverity.Error,message,"verification.material_regions."+id));
        if(spec.MaterialRegions is null)return [new("SOURCE_MATERIAL_SPEC",DiagnosticSeverity.Error,"material_regions 不能为 null。","verification.material_regions")];
        if(spec.MaterialRegions.Count>4096 || spec.MaterialRegions.Sum(c=>c?.Segments?.Count??0)>16384)
            Error("SOURCE_MATERIAL_PROBE_LIMIT","","材料检查数量超限。");
        foreach(var check in spec.MaterialRegions)
        {
            if(check is null){Error("SOURCE_MATERIAL_SPEC","","材料检查不能为 null。");continue;}
            if(string.IsNullOrWhiteSpace(check.Id)||string.IsNullOrWhiteSpace(check.FeatureId)||string.IsNullOrWhiteSpace(check.RegionId)||string.IsNullOrWhiteSpace(check.SourceLiteral)||!Enum.IsDefined(check.ExpectedState) ||
                !double.IsFinite(check.ToleranceMm)||check.ToleranceMm<=0||check.ToleranceMm>.01||check.Segments is not {Count:>=3 and <=4096})
            {Error("SOURCE_MATERIAL_SPEC",check.Id,"材料检查须有明确来源/区域、至少3条线段及(0,0.01]mm数值公差。");continue;}
            if(check.Segments.Any(s=>s is null)){Error("SOURCE_MATERIAL_SPEC",check.Id,"检查线段不能为 null。");continue;}
            if(check.Segments.Select(s=>s.Id).Distinct().Count()!=check.Segments.Count)
                Error("SOURCE_MATERIAL_SPEC",check.Id,"材料检查线段ID不得重复。");
            string PointKey(Vector3 p)=>JsonSerializer.Serialize(p,ModelingIrJson.Options);
            if(check.Segments.Where(s=>ModelVerification.Finite(s.StartMm)&&ModelVerification.Finite(s.EndMm)).Select(s=>string.CompareOrdinal(PointKey(s.StartMm),PointKey(s.EndMm))<=0?PointKey(s.StartMm)+PointKey(s.EndMm):PointKey(s.EndMm)+PointKey(s.StartMm)).Distinct().Count()!=check.Segments.Count)
                Error("SOURCE_MATERIAL_SPEC",check.Id,"检查线段必须具有不同的实际位置，不能重复同一条线伪造覆盖。");
            foreach(var segment in check.Segments)
                if(string.IsNullOrWhiteSpace(segment.Id)||string.IsNullOrWhiteSpace(segment.SourceExplanation)||!ModelVerification.Finite(segment.StartMm)||!ModelVerification.Finite(segment.EndMm)||
                    ModelVerification.Norm(ModelVerification.Sub(segment.EndMm,segment.StartMm))<=10*check.ToleranceMm)
                    Error("SOURCE_MATERIAL_SPEC",check.Id,"源检查线段须有限、长度超过10倍数值公差并有来源解释。");
        }
        if(context is null || !SourceWorkflow.RequiresSourceReview(context.WorkflowRevision))return errors;
        var generated=Generate(context);errors.AddRange(generated.Diagnostics);
        foreach(var expected in generated.Checks)
        {
            var actual=spec.MaterialRegions.Where(c=>c?.Id==expected.Id).ToArray();
            if(actual.Length!=1 || JsonSerializer.Serialize(actual[0],ModelingIrJson.Options)!=JsonSerializer.Serialize(expected,ModelingIrJson.Options))
                Error("SOURCE_MATERIAL_COVERAGE",expected.Id,"检查未覆盖当前源分区或已偏离源定义；重新编译，不得删除失败区域或改写期望材料状态。");
        }
        return errors;
    }
    private static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
}
