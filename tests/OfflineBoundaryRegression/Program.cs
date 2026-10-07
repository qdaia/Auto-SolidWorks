using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length!=1) throw new ArgumentException("Usage: offline output directory");
var output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output); var passed=new List<string>();
void Check(bool condition,string name) { if(!condition)throw new Exception("FAIL: "+name);passed.Add(name);Console.WriteLine("PASS: "+name); }
var compiler=new RuleBasedTextCompiler();
ModelingPlan Compile(string text,string id)
{
    var result=compiler.Compile(text,Path.Combine(output,id+".SLDPRT"));
    File.WriteAllText(Path.Combine(output,id+"-compilation.json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));
    Check(result.Success,id+" compiles");return result.Plan!;
}
void Reject(string text,string name) => Check(!compiler.Compile(text).Success,name);
var blind=Compile("80 x 50 x 20 mm plate centered diameter 10 mm blind hole depth 6 mm","blind-plate");
var cut=blind.Operations.OfType<ExtrudeCutOperation>().Single();
Check(cut.EndCondition==ExtrudeEndCondition.Blind&&cut.DepthMm==6,"blind is actual depth-6 cut, never through-hole");
Check(cut.ReverseDirection,"top-frame cut follows native-calibrated inward direction");
Check(blind.Operations.OfType<ProfileSketchOperation>().First().Primitives.Count==1,"blind hole is not an inner loop in base extrusion");
var profile=blind.Operations.OfType<ProfileSketchOperation>().Last();
Check(profile.AutoDimensionPrimitives&&profile.RequireFullyDefined&&profile.Frame!.OriginMm.Z==20,"blind entry has top frame and driving sketch");
Check(Math.Abs(blind.Acceptance.Geometry.ExpectedVolumeMm3!.Value-(80000-150*Math.PI))<1e-8,"blind volume independently subtracts 6 mm cylinder");
Check(blind.Verification.CylinderGroups.Single().LengthMm==6&&blind.Verification.CylinderGroups.Single().AxisStartsMm.Single().Z==14,"blind independent wall checks actual axial extent");
Check(blind.Verification.SurfaceSamples.Single().PointsMm.Single().Z==14,"blind floor independently required");
var chinese=Compile("长80毫米 宽50毫米 厚20毫米 板 中心直径10毫米盲孔 深度6毫米","blind-chinese");
Check(chinese.Operations.OfType<ExtrudeCutOperation>().Single().DepthMm==6,"Chinese depth retained");
var units=Compile("8 x 5 x 2 cm plate center blind hole diameter 1 cm depth 0.6 cm","blind-cm");
Check(units.Acceptance.ExpectedBoundingBoxMm==new BoundingBoxSpec(80,50,20)&&units.Operations.OfType<ExtrudeCutOperation>().Single().DepthMm==6,"all blind quantities normalized before grammar");
var cylinder=Compile("cylinder diameter 40 mm height 60 mm center blind hole diameter 10 mm depth 15 mm","blind-cylinder");
Check(((CircleProfile)cylinder.Operations.OfType<ProfileSketchOperation>().Last().Primitives.Single()).DiameterMm==10,"cylinder bore diameter distinct from body diameter");
Check(Math.Abs(cylinder.Acceptance.Geometry.ExpectedVolumeMm3!.Value-(24000*Math.PI-375*Math.PI))<1e-7,"cylinder blind volume");
var through=Compile("cylinder diameter 40 mm height 60 mm centered through hole diameter 10 mm","through-cylinder");
Check(through.Operations.OfType<ExtrudeCutOperation>().Single().EndCondition==ExtrudeEndCondition.ThroughAll,"cylinder center through hole uses through cut");
var legacy=Compile("80 x 50 x 10 mm plate center diameter 10 mm through hole","legacy-through");
Check(legacy.Operations.OfType<ProfileSketchOperation>().Single().Primitives.OfType<CircleProfile>().Single().Role==ContourRole.Inner,"legacy plate through profile retained");

var shell=Compile("80 x 50 x 20 mm plate open top shell wall thickness 2 mm","open-shell");
var native=shell.Operations.OfType<NativeFeatureOperation>().Single();
Check(native.Options.Kind==NativeFeatureKind.Shell&&!native.Options.Reverse&&native.Options.ThicknessMm==2,"shell is native inward shell operation");
Check(native.Options.Selections.Single().PositionMm==new Vector3(0,0,20)&&native.Options.Selections.Single().FeatureId=="extrude_base","shell opening uses scoped exact top face");
Check(shell.Acceptance.Geometry.ExpectedVolumeMm3==80000-76*46*18,"open box source volume accounts for bottom once");
Check(shell.Verification.BoundaryClearances.Single().PointsMm.Single().Z==20,"opening cannot become cap unnoticed");
var closed=Compile("80 x 50 x 20 mm plate closed shell thickness 2 mm","closed-shell");
Check(closed.Operations.OfType<NativeFeatureOperation>().Single().Options.Selections.Count==0,"closed shell removes no face");
Check(closed.Acceptance.Geometry.ExpectedVolumeMm3==80000-76*46*16,"closed box volume accounts for two caps");
var chineseShell=Compile("长80毫米 宽50毫米 厚20毫米 板 顶部开口向内抽壳 壁厚2毫米","shell-chinese");
Check(chineseShell.Operations.OfType<NativeFeatureOperation>().Single().Options.ThicknessMm==2,"Chinese shell wall consumed");
var cylindricalShell=Compile("cylinder diameter 40 mm height 60 mm open top shell thickness 2 mm","cylinder-shell");
Check(Math.Abs(cylindricalShell.Acceptance.Geometry.ExpectedVolumeMm3!.Value-(24000-324*58)*Math.PI)<1e-7,"open cylinder shell independent source volume");
var shallow=Compile("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 0.01 mm","shallow-blind");
Check(shallow.Verification.CylinderGroups.Single().ToleranceMm<=.0001&&shallow.Verification.SurfaceSamples.Single().ToleranceMm<=.0001,"shallow depth cannot be swallowed by default geometric tolerance");
var thinShell=Compile("80 x 50 x 20 mm plate closed shell thickness 0.01 mm","thin-shell");
Check(thinShell.Verification.SurfaceSamples.Single().ToleranceMm<=.0001,"thin wall floor cannot match original bottom under loose tolerance");

foreach(var (text,name) in new (string,string)[] {
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole","missing blind depth"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 20 mm","blind becomes through"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 21 mm","blind beyond material"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind through hole depth 6 mm","contradictory termination"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 6 mm depth 8 mm","duplicate depth"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 0 mm","zero depth"),
 ("80 x 50 x 20 mm plate diameter 10 mm blind hole depth 6 mm","unspecified hole position"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 6 mm from bottom","unsupported entrance cannot be ignored"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 6 mm drill tip angle 118","unmodeled drill tip rejected"),
 ("80 x 50 x 20 mm plate center diameter 10 mm blind hole depth 6 mm diameter 12 mm","duplicate bore diameter"),
 ("80 x 50 x 20 mm plate 2 center diameter 10 mm blind holes depth 6 mm","unmodeled count rejected"),
 ("cylinder diameter 40 mm height 60 mm center through hole diameter 10 mm depth 20 mm","through hole rejects finite depth"),
 ("80 x 50 x 20 mm plate shell thickness 2 mm","unspecified shell opening"),
 ("80 x 50 x 20 mm plate open top closed shell thickness 2 mm","conflicting shell opening"),
 ("80 x 50 x 20 mm plate open top shell thickness 10 mm","collapsing shell wall"),
 ("80 x 50 x 20 mm plate open top shell thickness 2 mm thickness 3 mm","duplicate wall thickness"),
 ("80 x 50 x 20 mm plate open top outward shell thickness 2 mm","unsupported outward intent rejected"),
 ("80 x 50 x 20 mm plate open top shell thickness 2 mm center diameter 10 mm through hole","unordered shell-hole combination rejected"),
 ("80 x 50 x 20 mm plate closed shell thickness 2 mm all edges fillet R2","unsupported shell-fillet combination rejected"),
 ("80 x 50 x 20 mm plate open top shell thickness 2 mm pocket 5 mm","unconsumed pocket rejected"),
 ("cylinder diameter 40 mm height 60 mm blind hole diameter 10 mm depth 15 mm","cylinder hole needs center"),
 ("centered cylinder diameter 40 mm height 60 mm blind hole diameter 10 mm depth 15 mm","body centered does not declare hole centered"),
 ("centered 80 x 50 x 20 mm plate diameter 10 mm blind hole depth 6 mm","plate centered does not declare blind hole centered"),
 ("centered 80 x 50 x 20 mm plate diameter 10 mm through hole","plate centered does not declare through hole centered") }) Reject(text,name);

File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new {status="pass",passed=passed.Count,scope="offline_text_operation_and_independent_geometry_contracts",native_acceptance="not_run",checks=passed},new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true}));
Console.WriteLine("Offline boundary checks passed: "+passed.Count);
