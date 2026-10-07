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
