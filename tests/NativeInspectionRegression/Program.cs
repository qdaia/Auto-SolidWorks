using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length!=3)throw new ArgumentException("Supply fixture root, exact executor executable, and unused result-directory name.");
if(Path.GetFileName(args[2])!=args[2])throw new ArgumentException("Result-directory name must be a single path segment.");
var root=Path.GetFullPath(args[0]);
var path=Path.Combine(root,"installed-native","revolved_ring.SLDPRT");
var output=Path.Combine(root,args[2]);
if(Directory.Exists(output))throw new IOException("Result directory already exists; preserve previous evidence.");
Directory.CreateDirectory(output);
var options=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,options));
string Sha(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var originalSha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var executable=Path.GetFullPath(args[1]);
if(!File.Exists(executable))throw new FileNotFoundException("Exact executor executable is required.",executable);
Save("run-manifest",new{fixture=path,fixture_sha256=originalSha,executor=executable,executor_dll_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(executable,".dll")))),core_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ModelInspection).Assembly.Location))),scope="native development regression, not real drawing or desktop plugin acceptance"});
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="native-inspection-regression-"+Guid.NewGuid().ToString("N"),ExecutablePath=executable,LogDirectory=output,StartupTimeoutMilliseconds=60000});
var initial=await executor.InspectAsync(new(path,Queries:[new(){Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,RadiusMm=10}]));Save("initial",initial);
Check("saved ring reopened and full inspection captured",initial.Success&&initial.ModelReopened&&initial.CaptureComplete);
if(!initial.Success||initial.Entities?.Count!=1){Console.WriteLine(initial.Message);Environment.ExitCode=1;return;}
var doc=GeometryDocumentIdentity.FromSavedPath(path,originalSha);
const string revision="native-development-fixture-v1";
var reference=new GeometryRef{RefId="ring-inner-wall",DocumentId=doc.DocumentId,DocumentPath=path,ModelSha256=originalSha,NativePersistentReference=initial.Entities[0].PersistentReference,EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,SourceRevisionId=revision,SourceFactIds=["ring-inner-diameter"],Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,RadiusMm=10,Direction=new(0,1,0)}};
var measurement=new MeasurementQuery{QueryId="inner-diameter",Geometry=reference,Kind=MeasurementKind.CylinderDiameter};
var topology=new ConnectivityRequirement{RequirementId="ring-through",SourceFactId="ring-inner-diameter",SourceRevisionId=revision,SourceSha256=Sha("fixed regression input: ring OD30 ID20 length20 axisY"),SourceFactFingerprint=Sha("inner diameter20; through length20"),Kind=ConnectivityKind.ThroughHole};
var connectivity=new ConnectivityInspectionQuery{QueryId="ring-through-query",Requirement=topology,GeometryRefs=[reference]};
var actual=await executor.InspectAsync(new(path,GeometryReferences:[reference],Measurements:[measurement],SourceRevisionId:revision,ConnectivityQueries:[connectivity]));Save("actual",actual);
Check("T06 persistent reference resolves on actual reopened native",actual.GeometryRefResolutions.Count==1&&actual.GeometryRefResolutions[0].Status==GeometryRefResolutionStatus.Resolved&&actual.GeometryRefResolutions[0].NativeReferenceRecovered);
Check("T07 actual saved BRep diameter is20mm",actual.Measurements.Count==1&&actual.Measurements[0].Status==GeometryMeasurementStatus.Measured&&Math.Abs(actual.Measurements[0].ScalarValue.GetValueOrDefault()-20)<.01);
Check("T07 measurement retains actual reopen provenance after rebuild",actual.ModelReopened&&actual.Measurements.Count==1&&actual.Measurements[0].ModelReopened);
Check("T08 independently probed ring through-hole passes",actual.ConnectivityChecks.Count==1&&actual.ConnectivityChecks[0].Status==ConnectivityStatus.Passed&&actual.ConnectivityChecks[0].ObservationComplete);
var wrongDoc=reference with{RefId="wrong-document",DocumentId=new string('F',64),DocumentPath=Path.Combine(root,"not-this-part.SLDPRT")};
var stale=reference with{RefId="stale-source",SourceRevisionId="old-source"};
var wrongBlind=connectivity with{QueryId="wrong-blind",Requirement=topology with{RequirementId="wrong-blind",Kind=ConnectivityKind.BlindHole}};
var negative=await executor.InspectAsync(new(path,GeometryReferences:[wrongDoc,stale],SourceRevisionId:revision,ConnectivityQueries:[wrongBlind]));Save("negative",negative);
Check("T06 wrong-document reference rejected",negative.GeometryRefResolutions.Any(r=>r.Reference.RefId=="wrong-document"&&r.Status==GeometryRefResolutionStatus.WrongDocument));
Check("T06 stale-source reference rejected",negative.GeometryRefResolutions.Any(r=>r.Reference.RefId=="stale-source"&&r.Status==GeometryRefResolutionStatus.Stale));
Check("T08 through native cannot pass blind source requirement",negative.ConnectivityChecks.Count==1&&negative.ConnectivityChecks[0].Status==ConnectivityStatus.Failed);
Check("original saved native bytes unchanged",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))==originalSha);
foreach(var fixture in new[]{"blind","thin_seal"})
{
    var fixturePath=Path.Combine(root,"t08-native-fixtures",fixture+".SLDPRT");
    var fixtureSha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath)));
    var inspected=await executor.InspectAsync(new(fixturePath,Queries:[new(){Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,RadiusMm=5}]));
    Save(fixture+"-initial",inspected);
    Check(fixture+" saved fixture captured",inspected.Success&&inspected.Entities?.Count==1);
    if(!inspected.Success||inspected.Entities?.Count!=1)continue;
    var fixtureDoc=GeometryDocumentIdentity.FromSavedPath(fixturePath,fixtureSha);
    var fixtureRef=reference with{RefId=fixture+"-wall",DocumentId=fixtureDoc.DocumentId,DocumentPath=fixturePath,ModelSha256=fixtureSha,
        NativePersistentReference=inspected.Entities[0].PersistentReference,Signature=reference.Signature with{RadiusMm=5,Direction=new(0,0,1)}};
    var blindRequirement=topology with{RequirementId=fixture+"-blind",SourceFactId=fixture+"-depth",SourceSha256=Sha("fixed native fixture "+fixture),SourceFactFingerprint=Sha("flat bottom "+fixture),Kind=ConnectivityKind.BlindHole};
    var throughRequirement=blindRequirement with{RequirementId=fixture+"-false-through",Kind=ConnectivityKind.ThroughHole};
    var checksForFixture=await executor.InspectAsync(new(fixturePath,SourceRevisionId:revision,ConnectivityQueries:[
        new(){QueryId=fixture+"-blind",Requirement=blindRequirement,GeometryRefs=[fixtureRef]},
        new(){QueryId=fixture+"-false-through",Requirement=throughRequirement,GeometryRefs=[fixtureRef]}]));
    Save(fixture+"-actual",checksForFixture);
    Check(fixture+" independently passes blind topology",checksForFixture.ConnectivityChecks.Count==2&&checksForFixture.ConnectivityChecks[0].Status==ConnectivityStatus.Passed);
    Check(fixture+" cannot pass through-hole requirement",checksForFixture.ConnectivityChecks.Count==2&&checksForFixture.ConnectivityChecks[1].Status==ConnectivityStatus.Failed);
    Check(fixture+" original bytes unchanged",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath)))==fixtureSha);
}
foreach(var name in new[]{"box_fillet","tube_chamfer","linear_holes","circular_holes"}){var inspection=await executor.InspectAsync(new(Path.Combine(root,"installed-native",name+".SLDPRT")));Save(name+"-inspection",inspection);}
var sourceProjection=new ProjectionSnapshot{ViewId="ring-top",CoordinateFrameId="ring-top-mm",SourceSha256=topology.SourceSha256!,SourceRevisionId=revision,
    Primitives=[new(){Id="outer",Kind=ProjectionPrimitiveKind.Circle,Center=new(0,0),RadiusMm=15},new(){Id="inner",Kind=ProjectionPrimitiveKind.Circle,Center=new(0,0),RadiusMm=10}]};
Save("projection-source",sourceProjection);
var projection=await executor.CaptureProjectionAsync(new(){NativePath=path,SourceDrawingSha256=sourceProjection.SourceSha256,ViewId=sourceProjection.ViewId,CoordinateFrameId=sourceProjection.CoordinateFrameId,Orientation=ProjectionOrientation.Top});
Save("projection-capture",projection);
Check("T09 production transport returns complete real saved-model projection",projection.Success&&projection.Complete&&projection.Snapshot?.NativeModelReopened==true);
if(projection.Snapshot is { } snapshot)
{
    var report=ProjectionVerifier.Compare(sourceProjection,snapshot);Save("projection-report",report);
    Check("T09 annular top view matches independently declared circles",report.Passed);
    var wrong=sourceProjection with{Primitives=sourceProjection.Primitives.Select(p=>p.Id=="inner"?p with{RadiusMm=9}:p).ToArray()};
    var rejected=ProjectionVerifier.Compare(wrong,snapshot);Save("projection-wrong-radius",rejected);
    Check("T09 wrong source radius rejected",!rejected.Passed);
}
var sectionSpec=new SectionSpec{SectionId="ring-cross-section",SourceFactId="ring-section",SourceRevisionId=revision,SourceSha256=sourceProjection.SourceSha256,
    ViewMapId="ring-views",ViewId="ring-section",CoordinateFrameId="ring-section-mm",PlaneOriginMm=new(0,0,0),PlaneNormal=new(0,1,0),ViewingDirection=new(0,1,0),InPlaneXDirection=new(1,0,0)};
var section=await executor.CaptureSectionAsync(new(){NativePath=path,Spec=sectionSpec});Save("section-capture",section);
Check("T10 production transport returns complete actual section",section.Success&&section.Complete&&section.Snapshot?.ModelReopened==true);
if(section.Snapshot is { } sectionSnapshot)
{
    Check("T10 real annulus has independently expected material and void loop areas",sectionSnapshot.Loops.Count==2&&sectionSnapshot.Loops.Count(l=>l.IsVoid)==1&&
        sectionSnapshot.Loops.Any(l=>!l.IsVoid&&Math.Abs(l.AreaMm2.GetValueOrDefault()-225*Math.PI)<.1)&&sectionSnapshot.Loops.Any(l=>l.IsVoid&&Math.Abs(l.AreaMm2.GetValueOrDefault()-100*Math.PI)<.1));
}
Check("projection and section leave original bytes unchanged",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))==originalSha);
var patternPath=Path.Combine(root,"installed-native","linear_holes.SLDPRT");
var offOrigin=await executor.CaptureProjectionAsync(new(){NativePath=patternPath,SourceDrawingSha256=Sha("plate80x30 with holes diameter5 at x=-25,-5,15"),ViewId="pattern-front",CoordinateFrameId="pattern-mm",Orientation=ProjectionOrientation.Front,OriginXmm=7,OriginYmm=11});
Save("off-origin-projection",offOrigin);
var actualCircles=offOrigin.Snapshot?.Primitives.Where(p=>p.Kind==ProjectionPrimitiveKind.Circle&&p.LineStyle=="visible").OrderBy(p=>p.Center.X).ToArray()??[];
Check("T09 preserves asymmetric positions and applies frozen source translation exactly once",offOrigin.Success&&offOrigin.Complete&&actualCircles.Length==3&&
    actualCircles.Select((p,i)=>Math.Abs(p.Center.X-new[]{-18d,2d,22d}[i])<.01&&Math.Abs(p.Center.Y-11)<.01&&Math.Abs(p.RadiusMm-2.5)<.01).All(p=>p));
var observationRequest=new ObservationRequest{RequestId="native-ring-observation",Question="Inspect the saved inner wall",SourceRevisionId=revision,SourceSha256=topology.SourceSha256!,ModelSha256=originalSha,TargetGeometry=reference,RequestedView=ObservationViewKind.Isometric,Budget=new(){MaxAttempts=1,MaxUniqueRequests=1}};
var session=new ObservationSession();var renders=0;
ObservationRenderArtifact Render(ObservationRequest request){renders++;return executor.CaptureObservationAsync(new(){NativePath=path,Observation=request,OutputDirectory=Path.Combine(output,"observations")}).GetAwaiter().GetResult();}
var observed=session.Observe(observationRequest,new(),Render);Save("observation",observed);
Check("T11 actual focused bitmap is captured and identity-bound",observed.Status==ObservationStatus.Completed&&observed.Artifact?.CoverageIds.Contains(reference.RefId)==true);
var reused=session.Observe(observationRequest with{RequestId="repeat-observation"},new(),Render);Save("observation-reused",reused);
Check("T11 identical observation reuses unchanged bytes without rendering",reused.Status==ObservationStatus.Reused&&renders==1);
var budget=session.Observe(observationRequest with{RequestId="budget",Question="A distinct follow-up question"},new(),Render);Save("observation-budget",budget);
Check("T11 exhausted budget rejects before native render",budget.Status==ObservationStatus.BudgetExceeded&&renders==1);
var staleObservation=await executor.CaptureObservationAsync(new(){NativePath=path,OutputDirectory=Path.Combine(output,"stale-observation"),Observation=observationRequest with{TargetGeometry=null,ModelSha256=new string('F',64)}});Save("observation-stale",staleObservation);
Check("T11 stale model identity cannot reuse prior screenshot",!staleObservation.Success&&staleObservation.OutputPath is null);
foreach(var probeCase in new[]{
    (Name:"box_fillet",Probes:new NativeEditabilityProbeSpec[]{new("radius","D1@corners",2.1,FeatureName:"corners")}),
    (Name:"tube_chamfer",Probes:new NativeEditabilityProbeSpec[]{new("distance","D1@bore_chamfer",5.1,FeatureName:"bore_chamfer"),new("angle","D2@bore_chamfer",44,DrawingValueUnit.Degree,"bore_chamfer")}),
    (Name:"linear_holes",Probes:new NativeEditabilityProbeSpec[]{new("count","D1@holes",4,DrawingValueUnit.Unitless,"holes"),new("spacing","D3@holes",19,FeatureName:"holes")}),
    (Name:"circular_holes",Probes:new NativeEditabilityProbeSpec[]{new("count","D1@pattern",5,DrawingValueUnit.Unitless,"pattern"),new("angle","D3@pattern",350,DrawingValueUnit.Degree,"pattern")})})
{
    var probePath=Path.Combine(root,"installed-native",probeCase.Name+".SLDPRT");
    var before=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probePath)));
    var result=await executor.InspectAsync(new(probePath,EditabilityProbes:probeCase.Probes));Save(probeCase.Name+"-editability",result);
    Check(probeCase.Name+" actual isolated parameter drive and restoration",result.Success&&result.EditabilityProbes.Count==probeCase.Probes.Length&&result.EditabilityProbes.All(p=>p.Passed));
    Check(probeCase.Name+" editability leaves original bytes unchanged",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probePath)))==before);
}
Console.WriteLine("DONE "+output);
