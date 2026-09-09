using System.Globalization;
using System.Text.RegularExpressions;
using CadModeling.Drawing.Contracts;

namespace CadModeling.Drawing.Ingestion;

public static partial class EngineeringDimensionParser
{
    // Preserve OCR text and its source location. Proximity supplies candidates, not an asserted attachment.
    public static IReadOnlyList<DimensionObservation> Extract(IReadOnlyList<ObservationEntity> observations)
    {
        var result=new List<DimensionObservation>();
        foreach(var text in observations.Where(o=>o.Kind==ObservationKind.Text))
        {
            var literal=text.RawLiteral.Trim().Replace('，','.').Replace('×','x').Replace('＊','x').Replace('X','x');
            var match=Dimension().Match(literal);
            if(!match.Success) continue;
            var symbol=match.Groups["symbol"].Value.ToUpperInvariant();
            var hasAngle=match.Groups["angle"].Success;
            var value=double.Parse(match.Groups["value"].Value,CultureInfo.InvariantCulture);
            var secondary=match.Groups["secondary"].Success ? double.Parse(match.Groups["secondary"].Value,CultureInfo.InvariantCulture) : (double?)null;
            var kind=symbol switch { "R"=>"radius","M"=>"thread","Ø" or "⌀" or "Φ" or "∅"=>"diameter", _=>hasAngle?(secondary.HasValue?"chamfer":"angle"):"linear" };
            var count=match.Groups["count"].Success?int.Parse(match.Groups["count"].Value,CultureInfo.InvariantCulture):1;
            var nearby=Array.Empty<string>();
            if(text.Geometry.Count>0)
            {
                var x=text.Geometry.Average(p=>p.X);var y=text.Geometry.Average(p=>p.Y);
                var scale=Math.Max(12,Math.Max(text.Geometry.Max(p=>p.X)-text.Geometry.Min(p=>p.X),text.Geometry.Max(p=>p.Y)-text.Geometry.Min(p=>p.Y)));
                nearby=observations.Where(o=>o.Kind!=ObservationKind.Text && o.Geometry.Count>0 &&
                        o.Geometry[0].CoordinateFrameId==text.Geometry[0].CoordinateFrameId)
                    .Select(o=>new { o.ObservationId,Distance=o.Geometry.Min(p=>Math.Sqrt((p.X-x)*(p.X-x)+(p.Y-y)*(p.Y-y))) })
                    .Where(o=>o.Distance<=scale*4).OrderBy(o=>o.Distance).Take(4).Select(o=>o.ObservationId).ToArray();
            }
            result.Add(new() { DimensionObservationId="dimension-"+text.ObservationId,SourceObservationId=text.ObservationId,
                SourceRegionId=text.SourceRegionId,RawLiteral=text.RawLiteral,CandidateNumericValue=value,CandidateSymbol=symbol,
                DimensionKind=kind,Count=count,SecondaryValue=secondary,NearbyGeometryObservationIds=nearby,
                Confidence=Math.Min(text.Confidence,0.85),EvidenceStatus=text.EvidenceStatus==EvidenceStatus.Conflict?EvidenceStatus.Conflict:EvidenceStatus.Candidate });
        }
        return result;
    }

    [GeneratedRegex(@"^(?:(?<count>\d+)\s*[-x]\s*(?=[Ø⌀φΦ∅RM]))?(?<symbol>[Ø⌀φΦ∅RM])?\s*(?<value>\d+(?:\.\d+)?)(?:\s*x\s*(?<secondary>\d+(?:\.\d+)?))?\s*(?<angle>[°º])?(?:mm|倒圆角|圆角|倒角)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)]
    private static partial Regex Dimension();
}
