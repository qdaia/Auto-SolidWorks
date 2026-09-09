using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using System.Security.Cryptography;

var count=0;
void Check(bool passed,string label) {if(!passed)throw new Exception(label);Console.WriteLine("PASS "+label);count++;}
LocatedPoint2 Point(string frame,double x,double y)=>new(){CoordinateFrameId=frame,X=x,Y=y};
var text=new ObservationEntity{ObservationId="text",Kind=ObservationKind.Text,RawLiteral="27",SourceRegionId="source",
    Confidence=.99,EvidenceStatus=EvidenceStatus.Conflict,Geometry=[Point("pdf",0,0),Point("pdf",3,9)]};
var foreign=new ObservationEntity{ObservationId="pixel-line",Kind=ObservationKind.VisibleLine,Geometry=[Point("pixel",1,1)]};
var same=new ObservationEntity{ObservationId="pdf-line",Kind=ObservationKind.VisibleLine,Geometry=[Point("pdf",2,2)]};
var parsed=EngineeringDimensionParser.Extract([text,foreign,same]).Single();
Check(parsed.EvidenceStatus==EvidenceStatus.Conflict,"dimension candidate preserves native/OCR conflict");
Check(parsed.NearbyGeometryObservationIds.SequenceEqual(new[]{"pdf-line"}),"proximity never mixes PDF points and raster pixels");
Check(EngineeringDimensionParser.Extract([text with{EvidenceStatus=EvidenceStatus.Confirmed}]).Single().EvidenceStatus==EvidenceStatus.Candidate,
    "text extraction cannot certify dimension attachment");
Check(EngineeringDimensionParser.Extract([text with{RawLiteral="90°"}]).Single().DimensionKind=="angle","angle unit preserved");
Check(EngineeringDimensionParser.Extract([text with{RawLiteral="R12.50"}]).Single().DimensionKind=="radius","radius prefix preserved");
if(args.Length>0)
{
    var root=Path.GetFullPath(args[0]);var provider=new PdfPigNativeObservationProvider();
    foreach(var n in Enumerable.Range(1,15).Where(n=>n!=6))
    {
        var path=Path.Combine(root,$"练习{n:00}",$"e{n:00}_delivery.pdf");
        var result=await provider.ObservePageAsync(new(){SourcePath=path,SourceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),PageNumber=1,RenderDpi=200});
        var texts=result.Observations.Where(o=>o.Kind==ObservationKind.Text).ToArray();
        Check(texts.All(o=>!string.IsNullOrWhiteSpace(o.RawLiteral)&&o.Geometry.All(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y))),$"exercise {n:00}: nonempty text and finite rotated bounds");
        if(n==1)
        {
            var actual=EngineeringDimensionParser.Extract(texts.Where(t=>t.Geometry.Average(p=>p.Y)>70&&t.Geometry.Average(p=>p.Y)<720).ToArray())
                .Select(d=>d.RawLiteral).Order().ToArray();
            var expected=new[]{"8","27","27","27","14","R10","23","8","6","13","8.50"}.Order().ToArray();
            Check(actual.SequenceEqual(expected),"exercise 01: all 11 drawing labels grouped exactly, including vertical 27 and 13");
            Check(texts.Any(t=>t.RawLiteral=="27"&&Math.Abs(double.Parse(t.CandidateProperties["text_rotation_degrees"],System.Globalization.CultureInfo.InvariantCulture))>45),
                "exercise 01: vertical dimension rotation retained");
        }
        if(n==9)Check(texts.Any(t=>t.RawLiteral=="45°")&&texts.Any(t=>t.RawLiteral=="8°")&&texts.Any(t=>t.RawLiteral=="R12.50"),
            "exercise 09: slanted angles and decimal radius remain whole");
        if(n==14)Check(texts.Any(t=>t.RawLiteral=="65°")&&texts.Any(t=>t.RawLiteral=="15°"),"exercise 14: angled annotations remain whole");
    }
}
Console.WriteLine($"{count} drawing checks passed.");
