using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length is <3 or >4||Path.GetFileName(args[2])!=args[2]||args.Length==4&&args[3]!="inspect-existing")throw new ArgumentException("Supply output root, exact executor executable, unused results directory, optionally inspect-existing.");
var inspectExisting=args.Length==4;
var output=Path.Combine(Path.GetFullPath(args[0]),args[2]);if(Directory.Exists(output))throw new IOException("Use a fresh result directory.");
Directory.CreateDirectory(output);
var options=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,options));
string Sha(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var executable=Path.GetFullPath(args[1]);
Save("manifest",new{executor=executable,executor_sha256=DrawingPlanValidation.FileHash(Path.ChangeExtension(executable,".dll")),scope="real native saved-hole acquisition and finite-family development fixtures; not independent drawing holdout"});
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="native-subtype-"+Guid.NewGuid().ToString("N"),ExecutablePath=executable,LogDirectory=output,StartupTimeoutMilliseconds=60000});
foreach(var item in new[]{(Name:"sink_front",Kind:HoleKind.Countersink,Reverse:false),(Name:"sink_rear",Kind:HoleKind.Countersink,Reverse:true),
    (Name:"tapped",Kind:HoleKind.Tapped,Reverse:false),(Name:"counterbore",Kind:HoleKind.Counterbore,Reverse:false),
    (Name:"tapped_blind",Kind:HoleKind.Tapped,Reverse:false),(Name:"sink_blind",Kind:HoleKind.Countersink,Reverse:false),
    (Name:"counterbore_blind",Kind:HoleKind.Counterbore,Reverse:true)})
{
    var through=!item.Name.EndsWith("_blind",StringComparison.Ordinal);var depth=through?10:8;
    var path=Path.Combine(inspectExisting?Path.GetFullPath(args[0]):output,item.Name+".SLDPRT");var diameter=item.Kind==HoleKind.Tapped?6.8:6;
    var extra=item.Kind==HoleKind.Countersink?36*Math.PI:item.Kind==HoleKind.Counterbore?27*Math.PI:0;
    var plan=new ModelingPlan{PlanId="native-subtype-"+Guid.NewGuid().ToString("N"),Name=item.Name,SourceText="Fixed native subtype development fixture:40x30x10 plate, centered supported hole",
        Output=new(){NativePath=path},Operations=[new ProfileSketchOperation{Id="base",Name="base",Primitives=[new CenteredRectangleProfile{WidthMm=40,HeightMm=30}]},
            new ExtrudeBossOperation{Id="plate",Name="plate",SketchId="base",DependsOn=["base"],DepthMm=10},
            new NativeFeatureOperation{Id="hole",Name="hole",DependsOn=["plate"],Options=new(){Kind=NativeFeatureKind.Hole,HoleKind=item.Kind,DiameterMm=diameter,ThroughAll=through,DepthMm=depth,
                CountersinkDiameterMm=12,CountersinkAngleDegrees=90,CounterboreDiameterMm=12,CounterboreDepthMm=1,ThreadMajorDiameterMm=8,ThreadDesignation="M8 x 1.25",
                Frame=new(){OriginMm=new(0,0,item.Reverse?10:0)},Reverse=item.Reverse,HoleCenters=[new(0,0)]}}],
        Acceptance=new(){Geometry=new(){ExpectedVolumeMm3=12000-Math.PI*diameter*diameter/4*depth-extra,VolumeTolerancePercent=.01}}};
    if(!inspectExisting)
    {
        Save(item.Name+"-plan",plan);var built=await executor.ExecuteAsync(plan,false);Save(item.Name+"-build",built);
        Check(item.Name+" typed native build",built.Success);if(!built.Success)continue;
    }
    var hash=DrawingPlanValidation.FileHash(path);var actual=await executor.InspectAsync(new(path));Save(item.Name+"-inspection",actual);
    Check(item.Name+" exact saved native inspection",actual.Success&&actual.ModelReopened&&actual.CaptureComplete);
    if(item.Kind==HoleKind.Countersink)
        Check(item.Name+" analytic complete interior cone measured",actual.Cones.Count==1&&actual.Cones[0].Interior&&actual.Cones[0].CompleteWall&&
            Math.Abs(Math.Max(actual.Cones[0].StartRadiusMm,actual.Cones[0].EndRadiusMm)-6)<.001&&Math.Abs(actual.Cones[0].IncludedAngleDegrees-90)<.001);
    if(item.Kind==HoleKind.Tapped)
        Check(item.Name+" native cosmetic-thread designation and major diameter read back",actual.CosmeticThreads.Count==1&&actual.CosmeticThreads[0].Complete&&
            actual.CosmeticThreads[0].Designation=="M8 x 1.25"&&Math.Abs(actual.CosmeticThreads[0].MajorDiameterMm-8)<.001&&actual.CosmeticThreads[0].ThroughAll==through&&
            (through||Math.Abs(actual.CosmeticThreads[0].BlindDepthMm-depth)<.001));
    Check(item.Name+" source bytes remain unchanged",DrawingPlanValidation.FileHash(path)==hash);
    const string revision="native-subtype-source-v1";
    var sourceSha=Sha("Independent development specification: "+item.Name+" plate40x30x10 nominal subtype dimensions");
    var factSha=Sha(item.Name+" diameter and subtype");var requirementSha=Sha(item.Name+" front projection circles");
    var identity=GeometryDocumentIdentity.FromSavedPath(path,hash);
    var refs=new List<GeometryRef>();
    GeometryRef Ref(string id,string? token,GeometryKind kind,Vector3 anchor,Vector3 direction,double? radius)=>new(){RefId=id,
        DocumentId=identity.DocumentId,DocumentPath=path,ModelSha256=hash,SourceRevisionId=revision,SourceFactIds=["hole"],
        EntityKind=EntityKind.Face,GeometryKind=kind,NativePersistentReference=token,
        Signature=new(){EntityKind=EntityKind.Face,GeometryKind=kind,AnchorMm=anchor,Direction=direction,RadiusMm=radius}};
    foreach(var cylinder in actual.Cylinders.Where(c=>c.Interior))
    {
        var center=ModelVerification.Scale(ModelVerification.Add(cylinder.AxisStartMm,cylinder.AxisEndMm),.5);
        var discovery=await executor.InspectAsync(new(path,Queries:[new(){Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,RadiusMm=cylinder.RadiusMm,
            PositionMm=new(center.X+cylinder.RadiusMm,center.Y,center.Z)}]));
        Save(item.Name+"-cylinder-"+refs.Count,discovery);
        if(discovery.Entities?.Count!=1)throw new InvalidOperationException("Ambiguous native cylinder discovery.");
        refs.Add(Ref("wall-"+refs.Count,discovery.Entities[0].PersistentReference,GeometryKind.Cylinder,center,cylinder.Direction,cylinder.RadiusMm));
    }
    foreach(var cone in actual.Cones)
        refs.Add(Ref("cone-"+refs.Count,cone.PersistentReference,GeometryKind.Cone,ModelVerification.Scale(ModelVerification.Add(cone.AxisStartMm,cone.AxisEndMm),.5),cone.Direction,null));
    var query=new ConnectivityInspectionQuery{QueryId="subtype-void",Requirement=new(){RequirementId="T08:hole",SourceFactId="hole",SourceRevisionId=revision,
        SourceSha256=sourceSha,SourceFactFingerprint=factSha,Kind=through?ConnectivityKind.ThroughHole:ConnectivityKind.BlindHole},GeometryRefs=refs};
    var topology=await executor.InspectAsync(new(path,SourceRevisionId:revision,ConnectivityQueries:[query]));Save(item.Name+"-topology",topology);
    Check(item.Name+" actual entire layered void passes T08",topology.ConnectivityChecks.Count==1&&topology.ConnectivityChecks[0].Status==ConnectivityStatus.Passed);
    var primitives=new List<ProjectionPrimitive>{new(){Id="drill",Kind=ProjectionPrimitiveKind.Circle,Center=new(0,0),RadiusMm=diameter/2,
        RequirementId="T09:hole",SourceFactId="hole",SourceFactFingerprint=factSha,RequirementFingerprint=requirementSha,LineStyle=through||item.Reverse?"visible":"hidden"}};
    // Front looks toward -Z: a large entrance at z=0 is behind the plate.
    if(item.Kind is HoleKind.Countersink or HoleKind.Counterbore)primitives.Add(primitives[0] with{Id="entrance",RadiusMm=6,LineStyle=item.Reverse?"visible":"hidden"});
    var corners=new[]{new ProjectionPointMm(-20,-15),new ProjectionPointMm(20,-15),new ProjectionPointMm(20,15),new ProjectionPointMm(-20,15)};
    for(var i=0;i<4;i++)primitives.Add(new(){Id="plate-"+i,Kind=ProjectionPrimitiveKind.Line,Start=corners[i],End=corners[(i+1)%4],
        RequirementId="T09:outline",SourceFactId="plate",SourceFactFingerprint=Sha("plate40x30"),RequirementFingerprint=Sha("rectangle40x30")});
    var source=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="model-xy-mm",SourceSha256=sourceSha,SourceRevisionId=revision,Primitives=primitives};
    var captured=await executor.CaptureProjectionAsync(new(){NativePath=path,SourceDrawingSha256=sourceSha,ViewId=source.ViewId,CoordinateFrameId=source.CoordinateFrameId,Orientation=ProjectionOrientation.Front});
    Save(item.Name+"-capture",captured);if(captured.Snapshot is null)throw new InvalidOperationException("Native projection capture failed.");
    var report=ProjectionVerifier.Compare(source,captured.Snapshot,new(){CompareVisibleOnly=false});Save(item.Name+"-projection",report);
    Check(item.Name+" T09 full native projection matches fixed source",report.Passed);
    var profile=new HoleGroupProfile{GroupId=item.Name,SourceSha256=sourceSha,SourceRevisionId=revision,SourceFactId="hole",SourceFactFingerprint=factSha,
        RequiredScopeFingerprint=Sha(item.Name+" frozen full subtype"),CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:hole",requirementSha}},
        ExpectedCenters=[new(0,0)],DiameterMm=diameter,HoleKind=item.Kind,ThroughAll=through,DepthMm=depth,ReverseDirection=item.Reverse,
        Frame=new(){OriginMm=new(0,0,item.Reverse?10:0)},CountersinkDiameterMm=12,CountersinkAngleDegrees=90,CounterboreDiameterMm=12,CounterboreDepthMm=1,
        ThreadMajorDiameterMm=8,ThreadDesignation="M8 x 1.25",ConnectivityCheckId="T08:hole",ProjectionCheckId="T09:hole"};
    var evidence=new Stage4EvidenceBundle{ConnectivityChecks=topology.ConnectivityChecks,ProjectionReports=[report]};
    var workflow=new Stage4SavedModelAcceptanceWorkflow(executor,new ArtifactCacheStore());
    var accepted=await workflow.VerifyHoleGroupAsync(path,profile,new(),evidence);Save(item.Name+"-accepted",accepted);
    Save(item.Name+"-profile",profile);Save(item.Name+"-evidence",evidence);
    Check(item.Name+" full T18 saved-model production gate",accepted.Passed);
    var wrongDirection=await workflow.VerifyHoleGroupAsync(path,profile with{ReverseDirection=!profile.ReverseDirection},new(),evidence);
    Save(item.Name+"-wrong-direction",wrongDirection);Check(item.Name+" rejects opposite entrance side",!wrongDirection.Passed);
    var wrongSize=await workflow.VerifyHoleGroupAsync(path,profile with{CountersinkDiameterMm=13,CounterboreDiameterMm=13,ThreadMajorDiameterMm=9},new(),evidence);
    Save(item.Name+"-wrong-size",wrongSize);Check(item.Name+" rejects wrong subtype size",!wrongSize.Passed);
    Check(item.Name+" final gate preserves original bytes",DrawingPlanValidation.FileHash(path)==hash);
}

// A real radial slot cuts through both end rings. Analytic cone support survives,
// but its trimmed wall is incomplete and must never certify an enclosed hole.
var slotPath=Path.Combine(output,"sink_slotted.SLDPRT");
var slotPlan=new ModelingPlan{PlanId="slotted-cone-"+Guid.NewGuid().ToString("N"),Name="sink_slotted",SourceText="Adversarial native fixture: radial slot in a countersunk hole",Output=new(){NativePath=slotPath},
    Operations=[new ProfileSketchOperation{Id="base",Name="base",Primitives=[new CenteredRectangleProfile{WidthMm=40,HeightMm=30}]},
        new ExtrudeBossOperation{Id="plate",Name="plate",SketchId="base",DependsOn=["base"],DepthMm=10},
        new NativeFeatureOperation{Id="hole",Name="hole",DependsOn=["plate"],Options=new(){Kind=NativeFeatureKind.Hole,HoleKind=HoleKind.Countersink,DiameterMm=6,
            ThroughAll=true,DepthMm=10,CountersinkDiameterMm=12,CountersinkAngleDegrees=90,HoleCenters=[new(0,0)]}},
        new ProfileSketchOperation{Id="slot",Name="slot",DependsOn=["hole"],Primitives=[new CenteredRectangleProfile{CenterXmm=6,WidthMm=12,HeightMm=1}]},
        new ExtrudeCutOperation{Id="radial_cut",Name="radial_cut",SketchId="slot",DependsOn=["slot"],DepthMm=10,EndCondition=ExtrudeEndCondition.ThroughAll}]};
Save("slotted-plan",slotPlan);var slotBuild=await executor.ExecuteAsync(slotPlan,false);Save("slotted-build",slotBuild);
Check("slotted cone adversarial native build",slotBuild.Success);
if(slotBuild.Success)
{
    var slotHash=DrawingPlanValidation.FileHash(slotPath);var slotInspection=await executor.InspectAsync(new(slotPath));Save("slotted-inspection",slotInspection);
    Check("slotted cone retains analytic support but fails complete-wall topology",slotInspection.Success&&slotInspection.Cones.Count>0&&slotInspection.Cones.All(c=>!c.CompleteWall));
    var cone=slotInspection.Cones.First();var doc=GeometryDocumentIdentity.FromSavedPath(slotPath,slotHash);
    var reference=new GeometryRef{RefId="slotted-cone",DocumentId=doc.DocumentId,DocumentPath=slotPath,ModelSha256=slotHash,SourceRevisionId="negative-v1",SourceFactIds=["hole"],
        EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cone,NativePersistentReference=cone.PersistentReference,
        Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cone,Direction=cone.Direction,
            AnchorMm=ModelVerification.Scale(ModelVerification.Add(cone.AxisStartMm,cone.AxisEndMm),.5)}};
    var query=new ConnectivityInspectionQuery{QueryId="slotted-void",Requirement=new(){RequirementId="T08:slotted",SourceFactId="hole",SourceRevisionId="negative-v1",
        SourceSha256=Sha("negative-slotted-cone"),SourceFactFingerprint=Sha("enclosed cone"),Kind=ConnectivityKind.ThroughHole},GeometryRefs=[reference]};
    var topology=await executor.InspectAsync(new(slotPath,SourceRevisionId:"negative-v1",ConnectivityQueries:[query]));Save("slotted-topology",topology);
    Check("actual incomplete conical wall cannot pass T08",topology.ConnectivityChecks.Count==1&&topology.ConnectivityChecks[0].Status!=ConnectivityStatus.Passed);
    Check("slotted negative inspection preserves native bytes",DrawingPlanValidation.FileHash(slotPath)==slotHash);
}
