using System.Globalization;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    private sealed class EntitySelectionException : InvalidOperationException
    {
        public IReadOnlyDictionary<string,string> Diagnostics {get;}
        public EntitySelectionException(string message,IReadOnlyDictionary<string,string> diagnostics):base(message){Diagnostics=diagnostics;}
    }
    private static Exception EntitySelectionFailure(EntityQuery query,object[] candidates,int matches,string scope)
    {
        var data=new Dictionary<string,string>{["entity_kind"]=query.Kind.ToString(),["geometry_kind"]=query.Geometry.ToString(),
            ["selection_scope"]=scope,["candidate_count"]=candidates.Length.ToString(CultureInfo.InvariantCulture),
            ["match_count"]=matches.ToString(CultureInfo.InvariantCulture),["feature_id"]=query.FeatureId??"<none>",
            ["tolerance_mm"]=query.ToleranceMm.ToString("G12",CultureInfo.InvariantCulture)};
        data["query_json"]=System.Text.Json.JsonSerializer.Serialize(query,ModelingIrJson.Options);
        data["diagnostic_scope"]="仅声明的选择范围；候选诊断不得自动拓宽范围或选最近项";
        data["candidate_diagnostics_truncated"]=(candidates.Length>1000).ToString();
        var filters=new List<(string Name,EntityQuery Filter)>{("entity_kind",new(){Kind=query.Kind})};
        if(query.Geometry!=GeometryKind.Any)filters.Add(("geometry",new(){Kind=query.Kind,Geometry=query.Geometry}));
        if(query.PositionMm is not null)filters.Add(("position_mm",new(){Kind=query.Kind,PositionMm=query.PositionMm,ToleranceMm=query.ToleranceMm}));
        if(query.Direction is not null)filters.Add(("direction",new(){Kind=query.Kind,Direction=query.Direction}));
        if(query.RadiusMm is not null)filters.Add(("radius_mm",new(){Kind=query.Kind,RadiusMm=query.RadiusMm,ToleranceMm=query.ToleranceMm}));
        if(query.LengthMm is not null)filters.Add(("length_mm",new(){Kind=query.Kind,LengthMm=query.LengthMm,ToleranceMm=query.ToleranceMm}));
        if(query.AreaMm2 is not null)filters.Add(("area_mm2",new(){Kind=query.Kind,AreaMm2=query.AreaMm2,AreaToleranceMm2=query.AreaToleranceMm2}));
        if(query.AdjacentFaceCount is not null)filters.Add(("adjacent_face_count",new(){Kind=query.Kind,AdjacentFaceCount=query.AdjacentFaceCount}));
        if(query.StartPointMm is not null)filters.Add(("start_point_mm",new(){Kind=query.Kind,StartPointMm=query.StartPointMm,ToleranceMm=query.ToleranceMm}));
        if(query.EndPointMm is not null)filters.Add(("end_point_mm",new(){Kind=query.Kind,EndPointMm=query.EndPointMm,ToleranceMm=query.ToleranceMm}));
        var rejectedFilters=candidates.Take(25).Select((entity,index)=>
        {
            var failed=new List<string>();
            foreach(var filter in filters)
            {
                try{if(!MatchesEntity(entity,filter.Filter))failed.Add(filter.Name);}
                catch(Exception ex){failed.Add(filter.Name+":unavailable:"+ex.GetType().Name);}
            }
            return new {candidate_index=index,rejected_filters=failed};
        }).ToArray();
        data["candidate_filter_results_json"]=System.Text.Json.JsonSerializer.Serialize(rejectedFilters,ModelingIrJson.Options);
        data["candidate_filter_results_truncated"]=(candidates.Length>25).ToString();
        if(query.PositionMm is {} p)
        {
            var nearby=new List<(double Distance,string Description)>();
            foreach(var entity in candidates.Take(1000))
            {
                try
                {
                    double[]? cp=entity is IEdge edge?edge.GetClosestPointOn(Mm(p.X),Mm(p.Y),Mm(p.Z)) as double[]:
                        entity is IFace2 face?face.GetClosestPointOn(Mm(p.X),Mm(p.Y),Mm(p.Z)) as double[]:null;
                    if(cp is null||cp.Length<3)continue;
                    var d=Math.Sqrt(Math.Pow(cp[0]*1000-p.X,2)+Math.Pow(cp[1]*1000-p.Y,2)+Math.Pow(cp[2]*1000-p.Z,2));
                    if(double.IsFinite(d))nearby.Add((d,FormattableString.Invariant($"最近点（{cp[0]*1000:G8},{cp[1]*1000:G8},{cp[2]*1000:G8}）毫米；距离 {d:G8} 毫米")));
                }
                catch { /* Diagnostic probing cannot broaden selection or hide the original failure. */ }
            }
            data["nearest_candidates"]=string.Join("; ",nearby.OrderBy(n=>n.Distance).Take(5).Select(n=>n.Description));
        }
        return new EntitySelectionException(matches==0?$"没有符合指定几何条件的{query.Geometry}{query.Kind}（scope={scope}，candidates={candidates.Length}）。":
            $"实体选择模糊 ({matches}匹配); 请细化位置/方向/半径或明确选择所有匹配项。",data);
    }
    private static string ReadInspectionHash(string path)
    {
        // SolidWorks can retain a write-capable handle after SaveAs Copy. Permit that handle,
        // while retaining the before/after content hashes and read-only document ownership checks.
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
    }
}
