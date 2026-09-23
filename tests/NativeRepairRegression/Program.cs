using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length is <3 or >4||Path.GetFileName(args[2])!=args[2])throw new ArgumentException("Supply output root, exact executor executable, unused result directory; optional native path runs only projection capture.");
var output=Path.Combine(Path.GetFullPath(args[0]),args[2]);if(Directory.Exists(output))throw new IOException("Preserve old evidence; use a fresh directory.");
Directory.CreateDirectory(output);var json=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,json));
string Sha(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var exe=Path.GetFullPath(args[1]);var path=Path.Combine(output,"repair-work.SLDPRT");
const string revision="native-repair-source-v1";
var sourceSha=Sha("Development source:40x30x10 plate; four outer vertical edges R3; centered diameter4 through hole; no dimension relaxation.");
var outerFact=Sha("four-corner-R3");var holeFact=Sha("center-hole4");var outerRequirement=Sha("40x30-R3-outline");var holeRequirement=Sha("center-hole4-projection");
Save("manifest",new{executor=exe,executor_sha256=DrawingPlanValidation.FileHash(Path.ChangeExtension(exe,".dll")),sourceSha,
    scope="native controlled repair development fixture; independent drawing and installed acceptance not claimed"});
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="native-repair-"+Guid.NewGuid().ToString("N"),ExecutablePath=exe,LogDirectory=output,StartupTimeoutMilliseconds=60000});
if(args.Length==4&&args[3]=="diff-replay")
{
    var previous=Path.GetFullPath(args[0]);
    T Read<T>(string name)=>JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(previous,name+".json")),json)!;
    var bundle=Read<Stage4RepairEvidenceBundle>("repair-bundle");
    var native=Path.Combine(previous,"repair-work.SLDPRT");
    if(DrawingPlanValidation.FileHash(native)!=bundle.Execution.CandidateModelSha256||DrawingPlanValidation.FileHash(Path.Combine(previous,"preserved-baseline.SLDPRT"))!=bundle.Execution.BaselineModelSha256)
        throw new InvalidDataException("Native prefix bytes changed; replay refused.");
    var views=new[]{"Front","Top","Left"};
    var corrected=CompareViews(views.Select(v=>Read<ProjectionCaptureResult>("before-"+v)).ToArray(),views.Select(v=>Read<ProjectionCaptureResult>("after-"+v)).ToArray());
    Save("collateral-diff",corrected);Check("verified native capture prefix has no non-target change under correct per-view frame",corrected.Status==ModelDiffStatus.Comparable&&!corrected.HasUnexpectedImpact);
    bundle=bundle with{Diff=corrected};Save("repair-bundle",bundle);
    var gate=ConstrainedRepairSession.ValidatePostRepair(bundle.Attempt,bundle.Execution,bundle.Coverage,bundle.Diff,bundle.Checks);Save("T14-final",gate);
    Check("verified native repair prefix passes full T14 recomputation",gate.Status==RepairValidationStatus.Passed);
    var replayIntent=Read<EdgeTreatmentIntent>("intent");var replayBinding=Read<EdgeTreatmentInspectionBinding>("binding");Save("intent",replayIntent);Save("binding",replayBinding);
    var replayFinal=await new Stage4SavedModelAcceptanceWorkflow(executor,new ArtifactCacheStore()).VerifyEdgeTreatmentAsync(native,replayIntent,replayBinding,bundle);
    Save("T19-repaired-final",replayFinal);Check("actual reopened repaired native passes corrected full T13/T14/T19",replayFinal.Passed);
    var challenge=CompareViews(views.Select(v=>Read<ProjectionCaptureResult>("after-"+v)).ToArray(),views.Select(v=>Read<ProjectionCaptureResult>("collateral-"+v)).ToArray());
    Save("unexpected-collateral-diff",challenge);Check("correct frame still detects actual unrelated-hole enlargement",challenge.HasUnexpectedImpact);
    Save("replay-provenance",new{previous,native,baseline_sha=bundle.Execution.BaselineModelSha256,candidate_sha=bundle.Execution.CandidateModelSha256,
        method="Reuse byte-verified native build/capture prefix; correct development fixture left-view corridor mapping; rerun actual saved-model final gate"});
    return;
}
if(args.Length==4)
{
    foreach(var orientation in new[]{ProjectionOrientation.Front,ProjectionOrientation.Top,ProjectionOrientation.Left})
    {
        var capture=await executor.CaptureProjectionAsync(new(){NativePath=Path.GetFullPath(args[3]),SourceDrawingSha256=sourceSha,ViewId=orientation.ToString(),CoordinateFrameId=orientation+"-mm",Orientation=orientation});
        Save("capture-"+orientation,capture);Check("complete native capture "+orientation,capture.Success&&capture.Complete);
    }
    return;
}
var plan=new ModelingPlan{PlanId="repair-"+Guid.NewGuid().ToString("N"),Name="repair_work",SourceText="Four vertical corner edges R3 and centered diameter4 through hole",DrawingSourceSha256=sourceSha,
    Output=new(){NativePath=path,OverwriteAllowed=true},Recovery=new(){Enabled=false},
    Operations=[new ProfileSketchOperation{Id="base",Name="base",Primitives=[new CenteredRectangleProfile{WidthMm=40,HeightMm=30}]},
        new ExtrudeBossOperation{Id="block",Name="block",DependsOn=["base"],SketchId="base",DepthMm=10},
        new NativeFeatureOperation{Id="hole",Name="hole",DependsOn=["block"],Options=new(){Kind=NativeFeatureKind.Hole,HoleKind=HoleKind.Simple,DiameterMm=4,ThroughAll=true,DepthMm=10,HoleCenters=[new(0,0)]}},
        new NativeFeatureOperation{Id="corners",Name="corners",DependsOn=["hole"],Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=3,
            Selections=[new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line,PositionMm=new(20,15,5),Direction=new(0,0,1)},new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line,PositionMm=new(20,-15,5),Direction=new(0,0,1)}]}}]};
Save("baseline-plan",plan);var built=await executor.ExecuteAsync(plan,false);Save("baseline-build",built);Check("actual baseline with two missing corner fillets builds",built.Success);if(!built.Success)return;
var baselineSha=DrawingPlanValidation.FileHash(path);File.Copy(path,Path.Combine(output,"preserved-baseline.SLDPRT"));
var baseline=await executor.InspectAsync(new(path));Save("baseline-inspection",baseline);
var primitiveList=new List<ProjectionPrimitive>();
void Outer(ProjectionPrimitive p)=>primitiveList.Add(p with{RequirementId="T09:outer",SourceFactId="outer",SourceFactFingerprint=outerFact,RequirementFingerprint=outerRequirement});
Outer(new(){Id="top",Kind=ProjectionPrimitiveKind.Line,Start=new(-17,15),End=new(17,15)});
Outer(new(){Id="right",Kind=ProjectionPrimitiveKind.Line,Start=new(20,12),End=new(20,-12)});
Outer(new(){Id="bottom",Kind=ProjectionPrimitiveKind.Line,Start=new(17,-15),End=new(-17,-15)});
Outer(new(){Id="left",Kind=ProjectionPrimitiveKind.Line,Start=new(-20,-12),End=new(-20,12)});
foreach(var (name,center,start,end) in new[]{("tr",new ProjectionPointMm(17,12),new ProjectionPointMm(20,12),new ProjectionPointMm(17,15)),
    ("tl",new ProjectionPointMm(-17,12),new ProjectionPointMm(-17,15),new ProjectionPointMm(-20,12)),
    ("bl",new ProjectionPointMm(-17,-12),new ProjectionPointMm(-20,-12),new ProjectionPointMm(-17,-15)),
    ("br",new ProjectionPointMm(17,-12),new ProjectionPointMm(17,-15),new ProjectionPointMm(20,-12))})
    Outer(new(){Id=name,Kind=ProjectionPrimitiveKind.Arc,Center=center,Start=start,End=end,RadiusMm=3,SweepDegrees=90});
primitiveList.Add(new(){Id="center-hole",Kind=ProjectionPrimitiveKind.Circle,Center=new(0,0),RadiusMm=2,RequirementId="T09:hole",SourceFactId="hole",SourceFactFingerprint=holeFact,RequirementFingerprint=holeRequirement});
var source=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="front-mm",SourceSha256=sourceSha,SourceRevisionId=revision,Primitives=primitiveList};Save("source-front",source);
async Task<ProjectionCaptureResult> Capture(string label,ProjectionOrientation orientation,string? input=null)
{
    var capture=await executor.CaptureProjectionAsync(new(){NativePath=input??path,SourceDrawingSha256=sourceSha,ViewId=orientation.ToString().ToLowerInvariant(),CoordinateFrameId=orientation.ToString().ToLowerInvariant()+"-mm",Orientation=orientation});
    Save(label+"-"+orientation,capture);if(!capture.Success||!capture.Complete||capture.Snapshot is null)throw new InvalidOperationException("Incomplete native projection: "+capture.Message);
    return capture;
}
var beforeCapture=await Capture("before",ProjectionOrientation.Front);
var beforeReport=ProjectionVerifier.Compare(source,beforeCapture.Snapshot!,new(){CompareVisibleOnly=false});Save("before-projection",beforeReport);
Check("actual missing fillets fail frozen T09 source",!beforeReport.Passed);
var packet=new ReviewPacket{SourceSha256=sourceSha,SourceRevisionId=revision,CandidateModelSha256=baselineSha,CandidatePlanFingerprint=ModelingPlanIdentity.Fingerprint(plan),
    RequiredFacts=[new(){FactId="outer",SourceRevisionId=revision,SourceFactFingerprint=outerFact,RequirementFingerprint=outerRequirement,RequiredEvidenceKinds=[ReviewEvidenceKind.Projection]},
        new(){FactId="hole",SourceRevisionId=revision,SourceFactFingerprint=holeFact,RequirementFingerprint=holeRequirement,RequiredEvidenceKinds=[ReviewEvidenceKind.Projection]}]};
var beforeCoverage=CoverageReviewer.Review(packet,CoverageReviewer.FromProjection(beforeReport,"before"));Save("before-coverage",beforeCoverage);
Check("actual T12 coverage localizes missing outer requirement",beforeCoverage.Conclusion==CoverageConclusion.Failed&&beforeCoverage.Items.Single(i=>i.FactId=="hole").Status==RequirementCheckStatus.Passed);
var doc=GeometryDocumentIdentity.FromSavedPath(path,baselineSha);
var refs=new[]{new Vector3(20,15,5),new Vector3(20,-15,5),new Vector3(-20,15,5),new Vector3(-20,-15,5)}.Select((p,i)=>new GeometryRef{RefId="corner-"+i,
    DocumentId=doc.DocumentId,DocumentPath=path,ModelSha256=baselineSha,SourceRevisionId=revision,SourceFactIds=["outer"],OperationId="corners",InputToFeature="corners",
    EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,Signature=new(){EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,AnchorMm=p,Direction=new(0,0,1)}}).ToArray();
var resolved=await executor.InspectAsync(new(path,GeometryReferences:refs,SourceRevisionId:revision));Save("repair-targets",resolved);
Check("all four frozen corner targets resolve in actual feature input",resolved.GeometryRefResolutions.Count==4&&resolved.GeometryRefResolutions.All(r=>r.Status==GeometryRefResolutionStatus.Resolved));
if(resolved.GeometryRefResolutions.Any(r=>r.Status!=GeometryRefResolutionStatus.Resolved))return;
var location=FailureLocator.Locate(plan,new(){FailureId="missing-corners",FailureClass=FailureClass.ProjectionSectionError,SourceFactIds=["outer"],OperationIds=["corners"],GeometryReferences=refs,EvidenceIds=["before-projection","before-coverage"]});Save("location",location);
var session=new ConstrainedRepairSession();
var request=new RepairRequest{RequestId="repair-corner-selection",FailureId=location.FailureId,Kind=RepairKind.FilletSelection,SourceRevisionId=revision,
    FailureFingerprint=Sha(JsonSerializer.Serialize(beforeReport,json)),NewEvidenceFingerprint=Sha(JsonSerializer.Serialize(resolved.GeometryRefResolutions,json)),TargetOperationId="corners",GeometryResolutions=resolved.GeometryRefResolutions};
var attempt=session.Prepare(plan,request,beforeCoverage,location);Save("attempt",attempt);
Check("T14 prepares only actual proven edge reselection with R3 locked",attempt.Status==RepairAttemptStatus.Prepared&&((NativeFeatureOperation)attempt.CandidatePlan!.Operations.Last()).Options.RadiusMm==3);
if(attempt.CandidatePlan is null)return;
var executed=await executor.ExecuteAsync(attempt.CandidatePlan,false);Save("repair-execution",executed);Check("prepared repair executes in real SolidWorks",executed.Success);if(!executed.Success)return;
var candidate=await executor.InspectAsync(new(path));Save("candidate-inspection",candidate);
var afterCapture=await Capture("after",ProjectionOrientation.Front);
var afterReport=ProjectionVerifier.Compare(source,afterCapture.Snapshot!,new(){CompareVisibleOnly=false});Save("after-projection",afterReport);
Check("repaired native geometry passes the unchanged complete T09 source",afterReport.Passed);
var afterCoverage=CoverageReviewer.Review(packet with{CandidateModelSha256=candidate.ModelSha256!,CandidatePlanFingerprint=attempt.AfterPlanFingerprint},CoverageReviewer.FromProjection(afterReport,"after"));Save("after-coverage",afterCoverage);
Check("T12 rechecks all frozen facts including unaffected hole",afterCoverage.FullPass);
Check("baseline native bytes preserved before work-file replacement",DrawingPlanValidation.FileHash(Path.Combine(output,"preserved-baseline.SLDPRT"))==baselineSha);
var beforeViews=new List<ProjectionCaptureResult>{beforeCapture};var afterViews=new List<ProjectionCaptureResult>{afterCapture};
foreach(var view in new[]{ProjectionOrientation.Top,ProjectionOrientation.Left})
{
    beforeViews.Add(await Capture("before",view,Path.Combine(output,"preserved-baseline.SLDPRT")));
    afterViews.Add(await Capture("after",view));
}
var diff=CompareViews(beforeViews,afterViews);Save("collateral-diff",diff);
Check("T13 complete three-view native capture finds only intended outer-profile changes",diff.Status==ModelDiffStatus.Comparable&&diff.Changes.Count>0&&!diff.HasUnexpectedImpact);
var discovery=await executor.InspectAsync(new(path,Queries:[new(){Kind=EntityKind.Face,Geometry=GeometryKind.Cylinder,RadiusMm=2,PositionMm=new(2,0,5)}]));
if(discovery.Entities?.Count!=1)throw new InvalidOperationException("Unchanged hole has no unique actual cylindrical wall.");
var holeDoc=GeometryDocumentIdentity.FromSavedPath(path,candidate.ModelSha256!);
var holeRef=new GeometryRef{RefId="unaffected-hole",DocumentId=holeDoc.DocumentId,DocumentPath=path,ModelSha256=candidate.ModelSha256!,SourceRevisionId=revision,SourceFactIds=["hole"],
    EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,NativePersistentReference=discovery.Entities[0].PersistentReference,
    Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,AnchorMm=new(0,0,5),Direction=new(0,0,1),RadiusMm=2}};
var topology=await executor.InspectAsync(new(path,SourceRevisionId:revision,ConnectivityQueries:[new(){QueryId="unaffected-hole",GeometryRefs=[holeRef],
    Requirement=new(){RequirementId="T08:hole",SourceFactId="hole",SourceSha256=sourceSha,SourceRevisionId=revision,SourceFactFingerprint=holeFact,Kind=ConnectivityKind.ThroughHole}}]));Save("unaffected-topology",topology);
var holePassed=topology.ConnectivityChecks.Count==1&&topology.ConnectivityChecks[0].Status==ConnectivityStatus.Passed;
Check("unaffected central hole independently passes actual T08 after repair",holePassed);
Check("native execution producer binds the exact prepared plan fingerprint",executed.PlanFingerprint==attempt.AfterPlanFingerprint);
var receipt=new RepairExecutionEvidence{ExecutionId="native-result-sha256:"+Sha(JsonSerializer.Serialize(executed,json)),AttemptRequestId=attempt.RequestId,AttemptIndex=attempt.AttemptIndex,
    BeforePlanFingerprint=attempt.BeforePlanFingerprint,AfterPlanFingerprint=attempt.AfterPlanFingerprint!,SourceSha256=sourceSha,SourceRevisionId=revision,
    BaselineModelSha256=baselineSha,CandidateModelSha256=candidate.ModelSha256!,CandidateModelReopened=candidate.ModelReopened,FrozenRequiredScopeFingerprint=attempt.FrozenRequiredScopeFingerprint,
    FrozenRequiredFactIds=attempt.FrozenRequiredFactIds,FrozenAffectedFactIds=attempt.FrozenAffectedFactIds,EvidenceMode="native_executor"};
var postChecks=new[]{new RepairCheckResult("unaffected-requirement-sample",holePassed?RequirementCheckStatus.Passed:RequirementCheckStatus.Failed,
    "unaffected-topology.json",sourceSha,revision,candidate.ModelSha256!,attempt.AfterPlanFingerprint!,attempt.RequestId)};
var repair=new Stage4RepairEvidenceBundle{Attempt=attempt,Execution=receipt,Coverage=afterCoverage,Diff=diff,Checks=postChecks};Save("repair-bundle",repair);
var validated=ConstrainedRepairSession.ValidatePostRepair(attempt,receipt,afterCoverage,diff,postChecks);Save("T14-final",validated);
Check("T14 actual executed repair passes complete frozen post-check bundle",validated.Status==RepairValidationStatus.Passed);
var intent=new EdgeTreatmentIntent{IntentId="native-repaired-corners",SourceLiteral="four vertical edges R3",SourceSha256=sourceSha,SourceRevisionId=revision,TargetOperationId="corners",
    Route=EdgeTreatmentRoute.SolidFillet,RadiusMm=3,TargetEdges=refs,AllowedRepairStrategies=[EdgeTreatmentRepairStrategy.ReselectEdges],
    EditabilityProbes=[new("radius","D1@corners",3.1,FeatureName:"corners")]};
var binding=new EdgeTreatmentInspectionBinding{FeatureName="corners",PrimaryDimensionName="D1@corners"};
var workflow=new Stage4SavedModelAcceptanceWorkflow(executor,new ArtifactCacheStore());
var final=await workflow.VerifyEdgeTreatmentAsync(path,intent,binding,repair);Save("T19-repaired-final",final);Save("intent",intent);Save("binding",binding);
Check("T19 real repaired candidate recomputes T14 and T13 before passing",final.Passed);
var synthetic=await workflow.VerifyEdgeTreatmentAsync(path,intent,binding,repair with{Execution=receipt with{EvidenceMode="synthetic_fixture"}});Save("synthetic-receipt-rejected",synthetic);
Check("actual native candidate cannot promote a synthetic repair receipt",!synthetic.Passed);
var incomplete=await workflow.VerifyEdgeTreatmentAsync(path,intent,binding,repair with{Diff=diff with{BaselineCaptureComplete=false}});
Save("incomplete-diff-rejected",incomplete);Check("actual repaired candidate rejects incomplete T13 capture",!incomplete.Passed);
var stale=await workflow.VerifyEdgeTreatmentAsync(path,intent,binding,repair with{Execution=receipt with{AttemptRequestId="other-attempt"}});
Save("stale-receipt-rejected",stale);Check("actual repaired candidate rejects another attempt receipt",!stale.Passed);
Check("repeated native final gates preserve candidate bytes",DrawingPlanValidation.FileHash(path)==candidate.ModelSha256);
var collateralPath=Path.Combine(output,"collateral-hole-change.SLDPRT");
var collateralPlan=attempt.CandidatePlan with{PlanId="collateral-"+Guid.NewGuid().ToString("N"),Name="collateral_hole_change",Output=new(){NativePath=collateralPath},
    Operations=attempt.CandidatePlan.Operations.Select(o=>o.Id=="hole"?((NativeFeatureOperation)o) with{Options=((NativeFeatureOperation)o).Options with{DiameterMm=6}}:o).ToArray()};
var collateralBuild=await executor.ExecuteAsync(collateralPlan,false);Save("collateral-build",collateralBuild);
Check("actual unrelated-hole enlargement challenge builds separately",collateralBuild.Success);
if(collateralBuild.Success)
{
    var collateralViews=new List<ProjectionCaptureResult>();
    foreach(var view in new[]{ProjectionOrientation.Front,ProjectionOrientation.Top,ProjectionOrientation.Left})collateralViews.Add(await Capture("collateral",view,collateralPath));
    var collateralDiff=CompareViews(afterViews,collateralViews);Save("unexpected-collateral-diff",collateralDiff);
    Check("T13 detects actual unrelated hole enlargement outside frozen repair scope",collateralDiff.Status==ModelDiffStatus.Comparable&&collateralDiff.HasUnexpectedImpact&&collateralDiff.UnexpectedChanges.Count>0);
    var rejectedCollateral=ConstrainedRepairSession.ValidatePostRepair(attempt,receipt,afterCoverage,collateralDiff,postChecks);Save("collateral-T14-rejected",rejectedCollateral);
    Check("T14 rejects real collateral change even with an otherwise valid repair bundle",rejectedCollateral.Status!=RepairValidationStatus.Passed);
}
Save("closure-state",new{native_execution=executed.Success,source_projection=afterReport.Passed,t13_collateral_diff=diff.Status.ToString(),t14_final_gate=validated.Status.ToString(),t19_final_gate=final.Passed,
    scope="bounded prismatic plate, outer vertical fillet reselection, complete front/top/left projected geometry and unaffected native through-hole; not arbitrary B-Rep equivalence"});

ModelDiff CompareViews(IReadOnlyList<ProjectionCaptureResult> before,IReadOnlyList<ProjectionCaptureResult> after)
{
    var diffs=new List<ModelDiff>();
    foreach(var b in before)
    {
        var a=after.Single(x=>x.Snapshot!.ViewId==b.Snapshot!.ViewId);
        var bg=Records(b.Snapshot!);var ag=Records(a.Snapshot!);
        diffs.Add(ModelDiffAnalyzer.Compare(new(){BaselineModelSha256=b.Snapshot!.NativeModelSha256!,CandidateModelSha256=a.Snapshot!.NativeModelSha256!,
            BaselineModelReopened=b.Snapshot.NativeModelReopened,CandidateModelReopened=a.Snapshot.NativeModelReopened,
            BaselineCaptureComplete=b.Success&&b.Complete,CandidateCaptureComplete=a.Success&&a.Complete,
            BaselineCaptureScopeIds=[b.Snapshot.ViewId],CandidateCaptureScopeIds=[a.Snapshot.ViewId],BaselineGeometry=bg,CandidateGeometry=ag,
            IntendedTargetIds=bg.Concat(ag).Where(g=>g.OperationId=="corners").Select(g=>g.SemanticId!).Distinct().ToArray()}));
    }
    return new(){BaselineModelSha256=before[0].Snapshot!.NativeModelSha256!,CandidateModelSha256=after[0].Snapshot!.NativeModelSha256!,
        BaselineModelReopened=before.All(c=>c.Snapshot!.NativeModelReopened),CandidateModelReopened=after.All(c=>c.Snapshot!.NativeModelReopened),
        BaselineCaptureComplete=before.Count==3&&before.All(c=>c.Success&&c.Complete),CandidateCaptureComplete=after.Count==3&&after.All(c=>c.Success&&c.Complete),
        CaptureScopeIds=before.Select(c=>"full-projection:"+c.Snapshot!.ViewId).ToArray(),Status=diffs.All(d=>d.Status==ModelDiffStatus.Comparable)?ModelDiffStatus.Comparable:ModelDiffStatus.Incomparable,
        Changes=diffs.SelectMany(d=>d.Changes).ToArray(),UnexpectedChanges=diffs.SelectMany(d=>d.UnexpectedChanges).ToArray(),IncomparableReason=string.Join(";",diffs.Select(d=>d.IncomparableReason).Where(s=>s.Length>0))};
}
IReadOnlyList<GeometryShapeRecord> Records(ProjectionSnapshot snapshot)
{
    // Frozen fixture scope: exterior silhouette may change; the central 10 mm
    // projected corridor belongs to the unaffected drill, not the fillet operation.
    // Each view is compared separately; all supported primitives contribute records.
    var records=new Dictionary<string,GeometryShapeRecord>();
    void Add(GeometrySignature signature,bool hole)
    {
        var item=new GeometryShapeRecord{RecordId="pending",Signature=signature};var key=snapshot.ViewId+":"+ModelDiffAnalyzer.ShapeFingerprint(item);
        records[key]=item with{RecordId=key,SemanticId=key,OperationId=hole?"hole":"corners",SourceFactIds=[hole?"hole":"outer"]};
    }
    Vector3 P(ProjectionPointMm p)=>new(Math.Round(p.X,6),Math.Round(p.Y,6),0);
    foreach(var p in snapshot.Primitives)
    {
        var hole=p.Kind==ProjectionPrimitiveKind.Circle?Math.Abs(p.Center.X)<5&&Math.Abs(p.Center.Y)<5&&p.RadiusMm<5:
            snapshot.ViewId=="left"?Math.Abs(p.Start.Y)<5&&Math.Abs(p.End.Y)<5:Math.Abs(p.Start.X)<5&&Math.Abs(p.End.X)<5;
        if(p.Kind is ProjectionPrimitiveKind.Circle or ProjectionPrimitiveKind.Arc)
            Add(new(){EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Circle,AnchorMm=P(p.Center),RadiusMm=Math.Round(p.RadiusMm,6),Direction=new(0,0,1)},hole);
        if(p.Kind==ProjectionPrimitiveKind.Circle)continue;
        Add(new(){EntityKind=EntityKind.Edge,AnchorMm=P(p.Start)},hole);Add(new(){EntityKind=EntityKind.Edge,AnchorMm=P(p.End)},hole);
        if(p.Kind==ProjectionPrimitiveKind.Line)
            Add(new(){EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,AnchorMm=P(new((p.Start.X+p.End.X)/2,(p.Start.Y+p.End.Y)/2)),
                Direction=ModelVerification.Unit(ModelVerification.Sub(P(p.End),P(p.Start)))},hole);
        else
        {
            var angle=Math.Atan2(p.Start.Y-p.Center.Y,p.Start.X-p.Center.X)+p.SweepDegrees*Math.PI/360;
            Add(new(){EntityKind=EntityKind.Edge,AnchorMm=P(new(p.Center.X+p.RadiusMm*Math.Cos(angle),p.Center.Y+p.RadiusMm*Math.Sin(angle)))},hole);
        }
    }
    return records.Values.ToArray();
}
