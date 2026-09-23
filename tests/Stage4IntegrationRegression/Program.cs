using System.Reflection;
using System.Text;
using CadModeling.Core;
using CadModeling.Ir;

var count=0;
void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}

// Diagnostic timing must preserve execution, exceptions, identities and independent async parents.
var previousTraceDirectory=Environment.GetEnvironmentVariable(PerformanceTrace.DirectoryVariable);
var traceTestDirectory=Path.Combine(Path.GetTempPath(),"AutoSolidWorks-timing-tests",Guid.NewGuid().ToString("N"));
try
{
    Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,null);
    var failures=PerformanceTrace.WriteFailures;
    Check(PerformanceTrace.Measure("disabled",()=>42)==42&&PerformanceTrace.WriteFailures==failures,
        "T21 disabled timing preserves result and performs no diagnostic writes");
    Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,traceTestDirectory);
    using(PerformanceTrace.Begin("test.parent"))
    {
        await Task.Yield();
        using var child=PerformanceTrace.Begin("test.child");
        child.Dispose(); // IDisposable remains idempotent.
    }
    var sentinel=new InvalidOperationException("original exception");
    Exception? observed=null;
    try { PerformanceTrace.Measure<int>("test.exception",()=>throw sentinel); }
    catch(InvalidOperationException ex){observed=ex;}
    Check(ReferenceEquals(sentinel,observed),"T21 tracing preserves the original thrown exception");
    await Task.WhenAll(Enumerable.Range(0,8).Select(i=>Task.Run(async ()=>
    {
        using var outer=PerformanceTrace.Begin("parallel."+i);
        await Task.Yield();
        using var inner=PerformanceTrace.Begin("child."+i);
    })));
    var traceLines=Directory.GetFiles(traceTestDirectory,"*.jsonl").SelectMany(File.ReadAllLines)
        .Select(line=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(line)).ToArray();
    var parentTrace=traceLines.Single(x=>x.GetProperty("stage").GetString()=="test.parent");
    var childTrace=traceLines.Single(x=>x.GetProperty("stage").GetString()=="test.child");
    Check(traceLines.Length==19&&childTrace.GetProperty("parent_span_id").GetInt64()==parentTrace.GetProperty("span_id").GetInt64(),
        "T21 nested spans retain async parent and duplicate dispose does not double count");
    Check(traceLines.All(x=>x.GetProperty("elapsed_ms").GetDouble()>=0&&x.GetProperty("ended_ticks").GetInt64()>=x.GetProperty("started_ticks").GetInt64()&&x.GetProperty("process_id").GetInt32()==Environment.ProcessId),
        "T21 timing uses monotonic nonnegative duration and actual process identity");
    Check(Enumerable.Range(0,8).All(i=>
        traceLines.Single(x=>x.GetProperty("stage").GetString()=="child."+i).GetProperty("parent_span_id").GetInt64()==
        traceLines.Single(x=>x.GetProperty("stage").GetString()=="parallel."+i).GetProperty("span_id").GetInt64()),
        "T21 concurrent scopes do not exchange parent identities");
    var blockedDestination=Path.Combine(traceTestDirectory,"existing-file");
    File.WriteAllText(blockedDestination,"keep");
    Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,blockedDestination);
    Check(PerformanceTrace.Measure("unwritable",()=>17)==17&&PerformanceTrace.WriteFailures==failures+1&&File.ReadAllText(blockedDestination)=="keep",
        "T21 unavailable sink preserves execution and existing bytes while reporting dropped evidence");
    Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,"relative-path-disallowed");
    Check(PerformanceTrace.Measure("invalid",()=>18)==18&&PerformanceTrace.WriteFailures==failures+2,
        "T21 relative timing destination is rejected without changing operation outcome");
    Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,null);
    var unchangedDraft=new GenericModelDraft{Name="timing-invalid-draft",SourceText="fixed diagnostic fixture",Operations=[]};
    var withoutTrace=new GenericPlanCompiler().Compile(unchangedDraft);
    Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,traceTestDirectory);
    var withTrace=new GenericPlanCompiler().Compile(unchangedDraft);
    Check(System.Text.Json.JsonSerializer.Serialize(withoutTrace,ModelingIrJson.Options)==System.Text.Json.JsonSerializer.Serialize(withTrace,ModelingIrJson.Options),
        "T21 timing does not change compiler diagnostics or serialized contract");
}
finally { Environment.SetEnvironmentVariable(PerformanceTrace.DirectoryVariable,previousTraceDirectory); }

ProjectionDisplayPolylineRecord EdgeOn(params double[] angles)
{
    double[] Point(double degrees)=>[.003*Math.Cos(degrees*Math.PI/180),0,.003*Math.Sin(degrees*Math.PI/180)];
    return new(){Type=1,GeometryData=[0,0,0,..Point(angles[0]),..Point(angles[^1]),0,1,0],PointsXyz=angles.SelectMany(Point).ToArray()};
}
bool RejectArc(ProjectionDisplayPolylineRecord record){try{EdgeOnCircularProjection.Project(record);return false;}catch(InvalidDataException){return true;}}
var quarter=EdgeOnCircularProjection.Project(EdgeOn(0,30,60,90));
Check(Math.Abs(quarter.StartMeters.X)<1e-10&&Math.Abs(quarter.EndMeters.X-.003)<1e-10,"T09 edge-on quarter arc projects to its actual radius span, not a full diameter");
var interiorExtremum=EdgeOnCircularProjection.Project(EdgeOn(-60,-20,20,60));
Check(Math.Abs(interiorExtremum.StartMeters.X-.0015)<1e-10&&Math.Abs(interiorExtremum.EndMeters.X-.003)<1e-10,"T09 trimmed arc projection uses analytic interior extrema rather than endpoint or polyline maxima");
var reverseArc=EdgeOnCircularProjection.Project(EdgeOn(90,60,30,0));
Check(Math.Abs(reverseArc.StartMeters.X-quarter.StartMeters.X)<1e-10&&Math.Abs(reverseArc.EndMeters.X-quarter.EndMeters.X)<1e-10,"T09 reversed native arc traversal preserves projected geometry");
var majorArc=EdgeOnCircularProjection.Project(EdgeOn(60,120,180,240,300));
Check(Math.Abs(majorArc.StartMeters.X+.003)<1e-10&&Math.Abs(majorArc.EndMeters.X-.0015)<1e-10,"T09 major edge-on arc keeps its actual signed trim and extrema");
var semicircle=EdgeOnCircularProjection.Project(EdgeOn(0,180));
Check(Math.Abs(Math.Abs(semicircle.EndMeters.X-semicircle.StartMeters.X)-.006)<1e-10,"T09 antipodal projected extrema still certify a complete diameter segment");
Check(RejectArc(EdgeOn(0,90)),"T09 non-antipodal arc without an interior display witness remains unsupported");
var badArc=EdgeOn(0,30,60,90);
Check(RejectArc(badArc with{PointsXyz=badArc.PointsXyz.Skip(3).ToArray()}),"T09 partially clipped display cannot impersonate the full analytic trim");
Check(RejectArc(badArc with{GeometryData=[..badArc.GeometryData.Take(9),0,1,.1]}),"T09 oblique circle remains an unsupported ellipse");
Check(RejectArc(badArc with{PointsXyz=badArc.PointsXyz.Select((x,i)=>i==4?.001:x).ToArray()}),"T09 display points outside the actual circle plane cannot certify an edge-on arc");
Check(RejectArc(EdgeOn(0,60,30,90)),"T09 display angle backtracking cannot establish a unique native trim");

// Real production IPC server/client with a fake executor boundary. No SolidWorks COM calls.
var pipeName="stage4-pause-"+Guid.NewGuid().ToString("N");
var executor=DispatchProxy.Create<IModelingExecutor,BoundaryProxy>();
var boundary=(BoundaryProxy)(object)executor;
using var shutdown=new CancellationTokenSource();
using var userCancel=new CancellationTokenSource();
var executorAssembly=Assembly.Load("CadModeling.Executor.SolidWorks");
var serverType=executorAssembly.GetType("ExecutorPipeServer",true)!;
var server=Activator.CreateInstance(serverType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[pipeName,executor],null)!;
var serverTask=(Task)serverType.GetMethod("RunAsync")!.Invoke(server,[shutdown.Token])!;
var client=new NamedPipeModelingExecutor(pipeName);
var pending=client.ExecuteAsync(new ModelingPlan{PlanId="stage4-pause",Name="synthetic",SourceText="IPC fixture",Operations=[]},false,userCancel.Token);
await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
userCancel.Cancel();
var pauseResult=await pending.WaitAsync(TimeSpan.FromSeconds(5));
Check(pauseResult.Status=="pause_requested","T15 production IPC returns pause_requested instead of transport failure after caller cancellation");
Check(boundary.ObservedToken.IsCancellationRequested,"T15 production IPC propagates caller pause into the server request CTS");
Check(boundary.ObservedToken!=shutdown.Token,"T15 executor receives request-level CTS instead of the server shutdown token");
var requestId=pauseResult.Evidence.Select(e=>e.Data).OfType<IReadOnlyDictionary<string,string>>()
    .SelectMany(d=>d).First(kv=>kv.Key=="request_id").Value;
boundary.Complete.TrySetResult(new ExecutionResult(false,"paused","Fake COM boundary reached; no COM was called.",[]));
ExecutorServiceResponse status=new(Pending:true);
for(var i=0;i<40&&status.Pending;i++)
{
    await Task.Delay(25);
    status=await client.GetExecutionStatusAsync(requestId);
}
Check(status.Execution?.Status=="paused","T15 paused execution result is persisted and queryable by request id after reconnect");
shutdown.Cancel();
await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

// Environment identity must be implementation-content based, not AssemblyVersion based.
var compilerFingerprint=ComponentContentIdentity.ForType(typeof(GenericPlanCompiler),"generic-plan-compiler/v1");
var verifierFingerprint=ComponentContentIdentity.ForType(typeof(ModelVerification),"model-verification/v1");
Check(compilerFingerprint.Length==64&&compilerFingerprint.All(Uri.IsHexDigit),"T15 compiler environment identity is a SHA-256 content fingerprint");
Check(verifierFingerprint.Length==64&&verifierFingerprint.All(Uri.IsHexDigit)&&compilerFingerprint!=verifierFingerprint,
    "T15 compiler/verifier identities are role-bound even when they share the same fixed AssemblyVersion");
var currentExecutorPath=Assembly.Load("CadModeling.Executor.SolidWorks").Location;
var packageExecutorPath=Path.GetFullPath("package/runtime/executor/CadModeling.Executor.SolidWorks.dll");
if(File.Exists(packageExecutorPath))
{
    var currentVersion=AssemblyName.GetAssemblyName(currentExecutorPath).Version;
    var packageVersion=AssemblyName.GetAssemblyName(packageExecutorPath).Version;
    Check(currentVersion==packageVersion&&ComponentContentIdentity.ForFile(currentExecutorPath,"solidworks-executor/v1")!=ComponentContentIdentity.ForFile(packageExecutorPath,"solidworks-executor/v1"),
        "T15 equal fixed AssemblyVersion does not hide different current/package executor bytes");
}

// Production T16/T17 consumer: saved-model inspection -> cached artifact -> family observation -> final gate.
var savedPath=Path.Combine(Path.GetTempPath(),"stage4-saved-"+Guid.NewGuid().ToString("N")+".SLDPRT");
File.WriteAllBytes(savedPath,Encoding.UTF8.GetBytes("synthetic saved native bytes; fake inspector boundary only"));
var modelSha=DrawingPlanValidation.FileHash(savedPath);var sourceSha=new string('A',64);var scope=new string('B',64);var reqFp=new string('C',64);
var inspectionExecutor=new InspectionExecutor();
inspectionExecutor.Result=new ModelInspection(true,"synthetic saved-model inspection",savedPath,
    new GeometrySnapshot(1,4,8,1000,100,new SpatialPoint(0,0,0),new BoundingBoxSpec(60,30,30)),
    [new ModelFeatureInfo("Revolve1","Revolution",false,"persist",[
        new ModelDimension("D1@Revolve1",.03,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=30},
        new ModelDimension("D2@Revolve1",.02,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=20},
        new ModelDimension("D3@Revolve1",.02,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=20},
        new ModelDimension("D4@Revolve1",.04,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=40}])],[])
{
    ModelSha256=modelSha,ModelReopened=true,CaptureComplete=true,RebuildSucceeded=true,FeatureTreeFingerprint=new string('D',64),
    Cylinders=[new(new(0,0,0),new(20,0,0),new(1,0,0),15,2*Math.PI*15*20,false,"Revolve1"),
        new(new(20,0,0),new(60,0,0),new(1,0,0),10,2*Math.PI*10*40,false,"Revolve1")],
    EditabilityProbes=[
        new("s1-diameter","D1@Revolve1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Revolve1",Unit=DrawingValueUnit.Millimeter,RequestedTrialValue=31,
            OriginalSystemValue=.03,TrialSystemValue=.031,TrialReadbackSystemValue=.031,RestoreReadbackSystemValue=.03,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true},
        new("s1-length","D2@Revolve1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Revolve1",Unit=DrawingValueUnit.Millimeter,RequestedTrialValue=21,
            OriginalSystemValue=.02,TrialSystemValue=.021,TrialReadbackSystemValue=.021,RestoreReadbackSystemValue=.02,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true},
        new("s2-diameter","D3@Revolve1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Revolve1",Unit=DrawingValueUnit.Millimeter,RequestedTrialValue=21,
            OriginalSystemValue=.02,TrialSystemValue=.021,TrialReadbackSystemValue=.021,RestoreReadbackSystemValue=.02,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true},
        new("s2-length","D4@Revolve1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Revolve1",Unit=DrawingValueUnit.Millimeter,RequestedTrialValue=41,
            OriginalSystemValue=.04,TrialSystemValue=.041,TrialReadbackSystemValue=.041,RestoreReadbackSystemValue=.04,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true}]
};
var stage4Workflow=new Stage4SavedModelAcceptanceWorkflow(inspectionExecutor,new ArtifactCacheStore());
var profile=new RevolvedFamilyProfile
{
    ProfileId="runtime-shaft",SourceSha256=sourceSha,SourceRevisionId="rev-runtime",RequiredScopeFingerprint=scope,
    AxisReference=new(){RefId="axis",DocumentId="doc",ModelSha256=modelSha,EntityKind=EntityKind.Axis,GeometryKind=GeometryKind.Line,SourceFactIds=["axis-fact"],SourceRevisionId="rev-runtime",Signature=new(){EntityKind=EntityKind.Axis,GeometryKind=GeometryKind.Line,AnchorMm=new(0,0,0),Direction=new(1,0,0)}},
    CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:front",reqFp}},EditabilityProbes=[
        new("s1-diameter","D1@Revolve1",31),new("s1-length","D2@Revolve1",21),new("s2-diameter","D3@Revolve1",21),new("s2-length","D4@Revolve1",41)],
    Steps=[new(){SourceFactId="s1",AxialStartMm=0,AxialLengthMm=20,RadialValueMm=30},new(){SourceFactId="s2",AxialStartMm=20,AxialLengthMm=40,RadialValueMm=20}],RequiredViewCheckIds=["T09:front"]
};
var revolvedBinding=new RevolvedFamilyInspectionBinding{FeatureName="Revolve1",DrivingDimensions=[
    new(){SourceFactId="s1",Parameter=RevolvedDrivingParameterKind.Diameter,DimensionName="D1@Revolve1"},
    new(){SourceFactId="s1",Parameter=RevolvedDrivingParameterKind.AxialLength,DimensionName="D2@Revolve1"},
    new(){SourceFactId="s2",Parameter=RevolvedDrivingParameterKind.Diameter,DimensionName="D3@Revolve1"},
    new(){SourceFactId="s2",Parameter=RevolvedDrivingParameterKind.AxialLength,DimensionName="D4@Revolve1"}]};
var projection=new ProjectionReport{SourceViewId="front",SourceSha256=sourceSha,SourceRevisionId="rev-runtime",ActualModelSha256=modelSha,CoordinateFrameId="frame",ModelReopened=true,Passed=true,
    RequiredPrimitiveCount=1,CheckedRequiredPrimitiveCount=1,Requirements=[new(){RequirementId="T09:front",SourceFactId="view-fact",SourceFactFingerprint=new string('E',64),RequirementFingerprint=reqFp,Status=ProjectionRequirementStatus.Passed}]};
var familyEvidence=new Stage4EvidenceBundle{ProjectionReports=[projection]};
var firstFamily=await stage4Workflow.VerifyRevolvedAsync(savedPath,profile,revolvedBinding,familyEvidence);
var secondFamily=await stage4Workflow.VerifyRevolvedAsync(savedPath,profile,revolvedBinding,familyEvidence);
// Real Revolve features own the angular dimension; profile dimensions belong to
// their direct native parent sketch. The explicit binding preserves R/diameter.
double ProfileScale(string name)=>name.StartsWith("D1@",StringComparison.Ordinal)||name.StartsWith("D3@",StringComparison.Ordinal)?.5:1;
string ProfileName(string name)=>name.Replace("@Revolve1","@Section",StringComparison.Ordinal);
var parentFeature=firstFamily.Inspection.Features![0];
var sketchDimensions=parentFeature.Dimensions.Select(d=>d with{Name=ProfileName(d.Name),SystemValue=d.SystemValue*ProfileScale(d.Name),Value=d.Value*ProfileScale(d.Name)}).ToArray();
var parentInspection=firstFamily.Inspection with{Features=[parentFeature with{Dimensions=[],ParentFeatureNames=["Section"]},new("Section","ProfileFeature",false,"sketch-token",sketchDimensions)],
    EditabilityProbes=firstFamily.Inspection.EditabilityProbes.Select(p=>p with{DimensionName=ProfileName(p.DimensionName),FeatureName="Section",
        RequestedTrialValue=p.RequestedTrialValue*ProfileScale(p.DimensionName),OriginalSystemValue=p.OriginalSystemValue*ProfileScale(p.DimensionName),
        TrialSystemValue=p.TrialSystemValue*ProfileScale(p.DimensionName),TrialReadbackSystemValue=p.TrialReadbackSystemValue*ProfileScale(p.DimensionName),RestoreReadbackSystemValue=p.RestoreReadbackSystemValue*ProfileScale(p.DimensionName)}).ToArray()};
var parentProfile=profile with{EditabilityProbes=profile.EditabilityProbes.Select(p=>p with{DimensionName=ProfileName(p.DimensionName),FeatureName="Section",TrialValue=p.TrialValue*ProfileScale(p.DimensionName)}).ToArray()};
var parentBinding=revolvedBinding with{DrivingDimensions=revolvedBinding.DrivingDimensions.Select(d=>d with{OwnerFeatureName="Section",DimensionName=ProfileName(d.DimensionName),
    NativeRadialSemantic=d.Parameter==RevolvedDrivingParameterKind.Diameter?RadialDimensionSemantic.Radius:RadialDimensionSemantic.Diameter}).ToArray()};
async Task<bool> AcceptParent(ModelInspection captured,RevolvedFamilyInspectionBinding? mapping=null)=>
    (await new Stage4SavedModelAcceptanceWorkflow(new InspectionExecutor{Result=captured},new ArtifactCacheStore()).VerifyRevolvedAsync(savedPath,parentProfile,mapping??parentBinding,familyEvidence)).Passed;
Check(await AcceptParent(parentInspection),"T17 producer-proven direct parent sketch supplies all independently bound dimensions with explicit radius semantics");
Check(!await AcceptParent(parentInspection with{Features=parentInspection.Features!.Select(f=>f with{ParentFeatureNames=[]}).ToArray()}),"T17 unrelated sketch with identical dimensions and valid probes cannot replace native parent evidence");
Check(!await AcceptParent(parentInspection with{Features=parentInspection.Features!.Select(f=>f.Name=="Section"?f with{Type="Boss"}:f).ToArray()}),"T17 ordinary Boss parent cannot masquerade as consumed profile sketch");
Check(!await AcceptParent(parentInspection,parentBinding with{DrivingDimensions=parentBinding.DrivingDimensions.Select(d=>d with{NativeRadialSemantic=RadialDimensionSemantic.Diameter}).ToArray()}),"T17 native radius cannot be silently treated as source diameter");
Check(firstFamily.Passed,"T17 production saved-model adapter derives actual revolved geometry and reaches the final family gate");
Check(secondFamily.Passed&&secondFamily.InspectionCacheHit&&inspectionExecutor.InspectCalls==1,"T16 production inspection cache reuses identical dependencies on the next request without re-reading the model");

// T18 product consumer: actual saved cylinders + native pattern dimensions + producer-owned T08/T09 results.
var connFp=new string('1',64);var holeViewFp=new string('2',64);var holeScope=new string('3',64);var holeFactFp=new string('5',64);
var centers=new[]{new ProfilePoint(0,0),new ProfilePoint(10,0),new ProfilePoint(20,0),new ProfilePoint(30,0)};
inspectionExecutor.Result=new ModelInspection(true,"synthetic hole-group inspection",savedPath,null,
    [new ModelFeatureInfo("Pattern1","LPattern",false,"pattern-ref",[
        new ModelDimension("D1@Pattern1",4,"Integer"){ParameterType="Integer",Unit="Unitless",Value=4},
        new ModelDimension("D2@Pattern1",.01,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=10}])],[])
{
    ModelSha256=modelSha,ModelReopened=true,CaptureComplete=true,RebuildSucceeded=true,FeatureTreeFingerprint=new string('4',64),
    Cylinders=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,0),new(p.Xmm,p.Ymm,10),new(0,0,1),2.5,2*Math.PI*2.5*10,true,"Hole")).ToArray(),
    EditabilityProbes=[
        new("pattern-count","D1@Pattern1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Pattern1",Unit=DrawingValueUnit.Unitless,RequestedTrialValue=3,
            OriginalSystemValue=4,TrialSystemValue=3,TrialReadbackSystemValue=3,RestoreReadbackSystemValue=4,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true},
        new("pattern-spacing","D2@Pattern1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Pattern1",Unit=DrawingValueUnit.Millimeter,RequestedTrialValue=12,
            OriginalSystemValue=.01,TrialSystemValue=.012,TrialReadbackSystemValue=.012,RestoreReadbackSystemValue=.01,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true}]
};
var goodHoleInspection=inspectionExecutor.Result;
var holeProfile=new HoleGroupProfile{GroupId="runtime-holes",SourceSha256=sourceSha,SourceRevisionId="rev-runtime",SourceFactId="holes-fact",SourceFactFingerprint=holeFactFp,RequiredScopeFingerprint=holeScope,
    CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:holes",holeViewFp}},PatternEditabilityProbes=[new("pattern-count","D1@Pattern1",3,DrawingValueUnit.Unitless),new("pattern-spacing","D2@Pattern1",12)],
    PatternKind=HoleGroupPatternKind.Linear,HoleKind=HoleKind.Simple,ExpectedCenters=centers,DiameterMm=5,ThroughAll=true,PatternSpacingMm=10,
    ConnectivityCheckId="T08:holes",ProjectionCheckId="T09:holes"};
AxialOpeningEvidence OpenHoleEnd(string id)=>new(){EndId=id,SampleCount=3,VoidSampleCount=3,Complete=true,ProbeOffsetMm=.02,NumericalUncertaintyMm=.001,Method="bounded_multi_point_probe"};
ConnectivityCheck ProduceHoleConnectivity(ProfilePoint center,int index,double worldStart=0,double worldEnd=10,ConnectivityKind kind=ConnectivityKind.ThroughHole,bool reverseBlind=false,bool reverseAxis=false,double? producerOriginWorldZ=null)
{
    var requirement=new ConnectivityRequirement{RequirementId=$"T08:holes:{index}",SourceFactId="holes-fact",SourceRevisionId="rev-runtime",SourceSha256=sourceSha,SourceFactFingerprint=holeFactFp,Kind=kind};
    var geometryRef=new GeometryRef{RefId=$"hole-scope-{index}",DocumentId="doc",ModelSha256=modelSha,EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,SourceFactIds=["holes-fact"],SourceRevisionId="rev-runtime",
        Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,AnchorMm=new(center.Xmm,center.Ymm,(worldStart+worldEnd)/2),Direction=reverseAxis?new(0,0,-1):new(0,0,1),RadiusMm=2.5}};
    var resolution=new GeometryRefResolution{Reference=geometryRef,Status=GeometryRefResolutionStatus.Resolved,ResolvedModelSha256=modelSha,CandidateIds=[$"hole-{index}"],
        Candidate=new(){CandidateId=$"hole-{index}",NativePersistentReference=$"hole-ref-{index}",Signature=geometryRef.Signature}};
    var open=OpenHoleEnd("open");
    var closed=new AxialOpeningEvidence{EndId="closed",SampleCount=3,MaterialSampleCount=3,Complete=true,ProbeOffsetMm=.02,MaterialThicknessBeyondMm=1,NumericalUncertaintyMm=.001,Method="bounded_multi_point_probe"};
    var axis=reverseAxis?new Vector3(0,0,-1):new Vector3(0,0,1);var originZ=producerOriginWorldZ??(reverseAxis?worldEnd:worldStart);var axisOrigin=new Vector3(center.Xmm,center.Ymm,originZ);
    var stationA=(worldStart-originZ)*axis.Z;var stationB=(worldEnd-originZ)*axis.Z;var localStart=Math.Min(stationA,stationB);var localEnd=Math.Max(stationA,stationB);
    var observation=new ConnectivityObservation{CheckId=requirement.RequirementId,ActualModelSha256=modelSha,ModelReopened=true,GeometryResolutions=[resolution],AxisOriginMm=axisOrigin,Axis=axis,AxialInterval=new(localStart,localEnd),
        AxialSegmentDetails=[new(){SegmentId=$"wall-{index}",StartMm=localStart,EndMm=localEnd,RadiusMm=2.5,BodyId="body-1",LateralBoundaryExcluded=true}],
        PassageEvidence=new(){SampleCount=3,Complete=true,CoverageStartMm=localStart,CoverageEndMm=localEnd,BodyScopeComplete=true,LateralOutletExcluded=true,BodyIds=["body-1"],NumericalUncertaintyMm=.001,Method="bounded_axial_passage_probe"},
        StartEvidence=kind==ConnectivityKind.BlindHole&&reverseBlind?closed:open,EndEvidence=kind==ConnectivityKind.BlindHole&&!reverseBlind?closed:open,Complete=true,Method="synthetic_brep_evidence"};
    return ConnectivityVerifier.Evaluate(requirement,observation);
}
var connectivityChecks=centers.Select((center,index)=>ProduceHoleConnectivity(center,index)).ToArray();
var holeProjection=new ProjectionReport{SourceViewId="front",SourceSha256=sourceSha,SourceRevisionId="rev-runtime",ActualModelSha256=modelSha,CoordinateFrameId="frame",ModelReopened=true,Passed=true,
    RequiredPrimitiveCount=1,CheckedRequiredPrimitiveCount=1,Requirements=[new(){RequirementId="T09:holes",SourceFactId="holes-fact",SourceFactFingerprint=new string('6',64),RequirementFingerprint=holeViewFp,Status=ProjectionRequirementStatus.Passed}]};
var holeResult=await stage4Workflow.VerifyHoleGroupAsync(savedPath,holeProfile,new(){PatternFeatureName="Pattern1",PatternCountDimensionName="D1@Pattern1",PatternSpacingDimensionName="D2@Pattern1"},
    new(){ConnectivityChecks=connectivityChecks,ProjectionReports=[holeProjection]});
Check(holeResult.Passed,"T18 production saved-model adapter measures every hole instance and native pattern parameters before the final gate");
Check(System.Text.Json.JsonSerializer.Serialize(holeResult,ModelingIrJson.Options).Length>0,
    "T18 accepted linear pattern remains valid numeric JSON without an inapplicable angle NaN");

// T19 product consumer: actual native feature dimension + current-model T06 resolution; no caller-authored observation.
var edgeRef=new GeometryRef{RefId="edge-1",DocumentId="doc",ModelSha256=modelSha,EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,SourceFactIds=["fillet-fact"],SourceRevisionId="rev-runtime",
    Signature=new(){EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,AnchorMm=new(0,0,0),Direction=new(1,0,0)}};
var edgeResolution=new GeometryRefResolution{Reference=edgeRef,Status=GeometryRefResolutionStatus.Resolved,Candidate=new(){CandidateId="edge-current",NativePersistentReference="edge-persist",Signature=edgeRef.Signature},
    ResolvedModelSha256=modelSha,CandidateIds=["edge-current"],Message="current model edge"};
inspectionExecutor.Result=new ModelInspection(true,"synthetic fillet inspection",savedPath,null,
    [new ModelFeatureInfo("Fillet1","Fillet",false,"fillet-ref",[new ModelDimension("R@Fillet1",.003,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=3}])
        {DrivingEdgePersistentReferences=["edge-persist"]}],[])
{
    ModelSha256=modelSha,ModelReopened=true,CaptureComplete=true,RebuildSucceeded=true,FeatureTreeFingerprint=new string('7',64),GeometryRefResolutions=[edgeResolution],
    EditabilityProbes=[new("fillet-radius","R@Fillet1",true,"trial/restored"){ModelSha256=modelSha,FeatureName="Fillet1",Unit=DrawingValueUnit.Millimeter,RequestedTrialValue=4,
        OriginalSystemValue=.003,TrialSystemValue=.004,TrialReadbackSystemValue=.004,RestoreReadbackSystemValue=.003,TrialRebuildSucceeded=true,RestoreSucceeded=true,RestoreRebuildSucceeded=true,ModelFileUnchanged=true}]
};
var goodFilletInspection=inspectionExecutor.Result;
var inputEdge=edgeRef with{InputToFeature="Fillet1"};
Check(GeometryRefResolver.Fingerprint(inputEdge)!=GeometryRefResolver.Fingerprint(edgeRef),"T06 feature-input scope participates in immutable reference fingerprint");
Check(!GeometryRefResolver.Matches(inputEdge,edgeResolution.Candidate!),"T06 final B-Rep candidate cannot impersonate feature-input geometry");
var filletIntent=new EdgeTreatmentIntent{IntentId="runtime-fillet",SourceLiteral="R3",SourceSha256=sourceSha,SourceRevisionId="rev-runtime",Route=EdgeTreatmentRoute.SolidFillet,RadiusMm=3,
    TargetEdges=[edgeRef],TargetOperationId="fillet",EditabilityProbes=[new("fillet-radius","R@Fillet1",4)]};
var emptyStrategyRoundTrip=System.Text.Json.JsonSerializer.Deserialize<EdgeTreatmentIntent>(System.Text.Json.JsonSerializer.Serialize(filletIntent,ModelingIrJson.Options),ModelingIrJson.Options)!;
Check(emptyStrategyRoundTrip.AllowedRepairStrategies.Count==0,"T19 public JSON can deserialize an explicit empty allowed-repair collection");
var permitted=filletIntent with{AllowedRepairStrategies=new HashSet<EdgeTreatmentRepairStrategy>{EdgeTreatmentRepairStrategy.ReselectEdges}};
var nonemptyStrategyRoundTrip=System.Text.Json.JsonSerializer.Deserialize<EdgeTreatmentIntent>(System.Text.Json.JsonSerializer.Serialize(permitted,ModelingIrJson.Options),ModelingIrJson.Options)!;
Check(nonemptyStrategyRoundTrip.AllowedRepairStrategies.Count==1&&nonemptyStrategyRoundTrip.AllowedRepairStrategies.Contains(EdgeTreatmentRepairStrategy.ReselectEdges),
    "T19 nonempty repair authorization preserves exact membership through public JSON roundtrip");
var filletResult=await stage4Workflow.VerifyEdgeTreatmentAsync(savedPath,filletIntent,new(){FeatureName="Fillet1",PrimaryDimensionName="R@Fillet1"});
Check(filletResult.Passed,"T19 production saved-model adapter reads the native fillet radius and current-model edge resolution before acceptance");
Check(System.Text.Json.JsonSerializer.Serialize(filletResult,ModelingIrJson.Options).Length>0,
    "T19 accepted fillet remains valid numeric JSON without an inapplicable chamfer-distance NaN");
var unavailableDimension=await new Stage4SavedModelAcceptanceWorkflow(inspectionExecutor,new ArtifactCacheStore()).VerifyEdgeTreatmentAsync(savedPath,filletIntent,
    new(){FeatureName="Fillet1",PrimaryDimensionName="missing-native-dimension"});
Check(!unavailableDimension.Passed&&unavailableDimension.Observation is null&&System.Text.Json.JsonSerializer.Serialize(unavailableDimension,ModelingIrJson.Options).Length>0,
    "T19 missing required native measurement returns serializable unverifiable result without source substitution");

async Task<Stage4SavedModelAcceptance<HoleGroupObservation>> VerifyHole(ModelInspection captured,HoleGroupProfile? requested=null,Stage4EvidenceBundle? supplied=null)
{
    var fake=new InspectionExecutor{Result=captured};
    return await new Stage4SavedModelAcceptanceWorkflow(fake,new ArtifactCacheStore()).VerifyHoleGroupAsync(savedPath,requested??holeProfile,
        new(){PatternFeatureName="Pattern1",PatternCountDimensionName="D1@Pattern1",PatternSpacingDimensionName="D2@Pattern1"},
        supplied??new(){ConnectivityChecks=connectivityChecks,ProjectionReports=[holeProjection]});
}
async Task<Stage4SavedModelAcceptance<EdgeTreatmentObservation>> VerifyEdge(ModelInspection captured,EdgeTreatmentIntent? requested=null,Stage4RepairEvidenceBundle? repair=null)
{
    var fake=new InspectionExecutor{Result=captured};
    return await new Stage4SavedModelAcceptanceWorkflow(fake,new ArtifactCacheStore()).VerifyEdgeTreatmentAsync(savedPath,requested??filletIntent,
        new(){FeatureName="Fillet1",PrimaryDimensionName="R@Fillet1"},repair);
}
Check(!(await VerifyEdge(goodFilletInspection,filletIntent with{TargetEdges=[inputEdge]})).Passed,
    "T19 old final-scope resolution cannot be relabeled as a feature-input target");
var otherInput=inputEdge with{InputToFeature="OtherFillet"};
var otherResolution=edgeResolution with{Reference=otherInput,Candidate=edgeResolution.Candidate! with{InputToFeature="OtherFillet"}};
Check(!(await VerifyEdge(goodFilletInspection with{GeometryRefResolutions=[otherResolution]},filletIntent with{TargetEdges=[otherInput]})).Passed,
    "T19 input geometry from another native feature is rejected even with matching token");

// Review-2 regressions: producer-level failures, actual instance enumeration and actual native feature semantics.
var extraCylinder=new MeasuredCylinder(new(40,0,0),new(40,0,10),new(0,0,1),2.5,2*Math.PI*2.5*10,true,"Hole");
var sourceProjection=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="frame",SourceSha256=sourceSha,SourceRevisionId="rev-runtime",
    Primitives=centers.Select((c,n)=>new ProjectionPrimitive{Id="source-hole-"+n,Kind=ProjectionPrimitiveKind.Circle,Center=new(c.Xmm,c.Ymm),RadiusMm=2.5,
        RequirementId="T09:holes",SourceFactId="holes-fact",SourceFactFingerprint=new string('6',64),RequirementFingerprint=holeViewFp}).ToArray()};
var actualProjection=sourceProjection with{NativeModelPath=savedPath,NativeModelSha256=modelSha,NativeModelReopened=true,
    Primitives=[..sourceProjection.Primitives,new ProjectionPrimitive{Id="extra-hole",Kind=ProjectionPrimitiveKind.Circle,Center=new(40,0),RadiusMm=2.5}]};
var producerFailure=ProjectionVerifier.Compare(sourceProjection,actualProjection);
var extraResult=await VerifyHole(goodHoleInspection with{Cylinders=[..goodHoleInspection.Cylinders,extraCylinder]},
    supplied:new(){ConnectivityChecks=connectivityChecks,ProjectionReports=[producerFailure]});
Check(!producerFailure.Passed&&!extraResult.Passed&&extraResult.Observation!.Instances.Count==5,
    "T18 report-level T09 failure and independently enumerated extra hole both remain blocking");
Check(!(await VerifyHole(goodHoleInspection with{Features=goodHoleInspection.Features!.Select(f=>f with{Type="Boss"}).ToArray()})).Passed,
    "T18 ordinary Boss cannot masquerade as a native linear pattern");
Check(!(await VerifyHole(goodHoleInspection with{Features=goodHoleInspection.Features!.Select(f=>f with{Suppressed=true}).ToArray()})).Passed,
    "T18 suppressed native pattern cannot substantiate the current hole group");
var counterProfile=holeProfile with{HoleKind=HoleKind.Counterbore,CounterboreDiameterMm=8,CounterboreDepthMm=2};
var wrongDiameterExtra=await VerifyHole(goodHoleInspection with{Cylinders=[..goodHoleInspection.Cylinders,extraCylinder with{RadiusMm=3}]});
Check(!wrongDiameterExtra.Passed&&wrongDiameterExtra.Observation!.Instances.Count==5,
    "T18 extra actual hole with a different diameter cannot disappear through source-diameter filtering");
var sinkProfile=holeProfile with{HoleKind=HoleKind.Countersink,CountersinkDiameterMm=8,CountersinkAngleDegrees=90};
var sinkWalls=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,1.5),new(p.Xmm,p.Ymm,10),new(0,0,1),2.5,100,true,"Hole")).ToArray();
var sinkCones=centers.Select((p,i)=>new MeasuredCone(new(p.Xmm,p.Ymm,0),new(p.Xmm,p.Ymm,1.5),new(0,0,1),4,2.5,90,true,true,"cone-"+i,"Sink")).ToArray();
var sinkInspection=goodHoleInspection with{Cylinders=sinkWalls,Cones=sinkCones};
Check((await VerifyHole(sinkInspection,sinkProfile)).Passed,"T18 independently acquired complete cones connect each trimmed drill wall to its entrance");
Check(!(await VerifyHole(sinkInspection with{Cones=[]},sinkProfile)).Passed,"T18 missing cone acquisition cannot be filled from source dimensions");
Check(!(await VerifyHole(sinkInspection with{Cones=sinkCones.Select((c,i)=>i==2?c with{CompleteWall=false}:c).ToArray()},sinkProfile)).Passed,"T18 one incomplete conical wall rejects the entire group");
Check(!(await VerifyHole(sinkInspection with{Cones=[..sinkCones,sinkCones[0]]},sinkProfile)).Passed,"T18 duplicate cone candidates cannot silently choose a matching one");
Check(!(await VerifyHole(sinkInspection with{Cones=sinkCones.Select(c=>c with{IncludedAngleDegrees=82}).ToArray()},sinkProfile)).Passed,"T18 actual cone angle remains independent of source angle");
var threadProfile=holeProfile with{HoleKind=HoleKind.Tapped,ThreadMajorDiameterMm=6,ThreadDesignation="M6x1"};
var threads=centers.Select((p,i)=>new MeasuredCosmeticThread("Thread"+i,"edge"+i,new(p.Xmm,p.Ymm,0),new(0,0,1),2.5,6,"M6x1",true,0,true)).ToArray();
var threaded=goodHoleInspection with{CosmeticThreads=threads};
Check((await VerifyHole(threaded,threadProfile)).Passed,"T18 actual cosmetic threads bind individually to drill radius, center, axis and entrance");
Check(!(await VerifyHole(threaded with{CosmeticThreads=[]},threadProfile)).Passed,"T18 drill geometry alone cannot certify a tapped designation");
Check(!(await VerifyHole(threaded with{CosmeticThreads=threads.Select(t=>t with{EntranceCenterMm=t.EntranceCenterMm with{Z=10}}).ToArray()},threadProfile)).Passed,"T18 opposite-side cosmetic threads are rejected");
Check(!(await VerifyHole(threaded with{CosmeticThreads=threads.Select(t=>t with{Designation="M6x0.75"}).ToArray()},threadProfile)).Passed,"T18 actual thread designation mismatch is rejected");
Check(!(await VerifyHole(threaded with{CosmeticThreads=threads.Select(t=>t with{ThroughAll=false,BlindDepthMm=2}).ToArray()},threadProfile)).Passed,"T18 short blind cosmetic thread cannot certify a through thread");
Check(!(await VerifyHole(threaded with{CosmeticThreads=[..threads,threads[0]]},threadProfile)).Passed,"T18 ambiguous duplicate native threads are rejected");
Check(!(await VerifyHole(threaded with{CosmeticThreads=threads.Select(t=>t with{AxisDirection=new(1,0,0)}).ToArray()},threadProfile)).Passed,"T18 unrelated thread axis cannot certify the current hole");
var oppositeCounterbores=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,8),new(p.Xmm,p.Ymm,10),new(0,0,1),4,2*Math.PI*4*2,true,"Counterbore")).ToArray();
Check(!(await VerifyHole(goodHoleInspection with{Cylinders=[..goodHoleInspection.Cylinders,..oppositeCounterbores]},counterProfile)).Passed,
    "T18 counterbore layer on the opposite entrance side is derived from actual axial stations and rejected");

// Review-3: every hole needs its own producer-owned T08 scope; trimmed counterbore walls are contiguous, not overlapping.
var oneBlind=goodHoleInspection.Cylinders.ToArray();
oneBlind[2]=new(new(20,0,0),new(20,0,8),new(0,0,1),2.5,2*Math.PI*2.5*8,true,"Hole");
Check(!(await VerifyHole(goodHoleInspection with{Cylinders=oneBlind})).Passed,
    "T18 third hole left blind cannot borrow another instance's through-hole T08 evidence");
Check(!(await VerifyHole(goodHoleInspection,supplied:new(){ConnectivityChecks=connectivityChecks.Where((_,i)=>i!=2).ToArray(),ProjectionReports=[holeProjection]})).Passed,
    "T18 missing one per-instance T08 check leaves that hole unverifiable");
Check(!(await VerifyHole(goodHoleInspection,supplied:new(){ConnectivityChecks=[connectivityChecks[0],connectivityChecks[0],connectivityChecks[0],connectivityChecks[0]],ProjectionReports=[holeProjection]})).Passed,
    "T18 duplicated seed-hole T08 evidence cannot certify four distinct geometry scopes");
var frontCounterbores=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,0),new(p.Xmm,p.Ymm,2),new(0,0,1),4,2*Math.PI*4*2,true,"Counterbore")).ToArray();
var frontSmallWalls=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,2),new(p.Xmm,p.Ymm,10),new(0,0,1),2.5,2*Math.PI*2.5*8,true,"Hole")).ToArray();
Check((await VerifyHole(goodHoleInspection with{Cylinders=[..frontSmallWalls,..frontCounterbores]},counterProfile)).Passed,
    "T18 normal front counterbore accepts contiguous trimmed walls z=0..2 plus z=2..10");
var rearCounterbores=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,8),new(p.Xmm,p.Ymm,10),new(0,0,1),4,2*Math.PI*4*2,true,"Counterbore")).ToArray();
var rearSmallWalls=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,0),new(p.Xmm,p.Ymm,8),new(0,0,1),2.5,2*Math.PI*2.5*8,true,"Hole")).ToArray();
Check((await VerifyHole(goodHoleInspection with{Cylinders=[..rearSmallWalls,..rearCounterbores]},counterProfile with{ReverseDirection=true})).Passed,
    "T18 reverse-side counterbore accepts contiguous trimmed walls z=0..8 plus z=8..10");
Check(!(await VerifyHole(goodHoleInspection with{Cylinders=[..goodHoleInspection.Cylinders,..frontCounterbores]},counterProfile)).Passed,
    "T18 overlapping small/counterbore walls do not masquerade as a valid trimmed counterbore");
var blindProfile=holeProfile with{GroupId="runtime-blind-holes",ThroughAll=false,DepthMm=8,ReverseDirection=false};
var blindWalls=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,0),new(p.Xmm,p.Ymm,8),new(0,0,1),2.5,2*Math.PI*2.5*8,true,"Hole")).ToArray();
var blindChecks=centers.Select((center,index)=>ProduceHoleConnectivity(center,index,0,8,ConnectivityKind.BlindHole,false)).ToArray();
Check((await VerifyHole(goodHoleInspection with{Cylinders=blindWalls},blindProfile,new(){ConnectivityChecks=blindChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 four blind holes require and accept four independently scoped front-entry T08 checks");
var mixedBlindChecks=blindChecks.Select((check,index)=>index==2?ProduceHoleConnectivity(centers[index],index,0,8,ConnectivityKind.BlindHole,true):check).ToArray();
Check(!(await VerifyHole(goodHoleInspection with{Cylinders=blindWalls},blindProfile,new(){ConnectivityChecks=mixedBlindChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 third reverse blind hole is rejected even when the other three instance T08 checks match");
var blindCounterProfile=blindProfile with{GroupId="runtime-blind-counterbore",HoleKind=HoleKind.Counterbore,CounterboreDiameterMm=8,CounterboreDepthMm=2};
var blindCounterSmall=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,2),new(p.Xmm,p.Ymm,8),new(0,0,1),2.5,2*Math.PI*2.5*6,true,"Hole")).ToArray();
Check((await VerifyHole(goodHoleInspection with{Cylinders=[..blindCounterSmall,..frontCounterbores]},blindCounterProfile,
        new(){ConnectivityChecks=blindChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 blind counterbore depth is measured over the full contiguous z=0..8 channel, not only the z=2..8 small wall");

// Review-4: T08 stations are producer-local and must be transformed through the producer axis origin into the profile frame.
var shiftedWalls=centers.Select(p=>new MeasuredCylinder(new(p.Xmm,p.Ymm,20),new(p.Xmm,p.Ymm,30),new(0,0,1),2.5,2*Math.PI*2.5*10,true,"Hole")).ToArray();
var shiftedChecks=centers.Select((center,index)=>ProduceHoleConnectivity(center,index,20,30)).ToArray();
Check((await VerifyHole(goodHoleInspection with{Cylinders=shiftedWalls},supplied:new(){ConnectivityChecks=shiftedChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 producer-local 0..10 intervals at world z=20..30 are transformed through AxisOriginMm before profile comparison");
var displacedFrameProfile=holeProfile with{Frame=holeProfile.Frame with{OriginMm=new(0,0,5)}};
Check((await VerifyHole(goodHoleInspection,displacedFrameProfile)).Passed,
    "T18 nonzero profile-frame axial origin does not invalidate the same world-space hole topology");
var reverseAxisChecks=centers.Select((center,index)=>ProduceHoleConnectivity(center,index,0,10,reverseAxis:true)).ToArray();
Check((await VerifyHole(goodHoleInspection,supplied:new(){ConnectivityChecks=reverseAxisChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 reversed producer axis is normalized by model-space endpoints before profile comparison");
var counterFaceOrderChecks=centers.Select((center,index)=>ProduceHoleConnectivity(center,index,0,10,producerOriginWorldZ:2)).ToArray();
Check((await VerifyHole(goodHoleInspection with{Cylinders=[..frontSmallWalls,..frontCounterbores]},counterProfile,
        new(){ConnectivityChecks=counterFaceOrderChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 multi-layer counterbore remains valid when producer local zero starts at the z=2 small-wall face");
var shortChecks=centers.Select((center,index)=>ProduceHoleConnectivity(center,index,0,9)).ToArray();
Check(!(await VerifyHole(goodHoleInspection,supplied:new(){ConnectivityChecks=shortChecks,ProjectionReports=[holeProjection]})).Passed,
    "T18 a genuinely shorter T08 world interval is still rejected after coordinate normalization");
Check(!(await VerifyEdge(goodFilletInspection with{Features=goodFilletInspection.Features!.Select(f=>f with{Type="Boss"}).ToArray()})).Passed,
    "T19 ordinary Boss named like a fillet cannot inherit the source route");
Check(!(await VerifyEdge(goodFilletInspection with{Features=goodFilletInspection.Features!.Select(f=>f with{Suppressed=true}).ToArray()})).Passed,
    "T19 suppressed fillet cannot substantiate an applied edge treatment");
Check(!(await VerifyEdge(goodFilletInspection with{Features=goodFilletInspection.Features!.Select(f=>f with{DrivingEdgePersistentReferences=["different-edge"]}).ToArray()})).Passed,
    "T19 native fillet driving-edge selection must exactly match the current T06 target edge set");

Check(!(await VerifyEdge(goodFilletInspection with{EditabilityProbes=[goodFilletInspection.EditabilityProbes[0] with{ProbeId="other-probe",DimensionName="D1@Other"}]})).Passed,
    "T19 editability receipt must match requested ProbeId and DimensionName, not merely result count");
var unrelatedProbeSpec=new NativeEditabilityProbeSpec("unrelated-probe","D1@UnrelatedSketch",4);
var unrelatedProbeReceipt=goodFilletInspection.EditabilityProbes[0] with{ProbeId="unrelated-probe",DimensionName="D1@UnrelatedSketch",FeatureName="UnrelatedSketch"};
var unrelatedFeature=new ModelFeatureInfo("UnrelatedSketch","ProfileFeature",false,"unrelated",[new ModelDimension("D1@UnrelatedSketch",.004,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=4}]);
Check(!(await VerifyEdge(goodFilletInspection with{Features=[..goodFilletInspection.Features!,unrelatedFeature],EditabilityProbes=[unrelatedProbeReceipt]},
        filletIntent with{EditabilityProbes=[unrelatedProbeSpec]})).Passed,
    "T19 self-consistent probe on an unrelated sketch cannot certify Fillet1 editability");
var sameFeatureOther=new NativeEditabilityProbeSpec("fillet-other","D2@Fillet1",4);
var sameFeatureReceipt=goodFilletInspection.EditabilityProbes[0] with{ProbeId="fillet-other",DimensionName="D2@Fillet1",FeatureName="Fillet1"};
var filletWithOtherDimension=goodFilletInspection.Features!.Select(f=>f.Name=="Fillet1"?f with{Dimensions=[..f.Dimensions,new ModelDimension("D2@Fillet1",.004,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=4}]}:f).ToArray();
Check(!(await VerifyEdge(goodFilletInspection with{Features=filletWithOtherDimension,EditabilityProbes=[sameFeatureReceipt]},filletIntent with{EditabilityProbes=[sameFeatureOther]})).Passed,
    "T19 unrelated dimension on the same native fillet cannot replace the bound radius parameter probe");
var missingPatternDrive=holeProfile with{PatternEditabilityProbes=[holeProfile.PatternEditabilityProbes[0]]};
Check(!(await VerifyHole(goodHoleInspection with{EditabilityProbes=[goodHoleInspection.EditabilityProbes[0]]},missingPatternDrive)).Passed,
    "T18 native pattern editability requires probes for count and spacing, not an arbitrary subset");
var unrelatedRevolveProbe=new NativeEditabilityProbeSpec("other-drive","D1@UnrelatedSketch",32);
var unrelatedRevolveFeature=new ModelFeatureInfo("UnrelatedSketch","ProfileFeature",false,"other",[new ModelDimension("D1@UnrelatedSketch",.032,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=32}]);
var unrelatedRevolveReceipt=firstFamily.Inspection.EditabilityProbes[0] with{ProbeId="other-drive",DimensionName="D1@UnrelatedSketch",FeatureName="UnrelatedSketch",RequestedTrialValue=32};
var badRevolve=await new Stage4SavedModelAcceptanceWorkflow(new InspectionExecutor{Result=firstFamily.Inspection with{Features=[..firstFamily.Inspection.Features!,unrelatedRevolveFeature],EditabilityProbes=[unrelatedRevolveReceipt]}},new ArtifactCacheStore())
    .VerifyRevolvedAsync(savedPath,profile with{EditabilityProbes=[unrelatedRevolveProbe]},revolvedBinding,familyEvidence);
Check(!badRevolve.Passed,"T17 editability probe must belong to the actual revolve feature, not an unrelated sketch");
var sameRevolveOtherDimension=new ModelDimension("D5@Revolve1",.03,"Linear"){ParameterType="DoubleLinear",Unit="Millimeter",Value=30};
var sameRevolveFeatures=firstFamily.Inspection.Features!.Select(f=>f.Name=="Revolve1"?f with{Dimensions=[..f.Dimensions,sameRevolveOtherDimension]}:f).ToArray();
var substitutedSpecs=profile.EditabilityProbes.Select((p,i)=>i==0?new NativeEditabilityProbeSpec("other-revolve-param","D5@Revolve1",31):p).ToArray();
var substitutedReceipts=firstFamily.Inspection.EditabilityProbes.Select((r,i)=>i==0?r with{ProbeId="other-revolve-param",DimensionName="D5@Revolve1",FeatureName="Revolve1"}:r).ToArray();
var sameRevolveWrong=await new Stage4SavedModelAcceptanceWorkflow(new InspectionExecutor{Result=firstFamily.Inspection with{Features=sameRevolveFeatures,EditabilityProbes=substitutedReceipts}},new ArtifactCacheStore())
    .VerifyRevolvedAsync(savedPath,profile with{EditabilityProbes=substitutedSpecs},revolvedBinding,familyEvidence);
Check(!sameRevolveWrong.Passed,"T17 same-Revolve unrelated dimension cannot replace the independently bound source-step diameter drive");
var partialRevolve=await new Stage4SavedModelAcceptanceWorkflow(new InspectionExecutor{Result=firstFamily.Inspection with{EditabilityProbes=firstFamily.Inspection.EditabilityProbes.Take(3).ToArray()}},new ArtifactCacheStore())
    .VerifyRevolvedAsync(savedPath,profile with{EditabilityProbes=profile.EditabilityProbes.Take(3).ToArray()},revolvedBinding,familyEvidence);
Check(!partialRevolve.Passed,"T17 every independently bound diameter/axial-length drive must be probed; partial coverage cannot certify editability");
var incompleteBinding=revolvedBinding with{DrivingDimensions=revolvedBinding.DrivingDimensions.Take(3).ToArray()};
var incompleteBindingResult=await new Stage4SavedModelAcceptanceWorkflow(new InspectionExecutor{Result=firstFamily.Inspection},new ArtifactCacheStore())
    .VerifyRevolvedAsync(savedPath,profile,incompleteBinding,familyEvidence);
Check(!incompleteBindingResult.Passed,"T17 binding itself must cover diameter and axial length for every source step fact");
var badAttempt=new RepairAttempt{RequestId="bad-through-repair",Kind=RepairKind.ThroughDirection,Status=RepairAttemptStatus.Rejected,SourceSha256=sourceSha,SourceRevisionId="rev-runtime",
    FailureFingerprint=new string('8',64),BeforePlanFingerprint=new string('8',64),AfterPlanFingerprint=new string('9',64),BaselineModelSha256=modelSha,
    FrozenRequiredScopeFingerprint=scope,LockedRequirementsFingerprint=scope,TargetOperationId="fillet"};
var badExecution=new RepairExecutionEvidence{ExecutionId="bad-exec",AttemptRequestId="wrong-request",BeforePlanFingerprint=new string('8',64),AfterPlanFingerprint=new string('9',64),
    SourceSha256=sourceSha,SourceRevisionId="rev-runtime",BaselineModelSha256=modelSha,CandidateModelSha256=modelSha,FrozenRequiredScopeFingerprint=scope,EvidenceMode="synthetic_fixture"};
var badCoverage=new CoverageReport{SourceSha256=sourceSha,SourceRevisionId="rev-runtime",CandidateModelSha256=modelSha,CandidatePlanFingerprint=new string('9',64),RequiredScopeFingerprint=scope,Conclusion=CoverageConclusion.Failed};
var cleanDiff=new ModelDiff{BaselineModelSha256=modelSha,CandidateModelSha256=modelSha,Status=ModelDiffStatus.Comparable,BaselineModelReopened=true,CandidateModelReopened=true,
    BaselineCaptureComplete=true,CandidateCaptureComplete=true,CaptureScopeIds=["all"]};
var unsupportedRepair=await VerifyEdge(goodFilletInspection,repair:new(){Attempt=badAttempt,Execution=badExecution,Coverage=badCoverage,Diff=cleanDiff});
Check(!unsupportedRepair.Passed&&unsupportedRepair.Verification.Checks.Count(c=>c.Id=="T14-repair-gate")==1,
    "T19 any supplied repair bundle triggers T14 and an unsupported repair kind cannot map to no repair");

// A fully self-consistent development fixture must remain excluded at the native public workflow boundary.
var repairPlan=new ModelingPlan{PlanId="synthetic-repair",Name="synthetic repair",SourceText="R3",DrawingSourceSha256=sourceSha,
    Operations=[new NativeFeatureOperation{Id="Fillet1",Name="Fillet1",Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=3,Selections=[new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line}]}}]};
var preCoverage=new CoverageReport{SourceSha256=sourceSha,SourceRevisionId="rev-runtime",CandidateModelSha256=modelSha,
    CandidatePlanFingerprint=ModelingPlanIdentity.Fingerprint(repairPlan),RequiredScopeFingerprint=scope,RequiredFactIds=["fillet-fact"],
    Items=[new(){FactId="fillet-fact",Status=RequirementCheckStatus.Failed}],Conclusion=CoverageConclusion.Failed};
var repairLocation=FailureLocator.Locate(repairPlan,new(){FailureId="fixture-failure",FailureClass=FailureClass.NativeKernelFailure,SourceFactIds=["fillet-fact"],OperationIds=["Fillet1"],EvidenceIds=["fixture"]});
var prepared=new ConstrainedRepairSession().Prepare(repairPlan,new(){RequestId="fixture-repair",FailureId="fixture-failure",Kind=RepairKind.FilletSelection,SourceRevisionId="rev-runtime",
    FailureFingerprint=new string('8',64),NewEvidenceFingerprint=new string('9',64),TargetOperationId="Fillet1",GeometryResolutions=[edgeResolution]},preCoverage,repairLocation);
if(prepared.Status!=RepairAttemptStatus.Prepared)throw new InvalidOperationException(prepared.StopReason);
var fixtureReceipt=new RepairExecutionEvidence{ExecutionId="fixture-execution",AttemptRequestId=prepared.RequestId,AttemptIndex=prepared.AttemptIndex,
    BeforePlanFingerprint=prepared.BeforePlanFingerprint,AfterPlanFingerprint=prepared.AfterPlanFingerprint!,SourceSha256=sourceSha,SourceRevisionId="rev-runtime",
    BaselineModelSha256=modelSha,CandidateModelSha256=modelSha,CandidateModelReopened=true,FrozenRequiredScopeFingerprint=scope,FrozenRequiredFactIds=prepared.FrozenRequiredFactIds,
    FrozenAffectedFactIds=prepared.FrozenAffectedFactIds,EvidenceMode="synthetic_fixture"};
var fixtureCoverage=preCoverage with{CandidatePlanFingerprint=prepared.AfterPlanFingerprint,Conclusion=CoverageConclusion.Passed,Items=[new(){FactId="fillet-fact",Status=RequirementCheckStatus.Passed}]};
var fixtureChecks=new[]{new RepairCheckResult("unaffected-requirement-sample",RequirementCheckStatus.Passed,"fixture-sample",sourceSha,"rev-runtime",modelSha,prepared.AfterPlanFingerprint!,prepared.RequestId)};
var fixtureBundle=new Stage4RepairEvidenceBundle{Attempt=prepared,Execution=fixtureReceipt,Coverage=fixtureCoverage,Diff=cleanDiff,Checks=fixtureChecks};
Check(ConstrainedRepairSession.ValidatePostRepair(prepared,fixtureReceipt,fixtureCoverage,cleanDiff,fixtureChecks).Status==RepairValidationStatus.Passed,
    "T14 deterministic fixture remains valid for development contract tests");
var syntheticAtNativeGate=await VerifyEdge(goodFilletInspection,filletIntent with{TargetOperationId="Fillet1",AllowedRepairStrategies=[EdgeTreatmentRepairStrategy.ReselectEdges]},fixtureBundle);
Check(!syntheticAtNativeGate.Passed,"T19 native production workflow rejects a fully consistent synthetic repair receipt");

// JSON clients may parse an integral -0 token as integer 0. Signed zero is the
// same geometric coordinate, while source text and every nonzero value remain bound.
var negativeZero=BitConverter.Int64BitsToDouble(long.MinValue);
var signedZeroPlan=repairPlan with{Operations=[new NativeFeatureOperation{Id="Fillet1",Name="Fillet1",Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=3,
    Selections=[new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line,Direction=new(negativeZero,negativeZero,-1)}]}}]};
var clientNormalized=ModelingIrJson.Deserialize(ModelingIrJson.Serialize(signedZeroPlan).Replace(": -0,",": 0,",StringComparison.Ordinal));
Check(ModelingPlanIdentity.Fingerprint(signedZeroPlan)==ModelingPlanIdentity.Fingerprint(clientNormalized),
    "plan fingerprint preserves geometric identity across client negative-zero normalization");
Check(ModelingPlanIdentity.Fingerprint(clientNormalized)==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ModelingIrJson.Serialize(clientNormalized,false)))),
    "plans without negative zero retain their previous fingerprint bytes");
Check(ModelingRecovery.TypedPlanFingerprint(signedZeroPlan)==ModelingRecovery.TypedPlanFingerprint(clientNormalized)&&
      ModelingRecovery.PrefixFingerprint(signedZeroPlan,1)==ModelingRecovery.PrefixFingerprint(clientNormalized,1),
    "T15 typed-plan and verified-prefix identities use the same signed-zero canonicalization");
Check(ModelingPlanIdentity.Fingerprint(signedZeroPlan with{SourceText="-0"})!=ModelingPlanIdentity.Fingerprint(signedZeroPlan with{SourceText="0"}),
    "numeric zero canonicalization must not rewrite source literals");
Check(ModelingPlanIdentity.Fingerprint(signedZeroPlan with{Operations=[new ExtrudeBossOperation{Id="tiny",Name="tiny",SketchId="base",DepthMm=1e-99}]})!=
      ModelingPlanIdentity.Fingerprint(signedZeroPlan with{Operations=[new ExtrudeBossOperation{Id="tiny",Name="tiny",SketchId="base",DepthMm=0}]}),
    "numeric zero canonicalization must retain every nonzero value");
var signedCandidate=prepared.CandidatePlan! with{Operations=[new NativeFeatureOperation{Id="Fillet1",Name="Fillet1",Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=3,
    Selections=[new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line,Direction=new(negativeZero,negativeZero,-1)}]}}]};
var signedFingerprint=ModelingPlanIdentity.Fingerprint(signedCandidate);
var signedAttempt=prepared with{CandidatePlan=signedCandidate,AfterPlanFingerprint=signedFingerprint};
var signedReceipt=fixtureReceipt with{AfterPlanFingerprint=signedFingerprint};
var signedCoverage=fixtureCoverage with{CandidatePlanFingerprint=signedFingerprint};
var signedChecks=fixtureChecks.Select(c=>c with{PlanFingerprint=signedFingerprint}).ToArray();
var signedBundle=fixtureBundle with{Attempt=signedAttempt,Execution=signedReceipt,Coverage=signedCoverage,Checks=signedChecks};
var clientBundle=System.Text.Json.JsonSerializer.Deserialize<Stage4RepairEvidenceBundle>(
    System.Text.Json.JsonSerializer.Serialize(signedBundle,new System.Text.Json.JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true})
        .Replace(": -0,",": 0,",StringComparison.Ordinal),ModelingIrJson.Options)!;
Check(ConstrainedRepairSession.ValidatePostRepair(clientBundle.Attempt,clientBundle.Execution,clientBundle.Coverage,clientBundle.Diff,clientBundle.Checks).Status==RepairValidationStatus.Passed,
    "T14 complete receipt remains valid after a JSON client normalizes signed zero");

var axialCheck=ProduceHoleConnectivity(new(0,0),0) with{ObservedAxis=new(1,0,0),ObservedAxisOriginMm=new(0,0,0),ObservedAxialInterval=new(0,60)};
var axialProfile=parentProfile with{Kind=RevolvedFamilyKind.SteppedShaftWithAxialHole,AxialHoleConnectivityCheckId=axialCheck.RequirementId,
    CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:front",reqFp},{axialCheck.RequirementId,axialCheck.RequirementFingerprint}}};
async Task<bool> AcceptAxial(ConnectivityCheck captured)=>
    (await new Stage4SavedModelAcceptanceWorkflow(new InspectionExecutor{Result=parentInspection},new ArtifactCacheStore()).VerifyRevolvedAsync(savedPath,axialProfile,parentBinding,
        familyEvidence with{ConnectivityChecks=[captured]})).Passed;
Check(await AcceptAxial(axialCheck),"T17 axial-hole evidence requires the full current rotation-axis interval");
Check(!await AcceptAxial(axialCheck with{ObservedAxisOriginMm=new(0,5,0)}),"T17 another through-hole on a parallel displaced axis cannot certify the axial hole");
Check(!await AcceptAxial(axialCheck with{ObservedAxialInterval=new(0,59)}),"T17 shorter T08 interval cannot certify full-length axial through-hole");
var generationStore=new ArtifactCacheStore();generationStore.MarkCurrent("slot","new-revision",8);
var rollbackRejected=false;try{generationStore.MarkCurrent("slot","old-revision",7);}catch(InvalidOperationException){rollbackRejected=true;}
Check(rollbackRejected,"T16 current-slot generation is monotonic across source revisions");

var solidWorksExecutorType=executorAssembly.GetType("SolidWorksComExecutor",true)!;
var isolationMethod=solidWorksExecutorType.GetMethod("RequiresIsolatedNativeCopy",BindingFlags.NonPublic|BindingFlags.Static)!;
var driveProbe=new NativeEditabilityProbeSpec("drive","D1@Feature",4);
Check((bool)isolationMethod.Invoke(null,[".sldprt",new NativeEditabilityProbeSpec[]{driveProbe}])!&&
      !(bool)isolationMethod.Invoke(null,[".sldprt",Array.Empty<NativeEditabilityProbeSpec>()])!,
    "T17-T19 native editability probing selects an owned isolated copy before any drive");
var changesMethod=solidWorksExecutorType.GetMethod("EditabilityTrialChanges",BindingFlags.NonPublic|BindingFlags.Static)!;
var readbackMethod=solidWorksExecutorType.GetMethod("EditabilityReadbackMatches",BindingFlags.NonPublic|BindingFlags.Static)!;
Check(!(bool)changesMethod.Invoke(null,[.003,.003])!&&(bool)changesMethod.Invoke(null,[.003,.004])!,
    "T17-T19 editability probe rejects no-op trial values");
Check(!(bool)readbackMethod.Invoke(null,[.004,(double?).003])!&&(bool)readbackMethod.Invoke(null,[.004,(double?).004])!,
    "T17-T19 editability probe requires trial parameter readback to equal the requested drive");
Check(!(bool)readbackMethod.Invoke(null,[.003,(double?).004])!&&(bool)readbackMethod.Invoke(null,[.003,(double?).003])!,
    "T17-T19 editability probe requires restored parameter readback to equal the original value");

Console.WriteLine($"{count} stage4 integration checks passed.");

public class BoundaryProxy:DispatchProxy
{
    public TaskCompletionSource<bool> Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<ExecutionResult> Complete { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken ObservedToken { get; private set; }
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method?.Name!="ExecuteAsync")throw new NotSupportedException(method?.Name);
        ObservedToken=(CancellationToken)args![2]!;Started.TrySetResult(true);return Complete.Task;
    }
}

public sealed class InspectionExecutor:IModelingExecutor
{
    public ModelInspection Result { get; set; }=new(false,"not configured",string.Empty);
    public int InspectCalls { get; private set; }
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new ExecutorHealth(true,"fixture","available","34.0"));
    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken cancellationToken=default)=>
        Task.FromResult(new ExecutionResult(false,"unsupported","not used",[]));
    public Task<ModelInspection> InspectAsync(ModelInspectionRequest request,CancellationToken cancellationToken=default)
    {InspectCalls++;return Task.FromResult(Result with{InputPath=request.InputPath});}
}
