using CadModeling.Ir;
namespace CadModeling.Core;

public sealed record MaterialRayHit(int BodyIndex, double DistanceMm, bool Enter, bool Exit);
public sealed record MaterialInterval(double StartMm,double EndMm);
public sealed record MaterialIntervalResult(bool Complete,bool Passed,string Message,IReadOnlyList<MaterialInterval> Intervals,double MismatchLengthMm);

/// <summary>Requires an origin outside every body and complete forward-ray crossings.</summary>
public static class MaterialIntervalVerification
{
    public static MaterialIntervalResult Evaluate(IReadOnlyList<MaterialRayHit> hits,int bodyCount,double start,double end,double tolerance,DrawingMaterialState expected)
    {
        MaterialIntervalResult Invalid(string message)=>new(false,false,message,[],0);
        if(bodyCount<=0 || !double.IsFinite(start)||!double.IsFinite(end)||!double.IsFinite(tolerance)||start<0||end<=start||tolerance<=0||!Enum.IsDefined(expected))return Invalid("Invalid ray scope.");
        if(hits.Any(h=>h.BodyIndex<0||h.BodyIndex>=bodyCount||!double.IsFinite(h.DistanceMm)||h.DistanceMm<0||h.Enter==h.Exit))return Invalid("Ambiguous or invalid crossing.");
        var intervals=new List<MaterialInterval>();
        foreach(var group in hits.GroupBy(h=>h.BodyIndex))
        {
            double? entered=null;MaterialRayHit? previous=null;
            foreach(var hit in group.OrderBy(h=>h.DistanceMm))
            {
                if(previous is not null && Math.Abs(previous.DistanceMm-hit.DistanceMm)<=tolerance)
                {
                    if(previous.Enter!=hit.Enter)return Invalid("Unresolved coincident entry/exit.");
                    continue;
                }
                if(hit.Enter)
                {
                    if(entered is not null)return Invalid("Repeated entry without exit.");
                    entered=hit.DistanceMm;
                }
                else
                {
                    if(entered is null)return Invalid("Exit without entry; origin or hit inventory invalid.");
                    intervals.Add(new(entered.Value,hit.DistanceMm));entered=null;
                }
                previous=hit;
            }
            if(entered is not null)return Invalid("Missing final exit.");
        }
        var merged=new List<MaterialInterval>();
        foreach(var interval in intervals.OrderBy(i=>i.StartMm))
        {
            if(merged.Count>0&&interval.StartMm<=merged[^1].EndMm)
                merged[^1]=merged[^1] with{EndMm=Math.Max(merged[^1].EndMm,interval.EndMm)};
            else merged.Add(interval);
        }
        var covered=merged.Sum(i=>Math.Max(0,Math.Min(end,i.EndMm)-Math.Max(start,i.StartMm)));
        var mismatch=expected==DrawingMaterialState.Material?Math.Max(0,end-start-covered):covered;
        return new(true,mismatch<=tolerance,mismatch<=tolerance?"Source interval satisfied.":"Source material/void interval differs from native solid.",merged,mismatch);
    }
}
