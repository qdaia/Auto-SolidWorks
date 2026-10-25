using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name) {if(!ok)throw new Exception(name); checks.Add(name); Console.WriteLine("PASS "+name);}
void Parse(string raw, params (string Role,double Value)[] values)
{
    var result=HoleCalloutParser.Parse(raw);
    Check(result.Complete,"complete grammar: "+raw);
    Check(result.Components.Where(c=>c.Value is not null).Select(c=>(c.Role,c.Value!.Value)).SequenceEqual(values),"independent expected component roles and values: "+raw);
    Check(result.Components.All(c=>raw.Substring(c.Start,c.Length)==c.Literal),"raw spans preserved: "+raw);
}
Parse("4× ⌀6,6 ↧12 ⌴ ⌀11 ↧6,5",("count",4),("bore_diameter",6.6),("bore_depth",12),("counterbore_diameter",11),("counterbore_depth",6.5));
Parse("2X ⌀5 THRU ⌵ ⌀10×90°",("count",2),("bore_diameter",5),("countersink_diameter",10),("countersink_angle",90));
Parse("M8×1,25-6H ↧16",("thread_major_diameter",8),("thread_pitch",1.25),("thread_depth",16));
Parse("⌀60 ⌴⌀64 SF ↧3",("bore_diameter",60),("spotface_diameter",64),("spotface_depth",3));
Parse("4×⌀12 ±0,05 EQUALLY SPACED ON ⌀40 PCD",("count",4),("bore_diameter",12),("pitch_circle_diameter",40));
Parse("5×R4",("count",5),("radius",4));
Parse("6,5 ±0,05",("linear",6.5));
Parse("⌀8 H7 +0,02/-0,01",("bore_diameter",8));
Parse("Ø.25in THRU",("bore_diameter",.25));
Parse("⌀8 深12",("bore_diameter",8),("bore_depth",12));
Parse("0",("linear",0));
Check(!HoleCalloutParser.Parse("M8x1.25",DrawingValueUnit.Inch).Complete,"metric thread cannot silently use inch nominal dimensions");
foreach(var bad in new[]{"⌀8 mystery 20","⌀8 ↧12 ↧14","M8×1.25-6H LH","⌀8,1.2","0×⌀8","⌀0","⌀8 ⌵⌀12×90","⌀8 ⌴⌀12 ↧","⌀8 ±0,02 MATERIAL 42","2×8","⌀8 ↧NaN"})
    Check(!HoleCalloutParser.Parse(bad).Complete,"unknown/conflicting suffix stays blocked: "+bad);
Check(HoleCalloutParser.Parse("Ø.25in THRU").Components[0].Unit==DrawingValueUnit.Inch,"explicit source units retained");
LedgerChecks.Run(output,Check);
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,native_execution=false},new JsonSerializerOptions{WriteIndented=true}));
