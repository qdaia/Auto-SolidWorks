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
string Sha(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var nativePath=Path.Combine(root,"installed-native","linear_holes.SLDPRT");var modelSha=DrawingPlanValidation.FileHash(nativePath);
var sourceSha=Sha("Independent fixture specification: plate80x30x10, three diameter5 through holes at (-25,0),(-5,0),(15,0), native linear pattern spacing20");
var sourceFact=Sha("three holes diameter5 through spacing20");var projectionFingerprint=Sha("fixed front projection three circles and rectangular plate boundary");
const string revision="native-family-source-v1";
var centers=new[]{new ProfilePoint(-25,0),new ProfilePoint(-5,0),new ProfilePoint(15,0)};
var executable=Path.GetFullPath(args[1]);
Save("manifest",new{nativePath,modelSha,sourceSha,executor=executable,executor_sha256=DrawingPlanValidation.FileHash(Path.ChangeExtension(executable,".dll")),scope="real finite-family production workflow with deterministic development requirements; no real drawing acceptance"});
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="native-family-"+Guid.NewGuid().ToString("N"),ExecutablePath=executable,LogDirectory=output,StartupTimeoutMilliseconds=60000});
var queries=new List<ConnectivityInspectionQuery>();
for(var i=0;i<centers.Length;i++)
{
    var center=centers[i];
    var discovery=await executor.InspectAsync(new(nativePath,Queries:[new(){Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,RadiusMm=2.5,PositionMm=new(center.Xmm+2.5,center.Ymm,5)}]));
    Save("hole-"+i+"-discovery",discovery);
    Check("hole "+i+" resolves to one real scoped cylindrical face",discovery.Success&&discovery.Entities?.Count==1);
    if(!discovery.Success||discovery.Entities?.Count!=1)return;
    var doc=GeometryDocumentIdentity.FromSavedPath(nativePath,modelSha);
    var reference=new GeometryRef{RefId="hole-"+i,DocumentId=doc.DocumentId,DocumentPath=nativePath,ModelSha256=modelSha,NativePersistentReference=discovery.Entities[0].PersistentReference,
        EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,SourceRevisionId=revision,SourceFactIds=["holes"],
        Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,RadiusMm=2.5,Direction=new(0,0,1),AnchorMm=new(center.Xmm,center.Ymm,5)}};
    queries.Add(new(){QueryId="hole-query-"+i,Requirement=new(){RequirementId="T08:holes:"+i,SourceFactId="holes",SourceRevisionId=revision,SourceSha256=sourceSha,SourceFactFingerprint=sourceFact,Kind=ConnectivityKind.ThroughHole},GeometryRefs=[reference]});
}
var topology=await executor.InspectAsync(new(nativePath,SourceRevisionId:revision,ConnectivityQueries:queries));Save("connectivity",topology);
Check("all three actual holes have independent complete T08 evidence",topology.ConnectivityChecks.Count==3&&topology.ConnectivityChecks.All(c=>c.Status==ConnectivityStatus.Passed)&&topology.ConnectivityChecks.Select(c=>c.GeometryScopeFingerprint).Distinct().Count()==3);
var primitives=centers.Select((c,i)=>new ProjectionPrimitive{Id="hole-"+i,Kind=ProjectionPrimitiveKind.Circle,Center=new(c.Xmm,c.Ymm),RadiusMm=2.5,
    RequirementId="T09:holes",SourceFactId="holes",SourceFactFingerprint=sourceFact,RequirementFingerprint=projectionFingerprint}).ToList();
var corners=new[]{new ProjectionPointMm(-40,-15),new ProjectionPointMm(40,-15),new ProjectionPointMm(40,15),new ProjectionPointMm(-40,15)};
for(var i=0;i<4;i++)primitives.Add(new(){Id="plate-"+i,Kind=ProjectionPrimitiveKind.Line,Start=corners[i],End=corners[(i+1)%4],RequirementId="T09:outline",SourceFactId="plate",SourceFactFingerprint=Sha("80x30plate"),RequirementFingerprint=Sha("rectangular outline")});
var source=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="model-xy-mm",SourceSha256=sourceSha,SourceRevisionId=revision,Primitives=primitives};Save("source-projection",source);
var captured=await executor.CaptureProjectionAsync(new(){NativePath=nativePath,SourceDrawingSha256=sourceSha,ViewId=source.ViewId,CoordinateFrameId=source.CoordinateFrameId,Orientation=ProjectionOrientation.Front});Save("captured-projection",captured);
Check("actual native front projection captured",captured.Success&&captured.Complete&&captured.Snapshot is not null);
if(captured.Snapshot is null)return;
var report=ProjectionVerifier.Compare(source,captured.Snapshot);Save("projection-report",report);
Check("T09 full actual projection matches source geometry",report.Passed);
var profile=new HoleGroupProfile{GroupId="three-linear-holes",SourceSha256=sourceSha,SourceRevisionId=revision,SourceFactId="holes",SourceFactFingerprint=sourceFact,RequiredScopeFingerprint=Sha("holes diameter5 count3 positions through linear20 native-editable"),
    CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:holes",projectionFingerprint}},PatternKind=HoleGroupPatternKind.Linear,HoleKind=HoleKind.Simple,ExpectedCenters=centers,DiameterMm=5,ThroughAll=true,PatternSpacingMm=20,
    ConnectivityCheckId="T08:holes",ProjectionCheckId="T09:holes",PatternEditabilityProbes=[new("count","D1@holes",4,DrawingValueUnit.Unitless,"holes"),new("spacing","D3@holes",19,FeatureName:"holes")]};
var binding=new HoleGroupInspectionBinding{PatternFeatureName="holes",PatternCountDimensionName="D1@holes",PatternSpacingDimensionName="D3@holes"};
var evidence=new Stage4EvidenceBundle{ConnectivityChecks=topology.ConnectivityChecks,ProjectionReports=[report]};Save("profile",profile);Save("binding",binding);Save("evidence",evidence);
var workflow=new Stage4SavedModelAcceptanceWorkflow(executor,new ArtifactCacheStore());
var accepted=await workflow.VerifyHoleGroupAsync(nativePath,profile,binding,evidence);Save("accepted",accepted);
Check("T18 full production workflow accepts real native linear group",accepted.Passed);
var warm=await workflow.VerifyHoleGroupAsync(nativePath,profile,binding,evidence);Save("warm",warm);
Check("T16 actual inspection cache reuses the same native identity",warm.Passed&&warm.InspectionCacheHit);
var seedOnly=await workflow.VerifyHoleGroupAsync(nativePath,profile,binding,evidence with{ConnectivityChecks=topology.ConnectivityChecks.Take(1).ToArray()});Save("seed-only",seedOnly);
Check("T18 one real seed check cannot certify three holes",!seedOnly.Passed);
var missingHole=await workflow.VerifyHoleGroupAsync(nativePath,profile with{ExpectedCenters=centers.Take(2).ToArray()},binding,evidence);Save("wrong-count",missingHole);
Check("T18 actual third hole cannot disappear from declared group",!missingHole.Passed);
var wrongReport=ProjectionVerifier.Compare(source with{Primitives=[..primitives,new(){Id="missing-hole",Kind=ProjectionPrimitiveKind.Circle,Center=new(30,0),RadiusMm=2.5,RequirementId="T09:holes",SourceFactId="holes",SourceFactFingerprint=sourceFact,RequirementFingerprint=projectionFingerprint}]},captured.Snapshot);
var wrongProjection=await workflow.VerifyHoleGroupAsync(nativePath,profile,binding,evidence with{ProjectionReports=[wrongReport]});Save("failed-projection",wrongProjection);
Check("T18 actual T09 producer failure blocks final gate",!wrongProjection.Passed);
Check("all native checks leave original bytes unchanged",DrawingPlanValidation.FileHash(nativePath)==modelSha);
Console.WriteLine("DONE "+output);
