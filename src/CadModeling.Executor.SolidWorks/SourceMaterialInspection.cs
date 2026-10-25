using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed partial class SolidWorksComExecutor
{
    private static IReadOnlyList<VerificationCheckResult> MeasureSourceMaterial(IModelDoc2 model,ModelVerificationSpec spec)
    {
        if(spec.MaterialRegions.Count==0)return [];
        var results=new List<VerificationCheckResult>();
        try
        {
            var invalid=SourceMaterialCoverage.Validate(spec,null);
            if(invalid.Count>0)throw new InvalidOperationException(string.Join("; ",invalid.Select(d=>d.Message)));
            if(model is not IPartDoc part)throw new InvalidOperationException("源材料检查需要原生实体零件。");
            var bodies=(part.GetBodies2((int)swBodyType_e.swSolidBody,false) as object[]??[]).OfType<IBody2>().ToArray();
            if(bodies.Length==0)throw new InvalidOperationException("没有实体体；不能把空文档认证为空缺区。");
            var corners=new List<Vector3>();
            foreach(var body in bodies)
            {
                var box=ToDoubles(body.GetBodyBox(),6,"材料射线实体包围盒");
                if(box.Length!=6||box.Any(v=>!double.IsFinite(v))||box[3]<=box[0]||box[4]<=box[1]||box[5]<=box[2])throw new InvalidOperationException("实体包围盒无效。");
                for(var x=0;x<2;x++)for(var y=0;y<2;y++)for(var z=0;z<2;z++)corners.Add(new(box[x*3]*1000,box[1+y*3]*1000,box[2+z*3]*1000));
            }
            foreach(var check in spec.MaterialRegions)
            {
                try
                {
                    var bases=new List<Vector3>();var axes=new List<Vector3>();var starts=new List<double>();var ends=new List<double>();
                    foreach(var segment in check.Segments)
                    {
                        var delta=ModelVerification.Sub(segment.EndMm,segment.StartMm);var length=ModelVerification.Norm(delta);var axis=ModelVerification.Unit(delta);
                        var offset=Math.Min(-1,corners.Min(p=>ModelVerification.Dot(ModelVerification.Sub(p,segment.StartMm),axis))-1);
                        bases.Add(ModelVerification.Add(segment.StartMm,ModelVerification.Scale(axis,offset)));axes.Add(axis);starts.Add(-offset);ends.Add(-offset+length);
                    }
                    var options=(int)(swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT|swRayPtsOpts_e.swRayPtsOptsNORMALS);
                    var count=model.Extension.RayIntersections(bodies.Select(b=>new DispatchWrapper(b)).ToArray(),bases.SelectMany(p=>new[]{p.X/1000,p.Y/1000,p.Z/1000}).ToArray(),axes.SelectMany(p=>new[]{p.X,p.Y,p.Z}).ToArray(),options,0,check.ToleranceMm/1000,true);
                    if(count<0)throw new InvalidOperationException("原生交点计数无效。");
                    var values=count==0?[]:ToDoubles(model.GetRayIntersectionsPoints(),count*9,"源材料射线交点");
                    if(values.Length!=count*9||values.Any(v=>!double.IsFinite(v)))throw new InvalidOperationException("原生交点数据不完整。");
                    var hits=Enumerable.Range(0,bases.Count).Select(_=>new List<MaterialRayHit>()).ToArray();
                    for(var i=0;i<count;i++)
                    {
                        var k=i*9;var body=ExactIndex(values[k],bodies.Length,"体索引");var ray=ExactIndex(values[k+1],bases.Count,"射线索引");
                        var type=ExactIndex(values[k+2],64,"相交类型");var point=new Vector3(values[k+3]*1000,values[k+4]*1000,values[k+5]*1000);
                        var delta=ModelVerification.Sub(point,bases[ray]);var distance=ModelVerification.Dot(delta,axes[ray]);
                        if(distance<0||ModelVerification.Norm(ModelVerification.Sub(delta,ModelVerification.Scale(axes[ray],distance)))>check.ToleranceMm)throw new InvalidOperationException("交点偏离请求射线。");
                        var enter=(type&(int)swRayPtsResults_e.swRayPtsResultsENTER)!=0;var exit=(type&(int)swRayPtsResults_e.swRayPtsResultsEXIT)!=0;
                        hits[ray].Add(new(body,distance,enter,exit));
                    }
                    var measured=Enumerable.Range(0,bases.Count).Select(i=>new{segment_id=check.Segments[i].Id,start_mm=check.Segments[i].StartMm,end_mm=check.Segments[i].EndMm,ray_origin_mm=bases[i],target_start=starts[i],target_end=ends[i],hits=hits[i],result=MaterialIntervalVerification.Evaluate(hits[i],bodies.Length,starts[i],ends[i],check.ToleranceMm,check.ExpectedState)}).ToArray();
                    var complete=measured.All(m=>m.result.Complete);var passed=complete&&measured.All(m=>m.result.Passed);
                    results.Add(new(check.Id,passed,passed?"passed":complete?"failed":"unverifiable",$"源区域 {check.FeatureId}/{check.RegionId}：{measured.Count(m=>m.result.Passed)}/{measured.Length} 条内部区间符合 {check.ExpectedState}。仅有限线段覆盖，不是完整体积/BREP等价。",new Dictionary<string,string>{["scope"]="source_material_finite_line_intervals",["method"]="solidworks_all_solid_bodies_ray_entry_exit",["feature_id"]=check.FeatureId,["region_id"]=check.RegionId,["source_digest"]=check.SourceDecompositionDigest??"",["body_count"]=bodies.Length.ToString(),["rays_json"]=JsonSerializer.Serialize(measured,ModelingIrJson.Options)}));
                }
                catch(Exception ex){results.Add(new(check.Id,false,"unverifiable","源材料区间无法完整测量："+ex.Message));}
            }
        }
        catch(Exception ex){return spec.MaterialRegions.Select(c=>new VerificationCheckResult(c.Id,false,"unverifiable","源材料检查失败："+ex.Message)).ToArray();}
        return results;
    }
}
