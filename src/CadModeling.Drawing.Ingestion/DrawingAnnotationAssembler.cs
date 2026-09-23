using System.Text.RegularExpressions;
using CadModeling.Drawing.Contracts;
using CadModeling.Ir;

namespace CadModeling.Drawing.Ingestion;

/// <summary>Conservative spatial assembly of split callouts. Originals are retained, and attachment remains unproven.</summary>
public static partial class DrawingAnnotationAssembler
{
    public static IReadOnlyList<DrawingOmissionCandidate> Assemble(IReadOnlyList<DrawingOmissionCandidate> candidates)
    {
        var text=candidates.Where(c=>c.Kind==DrawingCandidateKind.Annotation&&!string.IsNullOrWhiteSpace(c.Literal)).ToArray();
        var result=new List<DrawingOmissionCandidate>();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var first in text)
        {
            foreach(var vertical in new[]{false,true})
            {
                var parts=new List<DrawingOmissionCandidate>{first};
                var current=first;
                for(var step=0;step<2;step++)
                {
                    var next=text.Where(c=>c.PageNumber==current.PageNumber&&!parts.Contains(c)&&Adjacent(current.Bounds,c.Bounds,vertical))
                        .OrderBy(c=>Gap(current.Bounds,c.Bounds,vertical)).Take(2).ToArray();
                    if(next.Length==0)break;
                    if(next.Length==2&&Math.Abs(Gap(current.Bounds,next[0].Bounds,vertical)-Gap(current.Bounds,next[1].Bounds,vertical))<.001&&next[0].Literal!=next[1].Literal)break;
                    parts.Add(next[0]);current=next[0];
                    var literal=string.Concat(parts.Select(p=>p.Literal.Trim()));
                    if(!Compound().IsMatch(literal))continue;
                    var ids=parts.SelectMany(p=>p.ObservationIds).Distinct().Order().ToArray();
                    var key=string.Join("|",ids);if(!seen.Add(key))continue;
                    var parsed=EngineeringDimensionParser.Extract([new(){ObservationId="assembled",Kind=ObservationKind.Text,RawLiteral=literal,Confidence=parts.Any(p=>p.Conflict) ? .3 : .7}]).SingleOrDefault();
                    if(parsed is null)continue;
                    result.Add(new(){Id=IngestionUtilities.StableId("compound",key),PageNumber=first.PageNumber,Kind=DrawingCandidateKind.Annotation,
                        Bounds=new(parts.Min(p=>p.Bounds.Left),parts.Min(p=>p.Bounds.Top),parts.Max(p=>p.Bounds.Right),parts.Max(p=>p.Bounds.Bottom)),Literal=literal,
                        ObservationIds=ids,Provider="spatial-annotation-assembler",GeometryHint="assembled_callout_candidate_not_attachment",
                        Conflict=parts.Any(p=>p.Conflict),DimensionKind=parsed.DimensionKind,NumericValue=parsed.CandidateNumericValue,Multiplicity=parsed.Count,SecondaryValue=parsed.SecondaryValue,
                        RelatedCandidateIds=parts.Select(p=>p.Id).ToArray()});
                }
            }
        }
        return result;
    }

    private static bool Adjacent(DrawingReviewBox a,DrawingReviewBox b,bool vertical)
    {
        var crossSize=vertical?Math.Max(a.Right-a.Left,b.Right-b.Left):Math.Max(a.Bottom-a.Top,b.Bottom-b.Top);
        var crossDistance=vertical?Math.Abs((a.Left+a.Right-b.Left-b.Right)/2):Math.Abs((a.Top+a.Bottom-b.Top-b.Bottom)/2);
        var gap=Gap(a,b,vertical);
        return crossDistance<=crossSize*.65&&gap>=-.0005&&gap<=Math.Max(.004,crossSize*1.2);
    }
    private static double Gap(DrawingReviewBox a,DrawingReviewBox b,bool vertical)=>vertical?b.Top-a.Bottom:b.Left-a.Right;
    // Do not concatenate bare neighbouring numbers. A visible symbol/operator is mandatory.
    [GeneratedRegex(@"^(?:\d+\s*[-x×]\s*[Ø⌀φΦ∅RM]\s*\d+(?:\.\d+)?|[Ø⌀φΦ∅RM]\s*\d+(?:\.\d+)?(?:\s*[x×]\s*\d+(?:\.\d+)?)?|\d+(?:\.\d+)?\s*[x×]\s*\d+(?:\.\d+)?\s*[°º]|\d+(?:\.\d+)?\s*[°º])$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)]
    private static partial Regex Compound();
}
