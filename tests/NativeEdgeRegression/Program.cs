using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length!=3||Path.GetFileName(args[2])!=args[2])throw new ArgumentException("Supply fixture root, exact executor executable, unused result-directory name.");
var root=Path.GetFullPath(args[0]);var output=Path.Combine(root,args[2]);
if(Directory.Exists(output))throw new IOException("Use a fresh result directory.");
Directory.CreateDirectory(output);
var options=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,options));
string Sha(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var executable=Path.GetFullPath(args[1]);
Save("manifest",new{executor=executable,executor_sha256=DrawingPlanValidation.FileHash(Path.ChangeExtension(executable,".dll")),scope="native development fixture; source selects named feature and entire driving-edge group, tokens acquired from actual feature definition; not independent drawing edge-selection accuracy"});
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="native-edge-"+Guid.NewGuid().ToString("N"),ExecutablePath=executable,LogDirectory=output,StartupTimeoutMilliseconds=60000});
foreach(var item in new[]{(Name:"box_fillet",Feature:"corners",Count:4,Route:EdgeTreatmentRoute.SolidFillet),(Name:"tube_chamfer",Feature:"bore_chamfer",Count:1,Route:EdgeTreatmentRoute.SolidChamfer)})
{
    var path=Path.Combine(root,"installed-native",item.Name+".SLDPRT");var before=DrawingPlanValidation.FileHash(path);
    var discovery=await executor.InspectAsync(new(path));Save(item.Name+"-initial",discovery);
    var feature=discovery.Features?.SingleOrDefault(f=>f.Name==item.Feature);
    Check(item.Name+" actual feature has required driving-edge count",discovery.Success&&feature?.DrivingEdgePersistentReferences.Count==item.Count);
    if(feature is null||feature.DrivingEdgePersistentReferences.Count!=item.Count)continue;
    var doc=GeometryDocumentIdentity.FromSavedPath(path,before);const string revision="native-edge-fixture-v1";
    var refs=feature.DrivingEdgePersistentReferences.Select((token,i)=>new GeometryRef{RefId="edge-"+i,DocumentId=doc.DocumentId,DocumentPath=path,ModelSha256=before,NativePersistentReference=token,
        InputToFeature=item.Feature,SourceRevisionId=revision,SourceFactIds=["edge-treatment"],EntityKind=EntityKind.Edge,GeometryKind=item.Route==EdgeTreatmentRoute.SolidFillet?GeometryKind.Line:GeometryKind.Circle,
        Signature=new(){EntityKind=EntityKind.Edge,GeometryKind=item.Route==EdgeTreatmentRoute.SolidFillet?GeometryKind.Line:GeometryKind.Circle,Direction=new(0,0,1)}}).ToArray();
    var intent=new EdgeTreatmentIntent{IntentId=item.Name,SourceLiteral=item.Route==EdgeTreatmentRoute.SolidFillet?"Four named corner edges R2":"Named bore entrance chamfer5x45",SourceSha256=Sha(item.Name+" fixed native fixture specification"),SourceRevisionId=revision,
        Route=item.Route,RadiusMm=2,DistanceMm=5,AngleDegrees=45,TargetEdges=refs,TargetOperationId=item.Feature,
        EditabilityProbes=item.Route==EdgeTreatmentRoute.SolidFillet?[new("radius","D1@corners",2.1,FeatureName:"corners")]:[new("distance","D1@bore_chamfer",5.1,FeatureName:"bore_chamfer"),new("angle","D2@bore_chamfer",44,DrawingValueUnit.Degree,"bore_chamfer")]};
    var binding=new EdgeTreatmentInspectionBinding{FeatureName=item.Feature,PrimaryDimensionName="D1@"+item.Feature,SecondaryDimensionName=item.Route==EdgeTreatmentRoute.SolidChamfer?"D2@"+item.Feature:null};
    Save(item.Name+"-intent",intent);Save(item.Name+"-binding",binding);
    var workflow=new Stage4SavedModelAcceptanceWorkflow(executor,new ArtifactCacheStore());
    var actual=await workflow.VerifyEdgeTreatmentAsync(path,intent,binding);Save(item.Name+"-gate",actual);
    Check(item.Name+" actual full T19 gate passes",actual.Passed);
    Check(item.Name+" isolated probes preserve original document feature and edge identities",
        actual.Inspection.Features!.Single(f=>f.Name==item.Feature).PersistentReference==feature.PersistentReference&&
        actual.Inspection.Features!.Single(f=>f.Name==item.Feature).DrivingEdgePersistentReferences.ToHashSet().SetEquals(feature.DrivingEdgePersistentReferences));
    var finalScope=await workflow.VerifyEdgeTreatmentAsync(path,intent with{TargetEdges=refs.Select(r=>r with{InputToFeature=null}).ToArray()},binding);
    Save(item.Name+"-final-scope",finalScope);
    Check(item.Name+" consumed input edges cannot be certified in final B-Rep scope",!finalScope.Passed);
    var absentScope=await workflow.VerifyEdgeTreatmentAsync(path,intent with{TargetEdges=refs.Select(r=>r with{InputToFeature="missing-feature"}).ToArray()},binding);
    Save(item.Name+"-absent-scope",absentScope);
    Check(item.Name+" missing native feature input scope is rejected",!absentScope.Passed);
    var wrongSignature=await workflow.VerifyEdgeTreatmentAsync(path,intent with{TargetEdges=refs.Select(r=>r with{Signature=r.Signature with{AnchorMm=new(900,900,900)}}).ToArray()},binding);
    Save(item.Name+"-wrong-signature",wrongSignature);
    Check(item.Name+" matching persistent token cannot override conflicting source geometry",!wrongSignature.Passed);
    if(item.Count>1)
    {
        var repeated=await workflow.VerifyEdgeTreatmentAsync(path,intent with{TargetEdges=refs.Select((r,i)=>refs[0] with{RefId=r.RefId}).ToArray()},binding);
        Save(item.Name+"-repeated-edge",repeated);
        Check(item.Name+" repeated seed edge cannot certify a complete edge group",!repeated.Passed);
    }
    var wrong=intent.Route==EdgeTreatmentRoute.SolidFillet?intent with{RadiusMm=3}:intent with{DistanceMm=6};
    var rejected=await workflow.VerifyEdgeTreatmentAsync(path,wrong,binding);Save(item.Name+"-wrong-size",rejected);
    Check(item.Name+" different source size is rejected",!rejected.Passed);
    var missingDimension=await workflow.VerifyEdgeTreatmentAsync(path,intent,binding with{PrimaryDimensionName="missing-native-dimension"});Save(item.Name+"-missing-dimension",missingDimension);
    Check(item.Name+" unavailable required dimension yields serializable rejection",!missingDimension.Passed&&missingDimension.Observation is null);
    Check(item.Name+" original saved native remains byte-identical",DrawingPlanValidation.FileHash(path)==before);
}
Console.WriteLine("DONE "+output);
