using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length is <3 or >4||Path.GetFileName(args[2])!=args[2]||args.Length==4&&args[3]!="axial-hole")throw new ArgumentException("Supply fixture root, exact executor executable, unused results directory, and optional axial-hole.");
var axialHole=args.Length==4;
var root=Path.GetFullPath(args[0]);var output=Path.Combine(root,args[2]);
if(Directory.Exists(output))throw new IOException("Preserve previous evidence; use a fresh directory.");
Directory.CreateDirectory(output);
var options=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,options));
string Sha(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var executable=Path.GetFullPath(args[1]);var path=Path.Combine(output,"shaft.SLDPRT");
Save("manifest",new{executor=executable,executor_sha256=DrawingPlanValidation.FileHash(Path.ChangeExtension(executable,".dll")),scope="typed native development fixture, two stepped diameters and four independent driving parameters; no holdout drawing claim"});
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="native-revolve-"+Guid.NewGuid().ToString("N"),ExecutablePath=executable,LogDirectory=output,StartupTimeoutMilliseconds=60000});
SketchEntityReference Seg(int n,SketchEntityPart part=SketchEntityPart.Segment)=>new(){PrimitiveIndex=0,SegmentIndex=n,Part=part};
var points=new[]{new ProfilePoint(0,0),new ProfilePoint(20,0),new ProfilePoint(20,10),new ProfilePoint(15,10),new ProfilePoint(15,25),new ProfilePoint(0,25)};
var plan=new ModelingPlan{PlanId="native-revolve-"+Guid.NewGuid().ToString("N"),Name="native_stepped_shaft",SourceText="Development fixture: diameter40 length10 then diameter30 length15, Y axis, editable profile",
    Output=new(){NativePath=path},Operations=[
        new NativeFeatureOperation{Id="axis",Name="axis",Options=new(){Kind=NativeFeatureKind.ReferenceAxis,AxisStartMm=new(0,-10,0),AxisEndMm=new(0,35,0)}},
        new ProfileSketchOperation{Id="section",Name="section",Primitives=[new PolygonProfile{Points=points}],
            Constraints=Enumerable.Range(0,6).Select(i=>new SketchConstraintSpec{Kind=i%2==0?SketchConstraintKind.Horizontal:SketchConstraintKind.Vertical,Entities=[Seg(i)]}).ToArray(),
            Dimensions=[new(){Name="R1",Kind=SketchDimensionKind.Horizontal,Value=20,Entities=[Seg(0)],LabelPosition=new(10,-5)},
                new(){Name="L1",Kind=SketchDimensionKind.Vertical,Value=10,Entities=[Seg(1)],LabelPosition=new(25,5)},
                new(){Name="R2",Kind=SketchDimensionKind.Horizontal,Value=15,Entities=[Seg(0,SketchEntityPart.StartPoint),Seg(3,SketchEntityPart.StartPoint)],LabelPosition=new(8,30)},
                new(){Name="L2",Kind=SketchDimensionKind.Vertical,Value=15,Entities=[Seg(3)],LabelPosition=new(30,17)}]},
        new NativeFeatureOperation{Id="revolve",Name="revolve",DependsOn=["axis","section"],Options=new(){Kind=NativeFeatureKind.RevolveBoss,SketchId="section",AxisId="axis",AngleDegrees=360}}],
    Acceptance=new(){ExpectedFeatures=["axis","section","revolve"],Geometry=new(){ExpectedVolumeMm3=Math.PI*(400*10+225*15),VolumeTolerancePercent=.01}}};
if(axialHole)plan=plan with{SourceText=plan.SourceText+"; diameter10 through axial hole",Operations=[..plan.Operations,
    new NativeFeatureOperation{Id="axial_hole",Name="axial_hole",DependsOn=["revolve"],Options=new(){Kind=NativeFeatureKind.Hole,DiameterMm=10,ThroughAll=true,HoleCenters=[new(0,0)],
        Frame=new(){OriginMm=new(0,0,0),Normal=new(0,1,0),XDirection=new(1,0,0)}}}],
    Acceptance=plan.Acceptance with{ExpectedFeatures=[..plan.Acceptance.ExpectedFeatures,"axial_hole"],Geometry=plan.Acceptance.Geometry with{ExpectedVolumeMm3=plan.Acceptance.Geometry.ExpectedVolumeMm3-25*25*Math.PI}}};
Save("plan",plan);var built=await executor.ExecuteAsync(plan,false);Save("build",built);
Check("two-step native revolve builds from typed dimensioned profile",built.Success);if(!built.Success)return;
var before=DrawingPlanValidation.FileHash(path);var inspected=await executor.InspectAsync(new(path));Save("initial",inspected);
Check("actual revolve records its direct profile parent",inspected.Features?.SingleOrDefault(f=>f.Name=="revolve")?.ParentFeatureNames.Contains("section")==true);
var sourceSha=Sha("shaft40x10+30x15 fixed XY silhouette; axial-hole="+axialHole);const string revision="native-revolve-v1";var viewFp=Sha("complete fixed shaft front silhouette; hidden-hole="+axialHole);
// The shoulder's front semicircular rim projects across the whole diameter at y=10.
// This is a visible feature edge in addition to the outside silhouette.
var segments=new[]{(new ProjectionPointMm(-20,0),new ProjectionPointMm(20,0)),(new ProjectionPointMm(20,0),new ProjectionPointMm(20,10)),
    (new ProjectionPointMm(-20,0),new ProjectionPointMm(-20,10)),(new ProjectionPointMm(-20,10),new ProjectionPointMm(20,10)),
    (new ProjectionPointMm(15,10),new ProjectionPointMm(15,25)),(new ProjectionPointMm(-15,10),new ProjectionPointMm(-15,25)),
    (new ProjectionPointMm(-15,25),new ProjectionPointMm(15,25))};
var source=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="model-xy-mm",SourceSha256=sourceSha,SourceRevisionId=revision,
    Primitives=segments.Select((p,i)=>new ProjectionPrimitive{Id="outline-"+i,Kind=ProjectionPrimitiveKind.Line,Start=p.Item1,End=p.Item2,RequirementId="T09:shaft",SourceFactId="shaft",SourceFactFingerprint=sourceSha,RequirementFingerprint=viewFp}).ToArray()};
if(axialHole)source=source with{Primitives=[..source.Primitives,
    new(){Id="hole-left",Kind=ProjectionPrimitiveKind.Line,Start=new(-5,0),End=new(-5,25),LineStyle="hidden",RequirementId="T09:shaft",SourceFactId="axial-hole",SourceFactFingerprint=Sha("diameter10 through"),RequirementFingerprint=viewFp},
    new(){Id="hole-right",Kind=ProjectionPrimitiveKind.Line,Start=new(5,0),End=new(5,25),LineStyle="hidden",RequirementId="T09:shaft",SourceFactId="axial-hole",SourceFactFingerprint=Sha("diameter10 through"),RequirementFingerprint=viewFp}]};
Save("source-projection",source);
var capture=await executor.CaptureProjectionAsync(new(){NativePath=path,SourceDrawingSha256=sourceSha,ViewId=source.ViewId,CoordinateFrameId=source.CoordinateFrameId,Orientation=ProjectionOrientation.Front});Save("projection-capture",capture);
Check("actual stepped-shaft front projection is captured",capture.Success&&capture.Complete&&capture.Snapshot is not null);if(capture.Snapshot is null)return;
var projectionOptions=new ProjectionComparisonOptions{CompareVisibleOnly=!axialHole};
var report=ProjectionVerifier.Compare(source,capture.Snapshot,projectionOptions);Save("projection-report",report);Check("actual complete stepped-shaft projection matches declared fixture geometry",report.Passed);
var doc=GeometryDocumentIdentity.FromSavedPath(path,before);
var profile=new RevolvedFamilyProfile{ProfileId="shaft",SourceSha256=sourceSha,SourceRevisionId=revision,AxisOriginMm=new(0,0,0),AxisDirection=new(0,1,0),
    AxisReference=new(){RefId="axis",DocumentId=doc.DocumentId,DocumentPath=path,ModelSha256=before,SourceFactIds=["shaft"],SourceRevisionId=revision,EntityKind=EntityKind.Axis,Signature=new(){EntityKind=EntityKind.Axis,Direction=new(0,1,0)}},
    RequiredScopeFingerprint=Sha("two native steps, full projection, four independent dimensions"),CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:shaft",viewFp}},RequiredViewCheckIds=["T09:shaft"],
    Steps=[new(){SourceFactId="step1",AxialStartMm=0,AxialLengthMm=10,RadialValueMm=40},new(){SourceFactId="step2",AxialStartMm=10,AxialLengthMm=15,RadialValueMm=30}],
    EditabilityProbes=[new("r1","R1@section",20.1,FeatureName:"section"),new("l1","L1@section",10.1,FeatureName:"section"),new("r2","R2@section",15.1,FeatureName:"section"),new("l2","L2@section",15.1,FeatureName:"section")]};
var binding=new RevolvedFamilyInspectionBinding{FeatureName="revolve",DrivingDimensions=[
    new(){SourceFactId="step1",Parameter=RevolvedDrivingParameterKind.Diameter,DimensionName="R1@section"},new(){SourceFactId="step1",Parameter=RevolvedDrivingParameterKind.AxialLength,DimensionName="L1@section"},
    new(){SourceFactId="step2",Parameter=RevolvedDrivingParameterKind.Diameter,DimensionName="R2@section"},new(){SourceFactId="step2",Parameter=RevolvedDrivingParameterKind.AxialLength,DimensionName="L2@section"}]};
binding=binding with{DrivingDimensions=binding.DrivingDimensions.Select(d=>d with{OwnerFeatureName="section",NativeRadialSemantic=d.Parameter==RevolvedDrivingParameterKind.Diameter?RadialDimensionSemantic.Radius:RadialDimensionSemantic.Diameter}).ToArray()};
var evidence=new Stage4EvidenceBundle{ProjectionReports=[report]};
if(axialHole)
{
    var discovery=await executor.InspectAsync(new(path,Queries:[new(){Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,RadiusMm=5,Direction=new(0,1,0),PositionMm=new(5,12.5,0)}]));Save("axial-face",discovery);
    Check("axial hole has exactly one measured native cylindrical face",discovery.Success&&discovery.Entities?.Count==1);if(discovery.Entities?.Count!=1)return;
    var reference=new GeometryRef{RefId="axial-wall",DocumentId=doc.DocumentId,DocumentPath=path,ModelSha256=before,NativePersistentReference=discovery.Entities[0].PersistentReference,
        SourceFactIds=["axial-hole"],SourceRevisionId=revision,EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,
        Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,AnchorMm=new(0,12.5,0),RadiusMm=5,Direction=new(0,1,0)}};
    var requirement=new ConnectivityRequirement{RequirementId="T08:axial",SourceFactId="axial-hole",SourceRevisionId=revision,SourceSha256=sourceSha,SourceFactFingerprint=Sha("diameter10 through"),Kind=ConnectivityKind.ThroughHole};
    var topology=await executor.InspectAsync(new(path,SourceRevisionId:revision,ConnectivityQueries:[new(){QueryId="axial-through",Requirement=requirement,GeometryRefs=[reference]}]));Save("axial-topology",topology);
    Check("actual axial hole produces complete current-model T08 through evidence",topology.ConnectivityChecks.Count==1&&topology.ConnectivityChecks[0].Status==ConnectivityStatus.Passed);
    profile=profile with{Kind=RevolvedFamilyKind.SteppedShaftWithAxialHole,AxialHoleConnectivityCheckId=requirement.RequirementId,
        CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:shaft",viewFp},{requirement.RequirementId,ConnectivityVerifier.Fingerprint(requirement)}}};
    evidence=evidence with{ConnectivityChecks=topology.ConnectivityChecks};
}
Save("profile",profile);Save("binding",binding);Save("evidence",evidence);
var workflow=new Stage4SavedModelAcceptanceWorkflow(executor,new ArtifactCacheStore());
var actual=await workflow.VerifyRevolvedAsync(path,profile,binding,evidence);Save("gate",actual);
Check("T17 actual two-step native family final gate passes",actual.Passed);
var missing=await workflow.VerifyRevolvedAsync(path,profile,binding with{DrivingDimensions=binding.DrivingDimensions.Take(3).ToArray()},evidence);Save("missing-binding",missing);
Check("missing independent step parameter binding is rejected",!missing.Passed);
var wrongOwner=await workflow.VerifyRevolvedAsync(path,profile,binding with{DrivingDimensions=binding.DrivingDimensions.Select(d=>d with{OwnerFeatureName="axis"}).ToArray()},evidence);Save("wrong-owner",wrongOwner);
Check("native axis feature cannot impersonate consumed sketch parameter owner",!wrongOwner.Passed);
var wrongRadial=await workflow.VerifyRevolvedAsync(path,profile,binding with{DrivingDimensions=binding.DrivingDimensions.Select(d=>d with{NativeRadialSemantic=RadialDimensionSemantic.Diameter}).ToArray()},evidence);Save("wrong-radial-semantic",wrongRadial);
Check("radius dimensions require explicit radius-to-diameter semantics",!wrongRadial.Passed);
var partial=await workflow.VerifyRevolvedAsync(path,profile with{EditabilityProbes=profile.EditabilityProbes.Take(3).ToArray()},binding,evidence);Save("partial-probe",partial);
Check("three probes cannot certify four independently required native parameters",!partial.Passed);
var wrongSize=await workflow.VerifyRevolvedAsync(path,profile with{Steps=profile.Steps.Select((s,i)=>i==1?s with{RadialValueMm=32}:s).ToArray()},binding,evidence);Save("wrong-step-size",wrongSize);
Check("actual source diameter mismatch blocks final family acceptance",!wrongSize.Passed);
var brokenReport=ProjectionVerifier.Compare(source with{Primitives=source.Primitives.Select((p,i)=>i==0?p with{End=new(22,0)}:p).ToArray()},capture.Snapshot,projectionOptions);
var badProjection=await workflow.VerifyRevolvedAsync(path,profile,binding,new(){ProjectionReports=[brokenReport]});Save("failed-projection",badProjection);
Check("real projection producer failure remains blocking",!brokenReport.Passed&&!badProjection.Passed);
var reverseSteps=profile with{Steps=profile.Steps.Select((s,i)=>s with{AxialStartMm=i==0?15:0}).ToArray()};
Check("reversing source step order is rejected",!(await workflow.VerifyRevolvedAsync(path,reverseSteps,binding,evidence)).Passed);
Check("offset source rotation axis is rejected",!(await workflow.VerifyRevolvedAsync(path,profile with{AxisOriginMm=new(1,0,0)},binding,evidence)).Passed);
if(axialHole)
{
    Check("axial-hole family cannot pass without its own T08 evidence",!(await workflow.VerifyRevolvedAsync(path,profile,binding,evidence with{ConnectivityChecks=[]})).Passed);
    var displaced=evidence.ConnectivityChecks[0] with{ObservedAxisOriginMm=new(5,0,0)};
    Check("injected displaced T08 axis is rejected despite unchanged producer status",!(await workflow.VerifyRevolvedAsync(path,profile,binding,evidence with{ConnectivityChecks=[displaced]})).Passed);
    var shortened=evidence.ConnectivityChecks[0] with{ObservedAxialInterval=new(0,24)};
    Check("injected shortened T08 interval cannot certify axial through-hole",!(await workflow.VerifyRevolvedAsync(path,profile,binding,evidence with{ConnectivityChecks=[shortened]})).Passed);
}
Check("probe and inspection leave native source bytes unchanged",DrawingPlanValidation.FileHash(path)==before);
